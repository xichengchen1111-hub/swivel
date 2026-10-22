import AppKit
import AVFoundation
import CoreImage
import CoreText
import Speech

struct TimedWord: Equatable, Sendable {
    var text: String
    var start: Double
    var end: Double
}
struct SubtitleCue: Equatable, Sendable {
    var start: Double
    var end: Double
    var text: String
}

enum Subtitles {
    static func timestamp(_ seconds: Double) -> String {
        let ms = Int64((min(359999.999, max(0, seconds.isFinite ? seconds : 0)) * 1000).rounded())
        return String(format: "%02lld:%02lld:%02lld,%03lld", ms / 3600000, ms / 60000 % 60, ms / 1000 % 60, ms % 1000)
    }
    static func wrap(_ text: String, limit: Int = 42) -> String {
        var lines: [String] = [], line = ""
        for char in text {
            if char == "\n" { lines.append(line); line = ""; continue }
            if line.count >= limit { lines.append(line); line = "" }
            if !line.isEmpty || !char.isWhitespace { line.append(char) }
        }
        if !line.isEmpty { lines.append(line) }
        return lines.joined(separator: "\n")
    }
    static func cues(_ words: [TimedWord]) -> [SubtitleCue] {
        let words = words.filter { $0.start.isFinite && $0.end.isFinite && $0.end > max(0, $0.start) && !$0.text.isEmpty }
            .sorted { $0.start < $1.start }
        var cues: [SubtitleCue] = [], start = 0.0, end = 0.0, text = ""
        func flush() {
            guard !text.isEmpty else { return }
            let previous = cues.last?.end ?? 0
            let a = max(previous, start)
            if end > a { cues.append(SubtitleCue(start: a, end: end, text: wrap(text.trimmingCharacters(in: .whitespacesAndNewlines)))) }
            text = ""
        }
        for word in words {
            // Long runs (including a CJK attributed run) are split by grapheme, with proportional timestamps.
            let chars = Array(word.text)
            let pieces = max(1, Int(ceil(max(Double(chars.count) / 42, (word.end - word.start) / 7))))
            for piece in 0..<pieces {
                let a = chars.count * piece / pieces, b = chars.count * (piece + 1) / pieces
                guard b > a else { continue }
                let first = word.start + (word.end - word.start) * Double(piece) / Double(pieces)
                let last = word.start + (word.end - word.start) * Double(piece + 1) / Double(pieces)
                if !text.isEmpty && (first - end > 0.6 || last - start > 7 || text.count + b - a > 84) { flush() }
                if text.isEmpty { start = max(0, first) }
                let fragment = String(chars[a..<b])
                if let previous = text.last, let first = fragment.first,
                   previous.isASCII, previous.isLetter || previous.isNumber,
                   first.isASCII, first.isLetter || first.isNumber { text += " " }
                text += fragment; end = last
                if let c = text.last, ".!?。！？".contains(c) { flush() }
            }
        }
        flush()
        return cues
    }
    static func srt(_ cues: [SubtitleCue]) -> String {
        cues.enumerated().map { "\($0.offset + 1)\n\(timestamp($0.element.start)) --> \(timestamp($0.element.end))\n\($0.element.text)\n" }.joined(separator: "\n")
    }
    static func read(_ url: URL) throws -> [SubtitleCue] {
        let size = try url.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? Int.max
        guard size <= 2_000_000 else { throw ToolError(L("This subtitle file is too large.")) }
        let text = try String(contentsOf: url, encoding: .utf8).replacingOccurrences(of: "\r\n", with: "\n")
        func time(_ text: Substring) -> Double? {
            let values = text.trimmingCharacters(in: .whitespaces).replacingOccurrences(of: ",", with: ".").split(separator: ":")
            guard values.count == 3, let h = Double(values[0]), let m = Double(values[1]), let s = Double(values[2]),
                  h >= 0, h < 100, m >= 0, m < 60, s >= 0, s < 60 else { return nil }
            return h * 3600 + m * 60 + s
        }
        var output: [SubtitleCue] = []
        for block in text.components(separatedBy: "\n\n") {
            let lines = block.split(separator: "\n", omittingEmptySubsequences: false)
            guard let index = lines.firstIndex(where: { $0.contains(" --> ") }) else { continue }
            let pair = lines[index].components(separatedBy: " --> ")
            guard pair.count == 2, let a = time(Substring(pair[0])), let b = time(Substring(pair[1])), b > a else { continue }
            let content = lines.dropFirst(index + 1).joined(separator: "\n")
            guard !content.isEmpty else { continue }
            output.append(SubtitleCue(start: a, end: b, text: wrap(String(content.prefix(2000)))))
        }
        return output.sorted { $0.start < $1.start }
    }

