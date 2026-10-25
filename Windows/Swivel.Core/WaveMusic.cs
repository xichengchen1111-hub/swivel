using System;
using System.IO;
using System.Threading;

namespace Swivel.Core;

public readonly record struct MusicRange(long First, long Count, long FadeIn, long FadeOut);
public sealed record WaveMusic(double Start = 0, double Gain = .15, bool Loop = false, double FadeIn = 0, double FadeOut = 0, WaveDucking? Ducking = null, double LoopCrossfadeMilliseconds = 0)
{
    public const string CrossfadeInvalid = "Choose a loop crossfade from 0–1000 ms. A nonzero crossfade requires looping, at least two rounded sample frames and no more than half the music file.";
    public long CrossfadeFrames(WaveInfo music)
    {
        if(!double.IsFinite(LoopCrossfadeMilliseconds)||LoopCrossfadeMilliseconds is <0 or >1000)throw new UserError(CrossfadeInvalid);
        if(LoopCrossfadeMilliseconds==0)return 0;
        long frames=(long)Math.Round(LoopCrossfadeMilliseconds*music.Rate/1000,MidpointRounding.AwayFromZero);
        if(!Loop||frames<2||frames>music.Frames/2)throw new UserError(CrossfadeInvalid);
        return frames;
    }
    public MusicRange Range(WaveInfo source, WaveInfo music, AudioEdit edit)
    {
        var selection = edit.Range(source);
        if (source.Rate != music.Rate) throw new UserError("Use background music with the same sample rate as the main WAV. Resampling is not supported yet.");
        if (!double.IsFinite(Start) || !double.IsFinite(Gain) || !double.IsFinite(FadeIn) || !double.IsFinite(FadeOut) ||
            Start < 0 || Start >= (double)selection.Count / source.Rate || Gain is < 0 or > 1 || FadeIn < 0 || FadeOut < 0)
            throw new UserError("Choose a music start inside the selection, volume from 0–100%, and fades no longer than the music overlay.");
        long first = (long)Math.Round(Start * source.Rate, MidpointRounding.AwayFromZero);
        long count = Math.Min(selection.Count - first, Loop ? selection.Count : music.Frames);
        if (count < 1 || FadeIn > (double)count / source.Rate || FadeOut > (double)count / source.Rate)
            throw new UserError("Choose a music start inside the selection, volume from 0–100%, and fades no longer than the music overlay.");
        CrossfadeFrames(music);
        Ducking?.Validate(source,edit);
        return new(first, count, Math.Min(count, (long)Math.Round(FadeIn * source.Rate, MidpointRounding.AwayFromZero)),
            Math.Min(count, (long)Math.Round(FadeOut * source.Rate, MidpointRounding.AwayFromZero)));
    }
}

