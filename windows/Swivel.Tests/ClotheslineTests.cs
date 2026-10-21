using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using Swivel;

namespace Swivel.Tests;

/// The clothesline's motion: gentle and well damped (the owner's rule), interruptible, and laid out so a drop lands
/// where the print will hang; plus the card's timing, the top-edge trigger, the Screenshots watcher and the saved
/// prints. A port of the Mac app's ClotheslineFeelTests. Set SWIVEL_ROPE_FRAMES=<folder> to also render
/// filmstrips of the rope and the card.
[TestClass]
public sealed class ClotheslineTests
{
    [TestInitialize]
    public void Reset() => TestSetup.ResetPrefs();

    /// WPF objects need a single-threaded apartment.
    static void Sta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    // MARK: Physics

    /// One oscillator from (x0, v0) toward 0, stepped exactly as the rope steps it.
    static List<double> Simulate(RopeTuning.Oscillator o, double x0, double v0, double seconds = 3)
    {
        double x = x0, v = v0;
        var result = new List<double>();
        for (int i = 0; i < (int)(seconds / RopeTuning.Step); i++)
        {
            RopeTuning.Advance(ref x, ref v, 0, o);
            result.Add(x);
        }
        return result;
    }

    /// Turning points bigger than `visible`, and the time after which the motion stays below it.
    static (int Count, double Settled) Swings(List<double> xs, double visible)
    {
        int count = 0;
        for (int i = 1; i < xs.Count - 1; i++)
            if (Math.Abs(xs[i]) > visible && (xs[i] - xs[i - 1]) * (xs[i + 1] - xs[i]) <= 0) count++;
        int last = xs.FindLastIndex(x => Math.Abs(x) > visible);
        return (count, (last + 1) * RopeTuning.Step);
    }

    [TestMethod]
    public void EverythingThatSwingsIsWellDamped()
    {
        foreach (var (name, o) in new[] { ("sag", RopeTuning.Sag), ("swing", RopeTuning.Swing), ("arrive", RopeTuning.Arrive) })
            Assert.IsGreaterThanOrEqualTo(0.6, o.DampingRatio, $"{name} ζ = {o.DampingRatio}");
        Assert.IsGreaterThanOrEqualTo(0.7, RopeTuning.Fall.DampingRatio);
        foreach (var (name, d) in new[] { ("lift", RopeTuning.Lift.Damping), ("press out", RopeTuning.PressOut.Damping), ("slide", RopeTuning.Slide.Damping) })
            Assert.IsGreaterThanOrEqualTo(0.6, d, $"{name} damping {d}");
    }

    [TestMethod]
    public void StrongestSwingIsSmallAndSettlesWithinTwoOscillations()
    {
        double kick = Math.Max(RopeTuning.ArrivalSwingFromAbove.Hi, RopeTuning.DropKick.Hi);
        var angles = Simulate(RopeTuning.Swing, 0, kick);
        var result = Swings(angles, 0.5 * Math.PI / 180);
        Assert.IsLessThan(4 * Math.PI / 180, angles.Max(Math.Abs), "peak swing too large");
        Assert.IsLessThanOrEqualTo(2, result.Count, $"{result.Count} visible swings");
        Assert.IsLessThanOrEqualTo(1.5, result.Settled, $"settles after {result.Settled} s");
    }

    [TestMethod]
    public void NewPrintFallsOntoTheRopeAndSettles()
    {
        var drop = Simulate(RopeTuning.Arrive, RopeTuning.ArrivalHeight, 0);
        var result = Swings(drop, 0.5);
        Assert.IsLessThanOrEqualTo(2, result.Count);
        Assert.IsLessThan(6, drop.Max(), $"bounces {drop.Max()} px below the rope");
        Assert.IsLessThanOrEqualTo(1.5, result.Settled);
    }

    [TestMethod]
    public void RopeLowersWithASingleGentleDipInTheCord()
    {
        double offset = -RopeView.PanelHeight, offsetV = 0, sag = 0, sagV = 0;
        var offsets = new List<double>();
        var sags = new List<double>();
        for (int i = 0; i < (int)(3 / RopeTuning.Step); i++)
        {
            double a = RopeTuning.Advance(ref offset, ref offsetV, 0, RopeTuning.Fall);
            RopeTuning.Advance(ref sag, ref sagV, 0, RopeTuning.Sag, force: -RopeTuning.SagCoupling * a);
            offsets.Add(offset);
            sags.Add(sag);
        }
        Assert.IsLessThan(6, offsets.Max(), $"rope overshoots {offsets.Max()} px");
        Assert.IsLessThanOrEqualTo(1.0, Swings(offsets, 0.5).Settled);
        var cord = Swings(sags, 0.5);
        Assert.IsLessThan(4, sags.Max(Math.Abs), $"cord dips {sags.Max(Math.Abs)} px");
        Assert.IsTrue(cord.Count <= 2 && cord.Settled <= 1.5, $"cord: {cord}");
    }

    // MARK: Pure helpers

