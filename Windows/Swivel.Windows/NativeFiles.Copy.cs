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
            NativeFiles.RequireNtfs(directory, "Copy to Folder currently requires an NTFS destination, so every data stream and the download warning can be kept.");
            foreach (string path in paths)
            {
                token.ThrowIfCancellationRequested();
                var input = NativeFiles.OpenRead(path); session.sources.Add(input);
                session.TotalBytes += input.Stream.Length;
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
                var (copy, unverified) = NativeFiles.CopyInto(sources[i], Directory, token);
                copied.Add(new(sources[i].Path, copy));
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

    /// Returns the new copy and, when it exists but could not be confirmed, a warning.
    internal static (string Destination, UserError? Unverified) CopyInto(Input source, string directory, CancellationToken token)
    {
        string leaf = System.IO.Path.GetFileName(source.Path);
        var expected = Streams(source.Stream.SafeFileHandle);
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
                string? created = null; bool callbackFailed = false;
                CopyProgress progress = (message, _) =>
                {
                    try
                    {
                        if (Marshal.ReadInt32(message, 0) == 3 && Marshal.ReadInt32(message, 8) == 1)
                        {
                            using var handle = new SafeFileHandle(Marshal.ReadIntPtr(message, 16 + IntPtr.Size), false);
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
                if (result == 0) return (destination, Verify(destination, expected, created));
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
    // match the source; a mismatch removes it through this handle (read-only included).
    private static UserError? Verify(string destination, IReadOnlyList<(string, long)> expected, string? created)
    {
        if (created is null) return new UserError("A copy was made but could not be verified. Check it in the destination folder before relying on it.");
        SafeFileHandle handle;
        try { handle = Open(destination, ReadAttributes | DeleteAccess, 7, 3, OpenReparse); }
        catch (Win32Exception) { return new UserError("A copy was made but could not be verified. Check it in the destination folder before relying on it."); }
        using (handle)
        {
            string? current;
            try { current = Identity(handle); }
            catch (Win32Exception) { current = null; }
            if (current is null) return new UserError("A copy was made but could not be verified. Check it in the destination folder before relying on it.");
            if (current != created)
                throw new UserError("A copy was moved or replaced by another app before it could be verified. Swivel left it alone; check the destination folder.");
            bool keep;
            try { Check(handle, false); keep = CopyRules.SameStreams(expected, Streams(handle)); }
            catch (UserError) { keep = false; }
            catch (Win32Exception) { return new UserError("A copy was made but could not be verified. Check it in the destination folder before relying on it."); }
            if (keep) return null;
            var disposition = 0x1u | 0x2u | 0x10u; // DELETE | POSIX_SEMANTICS | IGNORE_READONLY_ATTRIBUTE
            if (!SetDispositionEx(handle, 21, ref disposition, 4))
                throw new UserError("A copy did not match its original and could not be removed. Check the destination folder. The original is unchanged.");
            throw new UserError("A copy did not match its original and was removed. The original is unchanged.");
        }
    }

    // FILE_ID_INFO: 64-bit volume serial plus 128-bit file ID; needs no particular access right.
    private static string? Identity(SafeFileHandle handle)
    {
        byte[] info = new byte[24];
        if (!GetStreamInfo(handle, 18, info, (uint)info.Length)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return info.AsSpan(8).IndexOfAnyExcept((byte)0) < 0 ? null : Convert.ToHexString(info);
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
