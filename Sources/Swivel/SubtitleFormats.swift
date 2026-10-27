import Foundation

/// Shared by the text-based converters (subtitles, data): bounded reads and honest encoding detection.
enum TextFile {
    /// UTF-8 (with or without BOM) and UTF-16 with a BOM are read directly. Anything else goes to the system's
    /// detector, limited to the common legacy encodings (left free, it picks Baltic code pages for Korean), and is
    /// refused if it can only be read lossily.
    static func read(_ url: URL, limit: Int, normalizeNewlines: Bool = true) throws -> String {
        let data = try FileSafety.read(url, limit: limit)
        return try decode(data, normalizeNewlines: normalizeNewlines)
    }

    private static let legacyEncodings: [NSNumber] = {
        let cf: [CFStringEncodings] = [.GB_18030_2000, .big5_HKSCS_1999, .EUC_KR]
        return (cf.map { CFStringConvertEncodingToNSStringEncoding(CFStringEncoding($0.rawValue)) }
                + [String.Encoding.shiftJIS.rawValue, String.Encoding.windowsCP1252.rawValue, String.Encoding.windowsCP1251.rawValue])
            .map { NSNumber(value: $0) }
    }()

    /// CSV keeps carriage returns: a CR LF inside a quoted cell is part of the cell.
    static func decode(_ data: Data, normalizeNewlines: Bool = true) throws -> String {
        var text: String?
        if data.starts(with: [0xEF, 0xBB, 0xBF]) { text = String(data: data.dropFirst(3), encoding: .utf8) }
        else if data.starts(with: [0xFF, 0xFE]) || data.starts(with: [0xFE, 0xFF]) { text = String(data: data, encoding: .utf16) }
        else if let utf8 = String(data: data, encoding: .utf8) { text = utf8 }
        else {
            var converted: NSString?, lossy: ObjCBool = false
            let options: [StringEncodingDetectionOptionsKey: Any] = [.allowLossyKey: false, .suggestedEncodingsKey: legacyEncodings,
                                                                     .useOnlySuggestedEncodingsKey: true]
            let found = NSString.stringEncoding(for: data, encodingOptions: options, convertedString: &converted, usedLossyConversion: &lossy)
            if found != 0, !lossy.boolValue { text = converted as String? }
        }
        guard var result = text, !result.contains("\0") else { throw ToolError(L("Couldn't work out this file's text encoding.")) }
        if result.first == "\u{FEFF}" { result.removeFirst() }
        guard normalizeNewlines else { return result }
        return result.replacingOccurrences(of: "\r\n", with: "\n").replacingOccurrences(of: "\r", with: "\n")
    }
}

/// Subtitle files ↔ each other. The text of each cue is kept; styling (italics, colours, positions) is not.
enum SubtitleFormats {
    struct Format { let name: String; let ext: String }
    static let inputExtensions: Set<String> = ["srt", "vtt", "ass", "ssa", "lrc", "sbv"]
    static let formats: [Format] = [
        Format(name: "SRT", ext: "srt"), Format(name: "VTT", ext: "vtt"), Format(name: "ASS", ext: "ass"),
        Format(name: "LRC", ext: "lrc"), Format(name: "SBV", ext: "sbv"), Format(name: "TXT", ext: "txt"),
    ]
    static let maximumBytes = 8 * 1024 * 1024
    static let maximumCues = 100_000
    /// All cue text together; LRC can repeat one line under many timestamps.
    static let maximumTextBytes = 32 * 1024 * 1024

    static func items(sourceExt: Set<String>) -> [FileOption] {
        formats.filter { sourceExt != [$0.ext] }.map { format in
            FileOption(title: format.name) { url in try await convert(url, to: format) }
        }
    }

    static func convert(_ url: URL, to format: Format) async throws -> [URL] {
        let text = try await Images.detached { () throws -> String in
            let cues = try parse(try TextFile.read(url, limit: maximumBytes), ext: url.pathExtension.lowercased())
            return write(cues, as: format.ext)
        }
        return [try Paths.create(Data(text.utf8), at: Paths.preferred(for: url, ext: format.ext))]
    }

