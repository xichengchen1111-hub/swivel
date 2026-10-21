using System.Windows.Media.Imaging;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using FileAttributes = System.IO.FileAttributes;

namespace Swivel;

// Port of Sources/Swivel/Media.swift (and Video.merge from TrimEditor.swift). AVAssetExportSession / afconvert become
// Media Foundation's MediaTranscoder with a MediaEncodingProfile; frames come from MediaFrames (Source Reader).
// What Media Foundation can't write is left out or built by Swivel itself:
//  • MOV: Media Foundation has no QuickTime writer, so "MOV" isn't offered and Mute writes MP4 (the Mac writes MOV).
//  • AIFF and CAF: transcoded to 16-bit PCM, then Swivel writes the container (AudioContainers).
//  • AAC (.aac): encoded into M4A, then remuxed to ADTS.
//  • MP3 is a Windows extra (Media Foundation ships an MP3 encoder).

sealed record AudioFormat(string Name, string Ext);

static class Video
{
    public static List<FileOption> ConvertItems(ISet<string> sourceExt)
    {
        var list = new List<FileOption>();
        if (!sourceExt.Contains("mp4")) list.Add(new("MP4", p => Export(p, VideoPreset.Highest, "mp4")));
        // MOV is not offered: Media Foundation can't write QuickTime files.
        if (Transcode.HevcAvailable != false) list.Add(new("HEVC", p => Export(p, VideoPreset.Highest with { Hevc = true }, "mp4", " hevc")));
        list.AddRange(
        [
            new("M4V", p => Export(p, VideoPreset.Highest, "m4v")),
            new("GIF", Gif),
            new("M4A", p => Audio.ExtractAac(p)),
            new("WAV", p => Audio.Run(p, Audio.Formats.First(f => f.Name == "WAV"), "", fromVideo: true)),
            new("PNG Frame", Frame),
        ]);
        return list;
    }

    public static List<FileOption> ToolItems =>
    [
        new("Compress", p => Export(p, VideoPreset.Medium, "mp4", " compressed")),
        new("1080p", p => Export(p, VideoPreset.Box(1920, 1080), "mp4", " 1080p")),
        new("720p", p => Export(p, VideoPreset.Box(1280, 720), "mp4", " 720p")),
        new("480p", p => Export(p, VideoPreset.Box(640, 480), "mp4", " 480p")),
        // The Mac writes " muted.mov"; Media Foundation writes MP4 only.
        new("Mute", p => Export(p, VideoPreset.Highest with { Audio = false }, "mp4", " muted")),
        new("Extract Audio", p => Audio.ExtractAac(p)),
        new("Frame", Frame),
        new("GIF", Gif),
    ];

    /// How a video is re-encoded: an optional bounding box (landscape; turned for portrait video), bits per pixel
    /// for the bitrate, and whether the sound is kept.
    internal sealed record VideoPreset(int BoxLong, int BoxShort, double BitsPerPixel, bool Audio = true, bool Hevc = false)
    {
        // AVAssetExportPresetHighestQuality: source size, generous bitrate.
        public static readonly VideoPreset Highest = new(0, 0, 0.14);
        // AVAssetExportPresetMediumQuality: Apple doesn't publish its numbers; this fits 960 × 540 at a modest bitrate.
        public static readonly VideoPreset Medium = new(960, 540, 0.07);
        public static VideoPreset Box(int longEdge, int shortEdge) => new(longEdge, shortEdge, 0.1);
    }

    static async Task<List<string>> Export(string path, VideoPreset preset, string ext, string suffix = "")
    {
        var output = Paths.Output(path, ext, suffix);
        if (preset.Hevc && !await Transcode.HevcAvailableAsync())
            throw new ToolError(L("This PC has no HEVC video encoder. Install HEVC Video Extensions from the Microsoft Store."));
        return [await Transcode.Run(path, output, source => Transcode.VideoProfile(source, preset, Path.GetFileName(path)))];
    }

