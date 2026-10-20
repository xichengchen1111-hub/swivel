import AppKit
import PDFKit
import UniformTypeIdentifiers

struct DocFormat {
    let name: String
    let ext: String
    let type: NSAttributedString.DocumentType?  // nil = PDF
}

enum Docs {
    static let readableExtensions: Set<String> = ["docx", "doc", "rtf", "rtfd", "odt", "txt", "text", "html", "htm",
                                                  "md", "markdown", "webarchive"]

    static let formats: [DocFormat] = [
        DocFormat(name: "DOCX", ext: "docx", type: .officeOpenXML),
        DocFormat(name: "PDF", ext: "pdf", type: nil),
        DocFormat(name: "RTF", ext: "rtf", type: .rtf),
        DocFormat(name: "TXT", ext: "txt", type: .plain),
        DocFormat(name: "HTML", ext: "html", type: .html),
        DocFormat(name: "ODT", ext: "odt", type: .openDocument),
        DocFormat(name: "DOC", ext: "doc", type: .docFormat),
    ]

    @MainActor
    static func convert(_ url: URL, to format: DocFormat) async throws -> [URL] {
        let text = try read(url)
        let out = Paths.output(for: url, ext: format.ext)
        try write(text, to: out, as: format)
        return [out]
    }

    @MainActor
    static func wordCount(_ url: URL) async throws -> [URL] {
        let string = try read(url).string
        let words = string.split { $0.isWhitespace || $0.isNewline }.count
        throw Info("\(url.lastPathComponent): \(words) words, \(string.count) characters")
    }

    @MainActor
    static func read(_ url: URL) throws -> NSAttributedString {
        let ext = url.pathExtension.lowercased()
        if ext == "md" || ext == "markdown" {
            let source = try String(contentsOf: url, encoding: .utf8)
            let options = AttributedString.MarkdownParsingOptions(interpretedSyntax: .inlineOnlyPreservingWhitespace)
            return NSAttributedString(try AttributedString(markdown: source, options: options))
        }
        return try NSAttributedString(url: url, options: [:], documentAttributes: nil)
    }

    @MainActor
    static func write(_ text: NSAttributedString, to out: URL, as format: DocFormat) throws {
        guard let type = format.type else { return try writePDF(text, to: out) }
        var attrs: [NSAttributedString.DocumentAttributeKey: Any] = [.documentType: type]
        if type == .plain || type == .html { attrs[.characterEncoding] = String.Encoding.utf8.rawValue }
        let data = try text.data(from: NSRange(location: 0, length: text.length), documentAttributes: attrs)
        try data.write(to: out)
    }

    /// Lays the text out on US Letter pages and prints it to a PDF file.
    @MainActor
    static func writePDF(_ text: NSAttributedString, to out: URL) throws {
        let info = NSPrintInfo()
        info.paperSize = NSSize(width: 612, height: 792)
        for edge in [\NSPrintInfo.topMargin, \.bottomMargin, \.leftMargin, \.rightMargin] { info[keyPath: edge] = 54 }
        info.horizontalPagination = .fit
        info.verticalPagination = .automatic
        info.jobDisposition = .save
        info.dictionary()[NSPrintInfo.AttributeKey.jobSavingURL] = out

        let width = info.paperSize.width - info.leftMargin - info.rightMargin
        let view = NSTextView(frame: NSRect(x: 0, y: 0, width: width, height: 1))
        view.appearance = NSAppearance(named: .aqua)
        view.textStorage?.setAttributedString(text)
        view.isVerticallyResizable = true
        view.textContainer?.widthTracksTextView = true
        view.layoutManager?.ensureLayout(for: view.textContainer!)
        let used = view.layoutManager?.usedRect(for: view.textContainer!).height ?? 1
        view.setFrameSize(NSSize(width: width, height: max(used, 1)))

        let op = NSPrintOperation(view: view, printInfo: info)
        op.showsPrintPanel = false
        op.showsProgressPanel = false
        guard op.run(), FileManager.default.fileExists(atPath: out.path) else { throw ToolError("Couldn't create PDF") }
    }
}

