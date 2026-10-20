import AppKit
import Testing
@testable import Swivel

/// The editors' hand-feel maths: slider detents, crop hit-testing and snapping, ⇧ constraints, PDF crop mapping,
/// and latest-wins preview rendering.
@Suite(.serialized)
@MainActor
struct EditorsFeelTests {
    // MARK: Slider detent

    @Test func testDetentCatchesOnceAndHoldsWithHysteresis() {
        // Exposure: −2…2, neutral 0, so the zone is ±0.06 and it re-arms past ±0.12.
        var detent = Detent(neutral: 0, range: 4, at: 0.5)
        #expect(detent.filter(0.3) == (0.3, false))
        #expect(detent.filter(0.05) == (0, true))        // caught: snaps and taps
        #expect(detent.filter(0.02) == (0, false))       // held: no more taps
        #expect(detent.filter(0.1) == (0.1, false))      // out of the zone, but not re-armed yet
        #expect(detent.filter(0.04) == (0, false))       // back in without a second tap
        #expect(detent.filter(0.2) == (0.2, false))      // clearly away: re-armed
        #expect(detent.filter(-0.03) == (0, true))
    }

    @Test func testDetentTapsWhenAFastDragJumpsAcross() {
        var detent = Detent(neutral: 1, range: 1, at: 1.3)
        let (value, tick) = detent.filter(0.7)
        #expect(value == 0.7 && tick)
    }

    @Test func testDetentDoesNotTapWhenADragStartsOnIt() {
        var detent = Detent(neutral: 0.12, range: 0.4, at: 0.3)
        detent.begin(at: 0.12)
        #expect(detent.filter(0.123) == (0.12, false))
    }

    // MARK: Crop handles

    @Test func testCropHitPrefersOneEdgePerAxis() {
        let box = CGRect(x: 100, y: 100, width: 300, height: 200)
        #expect(CropHandle.hit(CGPoint(x: 105, y: 200), box: box, tolerance: 12) == .left)
        #expect(CropHandle.hit(CGPoint(x: 92, y: 95), box: box, tolerance: 12) == .topLeft)
        #expect(CropHandle.hit(CGPoint(x: 395, y: 298), box: box, tolerance: 12) == .bottomRight)
        #expect(CropHandle.hit(CGPoint(x: 250, y: 290), box: box, tolerance: 12) == .bottom)
        #expect(CropHandle.hit(CGPoint(x: 250, y: 200), box: box, tolerance: 12) == .move)
        #expect(CropHandle.hit(CGPoint(x: 80, y: 200), box: box, tolerance: 12) == nil)
    }

    @Test func testTinyCropBoxKeepsAMiddleToMoveBy() {
        // 20 pt box: a 12 pt inward reach would grab both sides at once (the old jitter bug).
        let box = CGRect(x: 100, y: 100, width: 20, height: 20)
        #expect(CropHandle.hit(CGPoint(x: 110, y: 110), box: box, tolerance: 12) == .move)
        #expect(CropHandle.hit(CGPoint(x: 103, y: 110), box: box, tolerance: 12) == .left)
        #expect(CropHandle.hit(CGPoint(x: 117, y: 110), box: box, tolerance: 12) == .right)
        #expect(CropHandle.hit(CGPoint(x: 90, y: 110), box: box, tolerance: 12) == .left)
        #expect(CropHandle.hit(CGPoint(x: 130, y: 130), box: box, tolerance: 12) == .bottomRight)
    }

    // MARK: Crop snapping

    @Test func testSnapPickHoldsTheCurrentTargetALittleLonger() {
        let candidates: [SnapPick.Candidate] = [.init(id: 1, delta: 15), .init(id: 2, delta: -10)]
        // Fresh: the nearest within reach.
        #expect(SnapPick.pick(candidates, previous: nil, enter: 12, leave: 18)?.id == 2)
        // Already holding 1 at 15 (inside 1.5 × reach): keep it, no flicker between targets.
        #expect(SnapPick.pick(candidates, previous: 1, enter: 12, leave: 18)?.id == 1)
        #expect(SnapPick.pick([.init(id: 1, delta: 20)], previous: 1, enter: 12, leave: 18) == nil)
    }

    @Test func testMovingBoxCatchesTheCentreLine() {
        let drag = CropDrag(start: CGRect(x: 100, y: 100, width: 900, height: 800), handle: .move,
                            size: CGSize(width: 3000, height: 2000), enter: 12)
        let result = drag.rect(dx: 946, dy: 0, snapping: true)
        #expect(result.rect.midX == 1500)
        #expect(result.snap.x == 12)
        // ⌘ (no snapping) tracks the pointer 1:1.
        #expect(drag.rect(dx: 946, dy: 0, snapping: false).rect.minX == 1046)
    }

    @Test func testFreeCornerCatchesTheOriginalRatio() {
        let drag = CropDrag(start: CGRect(x: 0, y: 0, width: 1200, height: 1000), handle: .bottomRight,
                            size: CGSize(width: 3000, height: 2000), freeRatios: [1.5, 1], enter: 12)
        let result = drag.rect(dx: 0, dy: -195, snapping: true)
        #expect(result.rect.height == 800)
        #expect(result.snap.y == 100)
    }

