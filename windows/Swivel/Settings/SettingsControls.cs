using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace Swivel;

// Shared building blocks of the settings window: theme-aware text, Windows 11 setting cards, the toggle switch, the
// feature header, the status dot and the springs that move them. (The Mac app's Pane, CardView, FeatureHeader and
// StatusDot in AppWindow.swift / BundleWindow.swift.)

/// Text colours of the Fluent theme.
enum Tone { Primary, Secondary, Tertiary, Accent, Critical, Caution, Success }

static class Ui
{
    public static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    static string Key(Tone tone) => tone switch
    {
        Tone.Secondary => "TextFillColorSecondaryBrush",
        Tone.Tertiary => "TextFillColorTertiaryBrush",
        Tone.Accent => "AccentTextFillColorPrimaryBrush",
        Tone.Critical => "SystemFillColorCriticalBrush",
        Tone.Caution => "SystemFillColorCautionBrush",
        Tone.Success => "SystemFillColorSuccessBrush",
        _ => "TextFillColorPrimaryBrush",
    };

    /// A label that follows the theme. `text` is already translated.
    public static TextBlock Text(string text, double size = 14, FontWeight? weight = null, Tone tone = Tone.Primary, bool wrap = true)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight ?? FontWeights.Normal,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
        };
        SetTone(block, tone);
        return block;
    }

    public static void SetTone(TextBlock block, Tone tone) => block.SetResourceReference(TextBlock.ForegroundProperty, Key(tone));

    /// A Segoe Fluent Icons glyph.
    public static TextBlock Glyph(string glyph, double size = 16, Tone tone = Tone.Primary)
    {
        var block = new TextBlock
        {
            Text = glyph,
            FontFamily = IconFont,
            FontSize = size,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        SetTone(block, tone);
        return block;
    }

    /// Page title, 28 px semibold like Windows Settings.
    public static TextBlock Title(string text) => Text(text, 28, FontWeights.SemiBold);

    /// A heading over a group of cards.
    public static TextBlock Section(string text)
    {
        var block = Text(text, 14, FontWeights.SemiBold);
        block.Margin = new Thickness(1, 22, 0, 6);
        return block;
    }

    public static Button Button(string title, Action action, bool accent = false)
    {
        var button = new Button { Content = title, MinWidth = 96, Padding = new Thickness(12, 5, 12, 6) };
        if (accent) button.SetResourceReference(FrameworkElement.StyleProperty, "AccentButtonStyle");
        button.Click += (_, _) => action();
        return button;
    }

    /// A horizontal row of controls with Windows' 8 px spacing.
    public static StackPanel Row(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < children.Length; i++)
        {
            if (children[i] is FrameworkElement fe && i > 0) fe.Margin = new Thickness(8, fe.Margin.Top, fe.Margin.Right, fe.Margin.Bottom);
            row.Children.Add(children[i]);
        }
        return row;
    }

    public static StackPanel Column(double spacing, params UIElement[] children)
    {
        var column = new StackPanel { Orientation = Orientation.Vertical };
        for (int i = 0; i < children.Length; i++)
        {
            if (children[i] is FrameworkElement fe && i > 0) fe.Margin = new Thickness(fe.Margin.Left, spacing, fe.Margin.Right, fe.Margin.Bottom);
            column.Children.Add(children[i]);
        }
        return column;
    }

    /// A theme colour for custom drawing, with a light-theme fallback when no theme is loaded.
    public static Color Color(FrameworkElement element, string key, Color fallback) => element.TryFindResource(key) switch
    {
        SolidColorBrush brush => brush.Color,
        Color color => color,
        _ => fallback,
    };

    public static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// The colon after a form label, as the Mac app writes it (full-width in Chinese and Japanese).
    public static string Colon(string label) => label + (Languages.Resolved == Language.En ? ":" : "：");

    /// The "Move the pointer over the menu to try it" line under a live menu preview.
    public static StackPanel TryItHint()
    {
        var icon = Glyph("", 12, Tone.Tertiary); // TouchPointer
        var text = Text(L("Move the pointer over the menu to try it"), 12, tone: Tone.Secondary);
        text.Margin = new Thickness(6, 0, 0, 0);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 8, 0, 0) };
        row.Children.Add(icon);
        row.Children.Add(text);
        return row;
    }

    /// Shakes a field that refused its input (the Mac app's ToolUI.refuse): a sideways nudge that settles on a spring.
    public static void Shake(FrameworkElement field)
    {
        Feedback.Refuse();
        if (Motion.Reduced || !field.IsLoaded) return;
        var shift = field.RenderTransform as TranslateTransform;
        if (shift is null)
        {
            shift = new TranslateTransform();
            field.RenderTransform = shift;
        }
        var spring = new Spring(0, response: 0.18, damping: 0.6) { Velocity = 260 };
        var driver = new FrameDriver();
        driver.OnFrame = dt =>
        {
            spring.Step(dt);
            shift.X = spring.Value;
            return !spring.IsSettled;
        };
        driver.Start();
    }

    /// Runs `action` once the dispatcher is idle (after layout), so a Prefs write never re-enters a refresh.
    public static void Later(this Dispatcher dispatcher, Action action) => dispatcher.BeginInvoke(action, DispatcherPriority.Background);

    /// Waits on the dispatcher without blocking it (for scheduling, never for animation).
    public static void After(this Dispatcher dispatcher, double seconds, Action action)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
    }
}

