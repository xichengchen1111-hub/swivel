import AppKit
import CoreServices
import Darwin

// MARK: - Clean Up Files

/// One list of what the Mac no longer needs (owner asked 2026-10-27): apps not opened for months, files left by apps
/// that are gone, caches, installers, extra copies of files and rebuildable project folders. The last four come from
/// the existing checks, each still moved under its own rule; this adds the first two and puts everything together.
/// Apps are listed, but selected for you only when the user turns that on.
enum DoctorSweep {
    /// How long without opening counts as unused, in months, for the user to choose. Android hibernates apps after
    /// about three months without use; macOS records when each app was last opened (Spotlight's last-used date).
    static let monthChoices = [1, 3, 6, 12]
    static let monthsKey = "doctor.unusedAppsMonths"
    static let selectAppsKey = "doctor.cleanUpSelectsApps"
    static let maxOrphans = 100

    static var months: Int {
        let stored = UserDefaults.standard.integer(forKey: monthsKey)
        return monthChoices.contains(stored) ? stored : 3
    }
    static var unusedAfter: TimeInterval { TimeInterval(months) * 30 * 24 * 3600 }
    static var selectsApps: Bool { UserDefaults.standard.bool(forKey: selectAppsKey) }

    /// "1 month", "3 months"…
    static func monthsName(_ count: Int) -> String { count == 1 ? L("1 month") : L("%d months", count) }

    /// Folders in ~/Library searched for leftovers of removed apps. Launch agents are Background Items' (already in
    /// the list); per-Mac settings carry a hardware suffix and are left alone.
    static let orphanPlaces = DoctorUninstall.places.filter { !["Library/LaunchAgents", "Library/Preferences/ByHost"].contains($0.path) }

    struct UnusedApp: Sendable {
        var app: DoctorUninstall.App
        /// When macOS last saw it opened; nil when it has no record (then `since` is when it was added).
        var opened: Date?
        var since: Date
        var leftoverBytes: Int64 { app.leftovers.filter { !$0.byName }.reduce(0) { $0 + $1.bytes } }
    }

    struct Orphan: Sendable {
        var url: URL
        var path: String
        var what: String
        var bytes: Int64
        var isFolder: Bool
        var identity: FileIdentity
        var modified: Date
        /// Another app from the same maker is installed: it may still use this, so it isn't selected for you.
        var vendorInstalled: Bool
    }

    struct Outcome: Sendable {
        var apps: [UnusedApp] = []
        var orphans: [Orphan] = []
        /// No app had a last-opened date: Spotlight is probably off, so no app is suggested.
        var noRecords = false
        var checked = Date()
    }

    // MARK: Apps not opened for a while

    /// Apps in /Applications and ~/Applications, and one folder level below them.
    static func installedApps(home: URL) -> [URL] {
        var apps: [URL] = []
        for root in [URL(fileURLWithPath: "/Applications"), home.appendingPathComponent("Applications")] {
            guard let names = try? FileManager.default.contentsOfDirectory(atPath: root.path) else { continue }
            for name in names where !name.hasPrefix(".") {
                let url = root.appendingPathComponent(name)
                var info = stat()
                guard lstat(url.path, &info) == 0, info.st_mode & S_IFMT == S_IFDIR else { continue }
                if name.hasSuffix(".app") { apps.append(url); continue }
                for inner in (try? FileManager.default.contentsOfDirectory(atPath: url.path)) ?? [] where inner.hasSuffix(".app") {
                    let app = url.appendingPathComponent(inner)
                    var innerInfo = stat()
                    if lstat(app.path, &innerInfo) == 0, innerInfo.st_mode & S_IFMT == S_IFDIR { apps.append(app) }
                }
            }
        }
        return apps
    }

    /// When macOS last saw the app opened, and when it was added to this Mac.
    static func dates(_ app: URL) -> (opened: Date?, added: Date?) {
        guard let item = MDItemCreateWithURL(nil, app as CFURL) else { return (nil, nil) }
        let opened = MDItemCopyAttribute(item, kMDItemLastUsedDate) as? Date
        let added = MDItemCopyAttribute(item, kMDItemDateAdded) as? Date
        return (opened, added)
    }

    /// The latest the app is known to have been opened, or (never recorded) when it was added. Nil: nothing known.
    static func lastUse(opened: Date?, added: Date?) -> Date? { opened ?? added }

