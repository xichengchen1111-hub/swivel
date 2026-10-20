import Foundation
import Testing
@testable import Swivel

/// The pure logic behind the tools' feedback: what the shelf highlights and restores, which preset field gets
/// pointed at, and where the quality slider rests.
@Suite(.serialized)
@MainActor
struct ToolsFeelTests {
    private func files(_ names: [String]) throws -> (folder: URL, urls: [URL]) {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("SwivelFeel-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let urls = names.map { folder.appendingPathComponent($0) }
        for url in urls { try Data(url.lastPathComponent.utf8).write(to: url) }
        return (folder, urls)
    }

    @Test func testShelfReportsWhatArrivedAndWhatWasAlreadyThere() throws {
        let (folder, urls) = try files(["a.txt", "b.txt", "c.txt"])
        defer { try? FileManager.default.removeItem(at: folder) }
        let (a, b, c) = (urls[0], urls[1], urls[2])
        var shelf = ShelfContents()
        let first = shelf.add([a, b, a])
        #expect(first.added == IndexSet([0, 1]))
        #expect(first.existing.isEmpty)
        let second = shelf.add([b, c, folder.appendingPathComponent("missing.txt")])
        #expect(second.added == IndexSet([2]))
        #expect(second.existing == IndexSet([1]))
        #expect(shelf.urls == [a, b, c])
    }

    @Test func testShelfUndoPutsReferencesBackInPlace() throws {
        let (folder, urls) = try files(["0.txt", "1.txt", "2.txt", "3.txt"])
        defer { try? FileManager.default.removeItem(at: folder) }
        var shelf = ShelfContents()
        shelf.add(urls)
        let removed = [(offset: 0, url: urls[0]), (offset: 2, url: urls[2])]
        shelf.remove([0, 2])
        #expect(shelf.urls == [urls[1], urls[3]])
        let rows = shelf.restore(removed)
        #expect(shelf.urls == urls)
        #expect(rows == IndexSet([0, 2]))
    }

    @Test func testShelfUndoSkipsFilesAddedAgainAndClampsPositions() throws {
        let (folder, urls) = try files(["0.txt", "1.txt", "2.txt"])
        defer { try? FileManager.default.removeItem(at: folder) }
        var shelf = ShelfContents()
        shelf.add(urls)
        shelf.clear()
        shelf.add([urls[1]])
        let rows = shelf.restore([(offset: 0, url: urls[0]), (offset: 1, url: urls[1]), (offset: 2, url: urls[2])])
        #expect(shelf.urls == [urls[0], urls[1], urls[2]])
        #expect(rows == IndexSet([0, 2]))
        var short = ShelfContents()
        #expect(short.restore([(offset: 5, url: urls[2])]) == IndexSet([0]))
        #expect(short.urls == [urls[2]])
    }

    @Test func testPresetFormPointsAtTheFirstBadField() {
        #expect(ImagePreset.formIssue(name: "Web", longEdge: "1600") == nil)
        #expect(ImagePreset.formIssue(name: "Web", longEdge: " 0 ") == nil)
        #expect(ImagePreset.formIssue(name: "  ", longEdge: "1600") == .name)
        #expect(ImagePreset.formIssue(name: String(repeating: "x", count: 61), longEdge: "1600") == .name)
        #expect(ImagePreset.formIssue(name: "", longEdge: "abc") == .name)
        #expect(ImagePreset.formIssue(name: "Web", longEdge: "63") == .size)
        #expect(ImagePreset.formIssue(name: "Web", longEdge: "16385") == .size)
        #expect(ImagePreset.formIssue(name: "Web", longEdge: "1,600") == .size)
        #expect(ImagePreset.formIssue(name: "Web", longEdge: "") == .size)
        // Whatever the form accepts, the saved preset accepts too.
        var preset = ImagePreset.starters[0]
        preset.name = "Web"
        preset.longEdge = ImagePreset.longEdge(from: " 64 ") ?? -1
        #expect(preset.isValid)
    }

    @Test func testQualitySliderRestsOnRoundValuesButKeepsNeighbours() {
        #expect(QualityDetent.snap(79.4) == 80)
        #expect(QualityDetent.snap(80.7) == 80)
        #expect(QualityDetent.snap(81.2) == 81.2)
        #expect(QualityDetent.snap(9) == 9)
        #expect(QualityDetent.snap(11) == 11)
        #expect(QualityDetent.snap(99.5) == 100)
        #expect(QualityDetent.snap(1.3) == 1.3)
        #expect(QualityDetent.isDetent(90))
        #expect(!QualityDetent.isDetent(91))
        #expect(!QualityDetent.isDetent(0))
    }
}