    [TestMethod]
    public void SlotsOpenRoomAtTheFrontForADrop()
    {
        var plain = RopeTuning.Slots([100, 120, 80], 520);
        CollectionAssert.AreEqual(new double[] { 126, 258, 380 }, plain.Select(s => s.X).ToArray());
        CollectionAssert.AreEqual(new[] { true, true, true }, plain.Select(s => s.Fits).ToArray());
        var gapped = RopeTuning.Slots([100, 120, 80], 520, gap: 122);
        CollectionAssert.AreEqual(new double[] { 248, 380, 502 }, gapped.Select(s => s.X).ToArray());
        CollectionAssert.AreEqual(new[] { true, true, false }, gapped.Select(s => s.Fits).ToArray());   // pushed past the right edge
    }

    [TestMethod]
    public void PrintSizeFitsThePictureInsideTheMaximum()
    {
        Assert.AreEqual(new Size(178, 122), RopeTuning.PrintSize(1.5));
        Assert.AreEqual(new Size(178, 66), RopeTuning.PrintSize(3));
        Assert.AreEqual(new Size(66, 122), RopeTuning.PrintSize(0.5));
    }

    [TestMethod]
    public void RopePathIsExactlyTheSagParabola()
    {
        double width = 1440, y = 18, sag = 16;
        var (start, control, end) = RopeTuning.RopeCurve(width, y, sag);
        for (double t = 0; t <= 1; t += 0.125)
        {
            double x = (1 - t) * (1 - t) * start.X + 2 * t * (1 - t) * control.X + t * t * end.X;
            double by = (1 - t) * (1 - t) * start.Y + 2 * t * (1 - t) * control.Y + t * t * end.Y;
            double u = x / width;
            Assert.IsLessThan(1e-6, Math.Abs(by - (y + sag * 4 * u * (1 - u))), $"off the parabola at t = {t}");
        }
        Sta(() => Assert.IsFalse(RopeTuning.RopePath(width, y, sag).IsEmpty()));
    }

    [TestMethod]
    public void TimingCurvesAreMonotonicAndHitTheirEnds()
    {
        foreach (var (x1, y1, x2, y2) in new[] { (0.3, 0.0, 0.2, 1.0), (0.4, 0.0, 1.0, 1.0), (0.42, 0.0, 1.0, 1.0), (0.0, 0.0, 0.58, 1.0) })
        {
            double last = 0;
            for (int i = 0; i <= 100; i++)
            {
                double v = RopeTuning.Ease(i / 100.0, x1, y1, x2, y2);
                Assert.IsGreaterThanOrEqualTo(last - 1e-6, v);
                last = v;
            }
            Assert.IsLessThan(1e-4, Math.Abs(RopeTuning.Ease(0, x1, y1, x2, y2)));
            Assert.IsLessThan(1e-4, Math.Abs(RopeTuning.Ease(1, x1, y1, x2, y2) - 1));
        }
        Assert.IsLessThan(0.5, RopeTuning.Ease(0.5, 0.4, 0, 1, 1));      // an exit accelerates away
        Assert.IsGreaterThan(0.5, RopeTuning.Ease(0.5, 0.3, 0, 0.2, 1));  // the flight arrives gently
    }

    [TestMethod]
    public void HoldingGathersFromAFifthOfASecond()
    {
        Assert.AreEqual(0, RopeTuning.Charge(0.1));
        Assert.IsLessThan(1e-6, Math.Abs(RopeTuning.Charge(0.375) - 0.5));
        Assert.AreEqual(1, RopeTuning.Charge(0.9));
    }

    [TestMethod]
    public void RopeWaitsLongerWhileThePointerReachesAlongIt()
    {
        Assert.IsNull(RopeTuning.HideDelay(overPrint: true, inTopBand: false, nearPrints: true, inStrip: true));
        Assert.IsNull(RopeTuning.HideDelay(overPrint: false, inTopBand: true, nearPrints: false, inStrip: false));
        Assert.AreEqual(1.5, RopeTuning.HideDelay(overPrint: false, inTopBand: false, nearPrints: true, inStrip: true));
        Assert.AreEqual(1.0, RopeTuning.HideDelay(overPrint: false, inTopBand: false, nearPrints: false, inStrip: true));
        Assert.AreEqual(0.5, RopeTuning.HideDelay(overPrint: false, inTopBand: false, nearPrints: false, inStrip: false));
    }

    // MARK: The rope view

    static readonly Lazy<List<string>> fixtures = new(() =>
    {
        List<string>? result = null;
        Sta(() => result = ClotheslineFrames.Fixtures(Path.Combine(Path.GetTempPath(), "swivel-rope-tests")));
        return result!;
    });

    static List<string> Fixtures => fixtures.Value;

    static RopeView Rope(IReadOnlyList<string>? paths = null, bool reduced = false)
    {
        var view = new RopeView(900) { ReducedMotionOverride = reduced };
        view.SetItems(paths ?? Fixtures.Take(3).ToList());
        return view;
    }

    static void Run(RopeView view, double seconds, double fps = 120)
    {
        for (int i = 0; i < (int)(seconds * fps); i++) view.Advance(1 / fps);
    }

    static Point Centre(RopeView view, int i) => view.PrintPoint(i, new Point(view.PrintSizeAt(i).Width / 2, view.PrintSizeAt(i).Height / 2));

    [TestMethod]
    public void HiddenRopeStaysHiddenUntilLowered() => Sta(() =>
    {
        var view = Rope();
        Run(view, 1);
        Assert.IsLessThan(0, view.RopeY(450), "the hidden simulation lowered the rope");
        view.Lower(fromHidden: true);
        Run(view, 1.2);
        Assert.IsLessThan(0.5, Math.Abs(view.RopeY(0) - 18));
    });

