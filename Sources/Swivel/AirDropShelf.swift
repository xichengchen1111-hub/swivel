import AppKit
import Darwin

/// Puts files AirDrop saves into Downloads on the File Shelf as well. Only the folder's own entries are listed
/// (no file is opened or read); a file counts as AirDrop when its quarantine record names `sharingd` and was made
/// after watching began. Off by default: the first look at Downloads makes macOS ask for access.
@MainActor
final class AirDropShelf {
    static let shared = AirDropShelf()

    struct Entry: Hashable, Sendable { let device: dev_t; let inode: ino_t }
    struct Snapshot: Sendable { let size: off_t; let modified: timespec }

    private let folder: URL
    private let deliver: @MainActor ([URL]) -> Void
    private let openDirectory: @Sendable (URL) -> Int32
    private var opening: Task<Void, Never>?
    private var openingRequest: Startup?

    private final class Startup: @unchecked Sendable {
        private let lock = NSLock()
        private var cancelled = false
        func cancel() { lock.lock(); cancelled = true; lock.unlock() }
        var isCancelled: Bool { lock.lock(); defer { lock.unlock() }; return cancelled }
    }
    private var source: DispatchSourceFileSystemObject?
    private var observer: NSObjectProtocol?
    private var known: Set<Entry> = []
    /// Arrivals waiting for their size and date to stop changing (AirDrop may still be writing).
    private var waiting: [Entry: (url: URL, snapshot: Snapshot)] = [:]
    private var started = Date()
    private var scan: Task<Void, Never>?
    private var generation = 0
    /// At most one check is waiting and one listing is running. Events that arrive meanwhile fold into a single
    /// follow-up, so a busy Downloads folder can't start a recheck loop per event.
    private var checkScheduled = false
    private var scanning = false
    private var rescan = false
    /// Listings taken so far, for tests.
    private(set) var scans = 0
    private let queue = DispatchQueue(label: "local.swivel.airdrop-shelf")

    init(folder: URL = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Downloads"),
         openDirectory: @escaping @Sendable (URL) -> Int32 = { open($0.path, O_EVTONLY | O_DIRECTORY | O_CLOEXEC) },
         deliver: @escaping @MainActor ([URL]) -> Void = { Shelf.shared.add($0) }) {
        self.folder = folder
        self.deliver = deliver
        self.openDirectory = openDirectory
    }

    deinit { openingRequest?.cancel() }

    var isWatching: Bool { source != nil }

    /// Starts or stops watching to match the File Shelf switch and its AirDrop option.
    func sync() {
        if observer == nil {
            observer = NotificationCenter.default.addObserver(forName: Prefs.changed, object: nil, queue: .main) { _ in
                MainActor.assumeIsolated { AirDropShelf.shared.sync() }
            }
        }
        if Feature.shelf.isOn && Prefs.shelfAirDrop { start() } else { stop() }
    }

    func start() {
        guard source == nil, opening == nil else { return }
        generation += 1
        let generation = self.generation, folder = self.folder, queue = self.queue, opener = openDirectory
        started = Date()
        waiting = [:]
        let request = Startup()
        openingRequest = request
        // Opening Downloads can wait on privacy consent or its file provider. Never hold the UI thread.
        // Take the baseline before creating a source: stop/deinit can never release a suspended source.
        opening = Task { [weak self] in
            let baseline = await Self.openBaseline(folder, on: queue, opener: opener, request: request)
            guard let self, self.generation == generation else {
                if baseline.fd >= 0 { close(baseline.fd) }
                return
            }
            self.opening = nil
            self.openingRequest = nil
            guard baseline.fd >= 0 else { return }
            self.known = Set(baseline.entries.map(\.entry))
            let fd = baseline.fd
            let source = DispatchSource.makeFileSystemObjectSource(fileDescriptor: fd, eventMask: [.write, .rename, .delete], queue: .main)
            source.setEventHandler { [weak self] in MainActor.assumeIsolated { self?.schedule(after: 0.4) } }
            source.setCancelHandler { close(fd) }
            self.source = source
            source.resume()
            self.schedule(after: 0.4) // cover arrivals between the baseline and installing the event source
        }
    }

    private nonisolated static func openBaseline(_ folder: URL, on queue: DispatchQueue,
                                                 opener: @escaping @Sendable (URL) -> Int32, request: Startup) async -> (fd: Int32, entries: Listing) {
        await withCheckedContinuation { continuation in
            queue.async {
                guard !request.isCancelled else { continuation.resume(returning: (-1, [])); return }
                let fd = opener(folder)
                guard !request.isCancelled else {
                    if fd >= 0 { close(fd) }
                    continuation.resume(returning: (-1, [])); return
                }
                continuation.resume(returning: (fd, fd >= 0 ? entries(folder) : []))
            }
        }
    }

