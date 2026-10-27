using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Swivel.Core;
using static AppCacheChecks;

internal static class AppLeftoverChecks
{
    private const string AppData = @"C:\Users\me\AppData", Local = AppData + @"\Local", Roaming = AppData + @"\Roaming", LocalLow = AppData + @"\LocalLow", Programs = Local + @"\Programs";

    internal static void Run(Action<bool,string> check, Action<Action,string> reject)
    {
        // Apps gone from every record now, by name ignoring case.
        var before = Snapshot(
            Record("Foo App", "Foo Inc"), Record("Bar", "BarSoft"), Record("Zoom", "Zoom Video"), Record("KEEP", "KeepCo"), Record("Microsoft", "Someone"),
            Record("Bad:Name", "X"), Record("Gone Vendor Thing", "Microsoft"), Record("Dup", "DupCo"), Record("Dup", "dupco", RegistrationSource.Machine64),
            Record("Linked", null), Record("KeepCo Tool", "KeepCo"), Record("Name As File", null), Record("Prog", null),
            new InstalledAppRecord(RegistrationSource.User64, 99, null, RegistrationText.Unknown(RegistrationValueState.Missing), Text("1"), Text("Nobody"), RegistrationEntryIssue.None));
        var after = Snapshot(Record("Keep", "KeepCo"), Record("Zoom Workplace", "Zoom Video"));
        var asked = new List<RecordKey>();
        RecordKeyState Gone(RegistrationSource source, string key) { asked.Add(new(source, key)); return RecordKeyState.Gone; }
        var removed = AppLeftovers.Removed(before, after, Gone);
        check(removed.Apps.Select(a => a.Name).SequenceEqual(new[] { "Bad:Name", "Bar", "Dup", "Foo App", "Gone Vendor Thing", "KeepCo Tool", "Linked", "Microsoft", "Name As File", "Prog", "Zoom" }) && removed.Unconfirmed.Count == 0,
            "Removed apps are the reported names gone from every record now, compared ignoring case, in name order: " + string.Join(" | ", removed.Apps.Select(a => a.Name)));
        check(removed.Apps.Single(a => a.Name == "Dup").Publishers.SequenceEqual(new[] { "DupCo" }) && removed.Apps.Single(a => a.Name == "Prog").Publishers.Count == 0,
            "One app per name, with each reported publisher once");
        check(removed.Apps.Single(a => a.Name == "Dup").Keys.SequenceEqual(new RecordKey[] { new(RegistrationSource.User64, "Dup key"), new(RegistrationSource.Machine64, "Dup key") })
            && asked.Count == 12 && !asked.Any(k => k.Key.StartsWith("KEEP ", StringComparison.Ordinal)),
            "Each earlier subkey of a missing name, and only of one, is opened again in its own view: " + asked.Count);
        reject(() => AppLeftovers.Removed(before, Snapshot(Record("Keep", "KeepCo"), new InstalledAppRecord(RegistrationSource.User32, 1, null, RegistrationText.Unknown(RegistrationValueState.TimeLimit), Text("1"), Text("P"), RegistrationEntryIssue.TimeLimit)), Gone),
            "Partial records now refuse the comparison, since a missing name may not have been read");
        try { AppLeftovers.Removed(before, Snapshot(new InstalledAppRecord(RegistrationSource.User32, 1, null, Text("A"), Text("1"), Text("P"), RegistrationEntryIssue.Unavailable)), Gone); check(false, "Partial refusal is a UserError"); }
        catch (UserError error) { check(error.Message == AppLeftovers.NotComplete, "Partial refusal explains why nothing was searched"); }
        // A version that cannot be read is never compared, so it does not stop the search.
        var badVersion = new InstalledAppRecord(RegistrationSource.User64, 1, "Keep key", Text("Keep"), RegistrationText.Unknown(RegistrationValueState.Invalid), Text("KeepCo"), RegistrationEntryIssue.None);
        check(Snapshot(badVersion).Partial && AppLeftovers.Comparable(Snapshot(badVersion)) && AppLeftovers.Removed(before, Snapshot(badVersion, Record("Zoom Workplace", "Zoom Video")), Gone).Apps.Count == 11,
            "Records with only an unreadable version are partial for the records window but still comparable here");
        foreach (var (snapshot, why) in new (InstalledAppsSnapshot, string)[]
        {
            (new(DateTimeOffset.Now, Array.Empty<InstalledAppRecord>(), new[] { new RegistrationRootResult(RegistrationSource.User64, 1024, RegistrationRootIssue.Limit) }), "a view stopped at its limit"),
            (new(DateTimeOffset.Now, Array.Empty<InstalledAppRecord>(), new[] { new RegistrationRootResult(RegistrationSource.Machine32, 0, RegistrationRootIssue.Unavailable) }), "a view that could not be opened"),
            (Snapshot(new InstalledAppRecord(RegistrationSource.User64, 1, "P key", Text("P"), Text("1"), RegistrationText.Unknown(RegistrationValueState.Invalid), RegistrationEntryIssue.None)), "a publisher that could not be read"),
            (Snapshot(new InstalledAppRecord(RegistrationSource.User64, 1, "N key", RegistrationText.Unknown(RegistrationValueState.TooLarge), Text("1"), Text("P"), RegistrationEntryIssue.None)), "a name that could not be read"),
            (Snapshot(new InstalledAppRecord(RegistrationSource.User64, 1, "K key", Text("K"), Text("1"), Text("P"), RegistrationEntryIssue.KeyInvalid)), "an entry whose subkey name could not be used"),
        })
            check(!AppLeftovers.Comparable(snapshot), "Records are not comparable with " + why);
        check(AppLeftovers.Comparable(new(DateTimeOffset.Now, Array.Empty<InstalledAppRecord>(), new[] { new RegistrationRootResult(RegistrationSource.User32, 0, RegistrationRootIssue.Missing) })),
            "A view that does not exist leaves the records comparable");
        check(AppLeftovers.Removed(before, before, Gone).Apps.Count == 0, "Nothing is removed when the records did not change");
        // A name missing now whose own subkey is still there (renamed, or skipped while an
        // uninstaller changed the list), cannot be checked, or was never known, is not removed.
        var uncertain = AppLeftovers.Removed(Snapshot(Record("Renamed", null), Record("Unchecked", null), Record("Keyless", null, key: ""), Record("Half Gone", null), Record("Half Gone", null, RegistrationSource.Machine32, "Other key"), Record("Really Gone", null)),
            Snapshot(), (source, key) => key switch { "Renamed key" => RecordKeyState.Present, "Unchecked key" or "Other key" => RecordKeyState.Unknown, _ => RecordKeyState.Gone });
        check(uncertain.Apps.Select(a => a.Name).SequenceEqual(new[] { "Really Gone" }) && uncertain.Unconfirmed.Select(a => a.Name).SequenceEqual(new[] { "Half Gone", "Keyless", "Renamed", "Unchecked" }),
            "Only an app every one of whose earlier subkeys is gone counts as removed; the others are reported as unconfirmed: " + string.Join(" | ", uncertain.Unconfirmed.Select(a => a.Name)));
        var unconfirmedRead = AppLeftovers.Read(uncertain, Snapshot(), Reported, Tree(local: D("Local", D("Renamed"), D("Really Gone"))).Open, CancellationToken.None);
        check(unconfirmedRead.Items.Select(i => i.Folder.Name).SequenceEqual(new[] { "Really Gone" }) && unconfirmedRead.Unconfirmed.Count == 4, "Unconfirmed apps are not looked for and stay in the result");
        // An unconfirmed app may still be installed: its earlier name and publishers count as recorded.
        var stillThere = AppLeftovers.Removed(Snapshot(Record("Renamed Pro", "RenCo"), Record("Renamed", null), Record("RenCo", null), Record("Pro Tools", null)), Snapshot(),
            (_, key) => key == "Renamed Pro key" ? RecordKeyState.Present : RecordKeyState.Gone);
        var pro = AppLeftovers.Read(stillThere, Snapshot(), Reported, Tree(local: D("Local", D("Renamed"), D("RenCo"), D("Pro Tools"))).Open, CancellationToken.None);
        check(pro.Items.Select(i => i.Folder.Name).SequenceEqual(new[] { "Pro Tools" }) && pro.Skipped.SequenceEqual(new[] { "RenCo", "Renamed" }),
            "Names an unconfirmed app or its publisher use or extend are not looked for: " + string.Join(" | ", pro.Items.Select(i => i.Folder.Name)) + " / " + string.Join(" | ", pro.Skipped));

        var tree = Tree();
        var result = AppLeftovers.Read(removed, after, Reported, tree.Open, CancellationToken.None);
        var paths = result.Items.Select(i => i.Folder.Path).ToArray();
        check(paths.SequenceEqual(new[] { Local + @"\bar", Local + @"\Foo App", Local + @"\Foo Inc\Foo App", Local + @"\KeepCo\KeepCo Tool", Roaming + @"\Bar", Roaming + @"\Foo App", LocalLow + @"\Dup", Programs + @"\Prog" }),
            "Folders named exactly as removed apps, directly or in their publisher's folder, by place then name, keeping the folder's own spelling: " + string.Join(" | ", paths));
        check(!paths.Any(p => p.Contains("Zoom") || p.Contains("Microsoft") || p.Contains("Linked") || p.Contains("Name As File") || p.EndsWith(@"\Keep") || p.EndsWith(@"\KeepCo") || p.EndsWith(@"\Foo Inc")),
            "Names still recorded or begun by a recorded name, shared folders and their contents, links, files and publisher folders themselves are never listed");
        check(result.Skipped.SequenceEqual(new[] { "Bad:Name", "Microsoft", "Zoom" }), "Names that are shared folders, extended by a recorded name or invalid are reported as not looked for: " + string.Join(" | ", result.Skipped));
        var publisher = result.Items.Single(i => i.Folder.Path == Local + @"\Foo Inc\Foo App").Folder;
        check(publisher.Publisher == "Foo Inc" && publisher.Name == "Foo App" && publisher.Base == LeftoverBase.Local && result.Items[0].Folder.Publisher is null && result.Items[0].Folder.Name == "bar",
            "Each folder keeps its place, its publisher folder and its own name");
        var foo = result.Items.Single(i => i.Folder.Path == Local + @"\Foo App").Size!;
        check(foo.Bytes == 10 && foo.Files == 1 && !foo.Partial && foo.Issue is null, "Each folder is measured with directory records only");
        var denied = result.Items.Single(i => i.Folder.Path == Roaming + @"\Bar").Size!;
        check(denied.Issue == AppCaches.Unreadable && denied.Partial, "A folder that cannot be measured stays listed with its reason");
        check(result.Bases.Select(b => b.State).SequenceEqual(new[] { LeftoverBaseState.Read, LeftoverBaseState.Read, LeftoverBaseState.Read, LeftoverBaseState.Read })
            && result.Bases.Select(b => b.Path).SequenceEqual(new[] { Local, Roaming, LocalLow, Programs }) && result.Bases.All(b => !b.Partial && !b.Unreadable),
            "Every place is reported with its path and how it was read");
        check(!result.FolderLimit && !result.DiscoveryLimit && !result.MeasureEntryLimit && !result.MeasureTimeLimit, "No limit reached on the small fixture");
        check(tree.Opened > 0 && tree.Opened == tree.Disposed, "Every opened folder is released: " + tree.Opened + "/" + tree.Disposed);

        var noLow = AppLeftovers.Read(removed, after, b => b == LeftoverBase.LocalLow ? AppData + @"\Missing" : b == LeftoverBase.Programs ? null : Reported(b), Tree().Open, CancellationToken.None);
        var low = noLow.Bases.Single(b => b.Base == LeftoverBase.LocalLow); var programs = noLow.Bases.Single(b => b.Base == LeftoverBase.Programs);
        check(low.State == LeftoverBaseState.NotFound && programs.State == LeftoverBaseState.Unavailable && programs.Issue == ReportedFolderPath.Unreported && noLow.Items.Count == 6,
            "A missing place is reported as not found and an unreported one as unavailable; only those places' folders are left out");
        var unsafeBase = AppLeftovers.Read(removed, after, b => b == LeftoverBase.Local ? @"\\server\share\Local" : Reported(b), Tree().Open, CancellationToken.None);
        check(unsafeBase.Bases[0].State == LeftoverBaseState.Unavailable && unsafeBase.Bases[0].Issue is not null && unsafeBase.Items.All(i => i.Folder.Base != LeftoverBase.Local),
            "A network or unsafe app-data path is never opened");
        var linkBase = AppLeftovers.Read(removed, after, Reported, LinkedRoaming().Open, CancellationToken.None);
        check(linkBase.Bases[1].State == LeftoverBaseState.Unavailable && linkBase.Bases[1].Issue == "Choose a local folder without links, system attributes or cloud placeholders.",
            "A place Windows reports as a link stays visible with its reason and is not searched");
        var lockedPublisher = AppLeftovers.Read(removed, after, Reported, LockedPublisher().Open, CancellationToken.None);
        check(lockedPublisher.Bases[0].Unreadable && !lockedPublisher.Items.Any(i => i.Folder.Publisher == "Foo Inc") && lockedPublisher.Items.Any(i => i.Folder.Path == Local + @"\Foo App"),
            "A publisher folder that cannot be read marks its place as partly unreadable and leaves the rest listed");
        var oneFolder = AppLeftovers.Read(removed, after, Reported, Tree().Open, CancellationToken.None, new(Folders: 1));
        check(oneFolder.FolderLimit && oneFolder.Items.Count == 1 && oneFolder.Bases[0].Partial, "The folder limit stops the search and marks its place partial");
        var shortSearch = AppLeftovers.Read(removed, after, Reported, Tree().Open, CancellationToken.None, new(DiscoveryEntries: 3));
        check(shortSearch.DiscoveryLimit && shortSearch.Bases[0].Partial, "The total entry limit stops the search and is reported");
        var fewEntries = AppLeftovers.Read(removed, after, Reported, Tree().Open, CancellationToken.None, new(DirectoryEntries: 1));
        check(fewEntries.Bases.Take(2).All(b => b.Partial) && !fewEntries.DiscoveryLimit, "The per-folder entry limit marks a place partial");
        var budget = AppLeftovers.Read(removed, after, Reported, Tree().Open, CancellationToken.None, new(MeasureEntries: 1));
        check(budget.MeasureEntryLimit && budget.Items.Any(i => i.Size is null), "The shared measuring budget leaves later folders unmeasured");
        var late = AppLeftovers.Read(removed, after, Reported, Tree().Open, CancellationToken.None, elapsed: () => TimeSpan.FromSeconds(31));
        check(late.MeasureTimeLimit && late.Items.All(i => i.Size is null) && late.Items.Count == 8, "Past the time limit folders stay listed without sizes");
        foreach (var limits in new AppLeftoverLimits[] { new(DirectoryEntries: 0), new(DirectoryEntries: 4097), new(DiscoveryEntries: 20001), new(Folders: 257), new(MeasureEntries: 0), new(MeasureSeconds: 31) })
            reject(() => AppLeftovers.Read(removed, after, Reported, Tree().Open, CancellationToken.None, limits), "Leftover limits stay within their bounds");
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel(); var cancelled = Tree();
            reject(() => AppLeftovers.Read(removed, after, Reported, cancelled.Open, cancel.Token), "A cancelled leftover search stops");
            check(cancelled.Opened == cancelled.Disposed, "A cancelled search releases what it opened");
        }
        // Cancelled from inside: while the places are listed (second open) and while measuring.
        foreach (int at in new[] { 2, 6 })
            using (var cancel = new CancellationTokenSource())
            {
                var inner = Tree(); int opens = 0; bool stopped = false;
                try { AppLeftovers.Read(removed, after, Reported, path => { if (++opens == at) cancel.Cancel(); return inner.Open(path); }, cancel.Token); }
                catch (OperationCanceledException) { stopped = true; }
                check(stopped && inner.Opened >= at && inner.Opened == inner.Disposed, "Cancelling during the search (open " + at + ") stops it and releases every folder and child folder it opened: " + inner.Opened + "/" + inner.Disposed);
            }
        check(AppLeftovers.Read(RemovedApps.None, after, Reported, Tree().Open, CancellationToken.None).Items.Count == 0, "With no removed app nothing is listed");

