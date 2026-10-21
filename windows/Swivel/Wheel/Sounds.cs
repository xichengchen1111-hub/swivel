using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Swivel;

enum SoundEffect { Hover, Page, Press, Done, SwitchOn, SwitchOff }

/// Short interface sounds, synthesised once in memory (no bundled audio) and played through a few always-ready
/// waveOut streams, so a tick starts within a frame and overlapping sounds mix.
///
/// The Mac app plays them as system sounds (the alert device and volume). Windows has no low-latency equivalent for
/// app-made sounds: they go to the default output device under Swivel's own volume slider in the Volume Mixer.
static partial class Sounds
{
    static readonly Dictionary<SoundEffect, IntPtr[]> buffers = [];
    static readonly Dictionary<SoundEffect, int[]> lengths = [];
    static double lastHover = double.NegativeInfinity;
    static int variant;
    static bool preparing;
    /// A sound asked for before the sounds were ready (the first one after launch or after turning sounds on).
    static (SoundEffect Effect, double At)? pending;
    static readonly Random random = new();

    static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public static void Play(SoundEffect effect)
    {
        if (!Prefs.SoundEffects) return;
        if (effect == SoundEffect.Hover)
        {
            double now = Now;
            if (now - lastHover <= 0.045) return;
            lastHover = now;
        }
        if (!buffers.TryGetValue(effect, out var choices) || choices.Length == 0)
        {
            pending = (effect, Now);
            Prepare();
            return;
        }
        // Repeated ticks vary a little in pitch and level, so a sweep doesn't sound mechanical.
        variant = (variant + 1 + random.Next(2)) % choices.Length;
        int index = effect == SoundEffect.Hover ? variant : 0;
        WaveOut.Play(choices[index], lengths[effect][index]);
    }

    /// For diagnostics: how many sound buffers are ready.
    public static int RegisteredCount => buffers.Values.Sum(b => b.Length);

    /// Whether the output stream could be opened (for `--feedback check`).
    public static bool DeviceReady => WaveOut.Open();

    /// Builds the sounds off the UI thread (once) and opens the output streams, so the first tick is on time.
    public static void Prepare()
    {
        if (!Prefs.SoundEffects) return;
        if (buffers.Count > 0)
        {
            WaveOut.Open();
            return;
        }
        if (preparing) return;
        preparing = true;
        var dispatcher = Dispatcher.CurrentDispatcher;
        Task.Run(() =>
        {
            var made = new Dictionary<SoundEffect, (IntPtr[], int[])>();
            foreach (var effect in Enum.GetValues<SoundEffect>())
            {
                var variants = SoundSynth.Variants(effect);
                var ptrs = new IntPtr[variants.Length];
                var lens = new int[variants.Length];
                for (int n = 0; n < variants.Length; n++)
                {
                    var pcm = SoundSynth.Pcm16(SoundSynth.Samples(effect, variants[n].Pitch, variants[n].Gain));
                    // Lives for the whole run: waveOut reads it while playing, and the sounds are tiny.
                    ptrs[n] = Marshal.AllocHGlobal(pcm.Length);
                    Marshal.Copy(pcm, 0, ptrs[n], pcm.Length);
                    lens[n] = pcm.Length;
                }
                made[effect] = (ptrs, lens);
            }
            dispatcher.BeginInvoke(() =>
            {
                foreach (var (effect, (ptrs, lens)) in made)
                {
                    buffers[effect] = ptrs;
                    lengths[effect] = lens;
                }
                preparing = false;
                WaveOut.Open();
                // Play what was asked for meanwhile, unless it's now too late to belong to its moment.
                if (pending is { } p && Now - p.At < 0.4) Play(p.Effect);
                pending = null;
            });
        });
    }

    /// A few waveOut streams on the default device, opened on demand and closed again after a while of silence (an
    /// open stream keeps the audio engine awake).
    static partial class WaveOut
    {
        const int Streams = 4;
        const uint WAVE_MAPPER = 0xFFFFFFFF;
        const int WHDR_DONE = 0x1, WHDR_PREPARED = 0x2;
        static readonly int HeaderSize = Marshal.SizeOf<WAVEHDR>();

