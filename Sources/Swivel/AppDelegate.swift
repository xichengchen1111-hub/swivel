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
        launchToast.show("Swivel is running — drag a file, then tap ⇧", symbol: "circle.circle")
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

    private func setUpStatusItem() {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        statusItem.button?.image = NSImage(systemSymbolName: "circle.circle", accessibilityDescription: "Swivel")

        let menu = NSMenu()
        let title = NSMenuItem(title: "Swivel is running", action: nil, keyEquivalent: "")
        title.isEnabled = false
        menu.addItem(title)
        for hint in ["Drag a file, then tap:", "    ⇧ Shift — convert formats", "    ⌘ Command — file tools", "    ⌃ Control — file actions", "Tap the same key again for the next page."] {
            let item = NSMenuItem(title: hint, action: nil, keyEquivalent: "")
            item.isEnabled = false
            menu.addItem(item)
        }
        menu.addItem(.separator())
        menu.addItem(withTitle: "Show Shelf", action: #selector(showShelf), keyEquivalent: "").target = self
        loginItem = NSMenuItem(title: "Open at Login", action: #selector(toggleLogin), keyEquivalent: "")
        loginItem.target = self
        loginItem.state = SMAppService.mainApp.status == .enabled ? .on : .off
        menu.addItem(loginItem)
        menu.addItem(.separator())
        menu.addItem(withTitle: "Quit Swivel", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        statusItem.menu = menu
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
