using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Swivel;

namespace Swivel.Tests;

/// The editors' hand-feel maths (port of EditorsFeelTests.swift): slider detents and end stops, crop hit-testing,
/// snapping and glide takeover, Shift constraints, PDF crop mapping, latest-wins preview rendering, pixel maths, and
/// what Apply may and may not save.
[TestClass]
public sealed class EditorTests
{
    [TestInitialize]
    public void Reset() => TestSetup.ResetPrefs();

    // MARK: Slider detent

    [TestMethod]
    public void DetentCatchesOnceAndHoldsWithHysteresis()
    {
        // Exposure: −2…2, neutral 0, so the zone is ±0.06 and it re-arms past ±0.12.
        var detent = new Detent(0, 4, 0.5);
        Assert.AreEqual((0.3, false), detent.Filter(0.3));
        Assert.AreEqual((0.0, true), detent.Filter(0.05));     // caught: snaps and taps
        Assert.AreEqual((0.0, false), detent.Filter(0.02));    // held: no more taps
        Assert.AreEqual((0.1, false), detent.Filter(0.1));     // out of the zone, but not re-armed yet
        Assert.AreEqual((0.0, false), detent.Filter(0.04));    // back in without a second tap
        Assert.AreEqual((0.2, false), detent.Filter(0.2));     // clearly away: re-armed
        Assert.AreEqual((0.0, true), detent.Filter(-0.03));
    }

    [TestMethod]
    public void DetentTapsWhenAFastDragJumpsAcross()
    {
        var detent = new Detent(1, 1, 1.3);
        var (value, tick) = detent.Filter(0.7);
        Assert.AreEqual(0.7, value);
        Assert.IsTrue(tick);
    }

    [TestMethod]
    public void DetentDoesNotTapWhenADragStartsOnIt()
    {
        var detent = new Detent(0.12, 0.4, 0.12);
        Assert.AreEqual((0.12, false), detent.Filter(0.123));
    }

    [TestMethod]
    public void TrimEndStopsLetTheHandlesReachEverySecond()
    {
        // A 60-minute clip. A 1.5 % band would pull everything within 54 s of either end onto it.
        var start = Detent.ForSlider(0, 0, 3600, 600);
        Assert.AreEqual(0.0, start.Zone);
        Assert.AreEqual((30.0, false), start.Filter(30));
        Assert.AreEqual((0.5, false), start.Filter(0.5));
        Assert.AreEqual((0.0, true), start.Filter(0));
        Assert.AreEqual((0.0, false), start.Filter(0));
        Assert.AreEqual((20.0, false), start.Filter(20));
        Assert.AreEqual((0.0, false), start.Filter(0));
        Assert.AreEqual((120.0, false), start.Filter(120));
        Assert.AreEqual((0.0, true), start.Filter(0));

        var end = Detent.ForSlider(3600, 0, 3600, 3600);
        Assert.AreEqual((3570.0, false), end.Filter(3570));
        Assert.AreEqual((3500.0, false), end.Filter(3500));
        Assert.AreEqual((3599.9, false), end.Filter(3599.9));
        Assert.AreEqual((3600.0, true), end.Filter(3600));
    }

    [TestMethod]
    public void OnlyDetentsInsideTheRangeAreMagnetic()
    {
        var exposure = Detent.ForSlider(0, -2, 2, 0.5);
        Assert.AreEqual(0.06, exposure.Zone, 1e-12);
        Assert.AreEqual((0.0, true), exposure.Filter(0.05));
        var vignette = Detent.ForSlider(0, 0, 2, 1);
        Assert.AreEqual((0.02, false), vignette.Filter(0.02));
        Assert.AreEqual((0.0, true), vignette.Filter(0));
    }

    [STATestMethod]
    public void DoubleClickResetsAndTrackingFlagsAreClear()
    {
        var slider = new DetentSlider { Minimum = 0, Maximum = 2, Value = 1.4, Neutral = 1 };
        Assert.IsFalse(slider.IsTracking);
        Assert.AreEqual(0.02, slider.SmallChange, 1e-12);  // arrow keys step by 1 % of the range
    }

    // MARK: Undo

    [TestMethod]
    public void HistoryUndoesRedoesAndLocks()
    {
        var history = new EditorHistory();
        int value = 0;
        void Set(int v)
        {
            int old = value;
            value = v;
            history.Register(() => Set(old));
        }
        Set(1);
        Set(2);
        history.Undo();
        Assert.AreEqual(1, value);
        history.Redo();
        Assert.AreEqual(2, value);
        history.Undo();
        history.Undo();
        Assert.AreEqual(0, value);
        Assert.IsFalse(history.CanUndo);
        history.Redo();
        Assert.AreEqual(1, value);
        history.IsLocked = true;
        Assert.IsFalse(history.CanUndo);
        Assert.IsFalse(history.CanRedo);
        history.Redo();
        Assert.AreEqual(1, value);
        history.IsLocked = false;
        Set(5);  // a new change drops the redo
        Assert.IsFalse(history.CanRedo);
    }

    // MARK: Crop handles

    [TestMethod]
    public void CropHitPrefersOneEdgePerAxis()
    {
        var box = new Rect(100, 100, 300, 200);
        Assert.AreEqual(CropHandle.Left, CropHandles.Hit(new Point(105, 200), box, 12));
        Assert.AreEqual(CropHandle.TopLeft, CropHandles.Hit(new Point(92, 95), box, 12));
        Assert.AreEqual(CropHandle.BottomRight, CropHandles.Hit(new Point(395, 298), box, 12));
        Assert.AreEqual(CropHandle.Bottom, CropHandles.Hit(new Point(250, 290), box, 12));
        Assert.AreEqual(CropHandle.Move, CropHandles.Hit(new Point(250, 200), box, 12));
        Assert.IsNull(CropHandles.Hit(new Point(80, 200), box, 12));
    }

