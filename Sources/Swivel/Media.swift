import AppKit
import VideoToolbox
import AVFoundation
import ImageIO
import UniformTypeIdentifiers

enum Video {
    static func convertItems(sourceExt: Set<String>) -> [FileOption] {
        var list: [FileOption] = []
        if !sourceExt.contains("mp4") {
            list.append(FileOption(title: "MP4") { try await export($0, AVAssetExportPresetHighestQuality, .mp4, "mp4") })
        }
        if !sourceExt.contains("mov") {
            list.append(FileOption(title: "MOV") { try await export($0, AVAssetExportPresetHighestQuality, .mov, "mov") })
        }
        list += [
            FileOption(title: "HEVC") { try await export($0, AVAssetExportPresetHEVCHighestQuality, .mp4, "mp4", suffix: " hevc") },
            FileOption(title: "M4V") { try await export($0, AVAssetExportPresetHighestQuality, .m4v, "m4v") },
            FileOption(title: "GIF") { try await gif($0) },
            FileOption(title: "M4A") { try await export($0, AVAssetExportPresetAppleM4A, .m4a, "m4a") },
            FileOption(title: "WAV") { url in
                let m4a = try await export(url, AVAssetExportPresetAppleM4A, .m4a, "m4a")[0]
                defer { try? FileManager.default.removeItem(at: m4a) }
                let out = Paths.output(for: url, ext: "wav")
                try await Shell.run("/usr/bin/afconvert", ["-f", "WAVE", "-d", "LEI16", m4a.path, out.path])
                return [out]
            },
            FileOption(title: "PNG Frame") { try await frame($0) },
        ]
        return list
    }

    static let toolItems: [FileOption] = [
        FileOption(title: "Compress") { try await compress([$0]).files },
        FileOption(title: "1080p") { try await export($0, AVAssetExportPreset1920x1080, .mp4, "mp4", suffix: " 1080p") },
        FileOption(title: "720p") { try await export($0, AVAssetExportPreset1280x720, .mp4, "mp4", suffix: " 720p") },
        FileOption(title: "480p") { try await export($0, AVAssetExportPreset640x480, .mp4, "mp4", suffix: " 480p") },
        FileOption(title: "Mute") { try await mute($0) },
        FileOption(title: "Extract Audio") { try await export($0, AVAssetExportPresetAppleM4A, .m4a, "m4a") },
        FileOption(title: "Frame") { try await frame($0) },
        FileOption(title: "GIF") { try await gif($0) },
    ]

    @MainActor
    static func compress(_ urls: [URL]) async throws -> ActionResult {
        let alert = NSAlert()
        alert.messageText = L("Compress Video")
        alert.informativeText = L("Choose a target file size.")
        let popup = NSPopUpButton(frame: NSRect(x: 0, y: 0, width: 240, height: 26))
        popup.addItems(withTitles: [L("Smaller file"), L("8 MB"), L("10 MB"), L("25 MB"), L("50 MB"), L("100 MB")])
        popup.selectItem(at: Prefs.videoCompressTargets.firstIndex(of: Prefs.videoCompressTarget) ?? 0)
        alert.accessoryView = popup
        alert.addButton(withTitle: L("Compress"))
        alert.addButton(withTitle: L("Cancel"))
        // Dropping from Finder leaves Finder in front; the question must come forward to be seen.
        NSApp.activate()
        guard alert.runModal() == .alertFirstButtonReturn else { throw Cancelled() }
        let target = Prefs.videoCompressTargets[popup.indexOfSelectedItem]
        Prefs.videoCompressTarget = target
        var files: [URL] = [], messages: [String] = []
        for url in urls {
            let out: URL
            if target == 0 { out = try await compressSmaller(url) }
            else { out = try await compressToTarget(url, maxBytes: target) }
            files.append(out)
            let size = try out.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? 0
            messages.append(target > 0 && size > target
                ? L("Saved %@ (%@), but the target size was exceeded.", out.lastPathComponent, byteString(size))
                : L("Saved %@ (%@)", out.lastPathComponent, byteString(size)))
        }
        return ActionResult(message: messages.joined(separator: "\n"), files: files)
    }

    private static func compressSmaller(_ url: URL) async throws -> URL {
        let temporary = url.deletingLastPathComponent().appendingPathComponent(".swivel-\(UUID().uuidString).mp4")
        TemporaryItems.register(temporary)
        defer { try? FileManager.default.removeItem(at: temporary); TemporaryItems.unregister(temporary) }
        guard let session = AVAssetExportSession(asset: try await safeAsset(url), presetName: AVAssetExportPresetMediumQuality) else {
            throw ToolError(L("Couldn't encode video."))
        }
        try await withTaskCancellationHandler {
            try Task.checkCancellation()
            try await session.export(to: temporary, as: .mp4)
            try Task.checkCancellation()
        } onCancel: { session.cancelExport() }
        let out = Paths.output(for: url, ext: "mp4", suffix: " compressed")
        try FileManager.default.moveItem(at: temporary, to: out)
        return out
    }

