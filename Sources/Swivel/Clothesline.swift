import AppKit
import AVFoundation

/// A rope tucked under the menu bar.
///
/// New screenshots first appear as a card in the bottom-right corner, as macOS shows them. Drag the card up
/// (or click it) to hang it on the rope; leave it and it goes to your usual screenshot folder. Resting the
/// pointer on the menu bar lowers the rope, and every print can be clicked, opened or dragged away.
@MainActor
final class Clothesline: NSObject {
    static let shared = Clothesline()

    let folder: URL = {
        let url = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Pictures/Swivel Clothesline", isDirectory: true)
        try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }()

    /// Where macOS writes screenshots while Swivel has taken them over; each one waits here as a card.
    lazy var incoming: URL = {
        let url = folder.appendingPathComponent("Incoming", isDirectory: true)
        try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }()

    private(set) var items: [URL] = []
    let panel = RopePanel()
    private var watches: [DispatchSourceFileSystemObject] = []
    private var hoverTimer: Timer?
    private var hoverSince: Date?
    private var leftSince: Date?
    private var pinned = false              // shown with the hotkey: stays until toggled or clicked away
    private var pasteboardCount = NSPasteboard.general.changeCount
    private var hotKey: HotKey?
    private var seenIncoming: Set<String> = []
    var isDraggingItem = false              // a print is being dragged off the rope
    private(set) var isReceiving = false    // something is being dragged toward the rope

    static let mediaExtensions: Set<String> = ["png", "jpg", "jpeg", "heic", "tiff", "gif", "webp", "mov", "mp4"]