    [TestMethod]
    public void LoweringWhileLiftingTurnsAroundWithoutAJump() => Sta(() =>
    {
        var view = Rope();
        view.Lower(fromHidden: true);
        Run(view, 1.2);
        bool lifted = false;
        view.Lift(() => lifted = true);
        Run(view, 0.08);
        double earlier = view.RopeY(450);
        view.Advance(1.0 / 120);
        double before = view.RopeY(450);
        Assert.IsTrue(before < 18 && before > -RopeView.PanelHeight);
        view.Lower(fromHidden: false);
        Assert.AreEqual(before, view.RopeY(450));   // no teleport back to the top
        view.Advance(1.0 / 120);
        // It carries on at the speed it had (only the pull changes direction), then turns around.
        Assert.IsLessThan(6, Math.Abs((view.RopeY(450) - before) - (before - earlier)));
        Run(view, 1.5);
        Assert.IsTrue(Math.Abs(view.RopeY(0) - 18) < 0.5 && !lifted);
    });

    [TestMethod]
    public void LiftFinishesAndHandsBack() => Sta(() =>
    {
        var view = Rope();
        view.Lower(fromHidden: true);
        Run(view, 1);
        bool lifted = false;
        view.Lift(() => lifted = true);
        Run(view, 1);
        Assert.IsTrue(lifted && !view.IsLifting);
    });

    [TestMethod]
    public void HoverLiftsAPrintAndAPressSinksIt() => Sta(() =>
    {
        var view = Rope();
        view.Lower(fromHidden: true);
        Run(view, 1.5);
        double restY = view.PrintPosition(0).Y;
        view.SimulatedPointer = Centre(view, 0);
        Run(view, 0.8);
        double hoverY = view.PrintPosition(0).Y, hoverScale = view.PrintScale(0);
        Assert.IsLessThan(0.3, Math.Abs(hoverY - (restY - RopeTuning.HoverLift)));
        Assert.IsLessThan(0.003, Math.Abs(hoverScale - (1 + RopeTuning.HoverScale)));
        Assert.IsGreaterThan(0.99, view.RingOpacity(0));   // the ring is fully in
        view.PreviewPress(Fixtures[0], true);
        Run(view, 0.3);
        double pressScale = view.PrintScale(0);
        Assert.IsLessThan(0.3, Math.Abs(view.PrintPosition(0).Y - (hoverY + RopeTuning.PressSink)));
        Assert.IsTrue(pressScale < hoverScale && pressScale / hoverScale >= 0.95);
        view.PreviewPress(Fixtures[0], false);
        Run(view, 0.8);
        Assert.IsLessThan(0.3, Math.Abs(view.PrintPosition(0).Y - hoverY));
        view.SimulatedPointer = null;
        Run(view, 0.8);
        Assert.IsTrue(Math.Abs(view.PrintPosition(0).Y - restY) < 0.3 && view.RingOpacity(0) < 0.01);
    });

    [TestMethod]
    public void ReduceMotionKeepsTheHighlightButNotTheMovement() => Sta(() =>
    {
        var view = Rope(reduced: true);
        view.Lower(fromHidden: true);
        Assert.IsLessThan(0.001, Math.Abs(view.RopeY(0) - 18));   // no fall: it fades in place
        Run(view, 0.5);
        double restY = view.PrintPosition(0).Y;
        var restTransform = view.PrintTransform(0);
        view.SimulatedPointer = Centre(view, 0);
        Run(view, 0.5);
        Assert.AreEqual(restY, view.PrintPosition(0).Y);
        Assert.IsGreaterThan(0.99, view.RingOpacity(0));
        // It keeps its tilt instead of swinging straight, and the × appears at full size instead of growing in.
        Assert.AreEqual(restTransform, view.PrintTransform(0));
        Assert.AreEqual(1, view.CloseScaleAt(0));
        Assert.IsGreaterThan(0.99, view.CloseOpacity(0));
    });

    [TestMethod]
    public void TheWholeCloseButtonKeepsItsPrintHovered() => Sta(() =>
    {
        var view = Rope(reduced: true);
        view.Lower(fromHidden: true);
        Run(view, 0.5);
        double w = view.PrintSizeAt(0).Width;
        // The × reaches 9 px past the print's corner; aim near its right edge, beyond the print itself.
        var edge = view.PrintPoint(0, new Point(w + 7, 2));
        view.SimulatedPointer = edge;
        Run(view, 0.3);
        Assert.IsLessThan(0.01, view.CloseOpacity(0), "a hidden × mustn't widen the print");
        view.SimulatedPointer = Centre(view, 0);
        Run(view, 0.3);
        Assert.IsGreaterThan(0.99, view.CloseOpacity(0));
        view.SimulatedPointer = edge;
        Run(view, 0.3);
        Assert.IsGreaterThan(0.99, view.CloseOpacity(0), "the × faded under the pointer");
    });