/// Thrown to report a result that isn't a file (shown in the toast as-is).
struct Info: LocalizedError {
    let message: String
    init(_ message: String) { self.message = message }
    var errorDescription: String? { message }
}

enum PDFs {
    static let convertItems: [FileOption] = [
        FileOption(title: "PNG") { try await pages($0, as: .png, ext: "png") },
        FileOption(title: "JPG") { try await pages($0, as: .jpeg, ext: "jpg") },
        FileOption(title: "TXT") { try await text($0, format: Docs.formats.first { $0.ext == "txt" }!) },
        FileOption(title: "DOCX") { try await text($0, format: Docs.formats.first { $0.ext == "docx" }!) },
        FileOption(title: "RTF") { try await text($0, format: Docs.formats.first { $0.ext == "rtf" }!) },
        FileOption(title: "HTML") { try await text($0, format: Docs.formats.first { $0.ext == "html" }!) },
    ]

    static let toolItems: [FileOption] = [
        FileOption(title: "Compress") { try compress($0) },
        FileOption(title: "Split Pages") { try split($0) },
        FileOption(title: "Rotate") { try rotate($0) },
        FileOption(title: "Page Count") { url in
            throw Info("\(url.lastPathComponent): \(try open(url).pageCount) pages")
        },
    ]

    static func open(_ url: URL) throws -> PDFDocument {
        guard let doc = PDFDocument(url: url) else { throw ToolError("Couldn't open \(url.lastPathComponent)") }
        if doc.isLocked { throw ToolError("\(url.lastPathComponent) is password protected") }
        return doc
    }

    @MainActor
    private static func text(_ url: URL, format: DocFormat) async throws -> [URL] {
        let doc = try open(url)
        guard let text = doc.selectionForEntireDocument?.attributedString, text.length > 0 else {
            throw ToolError("No text found in this PDF")
        }
        let out = Paths.output(for: url, ext: format.ext)
        try Docs.write(text, to: out, as: format)
        return [out]
    }

    /// Renders every page at 300 DPI, next to the original: "name.png" for one page, "name 1.png", "name 2.png"… for more.
    private static func pages(_ url: URL, as type: UTType, ext: String) async throws -> [URL] {
        try await Images.detached {
            let doc = try open(url)
            var outputs: [URL] = []
            for index in 0..<doc.pageCount {
                guard let page = doc.page(at: index) else { continue }
                let suffix = doc.pageCount > 1 ? " \(index + 1)" : ""
                let out = Paths.output(for: url, ext: ext, suffix: suffix)
                try Images.write(try render(page, dpi: 300), to: out, type: type, quality: 0.92)
                outputs.append(out)
            }
            return outputs
        }
    }

