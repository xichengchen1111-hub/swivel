import AppKit
import AVFoundation
import CoreImage
import Speech
import Testing
@testable import Swivel

@Suite(.serialized)
struct SubtitleTests {
    @Test func srtFormatsSplitsSentencesPausesAndLongCJKRuns() {
        #expect(Subtitles.timestamp(3661.234) == "01:01:01,234")
        #expect(Subtitles.timestamp(59.9998) == "00:01:00,000")
        #expect(Subtitles.timestamp(-5) == "00:00:00,000")
        #expect(Subtitles.srt(Subtitles.cues([])).isEmpty)
        let words = [TimedWord(text: "Hello", start: 0, end: 1), TimedWord(text: "world.", start: 1.1, end: 2),
                     TimedWord(text: "Again!", start: 3, end: 4)]
        let cues = Subtitles.cues(words)
        #expect(cues.count == 2 && cues[0].text == "Hello world.")
        #expect(Subtitles.srt(cues).contains("2\n00:00:03,000 --> 00:00:04,000\nAgain!"))
        let cjk = Subtitles.cues([TimedWord(text: String(repeating: "你好日本語", count: 30), start: 0, end: 25)])
        #expect(cjk.count >= 4)
        #expect(cjk.allSatisfy { $0.end - $0.start <= 7 && $0.text.split(separator: "\n").allSatisfy { $0.count <= 42 } })
        #expect(zip(cjk, cjk.dropFirst()).allSatisfy { $0.end <= $1.start })
        let overlapping = Subtitles.cues([TimedWord(text: "one.", start: 0, end: 3), TimedWord(text: "two.", start: 2, end: 4)])
        #expect(overlapping[1].start == 3)
        #expect(Subtitles.cues([TimedWord(text: "bad", start: .nan, end: 2)]).isEmpty)
    }

    @Test func importedSRTAndBurnedFrameRespectTheSourceTimeline() throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("Swivel-SRT-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        let cues = [SubtitleCue(start: 5, end: 6, text: "Hello 世界")]
        let url = try Paths.create(Data(Subtitles.srt(cues).utf8), at: folder.appendingPathComponent("clip.srt"))
        #expect(try Subtitles.read(url) == cues)
        var settings = VideoSettings()
        settings.start = 4; settings.end = 8; settings.speed = 2; settings.subtitles = cues
        #expect(!settings.isTrimOnly)
        let base = CIImage(color: .blue).cropped(to: CGRect(x: 0, y: 0, width: 640, height: 360))
        let context = CIContext()
        func bytes(at time: Double) -> [UInt8] {
            let frame = VideoEdit.frame(base, settings, outputTime: time)
            var bytes = [UInt8](repeating: 0, count: 640 * 360 * 4)
            context.render(frame, toBitmap: &bytes, rowBytes: 640 * 4, bounds: base.extent, format: .RGBA8, colorSpace: nil)
            return bytes
        }
        let before = bytes(at: 0), during = bytes(at: 0.6), after = bytes(at: 1.1)
        #expect(before == after && before != during)
        // Safe bottom margin is clear.
        #expect(Array(during.prefix(640 * 20 * 4)) == Array(before.prefix(640 * 20 * 4)))
        let cjk = try #require(Subtitles.overlay(SubtitleCue(start: 0, end: 1, text: String(repeating: "你", count: 42)),
                                                  size: CGSize(width: 640, height: 360)))
        let glyphs = try Retouch.Raster(#require(context.createCGImage(cjk, from: cjk.extent)))
        var whiteColumns: [Int] = []
        for y in 0..<glyphs.height { for x in 0..<glyphs.width {
            let i = (y * glyphs.width + x) * 4
            if glyphs.bytes[i] > 200 && glyphs.bytes[i + 3] > 200 { whiteColumns.append(x) }
        } }
        #expect(!whiteColumns.isEmpty && (whiteColumns.min() ?? 0) >= 16 && (whiteColumns.max() ?? 640) < 624)

    }

    @Test @MainActor func menuPlacementAvailabilityAndTranslations() throws {
        for (ext, title) in [("mp4", "Subtitles"), ("m4a", "Transcribe")] {
            let url = URL(fileURLWithPath: "/nonexistent/clip.\(ext)")
            let titles = Catalog.defaultItems(for: [url], mode: .tools).map(\.title)
            if #available(macOS 26, *) { #expect(titles.firstIndex(of: title) == titles.firstIndex(of: "Trim").map { $0 + 1 }) }
            else { #expect(!titles.contains(title)) }
            #expect(!MCPCatalog.actions(for: [url]).contains { $0.id == "tools.\(title)" })
        }
        for key in ["Subtitles", "Transcribe", "Transcription Language", "Follow App Language", "Burn subtitles", "Choose SRT…", "Also save SRT"] {
            #expect(Strings.table[key]?.count == 3)
        }
    }

    @Test(.enabled("Needs macOS 26 and an installed English speech model") { await installedEnglishSpeech() })
    func localSpeechRecognizesAKnownEnglishFileWithoutDownloading() async throws {
        if #available(macOS 26, *) {
            let folder = FileManager.default.temporaryDirectory.appendingPathComponent("Swivel-Speech-\(UUID().uuidString)")
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            defer { try? FileManager.default.removeItem(at: folder) }
            let source = try await Paths.produce(folder.appendingPathComponent("known.aiff")) { output in
                try await Shell.run("/usr/bin/say", ["-v", "Samantha", "-o", output.path, "--", "The purple bicycle is beside the yellow house."])
            }
            let before = try Data(contentsOf: source)
            let words = try await LocalTranscription.recognize(source, locale: Locale(identifier: "en-US"))
            let text = words.map(\.text).joined().lowercased()
            print("Local speech fixture: \(text)")
            #expect(text.contains("bicycle") && text.contains("yellow"))
            #expect(words.allSatisfy { $0.start.isFinite && $0.end > $0.start })
            #expect(try Data(contentsOf: source) == before)
            #expect(try FileManager.default.contentsOfDirectory(atPath: folder.path) == ["known.aiff"])
        }
    }
}

private func installedEnglishSpeech() async -> Bool {
    if #available(macOS 26, *) {
        return await SpeechTranscriber.installedLocales.contains { $0.identifier == "en_US" || $0.identifier == "en-US" }
    }
    return false
}