        // "Zap2" is another app, not an edition of "Zap"; a publisher folder named as an app,
        // removed or still recorded, is never searched.
        var small = Snapshot(Record("Zap2", null), Record("Keep", "KeepCo"));
        var edges = AppLeftovers.Read(AppLeftovers.Removed(Snapshot(Record("Zap", null), Record("Same", "Same"), Record("Half", "Keep"), Record("Zap2", null), Record("Keep", "KeepCo")), small, Gone), small, Reported,
            Tree(local: D("Local", D("Zap"), D("Same", D("Same")), D("Keep", D("Half")))).Open, CancellationToken.None);
        check(edges.Items.Select(i => i.Folder.Path).SequenceEqual(new[] { Local + @"\Same", Local + @"\Zap" }) && edges.Skipped.Count == 0,
            "A recorded name extends a removed one only at a word break, and publisher folders named as apps are not searched: " + string.Join(" | ", edges.Items.Select(i => i.Folder.Path)));

        // Just before a move.
        var bar = removed.Apps.Single(a => a.Name == "Bar");
        var barFolder = new LeftoverFolder(bar, LeftoverBase.Local, null, "bar", Local + @"\bar");
        var barInVendor = new LeftoverFolder(bar, LeftoverBase.Local, "BarSoft", "Bar", Local + @"\BarSoft\Bar");
        AppLeftovers.StillRemoved(barFolder, after, (_, _) => RecordKeyState.Gone);
        AppLeftovers.StillRemoved(barInVendor, Snapshot(Record("Bar2", null), Record("Other", "BarSoft")), (_, _) => RecordKeyState.Gone);
        AppLeftovers.StillRemoved(barFolder, Snapshot(badVersion), (_, _) => RecordKeyState.Gone);
        check(true, "An app still missing with its subkeys gone may be moved, also while another app's name begins with the same letters, its publisher folder belongs to a recorded publisher, or a version cannot be read");
        var limited = new InstalledAppsSnapshot(DateTimeOffset.Now, Array.Empty<InstalledAppRecord>(), new[] { new RegistrationRootResult(RegistrationSource.Machine64, 1024, RegistrationRootIssue.Limit) });
        foreach (var (folder, now, state, why) in new (LeftoverFolder, InstalledAppsSnapshot, RecordKeyState, string)[]
        {
            (barFolder, Snapshot(Record("bar", "Other")), RecordKeyState.Gone, "recorded again"),
            (barFolder, Snapshot(Record("Bar (x64)", null)), RecordKeyState.Gone, "recorded again as an edition"),
            (barFolder, Snapshot(Record("Tool", "Bar")), RecordKeyState.Gone, "now a recorded publisher's name"),
            (barFolder, Snapshot(Record("Tool", "Bar Labs")), RecordKeyState.Gone, "begun by a recorded publisher's name"),
            (barInVendor, Snapshot(Record("BarSoft", null)), RecordKeyState.Gone, "in a publisher folder named as a recorded app"),
            (barFolder, Snapshot(new InstalledAppRecord(RegistrationSource.User32, 1, null, Text("A"), Text("1"), Text("P"), RegistrationEntryIssue.Unavailable)), RecordKeyState.Gone, "in records that are partial"),
            (barFolder, limited, RecordKeyState.Gone, "in records stopped at a limit"),
            (barFolder, after, RecordKeyState.Present, "back under its old subkey"), (barFolder, after, RecordKeyState.Unknown, "under a subkey that cannot be checked"),
        })
        {
            try { AppLeftovers.StillRemoved(folder, now, (_, _) => state); check(false, "A move is refused when its app is " + why); }
            catch (UserError error) { check(error.Message == AppLeftovers.RecordedAgain, "A move is refused when its app is " + why); }
        }

