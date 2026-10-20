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
    private var isDark: Bool { effectiveAppearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua }

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

    override func draw(_ dirtyRect: NSRect) {
        let dark = isDark
        // Translucent keycaps over the glass disc that sits behind this view.
        let segTop = dark ? NSColor(white: 1, alpha: 0.16) : NSColor(white: 1, alpha: 0.62)
        let segBottom = dark ? NSColor(white: 1, alpha: 0.07) : NSColor(white: 1, alpha: 0.34)
        let text = dark ? NSColor(white: 0.95, alpha: 1) : NSColor(white: 0.18, alpha: 1)
        let accent = NSColor(srgbRed: 1.0, green: 0.47, blue: 0.12, alpha: 0.92)

        // Segments, drawn like keycaps.
        let gradient = NSGradient(starting: segTop, ending: segBottom)!
        let hoverGradient = NSGradient(starting: accent.blended(withFraction: 0.15, of: .white)!, ending: accent)!
        for i in items.indices {
            let path = segmentPath(i)
            let isHovered = i == hovered
            NSGraphicsContext.saveGraphicsState()
            let capShadow = NSShadow()
            capShadow.shadowBlurRadius = 1.5
            capShadow.shadowOffset = NSSize(width: 0, height: -1.5)
            capShadow.shadowColor = NSColor.black.withAlphaComponent(dark ? 0.35 : 0.10)
            capShadow.set()
            (isHovered ? accent : segBottom).setFill()
            path.fill()
            NSGraphicsContext.restoreGraphicsState()
            (isHovered ? hoverGradient : gradient).draw(in: path, angle: -90)
            NSColor.white.withAlphaComponent(dark ? 0.18 : 0.75).setStroke()
            path.lineWidth = 1
            path.stroke()

            let mid = ((outerRadius - rimWidth) + innerRadius + 6) / 2
            let rad = centreAngle(i) * .pi / 180
            let labelCentre = NSPoint(x: center.x + cos(rad) * mid, y: center.y + sin(rad) * mid)
            drawText(items[i].title.uppercased(), at: labelCentre,
                     font: .systemFont(ofSize: items.count > 6 ? 10 : 11, weight: .bold),
                     color: isHovered ? .white : text, maxWidth: mid * sin(.pi / CGFloat(max(items.count, 2))) * 1.7)
        }

        // Centre button.
        let hub = NSRect(x: center.x - innerRadius, y: center.y - innerRadius,
                         width: innerRadius * 2, height: innerRadius * 2)
        NSGraphicsContext.saveGraphicsState()
        let hubShadow = NSShadow()
        hubShadow.shadowBlurRadius = 3
        hubShadow.shadowOffset = NSSize(width: 0, height: -1)
        hubShadow.shadowColor = NSColor.black.withAlphaComponent(0.12)
        hubShadow.set()
        (dark ? NSColor(white: 0, alpha: 0.28) : NSColor(white: 1, alpha: 0.72)).setFill()
        NSBezierPath(ovalIn: hub).fill()
        NSGraphicsContext.restoreGraphicsState()

        let headline = hovered.map { items[$0].title } ?? mode.title
        drawText(headline, at: NSPoint(x: center.x, y: center.y + 7),
                 font: .systemFont(ofSize: 12, weight: .semibold), color: hovered == nil ? text : accent,
                 maxWidth: innerRadius * 1.7)
        drawText(hovered == nil ? subtitle : "drop to apply", at: NSPoint(x: center.x, y: center.y - 8),
                 font: .systemFont(ofSize: 8.5, weight: .regular), color: text.withAlphaComponent(0.55),
                 maxWidth: innerRadius * 1.6)

        if pageCount > 1 {
            let dot: CGFloat = 4, spacing: CGFloat = 7
            let width = CGFloat(pageCount - 1) * spacing
            for p in 0..<pageCount {
                let x = center.x - width / 2 + CGFloat(p) * spacing
                (p == page ? accent : text.withAlphaComponent(0.25)).setFill()
                NSBezierPath(ovalIn: NSRect(x: x - dot / 2, y: center.y - 26, width: dot, height: dot)).fill()
            }
            drawText("\(mode.key) next", at: NSPoint(x: center.x, y: center.y - 36),
                     font: .systemFont(ofSize: 7.5, weight: .medium), color: text.withAlphaComponent(0.45),
                     maxWidth: innerRadius * 1.5)
        }
    }

    private func drawText(_ string: String, at point: NSPoint, font: NSFont, color: NSColor, maxWidth: CGFloat) {
        let style = NSMutableParagraphStyle()
        style.alignment = .center
        style.lineBreakMode = .byTruncatingMiddle
        let attrs: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: color, .paragraphStyle: style]
        let size = (string as NSString).size(withAttributes: attrs)
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
