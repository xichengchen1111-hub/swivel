using System.Diagnostics;
using System.Windows;
using Forms = System.Windows.Forms;

namespace Swivel;

/// The running app: the tray icon and its menu (the Mac app's ◎ menu), the drag watcher and the wheel, the
/// background features, single-instance handling and "open at login".
static class AppHost
{
    const string ReopenEventName = @"Local\Swivel.Reopen";
    const string InstanceMutexName = @"Local\Swivel.Instance";

    static Forms.NotifyIcon? tray;
    static readonly DragWatcher watcher = new();
    static WheelController? wheel;

    public static int Run(App app, string[] args)
    {
        // A second launch (Start menu, Explorer) asks the running copy to show its window, like reopening a Mac app.
        using var mutex = new Mutex(true, InstanceMutexName, out bool first);
        if (!first)
        {
            try { using var reopen = EventWaitHandle.OpenExisting(ReopenEventName); reopen.Set(); } catch (Exception) { }
            return 0;
        }
        using var reopenEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ReopenEventName);
        new Thread(() =>
        {
            while (reopenEvent.WaitOne())
                app.Dispatcher.BeginInvoke(() => AppWindow.Shared.Show());
        }) { IsBackground = true, Name = "Swivel reopen listener" }.Start();

        bool atLogin = args.Contains("--login");
        app.Startup += (_, _) => Launch(atLogin);
        app.Exit += (_, _) =>
        {
            Clothesline.Shared.Stop();
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
        };
        return app.Run();
    }

    static void Launch(bool atLogin)
    {
        // Probe which image formats this PC can write now, so the first Convert menu doesn't wait for it.
        Images.WarmUp();
        wheel = new WheelController();
        SetUpTray();
        watcher.OnKeyTap = mode => wheel.KeyTapped(mode, null);
        watcher.OnDragEnd = () => wheel.DragEnded();
        watcher.Start();
        QuickActions.Shared.SyncSettings();
        ShortcutWheel.Shared.Sync();
        Clothesline.Shared.Start();
        if (Environment.GetEnvironmentVariable("SWIVEL_SHOW_ROPE") != null) // for testing
            Delay(500, Clothesline.Shared.Toggle);
        Prefs.Changed += () => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            RebuildMenu();
            QuickActions.Shared.SyncSettings();
            ShortcutWheel.Shared.Sync();
        });

        // Opened by hand: show the window. Started at login: stay in the tray.
        if (atLogin)
        {
            if (Prefs.IsEnabled)
                Toast.Shared.Show(L("Swivel is running — drag a file, then tap %@", Prefs.Key(WheelMode.Convert).Symbol()), "wheel");
        }
        else
        {
            // SWIVEL_TAB=<0-15> opens a specific page (for testing).
            AppTab? tab = int.TryParse(Environment.GetEnvironmentVariable("SWIVEL_TAB"), out var n) && Enum.IsDefined(typeof(AppTab), n)
                ? (AppTab)n : null;
            AppWindow.Shared.Show(tab);
            if (tab is null) AskAboutScreenshots();
        }
    }

    static void Delay(int milliseconds, Action action)
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); action(); };
        timer.Start();
    }

    /// First launch: ask before hanging new screenshots.
    static void AskAboutScreenshots()
    {
        if (Prefs.ClotheslineAsked) return;
        Prefs.ClotheslineAsked = true;
        var answer = MessageBox.Show(
            L("New screenshots (Print Screen, Win + Print Screen, or the Snipping Tool with automatic saving) will appear as a card in the corner, ready to hang on a rope along the top of the screen. Move the pointer to the top edge of the screen to lower it. Screenshot files stay in your Screenshots folder."),
            L("Hang your screenshots on the clothesline?"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes) Prefs.ClotheslineTakeover = true;
    }

    // MARK: Tray

    static void SetUpTray()
    {
        tray = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Visible = true,
            Text = "Swivel",
        };
        tray.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) AppWindow.Shared.Show(AppTab.Overview);
        };
        RebuildMenu();
    }

    static System.Drawing.Icon LoadIcon()
    {
        var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Swivel.ico"))?.Stream;
        return stream != null ? new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize) : System.Drawing.SystemIcons.Application;
    }

    /// Rebuilt whenever settings change, so the key names and option lists always match.
    static void RebuildMenu()
    {
        if (tray is null) return;
        bool wheelOn = Feature.Wheel.IsOn(), ropeOn = Feature.Clothesline.IsOn();
        bool anyOn = Features.All.Any(f => f.IsOn());
        watcher.IsEnabled = wheelOn;

        var keys = string.Join(" · ", WheelModes.All.Where(m => m.IsAvailable()).Select(m => $"{Prefs.Key(m).Symbol()} {m.Title()}"));
        var tip = new List<string>();
        if (wheelOn) tip.Add(L("Wheel: drag a file, then tap %@", keys));
        if (ropeOn) tip.Add(L("Clothesline: move to the top edge, or press Ctrl + Alt + T"));
        var text = tip.Count == 0
            ? L(anyOn ? "Swivel — open the menu to use your enabled tools." : "Swivel — everything is off. Open this menu to turn a feature on.")
            : string.Join("\n", tip);
        // NotifyIcon tooltips are limited to 127 characters.
        tray.Text = text.Length > 127 ? text[..126] + "…" : text;

        var old = tray.ContextMenuStrip;
        var menu = new Forms.ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };
        menu.Items.Add(new Forms.ToolStripMenuItem(L("Swivel")) { Enabled = false });

        // Quick switches, one per feature.
        foreach (var feature in Features.All)
        {
            var item = Add(menu, feature.Title(), () =>
            {
                feature.SetOn(!feature.IsOn());
                Feedback.Toggle(feature.IsOn());
                Toast.Shared.Show(L(feature.IsOn() ? "%@ is on" : "%@ is off", feature.Title()), feature.IsOn() ? "on" : "off");
            });
            item.Checked = feature.IsOn();
            item.ToolTipText = feature.Blurb();
        }

        if (wheelOn)
        {
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(new Forms.ToolStripMenuItem(L("Wheel — drag a file, keep holding, then tap a key:")) { Enabled = false });
            foreach (var (mode, title) in new[] { (WheelMode.Convert, "Convert formats"), (WheelMode.Tools, "File tools"), (WheelMode.Actions, "File actions"),
                                                  (WheelMode.Apps, "App Actions"), (WheelMode.Codes, "QR Library") })
            {
                if (!mode.IsAvailable()) continue;
                var item = new Forms.ToolStripMenuItem($"{Prefs.Key(mode).Title()} — {L(title)}");
                foreach (var line in Guide(mode)) item.DropDownItems.Add(new Forms.ToolStripMenuItem(line) { Enabled = true });
                item.DropDownItems.Add(new Forms.ToolStripSeparator());
                item.DropDownItems.Add(new Forms.ToolStripMenuItem(L("Customize…"), null, (_, _) => AppWindow.Shared.Show(AppTab.Customize)));
                menu.Items.Add(item);
            }
            Add(menu, L("Menu Style: %@…", Prefs.Style.Title()), () => AppWindow.Shared.Show(AppTab.Style));
        }

        if (Feature.Shelf.IsOn()) Add(menu, L("Show Shelf"), () => Shelf.Shared.Show());
        if (Feature.TextCapture.IsOn())
            Add(menu, L("Extract Text…"), () =>
            {
                if (ToolUI.ChooseFiles(imagesOnly: true, multiple: false).FirstOrDefault() is { } file) TextCaptureWindow.Present(file);
            });
        if (Feature.ImagePresets.IsOn()) Add(menu, L("Image Presets…"), () => AppWindow.Shared.Show(AppTab.ImagePresets));
        if (Feature.QuickActions.IsOn()) Add(menu, L("Show Quick Actions"), () => QuickActions.Shared.Show());
        if (Feature.BatchRename.IsOn()) Add(menu, L("Batch Rename…"), () => FileTools.ChooseAndOpen(Feature.BatchRename));
        if (Feature.PdfPages.IsOn()) Add(menu, L("PDF Pages…"), () => FileTools.ChooseAndOpen(Feature.PdfPages));
        if (Feature.QrCodes.IsOn())
        {
            Add(menu, L("Read QR Codes…"), () => FileTools.ChooseAndOpen(Feature.QrCodes));
            Add(menu, L("QR Library…"), () => QrLibraryWindow.Show());
        }
        if (wheelOn && Prefs.ShortcutWheelEnabled)
            Add(menu, L("Show Tool Wheel"), () => ShortcutWheel.Shared.Show()).ShortcutKeyDisplayString = Prefs.WheelShortcut.Title();
        if (Feature.AppRouting.IsOn()) Add(menu, L("App Actions…"), () => AppWindow.Shared.Show(AppTab.AppRouting));

        if (ropeOn)
        {
            menu.Items.Add(new Forms.ToolStripSeparator());
            var show = Add(menu, L("Show Clothesline"), () => Clothesline.Shared.Toggle());
            show.ShortcutKeyDisplayString = "Ctrl+Alt+T";
            Add(menu, L("Hang New Screenshots"), () => Prefs.ClotheslineTakeover = !Prefs.ClotheslineTakeover).Checked = Prefs.ClotheslineTakeover;
            Add(menu, L("Hang Copied Images"), () => Prefs.ClotheslineHangCopied = !Prefs.ClotheslineHangCopied).Checked = Prefs.ClotheslineHangCopied;
        }

        menu.Items.Add(new Forms.ToolStripSeparator());
        Add(menu, L("Sound Effects"), () => Prefs.SoundEffects = !Prefs.SoundEffects).Checked = Prefs.SoundEffects;
        menu.Items.Add(new Forms.ToolStripSeparator());
        Add(menu, L("Feel & Text…"), () => AppWindow.Shared.Show(AppTab.Feel));
        Add(menu, L("Open Swivel…"), () => AppWindow.Shared.Show(AppTab.Overview));
        Add(menu, L("Open at Login"), () =>
        {
            try { LoginItem.IsEnabled = !LoginItem.IsEnabled; }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Swivel", MessageBoxButton.OK, MessageBoxImage.Warning); }
            RebuildMenu();
        }).Checked = LoginItem.IsEnabled;
        menu.Items.Add(new Forms.ToolStripSeparator());
        Add(menu, L("Quit Swivel"), () => Application.Current.Shutdown());

        tray.ContextMenuStrip = menu;
        old?.Dispose();
    }

    static Forms.ToolStripMenuItem Add(Forms.ContextMenuStrip menu, string title, Action action)
    {
        var item = new Forms.ToolStripMenuItem(title, null, (_, _) => action());
        menu.Items.Add(item);
        return item;
    }

    /// One row per file kind listing exactly what that key's menu offers, in the user's order.
    static IEnumerable<string> Guide(WheelMode mode)
    {
        FileKind[] kinds = mode is WheelMode.Actions or WheelMode.Codes
            ? [FileKind.Other]
            : [FileKind.Image, FileKind.Video, FileKind.Audio, FileKind.Pdf, FileKind.Document, FileKind.Archive, FileKind.Other];
        foreach (var kind in kinds)
        {
            var titles = Catalog.Items(kind.SampleFiles(), mode).Select(i => i.DisplayTitle).ToList();
            if (titles.Count == 0) continue;
            var label = mode is WheelMode.Actions or WheelMode.Codes ? L("Any file or folder") : kind.Title();
            yield return $"{label}:  {string.Join(" · ", titles)}";
        }
    }
}

/// "Open at Login" through the per-user Run key (no elevation needed).
static class LoginItem
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    static string Command => $"\"{Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName}\" --login";

    public static bool IsEnabled
    {
        get
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("Swivel") is string;
        }
        set
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue("Swivel", Command);
            else key.DeleteValue("Swivel", throwOnMissingValue: false);
        }
    }
}