public static class WaveMixer
{
    // Streaming sum with independent source/music envelopes, no resampling or
    // time stretching. The music position is relative to the trimmed output.
    // Each input has its own held stream and fixed frame buffers. Optional
    // activity lowering adds a bounded primary analysis pass and window flags.
    public static long Export(Stream source, Stream music, Stream output, AudioEdit edit, WaveMusic settings,
        CancellationToken token, bool preview = false)
    {
        if (ReferenceEquals(source, music)) throw new ArgumentException("Independent input streams are required.");
        var info = PcmWave.Read(source, token); var track = PcmWave.Read(music, token);
        var range = edit.Range(info); var overlay = settings.Range(info, track, edit);
        var ducking=settings.Ducking;
        var duckEnvelope=ducking is null?null:new WaveDuckEnvelope(WaveActivity.Analyze(source,edit,ducking,token),ducking);
        long count = preview ? Math.Min(range.Count, (long)PcmWave.PreviewSeconds * info.Rate) : range.Count;
        int bits = preview ? 16 : info.Bits, block = info.Channels * (bits / 8);
        PcmWave.Header(output, info.Rate, info.Channels, bits, count);
        source.Position = info.DataOffset + range.First * info.Block;
        var primary = new byte[4096 * info.Block]; var backing = new byte[settings.LoopCrossfadeMilliseconds==0?4096 * track.Block:0]; var encoded = new byte[4096 * block];
        double divisor = Math.Pow(2, bits - 1), sourceDivisor = Math.Pow(2, info.Bits - 1), musicDivisor = Math.Pow(2, track.Bits - 1);
        long crossfade=settings.CrossfadeFrames(track);
        var smoothLoop=crossfade==0?null:new WaveLoopReader(music,track,crossfade,token);
        var looped=smoothLoop is null?null:new double[4096*track.Channels];
        byte[]? shortLoop = null;
        if (smoothLoop is null && settings.Loop && track.Frames <= 4096)
        {
            token.ThrowIfCancellationRequested(); shortLoop = new byte[checked((int)track.DataBytes)];
            music.Position = track.DataOffset; music.ReadExactly(shortLoop); token.ThrowIfCancellationRequested();
        }
        long clipped = 0;
        for (long at = 0; at < count;)
        {
            token.ThrowIfCancellationRequested();
            int frames = (int)Math.Min(4096, count - at); source.ReadExactly(primary.AsSpan(0, frames * info.Block));
            int begin = (int)Math.Clamp(overlay.First - at, 0, frames), end = (int)Math.Clamp(overlay.First + overlay.Count - at, 0, frames);
            // Only fill the active subrange. Loops seek to the first sample; frame
            // boundaries keep channels together, including a one-frame music file.
            if(smoothLoop is not null&&end>begin)
                smoothLoop.Read(at+begin-overlay.First,looped!.AsSpan(begin*track.Channels,(end-begin)*track.Channels),token);
            else for (int position = begin; position < end;)
            {
                token.ThrowIfCancellationRequested();
                long frame = (at + position - overlay.First) % track.Frames;
                int length = (int)Math.Min(end - position, track.Frames - frame);
                if (shortLoop is not null) shortLoop.AsSpan((int)frame * track.Block, length * track.Block).CopyTo(backing.AsSpan(position * track.Block));
                else { music.Position = track.DataOffset + frame * track.Block; music.ReadExactly(backing.AsSpan(position * track.Block, length * track.Block)); }
                position += length;
            }
            for (int frame = 0; frame < frames; frame++)
            {
                double gain = edit.Gain * PcmWave.Ramp(at + frame, range.FadeIn) * PcmWave.Ramp(range.Count - 1 - at - frame, range.FadeOut);
                long musicAt = at + frame - overlay.First;
                double musicGain = frame >= begin && frame < end ? settings.Gain * PcmWave.Ramp(musicAt, overlay.FadeIn) * PcmWave.Ramp(overlay.Count - 1 - musicAt, overlay.FadeOut) : 0;
                if(duckEnvelope is not null)musicGain*=duckEnvelope.Next();
                for (int channel = 0; channel < info.Channels; channel++)
                {
                    double original = PcmWave.Sample(primary.AsSpan(frame * info.Block + channel * (info.Bits / 8), info.Bits / 8), info.Bits) / sourceDivisor;
                    double added = 0;
                    if (musicGain != 0)
                    {
                        double Read(int ch) => smoothLoop is not null?looped![frame*track.Channels+ch]:PcmWave.Sample(backing.AsSpan(frame * track.Block + ch * (track.Bits / 8), track.Bits / 8), track.Bits) / musicDivisor;
                        added = track.Channels == 1 ? Read(0) : info.Channels == 1 ? (Read(0) + Read(1)) / 2 : Read(channel);
                    }
                    double value = Math.Round((original * gain + added * musicGain) * divisor, MidpointRounding.AwayFromZero);
                    if (value < -divisor || value > divisor - 1) clipped++;
                    PcmWave.WriteSample(encoded.AsSpan(frame * block + channel * (bits / 8), bits / 8), bits, (long)Math.Clamp(value, -divisor, divisor - 1));
                }
            }
            output.Write(encoded.AsSpan(0, frames * block)); at += frames;
        }
        if ((count * block & 1) != 0) output.WriteByte(0);
        token.ThrowIfCancellationRequested(); return clipped;
    }
}
