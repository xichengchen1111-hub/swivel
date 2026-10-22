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
    ]

    static func convert(_ url: URL, to format: ImageFormat) async throws -> [URL] {
        try await detached {
            [try save(try load(url), as: Paths.preferred(for: url, ext: format.ext), type: format.type, quality: 0.92)]
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
        return [try PDFs.save(doc, as: Paths.preferred(for: urls[0], ext: "pdf"))]
    }

    // MARK: Helpers

    static func detached<T: Sendable>(_ work: @escaping @Sendable () throws -> T) async throws -> T {
        try await Task.detached(priority: .userInitiated, operation: work).value
    }

    /// Full-size decodes above this many pixels are refused (about 300 MP, 1.2 GB of RGBA).
    static let maxDecodePixels = 300_000_000.0

    /// Decodes the image upright (EXIF orientation applied), optionally capped in size.
    static func load(_ url: URL, maxPixels: Int? = nil) throws -> CGImage {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil) else {
            throw ToolError("Couldn't read \(url.lastPathComponent)")
        }
        let props = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any] ?? [:]
        let width = props[kCGImagePropertyPixelWidth] as? Int ?? 4096
        let height = props[kCGImagePropertyPixelHeight] as? Int ?? 4096
        // A full-size decode of a crafted "pixel flood" file would claim many gigabytes.
        if maxPixels == nil, Double(width) * Double(height) > maxDecodePixels {
            throw ToolError("\(url.lastPathComponent) is too large to open (\(width) × \(height) px)")
        }
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

    static func encode(_ image: CGImage, type: UTType, quality: Double) throws -> Data {
        var image = image
        if [UTType.jpeg, .bmp].contains(type), image.hasAlpha { image = image.flattened() }
        let data = NSMutableData()
        guard let dest = CGImageDestinationCreateWithData(data, type.identifier as CFString, 1, nil) else {
            throw ToolError("Can't write \(type.preferredFilenameExtension ?? "this format")")
        }
        CGImageDestinationAddImage(dest, image, [kCGImageDestinationLossyCompressionQuality: quality] as CFDictionary)
        guard CGImageDestinationFinalize(dest) else { throw ToolError("Couldn't encode image") }
        return data as Data
    }

    /// Highest fitting quality at each resolution; never returns an oversized result.
    static func fitting(_ image: CGImage, type: UTType, maxBytes: Int) throws
        -> (data: Data, quality: Double, longEdge: Int) {
        let originalEdge = max(image.width, image.height)
        let floor = min(320, originalEdge)
        let lossy = type == .jpeg || type == .heic
        var edge = originalEdge
        var smallest = Int.max
        while true {
            try Task.checkCancellation()
            let scaled = edge == originalEdge ? image : try resized(image, longEdge: edge)
            if lossy {
                let lowData = try encode(scaled, type: type, quality: 0.05)
                smallest = min(smallest, lowData.count)
                if lowData.count <= maxBytes {
                    let highData = try encode(scaled, type: type, quality: 0.95)
                    if highData.count <= maxBytes { return (highData, 0.95, edge) }
                    var low = 0.05, high = 0.95, best = lowData
                    for _ in 0..<7 {
                        try Task.checkCancellation()
                        let mid = (low + high) / 2
                        let data = try encode(scaled, type: type, quality: mid)
                        if data.count <= maxBytes { low = mid; best = data } else { high = mid }
                    }
                    return (best, low, edge)
                }
            } else {
                let data = try encode(scaled, type: type, quality: 1)
                smallest = min(smallest, data.count)
                if data.count <= maxBytes { return (data, 1, edge) }
            }
            if edge == floor { break }
            edge = max(floor, Int(Double(edge) * 0.85))
        }
        throw ToolError(L("Target size cannot be reached. Smallest size: %@.", byteString(smallest)))
    }

    static func resized(_ image: CGImage, longEdge: Int) throws -> CGImage {
        let scale = Double(longEdge) / Double(max(image.width, image.height))
        let width = max(1, Int((Double(image.width) * scale).rounded()))
        let height = max(1, Int((Double(image.height) * scale).rounded()))
        guard let context = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8,
                                      bytesPerRow: 0, space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                      bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
            throw ToolError(L("Couldn't encode image"))
        }
        context.interpolationQuality = .high
        context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
        guard let result = context.makeImage() else { throw ToolError(L("Couldn't encode image")) }
        return result
    }

    /// Encodes `image` and saves it as a new file at `preferred` or the next free name; returns where it went.
    /// (ImageIO writing to a URL would replace a file another conversion saved there after the name was chosen.)
    static func save(_ image: CGImage, as preferred: URL, type: UTType, quality: Double) throws -> URL {
        try Paths.create(try encode(image, type: type, quality: quality), at: preferred)
    }

    private static func sourceType(_ url: URL) -> UTType {
        UTType(filenameExtension: url.pathExtension) ?? .png
    }

    /// The source's own format if ImageIO can write it, else PNG.
    static func writableType(for url: URL) -> (UTType, String) {
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
        return [try save(image, as: Paths.preferred(for: url, ext: ext, suffix: suffix), type: type, quality: 0.9)]
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
        return [try save(image, as: Paths.preferred(for: url, ext: outExt, suffix: " compressed"), type: type, quality: 0.6)]
    }

    private static func stripMetadata(_ url: URL) throws -> [URL] {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil),
              let type = CGImageSourceGetType(source) else {
            throw ToolError("Couldn't read \(url.lastPathComponent)")
        }
        let data = NSMutableData()
        guard let dest = CGImageDestinationCreateWithData(data, type, 1, nil) else {
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
        return [try Paths.create(data as Data, at: Paths.preferred(for: url, ext: url.pathExtension, suffix: " clean"))]
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
