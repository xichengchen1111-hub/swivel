import AppKit
import ImageIO
import PDFKit
import Testing
import UniformTypeIdentifiers
import Vision
@testable import Swivel

/// Regression checks for the 2026-10 security review: output paths, subprocesses, archives and draft links.
@Suite(.serialized)
@MainActor
struct SecurityTests {
    private func folder() throws -> URL {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("swivel-security-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }

    @Test func aDanglingSymlinkAtTheOutputNameIsNeverWrittenThrough() async throws {
        let dir = try folder(); defer { try? FileManager.default.removeItem(at: dir) }
        let outside = try folder(); defer { try? FileManager.default.removeItem(at: outside) }
        let source = dir.appendingPathComponent("note.md")
        try "# hello".write(to: source, atomically: true, encoding: .utf8)
        let target = outside.appendingPathComponent("planted.txt")
        try FileManager.default.createSymbolicLink(at: dir.appendingPathComponent("note.txt"), withDestinationURL: target)

        let out = Paths.output(for: source, ext: "txt")
        #expect(out.lastPathComponent == "note 2.txt")
        #expect(Paths.taken(dir.appendingPathComponent("note.txt")))
        #expect(!Paths.taken(dir.appendingPathComponent("missing.txt")))
        // A real conversion lands beside the original and leaves the link's target uncreated.
        let txt = Docs.formats.first { $0.ext == "txt" }!
        let outputs = try await Docs.convert(source, to: txt)
        #expect(outputs.map(\.lastPathComponent) == ["note 2.txt"])
        #expect(!FileManager.default.fileExists(atPath: target.path))
    }

    @Test func shellDrainsLargeErrorOutputInsteadOfHanging() async throws {
        let start = Date()
        await #expect(throws: (any Error).self) {
            try await Shell.run("/usr/bin/perl", ["-e", "print STDERR 'x' x 400000; print STDERR 'last words'; exit 3"])
        }
        #expect(Date().timeIntervalSince(start) < 10)
        do {
            try await Shell.run("/usr/bin/perl", ["-e", "print STDERR 'y' x 100000; print STDERR 'tail-marker'; exit 1"])
        } catch {
            let message = error.localizedDescription
            #expect(message.hasSuffix("tail-marker"))
            #expect(message.utf8.count < 20_000)
        }
    }

    @Test func shellGivesToolsAnEmptyInputAndCanStreamFiles() async throws {
        let dir = try folder(); defer { try? FileManager.default.removeItem(at: dir) }
        // A tool that reads stdin gets end-of-file at once rather than waiting.
        try await Shell.run("/usr/bin/perl", ["-e", "my @all = <STDIN>; exit(scalar(@all) == 0 ? 0 : 1)"])
        let a = dir.appendingPathComponent("a.part"), b = dir.appendingPathComponent("b.part")
        try Data(repeating: 65, count: 3_000_000).write(to: a)
        try Data("tail".utf8).write(to: b)
        let joined = dir.appendingPathComponent("joined")
        try await Shell.run("/usr/bin/tee", [joined.path], input: [a, b])
        let data = try Data(contentsOf: joined)
        #expect(data.count == 3_000_004)
        #expect(data.suffix(4) == Data("tail".utf8))
    }

    @Test func shellWatchStopsAToolWithItsMessage() async throws {
        let start = Date()
        do {
            try await Shell.run("/bin/sleep", ["30"], watch: { "Not enough disk space" })
            Issue.record("The watched tool should have been stopped")
        } catch {
            #expect(error.localizedDescription == "Not enough disk space")
        }
        #expect(Date().timeIntervalSince(start) < 5)
    }

    @Test func draftReferencesShowHiddenCharactersButDecodeToTheExactPath() throws {
        let hidden = String(String.UnicodeScalarView([Unicode.Scalar(0xE0072)!, Unicode.Scalar(0xE0075)!, Unicode.Scalar(0xE006E)!]))
        let names = ["report\u{202E}fdp.txt", "a\u{200B}b.txt", "tag\(hidden).txt", "quote\"back\\slash\nline.txt", "资料 C++ & #1.txt", "smile ❤️.txt"]
        for name in names {
            let path = "/tmp/x/" + name
            let literal = AssistantDraft.visibleJSONString(path)
            let decoded = try JSONDecoder().decode(String.self, from: Data(literal.utf8))
            #expect(decoded == path)
            let invisible = literal.unicodeScalars.filter {
                [.format, .control, .privateUse, .unassigned].contains($0.properties.generalCategory)
                    || $0.properties.isDefaultIgnorableCodePoint || $0.properties.isVariationSelector
            }
            #expect(invisible.isEmpty, "\(name) leaves hidden characters in the draft")
        }
        #expect(AssistantDraft.visibleJSONString("/Users/me/a.txt") == "\"/Users/me/a.txt\"")
    }

    @Test func draftLinksEncodePlusSoFormDecodingKeepsThePath() throws {
        let files = [URL(fileURLWithPath: "/Users/me/C++ notes/a+b.txt")]
        for delivery in [FileAppTarget.Delivery.codexDraft, .claudeCodeDraft] {
            let link = try AssistantDraft.url(for: delivery, files: files)
            let query = try #require(URLComponents(url: link, resolvingAgainstBaseURL: false)?.percentEncodedQuery)
            #expect(!query.contains("+"))
            // Receivers decode like URLSearchParams: "+" is a space, then percent escapes.
            let form = Dictionary(uniqueKeysWithValues: query.split(separator: "&").map { pair -> (String, String) in
                let parts = pair.split(separator: "=", maxSplits: 1).map(String.init)
                let value = parts[1].replacingOccurrences(of: "+", with: " ").removingPercentEncoding!
                return (parts[0], value)
            })
            #expect(form[delivery == .codexDraft ? "path" : "folder"] == "/Users/me/C++ notes")
            #expect(form[delivery == .codexDraft ? "prompt" : "q"]?.contains("\"/Users/me/C++ notes/a+b.txt\"") == true)
        }
    }

    @Test func archiveKindsCoverRarAnd7zButNotDiskImagesOrWebArchives() {
        func kind(_ name: String) -> FileKind { FileKind.of(URL(fileURLWithPath: "/nonexistent/" + name)) }
        for name in ["a.rar", "a.7z", "a.part2.rar", "a.r00", "a.tar.xz", "a.iso", "a.zip", "a.tgz"] { #expect(kind(name) == .archive, "\(name)") }
        #expect(kind("Installer.dmg") == .other)
        #expect(kind("page.webarchive") == .other)
    }

    @Test func multiPartRarSetsAreFoundFromAnyPartAndGapsAreReported() throws {
        let dir = try folder(); defer { try? FileManager.default.removeItem(at: dir) }
        for n in 1...3 { try Data([UInt8(n)]).write(to: dir.appendingPathComponent("Holiday.part\(n).rar")) }
        let parts = try Archives.volumes(of: dir.appendingPathComponent("Holiday.part2.rar"))
        #expect(parts.map(\.lastPathComponent) == ["Holiday.part1.rar", "Holiday.part2.rar", "Holiday.part3.rar"])
        try FileManager.default.removeItem(at: dir.appendingPathComponent("Holiday.part2.rar"))
        #expect(throws: (any Error).self) { try Archives.volumes(of: dir.appendingPathComponent("Holiday.part3.rar")) }

        for name in ["Old.rar", "Old.r00", "Old.r01"] { try Data([1]).write(to: dir.appendingPathComponent(name)) }
        #expect(try Archives.volumes(of: dir.appendingPathComponent("Old.r01")).map(\.lastPathComponent) == ["Old.rar", "Old.r00", "Old.r01"])
        try Data([1]).write(to: dir.appendingPathComponent("Single.rar"))
        #expect(try Archives.volumes(of: dir.appendingPathComponent("Single.rar")).count == 1)
    }

    @Test func extractedFilesKeepTheDownloadQuarantineAndEncryptedArchivesFailFast() async throws {
        let dir = try folder(); defer { try? FileManager.default.removeItem(at: dir) }
        let content = dir.appendingPathComponent("Tool")
        try FileManager.default.createDirectory(at: content.appendingPathComponent("Inner"), withIntermediateDirectories: true)
        try "run".write(to: content.appendingPathComponent("Inner/run.sh"), atomically: true, encoding: .utf8)
        let archive = dir.appendingPathComponent("Tool.tgz")
        try await Shell.run("/usr/bin/tar", ["-czf", archive.path, "-C", dir.path, "Tool"])
        try FileManager.default.removeItem(at: content)
        let flag = "0081;6716b1c0;Safari;"
        #expect(setxattr(archive.path, "com.apple.quarantine", flag, flag.utf8.count, 0, 0) == 0)

        let outputs = try await Archives.extract(archive)
        #expect(outputs.map(\.lastPathComponent) == ["Tool"])
        for path in [outputs[0].path, outputs[0].appendingPathComponent("Inner/run.sh").path] {
            #expect(getxattr(path, "com.apple.quarantine", nil, 0, 0, 0) > 0, "\(path) lost its quarantine flag")
        }
        #expect(try FileManager.default.contentsOfDirectory(atPath: dir.path).allSatisfy { !$0.hasPrefix(".swivel-") })

        // An encrypted ZIP under another extension goes through tar: a clear message, at once, no staging left behind.
        let secret = dir.appendingPathComponent("s.txt")
        try "secret".write(to: secret, atomically: true, encoding: .utf8)
        let encrypted = dir.appendingPathComponent("locked.jar")
        try await Shell.run("/usr/bin/zip", ["-q", "-P", "pw", encrypted.path, "s.txt"], in: dir)
        let start = Date()
        do {
            _ = try await Archives.extract(encrypted)
            Issue.record("An encrypted archive should not extract")
        } catch {
            #expect(error.localizedDescription == L("This archive is password-protected. Swivel can't open encrypted archives yet."))
        }
        #expect(Date().timeIntervalSince(start) < 10)
        #expect(try FileManager.default.contentsOfDirectory(atPath: dir.path).allSatisfy { !$0.hasPrefix(".swivel-") })
    }

    @Test func sevenZipArchivesExtractBesideTheOriginal() async throws {
        let dir = try folder(); defer { try? FileManager.default.removeItem(at: dir) }
        let content = dir.appendingPathComponent("Photos 2026")
        try FileManager.default.createDirectory(at: content, withIntermediateDirectories: true)
        try Data(repeating: 7, count: 200_000).write(to: content.appendingPathComponent("a.raw"))
        try "notes".write(to: content.appendingPathComponent("说明.txt"), atomically: true, encoding: .utf8)
        let archive = dir.appendingPathComponent("Photos 2026.7z")
        try await Shell.run("/usr/bin/tar", ["--format", "7zip", "-cf", archive.path, "-C", dir.path, "Photos 2026"])
        try FileManager.default.removeItem(at: content)
        #expect(FileKind.of(archive) == .archive)
        let outputs = try await Archives.extract(archive)
        #expect(outputs.map(\.lastPathComponent) == ["Photos 2026"])
        #expect(try Data(contentsOf: outputs[0].appendingPathComponent("a.raw")).count == 200_000)
        #expect(try String(contentsOf: outputs[0].appendingPathComponent("说明.txt"), encoding: .utf8) == "notes")
    }

    @Test func webArchivesInDisguiseAreNotReadWithWebKit() async throws {
        let dir = try folder(); defer { try? FileManager.default.removeItem(at: dir) }
        let html = #"<html><body>Hello<iframe src="http://127.0.0.1:9/frame.html"></iframe><img src="file:///etc/hosts"></body></html>"#
        let archive: [String: Any] = ["WebMainResource": ["WebResourceData": Data(html.utf8), "WebResourceMIMEType": "text/html",
                                                          "WebResourceTextEncodingName": "UTF-8", "WebResourceURL": "https://example.com/"]]
        let data = try PropertyListSerialization.data(fromPropertyList: archive, format: .binary, options: 0)
        let txt = Docs.formats.first { $0.ext == "txt" }!
        for name in ["report.doc", "report.docx", "report.odt", "report.text"] {
            let url = dir.appendingPathComponent(name)
            try data.write(to: url)
            if let outputs = try? await Docs.convert(url, to: txt) {
                // Whatever the typed reader makes of it, nothing from the web or the disk comes along.
                let text = try String(contentsOf: outputs[0], encoding: .utf8)
                #expect(!text.contains("localhost") && !text.contains("broadcasthost"), "\(name)")
            }
        }
        #expect(FileKind.of(dir.appendingPathComponent("page.webarchive")) == .other)
    }

    @Test func readOnlyAndLockedItemsStillGetTheQuarantineFlag() async throws {
        let dir = try folder()
        defer { Archives.makeAccessible(dir); try? FileManager.default.removeItem(at: dir) }
        // The archive records the awkward modes (an mtree spec), so the fixture itself never needs unreadable folders.
        let payload = dir.appendingPathComponent("payload"), readme = dir.appendingPathComponent("readme")
        try "x".write(to: payload, atomically: true, encoding: .utf8)
        try "r".write(to: readme, atomically: true, encoding: .utf8)
        let spec = """
            #mtree
            Kit type=dir mode=0755
            Kit/readme.txt type=file mode=0444 contents=\(readme.path)
            Kit/locked type=dir mode=0300
            Kit/locked/deeper type=dir mode=0555
            Kit/locked/deeper/payload.command type=file mode=0644 contents=\(payload.path)

            """
        let specFile = dir.appendingPathComponent("spec.mtree")
        try spec.write(to: specFile, atomically: true, encoding: .utf8)
        try await Shell.run("/usr/bin/tar", ["-czf", dir.appendingPathComponent("Kit.tgz").path, "@" + specFile.path], in: dir)
        let archive = dir.appendingPathComponent("Kit.tgz")
        let flag = "0081;6716b1c0;Safari;"
        #expect(setxattr(archive.path, "com.apple.quarantine", flag, flag.utf8.count, 0, 0) == 0)
        let outputs = try await Archives.extract(archive)
        let root = try #require(outputs.first)
        for relative in ["", "readme.txt", "locked", "locked/deeper", "locked/deeper/payload.command"] {
            let path = relative.isEmpty ? root.path : root.appendingPathComponent(relative).path
            #expect(getxattr(path, "com.apple.quarantine", nil, 0, 0, XATTR_NOFOLLOW) > 0, "\(relative) has no quarantine flag")
        }
        var info = stat()
        #expect(lstat(root.appendingPathComponent("readme.txt").path, &info) == 0 && info.st_mode & 0o777 == 0o444)
        #expect(try FileManager.default.contentsOfDirectory(atPath: dir.path).allSatisfy { !$0.hasPrefix(".swivel-") })
    }

    @Test func archivingFilesFromSeveralFoldersIsRefusedNotTruncated() async throws {
        let one = try folder(), two = try folder()
        defer { try? FileManager.default.removeItem(at: one); try? FileManager.default.removeItem(at: two) }
        let a = one.appendingPathComponent("a.txt"), b = two.appendingPathComponent("b.txt")
        try "a".write(to: a, atomically: true, encoding: .utf8); try "b".write(to: b, atomically: true, encoding: .utf8)
        await #expect(throws: (any Error).self) { try await Archives.zip([a, b]) }
        await #expect(throws: (any Error).self) { try await Archives.tar([a, b]) }
    }

    @Test func optionLikeFileNamesAreArchivedNotObeyed() async throws {
        let dir = try folder(); defer { try? FileManager.default.removeItem(at: dir) }
        let names = ["a.dat", "-T", "--use-compress-program=touch pwned"]
        for name in names { try Data(name.utf8).write(to: dir.appendingPathComponent(name)) }
        let urls = names.map { dir.appendingPathComponent($0) }
        let zip = try await Archives.zip(urls)
        let tgz = try await Archives.tar(urls)
        for archive in [zip, tgz] {
            let listing = dir.appendingPathComponent("listing-\(archive.pathExtension).txt")
            try await Shell.run("/bin/sh", ["-c", "/usr/bin/tar -tf \"$1\" > \"$2\"", "sh", archive.path, listing.path])
            let entries = Set(try String(contentsOf: listing, encoding: .utf8).split(separator: "\n").map(String.init))
            #expect(entries == Set(names), "\(archive.lastPathComponent): \(entries)")
        }
        #expect(!FileManager.default.fileExists(atPath: dir.appendingPathComponent("pwned").path))
    }

    @Test func hugePdfPagesRenderWithinTheBudgetInsteadOfTrapping() throws {
        let page = PDFPage()
        page.setBounds(CGRect(x: 0, y: 0, width: 400_000, height: 300_000), for: .mediaBox)
        let image = try PDFs.render(page, dpi: 300)
        #expect(Double(image.width * image.height) <= PDFs.maxRenderPixels)
        #expect(Double(max(image.width, image.height)) <= PDFs.maxRenderSide)
        page.setBounds(CGRect(x: 0, y: 0, width: CGFloat.infinity, height: 10), for: .mediaBox)
        #expect(throws: (any Error).self) { try PDFs.render(page, dpi: 300) }
    }

    @Test func metadataRemovalLeavesNoLocationInAnyFormat() throws {
        let dir = try folder(); defer { try? FileManager.default.removeItem(at: dir) }
        let pixels = try #require(CGContext(data: nil, width: 64, height: 48, bitsPerComponent: 8, bytesPerRow: 0,
                                            space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        pixels.setFillColor(CGColor(red: 0.2, green: 0.5, blue: 0.8, alpha: 1)); pixels.fill(CGRect(x: 0, y: 0, width: 64, height: 48))
        let image = try #require(pixels.makeImage())
        let properties: [CFString: Any] = [
            kCGImagePropertyGPSDictionary: [kCGImagePropertyGPSLatitude: 51.5, kCGImagePropertyGPSLatitudeRef: "N",
                                            kCGImagePropertyGPSLongitude: 0.12, kCGImagePropertyGPSLongitudeRef: "W"],
            kCGImagePropertyIPTCDictionary: [kCGImagePropertyIPTCCity: "Secretville", kCGImagePropertyIPTCSubLocation: "12 Hidden Lane",
                                             kCGImagePropertyIPTCCountryPrimaryLocationName: "Nowhere"],
            kCGImagePropertyTIFFDictionary: [kCGImagePropertyTIFFMake: "CameraCo", kCGImagePropertyTIFFArtist: "A. Person"],
            kCGImagePropertyExifDictionary: [kCGImagePropertyExifDateTimeOriginal: "2026:10:21 09:00:00"],
        ]
        for (ext, type) in [("jpg", UTType.jpeg), ("png", .png), ("heic", .heic), ("tiff", .tiff)] {
            let url = dir.appendingPathComponent("photo.\(ext)")
            let dest = try #require(CGImageDestinationCreateWithURL(url as CFURL, type.identifier as CFString, 1, nil))
            CGImageDestinationAddImage(dest, image, properties as CFDictionary)
            #expect(CGImageDestinationFinalize(dest))
            let locationOnly = MetadataEditor.Removal(location: true, camera: false, date: false, author: false, everything: false)
            let cleaned = try MetadataEditor.cleaned(url, locationOnly)
            #expect(MetadataEditor.isClean(cleaned, locationOnly), "\(ext)")
            let text = String(decoding: cleaned, as: UTF8.self) + String(decoding: cleaned, as: Unicode.ASCII.self)
            #expect(!text.contains("Secretville") && !text.contains("Hidden Lane"), "\(ext) still names the place")
            let everything = MetadataEditor.Removal(location: false, camera: false, date: false, author: false, everything: true)
            let bare = try MetadataEditor.cleaned(url, everything)
            let bareText = String(decoding: bare, as: Unicode.ASCII.self)
            #expect(!bareText.contains("CameraCo") && !bareText.contains("A. Person") && !bareText.contains("Secretville"), "\(ext)")
        }
    }

    @Test func pixelateAndBlurHideLargeTextToo() throws {
        let size = CGSize(width: 1600, height: 1000)
        let ctx = try #require(CGContext(data: nil, width: Int(size.width), height: Int(size.height), bitsPerComponent: 8,
                                         bytesPerRow: 0, space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                         bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        ctx.setFillColor(.white); ctx.fill(CGRect(origin: .zero, size: size))
        let line = NSAttributedString(string: "PIN 4096", attributes: [.font: NSFont.boldSystemFont(ofSize: 220), .foregroundColor: NSColor.black])
        let framesetter = CTFramesetterCreateWithAttributedString(line)
        let frame = CTFramesetterCreateFrame(framesetter, CFRange(), CGPath(rect: CGRect(x: 120, y: 300, width: 1400, height: 400), transform: nil), nil)
        CTFrameDraw(frame, ctx)
        let image = try #require(ctx.makeImage())
        func words(_ image: CGImage) throws -> [String] {
            let request = VNRecognizeTextRequest()
            request.recognitionLevel = .accurate
            try VNImageRequestHandler(cgImage: image).perform([request])
            return (request.results ?? []).compactMap { $0.topCandidates(1).first?.string }
        }
        #expect(try words(image).joined().contains("4096"))
        let box = [CGRect(x: 0.05, y: 0.25, width: 0.9, height: 0.5)]
        for style in [0, 1] {
            let hidden = try #require(RedactEditor.render(image, rects: box, style: style))
            #expect(!(try words(hidden).joined().contains("4096")), "style \(style) leaves the text readable")
        }
    }
}
