using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Swivel.Core;
using Swivel.Windows;

internal static class ScreenshotCardNativeChecks
{
    [DllImport("user32.dll",ExactSpelling = true)] private static extern nint GetWindowLongPtrW(nint window,int index);
    [DllImport("user32.dll",ExactSpelling = true)] private static extern nint SendMessageW(nint window,uint message,nint wParam,nint lParam);
    [DllImport("user32.dll",ExactSpelling = true)] private static extern nint GetForegroundWindow();

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle,new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static void Until(Func<bool> done,string what)
    {
        var watch = Stopwatch.StartNew();
        while (!done()) { if (watch.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("Screenshot card fixture did not settle: " + what); Pump(); Thread.Sleep(5); }
        Pump();
    }
    private static void Wait(int milliseconds) { var watch = Stopwatch.StartNew(); while (watch.ElapsedMilliseconds < milliseconds) { Pump(); Thread.Sleep(5); } }

    internal static void Run(string directory,Action<bool,string> check,Action<Action,string> reject)
    {
        var app = Application.Current as App ?? throw new InvalidOperationException("An isolated validation App is required.");
        if (!app.IsValidationHost) throw new InvalidOperationException("Do not run this fixture in a normal product App.");
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try { Folder(check,reject); Controller(directory,check); Card(app,check); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    // The real known folder, read without creating it.
    private static void Folder(Action<bool,string> check,Action<Action,string> reject)
    {
        string? reported = ScreenshotFolder.Reported(ScreenshotCards.FolderId,default);
        check(reported is not null,"Windows reports a Screenshots known-folder location");
        if (reported is not null)
        {
            bool existed = Directory.Exists(reported);
            var location = ScreenshotFolder.Resolve(default);
            check(Directory.Exists(reported) == existed,"Resolving the Screenshots folder never creates it");
            check(existed ? location.Path == reported.TrimEnd('\\') && location.Issue is null : location.Path is null && location.Issue == ScreenshotCards.NoFolder,
                "A present folder is used; a missing one is reported with the reason");
            Console.WriteLine("INFO: Screenshots folder " + (existed ? "exists" : "does not exist") + " on this runner.");
        }
        check(ScreenshotFolder.Reported(Guid.NewGuid(),default) is null,"An unknown known-folder ID reports no path");
        reject(() => ScreenshotFolder.Resolve(new CancellationToken(true)),"Pre-cancelled resolution does not ask Windows");
    }

    private static void Controller(string directory,Action<bool,string> check)
    {
        string root = Path.Combine(directory,"screenshot-cards"); Directory.CreateDirectory(root);
        byte[] png = Png();
        string existing = Path.Combine(root,"Screenshot (1).png"); File.WriteAllBytes(existing,png);
        bool want = true; var shown = new List<ArrivalCard<BitmapSource>>(); var reports = new List<string>(); int hides = 0, creates = 0;
        Func<CancellationToken,Task<IImageArrivalSession>> real = token => { creates++; return Task.Run<IImageArrivalSession>(() => ImageArrivalSession.Create(root,token),token); };
        Func<CancellationToken,Task<IImageArrivalSession>> factory = real;
        var controller = new ScreenshotCardController(() => want,token => factory(token),shown.Add,() => hides++,reports.Add);

        var start = controller.Update(); Until(() => start.IsCompleted,"start");
        check(controller.Running && creates == 1,"Setting on starts one arrival session on the stand-in Screenshots folder");
        Wait(500);
        check(shown.Count == 0 && controller.Session!.Snapshot.EligibleNotifications == 0,"Existing screenshots are not scanned or shown");
        string added = Path.Combine(root,"Screenshot (2).png"); File.WriteAllBytes(added,png);
        Until(() => shown.Count > 0,"first card");
        Wait(800);
        check(shown.Count == 1 && shown[0].Path == added,"A new file in the folder produces exactly one card");
        check(File.ReadAllBytes(added).SequenceEqual(png) && File.Exists(existing),"Showing a card leaves the screenshot files untouched");
        Directory.Move(root,root + " live"); Directory.Move(root + " live",root);
        check(Directory.Exists(root) && controller.Running,"While watching, the person can still rename or move the Screenshots folder");
        controller.Done(shown[0].Id);
        check(controller.Session!.Snapshot.Cards.Count == 0,"A finished card frees its slot in the session");

        var first = controller.Session!;
        want = false; var stop = controller.Update(); Until(() => stop.IsCompleted,"stop");
        check(controller.Session is null && !controller.Running && first.Completion.IsCompleted && first.Snapshot.Status == ArrivalStatus.Stopped && hides >= 1,"Setting off stops and joins the watcher and hides the card");
        File.WriteAllBytes(Path.Combine(root,"Screenshot (3).png"),png); Wait(700);
        check(shown.Count == 1,"No card appears after the setting is off");
        Directory.Move(root,root + " moved"); Directory.Move(root + " moved",root);
        check(Directory.Exists(root),"The folder lease is released once the watcher stopped");

        // Quit: the same Update with Quit making the desired state false must await all cleanup.
        want = true; var again = controller.Update(); Until(() => again.IsCompleted,"restart");
        var second = controller.Session;
        check(controller.Running && second is not null && !ReferenceEquals(first,second) && creates == 2,"Turning the setting on again starts a fresh session");
        want = false; var quit = controller.Update();
        Until(() => quit.IsCompleted,"quit");
        check(second!.Completion.IsCompleted && controller.Idle.IsCompleted,"Quit's wait ends only after the session finished cleanup");
        Directory.Move(root,root + " quit"); Directory.Move(root + " quit",root);
        check(Directory.Exists(root),"Quit released the watcher and folder lease");

        // Off while the folder is still being opened: the late session is joined, never kept.
        IImageArrivalSession? late = null;
        factory = async token => { await Task.Delay(300); late = ImageArrivalSession.Create(root,CancellationToken.None); return late; };
        want = true; var slow = controller.Update(); want = false; var cancel = controller.Update();
        Until(() => slow.IsCompleted && cancel.IsCompleted,"late start");
        check(late is not null && late.Completion.IsCompleted && controller.Session is null,"A session created after the setting went off is stopped and joined");

        // No folder: reported once, not retried until the setting is turned off and on.
        int failing = 0;
        factory = _ => { failing++; throw new UserError(ScreenshotCards.NoFolder); };
        want = true; var missing = controller.Update(); Until(() => missing.IsCompleted,"missing folder");
        check(controller.Halted && reports.LastOrDefault() == ScreenshotCards.NoFolder && controller.Session is null,"A missing Screenshots folder is reported and nothing else is offered");
        var retry = controller.Update(); Until(() => retry.IsCompleted,"retry");
        check(failing == 1,"A halted watcher is not retried on unrelated settings changes");
        factory = real; want = false; var off = controller.Update(); Until(() => off.IsCompleted,"off");
        want = true; var on = controller.Update(); Until(() => on.IsCompleted,"on");
        check(!controller.Halted && controller.Running,"Off and on again tries once more");
        want = false; var end = controller.Update(); Until(() => end.IsCompleted,"end");
        Directory.Move(root,root + " released");
        check(Directory.Exists(root + " released"),"All screenshot watcher leases are released at the end");
    }

    private static void Card(App app,Action<bool,string> check)
    {
        var template = app.Prefs.MenuTemplate; bool animate = app.Prefs.AnimateMenus;
        app.Prefs.SetMenuPresentation(template,false); // Reduce Motion: deterministic, frame-free.
        var done = new List<Guid>(); var hung = new List<string>();
        var window = new ScreenshotCardWindow(app,path => { hung.Add(path); return Task.CompletedTask; },done.Add);
        try
        {
            nint handle = new WindowInteropHelper(window).EnsureHandle();
            long ex = GetWindowLongPtrW(handle,-20);
            check((ex & RopeWindow.ExNoActivate) != 0 && (ex & RopeWindow.ExToolWindow) != 0 && (ex & RopeWindow.ExAppWindow) == 0,"Screenshot card HWND is non-activating and kept out of the taskbar and Alt+Tab");
            check(SendMessageW(handle,RopeWindow.MouseActivate,handle,0) == RopeWindow.NoActivate,"A click on the card answers MA_NOACTIVATE");
            check(!window.ShowActivated && window.Topmost && !window.ShowInTaskbar && !window.Focusable,"Card is topmost and shown without activation");
            var a = new ArrivalCard<BitmapSource>(Guid.NewGuid(),@"C:\Shots\a.png",Bitmap(200,120),ArrivalKind.Created);
            var b = new ArrivalCard<BitmapSource>(Guid.NewGuid(),@"C:\Shots\b.png",Bitmap(120,200),ArrivalKind.Created);
            window.Present(a); Pump();
            check(window.IsVisible && window.Current == a.Id && window.Clock.Deadline is not null && GetForegroundWindow() != handle,"Presenting shows the card without taking the foreground");
            var area = SystemParameters.WorkArea;
            check(window.Left + window.ActualWidth <= area.Right + 1 && window.Top + window.ActualHeight <= area.Bottom + 1 || !window.IsVisible,"The card stays inside the work area");
            window.Present(b); Pump();
            check(done.SequenceEqual(new[] { a.Id }) && window.Current == b.Id,"A newer screenshot replaces the waiting card and frees the older one");
            window.Leave(false); Pump();
            check(!window.IsVisible && done.Count == 2 && done[1] == b.Id && hung.Count == 0,"Dismissing hides the card and frees it without hanging");
        }
        finally { window.Close(); app.Prefs.SetMenuPresentation(template,animate); Pump(); }
    }

    private static BitmapSource Bitmap(int width,int height)
    {
        var bitmap = BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,new byte[width * height * 4],width * 4); bitmap.Freeze(); return bitmap;
    }
    private static byte[] Png()
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(Bitmap(8,6)));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
}
