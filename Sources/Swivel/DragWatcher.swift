import AppKit
import os

let log = Logger(subsystem: "local.swivel.app", category: "drag")

enum WheelMode: CaseIterable {
    case convert, tools, actions

    var id: String {
        switch self {
        case .convert: L("convert")
        case .tools: L("tools")
        case .actions: L("actions")
        }
    }

    var title: String {
        switch self {
        case .convert: L("Convert")
        case .tools: L("Tools")
        case .actions: L("Actions")
        }
    }

    var symbol: String {
        switch self { case .convert: "arrow.triangle.2.circlepath"; case .tools: "slider.horizontal.3"; case .actions: "bolt" }
    }

    @MainActor var key: String { Prefs.key(for: self).symbol }
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
    /// When false, drags are ignored and no wheel opens.
    var isEnabled = true

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
        let flags = NSEvent.modifierFlags.intersection([.shift, .command, .control, .option, .function])
        defer {
            wasDown = down
            lastFlags = flags
        }

        guard down, isEnabled else {
            if active {
                active = false
                urls = []
                onDragEnd?()
            }
            return
        }

        if !wasDown {
            baselineCount = dragPasteboard.changeCount
            log.debug("mouse down, drag pasteboard count \(self.baselineCount)")
            return
        }

        if !active {
            let count = dragPasteboard.changeCount
            guard count != baselineCount else { return }
            // Keep retrying until the source has finished writing file URLs to the pasteboard.
            let types = dragPasteboard.types?.map(\.rawValue) ?? []
            let found = dragPasteboard.readObjects(forClasses: [NSURL.self],
                                                   options: [.urlReadingFileURLsOnly: true]) as? [URL] ?? []
            log.debug("drag pasteboard changed \(count): types \(types, privacy: .public), \(found.count) file URLs")
            guard !found.isEmpty else { return }
            baselineCount = count
            urls = found
            active = true
            log.info("file drag started: \(found.map(\.lastPathComponent), privacy: .public)")
            // Keys already held when the drag began don't count; the wheel opens on a fresh tap.
            return
        }

        let pressed = flags.subtracting(lastFlags)
        if !pressed.isEmpty { log.info("key tap during drag: \(pressed.rawValue)") }
        if let mode = WheelMode.allCases.first(where: { pressed.contains(Prefs.key(for: $0).flag) }) {
            onKeyTap?(mode)
        }
    }
}
