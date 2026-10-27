import Foundation

/// JSON, JSON Lines, property lists, CSV and TSV ↔ each other, plus YAML out.
/// Values are never guessed: CSV cells stay text, and a value a format can't hold stops the conversion.
enum DataFormats {
    struct Format { let name: String; let ext: String }
    static let inputExtensions: Set<String> = ["json", "jsonl", "ndjson", "plist", "csv", "tsv"]
    static let formats: [Format] = [
        Format(name: "JSON", ext: "json"), Format(name: "JSONL", ext: "jsonl"), Format(name: "PLIST", ext: "plist"),
        Format(name: "CSV", ext: "csv"), Format(name: "TSV", ext: "tsv"), Format(name: "YAML", ext: "yaml"),
    ]
    static let maximumBytes = 32 * 1024 * 1024
    static let maximumRows = 1_000_000
    static let maximumColumns = 16_384
    static let maximumCells = 10_000_000
    /// A binary plist can point at one object many times; written out, every reference is a full copy. The
    /// output estimate counts text, indentation and a little per value, so flat data far larger than any input
    /// passes while a shared or padded structure is stopped long before it is built.
    static let maximumValues = 20_000_000
    static let maximumOutputBytes = 256 * 1024 * 1024
    /// Foundation's pretty JSON writer recurses and overflows a worker thread's stack at about 440 levels.
    static let maximumDepth = 128

    static func items(sourceExt: Set<String>) -> [FileOption] {
        let same: Set<String> = sourceExt == ["ndjson"] ? ["jsonl"] : sourceExt
        return formats.filter { same != [$0.ext] }.map { format in
            FileOption(title: format.name) { url in try await convert(url, to: format) }
        }
    }

    static func convert(_ url: URL, to format: Format) async throws -> [URL] {
        let data = try await Images.detached { () throws -> Data in
            try write(try read(url), as: format.ext)
        }
        return [try Paths.create(data, at: Paths.preferred(for: url, ext: format.ext))]
    }

    /// Either a value tree (Foundation JSON / plist objects) or a table of text rows.
    enum Content {
        case tree(Any)
        case table([[String]])
    }

    static func read(_ url: URL) throws -> Content {
        let ext = url.pathExtension.lowercased()
        let data = try FileSafety.read(url, limit: maximumBytes)
        switch ext {
        case "plist":
            do { return .tree(try PropertyListSerialization.propertyList(from: data, format: nil)) }
            catch { throw ToolError(L("This file is not a valid property list.")) }
        case "csv", "tsv":
            return .table(try table(TextFile.decode(data, normalizeNewlines: false), separator: ext == "tsv" ? "\t" : ","))
        case "jsonl", "ndjson":
            var values: [Any] = []
            for (number, line) in try TextFile.decode(data).components(separatedBy: "\n").enumerated()
            where !line.trimmingCharacters(in: .whitespaces).isEmpty {
                guard values.count < maximumRows else { throw ToolError(L("This file has too many rows.")) }
                do { values.append(try JSONSerialization.jsonObject(with: Data(line.utf8), options: [.fragmentsAllowed])) }
                catch { throw ToolError(L("Line %d is not valid JSON.", number + 1)) }
            }
            return .tree(values)
        default:
            do { return .tree(try JSONSerialization.jsonObject(with: Data(try TextFile.decode(data).utf8), options: [.fragmentsAllowed])) }
            catch let error as ToolError { throw error }
            catch { throw ToolError(L("This file is not valid JSON.")) }
        }
    }

    static func write(_ content: Content, as ext: String) throws -> Data {
        if case .tree(let value) = content { try check(value, json: ["json", "jsonl", "csv", "tsv"].contains(ext)) }
        do { return try encode(content, as: ext) }
        catch let error as ToolError { throw error }
        catch { throw ToolError(L("Couldn't write this data. It may contain text the chosen format can't hold.")) }
    }

    private static func encode(_ content: Content, as ext: String) throws -> Data {
        switch ext {
        case "csv", "tsv":
            let rows = try rows(content)
            let separator: Unicode.Scalar = ext == "tsv" ? "\t" : ","
            var text = ""
            for row in rows {
                text += try row.map { try field($0, separator: separator) }.joined(separator: String(separator)) + "\r\n"
            }
            // A byte-order mark lets Excel and Numbers read non-English text as UTF-8.
            return Data([0xEF, 0xBB, 0xBF]) + Data(text.utf8)
        case "plist":
            let value = try plistValue(tree(content))
            return try PropertyListSerialization.data(fromPropertyList: value, format: .xml, options: 0)
        case "jsonl":
            let value = jsonValue(try tree(content))
            let lines = try (value as? [Any] ?? [value]).map { item -> String in
                String(decoding: try JSONSerialization.data(withJSONObject: item, options: [.fragmentsAllowed, .sortedKeys, .withoutEscapingSlashes]), as: UTF8.self)
            }
            return Data((lines.joined(separator: "\n") + "\n").utf8)
        case "yaml":
            var out = "---\n"
            yaml(jsonValue(try tree(content)), indent: 0, into: &out)
            return Data((out + "\n").utf8)
        default:
            return try JSONSerialization.data(withJSONObject: jsonValue(try tree(content)),
                                              options: [.fragmentsAllowed, .prettyPrinted, .sortedKeys, .withoutEscapingSlashes]) + Data("\n".utf8)
        }
    }

