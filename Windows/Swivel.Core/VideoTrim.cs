using System;
using System.Globalization;

namespace Swivel.Core;

/// The part of a video to keep, in milliseconds from its start, and whether its sound is kept.
public sealed record VideoTrimPlan(long Start, long End, bool KeepSound)
{
    public long Length => End - Start;
}

/// Trim Video…: reading and writing the start and end times typed in the window, and checking a
/// choice before Windows encodes it. Encoding is done by the platform.
public static class VideoTrim
{
    public const long MinLength = 100;
    public const string BadTime = "Type a time such as 12, 1:05 or 1:02:03.5 (hours, minutes, seconds; up to three decimals).";
    public const string OutOfRange = "The start must come before the end, both within the video, keeping at least 0.1 seconds.";
    public const string Unchanged = "This keeps the whole video with its sound. Move the start or end, or remove the sound.";
    public const string UnchangedSilent = "This keeps the whole video, which has no sound. Move the start or end.";

    /// Seconds, minutes:seconds or hours:minutes:seconds, with up to three decimals on the
    /// seconds; minutes and seconds after a colon are below 60. Western digits only. null when
    /// the text isn't such a time.
    public static long? Parse(string text)
    {
        var parts = text.Trim().Split(':');
        if (parts.Length > 3) return null;
        long total = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            bool last = i == parts.Length - 1;
            string part = parts[i];
            string whole = part, fraction = "";
            int dot = part.IndexOf('.');
            if (dot >= 0)
            {
                if (!last) return null;
                whole = part[..dot]; fraction = part[(dot + 1)..];
                if (fraction.Length is 0 or > 3 || !Digits(fraction)) return null;
            }
            if (whole.Length == 0 || whole.Length > 7 || !Digits(whole)) return null;
            long value = long.Parse(whole, NumberStyles.None, CultureInfo.InvariantCulture);
            if (i > 0 && value >= 60) return null;
            total = total * 60 + value;
            if (last) total = total * 1000 + (fraction.Length == 0 ? 0 : long.Parse(fraction.PadRight(3, '0'), NumberStyles.None, CultureInfo.InvariantCulture));
        }
        return total;
    }

    private static bool Digits(string text)
    {
        foreach (char c in text) if (c is < '0' or > '9') return false;
        return true;
    }

    /// m:ss.fff, or h:mm:ss.fff when the video is an hour or longer; trailing zero decimals dropped.
    public static string Format(long milliseconds, bool hours)
    {
        if (milliseconds < 0) throw new ArgumentOutOfRangeException(nameof(milliseconds));
        long ms = milliseconds % 1000, seconds = milliseconds / 1000;
        string text = hours
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", seconds / 3600, seconds / 60 % 60, seconds % 60)
            : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", seconds / 60, seconds % 60);
        return ms == 0 ? text : text + "." + ms.ToString("000", CultureInfo.InvariantCulture).TrimEnd('0');
    }

    /// The video's length in whole milliseconds, as shown and as the end of the last frame.
    public static long Duration(double seconds) =>
        double.IsFinite(seconds) && seconds > 0 ? (long)Math.Floor(seconds * 1000) : throw new UserError(SubtitleBurn.Unsupported);

    /// A video without sound has nothing to remove, so keeping all of it changes nothing.
    public static VideoTrimPlan Plan(long start, long end, long duration, bool keepSound, bool hasSound = true)
    {
        if (start < 0 || end > duration || end - start < MinLength) throw new UserError(OutOfRange);
        if (start == 0 && end == duration && (keepSound || !hasSound)) throw new UserError(hasSound ? Unchanged : UnchangedSilent);
        return new VideoTrimPlan(start, end, keepSound && hasSound);
    }
}
