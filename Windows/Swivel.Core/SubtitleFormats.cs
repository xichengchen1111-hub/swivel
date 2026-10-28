using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Swivel.Core;

/// Subtitle files ↔ each other (SRT, WebVTT, ASS/SSA, LRC, SBV, and plain TXT out), as the Mac's
/// Convert menu does. The text and timing of each cue are kept; styling (italics, colours,
/// positions) is not.
public static class SubtitleFormats
{
    public sealed record Format(string Name, string Extension)
    {
        /// The stable action ID, such as subtitle.vtt.
        public string Id => "subtitle." + Extension;
    }

    public static readonly string[] InputExtensions = { "srt", "vtt", "ass", "ssa", "lrc", "sbv" };
    public static readonly Format[] Formats =
    {
        new("SRT", "srt"), new("VTT", "vtt"), new("ASS", "ass"), new("LRC", "lrc"), new("SBV", "sbv"), new("TXT", "txt"),
    };
    public const int MaxBytes = 8 * 1024 * 1024, MaxCues = 100_000;
    /// All cue text together, in UTF-8 bytes; LRC can repeat one line under many timestamps.
    public const long MaxTextBytes = 32L * 1024 * 1024;
    /// 9 999:59:59.999, the largest time every format can write.
    public const long MaxTime = 35_999_999_999;
    public const string BadEncoding = "Swivel reads subtitle files in UTF-8 or UTF-16 (with a byte order mark). Save this file as UTF-8, for example with Notepad's Save As, and try again.";
    public const string TooLarge = "Subtitle files are limited to 8 MB.";
    public const string NoCues = "No subtitles with timing were found in this file.";
    public const string TooMany = "This subtitle file has too many cues.";
    public const string NoAssFormat = "This ASS/SSA file has no valid Format line for its events.";

    public static bool IsInput(string path) =>
        InputExtensions.Contains(System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant());

    public static Format? ForId(string id) => Formats.FirstOrDefault(f => f.Id == id);

    /// The converted file's bytes: UTF-8 without a byte order mark, CR LF line ends.
    public static byte[] Convert(byte[] source, string sourceExtension, Format target)
    {
        var cues = Parse(Decode(source), sourceExtension.TrimStart('.').ToLowerInvariant());
        return new UTF8Encoding(false).GetBytes(Write(cues, target.Extension).Replace("\n", "\r\n"));
    }

    // MARK: Reading

