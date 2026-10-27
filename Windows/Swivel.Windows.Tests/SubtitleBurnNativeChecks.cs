using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media.Imaging;
using Swivel.Core;
using Swivel.Windows;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.SpeechSynthesis;
using Windows.Storage;

internal static class SubtitleBurnNativeChecks
{
    internal static void Run(string directory, Action<bool, string> check, Action<Action, string> reject)
    {
        // Windows-only: test videos made with Windows' own media editing API, subtitles burned in,
        // and the result's frames read back.
        string root = Path.Combine(directory, "burn"); Directory.CreateDirectory(root);
        string silent = Path.Combine(root, "plain.mp4");
        MakeVideo(silent, 4, null);
        using (var video = VideoSubtitles.OpenAsync(silent, default).GetAwaiter().GetResult())
        {
            check(video.Width == 640 && video.Height == 360 && Math.Abs(video.Seconds - 4) < 0.1 && !video.HasSound, "Burn In Subtitles reads a silent video's size and length");
            reject(() => video.SoundAsync(default).GetAwaiter().GetResult(), "Burn In Subtitles refuses to transcribe a video without sound");
            var cues = new[] { new SubtitleCue(500, 1500, "Hello subtitles"), new SubtitleCue(2500, 3500, "第二行字幕") };
            var images = video.Draw(cues, 1, default);
            check(images.Count == 2 && images.All(i => File.Exists(i.Path) && i.Y + i.Height <= 360 && i.X >= 0 && i.X + i.Width <= 640 && i.Width % 2 == 0 && i.Height % 2 == 0),
                "Burn In Subtitles draws each cue inside the picture near the bottom");
            reject(() => video.Draw(new[] { new SubtitleCue(0, 1000, string.Join(" ", Enumerable.Repeat("much too long", 80))) }, 2, default), "Burn In Subtitles refuses a cue longer than three lines");
            var clock = Stopwatch.StartNew();
            string output = video.BurnAsync(images, null, default).GetAwaiter().GetResult();
            Console.WriteLine($"Burn In Subtitles 4 s 640×360: {clock.ElapsedMilliseconds} ms");
            check(output == Path.Combine(root, "plain subtitled.mp4") && new FileInfo(output).Length > 0, "Burn In Subtitles saves beside the original");
            double during = Bright(output, 1000), between = Bright(output, 2000), second = Bright(output, 3000);
            Console.WriteLine($"Burn In Subtitles bright share near the bottom: {during:0.000} during, {between:0.000} between, {second:0.000} second cue");
            check(during > 0.003 && second > 0.003 && between < 0.001, "Burned subtitles show only while their cue is on");
            string again = video.BurnAsync(images, null, default).GetAwaiter().GetResult();
            check(again == Path.Combine(root, "plain subtitled 2.mp4"), "Burn In Subtitles never overwrites an earlier result");
            using (var check2 = VideoSubtitles.OpenAsync(output, default).GetAwaiter().GetResult())
                check(check2.Width == 640 && check2.Height == 360 && Math.Abs(check2.Seconds - 4) < 0.2 && !check2.HasSound, "The subtitled video keeps the size and length");
        }
        reject(() => VideoSubtitles.OpenAsync(Path.Combine(root, "missing.mp4"), default).GetAwaiter().GetResult(), "Burn In Subtitles refuses a missing video");
        string junk = Path.Combine(root, "junk.mp4"); File.WriteAllBytes(junk, new byte[4096]);
        reject(() => VideoSubtitles.OpenAsync(junk, default).GetAwaiter().GetResult(), "Burn In Subtitles refuses a file that is not a video");

        // Synthesized English speech as the sound track: transcribed from the video and burned in.
        if (SpeechSynthesizer.AllVoices.Count == 0 || !File.Exists(SpeechModel.Encoder.Path)) { Console.WriteLine("Burn In Subtitles speech check skipped: no voice or no speech model."); return; }
        string speech = Path.Combine(root, "speech.wav");
        using (var synthesizer = new SpeechSynthesizer())
        using (var stream = synthesizer.SynthesizeTextToStreamAsync("The quick brown fox jumps over the lazy dog. Please meet me at the station at three o'clock tomorrow afternoon.").AsTask().GetAwaiter().GetResult())
        using (var file = File.Create(speech)) stream.AsStreamForRead().CopyTo(file);
        string talking = Path.Combine(root, "talk.mp4");
        MakeVideo(talking, 9, speech);
        using (var video = VideoSubtitles.OpenAsync(talking, default).GetAwaiter().GetResult())
        {
            check(video.HasSound, "Burn In Subtitles finds the video's sound track");
            Transcript transcript;
            using (var sound = video.SoundAsync(default).GetAwaiter().GetResult()) transcript = sound.Run(null, null, default);
            string all = string.Join(" ", transcript.Segments.Select(s => s.Text));
            Console.WriteLine($"Burn In Subtitles transcribed from the video: {all}");
            check(all.Contains("fox", StringComparison.OrdinalIgnoreCase), "Burn In Subtitles transcribes the video's speech");
            string output = video.BurnAsync(video.Draw(transcript.Cues, 0, default), null, default).GetAwaiter().GetResult();
            using var result = VideoSubtitles.OpenAsync(output, default).GetAwaiter().GetResult();
            check(result.HasSound && Math.Abs(result.Seconds - video.Seconds) < 0.2, "The subtitled video keeps its sound");
            double at = (transcript.Cues[0].Start + transcript.Cues[0].End) / 2.0;
            check(Bright(output, (long)at) > 0.003, "Transcribed subtitles are burned into the picture");
        }
    }

