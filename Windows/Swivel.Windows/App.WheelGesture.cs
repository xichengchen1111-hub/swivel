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
        if (wheelGesture is null)
        {
            var window = new WheelGestureWindow(this);
            wheelGesture = window;
            window.Closed += (_, _) => { if (ReferenceEquals(wheelGesture, window)) wheelGesture = null; };
        }
        if (wheelGesture.IsFinishing) return;
        // This is the explicitly opened control window. The transient drop
        // target itself must never use this activation path.
        wheelGesture.Show();
        if (wheelGesture.WindowState == WindowState.Minimized) wheelGesture.WindowState = WindowState.Normal;
        wheelGesture.Activate();
    });

    internal async Task OpenGestureFilesAsync(string[] paths, CancellationToken token, Func<bool> stillCurrent)
    {
        void CheckCurrent()
        {
            token.ThrowIfCancellationRequested();
            if (exiting || Busy || !Prefs.IsOn(Feature.Wheel) || !stillCurrent()) throw new OperationCanceledException(token);
        }
        CheckCurrent();
        var snapshot = GestureDropRules.Snapshot(paths);
        // Drop has returned before this method is called. Preflight metadata
        // away from the UI; actual chosen actions acquire their own leases.
        await Task.Run(() =>
        {
            foreach (string path in snapshot)
            {
                token.ThrowIfCancellationRequested();
                NativeFiles.ValidateInput(path);
            }
            token.ThrowIfCancellationRequested();
        }, token);
        CheckCurrent();
        var panel = panels.FirstOrDefault(x => x.IsWheel && !x.IsCustom);
        if (panel is null)
        {
            panel = new ActionWindow(this, true, false);
            panel.SetValidatedFiles(snapshot);
            panels.Add(panel);
            var created = panel;
            created.Closed += (_, _) => panels.Remove(created);
        }
        else panel.SetValidatedFiles(snapshot);
        panel.Show();
        if (panel.WindowState == WindowState.Minimized) panel.WindowState = WindowState.Normal;
        // Explicit successful Drop has completed; normal Wheel interaction is
        // now allowed. Opening this panel never executes a file action.
        panel.Activate();
    }

    private async Task FinishWheelGestureAsync()
    {
        var window = wheelGesture;
        if (window is not null) await window.FinishAsync();
    }
}
