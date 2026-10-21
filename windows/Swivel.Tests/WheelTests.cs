using System.Text;
using System.Windows;
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
        // Tests never make a sound.
        Prefs.SoundEffects = false;
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
