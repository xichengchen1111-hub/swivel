import AppKit
import QuartzCore

/// The radial menu. Each segment is a drop target; the centre is a "cancel" zone.
final class WheelView: NSView {
    var onDrop: ((WheelItem, [URL]) -> Void)?
    var subtitle = ""
    var hovered: Int? {
        didSet { if hovered != oldValue { needsDisplay = true } }
    }

    private var items: [WheelItem] = []
    private var mode: WheelMode = .convert
    private var page = 0
    private var pageCount = 1

    let outerRadius: CGFloat = 122
    private let innerRadius: CGFloat = 52
    private let rimWidth: CGFloat = 9
    private let gap: CGFloat = 7

    private var center: NSPoint { NSPoint(x: bounds.midX, y: bounds.midY) }

    override init(frame: NSRect) {
        super.init(frame: frame)
        wantsLayer = true
        registerForDraggedTypes([.fileURL])
    }

    required init?(coder: NSCoder) { fatalError() }

    func configure(items: [WheelItem], mode: WheelMode, page: Int, pageCount: Int) {
        self.items = items
        self.mode = mode
        self.page = page
        self.pageCount = pageCount
        hovered = nil
        needsDisplay = true
    }

    // MARK: Geometry

    private var step: CGFloat { 360 / CGFloat(max(items.count, 1)) }

    /// Segment i is centred at the top and proceeds clockwise.
    private func centreAngle(_ i: Int) -> CGFloat { 90 - CGFloat(i) * step }

    private func segment(at point: NSPoint) -> Int? {
        let dx = point.x - center.x, dy = point.y - center.y
        let distance = hypot(dx, dy)
        guard distance > innerRadius, distance < outerRadius + 30, !items.isEmpty else { return nil }
        let angle = atan2(dy, dx) * 180 / .pi
        var offset = (90 - angle + step / 2).truncatingRemainder(dividingBy: 360)
        if offset < 0 { offset += 360 }
        return min(Int(offset / step), items.count - 1)
    }

    private func segmentPath(_ i: Int) -> NSBezierPath {
        let half = step / 2
        let a0 = centreAngle(i) - half, a1 = centreAngle(i) + half
        let outer = outerRadius - rimWidth, inner = innerRadius + 6
        let padOut = (gap / 2) / outer * 180 / .pi
        let padIn = (gap / 2) / inner * 180 / .pi
        let path = NSBezierPath()
        if items.count == 1 {
            path.appendOval(in: NSRect(x: center.x - outer, y: center.y - outer, width: outer * 2, height: outer * 2))
            path.appendOval(in: NSRect(x: center.x - inner, y: center.y - inner, width: inner * 2, height: inner * 2))
            path.windingRule = .evenOdd
            return path
        }
        path.appendArc(withCenter: center, radius: outer, startAngle: a0 + padOut, endAngle: a1 - padOut, clockwise: false)
        path.appendArc(withCenter: center, radius: inner, startAngle: a1 - padIn, endAngle: a0 + padIn, clockwise: true)
        path.close()
        path.lineJoinStyle = .round
        return path
    }

    // MARK: Drawing

    private var isDark: Bool { effectiveAppearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua }

    override func draw(_ dirtyRect: NSRect) {
        // Frosted keycaps on clear glass, like milky glass: the desktop shows through, labels stay legible.
        let dark = isDark
        let cap = dark ? NSColor(white: 0, alpha: 0.30) : NSColor(white: 1, alpha: 0.38)
        let hubFill = dark ? NSColor(white: 0, alpha: 0.38) : NSColor(white: 1, alpha: 0.6)
        let line = dark ? NSColor(white: 1, alpha: 0.28) : NSColor(white: 1, alpha: 0.9)
        let text = dark ? NSColor.white : NSColor(white: 0.13, alpha: 1)
        let accent = NSColor.controlAccentColor.withAlphaComponent(0.92)
        labelGlow = dark ? NSColor.black.withAlphaComponent(0.6) : NSColor.white.withAlphaComponent(0.8)

        for i in items.indices {
            let path = segmentPath(i)
            let isHovered = i == hovered
            withShadow(blur: 3, alpha: dark ? 0.4 : 0.12) {
                (isHovered ? accent : cap).setFill()
                path.fill()
            }
            (isHovered ? NSColor.white.withAlphaComponent(0.9) : line).setStroke()
            path.lineWidth = 1
            path.stroke()

            let mid = ((outerRadius - rimWidth) + innerRadius + 6) / 2
            let rad = centreAngle(i) * .pi / 180
            let labelCentre = NSPoint(x: center.x + cos(rad) * mid, y: center.y + sin(rad) * mid)
            drawText(items[i].title, at: labelCentre,
                     font: .systemFont(ofSize: items.count > 6 ? 11 : 12, weight: .semibold),
                     color: isHovered ? .white : text, maxWidth: mid * sin(.pi / CGFloat(max(items.count, 2))) * 1.7,
                     glow: !isHovered)
        }

        // Centre button.
        let hub = NSBezierPath(ovalIn: NSRect(x: center.x - innerRadius, y: center.y - innerRadius,
                                              width: innerRadius * 2, height: innerRadius * 2))
        withShadow(blur: 3, alpha: dark ? 0.4 : 0.12) {
            hubFill.setFill()
            hub.fill()
        }
        line.setStroke()
        hub.lineWidth = 1
        hub.stroke()

        let headline = hovered.map { items[$0].title } ?? mode.title
        drawText(headline, at: NSPoint(x: center.x, y: center.y + 7),
                 font: .systemFont(ofSize: 12, weight: .bold), color: hovered == nil ? text : accent,
                 maxWidth: innerRadius * 1.7)
        drawText(hovered == nil ? subtitle : "drop to apply", at: NSPoint(x: center.x, y: center.y - 8),
                 font: .systemFont(ofSize: 8.5, weight: .medium), color: text.withAlphaComponent(0.6),
                 maxWidth: innerRadius * 1.6)

        if pageCount > 1 {
            let dot: CGFloat = 4, spacing: CGFloat = 7
            let width = CGFloat(pageCount - 1) * spacing
            withShadow(blur: 3, alpha: 0.5) {
                for p in 0..<pageCount {
                    let x = center.x - width / 2 + CGFloat(p) * spacing
                    (p == page ? accent : text.withAlphaComponent(0.3)).setFill()
                    NSBezierPath(ovalIn: NSRect(x: x - dot / 2, y: center.y - 26, width: dot, height: dot)).fill()
                }
            }
            drawText("\(mode.key) next", at: NSPoint(x: center.x, y: center.y - 36),
                     font: .systemFont(ofSize: 7.5, weight: .semibold), color: text.withAlphaComponent(0.55),
                     maxWidth: innerRadius * 1.5)
        }
    }

