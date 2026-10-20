import AppKit
import AVFoundation
import AVKit

/// Trims a video or audio file between two points, with a player to preview the cut.
final class TrimEditor: Editor {
    private let player: AVPlayer
    private let playerView = AVPlayerView()
    private let duration: Double
    private let isAudio: Bool
    private var start: NSSlider!
    private var end: NSSlider!
    private let startLabel = NSTextField(labelWithString: L(""))
    private let endLabel = NSTextField(labelWithString: L(""))
    private let lengthLabel = NSTextField(labelWithString: L(""))
    private var lastStart = 0.0

    init(source: URL) async throws {
        let asset = AVURLAsset(url: source)
        duration = try await asset.load(.duration).seconds
        isAudio = try await asset.loadTracks(withMediaType: .video).isEmpty
        guard duration > 0 else { throw ToolError("This file has no duration") }
        player = AVPlayer(playerItem: AVPlayerItem(asset: asset))
        playerView.player = player
        playerView.controlsStyle = .inline
        super.init(title: isAudio ? "Trim Audio" : "Trim Video", source: source, canvas: playerView)

        start = slider(0, duration, 0)
        end = slider(0, duration, duration)
        for label in [startLabel, endLabel] {
            label.font = .monospacedDigitSystemFont(ofSize: 12, weight: .medium)
            label.widthAnchor.constraint(equalToConstant: 64).isActive = true
        }
        addRow("Start", start, startLabel)
        addRow("End", end, endLabel)
        lengthLabel.font = .systemFont(ofSize: 13, weight: .semibold)
        lengthLabel.textColor = .controlAccentColor
        addRow("Length", lengthLabel)
    }

    private func time(_ seconds: Double) -> String {
        let s = max(seconds, 0)
        return String(format: "%d:%02d.%d", Int(s) / 60, Int(s) % 60, Int((s * 10).truncatingRemainder(dividingBy: 10)))
    }

    override func update() {
        // Keep at least a tenth of a second between the handles, and preview whichever one moved.
        let startMoved = start.doubleValue != lastStart
        if end.doubleValue < start.doubleValue + 0.1 {
            if startMoved { start.doubleValue = max(0, end.doubleValue - 0.1) } else { end.doubleValue = min(duration, start.doubleValue + 0.1) }
        }
        lastStart = start.doubleValue
        startLabel.stringValue = time(start.doubleValue)
        endLabel.stringValue = time(end.doubleValue)
        lengthLabel.stringValue = time(end.doubleValue - start.doubleValue)
        let seekTo = startMoved ? start.doubleValue : end.doubleValue
        player.seek(to: CMTime(seconds: seekTo, preferredTimescale: 600), toleranceBefore: .zero, toleranceAfter: .zero)
    }

    override func reset() {
        start.doubleValue = 0
        end.doubleValue = duration
    }

    override func apply() async throws -> [URL] {
        player.pause()
        let range = CMTimeRange(start: CMTime(seconds: start.doubleValue, preferredTimescale: 600),
                                end: CMTime(seconds: end.doubleValue, preferredTimescale: 600))
        let asset = AVURLAsset(url: source)
        let ext = source.pathExtension.lowercased()
        let (preset, type, outExt): (String, AVFileType, String)
        if isAudio {
            (preset, type, outExt) = (AVAssetExportPresetAppleM4A, .m4a, "m4a")
        } else if ext == "mp4" || ext == "m4v" {
            (preset, type, outExt) = (AVAssetExportPresetPassthrough, ext == "mp4" ? .mp4 : .m4v, ext)
        } else {
            (preset, type, outExt) = (AVAssetExportPresetPassthrough, .mov, "mov")
        }
        guard let session = AVAssetExportSession(asset: asset, presetName: preset) else { throw ToolError("Can't trim this file") }
        session.timeRange = range
        let out = Paths.output(for: source, ext: outExt, suffix: " trimmed")
        try await session.export(to: out, as: type)
        return [out]
    }
}

extension Video {
    /// Joins videos (or audio files) end to end in the order they were selected.
    static func merge(_ urls: [URL], audioOnly: Bool) async throws -> URL {
        let composition = AVMutableComposition()
        let videoTrack = audioOnly ? nil : composition.addMutableTrack(withMediaType: .video, preferredTrackID: kCMPersistentTrackID_Invalid)
        var audioTrack: AVMutableCompositionTrack?  // created on first use; an empty track makes export fail
        var cursor = CMTime.zero
        for url in urls {
            let asset = AVURLAsset(url: url)
            let duration = try await asset.load(.duration)
            let range = CMTimeRange(start: .zero, duration: duration)
            if let videoTrack, let source = try await asset.loadTracks(withMediaType: .video).first {
                try videoTrack.insertTimeRange(range, of: source, at: cursor)
                if cursor == .zero { videoTrack.preferredTransform = try await source.load(.preferredTransform) }
            }
            if let source = try await asset.loadTracks(withMediaType: .audio).first {
                if audioTrack == nil {
                    audioTrack = composition.addMutableTrack(withMediaType: .audio, preferredTrackID: kCMPersistentTrackID_Invalid)
                }
                try audioTrack?.insertTimeRange(range, of: source, at: cursor)
            }
            cursor = cursor + duration
        }
        let name = audioOnly ? "Merged.m4a" : "Merged.mp4"
        let out = Paths.unique(urls[0].deletingLastPathComponent().appendingPathComponent(name))
        let preset = audioOnly ? AVAssetExportPresetAppleM4A : AVAssetExportPresetHighestQuality
        guard let session = AVAssetExportSession(asset: composition, presetName: preset) else { throw ToolError("Can't merge these files") }
        try await session.export(to: out, as: audioOnly ? .m4a : .mp4)
        return out
    }
}
