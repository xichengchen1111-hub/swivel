using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Swivel;

/// A page section laid out as a vertical form that re-reads Prefs while it is showing (the Mac app's Pane).
abstract class Pane : StackPanel
{
    protected Pane() => PrefsWatch.Attach(this, Refresh);

    public virtual void Refresh() { }

    protected void Add(UIElement element, double top = 0)
    {
        if (element is FrameworkElement fe && top > 0) fe.Margin = new Thickness(fe.Margin.Left, top, fe.Margin.Right, fe.Margin.Bottom);
        Children.Add(element);
    }
}

/// The Wheel's page: title, the switch, then Shortcuts / Style / Customize / Colors.
sealed class WheelPage : StackPanel
{
    readonly SelectorBar picker;
    readonly Border holder = new() { Margin = new Thickness(0, 12, 0, 0) };
    readonly FrameworkElement[] sections;

    public WheelPage()
    {
        sections = [new ShortcutsPane(), new StylePane(), new CustomizePane(), new TemplateColorsPane()];
        Children.Add(Ui.Title(Feature.Wheel.Title()));
        var header = new FeatureHeader(Feature.Wheel) { Margin = new Thickness(0, 20, 0, 0) };
        Children.Add(header);
        picker = new SelectorBar([L("Shortcuts"), L("Style"), L("Customize"), L("Colors")]) { Margin = new Thickness(-12, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        picker.SelectionChanged += Show;
        Children.Add(picker);
        Children.Add(holder);
        picker.Select(0);
    }

    internal int SelectedSection => picker.Selected;
    internal FrameworkElement Section(int index) => sections[index];

    public void Select(AppTab tab) => picker.Select(tab switch
    {
        AppTab.Style => 1,
        AppTab.Customize => 2,
        AppTab.Colors => 3,
        _ => 0,
    });

    void Show(int index)
    {
        holder.Child = sections[index];
        // A section shown for the first time since the text settings changed: its buttons catch up (labels do so
        // on their own as they load).
        Dispatcher.Later(() => TextStyle.ApplyTree(sections[index]));
    }
}

// MARK: - Shortcuts

/// Which key opens each menu. Windows has Shift, Ctrl and Alt (the Windows key opens Start, and there is no fn key
/// that survives a drag). Picking a key another menu uses swaps the two (Prefs.SetKey).
sealed class ShortcutsPane : Pane
{
    readonly Dictionary<WheelMode, ComboBox> pickers = [];
    bool refreshing;

    public ShortcutsPane()
    {
        Add(Ui.Text(L("Choose which key opens each menu. Start dragging first, then tap the key."), tone: Tone.Secondary), 4);
        var group = new CardGroup { Margin = new Thickness(0, 16, 0, 0) };
        foreach (var (mode, title) in new[] { (WheelMode.Convert, "Convert formats"), (WheelMode.Tools, "File tools"), (WheelMode.Actions, "File actions") })
        {
            var picker = new ComboBox { MinWidth = 140 };
            foreach (var key in TriggerKeys.All) picker.Items.Add(key.Title());
            picker.SelectionChanged += (_, _) =>
            {
                if (refreshing || picker.SelectedIndex < 0) return;
                Prefs.SetKey(TriggerKeys.All[picker.SelectedIndex], mode);
            };
            pickers[mode] = picker;
            group.Add(new SettingsCard(mode.Glyph(), L(title), null, picker));
        }
        Add(group);
        Add(Ui.Text(L("Picking a key that another menu uses swaps the two."), 12, tone: Tone.Secondary), 10);
        Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { Ui.Button(L("Restore Defaults"), Prefs.ResetKeys) } }, 14);
        Refresh();
    }

    internal ComboBox Picker(WheelMode mode) => pickers[mode];

    public override void Refresh()
    {
        refreshing = true;
        foreach (var (mode, picker) in pickers) picker.SelectedIndex = Array.IndexOf(TriggerKeys.All, Prefs.Key(mode));
        refreshing = false;
    }
}

// MARK: - Style

sealed class StylePane : Pane
{
    readonly List<TemplateCard> cards = [];

