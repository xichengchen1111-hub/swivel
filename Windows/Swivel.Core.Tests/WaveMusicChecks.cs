using System;
using System.IO;
using System.Linq;
using System.Threading;
using Swivel.Core;

internal static class WaveMusicChecks
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        byte[] Render(byte[] source, byte[] music, AudioEdit edit, WaveMusic mix, bool preview = false)
        {
            using var a = new MemoryStream(source); using var b = new MemoryStream(music); using var output = new MemoryStream();
            WaveMixer.Export(a, b, output, edit, mix, default, preview); return output.ToArray();
        }
        foreach (int bits in new[] { 8, 16, 24, 32 }) foreach (int channels in new[] { 1, 2 })
        foreach (int musicBits in new[] { 8, 16, 24, 32 }) foreach (int musicChannels in new[] { 1, 2 })
        {
            double scale = Math.Pow(2, bits - 1), musicScale = Math.Pow(2, musicBits - 1);
            int[] samples = Enumerable.Range(0, 40 * channels).Select(i => (int)(scale * (i % 4 - 2) / 4)).ToArray();
            int[] track = Enumerable.Range(0, 3 * musicChannels).Select(i => (int)(musicScale * (i % 3 - 1) / 4)).ToArray();
            byte[] source = WaveChecks.Fixture(bits, channels, samples), music = WaveChecks.Fixture(musicBits, musicChannels, track);
            byte[] rendered = Render(source, music, new(3d / 8000, 37d / 8000, .5), new(2d / 8000, .25, true));
            int[] actual = WaveChecks.Decode(rendered); using var decoded = new MemoryStream(rendered); var info = PcmWave.Read(decoded, default);
            check(info.Frames == 34 && info.Bits == bits && info.Channels == channels && info.Rate == 8000, "Music mix retains main format/duration across PCM precisions and channels");
            int[] expected = Enumerable.Range(0, 34 * channels).Select(i => {
                int frame = i / channels, channel = i % channels; double backing = 0;
                if (frame >= 2)
                {
                    int at = (frame - 2) % 3;
                    backing = musicChannels == 1 ? track[at] : channels == 1 ? (track[at * 2] + (double)track[at * 2 + 1]) / 2 : track[at * 2 + channel];
                }
                return (int)Math.Round(samples[(frame + 3) * channels + channel] * .5 + backing / musicScale * scale * .25, MidpointRounding.AwayFromZero);
            }).ToArray();
            check(actual.SequenceEqual(expected), "Independent sample sum checks trim-relative offset, loop phase, stereo alignment and downmix");
            byte[] muted = Render(source, music, new(0, 40d / 8000), new(Gain: 0, Loop: true));
            check(WaveChecks.Decode(muted).SequenceEqual(samples), "Muted music preserves exact original samples including PCM8/PCM32");
        }
        byte[] main = WaveChecks.Fixture(16, 1, Enumerable.Repeat(1000, 9000).ToArray());
        byte[] shortMusic = WaveChecks.Fixture(16, 1, new[] { 2000, 4000, -2000 });
        int[] once = WaveChecks.Decode(Render(main, shortMusic, new(0, 9000d / 8000), new(4095d / 8000, 1)));
        check(once[4094] == 1000 && once[4095] == 3000 && once[4096] == 5000 && once[4097] == -1000 && once[4098] == 1000, "Non-loop overlay starts/ends correctly across streaming block boundary");
        int[] looped = WaveChecks.Decode(Render(main, shortMusic, new(0, 9000d / 8000), new(4095d / 8000, 1, true)));
        check(looped[4098] == 3000 && looped[8192] == -1000 && looped.Length == 9000, "Loop phase survives successive blocks and final partial block");
        foreach (int length in new[] { 4096, 4097 })
        {
            int[] musicFrames = Enumerable.Range(0, length).Select(i => i % 1000 - 500).ToArray();
            int[] across = WaveChecks.Decode(Render(main, WaveChecks.Fixture(16, 1, musicFrames), new(0, 9000d / 8000), new(3d / 8000, 1, true)));
            check(across.SequenceEqual(Enumerable.Range(0, 9000).Select(i => 1000 + (i < 3 ? 0 : musicFrames[(i - 3) % length]))), "Cached/non-cached loop threshold and file-read wrap across blocks: " + length);
        }
        byte[] silence = WaveChecks.Fixture(16, 1, new int[800]); byte[] level = WaveChecks.Fixture(16, 1, Enumerable.Repeat(16000, 800).ToArray());
        int[] faded = WaveChecks.Decode(Render(silence, level, new(0, .1), new(0, 1, false, .025, .025)));
        check(faded[0] == 0 && faded[199] == 16000 && faded[600] == 16000 && faded[^1] == 0 && faded[400] == 16000, "Music envelope exact endpoints/plateau");
        int[] overlap = WaveChecks.Decode(Render(silence, level, new(0, .1), new(0, 1, false, .1, .1)));
        check(overlap[0] == 0 && overlap[^1] == 0 && overlap.Max() is >= 3990 and <= 4001, "Overlapping music fades multiply without amplification");
        byte[] loud = WaveChecks.Fixture(16, 1, new[] { 32767, -32768 });
        using (var a = new MemoryStream(loud)) using (var b = new MemoryStream(loud)) using (var output = new MemoryStream())
        {
            check(WaveMixer.Export(a, b, output, new(0, 2d / 8000), new(Gain: 1), default) == 2 && WaveChecks.Decode(output.ToArray()).SequenceEqual(new[] { 32767, -32768 }), "Mixed clipping saturates and counts samples without integer wrap");
        }
        byte[] longMain = WaveChecks.Fixture(16, 2, new int[12 * 8000 * 2]);
        byte[] longTrack = WaveChecks.Fixture(16, 1, Enumerable.Repeat(10000, 12 * 8000).ToArray());
        byte[] full = Render(longMain, longTrack, new(0, 12), new(0, .5, false, .5, 2));
        byte[] preview = Render(longMain, longTrack, new(0, 12), new(0, .5, false, .5, 2), true);
        check(WaveChecks.Decode(preview).SequenceEqual(WaveChecks.Decode(full).Take(10 * 8000 * 2)) && WaveChecks.Decode(preview)[^1] == 5000, "Preview equals saved prefix and does not move music fade-out earlier");
        check(WaveChecks.Decode(Render(longMain, longTrack, new(0, 12), new(Start: 11), true)).All(x => x == 0), "Music after preview horizon does not appear early");
        using var sourceInfo = new MemoryStream(main); using var trackInfo = new MemoryStream(shortMusic);
        var primary = PcmWave.Read(sourceInfo, default); var trackFormat = PcmWave.Read(trackInfo, default); var edit = new AudioEdit(0, primary.Seconds);
        foreach (var invalid in new[] { new WaveMusic(Start: -1), new(Start: primary.Seconds), new(Start: double.NaN), new(Gain: -1), new(Gain: 1.01), new(Gain: double.PositiveInfinity), new(FadeIn: double.NaN), new(FadeOut: -1), new(FadeIn: .01), new(FadeOut: .01) })
            reject(() => invalid.Range(primary, trackFormat, edit), "Invalid music times/volumes/fades refused");
        reject(() => new WaveMusic().Range(primary, trackFormat with { Rate = 44100 }, edit), "No implicit resampling for different rates");
        reject(() => Render(main, shortMusic, new(0, 1), new(Start: 1 - .1 / 8000)), "Start rounded to empty music range refused");
        using var cancelled = new CancellationTokenSource(); using var outCancel = new CancelOutput(cancelled);
        reject(() => WaveMixer.Export(sourceInfo, trackInfo, outCancel, edit, new(Loop: true), cancelled.Token), "Mix cancellation during streaming output observed");
        check(sourceInfo.CanRead && trackInfo.CanRead && sourceInfo.ToArray().SequenceEqual(main) && trackInfo.ToArray().SequenceEqual(shortMusic), "Mix does not close or modify either input");
        using var oneFrame = new ReadCountStream(WaveChecks.Fixture(16, 1, new[] { 1000 })); using var empty = new MemoryStream();
        WaveMixer.Export(sourceInfo, oneFrame, empty, edit, new(Loop: true), default);
        check(oneFrame.Reads < 20, "One-frame loops use bounded cached music instead of per-sample file IO");
        using var same = new MemoryStream(main); using var sink = new MemoryStream();
        reject(() => WaveMixer.Export(same, same, sink, edit, new(), default), "Shared input stream refused");
        check(Catalog.Available(new Prefs(), new[] { "voice.wav" }).Any(x => x.Id == "audio.music") && !Catalog.Available(new Prefs(), new[] { "voice.mp3" }).Any(x => x.Id == "audio.music"), "Music action explicitly gated to PCM WAV input");
        string? fixtures = Environment.GetEnvironmentVariable("SWIVEL_MUSIC_FIXTURES");
        if (fixtures is not null)
        {
            Directory.CreateDirectory(fixtures); File.WriteAllBytes(Path.Combine(fixtures, "loop-mix.wav"), Render(main, shortMusic, new(0, 9000d / 8000), new(4095d / 8000, 1, true)));
            File.WriteAllBytes(Path.Combine(fixtures, "music-fade.wav"), Render(silence, level, new(0, .1), new(0, 1, false, .025, .025)));
            File.WriteAllBytes(Path.Combine(fixtures, "music-preview.wav"), preview); File.WriteAllBytes(Path.Combine(fixtures, "music-full.wav"), full);
            byte[] primary24 = WaveChecks.Fixture(24, 2, Enumerable.Range(0, 16).Select(i => i % 2 == 0 ? 1000000 : -1000000).ToArray());
            byte[] track16 = WaveChecks.Fixture(16, 1, new[] { 4000, -4000 });
            File.WriteAllBytes(Path.Combine(fixtures, "stereo-24-mix.wav"), Render(primary24, track16, new(0, 8d / 8000), new(Gain: .25, Loop: true)));
        }
    }
    private sealed class CancelOutput(CancellationTokenSource cancellation) : MemoryStream
    {
        public override void Write(ReadOnlySpan<byte> buffer) { base.Write(buffer); if (Length > 100) cancellation.Cancel(); }
    }
    private sealed class ReadCountStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int Reads { get; private set; }
        public override int Read(Span<byte> buffer) { Reads++; return base.Read(buffer); }
    }
}
