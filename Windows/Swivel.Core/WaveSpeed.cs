using System;

namespace Swivel.Core;

// Speed change. KeepPitch=false is the original playback-rate change (pitch moves with speed, 50–200%);
// KeepPitch=true time-stretches with WaveStretch and keeps pitch (25–400%). No voice classification.
public sealed record WaveSpeed(int Percent = 100, bool KeepPitch = false)
{
    public long Frames(WaveInfo source)
    {
        if (KeepPitch ? Percent is < 25 or > 400 : Percent is < 50 or > 200)
            throw new UserError(KeepPitch ? "Choose a whole-number speed from 25–400%." : "Choose a whole-number speed from 50–200%.");
        long frames = checked((source.Frames * 100 + Percent / 2) / Percent);
        if (frames < 1 || frames > (long)source.Rate * PcmWave.MaxSeconds)
            throw new UserError("The changed-speed WAV must contain at least one frame and be no longer than two hours.");
        long bytes = checked(frames * source.Block);
        if (bytes + (bytes & 1) + (source.Bits > 16 ? 68 : 44) > PcmWave.MaxBytes)
            throw new UserError("The changed-speed WAV would exceed 512 MB. Choose a faster speed.");
        return frames;
    }
}
