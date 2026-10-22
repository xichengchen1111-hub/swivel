import AppKit
import AVFoundation
import AVKit

/// Trims a video or audio file between two points, with a player to preview the cut.
///
/// The handles stop with a tap at the clip's ends (reaching exactly 0 and the full length) and where they meet;
/// scrubbing shows the frame under the handle (a nearby one while dragging, the exact one on release), and playback
/// stays inside the cut.
final class TrimEditor: Editor {
    private let asset: AVURLAsset
    private let player: AVPlayer
    private let playerView = AVPlayerView()
    /// Known once `load()` has read the asset.
    private(set) var duration = 0.0
    private(set) var isAudio: Bool
    private(set) var start: DetentSlider!
    private(set) var end: DetentSlider!
    private let startLabel = NSTextField(labelWithString: L(""))
    private let endLabel = NSTextField(labelWithString: L(""))
    private let lengthLabel = NSTextField(labelWithString: L(""))
    private var lastStart = 0.0
    private var lastEnd = 0.0
    /// The handles are pushed together at the minimum gap.
    private var pinned = false
    // Seeking chases the newest target with one seek in flight at a time (Apple QA1820), so scrubbing never queues up.
    private var seekTarget = 0.0
    private var seekExact = true
    private var seeking = false
    private var rateObservation: NSKeyValueObservation?

    /// Builds the window at once; the asset's length and tracks load in `load()`, after `present()` has counted the
    /// window, so the menu that opened it doesn't put up a busy toast meanwhile. (Still `async throws`, as callers
    /// await it.)
    init(source: URL) async throws {
        asset = Video.openAsset(source)
        isAudio = FileKind.of(source) == .audio
        player = AVPlayer(playerItem: AVPlayerItem(asset: asset))
        playerView.player = player
        playerView.controlsStyle = .inline
        super.init(title: isAudio ? "Trim Audio" : "Trim Video", source: source, canvas: playerView)

        // Each handle's default is its own end of the clip: an end stop that taps, never a band that swallows the
        // first or last seconds. The real range arrives with the duration.
        start = slider(0, 1, 0)
        end = slider(0, 1, 1)
        for label in [startLabel, endLabel] {
            label.font = .monospacedDigitSystemFont(ofSize: 12, weight: .medium)
            label.widthAnchor.constraint(equalToConstant: 64).isActive = true
        }
        addRow("Start", start, startLabel)
        addRow("End", end, endLabel)
        lengthLabel.font = .systemFont(ofSize: 13, weight: .semibold)
        lengthLabel.textColor = .controlAccentColor
        addRow("Length", lengthLabel)

        rateObservation = player.observe(\.rate, options: [.new]) { [weak self] _, _ in
            Task { @MainActor in self?.playbackChanged() }
        }
    }

    override func load() async throws {
        let seconds = try await asset.load(.duration).seconds
        let audio = try await asset.loadTracks(withMediaType: .video).isEmpty
        guard seconds > 0 else { throw ToolError("This file has no duration") }  // also NaN (indefinite)
        duration = seconds
        if audio != isAudio {
            isAudio = audio
            window.title = L(audio ? "Trim Audio" : "Trim Video")
        }
        for handle in [start!, end!] { handle.maxValue = seconds }
        start.doubleValue = 0
        end.doubleValue = seconds
        end.neutral = seconds
        lastStart = 0
        lastEnd = seconds
    }

    private func time(_ seconds: Double) -> String {
        let s = max(seconds, 0)
        return String(format: "%d:%02d.%d", Int(s) / 60, Int(s) % 60, Int((s * 10).truncatingRemainder(dividingBy: 10)))
    }

    override func update() {
        // Keep at least a tenth of a second between the handles, and preview whichever one moved.
        let startMoved = start.doubleValue != lastStart, endMoved = end.doubleValue != lastEnd
        var meeting = false
        if end.doubleValue < start.doubleValue + 0.1 {
            if startMoved { start.doubleValue = max(0, end.doubleValue - 0.1) } else { end.doubleValue = min(duration, start.doubleValue + 0.1) }
            meeting = true
        }
        // The handles meeting is a hard stop: one tap as they touch, none while held there.
        if meeting, !pinned, isTracking { Haptics.tick(.alignment) }
        pinned = meeting
        lastStart = start.doubleValue
        lastEnd = end.doubleValue
        startLabel.stringValue = time(start.doubleValue)
        endLabel.stringValue = time(end.doubleValue)
        lengthLabel.stringValue = time(end.doubleValue - start.doubleValue)
        player.currentItem?.reversePlaybackEndTime = CMTime(seconds: start.doubleValue, preferredTimescale: 600)
        player.currentItem?.forwardPlaybackEndTime = CMTime(seconds: end.doubleValue, preferredTimescale: 600)
        // Opening (or Reset) shows the first frame of the cut, not its last.
        seek(to: endMoved && !startMoved ? end.doubleValue : start.doubleValue, exact: !isTracking)
    }

