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
                var (hz, decoded) = Decode(output, root);
                check(Math.Abs(hz / 440 - 1) < 0.02 && Math.Abs(decoded - picturePart.Seconds) <= 0.1, $"The sound of {what} is still 440 Hz (pitch kept): {hz:0.0} Hz, {decoded:0.000} s decoded");
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
        check(SHA256.HashData(File.ReadAllBytes(source)).SequenceEqual(original), "Speed changes with sound leave the original byte for byte the same");
    }

    // Each track's handler and length (media duration / media timescale).
    private static List<(string Handler, double Seconds)> Tracks(byte[] b)
    {
        var list = new List<(string, double)>();
        foreach (var moov in Kids(b, 0, b.Length).Where(k => k.Type == "moov"))
            foreach (var trak in Kids(b, moov.Start, moov.End).Where(k => k.Type == "trak"))
                foreach (var mdia in Kids(b, trak.Start, trak.End).Where(k => k.Type == "mdia"))
                {
                    var kids = Kids(b, mdia.Start, mdia.End);
                    var hd = kids.Single(k => k.Type == "hdlr").Start; var md = kids.Single(k => k.Type == "mdhd").Start;
                    bool v1 = b[md] == 1;
                    double scale = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(md + (v1 ? 20 : 12)));
                    double duration = v1 ? BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(md + 24)) : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(md + 16));
                    list.Add((Encoding.ASCII.GetString(b, hd + 8, 4), duration / scale));
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
    private static (double Hz, double Seconds) Decode(string path, string root)
    {
        string wav = Path.Combine(root, "decoded-" + Guid.NewGuid().ToString("N") + ".wav");
        var file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
        var folder = StorageFolder.GetFolderFromPathAsync(root).AsTask().GetAwaiter().GetResult();
        var target = folder.CreateFileAsync(Path.GetFileName(wav), CreationCollisionOption.FailIfExists).AsTask().GetAwaiter().GetResult();
        var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.Auto); profile.Audio = AudioEncodingProperties.CreatePcm(48000, 1, 16); profile.Video = null;
        var prepared = new MediaTranscoder { AlwaysReencode = true, HardwareAccelerationEnabled = false }.PrepareFileTranscodeAsync(file, target, profile).AsTask().GetAwaiter().GetResult();
        if (!prepared.CanTranscode) { File.Delete(wav); return (0, 0); }
        prepared.TranscodeAsync().AsTask().GetAwaiter().GetResult();
        try
        {
            using var stream = File.OpenRead(wav);
            var info = PcmWave.Read(stream, default);
            stream.Position = info.DataOffset; var data = new byte[info.DataBytes]; stream.ReadExactly(data);
            // Rising zero crossings, away from the first and last 50 ms.
            long skip = info.Rate / 20, first = -1, last = -1, count = 0; short previous = 0;
            for (long i = skip; i < info.Frames - skip; i++)
            {
                short v = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan((int)(i * info.Block)));
                if (previous < 0 && v >= 0) { if (first < 0) first = i; else count++; last = i; }
                previous = v;
            }
            return (count == 0 ? 0 : count * (double)info.Rate / (last - first), info.Frames / (double)info.Rate);
        }
        finally { File.Delete(wav); }
    }
}
