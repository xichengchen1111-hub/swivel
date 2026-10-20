import AppKit
import AVFoundation
import Carbon
import Quartz

/// A rope tucked under the menu bar. New screenshots (and, optionally, copied images) hang on it;
/// pointing at the menu bar lowers it, and every item can be clicked, opened or dragged away.
@MainActor
final class Clothesline: NSObject {
    static let shared = Clothesline()

    let folder: URL = {
        let url = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Pictures/Swivel Clothesline", isDirectory: true)
        try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }()

    private(set) var items: [URL] = []
    private let panel = RopePanel()
    private var folderWatch: DispatchSourceFileSystemObject?
    private var hoverTimer: Timer?
    private var hoverSince: Date?
    private var leftSince: Date?
    private var pinned = false              // shown with the hotkey: stays until toggled or clicked away
    private var pasteboardCount = NSPasteboard.general.changeCount
    private var hotKey: HotKey?
    var isDraggingItem = false

    static let mediaExtensions: Set<String> = ["png", "jpg", "jpeg", "heic", "tiff", "gif", "webp", "mov", "mp4"]

    func start() {
        reload()
        watchFolder()
        let timer = Timer(timeInterval: 0.1, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.tick() }
        }
        RunLoop.main.add(timer, forMode: .common)
        hoverTimer = timer
        hotKey = HotKey { [weak self] in self?.toggle() }
        if Prefs.clotheslineTakeover { ScreenshotTakeover.enable(into: folder) }
    }

    /// Called on quit so the user's own screenshot settings come back.
    func stop() { ScreenshotTakeover.restore() }

    // MARK: Contents

    func reload() {
        let keys: [URLResourceKey] = [.creationDateKey, .isRegularFileKey]
        let files = (try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: keys,
                                                                  options: [.skipsHiddenFiles])) ?? []
        items = files
            .filter { Self.mediaExtensions.contains($0.pathExtension.lowercased()) }
            .sorted {
                let a = (try? $0.resourceValues(forKeys: [.creationDateKey]).creationDate) ?? .distantPast
                let b = (try? $1.resourceValues(forKeys: [.creationDateKey]).creationDate) ?? .distantPast
                return a > b
            }
        panel.rope.show(items)
        if items.isEmpty { hide() }
    }

    /// Copies files onto the rope and lowers it briefly so you can see them arrive.
    func hang(_ urls: [URL]) {
        for url in urls {
            try? FileManager.default.copyItem(at: url, to: Paths.unique(folder.appendingPathComponent(url.lastPathComponent)))
        }
        reload()
        peek()
    }

    private func watchFolder() {
        let fd = open(folder.path, O_EVTONLY)
        guard fd >= 0 else { return }
        let source = DispatchSource.makeFileSystemObjectSource(fileDescriptor: fd, eventMask: [.write, .rename, .delete], queue: .main)
        source.setEventHandler { [weak self] in
            MainActor.assumeIsolated {
                guard let self else { return }
                let before = Set(self.items)
                // Screenshots are written in a couple of steps; give the file a moment to settle.
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.4) {
                    self.reload()
                    if !Set(self.items).subtracting(before).isEmpty { self.peek() }
                }
            }
        }
        source.setCancelHandler { close(fd) }
        source.resume()
        folderWatch = source
    }

    // MARK: Showing and hiding

    var isShown: Bool { panel.isVisible }

    func toggle() {
        if isShown { hide() } else { pinned = true; show() }
    }

    func show() {
        guard Prefs.clotheslineEnabled, !items.isEmpty else { return }
        let screen = NSScreen.screens.first { NSMouseInRect(NSEvent.mouseLocation, $0.frame, false) } ?? NSScreen.main
        guard let screen else { return }
        panel.slideDown(on: screen)
    }

    func hide() {
        pinned = false
        hoverSince = nil
        leftSince = nil
        panel.slideUp()
    }

    /// Lowers the rope for a couple of seconds, e.g. after a new screenshot.
    private func peek() {
        guard Prefs.clotheslineEnabled, !items.isEmpty, !isShown else { return }
        show()
        leftSince = Date().addingTimeInterval(2)
    }

    private func menuBarHeight(_ screen: NSScreen) -> CGFloat {
        max(screen.frame.maxY - screen.visibleFrame.maxY, 24)
    }

    private func tick() {
        checkPasteboard()
        guard Prefs.clotheslineEnabled else { if isShown { hide() }; return }
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

        if isDraggingItem { return }
        // Clicking the menu bar puts the rope away.
        if inMenuBar && buttonDown { hide(); return }
        if pinned { return }
        let overRope = NSPointInRect(point, panel.frame.insetBy(dx: 0, dy: -8))
        if overRope || inMenuBar {
            leftSince = nil
        } else {
            if leftSince == nil { leftSince = Date() }
            if Date().timeIntervalSince(leftSince!) > 0.45 { hide() }
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
        try? png.write(to: Paths.unique(folder.appendingPathComponent("Copied \(formatter.string(from: Date())).png")))
    }

    // MARK: Item actions

    func copy(_ url: URL) {
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        var objects: [NSPasteboardWriting] = [url as NSURL]
        if let image = NSImage(contentsOf: url) { objects.append(image) }
        pasteboard.writeObjects(objects)
        pasteboardCount = pasteboard.changeCount  // don't re-hang our own copy
        Toast.shared.show("Copied \(url.lastPathComponent)", symbol: "doc.on.doc")
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
        NSWorkspace.shared.recycle([url]) { [weak self] _, _ in
            MainActor.assumeIsolated { self?.reload() }
        }
    }
}

