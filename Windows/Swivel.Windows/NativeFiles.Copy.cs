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

/// Copy to Folder. Sources stay open without write/delete sharing and the destination's
/// ancestors are leased from planning until the last copy, so the reviewed files are the
/// ones copied. CopyFile2 copies every data stream (including Mark of the Web), attributes
/// and the modified time, and creates each destination exclusively (fail if it exists).
public sealed class CopySession : IDisposable
{
    private readonly List<NativeFiles.Input> sources = new();
    private NativeFiles.DirectoryLease? destination;
    public string Directory { get; }
    public IReadOnlyList<string> Sources => sources.ConvertAll(x => x.Path);
    public long TotalBytes { get; private set; }
    /// Set for an exFAT or FAT32 destination, which holds only main data streams.
    public VolumeFormat? FatFormat { get; private set; }
    public bool IsFat => FatFormat is not null;
    private readonly List<string> zoneMarked = new();
    /// Sources carrying the download warning, which an exFAT/FAT32 copy cannot keep.
    public IReadOnlyList<string> ZoneMarked => zoneMarked;
    /// The user explicitly accepted that those copies lose the download warning.
    public bool AcceptZoneLoss { get; set; }
    private CopySession(string directory) { Directory = directory; }

    public static CopySession Open(IReadOnlyList<string> files, string directory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var paths = CopyRules.Validate(files, directory);
        directory = MoveRules.DirectoryPath(directory);
        var session = new CopySession(directory);
        try
        {
            session.destination = NativeFiles.DirectoryLease.Open(directory);
            var format = NativeFiles.Format(directory);
            if (FatRules.IsFat(format)) session.FatFormat = format;
            else NativeFiles.RequireNtfs(directory, "Copy to Folder currently requires an NTFS destination, so every data stream and the download warning can be kept.");
            foreach (string path in paths)
            {
                token.ThrowIfCancellationRequested();
                var input = NativeFiles.OpenRead(path); session.sources.Add(input);
                session.TotalBytes += input.Stream.Length;
                if (session.FatFormat is { } fat)
                {
                    // Refused before anything is copied: names, FAT32's size limit, other streams.
                    string leaf = Path.GetFileName(path);
                    FatRules.CheckName(leaf); FatRules.CheckSize(fat, (ulong)input.Stream.Length, leaf);
                    if (FatRules.CheckStreams(NativeFiles.StreamsOf(input.Stream.SafeFileHandle), leaf)) session.zoneMarked.Add(path);
                }
            }
            if (!NativeFiles.HasFreeSpace(directory, (ulong)session.TotalBytes + (ulong)paths.Count * 4096 + 64UL * 1024 * 1024))
                throw new UserError("There is not enough free space in the destination folder.");
            return session;
        }
        catch { session.Dispose(); throw; }
    }

    /// Stops at the first failure or cancellation; completed copies stay and are reported.
    public CopyOutcome Execute(CancellationToken token, Action<int, int>? progress = null)
    {
        var copied = new List<CopyResult>();
        try
        {
            for (int i = 0; i < sources.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var (copy, stamp, unverified) = NativeFiles.CopyInto(sources[i], Directory, token, null, FatFormat, AcceptZoneLoss);
                copied.Add(new(sources[i].Path, copy) { Stamp = stamp, Verified = unverified is null });
                if (unverified is not null) return new(copied, sources.Count, unverified, false);
                progress?.Invoke(i + 1, sources.Count);
            }
            return new(copied, sources.Count, null, false);
        }
        catch (OperationCanceledException) { return new(copied, sources.Count, null, true); }
        catch (Exception error) when (error is not OutOfMemoryException) { return new(copied, sources.Count, error, false); }
    }

    public void Dispose()
    {
        foreach (var input in sources) input.Dispose();
        sources.Clear(); destination?.Dispose(); destination = null;
    }
}

public static partial class NativeFiles
{
    internal static void RequireNtfs(string path, string message)
    {
        var fileSystem = new StringBuilder(64);
        if (!GetVolumeInformation(path[..3], null, 0, out _, out _, out _, fileSystem, fileSystem.Capacity)) ThrowLast();
        if (fileSystem.ToString() != "NTFS") throw new UserError(message);
    }
    internal static bool HasFreeSpace(string directory, ulong needed)
    {
        if (!GetDiskFreeSpaceEx(directory.TrimEnd('\\') + "\\", out ulong available, out _, out _)) ThrowLast();
        return available >= needed;
    }