        static readonly IntPtr[] handles = new IntPtr[Streams];
        static readonly IntPtr[] headers = new IntPtr[Streams];
        static readonly double[] started = new double[Streams];
        static DispatcherTimer? idle;
        static double lastPlay;

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        struct WAVEFORMATEX
        {
            public ushort wFormatTag, nChannels;
            public uint nSamplesPerSec, nAvgBytesPerSec;
            public ushort nBlockAlign, wBitsPerSample, cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WAVEHDR
        {
            public IntPtr lpData;
            public uint dwBufferLength, dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags, dwLoops;
            public IntPtr lpNext, reserved;
        }

        [LibraryImport("winmm.dll")]
        private static partial int waveOutOpen(out IntPtr handle, uint deviceId, ref WAVEFORMATEX format, IntPtr callback, IntPtr instance, uint flags);

        [LibraryImport("winmm.dll")]
        private static partial int waveOutClose(IntPtr handle);

        [LibraryImport("winmm.dll")]
        private static partial int waveOutReset(IntPtr handle);

        [LibraryImport("winmm.dll")]
        private static partial int waveOutPrepareHeader(IntPtr handle, IntPtr header, int size);

        [LibraryImport("winmm.dll")]
        private static partial int waveOutUnprepareHeader(IntPtr handle, IntPtr header, int size);

        [LibraryImport("winmm.dll")]
        private static partial int waveOutWrite(IntPtr handle, IntPtr header, int size);

        /// Opens the streams if they aren't. False when there is no output device.
        public static bool Open()
        {
            if (handles[0] != IntPtr.Zero) { KeepAlive(); return true; }
            var format = new WAVEFORMATEX
            {
                wFormatTag = 1, nChannels = 1, nSamplesPerSec = (uint)SoundSynth.Rate,
                nAvgBytesPerSec = (uint)SoundSynth.Rate * 2, nBlockAlign = 2, wBitsPerSample = 16,
            };
            for (int i = 0; i < Streams; i++)
            {
                if (waveOutOpen(out handles[i], WAVE_MAPPER, ref format, IntPtr.Zero, IntPtr.Zero, 0) != 0)
                {
                    handles[i] = IntPtr.Zero;
                    Close();
                    return false;
                }
                if (headers[i] == IntPtr.Zero)
                {
                    headers[i] = Marshal.AllocHGlobal(HeaderSize);
                    Marshal.StructureToPtr(new WAVEHDR(), headers[i], false);
                }
            }
            KeepAlive();
            return true;
        }

        public static void Play(IntPtr data, int length)
        {
            if (!Open()) return;
            lastPlay = Now;
            // A free stream, or else the one that started longest ago (its sound is nearly over).
            int pick = -1;
            for (int i = 0; i < Streams && pick < 0; i++)
                if ((Flags(i) & WHDR_PREPARED) == 0 || (Flags(i) & WHDR_DONE) != 0) pick = i;
            if (pick < 0)
            {
                pick = Array.IndexOf(started, started.Min());
                waveOutReset(handles[pick]);
            }
            var handle = handles[pick];
            var header = headers[pick];
            if ((Flags(pick) & WHDR_PREPARED) != 0) waveOutUnprepareHeader(handle, header, HeaderSize);
            Marshal.StructureToPtr(new WAVEHDR { lpData = data, dwBufferLength = (uint)length }, header, false);
            started[pick] = lastPlay;
            if (waveOutPrepareHeader(handle, header, HeaderSize) != 0 || waveOutWrite(handle, header, HeaderSize) != 0)
            {
                // The device went away (unplugged, default device changed): reopen on the next sound.
                Close();
            }
        }

        static int Flags(int i) => headers[i] == IntPtr.Zero ? 0 : Marshal.ReadInt32(headers[i], 24);

        static void KeepAlive()
        {
            lastPlay = Math.Max(lastPlay, Now);
            if (idle != null || Dispatcher.FromThread(Thread.CurrentThread) is null) return;
            idle = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(10) };
            idle.Tick += (_, _) =>
            {
                if (Now - lastPlay > 30) Close();
            };
            idle.Start();
        }

        static void Close()
        {
            for (int i = 0; i < Streams; i++)
            {
                if (handles[i] == IntPtr.Zero) continue;
                waveOutReset(handles[i]);
                if ((Flags(i) & WHDR_PREPARED) != 0) waveOutUnprepareHeader(handles[i], headers[i], HeaderSize);
                waveOutClose(handles[i]);
                handles[i] = IntPtr.Zero;
            }
            idle?.Stop();
            idle = null;
        }
    }
}

/// Builds the interface sounds as 48 kHz 16-bit mono PCM. Levels follow Apple's own UI sounds: a hover tick peaks
/// near −24 dBFS (it repeats), a press near −13 dBFS. The same synthesis as the Mac app's SoundSynth.
static class SoundSynth
{
    public const double Rate = 48_000.0;