    public StylePane()
    {
        Add(Ui.Text(L("Choose how the menu looks when it pops up. Click a template to use it."), tone: Tone.Secondary), 4);
        var grid = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        foreach (var style in MenuStyles.All)
        {
            var card = new TemplateCard(style) { Margin = new Thickness(0, 0, 12, 12) };
            card.OnPick = picked => Prefs.Style = picked;
            cards.Add(card);
            grid.Children.Add(card);
        }
        Add(grid);
        Add(Ui.TryItHint());
        Refresh();
    }

    internal IReadOnlyList<TemplateCard> Cards => cards;

    public override void Refresh()
    {
        foreach (var card in cards) card.SetSelected(card.MenuStyle == Prefs.Style);
    }
}

/// A button-style click: pressed while the mouse is down over the control, committed only on release over it.
struct ClickTracker
{
    public bool IsDown { get; private set; }
    public bool IsInside { get; private set; }
    public readonly bool ShowsPressed => IsDown && IsInside;

    public void Down()
    {
        IsDown = true;
        IsInside = true;
    }

    public void Drag(bool inside)
    {
        if (IsDown) IsInside = inside;
    }

    /// Ends the click. True when it should commit.
    public bool Up(bool inside)
    {
        bool commit = IsDown && inside;
        IsDown = false;
        IsInside = false;
        return commit;
    }
}

/// A template on the Style page. The whole card is one button: its edge lifts under the pointer, it presses in on
/// mouse-down and picks the template on release (dragging off first cancels, as with a native button). The menu
/// inside is a live preview that can be tried on its own.
sealed class TemplateCard : Grid
{
    public const double CardWidth = 228, CardHeight = 244, PreviewHeight = 156, Inset = 11;
    const int Hover = 0, Press = 1, Selection = 2;

    public MenuStyle MenuStyle { get; }
    public Action<MenuStyle>? OnPick { get; set; }
    public bool IsSelected { get; private set; }

    readonly Border face = new() { CornerRadius = new CornerRadius(8) };
    readonly PreviewFrame preview = new();
    readonly ScaleTransform scale = new();
    readonly DropShadowEffect shadow = new() { Direction = 270, Color = Colors.Black, Opacity = 0 };
    readonly SpringGroup motion;
    ClickTracker click;
    int keyPress;

    public TemplateCard(MenuStyle style)
    {
        MenuStyle = style;
        Width = CardWidth;
        // At least the Mac's height; larger text (Feel & Text) lets the blurb grow instead of clipping it.
        MinHeight = CardHeight;
        Focusable = true;
        Background = Brushes.Transparent;
        SetResourceReference(FocusVisualStyleProperty, "DefaultControlFocusVisualStyle");
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = scale;
        // The face is a sibling of the content, so its border thickening on selection never nudges the preview, and
        // its shadow never touches the text.
        face.Effect = null;
        Children.Add(face);

        preview.Height = PreviewHeight;
        preview.Show(MenuPreview.Create(style, ["JPG", "PNG", "HEIC", "WebP", "TIFF", "GIF", "PDF"], WheelMode.Convert, hovered: 1), crossfade: false);
        var title = Ui.Text(style.Title(), 14, FontWeights.SemiBold, wrap: false);
        title.Margin = new Thickness(1, 8, 0, 0);
        var blurb = Ui.Text(style.Blurb(), 12, tone: Tone.Secondary);
        blurb.Margin = new Thickness(1, 2, 0, 0);
        var content = new StackPanel { Margin = new Thickness(Inset), Children = { preview, title, blurb } };
        Children.Add(content);

        ToolTip = L("Use the %@ template", style.Title());
        motion = new SpringGroup(this, Apply,
            new Spring(0, response: 0.14, damping: 1),
            new Spring(0, response: 0.09, damping: 1),
            new Spring(0, response: 0.22, damping: 0.7));
        ThemeWatch.Attach(this, Apply);
        Unloaded += (_, _) =>
        {
            // Leaving the screen (another page): forget the pointer, so the card comes back at rest.
            click = new ClickTracker();
            motion[Hover].Snap(0);
            motion[Press].Snap(0);
            Apply();
        };
        Apply();
    }

    /// The live preview's area, in card coordinates.
    internal Rect PreviewArea => new(Inset, Inset, CardWidth - 2 * Inset, PreviewHeight);

