using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading;

namespace Swivel.Core;

/// The shape Trim Video… can crop the picture to, as seen after turning and flipping.
public enum VideoAspect { Original, Wide, Tall, Square, Portrait, Classic }

/// A rectangle in whole pixels.
public readonly record struct PixelRect(int X, int Y, int Width, int Height);

/// How the picture changes: clockwise quarter turns, then flips of the turned picture as seen,
/// then a crop to `Aspect` slid by `Position` (0 left/top, 1 right/bottom of what is seen).
public sealed record VideoPicture(int QuarterTurns = 0, bool FlipHorizontal = false, bool FlipVertical = false, VideoAspect Aspect = VideoAspect.Original, double Position = 0.5)
{
    public bool IsIdentity => QuarterTurns == 0 && !FlipHorizontal && !FlipVertical && Aspect == VideoAspect.Original;
}

/// What Windows is asked for: crop `SourceCrop` from the upright source, turn it `Rotation`
/// quarter turns clockwise, then mirror it left–right when `Mirror`. `Seen` is the crop in the
/// turned and flipped picture; the output is `Width` × `Height`.
public sealed record VideoGeometry(int Width, int Height, PixelRect SourceCrop, int Rotation, bool Mirror, PixelRect Seen, int SeenWidth, int SeenHeight);

/// Picture and speed changes for Trim Video…: pure geometry and timing; Windows does the pixels.
public static class VideoEdit
{
    /// Percent of normal speed, as offered (the Mac's 0.25×–4× steps plus 1.25×, 1.5× and 3×).
    public static readonly int[] Speeds = { 25, 50, 75, 100, 125, 150, 200, 300, 400 };
    public const int MinSide = 16;
    public const string TooSmall = "This crop would leave a picture smaller than 16 pixels on a side. Choose another shape.";
    /// Pixels a second after a speed-up, at most 3840 × 2160 at 60 fps (H.264 level 5.2), with 3%
    /// to spare since Windows reports average frame rates (30.659 for a 30 fps test video).
    public const double MaxPixelRate = 3840.0 * 2160 * 60;
    public const string TooFast = "At this speed the new video would show more than 3840 × 2160 pixels 60 times a second, which many players can't decode. Choose a lower speed or a smaller crop.";
    public const string CannotRetime = "Windows wrote a video whose timing Swivel can't change for this speed. No file was saved.";

    public static (int Width, int Height) Ratio(VideoAspect aspect) => aspect switch
    {
        VideoAspect.Wide => (16, 9), VideoAspect.Tall => (9, 16), VideoAspect.Square => (1, 1),
        VideoAspect.Portrait => (4, 5), VideoAspect.Classic => (4, 3),
        _ => throw new ArgumentOutOfRangeException(nameof(aspect)),
    };

    public static void Validate(VideoPicture picture)
    {
        if (picture.QuarterTurns is < 0 or > 3 || !Enum.IsDefined(picture.Aspect) || !double.IsFinite(picture.Position) || picture.Position is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(picture));
    }

    public static void ValidateSpeed(int percent)
    {
        if (Array.IndexOf(Speeds, percent) < 0) throw new ArgumentOutOfRangeException(nameof(percent));
    }

