import AppKit
import ImageIO
import PDFKit
import UniformTypeIdentifiers

struct ImageFormat {
    let name: String
    let type: UTType
    let ext: String
    var extensions: [String] { type.tags[.filenameExtension] ?? [ext] }
}

enum Images {
    static let outputFormats: [ImageFormat] = {
        let writable = Set(CGImageDestinationCopyTypeIdentifiers() as? [String] ?? [])
        var list = [
            ImageFormat(name: "JPG", type: .jpeg, ext: "jpg"),
            ImageFormat(name: "PNG", type: .png, ext: "png"),
            ImageFormat(name: "HEIC", type: .heic, ext: "heic"),
            ImageFormat(name: "WEBP", type: .webP, ext: "webp"),
            ImageFormat(name: "TIFF", type: .tiff, ext: "tiff"),
            ImageFormat(name: "AVIF", type: UTType("public.avif") ?? .image, ext: "avif"),
            ImageFormat(name: "GIF", type: .gif, ext: "gif"),
            ImageFormat(name: "BMP", type: .bmp, ext: "bmp"),
        ]
        list.removeAll { !writable.contains($0.type.identifier) }
        return list
    }()

    static let toolItems: [FileOption] = [
        FileOption(title: "Compress") { url in try await detached { try compress(url) } },
        FileOption(title: "Resize 50%") { url in
            try await detached { try transform(url, suffix: " small", scale: 0.5) }
        },
        FileOption(title: "Rotate") { url in
            try await detached { try transform(url, suffix: " rotated") { $0.rotatedRight() } }
        },
        FileOption(title: "Flip") { url in
            try await detached { try transform(url, suffix: " flipped") { $0.flippedHorizontally() } }
        },
        FileOption(title: "Grayscale") { url in
            try await detached { try transform(url, suffix: " gray") { $0.grayscale() } }
        },
        FileOption(title: "Strip Metadata") { url in try await detached { try stripMetadata(url) } },
    ]

    static func convert(_ url: URL, to format: ImageFormat) async throws -> [URL] {
        try await detached {
            let image = try load(url)
            let out = Paths.output(for: url, ext: format.ext)
            try write(image, to: out, type: format.type, quality: 0.92)
            return [out]
        }
    }

    static func makePDF(_ urls: [URL]) async throws -> [URL] {
        let doc = PDFDocument()
        for url in urls {
            guard let image = NSImage(contentsOf: url), let page = PDFPage(image: image) else {
                throw ToolError("Couldn't read \(url.lastPathComponent)")
            }
            doc.insert(page, at: doc.pageCount)
        }
        let out = Paths.output(for: urls[0], ext: "pdf")
        guard doc.write(to: out) else { throw ToolError("Couldn't write PDF") }
        return [out]
    }

    // MARK: Helpers

    static func detached<T: Sendable>(_ work: @escaping @Sendable () throws -> T) async throws -> T {
        try await Task.detached(priority: .userInitiated, operation: work).value
    }

