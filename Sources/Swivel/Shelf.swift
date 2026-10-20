import AppKit

/// Session-only references: staging and removing items never copies or deletes files.
struct ShelfContents {
    private(set) var urls: [URL] = []
    mutating func add(_ candidates: [URL]) {
        for candidate in candidates where candidate.isFileURL {
            let url = candidate.standardizedFileURL
            if FileManager.default.fileExists(atPath: url.path), !urls.contains(url) { urls.append(url) }
        }
    }
    mutating func remove(_ indexes: IndexSet) { urls = urls.enumerated().filter { !indexes.contains($0.offset) }.map(\.element) }
    mutating func clear() { urls.removeAll() }
}

@MainActor
final class Shelf: NSObject, NSWindowDelegate, NSTableViewDataSource, NSTableViewDelegate, NSMenuDelegate {
    static let shared = Shelf()
    private var panel: NSPanel?
    private let table = ToolTable()
    private var contents = ShelfContents()
    private let status = ToolUI.note("")
    private let empty = ShelfPlaceholder(wrappingLabelWithString: L("Drop files here, then select several and drag them out together."))
    private var removeButton: NSButton!
    private var clearButton: NSButton!
    private var previewButton: NSButton!
    private var observer: NSObjectProtocol?

    func add(_ urls: [URL]) {
        guard Feature.shelf.isOn else { return }
        contents.add(urls)
        show()
    }
    func show() {
        guard Feature.shelf.isOn else { return }
        if panel == nil { panel = makePanel() }
        refresh()
        if let panel { ToolUI.show(panel) }
    }
    func hide() { panel?.orderOut(nil); ToolUI.didClose() }

