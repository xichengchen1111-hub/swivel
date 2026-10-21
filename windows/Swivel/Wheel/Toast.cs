using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace Swivel;

enum ToastTint { Accent, Secondary, Red }

/// A small message near the bottom of the screen (or by the pointer) reporting what happened. Clicking one with files
/// reveals them in File Explorer.
///
/// It rises into place and sinks away when it leaves; a toast replaced while visible keeps its place and only changes
/// its text, width and symbol. Hovering keeps it on screen. All motion is springs on a FrameDriver.
sealed class Toast
{
    public static Toast Shared { get; } = new();

    const double Height = 48, MaxWidth = 520, MinWidth = 180, Margin = 24;

    ToastWindow? window;
    IReadOnlyList<string> files = [];
    DispatcherTimer? hideTimer;
    double? autoHideDelay;
    /// Changes whenever the toast is shown or hidden, so an older fade-out can't hide a newer toast.
    int generation;
    string? shownSymbol;
    bool isSpinning;

    // Motion.
    Spring alpha = new(0, 0.29, 1);
    Spring rise = new(0, 0.29, 1);      // DIPs below the resting place
    Spring width = new(MinWidth, 0.26, 1);
    Spring press = new(1, 0.08, 1);
    Spring iconScale = new(1, 0.3, 0.6);
    double spin;                          // degrees
    double shakeClock = double.PositiveInfinity;
    bool hiding;
    readonly FrameDriver driver = new();

    Toast()
    {
        driver.OnFrame = Advance;
    }

    /// Shows a message. `near` (physical screen pixels, e.g. from PointToScreen or the cursor) places it just above
    /// that point instead of the bottom of the screen, with a small shake, for "that didn't work" moments.
    /// `spinning` animates the symbol while busy. `symbol` is a semantic name: "ok", "error", "info", "cancel",
    /// "busy", "wheel", "on", "off", "nosign", "copy", "clothesline" (SF Symbol names from the Mac code work too);
    /// unknown names show a neutral icon.
    public void Show(string message, string symbol, IReadOnlyList<string>? files = null, ToastTint tint = ToastTint.Accent,
                     bool autoHide = true, Point? near = null, bool spinning = false)
    {
        if (Application.Current?.Dispatcher is { } d && !d.CheckAccess())
        {
            d.BeginInvoke(() => Show(message, symbol, files, tint, autoHide, near, spinning));
            return;
        }
        var w = window ??= new ToastWindow(this);
        generation++;
        hiding = false;
        this.files = files ?? [];
        bool dark = Native.DarkMode;
        w.Apply(dark);
        w.Pill.Cursor = this.files.Count > 0 ? Cursors.Hand : null;
        var text = this.files.Count == 0 ? L(message) : L(message) + "  ·  " + L("Show");
        w.IconText.Foreground = Look.Brush(TintColor(tint, dark));
        SetSymbol(symbol, spinning);

        double textWidth = ToastWindow.Measure(text, w);
        double target = Math.Min(Math.Max(textWidth + 66, MinWidth), MaxWidth);
        w.Label.Text = ToastWindow.TruncateMiddle(text, MaxWidth - 66, w);

        var pointer = near is { } p ? new Native.POINT { X = (int)Math.Round(p.X), Y = (int)Math.Round(p.Y) } : Native.CursorPixels();
        var monitor = Screens.At(pointer);
        var area = monitor.WorkDips;
        // The window is as wide as the widest toast; the pill inside animates its width.
        double windowWidth = MaxWidth + 2 * Margin, windowHeight = Height + 2 * Margin;
        double centreX = area.Left + area.Width / 2, top = area.Bottom - 70 - Height;
        if (near != null)
        {
            var at = monitor.ToDips(pointer);
            centreX = Math.Min(Math.Max(at.X, area.Left + 8 + target / 2), area.Right - 8 - target / 2);
            top = Math.Max(at.Y - 26 - Height, area.Top + 8);
        }
        var frame = new Rect(centreX - windowWidth / 2, top - Margin, windowWidth, windowHeight);

        bool reduced = Motion.Reduced;
        if (w.IsVisible && alpha.Value > 0.5 && near == null)
        {
            // Already up: keep the place and let the width follow the new text.
            Screens.Place(w, monitor, frame);
            alpha.Target = 1;
            rise.Target = 0;
            if (reduced) width.Snap(target); else width.Target = target;
        }
        else
        {
            // Rise into place from slightly below.
            alpha.Response = reduced ? 0.16 : 0.29;
            alpha.Snap(0);
            alpha.Target = 1;
            rise.Snap(reduced ? 0 : 10);
            rise.Target = 0;
            width.Snap(target);
            press.Snap(1);
            Apply();
            if (!w.IsVisible) w.Show();
            Screens.Place(w, monitor, frame);
            if (near != null && !reduced) shakeClock = 0;
        }
        Screens.BringToFront(w);
        driver.Start();

        hideTimer?.Stop();
        autoHideDelay = autoHide ? (this.files.Count == 0 ? 2.2 : 4) : null;
        // A toast that went away under the pointer (clicked, faded) never got a mouse-leave; start from where the
        // pointer really is, so an old hover can't hold this toast up forever.
        w.SyncHover();
        ScheduleHide();
    }