/// Re-reads Prefs whenever they change while `element` is on screen (and once each time it appears), on its
/// dispatcher. Unsubscribes when it leaves, so pages that are not showing cost nothing and closed windows are freed.
static class PrefsWatch
{
    public static void Attach(FrameworkElement element, Action refresh)
    {
        bool subscribed = false;
        void changed() => element.Dispatcher.BeginInvoke(() =>
        {
            if (element.IsLoaded) refresh();
        });
        element.Loaded += (_, _) =>
        {
            if (!subscribed)
            {
                Prefs.Changed += changed;
                subscribed = true;
            }
            refresh();
        };
        element.Unloaded += (_, _) =>
        {
            if (!subscribed) return;
            Prefs.Changed -= changed;
            subscribed = false;
        };
    }
}

/// Custom-drawn controls resolve theme colours when they draw; this redraws them when the theme (light/dark, accent)
/// changes, by watching two theme resources.
static class ThemeWatch
{
    public static readonly DependencyProperty TextProbeProperty = DependencyProperty.RegisterAttached(
        "TextProbe", typeof(object), typeof(ThemeWatch), new PropertyMetadata(null, Changed));

    public static readonly DependencyProperty AccentProbeProperty = DependencyProperty.RegisterAttached(
        "AccentProbe", typeof(object), typeof(ThemeWatch), new PropertyMetadata(null, Changed));

    static readonly DependencyProperty CallbackProperty = DependencyProperty.RegisterAttached(
        "Callback", typeof(Action), typeof(ThemeWatch));

    public static void Attach(FrameworkElement element, Action? changed = null)
    {
        if (changed != null) element.SetValue(CallbackProperty, changed);
        element.SetResourceReference(TextProbeProperty, "TextFillColorPrimaryBrush");
        element.SetResourceReference(AccentProbeProperty, "AccentFillColorDefaultBrush");
    }

    static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d.GetValue(CallbackProperty) is Action callback) callback();
        (d as UIElement)?.InvalidateVisual();
    }
}

/// A few springs that animate one element, stepped on a FrameDriver while it is on screen. Off screen, or with
/// animation effects turned off, every spring jumps to its target. Retargeting mid-flight carries the velocity over.
sealed class SpringGroup
{
    readonly Spring[] springs;
    readonly FrameworkElement owner;
    readonly Action apply;
    readonly FrameDriver driver = new();

