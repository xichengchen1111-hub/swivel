using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Swivel.Core;
using Swivel.Windows;

internal static class SubtitleConvertNativeChecks
{
    // Convert Subtitles on real files: written beside the original under its own name with the
    // new extension, the next numbered name when that is taken, the original unchanged, and
    // nothing written for a file Swivel refuses.
    internal static void Run(string directory, Action<bool, string> check)
    {
        string root = Path.Combine(directory, "subtitle formats"); Directory.CreateDirectory(root);
        string vtt = Path.Combine(root, "talk.vtt");
        File.WriteAllText(vtt, "WEBVTT\n\n00:01.000 --> 00:02.500\n<v Ann>你好</v>, world\n", new UTF8Encoding(true));
        byte[] original = SHA256.HashData(File.ReadAllBytes(vtt));
        SubtitleFormats.Format F(string ext) => SubtitleFormats.Formats.Single(f => f.Extension == ext);

        string srt = SubtitleConvert.Run(vtt, F("srt"), default);
        check(Path.GetFileName(srt) == "talk.srt" && File.ReadAllText(srt, Encoding.UTF8) == "1\r\n00:00:01,000 --> 00:00:02,500\r\n你好, world\r\n", "A VTT file is converted to talk.srt beside it: " + Path.GetFileName(srt));
        string again = SubtitleConvert.Run(vtt, F("srt"), default);
        check(Path.GetFileName(again) == "talk 2.srt" && File.ReadAllBytes(again).SequenceEqual(File.ReadAllBytes(srt)), "A second conversion takes the next numbered name: " + Path.GetFileName(again));
        string lrc = SubtitleConvert.Run(srt, F("lrc"), default);
        check(Path.GetFileName(lrc) == "talk.lrc" && File.ReadAllText(lrc) == "[00:01.00]你好, world\r\n[00:02.50]\r\n", "An SRT file is converted to LRC");
        check(SHA256.HashData(File.ReadAllBytes(vtt)).SequenceEqual(original), "Converting leaves the original byte for byte the same");

        FileStamp stamp; using (var held = NativeFiles.OpenRead(vtt)) stamp = held.Stamp;
        string ass = SubtitleConvert.Run(vtt, F("ass"), default, stamp);
        check(Path.GetFileName(ass) == "talk.ass" && File.ReadAllText(ass).Contains(",,你好, world\r\n"), "The agent path converts a file whose stamp matches");
        File.AppendAllText(vtt, "\n");
        bool changed = false;
        try { SubtitleConvert.Run(vtt, F("sbv"), default, stamp); } catch (McpToolError error) { changed = error.Message == McpSession.Changed; }
        check(changed && !File.Exists(Path.Combine(root, "talk.sbv")), "The agent path refuses a file changed since planning and writes nothing");

        // Data files the same way: CSV → JSON beside it, then back to CSV under the next free name.
        string csv = Path.Combine(root, "sales.csv");
        File.WriteAllBytes(csv, new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("item,price\r\n茶,\"1,5\"\r\n")).ToArray());
        DataFileFormats.Format D(string ext) => DataFileFormats.Formats.Single(f => f.Extension == ext);
        string json = SubtitleConvert.Run(csv, D("json"), default);
        check(Path.GetFileName(json) == "sales.json" && File.ReadAllText(json) == "[\n  {\n    \"item\": \"茶\",\n    \"price\": \"1,5\"\n  }\n]\n", "A CSV file becomes sales.json beside it: " + Path.GetFileName(json));
        string back = SubtitleConvert.Run(json, D("csv"), default);
        check(Path.GetFileName(back) == "sales 2.csv" && File.ReadAllBytes(back).SequenceEqual(File.ReadAllBytes(csv)), "JSON back to CSV takes the next free name and matches the original bytes");
        string plist = SubtitleConvert.Run(json, D("plist"), default);
        check(Path.GetFileName(plist) == "sales.plist" && SubtitleConvert.Run(plist, D("yaml"), default) is var yaml && File.ReadAllText(yaml) == "---\n-\n  \"item\": \"茶\"\n  \"price\": \"1,5\"\n", "JSON → plist → YAML keeps the values");
        string huge = Path.Combine(root, "huge.json"); File.WriteAllBytes(huge, new byte[DataFileFormats.MaxBytes + 1]);
        string? tooBig = null;
        try { SubtitleConvert.Run(huge, D("yaml"), default); } catch (UserError error) { tooBig = error.Message; }
        check(tooBig == DataFileFormats.TooLarge && !File.Exists(Path.Combine(root, "huge.yaml")), "A data file over 32 MB is refused before reading and nothing is written");

        // Fonts: Windows' own Arial to WOFF and WOFF2 and back; Windows loads every result.
        string arialSource = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf");
        check(File.Exists(arialSource), "Windows has Arial for the font checks");
        if (File.Exists(arialSource))
        {
            string arial = Path.Combine(root, "arial.ttf"); File.Copy(arialSource, arial);
            int Glyphs(string path) => new System.Windows.Media.GlyphTypeface(new Uri(path)).GlyphCount;
            int glyphs = Glyphs(arial);
            string woff = SubtitleConvert.Run(arial, FontFileFormats.Woff, default), woff2 = SubtitleConvert.Run(arial, FontFileFormats.Woff2, default);
            check(Path.GetFileName(woff) == "arial.woff" && Path.GetFileName(woff2) == "arial.woff2" && new FileInfo(woff2).Length < new FileInfo(woff).Length && new FileInfo(woff).Length < new FileInfo(arial).Length,
                $"Arial becomes a smaller WOFF and an even smaller WOFF2: {new FileInfo(arial).Length} → {new FileInfo(woff).Length} → {new FileInfo(woff2).Length} bytes");
            string fromWoff2 = SubtitleConvert.Run(woff2, FontFileFormats.Desktop, default);
            check(Path.GetFileName(fromWoff2) == "arial 2.ttf" && Glyphs(fromWoff2) == glyphs, $"Arial's WOFF2 unpacks to a TTF Windows loads with all {glyphs} glyphs");
            string fromWoff = SubtitleConvert.Run(woff, FontFileFormats.Desktop, default);
            check(Path.GetFileName(fromWoff) == "arial 3.ttf" && Glyphs(fromWoff) == glyphs && File.ReadAllBytes(fromWoff).SequenceEqual(File.ReadAllBytes(fromWoff2)), "WOFF and WOFF2 unpack to the same TTF");
        }

        string gbk = Path.Combine(root, "legacy.srt");
        File.WriteAllBytes(gbk, Encoding.ASCII.GetBytes("1\r\n00:00:01,000 --> 00:00:02,000\r\n").Concat(new byte[] { 0xC4, 0xE3, 0xBA, 0xC3 }).ToArray());
        string? message = null;
        try { SubtitleConvert.Run(gbk, F("vtt"), default); } catch (UserError error) { message = error.Message; }
        check(message == SubtitleFormats.BadEncoding && !File.Exists(Path.Combine(root, "legacy.vtt")), "A GBK subtitle file is refused with the encoding message and nothing is written");
    }
}
