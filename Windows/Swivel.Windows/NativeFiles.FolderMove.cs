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
    public MoveIdentity DestinationIdentity => destination.Snapshot.LeafIdentity;
    /// On an exFAT or FAT32 drive (Undo then needs the reference kept to the folder).
    public bool IsFat => start.IsFat;

    private FolderMoveSession(string source, string planned, NativeFiles.MoveDirectoryLease start, NativeFiles.MoveDirectoryLease destination, SafeFileHandle folder, MoveIdentity identity, bool undo)
    { Source = source; Planned = planned; this.start = start; this.destination = destination; this.folder = folder; Identity = identity; this.undo = undo; }

    /// `allowFat`: also within one exFAT or FAT32 drive (the app's own Move Folder, not agents).
    public static FolderMoveSession Open(string suppliedFolder, string suppliedDestination, CancellationToken token, bool allowFat = false)
    {
        token.ThrowIfCancellationRequested();
        string source = MoveRules.DirectoryPath(suppliedFolder), target = MoveRules.DirectoryPath(suppliedDestination);
        FolderMove.CheckDestination(source, target);
        NativeFiles.MoveDirectoryLease? parent = null, into = null; SafeFileHandle? handle = null;
        try
        {
            parent = NativeFiles.MoveDirectoryLease.Open(MoveRules.Parent(source), token, allowFat);
            try { handle = NativeFiles.OpenFolderToMove(source, parent.IsFat); }
            catch (Win32Exception error) when (error.NativeErrorCode is 5 or 32) { throw new UserError(FolderMove.InUse); }
            // exFAT/FAT32: the folder's identity is a token for the handle Swivel keeps (NativeFiles.Fat.cs).
            if (parent.IsFat) NativeFiles.RequireExactFatName(handle, source);
            var identity = parent.IsFat ? new MoveIdentity(NativeFiles.FatVolume(handle), NativeFiles.NewFatToken(), NativeFiles.FatToken) : NativeFiles.FolderMoveIdentity(handle);
            if (identity.Volume != parent.Snapshot.LeafIdentity.Volume) throw new UserError(FolderMove.NotPlain);
            // Compared again by the paths the file system reports, so another spelling of the same
            // place (a subst drive letter, a short 8.3 name) cannot put the folder inside itself.
            // Checked before the destination chain is held, which would otherwise hold the folder.
            NativeFiles.CheckFolderMoveTarget(handle, target);
            into = NativeFiles.MoveDirectoryLease.Open(target, token, allowFat);
            if (into.IsFat != parent.IsFat || into.Snapshot.LeafIdentity.Volume != identity.Volume) throw new UserError(FolderMove.OtherVolume);
            if (into.Snapshot.LeafIdentity == parent.Snapshot.LeafIdentity) throw new UserError(FolderMove.AlreadyThere);
            var position = parent.IsFat ? NativeFiles.FatPositionIdentity(handle) : identity;
            foreach (var entry in into.Snapshot.Ancestors) if (entry.Identity == position) throw new UserError(FolderMove.Inside);
            // The folder's name as stored (exact case, long name), not as it was typed or picked.
            source = MoveRules.Parent(source).TrimEnd('\\') + "\\" + NativeFiles.StoredLeaf(handle);
            string planned = FolderMove.Claim(source, target, path => !NativeFiles.FolderMoveNameTaken(into, path));
            token.ThrowIfCancellationRequested();
            var session = new FolderMoveSession(source, planned, parent, into, handle, identity, false);
            parent = null; into = null; handle = null; return session;
        }
        finally { handle?.Dispose(); into?.Dispose(); parent?.Dispose(); }
    }

    public static FolderMoveSession ForUndo(FolderMoveReceipt suppliedReceipt, CancellationToken token, bool allowFat = false)
    {
        token.ThrowIfCancellationRequested();
        var receipt = FolderMove.ForUndo(suppliedReceipt);
        NativeFiles.MoveDirectoryLease? parent = null, back = null; SafeFileHandle? handle = null;
        try
        {
            try
            {
                parent = NativeFiles.MoveDirectoryLease.Open(receipt.CurrentParent.Path, token, allowFat);
                back = NativeFiles.MoveDirectoryLease.Open(receipt.OriginalParent.Path, token, allowFat);
            }
            catch (Exception error) when (error is Win32Exception or UserError) { throw new UserError(FolderMove.ParentsChanged); }
            if (!MoveRules.SameDirectory(receipt.CurrentParent, parent.Snapshot) || !MoveRules.SameDirectory(receipt.OriginalParent, back.Snapshot))
                throw new UserError(FolderMove.ParentsChanged);
            if (parent.IsFat)
            {
                // The very folder Swivel kept a reference to, still at the receipt's path.
                if (!back.IsFat || receipt.Identity.High != NativeFiles.FatToken) throw new UserError(FatRules.Released);
                handle = NativeFiles.ReopenFat(receipt.Identity.Low, receipt.Current, 0x10000 | 0x80, 3, true);
                try { NativeFiles.CheckFolderToMove(handle); }
                catch (UserError error) when (error.Message == FolderMove.NotPlain) { throw new UserError(FolderMove.Replaced); }
            }
            else
            {
                try { handle = NativeFiles.OpenFolderToMove(receipt.Current); }
                catch (Win32Exception error) when (error.NativeErrorCode is 5 or 32) { throw new UserError(FolderMove.InUse); }
                catch (Win32Exception) { throw new UserError(FolderMove.Replaced); }
                catch (UserError error) when (error.Message == FolderMove.NotPlain) { throw new UserError(FolderMove.Replaced); }
                if (NativeFiles.FolderMoveIdentity(handle) != receipt.Identity) throw new UserError(FolderMove.Replaced);
            }
            if (NativeFiles.FolderMoveNameTaken(back, receipt.Original)) throw new UserError(FolderMove.OriginalTaken);
            token.ThrowIfCancellationRequested();
            var session = new FolderMoveSession(receipt.Current, receipt.Original, parent, back, handle, receipt.Identity, true);
            parent = null; back = null; handle = null; return session;
        }
        finally { handle?.Dispose(); back?.Dispose(); parent?.Dispose(); }
    }

    /// Renames the held folder. A move takes the planned name or the next free number; Undo
    /// takes only the original name and is refused if it is taken.
    /// With `exact` (an agent's plan, which keeps names), only the planned name is tried and a
    /// taken name refuses the move instead of numbering it.
    public FolderMoveReceipt Execute(CancellationToken token, bool exact = false)
    {
        if (disposed) throw new ObjectDisposedException(nameof(FolderMoveSession));
        if (executed) throw new InvalidOperationException("A folder move preview can only be executed once.");
        executed = true; token.ThrowIfCancellationRequested();
        start.Verify(); destination.Verify();
        if (!IsFat && NativeFiles.FolderMoveIdentity(folder) != Identity) throw new UserError(FolderMove.Replaced);
        string current;
        if (undo || exact)
        {
            if (!NativeFiles.RenameFolder(folder, destination, Planned, out int error))
                throw error is 80 or 183 ? (undo ? new UserError(FolderMove.OriginalTaken) : new McpToolError("A file or folder with this name is in the destination now. Nothing was moved or overwritten.")) : NativeFiles.FolderMoveError(error);
            current = Planned;
        }
        else
            current = FolderMove.Claim(Source, Destination, path =>
            {
                if (NativeFiles.RenameFolder(folder, destination, path, out int error)) return true;
                if (error is 80 or 183) return false;
                throw NativeFiles.FolderMoveError(error);
            });
        if (IsFat)
        {
            // The name actually stored decides where the folder is; Undo needs a kept reference.
            try { current = NativeFiles.FatCurrentPath(folder); NativeFiles.HoldFat(Identity.Low, folder, true); }
            catch (Exception error) when (error is Win32Exception or UserError) { /* Undo then reports the reference as released. */ }
        }
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
    internal static SafeFileHandle OpenFolderToMove(string path, bool allowFat = false)
    {
        WindowsPathPolicy.Validate(path);
        var handle = Open(path, DeleteAccess | ReadAttributes, 3, 3, OpenReparse | BackupSemantics);
        try
        {
            CheckFolderToMove(handle);
            if (!(allowFat && FatRules.IsFat(Format(handle)))) RequireMoveNtfs(handle);
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }
    internal static void CheckFolderToMove(SafeFileHandle handle)
    {
        if (GetFileType(handle) != 1 || !GetFileInformationByHandle(handle, out var info) || (info.Attributes & 0x10) == 0
            || (info.Attributes & (UnsafeAttributes | 0x4 | 0x40)) != 0)
            throw new UserError(FolderMove.NotPlain);
    }

    internal static MoveIdentity FolderMoveIdentity(SafeFileHandle handle) => ReadMoveIdentity(handle);

    /// For agents: whether a plain local path names a folder (or a link to one) itself, without
    /// following it. Anything else, including a network drive, is "not a folder".
    internal static bool IsLocalFolder(string path)
    {
        try
        {
            path = MoveRules.DirectoryPath(path);
            if (path.Length == 3 || GetDriveType(path[..3]) is not 2 and not 3 and not 6) return false;
            // The parent chain held first refuses links and junctions in it, so nothing is followed elsewhere.
            using var parent = DirectoryLease.Open(MoveRules.Parent(path));
            uint attributes = GetAttributes(path);
            return attributes != uint.MaxValue && (attributes & 0x10) != 0;
        }
        catch (Exception error) when (error is Win32Exception or UserError or System.IO.IOException or UnauthorizedAccessException) { return false; }
    }

    /// The identity of the plain local folder at this path (not a link or junction), or null.
    internal static MoveIdentity? FolderIdentityAt(string path)
    {
        try
        {
            path = MoveRules.DirectoryPath(path);
            if (path.Length == 3 || GetDriveType(path[..3]) is not 2 and not 3 and not 6) return null;
            using var parent = DirectoryLease.Open(MoveRules.Parent(path));
            using var handle = Open(path, ReadAttributes, 7, 3, OpenReparse | BackupSemantics);
            if (GetFileType(handle) != 1 || !GetFileInformationByHandle(handle, out var info) || (info.Attributes & 0x10) == 0 || (info.Attributes & UnsafeAttributes) != 0) return null;
            return ReadMoveIdentity(handle);
        }
        catch (Exception error) when (error is Win32Exception or UserError or System.IO.IOException or UnauthorizedAccessException) { return null; }
    }
    internal static string StoredLeaf(SafeFileHandle handle) => Leaf(FinalPath(handle));

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
                return VolumeKey(handle);
            }
            return Volume(folder) == Volume(destination);
        }
        catch (Exception error) when (error is Win32Exception or UserError or System.IO.IOException or UnauthorizedAccessException) { return null; }
    }
}
