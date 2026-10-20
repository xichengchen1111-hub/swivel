import AppKit

struct ActionResult {
    var message: String
    var files: [URL] = []
}

struct WheelItem {
    let title: String
    let run: @MainActor ([URL]) async throws -> ActionResult
}

/// Owns the wheel panel: opens it at the cursor on a key tap, pages it, and runs the dropped-on item.
@MainActor
final class WheelController {
    private let panel = WheelPanel()
    private let toast = Toast()
    private var mode: WheelMode?
    private var pages: [[WheelItem]] = []
    private var page = 0
    private var isDropping = false
    private static let perPage = 8

    init() {
        panel.wheel.onDrop = { [weak self] item, urls in self?.perform(item, urls: urls) }
    }

    func keyTapped(_ newMode: WheelMode, urls: [URL]) {
        if newMode == mode, panel.isVisible {
            guard pages.count > 1 else { return }
            page = (page + 1) % pages.count
            refresh()
            return
        }
        let items = Catalog.items(for: urls, mode: newMode)
        guard !items.isEmpty else {
            toast.show("Nothing to show for this file", symbol: "nosign")
            return
        }
        mode = newMode
        panel.wheel.style = Prefs.style
        pages = stride(from: 0, to: items.count, by: Self.perPage).map {
            Array(items[$0..<min($0 + Self.perPage, items.count)])
        }
        page = 0
        panel.wheel.subtitle = urls.count == 1 ? urls[0].lastPathComponent : "\(urls.count) files"
        refresh()
        panel.show(at: NSEvent.mouseLocation)
    }

    func demoHover(_ index: Int) { panel.wheel.hovered = index }

    func dragEnded() {
        // The drop callback can arrive just after the mouse button is seen going up.
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.35) { [weak self] in
            guard let self, !self.isDropping else { return }
            self.close()
        }
    }

    private func refresh() {
        guard let mode else { return }
        panel.wheel.configure(items: pages[page], mode: mode, page: page, pageCount: pages.count)
        panel.relayout()
    }

    private func close() {
        panel.hide()
        mode = nil
    }

    private func perform(_ item: WheelItem, urls: [URL]) {
        isDropping = true
        close()
        isDropping = false
        let busy = DispatchWorkItem { [toast] in
            toast.show(L(item.title) + "…", symbol: "hourglass", autoHide: false)
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.25, execute: busy)
        Task { @MainActor in
            do {
                let result = try await item.run(urls)
                busy.cancel()
                toast.show(result.message, symbol: "checkmark.circle.fill", files: result.files)
            } catch is Cancelled {
                busy.cancel()
                toast.show("Cancelled", symbol: "xmark.circle", tint: .secondaryLabelColor)
            } catch let info as Info {
                busy.cancel()
                toast.show(info.message, symbol: "info.circle.fill")
            } catch {
                busy.cancel()
                toast.show(error.localizedDescription, symbol: "exclamationmark.triangle.fill", tint: .systemRed)
            }
        }
    }
}

final class WheelPanel: NSPanel {
    let wheel = WheelView(frame: NSRect(x: 0, y: 0, width: 320, height: 320))

    init() {
        super.init(contentRect: wheel.frame, styleMask: [.borderless, .nonactivatingPanel],
                   backing: .buffered, defer: false)
        isOpaque = false
        backgroundColor = .clear
        hasShadow = false
        level = .popUpMenu
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
        isReleasedWhenClosed = false
        hidesOnDeactivate = false

        // Clear glass under styles that want a frosted backdrop (wheel, list).
        let container = NSView(frame: wheel.frame)
        container.addSubview(glass)
        container.addSubview(wheel)
        contentView = container
    }

    private let glass = WheelPanel.makeGlass(frame: .zero, radius: 0)

    /// Resizes the panel and its glass for the menu's current style and item count.
    func relayout() {
        let size = WheelView.size(for: wheel.style, count: wheel.items.count)
        setContentSize(size)
        contentView?.frame = NSRect(origin: .zero, size: size)
        wheel.frame = NSRect(origin: .zero, size: size)
        if let (rect, radius) = wheel.backdrop {
            glass.isHidden = false
            glass.frame = rect
            if #available(macOS 26.0, *), let glass = glass as? NSGlassEffectView {
                glass.cornerRadius = radius
            } else {
                glass.layer?.cornerRadius = radius
            }
        } else {
            glass.isHidden = true
        }
        wheel.needsDisplay = true
    }

    private static func makeGlass(frame: NSRect, radius: CGFloat) -> NSView {
        if #available(macOS 26.0, *) {
            let glass = NSGlassEffectView(frame: frame)
            glass.style = .clear
            glass.cornerRadius = radius
            return glass
        }
        let blur = NSVisualEffectView(frame: frame)
        blur.material = .hudWindow
        blur.blendingMode = .behindWindow
        blur.state = .active
        blur.alphaValue = 0.6
        blur.wantsLayer = true
        blur.layer?.cornerRadius = radius
        blur.layer?.masksToBounds = true
        return blur
    }

    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }

    func show(at point: NSPoint) {
        let size = frame.size
        // The wheel centres on the cursor (its hub is a cancel zone); other styles open beside it so the
        // pointer doesn't land on an item straight away.
        var origin: NSPoint = switch wheel.style {
        case .wheel: NSPoint(x: point.x - size.width / 2, y: point.y - size.height / 2)
        case .list: NSPoint(x: point.x + 28, y: point.y - size.height / 2)
        default: NSPoint(x: point.x - size.width / 2, y: point.y + 28)
        }
        if let screen = NSScreen.screens.first(where: { NSMouseInRect(point, $0.frame, false) }) {
            let bounds = screen.visibleFrame
            origin.x = min(max(origin.x, bounds.minX), bounds.maxX - size.width)
            origin.y = min(max(origin.y, bounds.minY), bounds.maxY - size.height)
        }
        setFrameOrigin(origin)
        alphaValue = 0
        orderFrontRegardless()
        contentView?.popIn()
        NSAnimationContext.runAnimationGroup { ctx in
            ctx.duration = 0.14
            animator().alphaValue = 1
        }
    }

    func hide() {
        orderOut(nil)
        wheel.hovered = nil
    }
}
