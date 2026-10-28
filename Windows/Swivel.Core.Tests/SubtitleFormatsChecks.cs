using System;
using System.Linq;
using System.Text;
using Swivel.Core;

internal static class SubtitleFormatsChecks
{
    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);
    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);
    private static SubtitleFormats.Format F(string ext) => SubtitleFormats.Formats.Single(f => f.Extension == ext);

    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        // Times: [h:]mm:ss with ',' or '.' and 1–3 decimals, in milliseconds.
        check(SubtitleFormats.Time("00:01:02,500") == 62_500 && SubtitleFormats.Time("1:02.5") == 62_500 && SubtitleFormats.Time("0:00:01.25") == 1_250
            && SubtitleFormats.Time(" 10:00:00.001 ") == 36_000_001 && SubtitleFormats.Time("90:00") == 5_400_000 && SubtitleFormats.Time("123456:00:00") == 444_441_600_000,
            "Subtitle times read hours, minutes, seconds and 1–3 decimals");
        foreach (var bad in new[] { "", "12", "1:60", "1:60:00", "1:00:60", "1:2:3:4", "a:00", "00:00,1234", "00:00,", "1234567:00:00", "00:00.1.2", "-1:00", "１:00" })
            check(SubtitleFormats.Time(bad) is null, "Subtitle times refuse “" + bad + "”");

        // SRT in, every format out.
        string srt = "1\r\n00:00:01,000 --> 00:00:02,500\r\n<i>Hello</i> &amp; welcome\r\n\r\n2\r\n00:00:03,000 --> 00:00:04,000 X1:10\r\nSecond\r\nline\r\n";
        var cues = SubtitleFormats.Parse(SubtitleFormats.Decode(Utf8(srt)), "srt");
        check(cues.Count == 2 && cues[0] == new SubtitleCue(1000, 2500, "Hello & welcome") && cues[1] == new SubtitleCue(3000, 4000, "Second\nline"), "SRT cues are read, tags removed and entities decoded");
        check(Text(SubtitleFormats.Convert(Utf8(srt), ".srt", F("vtt"))) == "WEBVTT\r\n\r\n00:00:01.000 --> 00:00:02.500\r\nHello &amp; welcome\r\n\r\n00:00:03.000 --> 00:00:04.000\r\nSecond\r\nline\r\n",
            "SRT becomes WebVTT with escaped text and CR LF line ends");
        check(Text(SubtitleFormats.Convert(Utf8(srt), "srt", F("srt"))) == "1\r\n00:00:01,000 --> 00:00:02,500\r\nHello & welcome\r\n\r\n2\r\n00:00:03,000 --> 00:00:04,000\r\nSecond\r\nline\r\n", "SRT is written numbered");
        string ass = Text(SubtitleFormats.Convert(Utf8(srt), "srt", F("ass")));
        check(ass.StartsWith("[Script Info]\r\n") && ass.Contains("Dialogue: 0,0:00:01.00,0:00:02.50,Default,,0,0,0,,Hello & welcome\r\n") && ass.EndsWith("Dialogue: 0,0:00:03.00,0:00:04.00,Default,,0,0,0,,Second\\Nline\r\n"),
            "SRT becomes ASS with \\N line breaks");
        check(Text(SubtitleFormats.Convert(Utf8(srt), "srt", F("lrc"))) == "[00:01.00]Hello & welcome\r\n[00:02.50]\r\n[00:03.00]Second line\r\n[00:04.00]\r\n", "SRT becomes LRC with end marks for gaps");
        check(Text(SubtitleFormats.Convert(Utf8(srt), "srt", F("sbv"))) == "0:00:01.000,0:00:02.500\r\nHello & welcome\r\n\r\n0:00:03.000,0:00:04.000\r\nSecond\r\nline\r\n", "SRT becomes SBV");
        check(Text(SubtitleFormats.Convert(Utf8(srt), "srt", F("txt"))) == "Hello & welcome\r\nSecond\r\nline\r\n", "SRT becomes plain text");
        check(!SubtitleFormats.Convert(Utf8(srt), "srt", F("vtt")).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), "Converted files have no byte order mark");

        // Round trips keep text and timing to each format's precision.
        foreach (var format in SubtitleFormats.Formats.Where(f => f.Extension != "txt"))
        {
            var back = SubtitleFormats.Parse(SubtitleFormats.Decode(SubtitleFormats.Convert(Utf8(srt), "srt", format)), format.Extension);
            bool isLrc = format.Extension == "lrc";
            check(back.Count == 2 && back[0].Start == 1000 && back[0].End == 2500 && back[1].Start == 3000 && back[1].End == 4000 && back[0].Text == "Hello & welcome" && back[1].Text == (isLrc ? "Second line" : "Second\nline"),
                $"SRT → {format.Name} → cues keeps text and timing");
        }

        // WebVTT: header, notes, styles, identifiers and settings skipped; voice tags removed.
        string vtt = "WEBVTT - title\n\nNOTE a comment\nspanning lines\n\nSTYLE\n::cue { color: red }\n\nintro\n00:01.000 --> 00:02.000 align:start position:10%\n<v Ann>Hi</v> <c.loud>there</c>\n\n00:03.000 --> 00:04.000\n&lt;b&gt; is bold\n";
        var vttCues = SubtitleFormats.Parse(SubtitleFormats.Decode(Utf8(vtt)), "vtt");
        check(vttCues.SequenceEqual(new[] { new SubtitleCue(1000, 2000, "Hi there"), new SubtitleCue(3000, 4000, "<b> is bold") }), "WebVTT cues are read without header, notes, styles or settings");

        // A missing blank line between cues still starts a new one.
        var joined = SubtitleFormats.Parse("1\n00:00:01,000 --> 00:00:02,000\nOne\n00:00:03,000 --> 00:00:04,000\nTwo\n", "srt");
        check(joined.Count == 2 && joined[1].Text == "Two", "A timing line without a blank line before it starts a new cue");

        // ASS/SSA: the Format line decides the fields; commas stay in the text; overrides removed.
        string ssa = "[Script Info]\nTitle: x\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:01.50,0:00:03.00,Default,,0,0,0,,{\\an8}Hi, there\\Nnext\\hword\nComment: 0,0:00:05.00,0:00:06.00,Default,,0,0,0,,ignored\n";
        check(SubtitleFormats.Parse(ssa, "ssa").SequenceEqual(new[] { new SubtitleCue(1500, 3000, "Hi, there\nnext word") }), "ASS dialogue keeps commas, line breaks and hard spaces, drops overrides and comments");
        reject(() => SubtitleFormats.Parse("[Events]\nFormat: Start, Text, End\nDialogue: 0:00:01.00,Hi,0:00:02.00\n", "ass"), "An ASS file whose Text isn't the last field is refused");
        string assOut = Text(SubtitleFormats.Convert(Utf8("1\n00:00:01,000 --> 00:00:02,000\n{curly} back\\slash\n"), "srt", F("ass")));
        check(assOut.Contains(",,(curly) back\uFF3Cslash\r\n"), "ASS output can't start override tags or escapes");

        // LRC: lyrics run to the next stamp, several stamps share a line, offset moves earlier, metadata skipped.
        string lrc = "[ar:Someone]\n[offset:+500]\n[00:01.00][00:05.00]Chorus\n[00:03.00]Verse\n[00:04.00]\n";
        check(SubtitleFormats.Parse(lrc, "lrc").SequenceEqual(new[] { new SubtitleCue(500, 2500, "Chorus"), new SubtitleCue(2500, 3500, "Verse"), new SubtitleCue(4500, 8500, "Chorus") }),
            "LRC lyrics run until the next stamp, the last for four seconds, moved by the offset");
        check(Text(SubtitleFormats.Convert(Utf8("1\n00:00:01,000 --> 00:00:02,000\n[not a tag]\n"), "srt", F("lrc"))).StartsWith("[00:01.00]\u200B[not a tag]"), "An LRC lyric starting with [ is kept as text");

        // SBV.
        check(SubtitleFormats.Parse("0:00:01.000,0:00:02.000\n<b>kept</b>\n", "sbv").SequenceEqual(new[] { new SubtitleCue(1000, 2000, "<b>kept</b>") }), "SBV cues are read; tags are not markup there");

        // Cleaning and order: empty or backwards cues dropped, sorted by start, inner blank lines collapsed on writing.
        var mixed = SubtitleFormats.Parse("00:00:05.000 --> 00:00:06.000\nLater\n\n00:00:02.000 --> 00:00:01.000\nBackwards\n\n00:00:03.000 --> 00:00:04.000\n<i></i>\n\n00:00:01.000 --> 00:00:02.000\nFirst\n", "vtt");
        check(mixed.Select(c => c.Text).SequenceEqual(new[] { "First", "Later" }), "Backwards and empty cues are dropped and the rest sorted by start");
        check(SubtitleFormats.Write(new[] { new SubtitleCue(0, 1000, "a\n \n\nb") }, "srt") == "1\n00:00:00,000 --> 00:00:01,000\na\nb\n", "A blank line inside a cue is collapsed so it can't end the cue");
        check(SubtitleFormats.Stamp(-5, ',') == "00:00:00,000" && SubtitleFormats.Stamp(long.MaxValue, '.') == "9999:59:59.999", "Written times are kept within 0 and 9999:59:59.999");

        // Encodings: UTF-8 with or without BOM, UTF-16 with BOM; others refused, not guessed.
        string one = "1\n00:00:01,000 --> 00:00:02,000\n中文 ✓\n";
        var expected = new SubtitleCue(1000, 2000, "中文 ✓");
        check(SubtitleFormats.Parse(SubtitleFormats.Decode(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Utf8(one)).ToArray()), "srt").Single() == expected
            && SubtitleFormats.Parse(SubtitleFormats.Decode(new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes(one)).ToArray()), "srt").Single() == expected
            && SubtitleFormats.Parse(SubtitleFormats.Decode(new byte[] { 0xFE, 0xFF }.Concat(Encoding.BigEndianUnicode.GetBytes(one)).ToArray()), "srt").Single() == expected,
            "Subtitles are read from UTF-8 with or without a BOM and UTF-16 with a BOM");
        reject(() => SubtitleFormats.Decode(new byte[] { 0x31, 0x0A, 0xD6, 0xD0, 0xCE, 0xC4 }), "GBK bytes are refused rather than guessed");
        reject(() => SubtitleFormats.Decode(Utf8("a\0b")), "A NUL character is refused");
        reject(() => SubtitleFormats.Decode(new byte[SubtitleFormats.MaxBytes + 1]), "Subtitle files over 8 MB are refused");
        reject(() => SubtitleFormats.Parse("just text\n", "srt"), "A file without timed cues is refused");
        var many = new StringBuilder();
        for (int i = 0; i <= SubtitleFormats.MaxCues; i++) many.Append("[00:01.00]");
        many.Append("x\n");
        reject(() => SubtitleFormats.Parse(many.ToString(), "lrc"), "LRC with more than 100,000 stamps is refused");

        // Catalog: offered for subtitle files, never a file's own format; behind the Subtitles switch.
        var prefs = new Prefs(); prefs.Set(Feature.SubtitleFiles, true);
        var ids = Catalog.Available(prefs, new[] { @"C:\a\talk.srt" }).Select(a => a.Id).Where(id => id.StartsWith("subtitle.")).ToArray();
        check(ids.SequenceEqual(new[] { "subtitle.edit", "subtitle.vtt", "subtitle.ass", "subtitle.lrc", "subtitle.sbv", "subtitle.txt" }), "An SRT file is offered every other subtitle format: " + string.Join(",", ids));
        check(Catalog.Available(prefs, new[] { @"C:\a\x.ssa", @"C:\a\y.lrc" }).Count(a => a.Id.StartsWith("subtitle.") && a.Id != "subtitle.edit") == 6
            && !Catalog.Available(prefs, new[] { @"C:\a\x.txt" }).Any(a => SubtitleFormats.ForId(a.Id) is not null), "Mixed subtitle files are offered all formats; text files none");
        prefs.Set(Feature.SubtitleFiles, false);
        check(!Catalog.Available(prefs, new[] { @"C:\a\talk.srt" }).Any(a => a.Id.StartsWith("subtitle.")), "Subtitle conversions are hidden while Subtitles is off");
    }
}
