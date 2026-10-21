import AppKit
import Carbon
import CoreImage
import ImageIO
import PDFKit
import Testing
@testable import Swivel

@Suite(.serialized)
@MainActor
struct FileActionsTests {
    @Test func importingAnImageWithoutCodesKeepsTheEmptyResultMessageAndNoUndoStep() async throws {
        let dir = try folder(); defer { try? FileManager.default.removeItem(at: dir) }
        let defaults = UserDefaults.standard, before = defaults.object(forKey: "feature.qrCodes")
        defer { if let before { defaults.set(before, forKey: "feature.qrCodes") } else { defaults.removeObject(forKey: "feature.qrCodes") } }
        defaults.set(true, forKey: "feature.qrCodes")
        let image = dir.appendingPathComponent("blank.png")
        try png(CIImage(color: .white).cropped(to: CGRect(x: 0, y: 0, width: 300, height: 300)), image)
        let store = QRLibraryStore(directory: dir.appendingPathComponent("library")), library = QRLibraryWindow(store: store)
        library.ingestImages([image])
        for _ in 0..<500 where library.isBusy { try await Task.sleep(for: .milliseconds(10)) }
        #expect(!library.isBusy)
        #expect(try store.load().isEmpty)
        #expect(!FileManager.default.fileExists(atPath: store.file.path))
        func descendants(_ view: NSView) -> [NSView] { [view] + view.subviews.flatMap(descendants) }
        let views = descendants(library.window.contentView!)
        #expect(views.compactMap { $0 as? NSTextField }.contains { $0.stringValue == L("No readable QR codes or barcodes found.") })
        #expect(views.compactMap { $0 as? NSButton }.contains { $0.title == L("Undo") && !$0.isEnabled })
    }

    @Test func appCustomizationIncludesEveryAllowedExtensionInItsCategory() {
        let defaults = UserDefaults.standard, keys = ["feature.appRouting", "appTargets.v1"]
        let before = keys.map { defaults.object(forKey: $0) }
        defer { for (key, value) in zip(keys, before) { if let value { defaults.set(value, forKey: key) } else { defaults.removeObject(forKey: key) } } }
        defaults.set(true, forKey: "feature.appRouting")
        let text = FileAppTarget(id: "text-only", name: "Text", bundleID: "", appPath: "", delivery: .openFiles, extensions: ["txt"])
        let image = FileAppTarget(id: "png-only", name: "PNG", bundleID: "", appPath: "", delivery: .openFiles, extensions: ["png"])
        Prefs.fileAppTargets = [text, image]
        #expect(Catalog.allItems(mode: .apps, kind: .document) == [text.actionID])
        #expect(Catalog.allItems(mode: .apps, kind: .image) == [image.actionID])
        #expect(Catalog.allItems(mode: .apps, kind: .pdf).isEmpty)
        #expect(Catalog.defaultItems(for: [URL(fileURLWithPath: "/fixture.jpg")], mode: .apps).isEmpty)
        #expect(Catalog.defaultItems(for: [URL(fileURLWithPath: "/fixture.txt")], mode: .apps).map(\.title) == [text.actionID])
    }

    @Test func symbolicSystemShortcutsBlockOnlyEnabledMatchingCombinations() {
        let modifiers = UInt32(controlKey | optionKey)
        let keys = [SystemShortcuts.Key(code: 49, modifiers: modifiers, enabled: true),
                    SystemShortcuts.Key(code: 38, modifiers: modifiers, enabled: false)]
        #expect(SystemShortcuts.contains(code: WheelShortcut.space.keyCode, modifiers: modifiers, in: keys))
        #expect(!SystemShortcuts.contains(code: WheelShortcut.j.keyCode, modifiers: modifiers, in: keys))
        #expect(!SystemShortcuts.contains(code: 49, modifiers: UInt32(controlKey), in: keys))
    }

