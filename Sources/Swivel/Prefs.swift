import AppKit

/// A modifier key that can open a menu while dragging.
enum TriggerKey: String, CaseIterable {
    case shift, command, control, option, fn

    var flag: NSEvent.ModifierFlags {
        switch self {
        case .shift: .shift
        case .command: .command
        case .control: .control
        case .option: .option
        case .fn: .function
        }
    }

    var symbol: String {
        switch self {
        case .shift: L("⇧")
        case .command: L("⌘")
        case .control: L("⌃")
        case .option: L("⌥")
        case .fn: L("fn")
        }
    }

    var title: String {
        switch self {
        case .shift: L("⇧ Shift")
        case .command: L("⌘ Command")
        case .control: L("⌃ Control")
        case .option: L("⌥ Option")
        case .fn: L("fn / 🌐")
        }
    }
}

/// How the menu that pops up under the cursor is laid out.
enum MenuStyle: String, CaseIterable {
    case wheel, list, shelf, notes, clothesline

    var title: String {
        switch self {
        case .wheel: L("Wheel")
        case .list: L("List")
        case .shelf: L("Bookshelf")
        case .notes: L("Sticky Notes")
        case .clothesline: L("Clothesline")
        }
    }

    var blurb: String {
        switch self {
        case .wheel: L("A ring of keycaps around the cursor.")
        case .list: L("A tidy column, like a context menu.")
        case .shelf: L("Book spines standing on a wooden shelf.")
        case .notes: L("Colourful sticky notes pinned in a grid.")
        case .clothesline: L("Tags pegged on a rope, swaying gently.")
        }
    }
}

/// User settings, stored in UserDefaults. Posts `Prefs.changed` whenever anything is saved.
@MainActor
enum Prefs {
    static let changed = Notification.Name("SwivelPrefsChanged")
    private static let defaults = UserDefaults.standard

    private static func post() { NotificationCenter.default.post(name: changed, object: nil) }

    /// Additional tools are opt-in. Disabling them retains their settings.
    static func featureEnabled(_ feature: Feature) -> Bool {
        defaults.bool(forKey: "feature.\(feature.rawValue)")
    }

    static func setFeatureEnabled(_ feature: Feature, _ enabled: Bool) {
        defaults.set(enabled, forKey: "feature.\(feature.rawValue)"); post()
    }

    static var imagePresets: [ImagePreset] {
        get {
            guard let data = defaults.data(forKey: "imagePresets.v1"),
                  let presets = try? JSONDecoder().decode([ImagePreset].self, from: data) else {
                return ImagePreset.starters
            }
            return presets.filter { $0.isValid }
        }
        set {
            guard let data = try? JSONEncoder().encode(newValue.filter { $0.isValid }) else { return }
            defaults.set(data, forKey: "imagePresets.v1"); post()
        }
    }

    static var quickShortcut: QuickShortcut {
        get { QuickShortcut(rawValue: defaults.string(forKey: "quickActions.shortcut") ?? "") ?? .space }
        set { defaults.set(newValue.rawValue, forKey: "quickActions.shortcut"); post() }
    }

    /// Force Touch trackpad taps when the pointer moves between menu items and on a drop.
    static var haptics: Bool {
        get { defaults.object(forKey: "feedback.haptics") as? Bool ?? true }
        set { defaults.set(newValue, forKey: "feedback.haptics"); post() }
    }

    /// Short interface sounds for the same moments (always quiet, and off when macOS's UI sounds are off).
    static var soundEffects: Bool {
        get { defaults.object(forKey: "feedback.sounds") as? Bool ?? true }
        set { defaults.set(newValue, forKey: "feedback.sounds"); post() }
    }

    static var isEnabled: Bool {
        get { defaults.object(forKey: "enabled") as? Bool ?? true }
        set { defaults.set(newValue, forKey: "enabled"); post() }
    }

    // MARK: Clothesline

    static var clotheslineEnabled: Bool {
        get { defaults.object(forKey: "clothesline.enabled") as? Bool ?? true }
        set { defaults.set(newValue, forKey: "clothesline.enabled"); post() }
    }