    private func withShadow(blur: CGFloat, alpha: CGFloat, _ draw: () -> Void) {
        NSGraphicsContext.saveGraphicsState()
        let shadow = NSShadow()
        shadow.shadowBlurRadius = blur
        shadow.shadowOffset = NSSize(width: 0, height: -1)
        shadow.shadowColor = NSColor.black.withAlphaComponent(alpha)
        shadow.set()
        draw()
        NSGraphicsContext.restoreGraphicsState()
    }

    private var labelGlow = NSColor.white

    private func drawText(_ string: String, at point: NSPoint, font: NSFont, color: NSColor, maxWidth: CGFloat,
                          glow: Bool = true) {
        let style = NSMutableParagraphStyle()
        style.alignment = .center
        style.lineBreakMode = .byTruncatingMiddle
        let shadow = NSShadow()
        shadow.shadowBlurRadius = 2.5
        shadow.shadowOffset = .zero
        shadow.shadowColor = glow ? labelGlow : NSColor.black.withAlphaComponent(0.35)
        var attrs: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: color, .paragraphStyle: style,
                                                    .shadow: shadow]
        var size = (string as NSString).size(withAttributes: attrs)
        if size.width > maxWidth {
            // Shrink long labels to fit rather than truncating them.
            let fitted = max(7, font.pointSize * maxWidth / size.width)
            attrs[.font] = NSFont.systemFont(ofSize: fitted, weight: .semibold)
            size = (string as NSString).size(withAttributes: attrs)
        }
        let width = min(size.width, maxWidth)
        let rect = NSRect(x: point.x - width / 2, y: point.y - size.height / 2, width: width, height: size.height)
        (string as NSString).draw(with: rect, options: [.usesLineFragmentOrigin, .truncatesLastVisibleLine], attributes: attrs)
    }

    // MARK: Drop target

    private func operation(for info: NSDraggingInfo) -> NSDragOperation {
        hovered = segment(at: convert(info.draggingLocation, from: nil))
        guard hovered != nil else { return [] }
        let mask = info.draggingSourceOperationMask
        if mask.contains(.copy) { return .copy }
        if mask.contains(.generic) { return .generic }
        return mask.contains(.move) ? .move : .every
    }

    override func draggingEntered(_ sender: NSDraggingInfo) -> NSDragOperation { operation(for: sender) }
    override func draggingUpdated(_ sender: NSDraggingInfo) -> NSDragOperation { operation(for: sender) }
    override func draggingExited(_ sender: NSDraggingInfo?) { hovered = nil }
    override func prepareForDragOperation(_ sender: NSDraggingInfo) -> Bool { hovered != nil }

    override func performDragOperation(_ sender: NSDraggingInfo) -> Bool {
        guard let index = hovered, items.indices.contains(index) else { return false }
        let urls = sender.draggingPasteboard.readObjects(forClasses: [NSURL.self],
                                                         options: [.urlReadingFileURLsOnly: true]) as? [URL] ?? []
        guard !urls.isEmpty else { return false }
        let item = items[index]
        DispatchQueue.main.async { [weak self] in self?.onDrop?(item, urls) }
        return true
    }
}

extension NSView {
    /// A small spring "pop" from 82% scale around the view's centre.
    func popIn() {
        wantsLayer = true
        guard let layer else { return }
        let c = NSPoint(x: bounds.midX, y: bounds.midY)
        func scaled(_ s: CGFloat) -> CATransform3D {
            var t = CATransform3DMakeTranslation(c.x, c.y, 0)
            t = CATransform3DScale(t, s, s, 1)
            return CATransform3DTranslate(t, -c.x, -c.y, 0)
        }
        let anim = CASpringAnimation(keyPath: "transform")
        anim.fromValue = scaled(0.82)
        anim.toValue = CATransform3DIdentity
        anim.damping = 16
        anim.stiffness = 260
        anim.duration = anim.settlingDuration
        layer.add(anim, forKey: "pop")
    }
}