    /// The largest `aspect` rectangle in a `width` × `height` picture (both even), with even sides
    /// and an even offset, slid along the axis it cuts by `position`.
    public static PixelRect Crop(int width, int height, VideoAspect aspect, double position)
    {
        if (width <= 0 || height <= 0 || width % 2 != 0 || height % 2 != 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (aspect == VideoAspect.Original) return new PixelRect(0, 0, width, height);
        if (!double.IsFinite(position) || position is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(position));
        var (rw, rh) = Ratio(aspect);
        int w = width, h = height;
        if ((long)width * rh > (long)height * rw) w = (int)((long)height * rw / rh) & ~1;
        else h = (int)((long)width * rh / rw) & ~1;
        if (w < MinSide || h < MinSide) throw new UserError(TooSmall);
        int x = (int)Math.Round((width - w) / 2 * position, MidpointRounding.AwayFromZero) * 2;
        int y = (int)Math.Round((height - h) / 2 * position, MidpointRounding.AwayFromZero) * 2;
        return new PixelRect(x, y, w, h);
    }

    /// `rect` inside a `width` × `height` picture, after that picture turns `turns` quarter turns clockwise.
    public static PixelRect Turn(PixelRect rect, int width, int height, int turns)
    {
        for (int i = 0; i < ((turns % 4) + 4) % 4; i++)
        {
            rect = new PixelRect(height - rect.Y - rect.Height, rect.X, rect.Height, rect.Width);
            (width, height) = (height, width);
        }
        return rect;
    }

    /// The upright `width` × `height` (even) source through `picture`.
    public static VideoGeometry Geometry(int width, int height, VideoPicture picture)
    {
        Validate(picture);
        int turns = picture.QuarterTurns;
        int seenW = turns % 2 == 0 ? width : height, seenH = turns % 2 == 0 ? height : width;
        var seen = Crop(seenW, seenH, picture.Aspect, picture.Position);
        // Undo the flips in the seen picture, then turn back to the source.
        var unflipped = new PixelRect(picture.FlipHorizontal ? seenW - seen.X - seen.Width : seen.X, picture.FlipVertical ? seenH - seen.Y - seen.Height : seen.Y, seen.Width, seen.Height);
        var source = Turn(unflipped, seenW, seenH, 4 - turns);
        // Flips after a turn: both = a half turn; up–down = left–right after a half turn.
        int rotation = (turns + (picture.FlipVertical ? 2 : 0)) % 4;
        bool mirror = picture.FlipHorizontal != picture.FlipVertical;
        return new VideoGeometry(seen.Width, seen.Height, source, rotation, mirror, seen, seenW, seenH);
    }

    /// Where a source pixel's centre lands in the output (for checks): crop, turn, mirror.
    public static (double X, double Y) Place(VideoGeometry g, double x, double y)
    {
        double u = x - g.SourceCrop.X, v = y - g.SourceCrop.Y; int w = g.SourceCrop.Width, h = g.SourceCrop.Height;
        for (int i = 0; i < g.Rotation; i++) { (u, v) = (h - v, u); (w, h) = (h, w); }
        return g.Mirror ? (w - u, v) : (u, v);
    }

    /// A speed-up multiplies the frame rate; refuse what common decoders can't keep up with.
    public static void CheckRate(int width, int height, double fps, int percent)
    {
        ValidateSpeed(percent);
        if (width * (double)height * fps * percent / 100 > MaxPixelRate * 1.03) throw new UserError(TooFast);
    }

    /// Bits a second to ask the encoder for, so the retimed video stays within `cap` when played.
    public static double EncodeBitrate(double wanted, int percent, double cap = 100_000_000) => Math.Min(wanted, cap * 100 / Math.Max(100, percent));

    public const string SoundTooLong = "At this speed the sound of the kept part would be too long to time-stretch (more than two hours or 512 MB of PCM). Remove the sound or keep a shorter part.";
    public const string SoundFailed = "Windows could not prepare the sound at this speed, so no file was saved. Remove the sound to save the video without it.";

    /// The PCM format the sound is decoded to before it is time-stretched: Windows' AAC encoder
    /// takes 44.1 or 48 kHz, so 44.1 kHz stays and anything else becomes 48 kHz; mono stays
    /// mono and anything else becomes stereo.
    public static (int Rate, int Channels) SoundFormat(uint rate, uint channels) => (rate == 44100 ? 44100 : 48000, channels == 1 ? 1 : 2);

    /// Sample frames lasting as long as `duration` in `timescale` (a movie's length), rounded.
    public static long SoundFrames(ulong duration, uint timescale, int rate)
    {
        if (timescale == 0 || rate <= 0) throw new ArgumentOutOfRangeException(nameof(timescale));
        return checked((long)(((Int128)duration * rate + timescale / 2) / timescale));
    }

    /// Refuses a kept sound whose decoded or stretched PCM16 would pass the WAV limits.
    public static void CheckSound(long length, int percent, int rate, int channels)
    {
        ValidateSpeed(percent);
        foreach (long ms in new[] { length, OutputLength(length, percent) })
        {
            long frames = (long)(((Int128)ms * rate + 999) / 1000) + rate; // a second to spare for decoder rounding
            if (ms > PcmWave.MaxSeconds * 1000L || frames * channels * 2 + 44 > PcmWave.MaxBytes) throw new UserError(SoundTooLong);
        }
    }

    /// Whether decoded sound of `frames` at `rate` is the kept part of `length` milliseconds, within
    /// a tenth of a second (Windows' cut ends within a millisecond on the CI runner).
    public static bool SoundMatches(long frames, int rate, long length) => Math.Abs(frames * 1000.0 / rate - length) <= 100;

    /// Cleans at the original speed before the existing stretch. The caller supplies a
    /// private seekable scratch stream, so a long cleaned recording need not live in RAM.
    /// With no noise settings the existing speed path is used without any change.
    public const string NoiseFailed = "Windows could not prepare the sound for noise reduction. Nothing was saved. Turn noise reduction off to save without it.";
    /// A sample that ends up to 100 ms past the decoded sound (the decoder's length can differ
    /// from the cut by that much) moves back to end at the sound's end, keeping its length.
    public static WaveNoise FitNoise(WaveNoise noise, WaveInfo info)
    {
        double over = noise.End - info.Seconds;
        if (over <= 0 || over > .1 || noise.Start < over) return noise;
        return noise with { Start = noise.Start - over, End = info.Seconds };
    }
    public static long PrepareSound(Stream input, Stream output, Stream? cleaned, WaveNoise? noise, int percent, long frames, double fps, CancellationToken token)
    {
        if (noise is not null)
        {
            if (cleaned is null) throw new ArgumentNullException(nameof(cleaned));
            noise = FitNoise(noise, PcmWave.Read(input, token));
            WaveDenoiser.Export(input, cleaned, noise, token);
            cleaned.Position = 0;
            input = cleaned;
        }
        if (percent != 100)
        {
            StretchSound(input, output, percent, frames, fps, token);
            return frames;
        }
        var info = PcmWave.Read(input, token);
        PcmWave.Export(input, output, new(0, info.Seconds), token);
        return Math.Min(frames, info.Frames);
    }

    /// Time-stretches a PCM WAV to `frames` sample frames at `percent` speed keeping its pitch
    /// (WaveStretch), as 16-bit PCM; `frames` comes from the retimed video so both end together.
    /// `fps` is the source's frame rate: the video's length is whole frames, so it may differ from
    /// the cut by up to a frame, which the speed change stretches by 100/percent.
    public static void StretchSound(Stream input, Stream output, int percent, long frames, double fps, CancellationToken token)
    {
        ValidateSpeed(percent);
        var source = PcmWave.Read(input, token);
        if (percent == 100 || source.Bits != 16 || frames < 1) throw new UserError(SoundFailed);
        long natural;
        try { natural = new WaveSpeed(percent, true).Frames(source); } catch (UserError) { throw new UserError(SoundTooLong); }
        // The video's length and the stretched sound's agree to a tenth of a second (or 2%) plus
        // two source frames at the new speed.
        double frame = double.IsFinite(fps) && fps >= 1 ? 1 / fps : 1;
        double slack = Math.Max(source.Rate / 10.0, natural / 50.0) + 2 * frame * source.Rate * 100 / percent;
        if (Math.Abs(frames - natural) > slack) throw new UserError(SoundFailed);
        if (frames * source.Block + 44 > PcmWave.MaxBytes || frames > (long)source.Rate * PcmWave.MaxSeconds) throw new UserError(SoundTooLong);
        WaveStretch.Export(input, output, source, percent, frames, token, false);
    }

    /// Milliseconds of output for `length` milliseconds of source at `percent` speed.
    public static long OutputLength(long length, int percent)
    {
        ValidateSpeed(percent);
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        return (long)(((Int128)length * 100 + percent / 2) / percent);
    }
}

/// Changes an MP4's speed without re-encoding: every video track's media timescale is multiplied
/// by percent/100 (each frame then lasts 100/percent times as long), and durations in the movie
/// timescale are scaled to match. Only fixed-size fields change, in place, so sample offsets stay
/// valid. Files with sound, fragments or a missing/duplicated required box are refused.
public static class Mp4Retime
{
    public const int MaxMovie = 64 << 20;

