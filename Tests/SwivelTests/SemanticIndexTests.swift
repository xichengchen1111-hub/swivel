import AppKit
import CoreText
import Foundation
import Testing
@testable import Swivel

/// Finding files by what they are about (SemanticIndex): which indexed files fit a request's filters, the stored
/// index, the rank bonus, text read from documents and the built-in word meanings. `Swivel --index "<request>"`
/// builds the real index and lists the closest files.
struct SemanticIndexTests {
    private static let calendar: Calendar = {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "UTC")!
        calendar.firstWeekday = 2
        return calendar
    }()
    /// Tuesday 27 October 2026, 15:00 UTC.
    private static let now = calendar.date(from: DateComponents(year: 2026, month: 10, day: 27, hour: 15))!

    private func entry(_ path: String, days: Double = 1, size: Int64 = 1_000, received: Bool = false, english: [Float]? = nil, chinese: [Float]? = nil) -> SemanticIndex.Entry {
        let date = Self.now.addingTimeInterval(-days * 86_400)
        return SemanticIndex.Entry(path: path, modified: date, created: date, added: date, size: size, received: received,
                                   english: english.map { SemanticIndex.Vector($0) }, chinese: chinese.map { SemanticIndex.Vector($0) })
    }

    private func temporaryFolder() throws -> URL {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("swivel-semantic-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        return folder
    }

    @Test func indexedFilesMustFitTheRequestsFilters() {
        let home = NSHomeDirectory()
        func fits(_ entry: SemanticIndex.Entry, _ build: (inout FileSearch.Plan) -> Void) -> Bool {
            var plan = FileSearch.Plan()
            build(&plan)
            return FileSearch.fits(entry, plan: plan, now: Self.now, calendar: Self.calendar)
        }
        let pdf = entry(home + "/Documents/a.pdf"), docx = entry(home + "/Desktop/b.docx", size: 5_000_000)
        #expect(fits(pdf) { $0.kinds = [.pdf] } && !fits(docx) { $0.kinds = [.pdf] })
        #expect(fits(docx) { $0.kinds = [.document] } && !fits(pdf) { $0.kinds = [.screenshot] })
        #expect(fits(docx) { $0.extensions = ["docx"] } && !fits(pdf) { $0.extensions = ["docx"] })
        #expect(fits(docx) { $0.minBytes = 1_000_000 } && !fits(pdf) { $0.minBytes = 1_000_000 } && !fits(docx) { $0.maxBytes = 1_000_000 })
        #expect(fits(docx) { $0.place = .desktop } && !fits(pdf) { $0.place = .desktop })
        // Sent to me: it says where it came from, or it sits in Downloads.
        #expect(!fits(pdf) { $0.received = true })
        #expect(fits(entry(home + "/Documents/c.pdf", received: true)) { $0.received = true })
        #expect(fits(entry(home + "/Downloads/d.pdf")) { $0.received = true })
        // Last week is 19–25 October; one day ago is not in it, six days ago is.
        #expect(!fits(pdf) { $0.period = .lastWeek } && fits(entry(home + "/a.pdf", days: 6)) { $0.period = .lastWeek })
        #expect(fits(entry(home + "/a.pdf", days: 10)) { $0.period = .past(2, .week); $0.dateField = .added })
    }

    @Test func theIndexIsStoredPrivatelyAndReadBackExactly() throws {
        let folder = try temporaryFolder()
        defer { try? FileManager.default.removeItem(at: folder) }
        let vector = (0..<Embedder.dimension).map { Float(sin(Double($0))) / 12 }
        let entries = [entry("/Users/me/a.pdf", english: vector), entry("/Users/me/报价 b.docx", received: true, chinese: vector.reversed()),
                       entry("/Users/me/c.txt", english: vector, chinese: vector), entry("/Users/me/d.md")]
        let data = SemanticIndex.encode(entries, revision: "r1")
        #expect(SemanticIndex.decode(data, revision: "r1") == entries)
        #expect(SemanticIndex.decode(data, revision: "r2") == nil, "other word meanings: start again")
        #expect(SemanticIndex.decode(data.dropLast(), revision: "r1") == nil && SemanticIndex.decode(data + Data([0]), revision: "r1") == nil)
        #expect(SemanticIndex.decode(Data("junk".utf8), revision: "r1") == nil)
        // Quantised vectors stay within a fraction of a percent.
        let restored = try #require(entries[0].english?.floats)
        #expect(zip(restored, vector).allSatisfy { abs($0 - $1) < 0.001 })

        let file = folder.appendingPathComponent("index/index-v1.bin")
        #expect(SemanticIndex.save(entries, revision: "r1", to: file))
        #expect(SemanticIndex.save(Array(entries.prefix(2)), revision: "r1", to: file), "a second save replaces the first")
        #expect(SemanticIndex.load(from: file, revision: "r1") == Array(entries.prefix(2)))
        let attributes = try FileManager.default.attributesOfItem(atPath: file.path)
        let folderAttributes = try FileManager.default.attributesOfItem(atPath: file.deletingLastPathComponent().path)
        #expect((attributes[.posixPermissions] as? NSNumber)?.intValue == 0o600 && (folderAttributes[.posixPermissions] as? NSNumber)?.intValue == 0o700)
        #expect(try FileManager.default.contentsOfDirectory(atPath: file.deletingLastPathComponent().path) == ["index-v1.bin"], "no temporary left behind")
        #expect(SemanticIndex.load(from: folder.appendingPathComponent("missing.bin"), revision: "r1").isEmpty)
    }

    @Test func meaningRaisesAFileTheWordsMissed() {
        var plan = FileSearch.Plan()
        plan.terms = [FileSearch.Term(["工作"])]
        func hit(_ name: String, days: Double, meaning: Double) -> FileSearch.Hit {
            FileSearch.Hit(url: URL(fileURLWithPath: "/Users/me/" + name), name: name, changed: Self.now.addingTimeInterval(-days * 86_400), strict: false, meaning: meaning)
        }
        let ranked = FileSearch.rank([hit("newer.pdf", days: 0, meaning: 0), hit("brief.pdf", days: 30, meaning: 0.7), hit("weak.pdf", days: 30, meaning: 0.2)],
                                     plan: plan, now: Self.now)
        #expect(ranked.map(\.name) == ["brief.pdf", "newer.pdf", "weak.pdf"])
    }

    @Test func closeFilesAreAddedBelowAThresholdAndOnlyWhileTheyExist() throws {
        let folder = try temporaryFolder()
        defer { try? FileManager.default.removeItem(at: folder) }
        func file(_ name: String) throws -> String {
            let url = folder.appendingPathComponent(name)
            try Data("x".utf8).write(to: url)
            return url.path
        }
        let found = FileSearch.Hit(url: URL(fileURLWithPath: try file("found.pdf")), name: "found.pdf", changed: Self.now)
        var closest: [(entry: SemanticIndex.Entry, similarity: Double)] = [
            (entry(found.url.path), 0.80), (entry(try file("close.docx")), 0.76),
            (entry(folder.appendingPathComponent("gone.pdf").path), 0.75), (entry(try file("far.pdf")), 0.50),
        ]
        var hits = FileSearch.addMeaning(closest, to: [found])
        #expect(hits.map(\.name) == ["found.pdf", "close.docx"])
        #expect(hits[0].meaning == 0.80 && hits[0].strict && hits[1].meaning == 0.76 && !hits[1].strict)
        // At most twelve extra files, and nothing when even the best is only loosely related.
        closest = try (0..<20).map { (entry(try file("n\($0).md")), 0.78) }
        #expect(FileSearch.addMeaning(closest, to: []).count == SemanticIndex.maxExtra)
        hits = FileSearch.addMeaning([(entry(try file("loose.md")), SemanticIndex.related - 0.01)], to: [])
        #expect(hits.isEmpty)
    }

    @Test func readsTheOpeningTextOfDocumentsSafely() throws {
        let folder = try temporaryFolder()
        defer { try? FileManager.default.removeItem(at: folder) }
        let sentence = "Operating systems cheat sheet: processes and threads"
        try Data(sentence.utf8).write(to: folder.appendingPathComponent("a.txt"))
        try Data("# 操作系统\n\n复习  资料".utf8).write(to: folder.appendingPathComponent("b.md"))
        let gb = String.Encoding(rawValue: CFStringConvertEncodingToNSStringEncoding(CFStringEncoding(CFStringEncodings.GB_18030_2000.rawValue)))
        try #require("租房合同 押金".data(using: gb)).write(to: folder.appendingPathComponent("c.txt"))
        let styled = NSAttributedString(string: sentence)
        try styled.data(from: NSRange(location: 0, length: styled.length), documentAttributes: [.documentType: NSAttributedString.DocumentType.officeOpenXML])
            .write(to: folder.appendingPathComponent("d.docx"))
        try styled.data(from: NSRange(location: 0, length: styled.length), documentAttributes: [.documentType: NSAttributedString.DocumentType.rtf])
            .write(to: folder.appendingPathComponent("e.rtf"))
        // A one-page PDF with one line of text.
        let pdf = NSMutableData()
        var box = CGRect(x: 0, y: 0, width: 612, height: 792)
        let context = try #require(CGDataConsumer(data: pdf).flatMap { CGContext(consumer: $0, mediaBox: &box, nil) })
        context.beginPDFPage(nil)
        context.textPosition = CGPoint(x: 72, y: 700)
        CTLineDraw(CTLineCreateWithAttributedString(NSAttributedString(string: sentence, attributes: [.font: NSFont.systemFont(ofSize: 14)])), context)
        context.endPDFPage()
        context.closePDF()
        try (pdf as Data).write(to: folder.appendingPathComponent("f.pdf"))
        try FileManager.default.createSymbolicLink(at: folder.appendingPathComponent("g.txt"), withDestinationURL: folder.appendingPathComponent("a.txt"))
        try Data("<w:t>hidden</w:t>".utf8).write(to: folder.appendingPathComponent("h.pages"))

        func text(_ name: String) -> String { SemanticIndex.text(of: folder.appendingPathComponent(name)) }
        #expect(text("a.txt") == sentence && text("b.md") == "# 操作系统 复习 资料" && text("c.txt") == "租房合同 押金")
        #expect(text("d.docx").contains("cheat sheet") && text("e.rtf") == sentence)
        #expect(text("f.pdf").contains("cheat sheet"), "\(text("f.pdf"))")
        #expect(text("g.txt").isEmpty, "a symlink is never followed")
        #expect(text("h.pages").isEmpty && text("missing.txt").isEmpty)
        #expect(SemanticIndex.text(of: folder.appendingPathComponent("a.txt"), limit: 9) == "Operating")
        #expect(SemanticIndex.passageName(URL(fileURLWithPath: "/Users/me/Uni/COMP3300_week5-lecture.pdf")) == "COMP3300 week5 lecture · Uni")
    }

    @Test func theBuiltInWordMeaningsFindTheDocumentAboutTheRequest() throws {
        let embedder = try #require(Embedder.shared, "English and Chinese word embeddings ship with macOS")
        let passages = [
            ("os.md", "Operating systems cheat sheet: processes, threads, scheduling, virtual memory, page tables, deadlocks."),
            ("stew.txt", "Slow cooked beef stew with carrots and potatoes, simmered for three hours."),
            ("invoice.pdf", "Invoice 2041. Total due 1,250 dollars. Payment terms 14 days."),
            ("lease.rtf", "Residential tenancy agreement between landlord and tenant; rent payable monthly and a bond."),
            ("合同.docx", "租房合同 甲方 乙方 租期一年 押金 月租金"),
            ("作业.docx", "机器学习课程作业：用神经网络做图像分类"),
        ]
        let store = SemanticStore()
        store.publish(passages.map { name, text in
            let vectors = embedder.passage(name: name, text: text)
            return SemanticIndex.Entry(path: "/Users/me/" + name, modified: Self.now, created: Self.now, added: Self.now, size: 1, received: false,
                                       english: vectors[.english].map { SemanticIndex.Vector($0) }, chinese: vectors[.chinese].map { SemanticIndex.Vector($0) })
        })
        func best(_ request: String) -> String? {
            let plan = FileSearch.parse(request)
            let groups = ([plan.topic] + plan.terms.map { $0.forms.joined(separator: " ") }).map(embedder.average).filter { !$0.isEmpty }
            return store.closest(groups, limit: 3) { _ in true }.first.map { ($0.entry.path as NSString).lastPathComponent }
        }
        #expect(best("操作系统复习资料") == "os.md", "Chinese request, English document")
        #expect(["lease.rtf", "合同.docx"].contains(best("rental lease") ?? ""))
        #expect(best("beef stew recipe") == "stew.txt")
        #expect(best("neural network homework") == "作业.docx", "English request, Chinese document")
        #expect(embedder.average("").isEmpty)
    }
}