    // MARK: Reading

    static func parse(_ text: String, ext: String) throws -> [SubtitleCue] {
        var cues: [SubtitleCue]
        switch ext {
        case "ass", "ssa": cues = try parseASS(text)
        case "lrc": cues = try parseLRC(text)
        case "sbv": cues = parseBlocks(text, timing: { line in
            let parts = line.split(separator: ",", omittingEmptySubsequences: false)
            guard parts.count == 2, let a = time(parts[0]), let b = time(parts[1]) else { return nil }
            return (a, b)
        }, stripTags: false)
        default: cues = parseBlocks(text, timing: { line in
            let parts = line.components(separatedBy: "-->")
            guard parts.count == 2, let a = time(Substring(parts[0])),
                  let end = parts[1].split(whereSeparator: \.isWhitespace).first, let b = time(end) else { return nil }
            return (a, b)
        }, stripTags: true)
        }
        cues = cues.filter { $0.end > $0.start && !$0.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
        guard !cues.isEmpty else { throw ToolError(L("No subtitles with timing were found in this file.")) }
        guard cues.count <= maximumCues, cues.reduce(0, { $0 + $1.text.utf8.count }) <= maximumTextBytes else {
            throw ToolError(L("This subtitle file has too many cues."))
        }
        return cues.sorted { $0.start < $1.start }
    }

    /// `[h:]mm:ss[.,]fff` with any number of hour digits and 1–3 fraction digits (ASS uses hundredths).
    static func time<S: StringProtocol>(_ raw: S) -> Double? {
        let value = raw.trimmingCharacters(in: .whitespaces)
        let fields = value.split(separator: ":", omittingEmptySubsequences: false)
        guard (2...3).contains(fields.count) else { return nil }
        let tail = fields.last!.split(omittingEmptySubsequences: false) { $0 == "," || $0 == "." }
        guard (1...2).contains(tail.count) else { return nil }
        func number(_ s: Substring, max: Int? = nil) -> Int? {
            guard !s.isEmpty, s.count <= 6, s.allSatisfy({ $0.isASCII && $0.isNumber }), let n = Int(s) else { return nil }
            if let max, n >= max { return nil }
            return n
        }
        guard let seconds = number(tail[0], max: 60) else { return nil }
        let minutes = number(fields[fields.count - 2], max: fields.count == 3 ? 60 : nil)
        let hours = fields.count == 3 ? number(fields[0]) : 0
        guard let minutes, let hours else { return nil }
        var fraction = 0.0
        if tail.count == 2 {
            guard (1...3).contains(tail[1].count), let f = number(tail[1]) else { return nil }
            fraction = Double(f) / pow(10, Double(tail[1].count))
        }
        return Double(hours * 3600 + minutes * 60 + seconds) + fraction
    }

    /// SRT, WebVTT and SBV: blocks separated by blank lines, each with a timing line and text.
    private static func parseBlocks(_ text: String, timing: (String) -> (Double, Double)?, stripTags: Bool) -> [SubtitleCue] {
        var cues: [SubtitleCue] = []
        var current: (Double, Double)?, lines: [String] = []
        func flush() {
            if let (a, b) = current {
                var body = lines.joined(separator: "\n")
                if stripTags { body = plain(body) }
                cues.append(SubtitleCue(start: a, end: b, text: body.trimmingCharacters(in: .whitespacesAndNewlines)))
            }
            current = nil; lines = []
        }
        var skipping = false
        for line in text.components(separatedBy: "\n") {
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            if trimmed.isEmpty { flush(); skipping = false; continue }
            if skipping { continue }
            if current == nil {
                // WebVTT's header, comments and style blocks carry no cues.
                if trimmed.hasPrefix("WEBVTT") || trimmed.hasPrefix("NOTE") || trimmed == "STYLE" || trimmed == "REGION" { skipping = true; continue }
                if let pair = timing(trimmed) { current = pair }
                continue  // SRT's counter or a VTT cue identifier.
            }
            if let pair = timing(trimmed) { flush(); current = pair; continue }  // A missing blank line.
            lines.append(line)
        }
        flush()
        return cues
    }

    /// `<i>`, `<b>`, `<font …>`, `<v Speaker>` and `{\an8}` markup removed; entities decoded.
    static func plain(_ text: String) -> String {
        var out = text.replacing(#/<[^<>\n]{0,200}>/#, with: "").replacing(#/\{\\[^{}\n]{0,200}\}/#, with: "")
        for (entity, char) in [("&lt;", "<"), ("&gt;", ">"), ("&nbsp;", " "), ("&lrm;", "\u{200E}"), ("&rlm;", "\u{200F}"), ("&amp;", "&")] {
            out = out.replacingOccurrences(of: entity, with: char)
        }
        return out
    }

    private static func parseASS(_ text: String) throws -> [SubtitleCue] {
        var inEvents = false, fields: [String] = []
        var cues: [SubtitleCue] = []
        for raw in text.components(separatedBy: "\n") {
            let line = raw.trimmingCharacters(in: .whitespaces)
            if line.hasPrefix("[") { inEvents = line.lowercased() == "[events]"; continue }
            guard inEvents, let colon = line.firstIndex(of: ":") else { continue }
            let key = line[..<colon].lowercased(), value = line[line.index(after: colon)...]
            if key == "format" {
                fields = value.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces).lowercased() }
            } else if key == "dialogue" {
                guard let startIndex = fields.firstIndex(of: "start"), let endIndex = fields.firstIndex(of: "end"),
                      let textIndex = fields.firstIndex(of: "text"), textIndex == fields.count - 1 else {
                    throw ToolError(L("This ASS/SSA file has no valid Format line for its events."))
                }
                // The text is the last field and may itself contain commas.
                let parts = value.split(separator: ",", maxSplits: fields.count - 1, omittingEmptySubsequences: false)
                guard parts.count == fields.count, let a = time(parts[startIndex]), let b = time(parts[endIndex]) else { continue }
                let body = plain(String(parts[textIndex]))
                    .replacingOccurrences(of: "\\N", with: "\n").replacingOccurrences(of: "\\n", with: "\n")
                    .replacingOccurrences(of: "\\h", with: " ")
                cues.append(SubtitleCue(start: a, end: b, text: body.trimmingCharacters(in: .whitespacesAndNewlines)))
            }
        }
        return cues
    }

