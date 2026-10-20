import AppKit

/// A mark on the photo. Points are normalized (0–1), bottom-left origin, so marks scale to full resolution.
struct Mark {
    enum Kind { case arrow, rectangle, ellipse, text, number }
    var kind: Kind
    var start: CGPoint
    var end: CGPoint
    var color: NSColor
    var width: CGFloat      // relative to the image's longer side
    var text = ""
}

/// Draws marks into any context whose drawing area is `rect`.
enum MarkRenderer {
    static func draw(_ marks: [Mark], in rect: CGRect, context ctx: CGContext) {
        let side = max(rect.width, rect.height)
        func point(_ p: CGPoint) -> CGPoint { CGPoint(x: rect.minX + p.x * rect.width, y: rect.minY + p.y * rect.height) }

        for mark in marks {
            let a = point(mark.start), b = point(mark.end)
            let lineWidth = max(1, mark.width * side)
            ctx.saveGState()
            ctx.setStrokeColor(mark.color.cgColor)
            ctx.setFillColor(mark.color.cgColor)
            ctx.setLineWidth(lineWidth)
            ctx.setLineCap(.round)
            ctx.setLineJoin(.round)
            ctx.setShadow(offset: CGSize(width: 0, height: -lineWidth * 0.3), blur: lineWidth,
                          color: NSColor.black.withAlphaComponent(0.3).cgColor)
            let box = CGRect(x: min(a.x, b.x), y: min(a.y, b.y), width: abs(b.x - a.x), height: abs(b.y - a.y))

            switch mark.kind {
            case .arrow:
                let angle = atan2(b.y - a.y, b.x - a.x)
                let head = lineWidth * 4.5
                let shaftEnd = CGPoint(x: b.x - cos(angle) * head * 0.8, y: b.y - sin(angle) * head * 0.8)
                ctx.move(to: a)
                ctx.addLine(to: shaftEnd)
                ctx.strokePath()
                ctx.move(to: b)
                ctx.addLine(to: CGPoint(x: b.x - cos(angle - .pi / 7) * head, y: b.y - sin(angle - .pi / 7) * head))
                ctx.addLine(to: CGPoint(x: b.x - cos(angle + .pi / 7) * head, y: b.y - sin(angle + .pi / 7) * head))
                ctx.closePath()
                ctx.fillPath()
            case .rectangle:
                ctx.addPath(CGPath(roundedRect: box, cornerWidth: lineWidth, cornerHeight: lineWidth, transform: nil))
                ctx.strokePath()
            case .ellipse:
                ctx.strokeEllipse(in: box)
            case .text:
                let size = lineWidth * 7
                let attrs: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: size, weight: .bold),
                                                            .foregroundColor: mark.color]
                drawString(mark.text, attrs: attrs, at: CGPoint(x: a.x, y: a.y - size), in: ctx)
            case .number:
                let r = lineWidth * 3.5
                ctx.fillEllipse(in: CGRect(x: a.x - r, y: a.y - r, width: r * 2, height: r * 2))
                ctx.setShadow(offset: .zero, blur: 0, color: nil)
                let attrs: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: r * 1.15, weight: .heavy),
                                                            .foregroundColor: NSColor.white]
                let size = (mark.text as NSString).size(withAttributes: attrs)
                drawString(mark.text, attrs: attrs, at: CGPoint(x: a.x - size.width / 2, y: a.y - size.height / 2), in: ctx)
            }
            ctx.restoreGState()
        }
    }

    private static func drawString(_ string: String, attrs: [NSAttributedString.Key: Any], at point: CGPoint, in ctx: CGContext) {
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(cgContext: ctx, flipped: false)
        (string as NSString).draw(at: point, withAttributes: attrs)
        NSGraphicsContext.restoreGraphicsState()
    }
}

final class AnnotateCanvas: NSView {
    var image: CGImage? { didSet { needsDisplay = true } }
    var marks: [Mark] = [] { didSet { needsDisplay = true } }
    var draft: Mark? { didSet { needsDisplay = true } }
    var makeMark: ((CGPoint) -> Mark?)?
    var onCommit: ((Mark) -> Void)?

