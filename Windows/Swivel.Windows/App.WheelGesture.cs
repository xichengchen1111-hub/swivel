using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Swivel.Core;

namespace Swivel.Windows;

public sealed partial class App
{
    private WheelGestureWindow? wheelGesture;

    public void OpenWheelGesture() => Try(() =>
    {
        if (exiting) return;
        if (!Prefs.IsOn(Feature.Wheel)) throw new UserError("Enable Wheel in Settings first.");
        EnsureWheelGesture();
        if (wheelGesture!.IsFinishing) return;
        // This is the explicitly opened control window. The transient drop
        // target itself must never use this activation path.
        wheelGesture.Show();
        if (wheelGesture.WindowState == WindowState.Minimized) wheelGesture.WindowState = WindowState.Normal;
        wheelGesture.Activate();
    });

    private void EnsureWheelGesture()
    {
        if (wheelGesture is not null) return;
        var window = new WheelGestureWindow(this);
        wheelGesture = window;
        window.Closed += (_, _) => { if (ReferenceEquals(wheelGesture, window)) wheelGesture = null; };
    }
    private Task gestureSettings = Task.CompletedTask;
    private bool gestureAutoRequested;
    private Task UpdateWheelGesture()
    {
        bool on = !exiting && Prefs.IsOn(Feature.Wheel) && Prefs.ListenForWheelGesture;
        bool wasOn = gestureAutoRequested; gestureAutoRequested = on;
        if (!Prefs.IsOn(Feature.Wheel) || exiting || (wasOn && !on))
        {
            var old = wheelGesture;
            if (old is not null) gestureSettings = CloseWheelGestureAfter(gestureSettings, old, old.FinishAsync());
        }
        else if (on && !wasOn) gestureSettings = StartWheelGestureAfter(gestureSettings);
        return gestureSettings;
    }
    private async Task CloseWheelGestureAfter(Task previous, WheelGestureWindow window, Task finishing)
    {
        try { await previous; await finishing; if (ReferenceEquals(wheelGesture, window)) window.Close(); }
        catch (Exception error) { if (!exiting) ShowError(error); }
    }
    private async Task StartWheelGestureAfter(Task previous)
    {
        try
        {
            await previous;
            if (validationHost || exiting || !Prefs.IsOn(Feature.Wheel) || !Prefs.ListenForWheelGesture) return;
            EnsureWheelGesture(); wheelGesture!.ListenInBackground();
        }
        catch (Exception error) { if (!exiting) ShowError(error); }
    }
    internal async Task RunGestureActionAsync(ToolAction action, string[] paths, CancellationToken token, Func<bool> stillCurrent)
    {
        void CheckCurrent()
        {
            token.ThrowIfCancellationRequested();
            if (exiting || Busy || !Prefs.IsOn(Feature.Wheel) || !stillCurrent()) throw new OperationCanceledException(token);
        }
        CheckCurrent(); var snapshot = GestureDropRules.Snapshot(paths);
        await Task.Run(() =>
        {
            foreach (string path in snapshot) { token.ThrowIfCancellationRequested(); NativeFiles.ValidateInput(path); }
        }, token);
        CheckCurrent();
        var offered = Catalog.Available(Prefs, snapshot).FirstOrDefault(item => item.Id == action.Id)
            ?? throw new UserError("The dropped action is no longer available. Open Wheel and choose it again.");
        // OLE has returned and its HWND has closed. Only now may an editor take focus.
        if (!GestureFocus.BringForward(Home))
        {
            Home.SetStatus(L(GestureFocus.Refused));
            throw new UserError(GestureFocus.Refused);
        }
        gestureAction = true;
        try { await RunActionAsync(offered, snapshot, Home, Home.SetStatus, cancellationToken: token); }
        finally { gestureAction = false; }
    }
    private bool gestureAction;

    private async Task FinishWheelGestureAsync()
    {
        await gestureSettings;
        var window = wheelGesture;
        if (window is not null) await window.FinishAsync();
    }
}
