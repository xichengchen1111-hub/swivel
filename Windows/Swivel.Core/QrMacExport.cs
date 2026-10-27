using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

namespace Swivel.Core;

public sealed record QrMacExportRow(int Index, string BatchTag, QrEntry Entry, int Utf8Bytes, bool RepeatedPayload, IReadOnlyList<string> Issues)
{
    // Names and payloads never become paths. The original library position also
    // makes the preview and its eventual output easy to match after omissions.
    public string Stem => "Swivel QR " + BatchTag + " " + (Index + 1).ToString("000", CultureInfo.InvariantCulture);
    public string SuggestedFileName => Stem + ".png";
    public bool Compatible => Issues.Count == 0;
}

public sealed class QrMacExportPlan
{
    public IReadOnlyList<QrMacExportRow> Rows { get; }
    public IReadOnlyList<QrMacExportRow> Compatible { get; }
    internal QrMacExportPlan(QrMacExportRow[] rows)
    {
        Rows = Array.AsReadOnly(rows);
        Compatible = Array.AsReadOnly(rows.Where(row => row.Compatible).ToArray());
    }
}

/// <summary>
/// Image interchange for Mac QR Library's existing Import Images action. Mac
/// stores only the decoded value plus new/default metadata; it has no JSON
/// library import UI. This does not promise a full library migration.
/// </summary>
public static class QrMacExport
{
    public const int MacPayloadLimit = 2000;
    public const string Title = "Export QR Images for Mac…";
    public const string Scope = "Export compatible saved codes as individual PNG images. On Mac, open QR Library, choose Import Images, and select these PNGs. QR content stays exact; no library or settings are changed here.";
    public const string MetadataLimit = "Mac image import uses the PNG filename as the new code name, an empty collection, the Summer Tree scene and a new creation date. Original names, groups and scene choices are not transferred. Rename and regroup the imported codes on Mac if needed.";
    public const string CorrectionLimit = "Mac saves imported content using M error correction and accepts up to 2,000 UTF-8 bytes. Only existing M codes within that limit are exported; other correction levels and longer content are listed below without alteration.";
    public const string DuplicateLimit = "Mac may merge text with equivalent Unicode spellings, as well as exact duplicates. It skips matching content already saved without a group. Existing Mac libraries are not inspected; image recognition can fail, so check the imported content on Mac.";
    public const string Privacy = "The PNGs contain the complete QR text and can reveal links, credentials or other private content to anyone who can read the images. Nothing is uploaded or opened automatically.";
    public const string OutputLimit = "Numbered filenames match the preview. Existing files are never replaced; a number is added if a filename is occupied. Export stops on the first error or cancellation and keeps every completed PNG.";
    public const string WrongCorrection = "Mac would change this correction level to M. This entry is not exported.";
    public const string TooLong = "This content exceeds Mac's 2,000-byte limit. It is not shortened or exported.";
    public const string Repeated = "Mac treats this content as a duplicate of another exported entry, including equivalent Unicode spellings. Image import keeps one ungrouped copy.";

    public static QrMacExportPlan Prepare(IReadOnlyList<QrEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        // Reuse the closed Windows library validation, including its count,
        // unique names/IDs, strict payload Unicode and supported enum checks.
        // The resulting JSON is neither returned nor written.
        var snapshot = entries.ToArray();
        _ = QrLibrary.Encode(snapshot);
        string batchTag = Guid.NewGuid().ToString("N")[..12];
        var compatibleTexts = snapshot.Where(entry => entry.Correction == QrCorrection.Medium &&
            QrCode.Payload(entry.Text, entry.Correction).Length <= MacPayloadLimit)
            // Swift String equality is canonically equivalent. Normalize only
            // the warning key; the payload passed to the QR encoder stays exact.
            .GroupBy(entry => entry.Text.Normalize(NormalizationForm.FormD), StringComparer.Ordinal)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        return new(snapshot.Select((entry, index) =>
        {
            int bytes = QrCode.Payload(entry.Text, entry.Correction).Length;
            var issues = new List<string>();
            if (entry.Correction != QrCorrection.Medium) issues.Add(WrongCorrection);
            if (bytes > MacPayloadLimit) issues.Add(TooLong);
            return new QrMacExportRow(index, batchTag, entry, bytes, issues.Count == 0 &&
                compatibleTexts.Contains(entry.Text.Normalize(NormalizationForm.FormD)), issues.AsReadOnly());
        }).ToArray());
    }

    public static IReadOnlyList<ImageBatchEntry> Run(QrMacExportPlan plan,
        Func<QrMacExportRow, string> save, CancellationToken token, Action<int, int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(save);
        var rows = plan.Compatible;
        // All display labels are unique because the filename uses the original
        // position. ImageBatch retains outputs published before cancellation.
        string Label(QrMacExportRow row) => row.SuggestedFileName + " · " + LibraryJson.Visible(row.Entry.Name);
        var byLabel = rows.ToDictionary(Label, StringComparer.Ordinal);
        return ImageBatch.Run(rows.Select(Label).ToArray(), label => save(byLabel[label]), token, progress);
    }
}