    public SpringGroup(FrameworkElement owner, Action apply, params Spring[] springs)
    {
        this.owner = owner;
        this.apply = apply;
        this.springs = springs;
        driver.OnFrame = dt =>
        {
            bool moving = false;
            for (int i = 0; i < this.springs.Length; i++)
            {
                this.springs[i].Step(dt);
                moving |= !this.springs[i].IsSettled;
            }
            this.apply();
            return moving;
        };
        owner.Unloaded += (_, _) =>
        {
            driver.Stop();
            Settle();
        };
    }

    public ref Spring this[int index] => ref springs[index];

    public bool IsRunning => driver.IsRunning;

    /// Starts moving toward the targets (or jumps there when nothing should move).
    public void Kick()
    {
        if (!Motion.Reduced && owner.IsLoaded && owner.IsVisible) driver.Start();
        else Settle();
    }

    public void Settle()
    {
        for (int i = 0; i < springs.Length; i++) springs[i].Snap(springs[i].Target);
        apply();
    }
}

/// The Windows 11 toggle switch (WinUI's ToggleSwitch, which WPF lacks), with the "On" / "Off" text on its left the
/// way Windows Settings shows it. The knob slides on a spring, colours fade critically damped, the knob grows under
/// the pointer and stretches while pressed (on mouse-down). Flips on release, Space, or through UI Automation.
sealed class ToggleSwitch : ToggleButton
{
    const int Knob = 0, Onness = 1, Hover = 2, Press = 3;
    const double TrackWidth = 40, TrackHeight = 20, TextGap = 12;

    readonly SpringGroup motion;
    bool syncing;

    /// Raised when the user flips the switch (mouse, keyboard or UI Automation), not when `SetQuietly` updates it.
    public event Action<bool>? Toggled;

    public bool ShowStateText { get; init; } = true;

    static ToggleSwitch()
    {
        // No theme template: the switch draws itself.
        DefaultStyleKeyProperty.OverrideMetadata(typeof(ToggleSwitch), new FrameworkPropertyMetadata(typeof(ToggleSwitch)));
    }

    public ToggleSwitch()
    {
        Focusable = true;
        IsTabStop = true;
        VerticalAlignment = VerticalAlignment.Center;
        SetResourceReference(FocusVisualStyleProperty, "DefaultControlFocusVisualStyle");
        motion = new SpringGroup(this, InvalidateVisual,
            new Spring(0, response: 0.25, damping: 0.78),
            new Spring(0, response: 0.15, damping: 1),
            new Spring(0, response: 0.12, damping: 1),
            new Spring(0, response: 0.09, damping: 1));
        ThemeWatch.Attach(this);
        IsEnabledChanged += (_, _) => InvalidateVisual();
    }

    public bool IsOn => IsChecked == true;

    /// Shows a state that changed elsewhere (Prefs), animating if on screen, without raising `Toggled`.
    public void SetQuietly(bool on)
    {
        if (IsOn == on) return;
        syncing = true;
        IsChecked = on;
        syncing = false;
    }

    /// The same as a click (tests, and the card around the switch).
    internal void Flip() => OnClick();

    protected override void OnChecked(RoutedEventArgs e)
    {
        base.OnChecked(e);
        StateChanged(true);
    }

    protected override void OnUnchecked(RoutedEventArgs e)
    {
        base.OnUnchecked(e);
        StateChanged(false);
    }

    void StateChanged(bool on)
    {
        motion[Knob].Target = on ? 1 : 0;
        motion[Onness].Target = on ? 1 : 0;
        motion.Kick();
        InvalidateMeasure();
        if (!syncing) Toggled?.Invoke(on);
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        double v = IsOn ? 1 : 0;
        motion[Knob].Snap(v);
        motion[Onness].Snap(v);
    }

