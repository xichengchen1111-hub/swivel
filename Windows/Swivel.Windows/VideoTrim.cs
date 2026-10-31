using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;
using Windows.Media.Editing;
using Windows.Media.Effects;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace Swivel.Windows;

/// One video held open for Trim Video…: Windows' media editing API shows frames for the chosen
/// start and end, then encodes the kept part (optionally without sound) as a new H.264/AAC MP4 in
/// a private temporary folder, which is published beside the original as `name trimmed.mp4`.
/// Picture changes go through Windows' built-in video transform effect (crop, quarter turns,
/// mirror), which the media editing API refuses (0xC00DA7FC on the CI runner), so those are
/// encoded with MediaTranscoder and its trim times instead; another speed rewrites the encoded file's timescales (Mp4Retime) before publishing.
/// A kept sound at another speed is decoded separately (the same cut, MediaComposition to PCM16 WAV),
/// time-stretched to the retimed video's exact length with its pitch kept (WaveStretch), encoded
/// as an AAC M4A by Windows and added to the video as a second track (Mp4AudioMux) without
/// re-encoding the picture. Either is saved as `name edited.mp4`.
public sealed class VideoTrimmer : IDisposable
{
    public const string Failed = "Windows could not encode the trimmed video. No file was saved.";
    public const string NoPictureChanges = "This video is stored with a rotation flag (as phones often do). Windows can trim it and change its speed, but not turn, flip or crop it here.";
    public const string StretchNote = "At this speed the sound is time-stretched to the new length with its pitch kept (Swivel's own method, not the Mac's): slowed sound can echo a little and sped-up sound can soften sharp sounds such as claps. Windows then encodes it as AAC beside the unchanged picture.";
    private static readonly Guid RotationKey = new("C380465D-2271-428C-9B83-ECEA3B4A85C1"); // MF_MT_VIDEO_ROTATION
    private readonly NativeFiles.Input input;
    private readonly StorageFile file;
    private readonly MediaComposition frames;
    private readonly SemaphoreSlim frameLock = new(1, 1);
    private readonly string workspace;
    public string Path => input.Path;
    public int Width { get; }
    public int Height { get; }
    /// Whole milliseconds.
    public long Duration { get; }
    public bool HasSound { get; }
    /// The PCM format a kept sound is decoded to before a speed change (VideoEdit.SoundFormat).
    private readonly (int Rate, int Channels) soundFormat;
    /// Windows' transcoder (which applies picture changes) reads the stored frames and doesn't turn
    /// a phone video stored with a rotation flag upright, so picture changes are refused when the
    /// stored size differs from the upright one or an MP4/MOV video track's matrix isn't identity.
    public bool PictureChanges { get; private init; }
    public bool Hours => Duration >= 3_600_000;
    private readonly uint frameNumerator, frameDenominator, bitrate;

    private VideoTrimmer(NativeFiles.Input input, StorageFile file, MediaClip clip, string workspace, VideoEncodingProperties video, long duration)
    {
        this.input = input; this.file = file; this.workspace = workspace;
        frames = new MediaComposition(); frames.Clips.Add(clip);
        Width = (int)video.Width; Height = (int)video.Height; Duration = duration; HasSound = clip.EmbeddedAudioTracks.Count > 0;
        if (HasSound)
        {
            AudioEncodingProperties? audio = null;
            try { audio = clip.EmbeddedAudioTracks[0].GetAudioEncodingProperties(); } catch (Exception error) when (error is not OperationCanceledException) { }
            soundFormat = VideoEdit.SoundFormat(audio?.SampleRate ?? 0, audio?.ChannelCount ?? 0);
        }
        if (!HasSound) soundFormat = (48000, 2);
        frameNumerator = video.FrameRate.Numerator; frameDenominator = video.FrameRate.Denominator; bitrate = video.Bitrate;
    }