    [TestMethod]
    public void FrameLoopRestsOnceNothingMoves() => Sta(() =>
    {
        // Reduced motion: nothing to animate once the rope has faded in.
        var still = Rope(reduced: true);
        still.Lower(fromHidden: true);
        Run(still, 1);
        Assert.IsFalse(still.NeedsFrames);

        // Otherwise the breeze blows for a few seconds after the rope comes down, then dies down and it rests.
        var view = Rope();
        view.Lower(fromHidden: true);
        Run(view, 1.5);
        Assert.IsTrue(view.NeedsFrames, "the breeze stopped too soon");
        Run(view, 8);
        Assert.IsFalse(view.NeedsFrames);
        // Anything that happens starts it again, until that has settled too.
        view.PreviewPress(Fixtures[0], true);
        Assert.IsTrue(view.NeedsFrames);
        Run(view, 0.3);
        view.PreviewPress(Fixtures[0], false);
        Run(view, 8);
        Assert.IsFalse(view.NeedsFrames);
    });

    [TestMethod]
    public void PointerWakesARestingRopeOnlyWhenTheHoverChanges() => Sta(() =>
    {
        var view = Rope(reduced: true);
        view.Lower(fromHidden: true);
        Run(view, 1);
        Assert.IsFalse(view.PointerChangesHover);
        view.SimulatedPointer = new Point(8, RopeView.PanelHeight - 8);   // empty strip below the rope
        Assert.IsFalse(view.PointerChangesHover);
        var centre = Centre(view, 0);
        view.SimulatedPointer = centre;
        Assert.IsTrue(view.PointerChangesHover);
        Run(view, 0.5);
        Assert.IsTrue(!view.PointerChangesHover && !view.NeedsFrames);    // hovered and settled: resting again
        view.SimulatedPointer = new Point(centre.X + 10, centre.Y - view.PrintSizeAt(0).Height / 6);
        Assert.IsFalse(view.PointerChangesHover);                          // moving about on the same print
        view.SimulatedPointer = new Point(8, RopeView.PanelHeight - 8);
        Assert.IsTrue(view.PointerChangesHover);                           // leaving it must wake the loop
    });

    [TestMethod]
    public void RemovedPrintLeavesAtOnceAndItsNeighboursCloseUp() => Sta(() =>
    {
        var paths = Fixtures.Take(3).ToList();
        var view = Rope(paths);
        view.Lower(fromHidden: true);
        Run(view, 1);
        view.Remove(paths[0], falling: true);
        view.SetItems(paths);   // a reload before the Recycle Bin has it changes nothing
        Run(view, 1.2);
        Assert.AreEqual(2, view.PrintCount);
        Assert.IsLessThan(0.5, Math.Abs(view.PrintPosition(0).X - (RopeTuning.FirstSlot + view.PrintSizeAt(0).Width / 2)));
        view.Departed(paths[0]);
        view.SetItems(paths);   // recycling failed: it comes back
        Assert.AreEqual(3, view.PrintCount);
    });

    [TestMethod]
    public void ADropOpensAGapAndTheNewPrintFillsIt() => Sta(() =>
    {
        var view = Rope(Fixtures.Take(2).ToList(), reduced: true);
        view.Lower(fromHidden: true);
        Run(view, 0.5);
        double x0 = view.PrintPosition(0).X;
        view.MarkNew(Fixtures[3], RopeArrival.InPlace);
        view.SetItems([Fixtures[3], .. Fixtures.Take(2)]);
        Run(view, 1);
        Assert.AreEqual(Fixtures[3], view.PathAt(0));
        Assert.IsLessThan(0.5, Math.Abs(view.PrintPosition(0).X - (RopeTuning.FirstSlot + view.PrintSizeAt(0).Width / 2)));
        Assert.IsGreaterThan(x0, view.PrintPosition(1).X, "the old first print moved along");
    });

    // MARK: Incoming hand-off

    [TestMethod]
    public void EveryNewScreenshotGetsTheCardOrStaysPut()
    {
        var folder = @"C:\Screens";
        string second = Path.Combine(folder, "Screenshot (2).png"), first = Path.Combine(folder, "Screenshot (1).png"), card = Path.Combine(folder, "Earlier.png");
        // Two monitors: one capture writes two files that arrive in the same look. The card takes the newest; the
        // other must not be forgotten unseen.
        var both = Clothesline.Triage([second, first, card], new HashSet<string> { "Earlier.png" }, showCard: true);
        Assert.AreEqual(second, both.Card);
        CollectionAssert.AreEqual(new[] { first }, both.PutAway);
        Assert.IsTrue(both.Seen.SetEquals(["Screenshot (2).png", "Screenshot (1).png", "Earlier.png"]));
        // Nothing new: nothing happens.
        var none = Clothesline.Triage([card], new HashSet<string> { "Earlier.png" }, showCard: true);
        Assert.IsTrue(none.Card is null && none.PutAway.Count == 0);
        // With the clothesline off, no card.
        var off = Clothesline.Triage([second, first], new HashSet<string>(), showCard: false);
        Assert.IsNull(off.Card);
        CollectionAssert.AreEqual(new[] { second, first }, off.PutAway);
    }