    /// Takes down a busy toast (a window that just opened is its own feedback); leaves results and errors alone.
    public void HideIfSpinning()
    {
        if (isSpinning) Hide();
    }

    /// Fades the toast out now.
    public void Hide()
    {
        hideTimer?.Stop();
        if (window is not { IsVisible: true }) return;
        generation++;
        hiding = true;
        alpha.Response = 0.24;
        alpha.Target = 0;
        rise.Target = Motion.Reduced ? 0 : 6;
        driver.Start();
    }

    internal bool IsHovered => window?.Hovered == true;

    void ScheduleHide(double? delay = null)
    {
        hideTimer?.Stop();
        if ((delay ?? autoHideDelay) is not { } wait || IsHovered) return;
        int token = generation;
        hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(wait) };
        hideTimer.Tick += (_, _) =>
        {
            hideTimer?.Stop();
            if (token == generation) Hide();
        };
        hideTimer.Start();
    }

    /// Hovering holds the toast; leaving gives a little more time to read it.
    internal void HoverChanged(bool inside)
    {
        if (inside) hideTimer?.Stop();
        else if (autoHideDelay is { } delay && !hiding) ScheduleHide(Math.Min(delay, 1.2));
    }

    internal void Pressed(bool down)
    {
        if (Motion.Reduced) return;
        press.Response = down ? 0.08 : 0.16;
        press.Target = down ? 0.97 : 1;
        driver.Start();
    }

    internal void Clicked()
    {
        if (files.Count > 0) FileActions.Reveal(files);
        Hide();
    }

    void SetSymbol(string name, bool spinning)
    {
        var glyph = Glyph(name);
        if (window is null) return;
        if (shownSymbol != null && shownSymbol != name && window.IsVisible && !Motion.Reduced)
        {
            // The new symbol pops in; a checkmark gets a small bounce.
            iconScale.Damping = name is "ok" or "checkmark.circle.fill" ? 0.5 : 0.8;
            iconScale.Snap(0.55);
            iconScale.Target = 1;
        }
        else iconScale.Snap(1);
        window.IconText.Text = glyph;
        shownSymbol = name;
        isSpinning = spinning;
        spin = 0;
    }

    /// The Segoe Fluent Icons glyph for a semantic symbol name (or an SF Symbol name from the Mac code).
    public static string Glyph(string name) => name switch
    {
        "ok" or "checkmark.circle.fill" or "checkmark.circle" or "checkmark" => "",    // CompletedSolid
        "error" or "exclamationmark.triangle.fill" or "exclamationmark.triangle" => "", // Warning
        "info" or "info.circle.fill" or "info.circle" => "",                             // Info
        "cancel" or "xmark.circle" or "xmark.circle.fill" or "xmark" => "",               // ErrorBadge (circled ×)
        "busy" or "arrow.triangle.2.circlepath.circle" or "arrow.triangle.2.circlepath" => "", // Sync
        "wheel" or "circle.circle" => Feature.Wheel.Glyph(),
        "on" or "power" or "togglepower" => "",                                         // ToggleFilled
        "off" or "poweroff" => "",                                                       // ToggleBorder
        "nosign" => "",                                                                  // Blocked
        "copy" or "doc.on.doc" or "doc.on.clipboard" => "",                              // Copy
        "clothesline" or "photo.on.rectangle" => Feature.Clothesline.Glyph(),
        // A glyph passed directly.
        { Length: 1 } single when single[0] >= '' && single[0] <= '' => single,
        _ => "",
    };

    static Color TintColor(ToastTint tint, bool dark) => tint switch
    {
        ToastTint.Red => dark ? Color.FromRgb(0xFF, 0x99, 0xA4) : Color.FromRgb(0xC4, 0x2B, 0x1C),
        ToastTint.Secondary => dark ? Look.Gray(1, 0.62) : Look.Gray(0, 0.55),
        _ => Look.AccentText(dark),
    };

    bool Advance(double dt)
    {
        alpha.Step(dt);
        rise.Step(dt);
        width.Step(dt);
        press.Step(dt);
        iconScale.Step(dt);
        if (isSpinning && !Motion.Reduced) spin = (spin + 360 * dt) % 360;
        if (!double.IsInfinity(shakeClock))
        {
            shakeClock += dt;
            if (shakeClock >= 0.32) shakeClock = double.PositiveInfinity;
        }
        Apply();
        if (hiding && alpha.Value < 0.01)
        {
            alpha.Snap(0);
            hiding = false;
            window?.Hide();
            isSpinning = false;
            return false;
        }
        bool moving = !(alpha.IsSettled && rise.IsSettled && width.IsSettled && press.IsSettled && iconScale.IsSettled)
                      || !double.IsInfinity(shakeClock) || (isSpinning && !Motion.Reduced && window?.IsVisible == true);
        return moving;
    }

    /// A short horizontal shake, like a refused password: 0, −7, 6, −4, 3, −1, 0 over 0.32 s, easing out.
    static double Shake(double t)
    {
        double[] values = [0, -7, 6, -4, 3, -1, 0];
        double u = 1 - Math.Pow(1 - Math.Clamp(t / 0.32, 0, 1), 2);
        double position = u * (values.Length - 1);
        int i = Math.Min((int)position, values.Length - 2);
        return Mix.Lerp(values[i], values[i + 1], position - i);
    }

    void Apply()
    {
        if (window is null) return;
        window.Root.Opacity = Math.Clamp(alpha.Value, 0, 1);
        window.Pill.Width = Math.Max(width.Value, 40);
        double s = press.Value;
        window.PillTransform.Children[0] = new ScaleTransform(s, s, window.Pill.Width / 2, Height / 2);
        window.PillTransform.Children[1] = new TranslateTransform(double.IsInfinity(shakeClock) ? 0 : Shake(shakeClock), rise.Value);
        window.IconText.RenderTransform = new TransformGroup
        {
            Children = { new ScaleTransform(iconScale.Value, iconScale.Value), new RotateTransform(isSpinning ? spin : 0) },
        };
    }

    /// The toast's window: a pill with an icon and one line of text, in a transparent, click-through margin that
    /// leaves room for the shadow and the rise.
    sealed class ToastWindow : Window
    {
        public readonly Grid Root = new();
        public readonly Border Pill;
        public readonly TextBlock IconText;
        public readonly TextBlock Label;
        public readonly TransformGroup PillTransform = new() { Children = { new ScaleTransform(), new TranslateTransform() } };
        readonly Toast owner;
        public bool Hovered { get; private set; }
        bool down;

        public ToastWindow(Toast owner)
        {
            this.owner = owner;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Focusable = false;
            Title = "Swivel";
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -10000;
            Top = -10000;
            Native.MakeNonActivating(this);

            IconText = new TextBlock
            {
                FontFamily = Look.Icons, FontSize = 16, VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5), Margin = new Thickness(0, 1, 0, 0),
            };
            Label = new TextBlock
            {
                FontFamily = Look.UI, FontSize = 13, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(9, 0, 0, 1), TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var stack = new StackPanel
            {
                Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 18, 0),
                VerticalAlignment = VerticalAlignment.Center, Children = { IconText, Label },
            };
            Pill = new Border
            {
                Height = Toast.Height, CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Child = stack, RenderTransform = PillTransform, ClipToBounds = false,
                Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 4, Direction = 270, Opacity = 0.22, RenderingBias = RenderingBias.Performance },
            };
            Root.Children.Add(Pill);
            Content = Root;

            Pill.MouseEnter += (_, _) => { Hovered = true; owner.HoverChanged(true); };
            Pill.MouseLeave += (_, _) =>
            {
                Hovered = false;
                if (down) { down = false; owner.Pressed(false); }
                owner.HoverChanged(false);
            };
            Pill.MouseLeftButtonDown += (_, e) =>
            {
                down = true;
                Pill.CaptureMouse();
                owner.Pressed(true);
                e.Handled = true;
            };
            Pill.MouseLeftButtonUp += (_, e) =>
            {
                bool wasDown = down;
                down = false;
                Pill.ReleaseMouseCapture();
                owner.Pressed(false);
                var p = e.GetPosition(Pill);
                if (wasDown && p.X >= 0 && p.Y >= 0 && p.X <= Pill.ActualWidth && p.Y <= Pill.ActualHeight) owner.Clicked();
                e.Handled = true;
            };
        }

        public void Apply(bool dark)
        {
            bool solid = Native.TransparencyOff;
            Pill.Background = Look.Brush(dark
                ? Color.FromArgb(solid ? (byte)255 : (byte)242, 0x2C, 0x2C, 0x2C)
                : Color.FromArgb(solid ? (byte)255 : (byte)245, 0xFB, 0xFB, 0xFB));
            Pill.BorderBrush = Look.Brush(dark ? Color.FromArgb(0x50, 0, 0, 0) : Color.FromArgb(0x18, 0, 0, 0));
            Label.Foreground = Look.Brush(dark ? Colors.White : Color.FromArgb(0xE4, 0, 0, 0));
            ((DropShadowEffect)Pill.Effect).Opacity = dark ? 0.45 : 0.2;
        }

        /// Where the pointer really is, without waiting for a mouse event.
        public void SyncHover()
        {
            if (!IsVisible) { Hovered = false; return; }
            try
            {
                var c = Native.CursorPixels();
                var p = Pill.PointFromScreen(new Point(c.X, c.Y));
                Hovered = p.X >= 0 && p.Y >= 0 && p.X <= Pill.ActualWidth && p.Y <= Pill.ActualHeight;
            }
            catch (InvalidOperationException) { Hovered = false; }
        }

        static FormattedText Format(string text, Visual visual) =>
            new(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(Look.UI, FontStyles.Normal, FontWeights.Medium, FontStretches.Normal), 13, Brushes.Black,
                VisualTreeHelper.GetDpi(visual).PixelsPerDip);

        public static double Measure(string text, Visual visual) => Format(text, visual).WidthIncludingTrailingWhitespace;

        /// Shortens the middle of a long line ("a very lo…ame.png"), keeping both ends readable.
        public static string TruncateMiddle(string text, double maxWidth, Visual visual)
        {
            if (Measure(text, visual) <= maxWidth) return text;
            int lo = 0, hi = text.Length;
            while (lo < hi)
            {
                int keep = (lo + hi + 1) / 2;
                var candidate = text[..((keep + 1) / 2)] + "…" + text[(text.Length - keep / 2)..];
                if (Measure(candidate, visual) <= maxWidth) lo = keep; else hi = keep - 1;
            }
            return text[..((lo + 1) / 2)] + "…" + text[(text.Length - lo / 2)..];
        }
    }
}
