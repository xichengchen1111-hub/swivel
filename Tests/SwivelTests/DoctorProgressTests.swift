import AppKit
import Testing
@testable import Swivel

struct DoctorProgressTests {
    @Test func unknownTotalsNeverInventAPercentageOrETA() {
        let work = DoctorProgress("Scanning", now: 10)
        work.scanned(256)
        let state = work.snapshot(now: 20)
        #expect(state.fraction == nil && state.remaining == nil)
        #expect(state.scanned == 256 && state.elapsed == 10)
    }

    @Test func estimatesUseOnlyMeasuredWorkAndResetForANewStage() {
        let work = DoctorProgress("Checking", now: 10)
        work.stage("Checking", completed: 0, total: 10, now: 10)
        work.stage("Checking", completed: 2, total: 10, now: 14)
        let state = work.snapshot(now: 14)
        #expect(state.fraction == 0.2 && state.remaining == 16)
        work.stage("Comparing", completed: 0, total: 20, now: 15)
        #expect(work.snapshot(now: 16).remaining == nil)
        work.stage("Comparing", completed: 20, total: 20, now: 19)
        #expect(work.snapshot(now: 19).fraction == 1)
        #expect(work.snapshot(now: 19).remaining == nil)
    }

    @Test func detachedWorkInheritsTheReporter() async {
        let work = DoctorProgress("Scanning")
        await DoctorProgress.$current.withValue(work) {
            await DoctorProgress.detached { DoctorProgress.current?.scanned(512) }.value
        }
        #expect(work.snapshot().scanned == 512)
    }
}

extension DoctorProgressTests {
    @Test func stalledWorkAndRepeatedFilesDiscardTheOldEstimate() {
        let work = DoctorProgress("Reading", now: 10)
        work.stage("Reading", completed: 0, total: 100, bytes: true, now: 10)
        work.stage("Reading", completed: 50, total: 100, bytes: true, now: 14)
        #expect(work.snapshot(now: 14).remaining == 4)
        #expect(work.snapshot(now: 25).remaining == nil)
        work.stage("Reading", completed: 0, total: 100, bytes: true, now: 30)
        work.stage("Reading", completed: 50, total: 100, bytes: true, now: 31)
        #expect(work.snapshot(now: 31).remaining == nil)
        work.stage("Reading", completed: 1000, total: 100, now: 32)
        #expect(work.snapshot(now: 32).fraction == 1)
    }

    @Test func scannerAndHasherReportActualFixtureWork() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let file = root.appendingPathComponent("data.bin")
        let data = Data(repeating: 42, count: 2_100_000)
        try data.write(to: file)
        let scan = DoctorProgress("Scanning")
        await DoctorProgress.$current.withValue(scan) {
            _ = await DoctorStorage.scan(root, home: root)
        }
        #expect(scan.snapshot().scanned == 2)
        #expect(scan.snapshot().fraction == nil)
        let hash = DoctorProgress("Reading")
        let digest = await DoctorProgress.$current.withValue(hash) {
            await DoctorProgress.detached { DoctorDuplicates.digest(file) }.value
        }
        #expect(digest?.count == 64)
        #expect(hash.snapshot().completed == data.count)
        #expect(hash.snapshot().fraction == 1 && hash.snapshot().bytes)
        #expect(try Data(contentsOf: file) == data)
    }
}

extension DoctorProgressTests {
    @Test func cancellationReachesTheDetachedScan() async {
        let work = DoctorProgress("Scanning")
        let task = Task {
            await DoctorProgress.$current.withValue(work) {
                await DoctorProgress.run {
                    while !Task.isCancelled { await Task.yield() }
                    return Task.isCancelled
                }
            }
        }
        task.cancel()
        #expect(await task.value)
    }
}
