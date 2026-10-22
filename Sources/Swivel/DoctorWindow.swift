import AppKit
import UniformTypeIdentifiers

/// Mac Doctor's window: the summary and the areas it checks on the left, the selected one's results and fixes on the
/// right. Checks and Eject run in their own tasks so the window stays usable (and closable) while one is busy; only
/// moving items to the Trash and putting them back lock the window, for the moment that takes.
@MainActor
final class DoctorWindow: FileToolWindow, NSTableViewDataSource, NSTableViewDelegate {
    private static weak var current: DoctorWindow?

    static func present(app: URL? = nil) {
        guard Feature.doctor.isOn else { return }
        let window = current ?? DoctorWindow()
        if current == nil { current = window; window.show() } else {
            NSApp.activate()
            window.window.makeKeyAndOrderFront(nil)
        }
        if let app {
            window.select(.app)
            if !DoctorCenter.shared.checkApp(app) { window.status.stringValue = L("That isn’t an app.") }
        }
    }

    private let center = DoctorCenter.shared
    private let sections = NSTableView()
    private let rowsTable = NSTableView()
    private let detail = NSStackView()
    private let status = ToolUI.note("")
    private let spinner = NSProgressIndicator()
    private var checkAllButton: NSButton!
    private var askButton: NSButton!
    private var selected: DoctorSection = .summary
    private var rows: [DoctorRow] = []
    private var observers: [NSObjectProtocol] = []
    private var resultMessage: String?
    private var showsCommands = false
    /// What the detail column last showed; it is rebuilt only when this changes, so scrolling, selection and focus
    /// survive the updates other sections post while they run.
    private var shownState = ""
    private let rowsScroll = NSScrollView()
    private let language = Language.resolved
    private weak var sheet: DoctorAssistantSheet?
    private weak var rowDetail: NSTextField?
    private weak var trashButton: NSButton?
    private weak var revealButton: NSButton?
    private weak var ejectButton: NSButton?
    private var rowsHeight: NSLayoutConstraint!

