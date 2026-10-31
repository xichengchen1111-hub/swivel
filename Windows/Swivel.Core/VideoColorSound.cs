using System;
using System.IO;
using System.Threading;

namespace Swivel.Core;

public sealed record VideoMusic(string Path, double Gain = .4, bool Loop = true, bool LowerDuringSpeech = false);

/// Fades use output seconds, after the cut and speed change. Neutral choices retain the old encoder path.
public sealed record VideoColorSound(PhotoSettings? Color = null, double FadeIn = 0, double FadeOut = 0, double Volume = 1, VideoMusic? Music = null)
{
    public const string Invalid = "Choose picture/sound fades from 0–5 seconds, original volume from 0–200%, and music volume from 0–100%.";
    public bool FrameChanges => Color is not null && Color.Effective() != new PhotoSettings() || FadeIn != 0 || FadeOut != 0;
    public bool IsNeutral => !FrameChanges && Volume == 1 && Music is null;
    public void Validate()
    {
        Color?.Validate();
        if (!double.IsFinite(FadeIn) || FadeIn is < 0 or > 5 || !double.IsFinite(FadeOut) || FadeOut is < 0 or > 5 ||
            !double.IsFinite(Volume) || Volume is < 0 or > 2 || Music is { } m && (string.IsNullOrWhiteSpace(m.Path) || !double.IsFinite(m.Gain) || m.Gain is < 0 or > 1))
            throw new UserError(Invalid);
    }
    public static double Envelope(double time, double length, double fadeIn, double fadeOut)
    {
        double level = 1;
        if (fadeIn > 0) level = Math.Min(level, Math.Clamp(time / fadeIn, 0, 1));
        if (fadeOut > 0) level = Math.Min(level, Math.Clamp((length - time) / fadeOut, 0, 1));
        return level;
    }
    /// Tall frames are stored sideways, then an MP4 display matrix turns them upright. This
    /// retains 2160 × 3840 output without asking the system encoder for more than 2304 rows.
    public static PixelImage StoreCounterclockwise(PixelImage frame, CancellationToken token)
    {
        frame.Validate(); var output = new byte[frame.Bgra.Length];
        for (int y = 0; y < frame.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < frame.Width; x++)
                frame.Bgra.AsSpan((y * frame.Width + x) * 4, 4).CopyTo(output.AsSpan(((frame.Width - 1 - x) * frame.Height + y) * 4, 4));
        }
        return new(frame.Height, frame.Width, output);
    }

    public PixelImage Apply(PixelImage frame, double time, double length, CancellationToken token)
    {
        var result = PhotoAdjust.Apply(frame, Color ?? new(), token);
        double level = Envelope(time, length, FadeIn, FadeOut);
        if (level < 1)
            for (int y = 0; y < result.Height; y++)
            {
                token.ThrowIfCancellationRequested();
                for (int i = y * result.Width * 4; i < (y + 1) * result.Width * 4; i++)
                    if (i % 4 != 3) result.Bgra[i] = (byte)Math.Round(result.Bgra[i] * level);
            }
        return result;
    }
}

/// Streaming PCM16 mix, using the speech detector's existing attack/release envelope. Both the
/// picture and the final sound use VideoColorSound.Envelope; only music has an automatic tail.
public static class VideoSound
{
    public static long MusicFrames(long output, long music, bool loop) => loop ? output : Math.Min(output, music);
    public static short Limit(double sample) => (short)Math.Clamp(Math.Round(sample, MidpointRounding.AwayFromZero), short.MinValue, short.MaxValue);

    public static void Silence(Stream output, int rate, int channels, long frames, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (rate is < 8000 or > 192000 || channels is not 1 and not 2 || frames < 1 || frames > (long)rate * PcmWave.MaxSeconds || frames * channels * 2 + 44 > PcmWave.MaxBytes) throw new UserError(VideoEdit.SoundTooLong);
        PcmWave.Header(output, rate, channels, 16, frames);
        var zeros = new byte[65536]; long bytes = frames * channels * 2;
        while (bytes > 0) { token.ThrowIfCancellationRequested(); int count = (int)Math.Min(bytes, zeros.Length); output.Write(zeros, 0, count); bytes -= count; }
    }

