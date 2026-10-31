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

extension DoctorProgressTests {
    @Test func broomKeepsMovingThroughEachTurn() {
        var motion = DoctorBroomMotion()
        let dt: CGFloat = 1 / 120
        var previous = motion.value
        var slowFrames = 0, longestPause = 0
        for frame in 0..<1200 {
            motion.step(dt)
            let velocity = abs(motion.value - previous) / dt
            previous = motion.value
            if frame > 120, velocity < 0.1 {
                slowFrames += 1; longestPause = max(longestPause, slowFrames)
            } else { slowFrames = 0 }
        }
        #expect(CGFloat(longestPause) * dt < 0.2, "The broom must reverse without a visible rest at either end")
    }
}

extension DoctorProgressTests {
    @Test func broomMotionIsBoundedAndIndependentOfDisplayRate() {
        func sample(_ fps: CGFloat) -> [CGFloat] {
            var motion = DoctorBroomMotion()
            var values: [CGFloat] = []
            for frame in 1...Int(fps * 6) {
                motion.step(1 / fps)
                #expect(abs(motion.value) <= 1)
                if frame % Int(fps / 30) == 0 { values.append(motion.value) }
            }
            motion.reset()
            #expect(motion.value == 0)
            return values
        }
        let low = sample(30), standard = sample(60), proMotion = sample(120)
        #expect(zip(low, standard).allSatisfy { abs($0 - $1) < 0.000001 })
        #expect(zip(low, proMotion).allSatisfy { abs($0 - $1) < 0.000001 })
        #expect(low.max()! - low.min()! > 1.8)
    }

    @Test func broomRepeatsWithoutALoopSeam() {
        var motion = DoctorBroomMotion()
        let frames = Int((DoctorBroomMotion.period * 120).rounded())
        for _ in 0..<(frames * 4) { motion.step(1 / 120) }
        var previous: [CGFloat] = []
        for _ in 0..<frames { motion.step(1 / 120); previous.append(motion.value) }
        for index in 0..<frames {
            motion.step(1 / 120)
            #expect(abs(motion.value - previous[index]) < 0.000001)
        }
    }
}