    init() {
        super.init(feature: .doctor, title: "Mac Doctor", size: NSSize(width: 1020, height: 720))
        window.minSize = NSSize(width: 860, height: 600)

        let intro = ToolUI.note("Mac Doctor only reads. Nothing changes until you choose a fix and confirm it.")
        checkAllButton = ToolUI.button("Check This Mac", target: self, action: #selector(checkAll))
        checkAllButton.keyEquivalent = "\r"
        checkAllButton.toolTip = L("Checks storage, caches, speed and memory, drives, network, background items, battery and sleep. Reads only; uses no network.")
        askButton = ToolUI.button("Ask the Assistant…", target: self, action: #selector(ask))
        spinner.style = .spinning
        spinner.controlSize = .small
        spinner.isDisplayedWhenStopped = false
        let top = ToolUI.stack([intro, NSView(), spinner, askButton, checkAllButton], vertical: false, spacing: 10)
        intro.setContentHuggingPriority(.defaultLow, for: .horizontal)
        intro.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)

        let column = NSTableColumn(identifier: .init("section"))
        sections.addTableColumn(column)
        sections.headerView = nil
        sections.style = .sourceList
        sections.rowHeight = 44
        sections.dataSource = self
        sections.delegate = self
        sections.setAccessibilityLabel(L("Areas to check"))
        let sidebar = NSScrollView()
        sidebar.documentView = sections
        sidebar.hasVerticalScroller = true
        sidebar.drawsBackground = false
        sidebar.widthAnchor.constraint(equalToConstant: 250).isActive = true

        detail.orientation = .vertical
        detail.alignment = .leading
        detail.spacing = 10
        detail.edgeInsets = NSEdgeInsets(top: 4, left: 4, bottom: 12, right: 12)
        let document = FlippedView()
        document.translatesAutoresizingMaskIntoConstraints = false
        ToolUI.embed(detail, in: document, inset: 0)
        let detailScroll = NSScrollView()
        detailScroll.documentView = document
        detailScroll.hasVerticalScroller = true
        detailScroll.drawsBackground = false
        document.widthAnchor.constraint(equalTo: detailScroll.contentView.widthAnchor).isActive = true

        for (id, title, width) in [("item", "Item", 380.0), ("value", "Status", 200.0)] {
            let column = NSTableColumn(identifier: .init(id))
            column.title = L(title)
            column.width = width
            rowsTable.addTableColumn(column)
        }
        rowsTable.style = .inset
        rowsTable.rowHeight = 38
        rowsTable.columnAutoresizingStyle = .firstColumnOnlyAutoresizingStyle
        rowsTable.allowsMultipleSelection = true
        rowsTable.usesAlternatingRowBackgroundColors = true
        rowsTable.dataSource = self
        rowsTable.delegate = self
        rowsTable.setAccessibilityLabel(L("Results"))
        rowsScroll.documentView = rowsTable
        rowsScroll.hasVerticalScroller = true
        rowsScroll.borderType = .noBorder
        rowsHeight = rowsScroll.heightAnchor.constraint(equalToConstant: 120)
        rowsHeight.isActive = true

        let split = ToolUI.stack([sidebar, detailScroll], vertical: false, spacing: 16)
        split.alignment = .top
        // The sidebar keeps its width; the detail column takes the rest of the window.
        split.distribution = .fill
        sidebar.setContentHuggingPriority(.required, for: .horizontal)
        detailScroll.setContentHuggingPriority(.defaultLow, for: .horizontal)
        detailScroll.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        let root = ToolUI.stack([top, split, status], spacing: 12)
        install(root)
        for view in [top, split, status] { view.widthAnchor.constraint(equalTo: root.widthAnchor).isActive = true }
        sidebar.heightAnchor.constraint(equalTo: split.heightAnchor).isActive = true
        detailScroll.heightAnchor.constraint(equalTo: split.heightAnchor).isActive = true

        observers.append(NotificationCenter.default.addObserver(forName: DoctorCenter.changed, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.reload() }
        })
        observers.append(NotificationCenter.default.addObserver(forName: DoctorCenter.finishedAll, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.announceFinished() }
        })
        observers.append(NotificationCenter.default.addObserver(forName: Prefs.changed, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.reload() }
        })
        sections.reloadData()
        sections.selectRowIndexes([0], byExtendingSelection: false)
        reload()
    }

    deinit { for observer in observers { NotificationCenter.default.removeObserver(observer) } }

    func select(_ section: DoctorSection) {
        guard let index = DoctorSection.allCases.firstIndex(of: section) else { return }
        selected = section
        sections.selectRowIndexes([index], byExtendingSelection: false)
        reload()
    }

    override func windowWillClose(_ notification: Notification) {
        sheet?.close()
        center.cancelAll()
        super.windowWillClose(notification)
    }

    override func workFinished() {
        if let resultMessage { status.stringValue = resultMessage; self.resultMessage = nil }
        reload()
    }

    private var ejecting = false

    private func announceFinished() {
        let problems = center.findings().count
        let message = problems == 0 ? L("Check finished. Nothing needs attention.") : L("Check finished. %d things are worth a look.", problems)
        status.stringValue = message
        NSAccessibility.post(element: window, notification: .announcementRequested,
                             userInfo: [.announcement: message, .priority: NSAccessibilityPriorityLevel.high.rawValue])
    }

    // MARK: Building the detail

    private func reload() {
        guard Feature.doctor.isOn else { return }
        // Results are written in the language they were checked in; reopen to see the new language.
        if Language.resolved != language { if !isBusy { window.close() }; return }
        center.isBusy ? spinner.startAnimation(nil) : spinner.stopAnimation(nil)
        checkAllButton.isEnabled = !isBusy && !center.running.contains(where: \.runsWithAll)
        askButton.isEnabled = !isBusy && !center.results.isEmpty
        askButton.toolTip = center.results.isEmpty ? L("Run a check first; then the assistant can explain the results.") : nil
        // Sidebar cells are refreshed in place, keeping the selection and keyboard focus.
        sections.reloadData(forRowIndexes: IndexSet(integersIn: 0..<DoctorSection.allCases.count), columnIndexes: [0])
        let state = detailState()
        guard state != shownState else { return updateRowButtons() }
        shownState = state
        let keep = Set(rowsTable.selectedRowIndexes.compactMap { $0 < rows.count ? rows[$0].id : nil })
        rows = withOwners(center.results[selected]?.rows ?? [])
        rowsTable.reloadData()
        rowsTable.selectRowIndexes(IndexSet(rows.indices.filter { keep.contains(rows[$0].id) }), byExtendingSelection: false)
        rebuildDetail()
        updateRowButtons()
    }

    /// Everything the selected section's detail depends on.
    private func detailState() -> String {
        let result = center.results[selected]
        var parts = [selected.rawValue, "\(result?.checked.timeIntervalSince1970 ?? 0)", "\(result?.rows.count ?? -1)",
                     "\(center.running.contains(selected))", "\(center.testingSpeed)", "\(center.receipts.count)",
                     "\(showsCommands)", "\(center.folders.count)", center.app?.path ?? "", "\(isBusy)",
                     "\(result?.speedReport.count ?? 0)", "\(result?.notes.count ?? 0)"]
        if selected == .summary {
            parts += DoctorSection.allCases.map { "\(center.results[$0]?.checked.timeIntervalSince1970 ?? 0)\(center.running.contains($0))" }
        }
        return parts.joined(separator: "|")
    }

    /// Caches whose app is running say so up front, and can't be selected for the Trash.
    private func withOwners(_ rows: [DoctorRow]) -> [DoctorRow] {
        rows.map { row in
            guard row.trash != nil, !row.bundleIDs.isEmpty else { return row }
            let owners = center.runningOwners([row])
            guard !owners.isEmpty else { return row }
            var changed = row
            changed.value = L("Quit %@ to clean", owners.joined(separator: ", "))
            changed.trash = nil
            return changed
        }
    }

    private func rebuildDetail() {
        for view in detail.arrangedSubviews { detail.removeArrangedSubview(view); view.removeFromSuperview() }
        let section = selected
        let title = NSTextField(labelWithString: L(section.title))
        title.font = .systemFont(ofSize: 20, weight: .semibold)
        add(title)
        add(wrapping(L(section.explanation), secondary: true))
        if section == .summary { return buildSummary() }

        let result = center.results[section]
        let running = center.running.contains(section)
        if running {
            add(wrapping(section == .performance ? L("Measuring for a second…") : L("Checking…")))
        } else if let result {
            let headline = wrapping(result.headline)
            headline.font = .systemFont(ofSize: 14, weight: .medium)
            add(ToolUI.stack([severityIcon(result.severity), headline], vertical: false, spacing: 8))
        } else {
            add(wrapping(emptyText(section), secondary: true))
        }
        add(actionRow(section, result: result, running: running))

        if let result, !result.rows.isEmpty {
            rowsHeight.constant = max(min(CGFloat(result.rows.count) * 40 + 30, 340), 120)
            add(rowsScroll)
            let rowDetail = wrapping("", secondary: true)
            rowDetail.isSelectable = true
            self.rowDetail = rowDetail
            add(rowDetail)
            add(rowActionRow(section))
        }
        if (section == .caches || section == .background), !center.receipts.isEmpty { add(undoRow()) }

        if let result {
            for note in result.notes { add(wrapping(note, secondary: true)) }
            let settings = settingsRow(result.panes, section: section)
            if !settings.arrangedSubviews.isEmpty { add(settings) }
            if !result.adminCommands.isEmpty { add(adminArea(result.adminCommands)) }
            if !result.ran.isEmpty {
                let ran = wrapping(L("Ran: %@", result.ran.joined(separator: " · ")), secondary: true)
                ran.font = .monospacedSystemFont(ofSize: 10.5, weight: .regular)
                ran.isSelectable = true
                add(ran)
            }
            let checked = NSTextField(labelWithString: L("Checked %@", DoctorFormat.date(result.checked)))
            checked.font = .systemFont(ofSize: 11)
            checked.textColor = .tertiaryLabelColor
            add(checked)
        }
    }

    /// Before any check: what Mac Doctor does and one button. Afterwards: only what is worth a look.
    private func buildSummary() {
        if center.results.isEmpty {
            let icon = NSImageView(image: NSImage(systemSymbolName: "stethoscope", accessibilityDescription: nil) ?? NSImage())
            icon.symbolConfiguration = .init(pointSize: 44, weight: .light)
            icon.contentTintColor = .secondaryLabelColor
            add(icon)
            add(wrapping(L("Find out why your Mac is full, slow or won’t open an app. Choose Check This Mac to look at everything at once, or pick an area on the left."), secondary: false))
            add(ToolUI.button("Check This Mac", target: self, action: #selector(checkAll)))
            for line in ["Storage and caches: what fills the disk, and what can be cleaned safely.",
                         "Slow or hot: the apps and system tasks using the processor and memory.",
                         "Apps that won’t open: “damaged” and “unidentified developer” explained.",
                         "Drives, network, background items, battery and sleep."] {
                add(wrapping("• " + L(line), secondary: true))
            }
            return
        }
        let findings = center.findings()
        if center.isBusy { add(wrapping(L("Checking…"))) }
        if findings.isEmpty && !center.isBusy {
            add(ToolUI.stack([severityIcon(.ok), wrapping(L("Nothing needs attention right now."))], vertical: false, spacing: 8))
        }
        for (index, finding) in findings.enumerated() {
            let text = wrapping(L(finding.section.title) + " — " + finding.text)
            let open = ToolUI.button("Show", target: self, action: #selector(showFinding(_:)))
            open.tag = index
            open.setAccessibilityLabel(L("Show %@", L(finding.section.title)))
            let line = ToolUI.stack([severityIcon(finding.severity), text, open], vertical: false, spacing: 8)
            text.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
            add(line)
        }
        let checked = DoctorSection.allCases.filter { center.results[$0] != nil }.map { L($0.title) }
        add(wrapping(L("Checked: %@", checked.joined(separator: ", ")), secondary: true))
        add(ToolUI.stack([ToolUI.button("Check Again", target: self, action: #selector(checkAll))], vertical: false))
    }

    @objc private func showFinding(_ sender: NSButton) {
        let findings = center.findings()
        guard sender.tag < findings.count else { return }
        select(findings[sender.tag].section)
    }

    private func add(_ view: NSView) {
        detail.addArrangedSubview(view)
        view.widthAnchor.constraint(lessThanOrEqualTo: detail.widthAnchor, constant: -16).isActive = true
        if view is NSTextField || view is NSScrollView || view is NSBox {
            view.widthAnchor.constraint(equalTo: detail.widthAnchor, constant: -16).isActive = true
        }
    }

    private func wrapping(_ text: String, secondary: Bool = false) -> NSTextField {
        let field = NSTextField(wrappingLabelWithString: text)
        field.font = .systemFont(ofSize: 13)
        if secondary { field.textColor = .secondaryLabelColor }
        return field
    }

    private func severityIcon(_ severity: DoctorSeverity) -> NSImageView {
        let icon = NSImageView(image: NSImage(systemSymbolName: severity.symbol, accessibilityDescription: L(severity.word)) ?? NSImage())
        icon.contentTintColor = severity.tint
        icon.symbolConfiguration = .init(pointSize: 16, weight: .medium)
        icon.toolTip = L(severity.word)
        return icon
    }

    private func emptyText(_ section: DoctorSection) -> String {
        switch section {
        case .app: L("Choose an app that won’t open, or one you want to check before opening it.")
        case .folders: L("Choose a folder to see what takes up its space.")
        default: L("Not checked yet. Choose Check, or Check This Mac to check everything at once.")
        }
    }

    private func actionRow(_ section: DoctorSection, result: DoctorResult?, running: Bool) -> NSStackView {
        var buttons: [NSButton] = []
        switch section {
        case .app:
            buttons.append(ToolUI.button("Choose App…", target: self, action: #selector(chooseApp)))
            if center.app != nil { buttons.append(ToolUI.button("Check Again", target: self, action: #selector(checkSelected))) }
        case .folders:
            buttons.append(ToolUI.button("Choose Folder…", target: self, action: #selector(addFolder)))
            if !center.folders.isEmpty {
                buttons.append(ToolUI.button("Check Again", target: self, action: #selector(checkSelected)))
                buttons.append(ToolUI.button("Clear Folders", target: self, action: #selector(clearFolders)))
            }
        default:
            buttons.append(ToolUI.button(result == nil ? "Check" : "Check Again", target: self, action: #selector(checkSelected)))
        }
        if section == .network {
            let speed = center.testingSpeed ? ToolUI.button("Stop Speed Test", target: self, action: #selector(stopSpeed))
                                            : ToolUI.button("Test Speed…", target: self, action: #selector(testSpeed))
            buttons.append(speed)
        }
        if running { buttons.append(ToolUI.button("Stop", target: self, action: #selector(stopSelected))) }
        for button in buttons where button.action != #selector(stopSelected) && button.action != #selector(stopSpeed) {
            button.isEnabled = !isBusy && !running && Feature.doctor.isOn
        }
        let row = ToolUI.stack(buttons, vertical: false, spacing: 8)
        if section == .network && center.testingSpeed { row.addArrangedSubview(ToolUI.note("Testing… about 20 seconds.")) }
        if section == .folders { row.addArrangedSubview(ToolUI.note("macOS may ask once before Mac Doctor can look inside Desktop, Documents or Downloads.")) }
        return row
    }

    private func rowActionRow(_ section: DoctorSection) -> NSStackView {
        var buttons: [NSButton] = []
        if section == .caches || section == .background {
            let trash = ToolUI.button("Move to Trash…", target: self, action: #selector(moveToTrash))
            trashButton = trash
            buttons.append(trash)
        }
        if section == .drives {
            let eject = ToolUI.button("Eject", target: self, action: #selector(eject))
            ejectButton = eject
            buttons.append(eject)
        }
        let reveal = ToolUI.button("Show in Finder", target: self, action: #selector(reveal))
        revealButton = reveal
        buttons.append(reveal)
        if section == .caches { buttons.append(ToolUI.button("Select All", target: self, action: #selector(selectAllTrashable))) }
        return ToolUI.stack(buttons, vertical: false, spacing: 8)
    }

    private func undoRow() -> NSView {
        let last = center.receipts.last
        let text = ToolUI.note("")
        text.stringValue = L("Moved %d items to the Trash. Undo works until you quit Swivel or empty the Trash.", last?.movedItems ?? 0)
        let undo = ToolUI.button("Undo Last Move", target: self, action: #selector(undoMove))
        let trash = ToolUI.button("Open Trash", target: self, action: #selector(openTrash))
        return ToolUI.stack([text, undo, trash], vertical: false, spacing: 8)
    }

    private func settingsRow(_ panes: [DoctorSettingsPane], section: DoctorSection) -> NSStackView {
        var buttons = panes.map { pane -> NSButton in
            let button = ToolUI.button(pane.buttonTitle, target: self, action: #selector(openPane(_:)))
            button.identifier = .init(pane.rawValue)
            return button
        }
        if section == .performance || section == .power {
            buttons.append(ToolUI.button("Open Activity Monitor", target: self, action: #selector(openActivityMonitor)))
        }
        if section == .drives { buttons.append(ToolUI.button("Open Disk Utility", target: self, action: #selector(openDiskUtility))) }
        return ToolUI.stack(buttons, vertical: false, spacing: 8)
    }

    /// Commands that need an administrator: collapsed, explained, copyable, never run by Swivel.
    private func adminArea(_ commands: [DoctorAdminCommand]) -> NSView {
        let toggle = NSButton(title: L("Advanced: commands for Terminal"), target: self, action: #selector(toggleCommands))
        toggle.setButtonType(.pushOnPushOff)
        toggle.bezelStyle = .disclosure
        toggle.title = ""
        toggle.state = showsCommands ? .on : .off
        toggle.setAccessibilityLabel(L("Advanced: commands for Terminal"))
        let caption = NSTextField(labelWithString: L("Advanced: commands for Terminal"))
        caption.font = .systemFont(ofSize: 12, weight: .medium)
        caption.textColor = .secondaryLabelColor
        let header = ToolUI.stack([toggle, caption], vertical: false, spacing: 4)
        guard showsCommands else { return header }
        var views: [NSView] = [wrapping(L("Mac Doctor never runs these. Copy one into Terminal only if you understand what it does. Swivel never asks you to paste commands from websites, chats or assistant replies."), secondary: true)]
        for command in commands {
            guard let index = DoctorAdminCommand.all.firstIndex(of: command) else { continue }
            let field = NSTextField(labelWithString: command.command)
            field.font = .monospacedSystemFont(ofSize: 12, weight: .regular)
            field.isSelectable = true
            field.lineBreakMode = .byTruncatingTail
            let copy = ToolUI.button("Copy", target: self, action: #selector(copyCommand(_:)))
            copy.tag = index
            copy.setAccessibilityLabel(L("Copy command"))
            views.append(ToolUI.stack([field, copy], vertical: false, spacing: 8))
            views.append(wrapping(L(command.explanation), secondary: true))
        }
        let box = NSBox()
        box.boxType = .custom
        box.borderColor = .separatorColor
        box.fillColor = .underPageBackgroundColor
        box.cornerRadius = 8
        box.contentViewMargins = NSSize(width: 12, height: 10)
        let stack = ToolUI.stack(views, spacing: 6)
        box.contentView = NSView()
        ToolUI.embed(stack, in: box.contentView!, inset: 0)
        return ToolUI.stack([header, box], spacing: 6)
    }

    @objc private func toggleCommands() { showsCommands.toggle(); reload() }

    private func updateRowButtons() {
        let chosen = selectedRows
        let movable = chosen.filter { $0.trash != nil }
        trashButton?.isEnabled = !isBusy && !movable.isEmpty && Feature.doctor.isOn
        if let trashButton {
            let bytes = movable.reduce(Int64(0)) { $0 + ($1.bytes ?? 0) }
            trashButton.title = movable.isEmpty ? L("Move to Trash…")
                : bytes > 0 ? L("Move to Trash (%d, %@)…", movable.count, DoctorFormat.bytes(bytes))
                : L("Move to Trash (%d)…", movable.count)
        }
        revealButton?.isEnabled = chosen.contains { $0.location != nil }
        ejectButton?.isEnabled = !isBusy && !ejecting && chosen.count == 1 && chosen[0].id.hasPrefix("drive.")
        rowDetail?.stringValue = chosen.count == 1 ? chosen[0].detail : (chosen.isEmpty ? "" : L("%d selected", chosen.count))
    }

    private var selectedRows: [DoctorRow] {
        rowsTable.selectedRowIndexes.compactMap { $0 < rows.count ? rows[$0] : nil }
    }

    // MARK: Actions

    @objc private func checkAll() {
        status.stringValue = ""
        center.runAll()
    }

    @objc private func checkSelected() {
        status.stringValue = ""
        center.run(selected)
    }

    @objc private func stopSelected() { center.cancel(selected) }
    @objc private func stopSpeed() { center.stopSpeedTest() }

    @objc private func testSpeed() {
        let alert = NSAlert()
        alert.messageText = L("Test the connection speed?")
        alert.informativeText = L("Apple’s network quality test downloads and uploads test data from Apple’s servers for about 20 seconds, which can use several hundred megabytes. On a phone hotspot or a metered connection, skip it.")
        alert.addButton(withTitle: L("Test Speed"))
        alert.addButton(withTitle: L("Cancel"))
        alert.beginSheetModal(for: window) { [weak self] response in
            MainActor.assumeIsolated {
                guard response == .alertFirstButtonReturn, Feature.doctor.isOn else { return }
                self?.center.testSpeed()
            }
        }
    }

    @objc private func chooseApp() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.applicationBundle]
        panel.directoryURL = URL(fileURLWithPath: "/Applications")
        panel.treatsFilePackagesAsDirectories = false
        panel.prompt = L("Check")
        panel.beginSheetModal(for: window) { [weak self] response in
            MainActor.assumeIsolated {
                guard let self, response == .OK, let url = panel.url, Feature.doctor.isOn else { return }
                if !self.center.checkApp(url) { self.status.stringValue = L("That isn’t an app.") }
            }
        }
    }

    @objc private func addFolder() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = true
        panel.prompt = L("Choose")
        panel.beginSheetModal(for: window) { [weak self] response in
            MainActor.assumeIsolated {
                guard let self, response == .OK, Feature.doctor.isOn else { return }
                for url in panel.urls { self.center.addFolder(url) }
            }
        }
    }

    @objc private func clearFolders() { center.removeFolders() }

    @objc private func selectAllTrashable() {
        rowsTable.selectRowIndexes(IndexSet(rows.indices.filter { rows[$0].trash != nil }), byExtendingSelection: false)
    }

    @objc private func moveToTrash() {
        let chosen = selectedRows.filter { $0.trash != nil }
        guard !chosen.isEmpty else { return }
        let total = chosen.reduce(Int64(0)) { $0 + ($1.bytes ?? 0) }
        let list = chosen.prefix(10).map { "• " + $0.title + ($0.bytes.map { " — " + DoctorFormat.bytes($0) } ?? "") }
        let alert = NSAlert()
        alert.messageText = chosen.count == 1 ? L("Move “%@” to the Trash?", chosen[0].title)
                                              : L("Move %d items to the Trash?", chosen.count)
        var info = list.joined(separator: "\n") + (chosen.count > 10 ? "\n…" : "")
        info += "\n\n" + (selected == .caches
            ? L("For each cache, what is inside goes to the Trash as one folder named after it; the cache folder itself stays, and the app rebuilds it as needed.")
            : L("The file goes to the Trash."))
        if total > 0 { info += "\n\n" + L("About %@ is freed only when you empty the Trash.", DoctorFormat.bytes(total)) }
        info += "\n" + L("Until then you can use Undo Last Move here, or Put Back in the Trash.")
        alert.informativeText = info
        alert.addButton(withTitle: L("Cancel"))
        let move = alert.addButton(withTitle: L("Move to Trash"))
        move.hasDestructiveAction = true
        alert.beginSheetModal(for: window) { [weak self] response in
            MainActor.assumeIsolated {
                guard let self, response == .alertSecondButtonReturn, Feature.doctor.isOn else { return }
                self.runWork {
                    let receipt = try await self.center.moveToTrash(chosen)
                    self.resultMessage = L("Moved %d items to the Trash.", receipt.movedItems)
                    if !receipt.failures.isEmpty {
                        throw ToolError(L("Some items were left in place:") + "\n" + receipt.failures.prefix(6).joined(separator: "\n"))
                    }
                }
            }
        }
    }

    @objc private func undoMove() {
        runWork {
            let outcome = await self.center.undoLastMove()
            guard outcome.left.isEmpty else {
                // Not everything came back: say so with the failure sound, not the success one.
                throw ToolError(L("Put back %d items. %d stay in the Trash or in the gathered folder because their place was taken again; open the Trash to put them back by hand.", outcome.restored, outcome.left.count))
            }
            self.resultMessage = L("Put back %d items.", outcome.restored)
        }
    }

    @objc private func openTrash() {
        NSWorkspace.shared.open(FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".Trash"))
    }

    @objc private func eject() {
        guard let row = selectedRows.first, row.id.hasPrefix("drive."), let volume = row.location else { return }
        // Like Finder: every volume on the same physical disk goes with it, so name them first.
        let others = rows.filter { $0.id.hasPrefix("drive.") && $0.id != row.id && $0.disk != nil && $0.disk == row.disk }
        let alert = NSAlert()
        alert.messageText = L("Eject “%@”?", row.title)
        alert.informativeText = others.isEmpty ? L("Files on it can’t be opened until you connect it again.")
            : L("These volumes on the same drive are ejected too: %@", others.map(\.title).joined(separator: ", "))
        alert.addButton(withTitle: L("Cancel"))
        alert.addButton(withTitle: L("Eject"))
        alert.beginSheetModal(for: window) { [weak self] response in
            MainActor.assumeIsolated {
                guard let self, response == .alertSecondButtonReturn, Feature.doctor.isOn else { return }
                // Not runWork: ejecting can take up to a minute, and the rest of the window stays usable meanwhile.
                self.ejecting = true
                self.status.stringValue = L("Ejecting…")
                self.updateRowButtons()
                Task { [weak self] in
                    guard let self else { return }
                    let problem = await self.center.eject(volume)
                    self.ejecting = false
                    if let problem {
                        Feedback.refuse()
                        self.status.stringValue = problem
                        let failure = NSAlert()
                        failure.messageText = L("The drive couldn’t be ejected.")
                        failure.informativeText = problem
                        failure.addButton(withTitle: L("OK"))
                        failure.beginSheetModal(for: self.window, completionHandler: nil)
                    } else {
                        Feedback.done()
                        self.status.stringValue = L("Ejected. You can unplug the drive.")
                    }
                    self.updateRowButtons()
                }
            }
        }
    }

    @objc private func reveal() {
        let urls = selectedRows.compactMap(\.location)
        guard !urls.isEmpty else { return }
        NSWorkspace.shared.activateFileViewerSelecting(urls)
    }

    @objc private func openPane(_ sender: NSButton) {
        guard let id = sender.identifier?.rawValue, let pane = DoctorSettingsPane(rawValue: id) else { return }
        pane.open()
    }

    @objc private func openActivityMonitor() { openApp("com.apple.ActivityMonitor") }
    @objc private func openDiskUtility() { openApp("com.apple.DiskUtility") }

    private func openApp(_ bundleID: String) {
        guard let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleID) else { return }
        NSWorkspace.shared.openApplication(at: url, configuration: .init())
    }

    @objc private func copyCommand(_ sender: NSButton) {
        guard sender.tag < DoctorAdminCommand.all.count else { return }
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(DoctorAdminCommand.all[sender.tag].command, forType: .string)
        Feedback.press()
        status.stringValue = L("Copied. Paste it into Terminal only if you want to run it.")
    }

    @objc private func ask() {
        guard !center.results.isEmpty else { status.stringValue = L("Run a check first; then the assistant can explain the results."); return }
        guard Assistant.isConfigured, DoctorSettings.allowAssistant else {
            let alert = NSAlert()
            alert.messageText = Assistant.isConfigured ? L("Let Mac Doctor use the assistant?") : L("Set up the assistant first")
            alert.informativeText = Assistant.isConfigured
                ? L("Turn on “Let Mac Doctor ask the assistant” on Mac Doctor’s settings page. You will always see the exact text before it is sent.")
                : L("Add a service and key in Quick Actions settings. Mac Doctor works fully without it.")
            alert.addButton(withTitle: L("Open Settings"))
            alert.addButton(withTitle: L("Cancel"))
            alert.beginSheetModal(for: window) { response in
                MainActor.assumeIsolated {
                    guard response == .alertFirstButtonReturn else { return }
                    AppWindow.shared.show(Assistant.isConfigured ? .doctor : .quickActions)
                }
            }
            return
        }
        let sheet = DoctorAssistantSheet(center: center)
        self.sheet = sheet
        sheet.begin(on: window)
    }

    // MARK: Tables

    func numberOfRows(in tableView: NSTableView) -> Int {
        tableView === sections ? DoctorSection.allCases.count : rows.count
    }

    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        if tableView === sections { return sectionCell(DoctorSection.allCases[row]) }
        guard row < rows.count else { return nil }
        let item = rows[row]
        if tableColumn?.identifier.rawValue == "value" {
            let value = NSTextField(labelWithString: item.value)
            value.lineBreakMode = .byTruncatingTail
            value.toolTip = item.value
            return value
        }
        let title = NSTextField(labelWithString: item.title)
        title.lineBreakMode = .byTruncatingMiddle
        title.toolTip = item.detail.isEmpty ? item.title : item.detail
        let first = item.detail.split(separator: "\n").first.map(String.init) ?? ""
        let sub = NSTextField(labelWithString: first)
        sub.font = .systemFont(ofSize: 11)
        sub.textColor = .secondaryLabelColor
        sub.lineBreakMode = .byTruncatingTail
        let text = ToolUI.stack([title, sub], spacing: 1)
        let icon = severityIcon(item.severity)
        icon.symbolConfiguration = .init(pointSize: 12, weight: .regular)
        let stack = ToolUI.stack([icon, text], vertical: false, spacing: 6)
        stack.edgeInsets = NSEdgeInsets(top: 0, left: CGFloat(item.level) * 18, bottom: 0, right: 0)
        let cell = DoctorCell(stack, primary: [title], secondary: [sub])
        cell.setAccessibilityElement(true)
        cell.setAccessibilityLabel(item.title + ", " + item.value + ", " + L(item.severity.word))
        return cell
    }

    private func sectionCell(_ section: DoctorSection) -> NSView {
        let result = center.results[section]
        let icon = NSImageView(image: NSImage(systemSymbolName: section.symbol, accessibilityDescription: nil) ?? NSImage())
        icon.symbolConfiguration = .init(pointSize: 15, weight: .regular)
        icon.widthAnchor.constraint(equalToConstant: 22).isActive = true
        let title = NSTextField(labelWithString: L(section.title))
        title.font = .systemFont(ofSize: 13, weight: .medium)
        title.lineBreakMode = .byTruncatingTail
        let badgeText: String
        if section == .summary {
            let count = center.findings().count
            badgeText = center.results.isEmpty ? L("Start here") : count == 0 ? L("All clear") : L("%d to review", count)
        } else if center.running.contains(section) {
            badgeText = L("Checking…")
        } else if let result {
            badgeText = result.badge.isEmpty ? L(result.severity.word) : result.badge
        } else {
            badgeText = [.app, .folders].contains(section) ? L("Needs you to choose") : L("Not checked")
        }
        let badge = NSTextField(labelWithString: badgeText)
        badge.font = .systemFont(ofSize: 11)
        badge.textColor = .secondaryLabelColor
        badge.lineBreakMode = .byTruncatingTail
        var views: [NSView] = [icon, ToolUI.stack([title, badge], spacing: 1)]
        if let result, !center.running.contains(section) {
            let state = severityIcon(result.severity)
            state.symbolConfiguration = .init(pointSize: 12, weight: .medium)
            views += [NSView(), state]
        }
        let cell = DoctorCell(ToolUI.stack(views, vertical: false, spacing: 8), primary: [title], secondary: [badge])
        cell.setAccessibilityElement(true)
        cell.setAccessibilityLabel(L(section.title) + ", " + badgeText + (result.map { ", " + L($0.severity.word) } ?? ""))
        return cell
    }

    func tableViewSelectionDidChange(_ notification: Notification) {
        guard let table = notification.object as? NSTableView else { return }
        if table === sections {
            let index = sections.selectedRow
            guard index >= 0, index < DoctorSection.allCases.count, DoctorSection.allCases[index] != selected else { return }
            selected = DoctorSection.allCases[index]
            rowsTable.deselectAll(nil)
            reload()
        } else {
            updateRowButtons()
        }
    }
}

/// A top-left origin, so the detail column scrolls from its first line.
private final class FlippedView: NSView {
    override var isFlipped: Bool { true }
}

/// A table cell whose labels follow the row's selection style, as NSTableCellView's own text field does: light text
/// on an emphasized (accent) selection, the usual colours otherwise.
private final class DoctorCell: NSTableCellView {
    private let primary: [NSTextField]
    private let secondary: [NSTextField]

    init(_ content: NSView, primary: [NSTextField], secondary: [NSTextField]) {
        self.primary = primary
        self.secondary = secondary
        super.init(frame: .zero)
        content.translatesAutoresizingMaskIntoConstraints = false
        addSubview(content)
        NSLayoutConstraint.activate([
            content.leadingAnchor.constraint(equalTo: leadingAnchor),
            content.trailingAnchor.constraint(lessThanOrEqualTo: trailingAnchor),
            content.centerYAnchor.constraint(equalTo: centerYAnchor),
        ])
        if let last = (content as? NSStackView)?.arrangedSubviews.last, !(last is NSTextField), !(last is NSStackView) {
            last.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -8).isActive = true
        }
    }

    required init?(coder: NSCoder) { fatalError() }

    override var backgroundStyle: NSView.BackgroundStyle {
        didSet {
            let emphasized = backgroundStyle == .emphasized
            for label in primary { label.textColor = emphasized ? .alternateSelectedControlTextColor : .labelColor }
            for label in secondary {
                label.textColor = emphasized ? NSColor.alternateSelectedControlTextColor.withAlphaComponent(0.8) : .secondaryLabelColor
            }
        }
    }
}

/// Shows exactly what would be sent to the assistant, and its answer. Nothing is sent before Send.
@MainActor
final class DoctorAssistantSheet: NSObject {
    private let center: DoctorCenter
    private var report: String
    private let panel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 640, height: 560), styleMask: [.titled], backing: .buffered, defer: false)
    private let text = NSTextView()
    private let heading = NSTextField(wrappingLabelWithString: "")
    private let note = ToolUI.note("")
    private let warning = NSTextField(wrappingLabelWithString: "")
    private let includeNames = NSButton(checkboxWithTitle: "", target: nil, action: nil)
    private let spinner = NSProgressIndicator()
    private var sendButton: NSButton!
    private var cancelButton: NSButton!
    private var task: Task<Void, Never>?
    private var keepAlive: DoctorAssistantSheet?
    private var observer: NSObjectProtocol?

    init(center: DoctorCenter) {
        self.center = center
        report = center.report(includeNames: false)
        super.init()
        heading.font = .systemFont(ofSize: 15, weight: .semibold)
        heading.stringValue = L("Ask the assistant to explain these results?")
        let settings = Prefs.assistant
        note.stringValue = L("Swivel sends the text below and fixed instructions to %@ (%@). It holds no file names, folder names, paths or file contents; addresses, user and computer names and serial numbers are replaced. Check it before sending.",
                             Assistant.host(settings), DoctorText.visible(settings.model))
        let count = DoctorReport.nameCount(DoctorSection.allCases.compactMap { center.results[$0] })
        includeNames.title = L("Also include %d names of caches, apps and background items (more helpful, less private)", count)
        includeNames.target = self
        includeNames.action = #selector(namesChanged)
        includeNames.isHidden = count == 0
        warning.textColor = .systemOrange
        warning.font = .systemFont(ofSize: 12, weight: .medium)
        warning.isHidden = true
        text.isEditable = false
        text.isSelectable = true
        text.isRichText = false
        text.isAutomaticLinkDetectionEnabled = false
        text.isAutomaticDataDetectionEnabled = false
        text.font = .monospacedSystemFont(ofSize: 11.5, weight: .regular)
        text.string = report
        text.textContainerInset = NSSize(width: 6, height: 6)
        text.isVerticallyResizable = true
        text.autoresizingMask = [.width]
        let scroll = NSScrollView()
        scroll.documentView = text
        scroll.hasVerticalScroller = true
        scroll.borderType = .bezelBorder
        scroll.heightAnchor.constraint(equalToConstant: 330).isActive = true
        spinner.style = .spinning
        spinner.controlSize = .small
        spinner.isDisplayedWhenStopped = false
        cancelButton = ToolUI.button("Cancel", target: self, action: #selector(close))
        cancelButton.keyEquivalent = "\u{1b}"
        sendButton = ToolUI.button("Send", target: self, action: #selector(send))
        let buttons = ToolUI.stack([spinner, NSView(), cancelButton, sendButton], vertical: false, spacing: 8)
        let root = ToolUI.stack([heading, note, includeNames, warning, scroll, buttons], spacing: 12)
        let content = NSView()
        panel.contentView = content
        ToolUI.embed(root, in: content)
        for view in [heading, note, warning, scroll, buttons] { view.widthAnchor.constraint(equalTo: root.widthAnchor).isActive = true }
    }

    func begin(on window: NSWindow) {
        keepAlive = self
        window.beginSheet(panel)
        // Turning Mac Doctor off ends the request and the sheet.
        observer = NotificationCenter.default.addObserver(forName: Prefs.changed, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { if !Feature.doctor.isOn { self?.close() } }
        }
    }

    @objc private func namesChanged() {
        report = center.report(includeNames: includeNames.state == .on)
        text.string = report
    }

    @objc private func send() {
        guard Feature.doctor.isOn, Assistant.isConfigured, DoctorSettings.allowAssistant else { return close() }
        sendButton.isEnabled = false
        includeNames.isEnabled = false
        spinner.startAnimation(nil)
        heading.stringValue = L("Asking the assistant…")
        let sent = report  // exactly what is on screen
        task = Task { [weak self] in
            do {
                let answer = try await Assistant.explain(sent)
                self?.show(answer.text + (answer.truncated ? "\n\n" + L("(The answer was cut short.)") : ""),
                           heading: L("The assistant’s explanation"), risky: DoctorReport.soundsRisky(answer.text))
            } catch is CancellationError {
                return
            } catch {
                self?.show(error.localizedDescription, heading: L("The assistant couldn’t answer"), risky: false)
            }
        }
    }

    private func show(_ answer: String, heading title: String, risky: Bool) {
        spinner.stopAnimation(nil)
        heading.stringValue = title
        let settings = Prefs.assistant
        note.stringValue = L("Written by %@ at %@. AI explanations can be wrong; Swivel never runs anything from them, and Mac Doctor does nothing until you choose a fix yourself.",
                             DoctorText.visible(settings.model), Assistant.host(settings))
        includeNames.isHidden = true
        warning.stringValue = L("This answer mentions commands or protection settings. Swivel never asks you to run commands from an assistant reply; don’t paste them into Terminal unless you fully understand them.")
        warning.isHidden = !risky
        text.font = .systemFont(ofSize: 13)
        // Line by line, so hidden characters are written out but the answer keeps its paragraphs.
        text.string = answer.components(separatedBy: "\n").map { DoctorText.visible($0, limit: 8000) }.joined(separator: "\n")
        sendButton.isHidden = true
        cancelButton.title = L("Done")
    }

    @objc func close() {
        task?.cancel()
        task = nil
        if let observer { NotificationCenter.default.removeObserver(observer) }
        observer = nil
        panel.sheetParent?.endSheet(panel)
        keepAlive = nil
    }
}
