using System.Windows;
using System.Windows.Media;

namespace Swivel;

/// A strip along the top of a monitor's work area (Windows has no menu bar; the taskbar is usually at the bottom).
/// Transparent, topmost and never activated; it lets clicks fall through to the windows below except over a print
/// (or while something is being dragged to it), which the rope switches as the pointer moves.
sealed class RopePanel : Window
{
    public RopeView Rope { get; } = new();
    public RopeMonitor Monitor { get; private set; }
    Int32Rect frame;

    public RopePanel()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000;
        Top = -32000;
        Width = 800;
        Height = RopeView.PanelHeight;
        Title = L("Clothesline");
        Content = Rope;
        Native.MakeNonActivating(this);
        SourceInitialized += (_, _) => Native.SetClickThrough(this, true);
        Rope.IsOnScreen = () => IsVisible;
        Rope.PointerSource = () => PointerInView();
        Rope.CatchMouse = on => Native.SetClickThrough(this, !on);
        IsVisibleChanged += (_, _) => { if (!IsVisible) Rope.Halt(); };
    }

    /// Physical pixels per WPF unit on the monitor the strip is on.
    public double Scale => Monitor.Scale > 0 ? Monitor.Scale : 1;

    public Int32Rect Frame => frame;

    /// A screen point (physical pixels) in the rope's coordinates.
    public Point ToView(Native.POINT p) => new((p.X - frame.X) / Scale, (p.Y - frame.Y) / Scale);

    /// A rectangle in the rope's coordinates, in physical screen pixels.
    public Rect ToScreen(Rect view) => new(frame.X + view.X * Scale, frame.Y + view.Y * Scale, view.Width * Scale, view.Height * Scale);

    public Point? PointerInView()
    {
        if (!IsVisible) return null;
        var p = Native.CursorPixels();
        return Monitor.Contains(p) ? ToView(p) : null;
    }

    /// Lowers the rope. Caught while it's still lifting, it turns around where it is instead of starting over.
    public void Lower(RopeMonitor monitor)
    {
        if (IsVisible && !Rope.IsLifting) return;
        var rect = new Int32Rect(monitor.Work.X, monitor.Work.Y, monitor.Work.Width, (int)Math.Ceiling(RopeView.PanelHeight * monitor.Scale));
        bool moved = !rect.Equals(frame);
        bool fromHidden = !IsVisible || moved;
        if (moved)
        {
            Monitor = monitor;
            frame = rect;
            RopeNative.Place(this, rect);
        }
        Rope.Resize(rect.Width / Scale);
        Rope.Lower(fromHidden);   // lays out and draws at this width before the strip is seen
        if (!IsVisible)
        {
            Show();
            RopeNative.Place(this, rect);
        }
        Rope.ResetMouseCatching();
        Rope.Wake();
    }

    public void Raise()
    {
        if (!IsVisible) return;
        Rope.Lift(Hide);
    }
}
