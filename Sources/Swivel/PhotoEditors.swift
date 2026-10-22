import AppKit
import CoreImage
import ImageIO
import UniformTypeIdentifiers
import Vision

private let ciContext = CIContext(options: [.useSoftwareRenderer: false])

/// Exposure, contrast, saturation, warmth, vignette and sharpness with a live preview.
final class AdjustEditor: Editor {
    private struct Adjustment {
        let slider: DetentSlider
        let label: NSTextField
        let neutral: Double
        let describe: (Double) -> String
    }

    private let preview = ImagePreview()
    private var small: CGImage?
    private var adjustments: [Adjustment] = []
    private lazy var renderer = LiveRender<CGImage?> { [weak self] image in
        if let image { self?.preview.image = image }
    }

    init(source: URL) throws {
        super.init(title: "Edit Photo", source: source, canvas: preview)
        let percent: (Double) -> String = { Self.signed(($0 - 1) * 100, decimals: 0) + "%" }
        let specs: [(String, Double, Double, Double, (Double) -> String)] = [
            ("Exposure", -2, 2, 0, { Self.signed($0, decimals: 1) + " EV" }),
            ("Contrast", 0.5, 1.5, 1, percent),
            ("Saturation", 0, 2, 1, percent),
            ("Warmth", -1, 1, 0, { Self.signed($0 * 100, decimals: 0) }),
            ("Vignette", 0, 2, 0, { "\(Int(($0 / 2 * 100).rounded()))%" }),
            ("Sharpen", 0, 2, 0, { "\(Int(($0 / 2 * 100).rounded()))%" }),
        ]
        for (name, min, max, neutral, describe) in specs {
            let control = slider(min, max, neutral)
            // The value beside each slider greys out at neutral: with the detent, that's how you find it again.
            let label = NSTextField(labelWithString: "")
            label.font = .monospacedDigitSystemFont(ofSize: 11, weight: .regular)
            label.alignment = .right
            label.widthAnchor.constraint(equalToConstant: 52).isActive = true
            adjustments.append(Adjustment(slider: control, label: label, neutral: neutral, describe: describe))
            addRow(name, control, label)
        }
    }

    /// "+0.7", "−0.3", or "0" at neutral, with a real minus sign.
    private static func signed(_ value: Double, decimals: Int) -> String {
        let text = String(format: "%.\(decimals)f", abs(value))
        if Double(text) == 0 { return "0" }
        return (value > 0 ? "+" : "−") + text
    }

    override func load() async throws {
        let source = self.source
        let image = try await Images.detached { try Images.load(source, maxPixels: 1400) }
        small = image
        preview.image = image
        preview.original = image
    }

    private nonisolated static func render(_ image: CGImage, _ v: [Double]) -> CGImage? {
        var ci = CIImage(cgImage: image)
        let extent = ci.extent
        func filter(_ name: String, _ params: [String: Any]) {
            guard let f = CIFilter(name: name) else { return }
            f.setValue(ci, forKey: kCIInputImageKey)
            for (k, value) in params { f.setValue(value, forKey: k) }
            if let out = f.outputImage { ci = out }
        }
        if v[0] != 0 { filter("CIExposureAdjust", [kCIInputEVKey: v[0]]) }
        if v[1] != 1 || v[2] != 1 { filter("CIColorControls", [kCIInputContrastKey: v[1], kCIInputSaturationKey: v[2]]) }
        if v[3] != 0 {
            filter("CITemperatureAndTint", ["inputNeutral": CIVector(x: 6500 + v[3] * 2500, y: 0),
                                            "inputTargetNeutral": CIVector(x: 6500, y: 0)])
        }
        if v[4] > 0 { filter("CIVignette", [kCIInputIntensityKey: v[4], kCIInputRadiusKey: 2]) }
        if v[5] > 0 { filter("CISharpenLuminance", [kCIInputSharpnessKey: v[5]]) }
        return ciContext.createCGImage(ci.cropped(to: extent), from: extent)
    }

    override func update() {
        let values = adjustments.map { $0.slider.doubleValue }
        for adjustment in adjustments {
            let value = adjustment.slider.doubleValue
            adjustment.label.stringValue = adjustment.describe(value)
            // Grey at neutral, so the detent reads at a glance too.
            adjustment.label.textColor = value == adjustment.neutral ? .tertiaryLabelColor : .secondaryLabelColor
        }
        guard let small else { return }
        renderer.request { Self.render(small, values) }
    }

    override func reset() { for adjustment in adjustments { adjustment.slider.doubleValue = adjustment.neutral } }

    override func didClose() { renderer.cancel() }

    override func apply() async throws -> [URL] {
        let values = adjustments.map { $0.slider.doubleValue }, source = self.source
        return try await Images.detached {
            guard let result = Self.render(try Images.load(source), values) else { throw ToolError("Couldn't render") }
            let (type, ext) = Images.writableType(for: source)
            return [try Images.save(result, as: Paths.preferred(for: source, ext: ext, suffix: " edited"), type: type, quality: 0.95)]
        }
    }
}

