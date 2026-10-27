using System;
using System.Linq;
using System.Text;
using Swivel.Core;

internal static class CopyFilesChecks
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        check(CopyRules.Name("photo.jpg", 1) == "photo.jpg" && CopyRules.Name("photo.jpg", 2) == "photo 2.jpg", "Copy names: original then numbered");
        check(CopyRules.Name("archive.tar.gz", 3) == "archive.tar 3.gz" && CopyRules.Name("README", 2) == "README 2" && CopyRules.Name(".profile", 2) == ".profile 2", "Copy names: extensions, none, dot files");
        reject(() => CopyRules.Name("photo.jpg", 0), "Copy names: ordinal lower bound");
        reject(() => CopyRules.Name("photo.jpg", 10001), "Copy names: ordinal upper bound");
        reject(() => CopyRules.Name("a:b", 1), "Copy names: unsafe leaf");
        var sources = new[] { @"C:\a\x.txt", @"C:\b\x.txt" };
        check(CopyRules.Validate(sources, @"D:\out").SequenceEqual(sources), "Copy plan keeps same-named files from different folders");
        reject(() => CopyRules.Validate(new[] { @"C:\a\x.txt", @"C:\A\X.TXT" }, @"D:\out"), "Copy plan rejects the same file twice");
        reject(() => CopyRules.Validate(Array.Empty<string>(), @"D:\out"), "Copy plan needs files");
        reject(() => CopyRules.Validate(Enumerable.Range(0, 101).Select(i => $@"C:\a\{i}.txt").ToArray(), @"D:\out"), "Copy plan limits to 100 files");
        reject(() => CopyRules.Validate(new[] { @"C:\a\x.txt" }, @"\\server\share"), "Copy plan rejects network destination paths");
        reject(() => CopyRules.Validate(new[] { @"relative.txt" }, @"D:\out"), "Copy plan rejects relative sources");
        check(CopyRules.Validate(new[] { @"C:\a\x.txt" }, @"D:\").Count == 1, "Copy plan accepts a drive root destination");

        byte[] Info(params (string Name, long Size)[] streams)
        {
            var parts = streams.Select(s => Encoding.Unicode.GetBytes(s.Name)).ToArray(); var buffer = new byte[4096]; int at = 0;
            for (int i = 0; i < streams.Length; i++)
            {
                int length = 24 + parts[i].Length, next = i == streams.Length - 1 ? 0 : (length + 7) / 8 * 8;
                BitConverter.GetBytes((uint)next).CopyTo(buffer, at); BitConverter.GetBytes((uint)parts[i].Length).CopyTo(buffer, at + 4);
                BitConverter.GetBytes(streams[i].Size).CopyTo(buffer, at + 8); parts[i].CopyTo(buffer, at + 24); at += next;
            }
            return buffer;
        }
        var original = CopyRules.ParseStreamInfo(Info(("::$DATA", 10), (":Zone.Identifier:$DATA", 26)));
        check(original.Count == 2 && original[1].Name == ":Zone.Identifier:$DATA" && original[1].Size == 26, "Stream info parsed");
        check(CopyRules.SameStreams(original, CopyRules.ParseStreamInfo(Info((":ZONE.IDENTIFIER:$DATA", 26), ("::$DATA", 10)))), "Streams match in any order and case");
        check(!CopyRules.SameStreams(original, CopyRules.ParseStreamInfo(Info(("::$DATA", 10)))), "Missing Mark of the Web detected");
        check(!CopyRules.SameStreams(original, CopyRules.ParseStreamInfo(Info(("::$DATA", 9), (":Zone.Identifier:$DATA", 26)))), "Size change detected");
        check(!CopyRules.SameStreams(original, CopyRules.ParseStreamInfo(Info(("::$DATA", 10), (":Zone.Identifier:$DATA", 26), (":extra:$DATA", 1)))), "Extra stream detected");
        var bad = Info(("::$DATA", 10)); BitConverter.GetBytes(5000u).CopyTo(bad, 4);
        reject(() => CopyRules.ParseStreamInfo(bad), "Stream info name past the buffer");
        var loop = Info(("::$DATA", 10), ("::$DATA", 10)); BitConverter.GetBytes(8u).CopyTo(loop, 0);
        reject(() => CopyRules.ParseStreamInfo(loop), "Stream info bad next offset");
    }
}
