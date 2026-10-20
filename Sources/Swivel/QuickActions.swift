import AppKit
import Carbon

enum QuickShortcut: String, CaseIterable {
    case space, k, p
    var title: String {
        switch self { case .space: "⌃⇧ Space"; case .k: "⌃⌥ K"; case .p: "⌘⇧ P" }
    }
    var keyCode: UInt32 {
        switch self { case .space: UInt32(kVK_Space); case .k: UInt32(kVK_ANSI_K); case .p: UInt32(kVK_ANSI_P) }
    }
    var modifiers: UInt32 {
        switch self { case .space: UInt32(controlKey | shiftKey); case .k: UInt32(controlKey | optionKey); case .p: UInt32(cmdKey | shiftKey) }
    }
}

@MainActor
struct QuickCommand {
    let id: String
    let title: String
    let detail: String
    let symbol: String
    let run: () async throws -> ActionResult?
}

@MainActor
final class QuickActions: NSObject, NSWindowDelegate, NSSearchFieldDelegate, NSTableViewDataSource, NSTableViewDelegate {
    static let shared = QuickActions()
    static let shortcutChanged = Notification.Name("SwivelShortcutChanged")
    private(set) var shortcutAvailable = true
    private var hotKey: HotKey?
    private var registeredShortcut: QuickShortcut?
    private var window: NSPanel?
    private let search = NSSearchField()
    private let table = NSTableView()
    private let filesLabel = ToolUI.note("")
    private let hint = ToolUI.note("↑ ↓ to select · Return to run · Esc to close")
    private var chosenFiles: [URL] = []
    private var commands: [QuickCommand] = []
    private var allCommands: [QuickCommand] = []
    private var runButton: NSButton!
    private var chooseButton: NSButton!
    private var clearButton: NSButton!
    private var running = false

    func syncSettings() {
        if !Feature.quickActions.isOn {
            hotKey?.unregister(); hotKey = nil; registeredShortcut = nil
            hide()
        } else if registeredShortcut != Prefs.quickShortcut {
            hotKey?.unregister()
            let key = Prefs.quickShortcut
            hotKey = HotKey(keyCode: key.keyCode, modifiers: key.modifiers) { [weak self] in self?.toggle() }
            registeredShortcut = key
            shortcutAvailable = hotKey?.status == noErr
            NotificationCenter.default.post(name: Self.shortcutChanged, object: nil)
        }
        if window?.isVisible == true, !running { rebuild() }
    }

    func show(files: [URL]? = nil) {
        guard Feature.quickActions.isOn else { return }
        if window == nil { window = makePanel() }
        if let files, !running { chosenFiles = files }
        if !running { search.stringValue = ""; rebuild() }
        if let window {
            ToolUI.show(window)
            window.makeFirstResponder(search)
        }
    }
    func hide() { window?.orderOut(nil); ToolUI.didClose() }
    private func toggle() { if window?.isVisible == true { hide() } else { show() } }

