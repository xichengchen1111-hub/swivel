using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinStorage = global::Windows.Storage;
using WinEditing = global::Windows.Media.Editing;
using WinProfiles = global::Windows.Media.MediaProperties;
using WinTranscoding = global::Windows.Media.Transcoding;

namespace Swivel;

/// The strip under a trim preview: a filmstrip (video) or a waveform (audio) across the whole clip, the cut lit, the
/// rest dimmed, and the playhead. A click or drag on it moves the playhead.
sealed class TrimStrip : FrameworkElement
{
    public double Duration { get; set; } = 1;
    public double Start { get; set; }
    public double End { get; set; } = 1;
    public double Playhead { get; set; }
    public IReadOnlyList<BitmapSource> Thumbnails { get; set; } = [];
    /// Peak levels 0–1, evenly spaced over the clip.
    public float[] Peaks { get; set; } = [];
    public event Action<double>? Scrubbed;

    public TrimStrip()
    {
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        Cursor = Cursors.IBeam;
    }

    double X(double seconds) => Duration <= 0 ? 0 : Math.Clamp(seconds / Duration, 0, 1) * ActualWidth;

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(RenderSize);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        var clip = new RectangleGeometry(bounds, 6, 6);
        dc.PushClip(clip);
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)), null, bounds);
        if (Thumbnails.Count > 0)
        {
            double tile = bounds.Width / Thumbnails.Count;
            for (int i = 0; i < Thumbnails.Count; i++)
            {
                var image = Thumbnails[i];
                var cell = new Rect(i * tile, 0, tile + 0.5, bounds.Height);
                double scale = Math.Max(cell.Width / image.PixelWidth, cell.Height / image.PixelHeight);
                var size = new Size(image.PixelWidth * scale, image.PixelHeight * scale);
                dc.PushClip(new RectangleGeometry(cell));
                dc.DrawImage(image, new Rect(cell.X + (cell.Width - size.Width) / 2, cell.Y + (cell.Height - size.Height) / 2, size.Width, size.Height));
                dc.Pop();
            }
        }
        if (Peaks.Length > 0)
        {
            var brush = new SolidColorBrush(SystemColors.AccentColor);
            brush.Freeze();
            double step = bounds.Width / Peaks.Length, mid = bounds.Height / 2;
            var bars = new StreamGeometry();
            using (var g = bars.Open())
                for (int i = 0; i < Peaks.Length; i++)
                {
                    double h = Math.Max(1, Peaks[i] * bounds.Height * 0.8);
                    double x = i * step;
                    g.BeginFigure(new Point(x, mid - h / 2), true, true);
                    g.LineTo(new Point(x + Math.Max(1, step * 0.7), mid - h / 2), false, false);
                    g.LineTo(new Point(x + Math.Max(1, step * 0.7), mid + h / 2), false, false);
                    g.LineTo(new Point(x, mid + h / 2), false, false);
                }
            bars.Freeze();
            dc.DrawGeometry(brush, null, bars);
        }
        // Outside the cut is dimmed.
        var dim = new SolidColorBrush(Color.FromArgb(0x90, 0, 0, 0));
        double x0 = X(Start), x1 = X(End);
        if (x0 > 0) dc.DrawRectangle(dim, null, new Rect(0, 0, x0, bounds.Height));
        if (x1 < bounds.Width) dc.DrawRectangle(dim, null, new Rect(x1, 0, bounds.Width - x1, bounds.Height));
        dc.Pop();
        var accent = new Pen(new SolidColorBrush(SystemColors.AccentColor), 2);
        dc.DrawRoundedRectangle(null, accent, new Rect(x0 + 1, 1, Math.Max(0, x1 - x0 - 2), Math.Max(0, bounds.Height - 2)), 5, 5);
        double p = X(Playhead);
        dc.DrawRectangle(Brushes.White, new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0, 0, 0)), 0.5), new Rect(p - 1, -1, 2, bounds.Height + 2));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        CaptureMouse();
        Scrub(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsMouseCaptured) Scrub(e.GetPosition(this).X);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        ReleaseMouseCapture();
    }

    void Scrub(double x) => Scrubbed?.Invoke(Math.Clamp(x / Math.Max(1, ActualWidth), 0, 1) * Duration);
}

