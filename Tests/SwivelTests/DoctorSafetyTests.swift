import Foundation
import Testing
import DoctorCatalog
@testable import Swivel

/// Mac Doctor's safety rules: fixed commands only, Trash moves that can't be redirected, no reads of protected
/// places, and no "Open Anyway" advice for apps Apple has blocked. Every file test runs in a temporary home folder
/// with an injected Trash, so the real ~/.Trash and ~/Library are never touched.
@Suite(.serialized)
@MainActor
struct DoctorSafetyTests {
    private func temporaryHome() throws -> URL {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("SwivelDoctor-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return try DoctorTrash.realHome(url)
    }

    private func file(_ url: URL, _ text: String = "cache data") throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data(text.utf8).write(to: url)
    }

    /// A Trash stand-in: moves items into `<home>/FakeTrash` and returns the new place.
    private func fakeTrash(_ home: URL) throws -> DoctorTrash.Mover {
        let trash = home.appendingPathComponent("FakeTrash")
        try FileManager.default.createDirectory(at: trash, withIntermediateDirectories: true)
        return { url in
            let target = trash.appendingPathComponent(url.lastPathComponent + "-" + UUID().uuidString.prefix(4))
            try FileManager.default.moveItem(at: url, to: target)
            return target
        }
    }

    // MARK: Commands

    private let sampleApp = URL(fileURLWithPath: "/System/Applications/Calculator.app")

    /// One sample per case, with no `default`: a new DoctorCommand case doesn't compile until it is listed here.
    private func sample(_ command: DoctorCommand) -> DoctorCommand {
        switch command {
        case .localSnapshots, .dnsConfiguration, .proxySettings, .launchdUserJobs, .processCPU, .processMemory,
             .networkQuality, .externalDisks: command
        case .launchdSystemService: .launchdSystemService("com.example.agent")
        case .gatekeeperAssessment: .gatekeeperAssessment(sampleApp)
        case .codeSignature: .codeSignature(sampleApp)
        case .diskInfo: .diskInfo("disk4")
        case .eject: .eject(URL(fileURLWithPath: "/Volumes/Backup"))
        }
    }

    private var allCommands: [DoctorCommand] {
        [.localSnapshots, .dnsConfiguration, .proxySettings, .launchdUserJobs, .launchdSystemService(""), .processCPU,
         .processMemory, .networkQuality, .gatekeeperAssessment(sampleApp), .codeSignature(sampleApp), .externalDisks,
         .diskInfo(""), .eject(sampleApp)].map(sample)
    }

    @Test func everyCommandIsOneAllowedToolAndVerb() {
        // An allow-list, so a state-changing verb (thinlocalsnapshots, unmountDisk, bootout, --remove-signature) fails.
        let allowed: [String: Set<String>] = [
            "/usr/bin/tmutil": ["listlocalsnapshots"], "/usr/sbin/scutil": ["--dns", "--proxy"],
            "/bin/launchctl": ["list", "print"], "/usr/bin/top": ["-l"], "/usr/bin/networkQuality": ["-M"],
            "/usr/sbin/spctl": ["--assess"], "/usr/bin/codesign": ["--verify"], "/usr/sbin/diskutil": ["list", "info", "eject"],
        ]
        for command in allCommands {
            let verbs = allowed[command.tool]
            #expect(verbs?.contains(command.arguments.first ?? "") == true, "\(command.tool) \(command.arguments)")
        }
        // Every argument is pinned, not only the verb.
        let pinned: [String: [String]] = [
            "/usr/bin/tmutil": ["listlocalsnapshots", "/"], "/usr/bin/networkQuality": ["-M", "30", "-c"],
            "/usr/bin/top": [], "/bin/launchctl": [], "/usr/sbin/scutil": [], "/usr/sbin/diskutil": [], "/usr/sbin/spctl": [], "/usr/bin/codesign": [],
        ]
        #expect(DoctorCommand.localSnapshots.arguments == pinned["/usr/bin/tmutil"])
        #expect(DoctorCommand.networkQuality.arguments == pinned["/usr/bin/networkQuality"])
        #expect(DoctorCommand.dnsConfiguration.arguments == ["--dns"] && DoctorCommand.proxySettings.arguments == ["--proxy"])
        #expect(DoctorCommand.launchdUserJobs.arguments == ["list"])
        #expect(DoctorCommand.processCPU.arguments == ["-l", "2", "-s", "1", "-n", "12", "-o", "cpu", "-stats", "pid,cpu,mem,command"])
        #expect(DoctorCommand.processMemory.arguments == ["-l", "1", "-n", "12", "-o", "mem", "-stats", "pid,cpu,mem,command"])
        #expect(DoctorCommand.gatekeeperAssessment(sampleApp).arguments == ["--assess", "--type", "execute", "--raw", "--no-cache", "--", sampleApp.path])
        #expect(DoctorCommand.codeSignature(sampleApp).arguments == ["--verify", "--deep", "--", sampleApp.path])
        #expect(DoctorCommand.externalDisks.arguments == ["list", "-plist", "external"])
        #expect(DoctorCommand.diskInfo("disk4").arguments == ["info", "-plist", "disk4"])
        #expect(DoctorCommand.eject(URL(fileURLWithPath: "/Volumes/Backup")).arguments == ["eject", "/Volumes/Backup"])
        #expect(DoctorCommand.launchdSystemService("x").arguments == ["print", "system/x"])
        #expect(DoctorCommand.processCPU.arguments.contains("-stats") && !DoctorCommand.processCPU.arguments.contains("-k"))
        #expect(!DoctorCommand.networkQuality.arguments.contains("-C") && !DoctorCommand.networkQuality.arguments.contains("-k"))
    }