    func start() {
        reload()
        // Anything left waiting from last time goes where screenshots normally go.
        for url in mediaFiles(in: incoming) { IncomingCard.shared.putAway(url) }
        watch(folder) { [weak self] in self?.reload() }
        watch(incoming) { [weak self] in self?.checkIncoming() }
        let timer = Timer(timeInterval: 0.1, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.tick() }
        }
        RunLoop.main.add(timer, forMode: .common)
        hoverTimer = timer
        hotKey = HotKey { [weak self] in self?.toggle() }
        if Prefs.clotheslineEnabled && Prefs.clotheslineTakeover { ScreenshotTakeover.enable(into: incoming) }
    }

    /// Turning the feature off puts the rope away, stops hanging copies and gives screenshots back to macOS.
    func setEnabled(_ on: Bool) {
        Prefs.clotheslineEnabled = on
        if on {
            if Prefs.clotheslineTakeover { ScreenshotTakeover.enable(into: incoming) }
        } else {
            hide()
            ScreenshotTakeover.restore()
        }
    }

    /// Called on quit so the user's own screenshot settings come back.
    func stop() { ScreenshotTakeover.restore() }

    // MARK: Contents

    private func mediaFiles(in directory: URL) -> [URL] {
        let keys: [URLResourceKey] = [.creationDateKey, .isRegularFileKey]
        let files = (try? FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: keys,
                                                                  options: [.skipsHiddenFiles])) ?? []
        return files
            .filter { Self.mediaExtensions.contains($0.pathExtension.lowercased()) }
            .sorted {
                let a = (try? $0.resourceValues(forKeys: [.creationDateKey]).creationDate) ?? .distantPast
                let b = (try? $1.resourceValues(forKeys: [.creationDateKey]).creationDate) ?? .distantPast
                return a > b
            }
    }

    func reload() {
        items = mediaFiles(in: folder)
        panel.rope.setItems(items)
        if items.isEmpty && !isReceiving { hide() }
    }

    /// Puts files on the rope: files waiting as a card are moved, anything else is copied.
    func receive(_ urls: [URL]) {
        let fm = FileManager.default
        for url in urls where Self.mediaExtensions.contains(url.pathExtension.lowercased()) {
            let destination = Paths.unique(folder.appendingPathComponent(url.lastPathComponent))
            let waiting = url.deletingLastPathComponent().standardizedFileURL == incoming.standardizedFileURL
            if waiting { try? fm.moveItem(at: url, to: destination) } else { try? fm.copyItem(at: url, to: destination) }
            panel.rope.markNew(destination)
        }
        reload()
        show(force: true)
        if !pinned { leftSince = Date().addingTimeInterval(2.2) }  // stay down long enough to see it land
    }

    /// From the ⌃ Actions menu.
    func hang(_ urls: [URL]) { receive(urls) }

    private func checkIncoming() {
        let waiting = mediaFiles(in: incoming)
        let fresh = waiting.filter { !seenIncoming.contains($0.lastPathComponent) }
        seenIncoming = Set(waiting.map(\.lastPathComponent))
        if let newest = fresh.first { IncomingCard.shared.present(newest) }
    }

    private func watch(_ directory: URL, _ changed: @escaping @MainActor () -> Void) {
        let fd = open(directory.path, O_EVTONLY)
        guard fd >= 0 else { return }
        let source = DispatchSource.makeFileSystemObjectSource(fileDescriptor: fd, eventMask: [.write, .rename, .delete], queue: .main)
        source.setEventHandler {
            // Screenshots are written in a couple of steps; give the file a moment to settle.
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.35) { MainActor.assumeIsolated { changed() } }
        }
        source.setCancelHandler { close(fd) }
        source.resume()
        watches.append(source)
    }

    // MARK: Showing and hiding

    var isShown: Bool { panel.isVisible }

    func toggle() {
        if isShown { hide() } else { pinned = true; show() }
    }

    func show(force: Bool = false) {
        guard Prefs.clotheslineEnabled, force || !items.isEmpty else { return }
        let screen = NSScreen.screens.first { NSMouseInRect(NSEvent.mouseLocation, $0.frame, false) } ?? NSScreen.main
        guard let screen else { return }
        panel.lower(on: screen)
    }

    func hide() {
        pinned = false
        hoverSince = nil
        leftSince = nil
        panel.raise()
    }

    /// A card is being dragged: lower the rope and keep it down until the drag ends.
    func beginReceiving() {
        isReceiving = true
        show(force: true)
    }

    func endReceiving() {
        isReceiving = false
        leftSince = Date()
    }

    private func menuBarHeight(_ screen: NSScreen) -> CGFloat {
        max(screen.frame.maxY - screen.visibleFrame.maxY, 24)
    }

    private func tick() {
        guard Prefs.clotheslineEnabled else { if isShown { hide() }; return }
        checkPasteboard()
        let point = NSEvent.mouseLocation
        guard let screen = NSScreen.screens.first(where: { NSMouseInRect(point, $0.frame, false) }) else { return }
        let inMenuBar = point.y >= screen.frame.maxY - menuBarHeight(screen)
        let buttonDown = NSEvent.pressedMouseButtons & 1 != 0

        if !isShown {
            // Rest the pointer on the menu bar (without clicking) to lower the rope.
            if inMenuBar && !buttonDown && !items.isEmpty {
                if hoverSince == nil { hoverSince = Date() }
                if Date().timeIntervalSince(hoverSince!) > 0.35 { show() }
            } else {
                hoverSince = nil
            }
            return
        }

        if isDraggingItem || isReceiving { return }
        // Clicking the menu bar puts the rope away.
        if inMenuBar && buttonDown { hide(); return }
        if pinned { return }
        let overRope = NSPointInRect(point, panel.frame.insetBy(dx: 0, dy: -8)) && panel.rope.isOverPrint(screenPoint: point)
        if overRope || inMenuBar {
            if let left = leftSince, left > Date() { return }  // still showing off a new arrival
            leftSince = nil
        } else {
            if leftSince == nil { leftSince = Date() }
            if Date().timeIntervalSince(leftSince!) > 0.5 { hide() }
        }
    }

    // MARK: Copied images

    private func checkPasteboard() {
        let pasteboard = NSPasteboard.general
        guard pasteboard.changeCount != pasteboardCount else { return }
        pasteboardCount = pasteboard.changeCount
        guard Prefs.clotheslineHangCopied, let types = pasteboard.types else { return }
        // Only bare images: skip anything copied with text or files, and anything marked private.
        let skip: Set<NSPasteboard.PasteboardType> = [.string, .fileURL, .URL, .rtf, .html,
                                                      .init("org.nspasteboard.ConcealedType"), .init("org.nspasteboard.TransientType")]
        guard types.allSatisfy({ !skip.contains($0) }),
              let image = NSImage(pasteboard: pasteboard),
              let tiff = image.tiffRepresentation,
              let png = NSBitmapImageRep(data: tiff)?.representation(using: .png, properties: [:]) else { return }
        let formatter = DateFormatter()
        formatter.dateFormat = "yyyy-MM-dd 'at' HH.mm.ss"
        // Lands in Incoming, so it shows up as a card like a screenshot.
        try? png.write(to: Paths.unique(incoming.appendingPathComponent("Copied \(formatter.string(from: Date())).png")))
    }

    // MARK: Item actions

    func copy(_ url: URL) {
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        var objects: [NSPasteboardWriting] = [url as NSURL]
        if let image = NSImage(contentsOf: url) { objects.append(image) }
        pasteboard.writeObjects(objects)
        pasteboardCount = pasteboard.changeCount  // don't re-hang our own copy
        Toast.shared.show(L("Copied %@", url.lastPathComponent), symbol: "doc.on.doc")
    }

    func openInPreview(_ url: URL) {
        hide()
        let isMovie = ["mov", "mp4"].contains(url.pathExtension.lowercased())
        let app = NSWorkspace.shared.urlForApplication(withBundleIdentifier: isMovie ? "com.apple.QuickTimePlayerX" : "com.apple.Preview")
        if let app { NSWorkspace.shared.open([url], withApplicationAt: app, configuration: NSWorkspace.OpenConfiguration(), completionHandler: nil) } else { NSWorkspace.shared.open(url) }
    }

    func quickLook(_ url: URL) {
        hide()
        QuickLooker.shared.show(url)
    }

    func discard(_ url: URL) {
        panel.rope.letFall(url)
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.35) {
            NSWorkspace.shared.recycle([url]) { [weak self] _, _ in
                MainActor.assumeIsolated { self?.reload() }
            }
        }
    }
}

// MARK: - Panel

/// A full-width strip under the menu bar. It only catches the mouse over a print (or while something is being
/// dragged to it); everywhere else clicks fall through to the windows below.
final class RopePanel: NSPanel {
    let rope = RopeView()
    static let height: CGFloat = 214

