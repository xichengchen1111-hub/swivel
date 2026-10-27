import AppKit
import Darwin

// MARK: - Large files

/// Large files and old installers in Downloads, Desktop and Documents. These folders are protected by macOS, so this
/// scan runs only from its own button, after an alert that says macOS may ask for access (owner approved 2026-10-23).
/// It reads names, sizes and dates only, never contents; nothing is remembered after Swivel quits, and names never
/// reach the assistant report.
enum DoctorLargeFiles {
    /// Where the scan looks, relative to the home folder.
    static let roots = ["Downloads", "Desktop", "Documents"]
    /// Disk images and installer packages: once an app is installed they are usually not needed.
    static let installerExtensions: Set<String> = ["dmg", "pkg", "mpkg", "xip", "iso"]
    /// Bundles that look like folders but must be kept whole: a file inside one is never offered.
    static let packageExtensions: Set<String> = ["app", "photoslibrary", "musiclibrary", "tvlibrary", "imovielibrary",
                                                 "fcpbundle", "logicx", "band", "bundle", "framework", "xcarchive",
                                                 "pages", "numbers", "key", "rtfd", "sparsebundle", "mlpackage"]
    static let largeFile: Int64 = 500_000_000
    static let smallestInstaller: Int64 = 5_000_000
    static let maxRows = 40

    struct Found: Sendable, Equatable {
        var url: URL
        var bytes: Int64
        var modified: Date
        var installer: Bool
        var identity: FileIdentity
    }

    struct Outcome: Sendable {
        var files: [Found] = []
        /// Roots that couldn't be read (macOS refused, or the folder is a link).
        var unreadable: [String] = []
        var inCloud = 0
        var cancelled = false
        var checked = Date()
        var total: Int64 { files.reduce(0) { $0 + $1.bytes } }
    }

    /// Walks the three folders. Links are never followed, the walk stays on the home folder's volume, cloud
    /// placeholders are skipped without downloading them, and packages, hidden folders and node_modules are skipped
    /// whole. Hard-linked files free nothing when moved, so they aren't offered. Blocking; checks for cancellation.
    static func scan(home: URL, large: Int64 = largeFile, installer smallest: Int64 = smallestInstaller,
                     maxItems: Int = 3_000_000) -> Outcome {
        var outcome = Outcome()
        let previous = getiopolicy_np(IOPOL_TYPE_VFS_MATERIALIZE_DATALESS_FILES, IOPOL_SCOPE_THREAD)
        setiopolicy_np(IOPOL_TYPE_VFS_MATERIALIZE_DATALESS_FILES, IOPOL_SCOPE_THREAD, IOPOL_MATERIALIZE_DATALESS_FILES_OFF)
        defer { if previous >= 0 { setiopolicy_np(IOPOL_TYPE_VFS_MATERIALIZE_DATALESS_FILES, IOPOL_SCOPE_THREAD, previous) } }
        var items = 0
        for name in roots {
            guard let root = folder(name, home: home), let path = strdup(root.path) else {
                outcome.unreadable.append(name)
                continue
            }
            defer { free(path) }
            var paths: [UnsafeMutablePointer<CChar>?] = [path, nil]
            guard let fts = fts_open(&paths, FTS_PHYSICAL | FTS_XDEV | FTS_NOCHDIR, nil) else {
                outcome.unreadable.append(name)
                continue
            }
            defer { fts_close(fts) }
            while let entry = fts_read(fts) {
                items += 1
                if items % 4096 == 0, Task.isCancelled || items >= maxItems { outcome.cancelled = true; break }
                let info = Int32(entry.pointee.fts_info)
                if info == FTS_DNR, entry.pointee.fts_level == 0 { outcome.unreadable.append(name); continue }
                if info == FTS_D {
                    let leaf = (String(cString: entry.pointee.fts_path) as NSString).lastPathComponent
                    let ext = (leaf as NSString).pathExtension.lowercased()
                    if entry.pointee.fts_level > 0, leaf.hasPrefix(".") || leaf == "node_modules" || packageExtensions.contains(ext)
                        || DoctorWalk.isPackage(String(cString: entry.pointee.fts_path)) {
                        fts_set(fts, entry, FTS_SKIP)
                    }
                    continue
                }
                guard info == FTS_F, let stat = entry.pointee.fts_statp?.pointee else { continue }
                if stat.st_flags & UInt32(SF_DATALESS) != 0 { outcome.inCloud += 1; continue }
                guard stat.st_nlink == 1 else { continue }
                let bytes = Int64(stat.st_blocks) * 512
                let url = URL(fileURLWithPath: String(cString: entry.pointee.fts_path))
                let installer = installerExtensions.contains(url.pathExtension.lowercased())
                guard bytes >= (installer ? smallest : large) else { continue }
                let modified = Date(timeIntervalSince1970: TimeInterval(stat.st_mtimespec.tv_sec))
                outcome.files.append(Found(url: url, bytes: bytes, modified: modified, installer: installer,
                                           identity: FileIdentity(device: stat.st_dev, inode: UInt64(stat.st_ino))))
            }
            if outcome.cancelled { break }
        }
        outcome.files.sort { $0.bytes > $1.bytes }
        if outcome.files.count > maxRows { outcome.files.removeLast(outcome.files.count - maxRows) }
        return outcome
    }