    [TestMethod]
    public void CardWaitsTwoAndAHalfSecondsAndPausesWhileTouched()
    {
        var timer = new CardTimer();
        timer.Present(0);
        Assert.IsFalse(timer.Due(2.4));
        Assert.IsTrue(timer.Due(2.5));
        Assert.IsFalse(timer.Due(3), "only once per deadline");

        // Hovering pauses it for as long as it lasts; leaving grants one more second.
        timer.Present(10);
        timer.SetHovered(true, 11);
        Assert.IsFalse(timer.Due(20));
        timer.SetHovered(false, 20);
        Assert.IsFalse(timer.Due(20.9));
        Assert.IsTrue(timer.Due(21));

        // A press pauses it; the click that follows hangs or opens the card, so the release doesn't restart it.
        timer.Present(30);
        timer.SetInteracting(true, 30.5);
        timer.SetInteracting(false, 30.6, resume: false);
        Assert.IsFalse(timer.Due(40));

        // A drag pauses it; a drag that leaves the card behind (copied into an app) grants one more second.
        timer.Present(50);
        timer.BeginDrag();
        Assert.IsFalse(timer.Due(60));
        timer.EndDrag(60, hovered: false);
        Assert.IsTrue(timer.Due(61));
        timer.Present(70);
        timer.BeginDrag();
        timer.EndDrag(71, hovered: true);   // ended over the card: it waits while hovered
        Assert.IsFalse(timer.Due(80));

        // A newer card starts a new wait; the old deadline is gone.
        timer.Present(90);
        int generation = timer.Generation;
        timer.Present(91);
        Assert.AreEqual(generation + 1, timer.Generation);
        Assert.IsFalse(timer.Due(92.6));
        Assert.IsTrue(timer.Due(93.5));

        // Once the card has gone (hung, opened, put away), nothing is due.
        timer.Present(100);
        timer.Dismissed();
        Assert.IsFalse(timer.Due(200));
        timer.SetHovered(false, 200);
        Assert.IsFalse(timer.Due(300));
    }

    [TestMethod]
    public void TopEdgeRevealsOnceAndRearmsAfterLeaving()
    {
        var edge = new TopEdgeTrigger();
        Assert.IsFalse(edge.Update(atTopEdge: false, buttonDown: false, canShow: true));
        Assert.IsTrue(edge.Update(atTopEdge: true, buttonDown: false, canShow: true), "touching the edge lowers at once");
        // Staying at the edge (after hiding it with a click or the shortcut) doesn't reopen it in place.
        Assert.IsFalse(edge.Update(atTopEdge: true, buttonDown: false, canShow: true));
        Assert.IsFalse(edge.Update(atTopEdge: true, buttonDown: false, canShow: true));
        // Moving away rearms it.
        Assert.IsFalse(edge.Update(atTopEdge: false, buttonDown: false, canShow: true));
        Assert.IsTrue(edge.Update(atTopEdge: true, buttonDown: false, canShow: true));
        // Arriving with the button held (dragging a window to the top to maximise it) doesn't lower it…
        edge.Update(atTopEdge: false, buttonDown: false, canShow: true);
        Assert.IsFalse(edge.Update(atTopEdge: true, buttonDown: true, canShow: true));
        // …and nor does it once the button comes up while still at the edge.
        Assert.IsFalse(edge.Update(atTopEdge: true, buttonDown: false, canShow: true));
        // Already down: nothing to do.
        edge.Update(atTopEdge: false, buttonDown: false, canShow: false);
        Assert.IsFalse(edge.Update(atTopEdge: true, buttonDown: false, canShow: false));
        edge.Reset();
        Assert.IsTrue(edge.Update(atTopEdge: true, buttonDown: false, canShow: true), "a reset rearms it");
    }

    [TestMethod]
    public void ScreenshotWatcherPicksUpANewPngOnceComplete()
    {
        var folder = TestSetup.NewFolder("Screenshots");
        File.WriteAllBytes(Path.Combine(folder, "Old.png"), [1, 2, 3]);
        using var watcher = new ScreenshotWatcher(folder);
        var arrived = new List<string>();
        using var signal = new ManualResetEventSlim();
        watcher.Arrived += files => { lock (arrived) arrived.AddRange(files); signal.Set(); };
        watcher.Start();
        Assert.IsTrue(watcher.IsWatching);

        var shot = Path.Combine(folder, "Screenshot 2026-10-21 101500.png");
        using (var stream = new FileStream(shot, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            // Written in two steps, with the writer holding the file in between: not complete yet.
            stream.Write(new byte[2048]);
            stream.Flush();
            Assert.IsFalse(signal.Wait(1200), "reported while still being written");
            stream.Write(new byte[2048]);
        }
        Assert.IsTrue(signal.Wait(5000), "never reported");
        Thread.Sleep(600);
        lock (arrived) CollectionAssert.AreEqual(new[] { shot }, arrived, "reported once, and only the new file");

        // Not pictures, and files Swivel puts there itself, are not screenshots.
        signal.Reset();
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "x");
        var ours = Path.Combine(folder, "Copied 2026-10-21 at 10.15.00.png");
        watcher.Ignore(ours);
        File.WriteAllBytes(ours, new byte[100]);
        Assert.IsFalse(signal.Wait(1500));
        watcher.Stop();
        Assert.IsFalse(watcher.IsWatching);
    }

