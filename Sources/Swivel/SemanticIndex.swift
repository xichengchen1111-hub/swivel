import Accelerate
import AppKit
import NaturalLanguage
import PDFKit

/// Finds files by what they are about, not only by the words they contain: “上周领导给的工作内容” finds the weekly
/// report even when it never says 工作 (FileSearch.cs / SemanticIndex.cs on Windows). Each document's name, folder and
/// opening text become one vector per language, made on this Mac from the word meanings macOS ships with
/// (NLEmbedding word embeddings for English and Simplified Chinese, no download, no model of our own): the average of
/// the content words' vectors. A request becomes vectors the same way, together with the translations and synonyms
/// the rules already chose (FileSearch's concept forms), which is how a Chinese request reaches an English document.
/// Vectors are compared after taking away the index's average, so the words every document shares count for nothing.
/// The index keeps paths, dates, sizes and vectors but no text, and nothing leaves the Mac.
///
/// Probe on macOS 26.6 (12 passages, 14 Chinese and English requests, 6 of them across languages): NLEmbedding's
/// sentence embeddings ranked the right passage first 8/14 (the Chinese one scores every pair about 0.93), the
/// contextual embeddings need an asset download, and averaged content words with the rules' forms, centred, 12/14
/// (13/14 in the top three; right pairs about 0.5, unrelated ones about 0).
enum SemanticIndex {
    /// Similarities are cosines after centring: unrelated documents sit around 0, a clear match above 0.4.
    static let related = 0.45
    /// Only files about as close as the best one are offered; a weak best match brings nothing.
    static let margin = 0.10
    /// A file the words missed is offered only when it is this close.
    static let maxExtra = 12
    static let maxFiles = 20_000
    /// The rank bonus grows from nothing here to its most there (FileSearch.rank).
    static let bonusFrom = 0.35
    static let bonusTo = 0.75

    /// Documents worth reading; pictures and media have no text to embed.
    static let extensions: Set<String> = ["pdf", "doc", "docx", "rtf", "odt", "txt", "md", "markdown", "ppt", "pptx", "odp", "xls",
                                          "xlsx", "csv", "ods", "pages", "key", "numbers", "epub", "eml"]

    enum Space: Int, CaseIterable, Sendable { case english, chinese }

    /// A unit vector stored as bytes (value × scale), a quarter of the size of floats and close enough for ranking.
    struct Vector: Equatable, Sendable {
        var values: [Int8]
        var scale: Float

        init(values: [Int8], scale: Float) { self.values = values; self.scale = scale }

        init(_ floats: [Float]) {
            let largest = floats.map(abs).max() ?? 0
            let scale: Float = largest > 0 ? largest / 127 : 1
            self.scale = scale
            values = floats.map { Int8(max(-127, min(127, ($0 / scale).rounded()))) }
        }

        var floats: [Float] { values.map { Float($0) * scale } }
    }

    struct Entry: Equatable, Sendable {
        var path: String
        var modified: Date
        var created: Date
        /// When it arrived on this Mac (kMDItemDateAdded), else its creation date.
        var added: Date
        var size: Int64
        /// Says where it came from (a browser, Mail, AirDrop or Messages saved it).
        var received: Bool
        var english: Vector?
        var chinese: Vector?

        func vector(_ space: Space) -> Vector? { space == .english ? english : chinese }
    }

    struct Candidate: Sendable {
        var path: String
        var modified: Date
        var created: Date
        var added: Date
        var size: Int64
        var received: Bool
    }

    // MARK: - State shown in Settings

    static let changed = Notification.Name("SemanticIndex.changed")

    /// (files ready, files left to read in the current pass), or nil before the first pass.
    @MainActor static private(set) var progress: (ready: Int, left: Int)?
    @MainActor private static var refreshing: Task<Void, Never>?
    @MainActor private static var refreshedAt = Date.distantPast

    @MainActor static var available: Bool { Embedder.shared != nil }
    @MainActor static var enabled: Bool { Prefs.fileSearch && Prefs.semanticSearch && available }

    /// What the settings page says about the index.
    @MainActor static var status: String {
        guard available else { return L("This Mac has no built-in word meanings for English and Chinese, so this is off.") }
        guard Prefs.semanticSearch else { return "" }
        guard let progress else { return L("The index is built in the background after Quick Actions opens.") }
        if progress.left > 0 { return L("Reading documents: %d ready, %d to go.", progress.ready, progress.left) }
        return L("%d documents ready.", progress.ready)
    }

