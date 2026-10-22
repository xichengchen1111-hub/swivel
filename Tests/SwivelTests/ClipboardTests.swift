import AppKit
import Testing
import UniformTypeIdentifiers
@testable import Swivel

@Suite(.serialized)
@MainActor
struct ClipboardTests {
    /// A copied print is one clipboard item (pasting it into a chat app sends one attachment, not the file plus a
    /// TIFF), carrying the file and the picture. Uses a private pasteboard, never the user's clipboard.
    @Test func copiedPrintsAreOneItemWithTheFileAndThePicture() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("swivel-clipboard-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        let ctx = try #require(CGContext(data: nil, width: 40, height: 30, bitsPerComponent: 8, bytesPerRow: 0,
                                         space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                         bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        ctx.setFillColor(CGColor(red: 1, green: 0.4, blue: 0.2, alpha: 1)); ctx.fill(CGRect(x: 0, y: 0, width: 40, height: 30))
        let image = try #require(ctx.makeImage())
        let pasteboard = NSPasteboard(name: .init("swivel-test-\(UUID().uuidString)"))
        defer { pasteboard.releaseGlobally() }

        for (name, type) in [("Shot.png", UTType.png), ("Photo.jpg", .jpeg)] {
            let url = dir.appendingPathComponent(name)
            try Images.encode(image, type: type, quality: 0.9).write(to: url)
            pasteboard.clearContents()
            #expect(pasteboard.writeObjects([Clothesline.pasteboardItem(for: url)]))
            let items = try #require(pasteboard.pasteboardItems)
            #expect(items.count == 1, "\(name)")
            #expect(items[0].string(forType: .fileURL).flatMap(URL.init(string:))?.path == url.path)
            #expect(items[0].data(forType: NSPasteboard.PasteboardType(type.identifier)) == (try Data(contentsOf: url)))
            #expect(!items[0].types.contains(.tiff), "\(name) shouldn't carry a TIFF")
            // Apps that ask for PNG get one (the JPEG's is made on request).
            let png = try #require(items[0].data(forType: .png), "\(name)")
            #expect(png.starts(with: [0x89, 0x50, 0x4E, 0x47]))
        }
    }
}