    internal bool IsInPreview(Point p) => PreviewArea.Contains(p);

    public void SetSelected(bool selected)
    {
        if (selected == IsSelected) return;
        IsSelected = selected;
        // Picked: the edge thickens into the accent on a short spring. Unpicked: it fades back, quicker.
        bool springy = selected && !Motion.Reduced;
        motion[Selection].Response = springy ? 0.22 : 0.14;
        motion[Selection].Damping = springy ? 0.7 : 1;
        motion[Selection].Target = selected ? 1 : 0;
        motion.Kick();
        (UIElementAutomationPeer.FromElement(this) as CardPeer)?.SelectionChanged(selected);
    }

    void Apply()
    {
        double h = Math.Clamp(motion[Hover].Value, 0, 1), p = Math.Clamp(motion[Press].Value, 0, 1), s = motion[Selection].Value;
        var separator = Ui.Color(this, "CardStrokeColorDefaultBrush", Color.FromArgb(0x0F, 0, 0, 0));
        var strong = Ui.Color(this, "ControlStrongStrokeColorDefaultBrush", Color.FromArgb(0x72, 0, 0, 0));
        var accent = Ui.Color(this, "AccentFillColorDefaultBrush", Color.FromRgb(0x00, 0x5F, 0xB8));
        var card = Ui.Color(this, "CardBackgroundFillColorDefaultBrush", Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));
        var label = Ui.Color(this, "TextFillColorPrimaryBrush", Color.FromArgb(0xE4, 0, 0, 0));
        face.BorderThickness = new Thickness(1 + 2 * Math.Max(s, 0));
        face.BorderBrush = Ui.Brush(Mix.Lerp(Mix.Lerp(separator, Mix.Lerp(separator, strong, 0.6), h), accent, Math.Clamp(s, 0, 1)));
        face.Background = Ui.Brush(Mix.Lerp(card, Color.FromArgb(card.A, label.R, label.G, label.B), 0.06 * p));
        // Under the pointer the card lifts on a soft shadow (no scaling, which would soften its text while it rests
        // there); pressed, the shadow tucks back in and the face darkens a little.
        double opacity = (Native.DarkMode ? 0.35 : 0.13) * h * (1 - 0.7 * p);
        if (opacity < 0.003) face.Effect = null;
        else
        {
            shadow.Opacity = opacity;
            shadow.BlurRadius = 2 * (4 + 6 * h * (1 - 0.5 * p));
            shadow.ShadowDepth = 1 + 3 * h * (1 - 0.6 * p);
            face.Effect = shadow;
        }
        // Pressed in to 97 %, about the centre. Animation effects off: colour only.
        double k = Motion.Reduced ? 1 : 1 - 0.03 * motion[Press].Value;
        scale.ScaleX = k;
        scale.ScaleY = k;
    }

    // MARK: Pointer — tunnelling events, so a click on the live preview presses the card too (as on the Mac).

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        if (e.LeftButton == MouseButtonState.Released && click.IsDown) click = new ClickTracker();
        SetHover(true);
        PressChanged();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        SetHover(false);
        if (!IsMouseCaptured)
        {
            click.Drag(inside: false);
            PressChanged();
        }
    }

    void SetHover(bool on)
    {
        motion[Hover].Response = on ? 0.14 : 0.1; // exits are quicker than entrances
        motion[Hover].Target = on ? 1 : 0;
        motion.Kick();
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        var p = e.GetPosition(this);
        PointerDown(p);
        Focus();
        // Outside the preview the card is the button and follows the drag; inside, the preview keeps the pointer.
        if (!IsInPreview(p)) CaptureMouse();
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        if (click.IsDown) PointerMove(e.GetPosition(this));
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        if (IsMouseCaptured) ReleaseMouseCapture();
        PointerUp(e.GetPosition(this));
    }

    bool Inside(Point p) => p.X >= 0 && p.Y >= 0 && p.X <= (ActualWidth > 0 ? ActualWidth : CardWidth) && p.Y <= (ActualHeight > 0 ? ActualHeight : CardHeight);

    internal void PointerDown(Point p)
    {
        click.Down();
        PressChanged();
    }

    internal void PointerMove(Point p)
    {
        click.Drag(Inside(p));
        PressChanged();
    }

    internal void PointerUp(Point p)
    {
        bool picked = click.Up(Inside(p));
        PressChanged();
        if (picked) OnPick?.Invoke(MenuStyle);
    }

    void PressChanged()
    {
        bool down = click.ShowsPressed;
        if (motion[Press].Target == (down ? 1 : 0)) return;
        // In fast and firm; back out with a little give.
        motion[Press].Response = down ? 0.09 : 0.28;
        motion[Press].Damping = down ? 1 : 0.8;
        motion[Press].Target = down ? 1 : 0;
        motion.Kick();
    }

    // MARK: Keyboard and accessibility

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is not (Key.Space or Key.Enter)) return;
        e.Handled = true;
        if (e.IsRepeat) return;
        // The same press and release as a click; the pick itself doesn't wait for it.
        int current = ++keyPress;
        motion[Press].Response = 0.09;
        motion[Press].Damping = 1;
        motion[Press].Target = 1;
        motion.Kick();
        OnPick?.Invoke(MenuStyle);
        Dispatcher.After(0.09, () =>
        {
            if (keyPress == current) PressChanged();
        });
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new CardPeer(this);

    internal sealed class CardPeer(TemplateCard card) : FrameworkElementAutomationPeer(card), ISelectionItemProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.RadioButton;
        protected override string GetClassNameCore() => "TemplateCard";
        protected override string GetNameCore() => card.MenuStyle.Title();
        protected override string GetHelpTextCore() => card.MenuStyle.Blurb();
        protected override bool IsControlElementCore() => true;
        protected override bool IsKeyboardFocusableCore() => true;
        public override object? GetPattern(PatternInterface pattern) => pattern == PatternInterface.SelectionItem ? this : base.GetPattern(pattern);

        public bool IsSelected => card.IsSelected;
        public IRawElementProviderSimple? SelectionContainer => null;
        public void Select() => card.OnPick?.Invoke(card.MenuStyle);
        public void AddToSelection() => Select();
        public void RemoveFromSelection() { }

        public void SelectionChanged(bool selected) =>
            RaisePropertyChangedEvent(SelectionItemPatternIdentifiers.IsSelectedProperty, !selected, selected);
    }
}

