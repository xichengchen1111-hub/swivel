using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Swivel.Core;

namespace Swivel.Windows;

public static partial class NativeFiles
{
    // Shown after the item's path, so they speak of "it".
    internal const string RecycleChanged = "It changed since the scan (or is now a link, system or cloud item, or has other hard links), so it was left alone.";
    internal const string NoRecycleBin = "This drive has no Recycle Bin, so it was left alone.";
    internal const string NotRecycled = "Windows did not move it to the Recycle Bin; it was left alone.";
    internal const string RecycledUntracked = "Windows moved or removed it, but Swivel could not tell where, so Undo is not available for it. Check the Recycle Bin.";
    internal const string RemovedNotRecycled = "Windows removed it without putting it in the Recycle Bin (it asks first when an item is too large for the Recycle Bin). It cannot be restored with Undo.";
    internal const string NotRestored = "Windows did not restore it. It may still be in the Recycle Bin.";
    internal const string RestoreTaken = "Something now exists at its original place, so it was not restored.";
    private const uint SkippedForRecycle = UnsafeAttributes | 0x4 | 0x40; // also system and device items

    /// Rechecks a build folder from Project Build Folders (plain folder, every shown project
    /// file still a plain file), then moves it to the Recycle Bin. Returns where it went.
    internal static string RecycleProjectFolder(string folder, IEnumerable<string> markers, IntPtr owner = default)
        => Recycle(folder, true, _ => { foreach (string marker in markers) { WindowsPathPolicy.Validate(marker); OpenPlain(marker, false).Dispose(); } }, owner);

    /// Rechecks a file from Large Files (plain local file, the recorded length, no other hard
    /// link, since recycling one name of several frees nothing), then moves it to the Recycle Bin.
    internal static string RecycleFile(string file, decimal bytes, IntPtr owner = default)
        => Recycle(file, false, held =>
        {
            if (!GetFileInformationByHandle(held, out var info) || info.Links != 1 || (((ulong)info.SizeHigh << 32) | info.SizeLow) != bytes)
                throw new UserError(RecycleChanged);
        }, owner);

    internal const string DuplicateChanged = "Its contents no longer match its group, so it was left alone.";
    internal const string NoKeptCopy = "No other file of its group could be checked as still identical, so it was left alone.";