    /// UTF-8 (with or without a byte order mark) and UTF-16 with one, strictly; anything else is
    /// refused rather than guessed. New lines become \n.
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length > MaxBytes) throw new UserError(TooLarge);
        string text;
        try
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) text = new UTF8Encoding(false, true).GetString(bytes, 3, bytes.Length - 3);
            else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) text = new UnicodeEncoding(false, false, true).GetString(bytes, 2, bytes.Length - 2);
            else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) text = new UnicodeEncoding(true, false, true).GetString(bytes, 2, bytes.Length - 2);
            else text = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException) { throw new UserError(BadEncoding); }
        if (text.Contains('\0')) throw new UserError(BadEncoding);
        if (text.StartsWith('﻿')) text = text[1..];
        return text.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    public static IReadOnlyList<SubtitleCue> Parse(string text, string extension)
    {
        List<SubtitleCue> cues = extension switch
        {
            "ass" or "ssa" => ParseAss(text),
            "lrc" => ParseLrc(text),
            "sbv" => ParseBlocks(text, line =>
            {
                var parts = line.Split(',');
                return parts.Length == 2 && Time(parts[0]) is { } a && Time(parts[1]) is { } b ? (a, b) : null;
            }, false),
            _ => ParseBlocks(text, line =>
            {
                int arrow = line.IndexOf("-->", StringComparison.Ordinal);
                if (arrow < 0 || line.IndexOf("-->", arrow + 3, StringComparison.Ordinal) >= 0) return null;
                // WebVTT cue settings (position, align…) follow the end time.
                string end = line[(arrow + 3)..].Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                return Time(line[..arrow]) is { } a && Time(end) is { } b ? (a, b) : null;
            }, true),
        };
        cues = cues.Where(c => c.End > c.Start && !string.IsNullOrWhiteSpace(c.Text)).ToList();
        if (cues.Count == 0) throw new UserError(NoCues);
        if (cues.Count > MaxCues || cues.Sum(c => (long)System.Text.Encoding.UTF8.GetByteCount(c.Text)) > MaxTextBytes) throw new UserError(TooMany);
        return cues.OrderBy(c => c.Start).ToList();
    }

    /// [h:]mm:ss[.,]f…, any of 1–6 hour digits, 1–3 fraction digits (ASS uses hundredths); minutes
    /// below 60 when hours are given, seconds always below 60. In milliseconds, or null.
    public static long? Time(string raw)
    {
        var fields = raw.Trim(' ', '\t').Split(':');
        if (fields.Length is < 2 or > 3) return null;
        var tail = fields[^1].Split(',', '.');
        if (tail.Length is < 1 or > 2) return null;
        static long? Number(string s, long? max = null)
        {
            if (s.Length is 0 or > 6 || s.Any(c => c is < '0' or > '9')) return null;
            long n = long.Parse(s, NumberStyles.None, CultureInfo.InvariantCulture);
            return max is { } limit && n >= limit ? null : n;
        }
        if (Number(tail[0], 60) is not { } seconds) return null;
        if (Number(fields[^2], fields.Length == 3 ? 60 : null) is not { } minutes) return null;
        long hours = 0;
        if (fields.Length == 3) { if (Number(fields[0]) is not { } h) return null; hours = h; }
        long fraction = 0;
        if (tail.Length == 2)
        {
            if (tail[1].Length is < 1 or > 3 || Number(tail[1]) is not { } f) return null;
            fraction = f * (tail[1].Length == 1 ? 100 : tail[1].Length == 2 ? 10 : 1);
        }
        return (hours * 3600 + minutes * 60 + seconds) * 1000 + fraction;
    }

    /// SRT, WebVTT and SBV: blocks separated by blank lines, each with a timing line and text.
    private static List<SubtitleCue> ParseBlocks(string text, Func<string, (long, long)?> timing, bool stripTags)
    {
        var cues = new List<SubtitleCue>();
        (long Start, long End)? current = null; var lines = new List<string>();
        void Flush()
        {
            if (current is { } c)
            {
                string body = string.Join("\n", lines);
                if (stripTags) body = Plain(body);
                cues.Add(new SubtitleCue(c.Start, c.End, body.Trim()));
                if (cues.Count > MaxCues) throw new UserError(TooMany);
            }
            current = null; lines.Clear();
        }
        bool skipping = false;
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim(' ', '\t');
            if (trimmed.Length == 0) { Flush(); skipping = false; continue; }
            if (skipping) continue;
            if (current is null)
            {
                // WebVTT's header, comments and style blocks carry no cues.
                if (trimmed.StartsWith("WEBVTT", StringComparison.Ordinal) || trimmed.StartsWith("NOTE", StringComparison.Ordinal) || trimmed is "STYLE" or "REGION") { skipping = true; continue; }
                current = timing(trimmed);
                continue; // SRT's counter or a WebVTT cue identifier.
            }
            if (timing(trimmed) is { } next) { Flush(); current = next; continue; } // A missing blank line.
            lines.Add(line);
        }
        Flush();
        return cues;
    }

    private static readonly Regex Tags = new(@"<[^<>\n]{0,200}>", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));
    private static readonly Regex AssTags = new(@"\{\\[^{}\n]{0,200}\}", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));

    /// <i>, <b>, <font …>, <v Speaker> and {\an8} markup removed; common entities decoded.
    public static string Plain(string text)
    {
        string result = AssTags.Replace(Tags.Replace(text, ""), "");
        foreach (var (entity, value) in new[] { ("&lt;", "<"), ("&gt;", ">"), ("&nbsp;", " "), ("&lrm;", "‎"), ("&rlm;", "‏"), ("&amp;", "&") })
            result = result.Replace(entity, value, StringComparison.Ordinal);
        return result;
    }

    private static List<SubtitleCue> ParseAss(string text)
    {
        bool events = false; string[] fields = Array.Empty<string>();
        var cues = new List<SubtitleCue>();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim(' ', '\t');
            if (line.StartsWith('[')) { events = line.Equals("[events]", StringComparison.OrdinalIgnoreCase); continue; }
            int colon = line.IndexOf(':');
            if (!events || colon < 0) continue;
            string key = line[..colon].ToLowerInvariant(), value = line[(colon + 1)..];
            if (key == "format") fields = value.Split(',').Select(f => f.Trim(' ', '\t').ToLowerInvariant()).Where(f => f.Length > 0).ToArray();
            else if (key == "dialogue")
            {
                int start = Array.IndexOf(fields, "start"), end = Array.IndexOf(fields, "end"), body = Array.IndexOf(fields, "text");
                if (start < 0 || end < 0 || body != fields.Length - 1) throw new UserError(NoAssFormat);
                // The text is the last field and may itself contain commas.
                var parts = value.Split(',', fields.Length);
                if (parts.Length != fields.Length || Time(parts[start]) is not { } a || Time(parts[end]) is not { } b) continue;
                string words = Plain(parts[body]).Replace("\\N", "\n").Replace("\\n", "\n").Replace("\\h", " ");
                cues.Add(new SubtitleCue(a, b, words.Trim()));
                if (cues.Count > MaxCues) throw new UserError(TooMany);
            }
        }
        return cues;
    }

    /// Each lyric runs until the next timestamp; the last one gets four seconds. [offset:+500]
    /// moves every lyric 0.5 s earlier.
    private static List<SubtitleCue> ParseLrc(string text)
    {
        var stamps = new List<(long Time, string Lyric)>(); long bytes = 0, offset = 0;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim(' ', '\t'); var times = new List<long>();
            while (line.StartsWith('[') && line.IndexOf(']') is var close and > 0)
            {
                string tag = line[1..close];
                if (tag.StartsWith("offset:", StringComparison.OrdinalIgnoreCase)
                    && double.TryParse(tag[7..].Trim(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double ms)
                    && Math.Abs(ms) <= MaxTime)
                    offset = (long)Math.Round(ms);
                if (Time(tag) is not { } t) break; // [ar:…], [ti:…] and other metadata.
                times.Add(t); line = line[(close + 1)..];
            }
            if (times.Count == 0) continue;
            string lyric = Plain(line).Trim(' ', '\t');
            bytes += (long)System.Text.Encoding.UTF8.GetByteCount(lyric) * times.Count;
            if (stamps.Count + times.Count > MaxCues || bytes > MaxTextBytes) throw new UserError(TooMany);
            foreach (long t in times) stamps.Add((t, lyric));
        }
        var ordered = stamps.Select(s => (Time: Math.Max(0, s.Time - offset), s.Lyric)).OrderBy(s => s.Time).ToList();
        var cues = new List<SubtitleCue>();
        for (int i = 0; i < ordered.Count; i++)
        {
            long end = i + 1 < ordered.Count ? ordered[i + 1].Time : ordered[i].Time + 4000;
            if (ordered[i].Lyric.Length > 0) cues.Add(new SubtitleCue(ordered[i].Time, end, ordered[i].Lyric));
        }
        return cues;
    }

    // MARK: Writing

    private static readonly Regex BlankLines = new(@"\n\s*\n+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));

    /// The cues in a format, with \n line ends.
    public static string Write(IReadOnlyList<SubtitleCue> source, string extension)
    {
        // A blank line inside a cue would end it early in block formats.
        var cues = source.Select(c => c with { Text = BlankLines.Replace(c.Text, "\n") }).ToList();
        var out_ = new StringBuilder();
        switch (extension)
        {
            case "vtt":
                out_.Append("WEBVTT\n\n");
                out_.AppendJoin("\n", cues.Select(c => $"{Stamp(c.Start, '.')} --> {Stamp(c.End, '.')}\n{c.Text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")}\n"));
                break;
            case "ass":
                out_.Append("[Script Info]\nScriptType: v4.00+\nWrapStyle: 0\nScaledBorderAndShadow: yes\nPlayResX: 1920\nPlayResY: 1080\n\n[V4+ Styles]\n"
                    + "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n"
                    + "Style: Default,Arial,64,&H00FFFFFF,&H000000FF,&H00000000,&H64000000,0,0,0,0,100,100,0,0,1,3,1,2,40,40,50,1\n\n[Events]\n"
                    + "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n");
                foreach (var c in cues)
                {
                    // Braces start override tags in ASS; there is no escape for them.
                    string body = c.Text.Replace('{', '(').Replace('}', ')').Replace("\\", "＼").Replace("\n", "\\N");
                    out_.Append($"Dialogue: 0,{AssStamp(c.Start)},{AssStamp(c.End)},Default,,0,0,0,,{body}\n");
                }
                break;
            case "lrc":
                for (int i = 0; i < cues.Count; i++)
                {
                    // A lyric that itself starts with "[" would be read back as another tag.
                    string lyric = cues[i].Text.Replace("\n", " ");
                    out_.Append('[').Append(LrcStamp(cues[i].Start)).Append(']').Append(lyric.StartsWith('[') ? "​" : "").Append(lyric).Append('\n');
                    // An empty line ends the lyric when the next one doesn't start right away.
                    long next = i + 1 < cues.Count ? cues[i + 1].Start : long.MaxValue;
                    if (next - cues[i].End > 50) out_.Append('[').Append(LrcStamp(cues[i].End)).Append("]\n");
                }
                break;
            case "sbv":
                out_.AppendJoin("\n", cues.Select(c => $"{SbvStamp(c.Start)},{SbvStamp(c.End)}\n{c.Text}\n"));
                break;
            case "txt":
                out_.AppendJoin("\n", cues.Select(c => c.Text)).Append('\n');
                break;
            default:
                out_.AppendJoin("\n", cues.Select((c, i) => $"{i + 1}\n{Stamp(c.Start, ',')} --> {Stamp(c.End, ',')}\n{c.Text}\n"));
                break;
        }
        return out_.ToString();
    }

    private static long Clamp(long ms) => Math.Clamp(ms, 0, MaxTime);
    public static string Stamp(long time, char separator)
    {
        long ms = Clamp(time);
        return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}{3}{4:000}", ms / 3_600_000, ms / 60_000 % 60, ms / 1000 % 60, separator, ms % 1000);
    }
    private static string AssStamp(long time)
    {
        long cs = (Clamp(time) + 5) / 10;
        return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}.{3:00}", cs / 360_000, cs / 6000 % 60, cs / 100 % 60, cs % 100);
    }
    private static string SbvStamp(long time)
    {
        long ms = Clamp(time);
        return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}.{3:000}", ms / 3_600_000, ms / 60_000 % 60, ms / 1000 % 60, ms % 1000);
    }
    private static string LrcStamp(long time)
    {
        long cs = (Clamp(time) + 5) / 10;
        return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}.{2:00}", cs / 6000, cs / 100 % 60, cs % 100);
    }
}