    func stop() {
        generation += 1
        openingRequest?.cancel(); openingRequest = nil
        opening?.cancel(); opening = nil
        scan?.cancel(); scan = nil
        source?.cancel(); source = nil
        known = []; waiting = [:]
        checkScheduled = false; scanning = false; rescan = false
    }

    private func schedule(after delay: Double) {
        guard !checkScheduled else { return }
        checkScheduled = true
        let generation = self.generation
        DispatchQueue.main.asyncAfter(deadline: .now() + delay) { [weak self] in
            MainActor.assumeIsolated {
                guard let self, self.generation == generation else { return }
                self.checkScheduled = false
                self.check()
            }
        }
    }

    private func check() {
        guard source != nil else { return }
        guard !scanning else { rescan = true; return }
        scanning = true
        scans += 1
        let folder = self.folder, generation = self.generation, started = self.started
        scan = Task { [weak self] in
            let entries = await Self.list(folder, on: self?.queue ?? .global())
            guard let self, self.generation == generation else { return }
            self.scanning = false
            var ready: [URL] = []
            for item in entries where !self.known.contains(item.entry) {
                if let previous = self.waiting[item.entry], previous.snapshot.size == item.snapshot.size,
                   previous.snapshot.modified.tv_sec == item.snapshot.modified.tv_sec,
                   previous.snapshot.modified.tv_nsec == item.snapshot.modified.tv_nsec {
                    self.waiting[item.entry] = nil
                    self.known.insert(item.entry)
                    if Self.isAirDrop(item.url, since: started) { ready.append(item.url) }
                } else {
                    self.waiting[item.entry] = (item.url, item.snapshot)
                }
            }
            // Names that disappeared while waiting are forgotten.
            let present = Set(entries.map(\.entry))
            self.waiting = self.waiting.filter { present.contains($0.key) }
            if !self.waiting.isEmpty || self.rescan {
                self.schedule(after: self.waiting.isEmpty ? 0.4 : 1)
                self.rescan = false
            }
            if !ready.isEmpty { self.deliver(ready) }
        }
    }

    private typealias Listing = [(entry: Entry, url: URL, snapshot: Snapshot)]

    /// Visible top-level entries with their identity, size and modification date (lstat: links are not followed).
    nonisolated static func list(_ folder: URL, on queue: DispatchQueue) async -> [(entry: Entry, url: URL, snapshot: Snapshot)] {
        await withCheckedContinuation { continuation in
            queue.async { continuation.resume(returning: entries(folder)) }
        }
    }

    private nonisolated static func entries(_ folder: URL) -> Listing {
        let names = (try? FileManager.default.contentsOfDirectory(atPath: folder.path)) ?? []
        var result: Listing = []
        for name in names.prefix(50_000) where !name.hasPrefix(".") {
            let url = folder.appendingPathComponent(name)
            var info = stat()
            guard lstat(url.path, &info) == 0 else { continue }
            let kind = info.st_mode & S_IFMT
            guard kind == S_IFREG || kind == S_IFDIR else { continue }
            result.append((Entry(device: info.st_dev, inode: info.st_ino), url, Snapshot(size: info.st_size, modified: info.st_mtimespec)))
        }
        return result
    }

    nonisolated static func isAirDrop(_ url: URL, since: Date) -> Bool {
        var buffer = [UInt8](repeating: 0, count: 512)
        let count = getxattr(url.path, "com.apple.quarantine", &buffer, buffer.count, 0, XATTR_NOFOLLOW)
        guard count > 0 else { return false }
        return isAirDrop(quarantine: String(decoding: buffer.prefix(count), as: UTF8.self), since: since)
    }

    /// `flags;hex-seconds;agent;uuid`. The time guards against an old AirDrop file being renamed or moved back in.
    nonisolated static func isAirDrop(quarantine: String, since: Date, now: Date = Date()) -> Bool {
        let fields = quarantine.split(separator: ";", omittingEmptySubsequences: false)
        guard fields.count >= 3, fields[2] == "sharingd", let seconds = UInt64(fields[1], radix: 16) else { return false }
        let made = Date(timeIntervalSince1970: TimeInterval(seconds))
        return made >= since.addingTimeInterval(-5) && made <= now.addingTimeInterval(60)
    }
}
