using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Swivel.Core;

public sealed record FolderEntry(string Name, bool Directory, long Bytes, uint Attributes);
public interface IFolderMetadata : IDisposable
{
    string Name { get; }
    IEnumerable<FolderEntry> Entries(CancellationToken token);
    IFolderMetadata OpenChild(string name);
}
public sealed class FolderSizeNode(string name, bool directory)
{
    public string Name { get; } = name;
    public bool Directory { get; } = directory;
    public decimal Bytes { get; internal set; }
    public int Files { get; internal set; }
    public int Skipped { get; internal set; }
    public bool Incomplete { get; internal set; }
    public bool Aggregate { get; internal set; }
    public string? Issue { get; internal set; }
    public IReadOnlyList<FolderSizeNode> Children { get; internal set; } = Array.Empty<FolderSizeNode>();
}
public sealed record FolderScanResult(FolderSizeNode Root, int Entries, bool EntryLimit, bool TimeLimit);
public sealed record FolderScanLimits(int MaxEntries = 20000, int MaxDepth = 4, int Seconds = 30);

public static class FolderSizes
{
    // System/device, reparse, offline and recall flags. Enumeration is metadata
    // only; skipped entries are never opened to read contents or follow targets.
    public const uint SkippedAttributes = 0x4 | 0x40 | 0x400 | 0x1000 | 0x40000 | 0x400000;
    public static FolderScanResult Scan(IFolderMetadata root, CancellationToken token, FolderScanLimits? limits = null, Func<TimeSpan>? elapsed = null)
    {
        limits ??= new();
        if (limits.MaxEntries is < 1 or > 20000 || limits.MaxDepth is < 0 or > 4 || limits.Seconds is < 1 or > 30) throw new ArgumentOutOfRangeException(nameof(limits));
        var watch = System.Diagnostics.Stopwatch.StartNew(); elapsed ??= () => watch.Elapsed;
        int entries = 0; bool entryLimit = false, timeLimit = false;
        bool Stop()
        {
            token.ThrowIfCancellationRequested();
            if (elapsed().TotalSeconds >= limits.Seconds) timeLimit = true;
            return entryLimit || timeLimit;
        }
        FolderSizeNode Visit(IFolderMetadata directory, int depth)
        {
            var node = new FolderSizeNode(directory.Name, true); var children = new List<FolderSizeNode>();
            try
            {
                if (Stop()) { node.Incomplete = true; return node; }
                foreach (var entry in directory.Entries(token))
                {
                    if (entry.Name is "." or "..") continue;
                    if (Stop()) { node.Incomplete = true; break; }
                    if (entries == limits.MaxEntries) { entryLimit = node.Incomplete = true; break; }
                    entries++;
                    void Skip(string issue) => children.Add(new(entry.Name, entry.Directory) { Skipped = 1, Incomplete = true, Issue = issue });
                    try
                    {
                        WindowsPathPolicy.ValidateName(entry.Name);
                        if ((entry.Attributes & SkippedAttributes) != 0 || entry.Bytes < 0 || entry.Directory != ((entry.Attributes & 0x10) != 0))
                        { Skip("System, link, cloud or invalid item"); continue; }
                        if (!entry.Directory) children.Add(new(entry.Name, false) { Bytes = entry.Bytes, Files = 1 });
                        else if (depth >= limits.MaxDepth) { Skip("Depth limit"); }
                        else
                        {
                            using var child = directory.OpenChild(entry.Name);
                            children.Add(Visit(child, depth + 1));
                        }
                    }
                    catch (Exception ex) when (Expected(ex)) { Skip("Unavailable or unsupported"); }
                    if (Stop()) { node.Incomplete = true; break; }
                }
            }
            catch (Exception ex) when (Expected(ex)) { node.Skipped++; node.Incomplete = true; }
            token.ThrowIfCancellationRequested();
            node.Children = children.AsReadOnly(); node.Bytes = children.Sum(x => x.Bytes); node.Files = children.Sum(x => x.Files);
            node.Skipped += children.Sum(x => x.Skipped); node.Incomplete |= children.Any(x => x.Incomplete);
            return node;
        }
        var result = Visit(root, 0); token.ThrowIfCancellationRequested();
        return new(result, entries, entryLimit, timeLimit);
    }
    private static bool Expected(Exception error) => error is IOException or UnauthorizedAccessException or Win32Exception or UserError or NotSupportedException;