    [TestMethod]
    public void ScreenshotsFolderCanBeOverriddenForTests()
    {
        var old = Environment.GetEnvironmentVariable("SWIVEL_SCREENSHOTS");
        try
        {
            Environment.SetEnvironmentVariable("SWIVEL_SCREENSHOTS", @"C:\Temp\Shots");
            Assert.AreEqual(@"C:\Temp\Shots", ScreenshotWatcher.DefaultFolder);
            Environment.SetEnvironmentVariable("SWIVEL_SCREENSHOTS", null);
            Assert.IsFalse(string.IsNullOrEmpty(ScreenshotWatcher.DefaultFolder));
        }
        finally { Environment.SetEnvironmentVariable("SWIVEL_SCREENSHOTS", old); }
    }

    [TestMethod]
    public void HungPrintsSurviveARestart()
    {
        var folder = TestSetup.NewFolder("Rope");
        var file = Path.Combine(folder, "clothesline.json");
        var a = Path.Combine(folder, "a.png");
        var b = Path.Combine(folder, "b 中文.png");
        File.WriteAllBytes(a, [1]);
        File.WriteAllBytes(b, [1]);
        var when = new DateTime(2026, 10, 21, 10, 15, 0);
        RopeStore.Save(file, [new RopeEntry(b, true, when), new RopeEntry(a, false, when)]);
        var loaded = RopeStore.Load(file);
        CollectionAssert.AreEqual(new[] { new RopeEntry(b, true, when), new RopeEntry(a, false, when) }, loaded);
        Assert.IsFalse(File.Exists(file + ".tmp"));

        // A damaged file starts over empty instead of failing.
        File.WriteAllText(file, "{ not json");
        Assert.IsEmpty(RopeStore.Load(file));
        Assert.IsEmpty(RopeStore.Load(Path.Combine(folder, "missing.json")));

        // The controller forgets prints whose files have gone, newest stays first.
        var rope = Clothesline.Shared;
        var (store, copies) = (rope.StoreFile, rope.CopiesFolder);
        try
        {
            rope.UseFiles(file, Path.Combine(folder, "Copies"));
            rope.AddEntriesForTest([new RopeEntry(b, true, when), new RopeEntry(a, false, when), new RopeEntry(Path.Combine(folder, "gone.png"), false, when)]);
            rope.ReloadEntriesForTest();
            Assert.HasCount(3, rope.Entries);
            Assert.IsTrue(rope.CanDiscardFile(b));
            Assert.IsFalse(rope.CanDiscardFile(a), "a file hung from elsewhere is the user's: only taken off the rope");
        }
        finally { rope.UseFiles(store, copies); }
    }

    [TestMethod]
    public void ACopiedImageIsPutAwayAndNeverLeftBehind()
    {
        var root = TestSetup.NewFolder("Release");
        var copies = Path.Combine(root, "Clothesline");
        var shots = Path.Combine(root, "Screenshots");
        Directory.CreateDirectory(copies);
        Directory.CreateDirectory(shots);
        string Waiting(string name)
        {
            var path = Path.Combine(copies, name);
            File.WriteAllText(path, name);
            return path;
        }

        // Saved in the Screenshots folder, next to (never over) a file of the same name.
        File.WriteAllText(Path.Combine(shots, "Shot.png"), "older");
        var saved = Clothesline.Save(Waiting("Shot.png"), shots);
        Assert.AreEqual(Path.Combine(shots, "Shot 2.png"), saved);
        Assert.AreEqual("older", File.ReadAllText(Path.Combine(shots, "Shot.png")));
        Assert.AreEqual("Shot.png", File.ReadAllText(Path.Combine(shots, "Shot 2.png")));

        // The folder is gone (an unplugged drive), or is the file's own folder: nothing moves, so the caller hangs it.
        var other = Waiting("Other.png");
        Assert.IsNull(Clothesline.Save(other, Path.Combine(root, "Unplugged", "Screenshots")));
        Assert.IsNull(Clothesline.Save(other, copies));
        Assert.IsTrue(File.Exists(other));

        // Through the controller: with SWIVEL_SCREENSHOTS pointing at the test folder, a released copy lands there.
        var rope = Clothesline.Shared;
        var (store, oldCopies) = (rope.StoreFile, rope.CopiesFolder);
        var oldShots = Environment.GetEnvironmentVariable("SWIVEL_SCREENSHOTS");
        try
        {
            rope.UseFiles(Path.Combine(root, "clothesline.json"), copies);
            Environment.SetEnvironmentVariable("SWIVEL_SCREENSHOTS", shots);
            var released = rope.Release(other);
            Assert.AreEqual(Path.Combine(shots, "Other.png"), released);
            Assert.IsFalse(File.Exists(other));
            // A screenshot is already where it belongs.
            var screenshot = Path.Combine(shots, "Screenshot.png");
            File.WriteAllText(screenshot, "s");
            Assert.AreEqual(screenshot, rope.Release(screenshot));
            Assert.IsTrue(File.Exists(screenshot));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SWIVEL_SCREENSHOTS", oldShots);
            rope.UseFiles(store, oldCopies);
        }
    }

    [TestMethod]
    public void OnlyPicturesAndRecordingsHang()
    {
        foreach (var ok in new[] { "a.png", "b.JPG", "c.jpeg", "d.heic", "e.gif", "f.webp", "g.mov", "h.mp4", "i.tiff", "j.bmp" })
            Assert.IsTrue(Clothesline.IsMedia(ok), ok);
        foreach (var no in new[] { "a.txt", "b.pdf", "c.zip", "d", "e.mp3" })
            Assert.IsFalse(Clothesline.IsMedia(no), no);
    }

