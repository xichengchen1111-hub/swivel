import AppKit
import AVFoundation
import CoreImage
import ImageIO
import VideoToolbox

private actor UpscaleAdmission {
    private var busy = false
    private var waiting: [CheckedContinuation<Void, Never>] = []
    func enter() async {
        if busy { await withCheckedContinuation { waiting.append($0) } } else { busy = true }
    }
    func leave() {
        if waiting.isEmpty { busy = false } else { waiting.removeFirst().resume() }
    }
}

/// One system processor, serialized even when AVFoundation requests multiple frames concurrently.
@available(macOS 26, *)
final class SystemUpscaler: @unchecked Sendable {
    let configuration: VTSuperResolutionScalerConfiguration
    private let processor = VTFrameProcessor()
    private let admission = UpscaleAdmission()
    private let context = CIContext()
    let requestedFactor: Int
    init(width: Int, height: Int, factor: Int, video: Bool) throws {
        guard VTSuperResolutionScalerConfiguration.isSupported else { throw ToolError(L("AI upscaling is not supported on this Mac.")) }
        guard width <= 1920, height <= (video ? 1080 : 1920) else {
            throw ToolError(L("AI upscaling input is limited to %@. Choose a smaller file.", video ? "1920 × 1080" : "1920 × 1920"))
        }
        let factors = VTSuperResolutionScalerConfiguration.supportedScaleFactors
        // This Mac exposes only 4×. A 2× result is reconstructed at 4× and then reduced, retaining the AI pass.
        guard let internalFactor = factors.filter({ $0 >= factor }).min(),
              let config = VTSuperResolutionScalerConfiguration(frameWidth: width, frameHeight: height, scaleFactor: internalFactor,
                inputType: video ? .video : .image, usePrecomputedFlow: false, qualityPrioritization: .normal, revision: .revision1)
        else { throw ToolError(L("These dimensions or this scale are not supported by the system upscaler.")) }
        requestedFactor = factor
        configuration = config
    }
    func start() throws {
        guard configuration.configurationModelStatus == .ready else { throw ToolError(L("Upscaling resources are not installed.")) }
        try processor.startSession(configuration: configuration)
    }
    deinit { processor.endSession() }

    private func buffer(width: Int, height: Int, attributes: [String: Any]) throws -> CVPixelBuffer {
        var attrs = attributes
        attrs[kCVPixelBufferIOSurfacePropertiesKey as String] = [:] as [String: Any]
        var pixel: CVPixelBuffer?
        let format = configuration.supportedPixelFormats.first ?? kCVPixelFormatType_64RGBAHalf
        guard CVPixelBufferCreate(nil, width, height, format, attrs as CFDictionary, &pixel) == kCVReturnSuccess, let pixel else {
            throw ToolError(L("Couldn't allocate an upscaling frame."))
        }
        return pixel
    }
    func render(_ source: CIImage, time: CMTime = .zero) async throws -> CIImage {
        await admission.enter()
        do {
            try Task.checkCancellation()
            let w = configuration.frameWidth, h = configuration.frameHeight, factor = configuration.scaleFactor
            let input = try buffer(width: w, height: h, attributes: configuration.sourcePixelBufferAttributes)
            let output = try buffer(width: w * factor, height: h * factor, attributes: configuration.destinationPixelBufferAttributes)
            let colorSpace = CGColorSpace(name: CGColorSpace.sRGB)!
            CVBufferSetAttachment(input, kCVImageBufferCGColorSpaceKey, colorSpace, .shouldPropagate)
            CVBufferSetAttachment(output, kCVImageBufferCGColorSpaceKey, colorSpace, .shouldPropagate)
            // VT produces opaque colour. Sending premultiplied RGB would bake transparency into the colour,
            // then restoring alpha below would darken soft edges a second time.
            let colors = try Upscale.opaqueColors(source)
            context.render(colors, to: input, bounds: CGRect(x: 0, y: 0, width: w, height: h), colorSpace: colorSpace)
            guard let a = VTFrameProcessorFrame(buffer: input, presentationTimeStamp: time),
                  let b = VTFrameProcessorFrame(buffer: output, presentationTimeStamp: time),
                  let parameters = VTSuperResolutionScalerParameters(sourceFrame: a, previousFrame: nil, previousOutputFrame: nil,
                    opticalFlow: nil, submissionMode: .random, destinationFrame: b) else {
                throw ToolError(L("Couldn't allocate an upscaling frame."))
            }
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                processor.process(parameters: parameters) { _, error in
                    if let error { continuation.resume(throwing: error) } else { continuation.resume() }
                }
            }
            try Task.checkCancellation()
            var result = CIImage(cvPixelBuffer: output, options: [.colorSpace: colorSpace])
            // Back-project the reconstructed low frequencies onto the known input. The model may shift colour;
            // correcting that shift uses only the input, while retaining its synthesized fine detail.
            let baseline = Upscale.lanczos(colors, factor: Double(factor))
            guard let kernel = Upscale.consistencyKernel else { throw ToolError(L("Couldn't render")) }
            let reprojected = Upscale.lanczos(Upscale.lanczos(result, factor: 1 / Double(factor)), factor: Double(factor))
            guard let corrected = kernel.apply(extent: result.extent, arguments: [result, baseline, reprojected]) else { throw ToolError(L("Couldn't render")) }
            result = corrected
            if factor != requestedFactor {
                result = Upscale.lanczos(result, factor: Double(requestedFactor) / Double(factor))
            }
            // Keep transparency independently of the system model's supported colour buffer formats.
            let alpha = Upscale.lanczos(source, factor: Double(requestedFactor))
            result = result.applyingFilter("CISourceInCompositing", parameters: [kCIInputBackgroundImageKey: alpha])
            // Materialize before the next frame reuses the processor, without keeping its buffers alive indefinitely.
            guard let cg = context.createCGImage(result, from: CGRect(x: 0, y: 0, width: w * requestedFactor, height: h * requestedFactor)) else {
                throw ToolError(L("Couldn't render"))
            }
            await admission.leave()
            return CIImage(cgImage: cg)
        } catch { await admission.leave(); throw error }
    }
}

