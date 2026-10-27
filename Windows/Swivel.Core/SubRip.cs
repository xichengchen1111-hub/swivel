using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Swivel.Core;

public sealed record SubtitleCue(long Start, long End, string Text);
public static class SubRip
{
    public const int MaxBytes = 2 * 1024 * 1024, MaxCues = 10000;
    public const long MaxTime = 999L * 3600000 + 3599999;
    private const string Invalid = "Use a valid UTF-8 SRT file: at most 2 MB and 10000 cues, with increasing start times and an end after each start.";
    private static readonly Regex Timing = new(@"^([0-9]{2,3}):([0-9]{2}):([0-9]{2}),([0-9]{3}) --> ([0-9]{2,3}):([0-9]{2}):([0-9]{2}),([0-9]{3})$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length > MaxBytes) throw new UserError(Invalid);
        try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\ufeff'); }
        catch (DecoderFallbackException) { throw new UserError(Invalid); }
    }
    public static IReadOnlyList<SubtitleCue> Parse(string text)
    {
        if (text.Length > MaxBytes || Encoding.UTF8.GetByteCount(text) > MaxBytes || text.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t')) throw new UserError(Invalid);
        string[] lines = text.TrimStart('\ufeff').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var cues = new List<SubtitleCue>();
        int i = 0;
        while (i < lines.Length)
        {
            while (i < lines.Length && string.IsNullOrWhiteSpace(lines[i])) i++;
            if (i == lines.Length) break;
            if (!int.TryParse(lines[i++], NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number < 1 || i == lines.Length) throw new UserError(Invalid);
            string timing = lines[i++].Trim();
            if (timing.Length > 31) throw new UserError(Invalid);
            var match = Timing.Match(timing);
            if (!match.Success) throw new UserError(Invalid);
            long Read(int first)
            {
                int Value(int n) => int.Parse(match.Groups[n].Value, CultureInfo.InvariantCulture);
                int hour = Value(first), minute = Value(first + 1), second = Value(first + 2), millisecond = Value(first + 3);
                if (minute > 59 || second > 59) throw new UserError(Invalid);
                return hour * 3600000L + minute * 60000 + second * 1000 + millisecond;
            }
            long start = Read(1), end = Read(5);
            var content = new List<string>();
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i])) content.Add(lines[i++]);
            if (end <= start || content.Count == 0 || (cues.Count > 0 && start < cues[^1].Start) || cues.Count >= MaxCues) throw new UserError(Invalid);
            // SRT tags remain literal text here; no HTML, file or URL renderer is used.
            cues.Add(new(start, end, string.Join("\n", content)));
        }
        if (cues.Count == 0) throw new UserError(Invalid);
        return cues;
    }
    public static IReadOnlyList<SubtitleCue> Shift(IReadOnlyList<SubtitleCue> cues, double seconds, double speed)
    {
        if (!double.IsFinite(seconds) || Math.Abs(seconds) > 36000 || !double.IsFinite(speed) || speed is < .25 or > 4)
            throw new UserError("Choose an offset within 10 hours and speed from 0.25× to 4×.");
        return cues.Select(cue => {
            long Map(long value) => checked((long)Math.Round(value / speed + seconds * 1000, MidpointRounding.AwayFromZero));
            long start = Map(cue.Start), end = Map(cue.End);
            if (start < 0 || end > MaxTime || end <= start) throw new UserError("The timing change would produce negative, empty or out-of-range cues. Adjust the offset or speed.");
            return cue with { Start = start, End = end };
        }).ToArray();
    }
    public static string Time(long milliseconds) => string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00},{3:000}", milliseconds / 3600000, milliseconds / 60000 % 60, milliseconds / 1000 % 60, milliseconds % 1000);
    public static string Write(IReadOnlyList<SubtitleCue> cues) => string.Join("\r\n\r\n", cues.Select((x, i) => $"{i + 1}\r\n{Time(x.Start)} --> {Time(x.End)}\r\n{x.Text.Replace("\n", "\r\n")}")) + "\r\n";
    public static string Transcript(IReadOnlyList<SubtitleCue> cues) => string.Join("\r\n", cues.Select(x => x.Text.Replace("\n", "\r\n"))) + "\r\n";
}