    /// ~/Downloads and the others as real folders on the home folder's volume; nil for a link or anything else.
    static func folder(_ name: String, home: URL) -> URL? {
        guard let base = try? DoctorTrash.realHome(home) else { return nil }
        let url = base.appendingPathComponent(name, isDirectory: true)
        var info = stat(), homeInfo = stat()
        guard lstat(url.path, &info) == 0, lstat(base.path, &homeInfo) == 0, info.st_mode & S_IFMT == S_IFDIR,
              info.st_dev == homeInfo.st_dev else { return nil }
        return url
    }

    /// Runs the scan off the main thread; cancelling the calling task stops it.
    static func scanDetached(home: URL) async -> Outcome {
        let task = Task.detached(priority: .utility) { scan(home: home) }
        return await withTaskCancellationHandler { await task.value } onCancel: { task.cancel() }
    }

    static func displayName(_ root: String) -> String {
        switch root {
        case "Downloads": L("Downloads")
        case "Desktop": L("Desktop")
        default: L("Documents")
        }
    }

    /// True for a target this scan could have offered: a single file inside one of the three folders, with the
    /// identity it had when it was found.
    static func isAllowed(_ target: DoctorTrashTarget) -> Bool {
        guard target.mode == .item, target.identity != nil else { return false }
        return roots.contains { target.path.hasPrefix("~/" + $0 + "/") } && !target.path.contains("/../")
    }

    /// The storage result with what the scan found below it: a heading row, then one row per file.
    static func merge(_ base: DoctorResult?, _ outcome: Outcome, home: URL) -> DoctorResult {
        var result = base ?? DoctorResult(section: .storage, severity: .info, badge: "",
                                          headline: L("Large files and installers in Downloads, Desktop and Documents."))
        result.rows.append(DoctorRow(id: "files", title: L("Large files and installers"),
                                     value: outcome.files.isEmpty ? L("None found") : DoctorFormat.bytes(outcome.total),
                                     detail: L("In Downloads, Desktop and Documents: installers over 5 MB and other files over 500 MB. Check each one before moving it to the Trash."),
                                     severity: .info, bytes: nil))
        for (index, file) in outcome.files.enumerated() {
            let target = (try? DoctorTrash.homeRelativePath(file.url, home: home)).map {
                DoctorTrashTarget(path: $0, mode: .item, identity: file.identity)
            }
            let kind = file.installer
                ? L("Installer or disk image. Once the app is installed it is usually no longer needed.")
                : L("Large file. Make sure you have a copy elsewhere, or no longer need it.")
            let detail = DoctorFormat.path(file.url) + "\n" + L("Modified %@", DoctorFormat.date(file.modified)) + "\n" + kind
            result.rows.append(DoctorRow(id: "file.\(index)", title: DoctorText.visible(file.url.lastPathComponent),
                                         value: DoctorFormat.bytes(file.bytes), detail: detail,
                                         severity: file.bytes >= 5_000_000_000 ? .review : .info, bytes: file.bytes,
                                         location: file.url, trash: target, level: 1))
        }
        if !outcome.files.isEmpty {
            result.notes.insert(L("Found %d large files and installers, together %@. Select the ones you no longer need and choose Move to Trash.",
                                  outcome.files.count, DoctorFormat.bytes(outcome.total)), at: 0)
        }
        if !outcome.unreadable.isEmpty {
            result.notes.append(L("macOS didn’t allow reading %@. Allow it in Privacy & Security → Files & Folders, then search again.",
                                  outcome.unreadable.map(displayName).joined(separator: ", ")))
        }
        if outcome.inCloud > 0 { result.notes.append(L("%d files are only in the cloud and use no space on this Mac.", outcome.inCloud)) }
        if outcome.cancelled { result.notes.append(L("The search stopped before it finished.")) }
        result.report.append(L("Large files and installers in the user’s folders: %d, together %@.", outcome.files.count,
                               DoctorFormat.bytes(outcome.total)))
        return result
    }
}

