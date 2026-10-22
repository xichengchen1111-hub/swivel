import AppKit
import Darwin

/// `Swivel --mcp`: a Model Context Protocol server on stdin/stdout, so agents (Claude Desktop, Codex, Cursor,
/// Raycast…) can use Swivel's file tools on the paths they pass. JSON-RPC 2.0, one message per line, stdio only:
/// nothing listens on a port. The tools themselves are in `MCPTools`.
///
/// Speaks both eras of the spec: the 2025 handshake (`initialize`, then requests; `ping`) that current hosts use,
/// and the stateless 2026-07-28 revision (`server/discover`, the version and capabilities in every request's `_meta`).
@MainActor
final class MCPSession {
    static let legacyVersions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"]
    static let modernVersion = "2026-07-28"
    static let versionKey = "io.modelcontextprotocol/protocolVersion"
    static let capabilitiesKey = "io.modelcontextprotocol/clientCapabilities"
    static let serverInfoKey = "io.modelcontextprotocol/serverInfo"

    static var serverInfo: [String: Any] {
        ["name": "swivel", "title": "Swivel",
         "version": Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "development"]
    }
    static let instructions = """
        Swivel's file tools run locally on this Mac and only touch the absolute paths you pass. Call list_actions to \
        see what a file can become. Anything that writes takes two steps: a plan_* tool returns what would be created, \
        renamed or moved (no changes yet); show that to the user, then call apply_plan with its plan_id. New files go \
        next to the originals and never overwrite anything. Every applied plan is logged: undo reverses the latest \
        one (created files go to the Trash, renames and moves are put back) and history lists them.
        """

    let tools: MCPTools
    /// Writes one protocol message to the host.
    var send: ([String: Any]) -> Void
    /// Called when input has ended and nothing is running any more.
    var finished: () -> Void = {}
    /// Set by a 2025-style `initialize`; requests without `_meta` are then answered in that version.
    private(set) var legacyVersion: String?
    private var running: [String: Task<Void, Never>] = [:]
    private var cancelled: Set<String> = []
    private var inputEnded = false

    init(tools: MCPTools, send: @escaping ([String: Any]) -> Void) {
        self.tools = tools
        self.send = send
    }

    var busy: Bool { !running.isEmpty }

    /// One line from the host. Protocol requests are answered in order; tool calls run concurrently (a video export
    /// mustn't hold up a ping) and their answers go out as they finish.
    func receive(_ line: Data) {
        guard !line.allSatisfy({ $0 == 0x20 || $0 == 0x09 || $0 == 0x0D }) else { return }
        guard let object = try? JSONSerialization.jsonObject(with: line, options: [.fragmentsAllowed]) else {
            return send(Self.failure(nil, -32700, "Parse error"))
        }
        guard let message = object as? [String: Any] else {
            // JSON-RPC batches were removed from MCP in 2025-06-18.
            return send(Self.failure(nil, -32600, "Invalid request: send one JSON object per line"))
        }
        if message["method"] == nil, message["result"] != nil || message["error"] != nil { return }  // We send no requests.
        guard message["id"] != nil else { return notify(message) }
        guard let key = Self.key(message["id"]) else { return send(Self.failure(nil, -32600, "Invalid request id")) }
        guard running[key] == nil else { return send(Self.failure(message["id"], -32600, "Request id is already in use")) }
        switch prepare(message) {
        case .reply(let response): send(response)
        case .call(let name, let arguments, let modern):
            running[key] = Task { [weak self] in
                guard let self else { return }
                let response = Self.success(message["id"], Self.complete(await self.call(name, arguments), modern: modern))
                self.running[key] = nil
                if self.cancelled.remove(key) == nil { self.send(response) }
                self.checkFinished()
            }
        }
    }

    /// stdin closed: answer what's in flight, then stop.
    func endOfInput() {
        inputEnded = true
        checkFinished()
    }

    private func checkFinished() {
        if inputEnded && running.isEmpty { finished() }
    }

    private func notify(_ message: [String: Any]) {
        guard (message["method"] as? String) == "notifications/cancelled",
              let params = message["params"] as? [String: Any], let key = Self.key(params["requestId"]),
              let task = running[key] else { return }
        // After a cancellation nothing more is sent for that request.
        cancelled.insert(key)
        task.cancel()
    }

    /// The answer to one request (with an id), for tests and callers that don't need concurrency.
    func response(to message: [String: Any]) async -> [String: Any] {
        switch prepare(message) {
        case .reply(let response): return response
        case .call(let name, let arguments, let modern):
            return Self.success(message["id"], Self.complete(await call(name, arguments), modern: modern))
        }
    }