/// Places the photo on a colour or gradient backdrop with padding, rounded corners and a shadow.
final class BackgroundEditor: Editor {
    private struct Look: Sendable {
        var padding: Double
        var radius: Double
        var ratio: CGFloat?
        var colors: [CGColor]
        var shadow: Bool
    }

    private let preview = ImagePreview()
    private var small: CGImage?
    private let styles: [(String, [NSColor])] = [
        ("Sunset", [NSColor(srgbRed: 1, green: 0.55, blue: 0.3, alpha: 1), NSColor(srgbRed: 0.93, green: 0.3, blue: 0.45, alpha: 1)]),
        ("Ocean", [NSColor(srgbRed: 0.25, green: 0.6, blue: 0.95, alpha: 1), NSColor(srgbRed: 0.35, green: 0.3, blue: 0.85, alpha: 1)]),
        ("Mint", [NSColor(srgbRed: 0.6, green: 0.95, blue: 0.8, alpha: 1), NSColor(srgbRed: 0.2, green: 0.7, blue: 0.65, alpha: 1)]),
        ("Candy", [NSColor(srgbRed: 1, green: 0.75, blue: 0.85, alpha: 1), NSColor(srgbRed: 0.75, green: 0.6, blue: 1, alpha: 1)]),
        ("White", [.white]),
        ("Black", [.black]),
        ("Custom", []),
    ]
    private let aspects: [(String, CGFloat?)] = [("Auto", nil), ("1:1", 1), ("4:5", 0.8), ("16:9", 16 / 9),
                                                  ("9:16", 9 / 16), ("4:3", 4 / 3)]
    private var style: NSPopUpButton!
    private let colorWell = NSColorWell(style: .minimal)
    private var padding: NSSlider!
    private var radius: NSSlider!
    private var shadow: NSButton!
    private var aspect: NSPopUpButton!
    private var lastAspect = 0
    private lazy var renderer = LiveRender<CGImage?> { [weak self] image in self?.show(image) }

    init(source: URL) throws {
        super.init(title: "Add Background", source: source, canvas: preview)
        style = popup(styles.map(\.0))
        colorWell.color = .systemTeal
        colorWell.target = self
        colorWell.action = #selector(controlChanged)
        addRow("Background", style, colorWell)
        padding = slider(0, 0.4, 0.12)
        addRow("Spacing", padding)
        radius = slider(0, 0.2, 0.04)
        addRow("Corners", radius)
        aspect = popup(aspects.map(\.0))
        addRow("Aspect ratio", aspect)
        shadow = checkbox("Drop shadow", true)
        addRow("", shadow)
    }

    private var look: Look {
        var colors = styles[style.indexOfSelectedItem].1
        if colors.isEmpty { colors = [colorWell.color] }
        return Look(padding: padding.doubleValue, radius: radius.doubleValue, ratio: aspects[aspect.indexOfSelectedItem].1,
                    colors: colors.map(\.cgColor), shadow: shadow.state == .on)
    }

    override func load() async throws {
        // Decode and compose the first preview together, so the window opens on the finished picture.
        let source = self.source, look = self.look
        let (image, first) = try await Images.detached { () throws -> (CGImage, CGImage?) in
            let image = try Images.load(source, maxPixels: 1000)
            return (image, Self.render(image, look))
        }
        small = image
        preview.image = first
    }