// MARK: - Panel

final class RopePanel: NSPanel {
    let rope = RopeView()
    static let height: CGFloat = 196

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
        contentView = rope
    }

    override var canBecomeKey: Bool { false }

    func slideDown(on screen: NSScreen) {
        let top = screen.visibleFrame.maxY
        let final = NSRect(x: screen.frame.minX, y: top - Self.height, width: screen.frame.width, height: Self.height)
        if isVisible, frame == final { return }
        setFrame(final.offsetBy(dx: 0, dy: 24), display: false)
        alphaValue = 0
        rope.layoutItems()
        orderFrontRegardless()
        NSAnimationContext.runAnimationGroup { ctx in
            ctx.duration = 0.28
            ctx.timingFunction = CAMediaTimingFunction(controlPoints: 0.2, 1.3, 0.4, 1)  // a little bounce, like a rope settling
            animator().setFrame(final, display: true)
            animator().alphaValue = 1
        }
    }

    func slideUp() {
        guard isVisible else { return }
        NSAnimationContext.runAnimationGroup({ ctx in
            ctx.duration = 0.18
            animator().setFrame(frame.offsetBy(dx: 0, dy: 24), display: true)
            animator().alphaValue = 0
        }, completionHandler: { [weak self] in
            MainActor.assumeIsolated { self?.orderOut(nil) }
        })
    }
}

/// Draws the rope and lays the hanging items along it, newest on the left.
final class RopeView: NSView {
    private var urls: [URL] = []
    private var views: [HangingItem] = []
    static let ropeTop: CGFloat = 14
    static let sag: CGFloat = 14

    override var isFlipped: Bool { true }

    func show(_ urls: [URL]) {
        guard urls != self.urls else { return }
        self.urls = urls
        layoutItems()
    }

    func ropeY(_ x: CGFloat) -> CGFloat {
        let t = bounds.width > 0 ? x / bounds.width : 0.5
        return Self.ropeTop + Self.sag * 4 * t * (1 - t)
    }

    func layoutItems() {
        let existing = Dictionary(views.map { ($0.url, $0) }, uniquingKeysWith: { a, _ in a })
        views.forEach { $0.removeFromSuperview() }
        views = []
        var x: CGFloat = 70
        for (i, url) in urls.enumerated() {
            let item = existing[url] ?? HangingItem(url: url)
            let size = item.size
            guard x + size.width < bounds.width - 40 else { break }
            item.setFrameSize(size)
            item.setFrameOrigin(NSPoint(x: x, y: ropeY(x + size.width / 2) - 10))
            item.tilt = [2.5, -2, 1.5, -3, 2, -1.5][i % 6]
            addSubview(item)
            views.append(item)
            x += size.width + 18
        }
        needsDisplay = true
    }

