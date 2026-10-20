import AppKit
import ServiceManagement

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private let watcher = DragWatcher()
    private let wheel = WheelController()
    private var statusItem: NSStatusItem!
    private let launchToast = Toast()

    func applicationDidFinishLaunching(_ notification: Notification) {
        setUpStatusItem()
        watcher.onKeyTap = { [weak self] mode in
            guard let self else { return }
            self.wheel.keyTapped(mode, urls: self.watcher.urls)
        }
        watcher.onDragEnd = { [weak self] in self?.wheel.dragEnded() }
        watcher.start()
        Clothesline.shared.start()
        if ProcessInfo.processInfo.environment["SWIVEL_SHOW_ROPE"] != nil {  // for testing
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { Clothesline.shared.toggle() }
        }
        NotificationCenter.default.addObserver(forName: Prefs.changed, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.rebuildMenu() }
        }

        // Opened by hand (Finder, Launchpad, Spotlight): show the window. Started at login: stay in the menu bar.
        let event = NSAppleEventManager.shared().currentAppleEvent
        let atLogin = event?.paramDescriptor(forKeyword: keyAELaunchedAsLogInItem) != nil
        if atLogin {
            if Prefs.isEnabled { launchToast.show("Swivel is running — drag a file, then tap \(Prefs.key(for: .convert).symbol)", symbol: "circle.circle") }
        } else {
            // SWIVEL_TAB=<0-4> opens a specific tab (for testing).
            let tab = ProcessInfo.processInfo.environment["SWIVEL_TAB"].flatMap(Int.init).flatMap(AppWindow.Tab.init)
            AppWindow.shared.show(tab)
            if tab == nil { askAboutScreenshots() }
        }
    }

    func applicationWillTerminate(_ notification: Notification) { Clothesline.shared.stop() }

    /// First launch: ask before redirecting screenshots.
    private func askAboutScreenshots() {
        guard !Prefs.clotheslineAsked else { return }
        Prefs.clotheslineAsked = true
        NSApp.activate()
        let alert = NSAlert()
        alert.messageText = "Hang your screenshots on the clothesline?"
        alert.informativeText = """
            New screenshots will skip the desktop and hang on a rope under the menu bar. \
            Rest the pointer on the menu bar to lower it. The floating thumbnail is turned off while this is on; \
            your own settings come back if you turn it off or quit Swivel.
            """
        alert.addButton(withTitle: "Take Over Screenshots")
        alert.addButton(withTitle: "Not Now")
        if alert.runModal() == .alertFirstButtonReturn {
            Prefs.clotheslineTakeover = true
            ScreenshotTakeover.enable(into: Clothesline.shared.folder)
        }
    }

    /// Opening the app again while it runs brings up the window.
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        AppWindow.shared.show()
        return false
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    private func setUpStatusItem() {
        // New items are added at the far left of the status area, which on notched Macs with a crowded
        // menu bar lands behind the notch. Start among the right-hand icons instead; ⌘-dragging the icon
        // later saves the user's own position under the same key.
        let positionKey = "NSStatusItem Preferred Position Swivel"
        if UserDefaults.standard.object(forKey: positionKey) == nil { UserDefaults.standard.set(330.0, forKey: positionKey) }
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        statusItem.autosaveName = "Swivel"
        rebuildMenu()
    }

    /// Rebuilt whenever settings change, so the key names and option lists always match.
    private func rebuildMenu() {
        let enabled = Prefs.isEnabled
        watcher.isEnabled = enabled
        statusItem.button?.image = NSImage(systemSymbolName: enabled ? "circle.circle" : "circle.dashed",
                                           accessibilityDescription: "Swivel")
        statusItem.button?.appearsDisabled = !enabled
        let keys = WheelMode.allCases.map { "\(Prefs.key(for: $0).symbol) \($0.title)" }.joined(separator: " · ")
        statusItem.button?.toolTip = enabled
            ? "Swivel is on — drag a file, then tap \(keys)"
            : "Swivel is off — open this menu and choose Enabled to turn it back on"

        let menu = NSMenu()
        menu.autoenablesItems = false
        let header = NSMenuItem(title: "Swivel", action: nil, keyEquivalent: "")
        header.isEnabled = false
        menu.addItem(header)
        add(to: menu, "Enabled", #selector(toggleEnabled), key: "e").state = enabled ? .on : .off
        menu.addItem(.separator())

        let how = NSMenuItem(title: "Drag a file, keep holding, then tap a key:", action: nil, keyEquivalent: "")
        how.isEnabled = false
        menu.addItem(how)
        for (mode, title, symbol) in [(WheelMode.convert, "Convert formats", "arrow.triangle.2.circlepath"),
                                      (.tools, "File tools", "slider.horizontal.3"),
                                      (.actions, "File actions", "bolt")] {
            let item = NSMenuItem(title: "\(Prefs.key(for: mode).title) — \(title)", action: nil, keyEquivalent: "")
            item.image = NSImage(systemSymbolName: symbol, accessibilityDescription: nil)
            item.submenu = Self.guide(for: mode)
            menu.addItem(item)
        }
        let page = NSMenuItem(title: "Tap the same key again for more options", action: nil, keyEquivalent: "")
        page.isEnabled = false
        menu.addItem(page)

        menu.addItem(.separator())
        add(to: menu, "Open Swivel…", #selector(openWindow), key: ",")
        add(to: menu, "Menu Style: \(Prefs.style.title)…", #selector(openStyle))
        add(to: menu, "Show Clothesline", #selector(toggleRope), key: "t").keyEquivalentModifierMask = [.control, .option]
        add(to: menu, "Hang New Screenshots", #selector(toggleTakeover)).state = Prefs.clotheslineTakeover ? .on : .off
        add(to: menu, "Show Shelf", #selector(showShelf))
        add(to: menu, "Open at Login", #selector(toggleLogin)).state = SMAppService.mainApp.status == .enabled ? .on : .off
        menu.addItem(.separator())
        menu.addItem(withTitle: "Quit Swivel", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        statusItem.menu = menu
    }

    @discardableResult
    private func add(to menu: NSMenu, _ title: String, _ action: Selector, key: String = "") -> NSMenuItem {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: key)
        item.target = self
        menu.addItem(item)
        return item
    }

    /// One row per file kind listing exactly what that key's menu offers, in the user's order.
    private static func guide(for mode: WheelMode) -> NSMenu {
        let menu = NSMenu()
        menu.autoenablesItems = false
        let kinds: [FileKind] = mode == .actions ? [.other] : [.image, .video, .audio, .pdf, .document, .archive, .other]
        for kind in kinds {
            let titles = Catalog.items(for: kind.sampleURLs, mode: mode).map(\.title)
            guard !titles.isEmpty else { continue }
            let label = mode == .actions ? "Any file or folder" : kind.title
            let item = NSMenuItem(title: "\(label):  \(titles.joined(separator: " · "))", action: nil, keyEquivalent: "")
            item.image = NSImage(systemSymbolName: mode == .actions ? "doc.on.doc" : kind.symbol, accessibilityDescription: nil)
            menu.addItem(item)
        }
        menu.addItem(.separator())
        let customize = NSMenuItem(title: "Customize…", action: #selector(AppDelegate.openCustomize), keyEquivalent: "")
        customize.target = NSApp.delegate
        menu.addItem(customize)
        return menu
    }

    @objc private func toggleEnabled() {
        Prefs.isEnabled.toggle()
        launchToast.show(Prefs.isEnabled ? "Swivel is on" : "Swivel is off — drags are ignored",
                         symbol: Prefs.isEnabled ? "circle.circle" : "circle.dashed")
    }

    @objc private func openWindow() { AppWindow.shared.show(.general) }
    @objc private func openStyle() { AppWindow.shared.show(.style) }
    @objc fileprivate func openCustomize() { AppWindow.shared.show(.customize) }
    @objc private func toggleRope() { Clothesline.shared.toggle() }

    @objc private func toggleTakeover() {
        Prefs.clotheslineTakeover.toggle()
        if Prefs.clotheslineTakeover { ScreenshotTakeover.enable(into: Clothesline.shared.folder) } else { ScreenshotTakeover.restore() }
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
        rebuildMenu()
    }
}