        // Running programs inside a folder, by reported program path.
        var processes = new Processes(new()
        {
            [1] = Local + @"\Foo App\foo.exe", [2] = Local + @"\FOO APP\bin\helper.exe", [3] = Local + @"\Foo Apps\other.exe", [4] = null, [6] = @"C:\Windows\explorer.exe",
        });
        var running = AppLeftovers.ProgramPaths(processes, CancellationToken.None)!;
        check(running.Count == 4 && processes.Opened == processes.Disposed, "Program paths skip processes that cannot be opened or report no path, and release each process");
        check(AppLeftovers.Inside(running, Local + @"\Foo App").Count == 2 && AppLeftovers.Inside(running, Local + @"\Foo App\").Count == 2 && AppLeftovers.Inside(running, Local + @"\Bar").Count == 0,
            "A program is inside a folder only below it (any case), not in a folder with a longer name");
        check(AppLeftovers.ProgramPaths(new Processes(new()) { Unavailable = true }, CancellationToken.None) is null && AppLeftovers.ProgramPaths(new Processes(new()) { Limited = true }, CancellationToken.None) is null,
            "When the running processes cannot all be listed there is no answer, so nothing is moved");
        reject(() => AppLeftovers.Inside(running, ""), "A folder is needed to look inside");
        check(AppLeftovers.InUse(null, Local + @"\Bar") && AppLeftovers.InUse(running, Local + @"\Foo App") && !AppLeftovers.InUse(running, Local + @"\Bar"),
            "A folder counts as in use while a program runs inside it, and always when the programs could not be listed");

        check(Enum.GetValues<LeftoverBase>().Select(AppLeftovers.Place).Distinct().Count() == 4, "Each place has its own name");
        foreach (string text in new[] { AppLeftovers.Notice, AppLeftovers.Limits, AppLeftovers.NotComplete, AppLeftovers.RecordedAgain, AppLeftovers.NotRecycled, AppLeftovers.Unreadable, AppLeftovers.SomeUnreadable, AppLeftovers.ProgramInside, InstalledApps.Uninstall }
            .Concat(Enum.GetValues<LeftoverBase>().Select(AppLeftovers.Place)))
            check(Strings.Table.ContainsKey(text), "Leftover text translated: " + text[..Math.Min(40, text.Length)]);
    }

