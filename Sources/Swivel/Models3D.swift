import AppKit
import Compression
import Darwin
import Metal
import ModelIO
import SceneKit
import SceneKit.ModelIO

/// 3D models ↔ each other through Model I/O, USDZ and a preview picture through SceneKit.
/// Only geometry travels: materials, textures and lights are dropped, so nothing a model points at (any file on the
/// Mac, named by an absolute path) can be packed into a USDZ; a USDZ holding more than its one layer is refused.
enum Models3D {
    struct Format { let name: String; let ext: String }
    static let inputExtensions: Set<String> = ["obj", "stl", "ply", "usd", "usda", "usdc", "usdz", "abc"]
    static let formats: [Format] = [
        Format(name: "USDZ", ext: "usdz"), Format(name: "OBJ", ext: "obj"), Format(name: "STL", ext: "stl"),
        Format(name: "PLY", ext: "ply"), Format(name: "USDA", ext: "usda"), Format(name: "Preview", ext: "png"),
    ]
    static let maximumBytes: Int64 = 512 * 1024 * 1024
    static let maximumVertices = 50_000_000
    static let maximumObjects = 1_000_000
    /// Text USD is scanned for composition before loading; it is read whole, so it gets a smaller budget.
    static let maximumTextUSDBytes = 64 * 1024 * 1024
    static let previewSize = 1600

    static func items(sourceExt: Set<String>) -> [FileOption] {
        formats.filter { sourceExt != [$0.ext] }.map { format in
            FileOption(title: format.name) { url in try await convert(url, to: format) }
        }
    }

    static func convert(_ url: URL, to format: Format) async throws -> [URL] {
        try await Images.detached {
            let asset = try load(url)
            let preferred = Paths.preferred(for: url, ext: format.ext, suffix: format.ext == "png" ? " preview" : "")
            return [try Paths.produce(preferred) { out in
                switch format.ext {
                case "png": try FileSafety.write(try preview(asset), to: out)
                case "usdz":
                    guard try scene(asset).write(to: out, options: nil, delegate: nil, progressHandler: nil), zipEntries(out) == 1 else {
                        throw ToolError(L("Couldn't write the 3D model."))
                    }
                case "obj":
                    // Model I/O names a material library after the temporary file; nothing uses it, and a stray
                    // file of that name next to the result would be picked up by viewers.
                    let raw = out.deletingLastPathComponent().appendingPathComponent("model.obj")
                    do { try asset.export(to: raw) } catch { throw ToolError(L("Couldn't write the 3D model.")) }
                    try copyLines(from: raw, to: out) { !$0.starts(with: Array("mtllib".utf8)) }
                default:
                    do { try asset.export(to: out) } catch { throw ToolError(L("Couldn't write the 3D model.")) }
                }
                if format.ext != "png" {
                    guard (try? meshes(in: MDLAsset(url: out)))?.isEmpty == false else { throw ToolError(L("Couldn't write the 3D model.")) }
                }
            }]
        }
    }

    static func load(_ url: URL) throws -> MDLAsset {
        var info = stat()
        guard lstat(url.path, &info) == 0, info.st_mode & S_IFMT == S_IFREG else { throw ToolError(L("Choose a regular local file.")) }
        guard Int64(info.st_size) <= maximumBytes else { throw ToolError(L("This file exceeds the safe reading limit.")) }
        let ext = url.pathExtension.lowercased()
        guard MDLAsset.canImportFileExtension(ext) else { throw ToolError(L("This 3D format can't be read on this Mac.")) }
        // A few lines of references, payloads or sublayers can expand into billions of vertices while loading,
        // before any limit can be checked. USD that uses them is refused, judged by content, not by extension.
        if ["usd", "usda", "usdc", "usdz"].contains(ext) { try USDComposition.refuse(url, package: ext == "usdz") }
        var error: NSError?
        let asset = MDLAsset(url: url, vertexDescriptor: nil, bufferAllocator: nil, preserveTopology: false, error: &error)
        guard error == nil else { throw ToolError(L("Couldn't read this 3D model.")) }
        let found = try meshes(in: asset)
        guard !found.isEmpty else { throw ToolError(L("This file contains no 3D geometry.")) }
        guard found.reduce(0, { $0 + $1.vertexCount }) <= maximumVertices else { throw ToolError(L("This 3D model is too large.")) }
        for mesh in found {
            for case let submesh as MDLSubmesh in mesh.submeshes ?? [] { submesh.material = nil }
        }
        return asset
    }

