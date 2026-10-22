import AppKit
import Darwin
import DoctorCatalog

/// Runs Mac Doctor's checks and keeps their results in memory while Swivel is open (never on disk), so the window
/// can be closed and reopened without checking again. Results are written in the language they were checked in, so
/// a language change clears them. Checks start only when the user asks.
@MainActor
final class DoctorCenter {
    static let shared = DoctorCenter()
    /// Posted whenever a result, a running check or the chosen folders change. Not Prefs.changed: that would rebuild
    /// the ◎ menu and every settings page on each step.
    static let changed = Notification.Name("SwivelDoctorChanged")
    /// Posted once when "Check This Mac" has finished every section.
    static let finishedAll = Notification.Name("SwivelDoctorFinishedAll")

    var environment = DoctorEnvironment.live
    private var stored: [DoctorSection: DoctorResult] = [:]
    /// The latest speed test, kept apart from the DNS result so either can be checked again on its own.
    private var speed: DoctorNetwork.SpeedOutcome?
    private var language = Language.resolved
    /// Every section's latest result; Network shows the DNS check with the latest speed test on top.
    var results: [DoctorSection: DoctorResult] {
        var all = stored
        if let speed { all[.network] = DoctorNetwork.withSpeed(stored[.network], speed) }
        return all
    }
    private(set) var running: Set<DoctorSection> = []
    private(set) var testingSpeed = false
    /// Chosen in this session only; nothing about them is saved.
    private(set) var folders: [URL] = []
    /// The app App Check last looked at, also for this session only.
    private(set) var app: URL?
    /// One receipt per Move to Trash, newest last, until Swivel quits.
    private(set) var receipts: [DoctorTrashReceipt] = []
    private var tasks: [DoctorSection: Task<Void, Never>] = [:]
    private var speedTask: Task<Void, Never>?
    private var checkingAll: Set<DoctorSection> = []
    private var observer: NSObjectProtocol?

    private init() {
        observer = NotificationCenter.default.addObserver(forName: Prefs.changed, object: nil, queue: .main) { _ in
            MainActor.assumeIsolated { DoctorCenter.shared.preferencesChanged() }
        }
    }

    var isBusy: Bool { !running.isEmpty || testingSpeed }

    private func preferencesChanged() {
        if !Feature.doctor.isOn { cancelAll() }
        // Results are written in the language they were checked in: a new language starts fresh.
        if Language.resolved != language {
            language = Language.resolved
            cancelAll()
            stored = [:]
            speed = nil
            post()
        }
    }

    // MARK: Running

    /// Every check that reads only this Mac and needs nothing chosen: not App Check, Folder Sizes or Test Speed.
    func runAll() {
        let sections = DoctorSection.allCases.filter(\.runsWithAll)
        checkingAll = Set(sections)
        for section in sections { run(section) }
    }

    /// Checks an app the user picked. Returns false when it isn't an app bundle with a plain path.
    @discardableResult
    func checkApp(_ url: URL) -> Bool {
        guard let app = DoctorCommand.validatedApp(url) else { return false }
        self.app = app
        run(.app)
        return true
    }

    func run(_ section: DoctorSection) {
        guard Feature.doctor.isOn, section != .summary else { return }
        tasks[section]?.cancel()
        running.insert(section)
        post()
        let env = environment
        let folders = folders
        let app = app
        tasks[section] = Task { [weak self] in
            let result: DoctorResult? = switch section {
            case .summary: nil
            case .storage: await DoctorStorage.checkStorage(env)
            case .caches: await DoctorStorage.checkCaches(env)
            case .folders: await DoctorStorage.checkFolders(folders)
            case .performance: await Self.performance(env)
            case .power: await Self.power()
            case .network: await DoctorNetwork.check(dns: try? env.run(.dnsConfiguration), proxy: try? env.run(.proxySettings))
            case .drives: await Self.drives(env)
            case .background: await Self.background(env)
            case .app: if let app { await Self.appCheck(app, env: env) } else { nil }
            }
            guard let self, !Task.isCancelled else { return }
            if let result { stored[section] = result }
            running.remove(section)
            tasks[section] = nil
            finishedOne(section)
            post()
        }
    }