    enum Step {
        case reply([String: Any])
        case call(name: String, arguments: [String: Any], modern: Bool)
    }

    /// Everything but running a tool happens here, synchronously, so `initialize` takes effect before the next line.
    func prepare(_ message: [String: Any]) -> Step {
        let id = message["id"]
        guard (message["jsonrpc"] as? String) == "2.0", let method = message["method"] as? String else {
            return .reply(Self.failure(id, -32600, "Invalid request: expected jsonrpc 2.0 and a method"))
        }
        if message["params"] != nil, !(message["params"] is [String: Any]) {
            return .reply(Self.failure(id, -32602, "params must be an object"))
        }
        let params = message["params"] as? [String: Any] ?? [:]
        switch method {
        case "initialize":
            let requested = params["protocolVersion"] as? String ?? ""
            let version = Self.legacyVersions.contains(requested) ? requested : Self.legacyVersions[0]
            legacyVersion = version
            return .reply(Self.success(id, ["protocolVersion": version, "capabilities": ["tools": ["listChanged": false]],
                                            "serverInfo": Self.serverInfo, "instructions": Self.instructions]))
        case "ping":
            return .reply(Self.success(id, [:]))
        case "server/discover":
            return .reply(Self.success(id, ["resultType": "complete", "supportedVersions": [Self.modernVersion],
                                            "capabilities": ["tools": [String: Any]()], "instructions": Self.instructions,
                                            "ttlMs": 3_600_000, "cacheScope": "public",
                                            "_meta": [Self.serverInfoKey: Self.serverInfo]]))
        case "tools/list", "tools/call":
            let modern: Bool
            if let meta = params["_meta"] as? [String: Any], let version = meta[Self.versionKey] {
                guard (version as? String) == Self.modernVersion else {
                    return .reply(Self.failure(id, -32022, "Unsupported protocol version",
                                               data: ["supported": [Self.modernVersion] + Self.legacyVersions, "requested": version]))
                }
                guard meta[Self.capabilitiesKey] is [String: Any] else {
                    return .reply(Self.failure(id, -32602, "Missing \(Self.capabilitiesKey) in _meta"))
                }
                modern = true
            } else if legacyVersion != nil {
                modern = false
            } else {
                return .reply(Self.failure(id, -32602, "Send initialize first, or put \(Self.versionKey) in params._meta"))
            }
            if method == "tools/list" {
                if let cursor = params["cursor"], !(cursor is NSNull) {
                    return .reply(Self.failure(id, -32602, "Invalid cursor"))  // Everything fits on one page.
                }
                var result: [String: Any] = ["tools": MCPTools.definitions]
                if modern { result["ttlMs"] = 3_600_000; result["cacheScope"] = "public" }
                return .reply(Self.success(id, Self.complete(result, modern: modern)))
            }
            guard let name = params["name"] as? String else { return .reply(Self.failure(id, -32602, "Missing tool name")) }
            guard MCPTools.names.contains(name) else { return .reply(Self.failure(id, -32602, "Unknown tool: \(name)")) }
            if params["arguments"] != nil, !(params["arguments"] is [String: Any]) {
                return .reply(Self.failure(id, -32602, "arguments must be an object"))
            }
            return .call(name: name, arguments: params["arguments"] as? [String: Any] ?? [:], modern: modern)
        default:
            return .reply(Self.failure(id, -32601, "Method not found: \(method)"))
        }
    }

    private static func complete(_ result: [String: Any], modern: Bool) -> [String: Any] {
        guard modern else { return result }
        var result = result
        result["resultType"] = "complete"
        result["_meta"] = [serverInfoKey: serverInfo]
        return result
    }

    /// Runs a tool. Its failures are results with isError, so the model sees what went wrong and can fix it.
    private func call(_ name: String, _ arguments: [String: Any]) async -> [String: Any] {
        do {
            let structured = try await tools.call(name, arguments)
            return ["content": [["type": "text", "text": Self.text(structured)]], "structuredContent": structured, "isError": false]
        } catch {
            let message = error is CancellationError ? "Cancelled" : error.localizedDescription
            return ["content": [["type": "text", "text": message]], "isError": true]
        }
    }

    static func text(_ object: [String: Any]) -> String {
        guard JSONSerialization.isValidJSONObject(object),
              let data = try? JSONSerialization.data(withJSONObject: object, options: [.sortedKeys, .withoutEscapingSlashes]) else { return "{}" }
        return String(decoding: data, as: UTF8.self)
    }

    nonisolated static func success(_ id: Any?, _ result: [String: Any]) -> [String: Any] {
        ["jsonrpc": "2.0", "id": id ?? NSNull(), "result": result]
    }