    /// Rechecks a file from Duplicate Files and moves it to the Recycle Bin only while another
    /// file of its group is held unchanged: `keep` (in order) is tried until one is a different
    /// plain local file with the group's length and SHA-256. That copy stays open without
    /// write or delete sharing until the move ends, so it cannot change or vanish meanwhile.
    /// The file itself must still be a plain local file with one link and the same contents.
    internal static string RecycleDuplicate(string file, long length, string sha256, IEnumerable<string> keep, IntPtr owner = default)
    {
        SafeFileHandle? kept = null;
        try
        {
            foreach (string candidate in keep)
            {
                SafeFileHandle handle;
                try { WindowsPathPolicy.Validate(candidate); handle = Open(candidate, GenericRead | ReadAttributes, 1, 3, OpenReparse); }
                catch (Exception error) when (error is Win32Exception or UserError) { continue; }
                try
                {
                    if (GetFileType(handle) == 1 && GetFileInformationByHandle(handle, out var info) && (info.Attributes & (SkippedForRecycle | 0x10)) == 0
                        && (((ulong)info.SizeHigh << 32) | info.SizeLow) == (ulong)length && Sha256(handle, length) == sha256) { kept = handle; break; }
                }
                catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException) { } // try the next one
                handle.Dispose();
            }
            if (kept is null || !GetFileInformationByHandle(kept, out var keptInfo)) throw new UserError(NoKeptCopy);
            return Recycle(file, false, held =>
            {
                if (!GetFileInformationByHandle(held, out var info) || info.Links != 1 || (((ulong)info.SizeHigh << 32) | info.SizeLow) != (ulong)length)
                    throw new UserError(RecycleChanged);
                if (info.Volume == keptInfo.Volume && info.IndexHigh == keptInfo.IndexHigh && info.IndexLow == keptInfo.IndexLow) throw new UserError(NoKeptCopy);
                SafeFileHandle data;
                try { data = Open(file, GenericRead | ReadAttributes, 7, 3, OpenReparse); }
                catch (Win32Exception) { throw new UserError(RecycleChanged); }
                using (data)
                {
                    if (!GetFileInformationByHandle(data, out var same) || same.Volume != info.Volume || same.IndexHigh != info.IndexHigh || same.IndexLow != info.IndexLow)
                        throw new UserError(RecycleChanged);
                    if (Sha256(data, length) != sha256) throw new UserError(DuplicateChanged);
                }
            }, owner);
        }
        finally { kept?.Dispose(); }
    }

    private static string Sha256(SafeFileHandle handle, long length)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long offset = 0;
        while (true)
        {
            int read = System.IO.RandomAccess.Read(handle, buffer, offset);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read); offset += read;
            if (offset > length) return "";
        }
        return offset == length ? Convert.ToHexString(hash.GetHashAndReset()) : "";
    }

    /// The parent and its ancestors are held, so the item stays where it was checked; the item
    /// itself is held with delete sharing, so after the move its handle tells exactly where
    /// Windows put it. Returns that Recycle Bin path for Undo.
    private static string Recycle(string path, bool directory, Action<SafeFileHandle> check, IntPtr owner)
    {
        WindowsPathPolicy.Validate(path);
        string parent = System.IO.Path.GetDirectoryName(path) ?? throw new UserError(RecycleChanged);
        using var lease = DirectoryLease.Open(parent);
        using var held = OpenPlain(path, directory);
        check(held);
        if (!RecycleBin.Available(path)) throw new UserError(NoRecycleBin);
        if (!RecycleBin.Move(path, owner) || GetAttributes(path) != uint.MaxValue) throw new UserError(NotRecycled);
        // An item Windows deleted permanently is only pending deletion behind our handle.
        var standard = new byte[24]; // FILE_STANDARD_INFO; DeletePending at offset 20
        if (GetDirectoryInfo(held, 1, standard, (uint)standard.Length) && standard[20] != 0) throw new UserError(RemovedNotRecycled);
        string stored;
        try { stored = FinalPath(held); } catch (Exception error) when (error is Win32Exception or UserError) { throw new UserError(RecycledUntracked); }
        if (stored.StartsWith(@"\\?\", StringComparison.Ordinal)) stored = stored[4..];
        string bin = System.IO.Path.Combine(System.IO.Path.GetPathRoot(path)!, "$Recycle.Bin") + "\\";
        if (!stored.StartsWith(bin, StringComparison.OrdinalIgnoreCase)) throw new UserError(RecycledUntracked);
        return stored;
    }

    /// Puts an item moved by Recycle back with the shell's Restore, only while nothing exists
    /// at its original place. Call on an STA thread.
    internal static void RestoreRecycled(string path, string stored, bool directory)
    {
        WindowsPathPolicy.Validate(path);
        using (var lease = DirectoryLease.Open(System.IO.Path.GetDirectoryName(path)!))
            if (GetAttributes(path) != uint.MaxValue) throw new UserError(RestoreTaken);
        // The shell restores by its own path; the parent cannot stay held while it renames back.
        if (!RecycleBin.Restore(stored)) throw new UserError(NotRestored);
        // The shell may finish the restore a moment after the verb returns.
        for (int wait = 0; wait < 50; wait++)
        {
            uint attributes = GetAttributes(path);
            if (attributes != uint.MaxValue) { if (((attributes & 0x10) != 0) != directory) throw new UserError(NotRestored); return; }
            using (var pause = new ManualResetEvent(false)) pause.WaitOne(100); // pumps COM on this STA, unlike Thread.Sleep
        }
        throw new UserError(NotRestored);
    }

    private static SafeFileHandle OpenPlain(string path, bool directory)
    {
        SafeFileHandle handle;
        try { handle = Open(path, ReadAttributes, 7, 3, OpenReparse | BackupSemantics); }
        catch (Win32Exception) { throw new UserError(RecycleChanged); }
        if (GetFileType(handle) != 1 || !GetFileInformationByHandle(handle, out var info) || (info.Attributes & SkippedForRecycle) != 0 || ((info.Attributes & 0x10) != 0) != directory)
        { handle.Dispose(); throw new UserError(RecycleChanged); }
        return handle;
    }
}
