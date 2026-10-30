using System;
using System.Collections.Generic;
using System.Linq;
using Swivel.Core;

internal static class RopeMotionChecks
{
    // Samples a spring from a displacement or a push and reports its visible excursions and when it came to rest.
    private static (int Swings,double RestAt,double Peak) Trace(double response,double damping,double start,double push,double rest,double restVelocity,double step)
    {
        var spring = new MotionSpring(0,response,damping);
        if (start != 0) { spring.Target = start; spring.Snap(); spring.Target = 0; }
        if (push != 0) spring.Nudge(push);
        var peaks = new List<double>(); double current = 0, restAt = -1; int sign = 0;
        for (double t = 0; t <= 3; t += step)
        {
            double value = spring.Value;
            int s = Math.Sign(value);
            if (s != 0 && s != sign && sign != 0) { peaks.Add(current); current = 0; }
            if (s != 0) sign = s;
            current = Math.Max(current,Math.Abs(value));
            bool still = Math.Abs(value) < rest && Math.Abs(spring.Velocity) < restVelocity;
            if (still && restAt < 0) restAt = t; else if (!still) restAt = -1;
            spring.Advance(step);
        }
        peaks.Add(current);
        double first = peaks.Max();
        return (peaks.Count(p => p >= first * RopeTuning.VisibleShare),restAt,first);
    }

    public static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        foreach (var (name,damping) in new[] { ("drop",RopeTuning.DropDamping),("sag",RopeTuning.SagDamping),("swing",RopeTuning.SwingDamping) })
            check(damping >= .6 && damping <= 1,"Rope " + name + " damping ratio at least 0.6");
        foreach (double step in new[] { 1 / 240.0,1 / 60.0,1 / 144.0 })
        {
            var drop = Trace(RopeTuning.DropResponse,RopeTuning.DropDamping,-1,0,RopeTuning.DropRest,RopeTuning.DropRestVelocity,step);
            check(drop.Swings <= 2 && drop.RestAt is >= 0 and <= 1.5,$"Rope lowering settles within 1.5 s with at most 2 visible swings ({step:0.####} s frames)");
            var sag = Trace(RopeTuning.SagResponse,RopeTuning.SagDamping,0,RopeTuning.DropSagKick,RopeTuning.SagRest,RopeTuning.SagRestVelocity,step);
            check(sag.Swings <= 2 && sag.RestAt is >= 0 and <= 1.5 && sag.Peak < 12,$"Rope sag bounce is small, settles within 1.5 s, at most 2 swings ({step:0.####} s frames)");
            foreach (double push in new[] { RopeTuning.DropSwingKick,RopeTuning.ArrivalSwingKick,-RopeTuning.ArrivalSwingKick })
            {
                var swing = Trace(RopeTuning.SwingResponse,RopeTuning.SwingDamping,0,push,RopeTuning.SwingRest,RopeTuning.SwingRestVelocity,step);
                check(swing.Swings <= 2 && swing.RestAt is >= 0 and <= 1.5 && swing.Peak <= RopeTuning.MaxSwing,$"Print swing is gentle (≤{RopeTuning.MaxSwing}°), ≤2 visible swings, rests within 1.5 s ({push} °/s, {step:0.####} s frames)");
            }
        }

        // The whole model: twelve prints lowered, measured with uneven frames, stops moving in time.
        var rope = new RopeMotion(); rope.SetPrints(12);
        check(rope.Drop == 0 && rope.Sag == RopeTuning.RestSag && !rope.Moving && !rope.Lowered,"Rope starts lifted and still");
        rope.Lower();
        check(rope.Moving && rope.Lowered,"Lowering starts motion");
        double time = 0, maxSwing = 0; var rng = new Random(7); bool restedInTime = false;
        while (time < 3)
        {
            double frame = (6 + rng.Next(0,20)) / 1000.0; time += frame;
            bool moving = rope.Advance(frame);
            for (int i = 0; i < 12; i++) maxSwing = Math.Max(maxSwing,Math.Abs(rope.Swing(i)));
            if (!moving) { restedInTime = time <= 1.5; break; }
        }
        check(restedInTime && rope.Drop == 1 && rope.Sag == RopeTuning.RestSag && Enumerable.Range(0,12).All(i => rope.Swing(i) == 0),"Lowered rope with twelve prints comes to rest within 1.5 s so the frame loop stops");
        check(maxSwing > .5 && maxSwing <= RopeTuning.MaxSwing,"Lowering visibly but gently swings the prints");
        var kicks = Enumerable.Range(0,12).Select(i => RopeTuning.DropKick(i,12)).ToArray();
        check(kicks.Zip(kicks.Skip(1)).All(p => Math.Sign(p.First) == -Math.Sign(p.Second)),"Neighbouring prints swing opposite ways");
        reject(() => RopeTuning.DropKick(3,3),"Push index outside the rope refused");

