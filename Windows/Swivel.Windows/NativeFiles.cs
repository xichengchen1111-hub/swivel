using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Swivel.Core;

namespace Swivel.Windows;

/// Fail-closed local-file I/O. Parent handles forbid rename/delete throughout an operation.
/// No path is handed to an image codec, shell command, URL loader, or network client.
public static partial class NativeFiles
{
    private const uint Traverse = 0x20, ReadAttributes = 0x80, GenericRead = 0x80000000, GenericWrite = 0x40000000, DeleteAccess = 0x10000;
    private const uint OpenReparse = 0x00200000, BackupSemantics = 0x02000000;
    private const uint UnsafeAttributes = 0x400 | 0x1000 | 0x40000 | 0x400000; // reparse, offline, recall-on-open/data-access
    internal const string LocalOnly = "Choose a local regular file. Links, network drives and cloud placeholders are not supported yet.";

    public sealed class Input : IDisposable
    {
        internal readonly DirectoryLease Parent;
        public FileStream Stream { get; }
        public string Path { get; }
        internal Input(string path, DirectoryLease parent, FileStream stream) { Path = path; Parent = parent; Stream = stream; }
        /// The held file's identity, size and times; the handle denies writers, so it stays true.
        public FileStamp Stamp => ReadStamp(Stream.SafeFileHandle);
        public void Dispose() { Stream.Dispose(); Parent.Dispose(); }
    }

    public static Input OpenRead(string path)
    {
        WindowsPathPolicy.Validate(path);
        var parent = DirectoryLease.Open(System.IO.Path.GetDirectoryName(path)!);
        SafeFileHandle? handle = null;
        try
        {
            // Attribute-only open refuses cloud files before a data-read handle can hydrate them.
            using var probe = Open(path, ReadAttributes, 1, 3, OpenReparse);
            Check(probe, false);
            handle = Open(path, GenericRead | ReadAttributes, 1, 3, OpenReparse);
            Check(handle, false);
            var stream = new FileStream(handle, FileAccess.Read);
            handle = null;
            return new Input(path, parent, stream);
        }
        catch { handle?.Dispose(); parent.Dispose(); throw; }
    }

    public static void ValidateInput(string path)
    {
        WindowsPathPolicy.Validate(path);
        using var parent = DirectoryLease.Open(System.IO.Path.GetDirectoryName(path)!);
        using var probe = Open(path, ReadAttributes, 1, 3, OpenReparse);
        Check(probe, false);
    }

    public static string WriteBeside(Input input, string stem, string extension, Action<Stream> produce, CancellationToken cancellation)
        => WriteInHeldDirectory(System.IO.Path.GetDirectoryName(input.Path)!, stem, extension, produce, cancellation);

    // New content (such as typed QR codes) has no source file. The user selects the
    // output folder explicitly; lease every component using the same no-link policy.
    public static string WriteInDirectory(string directory, string stem, string extension, Action<Stream> produce, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        WindowsPathPolicy.Validate(System.IO.Path.Combine(directory, WindowsPathPolicy.OutputName(stem, extension, 1)));
        using var lease = DirectoryLease.Open(directory);
        return WriteInHeldDirectory(directory, stem, extension, produce, cancellation);
    }