    private nonisolated static func render(_ image: CGImage, _ look: Look) -> CGImage? {
        let w = CGFloat(image.width), h = CGFloat(image.height)
        let pad = look.padding * max(w, h)
        var canvas = CGSize(width: w + pad * 2, height: h + pad * 2)
        if let ratio = look.ratio {
            if canvas.width / canvas.height > ratio { canvas.height = canvas.width / ratio }
            else { canvas.width = canvas.height * ratio }
        }
        guard let ctx = CGContext(data: nil, width: Int(canvas.width), height: Int(canvas.height), bitsPerComponent: 8,
                                  bytesPerRow: 0, space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        let full = CGRect(origin: .zero, size: canvas)
        if look.colors.count == 1 {
            ctx.setFillColor(look.colors[0])
            ctx.fill(full)
        } else if let gradient = CGGradient(colorsSpace: CGColorSpace(name: CGColorSpace.sRGB),
                                            colors: look.colors as CFArray, locations: nil) {
            ctx.drawLinearGradient(gradient, start: CGPoint(x: 0, y: canvas.height), end: CGPoint(x: canvas.width, y: 0),
                                   options: [])
        }

        let frame = CGRect(x: (canvas.width - w) / 2, y: (canvas.height - h) / 2, width: w, height: h)
        let corner = look.radius * min(w, h)
        let path = CGPath(roundedRect: frame, cornerWidth: corner, cornerHeight: corner, transform: nil)
        if look.shadow {
            ctx.saveGState()
            ctx.setShadow(offset: CGSize(width: 0, height: -max(w, h) * 0.012), blur: max(w, h) * 0.04,
                          color: CGColor(gray: 0, alpha: 0.4))
            ctx.addPath(path)
            ctx.setFillColor(.white)
            ctx.fillPath()
            ctx.restoreGState()
        }
        ctx.addPath(path)
        ctx.clip()
        ctx.interpolationQuality = .high
        ctx.draw(image, in: frame)
        return ctx.makeImage()
    }

    private var fadeNext = false

    private func show(_ image: CGImage?) {
        guard let image else { return }
        preview.setImage(image, fade: fadeNext)
        fadeNext = false
    }

    override func update() {
        colorWell.isHidden = style.indexOfSelectedItem != styles.count - 1
        // A new aspect reshapes the whole picture: crossfade it rather than cut.
        if aspect.indexOfSelectedItem != lastAspect {
            lastAspect = aspect.indexOfSelectedItem
            fadeNext = true
        }
        guard let small else { return }
        let look = self.look
        renderer.request { Self.render(small, look) }
    }

    override func reset() {
        style.selectItem(at: 0)
        padding.doubleValue = 0.12
        radius.doubleValue = 0.04
        aspect.selectItem(at: 0)
        shadow.state = .on
    }

    override func didClose() { renderer.cancel() }

    override func apply() async throws -> [URL] {
        let look = self.look, source = self.source
        return try await Images.detached {
            guard let result = Self.render(try Images.load(source), look) else { throw ToolError("Couldn't render") }
            return [try Images.save(result, as: Paths.preferred(for: source, ext: "png", suffix: " background"), type: .png, quality: 1)]
        }
    }
}

/// Finds faces and text on-device and covers them; drag on the photo to add a box, click a box to remove it.
/// Every change can be undone (⌘Z), Reset included.
///
/// Apply waits for detection to finish (under its Saving… state), so a quick Return can't save the photo before the
/// faces and text are found. It never writes a "redacted" file that covers nothing, and a detection that was cancelled
/// saves nothing at all.
final class RedactEditor: Editor {
    private enum Kind: Sendable { case face, text, manual }
    private struct Box: Equatable, Sendable { var rect: CGRect; var kind: Kind }  // normalized, bottom-left origin (Vision space)
    private struct Snapshot { var boxes: [Box]; var faces: Bool; var text: Bool; var style: Int }
    enum Detection: Equatable { case running, finished, failed }

    private let preview = RedactCanvas()
    private var small: CGImage?
    private var boxes: [Box] = []
    /// What detection found, which Reset returns to.
    private var detected: [Box] = []
    private var faces: NSButton!
    private var text: NSButton!
    private var style: NSSegmentedControl!
    private let status = NSTextField(labelWithString: L("Looking for faces and text…"))
    private let spinner = NSProgressIndicator()
    private var detection: Task<Void, Never>?
    /// Stays `.running` if the detection task was cancelled before it could add what it found.
    private(set) var detectionState = Detection.running
    private lazy var renderer = LiveRender<CGImage?> { [weak self] image in
        if let image { self?.preview.image = image }
    }

    init(source: URL) throws {
        super.init(title: "Redact Photo", source: source, canvas: preview)
        faces = checkbox("Faces", true)
        text = checkbox("Text", true)
        addRow("Detect", faces, text)
        style = segments(["Blur", "Pixelate", "Solid"])
        addRow("Style", style)
        status.textColor = .secondaryLabelColor
        spinner.style = .spinning
        spinner.controlSize = .small
        addRow("", spinner, status)

        preview.onAdd = { [weak self] rect in
            self?.change { $0.boxes.append(Box(rect: rect, kind: .manual)) }
        }
        preview.onRemove = { [weak self] index in
            guard let self, self.active.indices.contains(index) else { return }
            let target = self.active[index]
            self.change { editor in
                if let at = editor.boxes.firstIndex(of: target) { editor.boxes.remove(at: at) }
            }
        }
    }

    override func load() async throws {
        let source = self.source
        let image = try await Images.detached { try Images.load(source, maxPixels: 1400) }
        small = image
        spinner.startAnimation(nil)
        detection = Task { @MainActor [weak self] in
            let found = await Task.detached { Self.detect(image) }.value
            guard let self, !Task.isCancelled else { return }
            self.spinner.stopAnimation(nil)
            self.spinner.isHidden = true
            guard let found else {
                // Say so rather than "found 0": the photo may well have faces, and nothing covers them yet.
                self.detectionState = .failed
                self.status.stringValue = L("Couldn't look for faces and text. Drag on the photo to cover areas yourself.")
                self.status.textColor = .systemRed
                return
            }
            self.detected = found
            self.boxes += found
            self.detectionState = .finished
            // Undoing an edit made before detection finished would also drop what it found: start the history here.
            self.window.undoManager?.removeAllActions(withTarget: self)
            let f = found.filter { $0.kind == .face }.count, t = found.filter { $0.kind == .text }.count
            self.status.stringValue = L("Found %d faces and %d text areas. Drag to add a box, click one to remove it.", f, t)
            self.preview.revealOutlines()
            self.update()
        }
    }

