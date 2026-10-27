import AppKit
import CoreText
import Darwin
import ImageIO
import ModelIO
import Testing
@testable import Swivel

/// The kinds learned from Filee's format table: subtitles, data, 3D models, fonts and icon images.
@MainActor @Suite(.serialized)
struct FormatKindsTests {
    private func folder() throws -> URL {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("swivel-formats-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }

    @Test func newKindsAreRecognisedAndOfferTheirFormats() {
        func kind(_ name: String) -> FileKind { FileKind.of(URL(fileURLWithPath: "/nonexistent/" + name)) }
        #expect(kind("a.srt") == .subtitle && kind("a.ass") == .subtitle && kind("a.lrc") == .subtitle)
        #expect(kind("a.json") == .data && kind("a.csv") == .data && kind("a.plist") == .data)
        #expect(kind("a.obj") == .model3D && kind("a.usdz") == .model3D && kind("a.stl") == .model3D)
        #expect(kind("a.ttf") == .font && kind("a.woff2") == .font)
        #expect(kind("a.txt") == .document && kind("a.png") == .image)
        let srt = Catalog.defaultItems(for: [URL(fileURLWithPath: "/nonexistent/a.srt")], mode: .convert).map(\.title)
        #expect(srt.contains("VTT") && !srt.contains("SRT"))
        let woff = Catalog.defaultItems(for: [URL(fileURLWithPath: "/nonexistent/a.woff2")], mode: .convert).map(\.title)
        #expect(woff == ["Desktop Font", "WOFF"])
        let png = Catalog.defaultItems(for: [URL(fileURLWithPath: "/nonexistent/a.png")], mode: .convert).map(\.title)
        #expect(png.contains("ICO") && png.contains("ICNS") && png.contains("JPEG 2000"))
        for key in ["Subtitles", "Data files", "3D models", "Fonts", "Desktop Font"] { #expect(Strings.table[key]?.count == 3, "\(key)") }
        for kind in [FileKind.subtitle, .data, .model3D, .font] {
            for title in Catalog.allItems(mode: .convert, kind: kind) {
                #expect(MCPCatalog.output(kind: kind, mode: .convert, title: title) != nil, "\(kind) \(title)")
            }
        }
    }

    // MARK: Subtitles

    @Test func subtitleTimesAndFormatsRoundTrip() throws {
        #expect(SubtitleFormats.time("00:01:02,345") == 62.345)
        #expect(SubtitleFormats.time("1:02.5") == 62.5)
        #expect(SubtitleFormats.time("0:00:01.50") == 1.5)
        #expect(SubtitleFormats.time("00:61:00,000") == nil && SubtitleFormats.time("abc") == nil)

        let vtt = "WEBVTT\n\nNOTE skip\n\nid\n00:01.000 --> 00:02.500 align:start\n<v Bob>Hi &amp; bye</v>\n\n00:00:03.000 --> 00:00:05.000\nTwo\nlines\n"
        let cues = try SubtitleFormats.parse(vtt, ext: "vtt")
        #expect(cues == [SubtitleCue(start: 1, end: 2.5, text: "Hi & bye"), SubtitleCue(start: 3, end: 5, text: "Two\nlines")])
        for ext in ["srt", "vtt", "ass", "sbv"] {
            #expect(try SubtitleFormats.parse(SubtitleFormats.write(cues, as: ext), ext: ext) == cues, "\(ext)")
        }
        let lrc = try SubtitleFormats.parse(SubtitleFormats.write(cues, as: "lrc"), ext: "lrc")
        #expect(lrc.map(\.text) == ["Hi & bye", "Two lines"] && lrc[0].start == 1 && lrc[0].end == 2.5)

        let ass = "[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:01.50,0:00:03.00,Default,,0,0,0,,{\\an8}Hi, there\\Nline two\n"
        #expect(try SubtitleFormats.parse(ass, ext: "ass") == [SubtitleCue(start: 1.5, end: 3, text: "Hi, there\nline two")])
        #expect(throws: ToolError.self) { try SubtitleFormats.parse("no cues here", ext: "srt") }
    }

    @Test func subtitlesInALegacyEncodingBecomeUTF8() async throws {
        let dir = try folder()
        defer { try? FileManager.default.removeItem(at: dir) }
        let source = dir.appendingPathComponent("clip.srt")
        let text = "1\r\n00:00:01,000 --> 00:00:02,000\r\n<i>你好</i>，世界\r\n"
        try #require(text.data(using: String.Encoding(rawValue: CFStringConvertEncodingToNSStringEncoding(CFStringEncoding(CFStringEncodings.GB_18030_2000.rawValue)))))
            .write(to: source)
        let out = try await SubtitleFormats.convert(source, to: SubtitleFormats.formats.first { $0.ext == "vtt" }!)
        #expect(out.map(\.lastPathComponent) == ["clip.vtt"])
        #expect(try String(contentsOf: out[0], encoding: .utf8) == "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\n你好，世界\n")
    }

    // MARK: Data

    @Test func csvFollowsRFC4180AndTablesRoundTripThroughJSON() throws {
        let rows = try DataFormats.table("name,note\n\"Smith, J\",\"He said \"\"hi\"\"\nnext\"\nLi,简单\n", separator: ",")
        #expect(rows == [["name", "note"], ["Smith, J", "He said \"hi\"\nnext"], ["Li", "简单"]])
        let json = try DataFormats.write(.table(rows), as: "json")
        let back = try DataFormats.write(.tree(try JSONSerialization.jsonObject(with: json)), as: "csv")
        #expect(try DataFormats.table(TextFile.decode(back), separator: ",") == rows)
        #expect(throws: ToolError.self) { try DataFormats.table("a\n\"open", separator: ",") }
        #expect(throws: ToolError.self) { try DataFormats.write(.table([["a", "a"], ["1", "2"]]), as: "json") }
    }

    @Test func valuesAFormatCannotHoldAreRefusedOrMadeExplicit() throws {
        let tree = try JSONSerialization.jsonObject(with: Data(#"{"a":null}"#.utf8))
        #expect(throws: ToolError.self) { try DataFormats.write(.tree(tree), as: "plist") }
        let plist: [String: Any] = ["when": Date(timeIntervalSince1970: 0), "blob": Data("hi".utf8), "n": 4]
        let json = try JSONSerialization.jsonObject(with: try DataFormats.write(.tree(plist), as: "json")) as? [String: Any]
        #expect(json?["when"] as? String == "1970-01-01T00:00:00Z" && json?["blob"] as? String == "aGk=" && json?["n"] as? Int == 4)
        let yaml = String(decoding: try DataFormats.write(.tree(["k": ["yes", NSNull(), true, "a\"b\n"]] as [String: Any]), as: "yaml"), as: UTF8.self)
        #expect(yaml == "---\n\"k\":\n  - \"yes\"\n  - null\n  - true\n  - \"a\\\"b\\n\"\n")
        let lines = String(decoding: try DataFormats.write(.tree([["b": 1, "a": 2], 3]), as: "jsonl"), as: UTF8.self)
        #expect(lines == "{\"a\":2,\"b\":1}\n3\n")
    }

    // MARK: Fonts

    private func tables(_ data: Data) throws -> [UInt32: Data] {
        let font = try FontFormats.parseSFNT(data)
        return Dictionary(uniqueKeysWithValues: font.tables.map { ($0.tag, $0.data) })
    }

    @Test func fontsRoundTripThroughWOFFAndWOFF2WithIdenticalTables() async throws {
        let dir = try folder()
        defer { try? FileManager.default.removeItem(at: dir) }
        for system in ["/System/Library/Fonts/Supplemental/Georgia.ttf", "/System/Library/Fonts/Supplemental/NotoSansCanadianAboriginal-Regular.otf"] {
            guard FileManager.default.fileExists(atPath: system) else { continue }
            let ext = (system as NSString).pathExtension
            let source = dir.appendingPathComponent("font.\(ext)")
            try? FileManager.default.removeItem(at: source)
            try FileManager.default.copyItem(at: URL(fileURLWithPath: system), to: source)
            let original = try tables(Data(contentsOf: source))
            for web in [FontFormats.woff, FontFormats.woff2] {
                let packed = try await FontFormats.convert(source, to: web)[0]
                #expect(packed.pathExtension == web.ext)
                let unpacked = try await FontFormats.convert(packed, to: FontFormats.desktop)[0]
                #expect(unpacked.pathExtension == ext)
                let back = try tables(Data(contentsOf: unpacked))
                #expect(Set(back.keys) == Set(original.keys))
                for (tag, data) in original where tag != 0x6865_6164 { #expect(back[tag] == data, "\(web.name) \(tag)") }
                // Only head.checkSumAdjustment may differ, and the rebuilt font checksums to the magic number.
                #expect(back[0x6865_6164]?.prefix(8) == original[0x6865_6164]?.prefix(8))
                #expect(FontFormats.checksum(try Data(contentsOf: unpacked)) == 0xB1B0_AFBA)
                try FileManager.default.removeItem(at: packed); try FileManager.default.removeItem(at: unpacked)
            }
        }
        let junk = dir.appendingPathComponent("junk.ttf")
        try Data(repeating: 7, count: 64).write(to: junk)
        await #expect(throws: ToolError.self) { try await FontFormats.convert(junk, to: FontFormats.woff) }
    }

    // MARK: 3D

    @Test func modelsConvertKeepingTheirGeometry() async throws {
        let dir = try folder()
        defer { try? FileManager.default.removeItem(at: dir) }
        let source = dir.appendingPathComponent("cube.obj")
        let corners = (0..<8).map { "v \($0 & 1) \($0 >> 1 & 1) \($0 >> 2 & 1)" }
        let faces = ["1 2 4 3", "5 6 8 7", "1 2 6 5", "3 4 8 7", "1 3 7 5", "2 4 8 6"].map { "f " + $0 }
        try (corners + faces).joined(separator: "\n").write(to: source, atomically: true, encoding: .utf8)
        for format in Models3D.formats {
            let out = try await Models3D.convert(source, to: format)
            #expect(out.count == 1 && out[0].pathExtension == format.ext)
            if format.ext == "png" {
                let image = try #require(CGImageSourceCreateWithURL(out[0] as CFURL, nil).flatMap { CGImageSourceCreateImageAtIndex($0, 0, nil) })
                #expect(image.width == Models3D.previewSize && image.height == Models3D.previewSize)
            } else {
                let meshes = try Models3D.meshes(in: MDLAsset(url: out[0]))
                #expect(!meshes.isEmpty && meshes.reduce(0) { $0 + $1.vertexCount } >= 8, "\(format.name)")
            }
        }
        let empty = dir.appendingPathComponent("empty.obj")
        try "# nothing\n".write(to: empty, atomically: true, encoding: .utf8)
        await #expect(throws: ToolError.self) { try await Models3D.convert(empty, to: Models3D.formats[1]) }
    }

    // MARK: Images

    @Test func iconsHoldSeveralSquareSizesAndNewFormatsKeepDimensions() async throws {
        let dir = try folder()
        defer { try? FileManager.default.removeItem(at: dir) }
        let source = dir.appendingPathComponent("photo.png")
        let ctx = try #require(CGContext(data: nil, width: 600, height: 300, bitsPerComponent: 8, bytesPerRow: 0,
                                         space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        ctx.setFillColor(CGColor(red: 0.2, green: 0.5, blue: 0.9, alpha: 1)); ctx.fill(CGRect(x: 0, y: 0, width: 600, height: 300))
        let dest = try #require(CGImageDestinationCreateWithURL(source as CFURL, "public.png" as CFString, 1, nil))
        CGImageDestinationAddImage(dest, try #require(ctx.makeImage()), nil)
        #expect(CGImageDestinationFinalize(dest))

        for icon in Images.iconFormats {
            let out = try await Images.makeIcon(source, as: icon)[0]
            let read = try #require(CGImageSourceCreateWithURL(out as CFURL, nil))
            let sizes = (0..<CGImageSourceGetCount(read)).compactMap { CGImageSourceCopyPropertiesAtIndex(read, $0, nil) as? [CFString: Any] }
                .map { ($0[kCGImagePropertyPixelWidth] as? Int ?? 0, $0[kCGImagePropertyPixelHeight] as? Int ?? 0) }
            #expect(sizes.allSatisfy { $0.0 == $0.1 && $0.0 <= 600 }, "\(icon.name) \(sizes)")
            #expect(Set(sizes.map(\.0)) == Set(icon.sizes.filter { $0 <= 600 }), "\(icon.name)")
        }
        for name in ["JPEG 2000", "PSD", "TGA"] {
            let format = try #require(Images.outputFormats.first { $0.name == name })
            let out = try await Images.convert(source, to: format)[0]
            let props = CGImageSourceCreateWithURL(out as CFURL, nil).flatMap { CGImageSourceCopyPropertiesAtIndex($0, 0, nil) } as? [CFString: Any]
            #expect(props?[kCGImagePropertyPixelWidth] as? Int == 600 && props?[kCGImagePropertyPixelHeight] as? Int == 300)
        }
    }

    // MARK: Review findings (hostile and edge-case inputs)

    @Test func aMeshNestedDeeplyCannotPackALocalFileIntoAUSDZ() async throws {
        let dir = try folder()
        defer { try? FileManager.default.removeItem(at: dir) }
        let secret = dir.appendingPathComponent("secret.png")
        try Data("not really an image".utf8).write(to: secret)
        let mesh = """
        def Mesh "M"
        {
            int[] faceVertexCounts = [4]
            int[] faceVertexIndices = [0, 1, 2, 3]
            point3f[] points = [(0,0,0), (1,0,0), (1,1,0), (0,1,0)]
            texCoord2f[] primvars:st = [(0,0), (1,0), (1,1), (0,1)] ( interpolation = "vertex" )
            rel material:binding = </Root/Mat>
        }

        """
        var inner = mesh
        for level in stride(from: 300, through: 1, by: -1) { inner = "def Xform \"L\(level)\"\n{\n\(inner)}\n" }
        let material = """
        def Material "Mat"
        {
            token outputs:surface.connect = </Root/Mat/PBR.outputs:surface>
            def Shader "PBR"
            {
                uniform token info:id = "UsdPreviewSurface"
                color3f inputs:diffuseColor.connect = </Root/Mat/Tex.outputs:rgb>
                token outputs:surface
            }
            def Shader "Tex"
            {
                uniform token info:id = "UsdUVTexture"
                asset inputs:file = @\(secret.path)@
                float3 outputs:rgb
            }
        }

        """
        let source = dir.appendingPathComponent("deep.usda")
        try ("#usda 1.0\n(\n    defaultPrim = \"Root\"\n)\ndef Xform \"Root\"\n{\n" + inner + material + "}\n")
            .write(to: source, atomically: true, encoding: .utf8)
        let out = try await Models3D.convert(source, to: Models3D.formats.first { $0.ext == "usdz" }!)[0]
        #expect(Models3D.zipEntries(out) == 1)
        #expect(try Data(contentsOf: out).range(of: Data("not really an image".utf8)) == nil)

        let referencing = dir.appendingPathComponent("ref.usda")
        try "#usda 1.0\ndef Xform \"A\" (\n    references = </B>\n)\n{\n}\n".write(to: referencing, atomically: true, encoding: .utf8)
        await #expect(throws: ToolError.self) { try await Models3D.convert(referencing, to: Models3D.formats[1]) }

        let obj = try await Models3D.convert(source, to: Models3D.formats.first { $0.ext == "obj" }!)[0]
        #expect(!(try String(contentsOf: obj, encoding: .utf8)).contains("mtllib"))
    }

    @Test func dataValuesThatWouldCrashOrExplodeAreRefused() throws {
        let nan: [String: Any] = ["a": Double.nan, "b": [Double.infinity]]
        for ext in ["json", "jsonl", "csv"] {
            #expect(throws: ToolError.self) { try DataFormats.write(.tree(nan), as: ext) }
        }
        #expect(String(decoding: try DataFormats.write(.tree(nan), as: "yaml"), as: UTF8.self) == "---\n\"a\": .nan\n\"b\":\n  - .inf\n")
        // A binary plist can share one array at every level: tiny on disk, doubling with each level when written.
        var shared = NSArray(object: "x")
        for _ in 0..<30 { shared = NSArray(array: [shared, shared]) }
        for ext in ["json", "yaml", "plist"] {
            #expect(throws: ToolError.self) { try DataFormats.write(.tree(shared), as: ext) }
        }
        let big = try JSONSerialization.jsonObject(with: Data("[12345678901234567890123]".utf8))
        #expect(throws: ToolError.self) { try DataFormats.write(.tree(big), as: "plist") }
        // Many records with different keys would make a huge, mostly empty table.
        let sparse = (0..<5000).map { ["k\($0)": 1] }
        #expect(throws: ToolError.self) { try DataFormats.write(.tree(sparse), as: "csv") }
    }

    @Test func deepWideAndLargeDataFollowTheBudgets() throws {
        // Deeper than Foundation's JSON writer can recurse on a worker thread: refused, not a crash.
        var deep: Any = "x"
        for _ in 0..<200 { deep = [deep] }
        #expect(throws: ToolError.self) { try DataFormats.write(.tree(deep), as: "json") }
        // A wide header over many short rows would be padded into a huge result.
        let header = (0..<10_000).map { "c\($0)" }
        #expect(throws: ToolError.self) { try DataFormats.write(.table([header] + Array(repeating: ["x"], count: 2_000)), as: "json") }
        // Ordinary flat data with millions of values is fine.
        try DataFormats.check(Array(repeating: NSNumber(value: 1), count: 3_000_000), json: true)
    }

    @Test func binaryUSDAndPackagesAreCheckedForCompositionByContent() async throws {
        guard FileManager.default.isExecutableFile(atPath: "/usr/bin/usdcat"), FileManager.default.isExecutableFile(atPath: "/usr/bin/usdzip") else { return }
        let dir = try folder()
        defer { try? FileManager.default.removeItem(at: dir) }
        let mesh = "    def Mesh \"M\"\n    {\n        int[] faceVertexCounts = [3]\n        int[] faceVertexIndices = [0, 1, 2]\n        point3f[] points = [(0,0,0), (1,0,0), (0,1,0)]\n    }\n"
        try "#usda 1.0\ndef Xform \"P\"\n{\n\(mesh)}\n".write(to: dir.appendingPathComponent("plain.usda"), atomically: true, encoding: .utf8)
        try "#usda 1.0\ndef Xform \"B\"\n{\n\(mesh)}\ndef Xform \"A\" (\n    references = </B>\n)\n{\n}\n"
            .write(to: dir.appendingPathComponent("ref.usda"), atomically: true, encoding: .utf8)
        func tool(_ path: String, _ arguments: [String]) throws {
            let process = Process()
            process.executableURL = URL(fileURLWithPath: path)
            process.arguments = arguments
            process.currentDirectoryURL = dir
            process.standardOutput = FileHandle.nullDevice; process.standardError = FileHandle.nullDevice
            try process.run(); process.waitUntilExit()
            #expect(process.terminationStatus == 0, "\(path) \(arguments)")
        }
        for name in ["plain", "ref"] {
            try tool("/usr/bin/usdcat", ["\(name).usda", "-o", "\(name).usdc"])
            try tool("/usr/bin/usdzip", ["\(name).usdz", "\(name).usdc"])
        }
        try FileManager.default.copyItem(at: dir.appendingPathComponent("ref.usda"), to: dir.appendingPathComponent("renamed.usd"))
        let stl = Models3D.formats.first { $0.ext == "stl" }!
        for name in ["ref.usdc", "ref.usdz", "renamed.usd"] {
            await #expect(throws: ToolError.self, "\(name)") { try await Models3D.convert(dir.appendingPathComponent(name), to: stl) }
        }
        for name in ["plain.usdc", "plain.usdz"] {
            #expect(try await Models3D.convert(dir.appendingPathComponent(name), to: stl).count == 1, "\(name)")
        }
        // Swivel's own USDZ (SceneKit writes a binary layer) reads back.
        let ours = try await Models3D.convert(dir.appendingPathComponent("plain.usda"), to: Models3D.formats.first { $0.ext == "usdz" }!)[0]
        #expect(try await Models3D.convert(ours, to: stl).count == 1)
    }

    @Test func tablesKeepQuotesInTSVLineBreaksAndCombiningMarks() throws {
        #expect(try DataFormats.table("a\tb\n1\t\"Hi\" she said\n", separator: "\t") == [["a", "b"], ["1", "\"Hi\" she said"]])
        #expect(try DataFormats.table("a,b,c\n1,\u{301}x,3\n", separator: ",") == [["a", "b", "c"], ["1", "\u{301}x", "3"]])
        #expect(try DataFormats.table("a,b\r\n\"line\r\nbreak\",2\r\n", separator: ",") == [["a", "b"], ["line\r\nbreak", "2"]])
        #expect(throws: ToolError.self) { try DataFormats.write(.table([["a"], ["x\ty"]]), as: "tsv") }
        let yaml = String(decoding: try DataFormats.write(.tree(["\u{90}\u{85}\u{2028}\u{FFFE}"]), as: "yaml"), as: UTF8.self)
        #expect(yaml == "---\n- \"\\x90\\x85\\u2028\\uFFFE\"\n")
    }

    @Test func fontBudgetsAndVariableFonts() async throws {
        // 20 directory entries all covering one 4 MiB file add up to 80 MiB once copied out.
        var ttf = Data([0, 1, 0, 0, 0, 20, 0, 0, 0, 0, 0, 0])
        for i in 0..<20 {
            let tag: UInt32 = i == 0 ? 0x6865_6164 : 0x7400_0000 + UInt32(i)
            for v in [tag, 0, 0, UInt32(4 << 20)] { ttf.append(contentsOf: withUnsafeBytes(of: v.bigEndian, Array.init)) }
        }
        ttf.append(Data(count: (4 << 20) - ttf.count))
        #expect(throws: ToolError.self) { try FontFormats.parseSFNT(ttf) }
        // A WOFF declaring 16 × 64 MiB tables is refused before anything is inflated.
        var woff = Data([0x77, 0x4F, 0x46, 0x46, 0, 1, 0, 0])
        woff.append(Data(count: 4)); woff.append(contentsOf: [0, 16]); woff.append(Data(count: 30))
        for i in 0..<16 {
            for v in [0x7400_0000 + UInt32(i), UInt32(44 + 320), 8, UInt32(64 << 20), 0] { woff.append(contentsOf: withUnsafeBytes(of: v.bigEndian, Array.init)) }
        }
        woff.append(Data(count: 8))
        #expect(throws: ToolError.self) { try FontFormats.decodeWOFF(woff) }

        let skia = URL(fileURLWithPath: "/System/Library/Fonts/Supplemental/Skia.ttf")
        guard FileManager.default.fileExists(atPath: skia.path) else { return }
        let dir = try folder()
        defer { try? FileManager.default.removeItem(at: dir) }
        let source = dir.appendingPathComponent("Skia.ttf")
        try FileManager.default.copyItem(at: skia, to: source)
        let packed = try await FontFormats.convert(source, to: FontFormats.woff2)[0]
        let back = try await FontFormats.convert(packed, to: FontFormats.desktop)[0]
        #expect(try tables(Data(contentsOf: back)).count == tables(Data(contentsOf: source)).count)
    }

    @Test func lyricsCannotMultiplyAndKeepTheirOwnBrackets() throws {
        let stamps = (0..<1000).map { String(format: "[%02d:%02d.00]", $0 / 60, $0 % 60) }.joined()
        let bomb = stamps + String(repeating: "la ", count: 20_000) + "\n"
        #expect(throws: ToolError.self) { try SubtitleFormats.parse(bomb, ext: "lrc") }
        #expect(try SubtitleFormats.parse("[offset:500]\n[00:02.00]Hi\n", ext: "lrc").first?.start == 1.5)
        let cues = [SubtitleCue(start: 1, end: 2, text: "[00:09.00] is the time")]
        #expect(try SubtitleFormats.parse(SubtitleFormats.write(cues, as: "lrc"), ext: "lrc").map(\.start) == [1])
        let long = [SubtitleCue(start: 400_000, end: 400_001, text: "late")]
        #expect(try SubtitleFormats.parse(SubtitleFormats.write(long, as: "srt"), ext: "srt") == long)
        let korean = "1\n00:00:01,000 --> 00:00:02,000\n안녕하세요 세계\n"
        let euckr = try #require(korean.data(using: String.Encoding(rawValue: CFStringConvertEncodingToNSStringEncoding(CFStringEncoding(CFStringEncodings.EUC_KR.rawValue)))))
        #expect(try TextFile.decode(euckr) == korean)
    }

    // MARK: AirDrop

    @Test func onlyRecentSharingdQuarantineCountsAsAirDrop() {
        let start = Date(timeIntervalSince1970: 1_800_000_000)
        let hex = { (t: Double) in String(UInt64(t), radix: 16) }
        #expect(AirDropShelf.isAirDrop(quarantine: "0083;\(hex(1_800_000_010));sharingd;ABC", since: start, now: start.addingTimeInterval(20)))
        #expect(!AirDropShelf.isAirDrop(quarantine: "0083;\(hex(1_800_000_010));Safari;ABC", since: start, now: start.addingTimeInterval(20)))
        #expect(!AirDropShelf.isAirDrop(quarantine: "0083;\(hex(1_700_000_000));sharingd;ABC", since: start, now: start.addingTimeInterval(20)))
        #expect(!AirDropShelf.isAirDrop(quarantine: "garbage", since: start))
    }

    @Test func newAirDropArrivalsReachTheShelfOnceTheyStopGrowing() async throws {
        let dir = try folder()
        defer { try? FileManager.default.removeItem(at: dir) }
        func quarantined(_ name: String, agent: String) throws {
            let url = dir.appendingPathComponent(name)
            try Data("x".utf8).write(to: url)
            let value = "0083;\(String(UInt64(Date().timeIntervalSince1970), radix: 16));\(agent);1234"
            #expect(setxattr(url.path, "com.apple.quarantine", value, value.utf8.count, 0, 0) == 0)
        }
        try quarantined("old.jpg", agent: "sharingd")  // Already there before watching: not news.
        var delivered: [String] = []
        let watcher = AirDropShelf(folder: dir) { delivered += $0.map(\.lastPathComponent) }
        watcher.start()
        defer { watcher.stop() }
        let startDeadline = Date().addingTimeInterval(2)
        while !watcher.isWatching && Date() < startDeadline { try await Task.sleep(for: .milliseconds(10)) }
        #expect(watcher.isWatching)
        try quarantined("photo.heic", agent: "sharingd")
        try quarantined("page.pdf", agent: "Safari")
        for _ in 0..<40 where delivered.isEmpty { try await Task.sleep(for: .milliseconds(100)) }
        try await Task.sleep(for: .milliseconds(1500))
        #expect(delivered == ["photo.heic"])
        // Renaming the received file is not a second arrival.
        try FileManager.default.moveItem(at: dir.appendingPathComponent("photo.heic"), to: dir.appendingPathComponent("renamed.heic"))
        try await Task.sleep(for: .milliseconds(1800))
        #expect(delivered == ["photo.heic"])
    }
}
