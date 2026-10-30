using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Swivel.Core;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace Swivel.Windows;

/// One video held open for Upscale Video 2×…: Media Foundation decodes every frame, the pinned
/// Real-ESRGAN model enlarges it 2× through the same tiles as a still (Upscale), and Media
/// Foundation encodes the frames as H.264 at their original times in a private temporary folder.
/// The sound is decoded by Windows' media editing API, encoded as AAC and added as a second track
/// (Mp4AudioMux) like Trim Video's speed changes. The sound is prepared first (with the time
/// measurement), so a sound Windows can't handle is refused before any frame is upscaled. The
/// result is published beside the original as `name 2x.mp4`; a cancel or failure publishes nothing
/// and the temporary files are removed.
public sealed class VideoUpscaler : IDisposable
{
    public const string Failed = "Windows could not encode the upscaled video. No file was saved.";
    public const string SoundFailed = "Windows could not prepare the video's sound, so no file was saved.";
    public const string NotSquare = "This video has non-square pixels (anamorphic video). Upscale Video 2× writes square pixels, so the result would look squashed; such videos aren't supported.";
    public const string Rotated = "This video is stored with a rotation flag (as phones often do). Upscale Video 2× reads the frames as stored, so the result would be turned; such videos aren't supported.";
    private static readonly Guid RotationKey = new("C380465D-2271-428C-9B83-ECEA3B4A85C1"); // MF_MT_VIDEO_ROTATION
    private readonly NativeFiles.Input input;
    private readonly StorageFile file;
    internal string Workspace { get; }
    public string Path => input.Path;
    public int Width { get; }
    public int Height { get; }
    public double Seconds { get; }
    public double Fps { get; }
    /// Estimated from the length and the average frame rate Windows reports.
    public long Frames { get; }
    public bool HasSound { get; }
    private readonly uint rateNumerator, rateDenominator, bitrate;
    private readonly (int Rate, int Channels) soundFormat;
    private const string Prefix = "Swivel-upscale-";
    /// The decoded sound's sample frames once its AAC file is ready in the workspace's sound folder.
    private long? soundFrames;
    private string SoundFolder => System.IO.Path.Combine(Workspace, "sound");

    private VideoUpscaler(NativeFiles.Input input, StorageFile file, string workspace, VideoEncodingProperties video, double seconds, long frames, MediaClip clip)
    {
        this.input = input; this.file = file; Workspace = workspace;
        Width = (int)video.Width; Height = (int)video.Height; Seconds = seconds; Frames = frames;
        rateNumerator = video.FrameRate.Numerator; rateDenominator = video.FrameRate.Denominator; bitrate = video.Bitrate;
        Fps = rateNumerator > 0 && rateDenominator > 0 ? (double)rateNumerator / rateDenominator : 30;
        HasSound = clip.EmbeddedAudioTracks.Count > 0;
        if (HasSound)
        {
            AudioEncodingProperties? audio = null;
            try { audio = clip.EmbeddedAudioTracks[0].GetAudioEncodingProperties(); } catch (Exception error) when (error is not OperationCanceledException) { }
            soundFormat = VideoEdit.SoundFormat(audio?.SampleRate ?? 0, audio?.ChannelCount ?? 0);
        }
    }