    static func targetBitrates(maxBytes: Int, duration: Double, hasAudio: Bool) throws -> (video: Int, audio: Int) {
        guard duration.isFinite, duration > 0, maxBytes > 0 else {
            throw ToolError(L("Invalid video duration or target size."))
        }
        let total = Double(maxBytes) * 8 / duration * 0.95
        let audio = hasAudio ? (total < 1_000_000 ? 64_000 : 128_000) : 0
        guard total - Double(audio) >= 150_000 else {
            throw ToolError(L("Target size is too small for this video's duration."))
        }
        guard total < Double(Int.max) else { throw ToolError(L("Invalid video duration or target size.")) }
        return (Int(total) - audio, audio)
    }

    /// Encodes beside the source and publishes only a completed file. A second pass compensates for overshoot.
    static func compressToTarget(_ url: URL, maxBytes: Int) async throws -> URL {
        let asset = try await safeAsset(url)
        let duration = try await asset.load(.duration).seconds
        let audio = try await asset.loadTracks(withMediaType: .audio).first
        let budget = try targetBitrates(maxBytes: maxBytes, duration: duration, hasAudio: audio != nil)
        var totalRate = Double(budget.video + budget.audio)
        let audioRate = budget.audio
        let temporary = url.deletingLastPathComponent().appendingPathComponent(".swivel-\(UUID().uuidString).mp4")
        TemporaryItems.register(temporary)
        defer { try? FileManager.default.removeItem(at: temporary); TemporaryItems.unregister(temporary) }
        for attempt in 0..<2 {
            try Task.checkCancellation()
            let videoRate = totalRate - Double(audioRate)
            guard videoRate >= 150_000 else { throw ToolError(L("Target size is too small for this video's duration.")) }
            try await encodeTarget(asset, audio: audio, to: temporary, videoRate: Int(videoRate), audioRate: audioRate)
            try Task.checkCancellation()
            let size = try temporary.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? 0
            if size <= maxBytes || attempt == 1 { break }
            // Retry once, retaining the minimum supported video bitrate even when container overhead dominates.
            totalRate = max(Double(audioRate + 150_000), totalRate * Double(maxBytes) / Double(size) * 0.95)
            try FileManager.default.removeItem(at: temporary)
        }
        try Task.checkCancellation()
        let out = Paths.output(for: url, ext: "mp4", suffix: " compressed")
        try FileManager.default.moveItem(at: temporary, to: out)
        return out
    }

