import AVFoundation
import Testing
@testable import Swivel

@Suite(.serialized)
struct NoiseReductionTests {
    private func noisy(in folder: URL) throws -> URL {
        let format = try #require(AVAudioFormat(standardFormatWithSampleRate: 44100, channels: 2))
        let count = 132300
        let pcm = try #require(AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(count)))
        pcm.frameLength = AVAudioFrameCount(count)
        var rng: UInt64 = 1739
        for c in 0..<2 { for i in 0..<count {
            rng = rng &* 6364136223846793005 &+ 1
            let noise = (Double((rng >> 32) & 65535) / 32767.5 - 1) * 0.10
            let tone = i >= 44100 ? 0.3 * sin(Double(i) * 440 * 2 * .pi / 44100) : 0
            pcm.floatChannelData![c][i] = Float((noise + tone) * (c == 0 ? 1 : 0.8))
        } }
        return try Paths.produce(folder.appendingPathComponent("talk.wav")) { target in
            let file = try AVAudioFile(forWriting: target, settings: format.settings)
            try file.write(from: pcm)
        }
    }
    private func samples(_ url: URL) throws -> [[Float]] {
        let file = try AVAudioFile(forReading: url)
        let buffer = try #require(AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: AVAudioFrameCount(file.length)))
        try file.read(into: buffer)
        return (0..<Int(file.processingFormat.channelCount)).map { Array(UnsafeBufferPointer(start: buffer.floatChannelData![$0], count: Int(buffer.frameLength))) }
    }
    @Test func spectralReductionAttenuatesNoiseByTenDBAndKeepsTheToneWithinThreeDB() throws {
        let folder = try MediaBatchFixtures.folder()
        defer { try? FileManager.default.removeItem(at: folder) }
        let source = try noisy(in: folder)
        let beforeBytes = try Data(contentsOf: source), before = try samples(source)
        let output = try Paths.produce(folder.appendingPathComponent("clean.caf")) {
            try NoiseReduction.processPCM(source, to: $0, strength: 1)
        }
        let after = try samples(output)
        #expect(after.count == 2 && after[0].count == before[0].count)
        func energy(_ a: [Float], _ range: Range<Int>) -> Double { range.reduce(0) { $0 + pow(Double(a[$1]), 2) } / Double(range.count) }
        func tone(_ a: [Float]) -> Double {
            let range = 57330..<123480
            var r = 0.0, i = 0.0
            for n in range { let phase = Double(n) * 440 * 2 * .pi / 44100; r += Double(a[n]) * cos(phase); i += Double(a[n]) * sin(phase) }
            return hypot(r, i)
        }
        for c in 0..<2 {
            let reduction = 10 * log10(energy(before[c], 8820..<35280) / energy(after[c], 8820..<35280))
            let change = 20 * log10(tone(after[c]) / tone(before[c]))
            print("Noise channel \(c): reduction \(reduction) dB; tone \(change) dB")
            #expect(reduction > 10)
            #expect(abs(change) < 3)
        }
        #expect(try Data(contentsOf: source) == beforeBytes)
    }
    @Test func zeroStrengthIsTransparentAndShortFilesStayTheSameLength() throws {
        let folder = try MediaBatchFixtures.folder()
        defer { try? FileManager.default.removeItem(at: folder) }
        let source = try MediaBatchFixtures.tone(in: folder, seconds: 0.01, channels: 2)
        let before = try samples(source)
        let output = try Paths.produce(folder.appendingPathComponent("short.caf")) { try NoiseReduction.processPCM(source, to: $0, strength: 0) }
        let after = try samples(output)
        #expect(after[0].count == before[0].count && after.count == before.count)
        #expect(zip(before[0], after[0]).allSatisfy { abs($0 - $1) < 0.000001 })
    }
    @Test func decodedAndSavedAudioKeepsChannelsDurationNamesAndInput() async throws {
        let folder = try MediaBatchFixtures.folder()
        defer { try? FileManager.default.removeItem(at: folder) }
        let source = try noisy(in: folder), before = try Data(contentsOf: source)
        let output = try await NoiseReduction.save(source, strength: 1)
        #expect(output.lastPathComponent == "talk clean.m4a")
        let asset = try await Video.safeAsset(output)
        #expect(try await abs(asset.load(.duration).seconds - 3) < 0.02)
        let track = try #require(try await asset.loadTracks(withMediaType: .audio).first)
        let desc = try #require(try await track.load(.formatDescriptions).first)
        #expect(CMAudioFormatDescriptionGetStreamBasicDescription(desc)?.pointee.mChannelsPerFrame == 2)
        #expect(try Data(contentsOf: source) == before)
        #expect(try FileManager.default.contentsOfDirectory(atPath: folder.path).sorted() == ["talk clean.m4a", "talk.wav"])
        let cancelled = Task { try await NoiseReduction.save(source, strength: 1) }
        cancelled.cancel()
        do { _ = try await cancelled.value; Issue.record("Cancelled denoise unexpectedly saved") } catch {}
        #expect(try FileManager.default.contentsOfDirectory(atPath: folder.path).sorted() == ["talk clean.m4a", "talk.wav"])
    }
    @Test func videoExportUsesTheSameReductionAndPreservesTiming() async throws {
        let folder = try MediaBatchFixtures.folder()
        defer { try? FileManager.default.removeItem(at: folder) }
        let audio = try noisy(in: folder)
        let source = try await MediaBatchFixtures.video(in: folder, seconds: 3, audio: audio)
        let before = try Data(contentsOf: source)
        var s = VideoSettings(); s.start = 0.25; s.end = 2.75; s.speed = 1.25; s.fadeIn = 0.1; s.noiseReduction = 1
        #expect(!s.isTrimOnly)
        let output = try await VideoEdit.export(source, s, isAudio: false)
        let asset = try await Video.safeAsset(output)
        let resultDuration = try await asset.load(.duration).seconds
        print("Denoised video duration: \(resultDuration), expected \(s.outputLength)")
        #expect(abs(resultDuration - s.outputLength) < 0.02)
        #expect(try await !asset.loadTracks(withMediaType: .audio).isEmpty)
        #expect(try Data(contentsOf: source) == before)
    }
    @Test @MainActor func menuOrderAndTranslationsAndMCPStayExplicit() throws {
        let source = URL(fileURLWithPath: "/nonexistent/talk.wav")
        let titles = Catalog.defaultItems(for: [source], mode: .tools).map(\.title)
        #expect(titles.firstIndex(of: "Reduce Noise") == titles.firstIndex(of: "Compress").map { $0 - 1 })
        #expect(Strings.table["Reduce Noise"]?.count == 3)
        #expect(!MCPCatalog.actions(for: [source]).contains { $0.id == "tools.Reduce Noise" })
    }
}
