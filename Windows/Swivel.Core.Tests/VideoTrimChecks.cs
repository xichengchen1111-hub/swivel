using System;
using Swivel.Core;

internal static class VideoTrimChecks
{
    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        check(VideoTrim.Parse("12") == 12_000 && VideoTrim.Parse(" 0 ") == 0 && VideoTrim.Parse("12.5") == 12_500 && VideoTrim.Parse("0.05") == 50 && VideoTrim.Parse("1.234") == 1_234,
            "Trim times read plain seconds with up to three decimals");
        check(VideoTrim.Parse("1:05") == 65_000 && VideoTrim.Parse("90:00") == 5_400_000 && VideoTrim.Parse("1:02:03.5") == 3_723_500 && VideoTrim.Parse("0:00:00.001") == 1,
            "Trim times read minutes:seconds and hours:minutes:seconds");
        foreach (var bad in new[] { "", " ", "1:60", "1:5:60", "1:2:3:4", "1.2:03", "1.", ".5", "1.2345", "-1", "+1", "1e3", "１２", "1,5", "1:", ":5", "1 :05", "12345678" })
            check(VideoTrim.Parse(bad) is null, "Trim times refuse “" + bad + "”");

        check(VideoTrim.Format(0, false) == "0:00" && VideoTrim.Format(65_500, false) == "1:05.5" && VideoTrim.Format(61_230, false) == "1:01.23" && VideoTrim.Format(5_400_000, false) == "90:00",
            "Trim times are shown as m:ss with trailing zero decimals dropped");
        check(VideoTrim.Format(3_723_500, true) == "1:02:03.5" && VideoTrim.Format(1, true) == "0:00:00.001", "Trim times are shown as h:mm:ss for long videos");
        foreach (long ms in new long[] { 0, 1, 999, 1000, 59_999, 60_000, 3_599_999, 3_600_000, 7_200_000 })
            foreach (bool hours in new[] { false, true })
                check(VideoTrim.Parse(VideoTrim.Format(ms, hours)) == ms, $"Trim time {ms} ms reads back the same ({hours})");
        reject(() => VideoTrim.Format(-1, false), "Trim times refuse a negative time");

        check(VideoTrim.Duration(10.0009) == 10_000 && VideoTrim.Duration(0.0999) == 99, "The video length is counted in whole milliseconds, rounded down");
        reject(() => VideoTrim.Duration(double.NaN), "The video length must be known");
        reject(() => VideoTrim.Duration(0), "The video length must be positive");

        var noise = new WaveNoise(.1, .3);
        var clean = VideoTrim.Plan(1000, 1500, 3000, true, noise: noise);
        check(clean.Noise == noise && clean.Edited, "Video noise sample is relative to the kept part");
        check(VideoTrim.Plan(0, 3000, 3000, true, noise: noise).Edited, "Cleaning alone permits keeping the whole picture");
        check(VideoTrim.Plan(0, 1500, 3000, false, noise: noise).Noise is null, "Removing sound disables noise reduction");
        check(VideoTrim.Plan(0, 1500, 3000, true, false, noise: noise).Noise is null, "A silent video has no noise reduction");
        reject(() => VideoTrim.Plan(1000, 1500, 3000, true, noise: new(1, 1.2)), "Noise sample cannot use source times outside the kept part");
        reject(() => VideoTrim.Plan(1000, 1500, 3000, true, speed: 50, noise: new(.4, .6)), "Noise sample bounds use original speed before stretching");
        check(VideoTrim.Plan(1000, 1500, 3000, true, speed: 400, noise: new(.3, .5)).Noise is not null, "Noise sample may end at the original kept end at 4x");
        reject(() => VideoTrim.Plan(0, 3000, 3000, true, noise: new(0, 2.01)), "Video noise sample remains limited to two seconds");
        check(VideoTrim.Plan(0, 1000, 3000, true, noise: new(.000011, .050001), soundRate: 44100).Noise is not null,
            "Video noise sample uses actual 44.1 kHz nearest-frame boundaries");
        reject(() => VideoTrim.Plan(0, 1000, 3000, true, noise: new(.000011, .050001), soundRate: 48000),
            "The same sample is too short at 48 kHz after nearest-frame rounding");
        var plan = VideoTrim.Plan(1_000, 4_000, 10_000, true);
        check(plan == new VideoTrimPlan(1_000, 4_000, true) && plan.Length == 3_000, "A trim keeps the part between start and end");
        check(VideoTrim.Plan(0, 10_000, 10_000, false) == new VideoTrimPlan(0, 10_000, false), "Removing only the sound is allowed");
        check(VideoTrim.Plan(0, 100, 10_000, true).Length == 100 && VideoTrim.Plan(9_900, 10_000, 10_000, true).Length == 100, "A trim may keep exactly 0.1 seconds at either end");
        reject(() => VideoTrim.Plan(0, 10_000, 10_000, true), "A trim that keeps everything with sound is refused");
        reject(() => VideoTrim.Plan(0, 10_000, 10_000, false, hasSound: false), "A silent video kept whole is refused: there is no sound to remove");
        check(VideoTrim.Plan(0, 5_000, 10_000, true, hasSound: false) == new VideoTrimPlan(0, 5_000, false), "A silent video's trim never claims to keep sound");
        reject(() => VideoTrim.Plan(0, 99, 10_000, true), "A trim shorter than 0.1 seconds is refused");
        reject(() => VideoTrim.Plan(5_000, 5_000, 10_000, true), "A trim whose start is its end is refused");
        reject(() => VideoTrim.Plan(6_000, 5_000, 10_000, true), "A trim whose start is after its end is refused");
        reject(() => VideoTrim.Plan(-1, 5_000, 10_000, true), "A trim before the video's start is refused");
        reject(() => VideoTrim.Plan(0, 10_001, 10_000, true), "A trim past the video's end is refused");
    }
}