    @Test func toolWheelShortcutDefaultsToJAndRequiresTheWholeCombination() {
        let defaults = UserDefaults.standard, before = defaults.object(forKey: "wheel.shortcut")
        defer { if let before { defaults.set(before, forKey: "wheel.shortcut") } else { defaults.removeObject(forKey: "wheel.shortcut") } }
        defaults.removeObject(forKey: "wheel.shortcut")
        #expect(Prefs.wheelShortcut == .j)
        func event(_ flags: NSEvent.ModifierFlags, repeatKey: Bool = false) -> NSEvent {
            NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: flags, timestamp: 0, windowNumber: 0, context: nil, characters: "j", charactersIgnoringModifiers: "j", isARepeat: repeatKey, keyCode: 38)!
        }
        #expect(WheelShortcut.j.matches(event([.control, .option])))
        #expect(!WheelShortcut.j.matches(event([])))
        #expect(!WheelShortcut.j.matches(event([.control, .option, .command])))
        #expect(!WheelShortcut.j.matches(event([.control, .option], repeatKey: true)))
        #expect(!WheelShortcut.space.matches(event([.control, .option])))
    }

    @Test func emptyLibraryShowsCreationActionsAndRevealsSavedTrees() throws {
        let dir = try folder(); defer { try? FileManager.default.removeItem(at: dir) }
        let defaults = UserDefaults.standard, before = defaults.object(forKey: "feature.qrCodes")
        defer { if let before { defaults.set(before, forKey: "feature.qrCodes") } else { defaults.removeObject(forKey: "feature.qrCodes") } }
        defaults.set(true, forKey: "feature.qrCodes")
        let store = QRLibraryStore(directory: dir), library = QRLibraryWindow(store: store)
        func descendants(_ view: NSView) -> [NSView] { [view] + view.subviews.flatMap(descendants) }
        let views = descendants(library.window.contentView!)
        #expect(library.tree.isHidden)
        #expect(views.compactMap { $0 as? NSButton }.filter { $0.title == L("New Code…") && $0.isEnabled }.count == 2)
        #expect(views.compactMap { $0 as? NSTextField }.contains { $0.stringValue == L("Save your first QR code") })
        try store.add(SavedQRCode(name: "Saved", value: "https://example.com"))
        #expect(!library.tree.isHidden)
        try store.save([])
        #expect(library.tree.isHidden)
    }

    private func folder() throws -> URL {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("SwivelActions-" + UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true); return url
    }
    @discardableResult private func file(_ directory: URL, _ name: String, _ value: String = "original") throws -> URL {
        let url = directory.appendingPathComponent(name); try Data(value.utf8).write(to: url); return url
    }
    private func content(_ url: URL) throws -> String { try String(contentsOf: url, encoding: .utf8) }
    private func pdf(_ url: URL, labels: [String]) throws {
        let document = PDFDocument()
        for (index, label) in labels.enumerated() {
            let data = NSMutableData()
            var box = CGRect(x: 0, y: 0, width: 300 + index * 10, height: 400)
            let context = CGContext(consumer: CGDataConsumer(data: data)!, mediaBox: &box, nil)!
            context.beginPDFPage(nil)
            NSGraphicsContext.saveGraphicsState(); NSGraphicsContext.current = NSGraphicsContext(cgContext: context, flipped: false)
            (label as NSString).draw(at: NSPoint(x: 30, y: 100), withAttributes: [.font: NSFont.systemFont(ofSize: 20)])
            NSGraphicsContext.restoreGraphicsState(); context.endPDFPage(); context.closePDF()
            document.insert(PDFDocument(data: data as Data)!.page(at: 0)!, at: document.pageCount)
        }
        #expect(document.write(to: url))
    }
    private func qr(_ value: String) -> CIImage {
        let filter = CIFilter(name: "CIQRCodeGenerator")!
        filter.setValue(Data(value.utf8), forKey: "inputMessage"); filter.setValue("M", forKey: "inputCorrectionLevel")
        return filter.outputImage!.transformed(by: CGAffineTransform(scaleX: 8, y: 8))
    }
    private func png(_ image: CIImage, _ url: URL) throws {
        let cg = CIContext().createCGImage(image, from: image.extent)!
        let dest = CGImageDestinationCreateWithURL(url as CFURL, "public.png" as CFString, 1, nil)!
        CGImageDestinationAddImage(dest, cg, nil); #expect(CGImageDestinationFinalize(dest))
    }

    @Test func renameCombinesRulesAndKeepsExtensionsAndFolderNames() throws {
        let dir = try folder(); defer { try? FileManager.default.removeItem(at: dir) }
        let photo = try file(dir, "照片-raw.PNG")
        let rules = RenameRules(find: "raw", replacement: "final", prefix: "项目-", suffix: "-好", number: true, start: 7, digits: 3)
        #expect(rules.name(for: photo, index: 0) == "项目-照片-final-好-007.PNG")
        let sub = dir.appendingPathComponent("project.assets"); try FileManager.default.createDirectory(at: sub, withIntermediateDirectories: false)
        #expect(RenameRules(prefix: "new-").name(for: sub, index: 0) == "new-project.assets")
        #expect(RenameRules(base: "item", number: true, start: 9, digits: 2).name(for: photo, index: 1) == "item-10.PNG")
    }

    @Test func renamePreviewFindsDuplicateNamesExternalConflictsAndInvalidNames() throws {
        let dir = try folder(); defer { try? FileManager.default.removeItem(at: dir) }
        let a = try file(dir, "a.txt"), b = try file(dir, "b.txt")
        #expect(!RenamePlan.preview([a, b], rules: RenameRules(base: "same")).canApply)
        _ = try file(dir, "new-a.txt", "keep")
        #expect(!RenamePlan.preview([a], rules: RenameRules(prefix: "new-")).canApply)
        for bad in ["", ".", "..", ".hidden", "a/b", "a:b", "a\0b", "a\nb", String(repeating: "长", count: 100)] {
            #expect(!RenamePlan.validName(bad))
        }
        #expect(!RenamePlan.preview([a], rules: RenameRules(number: true, start: -1)).canApply)
        #expect(RenamePlan.preview([a, a], rules: RenameRules(prefix: "x-")).entries.count == 1)
        #expect(!RenamePlan.preview([a], rules: RenameRules()).canApply)
    }

    @Test func renameRejectsMissingAndParentChildSelections() throws {
        let dir = try folder(); defer { try? FileManager.default.removeItem(at: dir) }
        let a = try file(dir, "a")
        #expect(!RenamePlan.preview([dir, a], rules: RenameRules(prefix: "x")).canApply)
        #expect(!RenamePlan.preview([dir.appendingPathComponent("missing")], rules: RenameRules(prefix: "x")).canApply)
    }

    @Test func renameAndUndoPreserveDataAcrossDirectories() throws {
        let aDir = try folder(), bDir = try folder()
        defer { try? FileManager.default.removeItem(at: aDir); try? FileManager.default.removeItem(at: bDir) }
        let a = try file(aDir, "one.txt", "alpha"), b = try file(bDir, "two.txt", "beta")
        let plan = RenamePlan.preview([a, b], rules: RenameRules(base: "invoice", number: true))
        let receipt = try BatchRenamer.execute(plan)
        #expect(try content(plan.entries[0].destination) == "alpha")
        #expect(try content(plan.entries[1].destination) == "beta")
        _ = try receipt.undo()
        #expect(try content(a) == "alpha"); #expect(try content(b) == "beta")
        #expect(try FileManager.default.contentsOfDirectory(atPath: aDir.path) == ["one.txt"])
    }

    @Test func renameSupportsSwapsAndCaseOnlyChanges() throws {
        let dir = try folder(); defer { try? FileManager.default.removeItem(at: dir) }
        let a = try file(dir, "Alpha.txt", "alpha"), b = try file(dir, "Beta.txt", "beta")
        let rows = [RenameEntry(source: a, destination: b, identity: try FileIdentity.read(a)),
                    RenameEntry(source: b, destination: a, identity: try FileIdentity.read(b))]
        let receipt = try BatchRenamer.execute(RenamePlan.validate(rows))
        #expect(try content(a) == "beta"); #expect(try content(b) == "alpha")
        _ = try receipt.undo()
        let plan = RenamePlan.preview([a], rules: RenameRules(find: "Alpha", replacement: "alpha"))
        #expect(plan.canApply)
        _ = try BatchRenamer.execute(plan)
        #expect(try content(dir.appendingPathComponent("alpha.txt")) == "alpha")
    }

    @Test func staleRenamePreviewAndLateDestinationCannotReplaceFiles() throws {
        let dir = try folder(); defer { try? FileManager.default.removeItem(at: dir) }
        let a = try file(dir, "a.txt", "old")
        let plan = RenamePlan.preview([a], rules: RenameRules(prefix: "new-"))
        try FileManager.default.moveItem(at: a, to: dir.appendingPathComponent("saved-old"))
        try Data("replacement".utf8).write(to: a)
        #expect(throws: (any Error).self) { try BatchRenamer.execute(plan) }
        #expect(try content(a) == "replacement")
        let fresh = RenamePlan.preview([a], rules: RenameRules(prefix: "new-"))
        _ = try file(dir, "new-a.txt", "blocker")
        #expect(throws: (any Error).self) { try BatchRenamer.execute(fresh) }
        #expect(try content(a) == "replacement"); #expect(try content(dir.appendingPathComponent("new-a.txt")) == "blocker")
    }

    @Test func renameRollsBackFailuresInStagingAndCommit() throws {
        for failAt in [2, 4] {
            let dir = try folder(); defer { try? FileManager.default.removeItem(at: dir) }
            let a = try file(dir, "a.txt", "alpha"), b = try file(dir, "b.txt", "beta")
            let plan = RenamePlan.preview([a, b], rules: RenameRules(prefix: "new-"))
            var calls = 0
            #expect(throws: (any Error).self) {
                try BatchRenamer.execute(plan) { from, to in
                    calls += 1
                    if calls == failAt { throw ToolError("injected failure") }
                    try BatchRenamer.move(from, to)
                }
            }
            #expect(try content(a) == "alpha"); #expect(try content(b) == "beta")
            #expect(Set(try FileManager.default.contentsOfDirectory(atPath: dir.path)) == ["a.txt", "b.txt"])
        }
    }

    @Test func renameNoReplaceIsAtomicAndUndoRejectsConflicts() throws {
        let dir = try folder(); defer { try? FileManager.default.removeItem(at: dir) }
        let a = try file(dir, "a.txt", "alpha"), b = try file(dir, "b.txt", "beta")
        #expect(throws: (any Error).self) { try BatchRenamer.move(a, b) }
        #expect(try content(a) == "alpha"); #expect(try content(b) == "beta")
        let receipt = try BatchRenamer.execute(RenamePlan.preview([a], rules: RenameRules(prefix: "new-")))
        _ = try file(dir, "a.txt", "new-original")
        #expect(throws: (any Error).self) { try receipt.undo() }
        #expect(try content(a) == "new-original"); #expect(try content(receipt.entries[0].destination) == "alpha")
    }

    @Test func renameReportsRecoveryLocationsWithoutOverwritingANewOriginal() throws {
        let dir = try folder(); defer { try? FileManager.default.removeItem(at: dir) }
        let a = try file(dir, "a.txt", "alpha"), b = try file(dir, "b.txt", "beta")
        let plan = RenamePlan.preview([a, b], rules: RenameRules(prefix: "new-"))
        var calls = 0
        do {
            _ = try BatchRenamer.execute(plan) { from, to in
                calls += 1
                if calls == 3 { try Data("blocker".utf8).write(to: a); throw ToolError("injected failure") }
                try BatchRenamer.move(from, to)
            }
            Issue.record("Rename must fail")
        } catch { #expect(error.localizedDescription.contains(".swivel-rename-")) }
        #expect(try content(a) == "blocker"); #expect(try content(b) == "beta")
        let staged = try FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil).filter { $0.lastPathComponent.hasPrefix(".swivel-rename-") }
        #expect(staged.count == 1); #expect(try content(staged[0]) == "alpha")
    }

    @Test func pdfRangeValidationAndPageMovement() throws {
        #expect(try PDFPageOrder.parse("1, 3-5，3", count: 6) == IndexSet([0, 2, 3, 4]))
        for invalid in ["", "0", "7", "5-3", "1,", "x", "1--2", "1-99999999999"] {
            #expect(throws: (any Error).self) { try PDFPageOrder.parse(invalid, count: 6) }
        }
        let pages = (0..<5).map { PDFPageItem(source: URL(fileURLWithPath: "/tmp/a.pdf"), page: $0) }
        #expect(PDFPageOrder.moving(pages, indexes: [1, 2], to: 5).map(\.page) == [0, 3, 4, 1, 2])
        #expect(PDFPageOrder.moving(pages, indexes: [3, 4], to: 0).map(\.page) == [3, 4, 0, 1, 2])
        #expect(PDFPageOrder.moving(pages, indexes: [1, 2], to: 2) == pages)
    }

    @Test func pdfExportsExactOrderRotationSelectionAndUniqueFiles() throws {
        let dir = try folder(); defer { try? FileManager.default.removeItem(at: dir) }
        let source = dir.appendingPathComponent("source.pdf"); try pdf(source, labels: ["PAGE A", "PAGE B", "PAGE C"])
        let original = try Data(contentsOf: source), snapshot = try PDFPagesSnapshot.load([source])
        var rotated = snapshot.items[2]; rotated.rotation = 90
        let output = try snapshot.export([rotated, snapshot.items[0]])
        let second = try snapshot.export([snapshot.items[1]])
        let read = PDFDocument(url: output)!
        #expect(read.pageCount == 2); #expect(read.page(at: 0)?.string?.contains("PAGE C") == true)
        #expect(read.page(at: 1)?.string?.contains("PAGE A") == true); #expect(read.page(at: 0)?.rotation == 90)
        #expect(PDFDocument(url: second)?.pageCount == 1); #expect(output != second)
        #expect(try Data(contentsOf: source) == original)
        #expect(output.deletingLastPathComponent() == dir)
        #expect(throws: (any Error).self) { try snapshot.export([]) }
    }

    @Test func pdfSnapshotsSurviveExternalEditsAndCombineDifferentSources() throws {
        let dir = try folder(); defer { try? FileManager.default.removeItem(at: dir) }
        let a = dir.appendingPathComponent("a.pdf"), b = dir.appendingPathComponent("b.pdf")
        try pdf(a, labels: ["ALPHA"]); try pdf(b, labels: ["BETA"])
        let snapshot = try PDFPagesSnapshot.load([a, b])
        try Data("changed".utf8).write(to: a)
        let output = try snapshot.export(snapshot.items.reversed())
        let result = PDFDocument(url: output)!
        #expect(result.page(at: 0)?.string?.contains("BETA") == true)
        #expect(result.page(at: 1)?.string?.contains("ALPHA") == true)
        #expect(throws: (any Error).self) { try PDFPagesSnapshot.load([a]) }
        #expect(throws: (any Error).self) { try PDFPagesSnapshot.load([]) }
    }

    @Test func qrRecognitionReadsTextAndRotatedUnicodeURLs() async throws {
        let dir = try folder(); defer { try? FileManager.default.removeItem(at: dir) }
        let source = dir.appendingPathComponent("qr.png")
        let payload = "https://example.com/你好?q=a%26b"
        let code = qr(payload)
        let rotated = code.transformed(by: CGAffineTransform(rotationAngle: .pi / 2))
        try png(rotated, source)
        let original = try Data(contentsOf: source)
        let results = try await CodeRecognition.recognize([source])
        #expect(results.count == 1); #expect(results.first?.value == payload); #expect(results.first?.webURL != nil)
        #expect(try Data(contentsOf: source) == original)
    }

    @Test func qrFindsMultipleCodesAndDoesNotTreatTextAsAWebLink() async throws {
        let dir = try folder(); defer { try? FileManager.default.removeItem(at: dir) }
        let source = dir.appendingPathComponent("multiple.png")
        let left = qr("SWIVEL TEST"), right = qr("https://example.com/path")
        let width = max(left.extent.width, right.extent.width) + 100
        let canvas = CIImage(color: .white).cropped(to: CGRect(x: 0, y: 0, width: width * 2, height: width))
        let image = left.transformed(by: .init(translationX: 30, y: 30)).composited(over: right.transformed(by: .init(translationX: width + 30, y: 30)).composited(over: canvas))
        try png(image, source)
        let results = try await CodeRecognition.recognize([source])
        #expect(Set(results.map(\.value)) == ["SWIVEL TEST", "https://example.com/path"])
        #expect(results.first { $0.value == "SWIVEL TEST" }?.webURL == nil)
    }

    @Test func qrBlankAndBrokenImagesAndLinkSchemeChecks() async throws {
        let dir = try folder(); defer { try? FileManager.default.removeItem(at: dir) }
        let blank = dir.appendingPathComponent("blank.png")
        try png(CIImage(color: .white).cropped(to: CGRect(x: 0, y: 0, width: 300, height: 300)), blank)
        #expect(try await CodeRecognition.recognize([blank]).isEmpty)
        let bad = try file(dir, "broken.png")
        await #expect(throws: (any Error).self) { try await CodeRecognition.recognize([bad]) }
        for value in ["javascript:alert(1)", "file:///etc/passwd", "claude://code/new", "https://user:secret@example.com", "just text"] {
            #expect(DetectedCode(source: blank, kind: "QR", value: value).webURL == nil)
        }
    }

    @Test func assistantDraftsEncodePathsAndNeverSendOrPretendToAttach() throws {
        let files = [URL(fileURLWithPath: "/tmp/资料 A/#&?\".txt"), URL(fileURLWithPath: "/tmp/资料 A/b.txt")]
        for delivery in [FileAppTarget.Delivery.codexDraft, .claudeCodeDraft] {
            let link = try AssistantDraft.url(for: delivery, files: files)
            let c = URLComponents(url: link, resolvingAgainstBaseURL: false)!
            #expect(c.path == "/new")
            let parameters = Dictionary(uniqueKeysWithValues: c.queryItems!.map { ($0.name, $0.value!) })
            #expect(parameters[delivery == .codexDraft ? "prompt" : "q"] == AssistantDraft.referenceText(files))
            #expect(parameters[delivery == .codexDraft ? "path" : "folder"] == "/tmp/资料 A")
            #expect(parameters["file"] == nil && parameters["submit"] == nil && parameters["send"] == nil)
        }
        let mixed = try AssistantDraft.url(for: .codexDraft, files: [files[0], URL(fileURLWithPath: "/elsewhere/b.txt")])
        #expect(URLComponents(url: mixed, resolvingAgainstBaseURL: false)?.queryItems?.contains { $0.name == "path" } == false)
        #expect(throws: (any Error).self) { try AssistantDraft.url(for: .codexDraft, files: []) }
    }

    @Test func appFiltersPersistenceAndStableNames() throws {
        let defaults = UserDefaults.standard, key = "appTargets.v1", old = UserDefaults.standard.object(forKey: "appTargets.v1")
        defer { if let old { defaults.set(old, forKey: key) } else { defaults.removeObject(forKey: key) } }
        var target = FileAppTarget.starters[0]
        let id = target.actionID; target.name = "My Codex"; target.extensions = FileAppTarget.normalizedExtensions(".PNG, jpg，PNG pdf")
        #expect(target.actionID == id); #expect(target.extensions == ["png", "jpg", "pdf"])
        #expect(target.accepts([URL(fileURLWithPath: "/tmp/A.PNG")]))
        #expect(!target.accepts([URL(fileURLWithPath: "/tmp/A.PNG"), URL(fileURLWithPath: "/tmp/a.txt")]))
        Prefs.fileAppTargets = [target, target]
        #expect(Prefs.fileAppTargets == [target]); #expect(Catalog.displayTitle(id) == "My Codex")
        #expect(try JSONDecoder().decode(FileAppTarget.self, from: JSONEncoder().encode(target)) == target)
    }

    @Test func independentFeatureGatesIncludeWheelOffAndStaleActions() async throws {
        let defaults = UserDefaults.standard
        let features: [Feature] = [.batchRename, .pdfPages, .qrCodes, .appRouting]
        let keys = ["enabled", "appTargets.v1"] + features.map { "feature." + $0.rawValue }
        let before = keys.map { defaults.object(forKey: $0) }
        defer { for (key, value) in zip(keys, before) { if let value { defaults.set(value, forKey: key) } else { defaults.removeObject(forKey: key) } } }
        let image = [URL(fileURLWithPath: "/tmp/a.png")], pdf = [URL(fileURLWithPath: "/tmp/a.pdf")]
        for f in features { Prefs.setFeatureEnabled(f, false) }
        #expect(!Catalog.defaultItems(for: image, mode: .actions).contains { $0.title == "Batch Rename" || $0.title.hasPrefix("app.") })
        #expect(!Catalog.defaultItems(for: image, mode: .tools).contains { $0.title == "Read QR Codes" })
        #expect(!Catalog.defaultItems(for: pdf, mode: .tools).contains { $0.title == "Arrange PDF Pages" })
        Prefs.isEnabled = false
        for f in features { Prefs.setFeatureEnabled(f, true) }
        Prefs.fileAppTargets = FileAppTarget.starters
        #expect(QuickActions.makeCommands(files: image).contains { $0.id == "actions.Batch Rename" })
        #expect(QuickActions.makeCommands(files: image).contains { $0.id == "tools.Read QR Codes" })
        #expect(QuickActions.makeCommands(files: pdf).contains { $0.id == "tools.Arrange PDF Pages" })
        #expect(QuickActions.makeCommands(files: image).contains { $0.id == "apps.app.codex" })
        #expect(QuickActions.makeCommands(files: []).contains { $0.id == "choose.batchRename" })
        let oldAction = Catalog.defaultItems(for: image, mode: .tools).first { $0.title == "Read QR Codes" }!
        Prefs.setFeatureEnabled(.qrCodes, false)
        await #expect(throws: (any Error).self) { try await oldAction.run(image) }
        #expect(Feature.batchRename.isOn && Feature.pdfPages.isOn && Feature.appRouting.isOn)
    }
}