    override func draw(_ dirtyRect: NSRect) {
        let rope = NSBezierPath()
        rope.move(to: NSPoint(x: -4, y: ropeY(0)))
        for x in stride(from: 0, through: bounds.width + 4, by: 6) { rope.line(to: NSPoint(x: x, y: ropeY(x))) }
        rope.lineWidth = 2.5
        rope.lineCapStyle = .round
        NSGraphicsContext.saveGraphicsState()
        let shadow = NSShadow()
        shadow.shadowBlurRadius = 3
        shadow.shadowOffset = NSSize(width: 0, height: -2)
        shadow.shadowColor = NSColor.black.withAlphaComponent(0.35)
        shadow.set()
        NSColor(srgbRed: 0.9, green: 0.86, blue: 0.78, alpha: 1).setStroke()
        rope.stroke()
        NSGraphicsContext.restoreGraphicsState()
        // Twist highlights so it reads as cord, not a line.
        NSColor(srgbRed: 0.72, green: 0.66, blue: 0.56, alpha: 1).setStroke()
        let twist = NSBezierPath()
        for x in stride(from: CGFloat(0), through: bounds.width, by: 7) {
            twist.move(to: NSPoint(x: x, y: ropeY(x) - 1))
            twist.line(to: NSPoint(x: x + 3, y: ropeY(x) + 1))
        }
        twist.lineWidth = 0.8
        twist.stroke()
    }
}

/// One screenshot on the rope, with its peg. Handles every gesture.
final class HangingItem: NSView, NSDraggingSource {
    let url: URL
    private var thumbnail: NSImage?
    private let isMovie: Bool
    private var hovered = false { didSet { needsDisplay = true; frameCenterRotation = hovered ? 0 : tilt } }
    var tilt: CGFloat = 0 { didSet { if !hovered { frameCenterRotation = tilt } } }

    private var downEvent: NSEvent?
    private var longPress: DispatchWorkItem?
    private var singleClick: DispatchWorkItem?
    private var handled = false

    private static let maxThumb = NSSize(width: 168, height: 112)
    private static let pegSpace: CGFloat = 14
    private static let margin: CGFloat = 8

    init(url: URL) {
        self.url = url
        isMovie = ["mov", "mp4"].contains(url.pathExtension.lowercased())
        super.init(frame: .zero)
        wantsLayer = true
        toolTip = "\(url.lastPathComponent)\nClick: copy · Double-click: open · Hold: Quick Look · Drag: take it down"
        loadThumbnail()
    }

    required init?(coder: NSCoder) { fatalError() }

    override var isFlipped: Bool { true }

    private var pixelSize: NSSize {
        guard let thumbnail, thumbnail.size.width > 0 else { return NSSize(width: 160, height: 100) }
        let s = min(Self.maxThumb.width / thumbnail.size.width, Self.maxThumb.height / thumbnail.size.height)
        return NSSize(width: thumbnail.size.width * s, height: thumbnail.size.height * s)
    }

    var size: NSSize {
        let p = pixelSize
        return NSSize(width: p.width + Self.margin * 2 + 8, height: p.height + Self.pegSpace + Self.margin * 2 + 8)
    }

    private var photoRect: NSRect {
        let p = pixelSize
        return NSRect(x: Self.margin, y: Self.pegSpace + Self.margin, width: p.width + 8, height: p.height + 8)
    }

    private var closeRect: NSRect { NSRect(x: photoRect.maxX - 14, y: photoRect.minY - 8, width: 20, height: 20) }

    private func loadThumbnail() {
        if isMovie {
            let generator = AVAssetImageGenerator(asset: AVURLAsset(url: url))
            generator.appliesPreferredTrackTransform = true
            generator.maximumSize = CGSize(width: 400, height: 400)
            Task { @MainActor [weak self] in
                guard let (image, _) = try? await generator.image(at: .zero) else { return }
                self?.thumbnail = NSImage(cgImage: image, size: .zero)
                self?.needsDisplay = true
            }
        } else if let image = try? Images.load(url, maxPixels: 400) {
            thumbnail = NSImage(cgImage: image, size: NSSize(width: image.width, height: image.height))
        }
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(NSTrackingArea(rect: bounds, options: [.mouseEnteredAndExited, .activeAlways, .inVisibleRect], owner: self))
    }

