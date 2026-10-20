import AppKit
import ServiceManagement

/// The app's main window: settings tabs plus 晾图. While it is open Swivel shows in the Dock with a normal menu bar.
@MainActor
final class AppWindow: NSObject, NSWindowDelegate {
    static let shared = AppWindow()

    enum Tab: Int { case general, shortcuts, style, customize, clipline }

    private var window: NSWindow?
    private var tabs: NSTabViewController?
    let clipline = ClipLinePane()

    func show(_ tab: Tab? = nil) {
        let window = self.window ?? makeWindow()
        self.window = window
        if let tab { tabs?.selectedTabViewItemIndex = tab.rawValue }
        NSApp.setActivationPolicy(.regular)
        NSApp.mainMenu = Self.mainMenu()
        NSApp.activate()
        window.makeKeyAndOrderFront(nil)
    }

    private func makeWindow() -> NSWindow {
        let tabs = TitledTabs()
        tabs.tabStyle = .toolbar
        tabs.transitionOptions = [.crossfade, .allowUserInteraction]
        for (vc, title, symbol) in [(GeneralPane() as NSViewController, "General", "gearshape"),
                                    (ShortcutsPane(), "Shortcuts", "keyboard"),
                                    (StylePane(), "Style", "paintpalette"),
                                    (CustomizePane(), "Customize", "slider.horizontal.3"),
                                    (clipline, "晾图", "photo.on.rectangle.angled")] {
            vc.title = title  // shown as the window title for the selected tab
            let item = NSTabViewItem(viewController: vc)
            item.label = title
            item.image = NSImage(systemSymbolName: symbol, accessibilityDescription: title)
            tabs.addTabViewItem(item)
        }
        self.tabs = tabs
        let window = NSWindow(contentViewController: tabs)
        window.styleMask = [.titled, .closable, .miniaturizable, .resizable]
        window.toolbarStyle = .preference
        window.title = "Swivel"
        window.isReleasedWhenClosed = false
        window.delegate = self
        window.setFrameAutosaveName("SwivelMain")
        window.center()
        return window
    }

    func windowWillClose(_ notification: Notification) {
        // Back to a menu-bar-only app once the window is gone.
        DispatchQueue.main.async {
            if !(NSApp.windows.contains { $0.isVisible && $0.styleMask.contains(.titled) && !($0 is NSPanel) }) {
                NSApp.setActivationPolicy(.accessory)
            }
        }
    }

