using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
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
        Exception? failure = null;
        var thread = new Thread(() => {
            bool initialized = false;
            try
            {
                Marshal.ThrowExceptionForHR(OleInitialize(IntPtr.Zero)); initialized = true;
                using var window = new HwndSource(new HwndSourceParameters("Swivel ordinary capture fixture") { WindowStyle = unchecked((int)0x80000000), Width = 1, Height = 1 });
                threadId = GetCurrentThreadId();
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
        try
        {
            GestureActionDropNativeChecks.Until(() => fakeReady.WaitOne(0));
            if (failure is not null) throw new InvalidOperationException("OLE fixture startup failed.", failure);
            check(fakeCaptured && !GestureOleEvidence.IsActive(threadId), "An ordinary window holding SetCapture is not OLE evidence from another thread");
            CheckNoProbe(check, threadId);
            releaseFake.Set(); GestureActionDropNativeChecks.Until(() => before.WaitOne(0));
            check(!GestureOleEvidence.IsActive(threadId), "Real source thread has no OLE evidence before DoDragDrop");
            start.Set(); GestureActionDropNativeChecks.Until(() => feedback.WaitOne(0));
            // GiveFeedback holds the actual DoDragDrop call open without physical input.
            // This assertion must fail if Windows changes OLE's capture/class behaviour.
            check(GestureOleEvidence.IsActive(threadId), "Real STA DoDragDrop with CF_HDROP exposes the expected OLE capture class to another thread");
            cancel.Set(); GestureActionDropNativeChecks.Until(() => after.WaitOne(0));
            check(result == 0x00040101, "Fake IDropSource cancels the real DoDragDrop without a person releasing a button");
            check(!GestureOleEvidence.IsActive(threadId), "OLE capture evidence disappears after DoDragDrop while the source thread remains alive");
        }
        finally
        {
            releaseFake.Set(); start.Set(); cancel.Set(); finish.Set();
            GestureActionDropNativeChecks.Until(() => !thread.IsAlive); thread.Join();
        }
        if (failure is not null) throw new InvalidOperationException("OLE capture fixture failed.", failure);
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
        public int GiveFeedback(uint effect)
        {
            feedback.Set();
            if (!cancel.WaitOne(TimeSpan.FromSeconds(10))) cancel.Set();
            // Wake only OLE's own captured HWND; no SendInput, cursor move or global input.
            PostMessage(GetCapture(), 0x0200, IntPtr.Zero, IntPtr.Zero);
            return 0;
        }
    }
    [DllImport("ole32.dll")] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern void OleUninitialize();
    [DllImport("ole32.dll")] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int DoDragDrop([MarshalAs(UnmanagedType.Interface)] System.Runtime.InteropServices.ComTypes.IDataObject data, [MarshalAs(UnmanagedType.Interface)] IGestureTestDropSource source, uint allowed, out uint effect);
    [DllImport("kernel32.dll")] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern IntPtr GetCapture();
    [DllImport("user32.dll")] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReleaseCapture();
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
