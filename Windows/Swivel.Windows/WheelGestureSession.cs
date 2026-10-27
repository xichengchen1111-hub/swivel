using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Swivel.Core;

namespace Swivel.Windows;

internal interface IWheelGestureSession
{
    WheelGestureSnapshot Snapshot { get; }
    Task Completion { get; }
    bool TryTakeRequest(out WheelGestureRequest? request);
    Task StopAsync();
}

internal sealed class WheelGestureSession : IWheelGestureSession
{
    internal const int NotificationMessage = 0x8379;
    private const int MaxIssues = 8;
    private readonly object gate = new();
    private readonly Guid sessionId = Guid.NewGuid();
    private readonly WheelGestureModifier modifier;
    private readonly IntPtr notificationHwnd;
    private readonly IWheelGestureNative native;
    private readonly EventWaitHandle stop = new(false, EventResetMode.ManualReset);
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource complete = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<WheelGestureIssue> issues = new();
    private readonly WheelGestureRequests requests;
    private readonly WheelKeyboardHook hook;
    private readonly Thread thread;
    private WheelGestureEdges? edges;
    private WheelGestureIssue? callbackFault;
    private WheelGestureStatus status = WheelGestureStatus.Starting;
    private long revision;
    private int stopping, omittedIssues;
    private bool eventDisposed, callbackFaultRecorded;

