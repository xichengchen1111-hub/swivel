using System;
using System.Linq;
using System.Threading.Tasks;
using Swivel.Core;

internal static class WheelGestureChecks
{
    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        foreach (var (modifier, left, right, generic) in new[] {
            (WheelGestureModifier.Control, 0xA2u, 0xA3u, 0x11u),
            (WheelGestureModifier.Shift, 0xA0u, 0xA1u, 0x10u),
            (WheelGestureModifier.Alt, 0xA4u, 0xA5u, 0x12u) })
        {
            var state = new WheelGestureEdges(modifier);
            check(state.IsSelectedKey(left) && state.IsSelectedKey(right) && state.IsSelectedKey(generic), "Both modifier sides and generic virtual key recognized");
            for (uint key = 0; key <= 255; key++)
                if (key != left && key != right && key != generic)
                    check(!state.Observe(key, true, false) && !state.Observe(key, false, false), "Other keys never create or retain gesture state");
            check(!state.Observe(left, true, true), "Injected down does not trigger");
            check(state.Observe(left, true, false), "Real selected down creates one edge");
            for (int i = 0; i < 20; i++) check(!state.Observe(left, true, false), "Auto-repeat cannot create additional edges");
            check(!state.Observe(right, true, false), "Opposite modifier side while held does not trigger");
            check(!state.Observe(left, false, false) && !state.Observe(right, true, false), "Still-held opposite side suppresses repeat");
            check(!state.Observe(right, false, true) && !state.Observe(right, true, false), "Injected release cannot rearm a real held modifier");
            check(!state.Observe(right, false, false) && state.Observe(left, true, false), "A full real release rearms a new edge");
            check(!state.Observe(left, false, false) && state.Observe(generic, true, false), "Generic modifier down is supported without key text");
            check(!state.Observe(generic, true, false) && !state.Observe(generic, false, false), "Generic repeats and release do not trigger");
            state = new(modifier, true, false);
            check(!state.Observe(left, true, false) && !state.Observe(right, true, false), "Modifier already held at Start is not a new edge");
            state.Observe(left, false, false); state.Observe(right, false, false);
            check(state.Observe(right, true, false), "Startup-held state can rearm after both releases");
        }
        reject(() => new WheelGestureEdges((WheelGestureModifier)99), "Unknown modifier refused");
        Guid id = Guid.NewGuid(); var pending = new WheelGestureRequests(id);
        check(pending.Offer(1000, -400, 200, false), "First display request fits");
        for (int i = 0; i < 100; i++) check(!pending.Offer(1000 + i, 999, 999, true), "At most one pending display request, never an unbounded event queue");
        check(pending.TryTake(1500, 1000, true, false, out var first) && first!.SessionId == id && first.PhysicalX == -400 && first.PhysicalY == 200 && first.Timestamp == 1000, "500ms inclusive request retains physical negative-monitor coordinates");
        check(!pending.TryTake(1500, 1000, true, false, out _), "A request is delivered once");
        pending.Offer(2000, 0, 0, false);
        check(!pending.TryTake(2501, 1000, true, false, out _) && !pending.TryTake(2000, 1000, true, false, out _), "Stale requests are discarded, not postponed");
        pending.Offer(2000, 0, 0, false);
        check(!pending.TryTake(1999, 1000, true, false, out _), "Future or backward-clock request refused");
        pending.Offer(2000, 0, 0, false);
        check(!pending.TryTake(2000, 1000, false, false, out _), "Primary release before popup delivery refuses display");
        pending.Offer(2000, 0, 0, false);
        check(!pending.TryTake(2000, 1000, true, true, out _), "Changed primary button mapping refuses stale request");
        pending.Offer(2000, 0, 0, true);
        check(pending.TryTake(2000, 1000, true, true, out var swapped) && swapped!.PrimaryIsRight, "Swapped physical right primary is supported");
        foreach (long frequency in new[] { 1L, 10_000_000, long.MaxValue })
        {
            pending.Offer(0, 0, 0, false);
            check(pending.TryTake(frequency / 2, frequency, true, false, out _), "Expiration arithmetic has no frequency multiplication overflow");
        }
        pending.Offer(0, 0, 0, false); check(!pending.TryTake(long.MaxValue, 1, true, false, out _), "Very old timestamps fail without overflow");
        pending.Offer(0, 0, 0, false); check(!pending.TryTake(0, 0, true, false, out _), "Invalid timestamp frequency refuses display");
        pending.Offer(0, 0, 0, false); pending.Stop();
        check(!pending.TryTake(0, 1, true, false, out _) && !pending.Offer(0, 0, 0, false), "Stop discards pending and permanently invalidates its session");
        for (int i = 0; i < 200; i++)
        {
            var concurrent = new WheelGestureRequests(Guid.NewGuid()); int accepted = 0;
            Parallel.For(0, 16, _ => { if (concurrent.Offer(0, 0, 0, false)) System.Threading.Interlocked.Increment(ref accepted); });
            check(accepted == 1, "Concurrent offers retain exactly one pending request");
            Parallel.Invoke(concurrent.Stop, () => concurrent.Offer(0, 0, 0, false));
            check(!concurrent.TryTake(0, 1, true, false, out _), "Concurrent Stop never leaves a deliverable pending request");
        }
        var input = new[] { @"C:\One.png", @"C:\two.PNG" };
        var copy = GestureDropRules.Snapshot(input); input[0] = "changed";
        check(copy.SequenceEqual(new[] { @"C:\One.png", @"C:\two.PNG" }), "Drop snapshot isolates OLE array, preserving exact order and case");
        check(GestureDropRules.Snapshot(Enumerable.Range(0, 100).Select(i => "C:\\" + i).ToArray()).Length == 100, "100 paths accepted");
        check(GestureDropRules.Snapshot(new[] { new string('x', 240) })[0].Length == 240, "240 UTF16 boundary accepted without I/O");
        foreach (var bad in new[] { Array.Empty<string>(), new string[101], new[] { "" }, new[] { "a\0b" }, new[] { new string('x', 241) }, new[] { "a", "A" }, new[] { "a", "a" }, new[] { (string)null! } })
            reject(() => GestureDropRules.Snapshot(bad), "Invalid or case-ambiguous drop refuses entire batch");
        reject(() => GestureDropRules.Snapshot(null!), "Null drop array refused");
    }
}