    init() {
        super.init(contentRect: NSRect(x: 0, y: 0, width: 800, height: Self.height),
                   styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        isOpaque = false
        backgroundColor = .clear
        hasShadow = false
        level = .statusBar
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        isReleasedWhenClosed = false
        hidesOnDeactivate = false
        ignoresMouseEvents = true
        contentView = rope
    }

    override var canBecomeKey: Bool { false }

    func lower(on screen: NSScreen) {
        let frame = NSRect(x: screen.frame.minX, y: screen.visibleFrame.maxY - Self.height, width: screen.frame.width, height: Self.height)
        if isVisible && !rope.isLifting { return }
        setFrame(frame, display: false)
        orderFrontRegardless()
        rope.drop()
    }

    func raise() {
        guard isVisible else { return }
        rope.lift { [weak self] in self?.orderOut(nil) }
    }
}

// MARK: - Rope

/// Draws the rope and its prints with Core Animation layers, moved every frame by a small physics model:
/// the rope falls on a spring and sags, each print is a damped pendulum on its peg, and a light breeze keeps
/// them moving. Everything is interruptible because the state is simulated, not tweened.
final class RopeView: NSView, NSDraggingSource {
    private final class Print {
        let url: URL
        let layer = CALayer()
        let ring = CALayer()
        let close = CALayer()
        var size: CGSize
        var image: CGImage?
        var x: CGFloat = 0
        var tilt: CGFloat = 0          // resting angle, so the line doesn't look ruled
        var angle: CGFloat = 0, angleV: CGFloat = 0
        var lift: CGFloat = 0, liftV: CGFloat = 0
        var drop: CGFloat = 0, dropV: CGFloat = 0
        var kickAt: CFTimeInterval?, kick: CGFloat = 0
        var falling = false
        let phase = CGFloat.random(in: 0...(2 * .pi))

        init(url: URL, size: CGSize) {
            self.url = url
            self.size = size
        }
    }

    private let root = CALayer()
    private let ropeLayer = CAShapeLayer()
    private var prints: [Print] = []
    private var newcomers: Set<URL> = []
    private var timer: Timer?
    private var clock: CFTimeInterval = 0

    // Rope state: vertical offset of the whole rope (falls in from above) and extra sag.
    private var offset: CGFloat = -RopePanel.height, offsetV: CGFloat = 0, offsetTarget: CGFloat = 0
    private var sag: CGFloat = 0, sagV: CGFloat = 0
    private var onLifted: (() -> Void)?
    private(set) var isLifting = false
    private var hovered: Int? { didSet { if hovered != oldValue { updateHover() } } }
    private var receivingHighlight = false

    private static let ropeTop: CGFloat = 18
    private static let restSag: CGFloat = 16
    private static let maxPrint = CGSize(width: 168, height: 112)

    override init(frame: NSRect) {
        super.init(frame: frame)
        root.isGeometryFlipped = true
        layer = root
        wantsLayer = true
        ropeLayer.fillColor = nil
        ropeLayer.strokeColor = NSColor(srgbRed: 0.9, green: 0.86, blue: 0.78, alpha: 1).cgColor
        ropeLayer.lineWidth = 2.6
        ropeLayer.lineCap = .round
        ropeLayer.shadowOpacity = 0.35
        ropeLayer.shadowRadius = 2
        ropeLayer.shadowOffset = CGSize(width: 0, height: 2)
        root.addSublayer(ropeLayer)
        registerForDraggedTypes([.fileURL])
    }

    required init?(coder: NSCoder) { fatalError() }

    override var isFlipped: Bool { true }

    // MARK: Contents

    func markNew(_ url: URL) { newcomers.insert(url) }

    func setItems(_ urls: [URL]) {
        let existing = Dictionary(prints.map { ($0.url, $0) }, uniquingKeysWith: { a, _ in a })
        let kept = prints.filter { urls.contains($0.url) }
        for gone in prints where !urls.contains(gone.url) && !gone.falling { gone.layer.removeFromSuperlayer() }
        prints = urls.map { existing[$0] ?? makePrint($0) }
        for print in kept where !prints.contains(where: { $0 === print }) { print.layer.removeFromSuperlayer() }
        layoutPrints()
        for print in prints where newcomers.contains(print.url) {
            // Arrives from above, bounces on the rope and swings.
            print.drop = -110
            print.dropV = 0
            print.angleV = CGFloat.random(in: 0.9...1.4) * (Bool.random() ? 1 : -1)
        }
        newcomers.removeAll()
        startTimer()
    }

    private func layoutPrints() {
        var x: CGFloat = 76
        for (i, print) in prints.enumerated() {
            let fits = x + print.size.width < bounds.width - 40
            print.layer.isHidden = !fits
            print.x = x + print.size.width / 2
            print.tilt = [0.018, -0.015, 0.012, -0.02, 0.015, -0.012][i % 6]
            x += print.size.width + 22
        }
    }

