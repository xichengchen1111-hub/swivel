import AppKit
import CoreImage
import ImageIO
import Vision

private let ciContext = CIContext(options: [.useSoftwareRenderer: false])

/// Exposure, contrast, saturation, warmth, vignette and sharpness with a live preview.
final class AdjustEditor: Editor {
    private let preview = ImagePreview()
    private let small: CGImage
    private var sliders: [(NSSlider, Double)] = []

    init(source: URL) throws {
        small = try Images.load(source, maxPixels: 1400)
        super.init(title: "Edit Photo", source: source, canvas: preview)
        for (name, min, max, value) in [("Exposure", -2.0, 2.0, 0.0), ("Contrast", 0.5, 1.5, 1.0),
                                         ("Saturation", 0.0, 2.0, 1.0), ("Warmth", -1.0, 1.0, 0.0),
                                         ("Vignette", 0.0, 2.0, 0.0), ("Sharpen", 0.0, 2.0, 0.0)] {
            let control = slider(min, max, value)
            sliders.append((control, value))
            addRow(name, control)
        }
    }

    private func render(_ image: CGImage) -> CGImage? {
        let v = sliders.map { $0.0.doubleValue }
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

    override func update() { preview.image = render(small) }

    override func reset() { for (control, value) in sliders { control.doubleValue = value } }

    override func apply() async throws -> [URL] {
        guard let result = render(try Images.load(source)) else { throw ToolError("Couldn't render") }
        let (type, ext) = Images.writableType(for: source)
        let out = Paths.output(for: source, ext: ext, suffix: " edited")
        try Images.write(result, to: out, type: type, quality: 0.95)
        return [out]
    }
}

/// Places the photo on a colour or gradient backdrop with padding, rounded corners and a shadow.
final class BackgroundEditor: Editor {
    private let preview = ImagePreview()
    private let small: CGImage
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