    static func render(_ page: PDFPage, dpi: CGFloat) throws -> CGImage {
        let box = page.bounds(for: .mediaBox)
        let rotated = page.rotation % 180 != 0
        let scale = dpi / 72
        let width = Int((rotated ? box.height : box.width) * scale)
        let height = Int((rotated ? box.width : box.height) * scale)
        guard let ctx = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue) else {
            throw ToolError("Page too large")
        }
        ctx.setFillColor(CGColor(gray: 1, alpha: 1))
        ctx.fill(CGRect(x: 0, y: 0, width: width, height: height))
        ctx.scaleBy(x: scale, y: scale)
        page.draw(with: .mediaBox, to: ctx)
        guard let image = ctx.makeImage() else { throw ToolError("Couldn't render page") }
        return image
    }

    static func merge(_ urls: [URL]) async throws -> URL {
        let merged = PDFDocument()
        for url in urls {
            let doc = try open(url)
            for i in 0..<doc.pageCount {
                if let page = doc.page(at: i)?.copy() as? PDFPage { merged.insert(page, at: merged.pageCount) }
            }
        }
        let out = Paths.unique(urls[0].deletingLastPathComponent().appendingPathComponent("Merged.pdf"))
        guard merged.write(to: out) else { throw ToolError("Couldn't write PDF") }
        return out
    }

    private static func compress(_ url: URL) throws -> [URL] {
        let doc = try open(url)
        let out = Paths.output(for: url, ext: "pdf", suffix: " compressed")
        let options: [PDFDocumentWriteOption: Any] = [.saveImagesAsJPEGOption: true, .optimizeImagesForScreenOption: true]
        guard doc.write(to: out, withOptions: options) else { throw ToolError("Couldn't write PDF") }
        return [out]
    }

    /// One PDF per page, next to the original: "name 1.pdf", "name 2.pdf"…
    private static func split(_ url: URL) throws -> [URL] {
        let doc = try open(url)
        var outputs: [URL] = []
        for i in 0..<doc.pageCount {
            guard let page = doc.page(at: i)?.copy() as? PDFPage else { continue }
            let single = PDFDocument()
            single.insert(page, at: 0)
            let out = Paths.output(for: url, ext: "pdf", suffix: " \(i + 1)")
            single.write(to: out)
            outputs.append(out)
        }
        return outputs
    }

    private static func rotate(_ url: URL) throws -> [URL] {
        let doc = try open(url)
        for i in 0..<doc.pageCount { doc.page(at: i).map { $0.rotation = ($0.rotation + 90) % 360 } }
        let out = Paths.output(for: url, ext: "pdf", suffix: " rotated")
        guard doc.write(to: out) else { throw ToolError("Couldn't write PDF") }
        return [out]
    }
}

enum Archives {
    static var compressItems: [WheelItem] {
        [
            WheelItem(title: "ZIP") { urls in Catalog.saved([try await zip(urls)]) },
            WheelItem(title: "TAR.GZ") { urls in Catalog.saved([try await tar(urls)]) },
        ]
    }

    private static func archiveURL(for urls: [URL], ext: String) -> URL {
        let first = urls[0]
        let dir = first.deletingLastPathComponent()
        let name = urls.count == 1 ? first.deletingPathExtension().lastPathComponent : "Archive"
        return Paths.unique(dir.appendingPathComponent("\(name).\(ext)"))
    }

    static func zip(_ urls: [URL]) async throws -> URL {
        let out = archiveURL(for: urls, ext: "zip")
        if urls.count == 1 {
            try await Shell.run("/usr/bin/ditto", ["-c", "-k", "--sequesterRsrc", "--keepParent", urls[0].path, out.path])
        } else {
            try await Shell.run("/usr/bin/zip", ["-r", "-q", "-y", out.path] + urls.map(\.lastPathComponent),
                                in: urls[0].deletingLastPathComponent())
        }
        return out
    }

    static func tar(_ urls: [URL]) async throws -> URL {
        let out = archiveURL(for: urls, ext: "tar.gz")
        try await Shell.run("/usr/bin/tar", ["-czf", out.path, "-C", urls[0].deletingLastPathComponent().path]
                            + urls.map(\.lastPathComponent))
        return out
    }

    static func extract(_ url: URL) async throws -> [URL] {
        let fm = FileManager.default
        let parent = url.deletingLastPathComponent()
        let staging = parent.appendingPathComponent(".swivel-\(UUID().uuidString)")
        try fm.createDirectory(at: staging, withIntermediateDirectories: true)
        defer { try? fm.removeItem(at: staging) }
        if url.pathExtension.lowercased() == "zip" {
            try await Shell.run("/usr/bin/ditto", ["-x", "-k", url.path, staging.path])
        } else {
            try await Shell.run("/usr/bin/tar", ["-xf", url.path, "-C", staging.path])
        }

        // A single top-level item lands next to the archive; anything else gets a folder named after it.
        let contents = try fm.contentsOfDirectory(at: staging, includingPropertiesForKeys: nil)
            .filter { $0.lastPathComponent != "__MACOSX" }
        // Everything lands next to the archive, without an extra wrapping folder.
        return try contents.map { item in
            let out = Paths.unique(parent.appendingPathComponent(item.lastPathComponent))
            try fm.moveItem(at: item, to: out)
            return out
        }
    }
}