    // A dark blue video of the given length, with an optional sound track.
    internal static void MakeVideo(string path, int seconds, string? sound)
    {
        var composition = new MediaComposition();
        composition.Clips.Add(MediaClip.CreateFromColor(global::Windows.UI.Color.FromArgb(255, 20, 30, 90), TimeSpan.FromSeconds(seconds)));
        if (sound is not null)
        {
            var file = StorageFile.GetFileFromPathAsync(sound).AsTask().GetAwaiter().GetResult();
            composition.BackgroundAudioTracks.Add(BackgroundAudioTrack.CreateFromFileAsync(file).AsTask().GetAwaiter().GetResult());
        }
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Vga);
        profile.Video.Width = 640; profile.Video.Height = 360;
        if (sound is null) profile.Audio = null;
        var folder = StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path)).AsTask().GetAwaiter().GetResult();
        var target = folder.CreateFileAsync(Path.GetFileName(path), CreationCollisionOption.FailIfExists).AsTask().GetAwaiter().GetResult();
        var reason = composition.RenderToFileAsync(target, MediaTrimmingPreference.Precise, profile).AsTask().GetAwaiter().GetResult();
        if (reason != global::Windows.Media.Transcoding.TranscodeFailureReason.None) throw new InvalidOperationException("Could not make the test video: " + reason);
    }

    // The share of near-white pixels in the bottom third of the frame at a time (ms).
    private static double Bright(string path, long ms)
    {
        var file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
        var composition = new MediaComposition();
        composition.Clips.Add(MediaClip.CreateFromFileAsync(file).AsTask().GetAwaiter().GetResult());
        using var image = composition.GetThumbnailAsync(TimeSpan.FromMilliseconds(ms), 640, 360, VideoFramePrecision.NearestFrame).AsTask().GetAwaiter().GetResult();
        using var stream = image.AsStreamForRead();
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var pixels = new FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        int w = pixels.PixelWidth, h = pixels.PixelHeight, top = h * 2 / 3;
        var data = new byte[w * (h - top) * 4];
        pixels.CopyPixels(new System.Windows.Int32Rect(0, top, w, h - top), data, w * 4, 0);
        int bright = 0;
        for (int i = 0; i < data.Length; i += 4) if (data[i] > 200 && data[i + 1] > 200 && data[i + 2] > 200) bright++;
        return bright / (double)(data.Length / 4);
    }
}