// MARK: - Apps using memory

/// A running app the user may ask to quit, as it was when the check ran: a different app under the same process
/// number (or the same app relaunched) is never asked to quit.
struct DoctorQuitTarget: Sendable, Equatable {
    let pid: pid_t
    let bundleID: String
    let launched: Date?
    /// Not used for at least `DoctorAppActivity.idleAfter`: suggested for quitting.
    var idle = false
    /// The latest moment the app is known to have been used (sorting).
    var lastUsed: Date? = nil
}

/// Where `DoctorAppActivity` keeps its notes: Swivel's settings, or memory in tests.
protocol DoctorActivityStore: AnyObject {
    func object(forKey key: String) -> Any?
    func set(_ value: Any?, forKey key: String)
    func removeObject(forKey key: String)
}

extension UserDefaults: DoctorActivityStore {}

/// When each app was last in front. macOS keeps no such record that Swivel may read, so Swivel notes it itself while
/// Mac Doctor is on: bundle identifiers and times only, on this Mac, in Swivel's settings; turning Mac Doctor off
/// forgets them. Before Swivel started noting, nothing is known, so an app counts as used then at the latest.
@MainActor final class DoctorAppActivity {
    static let shared = DoctorAppActivity()
    /// How long counts as "not used", for the user to choose. Apps that hold memory are usually put to sleep after
    /// hours (Edge's sleeping tabs: 2 hours; Quitter: 10 minutes); days to a month leave out anything in recent use.
    nonisolated static let idleChoices: [TimeInterval] = [24 * 3600, 7 * 24 * 3600, 30 * 24 * 3600]
    nonisolated static let idleKey = "doctor.appIdleAfter"
    /// Apps not used for this long are suggested for quitting.
    nonisolated static var idleAfter: TimeInterval {
        let stored = UserDefaults.standard.double(forKey: idleKey)
        return idleChoices.contains(stored) ? stored : idleChoices[1]
    }
    /// "1 day", "1 week", "1 month".
    nonisolated static func idleName(_ seconds: TimeInterval) -> String {
        switch seconds {
        case idleChoices[0]: L("1 day")
        case idleChoices[2]: L("1 month")
        default: L("1 week")
        }
    }
    static let usedKey = "doctor.appsLastUsed"
    static let sinceKey = "doctor.appsNotedSince"
    static let maxApps = 500

    struct LastUse: Sendable, Equatable {
        /// The latest moment the app is known to have been used (or could have been, unseen).
        let date: Date
        /// `date` is a time Swivel saw the app in front; otherwise it's a lower bound (launch, or when noting began).
        let exact: Bool
        let front: Bool
        func idle(now: Date, after: TimeInterval = DoctorAppActivity.idleAfter) -> Bool {
            !front && now.timeIntervalSince(date) >= after
        }
    }

    private let defaults: DoctorActivityStore
    private var observer: NSObjectProtocol?
    private var front: String?

    init(defaults: DoctorActivityStore = UserDefaults.standard) { self.defaults = defaults }

