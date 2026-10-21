using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Swivel;

/// Pages of the settings window. The numbers match the Mac app's SWIVEL_TAB values.
enum AppTab { General = 0, Shortcuts = 1, Style = 2, Customize = 3, Clothesline = 4, Overview = 5, TextCapture = 6, Shelf = 7, ImagePresets = 8, QuickActions = 9, Feel = 10, Colors = 11 }

/// The app's main window, laid out like PowerToys Settings: features in a sidebar, each with its own switch and
/// settings (AppWindow.swift / BundleWindow.swift). While it is open Swivel shows on the taskbar.
sealed class AppWindow
{
    public static AppWindow Shared { get; } = new();

    SettingsWindow? window;

    /// The open window, if any.
    internal SettingsWindow? Window => window;

    /// Shows the window (Overview when no page is given) and brings it to the front.
    public void Show(AppTab? tab = null)
    {
        var shown = window;
        if (shown is null)
        {
            shown = window = new SettingsWindow();
            shown.Closed += (_, _) =>
            {
                if (window == shown) window = null;
            };
            shown.Select(tab ?? AppTab.Overview);
            shown.Show();
        }
        else
        {
            shown.Select(tab ?? AppTab.Overview);
            if (!shown.IsVisible) shown.Show();
            if (shown.WindowState == WindowState.Minimized) shown.WindowState = shown.StateBeforeMinimize;
        }
        shown.Activate();
        shown.Focus();
    }

    /// Rebuilds the window's contents in the new language and returns to General. The window itself stays, so it
    /// doesn't vanish and come back.
    public void ReloadForLanguage()
    {
        if (window is null) return;
        window.Rebuild(AppTab.General);
    }
}

/// The settings window: Fluent theme (light/dark follows Windows), Mica backdrop from WPF's Fluent window style on
/// Windows 11 (a solid background elsewhere), a sidebar of pages and a scrolling content area.
sealed class SettingsWindow : Window
{
    const string BoundsKey = "window.settings.bounds";

    readonly bool remembersBounds;
    readonly DispatcherTimer saveTimer;

    public SettingsView View { get; private set; }
    public WindowState StateBeforeMinimize { get; private set; } = WindowState.Normal;

    /// `offscreen` is for tests: the window opens far off the desktop, never activates and remembers nothing.
    public SettingsWindow(bool offscreen = false)
    {
        remembersBounds = !offscreen;
        Title = "Swivel";
        Width = 1100;
        Height = 780;
        MinWidth = 820;
        MinHeight = 560;
        // Running under the app, ThemeMode comes from Application (System). Without one (tests), set it here.
        if (Application.Current is null || Application.Current.ThemeMode == ThemeMode.None) ThemeMode = ThemeMode.System;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        if (offscreen)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -30000;
            Top = -30000;
            ShowActivated = false;
            ShowInTaskbar = false;
        }
        else LoadBounds();

        View = new SettingsView(this);
        Content = View;

        saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        saveTimer.Tick += (_, _) =>
        {
            saveTimer.Stop();
            SaveBounds();
        };
        LocationChanged += (_, _) => ScheduleSave();
        SizeChanged += (_, _) => ScheduleSave();
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized) StateBeforeMinimize = WindowState;
            ScheduleSave();
        };
        Closing += (_, _) => SaveBounds();
        Activated += (_, _) => View.RefreshCurrent();
        WatchTextSettings();
    }

    /// Feel & Text's text size and bold text apply at once: the window's own font (which the standard controls
    /// inherit) through AppTypography, and the window's labels through TextStyle. Only a change to those two
    /// settings walks the window, not every slider step.
    void WatchTextSettings()
    {
        AppTypography.Apply(this);
        (TextSize, bool)? applied = null;
        void update()
        {
            var settings = Prefs.Interaction;
            if (applied == (settings.TextSize, settings.BoldText)) return;
            applied = (settings.TextSize, settings.BoldText);
            TextStyle.ApplyTree(this);
        }
        void changed() => Dispatcher.BeginInvoke(update);
        Loaded += (_, _) => update();
        Prefs.Changed += changed;
        Closed += (_, _) => Prefs.Changed -= changed;
    }

    public void Select(AppTab tab) => View.Select(tab);

    /// A new view in the current language, keeping the window where it is.
    public void Rebuild(AppTab tab)
    {
        View = new SettingsView(this);
        Content = View;
        View.Select(tab);
    }

    public void SetPageTitle(string page) => Title = L("Swivel — %@", page);

    void ScheduleSave()
    {
        if (!remembersBounds || !IsLoaded) return;
        saveTimer.Stop();
        saveTimer.Start();
    }

    void SaveBounds()
    {
        if (!remembersBounds || !IsLoaded || WindowState == WindowState.Minimized && RestoreBounds.IsEmpty) return;
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (r.IsEmpty || r.Width < 100 || r.Height < 100) return;
        // Not a setting anyone watches: saved without raising Prefs.Changed.
        Prefs.Set(BoundsKey, new[] { r.X, r.Y, r.Width, r.Height, WindowState == WindowState.Maximized ? 1 : 0 }, notify: false);
    }

    void LoadBounds()
    {
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (Prefs.Get<double[]>(BoundsKey) is not { Length: 5 } b) return;
        var saved = new Rect(b[0], b[1], Math.Max(b[2], MinWidth), Math.Max(b[3], MinHeight));
        var desktop = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        // Only where at least the title bar is still reachable (a monitor may have gone).
        var titleBar = new Rect(saved.X + 40, saved.Y, Math.Max(saved.Width - 80, 1), 32);
        if (!desktop.IntersectsWith(titleBar)) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = saved.X;
        Top = saved.Y;
        Width = saved.Width;
        Height = saved.Height;
        if (b[4] >= 1) WindowState = WindowState.Maximized;
    }
}

/// Sidebar of pages on the left (Overview, each feature, General), the selected page on the right (BundleSplit).
sealed class SettingsView : Grid
{
    internal enum Page { Overview, Wheel, Clothesline, TextCapture, Shelf, ImagePresets, QuickActions, General, Feel }

    readonly SettingsWindow? owner;
    readonly Sidebar sidebar;
    readonly PageHost host = new();
    readonly Dictionary<Page, FrameworkElement> pages = [];

    public Page Current { get; private set; } = (Page)(-1);

    public SettingsView(SettingsWindow? owner)
    {
        this.owner = owner;
        // Text language for WPF's font fallback (Microsoft YaHei / JhengHei / Yu Gothic for the CJK translations).
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(Languages.Resolved switch
        {
            Swivel.Language.ZhHans => "zh-CN",
            Swivel.Language.ZhHant => "zh-TW",
            Swivel.Language.Ja => "ja-JP",
            _ => "en-US",
        });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(272) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sidebar = new Sidebar(ShowPage);
        Children.Add(sidebar);
        Grid.SetColumn(host, 1);
        Children.Add(host);
    }

    internal static string Title(Page page) => page switch
    {
        Page.Overview => L("Overview"),
        Page.Wheel => Feature.Wheel.Title(),
        Page.Clothesline => Feature.Clothesline.Title(),
        Page.TextCapture => Feature.TextCapture.Title(),
        Page.Shelf => Feature.Shelf.Title(),
        Page.ImagePresets => Feature.ImagePresets.Title(),
        Page.QuickActions => Feature.QuickActions.Title(),
        Page.Feel => L("Feel & Text"),
        _ => L("General"),
    };

    internal static string Glyph(Page page) => page switch
    {
        Page.Overview => "", // Home
        Page.Wheel => Feature.Wheel.Glyph(),
        Page.Clothesline => Feature.Clothesline.Glyph(),
        Page.TextCapture => Feature.TextCapture.Glyph(),
        Page.Shelf => Feature.Shelf.Glyph(),
        Page.ImagePresets => Feature.ImagePresets.Glyph(),
        Page.QuickActions => Feature.QuickActions.Glyph(),
        Page.Feel => "", // Personalize (the Mac's slider.horizontal.3 is Image Presets' glyph here)
        _ => "", // Settings
    };