    private func makePrint(_ url: URL) -> Print {
        let isMovie = ["mov", "mp4"].contains(url.pathExtension.lowercased())
        var thumb: CGImage?
        if isMovie {
            let generator = AVAssetImageGenerator(asset: AVURLAsset(url: url))
            generator.appliesPreferredTrackTransform = true
            generator.maximumSize = CGSize(width: 400, height: 400)
            thumb = try? generator.copyCGImage(at: .zero, actualTime: nil)
        } else {
            thumb = try? Images.load(url, maxPixels: 400)
        }
        var size = CGSize(width: 150, height: 100)
        if let thumb {
            let s = min(Self.maxPrint.width / CGFloat(thumb.width), Self.maxPrint.height / CGFloat(thumb.height))
            size = CGSize(width: CGFloat(thumb.width) * s, height: CGFloat(thumb.height) * s)
        }
        size = CGSize(width: size.width + 10, height: size.height + 10)  // white border
        let print = Print(url: url, size: size)
        print.image = Self.renderPrint(thumb, size: size, movie: isMovie)

        let layer = print.layer
        layer.bounds = CGRect(origin: .zero, size: size)
        layer.anchorPoint = CGPoint(x: 0.5, y: 0)  // hangs from the top centre
        layer.contents = print.image
        layer.contentsScale = 2
        layer.shadowOpacity = 0.32
        layer.shadowRadius = 6
        layer.shadowOffset = CGSize(width: 0, height: 4)
        layer.shadowPath = CGPath(rect: layer.bounds, transform: nil)

        // Wooden peg gripping the rope and the top edge of the print.
        let peg = CALayer()
        peg.frame = CGRect(x: size.width / 2 - 5, y: -14, width: 10, height: 28)
        peg.backgroundColor = NSColor(srgbRed: 0.8, green: 0.6, blue: 0.38, alpha: 1).cgColor
        peg.cornerRadius = 2.5
        peg.shadowOpacity = 0.4
        peg.shadowRadius = 1.5
        peg.shadowOffset = CGSize(width: 0, height: 1)
        let spring = CALayer()
        spring.frame = CGRect(x: 0, y: 12, width: 10, height: 2)
        spring.backgroundColor = NSColor(white: 0.62, alpha: 1).cgColor
        peg.addSublayer(spring)
        layer.addSublayer(peg)

        print.ring.frame = layer.bounds.insetBy(dx: -2, dy: -2)
        print.ring.borderWidth = 2
        print.ring.cornerRadius = 4
        print.ring.borderColor = NSColor.controlAccentColor.cgColor
        print.ring.isHidden = true
        layer.addSublayer(print.ring)

        print.close.frame = CGRect(x: size.width - 13, y: -9, width: 22, height: 22)
        print.close.cornerRadius = 11
        print.close.backgroundColor = NSColor(white: 0.12, alpha: 0.85).cgColor
        let cross = CAShapeLayer()
        let path = CGMutablePath()
        path.move(to: CGPoint(x: 7, y: 7)); path.addLine(to: CGPoint(x: 15, y: 15))
        path.move(to: CGPoint(x: 15, y: 7)); path.addLine(to: CGPoint(x: 7, y: 15))
        cross.path = path
        cross.strokeColor = NSColor.white.cgColor
        cross.lineWidth = 1.7
        cross.lineCap = .round
        print.close.addSublayer(cross)
        print.close.isHidden = true
        layer.addSublayer(print.close)

        root.addSublayer(layer)
        return print
    }

    private static func renderPrint(_ thumb: CGImage?, size: CGSize, movie: Bool) -> CGImage? {
        let scale: CGFloat = 2
        guard let ctx = CGContext(data: nil, width: Int(size.width * scale), height: Int(size.height * scale), bitsPerComponent: 8,
                                  bytesPerRow: 0, space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        ctx.scaleBy(x: scale, y: scale)
        let rect = CGRect(origin: .zero, size: size)
        ctx.addPath(CGPath(roundedRect: rect, cornerWidth: 3, cornerHeight: 3, transform: nil))
        ctx.setFillColor(.white)
        ctx.fillPath()
        ctx.interpolationQuality = .high
        if let thumb { ctx.draw(thumb, in: rect.insetBy(dx: 5, dy: 5)) }
        if movie {
            let c = CGPoint(x: rect.midX, y: rect.midY)
            ctx.setFillColor(NSColor.black.withAlphaComponent(0.5).cgColor)
            ctx.fillEllipse(in: CGRect(x: c.x - 16, y: c.y - 16, width: 32, height: 32))
            ctx.setFillColor(.white)
            ctx.move(to: CGPoint(x: c.x - 5, y: c.y - 8)); ctx.addLine(to: CGPoint(x: c.x + 9, y: c.y)); ctx.addLine(to: CGPoint(x: c.x - 5, y: c.y + 8))
            ctx.closePath()
            ctx.fillPath()
        }
        return ctx.makeImage()
    }

    // MARK: Motion

    /// The rope falls in from under the menu bar on a spring; prints get a staggered nudge so they swing in turn.
    func drop() {
        isLifting = false
        onLifted = nil
        offset = -bounds.height
        offsetV = 0
        offsetTarget = 0
        layoutPrints()
        for (i, print) in prints.enumerated() {
            print.kickAt = clock + 0.12 + Double(i) * 0.045
            print.kick = CGFloat.random(in: 0.35...0.6) * (i % 2 == 0 ? 1 : -1)
        }
        startTimer()
    }

    /// Pulled back up; the prints trail a little behind.
    func lift(_ done: @escaping () -> Void) {
        isLifting = true
        offsetTarget = -bounds.height - 30
        onLifted = done
        for print in prints { print.angleV += CGFloat.random(in: -0.25...0.25) }
        startTimer()
    }

    /// Discarded: the print drops off the rope and fades.
    func letFall(_ url: URL) {
        guard let print = prints.first(where: { $0.url == url }) else { return }
        print.falling = true
        let fall = CABasicAnimation(keyPath: "position.y")
        fall.byValue = 240
        fall.duration = 0.35
        fall.timingFunction = CAMediaTimingFunction(name: .easeIn)
        let spin = CABasicAnimation(keyPath: "transform.rotation.z")
        spin.byValue = print.angle > 0 ? 0.6 : -0.6
        spin.duration = 0.35
        let fade = CABasicAnimation(keyPath: "opacity")
        fade.toValue = 0
        fade.duration = 0.35
        let group = CAAnimationGroup()
        group.animations = [fall, spin, fade]
        group.duration = 0.35
        group.fillMode = .forwards
        group.isRemovedOnCompletion = false
        print.layer.add(group, forKey: "fall")
    }

    private func startTimer() {
        guard timer == nil else { return }
        let timer = Timer(timeInterval: 1.0 / 60.0, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.step() }
        }
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
    }