    override func mouseEntered(with event: NSEvent) { hovered = true }
    override func mouseExited(with event: NSEvent) { hovered = false }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func draw(_ dirtyRect: NSRect) {
        let photo = photoRect
        // Print with a white border and a soft shadow.
        NSGraphicsContext.saveGraphicsState()
        let shadow = NSShadow()
        shadow.shadowBlurRadius = hovered ? 10 : 6
        shadow.shadowOffset = NSSize(width: 0, height: -(hovered ? 5 : 3))
        shadow.shadowColor = NSColor.black.withAlphaComponent(0.35)
        shadow.set()
        NSColor.white.setFill()
        NSBezierPath(roundedRect: photo, xRadius: 3, yRadius: 3).fill()
        NSGraphicsContext.restoreGraphicsState()
        thumbnail?.draw(in: photo.insetBy(dx: 4, dy: 4), from: .zero, operation: .sourceOver, fraction: 1,
                        respectFlipped: true, hints: [.interpolation: NSImageInterpolation.high.rawValue])
        if hovered {
            NSColor.controlAccentColor.setStroke()
            let ring = NSBezierPath(roundedRect: photo.insetBy(dx: -1, dy: -1), xRadius: 4, yRadius: 4)
            ring.lineWidth = 2
            ring.stroke()
        }

        if isMovie {
            let c = NSPoint(x: photo.midX, y: photo.midY)
            NSColor.black.withAlphaComponent(0.5).setFill()
            NSBezierPath(ovalIn: NSRect(x: c.x - 16, y: c.y - 16, width: 32, height: 32)).fill()
            NSColor.white.setFill()
            let play = NSBezierPath()
            play.move(to: NSPoint(x: c.x - 5, y: c.y - 8))
            play.line(to: NSPoint(x: c.x + 9, y: c.y))
            play.line(to: NSPoint(x: c.x - 5, y: c.y + 8))
            play.close()
            play.fill()
        }

        // Wooden peg gripping the rope and the top of the print.
        let peg = NSRect(x: photo.midX - 5, y: 0, width: 10, height: Self.pegSpace + Self.margin + 10)
        NSGraphicsContext.saveGraphicsState()
        let pegShadow = NSShadow()
        pegShadow.shadowBlurRadius = 2
        pegShadow.shadowOffset = NSSize(width: 0, height: -1)
        pegShadow.shadowColor = NSColor.black.withAlphaComponent(0.4)
        pegShadow.set()
        NSColor(srgbRed: 0.8, green: 0.6, blue: 0.38, alpha: 1).setFill()
        NSBezierPath(roundedRect: peg, xRadius: 2.5, yRadius: 2.5).fill()
        NSGraphicsContext.restoreGraphicsState()
        NSColor(white: 0.6, alpha: 1).setFill()
        NSRect(x: peg.minX, y: peg.midY - 1, width: peg.width, height: 2).fill()

        if hovered {
            let close = closeRect
            NSColor(white: 0.15, alpha: 0.85).setFill()
            NSBezierPath(ovalIn: close).fill()
            NSColor.white.setStroke()
            let x = NSBezierPath()
            x.move(to: NSPoint(x: close.minX + 6.5, y: close.minY + 6.5)); x.line(to: NSPoint(x: close.maxX - 6.5, y: close.maxY - 6.5))
            x.move(to: NSPoint(x: close.maxX - 6.5, y: close.minY + 6.5)); x.line(to: NSPoint(x: close.minX + 6.5, y: close.maxY - 6.5))
            x.lineWidth = 1.6
            x.stroke()
        }
    }

    // MARK: Gestures

    override func mouseDown(with event: NSEvent) {
        handled = false
        downEvent = event
        let p = convert(event.locationInWindow, from: nil)
        if hovered, closeRect.insetBy(dx: -3, dy: -3).contains(p) {
            handled = true
            Clothesline.shared.discard(url)
            return
        }
        let press = DispatchWorkItem { [weak self] in
            guard let self, !self.handled else { return }
            self.handled = true
            Clothesline.shared.quickLook(self.url)
        }
        longPress = press
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.55, execute: press)
    }

    override func pressureChange(with event: NSEvent) {
        if event.stage >= 2, !handled {
            handled = true
            longPress?.cancel()
            Clothesline.shared.quickLook(url)
        }
    }

    override func mouseDragged(with event: NSEvent) {
        guard !handled, let down = downEvent else { return }
        let a = down.locationInWindow, b = event.locationInWindow
        guard hypot(b.x - a.x, b.y - a.y) > 4 else { return }
        handled = true
        longPress?.cancel()
        let item = NSDraggingItem(pasteboardWriter: url as NSURL)
        item.setDraggingFrame(photoRect, contents: thumbnail ?? NSWorkspace.shared.icon(forFile: url.path))
        Clothesline.shared.isDraggingItem = true
        beginDraggingSession(with: [item], event: down, source: self)
    }

    override func mouseUp(with event: NSEvent) {
        longPress?.cancel()
        guard !handled else { return }
        handled = true
        if event.clickCount >= 2 {
            singleClick?.cancel()
            Clothesline.shared.openInPreview(url)
        } else {
            let click = DispatchWorkItem { [weak self] in
                guard let self else { return }
                Clothesline.shared.copy(self.url)
            }
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
        if operation == .delete {
            Clothesline.shared.discard(url)
        } else {
            Clothesline.shared.reload()
        }
    }
}

