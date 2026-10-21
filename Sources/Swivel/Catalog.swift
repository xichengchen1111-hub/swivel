import AppKit
import UniformTypeIdentifiers

enum FileKind: String, CaseIterable {
    case image, video, audio, pdf, document, archive, folder, other

    var title: String {
        switch self {
        case .image: L("Images")
        case .video: L("Video")
        case .audio: L("Audio")
        case .pdf: L("PDF")
        case .document: L("Documents")
        case .archive: L("Archives")
        case .folder: L("Folders")
        case .other: L("Other files")
        }
    }

    var symbol: String {
        switch self {
        case .image: L("photo")
        case .video: L("film")
        case .audio: L("music.note")
        case .pdf: L("doc.richtext")
        case .document: L("doc.text")
        case .archive: L("archivebox")
        case .folder: L("folder")
        case .other: L("doc")
        }
    }

    /// A made-up file of this kind, for listing a menu's default contents.
    var sampleURLs: [URL] {
        let names: [String] = switch self {
        case .image: ["a.jpg", "b.jpg"]
        case .video: ["a.mov", "b.mov"]
        case .audio: ["a.mp3", "b.mp3"]
        case .pdf: ["a.pdf", "b.pdf"]
        case .document: ["a.docx"]
        case .archive: ["a.zip"]
        case .folder, .other: ["a.unknownext"]
        }
        return names.map { URL(fileURLWithPath: "/nonexistent/\($0)") }
    }