    private func stopTimer() {
        timer?.invalidate()
        timer = nil
    }

    func ropeY(_ x: CGFloat) -> CGFloat {
        let t = bounds.width > 0 ? min(max(x / bounds.width, 0), 1) : 0.5
        return Self.ropeTop + offset + (Self.restSag + sag) * 4 * t * (1 - t)
    }

    private func step() {
        let dt: CGFloat = 1.0 / 120.0
        let mouse = window.map { convert($0.convertPoint(fromScreen: NSEvent.mouseLocation), from: nil) }
        hovered = isLifting || Clothesline.shared.isDraggingItem ? nil : mouse.flatMap(printIndex(at:))

        for _ in 0..<2 {  // two half-steps per frame keeps the springs stable
            clock += Double(dt)
            // Whole rope: an under-damped spring, so it overshoots and settles.
            let offsetA = -260 * (offset - offsetTarget) - 24 * offsetV
            offsetV += offsetA * dt
            offset += offsetV * dt
            // The cord lags behind the rope's motion, so it bounces in the middle when the rope stops.
            let sagA = -150 * sag - 9 * sagV - 0.05 * offsetA
            sagV += sagA * dt
            sag += sagV * dt

            for (i, print) in prints.enumerated() where !print.falling {
                if let at = print.kickAt, clock >= at { print.angleV += print.kick; print.kickAt = nil }
                // Pendulum on its peg, pulled toward its resting tilt (straight when hovered), plus a breeze.
                let rest = i == hovered ? 0 : print.tilt
                let t = CGFloat(clock)
                let breeze = Prefs.clotheslineEnabled ? 0.07 * sin(1.1 * t + print.phase) + 0.03 * sin(2.7 * t + print.phase * 1.7) : 0
                let angleA = -34 * (print.angle - rest) - 6.0 * print.angleV + breeze
                print.angleV += angleA * dt
                print.angle += print.angleV * dt
                let liftTarget: CGFloat = i == hovered ? -6 : 0
                let liftA = -340 * (print.lift - liftTarget) - 30 * print.liftV
                print.liftV += liftA * dt
                print.lift += print.liftV * dt
                let dropA = -200 * print.drop - 16 * print.dropV
                print.dropV += dropA * dt
                print.drop += print.dropV * dt
            }
        }
        render()

        // Only catch the mouse where it matters, so the strip doesn't block the windows below.
        if let panel = window as? NSPanel {
            panel.ignoresMouseEvents = hovered == nil && !Clothesline.shared.isReceiving && !Clothesline.shared.isDraggingItem
        }

        if isLifting && offset < -bounds.height + 1 {
            isLifting = false
            stopTimer()
            let done = onLifted
            onLifted = nil
            done?()
        }
    }

    private func render() {
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        let path = CGMutablePath()
        path.move(to: CGPoint(x: -4, y: ropeY(0)))
        for x in stride(from: CGFloat(0), through: bounds.width + 4, by: 6) { path.addLine(to: CGPoint(x: x, y: ropeY(x))) }
        ropeLayer.path = path
        ropeLayer.strokeColor = (receivingHighlight ? NSColor.controlAccentColor : NSColor(srgbRed: 0.9, green: 0.86, blue: 0.78, alpha: 1)).cgColor
        ropeLayer.lineWidth = receivingHighlight ? 3.4 : 2.6
        for print in prints where !print.falling {
            print.layer.position = CGPoint(x: print.x, y: ropeY(print.x) + 4 + print.lift + print.drop)
            print.layer.transform = CATransform3DMakeRotation(print.angle, 0, 0, 1)
        }
        CATransaction.commit()
    }

    private func updateHover() {
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        for (i, print) in prints.enumerated() {
            print.ring.isHidden = i != hovered
            print.close.isHidden = i != hovered
            print.layer.zPosition = i == hovered ? 10 : 0
        }
        CATransaction.commit()
        toolTip = hovered.map { prints[$0].url.lastPathComponent + "\n" + L("Click: copy · Double-click: open · Hold: Quick Look · Drag: take it down") }
    }

    // MARK: Hit testing

    private func printIndex(at point: NSPoint) -> Int? {
        for i in prints.indices.reversed() where !prints[i].layer.isHidden && !prints[i].falling {
            let local = prints[i].layer.convert(point, from: root)
            if prints[i].layer.bounds.insetBy(dx: -4, dy: -14).contains(local) { return i }
        }
        return nil
    }

