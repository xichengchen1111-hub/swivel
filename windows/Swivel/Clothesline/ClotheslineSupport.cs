using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Swivel;

// MARK: - Hotkey and clipboard messages

/// A message-only window for the Ctrl + Alt + T hotkey (RegisterHotKey, the counterpart of the Mac's Carbon hotkey:
/// one shortcut, no keyboard monitoring) and for clipboard change notices (AddClipboardFormatListener).
sealed class RopeMessages : IDisposable
{
    const int WM_HOTKEY = 0x0312;
    const int WM_CLIPBOARDUPDATE = 0x031D;
    const int HotKeyId = 0x5357;   // "SW"
    const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
    const uint VK_T = 0x54;

    HwndSource? source;
    bool hotKey, clipboard;

    public Action? HotKeyPressed { get; set; }
    public Action? ClipboardChanged { get; set; }

    /// Whether Ctrl + Alt + T is registered (another app may own it).
    public bool HotKeyReady => hotKey;

    HwndSource Source
    {
        get
        {
            if (source != null) return source;
            var parameters = new HwndSourceParameters("Swivel clothesline messages")
            {
                ParentWindow = new IntPtr(-3),   // HWND_MESSAGE
                WindowStyle = 0,
            };
            source = new HwndSource(parameters);
            source.AddHook(Hook);
            return source;
        }
    }

    public void SetHotKey(bool on)
    {
        if (on == hotKey) return;
        if (on)
        {
            hotKey = RopeNative.RegisterHotKey(Source.Handle, HotKeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_T);
            if (!hotKey) Log.Write("Clothesline: Ctrl + Alt + T is already registered by another app.");
        }
        else
        {
            if (source != null) RopeNative.UnregisterHotKey(source.Handle, HotKeyId);
            hotKey = false;
        }
    }

    public void SetClipboard(bool on)
    {
        if (on == clipboard) return;
        if (on) clipboard = RopeNative.AddClipboardFormatListener(Source.Handle);
        else
        {
            if (source != null) RopeNative.RemoveClipboardFormatListener(source.Handle);
            clipboard = false;
        }
    }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotKeyId)
        {
            handled = true;
            HotKeyPressed?.Invoke();
        }
        else if (msg == WM_CLIPBOARDUPDATE)
        {
            handled = true;
            ClipboardChanged?.Invoke();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        SetHotKey(false);
        SetClipboard(false);
        source?.Dispose();
        source = null;
    }
}

// MARK: - Screenshot watcher

/// Watches the Screenshots folder for new pictures and recordings and reports each one once it is completely
/// written. Windows has no "screencapture location" to take over (and Swivel must not change system settings), so
/// screenshots stay where Windows saves them; the clothesline only offers them as a card.
sealed class ScreenshotWatcher : IDisposable
{
    sealed class Pending
    {
        public long Size = -1;
        public DateTime Changed = DateTime.UtcNow;
        public DateTime First = DateTime.UtcNow;
    }

    readonly object gate = new();
    readonly Dictionary<string, Pending> pending = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> done = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> ignored = new(StringComparer.OrdinalIgnoreCase);
    FileSystemWatcher? watcher;
    Timer? timer;
    Timer? retry;
    DateTime startedUtc;
    bool disposed;

    /// Raised on a thread-pool thread with the newly completed files, newest first.
    public event Action<IReadOnlyList<string>>? Arrived;

    public string Folder { get; }

    /// How long a file's size must stay put (and no writer may hold it) before it counts as complete.
    public TimeSpan Settle { get; init; } = TimeSpan.FromMilliseconds(300);

    public ScreenshotWatcher(string folder) => Folder = folder;

    /// Where screenshots are saved: SWIVEL_SCREENSHOTS (tests), else the Screenshots known folder.
    public static string DefaultFolder =>
        Environment.GetEnvironmentVariable("SWIVEL_SCREENSHOTS") is { Length: > 0 } custom ? custom : RopeNative.ScreenshotsFolder();

    public bool IsWatching => watcher != null;