        // Retargeting keeps position and velocity: lifting halfway down turns around without a jump.
        var turn = new RopeMotion(); turn.Lower(); turn.Advance(.12);
        double before = turn.Drop; turn.Lift(); check(turn.Drop == before && turn.Moving,"Lifting mid-way keeps the rope where it is");
        turn.Advance(1 / 60.0); check(Math.Abs(turn.Drop - before) < .12 && turn.Drop > .2,"Lift continues smoothly from the current position");
        while (turn.Advance(1 / 60.0)) { }
        check(turn.Hidden && turn.Drop == 0,"Lifted rope reports hidden once at rest");
        var again = new RopeMotion(); again.SetPrints(2); again.Lower(); again.Advance(.05); again.Lift(); again.Advance(.02); again.Lower();
        check(again.Lowered && again.Moving,"Caught while lifting it turns around");
        var hop = new RopeMotion(); hop.SetPrints(2); hop.Lower(); while (hop.Advance(1 / 60.0)) { }
        hop.Raise(); check(hop.Drop == 0 && !hop.Lowered,"Raise puts the rope up at once");
        hop.Lower(); check(hop.Moving && hop.Advance(1 / 60.0) && hop.Swing(0) != 0,"Lowered again after a raise, the prints swing in afresh");

        // Pushes are clamped to a gentle swing even when repeated.
        var many = new RopeMotion(); many.SetPrints(1); many.Lower(); double worst = 0;
        for (int i = 0; i < 40; i++) { many.Push(0,500); many.Advance(1 / 120.0); worst = Math.Max(worst,Math.Abs(many.Swing(0))); }
        check(worst <= RopeTuning.MaxSwing,"Repeated pushes never exceed the maximum swing");
        reject(() => many.Push(1,1),"Push for a missing print refused");
        reject(() => many.Advance(double.NaN),"Non-finite frame refused");
        reject(() => many.SetPrints(13),"More than twelve prints refused");
        many.SetPrints(3); check(many.Count == 3 && many.Swing(1) == 0 && many.Swing(2) == 0,"New prints start at rest");
        check(!many.Advance(1.0) && many.Drop == 1 && many.Swing(0) == 0,"A stalled frame snaps to rest instead of jumping");
        many.Arrive(2); check(many.Moving && many.Advance(1 / 60.0),"A print hung while the rope is down swings");

        // Frame rate does not change the path (analytic springs).
        var a = new RopeMotion(); var b = new RopeMotion(); a.SetPrints(4); b.SetPrints(4); a.Lower(); b.Lower();
        for (int i = 0; i < 30; i++) a.Advance(1 / 60.0);
        for (int i = 0; i < 120; i++) b.Advance(1 / 240.0);
        check(Math.Abs(a.Drop - b.Drop) < 1e-6 && Math.Abs(a.Sag - b.Sag) < 1e-6 && Enumerable.Range(0,4).All(i => Math.Abs(a.Swing(i) - b.Swing(i)) < 1e-6),"Same motion at 60 and 240 frames per second");

        // Reduce Motion: no travel, no swing, nothing to animate.
        var calm = new RopeMotion { Reduced = true }; calm.SetPrints(5); calm.Lower();
        check(calm.Drop == 1 && !calm.Moving && calm.Sag == RopeTuning.RestSag && calm.Swing(0) == 0,"Reduce Motion lowers instantly without swinging");
        calm.Arrive(0); calm.Push(1,20); check(!calm.Moving && calm.Swing(0) == 0 && !calm.Advance(1 / 60.0),"Reduce Motion ignores pushes");
        calm.Lift(); check(calm.Hidden,"Reduce Motion lifts instantly");
        var midway = new RopeMotion(); midway.SetPrints(2); midway.Lower(); midway.Advance(.1); midway.Reduced = true;
        check(midway.Drop == 1 && midway.Swing(0) == 0 && !midway.Moving,"Turning Reduce Motion on mid-motion snaps to rest");

        // Layout along the rope.
        foreach (double width in new[] { 800.0,1280,1920,3840 })
            for (int count = 0; count <= 12; count++)
            {
                var (size,pegs) = RopeLayout.Pegs(count,width);
                check(pegs.Length == count && pegs.All(x => x - size / 2 >= 0 && x + size / 2 <= width) && pegs.Zip(pegs.Skip(1)).All(p => p.Second - p.First >= size),$"Prints fit the rope without overlapping ({count} on {width})");
            }
        check(RopeLayout.Pegs(12,800).Size < RopeLayout.Print && RopeLayout.Pegs(3,1920).Size == RopeLayout.Print,"Prints shrink only on narrow screens");
        check(RopeLayout.Dip(0,1000,10) == 0 && RopeLayout.Dip(1000,1000,10) == 0 && RopeLayout.Dip(500,1000,10) == 10 && RopeLayout.Dip(250,1000,10) == 7.5,"Rope dips as a parabola between its ends");
        reject(() => RopeLayout.Pegs(13,1000),"Layout refuses more than twelve prints");
        reject(() => RopeLayout.Pegs(1,0),"Layout refuses an empty width");