    protected override void OnIsPressedChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnIsPressedChanged(e);
        // In fast and firm; back out with a little give.
        motion[Press].Response = IsPressed ? 0.09 : 0.2;
        motion[Press].Target = IsPressed ? 1 : 0;
        motion.Kick();
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        SetHover(true);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        SetHover(false);
    }

    void SetHover(bool on)
    {
        motion[Hover].Response = on ? 0.12 : 0.1; // exits are quicker than entrances
        motion[Hover].Target = on ? 1 : 0;
        motion.Kick();
    }

    FormattedText StateText(bool on) => new(L(on ? "On" : "Off"), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
        new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 14,
        Ui.Brush(Ui.Color(this, IsEnabled ? "TextFillColorPrimaryBrush" : "TextFillColorDisabledBrush", Colors.Black)),
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    double TextReserve => ShowStateText ? Math.Ceiling(Math.Max(StateText(true).Width, StateText(false).Width)) + TextGap : 0;

    protected override Size MeasureOverride(Size constraint) => new(TextReserve + TrackWidth, 32);

    protected override Size ArrangeOverride(Size arrangeBounds) => arrangeBounds;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // the whole control takes the click
        if (!IsEnabled) dc.PushOpacity(0.45);
        double hover = Math.Clamp(motion[Hover].Value, 0, 1), press = Math.Clamp(motion[Press].Value, 0, 1);
        double on = Math.Clamp(motion[Onness].Value, 0, 1), knob = motion[Knob].Value;

        if (ShowStateText)
        {
            var text = StateText(IsOn);
            dc.DrawText(text, new Point(0, (h - text.Height) / 2));
        }

        double x0 = w - TrackWidth, y0 = Math.Round((h - TrackHeight) / 2);
        var offFill = Mix.Lerp(Ui.Color(this, "ControlAltFillColorSecondaryBrush", Color.FromArgb(0x06, 0, 0, 0)),
            Ui.Color(this, "ControlAltFillColorTertiaryBrush", Color.FromArgb(0x0F, 0, 0, 0)), Math.Max(hover, press));
        var onFill = Mix.Lerp(Ui.Color(this, "AccentFillColorDefaultBrush", Color.FromRgb(0x00, 0x5F, 0xB8)),
            Ui.Color(this, "AccentFillColorSecondaryBrush", Color.FromArgb(0xE6, 0x00, 0x5F, 0xB8)), hover);
        onFill = Mix.Lerp(onFill, Ui.Color(this, "AccentFillColorTertiaryBrush", Color.FromArgb(0xCC, 0x00, 0x5F, 0xB8)), press);
        var stroke = Mix.Lerp(Ui.Color(this, "ControlStrongStrokeColorDefaultBrush", Color.FromArgb(0x72, 0, 0, 0)), onFill, on);
        var fill = Mix.Lerp(offFill, onFill, on);
        var track = new Rect(x0 + 0.5, y0 + 0.5, TrackWidth - 1, TrackHeight - 1);
        dc.DrawRoundedRectangle(Ui.Brush(fill), new Pen(Ui.Brush(stroke), 1), track, (TrackHeight - 1) / 2, (TrackHeight - 1) / 2);

        // The knob: 12 px at rest, 14 px under the pointer, stretched to 17 × 14 while pressed.
        double size = 12 + 2 * Math.Max(hover, press);
        double width = size + 3 * press;
        double cx = x0 + 10 + 20 * knob;
        var knobColor = Mix.Lerp(Ui.Color(this, "TextFillColorSecondaryBrush", Color.FromArgb(0x9E, 0, 0, 0)),
            Ui.Color(this, "TextOnAccentFillColorPrimaryBrush", Colors.White), on);
        dc.DrawRoundedRectangle(Ui.Brush(knobColor), null, new Rect(cx - width / 2, y0 + TrackHeight / 2 - size / 2, width, size), size / 2, size / 2);
        if (!IsEnabled) dc.Pop();
    }
}

/// A feature's status dot in the sidebar, like PowerToys' enabled indicator. It fades with the switch.
sealed class StatusDot : FrameworkElement
{
    readonly SpringGroup motion;
    bool configured;

    public bool IsOn { get; private set; }

