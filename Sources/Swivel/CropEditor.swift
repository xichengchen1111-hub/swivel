import AppKit
import PDFKit
import UniformTypeIdentifiers

/// Draggable crop box over an image. `crop` is in image pixels with a top-left origin.
final class CropView: NSView {
    var image: CGImage
    var crop: CGRect
    var ratio: CGFloat?
    var onChange: (() -> Void)?

    private struct Grab { var left = false, right = false, top = false, bottom = false, move = false }
    private var grab = Grab()
    private var startCrop = CGRect.zero
    private var startPoint = NSPoint.zero

    init(image: CGImage) {
        self.image = image
        crop = CGRect(x: 0, y: 0, width: image.width, height: image.height)
        super.init(frame: .zero)
    }

    required init?(coder: NSCoder) { fatalError() }

    override var isFlipped: Bool { true }

    private var bounds2D: CGSize { CGSize(width: image.width, height: image.height) }
    private var imageRect: NSRect { fit(bounds2D, in: bounds.insetBy(dx: 14, dy: 14)) }
    private var scale: CGFloat { imageRect.width / CGFloat(image.width) }

    private func viewRect(_ r: CGRect) -> NSRect {
        NSRect(x: imageRect.minX + r.minX * scale, y: imageRect.minY + r.minY * scale,
               width: r.width * scale, height: r.height * scale)
    }

    override func draw(_ dirtyRect: NSRect) {
        let rect = imageRect
        NSGraphicsContext.current?.imageInterpolation = .high
        NSImage(cgImage: image, size: rect.size)
            .draw(in: rect, from: .zero, operation: .sourceOver, fraction: 1, respectFlipped: true, hints: nil)

        let box = viewRect(crop)
        let dim = NSBezierPath(rect: rect)
        dim.append(NSBezierPath(rect: box))
        dim.windingRule = .evenOdd
        NSColor.black.withAlphaComponent(0.5).setFill()
        dim.fill()

        // Rule-of-thirds guides.
        NSColor.white.withAlphaComponent(0.35).setStroke()
        for i in 1...2 {
            let x = box.minX + box.width * CGFloat(i) / 3, y = box.minY + box.height * CGFloat(i) / 3
            NSBezierPath.strokeLine(from: NSPoint(x: x, y: box.minY), to: NSPoint(x: x, y: box.maxY))
            NSBezierPath.strokeLine(from: NSPoint(x: box.minX, y: y), to: NSPoint(x: box.maxX, y: y))
        }

        let accent = NSColor.systemOrange
        accent.setStroke()
        let border = NSBezierPath(rect: box)
        border.lineWidth = 2
        border.stroke()

        accent.setFill()
        let s: CGFloat = 10
        for p in [NSPoint(x: box.minX, y: box.minY), NSPoint(x: box.maxX, y: box.minY),
                  NSPoint(x: box.minX, y: box.maxY), NSPoint(x: box.maxX, y: box.maxY)] {
            NSBezierPath(rect: NSRect(x: p.x - s / 2, y: p.y - s / 2, width: s, height: s)).fill()
        }
        for (p, horizontal) in [(NSPoint(x: box.midX, y: box.minY), true), (NSPoint(x: box.midX, y: box.maxY), true),
                                (NSPoint(x: box.minX, y: box.midY), false), (NSPoint(x: box.maxX, y: box.midY), false)] {
            let size = horizontal ? NSSize(width: 20, height: 5) : NSSize(width: 5, height: 20)
            NSBezierPath(roundedRect: NSRect(x: p.x - size.width / 2, y: p.y - size.height / 2,
                                             width: size.width, height: size.height), xRadius: 2, yRadius: 2).fill()
        }
    }

    override func mouseDown(with event: NSEvent) {
        let p = convert(event.locationInWindow, from: nil)
        let box = viewRect(crop)
        let tol: CGFloat = 12
        let inY = p.y > box.minY - tol && p.y < box.maxY + tol
        let inX = p.x > box.minX - tol && p.x < box.maxX + tol
        grab = Grab(left: inY && abs(p.x - box.minX) < tol, right: inY && abs(p.x - box.maxX) < tol,
                    top: inX && abs(p.y - box.minY) < tol, bottom: inX && abs(p.y - box.maxY) < tol)
        if !(grab.left || grab.right || grab.top || grab.bottom) { grab.move = box.contains(p) }
        startCrop = crop
        startPoint = p
    }

