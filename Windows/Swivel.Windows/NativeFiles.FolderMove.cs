using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Swivel.Core;

namespace Swivel.Windows;

/// Move Folder to… and its Undo: one folder renamed, through its own held handle, into another
/// held folder on the same NTFS volume. Both parent chains are held without delete sharing from
/// the preview until the session is disposed, and the folder itself is held with DELETE access
/// and no delete sharing, so nothing else can rename or replace it in between. The rename never
/// replaces an existing name.
public sealed class FolderMoveSession : IDisposable
{
    private readonly NativeFiles.MoveDirectoryLease start, destination;
    private readonly SafeFileHandle folder;
    private readonly bool undo;
    private bool executed, disposed;
    public string Source { get; }
    public string Destination => destination.Snapshot.Path;
    /// Where the move would put the folder now (Undo: its original path). The rename itself
    /// claims the name, so the result may take a later number if this one is taken meanwhile.
    public string Planned { get; }
    public MoveIdentity Identity { get; }

    private FolderMoveSession(string source, string planned, NativeFiles.MoveDirectoryLease start, NativeFiles.MoveDirectoryLease destination, SafeFileHandle folder, MoveIdentity identity, bool undo)
    { Source = source; Planned = planned; this.start = start; this.destination = destination; this.folder = folder; Identity = identity; this.undo = undo; }

    public static FolderMoveSession Open(string suppliedFolder, string suppliedDestination, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string source = MoveRules.DirectoryPath(suppliedFolder), target = MoveRules.DirectoryPath(suppliedDestination);
        FolderMove.CheckDestination(source, target);
        NativeFiles.MoveDirectoryLease? parent = null, into = null; SafeFileHandle? handle = null;
        try
        {
            parent = NativeFiles.MoveDirectoryLease.Open(MoveRules.Parent(source), token);
            handle = NativeFiles.OpenFolderToMove(source);
            var identity = NativeFiles.FolderMoveIdentity(handle);
            if (identity.Volume != parent.Snapshot.LeafIdentity.Volume) throw new UserError(FolderMove.NotPlain);
            // Compared again by the paths the file system reports, so another spelling of the same
            // place (a subst drive letter, a short 8.3 name) cannot put the folder inside itself.
            // Checked before the destination chain is held, which would otherwise hold the folder.
            NativeFiles.CheckFolderMoveTarget(handle, target);
            into = NativeFiles.MoveDirectoryLease.Open(target, token);
            if (into.Snapshot.LeafIdentity.Volume != identity.Volume) throw new UserError(FolderMove.OtherVolume);
            if (into.Snapshot.LeafIdentity == parent.Snapshot.LeafIdentity) throw new UserError(FolderMove.AlreadyThere);
            foreach (var entry in into.Snapshot.Ancestors) if (entry.Identity == identity) throw new UserError(FolderMove.Inside);
            string planned = FolderMove.Claim(source, target, path => !NativeFiles.FolderMoveNameTaken(into, path));
            var session = new FolderMoveSession(source, planned, parent, into, handle, identity, false);
            parent = null; into = null; handle = null;
            token.ThrowIfCancellationRequested(); return session;
        }
        finally { handle?.Dispose(); into?.Dispose(); parent?.Dispose(); }
    }

    public static FolderMoveSession ForUndo(FolderMoveReceipt suppliedReceipt, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var receipt = FolderMove.ForUndo(suppliedReceipt);
        NativeFiles.MoveDirectoryLease? parent = null, back = null; SafeFileHandle? handle = null;
        try
        {
            try
            {
                parent = NativeFiles.MoveDirectoryLease.Open(receipt.CurrentParent.Path, token);
                back = NativeFiles.MoveDirectoryLease.Open(receipt.OriginalParent.Path, token);
            }
            catch (Exception error) when (error is Win32Exception or UserError) { throw new UserError(FolderMove.ParentsChanged); }
            if (!MoveRules.SameDirectory(receipt.CurrentParent, parent.Snapshot) || !MoveRules.SameDirectory(receipt.OriginalParent, back.Snapshot))
                throw new UserError(FolderMove.ParentsChanged);
            try { handle = NativeFiles.OpenFolderToMove(receipt.Current); }
            catch (Win32Exception) { throw new UserError(FolderMove.Replaced); }
            catch (UserError error) when (error.Message == FolderMove.NotPlain) { throw new UserError(FolderMove.Replaced); }
            if (NativeFiles.FolderMoveIdentity(handle) != receipt.Identity) throw new UserError(FolderMove.Replaced);
            if (NativeFiles.FolderMoveNameTaken(back, receipt.Original)) throw new UserError(FolderMove.OriginalTaken);
            var session = new FolderMoveSession(receipt.Current, receipt.Original, parent, back, handle, receipt.Identity, true);
            parent = null; back = null; handle = null;
            token.ThrowIfCancellationRequested(); return session;
        }
        finally { handle?.Dispose(); back?.Dispose(); parent?.Dispose(); }
    }