    /// App, Edit and Window menus, so ⌘V, ⌘C, ⌘A and ⌘W work in the window (and in 晾图).
    private static func mainMenu() -> NSMenu {
        let main = NSMenu()
        let app = NSMenu()
        app.addItem(withTitle: "About Swivel", action: #selector(NSApplication.orderFrontStandardAboutPanel(_:)), keyEquivalent: "")
        app.addItem(.separator())
        app.addItem(withTitle: "Hide Swivel", action: #selector(NSApplication.hide(_:)), keyEquivalent: "h")
        app.addItem(withTitle: "Quit Swivel", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        let edit = NSMenu(title: "Edit")
        edit.addItem(withTitle: "Undo", action: Selector(("undo:")), keyEquivalent: "z")
        edit.addItem(withTitle: "Redo", action: Selector(("redo:")), keyEquivalent: "Z")
        edit.addItem(.separator())
        edit.addItem(withTitle: "Cut", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        edit.addItem(withTitle: "Copy", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        edit.addItem(withTitle: "Paste", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        edit.addItem(withTitle: "Select All", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        let windowMenu = NSMenu(title: "Window")
        windowMenu.addItem(withTitle: "Close", action: #selector(NSWindow.performClose(_:)), keyEquivalent: "w")
        windowMenu.addItem(withTitle: "Minimize", action: #selector(NSWindow.performMiniaturize(_:)), keyEquivalent: "m")
        for (title, menu) in [("Swivel", app), ("Edit", edit), ("Window", windowMenu)] {
            let item = NSMenuItem(title: title, action: nil, keyEquivalent: "")
            item.submenu = menu
            main.addItem(item)
        }
        NSApp.windowsMenu = windowMenu
        return main
    }
}

/// Keeps the window title in step with the selected tab ("Swivel — Style").
final class TitledTabs: NSTabViewController {
    override func tabView(_ tabView: NSTabView, didSelect tabViewItem: NSTabViewItem?) {
        super.tabView(tabView, didSelect: tabViewItem)
        view.window?.title = "Swivel — \(tabViewItem?.label ?? "")"
    }

    override func viewDidAppear() {
        super.viewDidAppear()
        view.window?.title = "Swivel — \(tabViewItems[selectedTabViewItemIndex].label)"
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
        let field = NSTextField(wrappingLabelWithString: text)
        field.font = .systemFont(ofSize: size, weight: weight)
        if secondary { field.textColor = .secondaryLabelColor }
        field.preferredMaxLayoutWidth = self.size.width - 64
        return field
    }

    func row(_ title: String, _ views: NSView...) -> NSStackView {
        let name = NSTextField(labelWithString: title.isEmpty ? "" : title + ":")
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
    private let enabled = NSButton(checkboxWithTitle: "Open menus while dragging files", target: nil, action: nil)
    private let login = NSButton(checkboxWithTitle: "Open Swivel at login", target: nil, action: nil)

    init() { super.init(size: NSSize(width: 620, height: 420)) }
    required init?(coder: NSCoder) { fatalError() }

    override func build() {
        let icon = NSImageView(image: NSImage(systemSymbolName: "circle.circle", accessibilityDescription: nil)!)
        icon.symbolConfiguration = .init(pointSize: 40, weight: .light)
        icon.contentTintColor = .controlAccentColor
        let title = label("Swivel", size: 22, weight: .bold)
        let subtitle = label("Drag a file, tap a key, drop it on the menu that appears.", secondary: true)
        let titles = NSStackView(views: [title, subtitle])
        titles.orientation = .vertical
        titles.alignment = .leading
        titles.spacing = 2
        stack.addArrangedSubview(NSStackView(views: [icon, titles]))

        enabled.target = self
        enabled.action = #selector(toggleEnabled)
        login.target = self
        login.action = #selector(toggleLogin)
        stack.addArrangedSubview(row("Swivel", enabled))
        stack.addArrangedSubview(row("Startup", login))

        let steps = label("""
            1.  Start dragging any file in Finder and keep holding the mouse button.
            2.  Tap a key once — the menu opens under the pointer.
            3.  Drop the file on an option. Results are saved next to the original.
            Tap the same key again for the next page. Drop on empty space to cancel.
            """, secondary: true)
        steps.preferredMaxLayoutWidth = 400
        stack.addArrangedSubview(row("How it works", steps))

        let shelf = NSButton(title: "Show Shelf", target: self, action: #selector(showShelf))
        let liangtu = NSButton(title: "Open 晾图", target: self, action: #selector(openClipline))
        stack.addArrangedSubview(row("", shelf, liangtu))
    }

    override func refresh() {
        enabled.state = Prefs.isEnabled ? .on : .off
        login.state = SMAppService.mainApp.status == .enabled ? .on : .off
    }

    @objc private func toggleEnabled() { Prefs.isEnabled = enabled.state == .on }

    @objc private func toggleLogin() {
        do {
            if login.state == .on { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
        } catch {
            NSAlert(error: error).runModal()
        }
        refresh()
    }

    @objc private func showShelf() { Shelf.shared.show() }
    @objc private func openClipline() { AppWindow.shared.show(.clipline) }
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
        stack.addArrangedSubview(row("", NSButton(title: "Restore Defaults", target: self, action: #selector(reset))))
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
        box.contentView = content

        let click = NSClickGestureRecognizer(target: self, action: #selector(picked(_:)))
        box.addGestureRecognizer(click)
        box.toolTip = "Use the \(style.title) template"
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
        modePopup.addItems(withTitles: ["Convert formats", "File tools", "File actions"])
        kindPopup.addItems(withTitles: kinds.map(\.title))
        for (popup, i) in [(modePopup, 0), (kindPopup, 1)] {
            popup.target = self
            popup.action = #selector(selectionChanged)
            popup.tag = i
        }
        for (i, kind) in kinds.enumerated() {
            kindPopup.item(at: i)?.image = NSImage(systemSymbolName: kind.symbol, accessibilityDescription: nil)
        }
        let reset = NSButton(title: "Restore Defaults", target: self, action: #selector(resetContents))
        let pickers = NSStackView(views: [NSTextField(labelWithString: "Menu:"), modePopup,
                                          NSTextField(labelWithString: "File type:"), kindPopup, reset])
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
        let box = NSButton(checkboxWithTitle: rows[row].title, target: self, action: #selector(toggled(_:)))
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