// MARK: - Customize

/// Where a reordered row lands, and when a reorder drag has moved to a new place.
struct DropSlot
{
    public int? Destination { get; private set; }

    /// The index a row moved from `from` ends up at when dropped above row `proposed` (the row itself counts).
    public static int DestinationFor(int from, int proposed) => from < proposed ? proposed - 1 : proposed;

    /// Records the place a drag would drop now. True when that is a new place; the drag's first place doesn't count.
    public bool Move(int place)
    {
        bool moved = Destination != null && Destination != place;
        Destination = place;
        return moved;
    }

    public void Reset() => Destination = null;
}

/// What Customize saves for one menu. Its rows list only the items whose tools are on right now (Extract Text, image
/// presets, Hang and Float come and go with their features), so the stored entries for every other title are carried
/// over: they stay hidden if they were, and each keeps its place right after the title it followed.
static class Arrangement
{
    public static (List<string> Order, HashSet<string> Hidden) Merged(IReadOnlyList<(string Title, bool Shown)> rows,
        IReadOnlyList<string> storedOrder, IReadOnlySet<string> storedHidden)
    {
        var present = rows.Select(r => r.Title).ToHashSet();
        var hidden = rows.Where(r => !r.Shown).Select(r => r.Title).ToHashSet();
        hidden.UnionWith(storedHidden.Where(t => !present.Contains(t)));
        var order = rows.Select(r => r.Title).ToList();
        var placed = new HashSet<string>(present);
        for (int i = 0; i < storedOrder.Count; i++)
        {
            var title = storedOrder[i];
            if (!placed.Add(title)) continue;
            // Everything before it in the stored order is already placed (shown now, or put back just before), so it
            // goes right after its predecessor; one that led the list stays at the front.
            int after = i > 0 ? order.IndexOf(storedOrder[i - 1]) : -1;
            order.Insert(after >= 0 ? after + 1 : 0, title);
        }
        return (order, hidden);
    }
}

