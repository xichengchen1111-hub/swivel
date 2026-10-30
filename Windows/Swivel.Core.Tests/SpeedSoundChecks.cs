using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using Swivel.Core;

// Trim Video… at another speed with its sound: the plan, the timing math, the pitch-keeping
// stretch to the video's length and the muxer that adds the AAC track to the retimed MP4.
internal static class SpeedSoundChecks
{
    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        // Plan: the sound is kept at every speed unless removed.
        foreach (int speed in VideoEdit.Speeds.Where(s => s != 100))
        {
            var kept = VideoTrim.Plan(0, 4_000, 10_000, true, true, null, speed);
            var removed = VideoTrim.Plan(0, 4_000, 10_000, false, true, null, speed);
            var silent = VideoTrim.Plan(0, 4_000, 10_000, true, false, null, speed);
            check(kept.KeepSound && !removed.KeepSound && !silent.KeepSound && kept.Edited, $"At {speed}% the sound is kept unless removed (or absent)");
        }

        check(VideoEdit.SoundFormat(44100, 2) == (44100, 2) && VideoEdit.SoundFormat(48000, 1) == (48000, 1) && VideoEdit.SoundFormat(32000, 6) == (48000, 2) && VideoEdit.SoundFormat(96000, 2) == (48000, 2) && VideoEdit.SoundFormat(0, 0) == (48000, 2),
            "Sound is decoded to 44.1 kHz (kept) or 48 kHz, mono or stereo, for Windows' AAC encoder");

        // Frames for a movie length, against exact rounding.
        var random = new Random(4417); int exact = 0;
        foreach (var (d, ts, rate) in new (ulong, uint, int)[] { (46494, 30659, 48000), (90999, 30000, 44100), (0, 1, 48000), (ulong.MaxValue >> 20, 1, 48000), (1, uint.MaxValue, 44100) }.Concat(Enumerable.Range(0, 20000).Select(_ => ((ulong)random.NextInt64(0, long.MaxValue >> 10), (uint)random.Next(1, int.MaxValue), random.Next(2) == 0 ? 44100 : 48000))))
        {
            BigInteger want = ((BigInteger)d * rate * 2 + ts) / (2 * (BigInteger)ts);
            if (VideoEdit.SoundFrames(d, ts, rate) == want) exact++;
        }
        check(exact == 20005, $"Sound frames for a movie length round exactly ({exact}/20005)");
        check(VideoEdit.SoundFrames(46494, 30659, 48000) == 72791, "The CI runner's 2× video (46494/30659 s) needs 72,791 frames at 48 kHz");
        reject(() => VideoEdit.SoundFrames(1, 0, 48000), "A zero timescale is refused");

        // Limits: decoded and stretched PCM16 each within two hours and 512 MB.
        VideoEdit.CheckSound(2_700_000, 100, 48000, 2); VideoEdit.CheckSound(1_200_000, 50, 48000, 2); VideoEdit.CheckSound(1_200_000, 25, 44100, 1); VideoEdit.CheckSound(5_400_000, 400, 44100, 1);
        check(true, "45 minutes at 1×, 20 minutes at 0.5× (stereo 48 kHz), 20 minutes at 0.25× and 90 minutes at 4× (mono 44.1 kHz) keep their sound");
        reject(() => VideoEdit.CheckSound(2_700_000, 25, 48000, 2), "45 minutes at 0.25× (three hours) can't keep its sound");
        reject(() => VideoEdit.CheckSound(1_800_000, 50, 48000, 2), "30 minutes at 0.5× in stereo 48 kHz (over 512 MB) can't keep its sound");
        reject(() => VideoEdit.CheckSound(3_600_000, 400, 48000, 2), "An hour of stereo 48 kHz source (over 512 MB) can't keep its sound");
        reject(() => VideoEdit.CheckSound(1_800_000, 25, 44100, 1), "30 minutes at 0.25× (two hours of mono 44.1 kHz, over 512 MB) can't keep its sound");
        try { VideoEdit.CheckSound(2_700_000, 25, 48000, 2); } catch (UserError e) { check(e.Message == VideoEdit.SoundTooLong, "The refusal says to remove the sound or keep less"); }
        check(VideoEdit.SoundMatches(95967, 48000, 2000) && VideoEdit.SoundMatches(38366, 48000, 800) && !VideoEdit.SoundMatches(48000, 48000, 2000) && !VideoEdit.SoundMatches(101000, 48000, 2000),
            "Decoded sound matches the kept part within a tenth of a second (the CI runner's cuts are within a millisecond)");

