import AppKit
import UniformTypeIdentifiers

final class FileActionsPane: Pane {
    private let feature: Feature
    private var chooseButton: NSButton!
    private var libraryButton: NSButton?
    init(_ feature: Feature) { self.feature = feature; super.init(size: NSSize(width: 760, height: 370)) }
    required init?(coder: NSCoder) { fatalError() }
    override func build() {
        let description: String
        switch feature {
        case .batchRename: description = "Combine a base name, text replacement, prefix, suffix and numbering. Review the full preview and conflicts before changing names. Undo remains available while the rename window is open."
        case .pdfPages: description = "Select page ranges, drag thumbnails to change order, rotate or remove pages, then export selected pages or save all. The original PDFs are kept."
        default: description = "Choose images to read QR codes and barcodes locally. Copy the decoded content, or review a web link before opening it. No camera or screen capture is used."
        }
        stack.addArrangedSubview(label(feature.title, size: 17, weight: .semibold))
        stack.addArrangedSubview(label(description, secondary: true))
        chooseButton = ToolUI.button("Choose Files…", target: self, action: #selector(choose))
        stack.addArrangedSubview(chooseButton)
        if feature == .qrCodes {
            let button = ToolUI.button("QR Library…", target: self, action: #selector(openLibrary))
            libraryButton = button; stack.addArrangedSubview(button)
        }
        stack.addArrangedSubview(label(feature == .batchRename ? "Also available in the Wheel’s Actions menu and Quick Actions." : "Also available in the Wheel’s Tools menu and Quick Actions.", size: 12, secondary: true))
        stack.addArrangedSubview(label("Turn this feature on above to get started.", size: 11, secondary: true))
    }
    override func refresh() { chooseButton?.isEnabled = feature.isOn; libraryButton?.isEnabled = feature.isOn }
    @objc private func choose() { Self.chooseAndOpen(feature) }
    @objc private func openLibrary() { QRLibraryWindow().show() }
    @MainActor static func chooseAndOpen(_ feature: Feature) {
        guard feature.isOn else { return }
        let picker = NSOpenPanel(); picker.allowsMultipleSelection = true; picker.canChooseDirectories = feature == .batchRename
        picker.prompt = L("Choose")
        if feature == .pdfPages { picker.allowedContentTypes = [.pdf] }
        if feature == .qrCodes { picker.allowedContentTypes = [.image] }
        NSApp.activate()
        guard picker.runModal() == .OK, feature.isOn else { return }
        switch feature {
        case .batchRename: BatchRenameWindow(picker.urls).show()
        case .pdfPages: PDFPagesWindow(picker.urls).show()
        case .qrCodes: QRCodesWindow(picker.urls).show()
        default: break
        }
    }
}

final class AppActionsPane: Pane, NSTableViewDataSource, NSTableViewDelegate {
    let table = NSTableView()
    private var targets: [FileAppTarget] = []
    private var editButton: NSButton!, removeButton: NSButton!
    private var editor: AppTargetEditor?
    init() { super.init(size: NSSize(width: 790, height: 610)) }
    required init?(coder: NSCoder) { fatalError() }
    override func build() {
        stack.addArrangedSubview(label("Your files, your apps", size: 17, weight: .semibold))
        stack.addArrangedSubview(label("Choose which apps appear in the Apps wheel. Add an app, choose a delivery method and optionally limit its file extensions.", secondary: true))
        for (id, title, width) in [("enabled", "On", 45.0), ("app", "Application", 210.0), ("delivery", "Delivery", 215.0), ("types", "File extensions", 160.0)] {
            let column = NSTableColumn(identifier: .init(id)); column.title = L(title); column.width = width; table.addTableColumn(column)
        }
        table.style = .inset; table.rowHeight = 44; table.dataSource = self; table.delegate = self
        table.target = self; table.doubleAction = #selector(editTarget)
        let scroll = NSScrollView(); scroll.documentView = table; scroll.hasVerticalScroller = true; scroll.hasHorizontalScroller = true
        scroll.widthAnchor.constraint(equalToConstant: 720).isActive = true; scroll.heightAnchor.constraint(equalToConstant: 240).isActive = true
        stack.addArrangedSubview(scroll)
        let add = ToolUI.button("Add Application…", target: self, action: #selector(addTarget))
        editButton = ToolUI.button("Edit…", target: self, action: #selector(editTarget))
        removeButton = ToolUI.button("Remove", target: self, action: #selector(removeTarget))
        stack.addArrangedSubview(ToolUI.stack([add, editButton!, removeButton!], vertical: false))
        stack.addArrangedSubview(label("Codex and Claude Code open a new draft containing local file references. They do not upload attachments or send a message automatically. Review and send in the target app.", size: 12, secondary: true))
        stack.addArrangedSubview(label("Open files uses macOS to hand files to the app. Copy files and open app prepares attachments for you to paste. This only changes Swivel’s actions; your system’s default apps stay the same.", size: 12, secondary: true))
    }
    override func refresh() {
        let latest = Prefs.fileAppTargets
        if targets != latest { targets = latest; table.reloadData() }
        editButton?.isEnabled = targets.indices.contains(table.selectedRow)
        removeButton?.isEnabled = targets.indices.contains(table.selectedRow)
    }
    func tableViewSelectionDidChange(_ notification: Notification) { refresh() }
    @objc private func addTarget() { showEditor(nil) }
    @objc private func editTarget() { if targets.indices.contains(table.selectedRow) { showEditor(targets[table.selectedRow]) } }
    private func showEditor(_ target: FileAppTarget?) {
        guard let window = view.window else { return }
        editor = AppTargetEditor(target) { [weak self] value in
            guard let self else { return }
            var latest = Prefs.fileAppTargets
            if let index = latest.firstIndex(where: { $0.id == value.id }) { latest[index] = value } else { latest.append(value) }
            Prefs.fileAppTargets = latest
            if let index = latest.firstIndex(where: { $0.id == value.id }) { table.selectRowIndexes([index], byExtendingSelection: false) }
        }
        editor?.present(on: window)
    }
    @objc private func removeTarget() {
        guard targets.indices.contains(table.selectedRow) else { return }
        let id = targets[table.selectedRow].id
        Prefs.fileAppTargets = targets.filter { $0.id != id }
    }
    @objc private func toggleTarget(_ sender: NSButton) {
        guard targets.indices.contains(sender.tag) else { return }
        targets[sender.tag].enabled = sender.state == .on; Prefs.fileAppTargets = targets
    }
    func numberOfRows(in tableView: NSTableView) -> Int { targets.count }
    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        let target = targets[row]
        if tableColumn?.identifier.rawValue == "enabled" {
            let toggle = NSButton(checkboxWithTitle: "", target: self, action: #selector(toggleTarget(_:)))
            toggle.state = target.enabled ? .on : .off; toggle.tag = row; toggle.setAccessibilityLabel(target.name); return toggle
        }
        if tableColumn?.identifier.rawValue == "app" {
            let name = NSTextField(labelWithString: target.name); name.lineBreakMode = .byTruncatingTail
            let installed = FileAppRouting.application(for: target) != nil
            return ToolUI.stack([name, ToolUI.note(installed ? L("Installed") : L("Choose the app to finish setup"))], spacing: 3)
        }
        let value = tableColumn?.identifier.rawValue == "delivery" ? target.delivery.title : target.extensions.isEmpty ? L("All files") : target.extensions.joined(separator: ", ")
        let label = NSTextField(labelWithString: value); label.lineBreakMode = .byTruncatingTail; label.toolTip = value; return label
    }
}

@MainActor
final class AppTargetEditor: NSObject {
    private let panel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 610, height: 410), styleMask: [.titled, .closable], backing: .buffered, defer: false)
    private var value: FileAppTarget
    private let saved: (FileAppTarget) -> Void
    private let name = NSTextField(), extensions = NSTextField(), appLabel = ToolUI.note("")
    private let delivery = NSPopUpButton()
    private let installedApps = NSPopUpButton()
    private var applicationURLs: [URL] = []
    private let issue = ToolUI.note("")
    init(_ target: FileAppTarget?, saved: @escaping (FileAppTarget) -> Void) {
        value = target ?? FileAppTarget(id: UUID().uuidString, name: "", bundleID: "", appPath: "", delivery: .openFiles)
        self.saved = saved; super.init()
        panel.title = L(target == nil ? "Add Application" : "Edit Application")
        name.stringValue = value.name; name.placeholderString = L("Name in the Wheel"); name.setAccessibilityLabel(L("Name in the Wheel"))
        extensions.stringValue = value.extensions.joined(separator: ", "); extensions.placeholderString = L("Empty means all files, e.g. png, jpg, pdf")
        extensions.setAccessibilityLabel(L("File extensions"))
        delivery.addItems(withTitles: FileAppTarget.Delivery.allCases.map(\.title))
        delivery.selectItem(at: FileAppTarget.Delivery.allCases.firstIndex(of: value.delivery) ?? 0)
        delivery.target = self; delivery.action = #selector(deliveryChanged); delivery.setAccessibilityLabel(L("Delivery"))
        let roots = ["/Applications", "/System/Applications", "/System/Applications/Utilities", "/Applications/Utilities", NSHomeDirectory() + "/Applications"]
        applicationURLs = Array(Set(roots.flatMap { (try? FileManager.default.contentsOfDirectory(at: URL(fileURLWithPath: $0), includingPropertiesForKeys: nil)) ?? [] }))
            .filter { $0.pathExtension == "app" && Bundle(url: $0)?.bundleIdentifier != nil && Bundle(url: $0)?.bundleIdentifier != "local.swivel.app" }
            .sorted { $0.lastPathComponent.localizedStandardCompare($1.lastPathComponent) == .orderedAscending }
        installedApps.addItem(withTitle: L("Choose an installed app…"))
        installedApps.addItems(withTitles: applicationURLs.map { $0.deletingPathExtension().lastPathComponent })
        if let index = applicationURLs.firstIndex(where: { $0.path == value.appPath }) { installedApps.selectItem(at: index + 1) }
        installedApps.target = self; installedApps.action = #selector(installedAppChanged); installedApps.setAccessibilityLabel(L("Application"))
        let choose = ToolUI.button("Choose Application…", target: self, action: #selector(chooseApp))
        let grid = NSGridView(views: [[NSTextField(labelWithString: L("Application")), installedApps],
                                    [NSTextField(labelWithString: L("Name")), name],
                                    [NSTextField(labelWithString: L("Delivery")), delivery],
                                    [NSTextField(labelWithString: L("File extensions")), extensions]])
        grid.rowSpacing = 12; grid.columnSpacing = 12; grid.column(at: 0).xPlacement = .trailing
        name.widthAnchor.constraint(greaterThanOrEqualToConstant: 370).isActive = true
        let cancel = ToolUI.button("Cancel", target: self, action: #selector(cancel)); cancel.keyEquivalent = "\u{1b}"
        let save = ToolUI.button("Save", target: self, action: #selector(save)); save.keyEquivalent = "\r"
        let bar = NSStackView(); bar.addView(cancel, in: .trailing); bar.addView(save, in: .trailing)
        let root = ToolUI.stack([grid, ToolUI.stack([choose, appLabel], vertical: false), issue, bar])
        panel.contentView = NSView(); ToolUI.embed(root, in: panel.contentView!)
        bar.widthAnchor.constraint(equalTo: root.widthAnchor).isActive = true
        issue.preferredMaxLayoutWidth = 550; updateAppLabel()
    }
    func present(on window: NSWindow) { window.beginSheet(panel) }
    @objc private func deliveryChanged() {
        value.delivery = FileAppTarget.Delivery.allCases[delivery.indexOfSelectedItem]; updateAppLabel()
    }
    private func updateAppLabel() {
        appLabel.stringValue = FileAppRouting.application(for: value)?.lastPathComponent ?? L("No application selected")
        appLabel.lineBreakMode = .byTruncatingMiddle
    }
    @objc private func installedAppChanged() {
        let index = installedApps.indexOfSelectedItem - 1
        guard applicationURLs.indices.contains(index) else { return }
        selectApplication(applicationURLs[index])
    }
    private func selectApplication(_ url: URL) {
        guard let id = Bundle(url: url)?.bundleIdentifier, id != "local.swivel.app" else { return }
        value.appPath = url.path; value.bundleID = id
        if name.stringValue.isEmpty { name.stringValue = url.deletingPathExtension().lastPathComponent }
        updateAppLabel()
    }
    @objc private func chooseApp() {
        let picker = NSOpenPanel(); picker.allowsMultipleSelection = false
        // App bundles are directories. Let the panel select packages on all supported macOS versions,
        // then validate the selected bundle below rather than relying on the panel's UTI filter.
        picker.canChooseFiles = true; picker.canChooseDirectories = true; picker.treatsFilePackagesAsDirectories = false
        picker.directoryURL = URL(fileURLWithPath: "/Applications", isDirectory: true)
        // Use an application-modal picker here: this editor is already a sheet.
        NSApp.activate()
        guard picker.runModal() == .OK, let url = picker.url else { return }
        guard url.pathExtension == "app", let bundle = Bundle(url: url), let id = bundle.bundleIdentifier, id != "local.swivel.app" else {
            issue.stringValue = L("Choose an installed application first."); return
        }
        selectApplication(url)
    }
    @objc private func cancel() { panel.sheetParent?.endSheet(panel) }
    @objc private func save() {
        value.name = name.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        value.extensions = FileAppTarget.normalizedExtensions(extensions.stringValue)
        guard value.isValid else { issue.stringValue = L("Enter a name of 1–80 characters."); return }
        if !extensions.stringValue.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && value.extensions.isEmpty {
            issue.stringValue = L("Enter file extensions separated by commas."); return
        }
        guard FileAppRouting.application(for: value) != nil else { issue.stringValue = L("Choose an installed application first."); return }
        saved(value); panel.sheetParent?.endSheet(panel)
    }
}
