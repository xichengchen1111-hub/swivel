using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Swivel.Core;

internal static class RenameChecks
{
    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        var paths = new[] { @"C:\Photos\猫.jpg", @"C:\Photos\犬.PNG" };
        var rules = new RenameRules(Base: "旅行", Prefix: "2026-", Suffix: " edit", Number: true, Start: 9, Digits: 3);
        var plan = rules.Build(paths);
        check(plan[0].Destination == @"C:\Photos\2026-旅行 edit-009.jpg" && plan[1].Destination.EndsWith("-010.PNG"), "Unicode names, numbering and extension case preserved");
        check(new RenameRules(Find: "猫", Replacement: "$1").Build(paths)[0].Destination == @"C:\Photos\$1.jpg", "Literal replacement, no regex evaluation");
        check(!new RenameRules().Build(paths).Any(x => x.Changed), "Empty rules never change original names");
        check(new RenameRules(Suffix: " copy").Build(new[] { @"C:\a\.gitignore", @"C:\a\README" }).Select(x => x.Destination).SequenceEqual(new[] { @"C:\a\.gitignore copy", @"C:\a\README copy" }), "Dot files and extensionless names");
        foreach (var invalid in new[] { new RenameRules(Base: "CON"), new RenameRules(Prefix: "../"), new RenameRules(Number: true, Start: 99999999), new RenameRules(Digits: 0), new RenameRules(Base: new string('a',121)) })
            reject(() => invalid.Build(paths), "Reject invalid rename rules");
        reject(() => new RenameRules(Base: "same").Build(new[] { @"C:\x\a.txt", @"C:\x\b.txt" }), "Duplicate output rejected");
        check(new RenameRules(Find:"cat", Replacement:"CAT").Build(new[] { @"C:\x\cat.txt" })[0].Changed, "Case-only rename preview accepted");
        RenameRules.Validate(new[] { new RenamePair(@"C:\x\a.txt", @"C:\x\b.txt"), new RenamePair(@"C:\x\b.txt", @"C:\x\a.txt") });
        check(true, "Swaps accepted with held-source staging");
        reject(() => RenameRules.Validate(new[] { new RenamePair(@"C:\x\a.txt", @"D:\x\a.txt") }), "Cross directory moves never accepted");
        var prefs = new Prefs(); check(!prefs.IsOn(Feature.BatchRename), "Batch Rename starts off");
        prefs.Set(Feature.BatchRename, true); prefs.Set(Feature.Wheel, false);
        check(Catalog.Available(prefs, new[] { "file.txt" }).Any(x => x.Id == "rename.files"), "Rename independent of Wheel and supports non-media");
        check(Prefs.Parse(prefs.Serialize()).IsOn(Feature.BatchRename), "Rename setting persists");
        var items = new[] { @"C:\x\a.txt", @"C:\x\b.txt", @"C:\x\c.txt" };
        var proposed = new RenameRules(Prefix:"new-").Build(items);
        var world = new HashSet<string>(items, StringComparer.OrdinalIgnoreCase);
        var fake = items.Select((p,i)=>new Fake(p,world,i)).ToArray();
        var ok = RenameTransaction.Execute(fake,proposed,CancellationToken.None);
        check(ok.Error is null && ok.Remaining.Count == 3 && world.SetEquals(proposed.Select(x=>x.Destination)), "Successful batch captures all current names");
        var undoPlan = ok.Locations.Select(x=>new RenamePair(x.Current,x.Original)).ToArray();
        var undone = RenameTransaction.Execute(fake,undoPlan,CancellationToken.None);
        check(undone.Error is null && world.SetEquals(items), "Undo transaction restores originals");
        fake[1].Fail = destination => destination == proposed[1].Destination;
        var failed = RenameTransaction.Execute(fake,proposed,CancellationToken.None);
        check(failed.Error is not null && failed.Remaining.Count == 0 && world.SetEquals(items), "Second item failure rolls first back");
        fake[0].Fail = destination => destination == items[0];
        var partial = RenameTransaction.Execute(fake,proposed,CancellationToken.None);
        check(partial.Remaining.Count == 1 && partial.Remaining[0].Current == proposed[0].Destination && partial.Remaining[0].Original == items[0], "Failed rollback keeps exact recovery path and stamp");
        check(world.Contains(items[1]) && world.Contains(items[2]) && world.Contains(proposed[0].Destination), "Partial rollback never deletes a source");
        fake[0].Fail = null; fake[0].MoveTo(items[0]); fake[1].Fail = null;
        using var cancelled = new CancellationTokenSource(); fake[0].AfterMove = () => cancelled.Cancel();
        var cancel = RenameTransaction.Execute(fake,proposed,cancelled.Token);
        check(cancel.Cancelled && cancel.Remaining.Count == 0 && world.SetEquals(items), "Cancellation rolls back using uncancelled cleanup");
        // A process inserts an unrelated object into an old name before rollback.
        using var cancel2 = new CancellationTokenSource(); fake[0].AfterMove = () => { world.Add(items[0]); cancel2.Cancel(); };
        var occupied = RenameTransaction.Execute(fake,proposed,cancel2.Token);
        check(occupied.Remaining.Count == 1 && world.Contains(items[0]) && world.Contains(proposed[0].Destination), "Occupied rollback name preserved alongside renamed original");
    }
    private sealed class Fake : IRenameLease
    {
        private readonly HashSet<string> world;
        public string CurrentPath { get; private set; }
        public FileStamp Stamp { get; }
        public Func<string,bool>? Fail;
        public Action? AfterMove;
        public Fake(string path, HashSet<string> world, int i) { CurrentPath=path;this.world=world;Stamp=new(1,(ulong)i+1,20,30,40); }
        public void MoveTo(string destination)
        {
            if (Fail?.Invoke(destination)==true || world.Contains(destination)) throw new IOException("Injected move failure or occupied path");
            if (!world.Remove(CurrentPath)) throw new IOException("Missing fake source");
            world.Add(destination); CurrentPath=destination; AfterMove?.Invoke();
        }
    }
}
