using System;
using System.Threading.Tasks;
using System.Windows;
using Swivel.Core;

namespace Swivel.Windows;

public sealed partial class App
{
    private ImageArrivalWindow? imageArrivals;
    internal event Action? WorkStateChanged;

    private void NotifyWorkStateChanged()
    {
        // A tool's availability refresh must not interrupt another operation's
        // cancellation, cleanup, or completion signal.
        if (WorkStateChanged is not { } listeners) return;
        foreach (Action listener in listeners.GetInvocationList())
            try { listener(); } catch { }
    }

    public void OpenImageArrivalCards() => Try(() =>
    {
        if (exiting) return;
        if (!Prefs.IsOn(Feature.Clothesline)) throw new UserError("Enable Clothesline in Settings first.");
        if (imageArrivals is null)
        {
            var window = new ImageArrivalWindow(this);
            imageArrivals = window;
            window.Closed += (_, _) => { if (ReferenceEquals(imageArrivals, window)) imageArrivals = null; };
        }
        if (imageArrivals.IsFinishing) return;
        imageArrivals.Show();
        if (imageArrivals.WindowState == WindowState.Minimized) imageArrivals.WindowState = WindowState.Normal;
        imageArrivals.Activate();
    });

    // A caller's awaiting Task may not have completed its continuation even after
    // another window claimed work. Never cancel that other window's operation.
    internal void CancelWorkFrom(Window owner)
    {
        if (ReferenceEquals(workOwner, owner)) CancelWork();
    }

    private async Task FinishImageArrivalsAsync()
    {
        var window = imageArrivals;
        if (window is not null) await window.FinishAsync();
    }
}
