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
    private var source: DispatchSourceFileSystemObject?
    private var observer: NSObjectProtocol?
    private var known: Set<Entry> = []
    /// Arrivals waiting for their size and date to stop changing (AirDrop may still be writing).
    private var waiting: [Entry: (url: URL, snapshot: Snapshot)] = [:]
    private var started = Date()
    private var scan: Task<Void, Never>?
    private var generation = 0
    private let queue = DispatchQueue(label: "local.swivel.airdrop-shelf")

    init(folder: URL = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Downloads"),
         deliver: @escaping @MainActor ([URL]) -> Void = { Shelf.shared.add($0) }) {
        self.folder = folder
        self.deliver = deliver
    }

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
        guard source == nil else { return }
        let fd = open(folder.path, O_EVTONLY | O_DIRECTORY | O_CLOEXEC)
        guard fd >= 0 else { return }  // No access (yet), or no Downloads folder.
        let source = DispatchSource.makeFileSystemObjectSource(fileDescriptor: fd, eventMask: [.write, .rename, .delete], queue: .main)
        source.setEventHandler { [weak self] in MainActor.assumeIsolated { self?.schedule(after: 0.4) } }
        source.setCancelHandler { close(fd) }
        self.source = source
        started = Date()
        generation += 1
        waiting = [:]
        // Whatever is already there is not news.
        let folder = self.folder, generation = self.generation
        scan = Task { [weak self] in
            let entries = await Self.list(folder, on: self?.queue ?? .global())
            guard let self, self.generation == generation else { return }
            self.known = Set(entries.map(\.entry))
            source.resume()
        }
    }

    func stop() {
        generation += 1
        scan?.cancel(); scan = nil
        source?.cancel(); source = nil
        known = []; waiting = [:]
    }

    private func schedule(after delay: Double) {
        let generation = self.generation
        DispatchQueue.main.asyncAfter(deadline: .now() + delay) { [weak self] in
            MainActor.assumeIsolated {
                guard let self, self.generation == generation else { return }
                self.check()
            }
        }
    }

    private func check() {
        guard source != nil else { return }
        let folder = self.folder, generation = self.generation, started = self.started
        scan = Task { [weak self] in
            let entries = await Self.list(folder, on: self?.queue ?? .global())
            guard let self, self.generation == generation else { return }
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
            if !self.waiting.isEmpty { self.schedule(after: 1) }
            if !ready.isEmpty { self.deliver(ready) }
        }
    }

    /// Visible top-level entries with their identity, size and modification date (lstat: links are not followed).
    nonisolated static func list(_ folder: URL, on queue: DispatchQueue) async -> [(entry: Entry, url: URL, snapshot: Snapshot)] {
        await withCheckedContinuation { continuation in
            queue.async {
                let names = (try? FileManager.default.contentsOfDirectory(atPath: folder.path)) ?? []
                var result: [(Entry, URL, Snapshot)] = []
                for name in names.prefix(50_000) where !name.hasPrefix(".") {
                    let url = folder.appendingPathComponent(name)
                    var info = stat()
                    guard lstat(url.path, &info) == 0 else { continue }
                    let kind = info.st_mode & S_IFMT
                    guard kind == S_IFREG || kind == S_IFDIR else { continue }
                    result.append((Entry(device: info.st_dev, inode: info.st_ino), url, Snapshot(size: info.st_size, modified: info.st_mtimespec)))
                }
                continuation.resume(returning: result)
            }
        }
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
