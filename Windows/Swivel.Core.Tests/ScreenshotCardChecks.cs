using System;
using Swivel.Core;

internal static class ScreenshotCardChecks
{
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        TimeSpan T(double ms) => TimeSpan.FromMilliseconds(ms);

        // The setting: off by default, not written while off, independent of Clothesline and the rope.
        var prefs = new Prefs(); string old = prefs.Serialize();
        check(!prefs.ScreenshotCards && !old.Contains("ScreenshotCards"),"Screenshot cards are off and absent from default settings");
        int changes = 0; prefs.Changed += () => changes++;
        prefs.SetScreenshotCards(true); prefs.SetScreenshotCards(true);
        var loaded = Prefs.Parse(prefs.Serialize());
        check(changes == 1 && loaded.ScreenshotCards && !loaded.RopeAtTopEdge && !loaded.IsOn(Feature.Clothesline),"Screenshot cards choice survives restart without turning on Clothesline or the rope");
        prefs.SetScreenshotCards(false); check(prefs.Serialize() == old,"Turning screenshot cards off restores the previous settings bytes");
        check(!Prefs.Parse("{\"Version\":1,\"Language\":\"en\",\"Enabled\":[\"Clothesline\"]}").ScreenshotCards,"Older settings load with screenshot cards off");
        check(!Prefs.Parse(old.TrimEnd('}') + ",\"ScreenshotCards\":false}").ScreenshotCards,"Explicit false is read as off");
        prefs.SetRopeAtTopEdge(true); prefs.SetScreenshotCards(true);
        var both = Prefs.Parse(prefs.Serialize()); check(both.RopeAtTopEdge && both.ScreenshotCards,"Rope and screenshot cards are saved independently");

        // When the watcher runs.
        check(ScreenshotCards.ShouldRun(true,true,false),"Watcher runs with Clothesline and the setting on");
        check(!ScreenshotCards.ShouldRun(false,true,false) && !ScreenshotCards.ShouldRun(true,false,false) && !ScreenshotCards.ShouldRun(true,true,true),"Feature off, setting off or Quit stops the watcher");
        check(ScreenshotCards.FolderId == new Guid("b7bede81-df94-4682-a7d8-57a52620b86f"),"Screenshots known-folder ID is FOLDERID_Screenshots");
        check(!ScreenshotCards.Reportable(ArrivalStopReason.User) && !ScreenshotCards.Reportable(ArrivalStopReason.Closed) && !ScreenshotCards.Reportable(ArrivalStopReason.FeatureDisabled) && !ScreenshotCards.Reportable(ArrivalStopReason.None),
            "Stopping by the person, Quit or the feature switch is not reported as a problem");
        check(ScreenshotCards.Reportable(ArrivalStopReason.ByteLimit) && ScreenshotCards.Reportable(ArrivalStopReason.WatcherError) && ScreenshotCards.Reportable(ArrivalStopReason.CardLimit),"Limits and watcher failures are reported");

        check(ScreenshotCards.Renewable(ArrivalStopReason.ByteLimit) && ScreenshotCards.Renewable(ArrivalStopReason.NotificationLimit) && ScreenshotCards.Renewable(ArrivalStopReason.CardLimit) && ScreenshotCards.Renewable(ArrivalStopReason.QueueLimit),
            "Session budgets reached in long use allow a fresh session");
        check(ScreenshotCards.Renewable(ArrivalStopReason.WatcherError) && !ScreenshotCards.Renewable(ArrivalStopReason.ReadFailure) && !ScreenshotCards.Renewable(ArrivalStopReason.CleanupError) && !ScreenshotCards.Renewable(ArrivalStopReason.User),
            "A lost watcher may be renewed; read and cleanup failures are never restarted automatically");