    private func finishedOne(_ section: DoctorSection) {
        guard checkingAll.remove(section) != nil, checkingAll.isEmpty else { return }
        // One sound for the whole check, not one per section.
        Feedback.done()
        NotificationCenter.default.post(name: Self.finishedAll, object: nil)
    }

    func cancel(_ section: DoctorSection) {
        tasks[section]?.cancel()
        tasks[section] = nil
        running.remove(section)
        checkingAll.remove(section)
        post()
    }

    func cancelAll() {
        for task in tasks.values { task.cancel() }
        tasks = [:]
        running = []
        checkingAll = []
        speedTask?.cancel()
        speedTask = nil
        testingSpeed = false
        post()
    }

    /// Apple's network quality test: about 20 seconds of test traffic to Apple's servers.
    func testSpeed() {
        guard Feature.doctor.isOn, !testingSpeed else { return }
        testingSpeed = true
        post()
        let env = environment
        speedTask = Task { [weak self] in
            let output = try? await env.run(.networkQuality)
            guard let self, !Task.isCancelled else { return }
            speed = DoctorNetwork.SpeedOutcome(output)
            testingSpeed = false
            speedTask = nil
            post()
        }
    }

    func stopSpeedTest() {
        speedTask?.cancel()
        speedTask = nil
        testingSpeed = false
        post()
    }

    // MARK: Folders

    func addFolder(_ url: URL) {
        let folder = url.standardizedFileURL
        guard !folders.contains(folder) else { return }
        folders.append(folder)
        run(.folders)
    }

    func removeFolders() {
        folders = []
        cancel(.folders)
        stored[.folders] = nil
        post()
    }

    // MARK: Fixes

    /// Plans and moves the given rows to the Trash. Rows are re-checked here: only catalogue caches and your own
    /// leftover launch agents can be moved, and never while their app runs. Blocking file work runs off the main thread.
    func moveToTrash(_ rows: [DoctorRow]) async throws -> DoctorTrashReceipt {
        guard Feature.doctor.isOn else { throw ToolError(L("This feature is turned off.")) }
        let owners = runningOwners(rows)
        guard owners.isEmpty else { throw ToolError(L("Quit %@ first", owners.joined(separator: ", "))) }
        let env = environment
        let catalogue = ClearDiskCatalog.entries
        let jobs = rows.compactMap { row -> (DoctorTrashTarget, String)? in
            guard let target = row.trash, DoctorTrash.isAllowed(target, catalogue: catalogue) else { return nil }
            let name = catalogue.first { $0.path == target.path }?.name ?? row.title
            return (target, name)
        }
        guard !jobs.isEmpty else { throw ToolError(L("Mac Doctor can’t move these items.")) }
        let receipt = try await Images.detached { () -> DoctorTrashReceipt in
            var total = DoctorTrashReceipt()
            for (target, name) in jobs {
                do {
                    let plan: DoctorTrashPlan
                    if target.mode == .contents {
                        plan = try DoctorTrash.planContents(of: target.path, name: name, home: env.home)
                    } else {
                        // Still the same file, and still a leftover: a reinstalled app's agent is never moved.
                        let file = try DoctorTrash.resolve(target.path, home: env.home, expect: S_IFREG)
                        let item = DoctorBackground.read(file, place: .userAgents, home: env.home)
                        guard item.kind == .regular, item.status == .missing else {
                            throw ToolError(L("Files changed since the check. Run the check again."))
                        }
                        plan = try DoctorTrash.planItem(target.path, home: env.home, expected: target.identity)
                    }
                    let receipt = try DoctorTrash.execute(plan, move: env.trash)
                    total.moved += receipt.moved
                    total.failures += receipt.failures
                } catch {
                    total.failures.append(error.localizedDescription)
                }
            }
            return total
        }
        if !receipt.moved.isEmpty { receipts.append(receipt) }
        for section in Set(rows.map { $0.id.hasPrefix("bg.") ? DoctorSection.background : .caches }) { run(section) }
        post()
        return receipt
    }