    /// Faces and text in Vision's normalized space, or nil if Vision failed (as opposed to finding nothing).
    private nonisolated static func detect(_ image: CGImage) -> [Box]? {
        let faceRequest = VNDetectFaceRectanglesRequest()
        let textRequest = VNRecognizeTextRequest()
        textRequest.recognitionLevel = .fast
        do {
            try VNImageRequestHandler(cgImage: image).perform([faceRequest, textRequest])
        } catch {
            return nil
        }
        guard let faceResults = faceRequest.results, let textResults = textRequest.results else { return nil }
        let faces = faceResults.map { Box(rect: $0.boundingBox.insetBy(dx: -0.01, dy: -0.01), kind: .face) }
        let text = textResults.map { Box(rect: $0.boundingBox.insetBy(dx: -0.003, dy: -0.004), kind: .text) }
        return faces + text
    }

    private var active: [Box] {
        boxes.filter {
            switch $0.kind {
            case .face: faces.state == .on
            case .text: text.state == .on
            case .manual: true
            }
        }
    }

    // MARK: Undo

    private var snapshot: Snapshot {
        Snapshot(boxes: boxes, faces: faces.state == .on, text: text.state == .on, style: style.selectedSegment)
    }

    /// Makes a change that ⌘Z can take back.
    private func change(_ mutate: (RedactEditor) -> Void) {
        let before = snapshot
        mutate(self)
        registerUndo(returningTo: before)
        update()
        noteEdited()
    }

    private func registerUndo(returningTo state: Snapshot) {
        window.undoManager?.registerUndo(withTarget: self) { editor in
            MainActor.assumeIsolated { editor.restore(state) }
        }
    }

    private func restore(_ state: Snapshot) {
        let current = snapshot
        boxes = state.boxes
        faces.state = state.faces ? .on : .off
        text.state = state.text ? .on : .off
        style.selectedSegment = state.style
        registerUndo(returningTo: current)  // registered while undoing, so it becomes the redo
        update()
        noteEdited()
    }

    override var hasUnsavedWork: Bool { boxes != detected }

    // MARK: Rendering

    nonisolated static func render(_ image: CGImage, rects normalized: [CGRect], style: Int) -> CGImage? {
        let w = CGFloat(image.width), h = CGFloat(image.height)
        let rects = normalized.map { CGRect(x: $0.minX * w, y: $0.minY * h, width: $0.width * w, height: $0.height * h) }
        guard !rects.isEmpty else { return image }

        if style == 2 {
            guard let ctx = CGContext(data: nil, width: image.width, height: image.height, bitsPerComponent: 8,
                                      bytesPerRow: 0, space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                      bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
            ctx.draw(image, in: CGRect(x: 0, y: 0, width: w, height: h))
            ctx.setFillColor(.black)
            ctx.fill(rects)
            return ctx.makeImage()
        }

        // Each box gets an effect sized to the box, not to the photo: a large heading or a close-up face would
        // otherwise stay readable through a blur or mosaic tuned for small text. Solid black is the only style that
        // removes the content entirely.
        let original = CIImage(cgImage: image)
        var output = original
        for rect in rects {
            let shortSide = min(rect.width, rect.height)
            let effect: CIImage
            if style == 0 {
                let sigma = max(max(w, h) * 0.02, shortSide / 3)
                effect = original.clampedToExtent().applyingGaussianBlur(sigma: sigma)
            } else {
                guard let pixellate = CIFilter(name: "CIPixellate") else { return nil }
                pixellate.setValue(original.clampedToExtent(), forKey: kCIInputImageKey)
                pixellate.setValue(max(max(w, h) / 70, shortSide / 2.5), forKey: kCIInputScaleKey)
                pixellate.setValue(CIVector(x: rect.minX, y: rect.minY), forKey: kCIInputCenterKey)
                guard let mosaic = pixellate.outputImage else { return nil }
                effect = mosaic
            }
            output = effect.cropped(to: rect).composited(over: output)
        }
        return ciContext.createCGImage(output, from: original.extent)
    }

    override func update() {
        guard let small else { return }
        let rects = active.map(\.rect), style = self.style.selectedSegment
        preview.outlines = rects
        if preview.image == nil { preview.image = small }
        renderer.request { Self.render(small, rects: rects, style: style) }
    }

    override func reset() {
        change { editor in
            editor.boxes = editor.detected
            editor.faces.state = .on
            editor.text.state = .on
            editor.style.selectedSegment = 0
        }
    }

    override func didClose() {
        detection?.cancel()
        renderer.cancel()
        preview.stop()
    }

    override func apply() async throws -> [URL] {
        // Detection may still be running (Return pressed straight away): what it finds has to be in the file, so
        // wait for it here, under Apply's Saving… state, before reading the boxes.
        await detection?.value
        // Cancelled before it added its boxes (the window is closing): never write a file that may show the faces.
        guard detectionState != .running else { throw Cancelled() }
        let rects = active.map(\.rect), style = self.style.selectedSegment, source = self.source
        // Nothing would be covered, so a "… redacted" copy would be the original under a name that says otherwise.
        guard !rects.isEmpty else { throw ToolError(L("Nothing to redact. Drag on the photo to cover an area.")) }
        return try await Images.detached {
            guard let result = Self.render(try Images.load(source), rects: rects, style: style) else { throw ToolError("Couldn't render") }
            let (type, ext) = Images.writableType(for: source)
            return [try Images.save(result, as: Paths.preferred(for: source, ext: ext, suffix: " redacted"), type: type, quality: 0.95)]
        }
    }
}

/// Image preview that outlines redaction boxes and lets you draw or remove them. Rects are normalized, bottom-left origin.
///
/// The box under the pointer lights up with a × badge (and a pointing hand); elsewhere on the photo the crosshair
/// says "drag to add". A box is removed only by a click that starts and ends on it without moving, so a shaky
/// press meant to draw never un-redacts anything.
final class RedactCanvas: NSView {
    var image: CGImage? {
        didSet {
            picture.image = image
            needsLayout = true
        }
    }
    var outlines: [CGRect] = [] {
        didSet {
            if let hovered, hovered >= outlines.count { self.hovered = nil }
            overlay.needsDisplay = true
        }
    }
    var onAdd: ((CGRect) -> Void)?
    /// Index into `outlines`.
    var onRemove: ((Int) -> Void)?