    public StatusDot()
    {
        Width = 7;
        Height = 7;
        VerticalAlignment = VerticalAlignment.Center;
        motion = new SpringGroup(this, InvalidateVisual, new Spring(0, response: 0.2, damping: 1));
        ThemeWatch.Attach(this);
    }

    public void SetOn(bool on, bool animated)
    {
        if (on == IsOn && configured) return;
        IsOn = on;
        ToolTip = L(on ? "On" : "Off");
        AutomationProperties.SetItemStatus(this, L(on ? "On" : "Off"));
        motion[0].Target = on ? 1 : 0;
        if (animated && configured) motion.Kick();
        else motion.Settle();
        configured = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var color = Mix.Lerp(Ui.Color(this, "TextFillColorTertiaryBrush", Color.FromArgb(0x72, 0, 0, 0)),
            Ui.Color(this, "SystemFillColorSuccessBrush", Color.FromRgb(0x0F, 0x7B, 0x0F)), Math.Clamp(motion[0].Value, 0, 1));
        dc.DrawEllipse(Ui.Brush(color), null, new Point(ActualWidth / 2, ActualHeight / 2), 3.5, 3.5);
    }
}

/// A Windows 11 settings card: optional icon, title and description on the left, a control on the right. A
/// clickable card (one that opens a page) lights up under the pointer and shows a chevron.
sealed class SettingsCard : Border
{
    readonly SpringGroup? motion;
    readonly Action? onClick;
    readonly Grid grid = new();
    public TextBlock? TitleBlock { get; }
    public TextBlock? DescriptionBlock { get; }

    public SettingsCard(string? glyph, string? title, string? description, UIElement? action = null, Action? onClick = null)
    {
        this.onClick = onClick;
        CornerRadius = new CornerRadius(6);
        BorderThickness = new Thickness(1);
        Padding = new Thickness(16, 12, 16, 12);
        MinHeight = 68;
        SetResourceReference(BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        SetResourceReference(BorderBrushProperty, "CardStrokeColorDefaultBrush");

        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (glyph != null)
        {
            var icon = Ui.Glyph(glyph, 20);
            icon.Margin = new Thickness(2, 0, 18, 0);
            grid.Children.Add(icon);
        }
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        if (title != null)
        {
            TitleBlock = Ui.Text(title, 14);
            texts.Children.Add(TitleBlock);
        }
        if (description != null)
        {
            DescriptionBlock = Ui.Text(description, 12, tone: Tone.Secondary);
            DescriptionBlock.Margin = new Thickness(0, title != null ? 2 : 0, 0, 0);
            texts.Children.Add(DescriptionBlock);
        }
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        if (action != null)
        {
            if (action is FrameworkElement fe)
            {
                fe.VerticalAlignment = VerticalAlignment.Center;
                fe.Margin = new Thickness(16, 0, 0, 0);
            }
            Grid.SetColumn(action, 2);
            grid.Children.Add(action);
            if (title != null && action is Control control && AutomationProperties.GetName(control) is null or "")
                AutomationProperties.SetName(control, title);
        }
        if (onClick != null)
        {
            var chevron = Ui.Glyph("", 12, Tone.Secondary); // ChevronRight
            chevron.Margin = new Thickness(16, 0, 2, 0);
            Grid.SetColumn(chevron, 3);
            grid.Children.Add(chevron);
            Focusable = true;
            SetResourceReference(FocusVisualStyleProperty, "DefaultControlFocusVisualStyle");
            motion = new SpringGroup(this, ApplyHover, new Spring(0, response: 0.12, damping: 1), new Spring(0, response: 0.09, damping: 1));
            ThemeWatch.Attach(this, ApplyHover);
            AutomationProperties.SetName(this, title ?? "");
            AutomationProperties.SetHelpText(this, description ?? "");
        }
        Child = grid;
    }

    /// Clicks on the card's own control (a switch, a button) belong to that control, not the card.
    bool OnOwnControl(DependencyObject? source)
    {
        for (var node = source; node != null && node != this; node = VisualTreeHelper.GetParent(node))
            if (node is ButtonBase or Selector or TextBoxBase) return true;
        return false;
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        if (motion is null) return;
        motion[0].Target = 1;
        motion.Kick();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (motion is null) return;
        motion[0].Target = 0;
        motion[1].Target = 0;
        motion.Kick();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (motion is null || OnOwnControl(e.OriginalSource as DependencyObject)) return;
        motion[1].Target = 1;
        motion.Kick();
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (motion is null || !IsMouseCaptured) return;
        ReleaseMouseCapture();
        motion[1].Target = 0;
        motion.Kick();
        var p = e.GetPosition(this);
        if (p.X >= 0 && p.Y >= 0 && p.X <= ActualWidth && p.Y <= ActualHeight) onClick?.Invoke();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (onClick != null && e.OriginalSource == this && e.Key is Key.Enter or Key.Space)
        {
            onClick();
            e.Handled = true;
        }
    }

    void ApplyHover()
    {
        if (motion is null) return;
        double h = Math.Clamp(motion[0].Value, 0, 1), p = Math.Clamp(motion[1].Value, 0, 1);
        var rest = Ui.Color(this, "CardBackgroundFillColorDefaultBrush", Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));
        var over = Ui.Color(this, "ControlFillColorSecondaryBrush", Color.FromArgb(0x80, 0xF9, 0xF9, 0xF9));
        var down = Ui.Color(this, "ControlFillColorTertiaryBrush", Color.FromArgb(0x4D, 0xF9, 0xF9, 0xF9));
        if (h == 0 && p == 0) SetResourceReference(BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        else Background = Ui.Brush(Mix.Lerp(Mix.Lerp(rest, over, h), down, p));
    }

    protected override AutomationPeer OnCreateAutomationPeer() => onClick != null ? new ClickableCardPeer(this) : base.OnCreateAutomationPeer();

    internal void Invoke() => onClick?.Invoke();

    sealed class ClickableCardPeer(SettingsCard card) : FrameworkElementAutomationPeer(card), System.Windows.Automation.Provider.IInvokeProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;
        protected override string GetClassNameCore() => "SettingsCard";
        public override object? GetPattern(PatternInterface pattern) => pattern == PatternInterface.Invoke ? this : base.GetPattern(pattern);
        public void Invoke() => card.Invoke();
    }
}