    [TestMethod]
    public void TheStripNeverTakesFocusAndLetsClicksThroughUntilAPrintIsUnderThePointer() => Sta(() =>
    {
        // Only the window handle is created; nothing is shown on screen.
        var panel = new RopePanel();
        var hwnd = new System.Windows.Interop.WindowInteropHelper(panel).EnsureHandle();
        int style = Native.GetWindowLongW(hwnd, Native.GWL_EXSTYLE);
        Assert.AreNotEqual(0, style & Native.WS_EX_NOACTIVATE, "takes focus");
        Assert.AreNotEqual(0, style & Native.WS_EX_TOOLWINDOW, "shows in Alt + Tab");
        Assert.AreNotEqual(0, style & Native.WS_EX_TRANSPARENT, "blocks clicks while nothing is under the pointer");
        Assert.IsTrue(panel.Topmost && !panel.ShowInTaskbar && !panel.ShowActivated);
        panel.Rope.CatchMouse!(true);
        Assert.AreEqual(0, Native.GetWindowLongW(hwnd, Native.GWL_EXSTYLE) & Native.WS_EX_TRANSPARENT, "a print under the pointer must catch it");
        panel.Rope.CatchMouse!(false);
        Assert.AreNotEqual(0, Native.GetWindowLongW(hwnd, Native.GWL_EXSTYLE) & Native.WS_EX_TRANSPARENT);
        panel.Close();
    });

    // MARK: 1.3.3 colours, the ready cue and Feel & Text tuning

    static bool Near(Color a, Color b, int tolerance = 1) =>
        Math.Abs(a.R - b.R) <= tolerance && Math.Abs(a.G - b.G) <= tolerance && Math.Abs(a.B - b.B) <= tolerance;

    [TestMethod]
    public void ReadyDropTurnsRopeBlueThenRestoresTheChosenColor() => Sta(() =>
    {
        var colors = new ClotheslineColors { Rope = RopeColorPreset.Sage.Rgb()!, Peg = RopeColorPreset.Silver.Rgb()! };
        Prefs.ClotheslineColors = colors;
        foreach (bool reduced in new[] { false, true })
        {
            var view = Rope(reduced: reduced);
            view.Lower(fromHidden: true);
            Run(view, 0.5);
            Assert.AreEqual(colors.Rope.Color, view.DrawnRopeColor, "idle rope in the chosen colour");
            view.SetReceivingHighlight(Fixtures[0]);
            Run(view, 0.5);
            Assert.IsTrue(Near(view.DrawnRopeColor, RopeDropFeedback.ReadyBlue), $"ready rope is {view.DrawnRopeColor} (reduced: {reduced})");
            Assert.IsGreaterThan(4.9, view.DrawnRopeWidth);
            Assert.IsGreaterThan(0.39, view.RopeShadowOpacity);
            Assert.IsTrue(Near(view.RopeShadowColor, RopeDropFeedback.ReadyBlue), "a light blue halo");
            Assert.AreEqual(colors, Prefs.ClotheslineColors, "the cue never touches the saved colours");
            view.SetReceivingHighlight(null);
            Run(view, 0.5);
            // Exactly the chosen colour again: no residual tint from the last frames of the fade.
            Assert.AreEqual(colors.Rope.Color, view.DrawnRopeColor, $"reduced: {reduced}");
            Assert.IsLessThan(0.01, Math.Abs(view.DrawnRopeWidth - 2.6));
            Assert.IsLessThan(0.19, view.RopeShadowOpacity);
            Assert.AreEqual(Colors.Black, view.RopeShadowColor);
        }
    });

    [TestMethod]
    public void ReadyDropRequiresSupportedMediaAndAPermittedOperation()
    {
        // Always a copy toward the rope: Explorer would delete the source after a move.
        Assert.AreEqual(DragDropEffects.Copy, RopeDropFeedback.Operation(DragDropEffects.Copy | DragDropEffects.Move, acceptsMedia: true));
        Assert.AreEqual(DragDropEffects.Copy, RopeDropFeedback.Operation(DragDropEffects.Copy, acceptsMedia: true));
        Assert.AreEqual(DragDropEffects.Copy, RopeDropFeedback.Operation(DragDropEffects.All, acceptsMedia: true));
        foreach (var offered in new[] { DragDropEffects.None, DragDropEffects.Link, DragDropEffects.Move, DragDropEffects.Scroll })
            Assert.AreEqual(DragDropEffects.None, RopeDropFeedback.Operation(offered, acceptsMedia: true), offered.ToString());
        Assert.AreEqual(DragDropEffects.None, RopeDropFeedback.Operation(DragDropEffects.Copy | DragDropEffects.Move, acceptsMedia: false));
    }