    public static async Task<VideoUpscaler> OpenAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // The held read handle denies writers and deleters, so the file Windows opens by path
        // below is the one checked here, until the work ends.
        SweepStale();
        var input = NativeFiles.OpenRead(path);
        string? workspace = null;
        try
        {
            StorageFile file; MediaClip clip; VideoEncodingProperties video; bool straight;
            try
            {
                file = await NativeAsync.Join(StorageFile.GetFileFromPathAsync(path), token).ConfigureAwait(false);
                clip = await NativeAsync.Join(MediaClip.CreateFromFileAsync(file), token).ConfigureAwait(false);
                video = clip.GetVideoEncodingProperties();
                // Media Foundation's reader hands over the stored frames; a phone video turned by its
                // track header (or a stored size other than the upright one) would come out turned.
                try
                {
                    var profile = await NativeAsync.Join(MediaEncodingProfile.CreateFromFileAsync(file), token).ConfigureAwait(false);
                    straight = profile.Video is { } v && v.Width == video.Width && v.Height == video.Height;
                    input.Stream.Position = 0;
                    straight &= Mp4Retime.StraightVideo(input.Stream) != false;
                    input.Stream.Position = 0;
                }
                catch (Exception error) when (error is not OperationCanceledException) { straight = false; }
            }
            catch (Exception error) when (error is not OperationCanceledException) { throw new UserError(VideoSubtitles.Unreadable); }
            if (video is null || video.Width == 0 || video.Height == 0) throw new UserError(VideoSubtitles.Unreadable);
            if (!straight || video.Properties.TryGetValue(RotationKey, out var turn) && turn is uint degrees && degrees % 360 != 0) throw new UserError(Rotated);
            double seconds = clip.OriginalDuration.TotalSeconds;
            double fps = video.FrameRate.Numerator > 0 && video.FrameRate.Denominator > 0 ? (double)video.FrameRate.Numerator / video.FrameRate.Denominator : 30;
            long frames = VideoUpscale.Check((int)video.Width, (int)video.Height, seconds, fps);
            workspace = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workspace);
            return new VideoUpscaler(input, file, workspace, video, seconds, frames, clip);
        }
        catch
        {
            input.Dispose();
            if (workspace is not null) try { Directory.Delete(workspace, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// Workspaces a killed app left behind: removed once they are a day old. Directory.Delete
    /// removes links inside without following them.
    private static void SweepStale()
    {
        try
        {
            foreach (var stale in new DirectoryInfo(System.IO.Path.GetTempPath()).EnumerateDirectories(Prefix + "*"))
                if ((stale.Attributes & System.IO.FileAttributes.ReparsePoint) == 0 && stale.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1))
                    try { stale.Delete(true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public (int Width, int Height) Output => VideoUpscale.Dimensions(Width, Height);
    public long Blocks => VideoUpscale.Blocks(Width, Height, Frames);

    /// Prepares the sound, loads the model, decodes the first frame and times one model block on
    /// it (its top-left corner), so the estimate reflects this PC and a sound or frame Windows
    /// can't handle is refused before the dialog. Returns whether Windows ML uses a graphics card.
    public (bool Graphics, TimeSpan PerBlock) Measure(CancellationToken token)
    {
        PrepareSound(token);
        bool graphics = UpscaleModel.UsesGraphics(token);
        var frame = new byte[Width * Height * 4];
        using (var reader = Reader())
            if (!reader.Read(frame, out _, out _, token)) throw new UserError(VideoUpscale.Unreadable);
        int w = Math.Min(Width, Upscale.Tile - 2 * Upscale.Halo), h = Math.Min(Height, Upscale.Tile - 2 * Upscale.Halo);
        var corner = new byte[w * h * 4];
        for (int y = 0; y < h; y++) Buffer.BlockCopy(frame, y * Width * 4, corner, y * w * 4, w * 4);
        var clock = Stopwatch.StartNew();
        Upscale.Run(new PixelImage(w, h, corner), VideoUpscale.Factor, Upscale.Tile, tile => UpscaleModel.Predict(tile, token), null, token);
        return (graphics, clock.Elapsed);
    }

    // The frame reader, refusing anamorphic video and frame rates past the encoder's level.
    private VideoFrameReader Reader()
    {
        var reader = new VideoFrameReader(input.Path, Width, Height);
        try
        {
            if (!reader.SquarePixels) throw new UserError(NotSquare);
            if (reader.RateNumerator > 0 && reader.RateDenominator > 0) VideoUpscale.CheckRate(Width, Height, (double)reader.RateNumerator / reader.RateDenominator);
            return reader;
        }
        catch { reader.Dispose(); throw; }
    }

    private void PrepareSound(CancellationToken token)
    {
        if (!HasSound || soundFrames is not null) return;
        try { Directory.CreateDirectory(SoundFolder); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new UserError(SoundFailed); }
        try { soundFrames = SoundAsync(token).GetAwaiter().GetResult(); }
        catch
        {
            foreach (var temporary in new[] { Decoded, Encoded }) try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }
    private string Decoded => System.IO.Path.Combine(SoundFolder, "sound.wav");
    private string Encoded => System.IO.Path.Combine(SoundFolder, "sound.m4a");

    /// Upscales every frame and publishes `name 2x.mp4`. `progress` gets frames done, the
    /// estimated total and the time left; `sound` is called when the sound is being prepared.
    public string Run(Action<long, long, TimeSpan?>? progress, Action? sound, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var (ow, oh) = Output;
        if (HasSound && soundFrames is null) { sound?.Invoke(); PrepareSound(token); }
        string rendered = System.IO.Path.Combine(Workspace, "upscaled.mp4");
        try
        {
            using (var reader = Reader())
            {
                uint numerator = reader.RateNumerator, denominator = reader.RateDenominator;
                if (numerator == 0 || denominator == 0) (numerator, denominator) = rateNumerator > 0 && rateDenominator > 0 ? (rateNumerator, rateDenominator) : (30u, 1u);
                VideoUpscale.CheckRate(Width, Height, (double)numerator / denominator);
                long step = (long)Math.Round(10_000_000.0 * denominator / numerator);
                using var writer = new VideoFrameWriter(rendered, ow, oh, numerator, denominator, VideoUpscale.Bitrate(ow, oh, (double)numerator / denominator, bitrate));
                var frame = new byte[Width * Height * 4];
                var clock = Stopwatch.StartNew();
                long count = 0;
                while (reader.Read(frame, out long time, out long duration, token))
                {
                    if (!VideoUpscale.WithinBlocks(Width, Height, count + 1)) throw new UserError(VideoUpscale.TooLong);
                    var result = Upscale.Run(new PixelImage(Width, Height, frame), VideoUpscale.Factor, Upscale.Tile, tile => UpscaleModel.Predict(tile, token), null, token);
                    token.ThrowIfCancellationRequested();
                    writer.Write(result.Bgra, time, duration > 0 ? duration : step);
                    count++;
                    long total = Math.Max(Frames, count);
                    progress?.Invoke(count, total, VideoUpscale.Left(clock.Elapsed, count, total));
                }
                if (count == 0) throw new UserError(VideoUpscale.Unreadable);
                progress?.Invoke(count, count, TimeSpan.Zero);
                token.ThrowIfCancellationRequested();
                writer.Finish();
            }
            token.ThrowIfCancellationRequested();
            // The prepared sound, cut to the upscaled video's length.
            long play = 0;
            if (HasSound)
                try
                {
                    using var video = new FileStream(rendered, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var (duration, timescale) = Mp4AudioMux.Length(video);
                    play = Math.Min(soundFrames!.Value, VideoEdit.SoundFrames(duration, timescale, soundFormat.Rate));
                }
                catch (UserError) { throw new UserError(SoundFailed); }
            token.ThrowIfCancellationRequested();
            return NativeFiles.WriteBeside(input, System.IO.Path.GetFileNameWithoutExtension(input.Path) + " 2x", "mp4", stream =>
            {
                using var source = new FileStream(rendered, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
                if (source.Length == 0) throw new UserError(Failed);
                if (HasSound)
                {
                    using var audio = new FileStream(Encoded, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
                    try { Mp4AudioMux.Write(source, audio, stream, token, (play, soundFormat.Rate)); }
                    catch (UserError error) when (error.Message == Mp4AudioMux.Failed) { throw new UserError(SoundFailed); }
                    return;
                }
                var buffer = new byte[1 << 20]; int read;
                while ((read = source.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); stream.Write(buffer, 0, read); }
            }, token);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new UserError(Failed); }
        finally { try { File.Delete(rendered); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    /// The whole sound decoded to PCM16 (Windows' media editing API, precise) and encoded as AAC;
    /// returns the decoded sample frames.
    private async Task<long> SoundAsync(CancellationToken token)
    {
        string decoded = Decoded, encoded = Encoded;
        var (rate, channels) = soundFormat;
        long milliseconds = (long)Math.Round(Seconds * 1000);
        try { VideoEdit.CheckSound(milliseconds, 100, rate, channels); } catch (UserError) { throw new UserError(SoundFailed); }
        var folder = await NativeAsync.Join(StorageFolder.GetFolderFromPathAsync(SoundFolder), token).ConfigureAwait(false);
        try
        {
            var clip = await NativeAsync.Join(MediaClip.CreateFromFileAsync(file), token).ConfigureAwait(false);
            var composition = new MediaComposition(); composition.Clips.Add(clip);
            var wav = MediaEncodingProfile.CreateWav(AudioEncodingQuality.Auto);
            wav.Audio = AudioEncodingProperties.CreatePcm((uint)rate, (uint)channels, 16); wav.Video = null;
            var target = await NativeAsync.Join(folder.CreateFileAsync(System.IO.Path.GetFileName(decoded), CreationCollisionOption.FailIfExists), token).ConfigureAwait(false);
            var reason = await NativeAsync.Join(composition.RenderToFileAsync(target, MediaTrimmingPreference.Precise, wav), _ => { }, token).ConfigureAwait(false);
            if (reason != TranscodeFailureReason.None) throw new UserError(SoundFailed);
        }
        catch (Exception error) when (error is not OperationCanceledException and not UserError) { throw new UserError(SoundFailed); }
        token.ThrowIfCancellationRequested();

        long frames;
        try
        {
            using var pcm = new FileStream(decoded, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            var info = PcmWave.Read(pcm, token);
            if (info.Rate != rate || info.Channels != channels || info.Bits != 16 || !VideoEdit.SoundMatches(info.Frames, rate, milliseconds)) throw new UserError(SoundFailed);
            frames = info.Frames;
        }
        catch (UserError) { throw new UserError(SoundFailed); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new UserError(SoundFailed); }
        token.ThrowIfCancellationRequested();

        try
        {
            var source = await NativeAsync.Join(StorageFile.GetFileFromPathAsync(decoded), token).ConfigureAwait(false);
            var target = await NativeAsync.Join(folder.CreateFileAsync(System.IO.Path.GetFileName(encoded), CreationCollisionOption.FailIfExists), token).ConfigureAwait(false);
            var m4a = MediaEncodingProfile.CreateM4a(AudioEncodingQuality.High);
            m4a.Audio.SampleRate = (uint)rate; m4a.Audio.ChannelCount = (uint)channels;
            var prepared = await NativeAsync.Join(new MediaTranscoder { AlwaysReencode = true }.PrepareFileTranscodeAsync(source, target, m4a), token).ConfigureAwait(false);
            if (!prepared.CanTranscode) throw new UserError(SoundFailed);
            await NativeAsync.Join(prepared.TranscodeAsync(), _ => { }, token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException and not UserError) { throw new UserError(SoundFailed); }
        token.ThrowIfCancellationRequested();
        return frames;
    }

    public void Dispose()
    {
        input.Dispose();
        try { Directory.Delete(Workspace, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
