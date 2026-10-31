using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Swivel.Core;
using Swivel.Windows;

internal static class WheelGestureNativeChecks
{
    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
        => RunAsync(check, reject).GetAwaiter().GetResult();
    private static async Task RunAsync(Action<bool, string> check, Action<Action, string> reject)
    {
        check(Marshal.SizeOf<WheelKeyboardData>() == (IntPtr.Size == 8 ? 24 : 20), "Keyboard hook struct matches DWORD fields and pointer-sized extra information");
        var native = new FakeNative();
        reject(() => WheelGestureSession.StartAsync(WheelGestureModifier.Control, new(1), new CancellationToken(true), native).GetAwaiter().GetResult(), "Pre-cancelled hook creation never installs");
        check(native.Installs == 0, "Pre-cancelled native installation count stays zero");
        native.OwnWindow = false;
        reject(() => WheelGestureSession.StartAsync(WheelGestureModifier.Control, new(1), default, native).GetAwaiter().GetResult(), "Foreign or gone notification HWND refused");
        check(native.Installs == 0, "Invalid HWND installs nothing");
        native.OwnWindow = true;
        var session = await WheelGestureSession.StartAsync(WheelGestureModifier.Control, new(1), default, native);
        try
        {
            await Until(() => native.Entered);
            check(session.Snapshot.Status == WheelGestureStatus.Running && native.Installs == 1, "Explicit Start creates one hook thread");
            reject(() => ((IList<WheelGestureIssue>)session.Snapshot.Issues).Clear(), "Native diagnostic snapshot is readonly");
            native.Send(0xA2, true);
            native.PointerThreadId = 456;
            check(session.TryTakeRequest(out var request) && request!.PhysicalX == -1250 && request.PhysicalY == 40 && !request.PrimaryIsRight, "Hook request carries physical-screen coordinates and selected primary button");
            check(request!.PointerThreadId == 123, "Delivery preserves the thread under the pointer at the gesture press, even if the window under it changes");
            native.Send(0xA2, true); native.Send(0xA3, true);
            check(!session.TryTakeRequest(out _), "Repeat and second modifier side do not create a request");
            native.Send(0xA2, false); native.Send(0xA3, false);
            native.Send(0xA2, true, flags: 0x10); native.Send(0xA2, true, flags: 0x02);
            check(!session.TryTakeRequest(out _), "Both injected markers are ignored");
            native.Send(0xA2, true); native.PrimaryDown = false;
            check(!session.TryTakeRequest(out _), "Primary release before delivery rejects the request");
            native.PrimaryDown = true; native.Send(0xA2, false); native.Send(0xA2, true); native.Right = true;
            check(!session.TryTakeRequest(out _), "Primary mapping switch before delivery rejects request");
            native.Send(0xA2, false); native.Send(0xA2, true);
            check(session.TryTakeRequest(out var right) && right!.PrimaryIsRight, "Swapped right physical primary is supported");
            native.Send(0xA2, false); native.Send(0xA2, true); await Task.Delay(550);
            check(!session.TryTakeRequest(out _), "Delayed UI request expires before display");
            int forwarded = native.Forwards;
            native.Send(0, true, code: -1, invalidData: true); native.Send(0, true, code: 1, invalidData: true);
            check(native.Forwards == forwarded + 2, "Negative and unknown-positive hook codes forward without dereferencing data");
            check(native.Forwards == native.Sends, "Every tested input forwards exactly once");
            native.BlockExit.Reset();
            var stopTask = session.StopAsync(); await Until(() => native.Exiting);
            check(!stopTask.IsCompleted && !session.Completion.IsCompleted && !session.TryTakeRequest(out _), "Stop invalidates requests but awaits actual pump and thread termination");
            native.BlockExit.Set(); await stopTask;
            check(native.Unhooks == 1 && session.Completion.IsCompletedSuccessfully && session.Snapshot.Status == WheelGestureStatus.Stopped && native.HookThread is { IsAlive: false }, "Completion follows one unhook and true Thread.Join");
            await session.StopAsync(); check(native.Unhooks == 1, "Repeated Stop does not release resources twice");
        }
        finally { native.BlockExit.Set(); await session.StopAsync(); native.Dispose(); }

        native = new FakeNative { InitialLeft = true };
        session = await WheelGestureSession.StartAsync(WheelGestureModifier.Control, new(1), default, native);
        try
        {
            await Until(() => native.Entered);
            native.Send(0x11, true);
            check(!session.TryTakeRequest(out _), "Startup-held left modifier generic repeat cannot create an edge");
            native.Send(0x11, false); native.Send(0x11, true);
            check(session.TryTakeRequest(out _), "Generic modifier release normalizes to startup left-side state");
        }
        finally { await session.StopAsync(); native.Dispose(); }
        foreach (var (vk, scan, flags, expected) in new[] { (0x11u, 0u, 1u, 0xA3u), (0x12u, 0u, 0u, 0xA4u), (0x12u, 0u, 1u, 0xA5u), (0x10u, 0x2Au, 0u, 0xA0u), (0x10u, 0x36u, 0u, 0xA1u), (0x10u, 0xFFu, 0u, 0u) })
            check(WheelGestureSession.NormalizeKey(new() { VirtualKey = vk, ScanCode = scan, Flags = flags }) == expected, "Generic key normalization uses physical side or rejects ambiguous shift");

        native = new FakeNative { FailInstall = true };
        reject(() => WheelGestureSession.StartAsync(WheelGestureModifier.Alt, new(1), default, native).GetAwaiter().GetResult(), "Failed hook installation is visible");
        check(native.Installs == 1 && native.Unhooks == 0 && native.HookThread is { IsAlive: false }, "Installation failure returns only after failed thread terminates"); native.Dispose();

        native = new FakeNative { FailUnhook = true };
        session = await WheelGestureSession.StartAsync(WheelGestureModifier.Shift, new(1), default, native);
        await session.StopAsync();
        check(session.Snapshot.Status == WheelGestureStatus.Faulted && session.Snapshot.Issues.Any(x => x.Message.Contains("remove", StringComparison.Ordinal) && x.NativeErrorCode == 5) && native.HookThread is { IsAlive: false }, "Unhook failure is retained without skipping thread join"); native.Dispose();

        foreach (string failure in new[] { "notify", "point", "forward", "pump" })
        {
            native = new FakeNative(); session = await WheelGestureSession.StartAsync(WheelGestureModifier.Control, new(1), default, native);
            await Until(() => native.Entered);
            if (failure == "notify") native.FailNotify = true;
            if (failure == "point") native.FailPoint = true;
            if (failure == "forward") native.FailForward = true;
            if (failure == "pump") native.Enqueue(() => throw new InvalidOperationException());
            else native.Send(0xA2, true);
            await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            check(session.Snapshot.Status == WheelGestureStatus.Faulted && session.Snapshot.Issues.Count > 0 && session.Snapshot.Issues.Count <= 8 && native.Unhooks == 1 && native.HookThread is { IsAlive: false }, "Callback, pointer, notification and pump failures remain visible after full cleanup");
            check(!session.TryTakeRequest(out _), "Faulted listener cannot deliver a late request"); native.Dispose();
        }
        native = new FakeNative(); native.InstallGate.Reset(); using var cancel = new CancellationTokenSource();
        var starting = WheelGestureSession.StartAsync(WheelGestureModifier.Control, new(1), cancel.Token, native);
        await Until(() => native.Installs != 0); cancel.Cancel();
        check(!starting.IsCompleted, "Cancellation during blocking installation waits for native return");
        native.InstallGate.Set();
        reject(() => starting.GetAwaiter().GetResult(), "Cancelled start never publishes a session");
        check(native.Unhooks == 1 && native.HookThread is { IsAlive: false }, "Cancelled partial installation unhooks and joins"); native.Dispose();
        native = new FakeNative { FailUnhook = true }; native.InstallGate.Reset(); using var cancelFault = new CancellationTokenSource();
        starting = WheelGestureSession.StartAsync(WheelGestureModifier.Control, new(1), cancelFault.Token, native);
        await Until(() => native.Installs != 0); cancelFault.Cancel(); native.InstallGate.Set();
        try { await starting; check(false, "Cancelled cleanup error must be reported"); }
        catch (AggregateException error) { check(error.InnerExceptions.Any(x => x is OperationCanceledException) && error.InnerExceptions.Count >= 2, "Start cancellation does not conceal cleanup failure"); }
        native.Dispose();
    }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }
    private sealed class FakeNative : IWheelGestureNative, IDisposable
    {
        private readonly ConcurrentQueue<Action> work = new();
        private readonly AutoResetEvent wake = new(false);
        private WheelKeyboardHook? callback;
        internal bool OwnWindow = true, InitialLeft, FailInstall, FailUnhook;
        internal volatile bool PrimaryDown = true, Right, FailNotify, FailPoint, FailForward, Entered, Exiting;
        internal int Installs, Unhooks, Forwards, Sends;
        internal uint PointerThreadId = 123;
        internal Thread? HookThread;
        internal readonly ManualResetEventSlim BlockExit = new(true), InstallGate = new(true);
        public bool IsOwnWindow(IntPtr hwnd) => OwnWindow;
        public void InitializeQueue() { }
        public bool PrimaryIsRight => Right;
        public bool IsDown(int key) => key is 1 or 2 ? PrimaryDown : InitialLeft && key is 0xA0 or 0xA2 or 0xA4;
        public bool PhysicalPoint(out int x, out int y) { x = -1250; y = 40; return !FailPoint; }
        public uint PointerThread(int x, int y) => PointerThreadId;
        public int LastError => 5;
        public IntPtr Install(WheelKeyboardHook hook)
        {
            callback = hook; HookThread = Thread.CurrentThread; Interlocked.Increment(ref Installs); InstallGate.Wait(); return FailInstall ? IntPtr.Zero : new(17);
        }
        public bool Unhook(IntPtr hook) { Interlocked.Increment(ref Unhooks); return !FailUnhook; }
        public IntPtr Forward(int code, IntPtr message, IntPtr data) { Interlocked.Increment(ref Forwards); if (FailForward) throw new InvalidOperationException(); return new(42); }
        public bool Notify(IntPtr hwnd, int message) => !FailNotify;
        public void Pump(WaitHandle stop)
        {
            Entered = true;
            try
            {
                while (WaitHandle.WaitAny(new[] { stop, wake }) == 1)
                    while (work.TryDequeue(out var action)) action();
            }
            finally { Exiting = true; BlockExit.Wait(); }
        }
        internal void Enqueue(Action action) { work.Enqueue(action); wake.Set(); }
        internal void Send(uint key, bool down, uint flags = 0, uint scan = 0, int code = 0, bool invalidData = false)
        {
            using var done = new ManualResetEventSlim(); Exception? error = null;
            Enqueue(() =>
            {
                IntPtr data = invalidData ? new(1) : Marshal.AllocHGlobal(Marshal.SizeOf<WheelKeyboardData>());
                try
                {
                    if (!invalidData) Marshal.StructureToPtr(new WheelKeyboardData { VirtualKey = key, Flags = flags, ScanCode = scan }, data, false);
                    Interlocked.Increment(ref Sends); callback!(code, new(down ? 0x100 : 0x101), data);
                }
                catch (Exception failure) { error = failure; }
                finally { if (!invalidData) Marshal.FreeHGlobal(data); done.Set(); }
            });
            if (!done.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            if (error != null) throw error;
        }
        public void Dispose() { wake.Dispose(); BlockExit.Dispose(); InstallGate.Dispose(); }
    }
}
