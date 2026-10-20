import AppKit

/// Combines several images into a grid, row, column or featured layout.
final class CollageEditor: Editor {
    private let preview = ImagePreview()
    private let urls: [URL]
    private var thumbs: [CGImage] = []
    private let layouts = ["Grid", "Row", "Column", "Featured"]
    private var layout: NSSegmentedControl!
    private var spacing: NSSlider!
    private var corners: NSSlider!
    private let colorWell = NSColorWell(style: .minimal)

    init(urls: [URL]) throws {
        self.urls = urls
        super.init(title: "Make a Collage", source: urls[0], canvas: preview)
        window.subtitle = "\(urls.count) images"
        thumbs = try urls.map { try Images.load($0, maxPixels: 700) }
        layout = segments(layouts)
        addRow("Layout", layout)
        spacing = slider(0, 0.06, 0.02)
        addRow("Spacing", spacing)
        corners = slider(0, 0.15, 0.03)
        addRow("Corners", corners)
        colorWell.color = .white
        colorWell.target = self
        colorWell.action = #selector(controlChanged)
        addRow("Background", colorWell)
    }

    /// Cell frames in a unit-width canvas (y up), plus the canvas height.
    private func cells(for sizes: [CGSize], gap: CGFloat) -> ([CGRect], CGFloat) {
        let n = sizes.count
        func aspect(_ i: Int) -> CGFloat { sizes[i].width / max(sizes[i].height, 1) }

        switch layout.selectedSegment {
        case 1:  // Row: equal heights, widths follow each image's aspect.
            let total = (0..<n).reduce(0) { $0 + aspect($1) }
            let h = (1 - gap * CGFloat(n + 1)) / total
            var x = gap, frames: [CGRect] = []
            for i in 0..<n {
                frames.append(CGRect(x: x, y: gap, width: h * aspect(i), height: h))
                x += h * aspect(i) + gap
            }
            return (frames, h + gap * 2)
        case 2:  // Column: equal widths stacked top to bottom.
            let w = 1 - gap * 2
            let heights = (0..<n).map { w / aspect($0) }
            var y = gap, frames: [CGRect] = []
            for i in (0..<n).reversed() {
                frames.insert(CGRect(x: gap, y: y, width: w, height: heights[i]), at: 0)
                y += heights[i] + gap
            }
            return (frames, y)
        case 3 where n > 1:  // Featured: first image large on top, the rest in a row beneath.
            let big = CGRect(x: gap, y: 0, width: 1 - gap * 2, height: (1 - gap * 2) / aspect(0))
            let rest = n - 1
            let side = (1 - gap * CGFloat(rest + 1)) / CGFloat(rest)
            var frames: [CGRect] = []
            for i in 0..<rest { frames.append(CGRect(x: gap + CGFloat(i) * (side + gap), y: gap, width: side, height: side)) }
            let bigFrame = big.offsetBy(dx: 0, dy: side + gap * 2)
            return ([bigFrame] + frames, bigFrame.maxY + gap)
        default:  // Grid of square cells.
            let cols = Int(ceil(sqrt(Double(n))))
            let rows = Int(ceil(Double(n) / Double(cols)))
            let side = (1 - gap * CGFloat(cols + 1)) / CGFloat(cols)
            let height = gap + CGFloat(rows) * (side + gap)
            let frames = (0..<n).map { i -> CGRect in
                let row = i / cols, col = i % cols
                return CGRect(x: gap + CGFloat(col) * (side + gap), y: height - CGFloat(row + 1) * (side + gap),
                              width: side, height: side)
            }
            return (frames, height)
        }
    }

    private func render(_ images: [CGImage], width: CGFloat) -> CGImage? {
        let sizes = images.map { CGSize(width: $0.width, height: $0.height) }
        let (frames, unitHeight) = cells(for: sizes, gap: spacing.doubleValue)
        let height = unitHeight * width
        guard let ctx = CGContext(data: nil, width: Int(width), height: Int(height), bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        ctx.setFillColor(colorWell.color.cgColor)
        ctx.fill(CGRect(x: 0, y: 0, width: width, height: height))
        ctx.interpolationQuality = .high
        for (image, unit) in zip(images, frames) {
            let cell = CGRect(x: unit.minX * width, y: unit.minY * width, width: unit.width * width, height: unit.height * width)
            // Fill the cell, cropping the image's overflow.
            let scale = max(cell.width / CGFloat(image.width), cell.height / CGFloat(image.height))
            let drawn = CGSize(width: CGFloat(image.width) * scale, height: CGFloat(image.height) * scale)
            let r = corners.doubleValue * min(cell.width, cell.height)
            ctx.saveGState()
            ctx.addPath(CGPath(roundedRect: cell, cornerWidth: r, cornerHeight: r, transform: nil))
            ctx.clip()
            ctx.draw(image, in: CGRect(x: cell.midX - drawn.width / 2, y: cell.midY - drawn.height / 2,
                                       width: drawn.width, height: drawn.height))
            ctx.restoreGState()
        }
        return ctx.makeImage()
    }

    override func update() { preview.image = render(thumbs, width: 1200) }

    override func reset() {
        layout.selectedSegment = 0
        spacing.doubleValue = 0.02
        corners.doubleValue = 0.03
        colorWell.color = .white
    }

    override func apply() async throws -> [URL] {
        let full = try urls.map { try Images.load($0, maxPixels: 3000) }
        guard let result = render(full, width: 4000) else { throw ToolError("Collage too large") }
        let out = Paths.unique(urls[0].deletingLastPathComponent().appendingPathComponent("Collage.png"))
        try Images.write(result, to: out, type: .png, quality: 1)
        return [out]
    }
}