    /// Apps not opened for `after`, never one that is running, comes with macOS, or is Swivel. Blocking.
    static func unusedApps(home: URL, now: Date, after: TimeInterval, running: Set<String>,
                           apps: [URL]? = nil, dates: (URL) -> (opened: Date?, added: Date?) = dates) -> (apps: [UnusedApp], noRecords: Bool) {
        var found: [UnusedApp] = []
        var anyRecord = false
        let candidates = apps ?? installedApps(home: home)
        DoctorProgress.current?.stage("Checking unused apps…", total: candidates.count)
        for (index, url) in candidates.enumerated() {
            DoctorProgress.current?.stage("Checking unused apps…", completed: index, total: candidates.count)
            if Task.isCancelled { break }
            guard let bundle = Bundle(url: url), let id = bundle.bundleIdentifier,
                  DoctorUninstall.refusal(bundleID: id, url: url) == nil, !running.contains(id) else { continue }
            let known = dates(url)
            if known.opened != nil { anyRecord = true }
            guard let since = lastUse(opened: known.opened, added: known.added), now.timeIntervalSince(since) >= after,
                  let app = DoctorUninstall.inspect(url, home: home), app.refusal == nil else { continue }
            found.append(UnusedApp(app: app, opened: known.opened, since: since))
        }
        // Without a single last-opened date the record is missing, not the use: suggest nothing.
        guard anyRecord || found.isEmpty else { return ([], true) }
        return (found.sorted { $0.since < $1.since }, false)
    }

    // MARK: Leftovers of removed apps

    /// A name in ~/Library that is an app identifier (reverse-DNS, three parts or more), not Apple's or Swivel's.
    static func identifier(_ entry: String) -> String? {
        let lower = entry.lowercased()
        let stem = ["plist", "savedstate", "binarycookies"].contains((lower as NSString).pathExtension)
            ? (lower as NSString).deletingPathExtension : lower
        let parts = stem.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count >= 3, parts.allSatisfy({ !$0.isEmpty && $0.allSatisfy { $0.isLetter || $0.isNumber || $0 == "-" || $0 == "_" } }),
              parts[0].count >= 2, parts[0].count <= 6, parts[0].allSatisfy(\.isLetter),
              !stem.hasPrefix("com.apple."), !stem.hasPrefix("group."), !stem.hasPrefix("systemgroup."),
              DoctorCommand.isPlain(entry) else { return nil }
        if let own = Bundle.main.bundleIdentifier?.lowercased(), stem == own || stem.hasPrefix(own + ".") { return nil }
        return stem
    }

    /// True when an app with this identifier, or one it starts with (three parts or more), is installed or running.
    static func inUse(_ identifier: String, registered: (String) -> Bool, running: Set<String>) -> Bool {
        if running.contains(where: { let id = $0.lowercased(); return id == identifier || identifier.hasPrefix(id + ".") || id.hasPrefix(identifier + ".") }) {
            return true
        }
        var parts = identifier.split(separator: ".").map(String.init)
        while parts.count >= 3 {
            if registered(parts.joined(separator: ".")) { return true }
            parts.removeLast()
        }
        return false
    }

    static let registered: @Sendable (String) -> Bool = { id in
        NSWorkspace.shared.urlForApplication(withBundleIdentifier: id) != nil
    }

    /// Files and folders in ~/Library named after an app that is no longer installed. Containers are protected and
    /// never read. Blocking.
    static func orphans(home: URL, installed: Set<String>, running: Set<String>,
                        registered: (String) -> Bool) -> [Orphan] {
        let realHome = (try? DoctorTrash.realHome(home)) ?? home
        let vendors = Set(installed.map { $0.lowercased().split(separator: ".").prefix(2).joined(separator: ".") })
        var found: [Orphan] = []
        DoctorProgress.current?.stage("Checking app leftovers…")
        for place in orphanPlaces {
            let folder = realHome.appendingPathComponent(place.path, isDirectory: true)
            guard !DoctorPrivacy.isProtected(folder, home: realHome),
                  let names = try? FileManager.default.contentsOfDirectory(atPath: folder.path) else { continue }
            for entry in names {
                if Task.isCancelled { return found }
                guard let id = identifier(entry), !inUse(id, registered: registered, running: running) else { continue }
                let url = folder.appendingPathComponent(entry)
                var info = stat()
                guard lstat(url.path, &info) == 0 else { continue }
                let type = info.st_mode & S_IFMT
                guard type == S_IFREG || type == S_IFDIR else { continue }
                let bytes = type == S_IFDIR ? DoctorStorage.size(of: url, home: realHome).bytes : Int64(info.st_blocks) * 512
                found.append(Orphan(url: url, path: "~/" + place.path + "/" + entry, what: place.what, bytes: bytes,
                                    isFolder: type == S_IFDIR, identity: FileIdentity(device: info.st_dev, inode: UInt64(info.st_ino)),
                                    modified: Date(timeIntervalSince1970: TimeInterval(info.st_mtimespec.tv_sec)),
                                    vendorInstalled: vendors.contains(id.split(separator: ".").prefix(2).joined(separator: "."))))
            }
        }
        return Array(found.sorted { $0.bytes > $1.bytes }.prefix(maxOrphans))
    }

