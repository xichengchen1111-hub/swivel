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

    let window: NSWindow
    let source: URL
    let controls = NSStackView()
    private var continuation: CheckedContinuation<[URL], Error>?
    private var applyButton: NSButton!

    init(title: String, source: URL, canvas: NSView) {
        self.source = source
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 560, height: 680),
                          styleMask: [.titled, .closable, .resizable, .fullSizeContentView],
                          backing: .buffered, defer: false)
        super.init()
        window.title = title
        window.subtitle = source.lastPathComponent
        window.titlebarAppearsTransparent = true
        window.isReleasedWhenClosed = false
        window.level = .floating
        window.minSize = NSSize(width: 460, height: 560)
        window.delegate = self

        let background = NSVisualEffectView()
        background.material = .popover
        background.blendingMode = .behindWindow
        background.state = .active

        controls.orientation = .vertical
        controls.alignment = .leading
        controls.spacing = 10

        let reset = NSButton(title: "Reset", target: self, action: #selector(resetClicked))
        reset.bezelStyle = .push
        applyButton = NSButton(title: "Apply", target: self, action: #selector(applyClicked))
        applyButton.bezelStyle = .push
        applyButton.keyEquivalent = "\r"
        applyButton.bezelColor = .systemOrange
        let bar = NSStackView()
        bar.addView(reset, in: .leading)
        bar.addView(applyButton, in: .trailing)

        canvas.translatesAutoresizingMaskIntoConstraints = false
        canvas.setContentHuggingPriority(.defaultLow, for: .vertical)
        canvas.setContentCompressionResistancePriority(.defaultLow, for: .vertical)
        let root = NSStackView(views: [canvas, controls, bar])
        root.orientation = .vertical
        root.alignment = .leading
        root.spacing = 16
        root.edgeInsets = NSEdgeInsets(top: 40, left: 20, bottom: 18, right: 20)
        root.translatesAutoresizingMaskIntoConstraints = false
        background.addSubview(root)
        NSLayoutConstraint.activate([
            root.leadingAnchor.constraint(equalTo: background.leadingAnchor),
            root.trailingAnchor.constraint(equalTo: background.trailingAnchor),
            root.topAnchor.constraint(equalTo: background.topAnchor),
            root.bottomAnchor.constraint(equalTo: background.bottomAnchor),
            canvas.widthAnchor.constraint(equalTo: root.widthAnchor, constant: -40),
            controls.widthAnchor.constraint(equalTo: root.widthAnchor, constant: -40),
            bar.widthAnchor.constraint(equalTo: root.widthAnchor, constant: -40),
            canvas.heightAnchor.constraint(greaterThanOrEqualToConstant: 260),
        ])
        window.contentView = background
    }

    func present() async throws -> [URL] {
        try await withCheckedThrowingContinuation { continuation in
            self.continuation = continuation
            Editor.open.append(self)
            window.center()
            NSApp.activate()
            window.makeKeyAndOrderFront(nil)
            update()
        }
    }

    // MARK: Overridable

    /// Saves the result and returns the written files.
    func apply() throws -> [URL] { [] }
    func reset() {}
    /// Called whenever a control changes.
    func update() {}

    // MARK: Plumbing

    @objc private func resetClicked() {
        reset()
        update()
    }

    @objc private func applyClicked() {
        applyButton.isEnabled = false
        // Let the button redraw before a possibly slow full-resolution render.
        DispatchQueue.main.async { [self] in
            do {
                finish(.success(try apply()))
            } catch {
                applyButton.isEnabled = true
                NSAlert(error: error).beginSheetModal(for: window)
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
        let label = NSTextField(labelWithString: title)
        label.textColor = .secondaryLabelColor
        label.widthAnchor.constraint(equalToConstant: 92).isActive = true
        let row = NSStackView(views: [label] + views)
        row.spacing = 8
        controls.addArrangedSubview(row)
        row.widthAnchor.constraint(equalTo: controls.widthAnchor).isActive = true
    }

    func slider(_ min: Double, _ max: Double, _ value: Double) -> NSSlider {
        let slider = NSSlider(value: value, minValue: min, maxValue: max, target: self, action: #selector(controlChanged))
        slider.isContinuous = true
        slider.trackFillColor = .systemOrange
        return slider
    }

    func segments(_ labels: [String]) -> NSSegmentedControl {
        let control = NSSegmentedControl(labels: labels, trackingMode: .selectOne, target: self,
                                         action: #selector(controlChanged))
        control.selectedSegment = 0
        control.selectedSegmentBezelColor = .systemOrange
        return control
    }

    func popup(_ items: [String]) -> NSPopUpButton {
        let popup = NSPopUpButton(frame: .zero, pullsDown: false)
        popup.addItems(withTitles: items)
        popup.target = self
        popup.action = #selector(controlChanged)
        return popup
    }

    func checkbox(_ title: String, _ on: Bool) -> NSButton {
        let box = NSButton(checkboxWithTitle: title, target: self, action: #selector(controlChanged))
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