    override func trackingEnded() { seek(to: seekTarget, exact: true) }

    private func seek(to seconds: Double, exact: Bool) {
        seekTarget = seconds
        seekExact = exact
        if !seeking { chase() }
    }

    private func chase() {
        seeking = true
        let target = seekTarget, exact = seekExact
        // While dragging any nearby frame will do (quick to decode); the frame left on screen is exact.
        let slack = exact ? CMTime.zero : CMTime(seconds: min(duration / 200, 0.25), preferredTimescale: 600)
        player.seek(to: CMTime(seconds: target, preferredTimescale: 600), toleranceBefore: slack, toleranceAfter: slack) { [weak self] _ in
            Task { @MainActor in
                guard let self else { return }
                self.seeking = false
                if self.seekTarget != target || (self.seekExact && !exact) { self.chase() }
            }
        }
    }

    /// Play starts from the beginning of the cut when the playhead is outside it (or sitting at its end).
    private func playbackChanged() {
        guard player.rate > 0, isLoaded else { return }
        let now = player.currentTime().seconds
        if now < start.doubleValue - 0.05 || now >= end.doubleValue - 0.05 {
            player.seek(to: CMTime(seconds: start.doubleValue, preferredTimescale: 600), toleranceBefore: .zero, toleranceAfter: .zero)
        }
    }

    override func reset() {
        start.doubleValue = 0
        end.doubleValue = duration
    }

    override func didClose() {
        rateObservation?.invalidate()
        rateObservation = nil
        player.pause()
    }

    override func apply() async throws -> [URL] {
        player.pause()
        let range = CMTimeRange(start: CMTime(seconds: start.doubleValue, preferredTimescale: 600),
                                end: CMTime(seconds: end.doubleValue, preferredTimescale: 600))
        let asset = try await Video.safeAsset(source)
        let ext = source.pathExtension.lowercased()
        let (preset, type, outExt): (String, AVFileType, String)
        if isAudio {
            (preset, type, outExt) = (AVAssetExportPresetAppleM4A, .m4a, "m4a")
        } else {
            // Re-encoded rather than passed through: a passthrough cut starts at the previous keyframe and only hides
            // the frames before the start point behind an edit list, so "trimmed" footage would still be in the file.
            (preset, type, outExt) = (AVAssetExportPresetHighestQuality, ext == "mp4" ? .mp4 : ext == "m4v" ? .m4v : .mov,
                                      ext == "mp4" || ext == "m4v" ? ext : "mov")
        }
        guard let session = AVAssetExportSession(asset: asset, presetName: preset) else { throw ToolError("Can't trim this file") }
        session.timeRange = range
        // A video composition makes the export decode and re-encode even when the codec and size already match.
        if !isAudio { session.videoComposition = try await AVVideoComposition.videoComposition(withPropertiesOf: asset) }
        // The export goes to a temporary that is published under a free name once complete.
        return [try await Paths.produce(Paths.preferred(for: source, ext: outExt, suffix: " trimmed")) {
            try await session.export(to: $0, as: type)
        }]
    }
}

extension Video {
    /// Media that refers to other files (QuickTime reference movies, external data references) is never followed:
    /// converting a crafted movie could otherwise copy another local file's audio or video into the result.
    static func openAsset(_ url: URL) -> AVURLAsset {
        AVURLAsset(url: url, options: [AVURLAssetReferenceRestrictionsKey: AVAssetReferenceRestrictions.forbidAll.rawValue])
    }

    /// `openAsset`, plus a check that every track's media is inside the file itself.
    static func safeAsset(_ url: URL) async throws -> AVURLAsset {
        let asset = openAsset(url)
        for track in try await asset.load(.tracks) where try await !track.load(.isSelfContained) {
            throw ToolError(L("This movie uses media from other files. Only self-contained files can be converted."))
        }
        return asset
    }

    /// Joins videos (or audio files) end to end in the order they were selected.
    static func merge(_ urls: [URL], audioOnly: Bool) async throws -> URL {
        let composition = AVMutableComposition()
        let videoTrack = audioOnly ? nil : composition.addMutableTrack(withMediaType: .video, preferredTrackID: kCMPersistentTrackID_Invalid)
        var audioTrack: AVMutableCompositionTrack?  // created on first use; an empty track makes export fail
        var cursor = CMTime.zero
        for url in urls {
            let asset = try await safeAsset(url)
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
        let preset = audioOnly ? AVAssetExportPresetAppleM4A : AVAssetExportPresetHighestQuality
        guard let session = AVAssetExportSession(asset: composition, presetName: preset) else { throw ToolError("Can't merge these files") }
        return try await Paths.produce(urls[0].deletingLastPathComponent().appendingPathComponent(name)) {
            try await session.export(to: $0, as: audioOnly ? .m4a : .mp4)
        }
    }
}