    [TestMethod]
    public void TinyCropBoxKeepsAMiddleToMoveBy()
    {
        var box = new Rect(100, 100, 20, 20);
        Assert.AreEqual(CropHandle.Move, CropHandles.Hit(new Point(110, 110), box, 12));
        Assert.AreEqual(CropHandle.Left, CropHandles.Hit(new Point(103, 110), box, 12));
        Assert.AreEqual(CropHandle.Right, CropHandles.Hit(new Point(117, 110), box, 12));
        Assert.AreEqual(CropHandle.Left, CropHandles.Hit(new Point(90, 110), box, 12));
        Assert.AreEqual(CropHandle.BottomRight, CropHandles.Hit(new Point(130, 130), box, 12));
    }

    // MARK: Crop snapping

    [TestMethod]
    public void SnapPickHoldsTheCurrentTargetALittleLonger()
    {
        SnapPick.Candidate[] candidates = [new(1, 15), new(2, -10)];
        Assert.AreEqual(2, SnapPick.Pick(candidates, null, 12, 18)?.Id);
        Assert.AreEqual(1, SnapPick.Pick(candidates, 1, 12, 18)?.Id);
        Assert.IsNull(SnapPick.Pick([new(1, 20)], 1, 12, 18));
    }

    [TestMethod]
    public void MovingBoxCatchesTheCentreLine()
    {
        var drag = new CropDrag(new Rect(100, 100, 900, 800), CropHandle.Move, new Size(3000, 2000), enter: 12);
        var result = drag.Rect(946, 0, snapping: true);
        Assert.AreEqual(1500, result.Rect.X + result.Rect.Width / 2);
        Assert.AreEqual(12, result.Snap.X);
        // Ctrl (no snapping) tracks the pointer 1:1.
        Assert.AreEqual(1046, drag.Rect(946, 0, snapping: false).Rect.X);
    }

    [TestMethod]
    public void FreeCornerCatchesTheOriginalRatio()
    {
        var drag = new CropDrag(new Rect(0, 0, 1200, 1000), CropHandle.BottomRight, new Size(3000, 2000), enter: 12, freeRatios: [1.5, 1]);
        var result = drag.Rect(0, -195, snapping: true);
        Assert.AreEqual(800, result.Rect.Height);
        Assert.AreEqual(100, result.Snap.Y);
    }

    [TestMethod]
    public void EdgeCatchesTheImageEdgeAndThirds()
    {
        var drag = new CropDrag(new Rect(0, 0, 2900, 1500), CropHandle.Right, new Size(3000, 2000), enter: 12);
        Assert.AreEqual(3000, drag.Rect(92, 0, snapping: true).Rect.Right);
        var third = drag.Rect(-895, 0, snapping: true);
        Assert.AreEqual(2000, third.Rect.Right);
        Assert.AreEqual(3, third.Snap.X);
    }

    [TestMethod]
    public void LockedCornerPinsTheOppositeCorner()
    {
        var size = new Size(1000, 1000);
        var start = new Rect(100, 100, 400, 400);
        var grow = new CropDrag(start, CropHandle.BottomRight, size, enter: 6, ratio: 1).Rect(100, 0, snapping: true);
        Assert.AreEqual(new Rect(100, 100, 450, 450), grow.Rect);
        var far = new CropDrag(start, CropHandle.TopLeft, size, enter: 6, ratio: 1).Rect(-300, -300, snapping: true);
        Assert.AreEqual(new Rect(0, 0, 500, 500), far.Rect);
    }

    [TestMethod]
    public void PressDuringARatioGlideStartsFromTheRatio()
    {
        var size = new Size(3000, 2000);
        var midGlide = new Rect(250, 0, 2500, 2000);
        Assert.AreEqual(new Rect(500, 0, 2000, 2000), CropDrag.Fitted(midGlide, 1, CropHandle.Move, size));
        Assert.AreEqual(new Rect(750, 0, 2000, 2000), CropDrag.Fitted(midGlide, 1, CropHandle.Right, size));
        Assert.AreEqual(new Rect(250, 0, 2000, 2000), CropDrag.Fitted(midGlide, 1, CropHandle.TopLeft, size));
        var fitted = CropDrag.Fitted(midGlide, 1, CropHandle.BottomRight, size);
        var moved = new CropDrag(fitted, CropHandle.Move, size, enter: 6, ratio: 1).Rect(-100, 0, snapping: false).Rect;
        Assert.AreEqual(moved.Width, moved.Height);
        var corner = new CropDrag(fitted, CropHandle.BottomRight, size, enter: 6, ratio: 1).Rect(-300, -120, snapping: true).Rect;
        Assert.AreEqual(corner.Width, corner.Height);
        var wide = CropDrag.Fitted(new Rect(0, 100, 3000, 1800), 16.0 / 9, CropHandle.Move, size);
        Assert.AreEqual(3000, wide.Width);
        Assert.IsLessThan(1 / wide.Height, Math.Abs(wide.Width / wide.Height - 16.0 / 9));
    }

    [STATestMethod]
    public void GrabbingTheBoxMidGlideKeepsTheSelectedRatio()
    {
        var view = new CropView();
        view.SetImage(SolidImage(30, 20, Colors.Gray), new Size(3000, 2000));
        view.Measure(new Size(600, 400));
        view.Arrange(new Rect(0, 0, 600, 400));
        view.UpdateLayout();
        view.SetRatio(1);
        var centre = new Point(300, 200);
        Assert.IsTrue(view.PressAt(centre));
        view.DragTo(new Point(320, 200), System.Windows.Input.ModifierKeys.None);
        view.Release(new Point(320, 200));
        Assert.AreEqual(view.Crop.Width, view.Crop.Height);
        Assert.AreEqual(2000, view.Crop.Height);
        view.Stop();
    }

