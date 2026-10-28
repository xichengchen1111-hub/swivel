using System;
using System.Collections.Generic;
using System.Linq;

namespace Swivel.Core;

/// Where and how large one burned-in subtitle is drawn, in video pixels.
public sealed record SubtitleLayout(int FontPixels, int MaxWidth, int BottomMargin, int Padding, int MaxHeight);

/// The plan for burning subtitles into a video: which cues appear when (no two at once), and the
/// text geometry for the video's size. Drawing and encoding are done by the platform.
public static class SubtitleBurn
{
    public const int MaxWidth = 3840, MaxHeight = 2160, MinSide = 64, MaxLines = 3;
    public const double MaxSeconds = 2 * 3600;
    public static readonly string[] SizeNames = { "Small", "Medium", "Large" };
    private static readonly double[] Sizes = { 0.045, 0.058, 0.075 };
    public const string Unsupported = "Choose a video from 64 × 64 to 3840 × 2160 pixels, landscape or portrait (even width and height), and at most two hours long.";
    public const string NoCues = "None of the subtitles fall within the video's length.";

    /// The 3840 × 2160 limit holds either way round, so a 4K phone video turned upright (2160 × 3840) fits.
    public static void CheckVideo(int width, int height, double seconds)
    {
        if (width < MinSide || height < MinSide || Math.Max(width, height) > MaxWidth || Math.Min(width, height) > MaxHeight || width % 2 != 0 || height % 2 != 0 || !double.IsFinite(seconds) || seconds <= 0 || seconds > MaxSeconds)
            throw new UserError(Unsupported);
    }

    /// Text size, width limit and margins for a video; size 0–2 is Small, Medium or Large.
    public static SubtitleLayout Layout(int width, int height, int size)
    {
        if (size is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(size));
        int font = Math.Max(12, (int)Math.Round(height * Sizes[size]));
        int padding = Math.Max(4, font / 3);
        return new SubtitleLayout(font, (int)(width * 0.9), Math.Max(8, (int)Math.Round(height * 0.06)), padding, (int)(height * 0.4));
    }

    /// Cues inside the video, each ending before the next starts, in order; blank cues and blank
    /// lines dropped, cues shorter than 0.1 s skipped. At least one cue must remain.
    public static IReadOnlyList<SubtitleCue> Plan(IReadOnlyList<SubtitleCue> cues, double seconds)
    {
        long end = (long)Math.Floor(seconds * 1000);
        // Cues too brief to read are dropped before the others are cut, so they never hide a long one.
        var ordered = cues.Where(c => Math.Min(c.End, end) - Math.Max(0, c.Start) >= 100 && !string.IsNullOrWhiteSpace(c.Text))
            .OrderBy(c => c.Start).ThenBy(c => c.End).ToList();
        var plan = new List<SubtitleCue>();
        for (int i = 0; i < ordered.Count; i++)
        {
            var c = ordered[i];
            long stop = Math.Min(c.End, end);
            if (i + 1 < ordered.Count) stop = Math.Min(stop, ordered[i + 1].Start);
            if (stop - Math.Max(0, c.Start) < 100) continue; // hidden by the next one
            var lines = c.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);
            plan.Add(new SubtitleCue(Math.Max(0, c.Start), stop, string.Join("\n", lines)));
        }
        if (plan.Count == 0) throw new UserError(NoCues);
        return plan;
    }
}
