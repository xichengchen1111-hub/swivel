import AppKit
import Foundation
import Testing
@testable import Swivel

@Suite(.serialized)
struct RetouchTests {
    private func texture(_ width: Int, _ height: Int) -> Retouch.Raster {
        var bytes = [UInt8](repeating: 255, count: width * height * 4)
        for y in 0..<height { for x in 0..<width {
            let v: UInt8 = (x / 4 + y / 4) % 2 == 0 ? 30 : 220
            let i = (y * width + x) * 4
            bytes[i] = v; bytes[i + 1] = v; bytes[i + 2] = v
        } }
        return Retouch.Raster(width: width, height: height, bytes: bytes)
    }

    @Test func completionRestoresTextureAndAnAverageFillFails() throws {
        let original = texture(96, 80)
        var damaged = original
        var mask = [Bool](repeating: false, count: 96 * 80)
        for y in 28..<52 { for x in 32..<60 {
            let p = y * 96 + x
            mask[p] = true
            damaged.bytes[p * 4] = 255; damaged.bytes[p * 4 + 1] = 0; damaged.bytes[p * 4 + 2] = 0
        } }
        let repaired = try Retouch.complete(damaged, mask: mask)
        var error = 0.0, averageError = 0.0, n = 0.0
        for p in mask.indices {
            if mask[p] {
                for c in 0..<3 {
                    error += abs(Double(repaired.bytes[p * 4 + c]) - Double(original.bytes[p * 4 + c]))
                    averageError += abs(125 - Double(original.bytes[p * 4 + c]))
                    n += 1
                }
            } else {
                #expect(repaired.bytes[(p * 4)..<(p * 4 + 4)] == original.bytes[(p * 4)..<(p * 4 + 4)])
            }
        }
        print("Retouch texture MAE: \(error / n); average-fill MAE: \(averageError / n)")
        #expect(error / n < 20)
        #expect(averageError / n > 80)
    }

    @Test func edgeMasksLargeCoverageAndOnePixelStayFiniteAndPreserveOutside() throws {
        let input = texture(40, 40)
        var cases = [[Bool]]()
        cases.append((0..<1600).map { $0 % 40 < 5 }) // touches three borders
        cases.append((0..<1600).map { $0 % 40 < 36 }) // 90% painted
        cases.append((0..<1600).map { $0 == 820 })
        cases.append([Bool](repeating: false, count: 1600))
        for mask in cases {
            let output = try Retouch.complete(input, mask: mask)
            #expect(output.bytes.count == input.bytes.count)
            for p in mask.indices where !mask[p] {
                #expect(output.bytes[(p * 4)..<(p * 4 + 4)] == input.bytes[(p * 4)..<(p * 4 + 4)])
            }
        }
        #expect(throws: (any Error).self) { try Retouch.complete(input, mask: [Bool](repeating: true, count: 1600)) }
        #expect(throws: Cancelled.self) { try Retouch.complete(input, mask: cases[0], cancelled: { true }) }
    }