    func start() {
        guard observer == nil else { return }
        activated(NSWorkspace.shared.frontmostApplication?.bundleIdentifier, at: Date(), enabled: Feature.doctor.isOn)
        observer = NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.didActivateApplicationNotification,
                                                                     object: nil, queue: .main) { [weak self] note in
            let id = (note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication)?.bundleIdentifier
            MainActor.assumeIsolated { self?.activated(id, at: Date(), enabled: Feature.doctor.isOn) }
        }
    }

    /// The app coming to the front and the one leaving it were both in use until now.
    func activated(_ id: String?, at now: Date, enabled: Bool) {
        let previous = front
        front = id
        guard enabled else {
            defaults.removeObject(forKey: Self.usedKey)
            defaults.removeObject(forKey: Self.sinceKey)
            return
        }
        if defaults.object(forKey: Self.sinceKey) == nil { defaults.set(now.timeIntervalSince1970, forKey: Self.sinceKey) }
        var used = defaults.object(forKey: Self.usedKey) as? [String: Double] ?? [:]
        for key in [previous, id].compactMap({ $0 }) { used[key] = now.timeIntervalSince1970 }
        if used.count > Self.maxApps {
            used = Dictionary(uniqueKeysWithValues: used.sorted { $0.value > $1.value }.prefix(Self.maxApps).map { ($0.key, $0.value) })
        }
        defaults.set(used, forKey: Self.usedKey)
    }

    func lastUse(bundleID: String, launched: Date?, now: Date = Date()) -> LastUse {
        let used = (defaults.object(forKey: Self.usedKey) as? [String: Double])?[bundleID].map(Date.init(timeIntervalSince1970:))
        let since = (defaults.object(forKey: Self.sinceKey) as? Double).map(Date.init(timeIntervalSince1970:))
        return Self.lastUse(seen: used, since: since, launched: launched, front: front == bundleID, now: now)
    }

    /// Launching an app counts as using it; before noting began (or with nothing noted yet) it may have been used
    /// at any time, so the bound is then.
    nonisolated static func lastUse(seen: Date?, since: Date?, launched: Date?, front: Bool, now: Date) -> LastUse {
        if front { return LastUse(date: now, exact: true, front: true) }
        let known = seen ?? min(since ?? now, now)
        guard let launched, launched > known else { return LastUse(date: known, exact: seen != nil, front: false) }
        return LastUse(date: min(launched, now), exact: false, front: false)
    }
}

enum DoctorApps {
    struct App: Sendable {
        let pid: pid_t
        let bundleID: String
        let name: String
        let bundlePath: String
        let launched: Date?
    }

    struct Usage: Sendable {
        let app: App
        var bytes: Int64
        var lastUse: DoctorAppActivity.LastUse? = nil
    }

    /// Apps macOS needs to keep running, which Mac Doctor never offers to quit.
    static let neverQuit: Set<String> = ["com.apple.finder", "com.apple.dock", "com.apple.loginwindow",
                                         "com.apple.systemuiserver", "com.apple.controlcenter",
                                         "com.apple.notificationcenterui", "com.apple.Spotlight", "com.apple.WindowManager",
                                         "com.apple.TextInputMenuAgent", "com.apple.coreservices.uiagent"]
    static let smallest: Int64 = 50_000_000
    static let maxRows = 20

    /// The two orders the user picks between on the page.
    enum Sort: String, Sendable {
        /// Longest unused first.
        case lastUsed
        /// Most memory first.
        case memory
        static let key = "doctor.appSort"
        static var current: Sort { UserDefaults.standard.string(forKey: key).flatMap(Sort.init) ?? .lastUsed }
    }

    /// App rows in the chosen order; every other row keeps its place.
    static func sorted(_ rows: [DoctorRow], by sort: Sort) -> [DoctorRow] {
        let slots = rows.indices.filter { rows[$0].quit != nil }
        let apps = slots.map { rows[$0] }.sorted { a, b in
            switch sort {
            case .memory: return (a.bytes ?? 0) > (b.bytes ?? 0)
            case .lastUsed:
                let x = a.quit?.lastUsed ?? .distantFuture, y = b.quit?.lastUsed ?? .distantFuture
                return x != y ? x < y : (a.bytes ?? 0) > (b.bytes ?? 0)
            }
        }
        var out = rows
        for (slot, app) in zip(slots, apps) { out[slot] = app }
        return out
    }

    /// Running apps that may be offered, read on the main thread.
    @MainActor static func candidates() -> [App] {
        let me = getpid()
        let own = Bundle.main.bundleIdentifier ?? ""
        return NSWorkspace.shared.runningApplications.compactMap { app in
            guard app.processIdentifier != me, app.activationPolicy != .prohibited, !app.isTerminated,
                  let id = app.bundleIdentifier, quittable(id, own: own), let path = app.bundleURL?.path,
                  !path.hasPrefix("/System/Library/"), !path.hasPrefix("/usr/") else { return nil }
            return App(pid: app.processIdentifier, bundleID: id, name: app.localizedName ?? id, bundlePath: path,
                       launched: app.launchDate)
        }
    }

