using System;
using System.Globalization;

namespace Swivel.Core;

/// The part of a video to keep, in milliseconds from its start, whether its sound is kept, and
/// optional picture changes and speed (percent). No picture change is null.
public sealed record VideoTrimPlan(long Start, long End, bool KeepSound, VideoPicture? Picture = null, int Speed = 100, WaveNoise? Noise = null)
{
    public long Length => End - Start;
    /// Milliseconds of the new video.
    public long OutputLength => VideoEdit.OutputLength(Length, Speed);
    /// More than the cut and the sound changed: saved as “name edited.mp4”.
    public bool Edited => Picture is not null || Speed != 100 || Noise is not null;
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

    /// A video without sound has nothing to remove, so keeping all of it changes nothing. At
    /// another speed a kept sound is time-stretched to the new length with its pitch kept.
    public static VideoTrimPlan Plan(long start, long end, long duration, bool keepSound, bool hasSound = true, VideoPicture? picture = null, int speed = 100, WaveNoise? noise = null, int soundRate = 48000)
    {
        VideoEdit.ValidateSpeed(speed);
        if (picture is not null) { VideoEdit.Validate(picture); if (picture.IsIdentity) picture = null; }
        if (start < 0 || end > duration || end - start < MinLength) throw new UserError(OutOfRange);
        if (!keepSound || !hasSound) noise = null;
        // Sample seconds belong to the kept part at its original speed. The decoder's
        // actual frame boundaries are checked again before processing.
        noise?.Range(new WaveInfo(soundRate, 1, 16, 44, (long)Math.Round((end - start) * soundRate / 1000.0, MidpointRounding.AwayFromZero) * 2));
        bool edited = picture is not null || speed != 100 || noise is not null;
        if (start == 0 && end == duration && !edited && (keepSound || !hasSound)) throw new UserError(hasSound ? Unchanged : UnchangedSilent);
        return new VideoTrimPlan(start, end, keepSound && hasSound, picture, speed, noise);
    }
}
