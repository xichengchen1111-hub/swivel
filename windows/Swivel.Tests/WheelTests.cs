using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Swivel;

namespace Swivel.Tests;

/// The menu, its feel and its feedback (a port of the Mac app's FeelTests and the menu parts of SettingsFeelTests):
/// springs must be exact and continuous, menus must hit-test stably, taps during a drag must be recognised.
[TestClass]
public sealed class WheelTests
{
    [TestInitialize]
    public void Reset()
    {
        TestSetup.ResetPrefs();
        // Tests never make a sound: the audio device stays closed even when sounds are switched on.
        Sounds.DeviceDisabled = true;
        Sounds.ResetForTests();
        Prefs.SoundEffects = false;
    }

    /// Runs the dispatcher (background synthesis completions, queued redraws) until `done` or a timeout.
    static void PumpUntil(Func<bool> done, double seconds = 10)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!done() && DateTime.UtcNow < until)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }
    }

    // MARK: Springs

    static void Run(ref Spring spring, double seconds, double step = 1.0 / 120)
    {
        double t = 0;
        while (t < seconds - 1e-9)
        {
            spring.Step(step);
            t += step;
        }
    }

    [TestMethod]
    public void CriticallyDampedSpringSettlesOnSchedule()
    {
        // ζ = 1: within 5 % of the target at 0.755 × response, within 1 % at 1.06 × response.
        var spring = new Spring(0, 0.4, 1) { Target = 1 };
        Run(ref spring, 0.755 * 0.4);
        Assert.IsLessThan(0.01, Math.Abs(spring.Value - 0.95));
        Run(ref spring, (1.06 - 0.755) * 0.4);
        Assert.IsTrue(spring.Value > 0.985 && spring.Value <= 1);
    }

    [TestMethod]
    public void UnderdampedSpringOvershootsByTheTextbookAmount()
    {
        // ζ = 0.62 overshoots by e^(−πζ/√(1−ζ²)) ≈ 8.35 %.
        var spring = new Spring(0, 0.24, 0.62) { Target = 1 };
        double peak = 0;
        for (int i = 0; i < 240; i++)
        {
            spring.Step(1.0 / 480);
            peak = Math.Max(peak, spring.Value);
        }
        Assert.IsLessThan(0.005, Math.Abs(peak - 1.0835));
    }

    [TestMethod]
    public void RetargetingMidFlightKeepsPositionAndVelocity()
    {
        var spring = new Spring(0, 0.24, 0.62) { Target = 1 };
        Run(ref spring, 0.05);
        var (value, velocity) = (spring.Value, spring.Velocity);
        spring.Target = 0;
        spring.Damping = 0.95;
        Assert.AreEqual(value, spring.Value);
        Assert.AreEqual(velocity, spring.Velocity);
        // The next millisecond continues along the old velocity: no jump, no "brick wall".
        spring.Step(1.0 / 1000);
        Assert.IsLessThan(0.001, Math.Abs(spring.Value - (value + velocity / 1000)));
    }

    [TestMethod]
    public void StepSizeDoesNotChangeTheMotion()
    {
        // Solved analytically, so one 30 fps frame equals four 120 fps frames.
        var coarse = new Spring(0, 0.3, 0.7) { Target = 1 };
        var fine = coarse;
        coarse.Step(1.0 / 30);
        for (int i = 0; i < 4; i++) fine.Step(1.0 / 120);
        Assert.IsLessThan(1e-6, Math.Abs(coarse.Value - fine.Value));
        Assert.IsLessThan(1e-4, Math.Abs(coarse.Velocity - fine.Velocity));
    }

    [TestMethod]
    public void SpringSettlesAndStopsAtTarget()
    {
        var spring = new Spring(2, 0.2, 0.8) { Target = -1 };
        Run(ref spring, 2);
        Assert.IsTrue(spring.IsSettled);
        Assert.AreEqual(-1, spring.Value);
        Assert.AreEqual(0, spring.Velocity);
    }

    // MARK: Hit-testing

    static List<WheelItem> Items(int count, string prefix = "Item") =>
        Enumerable.Range(0, count).Select(i => new WheelItem($"{prefix} {i}", _ => Task.FromResult(new ActionResult("")))).ToList();

    static WheelView Menu(MenuStyle style, int count = 8, bool live = true)
    {
        var view = new WheelView { ViewSize = WheelView.MenuSize(style, count, live: style == MenuStyle.Wheel && live), MenuStyle = style, ManualClock = true };
        view.Configure(Items(count), WheelMode.Convert, 0, 1);
        return view;
    }

    /// A point around the wheel's centre in WPF coordinates, by angle in y-up degrees (0 = right, 90 = top).
    static Point Polar(WheelView view, double degrees, double radius) =>
        new(view.ViewSize.Width / 2 + Math.Cos(degrees * Math.PI / 180) * radius,
            view.ViewSize.Height / 2 - Math.Sin(degrees * Math.PI / 180) * radius);

    [STATestMethod]
    public void WheelSelectsByAngleWithHysteresisAndAHubDeadZone()
    {
        var view = Menu(MenuStyle.Wheel);
        Assert.IsFalse(view.Track(Polar(view, 90, 20)));                      // hub: nothing
        Assert.IsTrue(view.Track(Polar(view, 90, 90)));
        Assert.AreEqual(0, view.Hovered);
        // Sector 0 spans 67.5°–112.5°. Just past its edge it stays chosen…
        view.Track(Polar(view, 64, 90));
        Assert.AreEqual(0, view.Hovered);
        // …and well past it the next sector (clockwise) takes over.
        view.Track(Polar(view, 55, 90));
        Assert.AreEqual(1, view.Hovered);
        // Past the ring (a flick that overshoots) still selects by angle…
        view.Track(Polar(view, 0, 160));
        Assert.AreEqual(2, view.Hovered);
        // …but well away from it is empty space, where releasing cancels.
        view.Track(Polar(view, 0, 185));
        Assert.IsNull(view.Hovered);
        view.Track(Polar(view, 0, 90));
        Assert.AreEqual(2, view.Hovered);
        // Falling back into the hub needs to go clearly inside it.
        view.Track(Polar(view, 0, 50));
        Assert.AreEqual(2, view.Hovered);
        view.Track(Polar(view, 0, 40));
        Assert.IsNull(view.Hovered);
    }

    [STATestMethod]
    public void WheelSectorsGoClockwiseFromTheTop()
    {
        var view = Menu(MenuStyle.Wheel, 4);
        // Four sectors: top, right, bottom, left.
        double[] angles = [90, 0, -90, 180];
        for (int i = 0; i < 4; i++)
        {
            view.PointerLeft();
            view.Track(Polar(view, angles[i], 90));
            Assert.AreEqual(i, view.Hovered, $"sector at {angles[i]}°");
        }
    }

    [STATestMethod]
    public void ReturningToTheHubAfterAVisitArmsCancel()
    {
        var view = Menu(MenuStyle.Wheel);
        view.IsLive = true;
        view.BeginOpen();
        view.Track(Polar(view, 90, 20));
        Assert.IsFalse(view.CancelArmed, "the hub before any visit is not a cancel");
        view.Track(Polar(view, 90, 90));
        Assert.AreEqual(0, view.Hovered);
        view.Track(Polar(view, 90, 10));
        Assert.IsNull(view.Hovered);
        Assert.IsTrue(view.CancelArmed);
        // Leaving the menu altogether is not a cancel on the hub.
        view.PointerLeft();
        Assert.IsFalse(view.CancelArmed);
    }

    [STATestMethod]
    public void WheelIgnoresTheFirstFewPointsAfterOpeningOffCentre()
    {
        var view = Menu(MenuStyle.Wheel);
        var start = Polar(view, 0, 90);  // opened shifted, pointer already over a sector
        view.BeginOpen(start);
        Assert.IsFalse(view.Track(new Point(start.X + 4, start.Y)));
        Assert.IsNull(view.Hovered);
        Assert.IsTrue(view.Track(new Point(start.X + 12, start.Y)));
        Assert.AreEqual(2, view.Hovered);
    }

    [STATestMethod]
    public void ListRowsHaveNoDeadGapsBetweenThem()
    {
        var view = Menu(MenuStyle.List, 4);
        var size = view.ViewSize;
        double top = WheelView.HeaderHeight + 4;   // y-down: below the header
        // Every point down the column belongs to some row, including the 4 DIP gaps.
        for (double y = top + 1; y < top + 4 * WheelView.ListRow - 1; y++)
            Assert.IsTrue(view.Track(new Point(size.Width / 2, y)), $"no row at y = {y}");
        view.Track(new Point(size.Width / 2, top + 0.5 * WheelView.ListRow));
        Assert.AreEqual(0, view.Hovered);
        view.Track(new Point(size.Width / 2, top + 3.5 * WheelView.ListRow));
        Assert.AreEqual(3, view.Hovered);
        // The header is not a row.
        Assert.IsFalse(view.Track(new Point(size.Width / 2, 10)));
    }

    [STATestMethod]
    public void OtherTemplatesHitWholeCellsAndNotTheHeader()
    {
        foreach (var style in new[] { MenuStyle.Shelf, MenuStyle.Notes, MenuStyle.Clothesline })
        {
            var view = Menu(style, 6);
            var size = view.ViewSize;
            Assert.IsFalse(view.Track(new Point(size.Width / 2, 3)), $"{style}: header");
            var seen = new HashSet<int>();
            // Sweep across the item area: every item is reachable and the indices only grow left to right.
            double y = style switch
            {
                MenuStyle.Shelf => size.Height - 30 - 60,                 // a book's middle (y-down)
                MenuStyle.Notes => WheelView.HeaderHeight + 10 + 54,      // the first row of notes
                _ => WheelView.HeaderHeight + 60,                         // the tags
            };
            int last = -1;
            for (double x = 0; x < size.Width; x += 2)
            {
                view.Track(new Point(x, y));
                if (view.Hovered is { } i)
                {
                    Assert.IsGreaterThanOrEqualTo(last, i, $"{style}: order at x = {x}");
                    last = i;
                    seen.Add(i);
                }
            }
            int expected = style == MenuStyle.Notes ? 4 : 6;   // notes: four columns in the first row
            Assert.HasCount(expected, seen, $"{style}: {string.Join(",", seen)}");
        }
    }

    [STATestMethod]
    public void NotesSecondRowAndGapsBetweenNotes()
    {
        var view = Menu(MenuStyle.Notes, 6);
        var size = view.ViewSize;
        double originX = (size.Width - 4 * 108) / 2;
        // Second row, second column.
        view.Track(new Point(originX + 108 + 54, WheelView.HeaderHeight + 10 + 108 + 54));
        Assert.AreEqual(5, view.Hovered);
        // The gap between two notes still belongs to one of them.
        view.Track(new Point(originX + 107.5, WheelView.HeaderHeight + 10 + 54));
        Assert.AreEqual(0, view.Hovered);
    }

    [STATestMethod]
    public void ConfirmPressesTheItemAndBlocksTracking()
    {
        var view = Menu(MenuStyle.Wheel);
        view.Track(Polar(view, 90, 90));
        view.BeginConfirm(3);
        Assert.AreEqual(3, view.Pressed);
        Assert.AreEqual(3, view.Hovered);
        Assert.IsFalse(view.Track(Polar(view, 90, 90)), "a pressed menu ignores the pointer");
        for (int i = 0; i < 120; i++) view.Advance(1.0 / 60);
        Assert.IsTrue(view.IsSettled);
    }

    [STATestMethod]
    public void PageTurnKeepsVisitAndTurnsTheRing()
    {
        var view = Menu(MenuStyle.Wheel);
        view.IsLive = true;
        view.Configure(Items(3, "Next"), WheelMode.Convert, 1, 2);
        Assert.AreEqual(1, view.Page);
        Assert.HasCount(3, view.Items);
        Assert.IsFalse(view.IsSettled, "the ring turns into place");
    }

    [STATestMethod]
    public void MenuSizesFollowTheMac()
    {
        Assert.AreEqual(new Size(400, 400), WheelView.MenuSize(MenuStyle.Wheel, 8, live: true));
        Assert.AreEqual(new Size(320, 320), WheelView.MenuSize(MenuStyle.Wheel, 8));
        Assert.AreEqual(new Size(240, 34 + 4 * 32 + 14), WheelView.MenuSize(MenuStyle.List, 4));
        Assert.AreEqual(new Size(4 * 108 + 28, 2 * 108 + 34 + 28), WheelView.MenuSize(MenuStyle.Notes, 6));
        Assert.AreEqual(new Size(8 * 82 + 64, 178 + 34), WheelView.MenuSize(MenuStyle.Clothesline, 8));
        Assert.AreEqual(220, WheelView.MenuSize(MenuStyle.Shelf, 1).Width);
    }

    // MARK: Paging

    [TestMethod]
    public void ItemsArePagedByEight()
    {
        var pages = WheelController.Paginate(Items(19));
        CollectionAssert.AreEqual(new[] { 8, 8, 3 }, pages.Select(p => p.Count).ToArray());
        Assert.AreEqual("Item 8", pages[1][0].Title);
        Assert.HasCount(1, WheelController.Paginate(Items(8)));
        Assert.IsEmpty(WheelController.Paginate([]));
    }

    // MARK: Drag tap detection

    sealed class Feeder
    {
        public readonly DragTapDetector Detector = new() { ThresholdX = 4, ThresholdY = 4, KeyBit = m => 1 << (int)m };
        public readonly List<string> Events = [];
        public int X, Y = 0;

        public void Sample(bool down, int keys = 0, int dx = 0)
        {
            X += dx;
            var r = Detector.Feed(down, X, Y, keys);
            if (r.Started) Events.Add("start");
            if (r.Tap is { } mode) Events.Add($"tap {mode}");
            if (r.Ended) Events.Add("end");
        }
    }

    // Key bits in these tests: Convert = 1, Tools = 2, Actions = 4.

    [TestMethod]
    public void TapDuringADragOpensThatMenu()
    {
        var f = new Feeder();
        f.Sample(false);
        f.Sample(true);
        f.Sample(true, dx: 2);
        f.Sample(true, dx: 6);              // past the threshold: the drag starts
        f.Sample(true, keys: 2);            // Tools key goes down
        f.Sample(true, keys: 2, dx: 1);     // still held: no second tap
        f.Sample(true, keys: 0);
        f.Sample(true, keys: 2);            // a second tap (paging)
        f.Sample(true, keys: 2 | 1);        // another key while holding the first
        f.Sample(false);
        CollectionAssert.AreEqual(new[] { "start", "tap Tools", "tap Tools", "tap Convert", "end" }, f.Events);
    }

    [TestMethod]
    public void EarlyTapBeforeTheDragIsRecognisedCounts()
    {
        var f = new Feeder();
        f.Sample(false);
        f.Sample(true);
        f.Sample(true, keys: 4);            // tapped before moving
        f.Sample(true, keys: 4, dx: 8);     // the drag is recognised while it's still held
        f.Sample(false);
        CollectionAssert.AreEqual(new[] { "start", "tap Actions", "end" }, f.Events);
    }

    [TestMethod]
    public void EarlyTapReleasedBeforeTheDragDoesNotCount()
    {
        var f = new Feeder();
        f.Sample(false);
        f.Sample(true);
        f.Sample(true, keys: 4);
        f.Sample(true, keys: 0);
        f.Sample(true, dx: 8);
        CollectionAssert.AreEqual(new[] { "start" }, f.Events);
    }

    [TestMethod]
    public void KeyHeldBeforeThePressNeverCounts()
    {
        var f = new Feeder();
        f.Sample(false, keys: 1);           // Shift-click style: held first
        f.Sample(true, keys: 1);
        f.Sample(true, keys: 1, dx: 10);
        f.Sample(true, keys: 1, dx: 10);
        f.Sample(false, keys: 1);
        CollectionAssert.AreEqual(new[] { "start", "end" }, f.Events);
        // Even if the first poll after the press is the one that sees the key, it doesn't count.
        var g = new Feeder();
        g.Sample(false);
        g.Sample(true, keys: 1);
        g.Sample(true, keys: 1, dx: 10);
        CollectionAssert.AreEqual(new[] { "start" }, g.Events);
    }

    [TestMethod]
    public void TapWithoutMovingIsNotADrag()
    {
        var f = new Feeder();
        f.Sample(false);
        f.Sample(true);
        f.Sample(true, keys: 2, dx: 1);
        f.Sample(true, keys: 0, dx: 1);
        f.Sample(false);
        Assert.IsEmpty(f.Events);
        Assert.IsFalse(f.Detector.IsDragging);
    }

    [TestMethod]
    public void ReleaseEndsTheDragOnceAndDisabledIgnoresDrags()
    {
        var f = new Feeder();
        f.Sample(true);
        f.Sample(true, dx: 10);
        f.Sample(false);
        f.Sample(false);
        CollectionAssert.AreEqual(new[] { "start", "end" }, f.Events);
        var d = new DragTapDetector { KeyBit = m => 1 << (int)m };
        d.Feed(true, 0, 0, 0, enabled: false);
        var r = d.Feed(true, 50, 0, 2, enabled: false);
        Assert.IsNull(r.Tap);
        Assert.IsFalse(r.Started);
    }

    [TestMethod]
    public void DetectorUsesTheConfiguredKeys()
    {
        Prefs.SetKey(TriggerKey.Alt, WheelMode.Convert);   // Convert takes Alt, Actions gets Shift
        var d = new DragTapDetector { ThresholdX = 4, ThresholdY = 4 };
        d.Feed(true, 0, 0, 0);
        Assert.IsTrue(d.Feed(true, 10, 0, 0).Started);
        Assert.AreEqual(WheelMode.Convert, d.Feed(true, 10, 0, 1 << (int)TriggerKey.Alt).Tap);
        Assert.AreEqual(WheelMode.Actions, d.Feed(true, 10, 0, 1 << (int)TriggerKey.Alt | 1 << (int)TriggerKey.Shift).Tap);
    }

    // MARK: Sounds

    [TestMethod]
    public void InterfaceSoundsAreShortQuietAndStartImmediately()
    {
        foreach (var effect in Enum.GetValues<SoundEffect>())
        {
            foreach (var (pitch, gain) in SoundSynth.Variants(effect))
            {
                var samples = SoundSynth.Samples(effect, pitch, gain);
                double peak = samples.Max(s => Math.Abs(s));
                Assert.IsTrue(peak > 0.03 && peak <= 0.23, $"{effect} peak {peak}");
                Assert.IsLessThanOrEqualTo(48_000 * 3 / 10, samples.Length, $"{effect} too long");
                Assert.IsTrue(Math.Abs(samples[0]) > 0 || Math.Abs(samples[1]) > 0, $"{effect} starts with silence");
                Assert.IsLessThan(0.001, Math.Abs(samples[^1]), $"{effect} clicks at the end");
                Assert.IsTrue(samples.All(s => !float.IsNaN(s) && Math.Abs(s) < 1), $"{effect} clips");
            }
        }
        Assert.HasCount(3, SoundSynth.Variants(SoundEffect.Hover));
    }

    [TestMethod]
    public void WavHeaderIsMono16BitAt48kHz()
    {
        var wav = SoundSynth.Wav([0, 0.5f, -0.5f]);
        Assert.HasCount(44 + 6, wav);
        Assert.AreEqual("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.AreEqual("WAVEfmt ", Encoding.ASCII.GetString(wav, 8, 8));
        Assert.AreEqual(36 + 6, BitConverter.ToInt32(wav, 4));
        Assert.AreEqual(1, BitConverter.ToInt16(wav, 22));          // mono
        Assert.AreEqual(48_000, BitConverter.ToInt32(wav, 24));
        Assert.AreEqual(16, BitConverter.ToInt16(wav, 34));
        Assert.AreEqual("data", Encoding.ASCII.GetString(wav, 36, 4));
        Assert.AreEqual(6, BitConverter.ToInt32(wav, 40));
        Assert.AreEqual(16383, BitConverter.ToInt16(wav, 46));
        Assert.AreEqual(-16383, BitConverter.ToInt16(wav, 48));
    }

    [TestMethod]
    public void SoundsStaySilentWhenTurnedOff()
    {
        Prefs.SoundEffects = false;
        Feedback.Hover();
        Feedback.Press();
        Feedback.Prepare();
        Assert.AreEqual(0, Sounds.RegisteredCount);
    }

    [TestMethod]
    public void SoundStylesRespectVolumeAndNeverClip()
    {
        foreach (var style in FeelChoices.SoundStyles)
        {
            foreach (var effect in Enum.GetValues<SoundEffect>())
            {
                var samples = SoundSynth.Samples(effect, 1, 1, new SoundConfiguration(style, 0.25));
                double peak = samples.Max(s => Math.Abs(s));
                Assert.IsTrue(peak > 0 && peak <= 0.23 * 0.25 * style.Gain() + 1e-6, $"{style} {effect} peak {peak}");
                Assert.IsLessThan(0.001, Math.Abs(samples[^1]), $"{style} {effect} clicks at the end");
                Assert.IsTrue(SoundSynth.Samples(effect, 1, 1, new SoundConfiguration(style, 0)).All(s => s == 0), $"{style} {effect} muted");
            }
        }
        // Soft is lower, bright is higher: count the zero crossings of the done chime (no noise in it).
        static int crossings(SoundStyle style)
        {
            var s = SoundSynth.Samples(SoundEffect.Done, 1, 1, new SoundConfiguration(style, 1));
            return Enumerable.Range(1, s.Length - 1).Count(i => Math.Sign(s[i - 1]) != Math.Sign(s[i]));
        }
        Assert.IsTrue(crossings(SoundStyle.Soft) < crossings(SoundStyle.Classic) && crossings(SoundStyle.Classic) < crossings(SoundStyle.Bright));
        // Damaged volumes can't break the sounds; the level is whole percent.
        Assert.AreEqual(1, new SoundConfiguration(volume: double.NegativeInfinity).Volume);
        Assert.AreEqual(1, new SoundConfiguration(volume: 9).Volume);
        Assert.AreEqual(0, new SoundConfiguration(volume: -9).Volume);
        Assert.AreEqual(new SoundConfiguration(SoundStyle.Soft, 0.5), new SoundConfiguration(SoundStyle.Soft, 0.501));
        Assert.AreNotEqual(new SoundConfiguration(SoundStyle.Soft, 0.5), new SoundConfiguration(SoundStyle.Soft, 1));
        Assert.AreNotEqual(new SoundConfiguration(SoundStyle.Soft, 0.5), new SoundConfiguration(SoundStyle.Bright, 0.5));
    }

    [STATestMethod]
    public void ChangingTheSoundStyleOrVolumeRebuildsTheSounds()
    {
        Prefs.SoundEffects = true;
        Prefs.Interaction = new InteractionSettings { SoundStyle = SoundStyle.Soft, Volume = 0.5 };
        Feedback.Prepare();
        PumpUntil(() => Sounds.Loaded == Sounds.Configuration);
        Assert.AreEqual(new SoundConfiguration(SoundStyle.Soft, 0.5), Sounds.Loaded);
        Assert.AreEqual(3 + 5, Sounds.RegisteredCount);
        Feedback.Press();
        Assert.AreEqual(1, Sounds.PlayedCount);

        // A new volume: the next sound waits for the rebuilt set, then plays at the new level.
        Prefs.Interaction = Prefs.Interaction with { Volume = 0.3 };
        Feedback.Press();
        Assert.AreEqual(1, Sounds.PlayedCount, "nothing plays at the old level");
        PumpUntil(() => Sounds.PlayedCount == 2);
        Assert.AreEqual(new SoundConfiguration(SoundStyle.Soft, 0.3), Sounds.Loaded);
        Assert.AreEqual(2, Sounds.PlayedCount);

        // A new style too.
        Prefs.Interaction = Prefs.Interaction with { SoundStyle = SoundStyle.Bright };
        Feedback.Done();
        PumpUntil(() => Sounds.Loaded == Sounds.Configuration);
        Assert.AreEqual(SoundStyle.Bright, Sounds.Loaded?.Style);
        Assert.AreEqual(3 + 5, Sounds.RegisteredCount, "the old set was released, not added to");

        // Zero volume and the master switch are silent and build nothing.
        Prefs.Interaction = Prefs.Interaction with { Volume = 0 };
        int played = Sounds.PlayedCount;
        Feedback.Press();
        PumpUntil(() => false, 0.1);
        Assert.AreEqual(played, Sounds.PlayedCount);
        Assert.AreEqual(SoundStyle.Bright, Sounds.Loaded?.Style);
        Prefs.Interaction = Prefs.Interaction with { Volume = 1 };
        Prefs.SoundEffects = false;
        Feedback.Press();
        PumpUntil(() => false, 0.1);
        Assert.AreEqual(played, Sounds.PlayedCount);
    }

    [STATestMethod]
    public void HoverSoundSwitchMutesOnlyTheHoverTicks()
    {
        Prefs.SoundEffects = true;
        Prefs.Interaction = new InteractionSettings { HoverSound = false };
        Feedback.Prepare();
        PumpUntil(() => Sounds.Loaded == Sounds.Configuration);
        Assert.IsFalse(Sounds.Wants(SoundEffect.Hover));
        Feedback.Hover();
        Assert.AreEqual(0, Sounds.PlayedCount);
        Feedback.Page();
        Feedback.Press();
        Feedback.Toggle(true);
        Assert.AreEqual(3, Sounds.PlayedCount);
        Prefs.Interaction = Prefs.Interaction with { HoverSound = true };
        Feedback.Hover();
        Assert.AreEqual(4, Sounds.PlayedCount);
        // A live menu's hover goes through the same switch.
        Prefs.Interaction = Prefs.Interaction with { HoverSound = false };
        var view = Menu(MenuStyle.Wheel);
        view.IsLive = true;
        view.Track(Polar(view, 90, 90));
        Assert.AreEqual(0, view.Hovered);
        Assert.AreEqual(4, Sounds.PlayedCount);
    }

    // MARK: Toast

    [TestMethod]
    public void ToastSymbolsMapToFluentGlyphs()
    {
        string[] names = ["ok", "error", "info", "cancel", "busy", "wheel", "on", "off", "nosign", "copy", "clothesline"];
        var glyphs = names.Select(Toast.Glyph).ToList();
        foreach (var (name, glyph) in names.Zip(glyphs))
        {
            Assert.AreEqual(1, glyph.Length, name);
            Assert.IsTrue(glyph[0] >= '' && glyph[0] <= '', $"{name} is not an icon glyph");
        }
        // Distinct where it matters.
        Assert.HasCount(names.Length - 1, glyphs.Where(g => g != Toast.Glyph("info")));
        Assert.AreEqual(Toast.Glyph("info"), Toast.Glyph("something unknown"));
        // The Mac code's SF Symbol names work too.
        Assert.AreEqual(Toast.Glyph("ok"), Toast.Glyph("checkmark.circle.fill"));
        Assert.AreEqual(Toast.Glyph("error"), Toast.Glyph("exclamationmark.triangle.fill"));
        Assert.AreEqual(Toast.Glyph("busy"), Toast.Glyph("arrow.triangle.2.circlepath.circle"));
        Assert.AreEqual("", Toast.Glyph(""));
    }

    // MARK: Previews

    [STATestMethod]
    public void PreviewPointsAreInTheMenusOwnUnscaledCoordinates()
    {
        var host = (PreviewHost)MenuPreview.Create(MenuStyle.Wheel, ["JPG", "PNG", "HEIC", "WebP", "TIFF", "GIF", "PDF"], WheelMode.Convert, 1);
        host.Measure(new Size(250, 170));
        host.Arrange(new Rect(0, 0, 250, 170));
        var natural = WheelView.MenuSize(MenuStyle.Wheel, 7);
        Assert.AreEqual(natural, host.Preview.ViewSize);
        // The 320 DIP wheel is drawn scaled down into the card, centred: the host's centre is the wheel's centre.
        var centre = host.PreviewPoint(new Point(125, 85));
        Assert.IsNotNull(centre);
        Assert.IsLessThan(0.5, Math.Abs(centre.Value.X - natural.Width / 2));
        Assert.IsLessThan(0.5, Math.Abs(centre.Value.Y - natural.Height / 2));
        // The top edge of the drawn wheel is the top of its natural bounds.
        var top = host.PreviewPoint(new Point(125, 10.01));
        Assert.IsNotNull(top);
        Assert.IsLessThan(1, Math.Abs(top.Value.Y));
        // The margin around the scaled menu isn't part of it.
        Assert.IsNull(host.PreviewPoint(new Point(2, 2)));
    }

    [STATestMethod]
    public void StaticPreviewDrawsSettledAndIsNotLiveOffScreen()
    {
        var host = (PreviewHost)MenuPreview.Create(MenuStyle.List, ["JPG", "PNG", "HEIC"], WheelMode.Convert, 1);
        Assert.AreEqual(1, host.Preview.Hovered);
        Assert.IsFalse(host.Preview.IsLive);
        Assert.IsFalse(host.Preview.CancelArmed);
        host.IsInteractive = false;
        Assert.IsFalse(host.Preview.IsLive);
        // Nine titles: eight shown, and a second page dot.
        var many = (PreviewHost)MenuPreview.Create(MenuStyle.Wheel, Enumerable.Range(0, 9).Select(i => $"T{i}").ToList(), WheelMode.Tools);
        Assert.HasCount(8, many.Preview.Items);
        Assert.AreEqual(2, many.Preview.PageCount);
    }

    [STATestMethod]
    public void PreviewIsLiveOnlyInAWindowAndComesBackAtRest()
    {
        var host = (PreviewHost)MenuPreview.Create(MenuStyle.Wheel, ["JPG", "PNG", "HEIC", "WebP", "TIFF", "GIF", "PDF"], WheelMode.Convert, 1);
        var window = new Window
        {
            Width = 300, Height = 220, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            Left = -20000, Top = -20000, Content = host,
        };
        try
        {
            window.Show();
            Assert.IsTrue(host.Preview.IsLive);
            // Pressed mid-try, then the page goes away.
            host.Press(4);
            Assert.AreEqual(4, host.Preview.Pressed);
            Assert.AreEqual(4, host.Preview.Hovered);
            window.Hide();
            Assert.IsFalse(host.Preview.IsLive);
            Assert.IsNull(host.Preview.Pressed);
            Assert.AreEqual(1, host.Preview.Hovered);   // the resting picture again
            Assert.IsFalse(host.Preview.CancelArmed);
            window.Show();
            Assert.IsTrue(host.Preview.IsLive);
            // The resting hover is not a visit: the header or hub isn't "Cancel" on first entry.
            host.Follow(new Point(150, 110));
            Assert.IsFalse(host.Preview.CancelArmed);
            host.IsInteractive = false;
            Assert.IsFalse(host.Preview.IsLive);
            Assert.AreEqual(1, host.Preview.Hovered);
        }
        finally { window.Close(); }
    }

    // MARK: Feel & Text tuning

    /// Frames (60 fps) a confirmed press takes to settle completely.
    static int FramesToSettle(WheelView view)
    {
        view.Track(Polar(view, 90, 90));
        view.Advance(1);
        view.BeginConfirm(0);
        int frames = 0;
        while (!view.IsSettled && frames < 600) { view.Advance(1.0 / 60); frames++; }
        return frames;
    }

    static int FramesToBloom()
    {
        var view = Menu(MenuStyle.Shelf);
        view.BeginOpen();
        int frames = 0;
        while (!view.IsSettled && frames < 600) { view.Advance(1.0 / 60); frames++; }
        return frames;
    }

    [STATestMethod]
    public void MenuSpringsFollowTheMenuTuning()
    {
        if (Motion.Reduced) Assert.Inconclusive("Animation effects are off on this PC: menus keep the 1.2 timing.");
        int original = FramesToSettle(Menu(MenuStyle.Wheel));
        int originalBloom = FramesToBloom();
        Prefs.Interaction = new InteractionSettings().With(FeelArea.Menu, new MotionTuning { Speed = 1.5, Bounce = 0 });
        Assert.AreEqual(1.5, Menu(MenuStyle.Wheel).Tuning.Speed);
        int fast = FramesToSettle(Menu(MenuStyle.Wheel));
        Assert.IsLessThan(original * 0.8, fast, $"{fast} frames tuned vs {original}");
        // The opening bloom is on the tuned clock too.
        Assert.IsLessThan(originalBloom * 0.8, FramesToBloom());
        // Other areas' tuning doesn't touch the menu.
        Prefs.Interaction = new InteractionSettings().With(FeelArea.Clothesline, new MotionTuning { Speed = 1.5 })
            .With(FeelArea.Tools, new MotionTuning { Speed = 1.5 });
        Assert.AreEqual(original, FramesToSettle(Menu(MenuStyle.Wheel)));
    }

    [STATestMethod]
    public void PreviewsCanTryATuningBeforeItIsSaved()
    {
        if (Motion.Reduced) Assert.Inconclusive("Animation effects are off on this PC: menus keep the 1.2 timing.");
        var tuning = new MotionTuning { Speed = 1.5, Bounce = 0.2 };
        var host = (PreviewHost)MenuPreview.Create(MenuStyle.Wheel, ["JPG", "PNG", "HEIC"], WheelMode.Convert, 1, tuning: tuning);
        Assert.AreEqual(tuning, host.Tuning);
        Assert.AreEqual(tuning, host.Preview.Tuning);
        Assert.AreEqual(MotionTuning.Default, Prefs.Interaction.Menu, "nothing is saved");
        // Back to following the saved tuning, live.
        host.Tuning = null;
        Prefs.Interaction = new InteractionSettings().With(FeelArea.Menu, new MotionTuning { Speed = 0.75 });
        Assert.AreEqual(0.75, host.Preview.Tuning.Speed);
        // Replaying off screen does nothing (no frame driver, no sound).
        host.Replay();
        Assert.AreEqual(1, host.Preview.Hovered);
        Assert.IsTrue(host.Preview.IsSettled);
    }

    // MARK: Template colours

    static Color Read(WheelView view, Point at)
    {
        var bitmap = new RenderTargetBitmap((int)view.ViewSize.Width, (int)view.ViewSize.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var px = new byte[4];
        bitmap.CopyPixels(new Int32Rect((int)at.X, (int)at.Y, 1, 1), px, 4, 0);
        return Color.FromArgb(px[3], px[2], px[1], px[0]);
    }

    static Color Pixel(WheelView view, Point at)
    {
        var size = view.ViewSize;
        view.Measure(size);
        view.Arrange(new Rect(size));
        view.Redraw();
        return Read(view, at);
    }

    static void AssertClose(Color expected, Color actual, string what, int tolerance = 4)
    {
        Assert.AreEqual(255, actual.A, $"{what}: not opaque ({actual})");
        Assert.IsTrue(Math.Abs(expected.R - actual.R) <= tolerance && Math.Abs(expected.G - actual.G) <= tolerance
            && Math.Abs(expected.B - actual.B) <= tolerance, $"{what}: expected {expected}, drew {actual}");
    }

    static WheelView StillMenu(MenuStyle style, bool dark)
    {
        var view = new WheelView { ViewSize = WheelView.MenuSize(style, 8), MenuStyle = style, ManualClock = true, DarkOverride = dark };
        view.Configure(Items(8), WheelMode.Convert, 0, 1);
        view.Settle();
        return view;
    }

    /// A point low on the first book, clear of its label and bands (WPF coordinates).
    static Point FirstBook(WheelView view)
    {
        double total = new double[] { 40, 34, 44, 37, 42, 35, 39, 43 }.Sum(w => w + 5) - 5;
        return new Point((view.ViewSize.Width - total) / 2 + 20, view.ViewSize.Height - (30 + 8));
    }

    /// A point below the first note's label (WPF coordinates).
    static Point FirstNote(WheelView view)
    {
        double originX = (view.ViewSize.Width - 4 * 108) / 2 + 7, top = view.ViewSize.Height - WheelView.HeaderHeight - 10;
        return new Point(originX + 47, view.ViewSize.Height - (top - 108 + 7 + 47 - 30));
    }

    [STATestMethod]
    public void ShelfAndNotesDrawTheirOwnPalettes()
    {
        var notes = new TemplatePalette([new(0.15, 0.22, 0.35), new(0.25, 0.4, 0.38), new(0.93, 0.85, 0.85), new(0.88, 0.85, 0.93)], new(0.5, 0.5, 0.5));
        Prefs.TemplateColors = new TemplateColors().With(ColorTemplate.Shelf, TemplateColorPreset.Sage.Palette()!).With(ColorTemplate.Notes, notes);
        foreach (bool dark in new[] { false, true })
        {
            var shelf = StillMenu(MenuStyle.Shelf, dark);
            AssertClose(Prefs.TemplateColors.Shelf.Card(0, dark), Pixel(shelf, FirstBook(shelf)), $"book, dark {dark}");
            var board = StillMenu(MenuStyle.Notes, dark);
            AssertClose(notes.Card(0, dark), Pixel(board, FirstNote(board)), $"note, dark {dark}");
        }
        // The native palette is the quiet neutral surface.
        Prefs.TemplateColors = new TemplateColors();
        var plain = StillMenu(MenuStyle.Shelf, false);
        AssertClose(new MenuAppearance(false).Card(0, 0), Pixel(plain, FirstBook(plain)), "native book");
        // Dark custom notes get light text, light ones dark text.
        Assert.AreEqual(Colors.White, notes.Ink(0, false));
        Assert.AreNotEqual(Colors.White, notes.Ink(2, false));
    }

    [STATestMethod]
    public void ClotheslineTemplateUsesTheRopeColours()
    {
        Prefs.ClotheslineColors = new ClotheslineColors { Rope = RopeColorPreset.Rose.Rgb()!, Peg = RopeColorPreset.Graphite.Rgb()! };
        var view = StillMenu(MenuStyle.Clothesline, false);
        double w = view.ViewSize.Width, h = view.ViewSize.Height, x = 30;
        double t = (x - 22) / (w - 44);
        double ropeY = h - WheelView.HeaderHeight - 18 - 64 * t * (1 - t);
        AssertClose(RopeColorPreset.Rose.Rgb()!.Color, Pixel(view, new Point(x, Math.Floor(h - ropeY))), "rope", 8);
        // The peg of the first tag, above the rope.
        double tagX = (w - 8 * 82) / 2 + 41;
        double tagT = (tagX - 22) / (w - 44);
        double pegTop = h - WheelView.HeaderHeight - 18 - 64 * tagT * (1 - tagT) + 7;
        AssertClose(RopeColorPreset.Graphite.Rgb()!.Color, Pixel(view, new Point(tagX, h - (pegTop - 4))), "peg", 8);
    }

    [STATestMethod]
    public void MenusOnScreenRedrawWhenColoursChange()
    {
        var view = StillMenu(MenuStyle.Shelf, false);
        view.ManualClock = false;   // redraws itself, like a real menu
        // At the window's origin, so a bitmap of the view reads its own coordinates.
        view.HorizontalAlignment = HorizontalAlignment.Left;
        view.VerticalAlignment = VerticalAlignment.Top;
        var window = new Window
        {
            Width = 500, Height = 300, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            Left = -20000, Top = -20000, Content = view,
        };
        try
        {
            window.Show();
            PumpUntil(() => false, 0.2);
            var rose = TemplateColorPreset.Rose.Palette()!;
            Prefs.TemplateColors = new TemplateColors().With(ColorTemplate.Shelf, rose);
            PumpUntil(() => false, 0.2);
            // What is drawn now, without asking for a redraw.
            AssertClose(rose.Card(0, false), Read(view, FirstBook(view)), "redrawn book");
        }
        finally { window.Close(); }
    }

    [STATestMethod]
    public void MenuTextFollowsTheTextSize()
    {
        Prefs.Interaction = new InteractionSettings { TextSize = TextSize.ExtraLarge, BoldText = true };
        Assert.AreEqual(13 * 1.3, AppTypography.Size(13), 1e-9);
        Assert.AreEqual(FontWeights.SemiBold, AppTypography.Weight(FontWeights.Medium));
        Assert.AreEqual(FontWeights.Bold, AppTypography.Weight(FontWeights.Bold));
        // Every template still draws at the largest size (labels shrink to fit, then lose their middle).
        foreach (var style in MenuStyles.All)
        {
            var view = StillMenu(style, false);
            view.Hovered = 2;
            view.Settle();
            Pixel(view, new Point(1, 1));
        }
    }

    // MARK: Frames

    [STATestMethod]
    public void FramesToolRendersEveryFrame()
    {
        var dir = TestSetup.NewFolder("frames");
        var output = new StringWriter();
        var previous = Console.Out;
        Console.SetOut(output);
        int code;
        try { code = WheelFrames.Render("list", dir, "light").Result; }
        finally { Console.SetOut(previous); }
        Assert.AreEqual(0, code, output.ToString());
        StringAssert.StartsWith(output.ToString(), "OK draw: mean ");
        var files = Directory.GetFiles(dir, "*.png");
        Assert.HasCount(168, files);
        Assert.IsGreaterThan(1000L, new FileInfo(files.Order().Last()).Length);
        Assert.AreEqual(1, WheelFrames.Render("nope", dir, null).Result);
    }

    [TestMethod]
    public void BoxBlurKeepsAFlatColourFlat()
    {
        var px = new byte[8 * 6 * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = 10; px[i + 1] = 120; px[i + 2] = 240; }
        GlassBackdrop.BoxBlur(px, 8, 6, 3);
        for (int i = 0; i < px.Length; i += 4)
        {
            Assert.AreEqual(10, px[i]);
            Assert.AreEqual(120, px[i + 1]);
            Assert.AreEqual(240, px[i + 2]);
        }
    }
}