    private func makePanel() -> NSPanel {
        let panel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 480, height: 470),
                            styleMask: [.titled, .closable, .resizable, .utilityWindow], backing: .buffered, defer: false)
        panel.title = L("File Shelf")
        panel.level = .floating
        panel.isFloatingPanel = true
        panel.isReleasedWhenClosed = false
        panel.hidesOnDeactivate = false
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.minSize = NSSize(width: 420, height: 330)
        panel.delegate = self
        panel.setFrameAutosaveName("SwivelFileShelf")
        panel.center()
        table.addTableColumn(NSTableColumn(identifier: .init("file")))
        table.headerView = nil
        table.style = .inset
        table.rowHeight = 52
        table.allowsMultipleSelection = true
        table.dataSource = self; table.delegate = self
        table.registerForDraggedTypes([.fileURL])
        table.setDraggingSourceOperationMask(.copy, forLocal: false)
        table.setDraggingSourceOperationMask(.copy, forLocal: true)
        table.deleteSelection = { [weak self] in self?.removeSelected() }
        table.previewSelection = { [weak self] in self?.quickLook() }
        let menu = NSMenu()
        menu.autoenablesItems = false
        menu.delegate = self
        for (title, action) in [("Quick Look", #selector(quickLook)), ("Show in Finder", #selector(reveal)), ("Remove from Shelf", #selector(removeSelected))] {
            let item = NSMenuItem(title: L(title), action: action, keyEquivalent: "")
            item.target = self; menu.addItem(item)
        }
        table.menu = menu
        let scroll = NSScrollView()
        scroll.documentView = table
        scroll.hasVerticalScroller = true
        scroll.autohidesScrollers = true
        scroll.borderType = .bezelBorder
        let area = NSView()
        ToolUI.embed(scroll, in: area, inset: 0)
        empty.alignment = .center
        empty.font = .systemFont(ofSize: 12)
        empty.textColor = .secondaryLabelColor
        empty.translatesAutoresizingMaskIntoConstraints = false
        area.addSubview(empty)
        NSLayoutConstraint.activate([
            empty.centerXAnchor.constraint(equalTo: area.centerXAnchor),
            empty.centerYAnchor.constraint(equalTo: area.centerYAnchor),
            empty.widthAnchor.constraint(lessThanOrEqualTo: area.widthAnchor, constant: -48),
        ])
        let add = ToolUI.button("Add Files…", target: self, action: #selector(chooseFiles))
        previewButton = ToolUI.button("Quick Look", target: self, action: #selector(quickLook))
        removeButton = ToolUI.button("Remove from Shelf", target: self, action: #selector(removeSelected))
        clearButton = ToolUI.button("Clear", target: self, action: #selector(clearAll))
        let top = NSStackView()
        top.addView(status, in: .leading); top.addView(add, in: .trailing)
        let buttons = NSStackView()
        buttons.addView(previewButton, in: .leading)
        buttons.addView(removeButton, in: .trailing); buttons.addView(clearButton, in: .trailing)
        let note = ToolUI.note("Removing an item only clears its reference. Original files stay where they are. This shelf lasts until you quit.")
        let root = ToolUI.stack([top, area, buttons, note])
        let content = NSView(); panel.contentView = content
        ToolUI.embed(root, in: content, inset: 16)
        for view in [top, area, buttons, note] { view.widthAnchor.constraint(equalTo: root.widthAnchor).isActive = true }
        area.heightAnchor.constraint(greaterThanOrEqualToConstant: 160).isActive = true
        area.setContentHuggingPriority(.defaultLow, for: .vertical)
        observer = NotificationCenter.default.addObserver(forName: NSApplication.didBecomeActiveNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.refresh() }
        }
        return panel
    }
    private var selectedURLs: [URL] { table.selectedRowIndexes.compactMap { contents.urls.indices.contains($0) ? contents.urls[$0] : nil } }
    private var availableSelection: [URL] { selectedURLs.filter { FileManager.default.fileExists(atPath: $0.path) } }
    private func refresh() {
        guard panel != nil else { return }
        panel?.title = L("File Shelf")
        let selected = table.selectedRowIndexes
        table.reloadData()
        table.selectRowIndexes(IndexSet(selected.filter { $0 < contents.urls.count }), byExtendingSelection: false)
        empty.isHidden = !contents.urls.isEmpty
        updateSelection()
    }
    private func updateSelection() {
        status.stringValue = L("%d files · %d selected", contents.urls.count, table.numberOfSelectedRows)
        removeButton?.isEnabled = !selectedURLs.isEmpty
        clearButton?.isEnabled = !contents.urls.isEmpty
        previewButton?.isEnabled = !availableSelection.isEmpty
    }
    @objc private func chooseFiles() { add(ToolUI.chooseFiles(folders: true)) }
    @objc private func removeSelected() { contents.remove(table.selectedRowIndexes); table.deselectAll(nil); refresh() }
    @objc private func clearAll() { contents.clear(); refresh() }
    @objc private func quickLook() { if let url = availableSelection.first { QuickLooker.shared.show(url) } }
    @objc private func reveal() { NSWorkspace.shared.activateFileViewerSelecting(availableSelection) }
    func windowWillClose(_ notification: Notification) { ToolUI.didClose() }
    func tableViewSelectionDidChange(_ notification: Notification) { updateSelection() }
    func numberOfRows(in tableView: NSTableView) -> Int { contents.urls.count }
    func menuNeedsUpdate(_ menu: NSMenu) {
        if table.clickedRow >= 0, !table.selectedRowIndexes.contains(table.clickedRow) {
            table.selectRowIndexes([table.clickedRow], byExtendingSelection: false)
        }
        for item in menu.items { item.isEnabled = item.action == #selector(removeSelected) ? !selectedURLs.isEmpty : !availableSelection.isEmpty }
    }
    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        let url = contents.urls[row]
        let available = FileManager.default.fileExists(atPath: url.path)
        let icon = NSImageView(image: NSWorkspace.shared.icon(forFile: url.path))
        icon.widthAnchor.constraint(equalToConstant: 30).isActive = true
        icon.heightAnchor.constraint(equalToConstant: 30).isActive = true
        let title = NSTextField(labelWithString: url.lastPathComponent)
        title.lineBreakMode = .byTruncatingMiddle
        let path = NSTextField(labelWithString: available ? url.deletingLastPathComponent().path : L("File is no longer available"))
        path.lineBreakMode = .byTruncatingMiddle
        path.font = .systemFont(ofSize: 11)
        path.textColor = available ? .secondaryLabelColor : .systemRed
        let labels = ToolUI.stack([title, path], spacing: 3)
        labels.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        let cell = ToolUI.stack([icon, labels], vertical: false, spacing: 10)
        cell.toolTip = url.path
        cell.setAccessibilityLabel(url.lastPathComponent)
        return cell
    }
    func tableView(_ tableView: NSTableView, pasteboardWriterForRow row: Int) -> NSPasteboardWriting? {
        let url = contents.urls[row]
        return Feature.shelf.isOn && FileManager.default.fileExists(atPath: url.path) ? url as NSURL : nil
    }
    func tableView(_ tableView: NSTableView, validateDrop info: NSDraggingInfo, proposedRow row: Int,
                   proposedDropOperation dropOperation: NSTableView.DropOperation) -> NSDragOperation {
        guard Feature.shelf.isOn, info.draggingSource as? NSTableView !== table,
              !ToolUI.fileURLs(from: info.draggingPasteboard).isEmpty else { return [] }
        tableView.setDropRow(-1, dropOperation: .on)
        return .copy
    }
    func tableView(_ tableView: NSTableView, acceptDrop info: NSDraggingInfo, row: Int,
                   dropOperation: NSTableView.DropOperation) -> Bool {
        let urls = ToolUI.fileURLs(from: info.draggingPasteboard)
        guard Feature.shelf.isOn, !urls.isEmpty else { return false }
        contents.add(urls)
        refresh()
        return true
    }
}

/// Let drops reach the table even when the pointer is over the empty-state text.
private final class ShelfPlaceholder: NSTextField {
    override func hitTest(_ point: NSPoint) -> NSView? { nil }
}