enum Upscale {
    private static let opaqueColorKernel = CIColorKernel(source: """
        kernel vec4 opaqueColors(__sample source, __sample nearby) {
            vec3 color = source.a > 0.000001 ? source.rgb / source.a :
                (nearby.a > 0.000001 ? nearby.rgb / nearby.a : vec3(0.0));
            return vec4(clamp(color, 0.0, 1.0), 1.0);
        }
        """)
    /// Preserve straight colour at soft edges and extend it into transparent neighbours before spatial processing.
    static func opaqueColors(_ image: CIImage) throws -> CIImage {
        let nearby = image.clampedToExtent().applyingFilter("CIGaussianBlur", parameters: [kCIInputRadiusKey: 16])
            .cropped(to: image.extent)
        guard let result = opaqueColorKernel?.apply(extent: image.extent, arguments: [image, nearby]) else {
            throw ToolError(L("Couldn't render"))
        }
        return result
    }
    static let consistencyKernel = CIColorKernel(source: """
        kernel vec4 consistency(__sample reconstructed, __sample known, __sample projected) {
            return vec4(clamp(reconstructed.rgb + known.rgb - projected.rgb, 0.0, 1.0), reconstructed.a);
        }
        """)
    static func dimensions(width: Int, height: Int, factor: Int) throws -> CGSize {
        guard width > 0, height > 0, [2, 4].contains(factor),
              Double(width) * Double(height) * Double(factor * factor) <= Images.maxDecodePixels else {
            throw ToolError(L("The enlarged image would be too large."))
        }
        return CGSize(width: width * factor, height: height * factor)
    }
    static func lanczos(_ image: CIImage, factor: Double) -> CIImage {
        // Resampling a finite image against transparent black leaves a bright outline after back-projection.
        // Extend its actual edge pixels, then restore a finite extent for the next render/scale operation.
        image.clampedToExtent().applyingFilter("CILanczosScaleTransform", parameters: [kCIInputScaleKey: factor, kCIInputAspectRatioKey: 1])
            .cropped(to: image.extent.applying(CGAffineTransform(scaleX: factor, y: factor)))
    }
    static func ordinary(_ image: CGImage, factor: Int) throws -> CGImage {
        let size = try dimensions(width: image.width, height: image.height, factor: factor)
        let output = lanczos(CIImage(cgImage: image), factor: Double(factor))
        guard let cg = CIContext().createCGImage(output, from: CGRect(origin: .zero, size: size)) else { throw ToolError(L("Couldn't render")) }
        return cg
    }
    static func save(_ image: CGImage, source: URL, factor: Int) throws -> URL {
        try Task.checkCancellation()
        let (type, ext) = Images.writableType(for: source)
        let data = try Images.encode(image, type: type, quality: 0.95)
        try Task.checkCancellation()
        // Exclusive publication is the commit point: a later cancellation may return this completed output.
        return try Paths.create(data, at: Paths.preferred(for: source, ext: ext, suffix: " \(factor)x"))
    }
    /// Own the last worker so Cancel reaches both rendering and the check between encoding and publication.
    static func finishStill(_ image: CIImage, source: URL, factor: Int,
                            render: @escaping @Sendable (CIImage) throws -> CGImage = { image in
        guard let cg = CIContext().createCGImage(image, from: image.extent) else { throw ToolError(L("Couldn't render")) }
        return cg
    }) async throws -> URL {
        let worker = Task.detached(priority: .userInitiated) {
            try Task.checkCancellation()
            let cg = try render(image)
            try Task.checkCancellation()
            return try save(cg, source: source, factor: factor)
        }
        return try await withTaskCancellationHandler {
            try await worker.value
        } onCancel: { worker.cancel() }
    }
    static func video(_ source: URL, engine: SystemUpscalerBox, progress: @escaping @Sendable (Double) -> Void) async throws -> URL {
        let asset = try await Video.safeAsset(source)
        guard let track = try await asset.loadTracks(withMediaType: .video).first else { throw ToolError(L("No video track")) }
        let size = try await VideoEdit.uprightSize(of: track)
        let factor = 2
        let composition = try await AVMutableVideoComposition.videoComposition(with: asset) { request in
            Task.detached {
                do {
                    let frame = try await engine.render(request.sourceImage, request.compositionTime)
                    request.finish(with: frame, context: nil)
                } catch { request.finish(with: error) }
            }
        }
        composition.renderSize = CGSize(width: size.width * Double(factor), height: size.height * Double(factor))
        guard let session = AVAssetExportSession(asset: asset, presetName: AVAssetExportPresetHighestQuality) else { throw ToolError(L("Couldn't encode video.")) }
        session.videoComposition = composition
        let monitor = Task {
            while !Task.isCancelled { progress(Double(session.progress)); try await Task.sleep(for: .milliseconds(100)) }
        }
        defer { monitor.cancel() }
        return try await Paths.produce(Paths.preferred(for: source, ext: "mov", suffix: " 2x")) { temporary in
            try await withTaskCancellationHandler {
                try Task.checkCancellation()
                try await session.export(to: temporary, as: .mov)
                try Task.checkCancellation()
            } onCancel: { session.cancelExport() }
        }
    }
}