    private WheelGestureSession(WheelGestureModifier modifier, IntPtr notificationHwnd, IWheelGestureNative native)
    {
        this.modifier = modifier; this.notificationHwnd = notificationHwnd; this.native = native;
        requests = new(sessionId); hook = Callback;
        thread = new Thread(Run) { IsBackground = true, Name = "Swivel Wheel gesture" };
    }
    internal static Task<IWheelGestureSession> StartAsync(WheelGestureModifier modifier, IntPtr notificationHwnd, CancellationToken token)
        => StartAsync(modifier, notificationHwnd, token, new WheelGestureNative());
    internal static async Task<IWheelGestureSession> StartAsync(WheelGestureModifier modifier, IntPtr notificationHwnd, CancellationToken token, IWheelGestureNative native)
    {
        token.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(modifier)) throw new ArgumentOutOfRangeException(nameof(modifier));
        if (notificationHwnd == IntPtr.Zero || !native.IsOwnWindow(notificationHwnd))
            throw new UserError("The gesture control window is no longer available.");
        var session = new WheelGestureSession(modifier, notificationHwnd, native);
        try { session.thread.Start(); }
        catch { session.stop.Dispose(); throw; }
        _ = session.JoinAsync();
        CancellationTokenRegistration registration;
        try { registration = token.Register(session.RequestStop); }
        catch { await session.StopAsync().ConfigureAwait(false); throw; }
        using var registrationScope = registration;
        await session.ready.Task.ConfigureAwait(false);
        if (token.IsCancellationRequested || session.Snapshot.Status != WheelGestureStatus.Running)
        {
            await session.StopAsync().ConfigureAwait(false);
            // Preserve cleanup faults, including when creation was also cancelled.
            var snapshot = session.Snapshot;
            if (snapshot.Issues.Count != 0)
            {
                var errors = new List<Exception>();
                if (token.IsCancellationRequested) errors.Add(new OperationCanceledException(token));
                foreach (var issue in snapshot.Issues) errors.Add(IssueException(issue));
                throw new AggregateException(errors);
            }
            token.ThrowIfCancellationRequested();
            throw new UserError("Windows could not start the gesture listener.");
        }
        return session;
    }
    private static Exception IssueException(WheelGestureIssue issue)
        => issue.NativeErrorCode.HasValue ? new Win32Exception(issue.NativeErrorCode.Value, issue.Message) : new UserError(issue.Message);

    public WheelGestureSnapshot Snapshot
    {
        get
        {
            lock (gate)
            {
                var copy = new List<WheelGestureIssue>(issues);
                var fault = Volatile.Read(ref callbackFault);
                int omitted = omittedIssues;
                if (fault != null && !callbackFaultRecorded)
                {
                    if (copy.Count < MaxIssues) copy.Add(fault);
                    else if (omitted < int.MaxValue) omitted++;
                }
                var actual = status;
                if (Volatile.Read(ref stopping) != 0 && actual is WheelGestureStatus.Starting or WheelGestureStatus.Running)
                    actual = WheelGestureStatus.Stopping;
                return new(sessionId, revision + (fault != null ? 1 : 0), actual, Array.AsReadOnly(copy.ToArray()), omitted);
            }
        }
    }
    public Task Completion => complete.Task;
    public Task StopAsync() { RequestStop(); return complete.Task; }
    private void RequestStop()
    {
        Interlocked.Exchange(ref stopping, 1);
        requests.Stop();
        lock (gate)
        {
            if (!eventDisposed) stop.Set();
            if (status is WheelGestureStatus.Starting or WheelGestureStatus.Running) { status = WheelGestureStatus.Stopping; revision++; }
        }
    }
    public bool TryTakeRequest(out WheelGestureRequest? request)
    {
        request = null;
        if (Snapshot.Status != WheelGestureStatus.Running) return false;
        try
        {
            bool right = native.PrimaryIsRight;
            bool down = native.IsDown(right ? 2 : 1);
            if (!requests.TryTake(Stopwatch.GetTimestamp(), Stopwatch.Frequency, down, right, out var candidate) ||
                Volatile.Read(ref stopping) != 0) return false;
            request = candidate; return true;
        }
        catch { CallbackFailed("Windows could not read the gesture pointer state.", null); return false; }
    }
    private void Run()
    {
        IntPtr installed = IntPtr.Zero;
        try
        {
            native.InitializeQueue();
            if (Volatile.Read(ref stopping) != 0) return;
            int left = modifier switch { WheelGestureModifier.Control => 0xA2, WheelGestureModifier.Shift => 0xA0, _ => 0xA4 };
            // Do not mistake startup auto-repeat for a new press. In the callback itself the
            // changed key's asynchronous state is stale, so subsequent edges use hook data only.
            edges = new(modifier, native.IsDown(left), native.IsDown(left + 1));
            installed = native.Install(hook);
            if (installed == IntPtr.Zero)
            {
                AddIssue("Windows could not install the gesture listener.", native.LastError); return;
            }
            lock (gate)
            {
                if (Volatile.Read(ref stopping) == 0) { status = WheelGestureStatus.Running; revision++; }
            }
            ready.TrySetResult();
            Wake();
            if (Volatile.Read(ref stopping) == 0) native.Pump(stop);
        }
        catch (Exception error)
        {
            AddIssue("The gesture listener stopped because its message loop failed.", error is Win32Exception win32 ? win32.NativeErrorCode : null);
        }
        finally
        {
            Interlocked.Exchange(ref stopping, 1); requests.Stop();
            if (installed != IntPtr.Zero)
            {
                try { if (!native.Unhook(installed)) AddIssue("Windows could not remove the gesture hook. The listener thread has been stopped.", native.LastError); }
                catch (Exception error) { AddIssue("Windows could not remove the gesture hook. The listener thread has been stopped.", error is Win32Exception win32 ? win32.NativeErrorCode : null); }
            }
            ready.TrySetResult();
            // Public Completion is deliberately not completed on this thread. JoinAsync waits
            // for this signal and then Thread.Join, including the last callback and finally.
            ended.TrySetResult();
        }
    }
    private async Task JoinAsync()
    {
        await ended.Task.ConfigureAwait(false);
        thread.Join();
        lock (gate)
        {
            var fault = Volatile.Read(ref callbackFault);
            if (fault != null) { AddIssueLocked(fault); callbackFaultRecorded = true; }
            try { stop.Dispose(); }
            catch { AddIssueLocked(new("Windows could not release a gesture listener resource.")); }
            eventDisposed = true;
            status = issues.Count == 0 ? WheelGestureStatus.Stopped : WheelGestureStatus.Faulted;
            revision++;
        }
        // The hook delegate stays rooted through termination even when Unhook reports failure.
        GC.KeepAlive(hook);
        WakeFinal();
        complete.TrySetResult();
    }
    private IntPtr Callback(int code, IntPtr message, IntPtr data)
    {
        try
        {
            if (code == 0 && Volatile.Read(ref stopping) == 0 && edges != null)
            {
                long kind = message.ToInt64();
                if (kind is 0x100 or 0x101 or 0x104 or 0x105)
                {
                    var keyboard = Marshal.PtrToStructure<WheelKeyboardData>(data);
                    uint key = NormalizeKey(keyboard);
                    if (edges.Observe(key, kind is 0x100 or 0x104, (keyboard.Flags & 0x12) != 0))
                    {
                        bool right = native.PrimaryIsRight;
                        if (native.IsDown(right ? 2 : 1))
                        {
                            if (!native.PhysicalPoint(out int x, out int y)) CallbackFailed("Windows could not read the gesture pointer state.", native.LastError);
                            else if (requests.Offer(Stopwatch.GetTimestamp(), x, y, right)) Wake();
                        }
                    }
                }
            }
        }
        catch { CallbackFailed("The gesture listener stopped because an input callback failed.", null); }
        // Never consume or alter input. No UI, subscribers, file reads, log writes or blocking
        // dispatch are performed in the callback; PostMessage only wakes the owning UI.
        try { return native.Forward(code, message, data); }
        catch { CallbackFailed("The gesture listener stopped because forwarding input failed.", null); return IntPtr.Zero; }
    }
    internal static uint NormalizeKey(WheelKeyboardData keyboard) => keyboard.VirtualKey switch
    {
        0x11 => (keyboard.Flags & 1) != 0 ? 0xA3u : 0xA2u,
        0x12 => (keyboard.Flags & 1) != 0 ? 0xA5u : 0xA4u,
        0x10 => keyboard.ScanCode switch { 0x2A => 0xA0u, 0x36 => 0xA1u, _ => 0 },
        _ => keyboard.VirtualKey
    };
    private void CallbackFailed(string message, int? code)
    {
        Interlocked.CompareExchange(ref callbackFault, new WheelGestureIssue(message, code), null);
        Interlocked.Exchange(ref stopping, 1); requests.Stop();
        // Called on the hook thread (or TryTake before completion). The event is not disposed
        // until after Thread.Join; the public TryTake exception path also checks disposal.
        try { stop.Set(); } catch (ObjectDisposedException) { }
    }
    private void Wake()
    {
        if (!native.Notify(notificationHwnd, NotificationMessage))
            CallbackFailed("Windows could not notify the gesture control window.", native.LastError);
    }
    private void WakeFinal()
    {
        try
        {
            if (!native.Notify(notificationHwnd, NotificationMessage)) AddIssue("Windows could not notify the gesture control window.", native.LastError);
        }
        catch { AddIssue("Windows could not notify the gesture control window.", null); }
        lock (gate) if (issues.Count != 0) { status = WheelGestureStatus.Faulted; revision++; }
    }
    private void AddIssue(string message, int? code)
    {
        lock (gate) AddIssueLocked(new(message, code));
    }
    private void AddIssueLocked(WheelGestureIssue issue)
    {
        if (issues.Count < MaxIssues) issues.Add(issue); else if (omittedIssues < int.MaxValue) omittedIssues++;
        revision++;
    }
}

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate IntPtr WheelKeyboardHook(int code, IntPtr message, IntPtr data);
[StructLayout(LayoutKind.Sequential)]
internal struct WheelKeyboardData
{
    internal uint VirtualKey, ScanCode, Flags, Time;
    internal UIntPtr ExtraInfo;
}
internal interface IWheelGestureNative
{
    bool IsOwnWindow(IntPtr hwnd);
    void InitializeQueue();
    bool PrimaryIsRight { get; }
    bool IsDown(int virtualKey);
    bool PhysicalPoint(out int x, out int y);
    int LastError { get; }
    IntPtr Install(WheelKeyboardHook hook);
    bool Unhook(IntPtr hook);
    IntPtr Forward(int code, IntPtr message, IntPtr data);
    bool Notify(IntPtr hwnd, int message);
    void Pump(WaitHandle stop);
}
internal sealed class WheelGestureNative : IWheelGestureNative
{
    public int LastError => Marshal.GetLastPInvokeError();
    public bool IsOwnWindow(IntPtr hwnd) => IsWindow(hwnd) && GetWindowThreadProcessId(hwnd, out uint process) != 0 && process == GetCurrentProcessId();
    public void InitializeQueue() => PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
    public bool PrimaryIsRight => GetSystemMetrics(23) != 0;
    public bool IsDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    public bool PhysicalPoint(out int x, out int y)
    {
        bool ok = GetPhysicalCursorPos(out var point); x = point.X; y = point.Y; return ok;
    }
    public IntPtr Install(WheelKeyboardHook hook) => SetWindowsHookEx(13, hook, GetModuleHandle(null), 0);
    public bool Unhook(IntPtr hook) => UnhookWindowsHookEx(hook);
    public IntPtr Forward(int code, IntPtr message, IntPtr data) => CallNextHookEx(IntPtr.Zero, code, message, data);
    public bool Notify(IntPtr hwnd, int message) => PostMessage(hwnd, message, IntPtr.Zero, IntPtr.Zero);
    public void Pump(WaitHandle stop)
    {
        // GetMessage alone cannot be woken reliably by Stop if posting to the thread fails.
        // This event remains usable until the external join has observed real termination.
        var handles = new[] { stop.SafeWaitHandle.DangerousGetHandle() };
        while (true)
        {
            uint wait = MsgWaitForMultipleObjectsEx(1, handles, uint.MaxValue, 0x04FF, 0x0004);
            if (wait == 0) return;
            if (wait == uint.MaxValue) throw new Win32Exception(Marshal.GetLastPInvokeError());
            if (wait != 1) throw new InvalidOperationException();
            // Limit each drain so continuously arriving messages cannot starve Stop.
            for (int count = 0; count < 64 && !stop.WaitOne(0) && PeekMessage(out var message, IntPtr.Zero, 0, 0, 1); count++)
            {
                if (message.Message == 0x0012) return;
                TranslateMessage(ref message); DispatchMessage(ref message);
            }
            if (stop.WaitOne(0)) return;
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MessageData { public IntPtr Hwnd; public uint Message; public UIntPtr WParam; public IntPtr LParam; public uint Time; public Point Point; public uint Private; }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr hwnd);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetPhysicalCursorPos(out Point point);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr GetModuleHandle(string? name);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int kind, WheelKeyboardHook callback, IntPtr module, uint thread);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("user32.dll", SetLastError = true)] private static extern uint MsgWaitForMultipleObjectsEx(uint count, [In] IntPtr[] handles, uint milliseconds, uint wakeMask, uint flags);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("user32.dll", EntryPoint = "PeekMessageW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PeekMessage(out MessageData message, IntPtr hwnd, uint first, uint last, uint remove);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TranslateMessage(ref MessageData message);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern IntPtr DispatchMessage(ref MessageData message);
}
