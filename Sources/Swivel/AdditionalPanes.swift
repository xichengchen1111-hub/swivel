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
    private let progress = NSProgressIndicator()
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
        ToolUI.reserveWidth(applyButton, for: [(L("Apply to Images…"), nil), (L("Processing…"), nil), (L("Processing %d of %d…", 88, 88), nil)])
        progress.style = .spinning
        progress.controlSize = .small
        progress.isHidden = true
        let buttons = NSStackView()
        buttons.addView(add, in: .leading)
        buttons.addView(editButton, in: .leading)
        buttons.addView(removeButton, in: .leading)
        buttons.addView(progress, in: .trailing)
        buttons.addView(applyButton, in: .trailing)
        buttons.widthAnchor.constraint(equalToConstant: 680).isActive = true
        stack.addArrangedSubview(buttons)
        stack.addArrangedSubview(label("Results are saved beside each original with a unique name. Original files are kept. Presets remain saved when this feature is off.", size: 12, secondary: true))
    }
    private var selected: ImagePreset? { presets.indices.contains(table.selectedRow) ? presets[table.selectedRow] : nil }
    /// Reloads only when the saved presets differ from the rows, so this pane's own animated edits aren't redrawn
    /// in one frame by the Prefs notification they cause.
    override func refresh() {
        let latest = Prefs.imagePresets
        if latest != presets {
            let selectedID = selected?.id
            presets = latest
            table.reloadData()
            if !presets.isEmpty { table.selectRowIndexes([presets.firstIndex { $0.id == selectedID } ?? 0], byExtendingSelection: false) }
        }
        updateButtons()
    }
    private func updateButtons() {
        editButton?.isEnabled = selected != nil && !working
        removeButton?.isEnabled = selected != nil && !working
        applyButton?.isEnabled = selected != nil && Feature.imagePresets.isOn && !working
        if !working { applyButton?.title = L("Apply to Images…") }
    }
    func tableViewSelectionDidChange(_ notification: Notification) { updateButtons() }
    @objc private func addPreset() { showEditor(nil) }
    @objc private func editPreset() { if let selected, !working { showEditor(selected) } }
    private func showEditor(_ preset: ImagePreset?) {
        guard let window = view.window else { return }
        let editor = PresetEditor(preset: preset) { [weak self] value in self?.saved(value) }
        editor.present(on: window)
    }
    private func saved(_ value: ImagePreset) {
        if let index = presets.firstIndex(where: { $0.id == value.id }) {
            presets[index] = value
            table.reloadData(forRowIndexes: [index], columnIndexes: [0])
            table.selectRowIndexes([index], byExtendingSelection: false)
            Prefs.imagePresets = presets
        } else {
            insert(value, at: presets.count)
        }
    }
    @objc private func removePreset() {
        guard let selected, let index = presets.firstIndex(where: { $0.id == selected.id }) else { return }
        remove(at: index)
    }

    /// Fades the row out and selects its neighbour. ⌘Z (Edit ▸ Undo) puts it back where it was.
    private func remove(at index: Int) {
        guard presets.indices.contains(index) else { return }
        let preset = presets.remove(at: index)
        NSAnimationContext.runAnimationGroup { ctx in
            ctx.duration = 0.15
            table.removeRows(at: [index], withAnimation: .effectFade)
        }
        if !presets.isEmpty { table.selectRowIndexes([min(index, presets.count - 1)], byExtendingSelection: false) }
        Prefs.imagePresets = presets
        view.window?.undoManager?.registerUndo(withTarget: self) { pane in
            MainActor.assumeIsolated { pane.insert(preset, at: index) }
        }
        updateButtons()
    }

    /// Slides a new (or restored) preset in and selects it; undoing removes it again.
    private func insert(_ preset: ImagePreset, at index: Int) {
        guard !presets.contains(where: { $0.id == preset.id }) else { return }
        let index = min(index, presets.count)
        presets.insert(preset, at: index)
        let reduced = Motion.reduced
        NSAnimationContext.runAnimationGroup { ctx in
            ctx.duration = 0.18
            table.insertRows(at: [index], withAnimation: reduced ? .effectFade : [.slideDown, .effectFade])
        }
        table.selectRowIndexes([index], byExtendingSelection: false)
        table.scrollRowToVisible(index)
        Prefs.imagePresets = presets
        let id = preset.id
        view.window?.undoManager?.registerUndo(withTarget: self) { pane in
            MainActor.assumeIsolated { if let row = pane.presets.firstIndex(where: { $0.id == id }) { pane.remove(at: row) } }
        }
        updateButtons()
    }

    @objc private func applyPreset() {
        guard let preset = selected, Feature.imagePresets.isOn, !working else { return }
        let urls = ToolUI.chooseFiles(imagesOnly: true)
        guard !urls.isEmpty else { return }
        working = true; updateButtons()
        showProgress(done: 0, of: urls.count)
        Task { @MainActor in
            defer {
                working = false
                progress.stopAnimation(nil)
                progress.isHidden = true
                updateButtons()
            }
            do {
                let result = try await PresetActions.run(preset, urls: urls) { done, total in self.showProgress(done: done, of: total) }
                if !result.files.isEmpty { Feedback.done() }
                Toast.shared.show(result.message, symbol: "checkmark.circle.fill", files: result.files)
            } catch {
                Feedback.refuse()
                // A sheet on Settings can't open behind another app the way a free-standing alert can.
                let alert = NSAlert(error: error)
                if let window = view.window { alert.beginSheetModal(for: window, completionHandler: nil) } else { NSApp.activate(); alert.runModal() }
            }
        }
    }

    /// "Processing 3 of 12…" with a filling pie beside it; a single image just spins.
    private func showProgress(done: Int, of total: Int) {
        progress.isHidden = false
        if total > 1 {
            progress.stopAnimation(nil)
            progress.isIndeterminate = false
            progress.doubleValue = Double(done) / Double(total) * 100
            applyButton.title = L("Processing %d of %d…", min(done + 1, total), total)
        } else {
            progress.isIndeterminate = true
            progress.startAnimation(nil)
            applyButton.title = L("Processing…")
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