    [TestMethod]
    public void RoundingKeepsSnappedEdgesOnTheirLine()
    {
        var r = EditorGeometry.RoundedToPixels(new Rect(999.6, 10.2, 1000.2, 5.6));
        Assert.AreEqual(new Rect(1000, 10, 1000, 6), r);
    }

    // MARK: PDF crop mapping

    [TestMethod]
    public void PdfCropMapsThroughPageRotation()
    {
        // The top-left half-width, quarter-height of the page as displayed. Boxes are PDF boxes (bottom-left origin).
        var unit = new Rect(0, 0, 0.5, 0.25);
        var box = new Rect(0, 0, 100, 200);
        Assert.AreEqual(new Rect(0, 150, 50, 50), CropEditor.PageRect(unit, 0, box));
        Assert.AreEqual(new Rect(0, 0, 25, 100), CropEditor.PageRect(unit, 90, box));
        Assert.AreEqual(new Rect(50, 0, 50, 50), CropEditor.PageRect(unit, 180, box));
        Assert.AreEqual(new Rect(75, 100, 25, 100), CropEditor.PageRect(unit, 270, box));
        Assert.AreEqual(CropEditor.PageRect(unit, 270, box), CropEditor.PageRect(unit, -90, box));
    }

    // MARK: Annotate Shift constraints

    [TestMethod]
    public void ShiftTurnsArrowsToFortyFiveDegrees()
    {
        var diagonal = MarkConstraint.Snap45(new Point(0, 0), new Point(10, 9));
        Assert.AreEqual(1, diagonal?.Direction);
        Assert.AreEqual(diagonal!.Value.Point.X, diagonal.Value.Point.Y, 1e-9);
        var flat = MarkConstraint.Snap45(new Point(0, 0), new Point(10, 1))!.Value;
        Assert.AreEqual(0, flat.Direction);
        Assert.AreEqual(0, flat.Point.Y, 1e-9);
        Assert.AreEqual(10, flat.Point.X, 1e-9);
        Assert.AreEqual(4, MarkConstraint.Snap45(new Point(0, 0), new Point(-10, -1))?.Direction);
        Assert.AreEqual(6, MarkConstraint.Snap45(new Point(0, 0), new Point(1, -10))?.Direction);
        Assert.IsNull(MarkConstraint.Snap45(new Point(0, 0), new Point(0, 0)));
    }

    [TestMethod]
    public void ShiftMakesSquaresAndClampsKeepTheDirection()
    {
        Assert.AreEqual(new Point(10, -10), MarkConstraint.Square(new Point(0, 0), new Point(10, -3)));
        var rect = new Rect(0, 0, 10, 10);
        Assert.AreEqual(new Point(10, 5), MarkConstraint.Clamp(new Point(15, 5), new Point(5, 5), rect));
        Assert.AreEqual(new Point(10, 10), MarkConstraint.Clamp(new Point(13, 13), new Point(4, 4), rect));
    }

    [STATestMethod]
    public void AnnotateCanvasDrawsCommitsAndConstrains()
    {
        var canvas = new AnnotateCanvas { Picture = SolidImage(400, 200, Colors.White) };
        canvas.Measure(new Size(424, 224));
        canvas.Arrange(new Rect(0, 0, 424, 224));
        Mark? committed = null;
        canvas.MakeMark = p => new Mark(Mark.Kinds.Arrow, p, p, Colors.Red, 0.006);
        canvas.OnCommit = m => committed = m;
        canvas.Press(new Point(100, 100));
        canvas.DragTo(new Point(200, 110), shift: true);
        canvas.Release();
        Assert.IsNotNull(committed);
        // Shift held the arrow flat.
        Assert.AreEqual(committed.Start.Y, committed.End.Y, 1e-9);
        // A click without moving commits nothing for an arrow.
        committed = null;
        canvas.Press(new Point(100, 100));
        canvas.Release();
        Assert.IsNull(committed);
    }

    // MARK: Live preview

    [STATestMethod]
    public void LiveRenderSkipsToTheNewestRequest()
    {
        UseDispatcherContext();
        var delivered = new List<int>();
        var render = new LiveRender<int>(delivered.Add);
        render.Request(() =>
        {
            Thread.Sleep(80);
            return 1;
        });
        render.Request(() => 2);
        render.Request(() => 3);
        PumpUntil(() => delivered.Count >= 2, 3);
        CollectionAssert.AreEqual(new[] { 1, 3 }, delivered);
        render.Request(() => 4);
        render.Cancel();
        PumpFor(0.15);
        CollectionAssert.AreEqual(new[] { 1, 3 }, delivered);
    }

    // MARK: Pixel maths

    [TestMethod]
    public void NeutralAdjustmentsAreTheIdentity()
    {
        var pixels = Gradient(64, 48);
        CollectionAssert.AreEqual(pixels, AdjustMath.Apply(pixels, 64, 48, AdjustMath.Neutral));
    }

