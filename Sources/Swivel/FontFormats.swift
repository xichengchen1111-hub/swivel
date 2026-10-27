import Compression
import CoreText
import Foundation

/// Desktop fonts (TTF, OTF) ↔ web fonts (WOFF, WOFF2). The outlines are never touched: tables are copied as they
/// are, so TTF stays TrueType and OTF stays PostScript. WOFF2 is written with its "null" transforms.
enum FontFormats {
    struct Format { let name: String; let ext: String }
    static let inputExtensions: Set<String> = ["ttf", "otf", "woff", "woff2"]
    static let woff = Format(name: "WOFF", ext: "woff"), woff2 = Format(name: "WOFF2", ext: "woff2")
    /// Unpacks a web font to TTF or OTF, whichever its outlines are.
    static let desktop = Format(name: "Desktop Font", ext: "")
    static let maximumBytes = 64 * 1024 * 1024

    static func items(sourceExt: Set<String>) -> [FileOption] {
        var list: [Format] = []
        if !sourceExt.isDisjoint(with: ["woff", "woff2"]) { list.append(desktop) }
        list += [woff, woff2].filter { sourceExt != [$0.ext] }
        return list.map { format in FileOption(title: format.name) { url in try await convert(url, to: format) } }
    }

    static func convert(_ url: URL, to format: Format) async throws -> [URL] {
        let (data, ext) = try await Images.detached { () throws -> (Data, String) in
            let font = try read(url)
            switch format.ext {
            // Laid out afresh first, so head.checkSumAdjustment matches the font a decoder rebuilds.
            case "woff": return (try encodeWOFF(parseSFNT(sfnt(font))), "woff")
            case "woff2": return (try encodeWOFF2(parseSFNT(sfnt(font))), "woff2")
            default: return (sfnt(font), font.isCFF ? "otf" : "ttf")
            }
        }
        guard let descriptors = CTFontManagerCreateFontDescriptorsFromData(data as CFData) as? [CTFontDescriptor], !descriptors.isEmpty else {
            throw ToolError(L("The converted font could not be read back."))
        }
        return [try Paths.create(data, at: Paths.preferred(for: url, ext: ext))]
    }

    /// TTF or OTF for a web font, from the flavor in its header (for predicting an output name).
    static func desktopExtension(_ url: URL) -> String {
        guard let head = try? FileSafety.read(url, limit: maximumBytes).prefix(8), head.count == 8 else { return "ttf" }
        return head.u32(4) == 0x4F54_544F ? "otf" : "ttf"
    }

    struct Font {
        var flavor: UInt32
        var tables: [(tag: UInt32, data: Data, checksum: UInt32)]
        var isCFF: Bool { flavor == 0x4F54_544F }  // 'OTTO'
    }

    static func read(_ url: URL) throws -> Font {
        let data = try FileSafety.read(url, limit: maximumBytes)
        guard data.count >= 12 else { throw ToolError(L("This is not a font file Swivel can read.")) }
        switch data.u32(0) {
        case 0x774F_4646: return try decodeWOFF(data)                         // 'wOFF'
        case 0x774F_4632: return try decodeWithCoreText(data)                 // 'wOF2': transformed glyphs need a full decoder
        case 0x0001_0000, 0x4F54_544F, 0x7472_7565: return try parseSFNT(data) // TrueType, 'OTTO', 'true'
        default: throw ToolError(L("This is not a font file Swivel can read. Font collections (TTC) aren't supported."))
        }
    }

    // MARK: sfnt

    static func parseSFNT(_ data: Data) throws -> Font {
        let count = Int(data.u16(4))
        guard count > 0, count <= 1024, data.count >= 12 + count * 16 else { throw ToolError(L("This font file is damaged.")) }
        var tables: [(UInt32, Data, UInt32)] = []
        for i in 0..<count {
            let at = 12 + i * 16
            let offset = Int(data.u32(at + 8)), length = Int(data.u32(at + 12))
            guard offset >= 0, length >= 0, offset <= data.count, length <= data.count - offset else { throw ToolError(L("This font file is damaged.")) }
            tables.append((data.u32(at), data.subdata(in: data.startIndex + offset..<data.startIndex + offset + length), data.u32(at + 4)))
        }
        return try validated(Font(flavor: data.u32(0) == 0x7472_7565 ? 0x0001_0000 : data.u32(0), tables: tables))
    }

    private static func validated(_ font: Font) throws -> Font {
        let tags = font.tables.map(\.tag)
        guard Set(tags).count == tags.count, tags.contains(0x6865_6164), // 'head'
              font.tables.first(where: { $0.tag == 0x6865_6164 })!.data.count >= 54 else { throw ToolError(L("This font file is damaged.")) }
        return font
    }