    private func hitsClose(_ index: Int, _ point: NSPoint) -> Bool {
        let local = prints[index].layer.convert(point, from: root)
        return prints[index].close.frame.insetBy(dx: -3, dy: -3).contains(local)
    }

    func isOverPrint(screenPoint: NSPoint) -> Bool {
        guard let window else { return false }
        return printIndex(at: convert(window.convertPoint(fromScreen: screenPoint), from: nil)) != nil
    }

    // MARK: Gestures

    private var downEvent: NSEvent?
    private var downIndex: Int?
    private var longPress: DispatchWorkItem?
    private var singleClick: DispatchWorkItem?
    private var handled = false

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func rightMouseDown(with event: NSEvent) {
        let point = convert(event.locationInWindow, from: nil)
        guard let index = printIndex(at: point), Feature.textCapture.isOn,
              FileKind.of(prints[index].url) == .image else { return }
        let menu = NSMenu()
        let item = NSMenuItem(title: L("Extract Text"), action: #selector(extractPrintText(_:)), keyEquivalent: "")
        item.target = self
        item.representedObject = prints[index].url
        menu.addItem(item)
        Clothesline.shared.isDraggingItem = true
        defer { Clothesline.shared.isDraggingItem = false }
        NSMenu.popUpContextMenu(menu, with: event, for: self)
    }

    @objc private func extractPrintText(_ item: NSMenuItem) {
        guard let url = item.representedObject as? URL else { return }
        Clothesline.shared.hide()
        TextCaptureWindow.present(url)
    }

    override func mouseDown(with event: NSEvent) {
        if event.modifierFlags.contains(.control) { rightMouseDown(with: event); return }
        handled = false
        downEvent = event
        let p = convert(event.locationInWindow, from: nil)
        downIndex = printIndex(at: p)
        guard let index = downIndex else { handled = true; return }
        if hitsClose(index, p) {
            handled = true
            Clothesline.shared.discard(prints[index].url)
            return
        }
        let url = prints[index].url
        let press = DispatchWorkItem { [weak self] in
            guard let self, !self.handled else { return }
            self.handled = true
            Clothesline.shared.quickLook(url)
        }
        longPress = press
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.55, execute: press)
    }

    override func pressureChange(with event: NSEvent) {
        guard event.stage >= 2, !handled, let index = downIndex else { return }
        handled = true
        longPress?.cancel()
        Clothesline.shared.quickLook(prints[index].url)
    }

    override func mouseDragged(with event: NSEvent) {
        guard !handled, let down = downEvent, let index = downIndex else { return }
        let a = down.locationInWindow, b = event.locationInWindow
        guard hypot(b.x - a.x, b.y - a.y) > 4 else { return }
        handled = true
        longPress?.cancel()
        let print = prints[index]
        let item = NSDraggingItem(pasteboardWriter: print.url as NSURL)
        let image = print.image.map { NSImage(cgImage: $0, size: print.size) } ?? NSWorkspace.shared.icon(forFile: print.url.path)
        item.setDraggingFrame(print.layer.frame, contents: image)
        Clothesline.shared.isDraggingItem = true
        beginDraggingSession(with: [item], event: down, source: self)
    }

    override func mouseUp(with event: NSEvent) {
        longPress?.cancel()
        guard !handled, let index = downIndex else { return }
        handled = true
        let url = prints[index].url
        if event.clickCount >= 2 {
            singleClick?.cancel()
            Clothesline.shared.openInPreview(url)
        } else {
            // A little bounce so the click feels received.
            prints[index].liftV -= 120
            let click = DispatchWorkItem { Clothesline.shared.copy(url) }
            singleClick = click
            DispatchQueue.main.asyncAfter(deadline: .now() + NSEvent.doubleClickInterval, execute: click)
        }
    }

    // Into an app: a copy (the original stays). Into a folder: moved off the rope. Onto the Trash: discarded.
    func draggingSession(_ session: NSDraggingSession, sourceOperationMaskFor context: NSDraggingContext) -> NSDragOperation {
        context == .outsideApplication ? [.copy, .move, .generic, .delete] : []
    }

    func draggingSession(_ session: NSDraggingSession, endedAt screenPoint: NSPoint, operation: NSDragOperation) {
        Clothesline.shared.isDraggingItem = false
        guard let index = downIndex, prints.indices.contains(index) else { return }
        if operation == .delete {
            Clothesline.shared.discard(prints[index].url)
        } else {
            Clothesline.shared.reload()
        }
    }

    // MARK: Receiving drops (a screenshot card, or images from Finder)

    private func acceptable(_ info: NSDraggingInfo) -> [URL] {
        guard info.draggingSource as? RopeView !== self else { return [] }
        let urls = info.draggingPasteboard.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL] ?? []
        return urls.filter { Clothesline.mediaExtensions.contains($0.pathExtension.lowercased()) }
    }

    private func operation(_ info: NSDraggingInfo) -> NSDragOperation {
        let ok = !acceptable(info).isEmpty
        if ok != receivingHighlight { receivingHighlight = ok; startTimer() }
        guard ok else { return [] }
        return info.draggingSourceOperationMask.contains(.move) ? .move : .copy
    }

    override func draggingEntered(_ sender: NSDraggingInfo) -> NSDragOperation {
        let op = operation(sender)
        if op != [] { sagV += 30 }  // the rope dips as something comes near
        return op
    }