    /// Puts the newest move back. Items whose place was taken again stay in the Trash.
    func undoLastMove() async -> (restored: Int, left: [URL]) {
        guard let receipt = receipts.popLast() else { return (0, []) }
        let outcome = await (try? Images.detached { DoctorTrash.undo(receipt) }) ?? (0, receipt.moved.map(\.trashed))
        for section in [DoctorSection.caches, .background] where results[section] != nil { run(section) }
        post()
        return outcome
    }

    /// Names of running apps that own any of these rows.
    func runningOwners(_ rows: [DoctorRow]) -> [String] {
        var names: [String] = []
        for id in Set(rows.flatMap(\.bundleIDs)) {
            for app in NSRunningApplication.runningApplications(withBundleIdentifier: id) {
                if let name = app.localizedName, !names.contains(name) { names.append(name) }
            }
        }
        return names
    }

    /// Ejects the drive holding a volume, as Finder does (never forced). Returns nil when it worked, otherwise why not.
    func eject(_ volume: URL) async -> String? {
        guard Feature.doctor.isOn, let volume = DoctorCommand.validatedVolume(volume),
              DoctorDrives.volumes().contains(where: { $0.mountPoint == volume.path && $0.local }) else {
            return L("Mac Doctor can’t eject this drive.")
        }
        let output = try? await environment.run(.eject(volume))
        run(.drives)
        guard let output else { return L("The drive couldn’t be ejected.") }
        if output.status == 0 { return nil }
        if let holder = DoctorParse.dissenter(output) { return DoctorDrives.holder(holder.name) }
        return L("The drive couldn’t be ejected. Quit apps that use files on it and try again.")
    }

    // MARK: Checks that need several steps

    private static func performance(_ env: DoctorEnvironment) async -> DoctorResult {
        let cpu = (try? await env.run(.processCPU)).map { DoctorParse.processes($0.text) } ?? []
        let memory = (try? await env.run(.processMemory)).map { DoctorParse.processes($0.text) } ?? []
        var result = DoctorPerformance.check(cpu: cpu, memory: memory, state: DoctorPerformance.memory(),
                                             thermal: ProcessInfo.processInfo.thermalState,
                                             uptime: DoctorPerformance.timeSinceRestart())
        result.ran = [DoctorCommand.processCPU.displayString, DoctorCommand.processMemory.displayString]
        return result
    }

    private static func power() async -> DoctorResult {
        let (assertions, battery) = await Task.detached(priority: .utility) {
            (DoctorPower.assertions(), DoctorBattery.health())
        }.value
        return DoctorPower.check(assertions, battery: battery)
    }

    private static func drives(_ env: DoctorEnvironment) async -> DoctorResult {
        // Mounted volumes: only drives plugged into this Mac and network shares. Disk images (opened .dmg files,
        // simulator runtimes) and internal volumes (a Boot Camp partition) are left out.
        var volumes: [DoctorDrives.Volume] = []
        var infos: [String: DoctorParse.DiskInfo] = [:]
        func info(_ disk: String) async -> DoctorParse.DiskInfo? {
            if let known = infos[disk] { return known }
            guard !Task.isCancelled, DoctorCommand.isDiskIdentifier(disk),
                  let output = try? await env.run(.diskInfo(disk)), output.status == 0,
                  let details = DoctorParse.diskInfo(output.stdout) else { return nil }
            infos[disk] = details
            return details
        }
        for var volume in DoctorDrives.volumes() {
            if !volume.local { volumes.append(volume); continue }
            guard let details = await info(volume.device), details.isExternalDrive else { continue }
            // An APFS volume's own whole disk is a synthesized container: Eject takes the physical drive behind it.
            if let store = details.physicalStore, let physical = await info(store) {
                volume.wholeDisk = physical.wholeDisk
            } else {
                volume.wholeDisk = details.wholeDisk
            }
            volumes.append(volume)
        }
        var unmounted: [DoctorDrives.Unmounted] = []
        if !Task.isCancelled, let list = try? await env.run(.externalDisks), list.status == 0 {
            for (disk, partitions) in DoctorParse.externalDisks(list.stdout).sorted(by: { $0.key < $1.key }) {
                guard let details = await info(disk), details.isExternalDrive else { continue }
                if partitions.isEmpty {
                    unmounted.append(.init(identifier: disk, content: "", size: 0, uninitialized: true))
                }
                for part in partitions where part.mountPoint == nil && part.size > 50_000_000
                    && !DoctorDrives.unmountable(part.content, name: part.name) {
                    unmounted.append(.init(identifier: part.identifier, content: part.content, size: part.size))
                }
            }
        }
        var result = DoctorDrives.check(volumes, unmounted: unmounted)
        result.ran = [DoctorCommand.externalDisks.displayString, "/usr/sbin/diskutil info -plist <disk>"]
        return result
    }