    private static string WriteInHeldDirectory(string directory, string stem, string extension, Action<Stream> produce, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        string temp = System.IO.Path.Combine(directory, ".swivel-" + Guid.NewGuid().ToString("N") + ".tmp");
        // A private staging file is claimed atomically. DELETE permission allows cleanup/rename by
        // the very same handle. No FILE_SHARE_WRITE/DELETE means nobody can replace it mid-write.
        // The output folder's own handle: names are published relative to it, never by a path
        // that an empty folder turned into a mount point could redirect.
        using var parent = Open(directory, ReadAttributes | Traverse, 3, 3, OpenReparse | BackupSemantics);
        Check(parent, true);
        using var handle = Open(temp, GenericWrite | ReadAttributes | DeleteAccess, 0, 1, OpenReparse | 2 | 0x100);
        FileStream? stream = null;
        bool published = false;
        try
        {
            Check(handle, false);
            CheckCreatedIn(handle, parent, temp);
            stream = new FileStream(handle, FileAccess.Write, 1, false); // unbuffered: nothing left to flush after a failed produce
            produce(stream);
            stream.Flush(true);
            cancellation.ThrowIfCancellationRequested();
            var basic = new BasicInfo { Attributes = 0x80 };
            if (!SetBasic(handle, 0, ref basic, (uint)Marshal.SizeOf<BasicInfo>())) ThrowLast();
            for (int i = 1; i <= 10000; i++)
            {
                string destination = System.IO.Path.Combine(directory, WindowsPathPolicy.OutputName(stem, extension, i));
                WindowsPathPolicy.Validate(destination);
                cancellation.ThrowIfCancellationRequested();
                if (RenameInto(handle, parent, System.IO.Path.GetFileName(destination), out int error))
                {
                    published = true;
                    return destination;
                }
                if (error is not 80 and not 183) throw new Win32Exception(error);
            }
            throw new UserError("Too many files have this output name.");
        }
        finally
        {
            try
            {
                if (!published)
                {
                    byte delete = 1;
                    // Never delete by path: a cleanup targets only the object this call created.
                    // An exception is already propagating; a failed cleanup must not replace it.
                    SetDisposition(handle, 4, ref delete, 1);
                }
            }
            finally { stream?.Dispose(); } // closes the handle, so only after the disposition
        }
    }

    public sealed class RenameLease : IRenameLease, IDisposable
    {
        private readonly SafeFileHandle handle;
        private readonly DirectoryLease parent;
        // exFAT/FAT32: the held-handle token that stands for the file's identity (see NativeFiles.Fat.cs).
        private readonly ulong? fatToken;
        public string CurrentPath { get; private set; }
        public FileStamp Stamp { get; }
        internal bool IsFat => fatToken is not null;
        /// The held file's name as stored now (long name, exact case).
        public string StoredName => Leaf(FinalPath(handle));
        internal RenameLease(string path, DirectoryLease parent, SafeFileHandle handle, ulong? fatToken = null)
        {
            CurrentPath = path; this.parent = parent; this.handle = handle; this.fatToken = fatToken;
            Stamp = Now();
        }
        // On FAT the file index moves with the directory entry; the held handle is the identity.
        private FileStamp Now() => fatToken is ulong token ? ReadStamp(handle) with { Index = token } : ReadStamp(handle);
        public void MoveTo(string destination)
        {
            WindowsPathPolicy.Validate(destination);
            if (System.IO.Path.GetDirectoryName(destination) != System.IO.Path.GetDirectoryName(CurrentPath))
                throw new UserError("Batch Rename keeps each file in its original folder.");
            if (!Now().SameObjectAndContent(Stamp)) throw new UserError("A file changed. Create a new preview before renaming.");
            if (!Rename(handle, destination, out int error)) throw new Win32Exception(error);
            // Set immediately after success; a later failure must never lose its location.
            CurrentPath = destination;
            if (fatToken is null) return;
            // FAT reports success for a case-only rename without changing the stored name: the
            // name actually stored decides where the file is.
            CurrentPath = FatCurrentPath(handle);
            if (!string.Equals(CurrentPath, destination, StringComparison.Ordinal)) throw new UserError(FatRules.NameNotTaken);
        }
        /// Keeps a reference for Undo on exFAT/FAT32 (nothing on NTFS).
        internal void HoldForUndo() { if (fatToken is ulong token) HoldFat(token, handle, false); }
        public void EnsureDestinationFree(string destination)
        {
            WindowsPathPolicy.Validate(destination);
            if (System.IO.Path.GetDirectoryName(destination) != System.IO.Path.GetDirectoryName(CurrentPath))
                throw new UserError("Batch Rename keeps each file in its original folder.");
            uint attributes = GetAttributes(destination);
            if (attributes != uint.MaxValue) throw new UserError("A destination name is already occupied. Nothing was overwritten.");
            int error = Marshal.GetLastWin32Error();
            if (error != 2) throw new Win32Exception(error); // Missing leaf only; access/errors are not absence.
        }
        public void Dispose() { handle.Dispose(); parent.Dispose(); }
    }
    /// Whether anything (a file, folder or link) has this name; only "not found" counts as free.
    public static bool NameTaken(string path)
    {
        WindowsPathPolicy.Validate(path);
        if (GetAttributes(path) != uint.MaxValue) return true;
        int error = Marshal.GetLastWin32Error();
        if (error == 2) return false;
        throw new Win32Exception(error);
    }
    /// With `allowFat`, files on exFAT and FAT32 drives too; there `fatToken` (from an undo
    /// receipt) reopens the file Swivel kept a reference to instead of opening the path.
    public static RenameLease OpenRename(string path, bool allowFat = false, ulong? fatToken = null)
    {
        WindowsPathPolicy.Validate(path);
        var parent = DirectoryLease.Open(System.IO.Path.GetDirectoryName(path)!);
        SafeFileHandle? handle = null;
        try
        {
            var format = Format(path);
            if (allowFat && FatRules.IsFat(format))
            {
                // No content read, no reparse following, no shared writes or deletes.
                handle = fatToken is ulong token ? ReopenFat(token, path, DeleteAccess | ReadAttributes, 1, false) : Open(path, DeleteAccess | ReadAttributes, 1, 3, OpenReparse);
                Check(handle, false);
                if (fatToken is null) RequireExactFatName(handle, path);
                if (!FatRules.IsFat(Format(handle))) throw new UserError(LocalOnly);
                var fat = new RenameLease(path, parent, handle, fatToken ?? NewFatToken()); handle = null; return fat;
            }
            if (format != VolumeFormat.Ntfs)
                throw new UserError(allowFat ? "Batch Rename works on NTFS, exFAT and FAT32 drives only." : "Batch Rename currently requires NTFS. Other file systems need separate identity and undo validation.");
            // Single attribute/delete handle: a preceding READ-only probe would block DELETE.
            // No content read, no reparse following, no shared writes or deletes.
            handle = Open(path, DeleteAccess | ReadAttributes, 1, 3, OpenReparse);
            Check(handle, false);
            if (!GetFileInformationByHandle(handle, out var info)) ThrowLast();
            // Renaming changes only this name: a read-only file stays read-only and unchanged, and
            // a hard link's other names are left as they are (as Explorer renames them).
            if (info.IndexHigh == 0 && info.IndexLow == 0)
                throw new UserError("Batch Rename needs the file identity NTFS reports; this file has none.");
            var result = new RenameLease(path, parent, handle); handle = null; return result;
        }
        catch { handle?.Dispose(); parent.Dispose(); throw; }
    }
    private static FileStamp ReadStamp(SafeFileHandle handle)
    {
        Check(handle, false);
        if (!GetFileInformationByHandle(handle, out var info)) ThrowLast();
        static long Time(System.Runtime.InteropServices.ComTypes.FILETIME value) => ((long)value.dwHighDateTime << 32) | (uint)value.dwLowDateTime;
        return new(info.Volume, ((ulong)info.IndexHigh << 32) | info.IndexLow, ((ulong)info.SizeHigh << 32) | info.SizeLow, Time(info.Write), Time(info.Creation));
    }