    /// Joins the files end to end into "Merged.mp4" (or "Merged.m4a" for audio) next to the first one.
    public static async Task<string> Merge(IReadOnlyList<string> paths, bool audioOnly)
    {
        if (paths.Count == 0) throw new ToolError(L("Can't merge these files"));
        if (audioOnly) return await Audio.Merge(paths);
        var dir = Path.GetDirectoryName(Path.GetFullPath(paths[0])) ?? ".";
        var output = Paths.Unique(Path.Combine(dir, "Merged.mp4"));

        var composition = new MediaComposition();
        MediaEncodingProfile? first = null;
        bool anyAudio = false;
        foreach (var path in paths)
        {
            var file = await Transcode.Open(path);
            var source = await Transcode.SourceProfile(file, Path.GetFileName(path));
            if (source.Video is null) throw new ToolError(L("No video track"));
            first ??= source;
            anyAudio |= source.Audio != null;
            try { composition.Clips.Add(await MediaClip.CreateFromFileAsync(file)); }
            catch (Exception e) when (e is not ToolError) { throw new ToolError(L("Couldn't read %@", Path.GetFileName(path))); }
        }
        var profile = Transcode.VideoProfile(first!, VideoPreset.Highest with { Audio = anyAudio }, Path.GetFileName(paths[0]));
        return await Transcode.WithTemp(output, async temp =>
        {
            var result = await composition.RenderToFileAsync(temp, MediaTrimmingPreference.Precise, profile);
            if (result != TranscodeFailureReason.None) throw new ToolError(L("Can't merge these files"));
        });
    }

    /// A frame near the start (10% in, like the Mac app's "Frame"), upright, long edge at most maxPixels.
    public static Task<BitmapSource> Thumbnail(string path, int maxPixels) => Task.Run(() =>
    {
        using var frames = new MediaFrames(path);
        return frames.FrameAt(frames.Duration * 0.1, maxPixels).ToBitmap();
    });

    static Task<List<string>> Frame(string path) => Task.Run(() =>
    {
        Raster frame;
        using (var frames = new MediaFrames(path)) frame = frames.FrameAt(frames.Duration * 0.1);
        var output = Paths.Output(path, "png", " frame");
        Images.Write(frame.ToBitmap(), output, "png", 1);
        return new List<string> { output };
    });

    /// 12 fps, at most the first 30 s, long edge 540, looping forever.
    static Task<List<string>> Gif(string path) => Task.Run(() =>
    {
        const int fps = 12;
        var output = Paths.Output(path, "gif");
        using var frames = new MediaFrames(path);
        double seconds = Math.Min(frames.Duration.TotalSeconds, 30);
        int count = Math.Max((int)(seconds * fps), 1);
        var times = Enumerable.Range(0, count).Select(i => TimeSpan.FromSeconds((double)i / fps)).ToList();
        var dir = Path.GetDirectoryName(Path.GetFullPath(output)) ?? ".";
        var temp = Path.Combine(dir, $".swivel-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                try { File.SetAttributes(temp, FileAttributes.Hidden); } catch (IOException) { }
                GifWriter? writer = null;
                frames.FramesAt(times, 540, (i, frame) =>
                {
                    writer ??= new GifWriter(file, frame.Width, frame.Height, loopForever: true);
                    // 1/12 s is 8⅓ hundredths: alternate 8 and 9 so the clip keeps its length.
                    int delay = (int)Math.Round((i + 1) * 100.0 / fps) - (int)Math.Round(i * 100.0 / fps);
                    writer.AddFrame(frame, delay, dither: false);
                });
                if (writer is null) throw new ToolError(L("Couldn't create GIF"));
                writer.Finish();
            }
            File.Move(temp, output, overwrite: false);
            Images.Unhide(output);
        }
        catch (Exception e)
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            if (e is IOException or UnauthorizedAccessException) throw new ToolError(L("Couldn't create GIF"));
            throw;
        }
        return new List<string> { output };
    });
}

