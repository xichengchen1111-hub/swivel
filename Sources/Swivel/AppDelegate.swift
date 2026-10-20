import AppKit
import ServiceManagement

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private let watcher = DragWatcher()
    private let wheel = WheelController()
    private var statusItem: NSStatusItem!
    private var launchToast: Toast { Toast.shared }

    func applicationDidFinishLaunching(_ notification: Notification) {
        setUpStatusItem()
        watcher.onKeyTap = { [weak self] mode in
            guard let self else { return }
            self.wheel.keyTapped(mode, urls: self.watcher.urls)
        }
        watcher.onDragEnd = { [weak self] in self?.wheel.dragEnded() }
        watcher.start()
        QuickActions.shared.syncSettings()
        Clothesline.shared.start()
        if ProcessInfo.processInfo.environment["SWIVEL_SHOW_ROPE"] != nil {  // for testing
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { Clothesline.shared.toggle() }
        }
        NotificationCenter.default.addObserver(forName: Prefs.changed, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.rebuildMenu()
                QuickActions.shared.syncSettings()
            }
        }

        // Opened by hand (Finder, Launchpad, Spotlight): show the window. Started at login: stay in the menu bar.
        let event = NSAppleEventManager.shared().currentAppleEvent
        let atLogin = event?.paramDescriptor(forKeyword: keyAELaunchedAsLogInItem) != nil
        if atLogin {
            if Prefs.isEnabled { launchToast.show(L("Swivel is running — drag a file, then tap %@", Prefs.key(for: .convert).symbol), symbol: "circle.circle") }
        } else {
            // SWIVEL_TAB=<0-5> opens a specific page (for testing): general, shortcuts, style, customize, clothesline, overview.
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
        alert.messageText = L("Hang your screenshots on the clothesline?")
        alert.informativeText = L("""
            New screenshots will skip the desktop and hang on a rope under the menu bar. \
            Move the pointer to the top edge of the screen to lower it. The floating thumbnail is turned off while this is on; \
            your own settings come back if you turn it off or quit Swivel.
            """)
        alert.addButton(withTitle: L("Take Over Screenshots"))
        alert.addButton(withTitle: L("Not Now"))
        if alert.runModal() == .alertFirstButtonReturn {
            Prefs.clotheslineTakeover = true
            ScreenshotTakeover.enable(into: Clothesline.shared.incoming)
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
        let wheelOn = Feature.wheel.isOn, ropeOn = Feature.clothesline.isOn
        let anyOn = Feature.allCases.contains { $0.isOn }
        watcher.isEnabled = wheelOn
        statusItem.button?.image = NSImage(systemSymbolName: anyOn ? "circle.circle" : "circle.dashed",
                                           accessibilityDescription: "Swivel")
        statusItem.button?.appearsDisabled = !anyOn
        let keys = WheelMode.allCases.map { "\(Prefs.key(for: $0).symbol) \($0.title)" }.joined(separator: " · ")
        var tip: [String] = []
        if wheelOn { tip.append(L("Wheel: drag a file, then tap %@", keys)) }
        if ropeOn { tip.append(L("Clothesline: move to the top edge, or press ⌃⌥T")) }
        statusItem.button?.toolTip = tip.isEmpty ? L(anyOn ? "Swivel — open the menu to use your enabled tools." : "Swivel — everything is off. Open this menu to turn a feature on.") : tip.joined(separator: "\n")

        let menu = NSMenu()
        menu.autoenablesItems = false
        let header = NSMenuItem(title: L("Swivel"), action: nil, keyEquivalent: "")
        header.isEnabled = false
        menu.addItem(header)
        // Quick switches, one per feature.
        for (i, feature) in Feature.allCases.enumerated() {
            let item = add(to: menu, feature.title, #selector(toggleFeature(_:)))
            item.tag = i
            item.state = feature.isOn ? .on : .off
            item.image = NSImage(systemSymbolName: feature.symbol, accessibilityDescription: nil)
            item.toolTip = feature.blurb
        }

        if wheelOn {
            menu.addItem(.separator())
            let how = NSMenuItem(title: L("Wheel — drag a file, keep holding, then tap a key:"), action: nil, keyEquivalent: "")
            how.isEnabled = false
            menu.addItem(how)
            for (mode, title, symbol) in [(WheelMode.convert, "Convert formats", "arrow.triangle.2.circlepath"),
                                          (.tools, "File tools", "slider.horizontal.3"),
                                          (.actions, "File actions", "bolt")] {
                let item = NSMenuItem(title: "\(Prefs.key(for: mode).title) — \(L(title))", action: nil, keyEquivalent: "")
                item.image = NSImage(systemSymbolName: symbol, accessibilityDescription: nil)
                item.submenu = Self.guide(for: mode)
                menu.addItem(item)
            }
            add(to: menu, L("Menu Style: %@…", Prefs.style.title), #selector(openStyle))

        }

        if Feature.shelf.isOn { add(to: menu, "Show Shelf", #selector(showShelf)) }
        if Feature.textCapture.isOn { add(to: menu, "Extract Text…", #selector(extractText)) }
        if Feature.imagePresets.isOn { add(to: menu, "Image Presets…", #selector(openPresets)) }
        if Feature.quickActions.isOn { add(to: menu, "Show Quick Actions", #selector(showQuickActions)) }

        if ropeOn {
            menu.addItem(.separator())
            add(to: menu, "Show Clothesline", #selector(toggleRope), key: "t").keyEquivalentModifierMask = [.control, .option]
            add(to: menu, "Hang New Screenshots", #selector(toggleTakeover)).state = Prefs.clotheslineTakeover ? .on : .off
            add(to: menu, "Hang Copied Images", #selector(toggleHangCopied)).state = Prefs.clotheslineHangCopied ? .on : .off
        }

        menu.addItem(.separator())
        add(to: menu, "Haptic Feedback", #selector(toggleHaptics)).state = Prefs.haptics ? .on : .off
        add(to: menu, "Sound Effects", #selector(toggleSounds)).state = Prefs.soundEffects ? .on : .off
        menu.addItem(.separator())
        add(to: menu, "Open Swivel…", #selector(openWindow), key: ",")
        add(to: menu, "Open at Login", #selector(toggleLogin)).state = SMAppService.mainApp.status == .enabled ? .on : .off
        menu.addItem(.separator())
        menu.addItem(withTitle: L("Quit Swivel"), action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        statusItem.menu = menu
    }

    @discardableResult
    private func add(to menu: NSMenu, _ title: String, _ action: Selector, key: String = "") -> NSMenuItem {
        let item = NSMenuItem(title: L(title), action: action, keyEquivalent: key)
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
            let titles = Catalog.items(for: kind.sampleURLs, mode: mode).map { $0.displayTitle }
            guard !titles.isEmpty else { continue }
            let label = mode == .actions ? L("Any file or folder") : kind.title
            let item = NSMenuItem(title: "\(label):  \(titles.joined(separator: " · "))", action: nil, keyEquivalent: "")
            item.image = NSImage(systemSymbolName: mode == .actions ? "doc.on.doc" : kind.symbol, accessibilityDescription: nil)
            menu.addItem(item)
        }
        menu.addItem(.separator())
        let customize = NSMenuItem(title: L("Customize…"), action: #selector(AppDelegate.openCustomize), keyEquivalent: "")
        customize.target = NSApp.delegate
        menu.addItem(customize)
        return menu
    }

    @objc private func toggleFeature(_ sender: NSMenuItem) {
        let feature = Feature.allCases[sender.tag]
        feature.isOn.toggle()
        launchToast.show(L(feature.isOn ? "%@ is on" : "%@ is off", feature.title), symbol: feature.isOn ? feature.symbol : "circle.dashed")
    }

    @objc private func toggleHangCopied() { Prefs.clotheslineHangCopied.toggle() }
    @objc private func toggleHaptics() { Prefs.haptics.toggle() }
    @objc private func toggleSounds() { Prefs.soundEffects.toggle() }

    @objc private func openWindow() { AppWindow.shared.show(.overview) }
    @objc private func openStyle() { AppWindow.shared.show(.style) }
    @objc fileprivate func openCustomize() { AppWindow.shared.show(.customize) }
    @objc private func toggleRope() { Clothesline.shared.toggle() }

    @objc private func toggleTakeover() {
        Prefs.clotheslineTakeover.toggle()
        if Prefs.clotheslineTakeover && Prefs.clotheslineEnabled { ScreenshotTakeover.enable(into: Clothesline.shared.incoming) } else { ScreenshotTakeover.restore() }
    }
    @objc private func extractText() {
        if let url = ToolUI.chooseFiles(imagesOnly: true, multiple: false).first { TextCaptureWindow.present(url) }
    }
    @objc private func openPresets() { AppWindow.shared.show(.imagePresets) }
    @objc private func showQuickActions() { QuickActions.shared.show() }
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