    [TestMethod]
    public void ColorChangesReachExistingPrintsAndARestingRopeAtOnce() => Sta(() =>
    {
        var view = Rope();
        view.Lower(fromHidden: true);
        Run(view, 1.5);
        var silver = RopeColorPreset.Silver.Rgb()!.Color;
        Assert.AreEqual(silver, view.DrawnRopeColor);
        for (int i = 0; i < view.PrintCount; i++) Assert.AreEqual(silver, view.PegColorAt(i));
        var chosen = new ClotheslineColors { Rope = RopeColorPreset.Rose.Rgb()!, Peg = RopeColorPreset.Graphite.Rgb()! };
        Prefs.ClotheslineColors = chosen;
        // No frame stepped: Prefs.Changed redraws the cord and every peg.
        Assert.AreEqual(chosen.Rope.Color, view.DrawnRopeColor);
        for (int i = 0; i < view.PrintCount; i++) Assert.AreEqual(chosen.Peg.Color, view.PegColorAt(i), $"peg {i}");
        view.SetItems([Fixtures[3], .. Fixtures.Take(3)]);
        Assert.AreEqual(chosen.Peg.Color, view.PegColorAt(0), "a new print gets the chosen peg");
        // Other settings changing leave the colours alone.
        Prefs.Interaction = Prefs.Interaction with { TextSize = TextSize.Large };
        Assert.AreEqual(chosen.Rope.Color, view.DrawnRopeColor);
    });

    [TestMethod]
    public void EveryRopePresetAndSliderExtremeIsDampedAndSettles()
    {
        MotionTuning[] tunes =
        [
            new() { Speed = 0.75, Bounce = 0 }, new() { Speed = 0.75, Bounce = 1 },
            new() { Speed = 1.5, Bounce = 0 }, new() { Speed = 1.5, Bounce = 1 },
            .. FeelChoices.Presets.Select(p => new InteractionSettings().Applying(p).Clothesline),
        ];
        foreach (var tuning in tunes)
        {
            foreach (var original in new[] { RopeTuning.Fall, RopeTuning.Sag, RopeTuning.Swing, RopeTuning.Arrive })
            {
                var o = original.Tuned(tuning);
                Assert.IsTrue(o.DampingRatio >= original.DampingRatio - 1e-9 && o.DampingRatio <= 1.00001, $"ζ {o.DampingRatio} for {tuning}");
                double x = -1, v = 0;
                for (int i = 0; i < 960; i++)
                {
                    RopeTuning.Advance(ref x, ref v, 0, o);
                    Assert.IsTrue(double.IsFinite(x) && double.IsFinite(v) && Math.Abs(x) <= 1.1);
                }
                Assert.IsTrue(Math.Abs(x) < 0.001 && Math.Abs(v) < 0.01, $"not settled: {x}, {v} for {tuning}");
            }
        }
        // The defaults keep the 1.2 rope exactly.
        var same = RopeTuning.Fall.Tuned(new MotionTuning());
        Assert.IsTrue(Math.Abs(same.K - RopeTuning.Fall.K) < 1e-9 && Math.Abs(same.C - RopeTuning.Fall.C) < 1e-9, same.ToString());
    }

    [TestMethod]
    public void BounceCanOnlyReduceTheRopesOvershoot()
    {
        double Overshoot(MotionTuning t) => Simulate(RopeTuning.Fall.Tuned(t), -RopeView.PanelHeight, 0).Max();
        double original = Overshoot(new MotionTuning());
        Assert.IsLessThanOrEqualTo(original + 1e-9, Overshoot(new MotionTuning { Bounce = 0.5 }));
        Assert.IsLessThan(0.05, Overshoot(new MotionTuning { Bounce = 0 }), "calm: no overshoot at all");
        Assert.IsLessThan(6, Overshoot(new MotionTuning { Speed = 1.5, Bounce = 1 }));
    }

    [TestMethod]
    public void ClotheslineSpeedMovesTheRopeAndOnlyTheRope() => Sta(() =>
    {
        double LoweredAfter(double seconds)
        {
            var view = Rope();
            view.Lower(fromHidden: true);
            Run(view, seconds);
            return view.RopeY(450);
        }
        double normal = LoweredAfter(0.12);
        Prefs.Interaction = Prefs.Interaction.With(FeelArea.Menu, new MotionTuning { Speed = 1.5 });
        Assert.AreEqual(normal, LoweredAfter(0.12), 1e-9, "the menu's speed must not move the rope");
        Prefs.Interaction = Prefs.Interaction.With(FeelArea.Clothesline, new MotionTuning { Speed = 1.5 });
        Assert.IsGreaterThan(normal + 1, LoweredAfter(0.12), "a faster clothesline lowers sooner");
        Assert.AreEqual(1, Prefs.Interaction.Tools.Speed);
    });

    // MARK: Filmstrips for review (only with SWIVEL_ROPE_FRAMES set)

    [TestMethod]
    public void RenderRopeAndCardFilmstrips()
    {
        var folder = Environment.GetEnvironmentVariable("SWIVEL_ROPE_FRAMES");
        if (string.IsNullOrEmpty(folder)) return;   // nothing to review
        Sta(() => Assert.AreEqual(0, ClotheslineFrames.Render(folder)));
        foreach (var name in new[] { "rope-1-drop.png", "rope-2-hover-press-copy.png", "rope-3-arrive.png", "rope-4-lift.png", "rope-5-discard.png", "rope-chip.png", "rope-ready-normal.png", "rope-ready-reduced.png", "card.png" })
            Assert.IsTrue(File.Exists(Path.Combine(folder, name)), name);
    }
}
