import AppKit
import CoreImage
import ImageIO
import Testing
@testable import Swivel

@Suite(.serialized)
struct IDPhotoTests {
    let face = IDFace(bounds: CGRect(x: 0.3, y: 0.4, width: 0.4, height: 0.45), eyes: CGPoint(x: 0.5, y: 0.65), chin: 0.4, headTop: 0.9)
    @Test func cropKeepsTheHeadAndEyesAtTheirRatiosAndPadsAtEdges() throws {
        let crop = try #require(IDPhoto.crop(image: CGSize(width: 1000, height: 1200), output: CGSize(width: 295, height: 413), face: face))
        #expect(abs(crop.width / crop.height - 295.0 / 413) < 0.000001)
        #expect(abs(600 / crop.height - 0.7) < 0.000001)
        #expect(abs((780 - crop.minY) / crop.height - 0.55) < 0.000001)
        #expect(abs((500 - crop.minX) / crop.width - 0.5) < 0.000001)
        let zoomed = try #require(IDPhoto.crop(image: CGSize(width: 1000, height: 1200), output: CGSize(width: 295, height: 413), face: face, zoom: 1.2, vertical: 0.1))
        #expect(abs(600 / zoomed.height - 0.84) < 0.000001)
        #expect(abs((780 - zoomed.minY) / zoomed.height - 0.65) < 0.000001)
        var edge = face; edge.eyes.x = 0.03
        let padded = try #require(IDPhoto.crop(image: CGSize(width: 1000, height: 1200), output: CGSize(width: 600, height: 600), face: edge))
        #expect(padded.minX < 0 && padded.width == padded.height)
        #expect(IDPhoto.crop(image: .zero, output: CGSize(width: 1, height: 1), face: face) == nil)
        #expect(throws: (any Error).self) { try IDPhoto.singleFace([]) }
        #expect(throws: (any Error).self) { try IDPhoto.singleFace([face, face]) }
    }
    private func synthetic() throws -> (CGImage, CGImage) {
        let w = 128, h = 128
        var pixels = [UInt8](repeating: 255, count: w * h * 4), mask = pixels
        for y in 0..<h { for x in 0..<w {
            var alpha = 0.0
            if (20..<110).contains(y) { alpha = min(1, max(0, min(Double(x - 48) / 4, Double(94 - x) / 4))) }
            let i = (y * w + x) * 4
            pixels[i] = UInt8((220 * alpha).rounded()); pixels[i + 1] = 0; pixels[i + 2] = UInt8((220 * (1 - alpha)).rounded())
            mask[i] = UInt8((255 * alpha).rounded()); mask[i + 1] = mask[i]; mask[i + 2] = mask[i]
        } }
        return (try Retouch.Raster(width: w, height: h, bytes: pixels).image(), try Retouch.Raster(width: w, height: h, bytes: mask).image())
    }
    @Test func backgroundReplacementRemovesOldBlueFromSoftEdgesAndNaiveBlendFails() throws {
        let (image, mask) = try synthetic()
        let output = try #require(IDPhoto.replaceBackground(image, mask: mask, color: SIMD3(1, 1, 1)))
        let ctx = CIContext(), extent = CGRect(x: 0, y: 0, width: 128, height: 128)
        let clean = try Retouch.Raster(#require(ctx.createCGImage(output, from: extent)))
        let naive = CIImage(cgImage: image).applyingFilter("CIBlendWithMask", parameters: [
            kCIInputMaskImageKey: CIImage(cgImage: mask, options: [.colorSpace: NSNull()]),
            kCIInputBackgroundImageKey: CIImage(color: .white).cropped(to: extent)])
        let dirty = try Retouch.Raster(#require(ctx.createCGImage(naive, from: extent)))
        for x in [49, 50, 51] {
            let i = (64 * 128 + x) * 4
            #expect(abs(Int(clean.bytes[i + 2]) - Int(clean.bytes[i + 1])) < 6)
        }
        let edge = (64 * 128 + 50) * 4
        let naiveLeak = Int(dirty.bytes[edge + 2]) - Int(dirty.bytes[edge + 1])
        print("ID Photo old-background contamination: naive \(naiveLeak), corrected \(abs(Int(clean.bytes[edge + 2]) - Int(clean.bytes[edge + 1])))")
        // The same six-level tolerance used above must reject the ordinary blend.
        #expect(naiveLeak >= 6)
    }
    @Test func presetsCustomUnitsAndDPIOutputAreExactAndNeverReplaceTheSource() throws {
        let folder = try MediaBatchFixtures.folder()
        defer { try? FileManager.default.removeItem(at: folder) }
        let (image, mask) = try synthetic()
        let source = try Images.save(image, as: folder.appendingPathComponent("photo.png"), type: .png, quality: 1)
        let original = try Data(contentsOf: source)
        let expected = [(295, 413), (413, 579), (390, 567), (600, 600)]
        for index in 0..<4 {
            let (w, h) = try IDPhoto.dimensions(preset: index, width: 1, height: 1, millimeters: false, dpi: 300)
            #expect(w == expected[index].0 && h == expected[index].1)
            let settings = IDPhotoSettings(width: w, height: h)
            let result = try IDPhoto.render(image, mask: mask, face: face, settings: settings)
            #expect(result.width == w && result.height == h)
            let file = try IDPhoto.save(result, source: source, dpi: 300)
            if index == 0 { #expect(file.lastPathComponent == "photo id.jpg") }
            let decoder = try #require(CGImageSourceCreateWithURL(file as CFURL, nil))
            let props = try #require(CGImageSourceCopyPropertiesAtIndex(decoder, 0, nil) as? [CFString: Any])
            #expect(abs((props[kCGImagePropertyDPIWidth] as? Double ?? 0) - 300) < 0.1)
            #expect(abs((props[kCGImagePropertyDPIHeight] as? Double ?? 0) - 300) < 0.1)
        }
        let mm = try IDPhoto.dimensions(preset: 4, width: 25.4, height: 50.8, millimeters: true, dpi: 300)
        #expect(mm.0 == 300 && mm.1 == 600)
        #expect(throws: (any Error).self) { try IDPhoto.dimensions(preset: 4, width: 10000, height: 10000, millimeters: false, dpi: 300) }
        #expect(try Data(contentsOf: source) == original)
    }
    @Test func blankImageReportsNoFace() throws {
        let extent = CGRect(x: 0, y: 0, width: 256, height: 256)
        let image = try #require(CIContext().createCGImage(CIImage(color: .white).cropped(to: extent), from: extent))
        #expect(throws: (any Error).self) { try IDPhoto.detect(image) }
    }
    @Test @MainActor func menuIDAndTranslationsStayStable() throws {
        let source = URL(fileURLWithPath: "/nonexistent/photo.png")
        let titles = Catalog.defaultItems(for: [source], mode: .tools).map(\.title)
        #expect(titles.firstIndex(of: "ID Photo") == titles.firstIndex(of: "Add BG").map { $0 - 1 })
        #expect(!MCPCatalog.actions(for: [source]).contains { $0.id == "tools.ID Photo" })
        for key in IDPhoto.presetNames + ["ID Photo", "Vertical Position", "DPI", "Pixels", "Millimeters",
            "For convenience only. Follow the issuing authority's requirements for official use."] {
            #expect(Strings.table[key]?.count == 3)
        }
        _ = IDPhotoEditor(source: source)
    }
}