    var imageRect: NSRect {
        guard let image else { return .zero }
        return fit(CGSize(width: image.width, height: image.height), in: bounds.insetBy(dx: 12, dy: 12))
    }

    private func normalized(_ event: NSEvent) -> CGPoint {
        let p = convert(event.locationInWindow, from: nil), r = imageRect
        return CGPoint(x: min(max((p.x - r.minX) / r.width, 0), 1), y: min(max((p.y - r.minY) / r.height, 0), 1))
    }

    override func draw(_ dirtyRect: NSRect) {
        guard let image, let ctx = NSGraphicsContext.current?.cgContext else { return }
        ctx.interpolationQuality = .high
        ctx.draw(image, in: imageRect)
        MarkRenderer.draw(marks + (draft.map { [$0] } ?? []), in: imageRect, context: ctx)
    }

    override func mouseDown(with event: NSEvent) { draft = makeMark?(normalized(event)) }

    override func mouseDragged(with event: NSEvent) {
        guard var mark = draft, mark.kind != .number, mark.kind != .text else { return }
        mark.end = normalized(event)
        draft = mark
    }

    override func mouseUp(with event: NSEvent) {
        guard let mark = draft else { return }
        draft = nil
        let moved = hypot(mark.end.x - mark.start.x, mark.end.y - mark.start.y) > 0.01
        if mark.kind == .text || mark.kind == .number || moved { onCommit?(mark) }
    }
}

/// Arrows, boxes, circles, text and numbered callouts.
final class AnnotateEditor: Editor {
    private let canvas = AnnotateCanvas()
    private let tools: [(String, Mark.Kind)] = [("Arrow", .arrow), ("Box", .rectangle), ("Circle", .ellipse),
                                                 ("Text", .text), ("Number", .number)]
    private var tool: NSSegmentedControl!
    private let colorWell = NSColorWell(style: .minimal)
    private var thickness: NSSlider!
    private let textField = NSTextField(string: "Note")

    init(source: URL) throws {
        super.init(title: "Annotate Photo", source: source, canvas: canvas)
        canvas.image = try Images.load(source, maxPixels: 1600)
        tool = segments(tools.map(\.0))
        addRow("Tool", tool)
        colorWell.color = .systemRed
        thickness = slider(0.002, 0.015, 0.006)
        addRow("Style", colorWell, thickness)
        textField.placeholderString = "Text to place"
        addRow("Text", textField)
        let undo = NSButton(title: L("Undo"), target: self, action: #selector(undo))
        undo.keyEquivalent = "z"
        undo.keyEquivalentModifierMask = .command
        let hint = NSTextField(labelWithString: L("Drag on the photo to draw. Text and numbers are placed with a click."))
        hint.textColor = .secondaryLabelColor
        addRow("", undo, hint)

        canvas.makeMark = { [weak self] point in
            guard let self else { return nil }
            let kind = self.tools[self.tool.selectedSegment].1
            var mark = Mark(kind: kind, start: point, end: point, color: self.colorWell.color, width: self.thickness.doubleValue)
            switch kind {
            case .text:
                guard !self.textField.stringValue.isEmpty else { return nil }
                mark.text = self.textField.stringValue
            case .number:
                mark.text = "\(self.canvas.marks.filter { $0.kind == .number }.count + 1)"
            default: break
            }
            return mark
        }
        canvas.onCommit = { [weak self] mark in self?.canvas.marks.append(mark) }
    }

    @objc private func undo() { if !canvas.marks.isEmpty { canvas.marks.removeLast() } }

    override func reset() { canvas.marks.removeAll() }

    override func apply() async throws -> [URL] {
        let image = try Images.load(source)
        guard let ctx = CGContext(data: nil, width: image.width, height: image.height, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { throw ToolError("Image too large") }
        let rect = CGRect(x: 0, y: 0, width: image.width, height: image.height)
        ctx.draw(image, in: rect)
        MarkRenderer.draw(canvas.marks, in: rect, context: ctx)
        guard let result = ctx.makeImage() else { throw ToolError("Couldn't render") }
        let (type, ext) = Images.writableType(for: source)
        let out = Paths.output(for: source, ext: ext, suffix: " annotated")
        try Images.write(result, to: out, type: type, quality: 0.95)
        return [out]
    }
}