    @Test func everyCommandIsAFixedSystemToolThatNeedsNoAdministrator() {
        let forbidden = ["sudo", "rm", "osascript", "sh", "bash", "zsh", "xattr", "--master-disable", "deletelocalsnapshots",
                         "resetbtm", "dumpbtm", "--strict", "-f", "force", "erase", "kill"]
        for command in allCommands {
            #expect(["/usr/bin/", "/bin/", "/usr/sbin/"].contains { command.tool.hasPrefix($0) }, "\(command)")
            for argument in command.arguments { #expect(!forbidden.contains(argument), "\(command) \(argument)") }
            #expect(!command.tool.hasSuffix("/sh") && !command.tool.hasSuffix("stapler"))
        }
        #expect(allCommands.filter(\.changesState) == [.eject(URL(fileURLWithPath: "/Volumes/Backup"))])
        #expect(Set(allCommands.filter(\.usesNetwork).map(\.tool)) == ["/usr/bin/networkQuality", "/usr/sbin/spctl"])
        // A picked path always comes after "--", so it can never read as an option.
        #expect(DoctorCommand.gatekeeperAssessment(sampleApp).arguments.suffix(2) == ["--", sampleApp.path])
        #expect(DoctorCommand.codeSignature(sampleApp).arguments.suffix(2) == ["--", sampleApp.path])
        #expect(DoctorCommand.gatekeeperAssessment(sampleApp).arguments.contains("--raw"))
        #expect(DoctorCommand.gatekeeperAssessment(sampleApp).arguments.contains("--no-cache"))
    }

    @Test func administratorCommandsAreFixedTextThatNothingCanRun() throws {
        for admin in DoctorAdminCommand.all { #expect(admin.command.hasPrefix("sudo ")) }
        // No runnable command contains what the admin commands do.
        let adminWords = ["sudo", "deletelocalsnapshots", "dscacheutil", "killall", "mDNSResponder", "sfltool", "dumpbtm", "resetbtm"]
        for command in allCommands {
            for word in adminWords {
                #expect(!command.tool.contains(word) && !command.arguments.contains { $0.contains(word) }, "\(command)")
            }
        }
        // And DoctorAdminCommand values are only ever the three constants, never built at run time.
        let sources = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
            .appendingPathComponent("Sources/Swivel")
        for name in try FileManager.default.contentsOfDirectory(atPath: sources.path) where name.hasPrefix("Doctor") {
            let text = try String(contentsOf: sources.appendingPathComponent(name), encoding: .utf8)
            let constructions = text.components(separatedBy: "DoctorAdminCommand(").count - 1
            #expect(constructions == (name == "DoctorCommands.swift" ? 3 : 0), "\(name) builds \(constructions) admin commands")
        }
    }

    @Test func pickedPathsLabelsAndDisksMustBePlain() throws {
        let home = try temporaryHome()
        defer { try? FileManager.default.removeItem(at: home) }
        let good = home.appendingPathComponent("Good.app")
        try FileManager.default.createDirectory(at: good, withIntermediateDirectories: true)
        #expect(DoctorCommand.validatedApp(good)?.path == good.path)
        for name in ["Bad\r.app", "Bad\u{2028}.app", "Bad\u{202E}ppa.app", "Bad\u{85}.app", "Bad\n.app"] {
            let bad = home.appendingPathComponent(name)
            try FileManager.default.createDirectory(at: bad, withIntermediateDirectories: true)
            #expect(DoctorCommand.validatedApp(bad) == nil, "\(name.debugDescription)")
        }
        let notApp = home.appendingPathComponent("Folder")
        try FileManager.default.createDirectory(at: notApp, withIntermediateDirectories: true)
        #expect(DoctorCommand.validatedApp(notApp) == nil)
        // A link to an app is checked as the app itself; a dangling one is refused.
        let link = home.appendingPathComponent("Link.app")
        try FileManager.default.createSymbolicLink(at: link, withDestinationURL: good)
        #expect(DoctorCommand.validatedApp(link)?.path == good.path)
        let dangling = home.appendingPathComponent("Gone.app")
        try FileManager.default.createSymbolicLink(atPath: dangling.path, withDestinationPath: home.appendingPathComponent("Nowhere.app").path)
        #expect(DoctorCommand.validatedApp(dangling) == nil)

        for label in ["com.example.agent", "org.mozilla.updater-2"] { #expect(DoctorCommand.isSafeLabel(label)) }
        for label in ["x; sudo sh -c \"$(curl host)\"", "a$(b)", "a`b`", "a\nb", "com.\u{202E}elppa", "-flag", "../system/x", ""] {
            #expect(!DoctorCommand.isSafeLabel(label), "\(label.debugDescription)")
        }
        #expect(DoctorCommand.isDiskIdentifier("disk4") && DoctorCommand.isDiskIdentifier("disk12s3"))
        #expect(!DoctorCommand.isDiskIdentifier("disk4; ls") && !DoctorCommand.isDiskIdentifier("/dev/disk4"))
        #expect(DoctorCommand.validatedVolume(URL(fileURLWithPath: "/Volumes/Backup")) != nil)
        #expect(DoctorCommand.validatedVolume(URL(fileURLWithPath: "/Volumes/A/B")) == nil)
        #expect(DoctorCommand.validatedVolume(URL(fileURLWithPath: "/Users/me")) == nil)
    }

    // MARK: Runner

    @Test func captureReadsLargeOutputInOrderAndReportsExitStatus() async throws {
        let output = try await Shell.capture("/usr/bin/perl", ["-e", "print qq($_\\n) for 1..60000; print STDERR qq(done\\n); exit 3"],
                                             timeout: 30, limit: 4 << 20)
        #expect(output.status == 3)
        let lines = output.text.split(separator: "\n")
        #expect(lines.count == 60000 && lines.first == "1" && lines.last == "60000")
        #expect(output.errorText.contains("done") && !output.truncated && !output.timedOut)
        let capped = try await Shell.capture("/usr/bin/perl", ["-e", "print qq(x) x 200000"], timeout: 30, limit: 1000)
        #expect(capped.stdout.count == 1000 && capped.truncated && capped.status == 0)
    }

    @Test func captureStopsAToolThatRunsTooLongAndHonoursCancellation() async throws {
        let start = Date()
        let slow = try await Shell.capture("/bin/sleep", ["30"], timeout: 1, limit: 1000)
        #expect(slow.timedOut && Date().timeIntervalSince(start) < 10)
        let task = Task { try await Shell.capture("/bin/sleep", ["30"], timeout: 60, limit: 1000) }
        try await Task.sleep(for: .milliseconds(200))
        task.cancel()
        await #expect(throws: CancellationError.self) { _ = try await task.value }
        #expect(Date().timeIntervalSince(start) < 15)
    }

    // MARK: Trash

    @Test func cleaningMovesACachesContentsAsOneFolderAndUndoPutsThemBack() throws {
        let home = try temporaryHome()
        defer { try? FileManager.default.removeItem(at: home) }
        let cache = home.appendingPathComponent("Library/Caches/Homebrew")
        try file(cache.appendingPathComponent("downloads/a.tar.gz"))
        try file(cache.appendingPathComponent("b.json"))
        let plan = try DoctorTrash.planContents(of: "~/Library/Caches/Homebrew", name: "Homebrew Cache", home: home)
        #expect(plan.items.count == 2 && plan.skipped == 0)
        let receipt = try DoctorTrash.execute(plan, move: fakeTrash(home))
        #expect(receipt.failures.isEmpty && receipt.moved.count == 1 && receipt.movedItems == 2)
        #expect(try FileManager.default.contentsOfDirectory(atPath: cache.path).isEmpty)  // the folder stays, empty
        let trashed = try #require(receipt.moved.first?.trashed)
        #expect(trashed.lastPathComponent.hasPrefix(DoctorTrash.collectorName("Homebrew Cache")))
        #expect(DoctorTrash.collectorName("Google Chrome") == DoctorText.fileName(L("%@ cache (Mac Doctor)", "Google Chrome")))
        #expect(Set(try FileManager.default.contentsOfDirectory(atPath: trashed.path)) == ["downloads", "b.json"])

        let outcome = DoctorTrash.undo(receipt)
        #expect(outcome.restored == 2 && outcome.left.isEmpty)
        #expect(Set(try FileManager.default.contentsOfDirectory(atPath: cache.path)) == ["downloads", "b.json"])
    }

    @Test func undoNeverOverwritesWhatTheAppRebuilt() throws {
        let home = try temporaryHome()
        defer { try? FileManager.default.removeItem(at: home) }
        let cache = home.appendingPathComponent("Library/Caches/pip")
        try file(cache.appendingPathComponent("wheels"), "old")
        let receipt = try DoctorTrash.execute(try DoctorTrash.planContents(of: "~/Library/Caches/pip", name: "pip Cache", home: home),
                                              move: fakeTrash(home))
        try file(cache.appendingPathComponent("wheels"), "rebuilt")
        let outcome = DoctorTrash.undo(receipt)
        #expect(outcome.restored == 0 && outcome.left.count == 1)
        #expect(try String(contentsOf: cache.appendingPathComponent("wheels"), encoding: .utf8) == "rebuilt")
    }

    @Test func linksOtherVolumesAndSwappedFoldersAreNeverFollowed() throws {
        let home = try temporaryHome()
        defer { try? FileManager.default.removeItem(at: home) }
        let documents = home.appendingPathComponent("Documents")
        try file(documents.appendingPathComponent("thesis.txt"), "precious")
        // A cache folder that is a link to Documents is refused outright.
        try FileManager.default.createDirectory(at: home.appendingPathComponent("Library/Caches"), withIntermediateDirectories: true)
        try FileManager.default.createSymbolicLink(at: home.appendingPathComponent("Library/Caches/Yarn"), withDestinationURL: documents)
        #expect(throws: ToolError.self) { try DoctorTrash.planContents(of: "~/Library/Caches/Yarn", name: "Yarn", home: home) }
        // A link inside a cache is left in place, and its target survives.
        let cache = home.appendingPathComponent("Library/Caches/CocoaPods")
        try file(cache.appendingPathComponent("Specs/x"))
        try FileManager.default.createSymbolicLink(at: cache.appendingPathComponent("link"), withDestinationURL: documents)
        let plan = try DoctorTrash.planContents(of: "~/Library/Caches/CocoaPods", name: "CocoaPods", home: home)
        #expect(plan.items.map(\.url.lastPathComponent) == ["Specs"] && plan.skipped == 1)
        // The cache folder is swapped for a link between the check and the move: nothing moves.
        try FileManager.default.moveItem(at: cache, to: home.appendingPathComponent("Library/Caches/CocoaPods-real"))
        try FileManager.default.createSymbolicLink(at: cache, withDestinationURL: documents)
        #expect(throws: ToolError.self) { try DoctorTrash.execute(plan, move: try fakeTrash(home)) }
        #expect(FileManager.default.fileExists(atPath: documents.appendingPathComponent("thesis.txt").path))
    }

    @Test func anItemChangedSinceTheCheckIsLeftInPlace() throws {
        let home = try temporaryHome()
        defer { try? FileManager.default.removeItem(at: home) }
        let cache = home.appendingPathComponent(".npm/_cacache")
        try file(cache.appendingPathComponent("index"))
        try file(cache.appendingPathComponent("content"))
        let plan = try DoctorTrash.planContents(of: "~/.npm/_cacache", name: "npm Cache", home: home)
        try FileManager.default.removeItem(at: cache.appendingPathComponent("index"))
        try file(cache.appendingPathComponent("index"), "new file under the old name")
        let receipt = try DoctorTrash.execute(plan, move: fakeTrash(home))
        #expect(receipt.movedItems == 1 && receipt.failures.count == 1)
        #expect(FileManager.default.fileExists(atPath: cache.appendingPathComponent("index").path))
    }

    @Test func aTrashFailurePutsEverythingBack() throws {
        let home = try temporaryHome()
        defer { try? FileManager.default.removeItem(at: home) }
        let cache = home.appendingPathComponent("Library/Caches/go-build")
        try file(cache.appendingPathComponent("00/a"))
        let failing: DoctorTrash.Mover = { _ in throw ToolError("no Trash") }
        let receipt = try DoctorTrash.execute(try DoctorTrash.planContents(of: "~/Library/Caches/go-build", name: "Go", home: home),
                                              move: failing)
        #expect(receipt.moved.isEmpty && receipt.failures.count == 1)
        #expect(try FileManager.default.contentsOfDirectory(atPath: cache.path) == ["00"])
    }

    @Test func onlyCatalogueCachesAndYourOwnLaunchAgentsCanBeMoved() {
        #expect(DoctorTrash.isAllowed(DoctorTrashTarget(path: "~/Library/Caches/Homebrew", mode: .contents)))
        #expect(!DoctorTrash.isAllowed(DoctorTrashTarget(path: "~/Library/LaunchAgents", mode: .contents)))
        #expect(!DoctorTrash.isAllowed(DoctorTrashTarget(path: "~/Documents", mode: .contents)))
        #expect(!DoctorTrash.isAllowed(DoctorTrashTarget(path: "~/Library/Caches", mode: .contents)))
        #expect(DoctorTrash.isAllowed(DoctorTrashTarget(path: "~/Library/LaunchAgents/com.example.agent.plist", mode: .item)))
        #expect(!DoctorTrash.isAllowed(DoctorTrashTarget(path: "~/Library/LaunchAgents/sub/x.plist", mode: .item)))
        #expect(!DoctorTrash.isAllowed(DoctorTrashTarget(path: "~/Library/Preferences/x.plist", mode: .item)))
        #expect(!DoctorTrash.isAllowed(DoctorTrashTarget(path: "~/Library/Caches/Homebrew", mode: .item)))
    }

    @Test func aLeftoverAgentMovesAsOneFileAndRefusesSwapsAndReplacements() throws {
        let home = try temporaryHome()
        defer { try? FileManager.default.removeItem(at: home) }
        let thesis = home.appendingPathComponent("Documents/thesis.txt")
        try file(thesis, "precious")
        let agent = home.appendingPathComponent("Library/LaunchAgents/gone.plist")
        try file(agent, "<plist/>")
        let identity = try FileIdentity.read(agent)
        let receipt = try DoctorTrash.execute(try DoctorTrash.planItem("~/Library/LaunchAgents/gone.plist", home: home, expected: identity),
                                              move: fakeTrash(home))
        #expect(receipt.moved.count == 1 && !FileManager.default.fileExists(atPath: agent.path))
        #expect(DoctorTrash.undo(receipt).restored == 1 && FileManager.default.fileExists(atPath: agent.path))
        // A different file under the same name (the app was reinstalled) is never moved.
        try FileManager.default.removeItem(at: agent)
        try file(agent, "<plist/>")
        #expect(throws: ToolError.self) { try DoctorTrash.planItem("~/Library/LaunchAgents/gone.plist", home: home, expected: identity) }
        // A link in place of the file, or of its folder, is refused; the target survives.
        let plan = try DoctorTrash.planItem("~/Library/LaunchAgents/gone.plist", home: home)
        try FileManager.default.removeItem(at: agent)
        try FileManager.default.createSymbolicLink(at: agent, withDestinationURL: thesis)
        #expect(throws: ToolError.self) { try DoctorTrash.execute(plan, move: try fakeTrash(home)) }
        #expect(throws: ToolError.self) { try DoctorTrash.planItem("~/Library/LaunchAgents/gone.plist", home: home) }
        let folder = home.appendingPathComponent("Library/LaunchAgents")
        try FileManager.default.moveItem(at: folder, to: home.appendingPathComponent("Library/Agents-real"))
        try FileManager.default.createSymbolicLink(at: folder, withDestinationURL: home.appendingPathComponent("Documents"))
        #expect(throws: ToolError.self) { try DoctorTrash.planItem("~/Library/LaunchAgents/thesis.txt", home: home) }
        #expect(try String(contentsOf: thesis, encoding: .utf8) == "precious")
    }

    @Test func undoRefusesWhenTheCacheFolderWasSwappedForALink() throws {
        let home = try temporaryHome()
        defer { try? FileManager.default.removeItem(at: home) }
        let cache = home.appendingPathComponent("Library/Caches/Homebrew")
        try file(cache.appendingPathComponent("a"))
        let receipt = try DoctorTrash.execute(try DoctorTrash.planContents(of: "~/Library/Caches/Homebrew", name: "Homebrew Cache", home: home),
                                              move: fakeTrash(home))
        try FileManager.default.removeItem(at: cache)
        try FileManager.default.createDirectory(at: home.appendingPathComponent("Documents"), withIntermediateDirectories: true)
        try FileManager.default.createSymbolicLink(at: cache, withDestinationURL: home.appendingPathComponent("Documents"))
        let outcome = DoctorTrash.undo(receipt)
        #expect(outcome.restored == 0 && outcome.left.count == 1)
        #expect(try FileManager.default.contentsOfDirectory(atPath: home.appendingPathComponent("Documents").path).isEmpty)
    }

    // MARK: Protected places

    @Test func protectedPlacesAreSkippedWhileSizingAFolder() throws {
        let home = try temporaryHome()
        defer { try? FileManager.default.removeItem(at: home) }
        for place in ["Documents", "Library/Containers/com.example", "Library/Mail"] {
            try file(home.appendingPathComponent(place).appendingPathComponent("private.bin"), String(repeating: "x", count: 100_000))
        }
        try file(home.appendingPathComponent("Library/Caches/thing/data.bin"), String(repeating: "y", count: 100_000))
        #expect(DoctorPrivacy.isProtected(home.appendingPathComponent("Documents/a"), home: home))
        #expect(DoctorPrivacy.isProtected(URL(fileURLWithPath: "/Volumes/USB"), home: home))
        #expect(DoctorPrivacy.isProtected(home.appendingPathComponent("Pictures/Photos Library.photoslibrary"), home: home))
        #expect(!DoctorPrivacy.isProtected(home.appendingPathComponent("Library/Caches/thing"), home: home))
        let size = DoctorStorage.size(of: home, home: home)
        #expect(size.skipped == 3)
        #expect(size.bytes >= 100_000 && size.bytes < 200_000)
        // Other spellings of the same places: the Data volume's firmlink, "..", another case, network mounts.
        let data = URL(fileURLWithPath: "/System/Volumes/Data" + DoctorPrivacy.canonical(home.path))
        for spelling in [data.appendingPathComponent("Documents/a").path, home.path + "/Library/../Documents/a",
                         home.path.uppercased() + "/DESKTOP", "/System/Volumes/Data/Volumes/USB", "/Network", "/net/host/x"] {
            #expect(DoctorPrivacy.isProtected(URL(fileURLWithPath: spelling), home: home), "\(spelling)")
        }
        let throughData = DoctorStorage.size(of: data, home: home)
        #expect(throughData.skipped == 3 && throughData.bytes < 200_000)
        // A protected folder the user picked themselves is looked into.
        let picked = DoctorStorage.size(of: home.appendingPathComponent("Documents"), home: home)
        #expect(picked.skipped == 0 && picked.bytes >= 100_000)
    }

    @Test func everyCatalogueEntryIsANarrowCacheFolderOutsideProtectedPlaces() {
        let entries = ClearDiskCatalog.entries
        #expect(entries.count == 46)
        #expect(Set(entries.map(\.id)).count == entries.count && Set(entries.map(\.path)).count == entries.count)
        let home = URL(fileURLWithPath: "/Users/someone")
        let allowedSupport: Set<String> = ["Cache", "Code Cache", "GPUCache", "htmlcache", "CachedData", "CachedExtensionVSIXs"]
        for entry in entries {
            #expect(entry.path.hasPrefix("~/") && !entry.path.contains("..") && entry.path.split(separator: "/").count >= 3, "\(entry.id)")
            #expect(!entry.path.contains("/Containers/") && !entry.path.contains("Group Containers"), "\(entry.id)")
            if entry.path.hasPrefix("~/Library/Application Support/") {
                #expect(allowedSupport.contains(String(entry.path.split(separator: "/").last!)), "\(entry.id)")
            }
            #expect(DoctorTrash.isAllowed(DoctorTrashTarget(path: entry.path, mode: .contents)))
            let url = home.appendingPathComponent(String(entry.path.dropFirst(2)))
            #expect(!DoctorPrivacy.isProtected(url, home: home) || entry.id == "safari-cache", "\(entry.id)")
            for other in entries where other.id != entry.id { #expect(!other.path.hasPrefix(entry.path + "/"), "\(entry.id) contains \(other.id)") }
            if entry.section == .app { #expect(entry.removes != nil && entry.keeps != nil && entry.note != nil, "\(entry.id)") }
        }
    }

    // MARK: Background items

    private func plist(_ url: URL, _ object: [String: Any]) throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try PropertyListSerialization.data(fromPropertyList: object, format: .xml, options: 0).write(to: url)
    }

    @Test func onlyAnAbsoluteProgramThatIsReallyGoneCountsAsLeftOver() throws {
        let home = try temporaryHome()
        defer { try? FileManager.default.removeItem(at: home) }
        let agents = home.appendingPathComponent("Library/LaunchAgents")
        try plist(agents.appendingPathComponent("gone.plist"), ["Label": "com.example.gone", "Program": home.path + "/Applications/Gone.app/Contents/MacOS/Gone"])
        try plist(agents.appendingPathComponent("bare.plist"), ["Label": "com.example.bare", "ProgramArguments": ["bash", "-c", "true"]])
        try plist(agents.appendingPathComponent("docs.plist"), ["Label": "com.example.docs", "Program": home.path + "/Documents/run.sh"])
        try plist(agents.appendingPathComponent("bundle.plist"), ["Label": "com.example.bundle", "BundleProgram": "Contents/MacOS/Helper"])
        try plist(agents.appendingPathComponent("empty.plist"), [:])
        try FileManager.default.createSymbolicLink(atPath: agents.appendingPathComponent("dangling.plist").path,
                                                   withDestinationPath: home.appendingPathComponent("nowhere.plist").path)
        func item(_ name: String) -> DoctorBackground.Item {
            DoctorBackground.read(agents.appendingPathComponent(name), place: .userAgents, home: home)
        }
        #expect(item("gone.plist").status == .missing)
        #expect(item("bare.plist").status == .present)
        #expect(item("docs.plist").status == .cantCheck)  // in Documents: not even looked at
        #expect(item("bundle.plist").status == .managedByApp)
        #expect(item("empty.plist").kind == .inert)
        #expect(item("dangling.plist").kind == .danglingLink)

        let items = ["gone.plist", "bare.plist", "docs.plist", "bundle.plist", "empty.plist", "dangling.plist"].map(item)
        let result = DoctorBackground.check(items, loaded: [:], systemLoaded: [], home: home)
        let movable = result.rows.filter { $0.trash != nil }
        #expect(movable.count == 1 && movable[0].trash?.path == "~/Library/LaunchAgents/gone.plist")
        #expect(result.adminCommands == [.listBackgroundItems])
    }

    @Test func plistReaderRefusesFIFOsHugeFilesAndDeepNesting() throws {
        let home = try temporaryHome()
        defer { try? FileManager.default.removeItem(at: home) }
        let fifo = home.appendingPathComponent("fifo.plist")
        #expect(mkfifo(fifo.path, 0o600) == 0)
        let start = Date()
        #expect(DoctorBackground.readSmallFile(fifo) == nil)
        #expect(Date().timeIntervalSince(start) < 2)
        let huge = home.appendingPathComponent("huge.plist")
        try Data(count: 70_000).write(to: huge)
        #expect(DoctorBackground.readSmallFile(huge) == nil)
        let deep = String(repeating: "<array>", count: 40) + String(repeating: "</array>", count: 40)
        #expect(DoctorBackground.parse(Data(("<plist><dict><key>a</key>" + deep + "</dict></plist>").utf8)) == nil)
    }

    @Test func disguisedNamesAreFlagged() throws {
        let home = try temporaryHome()
        defer { try? FileManager.default.removeItem(at: home) }
        let file = home.appendingPathComponent("Library/LaunchAgents/x.plist")
        let items = [
            DoctorBackground.Item(file: file, place: .userAgents, kind: .regular, label: "com.\u{0430}pple.update", program: "/bin/ls",
                                  status: .present, runsAtLoad: true, keepsAlive: false, disabled: false, app: nil),
            DoctorBackground.Item(file: file, place: .userAgents, kind: .regular, label: "com.apple.softwareupdate", program: "/bin/ls",
                                  status: .present, runsAtLoad: true, keepsAlive: false, disabled: false, app: nil),
            DoctorBackground.Item(file: file, place: .userAgents, kind: .regular, label: "x\u{202E}gpj.exe", program: "/bin/ls",
                                  status: .present, runsAtLoad: true, keepsAlive: false, disabled: false, app: nil),
        ]
        let result = DoctorBackground.check(items, loaded: nil, systemLoaded: [], home: home)
        #expect(result.rows.allSatisfy { $0.severity == .review })
        #expect(result.rows.contains { $0.title.contains("\\u{202E}") })
        #expect(result.names.allSatisfy { $0 == "<unusual name>" || $0 == "com.apple.softwareupdate" })
    }

    // MARK: Apps that won't open

    private func findings(signature: DoctorParse.Signature?, verdict: DoctorParse.Assessment.Verdict?, source: String? = nil,
                          adHoc: Bool = false, signed: Bool = true, quarantine: UInt32? = 0x0083) -> DoctorApp.Findings {
        DoctorApp.Findings(name: "Sample", iOSApp: false, bundleComplete: true, adHoc: adHoc, hasSignature: signed,
                           signature: signature, assessment: verdict.map { .init(verdict: $0, source: source) },
                           quarantine: quarantine.map { .init(flags: $0, date: nil, agent: "Safari") }, intelOnly: false,
                           rosettaInstalled: true, minimumSystem: nil, tooNew: false, temporaryLocation: false)
    }

    /// Everything the section shows: headline, notes, row texts.
    private func text(_ result: DoctorResult) -> String {
        ([result.headline] + result.notes + result.rows.flatMap { [$0.title, $0.value, $0.detail] }).joined(separator: "\n")
    }

    @Test func appsAppleBlockedAreNeverSteeredTowardsOpenAnyway() {
        let openAnyway = L("To open it anyway: 1. Try to open the app once and close the message. 2. Open Privacy & Security and scroll down. 3. Choose Open Anyway and enter your password. The button appears for about an hour after the first try.")
        let app = URL(fileURLWithPath: "/Applications/Sample.app")
        for revoked in [findings(signature: .revoked, verdict: .rejected), findings(signature: .valid, verdict: .revoked)] {
            let result = DoctorApp.check(revoked, app: app)
            #expect(result.severity == .problem)
            #expect(result.headline == L("Apple has blocked this app as harmful. Move it to the Trash and don’t open it."))
            #expect(!text(result).contains(openAnyway))
            #expect(result.report.contains(L("Apple has blocked this app as harmful: never suggest opening it.")))
        }
        // Only a valid, identified, unnotarized app on its first open gets the steps, framed as a risk decision.
        let unnotarized = DoctorApp.check(findings(signature: .valid, verdict: .rejected, source: "Unnotarized Developer ID"), app: app)
        #expect(text(unnotarized).contains(openAnyway) && unnotarized.panes == [.privacy])
        let others = [findings(signature: .unsigned, verdict: .rejected, signed: false),
                      findings(signature: .valid, verdict: .rejected, adHoc: true),
                      // A self-signed app: intact, but Gatekeeper names no source, so not an identified developer.
                      findings(signature: .valid, verdict: .rejected),
                      findings(signature: .modified, verdict: .invalidSignature),
                      findings(signature: .other("x"), verdict: .invalidSignature),
                      findings(signature: .revoked, verdict: .rejected),
                      findings(signature: .valid, verdict: .rejected, source: "Unnotarized Developer ID", quarantine: 0x00c3)]
        for other in others {
            let result = DoctorApp.check(other, app: app)
            #expect(!text(result).contains(openAnyway) && !result.panes.contains(.privacy), "\(other.signature as Any)")
            #expect(!DoctorReport.soundsRisky(text(result)))
            #expect(result.adminCommands.isEmpty)
        }
        let selfSigned = DoctorApp.check(findings(signature: .valid, verdict: .rejected), app: app)
        #expect(selfSigned.rows.contains { $0.detail == L("Signed, but not by a developer Apple has identified.") })
        #expect(!text(selfSigned).contains(L("Signed by Apple.")))
        #expect(DoctorApp.check(findings(signature: .other("x"), verdict: .invalidSignature), app: app).severity == .problem)
        // No downloaded flag: Gatekeeper doesn't stop it at launch, so it isn't "blocked".
        let local = DoctorApp.check(findings(signature: .valid, verdict: .rejected, adHoc: true, quarantine: nil), app: app)
        #expect(local.severity == .info && local.headline == L("Gatekeeper wouldn’t approve this app, but it has no downloaded flag, so macOS opens it without asking. If it still won’t open, it may be crashing: try reinstalling it."))
        // Gatekeeper never answered: say so, never "accepts".
        let unanswered = DoctorApp.check(findings(signature: .valid, verdict: nil), app: app)
        #expect(unanswered.headline == L("Gatekeeper’s check didn’t finish, so Mac Doctor can’t say yet whether macOS allows this app. Try again; large apps can take a few minutes."))
        let accepted = DoctorApp.check(findings(signature: .valid, verdict: .accepted, source: "Notarized Developer ID"), app: app)
        #expect(accepted.severity == .ok)
        #expect(accepted.headline == L("Gatekeeper accepts this app’s signature. The first time you open it, macOS asks once to confirm — that is normal. This is not a malware scan."))
    }

    @Test func inspectingAnAppReadsItsFlagsWithoutFollowingItsInfoPlist() throws {
        let home = try temporaryHome()
        defer { try? FileManager.default.removeItem(at: home) }
        let app = home.appendingPathComponent("Fake.app")
        let contents = app.appendingPathComponent("Contents")
        try plist(contents.appendingPathComponent("Info.plist"), ["CFBundleExecutable": "../../../Documents/x", "CFBundleName": "Fake",
                                                                    "LSMinimumSystemVersion": "99.0"])
        try file(contents.appendingPathComponent("MacOS/Fake"))
        let flag = "00c3;6720b1c2;Safari;"
        #expect(setxattr(app.path, "com.apple.quarantine", flag, flag.utf8.count, 0, XATTR_NOFOLLOW) == 0)
        let findings = DoctorApp.inspect(try #require(DoctorCommand.validatedApp(app)), home: home)
        #expect(!findings.bundleComplete)  // an executable name with "/" is refused, not followed
        #expect(findings.quarantine?.approved == true && findings.quarantine?.agent == "Safari")
        #expect(findings.tooNew && findings.signer == .none && !findings.hasSignature)
        // A FIFO as the program file: never opened (Security would block on it forever).
        let fifoApp = home.appendingPathComponent("Fifo.app")
        try plist(fifoApp.appendingPathComponent("Contents/Info.plist"), ["CFBundleExecutable": "Fifo"])
        try FileManager.default.createDirectory(at: fifoApp.appendingPathComponent("Contents/MacOS"), withIntermediateDirectories: true)
        #expect(mkfifo(fifoApp.appendingPathComponent("Contents/MacOS/Fifo").path, 0o600) == 0)
        let start = Date()
        #expect(!DoctorApp.inspect(fifoApp, home: home).bundleComplete)
        #expect(Date().timeIntervalSince(start) < 2)

        let wrapped = home.appendingPathComponent("Phone.app")
        try FileManager.default.createDirectory(at: wrapped.appendingPathComponent("Wrapper/Inner.app"), withIntermediateDirectories: true)
        #expect(DoctorApp.inspect(wrapped, home: home).iOSApp)
    }

    @Test func architecturesComeFromTheProgramsOwnHeader() {
        let calculator = URL(fileURLWithPath: "/System/Applications/Calculator.app")
        guard FileManager.default.fileExists(atPath: calculator.path) else { return }
        let findings = DoctorApp.inspect(calculator, home: FileManager.default.homeDirectoryForCurrentUser)
        #expect(findings.bundleComplete && findings.hasSignature && !findings.adHoc && !findings.intelOnly && !findings.iOSApp)
        #expect(DoctorApp.machOArchitectures(calculator.appendingPathComponent("Contents/MacOS/Calculator")).contains(DoctorApp.cpuARM64))
        // Only Gatekeeper's verdict makes it Apple's; without one the signer is unknown, not "unidentified".
        var withVerdict = findings
        #expect(withVerdict.signer == .unknown)
        withVerdict.assessment = .init(verdict: .rejected, source: nil)
        #expect(withVerdict.signer == .unidentified)
        withVerdict.assessment = .init(verdict: .accepted, source: "Apple System")
        #expect(withVerdict.signer == .apple)
    }
}
