using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Swivel.Core;

public enum CleanupGroup { AppCaches, OldTemporary, Installers, ProjectBuilds }

/// One thing Clean Up Files offers. `Source` is the original search's record (AppCacheItem,
/// CleanupTemp, LargeFile or CleanupBuild); each is still moved under its own tool's rules.
public sealed record CleanupEntry(CleanupGroup Group, string Path, string Label, decimal Bytes, bool Partial, bool Preselected, object Source);

/// An old temporary item with the cutoff its search used.
public sealed record CleanupTemp(TempCandidate Item, long Cutoff);
/// A project build folder with the folder it was found in (its paths are relative to that).
public sealed record CleanupBuild(string Root, ProjectFolder Folder);

/// How one part of the search went, for the summary.
public sealed record CleanupPart(CleanupGroup Group, int Count, decimal Bytes, bool Partial, string? Issue);

/// Clean Up Files (as Mac 1.14.5): one search, one list grouped by kind, one move to the Recycle
/// Bin with Undo. Windows gathers what its existing Doctor tools already find: cache folders of
/// known browsers and apps, old items in the temporary folder, installers in Downloads and
/// project build folders in Documents, Desktop and source. Only caches of apps that are not running
/// are selected for you, as on Mac.
public static class CleanupFiles
{
    /// Installers this size or larger are offered (Mac uses the same 5 MB).
    public const long InstallerMinimum = 5_000_000;
    public const string Notice = "Clean Up Files looks in four places Windows Doctor already checks: cache folders of known browsers and apps, items in the temporary folder unchanged for 7 days, installers of 5 MB or more in Downloads, and project build folders in Documents, Desktop and your source folder. Only cache folders of apps that are not running are selected for you; review everything else and tick what you no longer need. Each item is checked again before it moves to the Recycle Bin, and Undo restores the last move while this window is open.";
    public const string Limits = "Each place is read within its tool's limits (the temporary folder, Downloads and each project folder four levels deep, 20,000 entries and about 30 seconds each; app caches within Review Browser and App Caches' own limits), so a list can be partial. Sizes are logical and may count shared files twice. Not included here: leftovers of removed apps (they need the installation records from before you uninstalled, so they stay in Inspect Desktop Installation Records), duplicate and similar files (Compare Common Folders), large files that are not installers (Find Large Files) and apps not opened for a while (Windows keeps no reliable last-opened date for desktop apps).";

    public static string Name(CleanupGroup group) => group switch
    {
        CleanupGroup.AppCaches => "Browser and App Caches",
        CleanupGroup.OldTemporary => "Old Temporary Files",
        CleanupGroup.Installers => "Installers in Downloads",
        CleanupGroup.ProjectBuilds => "Project Build Folders",
        _ => throw new ArgumentOutOfRangeException(nameof(group)),
    };

    /// Cache folders; selected only when the app is known not to be running and the folder was measured.
    public static IReadOnlyList<CleanupEntry> Caches(AppCacheResult result, IReadOnlySet<string>? running) =>
        result.Items.Select(item => new CleanupEntry(CleanupGroup.AppCaches, item.Folder.Path, item.Folder.App.Name,
            item.Size?.Bytes ?? 0, item.Size is null || item.Size.Partial || item.Size.Issue is not null,
            item.Size is { Issue: null } && !AppCaches.IsRunning(item.Folder.App, running), item)).ToArray();

    public static IReadOnlyList<CleanupEntry> Temporary(string folder, TempCleanupPlan plan) =>
        plan.Old.Select(item => new CleanupEntry(CleanupGroup.OldTemporary, System.IO.Path.Combine(folder, item.Name), item.Directory ? "Folder" : "File",
            item.Bytes, false, false, new CleanupTemp(item, plan.Cutoff))).ToArray();

    public static IReadOnlyList<CleanupEntry> Installers(string downloads, LargeFileResult result, CancellationToken token) =>
        LargeFiles.Filter(result, InstallerMinimum, true, token).Select(file => new CleanupEntry(CleanupGroup.Installers,
            System.IO.Path.Combine(downloads, file.RelativePath), "Installer", file.Bytes, false, false, file)).ToArray();

    public static IReadOnlyList<CleanupEntry> Builds(string root, ProjectFolderResult result) =>
        result.Folders.Select(folder => new CleanupEntry(CleanupGroup.ProjectBuilds, System.IO.Path.Combine(root, folder.RelativePath), folder.Kind,
            folder.Bytes, folder.Partial, false, new CleanupBuild(root, folder))).ToArray();

    /// All entries in group order, largest first within a group, then by path. A path found twice
    /// (one searched folder inside another), or inside another listed folder, is listed once: moving
    /// the outer folder moves it too.
    public static IReadOnlyList<CleanupEntry> Combine(IEnumerable<CleanupEntry> entries)
    {
        var unique = entries.DistinctBy(e => Trim(e.Path), StringComparer.OrdinalIgnoreCase).ToArray();
        var folders = new HashSet<string>(unique.Select(e => Trim(e.Path)), StringComparer.OrdinalIgnoreCase);
        bool Inside(string path)
        {
            for (string? parent = System.IO.Path.GetDirectoryName(Trim(path)); !string.IsNullOrEmpty(parent); parent = System.IO.Path.GetDirectoryName(parent))
                if (folders.Contains(parent)) return true;
            return false;
        }
        return unique.Where(e => !Inside(e.Path)).OrderBy(e => e.Group).ThenByDescending(e => e.Bytes).ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static string Trim(string path) => path.TrimEnd('\\', '/');

    /// A group's tick box: true when all are ticked, false when none, null when some.
    public static bool? GroupState(IEnumerable<bool> ticks)
    {
        bool any = false, all = true, empty = true;
        foreach (bool tick in ticks) { empty = false; any |= tick; all &= tick; }
        return empty || !any ? false : all ? true : null;
    }

    /// Clicking a group's box: ticks every item unless all already are, then clears them.
    public static bool GroupClick(bool? state) => state != true;

    public static (int Count, decimal Bytes) Total(IEnumerable<CleanupEntry> entries)
    {
        int count = 0; decimal bytes = 0;
        foreach (var entry in entries) { count++; bytes += entry.Bytes; }
        return (count, bytes);
    }
}