    static func of(_ url: URL) -> FileKind {
        let ext = url.pathExtension.lowercased()
        if ["md", "markdown", "rtfd"].contains(ext) { return .document }
        if (try? url.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true { return .folder }
        // Disk images conform to "archive" but can't be unpacked with tar; macOS opens them itself.
        if ext == "dmg" || ext == "sparseimage" || ext == "sparsebundle" { return .other }
        // RAR and 7-Zip have no system type unless an app declares one; old-style RAR parts are .r00, .r01…
        if Archives.extractableExtensions.contains(ext) || ext.wholeMatch(of: #/r\d\d/#) != nil { return .archive }
        guard let type = UTType(filenameExtension: ext) else { return .other }
        if type.conforms(to: .pdf) { return .pdf }
        if type.conforms(to: .image) { return .image }
        if type.conforms(to: .movie) { return .video }
        if type.conforms(to: .audio) { return .audio }
        if type.conforms(to: .archive) { return .archive }
        if Docs.readableExtensions.contains(ext) { return .document }
        return .other
    }

    static func of(_ urls: [URL]) -> FileKind {
        let kinds = Set(urls.map(of))
        return kinds.count == 1 ? kinds.first! : .other
    }
}

/// Decides which wheel items a drag gets, per file kind and key.
@MainActor
enum Catalog {
    private static var codeCache: [SavedQRCode]?
    private static var codeObserver: NSObjectProtocol?
    static func savedCodes() -> [SavedQRCode] {
        if codeObserver == nil {
            codeObserver = NotificationCenter.default.addObserver(forName: QRLibraryStore.changed, object: nil, queue: .main) { _ in
                MainActor.assumeIsolated { codeCache = nil }
            }
        }
        if codeCache == nil { codeCache = (try? QRLibraryStore().load()) ?? [] }
        return codeCache ?? []
    }
    static func displayTitle(_ id: String) -> String {
        if id == "Arrange PDF Pages" { return L("PDF Pages") }
        if id == "Read QR Codes" { return L("Scan QR") }
        if id == "Float" { return L("Add to Shelf") }
        if id.hasPrefix("qr."), let entry = savedCodes().first(where: { "qr." + $0.id.uuidString == id }) { return entry.name }
        if id.hasPrefix("app."), let target = Prefs.fileAppTargets.first(where: { $0.actionID == id }) {
            return target.name
        }
        if id.hasPrefix("preset."), let preset = Prefs.imagePresets.first(where: { $0.actionID == id }) {
            return preset.displayName
        }
        return L(id)
    }

    static func standaloneItems(for urls: [URL], mode: WheelMode) -> [WheelItem] {
        defaultItems(for: urls, mode: mode).filter {
            ["Extract Text", "Float", "Hang", "Batch Rename", "Arrange PDF Pages", "Read QR Codes", "QR Library"].contains($0.title) || $0.title.hasPrefix("preset.") || $0.title.hasPrefix("app.") || $0.title.hasPrefix("qr.")
        }
    }
    /// The menu for a drag, in the user's order with hidden items removed.
    @MainActor
    static func items(for urls: [URL], mode: WheelMode) -> [WheelItem] {
        Prefs.arrange(defaultItems(for: urls, mode: mode), mode, FileKind.of(urls))
    }

    /// Everything a menu can offer for a kind of file, used by the Customize settings.
    static func allItems(mode: WheelMode, kind: FileKind) -> [String] {
        if mode == .apps {
            guard Feature.appRouting.isOn else { return [] }
            // Customize represents a whole category, not just its sample .jpg/.docx file.
            return Prefs.fileAppTargets.filter { target in
                target.enabled && (target.extensions.isEmpty || target.extensions.contains { ext in
                    FileKind.of(URL(fileURLWithPath: "/nonexistent/file." + ext)) == kind
                })
            }.map(\.actionID)
        }
        let multi = defaultItems(for: kind.sampleURLs, mode: mode).map(\.title)
        let single = defaultItems(for: Array(kind.sampleURLs.prefix(1)), mode: mode).map(\.title)
        return multi + single.filter { !multi.contains($0) }
    }

    static func defaultItems(for urls: [URL], mode: WheelMode) -> [WheelItem] {
        if mode == .codes {
            guard Feature.qrCodes.isOn else { return [] }
            return [WheelItem(title: "QR Library") { _ in
                guard Feature.qrCodes.isOn else { throw ToolError(L("This feature is turned off.")) }
                QRLibraryWindow().show(); return ActionResult(message: "")
            }] + savedCodes().map { entry in
                WheelItem(title: "qr." + entry.id.uuidString) { _ in
                    guard Feature.qrCodes.isOn else { throw ToolError(L("This feature is turned off.")) }
                    guard try QRLibraryStore().load().contains(where: { $0.id == entry.id }) else { throw ToolError(L("This saved code was removed. Open the QR Library to refresh.")) }
                    QRLibraryWindow(select: entry.id).show(); return ActionResult(message: "")
                }
            }
        }
        if mode == .apps {
            guard Feature.appRouting.isOn else { return [] }
            return Prefs.fileAppTargets.filter { $0.enabled && (urls.isEmpty || $0.accepts(urls)) }.map { target in
                WheelItem(title: target.actionID) { files in
                    let selected = files.isEmpty ? ToolUI.chooseFiles(folders: true) : files
                    guard !selected.isEmpty else { throw Cancelled() }
                    return try await FileAppRouting.run(target, files: selected)
                }
            }
        }
        guard !urls.isEmpty else { return [] }
        let kind = FileKind.of(urls)
        let sourceExt = Set(urls.map { $0.pathExtension.lowercased() })
        let multi = urls.count > 1

        switch mode {
        case .apps, .codes: return [] // Handled above, including no-file shortcut wheels.
        case .actions:
            var list = actions(multi: multi)
            if Feature.batchRename.isOn {
                list.append(WheelItem(title: "Batch Rename") { files in
                    guard Feature.batchRename.isOn else { throw ToolError(L("This feature is turned off.")) }
                    BatchRenameWindow(files).show(); return ActionResult(message: "")
                })
            }
            if kind == .image && Feature.clothesline.isOn {
                list.insert(WheelItem(title: "Hang") { urls in
                    guard Feature.clothesline.isOn else { throw ToolError(L("This feature is turned off.")) }
                    Clothesline.shared.hang(urls)
                    return ActionResult(message: urls.count == 1 ? L("Hung on the clothesline") : L("Hung %d images on the clothesline", urls.count))
                }, at: 1)
            }
            return Feature.shelf.isOn ? list : list.filter { $0.title != "Float" }

        case .convert:
            switch kind {
            case .image:
                var list = Images.outputFormats
                    .filter { !$0.extensions.contains(where: sourceExt.contains) || multi }
                    .map { format in each(format.name, urls) { try await Images.convert($0, to: format) } }
                list.append(WheelItem(title: "PDF") { urls in
                    saved(try await Images.makePDF(urls))
                })
                return list
            case .video:
                return Video.convertItems(sourceExt: sourceExt).map { option in each(option.title, urls, option.run) }
            case .audio:
                return Audio.formats
                    .filter { !sourceExt.contains($0.ext) || $0.name == "ALAC" }
                    .map { format in each(format.name, urls) { try await Audio.convert($0, to: format) } }
            case .pdf:
                return PDFs.convertItems.map { option in each(option.title, urls, option.run) }
            case .document:
                return Docs.formats
                    .filter { !sourceExt.contains($0.ext) }
                    .map { format in each(format.name, urls) { try await Docs.convert($0, to: format) } }
            case .archive:
                return [WheelItem(title: "Extract") { urls in
                    // Dropping every part of a multi-part RAR extracts it once.
                    var seen: Set<URL> = [], outputs: [URL] = []
                    for url in urls {
                        let first = (try? Archives.volumes(of: url).first) ?? url
                        guard seen.insert(first.standardizedFileURL).inserted else { continue }
                        outputs += try await Archives.extract(url)
                    }
                    return saved(outputs)
                }]
            case .folder, .other:
                return Archives.compressItems
            }

        case .tools:
            switch kind {
            case .image:
                let editors: [WheelItem] = [
                    each("Crop", urls) { try await CropEditor(source: $0).present() },
                    each("Compress", urls) { try await CompressEditor(source: $0).present() },
                    each("Edit", urls) { try await AdjustEditor(source: $0).present() },
                    each("Add BG", urls) { try await BackgroundEditor(source: $0).present() },
                    each("Remove BG", urls) { try await Cutout.removeBackground($0) },
                    each("Redact", urls) { try await RedactEditor(source: $0).present() },
                    each("Metadata", urls) { try await MetadataEditor(source: $0).present() },
                    each("Annotate", urls) { try await AnnotateEditor(source: $0).present() },
                ]
                var list = editors + Images.toolItems.map { option in each(option.title, urls, option.run) }
                    + [WheelItem(title: "Make PDF") { saved(try await Images.makePDF($0)) }]
                if Feature.qrCodes.isOn {
                    list.insert(WheelItem(title: "Read QR Codes") { files in
                        guard Feature.qrCodes.isOn else { throw ToolError(L("This feature is turned off.")) }
                        QRCodesWindow(files).show(); return ActionResult(message: "")
                    }, at: 0)
                }
                if Feature.textCapture.isOn && !multi {
                    list.insert(WheelItem(title: "Extract Text") { urls in
                        guard Feature.textCapture.isOn, let url = urls.first else { throw ToolError(L("This feature is turned off.")) }
                        TextCaptureWindow.present(url)
                        // The window opening is the feedback; no toast.
                        return ActionResult(message: "")
                    }, at: 0)
                }
                if Feature.imagePresets.isOn {
                    list += Prefs.imagePresets.map { preset in
                        WheelItem(title: preset.actionID) { try await PresetActions.run(preset, urls: $0) }
                    }
                }
                if multi { list.insert(WheelItem(title: "Collage") { saved(try await CollageEditor(urls: $0).present()) }, at: 0) }
                return list
            case .video:
                var list = [each("Trim", urls) { try await TrimEditor(source: $0).present() }]
                    + Video.toolItems.map { option in
                        option.title == "Compress" ? WheelItem(title: "Compress") { try await Video.compress($0) }
                            : each(option.title, urls, option.run)
                    }
                if multi { list.insert(WheelItem(title: "Merge") { saved([try await Video.merge($0, audioOnly: false)]) }, at: 0) }
                return list
            case .audio:
                var list = [each("Trim", urls) { try await TrimEditor(source: $0).present() }]
                    + Audio.toolItems.map { option in each(option.title, urls, option.run) }
                if multi { list.insert(WheelItem(title: "Merge") { saved([try await Video.merge($0, audioOnly: true)]) }, at: 0) }
                return list
            case .pdf:
                var list = [each("Crop", urls) { try await CropEditor(source: $0).present() }]
                    + PDFs.toolItems.map { option in each(option.title, urls, option.run) }
                if Feature.pdfPages.isOn {
                    list.insert(WheelItem(title: "Arrange PDF Pages") { files in
                        guard Feature.pdfPages.isOn else { throw ToolError(L("This feature is turned off.")) }
                        PDFPagesWindow(files).show(); return ActionResult(message: "")
                    }, at: 0)
                }
                if multi { list.insert(WheelItem(title: "Merge") { saved([try await PDFs.merge($0)]) }, at: 0) }
                return list
            case .document:
                return [each("Word Count", urls, Docs.wordCount)]
            case .archive, .folder, .other:
                return Archives.compressItems
            }
        }
    }

    /// An item that runs `work` on each file in turn and collects the outputs.
    static func each(_ title: String, _ urls: [URL],
                     _ work: @escaping @MainActor (URL) async throws -> [URL]) -> WheelItem {
        WheelItem(title: title) { urls in
            var outputs: [URL] = []
            for url in urls { outputs += try await work(url) }
            return saved(outputs)
        }
    }

    static func saved(_ files: [URL]) -> ActionResult {
        switch files.count {
        case 0: ActionResult(message: "Done")
        case 1: ActionResult(message: L("Saved %@", files[0].lastPathComponent), files: files)
        default: ActionResult(message: L("Saved %d files", files.count), files: files)
        }
    }

    private static func actions(multi: Bool) -> [WheelItem] {
        [
            WheelItem(title: "AirDrop") { urls in
                guard let service = NSSharingService(named: .sendViaAirDrop), service.canPerform(withItems: urls) else {
                    throw ToolError("AirDrop isn't available")
                }
                NSApp.activate()
                service.perform(withItems: urls)
                return ActionResult(message: "Opened AirDrop")
            },
            WheelItem(title: "Float") { urls in
                guard Feature.shelf.isOn else { throw ToolError(L("This feature is turned off.")) }
                Shelf.shared.add(urls)
                return ActionResult(message: "Added to Shelf")
            },
            WheelItem(title: "Copy") { urls in
                NSPasteboard.general.clearContents()
                NSPasteboard.general.writeObjects(urls as [NSURL])
                return ActionResult(message: multi ? L("Copied %d files", urls.count) : L("Copied %@", urls[0].lastPathComponent))
            },
            WheelItem(title: "Move") { urls in
                let moved = try FileActions.move(urls)
                return moved.isEmpty ? ActionResult(message: L("Move cancelled")) : saved(moved).renamed()
            },
            WheelItem(title: "Copy Path") { urls in
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(urls.map(\.path).joined(separator: "\n"), forType: .string)
                return ActionResult(message: multi ? L("Copied %d paths", urls.count) : L("Copied path"))
            },
            WheelItem(title: "Terminal") { urls in
                try await FileActions.openTerminal(at: urls[0])
                return ActionResult(message: "Opened in Terminal")
            },
        ]
    }
}

private extension ActionResult {
    func renamed() -> ActionResult {
        var copy = self
        copy.message = files.count == 1 ? L("Moved %@", files[0].lastPathComponent) : L("Moved %d files", files.count)
        return copy
    }
}

/// A titled per-file operation offered on a wheel.
struct FileOption {
    let title: String
    let run: @MainActor (URL) async throws -> [URL]
}

struct ToolError: LocalizedError {
    let message: String
    init(_ message: String) { self.message = message }
    var errorDescription: String? { message }
}

enum FileActions {
    @MainActor
    static func move(_ urls: [URL]) throws -> [URL] {
        NSApp.activate()
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.canCreateDirectories = true
        panel.prompt = L("Move Here")
        panel.message = "Choose where to move \(urls.count == 1 ? urls[0].lastPathComponent : "\(urls.count) files")"
        guard panel.runModal() == .OK, let folder = panel.url else { return [] }
        return try urls.map { url in
            let destination = Paths.unique(folder.appendingPathComponent(url.lastPathComponent))
            try FileManager.default.moveItem(at: url, to: destination)
            return destination
        }
    }

    @MainActor
    static func openTerminal(at url: URL) async throws {
        let isFolder = (try? url.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true
        let folder = isFolder ? url : url.deletingLastPathComponent()
        let ws = NSWorkspace.shared
        guard let app = ws.urlForApplication(withBundleIdentifier: "com.googlecode.iterm2")
                ?? ws.urlForApplication(withBundleIdentifier: "com.apple.Terminal") else {
            throw ToolError("Terminal not found")
        }
        try await ws.open([folder], withApplicationAt: app, configuration: .init())
    }
}

enum Paths {
    /// `folder/name.ext`, or `name 2.ext`, `name 3.ext`… if taken.
    static func output(for source: URL, ext: String, suffix: String = "") -> URL {
        let base = source.deletingPathExtension().lastPathComponent + suffix
        let dir = source.deletingLastPathComponent()
        return unique(dir.appendingPathComponent(base).appendingPathExtension(ext))
    }

    static func unique(_ url: URL) -> URL {
        guard taken(url) else { return url }
        let dir = url.deletingLastPathComponent()
        let ext = url.pathExtension
        let base = url.deletingPathExtension().lastPathComponent
        var n = 2
        while true {
            var candidate = dir.appendingPathComponent("\(base) \(n)")
            if !ext.isEmpty { candidate.appendPathExtension(ext) }
            if !taken(candidate) { return candidate }
            n += 1
        }
    }

    /// Anything at the path counts, a symlink included. `fileExists` follows links, so a dangling one planted under
    /// the predicted output name (for example inside a downloaded archive) would look free and be written through.
    static func taken(_ url: URL) -> Bool {
        var info = stat()
        return lstat(url.path, &info) == 0
    }
}

/// Hidden working folders and files (extraction staging, encodes in progress) to clean up if Swivel quits first.
enum TemporaryItems {
    private static let lock = NSLock()
    nonisolated(unsafe) private static var items: Set<URL> = []
    static func register(_ url: URL) { lock.lock(); items.insert(url); lock.unlock() }
    static func unregister(_ url: URL) { lock.lock(); items.remove(url); lock.unlock() }
    /// At quit: stops running tools first, so nothing writes into a folder while it's removed.
    static func removeAll() {
        Shell.stopAll()
        lock.lock(); let all = items; items = []; lock.unlock()
        for url in all {
            Archives.makeAccessible(url)
            try? FileManager.default.removeItem(at: url)
        }
    }
}

enum Shell {
    /// Stops every running tool (Swivel is quitting): children would otherwise keep writing after it's gone.
    static func stopAll() { ShellRun.stopAll() }

    /// Runs a command-line tool and throws its stderr on failure.
    ///
    /// The tool reads `input` files in order (or an empty stdin), so it never waits on a terminal. Its stderr is
    /// drained while it runs: a tool that writes more than a pipe's 64 KB would otherwise block forever. `watch` is
    /// asked twice a second; returning a message stops the tool and throws that message. Cancelling the task stops it.
    static func run(_ tool: String, _ args: [String], in directory: URL? = nil, input: [URL] = [],
                    watch: (@Sendable () -> String?)? = nil) async throws {
        let run = ShellRun(tool: tool, args: args, directory: directory, input: input, watch: watch)
        try await withTaskCancellationHandler {
            try await run.start()
        } onCancel: {
            run.stop(L("Cancelled"))
        }
    }
}

/// One tool run for `Shell.run`; its state is shared with Foundation's callback threads behind a lock.
private final class ShellRun: @unchecked Sendable {
    private let process = Process()
    private let tool: String
    private let input: [URL]
    private let watch: (@Sendable () -> String?)?
    private let lock = NSLock()
    private var errorTail = Data()
    private var stopReason: String?
    private var timer: DispatchSourceTimer?
    private static let keptErrorBytes = 16_384

    init(tool: String, args: [String], directory: URL?, input: [URL], watch: (@Sendable () -> String?)?) {
        self.tool = tool
        self.input = input
        self.watch = watch
        process.executableURL = URL(fileURLWithPath: tool)
        process.arguments = args
        if let directory { process.currentDirectoryURL = directory }
    }

    private static let registry = NSLock()
    nonisolated(unsafe) private static var running: [ObjectIdentifier: ShellRun] = [:]
    static func stopAll() {
        let all = registry.withLock { Array(running.values) }
        for run in all { run.stop(L("Cancelled")) }
    }
    private static func track(_ run: ShellRun, _ active: Bool) {
        registry.withLock { running[ObjectIdentifier(run)] = active ? run : nil }
    }

    func start() async throws {
        Self.track(self, true)
        defer { Self.track(self, false) }
        try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
            let errors = Pipe()
            process.standardError = errors
            process.standardOutput = FileHandle.nullDevice
            let feed = input.isEmpty ? nil : Pipe()
            process.standardInput = feed ?? FileHandle.nullDevice
            errors.fileHandleForReading.readabilityHandler = { [weak self] handle in
                let data = handle.availableData
                if data.isEmpty { handle.readabilityHandler = nil } else { self?.keep(data) }
            }
            process.terminationHandler = { [self] p in
                errors.fileHandleForReading.readabilityHandler = nil
                keep(errors.fileHandleForReading.readDataToEndOfFile())
                lock.lock()
                timer?.cancel()
                let reason = stopReason, text = String(decoding: errorTail, as: UTF8.self)
                lock.unlock()
                if let reason { return cont.resume(throwing: ToolError(reason)) }
                if p.terminationStatus == 0 { return cont.resume() }
                let message = text.trimmingCharacters(in: .whitespacesAndNewlines)
                let name = URL(fileURLWithPath: tool).lastPathComponent
                cont.resume(throwing: ToolError(message.isEmpty ? "\(name) failed" : "\(name): \(message)"))
            }
            do { try process.run() } catch {
                errors.fileHandleForReading.readabilityHandler = nil
                return cont.resume(throwing: error)
            }
            lock.lock(); let stoppedEarly = stopReason != nil; lock.unlock()
            if stoppedEarly { process.terminate() }
            if let feed { send(input, to: feed.fileHandleForWriting) }
            if let watch {
                let timer = DispatchSource.makeTimerSource(queue: .global(qos: .utility))
                timer.schedule(deadline: .now() + 0.5, repeating: 0.5)
                timer.setEventHandler { [weak self] in if let message = watch() { self?.stop(message) } }
                lock.lock(); self.timer = timer; lock.unlock()
                timer.resume()
            }
        }
    }

    /// Stops the tool; its run then throws `reason` (the first reason given wins).
    func stop(_ reason: String) {
        lock.lock()
        if stopReason == nil { stopReason = reason }
        lock.unlock()
        if process.isRunning { process.terminate() }
    }

    private func keep(_ data: Data) {
        guard !data.isEmpty else { return }
        lock.lock()
        errorTail.append(data)
        if errorTail.count > Self.keptErrorBytes { errorTail = errorTail.suffix(Self.keptErrorBytes) }
        lock.unlock()
    }

    /// Streams the files into the tool's stdin on a background thread. A tool that exits early just ends the copy:
    /// the write end ignores SIGPIPE, so the write fails instead of killing Swivel.
    private func send(_ files: [URL], to handle: FileHandle) {
        _ = fcntl(handle.fileDescriptor, F_SETNOSIGPIPE, 1)
        Thread.detachNewThread {
            defer { try? handle.close() }
            for file in files {
                guard let reader = try? FileHandle(forReadingFrom: file) else { return }
                defer { try? reader.close() }
                while let chunk = try? reader.read(upToCount: 1 << 20), !chunk.isEmpty {
                    do { try handle.write(contentsOf: chunk) } catch { return }
                }
            }
        }
    }
}
