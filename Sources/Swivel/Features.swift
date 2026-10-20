import AppKit

/// The tools in the Swivel bundle. Each one can be switched on or off on its own.
enum Feature: String, CaseIterable {
    case wheel, clothesline, textCapture, shelf, imagePresets, quickActions

    var title: String {
        switch self {
        case .wheel: L("Wheel")
        case .clothesline: L("Clothesline")
        case .textCapture: L("Text Capture")
        case .shelf: L("File Shelf")
        case .imagePresets: L("Image Presets")
        case .quickActions: L("Quick Actions")
        }
    }

    var symbol: String {
        switch self {
        case .wheel: "circle.circle"
        case .clothesline: "tshirt"
        case .textCapture: "text.viewfinder"
        case .shelf: "tray.full"
        case .imagePresets: "slider.horizontal.3"
        case .quickActions: "command.square"
        }
    }

    var blurb: String {
        switch self {
        case .wheel: L("Drag a file and tap a key: a menu pops up to convert it, edit it or send it somewhere.")
        case .clothesline: L("Screenshots hang on a rope under the menu bar instead of piling up on the desktop.")
        case .textCapture: L("Turn text in an image into editable, copyable text. Everything stays on your Mac.")
        case .shelf: L("Gather files from different places, then drag a selection out together.")
        case .imagePresets: L("Save your favourite image sizes, formats and privacy settings for next time.")
        case .quickActions: L("Find an action from the keyboard, even when the menu bar is crowded.")
        }
    }

    @MainActor var isOn: Bool {
        get {
            switch self {
            case .wheel: Prefs.isEnabled
            case .clothesline: Prefs.clotheslineEnabled
            default: Prefs.featureEnabled(self)
            }
        }
        nonmutating set {
            switch self {
            case .wheel: Prefs.isEnabled = newValue
            case .clothesline: Clothesline.shared.setEnabled(newValue)
            default:
                Prefs.setFeatureEnabled(self, newValue)
                if !newValue {
                    switch self {
                    case .shelf: Shelf.shared.hide()
                    case .textCapture: TextCaptureWindow.closeAll()
                    case .quickActions: QuickActions.shared.hide()
                    default: break
                    }
                }
            }
        }
    }
}
