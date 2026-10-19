import AppKit

enum WheelMode {
    case convert, tools, actions

    var title: String {
        switch self {
        case .convert: "Convert"
        case .tools: "Tools"
        case .actions: "Actions"
        }
    }

    var key: String {
        switch self {
        case .convert: "⇧"
        case .tools: "⌘"
        case .actions: "⌃"
        }
    }
}

/// Notices when a file drag is in progress anywhere on screen and reports modifier-key taps during it.
///
/// Polls instead of using event monitors: during a drag session the window server swallows most
/// mouse events, and polling the drag pasteboard and modifier state needs no Accessibility permission.
@MainActor
final class DragWatcher {
    var onKeyTap: ((WheelMode) -> Void)?
    var onDragEnd: (() -> Void)?
    private(set) var urls: [URL] = []

    private let dragPasteboard = NSPasteboard(name: .drag)
    private var timer: Timer?
    private var wasDown = false
    private var baselineCount = 0
    private var active = false
    private var lastFlags: NSEvent.ModifierFlags = []

    func start() {
        let timer = Timer(timeInterval: 1.0 / 60.0, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.tick() }
        }
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
    }

    private func tick() {
        let down = NSEvent.pressedMouseButtons & 1 != 0
        let flags = NSEvent.modifierFlags.intersection([.shift, .command, .control])
        defer {
            wasDown = down
            lastFlags = flags
        }

        guard down else {
            if active {
                active = false
                urls = []
                onDragEnd?()
            }
            return
        }

        if !wasDown {
            baselineCount = dragPasteboard.changeCount
            return
        }

        if !active {
            let count = dragPasteboard.changeCount
            guard count != baselineCount else { return }
            baselineCount = count
            let found = dragPasteboard.readObjects(forClasses: [NSURL.self],
                                                   options: [.urlReadingFileURLsOnly: true]) as? [URL] ?? []
            guard !found.isEmpty else { return }
            urls = found
            active = true
            // Keys already held when the drag began don't count; the wheel opens on a fresh tap.
            return
        }

        let pressed = flags.subtracting(lastFlags)
        if pressed.contains(.shift) {
            onKeyTap?(.convert)
        } else if pressed.contains(.command) {
            onKeyTap?(.tools)
        } else if pressed.contains(.control) {
            onKeyTap?(.actions)
        }
    }
}