/// A group of cards with 4 px between them, like Windows Settings.
sealed class CardGroup : StackPanel
{
    public CardGroup(params UIElement[] cards)
    {
        foreach (var card in cards) Add(card);
    }

    public void Add(UIElement card)
    {
        if (card is FrameworkElement fe && Children.Count > 0) fe.Margin = new Thickness(0, 4, 0, 0);
        Children.Add(card);
    }
}

/// Icon, description and the on/off switch for one feature (the Mac app's FeatureHeader). `compact` is the Overview
/// card version, with the feature's title and a link to its page.
sealed class FeatureHeader : Border
{
    readonly Feature feature;
    public ToggleSwitch Switch { get; } = new();

    public FeatureHeader(Feature feature, bool compact = false, Action? open = null)
    {
        this.feature = feature;
        CornerRadius = new CornerRadius(6);
        BorderThickness = new Thickness(1);
        Padding = compact ? new Thickness(16, 14, 16, 14) : new Thickness(20, 18, 20, 18);
        SetResourceReference(BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        SetResourceReference(BorderBrushProperty, "CardStrokeColorDefaultBrush");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        double tile = compact ? 40 : 52;
        var iconTile = new Border
        {
            Width = tile,
            Height = tile,
            CornerRadius = new CornerRadius(8),
            Child = Ui.Glyph(feature.Glyph(), compact ? 20 : 26, Tone.Accent),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, compact ? 14 : 18, 0),
        };
        iconTile.SetResourceReference(BackgroundProperty, "SubtleFillColorSecondaryBrush");
        grid.Children.Add(iconTile);

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        if (compact) texts.Children.Add(Ui.Text(feature.Title(), 15, FontWeights.SemiBold));
        var blurb = Ui.Text(feature.Blurb(), compact ? 12 : 14, tone: compact ? Tone.Secondary : Tone.Primary);
        if (compact) blurb.Margin = new Thickness(0, 2, 0, 0);
        texts.Children.Add(blurb);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        Switch.Margin = new Thickness(20, 0, 0, 0);
        AutomationProperties.SetName(Switch, feature.Title());
        Switch.ToolTip = feature.Title();
        Switch.Toggled += on =>
        {
            feature.SetOn(on);
            Feedback.Toggle(feature.IsOn());
        };
        Grid.SetColumn(Switch, 2);
        grid.Children.Add(Switch);

        if (open != null)
        {
            var settings = new Button
            {
                Content = Ui.Glyph("", 12), // ChevronRight
                Width = 36,
                Height = 32,
                Padding = new Thickness(0),
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = L("Settings…"),
            };
            AutomationProperties.SetName(settings, L("Settings…"));
            settings.Click += (_, _) => open();
            Grid.SetColumn(settings, 3);
            grid.Children.Add(settings);
        }
        Child = grid;
        Switch.SetQuietly(feature.IsOn());
        PrefsWatch.Attach(this, Refresh);
    }