/// Trims a video or audio file between two points, with a player to preview the cut (port of TrimEditor.swift).
///
/// The handles stop with a snap at the clip's ends (reaching exactly 0 and the full length) and where they meet;
/// scrubbing shows the frame under the handle, and playback stays inside the cut. Video is cut frame-accurately with
/// Windows.Media.Editing (re-encoded to MP4 / H.264: Windows can't pass a stream through untouched, as AVFoundation's
/// passthrough export does, and can't write QuickTime .mov); audio with Windows.Media.Transcoding to M4A (AAC), as the
/// Mac's Apple M4A preset does.
sealed class TrimEditor : Editor
{
    readonly MediaElement player = new()
    {
        LoadedBehavior = MediaState.Manual,
        UnloadedBehavior = MediaState.Manual,
        ScrubbingEnabled = true,
        Stretch = System.Windows.Media.Stretch.Uniform,
        Volume = 1,
    };
    readonly TrimStrip strip = new() { Height = 52, Margin = new Thickness(12, 8, 12, 0) };
    readonly TrimStrip wave = new() { Margin = new Thickness(16) };
    readonly TextBlock unavailable = new() { TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24), Visibility = Visibility.Collapsed };
    readonly Button playButton = new() { Width = 40, Height = 32, Padding = new Thickness(0) };
    readonly TextBlock playGlyph = new() { FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14, Text = "" };
    readonly TextBlock positionLabel = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI") };
    readonly Grid stage = new();

    /// Known once `LoadAsync` has read the file.
    public double Duration { get; private set; }
    public bool IsAudio { get; private set; }
    public DetentSlider Start { get; }
    public DetentSlider End { get; }
    readonly TextBlock startLabel = new() { MinWidth = 64 }, endLabel = new() { MinWidth = 64 }, lengthLabel = ToolUI.Scaled(new TextBlock { FontWeight = FontWeights.SemiBold }, 13);
    double lastStart, lastEnd;
    /// The handles are pushed together at the minimum gap.
    bool pinned;
    bool playing, mediaReady;
    readonly FrameDriver playback = new();
    CancellationTokenSource? stripCancel;

    public TrimEditor(string path) : this(path, new Grid()) { }

    TrimEditor(string path, Grid canvas) : base(FileKinds.Of(path) == FileKind.Audio ? "Trim Audio" : "Trim Video", path, canvas)
    {
        IsAudio = FileKinds.Of(path) == FileKind.Audio;
        canvas.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        canvas.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        canvas.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        stage.Children.Add(player);
        stage.Children.Add(wave);
        stage.Children.Add(unavailable);
        unavailable.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        player.Margin = new Thickness(12, 12, 12, 0);
        Grid.SetRow(stage, 0);
        Grid.SetRow(strip, 1);
        canvas.Children.Add(stage);
        canvas.Children.Add(strip);
        playButton.Content = playGlyph;
        playButton.ToolTip = L("Play");
        playButton.Click += (_, _) => TogglePlayback();
        var transport = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 8, 12, 10) };
        transport.Children.Add(playButton);
        transport.Children.Add(positionLabel);
        Grid.SetRow(transport, 2);
        canvas.Children.Add(transport);
        positionLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        // Each handle's default is its own end of the clip: an end stop that snaps, never a band that swallows the
        // first or last seconds. The real range arrives with the duration.
        Start = Slider(0, 1, 0);
        End = Slider(0, 1, 1);
        AddRow("Start", Start, startLabel);
        AddRow("End", End, endLabel);
        lengthLabel.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        AddRow("Length", lengthLabel);

        player.MediaOpened += (_, _) =>
        {
            mediaReady = true;
            player.Pause();
            Seek(Start.Value);
        };
        player.MediaFailed += (_, e) =>
        {
            Log.Write($"Trim preview failed: {e.ErrorException?.Message}");
            mediaReady = false;
            ShowUnavailable();
        };
        player.MediaEnded += (_, _) => StopPlayback(seekToStart: false);
        strip.Scrubbed += ScrubTo;
        wave.Scrubbed += ScrubTo;
        playback.OnFrame = _ => Tick();
        Window.PreviewKeyDown += (_, e) =>
        {
            // Space plays and pauses, as in Films & TV, unless a control that uses it has focus.
            if (e.Key == Key.Space && Keyboard.FocusedElement is not (TextBox or System.Windows.Controls.Primitives.ButtonBase) && IsLoaded)
            {
                TogglePlayback();
                e.Handled = true;
            }
        };
    }

    void ShowUnavailable()
    {
        unavailable.Text = L("Preview isn't available for this file.");
        unavailable.Visibility = Visibility.Visible;
        player.Visibility = Visibility.Collapsed;
    }

    protected override async Task LoadAsync()
    {
        var (seconds, audio) = await ReadDuration(Source);
        if (!(seconds > 0)) throw new ToolError(L("This file has no duration"));  // also NaN
        Duration = seconds;
        if (audio != IsAudio)
        {
            IsAudio = audio;
            SetTitle(audio ? "Trim Audio" : "Trim Video");
        }
        foreach (var handle in new[] { Start, End }) handle.Maximum = seconds;
        Start.Value = 0;
        End.Value = seconds;
        End.Neutral = seconds;
        lastStart = 0;
        lastEnd = seconds;
        // Audio shows its waveform in the stage; video shows the picture and a filmstrip under it.
        wave.Visibility = IsAudio ? Visibility.Visible : Visibility.Collapsed;
        strip.Visibility = IsAudio ? Visibility.Collapsed : Visibility.Visible;
        player.Visibility = IsAudio ? Visibility.Collapsed : Visibility.Visible;
        foreach (var s in new[] { strip, wave }) { s.Duration = seconds; s.End = seconds; }
        try { player.Source = new Uri(Source); }
        catch (Exception) { ShowUnavailable(); }
        stripCancel = new CancellationTokenSource();
        _ = FillStrip(stripCancel.Token);
    }

    /// The clip's length in seconds and whether it has no picture.
    public static async Task<(double Seconds, bool Audio)> ReadDuration(string path)
    {
        var file = await WinStorage.StorageFile.GetFileFromPathAsync(path);
        double seconds = 0;
        bool audio = FileKinds.Of(path) == FileKind.Audio;
        try
        {
            var video = await file.Properties.GetVideoPropertiesAsync();
            bool hasPicture = video.Width > 0 && video.Height > 0;
            if (FileKinds.Of(path) != FileKind.Audio || hasPicture) audio = !hasPicture;
            seconds = video.Duration.TotalSeconds;
            if (seconds <= 0) seconds = (await file.Properties.GetMusicPropertiesAsync()).Duration.TotalSeconds;
        }
        catch (Exception) { /* Read by Media Foundation below. */ }
        if (seconds <= 0)
        {
            // Formats the shell doesn't describe (some MKV / WebM / FLAC): ask Media Foundation.
            try
            {
                using var media = global::Windows.Media.Core.MediaSource.CreateFromStorageFile(file);
                await media.OpenAsync();
                seconds = media.Duration?.TotalSeconds ?? 0;
            }
            catch (Exception) { }
        }
        return (seconds, audio);
    }

    async Task FillStrip(CancellationToken token)
    {
        try
        {
            if (IsAudio)
            {
                var peaks = await Waveform(Source, 400);
                if (token.IsCancellationRequested) return;
                wave.Peaks = peaks;
                wave.InvalidateVisual();
            }
            else
            {
                var thumbs = await Filmstrip(Source, Duration, 10, 120);
                if (token.IsCancellationRequested) return;
                strip.Thumbnails = thumbs;
                strip.InvalidateVisual();
            }
        }
        catch (Exception ex) { Log.Write($"Trim strip failed: {ex.Message}"); }
    }

    /// Frames evenly spaced over the clip (Windows.Media.Editing thumbnails), about `height` pixels tall.
    public static async Task<List<BitmapSource>> Filmstrip(string path, double duration, int count, int height)
    {
        var file = await WinStorage.StorageFile.GetFileFromPathAsync(path);
        var clip = await WinEditing.MediaClip.CreateFromFileAsync(file);
        var composition = new WinEditing.MediaComposition();
        composition.Clips.Add(clip);
        var props = clip.GetVideoEncodingProperties();
        double aspect = props.Height > 0 ? (double)props.Width / props.Height : 16.0 / 9;
        int width = Math.Max(1, (int)Math.Round(height * aspect));
        var times = Enumerable.Range(0, count).Select(i => TimeSpan.FromSeconds(duration * (i + 0.5) / count)).ToList();
        var streams = await composition.GetThumbnailsAsync(times, width, height, WinEditing.VideoFramePrecision.NearestKeyFrame);
        var result = new List<BitmapSource>();
        foreach (var stream in streams)
        {
            using var managed = stream.AsStreamForRead();
            var memory = new MemoryStream();
            await managed.CopyToAsync(memory);
            memory.Position = 0;
            var frame = BitmapDecoder.Create(memory, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            frame.Freeze();
            result.Add(frame);
        }
        return result;
    }

    /// Peak levels (0–1) in `buckets` slices, from the audio decoded to 8 kHz mono PCM in a temporary WAV.
    public static async Task<float[]> Waveform(string path, int buckets)
    {
        var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"Swivel-wave-{Guid.NewGuid():N}.wav");
        try
        {
            var source = await WinStorage.StorageFile.GetFileFromPathAsync(path);
            File.WriteAllBytes(temp, []);
            var target = await WinStorage.StorageFile.GetFileFromPathAsync(temp);
            var profile = WinProfiles.MediaEncodingProfile.CreateWav(WinProfiles.AudioEncodingQuality.Low);
            profile.Audio = WinProfiles.AudioEncodingProperties.CreatePcm(8000, 1, 16);
            var transcoder = new WinTranscoding.MediaTranscoder();
            var prepared = await transcoder.PrepareFileTranscodeAsync(source, target, profile);
            if (!prepared.CanTranscode) return [];
            await prepared.TranscodeAsync();
            return Peaks(File.ReadAllBytes(temp), buckets);
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { }
        }
    }

    /// Peaks of 16-bit PCM in a WAV file's data chunk.
    public static float[] Peaks(byte[] wav, int buckets)
    {
        int offset = 12, dataStart = -1, dataLength = 0;
        while (offset + 8 <= wav.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(wav, offset, 4);
            int size = BitConverter.ToInt32(wav, offset + 4);
            if (id == "data")
            {
                dataStart = offset + 8;
                dataLength = Math.Min(size < 0 ? int.MaxValue : size, wav.Length - dataStart);
                break;
            }
            offset += 8 + size + (size & 1);
        }
        if (dataStart < 0 || dataLength < 2) return [];
        int samples = dataLength / 2;
        var peaks = new float[buckets];
        for (int b = 0; b < buckets; b++)
        {
            int from = (int)((long)samples * b / buckets), to = Math.Max(from + 1, (int)((long)samples * (b + 1) / buckets));
            int peak = 0;
            for (int s = from; s < to && s < samples; s++) peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(wav, dataStart + s * 2)));
            peaks[b] = peak / 32768f;
        }
        float max = peaks.DefaultIfEmpty(0).Max();
        if (max > 0) for (int i = 0; i < peaks.Length; i++) peaks[i] = (float)Math.Sqrt(peaks[i] / max);
        return peaks;
    }

    public static string Time(double seconds)
    {
        double s = Math.Max(seconds, 0);
        int whole = (int)s;
        return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}.{2}", whole / 60, whole % 60, (int)(s * 10 % 10));
    }

    protected override void Update()
    {
        // Keep at least a tenth of a second between the handles, and preview whichever one moved.
        bool startMoved = Start.Value != lastStart, endMoved = End.Value != lastEnd;
        bool meeting = false;
        if (End.Value < Start.Value + 0.1)
        {
            if (startMoved) Start.Value = Math.Max(0, End.Value - 0.1);
            else End.Value = Math.Min(Duration, Start.Value + 0.1);
            meeting = true;
        }
        // The handles meeting is a hard stop: one snap as they touch, none while held there.
        if (meeting && !pinned && IsTracking) Feedback.Snap();
        pinned = meeting;
        lastStart = Start.Value;
        lastEnd = End.Value;
        startLabel.Text = Time(Start.Value);
        endLabel.Text = Time(End.Value);
        lengthLabel.Text = Time(End.Value - Start.Value);
        foreach (var s in new[] { strip, wave })
        {
            s.Start = Start.Value;
            s.End = End.Value;
            s.InvalidateVisual();
        }
        if (playing) StopPlayback(seekToStart: false);
        // Opening (or Reset) shows the first frame of the cut, not its last.
        Seek(endMoved && !startMoved ? End.Value : Start.Value);
    }

    protected override void TrackingEnded() => Seek(Playhead);

    double Playhead => strip.Playhead;

    /// Moves the picture to `seconds`. MediaElement keeps only the newest position while a seek is in flight, so
    /// scrubbing never queues up (what Apple's QA1820 chase loop does on the Mac).
    void Seek(double seconds)
    {
        seconds = Math.Clamp(seconds, 0, Math.Max(Duration, 0));
        foreach (var s in new[] { strip, wave })
        {
            s.Playhead = seconds;
            s.InvalidateVisual();
        }
        positionLabel.Text = $"{Time(seconds)} / {Time(Duration)}";
        if (mediaReady) player.Position = TimeSpan.FromSeconds(seconds);
    }

    void ScrubTo(double seconds)
    {
        if (playing) StopPlayback(seekToStart: false);
        Seek(Math.Clamp(seconds, Start.Value, End.Value));
    }

    void TogglePlayback()
    {
        if (!IsLoaded || !mediaReady) return;
        if (playing)
        {
            StopPlayback(seekToStart: false);
            return;
        }
        // Play starts from the beginning of the cut when the playhead is outside it (or sitting at its end).
        double now = player.Position.TotalSeconds;
        if (now < Start.Value - 0.05 || now >= End.Value - 0.05) player.Position = TimeSpan.FromSeconds(Start.Value);
        player.Play();
        playing = true;
        playGlyph.Text = "";
        playButton.ToolTip = L("Pause");
        playback.Start();
    }

    void StopPlayback(bool seekToStart)
    {
        if (!playing) return;
        player.Pause();
        playing = false;
        playGlyph.Text = "";
        playButton.ToolTip = L("Play");
        playback.Stop();
        if (seekToStart) Seek(Start.Value);
    }

    /// While playing: follow the playhead and stop at the end of the cut.
    bool Tick()
    {
        if (!playing) return false;
        double now = player.Position.TotalSeconds;
        if (now >= End.Value)
        {
            player.Pause();
            playing = false;
            playGlyph.Text = "";
            playButton.ToolTip = L("Play");
            Seek(End.Value);
            return false;
        }
        foreach (var s in new[] { strip, wave })
        {
            s.Playhead = now;
            s.InvalidateVisual();
        }
        positionLabel.Text = $"{Time(now)} / {Time(Duration)}";
        return true;
    }

    protected override void Reset()
    {
        Start.Value = 0;
        End.Value = Duration;
    }

    public override void DidClose()
    {
        stripCancel?.Cancel();
        playback.Stop();
        try
        {
            player.Stop();
            player.Close();
        }
        catch (Exception) { }
    }

    protected override Task<List<string>> ApplyAsync()
    {
        StopPlayback(seekToStart: false);
        double start = Start.Value, end = End.Value;
        return Trim(Source, start, end, IsAudio);
    }

    /// Writes "name trimmed.m4a" (audio) or "name trimmed.mp4" / ".m4v" (video) holding `start`…`end` seconds.
    public static async Task<List<string>> Trim(string source, double start, double end, bool audio)
    {
        var ext = FileKinds.Ext(source);
        string outExt = audio ? "m4a" : ext == "m4v" ? "m4v" : "mp4";
        var output = Paths.Output(source, outExt, " trimmed");
        var folderPath = System.IO.Path.GetDirectoryName(output) ?? "";
        var tempName = $".{System.IO.Path.GetFileNameWithoutExtension(output)}.{Guid.NewGuid():N}.{outExt}";
        var tempPath = System.IO.Path.Combine(folderPath, tempName);
        try
        {
            var input = await WinStorage.StorageFile.GetFileFromPathAsync(source);
            var folder = await WinStorage.StorageFolder.GetFolderFromPathAsync(folderPath);
            var target = await folder.CreateFileAsync(tempName, WinStorage.CreationCollisionOption.FailIfExists);
            if (audio)
            {
                var transcoder = new WinTranscoding.MediaTranscoder
                {
                    TrimStartTime = TimeSpan.FromSeconds(start),
                    TrimStopTime = TimeSpan.FromSeconds(end),
                };
                var profile = WinProfiles.MediaEncodingProfile.CreateM4a(WinProfiles.AudioEncodingQuality.High);
                var prepared = await transcoder.PrepareFileTranscodeAsync(input, target, profile);
                if (!prepared.CanTranscode) throw new ToolError(L("Can't trim this file"));
                await prepared.TranscodeAsync();
            }
            else
            {
                WinEditing.MediaClip clip;
                try { clip = await WinEditing.MediaClip.CreateFromFileAsync(input); }
                catch (Exception) { throw new ToolError(L("Can't trim this file")); }
                clip.TrimTimeFromStart = TimeSpan.FromSeconds(start);
                clip.TrimTimeFromEnd = clip.OriginalDuration - TimeSpan.FromSeconds(end) is var tail && tail > TimeSpan.Zero ? tail : TimeSpan.Zero;
                var composition = new WinEditing.MediaComposition();
                composition.Clips.Add(clip);
                // Keep the source's size and frame rate where Windows can encode them.
                var profile = WinProfiles.MediaEncodingProfile.CreateMp4(WinProfiles.VideoEncodingQuality.HD1080p);
                var props = clip.GetVideoEncodingProperties();
                if (props.Width > 0 && props.Height > 0)
                {
                    profile.Video.Width = props.Width;
                    profile.Video.Height = props.Height;
                    if (props.FrameRate.Numerator > 0 && props.FrameRate.Denominator > 0)
                    {
                        profile.Video.FrameRate.Numerator = props.FrameRate.Numerator;
                        profile.Video.FrameRate.Denominator = props.FrameRate.Denominator;
                    }
                    if (props.Bitrate > 0) profile.Video.Bitrate = props.Bitrate;
                }
                var result = await composition.RenderToFileAsync(target, WinEditing.MediaTrimmingPreference.Precise, profile);
                if (result != WinTranscoding.TranscodeFailureReason.None) throw new ToolError(L("Can't trim this file"));
            }
            File.Move(tempPath, output, overwrite: false);
            return [output];
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch (IOException) { }
        }
    }
}