    // Renames a held file to one leaf name inside a held directory handle, never resolving a
    // path. ntdll is called directly: kernelbase rewrites FileName to a full path even when
    // RootDirectory is set (ERROR_INVALID_PARAMETER). No replacement.
    private static bool RenameInto(SafeFileHandle file, SafeFileHandle directory, string leaf, out int error)
    {
        WindowsPathPolicy.ValidateName(leaf);
        byte[] name = Encoding.Unicode.GetBytes(leaf);
        int lengthOffset = IntPtr.Size * 2, nameOffset = lengthOffset + 4;
        int size = Math.Max(nameOffset + name.Length, IntPtr.Size == 8 ? 24 : 16); // never below sizeof(FILE_RENAME_INFORMATION), even for a one-character name
        IntPtr buffer = Marshal.AllocHGlobal(size); bool retained = false;
        try
        {
            directory.DangerousAddRef(ref retained);
            Marshal.Copy(new byte[size], 0, buffer, size); // FILE_RENAME_INFORMATION.ReplaceIfExists = FALSE
            Marshal.WriteIntPtr(buffer, IntPtr.Size, directory.DangerousGetHandle());
            Marshal.WriteInt32(buffer, lengthOffset, name.Length);
            Marshal.Copy(name, 0, buffer + nameOffset, name.Length);
            int status = NtSetInformationFile(file, out _, buffer, (uint)size, 10);
            error = status >= 0 ? 0 : (int)RtlNtStatusToDosError(status);
            return status >= 0;
        }
        finally { if (retained) directory.DangerousRelease(); Marshal.FreeHGlobal(buffer); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoStatusBlock { public IntPtr Status; public IntPtr Information; }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtSetInformationFile(SafeFileHandle file, out IoStatusBlock status, IntPtr information, uint length, int informationClass);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern uint RtlNtStatusToDosError(int status);
    private static bool Rename(SafeFileHandle handle, string destination, out int error)
    {
        byte[] name = Encoding.Unicode.GetBytes(destination);
        int lengthOffset = IntPtr.Size * 2, nameOffset = lengthOffset + 4;
        int size = Math.Max(nameOffset + name.Length + 2, IntPtr.Size == 8 ? 24 : 16);
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, buffer, size);
            // FILE_RENAME_INFO.ReplaceIfExists = FALSE; RootDirectory = NULL.
            Marshal.WriteInt32(buffer, lengthOffset, name.Length);
            Marshal.Copy(name, 0, buffer + nameOffset, name.Length);
            bool ok = SetInfo(handle, 3, buffer, (uint)size);
            error = ok ? 0 : Marshal.GetLastWin32Error();
            return ok;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal sealed class DirectoryLease : IDisposable
    {
        private readonly List<SafeFileHandle> handles = new();
        private uint share = 3;
        /// `shareDelete` lets the person rename, move or delete the folder and its ancestors while it is
        /// held (for an always-on watcher); reads are then checked again at the reported path.
        public static DirectoryLease Open(string directory, bool shareDelete = false)
        {
            string root = directory[..3];
            // Mapped SMB drives are rejected too, before opening a path on them.
            if (GetDriveType(root) is not 2 and not 3 and not 6) throw new UserError(LocalOnly);
            var lease = new DirectoryLease { share = shareDelete ? 7u : 3u };
            try
            {
                lease.Add(root);
                string current = root;
                foreach (string part in directory[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
                {
                    current = System.IO.Path.Combine(current, part);
                    lease.Add(current);
                }
                return lease;
            }
            catch { lease.Dispose(); throw; }
        }
        private void Add(string path)
        {
            // Traverse access counts as read in share checks (attribute-only opens do not), so
            // without delete sharing the folder cannot be renamed or removed while held. It needs
            // no list permission. Write sharing stays on: publishing a child opens its parent
            // for write data.
            var handle = NativeFiles.Open(path, ReadAttributes | Traverse, share, 3, OpenReparse | BackupSemantics);
            handles.Add(handle);
            Check(handle, true);
        }
        public void Dispose() { for (int i = handles.Count - 1; i >= 0; i--) handles[i].Dispose(); handles.Clear(); }
        /// The held folder is still at `directory` (compared by its final path, case-insensitively).
        internal bool StillAt(string directory)
        {
            if (handles.Count == 0) return false;
            try
            {
                string final = FinalPath(handles[^1]);
                if (final.StartsWith(@"\\?\", StringComparison.Ordinal)) final = final[4..];
                return string.Equals(final.TrimEnd('\\'), directory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) && Directory.Exists(directory);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or UserError or System.ComponentModel.Win32Exception) { return false; }
        }
    }

    // Read-only accounting enumerates held directories, never child file data.
    internal sealed partial class MetadataDirectory : IFolderMetadata
    {
        private readonly SafeFileHandle handle;
        private readonly DirectoryLease? ancestors;
        private readonly string path;
        private readonly uint volume;
        public string Name => System.IO.Path.GetFileName(path) is { Length: > 0 } name ? name : path;
        private MetadataDirectory(string path, SafeFileHandle handle, uint volume, DirectoryLease? ancestors)
        { this.path = path; this.handle = handle; this.volume = volume; this.ancestors = ancestors; }
        public static MetadataDirectory Open(string directory)
        {
            ValidateDirectoryPath(directory);
            var lease = DirectoryLease.Open(directory);
            try { return OpenHeld(directory, null, lease); }
            catch { lease.Dispose(); throw; }
        }
        private static void ValidateDirectoryPath(string directory)
        {
            if (directory.Length == 3 && directory[1] == ':' && directory[2] == '\\') WindowsPathPolicy.Validate(directory + "_");
            else WindowsPathPolicy.Validate(directory);
        }
        private static MetadataDirectory OpenHeld(string path, uint? expectedVolume, DirectoryLease? ancestors)
        {
            var handle = NativeFiles.Open(path, ReadAttributes | 1, 1, 3, OpenReparse | BackupSemantics);
            try
            {
                Check(handle, true);
                if (!GetFileInformationByHandle(handle, out var info)) ThrowLast();
                if ((info.Attributes & FolderSizes.SkippedAttributes) != 0 || (expectedVolume is uint wanted && info.Volume != wanted))
                    throw new UserError("Choose a local folder without links, system attributes or cloud placeholders.");
                return new(path, handle, info.Volume, ancestors);
            }
            catch { handle.Dispose(); throw; }
        }
        public IFolderMetadata OpenChild(string name)
        {
            WindowsPathPolicy.ValidateName(name); string child = System.IO.Path.Combine(path, name);
            ValidateDirectoryPath(child); return OpenHeld(child, volume, null);
        }
        public IEnumerable<FolderEntry> Entries(CancellationToken token)
        {
            var buffer = new byte[64 * 1024]; int kind = 15; // Restart, then continuation.
            for (;;)
            {
                token.ThrowIfCancellationRequested(); Array.Clear(buffer);
                if (!GetDirectoryInfo(handle, kind, buffer, (uint)buffer.Length))
                {
                    int error = Marshal.GetLastWin32Error(); token.ThrowIfCancellationRequested();
                    if (error == 18) yield break; // ERROR_NO_MORE_FILES only.
                    throw new Win32Exception(error);
                }
                token.ThrowIfCancellationRequested(); kind = 14;
                foreach (var entry in FolderDirectoryBuffer.Parse(buffer)) { token.ThrowIfCancellationRequested(); yield return entry; }
            }
        }
        public void Dispose() { handle.Dispose(); ancestors?.Dispose(); }
    }

    private static SafeFileHandle Open(string path, uint access, uint share, uint creation, uint flags)
    {
        var handle = CreateFile(path, access, share, IntPtr.Zero, creation, flags, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        int error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(error);
    }
    private static void Check(SafeFileHandle handle, bool directory)
    {
        if (GetFileType(handle) != 1) throw new UserError(LocalOnly);
        if (!GetFileInformationByHandle(handle, out var info)) ThrowLast();
        if ((info.Attributes & UnsafeAttributes) != 0 || ((info.Attributes & 0x10) != 0) != directory)
            throw new UserError(LocalOnly);
    }
    /// The staging file was created by path; an empty held folder could have become a mount
    /// point in between. Its final path must be the held folder's own path plus the staging name.
    internal static void CheckCreatedIn(SafeFileHandle file, SafeFileHandle folder, string temp)
    {
        string expected = System.IO.Path.Combine(FinalPath(folder), System.IO.Path.GetFileName(temp));
        // Both names come from the file system itself; exact comparison also holds in case-sensitive folders.
        if (!string.Equals(FinalPath(file), expected, StringComparison.Ordinal))
            throw new UserError("The output folder changed while writing. Nothing was written.");
    }
    private static string FinalPath(SafeFileHandle handle)
    {
        var buffer = new char[32768];
        uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0) ThrowLast();
        if (length >= buffer.Length) throw new UserError(LocalOnly);
        return new string(buffer, 0, (int)length);
    }
    private static void ThrowLast() => throw new Win32Exception(Marshal.GetLastWin32Error());

    [StructLayout(LayoutKind.Sequential)] private struct FileInfo
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BasicInfo
    {
        public long Creation, Access, Write, Change;
        public uint Attributes;
    }
    [DllImport("kernel32.dll", EntryPoint = "GetFileAttributesW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetAttributes(string path);
    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformation(string root, StringBuilder? volumeName, int volumeNameSize, out uint serial, out uint maxComponent, out uint flags, StringBuilder fileSystem, int fileSystemSize);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, [Out] char[] path, uint length, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfo info);
    [DllImport("kernel32.dll")] private static extern uint GetFileType(SafeFileHandle handle);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    private static extern bool GetDirectoryInfo(SafeFileHandle handle, int kind, [Out] byte[] buffer, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetDriveTypeW", CharSet = CharSet.Unicode)] private static extern uint GetDriveType(string root);
    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    private static extern bool SetInfo(SafeFileHandle handle, int kind, IntPtr info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    private static extern bool SetBasic(SafeFileHandle handle, int kind, ref BasicInfo info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    private static extern bool SetDisposition(SafeFileHandle handle, int kind, ref byte info, uint size);
}
