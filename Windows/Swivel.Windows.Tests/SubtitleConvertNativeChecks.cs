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

        string gbk = Path.Combine(root, "legacy.srt");
        File.WriteAllBytes(gbk, Encoding.ASCII.GetBytes("1\r\n00:00:01,000 --> 00:00:02,000\r\n").Concat(new byte[] { 0xC4, 0xE3, 0xBA, 0xC3 }).ToArray());
        string? message = null;
        try { SubtitleConvert.Run(gbk, F("vtt"), default); } catch (UserError error) { message = error.Message; }
        check(message == SubtitleFormats.BadEncoding && !File.Exists(Path.Combine(root, "legacy.vtt")), "A GBK subtitle file is refused with the encoding message and nothing is written");
    }
}
