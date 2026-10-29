using System;
using System.Collections.Generic;
using System.Linq;
using Swivel.Core;

internal static class CleanupFilesChecks
{
    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        // Caches: selected only for a closed app with a measured folder.
        var chrome = AppCaches.Apps.First(); var other = AppCaches.Apps.Skip(1).First();
        AppCacheItem Item(AppCacheApp app, string path, AppCacheSize? size) => new(new AppCacheFolder(app, null, "Cache", path), size);
        var caches = new AppCacheResult(Array.Empty<AppCacheAppResult>(), new[]
        {
            Item(chrome, @"C:\U\a\Cache", new(300, 3, 3, false, null)),
            Item(other, @"C:\U\b\Cache", new(900, 9, 9, true, null)),
            Item(other, @"C:\U\b\Code Cache", new(0, 0, 0, false, "x")),
            Item(other, @"C:\U\b\GPUCache", null),
        }, false, false, false, false, DateTimeOffset.UnixEpoch);
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { chrome.Processes[0] };
        var cacheEntries = CleanupFiles.Caches(caches, running);
        check(cacheEntries.Select(e => e.Preselected).SequenceEqual(new[] { false, true, false, false }), "Only a closed app's cache with a size (even a partial one) is selected; a running app's, an unmeasurable and an unmeasured one are not");
        check(cacheEntries.Select(e => e.Partial).SequenceEqual(new[] { false, true, true, true }) && cacheEntries[1].Bytes == 900 && cacheEntries[3].Bytes == 0, "Partial, failed and unmeasured cache sizes are marked partial");
        check(CleanupFiles.Caches(caches, null).All(e => !e.Preselected), "When running apps cannot be listed, no cache is selected");

        // Temporary items, installers and builds: listed, never selected; full paths; sources kept.
        // Expected paths are joined the way the platform running the checks joins them.
        string J(string a, string b) => System.IO.Path.Combine(a, b);
        var temp = CleanupFiles.Temporary(@"C:\T", new TempCleanupPlan(new[] { new TempCandidate("old.log", false, 50, 1, 1, 1, 1), new TempCandidate("dir", true, 70, 2, 1, 1, 1) }, 0, 0, 120, 42));
        check(temp.Select(e => e.Path).SequenceEqual(new[] { J(@"C:\T", "old.log"), J(@"C:\T", "dir") }) && temp.All(e => !e.Preselected && e.Group == CleanupGroup.OldTemporary)
            && temp[1].Label == "Folder" && temp[0].Source is CleanupTemp { Cutoff: 42 }, "Old temporary items are listed with their search's cutoff, not selected");
        var large = new LargeFileResult(new[] { new LargeFile(@"setup.exe", 6_000_000, true), new LargeFile(@"a\tiny.msi", 4_999_999, true), new LargeFile(@"movie.mp4", 900_000_000, false), new LargeFile(@"b\exact.iso", 5_000_000, true) }, Array.Empty<LargeFileIssue>(), false, 4, 0);
        var installers = CleanupFiles.Installers(@"D:\Down", large, default);
        check(installers.Select(e => e.Path).SequenceEqual(new[] { J(@"D:\Down", "setup.exe"), J(@"D:\Down", @"b\exact.iso") }) && installers.All(e => !e.Preselected),
            "Installers of 5 MB or more are listed (a 4,999,999-byte one and a non-installer are not), not selected");
        var folder = new ProjectFolder(@"web\node_modules", @"web", "Node", new[] { @"web\package.json" }, 1, 1000, 10, true);
        var builds = CleanupFiles.Builds(@"C:\Docs", new ProjectFolderResult(new[] { folder }, Array.Empty<ProjectFolderIssue>()));
        check(builds.Single().Path == J(@"C:\Docs", @"web\node_modules") && builds[0].Partial && !builds[0].Preselected && builds[0].Source is CleanupBuild { Root: @"C:\Docs" }, "Build folders keep the folder they were found in");

