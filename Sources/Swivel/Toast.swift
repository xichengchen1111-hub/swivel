import AppKit

/// A small HUD at the bottom of the screen reporting what happened. Click it to reveal the output.
@MainActor
final class Toast {
    static let shared = Toast()
    private let panel: NSPanel
    private let icon = NSImageView()
    private let label = NSTextField(labelWithString: L(""))
    private var files: [URL] = []
    private var hideWork: DispatchWorkItem?

    init() {
        panel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 300, height: 44),
                        styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.level = .statusBar
        panel.hasShadow = true
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.isReleasedWhenClosed = false
        panel.hidesOnDeactivate = false

        let background = NSVisualEffectView()
        background.material = .popover
        background.state = .active
        background.wantsLayer = true
        background.layer?.cornerRadius = 16
        background.layer?.borderWidth = 0.5
        background.layer?.borderColor = NSColor.separatorColor.cgColor
        background.layer?.masksToBounds = true

        icon.symbolConfiguration = .init(pointSize: 15, weight: .semibold)
        label.font = .systemFont(ofSize: 13, weight: .medium)
        label.lineBreakMode = .byTruncatingMiddle
        label.maximumNumberOfLines = 1

        let stack = NSStackView(views: [icon, label])
        stack.spacing = 8
        stack.edgeInsets = NSEdgeInsets(top: 0, left: 14, bottom: 0, right: 16)
        stack.translatesAutoresizingMaskIntoConstraints = false
        background.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: background.leadingAnchor),
            stack.trailingAnchor.constraint(equalTo: background.trailingAnchor),
            stack.topAnchor.constraint(equalTo: background.topAnchor),
            stack.bottomAnchor.constraint(equalTo: background.bottomAnchor),
        ])
        panel.contentView = background
        background.addGestureRecognizer(NSClickGestureRecognizer(target: self, action: #selector(clicked)))
    }

    func show(_ message: String, symbol: String, files: [URL] = [], tint: NSColor = .controlAccentColor, autoHide: Bool = true) {
        self.files = files
        icon.image = NSImage(systemSymbolName: symbol, accessibilityDescription: nil)
        icon.contentTintColor = tint
        label.stringValue = files.isEmpty ? L(message) : L(message) + "  ·  " + L("Show")

        let width = min(max(label.intrinsicContentSize.width + 66, 180), 520)
        let screen = NSScreen.screens.first { NSMouseInRect(NSEvent.mouseLocation, $0.frame, false) } ?? NSScreen.main
        let area = screen?.visibleFrame ?? .zero
        panel.setFrame(NSRect(x: area.midX - width / 2, y: area.minY + 70, width: width, height: 48), display: true)
        panel.alphaValue = 1
        panel.orderFrontRegardless()

        hideWork?.cancel()
        guard autoHide else { return }
        let work = DispatchWorkItem { [weak self] in self?.fadeOut() }
        hideWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + (files.isEmpty ? 2.2 : 4), execute: work)
    }

    private func fadeOut() {
        NSAnimationContext.runAnimationGroup({ ctx in
            ctx.duration = 0.25
            panel.animator().alphaValue = 0
        }, completionHandler: { [panel] in
            MainActor.assumeIsolated { panel.orderOut(nil) }
        })
    }

    @objc private func clicked() {
        if !files.isEmpty { NSWorkspace.shared.activateFileViewerSelecting(files) }
        hideWork?.cancel()
        panel.orderOut(nil)
    }
}