/// Pick which options each menu shows and in what order. A live preview of the menu follows every change.
sealed class CustomizePane : Pane
{
    static readonly FileKind[] Kinds = [FileKind.Image, FileKind.Video, FileKind.Audio, FileKind.Pdf, FileKind.Document, FileKind.Archive, FileKind.Other];

    readonly ComboBox modePicker = new() { MinWidth = 150 };
    readonly ComboBox kindPicker = new() { MinWidth = 160 };
    readonly ArrangeList list = new();
    readonly PreviewFrame preview = new() { Width = 420, Height = 380 };
    List<(string Title, bool Shown)> rows = [];
    List<string> labels = []; // display titles: renaming an image preset keeps its stored title
    (MenuStyle Style, WheelMode Mode, string Titles)? previewKey;

    public CustomizePane()
    {
        Add(Ui.Text(L("Pick which options each menu shows and in what order. Untick to hide, drag to reorder."), tone: Tone.Secondary), 4);
        foreach (var title in new[] { "Convert formats", "File tools", "File actions" }) modePicker.Items.Add(L(title));
        foreach (var kind in Kinds)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal };
            item.Children.Add(Ui.Glyph(kind.Glyph(), 14));
            var name = Ui.Text(kind.Title(), 14, wrap: false);
            name.Margin = new Thickness(8, 0, 0, 0);
            item.Children.Add(name);
            AutomationProperties.SetName(item, kind.Title());
            kindPicker.Items.Add(new ComboBoxItem { Content = item });
        }
        modePicker.SelectedIndex = 0;
        kindPicker.SelectedIndex = 0;
        AutomationProperties.SetName(modePicker, L("Menu:"));
        AutomationProperties.SetName(kindPicker, L("File type:"));
        modePicker.SelectionChanged += (_, _) => Load(crossfade: true);
        kindPicker.SelectionChanged += (_, _) => Load(crossfade: true);

        var pickers = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        pickers.Children.Add(Labelled(L("Menu:"), modePicker));
        pickers.Children.Add(Labelled(L("File type:"), kindPicker));
        var reset = Ui.Button(L("Restore Defaults"), () => Prefs.ResetContents(Mode, Kind));
        reset.VerticalAlignment = VerticalAlignment.Center;
        reset.Margin = new Thickness(0, 0, 0, 8);
        pickers.Children.Add(reset);
        Add(pickers);

        list.Toggled += (index, shown) =>
        {
            if (index < 0 || index >= rows.Count) return;
            rows[index] = (rows[index].Title, shown);
            Save();
        };
        list.Moved += (from, to) =>
        {
            var row = rows[from];
            rows.RemoveAt(from);
            rows.Insert(to, row);
            var label = labels[from];
            labels.RemoveAt(from);
            labels.Insert(to, label);
            Save();
        };

        var previewColumn = new StackPanel();
        previewColumn.Children.Add(preview);
        previewColumn.Children.Add(Ui.TryItHint());
        // List and preview side by side; the preview goes under the list when the window is narrow.
        var split = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        split.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        split.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        list.VerticalAlignment = VerticalAlignment.Top;
        previewColumn.VerticalAlignment = VerticalAlignment.Top;
        previewColumn.HorizontalAlignment = HorizontalAlignment.Left;
        split.Children.Add(list);
        split.Children.Add(previewColumn);
        void layout(double width)
        {
            bool side = width >= ArrangeList.ListWidth + 20 + 340;
            Grid.SetColumn(previewColumn, side ? 1 : 0);
            Grid.SetRow(previewColumn, side ? 0 : 1);
            Grid.SetColumnSpan(previewColumn, side ? 1 : 2);
            previewColumn.Margin = side ? new Thickness(20, 0, 0, 0) : new Thickness(0, 16, 0, 0);
            preview.Width = Math.Clamp(side ? width - ArrangeList.ListWidth - 20 : width, 300, 520);
        }
        split.SizeChanged += (_, e) => layout(e.NewSize.Width);
        layout(760);
        Add(split);
        Load(crossfade: false);
    }

    static StackPanel Labelled(string label, ComboBox picker)
    {
        var text = Ui.Text(label, 14, wrap: false);
        text.VerticalAlignment = VerticalAlignment.Center;
        picker.Margin = new Thickness(8, 0, 0, 0);
        return new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 20, 8), Children = { text, picker } };
    }

    internal WheelMode Mode => WheelModes.All[Math.Max(modePicker.SelectedIndex, 0)];
    internal FileKind Kind => Kinds[Math.Max(kindPicker.SelectedIndex, 0)];
    internal ComboBox ModePicker => modePicker;
    internal ComboBox KindPicker => kindPicker;
    internal ArrangeList List => list;
    internal IReadOnlyList<(string Title, bool Shown)> Rows => rows;
    internal PreviewFrame Preview => preview;

    public override void Refresh() => Load(crossfade: true);

    void Load(bool crossfade)
    {
        var all = Catalog.AllItems(Mode, Kind);
        var hidden = Prefs.Hidden(Mode, Kind);
        var order = Prefs.Order(Mode, Kind);
        var sorted = all.Select((title, offset) => (title, offset))
            .OrderBy(x => order.IndexOf(x.title) is var i and >= 0 ? i : order.Count + x.offset)
            .Select(x => x.title)
            .ToList();
        var fresh = sorted.Select(t => (t, !hidden.Contains(t))).ToList();
        var freshLabels = sorted.Select(Catalog.DisplayTitle).ToList();
        // Reload only for a real change from elsewhere (another menu or file type, Restore Defaults, a renamed
        // preset). Reloading for every settings change would rebuild the rows under the pointer, mid-click.
        if (!fresh.SequenceEqual(rows) || !freshLabels.SequenceEqual(labels))
        {
            rows = fresh;
            labels = freshLabels;
            list.SetRows(labels, rows.Select(r => r.Shown).ToList());
        }
        UpdatePreview(crossfade);
    }

    void UpdatePreview(bool crossfade)
    {
        var titles = rows.Where(r => r.Shown).Select(r => r.Title).ToList();
        var key = (Prefs.Style, Mode, string.Join("\n", titles.Concat(titles.Select(Catalog.DisplayTitle))));
        if (key == previewKey) return;
        previewKey = key;
        preview.Show(MenuPreview.Create(key.Style, titles, Mode), crossfade);
    }

    /// Writes the rows over the stored arrangement, keeping what's stored for items whose tools are off right now.
    /// (Restore Defaults is the one thing that clears those too.)
    void Save()
    {
        var merged = Arrangement.Merged(rows, Prefs.Order(Mode, Kind), Prefs.Hidden(Mode, Kind));
        Prefs.SetArrangement(merged.Order, merged.Hidden, Mode, Kind);
        UpdatePreview(crossfade: true);
    }
}