    /// Walks the whole value once before anything is written: how many values it expands to, how much text, how
    /// deep, and (for JSON, JSON Lines, CSV and TSV) no NaN or infinity, which JSON can't hold and Foundation's
    /// writer answers with an uncatchable exception. YAML writes them as .nan and .inf.
    static func check(_ root: Any, json: Bool) throws {
        var stack: [(Any, Int)] = [(root, 0)], count = 0, bytes = 0
        while let (value, depth) = stack.popLast() {
            count += 1
            bytes += 8 + 2 * depth
            guard count <= maximumValues, depth <= maximumDepth else { throw ToolError(L("This file expands to too many values to convert.")) }
            switch value {
            case let dict as [String: Any]:
                for (key, item) in dict { bytes += key.utf8.count; stack.append((item, depth + 1)) }
            case let array as [Any]:
                for item in array { stack.append((item, depth + 1)) }
            case let string as String: bytes += string.utf8.count
            case let data as Data: bytes += data.count * 4 / 3
            case let number as NSNumber:
                if json, CFGetTypeID(number) != CFBooleanGetTypeID(), !number.doubleValue.isFinite {
                    throw ToolError(L("This file contains NaN or infinite numbers, which this format can't hold."))
                }
            default: break
            }
            guard bytes <= maximumOutputBytes else { throw ToolError(L("This file expands to too many values to convert.")) }
        }
    }

    // MARK: Tables

    /// RFC 4180 for CSV: quoted fields may contain commas, quotes ("") and line breaks (CR LF kept as it is).
    /// TSV has no quoting: a quote is an ordinary character. Read by Unicode scalar, so a combining mark after a
    /// separator can't hide it.
    static func table(_ text: String, separator: Unicode.Scalar) throws -> [[String]] {
        let quoting = separator != "\t"
        var rows: [[String]] = [], row: [String] = [], field = String.UnicodeScalarView(), quoted = false, atStart = true
        var scalars = text.unicodeScalars.makeIterator(), pending: Unicode.Scalar?
        func endField() throws {
            row.append(String(field)); field = .init(); atStart = true
            guard row.count <= maximumColumns else { throw ToolError(L("This file has too many columns.")) }
        }
        func endRow() throws {
            try endField()
            if !(row.count == 1 && row[0].isEmpty) { rows.append(row) }
            guard rows.count <= maximumRows else { throw ToolError(L("This file has too many rows.")) }
            row = []
        }
        while let c = pending ?? scalars.next() {
            pending = nil
            if quoted {
                if c == "\"" {
                    if let next = scalars.next() { if next == "\"" { field.append("\"") } else { quoted = false; pending = next } }
                    else { quoted = false }
                } else { field.append(c) }
                continue
            }
            if quoting && c == "\"" && atStart && field.isEmpty {
                quoted = true
            } else if c == separator {
                try endField(); continue
            } else if c == "\n" {
                try endRow(); continue
            } else if c == "\r" {
                if let next = scalars.next(), next != "\n" { pending = next }
                try endRow(); continue
            } else { field.append(c) }
            atStart = false
        }
        guard !quoted else { throw ToolError(L("A quoted field in this file is never closed.")) }
        if !field.isEmpty || !row.isEmpty { try endRow() }
        guard !rows.isEmpty else { throw ToolError(L("This file has no rows.")) }
        return rows
    }

    private static func field(_ value: String, separator: Unicode.Scalar) throws -> String {
        let special = value.unicodeScalars.contains { $0 == separator || $0 == "\n" || $0 == "\r" || ($0 == "\"" && separator == ",") }
        guard special else { return value }
        guard separator == "," else {
            throw ToolError(L("TSV can't hold tabs or line breaks inside a cell. Choose CSV instead."))
        }
        return "\"" + value.replacingOccurrences(of: "\"", with: "\"\"") + "\""
    }

    /// The first row names the columns; each later row becomes an object with text values.
    private static func tree(_ content: Content) throws -> Any {
        switch content {
        case .tree(let value): return value
        case .table(let rows):
            var names: [String] = [], seen: Set<String> = []
            for (i, raw) in rows[0].enumerated() {
                let name = raw.trimmingCharacters(in: .whitespaces).isEmpty ? "column \(i + 1)" : raw
                guard seen.insert(name).inserted else { throw ToolError(L("Column \"%@\" appears twice in the header.", name)) }
                names.append(name)
            }
            // Short rows are padded to the header's width, so a wide header times many rows is the real size.
            guard (rows.count - 1) * names.count <= maximumCells else { throw ToolError(L("This file has too many columns.")) }
            return try rows.dropFirst().enumerated().map { number, row -> [String: Any] in
                guard row.count <= names.count else { throw ToolError(L("Row %d has more cells than the header.", number + 2)) }
                var object: [String: Any] = [:]
                for (i, name) in names.enumerated() { object[name] = i < row.count ? row[i] : "" }
                return object
            }
        }
    }