        // Stretch to the video's length, pitch kept.
        foreach (var (rate, channels, percent, hz) in new[] { (48000, 2, 50, 440.0), (48000, 2, 200, 440.0), (44100, 1, 25, 1000.0), (44100, 2, 400, 1000.0), (48000, 1, 75, 997.0), (48000, 2, 300, 220.0), (44100, 1, 125, 3000.0), (48000, 2, 150, 440.0) })
        {
            var wav = Tone(rate, channels, rate * 2, hz);
            long natural = (rate * 2L * 100 + percent / 2) / percent;
            foreach (long frames in new[] { natural, natural + rate / 50, natural - rate / 50 })
            {
                using var input = new MemoryStream(wav, false); using var output = new MemoryStream();
                VideoEdit.StretchSound(input, output, percent, frames, 30, default);
                output.Position = 0; var info = PcmWave.Read(output, default);
                double found = Peak(output, info);
                check(info.Frames == frames && info.Rate == rate && info.Channels == channels && info.Bits == 16 && Math.Abs(found / hz - 1) < 0.01,
                    $"{rate} Hz {channels} ch at {percent}% is stretched to exactly {frames} frames ({info.Frames}) with its {hz} Hz tone kept ({found:0.0} Hz)");
            }
            reject(() => VideoEdit.StretchSound(new MemoryStream(wav, false), new MemoryStream(), percent, natural + rate / 2 + rate * 200 / (30 * percent), 30, default), $"At {percent}% a length half a second from the stretched sound's is refused");
        }
        // A 24 fps cut of 0.8 s is 19.2 frames; a video of 20 frames at 25% is 0.13 s longer than the stretched cut.
        {
            var wav = Tone(48000, 2, 38400, 440); long natural = 38400 * 4; long frames = VideoEdit.SoundFrames(20 * 4, 24, 48000);
            using var output = new MemoryStream(); VideoEdit.StretchSound(new MemoryStream(wav, false), output, 25, frames, 24, default);
            output.Position = 0; check(PcmWave.Read(output, default).Frames == frames && frames - natural == 6400, "A video a whole frame longer than the cut still gets its stretched sound at 0.25×");
        }
        reject(() => VideoEdit.StretchSound(new MemoryStream(Tone(48000, 2, 4800, 440), false), new MemoryStream(), 100, 4800, 30, default), "1× is never stretched");