    @MainActor private static func publish(_ ready: Int, _ left: Int) {
        progress = (ready, left)
        NotificationCenter.default.post(name: changed, object: nil)
    }

    // MARK: - Updating

    /// Brings the index up to date in the background, at most every ten minutes (Quick Actions calls this when it opens).
    @MainActor static func refreshSoon(force: Bool = false) {
        guard enabled, refreshing == nil, force || Date().timeIntervalSince(refreshedAt) >= 600 else { return }
        refreshedAt = Date()
        refreshing = Task { @MainActor in
            await refresh()
            refreshing = nil
        }
    }

    /// Waits for a full pass (the `--index` command line).
    @MainActor static func refreshNow() async {
        if let refreshing { await refreshing.value }
        refreshedAt = Date()
        let task = Task { @MainActor in await refresh() }
        refreshing = task
        await task.value
        refreshing = nil
    }

    @MainActor private static func refresh() async {
        guard let embedder = Embedder.shared else { return }
        let store = Store.shared, file = indexFile
        await Task.detached(priority: .utility) { store.loadIfNeeded(from: file, revision: embedder.revision) }.value
        if progress == nil, store.count > 0 { publish(store.count, 0) }
        let candidates = await Task.detached(priority: .utility) { SemanticIndex.candidates() }.value
        // Spotlight is off or still indexing: keep what we have.
        guard !candidates.isEmpty else { return }
        let generation = store.generation
        let (keep, todo) = store.split(candidates)
        store.publish(keep)
        publish(keep.count, todo.count)
        await Task.detached(priority: .background) {
            var fresh = keep, sinceSave = 0
            for (index, candidate) in todo.enumerated() {
                guard !Task.isCancelled, store.generation == generation else { return }
                // PDFKit and the document readers leave autoreleased objects behind; drain them file by file.
                fresh.append(autoreleasepool { SemanticIndex.entry(for: candidate, embedder: embedder) })
                sinceSave += 1
                if sinceSave >= 100 {
                    sinceSave = 0
                    guard store.publish(fresh, generation: generation) else { return }
                    SemanticIndex.save(fresh, revision: embedder.revision, to: file)
                    let ready = fresh.count, left = todo.count - index - 1
                    await MainActor.run { SemanticIndex.publish(ready, left) }
                }
            }
            guard store.publish(fresh, generation: generation) else { return }
            SemanticIndex.save(fresh, revision: embedder.revision, to: file)
            let ready = fresh.count
            await MainActor.run { SemanticIndex.publish(ready, 0) }
        }.value
    }

    /// Forgets the index (Settings, when the switch is turned off).
    @MainActor static func clear() {
        refreshing?.cancel()
        Store.shared.clear()
        try? FileManager.default.removeItem(at: indexFile)
        progress = nil
        NotificationCenter.default.post(name: changed, object: nil)
    }

    /// Every document in the home folder Spotlight knows, newest first, at most `maxFiles`. One synchronous MDQuery
    /// on a background thread: the attributes come with the results (they are its sorting attributes), where
    /// reading them from NSMetadataItems one by one on the main thread took about a millisecond a file.
    static func candidates() -> [Candidate] {
        let names = extensions.sorted().map { "kMDItemFSName == \"*.\($0)\"c" }.joined(separator: " || ")
        let attributes = [kMDItemFSContentChangeDate, kMDItemFSCreationDate, kMDItemDateAdded, kMDItemFSSize,
                          kMDItemWhereFroms, kMDItemDownloadedDate].map { $0! as String }
        guard let query = MDQueryCreate(kCFAllocatorDefault, names as CFString, nil, attributes as CFArray) else { return [] }
        MDQuerySetSearchScope(query, [kMDQueryScopeHome!] as CFArray, 0)
        guard MDQueryExecute(query, CFOptionFlags(kMDQuerySynchronous.rawValue)) else { return [] }
        MDQueryDisableUpdates(query)
        defer { MDQueryStop(query) }
        func value(_ name: String, _ index: Int) -> Any? {
            MDQueryGetAttributeValueOfResultAtIndex(query, name as CFString, index).map { Unmanaged<AnyObject>.fromOpaque($0).takeUnretainedValue() }
        }
        let downloads = FileSearch.Place.downloads.url.path + "/"
        var list: [Candidate] = []
        for index in 0..<MDQueryGetResultCount(query) {
            // The path isn't one of the gathered attributes; the item has it.
            guard let item = MDQueryGetResultAtIndex(query, index).map({ Unmanaged<MDItem>.fromOpaque($0).takeUnretainedValue() }),
                  let path = MDItemCopyAttribute(item, kMDItemPath) as? String, FileSearch.isVisible(path) else { continue }
            let modified = value(attributes[0], index) as? Date ?? .distantPast
            let created = value(attributes[1], index) as? Date ?? modified
            let fromSomewhere = value(attributes[4], index) != nil || value(attributes[5], index) != nil
            list.append(Candidate(path: path, modified: modified, created: created, added: value(attributes[2], index) as? Date ?? created,
                                  size: (value(attributes[3], index) as? NSNumber)?.int64Value ?? 0,
                                  received: fromSomewhere || path.hasPrefix(downloads)))
        }
        return Array(list.sorted { $0.modified > $1.modified }.prefix(maxFiles))
    }

