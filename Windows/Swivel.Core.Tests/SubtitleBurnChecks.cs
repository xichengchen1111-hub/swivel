using System;
using System.Linq;
using Swivel.Core;

internal static class SubtitleBurnChecks
{
    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        SubtitleBurn.CheckVideo(64, 64, 1); SubtitleBurn.CheckVideo(3840, 2160, 7200);
        reject(() => SubtitleBurn.CheckVideo(3841, 2160, 10), "Subtitle burning refuses more than 3840 pixels wide");
        reject(() => SubtitleBurn.CheckVideo(1920, 2161, 10), "Subtitle burning refuses more than 2160 pixels high");
        reject(() => SubtitleBurn.CheckVideo(63, 100, 10), "Subtitle burning refuses tiny videos");
        reject(() => SubtitleBurn.CheckVideo(640, 360, 7200.5), "Subtitle burning refuses more than two hours");
        reject(() => SubtitleBurn.CheckVideo(640, 360, double.NaN), "Subtitle burning refuses an unknown length");
        reject(() => SubtitleBurn.CheckVideo(1081, 608, 10), "Subtitle burning refuses an odd width (H.264 needs even sizes)");

        var medium = SubtitleBurn.Layout(1920, 1080, 1);
        check(medium == new SubtitleLayout(63, 1728, 65, 21, 432), "Subtitle layout for 1080p, medium text");
        check(SubtitleBurn.Layout(1920, 1080, 0).FontPixels < medium.FontPixels && SubtitleBurn.Layout(1920, 1080, 2).FontPixels > medium.FontPixels && SubtitleBurn.Layout(64, 64, 0).FontPixels == 12,
            "Subtitle text sizes grow from small to large, at least 12 pixels");
        reject(() => SubtitleBurn.Layout(640, 360, 3), "Subtitle layout refuses an unknown size");

        var plan = SubtitleBurn.Plan(new[] {
            new SubtitleCue(5000, 7000, "  second  "),
            new SubtitleCue(1000, 6000, "first\r\n\r\n  line two "),
            new SubtitleCue(6950, 7000, "too brief to read"),
            new SubtitleCue(7000, 9000, "   "),
            new SubtitleCue(9500, 20000, "runs past the end"),
            new SubtitleCue(12000, 13000, "after the end"),
        }, 11.2);
        check(plan.Count == 3 && plan[0] == new SubtitleCue(1000, 5000, "first\nline two") && plan[1] == new SubtitleCue(5000, 7000, "second") && plan[2] == new SubtitleCue(9500, 11200, "runs past the end"),
            "Subtitle plan orders cues, ends each before the next, trims lines, drops blank, brief and late cues");
        reject(() => SubtitleBurn.Plan(new[] { new SubtitleCue(20000, 21000, "late") }, 10), "Subtitle plan refuses when no cue is inside the video");
        reject(() => SubtitleBurn.Plan(Array.Empty<SubtitleCue>(), 10), "Subtitle plan refuses no cues");
        var brief = SubtitleBurn.Plan(new[] { new SubtitleCue(1000, 5000, "long"), new SubtitleCue(1050, 1100, "blip") }, 10);
        check(brief.Count == 1 && brief[0] == new SubtitleCue(1000, 5000, "long"), "Subtitle plan drops a brief cue before cutting, so it never hides a long one");
    }
}