    /// How many of `files` are on the volume of `directory` (FILE_ID_INFO volume serial), or
    /// null when that cannot be read. Only chooses between moving and offering a copy: the move
    /// itself refuses another volume again. Like the move, nothing is opened on a network or
    /// optical drive, so an unreachable server cannot stall the choice.
    internal static int? CountOnVolumeOf(IReadOnlyList<string> files, string directory)
    {
        try
        {
            ulong Volume(string path, bool folder)
            {
                if (GetDriveType(path[..3]) is not 2 and not 3 and not 6) throw new UserError(LocalOnly);
                using var handle = Open(path, ReadAttributes, 7, 3, OpenReparse | (folder ? BackupSemantics : 0));
                return VolumeKey(handle);
            }
            ulong target = Volume(MoveRules.DirectoryPath(directory), true);
            int count = 0;
            foreach (string file in files) if (Volume(WindowsPathPolicy.Validate(file), false) == target) count++;
            return count;
        }
        catch (Exception error) when (error is Win32Exception or UserError or IOException or UnauthorizedAccessException) { return null; }
    }

    /// Returns the new copy, its stamp once verified and, when it exists but could not be
    /// confirmed, a warning.
    /// With `folder` (a held folder this app created), the copy must be created in that very
    /// folder: if the path was redirected (an empty folder turned into a mount point), CopyFile2 is
    /// cancelled, which removes the copy, and the copy is refused.
    /// With `fat` (an exFAT or FAT32 destination), only the main stream is copied and checked: a
    /// download warning is left behind only with `acceptZoneLoss`, any other stream refuses.
    internal static (string Destination, DuplicateStamp? Stamp, UserError? Unverified) CopyInto(Input source, string directory, CancellationToken token, SafeFileHandle? folder = null, VolumeFormat? fat = null, bool acceptZoneLoss = false)
    {
        string? inside = folder is null ? null : FinalPath(folder);
        string leaf = System.IO.Path.GetFileName(source.Path);
        // A source on exFAT/FAT32 has only its main stream.
        var expected = StreamsOf(source.Stream.SafeFileHandle);
        var before = expected;
        if (fat is { } format)
        {
            // Checked again here: a stream may have been added since the preview.
            expected = FatRules.CopyStreams(expected, acceptZoneLoss, leaf);
            FatRules.CheckSize(format, (ulong)source.Stream.Length, leaf);
        }
        IntPtr cancel = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(cancel, 0);
            using var registration = token.Register(() => Marshal.WriteInt32(cancel, 1));
            for (int i = 1; i <= 10000; i++)
            {
                token.ThrowIfCancellationRequested();
                string destination = System.IO.Path.Combine(directory, CopyRules.Name(leaf, i));
                WindowsPathPolicy.Validate(destination);
                // CopyFile2 hands us the destination it created: its identity is recorded from that
                // handle, so verification never trusts whatever happens to be at the path later.
                string? created = null; bool callbackFailed = false, redirected = false;
                CopyProgress progress = (message, _) =>
                {
                    try
                    {
                        if (Marshal.ReadInt32(message, 0) == 3 && Marshal.ReadInt32(message, 8) == 1)
                        {
                            using var handle = new SafeFileHandle(Marshal.ReadIntPtr(message, 16 + IntPtr.Size), false);
                            if (inside is not null && !string.Equals(FinalPath(handle), System.IO.Path.Combine(inside, System.IO.Path.GetFileName(destination)), StringComparison.Ordinal))
                            { redirected = true; return 1; }
                            created = Identity(handle);
                        }
                        return 0;
                    }
                    catch { callbackFailed = true; return 1; } // COPYFILE2_PROGRESS_CANCEL: never let an exception cross into CopyFile2
                };
                var parameters = new CopyParameters
                {
                    Size = (uint)Marshal.SizeOf<CopyParameters>(),
                    // FAIL_IF_EXISTS claims the name with the copy itself; COPY_SYMLINK never follows a link.
                    Flags = 0x1 | 0x800,
                    Cancel = cancel,
                    Progress = Marshal.GetFunctionPointerForDelegate(progress)
                };
                int result = CopyFile2(source.Path, destination, ref parameters);
                GC.KeepAlive(progress);
                if (redirected) throw new UserError(FolderCopy.Interfered);
                // On FAT a stream another app added to the source during the copy would vanish
                // silently (on NTFS the copy's stream list shows it): the source must be unchanged.
                bool sourceUnchanged = fat is null || CopyRules.SameStreams(before, StreamsOf(source.Stream.SafeFileHandle));
                if (result == 0) { var (unverified, stamp) = Verify(destination, expected, created, fat is not null, sourceUnchanged); return (destination, stamp, unverified); }
                if (callbackFailed && !token.IsCancellationRequested) throw new UserError("The copy could not be verified.");
                if (unchecked((uint)result & 0xFFFF0000) != 0x80070000) throw Marshal.GetExceptionForHR(result)!;
                int error = result & 0xFFFF;
                if (error is 80 or 183) continue;
                if (error == 1235 || token.IsCancellationRequested) throw new OperationCanceledException(token);
                throw new Win32Exception(error);
            }
            throw new UserError("Too many files have this output name.");
        }
        finally { Marshal.FreeHGlobal(cancel); }
    }

    // Only the object CopyFile2 created is checked or removed. Its stream names and sizes must
    // match the source; a mismatch removes it through this handle (read-only included). A kept
    // copy's stamp is read through the same handle, so undoing the copy recognises that object.
    // exFAT/FAT32 (`fat`): no stream list there, and no stamp, so a copy is never offered for undo.
    private static (UserError? Unverified, DuplicateStamp? Stamp) Verify(string destination, IReadOnlyList<(string, long)> expected, string? created, bool fat = false, bool sourceUnchanged = true)
    {
        const string unverified = "A copy was made but could not be verified. Check it in the destination folder before relying on it.";
        if (created is null) return (new UserError(unverified), null);
        SafeFileHandle handle;
        try { handle = Open(destination, ReadAttributes | DeleteAccess, 7, 3, OpenReparse); }
        catch (Win32Exception) { return (new UserError(unverified), null); }
        using (handle)
        {
            string? current;
            try { current = Identity(handle); }
            catch (Win32Exception) { current = null; }
            if (current is null) return (new UserError(unverified), null);
            if (current != created)
                throw new UserError("A copy was moved or replaced by another app before it could be verified. Swivel left it alone; check the destination folder.");
            bool keep;
            try { Check(handle, false); keep = sourceUnchanged && CopyRules.SameStreams(expected, fat ? StreamsOf(handle) : Streams(handle)); }
            catch (UserError) { keep = false; }
            catch (Win32Exception) { return (new UserError(unverified), null); }
            if (keep && fat) return (null, null);
            if (keep)
            {
                // A copy with attributes other tools skip (system, offline…) is simply not offered for undo.
                try { return (null, ReadDuplicateStamp(handle, ReadIdentity(handle).Volume)); }
                catch (Exception error) when (error is Win32Exception or UserError) { return (null, null); }
            }
            var disposition = 0x1u | 0x2u | 0x10u; // DELETE | POSIX_SEMANTICS | IGNORE_READONLY_ATTRIBUTE
            byte delete = 1; // FAT has no POSIX delete: the plain disposition, through the same handle.
            if (fat ? !SetDisposition(handle, 4, ref delete, 1) : !SetDispositionEx(handle, 21, ref disposition, 4))
                throw new UserError("A copy did not match its original and could not be removed. Check the destination folder. The original is unchanged.");
            throw new UserError("A copy did not match its original and was removed. The original is unchanged.");
        }
    }

    // FILE_ID_INFO: 64-bit volume serial plus 128-bit file ID; needs no particular access right.
    // exFAT/FAT32 have no FILE_ID_INFO: there the volume serial and directory entry position stand
    // in, which is enough to see the just-created copy moved or replaced before it is verified.
    private static string? Identity(SafeFileHandle handle)
    {
        byte[] info = new byte[24];
        if (!GetStreamInfo(handle, 18, info, (uint)info.Length))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != 87 || !GetFileInformationByHandle(handle, out var fat)) throw new Win32Exception(error);
            return "FAT" + fat.Volume.ToString("X8") + fat.IndexHigh.ToString("X8") + fat.IndexLow.ToString("X8");
        }
        return info.AsSpan(8).IndexOfAnyExcept((byte)0) < 0 ? null : Convert.ToHexString(info);
    }

    /// The stream list, or on a drive without streams (exFAT, FAT32) the main stream alone.
    internal static IReadOnlyList<(string Name, long Size)> StreamsOf(SafeFileHandle handle)
    {
        try { return Streams(handle); }
        // Only on a drive that has no streams: elsewhere a failed listing must never hide a download warning.
        catch (Win32Exception error) when (error.NativeErrorCode is 1 or 50 or 87 && FatRules.IsFat(Format(handle)))
        {
            if (!GetFileInformationByHandle(handle, out var info)) ThrowLast();
            return new[] { (FatRules.MainStream, (long)(((ulong)info.SizeHigh << 32) | info.SizeLow)) };
        }
    }

    private static IReadOnlyList<(string Name, long Size)> Streams(SafeFileHandle handle)
    {
        byte[] buffer = new byte[64 * 1024];
        if (!GetStreamInfo(handle, 7, buffer, (uint)buffer.Length))
        {
            if (Marshal.GetLastWin32Error() == 234) throw new UserError("This file has too many alternate data streams to copy safely.");
            ThrowLast();
        }
        return CopyRules.ParseStreamInfo(buffer);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyParameters
    {
        public uint Size, Flags;
        public IntPtr Cancel, Progress, Context;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int CopyProgress(IntPtr message, IntPtr context);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    private static extern bool SetDispositionEx(SafeFileHandle handle, int kind, ref uint flags, uint size);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CopyFile2(string existing, string destination, ref CopyParameters parameters);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    private static extern bool GetStreamInfo(SafeFileHandle handle, int kind, [Out] byte[] buffer, uint size);
}
