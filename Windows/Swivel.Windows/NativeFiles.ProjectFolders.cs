using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Swivel.Core;

namespace Swivel.Windows;

public static partial class NativeFiles
{
    internal const string ProjectChanged = "The folder or its project files changed since the scan (or are now a link, system or cloud item), so it was left alone.";
    internal const string NoRecycleBin = "This drive has no Recycle Bin, so the folder was left alone.";
    internal const string NotRecycled = "Windows did not move the folder to the Recycle Bin; it was left alone.";
    internal const string RecycledUntracked = "Windows moved or removed the folder, but Swivel could not tell where, so Undo is not available for it. Check the Recycle Bin.";
    internal const string RemovedNotRecycled = "Windows removed the folder without putting it in the Recycle Bin (it asks first when a folder is too large for the Recycle Bin). It cannot be restored with Undo.";
    internal const string NotRestored = "Windows did not restore the folder. It may still be in the Recycle Bin.";
    internal const string RestoreTaken = "Something now exists at the folder's original place, so it was not restored.";
    private const uint SkippedProject = UnsafeAttributes | 0x4 | 0x40; // also system and device items

    /// Rechecks a build folder from Project Build Folders, then moves it to the Recycle Bin.
    /// The parent and its ancestors are held, so the folder stays where it was checked; the
    /// folder itself is held with delete sharing, so after the move its handle tells exactly
    /// where Windows put it. Returns that Recycle Bin path for Undo.
    internal static string RecycleProjectFolder(string folder, IEnumerable<string> markers, IntPtr owner = default)
    {
        WindowsPathPolicy.Validate(folder);
        string parent = System.IO.Path.GetDirectoryName(folder) ?? throw new UserError(ProjectChanged);
        using var lease = DirectoryLease.Open(parent);
        using var held = OpenPlain(folder, true);
        foreach (string marker in markers) { WindowsPathPolicy.Validate(marker); OpenPlain(marker, false).Dispose(); }
        if (!RecycleBin.Available(folder)) throw new UserError(NoRecycleBin);
        if (!RecycleBin.Move(folder, owner) || GetAttributes(folder) != uint.MaxValue) throw new UserError(NotRecycled);
        // A folder Windows deleted permanently is only pending deletion behind our handle.
        var standard = new byte[24]; // FILE_STANDARD_INFO; DeletePending at offset 20
        if (GetDirectoryInfo(held, 1, standard, (uint)standard.Length) && standard[20] != 0) throw new UserError(RemovedNotRecycled);
        string stored;
        try { stored = FinalPath(held); } catch (Exception error) when (error is Win32Exception or UserError) { throw new UserError(RecycledUntracked); }
        if (stored.StartsWith(@"\\?\", StringComparison.Ordinal)) stored = stored[4..];
        string bin = System.IO.Path.Combine(System.IO.Path.GetPathRoot(folder)!, "$Recycle.Bin") + "\\";
        if (!stored.StartsWith(bin, StringComparison.OrdinalIgnoreCase)) throw new UserError(RecycledUntracked);
        return stored;
    }

    /// Puts a folder moved by RecycleProjectFolder back with the shell's Restore, only while
    /// nothing exists at its original place. Call on an STA thread.
    internal static void RestoreProjectFolder(string folder, string stored)
    {
        WindowsPathPolicy.Validate(folder);
        using (var lease = DirectoryLease.Open(System.IO.Path.GetDirectoryName(folder)!))
            if (GetAttributes(folder) != uint.MaxValue) throw new UserError(RestoreTaken);
        // The shell restores by its own path; the parent cannot stay held while it renames back.
        if (!RecycleBin.Restore(stored)) throw new UserError(NotRestored);
        // The shell may finish the restore a moment after the verb returns.
        for (int wait = 0; wait < 50; wait++)
        {
            uint attributes = GetAttributes(folder);
            if (attributes != uint.MaxValue) { if ((attributes & 0x10) == 0) throw new UserError(NotRestored); return; }
            using (var pause = new ManualResetEvent(false)) pause.WaitOne(100); // pumps COM on this STA, unlike Thread.Sleep
        }
        throw new UserError(NotRestored);
    }

    private static SafeFileHandle OpenPlain(string path, bool directory)
    {
        SafeFileHandle handle;
        try { handle = Open(path, ReadAttributes, 7, 3, OpenReparse | BackupSemantics); }
        catch (Win32Exception) { throw new UserError(ProjectChanged); }
        if (GetFileType(handle) != 1 || !GetFileInformationByHandle(handle, out var info) || (info.Attributes & SkippedProject) != 0 || ((info.Attributes & 0x10) != 0) != directory)
        { handle.Dispose(); throw new UserError(ProjectChanged); }
        return handle;
    }
}