    internal static Feature? FeatureOf(Page page) => page switch
    {
        Page.Wheel => Swivel.Feature.Wheel,
        Page.Clothesline => Swivel.Feature.Clothesline,
        Page.TextCapture => Swivel.Feature.TextCapture,
        Page.Shelf => Swivel.Feature.Shelf,
        Page.ImagePresets => Swivel.Feature.ImagePresets,
        Page.QuickActions => Swivel.Feature.QuickActions,
        _ => null,
    };

    public void Select(AppTab tab)
    {
        switch (tab)
        {
            case AppTab.Overview: ShowPage(Page.Overview); break;
            case AppTab.General: ShowPage(Page.General); break;
            case AppTab.Clothesline: ShowPage(Page.Clothesline); break;
            case AppTab.TextCapture: ShowPage(Page.TextCapture); break;
            case AppTab.Shelf: ShowPage(Page.Shelf); break;
            case AppTab.ImagePresets: ShowPage(Page.ImagePresets); break;
            case AppTab.QuickActions: ShowPage(Page.QuickActions); break;
            case AppTab.Feel: ShowPage(Page.Feel); break;
            default:
                ShowPage(Page.Wheel);
                ((WheelPage)pages[Page.Wheel]).Select(tab);
                break;
        }
    }

    /// The page object (built on first use).
    internal FrameworkElement PageFor(Page page)
    {
        if (pages.TryGetValue(page, out var built)) return built;
        FrameworkElement made = page switch
        {
            Page.Overview => new OverviewPage(ShowPage),
            Page.Wheel => new WheelPage(),
            Page.Clothesline => new FeaturePage(Swivel.Feature.Clothesline, new ClotheslinePane()),
            Page.TextCapture => new FeaturePage(Swivel.Feature.TextCapture, new TextCapturePane()),
            Page.Shelf => new FeaturePage(Swivel.Feature.Shelf, new ShelfPane()),
            Page.ImagePresets => new FeaturePage(Swivel.Feature.ImagePresets, new ImagePresetsPane()),
            Page.QuickActions => new FeaturePage(Swivel.Feature.QuickActions, new QuickActionsPane()),
            Page.Feel => new FeelPage(),
            _ => new GeneralPage(),
        };
        pages[page] = made;
        return made;
    }

    /// The page showing now.
    internal FrameworkElement? CurrentPage => pages.GetValueOrDefault(Current);

    public void ShowPage(Page page)
    {
        sidebar.Select(page);
        if (page == Current) return;
        Current = page;
        host.Show(PageFor(page));
        owner?.SetPageTitle(Title(page));
    }

    internal string SidebarLabel(Page page) => sidebar.Items[page].Label.Text;
    internal bool FindSidebarDot(Page page) => sidebar.Items[page].DotOn;

    /// Re-reads anything that is not announced through Prefs.Changed (the login item, a hotkey's registration).
    public void RefreshCurrent()
    {
        if (CurrentPage is FeaturePage { Content: QuickActionsPane quick }) quick.Refresh();
        if (CurrentPage is GeneralPage general) general.Refresh();
    }
}

/// The content area: Windows 11's raised layer with a rounded top-left corner, scrolling, and a short entrance
/// (rise and fade on springs) when the page changes.
sealed class PageHost : Border
{
    readonly ScrollViewer scroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Focusable = false,
    };
    readonly Border holder = new() { Padding = new Thickness(40, 28, 40, 40) };
    readonly TranslateTransform rise = new();
    readonly SpringGroup motion;

    public PageHost()
    {
        CornerRadius = new CornerRadius(8, 0, 0, 0);
        BorderThickness = new Thickness(1, 1, 0, 0);
        SetResourceReference(BackgroundProperty, "LayerFillColorDefaultBrush");
        SetResourceReference(BorderBrushProperty, "CardStrokeColorDefaultBrush");
        holder.RenderTransform = rise;
        scroll.Content = holder;
        Child = scroll;
        motion = new SpringGroup(this, () =>
        {
            rise.Y = motion![0].Value;
            holder.Opacity = Math.Clamp(motion[1].Value, 0, 1);
        }, new Spring(0, response: 0.32, damping: 0.9), new Spring(1, response: 0.2, damping: 1));
    }

    public void Show(FrameworkElement page)
    {
        // A page moves between hosts only when the view is rebuilt; detach it from any old one.
        if (page.Parent is Border old && old != holder) old.Child = null;
        holder.Child = page;
        scroll.ScrollToTop();
        // Labels catch up with the text settings as they load; buttons need a pass once their templates exist.
        Dispatcher.Later(() => TextStyle.ApplyTree(page));
        if (Motion.Reduced || !IsLoaded) return;
        motion[0].Snap(24);
        motion[1].Snap(0);
        motion[0].Target = 0;
        motion[1].Target = 1;
        motion.Kick();
    }
}