    nonisolated static func failure(_ id: Any?, _ code: Int, _ message: String, data: Any? = nil) -> [String: Any] {
        var error: [String: Any] = ["code": code, "message": message]
        if let data { error["data"] = data }
        return ["jsonrpc": "2.0", "id": id ?? NSNull(), "error": error]
    }

    /// Request ids are strings or whole numbers, never null or booleans.
    static func key(_ id: Any?) -> String? {
        if let string = id as? String { return "s:" + string }
        if let number = id as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID() { return "n:" + number.stringValue }
        return nil
    }
}

/// The process side of `--mcp`: stdin/stdout framing, signals, shutdown.
enum MCPStdio {
    /// Exactly `Swivel --mcp`. Other arguments go to the developer-mode gate in main.swift as before.
    static func isRequested(_ arguments: [String]) -> Bool {
        arguments.count == 2 && arguments[1] == "--mcp"
    }

    @MainActor
    static func run() -> Never {
        // A host starts this process and is its parent. Launched through LaunchServices (`open`), launchd is the
        // parent and Swivel would act with its own privacy grants rather than the host's: refuse.
        guard getppid() != 1 else {
            FileHandle.standardError.write(Data("Swivel --mcp is started by an MCP host (Claude Desktop, Codex…), not opened as an app.\n".utf8))
            exit(64)
        }
        signal(SIGPIPE, SIG_IGN)
        // stdout carries protocol messages only. Keep a private copy for them and point fd 1 at stderr, so a stray
        // print or framework warning can't corrupt the stream.
        let protocolOutput = dup(STDOUT_FILENO)
        dup2(STDERR_FILENO, STDOUT_FILENO)
        _ = fcntl(protocolOutput, F_SETNOSIGPIPE, 1)
        let writer = MCPWriter(fd: protocolOutput)
        // Development builds can keep a separate undo log (manual sessions, tests); the installed app always uses its own.
        let directory = Bundle.main.bundleIdentifier == "local.swivel.app" ? nil
            : ProcessInfo.processInfo.environment["SWIVEL_MCP_HISTORY_DIR"].map { URL(fileURLWithPath: $0, isDirectory: true) }
        let session = MCPSession(tools: MCPTools(history: directory.map(MCPHistory.init) ?? MCPHistory())) { writer.write($0) }
        session.finished = { stop(0) }

        for number in [SIGTERM, SIGINT, SIGHUP] {
            signal(number, SIG_IGN)
            let source = DispatchSource.makeSignalSource(signal: number, queue: .main)
            source.setEventHandler { MainActor.assumeIsolated { stop(0) } }
            source.resume()
            signalSources.append(source)
        }
        Thread.detachNewThread {
            while let line = readLine(strippingNewline: true) {
                let data = Data(line.utf8)
                DispatchQueue.main.async { MainActor.assumeIsolated { session.receive(data) } }
            }
            DispatchQueue.main.async { MainActor.assumeIsolated { session.endOfInput() } }
        }
        // No NSApplication: creating one checks this process in with Launch Services as an instance of Swivel, and
        // then `open` (Finder, Dock, login items) only "activates" the running --mcp child instead of starting the
        // menu-bar app while a host keeps a server open. The main run loop also serves the main queue.
        while true { CFRunLoopRun() }
    }

    nonisolated(unsafe) private static var signalSources: [DispatchSourceSignal] = []

    /// Stops running tools and removes their staging files before exiting.
    @MainActor
    private static func stop(_ status: Int32) -> Never {
        TemporaryItems.removeAll()
        exit(status)
    }
}

/// Writes whole messages, one per line, from any thread.
final class MCPWriter: @unchecked Sendable {
    private let fd: Int32
    private let lock = NSLock()
    init(fd: Int32) { self.fd = fd }

    func write(_ message: [String: Any]) {
        // A value JSON can't hold (NaN, a URL) would raise rather than throw; the host still gets an answer.
        let message = JSONSerialization.isValidJSONObject(message) ? message
            : MCPSession.failure(message["id"], -32603, "Internal error: the result couldn't be encoded as JSON")
        // JSONSerialization escapes newlines inside strings, so a message is always a single line.
        guard var data = try? JSONSerialization.data(withJSONObject: message, options: [.withoutEscapingSlashes]) else { return }
        data.append(0x0A)
        lock.lock(); defer { lock.unlock() }
        data.withUnsafeBytes { buffer in
            var offset = 0
            while offset < buffer.count {
                let written = Darwin.write(fd, buffer.baseAddress! + offset, buffer.count - offset)
                if written > 0 { offset += written } else if written < 0, errno == EINTR { continue } else { return }
            }
        }
    }
}
