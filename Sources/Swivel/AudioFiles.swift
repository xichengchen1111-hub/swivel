import AVFoundation
import os

/// File-only PCM decoding shared by media effects. No input device, permissions, or network.
enum AudioFiles {
    static func decode(_ source: URL) async throws -> URL {
        let asset = try await Video.safeAsset(source)
        guard let track = try await asset.loadTracks(withMediaType: .audio).first,
              let description = try await track.load(.formatDescriptions).first,
              let stream = CMAudioFormatDescriptionGetStreamBasicDescription(description)?.pointee else {
            throw ToolError(L("This file has no audio."))
        }
        let rate = stream.mSampleRate, channels = Int(stream.mChannelsPerFrame)
        let duration = try await asset.load(.duration).seconds
        guard rate.isFinite, rate >= 8000, rate <= 384000, (1...16).contains(channels),
              duration.isFinite, duration > 0, duration <= 86400,
              let format = AVAudioFormat(standardFormatWithSampleRate: rate, channels: AVAudioChannelCount(channels)) else {
            throw ToolError(L("This audio format or duration is not supported."))
        }
        let reader = try AVAssetReader(asset: asset)
        let output = AVAssetReaderTrackOutput(track: track, outputSettings: [AVFormatIDKey: kAudioFormatLinearPCM,
            AVSampleRateKey: rate, AVNumberOfChannelsKey: channels, AVLinearPCMBitDepthKey: 32,
            AVLinearPCMIsFloatKey: true, AVLinearPCMIsNonInterleaved: false, AVLinearPCMIsBigEndianKey: false])
        guard reader.canAdd(output) else { throw ToolError(L("Couldn't encode audio.")) }
        reader.add(output)
        let gate = OSAllocatedUnfairLock(initialState: (started: false, cancelled: false))
        let preferred = Paths.temporary(beside: source, ext: "caf")
        do {
            return try await Paths.produce(preferred) { target in
                let worker = Task.detached(priority: .userInitiated) {
                    let started = try gate.withLockUnchecked { state -> Bool in
                        guard !state.cancelled else { return false }
                        guard reader.startReading() else { throw reader.error ?? ToolError(L("Couldn't encode audio.")) }
                        state.started = true
                        return true
                    }
                    guard started else { throw CancellationError() }
                    let file = try AVAudioFile(forWriting: target, settings: format.settings.merging([AVLinearPCMIsNonInterleaved: false]) { _, new in new })
                    var written = 0
                    while true {
                        try Task.checkCancellation()
                        guard let sample = output.copyNextSampleBuffer() else { break }
                        let frames = CMSampleBufferGetNumSamples(sample)
                        guard frames > 0, frames <= 4_000_000, let block = CMSampleBufferGetDataBuffer(sample) else { continue }
                        let seconds = CMSampleBufferGetPresentationTimeStamp(sample).seconds
                        guard seconds.isFinite, seconds >= -1, seconds <= duration + 1 else { throw ToolError(L("Invalid audio timing.")) }
                        let expected = max(0, Int((seconds * rate).rounded()))
                        while written < expected {
                            try Task.checkCancellation()
                            let count = min(4096, expected - written)
                            guard let silence = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(count)) else { throw ToolError(L("Couldn't encode audio.")) }
                            silence.frameLength = AVAudioFrameCount(count)
                            for c in 0..<channels { silence.floatChannelData![c].initialize(repeating: 0, count: count) }
                            try file.write(from: silence); written += count
                        }
                        var interleaved = [Float](repeating: 0, count: frames * channels)
                        let code = interleaved.withUnsafeMutableBytes {
                            CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: $0.count, destination: $0.baseAddress!)
                        }
                        guard code == noErr, let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(frames)) else {
                            throw ToolError(L("Couldn't encode audio."))
                        }
                        buffer.frameLength = AVAudioFrameCount(frames)
                        for c in 0..<channels { for i in 0..<frames { buffer.floatChannelData![c][i] = interleaved[i * channels + c] } }
                        try file.write(from: buffer); written += frames
                    }
                    guard reader.status == .completed else { throw reader.error ?? CancellationError() }
                    try Task.checkCancellation()
                }
                try await withTaskCancellationHandler { try await worker.value } onCancel: {
                    worker.cancel()
                    gate.withLockUnchecked { state in
                        state.cancelled = true
                        if state.started, reader.status == .reading { reader.cancelReading() }
                    }
                }
            }
        } catch { Paths.discard(preferred); throw error }
    }
}