/// The sidebar: the app's name, then one row per page with a feature's status dot, and the accent pill that slides
/// to the selected row.
sealed class Sidebar : Grid
{
    readonly Action<SettingsView.Page> onSelect;
    readonly StackPanel list = new() { Margin = new Thickness(4, 0, 4, 0) };
    readonly Canvas overlay = new() { IsHitTestVisible = false };
    readonly Border pill = new() { Width = 3, Height = 16, CornerRadius = new CornerRadius(1.5) };
    readonly Dictionary<SettingsView.Page, NavItem> items = [];
    readonly SpringGroup motion;
    SettingsView.Page? selected;
    bool placed;

    public Sidebar(Action<SettingsView.Page> onSelect)
    {
        this.onSelect = onSelect;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var mark = AppIcon.Element(20);
        var name = Ui.Text("Swivel", 14, FontWeights.SemiBold, wrap: false);
        name.Margin = new Thickness(10, 0, 0, 0);
        name.VerticalAlignment = VerticalAlignment.Center;
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 16, 16, 16), Children = { mark, name } };
        Children.Add(header);

        var body = new Grid();
        body.Children.Add(list);
        body.Children.Add(overlay);
        overlay.Children.Add(pill);
        pill.SetResourceReference(Border.BackgroundProperty, "AccentFillColorDefaultBrush");
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body, Focusable = false };
        Grid.SetRow(scroll, 1);
        Children.Add(scroll);

        foreach (var page in Enum.GetValues<SettingsView.Page>())
        {
            var item = new NavItem(page, () => Choose(page), Move);
            items[page] = item;
            if (page == SettingsView.Page.General)
            {
                var divider = new Border { Height = 1, Margin = new Thickness(12, 8, 12, 8) };
                divider.SetResourceReference(Border.BackgroundProperty, "DividerStrokeColorDefaultBrush");
                list.Children.Add(divider);
            }
            list.Children.Add(item);
        }
        AutomationProperties.SetName(list, "Swivel");
        motion = new SpringGroup(this, () => Canvas.SetTop(pill, motion![0].Value), new Spring(0, response: 0.3, damping: 0.8));
        LayoutUpdated += (_, _) => PlacePill(animated: placed);
        PrefsWatch.Attach(this, RefreshDots);
    }

    internal IReadOnlyDictionary<SettingsView.Page, NavItem> Items => items;

    void Choose(SettingsView.Page page) => onSelect(page);

    /// Up and Down walk the list, selecting as they go (like the Mac app's source list).
    void Move(SettingsView.Page from, int step)
    {
        var all = Enum.GetValues<SettingsView.Page>();
        int index = Math.Clamp(Array.IndexOf(all, from) + step, 0, all.Length - 1);
        onSelect(all[index]);
        items[all[index]].Focus();
    }

    public void Select(SettingsView.Page page)
    {
        if (selected == page) return;
        selected = page;
        foreach (var (p, item) in items) item.SetSelected(p == page);
        PlacePill(animated: placed);
    }

    void PlacePill(bool animated)
    {
        if (selected is not { } page || !items[page].IsLoaded || items[page].ActualHeight == 0) return;
        var item = items[page];
        double y = item.TranslatePoint(new Point(0, 0), overlay).Y + (item.ActualHeight - pill.Height) / 2;
        Canvas.SetLeft(pill, item.TranslatePoint(new Point(0, 0), overlay).X);
        if (placed && Math.Abs(motion[0].Target - y) < 0.5) return;
        motion[0].Target = y;
        if (animated) motion.Kick();
        else motion.Settle();
        placed = true;
    }

    /// Only the dots follow the switches; rows are never rebuilt for a settings change.
    void RefreshDots()
    {
        foreach (var item in items.Values) item.RefreshDot();
    }
}

