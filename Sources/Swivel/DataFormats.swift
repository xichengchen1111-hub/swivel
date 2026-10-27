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
            return .table(try table(TextFile.decode(data), separator: ext == "tsv" ? "\t" : ","))
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
        switch ext {
        case "csv", "tsv":
            let rows = try rows(content)
            let separator: Character = ext == "tsv" ? "\t" : ","
            let text = rows.map { $0.map { field(String($0), separator: separator) }.joined(separator: String(separator)) }
                .joined(separator: "\r\n") + "\r\n"
            // A byte-order mark lets Excel and Numbers read non-English text as UTF-8.
            return Data([0xEF, 0xBB, 0xBF]) + Data(text.utf8)
        case "plist":
            let value = try plistValue(tree(content))
            return try PropertyListSerialization.data(fromPropertyList: value, format: .xml, options: 0)
        case "jsonl":
            let value = jsonValue(try tree(content))
            let lines = (value as? [Any] ?? [value]).map { item -> String in
                String(decoding: (try? JSONSerialization.data(withJSONObject: item, options: [.fragmentsAllowed, .sortedKeys, .withoutEscapingSlashes])) ?? Data(), as: UTF8.self)
            }
            return Data((lines.joined(separator: "\n") + "\n").utf8)
        case "yaml":
            return Data(("---\n" + yaml(jsonValue(try tree(content)), indent: 0) + "\n").utf8)
        default:
            return try JSONSerialization.data(withJSONObject: jsonValue(try tree(content)),
                                              options: [.fragmentsAllowed, .prettyPrinted, .sortedKeys, .withoutEscapingSlashes]) + Data("\n".utf8)
        }
    }

    // MARK: Tables

    /// RFC 4180: quoted fields may contain separators, quotes ("") and line breaks.
    static func table(_ text: String, separator: Character) throws -> [[String]] {
        var rows: [[String]] = [], row: [String] = [], field = "", quoted = false, atStart = true
        var chars = text.makeIterator(), pending: Character?
        while let c = pending ?? chars.next() {
            pending = nil
            if quoted {
                if c == "\"" {
                    if let next = chars.next() { if next == "\"" { field.append("\"") } else { quoted = false; pending = next } }
                    else { quoted = false }
                } else { field.append(c) }
            } else if c == "\"" && atStart && field.isEmpty {
                quoted = true
            } else if c == separator {
                row.append(field); field = ""; atStart = true; continue
            } else if c == "\n" {
                row.append(field); field = ""
                if !(row.count == 1 && row[0].isEmpty) { rows.append(row) }
                guard rows.count <= maximumRows else { throw ToolError(L("This file has too many rows.")) }
                row = []; atStart = true; continue
            } else { field.append(c) }
            atStart = false
        }
        guard !quoted else { throw ToolError(L("A quoted field in this file is never closed.")) }
        if !field.isEmpty || !row.isEmpty { row.append(field); rows.append(row) }
        guard !rows.isEmpty else { throw ToolError(L("This file has no rows.")) }
        return rows
    }

    private static func field(_ value: String, separator: Character) -> String {
        guard value.contains(where: { $0 == separator || $0 == "\"" || $0 == "\n" || $0 == "\r" }) else { return value }
        return "\"" + value.replacingOccurrences(of: "\"", with: "\"\"") + "\""
    }

    /// The first row names the columns; each later row becomes an object with text values.
    private static func tree(_ content: Content) throws -> Any {
        switch content {
        case .tree(let value): return value
        case .table(let rows):
            var names: [String] = []
            for (i, raw) in rows[0].enumerated() {
                let name = raw.trimmingCharacters(in: .whitespaces).isEmpty ? "column \(i + 1)" : raw
                guard !names.contains(name) else { throw ToolError(L("Column \"%@\" appears twice in the header.", name)) }
                names.append(name)
            }
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
        func cell(_ value: Any?) -> String {
            switch jsonValue(value ?? NSNull()) {
            case is NSNull: return ""
            case let s as String: return s
            case let n as NSNumber: return CFGetTypeID(n) == CFBooleanGetTypeID() ? (n.boolValue ? "true" : "false") : n.stringValue
            case let other:
                return String(decoding: (try? JSONSerialization.data(withJSONObject: other, options: [.sortedKeys, .withoutEscapingSlashes])) ?? Data(), as: UTF8.self)
            }
        }
        if items.allSatisfy({ $0 is [Any] }) { return items.map { ($0 as! [Any]).map(cell) } }
        guard items.allSatisfy({ $0 is [String: Any] }) else { throw ToolError(L("Only a list of records can become a table.")) }
        var columns: [String] = [], seen: Set<String> = []
        for case let object as [String: Any] in items {
            // Dictionaries have no order; keep a stable one (sorted within each record, first-seen across records).
            for key in object.keys.sorted() where seen.insert(key).inserted { columns.append(key) }
        }
        return [columns] + items.map { item in columns.map { cell((item as! [String: Any])[$0]) } }
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
        default: return value
        }
    }

    private static func yaml(_ value: Any, indent: Int) -> String {
        let pad = String(repeating: "  ", count: indent)
        func scalar(_ v: Any) -> String? {
            switch v {
            case is NSNull: return "null"
            case let n as NSNumber: return CFGetTypeID(n) == CFBooleanGetTypeID() ? (n.boolValue ? "true" : "false") : n.stringValue
            case let s as String:
                // Always quoted: YAML reads bare yes, no, 1.0, ~ or dates as other types.
                let escaped = s.unicodeScalars.map { u -> String in
                    switch u {
                    case "\\": return "\\\\"
                    case "\"": return "\\\""
                    case "\n": return "\\n"
                    case "\t": return "\\t"
                    case _ where u.value < 0x20 || u.value == 0x7F: return String(format: "\\x%02X", u.value)
                    default: return String(u)
                    }
                }.joined()
                return "\"\(escaped)\""
            case let d as [String: Any] where d.isEmpty: return "{}"
            case let a as [Any] where a.isEmpty: return "[]"
            default: return nil
            }
        }
        if let s = scalar(value) { return s }
        if let dict = value as? [String: Any] {
            return dict.keys.sorted().map { key in
                let k = scalar(key)!, v = dict[key]!
                if let s = scalar(v) { return "\(pad)\(k): \(s)" }
                return "\(pad)\(k):\n" + yaml(v, indent: indent + 1)
            }.joined(separator: "\n")
        }
        let array = value as! [Any]
        return array.map { v in
            if let s = scalar(v) { return "\(pad)- \(s)" }
            return "\(pad)-\n" + yaml(v, indent: indent + 1)
        }.joined(separator: "\n")
    }
}