    override func mouseDragged(with event: NSEvent) {
        guard grab.move || grab.left || grab.right || grab.top || grab.bottom else { return }
        let p = convert(event.locationInWindow, from: nil)
        let dx = (p.x - startPoint.x) / scale, dy = (p.y - startPoint.y) / scale
        let W = CGFloat(image.width), H = CGFloat(image.height)
        let minSide: CGFloat = 16
        var r = startCrop

        if grab.move {
            r.origin.x = min(max(0, r.minX + dx), W - r.width)
            r.origin.y = min(max(0, r.minY + dy), H - r.height)
        } else {
            var minX = r.minX, maxX = r.maxX, minY = r.minY, maxY = r.maxY
            if grab.left { minX = min(max(0, minX + dx), maxX - minSide) }
            if grab.right { maxX = max(min(W, maxX + dx), minX + minSide) }
            if grab.top { minY = min(max(0, minY + dy), maxY - minSide) }
            if grab.bottom { maxY = max(min(H, maxY + dy), minY + minSide) }
            r = CGRect(x: minX, y: minY, width: maxX - minX, height: maxY - minY)

            if let ratio {
                let horizontal = grab.left || grab.right, vertical = grab.top || grab.bottom
                if horizontal && !vertical {
                    r.size.height = r.width / ratio
                    r.origin.y = startCrop.midY - r.height / 2
                } else if vertical && !horizontal {
                    r.size.width = r.height * ratio
                    r.origin.x = startCrop.midX - r.width / 2
                } else {
                    r.size.height = r.width / ratio
                    if grab.top { r.origin.y = maxY - r.height }
                }
                r = constrain(r)
            }
        }
        crop = r.integral
        onChange?()
        needsDisplay = true
    }

    /// Shrinks (keeping the ratio) and shifts a rect until it lies inside the image.
    private func constrain(_ rect: CGRect) -> CGRect {
        var r = rect
        let W = CGFloat(image.width), H = CGFloat(image.height)
        let shrink = min(1, W / r.width, H / r.height)
        r.size = CGSize(width: r.width * shrink, height: r.height * shrink)
        r.origin.x = min(max(0, r.minX), W - r.width)
        r.origin.y = min(max(0, r.minY), H - r.height)
        return r
    }

    func setRatio(_ ratio: CGFloat?) {
        self.ratio = ratio
        guard let ratio else { return }
        let W = CGFloat(image.width), H = CGFloat(image.height)
        let size = W / H > ratio ? CGSize(width: H * ratio, height: H) : CGSize(width: W, height: W / ratio)
        crop = CGRect(x: (W - size.width) / 2, y: (H - size.height) / 2, width: size.width, height: size.height).integral
        needsDisplay = true
    }

    func setSize(width: CGFloat, height: CGFloat) {
        let W = CGFloat(image.width), H = CGFloat(image.height)
        let w = min(max(width, 16), W), h = min(max(height, 16), H)
        crop = constrain(CGRect(x: crop.midX - w / 2, y: crop.midY - h / 2, width: w, height: h)).integral
        needsDisplay = true
    }

    func reset() {
        ratio = nil
        crop = CGRect(x: 0, y: 0, width: image.width, height: image.height)
        needsDisplay = true
    }
}

/// Crops an image, or every page of a PDF by the same proportion.
final class CropEditor: Editor {
    private let view: CropView
    private let ratios: [(String, CGFloat?)] = [("Free", nil), ("1:1", 1), ("16:9", 16 / 9), ("9:16", 9 / 16),
                                                 ("4:3", 4 / 3), ("3:4", 3 / 4)]
    private var aspect: NSSegmentedControl!
    private var widthField: NSTextField!
    private var heightField: NSTextField!
    private var lastAspect = 0
    private let isPDF: Bool
    /// Output units per image pixel (PDF crops are shown in points).
    private let unit: CGFloat