    private static let cache = NSCache<NSString, CGImage>()
    static func overlay(_ cue: SubtitleCue, size: CGSize) -> CIImage? {
        let key = "\(Int(size.width))x\(Int(size.height)):\(cue.text)" as NSString
        if let image = cache.object(forKey: key) { return CIImage(cgImage: image) }
        let w = Int(size.width), h = Int(size.height)
        guard w > 0, h > 0 else { return nil }
        var fontSize = max(10, min(size.width * 0.034, size.height * 0.055))
        let lines = Array(wrap(cue.text).split(separator: "\n").prefix(3))
        // A 42-character CJK line is wider than an English one. Fit the actual glyphs to the safe width.
        let measuringFont = CTFontCreateUIFontForLanguage(.system, fontSize, nil)!
        let widest = lines.map {
            let line = CTLineCreateWithAttributedString(NSAttributedString(string: String($0), attributes: [
                NSAttributedString.Key(kCTFontAttributeName as String): measuringFont]))
            return CTLineGetTypographicBounds(line, nil, nil, nil)
        }.max() ?? 0
        if widest > size.width * 0.9 { fontSize *= size.width * 0.9 / widest }
        let lineHeight = fontSize * 1.35
        let height = min(h, Int(ceil(lineHeight * Double(lines.count) + fontSize)))
        guard let ctx = CGContext(data: nil, width: w, height: height, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        let font = CTFontCreateUIFontForLanguage(.system, fontSize, nil)!
        let attrs: [NSAttributedString.Key: Any] = [NSAttributedString.Key(kCTFontAttributeName as String): font,
            NSAttributedString.Key(kCTForegroundColorAttributeName as String): CGColor(gray: 1, alpha: 1)]
        let textLines = lines.map { CTLineCreateWithAttributedString(NSAttributedString(string: String($0), attributes: attrs)) }
        let textWidth = textLines.map { CTLineGetTypographicBounds($0, nil, nil, nil) }.max() ?? 0
        let plateWidth = min(Double(w), textWidth + fontSize * 1.2)
        ctx.setFillColor(CGColor(gray: 0, alpha: 0.6))
        ctx.addPath(CGPath(roundedRect: CGRect(x: (Double(w) - plateWidth) / 2, y: 0, width: plateWidth, height: Double(height)),
                          cornerWidth: fontSize * 0.25, cornerHeight: fontSize * 0.25, transform: nil))
        ctx.fillPath()
        for (index, line) in textLines.enumerated() {
            let width = CTLineGetTypographicBounds(line, nil, nil, nil)
            ctx.textPosition = CGPoint(x: (Double(w) - width) / 2,
                                      y: Double(height) - fontSize * 1.2 - Double(index) * lineHeight)
            CTLineDraw(line, ctx)
        }
        guard let image = ctx.makeImage() else { return nil }
        cache.countLimit = 64
        cache.setObject(image, forKey: key)
        return CIImage(cgImage: image)
    }
}

@available(macOS 26, *)
enum LocalTranscription {
    static func recognize(_ source: URL, locale: Locale) async throws -> [TimedWord] {
        guard let locale = await SpeechTranscriber.supportedLocale(equivalentTo: locale) else {
            throw ToolError(L("This transcription language is not supported on this Mac."))
        }
        let transcriber = SpeechTranscriber(locale: locale, transcriptionOptions: [], reportingOptions: [], attributeOptions: [.audioTimeRange])
        // A reservation makes already downloaded resources visible to this process. false means already reserved.
        _ = try await AssetInventory.reserve(locale: locale)
        // This service never downloads implicitly. Only the editor's explicit consent path can install resources.
        guard await AssetInventory.status(forModules: [transcriber]) == .installed else {
            throw ToolError(L("Language resources are not installed."))
        }
        let asset = try await Video.safeAsset(source)
        guard try await !asset.loadTracks(withMediaType: .audio).isEmpty else { throw ToolError(L("This file has no audio.")) }
        let preferredTemporary = Paths.temporary(beside: source, ext: "m4a")
        defer { Paths.discard(preferredTemporary) }
        guard let export = AVAssetExportSession(asset: asset, presetName: AVAssetExportPresetAppleM4A) else {
            throw ToolError(L("Couldn't encode audio."))
        }
        let temporary = try await Paths.produce(preferredTemporary) { target in
            try await withTaskCancellationHandler {
                try Task.checkCancellation()
                try await export.export(to: target, as: .m4a)
                try Task.checkCancellation()
            } onCancel: { export.cancelExport() }
        }
        defer { Paths.discard(temporary) }
        let audio = try AVAudioFile(forReading: temporary)
        let analyzer = SpeechAnalyzer(modules: [transcriber])
        let consumer = Task { () throws -> [TimedWord] in
            var words: [TimedWord] = []
            for try await result in transcriber.results where result.isFinal {
                for run in result.text.runs {
                    let range = run.audioTimeRange ?? result.range
                    words.append(TimedWord(text: String(result.text[run.range].characters), start: range.start.seconds, end: range.end.seconds))
                }
            }
            return words
        }
        return try await withTaskCancellationHandler {
            do {
                try await analyzer.start(inputAudioFile: audio, finishAfterFile: true)
                let words = try await consumer.value
                try Task.checkCancellation()
                return words
            } catch {
                await analyzer.cancelAndFinishNow(); consumer.cancel(); throw error
            }
        } onCancel: { consumer.cancel(); Task { await analyzer.cancelAndFinishNow() } }
    }
}

@available(macOS 26, *)
final class TranscriptionEditor: Editor {
    private let message = NSTextField(wrappingLabelWithString: L("Transcription stays on this Mac. Open Trim to burn an SRT file into a video."))
    private var language: NSPopUpButton!
    private var extraSRT: NSButton!
    private let progress = NSProgressIndicator()
    private var observation: NSKeyValueObservation?
    private var task: Task<[URL], Error>?
    private let isVideo: Bool

