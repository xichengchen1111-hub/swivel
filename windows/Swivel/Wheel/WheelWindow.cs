using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace Swivel;

/// The menu's window (the Mac's WheelPanel): borderless, transparent, topmost and never activated. It is the drop
/// target for the drag in progress: the whole window, clear margins included, catches the drag, so a flick past the
/// ring keeps its sector and a release off the menu cancels instead of dropping into the window underneath.
///
/// Windows has no global drag pasteboard, so the files being dragged are only known once the drag enters this
/// window. A key tap first puts up an invisible "probe" under the pointer; the drag enters it at once (Windows'
/// drag loop re-checks the window under the pointer continuously) and the menu opens with the files it carries.
sealed partial class WheelWindow : Window
{
    public WheelView View { get; } = new();
    readonly Canvas root = new() { Background = Look.Brush(Color.FromArgb(1, 0, 0, 0)) };
    readonly Canvas content = new();
    readonly GlassBackdrop glass = new();
    readonly ScaleTransform pop = new();

    /// Open with a menu (not the probe, not hidden).
    public bool IsShown { get; private set; }
    /// Up as an invisible drop target, waiting for the drag to enter and say which files it carries.
    public bool IsProbing { get; private set; }
    /// True while the menu animates away after a drop or cancel.
    public bool IsClosing { get; private set; }
    /// Changes every time the menu opens or goes away, so stale timers and completions can tell.
    public int Generation { get; private set; }

    /// The drag entered with these files (null: it carries no files).
    public Action<IReadOnlyList<string>?>? FilesEntered { get; set; }

    Native.POINT anchor;
    Screens.Monitor monitor;
    Rect frame;               // the window, in DIPs on the monitor
    Point viewOrigin;          // the view's top-left inside the window

    Spring alpha = new(0, 0.12, 1);
    Spring scale = new(1, 0.24, 0.9);
    bool fading;
    readonly FrameDriver driver = new();
    IDropTargetHelper? dragImages;

