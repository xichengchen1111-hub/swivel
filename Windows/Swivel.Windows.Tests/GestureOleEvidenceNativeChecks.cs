using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using Swivel.Core;
using Swivel.Windows;

[ComVisible(true), Guid("00000121-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IGestureTestDropSource
{
    [PreserveSig] int QueryContinueDrag([MarshalAs(UnmanagedType.Bool)] bool escape, uint keys);
    [PreserveSig] int GiveFeedback(uint effect);
}

internal static class GestureOleEvidenceNativeChecks
{
    private static void Wait(WaitHandle signal)
    {
        if (!signal.WaitOne(TimeSpan.FromSeconds(10))) throw new TimeoutException("OLE capture fixture did not settle.");
    }
    internal static void Run(Action<bool, string> check)
    {
        using var fakeReady = new ManualResetEvent(false);
        using var releaseFake = new ManualResetEvent(false);
        using var before = new ManualResetEvent(false);
        using var start = new ManualResetEvent(false);
        using var after = new ManualResetEvent(false);
        using var finish = new ManualResetEvent(false);
        using var feedback = new ManualResetEvent(false);
        using var cancel = new ManualResetEvent(false);
        uint threadId = 0;
        bool fakeCaptured = false;
        int result = -1;
        IntPtr fixtureHandle = IntPtr.Zero;
        Exception? failure = null;
        var thread = new Thread(() => {
            bool initialized = false;
            try
            {
                Marshal.ThrowExceptionForHR(OleInitialize(IntPtr.Zero)); initialized = true;
                using var window = new HwndSource(new HwndSourceParameters("Swivel ordinary capture fixture") { WindowStyle = unchecked((int)0x80000000), Width = 1, Height = 1 });
                fixtureHandle = window.Handle; threadId = GetCurrentThreadId();
                SetCapture(window.Handle); fakeCaptured = GetCapture() == window.Handle;
                fakeReady.Set(); Wait(releaseFake); ReleaseCapture();
                before.Set(); Wait(start);
                var data = new DataObject(DataFormats.FileDrop, new[] { @"C:\GestureFixture\photo.png" });
                var source = new DropSource(feedback, cancel);
                result = DoDragDrop((System.Runtime.InteropServices.ComTypes.IDataObject)data, source, 1, out _);
                GC.KeepAlive(source); after.Set(); Wait(finish);
            }
            catch (Exception error) { failure = error; }
            finally
            {
                if (initialized) OleUninitialize();
                fakeReady.Set(); before.Set(); after.Set();
            }
        }) { IsBackground = true, Name = "Swivel real OLE capture check" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        void Until(Func<bool> done, string stage)
        {
            try { GestureActionDropNativeChecks.Until(() => done() || failure is not null); }
            catch (TimeoutException error)
            {
                throw new TimeoutException($"OLE fixture timed out at {stage}; {Observe(threadId, out _, out _)}; feedback={feedback.WaitOne(0)}, after={after.WaitOne(0)}, HRESULT=0x{result:X8}.", error);
            }
            if (failure is not null) throw new InvalidOperationException($"OLE fixture failed at {stage}; {Observe(threadId, out _, out _)}.", failure);
            Console.WriteLine($"OLE fixture {stage}: {Observe(threadId, out _, out _)}; HRESULT=0x{result:X8}");
        }
        try
        {
            Until(() => fakeReady.WaitOne(0), "ordinary capture ready");
            check(fakeCaptured && !GestureOleEvidence.IsActive(threadId), "An ordinary window holding SetCapture is not OLE evidence from another thread");
            CheckNoProbe(check, threadId);
            releaseFake.Set(); Until(() => before.WaitOne(0), "before DoDragDrop");
            check(!GestureOleEvidence.IsActive(threadId), "Real source thread has no OLE evidence before DoDragDrop");
            start.Set(); Until(() => feedback.WaitOne(0) || after.WaitOne(0), "DoDragDrop feedback or return");
            // Leave OLE's loop running: a callback can precede acquisition of capture.
            var observation = Stopwatch.StartNew();
            Until(() => {
                Observe(threadId, out IntPtr capture, out _);
                return capture != IntPtr.Zero || after.WaitOne(0) || observation.ElapsedMilliseconds >= 500;
            }, "DoDragDrop capture observation");
            string observed = Observe(threadId, out IntPtr hwndCapture, out string className);
            Console.WriteLine("OLE drag evidence: " + observed);
            // Check this thread itself, not an unrelated foreground drag accepted by IsActive.
            check(feedback.WaitOne(0) && !after.WaitOne(0) && hwndCapture != IntPtr.Zero && className == "CLIPBRDWNDCLASS" && GestureOleEvidence.IsActive(threadId),
                "Real STA DoDragDrop with CF_HDROP exposes the expected OLE capture class: " + observed);
            cancel.Set();
            Until(() => {
                Wake(threadId, fixtureHandle);
                return after.WaitOne(0);
            }, "cancelled DoDragDrop return");
            check(result == 0x00040101, "Fake IDropSource cancels the real DoDragDrop without a person releasing a button");
            check(!GestureOleEvidence.IsActive(threadId), "OLE capture evidence disappears after DoDragDrop while the source thread remains alive");
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"OLE capture evidence failed; {Observe(threadId, out _, out _)}; feedback={feedback.WaitOne(0)}, after={after.WaitOne(0)}, HRESULT=0x{result:X8}.", error);
        }
        finally
        {
            releaseFake.Set(); start.Set(); cancel.Set(); finish.Set();
            try
            {
                GestureActionDropNativeChecks.Until(() => {
                    if (thread.Join(0)) return true;
                    Wake(threadId, fixtureHandle);
                    return false;
                });
            }
            catch (TimeoutException error)
            {
                throw new TimeoutException("OLE fixture thread did not join after cancellation; " + Observe(threadId, out _, out _), error);
            }
        }
        if (failure is not null) throw new InvalidOperationException("OLE capture fixture failed.", failure);
    }
    private static void Wake(uint thread, IntPtr fixture)
    {
        // Queue only to the fixture's drag thread/windows, never inject physical input.
        Observe(thread, out IntPtr capture, out _);
        if (capture != IntPtr.Zero) PostMessage(capture, 0x0200, IntPtr.Zero, IntPtr.Zero);
        else if (fixture != IntPtr.Zero) PostMessage(fixture, 0x0200, IntPtr.Zero, IntPtr.Zero);
        if (thread != 0) PostThreadMessage(thread, 0x0200, IntPtr.Zero, IntPtr.Zero);
    }
    private static string Observe(uint thread, out IntPtr capture, out string className)
    {
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        bool read = thread != 0 && GetGUIThreadInfo(thread, ref info);
        int error = read || thread == 0 ? 0 : Marshal.GetLastPInvokeError();
        capture = info.Capture;
        var name = new StringBuilder(256);
        className = capture != IntPtr.Zero && GetClassName(capture, name, name.Capacity) != 0 ? name.ToString() : "";
        return $"dragThread={thread}, GetGUIThreadInfo={read}, error={error}, hwndCapture=0x{capture.ToInt64():X}, class='{className}'";
    }
    private static void CheckNoProbe(Action<bool, string> check, uint pointerThread)
    {
        long elapsed = 0;
        var target = new GestureDropWindow((App)Application.Current, () => true, (_, _) => { }, error => throw error,
            () => true, () => elapsed);
        try
        {
            target.ShowAt(new(Guid.NewGuid(), 1, Stopwatch.GetTimestamp(), 300, 300, false, pointerThread));
            check(target.Probe is null && new WindowInteropHelper(target).Handle == IntPtr.Zero && !target.IsVisible,
                "A gesture with ordinary capture creates no probe or WPF HWND");
            elapsed = WheelGestureFlow.EvidenceMilliseconds - 1;
            // Let a real dispatcher poll run before expiring the injected monotonic clock.
            var ticks = Stopwatch.StartNew();
            GestureActionDropNativeChecks.Until(() => ticks.ElapsedMilliseconds >= GestureOleEvidence.PollMilliseconds * 2);
            check(target.Probe is null && new WindowInteropHelper(target).Handle == IntPtr.Zero && !target.IsVisible,
                "Repeated polling without OLE evidence never creates a window");
            elapsed = WheelGestureFlow.EvidenceMilliseconds;
            GestureActionDropNativeChecks.Until(() => target.Completion.IsCompleted);
            target.Completion.GetAwaiter().GetResult();
            check(target.Probe is null && new WindowInteropHelper(target).Handle == IntPtr.Zero && !target.IsVisible,
                "An evidence timeout joins the session with no probe window");
        }
        finally { GestureActionDropNativeChecks.Close(target); }
    }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class DropSource(ManualResetEvent feedback, ManualResetEvent cancel) : IGestureTestDropSource
    {
        public int QueryContinueDrag(bool escape, uint keys) => cancel.WaitOne(0) ? 0x00040101 : 0;
        public int GiveFeedback(uint effect) { feedback.Set(); return 0; }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
    {
        internal uint Size, Flags;
        internal IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        internal Rect CaretRect;
    }
    [DllImport("user32.dll", SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int count);
    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW")] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostThreadMessage(uint thread, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("ole32.dll")] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern void OleUninitialize();
    [DllImport("ole32.dll")] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int DoDragDrop([MarshalAs(UnmanagedType.Interface)] System.Runtime.InteropServices.ComTypes.IDataObject data, [MarshalAs(UnmanagedType.Interface)] IGestureTestDropSource source, uint allowed, out uint effect);
    [DllImport("kernel32.dll")] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern IntPtr GetCapture();
    [DllImport("user32.dll")] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReleaseCapture();
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
