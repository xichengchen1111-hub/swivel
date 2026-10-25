using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using Swivel.Core;

internal static class FileToolChecks
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        byte[] data = Enumerable.Range(0, 200000).Select(i => (byte)(i % 253)).ToArray();
        foreach (var format in Enum.GetValues<ArchiveFormat>())
        {
            using var source = new MemoryStream(data); using var output = new MemoryStream();
            var entries = new[] { new ArchiveSource("照片 🦜.dat", source) };
            Archives.Create(entries, format, output, CancellationToken.None);
            check(source.CanRead && output.CanWrite, "Archive does not own caller streams"); output.Position = 0;
            if (format == ArchiveFormat.Zip)
            {
                using var zip = new ZipArchive(output, ZipArchiveMode.Read, true); var entry = zip.Entries.Single();
                using var decoded = new MemoryStream(); using var input = entry.Open(); input.CopyTo(decoded);
                check(entry.FullName == entries[0].Name && decoded.ToArray().SequenceEqual(data), "ZIP Unicode name and exact contents");
            }
            else if (format == ArchiveFormat.Tar)
            {
                using var tar = new TarReader(output, true); var entry = tar.GetNextEntry()!; using var decoded = new MemoryStream(); entry.DataStream!.CopyTo(decoded);
                check(entry.Name == entries[0].Name && entry.EntryType == TarEntryType.RegularFile && decoded.ToArray().SequenceEqual(data), "TAR regular entry and exact contents");
                check(tar.GetNextEntry() is null, "No extra TAR entries");
            }
            else { using var gzip = new GZipStream(output, CompressionMode.Decompress, true); using var decoded = new MemoryStream(); gzip.CopyTo(decoded); check(decoded.ToArray().SequenceEqual(data), "GZip exact round trip"); }
            reject(() => Archives.Create(entries, format, new MemoryStream(), new CancellationToken(true)), "Archive initial cancellation");
            using var cancel = new CancellationTokenSource(); using var interrupted = new CancellingStream(data, cancel);
            reject(() => Archives.Create(new[] { new ArchiveSource("cancel.dat", interrupted) }, format, new MemoryStream(), cancel.Token), "Archive cancels during streaming " + format);
        }
        using var small = new MemoryStream(new byte[1]);
        foreach (var name in new[] { "../bad", "a/b", "a\\b", "a:ads", "CON", "bad.", "bad\u202e.txt" })
            reject(() => Archives.Validate(new[] { new ArchiveSource(name, small) }, ArchiveFormat.Zip), "Archive entry rejects paths and unsafe names");
        reject(() => Archives.Validate(new[] { new ArchiveSource("a", small), new ArchiveSource("A", small) }, ArchiveFormat.Zip), "Archive duplicate case-insensitive names");
        reject(() => Archives.Validate(new[] { new ArchiveSource("a", small), new ArchiveSource("b", small) }, ArchiveFormat.GZip), "GZip exactly one file");
        reject(() => Archives.Validate(Array.Empty<ArchiveSource>(), ArchiveFormat.Tar), "No empty archive");
        using var huge = new SizeStream(); reject(() => Archives.Validate(new[] { new ArchiveSource("large", huge) }, ArchiveFormat.Zip), "Archive source budget before writes");
        const string srt = "1\n00:00:01,999 --> 00:00:03,000\n中文 🦜\n<i>literal</i>\n\n2\n00:00:02,000 --> 00:00:04,000\nsecond\n";
        var cues = SubRip.Parse(srt); check(cues.Count == 2 && cues[0].Start == 1999 && cues[0].End == 3000, "SRT milliseconds and overlaps retained");
        check(SubRip.Parse(SubRip.Write(cues)).SequenceEqual(cues), "SRT exact semantic round trip");
        check(SubRip.Decode(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(srt)).ToArray()) == srt, "SRT BOM and Unicode");
        check(SubRip.Transcript(cues) == "中文 🦜\r\n<i>literal</i>\r\nsecond\r\n", "Plain transcript no tag execution");
        var shifted = SubRip.Shift(cues, 1.001, 2); check(shifted[0].Start == 2001 && shifted[0].End == 2501, "Timing division and millisecond rounding");
        check(SubRip.Time(3599999) == "00:59:59,999" && SubRip.Time(3600000) == "01:00:00,000", "Timestamp carry");
        foreach (var invalid in new[] { "", srt.Replace("03,000", "01,999"), srt.Replace("00:00:01,999", "00:60:01,999"), srt.Replace("00:00:02,000", "00:00:00,000"), srt.Replace("second", "bad\0text"), srt.Replace(" --> ", " → "), "1\n00:00:01,000 --> 00:00:02,000\n" })
            reject(() => SubRip.Parse(invalid), "Invalid SRT rejected");
        foreach (string digits in new[] { "００", "٠١", "०१" })
        {
            bool userError = false;
            try { SubRip.Parse(srt.Replace("00:00:01,999", digits + ":00:01,999")); } catch (UserError) { userError = true; }
            check(userError, "Unicode digits produce a recoverable UI error");
        }
        reject(() => SubRip.Decode(new byte[] { 0xc0, 0xaf }), "No lossy UTF8 decode");
        reject(() => SubRip.Parse(new string('字', SubRip.MaxBytes / 2)), "SRT UTF8 byte budget");
        reject(() => SubRip.Shift(cues, -5, 1), "Negative timestamps rejected");
        reject(() => SubRip.Shift(cues, double.NaN, 1), "Nonfinite timing rejected");
        reject(() => SubRip.Shift(cues, 0, 0), "Zero speed rejected");
        reject(() => SubRip.Shift(new[] { new SubtitleCue(0, 1, "a") }, 0, 4), "Rounded empty cue rejected");
        var prefs = new Prefs(); check(!prefs.IsOn(Feature.Archives) && !prefs.IsOn(Feature.SubtitleFiles), "File tools opt in");
        prefs.Set(Feature.Archives, true); prefs.Set(Feature.SubtitleFiles, true);
        check(Catalog.Available(prefs, new[] { "talk.SRT" }).Any(x => x.Id == "subtitle.edit"), "SRT offered by extension");
        check(!Catalog.Available(prefs, new[] { "a.txt" }).Any(x => x.SrtOnly), "Other text not offered as SRT");
        check(!Catalog.Available(prefs, new[] { "a.srt", "b.srt" }).Any(x => x.Id is "subtitle.edit" or "archive.gzip"), "Single-file action gates");
    }
    private sealed class CancellingStream(byte[] bytes, CancellationTokenSource cancellation) : MemoryStream(bytes)
    {
        public override int Read(byte[] buffer, int offset, int count) { int read = base.Read(buffer, offset, count); cancellation.Cancel(); return read; }
        public override int Read(Span<byte> buffer) { int read = base.Read(buffer); cancellation.Cancel(); return read; }
    }
    private sealed class SizeStream : MemoryStream { public override long Length => Archives.MaxBytes + 1; }
}
