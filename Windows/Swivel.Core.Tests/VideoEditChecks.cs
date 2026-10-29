using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Swivel.Core;

internal static class VideoEditChecks
{
    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Crops(check, reject);
        Geometry(check, reject);
        Timing(check, reject);
        Retime(check, reject);
        foreach (var text in new[] { VideoEdit.TooSmall, VideoEdit.CannotRetime, VideoEdit.TooFast, "No turn", "90° clockwise", "180°", "90° counterclockwise", "Flip left to right", "Flip upside down", "Whole picture", "Turn",
            "Crop to shape", "Crop position", "Picture", "Speed", "Crop position (left to right, or top to bottom)", "The new video is {0} × {1} pixels and {2} long.",
            "At any speed but 1× the sound is removed: Windows' editing API used here can't change its speed.", "When the picture or speed changes, it is saved as “name edited.mp4” instead." })
            check(Strings.Table.TryGetValue(text, out var row) && row.Length == 3 && row.All(x => x.Length > 0 && (!text.Contains("{2}") || x.Contains("{0}") && x.Contains("{1}") && x.Contains("{2}"))), "Video edit message translated: " + text);
    }

    private static void Crops(Action<bool, string> check, Action<Action, string> reject)
    {
        check(VideoEdit.Crop(1920, 1080, VideoAspect.Original, 0.3) == new PixelRect(0, 0, 1920, 1080), "No crop keeps the whole picture");
        check(VideoEdit.Crop(1920, 1080, VideoAspect.Wide, 0.5) == new PixelRect(0, 0, 1920, 1080), "16:9 of a 16:9 picture is all of it");
        check(VideoEdit.Crop(1920, 1080, VideoAspect.Square, 0) == new PixelRect(0, 0, 1080, 1080)
            && VideoEdit.Crop(1920, 1080, VideoAspect.Square, 1) == new PixelRect(840, 0, 1080, 1080)
            && VideoEdit.Crop(1920, 1080, VideoAspect.Square, 0.5) == new PixelRect(420, 0, 1080, 1080), "A square crop of a wide picture slides left to right");
        check(VideoEdit.Crop(1920, 1080, VideoAspect.Tall, 0.5) == new PixelRect(658, 0, 606, 1080), "9:16 of 1920×1080 is 606×1080 (even, rounded down), centred on an even offset");
        check(VideoEdit.Crop(1080, 1920, VideoAspect.Wide, 0) == new PixelRect(0, 0, 1080, 606) && VideoEdit.Crop(1080, 1920, VideoAspect.Wide, 1) == new PixelRect(0, 1314, 1080, 606),
            "A wide crop of a tall picture slides top to bottom");
        check(VideoEdit.Crop(320, 240, VideoAspect.Portrait, 0.5) == new PixelRect(64, 0, 192, 240) && VideoEdit.Crop(320, 240, VideoAspect.Classic, 0.5) == new PixelRect(0, 0, 320, 240),
            "4:5 and 4:3 crops of 320×240");
        var random = new Random(7);
        foreach (var aspect in Enum.GetValues<VideoAspect>())
            for (int i = 0; i < 400; i++)
            {
                int w = random.Next(8, 2000) * 2, h = random.Next(8, 2000) * 2; double p = random.NextDouble();
                PixelRect r;
                try { r = VideoEdit.Crop(w, h, aspect, p); } catch (UserError) { continue; }
                bool fits = r.X >= 0 && r.Y >= 0 && r.X + r.Width <= w && r.Y + r.Height <= h && r.Width % 2 == 0 && r.Height % 2 == 0 && r.X % 2 == 0 && r.Y % 2 == 0 && r.Width >= 16 && r.Height >= 16;
                bool full = r.Width == w || r.Height == h || r.Width >= w - 1 || r.Height >= h - 1;
                var (rw, rh) = aspect == VideoAspect.Original ? (w, h) : VideoEdit.Ratio(aspect);
                bool shape = r.Width == w ? Math.Abs(r.Height - r.Width * (double)rh / rw) < 2 : r.Height == h && Math.Abs(r.Width - r.Height * (double)rw / rh) < 2;
                if (!(fits && full && shape)) { check(false, $"Crop {aspect} of {w}×{h} at {p}: {r}"); return; }
            }
        check(true, "Thousands of random crops are even, inside the picture, as large as they can be and of the chosen shape");
        reject(() => VideoEdit.Crop(321, 240, VideoAspect.Square, 0.5), "Crops refuse an odd picture size");
        reject(() => VideoEdit.Crop(320, 240, VideoAspect.Square, 1.5), "Crops refuse a position past the end");
        reject(() => VideoEdit.Crop(320, 240, VideoAspect.Square, double.NaN), "Crops refuse a position that isn't a number");
        try { VideoEdit.Crop(400, 16, VideoAspect.Tall, 0.5); check(false, "A 9:16 crop of a 400×16 picture is too small"); }
        catch (UserError error) { check(error.Message == VideoEdit.TooSmall, "A 9:16 crop of a 400×16 picture is refused as too small"); }
    }

    private static void Geometry(Action<bool, string> check, Action<Action, string> reject)
    {
        var rect = new PixelRect(10, 20, 30, 40);
        check(VideoEdit.Turn(rect, 320, 240, 1) == new PixelRect(180, 10, 40, 30), "A quarter turn moves a rectangle as the picture turns clockwise");
        check(VideoEdit.Turn(VideoEdit.Turn(rect, 320, 240, 3), 240, 320, 1) == rect && VideoEdit.Turn(rect, 320, 240, 4) == rect && VideoEdit.Turn(VideoEdit.Turn(rect, 320, 240, 2), 320, 240, 2) == rect,
            "Four quarter turns bring a rectangle back");

        var plain = VideoEdit.Geometry(320, 240, new VideoPicture());
        check(plain == new VideoGeometry(320, 240, new PixelRect(0, 0, 320, 240), 0, false, new PixelRect(0, 0, 320, 240), 320, 240), "No change keeps the picture as it is");
        var turned = VideoEdit.Geometry(320, 240, new VideoPicture(QuarterTurns: 1));
        check(turned.Width == 240 && turned.Height == 320 && turned.Rotation == 1 && !turned.Mirror, "A quarter turn makes 320×240 into 240×320");
        // A square crop slid left in the seen picture, after a turn: which part of the source is it?
        var g = VideoEdit.Geometry(320, 240, new VideoPicture(QuarterTurns: 1, Aspect: VideoAspect.Wide, Position: 0));
        check(g.Width == 240 && g.Height == 134 && g.Seen == new PixelRect(0, 0, 240, 134) && g.SourceCrop == new PixelRect(0, 0, 134, 240),
            "The top of a clockwise-turned picture is the source's left side: " + g.SourceCrop);
        g = VideoEdit.Geometry(320, 240, new VideoPicture(QuarterTurns: 1, FlipVertical: true, Aspect: VideoAspect.Wide, Position: 0));
        check(g.SourceCrop == new PixelRect(186, 0, 134, 240) && g.Rotation == 3 && g.Mirror, "Flipping up–down after a turn takes the top from the source's right side");
        g = VideoEdit.Geometry(320, 240, new VideoPicture(FlipHorizontal: true, FlipVertical: true));
        check(g.Rotation == 2 && !g.Mirror, "Flipping both ways is a half turn");

        // Every combination: the output is even and inside the limits, the source crop lies inside the
        // source, and every source point lands where the turn → flip → crop picture puts it.
        int combos = 0;
        foreach (var (w, h) in new[] { (320, 240), (240, 320), (1920, 1080), (3840, 2160), (2160, 3840), (640, 640), (16, 16), (1080, 1920) })
            for (int turns = 0; turns < 4; turns++)
                foreach (bool fh in new[] { false, true })
                    foreach (bool fv in new[] { false, true })
                        foreach (var aspect in Enum.GetValues<VideoAspect>())
                            foreach (double p in new[] { 0, 0.25, 0.5, 1 })
                            {
                                var picture = new VideoPicture(turns, fh, fv, aspect, p);
                                try { g = VideoEdit.Geometry(w, h, picture); } catch (UserError) { continue; }
                                var s = g.SourceCrop;
                                bool ok = g.Width % 2 == 0 && g.Height % 2 == 0 && g.Width >= 16 && g.Height >= 16 && Math.Max(g.Width, g.Height) <= Math.Max(w, h) && Math.Min(g.Width, g.Height) <= Math.Min(w, h)
                                    && s.X >= 0 && s.Y >= 0 && s.X + s.Width <= w && s.Y + s.Height <= h && s.X % 2 == 0 && s.Y % 2 == 0
                                    && (g.Rotation % 2 == 0 ? (s.Width, s.Height) == (g.Width, g.Height) : (s.Width, s.Height) == (g.Height, g.Width))
                                    && (g.SeenWidth, g.SeenHeight) == (turns % 2 == 0 ? (w, h) : (h, w));
                                foreach (var (x, y) in new[] { (s.X + 0.5, s.Y + 0.5), (s.X + s.Width - 0.5, s.Y + 0.5), (s.X + 0.5, s.Y + s.Height - 0.5), (s.X + s.Width / 3 + 0.5, s.Y + s.Height / 5 + 0.5) })
                                {
                                    var (ox, oy) = VideoEdit.Place(g, x, y);
                                    var (ex, ey) = Reference(w, h, picture, g.Seen, x, y);
                                    ok &= Math.Abs(ox - ex) < 1e-9 && Math.Abs(oy - ey) < 1e-9 && ox > 0 && oy > 0 && ox < g.Width && oy < g.Height;
                                }
                                if (!ok) { check(false, $"Geometry {w}×{h} {picture}: {g}"); return; }
                                combos++;
                            }
        check(combos > 2000, $"Every turn, flip, shape and position maps the source exactly ({combos} combinations)");
        reject(() => VideoEdit.Geometry(320, 240, new VideoPicture(QuarterTurns: 4)), "Five quarter turns are refused");
        reject(() => VideoEdit.Geometry(320, 240, new VideoPicture(Aspect: (VideoAspect)9)), "An unknown shape is refused");
        reject(() => VideoEdit.Geometry(320, 240, new VideoPicture(Aspect: VideoAspect.Square, Position: -0.1)), "A negative position is refused");
    }

    // Turn the whole source clockwise, flip what is seen, then subtract the crop's corner.
    private static (double, double) Reference(int w, int h, VideoPicture picture, PixelRect seen, double x, double y)
    {
        double u = x, v = y; int cw = w, ch = h;
        for (int i = 0; i < picture.QuarterTurns; i++) { (u, v) = (ch - v, u); (cw, ch) = (ch, cw); }
        if (picture.FlipHorizontal) u = cw - u;
        if (picture.FlipVertical) v = ch - v;
        return (u - seen.X, v - seen.Y);
    }

    private static void Timing(Action<bool, string> check, Action<Action, string> reject)
    {
        check(VideoEdit.OutputLength(1000, 200) == 500 && VideoEdit.OutputLength(1000, 25) == 4000 && VideoEdit.OutputLength(1001, 200) == 501 && VideoEdit.OutputLength(1000, 300) == 333
            && VideoEdit.OutputLength(1000, 75) == 1333 && VideoEdit.OutputLength(7_200_000, 25) == 28_800_000 && VideoEdit.OutputLength(100, 400) == 25,
            "Speed changes the length: 2× halves it, 0.25× makes it four times as long, rounded to the millisecond");
        foreach (int speed in VideoEdit.Speeds)
            check(Math.Abs(VideoEdit.OutputLength(123_457, speed) - 123_457 * 100.0 / speed) <= 0.5, $"{speed}% speed length is exact to half a millisecond");
        reject(() => VideoEdit.OutputLength(1000, 110), "Speeds outside the offered steps are refused");
        reject(() => VideoEdit.OutputLength(1000, 0), "Zero speed is refused");

        VideoEdit.CheckRate(1920, 1080, 60, 200); VideoEdit.CheckRate(3840, 2160, 30, 200); VideoEdit.CheckRate(1920, 1080, 29.97, 400); VideoEdit.CheckRate(3840, 2160, 60, 25);
        check(true, "Speed-ups up to 3840 × 2160 at 60 frames a second are allowed");
        VideoEdit.CheckRate(1920, 1080, 60, 400); reject(() => VideoEdit.CheckRate(1920, 1080, 120, 300), "1080p120 at 3× (360 frames a second) is refused");
        reject(() => VideoEdit.CheckRate(3840, 2160, 30, 300), "4K30 at 3× is refused");
        check(VideoEdit.EncodeBitrate(20e6, 100) == 20e6 && VideoEdit.EncodeBitrate(20e6, 400) == 20e6 && VideoEdit.EncodeBitrate(60e6, 400) == 25e6 && VideoEdit.EncodeBitrate(60e6, 25) == 60e6,
            "Speed-ups ask for fewer bits only when the played rate would pass 100 Mbit/s");
        var picture = new VideoPicture(QuarterTurns: 1);
        var plan = VideoTrim.Plan(0, 10_000, 10_000, true, true, picture, 200);
        check(plan.Picture == picture && plan.Speed == 200 && !plan.KeepSound && plan.Edited && plan.OutputLength == 5_000, "At 2× the whole video can be edited; its sound is removed");
        plan = VideoTrim.Plan(0, 10_000, 10_000, true, true, new VideoPicture(Aspect: VideoAspect.Square));
        check(plan.KeepSound && plan.Edited && plan.Speed == 100, "A crop alone keeps the sound");
        plan = VideoTrim.Plan(1_000, 3_000, 10_000, true, true, new VideoPicture(Position: 0.2));
        check(plan.Picture is null && !plan.Edited && plan == new VideoTrimPlan(1_000, 3_000, true), "A picture without changes is no change: the plan is the same as a plain trim");
        reject(() => VideoTrim.Plan(0, 10_000, 10_000, true, true, new VideoPicture(Position: 0.2), 100), "Keeping everything unchanged is still refused");
        reject(() => VideoTrim.Plan(0, 5_000, 10_000, true, true, null, 90), "A trim at an unoffered speed is refused");
        reject(() => VideoTrim.Plan(0, 5_000, 10_000, true, true, new VideoPicture(QuarterTurns: -1)), "A trim with a negative turn is refused");
    }

    // A minimal MP4 movie: mvhd, one track with tkhd, an edit list, mdhd, hdlr and stts/ctts.
    private static byte[] Movie(uint movieScale, uint mediaScale, uint frames, uint delta, string handler = "vide", bool wide = false, bool ctts = true, bool edit = true, bool stts = true, bool cslg = false)
    {
        byte[] Box(string type, params byte[][] parts) { var body = parts.SelectMany(x => x).ToArray(); return U32((uint)(8 + body.Length)).Concat(type.Select(c => (byte)c)).Concat(body).ToArray(); }
        byte[] Full(int version) => new byte[] { (byte)version, 0, 0, 0 };
        ulong mediaDuration = (ulong)frames * delta, movieDuration = mediaDuration * movieScale / mediaScale;
        byte[] Dur(ulong value) => wide ? U64(value) : U32((uint)value);
        int v = wide ? 1 : 0;
        var mvhd = Box("mvhd", Full(v), Dur(1), Dur(2), U32(movieScale), Dur(movieDuration), new byte[80]);
        var tkhd = Box("tkhd", Full(v), Dur(1), Dur(2), U32(1), U32(0), Dur(movieDuration), new byte[60]);
        var elst = Box("edts", Box("elst", Full(v), U32(2), Dur(movieDuration / 4), wide ? U64(ulong.MaxValue) : U32(uint.MaxValue), U32(0x00010000), Dur(movieDuration), wide ? U64(delta) : U32(delta), U32(0x00010000)));
        var mdhd = Box("mdhd", Full(v), Dur(1), Dur(2), U32(mediaScale), Dur(mediaDuration), new byte[4]);
        var hdlr = Box("hdlr", Full(0), U32(0), handler.Select(c => (byte)c).ToArray(), new byte[13]);
        var sttsBox = Box("stts", Full(0), U32(1), U32(frames), U32(delta));
        var cttsBox = Box("ctts", Full(1), U32(2), U32(1), U32(2 * delta), U32(1), U32(unchecked((uint)-(int)delta)));
        var cslgBox = Box("cslg", Full(0), U32(delta), U32(unchecked((uint)-(int)delta)), U32(2 * delta), U32(0), U32(frames * delta));
        var stbl = Box("stbl", (stts ? sttsBox : Array.Empty<byte>()).Concat(ctts ? cttsBox : Array.Empty<byte>()).Concat(cslg ? cslgBox : Array.Empty<byte>()).ToArray(), Box("stsz", Full(0), U32(0), U32(0)));
        var mdia = Box("mdia", mdhd, hdlr, Box("minf", Box("vmhd", Full(0), new byte[8]), stbl));
        var trak = Box("trak", tkhd, edit ? elst : Array.Empty<byte>(), mdia);
        return Box("moov", mvhd, trak);
    }

    private static byte[] U32(uint value) => new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };
    private static byte[] U64(ulong value) => U32((uint)(value >> 32)).Concat(U32((uint)value)).ToArray();
    private static uint R32(byte[] b, int at) => (uint)(b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3]);
    private static ulong R64(byte[] b, int at) => (ulong)R32(b, at) << 32 | R32(b, at + 4);
    private static int Find(byte[] b, string type) { for (int i = 4; i + 4 <= b.Length; i++) if (b[i] == type[0] && b[i + 1] == type[1] && b[i + 2] == type[2] && b[i + 3] == type[3]) return i + 4; return -1; }

    // Seconds of the track (media duration ÷ timescale), of the movie, of the first sample and edit.
    private static (double Media, double Movie, double Sample, double Edit, long Offset, long EditMedia, uint Scale) Read(byte[] b, bool wide)
    {
        int mvhd = Find(b, "mvhd"), mdhd = Find(b, "mdhd"), stts = Find(b, "stts"), elst = Find(b, "elst"), ctts = Find(b, "ctts"), tkhd = Find(b, "tkhd");
        uint movieScale = R32(b, mvhd + (wide ? 20 : 12)), scale = R32(b, mdhd + (wide ? 20 : 12));
        double D(int at) => wide ? R64(b, at) : R32(b, at);
        double media = D(mdhd + (wide ? 24 : 16)) / scale, movie = D(mvhd + (wide ? 24 : 16)) / movieScale, track = D(tkhd + (wide ? 28 : 20)) / movieScale;
        if (Math.Abs(track - movie) > 1e-6) movie = double.NaN;
        double sample = R32(b, stts + 12) / (double)scale;
        double edit = D(elst + 8 + (wide ? 20 : 12)) / movieScale;
        long editMedia = wide ? (long)R64(b, elst + 8 + 20 + 8) : (int)R32(b, elst + 8 + 12 + 4);
        return (media, movie, sample, edit, (int)R32(b, ctts + 8 + 12), editMedia, scale);
    }

    private static void Retime(Action<bool, string> check, Action<Action, string> reject)
    {
        foreach (bool wide in new[] { false, true })
            foreach (var (movieScale, mediaScale, delta) in new[] { (1000u, 30000u, 1001u), (600u, 15360u, 512u), (1000u, 30u, 1u), (90000u, 90000u, 3003u), (1000u, 10_000_000u, 333_667u), (1000u, 2997u, 100u) })
                foreach (int speed in VideoEdit.Speeds)
                {
                    var movie = Movie(movieScale, mediaScale, 300, delta, wide: wide);
                    var before = Read(movie, wide);
                    var after = movie.ToArray(); Mp4Retime.Patch(after, speed);
                    var now = Read(after, wide);
                    double factor = 100.0 / speed;
                    bool ok = after.Length == movie.Length
                        && Math.Abs(now.Media - before.Media * factor) < 1e-9 && Math.Abs(now.Sample - before.Sample * factor) < 1e-12
                        && Math.Abs(now.Movie - before.Movie * factor) <= 0.5 / movieScale + 1e-9 && Math.Abs(now.Edit - before.Edit * factor) <= 0.5 / movieScale + 1e-9
                        && Math.Abs(now.Offset / (double)now.Scale - before.Offset / (double)before.Scale * factor) < 1e-12 && Math.Abs(now.EditMedia / (double)now.Scale - before.EditMedia / (double)before.Scale * factor) < 1e-12;
                    if (!ok) { check(false, $"Retime {movieScale}/{mediaScale}/{delta} {speed}% wide {wide}: {before} → {now}"); return; }
                    if (speed == 100) check(after.SequenceEqual(movie), "100% speed changes nothing");
                }
        check(true, "Retiming scales track, movie, edit, sample and reordering times exactly for every speed and common timescale, in 32- and 64-bit boxes");
        var simple = Movie(1000, 30000, 300, 1000); Mp4Retime.Patch(simple, 200);
        check(R32(simple, Find(simple, "mdhd") + 12) == 60000 && R32(simple, Find(simple, "stts") + 12) == 1000, "2× doubles a 30000 timescale and leaves frame durations alone");
        var odd = Movie(1000, 30, 300, 1); Mp4Retime.Patch(odd, 75);
        check(R32(odd, Find(odd, "mdhd") + 12) == 45 && R32(odd, Find(odd, "stts") + 12) == 2, "0.75× of a 30 timescale needs whole numbers: timescale 45, each frame 2 (was 30 and 1)");

        reject(() => Mp4Retime.Patch(Movie(1000, 44100, 300, 1024, "soun"), 200), "Retiming refuses a sound track");
        reject(() => Mp4Retime.Patch(Movie(1000, 30000, 300, 1000, stts: false), 200), "Retiming refuses a track without sample times");
        reject(() => Mp4Retime.Patch(Movie(1000, 30000, 300, 1000)[..^5], 200), "Retiming refuses a cut-off movie box");
        reject(() => Mp4Retime.Patch(Movie(1000, 4_000_000_000, 300, 1000), 200), "Retiming refuses a timescale that would overflow");
        reject(() => Mp4Retime.Patch(Movie(1000, 30, 1, 2_200_000_000), 75), "Retiming refuses frame times that would overflow");
        var corrupt = Movie(1000, 30, 300, 1); corrupt[Find(corrupt, "stts") + 7] = 2;
        reject(() => Mp4Retime.Patch(corrupt, 75), "Retiming refuses a sample-time count running past its own box, even inside the movie box");
        var shifts = Movie(1000, 30, 300, 1, cslg: true); Mp4Retime.Patch(shifts, 75);
        int cs = Find(shifts, "cslg");
        check(R32(shifts, cs + 4) == 2 && (int)R32(shifts, cs + 8) == -2 && R32(shifts, cs + 12) == 4 && R32(shifts, cs + 20) == 600, "Retiming scales the composition shift box with the sample times");
        var noEdits = Movie(1000, 30000, 300, 1000, ctts: false, edit: false); Mp4Retime.Patch(noEdits, 400);
        check(R32(noEdits, Find(noEdits, "mdhd") + 12) == 120000, "Retiming works without an edit list or reordering");
        reject(() => Mp4Retime.Patch(Movie(1000, 30000, 300, 1000), 110), "Retiming refuses an unoffered speed");

        // A whole file: only the movie box changes, wherever it is.
        byte[] File(params byte[][] boxes) => boxes.SelectMany(x => x).ToArray();
        byte[] Plain(string type, int length) { var b = new byte[length]; U32((uint)length).CopyTo(b, 0); for (int i = 0; i < 4; i++) b[4 + i] = (byte)type[i]; for (int i = 8; i < length; i++) b[i] = (byte)(i * 7); return b; }
        foreach (bool front in new[] { false, true })
        {
            var moov = Movie(1000, 30000, 300, 1000);
            var bytes = front ? File(Plain("ftyp", 24), moov, Plain("mdat", 5000)) : File(Plain("ftyp", 24), Plain("mdat", 5000), moov);
            using var stream = new MemoryStream(); stream.Write(bytes);
            Mp4Retime.Apply(stream, 50);
            var result = stream.ToArray(); var patched = moov.ToArray(); Mp4Retime.Patch(patched, 50);
            int at = front ? 24 : 5024;
            check(result.Length == bytes.Length && result.AsSpan(at, moov.Length).SequenceEqual(patched) && result.AsSpan(0, at).SequenceEqual(bytes.AsSpan(0, at)) && result.AsSpan(at + moov.Length).SequenceEqual(bytes.AsSpan(at + moov.Length)),
                $"Retiming a file changes only its movie box ({(front ? "before" : "after")} the media)");
        }
        void Refuses(string name, params byte[][] boxes)
        {
            using var stream = new MemoryStream(); stream.Write(File(boxes)); var copy = stream.ToArray();
            reject(() => Mp4Retime.Apply(stream, 200), name);
            check(stream.ToArray().SequenceEqual(copy), name + " (and leaves the file as it was)");
        }
        Refuses("Retiming refuses a file without a movie box", Plain("ftyp", 24), Plain("mdat", 100));
        Refuses("Retiming refuses two movie boxes", Plain("ftyp", 24), Movie(1000, 30000, 300, 1000), Movie(1000, 30000, 300, 1000));
        Refuses("Retiming refuses fragments", Plain("ftyp", 24), Movie(1000, 30000, 300, 1000), Plain("moof", 40), Plain("mdat", 100));
        Refuses("Retiming refuses a box running past the end", Plain("ftyp", 24), Movie(1000, 30000, 300, 1000), U32(500).Concat("mdat"u8.ToArray()).ToArray());
        Refuses("Retiming refuses a few stray bytes at the end", Plain("ftyp", 24), Movie(1000, 30000, 300, 1000), new byte[5]);
    }
}
