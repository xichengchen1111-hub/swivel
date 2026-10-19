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
            toast.show("Nothing to \(newMode.title.lowercased()) for this file", symbol: "nosign")
            return
        }
        mode = newMode
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
            toast.show("\(item.title)…", symbol: "hourglass", autoHide: false)
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.25, execute: busy)
        Task { @MainActor in
            do {
                let result = try await item.run(urls)
                busy.cancel()
                toast.show(result.message, symbol: "checkmark.circle.fill", files: result.files)
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
        contentView = wheel
    }

    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }

    func show(at point: NSPoint) {
        let size = frame.size
        var origin = NSPoint(x: point.x - size.width / 2, y: point.y - size.height / 2)
        if let screen = NSScreen.screens.first(where: { NSMouseInRect(point, $0.frame, false) }) {
            let bounds = screen.visibleFrame
            origin.x = min(max(origin.x, bounds.minX), bounds.maxX - size.width)
            origin.y = min(max(origin.y, bounds.minY), bounds.maxY - size.height)
        }
        setFrameOrigin(origin)
        alphaValue = 0
        orderFrontRegardless()
        wheel.popIn()
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