    void Refresh() => Switch.SetQuietly(feature.IsOn());
}

/// A soft desktop-like backdrop so the translucent menu previews look the way they do on screen.
class WallpaperBackdrop : Border
{
    public WallpaperBackdrop()
    {
        CornerRadius = new CornerRadius(10);
        ClipToBounds = true;
        // The Mac app's three-stop gradient at -60°.
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0.21, 0),
            EndPoint = new Point(0.79, 1),
            GradientStops =
            {
                new GradientStop(Color.FromRgb(0x78, 0xA8, 0xE6), 0),
                new GradientStop(Color.FromRgb(0xB8, 0xC7, 0xF2), 0.5),
                new GradientStop(Color.FromRgb(0xFA, 0xCC, 0x9E), 1),
            },
        };
        brush.Freeze();
        Background = brush;
    }
}

/// A scaled-down live menu on the wallpaper. Swapping in a changed menu cross-fades (critically damped), so edits in
/// Customize don't pop.
sealed class PreviewFrame : WallpaperBackdrop
{
    readonly Grid layers = new();
    FrameworkElement? current;
    readonly List<(FrameworkElement View, Spring Fade)> fading = [];
    readonly FrameDriver driver = new();
    Spring incoming = new(1, response: 0.15, damping: 1);

    public PreviewFrame()
    {
        Child = layers;
        driver.OnFrame = Step;
        Unloaded += (_, _) => Finish();
    }

    public FrameworkElement? Preview => current;

    public void Show(FrameworkElement preview, bool crossfade)
    {
        // MenuPreview's host fits the menu inside itself (10 px inset, never enlarged), so it simply fills the frame.
        preview.HorizontalAlignment = HorizontalAlignment.Stretch;
        preview.VerticalAlignment = VerticalAlignment.Stretch;
        var box = new Border { Child = preview };
        if (crossfade && current != null && IsLoaded && IsVisible && !Motion.Reduced)
        {
            var old = (FrameworkElement)current.Parent;
            fading.Add((old, new Spring(old.Opacity, response: 0.15, damping: 1) { Target = 0 }));
            old.IsHitTestVisible = false;
            incoming = new Spring(0, response: 0.15, damping: 1) { Target = 1 };
            box.Opacity = 0;
            layers.Children.Add(box);
            current = preview;
            driver.Start();
        }
        else
        {
            Finish();
            layers.Children.Clear();
            layers.Children.Add(box);
            current = preview;
        }
    }

    bool Step(double dt)
    {
        incoming.Step(dt);
        if (current?.Parent is FrameworkElement box) box.Opacity = incoming.Value;
        for (int i = fading.Count - 1; i >= 0; i--)
        {
            var (view, fade) = fading[i];
            fade.Step(dt);
            view.Opacity = Math.Max(fade.Value, 0);
            if (fade.IsSettled)
            {
                layers.Children.Remove(view);
                fading.RemoveAt(i);
            }
            else fading[i] = (view, fade);
        }
        return fading.Count > 0 || !incoming.IsSettled;
    }

