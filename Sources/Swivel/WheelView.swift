import AppKit
import QuartzCore

/// One drop target in a menu layout.
struct Slot {
    var path: NSBezierPath          // hit area and fill
    var label: NSPoint              // label centre
    var angle: CGFloat = 0          // label rotation in degrees
    var maxWidth: CGFloat
    var fontSize: CGFloat = 12
}

/// The pop-up menu. Lays its items out in the chosen `MenuStyle`; every item is a drop target.
final class WheelView: NSView {
    var onDrop: ((WheelItem, [URL]) -> Void)?
    var subtitle = ""
    var style: MenuStyle = .wheel { didSet { needsDisplay = true } }
    var hovered: Int? {
        didSet { if hovered != oldValue { needsDisplay = true } }
    }

    private(set) var items: [WheelItem] = []
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

    // MARK: Sizes per style

    static let headerHeight: CGFloat = 34
    static let listRow: CGFloat = 32

    static func bookWidth(_ i: Int) -> CGFloat { [40, 34, 44, 37, 42, 35, 39, 43][i % 8] }
    static func bookHeight(_ i: Int) -> CGFloat { [150, 132, 158, 140, 128, 154, 136, 146][i % 8] }

    /// Natural view size for a style and item count.
    static func size(for style: MenuStyle, count: Int) -> NSSize {
        let n = max(count, 1)
        switch style {
        case .wheel:
            return NSSize(width: 320, height: 320)
        case .list:
            return NSSize(width: 240, height: headerHeight + CGFloat(n) * listRow + 14)
        case .shelf:
            let books = (0..<n).reduce(CGFloat(0)) { $0 + bookWidth($1) + 5 }
            return NSSize(width: max(books + 56, 220), height: 168 + headerHeight + 24)
        case .notes:
            let cols = min(n, 4), rows = Int(ceil(Double(n) / 4))
            return NSSize(width: max(CGFloat(cols) * 108 + 28, 220), height: CGFloat(rows) * 108 + headerHeight + 28)
        case .clothesline:
            return NSSize(width: max(CGFloat(n) * 82 + 64, 240), height: 178 + headerHeight)
        }
    }

    /// The frosted backdrop behind the menu, if the style uses one (rect, corner radius).
    var backdrop: (NSRect, CGFloat)? {
        switch style {
        case .wheel:
            return (NSRect(x: center.x - outerRadius, y: center.y - outerRadius, width: outerRadius * 2, height: outerRadius * 2), outerRadius)
        case .list:
            return (bounds.insetBy(dx: 4, dy: 4), 14)
        default:
            return nil
        }
    }

    // MARK: Layout

    private var step: CGFloat { 360 / CGFloat(max(items.count, 1)) }

    /// Wheel segment i is centred at the top and proceeds clockwise.
    private func centreAngle(_ i: Int) -> CGFloat { 90 - CGFloat(i) * step }

    /// Area below the header that the items occupy.
    private var body: NSRect {
        NSRect(x: 0, y: 0, width: bounds.width, height: bounds.height - Self.headerHeight)
    }

    private func slots() -> [Slot] {
        switch style {
        case .wheel: return wheelSlots()
        case .list: return listSlots()
        case .shelf: return shelfSlots()
        case .notes: return noteSlots()
        case .clothesline: return clothesSlots()
        }
    }