    override func draggingUpdated(_ sender: NSDraggingInfo) -> NSDragOperation { operation(sender) }
    override func draggingExited(_ sender: NSDraggingInfo?) { receivingHighlight = false }

    override func performDragOperation(_ sender: NSDraggingInfo) -> Bool {
        receivingHighlight = false
        let urls = acceptable(sender)
        guard !urls.isEmpty else { return false }
        sagV += 80  // catches the weight
        DispatchQueue.main.async { Clothesline.shared.receive(urls) }
        return true
    }
}

// MARK: - Screenshot card

/// The new screenshot, waiting in the bottom-right corner like the system thumbnail. Drag it up to the rope or
/// click it to hang it; drag it into an app or folder like any file; ignore it and it goes to the usual folder.
@MainActor
final class IncomingCard: NSObject, NSDraggingSource {
    static let shared = IncomingCard()

    private let panel: NSPanel
    private let view = IncomingView()
    private(set) var url: URL?
    private var dismissal: DispatchWorkItem?
    private var dragging = false
    private static let waitSeconds: TimeInterval = 6

    override init() {
        panel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 220, height: 170),
                        styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        super.init()
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.level = .statusBar
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        panel.isReleasedWhenClosed = false
        panel.hidesOnDeactivate = false
        panel.contentView = view
        view.card = self
    }

    func present(_ url: URL) {
        if let current = self.url, current != url { putAway(current, animated: false) }
        self.url = url
        let isMovie = ["mov", "mp4"].contains(url.pathExtension.lowercased())
        var thumb: CGImage?
        if isMovie {
            let generator = AVAssetImageGenerator(asset: AVURLAsset(url: url))
            generator.appliesPreferredTrackTransform = true
            generator.maximumSize = CGSize(width: 480, height: 480)
            thumb = try? generator.copyCGImage(at: .zero, actualTime: nil)
        } else {
            thumb = try? Images.load(url, maxPixels: 480)
        }
        view.thumbnail = thumb.map { NSImage(cgImage: $0, size: NSSize(width: $0.width, height: $0.height)) }
        let size = view.preferredSize
        let screen = NSScreen.screens.first { NSMouseInRect(NSEvent.mouseLocation, $0.frame, false) } ?? NSScreen.main
        guard let area = screen?.visibleFrame else { return }
        let final = NSRect(x: area.maxX - size.width - 14, y: area.minY + 14, width: size.width, height: size.height)
        panel.setFrame(final.offsetBy(dx: size.width + 40, dy: 0), display: false)
        panel.alphaValue = 1
        panel.orderFrontRegardless()
        NSAnimationContext.runAnimationGroup { ctx in
            ctx.duration = 0.5
            ctx.timingFunction = CAMediaTimingFunction(controlPoints: 0.2, 1.25, 0.35, 1)  // slides in, overshoots a hair
            panel.animator().setFrame(final, display: true)
        }
        scheduleDismissal()
    }

    private func scheduleDismissal() {
        dismissal?.cancel()
        let work = DispatchWorkItem { [weak self] in
            guard let self, !self.dragging, !self.view.isHovered, let url = self.url else {
                self?.scheduleDismissal()
                return
            }
            self.putAway(url)
        }
        dismissal = work
        DispatchQueue.main.asyncAfter(deadline: .now() + Self.waitSeconds, execute: work)
    }

    /// Not wanted on the rope: saved where screenshots normally go, and the card slides away.
    func putAway(_ url: URL, animated: Bool = true) {
        let destination = Paths.unique(ScreenshotTakeover.originalLocation.appendingPathComponent(url.lastPathComponent))
        try? FileManager.default.moveItem(at: url, to: destination)
        guard url == self.url else { return }
        self.url = nil
        dismissal?.cancel()
        guard animated else { panel.orderOut(nil); return }
        NSAnimationContext.runAnimationGroup({ ctx in
            ctx.duration = 0.28
            ctx.timingFunction = CAMediaTimingFunction(name: .easeIn)
            panel.animator().setFrame(panel.frame.offsetBy(dx: panel.frame.width + 40, dy: 0), display: true)
        }, completionHandler: { [panel] in MainActor.assumeIsolated { panel.orderOut(nil) } })
    }

    /// Click: the card flies up to the rope.
    func hangNow() {
        guard let url else { return }
        self.url = nil
        dismissal?.cancel()
        Clothesline.shared.show(force: true)
        let area = panel.screen?.visibleFrame ?? .zero
        let target = NSRect(x: area.midX - panel.frame.width * 0.25, y: area.maxY - 130,
                            width: panel.frame.width * 0.5, height: panel.frame.height * 0.5)
        NSAnimationContext.runAnimationGroup({ ctx in
            ctx.duration = 0.38
            ctx.timingFunction = CAMediaTimingFunction(controlPoints: 0.5, 0, 0.3, 1)
            panel.animator().setFrame(target, display: true)
            panel.animator().alphaValue = 0
        }, completionHandler: { [panel] in
            MainActor.assumeIsolated {
                panel.orderOut(nil)
                Clothesline.shared.receive([url])
            }
        })
    }

    func open() {
        guard let url else { return }
        dismissal?.cancel()
        panel.orderOut(nil)
        self.url = nil
        Clothesline.shared.openInPreview(url)
    }

    func beginDrag(with event: NSEvent) {
        guard let url else { return }
        dragging = true
        let item = NSDraggingItem(pasteboardWriter: url as NSURL)
        item.setDraggingFrame(view.photoRect, contents: view.thumbnail ?? NSWorkspace.shared.icon(forFile: url.path))
        panel.animator().alphaValue = 0.25
        Clothesline.shared.beginReceiving()
        view.beginDraggingSession(with: [item], event: event, source: self)
    }

    func draggingSession(_ session: NSDraggingSession, sourceOperationMaskFor context: NSDraggingContext) -> NSDragOperation {
        context == .outsideApplication ? [.copy, .move, .generic, .delete] : [.move]
    }

    func draggingSession(_ session: NSDraggingSession, endedAt screenPoint: NSPoint, operation: NSDragOperation) {
        dragging = false
        Clothesline.shared.endReceiving()
        guard let url else { return }
        if operation == .delete {
            NSWorkspace.shared.recycle([url])
        }
        if !FileManager.default.fileExists(atPath: url.path) || operation == .delete {
            // Hung on the rope, moved to a folder or thrown away: the card's job is done.
            self.url = nil
            dismissal?.cancel()
            panel.orderOut(nil)
            panel.alphaValue = 1
        } else {
            // Copied into an app, or dropped nowhere: the card comes back and waits again.
            NSAnimationContext.runAnimationGroup { ctx in
                ctx.duration = 0.2
                panel.animator().alphaValue = 1
            }
            scheduleDismissal()
        }
    }
}