    /// Every mesh, however deeply nested: one left out would keep its material.
    static func meshes(in asset: MDLAsset) throws -> [MDLMesh] {
        var found: [MDLMesh] = [], visited = 0
        var stack: [MDLObject] = (0..<asset.count).map { asset.object(at: $0) }
        while let object = stack.popLast() {
            visited += 1
            guard visited <= maximumObjects else { throw ToolError(L("This 3D model is too large.")) }
            if let mesh = object as? MDLMesh { found.append(mesh) }
            stack += object.children.objects
        }
        return found
    }

    /// The SceneKit scene for USDZ and the preview, with plain materials and no lights, cameras or environment, so
    /// it refers to no file at all.
    static func scene(_ asset: MDLAsset) throws -> SCNScene {
        let scene = SCNScene(mdlAsset: asset)
        var visited = 0
        var failure: Error?
        scene.rootNode.enumerateHierarchy { node, stop in
            visited += 1
            if visited > maximumObjects { failure = ToolError(L("This 3D model is too large.")); stop.pointee = true; return }
            node.geometry?.materials = [SCNMaterial()]
            node.light = nil
            node.camera = nil
        }
        if let failure { throw failure }
        scene.background.contents = nil
        scene.lightingEnvironment.contents = nil
        return scene
    }

    /// The entry count from a ZIP's end-of-central-directory record; nil when there is none.
    static func zipEntries(_ url: URL) -> Int? {
        guard let handle = try? FileHandle(forReadingFrom: url) else { return nil }
        defer { try? handle.close() }
        guard let size = try? handle.seekToEnd(), size >= 22 else { return nil }
        let length = min(size, 22 + 65_535)
        try? handle.seek(toOffset: size - length)
        guard let tail = try? handle.read(upToCount: Int(length)), tail.count >= 22 else { return nil }
        let bytes = [UInt8](tail)
        for at in stride(from: bytes.count - 22, through: 0, by: -1)
        where bytes[at] == 0x50 && bytes[at + 1] == 0x4B && bytes[at + 2] == 0x05 && bytes[at + 3] == 0x06 {
            return Int(bytes[at + 10]) | Int(bytes[at + 11]) << 8
        }
        return nil
    }

    /// Copies a text file line by line (LF), keeping only the lines `keep` accepts.
    static func copyLines(from source: URL, to target: URL, keep: (ArraySlice<UInt8>) -> Bool) throws {
        let input = try FileHandle(forReadingFrom: source)
        defer { try? input.close() }
        try FileSafety.write(Data(), to: target)
        let output = try FileHandle(forWritingTo: target)
        defer { try? output.close() }
        var carry: [UInt8] = []
        func flush(_ line: ArraySlice<UInt8>) throws { if keep(line) { try output.write(contentsOf: line) } }
        while let chunk = try input.read(upToCount: 1 << 20), !chunk.isEmpty {
            try Task.checkCancellation()
            carry += chunk
            var start = carry.startIndex
            while let newline = carry[start...].firstIndex(of: 0x0A) {
                try flush(carry[start...newline]); start = newline + 1
            }
            carry.removeSubrange(..<start)
        }
        if !carry.isEmpty { try flush(carry[...]) }
    }

    /// A transparent PNG from a three-quarter view above the model, lit by SceneKit's default light.
    static func preview(_ asset: MDLAsset) throws -> Data {
        guard let device = MTLCreateSystemDefaultDevice() else { throw ToolError(L("This Mac can't draw 3D previews.")) }
        let scene = try scene(asset)
        let box = asset.boundingBox
        let center = SCNVector3((box.minBounds + box.maxBounds) / 2)
        let radius = simd_length(box.maxBounds - box.minBounds) / 2
        guard radius.isFinite, radius > 0 else { throw ToolError(L("This file contains no 3D geometry.")) }
        let camera = SCNCamera()
        camera.fieldOfView = 30
        camera.zNear = Double(radius) * 0.01
        camera.zFar = Double(radius) * 20
        let eye = SCNNode()
        eye.camera = camera
        // Far enough that the bounding sphere fits the 30° field of view.
        let distance: CGFloat = CGFloat(radius) / CGFloat(sin(Double.pi / 12)) * 1.1
        let x: CGFloat = CGFloat(center.x) + 0.6 * distance
        let y: CGFloat = CGFloat(center.y) + 0.45 * distance
        let z: CGFloat = CGFloat(center.z) + 0.66 * distance
        eye.position = SCNVector3(x, y, z)
        eye.look(at: center)
        scene.rootNode.addChildNode(eye)
        scene.background.contents = NSColor.clear
        let renderer = SCNRenderer(device: device, options: nil)
        renderer.scene = scene
        renderer.pointOfView = eye
        renderer.autoenablesDefaultLighting = true
        let image = renderer.snapshot(atTime: 0, with: CGSize(width: previewSize, height: previewSize), antialiasingMode: .multisampling4X)
        guard let cg = image.cgImage(forProposedRect: nil, context: nil, hints: nil),
              let png = NSBitmapImageRep(cgImage: cg).representation(using: .png, properties: [:]) else {
            throw ToolError(L("This Mac can't draw 3D previews."))
        }
        return png
    }
}