    public static (double Pitch, double Gain)[] Variants(SoundEffect effect) => effect == SoundEffect.Hover
        ? [(1, 1), (0.97, 0.88), (1.03, 0.94)]
        : [(1, 1)];

    public static float[] Samples(SoundEffect effect, double pitch, double gain)
    {
        var (length, peak) = effect switch
        {
            SoundEffect.Hover => (0.025, 0.063),  // −24 dBFS
            SoundEffect.Page => (0.07, 0.09),     // −21 dBFS
            SoundEffect.Press => (0.11, 0.22),    // −13 dBFS
            SoundEffect.Done => (0.26, 0.14),     // −17 dBFS
            _ => (0.06, 0.16),                    // switches, −16 dBFS
        };
        int count = (int)(Rate * length);
        var noise = new Random();
        double Hiss() => noise.NextDouble() * 2 - 1;
        double Ping(double t, double freq, double decay) => t < 0 ? 0 : Math.Exp(-t / decay) * Math.Sin(2 * Math.PI * freq * pitch * t);
        var output = new double[count];
        for (int i = 0; i < count; i++)
        {
            double t = i / Rate;
            switch (effect)
            {
                case SoundEffect.Hover:
                    // A small plastic detent: two bright partials and a breath of noise, gone in ~10 ms.
                    output[i] = 0.6 * Ping(t, 2350, 0.0038) + 0.3 * Ping(t, 3900, 0.0022) + 0.15 * Hiss() * Math.Exp(-t / 0.0007);
                    break;
                case SoundEffect.Page:
                    // A ratchet: two detents, the second a little higher.
                    output[i] = 0.6 * Ping(t, 2000, 0.003) + 0.25 * Ping(t, 3300, 0.0018)
                        + 0.65 * Ping(t - 0.034, 2500, 0.003) + 0.25 * Ping(t - 0.034, 4100, 0.0018);
                    break;
                case SoundEffect.Press:
                {
                    // A keycap bottoming out: a sharp click over a short, falling low body.
                    double f = 210 - 80 * Math.Min(t / 0.05, 1);
                    output[i] = 0.75 * Math.Exp(-t / 0.022) * Math.Sin(2 * Math.PI * f * t) + 0.3 * Ping(t, 1150, 0.006)
                        + 0.3 * Hiss() * Math.Exp(-t / 0.001);
                    break;
                }
                case SoundEffect.Done:
                {
                    // A soft two-note chime (C6 then G6) with a quick attack.
                    double attack = Math.Min(t / 0.004, 1);
                    output[i] = attack * (0.55 * (Ping(t, 1046.5, 0.07) + 0.25 * Ping(t, 2093, 0.03))
                        + 0.6 * (Ping(t - 0.075, 1568, 0.09) + 0.2 * Ping(t - 0.075, 3136, 0.035)));
                    break;
                }
                default:
                {
                    // A rocker light switch: the lever's soft first contact, then the snap 9 ms later, a sharp crack of
                    // noise over two plastic resonances and a short low thump. "On" is a little higher and crisper.
                    double up = effect == SoundEffect.SwitchOn ? 1.12 : 0.88;
                    double snap = t - 0.009;
                    double crack = snap < 0 ? 0 : Hiss() * Math.Exp(-snap / 0.0006);
                    output[i] = 0.18 * Ping(t, 2900 * up, 0.0015)
                        + 0.55 * crack + 0.5 * Ping(snap, 1750 * up, 0.004) + 0.3 * Ping(snap, 3400 * up, 0.0025)
                        + 0.45 * Ping(snap, 150 * up, 0.012);
                    break;
                }
            }
            // Fade the last few milliseconds so nothing clicks at the cut.
            output[i] *= Math.Min((count - i) / (Rate * 0.004), 1);
        }
        double loudest = output.Max(Math.Abs);
        return output.Select(v => (float)(v / Math.Max(loudest, 1e-9) * peak * gain)).ToArray();
    }

    /// Little-endian 16-bit PCM.
    public static byte[] Pcm16(float[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short s = (short)(Math.Clamp(samples[i], -1f, 1f) * 32_767);
            bytes[2 * i] = (byte)s;
            bytes[2 * i + 1] = (byte)(s >> 8);
        }
        return bytes;
    }

    /// A minimal mono 16-bit PCM WAV file (for diagnostics and tests; playback uses the raw PCM).
    public static byte[] Wav(float[] samples)
    {
        var pcm = Pcm16(samples);
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write("RIFF"u8); w.Write(36 + pcm.Length);
        w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write((int)Rate); w.Write((int)Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(pcm.Length);
        w.Write(pcm);
        w.Flush();
        return stream.ToArray();
    }
}