/// The Customize rows: a checkbox and a grip each. Dragging anywhere but the checkbox lifts the row; the others make
/// room on springs as it passes, and on release it settles into its new place. Alt + Up / Alt + Down move the focused
/// row. Animation effects off: rows jump to their places (the dragged one still follows the pointer).
sealed class ArrangeList : Canvas
{
    public const double RowHeight = 40, Gap = 4, Pitch = RowHeight + Gap, ListWidth = 300;

    readonly List<Row> rows = [];
    readonly FrameDriver driver = new();
    int? dragging;
    double grabOffset;
    double pointerY;
    DropSlot slot;
    int dropAt;

    /// A row was ticked or unticked (its current index, shown).
    public event Action<int, bool>? Toggled;
    /// A row was dropped in a new place.
    public event Action<int, int>? Moved;

    public ArrangeList()
    {
        Width = ListWidth;
        ClipToBounds = false;
        FocusVisualStyle = null;
        driver.OnFrame = Step;
        Unloaded += (_, _) =>
        {
            if (dragging != null) EndDrag(commit: false);
            driver.Stop();
            foreach (var row in rows) row.Settle();
            Place();
        };
    }

    internal IReadOnlyList<Row> RowViews => rows;
    internal bool IsDragging => dragging != null;

    public void SetRows(IReadOnlyList<string> labels, IReadOnlyList<bool> shown)
    {
        if (dragging != null) EndDrag(commit: false);
        Children.Clear();
        rows.Clear();
        for (int i = 0; i < labels.Count; i++)
        {
            var row = new Row(this, labels[i], shown[i]);
            row.Y.Snap(i * Pitch);
            rows.Add(row);
            Children.Add(row);
        }
        Height = Math.Max(rows.Count * Pitch - Gap, 0);
        Place();
    }

