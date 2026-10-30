using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Swivel.Core;

namespace Swivel.Windows;

/// Explorer folder icons: a desktop.ini and an icon file written into the folder itself.
/// Every folder from the drive root down is held. Only files Swivel wrote are replaced or
/// removed, each through a handle opened inside the held folder; nothing is overwritten.
public static partial class NativeFiles
{
    private const uint WriteAttributes = 0x100, HiddenSystem = 0x2 | 0x4;
    private const string FolderChanged = "Something else changed this folder. Nothing was overwritten.";

    public static FolderStyleState InspectFolderStyle(string folder)
    {
        using var held = HoldStyledFolder(folder);
        using var ini = OpenIni(held.Handle, folder, false);
        return FolderStyles.Classify(ini?.Bytes);
    }

    public static void ApplyFolderStyle(string folder, byte[] icon, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var held = HoldStyledFolder(folder);
        using var old = OpenIni(held.Handle, folder, true);
        SwivelFolderIni? previous = null;
        if (old is not null && (previous = FolderStyles.ParseOurs(old.Bytes)) is null)
            throw new UserError("This folder already has its own Explorer settings (desktop.ini). Swivel leaves them unchanged.");
        // Keep the earlier record if Swivel set Read-only then; otherwise record what happens now.
        var ini = new SwivelFolderIni(FolderStyles.NewIconName(), previous?.ReadOnlyAdded == true || (held.Attributes & 1) == 0);
        token.ThrowIfCancellationRequested();
        PublishInFolder(held.Handle, folder, ini.Icon, icon);
        string? aside = null;
        bool iniPublished = false;
        try
        {
            // The old file is moved aside through its own handle, not deleted, so a failed
            // publish can put it back and the folder keeps its previous style.
            if (old is not null)
            {
                string name = ".swivel-" + Guid.NewGuid().ToString("N") + ".tmp";
                if (!RenameInto(old.Handle, held.Handle, name, out int error)) throw new Win32Exception(error);
                aside = name;
            }
            PublishInFolder(held.Handle, folder, FolderStyles.IniName, FolderStyles.IniBytes(ini));
            iniPublished = true;
        }
        finally
        {
            if (!iniPublished)
            {
                TryDeleteOurIcon(held.Handle, folder, ini.Icon);
                if (aside is not null && old is not null) RenameInto(old.Handle, held.Handle, FolderStyles.IniName, out _);
            }
        }
        if (old is not null) { try { old.Delete(); } catch (Win32Exception) { } } // a hidden leftover at worst
        SetFolderReadOnly(held.Handle, true);
        if (previous is not null) TryDeleteOurIcon(held.Handle, folder, previous.Icon);
        NotifyShell(folder);
    }

    public static void RemoveFolderStyle(string folder, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var held = HoldStyledFolder(folder);
        using var old = OpenIni(held.Handle, folder, true);
        if (old is null) throw new UserError("This folder has no Swivel style.");
        var previous = FolderStyles.ParseOurs(old.Bytes) ?? throw new UserError("This folder's desktop.ini was not written by Swivel, so it was left unchanged.");
        old.Delete();
        TryDeleteOurIcon(held.Handle, folder, previous.Icon);
        if (previous.ReadOnlyAdded) SetFolderReadOnly(held.Handle, false);
        NotifyShell(folder);
    }

    private sealed class StyledFolder : IDisposable
    {
        public required DirectoryLease Lease { get; init; }
        public required SafeFileHandle Handle { get; init; }
        public uint Attributes { get; init; }
        public void Dispose() { Handle.Dispose(); Lease.Dispose(); }
    }

    private static StyledFolder HoldStyledFolder(string folder)
    {
        if (System.IO.Path.GetDirectoryName(folder) is null) throw new UserError("Choose a folder, not a whole drive.");
        WindowsPathPolicy.Validate(folder);
        WindowsPathPolicy.Validate(System.IO.Path.Combine(folder, ".swivel-" + new string('0', 32) + ".tmp")); // the longest name written inside
        var lease = DirectoryLease.Open(folder);
        SafeFileHandle? handle = null;
        try
        {
            // A second handle on the same held folder, able to set its Read-only attribute.
            handle = Open(folder, ReadAttributes | Traverse | WriteAttributes, 3, 3, OpenReparse | BackupSemantics);
            Check(handle, true);
            if (!GetFileInformationByHandle(handle, out var info)) ThrowLast();
            return new StyledFolder { Lease = lease, Handle = handle, Attributes = info.Attributes };
        }
        catch { handle?.Dispose(); lease.Dispose(); throw; }
    }

    private sealed class HeldIni : IDisposable
    {
        public required SafeFileHandle Handle { get; init; }
        public required byte[] Bytes { get; init; }
        /// POSIX semantics free the name at once, even while another reader still has the file
        /// open; file systems without them fall back to delete-on-close.
        public void Delete()
        {
            uint flags = 0x1 | 0x2; // DELETE | POSIX_SEMANTICS
            if (SetDispositionEx(Handle, 21, ref flags, 4)) return;
            if (Marshal.GetLastWin32Error() is not 1 and not 50 and not 87 and not 124) ThrowLast();
            byte delete = 1;
            if (!SetDisposition(Handle, 4, ref delete, 1)) ThrowLast();
        }
        public void Dispose() => Handle.Dispose();
    }

