import AppKit
import CoreImage
import ImageIO

enum QRSeason: String, Codable, CaseIterable, Sendable {
    case spring, summer, autumn
    var title: String { switch self { case .spring: L("Spring"); case .summer: L("Summer"); case .autumn: L("Autumn") } }
}

struct SavedQRCode: Codable, Equatable, Identifiable, Sendable {
    var id: UUID = UUID()
    var name: String
    var value: String
    var collection: String = ""
    var season: QRSeason = .summer
    var created: Date = Date()
    var isValid: Bool {
        !name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && name.count <= 120 &&
        !value.isEmpty && value.utf8.count <= 2000 && collection.count <= 80
    }
}

enum QRImage {
    static func raw(_ value: String) throws -> CIImage {
        guard !value.isEmpty, value.utf8.count <= 2000, let filter = CIFilter(name: "CIQRCodeGenerator") else {
            throw ToolError(L("Enter up to 2,000 bytes of QR content."))
        }
        filter.setValue(Data(value.utf8), forKey: "inputMessage"); filter.setValue("M", forKey: "inputCorrectionLevel")
        guard let image = filter.outputImage else { throw ToolError(L("This content is too long for a QR code.")) }
        return image
    }
    /// Integer scaling and a four-module white quiet zone keep the exported QR reliably scannable.
    static func cgImage(_ value: String, scale: Int = 8) throws -> CGImage {
        let code = try raw(value)
        let extent = code.extent.insetBy(dx: -4, dy: -4)
        let padded = code.composited(over: CIImage(color: .white).cropped(to: extent))
        let image = padded.transformed(by: CGAffineTransform(scaleX: CGFloat(scale), y: CGFloat(scale)))
        guard let cg = CIContext(options: [.useSoftwareRenderer: false]).createCGImage(image, from: image.extent) else {
            throw ToolError(L("Could not generate the QR image."))
        }
        return cg
    }
    static func png(_ value: String) throws -> Data {
        let buffer = NSMutableData()
        guard let dest = CGImageDestinationCreateWithData(buffer, "public.png" as CFString, 1, nil) else { throw ToolError(L("Could not generate the QR image.")) }
        CGImageDestinationAddImage(dest, try cgImage(value), nil)
        guard CGImageDestinationFinalize(dest) else { throw ToolError(L("Could not generate the QR image.")) }
        return buffer as Data
    }
}

/// Only explicitly saved content is retained. Originals are not needed after a QR is imported.
final class QRLibraryStore {
    static let changed = Notification.Name("SwivelQRLibraryChanged")
    /// Set only by the isolated --tool preview entry point; never changes production preferences.
    static var previewDirectory: URL?
    let file: URL
    init(directory: URL? = nil) {
        let root = directory ?? Self.previewDirectory ?? FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Swivel", isDirectory: true).appendingPathComponent("QRCodes", isDirectory: true)
        file = root.appendingPathComponent("library-v1.json")
    }
    func load() throws -> [SavedQRCode] {
        guard FileManager.default.fileExists(atPath: file.path) else { return [] }
        let values = try JSONDecoder().decode([SavedQRCode].self, from: Data(contentsOf: file))
        guard values.allSatisfy(\.isValid), Set(values.map(\.id)).count == values.count else { throw ToolError(L("The QR library could not be read. Its saved data was kept.")) }
        return values
    }
    func save(_ values: [SavedQRCode]) throws {
        guard values.allSatisfy(\.isValid), Set(values.map(\.id)).count == values.count else { throw ToolError(L("Check the code name and content.")) }
        let data = try JSONEncoder().encode(values)
        let folder = file.deletingLastPathComponent()
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        try data.write(to: file, options: .atomic)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: file.path)
        NotificationCenter.default.post(name: Self.changed, object: file)
    }
    @discardableResult func add(_ value: SavedQRCode) throws -> SavedQRCode {
        var values = try load()
        if let existing = values.first(where: { $0.value == value.value && $0.collection == value.collection }) { return existing }
        values.insert(value, at: 0); try save(values); return value
    }
    /// Undo only this window's changes. Files saved or edited by another library window remain intact.
    func revert(before: [SavedQRCode], after: [SavedQRCode]) throws {
        let old = Dictionary(uniqueKeysWithValues: before.map { ($0.id, $0) })
        let new = Dictionary(uniqueKeysWithValues: after.map { ($0.id, $0) })
        let affected = Set(old.keys).union(new.keys).filter { old[$0] != new[$0] }
        var current = try load()
        for id in affected {
            guard current.first(where: { $0.id == id }) == new[id] else {
                throw ToolError(L("This code changed in another window. Refresh before undoing."))
            }
        }
        current.removeAll { affected.contains($0.id) }
        current += before.filter { affected.contains($0.id) }
        try save(current.sorted { $0.created > $1.created })
    }
}