    private let picture = PictureView()
    private let overlay = OverlayView()
    private var dragStart: NSPoint?
    private var dragRect: NSRect?
    private var pressedBox: Int?
    private var hovered: Int?
    private var hoverAmount = Spring(0, response: 0.12, damping: 1)
    private var pressAmount = Spring(0, response: 0.1, damping: 1)
    private var reveal = Spring(1, response: 0.2, damping: 1)
    private lazy var driver: FrameDriver = {
        let driver = FrameDriver(view: self)
        driver.onFrame = { [weak self] dt in self?.advance(dt) ?? false }
        return driver
    }()
    private static let badgeRadius: CGFloat = 8

    override init(frame: NSRect) {
        super.init(frame: frame)
        wantsLayer = true
        overlay.drawer = { [unowned self] in self.drawOverlay() }
        addSubview(picture)
        addSubview(overlay)
        addTrackingArea(NSTrackingArea(rect: .zero, options: [.mouseMoved, .mouseEnteredAndExited, .cursorUpdate,
                                                              .activeInKeyWindow, .inVisibleRect], owner: self))
    }

    required init?(coder: NSCoder) { fatalError() }

    private var imageRect: NSRect {
        guard let image else { return .zero }
        return fit(CGSize(width: image.width, height: image.height), in: bounds.insetBy(dx: 12, dy: 12))
    }

    override func layout() {
        super.layout()
        picture.frame = imageRect
        overlay.frame = bounds
    }

    private func toView(_ r: CGRect) -> NSRect {
        let i = imageRect
        return NSRect(x: i.minX + r.minX * i.width, y: i.minY + r.minY * i.height, width: r.width * i.width, height: r.height * i.height)
    }

    private func toNormalized(_ p: NSPoint) -> CGPoint {
        let i = imageRect
        return CGPoint(x: (p.x - i.minX) / i.width, y: (p.y - i.minY) / i.height)
    }

    /// The hovered box is lit (not just fading out), so its × badge is live.
    private var badgeLive: Bool { hovered != nil && hoverAmount.target > 0.5 }

    /// The topmost box at a point, counting the hovered box's × badge as part of it.
    private func box(at p: NSPoint) -> Int? {
        if badgeLive, let hovered, outlines.indices.contains(hovered) {
            let r = toView(outlines[hovered])
            if hypot(p.x - r.maxX, p.y - r.maxY) <= Self.badgeRadius + 2 { return hovered }
        }
        return outlines.indices.last { toView(outlines[$0]).contains(p) }
    }

    /// Detection finished: the outlines fade in rather than appear in one frame.
    func revealOutlines() {
        reveal.snap(Motion.reduced ? 1 : 0)
        reveal.target = 1
        kick()
    }

    private func kick() {
        overlay.needsDisplay = true
        driver.start()
    }

    private func advance(_ dt: CGFloat) -> Bool {
        hoverAmount.step(dt, tuning: Prefs.interaction.tools)
        pressAmount.step(dt, tuning: Prefs.interaction.tools)
        reveal.step(dt, tuning: Prefs.interaction.tools)
        overlay.needsDisplay = true
        return !(hoverAmount.isSettled && pressAmount.isSettled && reveal.isSettled)
    }

    func stop() { driver.stop() }

    // MARK: Pointer