    void Place()
    {
        foreach (var row in rows)
        {
            SetTop(row, row.Y.Value);
            row.ApplyLift();
        }
    }

    bool Step(double dt)
    {
        bool moving = false;
        var tuning = Prefs.Interaction.Tools;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (i != dragging) row.Y.Step(dt, tuning);
            row.Lift.Step(dt, tuning);
            moving |= (i != dragging && !row.Y.IsSettled) || !row.Lift.IsSettled;
        }
        if (dragging != null) moving |= AutoScroll(dt);
        Place();
        return moving || dragging != null;
    }

    void Kick()
    {
        if (Motion.Reduced || !IsLoaded)
        {
            for (int i = 0; i < rows.Count; i++)
                if (i != dragging) rows[i].Settle();
                else rows[i].Lift.Snap(rows[i].Lift.Target);
            Place();
            if (dragging is null) return;
        }
        driver.Start();
    }

    // MARK: Dragging

    internal void BeginDrag(int index, double y)
    {
        if (dragging != null || index < 0 || index >= rows.Count) return;
        dragging = index;
        dropAt = index;
        slot.Reset();
        slot.Move(index);
        pointerY = y;
        grabOffset = y - rows[index].Y.Value;
        SetZIndex(rows[index], 1);
        rows[index].Lift.Target = 1;
        Kick();
        // Takes the keyboard for the drag, so Esc can put the row back.
        Focusable = true;
        Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key != Key.Escape || dragging is not { } from) return;
        var row = rows[from];
        EndDrag(commit: false);
        row.ReleaseMouseCapture();
        e.Handled = true;
    }

    internal void DragTo(double y)
    {
        if (dragging is not { } from) return;
        pointerY = y;
        var row = rows[from];
        double top = Math.Clamp(y - grabOffset, -Pitch * 0.3, (rows.Count - 1) * Pitch + Pitch * 0.3);
        row.Y.Snap(top);
        int to = Math.Clamp((int)Math.Floor((top + RowHeight / 2) / Pitch), 0, rows.Count - 1);
        // A new place for the gap. (The Mac app taps the trackpad here; Windows has no haptics, and a sound for every
        // row passed would be noise, so the rows parting is the feedback.)
        slot.Move(to);
        dropAt = to;
        for (int i = 0; i < rows.Count; i++)
        {
            if (i == from) continue;
            int j = i < from ? i : i - 1;
            rows[i].Y.Target = (j >= to ? j + 1 : j) * Pitch;
        }
        Kick();
        Place();
    }

    internal void EndDrag(bool commit = true)
    {
        if (dragging is not { } from) return;
        dragging = null;
        int to = commit ? dropAt : from;
        var row = rows[from];
        row.Lift.Target = 0;
        rows.RemoveAt(from);
        rows.Insert(to, row);
        for (int i = 0; i < rows.Count; i++) rows[i].Y.Target = i * Pitch;
        SetZIndex(row, 0);
        slot.Reset();
        Kick();
        Place();
        if (to != from) Moved?.Invoke(from, to);
    }

    /// Moves a row by keyboard, sliding the others aside.
    internal void MoveRow(int from, int to)
    {
        if (dragging != null || from == to || from < 0 || to < 0 || from >= rows.Count || to >= rows.Count) return;
        var row = rows[from];
        rows.RemoveAt(from);
        rows.Insert(to, row);
        for (int i = 0; i < rows.Count; i++) rows[i].Y.Target = i * Pitch;
        Kick();
        Moved?.Invoke(from, to);
        row.Box.Focus();
    }

    /// Scrolls the page while a dragged row is held near the top or bottom of the visible area.
    bool AutoScroll(double dt)
    {
        if (FindScroller() is not { } scroller) return false;
        double y = Mouse.GetPosition(scroller).Y;
        const double edge = 36;
        double speed = y < edge ? -(edge - y) * 12 : y > scroller.ViewportHeight - edge ? (y - (scroller.ViewportHeight - edge)) * 12 : 0;
        if (speed == 0) return false;
        double before = scroller.VerticalOffset;
        scroller.ScrollToVerticalOffset(Math.Clamp(before + speed * dt, 0, scroller.ScrollableHeight));
        scroller.UpdateLayout();
        double delta = scroller.VerticalOffset - before;
        if (Math.Abs(delta) > 0.01) DragTo(pointerY + delta);
        return true;
    }

    ScrollViewer? FindScroller()
    {
        for (DependencyObject? node = this; node != null; node = VisualTreeHelper.GetParent(node))
            if (node is ScrollViewer s) return s;
        return null;
    }

    void RowToggled(Row row, bool shown) => Toggled?.Invoke(rows.IndexOf(row), shown);

    /// One row: a card with the item's checkbox and a grip.
    internal sealed class Row : Border
    {
        readonly ArrangeList owner;
        public Spring Y = new(0, response: 0.28, damping: 0.82);
        public Spring Lift = new(0, response: 0.2, damping: 0.8);
        public CheckBox Box { get; }
        readonly ScaleTransform scale = new();
        readonly DropShadowEffect shadow = new() { Direction = 270, Color = Colors.Black, Opacity = 0, BlurRadius = 12, ShadowDepth = 3 };

        public Row(ArrangeList owner, string label, bool shown)
        {
            this.owner = owner;
            Width = ListWidth;
            Height = RowHeight;
            CornerRadius = new CornerRadius(4);
            BorderThickness = new Thickness(1);
            Padding = new Thickness(12, 0, 8, 0);
            SetResourceReference(BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
            SetResourceReference(BorderBrushProperty, "CardStrokeColorDefaultBrush");
            RenderTransformOrigin = new Point(0.5, 0.5);
            RenderTransform = scale;
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Box = new CheckBox { Content = label, IsChecked = shown, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            Box.Click += (_, _) => owner.RowToggled(this, Box.IsChecked == true);
            Box.PreviewKeyDown += (_, e) =>
            {
                if (Keyboard.Modifiers != ModifierKeys.Alt) return;
                var key = e.Key == Key.System ? e.SystemKey : e.Key;
                int index = owner.rows.IndexOf(this);
                if (key == Key.Up) owner.MoveRow(index, index - 1);
                else if (key == Key.Down) owner.MoveRow(index, index + 1);
                else return;
                e.Handled = true;
            };
            grid.Children.Add(Box);
            var grip = Ui.Glyph("", 14, Tone.Tertiary); // GripperBarHorizontal
            grip.Width = 28;
            grip.Height = RowHeight - 2;
            grip.TextAlignment = TextAlignment.Center;
            grip.Padding = new Thickness(0, 11, 0, 0);
            grip.Background = Brushes.Transparent;
            grip.Cursor = Cursors.SizeNS;
            grip.ToolTip = L("Drag to reorder");
            Grid.SetColumn(grip, 1);
            grid.Children.Add(grip);
            Child = grid;
            Cursor = Cursors.SizeNS;
            Box.Cursor = Cursors.Arrow;
            AutomationProperties.SetName(Box, label);
            AutomationProperties.SetHelpText(Box, L("Drag to reorder"));
        }

        public void Settle()
        {
            Y.Snap(Y.Target);
            Lift.Snap(Lift.Target);
        }

        /// Lifted: a little larger, on a shadow.
        public void ApplyLift()
        {
            double lift = Math.Clamp(Lift.Value, 0, 1.2);
            double k = 1 + 0.02 * lift;
            scale.ScaleX = k;
            scale.ScaleY = k;
            if (lift < 0.01) Effect = null;
            else
            {
                shadow.Opacity = (Native.DarkMode ? 0.4 : 0.18) * Math.Clamp(lift, 0, 1);
                Effect = shadow;
            }
        }

        // Anywhere but the checkbox starts a drag (the checkbox handles its own clicks), so reordering doesn't
        // fight with ticking.
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (e.Handled) return;
            owner.BeginDrag(owner.rows.IndexOf(this), e.GetPosition(owner).Y);
            CaptureMouse();
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (IsMouseCaptured && owner.dragging != null) owner.DragTo(e.GetPosition(owner).Y);
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (!IsMouseCaptured) return;
            ReleaseMouseCapture(); // ends the drag in OnLostMouseCapture
            e.Handled = true;
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);
            if (owner.dragging != null) owner.EndDrag();
        }
    }
}