    public void Start()
    {
        lock (gate)
        {
            if (disposed || watcher != null) return;
            startedUtc = DateTime.UtcNow;
            if (!Directory.Exists(Folder))
            {
                // Windows creates the folder with the first screenshot; look again in a while (never create it).
                retry ??= new Timer(_ => { lock (gate) { retry?.Dispose(); retry = null; } Start(); }, null, 5000, Timeout.Infinite);
                return;
            }
            try
            {
                var w = new FileSystemWatcher(Folder)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    IncludeSubdirectories = false,
                    InternalBufferSize = 32 * 1024,
                };
                w.Created += (_, e) => Note(e.FullPath);
                w.Changed += (_, e) => Note(e.FullPath);
                w.Renamed += (_, e) => Note(e.FullPath);   // tools that write a temporary file and rename it
                w.Error += (_, _) => Restart();
                w.EnableRaisingEvents = true;
                watcher = w;
            }
            catch (Exception ex)
            {
                Log.Write($"Clothesline: can't watch {Folder}: {ex.Message}");
                retry ??= new Timer(_ => { lock (gate) { retry?.Dispose(); retry = null; } Start(); }, null, 5000, Timeout.Infinite);
            }
        }
    }

    void Restart()
    {
        lock (gate)
        {
            watcher?.Dispose();
            watcher = null;
        }
        Start();
    }

    public void Stop()
    {
        lock (gate)
        {
            watcher?.Dispose();
            watcher = null;
            timer?.Dispose();
            timer = null;
            retry?.Dispose();
            retry = null;
            pending.Clear();
        }
    }

    /// A file Swivel itself puts into the folder (a copied image being put away) is not a new screenshot.
    public void Ignore(string path)
    {
        lock (gate) { ignored.Add(path); pending.Remove(path); }
    }

    void Note(string path)
    {
        if (!Clothesline.IsMedia(path)) return;
        lock (gate)
        {
            if (disposed || ignored.Contains(path) || done.Contains(path)) return;
            if (!pending.TryGetValue(path, out var p)) pending[path] = p = new Pending();
            p.Changed = DateTime.UtcNow;
            // Screenshots are written in a couple of steps; give the file a moment to settle (the Mac waits 0.35 s).
            timer ??= new Timer(_ => Check(), null, 350, Timeout.Infinite);
        }
    }

    static bool Unlocked(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return stream.Length > 0;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    void Check()
    {
        var complete = new List<string>();
        lock (gate)
        {
            timer?.Dispose();
            timer = null;
            if (disposed) return;
            var now = DateTime.UtcNow;
            foreach (var (path, p) in pending.ToList())
            {
                FileInfo info;
                try { info = new FileInfo(path); } catch (Exception) { pending.Remove(path); continue; }
                if (!info.Exists) { pending.Remove(path); continue; }
                // Old pictures being edited or renamed in the folder aren't new screenshots.
                if (info.CreationTimeUtc < startedUtc - TimeSpan.FromSeconds(5) && info.LastWriteTimeUtc < startedUtc)
                {
                    pending.Remove(path);
                    continue;
                }
                long size = info.Length;
                if (size != p.Size) { p.Size = size; p.Changed = now; continue; }
                if (size > 0 && now - p.Changed >= Settle && Unlocked(path))
                {
                    pending.Remove(path);
                    done.Add(path);
                    complete.Add(path);
                }
                else if (now - p.First > TimeSpan.FromMinutes(2)) pending.Remove(path);   // never finished
            }
            if (pending.Count > 0) timer = new Timer(_ => Check(), null, 150, Timeout.Infinite);
        }
        if (complete.Count == 0) return;
        var newest = complete.OrderByDescending(f => { try { return File.GetCreationTimeUtc(f); } catch (Exception) { return DateTime.MinValue; } })
            .ThenBy(f => System.IO.Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).ToList();
        Arrived?.Invoke(newest);
    }

    public void Dispose()
    {
        Stop();
        lock (gate) disposed = true;
    }
}

// MARK: - Hung prints

/// One print on the rope. `Owned` prints are the clothesline's own (a screenshot hung from its card, or a copied
/// image Swivel saved): on the Mac they live in the rope's folder, so throwing one away sends the file to the
/// Recycle Bin. Anything else (hung from the Actions menu or dropped from File Explorer) is the user's file in its
/// own place; the Mac hangs a copy of it, so here taking it off the rope only forgets it.
sealed record RopeEntry(string Path, bool Owned, DateTime HungAt);

