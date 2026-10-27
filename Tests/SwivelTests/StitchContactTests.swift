import AppKit
import ImageIO
import Testing
import UniformTypeIdentifiers
@testable import Swivel

@Suite(.serialized)
@MainActor
struct StitchContactTests {
    private let source = URL(fileURLWithPath: "/tmp/contact-source.png")
    private let target = URL(fileURLWithPath: "/tmp/contact-target.png")
    private let output = URL(fileURLWithPath: "/tmp/contact-joined.png")

    private func screenshot(_ title: String, color: NSColor) throws -> CGImage {
        let ctx = try #require(CGContext(data: nil, width: 360, height: 240, bitsPerComponent: 8, bytesPerRow: 0,
                                        space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                        bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        ctx.setFillColor(NSColor(white: 0.96, alpha: 1).cgColor)
        ctx.fill(CGRect(x: 0, y: 0, width: 360, height: 240))
        ctx.setFillColor(color.cgColor); ctx.fill(CGRect(x: 0, y: 170, width: 360, height: 70))
        for i in 0..<4 {
            ctx.setFillColor(NSColor(white: 0.7 + CGFloat(i) * 0.045, alpha: 1).cgColor)
            ctx.fill(CGRect(x: 24, y: 130 - i * 29, width: 250 - i * 30, height: 9))
        }
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(cgContext: ctx, flipped: false)
        (title as NSString).draw(at: CGPoint(x: 24, y: 190), withAttributes: [
            .font: NSFont.systemFont(ofSize: 26, weight: .bold), .foregroundColor: NSColor.white])
        NSGraphicsContext.restoreGraphicsState()
        return try #require(ctx.makeImage())
    }

