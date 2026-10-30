using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Swivel.Core;
using Swivel.Windows;

internal static class RopeNativeChecks
{
    [DllImport("user32.dll",ExactSpelling = true)] private static extern nint GetWindowLongPtrW(nint window,int index);
    [DllImport("user32.dll",ExactSpelling = true)] private static extern nint SendMessageW(nint window,uint message,nint wParam,nint lParam);
    [DllImport("user32.dll",ExactSpelling = true)] private static extern nint GetForegroundWindow();

    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        var app = Application.Current as App ?? throw new InvalidOperationException("An isolated validation App is required.");
        if (!app.IsValidationHost) throw new InvalidOperationException("Do not run this fixture in a normal product App.");

        // Window flags, from the pure helper and from a real HWND (created, never shown).
        long style = RopeWindow.RopeStyle(RopeWindow.ExAppWindow | 0x100);
        check((style & RopeWindow.ExNoActivate) != 0 && (style & RopeWindow.ExToolWindow) != 0 && (style & RopeWindow.ExTopmost) != 0 && (style & RopeWindow.ExAppWindow) == 0 && (style & 0x100) != 0,
            "Rope style adds NOACTIVATE, TOOLWINDOW and TOPMOST, drops APPWINDOW and keeps other bits");
        var window = new RopeWindow(app);
        try
        {
            nint handle = new WindowInteropHelper(window).EnsureHandle();
            long ex = GetWindowLongPtrW(handle,-20);
            check((ex & RopeWindow.ExNoActivate) != 0 && (ex & RopeWindow.ExToolWindow) != 0 && (ex & RopeWindow.ExAppWindow) == 0,"Real rope HWND is non-activating and kept out of the taskbar and Alt+Tab");
            check(!window.ShowActivated && window.Topmost && !window.ShowInTaskbar && window.WindowStyle == WindowStyle.None && window.AllowsTransparency && !window.Focusable,
                "Rope window is borderless, transparent, topmost and shown without activation");
            check(SendMessageW(handle,RopeWindow.MouseActivate,handle,0) == RopeWindow.NoActivate,"A click on the rope answers MA_NOACTIVATE");
            check(!window.IsVisible && !window.Engaged && !window.Lowered && window.ActiveArea() is null,"An unshown rope is not engaged and has no active area");
            check(GetForegroundWindow() != handle,"Creating the rope did not make it the foreground window");
        }
        finally { window.Close(); }

        // The watcher's real step logic, fed with synthetic cursor positions and monitor rectangles.
        var primary = new ScreenRect(0,0,1920,1080); var second = new ScreenRect(-1280,-300,0,724);
        RopeSample? next = null; bool engaged = false; ScreenRect? area = null; var shown = new List<RopeSample>(); int hidden = 0; var asked = new List<bool>();
        var watcher = new RopeEdgeWatcher(showing => { asked.Add(showing); return next; },shown.Add,() => hidden++,() => area,() => engaged);
        TimeSpan T(double ms) => TimeSpan.FromMilliseconds(ms);
        RopeSample At(int x,int y,ScreenRect monitor,bool full = false) => new(x,y,monitor,monitor with { Bottom = monitor.Bottom - 40 },1,full);
        check(watcher.Step(T(0)) == RopeEdgeAction.None && shown.Count == 0,"No cursor sample changes nothing");
        next = At(500,0,primary); watcher.Step(T(0));
        check(watcher.Step(T(100)) == RopeEdgeAction.None && shown.Count == 0,"Watcher waits for the dwell");
        check(watcher.Step(T(160)) == RopeEdgeAction.Show && shown.Count == 1 && shown[0].Work.Bottom == 1040 && watcher.Showing,"Watcher shows the rope on the pointer's monitor work area");
        check(asked[^1] == false,"The sampler is told the rope was hidden (full-screen check only at the edge)");
        area = new ScreenRect(300,0,1600,300);
        next = At(800,150,primary); watcher.Step(T(200)); check(asked[^1],"While showing, the sampler is told so");
        next = At(800,700,primary); watcher.Step(T(300));
        engaged = true; check(watcher.Step(T(2000)) == RopeEdgeAction.None && hidden == 0,"Watcher keeps the rope while a print is engaged");
        engaged = false; watcher.Step(T(2100));
        check(watcher.Step(T(3150)) == RopeEdgeAction.Hide && hidden == 1 && !watcher.Showing,"Watcher lifts the rope one second after leaving");
        next = At(-600,-300,second); watcher.Step(T(3200));
        check(watcher.Step(T(3400)) == RopeEdgeAction.Show && shown[^1].Monitor == second,"Watcher shows on a secondary monitor with negative coordinates");
        next = At(-600,-299,second,full: true);
        check(watcher.Step(T(3500)) == RopeEdgeAction.Hide && hidden == 2,"A full-screen app in front hides the rope");
        next = At(-600,-299,second,full: true); watcher.Step(T(3600)); next = At(-600,-200,second); watcher.Step(T(3700));
        next = At(-600,-300,second,full: true); watcher.Step(T(3800));
        check(watcher.Step(T(4200)) == RopeEdgeAction.None && shown.Count == 2,"Full-screen app in front: the edge never shows the rope");
        watcher.Start(); check(watcher.Running,"Watcher polls once started");
        watcher.Stop(); check(!watcher.Running && !watcher.Showing,"Stopping the watcher stops polling and forgets the rope");
        check(RopeEdgeWatcher.Interval == TimeSpan.FromMilliseconds(50),"Cursor is polled every 50 ms");

        // Full-screen skip: desktop and shell windows never count; a covering window does.
        foreach (var name in new[] { "Progman","WorkerW","Shell_TrayWnd","Shell_SecondaryTrayWnd" }) check(RopeEdgeWatcher.IsDesktopClass(name),"Desktop class is never full screen: " + name);
        check(!RopeEdgeWatcher.IsDesktopClass("Chrome_WidgetWin_1") && !RopeEdgeWatcher.IsDesktopClass("progman"),"Ordinary window classes are not desktop");
        check(RopeEdgeTrigger.IsFullScreen(new ScreenRect(-1280,-300,0,724),second,true,false,false) && !RopeEdgeTrigger.IsFullScreen(new ScreenRect(-1280,-300,0,684),second,true,false,false),
            "Covering a secondary monitor is full screen; stopping at its taskbar is not");
        check(!RopeEdgeTrigger.IsFullScreen(new ScreenRect(-1288,-308,8,732),second,true,false,true),"A maximized captioned window on a taskbar-less monitor is not full screen");
        check(RopeEdgeWatcher.IsFramed(true,0x00040000) && RopeEdgeWatcher.IsFramed(false,0x00C00000) && !RopeEdgeWatcher.IsFramed(false,unchecked((long)0x80000000)) && !RopeEdgeWatcher.IsFramed(false,0x00800000),
            "Maximized sizing frames and full-caption windows are frames; a popup without a caption is not");
        check(!RopeEdgeWatcher.IsFramed(true,unchecked((long)0x80000000)) && RopeEdgeTrigger.IsFullScreen(second,second,true,false,RopeEdgeWatcher.IsFramed(true,unchecked((long)0x80000000))),
            "A maximized window without caption or frame that equals the monitor is full screen");
        // The real sampler runs without throwing (a locked or headless session may give no cursor).
        var real = RopeEdgeWatcher.Native(false);
        check(real is null || (real.Value.Monitor.Width > 0 && real.Value.Work.Width > 0 && real.Value.Monitor.Covers(real.Value.Work)),"Native cursor sample reports a monitor containing its work area");
        _ = reject;
    }
}