    public static void Apply(Stream stream, int percent)
    {
        VideoEdit.ValidateSpeed(percent);
        if (percent == 100) return;
        var (moov, movie) = Movie(stream);
        Patch(movie, percent);
        stream.Position = moov; stream.Write(movie); stream.Flush();
    }

    /// Whether every video track's header matrix is the identity (stored upright, unmirrored).
    /// null when the stream isn't a readable ISO media file with one movie box (e.g. WMV).
    public static bool? StraightVideo(Stream stream)
    {
        byte[] movie;
        try { (_, movie) = Movie(stream); } catch (UserError) { return null; }
        try
        {
            var (start, end) = Body(movie, 0, movie.Length, "moov");
            int video = 0;
            foreach (var (type, s, e) in Children(movie, start, end))
            {
                if (type != "trak") continue;
                int? tkhd = null; bool isVideo = false;
                foreach (var (t, s2, e2) in Children(movie, s, e))
                {
                    if (t == "tkhd") tkhd = s2;
                    else if (t == "mdia") foreach (var (t3, s3, _) in Children(movie, s2, e2)) if (t3 == "hdlr") { Need(movie, s3, 12); isVideo = Type(movie, s3 + 8) == "vide"; }
                }
                if (!isVideo) continue;
                if (tkhd is not { } th) return false;
                Need(movie, th, 1); if (movie[th] > 1) return false;
                int matrix = th + (movie[th] == 1 ? 52 : 40);
                Need(movie, matrix, 36);
                uint[] identity = { 0x10000, 0, 0, 0, 0x10000, 0, 0, 0, 0x40000000 };
                for (int i = 0; i < 9; i++) if (Be32(movie, matrix + i * 4) != identity[i]) return false;
                video++;
            }
            return video > 0;
        }
        catch (UserError) { return false; }
    }