// MARK: - Quick Look

/// An invisible key window that drives the shared Quick Look panel for long-press and force click.
@MainActor
final class QuickLooker: NSObject, QLPreviewPanelDataSource, QLPreviewPanelDelegate {
    static let shared = QuickLooker()
    private var url: URL?
    private lazy var host: NSWindow = {
        let window = QLHostWindow(contentRect: NSRect(x: -10_000, y: -10_000, width: 1, height: 1),
                                  styleMask: [.borderless], backing: .buffered, defer: false)
        window.looker = self
        window.isReleasedWhenClosed = false
        return window
    }()

    func show(_ url: URL) {
        self.url = url
        NSApp.activate()
        host.makeKeyAndOrderFront(nil)
        guard let panel = QLPreviewPanel.shared() else { return }
        panel.updateController()
        panel.reloadData()
        panel.makeKeyAndOrderFront(nil)
    }

    nonisolated func numberOfPreviewItems(in panel: QLPreviewPanel!) -> Int { 1 }

    nonisolated func previewPanel(_ panel: QLPreviewPanel!, previewItemAt index: Int) -> (any QLPreviewItem)! {
        MainActor.assumeIsolated { url as NSURL? }
    }
}

final class QLHostWindow: NSWindow {
    weak var looker: QuickLooker?
    override var canBecomeKey: Bool { true }
    override func acceptsPreviewPanelControl(_ panel: QLPreviewPanel!) -> Bool { true }
    override func beginPreviewPanelControl(_ panel: QLPreviewPanel!) {
        panel.dataSource = looker
        panel.delegate = looker
    }
    override func endPreviewPanelControl(_ panel: QLPreviewPanel!) {
        panel.dataSource = nil
        panel.delegate = nil
    }
}

// MARK: - Screenshot takeover

/// Points macOS screenshots at the clothesline folder and turns off the floating thumbnail,
/// remembering the user's own settings so they can be put back.
enum ScreenshotTakeover {
    private static let domain = "com.apple.screencapture" as CFString
    private static let savedKey = "savedScreencaptureSettings"

    static func enable(into folder: URL) {
        let defaults = UserDefaults.standard
        if defaults.dictionary(forKey: savedKey) == nil {
            var saved: [String: Any] = [:]
            for key in ["location", "show-thumbnail"] {
                if let value = CFPreferencesCopyAppValue(key as CFString, domain) { saved[key] = value }
                else { saved[key] = "__unset__" }
            }
            defaults.set(saved, forKey: savedKey)
        }
        CFPreferencesSetAppValue("location" as CFString, folder.path as CFString, domain)
        CFPreferencesSetAppValue("show-thumbnail" as CFString, kCFBooleanFalse, domain)
        CFPreferencesAppSynchronize(domain)
    }

    static func restore() {
        let defaults = UserDefaults.standard
        guard let saved = defaults.dictionary(forKey: savedKey) else { return }
        for key in ["location", "show-thumbnail"] {
            let value = saved[key]
            if value as? String == "__unset__" || value == nil {
                CFPreferencesSetAppValue(key as CFString, nil, domain)
            } else {
                CFPreferencesSetAppValue(key as CFString, value as CFPropertyList, domain)
            }
        }
        CFPreferencesAppSynchronize(domain)
        defaults.removeObject(forKey: savedKey)
    }
}

// MARK: - Global hotkey

/// ⌃⌥T anywhere toggles the rope. Uses Carbon hotkeys, which need no extra permission.
final class HotKey {
    private var ref: EventHotKeyRef?
    nonisolated(unsafe) private static var action: (() -> Void)?

    init(_ action: @escaping () -> Void) {
        Self.action = action
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), { _, _, _ in
            DispatchQueue.main.async { HotKey.action?() }
            return noErr
        }, 1, &spec, nil, nil)
        let id = EventHotKeyID(signature: OSType(0x5357_5654), id: 1)  // 'SWVT'
        RegisterEventHotKey(UInt32(kVK_ANSI_T), UInt32(controlKey | optionKey), id, GetApplicationEventTarget(), 0, &ref)
    }
}