/// Original native illustration inspired by the tree-to-QR interaction, not a copy of the site's assets.
/// Drawing uses deterministic positions from the payload; a spring flips between the tree and the plain code.
@MainActor
final class QRTreeView: NSView {
    private(set) var entry: SavedQRCode?
    private var image: CGImage?
    private var flip = Spring(0, response: 0.4, damping: 1)
    private lazy var driver: FrameDriver = {
        let driver = FrameDriver(view: self)
        driver.onFrame = { [weak self] dt in
            guard let self else { return false }
            if Motion.reduced { self.flip.snap(self.flip.target) } else { self.flip.step(dt, tuning: Prefs.interaction.tools) }
            self.needsDisplay = true; return !self.flip.isSettled
        }
        return driver
    }()
    var showsCode: Bool { flip.target == 1 }
    var onToggle: ((Bool) -> Void)?
    override var acceptsFirstResponder: Bool { entry != nil }
    override init(frame: NSRect) {
        super.init(frame: frame); setAccessibilityElement(true); setAccessibilityRole(.button)
        setAccessibilityLabel(L("Show QR code or tree"))
    }
    required init?(coder: NSCoder) { fatalError() }
    func display(_ entry: SavedQRCode?) throws {
        self.image = try entry.map { try QRImage.cgImage($0.value) }; self.entry = entry
        setAccessibilityElement(entry != nil)
        flip.snap(0); driver.stop(); needsDisplay = true
    }
    func setShowsCode(_ value: Bool) {
        flip.target = value ? 1 : 0
        if Motion.reduced || window == nil { flip.snap(flip.target); needsDisplay = true } else { driver.start() }
        onToggle?(value)
    }
    override func mouseDown(with event: NSEvent) { window?.makeFirstResponder(self); toggle() }
    override func keyDown(with event: NSEvent) {
        if event.keyCode == 49 || event.keyCode == 36 { toggle() } else { super.keyDown(with: event) }
    }
    override func accessibilityPerformPress() -> Bool { toggle(); return true }
    override func viewDidMoveToWindow() { if window == nil { driver.stop() }; super.viewDidMoveToWindow() }
    private func toggle() { guard entry != nil else { return }; Feedback.press(); setShowsCode(!showsCode) }
    override func draw(_ dirtyRect: NSRect) {
        NSColor.controlBackgroundColor.setFill(); NSBezierPath(roundedRect: bounds, xRadius: 12, yRadius: 12).fill()
        guard let entry, let image, let ctx = NSGraphicsContext.current?.cgContext else { return }
        let phase = min(1, max(0, flip.value)), scale = abs(cos(phase * .pi))
        ctx.saveGState(); ctx.translateBy(x: bounds.midX, y: bounds.midY); ctx.scaleBy(x: 1, y: max(0.01, scale)); ctx.translateBy(x: -bounds.midX, y: -bounds.midY)
        if phase >= 0.5 {
            let edge = min(bounds.width, bounds.height) - 40
            let size = max(1, floor(edge / CGFloat(image.width))) * CGFloat(image.width)
            let drawSize = min(edge, size)
            let rect = CGRect(x: bounds.midX - drawSize / 2, y: bounds.midY - drawSize / 2, width: drawSize, height: drawSize)
            ctx.interpolationQuality = .none; ctx.draw(image, in: rect)
        } else { drawTree(entry, context: ctx) }
        ctx.restoreGState()
    }