extension FileActionsTests {
    @Test func qrLibraryPersistsAcrossRestartsAndDeduplicatesWithoutNeedingOriginals() throws {
        let dir = try folder(); defer { try? FileManager.default.removeItem(at: dir) }
        let store = QRLibraryStore(directory: dir)
        let first = SavedQRCode(name: "Website", value: "https://example.com", collection: "Work", season: .autumn)
        #expect(try store.load().isEmpty)
        let saved = try store.add(first)
        let same = try store.add(SavedQRCode(name: "Duplicate", value: first.value, collection: "Work"))
        #expect(saved.id == same.id)
        let reloaded = try QRLibraryStore(directory: dir).load()
        #expect(reloaded == [first]); #expect(reloaded[0].season == .autumn)
        #expect((try FileManager.default.attributesOfItem(atPath: store.file.path)[.posixPermissions] as? NSNumber)?.intValue == 0o600)
    }

    @Test func qrLibraryUndoPreservesOtherWindowsChangesAndRefusesConflictingEdits() throws {
        let dir = try folder(); defer { try? FileManager.default.removeItem(at: dir) }
        let store = QRLibraryStore(directory: dir)
        let a = SavedQRCode(name: "A", value: "alpha"), b = SavedQRCode(name: "B", value: "beta")
        try store.save([a]); try store.save([]) // A deleted in one window.
        _ = try store.add(b) // B added by a second window.
        try store.revert(before: [a], after: [])
        #expect(Set(try store.load().map(\.id)) == [a.id, b.id])
        var changed = a; changed.name = "Edited elsewhere"
        try store.save([changed, b])
        #expect(throws: (any Error).self) { try store.revert(before: [], after: [a]) }
        #expect(try store.load().contains(changed))
    }