    // The single movie box: where it starts and its bytes, header included.
    private static (long At, byte[] Bytes) Movie(Stream stream)
    {
        long length = stream.Length, at = 0, moov = -1, moovSize = 0;
        var header = new byte[16];
        while (at < length)
        {
            if (length - at < 8) Fail();
            stream.Position = at; stream.ReadExactly(header, 0, 8);
            long size = Be32(header, 0); string type = Type(header, 4); int head = 8;
            if (size == 1) { if (length - at < 16) Fail(); stream.ReadExactly(header, 8, 8); size = (long)Be64(header, 8); head = 16; }
            else if (size == 0) size = length - at;
            if (size < head || size > length - at) Fail();
            if (type == "moof" || type == "mvex") Fail();
            if (type == "moov") { if (moov >= 0) Fail(); moov = at; moovSize = size; }
            at += size;
        }
        if (moov < 0 || moovSize > MaxMovie) Fail();
        var movie = new byte[moovSize];
        stream.Position = moov; stream.ReadExactly(movie);
        return (moov, movie);
    }

    /// `movie` is one whole moov box, header included.
    public static void Patch(byte[] movie, int percent)
    {
        VideoEdit.ValidateSpeed(percent);
        var (moovStart, moovEnd) = Body(movie, 0, movie.Length, "moov");
        var writes = new List<Action>();
        int? mvhd = null; int tracks = 0;
        foreach (var (type, start, end) in Children(movie, moovStart, moovEnd))
        {
            if (type == "mvhd") { if (mvhd is not null) Fail(); mvhd = start; }
            else if (type == "mvex") Fail();
            else if (type == "trak") { tracks++; Track(movie, start, end, percent, writes); }
        }
        if (mvhd is not { } m || tracks == 0) { Fail(); return; }
        writes.Add(MovieDuration(movie, m, 16, 24, percent));
        foreach (var write in writes) write();
    }

