using System;
using System.Threading;
using System.Windows.Interop;
using Swivel.Core;
using Swivel.Windows;

internal static class WheelGestureLiveChecks
{
    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        // Windows-only installation/stop fixture. It never injects input or reads clipboard data.
        // Actual physical drags, DPI, application interop and secure desktops still need manual acceptance.
        using var source = new HwndSource(new HwndSourceParameters("Swivel gesture lifecycle checks") { ParentWindow = new IntPtr(-3) });
        reject(() => WheelGestureSession.StartAsync(WheelGestureModifier.Control, source.Handle, new CancellationToken(true)).GetAwaiter().GetResult(), "Live pre-cancelled listener installs nothing");
        var session = WheelGestureSession.StartAsync(WheelGestureModifier.Control, source.Handle, default).GetAwaiter().GetResult();
        try { check(session.Snapshot.Status == WheelGestureStatus.Running, "Windows installed the low-level keyboard hook on its message thread"); }
        finally { session.StopAsync().GetAwaiter().GetResult(); }
        check(session.Completion.IsCompletedSuccessfully && session.Snapshot.Status == WheelGestureStatus.Stopped && session.Snapshot.Issues.Count == 0, "Windows hook removal and thread termination complete before returning");
        check(!session.TryTakeRequest(out _), "Stopped live session has no late display request");
    }
}