    /// Tables in tag order, 4-byte aligned, with fresh checksums and head.checkSumAdjustment.
    static func sfnt(_ font: Font) -> Data {
        let tables = font.tables.sorted { $0.tag < $1.tag }
        let n = tables.count
        var power = 1, selector = 0
        while power * 2 <= n { power *= 2; selector += 1 }
        var out = Data()
        out.put32(font.flavor); out.put16(UInt16(n)); out.put16(UInt16(power * 16)); out.put16(UInt16(selector)); out.put16(UInt16(n * 16 - power * 16))
        var offset = 12 + n * 16, headOffset = 0
        for table in tables {
            var body = table.data
            if table.tag == 0x6865_6164 { body.replaceSubrange(body.startIndex + 8..<body.startIndex + 12, with: [0, 0, 0, 0]); headOffset = offset }
            out.put32(table.tag); out.put32(checksum(body)); out.put32(UInt32(offset)); out.put32(UInt32(body.count))
            offset += (body.count + 3) & ~3
        }
        for table in tables {
            var body = table.data
            if table.tag == 0x6865_6164 { body.replaceSubrange(body.startIndex + 8..<body.startIndex + 12, with: [0, 0, 0, 0]) }
            out.append(body); out.append(Data(count: (4 - body.count % 4) % 4))
        }
        let adjustment = 0xB1B0_AFBA &- checksum(out)
        out.replaceSubrange(headOffset + 8..<headOffset + 12, with: withUnsafeBytes(of: adjustment.bigEndian, Array.init))
        return out
    }

    /// head's own checksum is taken with its checkSumAdjustment field zeroed.
    static func tableChecksum(_ tag: UInt32, _ data: Data) -> UInt32 {
        guard tag == 0x6865_6164, data.count >= 12 else { return checksum(data) }
        var body = data
        body.replaceSubrange(body.startIndex + 8..<body.startIndex + 12, with: [0, 0, 0, 0])
        return checksum(body)
    }

    static func checksum(_ data: Data) -> UInt32 {
        var sum: UInt32 = 0, i = data.startIndex
        while i < data.endIndex {
            var word: UInt32 = 0
            for k in 0..<4 { word = word << 8 | UInt32(i + k < data.endIndex ? data[i + k] : 0) }
            sum &+= word; i += 4
        }
        return sum
    }

    private static func sfntSize(_ font: Font) -> Int { 12 + 16 * font.tables.count + font.tables.reduce(0) { $0 + ($1.data.count + 3) & ~3 } }

    // MARK: WOFF 1.0 (zlib per table)

    static func encodeWOFF(_ font: Font) throws -> Data {
        let tables = font.tables.sorted { $0.tag < $1.tag }
        var directory = Data(), body = Data()
        var offset = 44 + 20 * tables.count
        for table in tables {
            let compressed = zlib(table.data)
            let stored = compressed.count < table.data.count ? compressed : table.data
            directory.put32(table.tag); directory.put32(UInt32(offset)); directory.put32(UInt32(stored.count))
            directory.put32(UInt32(table.data.count)); directory.put32(tableChecksum(table.tag, table.data))
            body.append(stored); body.append(Data(count: (4 - stored.count % 4) % 4))
            offset += (stored.count + 3) & ~3
        }
        var out = Data()
        out.put32(0x774F_4646); out.put32(font.flavor); out.put32(UInt32(44 + directory.count + body.count))
        out.put16(UInt16(tables.count)); out.put16(0); out.put32(UInt32(sfntSize(font)))
        out.put16(1); out.put16(0)
        for _ in 0..<5 { out.put32(0) }  // No metadata or private block.
        return out + directory + body
    }

    static func decodeWOFF(_ data: Data) throws -> Font {
        let count = Int(data.u16(12))
        guard data.count >= 44 + 20 * count, count > 0, count <= 1024 else { throw ToolError(L("This font file is damaged.")) }
        var tables: [(UInt32, Data, UInt32)] = []
        for i in 0..<count {
            let at = 44 + 20 * i
            let offset = Int(data.u32(at + 4)), stored = Int(data.u32(at + 8)), length = Int(data.u32(at + 12))
            guard offset <= data.count, stored <= data.count - offset, stored <= length, length <= maximumBytes else {
                throw ToolError(L("This font file is damaged."))
            }
            let chunk = data.subdata(in: data.startIndex + offset..<data.startIndex + offset + stored)
            let table = stored == length ? chunk : try unzlib(chunk, length: length)
            tables.append((data.u32(at), table, data.u32(at + 16)))
        }
        return try validated(Font(flavor: data.u32(4), tables: tables))
    }

    /// The Compression framework speaks raw DEFLATE; WOFF wants the zlib wrapper around it.
    private static func zlib(_ data: Data) -> Data {
        guard !data.isEmpty else { return data }
        var buffer = [UInt8](repeating: 0, count: data.count + 1024)
        let n = data.withUnsafeBytes { compression_encode_buffer(&buffer, buffer.count, $0.bindMemory(to: UInt8.self).baseAddress!, data.count, nil, COMPRESSION_ZLIB) }
        guard n > 0 else { return data }
        var a: UInt32 = 1, b: UInt32 = 0
        for byte in data { a = (a + UInt32(byte)) % 65521; b = (b + a) % 65521 }
        var out = Data([0x78, 0x9C]); out.append(contentsOf: buffer.prefix(n)); out.put32(b << 16 | a)
        return out
    }

