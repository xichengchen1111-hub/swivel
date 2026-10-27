import AppKit
import Darwin
import Testing
@testable import Swivel

@Suite(.serialized)
@MainActor
struct AirDropStartupTests {
    @Test func disablingBeforeTheOpeningTaskStartsDoesNotReadTheFolder() async throws {
        let called = DispatchSemaphore(value: 0)
        let watcher = AirDropShelf(folder: URL(fileURLWithPath: "/must-not-open"), openDirectory: { _ in
            called.signal(); return -1
        }, deliver: { _ in Issue.record("Disabled watcher delivered files") })
        watcher.start()
        watcher.stop()
        try await Task.sleep(for: .milliseconds(100))
        #expect(called.wait(timeout: .now()) == .timedOut)
        #expect(!watcher.isWatching)
    }

    @Test func aBlockedDirectoryOpenDoesNotBlockStartupOrRearmAfterStop() async throws {
        let gate = DispatchSemaphore(value: 0)
        let entered = DispatchSemaphore(value: 0)
        let finished = DispatchSemaphore(value: 0)
        let watcher = AirDropShelf(folder: URL(fileURLWithPath: "/unused"), openDirectory: { _ in
            entered.signal()
            gate.wait()
            finished.signal()
            return -1
        }, deliver: { _ in Issue.record("An unarmed watcher delivered files") })
        DispatchQueue.global().asyncAfter(deadline: .now() + 2) { gate.signal() } // bound an old blocking implementation
        let started = Date()
        watcher.start() // must return while the opener is still blocked
        #expect(Date().timeIntervalSince(started) < 1)
        let limit = Date().addingTimeInterval(2)
        while entered.wait(timeout: .now()) != .success && Date() < limit { try await Task.sleep(for: .milliseconds(10)) }
        #expect(!watcher.isWatching)
        watcher.stop()
        gate.signal()
        let done = Date().addingTimeInterval(2)
        while finished.wait(timeout: .now()) != .success && Date() < done { try await Task.sleep(for: .milliseconds(10)) }
        try await Task.sleep(for: .milliseconds(30))
        #expect(!watcher.isWatching)
    }

    @Test func stoppingDuringBaselineClosesItsLateDirectoryHandle() async throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        let gate = DispatchSemaphore(value: 0)
        let opened = DispatchSemaphore(value: 0)
        // A separate reference handle lets us check that the late handle does not remain owned.
        let fd = open(folder.path, O_EVTONLY | O_DIRECTORY | O_CLOEXEC)
        try #require(fd >= 0)
        var identity = stat()
        try #require(fstat(fd, &identity) == 0)
        func stillOwnsDirectory() -> Bool {
            var current = stat()
            return fstat(fd, &current) == 0 && current.st_dev == identity.st_dev && current.st_ino == identity.st_ino
        }
        let watcher = AirDropShelf(folder: folder, openDirectory: { _ in
            opened.signal(); gate.wait(); return fd
        }, deliver: { _ in Issue.record("Stopped watcher delivered files") })
        DispatchQueue.global().asyncAfter(deadline: .now() + 2) { gate.signal() }
        watcher.start()
        let limit = Date().addingTimeInterval(2)
        while opened.wait(timeout: .now()) != .success && Date() < limit { try await Task.sleep(for: .milliseconds(10)) }
        watcher.stop()
        gate.signal()
        let closed = Date().addingTimeInterval(2)
        while stillOwnsDirectory() && Date() < closed { try await Task.sleep(for: .milliseconds(10)) }
        #expect(!stillOwnsDirectory())
        #expect(!watcher.isWatching)
    }
}