    public static void Export(Stream source, Stream? music, Stream output, long frames, VideoColorSound settings, WaveDucking? ducking, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); settings.Validate();
        if (ReferenceEquals(source, output) || ReferenceEquals(music, output) || ReferenceEquals(source, music)) throw new ArgumentException("Independent streams are required.");
        if ((music is null) != (settings.Music is null)) throw new UserError(VideoEdit.SoundFailed);
        var info = PcmWave.Read(source, token); var track = music is null ? null : PcmWave.Read(music, token);
        if (info.Bits != 16 || frames < 1 || frames > (long)info.Rate * PcmWave.MaxSeconds || frames * info.Block + 44 > PcmWave.MaxBytes ||
            track is not null && (track.Rate != info.Rate || track.Bits != 16 || settings.Music is null)) throw new UserError(VideoEdit.SoundTooLong);
        var duck = ducking is null ? null : new WaveDuckEnvelope(WaveActivity.Analyze(source, new(0, info.Seconds), ducking, token), ducking);
        long musicCount = track is null ? 0 : MusicFrames(frames, track.Frames, settings.Music!.Loop);
        byte[]? shortLoop = null;
        if (track is not null && settings.Music!.Loop && track.Frames <= 4096)
        {
            shortLoop = new byte[(int)track.DataBytes]; music!.Position = track.DataOffset; music.ReadExactly(shortLoop);
        }
        PcmWave.Header(output, info.Rate, info.Channels, 16, frames);
        source.Position = info.DataOffset;
        var primary = new byte[4096 * info.Block]; var backing = new byte[4096 * (track?.Block ?? 2)]; var encoded = new byte[4096 * info.Block];
        double length = (frames - 1) / (double)info.Rate;
        for (long at = 0; at < frames;)
        {
            token.ThrowIfCancellationRequested();
            int count = (int)Math.Min(4096, frames - at), read = (int)Math.Clamp(info.Frames - at, 0, count);
            Array.Clear(primary); source.ReadExactly(primary.AsSpan(0, read * info.Block));
            int added = (int)Math.Clamp(musicCount - at, 0, count);
            for (int pos = 0; pos < added;)
            {
                token.ThrowIfCancellationRequested();
                long index = (at + pos) % track!.Frames;
                int n = (int)Math.Min(added - pos, track.Frames - index);
                if (shortLoop is not null) shortLoop.AsSpan((int)index * track.Block, n * track.Block).CopyTo(backing.AsSpan(pos * track.Block));
                else { music!.Position = track.DataOffset + index * track.Block; music.ReadExactly(backing.AsSpan(pos * track.Block, n * track.Block)); }
                pos += n;
            }
            for (int f = 0; f < count; f++)
            {
                long position = at + f;
                double envelope = VideoColorSound.Envelope(position / (double)info.Rate, length, settings.FadeIn, settings.FadeOut);
                double lower = duck is not null && position < info.Frames ? duck.Next() : 1;
                double musicGain = f < added ? settings.Music!.Gain * lower * VideoColorSound.Envelope(position / (double)info.Rate,
                    (musicCount - 1) / (double)info.Rate, 0, Math.Min(.8, musicCount / (double)info.Rate)) : 0;
                for (int c = 0; c < info.Channels; c++)
                {
                    double original = PcmWave.Sample(primary.AsSpan(f * info.Block + c * 2, 2), 16);
                    double extra = 0;
                    if (musicGain != 0)
                    {
                        double Sample(int channel) => PcmWave.Sample(backing.AsSpan(f * track!.Block + channel * 2, 2), 16);
                        extra = track!.Channels == 1 ? Sample(0) : info.Channels == 1 ? (Sample(0) + Sample(1)) / 2 : Sample(c);
                    }
                    PcmWave.WriteSample(encoded.AsSpan(f * info.Block + c * 2, 2), 16, Limit((original * settings.Volume + extra * musicGain) * envelope));
                }
            }
            output.Write(encoded.AsSpan(0, count * info.Block)); at += count;
        }
        token.ThrowIfCancellationRequested();
    }
}