    @Test func qrLibraryInvalidAndCorruptedDataIsNeverSilentlyOverwritten() throws {
        let dir = try folder(); defer { try? FileManager.default.removeItem(at: dir) }
        let store = QRLibraryStore(directory: dir)
        let a = SavedQRCode(name: "Keep", value: "https://example.com")
        try store.save([a]); let before = try Data(contentsOf: store.file)
        #expect(throws: (any Error).self) { try store.save([SavedQRCode(name: "", value: "")]) }
        #expect(try Data(contentsOf: store.file) == before)
        try Data("broken data".utf8).write(to: store.file)
        #expect(throws: (any Error).self) { try store.add(a) }
        #expect(try String(contentsOf: store.file, encoding: .utf8) == "broken data")
    }

    @Test func generatedLibraryQRImagesRoundTripUnicodeAndKeepContentForEverySeason() async throws {
        let dir = try folder(); defer { try? FileManager.default.removeItem(at: dir) }
        for season in QRSeason.allCases {
            let value = SavedQRCode(name: "树", value: "https://example.com/资料?a=1&b=two#hello", season: season)
            let output = dir.appendingPathComponent(season.rawValue + ".png")
            try QRImage.png(value.value).write(to: output)
            let recognized = try await CodeRecognition.recognize([output])
            #expect(recognized.map(\.value) == [value.value])
        }
        #expect(throws: (any Error).self) { try QRImage.png("") }
        #expect(throws: (any Error).self) { try QRImage.png(String(repeating: "a", count: 2001)) }
        #expect(QRTreeView.seed("same") == QRTreeView.seed("same"))
        #expect(QRTreeView.seed("same") != QRTreeView.seed("different"))
    }

