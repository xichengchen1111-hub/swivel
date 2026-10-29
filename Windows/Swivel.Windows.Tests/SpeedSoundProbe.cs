using System;
using System.IO;
using System.Text;
using Swivel.Core;
using Swivel.Windows;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

// TEMPORARY CI probe (removed before finishing).
internal static class SpeedSoundProbe
{
    internal static void Run(string directory)
    {
        string root = Path.Combine(directory, "probe"); Directory.CreateDirectory(root);
        string source = Path.Combine(root, "colours.mp4");
        VideoTrimNativeChecks.MakeColourVideo(root, source, 320, 240);
        Console.WriteLine("PROBE source " + Tree(File.ReadAllBytes(source)));
        var file = StorageFile.GetFileFromPathAsync(source).AsTask().GetAwaiter().GetResult();
        var folder = StorageFolder.GetFolderFromPathAsync(root).AsTask().GetAwaiter().GetResult();
        var clip0 = MediaClip.CreateFromFileAsync(file).AsTask().GetAwaiter().GetResult();
        var ap = clip0.EmbeddedAudioTracks[0].GetAudioEncodingProperties();
        Console.WriteLine($"PROBE audio props {ap.Subtype} {ap.SampleRate} {ap.ChannelCount} {ap.BitsPerSample}");
        // 1. transcoder trim to wav
        foreach (var (name, start, stop) in new[] { ("t1", 500, 2500), ("t2", 0, 3000), ("t3", 1100, 1900) })
            try
            {
                var wav = MediaEncodingProfile.CreateWav(AudioEncodingQuality.Auto); wav.Audio = AudioEncodingProperties.CreatePcm(48000, 2, 16); wav.Video = null;
                var target = folder.CreateFileAsync(name + ".wav", CreationCollisionOption.ReplaceExisting).AsTask().GetAwaiter().GetResult();
                var tr = new MediaTranscoder { AlwaysReencode = true, HardwareAccelerationEnabled = false, TrimStartTime = TimeSpan.FromMilliseconds(start), TrimStopTime = TimeSpan.FromMilliseconds(stop) };
                var p = tr.PrepareFileTranscodeAsync(file, target, wav).AsTask().GetAwaiter().GetResult();
                Console.WriteLine($"PROBE {name} can {p.CanTranscode} {p.FailureReason}");
                if (p.CanTranscode) { p.TranscodeAsync().AsTask().GetAwaiter().GetResult(); Wav(Path.Combine(root, name + ".wav"), name); }
            }
            catch (Exception e) { Console.WriteLine($"PROBE {name} threw {e.GetType().Name} {e.Message}"); }
        // 2. composition to wav
        try
        {
            var clip = MediaClip.CreateFromFileAsync(file).AsTask().GetAwaiter().GetResult();
            clip.TrimTimeFromStart = TimeSpan.FromMilliseconds(500); clip.TrimTimeFromEnd = clip.OriginalDuration - TimeSpan.FromMilliseconds(2500);
            var c = new MediaComposition(); c.Clips.Add(clip);
            var wav = MediaEncodingProfile.CreateWav(AudioEncodingQuality.Auto); wav.Audio = AudioEncodingProperties.CreatePcm(48000, 2, 16); wav.Video = null;
            var target = folder.CreateFileAsync("c1.wav", CreationCollisionOption.ReplaceExisting).AsTask().GetAwaiter().GetResult();
            var r = c.RenderToFileAsync(target, MediaTrimmingPreference.Precise, wav).AsTask().GetAwaiter().GetResult();
            Console.WriteLine("PROBE c1 " + r); if (r == TranscodeFailureReason.None) Wav(Path.Combine(root, "c1.wav"), "c1");
        }
        catch (Exception e) { Console.WriteLine($"PROBE c1 threw {e.GetType().Name} {e.Message}"); }
        // 3. wav -> m4a box tree
        foreach (var (rate, ch) in new[] { (48000, 2), (44100, 1) })
            try
            {
                string w = Path.Combine(root, $"in{rate}.wav"); File.WriteAllBytes(w, Pcm(rate, ch, rate * 2 + 123));
                var wf = StorageFile.GetFileFromPathAsync(w).AsTask().GetAwaiter().GetResult();
                var m4a = MediaEncodingProfile.CreateM4a(AudioEncodingQuality.High); m4a.Audio.SampleRate = (uint)rate; m4a.Audio.ChannelCount = (uint)ch;
                var target = folder.CreateFileAsync($"out{rate}.m4a", CreationCollisionOption.ReplaceExisting).AsTask().GetAwaiter().GetResult();
                var p = new MediaTranscoder().PrepareFileTranscodeAsync(wf, target, m4a).AsTask().GetAwaiter().GetResult();
                Console.WriteLine($"PROBE m4a{rate} can {p.CanTranscode} {p.FailureReason} bitrate {m4a.Audio.Bitrate}");
                if (p.CanTranscode) { p.TranscodeAsync().AsTask().GetAwaiter().GetResult(); var b = File.ReadAllBytes(Path.Combine(root, $"out{rate}.m4a")); Console.WriteLine($"PROBE m4a{rate} {b.Length} " + Tree(b)); try { Console.WriteLine("PROBE gate " + M4aAudio.Identify(new MemoryStream(b), default)); } catch (Exception e) { Console.WriteLine("PROBE gate threw " + e.Message); } }
            }
            catch (Exception e) { Console.WriteLine($"PROBE m4a threw {e.GetType().Name} {e.Message}"); }
        // 4. silent retimed output layout
        try
        {
            using var video = VideoTrimmer.OpenAsync(source, default).GetAwaiter().GetResult();
            string o = video.TrimAsync(VideoTrim.Plan(0, video.Duration, video.Duration, true, true, null, 200), null, default).GetAwaiter().GetResult();
            Console.WriteLine("PROBE retimed " + Tree(File.ReadAllBytes(o)));
        }
        catch (Exception e) { Console.WriteLine($"PROBE retime threw {e.GetType().Name} {e.Message}"); }
    }