    /// A direct child of one of the searched Library folders, named like an app identifier, as it was found.
    static func isAllowedOrphan(_ target: DoctorTrashTarget) -> Bool {
        guard target.mode == .item || target.mode == .folder, target.identity != nil, !target.path.contains("/../") else { return false }
        return orphanPlaces.contains { place in
            let prefix = "~/" + place.path + "/"
            guard target.path.hasPrefix(prefix) else { return false }
            let name = String(target.path.dropFirst(prefix.count))
            return !name.contains("/") && identifier(name) != nil
        }
    }

    // MARK: The page

    /// The apps and leftovers this check found, as rows: a heading, then one row per item.
    static func result(_ outcome: Outcome, now: Date, selectApps: Bool = selectsApps, months: Int = months) -> DoctorResult {
        var result = DoctorResult(section: .uninstall, severity: .info, badge: "", headline: "")
        result.checked = outcome.checked
        if !outcome.apps.isEmpty {
            let total = outcome.apps.reduce(Int64(0)) { $0 + $1.app.bytes + $1.leftoverBytes }
            result.rows.append(DoctorRow(id: "cleanup.apps", title: L("Apps not opened for %@", monthsName(months)),
                                         value: L("%d items · %@", outcome.apps.count, DoctorFormat.bytes(total)),
                                         detail: L("Each app goes to the Trash with the settings, caches and support files named after it."),
                                         severity: .info))
            for (index, item) in outcome.apps.enumerated() {
                let app = item.app
                let when = item.opened.map { L("Last opened %@", DoctorFormat.date($0)) }
                    ?? L("Never opened since it was added %@", DoctorFormat.date(item.since))
                let size = DoctorFormat.bytes(app.bytes + item.leftoverBytes)
                result.rows.append(DoctorRow(id: "uapp.\(index)", title: DoctorText.visible(app.name),
                                             value: size + " · " + ago(item.since, now: now),
                                             detail: DoctorFormat.path(app.url) + "\n" + when + "\n"
                                                + L("The app uses %@; %d related items in your Library use %@.", DoctorFormat.bytes(app.bytes),
                                                    app.leftovers.filter { !$0.byName }.count, DoctorFormat.bytes(item.leftoverBytes)),
                                             severity: selectApps ? .info : .review, bytes: app.bytes + item.leftoverBytes,
                                             location: app.url,
                                             trash: DoctorTrashTarget(path: app.url.path, mode: .folder, identity: app.identity, origin: .app),
                                             bundleIDs: [app.bundleID], level: 1))
            }
        }
        if !outcome.orphans.isEmpty {
            result.rows.append(DoctorRow(id: "cleanup.orphans", title: L("Left behind by apps you removed"),
                                         value: L("%d items · %@", outcome.orphans.count, DoctorFormat.bytes(outcome.orphans.reduce(0) { $0 + $1.bytes })),
                                         detail: L("Named after an app that is no longer installed. Items untouched for %@ are selected for you.", monthsName(months)),
                                         severity: .info))
            for (index, item) in outcome.orphans.enumerated() {
                let stale = now.timeIntervalSince(item.modified) >= TimeInterval(months) * 30 * 24 * 3600
                // Support folders can hold the user's own data (game saves, documents): listed, never selected for you.
                let data = item.path.hasPrefix("~/Library/Application Support/")
                let target = DoctorTrashTarget(path: item.path, mode: item.isFolder ? .folder : .item, identity: item.identity, origin: .orphan)
                var detail = DoctorFormat.path(item.url) + "\n" + L("Last changed %@", DoctorFormat.date(item.modified))
                if item.vendorInstalled { detail += "\n" + L("Another app from the same maker is installed and may still use this: check it before selecting.") }
                if data { detail += "\n" + L("Support files can hold your own data, such as game saves: check it before selecting.") }
                result.rows.append(DoctorRow(id: "orphan.\(index)", title: L(item.what) + " · " + DoctorText.visible(item.url.lastPathComponent),
                                             value: DoctorFormat.bytes(item.bytes), detail: detail,
                                             severity: stale && !item.vendorInstalled && !data ? .info : .review, bytes: item.bytes,
                                             location: item.url, trash: DoctorTrash.isAllowed(target) ? target : nil, level: 1))
            }
        }
        if outcome.noRecords {
            result.notes.append(L("macOS has no record of when apps were last opened (Spotlight may be off), so no app is suggested."))
        }
        result.report.append(L("Apps not opened for %@: %d, together %@.", monthsName(months), outcome.apps.count,
                               DoctorFormat.bytes(outcome.apps.reduce(0) { $0 + $1.app.bytes + $1.leftoverBytes })))
        result.report.append(L("Files left by removed apps: %d, together %@.", outcome.orphans.count,
                               DoctorFormat.bytes(outcome.orphans.reduce(0) { $0 + $1.bytes })))
        return result
    }