    /// Whether new screenshots go to the clothesline folder (asked once on first launch).
    static var clotheslineTakeover: Bool {
        get { defaults.bool(forKey: "clothesline.takeover") }
        set { defaults.set(newValue, forKey: "clothesline.takeover"); post() }
    }

    static var clotheslineAsked: Bool {
        get { defaults.bool(forKey: "clothesline.asked") }
        set { defaults.set(newValue, forKey: "clothesline.asked") }
    }

    static var clotheslineHangCopied: Bool {
        get { defaults.bool(forKey: "clothesline.hangCopied") }
        set { defaults.set(newValue, forKey: "clothesline.hangCopied"); post() }
    }

    static var style: MenuStyle {
        get { MenuStyle(rawValue: defaults.string(forKey: "style") ?? "") ?? .wheel }
        set { defaults.set(newValue.rawValue, forKey: "style"); post() }
    }

    private static let defaultKeys: [WheelMode: TriggerKey] = [.convert: .shift, .tools: .command, .actions: .control]

    static func key(for mode: WheelMode) -> TriggerKey {
        TriggerKey(rawValue: defaults.string(forKey: "key.\(mode.id)") ?? "") ?? defaultKeys[mode]!
    }

    /// Assigns a key; if another menu already used it, that menu takes this one's old key.
    static func setKey(_ key: TriggerKey, for mode: WheelMode) {
        let old = self.key(for: mode)
        if let clash = WheelMode.allCases.first(where: { $0 != mode && self.key(for: $0) == key }) {
            defaults.set(old.rawValue, forKey: "key.\(clash.id)")
        }
        defaults.set(key.rawValue, forKey: "key.\(mode.id)")
        post()
    }

    static func resetKeys() {
        for mode in WheelMode.allCases { defaults.removeObject(forKey: "key.\(mode.id)") }
        post()
    }

    // MARK: Menu contents

    private static func slot(_ mode: WheelMode, _ kind: FileKind) -> String { "\(mode.id).\(kind.rawValue)" }

    static func hidden(_ mode: WheelMode, _ kind: FileKind) -> Set<String> {
        Set(defaults.stringArray(forKey: "hidden.\(slot(mode, kind))") ?? [])
    }

    static func setHidden(_ titles: Set<String>, _ mode: WheelMode, _ kind: FileKind) {
        defaults.set(Array(titles), forKey: "hidden.\(slot(mode, kind))")
        post()
    }

    static func order(_ mode: WheelMode, _ kind: FileKind) -> [String] {
        defaults.stringArray(forKey: "order.\(slot(mode, kind))") ?? []
    }

    static func setOrder(_ titles: [String], _ mode: WheelMode, _ kind: FileKind) {
        defaults.set(titles, forKey: "order.\(slot(mode, kind))")
        post()
    }

    /// Saves order and hidden items together, posting one change.
    static func setArrangement(order: [String], hidden: Set<String>, _ mode: WheelMode, _ kind: FileKind) {
        defaults.set(order, forKey: "order.\(slot(mode, kind))")
        defaults.set(Array(hidden), forKey: "hidden.\(slot(mode, kind))")
        post()
    }

    static func resetContents(_ mode: WheelMode, _ kind: FileKind) {
        defaults.removeObject(forKey: "hidden.\(slot(mode, kind))")
        defaults.removeObject(forKey: "order.\(slot(mode, kind))")
        post()
    }

    /// Applies the user's order and hidden set to a menu's default items.
    static func arrange(_ items: [WheelItem], _ mode: WheelMode, _ kind: FileKind) -> [WheelItem] {
        let hidden = self.hidden(mode, kind)
        let order = self.order(mode, kind)
        let visible = items.filter { !hidden.contains($0.title) }
        return visible.enumerated().sorted { a, b in
            let ia = order.firstIndex(of: a.element.title) ?? (order.count + a.offset)
            let ib = order.firstIndex(of: b.element.title) ?? (order.count + b.offset)
            return ia < ib
        }.map(\.element)
    }
}
