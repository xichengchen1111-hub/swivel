import AppKit
import AVFoundation
import CoreImage
import Testing
import UniformTypeIdentifiers
import VideoToolbox
@testable import Swivel

@Suite(.serialized)
struct UpscaleTests {
    @Test func fallbackSizesNamesAndOriginalBytes() throws {
        let folder = try MediaBatchFixtures.folder()
        defer { try? FileManager.default.removeItem(at: folder) }
        let original = try #require(CIContext().createCGImage(CIImage(color: .red).cropped(to: CGRect(x: 0, y: 0, width: 120, height: 80)), from: CGRect(x: 0, y: 0, width: 120, height: 80)))
        for (ext, type) in [("png", UTType.png), ("jpg", UTType.jpeg)] {
            let source = try Images.save(original, as: folder.appendingPathComponent("photo.\(ext)"), type: type, quality: 1)
            let before = try Data(contentsOf: source)
            for factor in [2, 4] {
                let out = try Upscale.save(Upscale.ordinary(original, factor: factor), source: source, factor: factor)
                #expect(out.lastPathComponent == "photo \(factor)x.\(ext)")
                let decoded = try Images.load(out)
                #expect(decoded.width == 120 * factor && decoded.height == 80 * factor)
            }
            #expect(try Data(contentsOf: source) == before)
        }
        #expect(throws: (any Error).self) { try Upscale.dimensions(width: 50000, height: 50000, factor: 4) }
        if #available(macOS 26, *) {
            #expect(throws: (any Error).self) { try SystemUpscaler(width: 1921, height: 1920, factor: 2, video: false) }
        }
    }
    @Test @MainActor func menusKeepImageAndVideoActionsNearResizeAndCompression() throws {
        let image = URL(fileURLWithPath: "/nonexistent/photo.png"), video = URL(fileURLWithPath: "/nonexistent/clip.mov")
        let titles = Catalog.defaultItems(for: [image], mode: .tools).map(\.title)
        #expect(titles.firstIndex(of: "Upscale") == titles.firstIndex(of: "Resize…").map { $0 - 1 })
        let movies = Catalog.defaultItems(for: [video], mode: .tools).map(\.title)
        #expect(movies.firstIndex(of: "Upscale") == movies.firstIndex(of: "Compress").map { $0 - 1 })
        for source in [image, video] { #expect(!MCPCatalog.actions(for: [source]).contains { $0.id == "tools.Upscale" }) }
        #expect(Strings.table["Upscale"]?.count == 3)
    }
    @Test func cancellingAVideoEnlargementLeavesNoPartialOutput() async throws {
        let folder = try MediaBatchFixtures.folder()
        defer { try? FileManager.default.removeItem(at: folder) }
        let source = try await MediaBatchFixtures.video(in: folder)
        let before = try FileManager.default.contentsOfDirectory(atPath: folder.path).sorted()
        let task = Task {
            try await Upscale.video(source, engine: SystemUpscalerBox { image, _ in Upscale.lanczos(image, factor: 2) }) { _ in }
        }
        task.cancel()
        do { _ = try await task.value; Issue.record("Cancelled upscale unexpectedly completed") }
        catch is CancellationError {} catch {
            #expect((error as NSError).domain == AVFoundationErrorDomain)
        }
        #expect(try FileManager.default.contentsOfDirectory(atPath: folder.path).sorted() == before)
    }

    @Test func cancellingTheFinalStillWorkerBeforeEncodingPublishesNothing() async throws {
        let folder = try MediaBatchFixtures.folder()
        defer { try? FileManager.default.removeItem(at: folder) }
        let image = CIImage(color: .red).cropped(to: CGRect(x: 0, y: 0, width: 64, height: 64))
        let cg = try #require(CIContext().createCGImage(image, from: image.extent))
        let source = try Images.save(cg, as: folder.appendingPathComponent("photo.png"), type: .png, quality: 1)
        let original = try Data(contentsOf: source)
        let files = try FileManager.default.contentsOfDirectory(atPath: folder.path).sorted()
        let gate = UpscaleRenderGate()
        let task = Task {
            try await Upscale.finishStill(image, source: source, factor: 2) { _ in
                gate.blockRenderer()
                return cg
            }
        }
        await gate.waitForRenderer()
        task.cancel()
        gate.releaseRenderer()
        do { _ = try await task.value; Issue.record("Cancelled still worker published an output") }
        catch is CancellationError {} catch { Issue.record("Unexpected cancellation error: \(error)") }
        #expect(try FileManager.default.contentsOfDirectory(atPath: folder.path).sorted() == files)
        #expect(try Data(contentsOf: source) == original)
    }

    @Test func lanczosBackProjectionKeepsOpaqueBordersAndFiniteDimensions() throws {
        let source = CIImage(color: CIColor(red: 0.5, green: 0.5, blue: 0.5))
            .cropped(to: CGRect(x: 0, y: 0, width: 32, height: 24))
        let projected = Upscale.lanczos(Upscale.lanczos(Upscale.lanczos(source, factor: 4), factor: 0.25), factor: 4)
        #expect(projected.extent == CGRect(x: 0, y: 0, width: 128, height: 96))
        let cg = try #require(CIContext().createCGImage(projected, from: projected.extent))
        let bytes = try Retouch.Raster(cg).bytes
        var maxColorError = 0, minAlpha = 255
        for i in stride(from: 0, to: bytes.count, by: 4) {
            for channel in 0..<3 { maxColorError = max(maxColorError, abs(Int(bytes[i + channel]) - 128)) }
            minAlpha = min(minAlpha, Int(bytes[i + 3]))
        }
        #expect(maxColorError <= 2 && minAlpha == 255)
    }

    @Test(.enabled("Requires an available system model") { systemUpscaleReady() })
    func systemUpscalePreservesTransparentColorsAndSoftEdges() async throws {
        if #available(macOS 26, *) {
            let context = CIContext()
            let engine = try SystemUpscaler(width: 128, height: 128, factor: 4, video: false)
            try engine.start()
            func raster(_ image: CIImage) throws -> Retouch.Raster {
                try Retouch.Raster(#require(context.createCGImage(image, from: image.extent)))
            }
            func centre(_ image: CIImage) throws -> [UInt8] {
                let r = try raster(image), i = (r.width * (r.height / 2) + r.width / 2) * 4
                return Array(r.bytes[i..<i + 4])
            }
            for alpha in [0.0, 0.25, 0.5, 0.75, 1.0] {
                let input = CIImage(color: CIColor(red: 0.8, green: 0.2, blue: 0.1, alpha: alpha))
                    .cropped(to: CGRect(x: 0, y: 0, width: 128, height: 128))
                let actual = try centre(await engine.render(input)), expected = try centre(input)
                for channel in 0..<4 { #expect(abs(Int(actual[channel]) - Int(expected[channel])) <= 2) }
                if alpha == 0.5 {
                    // Negative control: restoring alpha twice preserves alpha-related dimensions but corrupts colour.
                    let wrong = try centre(input.applyingFilter("CISourceInCompositing", parameters: [kCIInputBackgroundImageKey: input]))
                    #expect(abs(Int(wrong[0]) - Int(expected[0])) > 20)
                }
            }
            var soft = Retouch.Raster(width: 128, height: 128, bytes: [UInt8](repeating: 0, count: 128 * 128 * 4))
            for y in 0..<128 { for x in 0..<128 {
                let alpha = min(1, max(0, (48 - hypot(Double(x) - 63.5, Double(y) - 63.5)) / 12))
                let i = (y * 128 + x) * 4
                for (channel, value) in [204.0, 51.0, 25.0, 255.0].enumerated() { soft.bytes[i + channel] = UInt8((value * alpha).rounded()) }
            } }
            let source = CIImage(cgImage: try soft.image())
            let output = try await engine.render(source)
            let actual = try raster(output), expected = try raster(Upscale.lanczos(source, factor: 4))
            var maxColorError = 0, maxAlphaError = 0, wrongError = 0, transparentPixelsEmpty = true
            let wrong = try raster(output.applyingFilter("CISourceInCompositing", parameters: [kCIInputBackgroundImageKey: Upscale.lanczos(source, factor: 4)]))
            for y in 0..<actual.height { for x in 0..<actual.width {
                let i = (y * actual.width + x) * 4
                maxAlphaError = max(maxAlphaError, abs(Int(actual.bytes[i + 3]) - Int(expected.bytes[i + 3])))
                // Compare visible soft-edge pixels; fully transparent RGB must remain exactly empty.
                if (32...224).contains(expected.bytes[i + 3]) {
                    for channel in 0..<3 {
                        maxColorError = max(maxColorError, abs(Int(actual.bytes[i + channel]) - Int(expected.bytes[i + channel])))
                        wrongError = max(wrongError, abs(Int(wrong.bytes[i + channel]) - Int(expected.bytes[i + channel])))
                    }
                } else if expected.bytes[i + 3] == 0 {
                    transparentPixelsEmpty = transparentPixelsEmpty && actual.bytes[i] == 0 && actual.bytes[i + 1] == 0 && actual.bytes[i + 2] == 0
                }
            } }
            print("Upscale soft-edge error: corrected \(maxColorError), double-alpha negative control \(wrongError), alpha \(maxAlphaError)")
            #expect(maxColorError <= 6 && maxAlphaError <= 1)
            #expect(wrongError > 20)
            #expect(transparentPixelsEmpty)
        }
    }

    @Test(.enabled("Requires an available system model") { systemUpscaleReady() })
    func systemUpscaleDimensionsAndReportsPSNRForHumanReview() async throws {
        if #available(macOS 26, *) {
            let reference = try Images.load(URL(fileURLWithPath: "/Library/User Pictures/Animals/Parrot.heic"), maxPixels: 512)
            let small = try Images.resized(reference, longEdge: 128)
            func bytes(_ cg: CGImage) throws -> [UInt8] { try Retouch.Raster(cg).bytes }
            let original = try bytes(reference)
            func psnr(_ cg: CGImage) throws -> Double {
                let values = try bytes(cg)
                #expect(values.count == original.count)
                let mse = zip(values, original).reduce(0.0) { $0 + pow(Double($1.0) - Double($1.1), 2) } / Double(original.count)
                return 10 * log10(255 * 255 / max(mse, 0.000001))
            }
            let engine = try SystemUpscaler(width: small.width, height: small.height, factor: 4, video: false)
            try engine.start()
            let result = try await engine.render(CIImage(cgImage: small))
            let cg = try #require(CIContext().createCGImage(result, from: result.extent))
            #expect(cg.width == small.width * 4 && cg.height == small.height * 4)
            let ai = try psnr(cg), regular = try psnr(Upscale.ordinary(small, factor: 4))
            print("Upscale PSNR: AI \(ai), Lanczos \(regular)")
            let grey = CIImage(color: CIColor(red: 0.5, green: 0.5, blue: 0.5)).cropped(to: CGRect(x: 0, y: 0, width: small.width, height: small.height))
            let greyOutput = try await engine.render(grey)
            let greyCG = try #require(CIContext().createCGImage(greyOutput, from: greyOutput.extent))
            let greyBytes = try bytes(greyCG)
            let centre = (greyCG.width * (greyCG.height / 2) + greyCG.width / 2) * 4
            for channel in 0..<3 { #expect(abs(Int(greyBytes[centre + channel]) - 128) <= 2) }
            var landmark = Retouch.Raster(width: small.width, height: small.height,
                bytes: [UInt8](repeating: 0, count: small.width * small.height * 4))
            for y in 0..<small.height { for x in 0..<small.width {
                let i = (y * small.width + x) * 4
                let v: UInt8 = (35..<67).contains(x) && (42..<74).contains(y) ? 220 : 0
                landmark.bytes[i] = v; landmark.bytes[i + 1] = v; landmark.bytes[i + 2] = v; landmark.bytes[i + 3] = 255
            } }
            let landmarkImage = try landmark.image()
            let landmarkOutput = try await engine.render(CIImage(cgImage: landmarkImage))
            let landmarkCG = try #require(CIContext().createCGImage(landmarkOutput, from: landmarkOutput.extent))
            func centroid(_ image: CGImage) throws -> CGPoint {
                let p = try bytes(image)
                var mass = 0.0, mx = 0.0, my = 0.0
                for y in 0..<image.height { for x in 0..<image.width {
                    let value = Double(p[(y * image.width + x) * 4])
                    mass += value; mx += value * Double(x); my += value * Double(y)
                } }
                return CGPoint(x: mx / mass, y: my / mass)
            }
            let modelCentre = try centroid(landmarkCG)
            #expect(abs(modelCentre.x - 203.5) < 0.2 && abs(modelCentre.y - 231.5) < 0.2)
            print("Upscale independent landmark: \(modelCentre), expected (203.5, 231.5)")
            // Owner deferred the superiority gate to visual review on 2026-10-22. Keep the measured comparison.
            #expect(ai.isFinite && regular.isFinite && ai > 0 && regular > 0)
            let twice = try SystemUpscaler(width: small.width, height: small.height, factor: 2, video: false)
            try twice.start()
            let result2 = try await twice.render(CIImage(cgImage: small))
            #expect(result2.extent.width == Double(small.width * 2) && result2.extent.height == Double(small.height * 2))
        }
    }
    @Test(.enabled("Requires an available system model") { systemUpscaleReady() })
    func systemVideoUpscaleKeepsSoundDurationAndSource() async throws {
        if #available(macOS 26, *) {
            let folder = try MediaBatchFixtures.folder()
            defer { try? FileManager.default.removeItem(at: folder) }
            let audio = try MediaBatchFixtures.tone(in: folder)
            let source = try await MediaBatchFixtures.video(in: folder, audio: audio)
            let before = try Data(contentsOf: source)
            let engine = try SystemUpscaler(width: 320, height: 240, factor: 2, video: true)
            try engine.start()
            let box = SystemUpscalerBox { image, time in try await engine.render(image, time: time) }
            let output = try await Upscale.video(source, engine: box) { _ in }
            let asset = try await Video.safeAsset(output)
            let video = try #require(try await asset.loadTracks(withMediaType: .video).first)
            #expect(try await video.load(.naturalSize) == CGSize(width: 640, height: 480))
            #expect(try await abs(asset.load(.duration).seconds - 1) < 0.02)
            #expect(try await !asset.loadTracks(withMediaType: .audio).isEmpty)
            #expect(try Data(contentsOf: source) == before)
            #expect(output.lastPathComponent == "clip 2x.mov")
        }
    }
}

private final class UpscaleRenderGate: @unchecked Sendable {
    private let began = DispatchSemaphore(value: 0)
    private let proceed = DispatchSemaphore(value: 0)
    func blockRenderer() { began.signal(); proceed.wait() }
    func releaseRenderer() { proceed.signal() }
    func waitForRenderer() async {
        await withCheckedContinuation { continuation in
            DispatchQueue.global().async { self.began.wait(); continuation.resume() }
        }
    }
}
private func systemUpscaleReady() -> Bool {
    if #available(macOS 26, *) {
        return VTSuperResolutionScalerConfiguration.isSupported &&
            VTSuperResolutionScalerConfiguration(frameWidth: 320, frameHeight: 240, scaleFactor: 4, inputType: .image,
                usePrecomputedFlow: false, qualityPrioritization: .normal, revision: .revision1)?.configurationModelStatus == .ready
    }
    return false
}
