import AppKit
import UniformTypeIdentifiers

struct FileAppTarget: Codable, Equatable, Identifiable, Sendable {
    enum Delivery: String, Codable, CaseIterable, Sendable {
        case openFiles, codexDraft, claudeCodeDraft, copyFiles
        var title: String {
            switch self {
            case .openFiles: L("Open files")
            case .codexDraft: L("Codex draft")
            case .claudeCodeDraft: L("Claude Code draft")
            case .copyFiles: L("Copy files and open app")
            }
        }
    }
    let id: String
    var name: String
    var bundleID: String
    var appPath: String
    var delivery: Delivery
    var extensions: [String] = []
    var enabled: Bool = true
    var actionID: String { "app." + id }
    var isValid: Bool { !id.isEmpty && !name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && name.count <= 80 }
    func accepts(_ urls: [URL]) -> Bool {
        !urls.isEmpty && urls.allSatisfy { $0.isFileURL && (extensions.isEmpty || extensions.contains($0.pathExtension.lowercased())) }
    }
    static let starters: [Self] = [
        Self(id: "codex", name: "Codex", bundleID: "", appPath: "", delivery: .codexDraft),
        Self(id: "claude-code", name: "Claude Code", bundleID: "com.anthropic.claudefordesktop", appPath: "", delivery: .claudeCodeDraft),
    ]
    static func normalizedExtensions(_ text: String) -> [String] {
        var seen = Set<String>()
        return text.lowercased().split(whereSeparator: { $0 == "," || $0 == "，" || $0.isWhitespace }).map {
            String($0).trimmingCharacters(in: CharacterSet(charactersIn: "."))
        }.filter { !$0.isEmpty && $0.allSatisfy { $0.isLetter || $0.isNumber } && seen.insert($0).inserted }
    }
}

enum AssistantDraft {
    static func referenceText(_ urls: [URL]) -> String {
        // JSON strings keep quotes, newlines and unusual names unambiguous. File content is never read here.
        let paths = urls.map { url -> String in
            let bytes = try? JSONEncoder().encode(url.path)
            return bytes.flatMap { String(data: $0, encoding: .utf8) } ?? url.path
        }
        return L("Selected local files:") + "\n" + paths.map { "- " + $0 }.joined(separator: "\n")
    }
    static func url(for delivery: FileAppTarget.Delivery, files: [URL]) throws -> URL {
        guard !files.isEmpty, files.allSatisfy(\.isFileURL), [.codexDraft, .claudeCodeDraft].contains(delivery) else {
            throw ToolError(L("Choose files for this draft."))
        }
        let text = referenceText(files)
        guard text.utf8.count <= 12000 else { throw ToolError(L("Too many file paths for one draft. Choose fewer files.")) }
        var components = URLComponents()
        components.scheme = delivery == .codexDraft ? "codex" : "claude"
        components.host = delivery == .codexDraft ? "threads" : "code"
        components.path = "/new"
        // Use an explicit workspace only when all files are in the same directory. No broad common-root grant.
        let directories = Set(files.map { url in
            (try? url.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true ? url : url.deletingLastPathComponent()
        })
        components.queryItems = [URLQueryItem(name: delivery == .codexDraft ? "prompt" : "q", value: text)]
        if directories.count == 1, let directory = directories.first {
            components.queryItems?.append(URLQueryItem(name: delivery == .codexDraft ? "path" : "folder", value: directory.path))
        }
        guard let url = components.url else { throw ToolError(L("Could not prepare the draft.")) }
        return url
    }
}

@MainActor
enum FileAppRouting {
    private static func supports(_ url: URL, delivery: FileAppTarget.Delivery) -> Bool {
        guard let bundle = Bundle(url: url), url.pathExtension == "app" else { return false }
        guard [.codexDraft, .claudeCodeDraft].contains(delivery) else { return true }
        let scheme = delivery == .codexDraft ? "codex" : "claude"
        let types = bundle.object(forInfoDictionaryKey: "CFBundleURLTypes") as? [[String: Any]] ?? []
        return types.contains { ($0["CFBundleURLSchemes"] as? [String] ?? []).contains(scheme) }
    }
    static func application(for target: FileAppTarget) -> URL? {
        if !target.appPath.isEmpty, let bundle = Bundle(url: URL(fileURLWithPath: target.appPath)),
           supports(bundle.bundleURL, delivery: target.delivery), bundle.bundleIdentifier == target.bundleID { return bundle.bundleURL }
        if !target.bundleID.isEmpty, let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: target.bundleID), supports(url, delivery: target.delivery) { return url }
        guard target.appPath.isEmpty else { return nil }
        let scheme = target.delivery == .codexDraft ? "codex://threads/new" : target.delivery == .claudeCodeDraft ? "claude://code/new" : ""
        return URL(string: scheme).flatMap { NSWorkspace.shared.urlForApplication(toOpen: $0) }
    }

    static func run(_ target: FileAppTarget, files: [URL], pasteboard: NSPasteboard = .general,
                    open: (([URL], URL) async throws -> Void)? = nil) async throws -> ActionResult {
        guard Feature.appRouting.isOn, target.enabled, Prefs.fileAppTargets.contains(target), target.accepts(files) else {
            throw ToolError(L("This app action is unavailable or turned off."))
        }
        for file in files { _ = try FileIdentity.read(file) }
        guard let application = application(for: target) else { throw ToolError(L("%@ is not installed. Choose its application in App Actions.", target.name)) }
        let urls: [URL]
        switch target.delivery {
        case .codexDraft, .claudeCodeDraft: urls = [try AssistantDraft.url(for: target.delivery, files: files)]
        case .copyFiles: urls = []
        case .openFiles: urls = files
        }
        let opener = open ?? openWithWorkspace
        try await opener(urls, application)
        if target.delivery == .copyFiles {
            pasteboard.clearContents()
            guard pasteboard.writeObjects(files as [NSURL]) else { throw ToolError(L("Could not copy the selected files.")) }
        }
        let message: String
        switch target.delivery {
        case .codexDraft, .claudeCodeDraft: message = L("Opened %@ draft with file references. Review and send there.", target.name)
        case .copyFiles: message = L("Files copied. Paste them in %@.", target.name)
        case .openFiles: message = L("Opened files in %@", target.name)
        }
        return ActionResult(message: message)
    }

    private static func openWithWorkspace(_ urls: [URL], _ app: URL) async throws {
        let config = NSWorkspace.OpenConfiguration(); config.activates = true
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            if urls.isEmpty {
                NSWorkspace.shared.openApplication(at: app, configuration: config) { _, error in
                    if let error { continuation.resume(throwing: error) } else { continuation.resume() }
                }
            } else {
                NSWorkspace.shared.open(urls, withApplicationAt: app, configuration: config) { _, error in
                    if let error { continuation.resume(throwing: error) } else { continuation.resume() }
                }
            }
        }
    }
}
