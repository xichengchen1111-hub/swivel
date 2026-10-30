using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Swivel.Core;

namespace Swivel.Windows;

public static partial class NativeFiles
{
    // Independent of RenameLease: the older same-directory rename contract stays intact.
    internal sealed class MoveDirectoryLease : IDisposable
    {
        private readonly List<SafeFileHandle> handles = new();
        internal MoveDirectorySnapshot Snapshot { get; private set; } = null!;
        internal SafeFileHandle Handle => handles[^1];
        /// On exFAT/FAT32 (only with `allowFat`): ancestors are identified by their directory entry
        /// position, which stays put while they are held without delete sharing.
        internal bool IsFat { get; private set; }
        private MoveDirectoryLease() { }
        internal static MoveDirectoryLease Open(string suppliedPath, CancellationToken token, bool allowFat = false)
        {
            string path = MoveRules.DirectoryPath(suppliedPath);
            token.ThrowIfCancellationRequested();
            if (GetDriveType(path[..3]) is not 2 and not 3 and not 6) throw new UserError(LocalOnly);
            var lease = new MoveDirectoryLease();
            try
            {
                var entries = new List<MoveDirectoryEntry>();
                void Add(string current)
                {
                    token.ThrowIfCancellationRequested();
                    var handle = NativeFiles.Open(current, ReadAttributes | Traverse, 3, 3, OpenReparse | BackupSemantics); // held like DirectoryLease
                    lease.handles.Add(handle); Check(handle, true);
                    if (lease.handles.Count == 1) lease.IsFat = allowFat && FatRules.IsFat(Format(handle));
                    entries.Add(new(current, lease.ReadIdentity(handle)));
                }
                string current = path[..3]; Add(current);
                foreach (string component in path[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
                { current = current.TrimEnd('\\') + "\\" + component; Add(current); }
                // FAT: the folders must be named exactly as stored, so the paths a move or its Undo
                // records are the ones the file system reports for the kept references.
                if (lease.IsFat) { if (!FatRules.IsFat(Format(lease.Handle))) throw new UserError(LocalOnly); RequireExactFatName(lease.Handle, path); }
                else RequireMoveNtfs(lease.Handle);
                lease.Snapshot = new(path, entries.AsReadOnly()); MoveRules.ValidateDirectory(lease.Snapshot);
                token.ThrowIfCancellationRequested(); return lease;
            }
            catch (Exception error)
            {
                try { lease.Dispose(); } catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
                throw;
            }
        }
        internal void Verify()
        {
            if (handles.Count != Snapshot.Ancestors.Count) throw new ObjectDisposedException(nameof(MoveDirectoryLease));
            for (int i = 0; i < handles.Count; i++)
            {
                Check(handles[i], true);
                if (ReadIdentity(handles[i]) != Snapshot.Ancestors[i].Identity)
                    throw new UserError("A source or destination folder changed. Create a new move preview.");
            }
        }
        private MoveIdentity ReadIdentity(SafeFileHandle handle) => IsFat ? FatPositionIdentity(handle) : ReadMoveIdentity(handle);
        public void Dispose()
        {
            List<Exception>? errors = null;
            for (int i = handles.Count - 1; i >= 0; i--)
                try { handles[i].Dispose(); } catch (Exception error) { (errors ??= new()).Add(error); }
            handles.Clear(); if (errors is not null) throw new AggregateException(errors);
        }
    }

    internal sealed class MoveLease : IMoveLease, IDisposable
    {
        private readonly SafeFileHandle handle;
        private readonly MoveDirectoryLease start, destination;
        private readonly MoveIdentity identity;
        // exFAT/FAT32: the held-handle token that stands for the file's identity (see NativeFiles.Fat.cs).
        private readonly ulong? fatToken;
        public string CurrentPath { get; private set; }
        public MoveFileStamp? VerifiedStamp { get; private set; }
        public MoveDirectorySnapshot CurrentDirectory { get; private set; }
        /// The held file's stamp and stored name (long name, exact case), as the agent tools compare them.
        public FileStamp FileStamp => ReadStamp(handle);
        public string StoredName => Leaf(FinalPath(handle));
        private MoveLease(string path, SafeFileHandle handle, MoveDirectoryLease start, MoveDirectoryLease destination, MoveFileStamp stamp, ulong? fatToken)
        {
            CurrentPath = path; this.handle = handle; this.start = start; this.destination = destination; this.fatToken = fatToken;
            VerifiedStamp = stamp; identity = stamp.Identity; CurrentDirectory = start.Snapshot;
        }
        /// On exFAT/FAT32 (when the folders are), `fatToken` from an undo receipt reopens the file
        /// Swivel kept a reference to instead of opening the path.
        internal static MoveLease Open(string path, MoveDirectoryLease start, MoveDirectoryLease destination, ulong? fatToken = null)
        {
            WindowsPathPolicy.Validate(path); SafeFileHandle? handle = null;
            try
            {
                if (start.IsFat || destination.IsFat)
                {
                    if (!start.IsFat || !destination.IsFat) throw new UserError("Move Files currently requires source and destination on the same NTFS volume. Nothing was copied or deleted.");
                    handle = fatToken is ulong held ? ReopenFat(held, path, DeleteAccess | ReadAttributes, 1, false) : NativeFiles.Open(path, DeleteAccess | ReadAttributes, 1, 3, OpenReparse);
                    Check(handle, false);
                    if (fatToken is null) RequireExactFatName(handle, path);
                    if (!FatRules.IsFat(Format(handle))) throw new UserError(LocalOnly);
                    ulong token = fatToken ?? NewFatToken(); var fatStamp = ReadMoveStamp(handle, token);
                    if (fatStamp.Identity.Volume != start.Snapshot.LeafIdentity.Volume || fatStamp.Identity.Volume != destination.Snapshot.LeafIdentity.Volume)
                        throw new UserError("Move Files moves files only within one drive. Nothing was copied or deleted.");
                    var fat = new MoveLease(path, handle, start, destination, fatStamp, token); handle = null; return fat;
                }
                // Do not open a preceding READ-only probe: it would block DELETE.
                // Attribute/delete access does not read or copy any content stream.
                handle = NativeFiles.Open(path, DeleteAccess | ReadAttributes, 1, 3, OpenReparse);
                RequireMoveNtfs(handle); var stamp = ReadMoveStamp(handle);
                if (stamp.Identity.Volume != start.Snapshot.LeafIdentity.Volume || stamp.Identity.Volume != destination.Snapshot.LeafIdentity.Volume)
                    throw new UserError("Move Files currently requires source and destination on the same NTFS volume. Nothing was copied or deleted.");
                var lease = new MoveLease(path, handle, start, destination, stamp, null); handle = null; return lease;
            }
            finally { handle?.Dispose(); }
        }
        public void Validate()
        {
            start.Verify(); destination.Verify();
            if (VerifiedStamp is not { } expected || ReadMoveStamp(handle, fatToken) != expected)
                throw new UserError("The recorded file identity or metadata changed. The move was refused; create a new preview.");
        }
        private MoveDirectoryLease DirectoryFor(string path)
        {
            string parent = MoveRules.Parent(path);
            if (parent == start.Snapshot.Path) return start;
            if (parent == destination.Snapshot.Path) return destination;
            throw new ArgumentException("Move destination is outside the held directories.");
        }
        public void EnsureDestinationFree(string path)
        {
            DirectoryFor(path).Verify();
            uint attributes = GetAttributes(path);
            if (attributes != uint.MaxValue) throw new UserError("A destination name is already occupied. Nothing was overwritten.");
            int error = Marshal.GetLastWin32Error(); if (error != 2) throw new Win32Exception(error);
        }
        public void MoveTo(string path, bool restoring)
        {
            var directory = DirectoryFor(path);
            var before = VerifiedStamp;
            if (MoveRules.FileName(path) != MoveRules.FileName(CurrentPath)) throw new ArgumentException("Move Files keeps file names.");
            if (!restoring) Validate();
            else
            {
                // A failed metadata refresh must not strand a moved object. Recovery
                // uses the still-held file identity, without trusting a path or stamp.
                start.Verify(); destination.Verify(); Check(handle, false);
                if (fatToken is null && ReadMoveIdentity(handle) != identity) throw new IOException("The held move identity changed.");
            }
            if (!RenameMove(handle, directory, MoveRules.FileName(path), out int error)) throw new Win32Exception(error);
            CurrentPath = path; CurrentDirectory = directory.Snapshot; VerifiedStamp = null;
            // FAT: the name actually stored decides where the file is.
            if (fatToken is not null && FatCurrentPath(handle) is var actual && !string.Equals(actual, path, StringComparison.Ordinal))
            { CurrentPath = actual; throw new UserError(FatRules.NameNotTaken); }
            VerifiedStamp = ReadMoveStamp(handle, fatToken);
            if (!restoring && (before is not { } previous || !MoveRules.SameRenameMetadata(previous, VerifiedStamp.Value)))
                throw new UserError("The recorded file identity or metadata changed. The move was refused; create a new preview.");
        }
        /// Keeps a reference for Undo on exFAT/FAT32 (nothing on NTFS).
        internal void HoldForUndo() { if (fatToken is ulong token) HoldFat(token, handle, false); }
        public void Dispose() => handle.Dispose();
    }

    private static MoveIdentity ReadMoveIdentity(SafeFileHandle handle)
    {
        var bytes = new byte[24]; if (!GetDirectoryInfo(handle, 18, bytes, 24)) ThrowLast();
        var identity = new MoveIdentity(BinaryPrimitives.ReadUInt64LittleEndian(bytes), BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8)), BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(16)));
        if (identity.Low == 0 && identity.High == 0) throw new UserError("Move Files requires complete Windows file identities.");
        return identity;
    }
    // On exFAT/FAT32 `fatToken` stands in for the file ID, which those drives do not have.
    private static MoveFileStamp ReadMoveStamp(SafeFileHandle handle, ulong? fatToken = null)
    {
        Check(handle, false);
        if (!GetFileInformationByHandle(handle, out var info)) ThrowLast();
        // A same-volume move renames only this name: a read-only file stays read-only and a hard
        // link's other names stay where they are. The link count is part of the stamp, so a link
        // added or removed elsewhere since the preview still stops the move or its undo.
        if (info.Links < 1) throw new UserError("Move Files requires complete Windows file identities.");
        var basic = new byte[40]; if (!GetDirectoryInfo(handle, 0, basic, 40)) ThrowLast();
        var identity = fatToken is ulong token ? new MoveIdentity(info.Volume, token, FatToken) : ReadMoveIdentity(handle);
        return new(identity, ((ulong)info.SizeHigh << 32) | info.SizeLow,
            BinaryPrimitives.ReadInt64LittleEndian(basic), BinaryPrimitives.ReadInt64LittleEndian(basic.AsSpan(16)),
            BinaryPrimitives.ReadInt64LittleEndian(basic.AsSpan(24)), BinaryPrimitives.ReadUInt32LittleEndian(basic.AsSpan(32)), info.Links);
    }
    private static void RequireMoveNtfs(SafeFileHandle handle)
    {
        var name = new StringBuilder(64);
        if (!MoveVolumeInformation(handle, null, 0, out _, out _, out _, name, name.Capacity)) ThrowLast();
        if (!string.Equals(name.ToString(), "NTFS", StringComparison.Ordinal))
            throw new UserError("Move Files currently requires source and destination on the same NTFS volume. Nothing was copied or deleted.");
    }
    private static bool RenameMove(SafeFileHandle source, MoveDirectoryLease directory, string fileName, out int error)
    {
        // Relative to the held destination handle: a path could be redirected if an empty
        // held folder were turned into a mount point between the check and the rename.
        directory.Verify();
        return RenameInto(source, directory.Handle, fileName, out error);
    }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveVolumeInformation(SafeFileHandle handle, StringBuilder? volumeName, int volumeNameSize, out uint serial, out uint maxComponent, out uint flags, StringBuilder fileSystem, int fileSystemSize);
}

