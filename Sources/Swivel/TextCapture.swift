import AppKit
import Vision

enum TextRecognition {
    /// Input is an existing user-selected image. No screen capture is performed.
    static func recognize(_ url: URL) async throws -> String {
        let work = Task.detached(priority: .userInitiated) {
            try Task.checkCancellation()
            let image = try Images.load(url, maxPixels: 8000)
            let request = VNRecognizeTextRequest()
            request.recognitionLevel = .accurate
            request.usesLanguageCorrection = true
            request.automaticallyDetectsLanguage = true
            let supported = try request.supportedRecognitionLanguages()
            request.recognitionLanguages = ["en-US", "zh-Hans", "zh-Hant", "ja-JP"].filter(supported.contains)
            try VNImageRequestHandler(cgImage: image).perform([request])
            try Task.checkCancellation()
            return (request.results ?? []).compactMap { $0.topCandidates(1).first?.string }.joined(separator: "\n")
        }
        return try await withTaskCancellationHandler(operation: { try await work.value }, onCancel: { work.cancel() })
    }
}

@MainActor
final class TextCaptureWindow: NSObject, NSWindowDelegate, NSTextViewDelegate {
    private static var open: [TextCaptureWindow] = []
    let window: NSWindow
    private let text = NSTextView()
    private let status = ToolUI.note("Recognizing text…")
    private let progress = NSProgressIndicator()
    private var copyButton: NSButton!
    private var task: Task<Void, Never>?

    static func present(_ url: URL) {
        guard Feature.textCapture.isOn else { return }
        let controller = TextCaptureWindow(url)
        open.append(controller)
        ToolUI.show(controller.window)
        controller.recognize(url)
    }

    static func closeAll() { for controller in open { controller.window.close() } }

    private init(_ url: URL) {
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 780, height: 540),
                          styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        super.init()
        window.title = L("Text Capture")
        window.subtitle = url.lastPathComponent
        window.toolbarStyle = .unified
        window.minSize = NSSize(width: 640, height: 420)
        window.isReleasedWhenClosed = false
        window.delegate = self
        window.center()

        let preview = NSImageView()
        if let image = try? Images.load(url, maxPixels: 1800) {
            preview.image = NSImage(cgImage: image, size: NSSize(width: image.width, height: image.height))
        }
        preview.imageScaling = .scaleProportionallyUpOrDown
        preview.setAccessibilityLabel(L("Source image"))
        let well = NSBox()
        well.boxType = .custom
        well.borderColor = .separatorColor
        well.fillColor = .underPageBackgroundColor
        well.cornerRadius = 8
        well.contentView = preview

        text.font = .systemFont(ofSize: 15)
        text.isRichText = false
        text.allowsUndo = true
        text.isEditable = false
        text.delegate = self
        text.textContainerInset = NSSize(width: 14, height: 14)
        text.isVerticallyResizable = true
        text.isHorizontallyResizable = false
        text.autoresizingMask = [.width]
        text.textContainer?.widthTracksTextView = true
        text.setAccessibilityLabel(L("Recognized text"))
        let scroll = NSScrollView()
        scroll.documentView = text
        scroll.hasVerticalScroller = true
        scroll.borderType = .bezelBorder
        let columns = ToolUI.stack([well, scroll], vertical: false, spacing: 16)
        columns.distribution = .fillEqually
        columns.alignment = .height

        progress.style = .spinning
        progress.controlSize = .small
        progress.isDisplayedWhenStopped = false
        progress.startAnimation(nil)
        copyButton = ToolUI.button("Copy Text", target: self, action: #selector(copyText))
        copyButton.isEnabled = false
        copyButton.keyEquivalent = "\r"
        let close = ToolUI.button("Close", target: self, action: #selector(closeWindow))
        close.keyEquivalent = "\u{1b}"
        let footer = NSStackView()
        footer.addView(ToolUI.stack([progress, status], vertical: false, spacing: 8), in: .leading)
        footer.addView(close, in: .trailing)
        footer.addView(copyButton, in: .trailing)
        let note = ToolUI.note("Review the recognized text before copying. You can edit it here.")
        let root = ToolUI.stack([columns, note, footer])
        let content = NSView()
        window.contentView = content
        ToolUI.embed(root, in: content)
        NSLayoutConstraint.activate([
            columns.widthAnchor.constraint(equalTo: root.widthAnchor),
            footer.widthAnchor.constraint(equalTo: root.widthAnchor),
            note.widthAnchor.constraint(equalTo: root.widthAnchor),
            columns.heightAnchor.constraint(greaterThanOrEqualToConstant: 260),
        ])
        columns.setContentHuggingPriority(.defaultLow, for: .vertical)
    }

    private func recognize(_ url: URL) {
        task = Task { [weak self] in
            do {
                let value = try await TextRecognition.recognize(url)
                guard !Task.isCancelled, let self else { return }
                text.string = value
                text.isEditable = true
                status.stringValue = value.isEmpty ? L("No text found in this image.") : L("Ready to copy")
                progress.stopAnimation(nil)
                updateCopy()
            } catch {
                guard !Task.isCancelled, let self else { return }
                progress.stopAnimation(nil)
                status.stringValue = L("Could not recognize text: %@", error.localizedDescription)
            }
        }
    }

    func textDidChange(_ notification: Notification) { updateCopy() }
    private func updateCopy() { copyButton.isEnabled = !text.string.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }

    @objc private func copyText() {
        guard Feature.textCapture.isOn, copyButton.isEnabled else { return }
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text.string, forType: .string)
        status.stringValue = L("Text copied")
    }
    @objc private func closeWindow() { window.close() }

    func windowWillClose(_ notification: Notification) {
        task?.cancel()
        Self.open.removeAll { $0 === self }
        ToolUI.didClose()
    }
}
