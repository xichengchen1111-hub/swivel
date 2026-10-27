using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Swivel.Core;

internal static class LargeFileChecks
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        FolderScanResult Scan(Fixture root, FolderScanLimits? limits = null) => FolderSizes.Scan(root, default, limits);
        LargeFileResult Find(Fixture root, FolderScanLimits? limits = null) => LargeFiles.Find(Scan(root, limits), default);
        var ordinary = Find(D("chosen", F("small", 5), F("empty", 0), F("B", 100), F("a", 100), D("folder.exe", F("nested.iso", 70))));
        check(ordinary.Files.Select(x => x.RelativePath).SequenceEqual(new[] { "B", "a", "folder.exe\\nested.iso", "small", "empty" }), "Large files use descending logical size, ordinal ties and nested relative paths");
        check(ordinary.KnownFiles == 5 && ordinary.KnownBytes == 275 && !ordinary.Incomplete && ordinary.Issues.Count == 0, "Known total is every recorded ordinary file, including zero bytes, without counting directories");
        check(ordinary.Files.Count(x => x.InstallerHint) == 1, "Directory names with installer-like suffixes are not files or installer hints");
        check(LargeFiles.Filter(ordinary, 0, false, default).SequenceEqual(ordinary.Files), "All threshold preserves zero-byte files and complete ordering");
        check(LargeFiles.Filter(ordinary, 100, false, default).Select(x => x.RelativePath).SequenceEqual(new[] { "B", "a" }), "Minimum length comparison is inclusive");
        check(LargeFiles.Filter(ordinary, 71, true, default).Count == 0 && ordinary.Files.Count == 5 && ordinary.KnownBytes == 275, "Empty installer-only filter never mutates the saved snapshot or known totals");
        check(LargeFiles.Filter(ordinary, 0, true, default).Single().RelativePath == "folder.exe\\nested.iso", "Installer filter keeps only exact suffix hints");
        reject(() => ((IList<LargeFile>)ordinary.Files).Add(new("injected", 99, false)), "Returned file collection cannot be mutated");
        reject(() => ((IList<LargeFileIssue>)ordinary.Issues).Add(new(".", "injected")), "Returned issue collection cannot be mutated");
        reject(() => ((IList<LargeFile>)LargeFiles.Filter(ordinary, 0, false, default))[0] = new("injected", 0, false), "Filtered result cannot replace saved rows");

        foreach (string extension in new[] { "exe", "msi", "msix", "msixbundle", "appx", "appxbundle", "cab", "iso", "img" })
        {
            foreach (string name in new[] { "setup." + extension, "照片 🦜." + extension.ToUpperInvariant(), "." + extension })
                check(Find(D("root", F(name))).Files.Single().InstallerHint, "Filename-only hint supports an exact case-insensitive final suffix: " + name);
            foreach (string name in new[] { "setup." + extension + ".bak", "setup." + extension + "extra", "setup" + extension })
                check(!Find(D("root", F(name))).Files.Single().InstallerHint, "No prefix, embedded extension or fuzzy installer hint: " + name);
        }
        foreach (string name in new[] { "archive.zip", "README", "download.dmg", "setup.ınstaller", "firmware.bin", "app.exé", "disk.ｉｓｏ" })
            check(!Find(D("root", F(name))).Files.Single().InstallerHint, "Unlisted suffix is not installer or disk-image evidence: " + name);
        foreach (long threshold in new long[] { 10_000_000, 100_000_000, 500_000_000, 1_000_000_000, long.MaxValue })
        {
            var boundary = Find(D("root", F("below", threshold - 1), F("equal.msi", threshold)));
            check(LargeFiles.Filter(boundary, threshold, false, default).Single().RelativePath == "equal.msi", "Threshold boundaries use exact decimal-byte lengths, not rounded display sizes");
            check(LargeFiles.Filter(boundary, threshold, true, default).Single().Bytes == threshold, "Installer and inclusive byte predicates compose without precision loss");
        }
        var huge = Find(D("root", F("hardlink-name-one", long.MaxValue), F("hardlink-name-two", long.MaxValue), F("third", 1)));
        check(huge.KnownFiles == 3 && huge.KnownBytes == 2m * long.MaxValue + 1, "Decimal totals cannot overflow long or deduplicate unknown hard-link identities");
        check(LargeFiles.Filter(huge, long.MaxValue, false, default).Count == 2, "Repeated metadata lengths retain separate paths instead of inventing identity knowledge");

        foreach (uint flag in new uint[] { 0x4, 0x40, 0x400, 0x1000, 0x40000, 0x400000 })
        {
            var unavailable = Find(D("root", F("known", 5), F("unavailable.exe", 999, flag)));
            check(unavailable.KnownBytes == 5 && unavailable.KnownFiles == 1 && unavailable.Incomplete && unavailable.Issues.Single().RelativePath == "unavailable.exe" && unavailable.Issues[0].Reason == "System, link, cloud or invalid item", "Unknown system/device/link/offline/recall file sizes remain skips, never zero-byte known files");
            check(LargeFiles.Filter(unavailable, 1000, true, default).Count == 0 && unavailable.Issues.Count == 1, "Filtering all rows does not erase metadata skip reasons");
        }
        var malformed = Find(D("root", F("negative", -1), F("wrong-kind", 9, 0x10), F("invalid/name", 4)));
        check(malformed.Files.Count == 0 && malformed.KnownBytes == 0 && malformed.Issues.Count == 3 && malformed.Incomplete, "Malformed metadata and unsafe names cannot enter known file rows");
        var deniedChild = D("denied", F("unread", 90)); deniedChild.Denied = true;
        var denied = Find(D("root", F("known", 12), deniedChild));
        check(denied.KnownBytes == 12 && denied.Issues.Single() == new LargeFileIssue("denied", "Unavailable or unsupported") && denied.Incomplete, "Unavailable child subtree keeps its path and reason without claiming its total");
        var failedRoot = D("root", F("unread", 100)); failedRoot.FailAt = 0;
        var rootFailure = Find(failedRoot);
        check(rootFailure.KnownFiles == 0 && rootFailure.KnownBytes == 0 && rootFailure.Incomplete && rootFailure.Issues.Single() == new LargeFileIssue(".", "Folder enumeration incomplete"), "Root enumeration failure is explicitly partial rather than a clean empty folder");
        var partialChild = D("partial", F("seen.exe", 17), F("unread", 100)); partialChild.FailAt = 1;
        var partial = Find(D("root", partialChild, F("also-seen", 19)));
        check(partial.KnownFiles == 2 && partial.KnownBytes == 36 && partial.Incomplete && partial.Issues.Single() == new LargeFileIssue("partial", "Folder enumeration incomplete"), "Completed prefix survives child enumeration failure with one local issue and no invented parent failure");
        var partialRoot = D("root", F("bad", 900, 0x400), partialChild, F("unread", 800)); partialRoot.FailAt = 2;
        var severalFailures = Find(partialRoot);
        check(severalFailures.Issues.Select(x => x.RelativePath).SequenceEqual(new[] { ".", "bad", "partial" }) && severalFailures.KnownBytes == 17, "Local and descendant failure counts remain separately visible in ordinal issue order");
        var depth = Find(D("root", F("known", 2), D("deeper", F("unread", 90))), new(MaxDepth: 0));
        check(depth.KnownBytes == 2 && depth.Issues.Single() == new LargeFileIssue("deeper", "Depth limit") && depth.Incomplete, "Depth-limited directory remains an unknown-size omission");
        var chain = D("below", F("unread", 77));
        for (int i = 4; i >= 0; i--) chain = D("level" + i, F("local" + i, i), chain);
        var chainResult = Find(chain);
        check(chainResult.KnownFiles == 5 && chainResult.KnownBytes == 10 && chainResult.Issues.Single().Reason == "Depth limit", "Original root plus four-level scanner bound remains unchanged");
        var limited = Find(D("root", F("known", 3), F("unseen", 90)), new(MaxEntries: 1));
        check(limited.KnownFiles == 1 && limited.KnownBytes == 3 && limited.Incomplete && limited.Issues.Single() == new LargeFileIssue(".", "Scan entry limit"), "Entry budget preserves known prefix and explicit global omission");
        var exact = Find(D("root", F("known", 3)), new(MaxEntries: 1));
        check(!exact.Incomplete && exact.Issues.Count == 0, "Exactly exhausting a complete directory does not invent a limit failure");
        var timed = LargeFiles.Find(FolderSizes.Scan(D("root", F("unread", 90)), default, elapsed: () => TimeSpan.FromSeconds(30)), default);
        check(timed.KnownFiles == 0 && timed.Incomplete && timed.Issues.Single() == new LargeFileIssue(".", "Scan time limit"), "Time-limited empty snapshot does not imply a completed scan");
        int ticks = 0;
        var timedPrefix = LargeFiles.Find(FolderSizes.Scan(D("root", F("known", 13), F("unread", 90)), default, new(Seconds: 1), () => TimeSpan.FromMilliseconds(++ticks * 300)), default);
        check(timedPrefix.KnownBytes == 13 && timedPrefix.Incomplete && timedPrefix.Issues.Single().Reason == "Scan time limit", "Time-limit prefix retains its known file without promoting unknown remainder");

        var manyScan = Scan(D("root", Enumerable.Range(0, 20000).Reverse().Select(i => F($"file{i:D5}.exe", 1)).ToArray()));
        var many = LargeFiles.Find(manyScan, default);
        check(many.Files.Count == 20000 && many.KnownFiles == 20000 && many.KnownBytes == 20000 && !many.Incomplete && many.Files.Select(x => x.RelativePath).SequenceEqual(Enumerable.Range(0, 20000).Select(i => $"file{i:D5}.exe")), "Every matching file at the 20K scan bound is retained and tie-sorted, never a top-N truncation");
        check(LargeFiles.Filter(many, 1, true, default).Count == 20000, "Filtering retains all 20K matching rows");
        var aggregate = FolderSizes.Display(manyScan.Root)[^1];
        var aggregateResult = LargeFiles.Find(new(aggregate, manyScan.Entries, false, false), default);
        check(aggregate.Aggregate && aggregateResult.Files.Count == 0 && aggregateResult.KnownBytes == 0, "Synthetic display aggregates cannot manufacture real file paths or double-count children");
        var random = new Random(1818);
        for (int run = 0; run < 50; run++)
        {
            var raw = Enumerable.Range(0, 100).Select(i => (Name: $"f{i:D3}" + (i % 3 == 0 ? ".msi" : ".dat"), Bytes: random.NextInt64(0, 2_000_000_001))).ToArray();
            var result = Find(D("root", raw.Reverse().Select(x => F(x.Name, x.Bytes)).ToArray()));
            check(result.KnownBytes == raw.Sum(x => (decimal)x.Bytes) && result.KnownFiles == raw.Length, "Random metadata totals equal independent decimal accounting");
            foreach (long minimum in new long[] { 0, 10_000_000, 100_000_000, 500_000_000, 1_000_000_000 })
            {
                foreach (bool only in new[] { false, true })
                {
                    var expected = raw.Where(x => x.Bytes >= minimum && (!only || x.Name.EndsWith(".msi", StringComparison.Ordinal)))
                        .OrderByDescending(x => x.Bytes).ThenBy(x => x.Name, StringComparer.Ordinal).Select(x => x.Name);
                    check(LargeFiles.Filter(result, minimum, only, default).Select(x => x.RelativePath).SequenceEqual(expected), "Random filtered rows match an independent source-entry oracle");
                }
            }
        }

        reject(() => LargeFiles.Find(null!, default), "Missing scanner result refused");
        reject(() => LargeFiles.Filter(null!, 0, false, default), "Missing file result refused");
        reject(() => LargeFiles.Filter(ordinary, -1, false, default), "Negative minimum size refused");
        MustCancel(() => LargeFiles.Find(manyScan, new(true)), "Precancelled classification propagates cancellation");
        MustCancel(() => LargeFiles.Filter(many, 0, false, new(true)), "Precancelled filter propagates cancellation");
        using (var cancellation = new CancellationTokenSource())
        {
            var triggeringFiles = new CancellingList<LargeFile>(many.Files, cancellation, 37);
            MustCancel(() => LargeFiles.Filter(many with { Files = triggeringFiles }, 0, false, cancellation.Token), "Cancellation during filtering does not return a partial success");
            check(triggeringFiles.Visited == 38, "Filter observes cancellation at the current row instead of finishing the full list");
        }
        using (var cancellation = new CancellationTokenSource())
        {
            var traversalScan = Scan(D("root", Enumerable.Range(0, 100).Select(i => F("f" + i, i)).ToArray()));
            var triggeringNodes = new CancellingList<FolderSizeNode>(traversalScan.Root.Children, cancellation, 37);
            // Inject an enumeration hook into an otherwise real scanner result;
            // this makes mid-traversal cancellation deterministic, with no race.
            typeof(FolderSizeNode).GetProperty(nameof(FolderSizeNode.Children))!.SetValue(traversalScan.Root, triggeringNodes);
            MustCancel(() => LargeFiles.Find(traversalScan, cancellation.Token), "Cancellation during snapshot traversal does not return a partial success");
            check(triggeringNodes.Visited == 38, "Classification checks cancellation inside metadata traversal");
        }
        var prefs = new Prefs();
        check(!Catalog.Available(prefs, Array.Empty<string>()).Any(x => x.Id == "doctor.largefiles"), "Large-file scan is opt-in off by default");
        prefs.Set(Feature.Doctor, true);
        check(Catalog.Available(prefs, Array.Empty<string>()).Any(x => x.Id == "doctor.largefiles" && !x.RequiresFiles), "Large-file action chooses its explicit root without supplied file input");
        prefs.SetCustomActions(new[] { "doctor.largefiles" }); prefs.Set(Feature.MyWheel, true);
        var restored = Prefs.Parse(prefs.Serialize());
        check(Catalog.ForCustomWheel(restored, Array.Empty<string>()).Single().Id == "doctor.largefiles", "Large-file stable action ID survives custom menu persistence");
        restored.Set(Feature.Doctor, false);
        check(!Catalog.ForCustomWheel(restored, Array.Empty<string>()).Any() && restored.CustomActionIds.Single() == "doctor.largefiles", "Disabling Doctor hides the action while preserving custom menu preference");

        void MustCancel(Action action, string name)
        {
            bool cancelled = false;
            try { action(); } catch (OperationCanceledException) { cancelled = true; }
            check(cancelled, name);
        }
    }
    private static Fixture F(string name, long bytes = 0, uint attributes = 0) => new(name, false, bytes, attributes);
    private static Fixture D(string name, params Fixture[] children) => new(name, true, 0, 0x10, children);
    private sealed class Fixture(string name, bool directory, long bytes, uint attributes, Fixture[]? children = null) : IFolderMetadata
    {
        public string Name => name;
        public bool Denied;
        public int FailAt = -1;
        private Dictionary<string, Fixture>? index;
        public IEnumerable<FolderEntry> Entries(CancellationToken token)
        {
            int count = 0;
            foreach (var child in children ?? Array.Empty<Fixture>())
            {
                token.ThrowIfCancellationRequested();
                if (count++ == FailAt) throw new IOException("Injected enumeration failure.");
                yield return new(child.Name, child.IsDirectory, child.Bytes, child.Attributes);
            }
        }
        private bool IsDirectory => directory;
        private long Bytes => bytes;
        private uint Attributes => attributes;
        public IFolderMetadata OpenChild(string childName)
        {
            index ??= children!.ToDictionary(x => x.Name, StringComparer.Ordinal);
            var child = index[childName];
            return child.Denied ? throw new UnauthorizedAccessException() : child;
        }
        public void Dispose() { }
    }
    private sealed class CancellingList<T>(IReadOnlyList<T> items, CancellationTokenSource cancellation, int at) : IReadOnlyList<T>
    {
        public int Count => items.Count;
        public T this[int index] => items[index];
        public int Visited { get; private set; }
        public IEnumerator<T> GetEnumerator()
        {
            for (int i = 0; i < items.Count; i++)
            {
                Visited++;
                if (i == at) cancellation.Cancel();
                yield return items[i];
            }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