    public static async Task<VideoTrimmer> OpenAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // The held read handle denies writers and deleters, so the file Windows opens by path
        // below is the one checked here, until the window closes.
        var input = NativeFiles.OpenRead(path);
        string? workspace = null;
        try
        {
            StorageFile file; MediaClip clip; VideoEncodingProperties video; bool stored;
            try
            {
                file = await NativeAsync.Join(StorageFile.GetFileFromPathAsync(path), token).ConfigureAwait(false);
                clip = await NativeAsync.Join(MediaClip.CreateFromFileAsync(file), token).ConfigureAwait(false);
                video = clip.GetVideoEncodingProperties();
                try
                {
                    var profile = await NativeAsync.Join(MediaEncodingProfile.CreateFromFileAsync(file), token).ConfigureAwait(false);
                    stored = profile.Video is { } v && v.Width == video.Width && v.Height == video.Height;
                    // A 180° or mirrored phone video keeps its size; its track header says so.
                    input.Stream.Position = 0;
                    stored &= Mp4Retime.StraightVideo(input.Stream) != false;
                    input.Stream.Position = 0;
                }
                catch (Exception error) when (error is not OperationCanceledException) { stored = false; }
            }
            catch (Exception error) when (error is not OperationCanceledException) { throw new UserError(VideoSubtitles.Unreadable); }
            if (video is null || video.Width == 0 || video.Height == 0) throw new UserError(VideoSubtitles.Unreadable);
            if (video.Properties.TryGetValue(RotationKey, out var turn) && turn is uint degrees && degrees % 360 != 0) throw new UserError(VideoSubtitles.Rotated);
            double seconds = clip.OriginalDuration.TotalSeconds;
            SubtitleBurn.CheckVideo((int)video.Width, (int)video.Height, seconds);
            workspace = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Swivel-trim-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workspace);
            return new VideoTrimmer(input, file, clip, workspace, video, VideoTrim.Duration(seconds)) { PictureChanges = stored };
        }
        catch
        {
            input.Dispose();
            if (workspace is not null) try { Directory.Delete(workspace, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    internal int SoundRate => soundFormat.Rate;

    /// Refuses decoded or stretched sound beyond the existing PCM size/duration limits.
    public void CheckSound(VideoTrimPlan plan)
    {
        if ((plan.KeepSound && HasSound && (plan.Speed != 100 || plan.Noise is not null || plan.Effects is not null)) || plan.Effects?.Music is not null)
            VideoEdit.CheckSound(plan.Length, plan.Speed, soundFormat.Rate, soundFormat.Channels);
        if (plan.Effects?.Music?.LowerDuringSpeech == true && plan.KeepSound && plan.Effects.Volume > 0 && plan.OutputLength > Transcribe.MaxSeconds * 1000L)
            throw new UserError(Transcribe.TooLong);
    }

    /// The frame rate Windows reports (30 when it reports none), used to find frames.
    public double Fps => frameNumerator > 0 && frameDenominator > 0 ? (double)frameNumerator / frameDenominator : 30;

    /// The frame showing at a time (clamped into the video), at most `box` pixels on its longer
    /// side, frozen for the UI thread. Windows is asked for the middle of that frame, since it
    /// returns the frame nearest the time asked; the frame rate it reports is an average (30.659
    /// for a 30 fps test video), so this can still be a frame off. Near the end,
    /// where Windows may have no frame for the last instant, earlier frames are tried. One frame
    /// is made at a time.
    public async Task<BitmapSource> FrameAsync(long milliseconds, int box, CancellationToken token)
    {
        double scale = Math.Min(1, box / (double)Math.Max(Width, Height));
        int w = Math.Max(2, (int)Math.Round(Width * scale)), h = Math.Max(2, (int)Math.Round(Height * scale));
        double step = 1000 / Math.Max(1, Fps);
        long index = (long)Math.Floor(Math.Clamp(milliseconds, 0, Math.Max(0, Duration - 1)) / step);
        await frameLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            for (int back = 0; ; back++)
            {
                double at = Math.Max(0, Math.Min((index - back + 0.5) * step, Duration - 1));
                global::Windows.Graphics.Imaging.ImageStream image;
                try { image = await NativeAsync.Join(frames.GetThumbnailAsync(TimeSpan.FromMilliseconds(at), w, h, VideoFramePrecision.NearestFrame), token).ConfigureAwait(false); }
                catch (ArgumentException) when (back < 15 && index - back > 0) { continue; }
                using (image)
                {
                    using var stream = image.AsStreamForRead();
                    // Copied out of the decoder, so the frame belongs to no thread and can be turned and cropped anywhere.
                    var decoded = new FormatConvertedBitmap(BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0], PixelFormats.Bgra32, null, 0);
                    var pixels = new byte[decoded.PixelWidth * decoded.PixelHeight * 4]; decoded.CopyPixels(pixels, decoded.PixelWidth * 4, 0);
                    var frame = BitmapSource.Create(decoded.PixelWidth, decoded.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, decoded.PixelWidth * 4);
                    frame.Freeze();
                    return frame;
                }
            }
        }
        finally { frameLock.Release(); }
    }

    /// Windows' transform effect, as checked on the Windows CI runner with a four-colour picture:
    /// a crop alone and a turn (then mirror) alone are exact, but a crop that isn't square
    /// combined with a turn in one effect comes out letterboxed. So a crop is one effect and the
    /// turn and mirror are a second one after it.
    internal static VideoTransformEffectDefinition[] Effects(VideoGeometry g, int width, int height)
    {
        var list = new System.Collections.Generic.List<VideoTransformEffectDefinition>();
        var c = g.SourceCrop;
        if (c != new PixelRect(0, 0, width, height))
            list.Add(new VideoTransformEffectDefinition { CropRectangle = new global::Windows.Foundation.Rect(c.X, c.Y, c.Width, c.Height), OutputSize = new global::Windows.Foundation.Size(c.Width, c.Height) });
        if (g.Rotation != 0 || g.Mirror)
            list.Add(new VideoTransformEffectDefinition
            {
                Rotation = g.Rotation switch { 1 => MediaRotation.Clockwise90Degrees, 2 => MediaRotation.Clockwise180Degrees, 3 => MediaRotation.Clockwise270Degrees, _ => MediaRotation.None },
                Mirror = g.Mirror ? MediaMirroringOptions.Horizontal : MediaMirroringOptions.None,
                OutputSize = new global::Windows.Foundation.Size(g.Width, g.Height),
            });
        return list.ToArray();
    }

    /// A preview frame (any size, the whole upright picture) as the plan's picture would show it.
    public static BitmapSource Preview(BitmapSource frame, int width, int height, VideoPicture? picture)
    {
        if (picture is null || picture.IsIdentity) return frame;
        var g = VideoEdit.Geometry(width, height, picture);
        var transform = new TransformGroup();
        transform.Children.Add(new RotateTransform(90 * picture.QuarterTurns));
        transform.Children.Add(new ScaleTransform(picture.FlipHorizontal ? -1 : 1, picture.FlipVertical ? -1 : 1));
        BitmapSource seen = new TransformedBitmap(frame, transform);
        double scale = seen.PixelWidth / (double)g.SeenWidth;
        int x = Math.Clamp((int)Math.Round(g.Seen.X * scale), 0, seen.PixelWidth - 1), y = Math.Clamp((int)Math.Round(g.Seen.Y * scale), 0, seen.PixelHeight - 1);
        int w = Math.Clamp((int)Math.Round(g.Seen.Width * scale), 1, seen.PixelWidth - x), h = Math.Clamp((int)Math.Round(g.Seen.Height * scale), 1, seen.PixelHeight - y);
        var cropped = new CroppedBitmap(seen, new System.Windows.Int32Rect(x, y, w, h));
        cropped.Freeze();
        return cropped;
    }

    internal static BitmapSource ColorPreview(BitmapSource frame, VideoColorSound settings, double time, double length, CancellationToken token)
    {
        var pixels = new byte[frame.PixelWidth * frame.PixelHeight * 4]; frame.CopyPixels(pixels, frame.PixelWidth * 4, 0);
        var adjusted = settings.Apply(new PixelImage(frame.PixelWidth, frame.PixelHeight, pixels), time, length, token);
        var result = BitmapSource.Create(adjusted.Width, adjusted.Height, 96, 96, PixelFormats.Bgra32, null, adjusted.Bgra, adjusted.Width * 4);
        result.Freeze(); return result;
    }

    /// Encodes the kept part and publishes `name trimmed.mp4`, or `name edited.mp4` when the
    /// picture or speed changed. Progress runs from 0 to 1.
    internal async Task<string> TrimAsync(VideoTrimPlan plan, Action<double>? progress, CancellationToken token)
    {
        plan = VideoTrim.Plan(plan.Start, plan.End, Duration, plan.KeepSound, HasSound, plan.Picture, plan.Speed, plan.Noise, soundFormat.Rate, plan.Effects);
        if (plan.Picture is not null && !PictureChanges) throw new UserError(NoPictureChanges);
        var geometry = plan.Picture is null ? null : VideoEdit.Geometry(Width, Height, plan.Picture);
        int width = geometry?.Width ?? Width, height = geometry?.Height ?? Height;
        if (plan.Speed != 100) VideoEdit.CheckRate(width, height, Fps, plan.Speed);
        // Sound processing runs separately after the picture; the picture's encoding gets 80% of the progress.
        bool processFrames = plan.Effects?.FrameChanges == true;
        bool processSound = plan.KeepSound && HasSound && (plan.Speed != 100 || plan.Noise is not null || plan.Effects is not null) || plan.Effects?.Music is not null;
        CheckSound(plan);
        Action<double>? basePictureProgress = processSound && progress is not null ? value => progress(value * .8) : progress;
        Action<double>? pictureProgress = processFrames ? value => basePictureProgress?.Invoke(value * .5) : basePictureProgress;
        string rendered = System.IO.Path.Combine(workspace, "trimmed.mp4");
        string decoded = System.IO.Path.Combine(workspace, "sound.wav"), stretched = System.IO.Path.Combine(workspace, "stretched.wav"), encoded = System.IO.Path.Combine(workspace, "sound.m4a");
        using var music = plan.Effects?.Music is { } chosenMusic ? await SystemAudioDocument.OpenAsync(chosenMusic.Path, token).ConfigureAwait(false) : null;
        try
        {
            var clip = await NativeAsync.Join(MediaClip.CreateFromFileAsync(file), token).ConfigureAwait(false);
            clip.TrimTimeFromStart = TimeSpan.FromMilliseconds(plan.Start);
            var tail = clip.OriginalDuration - TimeSpan.FromMilliseconds(plan.End);
            clip.TrimTimeFromEnd = tail > TimeSpan.Zero ? tail : TimeSpan.Zero;
            // Without sound the profile has no audio stream at all; at another speed the
            // picture is encoded alone and the processed sound is added afterwards.
            bool sound = plan.KeepSound && HasSound && !processSound;
            var composition = new MediaComposition();
            composition.Clips.Add(clip);
            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
            profile.Video.Width = (uint)width; profile.Video.Height = (uint)height;
            if (frameNumerator > 0 && frameDenominator > 0) { profile.Video.FrameRate.Numerator = frameNumerator; profile.Video.FrameRate.Denominator = frameDenominator; }
            double fps = frameNumerator > 0 && frameDenominator > 0 ? (double)frameNumerator / frameDenominator : 30;
            // At least the original's bit rate, so the kept part loses as little as re-encoding allows.
            // Faster speeds play more bits a second, so they ask for proportionally fewer (still at least 1 Mbit/s).
            double wanted = Math.Max(bitrate, width * (double)height * fps * 0.1);
            profile.Video.Bitrate = (uint)Math.Clamp(plan.Speed == 100 ? wanted : VideoEdit.EncodeBitrate(wanted, plan.Speed), 1_000_000, 100_000_000);
            if (!sound) profile.Audio = null;
            var output = await NativeAsync.Join(StorageFolder.GetFolderFromPathAsync(workspace), token).ConfigureAwait(false);
            var target = await NativeAsync.Join(output.CreateFileAsync("trimmed.mp4", CreationCollisionOption.FailIfExists), token).ConfigureAwait(false);
            TranscodeFailureReason reason;
            try
            {
                if (geometry is null) reason = await NativeAsync.Join(composition.RenderToFileAsync(target, MediaTrimmingPreference.Precise, profile), value => pictureProgress?.Invoke(value / 100), token).ConfigureAwait(false);
                else
                {
                    var transcoder = new MediaTranscoder { AlwaysReencode = true, TrimStartTime = TimeSpan.FromMilliseconds(plan.Start), TrimStopTime = TimeSpan.FromMilliseconds(plan.End) };
                    foreach (var effect in Effects(geometry, Width, Height)) transcoder.AddVideoEffect(effect.ActivatableClassId, true, effect.Properties);
                    var prepared = await NativeAsync.Join(transcoder.PrepareFileTranscodeAsync(file, target, profile), token).ConfigureAwait(false);
                    reason = prepared.CanTranscode ? TranscodeFailureReason.None : prepared.FailureReason;
                    if (prepared.CanTranscode) await NativeAsync.Join(prepared.TranscodeAsync(), value => pictureProgress?.Invoke(Math.Clamp(value / 100, 0, 1)), token).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException) { throw new UserError(Failed); }
            if (reason != TranscodeFailureReason.None) throw new UserError(Failed);
            token.ThrowIfCancellationRequested();
            // Another speed: the frames stay, each lasting 100/speed times as long.
            if (plan.Speed != 100)
                try
                {
                    using var retime = new FileStream(rendered, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                    Mp4Retime.Apply(retime, plan.Speed);
                }
                catch (IOException) { throw new UserError(Failed); }
            string pictureFile = rendered;
            if (processFrames)
            {
                pictureFile = System.IO.Path.Combine(workspace, "colored.mp4");
                ColorFrames(rendered, pictureFile, width, height, plan, value => basePictureProgress?.Invoke(.5 + value * .5), token);
            }
            long soundLength = processSound ? await SoundAsync(plan, pictureFile, decoded, stretched, encoded, music, progress, token).ConfigureAwait(false) : 0;
            return NativeFiles.WriteBeside(input, System.IO.Path.GetFileNameWithoutExtension(input.Path) + (plan.Edited ? " edited" : " trimmed"), "mp4", stream =>
            {
                using var source = new FileStream(pictureFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
                if (source.Length == 0) throw new UserError(Failed);
                if (processSound)
                {
                    using var audio = new FileStream(encoded, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
                    Mp4AudioMux.Write(source, audio, stream, token, (soundLength, soundFormat.Rate));
                    return;
                }
                var buffer = new byte[1 << 20]; int read;
                while ((read = source.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); stream.Write(buffer, 0, read); }
            }, token);
        }
        finally
        {
            foreach (var temporary in new[] { rendered, decoded, stretched, encoded, System.IO.Path.Combine(workspace, "colored.mp4") })
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// The kept part's sound for a speed change: decoded with the same cut as the picture
    /// (MediaComposition, precise trimming, measured within a millisecond on the CI runner),
    /// stretched to exactly the retimed video's length with its pitch kept, then encoded as AAC.
    /// Any step Windows refuses stops the save with VideoEdit.SoundFailed rather than saving a
    /// video whose sound is missing or out of step.
    private async Task<long> SoundAsync(VideoTrimPlan plan, string rendered, string decoded, string stretched, string encoded, SystemAudioDocument? music, Action<double>? progress, CancellationToken token)
    {
        var (rate, channels) = soundFormat;
        var folder = await NativeAsync.Join(StorageFolder.GetFolderFromPathAsync(workspace), token).ConfigureAwait(false);
        if (plan.KeepSound && HasSound) await DecodeSoundAsync(plan, decoded, progress, token).ConfigureAwait(false);

        // A speed change stretches to the movie length. At 1× the cleaned PCM keeps
        // its samples, with AAC padding cut to the shorter of sound and picture.
        long frames;
        try
        {
            ulong duration; uint timescale;
            using (var video = new FileStream(rendered, FileMode.Open, FileAccess.Read, FileShare.Read)) (duration, timescale) = Mp4AudioMux.Length(video);
            frames = VideoEdit.SoundFrames(duration, timescale, rate);
            if (!plan.KeepSound || !HasSound)
            {
                using var silence = new FileStream(stretched, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                VideoSound.Silence(silence, rate, channels, frames, token);
            }
            else
            {
                using var input = new FileStream(decoded, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                var info = PcmWave.Read(input, token);
                if (info.Rate != rate || info.Channels != channels || info.Bits != 16 || !VideoEdit.SoundMatches(info.Frames, rate, plan.Length)) throw new UserError(VideoEdit.SoundFailed);
                using var output = new FileStream(stretched, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16);
                using var cleaned = plan.Noise is null ? null : new FileStream(System.IO.Path.Combine(workspace, "cleaned-" + Guid.NewGuid().ToString("N") + ".wav"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose);
                long preparedFrames = VideoEdit.PrepareSound(input, output, cleaned, plan.Noise, plan.Speed, frames, Fps, token);
                if (plan.Effects is null) frames = preparedFrames;
            }
        }
        catch (IOException) { throw new UserError(VideoEdit.SoundFailed); }
        progress?.Invoke(.9);
        token.ThrowIfCancellationRequested();

        string preparedSound = stretched;
        string mixed = System.IO.Path.Combine(workspace, "mixed-" + Guid.NewGuid().ToString("N") + ".wav");
        string backing = System.IO.Path.Combine(workspace, "music-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            if (plan.Effects is { } settings)
            {
                WaveDucking? ducking = null;
                if (settings.Music?.LowerDuringSpeech == true && plan.KeepSound && settings.Volume > 0)
                {
                    using var speech = await TranscribeDocument.OpenAsync(stretched, token).ConfigureAwait(false);
                    var segments = speech.Run(null, null, token).Segments;
                    ducking = new WaveDucking(Gain: .25, Speech: segments);
                }
                using var primary = new FileStream(stretched, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                if (music is not null)
                {
                    WaveMusicRatePlan.Create(music.Info, rate);
                    using var inputMusic = music.OpenPcm();
                    using var converted = new FileStream(backing, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    WaveConverter.Export(inputMusic, converted, new WaveFormat(rate, music.Info.Channels, 16), token);
                }
                using var musicStream = music is null ? null : new FileStream(backing, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                using (var output = new FileStream(mixed, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                    VideoSound.Export(primary, musicStream, output, frames, settings, ducking, token);
                preparedSound = mixed;
            }
            var source = await NativeAsync.Join(StorageFile.GetFileFromPathAsync(preparedSound), token).ConfigureAwait(false);
            var target = await NativeAsync.Join(folder.CreateFileAsync(System.IO.Path.GetFileName(encoded), CreationCollisionOption.FailIfExists), token).ConfigureAwait(false);
            var m4a = MediaEncodingProfile.CreateM4a(AudioEncodingQuality.High);
            m4a.Audio.SampleRate = (uint)rate; m4a.Audio.ChannelCount = (uint)channels;
            var prepared = await NativeAsync.Join(new MediaTranscoder { AlwaysReencode = true }.PrepareFileTranscodeAsync(source, target, m4a), token).ConfigureAwait(false);
            if (!prepared.CanTranscode) throw new UserError(VideoEdit.SoundFailed);
            await NativeAsync.Join(prepared.TranscodeAsync(), value => progress?.Invoke(.9 + Math.Clamp(value / 100, 0, 1) * .1), token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException and not UserError) { throw new UserError(VideoEdit.SoundFailed); }
        finally
        {
            foreach (string path in new[] { mixed, backing })
                try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        token.ThrowIfCancellationRequested();
        return frames;
    }

    // The precise cut/geometry pass above also normalizes rotation and HDR to today's SDR output.
    // Only color/fades add this pass; trim and transform-only exports retain their original encoder path.
    private void ColorFrames(string source, string target, int width, int height, VideoTrimPlan plan, Action<double>? progress, CancellationToken token)
    {
        ulong duration; uint timescale;
        using (var movie = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read)) (duration, timescale) = Mp4AudioMux.Length(movie);
        double length = duration / (double)timescale;
        bool sideways = height > 2304;
        int storedWidth = sideways ? height : width, storedHeight = sideways ? width : height;
        try
        {
            using (var reader = new VideoFrameReader(source, width, height))
            {
                if (!reader.SquarePixels) throw new UserError(VideoUpscaler.NotSquare);
                uint numerator = reader.RateNumerator, denominator = reader.RateDenominator;
                if (numerator == 0 || denominator == 0) (numerator, denominator) = (30, 1);
                using var writer = new VideoFrameWriter(target, storedWidth, storedHeight, numerator, denominator,
                    (uint)Math.Clamp(width * (double)height * numerator / denominator * .1, 1_000_000, 100_000_000));
                var buffer = new byte[width * height * 4];
                long count = 0, step = (long)Math.Round(10_000_000.0 * denominator / numerator);
                while (reader.Read(buffer, out long time, out long frameDuration, token))
                {
                    var image = plan.Effects!.Apply(new PixelImage(width, height, buffer), time / 10_000_000.0, length, token);
                    if (sideways) image = VideoColorSound.StoreCounterclockwise(image, token);
                    writer.Write(image.Bgra, time, frameDuration > 0 ? frameDuration : step);
                    count++; progress?.Invoke(Math.Clamp(time / (length * 10_000_000), 0, 1));
                }
                if (count == 0) throw new UserError(Failed);
                token.ThrowIfCancellationRequested(); writer.Finish();
            }
            if (sideways)
            {
                using var movie = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                Mp4Retime.DisplayClockwise(movie, storedWidth, storedHeight);
            }
        }
        catch (UserError error) when (error.Message is VideoUpscaler.Failed or VideoUpscale.Unreadable or VideoEdit.CannotRetime) { throw new UserError(Failed); }
    }

    private async Task DecodeSoundAsync(VideoTrimPlan plan, string decoded, Action<double>? progress, CancellationToken token)
    {
        var (rate, channels) = soundFormat;
        var folder = await NativeAsync.Join(StorageFolder.GetFolderFromPathAsync(workspace), token).ConfigureAwait(false);
        try
        {
            var clip = await NativeAsync.Join(MediaClip.CreateFromFileAsync(file), token).ConfigureAwait(false);
            clip.TrimTimeFromStart = TimeSpan.FromMilliseconds(plan.Start);
            var tail = clip.OriginalDuration - TimeSpan.FromMilliseconds(plan.End);
            clip.TrimTimeFromEnd = tail > TimeSpan.Zero ? tail : TimeSpan.Zero;
            var composition = new MediaComposition(); composition.Clips.Add(clip);
            var wav = MediaEncodingProfile.CreateWav(AudioEncodingQuality.Auto);
            wav.Audio = AudioEncodingProperties.CreatePcm((uint)rate, (uint)channels, 16); wav.Video = null;
            var target = await NativeAsync.Join(folder.CreateFileAsync(System.IO.Path.GetFileName(decoded), CreationCollisionOption.FailIfExists), token).ConfigureAwait(false);
            var reason = await NativeAsync.Join(composition.RenderToFileAsync(target, MediaTrimmingPreference.Precise, wav), value => progress?.Invoke(.8 + Math.Clamp(value / 100, 0, 1) * .05), token).ConfigureAwait(false);
            if (reason != TranscodeFailureReason.None) throw new UserError(VideoEdit.SoundFailed);
        }
        catch (Exception error) when (error is not OperationCanceledException and not UserError) { throw new UserError(VideoEdit.SoundFailed); }
        token.ThrowIfCancellationRequested();
    }

    // Caller owns the returned document and must join all preview work before disposal.
    internal async Task<WaveDocument> NoiseDocumentAsync(VideoTrimPlan plan, CancellationToken token)
    {
        VideoEdit.CheckSound(plan.Length, 100, soundFormat.Rate, soundFormat.Channels);
        string path = System.IO.Path.Combine(workspace, "noise-preview-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            await DecodeSoundAsync(plan, path, null, token).ConfigureAwait(false);
            var document = WaveDocument.Open(path, token);
            if (!VideoEdit.SoundMatches(document.Info.Frames, document.Info.Rate, plan.Length))
            {
                document.Dispose(); throw new UserError(VideoEdit.SoundFailed);
            }
            return document;
        }
        catch { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } throw; }
    }

    public void Dispose()
    {
        input.Dispose();
        frameLock.Dispose();
        try { Directory.Delete(workspace, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