/// The hung prints, newest first, in %APPDATA%\Swivel\clothesline.json.
static class RopeStore
{
    sealed record Document([property: JsonPropertyName("version")] int Version, [property: JsonPropertyName("prints")] List<Item> Prints);

    sealed record Item(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("owned")] bool Owned,
        [property: JsonPropertyName("hungAt")] DateTime HungAt);

    static readonly JsonSerializerOptions options = new() { WriteIndented = true };

    public static List<RopeEntry> Load(string file)
    {
        try
        {
            if (!File.Exists(file)) return [];
            var doc = JsonSerializer.Deserialize<Document>(File.ReadAllText(file), options);
            return doc?.Prints?.Where(i => !string.IsNullOrWhiteSpace(i.Path))
                .Select(i => new RopeEntry(i.Path, i.Owned, i.HungAt))
                .DistinctBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        }
        catch (Exception ex)
        {
            Log.Write($"Clothesline: can't read {file}: {ex.Message}");
            return [];
        }
    }

    /// Written to a temporary file and moved into place, so a failure never leaves half a list.
    public static void Save(string file, IEnumerable<RopeEntry> entries)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var doc = new Document(1, entries.Select(e => new Item(e.Path, e.Owned, e.HungAt)).ToList());
            var temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(doc, options));
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex) { Log.Write($"Clothesline: can't save {file}: {ex.Message}"); }
    }
}

// MARK: - Preview (Windows has no Quick Look)

/// Holding a print opens this: a borderless window over the monitor whose picture grows out of the print and
/// shrinks back into it when closed (or fades, once the rope has gone). Esc, Space, Enter or a click closes it.
/// Recordings play (MediaElement); pictures show at the monitor's resolution.
sealed class PrintPreview : Window
{
    static PrintPreview? current;

