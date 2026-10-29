using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using Swivel.Core;

internal static class FolderZipChecks
{
    private sealed class Node(string name, bool directory, byte[]? data = null, uint attributes = 0, long lastWrite = 0, Node[]? children = null) : IFolderMetadata
    {
        public string Name => name;
        public bool Directory => directory;
        public byte[] Data = data ?? Array.Empty<byte>();
        public uint Attributes => attributes | (directory ? 0x10u : 0x20u);
        public long LastWrite => lastWrite;
        public Node[] Children = children ?? Array.Empty<Node>();
        public int Open, Closed;
        public IEnumerable<FolderEntry> Entries(CancellationToken token)
        {
            yield return new(".", true, 0, 0x10); yield return new("..", true, 0, 0x10);
            foreach (var child in Children) yield return new(child.Name, child.Directory, child.Directory ? 0 : child.Data.Length, child.Attributes, child.LastWrite);
        }
        public IFolderMetadata OpenChild(string childName) { var child = Children.Single(c => c.Name == childName && c.Directory); child.Open++; return child; }
        public void Dispose() => Closed++;
        public Node Find(string relative) => relative.Split('\\').Aggregate(this, (node, part) => node.Children.Single(c => c.Name == part));
    }

    private sealed class Input(Node node, Action? whileReading = null) : IDuplicateInput
    {
        private readonly DuplicateStamp stamp = new(node.Name, node.Data.Length, node.LastWrite);
        public Stream Stream { get; } = new Reading(node.Data, whileReading);
        public DuplicateStamp Stamp => stamp;
        public DuplicateStamp Refresh() => new(node.Name, node.Data.Length, node.LastWrite);
        public void Dispose() => Stream.Dispose();
    }