    void Finish()
    {
        driver.Stop();
        foreach (var (view, _) in fading) layers.Children.Remove(view);
        fading.Clear();
        incoming.Snap(1);
        if (current?.Parent is FrameworkElement box) box.Opacity = 1;
    }
}

/// Tabs with a sliding underline, like WinUI's SelectorBar (the Mac app's segmented control on the Wheel page).
sealed class SelectorBar : Grid
{
    readonly StackPanel items = new() { Orientation = Orientation.Horizontal };
    readonly Border pill = new() { Height = 3, CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom };
    readonly SpringGroup motion;
    readonly List<TextBlock> labels = [];
    bool placed;

    public int Selected { get; private set; } = -1;
    public event Action<int>? SelectionChanged;

    public SelectorBar(IReadOnlyList<string> titles)
    {
        Children.Add(items);
        Children.Add(pill);
        pill.SetResourceReference(Border.BackgroundProperty, "AccentFillColorDefaultBrush");
        pill.IsHitTestVisible = false;
        motion = new SpringGroup(this, PlacePill, new Spring(0, response: 0.3, damping: 0.8), new Spring(16, response: 0.3, damping: 0.8));
        for (int i = 0; i < titles.Count; i++)
        {
            int index = i;
            var label = Ui.Text(titles[i], 14, wrap: false);
            labels.Add(label);
            var item = new Border
            {
                Child = label,
                Padding = new Thickness(12, 8, 12, 10),
                Background = Brushes.Transparent,
                Focusable = true,
                Cursor = Cursors.Hand,
            };
            item.SetResourceReference(FocusVisualStyleProperty, "DefaultControlFocusVisualStyle");
            AutomationProperties.SetName(item, titles[i]);
            item.MouseLeftButtonDown += (_, e) =>
            {
                Select(index);
                e.Handled = true;
            };
            item.KeyDown += (_, e) =>
            {
                int next = e.Key switch { Key.Left => index - 1, Key.Right => index + 1, Key.Enter or Key.Space => index, _ => -2 };
                if (next < -1) return;
                next = Math.Clamp(next, 0, titles.Count - 1);
                Select(next);
                (items.Children[next] as UIElement)?.Focus();
                e.Handled = true;
            };
            items.Children.Add(item);
        }
        LayoutUpdated += (_, _) =>
        {
            if (Selected >= 0) Retarget(animated: placed);
        };
    }

    public void Select(int index, bool notify = true)
    {
        if (index == Selected) return;
        Selected = index;
        for (int i = 0; i < labels.Count; i++)
        {
            labels[i].FontWeight = i == index ? FontWeights.SemiBold : FontWeights.Normal;
            Ui.SetTone(labels[i], i == index ? Tone.Primary : Tone.Secondary);
        }
        Retarget(animated: placed);
        if (notify) SelectionChanged?.Invoke(index);
    }

    void Retarget(bool animated)
    {
        if (Selected < 0 || items.Children[Selected] is not FrameworkElement item || item.ActualWidth == 0) return;
        double x = item.TranslatePoint(new Point(0, 0), this).X + item.ActualWidth / 2 - 8;
        if (Math.Abs(motion[0].Target - x) < 0.5 && placed) return;
        motion[0].Target = x;
        motion[1].Target = 16;
        if (animated)
        {
            // Stretch a little toward the destination while sliding, like Windows' selection indicators.
            motion[1].Velocity = 60;
            motion.Kick();
        }
        else motion.Settle();
        placed = true;
    }

    void PlacePill()
    {
        pill.Width = Math.Max(motion[1].Value, 4);
        pill.Margin = new Thickness(motion[0].Value - (pill.Width - 16) / 2, 0, 0, 0);
    }
}
