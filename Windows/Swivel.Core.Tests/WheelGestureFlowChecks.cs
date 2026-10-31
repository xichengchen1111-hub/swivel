using System;
using Swivel.Core;

internal static class WheelGestureFlowChecks
{
    internal static void Run(Action<bool, string> check)
    {
        WheelGestureFlow Waiting(long now = 0) { var f = new WheelGestureFlow(); check(f.Begin(now), "Gesture waits without creating a target"); return f; }
        WheelGestureFlow Hidden(long now = 0) { var f = Waiting(now); check(f.ObserveEvidence(now, true, true) && f.Phase == WheelGesturePhase.HiddenTarget, "Only OLE evidence permits a hidden target"); return f; }
        var waiting = Waiting();
        check(waiting.ObserveEvidence(0, true, false) && waiting.Phase == WheelGesturePhase.WaitingForEvidence && !waiting.ConfirmFiles(0, true), "No OLE evidence means no probe or visible Wheel");
        check(waiting.ObserveEvidence(499, true, false) && !waiting.ObserveEvidence(500, true, true) && waiting.Phase == WheelGesturePhase.Cancelled, "Evidence at the 500ms deadline cannot show a probe");
        check(!waiting.ObserveEvidence(501, true, true) && !waiting.Begin(501), "Evidence timeout is terminal");
        waiting = Waiting(); check(!waiting.ObserveEvidence(1, false, true) && waiting.Phase == WheelGesturePhase.Cancelled, "Button up before evidence cancels without a probe");
        waiting = Waiting(); check(waiting.ObserveEvidence(499, true, true) && waiting.Tick(948, true) && !waiting.Tick(949, true), "Evidence just before timeout starts a separate bounded file probe");
        waiting = Waiting(100); check(!waiting.ObserveEvidence(99, true, true), "Backward time cannot admit OLE evidence");
        waiting = Hidden(); check(!waiting.ObserveEvidence(1, true, false) && waiting.Phase == WheelGesturePhase.Cancelled, "Lost OLE capture removes an unconfirmed probe");
        waiting = Hidden(); waiting.ConfirmFiles(0, true); check(!waiting.ObserveEvidence(1, true, false), "Lost capture while held cancels a visible Wheel");
        var flow = Hidden();
        check(!flow.Begin(0) && !flow.TryDrop(0, 0), "Repeated arm and unconfirmed drop cannot execute");
        check(!flow.ConfirmFiles(0, false) && flow.Phase == WheelGesturePhase.Cancelled, "Non-file OLE drag cancels invisibly");
        flow = Hidden(); check(flow.Tick(449, true) && !flow.Tick(450, true), "Hidden probe expires in 450ms without a file DragEnter");
        flow = Hidden(); check(!flow.Tick(1, false) && !flow.ConfirmFiles(1, true), "Button release cancels hidden probe and late enter");
        flow = Hidden(); check(flow.ConfirmFiles(449, true) && flow.Phase == WheelGesturePhase.Wheel, "Only file DragEnter confirms the wheel");
        check(!flow.ConfirmFiles(449, true) && flow.Tick(450, true), "Repeated enter never rearms or refreshes expiry");
        check(flow.Tick(451, false) && flow.TryDrop(452, 3) && flow.Phase == WheelGesturePhase.Dropped, "OLE Drop has a short release grace and supplies an item");
        check(!flow.TryDrop(453, 3) && !flow.Tick(454, true), "Duplicate and stale events after drop are ignored");
        foreach (int hit in new[] { -3, -2, -1, 8, 99 }) { flow = Hidden(); flow.ConfirmFiles(0, true); check(!flow.TryDrop(1, hit) && flow.Phase == WheelGesturePhase.Cancelled, "Hub, pages and outside drops cancel"); }
        flow = Hidden(); flow.ConfirmFiles(0, true); flow.Tick(1, false); check(!flow.Tick(151, false) && !flow.TryDrop(152, 0), "Unclaimed release expires after 150ms");
        flow = Hidden(); flow.ConfirmFiles(0, true); check(!flow.Tick(30000, true), "Visible wheel has bounded lifetime");
        flow = Hidden(100); check(!flow.ConfirmFiles(99, true), "Backward timestamps cancel");
        flow = Hidden(); flow.Cancel(); check(!flow.ConfirmFiles(1, true) && !flow.TryDrop(1, 0), "Explicit cancellation is terminal");
        foreach (var template in Enum.GetValues<MenuTemplate>())
            for (int count = 1; count <= 8; count++)
            {
                var tiles = MenuGeometry.Layout(template, count);
                for (int i = 0; i < count; i++) check(MenuGeometry.HitTest(template, count, tiles[i].X + tiles[i].Width / 2, tiles[i].Y + tiles[i].Height / 2) == i, "Shared template hit test selects each item center");
                check(MenuGeometry.HitTest(template, count, -1, 50) == -1 && MenuGeometry.HitTest(template, count, double.NaN, 0) == -1, "Outside and invalid points never select an item");
            }
        check(MenuGeometry.HitTest(MenuTemplate.Wheel, 8, 310, 225) == -1, "Wheel hub cancels");
        flow = Hidden(); flow.ConfirmFiles(0, true);
        check(!flow.HoverPage(0, 1, 3) && !flow.HoverPage(699, 1, 3) && flow.HoverPage(700, 1, 3) && flow.Page == 1, "Page turns only after a 700ms drag dwell");
        check(!flow.HoverPage(701, -1, 3) && !flow.HoverPage(1400, -1, 3) && flow.HoverPage(1401, -1, 3) && flow.Page == 0, "Direction change starts a fresh page dwell");
        check(!flow.HoverPage(2101, -1, 3) && flow.Page == 0, "Previous at first page never underflows");
        flow.HoverPage(2102, 1, 3); flow.HoverPage(2802, 1, 3);
        check(flow.HoverPage(3502, 1, 3) && flow.Page == 2 && !flow.HoverPage(4202, 1, 3), "Next clamps at final page");
        flow.HoverPage(4203, 0, 3); check(!flow.HoverPage(5000, -1, 3), "Leaving a page control clears dwell");
        flow.Cancel(); check(!flow.HoverPage(6000, -1, 3), "Cancelled gestures cannot turn pages");
        for (int delay = 0; delay <= WheelGestureFlow.ProbeMilliseconds; delay++)
        {
            var state = Hidden();
            bool confirmed = state.ConfirmFiles(delay, true);
            check(confirmed == (delay < WheelGestureFlow.ProbeMilliseconds), "Every probe deadline millisecond has the expected boundary");
            if (confirmed)
            {
                int drops = 0;
                for (int repeat = 0; repeat < 5; repeat++) if (state.TryDrop(delay, 0)) drops++;
                check(drops == 1 && !state.Begin(delay), "Every confirmation admits at most one drop and cannot rearm");
            }
        }
        var health = new WheelGestureHealth();
        check(!health.Observe(0, false, true) && !health.Observe(999, false, true) && health.Observe(1000, false, true), "Only a persistent mismatch reports hook trouble");
        check(!health.Observe(1001, true, true) && !health.Observe(1002, false, true), "Matching state resets hook suspicion");
        check(!health.Observe(999, false, true), "Backward health time resets suspicion");
        var prefs = new Prefs(); check(!prefs.ListenForWheelGesture && !prefs.Serialize().Contains("ListenForWheelGesture"), "Always listening defaults off and is omitted");
        prefs.SetListenForWheelGesture(true); check(Prefs.Parse(prefs.Serialize()).ListenForWheelGesture, "Explicit listening choice persists");
        prefs.SetListenForWheelGesture(false); check(!Prefs.Parse(prefs.Serialize()).ListenForWheelGesture, "Disabled listening roundtrips");
    }
}