/// Finds USD composition arcs (references, payloads, sublayers, inherits, specializes) without loading the stage.
/// Text layers are searched as text; binary "crate" layers by their token table; a USDZ by every layer inside it.
/// A layer that can't be read this way is refused rather than loaded.
enum USDComposition {
    static let textArcs = #/\b(references|payload|subLayers|inherits|specializes)\b/#
    static let crateArcs: Set<String> = ["references", "payload", "subLayers", "inheritPaths", "specializes"]
    static let maximumTokenBytes = 64 * 1024 * 1024

    static func refuse(_ url: URL, package: Bool) throws {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        let size = try handle.seekToEnd()
        if package {
            let layers = try zipLayers(handle, size: size)
            guard !layers.isEmpty else { throw unsupported }
            for layer in layers { try check(handle, base: layer.offset, length: layer.length) }
        } else {
            try check(handle, base: 0, length: size)
        }
    }

    static var unsupported: ToolError { ToolError(L("USD files that reference other layers or prims aren't supported.")) }

    private static func read(_ handle: FileHandle, _ offset: UInt64, _ count: Int) throws -> Data {
        try handle.seek(toOffset: offset)
        guard let data = try handle.read(upToCount: count), data.count == count else { throw unsupported }
        return data
    }

    private static func le(_ data: Data, _ at: Int, _ bytes: Int) -> UInt64 {
        (0..<bytes).reduce(0) { $0 | UInt64(data[data.startIndex + at + $1]) << (8 * UInt64($1)) }
    }

    static func check(_ handle: FileHandle, base: UInt64, length: UInt64) throws {
        guard length >= 8 else { throw unsupported }
        let head = try read(handle, base, 8)
        if head.starts(with: Array("#usda".utf8)) {
            guard length <= UInt64(Models3D.maximumTextUSDBytes) else { throw ToolError(L("This 3D model is too large.")) }
            if String(decoding: try read(handle, base, Int(length)), as: UTF8.self).firstMatch(of: textArcs) != nil { throw unsupported }
        } else if head == Data("PXR-USDC".utf8) {
            guard try crateTokens(handle, base: base, length: length).isDisjoint(with: crateArcs) else { throw unsupported }
        } else {
            throw unsupported
        }
    }

    /// Crate layout: "PXR-USDC", version bytes, table-of-contents offset; the TOKENS section holds every field
    /// name, LZ4-compressed from version 0.4.
    static func crateTokens(_ handle: FileHandle, base: UInt64, length: UInt64) throws -> Set<String> {
        guard length >= 24 else { throw unsupported }
        let boot = try read(handle, base, 24)
        let major = boot[boot.startIndex + 8], minor = boot[boot.startIndex + 9]
        let toc = le(boot, 16, 8)
        guard toc <= length - 8 else { throw unsupported }
        let count = le(try read(handle, base + toc, 8), 0, 8)
        guard count > 0, count <= 64, toc + 8 + count * 32 <= length else { throw unsupported }
        let table = try read(handle, base + toc + 8, Int(count * 32))
        for index in 0..<Int(count) {
            let at = index * 32
            let name = String(decoding: table[(table.startIndex + at)..<(table.startIndex + at + 16)].prefix { $0 != 0 }, as: UTF8.self)
            guard name == "TOKENS" else { continue }
            let start = le(table, at + 16, 8), size = le(table, at + 24, 8)
            guard start <= length, size <= length - start, size >= 16, size <= UInt64(maximumTokenBytes) else { throw unsupported }
            let section = try read(handle, base + start, Int(size))
            var chars: Data
            if major == 0 && minor < 4 {
                let bytes = Int(le(section, 8, 8))
                guard bytes <= section.count - 16 else { throw unsupported }
                chars = section[(section.startIndex + 16)..<(section.startIndex + 16 + bytes)]
            } else {
                guard section.count >= 24 else { throw unsupported }
                let expanded = Int(le(section, 8, 8)), packed = Int(le(section, 16, 8))
                guard expanded <= maximumTokenBytes, packed >= 1, packed <= section.count - 24 else { throw unsupported }
                chars = try fastDecompress(section[(section.startIndex + 24)..<(section.startIndex + 24 + packed)], expanded: expanded)
            }
            return Set(chars.split(separator: 0).map { String(decoding: $0, as: UTF8.self) })
        }
        throw unsupported
    }