    /// Never one of macOS's own essentials, and never Swivel or its helper.
    static func quittable(_ bundleID: String, own: String) -> Bool {
        if neverQuit.contains(bundleID) { return false }
        return own.isEmpty || (bundleID != own && !bundleID.hasPrefix(own + "."))
    }

    /// Memory each app uses with its helpers: every process of this user that descends from the app or runs from
    /// inside its bundle counts towards it (Chrome's and Electron apps' helpers are where most of it goes). Uses the
    /// same "memory footprint" Activity Monitor shows. Blocking.
    static func usage(_ apps: [App]) -> [Usage] {
        guard !apps.isEmpty else { return [] }
        let uid = getuid()
        var parent: [pid_t: pid_t] = [:]
        var footprint: [pid_t: Int64] = [:]
        var path: [pid_t: String] = [:]
        let count = proc_listallpids(nil, 0)
        guard count > 0 else { return [] }
        var pids = [pid_t](repeating: 0, count: Int(count) + 64)
        let filled = proc_listallpids(&pids, Int32(pids.count * MemoryLayout<pid_t>.size))
        for pid in pids.prefix(Int(max(filled, 0))) where pid > 0 {
            var info = proc_bsdinfo()
            guard proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, &info, Int32(MemoryLayout<proc_bsdinfo>.size)) > 0,
                  info.pbi_uid == uid else { continue }
            parent[pid] = pid_t(info.pbi_ppid)
            var usage = rusage_info_v2()
            let ok = withUnsafeMutablePointer(to: &usage) {
                $0.withMemoryRebound(to: rusage_info_t?.self, capacity: 1) { proc_pid_rusage(pid, RUSAGE_INFO_V2, $0) }
            }
            if ok == 0 { footprint[pid] = Int64(usage.ri_phys_footprint) }
            var buffer = [CChar](repeating: 0, count: Int(MAXPATHLEN) * 4)
            if proc_pidpath(pid, &buffer, UInt32(buffer.count)) > 0 { path[pid] = String(cString: buffer) }
        }
        var totals = Dictionary(uniqueKeysWithValues: apps.map { ($0.pid, Int64(0)) })
        let byPID = Dictionary(uniqueKeysWithValues: apps.map { ($0.pid, $0) })
        for (pid, bytes) in footprint {
            var owner: pid_t?
            var current = pid
            for _ in 0..<32 {
                if byPID[current] != nil { owner = current; break }
                guard let up = parent[current], up > 1, up != current else { break }
                current = up
            }
            if owner == nil, let executable = path[pid] {
                owner = apps.first { executable.hasPrefix($0.bundlePath + "/") }?.pid
            }
            if let owner { totals[owner, default: 0] += bytes }
        }
        return apps.compactMap { app in
            guard let bytes = totals[app.pid], bytes >= smallest else { return nil }
            return Usage(app: app, bytes: bytes)
        }.sorted { $0.bytes > $1.bytes }.prefix(maxRows).map { $0 }
    }

    static func rows(_ usage: [Usage], now: Date = Date()) -> [DoctorRow] {
        guard !usage.isEmpty else { return [] }
        var rows = [DoctorRow(id: "apps", title: L("Apps you can quit to free memory"),
                              value: DoctorFormat.memory(usage.reduce(0) { $0 + $1.bytes }),
                              detail: L("Memory each app uses together with its helper processes, and when you last used it. Apps you haven’t used for %@ or longer are selected for you.", DoctorAppActivity.idleName(DoctorAppActivity.idleAfter)),
                              severity: .info)]
        for item in usage {
            let idle = item.lastUse?.idle(now: now) ?? false
            let memory = DoctorFormat.memory(item.bytes)
            rows.append(DoctorRow(id: "app.\(item.app.pid)", title: DoctorText.visible(item.app.name),
                                  value: item.lastUse.map { memory + " · " + lastUsed($0, now: now) } ?? memory,
                                  detail: idle ? L("Not used for %@ or longer. Quitting it frees about %@; it asks you to save open work first.",
                                                   DoctorAppActivity.idleName(DoctorAppActivity.idleAfter), memory) : "",
                                  severity: item.bytes >= 2_000_000_000 || idle ? .review : .info, bytes: item.bytes,
                                  quit: DoctorQuitTarget(pid: item.app.pid, bundleID: item.app.bundleID, launched: item.app.launched, idle: idle,
                                                         lastUsed: item.lastUse?.date),
                                  level: 1))
        }
        return rows
    }

    /// "Last used 3 days ago", or "Not used for at least 3 days" when Swivel didn't see it.
    static func lastUsed(_ use: DoctorAppActivity.LastUse, now: Date) -> String {
        let ago = now.timeIntervalSince(use.date)
        if use.front { return L("In use now") }
        if ago < 60 { return use.exact ? L("Used just now") : L("No record yet") }
        return use.exact ? L("Last used %@ ago", DoctorFormat.duration(ago)) : L("Not used for at least %@", DoctorFormat.duration(ago))
    }

    /// Asks each app to quit, as choosing Quit in it would (never forced). Only an app that is still the one the
    /// check saw is asked. Returns the names asked and those left alone.
    @MainActor static func quit(_ targets: [DoctorQuitTarget]) -> (asked: [String], skipped: [String]) {
        var asked: [String] = [], skipped: [String] = []
        let own = Bundle.main.bundleIdentifier ?? ""
        for target in targets {
            guard let app = NSRunningApplication(processIdentifier: target.pid), !app.isTerminated,
                  app.bundleIdentifier == target.bundleID, app.launchDate == target.launched,
                  quittable(target.bundleID, own: own) else {
                skipped.append(target.bundleID)
                continue
            }
            let name = app.localizedName ?? target.bundleID
            if app.terminate() { asked.append(name) } else { skipped.append(name) }
        }
        return (asked, skipped)
    }
}