public sealed class MoveSession : IDisposable
{
    private readonly List<NativeFiles.MoveDirectoryLease> directories = new();
    private readonly List<NativeFiles.MoveLease> leases = new();
    private bool executed, disposed;
    public IReadOnlyList<MovePair> Plan { get; private set; } = Array.Empty<MovePair>();
    public IReadOnlyList<FileStamp> Stamps => leases.Select(x => x.FileStamp).ToArray();
    public IReadOnlyList<string> StoredNames => leases.Select(x => x.StoredName).ToArray();
    /// The held destination folder of a move (not of an undo, which may return to several).
    public MoveIdentity? DestinationIdentity { get; private set; }
    private MoveSession() { }
    private bool allowFat;
    /// Whether the files are on an exFAT or FAT32 drive (Undo then needs kept references).
    public bool IsFat => directories.Any(x => x.IsFat);
    private NativeFiles.MoveDirectoryLease Directory(string path, CancellationToken token)
    {
        path = MoveRules.DirectoryPath(path);
        var found = directories.FirstOrDefault(x => x.Snapshot.Path == path);
        if (found is not null) return found;
        var added = NativeFiles.MoveDirectoryLease.Open(path, token, allowFat); directories.Add(added); return added;
    }
    /// `allowFat`: also within one exFAT or FAT32 drive (the app's own Move Files, not agents).
    public static MoveSession Open(IReadOnlyList<string> suppliedPaths, string destinationDirectory, CancellationToken token, bool allowFat = false)
    {
        token.ThrowIfCancellationRequested();
        if (suppliedPaths is null || suppliedPaths.Count is < 1 or > MoveRules.MaxFiles)
            throw new UserError("Choose 1–100 distinct files to move.");
        var paths = suppliedPaths.ToArray(); destinationDirectory = MoveRules.DirectoryPath(destinationDirectory);
        MoveRules.Validate(paths.Select(path => new MovePair(path, MoveRules.Destination(path, destinationDirectory))).ToArray());
        var session = new MoveSession { allowFat = allowFat };
        try
        {
            var destination = session.Directory(destinationDirectory, token); var plan = new List<MovePair>();
            foreach (string path in paths)
            {
                token.ThrowIfCancellationRequested(); var source = session.Directory(MoveRules.Parent(path), token);
                var lease = NativeFiles.MoveLease.Open(path, source, destination); session.leases.Add(lease);
                plan.Add(new(path, source.Snapshot.LeafIdentity == destination.Snapshot.LeafIdentity ? path : MoveRules.Destination(path, destinationDirectory)));
            }
            session.Plan = plan.AsReadOnly(); session.DestinationIdentity = destination.Snapshot.LeafIdentity; session.Preflight(token); return session;
        }
        catch (Exception error) { session.DisposeAfterFailure(error); throw; }
    }
    public static MoveSession ForUndo(IReadOnlyList<MoveLocation> suppliedReceipt, CancellationToken token, bool allowFat = false)
    {
        token.ThrowIfCancellationRequested(); var receipt = MoveReceipts.ForUndo(suppliedReceipt); var session = new MoveSession { allowFat = allowFat };
        try
        {
            var plan = new List<MovePair>();
            foreach (var entry in receipt)
            {
                token.ThrowIfCancellationRequested();
                var source = session.Directory(entry.CurrentDirectory.Path, token); var destination = session.Directory(entry.OriginalDirectory.Path, token);
                if (!MoveRules.SameDirectory(entry.CurrentDirectory, source.Snapshot) || !MoveRules.SameDirectory(entry.OriginalDirectory, destination.Snapshot))
                    throw new UserError("A source or destination folder changed. Undo was refused without moving any file.");
                ulong? fatToken = source.IsFat && entry.Stamp!.Value.Identity.High == NativeFiles.FatToken ? entry.Stamp.Value.Identity.Low : null;
                if (source.IsFat && fatToken is null) throw new UserError(FatRules.Released);
                var lease = NativeFiles.MoveLease.Open(entry.Current, source, destination, fatToken); session.leases.Add(lease);
                if (lease.VerifiedStamp != entry.Stamp)
                    throw new UserError("The recorded file identity or metadata changed. The move was refused; create a new preview.");
                plan.Add(new(entry.Current, entry.Original));
            }
            session.Plan = plan.AsReadOnly(); session.Preflight(token); return session;
        }
        catch (Exception error) { session.DisposeAfterFailure(error); throw; }
    }
    private void Preflight(CancellationToken token)
    {
        MoveRules.Validate(Plan);
        if (leases.Select(x => x.VerifiedStamp!.Value.Identity).Distinct().Count() != leases.Count)
            throw new UserError("Two of the chosen paths are names of the same file (hard links). Move them separately.");
        foreach (var lease in leases) { token.ThrowIfCancellationRequested(); lease.Validate(); }
        for (int i = 0; i < leases.Count; i++)
        { token.ThrowIfCancellationRequested(); if (Plan[i].Changed) leases[i].EnsureDestinationFree(Plan[i].Destination); }
        token.ThrowIfCancellationRequested();
    }
    public MoveOutcome Execute(CancellationToken token)
    {
        if (disposed) throw new ObjectDisposedException(nameof(MoveSession));
        if (executed) throw new InvalidOperationException("A move preview can only be executed once.");
        executed = true; var outcome = MoveTransaction.Execute(leases, Plan, token);
        // exFAT/FAT32: a reference to each file not in its starting place, for Undo.
        for (int i = 0; i < leases.Count; i++)
            if (!string.Equals(leases[i].CurrentPath, Plan[i].Source, StringComparison.Ordinal))
                try { leases[i].HoldForUndo(); } catch (Exception error) when (error is System.ComponentModel.Win32Exception or UserError) { /* Undo then reports the reference as released. */ }
        return outcome;
    }
    private void DisposeAfterFailure(Exception original)
    {
        try { Dispose(); } catch (Exception cleanup) { throw new AggregateException(original, cleanup); }
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; List<Exception>? errors = null;
        for (int i = leases.Count - 1; i >= 0; i--)
            try { leases[i].Dispose(); } catch (Exception error) { (errors ??= new()).Add(error); }
        leases.Clear();
        for (int i = directories.Count - 1; i >= 0; i--)
            try { directories[i].Dispose(); } catch (Exception error) { (errors ??= new()).Add(error); }
        directories.Clear(); if (errors is not null) throw new AggregateException(errors);
    }
}