    private static void Track(byte[] b, int start, int end, int percent, List<Action> writes)
    {
        int? tkhd = null, elst = null, mdhd = null, hdlr = null, stts = null, ctts = null, cslg = null;
        int elstEnd = 0, sttsEnd = 0, cttsEnd = 0, cslgEnd = 0;
        foreach (var (type, s, e) in Children(b, start, end))
        {
            if (type == "tkhd") One(ref tkhd, s);
            else if (type == "edts") foreach (var (t, s2, e2) in Children(b, s, e)) { if (t == "elst") { One(ref elst, s2); elstEnd = e2; } }
            else if (type == "mdia")
                foreach (var (t, s2, e2) in Children(b, s, e))
                {
                    if (t == "mdhd") One(ref mdhd, s2);
                    else if (t == "hdlr") One(ref hdlr, s2);
                    else if (t == "minf")
                        foreach (var (t3, s3, e3) in Children(b, s2, e2))
                            if (t3 == "stbl")
                                foreach (var (t4, s4, e4) in Children(b, s3, e3))
                                {
                                    if (t4 == "stts") { One(ref stts, s4); sttsEnd = e4; }
                                    else if (t4 == "ctts") { One(ref ctts, s4); cttsEnd = e4; }
                                    else if (t4 == "cslg") { One(ref cslg, s4); cslgEnd = e4; }
                                }
                }
        }
        if (tkhd is not { } th || mdhd is not { } md || hdlr is not { } hd || stts is not { } st) { Fail(); return; }
        Need(b, hd, 12); if (Type(b, hd + 8) != "vide") Fail();
        writes.Add(MovieDuration(b, th, 20, 28, percent));

        // Media timescale × k × percent / 100, with sample times × k when that isn't whole.
        Need(b, md, 1); bool v1 = b[md] == 1; if (b[md] > 1) Fail();
        int scaleAt = md + (v1 ? 20 : 12), durationAt = scaleAt + 4;
        Need(b, durationAt, v1 ? 8 : 4);
        BigInteger timescale = Be32(b, scaleAt), product = timescale * percent;
        if (timescale == 0) Fail();
        BigInteger k = 100 / BigInteger.GreatestCommonDivisor(product, 100), scaled = product * k / 100;
        if (scaled > uint.MaxValue) Fail();
        writes.Add(() => Put32(b, scaleAt, (uint)scaled));
        writes.Add(Times(b, durationAt, v1 ? 8 : 4, k, false));

        Need(b, st, 8); long count = Be32(b, st + 4); if (count > (sttsEnd - st - 8) / 8) Fail();
        for (int i = 0; i < count; i++) writes.Add(Times(b, st + 8 + i * 8 + 4, 4, k, false));
        if (ctts is { } ct)
        {
            Need(b, ct, 8); bool signed = b[ct] == 1; if (b[ct] > 1) Fail();
            long n = Be32(b, ct + 4); if (n > (cttsEnd - ct - 8) / 8) Fail();
            for (int i = 0; i < n; i++) writes.Add(Times(b, ct + 8 + i * 8 + 4, 4, k, signed));
        }
        if (cslg is { } cs)
        {
            Need(b, cs, 4); bool wide = b[cs] == 1; if (b[cs] > 1) Fail();
            int field = wide ? 8 : 4; if (cslgEnd - cs - 4 < 5 * field) Fail();
            for (int i = 0; i < 5; i++) writes.Add(Times(b, cs + 4 + i * field, field, k, true));
        }
        if (elst is { } el)
        {
            Need(b, el, 8); bool wide = b[el] == 1; if (b[el] > 1) Fail();
            int entry = wide ? 20 : 12, field = wide ? 8 : 4;
            long n = Be32(b, el + 4); if (n > (elstEnd - el - 8) / entry) Fail();
            for (int i = 0; i < n; i++)
            {
                int e = el + 8 + i * entry;
                writes.Add(Scale(b, e, field, percent));
                // Media time −1 is an empty edit; others are in the media timescale.
                if (field == 4 ? Be32(b, e + 4) != uint.MaxValue : Be64(b, e + 8) != ulong.MaxValue) writes.Add(Times(b, e + field, field, k, true));
            }
        }
    }