    private func wheelSlots() -> [Slot] {
        let half = step / 2
        let outer = outerRadius - rimWidth, inner = innerRadius + 6
        let padOut = (gap / 2) / outer * 180 / .pi
        let padIn = (gap / 2) / inner * 180 / .pi
        let mid = (outer + inner) / 2
        return items.indices.map { i in
            let path = NSBezierPath()
            if items.count == 1 {
                path.appendOval(in: NSRect(x: center.x - outer, y: center.y - outer, width: outer * 2, height: outer * 2))
                path.appendOval(in: NSRect(x: center.x - inner, y: center.y - inner, width: inner * 2, height: inner * 2))
                path.windingRule = .evenOdd
            } else {
                let a0 = centreAngle(i) - half, a1 = centreAngle(i) + half
                path.appendArc(withCenter: center, radius: outer, startAngle: a0 + padOut, endAngle: a1 - padOut, clockwise: false)
                path.appendArc(withCenter: center, radius: inner, startAngle: a1 - padIn, endAngle: a0 + padIn, clockwise: true)
                path.close()
            }
            let rad = centreAngle(i) * .pi / 180
            return Slot(path: path, label: NSPoint(x: center.x + cos(rad) * mid, y: center.y + sin(rad) * mid),
                        maxWidth: mid * sin(.pi / CGFloat(max(items.count, 2))) * 1.7,
                        fontSize: items.count > 6 ? 11 : 12)
        }
    }

    private func listSlots() -> [Slot] {
        let top = bounds.height - Self.headerHeight
        return items.indices.map { i in
            let rect = NSRect(x: 10, y: top - CGFloat(i + 1) * Self.listRow, width: bounds.width - 20, height: Self.listRow - 4)
            return Slot(path: NSBezierPath(roundedRect: rect, xRadius: 7, yRadius: 7),
                        label: NSPoint(x: rect.minX + 14, y: rect.midY), maxWidth: rect.width - 28, fontSize: 13)
        }
    }

    private var shelfTop: CGFloat { 30 }  // top surface of the plank

    private func shelfSlots() -> [Slot] {
        let total = items.indices.reduce(CGFloat(0)) { $0 + Self.bookWidth($1) + 5 } - 5
        var x = (bounds.width - total) / 2
        return items.indices.map { i in
            let w = Self.bookWidth(i), h = Self.bookHeight(i)
            let lift: CGFloat = i == hovered ? 12 : 0
            let rect = NSRect(x: x, y: shelfTop + lift, width: w, height: h)
            x += w + 5
            return Slot(path: NSBezierPath(roundedRect: rect, xRadius: 3, yRadius: 3),
                        label: NSPoint(x: rect.midX, y: rect.midY + 6), angle: 90, maxWidth: h - 34, fontSize: 12)
        }
    }

    private func noteSlots() -> [Slot] {
        let cols = min(max(items.count, 1), 4)
        let side: CGFloat = 94, pitch: CGFloat = 108
        let gridWidth = CGFloat(cols) * pitch
        let originX = (bounds.width - gridWidth) / 2 + (pitch - side) / 2
        let top = body.maxY - 10
        return items.indices.map { i in
            let row = i / cols, col = i % cols
            let rect = NSRect(x: originX + CGFloat(col) * pitch, y: top - CGFloat(row + 1) * pitch + (pitch - side) / 2,
                              width: side, height: side)
            let tilt: CGFloat = i == hovered ? 0 : [-3, 2.5, -1.5, 3, -2.5, 1.5, -3.5, 2][i % 8]
            let scale: CGFloat = i == hovered ? 1.08 : 1
            var t = AffineTransform(translationByX: rect.midX, byY: rect.midY)
            t.rotate(byDegrees: tilt)
            t.scale(scale)
            t.translate(x: -rect.midX, y: -rect.midY)
            let path = NSBezierPath(rect: rect)
            path.transform(using: t)
            return Slot(path: path, label: NSPoint(x: rect.midX, y: rect.midY - 4), angle: tilt,
                        maxWidth: side - 16, fontSize: 15)
        }
    }

    /// Rope height at x: a gentle sag between the two posts.
    private func ropeY(_ x: CGFloat) -> CGFloat {
        let left: CGFloat = 22, right = bounds.width - 22
        let t = (x - left) / (right - left)
        return body.maxY - 18 - 16 * 4 * t * (1 - t)
    }

