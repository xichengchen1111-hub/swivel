using System;
using Swivel.Core;

internal static class ClotheslineGestureChecks
{
    private static TimeSpan At(double milliseconds)=>TimeSpan.FromMilliseconds(milliseconds);
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        foreach(int delay in new[]{1,100,250,500,900,2000,10000})
        {
            var state=new ClotheslineGesture(At(delay));
            check(state.Begin(At(0),false)==ClotheslineGestureAction.None&&state.Pressed&&state.Waiting,"Pointer down only arms hold; no file action");
            check(state.CompleteClick(At(30))==ClotheslineGestureAction.None&&!state.Pressed&&state.Waiting,"Short mouse click waits for system double-click qualification");
            state.EndPress();
            check(state.Tick(At(30+delay-0.001))==ClotheslineGestureAction.None,"Single click never copies before its full system interval");
            check(state.Tick(At(30+delay))==ClotheslineGestureAction.Copy&&!state.Waiting,"Single click copies once at the deadline");
            check(state.Tick(At(30000))==ClotheslineGestureAction.None,"Completed click never repeats");

            state.Begin(At(0),false);state.CompleteClick(At(30));
            check(state.Begin(At(31),true)==ClotheslineGestureAction.Preview&&!state.Pressed&&!state.Waiting,"Qualified double-click consumes the waiting single click and previews once");
            check(state.CompleteClick(At(60))==ClotheslineGestureAction.None&&state.Tick(At(30000))==ClotheslineGestureAction.None,"Double-click release and late timers cannot copy");

            state.Begin(At(0),false);
            check(state.Tick(At(599.999))==ClotheslineGestureAction.None,"Hold threshold is not early");
            check(state.Tick(At(600))==ClotheslineGestureAction.Preview&&!state.Pressed&&!state.Waiting,"600ms hold previews exactly once");
            check(state.CompleteClick(At(700))==ClotheslineGestureAction.None&&state.Tick(At(30000))==ClotheslineGestureAction.None,"Hold release never causes an extra copy or preview");

            state.Begin(At(50),false);
            check(state.CompleteClick(At(650))==ClotheslineGestureAction.Preview&&!state.Waiting,"Due hold still previews when input release precedes a delayed timer callback");
            check(state.Tick(At(30000))==ClotheslineGestureAction.None,"Delayed hold completion has no pending copy");

            state.Begin(At(0),false);state.Leave();
            check(state.Pressed&&!state.Waiting&&state.Tick(At(1000))==ClotheslineGestureAction.None,"Leaving cancels timers but retains the held press for drag-out");
            state.Cancel();
            check(state.CompleteClick(At(1100))==ClotheslineGestureAction.None&&state.Tick(At(30000))==ClotheslineGestureAction.None,"Drag cancellation prevents native release from copying");

            state.Begin(At(0),false);state.Leave();
            check(state.CompleteClick(At(1000))==ClotheslineGestureAction.None&&state.Tick(At(1000+delay))==ClotheslineGestureAction.Copy,"Leaving and returning can complete a native click without restarting long press");
            state.Begin(At(0),false);state.EndPress();
            check(!state.Pressed&&!state.Waiting&&state.Tick(At(30000))==ClotheslineGestureAction.None,"Mouse release without Button click has no action");
        }

        foreach(int cancelAt in new[]{0,1,599,600,601,10000})
        {
            var hold=new ClotheslineGesture(At(500));hold.Begin(At(0),false);hold.Cancel();
            check(!hold.Pressed&&!hold.Waiting&&hold.CompleteClick(At(cancelAt))==ClotheslineGestureAction.None&&hold.Tick(At(30000))==ClotheslineGestureAction.None,"Focus/capture/right-click/close/refresh cancellation suppresses all later hold events");
            var click=new ClotheslineGesture(At(500));click.Begin(At(0),false);click.CompleteClick(At(30));click.Cancel();
            check(!click.Waiting&&click.Tick(At(cancelAt))==ClotheslineGestureAction.None&&click.Tick(At(30000))==ClotheslineGestureAction.None,"Cancellation suppresses queued copy even if its deadline has passed");
        }
        var leaveClick=new ClotheslineGesture(At(500));leaveClick.Begin(At(0),false);leaveClick.CompleteClick(At(20));leaveClick.Leave();
        check(leaveClick.Waiting&&leaveClick.Tick(At(1000))==ClotheslineGestureAction.Copy,"Leaving after a completed short click preserves its one pending copy");
        var staleDouble=new ClotheslineGesture(At(500));
        check(staleDouble.Begin(At(0),true)==ClotheslineGestureAction.None&&staleDouble.Pressed,"A native double-click count without a preceding completed click starts a fresh press");
        staleDouble.Cancel();staleDouble.Begin(At(0),false);staleDouble.CompleteClick(At(20));staleDouble.Begin(At(30),false);staleDouble.CompleteClick(At(50));
        check(staleDouble.Tick(At(520))==ClotheslineGestureAction.None&&staleDouble.Tick(At(550))==ClotheslineGestureAction.Copy,"A new unqualified click on the same print replaces a pending identical copy");
        reject(()=>new ClotheslineGesture(TimeSpan.Zero),"Invalid zero double-click interval refused");
        reject(()=>new ClotheslineGesture(At(-1)),"Invalid negative double-click interval refused");
        reject(()=>new ClotheslineGesture(At(10001)),"Unbounded double-click interval refused");
    }
}
