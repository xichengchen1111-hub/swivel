import AppKit
import ServiceManagement

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private let watcher = DragWatcher()
    private let wheel = WheelController()
    private var statusItem: NSStatusItem!
    private var loginItem: NSMenuItem!

    func applicationDidFinishLaunching(_ notification: Notification) {
        setUpStatusItem()
        watcher.onKeyTap = { [weak self] mode in
            guard let self else { return }
            self.wheel.keyTapped(mode, urls: self.watcher.urls)
        }
        watcher.onDragEnd = { [weak self] in self?.wheel.dragEnded() }
        watcher.start()
        if isEnabled { launchToast.show("Swivel is running — drag a file, then tap ⇧", symbol: "circle.circle") }
    }

    private let launchToast = Toast()

    /// Opening the app again (Spotlight, Launchpad, Finder) shows instructions, since the menu bar icon
    /// can be hidden behind the notch when the menu bar is crowded.
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        NSApp.activate()
        let alert = NSAlert()
        alert.messageText = "Swivel is running"
        alert.informativeText = """
            Start dragging a file in Finder, keep holding the mouse button, then tap:

            ⇧ Shift — convert formats
            ⌘ Command — file tools
            ⌃ Control — file actions

            Drop the file on a segment of the wheel. Tap the same key again for more options.
            """
        alert.addButton(withTitle: "OK")
        alert.addButton(withTitle: "Quit Swivel")
        if alert.runModal() == .alertSecondButtonReturn { NSApp.terminate(nil) }
        return false
    }

    private var enabledItem: NSMenuItem!
    private static let enabledKey = "enabled"

    private var isEnabled: Bool {
        get { UserDefaults.standard.object(forKey: Self.enabledKey) as? Bool ?? true }
        set { UserDefaults.standard.set(newValue, forKey: Self.enabledKey) }
    }

    private func setUpStatusItem() {
        // New items are added at the far left of the status area, which on notched Macs with a crowded
        // menu bar lands behind the notch. Start among the right-hand icons instead; ⌘-dragging the icon
        // later saves the user's own position under the same key.
        let positionKey = "NSStatusItem Preferred Position Swivel"
        if UserDefaults.standard.object(forKey: positionKey) == nil { UserDefaults.standard.set(330.0, forKey: positionKey) }
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        statusItem.autosaveName = "Swivel"

        let menu = NSMenu()
        menu.autoenablesItems = false
        let header = NSMenuItem(title: "Swivel", action: nil, keyEquivalent: "")
        header.isEnabled = false
        menu.addItem(header)
        enabledItem = NSMenuItem(title: "Enabled", action: #selector(toggleEnabled), keyEquivalent: "e")
        enabledItem.target = self
        menu.addItem(enabledItem)
        menu.addItem(.separator())

        let how = NSMenuItem(title: "Drag a file, keep holding, then tap a key:", action: nil, keyEquivalent: "")
        how.isEnabled = false
        menu.addItem(how)
        for (mode, title, symbol) in [(WheelMode.convert, "⇧ Shift — Convert formats", "arrow.triangle.2.circlepath"),
                                      (.tools, "⌘ Command — File tools", "slider.horizontal.3"),
                                      (.actions, "⌃ Control — File actions", "bolt")] {
            let item = NSMenuItem(title: title, action: nil, keyEquivalent: "")
            item.image = NSImage(systemSymbolName: symbol, accessibilityDescription: nil)
            item.submenu = Self.guide(for: mode)
            menu.addItem(item)
        }
        let page = NSMenuItem(title: "Tap the same key again for more options", action: nil, keyEquivalent: "")
        page.isEnabled = false
        menu.addItem(page)

        menu.addItem(.separator())
        let shelf = NSMenuItem(title: "Show Shelf", action: #selector(showShelf), keyEquivalent: "")
        shelf.target = self
        menu.addItem(shelf)
        loginItem = NSMenuItem(title: "Open at Login", action: #selector(toggleLogin), keyEquivalent: "")
        loginItem.target = self
        loginItem.state = SMAppService.mainApp.status == .enabled ? .on : .off
        menu.addItem(loginItem)
        menu.addItem(.separator())
        menu.addItem(withTitle: "Quit Swivel", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        statusItem.menu = menu
        applyEnabled()
    }

    /// One row per file kind listing exactly what that key's wheel offers, built from the real catalog.
    private static func guide(for mode: WheelMode) -> NSMenu {
        let menu = NSMenu()
        menu.autoenablesItems = false
        let kinds: [(String, String, [String], String)] = [
            ("Images", "photo", ["a.jpg"], "+ Collage with several images"),
            ("Video", "film", ["a.mov"], "+ Merge with several videos"),
            ("Audio", "music.note", ["a.mp3"], "+ Merge with several audio files"),
            ("PDF", "doc.richtext", ["a.pdf"], "+ Merge with several PDFs"),
            ("Documents", "doc.text", ["a.docx"], ""),
            ("Archives", "archivebox", ["a.zip"], ""),
            ("Other files & folders", "folder", ["a.unknownext"], ""),
        ]
        var seen: [String] = []
        for (name, symbol, samples, multiNote) in kinds {
            let titles = Catalog.items(for: samples.map { URL(fileURLWithPath: "/nonexistent/\($0)") }, mode: mode).map(\.title)
            guard !titles.isEmpty else { continue }
            let list = titles.joined(separator: " · ")
            if mode == .actions {  // the same for every file kind
                if seen.contains(list) { continue }
                seen.append(list)
            }
            let label = mode == .actions ? "Any file or folder" : name
            let item = NSMenuItem(title: "\(label):  \(list)", action: nil, keyEquivalent: "")
            item.image = NSImage(systemSymbolName: mode == .actions ? "doc.on.doc" : symbol, accessibilityDescription: nil)
            if mode == .tools, !multiNote.isEmpty { item.toolTip = multiNote }
            menu.addItem(item)
            if mode == .tools, !multiNote.isEmpty {
                let note = NSMenuItem(title: "      \(multiNote)", action: nil, keyEquivalent: "")
                note.isEnabled = false
                menu.addItem(note)
            }
        }
        return menu
    }

    @objc private func toggleEnabled() {
        isEnabled.toggle()
        applyEnabled()
        launchToast.show(isEnabled ? "Swivel is on" : "Swivel is off — drags are ignored",
                         symbol: isEnabled ? "circle.circle" : "circle.dashed")
    }

    private func applyEnabled() {
        watcher.isEnabled = isEnabled
        enabledItem.state = isEnabled ? .on : .off
        statusItem.button?.image = NSImage(systemSymbolName: isEnabled ? "circle.circle" : "circle.dashed",
                                           accessibilityDescription: "Swivel")
        statusItem.button?.appearsDisabled = !isEnabled
        statusItem.button?.toolTip = isEnabled
            ? "Swivel is on — drag a file, then tap ⇧ Convert · ⌘ Tools · ⌃ Actions"
            : "Swivel is off — open this menu and choose Enabled to turn it back on"
    }

    @objc private func showShelf() { Shelf.shared.show() }

    @objc private func toggleLogin() {
        do {
            if SMAppService.mainApp.status == .enabled {
                try SMAppService.mainApp.unregister()
            } else {
                try SMAppService.mainApp.register()
            }
        } catch {
            NSAlert(error: error).runModal()
        }
        loginItem.state = SMAppService.mainApp.status == .enabled ? .on : .off
    }
}