    private static func unzlib(_ data: Data, length: Int) throws -> Data {
        guard data.count > 6, data[data.startIndex] & 0x0F == 8, (UInt16(data[data.startIndex]) << 8 | UInt16(data[data.startIndex + 1])) % 31 == 0 else {
            throw ToolError(L("This font file is damaged."))
        }
        let raw = data.dropFirst(2).dropLast(4)
        var out = [UInt8](repeating: 0, count: length + 1)
        let n = raw.withUnsafeBytes { compression_decode_buffer(&out, out.count, $0.bindMemory(to: UInt8.self).baseAddress!, raw.count, nil, COMPRESSION_ZLIB) }
        guard n == length else { throw ToolError(L("This font file is damaged.")) }
        return Data(out.prefix(n))
    }

    // MARK: WOFF 2.0

    static func encodeWOFF2(_ font: Font) throws -> Data {
        var tables = font.tables.sorted { $0.tag < $1.tag }
        // loca follows glyf in the directory, as WOFF2 decoders expect.
        if let loca = tables.firstIndex(where: { $0.tag == 0x6C6F_6361 }), tables.contains(where: { $0.tag == 0x676C_7966 }) {
            let entry = tables.remove(at: loca)
            tables.insert(entry, at: tables.firstIndex(where: { $0.tag == 0x676C_7966 })! + 1)
        }
        var directory = Data(), stream = Data()
        for table in tables {
            // Tag index 63 means "the tag follows"; transform version 3 is the null transform for glyf and loca.
            let nullTransform: UInt8 = [0x676C_7966, 0x6C6F_6361].contains(table.tag) ? 3 << 6 : 0
            directory.append(nullTransform | 63); directory.put32(table.tag); directory.append(base128(UInt32(table.data.count)))
            stream.append(table.data)
        }
        var compressed = [UInt8](repeating: 0, count: stream.count + stream.count / 2 + 4096)
        let n = stream.withUnsafeBytes { compression_encode_buffer(&compressed, compressed.count, $0.bindMemory(to: UInt8.self).baseAddress!, stream.count, nil, COMPRESSION_BROTLI) }
        guard n > 0 else { throw ToolError(L("Couldn't compress the font.")) }
        let padding = (4 - (48 + directory.count + n) % 4) % 4
        var out = Data()
        out.put32(0x774F_4632); out.put32(font.flavor); out.put32(UInt32(48 + directory.count + n + padding))
        out.put16(UInt16(tables.count)); out.put16(0); out.put32(UInt32(sfntSize(font))); out.put32(UInt32(n))
        out.put16(1); out.put16(0)
        for _ in 0..<5 { out.put32(0) }
        out.append(directory); out.append(contentsOf: compressed.prefix(n)); out.append(Data(count: padding))
        return out
    }

    private static func base128(_ value: UInt32) -> Data {
        var bytes: [UInt8] = [UInt8(value & 0x7F)], v = value >> 7
        while v > 0 { bytes.insert(UInt8(v & 0x7F) | 0x80, at: 0); v >>= 7 }
        return Data(bytes)
    }

    /// WOFF2 usually stores glyphs in a transformed form; CoreText decodes it and hands back the sfnt tables.
    private static func decodeWithCoreText(_ data: Data) throws -> Font {
        guard let descriptors = CTFontManagerCreateFontDescriptorsFromData(data as CFData) as? [CTFontDescriptor], descriptors.count == 1 else {
            throw ToolError(L("This font file is damaged."))
        }
        let font = CTFontCreateWithFontDescriptor(descriptors[0], 12, nil)
        // The array holds the tags themselves, not CFNumbers.
        guard let available = CTFontCopyAvailableTables(font, []) else { throw ToolError(L("This font file is damaged.")) }
        let tags = (0..<CFArrayGetCount(available)).map { UInt32(truncatingIfNeeded: UInt(bitPattern: CFArrayGetValueAtIndex(available, $0))) }
        let tables = tags.compactMap { tag -> (UInt32, Data, UInt32)? in
            guard let table = CTFontCopyTable(font, tag, []) as Data? else { return nil }
            return (tag, table, tableChecksum(tag, table))
        }
        let cff = tags.contains(0x4346_4620) || tags.contains(0x4346_4632)  // 'CFF ', 'CFF2'
        return try validated(Font(flavor: cff ? 0x4F54_544F : 0x0001_0000, tables: tables))
    }
}

private extension Data {
    func u16(_ at: Int) -> UInt16 {
        guard at >= 0, at + 2 <= count else { return 0 }
        return UInt16(self[startIndex + at]) << 8 | UInt16(self[startIndex + at + 1])
    }
    func u32(_ at: Int) -> UInt32 {
        guard at >= 0, at + 4 <= count else { return 0 }
        return (0..<4).reduce(0) { $0 << 8 | UInt32(self[startIndex + at + $1]) }
    }
    mutating func put16(_ v: UInt16) { append(contentsOf: [UInt8(v >> 8), UInt8(v & 0xFF)]) }
    mutating func put32(_ v: UInt32) { append(contentsOf: [UInt8(v >> 24), UInt8(v >> 16 & 0xFF), UInt8(v >> 8 & 0xFF), UInt8(v & 0xFF)]) }
}