    @Test func testEdgeCatchesTheImageEdgeAndThirds() {
        let drag = CropDrag(start: CGRect(x: 0, y: 0, width: 2900, height: 1500), handle: .right,
                            size: CGSize(width: 3000, height: 2000), enter: 12)
        #expect(drag.rect(dx: 92, dy: 0, snapping: true).rect.maxX == 3000)
        let third = drag.rect(dx: -895, dy: 0, snapping: true)
        #expect(third.rect.maxX == 2000 && third.snap.x == 3)
    }

    @Test func testLockedCornerPinsTheOppositeCorner() {
        let size = CGSize(width: 1000, height: 1000)
        let start = CGRect(x: 100, y: 100, width: 400, height: 400)
        let grow = CropDrag(start: start, handle: .bottomRight, size: size, ratio: 1, enter: 6).rect(dx: 100, dy: 0, snapping: true)
        #expect(grow.rect == CGRect(x: 100, y: 100, width: 450, height: 450))
        // Dragged far past the image's corner: stops at the edge, bottom-right stays put.
        let far = CropDrag(start: start, handle: .topLeft, size: size, ratio: 1, enter: 6).rect(dx: -300, dy: -300, snapping: true)
        #expect(far.rect == CGRect(x: 0, y: 0, width: 500, height: 500))
    }

    // MARK: Annotate ⇧ constraints

    @Test func testShiftTurnsArrowsToFortyFiveDegrees() {
        let diagonal = MarkConstraint.snap45(from: .zero, to: CGPoint(x: 10, y: 9))
        #expect(diagonal?.direction == 1)
        #expect(abs((diagonal?.point.x ?? 0) - (diagonal?.point.y ?? 1)) < 1e-9)
        let flat = MarkConstraint.snap45(from: .zero, to: CGPoint(x: 10, y: 1))
        #expect(flat?.direction == 0 && abs((flat?.point.y ?? 1)) < 1e-9 && abs((flat?.point.x ?? 0) - 10) < 1e-9)
        #expect(MarkConstraint.snap45(from: .zero, to: CGPoint(x: -10, y: -1))?.direction == 4)
        #expect(MarkConstraint.snap45(from: .zero, to: .zero) == nil)
    }

    @Test func testShiftMakesSquaresAndClampsKeepTheDirection() {
        #expect(MarkConstraint.square(from: .zero, to: CGPoint(x: 10, y: -3)) == CGPoint(x: 10, y: -10))
        let rect = CGRect(x: 0, y: 0, width: 10, height: 10)
        #expect(MarkConstraint.clamp(CGPoint(x: 15, y: 5), from: CGPoint(x: 5, y: 5), in: rect) == CGPoint(x: 10, y: 5))
        #expect(MarkConstraint.clamp(CGPoint(x: 13, y: 13), from: CGPoint(x: 4, y: 4), in: rect) == CGPoint(x: 10, y: 10))
    }

    // MARK: PDF crop mapping

    @Test func testPDFCropMapsThroughPageRotation() {
        // The top-left half-width, quarter-height of the page as displayed.
        let unit = CGRect(x: 0, y: 0, width: 0.5, height: 0.25)
        let box = CGRect(x: 0, y: 0, width: 100, height: 200)
        #expect(CropEditor.pageRect(unit, rotation: 0, in: box) == CGRect(x: 0, y: 150, width: 50, height: 50))
        #expect(CropEditor.pageRect(unit, rotation: 90, in: box) == CGRect(x: 0, y: 0, width: 25, height: 100))
        #expect(CropEditor.pageRect(unit, rotation: 180, in: box) == CGRect(x: 50, y: 0, width: 50, height: 50))
        #expect(CropEditor.pageRect(unit, rotation: 270, in: box) == CGRect(x: 75, y: 100, width: 25, height: 100))
        #expect(CropEditor.pageRect(unit, rotation: -90, in: box) == CropEditor.pageRect(unit, rotation: 270, in: box))
    }

    @Test func testRoundingKeepsSnappedEdgesOnTheirLine() {
        let r = CGRect(x: 999.6, y: 10.2, width: 1000.2, height: 5.6).roundedToPixels
        #expect(r == CGRect(x: 1000, y: 10, width: 1000, height: 6))
    }

    // MARK: Live preview

    @Test func testLiveRenderSkipsToTheNewestRequest() async throws {
        var delivered: [Int] = []
        let render = LiveRender<Int> { delivered.append($0) }
        render.request {
            Thread.sleep(forTimeInterval: 0.08)
            return 1
        }
        render.request { 2 }
        render.request { 3 }
        for _ in 0..<50 where delivered.count < 2 { try await Task.sleep(for: .milliseconds(20)) }
        #expect(delivered == [1, 3])
        render.request { 4 }
        render.cancel()
        try await Task.sleep(for: .milliseconds(100))
        #expect(delivered == [1, 3])
    }
}
