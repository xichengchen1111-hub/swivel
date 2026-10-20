import AppKit
import Carbon
import Quartz

// MARK: - Quick Look

/// An invisible key window that drives the shared Quick Look panel for long-press and force click.
@MainActor
final class QuickLooker: NSObject, QLPreviewPanelDataSource, QLPreviewPanelDelegate {
    static let shared = QuickLooker()
    private var url: URL?
    private lazy var host: NSWindow = {
        let window = QLHostWindow(contentRect: NSRect(x: -10_000, y: -10_000, width: 1, height: 1),
                                  styleMask: [.borderless], backing: .buffered, defer: false)
        window.looker = self
        window.isReleasedWhenClosed = false
        return window
    }()

    func show(_ url: URL) {
        self.url = url
        NSApp.activate()
        host.makeKeyAndOrderFront(nil)
        guard let panel = QLPreviewPanel.shared() else { return }
        panel.updateController()
        panel.reloadData()
        panel.makeKeyAndOrderFront(nil)
    }

    nonisolated func numberOfPreviewItems(in panel: QLPreviewPanel!) -> Int { 1 }

    nonisolated func previewPanel(_ panel: QLPreviewPanel!, previewItemAt index: Int) -> (any QLPreviewItem)! {
        MainActor.assumeIsolated { url as NSURL? }
    }
}

final class QLHostWindow: NSWindow {
    weak var looker: QuickLooker?
    override var canBecomeKey: Bool { true }
    override func acceptsPreviewPanelControl(_ panel: QLPreviewPanel!) -> Bool { true }
    override func beginPreviewPanelControl(_ panel: QLPreviewPanel!) {
        panel.dataSource = looker
        panel.delegate = looker
    }
    override func endPreviewPanelControl(_ panel: QLPreviewPanel!) {
        panel.dataSource = nil
        panel.delegate = nil
    }
}

// MARK: - Screenshot takeover

/// Points macOS screenshots at the clothesline folder and turns off the floating thumbnail,
/// remembering the user's own settings so they can be put back.
enum ScreenshotTakeover {
    private static let domain = "com.apple.screencapture" as CFString
    private static let savedKey = "savedScreencaptureSettings"

    static func enable(into folder: URL) {
        let defaults = UserDefaults.standard
        if defaults.dictionary(forKey: savedKey) == nil {
            var saved: [String: Any] = [:]
            for key in ["location", "show-thumbnail"] {
                if let value = CFPreferencesCopyAppValue(key as CFString, domain) { saved[key] = value }
                else { saved[key] = "__unset__" }
            }
            defaults.set(saved, forKey: savedKey)
        }
        CFPreferencesSetAppValue("location" as CFString, folder.path as CFString, domain)
        CFPreferencesSetAppValue("show-thumbnail" as CFString, kCFBooleanFalse, domain)
        CFPreferencesAppSynchronize(domain)
    }

    /// Where screenshots went before Swivel took over (the Desktop if the user never changed it).
    static var originalLocation: URL {
        let saved = UserDefaults.standard.dictionary(forKey: savedKey)
        if let path = saved?["location"] as? String, path != "__unset__" {
            return URL(fileURLWithPath: (path as NSString).expandingTildeInPath, isDirectory: true)
        }
        if saved == nil, let path = CFPreferencesCopyAppValue("location" as CFString, domain) as? String {
            return URL(fileURLWithPath: (path as NSString).expandingTildeInPath, isDirectory: true)
        }
        return FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Desktop", isDirectory: true)
    }

    static func restore() {
        let defaults = UserDefaults.standard
        guard let saved = defaults.dictionary(forKey: savedKey) else { return }
        for key in ["location", "show-thumbnail"] {
            let value = saved[key]
            if value as? String == "__unset__" || value == nil {
                CFPreferencesSetAppValue(key as CFString, nil, domain)
            } else {
                CFPreferencesSetAppValue(key as CFString, value as CFPropertyList, domain)
            }
        }
        CFPreferencesAppSynchronize(domain)
        defaults.removeObject(forKey: savedKey)
    }
}

// MARK: - Global hotkey

/// ⌃⌥T anywhere toggles the rope. Uses Carbon hotkeys, which need no extra permission.
final class HotKey {
    private var ref: EventHotKeyRef?
    nonisolated(unsafe) private static var action: (() -> Void)?

    init(_ action: @escaping () -> Void) {
        Self.action = action
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), { _, _, _ in
            DispatchQueue.main.async { HotKey.action?() }
            return noErr
        }, 1, &spec, nil, nil)
        let id = EventHotKeyID(signature: OSType(0x5357_5654), id: 1)  // 'SWVT'
        RegisterEventHotKey(UInt32(kVK_ANSI_T), UInt32(controlKey | optionKey), id, GetApplicationEventTarget(), 0, &ref)
    }
}