    /// Decodes the image upright (EXIF orientation applied), optionally capped in size.
    static func load(_ url: URL, maxPixels: Int? = nil) throws -> CGImage {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil) else {
            throw ToolError("Couldn't read \(url.lastPathComponent)")
        }
        let props = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any] ?? [:]
        let width = props[kCGImagePropertyPixelWidth] as? Int ?? 4096
        let height = props[kCGImagePropertyPixelHeight] as? Int ?? 4096
        let options: [CFString: Any] = [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceShouldCacheImmediately: true,
            kCGImageSourceThumbnailMaxPixelSize: maxPixels ?? max(width, height),
        ]
        guard let image = CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary) else {
            throw ToolError("Couldn't decode \(url.lastPathComponent)")
        }
        return image
    }

    static func write(_ image: CGImage, to url: URL, type: UTType, quality: Double) throws {
        var image = image
        if [UTType.jpeg, .bmp].contains(type), image.hasAlpha { image = image.flattened() }
        guard let dest = CGImageDestinationCreateWithURL(url as CFURL, type.identifier as CFString, 1, nil) else {
            throw ToolError("Can't write \(type.preferredFilenameExtension ?? "this format")")
        }
        CGImageDestinationAddImage(dest, image, [kCGImageDestinationLossyCompressionQuality: quality] as CFDictionary)
        guard CGImageDestinationFinalize(dest) else { throw ToolError("Couldn't save \(url.lastPathComponent)") }
    }

    private static func sourceType(_ url: URL) -> UTType {
        UTType(filenameExtension: url.pathExtension) ?? .png
    }

    private static func writableType(for url: URL) -> (UTType, String) {
        let type = sourceType(url)
        let writable = CGImageDestinationCopyTypeIdentifiers() as? [String] ?? []
        return writable.contains(type.identifier) ? (type, url.pathExtension) : (.png, "png")
    }

    private static func transform(_ url: URL, suffix: String, scale: CGFloat = 1,
                                  _ change: (CGImage) throws -> CGImage = { $0 }) throws -> [URL] {
        var maxPixels: Int?
        if scale != 1 {
            let full = try load(url)
            maxPixels = Int(CGFloat(max(full.width, full.height)) * scale)
        }
        let image = try change(try load(url, maxPixels: maxPixels))
        let (type, ext) = writableType(for: url)
        let out = Paths.output(for: url, ext: ext, suffix: suffix)
        try write(image, to: out, type: type, quality: 0.9)
        return [out]
    }

    private static func compress(_ url: URL) throws -> [URL] {
        let image = try load(url)
        let lossy: Set<String> = ["jpg", "jpeg", "heic", "heif", "webp", "avif"]
        let ext = url.pathExtension.lowercased()
        let (type, outExt): (UTType, String)
        if lossy.contains(ext), Images.outputFormats.contains(where: { $0.extensions.contains(ext) }) {
            (type, outExt) = (sourceType(url), url.pathExtension)
        } else if image.hasAlpha {
            (type, outExt) = (.heic, "heic")
        } else {
            (type, outExt) = (.jpeg, "jpg")
        }
        let out = Paths.output(for: url, ext: outExt, suffix: " compressed")
        try write(image, to: out, type: type, quality: 0.6)
        return [out]
    }

    private static func stripMetadata(_ url: URL) throws -> [URL] {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil),
              let type = CGImageSourceGetType(source) else {
            throw ToolError("Couldn't read \(url.lastPathComponent)")
        }
        let out = Paths.output(for: url, ext: url.pathExtension, suffix: " clean")
        guard let dest = CGImageDestinationCreateWithURL(out as CFURL, type, 1, nil) else {
            throw ToolError("Can't write this format")
        }
        let removed: [CFString: Any] = [
            kCGImagePropertyExifDictionary: kCFNull!,
            kCGImagePropertyGPSDictionary: kCFNull!,
            kCGImagePropertyIPTCDictionary: kCFNull!,
            kCGImagePropertyTIFFDictionary: kCFNull!,
            kCGImagePropertyMakerAppleDictionary: kCFNull!,
            kCGImageDestinationLossyCompressionQuality: 0.95,
        ]
        CGImageDestinationAddImageFromSource(dest, source, 0, removed as CFDictionary)
        guard CGImageDestinationFinalize(dest) else { throw ToolError("Couldn't save") }
        return [out]
    }
}

extension CGImage {
    var hasAlpha: Bool {
        ![.none, .noneSkipFirst, .noneSkipLast].contains(alphaInfo)
    }

    private func redraw(width: Int, height: Int, space: CGColorSpace? = nil, background: CGColor? = nil,
                        bitmapInfo: UInt32? = nil, _ draw: (CGContext) -> Void) -> CGImage {
        let space = space ?? CGColorSpace(name: CGColorSpace.sRGB)!
        let info = bitmapInfo ?? CGImageAlphaInfo.premultipliedLast.rawValue
        guard let ctx = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: space, bitmapInfo: info) else { return self }
        if let background {
            ctx.setFillColor(background)
            ctx.fill(CGRect(x: 0, y: 0, width: width, height: height))
        }
        ctx.interpolationQuality = .high
        draw(ctx)
        return ctx.makeImage() ?? self
    }

    func flattened() -> CGImage {
        redraw(width: width, height: height, background: CGColor(gray: 1, alpha: 1)) {
            $0.draw(self, in: CGRect(x: 0, y: 0, width: width, height: height))
        }
    }

    func rotatedRight() -> CGImage {
        redraw(width: height, height: width) { ctx in
            ctx.translateBy(x: 0, y: CGFloat(width))
            ctx.rotate(by: -.pi / 2)
            ctx.draw(self, in: CGRect(x: 0, y: 0, width: width, height: height))
        }
    }

    func flippedHorizontally() -> CGImage {
        redraw(width: width, height: height) { ctx in
            ctx.translateBy(x: CGFloat(width), y: 0)
            ctx.scaleBy(x: -1, y: 1)
            ctx.draw(self, in: CGRect(x: 0, y: 0, width: width, height: height))
        }
    }

    func grayscale() -> CGImage {
        redraw(width: width, height: height, space: CGColorSpaceCreateDeviceGray(),
               background: CGColor(gray: 1, alpha: 1), bitmapInfo: CGImageAlphaInfo.none.rawValue) {
            $0.draw(self, in: CGRect(x: 0, y: 0, width: width, height: height))
        }
    }
}