    static func seed(_ text: String) -> UInt64 { text.utf8.reduce(UInt64(14695981039346656037)) { ($0 ^ UInt64($1)) &* 1099511628211 } }
    private func drawTree(_ entry: SavedQRCode, context ctx: CGContext) {
        var state = Self.seed(entry.value)
        func random() -> CGFloat { state = state &* 6364136223846793005 &+ 1442695040888963407; return CGFloat((state >> 33) & 0xffff) / 65535 }
        let unit = min(bounds.width / 400, bounds.height / 400)
        ctx.saveGState(); ctx.translateBy(x: bounds.midX, y: bounds.minY + bounds.height * 0.29); ctx.scaleBy(x: unit, y: unit)
        func diamond(_ x: CGFloat, _ y: CGFloat, _ width: CGFloat, _ height: CGFloat, _ color: NSColor) {
            color.setFill(); let p = NSBezierPath(); p.move(to: NSPoint(x: x, y: y + height / 2)); p.line(to: NSPoint(x: x + width / 2, y: y)); p.line(to: NSPoint(x: x, y: y - height / 2)); p.line(to: NSPoint(x: x - width / 2, y: y)); p.close(); p.fill()
        }
        diamond(0, -6, 320, 155, NSColor(srgbRed: 0.40, green: 0.48, blue: 0.35, alpha: 1))
        diamond(0, 0, 320, 150, NSColor(srgbRed: 0.83, green: 0.86, blue: 0.74, alpha: 1))
        // A small QR-like garden pattern is decorative; the other face is the actual unmodified QR.
        for y in -6...6 { for x in -6...6 where random() > 0.45 {
            diamond(CGFloat(x - y) * 11, CGFloat(x + y) * 5.2, 20, 10,
                    NSColor(srgbRed: 0.62, green: 0.71, blue: 0.53, alpha: 0.7))
        } }
        let brown = NSColor(srgbRed: 0.43, green: 0.29, blue: 0.19, alpha: 1)
        brown.setStroke(); let trunk = NSBezierPath(); trunk.move(to: .zero); trunk.curve(to: NSPoint(x: 4, y: 175), controlPoint1: NSPoint(x: -13, y: 55), controlPoint2: NSPoint(x: 16, y: 120)); trunk.lineWidth = 12; trunk.lineCapStyle = .round; trunk.stroke()
        var leaf: NSColor
        switch entry.season {
        case .spring: leaf = NSColor(srgbRed: 0.94, green: 0.57, blue: 0.67, alpha: 1)
        case .summer: leaf = NSColor(srgbRed: 0.35, green: 0.64, blue: 0.28, alpha: 1)
        case .autumn: leaf = NSColor(srgbRed: 0.90, green: 0.49, blue: 0.16, alpha: 1)
        }
        for branch in 0..<13 {
            let side: CGFloat = branch % 2 == 0 ? -1 : 1
            let y = CGFloat(branch) * 9 + 43, x = side * (32 + random() * 57)
            let end = NSPoint(x: x, y: y + 36 + random() * 28)
            brown.setStroke(); let path = NSBezierPath(); path.move(to: NSPoint(x: 1, y: y)); path.line(to: end); path.lineWidth = 4; path.lineCapStyle = .round; path.stroke()
            for _ in 0..<28 {
                let angle = random() * .pi * 2, radius = sqrt(random()) * 37
                let px = end.x + cos(angle) * radius, py = end.y + sin(angle) * radius * 0.65
                let color = leaf.mix(.white, random() * 0.3)
                diamond(px, py, 10 + random() * 11, 7 + random() * 8, color)
            }
        }
        for _ in 0..<50 {
            let angle = random() * .pi * 2, radius = sqrt(random()) * 32
            diamond(3 + cos(angle) * radius, 190 + sin(angle) * radius * 0.7, 13, 10, leaf.mix(.white, random() * 0.3))
        }
        ctx.restoreGState()
    }
}