/// One sidebar row: glyph, title and (for features) the status dot. The background fades in under the pointer and
/// stays for the selected row.
sealed class NavItem : Border
{
    readonly SettingsView.Page page;
    readonly StatusDot? dot;
    readonly SpringGroup motion;
    readonly Action<SettingsView.Page, int> move;
    readonly Action choose;

    public bool IsSelected { get; private set; }
    public TextBlock Label { get; }
    public bool DotOn => dot?.IsOn == true;

    public NavItem(SettingsView.Page page, Action choose, Action<SettingsView.Page, int> move)
    {
        this.page = page;
        this.choose = choose;
        this.move = move;
        Height = 38;
        Margin = new Thickness(0, 1, 0, 1);
        CornerRadius = new CornerRadius(4);
        Padding = new Thickness(14, 0, 12, 0);
        Focusable = true;
        Background = Brushes.Transparent;
        SetResourceReference(FocusVisualStyleProperty, "DefaultControlFocusVisualStyle");
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = Ui.Glyph(SettingsView.Glyph(page), 16, Tone.Accent);
        grid.Children.Add(icon);
        Label = Ui.Text(SettingsView.Title(page), 14, wrap: false);
        Label.Margin = new Thickness(14, 0, 8, 0);
        Label.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(Label, 1);
        grid.Children.Add(Label);
        if (SettingsView.FeatureOf(page) is { } feature)
        {
            dot = new StatusDot();
            dot.SetOn(feature.IsOn(), animated: false);
            Grid.SetColumn(dot, 2);
            grid.Children.Add(dot);
        }
        Child = grid;
        AutomationProperties.SetName(this, SettingsView.Title(page));
        motion = new SpringGroup(this, Apply, new Spring(0, response: 0.12, damping: 1), new Spring(0, response: 0.15, damping: 1),
            new Spring(0, response: 0.09, damping: 1));
        ThemeWatch.Attach(this, Apply);
    }

    public void RefreshDot()
    {
        if (dot != null && SettingsView.FeatureOf(page) is { } feature) dot.SetOn(feature.IsOn(), animated: true);
    }

    public void SetSelected(bool on)
    {
        IsSelected = on;
        TextStyle.SetWeight(Label, on ? FontWeights.SemiBold : FontWeights.Normal);
        motion[1].Target = on ? 1 : 0;
        motion.Kick();
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        motion[0].Target = 1;
        motion.Kick();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        motion[0].Target = 0;
        motion[2].Target = 0;
        motion.Kick();
    }

    // Presses respond on mouse-down: the page switches as the button goes down, like the Mac source list.
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        motion[2].Target = 1;
        motion.Kick();
        choose();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        motion[2].Target = 0;
        motion.Kick();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Up: move(page, -1); e.Handled = true; break;
            case Key.Down: move(page, 1); e.Handled = true; break;
            case Key.Enter or Key.Space: choose(); e.Handled = true; break;
        }
    }

    void Apply()
    {
        double hover = Math.Clamp(motion[0].Value, 0, 1), sel = Math.Clamp(motion[1].Value, 0, 1), press = Math.Clamp(motion[2].Value, 0, 1);
        var clear = Color.FromArgb(0, 0, 0, 0);
        var secondary = Ui.Color(this, "SubtleFillColorSecondaryBrush", Color.FromArgb(0x09, 0, 0, 0));
        var tertiary = Ui.Color(this, "SubtleFillColorTertiaryBrush", Color.FromArgb(0x06, 0, 0, 0));
        // Hovered or selected: the subtle fill; selected and hovered: a touch stronger; pressed: the tertiary fill.
        var color = Mix.Lerp(clear, secondary, Math.Max(hover, sel));
        color = Mix.Lerp(color, Color.FromArgb((byte)Math.Min(255, secondary.A * 1.6), secondary.R, secondary.G, secondary.B), hover * sel);
        color = Mix.Lerp(color, tertiary, press);
        Background = Ui.Brush(color);
    }
}
