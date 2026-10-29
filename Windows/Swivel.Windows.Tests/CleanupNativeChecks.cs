using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Swivel.Core;
using Swivel.Windows;

internal static class CleanupNativeChecks
{
    // Clean Up Files on real folders: an old temporary file, an installer and a build folder found by
    // the existing readers, shown in one window, ticked by group, moved to the Recycle Bin and back.
    public static void Run(string directory, Action<bool, string> check, Action<Action, string> reject)
    {
        string root = Path.Combine(directory, "cleanup"); string temp = Path.Combine(root, "temp"), downloads = Path.Combine(root, "downloads"), docs = Path.Combine(root, "docs");
        foreach (string folder in new[] { temp, downloads, Path.Combine(docs, "web", "node_modules", "pkg") }) Directory.CreateDirectory(folder);
        DateTime old = DateTime.UtcNow.AddDays(-30);
        string oldFile = Path.Combine(temp, "old.tmp"); File.WriteAllText(oldFile, "old"); TempCleanupNativeChecks.Backdate(oldFile, old, false);
        File.WriteAllText(Path.Combine(temp, "new.tmp"), "new");
        string setup = Path.Combine(downloads, "setup.msi"); File.WriteAllBytes(setup, new byte[6_000_000]);
        File.WriteAllBytes(Path.Combine(downloads, "small.exe"), new byte[1000]);
        File.WriteAllText(Path.Combine(docs, "web", "package.json"), "{}");
        string build = Path.Combine(docs, "web", "node_modules"); File.WriteAllText(Path.Combine(build, "pkg", "index.js"), "x");

        FolderScanResult tempScan; using (var held = NativeFiles.MetadataDirectory.Open(temp)) tempScan = FolderSizes.Scan(held, CancellationToken.None);
        var plan = TempCleanup.Plan(tempScan, DateTime.UtcNow);
        var entries = CleanupFiles.Combine(CleanupFiles.Temporary(temp, plan)
            .Concat(CleanupFiles.Installers(downloads, DoctorLargeFiles.Run(downloads, default).Files, default))
            .Concat(CleanupFiles.Builds(docs, DoctorProjects.Run(docs, default).Projects)));
        check(entries.Select(e => (e.Group, e.Path)).SequenceEqual(new[] { (CleanupGroup.OldTemporary, oldFile), (CleanupGroup.Installers, setup), (CleanupGroup.ProjectBuilds, build) }),
            "The real readers find the old temporary file, the 6 MB installer and the build folder, and nothing else: " + string.Join(", ", entries.Select(e => Path.GetFileName(e.Path))));

        var app = Application.Current as App ?? new App();
        var result = new CleanupReader.Result(entries, Enum.GetValues<CleanupGroup>().Select(g => new CleanupPart(g, entries.Count(e => e.Group == g), 0, false, null)).ToArray());
        var dialog = new CleanupFilesDialog(app, result);
        var buttons = ((DockPanel)dialog.Content).Children.OfType<WrapPanel>().Single().Children.OfType<Button>().ToArray();
        var move = buttons.Single(b => Equals(b.Content, app.L("Move Selected to Recycle Bin…")));
        var body = (StackPanel)((ScrollViewer)((DockPanel)dialog.Content).Children.OfType<ScrollViewer>().Single()).Content;
        var groups = body.Children.OfType<Expander>().ToArray();
        check(groups.Length == 3 && groups.All(g => !g.IsExpanded) && !move.IsEnabled && buttons.Last().IsCancel, "Three groups, folded; nothing found here is selected for you, so Move is off");
        var box = ((StackPanel)groups[1].Header).Children.OfType<CheckBox>().Single();
        box.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        check(box.IsChecked == true && move.IsEnabled && dialog.CurrentReport.Contains("[x] ") && dialog.CurrentReport.Contains("[ ] "), "Ticking the installers group selects it alone and enables Move");
        box.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        check(box.IsChecked == false && !move.IsEnabled, "Clicking a ticked group clears it");
        dialog.Close();

        if (!RecycleBin.Available(setup)) { Console.WriteLine("SKIP: no Recycle Bin on the test drive."); return; }
        var stored = entries.Select(e => CleanupFilesDialog.Recycle(e, IntPtr.Zero)).ToArray();
        check(!File.Exists(oldFile) && !File.Exists(setup) && !Directory.Exists(build) && File.Exists(Path.Combine(temp, "new.tmp")) && File.Exists(Path.Combine(docs, "web", "package.json")),
            "Each kind moves to the Recycle Bin under its own rules; everything else stays");
        for (int i = 0; i < entries.Count; i++) CleanupFilesDialog.Restore(entries[i], stored[i]);
        check(File.ReadAllText(oldFile) == "old" && new FileInfo(setup).Length == 6_000_000 && File.ReadAllText(Path.Combine(build, "pkg", "index.js")) == "x", "Undo puts each one back");

        // A build folder whose project file is gone is refused, as in Find Project Build Folders.
        File.Delete(Path.Combine(docs, "web", "package.json"));
        reject(() => CleanupFilesDialog.Recycle(entries.Single(e => e.Group == CleanupGroup.ProjectBuilds), IntPtr.Zero), "A build folder without its project file is not moved");
        check(Directory.Exists(build), "The refused build folder stays");

        // The real search on this machine runs every part and reads nothing it should not change.
        var found = CleanupReader.Find(_ => { }, default);
        Console.WriteLine("Clean Up Files on the CI machine: " + string.Join("; ", found.Parts.Select(p => $"{p.Group} {p.Count} {p.Bytes:N0}{(p.Partial ? " partial" : "")}{(p.Issue is null ? "" : " " + p.Issue)}")));
        check(found.Parts.Select(p => p.Group).SequenceEqual(Enum.GetValues<CleanupGroup>()) && found.Entries.All(e => Path.IsPathFullyQualified(e.Path)), "The real search reports all four parts with full paths");
    }
}