    private func updateHover(_ p: NSPoint) {
        let box = box(at: p)
        let target: CGFloat = box == nil ? 0 : 1
        if let box, box != hovered || hoverAmount.target == 0 {
            // A newly hovered box lights up from nothing; one left behind keeps `hovered` while it fades out.
            if box != hovered { hoverAmount.snap(0) }
            hovered = box
        }
        if hoverAmount.target != target {
            hoverAmount.target = target
            kick()
        }
        (box != nil ? NSCursor.pointingHand : imageRect.contains(p) ? NSCursor.crosshair : NSCursor.arrow).set()
    }

    override func cursorUpdate(with event: NSEvent) {
        updateHover(convert(event.locationInWindow, from: nil))
    }

    override func mouseMoved(with event: NSEvent) {
        guard dragStart == nil else { return }
        updateHover(convert(event.locationInWindow, from: nil))
    }

    override func mouseExited(with event: NSEvent) {
        guard dragStart == nil else { return }
        if hoverAmount.target != 0 {
            hoverAmount.target = 0
            kick()
        }
        NSCursor.arrow.set()
    }

    override func mouseDown(with event: NSEvent) {
        guard image != nil else { return }
        let p = convert(event.locationInWindow, from: nil)
        dragStart = p
        dragRect = nil
        pressedBox = box(at: p)
        pressAmount.target = pressedBox == nil ? 0 : 1
        kick()
    }

    override func mouseDragged(with event: NSEvent) {
        guard let start = dragStart else { return }
        let p = convert(event.locationInWindow, from: nil)
        // Past a few points it's a drag: draw a new box, even if it started on an existing one.
        guard dragRect != nil || hypot(p.x - start.x, p.y - start.y) > 3 else { return }
        if pressedBox != nil {
            pressedBox = nil
            pressAmount.target = 0
            kick()
        }
        NSCursor.crosshair.set()
        dragRect = NSRect(x: min(start.x, p.x), y: min(start.y, p.y), width: abs(p.x - start.x), height: abs(p.y - start.y))
            .intersection(imageRect)
        overlay.needsDisplay = true
    }

    override func mouseUp(with event: NSEvent) {
        let p = convert(event.locationInWindow, from: nil)
        defer {
            dragStart = nil
            dragRect = nil
            pressedBox = nil
            pressAmount.target = 0
            kick()
            updateHover(p)
        }
        if let rect = dragRect, rect.width > 4, rect.height > 4 {
            let a = toNormalized(rect.origin), b = toNormalized(NSPoint(x: rect.maxX, y: rect.maxY))
            onAdd?(CGRect(x: a.x, y: a.y, width: b.x - a.x, height: b.y - a.y))
        } else if dragRect == nil, let pressed = pressedBox, box(at: p) == pressed {
            onRemove?(pressed)
        }
    }

    // MARK: Drawing

    private func drawOverlay() {
        guard image != nil else { return }
        let accent = NSColor.controlAccentColor
        for (index, rect) in outlines.enumerated() {
            let frame = toView(rect)
            let lit = index == hovered ? hoverAmount.value : 0
            let pressed = index == pressedBox ? pressAmount.value : 0
            if lit + pressed > 0.01 {
                accent.withAlphaComponent(0.15 * lit + 0.15 * pressed).setFill()
                frame.fill()
            }
            accent.withAlphaComponent(reveal.value).setStroke()
            let path = NSBezierPath(rect: frame)
            path.lineWidth = 1.5 + lit
            path.stroke()
        }
        if let hovered, outlines.indices.contains(hovered), hoverAmount.value > 0.01, dragRect == nil {
            // The × badge says what a click on this box does.
            let r = toView(outlines[hovered])
            let radius = Self.badgeRadius * (Motion.reduced ? 1 : 0.7 + 0.3 * hoverAmount.value)
            let badge = NSRect(x: r.maxX - radius, y: r.maxY - radius, width: radius * 2, height: radius * 2)
            accent.withAlphaComponent(hoverAmount.value).setFill()
            NSBezierPath(ovalIn: badge).fill()
            NSColor.white.withAlphaComponent(hoverAmount.value).setStroke()
            let cross = NSBezierPath()
            let d = radius * 0.38
            cross.move(to: NSPoint(x: badge.midX - d, y: badge.midY - d))
            cross.line(to: NSPoint(x: badge.midX + d, y: badge.midY + d))
            cross.move(to: NSPoint(x: badge.midX - d, y: badge.midY + d))
            cross.line(to: NSPoint(x: badge.midX + d, y: badge.midY - d))
            cross.lineWidth = 1.5
            cross.lineCapStyle = .round
            cross.stroke()
        }
        if let dragRect {
            accent.setStroke()
            let path = NSBezierPath(rect: dragRect)
            path.setLineDash([4, 3], count: 2, phase: 0)
            path.stroke()
        }
    }
}

/// Shows camera, date and location tags and removes the chosen groups. JPEG keeps its pixels untouched; other formats
/// are re-encoded, because a lossless copy keeps raw blocks (PNG text chunks, TIFF IPTC) that still hold the tags.
/// Every result is read back and checked before it is saved.
final class MetadataEditor: Editor {
    struct Removal: Sendable {
        var location: Bool
        var camera: Bool
        var date: Bool
        var author: Bool
        var everything: Bool
    }