    /// One document's entry: its name and folder, and what it says, as vectors.
    static func entry(for candidate: Candidate, embedder: Embedder) -> Entry {
        let url = URL(fileURLWithPath: candidate.path)
        let vectors = embedder.passage(name: passageName(url), text: text(of: url))
        return Entry(path: candidate.path, modified: candidate.modified, created: candidate.created, added: candidate.added,
                     size: candidate.size, received: candidate.received,
                     english: vectors[.english].map { Vector($0) }, chinese: vectors[.chinese].map { Vector($0) })
    }

    /// The name as words and the folder it sits in: “COMP3300_week5-lecture” → “COMP3300 week5 lecture · Uni”.
    static func passageName(_ url: URL) -> String {
        let name = url.deletingPathExtension().lastPathComponent.replacingOccurrences(of: "_", with: " ").replacingOccurrences(of: "-", with: " ")
        let folder = url.deletingLastPathComponent().lastPathComponent
        return folder.isEmpty ? name : name + " · " + folder
    }

    // MARK: - Reading text

    /// The opening text of a document, at most `limit` characters; empty when it can't be read safely. Only regular
    /// files (never a symlink), never a cloud placeholder that isn't downloaded (reading it would download it), and
    /// only formats read with an explicit type, so AppKit never guesses one from the content.
    static func text(of url: URL, limit: Int = 4_000) -> String {
        var info = stat()
        guard lstat(url.path, &info) == 0, info.st_mode & S_IFMT == S_IFREG, info.st_flags & UInt32(SF_DATALESS) == 0 else { return "" }
        let text: String
        switch url.pathExtension.lowercased() {
        case "txt", "md", "markdown", "csv", "eml":
            guard let data = try? prefix(of: url, bytes: 256 * 1024) else { return "" }
            text = decodeText(data)
        case "pdf":
            guard info.st_size <= 32 * 1024 * 1024, let data = try? FileSafety.read(url, limit: 32 * 1024 * 1024),
                  let document = PDFDocument(data: data), !document.isLocked else { return "" }
            text = (0..<min(3, document.pageCount)).compactMap { document.page(at: $0)?.string }.joined(separator: "\n")
        case "docx", "doc", "rtf", "odt":
            let types: [String: NSAttributedString.DocumentType] = ["docx": .officeOpenXML, "doc": .docFormat, "rtf": .rtf, "odt": .openDocument]
            guard info.st_size <= 16 * 1024 * 1024, let type = types[url.pathExtension.lowercased()],
                  let data = try? FileSafety.read(url, limit: 16 * 1024 * 1024),
                  let document = try? NSAttributedString(data: data, options: [.documentType: type], documentAttributes: nil) else { return "" }
            text = document.string
        default:
            return ""
        }
        return clean(text, limit: limit)
    }