// MARK: - Live readings

/// Readings for the charts, from Mach and the file system only (no commands, no protected places). Taken every two
/// seconds while a page with charts is on screen, and never otherwise.
struct DoctorLive {
    struct Memory: Sendable, Equatable {
        var used: Int64
        var physical: Int64
        var fraction: Double { physical > 0 ? min(1, Double(used) / Double(physical)) : 0 }
    }

    private var previous: [UInt32]?

    /// Processor use of the whole Mac since the previous call, 0…1; nil the first time.
    mutating func cpu() -> Double? {
        var load = host_cpu_load_info()
        var count = mach_msg_type_number_t(MemoryLayout<host_cpu_load_info>.size / MemoryLayout<integer_t>.size)
        let status = withUnsafeMutablePointer(to: &load) {
            $0.withMemoryRebound(to: integer_t.self, capacity: Int(count)) { host_statistics(mach_host_self(), HOST_CPU_LOAD_INFO, $0, &count) }
        }
        guard status == KERN_SUCCESS else { return nil }
        let ticks = [load.cpu_ticks.0, load.cpu_ticks.1, load.cpu_ticks.2, load.cpu_ticks.3]  // user, system, idle, nice
        defer { previous = ticks }
        guard let previous else { return nil }
        let delta = zip(ticks, previous).map { Double($0 &- $1) }
        let total = delta.reduce(0, +)
        guard total > 0 else { return nil }
        return min(1, max(0, (delta[0] + delta[1] + delta[3]) / total))
    }

    /// Memory in use as Activity Monitor counts it: app memory, wired memory and the compressor.
    static func memory() -> Memory? {
        var stats = vm_statistics64()
        var count = mach_msg_type_number_t(MemoryLayout<vm_statistics64>.size / MemoryLayout<integer_t>.size)
        let status = withUnsafeMutablePointer(to: &stats) {
            $0.withMemoryRebound(to: integer_t.self, capacity: Int(count)) { host_statistics64(mach_host_self(), HOST_VM_INFO64, $0, &count) }
        }
        guard status == KERN_SUCCESS else { return nil }
        let page = Int64(vm_kernel_page_size)
        let app = Int64(stats.internal_page_count) - Int64(stats.purgeable_count)
        let used = (max(0, app) + Int64(stats.wire_count) + Int64(stats.compressor_page_count)) * page
        let physical = Int64(ProcessInfo.processInfo.physicalMemory)
        return Memory(used: min(used, physical), physical: physical)
    }
}