    private static func encodeTarget(_ asset: AVAsset, audio: AVAssetTrack?, to out: URL,
                                     videoRate: Int, audioRate: Int) async throws {
        guard let video = try await asset.loadTracks(withMediaType: .video).first else { throw ToolError(L("No video track")) }
        let size = try await video.load(.naturalSize)
        let transform = try await video.load(.preferredTransform)
        let bounds = CGRect(origin: .zero, size: size).applying(transform)
        let cap = videoRate < 500_000 ? 640 : videoRate < 1_000_000 ? 854 : videoRate < 2_500_000 ? 1280 : 1920
        let scale = min(1, Double(cap) / max(bounds.width, bounds.height))
        let width = max(2, Int(size.width * scale) / 2 * 2)
        let height = max(2, Int(size.height * scale) / 2 * 2)
        let composition = AVMutableVideoComposition()
        composition.renderSize = CGSize(width: width, height: height)
        let fps = try await video.load(.nominalFrameRate)
        composition.frameDuration = CMTime(value: 1, timescale: CMTimeScale(max(1, min(60, fps.rounded()))))
        let instruction = AVMutableVideoCompositionInstruction()
        instruction.timeRange = CMTimeRange(start: .zero, duration: try await asset.load(.duration))
        let layer = AVMutableVideoCompositionLayerInstruction(assetTrack: video)
        layer.setTransform(CGAffineTransform(scaleX: Double(width) / size.width,
                                             y: Double(height) / size.height), at: .zero)
        instruction.layerInstructions = [layer]
        composition.instructions = [instruction]
        let reader = try AVAssetReader(asset: asset)
        let writer = try AVAssetWriter(outputURL: out, fileType: .mp4)
        let videoOutput = AVAssetReaderVideoCompositionOutput(videoTracks: [video], videoSettings: [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange])
        videoOutput.videoComposition = composition
        let videoInput = AVAssetWriterInput(mediaType: .video, outputSettings: [
            AVVideoCodecKey: AVVideoCodecType.h264, AVVideoWidthKey: width, AVVideoHeightKey: height,
            AVVideoCompressionPropertiesKey: [AVVideoAverageBitRateKey: videoRate,
                kVTCompressionPropertyKey_DataRateLimits as String: [Double(videoRate) / 8, 1]]])
        // Keep the track's orientation/mirroring rather than baking it into the pixels.
        videoInput.transform = CGAffineTransform(a: transform.a, b: transform.b, c: transform.c, d: transform.d,
                                                tx: transform.tx * scale, ty: transform.ty * scale)
        var streams: [(AVAssetReaderOutput, AVAssetWriterInput)] = [(videoOutput, videoInput)]
        if let audio {
            let descriptions = try await audio.load(.formatDescriptions)
            let channels = descriptions.first.flatMap { CMAudioFormatDescriptionGetStreamBasicDescription($0)?.pointee.mChannelsPerFrame } ?? 2
            let output = AVAssetReaderTrackOutput(track: audio, outputSettings: [AVFormatIDKey: kAudioFormatLinearPCM])
            let input = AVAssetWriterInput(mediaType: .audio, outputSettings: [AVFormatIDKey: kAudioFormatMPEG4AAC,
                AVSampleRateKey: 44100, AVNumberOfChannelsKey: min(2, max(1, Int(channels))), AVEncoderBitRateKey: audioRate])
            streams.append((output, input))
        }
        for (output, input) in streams {
            guard reader.canAdd(output), writer.canAdd(input) else { throw ToolError(L("Couldn't encode video.")) }
            reader.add(output); writer.add(input)
        }
        try await withTaskCancellationHandler {
            do {
                try Task.checkCancellation()
                guard writer.startWriting(), reader.startReading() else { throw reader.error ?? writer.error ?? ToolError(L("Couldn't encode video.")) }
                writer.startSession(atSourceTime: .zero)
                try await withThrowingTaskGroup(of: Void.self) { group in
                    for (output, input) in streams {
                        group.addTask {
                            while true {
                                try Task.checkCancellation()
                                guard writer.status == .writing else { throw writer.error ?? ToolError(L("Couldn't encode video.")) }
                                if !input.isReadyForMoreMediaData {
                                    try await Task.sleep(for: .milliseconds(2))
                                    continue
                                }
                                let appended: Bool? = autoreleasepool {
                                    guard let sample = output.copyNextSampleBuffer() else { return nil }
                                    return input.append(sample)
                                }
                                guard let appended else {
                                    if reader.status == .failed || reader.status == .cancelled {
                                        throw reader.error ?? CancellationError()
                                    }
                                    input.markAsFinished()
                                    return
                                }
                                if !appended { throw writer.error ?? ToolError(L("Couldn't encode video.")) }
                            }
                        }
                    }
                    for try await _ in group { }
                }
                await writer.finishWriting()
                try Task.checkCancellation()
                guard writer.status == .completed else { throw writer.error ?? ToolError(L("Couldn't encode video.")) }
            } catch {
                if reader.status == .reading { reader.cancelReading() }
                if writer.status == .writing { writer.cancelWriting() }
                throw error
            }
        } onCancel: {
            if reader.status == .reading { reader.cancelReading() }
            if writer.status == .writing { writer.cancelWriting() }
        }
    }

    static func export(_ url: URL, _ preset: String, _ type: AVFileType, _ ext: String,
                       suffix: String = "") async throws -> [URL] {
        try await export(try await safeAsset(url), preset, type, to: Paths.output(for: url, ext: ext, suffix: suffix))
    }

    private static func export(_ asset: AVAsset, _ preset: String, _ type: AVFileType, to out: URL) async throws -> [URL] {
        guard let session = AVAssetExportSession(asset: asset, presetName: preset) else {
            throw ToolError("This video can't be exported that way")
        }
        try await session.export(to: out, as: type)
        return [out]
    }

    private static func mute(_ url: URL) async throws -> [URL] {
        let asset = try await safeAsset(url)
        guard let track = try await asset.loadTracks(withMediaType: .video).first else { throw ToolError("No video track") }
        let duration = try await asset.load(.duration)
        let composition = AVMutableComposition()
        let videoTrack = composition.addMutableTrack(withMediaType: .video, preferredTrackID: kCMPersistentTrackID_Invalid)
        try videoTrack?.insertTimeRange(CMTimeRange(start: .zero, duration: duration), of: track, at: .zero)
        videoTrack?.preferredTransform = try await track.load(.preferredTransform)
        return try await export(composition, AVAssetExportPresetHighestQuality, .mov,
                                to: Paths.output(for: url, ext: "mov", suffix: " muted"))
    }

