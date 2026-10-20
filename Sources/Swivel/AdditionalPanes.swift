import AppKit

final class TextCapturePane: Pane {
    private var choose: NSButton!
    init() { super.init(size: NSSize(width: 760, height: 330)) }
    required init?(coder: NSCoder) { fatalError() }
    override func build() {
        stack.addArrangedSubview(label("From picture to text", size: 17, weight: .semibold))
        stack.addArrangedSubview(label("Choose an image, review the recognized text, then copy it. English, Chinese and Japanese are detected automatically.", secondary: true))
        choose = ToolUI.button("Choose an Image…", target: self, action: #selector(openImage))
        stack.addArrangedSubview(choose)
        stack.addArrangedSubview(label("You can also drop one image on Extract Text in the Wheel, or right-click a photo on the Clothesline.", secondary: true))
        stack.addArrangedSubview(label("Turn this feature on above to get started.", size: 11, secondary: true))
    }
    override func refresh() { choose?.isEnabled = Feature.textCapture.isOn }
    @objc private func openImage() {
        if let url = ToolUI.chooseFiles(imagesOnly: true, multiple: false).first { TextCaptureWindow.present(url) }
    }
}

final class ShelfPane: Pane {
    private var open: NSButton!
    init() { super.init(size: NSSize(width: 760, height: 340)) }
    required init?(coder: NSCoder) { fatalError() }
    override func build() {
        stack.addArrangedSubview(label("A place to gather your files", size: 17, weight: .semibold))
        stack.addArrangedSubview(label("Drop files or folders onto the shelf. Use Command-click or Shift-click to select several, then drag them out together. Press Space for Quick Look.", secondary: true))
        open = ToolUI.button("Show Shelf", target: self, action: #selector(showShelf))
        stack.addArrangedSubview(open)
        stack.addArrangedSubview(label("Removing an item only clears its reference. Original files stay where they are. This shelf lasts until you quit.", secondary: true))
        stack.addArrangedSubview(label("Turn this feature on above to get started.", size: 11, secondary: true))
    }
    override func refresh() { open?.isEnabled = Feature.shelf.isOn }
    @objc private func showShelf() { Shelf.shared.show() }
}

final class ImagePresetsPane: Pane, NSTableViewDataSource, NSTableViewDelegate {
    private let table = NSTableView()
    private var presets: [ImagePreset] = []
    private var editButton: NSButton!
    private var removeButton: NSButton!
    private var applyButton: NSButton!
    private var working = false
    init() { super.init(size: NSSize(width: 760, height: 530)) }
    required init?(coder: NSCoder) { fatalError() }
    override func build() {
        stack.addArrangedSubview(label("Your everyday image recipes", size: 17, weight: .semibold))
        stack.addArrangedSubview(label("Save a size, format and metadata preference. Apply a preset to several images, or find it in the Wheel’s Tools menu.", secondary: true))
        table.addTableColumn(NSTableColumn(identifier: .init("preset")))
        table.headerView = nil; table.style = .inset; table.rowHeight = 54
        table.dataSource = self; table.delegate = self
        table.target = self; table.doubleAction = #selector(editPreset)
        let scroll = NSScrollView()
        scroll.documentView = table; scroll.hasVerticalScroller = true
        scroll.borderType = .bezelBorder
        scroll.widthAnchor.constraint(equalToConstant: 680).isActive = true
        scroll.heightAnchor.constraint(equalToConstant: 230).isActive = true
        stack.addArrangedSubview(scroll)
        let add = ToolUI.button("New Preset…", target: self, action: #selector(addPreset))
        editButton = ToolUI.button("Edit…", target: self, action: #selector(editPreset))
        removeButton = ToolUI.button("Remove", target: self, action: #selector(removePreset))
        applyButton = ToolUI.button("Apply to Images…", target: self, action: #selector(applyPreset))
        let buttons = NSStackView()
        buttons.addView(add, in: .leading)
        buttons.addView(editButton, in: .leading)
        buttons.addView(removeButton, in: .leading)
        buttons.addView(applyButton, in: .trailing)
        buttons.widthAnchor.constraint(equalToConstant: 680).isActive = true
        stack.addArrangedSubview(buttons)
        stack.addArrangedSubview(label("Results are saved beside each original with a unique name. Original files are kept. Presets remain saved when this feature is off.", size: 12, secondary: true))
    }
    private var selected: ImagePreset? { presets.indices.contains(table.selectedRow) ? presets[table.selectedRow] : nil }
    override func refresh() {
        let selectedID = selected?.id
        presets = Prefs.imagePresets
        table.reloadData()
        if !presets.isEmpty { table.selectRowIndexes([presets.firstIndex { $0.id == selectedID } ?? 0], byExtendingSelection: false) }
        updateButtons()
    }
    private func updateButtons() {
        editButton?.isEnabled = selected != nil && !working
        removeButton?.isEnabled = selected != nil && !working
        applyButton?.isEnabled = selected != nil && Feature.imagePresets.isOn && !working
        applyButton?.title = L(working ? "Processing…" : "Apply to Images…")
    }
    func tableViewSelectionDidChange(_ notification: Notification) { updateButtons() }
    @objc private func addPreset() { showEditor(nil) }
    @objc private func editPreset() { if let selected, !working { showEditor(selected) } }
    private func showEditor(_ preset: ImagePreset?) {
        guard let window = view.window else { return }
        let editor = PresetEditor(preset: preset) { [weak self] value in
            var saved = Prefs.imagePresets
            if let index = saved.firstIndex(where: { $0.id == value.id }) { saved[index] = value } else { saved.append(value) }
            Prefs.imagePresets = saved
            if let index = saved.firstIndex(where: { $0.id == value.id }) { self?.table.selectRowIndexes([index], byExtendingSelection: false) }
        }
        editor.present(on: window)
    }
    @objc private func removePreset() {
        guard let selected else { return }
        Prefs.imagePresets = Prefs.imagePresets.filter { $0.id != selected.id }
    }
    @objc private func applyPreset() {
        guard let preset = selected, Feature.imagePresets.isOn, !working else { return }
        let urls = ToolUI.chooseFiles(imagesOnly: true)
        guard !urls.isEmpty else { return }
        working = true; updateButtons()
        Task { @MainActor in
            defer { working = false; updateButtons() }
            do {
                let result = try await PresetActions.run(preset, urls: urls)
                Toast.shared.show(result.message, symbol: "checkmark.circle", files: result.files)
            } catch { NSAlert(error: error).runModal() }
        }
    }
    func numberOfRows(in tableView: NSTableView) -> Int { presets.count }
    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        let preset = presets[row]
        let name = NSTextField(labelWithString: preset.displayName)
        name.font = .systemFont(ofSize: 13, weight: .medium)
        name.lineBreakMode = .byTruncatingTail
        let summary = ToolUI.note(preset.summary)
        summary.lineBreakMode = .byTruncatingTail
        return ToolUI.stack([name, summary], spacing: 4)
    }
}

final class QuickActionsPane: Pane {
    private let shortcuts = NSPopUpButton(frame: .zero, pullsDown: false)
    private let availability = ToolUI.note("")
    private var open: NSButton!
    init() { super.init(size: NSSize(width: 760, height: 360)) }
    required init?(coder: NSCoder) { fatalError() }
    override func build() {
        stack.addArrangedSubview(label("A shortcut to everything", size: 17, weight: .semibold))
        stack.addArrangedSubview(label("Search enabled tools, choose files and press Return. The panel also accepts files dragged from Finder.", secondary: true))
        shortcuts.addItems(withTitles: QuickShortcut.allCases.map(\.title))
        shortcuts.target = self; shortcuts.action = #selector(shortcutChanged)
        stack.addArrangedSubview(row("Shortcut", shortcuts))
        availability.preferredMaxLayoutWidth = 650
        stack.addArrangedSubview(availability)
        open = ToolUI.button("Show Quick Actions", target: self, action: #selector(showPanel))
        stack.addArrangedSubview(open)
        stack.addArrangedSubview(label("If another app uses the same shortcut, choose a different one. You can always reopen Swivel from Finder or Spotlight to reach Settings.", size: 12, secondary: true))
        NotificationCenter.default.addObserver(forName: QuickActions.shortcutChanged, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.refresh() }
        }
    }
    override func refresh() {
        shortcuts.selectItem(at: QuickShortcut.allCases.firstIndex(of: Prefs.quickShortcut) ?? 0)
        open?.isEnabled = Feature.quickActions.isOn
        availability.stringValue = !Feature.quickActions.isOn ? L("Turn this feature on above to get started.") : QuickActions.shared.shortcutAvailable ? L("Shortcut is ready") : L("This shortcut is unavailable. Choose another shortcut or open the panel below.")
        availability.textColor = QuickActions.shared.shortcutAvailable ? .secondaryLabelColor : .systemOrange
    }
    @objc private func shortcutChanged() { Prefs.quickShortcut = QuickShortcut.allCases[shortcuts.indexOfSelectedItem] }
    @objc private func showPanel() { QuickActions.shared.show() }
}
