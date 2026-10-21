using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VBFileSystem = Microsoft.VisualBasic.FileIO.FileSystem;

namespace Swivel;

/// A rope along the top of the screen.
///
/// New screenshots first appear as a card in the bottom-right corner, as Windows shows them. Drag the card up (or
/// click it) to hang it on the rope; leave it and it slides away (the file stays in the Screenshots folder). Moving
/// the pointer to the top edge of the screen, or pressing Ctrl + Alt + T, lowers the rope, and every print can be
/// clicked (copy), double-clicked (open), held (preview), right-clicked or dragged away.
///
/// The Mac app moves screenshots into a rope folder and hangs copies of other files; here the rope is a list of
/// references (clothesline.json), and only copied images that exist nowhere else are saved, into
/// %APPDATA%\Swivel\Clothesline.
sealed class Clothesline : IRopeHost
{
    public static Clothesline Shared { get; } = new();

    public static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "png", "jpg", "jpeg", "heic", "tiff", "tif", "gif", "webp", "bmp", "mov", "mp4" };

    public static bool IsMedia(string path) => MediaExtensions.Contains(FileKinds.Ext(path));

    /// %APPDATA%\Swivel\clothesline.json (tests point it elsewhere).
    public string StoreFile { get; set; } = System.IO.Path.Combine(Prefs.AppFolder, "clothesline.json");

    /// Where copied images are saved before they hang (%APPDATA%\Swivel\Clothesline).
    public string CopiesFolder { get; set; } = System.IO.Path.Combine(Prefs.AppFolder, "Clothesline");

    /// The folder screenshots are saved to and copied images are put away in when not hung.
    public string ScreenshotsFolder => ScreenshotWatcher.DefaultFolder;

    List<RopeEntry> entries = [];
    bool loaded;
    RopePanel? panel;
    RopeMessages? messages;
    ScreenshotWatcher? screenshots;
    DispatcherTimer? poll;
    readonly TopEdgeTrigger edge = new();
    Native.POINT? pressedAt;               // where the current click started, for telling a click from a drag
    bool wasDown;
    DateTime? leftSince;
    bool pinned;                           // shown with the hotkey: stays until toggled or clicked away
    uint ownClipboard;                     // the clipboard sequence number of our own copy
    DateTime lastScreenshot = DateTime.MinValue;
    string? lastCopiedHash;
    DispatcherTimer? clipboardDelay;
    HashSet<string> seenIncoming = new(StringComparer.OrdinalIgnoreCase);
    /// Files dropped on the rope whose hanging runs on the next dispatcher turn, so the card can tell it was hung.
    readonly HashSet<string> taking = new(StringComparer.OrdinalIgnoreCase);
    DateTime lastExistenceCheck;
    bool started;

    public RopePanel Panel => panel ??= CreatePanel();

    RopePanel CreatePanel()
    {
        var p = new RopePanel();
        p.Rope.Host = this;
        return p;
    }

    public IReadOnlyList<RopeEntry> Entries { get { EnsureLoaded(); return entries; } }

    /// A print is being dragged off the rope (or its context menu is open): the rope stays down.
    bool IsDraggingItem => panel?.Rope.IsDraggingItem == true;

    /// Something is being dragged toward the rope.
    public bool IsReceiving { get; private set; }

    void EnsureLoaded()
    {
        if (loaded) return;
        loaded = true;
        entries = RopeStore.Load(StoreFile);
    }

    /// Starts the screenshot watcher, the top-edge watcher and the Ctrl + Alt + T hotkey (when the feature is on).
    public void Start()
    {
        if (started) return;
        started = true;
        EnsureLoaded();
        Reload();
        messages = new RopeMessages
        {
            HotKeyPressed = Toggle,
            ClipboardChanged = ClipboardChanged,
        };
        // The pointer is only watched while the feature is on, wherever the setting gets changed.
        Prefs.Changed += OnPrefsChanged;
        SyncWatching();
    }

    void OnPrefsChanged()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        if (dispatcher.CheckAccess()) SyncWatching(); else dispatcher.BeginInvoke(SyncWatching);
    }

    /// Turning the feature off puts the rope and any waiting card away and stops hanging copies and screenshots.
    public void SetEnabled(bool on)
    {
        if (!on) Hide();
        Prefs.ClotheslineEnabled = on;   // raises Prefs.Changed, which starts or stops watching
        if (!on) IncomingCard.Shared.PutAwayAll(animated: true);
        SyncWatching();
    }

    /// Stops watchers and saves state (on quit). A card still up, or one queued behind a drag, is released.
    public void Stop()
    {
        if (!started) return;
        started = false;
        Prefs.Changed -= OnPrefsChanged;
        StopWatching();
        messages?.Dispose();
        messages = null;
        IncomingCard.Shared.PutAwayAll(animated: false);
        Save();
        panel?.Close();
        panel = null;
    }

    // MARK: Watching

    /// The edge poll, the hotkey, the clipboard listener and the Screenshots watcher run only while the clothesline
    /// is on, so a switched-off tool doesn't wake Swivel for every pointer move.
    void SyncWatching()
    {
        if (!started) return;
        bool on = Prefs.ClotheslineEnabled;
        if (on && poll is null) StartWatching();
        else if (!on && poll != null)
        {
            StopWatching();
            if (IsShown && !Panel.Rope.IsLifting) Hide();
        }
        if (!on) return;
        messages?.SetHotKey(true);
        bool copies = Prefs.ClotheslineHangCopied;
        if (copies && !clipboardOn) ownClipboard = RopeNative.GetClipboardSequenceNumber();  // what was copied before isn't hung now
        clipboardOn = copies;
        messages?.SetClipboard(copies);
        if (Prefs.ClotheslineTakeover)
        {
            if (screenshots is null || !string.Equals(screenshots.Folder, ScreenshotsFolder, StringComparison.OrdinalIgnoreCase))
            {
                screenshots?.Dispose();
                screenshots = new ScreenshotWatcher(ScreenshotsFolder);
                screenshots.Arrived += files => Application.Current?.Dispatcher.BeginInvoke(() => CheckIncoming(files));
                screenshots.Start();
            }
        }
        else if (screenshots != null)
        {
            screenshots.Dispose();
            screenshots = null;
        }
    }

    bool clipboardOn;

    void StartWatching()
    {
        edge.Reset();
        poll = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(50) };
        poll.Tick += (_, _) => Tick();
        poll.Start();
    }

    void StopWatching()
    {
        poll?.Stop();
        poll = null;
        edge.Reset();
        pressedAt = null;
        messages?.SetHotKey(false);
        messages?.SetClipboard(false);
        clipboardOn = false;
        clipboardDelay?.Stop();
        screenshots?.Dispose();
        screenshots = null;
    }

    // MARK: Contents

    void Save()
    {
        if (loaded) RopeStore.Save(StoreFile, entries);
    }

    /// Prints whose files have gone (deleted or moved elsewhere) leave the rope.
    public void Reload()
    {
        EnsureLoaded();
        int before = entries.Count;
        entries = entries.Where(e => File.Exists(e.Path)).ToList();
        if (entries.Count != before) Save();
        if (panel != null || entries.Count > 0) Panel.Rope.SetItems(entries.Select(e => e.Path).ToList());
        if (entries.Count == 0 && !IsReceiving) Hide();
    }

    RopeEntry? Entry(string path) => entries.FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));

    /// Puts files on the rope, newest at the front. Returns what was hung. `thumbnails` (from the card) saves
    /// decoding the image again; `owned` marks the clothesline's own files (see `RopeEntry`).
    public List<string> Receive(IReadOnlyList<string> paths, RopeArrival arrival = RopeArrival.FromAbove,
                                IReadOnlyDictionary<string, BitmapSource>? thumbnails = null, bool owned = false)
    {
        EnsureLoaded();
        var landed = new List<string>();
        foreach (var raw in paths)
        {
            string path;
            try { path = System.IO.Path.GetFullPath(raw); } catch (Exception) { continue; }
            if (!IsMedia(path) || !File.Exists(path)) continue;
            var existing = Entry(path);
            if (existing != null) entries.Remove(existing);
            else Panel.Rope.MarkNew(path, arrival, thumbnails != null && thumbnails.TryGetValue(raw, out var t) ? t : null);
            entries.Insert(0, new RopeEntry(path, owned || existing?.Owned == true, DateTime.Now));
            landed.Add(path);
        }
        foreach (var p in paths) taking.Remove(p);
        Save();
        Reload();
        if (landed.Count == 0)
        {
            Feedback.Refuse();
            return [];
        }
        Show(force: true);
        if (!pinned) leftSince = DateTime.Now.AddSeconds(2.2);  // stay down long enough to see it land
        return landed;
    }

    /// From the Actions menu.
    public void Hang(IReadOnlyList<string> paths)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess()) dispatcher.Invoke(() => Receive(paths));
        else Receive(paths);
    }

    void IRopeHost.Receive(IReadOnlyList<string> paths) =>
        Receive(paths, RopeArrival.InPlace, IncomingCard.Shared.Thumbnails(paths), owned: paths.Any(p => Same(p, IncomingCard.Shared.Path)));

    /// The rope accepted a drop of these files; hanging them follows on the next dispatcher turn.
    public void Take(IReadOnlyList<string> paths) { foreach (var p in paths) taking.Add(p); }

    public bool IsTaking(string path) => taking.Contains(path);

    static bool Same(string? a, string? b) => a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // MARK: Screenshots and copied images

    void CheckIncoming(IReadOnlyList<string> files)
    {
        lastScreenshot = DateTime.Now;
        if (!Prefs.ClotheslineEnabled || !Prefs.ClotheslineTakeover) return;
        var decision = Triage(files, seenIncoming, showCard: true);
        seenIncoming = decision.Seen;
        // Windows saves screenshots where they belong already, so files the card can't show simply stay there.
        if (decision.Card is { } newest) IncomingCard.Shared.Present(newest);
    }

    /// What one look at the new files (newest first) decides. The card shows one file at a time, so the newest new
    /// file gets it and every other new file stays where it is (two monitors can write two files at once); with the
    /// clothesline off, they all do. A port of the Mac's `Clothesline.triage`.
    public static (string? Card, List<string> PutAway, HashSet<string> Seen) Triage(IReadOnlyList<string> waiting, IReadOnlySet<string> seen, bool showCard)
    {
        var fresh = waiting.Where(p => !seen.Contains(System.IO.Path.GetFileName(p))).ToList();
        var card = showCard ? fresh.FirstOrDefault() : null;
        var putAway = fresh.Skip(card is null ? 0 : 1).ToList();
        var nowSeen = new HashSet<string>(seen, StringComparer.OrdinalIgnoreCase);
        foreach (var p in waiting) nowSeen.Add(System.IO.Path.GetFileName(p));
        return (card, putAway, nowSeen);
    }

    /// A card's file that wasn't hung: a copied image (saved only in Swivel's folder) goes into the Screenshots
    /// folder, as the Mac puts it where screenshots go; anything else is already where it belongs. If that fails,
    /// it hangs on the rope instead, so it's never left behind unseen. Returns where the file is now.
    public string? Release(string path)
    {
        if (!IsInCopies(path) || !File.Exists(path)) return File.Exists(path) ? path : null;
        if (Entry(path) != null) return path;   // it hangs on the rope
        var folder = ScreenshotsFolder;
        var saved = Save(path, folder, screenshots);
        if (saved != null) return saved;
        // The Screenshots folder is missing or read-only: hang it rather than strand it.
        EnsureLoaded();
        entries.Insert(0, new RopeEntry(path, true, DateTime.Now));
        Save();
        Reload();
        return path;
    }

    /// Moves a file into `folder` without overwriting anything (never into its own folder). Returns where it went,
    /// or null if that didn't work. The watcher, if any, is told first so the move isn't a new screenshot.
    public static string? Save(string path, string folder, ScreenshotWatcher? watcher = null)
    {
        try
        {
            if (!Directory.Exists(folder)) return null;
            var home = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) ?? "";
            if (string.Equals(System.IO.Path.GetFullPath(folder).TrimEnd('\\'), home.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return null;
            var destination = Paths.Unique(System.IO.Path.Combine(folder, System.IO.Path.GetFileName(path)));
            watcher?.Ignore(destination);
            File.Move(path, destination);
            return destination;
        }
        catch (Exception ex)
        {
            Log.Write($"Clothesline: can't move {path} to {folder}: {ex.Message}");
            return null;
        }
    }

    bool IsInCopies(string path)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) ?? "";
            return string.Equals(dir.TrimEnd('\\'), System.IO.Path.GetFullPath(CopiesFolder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }

    /// A card's file was dragged into a folder, where File Explorer copied it: Swivel's own saved copy of a copied
    /// image is not needed any more (on the Mac the drag moves it), so it goes to the Recycle Bin.
    public void CopiedAway(string path)
    {
        if (IsInCopies(path) && Entry(path) is null) Recycle(path, _ => { });
    }

    void ClipboardChanged()
    {
        if (!Prefs.ClotheslineEnabled || !Prefs.ClotheslineHangCopied) return;
        uint sequence = RopeNative.GetClipboardSequenceNumber();
        if (sequence == ownClipboard) return;   // our own copy
        var changedAt = DateTime.Now;
        clipboardDelay?.Stop();
        // Win + Print Screen copies the picture and saves it: give the Screenshots watcher time to report the file,
        // so the same screenshot doesn't come up twice.
        var wait = Prefs.ClotheslineTakeover ? TimeSpan.FromMilliseconds(1500) : TimeSpan.FromMilliseconds(150);
        clipboardDelay = new DispatcherTimer(DispatcherPriority.Background) { Interval = wait };
        clipboardDelay.Tick += (_, _) =>
        {
            clipboardDelay?.Stop();
            if (RopeNative.GetClipboardSequenceNumber() != sequence) return;   // changed again; that change decides
            if (Prefs.ClotheslineTakeover && lastScreenshot > changedAt.AddSeconds(-2)) return;
            CheckClipboard();
        };
        clipboardDelay.Start();
    }

    /// Only bare images: anything copied with text or files is skipped, and so is anything marked private (the
    /// Windows counterparts of the Mac's concealed and transient pasteboard types).
    void CheckClipboard()
    {
        try
        {
            var data = Clipboard.GetDataObject();
            if (data is null) return;
            var formats = data.GetFormats(false);
            string[] skip = [DataFormats.Text, DataFormats.UnicodeText, DataFormats.FileDrop, DataFormats.Html, DataFormats.Rtf,
                             "ExcludeClipboardContentFromMonitorProcessing", "Clipboard Viewer Ignore", "UniformResourceLocatorW"];
            if (formats.Any(f => skip.Contains(f, StringComparer.OrdinalIgnoreCase))) return;
            if (formats.Contains("CanIncludeInClipboardHistory") && data.GetData("CanIncludeInClipboardHistory") is MemoryStream history
                && history.Length >= 4 && BitConverter.ToInt32(history.ToArray(), 0) == 0) return;
            BitmapSource? image = null;
            if (formats.Contains("PNG") && data.GetData("PNG") is MemoryStream png)
            {
                png.Position = 0;
                image = BitmapFrame.Create(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            }
            else if (data.GetDataPresent(DataFormats.Bitmap)) image = Clipboard.GetImage();
            if (image is null || image.PixelWidth < 2 || image.PixelHeight < 2) return;
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var buffer = new MemoryStream();
            encoder.Save(buffer);
            var bytes = buffer.ToArray();
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
            if (hash == lastCopiedHash) return;   // the same picture put on the clipboard again
            lastCopiedHash = hash;
            Directory.CreateDirectory(CopiesFolder);
            var name = $"Copied {DateTime.Now:yyyy-MM-dd} at {DateTime.Now:HH.mm.ss}.png";
            var file = Paths.Unique(System.IO.Path.Combine(CopiesFolder, name));
            var temp = file + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, file);
            // Shows up as a card, like a screenshot.
            IncomingCard.Shared.Present(file);
        }
        catch (Exception ex) { Log.Write($"Clothesline clipboard: {ex.Message}"); }
    }

    // MARK: Showing and hiding

    public bool IsShown => panel?.IsVisible == true;

    /// Lowers or raises the rope. A rope shown this way (Ctrl + Alt + T) stays until you click elsewhere.
    public void Toggle()
    {
        // Caught while it's still lifting, the shortcut brings it back instead of hiding it again.
        if (IsShown && !Panel.Rope.IsLifting) Hide();
        else
        {
            pinned = true;
            Show();
        }
    }

    /// Lowers the rope even when it is empty, and keeps it down until you click elsewhere (Quick Actions, Settings;
    /// the Mac app's `show(force: true)`). Unlike Toggle, it never raises a rope that is already down.
    public void Reveal()
    {
        pinned = true;
        Show(force: true);
    }

    public void Show(bool force = false)
    {
        EnsureLoaded();
        if (!Prefs.ClotheslineEnabled || (!force && entries.Count == 0)) return;
        if (!force) Reload();
        if (!force && entries.Count == 0) return;
        Panel.Lower(RopeNative.MonitorUnderPointer());
    }

    public void Hide()
    {
        pinned = false;
        leftSince = null;
        panel?.Raise();
    }

    /// A card is being dragged: lower the rope and keep it down until the drag ends.
    public void BeginReceiving()
    {
        IsReceiving = true;
        Panel.Rope.IsReceiving = true;
        Show(force: true);
    }

    public void EndReceiving()
    {
        IsReceiving = false;
        if (panel != null) panel.Rope.IsReceiving = false;
        // A drop may already have asked to stay down for the landing; don't cut that short.
        var now = DateTime.Now;
        leftSince = leftSince is { } l && l > now ? l : now;
    }

    /// The top band: from the top of the monitor to just above the cord (the Windows stand-in for the Mac's menu
    /// bar; with the taskbar along the top it includes the taskbar).
    static bool InTopBand(Native.POINT p, RopeMonitor monitor) => p.Y < monitor.Work.Y + 8 * monitor.Scale;

    void Tick()
    {
        if (!Prefs.ClotheslineEnabled)
        {
            edge.Reset();
            if (IsShown) Hide();
            return;
        }
        // 50 ms while the rope is up (the top-edge touch); every frame or so while it's down, so arriving on a print
        // lights it (and lets it catch the mouse) without a noticeable wait.
        var interval = TimeSpan.FromMilliseconds(IsShown ? 16 : 50);
        if (poll != null && poll.Interval != interval) poll.Interval = interval;
        var point = Native.CursorPixels();
        bool down = Native.PrimaryButtonDown();
        var monitor = RopeNative.MonitorAt(point);
        // Touching the top edge reveals immediately. Moving away rearms it.
        bool atTop = point.Y < monitor.Bounds.Y + TopEdgeTrigger.EdgePixels;
        if (edge.Update(atTop, down, canShow: !IsShown || Panel.Rope.IsLifting)) Show();

        if (!IsShown || panel is null) { wasDown = down; pressedAt = null; return; }
        var rope = panel.Rope;
        // The poll also catches what mouse events don't see, such as the pointer arriving on a print while the strip
        // lets clicks through, or a drag from another app passing over a print.
        rope.PointerMoved();
        bool sameMonitor = panel.Monitor.Contains(point);
        var view = panel.ToView(point);
        bool inTopBand = sameMonitor && InTopBand(point, panel.Monitor);
        bool overPrint = sameMonitor && rope.IsOverPrint(view);

        // Files deleted or moved elsewhere leave the rope (checked once a second while it's down).
        if (DateTime.Now - lastExistenceCheck > TimeSpan.FromSeconds(1))
        {
            lastExistenceCheck = DateTime.Now;
            if (entries.Any(e => !File.Exists(e.Path))) Reload();
            if (!IsShown) { wasDown = down; return; }
        }

        bool busy = IsDraggingItem || IsReceiving || rope.IsLifting || IncomingCard.Shared.IsDragging;
        // Pressing in the top band puts the rope away at once. A pinned rope also goes when the user clicks away from
        // the prints, on the release, and only for a click: a drag that starts elsewhere may be bringing a file to it.
        // A click on the screenshot card is the card's own (it hangs the screenshot).
        if (down != wasDown)
        {
            wasDown = down;
            bool onCard = IncomingCard.Shared.Owns(RopeNative.TopWindowAt(point));
            if (busy || onCard) pressedAt = null;
            else if (down)
            {
                pressedAt = point;
                if (inTopBand && !overPrint) { Hide(); return; }
            }
            else if (pressedAt is { } start)
            {
                pressedAt = null;
                double moved = Math.Sqrt(Math.Pow(point.X - start.X, 2) + Math.Pow(point.Y - start.Y, 2)) / panel.Scale;
                if (pinned && moved < 5 && !(panel.Monitor.Contains(start) && rope.IsNear(panel.ToView(start)))) { Hide(); return; }
            }
        }

        if (busy) return;
        if (pinned) return;
        bool inStrip = sameMonitor && view.Y <= RopeView.PanelHeight + 8;
        var delay = RopeTuning.HideDelay(overPrint, inTopBand, sameMonitor && rope.IsNear(view), inStrip);
        if (delay is null)
        {
            if (leftSince is { } left && left > DateTime.Now) return;  // still showing off a new arrival
            leftSince = null;
            return;
        }
        leftSince ??= DateTime.Now;
        if ((DateTime.Now - leftSince.Value).TotalSeconds > delay.Value) Hide();
    }

    // MARK: Item actions (IRopeHost)

    public void Copy(string path)
    {
        try
        {
            var data = new DataObject();
            data.SetFileDropList(new System.Collections.Specialized.StringCollection { path });
            if (!RopeThumbnails.IsMovie(path) && RopeThumbnails.Image(path, 16384) is { } image) data.SetImage(image);
            FileActions.RetryClipboard(() => Clipboard.SetDataObject(data, true));
            ownClipboard = RopeNative.GetClipboardSequenceNumber();   // don't hang our own copy again
        }
        catch (Exception ex)
        {
            Log.Write($"Clothesline copy: {ex.Message}");
            Feedback.Refuse();
            return;
        }
        // Confirmed where the user is looking (on the print), with the name at the bottom of the screen.
        panel?.Rope.ConfirmCopy(path);
        Feedback.Done();
        Toast.Shared.Show(L("Copied %@", System.IO.Path.GetFileName(path)), "copy");
    }

    /// Opens with the default app (Photos, Media Player…). The rope leaves a beat later, so the eye catches the
    /// print's pulse first.
    public void Open(string path)
    {
        try { FileActions.Open(path); }
        catch (Exception ex)
        {
            Log.Write($"Clothesline open: {ex.Message}");
            Feedback.Refuse();
        }
        After(80, Hide);
    }

    /// The preview grows out of the print, and the rope lifts once it has started.
    public void Preview(string path)
    {
        var rope = Panel.Rope;
        var picture = rope.ImageOf(path);
        PrintPreview.Show(path, () => rope.FrameOf(path) is { } r && panel is { } p ? p.ToScreen(r) : null, picture);
        After(100, Hide);
    }

    public void ExtractText(string path)
    {
        Hide();
        TextCaptureWindow.Present(path);
    }

    public void Reveal(string path) => FileActions.Reveal([path]);

    public bool CanDiscardFile(string path) => Entry(path)?.Owned == true;

    /// Thrown away: the print leaves the rope at once (falling, or shrinking away if it already went to the Recycle
    /// Bin as a drag). The clothesline's own files go to the Recycle Bin; the user's other files only leave the rope.
    public void Discard(string path, bool fall)
    {
        var rope = Panel.Rope;
        rope.Remove(path, fall);
        var entry = Entry(path);
        void Forget()
        {
            entries.RemoveAll(e => Same(e.Path, path));
            Save();
            rope.Departed(path);
            Reload();
        }
        if (entry?.Owned == true && File.Exists(path))
            Recycle(path, ok =>
            {
                if (ok) Forget();
                else { rope.Departed(path); Reload(); }   // couldn't be thrown away: it hangs again
            });
        else Forget();
    }

    /// A print dragged into a folder comes off the rope (File Explorer copied it there).
    public void TakenDown(string path)
    {
        entries.RemoveAll(e => Same(e.Path, path));
        Save();
        Reload();
        if (IsInCopies(path)) Recycle(path, _ => { });
    }

    /// Sends a file to the Recycle Bin in the background (never deletes it permanently).
    static void Recycle(string path, Action<bool> done)
    {
        var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        Task.Run(() =>
        {
            bool ok;
            try
            {
                VBFileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                ok = !File.Exists(path);
            }
            catch (Exception ex)
            {
                Log.Write($"Clothesline: can't recycle {path}: {ex.Message}");
                ok = false;
            }
            dispatcher.BeginInvoke(() => done(ok));
        });
    }

    static void After(int milliseconds, Action action)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); action(); };
        timer.Start();
    }

    // MARK: For tests

    /// Points the clothesline at other files and forgets what it loaded.
    internal void UseFiles(string storeFile, string copiesFolder)
    {
        StoreFile = storeFile;
        CopiesFolder = copiesFolder;
        loaded = false;
        entries = [];
    }

    /// Records hung prints without showing anything (tests have no rope on screen).
    internal void AddEntriesForTest(IEnumerable<RopeEntry> list)
    {
        EnsureLoaded();
        entries.InsertRange(0, list);
        Save();
    }

    internal void ReloadEntriesForTest()
    {
        loaded = false;
        EnsureLoaded();
    }
}
