import AppKit
import Darwin
import Metal
import ModelIO
import SceneKit
import SceneKit.ModelIO

/// 3D models ↔ each other through Model I/O, USDZ and a preview picture through SceneKit.
/// Only geometry travels: materials and textures are dropped, so nothing a model points at (an image anywhere on
/// the Mac, named by an absolute path) can be packed into a USDZ.
enum Models3D {
    struct Format { let name: String; let ext: String }
    static let inputExtensions: Set<String> = ["obj", "stl", "ply", "usd", "usda", "usdc", "usdz", "abc"]
    static let formats: [Format] = [
        Format(name: "USDZ", ext: "usdz"), Format(name: "OBJ", ext: "obj"), Format(name: "STL", ext: "stl"),
        Format(name: "PLY", ext: "ply"), Format(name: "USDA", ext: "usda"), Format(name: "Preview", ext: "png"),
    ]
    static let maximumBytes: Int64 = 512 * 1024 * 1024
    static let maximumVertices = 50_000_000
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
                    guard SCNScene(mdlAsset: asset).write(to: out, options: nil, delegate: nil, progressHandler: nil) else {
                        throw ToolError(L("Couldn't write the 3D model."))
                    }
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
        guard MDLAsset.canImportFileExtension(url.pathExtension.lowercased()) else { throw ToolError(L("This 3D format can't be read on this Mac.")) }
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

    static func meshes(in asset: MDLAsset) throws -> [MDLMesh] {
        var found: [MDLMesh] = []
        func walk(_ object: MDLObject, depth: Int) {
            guard depth < 256 else { return }
            if let mesh = object as? MDLMesh { found.append(mesh) }
            for child in object.children.objects { walk(child, depth: depth + 1) }
        }
        for index in 0..<asset.count { walk(asset.object(at: index), depth: 0) }
        return found
    }

    /// A transparent PNG from a three-quarter view above the model, lit by SceneKit's default light.
    static func preview(_ asset: MDLAsset) throws -> Data {
        guard let device = MTLCreateSystemDefaultDevice() else { throw ToolError(L("This Mac can't draw 3D previews.")) }
        let scene = SCNScene(mdlAsset: asset)
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
