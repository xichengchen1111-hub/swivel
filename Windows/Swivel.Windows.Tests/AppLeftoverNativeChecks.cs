using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Swivel.Core;
using Swivel.Windows;

internal static class AppLeftoverNativeChecks
{
    public static void Run(string directory, Action<bool,string> check, Action<Action,string> reject)
    {
        string root = Path.Combine(directory, "app-leftovers"), local = Path.Combine(root, "Local"), roaming = Path.Combine(root, "Roaming"), low = Path.Combine(root, "LocalLow"), programs = Path.Combine(local, "Programs");
        void Put(string path, int bytes) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); System.IO.File.WriteAllBytes(path, new byte[bytes]); }
        string foo = Path.Combine(local, "Foo App"), vendor = Path.Combine(local, @"Foo Inc\Foo App"), kept = Path.Combine(roaming, "Keep");
        Put(Path.Combine(foo, @"data\a.bin"), 300); Put(Path.Combine(foo, "b.bin"), 20); Put(Path.Combine(vendor, "c.bin"), 5); Put(Path.Combine(kept, "settings.json"), 9);
        Put(Path.Combine(programs, @"Prog\prog.exe"), 7); Directory.CreateDirectory(low);
        string marked = Path.Combine(roaming, "Foo App"); Directory.CreateDirectory(marked);
        new DirectoryInfo(marked).Attributes |= FileAttributes.System;

        static RegistrationText Text(string value) => new(value, RegistrationValueState.Present);
        static InstalledAppRecord Record(string name, string publisher) => new(RegistrationSource.User64, 0, name + " key", Text(name), Text("1"), Text(publisher), RegistrationEntryIssue.None);
        var before = new InstalledAppsSnapshot(DateTimeOffset.Now, new[] { Record("Foo App", "Foo Inc"), Record("Keep", "KeepCo"), Record("Prog", "ProgCo") }, Array.Empty<RegistrationRootResult>());
        var after = new InstalledAppsSnapshot(DateTimeOffset.Now, new[] { Record("Keep", "KeepCo") }, Array.Empty<RegistrationRootResult>());
        string Reported(LeftoverBase place) => place switch { LeftoverBase.Local => local, LeftoverBase.Roaming => roaming, LeftoverBase.LocalLow => low, _ => programs };
        AppLeftoverResult result;
        try
        {
            result = AppLeftovers.Read(AppLeftovers.Removed(before, after, (_, _) => RecordKeyState.Gone), after, Reported, path => NativeFiles.MetadataDirectory.Open(path), CancellationToken.None);
            var paths = result.Items.Select(i => i.Folder.Path).ToArray();
            check(paths.SequenceEqual(new[] { foo, vendor, Path.Combine(programs, "Prog") }),
                "Native search lists the plain folders named as removed apps, also in the publisher's folder, but not a system-marked one or a still-recorded app's: " + string.Join(" | ", paths));
            var size = result.Items[0].Size!;
            check(size.Bytes == 320 && size.Files == 2 && !size.Partial, "Native directory records measure a leftover folder below its top level");
            check(result.Bases.All(b => b.State == LeftoverBaseState.Read && !b.Partial && !b.Unreadable), "Every native place is read in full");
        }
        finally { new DirectoryInfo(marked).Attributes &= ~FileAttributes.System; }
        bool released;
        try { Directory.Move(local, local + " moved"); Directory.Move(local + " moved", local); released = true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { released = false; }
        check(released, "Every search and measurement handle is released afterwards");

        string self = Path.GetFileName(Environment.ProcessPath ?? "");
        var running = AppLeftoverReader.ProgramPaths();
        string? mine = running?.FirstOrDefault(path => string.Equals(Path.GetFileName(path), self, StringComparison.OrdinalIgnoreCase));
        check(self.Length > 0 && mine is not null, "Running programs are listed by path, including this test");
        check(mine is not null && AppLeftovers.InUse(running, Path.GetDirectoryName(mine)!) && !AppLeftovers.InUse(running, foo), "A folder counts as in use only while a program runs from inside it");

        // The registry recheck: a subkey that cannot exist is never reported present, and a
        // subkey just listed is reported present in its own view.
        var unlikely = InstalledAppsReader.Recheck(RegistrationSource.User64, "Swivel-test-" + Guid.NewGuid().ToString("N"));
        check(unlikely is RecordKeyState.Gone or RecordKeyState.Unknown, "A subkey that does not exist is reported gone (or unknown when the view is absent): " + unlikely);
        check(InstalledAppsReader.Recheck(RegistrationSource.User64, @"bad\key") == RecordKeyState.Unknown, "An invalid subkey name is never opened");
        var listed = InstalledAppsReader.Read(CancellationToken.None).Records.FirstOrDefault(r => r.Key is not null && r.Issues == RegistrationEntryIssue.None);
        if (listed is not null) check(InstalledAppsReader.Recheck(listed.Source, listed.Key!) == RecordKeyState.Present, "A subkey just listed is reported present in its own view");

        foreach (var (refusal, why) in new (string, string)[] { (AppLeftovers.ProgramInside, "a program runs from it"), (AppLeftovers.RecordedAgain, "its app is recorded again") })
        {
            try { NativeFiles.RecycleLeftover(foo, () => throw new UserError(refusal)); check(false, "A leftover folder is never moved while " + why); }
            catch (UserError error) { check(error.Message == refusal, "A leftover folder is never moved while " + why + ", and the refusal says why"); }
        }
        check(System.IO.File.Exists(Path.Combine(foo, @"data\a.bin")), "The refused folder stays in place");

        var app = Application.Current as App ?? new App();
        int recordReads = 0;
        var dialog = new AppLeftoversDialog(app, result, () => Array.Empty<string>(), () => { recordReads++; return _ => { }; });
        var footer = ((DockPanel)dialog.Content).Children.OfType<WrapPanel>().Single().Children.OfType<Button>().ToArray();
        var move = footer.Single(b => Equals(b.Content, app.L("Move Selected to Recycle Bin…")));
        var body = (StackPanel)((ScrollViewer)((DockPanel)dialog.Content).Children.OfType<ScrollViewer>().Single()).Content;
        var list = body.Children.OfType<ListView>().Single();
        check(!move.IsEnabled && list.Items.Count == 3 && list.SelectedItems.Count == 0 && list.SelectionMode == SelectionMode.Extended && footer.Last().IsCancel,
            "Leftovers of Removed Apps lists the folders with nothing selected and Move disabled");
        check(footer.Any(b => Equals(b.Content, app.L("Back to Installation Records")) && b.IsEnabled) && recordReads == 0, "Back to Installation Records is offered, and the records are not read before a move");
        // The window's own check before each move: the records are read once for the batch and
        // each folder's app is checked; a refusal leaves the folder in place.
        var checkedFolders = new System.Collections.Generic.List<LeftoverFolder>();
        var checking = new AppLeftoversDialog(app, result, () => Array.Empty<string>(), () => { recordReads++; return checkedFolders.Add; });
        checking.Check(result.Items[0]); checking.Check(result.Items[1]);
        check(recordReads == 1 && checkedFolders.SequenceEqual(new[] { result.Items[0].Folder, result.Items[1].Folder }), "Before moves the records are read once for the batch and each folder's app is checked");
        checking.Close();
        var refusing = new AppLeftoversDialog(app, result, () => Array.Empty<string>(), () => _ => throw new UserError(AppLeftovers.RecordedAgain));
        try { NativeFiles.RecycleLeftover(foo, () => refusing.Check(result.Items[0])); check(false, "The window's check refuses a move when the app is recorded again"); }
        catch (UserError error) { check(error.Message == AppLeftovers.RecordedAgain && System.IO.File.Exists(Path.Combine(foo, @"data\a.bin")), "The window's check refuses a move when the app is recorded again, and the folder stays"); }
        refusing.Close();
        var busy = new AppLeftoversDialog(app, result, () => new[] { Path.Combine(foo, "foo.exe") }, () => _ => { });
        try { busy.Check(result.Items[0]); check(false, "The window's check refuses a folder a program runs from"); }
        catch (UserError error) { check(error.Message == AppLeftovers.ProgramInside, "The window's check refuses a folder a program runs from"); }
        busy.Close();
        check(!footer.Any(b => Equals(b.Content, app.L("Select All Listed"))), "There is no Select All: each leftover is chosen on purpose");
        check(list.Items.Cast<object>().Select(x => x.ToString()!).Any(x => x.Contains(@"Foo Inc\Foo App", StringComparison.Ordinal) && x.Contains(app.L(AppLeftovers.Place(LeftoverBase.Local)), StringComparison.Ordinal)),
            "Rows name the place and the publisher folder");
        list.SelectAll(); check(move.IsEnabled, "Selecting listed folders enables the move");
        check(dialog.CurrentReport.Contains(vendor) && dialog.CurrentReport.Contains(app.L(AppLeftovers.Notice)) && dialog.CurrentReport.Contains("Foo App") && dialog.CurrentReport.Contains(low),
            "The report lists the folders, the removed apps, the places and the limits");
        dialog.Close();
        var unknown = new AppLeftoversDialog(app, result, () => null, () => _ => { });
        var unknownBody = (StackPanel)((ScrollViewer)((DockPanel)unknown.Content).Children.OfType<ScrollViewer>().Single()).Content;
        check(unknownBody.Children.OfType<TextBlock>().Any(t => t.Text == app.L("Swivel could not list the running programs, so no folder can be moved now.")), "When programs cannot be listed the window says nothing can be moved");
        check(unknownBody.Children.OfType<ListView>().Single().Items.Cast<object>().All(x => x.ToString()!.EndsWith(app.L("Running programs could not be listed; it cannot be moved now"), StringComparison.Ordinal)
            || x.ToString()!.EndsWith(app.L("Not on a fixed drive; it cannot be moved"), StringComparison.Ordinal)), "Each row then says the running programs could not be listed");
        unknown.Close();
        var none = new AppLeftoversDialog(app, AppLeftovers.Read(RemovedApps.None, after, Reported, path => NativeFiles.MetadataDirectory.Open(path), CancellationToken.None), () => Array.Empty<string>(), () => _ => { });
        check(((StackPanel)((ScrollViewer)((DockPanel)none.Content).Children.OfType<ScrollViewer>().Single()).Content).Children.OfType<TextBlock>()
            .Any(t => t.Text == app.L("No app from the earlier list is confirmed removed from the installation records now. Go back to the installation records, uninstall an app with Open Installed Apps in Settings, wait for its uninstaller to finish, then choose Find Leftovers again.")),
            "With no removed app the window says to go back, uninstall and look again");
        none.Close();

        if (!RecycleBin.Available(foo)) { Console.WriteLine("SKIP: no Recycle Bin on the test drive."); return; }
        string stored = NativeFiles.RecycleLeftover(foo, () => { });
        check(!Directory.Exists(foo) && stored.Contains("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) && Directory.Exists(vendor), "The leftover folder moves to the Recycle Bin and the publisher's other folder stays");
        Directory.CreateDirectory(foo);
        reject(() => NativeFiles.RestoreRecycled(foo, stored, true), "Undo refuses when something new is in the folder's place");
        Directory.Delete(foo);
        NativeFiles.RestoreRecycled(foo, stored, true);
        check(new FileInfo(Path.Combine(foo, @"data\a.bin")).Length == 300, "Undo restores the leftover folder with its files");
    }
}