final class IncomingView: NSView {
    weak var card: IncomingCard?
    var thumbnail: NSImage? { didSet { needsDisplay = true } }
    private(set) var isHovered = false { didSet { needsDisplay = true } }
    private var downEvent: NSEvent?
    private var dragged = false
    private var singleClick: DispatchWorkItem?

    private static let maxThumb = NSSize(width: 200, height: 128)
    private static let caption: CGFloat = 26

    private var thumbSize: NSSize {
        guard let thumbnail, thumbnail.size.width > 0 else { return NSSize(width: 180, height: 110) }
        let s = min(Self.maxThumb.width / thumbnail.size.width, Self.maxThumb.height / thumbnail.size.height)
        return NSSize(width: thumbnail.size.width * s, height: thumbnail.size.height * s)
    }

    var preferredSize: NSSize { NSSize(width: max(thumbSize.width + 24, 170), height: thumbSize.height + 24 + Self.caption) }

    var photoRect: NSRect {
        let t = thumbSize
        return NSRect(x: (bounds.width - t.width - 8) / 2, y: Self.caption + 8, width: t.width + 8, height: t.height + 8)
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(NSTrackingArea(rect: bounds, options: [.mouseEnteredAndExited, .activeAlways, .inVisibleRect], owner: self))
    }

    override func mouseEntered(with event: NSEvent) { isHovered = true }
    override func mouseExited(with event: NSEvent) { isHovered = false }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func draw(_ dirtyRect: NSRect) {
        let photo = photoRect
        NSGraphicsContext.saveGraphicsState()
        let shadow = NSShadow()
        shadow.shadowBlurRadius = isHovered ? 14 : 10
        shadow.shadowOffset = NSSize(width: 0, height: -4)
        shadow.shadowColor = NSColor.black.withAlphaComponent(0.35)
        shadow.set()
        NSColor.white.setFill()
        NSBezierPath(roundedRect: photo, xRadius: 6, yRadius: 6).fill()
        NSGraphicsContext.restoreGraphicsState()
        if let thumbnail {
            NSGraphicsContext.saveGraphicsState()
            NSBezierPath(roundedRect: photo.insetBy(dx: 4, dy: 4), xRadius: 3, yRadius: 3).addClip()
            thumbnail.draw(in: photo.insetBy(dx: 4, dy: 4))
            NSGraphicsContext.restoreGraphicsState()
        }

        // "Drag up to hang" with an upward arrow, on a small frosted capsule.
        let text = "↑  " + L("Drag up to hang")
        let attrs: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: 11, weight: .semibold),
                                                    .foregroundColor: NSColor.white]
        let size = (text as NSString).size(withAttributes: attrs)
        let capsule = NSRect(x: bounds.midX - size.width / 2 - 10, y: 2, width: size.width + 20, height: 20)
        NSColor.black.withAlphaComponent(0.55).setFill()
        NSBezierPath(roundedRect: capsule, xRadius: 10, yRadius: 10).fill()
        (text as NSString).draw(at: NSPoint(x: capsule.minX + 10, y: capsule.midY - size.height / 2), withAttributes: attrs)
    }

    override func mouseDown(with event: NSEvent) {
        downEvent = event
        dragged = false
    }

    override func mouseDragged(with event: NSEvent) {
        guard !dragged, let down = downEvent else { return }
        let a = down.locationInWindow, b = event.locationInWindow
        guard hypot(b.x - a.x, b.y - a.y) > 4 else { return }
        dragged = true
        card?.beginDrag(with: down)
    }

    override func mouseUp(with event: NSEvent) {
        guard !dragged else { return }
        if event.clickCount >= 2 {
            singleClick?.cancel()
            card?.open()
        } else {
            let click = DispatchWorkItem { [weak self] in self?.card?.hangNow() }
            singleClick = click
            DispatchQueue.main.asyncAfter(deadline: .now() + NSEvent.doubleClickInterval, execute: click)
        }
    }
}
