using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Swivel;

/// A floating tool window (the Mac app's non-activating `ToolPanel`): always on top, no taskbar button, and it
/// appears without taking the keyboard from the app the user is in.
///
/// The trade-off on Windows: a WS_EX_NOACTIVATE window can never receive keyboard focus, while an NSPanel can be key
/// without activating its app. So a panel starts non-activating (adding files to the shelf, or the shelf appearing,
/// never interrupts the user's app or a drag in progress) and becomes an ordinary activatable window only when it
/// needs the keyboard: when it is shown on purpose (`Present(focus: true)` — Quick Actions from its hotkey, the shelf
/// from the tray) or when the user clicks into it. Taking focus then is what the user asked for. When it closes
/// while it still has the foreground, activation goes back to the window that was in front before
/// (`ActivationReturn`), so the user's app isn't left in the background. Once the panel loses activation it is
/// non-activating again.
class ToolPanel : Window
{
    const int WM_MOUSEACTIVATE = 0x0021, MA_ACTIVATE = 1;
    const int HTCLIENT = 1;

    ActivationReturn handBack;

    /// Tests show panels far off screen, so nothing appears on the desktop while they run.
    internal static bool OffscreenForTests { get; set; }

    /// Hides instead of closing, so the panel and what it holds last for the session.
    public bool HidesOnClose { get; set; } = true;

    /// Clicking into the panel activates it (so its keys work), as becomesKeyOnlyIfNeeded = false does on the Mac.
    public bool ActivatesOnClick { get; set; } = true;

    public ToolPanel()
    {
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStyle = WindowStyle.ToolWindow;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Native.MakeNonActivating(this);
        AppTypography.Apply(this);   // Feel & Text's text size and bold text (the Shelf and Quick Actions)
        SourceInitialized += (_, _) => HwndSource.FromHwnd(Handle)?.AddHook(Hook);
        Deactivated += (_, _) => SetActivatable(false);
        PreviewKeyDown += (_, e) =>
        {
            // The Mac Window menu's ⌘W.
            if (e.Key == Key.W && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                Dismiss();
            }
        };
    }

    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    /// Brings the panel forward. `focus` also activates it so it takes typing straight away.
    public void Present(bool focus)
    {
        if (!IsVisible)
        {
            if (OffscreenForTests)
            {
                Left = -32000;
                Top = -32000;
            }
            Show();
        }
        if (!focus || OffscreenForTests) return;
        var foreground = ToolNative.GetForegroundWindow();
        if (foreground != Handle) handBack.Remember(foreground);
        SetActivatable(true);
        Activate();
        ToolNative.SetForegroundWindow(Handle);
    }

    /// Hides the panel; if it still had the foreground, the window that was in front before gets it back.
    public void Dismiss()
    {
        if (!IsVisible) return;
        bool active = ToolNative.GetForegroundWindow() is var foreground && (foreground == Handle || IsOwnedPopup(foreground));
        var previous = handBack.Take(swivelIsActive: active);
        Hide();
        if (previous != IntPtr.Zero) ToolNative.SetForegroundWindow(previous);
    }

    bool IsOwnedPopup(IntPtr hwnd) => hwnd != IntPtr.Zero && OwnedWindows.Cast<Window>().Any(w => new WindowInteropHelper(w).Handle == hwnd);

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (HidesOnClose && !e.Cancel && Application.Current is { } app && !IsShuttingDown(app))
        {
            e.Cancel = true;
            Dismiss();
        }
    }

    static bool IsShuttingDown(Application app)
    {
        try { return app.Dispatcher.HasShutdownStarted; } catch (InvalidOperationException) { return true; }
    }

    /// Adds or removes WS_EX_NOACTIVATE.
    void SetActivatable(bool activatable)
    {
        var hwnd = Handle;
        if (hwnd == IntPtr.Zero) return;
        var style = Native.GetWindowLongW(hwnd, Native.GWL_EXSTYLE);
        var updated = activatable ? style & ~Native.WS_EX_NOACTIVATE : style | Native.WS_EX_NOACTIVATE;
        if (updated != style) Native.SetWindowLongW(hwnd, Native.GWL_EXSTYLE, updated);
    }

    IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // A click into the panel (not a drop, which never sends this) makes it a normal window for the keyboard.
        if (message == WM_MOUSEACTIVATE && ActivatesOnClick && (lParam.ToInt64() & 0xFFFF) == HTCLIENT)
        {
            if (!IsActive)
            {
                var foreground = ToolNative.GetForegroundWindow();
                if (foreground != hwnd) handBack.Remember(foreground);
            }
            SetActivatable(true);
            handled = true;
            Dispatcher.BeginInvoke(() => { if (!IsActive && IsVisible) Activate(); });
            return new IntPtr(MA_ACTIVATE);
        }
        return IntPtr.Zero;
    }
}