    // A duration field (version 0 at `at0`, version 1 at `at1`, after version/flags) in the movie timescale.
    private static Action MovieDuration(byte[] b, int box, int at0, int at1, int percent)
    {
        Need(b, box, 1); if (b[box] > 1) Fail();
        bool v1 = b[box] == 1;
        int at = box + (v1 ? at1 : at0), width = v1 ? 8 : 4;
        Need(b, at, width);
        return Scale(b, at, width, percent);
    }

    // Movie-timescale duration × 100 / percent, rounded; all ones (unknown) stays.
    private static Action Scale(byte[] b, int at, int width, int percent)
    {
        Need(b, at, width);
        BigInteger value = width == 4 ? Be32(b, at) : Be64(b, at), max = width == 4 ? uint.MaxValue : ulong.MaxValue;
        if (value == max) return () => { };
        BigInteger result = (value * 100 + percent / 2) / percent;
        if (result >= max) Fail();
        return () => Put(b, at, width, result);
    }

    // A media-timescale time × k.
    private static Action Times(byte[] b, int at, int width, BigInteger k, bool signed)
    {
        Need(b, at, width);
        BigInteger value = width == 4 ? (signed ? (BigInteger)(int)Be32(b, at) : Be32(b, at)) : (signed ? (BigInteger)(long)Be64(b, at) : Be64(b, at));
        BigInteger result = value * k;
        BigInteger low = signed ? (width == 4 ? int.MinValue : long.MinValue) : 0, high = signed ? (width == 4 ? int.MaxValue : long.MaxValue) : (width == 4 ? uint.MaxValue : ulong.MaxValue);
        if (result < low || result > high) Fail();
        return () => Put(b, at, width, result < 0 ? result + (BigInteger.One << (width * 8)) : result);
    }

    private static void Put(byte[] b, int at, int width, BigInteger value)
    {
        for (int i = width - 1; i >= 0; i--) { b[at + i] = (byte)(value & 0xFF); value >>= 8; }
    }

    private static void Put32(byte[] b, int at, uint value) => Put(b, at, 4, value);

    private static void One(ref int? slot, int start) { if (slot is not null) Fail(); slot = start; }

    // The body of the single box of `type` filling [start, end).
    private static (int Start, int End) Body(byte[] b, int start, int end, string type)
    {
        var children = Children(b, start, end);
        if (children.Count != 1 || children[0].Type != type) Fail();
        return (children[0].Start, children[0].End);
    }

    // Child boxes of [start, end): type and body range (after the header).
    private static List<(string Type, int Start, int End)> Children(byte[] b, int start, int end)
    {
        var list = new List<(string, int, int)>();
        int at = start;
        while (at < end)
        {
            if (end - at < 8) Fail();
            long size = Be32(b, at); int head = 8;
            if (size == 1) { if (end - at < 16) Fail(); ulong large = Be64(b, at + 8); size = large > int.MaxValue ? -1 : (long)large; head = 16; }
            if (size < head || size > end - at) Fail();
            list.Add((Type(b, at + 4), at + head, at + (int)size));
            at += (int)size;
        }
        return list;
    }

    private static void Need(byte[] b, int at, int count) { if (at < 0 || count > b.Length - at) Fail(); }
    private static uint Be32(byte[] b, int at) => (uint)(b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3]);
    private static ulong Be64(byte[] b, int at) => (ulong)Be32(b, at) << 32 | Be32(b, at + 4);
    private static string Type(byte[] b, int at) => string.Create(4, (b, at), (span, s) => { for (int i = 0; i < 4; i++) span[i] = (char)s.b[s.at + i]; });
    private static void Fail() => throw new UserError(VideoEdit.CannotRetime);
}
