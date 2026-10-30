using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Swivel.Core;
using Swivel.Windows;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

internal static class SpeedSoundNativeChecks
{
    // The 3-second red/green/blue test video with a 440 Hz tone, at other speeds with its sound:
    // the saved MP4 must have an audio track as long as the video (within a frame), and the sound
    // Windows decodes from it must still be 440 Hz (pitch kept) and as long as the new video.
    internal static void Run(string directory, Action<bool, string> check)
    {
        string root = Path.Combine(directory, "speed-sound"); Directory.CreateDirectory(root);
        string source = Path.Combine(root, "tone.mp4");
        VideoTrimNativeChecks.MakeColourVideo(root, source, 320, 240);
        byte[] original = SHA256.HashData(File.ReadAllBytes(source));
        using (var video = VideoTrimmer.OpenAsync(source, default).GetAwaiter().GetResult())
        {
            check(video.HasSound, "The tone test video has sound");
            foreach (var (speed, start, end, picture, colours) in new (int, long, long, VideoPicture?, string)[]
            {
                (50, 0, 3000, null, "RB"), (200, 0, 3000, null, "RB"), (50, 1100, 1900, null, "GG"), (200, 500, 2500, new VideoPicture(QuarterTurns: 1), "RB"), (400, 0, 3000, null, "RB"), (25, 1100, 1900, null, "GG"),
            })
            {
                var plan = VideoTrim.Plan(start, end, video.Duration, true, video.HasSound, picture, speed);
                double progress = -1;
                string output = video.TrimAsync(plan, done => progress = Math.Max(progress, done), default).GetAwaiter().GetResult();
                var tracks = Tracks(File.ReadAllBytes(output));
                var picturePart = tracks.SingleOrDefault(t => t.Handler == "vide"); var soundPart = tracks.SingleOrDefault(t => t.Handler == "soun");
                double frame = 1 / Math.Max(1, video.Fps * speed / 100);
                double expected = plan.OutputLength / 1000.0;
                string what = $"{speed}% of {start}–{end} ms{(picture is null ? "" : " turned")}";
                check(tracks.Count == 2 && picturePart.Handler is not null && soundPart.Handler is not null && Math.Abs(picturePart.Seconds - expected) <= 0.1 + expected * 0.03 && Math.Abs(soundPart.Seconds - picturePart.Seconds) <= frame && progress is > 0 and <= 1.0001,
                    $"{what} has a sound track as long as the picture within a frame ({frame * 1000:0.#} ms): picture {picturePart.Seconds:0.0000} s, sound {soundPart.Seconds:0.0000} s, expected {expected:0.000} s, progress {progress:0.###}");
                var (seconds, sound) = VideoTrimNativeChecks.Probe(output);
                check(sound && Math.Abs(seconds - expected) <= 0.1 + expected * 0.03 && VideoTrimNativeChecks.Colours(output, seconds) == colours,
                    $"Windows reads {what} back with sound and the planned picture: {seconds:0.000} s, {VideoTrimNativeChecks.Colours(output, seconds)} (expected {colours})");
                var (hz, decoded, onset, _, _) = Decode(output, root);
                check(Math.Abs(hz / 440 - 1) < 0.02 && Math.Abs(decoded - picturePart.Seconds) <= 0.1, $"The sound of {what} is still 440 Hz (pitch kept): {hz:0.0} Hz, {decoded:0.000} s decoded");
                // The tone plays from the source's start, so a cut from 0 must sound at once (within 25 ms: Windows decodes the original with 11 ms of lead-in too).
                Console.WriteLine($"INFO: {what}: tone heard from {onset * 1000:0.0} ms");
                if (start == 0) check(onset <= 0.025, $"The sound of {what} starts with the picture: tone heard from {onset * 1000:0.0} ms");
            }

            // Cancelled once the sound is being prepared: nothing is saved.
            int before = Directory.GetFiles(root).Length;
            using (var cancel = new CancellationTokenSource())
            {
                bool cancelled = false; double at = -1;
                var trim = Task.Run(() => video.TrimAsync(VideoTrim.Plan(0, 3000, video.Duration, true, true, null, 50), done => { if (done > .8 && at < 0) { at = done; Task.Run(cancel.Cancel); } }, cancel.Token));
                bool ended = Task.WhenAny(trim, Task.Delay(TimeSpan.FromMinutes(2))).GetAwaiter().GetResult() == trim;
                if (!ended) { check(false, "A speed change cancelled while preparing its sound ends within two minutes"); return; }
                try { trim.GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancelled = true; }
                check(cancelled && Directory.GetFiles(root).Length == before, $"A speed change cancelled while preparing its sound (at {at:0.###}) saves nothing");
            }

            // Removing the sound at another speed still gives a silent video.
            string silent = video.TrimAsync(VideoTrim.Plan(0, 3000, video.Duration, false, true, null, 200), null, default).GetAwaiter().GetResult();
            check(Tracks(File.ReadAllBytes(silent)).Count == 1 && !VideoTrimNativeChecks.Probe(silent).Sound, "With the sound removed, 2× is saved silent");
        }
        Noise(root, check);
        check(SHA256.HashData(File.ReadAllBytes(source)).SequenceEqual(original), "Speed changes with sound leave the original byte for byte the same");
    }