    private static func generator(_ asset: AVAsset, maxSize: CGFloat? = nil) -> AVAssetImageGenerator {
        let generator = AVAssetImageGenerator(asset: asset)
        generator.appliesPreferredTrackTransform = true
        if let maxSize { generator.maximumSize = CGSize(width: maxSize, height: maxSize) }
        return generator
    }

    private static func frame(_ url: URL) async throws -> [URL] {
        let asset = try await safeAsset(url)
        let duration = try await asset.load(.duration).seconds
        let (image, _) = try await generator(asset).image(at: CMTime(seconds: duration * 0.1, preferredTimescale: 600))
        let out = Paths.output(for: url, ext: "png", suffix: " frame")
        try Images.write(image, to: out, type: .png, quality: 1)
        return [out]
    }

    private static func gif(_ url: URL) async throws -> [URL] {
        let asset = try await safeAsset(url)
        let duration = min(try await asset.load(.duration).seconds, 30)
        let fps = 12.0
        let count = max(Int(duration * fps), 1)
        let gen = generator(asset, maxSize: 540)
        gen.requestedTimeToleranceBefore = CMTime(seconds: 0.5 / fps, preferredTimescale: 600)
        gen.requestedTimeToleranceAfter = gen.requestedTimeToleranceBefore

        let out = Paths.output(for: url, ext: "gif")
        guard let dest = CGImageDestinationCreateWithURL(out as CFURL, UTType.gif.identifier as CFString, count, nil) else {
            throw ToolError("Couldn't create GIF")
        }
        CGImageDestinationSetProperties(dest, [kCGImagePropertyGIFDictionary: [kCGImagePropertyGIFLoopCount: 0]] as CFDictionary)
        let frameProps = [kCGImagePropertyGIFDictionary: [kCGImagePropertyGIFDelayTime: 1 / fps]] as CFDictionary
        for i in 0..<count {
            let (image, _) = try await gen.image(at: CMTime(seconds: Double(i) / fps, preferredTimescale: 600))
            CGImageDestinationAddImage(dest, image, frameProps)
        }
        guard CGImageDestinationFinalize(dest) else { throw ToolError("Couldn't save GIF") }
        return [out]
    }
}

struct AudioFormat {
    let name: String
    let ext: String
    let args: [String]
}

enum Audio {
    static let formats: [AudioFormat] = [
        AudioFormat(name: "M4A", ext: "m4a", args: ["-f", "m4af", "-d", "aac", "-s", "3", "-u", "vbrq", "110"]),
        AudioFormat(name: "WAV", ext: "wav", args: ["-f", "WAVE", "-d", "LEI16"]),
        AudioFormat(name: "AIFF", ext: "aiff", args: ["-f", "AIFF", "-d", "BEI16"]),
        AudioFormat(name: "FLAC", ext: "flac", args: ["-f", "flac", "-d", "flac"]),
        AudioFormat(name: "ALAC", ext: "m4a", args: ["-f", "m4af", "-d", "alac"]),
        AudioFormat(name: "CAF", ext: "caf", args: ["-f", "caff", "-d", "LEI16"]),
        AudioFormat(name: "AAC", ext: "aac", args: ["-f", "adts", "-d", "aac", "-s", "3", "-u", "vbrq", "110"]),
    ]

    static let toolItems: [FileOption] = [
        FileOption(title: "Compress") { url in
            try await run(url, ext: "m4a", suffix: " compressed", ["-f", "m4af", "-d", "aac", "-s", "3", "-u", "vbrq", "40"])
        },
        FileOption(title: "Mono") { url in
            try await run(url, ext: "m4a", suffix: " mono", ["-f", "m4af", "-d", "aac", "-c", "1", "-s", "3", "-u", "vbrq", "90"])
        },
        FileOption(title: "Voice Memo") { url in
            try await run(url, ext: "m4a", suffix: " voice", ["-f", "m4af", "-d", "aac@22050", "-b", "48000", "-c", "1"])
        },
    ]

    static func convert(_ url: URL, to format: AudioFormat) async throws -> [URL] {
        let suffix = format.name == "ALAC" ? " lossless" : ""
        return try await run(url, ext: format.ext, suffix: suffix, format.args)
    }

    private static func run(_ url: URL, ext: String, suffix: String, _ args: [String]) async throws -> [URL] {
        let out = Paths.output(for: url, ext: ext, suffix: suffix)
        try await Shell.run("/usr/bin/afconvert", args + [url.path, out.path])
        return [out]
    }
}