    private func clothesSlots() -> [Slot] {
        let pitch: CGFloat = 82
        let start = (bounds.width - CGFloat(items.count) * pitch) / 2 + pitch / 2
        return items.indices.map { i in
            let x = start + CGFloat(i) * pitch
            let hang = ropeY(x)
            let w: CGFloat = 66, h: CGFloat = 92
            let tilt: CGFloat = i == hovered ? 0 : [4, -3, 2.5, -4, 3, -2, 3.5, -3][i % 8]
            let rect = NSRect(x: x - w / 2, y: hang - 6 - h, width: w, height: h)
            var t = AffineTransform(translationByX: x, byY: hang)
            t.rotate(byDegrees: tilt)
            t.translate(x: -x, y: -hang)
            let path = NSBezierPath(roundedRect: rect, xRadius: 6, yRadius: 6)
            path.transform(using: t)
            let labelPoint = t.transform(NSPoint(x: rect.midX, y: rect.midY - 8))
            return Slot(path: path, label: labelPoint, angle: tilt, maxWidth: w - 10, fontSize: 12)
        }
    }

    private func slot(at point: NSPoint) -> Int? {
        guard !items.isEmpty else { return nil }
        if style == .wheel {
            let dx = point.x - center.x, dy = point.y - center.y
            let distance = hypot(dx, dy)
            guard distance > innerRadius, distance < outerRadius + 30 else { return nil }
            let angle = atan2(dy, dx) * 180 / .pi
            var offset = (90 - angle + step / 2).truncatingRemainder(dividingBy: 360)
            if offset < 0 { offset += 360 }
            return min(Int(offset / step), items.count - 1)
        }
        // Keep the hovered item while the pointer is still on it (shelf books lift when hovered).
        let all = slots()
        if let hovered, all.indices.contains(hovered), all[hovered].path.contains(point) { return hovered }
        return all.lastIndex { $0.path.contains(point) }
    }

    // MARK: Drawing

    private var isDark: Bool { effectiveAppearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua }
    private var accent: NSColor { NSColor.controlAccentColor.withAlphaComponent(0.92) }
    private var ink: NSColor { isDark ? .white : NSColor(white: 0.13, alpha: 1) }

    override func draw(_ dirtyRect: NSRect) {
        labelGlow = isDark ? NSColor.black.withAlphaComponent(0.6) : NSColor.white.withAlphaComponent(0.8)
        switch style {
        case .wheel: drawWheel()
        case .list: drawList()
        case .shelf: drawShelf()
        case .notes: drawNotes()
        case .clothesline: drawClothesline()
        }
    }

    private func drawWheel() {
        // Frosted keycaps on clear glass: the desktop shows through, labels stay legible.
        let dark = isDark
        let cap = dark ? NSColor(white: 0, alpha: 0.30) : NSColor(white: 1, alpha: 0.38)
        let hubFill = dark ? NSColor(white: 0, alpha: 0.38) : NSColor(white: 1, alpha: 0.6)
        let line = dark ? NSColor(white: 1, alpha: 0.28) : NSColor(white: 1, alpha: 0.9)

        for (i, slot) in slots().enumerated() {
            let isHovered = i == hovered
            withShadow(blur: 3, alpha: dark ? 0.4 : 0.12) {
                (isHovered ? accent : cap).setFill()
                slot.path.fill()
            }
            (isHovered ? NSColor.white.withAlphaComponent(0.9) : line).setStroke()
            slot.path.lineWidth = 1
            slot.path.stroke()
            drawLabel(items[i].displayTitle, slot, color: isHovered ? .white : ink, glow: !isHovered)
        }

        let hub = NSBezierPath(ovalIn: NSRect(x: center.x - innerRadius, y: center.y - innerRadius,
                                              width: innerRadius * 2, height: innerRadius * 2))
        withShadow(blur: 3, alpha: dark ? 0.4 : 0.12) {
            hubFill.setFill()
            hub.fill()
        }
        line.setStroke()
        hub.lineWidth = 1
        hub.stroke()

        let headline = hovered.map { items[$0].displayTitle } ?? mode.title
        drawText(headline, at: NSPoint(x: center.x, y: center.y + 7),
                 font: .systemFont(ofSize: 12, weight: .bold), color: hovered == nil ? ink : accent,
                 maxWidth: innerRadius * 1.7)
        drawText(hovered == nil ? subtitle : L("drop to apply"), at: NSPoint(x: center.x, y: center.y - 8),
                 font: .systemFont(ofSize: 8.5, weight: .medium), color: ink.withAlphaComponent(0.6),
                 maxWidth: innerRadius * 1.6)
        drawPageDots(at: NSPoint(x: center.x, y: center.y - 26), hintBelow: true)
    }

