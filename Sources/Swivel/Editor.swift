import AppKit
import UniformTypeIdentifiers

struct Cancelled: LocalizedError {
    var errorDescription: String? { "Cancelled" }
}

/// A floating editor window: a canvas on top, controls below, Reset / Apply at the bottom.
/// `present()` suspends until the user applies (returning the saved files) or closes the window.
@MainActor
class Editor: NSObject, NSWindowDelegate {
    private static var open: [Editor] = []
    /// How many editor windows have been opened, so callers can tell whether an action showed one.
    private(set) static var presented = 0

    let window: NSWindow
    let source: URL
    let controls = NSStackView()
    private var continuation: CheckedContinuation<[URL], Error>?
    private var applyButton: NSButton!

    init(title: String, source: URL, canvas: NSView) {
        self.source = source
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 580, height: 700),
                          styleMask: [.titled, .closable, .miniaturizable, .resizable],
                          backing: .buffered, defer: false)
        super.init()
        window.title = L(title)
        window.subtitle = source.lastPathComponent
        window.isReleasedWhenClosed = false
        window.level = .floating
        window.minSize = NSSize(width: 480, height: 580)
        window.delegate = self

        controls.orientation = .vertical
        controls.alignment = .leading
        controls.spacing = 10

        // macOS convention: secondary action on the left, Cancel / default action on the right.
        let reset = NSButton(title: L("Reset"), target: self, action: #selector(resetClicked))
        let cancel = NSButton(title: L("Cancel"), target: self, action: #selector(cancelClicked))
        cancel.keyEquivalent = "\u{1b}"
        applyButton = NSButton(title: L("Apply"), target: self, action: #selector(applyClicked))
        applyButton.keyEquivalent = "\r"
        for button in [reset, cancel, applyButton!] {
            button.bezelStyle = .push
            button.widthAnchor.constraint(greaterThanOrEqualToConstant: 80).isActive = true
        }
        let bar = NSStackView()
        bar.addView(reset, in: .leading)
        bar.addView(cancel, in: .trailing)
        bar.addView(applyButton, in: .trailing)

        // The canvas sits in a recessed well, like Preview's editing area.
        let well = NSBox()
        well.boxType = .custom
        well.cornerRadius = 10
        well.borderColor = .separatorColor
        well.borderWidth = 1
        well.fillColor = .underPageBackgroundColor
        well.contentViewMargins = .zero
        well.contentView = canvas
        well.translatesAutoresizingMaskIntoConstraints = false
        well.setContentHuggingPriority(.defaultLow, for: .vertical)
        well.setContentCompressionResistancePriority(.defaultLow, for: .vertical)

        let separator = NSBox()
        separator.boxType = .separator

        let root = NSStackView(views: [well, controls, separator, bar])
        root.orientation = .vertical
        root.alignment = .leading
        root.spacing = 16
        root.setCustomSpacing(12, after: separator)
        root.edgeInsets = NSEdgeInsets(top: 16, left: 20, bottom: 18, right: 20)
        root.translatesAutoresizingMaskIntoConstraints = false
        let content = NSView()
        content.addSubview(root)
        NSLayoutConstraint.activate([
            root.leadingAnchor.constraint(equalTo: content.leadingAnchor),
            root.trailingAnchor.constraint(equalTo: content.trailingAnchor),
            root.topAnchor.constraint(equalTo: content.topAnchor),
            root.bottomAnchor.constraint(equalTo: content.bottomAnchor),
            well.widthAnchor.constraint(equalTo: root.widthAnchor, constant: -40),
            controls.widthAnchor.constraint(equalTo: root.widthAnchor, constant: -40),
            separator.widthAnchor.constraint(equalTo: root.widthAnchor, constant: -40),
            bar.widthAnchor.constraint(equalTo: root.widthAnchor, constant: -40),
            well.heightAnchor.constraint(greaterThanOrEqualToConstant: 280),
        ])
        window.contentView = content
    }

    func present() async throws -> [URL] {
        try await withCheckedThrowingContinuation { continuation in
            self.continuation = continuation
            Editor.open.append(self)
            Editor.presented += 1
            window.center()
            NSApp.activate()
            window.makeKeyAndOrderFront(nil)
            update()
        }
    }

    // MARK: Overridable

    /// Saves the result and returns the written files.
    func apply() async throws -> [URL] { [] }
    func reset() {}
    /// Called whenever a control changes.
    func update() {}

    // MARK: Plumbing

    @objc private func cancelClicked() { window.close() }

    @objc private func resetClicked() {
        reset()
        update()
    }

    @objc private func applyClicked() {
        applyButton.isEnabled = false
        Task { @MainActor [self] in
            // Let the button redraw before a possibly slow full-resolution render.
            await Task.yield()
            do {
                finish(.success(try await apply()))
            } catch {
                applyButton.isEnabled = true
                NSAlert(error: error).beginSheetModal(for: window, completionHandler: nil)
            }
        }
    }

    @objc func controlChanged(_ sender: Any?) { update() }

    private func finish(_ result: Result<[URL], Error>) {
        guard let continuation else { return }
        self.continuation = nil
        window.delegate = nil
        window.close()
        Editor.open.removeAll { $0 === self }
        continuation.resume(with: result)
    }

    func windowWillClose(_ notification: Notification) { finish(.failure(Cancelled())) }

    // MARK: Control builders

    func addRow(_ title: String, _ views: NSView...) {
        // Right-aligned "Label:" column, as in System Settings and inspector panels.
        let label = NSTextField(labelWithString: title.isEmpty ? "" : L(title) + (Language.resolved == .en ? ":" : "："))
        label.alignment = .right
        label.widthAnchor.constraint(equalToConstant: 96).isActive = true
        let row = NSStackView(views: [label] + views)
        row.spacing = 8
        controls.addArrangedSubview(row)
        row.widthAnchor.constraint(equalTo: controls.widthAnchor).isActive = true
    }

    func slider(_ min: Double, _ max: Double, _ value: Double) -> NSSlider {
        let slider = NSSlider(value: value, minValue: min, maxValue: max, target: self, action: #selector(controlChanged))
        slider.isContinuous = true
        return slider
    }

    func segments(_ labels: [String]) -> NSSegmentedControl {
        let control = NSSegmentedControl(labels: labels.map { L($0) }, trackingMode: .selectOne, target: self,
                                         action: #selector(controlChanged))
        control.selectedSegment = 0
        return control
    }

    func popup(_ items: [String]) -> NSPopUpButton {
        let popup = NSPopUpButton(frame: .zero, pullsDown: false)
        popup.addItems(withTitles: items.map { L($0) })
        popup.target = self
        popup.action = #selector(controlChanged)
        return popup
    }

    func checkbox(_ title: String, _ on: Bool) -> NSButton {
        let box = NSButton(checkboxWithTitle: L(title), target: self, action: #selector(controlChanged))
        box.state = on ? .on : .off
        return box
    }

    func numberField(_ value: Int) -> NSTextField {
        let field = NSTextField(string: "\(value)")
        field.alignment = .center
        field.target = self
        field.action = #selector(controlChanged)
        field.widthAnchor.constraint(equalToConstant: 70).isActive = true
        let formatter = NumberFormatter()
        formatter.allowsFloats = false
        formatter.minimum = 1
        field.formatter = formatter
        return field
    }
}

/// Shows a CGImage aspect-fit with a soft shadow.
final class ImagePreview: NSView {
    var image: CGImage? { didSet { needsDisplay = true } }
    var overlay: ((NSRect) -> Void)?

    var imageRect: NSRect {
        guard let image else { return .zero }
        return fit(CGSize(width: image.width, height: image.height), in: bounds.insetBy(dx: 12, dy: 12))
    }

    override func draw(_ dirtyRect: NSRect) {
        guard let image else { return }
        let rect = imageRect
        NSGraphicsContext.saveGraphicsState()
        let shadow = NSShadow()
        shadow.shadowBlurRadius = 10
        shadow.shadowOffset = NSSize(width: 0, height: -3)
        shadow.shadowColor = NSColor.black.withAlphaComponent(0.25)
        shadow.set()
        NSGraphicsContext.current?.imageInterpolation = .high
        NSImage(cgImage: image, size: rect.size).draw(in: rect)
        NSGraphicsContext.restoreGraphicsState()
        overlay?(rect)
    }
}

func fit(_ size: CGSize, in bounds: NSRect) -> NSRect {
    guard size.width > 0, size.height > 0, bounds.width > 0, bounds.height > 0 else { return .zero }
    let scale = min(bounds.width / size.width, bounds.height / size.height)
    let w = size.width * scale, h = size.height * scale
    return NSRect(x: bounds.midX - w / 2, y: bounds.midY - h / 2, width: w, height: h)
}

func byteString(_ bytes: Int) -> String {
    ByteCountFormatter.string(fromByteCount: Int64(bytes), countStyle: .file)
}