    init(source: URL) throws {
        isPDF = FileKind.of(source) == .pdf
        let image: CGImage
        if isPDF {
            guard let page = try PDFs.open(source).page(at: 0) else { throw ToolError("This PDF has no pages") }
            image = try PDFs.render(page, dpi: 144)
            unit = 0.5
        } else {
            image = try Images.load(source)
            unit = 1
        }
        view = CropView(image: image)
        super.init(title: "Crop Image", source: source, canvas: view)
        if isPDF { window.title = "Crop PDF" }

        aspect = segments(ratios.map(\.0))
        addRow("Aspect ratio", aspect)
        widthField = numberField(0)
        heightField = numberField(0)
        for field in [widthField!, heightField!] { field.action = #selector(sizeEdited) }
        let units = NSTextField(labelWithString: isPDF ? "pt" : "px")
        units.textColor = .systemOrange
        let times = NSTextField(labelWithString: "×")
        addRow("Size", NSTextField(labelWithString: "W"), widthField, times, NSTextField(labelWithString: "H"),
               heightField, units)
        view.onChange = { [weak self] in self?.syncFields() }
    }

    private func syncFields() {
        widthField.integerValue = Int((view.crop.width * unit).rounded())
        heightField.integerValue = Int((view.crop.height * unit).rounded())
    }

    override func update() {
        if aspect.selectedSegment != lastAspect {
            lastAspect = aspect.selectedSegment
            view.setRatio(ratios[lastAspect].1)
        }
        syncFields()
    }

    @objc private func sizeEdited(_ sender: Any?) {
        view.setSize(width: CGFloat(widthField.integerValue) / unit, height: CGFloat(heightField.integerValue) / unit)
        syncFields()
    }

    override func reset() {
        view.reset()
        aspect.selectedSegment = 0
        lastAspect = 0
    }

    override func apply() throws -> [URL] {
        let crop = view.crop
        if isPDF {
            let doc = try PDFs.open(source)
            let W = CGFloat(view.image.width), H = CGFloat(view.image.height)
            for i in 0..<doc.pageCount {
                guard let page = doc.page(at: i) else { continue }
                let b = page.bounds(for: .cropBox)
                let box = CGRect(x: b.minX + crop.minX / W * b.width,
                                 y: b.minY + (1 - crop.maxY / H) * b.height,
                                 width: crop.width / W * b.width, height: crop.height / H * b.height)
                page.setBounds(box, for: .cropBox)
                page.setBounds(box, for: .mediaBox)
            }
            let out = Paths.output(for: source, ext: "pdf", suffix: " cropped")
            guard doc.write(to: out) else { throw ToolError("Couldn't write PDF") }
            return [out]
        }
        guard let cropped = view.image.cropping(to: crop) else { throw ToolError("Couldn't crop") }
        let (type, ext) = Images.writableType(for: source)
        let out = Paths.output(for: source, ext: ext, suffix: " cropped")
        try Images.write(cropped, to: out, type: type, quality: 0.95)
        return [out]
    }
}

/// Re-encodes an image with a chosen quality, size cap and format, showing the resulting size live.
final class CompressEditor: Editor {
    private let preview = ImagePreview()
    private let originalBytes: Int
    private let sizes = [0, 3840, 2560, 1920, 1280, 800]
    private var formats: [(String, UTType, String)] = []
    private var cache: [Int: CGImage] = [:]
    private var quality: NSSlider!
    private var qualityLabel: NSTextField!
    private var maxSize: NSPopUpButton!
    private var format: NSPopUpButton!
    private let estimate = NSTextField(labelWithString: "")
    private var job: Task<Void, Never>?

    init(source: URL) throws {
        originalBytes = (try? source.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0
        super.init(title: "Compress Image", source: source, canvas: preview)
        preview.image = try Images.load(source, maxPixels: 1400)

        let (sourceType, sourceExt) = Images.writableType(for: source)
        let lossy: Set<UTType> = [.jpeg, .heic, .webP]
        if lossy.contains(sourceType) { formats.append(("Same (\(sourceExt.uppercased()))", sourceType, sourceExt)) }
        formats += [("JPG", .jpeg, "jpg"), ("HEIC", .heic, "heic"), ("PNG", .png, "png")]

        quality = slider(0.1, 1, 0.7)
        qualityLabel = NSTextField(labelWithString: "70%")
        qualityLabel.widthAnchor.constraint(equalToConstant: 40).isActive = true
        addRow("Quality", quality, qualityLabel)
        maxSize = popup(sizes.map { $0 == 0 ? "Original size" : "Max \($0) px" })
        addRow("Dimensions", maxSize)
        format = popup(formats.map(\.0))
        addRow("Format", format)
        estimate.font = .systemFont(ofSize: 13, weight: .semibold)
        addRow("Result", estimate)
    }

    private func image(maxPixels: Int) throws -> CGImage {
        if let cached = cache[maxPixels] { return cached }
        let image = try Images.load(source, maxPixels: maxPixels == 0 ? nil : maxPixels)
        cache[maxPixels] = image
        return image
    }

    private func encode() throws -> (Data, String) {
        let (_, type, ext) = formats[format.indexOfSelectedItem]
        return (try Images.encode(try image(maxPixels: sizes[maxSize.indexOfSelectedItem]), type: type,
                                  quality: quality.doubleValue), ext)
    }

    override func update() {
        qualityLabel.stringValue = "\(Int((quality.doubleValue * 100).rounded()))%"
        estimate.stringValue = "Calculating…"
        job?.cancel()
        job = Task { @MainActor [weak self] in
            try? await Task.sleep(for: .milliseconds(150))
            guard let self, !Task.isCancelled, let (data, _) = try? self.encode() else { return }
            let change = self.originalBytes > 0 ? Int((1 - Double(data.count) / Double(self.originalBytes)) * 100) : 0
            self.estimate.stringValue = "\(byteString(self.originalBytes)) → \(byteString(data.count))"
                + (change > 0 ? "  (−\(change)%)" : "  (+\(-change)%)")
            self.estimate.textColor = change > 0 ? .systemGreen : .systemRed
        }
    }

    override func reset() {
        quality.doubleValue = 0.7
        maxSize.selectItem(at: 0)
        format.selectItem(at: 0)
    }

    override func apply() throws -> [URL] {
        let (data, ext) = try encode()
        let out = Paths.output(for: source, ext: ext, suffix: " compressed")
        try data.write(to: out)
        return [out]
    }
}