    /// Title, file name and page dots above the items (every style but the wheel).
    private func drawHeader(onGlass: Bool) {
        let rect = NSRect(x: 0, y: bounds.height - Self.headerHeight, width: bounds.width, height: Self.headerHeight)
        let title = hovered.map { "\(mode.title): \(items[$0].displayTitle)" } ?? mode.title
        let detail = hovered == nil ? subtitle : L("drop to apply")
        let attrs: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: 13, weight: .bold)]
        let titleWidth = (title as NSString).size(withAttributes: attrs).width
        let detailWidth = min((detail as NSString).size(withAttributes: [.font: NSFont.systemFont(ofSize: 11)]).width, 150)
        let dotsWidth: CGFloat = pageCount > 1 ? CGFloat(pageCount) * 7 + 44 : 0
        let capsuleWidth = min(titleWidth + detailWidth + dotsWidth + 34, bounds.width - 8)
        let capsule = NSRect(x: rect.midX - capsuleWidth / 2, y: rect.midY - 13, width: capsuleWidth, height: 26)

        if !onGlass {
            withShadow(blur: 6, alpha: isDark ? 0.5 : 0.18) {
                (isDark ? NSColor(white: 0.12, alpha: 0.86) : NSColor(white: 1, alpha: 0.88)).setFill()
                NSBezierPath(roundedRect: capsule, xRadius: 13, yRadius: 13).fill()
            }
        }
        var x = capsule.minX + 13
        drawText(title, at: NSPoint(x: x + titleWidth / 2, y: capsule.midY),
                 font: .systemFont(ofSize: 13, weight: .bold), color: hovered == nil ? ink : NSColor.controlAccentColor,
                 maxWidth: titleWidth, glow: false)
        x += titleWidth + 8
        drawText(detail, at: NSPoint(x: x + detailWidth / 2, y: capsule.midY),
                 font: .systemFont(ofSize: 11), color: ink.withAlphaComponent(0.55), maxWidth: detailWidth, glow: false)
        if pageCount > 1 {
            drawPageDots(at: NSPoint(x: capsule.maxX - dotsWidth / 2 - 6, y: capsule.midY), hintBelow: false)
        }
    }

    private func drawPageDots(at point: NSPoint, hintBelow: Bool) {
        guard pageCount > 1 else { return }
        let dot: CGFloat = 4, spacing: CGFloat = 7
        let width = CGFloat(pageCount - 1) * spacing
        let dotsX = hintBelow ? point.x - width / 2 : point.x - width / 2 - 16
        for p in 0..<pageCount {
            let x = dotsX + CGFloat(p) * spacing
            (p == page ? accent : ink.withAlphaComponent(0.3)).setFill()
            NSBezierPath(ovalIn: NSRect(x: x - dot / 2, y: point.y - dot / 2, width: dot, height: dot)).fill()
        }
        let hint = L("%@ next", mode.key)
        if hintBelow {
            drawText(hint, at: NSPoint(x: point.x, y: point.y - 10), font: .systemFont(ofSize: 7.5, weight: .semibold),
                     color: ink.withAlphaComponent(0.55), maxWidth: 80)
        } else {
            drawText(hint, at: NSPoint(x: dotsX + width + 22, y: point.y), font: .systemFont(ofSize: 9, weight: .semibold),
                     color: ink.withAlphaComponent(0.55), maxWidth: 50, glow: false)
        }
    }

    private func drawList() {
        let dark = isDark
        // Light tint over the glass backdrop so rows read like a native context menu.
        if let (rect, radius) = backdrop {
            (dark ? NSColor(white: 0, alpha: 0.25) : NSColor(white: 1, alpha: 0.35)).setFill()
            let shape = NSBezierPath(roundedRect: rect, xRadius: radius, yRadius: radius)
            shape.fill()
            NSColor.white.withAlphaComponent(dark ? 0.15 : 0.7).setStroke()
            shape.lineWidth = 0.5
            shape.stroke()
        }
        drawHeader(onGlass: true)
        NSColor.separatorColor.setFill()
        NSRect(x: 16, y: bounds.height - Self.headerHeight - 1, width: bounds.width - 32, height: 1).fill()

        for (i, slot) in slots().enumerated() {
            let isHovered = i == hovered
            if isHovered {
                accent.setFill()
                slot.path.fill()
            }
            let style = NSMutableParagraphStyle()
            style.lineBreakMode = .byTruncatingTail
            let attrs: [NSAttributedString.Key: Any] = [
                .font: NSFont.systemFont(ofSize: slot.fontSize, weight: .medium),
                .foregroundColor: isHovered ? NSColor.white : ink, .paragraphStyle: style,
            ]
            let height = ("A" as NSString).size(withAttributes: attrs).height
            (items[i].displayTitle as NSString).draw(with: NSRect(x: slot.label.x, y: slot.label.y - height / 2,
                                                           width: slot.maxWidth, height: height),
                                              options: [.usesLineFragmentOrigin, .truncatesLastVisibleLine], attributes: attrs)
        }
    }

    private func drawShelf() {
        let palette: [NSColor] = [
            NSColor(srgbRed: 0.55, green: 0.16, blue: 0.18, alpha: 1), NSColor(srgbRed: 0.15, green: 0.25, blue: 0.45, alpha: 1),
            NSColor(srgbRed: 0.18, green: 0.38, blue: 0.27, alpha: 1), NSColor(srgbRed: 0.78, green: 0.58, blue: 0.2, alpha: 1),
            NSColor(srgbRed: 0.16, green: 0.45, blue: 0.48, alpha: 1), NSColor(srgbRed: 0.42, green: 0.22, blue: 0.42, alpha: 1),
            NSColor(srgbRed: 0.33, green: 0.36, blue: 0.4, alpha: 1), NSColor(srgbRed: 0.66, green: 0.3, blue: 0.16, alpha: 1),
        ]
        drawHeader(onGlass: false)

        // Wooden plank with brackets.
        let plank = NSRect(x: 10, y: shelfTop - 16, width: bounds.width - 20, height: 16)
        withShadow(blur: 10, alpha: 0.35) {
            NSGradient(starting: NSColor(srgbRed: 0.72, green: 0.52, blue: 0.33, alpha: 1),
                       ending: NSColor(srgbRed: 0.5, green: 0.33, blue: 0.19, alpha: 1))!
                .draw(in: NSBezierPath(roundedRect: plank, xRadius: 3, yRadius: 3), angle: -90)
        }
        NSColor(srgbRed: 0.85, green: 0.68, blue: 0.48, alpha: 1).setFill()
        NSRect(x: plank.minX + 2, y: plank.maxY - 2, width: plank.width - 4, height: 2).fill()
        for bx in [plank.minX + 22, plank.maxX - 34] {
            NSColor(srgbRed: 0.35, green: 0.35, blue: 0.38, alpha: 1).setFill()
            NSBezierPath(roundedRect: NSRect(x: bx, y: plank.minY - 12, width: 12, height: 14), xRadius: 2, yRadius: 2).fill()
        }

        for (i, slot) in slots().enumerated() {
            let isHovered = i == hovered
            let color = isHovered ? NSColor.controlAccentColor : palette[i % palette.count]
            withShadow(blur: 4, alpha: 0.35) {
                color.setFill()
                slot.path.fill()
            }
            let rect = slot.path.bounds
            // Spine details: darker edges, two gilt bands.
            NSGradient(colors: [NSColor.black.withAlphaComponent(0.25), .clear, .clear, NSColor.black.withAlphaComponent(0.3)])!
                .draw(in: slot.path, angle: 0)
            NSColor(srgbRed: 0.95, green: 0.82, blue: 0.5, alpha: isHovered ? 0.9 : 0.75).setFill()
            NSRect(x: rect.minX + 3, y: rect.maxY - 16, width: rect.width - 6, height: 2).fill()
            NSRect(x: rect.minX + 3, y: rect.minY + 14, width: rect.width - 6, height: 2).fill()
            drawLabel(items[i].displayTitle, slot, color: .white, glow: false, weight: .bold)
        }
    }

    private func drawNotes() {
        let colors: [NSColor] = [
            NSColor(srgbRed: 1, green: 0.92, blue: 0.45, alpha: 1), NSColor(srgbRed: 1, green: 0.72, blue: 0.8, alpha: 1),
            NSColor(srgbRed: 0.68, green: 0.93, blue: 0.7, alpha: 1), NSColor(srgbRed: 0.66, green: 0.85, blue: 1, alpha: 1),
            NSColor(srgbRed: 1, green: 0.78, blue: 0.5, alpha: 1), NSColor(srgbRed: 0.85, green: 0.76, blue: 1, alpha: 1),
        ]
        drawHeader(onGlass: false)
        for (i, slot) in slots().enumerated() {
            let isHovered = i == hovered
            withShadow(blur: isHovered ? 12 : 6, alpha: 0.3) {
                colors[i % colors.count].setFill()
                slot.path.fill()
            }
            // Slightly darker bottom edge, like a curled note.
            NSGradient(colors: [.clear, NSColor.black.withAlphaComponent(0.08)])!.draw(in: slot.path, angle: -90)
            if isHovered {
                NSColor.controlAccentColor.setStroke()
                slot.path.lineWidth = 3
                slot.path.stroke()
            }
            // A strip of tape across the top.
            let box = slot.path.bounds
            let tape = NSBezierPath(rect: NSRect(x: box.midX - 18, y: box.maxY - 12, width: 36, height: 14))
            var t = AffineTransform(translationByX: box.midX, byY: box.maxY - 5)
            t.rotate(byDegrees: slot.angle - 4)
            t.translate(x: -box.midX, y: -(box.maxY - 5))
            tape.transform(using: t)
            NSColor(white: 1, alpha: 0.55).setFill()
            tape.fill()
            let font = NSFont(name: "Noteworthy-Bold", size: slot.fontSize) ?? .systemFont(ofSize: slot.fontSize, weight: .semibold)
            drawLabel(items[i].displayTitle, slot, color: NSColor(white: 0.15, alpha: 1), glow: false, font: font)
        }
    }

    private func drawClothesline() {
        drawHeader(onGlass: false)
        let left: CGFloat = 22, right = bounds.width - 22

        // Posts.
        for x in [left, right] {
            withShadow(blur: 4, alpha: 0.3) {
                NSColor(srgbRed: 0.55, green: 0.38, blue: 0.24, alpha: 1).setFill()
                NSBezierPath(roundedRect: NSRect(x: x - 4, y: 6, width: 8, height: ropeY(x) - 6 + 8), xRadius: 3, yRadius: 3).fill()
            }
        }
        // Rope.
        let rope = NSBezierPath()
        rope.move(to: NSPoint(x: left, y: ropeY(left)))
        for x in stride(from: left, through: right, by: 4) { rope.line(to: NSPoint(x: x, y: ropeY(x))) }
        rope.lineWidth = 2.5
        rope.lineCapStyle = .round
        withShadow(blur: 3, alpha: 0.35) {
            NSColor(srgbRed: 0.93, green: 0.9, blue: 0.84, alpha: 1).setStroke()
            rope.stroke()
        }

        for (i, slot) in slots().enumerated() {
            let isHovered = i == hovered
            let kraft = isHovered ? NSColor.controlAccentColor : NSColor(srgbRed: 0.86, green: 0.74, blue: 0.56, alpha: 1)
            withShadow(blur: isHovered ? 10 : 5, alpha: 0.32) {
                kraft.setFill()
                slot.path.fill()
            }
            NSColor.black.withAlphaComponent(0.12).setStroke()
            slot.path.lineWidth = 1
            slot.path.stroke()

            // Peg where the tag meets the rope.
            let x = slot.path.bounds.midX
            let pegTop = ropeY(x) + 7
            withShadow(blur: 2, alpha: 0.35) {
                NSColor(srgbRed: 0.76, green: 0.56, blue: 0.36, alpha: 1).setFill()
                NSBezierPath(roundedRect: NSRect(x: x - 4, y: pegTop - 24, width: 8, height: 24), xRadius: 2, yRadius: 2).fill()
            }
            NSColor(white: 0.55, alpha: 1).setFill()
            NSRect(x: x - 4, y: pegTop - 12, width: 8, height: 2).fill()
            drawLabel(items[i].displayTitle, slot, color: isHovered ? .white : NSColor(white: 0.18, alpha: 1), glow: false, weight: .bold)
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

    /// Draws a slot's label at its centre, rotated by the slot's angle.
    private func drawLabel(_ string: String, _ slot: Slot, color: NSColor, glow: Bool,
                           weight: NSFont.Weight = .semibold, font: NSFont? = nil) {
        NSGraphicsContext.saveGraphicsState()
        let t = NSAffineTransform()
        t.translateX(by: slot.label.x, yBy: slot.label.y)
        t.rotate(byDegrees: slot.angle)
        t.concat()
        drawText(string, at: .zero, font: font ?? .systemFont(ofSize: slot.fontSize, weight: weight), color: color,
                 maxWidth: slot.maxWidth, glow: glow)
        NSGraphicsContext.restoreGraphicsState()
    }

    private func drawText(_ string: String, at point: NSPoint, font: NSFont, color: NSColor, maxWidth: CGFloat,
                          glow: Bool = true) {
        let style = NSMutableParagraphStyle()
        style.alignment = .center
        style.lineBreakMode = .byTruncatingMiddle
        var attrs: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: color, .paragraphStyle: style]
        if glow {
            let shadow = NSShadow()
            shadow.shadowBlurRadius = 2.5
            shadow.shadowOffset = .zero
            shadow.shadowColor = labelGlow
            attrs[.shadow] = shadow
        }
        var size = (string as NSString).size(withAttributes: attrs)
        if size.width > maxWidth, maxWidth > 0 {
            // Shrink long labels to fit rather than truncating them.
            let fitted = max(7, font.pointSize * maxWidth / size.width)
            attrs[.font] = NSFont(descriptor: font.fontDescriptor, size: fitted) ?? font
            size = (string as NSString).size(withAttributes: attrs)
        }
        let width = min(size.width, maxWidth)
        let rect = NSRect(x: point.x - width / 2, y: point.y - size.height / 2, width: width, height: size.height)
        (string as NSString).draw(with: rect, options: [.usesLineFragmentOrigin, .truncatesLastVisibleLine], attributes: attrs)
    }

    // MARK: Drop target

    private func operation(for info: NSDraggingInfo) -> NSDragOperation {
        hovered = slot(at: convert(info.draggingLocation, from: nil))
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
