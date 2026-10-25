using System;
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

public sealed class ZipOutcome
{
    public string? Folder { get; internal set; }
    public int FilesWritten { get; internal set; }
    public int FileCount { get; init; }
    public Exception? Error { get; internal set; }
    public bool Cancelled { get; internal set; }
}

public static partial class NativeFiles
{
    private const int StagingNameLength = 45, ZoneStreamLength = 16; // "\.swivel-" + 32 hex + ".tmp", ":Zone.Identifier"

    /// The archive's own Mark of the Web, bounded to 4 KiB. Missing means none.
    public static byte[]? ReadZoneIdentifier(Input input)
    {
        SafeFileHandle handle;
        try { handle = Open(input.Path + ":Zone.Identifier", GenericRead, 1, 3, 0); }
        catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { return null; }
        using (handle)
        {
            if (GetFileType(handle) != 1) throw new UserError(LocalOnly);
            using var stream = new FileStream(handle, FileAccess.Read, 1, false);
            byte[] buffer = new byte[4096]; int total = 0, read;
            while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0) total += read;
            return buffer[..total];
        }
    }

    /// Everything that can be checked before the new folder exists: NTFS (for Mark of the
    /// Web), path lengths and free space with a margin. Refusals here leave no trace.
    public static void CheckExtractionTarget(Input input, ZipPlan plan, string stem)
    {
        string parent = System.IO.Path.GetDirectoryName(input.Path)!;
        var fileSystem = new StringBuilder(64);
        if (!GetVolumeInformation(parent[..3], null, 0, out _, out _, out _, fileSystem, fileSystem.Capacity)) ThrowLast();
        if (fileSystem.ToString() != "NTFS") throw new UserError("Archive extraction currently requires an NTFS drive, so extracted files can keep their download warning.");
        int root = parent.TrimEnd('\\').Length + 1 + stem.Length + 6; // worst case " 10000"
        int deepestFolder = plan.Files.Select(file => System.IO.Path.GetDirectoryName(file.Path)!.Length).DefaultIfEmpty(0).Max();
        // Staging files also get a ":Zone.Identifier" stream; paths stay below MAX_PATH without long-path support.
        if (root + 1 + plan.LongestPath > 240 || root + 1 + deepestFolder + StagingNameLength + ZoneStreamLength > 259)
            throw new UserError("Extracted paths would be longer than 240 characters. Move the archive to a shorter folder first.");
        if (!GetDiskFreeSpaceEx(parent.TrimEnd('\\') + "\\", out ulong available, out _, out _)) ThrowLast();
        ulong needed = (ulong)plan.TotalBytes + (ulong)(plan.Files.Count + plan.Directories.Count + 1) * 4096 + 64UL * 1024 * 1024;
        if (available < needed) throw new UserError("There is not enough free space to extract this archive.");
    }

    /// Creates a new folder beside the archive (never an existing one), then publishes each
    /// file exclusively after its size and CRC-32 check. Not a transaction: on a failure or
    /// cancellation, files already published stay and are reported.
    public static ZipOutcome ExtractZip(Input input, ZipPlan plan, int zone, CancellationToken token, Action<int, int>? progress = null)
        => ExtractArchive(input, plan, ZipExtract.FolderStem(System.IO.Path.GetFileName(input.Path)), zone,
            (member, output) => ZipExtract.Extract(input.Stream, member, output, token), token, progress);

    /// Any reviewed plan (ZIP or TAR): write copies one member's exact bytes, in plan order.
    public static ZipOutcome ExtractArchive(Input input, ZipPlan plan, string stem, int zone, Action<ZipMember, Stream> write, CancellationToken token, Action<int, int>? progress = null)
    {
        token.ThrowIfCancellationRequested();
        CheckExtractionTarget(input, plan, stem);
        byte[] zoneData = ZipExtract.ZoneIdentifier(zone);
        string parent = System.IO.Path.GetDirectoryName(input.Path)!.TrimEnd('\\');
        if (!GetFileInformationByHandle(input.Stream.SafeFileHandle, out var source)) ThrowLast();
        var outcome = new ZipOutcome { FileCount = plan.Files.Count };
        var held = new List<SafeFileHandle>();
        var folders = new Dictionary<string, SafeFileHandle>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string? root = null;
            for (int i = 1; i <= 10000 && root is null; i++)
            {
                token.ThrowIfCancellationRequested();
                string candidate = System.IO.Path.Combine(parent.Length == 2 ? parent + "\\" : parent, ZipExtract.FolderName(stem, i));
                WindowsPathPolicy.Validate(candidate);
                if (CreateDirectory(candidate, IntPtr.Zero)) root = candidate;
                else if (Marshal.GetLastWin32Error() is int error && error != 183) throw new Win32Exception(error);
            }
            if (root is null) throw new UserError("Too many files have this output name.");
            outcome.Folder = root;
            folders[""] = HoldCreated(root, source.Volume, held);
            foreach (string directory in plan.Directories)
            {
                token.ThrowIfCancellationRequested();
                string path = System.IO.Path.Combine(root, directory);
                WindowsPathPolicy.Validate(path);
                // The folder is new, so an existing child means something else wrote into it.
                if (!CreateDirectory(path, IntPtr.Zero))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 183) throw new UserError("Something else changed the new folder while extracting. Nothing was overwritten.");
                    throw new Win32Exception(error);
                }
                folders[directory] = HoldCreated(path, source.Volume, held);
            }
            for (int i = 0; i < plan.Files.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var file = plan.Files[i];
                string destination = System.IO.Path.Combine(root, file.Path);
                WindowsPathPolicy.Validate(destination);
                PublishExactly(folders[System.IO.Path.GetDirectoryName(file.Path) ?? ""], destination, output => write(file, output), zoneData, file.Modified, token);
                outcome.FilesWritten++;
                progress?.Invoke(i + 1, plan.Files.Count);
            }
        }
        catch (OperationCanceledException) when (outcome.Folder is not null) { outcome.Cancelled = true; }
        catch (Exception error) when (outcome.Folder is not null && error is not OutOfMemoryException) { outcome.Error = error; }
        finally { for (int i = held.Count - 1; i >= 0; i--) held[i].Dispose(); }
        return outcome;
    }

    // A folder this call created is held without delete sharing, so it cannot be renamed,
    // removed or swapped for a link while files are published into it. List/traverse access
    // is requested because attribute-only opens do not take part in share checks.
    private static SafeFileHandle HoldCreated(string path, uint volume, List<SafeFileHandle> held)
    {
        var handle = Open(path, ReadAttributes | Traverse, 3, 3, OpenReparse | BackupSemantics);
        held.Add(handle);
        Check(handle, true);
        if (!GetFileInformationByHandle(handle, out var info)) ThrowLast();
        if (info.Volume != volume) throw new UserError(LocalOnly);
        return handle;
    }

    private static void PublishExactly(SafeFileHandle parent, string destination, Action<Stream> produce, byte[] zoneData, DateTime? modified, CancellationToken token)
    {
        string directory = System.IO.Path.GetDirectoryName(destination)!;
        string temp = System.IO.Path.Combine(directory, ".swivel-" + Guid.NewGuid().ToString("N") + ".tmp");
        using var handle = Open(temp, GenericWrite | ReadAttributes | DeleteAccess, 0, 1, OpenReparse | 2 | 0x100);
        FileStream? stream = null;
        bool published = false;
        try
        {
            Check(handle, false);
            stream = new FileStream(handle, FileAccess.Write, 1, false); // unbuffered: nothing left to flush after a failed produce
            produce(stream);
            stream.Flush(true);
            token.ThrowIfCancellationRequested();
            // The main stream is held without sharing and with DELETE access, so another stream
            // of the same new file can only be opened with delete sharing.
            using (var zoneHandle = Open(temp + ":Zone.Identifier", GenericWrite, 4, 1, 0))
            using (var zone = new FileStream(zoneHandle, FileAccess.Write, 1, false))
            {
                zone.Write(zoneData); zone.Flush(true);
            }
            var basic = new BasicInfo { Attributes = 0x80, Write = modified is DateTime time ? time.ToFileTimeUtc() : 0 };
            if (!SetBasic(handle, 0, ref basic, (uint)Marshal.SizeOf<BasicInfo>())) ThrowLast();
            token.ThrowIfCancellationRequested();
            if (!RenameInto(handle, parent, System.IO.Path.GetFileName(destination), out int error))
            {
                if (error is 80 or 183) throw new UserError("Something else changed the new folder while extracting. Nothing was overwritten.");
                throw new Win32Exception(error);
            }
            published = true;
        }
        finally
        {
            if (!published)
            {
                // An exception is already propagating; a failed cleanup must not replace it.
                byte delete = 1;
                SetDisposition(handle, 4, ref delete, 1);
            }
            stream?.Dispose(); // closes the handle, so only after the disposition
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateDirectory(string path, IntPtr security);
    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceEx(string directory, out ulong availableToCaller, out ulong total, out ulong free);
}