    /// USD's TfFastCompression: a chunk count, then one raw LZ4 block (count 0) or that many size-prefixed blocks.
    private static func fastDecompress(_ data: Data, expanded: Int) throws -> Data {
        func block(_ chunk: Data, capacity: Int) throws -> Data {
            guard capacity > 0, !chunk.isEmpty else { throw unsupported }
            var out = [UInt8](repeating: 0, count: capacity)
            let n = chunk.withUnsafeBytes { compression_decode_buffer(&out, capacity, $0.bindMemory(to: UInt8.self).baseAddress!, chunk.count, nil, COMPRESSION_LZ4_RAW) }
            guard n > 0 else { throw unsupported }
            return Data(out.prefix(n))
        }
        let chunks = Int(data[data.startIndex])
        if chunks == 0 { return try block(data.dropFirst(), capacity: expanded) }
        var out = Data(), at = 1
        for _ in 0..<chunks {
            guard at + 4 <= data.count else { throw unsupported }
            let size = Int(le(data, at, 4)); at += 4
            guard size > 0, size <= data.count - at else { throw unsupported }
            out += try block(data[(data.startIndex + at)..<(data.startIndex + at + size)], capacity: expanded - out.count)
            at += size
        }
        return out
    }

    /// The stored USD layers of a USDZ (a ZIP whose members are never compressed). A nested package, a
    /// compressed layer or a damaged directory makes the whole file unsupported.
    static func zipLayers(_ handle: FileHandle, size: UInt64) throws -> [(offset: UInt64, length: UInt64)] {
        guard size >= 22 else { throw unsupported }
        let tailLength = min(size, 22 + 65_535)
        let tail = try read(handle, size - tailLength, Int(tailLength))
        guard let end = stride(from: tail.count - 22, through: 0, by: -1).first(where: { le(tail, $0, 4) == 0x0605_4B50 }) else { throw unsupported }
        let entries = Int(le(tail, end + 10, 2)), directorySize = le(tail, end + 12, 4), directoryStart = le(tail, end + 16, 4)
        guard entries > 0, entries < 0xFFFF, directoryStart <= size, directorySize <= size - directoryStart, directorySize <= 16 << 20 else { throw unsupported }
        let directory = try read(handle, directoryStart, Int(directorySize))
        var layers: [(UInt64, UInt64)] = [], at = 0
        for _ in 0..<entries {
            guard at + 46 <= directory.count, le(directory, at, 4) == 0x0201_4B50 else { throw unsupported }
            let method = le(directory, at + 10, 2), stored = le(directory, at + 20, 4)
            let nameLength = Int(le(directory, at + 28, 2)), extra = Int(le(directory, at + 30, 2)), comment = Int(le(directory, at + 32, 2))
            let local = le(directory, at + 42, 4)
            guard at + 46 + nameLength <= directory.count else { throw unsupported }
            let name = String(decoding: directory[(directory.startIndex + at + 46)..<(directory.startIndex + at + 46 + nameLength)], as: UTF8.self).lowercased()
            at += 46 + nameLength + extra + comment
            let ext = (name as NSString).pathExtension
            if ext == "usdz" { throw unsupported }
            guard ["usd", "usda", "usdc"].contains(ext) else { continue }
            guard method == 0, local <= size - 30 else { throw unsupported }
            let header = try read(handle, local, 30)
            guard le(header, 0, 4) == 0x0403_4B50 else { throw unsupported }
            let offset = local + 30 + le(header, 26, 2) + le(header, 28, 2)
            guard offset <= size, stored <= size - offset else { throw unsupported }
            layers.append((offset, stored))
        }
        return layers
    }
}