    /// Renames the held folder. A move takes the planned name or the next free number; Undo
    /// takes only the original name and is refused if it is taken.
    public FolderMoveReceipt Execute(CancellationToken token)
    {
        if (disposed) throw new ObjectDisposedException(nameof(FolderMoveSession));
        if (executed) throw new InvalidOperationException("A folder move preview can only be executed once.");
        executed = true; token.ThrowIfCancellationRequested();
        start.Verify(); destination.Verify();
        if (NativeFiles.FolderMoveIdentity(folder) != Identity) throw new UserError(FolderMove.Replaced);
        string current;
        if (undo)
        {
            if (!NativeFiles.RenameFolder(folder, destination, Planned, out int error))
                throw error is 80 or 183 ? new UserError(FolderMove.OriginalTaken) : NativeFiles.FolderMoveError(error);
            current = Planned;
        }
        else
            current = FolderMove.Claim(Source, Destination, path =>
            {
                if (NativeFiles.RenameFolder(folder, destination, path, out int error)) return true;
                if (error is 80 or 183) return false;
                throw NativeFiles.FolderMoveError(error);
            });
        // Cached snapshots: the result never depends on a query after the rename.
        return new FolderMoveReceipt(Source, current, Identity, start.Snapshot, destination.Snapshot);
    }

    public void Dispose()
    {
        if (disposed) return; disposed = true;
        try { folder.Dispose(); }
        finally { try { destination.Dispose(); } finally { start.Dispose(); } }
    }
}

public static partial class NativeFiles
{
    // A folder to move: no link, cloud placeholder or system folder. DELETE access for the rename,
    // without delete sharing so nothing else renames or removes it while it is held.
    internal static SafeFileHandle OpenFolderToMove(string path)
    {
        WindowsPathPolicy.Validate(path);
        var handle = Open(path, DeleteAccess | ReadAttributes, 3, 3, OpenReparse | BackupSemantics);
        try
        {
            if (GetFileType(handle) != 1 || !GetFileInformationByHandle(handle, out var info) || (info.Attributes & 0x10) == 0
                || (info.Attributes & (UnsafeAttributes | 0x4 | 0x40)) != 0)
                throw new UserError(FolderMove.NotPlain);
            RequireMoveNtfs(handle);
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    internal static MoveIdentity FolderMoveIdentity(SafeFileHandle handle) => ReadMoveIdentity(handle);

    internal static void CheckFolderMoveTarget(SafeFileHandle folder, string destination)
        => FolderMove.CheckDestination(FinalForCompare(FinalPath(folder)), FinalForCompare(FinalDirectoryPath(destination)));

    private static string FinalForCompare(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        if (path.Length < 3 || path[1] != ':') throw new UserError(LocalOnly);
        return path;
    }

    /// For the preview only (the rename claims the name): whether `path` exists inside the held
    /// folder now. Anything but "not found" counts as taken.
    internal static bool FolderMoveNameTaken(MoveDirectoryLease directory, string path)
    {
        directory.Verify();
        if (GetAttributes(path) != uint.MaxValue) return true;
        return Marshal.GetLastWin32Error() != 2;
    }

    internal static bool RenameFolder(SafeFileHandle folder, MoveDirectoryLease directory, string path, out int error)
    {
        if (MoveRules.Parent(path) != directory.Snapshot.Path) throw new ArgumentException("Folder move destination is outside the held folder.");
        return RenameMove(folder, directory, MoveRules.FileName(path), out error);
    }

    internal static Exception FolderMoveError(int error)
        => error is 5 or 32 ? new UserError(FolderMove.InUse) : new Win32Exception(error);

    /// Whether `folder` is on the volume of `destination` (FILE_ID_INFO volume serial), or null
    /// when that cannot be read. Only chooses between moving and offering a copy; the move
    /// itself refuses another volume again.
    internal static bool? FolderOnVolumeOf(string folder, string destination)
    {
        try
        {
            ulong Volume(string path)
            {
                path = MoveRules.DirectoryPath(path);
                if (GetDriveType(path[..3]) is not 2 and not 3 and not 6) throw new UserError(LocalOnly);
                using var handle = Open(path, ReadAttributes, 7, 3, OpenReparse | BackupSemantics);
                return ReadIdentity(handle).Volume;
            }
            return Volume(folder) == Volume(destination);
        }
        catch (Exception error) when (error is Win32Exception or UserError or System.IO.IOException or UnauthorizedAccessException) { return null; }
    }
}