    @Test func pyramidPreservesUnmaskedPixelsAndUsesOnlyBackground() throws {
        var input = texture(256, 192)
        var mask = [Bool](repeating: false, count: 256 * 192)
        for y in 60..<130 { for x in 80..<160 {
            let p = y * 256 + x
            mask[p] = true
            input.bytes[p * 4] = 255; input.bytes[p * 4 + 1] = 0
        } }
        let output = try Retouch.complete(input, mask: mask)
        for p in mask.indices {
            if mask[p] { #expect(output.bytes[p * 4] == output.bytes[p * 4 + 1]) }
            else { #expect(output.bytes[(p * 4)..<(p * 4 + 4)] == input.bytes[(p * 4)..<(p * 4 + 4)]) }
        }
    }

    @Test func strokeCoordinatesFileNamesAndExclusiveOutputs() throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("Swivel-Retouch-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        var input = texture(160, 100)
        for x in 65..<78 {
            let i = (30 * 160 + x) * 4
            input.bytes[i] = 255; input.bytes[i + 1] = 0; input.bytes[i + 2] = 0
        }
        let source = try Images.save(input.image(), as: folder.appendingPathComponent("photo.png"), type: .png, quality: 1)
        let before = try Data(contentsOf: source)
        let stroke = RetouchStroke(points: [CGPoint(x: 0.4, y: 0.7), CGPoint(x: 0.5, y: 0.7)], diameter: 0.05)
        let one = try Retouch.save(source, strokes: [stroke])
        let firstBytes = try Data(contentsOf: one)
        let two = try Retouch.save(source, strokes: [stroke])
        #expect(one.lastPathComponent == "photo retouched.png")
        #expect(two.lastPathComponent == "photo retouched 2.png")
        #expect(try Data(contentsOf: source) == before)
        #expect(try Data(contentsOf: one) == firstBytes)
        let output = try Retouch.Raster(Images.load(one))
        for x in 65..<78 {
            let i = (30 * 160 + x) * 4
            #expect(output.bytes[i] == output.bytes[i + 1]) // the top-down mark was actually removed
        }
        for y in 0..<100 { for x in 0..<160 where x < 59 || x > 85 || y < 25 || y > 35 {
            let i = (y * 160 + x) * 4
            #expect(output.bytes[i..<(i + 4)] == input.bytes[i..<(i + 4)])
        } }
        let jpeg = try Images.save(input.image(), as: folder.appendingPathComponent("photo.jpg"), type: .jpeg, quality: 1)
        let savedJPEG = try Retouch.save(jpeg, strokes: [stroke])
        #expect(savedJPEG.lastPathComponent == "photo retouched.jpg")
    }

    @Test func cancellationDuringSynthesisStopsAndSavingLeavesNoPartialFile() async throws {
        let input = texture(256, 192)
        let mask = (0..<(256 * 192)).map { $0 % 256 > 80 && $0 % 256 < 180 }
        var polls = 0
        #expect(throws: Cancelled.self) {
            try Retouch.complete(input, mask: mask, cancelled: { polls += 1; return polls > 4 })
        }
        #expect(polls > 4)
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("Swivel-Retouch-Cancel-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        let source = try Images.save(input.image(), as: folder.appendingPathComponent("photo.png"), type: .png, quality: 1)
        let task = Task.detached {
            try Task.checkCancellation()
            return try Retouch.save(source, strokes: [RetouchStroke(points: [CGPoint(x: 0.5, y: 0.5)], diameter: 0.3)])
        }
        task.cancel()
        do {
            _ = try await task.value
            Issue.record("A cancelled repair unexpectedly saved a file")
        } catch is CancellationError {} catch is Cancelled {}
        #expect(try FileManager.default.contentsOfDirectory(atPath: folder.path) == ["photo.png"])
    }

    @Test @MainActor func menuOrderTranslationsAndUndoAreStable() throws {
        let source = URL(fileURLWithPath: "/nonexistent/photo.png")
        let titles = Catalog.defaultItems(for: [source], mode: .tools).map(\.title)
        let redact = try #require(titles.firstIndex(of: "Redact"))
        #expect(titles[redact - 1] == "Retouch")
        #expect(!MCPCatalog.actions(for: [source]).contains { $0.id == "tools.Retouch" })
        for key in ["Retouch", "Brush Size", "Show Original", "Paint over an object to repair the background.",
                    "Couldn't repair this area.", "Nothing to retouch. Paint over an object first.",
                    "Leave some background unpainted to repair this area."] {
            let translations = try #require(Strings.table[key])
            #expect(translations.count == 3 && translations.allSatisfy { !$0.isEmpty })
        }
        let editor = try RetouchEditor(source: source)
        // The canvas callback is the same path used by mouse-up, without opening a real window.
        let canvas = try #require(editor.window.contentView?.descendantsForRetouchTest.compactMap { $0 as? RetouchCanvas }.first)
        editor.history.groupsByEvent = false
        editor.history.beginUndoGrouping()
        canvas.onCommit?(RetouchStroke(points: [CGPoint(x: 0.5, y: 0.5)], diameter: 0.02))
        editor.history.endUndoGrouping()
        #expect(editor.strokes.count == 1)
        editor.history.undo()
        #expect(editor.strokes.isEmpty)
        editor.history.redo()
        #expect(editor.strokes.count == 1)
        editor.didClose()
    }

    @Test func previewSizedLocalRepairHasATimedMeasurement() throws {
        let input = try texture(1400, 900).image()
        let clock = ContinuousClock()
        let start = clock.now
        _ = try Retouch.render(input, strokes: [RetouchStroke(points: [CGPoint(x: 0.5, y: 0.5)], diameter: 0.04)])
        let elapsed = start.duration(to: clock.now)
        print("Retouch 1400px preview, 56px brush: \(elapsed)")
        #expect(elapsed < .seconds(1))
    }
}

private extension NSView {
    var descendantsForRetouchTest: [NSView] { subviews.flatMap { [$0] + $0.descendantsForRetouchTest } }
}
