using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;

namespace Swivel.Core;

/// One file or folder inside the chosen folder. EntryName is the ZIP name (with the chosen
/// folder's own name first, '/' between parts, a trailing '/' for folders); RelativePath is the
/// Windows path below the chosen folder.
public sealed record FolderZipItem(string EntryName, string RelativePath, bool Directory, long Length, long LastWrite);
public sealed record FolderZipSkip(string RelativePath, string Reason);
public sealed record FolderZipPlan(string Name, IReadOnlyList<FolderZipItem> Items, IReadOnlyList<FolderZipSkip> Skipped, int Files, int Folders, long Bytes);

/// Compress Folder to ZIP (as Mac's ZIP of a dropped folder): the whole tree under one folder,
/// in a ZIP named after it beside it. Links, junctions, cloud placeholders and system items are
/// never followed or read; they are listed as skipped. Bounded like extraction.
public static class FolderZip
{
    public const int MaxEntries = 10_000, MaxDepth = 32;
    public const long MaxBytes = Archives.MaxBytes;
    public const string Notice = "The ZIP holds the folder itself and everything inside it, with each file's last modified time. Links, junctions, cloud-only files and system files are skipped and listed. File attributes, permissions and alternate data streams are not kept.";
    public const string TooMany = "A folder ZIP is limited to 10,000 files and folders.";
    public const string TooLarge = "A folder ZIP is limited to 2 GB of files.";
    public const string TooDeep = "A folder ZIP is limited to 32 levels of folders.";
    public const string Changed = "A file changed while it was being compressed. Nothing was saved.";

    public static FolderZipPlan Plan(IFolderMetadata root, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string name = root.Name;
        WindowsPathPolicy.ValidateName(name);
        var items = new List<FolderZipItem> { new(name + "/", "", true, 0, 0) };
        var skipped = new List<FolderZipSkip>();
        int files = 0, folders = 0; long bytes = 0;
        void Walk(IFolderMetadata folder, string relative, string entry, int depth)
        {
            // Names in one folder are written in a stable order, folders and files mixed.
            var entries = new List<FolderEntry>();
            foreach (var child in folder.Entries(token))
            {
                if (child.Name is "." or "..") continue;
                entries.Add(child);
                if (entries.Count + items.Count > MaxEntries + 1) throw new UserError(TooMany);
            }
            entries.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            foreach (var child in entries)
            {
                token.ThrowIfCancellationRequested();
                WindowsPathPolicy.ValidateName(child.Name);
                string path = relative.Length == 0 ? child.Name : relative + "\\" + child.Name;
                string reason = (child.Attributes & 0x400) != 0 ? "Link or junction (not followed)"
                    : (child.Attributes & (0x1000 | 0x40000 | 0x400000)) != 0 ? "Cloud-only or offline file (not downloaded)"
                    : (child.Attributes & 0x4) != 0 ? "System file"
                    : (child.Attributes & 0x40) != 0 || child.Bytes < 0 || child.Directory != ((child.Attributes & 0x10) != 0) ? "Not a regular file or folder" : "";
                if (reason.Length > 0) { skipped.Add(new(path, reason)); continue; }
                if (items.Count >= MaxEntries + 1) throw new UserError(TooMany);
                if (child.Directory)
                {
                    if (depth + 1 > MaxDepth) throw new UserError(TooDeep);
                    items.Add(new(entry + child.Name + "/", path, true, 0, child.LastWrite)); folders++;
                    using var inner = folder.OpenChild(child.Name);
                    Walk(inner, path, entry + child.Name + "/", depth + 1);
                }
                else
                {
                    if (child.Bytes > MaxBytes - bytes) throw new UserError(TooLarge);
                    bytes += child.Bytes; files++;
                    items.Add(new(entry + child.Name, path, false, child.Bytes, child.LastWrite));
                }
            }
        }
        Walk(root, "", name + "/", 1);
        return new(name, items, skipped, files, folders, bytes);
    }

    /// Writes the planned items. Each file is opened through `open`, which checks it is still
    /// the planned regular file of the planned length; after copying, its identity, length and
    /// modified time must be unchanged, otherwise nothing is saved.
    public static void Write(FolderZipPlan plan, Func<FolderZipItem, IDuplicateInput> open, Stream output, CancellationToken token, Action<int, int>? progress = null)
    {
        token.ThrowIfCancellationRequested();
        using var buffered = new BufferedStream(output, 1 << 20);
        using (var zip = new ZipArchive(buffered, ZipArchiveMode.Create, true, Encoding.UTF8))
        {
            int done = 0;
            foreach (var item in plan.Items)
            {
                token.ThrowIfCancellationRequested();
                var entry = zip.CreateEntry(item.EntryName, item.Directory ? CompressionLevel.NoCompression : CompressionLevel.Fastest);
                entry.LastWriteTime = Time(item.LastWrite);
                if (item.Directory) continue;
                using (var input = open(item))
                {
                    var before = input.Stamp;
                    if (before.Length != item.Length) throw new UserError(Changed);
                    input.Stream.Position = 0;
                    using (var destination = entry.Open())
                    {
                        var copy = new byte[81920]; long total = 0; int read;
                        while ((read = input.Stream.Read(copy, 0, copy.Length)) > 0)
                        {
                            token.ThrowIfCancellationRequested();
                            total += read;
                            if (total > item.Length) throw new UserError(Changed);
                            destination.Write(copy, 0, read);
                        }
                        if (total != item.Length) throw new UserError(Changed);
                    }
                    if (input.Refresh() != before) throw new UserError(Changed);
                }
                progress?.Invoke(++done, plan.Files);
            }
        }
        buffered.Flush();
        token.ThrowIfCancellationRequested();
    }

    /// ZIP stores local DOS times from 1980 to 2107; anything outside (or unknown) becomes 1 January 1980.
    public static DateTimeOffset Time(long fileTime)
    {
        var floor = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        if (fileTime <= 0 || fileTime > DateTime.MaxValue.ToFileTimeUtc()) return floor;
        var time = DateTime.FromFileTimeUtc(fileTime).ToLocalTime();
        return time.Year is < 1980 or > 2107 ? floor : new DateTimeOffset(time);
    }
}