        EdgeChecks(check);

        // The setting: off by default, not written while off, older settings still load.
        var prefs = new Prefs(); string old = prefs.Serialize();
        check(!prefs.RopeAtTopEdge && !old.Contains("RopeAtTopEdge"),"Top-edge rope is off and absent from default settings");
        int changes = 0; prefs.Changed += () => changes++;
        prefs.SetRopeAtTopEdge(true); prefs.SetRopeAtTopEdge(true);
        check(changes == 1 && Prefs.Parse(prefs.Serialize()).RopeAtTopEdge,"Top-edge rope choice survives restart");
        check(!Prefs.Parse(prefs.Serialize()).IsOn(Feature.Clothesline),"Rope setting does not turn Clothesline on");
        prefs.SetRopeAtTopEdge(false); check(prefs.Serialize() == old,"Turning the rope off restores the previous settings bytes");
        check(!Prefs.Parse("{\"Version\":1,\"Language\":\"en\",\"Enabled\":[\"Clothesline\"]}").RopeAtTopEdge,"Older settings load with the rope off");
        check(!Prefs.Parse(old.TrimEnd('}') + ",\"RopeAtTopEdge\":false}").RopeAtTopEdge,"Explicit false is read as off");
    }

    private static void EdgeChecks(Action<bool,string> check)
    {
        var left = new ScreenRect(0,0,1920,1080); var right = new ScreenRect(1920,-200,4480,1240);
        TimeSpan T(double ms) => TimeSpan.FromMilliseconds(ms);
        var rope = new ScreenRect(0,0,1920,300);

        var edge = new RopeEdgeTrigger();
        check(edge.Update(T(0),900,0,left,false,null,false) == RopeEdgeAction.None,"Touching the top edge waits a moment");
        check(edge.Update(T(100),900,1,left,false,null,false) == RopeEdgeAction.None,"Still waiting within the dwell (second pixel row counts)");
        check(edge.Update(T(160),900,0,left,false,null,false) == RopeEdgeAction.Show && edge.Showing && edge.Monitor == left,"Resting at the top edge shows the rope on that monitor");
        check(edge.Update(T(200),900,0,left,false,rope,false) == RopeEdgeAction.None,"No repeated show while showing");
        check(edge.Update(T(300),900,200,left,false,rope,false) == RopeEdgeAction.None,"Pointer on the rope keeps it");
        check(edge.Update(T(400),900,600,left,false,rope,false) == RopeEdgeAction.None,"Leaving starts the hide wait");
        check(edge.Update(T(1300),900,600,left,false,rope,false) == RopeEdgeAction.None,"Not hidden before one second");
        check(edge.Update(T(1450),900,600,left,false,rope,false) == RopeEdgeAction.Hide && !edge.Showing,"Hidden one second after the pointer left");

        var passing = new RopeEdgeTrigger();
        passing.Update(T(0),10,0,left,false,null,false); passing.Update(T(100),10,5,left,false,null,false);
        check(passing.Update(T(200),10,0,left,false,null,false) == RopeEdgeAction.None,"Passing through the edge restarts the dwell");
        check(passing.Update(T(300),10,2,left,false,null,false) == RopeEdgeAction.None,"The third pixel row is not the edge");

        var back = new RopeEdgeTrigger();
        back.Update(T(0),900,0,left,false,null,false); back.Update(T(200),900,0,left,false,null,false);
        back.Update(T(300),900,700,left,false,rope,false);
        check(back.Update(T(900),900,700,left,false,rope,false) == RopeEdgeAction.None && back.Update(T(1000),900,250,left,false,rope,false) == RopeEdgeAction.None,"Coming back to the rope cancels the hide");
        check(back.Update(T(1800),900,250,left,false,rope,false) == RopeEdgeAction.None && back.Showing,"Rope stays while the pointer is on it");

        var held = new RopeEdgeTrigger();
        held.Update(T(0),900,0,left,false,null,false); held.Update(T(200),900,0,left,false,null,false);
        held.Update(T(300),900,900,left,false,rope,true);
        check(held.Update(T(5000),900,900,left,false,rope,true) == RopeEdgeAction.None,"A print being dragged or opened keeps the rope down");
        held.Update(T(5100),900,900,left,false,rope,false);
        check(held.Update(T(6200),900,900,left,false,rope,false) == RopeEdgeAction.Hide,"After the print is let go the normal hide wait applies");
        check(held.Update(T(6300),900,0,left,false,null,false) == RopeEdgeAction.None && held.Update(T(6460),900,0,left,false,null,false) == RopeEdgeAction.Show,"Back at the edge after hiding, the rope returns after the usual rest");

        var full = new RopeEdgeTrigger();
        full.Update(T(0),900,0,left,true,null,false);
        check(full.Update(T(500),900,0,left,true,null,false) == RopeEdgeAction.None && !full.Showing,"Never shown over a full-screen app");
        full.Update(T(600),900,0,left,false,null,false);
        check(full.Update(T(800),900,0,left,false,null,false) == RopeEdgeAction.Show,"Shown once the full-screen app is gone");
        check(full.Update(T(900),900,100,left,true,rope,false) == RopeEdgeAction.Hide,"A full-screen app coming to the front hides the rope");
        var edgeHide = new RopeEdgeTrigger();
        edgeHide.Update(T(0),900,0,left,false,null,false); edgeHide.Update(T(200),900,0,left,false,null,false);
        check(edgeHide.Update(T(300),900,0,left,true,rope,false) == RopeEdgeAction.Hide,"Full-screen app hides the rope even with the pointer at the edge");
        check(edgeHide.Update(T(400),900,0,left,false,null,false) == RopeEdgeAction.None && edgeHide.Update(T(900),900,0,left,false,null,false) == RopeEdgeAction.None,"Hidden at the edge, staying there does not bring it straight back");
        edgeHide.Update(T(1000),900,40,left,false,null,false); edgeHide.Update(T(1100),900,0,left,false,null,false);
        check(edgeHide.Update(T(1300),900,0,left,false,null,false) == RopeEdgeAction.Show,"Leaving the edge and touching it again re-arms");
        var held2 = new RopeEdgeTrigger();
        held2.Update(T(0),900,0,left,false,null,false); held2.Update(T(200),900,0,left,false,null,false);
        check(held2.Update(T(300),900,0,left,true,rope,true) == RopeEdgeAction.None && held2.Showing,"A full-screen app does not pull the rope away from a print in use");

        var monitors = new RopeEdgeTrigger();
        monitors.Update(T(0),900,0,left,false,null,false); monitors.Update(T(200),900,0,left,false,null,false);
        check(monitors.Update(T(300),2500,-200,right,false,rope,false) == RopeEdgeAction.None,"Top edge of another monitor waits too");
        check(monitors.Update(T(460),2500,-199,right,false,rope,false) == RopeEdgeAction.Show && monitors.Monitor == right,"The rope follows to another monitor's top edge (negative coordinates)");
        var other = new RopeEdgeTrigger();
        check(other.Update(T(0),2500,-150,right,false,null,false) == RopeEdgeAction.None && other.Update(T(400),2500,-150,right,false,null,false) == RopeEdgeAction.None,"Below another monitor's top edge does not show");
        check(!RopeEdgeTrigger.AtEdge(1920,0,left) && RopeEdgeTrigger.AtEdge(1919,0,left) && !RopeEdgeTrigger.AtEdge(5,-1,left),"Edge is inside the monitor only");

        var clock = new RopeEdgeTrigger();
        clock.Update(T(10000),900,0,left,false,null,false);
        check(clock.Update(T(5),900,0,left,false,null,false) == RopeEdgeAction.None,"A clock going backwards restarts the wait");
        var reset = new RopeEdgeTrigger(); reset.Update(T(0),900,0,left,false,null,false); reset.Update(T(200),900,0,left,false,null,false);
        reset.Reset(); check(!reset.Showing && reset.Monitor is null,"Reset hides without an action");
        reset.Update(T(300),900,0,left,false,null,false); check(reset.Update(T(500),900,0,left,false,null,false) == RopeEdgeAction.Show,"After a reset the edge works again");

        check(RopeEdgeTrigger.IsFullScreen(left,left,true,false) && RopeEdgeTrigger.IsFullScreen(new ScreenRect(-8,-8,1928,1088),left,true,false),"A window covering the monitor is full screen");
        check(!RopeEdgeTrigger.IsFullScreen(new ScreenRect(0,0,1920,1040),left,true,false),"A maximized window above the taskbar is not full screen");
        check(!RopeEdgeTrigger.IsFullScreen(left,left,true,true),"The desktop and Swivel's own windows are never full screen");
        check(!RopeEdgeTrigger.IsFullScreen(left,left,false,false) && !RopeEdgeTrigger.IsFullScreen(null,left,true,false),"A window on another monitor or no window is not full screen");
        check(!RopeEdgeTrigger.IsFullScreen(left,new ScreenRect(0,0,0,0),true,false),"An empty monitor rectangle is never full screen");
    }
}