    /// Each lyric runs until the next timestamp; the last one gets four seconds. `[offset:+500]` moves every
    /// lyric 0.5 s earlier.
    private static func parseLRC(_ text: String) throws -> [SubtitleCue] {
        var stamps: [(Double, String)] = [], bytes = 0, offset = 0.0
        for raw in text.components(separatedBy: "\n") {
            var line = Substring(raw.trimmingCharacters(in: .whitespaces)), times: [Double] = []
            while line.first == "[", let close = line.firstIndex(of: "]") {
                let tag = line[line.index(after: line.startIndex)..<close]
                if tag.lowercased().hasPrefix("offset:"), let ms = Double(tag.dropFirst(7).trimmingCharacters(in: .whitespaces)), ms.isFinite {
                    offset = ms / 1000
                }
                guard let t = time(tag) else { break }  // [ar:…], [ti:…] and other metadata.
                times.append(t); line = line[line.index(after: close)...]
            }
            guard !times.isEmpty else { continue }
            let lyric = plain(String(line)).trimmingCharacters(in: .whitespaces)
            bytes += lyric.utf8.count * times.count
            guard stamps.count + times.count <= maximumCues, bytes <= maximumTextBytes else {
                throw ToolError(L("This subtitle file has too many cues."))
            }
            for t in times { stamps.append((t, lyric)) }
        }
        stamps = stamps.map { (max(0, $0.0 - offset), $0.1) }.sorted { $0.0 < $1.0 }
        return stamps.enumerated().compactMap { i, stamp in
            let end = i + 1 < stamps.count ? stamps[i + 1].0 : stamp.0 + 4
            return stamp.1.isEmpty ? nil : SubtitleCue(start: stamp.0, end: end, text: stamp.1)
        }
    }

    // MARK: Writing

