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
        FileOption(title: "Compress") { try await export($0, AVAssetExportPresetMediumQuality, .mp4, "mp4", suffix: " compressed") },
        FileOption(title: "1080p") { try await export($0, AVAssetExportPreset1920x1080, .mp4, "mp4", suffix: " 1080p") },
        FileOption(title: "720p") { try await export($0, AVAssetExportPreset1280x720, .mp4, "mp4", suffix: " 720p") },
        FileOption(title: "480p") { try await export($0, AVAssetExportPreset640x480, .mp4, "mp4", suffix: " 480p") },
        FileOption(title: "Mute") { try await mute($0) },
        FileOption(title: "Extract Audio") { try await export($0, AVAssetExportPresetAppleM4A, .m4a, "m4a") },
        FileOption(title: "Frame") { try await frame($0) },
        FileOption(title: "GIF") { try await gif($0) },
    ]

    static func export(_ url: URL, _ preset: String, _ type: AVFileType, _ ext: String,
                       suffix: String = "") async throws -> [URL] {
        try await export(AVURLAsset(url: url), preset, type, to: Paths.output(for: url, ext: ext, suffix: suffix))
    }

    private static func export(_ asset: AVAsset, _ preset: String, _ type: AVFileType, to out: URL) async throws -> [URL] {
        guard let session = AVAssetExportSession(asset: asset, presetName: preset) else {
            throw ToolError("This video can't be exported that way")
        }
        try await session.export(to: out, as: type)
        return [out]
    }

    private static func mute(_ url: URL) async throws -> [URL] {
        let asset = AVURLAsset(url: url)
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
        let asset = AVURLAsset(url: url)
        let duration = try await asset.load(.duration).seconds
        let (image, _) = try await generator(asset).image(at: CMTime(seconds: duration * 0.1, preferredTimescale: 600))
        let out = Paths.output(for: url, ext: "png", suffix: " frame")
        try Images.write(image, to: out, type: .png, quality: 1)
        return [out]
    }

    private static func gif(_ url: URL) async throws -> [URL] {
        let asset = AVURLAsset(url: url)
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