    private static void Noise(string directory, Action<bool, string> check)
    {
        string root = Path.Combine(directory, "noise"); Directory.CreateDirectory(root);
        const int rate = 44100;
        byte[] noisy = VideoTrimNativeChecks.Tone(rate, 3);
        var random = new Random(77189);
        for (int i = 0; i < rate * 3; i++)
        {
            double tone = i >= rate && i < rate * 2.7 ? 8000 * Math.Sin(2 * Math.PI * 440 * i / rate) : 0;
            BinaryPrimitives.WriteInt16LittleEndian(noisy.AsSpan(44 + i * 2), (short)(tone + random.Next(-2400, 2401)));
        }
        string source = Path.Combine(root, "noisy.mp4");
        VideoTrimNativeChecks.MakeColourVideo(root, source, 320, 240, noisy);
        byte[] original = SHA256.HashData(File.ReadAllBytes(source));
        var baseline = Decode(source, root, measureNoise: true);
        using (var video = VideoTrimmer.OpenAsync(source, default).GetAwaiter().GetResult())
        {
            foreach (int speed in new[] { 100, 50, 200 })
            {
                var plan = VideoTrim.Plan(200, 2800, video.Duration, true, speed: speed, noise: new(.1, .5, 1));
                using (var sample = video.NoiseDocumentAsync(plan, default).GetAwaiter().GetResult())
                {
                    var suggestion = sample.FindQuietSample((long)(sample.Info.Rate * .2), default);
                    check(sample.Info.Bits == 16 && VideoEdit.SoundMatches(sample.Info.Frames, sample.Info.Rate, plan.Length)
                        && suggestion.Start >= 0 && suggestion.End <= sample.Info.Seconds,
                        "Video noise settings and quiet suggestion use only the decoded kept part at original speed");
                    using var preview = new MemoryStream(sample.NoisePreview(plan.Noise!, default));
                    check(PcmWave.Read(preview, default).Frames == sample.Info.Frames, "Short video cleaned preview reuses the existing full-duration denoiser preview");
                }
                string output = video.TrimAsync(plan, null, default).GetAwaiter().GetResult();
                var tracks = Tracks(File.ReadAllBytes(output));
                var picture = tracks.Single(t => t.Handler == "vide");
                var sound = tracks.Single(t => t.Handler == "soun");
                var result = Decode(output, root, speed, measureNoise: true);
                double floorDb = 20 * Math.Log10(result.Noise / baseline.Noise);
                double toneDb = 20 * Math.Log10(result.Tone / baseline.Tone);
                check(tracks.Count == 2 && Math.Abs(sound.Seconds - picture.Seconds) <= 1 / (video.Fps * speed / 100)
                    && Math.Abs(result.Seconds - plan.OutputLength / 1000.0) <= .1 + plan.OutputLength / 1000.0 * .03,
                    $"Cleaned video at {speed}% has exactly one audio track of the kept/retimed duration: {sound.Seconds:0.000}s");
                check(floorDb < -5 && Math.Abs(toneDb) < 2,
                    $"Cleaned AAC video at {speed}% lowers noise by >5dB and preserves tone within 2dB: noise {floorDb:0.00}dB, tone {toneDb:0.00}dB");
                check(VideoTrimNativeChecks.Colours(output, picture.Seconds) == "RB", "Video noise reduction retains the planned picture");
            }
            var removed = VideoTrim.Plan(200, 2800, video.Duration, false, speed: 200, noise: new(.1, .5));
            string silent = video.TrimAsync(removed, null, default).GetAwaiter().GetResult();
            check(removed.Noise is null && Tracks(File.ReadAllBytes(silent)).Count == 1, "Removing sound disables cleaning and emits no audio track");
            int before = Directory.GetFiles(root).Length;
            using var cancel = new CancellationTokenSource();
            bool cancelled = false;
            try
            {
                var task = Task.Run(() => video.TrimAsync(VideoTrim.Plan(200, 2800, video.Duration, true, noise: new(.1, .5)),
                    done => { if (done > .8) _ = Task.Run(cancel.Cancel); }, cancel.Token));
                if (Task.WhenAny(task, Task.Delay(TimeSpan.FromMinutes(2))).GetAwaiter().GetResult() != task)
                    throw new TimeoutException("Video noise cancellation did not finish within two minutes.");
                task.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { cancelled = true; }
            check(cancelled && Directory.GetFiles(root).Length == before, "Cancelling video cleaning while preparing sound publishes nothing");
        }
        check(SHA256.HashData(File.ReadAllBytes(source)).SequenceEqual(original), "Video cleaning leaves the noisy original unchanged");
    }

    // Each track's handler and played length: its edit list's segments in the movie timescale, or
    // else its media duration in the media timescale.
    private static List<(string Handler, double Seconds)> Tracks(byte[] b)
    {
        var list = new List<(string, double)>();
        foreach (var moov in Kids(b, 0, b.Length).Where(k => k.Type == "moov"))
        {
            int mvhd = Kids(b, moov.Start, moov.End).Single(k => k.Type == "mvhd").Start;
            double movieScale = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(mvhd + (b[mvhd] == 1 ? 20 : 12)));
            foreach (var trak in Kids(b, moov.Start, moov.End).Where(k => k.Type == "trak"))
            {
                double? edits = null;
                foreach (var edts in Kids(b, trak.Start, trak.End).Where(k => k.Type == "edts"))
                    foreach (var elst in Kids(b, edts.Start, edts.End).Where(k => k.Type == "elst"))
                    {
                        bool wide = b[elst.Start] == 1; int n = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(elst.Start + 4)); double sum = 0;
                        for (int i = 0; i < n; i++) { int e = elst.Start + 8 + i * (wide ? 20 : 12); bool empty = wide ? BinaryPrimitives.ReadInt64BigEndian(b.AsSpan(e + 8)) == -1 : BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(e + 4)) == -1; if (!empty) sum += wide ? BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(e)) : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(e)); }
                        edits = sum / movieScale;
                    }
                foreach (var mdia in Kids(b, trak.Start, trak.End).Where(k => k.Type == "mdia"))
                {
                    var kids = Kids(b, mdia.Start, mdia.End);
                    var hd = kids.Single(k => k.Type == "hdlr").Start; var md = kids.Single(k => k.Type == "mdhd").Start;
                    bool v1 = b[md] == 1;
                    double scale = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(md + (v1 ? 20 : 12)));
                    double duration = v1 ? BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(md + 24)) : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(md + 16));
                    list.Add((Encoding.ASCII.GetString(b, hd + 8, 4), edits ?? duration / scale));
                }
            }
        }
        return list;
    }

    private static List<(string Type, int Start, int End)> Kids(byte[] b, int start, int end)
    {
        var list = new List<(string, int, int)>();
        for (int at = start; at + 8 <= end;)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at)); if (size == 0) size = end - at;
            if (size < 8 || at + size > end) break;
            list.Add((Encoding.ASCII.GetString(b, at + 4, 4), at + 8, at + (int)size)); at += (int)size;
        }
        return list;
    }

    // Windows decodes the saved video's sound to mono 48 kHz PCM16; its tone frequency and length.
    private static (double Hz, double Seconds, double Onset, double Noise, double Tone) Decode(string path, string root, int speed = 100, bool measureNoise = false)
    {
        string wav = Path.Combine(root, "decoded-" + Guid.NewGuid().ToString("N") + ".wav");
        var file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
        var folder = StorageFolder.GetFolderFromPathAsync(root).AsTask().GetAwaiter().GetResult();
        var target = folder.CreateFileAsync(Path.GetFileName(wav), CreationCollisionOption.FailIfExists).AsTask().GetAwaiter().GetResult();
        var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.Auto); profile.Audio = AudioEncodingProperties.CreatePcm(48000, 1, 16); profile.Video = null;
        var prepared = new MediaTranscoder { AlwaysReencode = true, HardwareAccelerationEnabled = false }.PrepareFileTranscodeAsync(file, target, profile).AsTask().GetAwaiter().GetResult();
        if (!prepared.CanTranscode) { File.Delete(wav); return (0, 0, double.MaxValue, 0, 0); }
        prepared.TranscodeAsync().AsTask().GetAwaiter().GetResult();
        try
        {
            using var stream = File.OpenRead(wav);
            var info = PcmWave.Read(stream, default);
            stream.Position = info.DataOffset; var data = new byte[info.DataBytes]; stream.ReadExactly(data);
            // Rising zero crossings, away from the first and last 50 ms.
            long skip = info.Rate / 20, first = -1, last = -1, count = 0, loud = -1; short previous = 0;
            for (long i = 0; i < info.Frames && loud < 0; i++) if (Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan((int)(i * info.Block)))) > 2000) loud = i;
            for (long i = skip; i < info.Frames - skip; i++)
            {
                short v = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan((int)(i * info.Block)));
                if (previous < 0 && v >= 0) { if (first < 0) first = i; else count++; last = i; }
                previous = v;
            }
            double noiseEnergy = 0, sine = 0, cosine = 0;
            int firstNoise = (int)(info.Rate * .15 * 100 / speed), endNoise = (int)(info.Rate * .45 * 100 / speed);
            int firstTone = (int)(info.Rate * 1.2 * 100 / speed), endTone = (int)(info.Rate * 1.8 * 100 / speed);
            if (!measureNoise) return (count == 0 ? 0 : count * (double)info.Rate / (last - first), info.Seconds, loud < 0 ? double.MaxValue : loud / (double)info.Rate, 0, 0);
            if (endTone > info.Frames) throw new InvalidDataException("Noise fixture decoded shorter than its measurement window.");
            for (int i = firstNoise; i < endNoise; i++)
            {
                double v = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(i * info.Block)); noiseEnergy += v * v;
            }
            for (int i = firstTone; i < endTone; i++)
            {
                double v = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(i * info.Block));
                sine += v * Math.Sin(2 * Math.PI * 440 * i / info.Rate); cosine += v * Math.Cos(2 * Math.PI * 440 * i / info.Rate);
            }
            return (count == 0 ? 0 : count * (double)info.Rate / (last - first), info.Frames / (double)info.Rate, loud < 0 ? double.MaxValue : loud / (double)info.Rate, Math.Sqrt(noiseEnergy / (endNoise - firstNoise)), 2 * Math.Sqrt(sine * sine + cosine * cosine) / (endTone - firstTone));
        }
        finally { File.Delete(wav); }
    }
}