    static func write(_ cues: [SubtitleCue], as ext: String) -> String {
        // A blank line inside a cue would end it early in block formats.
        let cues = cues.map { SubtitleCue(start: $0.start, end: $0.end, text: $0.text.replacing(#/\n\s*\n+/#, with: "\n")) }
        switch ext {
        case "vtt":
            return "WEBVTT\n\n" + cues.map { cue in
                let body = cue.text.replacingOccurrences(of: "&", with: "&amp;").replacingOccurrences(of: "<", with: "&lt;")
                    .replacingOccurrences(of: ">", with: "&gt;")
                return "\(stamp(cue.start, ".")) --> \(stamp(cue.end, "."))\n\(body)\n"
            }.joined(separator: "\n")
        case "ass":
            let header = """
            [Script Info]
            ScriptType: v4.00+
            WrapStyle: 0
            ScaledBorderAndShadow: yes
            PlayResX: 1920
            PlayResY: 1080

            [V4+ Styles]
            Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
            Style: Default,Arial,64,&H00FFFFFF,&H000000FF,&H00000000,&H64000000,0,0,0,0,100,100,0,0,1,3,1,2,40,40,50,1

            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text

            """
            return header + cues.map { cue in
                // Braces start override tags in ASS; there is no escape for them.
                let body = cue.text.replacingOccurrences(of: "{", with: "(").replacingOccurrences(of: "}", with: ")")
                    .replacingOccurrences(of: "\\", with: "\u{FF3C}").replacingOccurrences(of: "\n", with: "\\N")
                return "Dialogue: 0,\(assStamp(cue.start)),\(assStamp(cue.end)),Default,,0,0,0,,\(body)\n"
            }.joined()
        case "lrc":
            var lines: [String] = []
            for (i, cue) in cues.enumerated() {
                // A lyric that itself starts with "[" would be read back as another tag.
                let lyric = cue.text.replacingOccurrences(of: "\n", with: " ")
                lines.append("[\(lrcStamp(cue.start))]" + (lyric.hasPrefix("[") ? "\u{200B}" : "") + lyric)
                // An empty line ends the lyric when the next one doesn't start right away.
                let next = i + 1 < cues.count ? cues[i + 1].start : .infinity
                if next - cue.end > 0.05 { lines.append("[\(lrcStamp(cue.end))]") }
            }
            return lines.joined(separator: "\n") + "\n"
        case "sbv":
            return cues.map { "\(sbvStamp($0.start)),\(sbvStamp($0.end))\n\($0.text)\n" }.joined(separator: "\n")
        case "txt":
            return cues.map(\.text).joined(separator: "\n") + "\n"
        default:
            return cues.enumerated().map { "\($0.offset + 1)\n\(stamp($0.element.start, ",")) --> \(stamp($0.element.end, ","))\n\($0.element.text)\n" }
                .joined(separator: "\n")
        }
    }

    private static func milliseconds(_ seconds: Double) -> Int64 {
        Int64((min(35_999_999.999, max(0, seconds.isFinite ? seconds : 0)) * 1000).rounded())
    }
    static func stamp(_ seconds: Double, _ separator: String) -> String {
        let ms = milliseconds(seconds)
        return String(format: "%02lld:%02lld:%02lld%@%03lld", ms / 3_600_000, ms / 60000 % 60, ms / 1000 % 60, separator, ms % 1000)
    }
    private static func assStamp(_ seconds: Double) -> String {
        let cs = (milliseconds(seconds) + 5) / 10
        return String(format: "%lld:%02lld:%02lld.%02lld", cs / 360_000, cs / 6000 % 60, cs / 100 % 60, cs % 100)
    }
    private static func sbvStamp(_ seconds: Double) -> String {
        let ms = milliseconds(seconds)
        return String(format: "%lld:%02lld:%02lld.%03lld", ms / 3_600_000, ms / 60000 % 60, ms / 1000 % 60, ms % 1000)
    }
    private static func lrcStamp(_ seconds: Double) -> String {
        let cs = (milliseconds(seconds) + 5) / 10
        return String(format: "%02lld:%02lld.%02lld", cs / 6000, cs / 100 % 60, cs % 100)
    }
}
