using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

public sealed class RenameSession : IDisposable
{
    private readonly List<NativeFiles.RenameLease> leases = new();
    public IReadOnlyList<string> Paths => leases.Select(x => x.CurrentPath).ToArray();
    public IReadOnlyList<FileStamp> Stamps => leases.Select(x => x.Stamp).ToArray();
    public IReadOnlyList<string> StoredNames => leases.Select(x => x.StoredName).ToArray();
    private RenameSession() { }
    public static RenameSession Open(IEnumerable<string> paths, CancellationToken token)
    {
        var session = new RenameSession();
        try
        {
            var inputs = paths.Take(101).ToArray();
            if (inputs.Length is < 1 or > 100 || inputs.Distinct(StringComparer.OrdinalIgnoreCase).Count() != inputs.Length)
                throw new UserError("Choose distinct files for batch rename.");
            foreach (var path in inputs) { token.ThrowIfCancellationRequested(); session.leases.Add(NativeFiles.OpenRename(path)); }
            if (session.leases.Select(x => (x.Stamp.Volume, x.Stamp.Index)).Distinct().Count() != session.leases.Count)
                throw new UserError("Choose distinct files for batch rename.");
            return session;
        }
        catch { session.Dispose(); throw; }
    }
    public static RenameSession ForUndo(IReadOnlyList<RenameLocation> receipt, CancellationToken token)
    {
        var session = Open(receipt.Select(x => x.Current), token);
        try
        {
            for (int i = 0; i < receipt.Count; i++)
                if (!session.leases[i].Stamp.SameObjectAndContent(receipt[i].Stamp)) throw new UserError("A renamed file was replaced or edited. Undo was refused without changing any file.");
            return session;
        }
        catch { session.Dispose(); throw; }
    }
    public RenameOutcome Execute(IReadOnlyList<RenamePair> plan, CancellationToken token)
    {
        RenameRules.Validate(plan);
        if (plan.Count != leases.Count || plan.Where((x, i) => x.Source != leases[i].CurrentPath).Any()) throw new ArgumentException("Stale rename preview.");
        // Check every destination before changing the first file. Atomic rename is still
        // authoritative if an unrelated process creates a target after this preflight.
        for (int i = 0; i < plan.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (plan[i].Changed && !plan.Any(x => x.Source.Equals(plan[i].Destination, StringComparison.OrdinalIgnoreCase)))
                leases[i].EnsureDestinationFree(plan[i].Destination);
        }
        return RenameTransaction.Execute(leases, plan, token);
    }
    public void Dispose() { foreach (var lease in leases) lease.Dispose(); leases.Clear(); }
}