        // The combined path must equal explicit denoise-then-stretch bytes, while off
        // must equal the original stretch bytes (including the WAV header).
        var noisy = Tone(48000, 1, 48000, 440);
        var rng = new Random(738);
        for (int i = 0; i < 48000; i++)
        {
            double tone = i < 24000 ? 0 : Math.Sin(2 * Math.PI * 440 * i / 48000) * 12000;
            BinaryPrimitives.WriteInt16LittleEndian(noisy.AsSpan(44 + i * 2), (short)(tone + rng.Next(-1800, 1801)));
        }
        var settings = new WaveNoise(.1, .35, .85);
        foreach (int speed in new[] { 50, 100, 200, 400 })
        {
            long frames = 48000L * 100 / speed;
            using var source = new MemoryStream(noisy, false);
            using var cleaned = new MemoryStream(); using var actual = new MemoryStream();
            long kept = VideoEdit.PrepareSound(source, actual, cleaned, settings, speed, frames, 30, default);
            using var expectedClean = new MemoryStream();
            WaveDenoiser.Export(new MemoryStream(noisy, false), expectedClean, settings, default);
            using var expected = new MemoryStream();
            if (speed != 100) VideoEdit.StretchSound(expectedClean, expected, speed, frames, 30, default);
            else PcmWave.Export(expectedClean, expected, new(0, 1), default);
            check(kept == frames && actual.ToArray().SequenceEqual(expected.ToArray()), $"Video sound at {speed}% is exactly denoise first, then stretch");
            if (speed != 100)
            {
                using var off = new MemoryStream(); using var old = new MemoryStream();
                VideoEdit.PrepareSound(new MemoryStream(noisy, false), off, null, null, speed, frames, 30, default);
                VideoEdit.StretchSound(new MemoryStream(noisy, false), old, speed, frames, 30, default);
                check(off.ToArray().SequenceEqual(old.ToArray()), $"Video sound at {speed}% with cleaning off retains original bytes");
            }
        }
        reject(() => VideoEdit.PrepareSound(new MemoryStream(noisy), new MemoryStream(), new MemoryStream(), settings, 200, 24000, 30, new CancellationToken(true)), "Combined video denoise/stretch observes cancellation");
        Mux(check, reject);
    }

    // 16-bit PCM tone.
    internal static byte[] Tone(int rate, int channels, int frames, double hz)
    {
        int data = frames * channels * 2; var b = new byte[44 + data];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(b, 0); BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), 36 + data); Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(b, 8);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(16), 16); BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(20), 1); BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(22), (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(24), rate); BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(28), rate * channels * 2); BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(32), (short)(channels * 2));
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(34), 16); Encoding.ASCII.GetBytes("data").CopyTo(b, 36); BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(40), data);
        for (int i = 0; i < frames; i++) for (int c = 0; c < channels; c++) BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(44 + (i * channels + c) * 2), (short)Math.Round(Math.Sin(2 * Math.PI * hz * i / rate) * 12000));
        return b;
    }

    // Frequency of a tone from its rising zero crossings, skipping the first and last 50 ms.
    internal static double Peak(Stream wav, WaveInfo info)
    {
        wav.Position = info.DataOffset; var data = new byte[info.DataBytes]; wav.ReadExactly(data);
        long skip = info.Rate / 20, first = -1, last = -1, count = 0; short previous = 0;
        for (long i = skip; i < info.Frames - skip; i++)
        {
            short v = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan((int)(i * info.Block)));
            if (previous < 0 && v >= 0) { if (first < 0) first = i; else count++; last = i; }
            previous = v;
        }
        return count == 0 ? 0 : count * (double)info.Rate / (last - first);
    }

    // ---- Muxer ----

    private static byte[] Box(string type, params byte[][] parts) => M4aChecks.Box(type, parts);
    private static byte[] U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); return b; }
    private static byte[] U64(ulong v) { var b = new byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, v); return b; }
    private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    // A silent video like Windows' retimed output: ftyp, (uuid), mdat with three chunks, moov with one
    // video track. `moovFirst` puts the movie box before the media data; `toEnd` gives mdat size 0.
    private static byte[] Video(bool moovFirst, bool toEnd, bool wide = false, uint next = 2, string handler = "vide", byte[]? extraTrak = null, byte[]? extraTop = null, bool emptyChunks = false)
    {
        var payload = Enumerable.Range(0, 96).Select(i => (byte)(0x80 | i)).ToArray();
        byte[] Make(long mdatPayload)
        {
            uint[] offsets = { (uint)mdatPayload, (uint)mdatPayload + 32, (uint)mdatPayload + 64 };
            var chunk = emptyChunks ? Box("stco") : wide ? Box("co64", U32(0), U32(3), U64(offsets[0]), U64(offsets[1]), U64(offsets[2])) : Box("stco", U32(0), U32(3), U32(offsets[0]), U32(offsets[1]), U32(offsets[2]));
            var stbl = Box("stbl", Box("stsd", U32(0), U32(1), Box("avc1", new byte[78])), Box("stts", U32(0), U32(1), U32(3), U32(1000)), Box("stsc", U32(0), U32(1), U32(1), U32(1), U32(1)), Box("stsz", U32(0), U32(32), U32(3)), chunk);
            var minf = Box("minf", Box("vmhd", new byte[12]), Box("dinf", Box("dref", U32(0), U32(1), Box("url ", U32(1)))), stbl);
            var mdia = Box("mdia", Box("mdhd", U32(0), U32(0), U32(0), U32(61318), U32(92988), new byte[4]), Box("hdlr", U32(0), U32(0), Encoding.ASCII.GetBytes(handler), new byte[13]), minf);
            var tkhd = Box("tkhd", U32(1), U32(0), U32(0), U32(1), U32(0), U32(46494), new byte[60]);
            var traks = new List<byte[]> { Box("trak", tkhd, mdia) }; if (extraTrak is not null) traks.Add(extraTrak);
            var mvhd = Box("mvhd", U32(0), U32(0), U32(0), U32(30659), U32(46494), new byte[76], U32(next));
            var moov = Box("moov", new[] { mvhd }.Concat(traks).Append(Box("udta", Box("Xtra", new byte[4]))).ToArray());
            var ftyp = Box("ftyp", Encoding.ASCII.GetBytes("mp42"), U32(0), Encoding.ASCII.GetBytes("mp42isom"));
            var uuid = Box("uuid", new byte[31]);
            var mdat = Box("mdat", payload); if (toEnd) BinaryPrimitives.WriteUInt32BigEndian(mdat, 0);
            var tops = moovFirst ? new List<byte[]> { ftyp, uuid, moov, mdat } : new List<byte[]> { ftyp, uuid, mdat, moov };
            if (extraTop is not null) tops.Insert(2, extraTop);
            return Cat(tops.ToArray());
        }
        var probe = Make(0);
        int at = IndexOf(probe, "mdat") + 8;
        return Make(at);
    }

    private static int IndexOf(byte[] b, string type)
    {
        int at = 0;
        while (at + 8 <= b.Length) { uint size = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at)); if (Encoding.ASCII.GetString(b, at + 4, 4) == type) return at; if (size == 0) break; at += (int)size; }
        return -1;
    }

    private sealed record Parsed(List<(string Type, long At, long Size)> Top, uint NextId, ulong Duration, uint Timescale, List<(string Handler, uint Id, ulong Duration, List<ulong> Offsets, string ChunkBox)> Tracks);

    // A small independent reader of the muxed output.
    private static Parsed Parse(byte[] b)
    {
        var top = new List<(string, long, long)>(); long at = 0;
        while (at < b.Length) { long size = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan((int)at)); string t = Encoding.ASCII.GetString(b, (int)at + 4, 4); if (size < 8 || at + size > b.Length) throw new InvalidDataException("top"); top.Add((t, at, size)); at += size; }
        var moov = top.Single(x => x.Item1 == "moov");
        var children = Kids(b, (int)moov.Item2 + 8, (int)(moov.Item2 + moov.Item3));
        var mvhd = children.Single(x => x.Type == "mvhd").Start;
        var tracks = new List<(string, uint, ulong, List<ulong>, string)>();
        foreach (var trak in children.Where(x => x.Type == "trak"))
        {
            var tk = Kids(b, trak.Start, trak.End); var tkhd = tk.Single(x => x.Type == "tkhd").Start;
            var mdia = tk.Single(x => x.Type == "mdia"); var md = Kids(b, mdia.Start, mdia.End);
            string handler = Encoding.ASCII.GetString(b, md.Single(x => x.Type == "hdlr").Start + 8, 4);
            var minf = md.Single(x => x.Type == "minf"); var stbl = Kids(b, minf.Start, minf.End).Single(x => x.Type == "stbl");
            var chunk = Kids(b, stbl.Start, stbl.End).Single(x => x.Type is "stco" or "co64");
            int n = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(chunk.Start + 4)); var offsets = new List<ulong>();
            for (int i = 0; i < n; i++) offsets.Add(chunk.Type == "co64" ? BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(chunk.Start + 8 + i * 8)) : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(chunk.Start + 8 + i * 4)));
            tracks.Add((handler, BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(tkhd + 12)), BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(tkhd + 20)), offsets, chunk.Type));
        }
        return new Parsed(top, BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(mvhd + 96)), BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(mvhd + 16)), BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(mvhd + 12)), tracks);
    }

    private static List<(string Type, int Start, int End)> Kids(byte[] b, int start, int end)
    {
        var list = new List<(string, int, int)>();
        for (int at = start; at < end;) { int size = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at)); if (size < 8 || at + size > end) throw new InvalidDataException("child"); list.Add((Encoding.ASCII.GetString(b, at + 4, 4), at + 8, at + size)); at += size; }
        return list;
    }

    private static byte[] MuxBytes(byte[] video, byte[] audio)
    {
        using var v = new MemoryStream(video, false); using var a = new MemoryStream(audio, false); using var o = new MemoryStream();
        long written = Mp4AudioMux.Write(v, a, o, default);
        if (written != o.Length) throw new InvalidOperationException("length");
        return o.ToArray();
    }

    private static void Mux(Action<bool, string> check, Action<Action, string> reject)
    {
        foreach (var (rate, channels, wide) in new[] { (48000, 2, false), (44100, 1, true) })
        {
            var audio = M4aChecks.Build(new M4aChecks.Spec { Rate = rate, Channels = channels, Wide = wide });
            int audioPayload = IndexOf(audio, "mdat") + 8;
            foreach (bool moovFirst in new[] { true, false })
                foreach (bool toEnd in new[] { false, true })
                {
                    if (toEnd && moovFirst == false) continue; // a box to the end of the file must be last
                    var video = Video(moovFirst, toEnd);
                    var before = Parse(toEnd ? Fixed(video) : video);
                    var output = MuxBytes(video, audio);
                    var after = Parse(output);
                    var v = after.Tracks[0]; var a = after.Tracks.Count > 1 ? after.Tracks[1] : default;
                    bool videoData = v.Offsets.Count == 3 && v.Offsets.Select((o, i) => output.AsSpan((int)o, 32).SequenceEqual(video.AsSpan((int)before.Tracks[0].Offsets[i], 32))).All(x => x);
                    bool audioData = a.Offsets?.Count == 1 && output.AsSpan((int)a.Offsets[0], 64).SequenceEqual(audio.AsSpan(audioPayload, 64));
                    string layout = string.Join(",", after.Top.Select(t => t.Type));
                    check(after.Tracks.Count == 2 && v.Handler == "vide" && a.Handler == "soun" && v.Id == 1 && a.Id == 2 && after.NextId == 3 && videoData && audioData,
                        $"Muxing {rate} Hz {(wide ? "co64" : "stco")} audio into a video with its movie box {(moovFirst ? "first" : "last")}{(toEnd ? ", media data to the end" : "")} keeps every chunk's bytes: {layout}");
                    check(layout == "ftyp,uuid,mdat,mdat,moov" && a.ChunkBox == "stco" && a.Duration == 30659 && after.Duration == 46494 && after.Timescale == 30659 && v.Duration == 46494,
                        $"The audio track lasts its media length in the video's timescale ({a.Duration}), the movie the longer of both ({after.Duration})");
                    using var again = new MemoryStream(output, false);
                    check(Mp4AudioMux.Length(again) == (46494UL, 30659U), "The muxed movie reads back with the video's length");
                }
        }

        // An exact sound length: an edit list ends the (padded) audio there, never past its media.
        {
            var plain = M4aChecks.Build(new M4aChecks.Spec { Rate = 48000 });
            using var v = new MemoryStream(Video(false, false), false); using var a = new MemoryStream(plain, false); using var o = new MemoryStream();
            Mp4AudioMux.Write(v, a, o, default, (36000, 48000));
            var bytes = o.ToArray(); var track = Parse(bytes).Tracks[1]; int elst = Find(bytes, "elst");
            check(track.Duration == 22994 && elst > 0 && BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(elst + 12)) == 1 && BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(elst + 16)) == 22994 && BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(elst + 20)) == 0 && BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(elst + 24)) == 0x10000,
                $"0.75 s of sound gets an edit list of exactly 0.75 s from media time 0 ({track.Duration}/30659)");
            using var v2 = new MemoryStream(Video(false, false), false); using var a2 = new MemoryStream(plain, false); using var o2 = new MemoryStream();
            Mp4AudioMux.Write(v2, a2, o2, default, (96000, 48000));
            check(Parse(o2.ToArray()).Tracks[1].Duration == 30659, "A requested length past the encoded sound is held to the sound's own length");
        }
        // Edit list on the audio: its segments set the track length.
        var edited = M4aChecks.Build(new M4aChecks.Spec { TrakExtra = new[] { Box("edts", Box("elst", U32(0), U32(1), U32(500), U32(1024), U32(0x10000))) } });
        var withEdits = Parse(MuxBytes(Video(false, false), edited));
        check(withEdits.Tracks[1].Duration == (500 * 30659 + 500) / 1000, $"An audio edit list (0.5 s in a 1000 timescale) gives the track's length: {withEdits.Tracks[1].Duration}");
        // Next track ID 0 or taken: the audio takes the next after the video's.
        check(Parse(MuxBytes(Video(false, false, next: 0), M4aChecks.Build(new M4aChecks.Spec()))).Tracks[1].Id == 2, "A missing next track ID still gives the audio its own ID");
        check(Parse(MuxBytes(Video(false, false, wide: true), M4aChecks.Build(new M4aChecks.Spec()))).Tracks[0].ChunkBox == "co64", "A video with 64-bit chunk offsets keeps them");

        var good = M4aChecks.Build(new M4aChecks.Spec());
        void Refused(byte[] video, byte[] audio, string name)
        {
            string? message = null;
            try { MuxBytes(video, audio); } catch (UserError e) { message = e.Message; }
            check(message == Mp4AudioMux.Failed, name);
        }
        Refused(Video(false, false, extraTrak: Box("trak", Box("tkhd", new byte[84]))), good, "A video with a second track is refused");
        Refused(Video(false, false, handler: "soun"), good, "A 'video' whose track is sound is refused");
        Refused(Video(false, false, extraTop: Box("moof", new byte[8])), good, "A fragmented video is refused");
        Refused(Video(false, false, extraTop: Box("abcd", new byte[8])), good, "An unknown top-level box is refused");
        Refused(Video(false, false), M4aChecks.Build(new M4aChecks.Spec { Handlers = new[] { "soun", "vide" } }), "Audio that isn't a single-track M4A is refused");
        Refused(Video(false, false), M4aChecks.Build(new M4aChecks.Spec { TrakExtra = new[] { Box("tref", new byte[8]) } }), "An audio track referring to others is refused");
        Refused(Video(false, false), M4aChecks.Build(new M4aChecks.Spec { TrakExtra = new[] { Box("edts", Box("elst", U32(0), U32(1), U32(uint.MaxValue), U32(0), U32(0x10000))) } }), "An audio edit of unknown length is refused");
        Refused(Video(false, false), M4aChecks.Build(new M4aChecks.Spec { TrakExtra = new[] { Box("edts", Box("free")) } }), "An audio edit box without an edit list is refused");
        Refused(Video(false, false)[..^4], good, "A truncated video is refused");
        var badOffset = Video(false, false); { int stco = Find(badOffset, "stco"); BinaryPrimitives.WriteUInt32BigEndian(badOffset.AsSpan(stco + 16), 5); }
        Refused(badOffset, good, "A video chunk outside its media data is refused");
        long allocated = GC.GetTotalAllocatedBytes(true);
        Refused(Video(false, false, emptyChunks: true), good, "A chunk-offset box too short for its count is refused");
        check(GC.GetTotalAllocatedBytes(true) - allocated < 16 << 20, "…without reading a count from the next box (no large allocation)");

        // Box sizes shrunk or grown inside the movie box: refused or muxed, nothing else, and little memory.
        {
            var source = Video(false, false); int moov = IndexOf(source, "moov"); var sizes = new List<int>();
            for (int i = moov + 8; i + 8 <= source.Length; i++) if (source.Skip(i + 4).Take(4).All(c => c is >= (byte)'a' and <= (byte)'z' or (byte)' ' or >= (byte)'0' and <= (byte)'9') && BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(i)) is >= 8 and < 4096) sizes.Add(i);
            int fine = 0, odd = 0; long before = GC.GetTotalAllocatedBytes(true);
            foreach (int at in sizes)
                foreach (int delta in new[] { -1, -4, -8, -12, 4 })
                {
                    var v = (byte[])source.Clone(); uint size = BinaryPrimitives.ReadUInt32BigEndian(v.AsSpan(at));
                    BinaryPrimitives.WriteUInt32BigEndian(v.AsSpan(at), (uint)Math.Max(0, size + delta));
                    try { MuxBytes(v, good); fine++; } catch (UserError) { fine++; } catch (Exception) { odd++; }
                }
            check(odd == 0 && sizes.Count > 15 && GC.GetTotalAllocatedBytes(true) - before < 256 << 20, $"{sizes.Count * 5} shrunk or grown box sizes are muxed or refused with little memory ({odd} other)");
        }

        // Random corruption: either a valid result or the muxer's own refusal, nothing else.
        var random = new Random(9001); int ok = 0, refused = 0, other = 0;
        var baseVideo = Video(false, false); var baseAudio = M4aChecks.Build(new M4aChecks.Spec());
        for (int i = 0; i < 6000; i++)
        {
            var v = (byte[])baseVideo.Clone(); var a = (byte[])baseAudio.Clone(); var target = i % 2 == 0 ? v : a;
            for (int k = random.Next(1, 4); k > 0; k--) target[random.Next(target.Length)] = (byte)random.Next(256);
            try { var o = MuxBytes(v, a); Parse(o); ok++; }
            catch (UserError) { refused++; }
            catch (Exception) { other++; }
        }
        check(other == 0 && ok > 0 && refused > 0, $"6000 corrupted inputs are muxed or refused, never crash: {ok} muxed, {refused} refused, {other} other");

        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel(); bool stopped = false;
            try { Mp4AudioMux.Write(new MemoryStream(baseVideo), new MemoryStream(baseAudio), new MemoryStream(), cancel.Token); } catch (OperationCanceledException) { stopped = true; }
            check(stopped, "A cancelled mux stops");
        }
        _ = reject;
    }

    private static int Find(byte[] b, string type)
    {
        var t = Encoding.ASCII.GetBytes(type);
        for (int i = 0; i + 4 <= b.Length; i++) if (b.AsSpan(i, 4).SequenceEqual(t)) return i - 4;
        return -1;
    }

    // The same bytes with a size-0 last box given its real size, for parsing the input.
    private static byte[] Fixed(byte[] video)
    {
        var copy = (byte[])video.Clone(); int at = 0;
        while (at + 8 <= copy.Length) { uint size = BinaryPrimitives.ReadUInt32BigEndian(copy.AsSpan(at)); if (size == 0) { BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(at), (uint)(copy.Length - at)); break; } at += (int)size; }
        return copy;
    }
}