    init(source: URL, isVideo: Bool) {
        self.isVideo = isVideo
        let canvas = NSView()
        super.init(title: isVideo ? "Subtitles" : "Transcribe", source: source, canvas: canvas)
        message.translatesAutoresizingMaskIntoConstraints = false
        canvas.addSubview(message)
        NSLayoutConstraint.activate([message.centerYAnchor.constraint(equalTo: canvas.centerYAnchor),
            message.leadingAnchor.constraint(equalTo: canvas.leadingAnchor, constant: 24),
            message.trailingAnchor.constraint(equalTo: canvas.trailingAnchor, constant: -24)])
        language = popup(Prefs.transcriptionLanguageTitles)
        language.selectItem(at: Prefs.transcriptionLanguages.firstIndex(of: Prefs.transcriptionLanguage) ?? 0)
        addRow("Transcription Language", language)
        extraSRT = checkbox("Also save SRT", false)
        if !isVideo { addRow("", extraSRT) }
        progress.isIndeterminate = false
        progress.minValue = 0; progress.maxValue = 1
        progress.widthAnchor.constraint(equalToConstant: 240).isActive = true
        addRow("", progress)
    }
    static func run(_ sources: [URL], isVideo: Bool) async throws -> ActionResult {
        var outputs: [URL] = []
        for source in sources { outputs += try await TranscriptionEditor(source: source, isVideo: isVideo).present() }
        var result = Catalog.saved(outputs)
        if isVideo { result.message += "\n" + L("Open Trim → Subtitles to burn them into the video.") }
        return result
    }
    override var canCancelSaving: Bool { true }
    override func cancelSaving() { task?.cancel() }
    override func didClose() { task?.cancel(); observation?.invalidate() }
    override func apply() async throws -> [URL] {
        Prefs.transcriptionLanguage = Prefs.transcriptionLanguages[max(0, language.indexOfSelectedItem)]
        let locale = Prefs.transcriptionLocale
        guard let locale = await SpeechTranscriber.supportedLocale(equivalentTo: locale) else {
            throw ToolError(L("This transcription language is not supported on this Mac."))
        }
        let module = SpeechTranscriber(locale: locale, preset: .transcription)
        let source = self.source, isVideo = self.isVideo, extra = extraSRT.state == .on
        let task = Task { [self] () throws -> [URL] in
            _ = try await AssetInventory.reserve(locale: locale)
            if await AssetInventory.status(forModules: [module]) != .installed {
                let alert = NSAlert()
                alert.messageText = L("Download language resources?")
                alert.informativeText = L("Apple needs to download this language once. Only system resources are downloaded; your audio stays on this Mac.")
                alert.addButton(withTitle: L("Download")); alert.addButton(withTitle: L("Cancel"))
                let answer = await withCheckedContinuation { continuation in
                    alert.beginSheetModal(for: window) { continuation.resume(returning: $0) }
                }
                guard answer == .alertFirstButtonReturn else { throw Cancelled() }
                try Task.checkCancellation()
                if let request = try await AssetInventory.assetInstallationRequest(supporting: [module]) {
                    observation = request.progress.observe(\.fractionCompleted, options: [.initial, .new]) { [weak self] p, _ in
                        let value = p.fractionCompleted
                        Task { @MainActor in self?.progress.doubleValue = value }
                    }
                    message.stringValue = L("Downloading language resources…")
                    try await withTaskCancellationHandler {
                        try await request.downloadAndInstall()
                    } onCancel: { request.progress.cancel() }
                }
            }
            try Task.checkCancellation()
            message.stringValue = L("Transcribing on this Mac…")
            let words = try await LocalTranscription.recognize(source, locale: locale)
            guard !words.isEmpty else { throw ToolError(L("No speech was found.")) }
            try Task.checkCancellation()
            var outputs: [URL] = []
            do {
                if !isVideo {
                    outputs.append(try Paths.create(Data(words.map(\.text).joined().utf8), at: Paths.preferred(for: source, ext: "txt")))
                }
                if isVideo || extra {
                    outputs.append(try Paths.create(Data(Subtitles.srt(Subtitles.cues(words)).utf8), at: Paths.preferred(for: source, ext: "srt")))
                }
                return outputs
            } catch {
                for output in outputs { try? FileManager.default.removeItem(at: output) }
                throw error
            }
        }
        self.task = task
        defer { self.task = nil; observation?.invalidate(); observation = nil }
        return try await task.value
    }
}