        // One list: by kind, largest first, a path found twice once.
        var dup = builds[0] with { Path = builds[0].Path.ToUpperInvariant() };
        var all = CleanupFiles.Combine(builds.Concat(installers).Concat(new[] { dup }).Concat(temp).Concat(cacheEntries));
        check(all.Select(e => e.Group).SequenceEqual(all.Select(e => e.Group).Order()) && all.Count == 4 + 2 + 2 + 1, "The list is grouped in kind order and a path found twice appears once");
        check(all.Where(e => e.Group == CleanupGroup.OldTemporary).Select(e => e.Bytes).SequenceEqual(new decimal[] { 70, 50 }), "Largest first within a kind");
        check(CleanupFiles.Total(all) == (9, 300 + 900 + 0 + 0 + 70 + 50 + 6_000_000 + 5_000_000 + 1000), "Totals count items and bytes");

        var inner = installers[0] with { Path = System.IO.Path.Combine(builds[0].Path, "pkg", "setup.exe") };
        var sibling = installers[0] with { Path = builds[0].Path + " copy" + System.IO.Path.DirectorySeparatorChar + "setup.exe" };
        var nested = CleanupFiles.Combine(new[] { inner, builds[0], sibling });
        check(nested.Count == 2 && nested.Contains(builds[0]) && nested.Contains(sibling), "An item inside another listed folder is listed once, with the folder; a folder with a longer name beside it is not its parent");

        // Group tick boxes.
        check(CleanupFiles.GroupState(new[] { true, true }) == true && CleanupFiles.GroupState(new[] { false, false }) == false && CleanupFiles.GroupState(new[] { true, false }) is null
            && CleanupFiles.GroupState(Array.Empty<bool>()) == false, "A group shows ticked, clear or mixed");
        check(CleanupFiles.GroupClick(null) && CleanupFiles.GroupClick(false) && !CleanupFiles.GroupClick(true), "Clicking a mixed or clear group ticks all; a ticked one clears");

        // Catalog, Doctor area and text.
        var prefs = new Prefs(); var action = Catalog.Actions.Single(a => a.Id == "doctor.cleanup");
        check(action.Feature == Feature.Doctor && !action.RequiresFiles && !Catalog.Available(prefs, Array.Empty<string>()).Any(a => a.Id == "doctor.cleanup"), "Clean Up Files is a Doctor action, off while Doctor is off");
        prefs.Set(Feature.Doctor, true);
        check(Catalog.Available(prefs, Array.Empty<string>()).Any(a => a.Id == "doctor.cleanup") && WindowsDoctor.Areas.Single(a => a.Id == "uninstall") is { Title: "Clean Up Files" } area && area.ActionIds.SequenceEqual(new[] { "doctor.cleanup", "doctor.installedapps" }),
            "With Doctor on it is offered, first in the renamed Clean Up Files area (as Mac 1.14.5)");
        foreach (string text in new[] { "Clean Up Files", "Clean Up Files…", CleanupFiles.Notice, CleanupFiles.Limits, WindowsDoctor.Areas.Single(a => a.Id == "uninstall").Description,
            "Found: {0} items · {1} · selected: {2} items · {3}", "{0} items · {1}", "Select all: {0}", "Looking for things to clean: {0}…", "Move {0} items to the Recycle Bin?" }
            .Concat(Enum.GetValues<CleanupGroup>().Select(CleanupFiles.Name)))
            check(Strings.Table.TryGetValue(text, out var translated) && translated.Length == 3 && translated.All(t => Enumerable.Range(0, 4).All(n => !text.Contains("{" + n + "}") || t.Contains("{" + n + "}"))), "Clean Up Files text translated: " + text[..Math.Min(40, text.Length)]);
        reject(() => CleanupFiles.Name((CleanupGroup)9), "An unknown kind has no name");
    }
}