    /// Arrays of objects (columns in first-seen order) or arrays of arrays. Nested values are written as JSON text.
    private static func rows(_ content: Content) throws -> [[String]] {
        if case .table(let rows) = content { return rows }
        guard case .tree(let value) = content, let items = (value as? [Any]) ?? (value as? [String: Any]).map({ [$0] }), !items.isEmpty else {
            throw ToolError(L("Only a list of records can become a table."))
        }
        func cell(_ value: Any?) throws -> String {
            switch jsonValue(value ?? NSNull()) {
            case is NSNull: return ""
            case let s as String: return s
            case let n as NSNumber: return CFGetTypeID(n) == CFBooleanGetTypeID() ? (n.boolValue ? "true" : "false") : n.stringValue
            case let other:
                return String(decoding: try JSONSerialization.data(withJSONObject: other, options: [.sortedKeys, .withoutEscapingSlashes]), as: UTF8.self)
            }
        }
        if items.allSatisfy({ $0 is [Any] }) { return try items.map { try ($0 as! [Any]).map(cell) } }
        guard items.allSatisfy({ $0 is [String: Any] }) else { throw ToolError(L("Only a list of records can become a table.")) }
        var columns: [String] = [], seen: Set<String> = []
        for case let object as [String: Any] in items {
            // Dictionaries have no order; keep a stable one (sorted within each record, first-seen across records).
            for key in object.keys.sorted() where seen.insert(key).inserted { columns.append(key) }
            guard columns.count <= maximumColumns, columns.count * items.count <= maximumCells else {
                throw ToolError(L("This file has too many columns."))
            }
        }
        return try [columns] + items.map { item in try columns.map { try cell((item as! [String: Any])[$0]) } }
    }

    // MARK: Value conversions

    /// Dates become ISO 8601 text and data becomes Base64, since JSON has neither.
    static func jsonValue(_ value: Any) -> Any {
        switch value {
        case let dict as [String: Any]: return dict.mapValues(jsonValue)
        case let array as [Any]: return array.map(jsonValue)
        case let date as Date: return ISO8601DateFormatter().string(from: date)
        case let data as Data: return data.base64EncodedString()
        default: return value
        }
    }

    private static func plistValue(_ value: Any) throws -> Any {
        switch value {
        case is NSNull: throw ToolError(L("Property lists can't hold empty (null) values. Remove them or choose another format."))
        case let dict as [String: Any]: return try dict.mapValues(plistValue)
        case let array as [Any]: return try array.map(plistValue)
        case let number as NSDecimalNumber:
            // JSON numbers beyond 64-bit integers or double precision would be silently rounded.
            if let exact = Int64(exactly: number) { return NSNumber(value: exact) }
            guard Decimal(number.doubleValue) == number.decimalValue else {
                throw ToolError(L("A number in this file is too large or precise for a property list."))
            }
            return value
        default: return value
        }
    }

    /// Written into one buffer: rebuilding a string at every level would copy deep output once per level.
    private static func yaml(_ value: Any, indent: Int, into out: inout String) {
        let pad = String(repeating: "  ", count: indent)
        func scalar(_ v: Any) -> String? {
            switch v {
            case is NSNull: return "null"
            case let n as NSNumber:
                if CFGetTypeID(n) == CFBooleanGetTypeID() { return n.boolValue ? "true" : "false" }
                let d = n.doubleValue
                if d.isNaN { return ".nan" }
                if d.isInfinite { return d > 0 ? ".inf" : "-.inf" }
                return n.stringValue
            case let s as String:
                // Always quoted: YAML reads bare yes, no, 1.0, ~ or dates as other types. Characters YAML forbids
                // or folds (controls, C1, NEL, line/paragraph separators, BOM, noncharacters) are escaped.
                var escaped = "\""
                for u in s.unicodeScalars {
                    switch u {
                    case "\\": escaped += "\\\\"
                    case "\"": escaped += "\\\""
                    case "\n": escaped += "\\n"
                    case "\t": escaped += "\\t"
                    case _ where u.value < 0x20 || (0x7F...0x9F).contains(u.value): escaped += String(format: "\\x%02X", u.value)
                    case _ where [0x2028, 0x2029, 0xFEFF, 0xFFFE, 0xFFFF].contains(u.value): escaped += String(format: "\\u%04X", u.value)
                    default: escaped.unicodeScalars.append(u)
                    }
                }
                return escaped + "\""
            case let d as [String: Any] where d.isEmpty: return "{}"
            case let a as [Any] where a.isEmpty: return "[]"
            default: return nil
            }
        }
        if let s = scalar(value) { out += s; return }
        var first = true
        func line(_ prefix: String, _ child: Any) {
            if !first { out += "\n" }
            first = false
            if let s = scalar(child) { out += pad + prefix + " " + s; return }
            out += pad + prefix + "\n"
            yaml(child, indent: indent + 1, into: &out)
        }
        if let dict = value as? [String: Any] {
            for key in dict.keys.sorted() { line(scalar(key)! + ":", dict[key]!) }
        } else {
            for v in value as! [Any] { line("-", v) }
        }
    }
}