static class Audio
{
    public static List<AudioFormat> Formats =>
    [
        new("M4A", "m4a"),
        new("WAV", "wav"),
        new("AIFF", "aiff"),
        new("FLAC", "flac"),
        new("ALAC", "m4a"),
        new("CAF", "caf"),
        new("AAC", "aac"),
        new("MP3", "mp3"),  // Windows extra: Media Foundation has an MP3 encoder
    ];

    public static List<FileOption> ToolItems =>
    [
        // afconvert -s 3 -u vbrq 40 / 90 (VBR). Media Foundation's AAC encoder is CBR and lowers the sample rate on its
        // own when the bitrate is low for it (64 kbps stereo comes out at 22.05 kHz), so Compress keeps 96 kbps.
        new("Compress", p => RunAac(p, " compressed", 96_000, null)),
        new("Mono", p => RunAac(p, " mono", 64_000, 1)),
        // afconvert aac@22050 -b 48000 -c 1: the same request (the encoder may settle on 32 kbps at 22.05 kHz).
        new("Voice Memo", p => RunAac(p, " voice", 48_000, 1, voice: true)),
    ];

    public static Task<List<string>> Convert(string path, AudioFormat format) =>
        Run(path, format, format.Name == "ALAC" ? " lossless" : "", fromVideo: false);

    internal static async Task<List<string>> ExtractAac(string path) =>
        [await Transcode.Run(path, Paths.Output(path, "m4a"), s => Transcode.AudioProfile(s, "M4A", Path.GetFileName(path)))];

    static async Task<List<string>> RunAac(string path, string suffix, uint bitrate, uint? channels, bool voice = false)
    {
        var output = Paths.Output(path, "m4a", suffix);
        return [await Transcode.Run(path, output, s => Transcode.AacProfile(s, bitrate, channels, voice, Path.GetFileName(path)))];
    }

    internal static async Task<List<string>> Run(string path, AudioFormat format, string suffix, bool fromVideo)
    {
        var output = Paths.Output(path, format.Ext, suffix);
        var name = Path.GetFileName(path);
        switch (format.Name)
        {
            case "AIFF" or "CAF":
            {
                var pcm = await Transcode.ToPcm(path, null, null);
                var bytes = format.Name == "AIFF" ? AudioContainers.Aiff(pcm) : AudioContainers.Caf(pcm);
                Images.WriteAtomically(output, bytes);
                return [output];
            }
            case "AAC":
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
                var m4a = Path.Combine(dir, $".swivel-{Guid.NewGuid():N}.m4a");
                try
                {
                    await Transcode.Run(path, m4a, s => Transcode.AudioProfile(s, "M4A", name), hidden: true);
                    byte[] adts;
                    try { adts = await Task.Run(() => AudioContainers.Adts(m4a)); }
                    catch (InvalidDataException) { throw new ToolError(L("Couldn't convert %@", name)); }
                    Images.WriteAtomically(output, adts);
                    return [output];
                }
                finally { try { File.Delete(m4a); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            }
            default:
                return [await Transcode.Run(path, output, s => Transcode.AudioProfile(s, format.Name, name))];
        }
    }

    /// Decodes every file to PCM in the first file's format, joins them, and encodes "Merged.m4a" next to the first.
    internal static async Task<string> Merge(IReadOnlyList<string> paths)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(paths[0])) ?? ".";
        var output = Paths.Unique(Path.Combine(dir, "Merged.m4a"));
        var firstFile = await Transcode.Open(paths[0]);
        var firstProfile = await Transcode.SourceProfile(firstFile, Path.GetFileName(paths[0]));
        if (firstProfile.Audio is not { } first) throw new ToolError(L("No audio track"));
        uint rate = Transcode.AacRate(first.SampleRate), channels = Math.Clamp(first.ChannelCount, 1u, 2u);