    private let preview = ImagePreview()
    private var location: NSButton!
    private var camera: NSButton!
    private var date: NSButton!
    private var author: NSButton!
    private var everything: NSButton!

    init(source: URL) throws {
        super.init(title: "Edit Metadata", source: source, canvas: preview)

        let props = Self.properties(source)
        let exif = props[kCGImagePropertyExifDictionary] as? [CFString: Any] ?? [:]
        let tiff = props[kCGImagePropertyTIFFDictionary] as? [CFString: Any] ?? [:]
        let gps = props[kCGImagePropertyGPSDictionary] as? [CFString: Any] ?? [:]
        func text(_ any: Any?) -> String? { any.map { "\($0)" }.flatMap { $0.isEmpty ? nil : $0 } }

        let camera = [text(tiff[kCGImagePropertyTIFFMake]), text(tiff[kCGImagePropertyTIFFModel])].compactMap { $0 }.joined(separator: " ")
        var shot: [String] = []
        if let f = exif[kCGImagePropertyExifFNumber] as? Double { shot.append("ƒ/\(f)") }
        if let t = exif[kCGImagePropertyExifExposureTime] as? Double { shot.append(t < 1 ? "1/\(Int((1 / t).rounded()))s" : "\(t)s") }
        if let iso = (exif[kCGImagePropertyExifISOSpeedRatings] as? [Int])?.first { shot.append("ISO \(iso)") }
        var place = "—"
        if let lat = gps[kCGImagePropertyGPSLatitude] as? Double, let lon = gps[kCGImagePropertyGPSLongitude] as? Double {
            let ns = gps[kCGImagePropertyGPSLatitudeRef] as? String ?? "N", ew = gps[kCGImagePropertyGPSLongitudeRef] as? String ?? "E"
            place = String(format: "%.5f° %@, %.5f° %@", lat, ns, lon, ew)
        }
        let rows: [(String, String)] = [
            ("Dimensions", "\(props[kCGImagePropertyPixelWidth] ?? "?") × \(props[kCGImagePropertyPixelHeight] ?? "?") px"),
            ("Camera", camera.isEmpty ? "—" : camera),
            ("Lens", text(exif[kCGImagePropertyExifLensModel]) ?? "—"),
            ("Exposure", shot.isEmpty ? "—" : shot.joined(separator: "  ")),
            ("Taken", text(exif[kCGImagePropertyExifDateTimeOriginal]) ?? text(tiff[kCGImagePropertyTIFFDateTime]) ?? "—"),
            ("Location", place),
            ("Author", text(tiff[kCGImagePropertyTIFFArtist]) ?? "—"),
        ]
        for (name, value) in rows {
            let field = NSTextField(labelWithString: value)
            field.isSelectable = true
            field.lineBreakMode = .byTruncatingTail
            addRow(name, field)
        }
        location = checkbox("Location", true)
        self.camera = checkbox("Camera & lens", false)
        date = checkbox("Dates", false)
        author = checkbox("Author", false)
        addRow("Remove", location, self.camera, date, author)
        everything = checkbox("Remove all metadata", false)
        addRow("", everything)
    }

    override func load() async throws {
        let source = self.source
        preview.image = try await Images.detached { try Images.load(source, maxPixels: 900) }
    }

    private static func properties(_ url: URL) -> [CFString: Any] {
        guard let src = CGImageSourceCreateWithURL(url as CFURL, nil) else { return [:] }
        return CGImageSourceCopyPropertiesAtIndex(src, 0, nil) as? [CFString: Any] ?? [:]
    }

    override func update() {
        for box in [location, camera, date, author] { box?.isEnabled = everything.state == .off }
    }

    override func reset() {
        location.state = .on
        for box in [camera, date, author, everything] { box?.state = .off }
    }

    private nonisolated static let cameraTags: Set<String> = [
        "Make", "Model", "LensModel", "LensMake", "LensSpecification", "BodySerialNumber", "SerialNumber",
        "LensSerialNumber", "LensID", "LensInfo", "Lens", "ImageUniqueID", "FNumber", "ExposureTime", "ISOSpeedRatings",
        "PhotographicSensitivity", "FocalLength", "FocalLenIn35mmFilm", "Flash", "ExposureProgram", "MeteringMode",
        "WhiteBalance", "ApertureValue", "ShutterSpeedValue", "ExposureBiasValue", "BrightnessValue", "Software",
        "HostComputer", "CreatorTool"]
    private nonisolated static let authorTags: Set<String> = [
        "Artist", "Copyright", "creator", "Byline", "rights", "CameraOwnerName", "OwnerName", "Credit",
        "CreatorContactInfo", "AuthorsPosition", "CaptionWriter"]
    /// Places written as text by IPTC and XMP (photoshop:City, Iptc4xmpCore:Location, Iptc4xmpExt:LocationShown…).
    private nonisolated static let placeTags: Set<String> = [
        "City", "State", "Country", "CountryCode", "CountryName", "ProvinceState", "Location", "Sublocation",
        "SubLocation", "LocationCreated", "LocationShown", "IIM2-026", "IIM2-027"]

