using System;
using System.IO;
using System.Linq;
using System.Threading;
using Swivel.Core;

internal static class VideoColorSoundChecks
{
    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        check(new VideoColorSound().IsNeutral && !new VideoColorSound().FrameChanges, "Default video effects use the old path");
        check(new VideoColorSound(Color: new(Look: PhotoLook.Warm, LookStrength: 0)).IsNeutral, "Zero-strength look is neutral");
        foreach (var look in Enum.GetValues<PhotoLook>().Where(x => x != PhotoLook.Original))
            check(new VideoColorSound(Color: new(Look: look)).FrameChanges, "Look changes frames: " + look);
        check(!new VideoColorSound(Volume: 2).FrameChanges && !new VideoColorSound(Music: new("track.wav")).FrameChanges, "Volume and music never choose frame encoding");
        check(VideoTrim.Plan(0, 1000, 1000, true, effects: new(Volume: 2)).Edited, "Volume alone permits whole-video export");
        reject(() => VideoTrim.Plan(0, 1000, 1000, true, effects: new()), "Neutral settings refuse unchanged export");
        check(VideoTrim.Plan(0, 500, 1000, true, effects: new()) == VideoTrim.Plan(0, 500, 1000, true), "Neutral effects canonicalize to the identical trim-only plan");
        foreach (double bad in new[] { -1, 5.01, double.NaN, double.PositiveInfinity })
            reject(() => new VideoColorSound(FadeIn: bad).Validate(), "Invalid fade refused");
        reject(() => new VideoColorSound(Volume: 2.01).Validate(), "Over-200% original volume refused");
        check(VideoColorSound.Envelope(0, 2, 1, 1) == 0 && VideoColorSound.Envelope(1, 2, 1, 1) == 1 && VideoColorSound.Envelope(2, 2, 1, 1) == 0, "Shared fade endpoints and middle");
        check(Math.Abs(VideoColorSound.Envelope(.25, 1, 1, 1) - .25) < 1e-12, "Overlapping fades take the lower ramp");
        check(VideoColorSound.Envelope(.5, 1, 0, 0) == 1 && VideoColorSound.Envelope(.5, 1, 5, 5) == .1, "Zero and longer-than-video fades are defined");
        check(VideoSound.MusicFrames(7, 3, true) == 7 && VideoSound.MusicFrames(7, 3, false) == 3 && VideoSound.MusicFrames(3, 7, false) == 3, "Music loops or truncates to output length");
        using var original = Tone(8000, 8, 30000); using var output = new MemoryStream();
        VideoSound.Export(original, null, output, 8, new(Volume: 2), null, default);
        check(Samples(output).All(x => x == short.MaxValue), "200% volume clips positive samples without wrapping");
        using var negative = Tone(8000, 8, -30000); using var down = new MemoryStream();
        VideoSound.Export(negative, null, down, 8, new(Volume: 2), null, default);
        check(Samples(down).All(x => x == short.MinValue), "200% volume clips negative samples without wrapping");
        using var primary = Tone(8000, 8000, 10000); using var faded = new MemoryStream();
        var settings = new VideoColorSound(FadeIn: .25, FadeOut: .25);
        VideoSound.Export(primary, null, faded, 8000, settings, null, default);
        var samples = Samples(faded);
        check(samples[0] == 0 && samples[^1] == 0 && samples[4000] == 10000, "Sound fades start/end silent and retain the middle");
        var pixels = Enumerable.Repeat((byte)100, 16 * 16 * 4).ToArray(); for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var image = new PixelImage(16, 16, pixels); var expected = PhotoAdjust.Apply(image, new(Look: PhotoLook.Warm), default);
        check(new VideoColorSound(Color: new(Look: PhotoLook.Warm)).Apply(image, 1, 2, default).Bgra.SequenceEqual(expected.Bgra), "Video uses exactly PhotoAdjust pixels");
        check(settings.Apply(image, 0, 1, default).Bgra.Where((_, i) => i % 4 != 3).All(x => x == 0), "First picture frame is black during fade");
        using var music = Tone(8000, 3, 8000); using var silent = Tone(8000, 8000, 0); using var loop = new MemoryStream();
        VideoSound.Export(silent, music, loop, 8000, new(Music: new("music", Gain: 1, Loop: true)), null, default);
        var loopSamples = Samples(loop);
        check(loopSamples.Length == 8000 && loopSamples[0] == 8000 && loopSamples[1000] == 8000 && loopSamples[^1] == 0, "Short music loops and fades at output end");
        using var duckSource = Tone(8000, 16000, 0); using var duckMusic = Tone(8000, 4097, 8000); using var lowered = new MemoryStream();
        VideoSound.Export(duckSource, duckMusic, lowered, 16000, new(Music: new("track", Gain: 1)),
            new WaveDucking(Gain: .25, Speech: new[] { new TranscriptSegment(.2, .6, "speech") }), default);
        var speechSamples = Samples(lowered);
        check(speechSamples[3200] == 2000 && speechSamples[8000] == 8000, "Video music reuses speech attack/release, including the uncached loop boundary");
        using var truncated = new MemoryStream();
        VideoSound.Export(silent, music, truncated, 8000, new(Music: new("track", Gain: 1, Loop: false)), null, default);
        check(Samples(truncated).Skip(3).All(x => x == 0), "Nonlooped music has an exact silent tail");
        using var padded = new MemoryStream();
        VideoSound.Export(original, null, padded, 16, new(), null, default);
        check(Samples(padded).Take(8).All(x => x == 30000) && Samples(padded).Skip(8).All(x => x == 0), "Short decoded sound is padded to the exact video length");
        reject(() => VideoSound.Export(primary, null, new MemoryStream(), 8000, new(Music: new("missing")), null, default), "Requested music is never silently dropped");
        reject(() => new VideoColorSound(Music: new("track", Gain: double.NaN)).Validate(), "Nonfinite music volume refused");
        check(new VideoColorSound(Color: new(Look: PhotoLook.Warm, Warmth: -.35)).IsNeutral, "Adjustments cancelling a look are neutral");
        var marked = new byte[16 * 32 * 4];
        for (int y = 0; y < 32; y++) for (int x = 0; x < 16; x++) { int i = (y * 16 + x) * 4; marked[i] = (byte)x; marked[i + 1] = (byte)y; marked[i + 3] = 255; }
        var stored = VideoColorSound.StoreCounterclockwise(new(16, 32, marked), default);
        check(stored.Width == 32 && stored.Height == 16 && Enumerable.Range(0, 16 * 32).All(i =>
            stored.Bgra[((15 - i % 16) * 32 + i / 16) * 4] == i % 16 && stored.Bgra[((15 - i % 16) * 32 + i / 16) * 4 + 1] == i / 16),
            "Tall color frames rotate counterclockwise exactly before the upright display flag");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        reject(() => VideoSound.Export(primary, null, new MemoryStream(), 8000, settings, null, cancelled.Token), "Sound export honours pre-cancellation");
    }
    private static MemoryStream Tone(int rate, int frames, short sample)
    {
        using var data = new MemoryStream(); using var writer = new BinaryWriter(data);
        writer.Write("RIFF"u8); writer.Write(36 + frames * 2); writer.Write("WAVEfmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(frames * 2);
        for (int i = 0; i < frames; i++) writer.Write(sample);
        return new MemoryStream(data.ToArray(), false);
    }
    private static short[] Samples(MemoryStream wave)
    {
        var info = PcmWave.Read(wave, default); wave.Position = info.DataOffset; using var reader = new BinaryReader(wave, System.Text.Encoding.UTF8, true);
        return Enumerable.Range(0, (int)info.Frames).Select(_ => reader.ReadInt16()).ToArray();
    }
}