    /// At most `bytes` from the start of a regular file, opened without following a symlink.
    static func prefix(of url: URL, bytes: Int) throws -> Data {
        let fd = open(url.path, O_RDONLY | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC)
        guard fd >= 0 else { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
        defer { close(fd) }
        var info = stat()
        guard fstat(fd, &info) == 0, info.st_mode & S_IFMT == S_IFREG else { throw POSIXError(.EINVAL) }
        var data = Data(count: bytes)
        let count = data.withUnsafeMutableBytes { Darwin.read(fd, $0.baseAddress, bytes) }
        guard count >= 0 else { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
        return data.prefix(count)
    }

    /// UTF-8 (a character cut at the end is dropped), else GB 18030, which older Chinese text files use.
    static func decodeText(_ data: Data) -> String {
        for cut in 0...3 where data.count >= cut {
            if let text = String(data: data.dropLast(cut), encoding: .utf8) { return text }
        }
        let gb = String.Encoding(rawValue: CFStringConvertEncodingToNSStringEncoding(CFStringEncoding(CFStringEncodings.GB_18030_2000.rawValue)))
        return String(data: data, encoding: gb) ?? String(decoding: data, as: UTF8.self)
    }

    /// One space between words, no control characters.
    static func clean(_ text: String, limit: Int) -> String {
        var result = "", space = false
        for character in text {
            if character.isWhitespace || character.unicodeScalars.allSatisfy({ CharacterSet.controlCharacters.contains($0) }) { space = !result.isEmpty; continue }
            if space { result.append(" ") }
            space = false
            result.append(character)
            if result.count >= limit { break }
        }
        return result
    }

    // MARK: - Asking

    /// How close each indexed file that passes `keep` is to the request (best first). The topic (Plan.topic: what
    /// the request is about, without its time, sender or filler) and each of the plan's terms (its word with translations and synonyms) are compared on their own, in each language the file
    /// has, and a file's similarity is the average over them: a file has to be about most of what was asked, not
    /// only one word of it (“agent” alone finds every agent note, “agent” and “requirements” and “paper” the paper).
    static func closest(topic: String, terms: [[String]], limit: Int = 60, keep: (Entry) -> Bool) -> [(entry: Entry, similarity: Double)] {
        guard let embedder = Embedder.shared else { return [] }
        let store = Store.shared
        store.loadIfNeeded(from: indexFile, revision: embedder.revision)
        let groups = ([topic] + terms.map { $0.joined(separator: " ") }).map(embedder.average).filter { !$0.isEmpty }
        return store.closest(groups, limit: limit, keep: keep)
    }

    // MARK: - Storage

    static var indexFile: URL {
        // Development builds can keep their index apart from the real one.
        if Bundle.main.bundleIdentifier != "local.swivel.app", let directory = ProcessInfo.processInfo.environment["SWIVEL_SEMANTIC_INDEX_DIR"] {
            return URL(fileURLWithPath: directory, isDirectory: true).appendingPathComponent("index-v1.bin")
        }
        let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        return support.appendingPathComponent("Swivel/SemanticIndex/index-v1.bin")
    }

    private static let magic = Data("SWSI1\n".utf8)

    /// paths, dates, sizes, flags and vectors (no text), little-endian.
    static func encode(_ entries: [Entry], revision: String) -> Data {
        var data = magic
        func append<T: FixedWidthInteger>(_ value: T) { withUnsafeBytes(of: value.littleEndian) { data.append(contentsOf: $0) } }
        func append(_ text: String) { let bytes = Data(text.utf8); append(UInt16(min(bytes.count, Int(UInt16.max)))); data.append(bytes.prefix(Int(UInt16.max))) }
        func append(_ date: Date) { append(date.timeIntervalSinceReferenceDate.bitPattern) }
        append(revision)
        append(UInt16(Embedder.dimension))
        append(UInt32(entries.count))
        for entry in entries {
            append(entry.path)
            append(entry.modified); append(entry.created); append(entry.added)
            append(entry.size)
            append(UInt8((entry.received ? 1 : 0) | (entry.english != nil ? 2 : 0) | (entry.chinese != nil ? 4 : 0)))
            for vector in [entry.english, entry.chinese].compactMap({ $0 }) {
                append(vector.scale.bitPattern)
                data.append(contentsOf: vector.values.map { UInt8(bitPattern: $0) })
            }
        }
        return data
    }

    /// The entries, or nil when the data is damaged or was made with other word meanings.
    static func decode(_ data: Data, revision: String) -> [Entry]? {
        let bytes = [UInt8](data)
        var at = 0
        func take(_ count: Int) -> ArraySlice<UInt8>? {
            guard count >= 0, at + count <= bytes.count else { return nil }
            defer { at += count }
            return bytes[at..<at + count]
        }
        func integer<T: FixedWidthInteger>(_ type: T.Type) -> T? {
            guard let slice = take(MemoryLayout<T>.size) else { return nil }
            return slice.reversed().reduce(T(0)) { $0 << 8 | T($1) }
        }
        func text() -> String? { integer(UInt16.self).flatMap { take(Int($0)) }.flatMap { String(bytes: $0, encoding: .utf8) } }
        func date() -> Date? { integer(UInt64.self).map { Date(timeIntervalSinceReferenceDate: Double(bitPattern: $0)) } }
        guard take(magic.count).map(Array.init) == [UInt8](magic), text() == revision,
              integer(UInt16.self) == UInt16(Embedder.dimension), let count = integer(UInt32.self), Int(count) <= maxFiles * 2 else { return nil }
        var entries: [Entry] = []
        entries.reserveCapacity(Int(count))
        for _ in 0..<count {
            guard let path = text(), let modified = date(), let created = date(), let added = date(),
                  let size = integer(Int64.self), let flags = integer(UInt8.self) else { return nil }
            var vectors: [Vector] = []
            for bit in [UInt8(2), 4] where flags & bit != 0 {
                guard let scale = integer(UInt32.self), let values = take(Embedder.dimension) else { return nil }
                vectors.append(Vector(values: values.map { Int8(bitPattern: $0) }, scale: Float(bitPattern: scale)))
            }
            var entry = Entry(path: path, modified: modified, created: created, added: added, size: size, received: flags & 1 != 0)
            if flags & 2 != 0 { entry.english = vectors.removeFirst() }
            if flags & 4 != 0 { entry.chinese = vectors.removeFirst() }
            entries.append(entry)
        }
        return at == bytes.count ? entries : nil
    }

    /// Writes beside the index and renames it into place, so a crash never leaves half an index. The folder is the
    /// user's own (0700) and the file 0600.
    @discardableResult
    static func save(_ entries: [Entry], revision: String, to file: URL) -> Bool {
        let folder = file.deletingLastPathComponent()
        do {
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
            var info = stat()
            guard lstat(folder.path, &info) == 0, info.st_mode & S_IFMT == S_IFDIR, info.st_uid == getuid() else { return false }
            if info.st_mode & 0o077 != 0 { chmod(folder.path, 0o700) }
            let temporary = folder.appendingPathComponent(".index-\(UUID().uuidString).tmp")
            try FileSafety.write(encode(entries, revision: revision), to: temporary)
            guard rename(temporary.path, file.path) == 0 else { unlink(temporary.path); return false }
            return true
        } catch {
            return false
        }
    }

    static func load(from file: URL, revision: String) -> [Entry] {
        guard let data = try? FileSafety.read(file, limit: 256 * 1024 * 1024) else { return [] }
        return decode(data, revision: revision) ?? []
    }
}

/// The entries in memory, and per language a matrix of their vectors with the index's average taken away (at unit
/// length), so comparing a request with every file is one matrix product.
final class SemanticStore: @unchecked Sendable {
    private struct Matrix {
        var mean: [Float]
        /// The entry each row belongs to.
        var rows: [Int] = []
        var values: [Float] = []
    }

    private let lock = NSLock()
    private var entries: [SemanticIndex.Entry] = []
    private var byPath: [String: Int] = [:]
    private var matrices: [Matrix] = []
    private var loaded = false
    private(set) var generation = 0

    var count: Int { lock.withLock { entries.count } }

    func loadIfNeeded(from file: URL, revision: String) {
        guard !lock.withLock({ loaded }) else { return }
        let list = SemanticIndex.load(from: file, revision: revision)
        lock.withLock {
            guard !loaded else { return }
            loaded = true
            install(list)
        }
    }

    /// Unchanged entries (same modified date and size) and the candidates still to read.
    func split(_ candidates: [SemanticIndex.Candidate]) -> (keep: [SemanticIndex.Entry], todo: [SemanticIndex.Candidate]) {
        lock.withLock {
            var keep: [SemanticIndex.Entry] = [], todo: [SemanticIndex.Candidate] = []
            for candidate in candidates {
                if let index = byPath[candidate.path], entries[index].modified == candidate.modified, entries[index].size == candidate.size {
                    var entry = entries[index]
                    entry.added = candidate.added
                    entry.received = candidate.received
                    keep.append(entry)
                } else {
                    todo.append(candidate)
                }
            }
            return (keep, todo)
        }
    }

    func publish(_ list: [SemanticIndex.Entry]) { lock.withLock { loaded = true; install(list) } }

    /// False when the index was cleared since the pass began.
    func publish(_ list: [SemanticIndex.Entry], generation: Int) -> Bool {
        lock.withLock {
            guard generation == self.generation else { return false }
            install(list)
            return true
        }
    }

    func clear() { lock.withLock { generation += 1; loaded = true; install([]) } }

    private func install(_ list: [SemanticIndex.Entry]) {
        entries = list
        byPath = Dictionary(list.enumerated().map { ($1.path, $0) }, uniquingKeysWith: { _, last in last })
        let dimension = Embedder.dimension
        matrices = SemanticIndex.Space.allCases.map { space in
            let vectors = list.enumerated().compactMap { index, entry in entry.vector(space).map { (index, $0.floats) } }
            var mean = [Float](repeating: 0, count: dimension)
            // A few documents have no meaningful average yet.
            if vectors.count >= 10 {
                for (_, vector) in vectors { vDSP_vadd(mean, 1, vector, 1, &mean, 1, vDSP_Length(dimension)) }
                var scale = 1 / Float(vectors.count)
                vDSP_vsmul(mean, 1, &scale, &mean, 1, vDSP_Length(dimension))
            }
            var matrix = Matrix(mean: mean)
            matrix.rows.reserveCapacity(vectors.count)
            matrix.values.reserveCapacity(vectors.count * dimension)
            for (index, vector) in vectors {
                guard let centred = Self.centred(vector, mean) else { continue }
                matrix.rows.append(index)
                matrix.values += centred
            }
            return matrix
        }
    }

    /// `vector` − `mean` at unit length, or nil when nothing is left.
    private static func centred(_ vector: [Float], _ mean: [Float]) -> [Float]? {
        var result = [Float](repeating: 0, count: vector.count)
        vDSP_vsub(mean, 1, vector, 1, &result, 1, vDSP_Length(vector.count))
        var length: Float = 0
        vDSP_svesq(result, 1, &length, vDSP_Length(result.count))
        guard length > 1e-12 else { return nil }
        var scale = 1 / length.squareRoot()
        vDSP_vsmul(result, 1, &scale, &result, 1, vDSP_Length(result.count))
        return result
    }

    /// Each group is one thing asked for, as a vector per language. A file's similarity is the average, over the
    /// groups it shares a language with, of the best cosine in any of those languages.
    func closest(_ groups: [[SemanticIndex.Space: [Float]]], limit: Int, keep: (SemanticIndex.Entry) -> Bool) -> [(entry: SemanticIndex.Entry, similarity: Double)] {
        lock.withLock {
            guard !entries.isEmpty, !groups.isEmpty else { return [] }
            let dimension = Embedder.dimension
            var total = [Double](repeating: 0, count: entries.count), counted = [Int](repeating: 0, count: entries.count)
            for group in groups {
                var best = [Float](repeating: -.infinity, count: entries.count)
                for (space, vector) in group {
                    let matrix = matrices[space.rawValue]
                    guard !matrix.rows.isEmpty, let query = Self.centred(vector, matrix.mean) else { continue }
                    var similarities = [Float](repeating: 0, count: matrix.rows.count)
                    vDSP_mmul(matrix.values, 1, query, 1, &similarities, 1, vDSP_Length(matrix.rows.count), 1, vDSP_Length(dimension))
                    for (row, index) in matrix.rows.enumerated() { best[index] = max(best[index], similarities[row]) }
                }
                for index in entries.indices where best[index] > -.infinity {
                    total[index] += Double(best[index])
                    counted[index] += 1
                }
            }
            var scored: [(entry: SemanticIndex.Entry, similarity: Double)] = []
            for index in entries.indices where counted[index] > 0 && keep(entries[index]) {
                scored.append((entries[index], total[index] / Double(counted[index])))
            }
            return Array(scored.sorted { $0.similarity > $1.similarity }.prefix(limit))
        }
    }
}

extension SemanticStore {
    static let shared = SemanticStore()
}

extension SemanticIndex {
    typealias Store = SemanticStore
}

/// The word meanings macOS ships with (NLEmbedding, 300 numbers per word, English and Simplified Chinese).
final class Embedder: @unchecked Sendable {
    static let dimension = 300
    static let shared: Embedder? = Embedder()

    private let english: NLEmbedding
    private let chinese: NLEmbedding
    private let lock = NSLock()
    /// Changes when macOS updates the word meanings, which makes the stored vectors meaningless.
    let revision: String

    init?() {
        guard let english = NLEmbedding.wordEmbedding(for: .english), let chinese = NLEmbedding.wordEmbedding(for: .simplifiedChinese),
              english.dimension == Self.dimension, chinese.dimension == Self.dimension else { return nil }
        self.english = english
        self.chinese = chinese
        revision = "word en \(english.revision) zh \(chinese.revision) \(Self.dimension)"
    }

    /// Words that carry no topic.
    private static let skipped: Set<NLTag> = [.pronoun, .determiner, .particle, .preposition, .conjunction, .interjection, .classifier, .number]

    /// The average of the content words' vectors in each language the text has, at unit length. Chinese and Japanese
    /// runs (Traditional Chinese read as Simplified) use the Chinese meanings, everything else the English ones.
    func average(_ text: String) -> [SemanticIndex.Space: [Float]] {
        var runs: [SemanticIndex.Space: String] = [:]
        var current = "", currentSpace = SemanticIndex.Space.english
        func flush() {
            if !current.isEmpty { runs[currentSpace, default: ""] += current + " " }
            current = ""
        }
        for character in text {
            let cjk = character.unicodeScalars.contains { $0.properties.isIdeographic || (0x3040...0x30FF).contains($0.value) }
            let space: SemanticIndex.Space = cjk ? .chinese : .english
            if space != currentSpace, !(character.isWhitespace || character.isPunctuation) { flush(); currentSpace = space }
            current.append(character)
        }
        flush()
        var result: [SemanticIndex.Space: [Float]] = [:]
        for (space, run) in runs {
            let source = space == .chinese ? (run.applyingTransform(StringTransform("Hant-Hans"), reverse: false) ?? run) : run
            if let vector = average(source, space: space) { result[space] = vector }
        }
        return result
    }

    private func average(_ text: String, space: SemanticIndex.Space) -> [Float]? {
        let tagger = NLTagger(tagSchemes: [.lexicalClass])
        tagger.string = text
        tagger.setLanguage(space == .chinese ? .simplifiedChinese : .english, range: text.startIndex..<text.endIndex)
        var words: [String] = []
        tagger.enumerateTags(in: text.startIndex..<text.endIndex, unit: .word, scheme: .lexicalClass, options: [.omitWhitespace, .omitPunctuation]) { tag, range in
            if tag.map({ !Self.skipped.contains($0) }) ?? true { words.append(String(text[range]).lowercased()) }
            return words.count < 800
        }
        let model = space == .chinese ? chinese : english
        var sum = [Double](repeating: 0, count: Self.dimension), found = 0
        lock.withLock {
            for word in words {
                guard let vector = model.vector(for: word), vector.count == Self.dimension else { continue }
                for i in 0..<Self.dimension { sum[i] += vector[i] }
                found += 1
            }
        }
        guard found > 0 else { return nil }
        let length = sum.reduce(0) { $0 + $1 * $1 }.squareRoot()
        return length > 0 ? sum.map { Float($0 / length) } : nil
    }

    /// A document: its name and folder count as much as its text, so a telling name isn't drowned by pages of text.
    func passage(name: String, text: String) -> [SemanticIndex.Space: [Float]] {
        let parts = [average(name), average(text)]
        var result: [SemanticIndex.Space: [Float]] = [:]
        for space in SemanticIndex.Space.allCases {
            let vectors = parts.compactMap { $0[space] }
            guard !vectors.isEmpty else { continue }
            var sum = [Float](repeating: 0, count: Self.dimension)
            for vector in vectors { for i in 0..<Self.dimension { sum[i] += vector[i] } }
            let length = sum.reduce(0) { $0 + $1 * $1 }.squareRoot()
            if length > 0 { result[space] = sum.map { $0 / length } }
        }
        return result
    }
}