    /// "5 months ago" style, in the app's language.
    static func ago(_ date: Date, now: Date) -> String {
        let days = Int(now.timeIntervalSince(date) / 86400)
        if days >= 60 { return L("%d months ago", days / 30) }
        return L("%d days ago", max(0, days))
    }

    /// Everything to clean on one page: this check's own rows, then what the other checks found that can go, each under
    /// a heading. Items that are safe to remove are selected for you (severity info); the rest are listed to review.
    static func combine(own: DoctorResult?, caches: DoctorResult?, background: DoctorResult?, installers: DoctorResult?,
                        duplicates: DoctorResult?, projects: DoctorResult?, pending: [String]) -> DoctorResult {
        var result = own ?? DoctorResult(section: .uninstall, severity: .info, badge: "", headline: "")
        func add(_ id: String, _ title: String, _ detail: String, _ rows: [DoctorRow], selected: (DoctorRow) -> Bool = { _ in true }) {
            guard !rows.isEmpty else { return }
            result.rows.append(DoctorRow(id: "cleanup." + id, title: title,
                                         value: L("%d items · %@", rows.count, DoctorFormat.bytes(rows.reduce(0) { $0 + ($1.bytes ?? 0) })),
                                         detail: detail, severity: .info))
            result.rows += rows.map { row in
                var row = row
                row.level = 1
                row.severity = selected(row) ? .info : .review
                return row
            }
        }
        let agents = (background?.rows ?? []).filter { $0.trash != nil }
        if !agents.isEmpty {
            // Leftover launch agents belong with the other leftovers.
            if let heading = result.rows.firstIndex(where: { $0.id == "cleanup.orphans" }) {
                let end = result.rows[(heading + 1)...].firstIndex { $0.level == 0 } ?? result.rows.endIndex
                result.rows.insert(contentsOf: agents.map { var row = $0; row.level = 1; row.severity = .info; return row }, at: end)
                let group = result.rows[(heading + 1)...].prefix { $0.level > 0 }
                result.rows[heading].value = L("%d items · %@", group.count, DoctorFormat.bytes(group.reduce(0) { $0 + ($1.bytes ?? 0) }))
            } else {
                add("agents", L("Left behind by apps you removed"), L("Small programs that would start by themselves, from apps that are gone."), agents)
            }
        }
        add("caches", L("Caches"), L("Apps rebuild them when needed."),
            (caches?.rows ?? []).filter { $0.id.hasPrefix("cache.") && ($0.trash != nil || !$0.bundleIDs.isEmpty) })
        // Personal files (installers, copies) and folders that may need the network to rebuild are listed for the user
        // to tick; only what is safe by nature is ticked for them.
        add("installers", L("Installers and disk images"), L("In Downloads, Desktop and Documents. Once an app is installed they are usually no longer needed."),
            (installers?.rows ?? []).filter { $0.id.hasPrefix("file.") && DoctorLargeFiles.installerExtensions.contains($0.location?.pathExtension.lowercased() ?? "") },
            selected: { _ in false })
        // Every copy but the first (oldest) of identical files; similar files are listed but never selected.
        let copies = (duplicates?.rows ?? []).filter { $0.level == 1 && $0.trash != nil && !$0.id.hasSuffix(".0") }
        add("duplicates", L("Duplicate files"), L("Extra copies; the oldest copy of each file stays. Similar files are listed to compare, not selected."),
            copies, selected: { _ in false })
        add("builds", L("Project build folders"), L("Folders such as node_modules that the project’s tools build again next time, which may need the network."),
            (projects?.rows ?? []).filter { $0.trash != nil }, selected: { _ in false })
        // A group still being searched shows as its own line until it is done.
        for (index, name) in pending.enumerated() {
            result.rows.append(DoctorRow(id: "cleanup.pending.\(index)", title: name, value: L("Looking…"), detail: "", severity: .info))
        }

        let movable = result.rows.filter { $0.trash != nil }
        let total = movable.reduce(Int64(0)) { $0 + ($1.bytes ?? 0) }
        result.badge = movable.isEmpty ? (pending.isEmpty ? L("None found") : "") : DoctorFormat.bytes(total)
        result.headline = movable.isEmpty ? (pending.isEmpty ? L("Nothing to clean up was found.") : L("Still looking…"))
            : L("Found %d things you may not need, together %@.", movable.count, DoctorFormat.bytes(total))
        result.notes.append(L("Caches and old leftovers are ticked for you. Installers, copies of your files, build folders and apps are only listed: tick a group to include all of it. Everything goes to the Trash, and Undo Last Move puts it back until you empty the Trash."))
        result.report += [caches, installers, duplicates, projects].compactMap { $0?.report.first }
        return result
    }
}