        var parts = new List<AudioContainers.Pcm>();
        foreach (var path in paths) parts.Add(await Transcode.ToPcm(path, rate, channels));
        var joined = new byte[parts.Sum(p => (long)p.Data.Length)];
        long at = 0;
        foreach (var part in parts) { Buffer.BlockCopy(part.Data, 0, joined, (int)at, part.Data.Length); at += part.Data.Length; }

        var wav = Path.Combine(dir, $".swivel-{Guid.NewGuid():N}.wav");
        try
        {
            File.WriteAllBytes(wav, AudioContainers.Wav(new AudioContainers.Pcm((int)channels, (int)rate, 16, joined)));
            return await Transcode.Run(wav, output, s => Transcode.AudioProfile(s, "M4A", "Merged.m4a"));
        }
        finally { try { File.Delete(wav); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}

/// MediaTranscoder plumbing shared by Video and Audio.
static class Transcode
{
    /// The M4A rate (afconvert vbrq 110 on the Mac).
    public const uint AacHigh = 192_000;

    static readonly Lazy<Task<bool>> hevc = new(async () =>
    {
        try
        {
            var query = new Windows.Media.Core.CodecQuery();
            var found = await query.FindAllAsync(Windows.Media.Core.CodecKind.Video, Windows.Media.Core.CodecCategory.Encoder,
                Windows.Media.Core.CodecSubtypes.VideoFormatHevc);
            return found.Count > 0;
        }
        catch (Exception) { return true; }  // can't tell: offer it and let the transcoder report
    });

    static Transcode() => _ = hevc.Value;

    /// Touching the class starts the HEVC encoder query.
    public static void WarmUp() { }

    /// null while the codec query is still running.
    public static bool? HevcAvailable => hevc.Value.IsCompletedSuccessfully ? hevc.Value.Result : null;

    public static Task<bool> HevcAvailableAsync() => hevc.Value;

    public static async Task<StorageFile> Open(string path)
    {
        try { return await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)); }
        catch (Exception) { throw new ToolError(L("Couldn't read %@", Path.GetFileName(path))); }
    }

    public static async Task<MediaEncodingProfile> SourceProfile(StorageFile file, string name)
    {
        try { return await MediaEncodingProfile.CreateFromFileAsync(file); }
        catch (Exception) { throw new ToolError(L("Couldn't read %@", name)); }
    }

    /// Transcodes `source` into a hidden temp file next to `output` with the profile `make` builds from the source's
    /// own properties, then moves it into place. Returns the final path.
    public static async Task<string> Run(string source, string output, Func<MediaEncodingProfile, MediaEncodingProfile> make, bool hidden = false)
    {
        var file = await Open(source);
        var profile = make(await SourceProfile(file, Path.GetFileName(source)));
        var name = Path.GetFileName(source);
        return await WithTemp(output, async temp =>
        {
            var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
            PrepareTranscodeResult prepared;
            try { prepared = await transcoder.PrepareFileTranscodeAsync(file, temp, profile); }
            catch (Exception) { throw new ToolError(L("This file can't be converted that way")); }
            if (!prepared.CanTranscode)
                throw new ToolError(prepared.FailureReason == TranscodeFailureReason.CodecNotFound
                    ? L("This PC doesn't have the codec for %@", name)
                    : L("This file can't be converted that way"));
            try { await prepared.TranscodeAsync(); }
            catch (Exception) { throw new ToolError(L("Couldn't convert %@", name)); }
        }, keepHidden: hidden);
    }