    private sealed class Reading(byte[] data, Action? whileReading) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) { whileReading?.Invoke(); return base.Read(buffer, offset, count); }
    }

    private static readonly long March2024 = new DateTime(2024, 3, 5, 12, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();

    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        var big = new byte[200_000]; new Random(5).NextBytes(big);
        Node Tree() => new("项目 Notes", true, children: new[]
        {
            new Node("b.txt", false, Encoding.UTF8.GetBytes("bravo"), lastWrite: March2024),
            new Node("A", true, children: new[] { new Node("deep", true, children: new[] { new Node("big.bin", false, big) }), new Node("empty", true) }),
            new Node("hidden.txt", false, Encoding.UTF8.GetBytes("h"), attributes: 0x2),
            new Node("link", true, attributes: 0x400),
            new Node("cloud.docx", false, new byte[10], attributes: 0x400000),
            new Node("desktop.ini", false, new byte[3], attributes: 0x4 | 0x2),
        });
        var root = Tree();
        var plan = FolderZip.Plan(root, default);
        check(plan.Name == "项目 Notes" && plan.Files == 3 && plan.Folders == 3 && plan.Bytes == 5 + 200_000 + 1, $"The plan counts 3 files in 3 folders and their bytes: {plan.Files}/{plan.Folders}/{plan.Bytes}");
        check(plan.Items.Select(i => i.EntryName).SequenceEqual(new[] { "项目 Notes/", "项目 Notes/A/", "项目 Notes/A/deep/", "项目 Notes/A/deep/big.bin", "项目 Notes/A/empty/", "项目 Notes/b.txt", "项目 Notes/hidden.txt" }),
            "Entries start with the folder's own name, folders end in '/', names are in a stable order and hidden files are kept: " + string.Join(", ", plan.Items.Select(i => i.EntryName)));
        check(plan.Items.Single(i => i.EntryName.EndsWith("big.bin")).RelativePath == "A\\deep\\big.bin", "Each item keeps its Windows path below the folder");
        check(plan.Skipped.Select(s => s.RelativePath + ":" + s.Reason).SequenceEqual(new[] { "cloud.docx:Cloud-only or offline file (not downloaded)", "desktop.ini:System file or folder", "link:Link or junction (not followed)" }),
            "Links, cloud-only files and system files are listed as skipped with a reason: " + string.Join(", ", plan.Skipped.Select(s => s.RelativePath + ":" + s.Reason)));
        check(root.Find("link").Open == 0, "A link is never opened");

        string? message;
        var output = new MemoryStream();
        var progress = new List<(int, int)>();
        FolderZip.Write(plan, item => new Input(root.Find(item.RelativePath)), output, default, (done, total) => progress.Add((done, total)));
        using (var zip = new ZipArchive(new MemoryStream(output.ToArray()), ZipArchiveMode.Read))
        {
            check(zip.Entries.Select(e => e.FullName).SequenceEqual(plan.Items.Select(i => i.EntryName)), "The ZIP holds exactly the planned entries in order");
            byte[] Read(string name) { using var stream = zip.GetEntry(name)!.Open(); var memory = new MemoryStream(); stream.CopyTo(memory); return memory.ToArray(); }
            check(Read("项目 Notes/A/deep/big.bin").SequenceEqual(big) && Encoding.UTF8.GetString(Read("项目 Notes/b.txt")) == "bravo", "File contents come back exactly, including a 200 KB file");
            check(zip.GetEntry("项目 Notes/A/empty/")!.Length == 0, "An empty folder is kept as a folder entry");
            var time = zip.GetEntry("项目 Notes/b.txt")!.LastWriteTime.DateTime;
            var expected = DateTime.FromFileTimeUtc(March2024).ToLocalTime();
            check(Math.Abs((time - expected).TotalSeconds) <= 2, $"A file keeps its modified time (to ZIP's two seconds): {time} vs {expected}");
            check(zip.GetEntry("项目 Notes/hidden.txt")!.LastWriteTime.Year == 1980, "An unknown modified time becomes 1 January 1980");
        }
        check(output.CanWrite, "Writing leaves the caller's output stream open for it to flush and publish");
        check(progress.Select(p => p.Item1).SequenceEqual(new[] { 1, 2, 3 }) && progress.All(p => p.Item2 == 3), "Progress counts the files");

        // Names Windows cannot hold, long paths and case-only twins are skipped, not fatal; a folder that cannot be opened is skipped.
        var odd = new Node("odd", true, children: new[]
        {
            new Node("Icon\r", false, new byte[1]), new Node("con.txt", false, new byte[1]), new Node("x", false, new byte[1]), new Node("X", false, new byte[1]),
            new Node(new string('n', 100), true, children: new[] { new Node(new string('m', 100) + ".txt", false, new byte[1]) }),
        });
        var oddPlan = FolderZip.Plan(odd, default, 40);
        check(oddPlan.Files == 1 && oddPlan.Folders == 1 && oddPlan.Skipped.Select(x => x.Reason).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(new[] { "Name not supported on Windows", "Name not supported on Windows", "Path longer than 240 characters", "Same name as another item except for letter case" }),
            "Unsupported names, a path over 240 characters and a case-only twin are skipped with reasons: " + string.Join(", ", oddPlan.Skipped.Select(x => x.RelativePath.Replace("\r", "\\r") + ":" + x.Reason)));
        var locked = Tree(); var lockedPlan = FolderZip.Plan(locked, default);
        message = null;
        try { FolderZip.Write(lockedPlan, item => item.RelativePath == "b.txt" ? throw new System.ComponentModel.Win32Exception(32) : new Input(locked.Find(item.RelativePath)), new MemoryStream(), default); }
        catch (UserError error) { message = error.Message; }
        check(message == "\"b.txt\" is in use by another app or could not be read. Nothing was saved.", "A file in use names itself in the message: " + message);
        var full = new FullDisk(); message = null; Exception? thrown = null;
        try { FolderZip.Write(FolderZip.Plan(Tree(), default), item => new Input(Tree().Find(item.RelativePath)), full, default); } catch (Exception error) { thrown = error; }
        check(thrown is IOException && thrown is not UserError, "A full disk while writing the ZIP is not blamed on one of the user's files: " + thrown?.GetType().Name);
        // A file that changes while being read, or no longer has its planned length, stops the whole ZIP.
        var changing = Tree(); var changingPlan = FolderZip.Plan(changing, default);
        message = null;
        try { FolderZip.Write(changingPlan, item => new Input(changing.Find(item.RelativePath), () => changing.Find("b.txt").Data = Encoding.UTF8.GetBytes("changed!")), new MemoryStream(), default); }
        catch (UserError error) { message = error.Message; }
        check(message == FolderZip.Changed, "A file that changes while it is compressed stops the ZIP with the changed message");
        var grown = Tree(); var grownPlan = FolderZip.Plan(grown, default); grown.Find("b.txt").Data = new byte[6];
        message = null;
        try { FolderZip.Write(grownPlan, item => new Input(grown.Find(item.RelativePath)), new MemoryStream(), default); } catch (UserError error) { message = error.Message; }
        check(message == FolderZip.Changed, "A file whose length changed after planning is refused");
        var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        reject(() => FolderZip.Write(plan, item => new Input(root.Find(item.RelativePath)), new MemoryStream(), cancelled.Token), "A cancelled ZIP stops");

        // Limits: entries, depth, bytes.
        Node Many(int count) => new("many", true, children: Enumerable.Range(0, count).Select(i => new Node("f" + i, false, new byte[1])).ToArray());
        check(FolderZip.Plan(Many(FolderZip.MaxEntries), default).Files == FolderZip.MaxEntries, "10,000 files fit");
        message = null; try { FolderZip.Plan(Many(FolderZip.MaxEntries + 1), default); } catch (UserError error) { message = error.Message; }
        check(message == FolderZip.TooMany, "10,001 files are refused");
        Node Deep(int levels) => levels == 0 ? new Node("leaf.txt", false, new byte[1]) : new Node("d" + levels, true, children: new[] { Deep(levels - 1) });
        check(FolderZip.Plan(new Node("top", true, children: new[] { Deep(FolderZip.MaxDepth - 1) }), default).Folders == FolderZip.MaxDepth - 1, "32 levels of folders fit");
        message = null; try { FolderZip.Plan(new Node("top", true, children: new[] { Deep(FolderZip.MaxDepth) }), default); } catch (UserError error) { message = error.Message; }
        check(message == FolderZip.TooDeep, "33 levels are refused");
        var huge = new Node("huge", true, children: new[] { new Node("a", false), new Node("b", false) });
        var sized = new SizedFolder(huge, FolderZip.MaxBytes / 2 + 1);
        message = null; try { FolderZip.Plan(sized, default); } catch (UserError error) { message = error.Message; }
        check(message == FolderZip.TooLarge, "More than 2 GB of files is refused");
        check(FolderZip.Plan(new SizedFolder(huge, FolderZip.MaxBytes / 2), default).Bytes == FolderZip.MaxBytes, "Exactly 2 GB fits");

        // ZIP times.
        check(FolderZip.Time(0).Year == 1980 && FolderZip.Time(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc()).Year == 1980 && FolderZip.Time(-5).Year == 1980,
            "Unknown or pre-1980 times become 1980");
        check(Catalog.Actions.Any(a => a.Id == "archive.zipfolder" && a.Feature == Feature.Archives && !a.RequiresFiles), "Compress Folder to ZIP is an Archives action that picks its own folder");
    }

    /// Reports every file at the given size without holding the bytes.
    private sealed class SizedFolder(IFolderMetadata inner, long size) : IFolderMetadata
    {
        public string Name => inner.Name;
        public IEnumerable<FolderEntry> Entries(CancellationToken token) => inner.Entries(token).Select(e => e.Directory || e.Name is "." or ".." ? e : e with { Bytes = size });
        public IFolderMetadata OpenChild(string name) => inner.OpenChild(name);
        public void Dispose() { }
    }

    private sealed class FullDisk : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) { if (Length + count > 4096) throw new IOException("There is not enough space on the disk."); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { if (Length + buffer.Length > 4096) throw new IOException("There is not enough space on the disk."); base.Write(buffer); }
    }
}