    private static string? Reported(LeftoverBase root) => root switch
    {
        LeftoverBase.Local => Local, LeftoverBase.Roaming => Roaming + @"\", LeftoverBase.LocalLow => LocalLow, _ => Programs,
    };
    private static RegistrationText Text(string value) => new(value, RegistrationValueState.Present);
    // Each record's own subkey is "<name> key" unless given; an empty key stands for none.
    private static InstalledAppRecord Record(string name, string? publisher, RegistrationSource source = RegistrationSource.User64, string? key = null)
        => new(source, 0, key == "" ? null : key ?? name + " key", Text(name), Text("1.0"), publisher is null ? RegistrationText.Unknown(RegistrationValueState.Missing) : Text(publisher), RegistrationEntryIssue.None);
    private static InstalledAppsSnapshot Snapshot(params InstalledAppRecord[] records) => new(DateTimeOffset.Now, records, Array.Empty<RegistrationRootResult>());

    private static Fake Tree(Fake? roaming = null, Fake? local = null)
    {
        const uint Dir = 0x10, Link = 0x10 | 0x400;
        local ??= D("Local",
            D("Foo App", F("x", 10)), D("Foo Inc", D("Foo App", F("y", 5)), D("Unrelated")), D("bar"), D("Zoom", F("z", 1)), D("Microsoft", D("Gone Vendor Thing")),
            N("Linked", Link), F("Name As File", 3), D("KeepCo", D("KeepCo Tool", F("t", 1)), D("Other")), D("Keep"), D("Programs", D("Prog", F("p", 7))));
        roaming ??= D("Roaming", D("Foo App", F("r", 3)), N("Bar", Dir, denied: true));
        return new(D("AppData", local, roaming, D("LocalLow", D("Dup", F("d", 2)))));
    }
    private static Fake LinkedRoaming() => Tree(roaming: N("Roaming", 0x10 | 0x400));
    private static Fake LockedPublisher() => Tree(local: D("Local", D("Foo App", F("x", 10)), N("Foo Inc", 0x10, denied: true), D("Programs")));

    private sealed class Processes(Dictionary<uint, string?> paths) : IAppMemorySource
    {
        public bool Unavailable, Limited;
        public int Opened, Disposed;
        public AppMemoryIds Enumerate() => new(paths.Keys.Append(5u).ToArray(), Limited, Unavailable);
        public IAppMemoryProcess? Open(uint pid)
        {
            if (!paths.TryGetValue(pid, out string? path)) return null; // 5: cannot be opened
            Opened++; return new Process(this, path);
        }
        private sealed class Process(Processes owner, string? path) : IAppMemoryProcess
        {
            public ulong? CreationTime() => null;
            public string? ImagePath() => path;
            public ulong? WorkingSet() => null;
            public void Dispose() => owner.Disposed++;
        }
    }
}