    nonisolated static func shouldRemove(_ name: String, _ removal: Removal) -> Bool {
        if name == "Orientation" { return false }  // keeps photos upright
        if removal.everything { return true }
        if removal.location && (name.hasPrefix("GPS") || placeTags.contains(name)) { return true }
        if removal.camera && cameraTags.contains(name) { return true }
        if removal.date && (name.contains("Date") || name.hasPrefix("OffsetTime") || name.hasPrefix("SubsecTime")) { return true }
        if removal.author && authorTags.contains(name) { return true }
        return false
    }

    override func apply() async throws -> [URL] {
        let removal = Removal(location: location.state == .on, camera: camera.state == .on, date: date.state == .on,
                              author: author.state == .on, everything: everything.state == .on)
        let source = self.source
        return try await Images.detached {
            let data = try Self.cleaned(source, removal)
            return [try Paths.create(data, at: Paths.preferred(for: source, ext: source.pathExtension, suffix: " clean"))]
        }
    }

    /// The file's bytes without the chosen metadata groups.
    nonisolated static func cleaned(_ source: URL, _ removal: Removal) throws -> Data {
        guard let src = CGImageSourceCreateWithURL(source as CFURL, nil), let type = CGImageSourceGetType(src) else {
            throw ToolError("Couldn't read \(source.lastPathComponent)")
        }
        let metadata = CGImageSourceCopyMetadataAtIndex(src, 0, nil).flatMap { CGImageMetadataCreateMutableCopy($0) }
            ?? CGImageMetadataCreateMutable()
        var doomed: [String] = []
        CGImageMetadataEnumerateTagsUsingBlock(metadata, nil, [kCGImageMetadataEnumerateRecursively: true] as CFDictionary) { path, tag in
            if shouldRemove(CGImageMetadataTagCopyName(tag) as String? ?? "", removal) { doomed.append(path as String) }
            return true
        }
        for path in doomed { CGImageMetadataRemoveTagWithPath(metadata, nil, path as CFString) }

        if type as String == UTType.jpeg.identifier {
            let data = NSMutableData()
            if let dest = CGImageDestinationCreateWithData(data, type, 1, nil) {
                let options: [CFString: Any] = [
                    kCGImageDestinationMetadata: metadata,
                    kCGImageDestinationMergeMetadata: false,
                    kCGImageMetadataShouldExcludeGPS: removal.location || removal.everything,
                ]
                if CGImageDestinationCopyImageSource(dest, src, options as CFDictionary, nil), isClean(data as Data, removal) {
                    return data as Data
                }
            }
        }
        // Re-encode the upright pixels with only the kept tags. Orientation is already applied, so it becomes 1.
        let image = try Images.load(source)
        let data = NSMutableData()
        guard let dest = CGImageDestinationCreateWithData(data, type, 1, nil) else { throw ToolError("Can't write this format") }
        let quality = [kCGImageDestinationLossyCompressionQuality: 0.95] as CFDictionary
        if removal.everything {
            CGImageDestinationAddImage(dest, image, quality)
        } else {
            CGImageMetadataSetValueMatchingImageProperty(metadata, kCGImagePropertyTIFFDictionary, kCGImagePropertyTIFFOrientation, 1 as CFNumber)
            CGImageDestinationAddImageAndMetadata(dest, image, metadata, quality)
        }
        guard CGImageDestinationFinalize(dest) else { throw ToolError("Couldn't save") }
        guard isClean(data as Data, removal) else { throw ToolError(L("Couldn't remove all of the selected metadata from this file.")) }
        return data as Data
    }

    /// Reads the result back: none of the chosen groups may remain, in XMP, IPTC, EXIF, GPS or maker notes.
    nonisolated static func isClean(_ data: Data, _ removal: Removal) -> Bool {
        guard let src = CGImageSourceCreateWithData(data as CFData, nil) else { return false }
        // "Everything" is checked group by group: encoders add harmless technical tags (colour space, resolution).
        let check = removal.everything ? Removal(location: true, camera: true, date: true, author: true, everything: false) : removal
        var clean = true
        if let metadata = CGImageSourceCopyMetadataAtIndex(src, 0, nil) {
            CGImageMetadataEnumerateTagsUsingBlock(metadata, nil, [kCGImageMetadataEnumerateRecursively: true] as CFDictionary) { _, tag in
                if shouldRemove(CGImageMetadataTagCopyName(tag) as String? ?? "", check) { clean = false }
                return clean
            }
        }
        let props = CGImageSourceCopyPropertiesAtIndex(src, 0, nil) as? [CFString: Any] ?? [:]
        if check.location, let gps = props[kCGImagePropertyGPSDictionary] as? [CFString: Any], !gps.isEmpty { clean = false }
        if check.camera, props.keys.contains(where: { ($0 as String).hasPrefix("{Maker") }) { clean = false }
        return clean
    }
}