    /// Runs `work` on a fresh hidden StorageFile in the output's folder; on success moves it to `output` (or the next
    /// free name), on failure deletes it.
    public static async Task<string> WithTemp(string output, Func<StorageFile, Task> work, bool keepHidden = false)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(output)) ?? ".";
        var temp = keepHidden ? output : Path.Combine(dir, $".swivel-{Guid.NewGuid():N}{Path.GetExtension(output)}");
        try
        {
            File.WriteAllBytes(temp, []);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new ToolError(L("Couldn't save %@", Path.GetFileName(output))); }
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(temp);
            // Hidden only once StorageFile has it: GetFileFromPathAsync refuses hidden files.
            try { File.SetAttributes(temp, FileAttributes.Hidden); } catch (IOException) { }
            await work(file);
            if (keepHidden) return temp;
            var final = File.Exists(output) || Directory.Exists(output) ? Paths.Unique(output) : output;
            File.Move(temp, final, overwrite: false);
            try { File.SetAttributes(final, File.GetAttributes(final) & ~FileAttributes.Hidden); } catch (IOException) { }
            return final;
        }
        catch (Exception e)
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            if (e is ToolError) throw;
            throw new ToolError(L("Couldn't save %@", Path.GetFileName(output)));
        }
    }

    // A track with a display rotation (phone video held upright) needs nothing extra: MediaTranscoder writes the
    // frames turned upright (checked by ConverterTests.RotatedVideoComesOutUpright), and the size below is of the
    // stored frames, which the box fits the same way either way.
    public static MediaEncodingProfile VideoProfile(MediaEncodingProfile source, Video.VideoPreset preset, string name)
    {
        var video = source.Video ?? throw new ToolError(L("No video track"));
        if (video.Width == 0 || video.Height == 0) throw new ToolError(L("Couldn't read %@", name));
        double aspect = video.PixelAspectRatio is { Numerator: > 0, Denominator: > 0 } par ? (double)par.Numerator / par.Denominator : 1;
        double width = video.Width * aspect, height = video.Height;
        // The box follows the frame's own orientation (a portrait 1080p box is 1080 wide), so it fits the same
        // whether or not a rotation flag turns the frames for display.
        double scale = 1;
        if (preset.BoxLong > 0)
        {
            bool landscape = width >= height;
            double boxW = landscape ? preset.BoxLong : preset.BoxShort, boxH = landscape ? preset.BoxShort : preset.BoxLong;
            scale = Math.Min(1, Math.Min(boxW / width, boxH / height));
        }
        uint w = Even(width * scale), h = Even(height * scale);

        double fps = video.FrameRate is { Numerator: > 0, Denominator: > 0 } rate ? (double)rate.Numerator / rate.Denominator : 30;
        var profile = preset.Hevc ? MediaEncodingProfile.CreateHevc(VideoEncodingQuality.HD1080p) : MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        profile.Video.Width = w;
        profile.Video.Height = h;
        if (fps > 60) { profile.Video.FrameRate.Numerator = 60; profile.Video.FrameRate.Denominator = 1; fps = 60; }
        else if (video.FrameRate is { Numerator: > 0, Denominator: > 0 })
        {
            profile.Video.FrameRate.Numerator = video.FrameRate.Numerator;
            profile.Video.FrameRate.Denominator = video.FrameRate.Denominator;
        }
        profile.Video.PixelAspectRatio.Numerator = 1;
        profile.Video.PixelAspectRatio.Denominator = 1;
        double bitrate = w * h * fps * preset.BitsPerPixel * (preset.Hevc ? 0.6 : 1);
        // Don't inflate a small source: stay within 1.5× of its bitrate (scaled to the new size), above a floor.
        if (video.Bitrate > 0 && video.Subtype is not ("RGB32" or "ARGB32" or "YUY2" or "NV12" or "IYUV" or "UYVY"))
            bitrate = Math.Min(bitrate, Math.Max(video.Bitrate * (double)(w * h) / (video.Width * video.Height) * 1.5, bitrate * 0.4));
        profile.Video.Bitrate = (uint)Math.Clamp(bitrate, 200_000, 100_000_000);
        if (preset.Audio && source.Audio is { } audio)
            profile.Audio = AudioEncodingProperties.CreateAac(AacRate(audio.SampleRate), Math.Clamp(audio.ChannelCount, 1u, 2u), AacHigh);
        else
            profile.Audio = null;
        return profile;
    }

    static uint Even(double v) => (uint)Math.Max(2, ((int)Math.Round(v)) & ~1);

    /// Full-bandwidth AAC rates: 44.1 kHz for the 44.1 family, else 48 kHz.
    public static uint AacRate(uint source) => source is 0 or 44100 or 22050 or 11025 or 88200 or 176400 ? 44100u : 48000u;

    public static MediaEncodingProfile AacProfile(MediaEncodingProfile source, uint bitrate, uint? channels, bool voice, string name)
    {
        var audio = source.Audio ?? throw new ToolError(L("No audio track"));
        var profile = MediaEncodingProfile.CreateM4a(AudioEncodingQuality.High);
        uint ch = channels ?? Math.Clamp(audio.ChannelCount, 1u, 2u);
        profile.Audio = AudioEncodingProperties.CreateAac(voice ? 22050u : AacRate(audio.SampleRate), ch, bitrate);
        profile.Video = null;
        return profile;
    }

    public static MediaEncodingProfile AudioProfile(MediaEncodingProfile source, string format, string name)
    {
        var audio = source.Audio ?? throw new ToolError(L("No audio track"));
        uint rate = audio.SampleRate == 0 ? 44100 : audio.SampleRate;
        uint channels = Math.Clamp(audio.ChannelCount == 0 ? 2 : audio.ChannelCount, 1u, 8u);
        uint bits = audio.BitsPerSample >= 24 ? 24u : 16u;
        MediaEncodingProfile profile;
        switch (format)
        {
            case "WAV":
                profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.High);
                profile.Audio = AudioEncodingProperties.CreatePcm(rate, channels, 16);
                break;
            case "FLAC":
                profile = MediaEncodingProfile.CreateFlac(AudioEncodingQuality.High);
                profile.Audio = AudioEncodingProperties.CreateFlac(rate, channels, bits);
                break;
            case "ALAC":
                profile = MediaEncodingProfile.CreateAlac(AudioEncodingQuality.High);
                profile.Audio = AudioEncodingProperties.CreateAlac(rate, channels, bits);
                break;
            case "MP3":
                profile = MediaEncodingProfile.CreateMp3(AudioEncodingQuality.High);
                profile.Audio = AudioEncodingProperties.CreateMp3(rate is 32000 or 44100 or 48000 ? rate : AacRate(rate), Math.Min(channels, 2u), 256_000);
                break;
            default:  // M4A (afconvert aac vbrq 110 ≈ the encoder's highest CBR rate)
                profile = MediaEncodingProfile.CreateM4a(AudioEncodingQuality.High);
                profile.Audio = AudioEncodingProperties.CreateAac(AacRate(rate), Math.Min(channels, 2u), AacHigh);
                break;
        }
        profile.Video = null;
        return profile;
    }

    /// Decodes a file's sound to 16-bit PCM (in a hidden temp WAV), optionally resampled / remixed.
    public static async Task<AudioContainers.Pcm> ToPcm(string path, uint? rate, uint? channels)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        var wav = Path.Combine(dir, $".swivel-{Guid.NewGuid():N}.wav");
        var name = Path.GetFileName(path);
        try
        {
            await Run(path, wav, source =>
            {
                var profile = AudioProfile(source, "WAV", name);
                if (rate != null || channels != null)
                    profile.Audio = AudioEncodingProperties.CreatePcm(rate ?? profile.Audio.SampleRate, channels ?? profile.Audio.ChannelCount, 16);
                return profile;
            }, hidden: true);
            try { return await Task.Run(() => AudioContainers.ReadWav(wav)); }
            catch (InvalidDataException) { throw new ToolError(L("Couldn't convert %@", name)); }
        }
        finally { try { File.Delete(wav); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
