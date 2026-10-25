using System;

namespace Swivel.Core;

// Playback-rate change, including pitch. No time-stretch or voice classification.
public sealed record WaveSpeed(int Percent = 100)
{
    public long Frames(WaveInfo source)
    {
        if (Percent is < 50 or > 200)
            throw new UserError("Choose a whole-number speed from 50–200%.");
        long frames = checked((source.Frames * 100 + Percent / 2) / Percent);
        if (frames < 1 || frames > (long)source.Rate * PcmWave.MaxSeconds)
            throw new UserError("The changed-speed WAV must contain at least one frame and be no longer than two hours.");
        long bytes = checked(frames * source.Block);
        if (bytes + (bytes & 1) + (source.Bits > 16 ? 68 : 44) > PcmWave.MaxBytes)
            throw new UserError("The changed-speed WAV would exceed 512 MB. Choose a faster speed.");
        return frames;
    }
}
