using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Swivel.Core;

internal static class MoveFilesChecks
{
    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        check(MoveRules.Destination(@"C:\one\noextension", @"C:\two") == @"C:\two\noextension", "Move retains extensionless names");
        check(MoveRules.Destination(@"C:\one\.profile", @"C:\") == @"C:\.profile", "Move retains dotfiles into a drive root");
        check(MoveRules.Destination(@"C:\one\a.a-b", @"C:\two\") == @"C:\two\a.a-b", "Move retains non-alphanumeric extensions");
        reject(() => MoveRules.Validate(Array.Empty<MovePair>()), "Empty move plan refused");
        reject(() => MoveRules.Validate(Enumerable.Range(0, 101).Select(i => new MovePair(@"C:\one\" + i, @"C:\two\" + i)).ToArray()), "101 move files refused");
        reject(() => MoveRules.Validate(new[] { new MovePair(@"C:\one\a", @"C:\two\a"), new MovePair(@"C:\three\A", @"C:\two\A") }), "Casefold target basename collision refused as a whole");
        reject(() => MoveRules.Validate(new[] { new MovePair(@"C:\one\a", @"C:\two\a"), new MovePair(@"C:\one\A", @"C:\three\A") }), "Casefold source aliases are refused, never silently deduplicated");
        reject(() => MoveRules.Validate(new[] { new MovePair(@"C:\one\a", @"C:\two\b") }), "Move never renames the original leaf");
        foreach (string path in new[] { @"\\server\share", @"C:\a\\", @"C:\a\..\b", @"C:\a:stream", @"C:\NUL", "C:\\a\u200b" })
            reject(() => MoveRules.DirectoryPath(path), "Unsafe move directory refused");

        var simple = World.Create(3); var original = simple.Files.Values.Select(f => f.Stamp).ToArray();
        var moved = simple.Run(@"C:\two");
        check(moved.Error is null && !moved.Cancelled && moved.Remaining.Count == 3 && moved.Locations.All(x => x.Current.StartsWith(@"C:\two\", StringComparison.Ordinal)), "Whole move returns every exact destination");
        check(moved.Locations.Select(x => x.BeforeStamp).SequenceEqual(original.Cast<MoveFileStamp?>()) && moved.Locations.All(x => x.Stamp!.Value.ChangeTime > x.BeforeStamp!.Value.ChangeTime), "Move records verified before and after stamps");
        check(simple.Log.Take(6).SequenceEqual(new[] { "validate", "validate", "validate", "preflight", "preflight", "preflight" }), "All sources and targets preflight before first rename");
        reject(() => ((IList<MoveLocation>)moved.Locations).Clear(), "Outcome locations immutable");
        var undone = simple.Undo(moved.Remaining); check(undone.Error is null && simple.Files.Keys.All(p => p.StartsWith(@"C:\one\", StringComparison.Ordinal)) && MoveReceipts.AfterUndo(moved.Remaining, undone).Count == 0, "Undo restores every original and clears receipt");
        check(undone.Locations.All(x => x.Original.StartsWith(@"C:\two\", StringComparison.Ordinal)), "Undo result original is its own operation start");

        for (int count = 1; count <= 8; count++) for (int failure = 0; failure < count; failure++)
        {
            foreach (bool cancel in new[] { false, true })
            {
                var world = World.Create(count); using var cts = new CancellationTokenSource(); int forward = 0;
                world.BeforeMove = (_, _, restoring) => { if (!restoring && forward++ == failure) { if (cancel) cts.Cancel(); throw cancel ? new OperationCanceledException(cts.Token) : new IOException("injected forward failure"); } };
                var result = world.Run(@"C:\two", cts.Token);
                check(result.Error is not null && result.Cancelled == cancel && result.Remaining.Count == 0 && world.Files.Keys.All(p => p.StartsWith(@"C:\one\", StringComparison.Ordinal)), "Failure/cancellation at every forward position restores originals");
                var restores = world.Log.Where(x => x.StartsWith("restore:", StringComparison.Ordinal)).ToArray();
                check(restores.SequenceEqual(Enumerable.Range(0, failure).Reverse().Select(i => "restore:" + i + ".dat")), "Rollback always runs in reverse despite cancellation");
            }
        }
        var pre = World.Create(3); using (var cancelled = new CancellationTokenSource())
        { cancelled.Cancel(); var result = pre.Run(@"C:\two", cancelled.Token); check(result.Cancelled && result.Remaining.Count == 0 && pre.Log.Count == 0, "Pre-cancel moves and checks nothing"); }
        var lastCancel = World.Create(3); using (var cts = new CancellationTokenSource())
        { int movedCount = 0; lastCancel.AfterMove = (_, _, restoring) => { if (!restoring && ++movedCount == 3) cts.Cancel(); }; var result = lastCancel.Run(@"C:\two", cts.Token); check(result.Cancelled && result.Remaining.Count == 0, "Cancellation during final native rename also rolls back"); }

        var lateRace = World.Create(3); lateRace.BeforeMove = (lease, target, restore) => { if (!restore && target.EndsWith("1.dat", StringComparison.Ordinal)) lateRace.Add(target, 999); };
        var raced = lateRace.Run(@"C:\two"); check(raced.Error is not null && raced.Remaining.Count == 0 && lateRace.Files[@"C:\two\1.dat"].Stamp.Identity.Low == 999, "Atomic destination race preserves unrelated target and restores prior move");
        var occupied = World.Create(3); occupied.Add(@"C:\two\2.dat", 999); var blocked = occupied.Run(@"C:\two");
        check(blocked.Error is not null && blocked.Remaining.Count == 0 && !occupied.Log.Any(x => x.StartsWith("move:", StringComparison.Ordinal)), "Last preflight occupied target prevents all earlier moves");
        var noop = World.Create(2); var noChange = noop.Run(@"C:\one"); check(noChange.Error is null && noChange.Remaining.Count == 0 && !noop.Log.Any(x => x.StartsWith("move:", StringComparison.Ordinal)), "Noop rows are retained without mutation");

        var incomplete = World.Create(3); incomplete.BeforeMove = (_, target, restoring) => {
            if ((!restoring && target.EndsWith("2.dat", StringComparison.Ordinal)) || (restoring && target.EndsWith("1.dat", StringComparison.Ordinal))) throw new IOException("injected failure");
        };
        var partial = incomplete.Run(@"C:\two");
        check(partial.Error is AggregateException && partial.Remaining.Count == 1 && partial.Remaining[0].Current == @"C:\two\1.dat" && incomplete.Files.ContainsKey(@"C:\one\0.dat"), "Rollback failure retains precise remaining receipt and still restores earlier files");
        incomplete.BeforeMove = null; var retry = incomplete.Undo(partial.Remaining); check(retry.Error is null && MoveReceipts.AfterUndo(partial.Remaining, retry).Count == 0, "Remaining failed-rollback receipt can be undone later");

        var stampFailure = World.Create(2); stampFailure.AfterMove = (_, _, restoring) => { if (!restoring) throw new IOException("post-rename stamp failure"); };
        var recovered = stampFailure.Run(@"C:\two"); check(recovered.Error is not null && recovered.Remaining.Count == 0, "Post-rename refresh failure still restores the held object");
        stampFailure = World.Create(2); stampFailure.AfterMove = (_, _, _) => throw new IOException("stamp failure"); stampFailure.BeforeMove = (_, _, restoring) => { if (restoring) throw new IOException("rollback failure"); };
        var unknown = stampFailure.Run(@"C:\two"); check(unknown.Remaining.Count == 1 && unknown.Remaining[0].Current == @"C:\two\0.dat" && unknown.Remaining[0].Stamp is null, "Post-rename metadata failure never loses location or fabricates stamp");
        reject(() => MoveReceipts.ForUndo(unknown.Remaining), "Unverified remaining receipt cannot undo");

        var history = World.Create(1); var batch1 = history.Run(@"C:\two"); var batch2 = history.Run(@"C:\three"); var undo2 = history.Undo(batch2.Remaining);
        var updated1 = MoveReceipts.RefreshHistory(batch1.Remaining, undo2, batch2.Remaining);
        check(updated1[0].Stamp != batch1.Remaining[0].Stamp && updated1[0].Current == batch1.Remaining[0].Current && updated1[0].BeforeStamp == batch1.Remaining[0].BeforeStamp, "Trusted undo bridge refreshes earlier ChangeTime without changing the original baseline");
        check(history.Undo(updated1).Error is null && history.Files.ContainsKey(@"C:\one\0.dat"), "A to B to C to undo2 to undo1 succeeds");
        var referenceStamp = undo2.Locations[0].Stamp!.Value;
        foreach (var changedStamp in new[] { referenceStamp with { Length = referenceStamp.Length + 1 }, referenceStamp with { CreationTime = 77 }, referenceStamp with { WriteTime = 88 }, referenceStamp with { Attributes = 2 }, referenceStamp with { Links = 2 } })
        {
            var changedUndo = undo2 with { Locations = new[] { undo2.Locations[0] with { Stamp = changedStamp } } };
            check(MoveReceipts.RefreshHistory(batch1.Remaining, changedUndo, batch2.Remaining)[0].Stamp == batch1.Remaining[0].Stamp, "Undo bridge never refreshes changed non-ChangeTime metadata");
            var start = batch1.Remaining[0];
            var returned = new MoveLocation(start.Current, start.Current, changedStamp, start.CurrentDirectory, start.CurrentDirectory) { BeforeStamp = start.Stamp };
            check(MoveReceipts.RefreshHistory(batch1.Remaining, new(new[] { returned }, new IOException("rollback"), false))[0].Stamp == start.Stamp, "Rollback bridge never refreshes changed non-ChangeTime metadata");
        }
        check(MoveRules.SameRenameMetadata(referenceStamp, referenceStamp with { ChangeTime = referenceStamp.ChangeTime + 50 }) && !MoveRules.SameRenameMetadata(referenceStamp, referenceStamp with { Identity = new(1, 999, 3) }), "Only ChangeTime differs across a trusted rename metadata bridge");
        var edited = World.Create(1); var beforeEdit = edited.Run(@"C:\two"); edited.Files[@"C:\two\0.dat"].Stamp = edited.Files[@"C:\two\0.dat"].Stamp with { WriteTime = 900, ChangeTime = 901 };
        var afterEdit = edited.Run(@"C:\three"); var undoEditMove = edited.Undo(afterEdit.Remaining); var unwashed = MoveReceipts.RefreshHistory(beforeEdit.Remaining, undoEditMove, afterEdit.Remaining);
        check(unwashed[0].Stamp == beforeEdit.Remaining[0].Stamp, "Later move and undo never wash an external edit out of earlier history");
        reject(() => edited.Undo(unwashed), "Earlier undo still refuses externally edited file after later undo");
        var rolledHistory = World.Create(2); var older = rolledHistory.Run(@"C:\two"); rolledHistory.BeforeMove = (_, target, restore) => { if (!restore && target.EndsWith("1.dat", StringComparison.Ordinal)) throw new IOException("later batch failed"); };
        var failedLater = rolledHistory.Run(@"C:\three"); var refreshed = MoveReceipts.RefreshHistory(older.Remaining, failedLater); rolledHistory.BeforeMove = null;
        check(rolledHistory.Undo(refreshed).Error is null, "Failed later move rollback refreshes only exact matching earlier stamps");

        var dir = World.Directory(@"C:\one"); var receipt = batch1.Remaining[0];
        reject(() => MoveRules.ValidateDirectory(dir with { Ancestors = new[] { dir.Ancestors[0] } }), "Incomplete ancestor identity chain refused");
        reject(() => MoveRules.ValidateDirectory(dir with { Ancestors = new MoveDirectoryEntry[] { null!, dir.Ancestors[1] } }), "Null directory identity entry refused");
        check(!MoveRules.SameDirectory(dir, dir with { Ancestors = new[] { dir.Ancestors[0] with { Identity = new(1, 999, 0) }, dir.Ancestors[1] } }), "Changed ancestor identity cannot match despite same leaf identity");
        reject(() => MoveReceipts.ForUndo(new[] { receipt with { Stamp = receipt.Stamp!.Value with { Identity = new(2, 1, 0) } } }), "Receipt cross-volume identity refused");
        reject(() => MoveReceipts.ForUndo(new[] { receipt, receipt }), "Duplicate receipt identity refused");
        reject(() => MoveReceipts.AfterUndo(batch2.Remaining, undo2 with { Locations = new[] { undo2.Locations[0] with { CurrentDirectory = World.Directory(@"C:\wrong") } } }), "Forged end-directory in undo outcome refused");
        var mutableAncestors = receipt.CurrentDirectory.Ancestors.ToList(); var mutableReceipt = receipt with { CurrentDirectory = receipt.CurrentDirectory with { Ancestors = mutableAncestors } }; var frozen = MoveReceipts.ForUndo(new[] { mutableReceipt }); mutableAncestors.Clear();
        check(frozen[0].CurrentDirectory.Ancestors.Count == 2, "Undo receipt freezes caller-owned directory list");

        for (int count = 1; count <= 100; count++)
        {
            var world = World.Create(count); var result = world.Run(@"C:\two"); var undo = world.Undo(result.Remaining);
            check(result.Remaining.Count == count && undo.Error is null && MoveReceipts.AfterUndo(result.Remaining, undo).Count == 0 && world.Files.Count == count, "All supported batch sizes preserve count and undo exactly");
        }
    }

    private sealed class FileState(string path, MoveFileStamp stamp) { internal string Path = path; internal MoveFileStamp Stamp = stamp; }
    private sealed class World
    {
        internal readonly Dictionary<string, FileState> Files = new(StringComparer.Ordinal);
        internal readonly List<string> Log = new();
        internal Action<Lease, string, bool>? BeforeMove, AfterMove;
        internal static World Create(int count) { var world = new World(); for (int i = 0; i < count; i++) world.Add(@"C:\one\" + i + ".dat", (ulong)i + 1); return world; }
        internal void Add(string path, ulong id) => Files.Add(path, new(path, new(new(1, id, 123), 10, 1, 2, 3, 0x20, 1)));
        internal static MoveDirectorySnapshot Directory(string path)
        {
            var parts = new List<MoveDirectoryEntry> { new(@"C:\", new(1, 500, 0)) }; string current = @"C:\";
            foreach (string component in path[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            { current = current.TrimEnd('\\') + "\\" + component; ulong id = 600; foreach (char c in current) id = unchecked(id * 31 + c); parts.Add(new(current, new(1, id, 7))); }
            return new(path, parts.AsReadOnly());
        }
        internal MoveOutcome Run(string target, CancellationToken token = default)
        {
            var files = Files.Values.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
            var plan = files.Select(f => new MovePair(f.Path, MoveRules.Destination(f.Path, target))).ToArray();
            // An unrelated occupied target is not part of the explicit source selection.
            if (files.Any(f => MoveRules.Parent(f.Path) != target)) { files = files.Where(f => MoveRules.Parent(f.Path) != target).ToArray(); plan = files.Select(f => new MovePair(f.Path, MoveRules.Destination(f.Path, target))).ToArray(); }
            return MoveTransaction.Execute(files.Select(f => new Lease(this, f)).ToArray(), plan, token);
        }
        internal MoveOutcome Undo(IReadOnlyList<MoveLocation> receipt)
        {
            var entries = MoveReceipts.ForUndo(receipt); var leases = entries.Select(entry => new Lease(this, Files[entry.Current])).ToArray();
            for (int i = 0; i < leases.Length; i++) if (leases[i].VerifiedStamp != entries[i].Stamp) throw new IOException("External edit");
            return MoveTransaction.Execute(leases, entries.Select(x => new MovePair(x.Current, x.Original)).ToArray(), default);
        }
    }
    private sealed class Lease : IMoveLease
    {
        private readonly World world; private readonly FileState state;
        public string CurrentPath => state.Path;
        public MoveFileStamp? VerifiedStamp { get; private set; }
        public MoveDirectorySnapshot CurrentDirectory => World.Directory(MoveRules.Parent(CurrentPath));
        internal Lease(World world, FileState state) { this.world = world; this.state = state; VerifiedStamp = state.Stamp; }
        public void Validate() { world.Log.Add("validate"); if (VerifiedStamp != state.Stamp) throw new IOException("changed"); }
        public void EnsureDestinationFree(string path) { world.Log.Add("preflight"); if (world.Files.ContainsKey(path)) throw new IOException("occupied"); }
        public void MoveTo(string path, bool restoring)
        {
            world.BeforeMove?.Invoke(this, path, restoring); if (!restoring && VerifiedStamp != state.Stamp) throw new IOException("changed");
            if (world.Files.ContainsKey(path)) throw new IOException("occupied race");
            world.Files.Remove(state.Path); state.Path = path; world.Files.Add(path, state); VerifiedStamp = null;
            world.Log.Add((restoring ? "restore:" : "move:") + MoveRules.FileName(path)); state.Stamp = state.Stamp with { ChangeTime = state.Stamp.ChangeTime + 1 };
            world.AfterMove?.Invoke(this, path, restoring); VerifiedStamp = state.Stamp;
        }
    }
}