    private func makePanel() -> NSPanel {
        let panel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 620, height: 500),
                            styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
        panel.title = L("Quick Actions")
        panel.toolbarStyle = .unified
        panel.isReleasedWhenClosed = false
        panel.minSize = NSSize(width: 540, height: 380)
        panel.delegate = self
        panel.level = .floating
        panel.center()
        search.placeholderString = L("Search actions…")
        search.controlSize = .large
        search.sendsSearchStringImmediately = true
        search.delegate = self
        search.setAccessibilityLabel(L("Search actions…"))
        table.addTableColumn(NSTableColumn(identifier: .init("command")))
        table.headerView = nil; table.style = .inset; table.rowHeight = 48
        table.dataSource = self; table.delegate = self
        table.target = self; table.doubleAction = #selector(runSelected)
        let scroll = NSScrollView()
        scroll.documentView = table; scroll.hasVerticalScroller = true
        scroll.drawsBackground = false
        chooseButton = ToolUI.button("Choose Files…", target: self, action: #selector(chooseFiles))
        clearButton = ToolUI.button("Clear", target: self, action: #selector(clearFiles))
        filesLabel.lineBreakMode = .byTruncatingMiddle
        let fileRow = NSStackView()
        fileRow.addView(filesLabel, in: .leading)
        fileRow.addView(clearButton, in: .trailing)
        fileRow.addView(chooseButton, in: .trailing)
        runButton = ToolUI.button("Run", target: self, action: #selector(runSelected))
        runButton.keyEquivalent = "\r"
        let footer = NSStackView()
        footer.addView(hint, in: .leading); footer.addView(runButton, in: .trailing)
        let separator = NSBox(); separator.boxType = .separator
        let root = ToolUI.stack([search, fileRow, separator, scroll, footer], spacing: 12)
        let material = FileDropMaterial()
        material.material = .popover
        material.blendingMode = .behindWindow
        material.onDrop = { [weak self] urls in
            guard let self, Feature.quickActions.isOn, !running else { return }
            chosenFiles = urls; rebuild()
        }
        panel.contentView = material
        ToolUI.embed(root, in: material)
        for view in [search, fileRow, separator, scroll, footer] { view.widthAnchor.constraint(equalTo: root.widthAnchor).isActive = true }
        scroll.heightAnchor.constraint(greaterThanOrEqualToConstant: 200).isActive = true
        scroll.setContentHuggingPriority(.defaultLow, for: .vertical)
        return panel
    }

    /// Does not read another app's selection; files arrive through a picker or an explicit drop.
    static func makeCommands(files: [URL]) -> [QuickCommand] {
        var list = [QuickCommand(id: "settings", title: L("Open Swivel…"), detail: L("Settings"), symbol: "gearshape") {
            AppWindow.shared.show(); return nil
        }]
        if Feature.shelf.isOn {
            list.append(QuickCommand(id: "shelf", title: L("Show Shelf"), detail: Feature.shelf.title, symbol: Feature.shelf.symbol) { Shelf.shared.show(); return nil })
        }
        if Feature.clothesline.isOn {
            list.append(QuickCommand(id: "rope", title: L("Show Clothesline"), detail: Feature.clothesline.title, symbol: Feature.clothesline.symbol) {
                Clothesline.shared.show(force: true); return nil
            })
        }
        guard !files.isEmpty else {
            if Feature.textCapture.isOn {
                list.append(QuickCommand(id: "chooseOCR", title: L("Extract Text"), detail: L("Choose an image…"), symbol: Feature.textCapture.symbol) {
                    if let url = ToolUI.chooseFiles(imagesOnly: true, multiple: false).first { TextCaptureWindow.present(url) }
                    return nil
                })
            }
            return list
        }
        for mode in WheelMode.allCases {
            let items = Feature.wheel.isOn ? Catalog.items(for: files, mode: mode) : Catalog.standaloneItems(for: files, mode: mode)
            list += items.map { item in
                QuickCommand(id: "\(mode.id).\(item.title)", title: item.displayTitle, detail: mode.title, symbol: mode.symbol) {
                    try await item.run(files)
                }
            }
        }
        return list
    }

    static func matching(_ commands: [QuickCommand], query: String) -> [QuickCommand] {
        let terms = query.split(whereSeparator: \.isWhitespace).map(String.init)
        return commands.filter { command in
            terms.allSatisfy { command.title.localizedStandardContains($0) || command.detail.localizedStandardContains($0) || command.id.localizedStandardContains($0) }
        }
    }
    private func rebuild() {
        filesLabel.stringValue = chosenFiles.isEmpty ? L("Choose files or drop them here") : chosenFiles.count == 1 ? chosenFiles[0].lastPathComponent : L("%d files", chosenFiles.count)
        filesLabel.toolTip = chosenFiles.map(\.path).joined(separator: "\n")
        clearButton?.isEnabled = !chosenFiles.isEmpty
        allCommands = Self.makeCommands(files: chosenFiles)
        filter()
    }
    private func filter() {
        commands = Self.matching(allCommands, query: search.stringValue)
        table.reloadData()
        if !commands.isEmpty { table.selectRowIndexes([0], byExtendingSelection: false) }
        runButton?.isEnabled = !commands.isEmpty && !running
        hint.stringValue = commands.isEmpty ? L("No matching actions. Try another search.") : L("↑ ↓ to select · Return to run · Esc to close")
    }
    @objc private func chooseFiles() {
        let urls = ToolUI.chooseFiles(folders: true)
        if !urls.isEmpty { chosenFiles = urls; rebuild() }
    }
    @objc private func clearFiles() { chosenFiles = []; rebuild() }
    @objc private func runSelected() {
        guard Feature.quickActions.isOn, !running, commands.indices.contains(table.selectedRow) else { return }
        let command = commands[table.selectedRow]
        // Re-resolve against enabled features, so a stale panel cannot run a disabled action.
        guard Self.makeCommands(files: chosenFiles).contains(where: { $0.id == command.id }) else { rebuild(); return }
        running = true
        runButton.isEnabled = false; chooseButton.isEnabled = false; clearButton.isEnabled = false; search.isEnabled = false
        hide()
        Task { @MainActor in
            defer {
                running = false; chooseButton.isEnabled = true; search.isEnabled = true
                rebuild()
            }
            do {
                if let result = try await command.run() { Toast.shared.show(result.message, symbol: "checkmark.circle", files: result.files) }
            } catch is Cancelled { }
            catch { NSAlert(error: error).runModal() }
        }
    }
    func numberOfRows(in tableView: NSTableView) -> Int { commands.count }
    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        let command = commands[row]
        let image = NSImageView(image: NSImage(systemSymbolName: command.symbol, accessibilityDescription: nil) ?? NSImage())
        image.contentTintColor = .controlAccentColor
        image.widthAnchor.constraint(equalToConstant: 28).isActive = true
        let title = NSTextField(labelWithString: command.title)
        title.lineBreakMode = .byTruncatingTail
        let subtitle = ToolUI.note(command.detail)
        return ToolUI.stack([image, ToolUI.stack([title, subtitle], spacing: 2)], vertical: false)
    }
    func controlTextDidChange(_ obj: Notification) { if !running { filter() } }
    func control(_ control: NSControl, textView: NSTextView, doCommandBy commandSelector: Selector) -> Bool {
        if commandSelector == #selector(NSResponder.moveDown(_:)) || commandSelector == #selector(NSResponder.moveUp(_:)) {
            guard !commands.isEmpty else { return true }
            let delta = commandSelector == #selector(NSResponder.moveDown(_:)) ? 1 : -1
            let row = max(0, min(commands.count - 1, table.selectedRow + delta))
            table.selectRowIndexes([row], byExtendingSelection: false); table.scrollRowToVisible(row)
            return true
        }
        if commandSelector == #selector(NSResponder.insertNewline(_:)) { runSelected(); return true }
        if commandSelector == #selector(NSResponder.cancelOperation(_:)) { hide(); return true }
        return false
    }
    func windowWillClose(_ notification: Notification) { ToolUI.didClose() }
}

final class FileDropMaterial: NSVisualEffectView {
    var onDrop: (([URL]) -> Void)?
    override init(frame: NSRect) { super.init(frame: frame); registerForDraggedTypes([.fileURL]) }
    required init?(coder: NSCoder) { fatalError() }
    override func draggingEntered(_ sender: NSDraggingInfo) -> NSDragOperation { ToolUI.fileURLs(from: sender.draggingPasteboard).isEmpty ? [] : .copy }
    override func performDragOperation(_ sender: NSDraggingInfo) -> Bool {
        let urls = ToolUI.fileURLs(from: sender.draggingPasteboard)
        guard !urls.isEmpty else { return false }
        onDrop?(urls); return true
    }
}
