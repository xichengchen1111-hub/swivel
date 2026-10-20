import AppKit
import ServiceManagement

/// The app's main window, laid out like PowerToys: features in a sidebar, each with its own switch and settings.
/// While it is open Swivel shows in the Dock with a normal menu bar.
@MainActor
final class AppWindow: NSObject, NSWindowDelegate {
    static let shared = AppWindow()

    enum Tab: Int { case general, shortcuts, style, customize, clothesline, overview }

    private var window: NSWindow?
    private var split: BundleSplit?

    func show(_ tab: Tab? = nil) {
        let window = self.window ?? makeWindow()
        self.window = window
        split?.select(tab ?? .overview)
        NSApp.setActivationPolicy(.regular)
        NSApp.mainMenu = Self.mainMenu()
        NSApp.activate()
        window.makeKeyAndOrderFront(nil)
    }

    private func makeWindow() -> NSWindow {
        let split = BundleSplit()
        self.split = split
        let window = NSWindow(contentViewController: split)
        window.styleMask = [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView]
        window.titlebarAppearsTransparent = true
        window.toolbarStyle = .unified
        window.title = "Swivel"
        window.setContentSize(NSSize(width: 1100, height: 780))
        window.minSize = NSSize(width: 900, height: 600)
        window.isReleasedWhenClosed = false
        window.delegate = self
        window.setFrameAutosaveName("SwivelBundle")
        window.center()
        return window
    }

    /// Recreates the window in the new language and returns to General.
    func reloadForLanguage() {
        let frame = window?.frame
        window?.delegate = nil
        window?.close()
        window = nil
        split = nil
        NotificationCenter.default.post(name: Prefs.changed, object: nil)
        show(.general)
        if let frame { window?.setFrame(frame, display: true) }
    }

    func windowWillClose(_ notification: Notification) {
        // Back to a menu-bar-only app once the window is gone.
        DispatchQueue.main.async {
            if !(NSApp.windows.contains { $0.isVisible && $0.styleMask.contains(.titled) && !($0 is NSPanel) }) {
                NSApp.setActivationPolicy(.accessory)
            }
        }
    }