    [TestMethod]
    public void AdjustmentsMoveThePixelsTheRightWay()
    {
        var gray = Fill(16, 16, 128, 128, 128);
        // +1 EV doubles the light: 128 (0.216 linear) → 0.432 linear ≈ 174.
        var brighter = AdjustMath.Apply(gray, 16, 16, [1, 1, 1, 0, 0, 0]);
        Assert.AreEqual(174, brighter[2], 2);
        var color = Fill(16, 16, 40, 120, 200);  // B, G, R
        var gray0 = AdjustMath.Apply(color, 16, 16, [0, 1, 0, 0, 0, 0]);
        Assert.AreEqual(gray0[0], gray0[1], 1);
        Assert.AreEqual(gray0[1], gray0[2], 1);
        var punchy = AdjustMath.Apply(Fill(4, 4, 200, 200, 200), 4, 4, [0, 1.5, 1, 0, 0, 0]);
        Assert.IsGreaterThan(200, (int)punchy[0]);
        var warm = AdjustMath.Apply(gray, 16, 16, [0, 1, 1, 1, 0, 0]);
        Assert.IsGreaterThan((int)warm[0], (int)warm[2]);  // red above blue
        var vignette = AdjustMath.Apply(Fill(65, 65, 200, 200, 200), 65, 65, [0, 1, 1, 0, 2, 0]);
        Assert.AreEqual(200, vignette[(32 * 65 + 32) * 4], 1);  // centre untouched
        Assert.IsLessThan(150, (int)vignette[0]);               // corner darker
        // Sharpen raises the contrast across an edge.
        var edge = new byte[8 * 8 * 4];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                byte v = x < 4 ? (byte)100 : (byte)150;
                int i = (y * 8 + x) * 4;
                edge[i] = edge[i + 1] = edge[i + 2] = v;
                edge[i + 3] = 255;
            }
        var sharp = AdjustMath.Apply(edge, 8, 8, [0, 1, 1, 0, 0, 2]);
        Assert.IsLessThan(100, (int)sharp[(4 * 8 + 3) * 4]);
        Assert.IsGreaterThan(150, (int)sharp[(4 * 8 + 4) * 4]);
    }

    [TestMethod]
    public void RedactStylesCoverOnlyTheBoxes()
    {
        var source = Noise(100, 100);
        var box = new Rect(0.2, 0.2, 0.3, 0.3);
        var solid = (byte[])source.Clone();
        RedactMath.Apply(solid, 100, 100, [box], RedactMath.Style.Solid);
        Assert.AreEqual(0, solid[(30 * 100 + 30) * 4]);
        Assert.AreEqual(source[(80 * 100 + 80) * 4], solid[(80 * 100 + 80) * 4]);
        var blurred = (byte[])source.Clone();
        RedactMath.Apply(blurred, 100, 100, [box], RedactMath.Style.Blur);
        Assert.AreEqual(source[(80 * 100 + 80) * 4], blurred[(80 * 100 + 80) * 4]);
        Assert.AreNotEqual(source[(25 * 100 + 30) * 4 + 1], blurred[(25 * 100 + 30) * 4 + 1]);
        var blocky = (byte[])source.Clone();
        RedactMath.Apply(blocky, 100, 100, [new Rect(0, 0, 1, 1)], RedactMath.Style.Pixelate);
        // Cells are about 100 / 70 ≈ 1.4 px; pixels in the same cell share a colour, and the picture changed.
        Assert.IsFalse(blocky.SequenceEqual(source));
        Assert.AreEqual(new Int32Rect(20, 20, 30, 30), RedactMath.PixelRect(box, 100, 100));
    }

    // MARK: Layout maths

    [TestMethod]
    public void CollageCellsFitTheCanvas()
    {
        var sizes = Enumerable.Repeat(new Size(400, 300), 4).ToList();
        double gap = 0.02;
        var (grid, gridHeight) = CollageEditor.Cells(sizes, 0, gap);
        double side = (1 - gap * 3) / 2;
        Assert.HasCount(4, grid);
        Assert.AreEqual(gap + 2 * (side + gap), gridHeight, 1e-9);
        Assert.AreEqual(side, grid[0].Width, 1e-9);
        Assert.IsGreaterThan(grid[2].Y, grid[0].Y);  // y up: the first row is on top
        var (row, rowHeight) = CollageEditor.Cells(sizes, 1, gap);
        Assert.AreEqual(1 - gap, row[^1].Right, 1e-9);
        Assert.AreEqual(row[0].Height + gap * 2, rowHeight, 1e-9);
        var (column, columnHeight) = CollageEditor.Cells(sizes, 2, gap);
        Assert.AreEqual(columnHeight - gap, column[0].Bottom, 1e-9);
        var (featured, featuredHeight) = CollageEditor.Cells(sizes, 3, gap);
        Assert.AreEqual(1 - gap * 2, featured[0].Width, 1e-9);
        Assert.AreEqual(featuredHeight - gap, featured[0].Bottom, 1e-9);
        // Featured with one image is a grid of one.
        Assert.HasCount(1, CollageEditor.Cells(sizes.Take(1).ToList(), 3, gap).Frames);
    }

    [STATestMethod]
    public void CollageRendersAtTheLayoutsHeight()
    {
        var images = new[] { SolidImage(400, 300, Colors.Red), SolidImage(300, 400, Colors.Blue), SolidImage(300, 300, Colors.Green) };
        var look = new CollageEditor.Look(1, 0.02, 0.03, Colors.White);
        var result = CollageEditor.Render(images, 1200, look);
        var (_, unitHeight) = CollageEditor.Cells(images.Select(i => new Size(i.PixelWidth, i.PixelHeight)).ToList(), 1, 0.02);
        Assert.AreEqual(1200, result.PixelWidth);
        Assert.AreEqual((int)Math.Round(unitHeight * 1200), result.PixelHeight);
    }

    [STATestMethod]
    public void BackgroundPadsAndKeepsTheRatio()
    {
        var look = new BackgroundEditor.Look(0.1, 0.04, null, [Colors.Orange, Colors.Purple], true);
        Assert.AreEqual(new Size(1200, 800), BackgroundEditor.CanvasSize(1000, 600, look));
        Assert.AreEqual(new Size(1200, 1200), BackgroundEditor.CanvasSize(1000, 600, look with { Ratio = 1 }));
        var image = BackgroundEditor.Render(SolidImage(100, 60, Colors.Black), look with { Ratio = 16.0 / 9 });
        // 100 × 60 padded by 10 is 120 × 80; widened to 16:9 is 142 × 80.
        Assert.AreEqual(142, image.PixelWidth);
        Assert.AreEqual(80, image.PixelHeight);
    }

    // MARK: Metadata

    [TestMethod]
    public void MetadataNamesAndGroups()
    {
        var location = new MetadataEditor.Removal(true, false, false, false, false);
        Assert.AreEqual("GPS2", MetadataEditor.TagName("/{ushort=2}", inGps: true));
        Assert.AreEqual("Artist", MetadataEditor.TagName("/{ushort=315}", inGps: false));
        Assert.AreEqual("GPSLatitude", MetadataEditor.TagName("/exif:GPSLatitude", inGps: false));
        Assert.AreEqual("creator", MetadataEditor.TagName("/dc:creator", inGps: false));
        Assert.AreEqual("Byline", MetadataEditor.TagName("/By-line", inGps: false));
        Assert.IsTrue(MetadataEditor.ShouldRemove("GPSLatitude", location));
        Assert.IsFalse(MetadataEditor.ShouldRemove("Artist", location));
        Assert.IsFalse(MetadataEditor.ShouldRemove("Orientation", location with { Everything = true }));
        Assert.IsTrue(MetadataEditor.ShouldRemove("Make", location with { Camera = true }));
        Assert.IsTrue(MetadataEditor.ShouldRemove("DateTimeOriginal", location with { Date = true }));
        Assert.IsTrue(MetadataEditor.ShouldRemove("SubsecTimeOriginal", location with { Date = true }));
        Assert.IsTrue(MetadataEditor.ShouldRemove("creator", location with { Author = true }));
    }

    [STATestMethod]
    public void MetadataRemovesLocationWithoutTouchingThePixels()
    {
        var dir = TestSetup.NewFolder("metadata");
        var source = Path.Combine(dir, "photo.jpg");
        WriteJpegWithTags(source);
        var rows = MetadataEditor.Describe(source).ToDictionary(r => r.Name, r => r.Value);
        Assert.AreEqual("Swivel fixture", rows["Author"]);
        StringAssert.Contains(rows["Camera"], "Contoso");
        StringAssert.Contains(rows["Location"], "27.40000");

        var output = MetadataEditor.Clean(source, new MetadataEditor.Removal(true, false, false, false, false));
        Assert.AreEqual(Path.Combine(dir, "photo clean.jpg"), output);
        var after = MetadataEditor.Describe(output).ToDictionary(r => r.Name, r => r.Value);
        Assert.AreEqual("—", after["Location"]);
        Assert.AreEqual("Swivel fixture", after["Author"]);
        StringAssert.Contains(after["Camera"], "Contoso");
        // Rewritten in place: the compressed picture is byte-for-byte the same, so the decode is identical.
        CollectionAssert.AreEqual(DecodeBgra(source), DecodeBgra(output));

        var bare = MetadataEditor.Clean(source, new MetadataEditor.Removal(false, false, false, false, true));
        var none = MetadataEditor.Describe(bare).ToDictionary(r => r.Name, r => r.Value);
        Assert.AreEqual("—", none["Author"]);
        Assert.AreEqual("—", none["Camera"]);
        Assert.AreEqual("—", none["Location"]);
    }

    // MARK: Trim

    [TestMethod]
    public void TrimTimesAndPeaks()
    {
        Assert.AreEqual("0:00.0", TrimEditor.Time(0));
        Assert.AreEqual("1:05.4", TrimEditor.Time(65.47));
        Assert.AreEqual("0:00.0", TrimEditor.Time(-3));
        var wav = Wav(8000, 1.0, amplitude: 0.5);
        var peaks = TrimEditor.Peaks(wav, 10);
        Assert.HasCount(10, peaks);
        Assert.IsTrue(peaks.All(p => p > 0.9f));  // normalised to the loudest slice
    }

    [STATestMethod]
    public void TrimLearnsItsLengthAfterOpeningAndItsHandlesReachBothEnds()
    {
        UseDispatcherContext();
        var dir = TestSetup.NewFolder("trim");
        var path = Path.Combine(dir, "tone.wav");
        File.WriteAllBytes(path, Wav(44100, 2, amplitude: 0.3));
        var editor = new TrimEditor(path);
        try
        {
            Assert.AreEqual(0, editor.Duration);
            Run(() => editor.LoadForTesting(), 20);
            Assert.AreEqual(2, editor.Duration, 0.05);
            Assert.IsTrue(editor.IsAudio);
            StringAssert.StartsWith(editor.Window.Title, Loc.L("Trim Audio"));
            Assert.AreEqual(editor.Duration, editor.Start.Maximum);
            Assert.AreEqual(editor.Duration, editor.End.Maximum);
            Assert.AreEqual(0, editor.Start.Value);
            Assert.AreEqual(editor.Duration, editor.End.Value);
            var startStop = Detent.ForSlider(editor.Start.Neutral!.Value, editor.Start.Minimum, editor.Start.Maximum, 1);
            Assert.AreEqual((0.02, false), startStop.Filter(0.02));
            double nearEnd = editor.Duration - 0.02;
            var endStop = Detent.ForSlider(editor.End.Neutral!.Value, editor.End.Minimum, editor.End.Maximum, 1);
            Assert.AreEqual((nearEnd, false), endStop.Filter(nearEnd));
        }
        finally { editor.DidClose(); }
    }

    [STATestMethod]
    public void TrimWritesTheCutAsM4a()
    {
        UseDispatcherContext();
        var dir = TestSetup.NewFolder("trimout");
        var path = Path.Combine(dir, "tone.wav");
        File.WriteAllBytes(path, Wav(44100, 2, amplitude: 0.3));
        var files = Run(() => TrimEditor.Trim(path, 0.5, 1.5, audio: true), 60);
        Assert.AreEqual(Path.Combine(dir, "tone trimmed.m4a"), files[0]);
        var (seconds, audio) = Run(() => TrimEditor.ReadDuration(files[0]), 20);
        Assert.IsTrue(audio);
        Assert.AreEqual(1.0, seconds, 0.1);
        Assert.HasCount(2, Directory.GetFiles(dir));  // no temporary file left behind
    }

    [STATestMethod]
    public void TrimCutsVideoAndShowsAFilmstrip()
    {
        UseDispatcherContext();
        var dir = TestSetup.NewFolder("trimvideo");
        var path = Path.Combine(dir, "clip.mp4");
        Run(() => MakeVideo(path, 2), 120);
        var (seconds, audio) = Run(() => TrimEditor.ReadDuration(path), 20);
        Assert.IsFalse(audio);
        Assert.AreEqual(2, seconds, 0.1);
        var strip = Run(() => TrimEditor.Filmstrip(path, seconds, 4, 48), 60);
        Assert.HasCount(4, strip);
        Assert.AreEqual(48, strip[0].PixelHeight, 2);
        var files = Run(() => TrimEditor.Trim(path, 0.5, 1.5, audio: false), 120);
        Assert.AreEqual(Path.Combine(dir, "clip trimmed.mp4"), files[0]);
        var (cut, cutAudio) = Run(() => TrimEditor.ReadDuration(files[0]), 20);
        Assert.IsFalse(cutAudio);
        Assert.AreEqual(1.0, cut, 0.15);
        Assert.HasCount(2, Directory.GetFiles(dir));
    }

    /// A short solid-colour H.264 clip made with Windows.Media.Editing.
    static async Task MakeVideo(string path, double seconds)
    {
        var composition = new global::Windows.Media.Editing.MediaComposition();
        composition.Clips.Add(global::Windows.Media.Editing.MediaClip.CreateFromColor(global::Windows.UI.Color.FromArgb(255, 30, 120, 200), TimeSpan.FromSeconds(seconds)));
        File.WriteAllBytes(path, []);
        var file = await global::Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        var profile = global::Windows.Media.MediaProperties.MediaEncodingProfile.CreateMp4(global::Windows.Media.MediaProperties.VideoEncodingQuality.Vga);
        await composition.RenderToFileAsync(file, global::Windows.Media.Editing.MediaTrimmingPreference.Precise, profile);
    }

    // MARK: Saving

    [STATestMethod]
    public void SavingCoversTheCanvasAndHoldsUndo()
    {
        var dir = TestSetup.NewFolder("saving");
        var source = Path.Combine(dir, "photo.png");
        SavePng(SolidImage(80, 40, Colors.White), source);
        var editor = new AnnotateEditor(source);
        try
        {
            var content = editor.Content;
            content.Measure(new Size(540, 660));
            content.Arrange(new Rect(0, 0, 540, 660));
            content.UpdateLayout();
            var canvas = editor.Canvas;
            var centre = canvas.TranslatePoint(new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2), content);
            Assert.AreSame(canvas, HitTest(content, centre));

            int count = 0;
            editor.History.Register(() => count++);
            editor.SetSaving(true);
            content.UpdateLayout();
            Assert.AreNotSame(canvas, HitTest(content, centre));
            Assert.IsFalse(editor.History.CanUndo);
            editor.History.Undo();
            Assert.AreEqual(0, count);

            editor.SetSaving(false);
            content.UpdateLayout();
            Assert.AreSame(canvas, HitTest(content, centre), Describe(VisualTreeHelper.HitTest(content, centre)?.VisualHit));
            Assert.IsTrue(editor.History.CanUndo);
            editor.History.Undo();
            Assert.AreEqual(1, count);
        }
        finally
        {
            editor.DidClose();
            editor.Window.Close();
        }
    }

    // MARK: Editors that read files (need the Converters' Images.Load / Write)

    [STATestMethod]
    public void CompressEstimateQueuedBehindTheFirstReusesItsDecode()
    {
        UseDispatcherContext();
        var dir = TestSetup.NewFolder("compress");
        var source = Path.Combine(dir, "photo.png");
        SavePng(SolidImage(1600, 1200, Colors.SteelBlue), source);
        var editor = new CompressEditor(source);
        try
        {
            Run(() => editor.LoadForTesting(), 20);
            editor.UpdateForTesting();  // opening: the first estimate decodes the original
            editor.UpdateForTesting();  // a control moves while it runs: this one waits its turn
            PumpUntil(() => !editor.IsEstimating, 10);
            Assert.IsFalse(editor.IsEstimating);
            Assert.AreEqual(1, editor.Decoded.DecodeCount);
            StringAssert.Contains(editor.EstimateText, "→");
            var files = Run(() => editor.ApplyForTesting(), 20);
            // A PNG compresses to JPG by default (the first format offered), as on the Mac.
            Assert.AreEqual(Path.Combine(dir, "photo compressed.jpg"), files[0]);
        }
        finally { editor.DidClose(); }
    }

    [STATestMethod]
    public void RedactApplyWaitsForDetectionBeforeSaving()
    {
        UseDispatcherContext();
        var dir = TestSetup.NewFolder("redact");
        var source = Path.Combine(dir, "note.png");
        SavePng(TextImage("SECRET PASSWORD 1234"), source);
        var editor = new RedactEditor(source);
        try
        {
            Run(() => editor.LoadForTesting(), 20);
            Assert.AreEqual(RedactEditor.Detection.Running, editor.DetectionState);
            // Enter pressed straight away, before detection has reported anything.
            var files = Run(() => editor.ApplyForTesting(), 60);
            Assert.AreEqual(RedactEditor.Detection.Finished, editor.DetectionState);
            Assert.AreEqual(Path.Combine(dir, "note redacted.png"), files[0]);
            int before = DarkPixels(DecodeBgra(source)), after = DarkPixels(DecodeBgra(files[0]));
            Assert.IsGreaterThan(500, before);
            Assert.IsLessThan(before, after * 10);
        }
        finally { editor.DidClose(); }
    }

    [STATestMethod]
    public void RedactNeverSavesACopyThatCoversNothing()
    {
        UseDispatcherContext();
        var dir = TestSetup.NewFolder("redact-empty");
        var blank = Path.Combine(dir, "blank.png");
        SavePng(SolidImage(800, 400, Colors.White), blank);
        var editor = new RedactEditor(blank);
        try
        {
            Run(() => editor.LoadForTesting(), 20);
            Assert.ThrowsExactly<ToolError>(() => Run(() => editor.ApplyForTesting(), 60));
            Assert.AreEqual(RedactEditor.Detection.Finished, editor.DetectionState);
        }
        finally { editor.DidClose(); }

        // Detection cancelled before it could add its boxes (the window closing): nothing is written either.
        var note = Path.Combine(dir, "note.png");
        SavePng(TextImage("SECRET PASSWORD 1234"), note);
        var closing = new RedactEditor(note);
        Run(() => closing.LoadForTesting(), 20);
        closing.DidClose();
        Assert.ThrowsExactly<Cancelled>(() => Run(() => closing.ApplyForTesting(), 60));
        Assert.AreEqual(RedactEditor.Detection.Running, closing.DetectionState);
        CollectionAssert.AreEqual(new[] { "blank.png", "note.png" }, Directory.GetFiles(dir).Select(Path.GetFileName).Order().ToArray());
    }

    [STATestMethod]
    public void EditorsSaveWithTheMacSuffixes()
    {
        UseDispatcherContext();
        var dir = TestSetup.NewFolder("suffixes");
        var source = Path.Combine(dir, "photo.png");
        SavePng(TextImage("Swivel 3410"), source);

        var crop = new CropEditor(source);
        Run(() => crop.LoadForTesting(), 20);
        Assert.AreEqual(new Size(800, 400), crop.View.PixelSize);
        crop.View.SetRatio(1);
        var cropped = Run(() => crop.ApplyForTesting(), 20);
        Assert.AreEqual(Path.Combine(dir, "photo cropped.png"), cropped[0]);
        var square = BitmapDecoder.Create(new Uri(cropped[0]), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        Assert.AreEqual(400, square.PixelWidth);
        Assert.AreEqual(400, square.PixelHeight);
        crop.DidClose();

        var adjust = new AdjustEditor(source);
        Run(() => adjust.LoadForTesting(), 20);
        adjust.Sliders[0].Value = 0.5;
        Assert.AreEqual(Path.Combine(dir, "photo edited.png"), Run(() => adjust.ApplyForTesting(), 20)[0]);
        adjust.DidClose();

        var background = new BackgroundEditor(source);
        Run(() => background.LoadForTesting(), 20);
        Assert.AreEqual(Path.Combine(dir, "photo background.png"), Run(() => background.ApplyForTesting(), 20)[0]);
        background.DidClose();

        var annotate = new AnnotateEditor(source);
        Run(() => annotate.LoadForTesting(), 20);
        Assert.AreEqual(Path.Combine(dir, "photo annotated.png"), Run(() => annotate.ApplyForTesting(), 20)[0]);
        annotate.DidClose();

        var second = Path.Combine(dir, "second.png");
        SavePng(SolidImage(300, 500, Colors.Teal), second);
        var collage = new CollageEditor([source, second]);
        Run(() => collage.LoadForTesting(), 20);
        Assert.AreEqual(Path.Combine(dir, "Collage.png"), Run(() => collage.ApplyForTesting(), 30)[0]);
        collage.DidClose();
    }

    /// Lays out each editor offscreen and saves a picture of it (to look at, and to prove it builds without errors).
    [STATestMethod]
    public void EditorsLayOutOffscreen()
    {
        UseDispatcherContext();
        var dir = TestSetup.NewFolder("layout");
        var outDir = Environment.GetEnvironmentVariable("SWIVEL_EDITOR_SHOTS") is { Length: > 0 } custom ? custom : dir;
        Directory.CreateDirectory(outDir);
        var source = Path.Combine(dir, "photo.png");
        SavePng(TextImage("SECRET PASSWORD 1234"), source);
        var second = Path.Combine(dir, "second.png");
        SavePng(SolidImage(300, 500, Colors.Teal), second);
        var audio = Path.Combine(dir, "tone.wav");
        File.WriteAllBytes(audio, Wav(44100, 2, amplitude: 0.3));
        var editors = new (string Name, Func<Editor> Make)[]
        {
            ("crop", () => new CropEditor(source)),
            ("compress", () => new CompressEditor(source)),
            ("edit", () => new AdjustEditor(source)),
            ("bg", () => new BackgroundEditor(source)),
            ("redact", () => new RedactEditor(source)),
            ("metadata", () => new MetadataEditor(source)),
            ("annotate", () => new AnnotateEditor(source)),
            ("collage", () => new CollageEditor([source, second])),
            ("trim", () => new TrimEditor(audio)),
        };
        var failures = new List<string>();
        foreach (var (name, make) in editors)
        {
            Editor? editor = null;
            try
            {
                editor = make();
                editor.Content.Resources.MergedDictionaries.Add(FluentLight());
                try { Run(() => editor.LoadForTesting(), 30); } catch (Exception ex) { failures.Add($"{name} load: {ex.Message}"); }
                editor.UpdateForTesting();
                PumpFor(1.5);
                var root = editor.Content;
                var host = new Border { Child = null, Background = Brushes.White };
                root.Measure(new Size(540, 666));
                root.Arrange(new Rect(0, 0, 540, 666));
                root.UpdateLayout();
                var shot = Pixels.Render(root, 540, 666);
                var white = Pixels.Render(540, 666, dc =>
                {
                    dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 540, 666));
                    dc.DrawImage(shot, new Rect(0, 0, 540, 666));
                });
                SavePng(white, Path.Combine(outDir, $"editor-{name}.png"));
            }
            catch (Exception ex) { failures.Add($"{name}: {ex}"); }
            finally
            {
                editor?.DidClose();
                editor?.Window.Close();
            }
        }
        Assert.IsEmpty(failures, string.Join("\n", failures));
    }

    // MARK: Helpers

    static ResourceDictionary FluentLight()
    {
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        try { return new ResourceDictionary { Source = new Uri("pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.Light.xaml") }; }
        catch (Exception) { return new ResourceDictionary(); }
    }

    static object? HitTest(Visual root, Point p)
    {
        DependencyObject? hit = VisualTreeHelper.HitTest(root, p)?.VisualHit;
        while (hit != null && hit is not (AnnotateCanvas or Border { Background: SolidColorBrush { Color.A: 0 } })) hit = VisualTreeHelper.GetParent(hit);
        return hit;
    }

    static string Describe(DependencyObject? hit)
    {
        var parts = new List<string>();
        for (var d = hit; d != null; d = VisualTreeHelper.GetParent(d))
            parts.Add(d is FrameworkElement f ? $"{f.GetType().Name}({f.Visibility},{f.IsHitTestVisible},{f.ActualWidth:0})" : d.GetType().Name);
        return string.Join(" < ", parts);
    }

    static void UseDispatcherContext() =>
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

    /// Runs async work on this STA thread's dispatcher until it finishes.
    static T Run<T>(Func<Task<T>> work, double timeoutSeconds)
    {
        UseDispatcherContext();
        var frame = new DispatcherFrame();
        Task<T>? task = null;
        Dispatcher.CurrentDispatcher.BeginInvoke(() =>
        {
            task = work();
            task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        });
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(timeoutSeconds) };
        timer.Tick += (_, _) => frame.Continue = false;
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        if (task is null || !task.IsCompleted) throw new TimeoutException($"Timed out after {timeoutSeconds} s");
        return task.GetAwaiter().GetResult();
    }

    static void Run(Func<Task> work, double timeoutSeconds) => Run(async () => { await work(); return 0; }, timeoutSeconds);

    static void PumpUntil(Func<bool> condition, double timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (!condition() && DateTime.UtcNow < deadline) PumpFor(0.02);
    }

    static void PumpFor(double seconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    static BitmapSource SolidImage(int w, int h, Color color)
    {
        var pixels = new byte[w * h * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = color.B; pixels[i + 1] = color.G; pixels[i + 2] = color.R; pixels[i + 3] = 255;
        }
        return Pixels.FromBgra(pixels, w, h);
    }

    static BitmapSource TextImage(string text, int w = 800, int h = 400) => Pixels.Render(w, h, dc =>
    {
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 48, Brushes.Black, 1);
        dc.DrawText(formatted, new Point(30, h / 2 - 32));
    });

    static void SavePng(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    static byte[] DecodeBgra(string path)
    {
        var frame = BitmapDecoder.Create(new MemoryStream(File.ReadAllBytes(path)), BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames[0];
        return Pixels.Bgra(frame, out _, out _);
    }

    /// Pixels darker than mid-grey: black text, and none once it's covered.
    static int DarkPixels(byte[] bgra)
    {
        int count = 0;
        for (int i = 0; i < bgra.Length; i += 4) if (bgra[i] < 100 && bgra[i + 1] < 100 && bgra[i + 2] < 100) count++;
        return count;
    }

    static byte[] Fill(int w, int h, byte b, byte g, byte r)
    {
        var pixels = new byte[w * h * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = b; pixels[i + 1] = g; pixels[i + 2] = r; pixels[i + 3] = 255; }
        return pixels;
    }

    static byte[] Noise(int w, int h)
    {
        var random = new Random(7);
        var pixels = new byte[w * h * 4];
        random.NextBytes(pixels);
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        return pixels;
    }

    static byte[] Gradient(int w, int h)
    {
        var pixels = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                pixels[i] = (byte)(x * 255 / Math.Max(1, w - 1));
                pixels[i + 1] = (byte)(y * 255 / Math.Max(1, h - 1));
                pixels[i + 2] = (byte)((x + y) * 127 / Math.Max(1, w + h - 2));
                pixels[i + 3] = 255;
            }
        return pixels;
    }

    /// A 16-bit mono WAV of a 440 Hz tone.
    static byte[] Wav(int rate, double seconds, double amplitude)
    {
        int samples = (int)(rate * seconds);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + samples * 2);
        writer.Write("WAVEfmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(rate);
        writer.Write(rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8.ToArray());
        writer.Write(samples * 2);
        for (int i = 0; i < samples; i++) writer.Write((short)(Math.Sin(2 * Math.PI * 440 * i / rate) * amplitude * short.MaxValue));
        writer.Flush();
        return stream.ToArray();
    }

    /// A JPEG with camera, author and GPS tags, written with WIC directly (not through the app's converters).
    static void WriteJpegWithTags(string path)
    {
        var metadata = new BitmapMetadata("jpg");
        metadata.SetQuery("/app1/ifd/{ushort=271}", "Contoso");
        metadata.SetQuery("/app1/ifd/{ushort=272}", "Model 7");
        metadata.SetQuery("/app1/ifd/{ushort=315}", "Swivel fixture");
        metadata.SetQuery("/app1/ifd/gps/{ushort=1}", "S");
        metadata.SetQuery("/app1/ifd/gps/{ushort=2}", new ulong[] { Rational(27, 1), Rational(24, 1), Rational(0, 1) });
        metadata.SetQuery("/app1/ifd/gps/{ushort=3}", "E");
        metadata.SetQuery("/app1/ifd/gps/{ushort=4}", new ulong[] { Rational(153, 1), Rational(1, 1), Rational(30, 1) });
        var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(BitmapFrame.Create(TextImage("Metadata 42", 320, 200), null, metadata, null));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    static ulong Rational(uint numerator, uint denominator) => ((ulong)denominator << 32) | numerator;
}