    init(source: URL) throws {
        small = try Images.load(source, maxPixels: 1000)
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

    private func render(_ image: CGImage) -> CGImage? {
        let w = CGFloat(image.width), h = CGFloat(image.height)
        let pad = padding.doubleValue * max(w, h)
        var canvas = CGSize(width: w + pad * 2, height: h + pad * 2)
        if let ratio = aspects[aspect.indexOfSelectedItem].1 {
            if canvas.width / canvas.height > ratio { canvas.height = canvas.width / ratio }
            else { canvas.width = canvas.height * ratio }
        }
        guard let ctx = CGContext(data: nil, width: Int(canvas.width), height: Int(canvas.height), bitsPerComponent: 8,
                                  bytesPerRow: 0, space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        let full = CGRect(origin: .zero, size: canvas)
        var colors = styles[style.indexOfSelectedItem].1
        if colors.isEmpty { colors = [colorWell.color] }
        if colors.count == 1 {
            ctx.setFillColor(colors[0].cgColor)
            ctx.fill(full)
        } else if let gradient = CGGradient(colorsSpace: CGColorSpace(name: CGColorSpace.sRGB),
                                            colors: colors.map(\.cgColor) as CFArray, locations: nil) {
            ctx.drawLinearGradient(gradient, start: CGPoint(x: 0, y: canvas.height), end: CGPoint(x: canvas.width, y: 0),
                                   options: [])
        }

        let frame = CGRect(x: (canvas.width - w) / 2, y: (canvas.height - h) / 2, width: w, height: h)
        let corner = radius.doubleValue * min(w, h)
        let path = CGPath(roundedRect: frame, cornerWidth: corner, cornerHeight: corner, transform: nil)
        if shadow.state == .on {
            ctx.saveGState()
            ctx.setShadow(offset: CGSize(width: 0, height: -max(w, h) * 0.012), blur: max(w, h) * 0.04,
                          color: NSColor.black.withAlphaComponent(0.4).cgColor)
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

    override func update() {
        colorWell.isHidden = style.indexOfSelectedItem != styles.count - 1
        preview.image = render(small)
    }

    override func reset() {
        style.selectItem(at: 0)
        padding.doubleValue = 0.12
        radius.doubleValue = 0.04
        aspect.selectItem(at: 0)
        shadow.state = .on
    }

    override func apply() async throws -> [URL] {
        guard let result = render(try Images.load(source)) else { throw ToolError("Couldn't render") }
        let out = Paths.output(for: source, ext: "png", suffix: " background")
        try Images.write(result, to: out, type: .png, quality: 1)
        return [out]
    }
}

/// Finds faces and text on-device and covers them; drag on the photo to add a box, click a box to remove it.
final class RedactEditor: Editor {
    private enum Kind { case face, text, manual }
    private struct Box { var rect: CGRect; var kind: Kind }  // normalized, bottom-left origin (Vision space)

    private let preview = RedactCanvas()
    private let small: CGImage
    private var boxes: [Box] = []
    private var faces: NSButton!
    private var text: NSButton!
    private var style: NSSegmentedControl!
    private let status = NSTextField(labelWithString: "Looking for faces and text…")

    init(source: URL) throws {
        small = try Images.load(source, maxPixels: 1400)
        super.init(title: "Redact Photo", source: source, canvas: preview)
        faces = checkbox("Faces", true)
        text = checkbox("Text", true)
        addRow("Detect", faces, text)
        style = segments(["Blur", "Pixelate", "Solid"])
        addRow("Style", style)
        status.textColor = .secondaryLabelColor
        addRow("", status)

        preview.onAdd = { [weak self] rect in
            self?.boxes.append(Box(rect: rect, kind: .manual))
            self?.update()
        }
        preview.onRemove = { [weak self] point in
            guard let self, let index = self.active.lastIndex(where: { $0.rect.contains(point) }) else { return }
            let target = self.active[index].rect
            self.boxes.removeAll { $0.rect == target }
            self.update()
        }

        let image = small
        Task { @MainActor [weak self] in
            let found = await Task.detached { Self.detect(image) }.value
            guard let self else { return }
            self.boxes += found
            let f = found.filter { $0.kind == .face }.count, t = found.filter { $0.kind == .text }.count
            self.status.stringValue = "Found \(f) face\(f == 1 ? "" : "s") and \(t) text area\(t == 1 ? "" : "s"). Drag to add a box, click one to remove it."
            self.update()
        }
    }

    private nonisolated static func detect(_ image: CGImage) -> [Box] {
        let faceRequest = VNDetectFaceRectanglesRequest()
        let textRequest = VNRecognizeTextRequest()
        textRequest.recognitionLevel = .fast
        try? VNImageRequestHandler(cgImage: image).perform([faceRequest, textRequest])
        let faces = (faceRequest.results ?? []).map { Box(rect: $0.boundingBox.insetBy(dx: -0.01, dy: -0.01), kind: .face) }
        let text = (textRequest.results ?? []).map { Box(rect: $0.boundingBox.insetBy(dx: -0.003, dy: -0.004), kind: .text) }
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

    private func render(_ image: CGImage) -> CGImage? {
        let w = CGFloat(image.width), h = CGFloat(image.height)
        let rects = active.map { CGRect(x: $0.rect.minX * w, y: $0.rect.minY * h, width: $0.rect.width * w, height: $0.rect.height * h) }
        guard !rects.isEmpty else { return image }

        if style.selectedSegment == 2 {
            guard let ctx = CGContext(data: nil, width: image.width, height: image.height, bitsPerComponent: 8,
                                      bytesPerRow: 0, space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                      bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
            ctx.draw(image, in: CGRect(x: 0, y: 0, width: w, height: h))
            ctx.setFillColor(.black)
            ctx.fill(rects)
            return ctx.makeImage()
        }

        let original = CIImage(cgImage: image)
        let effect: CIImage
        if style.selectedSegment == 0 {
            effect = original.clampedToExtent().applyingGaussianBlur(sigma: max(w, h) * 0.02).cropped(to: original.extent)
        } else {
            let pixellate = CIFilter(name: "CIPixellate")!
            pixellate.setValue(original.clampedToExtent(), forKey: kCIInputImageKey)
            pixellate.setValue(max(w, h) / 70, forKey: kCIInputScaleKey)
            effect = pixellate.outputImage!.cropped(to: original.extent)
        }
        var mask = CIImage(color: .black).cropped(to: original.extent)
        for rect in rects { mask = CIImage(color: .white).cropped(to: rect).composited(over: mask) }
        let blend = CIFilter(name: "CIBlendWithMask")!
        blend.setValue(effect, forKey: kCIInputImageKey)
        blend.setValue(original, forKey: kCIInputBackgroundImageKey)
        blend.setValue(mask, forKey: kCIInputMaskImageKey)
        return ciContext.createCGImage(blend.outputImage!, from: original.extent)
    }

    override func update() {
        preview.image = render(small)
        preview.outlines = active.map(\.rect)
    }

    override func reset() {
        boxes.removeAll { $0.kind == .manual }
        faces.state = .on
        text.state = .on
        style.selectedSegment = 0
    }

    override func apply() async throws -> [URL] {
        guard let result = render(try Images.load(source)) else { throw ToolError("Couldn't render") }
        let (type, ext) = Images.writableType(for: source)
        let out = Paths.output(for: source, ext: ext, suffix: " redacted")
        try Images.write(result, to: out, type: type, quality: 0.95)
        return [out]
    }
}

/// Image preview that outlines redaction boxes and lets you draw or remove them. Rects are normalized, bottom-left origin.
final class RedactCanvas: NSView {
    var image: CGImage? { didSet { needsDisplay = true } }
    var outlines: [CGRect] = [] { didSet { needsDisplay = true } }
    var onAdd: ((CGRect) -> Void)?
    var onRemove: ((CGPoint) -> Void)?
    private var dragStart: NSPoint?
    private var dragRect: NSRect?

    private var imageRect: NSRect {
        guard let image else { return .zero }
        return fit(CGSize(width: image.width, height: image.height), in: bounds.insetBy(dx: 12, dy: 12))
    }

    private func toView(_ r: CGRect) -> NSRect {
        let i = imageRect
        return NSRect(x: i.minX + r.minX * i.width, y: i.minY + r.minY * i.height, width: r.width * i.width, height: r.height * i.height)
    }

    private func toNormalized(_ p: NSPoint) -> CGPoint {
        let i = imageRect
        return CGPoint(x: (p.x - i.minX) / i.width, y: (p.y - i.minY) / i.height)
    }

    override func draw(_ dirtyRect: NSRect) {
        guard let image else { return }
        NSGraphicsContext.current?.imageInterpolation = .high
        NSImage(cgImage: image, size: imageRect.size).draw(in: imageRect)
        NSColor.controlAccentColor.setStroke()
        for rect in outlines {
            let path = NSBezierPath(rect: toView(rect))
            path.lineWidth = 1.5
            path.stroke()
        }
        if let dragRect {
            let path = NSBezierPath(rect: dragRect)
            path.setLineDash([4, 3], count: 2, phase: 0)
            path.stroke()
        }
    }

    override func mouseDown(with event: NSEvent) {
        dragStart = convert(event.locationInWindow, from: nil)
        dragRect = nil
    }

    override func mouseDragged(with event: NSEvent) {
        guard let start = dragStart else { return }
        let p = convert(event.locationInWindow, from: nil)
        dragRect = NSRect(x: min(start.x, p.x), y: min(start.y, p.y), width: abs(p.x - start.x), height: abs(p.y - start.y))
            .intersection(imageRect)
        needsDisplay = true
    }

    override func mouseUp(with event: NSEvent) {
        defer { dragStart = nil; dragRect = nil; needsDisplay = true }
        if let rect = dragRect, rect.width > 4, rect.height > 4 {
            let a = toNormalized(rect.origin), b = toNormalized(NSPoint(x: rect.maxX, y: rect.maxY))
            onAdd?(CGRect(x: a.x, y: a.y, width: b.x - a.x, height: b.y - a.y))
        } else if let start = dragStart {
            onRemove?(toNormalized(start))
        }
    }
}

/// Shows camera, date and location tags and removes the chosen groups without re-encoding where possible.
final class MetadataEditor: Editor {
    private let preview = ImagePreview()
    private var location: NSButton!
    private var camera: NSButton!
    private var date: NSButton!
    private var author: NSButton!
    private var everything: NSButton!

    init(source: URL) throws {
        super.init(title: "Edit Metadata", source: source, canvas: preview)
        preview.image = try Images.load(source, maxPixels: 900)

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

    private func shouldRemove(_ name: String) -> Bool {
        if name == "Orientation" { return false }  // keeps photos upright
        if everything.state == .on { return true }
        let cameraTags: Set<String> = ["Make", "Model", "LensModel", "LensMake", "LensSpecification", "BodySerialNumber",
                                       "SerialNumber", "LensSerialNumber", "FNumber", "ExposureTime", "ISOSpeedRatings",
                                       "PhotographicSensitivity", "FocalLength", "FocalLenIn35mmFilm", "Flash",
                                       "ExposureProgram", "MeteringMode", "WhiteBalance", "ApertureValue",
                                       "ShutterSpeedValue", "ExposureBiasValue", "BrightnessValue", "Software", "HostComputer"]
        let authorTags: Set<String> = ["Artist", "Copyright", "creator", "Byline", "rights", "CameraOwnerName"]
        if location.state == .on && name.hasPrefix("GPS") { return true }
        if camera.state == .on && cameraTags.contains(name) { return true }
        if date.state == .on && (name.contains("Date") || name.hasPrefix("OffsetTime") || name.hasPrefix("SubsecTime")) { return true }
        if author.state == .on && authorTags.contains(name) { return true }
        return false
    }

    override func apply() async throws -> [URL] {
        guard let src = CGImageSourceCreateWithURL(source as CFURL, nil), let type = CGImageSourceGetType(src) else {
            throw ToolError("Couldn't read \(source.lastPathComponent)")
        }
        let metadata = CGImageSourceCopyMetadataAtIndex(src, 0, nil).flatMap { CGImageMetadataCreateMutableCopy($0) }
            ?? CGImageMetadataCreateMutable()
        var doomed: [String] = []
        func collect(_ parent: String?, _ meta: CGImageMetadata) {
            CGImageMetadataEnumerateTagsUsingBlock(meta, parent as CFString?, [kCGImageMetadataEnumerateRecursively: true] as CFDictionary) { path, tag in
                let name = CGImageMetadataTagCopyName(tag) as String? ?? ""
                if self.shouldRemove(name) { doomed.append(path as String) }
                return true
            }
        }
        collect(nil, metadata)
        for path in doomed { CGImageMetadataRemoveTagWithPath(metadata, nil, path as CFString) }

        let out = Paths.output(for: source, ext: source.pathExtension, suffix: " clean")
        guard let dest = CGImageDestinationCreateWithURL(out as CFURL, type, 1, nil) else { throw ToolError("Can't write this format") }
        let options: [CFString: Any] = [
            kCGImageDestinationMetadata: metadata,
            kCGImageDestinationMergeMetadata: false,
            kCGImageMetadataShouldExcludeGPS: location.state == .on || everything.state == .on,
        ]
        var error: Unmanaged<CFError>?
        if !CGImageDestinationCopyImageSource(dest, src, options as CFDictionary, &error) {
            // Formats that can't be copied losslessly get re-encoded without the removed tags.
            try? FileManager.default.removeItem(at: out)
            guard let dest = CGImageDestinationCreateWithURL(out as CFURL, type, 1, nil) else { throw ToolError("Can't write this format") }
            CGImageDestinationAddImageAndMetadata(dest, try Images.load(source), metadata,
                                                  [kCGImageDestinationLossyCompressionQuality: 0.95] as CFDictionary)
            guard CGImageDestinationFinalize(dest) else { throw ToolError("Couldn't save") }
        }
        return [out]
    }
}