    /// App, Edit and Window menus, so ⌘V, ⌘C, ⌘A and ⌘W work in the window.
    private static func mainMenu() -> NSMenu {
        let main = NSMenu()
        let app = NSMenu()
        app.addItem(withTitle: L("About Swivel"), action: #selector(NSApplication.orderFrontStandardAboutPanel(_:)), keyEquivalent: "")
        app.addItem(.separator())
        app.addItem(withTitle: L("Hide Swivel"), action: #selector(NSApplication.hide(_:)), keyEquivalent: "h")
        app.addItem(withTitle: L("Quit Swivel"), action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        let edit = NSMenu(title: "Edit")
        edit.addItem(withTitle: L("Undo"), action: Selector(("undo:")), keyEquivalent: "z")
        edit.addItem(withTitle: L("Redo"), action: Selector(("redo:")), keyEquivalent: "Z")
        edit.addItem(.separator())
        edit.addItem(withTitle: L("Cut"), action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        edit.addItem(withTitle: L("Copy"), action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        edit.addItem(withTitle: L("Paste"), action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        edit.addItem(withTitle: L("Select All"), action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        let windowMenu = NSMenu(title: "Window")
        windowMenu.addItem(withTitle: L("Close"), action: #selector(NSWindow.performClose(_:)), keyEquivalent: "w")
        windowMenu.addItem(withTitle: L("Minimize"), action: #selector(NSWindow.performMiniaturize(_:)), keyEquivalent: "m")
        for (title, menu) in [("Swivel", app), ("Edit", edit), ("Window", windowMenu)] {
            let item = NSMenuItem(title: L(title), action: nil, keyEquivalent: "")
            item.submenu = menu
            main.addItem(item)
        }
        NSApp.windowsMenu = windowMenu
        return main
    }
}

// MARK: - Shared bits

/// A pane laid out as a vertical form with a fixed preferred size.
class Pane: NSViewController {
    let stack = NSStackView()
    private let size: NSSize

    init(size: NSSize) {
        self.size = size
        super.init(nibName: nil, bundle: nil)
    }

    required init?(coder: NSCoder) { fatalError() }

    override func loadView() {
        let root = NSView(frame: NSRect(origin: .zero, size: size))
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 14
        stack.edgeInsets = NSEdgeInsets(top: 24, left: 32, bottom: 24, right: 32)
        stack.translatesAutoresizingMaskIntoConstraints = false
        root.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: root.leadingAnchor),
            stack.trailingAnchor.constraint(equalTo: root.trailingAnchor),
            stack.topAnchor.constraint(equalTo: root.topAnchor),
            stack.bottomAnchor.constraint(lessThanOrEqualTo: root.bottomAnchor),
        ])
        view = root
        preferredContentSize = size
        build()
        NotificationCenter.default.addObserver(forName: Prefs.changed, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.refresh() }
        }
        refresh()
    }

    func build() {}
    func refresh() {}

    func label(_ text: String, size: CGFloat = 13, weight: NSFont.Weight = .regular, secondary: Bool = false) -> NSTextField {
        let field = NSTextField(wrappingLabelWithString: L(text))
        field.font = .systemFont(ofSize: size, weight: weight)
        if secondary { field.textColor = .secondaryLabelColor }
        field.preferredMaxLayoutWidth = self.size.width - 64
        return field
    }

    func row(_ title: String, _ views: NSView...) -> NSStackView {
        let name = NSTextField(labelWithString: title.isEmpty ? "" : L(title) + (Language.resolved == .en ? ":" : "："))
        name.alignment = .right
        name.widthAnchor.constraint(equalToConstant: 150).isActive = true
        let row = NSStackView(views: [name] + views)
        row.spacing = 10
        return row
    }
}

/// A soft desktop-like backdrop so translucent menu previews look the way they do on screen.
final class WallpaperView: NSView {
    override func draw(_ dirtyRect: NSRect) {
        let path = NSBezierPath(roundedRect: bounds, xRadius: 10, yRadius: 10)
        NSGradient(colors: [NSColor(srgbRed: 0.47, green: 0.66, blue: 0.9, alpha: 1),
                            NSColor(srgbRed: 0.72, green: 0.78, blue: 0.95, alpha: 1),
                            NSColor(srgbRed: 0.98, green: 0.8, blue: 0.62, alpha: 1)])!.draw(in: path, angle: -60)
    }
}

/// A non-interactive, scaled-down menu for previews.
@MainActor
func menuPreview(style: MenuStyle, titles: [String], mode: WheelMode, hovered: Int? = nil) -> WheelView {
    let shown = Array(titles.prefix(8))
    let natural = WheelView.size(for: style, count: shown.count)
    let view = WheelView(frame: NSRect(origin: .zero, size: natural))
    view.unregisterDraggedTypes()
    view.style = style
    view.subtitle = "photo.jpg"
    view.configure(items: shown.map { WheelItem(title: $0) { _ in ActionResult(message: "") } }, mode: mode, page: 0,
                   pageCount: titles.count > 8 ? 2 : 1)
    view.hovered = hovered
    return view
}

/// Hosts a preview, scaling it to fit while keeping its proportions.
final class PreviewHost: NSView {
    var preview: WheelView? {
        didSet {
            oldValue?.removeFromSuperview()
            if let preview { addSubview(preview) }
            needsLayout = true
        }
    }
    private var natural = NSSize(width: 1, height: 1)

    func show(_ view: WheelView) {
        natural = view.bounds.size
        preview = view
    }

    override func layout() {
        super.layout()
        guard let preview else { return }
        let rect = fit(natural, in: bounds.insetBy(dx: 10, dy: 10))
        preview.frame = rect
        preview.bounds = NSRect(origin: .zero, size: natural)  // draws at natural size, scaled into frame
    }
}

// MARK: - General

final class GeneralPane: Pane {
    private let login = NSButton(checkboxWithTitle: L("Open Swivel at login"), target: nil, action: nil)

    init() { super.init(size: NSSize(width: 620, height: 420)) }
    required init?(coder: NSCoder) { fatalError() }

    override func build() {
        let icon = NSImageView(image: NSImage(systemSymbolName: "circle.circle", accessibilityDescription: nil)!)
        icon.symbolConfiguration = .init(pointSize: 40, weight: .light)
        icon.contentTintColor = .controlAccentColor
        let title = label("Swivel", size: 22, weight: .bold)
        let subtitle = label("A bundle of small Mac tools. Turn on the ones you want.", secondary: true)
        let titles = NSStackView(views: [title, subtitle])
        titles.orientation = .vertical
        titles.alignment = .leading
        titles.spacing = 2
        stack.addArrangedSubview(NSStackView(views: [icon, titles]))

        login.target = self
        login.action = #selector(toggleLogin)
        stack.addArrangedSubview(row("Startup", login))

        let languages = NSPopUpButton(frame: .zero, pullsDown: false)
        languages.addItems(withTitles: Language.allCases.map(\.title))
        languages.selectItem(at: Language.allCases.firstIndex(of: Language.chosen) ?? 0)
        languages.target = self
        languages.action = #selector(languageChanged(_:))
        stack.addArrangedSubview(row("Language", languages))

        let steps = label("""
            1.  Start dragging any file in Finder and keep holding the mouse button.
            2.  Tap a key once — the menu opens under the pointer.
            3.  Drop the file on an option. Results are saved next to the original.
            Tap the same key again for the next page. Drop on empty space to cancel.
            """, secondary: true)
        steps.preferredMaxLayoutWidth = 400
        stack.addArrangedSubview(row("Wheel", steps))

        let shelf = NSButton(title: L("Show Shelf"), target: self, action: #selector(showShelf))
        let rope = NSButton(title: L("Show Clothesline"), target: self, action: #selector(showClothesline))
        stack.addArrangedSubview(row("", shelf, rope))
    }

    override func refresh() {
        login.state = SMAppService.mainApp.status == .enabled ? .on : .off
    }


    @objc private func toggleLogin() {
        do {
            if login.state == .on { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
        } catch {
            NSAlert(error: error).runModal()
        }
        refresh()
    }

    @objc private func languageChanged(_ sender: NSPopUpButton) {
        UserDefaults.standard.set(Language.allCases[sender.indexOfSelectedItem].rawValue, forKey: "language")
        // Rebuild everything that shows text: the window, the ◎ menu and the app menus.
        DispatchQueue.main.async { AppWindow.shared.reloadForLanguage() }
    }

    @objc private func showShelf() { Shelf.shared.show() }
    @objc private func showClothesline() { Clothesline.shared.toggle() }
}

// MARK: - Clothesline

final class ClotheslinePane: Pane {
    private let takeover = NSButton(checkboxWithTitle: L("Hang new screenshots and screen recordings on the rope"), target: nil, action: nil)
    private let copied = NSButton(checkboxWithTitle: L("Also hang images you copy (e.g. ⌃ screenshots, Copy in Preview)"), target: nil, action: nil)

    init() { super.init(size: NSSize(width: 680, height: 560)) }
    required init?(coder: NSCoder) { fatalError() }

    override func build() {
        stack.addArrangedSubview(label("A rope hidden under the menu bar. Screenshots hang on it instead of piling up on the desktop; take one down when you need it.", secondary: true))
        for (box, action) in [(takeover, #selector(toggleTakeover)), (copied, #selector(toggleCopied))] {
            box.target = self
            box.action = action
        }
        stack.addArrangedSubview(row("Screenshots", takeover))
        let note = label("Turns off the floating thumbnail and saves screenshots to the rope's folder. Your own settings come back when this is off or Swivel quits.", size: 11, secondary: true)
        note.preferredMaxLayoutWidth = 440
        stack.addArrangedSubview(row("", note))
        stack.addArrangedSubview(row("Clipboard", copied))

        let gestures = NSGridView(views: [
            ["Click", "Copy the image"], ["Double-click", "Open in Preview (QuickTime for recordings)"],
            ["Hold or force click", "Quick Look"], ["Drag into an app", "Send a copy — the original stays on the rope"],
            ["Drag into a folder", "Move it there — it comes off the rope"], ["Drag to the Trash, or click ×", "Throw it away"],
            ["Rest on the menu bar", "Lower the rope"], ["Click the menu bar", "Put the rope away"], ["⌃ ⌥ T", "Show or hide the rope"],
        ].map { pair -> [NSView] in
            let key = NSTextField(labelWithString: L(pair[0]))
            key.font = .systemFont(ofSize: 12, weight: .semibold)
            let value = NSTextField(labelWithString: L(pair[1]))
            value.font = .systemFont(ofSize: 12)
            value.textColor = .secondaryLabelColor
            return [key, value]
        })
        gestures.rowSpacing = 6
        gestures.columnSpacing = 16
        stack.addArrangedSubview(row("Gestures", gestures))

        let show = NSButton(title: L("Show Rope"), target: self, action: #selector(showRope))
        let open = NSButton(title: L("Open Folder"), target: self, action: #selector(openFolder))
        stack.addArrangedSubview(row("", show, open))
    }

    override func refresh() {
        takeover.state = Prefs.clotheslineTakeover ? .on : .off
        copied.state = Prefs.clotheslineHangCopied ? .on : .off
    }

    @objc private func toggleCopied() { Prefs.clotheslineHangCopied = copied.state == .on }

    @objc private func toggleTakeover() {
        Prefs.clotheslineTakeover = takeover.state == .on
        if Prefs.clotheslineTakeover && Prefs.clotheslineEnabled { ScreenshotTakeover.enable(into: Clothesline.shared.folder) } else { ScreenshotTakeover.restore() }
    }

    @objc private func showRope() { Clothesline.shared.toggle() }
    @objc private func openFolder() { NSWorkspace.shared.open(Clothesline.shared.folder) }
}

// MARK: - Shortcuts

final class ShortcutsPane: Pane {
    private var popups: [WheelMode: NSPopUpButton] = [:]

    init() { super.init(size: NSSize(width: 620, height: 360)) }
    required init?(coder: NSCoder) { fatalError() }

    override func build() {
        stack.addArrangedSubview(label("Choose which key opens each menu. Start dragging first, then tap the key.", secondary: true))
        for (mode, title) in [(WheelMode.convert, "Convert formats"), (.tools, "File tools"), (.actions, "File actions")] {
            let popup = NSPopUpButton(frame: .zero, pullsDown: false)
            popup.addItems(withTitles: TriggerKey.allCases.map(\.title))
            popup.target = self
            popup.action = #selector(changed(_:))
            popup.tag = WheelMode.allCases.firstIndex(of: mode)!
            popups[mode] = popup
            stack.addArrangedSubview(row(title, popup))
        }
        stack.addArrangedSubview(row("", label("""
            Picking a key that another menu uses swaps the two.
            For fn / 🌐: System Settings → Keyboard →
            “Press 🌐 key to” → “Do Nothing”.
            """, size: 11, secondary: true)))
        stack.addArrangedSubview(row("", NSButton(title: L("Restore Defaults"), target: self, action: #selector(reset))))
    }

    override func refresh() {
        for (mode, popup) in popups {
            popup.selectItem(at: TriggerKey.allCases.firstIndex(of: Prefs.key(for: mode))!)
        }
    }

    @objc private func changed(_ sender: NSPopUpButton) {
        Prefs.setKey(TriggerKey.allCases[sender.indexOfSelectedItem], for: WheelMode.allCases[sender.tag])
    }

    @objc private func reset() { Prefs.resetKeys() }
}

// MARK: - Style

final class StylePane: Pane {
    private var cards: [(MenuStyle, NSBox)] = []

    init() { super.init(size: NSSize(width: 860, height: 640)) }
    required init?(coder: NSCoder) { fatalError() }

    override func build() {
        stack.addArrangedSubview(label("Choose how the menu looks when it pops up. Click a template to use it.", secondary: true))
        let grid = NSGridView()
        grid.rowSpacing = 14
        grid.columnSpacing = 14
        var row: [NSView] = []
        for style in MenuStyle.allCases {
            let card = makeCard(style)
            cards.append((style, card))
            row.append(card)
            if row.count == 3 { grid.addRow(with: row); row = [] }
        }
        if !row.isEmpty { grid.addRow(with: row) }
        stack.addArrangedSubview(grid)
    }

    private func makeCard(_ style: MenuStyle) -> NSBox {
        let box = NSBox()
        box.boxType = .custom
        box.cornerRadius = 12
        box.borderWidth = 1
        box.fillColor = .controlBackgroundColor
        box.contentViewMargins = NSSize(width: 10, height: 10)
        box.translatesAutoresizingMaskIntoConstraints = false
        box.widthAnchor.constraint(equalToConstant: 250).isActive = true
        box.heightAnchor.constraint(equalToConstant: 250).isActive = true

        let wallpaper = WallpaperView()
        let host = PreviewHost()
        host.show(menuPreview(style: style, titles: ["JPG", "PNG", "HEIC", "WebP", "TIFF", "GIF", "PDF"], mode: .convert, hovered: 1))
        host.translatesAutoresizingMaskIntoConstraints = false
        wallpaper.addSubview(host)
        wallpaper.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            host.leadingAnchor.constraint(equalTo: wallpaper.leadingAnchor), host.trailingAnchor.constraint(equalTo: wallpaper.trailingAnchor),
            host.topAnchor.constraint(equalTo: wallpaper.topAnchor), host.bottomAnchor.constraint(equalTo: wallpaper.bottomAnchor),
            wallpaper.heightAnchor.constraint(equalToConstant: 170),
        ])
        let title = NSTextField(labelWithString: style.title)
        title.font = .systemFont(ofSize: 13, weight: .semibold)
        let blurb = NSTextField(wrappingLabelWithString: style.blurb)
        blurb.font = .systemFont(ofSize: 11)
        blurb.textColor = .secondaryLabelColor
        let content = NSStackView(views: [wallpaper, title, blurb])
        content.orientation = .vertical
        content.alignment = .leading
        content.spacing = 6
        wallpaper.widthAnchor.constraint(equalTo: content.widthAnchor).isActive = true
        content.widthAnchor.constraint(equalToConstant: 228).isActive = true  // fixed, so short captions don't shrink the preview
        blurb.preferredMaxLayoutWidth = 228
        box.contentView = content

        let click = NSClickGestureRecognizer(target: self, action: #selector(picked(_:)))
        box.addGestureRecognizer(click)
        box.toolTip = L("Use the %@ template", style.title)
        return box
    }

    override func refresh() {
        for (style, card) in cards {
            let selected = style == Prefs.style
            card.borderColor = selected ? .controlAccentColor : .separatorColor
            card.borderWidth = selected ? 3 : 1
        }
    }

    @objc private func picked(_ sender: NSClickGestureRecognizer) {
        guard let match = cards.first(where: { $0.1 === sender.view }) else { return }
        Prefs.style = match.0
    }
}

// MARK: - Customize

final class CustomizePane: Pane, NSTableViewDataSource, NSTableViewDelegate {
    private let modePopup = NSPopUpButton(frame: .zero, pullsDown: false)
    private let kindPopup = NSPopUpButton(frame: .zero, pullsDown: false)
    private let table = NSTableView()
    private let host = PreviewHost()
    private var rows: [(title: String, shown: Bool)] = []
    private static let rowType = NSPasteboard.PasteboardType("local.swivel.row")
    private let kinds: [FileKind] = [.image, .video, .audio, .pdf, .document, .archive, .other]

    init() { super.init(size: NSSize(width: 860, height: 600)) }
    required init?(coder: NSCoder) { fatalError() }

    private var mode: WheelMode { WheelMode.allCases[modePopup.indexOfSelectedItem] }
    private var kind: FileKind { kinds[kindPopup.indexOfSelectedItem] }

    override func build() {
        stack.addArrangedSubview(label("Pick which options each menu shows and in what order. Untick to hide, drag to reorder.", secondary: true))
        modePopup.addItems(withTitles: ["Convert formats", "File tools", "File actions"].map { L($0) })
        kindPopup.addItems(withTitles: kinds.map(\.title))
        for (popup, i) in [(modePopup, 0), (kindPopup, 1)] {
            popup.target = self
            popup.action = #selector(selectionChanged)
            popup.tag = i
        }
        for (i, kind) in kinds.enumerated() {
            kindPopup.item(at: i)?.image = NSImage(systemSymbolName: kind.symbol, accessibilityDescription: nil)
        }
        let reset = NSButton(title: L("Restore Defaults"), target: self, action: #selector(resetContents))
        let pickers = NSStackView(views: [NSTextField(labelWithString: L("Menu:")), modePopup,
                                          NSTextField(labelWithString: L("File type:")), kindPopup, reset])
        pickers.spacing = 8
        stack.addArrangedSubview(pickers)

        let column = NSTableColumn(identifier: .init("item"))
        column.title = "Option"
        table.addTableColumn(column)
        table.headerView = nil
        table.rowHeight = 26
        table.style = .inset
        table.dataSource = self
        table.delegate = self
        table.registerForDraggedTypes([Self.rowType])
        table.setDraggingSourceOperationMask(.move, forLocal: true)
        let scroll = NSScrollView()
        scroll.documentView = table
        scroll.hasVerticalScroller = true
        scroll.borderType = .noBorder
        scroll.translatesAutoresizingMaskIntoConstraints = false
        scroll.widthAnchor.constraint(equalToConstant: 260).isActive = true
        scroll.heightAnchor.constraint(equalToConstant: 420).isActive = true

        let wallpaper = WallpaperView()
        wallpaper.translatesAutoresizingMaskIntoConstraints = false
        host.translatesAutoresizingMaskIntoConstraints = false
        wallpaper.addSubview(host)
        NSLayoutConstraint.activate([
            host.leadingAnchor.constraint(equalTo: wallpaper.leadingAnchor), host.trailingAnchor.constraint(equalTo: wallpaper.trailingAnchor),
            host.topAnchor.constraint(equalTo: wallpaper.topAnchor), host.bottomAnchor.constraint(equalTo: wallpaper.bottomAnchor),
            wallpaper.widthAnchor.constraint(equalToConstant: 500), wallpaper.heightAnchor.constraint(equalToConstant: 420),
        ])
        let split = NSStackView(views: [scroll, wallpaper])
        split.spacing = 20
        split.alignment = .top
        stack.addArrangedSubview(split)
        load()
    }

    override func refresh() {
        guard isViewLoaded, modePopup.numberOfItems > 0 else { return }
        load()
    }

    private func load() {
        let all = Catalog.allItems(mode: mode, kind: kind)
        let hidden = Prefs.hidden(mode, kind)
        let order = Prefs.order(mode, kind)
        let sorted = all.enumerated().sorted { a, b in
            (order.firstIndex(of: a.element) ?? order.count + a.offset) < (order.firstIndex(of: b.element) ?? order.count + b.offset)
        }.map(\.element)
        rows = sorted.map { ($0, !hidden.contains($0)) }
        table.reloadData()
        updatePreview()
    }

    private func updatePreview() {
        host.show(menuPreview(style: Prefs.style, titles: rows.filter(\.shown).map(\.title), mode: mode))
    }

    private func save() {
        Prefs.setOrder(rows.map(\.title), mode, kind)
        Prefs.setHidden(Set(rows.filter { !$0.shown }.map(\.title)), mode, kind)
    }

    @objc private func selectionChanged() { load() }
    @objc private func resetContents() { Prefs.resetContents(mode, kind) }

    @objc private func toggled(_ sender: NSButton) {
        rows[sender.tag].shown = sender.state == .on
        save()
    }

    func numberOfRows(in tableView: NSTableView) -> Int { rows.count }

    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        let box = NSButton(checkboxWithTitle: L(rows[row].title), target: self, action: #selector(toggled(_:)))
        box.state = rows[row].shown ? .on : .off
        box.tag = row
        return box
    }

    func tableView(_ tableView: NSTableView, pasteboardWriterForRow row: Int) -> NSPasteboardWriting? {
        let item = NSPasteboardItem()
        item.setString(String(row), forType: Self.rowType)
        return item
    }

    func tableView(_ tableView: NSTableView, validateDrop info: NSDraggingInfo, proposedRow row: Int,
                   proposedDropOperation dropOperation: NSTableView.DropOperation) -> NSDragOperation {
        dropOperation == .above ? .move : []
    }

    func tableView(_ tableView: NSTableView, acceptDrop info: NSDraggingInfo, row: Int,
                   dropOperation: NSTableView.DropOperation) -> Bool {
        guard let string = info.draggingPasteboard.pasteboardItems?.first?.string(forType: Self.rowType),
              let from = Int(string) else { return false }
        let moved = rows.remove(at: from)
        rows.insert(moved, at: from < row ? row - 1 : row)
        save()
        return true
    }
}
