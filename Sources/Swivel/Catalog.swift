import AppKit
import UniformTypeIdentifiers

enum FileKind: String, CaseIterable {
    case image, video, audio, pdf, document, archive, folder, other

    var title: String {
        switch self {
        case .image: "Images"
        case .video: "Video"
        case .audio: "Audio"
        case .pdf: "PDF"
        case .document: "Documents"
        case .archive: "Archives"
        case .folder: "Folders"
        case .other: "Other files"
        }
    }

    var symbol: String {
        switch self {
        case .image: "photo"
        case .video: "film"
        case .audio: "music.note"
        case .pdf: "doc.richtext"
        case .document: "doc.text"
        case .archive: "archivebox"
        case .folder: "folder"
        case .other: "doc"
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
        if ["md", "markdown", "rtfd", "webarchive"].contains(ext) { return .document }
        if (try? url.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true { return .folder }
        guard let type = UTType(filenameExtension: ext) else { return .other }
        if type.conforms(to: .pdf) { return .pdf }
        if type.conforms(to: .image) { return .image }
        if type.conforms(to: .movie) { return .video }
        if type.conforms(to: .audio) { return .audio }
        if type.conforms(to: .archive) || ["zip", "tar", "tgz", "gz"].contains(ext) { return .archive }
        if Docs.readableExtensions.contains(ext) { return .document }
        return .other
    }

    static func of(_ urls: [URL]) -> FileKind {
        let kinds = Set(urls.map(of))
        return kinds.count == 1 ? kinds.first! : .other
    }
}

/// Decides which wheel items a drag gets, per file kind and key.
enum Catalog {
    /// The menu for a drag, in the user's order with hidden items removed.
    @MainActor
    static func items(for urls: [URL], mode: WheelMode) -> [WheelItem] {
        Prefs.arrange(defaultItems(for: urls, mode: mode), mode, FileKind.of(urls))
    }

    /// Everything a menu can offer for a kind of file, used by the Customize settings.
    static func allItems(mode: WheelMode, kind: FileKind) -> [String] {
        defaultItems(for: kind.sampleURLs, mode: mode).map(\.title)
    }

    static func defaultItems(for urls: [URL], mode: WheelMode) -> [WheelItem] {
        guard !urls.isEmpty else { return [] }
        let kind = FileKind.of(urls)
        let sourceExt = Set(urls.map { $0.pathExtension.lowercased() })
        let multi = urls.count > 1

        switch mode {
        case .actions:
            var list = actions(multi: multi)
            if kind == .image {
                list.insert(WheelItem(title: "Hang") { urls in
                    Clothesline.shared.hang(urls)
                    return ActionResult(message: urls.count == 1 ? "Hung on the clothesline" : "Hung \(urls.count) images on the clothesline")
                }, at: 1)
            }
            return list

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
                return [each("Extract", urls, Archives.extract)]
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
                    each("Redact", urls) { try await RedactEditor(source: $0).present() },
                    each("Metadata", urls) { try await MetadataEditor(source: $0).present() },
                    each("Annotate", urls) { try await AnnotateEditor(source: $0).present() },
                ]
                var list = editors + Images.toolItems.map { option in each(option.title, urls, option.run) }
                    + [WheelItem(title: "Make PDF") { saved(try await Images.makePDF($0)) }]
                if multi { list.insert(WheelItem(title: "Collage") { saved(try await CollageEditor(urls: $0).present()) }, at: 0) }
                return list
            case .video:
                var list = [each("Trim", urls) { try await TrimEditor(source: $0).present() }]
                    + Video.toolItems.map { option in each(option.title, urls, option.run) }
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
        case 1: ActionResult(message: "Saved \(files[0].lastPathComponent)", files: files)
        default: ActionResult(message: "Saved \(files.count) files", files: files)
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
                Shelf.shared.add(urls)
                return ActionResult(message: "Added to Shelf")
            },
            WheelItem(title: "Copy") { urls in
                NSPasteboard.general.clearContents()
                NSPasteboard.general.writeObjects(urls as [NSURL])
                return ActionResult(message: multi ? "Copied \(urls.count) files" : "Copied \(urls[0].lastPathComponent)")
            },
            WheelItem(title: "Move") { urls in
                let moved = try FileActions.move(urls)
                return moved.isEmpty ? ActionResult(message: "Move cancelled") : saved(moved).renamed("Moved")
            },
            WheelItem(title: "Copy Path") { urls in
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(urls.map(\.path).joined(separator: "\n"), forType: .string)
                return ActionResult(message: multi ? "Copied \(urls.count) paths" : "Copied path")
            },
            WheelItem(title: "Terminal") { urls in
                try await FileActions.openTerminal(at: urls[0])
                return ActionResult(message: "Opened in Terminal")
            },
        ]
    }
}

private extension ActionResult {
    func renamed(_ verb: String) -> ActionResult {
        var copy = self
        copy.message = files.count == 1 ? "\(verb) \(files[0].lastPathComponent)" : "\(verb) \(files.count) files"
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
        panel.prompt = "Move Here"
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
        let fm = FileManager.default
        guard fm.fileExists(atPath: url.path) else { return url }
        let dir = url.deletingLastPathComponent()
        let ext = url.pathExtension
        let base = url.deletingPathExtension().lastPathComponent
        var n = 2
        while true {
            var candidate = dir.appendingPathComponent("\(base) \(n)")
            if !ext.isEmpty { candidate.appendPathExtension(ext) }
            if !fm.fileExists(atPath: candidate.path) { return candidate }
            n += 1
        }
    }
}

enum Shell {
    /// Runs a command-line tool and throws its stderr on failure.
    static func run(_ tool: String, _ args: [String], in directory: URL? = nil) async throws {
        try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
            let process = Process()
            process.executableURL = URL(fileURLWithPath: tool)
            process.arguments = args
            if let directory { process.currentDirectoryURL = directory }
            let errors = Pipe()
            process.standardError = errors
            process.standardOutput = FileHandle.nullDevice
            process.terminationHandler = { p in
                let data = errors.fileHandleForReading.readDataToEndOfFile()
                if p.terminationStatus == 0 {
                    cont.resume()
                } else {
                    let text = String(decoding: data, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
                    let name = URL(fileURLWithPath: tool).lastPathComponent
                    cont.resume(throwing: ToolError(text.isEmpty ? "\(name) failed" : "\(name): \(text)"))
                }
            }
            do { try process.run() } catch { cont.resume(throwing: error) }
        }
    }
}
