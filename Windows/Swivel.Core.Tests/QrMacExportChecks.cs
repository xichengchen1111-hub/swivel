using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Swivel.Core;

internal static class QrMacExportChecks
{
    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        QrEntry Entry(string name, string text = "https://example.com/二维码", QrCorrection correction = QrCorrection.Medium,
            string group = "", QrGardenTemplate template = QrGardenTemplate.Tree, QrGardenSeason season = QrGardenSeason.Summer)
            => new(Guid.NewGuid().ToString("N"), name, text, group, correction, template, season);
        var original = new[] {
            Entry("First", group: "Group A", template: QrGardenTemplate.Bouquet, season: QrGardenSeason.Autumn),
            Entry("L", correction: QrCorrection.Low), Entry("Q", correction: QrCorrection.Quartile),
            Entry("H", correction: QrCorrection.High), Entry("Too long", new string('x', 2001)),
            Entry("Limit", new string('x', 2000)), Entry("Chinese", new string('中', 666) + "ab"),
            Entry("Too long Chinese", new string('中', 667))
        };
        var source = original.ToArray();
        var plan = QrMacExport.Prepare(source);
        check(plan.Rows.Count == 8 && plan.Compatible.Count == 3, "Mac PNG export retains all rows and exports only M payloads up to 2000 bytes");
        check(plan.Rows.Take(4).Skip(1).All(row => row.Issues.SequenceEqual(new[] { QrMacExport.WrongCorrection })), "Mac PNG export does not silently change L/Q/H to M");
        check(plan.Rows[4].Issues.SequenceEqual(new[] { QrMacExport.TooLong }) && plan.Rows[7].Issues.SequenceEqual(new[] { QrMacExport.TooLong }), "Mac PNG export uses a UTF8 byte limit without shortening");
        check(plan.Rows[5].Utf8Bytes == 2000 && plan.Rows[6].Utf8Bytes == 2000 && plan.Rows[7].Utf8Bytes == 2001, "Mac PNG export exact ASCII and multibyte boundary");
        check(plan.Rows[0].Entry == original[0] && plan.Rows[0].Entry.Group == "Group A" && plan.Rows[0].Entry.Template == QrGardenTemplate.Bouquet && plan.Rows[0].Entry.Season == QrGardenSeason.Autumn, "Mac PNG export preview preserves original metadata for loss disclosure");
        check(plan.Rows.All(row => row.BatchTag == plan.Rows[0].BatchTag && row.BatchTag.Length == 12 && row.BatchTag.All(Uri.IsHexDigit)), "Mac PNG export has one stable generated batch tag");
        check(plan.Rows[0].SuggestedFileName.EndsWith(" 001.png", StringComparison.Ordinal) && plan.Rows[7].SuggestedFileName.EndsWith(" 008.png", StringComparison.Ordinal), "Mac PNG output indices refer to original library positions including omissions");
        foreach (var row in plan.Rows)
        {
            WindowsPathPolicy.ValidateName(row.SuggestedFileName);
            check(!row.Stem.Contains(row.Entry.Name, StringComparison.Ordinal) || row.Entry.Name.Length == 1, "Mac PNG output names do not embed user names");
        }
        source[0] = Entry("Replaced", "other");
        check(plan.Rows[0].Entry == original[0], "Mac PNG export pins the library snapshot before preview");
        check(QrMacExport.Prepare(original).Rows[0].BatchTag != plan.Rows[0].BatchTag, "Mac PNG exports receive distinct batch prefixes");
        var bothIssues = QrMacExport.Prepare(new[] { Entry("Both", new string('a', 2200), QrCorrection.Low) });
        check(bothIssues.Rows[0].Issues.SequenceEqual(new[] { QrMacExport.WrongCorrection, QrMacExport.TooLong }), "Mac PNG export lists both applicable compatibility reasons");