    // A bounded current view. "Other Items" is a navigable aggregate, so every
    // counted child remains reachable without constructing 20000 native controls.
    public static IReadOnlyList<FolderSizeNode> Display(FolderSizeNode node, int max = 24)
    {
        if (max is < 2 or > 24) throw new ArgumentOutOfRangeException(nameof(max));
        var sorted = node.Children.OrderByDescending(x => x.Bytes).ThenBy(x => x.Name, StringComparer.Ordinal).ToArray();
        if (sorted.Length <= max) return sorted;
        var rest = sorted.Skip(max - 1).ToArray();
        var other = new FolderSizeNode("Other Items", true) { Aggregate = true, Children = rest, Bytes = rest.Sum(x => x.Bytes), Files = rest.Sum(x => x.Files), Skipped = rest.Sum(x => x.Skipped), Incomplete = rest.Any(x => x.Incomplete) };
        return sorted.Take(max - 1).Append(other).ToArray();
    }
}

// FILE_FULL_DIR_INFO (Windows 8+): variable UTF-16 names, 8-byte aligned next
// records, filename at byte 68. Validate native lengths before reading strings.
public static class FolderDirectoryBuffer
{
    private static readonly Encoding Utf16 = new UnicodeEncoding(false, false, true);
    public static IReadOnlyList<FolderEntry> Parse(ReadOnlySpan<byte> buffer)
    {
        var entries = new List<FolderEntry>(); int at = 0;
        for (;;)
        {
            if (buffer.Length - at < 70) throw new InvalidDataException("Invalid directory record.");
            var item = buffer[at..]; uint next = BinaryPrimitives.ReadUInt32LittleEndian(item), nameBytes = BinaryPrimitives.ReadUInt32LittleEndian(item[60..]);
            if (nameBytes is < 2 or > 510 || (nameBytes & 1) != 0 || nameBytes > item.Length - 68) throw new InvalidDataException("Invalid directory name length.");
            if (next != 0 && ((next & 7) != 0 || next < 68 + nameBytes || next > item.Length - 70)) throw new InvalidDataException("Invalid directory record offset.");
            string name;
            try { name = Utf16.GetString(item.Slice(68, (int)nameBytes)); }
            catch (DecoderFallbackException ex) { throw new InvalidDataException("Invalid directory name encoding.", ex); }
            uint attributes = BinaryPrimitives.ReadUInt32LittleEndian(item[56..]);
            long bytes = BinaryPrimitives.ReadInt64LittleEndian(item[40..]);
            entries.Add(new(name, (attributes & 0x10) != 0, bytes, attributes));
            if (next == 0) return entries;
            at += (int)next;
        }
    }
}

public readonly record struct FolderTile(int Index, double X, double Y, double Width, double Height);
public static class FolderMapLayout
{
    // Original balanced binary area subdivision. Only metadata sizes influence
    // geometry. Zero-byte items remain in the native list, not in the area chart.
    public static IReadOnlyList<FolderTile> Build(IReadOnlyList<FolderSizeNode> nodes, double width, double height)
    {
        if (nodes.Count > 24 || !double.IsFinite(width) || !double.IsFinite(height) || width < 0 || height < 0) throw new ArgumentOutOfRangeException(nameof(width));
        var visible = nodes.Select((node, index) => (node.Bytes, Index: index)).Where(x => x.Bytes > 0).ToArray();
        var tiles = new List<FolderTile>();
        void Split(int first, int end, double x, double y, double w, double h)
        {
            if (first == end || w <= 0 || h <= 0) return;
            if (end - first == 1) { tiles.Add(new(visible[first].Index, x, y, w, h)); return; }
            decimal total = 0, running = 0; for (int i = first; i < end; i++) total += visible[i].Bytes;
            int middle = first;
            do { running += visible[middle++].Bytes; } while (middle < end - 1 && running < total / 2);
            double share = (double)(running / total);
            if (w >= h) { double cut = w * share; Split(first, middle, x, y, cut, h); Split(middle, end, x + cut, y, w - cut, h); }
            else { double cut = h * share; Split(first, middle, x, y, w, cut); Split(middle, end, x, y + cut, w, h - cut); }
        }
        Split(0, visible.Length, 0, 0, width, height); return tiles;
    }
}
