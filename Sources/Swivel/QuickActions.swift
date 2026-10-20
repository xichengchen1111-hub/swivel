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
    private var window: ToolPanel?
    private let search = NSSearchField()
    private let table = QuickTable()
    private let filesLabel = ToolUI.note("")
    private let hint = ToolUI.note("↑ ↓ to select · Return to run · Esc to close")
    private var chosenFiles: [URL] = []
    private var commands: [QuickCommand] = []
    private var allCommands: [QuickCommand] = []
    private var runButton: NSButton!
    private var chooseButton: NSButton!
    private var clearButton: NSButton!
    /// Changes on every show and hide, so a pending fade or confirm blink can't act on a newer panel.
    private var generation = 0
    /// The chosen row is blinking before the panel closes; a second Return must not run it twice.
    private var confirming = false
    private var placed = false
    private var runs = 0
    /// The run whose busy toast ("Title…") is up, so only that run takes it down.
    private var busyToastRun: Int?

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
        if window?.isVisible == true, !confirming { rebuild() }
    }

    /// Appears without activating Swivel (no Dock icon, the current app keeps its menu bar) and takes typing at once.
    func show(files: [URL]? = nil) {
        guard Feature.quickActions.isOn else { return }
        if window == nil { window = makePanel() }
        guard let window else { return }
        generation += 1
        confirming = false
        if let files { chosenFiles = files }
        search.stringValue = ""
        rebuild()
        if !window.isVisible {
            place(window)
            // Used many times a day from the keyboard: a fade of a few frames, no movement or scale (so nothing to
            // drop for Reduce Motion). The zero-length group replaces any fade still running from a quick hide and show.
            NSAnimationContext.runAnimationGroup { $0.duration = 0; window.animator().alphaValue = 0 }
            ToolUI.present(window, focus: true)
            NSAnimationContext.runAnimationGroup { ctx in
                ctx.duration = 0.08
                window.animator().alphaValue = 1
            }
        } else {
            ToolUI.present(window, focus: true)
        }
        window.makeFirstResponder(search)
        updateRowEmphasis()
    }

    /// Closes at once: exits are faster than entrances.
    func hide() {
        generation += 1
        confirming = false
        if let window, window.isVisible {
            window.orderOut(nil)
            window.alphaValue = 1
        }
        ToolUI.didClose()
    }

    /// The shortcut closes the panel when it has focus, and brings it back into focus when the user had clicked away.
    private func toggle() {
        guard let window, window.isVisible else { show(); return }
        if window.isKeyWindow {
            hide()
        } else {
            ToolUI.present(window, focus: true)
            window.makeFirstResponder(search)
        }
    }

    /// Centres the panel a little above the middle of the screen with the pointer, Spotlight-style. On that same
    /// screen it then stays wherever the user moves it.
    private func place(_ window: NSWindow) {
        let mouse = NSEvent.mouseLocation
        guard let screen = NSScreen.screens.first(where: { NSMouseInRect(mouse, $0.frame, false) }) ?? NSScreen.main else { return }
        let centre = NSPoint(x: window.frame.midX, y: window.frame.midY)
        guard !placed || !NSMouseInRect(centre, screen.frame, false) else { return }
        placed = true
        let area = screen.visibleFrame
        var frame = window.frame
        frame.size.width = min(frame.width, area.width - 40)
        frame.size.height = min(frame.height, area.height - 40)
        frame.origin.x = area.midX - frame.width / 2
        frame.origin.y = max(area.minY + 20, area.maxY - area.height * 0.2 - frame.height)
        window.setFrame(frame, display: false)
    }

    private func makePanel() -> ToolPanel {
        let panel = ToolPanel(contentRect: NSRect(x: 0, y: 0, width: 620, height: 500),
                              styleMask: [.titled, .closable, .resizable, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.title = L("Quick Actions")
        panel.toolbarStyle = .unified
        panel.isReleasedWhenClosed = false
        panel.minSize = NSSize(width: 540, height: 380)
        panel.delegate = self
        panel.level = .floating
        // NSPanel hides on deactivate by default, which used to strand Swivel in the Dock with no window.
        panel.hidesOnDeactivate = false
        panel.becomesKeyOnlyIfNeeded = false
        panel.animationBehavior = .none
        panel.collectionBehavior = [.moveToActiveSpace, .fullScreenAuxiliary]
        search.placeholderString = L("Search actions…")
        search.controlSize = .large
        search.sendsSearchStringImmediately = true
        search.delegate = self
        search.setAccessibilityLabel(L("Search actions…"))
        table.addTableColumn(NSTableColumn(identifier: .init("command")))
        table.headerView = nil; table.style = .inset; table.rowHeight = 48
        table.dataSource = self; table.delegate = self
        table.target = self; table.doubleAction = #selector(doubleClicked)
        // Moving the pointer over a row selects it, as in Spotlight; Return then runs what's under the pointer.
        table.onHover = { [weak self] row in
            guard let self, !confirming, row != table.selectedRow else { return }
            table.selectRowIndexes([row], byExtendingSelection: false)
        }
        let scroll = NSScrollView()
        scroll.documentView = table; scroll.hasVerticalScroller = true
        scroll.drawsBackground = false
        chooseButton = ToolUI.button("Choose Files…", target: self, action: #selector(chooseFiles))
        clearButton = ToolUI.button("Clear", target: self, action: #selector(clearFiles))
        filesLabel.lineBreakMode = .byTruncatingMiddle
        filesLabel.maximumNumberOfLines = 1
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
        material.onTargeted = { [weak self] targeted in self?.dropTargeted(targeted) }
        material.onDrop = { [weak self] urls in
            guard let self, Feature.quickActions.isOn else { return }
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
        filesLabel.stringValue = filesDescription
        filesLabel.toolTip = chosenFiles.map(\.path).joined(separator: "\n")
        clearButton?.isEnabled = !chosenFiles.isEmpty
        allCommands = Self.makeCommands(files: chosenFiles)
        filter()
    }
    private var filesDescription: String {
        chosenFiles.isEmpty ? L("Choose files or drop them here") : chosenFiles.count == 1 ? chosenFiles[0].lastPathComponent : L("%d files", chosenFiles.count)
    }
    private func filter() {
        commands = Self.matching(allCommands, query: search.stringValue)
        table.reloadData()
        if !commands.isEmpty { table.selectRowIndexes([0], byExtendingSelection: false) }
        runButton?.isEnabled = !commands.isEmpty
        hint.stringValue = commands.isEmpty ? L("No matching actions. Try another search.") : L("↑ ↓ to select · Return to run · Esc to close")
    }
    @objc private func chooseFiles() {
        let urls = ToolUI.chooseFiles(folders: true)
        if !urls.isEmpty { chosenFiles = urls; rebuild() }
    }
    @objc private func clearFiles() { chosenFiles = []; rebuild() }
    /// Only a double-click on a row runs it; one on the empty space below the rows does nothing.
    @objc private func doubleClicked() { if table.clickedRow >= 0 { runSelected() } }
    @objc private func runSelected() {
        guard Feature.quickActions.isOn, !confirming else { return }
        guard commands.indices.contains(table.selectedRow) else {
            ToolUI.refuse(search)
            return
        }
        let row = table.selectedRow
        let command = commands[row]
        // Re-resolve against enabled features, so a stale panel cannot run a disabled action.
        guard Self.makeCommands(files: chosenFiles).contains(where: { $0.id == command.id }) else { rebuild(); return }
        confirm(row)
        perform(command)
    }

    /// Blinks the chosen row off and on, the way a menu item confirms a choice, then closes. It stays under 150 ms
    /// because the keyboard started it; the command itself has already begun.
    private func confirm(_ row: Int) {
        confirming = true
        generation += 1
        let token = generation
        table.deselectAll(nil)
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) { [weak self] in
            guard let self, generation == token else { return }
            table.selectRowIndexes([row], byExtendingSelection: false)
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.12) { [weak self] in
            guard let self, generation == token else { return }
            hide()
        }
    }

    /// Runs a command after the panel has gone. Slow ones get a "Title…" toast so the wait isn't silent; commands that
    /// open their own window (editors) don't. Errors use a red toast, which can't end up behind another app.
    private func perform(_ command: QuickCommand) {
        runs += 1
        let run = runs
        let editorsBefore = Editor.presented
        let busy = Task { @MainActor [weak self] in
            try? await Task.sleep(for: .milliseconds(350))
            guard let self, !Task.isCancelled, Editor.presented == editorsBefore else { return }
            busyToastRun = run
            Toast.shared.show(command.title + "…", symbol: "arrow.triangle.2.circlepath", autoHide: false, spinning: true)
        }
        Task { @MainActor [weak self] in
            defer { if self?.busyToastRun == run { self?.busyToastRun = nil } }
            do {
                let result = try await command.run()
                busy.cancel()
                if let result, !(result.message.isEmpty && result.files.isEmpty) {
                    if !result.files.isEmpty { Feedback.done() }
                    Toast.shared.show(result.message, symbol: "checkmark.circle.fill", files: result.files)
                } else if self?.busyToastRun == run {
                    Toast.shared.hide()
                }
            } catch is Cancelled {
                busy.cancel()
                if self?.busyToastRun == run { Toast.shared.hide() }
            } catch let info as Info {
                busy.cancel()
                Toast.shared.show(info.message, symbol: "info.circle.fill")
            } catch {
                busy.cancel()
                Feedback.refuse()
                Toast.shared.show(error.localizedDescription, symbol: "exclamationmark.triangle.fill", tint: .systemRed)
            }
        }
    }

    /// Files hovering over the panel: say what letting go will do. The finger is down, so a detent tap lands too.
    private func dropTargeted(_ targeted: Bool) {
        if targeted { Haptics.tick(.alignment) }
        filesLabel.textColor = targeted ? .controlAccentColor : .secondaryLabelColor
        crossfade(filesLabel, to: targeted ? L("Drop to use these files") : filesDescription)
    }

    private func crossfade(_ label: NSTextField, to text: String) {
        guard label.stringValue != text else { return }
        label.wantsLayer = true
        let fade = CATransition()
        fade.type = .fade
        fade.duration = 0.15
        label.layer?.add(fade, forKey: "text")
        label.stringValue = text
    }

    /// The selection shows in the accent colour while the panel has focus, even though typing goes to the search
    /// field, so it's always clear what Return will run.
    private func updateRowEmphasis() {
        let active = window?.isKeyWindow == true
        table.enumerateAvailableRowViews { rowView, _ in (rowView as? QuickRowView)?.active = active }
    }

    func numberOfRows(in tableView: NSTableView) -> Int { commands.count }
    func tableView(_ tableView: NSTableView, rowViewForRow row: Int) -> NSTableRowView? {
        let rowView = tableView.makeView(withIdentifier: QuickRowView.identifier, owner: nil) as? QuickRowView ?? QuickRowView()
        rowView.active = window?.isKeyWindow == true
        return rowView
    }
    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        let cell = tableView.makeView(withIdentifier: QuickCell.identifier, owner: nil) as? QuickCell ?? QuickCell()
        cell.configure(commands[row])
        return cell
    }
    func controlTextDidChange(_ obj: Notification) { if !confirming { filter() } }
    func control(_ control: NSControl, textView: NSTextView, doCommandBy commandSelector: Selector) -> Bool {
        if commandSelector == #selector(NSResponder.moveDown(_:)) || commandSelector == #selector(NSResponder.moveUp(_:)) {
            guard !commands.isEmpty, !confirming else { return true }
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
    func windowDidBecomeKey(_ notification: Notification) { updateRowEmphasis() }
    func windowDidResignKey(_ notification: Notification) { updateRowEmphasis() }
}

/// The command list. Moving the pointer over a row reports it, so the selection can follow the mouse.
final class QuickTable: NSTableView {
    var onHover: ((Int) -> Void)?
    private var hoverArea: NSTrackingArea?

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let hoverArea { removeTrackingArea(hoverArea) }
        let area = NSTrackingArea(rect: .zero, options: [.mouseMoved, .activeAlways, .inVisibleRect], owner: self, userInfo: nil)
        addTrackingArea(area)
        hoverArea = area
    }

    override func mouseMoved(with event: NSEvent) {
        super.mouseMoved(with: event)
        let row = row(at: convert(event.locationInWindow, from: nil))
        if row >= 0 { onHover?(row) }
    }
}

/// Keeps the selected row emphasised (accent colour) while the panel is key, although the table never has focus.
private final class QuickRowView: NSTableRowView {
    static let identifier = NSUserInterfaceItemIdentifier("QuickRow")

    init() {
        super.init(frame: .zero)
        identifier = Self.identifier
    }

    required init?(coder: NSCoder) { fatalError() }

    var active = false {
        didSet {
            guard active != oldValue else { return }
            needsDisplay = true
            for case let cell as NSTableCellView in subviews { cell.backgroundStyle = interiorBackgroundStyle }
        }
    }

    override var isEmphasized: Bool {
        get { active }
        set { }
    }

    override var interiorBackgroundStyle: NSView.BackgroundStyle { isSelected && active ? .emphasized : .normal }
}

/// Symbol, title and where the command comes from; turns white on the accent selection like a menu item.
private final class QuickCell: NSTableCellView {
    static let identifier = NSUserInterfaceItemIdentifier("QuickCell")
    private let symbol = NSImageView()
    private let title = NSTextField(labelWithString: "")
    private let detail = NSTextField(labelWithString: "")

    init() {
        super.init(frame: .zero)
        identifier = Self.identifier
        symbol.widthAnchor.constraint(equalToConstant: 28).isActive = true
        title.lineBreakMode = .byTruncatingTail
        detail.font = .systemFont(ofSize: 12)
        detail.lineBreakMode = .byTruncatingTail
        let labels = ToolUI.stack([title, detail], spacing: 2)
        labels.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        let row = ToolUI.stack([symbol, labels], vertical: false)
        row.translatesAutoresizingMaskIntoConstraints = false
        addSubview(row)
        NSLayoutConstraint.activate([
            row.leadingAnchor.constraint(equalTo: leadingAnchor),
            row.trailingAnchor.constraint(lessThanOrEqualTo: trailingAnchor),
            row.centerYAnchor.constraint(equalTo: centerYAnchor),
        ])
        recolor()
    }

    required init?(coder: NSCoder) { fatalError() }

    func configure(_ command: QuickCommand) {
        symbol.image = NSImage(systemSymbolName: command.symbol, accessibilityDescription: nil)
        title.stringValue = command.title
        detail.stringValue = command.detail
    }

    override var backgroundStyle: NSView.BackgroundStyle {
        didSet { recolor() }
    }

    private func recolor() {
        let selected = backgroundStyle == .emphasized
        symbol.contentTintColor = selected ? .alternateSelectedControlTextColor : .controlAccentColor
        title.textColor = selected ? .alternateSelectedControlTextColor : .labelColor
        detail.textColor = selected ? .alternateSelectedControlTextColor.withAlphaComponent(0.8) : .secondaryLabelColor
    }
}

/// The panel's background, which also takes files dragged from Finder and outlines itself while they hover.
final class FileDropMaterial: NSVisualEffectView {
    var onDrop: (([URL]) -> Void)?
    var onTargeted: ((Bool) -> Void)?
    private let ring = DropRing()
    private var targeted = false

    override init(frame: NSRect) { super.init(frame: frame); registerForDraggedTypes([.fileURL]) }
    required init?(coder: NSCoder) { fatalError() }

    override func draggingEntered(_ sender: NSDraggingInfo) -> NSDragOperation {
        guard !ToolUI.fileURLs(from: sender.draggingPasteboard).isEmpty else { return [] }
        setTargeted(true)
        return .copy
    }
    override func draggingExited(_ sender: NSDraggingInfo?) { setTargeted(false) }
    override func draggingEnded(_ sender: NSDraggingInfo) { setTargeted(false) }
    override func performDragOperation(_ sender: NSDraggingInfo) -> Bool {
        let urls = ToolUI.fileURLs(from: sender.draggingPasteboard)
        setTargeted(false)
        guard !urls.isEmpty else { return false }
        onDrop?(urls); return true
    }

    private func setTargeted(_ value: Bool) {
        guard value != targeted else { return }
        targeted = value
        if ring.superview == nil {
            // Above the panel's content, added late so it stays on top of everything embedded before.
            ring.frame = bounds
            ring.autoresizingMask = [.width, .height]
            addSubview(ring, positioned: .above, relativeTo: nil)
        }
        ring.setShown(value)
        onTargeted?(value)
    }
}