        var duplicate = QrMacExport.Prepare(new[] { Entry("A", "same", group: "one"), Entry("B", "same", group: "two"), Entry("C", "different"), Entry("D", "different", QrCorrection.Low) });
        check(duplicate.Compatible.Count == 3 && duplicate.Rows[0].RepeatedPayload && duplicate.Rows[1].RepeatedPayload && !duplicate.Rows[2].RepeatedPayload && !duplicate.Rows[3].RepeatedPayload, "Mac PNG export flags repeated eligible content without silently choosing a Windows group");
        var canonical = QrMacExport.Prepare(new[] { Entry("Composed", "é"), Entry("Decomposed", "e\u0301"), Entry("Compat1", "\ufb01"), Entry("Compat2", "fi") });
        check(canonical.Rows.Take(2).All(row => row.RepeatedPayload) && canonical.Rows.Skip(2).All(row => !row.RepeatedPayload), "Mac PNG warning matches Swift canonical equivalence without compatibility folding");
        check(canonical.Rows[0].Entry.Text == "é" && canonical.Rows[1].Entry.Text == "e\u0301" && canonical.Rows[0].Utf8Bytes == 2 && canonical.Rows[1].Utf8Bytes == 3, "Mac PNG duplicate warning does not normalize original QR payloads or byte counts");
        var exactPayloads = new List<string>();
        _ = QrMacExport.Run(canonical, row => { exactPayloads.Add(row.Entry.Text); return row.SuggestedFileName; }, default);
        check(exactPayloads.SequenceEqual(new[] { "é", "e\u0301", "\ufb01", "fi" }, StringComparer.Ordinal), "Mac PNG exporter receives exact original Unicode spellings");
        var unique = QrMacExport.Prepare(Enumerable.Range(0, 32).Select(i => Entry("Code " + i, "Payload " + i)).ToArray());
        check(unique.Rows.Count == 32 && unique.Rows.Select(row => row.SuggestedFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 32, "Mac PNG export accepts full 32 entry library with unique safe names");
        reject(() => QrMacExport.Prepare(Enumerable.Range(0, 33).Select(i => Entry("Code " + i)).ToArray()), "Mac PNG export refuses above library limit");
        reject(() => QrMacExport.Prepare(new[] { Entry("Dup"), Entry("dup") }), "Mac PNG export refuses invalid duplicate names");
        reject(() => QrMacExport.Prepare(new[] { Entry("Broken", "\ud800") }), "Mac PNG export refuses invalid payload Unicode");
        reject(() => QrMacExport.Prepare(new[] { Entry("Invalid enum", correction: (QrCorrection)99) }), "Mac PNG export refuses invalid correction enum");
        check(QrMacExport.Prepare(Array.Empty<QrEntry>()).Rows.Count == 0, "Mac PNG export handles an empty library without phantom entries");

        var small = QrMacExport.Prepare(new[] { Entry("One", "one"), Entry("Unsupported", "two", QrCorrection.Low), Entry("Three", "three"), Entry("Four", "four") });
        var order = new List<int>(); var progress = new List<(int, int)>();
        var saved = QrMacExport.Run(small, row => { order.Add(row.Index); return row.SuggestedFileName; }, default, (index, count) => progress.Add((index, count)));
        check(order.SequenceEqual(new[] { 0, 2, 3 }) && saved.All(row => row.State == BatchState.Saved) && progress.SequenceEqual(new[] { (1, 3), (2, 3), (3, 3) }), "Mac PNG batch exports compatible rows in original order and reports compatible count");
        check(saved.Select(row => row.Output).SequenceEqual(small.Compatible.Select(row => row.SuggestedFileName)), "Mac PNG batch returns every completed path");
        int calls = 0;
        var failed = QrMacExport.Run(small, row => { if (++calls == 2) throw new UserError("probe failure"); return row.SuggestedFileName; }, default);
        check(calls == 2 && failed.Select(row => row.State).SequenceEqual(new[] { BatchState.Saved, BatchState.Failed, BatchState.NotStarted }) && failed[0].Output is not null, "Mac PNG batch stops on first error and retains already published output");
        using (var cancellation = new CancellationTokenSource())
        {
            int invoked = 0;
            var canceled = QrMacExport.Run(small, row => { invoked++; cancellation.Cancel(); return row.SuggestedFileName; }, cancellation.Token);
            check(invoked == 1 && canceled.Select(row => row.State).SequenceEqual(new[] { BatchState.Saved, BatchState.Cancelled, BatchState.NotStarted }) && canceled[0].Output is not null, "Mac PNG batch retains a file published just before cancellation");
        }
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel(); int invoked = 0;
            var canceled = QrMacExport.Run(small, row => { invoked++; return row.SuggestedFileName; }, cancellation.Token);
            check(invoked == 0 && canceled.Select(row => row.State).SequenceEqual(new[] { BatchState.Cancelled, BatchState.NotStarted, BatchState.NotStarted }), "Mac PNG pre-cancel invokes no exporter");
        }
        check(QrMacExport.MetadataLimit.Contains("Original names, groups and scene choices are not transferred", StringComparison.Ordinal) && QrMacExport.DuplicateLimit.Contains("image recognition can fail", StringComparison.Ordinal), "Mac PNG preview does not claim full library or scanner fidelity");
    }
}