    @Test func nativeAppRoutingUsesExplicitFilesAndPrivateClipboardAndHandlesFailures() async throws {
        let dir = try folder(); defer { try? FileManager.default.removeItem(at: dir) }
        let fake = dir.appendingPathComponent("Fixture.app", isDirectory: true)
        try FileManager.default.createDirectory(at: fake.appendingPathComponent("Contents"), withIntermediateDirectories: true)
        let info: [String: Any] = ["CFBundleIdentifier": "local.swivel.routing-fixture", "CFBundlePackageType": "APPL", "CFBundleName": "Fixture", "CFBundleURLTypes": [["CFBundleURLSchemes": ["codex", "claude"]]]]
        try PropertyListSerialization.data(fromPropertyList: info, format: .xml, options: 0).write(to: fake.appendingPathComponent("Contents/Info.plist"))
        let source = try file(dir, "file & #.txt")
        let keys = ["feature.appRouting", "appTargets.v1"], defaults = UserDefaults.standard
        let before = keys.map { defaults.object(forKey: $0) }
        defer { for (key, value) in zip(keys, before) { if let value { defaults.set(value, forKey: key) } else { defaults.removeObject(forKey: key) } } }
        Prefs.setFeatureEnabled(.appRouting, true)
        let pasteboard = NSPasteboard.withUniqueName(); defer { pasteboard.releaseGlobally() }
        pasteboard.setString("keep clipboard", forType: .string)
        var target = FileAppTarget(id: "fixture", name: "Fixture", bundleID: "local.swivel.routing-fixture", appPath: fake.path, delivery: .openFiles)
        Prefs.fileAppTargets = [target]
        var calls = 0
        _ = try await FileAppRouting.run(target, files: [source], pasteboard: pasteboard) { urls, app in
            #expect(urls == [source]); #expect(app == fake); calls += 1
        }
        #expect(calls == 1); #expect(pasteboard.string(forType: .string) == "keep clipboard")
        target.delivery = .copyFiles; Prefs.fileAppTargets = [target]
        await #expect(throws: (any Error).self) {
            try await FileAppRouting.run(target, files: [source], pasteboard: pasteboard) { _, _ in throw ToolError("open failed") }
        }
        #expect(pasteboard.string(forType: .string) == "keep clipboard")
        _ = try await FileAppRouting.run(target, files: [source], pasteboard: pasteboard) { urls, _ in #expect(urls.isEmpty) }
        #expect(ToolUI.fileURLs(from: pasteboard) == [source])
        target.delivery = .codexDraft; Prefs.fileAppTargets = [target]
        _ = try await FileAppRouting.run(target, files: [source], pasteboard: pasteboard) { urls, _ in
            #expect(urls.count == 1 && urls[0].scheme == "codex")
        }
        var edited = target; edited.name = "Edited"; Prefs.fileAppTargets = [edited]
        await #expect(throws: (any Error).self) {
            try await FileAppRouting.run(target, files: [source], pasteboard: pasteboard) { _, _ in Issue.record("A stale action must not open an app") }
        }
    }

    @Test func wheelKeyMigrationKeepsCustomKeysAndAssignsUniqueNewKeys() {
        let defaults = UserDefaults.standard, keys = WheelMode.allCases.map { "key." + $0.id }
        let before = keys.map { defaults.object(forKey: $0) }
        defer { for (key, value) in zip(keys, before) { if let value { defaults.set(value, forKey: key) } else { defaults.removeObject(forKey: key) } } }
        keys.forEach(defaults.removeObject(forKey:))
        defaults.set("option", forKey: "key.convert"); defaults.set("fn", forKey: "key.tools")
        #expect(Prefs.key(for: .convert) == .option && Prefs.key(for: .tools) == .fn)
        #expect(Set(WheelMode.allCases.map { Prefs.key(for: $0) }).count == 5)
        Prefs.setKey(.option, for: .apps)
        #expect(Prefs.key(for: .apps) == .option)
        #expect(Set(WheelMode.allCases.map { Prefs.key(for: $0) }).count == 5)
    }

    @Test func clickableWheelKeyboardSelectsAndCancelsWithoutADrag() {
        let view = WheelView(frame: NSRect(x: 0, y: 0, width: 340, height: 340))
        view.isClickable = true
        view.configure(items: [WheelItem(title: "A") { _ in ActionResult(message: "") }, WheelItem(title: "B") { _ in ActionResult(message: "") }], mode: .tools, page: 0, pageCount: 2)
        var chosen: String?, cancelled = false, paged = false
        view.onClick = { chosen = $0.title }; view.onCancel = { cancelled = true }; view.onNextPage = { paged = true }
        func key(_ code: UInt16) -> NSEvent { NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: [], timestamp: 0, windowNumber: 0, context: nil, characters: "", charactersIgnoringModifiers: "", isARepeat: false, keyCode: code)! }
        view.keyDown(with: key(125)); #expect(view.hovered == 0)
        view.keyDown(with: key(124)); #expect(view.hovered == 1)
        view.keyDown(with: key(36)); #expect(chosen == "B")
        view.keyDown(with: key(53)); #expect(cancelled)
        view.keyDown(with: key(49)); #expect(paged)
    }

    @Test func newWheelsRespectFeatureSwitchesAndStandaloneLaunchers() {
        let defaults = UserDefaults.standard
        let features: [Feature] = [.batchRename, .pdfPages, .qrCodes, .appRouting]
        let keys = features.map { "feature." + $0.rawValue }
        let before = keys.map { defaults.object(forKey: $0) }
        defer { for (key, value) in zip(keys, before) { if let value { defaults.set(value, forKey: key) } else { defaults.removeObject(forKey: key) } } }
        features.forEach { Prefs.setFeatureEnabled($0, false) }
        #expect(Catalog.items(for: [], mode: .apps).isEmpty && Catalog.items(for: [], mode: .codes).isEmpty)
        #expect(ShortcutWheel.shared.items().map(\.title) == ["Open Swivel…"])
        features.forEach { Prefs.setFeatureEnabled($0, true) }
        #expect(Catalog.items(for: [], mode: .codes).first?.title == "QR Library")
        #expect(!Catalog.items(for: [], mode: .apps).isEmpty)
        #expect(Set(ShortcutWheel.shared.items().map(\.title)).isSuperset(of: ["Batch Rename", "Arrange PDF Pages", "Read QR Codes", "QR Library", "App Actions"]))
    }

    @Test func prepareFileActionPreviewFixtures() throws {
        guard let path = ProcessInfo.processInfo.environment["SWIVEL_FILE_ACTIONS_FIXTURES"] else { return }
        let directory = URL(fileURLWithPath: path, isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        _ = try file(directory, "photo-one.txt", "first fixture"); _ = try file(directory, "photo-two.txt", "second fixture")
        try pdf(directory.appendingPathComponent("pages.pdf"), labels: ["PAGE ONE", "PAGE TWO", "PAGE THREE", "PAGE FOUR"])
        try QRImage.png("https://example.com/swivel-test").write(to: directory.appendingPathComponent("code.png"))
        let library = QRLibraryStore(directory: directory.appendingPathComponent("library"))
        try library.save([SavedQRCode(name: "Swivel", value: "https://example.com/swivel", collection: "Work"), SavedQRCode(name: "Notes", value: "Local test note", collection: "Personal", season: .spring)])
    }
}

extension FileActionsTests {
    @Test func renderTreesAndFlatCodesWhenRequested() async throws {
        guard let path = ProcessInfo.processInfo.environment["SWIVEL_FILE_ACTIONS_RENDERS"] else { return }
        let directory = URL(fileURLWithPath: path, isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        for season in QRSeason.allCases {
            let view = QRTreeView(frame: NSRect(x: 0, y: 0, width: 620, height: 520))
            let value = SavedQRCode(name: "Swivel", value: "https://example.com/swivel", season: season)
            try view.display(value)
            for showCode in [false, true] {
                view.setShowsCode(showCode)
                let rep = view.bitmapImageRepForCachingDisplay(in: view.bounds)!
                view.cacheDisplay(in: view.bounds, to: rep)
                let output = directory.appendingPathComponent("tree-\(season.rawValue)-\(showCode ? "qr" : "tree").png")
                try rep.representation(using: .png, properties: [:])!.write(to: output)
                if showCode {
                    let recognized = try await CodeRecognition.recognize([output])
                    #expect(recognized.map(\.value) == [value.value])
                }
            }
        }
    }
}