        // Dismiss timing.
        var clock = new CardDismissClock();
        check(!clock.Due(T(0)) && clock.Deadline is null,"A clock that never showed a card is never due");
        clock.Show(T(0));
        check(!clock.Due(T(2499)) && clock.Due(T(2500)) && clock.Deadline == T(2500),"An idle card leaves after 2.5 s");
        clock.Show(T(0)); clock.SetHover(true,T(500));
        check(clock.Held && clock.Deadline is null && !clock.Due(T(60_000)),"Hovering pauses dismissal indefinitely");
        clock.SetHover(false,T(60_000));
        check(!clock.Due(T(60_999)) && clock.Due(T(61_000)),"Leaving grants one more second");
        clock.Show(T(0)); clock.SetHover(true,T(100)); clock.SetHover(false,T(200));
        check(!clock.Due(T(2499)) && clock.Due(T(2500)),"A brief hover never shortens the idle time");
        clock.Show(T(0)); clock.SetHover(true,T(2000)); clock.SetHover(false,T(2100));
        check(!clock.Due(T(3099)) && clock.Due(T(3100)),"Leaving near the end still grants at least one second");
        clock.Show(T(0)); clock.SetHover(true,T(100)); clock.SetPressed(true,T(200)); clock.SetHover(false,T(300));
        check(clock.Held && !clock.Due(T(10_000)),"A press keeps the card while the pointer leaves it during a drag");
        clock.SetPressed(false,T(10_000));
        check(!clock.Due(T(10_999)) && clock.Due(T(11_000)),"A cancelled drag grants one more second");
        clock.SetPressed(true,T(20_000)); clock.Show(T(20_000));
        check(!clock.Held && clock.Due(T(22_500)),"A new card resets hold state and the idle time");

        // Gestures.
        check(CardGestures.Classify(2,-3,4) == CardGesture.Pending,"Small movement stays a click");
        check(CardGestures.Classify(1,-10,4) == CardGesture.Lift && CardGestures.Classify(-5,-8,4) == CardGesture.Lift,"Mostly upward movement lifts toward the rope");
        check(CardGestures.Classify(10,-10,4) == CardGesture.HandOut && CardGestures.Classify(-10,0,4) == CardGesture.HandOut && CardGestures.Classify(0,10,4) == CardGesture.HandOut,"Sideways, diagonal or downward movement drags the file out");
        check(CardGestures.Classify(double.NaN,-10,4) == CardGesture.Pending && CardGestures.Classify(0,-10,0) == CardGesture.Pending,"Invalid input never starts a gesture");
        check(CardGestures.Hangs(-64) && !CardGestures.Hangs(-63) && !CardGestures.Hangs(20) && !CardGestures.Hangs(double.NaN),"A lift hangs only after travelling far enough up");

        // Corner placement.
        check(ScreenshotCards.Corner(0,0,1920,1040,260,200,16) == (1644,824),"Card rests in the bottom-right corner of the work area");
        check(ScreenshotCards.Corner(-1280,-300,0,724,260,200,16) == (-276,508),"Placement works on a monitor left of and above the primary");
        check(ScreenshotCards.Corner(0,0,100,100,260,200,16) == (0,0),"A work area smaller than the card keeps the card on screen at its top-left");
        reject(() => ScreenshotCards.Corner(0,0,100,100,0,10,0),"Placement refuses an empty card");

        // The rope shown from a hung card lingers, then follows the normal leave rule.
        var trigger = new RopeEdgeTrigger(); var monitor = new ScreenRect(0,0,1920,1080); var area = new ScreenRect(0,0,1920,300);
        trigger.Present(T(1000),monitor,TimeSpan.FromSeconds(2.5));
        check(trigger.Showing && trigger.Monitor == monitor,"Presenting shows the rope on the card's monitor");
        check(trigger.Update(T(3000),1800,1000,monitor,false,area,false) == RopeEdgeAction.None,"Presented rope stays while the pointer is still at the card");
        check(trigger.Update(T(3500),1800,1000,monitor,false,area,false) == RopeEdgeAction.Hide,"Presented rope lifts once the linger has passed");
        trigger.Present(T(5000),monitor,TimeSpan.FromSeconds(2.5));
        check(trigger.Update(T(7000),900,100,monitor,false,area,false) == RopeEdgeAction.None && trigger.Update(T(9000),900,100,monitor,false,area,false) == RopeEdgeAction.None,"Pointer on the presented rope keeps it down");
        check(trigger.Update(T(9500),900,900,monitor,false,area,false) == RopeEdgeAction.None && trigger.Update(T(10_500),900,900,monitor,false,area,false) == RopeEdgeAction.Hide,"After visiting it, the ordinary one-second leave delay applies");
        trigger.Present(T(0),monitor,TimeSpan.Zero);
        check(trigger.Update(T(999),1800,1000,monitor,false,area,false) == RopeEdgeAction.None && trigger.Update(T(1000),1800,1000,monitor,false,area,false) == RopeEdgeAction.Hide,"A linger shorter than the leave delay uses the leave delay");
    }
}