    public WheelWindow()
    {
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
        Width = 400;
        Height = 400;
        AllowDrop = true;
        Native.MakeNonActivating(this);

        content.Children.Add(glass);
        content.Children.Add(View);
        content.RenderTransform = pop;
        root.Children.Add(content);
        Content = root;

        driver.OnFrame = Advance;
        DragEnter += OnDragEnter;
        DragOver += OnDragOver;
        DragLeave += OnDragLeave;
        Drop += OnDrop;
        try { dragImages = (IDropTargetHelper?)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("4657278A-411B-11D2-839A-00C04FD918D0"))!); }
        catch (Exception) { dragImages = null; }
    }

    public enum Dismissal { Cancel, Confirm }

    // MARK: Placement

    /// Puts the menu at the pointer (physical pixels). The wheel centres on it (the hub is the cancel zone) and only
    /// moves if its ring would leave the screen; other styles open beside the pointer, flipping sides when there's
    /// no room. The window also covers the pointer, so the drag is inside it from the first moment.
    void Place(Native.POINT point)
    {
        anchor = point;
        monitor = Screens.At(point);
        var area = monitor.BoundsDips;
        var p = monitor.ToDips(point);
        var size = View.ViewSize;
        Rect menu;
        switch (View.MenuStyle)
        {
            case MenuStyle.Wheel:
            {
                // Topmost windows sit above the taskbar, so only the visible ring has to fit on screen.
                double r = WheelView.OuterRadius + 14;
                var c = new Point(Math.Clamp(p.X, area.Left + r, Math.Max(area.Right - r, area.Left + r)),
                                  Math.Clamp(p.Y, area.Top + r, Math.Max(area.Bottom - r, area.Top + r)));
                menu = new Rect(c.X - size.Width / 2, c.Y - size.Height / 2, size.Width, size.Height);
                break;
            }
            case MenuStyle.List:
            {
                double x = p.X + 28, y = p.Y - size.Height / 2;
                if (x + size.Width > area.Right) x = p.X - 28 - size.Width;
                y = Math.Clamp(y, area.Top, Math.Max(area.Bottom - size.Height, area.Top));
                menu = new Rect(x, y, size.Width, size.Height);
                break;
            }
            default:
            {
                double x = p.X - size.Width / 2, y = p.Y - 28 - size.Height;
                if (y < area.Top) y = p.Y + 28;
                x = Math.Clamp(x, area.Left, Math.Max(area.Right - size.Width, area.Left));
                menu = new Rect(x, y, size.Width, size.Height);
                break;
            }
        }
        var window = menu;
        var reach = new Rect(p.X - 32, p.Y - 32, 64, 64);
        reach.Intersect(area);
        if (!reach.IsEmpty) window.Union(reach);
        SetFrame(window, new Point(menu.X - window.X, menu.Y - window.Y));
    }

    void SetFrame(Rect window, Point viewAt)
    {
        frame = window;
        viewOrigin = viewAt;
        root.Width = content.Width = window.Width;
        root.Height = content.Height = window.Height;
        Canvas.SetLeft(View, viewAt.X);
        Canvas.SetTop(View, viewAt.Y);
        glass.Width = window.Width;
        glass.Height = window.Height;
        var size = View.ViewSize;
        pop.CenterX = viewAt.X + size.Width / 2;
        pop.CenterY = viewAt.Y + size.Height / 2;
        UpdateGlassShape();
        Screens.Place(this, monitor, window);
    }

    void UpdateGlassShape()
    {
        if (View.Backdrop is { } b && !(View.MenuStyle == MenuStyle.Wheel && Native.TransparencyOff))
        {
            glass.Visibility = Visibility.Visible;
            glass.Shape = new Rect(b.Rect.X + viewOrigin.X, b.Rect.Y + viewOrigin.Y, b.Rect.Width, b.Rect.Height);
            glass.Radius = b.Radius;
            glass.Ellipse = View.MenuStyle == MenuStyle.Wheel;
            glass.Dark = View.DarkOverride ?? Native.DarkMode;
        }
        else glass.Visibility = Visibility.Collapsed;
        glass.InvalidateVisual();
    }

    /// Takes a fresh blurred snapshot of the desktop under the window for the glass.
    void CaptureGlass()
    {
        glass.Snapshot = null;
        if (glass.Visibility != Visibility.Visible || Native.TransparencyOff) return;
        var origin = monitor.ToPixels(frame.TopLeft);
        int w = (int)Math.Round(frame.Width * monitor.Scale), h = (int)Math.Round(frame.Height * monitor.Scale);
        glass.Snapshot = GlassBackdrop.Capture(origin.X, origin.Y, w, h);
        glass.SnapshotRect = new Rect(0, 0, frame.Width, frame.Height);
        glass.InvalidateVisual();
    }

    /// Resizes the window for the menu's current style and item count, keeping it where it opened.
    public void Relayout()
    {
        var size = WheelView.MenuSize(View.MenuStyle, View.Items.Count, live: true);
        if (size != View.ViewSize)
        {
            View.ViewSize = size;
            if (IsShown) Place(anchor);
        }
        UpdateGlassShape();
        View.Invalidate();
    }

    // MARK: Showing

    /// An invisible drop target under the pointer, to learn which files the drag carries. It goes away by itself
    /// if no file drag enters it soon.
    public void Probe(Native.POINT point)
    {
        if (IsShown && !IsClosing) return;
        Generation++;
        IsProbing = true;
        IsClosing = false;
        anchor = point;
        monitor = Screens.At(point);
        var p = monitor.ToDips(point);
        content.Opacity = 0;
        Native.SetClickThrough(this, false);
        if (!IsVisible) Show();
        SetFrame(new Rect(p.X - 80, p.Y - 80, 160, 160), new Point(0, 0));
        // Windows' drag loop re-checks the window under a still pointer on its own timer, so the drag usually enters
        // within a frame or two. The probe stays while the button is held (a pointer that hasn't moved yet) and goes
        // once it is released; a drag that never enters (not a drag-and-drop at all, e.g. a text selection) leaves
        // nothing behind.
        int token = Generation;
        void Check()
        {
            if (Generation != token || !IsProbing) return;
            if (!Native.PrimaryButtonDown()) HideNow();
            else After(0.25, Check);
        }
        After(0.25, Check);
    }

    /// Opens the menu at the pointer (physical pixels). Switching to another menu keeps the window up and just
    /// blooms the new items.
    public void ShowMenu(Native.POINT point)
    {
        bool switching = IsShown && !IsClosing;
        bool fromProbe = IsProbing;
        Generation++;
        IsClosing = false;
        IsProbing = false;
        fading = false;
        Native.SetClickThrough(this, false);
        double moved = Math.Sqrt(Math.Pow(point.X - anchor.X, 2) + Math.Pow(point.Y - anchor.Y, 2)) / Math.Max(monitor.Scale, 1);
        if (fromProbe) point = anchor;
        if (!switching || moved > 4) Place(point);
        View.IsLive = true;
        var c = monitor.ToDips(point);
        View.BeginOpen(new Point(c.X - frame.X - viewOrigin.X, c.Y - frame.Y - viewOrigin.Y));
        Feedback.Prepare();
        IsShown = true;
        if (switching) return;
        CaptureGlass();
        alpha.Snap(0);
        alpha.Target = 1;
        alpha.Response = 0.12;
        scale.Response = 0.24;
        scale.Damping = 0.9;
        scale.Snap(Motion.Reduced ? 1 : 0.94);
        scale.Target = 1;
        ApplyChrome();
        if (!IsVisible) Show();
        Screens.BringToFront(this);
        driver.Start();
    }

    /// Animates the menu away. A confirmed drop lets the pressed item finish its release first; a cancel shrinks
    /// and fades at once. Opening again (a new key tap) interrupts it.
    public void Dismiss(Dismissal how)
    {
        if (IsProbing && !IsShown) { HideNow(); return; }
        if (!IsShown || IsClosing) return;
        IsClosing = true;
        // A closing menu no longer catches the drag.
        Native.SetClickThrough(this, true);
        int token = Generation;
        // A confirmed drop stays up long enough for the press to land (about 0.2 s).
        double delay = how == Dismissal.Confirm ? 0.16 : 0;
        double duration = how == Dismissal.Confirm ? 0.14 : 0.12;
        void Fade()
        {
            if (Generation != token) return;
            fading = true;
            // A critically damped spring is 99 % there after about 1.06 × its response.
            alpha.Response = duration * 1.1;
            alpha.Target = 0;
            if (!Motion.Reduced)
            {
                scale.Response = duration * 1.4;
                scale.Damping = 1;
                scale.Target = how == Dismissal.Confirm ? 0.97 : 0.93;
            }
            driver.Start();
        }
        if (delay > 0) After(delay, Fade); else Fade();
    }

    /// Removes the menu immediately.
    public void HideNow()
    {
        Generation++;
        IsClosing = false;
        IsShown = false;
        IsProbing = false;
        fading = false;
        driver.Stop();
        Hide();
        Native.SetClickThrough(this, false);
        alpha.Snap(0);
        scale.Snap(1);
        ApplyChrome();
        View.Hovered = null;
        glass.Snapshot = null;
    }

    bool Advance(double dt)
    {
        alpha.Step(dt);
        scale.Step(dt);
        ApplyChrome();
        if (fading && alpha.Value < 0.01)
        {
            HideNow();
            return false;
        }
        return !(alpha.IsSettled && scale.IsSettled);
    }

    void ApplyChrome()
    {
        content.Opacity = Math.Clamp(alpha.Value, 0, 1);
        pop.ScaleX = pop.ScaleY = scale.Value;
    }

    void After(double seconds, Action action)
    {
        var t = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(seconds) };
        t.Tick += (_, _) => { t.Stop(); action(); };
        t.Start();
    }

    // MARK: Drop target

    static IReadOnlyList<string>? Files(DragEventArgs e)
    {
        try
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
                return files;
        }
        catch (COMException) { }
        catch (InvalidOperationException) { }
        return null;
    }

    /// The effect shown while over an item: Copy, or Link. Never Move (Explorer would delete the original after a
    /// move); a source that only allows Move gets Move shown, but the drop itself then reports None.
    static DragDropEffects HoverEffect(DragDropEffects allowed) =>
        allowed.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy
        : allowed.HasFlag(DragDropEffects.Link) ? DragDropEffects.Link
        : allowed.HasFlag(DragDropEffects.Move) ? DragDropEffects.Move : DragDropEffects.None;

    static DragDropEffects DropEffect(DragDropEffects allowed) =>
        allowed.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy
        : allowed.HasFlag(DragDropEffects.Link) ? DragDropEffects.Link : DragDropEffects.None;

    void OnDragEnter(object sender, DragEventArgs e)
    {
        var files = Files(e);
        FilesEntered?.Invoke(files);
        Update(e);
        DragImage(e, entering: true);
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        Update(e);
        DragImage(e, entering: false);
    }

    void Update(DragEventArgs e)
    {
        e.Handled = true;
        if (!IsShown || IsClosing)
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        var p = e.GetPosition(View);
        bool inside = p.X >= 0 && p.Y >= 0 && p.X < View.ViewSize.Width && p.Y < View.ViewSize.Height;
        bool onItem = false;
        if (inside) onItem = View.Track(p);
        else View.PointerLeft();
        e.Effects = onItem ? HoverEffect(e.AllowedEffects) : DragDropEffects.None;
    }

    void OnDragLeave(object sender, DragEventArgs e)
    {
        e.Handled = true;
        try { dragImages?.DragLeave(); } catch (Exception) { }
        // Windows reports the end of a drag over the menu without a drop (Esc, or released on the hub) as a leave.
        bool ended = !Native.PrimaryButtonDown() || Native.IsKeyDown(0x1B);
        if (ended && IsShown && !IsClosing && View.Pressed == null)
            Dispatcher.BeginInvoke(() => View.OnDragEndedWithoutDrop?.Invoke());
        else View.PointerLeft();
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var cursor = Native.CursorPixels();
        try { dragImages?.Drop((ComDataObject)e.Data, ref cursor, (int)DragDropEffects.Copy); } catch (Exception) { }
        var files = Files(e);
        if (IsShown && !IsClosing && files != null)
        {
            // The hover from the last DragOver is the target; check the drop point too.
            var p = e.GetPosition(View);
            if (p.X >= 0 && p.Y >= 0 && p.X < View.ViewSize.Width && p.Y < View.ViewSize.Height) View.Track(p);
            if (View.PerformDrop(files))
            {
                e.Effects = DropEffect(e.AllowedEffects);
                return;
            }
        }
        e.Effects = DragDropEffects.None;
        if (IsShown && !IsClosing) Dispatcher.BeginInvoke(() => View.OnDragEndedWithoutDrop?.Invoke());
    }

    /// Shows the shell's drag image (the file's thumbnail) over the menu, as Explorer does over its own windows.
    void DragImage(DragEventArgs e, bool entering)
    {
        if (dragImages is null) return;
        var cursor = Native.CursorPixels();
        try
        {
            if (entering)
            {
                if (e.Data is ComDataObject data)
                    dragImages.DragEnter(new WindowInteropHelper(this).Handle, data, ref cursor, (int)e.Effects);
            }
            else dragImages.DragOver(ref cursor, (int)e.Effects);
        }
        catch (Exception) { }
    }

    [ComImport, Guid("4657278B-411B-11D2-839A-00C04FD918D0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDropTargetHelper
    {
        void DragEnter(IntPtr hwndTarget, ComDataObject dataObject, ref Native.POINT pt, int effect);
        void DragLeave();
        void DragOver(ref Native.POINT pt, int effect);
        void Drop(ComDataObject dataObject, ref Native.POINT pt, int effect);
        void Show([MarshalAs(UnmanagedType.Bool)] bool show);
    }
}
