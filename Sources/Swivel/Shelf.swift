import AppKit

/// A floating shelf that holds files so they can be dragged into another app later.
@MainActor
final class Shelf: NSObject, NSWindowDelegate {
    static let shared = Shelf()

    private var panel: NSPanel?
    private let stack = NSStackView()
    private var urls: [URL] = []

    func add(_ new: [URL]) {
        for url in new where !urls.contains(url) { urls.append(url) }
        rebuild()
        show()
    }

    func show() {
        let panel = self.panel ?? makePanel()
        self.panel = panel
        rebuild()
        if !panel.isVisible, let area = NSScreen.main?.visibleFrame {
            panel.setFrameTopLeftPoint(NSPoint(x: area.maxX - panel.frame.width - 16, y: area.maxY - 16))
        }
        panel.orderFrontRegardless()
    }

    private func makePanel() -> NSPanel {
        let panel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 240, height: 320),
                            styleMask: [.titled, .closable, .resizable, .utilityWindow, .nonactivatingPanel],
                            backing: .buffered, defer: false)
        panel.title = "Shelf"
        panel.level = .floating
        panel.isFloatingPanel = true
        panel.isReleasedWhenClosed = false
        panel.hidesOnDeactivate = false
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]

        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 2
        stack.edgeInsets = NSEdgeInsets(top: 8, left: 8, bottom: 8, right: 8)

        let scroll = NSScrollView()
        scroll.hasVerticalScroller = true
        scroll.drawsBackground = false
        let flipped = FlippedView()
        flipped.translatesAutoresizingMaskIntoConstraints = false
        stack.translatesAutoresizingMaskIntoConstraints = false
        flipped.addSubview(stack)
        scroll.documentView = flipped
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: flipped.leadingAnchor),
            stack.trailingAnchor.constraint(equalTo: flipped.trailingAnchor),
            stack.topAnchor.constraint(equalTo: flipped.topAnchor),
            stack.bottomAnchor.constraint(equalTo: flipped.bottomAnchor),
            flipped.widthAnchor.constraint(equalTo: scroll.contentView.widthAnchor),
        ])

        let clear = NSButton(title: L("Clear"), target: self, action: #selector(clearAll))
        clear.bezelStyle = .accessoryBarAction
        let root = NSStackView(views: [scroll, clear])
        root.orientation = .vertical
        root.edgeInsets = NSEdgeInsets(top: 0, left: 0, bottom: 8, right: 0)
        panel.contentView = root
        return panel
    }

    private func rebuild() {
        stack.arrangedSubviews.forEach { $0.removeFromSuperview() }
        if urls.isEmpty {
            let empty = NSTextField(labelWithString: L("Drag a file, tap ⌃, drop on Float."))
            empty.textColor = .secondaryLabelColor
            stack.addArrangedSubview(empty)
        }
        for url in urls {
            let row = ShelfRow(url: url)
            stack.addArrangedSubview(row)
            row.widthAnchor.constraint(equalTo: stack.widthAnchor, constant: -16).isActive = true
        }
    }

    @objc private func clearAll() {
        urls.removeAll()
        rebuild()
    }
}

private final class FlippedView: NSView {
    override var isFlipped: Bool { true }
}

private final class ShelfRow: NSView, NSDraggingSource {
    let url: URL

    init(url: URL) {
        self.url = url
        super.init(frame: .zero)
        let icon = NSImageView(image: NSWorkspace.shared.icon(forFile: url.path))
        icon.widthAnchor.constraint(equalToConstant: 28).isActive = true
        icon.heightAnchor.constraint(equalToConstant: 28).isActive = true
        let name = NSTextField(labelWithString: url.lastPathComponent)
        name.lineBreakMode = .byTruncatingMiddle
        name.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        let row = NSStackView(views: [icon, name])
        row.spacing = 8
        row.translatesAutoresizingMaskIntoConstraints = false
        addSubview(row)
        NSLayoutConstraint.activate([
            row.leadingAnchor.constraint(equalTo: leadingAnchor),
            row.trailingAnchor.constraint(equalTo: trailingAnchor),
            row.topAnchor.constraint(equalTo: topAnchor, constant: 2),
            row.bottomAnchor.constraint(equalTo: bottomAnchor, constant: -2),
        ])
        toolTip = url.path
    }

    required init?(coder: NSCoder) { fatalError() }

    override func mouseDown(with event: NSEvent) {}

    override func mouseDragged(with event: NSEvent) {
        let item = NSDraggingItem(pasteboardWriter: url as NSURL)
        item.setDraggingFrame(NSRect(x: 0, y: 0, width: 32, height: 32),
                              contents: NSWorkspace.shared.icon(forFile: url.path))
        beginDraggingSession(with: [item], event: event, source: self)
    }

    func draggingSession(_ session: NSDraggingSession,
                         sourceOperationMaskFor context: NSDraggingContext) -> NSDragOperation {
        context == .outsideApplication ? [.copy, .generic] : []
    }
}
