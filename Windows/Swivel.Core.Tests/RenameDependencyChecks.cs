using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Swivel.Core;

internal static class RenameDependencyChecks
{
    internal static void Run(Action<bool,string> check, Action<Action,string> reject)
    {
        string[] names = Enumerable.Range(0,5).Select(i => $@"C:\rename\file-{i}.txt").ToArray();
        IEnumerable<int[]> Permute(int[] input) => input.Length == 0 ? new[] { Array.Empty<int>() } :
            input.SelectMany((value, i) => Permute(input.Where((_, j) => j != i).ToArray()).Select(rest => new[] { value }.Concat(rest).ToArray()));
        foreach (var permutation in Permute(Enumerable.Range(0,5).ToArray()))
        {
            var plan = names.Select((name, i) => new RenamePair(name, names[permutation[i]])).ToArray();
            for (int fault = 0; fault <= 12; fault++)
            {
                var world = new World(names) { FailAt = fault };
                var outcome = RenameTransaction.Execute(world.Leases, plan, CancellationToken.None);
                Verify(world, outcome, check);
                if (outcome.Error is null)
                {
                    check(world.Leases.Select(x => x.CurrentPath).SequenceEqual(plan.Select(x => x.Destination)), "Permutation destinations preserve identity");
                    world.FailAt = 0;
                    var undone = RenameTransaction.Execute(world.Leases, outcome.Locations.Select(x => new RenamePair(x.Current,x.Original)).ToArray(), CancellationToken.None);
                    check(undone.Error is null && world.Leases.Select(x => x.CurrentPath).SequenceEqual(names), "Permutation undo restores identity and names");
                }
                else check(outcome.Remaining.Count == 0, "One transient failure restores every original");
            }
        }
        var cases = new[] {
            new[] { new RenamePair(names[0], names[0].Replace("file", "FILE")) },
            new[] { new RenamePair(names[0],names[1]), new RenamePair(names[1],names[2]) },
            new[] { new RenamePair(names[0],names[1]), new RenamePair(names[1],names[0]) }
        };
        foreach (var plan in cases)
        {
            for (int cancelAt = 1; cancelAt <= 2 * plan.Length; cancelAt++)
            {
                using var cancellation = new CancellationTokenSource();
                var world = new World(plan.Select(x => x.Source).ToArray());
                world.AfterMove = () => { if (world.Calls == cancelAt) cancellation.Cancel(); };
                var outcome = RenameTransaction.Execute(world.Leases,plan,cancellation.Token);
                Verify(world,outcome,check);
                if (cancelAt < 2 * plan.Length) check(outcome.Cancelled && outcome.Remaining.Count == 0, "Staging and publish cancellation restore original spellings");
            }
            for (int first = 1; first <= 2 * plan.Length; first++)
            for (int second = first + 1; second <= 4 * plan.Length; second++)
            {
                var world = new World(plan.Select(x => x.Source).ToArray()) { FailAt = first, FailAgain = second };
                Verify(world,RenameTransaction.Execute(world.Leases,plan,CancellationToken.None),check);
            }
            // Another process occupies an original name after staging. Rollback must
            // preserve it and disclose the displaced source's exact temporary path.
            using var cancel = new CancellationTokenSource();
            var raced = new World(plan.Select(x => x.Source).ToArray());
            raced.AfterMove = () => { if (raced.Calls == 1) { raced.Files.Add(plan[0].Source,999); cancel.Cancel(); } };
            var blocked = RenameTransaction.Execute(raced.Leases,plan,cancel.Token);
            Verify(raced,blocked,check);
            check(raced.Files[plan[0].Source] == 999 && blocked.Remaining.Count > 0, "Unrelated recreated original is never replaced");
        }
        var longName = @"C:\" + new string('x',220) + @"\a";
        var longWorld = new World(new[] { longName });
        reject(() => RenameTransaction.Execute(longWorld.Leases,new[] { new RenamePair(longName,longName[..^1]+"A") },CancellationToken.None), "Insufficient room for temporary path refuses before first rename");
        check(longWorld.Calls == 0 && longWorld.Files[longName] == 0, "Long-path staging preflight leaves source unchanged");
    }
    private static void Verify(World world,RenameOutcome outcome,Action<bool,string> check)
    {
        for (int i = 0; i < world.Leases.Length; i++)
        {
            var lease = world.Leases[i];
            check(world.Files.Count(x => x.Value == i) == 1 && world.Files[lease.CurrentPath] == i, "Every source survives exactly once");
            check(outcome.Locations[i].Current == lease.CurrentPath && outcome.Locations[i].Stamp == lease.Stamp, "Recovery receipt gives exact path and identity");
        }
    }
    private sealed class World
    {
        internal readonly Dictionary<string,int> Files = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Lease[] Leases;
        internal int Calls, FailAt, FailAgain;
        internal Action? AfterMove;
        internal World(string[] paths) { Leases = paths.Select((p,i) => { Files.Add(p,i); return new Lease(this,p,i); }).ToArray(); }
        internal sealed class Lease : IRenameLease
        {
            private readonly World world; private readonly int identity;
            public string CurrentPath { get; private set; }
            public FileStamp Stamp => new(1,(ulong)identity+1,123,456,789);
            internal Lease(World world,string path,int identity) { this.world=world;CurrentPath=path;this.identity=identity; }
            public void MoveTo(string destination)
            {
                world.Calls++;
                if (world.Calls == world.FailAt || world.Calls == world.FailAgain || world.Files.ContainsKey(destination)) throw new IOException("Injected failure or occupied target");
                if (!world.Files.Remove(CurrentPath)) throw new IOException("Missing source");
                world.Files.Add(destination,identity); CurrentPath=destination; world.AfterMove?.Invoke();
            }
        }
    }
}