    private static void Wav(string path, string name)
    {
        using var s = File.OpenRead(path);
        try { var i = PcmWave.Read(s, default); Console.WriteLine($"PROBE {name} wav {i.Rate} {i.Channels} {i.Bits} frames {i.Frames} = {i.Frames / (double)i.Rate:0.0000} s; first nonzero {First(s, i)}"); }
        catch (Exception e) { Console.WriteLine($"PROBE {name} wav read threw {e.Message}"); }
    }

    private static string First(Stream s, WaveInfo i)
    {
        s.Position = i.DataOffset; var b = new byte[i.Block]; long f = 0; double peak = 0; long firstLoud = -1;
        for (; f < i.Frames; f++) { s.ReadExactly(b); short v = BitConverter.ToInt16(b, 0); if (Math.Abs(v) > 1000 && firstLoud < 0) firstLoud = f; peak = Math.Max(peak, Math.Abs(v)); }
        return $"{firstLoud} peak {peak}";
    }

    private static byte[] Pcm(int rate, int ch, int frames)
    {
        using var m = new MemoryStream(); using var w = new BinaryWriter(m); int data = frames * 2 * ch;
        w.Write("RIFF"u8); w.Write(36 + data); w.Write("WAVE"u8); w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)ch); w.Write(rate); w.Write(rate * 2 * ch); w.Write((short)(2 * ch)); w.Write((short)16); w.Write("data"u8); w.Write(data);
        for (int i = 0; i < frames; i++) for (int c = 0; c < ch; c++) w.Write((short)(Math.Sin(2 * Math.PI * 1000 * i / rate) * 8000));
        w.Flush(); return m.ToArray();
    }

    private static readonly string[] Containers = { "moov", "trak", "mdia", "minf", "stbl", "edts", "dinf", "udta" };
    internal static string Tree(byte[] b)
    {
        var sb = new StringBuilder(); Walk(b, 0, b.Length, 0, sb); return sb.ToString();
    }
    private static uint Be(byte[] b, int at) => (uint)(b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3]);
    private static void Walk(byte[] b, int start, int end, int depth, StringBuilder sb)
    {
        int at = start;
        while (at + 8 <= end)
        {
            long size = Be(b, at); string type = Encoding.ASCII.GetString(b, at + 4, 4);
            if (size == 0) size = end - at; if (size < 8 || size > end - at) { sb.Append($" BAD@{at}"); return; }
            sb.Append($" {type}({at},{size}");
            int body = at + 8;
            if (type is "mvhd" or "mdhd") { int v = b[body]; int ts = body + (v == 1 ? 20 : 12); sb.Append($" v{v} ts {Be(b, ts)} dur {(v == 1 ? ((ulong)Be(b, ts + 4) << 32 | Be(b, ts + 8)) : Be(b, ts + 4))}"); if (type == "mvhd") sb.Append($" next {Be(b, body + (v == 1 ? 108 : 96))}"); }
            if (type == "tkhd") { int v = b[body]; sb.Append($" v{v} flags {b[body + 3]} id {Be(b, body + (v == 1 ? 20 : 12))} dur {Be(b, body + (v == 1 ? 32 : 20))}"); }
            if (type == "elst") { int v = b[body]; uint n = Be(b, body + 4); sb.Append($" v{v} n{n}"); for (int i = 0; i < n && i < 4; i++) { int e = body + 8 + i * (v == 1 ? 20 : 12); sb.Append(v == 1 ? $" [{Be(b, e + 4)} {(int)Be(b, e + 12)}]" : $" [{Be(b, e)} {(int)Be(b, e + 4)}]"); } }
            if (type is "stts" or "stsc" or "stco" or "stsz") sb.Append($" n{Be(b, body + (type == "stsz" ? 8 : 4))}" + (type == "stts" ? $" first [{Be(b, body + 8)}x{Be(b, body + 12)}]" : type == "stco" ? $" first {Be(b, body + 8)}" : type == "stsz" ? $" fixed {Be(b, body + 4)}" : ""));
            if (type == "hdlr") sb.Append(" " + Encoding.ASCII.GetString(b, body + 8, 4));
            if (type == "stsd") sb.Append(" " + Encoding.ASCII.GetString(b, body + 12, 4));
            if (Array.IndexOf(Containers, type) >= 0) { sb.Append(" {"); Walk(b, body, at + (int)size, depth + 1, sb); sb.Append(" }"); }
            else if (type == "stsd") { sb.Append(" {"); Walk(b, body + 8 + 8 + 28, at + (int)size, depth + 1, sb); sb.Append(" }"); }
            sb.Append(')');
            at += (int)size;
        }
    }
}
