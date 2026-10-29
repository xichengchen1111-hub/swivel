using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Swivel.Core;

namespace Swivel.Windows;

public sealed class FolderCopyOutcome
{
    public string? Folder { get; internal set; }
    public int FilesCopied { get; internal set; }
    public int FileCount { get; internal set; }
    public bool Cancelled { get; internal set; }
    public Exception? Error { get; internal set; }
}

/// One chosen folder copied into another. The source folder and its ancestors stay held from
/// planning through copying, and so do the destination's; each new folder is created
/// exclusively and held, and each file is copied by CopyFile2 into its held new folder.
public sealed class FolderCopyWork : IDisposable
{
    private readonly NativeFiles.MetadataDirectory source;
    private NativeFiles.DirectoryLease? destination;
    public string Folder { get; }
    public string Destination { get; }
    public FolderZipPlan Plan { get; }
    private FolderCopyWork(string folder, string destination, NativeFiles.MetadataDirectory source, FolderZipPlan plan) { Folder = folder; Destination = destination; this.source = source; Plan = plan; }

    public static FolderCopyWork Open(string folder, string destination, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Path.GetDirectoryName(folder) is null) throw new UserError("Choose a folder, not a whole drive.");
        destination = MoveRules.DirectoryPath(destination);
        FolderCopy.CheckDestination(folder, destination);
        var root = NativeFiles.MetadataDirectory.Open(folder);
        FolderCopyWork? work = null;
        try
        {
            work = new FolderCopyWork(folder, destination, root, FolderZip.Plan(root, token, folder.Length));
            FolderCopy.CheckLengths(work.Plan, destination);
            work.destination = NativeFiles.DirectoryLease.Open(destination);
            NativeFiles.RequireNtfs(destination, FolderCopy.NeedsNtfs);
            if (!NativeFiles.HasFreeSpace(destination, (ulong)work.Plan.Bytes + (ulong)work.Plan.Items.Count * 4096 + 64UL * 1024 * 1024))
                throw new UserError("There is not enough free space in the destination folder.");
            return work;
        }
        catch { if (work is not null) work.Dispose(); else root.Dispose(); throw; }
    }

    public FolderCopyOutcome Execute(CancellationToken token, Action<int, int>? progress) => NativeFiles.CopyFolderTree(Folder, Destination, Plan, token, progress);

    public void Dispose() { destination?.Dispose(); destination = null; source.Dispose(); }
}

public static partial class NativeFiles
{
    /// Stops at the first failure or cancellation; the new folder and completed copies stay.
    internal static FolderCopyOutcome CopyFolderTree(string folder, string destination, FolderZipPlan plan, CancellationToken token, Action<int, int>? progress)
    {
        token.ThrowIfCancellationRequested();
        var outcome = new FolderCopyOutcome { FileCount = plan.Files };
        var held = new List<SafeFileHandle>();
        var folders = new Dictionary<string, SafeFileHandle>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var parent = Open(destination, ReadAttributes | Traverse, 3, 3, OpenReparse | BackupSemantics);
            Check(parent, true);
            if (!GetFileInformationByHandle(parent, out var target)) ThrowLast();
            string? root = null;
            for (int i = 1; i <= 10000 && root is null; i++)
            {
                token.ThrowIfCancellationRequested();
                string candidate = System.IO.Path.Combine(destination, FolderCopy.FolderName(plan.Name, i));
                WindowsPathPolicy.Validate(candidate);
                if (CreateDirectory(candidate, IntPtr.Zero)) root = candidate;
                else if (Marshal.GetLastWin32Error() is int error && error != 183) throw new Win32Exception(error);
            }
            if (root is null) throw new UserError("Too many files have this output name.");
            outcome.Folder = root;
            var top = HoldCreated(root, target.Volume, held);
            CheckCreatedIn(top, parent, root);
            folders[""] = top;
            int done = 0;
            foreach (var item in plan.Items)
            {
                token.ThrowIfCancellationRequested();
                if (item.RelativePath.Length == 0) continue;
                string path = System.IO.Path.Combine(root, item.RelativePath);
                WindowsPathPolicy.Validate(path);
                string container = System.IO.Path.GetDirectoryName(item.RelativePath) ?? "";
                if (item.Directory)
                {
                    // The folder is new, so an existing child means something else wrote into it.
                    if (!CreateDirectory(path, IntPtr.Zero))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error == 183) throw new UserError(FolderCopy.Interfered);
                        throw new Win32Exception(error);
                    }
                    var created = HoldCreated(path, target.Volume, held);
                    CheckCreatedIn(created, folders[container], path);
                    folders[item.RelativePath] = created;
                    continue;
                }
                using var input = OpenRead(System.IO.Path.Combine(folder, item.RelativePath));
                if (input.Stream.Length != item.Length) throw new UserError(FolderCopy.Changed);
                var (copy, _, unverified) = CopyInto(input, System.IO.Path.GetDirectoryName(path)!, token);
                if (!string.Equals(copy, path, StringComparison.OrdinalIgnoreCase)) throw new UserError(FolderCopy.Interfered);
                if (unverified is not null) throw unverified;
                outcome.FilesCopied = ++done;
                progress?.Invoke(done, plan.Files);
            }
        }
        catch (OperationCanceledException) when (outcome.Folder is not null) { outcome.Cancelled = true; }
        catch (Exception error) when (outcome.Folder is not null && error is not OutOfMemoryException) { outcome.Error = error; }
        finally { for (int i = held.Count - 1; i >= 0; i--) held[i].Dispose(); }
        return outcome;
    }
}