    private func scene() throws -> (RopeView, CALayer, CALayer, CGImage, CGImage) {
        let a = try screenshot("SCREENSHOT A", color: .systemBlue)
        let b = try screenshot("SCREENSHOT B", color: .systemOrange)
        let view = RopeView(frame: CGRect(x: 0, y: 0, width: 650, height: RopePanel.stitchHeight))
        view.reducedMotionOverride = false
        view.markNew(source, arrival: .inPlace, thumbnail: a)
        view.markNew(target, arrival: .inPlace, thumbnail: b)
        view.setItems([source, target]); view.drop(fromHidden: true)
        for _ in 0..<600 { _ = view.advance(1 / 120) }
        let photos = (view.layer?.sublayers ?? []).filter { $0.contents != nil }
        return (view, try #require(photos.first), try #require(photos.last), a, b)
    }

    @Test func pickingUpOrPassingBesideANeighbourNeverAttracts() throws {
        let (view, a, b, _, _) = try scene()
        let overlay = try #require(view.layer?.sublayers?.first?.sublayers?.first)
        for point in [CGPoint(x: a.frame.midX + 2, y: a.frame.midY),
                      CGPoint(x: a.frame.maxX + 4, y: a.frame.midY),
                      CGPoint(x: b.frame.midX, y: b.frame.midY),
                      CGPoint(x: b.frame.minX - 4, y: b.frame.maxY + 5)] {
            view.updateStitchTarget(source: source, point: point)
            for _ in 0..<100 { _ = view.advance(1 / 120) }
            #expect(view.stitchIntent.target == nil && overlay.opacity < 0.01)
            #expect(b.opacity > 0.99)
            view.commitStitchPreview()
            #expect(!view.isCommittingStitch)
        }
        let edge = CGPoint(x: b.frame.midX, y: b.frame.maxY + a.bounds.height / 2)
        view.updateStitchTarget(source: source, point: edge, now: CACurrentMediaTime() - 1)
        for _ in 0..<150 { _ = view.advance(1 / 120) }
        #expect(view.stitchIntent.target == target)
        #expect(view.stitchHintText == "Release to stitch · Originals kept")
        view.updateStitchTarget(source: source, point: CGPoint(x: b.frame.midX, y: b.frame.midY))
        #expect(view.stitchIntent.target == nil)
        view.updateStitchTarget(source: source, point: edge)
        #expect(view.stitchHintText == "Hold briefly to stitch")
        #expect(!view.stitchIntent.ready(at: CACurrentMediaTime()))
    }

    @Test func contactBuildsGraduallyInsteadOfJumpingIntoPlace() throws {
        let (view, a, b, _, _) = try scene()
        let edge = CGPoint(x: b.frame.midX, y: b.frame.maxY + a.bounds.height / 2 + 15)
        view.updateStitchTarget(source: source, point: edge)
        let overlay = try #require(view.layer?.sublayers?.first?.sublayers?.first)
        let top = try #require(overlay.sublayers?[1])
        let bottom = try #require(overlay.sublayers?[2])
        for _ in 0..<10 { _ = view.advance(1 / 120) }
        let earlyGap = bottom.frame.minY - top.frame.maxY
        for _ in 0..<230 { _ = view.advance(1 / 120) }
        #expect(earlyGap > 10)
        #expect(abs(bottom.frame.minY - top.frame.maxY) < 0.1)
        #expect(view.stitchIntent.target == target && !view.isCommittingStitch)
    }

    @Test func holdingOffCentreNeverAccumulatesSidewaysAttraction() throws {
        let (view, a, b, _, _) = try scene()
        let point = CGPoint(x: b.frame.midX + 25, y: b.frame.maxY + a.bounds.height / 2)
        for _ in 0..<240 {
            view.updateStitchTarget(source: source, point: point, now: 0)
            _ = view.advance(1 / 120)
        }
        let overlay = try #require(view.layer?.sublayers?.first?.sublayers?.first)
        let bottom = try #require(overlay.sublayers?[2])
        let shown = overlay.convert(bottom.frame, to: view.layer)
        #expect(abs(shown.midX - point.x) <= 8.01)
        #expect(shown.midX > b.frame.midX + 16) // does not keep walking to the target centre
        view.updateStitchTarget(source: source, point: CGPoint(x: point.x, y: b.frame.maxY - 1))
        #expect(view.stitchIntent.target == nil)
        view.commitStitchPreview()
        #expect(!view.isCommittingStitch)
    }

    @Test func photoPixelsStretchOnlyInTheJoiningBand() throws {
        let (view, a, b, _, _) = try scene()
        let near = CGPoint(x: b.frame.midX, y: b.frame.maxY + a.bounds.height / 2 + 15)
        view.updateStitchTarget(source: source, point: near)
        for _ in 0..<32 { _ = view.advance(1 / 120) }
        #expect(view.stitchIntent.target == target && !view.isCommittingStitch)
        let overlay = try #require(view.layer?.sublayers?.first?.sublayers?.first)
        let bodies = try #require(overlay.sublayers)
        let top = bodies[1], bottom = bodies[2]
        let photo = try #require(top.sublayers?.first)
        let flow = try #require(top.sublayers?.last)
        #expect(!flow.isHidden && flow.contents != nil)
        #expect(flow.frame.maxY > photo.frame.maxY + 5)
        #expect(photo.mask != nil) // edge pixels are exclusively drawn by the warped band
        let clip = try #require(photo.mask as? CAShapeLayer)
        #expect(clip.path?.contains(CGPoint(x: photo.bounds.midX, y: 10)) == true)
        #expect(clip.path?.contains(CGPoint(x: photo.bounds.midX, y: photo.bounds.maxY - 1)) == false)
        #expect(abs(top.frame.maxY - bottom.frame.minY) < 0.01) // shared physical neck, before release
        #expect(CATransform3DIsIdentity(top.transform) && CATransform3DIsIdentity(bottom.transform))
    }

    @Test func heldEdgeIsAlreadyOneContourAndReleaseDoesNotRejoin() throws {
        for reduced in [false, true] {
            let (view, a, b, _, _) = try scene()
            view.reducedMotionOverride = reduced
            let edge = CGPoint(x: b.frame.midX, y: b.frame.maxY + a.bounds.height / 2)
            view.updateStitchTarget(source: source, point: edge, now: CACurrentMediaTime() - 1)
            for _ in 0..<180 { _ = view.advance(1 / 120) }
            #expect(view.stitchIntent.target == target && !view.isCommittingStitch)
            #expect(view.stitchHintText == "Release to stitch · Originals kept")
            let overlay = try #require(view.layer?.sublayers?.first?.sublayers?.first)
            let bodies = try #require(overlay.sublayers)
            let top = bodies[1].frame, bottom = bodies[2].frame
            #expect(abs(top.maxY - bottom.minY) < 0.01)
            #expect(abs(top.midX - bottom.midX) < 0.01)
            #expect(top.size == b.bounds.size && bottom.size == a.bounds.size)
            #expect(CATransform3DIsIdentity(bodies[1].transform) && CATransform3DIsIdentity(bodies[2].transform))
            #expect((bodies[0] as? CAShapeLayer)?.path != nil)
            // A held seam is only a preview. Pulling away cancels without submitting work.
            view.updateStitchTarget(source: source, point: CGPoint(x: -100, y: 300))
            view.commitStitchPreview()
            #expect(!view.isCommittingStitch && view.stitchIntent.target == nil)
            // Release starts from the exact attached geometry; it never flashes back to separate bodies.
            view.updateStitchTarget(source: source, point: edge, now: CACurrentMediaTime() - 1)
            for _ in 0..<180 { _ = view.advance(1 / 120) }
            let beforeTop = bodies[1].frame, beforeBottom = bodies[2].frame
            view.commitStitchPreview()
            _ = view.advance(0)
            if !reduced {
                #expect(bodies[1].frame == beforeTop && bodies[2].frame == beforeBottom)
                #expect(bodies[3].opacity == 0)
            }
            #expect(view.isCommittingStitch)
            view.clearStitchTarget(force: true)
        }
    }

    @Test func aCursorJustBelowThePhotoCanMergeFromEveryGrabPosition() throws {
        for offset in [CGPoint.zero, CGPoint(x: -65, y: 50), CGPoint(x: 65, y: -50)] {
            let (view, _, b, _, _) = try scene()
            view.stitchGrabOffset = offset
            let cursor = CGPoint(x: b.frame.midX, y: b.frame.maxY + 12)
            view.updateStitchTarget(source: source, point: cursor, now: CACurrentMediaTime() - 1)
            for _ in 0..<240 { _ = view.advance(1 / 120) }
            #expect(view.stitchIntent.target == target && view.stitchIntent.ready(at: CACurrentMediaTime()))
            #expect(!view.isCommittingStitch)
            view.commitStitchPreview()
            #expect(view.isCommittingStitch)
            view.clearStitchTarget(force: true)
        }
    }

    @Test func aClippedNativeDragDoesNotDisableABelowCursorDrop() throws {
        let (_, _, _, first, _) = try scene()
        let context = try #require(CGContext(data: nil, width: 1000, height: 20, bitsPerComponent: 8, bytesPerRow: 0,
                                            space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        context.setFillColor(NSColor.systemOrange.cgColor)
        context.fill(CGRect(x: 0, y: 0, width: 1000, height: 20))
        let wide = try #require(context.makeImage())
        let view = RopeView(frame: CGRect(x: 0, y: 0, width: 650, height: RopePanel.stitchHeight))
        view.reducedMotionOverride = true
        view.markNew(source, arrival: .inPlace, thumbnail: first)
        view.markNew(target, arrival: .inPlace, thumbnail: wide)
        view.setItems([source, target]); view.drop(fromHidden: true)
        for _ in 0..<240 { _ = view.advance(1 / 120) }
        let photos = (view.layer?.sublayers ?? []).filter { $0.contents != nil }
        let a = try #require(photos.first), b = try #require(photos.last)
        view.stitchGrabOffset = CGPoint(x: 0, y: a.bounds.height / 2 - 2)
        let cursor = CGPoint(x: b.frame.midX, y: b.frame.maxY + 12)
        view.updateStitchTarget(source: source, point: cursor, now: CACurrentMediaTime() - 1)
        _ = view.advance(1 / 60)
        #expect(view.stitchIntent.target == target && view.stitchIntent.ready(at: CACurrentMediaTime()))
        let overlay = try #require(view.layer?.sublayers?.first?.sublayers?.first)
        #expect(overlay.opacity == 0 && b.opacity > 0.99) // leave the native image intact while it extends beyond the panel
        view.commitStitchPreview()
        _ = view.advance(1 / 60)
        #expect(view.isCommittingStitch)
        view.clearStitchTarget(force: true)
    }

    @Test func completedFileHasABoundedHandoffEvenWithoutDisplayFrames() async throws {
        let (view, _, b, _, _) = try scene()
        let centre = CGPoint(x: b.frame.midX, y: b.frame.maxY + b.bounds.height / 2)
        view.updateStitchTarget(source: source, point: centre)
        _ = view.advance(1 / 60)
        view.commitStitchPreview()
        var handoffs = 0
        view.onStitchHandoff = { handoffs += 1 }
        view.finishStitchPreview(output: output)
        view.finishStitchPreview() // the worker's defer must not erase a successful queued result
        #expect(view.isCommittingStitch && handoffs == 0)
        let deadline = Date().addingTimeInterval(1.8)
        while Date() < deadline && handoffs == 0 { try await Task.sleep(nanoseconds: 20_000_000) }
        #expect(handoffs == 1 && !view.isCommittingStitch)
    }

    @Test func downwardContactFitsExpandedPanelAndItsTopNeverMoves() throws {
        let (view, a, b, _, _) = try scene()
        let panel = RopePanel(rope: view)
        defer { panel.orderOut(nil) }
        let top = panel.frame.maxY
        panel.setStitchTracking(true)
        #expect(panel.frame.maxY == top)
        #expect(view.bounds.height == RopePanel.stitchHeight)
        view.isDraggingItem = true
        defer { view.isDraggingItem = false }
        view.drop(fromHidden: true)
        for _ in 0..<240 { _ = view.advance(1 / 120) }
        let below = CGPoint(x: b.frame.midX, y: b.frame.maxY + a.bounds.height / 2 + 15)
        view.updateStitchTarget(source: source, point: below)
        for _ in 0..<100 { _ = view.advance(1 / 120) }
        let overlay = try #require(view.layer?.sublayers?.first?.sublayers?.first)
        let bottom = try #require(overlay.sublayers?[2])
        let bodyOnRope = overlay.convert(bottom.frame, to: view.layer)
        #expect(bodyOnRope.maxY > RopePanel.height)
        #expect(bodyOnRope.maxY < view.bounds.height)
        #expect(bottom.opacity > 0.99 && view.stitchIntent.target == target)
        view.lift {}
        #expect(panel.frame.height == RopePanel.height && panel.frame.maxY == top)
    }

    @Test func renderContinuousContactFilms() throws {
        guard let path = ProcessInfo.processInfo.environment["SWIVEL_STITCH_CONTACT_FILMS"] else { return }
        let folder = URL(fileURLWithPath: path, isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        for below in [false, true] {
            let (view, a, b, first, second) = try scene()
            let url = folder.appendingPathComponent(below ? "contact-below.gif" : "contact-side.gif")
            let gif = try #require(CGImageDestinationCreateWithURL(url as CFURL, UTType.gif.identifier as CFString, 220, nil))
            CGImageDestinationSetProperties(gif, [kCGImagePropertyGIFDictionary: [kCGImagePropertyGIFLoopCount: 0]] as CFDictionary)
            let from = below ? CGPoint(x: b.frame.midX, y: b.frame.maxY + a.bounds.height / 2 + 24)
                : CGPoint(x: a.frame.midX, y: a.frame.midY)
            let to = CGPoint(x: b.frame.midX, y: b.frame.maxY + a.bounds.height / 2)
            let merged = try ImageStitch.render(top: second, bottom: first)
            if !below {
                _ = try Images.save(first, as: folder.appendingPathComponent("fixture-a.png"), type: .png, quality: 1)
                _ = try Images.save(second, as: folder.appendingPathComponent("fixture-b.png"), type: .png, quality: 1)
            }
            view.onStitchHandoff = { [weak view] in view?.setItems([output]) }
            for frame in 0..<220 {
                if frame >= 10 && frame < 132 {
                    let near = CGPoint(x: to.x, y: to.y + 15)
                    let approaching = frame < 62
                    let t = approaching ? min(CGFloat(frame - 10) / 24, 1) : min(CGFloat(frame - 62) / 30, 1)
                    let eased = t * t * (3 - 2 * t)
                    let start = approaching ? from : near, end = approaching ? near : to
                    view.updateStitchTarget(source: source, point: CGPoint(x: start.x + (end.x - start.x) * eased, y: start.y + (end.y - start.y) * eased), now: 0)
                }
                if frame == 132 { view.commitStitchPreview() }
                if frame == 133 {
                    view.markNew(output, arrival: .inPlace, thumbnail: merged)
                    view.finishStitchPreview(output: output)
                }
                _ = view.advance(1 / 50)
                let ctx = try #require(CGContext(data: nil, width: 650, height: Int(RopePanel.stitchHeight), bitsPerComponent: 8, bytesPerRow: 0,
                                                space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
                ctx.setFillColor(NSColor(white: 0.18, alpha: 1).cgColor); ctx.fill(view.bounds)
                ctx.translateBy(x: 0, y: view.bounds.height); ctx.scaleBy(x: 1, y: -1)
                view.layer?.render(in: ctx)
                let image = try #require(ctx.makeImage())
                CGImageDestinationAddImage(gif, image, [kCGImagePropertyGIFDictionary: [kCGImagePropertyGIFDelayTime: 0.02]] as CFDictionary)
                if [18, 36, 60, 90, 120, 132, 150, 180].contains(frame) {
                    _ = try Images.save(image, as: folder.appendingPathComponent("\(below ? "below" : "side")-\(frame).png"), type: .png, quality: 1)
                }
            }
            #expect(CGImageDestinationFinalize(gif))
        }
    }
}
