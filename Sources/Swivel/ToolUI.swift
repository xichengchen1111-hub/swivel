import AppKit
import UniformTypeIdentifiers

@MainActor
enum ToolUI {
    static func button(_ title: String, target: AnyObject?, action: Selector?) -> NSButton {
        let button = NSButton(title: L(title), target: target, action: action)
        button.bezelStyle = .push
        return button
    }

    static func note(_ title: String) -> NSTextField {
        let label = NSTextField(wrappingLabelWithString: L(title))
        label.textColor = .secondaryLabelColor
        label.font = .systemFont(ofSize: 12)
        return label
    }

    static func show(_ window: NSWindow) {
        NSApp.setActivationPolicy(.regular)
        NSApp.mainMenu = AppWindow.mainMenu()
        NSApp.activate()
        window.makeKeyAndOrderFront(nil)
    }

    static func didClose() {
        DispatchQueue.main.async {
            if !NSApp.windows.contains(where: { $0.isVisible && $0.styleMask.contains(.titled) }) {
                NSApp.setActivationPolicy(.accessory)
            }
        }
    }

    static func chooseFiles(imagesOnly: Bool = false, multiple: Bool = true, folders: Bool = false) -> [URL] {
        let panel = NSOpenPanel()
        panel.allowsMultipleSelection = multiple
        panel.canChooseDirectories = folders
        panel.canChooseFiles = true
        if imagesOnly { panel.allowedContentTypes = [.image] }
        panel.prompt = L("Choose")
        NSApp.activate()
        return panel.runModal() == .OK ? panel.urls : []
    }

    static func fileURLs(from pasteboard: NSPasteboard) -> [URL] {
        (pasteboard.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL]) ?? []
    }

    static func stack(_ views: [NSView], vertical: Bool = true, spacing: CGFloat = 12) -> NSStackView {
        let stack = NSStackView(views: views)
        stack.orientation = vertical ? .vertical : .horizontal
        stack.alignment = vertical ? .leading : .centerY
        stack.spacing = spacing
        return stack
    }

    static func embed(_ child: NSView, in parent: NSView, inset: CGFloat = 20) {
        child.translatesAutoresizingMaskIntoConstraints = false
        parent.addSubview(child)
        NSLayoutConstraint.activate([
            child.leadingAnchor.constraint(equalTo: parent.leadingAnchor, constant: inset),
            child.trailingAnchor.constraint(equalTo: parent.trailingAnchor, constant: -inset),
            child.topAnchor.constraint(equalTo: parent.safeAreaLayoutGuide.topAnchor, constant: inset),
            child.bottomAnchor.constraint(equalTo: parent.bottomAnchor, constant: -inset),
        ])
    }
}

/// Native text fields retain editing shortcuts even in a menu-bar app.
final class ToolTable: NSTableView {
    var deleteSelection: (() -> Void)?
    var previewSelection: (() -> Void)?

    override func keyDown(with event: NSEvent) {
        if event.keyCode == 51 { deleteSelection?(); return }
        if event.keyCode == 49 { previewSelection?(); return }
        super.keyDown(with: event)
    }
}