/// The macOS-15 fallback is explicit. The system implementation is boxed so older OSes never instantiate it.
struct SystemUpscalerBox: Sendable {
    let render: @Sendable (CIImage, CMTime) async throws -> CIImage
}

final class UpscaleEditor: Editor {
    private let preview = ImagePreview()
    private let video: Bool
    private var factors: NSSegmentedControl!
    private let sizeLabel = NSTextField(labelWithString: "")
    private let explanation = NSTextField(wrappingLabelWithString: "")
    private let progress = NSProgressIndicator()
    private var sourceSize = CGSize.zero
    private var task: Task<[URL], Error>?
    private var progressTask: Task<Void, Never>?

    init(source: URL, video: Bool = false) {
        self.video = video
        super.init(title: "Upscale", source: source, canvas: preview)
        factors = segments(video ? ["2×"] : ["2×", "4×"])
        addRow("Scale", factors, sizeLabel)
        if #available(macOS 26, *) { explanation.stringValue = L("AI upscaling uses Apple's on-device model.") }
        else { explanation.stringValue = L("Ordinary Lanczos enlargement. AI upscaling requires macOS 26.") }
        explanation.font = .systemFont(ofSize: 11); explanation.textColor = .secondaryLabelColor
        addRow("", explanation)
        progress.isIndeterminate = false; progress.minValue = 0; progress.maxValue = 1
        progress.widthAnchor.constraint(equalToConstant: 240).isActive = true
        addRow("", progress)
    }
    override func load() async throws {
        let source = self.source
        if video {
            let asset = try await Video.safeAsset(source)
            guard let track = try await asset.loadTracks(withMediaType: .video).first else { throw ToolError(L("No video track")) }
            sourceSize = try await VideoEdit.uprightSize(of: track)
            let generator = AVAssetImageGenerator(asset: asset)
            generator.appliesPreferredTrackTransform = true; generator.maximumSize = CGSize(width: 1400, height: 1400)
            preview.image = try await generator.image(at: .zero).image
        } else {
            let result = try await Images.detached { () throws -> (CGImage, CGSize) in
                guard let src = CGImageSourceCreateWithURL(source as CFURL, nil),
                      let props = CGImageSourceCopyPropertiesAtIndex(src, 0, nil) as? [CFString: Any],
                      var w = props[kCGImagePropertyPixelWidth] as? Int, var h = props[kCGImagePropertyPixelHeight] as? Int else {
                    throw ToolError(L("Couldn't render"))
                }
                if let orientation = props[kCGImagePropertyOrientation] as? Int, orientation >= 5 { swap(&w, &h) }
                return (try Images.load(source, maxPixels: 1400), CGSize(width: w, height: h))
            }
            preview.image = result.0; sourceSize = result.1
        }
    }
    override func update() {
        let factor = factors.selectedSegment == 1 ? 4 : 2
        sizeLabel.stringValue = "\(Int(sourceSize.width) * factor) × \(Int(sourceSize.height) * factor) px"
    }
    override func reset() { factors.selectedSegment = 0 }
    override var canCancelSaving: Bool { true }
    override func cancelSaving() { task?.cancel() }
    override func didClose() { task?.cancel(); progressTask?.cancel() }
    override func apply() async throws -> [URL] {
        let factor = factors.selectedSegment == 1 ? 4 : 2, source = self.source, video = self.video
        let size = try Upscale.dimensions(width: Int(sourceSize.width), height: Int(sourceSize.height), factor: factor)
        _ = size
        let task = Task { [self] () throws -> [URL] in
            var box: SystemUpscalerBox
            if #available(macOS 26, *) {
                let engine = try SystemUpscaler(width: Int(sourceSize.width), height: Int(sourceSize.height), factor: factor, video: video)
                if engine.configuration.configurationModelStatus != .ready {
                    let alert = NSAlert()
                    alert.messageText = L("Download upscaling resources?")
                    alert.informativeText = L("Apple needs to download an upscaling model once. Your files stay on this Mac.")
                    alert.addButton(withTitle: L("Download")); alert.addButton(withTitle: L("Cancel"))
                    let answer = await withCheckedContinuation { continuation in alert.beginSheetModal(for: window) { continuation.resume(returning: $0) } }
                    guard answer == .alertFirstButtonReturn else { throw Cancelled() }
                    try Task.checkCancellation()
                    progressTask = Task { [weak self, engine] in
                        while !Task.isCancelled {
                            self?.progress.doubleValue = Double(engine.configuration.configurationModelPercentageAvailable)
                            try? await Task.sleep(for: .milliseconds(100))
                        }
                    }
                    try await engine.configuration.downloadConfigurationModel()
                    progressTask?.cancel(); progressTask = nil
                }
                try Task.checkCancellation()
                try engine.start()
                box = SystemUpscalerBox { image, time in try await engine.render(image, time: time) }
            } else {
                box = SystemUpscalerBox { image, _ in Upscale.lanczos(image, factor: Double(factor)) }
            }
            if video {
                return [try await Upscale.video(source, engine: box) { [weak self] value in Task { @MainActor in self?.progress.doubleValue = value } }]
            }
            let image = try await Images.detached { try Images.load(source) }
            let result = try await box.render(CIImage(cgImage: image), .zero)
            try Task.checkCancellation()
            return [try await Upscale.finishStill(result, source: source, factor: factor)]
        }
        self.task = task
        defer { self.task = nil; progressTask?.cancel(); progressTask = nil }
        return try await task.value
    }
}
