using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using Swivel.Core;
using Swivel.Windows;

internal static class FileToolChecks
{
    public static void Run(string directory, Action<bool,string> check, Action<Action,string> reject)
    {
        string first = Path.Combine(directory, "archive-甲.txt"), second = Path.Combine(directory, "archive-乙.txt");
        File.WriteAllText(first, "first"); File.WriteAllText(second, "second");
        using (var work = ArchiveWork.Open(new[] { first, second }, ArchiveFormat.Zip, CancellationToken.None))
        {
            reject(() => File.WriteAllText(second, "changed"), "Every archive input is held against edits");
            string output = work.Save(CancellationToken.None); string collision = work.Save(CancellationToken.None);
            check(output != collision && Path.GetDirectoryName(output) == directory, "Archive exclusive beside first source");
            using var zip = ZipFile.OpenRead(output);
            check(zip.Entries.Count == 2 && zip.Entries.Select(x => x.Name).SequenceEqual(new[] { Path.GetFileName(first), Path.GetFileName(second) }), "Native ZIP selected entries only");
            reject(() => work.Save(new CancellationToken(true)), "Native archive cancellation");
        }
        string source = Path.Combine(directory, "captions.srt"); const string original = "1\n00:00:01,000 --> 00:00:02,000\nHello 中文\n";
        File.WriteAllText(source, original, new UTF8Encoding(false));
        using (var subtitle = SubtitleFile.Open(source, CancellationToken.None))
        {
            reject(() => File.Move(source, source + ".moved"), "SRT source pinned throughout editor");
            string text = SubRip.Write(SubRip.Shift(SubRip.Parse(subtitle.Text), .5, 1));
            string output = subtitle.Save(text, false, CancellationToken.None);
            check(SubRip.Parse(File.ReadAllText(output))[0].Start == 1500, "Native SRT export timings");
            string transcript = subtitle.Save(text, true, CancellationToken.None);
            check(File.ReadAllText(transcript) == "Hello 中文\r\n", "Native transcript UTF8");
            int count = Directory.GetFiles(directory).Length;
            reject(() => subtitle.Save("invalid", false, CancellationToken.None), "Invalid SRT never written");
            reject(() => subtitle.Save(text, false, new CancellationToken(true)), "SRT cancellation no output");
            check(Directory.GetFiles(directory).Length == count, "Failed file edits leave no staging");
        }
        check(File.ReadAllText(source) == original && File.ReadAllText(first) == "first" && File.ReadAllText(second) == "second", "File tool originals unchanged");
        check(!Directory.GetFiles(directory, ".swivel-*.tmp").Any(), "File tool cleanup");
    }
}