    private static HeldIni? OpenIni(SafeFileHandle folderHandle, string folder, bool forDelete)
    {
        string path = System.IO.Path.Combine(folder, FolderStyles.IniName);
        var handle = CreateFile(path, GenericRead | ReadAttributes | (forDelete ? DeleteAccess : 0), 1, IntPtr.Zero, 3, OpenReparse, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error(); handle.Dispose();
            if (error is 2) return null;
            if (error is 32 or 33) throw new UserError("Another app is using this folder's desktop.ini. Try again in a moment.");
            throw new Win32Exception(error);
        }
        try
        {
            Check(handle, false);
            CheckCreatedIn(handle, folderHandle, path);
            if (!GetFileInformationByHandle(handle, out var info)) ThrowLast();
            long length = ((long)info.SizeHigh << 32) | info.SizeLow;
            // Too large to be Swivel's: classify it as someone else's without reading it all.
            var bytes = new byte[Math.Min(length, FolderStyles.MaxIniBytes + 1)];
            int read = 0;
            while (read < bytes.Length)
            {
                int count = RandomAccess.Read(handle, bytes.AsSpan(read), read); // never closes the handle
                if (count == 0) break;
                read += count;
            }
            if (read != bytes.Length) throw new UserError(FolderChanged);
            return new HeldIni { Handle = handle, Bytes = bytes };
        }
        catch { handle.Dispose(); throw; }
    }

    /// Exactly this name, created through a private staging file and renamed relative to the held folder.
    private static void PublishInFolder(SafeFileHandle folderHandle, string folder, string leaf, byte[] data)
    {
        WindowsPathPolicy.ValidateName(leaf);
        string temp = System.IO.Path.Combine(folder, ".swivel-" + Guid.NewGuid().ToString("N") + ".tmp");
        using var handle = Open(temp, GenericWrite | ReadAttributes | DeleteAccess, 0, 1, OpenReparse | 2 | 0x100);
        FileStream? stream = null;
        bool published = false;
        try
        {
            Check(handle, false);
            CheckCreatedIn(handle, folderHandle, temp);
            stream = new FileStream(handle, FileAccess.Write, 1, false);
            stream.Write(data); stream.Flush(true);
            var basic = new BasicInfo { Attributes = HiddenSystem };
            if (!SetBasic(handle, 0, ref basic, (uint)Marshal.SizeOf<BasicInfo>())) ThrowLast();
            if (!RenameInto(handle, folderHandle, leaf, out int error))
            {
                if (error is 80 or 183) throw new UserError(FolderChanged);
                throw new Win32Exception(error);
            }
            published = true;
        }
        finally
        {
            try
            {
                if (!published)
                {
                    // An exception is already propagating; a failed cleanup must not replace it.
                    byte delete = 1;
                    SetDisposition(handle, 4, ref delete, 1);
                }
            }
            finally { stream?.Dispose(); }
        }
    }

    /// Removes an icon only by a name Swivel generates and only through a handle inside the held folder.
    private static void TryDeleteOurIcon(SafeFileHandle folderHandle, string folder, string leaf)
    {
        if (!FolderStyles.IsIconName(leaf)) return;
        try { DeleteOurIcon(folderHandle, folder, leaf); }
        catch (Exception error) when (error is not OutOfMemoryException) { } // a leftover hidden icon at worst; never mask the real result
    }
    private static void DeleteOurIcon(SafeFileHandle folderHandle, string folder, string leaf)
    {
        string path = System.IO.Path.Combine(folder, leaf);
        var handle = CreateFile(path, ReadAttributes | DeleteAccess, 1, IntPtr.Zero, 3, OpenReparse, IntPtr.Zero);
        try
        {
            if (handle.IsInvalid) return;
            Check(handle, false);
            CheckCreatedIn(handle, folderHandle, path);
            byte delete = 1;
            SetDisposition(handle, 4, ref delete, 1);
        }
        finally { handle.Dispose(); }
    }

    /// Reads the current attributes just before changing only the Read-only bit.
    private static void SetFolderReadOnly(SafeFileHandle folderHandle, bool readOnly)
    {
        if (!GetFileInformationByHandle(folderHandle, out var info)) ThrowLast();
        uint attributes = readOnly ? info.Attributes | 1 : info.Attributes & ~1u;
        if (attributes == info.Attributes) return;
        // The directory flag itself is not settable; Normal stands for "no other attributes".
        uint value = attributes & ~0x10u;
        var basic = new BasicInfo { Attributes = value == 0 ? 0x80 : value };
        if (!SetBasic(folderHandle, 0, ref basic, (uint)Marshal.SizeOf<BasicInfo>())) ThrowLast();
    }

    private static void NotifyShell(string folder)
    {
        IntPtr path = Marshal.StringToHGlobalUni(folder);
        try
        {
            SHChangeNotify(0x00001000, 0x0005, path, IntPtr.Zero); // SHCNE_UPDATEDIR, SHCNF_PATHW
            SHChangeNotify(0x00002000, 0x0005, path, IntPtr.Zero); // SHCNE_UPDATEITEM, SHCNF_PATHW
        }
        finally { Marshal.FreeHGlobal(path); }
    }

    [DllImport("shell32.dll")] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