    readonly Func<Rect?> sourceFrame;      // the print in screen pixels, while it's still on the rope
    readonly Border content;
    readonly Border backdrop = new() { Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)) };
    readonly Canvas canvas = new();
    readonly FrameDriver driver = new();
    readonly RopeMonitor monitor;
    readonly IntPtr previous;
    readonly MediaElement? media;
    Spring progress = new(0, 0.32, 0.9);   // 0 = on the print, 1 = full size
    Spring alpha = new(0, 0.14, 1);
    Rect from, to;
    bool closing;

    public static void Show(string path, Func<Rect?> sourceFrame, BitmapSource? picture)
    {
        current?.Dismiss();
        var preview = new PrintPreview(path, sourceFrame, picture);
        current = preview;
        preview.Open();
    }

    PrintPreview(string path, Func<Rect?> sourceFrame, BitmapSource? picture)
    {
        this.sourceFrame = sourceFrame;
        previous = RopeNative.GetForegroundWindow();
        monitor = RopeNative.MonitorUnderPointer();
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Title = System.IO.Path.GetFileName(path);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000;
        Top = -32000;

        double scale = monitor.Scale;
        var area = new Size(monitor.Work.Width / scale, monitor.Work.Height / scale);
        FrameworkElement shown;
        Size natural;
        if (RopeThumbnails.IsMovie(path))
        {
            media = new MediaElement { Source = new Uri(path), LoadedBehavior = MediaState.Play, UnloadedBehavior = MediaState.Close, Stretch = Stretch.Uniform };
            natural = picture != null ? new Size(picture.Width, picture.Height) : new Size(16, 9);
            media.MediaOpened += (_, _) =>
            {
                if (media.NaturalVideoWidth <= 0) return;
                Fit(new Size(media.NaturalVideoWidth, media.NaturalVideoHeight), area);
                Render();
            };
            shown = media;
        }
        else
        {
            var image = RopeThumbnails.Image(path, (int)Math.Max(monitor.Work.Width, monitor.Work.Height)) ?? picture;
            natural = image != null ? new Size(image.PixelWidth, image.PixelHeight) : new Size(4, 3);
            shown = new Image { Source = image, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(shown, BitmapScalingMode.HighQuality);
        }
        content = new Border
        {
            Child = shown,
            Background = Brushes.White,
            Padding = new Thickness(0),
            Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Opacity = 0.45, Direction = 270 },
        };
        canvas.Children.Add(content);
        var root = new Grid();
        root.Children.Add(backdrop);
        root.Children.Add(canvas);
        Content = root;
        Fit(natural, area);
        var source = sourceFrame();
        from = source is { } s ? ToLocal(s) : Shrunk(to);

        driver.OnFrame = Advance;
        MouseLeftButtonUp += (_, _) => Dismiss();
        MouseRightButtonUp += (_, _) => Dismiss();
        KeyDown += (_, e) =>
        {
            if (e.Key is Key.Escape or Key.Space or Key.Enter) { e.Handled = true; Dismiss(); }
        };
        Deactivated += (_, _) => Dismiss();
    }

    void Fit(Size natural, Size area)
    {
        double s = Math.Min(Math.Min(area.Width * 0.86 / Math.Max(natural.Width, 1), area.Height * 0.86 / Math.Max(natural.Height, 1)), 1.0 / monitor.Scale);
        var size = new Size(Math.Max(natural.Width * s, 40), Math.Max(natural.Height * s, 30));
        to = new Rect((area.Width - size.Width) / 2, (area.Height - size.Height) / 2, size.Width, size.Height);
    }

    static Rect Shrunk(Rect r) => new(r.X + r.Width * 0.1, r.Y + r.Height * 0.1, r.Width * 0.8, r.Height * 0.8);

    Rect ToLocal(Rect screenPixels) => new((screenPixels.X - monitor.Work.X) / monitor.Scale, (screenPixels.Y - monitor.Work.Y) / monitor.Scale,
        screenPixels.Width / monitor.Scale, screenPixels.Height / monitor.Scale);

    void Open()
    {
        new WindowInteropHelper(this).EnsureHandle();
        RopeNative.Place(this, monitor.Work);
        bool reduced = Motion.Reduced;
        progress.Snap(reduced ? 1 : 0);
        progress.Target = 1;
        alpha.Snap(0);
        alpha.Target = 1;
        Render();
        Show();
        RopeNative.Place(this, monitor.Work);
        Activate();
        Focus();
        driver.Start();
    }

    public void Dismiss()
    {
        if (closing) return;
        closing = true;
        if (current == this) current = null;
        // Back into the print if it still hangs there; otherwise it fades.
        if (sourceFrame() is { } s && !Motion.Reduced) { from = ToLocal(s); progress.Target = 0; }
        alpha.Response = 0.1;
        alpha.Target = 0;
        media?.Pause();
        driver.Start();
    }

    bool Advance(double dt)
    {
        // Windows-only preview window (the Mac uses Quick Look); it moves with the clothesline's Feel & Text tuning.
        progress.Step(dt, RopeTuning.Tuning);
        alpha.Step(dt, RopeTuning.Tuning);
        Render();
        if (closing && alpha.Value < 0.02)
        {
            Close();
            // Give the keyboard back to where the user was, unless they have clicked somewhere else meanwhile.
            if (previous != IntPtr.Zero && RopeNative.GetForegroundWindow() == new WindowInteropHelper(this).Handle) RopeNative.SetForegroundWindow(previous);
            else if (previous != IntPtr.Zero && RopeNative.GetForegroundWindow() == IntPtr.Zero) RopeNative.SetForegroundWindow(previous);
            return false;
        }
        return !(progress.IsSettled && alpha.IsSettled);
    }

    void Render()
    {
        double p = Math.Clamp(progress.Value, -0.1, 1.1);
        var r = new Rect(Mix.Lerp(from.X, to.X, p), Mix.Lerp(from.Y, to.Y, p), Math.Max(Mix.Lerp(from.Width, to.Width, p), 1), Math.Max(Mix.Lerp(from.Height, to.Height, p), 1));
        Canvas.SetLeft(content, r.X);
        Canvas.SetTop(content, r.Y);
        content.Width = r.Width;
        content.Height = r.Height;
        // The picture keeps a little of its print's white border on the way.
        content.Padding = new Thickness(Math.Max(0, 5 * (1 - p)));
        backdrop.Opacity = Math.Clamp(alpha.Value * Math.Clamp(p, 0, 1), 0, 1);
        content.Opacity = Math.Clamp(Math.Max(alpha.Value, closing ? 0 : alpha.Value), 0, 1);
    }

    protected override void OnClosed(EventArgs e)
    {
        driver.Stop();
        base.OnClosed(e);
    }
}