    private static func background(_ env: DoctorEnvironment) async -> DoctorResult {
        let items = await Task.detached(priority: .utility) { DoctorBackground.items(home: env.home) }.value
        let jobs = (try? await env.run(.launchdUserJobs)).flatMap { $0.status == 0 ? DoctorParse.launchdJobs($0.text) : nil }
        var systemLoaded = Set<String>()
        for item in items where item.place == .daemons && item.kind == .regular && DoctorCommand.isSafeLabel(item.label) {
            if Task.isCancelled { break }
            if let output = try? await env.run(.launchdSystemService(item.label)), output.status == 0 {
                systemLoaded.insert(item.label)
            }
        }
        var result = DoctorBackground.check(items, loaded: jobs, systemLoaded: systemLoaded, home: env.home)
        result.ran = [DoctorCommand.launchdUserJobs.displayString, "/bin/launchctl print system/<label>"]
        return result
    }

    private static func appCheck(_ app: URL, env: DoctorEnvironment) async -> DoctorResult {
        var findings = await Task.detached(priority: .userInitiated) { DoctorApp.inspect(app, home: env.home) }.value
        var ran: [String] = []
        // An incomplete bundle is never handed to codesign or spctl: they would follow whatever it points at.
        if !findings.iOSApp, findings.bundleComplete, !Task.isCancelled {
            let signature = DoctorCommand.codeSignature(app)
            ran.append(signature.displayString)
            findings.signature = (try? await env.run(signature)).flatMap { DoctorParse.signature($0, app: app) }
            let gatekeeper = DoctorCommand.gatekeeperAssessment(app)
            if !Task.isCancelled {
                ran.append(gatekeeper.displayString)
                if let output = try? await env.run(gatekeeper), !output.timedOut {
                    findings.assessment = DoctorParse.assessment(output, app: app)
                }
            }
        }
        var result = DoctorApp.check(findings, app: app)
        result.ran = ran
        return result
    }

    // MARK: Summary and assistant

    /// Results worth acting on, most serious first, across every section that has run.
    func findings() -> [(section: DoctorSection, row: DoctorRow?, text: String, severity: DoctorSeverity)] {
        var list: [(DoctorSection, DoctorRow?, String, DoctorSeverity)] = []
        for section in DoctorSection.allCases {
            guard let result = results[section], result.severity >= .review else { continue }
            list.append((section, nil, result.headline, result.severity))
        }
        return list.sorted { $0.3 > $1.3 }
    }

    /// The report for the checks that have run, exactly as it would be sent.
    func report(includeNames: Bool) -> String {
        DoctorReport.text(DoctorSection.allCases.compactMap { results[$0] }, includeNames: includeNames)
    }

    /// Stores a result as if its check had just run (tests and previews).
    func record(_ result: DoctorResult) {
        stored[result.section] = result
        post()
    }

    private func post() {
        NotificationCenter.default.post(name: Self.changed, object: nil)
    }
}
