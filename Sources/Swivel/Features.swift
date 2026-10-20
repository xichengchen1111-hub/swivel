import AppKit

/// The tools in the Swivel bundle. Each one can be switched on or off on its own.
enum Feature: String, CaseIterable {
    case wheel, clothesline

    var title: String {
        switch self {
        case .wheel: L("Wheel")
        case .clothesline: L("Clothesline")
        }
    }

    var symbol: String {
        switch self {
        case .wheel: L("circle.circle")
        case .clothesline: L("tshirt")
        }
    }

    var blurb: String {
        switch self {
        case .wheel: L("Drag a file and tap a key: a menu pops up to convert it, edit it or send it somewhere.")
        case .clothesline: L("Screenshots hang on a rope under the menu bar instead of piling up on the desktop.")
        }
    }

    @MainActor var isOn: Bool {
        get {
            switch self {
            case .wheel: Prefs.isEnabled
            case .clothesline: Prefs.clotheslineEnabled
            }
        }
        nonmutating set {
            switch self {
            case .wheel: Prefs.isEnabled = newValue
            case .clothesline: Clothesline.shared.setEnabled(newValue)
            }
        }
    }
}
