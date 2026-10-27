import AppKit
import NaturalLanguage
import UniformTypeIdentifiers
#if canImport(FoundationModels)
import FoundationModels
#endif

/// Finds files from a sentence such as “the PDF about the quote I downloaded last week” or “上周的截图”, using the
/// index Spotlight already keeps. Built-in rules read the type, time, folder and size on every Mac; on macOS 26 with
/// Apple Intelligence the on-device model also picks the search words and adds their translations and synonyms.
/// Nothing leaves the Mac, and only Spotlight's index is read, so no new permission is needed.
enum FileSearch {
    enum Kind: String, CaseIterable, Sendable {
        case image, screenshot, pdf, document, spreadsheet, presentation, video, audio, archive, folder, app, code, text

        var contentTypes: [String] {
            switch self {
            case .image, .screenshot: ["public.image"]
            case .pdf: ["com.adobe.pdf"]
            case .document: ["org.openxmlformats.wordprocessingml.document", "com.microsoft.word.doc", "com.apple.iwork.pages.sffpages",
                             "com.apple.iwork.pages.pages", "public.rtf", "org.oasis-open.opendocument.text"]
            case .spreadsheet: ["org.openxmlformats.spreadsheetml.sheet", "com.microsoft.excel.xls", "com.apple.iwork.numbers.sffnumbers",
                                "com.apple.iwork.numbers.numbers", "public.comma-separated-values-text"]
            case .presentation: ["org.openxmlformats.presentationml.presentation", "com.microsoft.powerpoint.ppt",
                                 "com.apple.iwork.keynote.sffkey", "com.apple.iwork.keynote.key"]
            case .video: ["public.movie"]
            case .audio: ["public.audio"]
            case .archive: ["public.archive", "com.apple.disk-image"]
            case .folder: ["public.folder"]
            case .app: ["com.apple.application-bundle"]
            case .code: ["public.source-code", "public.script"]
            case .text: ["public.plain-text", "net.daringfireball.markdown"]
            }
        }

        var title: String {
            switch self {
            case .image: L("Images")
            case .screenshot: L("Screenshots")
            case .pdf: "PDF"
            case .document: L("Documents")
            case .spreadsheet: L("Spreadsheets")
            case .presentation: L("Presentations")
            case .video: L("Videos")
            case .audio: L("Audio")
            case .archive: L("Archives")
            case .folder: L("Folders")
            case .app: L("Apps")
            case .code: L("Code")
            case .text: L("Text files")
            }
        }
    }

    /// Which date a time in the request refers to.
    enum DateField: String, Sendable {
        case changed, created, used, added
        var attribute: String {
            switch self {
            case .changed: "kMDItemFSContentChangeDate"
            case .created: "kMDItemContentCreationDate"
            case .used: "kMDItemLastUsedDate"
            case .added: "kMDItemDateAdded"
            }
        }
    }

    enum Place: String, CaseIterable, Sendable {
        case downloads, desktop, documents, pictures, movies, music, applications

        var url: URL {
            let home = FileManager.default.homeDirectoryForCurrentUser
            switch self {
            case .downloads: return home.appendingPathComponent("Downloads", isDirectory: true)
            case .desktop: return home.appendingPathComponent("Desktop", isDirectory: true)
            case .documents: return home.appendingPathComponent("Documents", isDirectory: true)
            case .pictures: return home.appendingPathComponent("Pictures", isDirectory: true)
            case .movies: return home.appendingPathComponent("Movies", isDirectory: true)
            case .music: return home.appendingPathComponent("Music", isDirectory: true)
            case .applications: return URL(fileURLWithPath: "/Applications", isDirectory: true)
            }
        }

        var title: String {
            switch self {
            case .downloads: L("In Downloads")
            case .desktop: L("On the Desktop")
            case .documents: L("In Documents")
            case .pictures: L("In Pictures")
            case .movies: L("In Movies")
            case .music: L("In Music")
            case .applications: L("In Applications")
            }
        }
    }

    enum Unit: String, Sendable {
        case day, week, month, year, hour, quarter
    }

    /// Which part of a day, week, month or year: morning / start of the week / 上旬 / 年初 (early), afternoon / 中旬
    /// (middle), evening / weekend / 月底 / 年底 (late).
    enum Part: Int, Sendable {
        case whole, early, middle, late, firstHalf, secondHalf
    }

    /// `calendar`: a whole calendar unit counted back from the current one (`count` 0 = this, −1 = last, −2 = the one
    /// before). `date`: a month or day of a year (`count` = the year, 0 = the latest that has begun, −N = N years
    /// back). `weekday`: a weekday (`count` 0 = the latest, −1 = in last week). `season`: `month` is its first month.
    enum PeriodKind: Sendable {
        case today, yesterday, dayBeforeYesterday, thisWeek, lastWeek, thisMonth, lastMonth, thisYear, lastYear, recent, past, ago, year
        case calendar, date, weekday, season
    }

    /// A time the request names, kept unresolved so the same plan gives the same dates in tests (FileSearch.cs has
    /// the same model). `count` is the N of “past N days” / “N weeks ago”, or the year.
    struct Period: Equatable, Sendable, CustomStringConvertible {
        var kind: PeriodKind
        var count = 0
        var unit: Unit = .day
        var month = 0
        var day = 0
        /// 0 = Sunday … 6 = Saturday.
        var weekday = 0
        var part: Part = .whole
        /// “左右”, “about”, “两周多”: soft edges, so the search looks wider and files near the middle rank higher.
        var loose = false
        /// “从上个月开始”, “since March”: from the start of the time until now.
        var onward = false

        init(_ kind: PeriodKind, _ count: Int = 0, _ unit: Unit = .day, month: Int = 0, day: Int = 0, weekday: Int = 0,
             part: Part = .whole, loose: Bool = false) {
            self.kind = kind
            self.count = count
            self.unit = unit
            self.month = month
            self.day = day
            self.weekday = weekday
            self.part = part
            self.loose = loose
        }

        static let today = Period(.today)
        static let yesterday = Period(.yesterday)
        static let dayBeforeYesterday = Period(.dayBeforeYesterday)
        static let thisWeek = Period(.thisWeek)
        static let lastWeek = Period(.lastWeek)
        static let thisMonth = Period(.thisMonth)
        static let lastMonth = Period(.lastMonth)
        static let thisYear = Period(.thisYear)
        static let lastYear = Period(.lastYear)
        static let recent = Period(.recent)
        static func past(_ count: Int, _ unit: Unit) -> Period { Period(.past, count, unit) }
        static func ago(_ count: Int, _ unit: Unit) -> Period { Period(.ago, count, unit) }
        static func year(_ year: Int) -> Period { Period(.year, year) }

        var description: String {
            "\(kind)(\(count) \(unit) m\(month) d\(day) w\(weekday) \(part)\(loose ? " loose" : "")\(onward ? " onward" : ""))"
        }

        /// The range to search: the named range, widened where the words were vague (“最近”, “过去两周”, “两周前”,
        /// anything with “左右”), so a time read a little differently from what was meant still finds the file.
        func window(now: Date, calendar: Calendar) -> (start: Date, end: Date) {
            let range = range(now: now, calendar: calendar)
            guard loose || [.past, .recent, .ago].contains(kind) else { return range }
            let span = range.end.timeIntervalSince(range.start)
            let margin = min(max(span / (loose ? 2 : 4), 12 * 3_600), Double(loose ? 60 : 30) * 86_400)
            let soon = now.addingTimeInterval(60)
            let end = range.end >= soon ? range.end : min(range.end.addingTimeInterval(margin), soon)
            return (range.start.addingTimeInterval(-margin), end)
        }

        /// Start (inclusive) and end (exclusive), in the calendar's time zone.
        func range(now: Date, calendar: Calendar) -> (start: Date, end: Date) {
            if onward {
                var from = self
                from.onward = false
                return (from.range(now: now, calendar: calendar).start, now.addingTimeInterval(60))
            }
            let today = calendar.startOfDay(for: now)
            let soon = now.addingTimeInterval(60)
            func add(_ component: Calendar.Component, _ value: Int, _ date: Date) -> Date {
                calendar.date(byAdding: component, value: value, to: date) ?? date
            }
            func shifted(_ n: Int, _ unit: Unit, _ date: Date) -> Date {
                switch unit {
                case .hour: add(.hour, n, date)
                case .day: add(.day, n, date)
                case .week: add(.day, 7 * n, date)
                case .month: add(.month, n, date)
                case .quarter: add(.month, 3 * n, date)
                case .year: add(.year, n, date)
                }
            }
            func make(_ year: Int, _ month: Int, _ day: Int) -> Date {
                calendar.date(from: DateComponents(year: min(max(year, 1), 9998), month: month, day: day)) ?? today
            }
            func yearOf(_ date: Date) -> Int { calendar.component(.year, from: date) }
            func weekdayOf(_ date: Date) -> Int { calendar.component(.weekday, from: date) - 1 }
            func daysIn(_ year: Int, _ month: Int) -> Int {
                calendar.range(of: .day, in: .month, for: make(year, month, 1))?.count ?? 31
            }
            func weekStart(_ date: Date) -> Date {
                calendar.startOfDay(for: add(.day, -((weekdayOf(date) - (calendar.firstWeekday - 1) + 7) % 7), date))
            }
            func monthStart(_ date: Date) -> Date {
                let parts = calendar.dateComponents([.year, .month], from: date)
                return make(parts.year ?? 2000, parts.month ?? 1, 1)
            }
            func align(_ unit: Unit, _ date: Date) -> Date {
                switch unit {
                case .hour, .day: return date
                case .week: return weekStart(date)
                case .month: return monthStart(date)
                case .quarter:
                    let parts = calendar.dateComponents([.year, .month], from: date)
                    return make(parts.year ?? 2000, ((parts.month ?? 1) - 1) / 3 * 3 + 1, 1)
                case .year: return make(yearOf(date), 1, 1)
                }
            }
            // Weekends and “start of the week” go by Monday whatever the first day of the week is.
            func monday(_ start: Date) -> Date {
                var offset = (1 - weekdayOf(start) + 7) % 7
                if offset > 3 { offset -= 7 }
                return add(.day, offset, start)
            }
            func portion(_ start: Date, _ end: Date, _ unit: Unit) -> (Date, Date) {
                if part == .whole { return (start, end) }
                if part == .firstHalf || part == .secondHalf {
                    let middle: Date = switch unit {
                    case .year: add(.month, 6, start)
                    case .quarter: add(.day, 45, start)
                    case .month: add(.day, 15, start)
                    case .week: add(.day, 3, monday(start))
                    default: add(.hour, 12, start)
                    }
                    return part == .firstHalf ? (unit == .week ? monday(start) : start, middle)
                        : (middle, unit == .week ? add(.day, 7, monday(start)) : end)
                }
                let i = part.rawValue - 1
                switch unit {
                case .day:
                    let hours = [0, 12, 18, 28]
                    return (add(.hour, hours[i], start), add(.hour, hours[i + 1], start))
                case .week:
                    let first = monday(start)
                    let days = [0, 2, 5, 7]
                    return (add(.day, part == .early ? 0 : days[i], first), add(.day, part == .early ? 3 : days[i + 1], first))
                case .month:
                    return part == .late ? (add(.day, 20, start), end) : (add(.day, 10 * i, start), add(.day, 10 * i + 10, start))
                case .quarter:
                    return (add(.month, i, start), add(.month, i + 1, start))
                default:
                    let months = [0, 3, 9, 12]
                    return (add(.month, months[i], start), add(.month, months[i + 1], start))
                }
            }
            func capped(_ range: (Date, Date)) -> (start: Date, end: Date) { (range.0, min(range.1, soon)) }

            switch kind {
            case .calendar:
                // “周末” on a Wednesday is last weekend; “月底” on the 5th is last month's end.
                var back = 0
                while true {
                    let start = shifted(count - back, unit, align(unit, today))
                    let range = portion(start, shifted(1, unit, start), unit)
                    if range.0 <= now || back > 0 { return capped(range) }
                    back += 1
                }
            case .date where month == 0 && day > 0:
                // “14号”: the latest 14th that has come.
                for back in 0..<13 {
                    let first = add(.month, -back, monthStart(today))
                    let parts = calendar.dateComponents([.year, .month], from: first)
                    if day > daysIn(parts.year ?? 2000, parts.month ?? 1) { continue }
                    let date = add(.day, day - 1, first)
                    if date <= now { return (date, add(.day, 1, date)) }
                }
                return (today, add(.day, 1, today))
            case .date:
                var year = count > 0 ? count : yearOf(today) + count
                while true {
                    let valid = (1...12).contains(month)
                    let start = make(year, valid ? month : 1, day > 0 && valid ? min(day, daysIn(year, month)) : 1)
                    let unit: Unit = month == 0 ? .year : day == 0 ? .month : .day
                    let range = portion(start, shifted(1, unit, start), unit)
                    if range.0 <= now || count != 0 || year < yearOf(today) - 1 { return capped(range) }
                    year -= 1
                }
            case .season:
                // Winter runs into the next year.
                var year = count > 0 ? count : yearOf(today) + count
                while true {
                    let start = make(year, min(max(month, 1), 12), 1)
                    if start <= now || count != 0 || year < yearOf(today) - 1 { return capped((start, add(.month, 3, start))) }
                    year -= 1
                }
            case .weekday:
                var date = today
                if count == 0 {
                    while weekdayOf(date) != weekday { date = add(.day, -1, date) }
                } else {
                    date = add(.day, 7 * count + (weekday + 6) % 7, monday(weekStart(today)))
                }
                return (date, add(.day, 1, date))
            case .today: return (today, add(.day, 1, today))
            case .yesterday: return (add(.day, -1, today), today)
            case .dayBeforeYesterday: return (add(.day, -2, today), add(.day, -1, today))
            case .thisWeek: return (weekStart(today), soon)
            case .lastWeek: return (add(.day, -7, weekStart(today)), weekStart(today))
            case .thisMonth: return (monthStart(today), soon)
            case .lastMonth: return (add(.month, -1, monthStart(today)), monthStart(today))
            case .thisYear: return (make(yearOf(today), 1, 1), soon)
            case .lastYear: return (make(yearOf(today) - 1, 1, 1), make(yearOf(today), 1, 1))
            case .recent: return (add(.day, -14, today), soon)
            case .past: return (shifted(-max(1, count), unit, now), soon)
            case .ago where unit == .day:
                // “3天前” is that day; “十几天前”, “半个月前” are a few days either side.
                let agoDay = add(.day, -count, today)
                let slack = count <= 3 ? 0 : max(1, count / 4)
                return (add(.day, -slack, agoDay), add(.day, 1 + slack, agoDay))
            case .ago:
                // “Two weeks ago” is loose: anything between one and three weeks back.
                return (shifted(-(count + 1), unit, now), shifted(-max(0, count - 1), unit, now))
            case .year:
                let january = make(count, 1, 1)
                return (january, add(.year, 1, january))
            }
        }

        var title: String { (loose ? "≈ " : "") + name }

        private var name: String {
            if onward || [.calendar, .date, .weekday, .season].contains(kind) { return dates() }
            switch (kind, unit) {
            case (.past, .hour): return L("Past %d hours", count)
            case (.past, .quarter), (.ago, .quarter): return dates()
            case (.today, _): return L("Today")
            case (.yesterday, _): return L("Yesterday")
            case (.dayBeforeYesterday, _): return L("The day before yesterday")
            case (.thisWeek, _): return L("This week")
            case (.lastWeek, _): return L("Last week")
            case (.thisMonth, _): return L("This month")
            case (.lastMonth, _): return L("Last month")
            case (.thisYear, _): return L("This year")
            case (.lastYear, _): return L("Last year")
            case (.recent, _): return L("Recently")
            case (.past, _) where count == 1:
                switch unit {
                case .day: return L("Past day")
                case .week: return L("Past week")
                case .month: return L("Past month")
                default: return L("Past year")
                }
            case (.ago, _) where count == 1:
                switch unit {
                case .hour: return L("An hour ago")
                case .day: return L("A day ago")
                case .week: return L("A week ago")
                case .month: return L("A month ago")
                default: return L("A year ago")
                }
            case (.ago, .hour): return L("%d hours ago", count)
            case (.past, .day): return L("Past %d days", count)
            case (.past, .week): return L("Past %d weeks", count)
            case (.past, .month): return L("Past %d months", count)
            case (.past, _): return L("Past %d years", count)
            case (.ago, .day): return L("%d days ago", count)
            case (.ago, .week): return L("%d weeks ago", count)
            case (.ago, .month): return L("%d months ago", count)
            case (.ago, _): return L("%d years ago", count)
            default: return String(count)
            }
        }

        /// The range in the user's own date format (“10月1日 – 10月14日”, “27 Oct 18:00 – 04:00”).
        func dates(now: Date = Date(), calendar: Calendar = .current) -> String {
            let range = range(now: now, calendar: calendar)
            let start = range.start
            var end = range.end, last = end.addingTimeInterval(-1)
            let midnight = { (date: Date) in calendar.startOfDay(for: date) == date }
            var wholeDays = midnight(start) && midnight(end)
            // A range that runs until now ends today.
            if end > now {
                last = calendar.startOfDay(for: now)
                end = calendar.date(byAdding: .day, value: 1, to: last) ?? now
                wholeDays = midnight(start)
            }
            let year = calendar.component(.year, from: now)
            let otherYear = calendar.component(.year, from: start) != year || calendar.component(.year, from: last) != year
            func formatter(_ template: String) -> DateFormatter {
                let formatter = DateFormatter()
                formatter.locale = Language.resolved.locale
                formatter.calendar = calendar
                formatter.timeZone = calendar.timeZone
                formatter.setLocalizedDateFormatFromTemplate(template)
                return formatter
            }
            let days = formatter(otherYear ? "yMMMd" : "MMMd"), time = formatter("jm")
            if !wholeDays { return days.string(from: start) + " " + time.string(from: start) + " – " + time.string(from: end) }
            return calendar.isDate(start, inSameDayAs: last) ? days.string(from: start)
                : days.string(from: start) + " – " + days.string(from: last)
        }
    }

    /// One thing the file should mention. Any of its forms (the word, a translation, a synonym) is enough.
    struct Term: Equatable, Sendable {
        var forms: [String]
        /// Typed in quotes: the user's exact words, which the model never replaces.
        var exact = false
        init(_ forms: [String], exact: Bool = false) { self.forms = forms; self.exact = exact }
    }

    struct Plan: Equatable, Sendable {
        var terms: [Term] = []
        var kinds: [Kind] = []
        var extensions: [String] = []
        var period: Period?
        var dateField: DateField = .changed
        var place: Place?
        var minBytes: Int64?
        var maxBytes: Int64?
        /// “领导给的”, “sent to me”: files someone else sent, which arrive from the web, Mail, AirDrop or in Downloads.
        var received = false
        /// The on-device model chose the terms.
        var understood = false
        /// What the request is about, for finding files by meaning (SemanticIndex): the quoted phrases, the concept
        /// words as typed and the words left over, without the time, sender, place, size, kind or filler (in
        /// “前两周存的一个密钥” the 周 is a time, and would otherwise pull every weekly report to the top).
        var topic = ""

        var isEmpty: Bool {
            terms.isEmpty && kinds.isEmpty && extensions.isEmpty && period == nil && place == nil && minBytes == nil && maxBytes == nil
                && !received
        }

        /// Anything besides the words: a type, time, folder, size or “sent to me”.
        var hasFilters: Bool {
            !kinds.isEmpty || !extensions.isEmpty || period != nil || place != nil || minBytes != nil || maxBytes != nil || received
        }

        /// What the search looks for, for the hint under the results.
        var summary: String {
            var parts: [String] = []
            if !extensions.isEmpty { parts.append(extensions.prefix(2).map { $0.uppercased() }.joined(separator: "/")) }
            else if !kinds.isEmpty { parts.append(kinds.map(\.title).joined(separator: "/")) }
            if let period { parts.append(period.title) }
            if let place { parts.append(place.title) } else if received { parts.append(L("Sent to you")) }
            let bytes = ByteCountFormatter()
            if let minBytes { parts.append("> " + bytes.string(fromByteCount: minBytes)) }
            if let maxBytes { parts.append("< " + bytes.string(fromByteCount: maxBytes)) }
            parts += terms.map { "“" + $0.forms.prefix(3).joined(separator: " / ") + "”" }
            return parts.joined(separator: " · ")
        }
    }

    struct Hit: Equatable, Sendable {
        let url: URL
        let name: String
        var changed: Date?
        var used: Date?
        var added: Date?
        /// Spotlight's content type tree, such as public.source-code → public.plain-text → public.text.
        var types: [String] = []
        /// Found by the query that needs every term; the looser one only needs one.
        var strict = true
        /// How close the file is to the request in meaning (SemanticIndex; 0 when it isn't in the index).
        var meaning = 0.0

        var latest: Date? { [changed, used, added].compactMap { $0 }.max() }

        /// The date a time in the request was about; creation dates aren't fetched, so those show the latest.
        func date(for field: DateField) -> Date? {
            switch field {
            case .changed: changed
            case .used: used
            case .added: added
            case .created: latest
            }
        }
    }

    // MARK: - Reading the request

    private enum Cue {
        case kind(Kind), place(Place), field(DateField), big, small, received, filler
    }

    /// Words and phrases with a fixed meaning, longest first so “screen recording” wins over “recording”. Chinese
    /// and Japanese are matched anywhere, Latin words only as whole words.
    private static let lexicon: [(NSRegularExpression, Cue)] = {
        var entries: [(String, Cue)] = []
        func add(_ cue: Cue, _ phrases: [String]) { entries += phrases.map { ($0, cue) } }
        add(.place(.downloads), ["下载文件夹", "下载目录", "下载里", "下载中", "在下载", "downloads folder", "in downloads", "in my downloads", "downloads"])
        add(.place(.desktop), ["桌面上", "桌面", "desktop"])
        add(.place(.documents), ["文稿文件夹", "文稿里", "文稿中", "在文稿", "documents folder", "in documents", "in my documents"])
        add(.place(.pictures), ["图片文件夹", "pictures folder"])
        add(.place(.movies), ["影片文件夹", "movies folder"])
        add(.place(.music), ["音乐文件夹", "music folder"])
        add(.place(.applications), ["应用程序文件夹", "applications folder"])
        add(.kind(.screenshot), ["截图", "截屏", "屏幕快照", "スクリーンショット", "screenshot", "screenshots", "screen shot", "screen shots"])
        add(.kind(.image), ["图片", "照片", "相片", "图像", "画像", "写真", "image", "images", "photo", "photos", "picture", "pictures", "pic", "pics"])
        add(.kind(.pdf), ["pdf", "pdfs"])
        add(.kind(.document), ["word 文档", "文档", "文書", "word", "doc", "docs", "document", "documents"])
        add(.kind(.spreadsheet), ["电子表格", "表格", "excel", "spreadsheet", "spreadsheets"])
        add(.kind(.presentation), ["演示文稿", "幻灯片", "ppt", "keynote", "slides", "slide deck", "deck", "presentation", "presentations"])
        add(.kind(.video), ["视频", "影片", "录屏", "录像", "电影", "動画", "video", "videos", "movie", "movies", "screen recording", "screen recordings"])
        add(.kind(.audio), ["音频", "音乐", "歌曲", "录音", "语音", "audio", "music", "song", "songs", "recording", "recordings", "voice memo", "voice memos", "podcast"])
        add(.kind(.archive), ["压缩包", "压缩文件", "安装包", "archive", "archives", "zip file", "zip files"])
        add(.kind(.folder), ["文件夹", "目录", "フォルダ", "folder", "folders", "directory"])
        add(.kind(.app), ["应用程序", "应用", "软件", "アプリ", "app", "apps", "application", "applications"])
        add(.kind(.code), ["源代码", "代码", "脚本", "source code", "code", "script", "scripts"])
        add(.kind(.text), ["纯文本", "文本文件", "text file", "text files"])
        add(.field(.used), ["打开过的", "打开过", "打开的", "用过的", "用过", "看过的", "看过", "opened", "used", "viewed"])
        add(.field(.changed), ["修改过的", "修改过", "改过的", "改过", "编辑过", "modified", "changed", "edited", "updated"])
        add(.field(.created), ["创建的", "新建的", "拍摄的", "拍的", "created", "made", "taken", "shot"])
        add(.field(.added), ["下载的", "下载", "下的", "保存的", "存的", "存下来的", "downloaded", "saved", "got"])
        // Someone else sent it: who it was doesn't appear in the file, how it arrived does.
        add(.received, ["领导给的", "领导发的", "领导给我的", "领导发给我的", "领导发来的", "老板给的", "老板发的", "老板发来的",
                        "上司给的", "上司发的", "经理给的", "经理发的", "主管给的", "主管发的", "老师给的", "老师发的", "老师发来的", "教授发的",
                        "同事给的", "同事发的", "同学给的", "同学发的", "客户给的", "客户发的", "朋友给的", "朋友发的", "别人给的", "别人发的",
                        "别人传的", "发给我的", "发我的", "传给我的", "发来的", "收到的", "接收的",
                        "sent to me", "sent me", "someone sent", "shared with me", "from my boss", "from my manager", "from my teacher",
                        "from my professor", "my boss sent", "boss sent", "manager sent", "teacher sent", "received"])
        add(.big, ["大文件", "很大的", "big files", "big file", "large files", "large file", "huge", "big", "large"])
        add(.small, ["小文件", "small files", "small file", "tiny"])
        add(.filler, ["帮我找一下", "帮我找", "帮我搜", "帮我", "给我", "找一下", "搜一下", "找到", "查找", "搜索", "寻找", "找找", "找", "搜",
                      "一下", "那个", "那些", "那份", "那张", "那几个", "这个", "这些", "我的", "我们的", "有关", "关于", "相关的", "相关",
                      "一个", "一份", "一张", "文件", "东西", "里面的", "里面", "里的", "上的", "中的", "以前的", "之前的", "时候", "的", "了", "吗",
                      "内容", "资料", "材料", "方面的", "方面", "要用的", "要用", "用的", "需要的", "要交的", "相关内容", "什么的", "之类的", "那种",
                      "呢", "和", "跟", "与", "或者", "还是", "从", "在", "是", "有", "ファイル", "探して",
                      "那会儿", "那会", "那时候", "那时", "的时候", "左右", "前后", "上次", "上回", "那次", "大概", "大约", "差不多",
                      "find", "search", "look", "looking", "for", "show", "me", "my", "mine", "our", "the", "a", "an", "that", "this", "those",
                      "these", "some", "file", "files", "from", "in", "on", "at", "of", "with", "about", "regarding", "which", "what", "where",
                      "i", "we", "you", "it", "is", "was", "were", "are", "named", "called", "titled", "please", "can", "could", "get", "open",
                      "thing", "stuff", "one", "ones", "to", "and", "or", "by", "there", "had", "have", "all", "any", "ago"])
        return entries.sorted { $0.0.count > $1.0.count }.compactMap { phrase, cue in
            let escaped = NSRegularExpression.escapedPattern(for: phrase).replacingOccurrences(of: " ", with: "\\s+")
            let latin = phrase.unicodeScalars.allSatisfy { $0.isASCII }
            let pattern = latin ? "(?<![\\p{L}\\p{N}])" + escaped + "(?![\\p{L}\\p{N}])" : escaped
            return (try? NSRegularExpression(pattern: pattern, options: [.caseInsensitive])).map { ($0, cue) }
        }
    }()

    /// What people call a kind of file versus what the files are named or say (FileSearch.cs has the same table):
    /// “找工作” finds résumés and cover letters, “操作系统” English “operating system” notes. When a request names one
    /// of the left-hand phrases, the search looks for any of the right-hand words instead (one term, any form).
    /// Latin words of three letters or fewer are matched as whole words (“os”, “cv”), longer ones as prefixes.
    static let conceptGroups: [(triggers: [String], forms: [String])] = [
    (["找工作", "求职", "应聘", "投简历", "找实习", "申请工作", "job hunting", "job search", "job application", "job applications", "career"],
     ["简历", "求职信", "推荐信", "面试", "申请", "resume", "cv", "cover letter", "recommendation letter", "reference letter", "lor", "interview", "offer", "application"]),
    (["简历", "履历", "resume", "cv", "curriculum vitae"], ["简历", "履历", "resume", "cv", "curriculum vitae"]),
    (["大学", "学校", "上学", "读书", "课程", "上课", "学业", "学习", "uni", "university", "college", "school", "course", "coursework", "class", "study", "studies"],
     ["大学", "课程", "学期", "作业", "课件", "讲义", "考试", "期末", "期中", "实验", "复习", "university", "uni", "college", "semester", "subject",
      "course", "lecture", "tutorial", "workshop", "assignment", "homework", "exam", "quiz", "midterm", "syllabus", "lab"]),
    (["作业", "功课", "习题", "homework", "assignment", "problem set"],
     ["作业", "功课", "习题", "练习", "homework", "assignment", "exercise", "problem set", "submission"]),
    (["课件", "讲义", "课堂笔记", "lecture notes", "lecture slides"], ["课件", "讲义", "lecture", "slides", "tutorial"]),
    (["复习", "备考", "考试", "期末", "期中", "真题", "exam", "revision", "final exam", "midterm", "cheat sheet"],
     ["复习", "考试", "期末", "期中", "真题", "笔记", "exam", "revision", "review", "cheat sheet", "midterm", "final", "past paper", "practice", "notes", "summary"]),
    (["笔记", "记录", "notes"], ["笔记", "记录", "备忘", "notes", "note", "memo"]),
    (["工作内容", "工作安排", "工作任务", "工作", "上班", "公司", "work", "job"],
     ["工作", "周报", "日报", "月报", "汇报", "任务", "需求", "方案", "计划", "会议", "纪要", "项目", "报告", "进度", "安排",
      "report", "task", "plan", "proposal", "meeting", "minutes", "project", "requirement", "weekly", "schedule"]),
    (["会议记录", "会议", "开会", "纪要", "meeting", "minutes", "agenda"], ["会议", "纪要", "会议记录", "议程", "meeting", "minutes", "agenda"]),
    (["报告", "汇报", "总结", "周报", "月报", "report", "summary"], ["报告", "汇报", "总结", "周报", "月报", "report", "summary", "review"]),
    (["合同", "协议", "contract", "agreement"], ["合同", "协议", "条款", "contract", "agreement", "terms"]),
    (["发票", "收据", "报销", "账单", "invoice", "receipt", "bill"], ["发票", "收据", "报销", "账单", "invoice", "receipt", "bill", "payment"]),
    (["报税", "税务", "退税", "个税", "tax", "tax return"], ["报税", "税务", "退税", "个税", "tax", "ato", "payg"]),
    (["银行", "流水", "对账单", "bank statement", "bank"], ["银行", "流水", "对账单", "bank", "statement"]),
    (["移民", "签证", "visa", "immigration"], ["移民", "签证", "护照", "材料", "visa", "immigration", "passport", "coe", "skills assessment"]),
    (["成绩单", "成绩", "学历", "毕业证", "transcript", "grades", "results"],
     ["成绩单", "成绩", "学历", "毕业证", "学位", "transcript", "statement of results", "academic record", "grades", "degree", "certificate"]),
    (["旅行", "旅游", "出差", "行程", "机票", "酒店", "travel", "trip", "flight", "hotel", "itinerary"],
     ["旅行", "旅游", "行程", "机票", "酒店", "车票", "出差", "travel", "trip", "itinerary", "flight", "booking", "hotel", "boarding pass", "ticket"]),
    (["租房", "房租", "租约", "lease", "rent", "rental", "tenancy"], ["租房", "房租", "租约", "押金", "lease", "rent", "rental", "tenancy", "bond"]),
    (["论文", "文献", "paper", "thesis", "article"], ["论文", "文献", "研究", "paper", "thesis", "journal", "article", "research"]),
    (["报价单", "报价", "quote", "quotation"], ["报价", "报价单", "价格", "quote", "quotation", "pricing", "price list"]),
    (["身份证", "证件", "驾照", "护照", "passport", "id card", "driver licence", "driver license"],
     ["身份证", "证件", "驾照", "护照", "passport", "id", "licence", "license"]),
    (["方案", "计划书", "计划", "策划", "proposal", "plan", "strategy"], ["方案", "计划", "策划", "plan", "proposal", "strategy", "roadmap"]),
    (["预算", "财务", "利润", "测算", "成本", "budget", "finance", "profit", "cost"], ["预算", "财务", "利润", "测算", "成本", "budget", "finance", "profit", "forecast", "cost"]),
    (["需求分析", "需求", "requirement", "requirements", "spec"], ["需求", "requirement", "specification", "spec", "prd", "elicitation"]),
    (["操作系统", "operating system"], ["操作系统", "operating system", "os"]),
    (["数据库", "database"], ["数据库", "database", "sql"]),
    (["计算机网络", "网络", "network", "networking"], ["网络", "network", "networking"]),
    (["数据结构", "算法", "algorithm", "data structure"], ["算法", "数据结构", "algorithm", "data structure"]),
    (["机器学习", "深度学习", "人工智能", "machine learning", "deep learning", "artificial intelligence"],
     ["机器学习", "深度学习", "人工智能", "machine learning", "deep learning", "artificial intelligence", "ai", "neural"]),
    (["软件工程", "software engineering"], ["软件工程", "software engineering"]),
    (["编程", "programming"], ["编程", "programming"]),
    (["统计", "statistics"], ["统计", "statistics", "statistical"]),
    (["经济", "economics"], ["经济", "economics", "economy"]),
    (["会计", "accounting"], ["会计", "accounting"]),
    (["营销", "市场", "marketing", "market"], ["市场", "营销", "调研", "market", "marketing"]),
    (["病历", "体检", "医院", "medical", "health"], ["病历", "体检", "医院", "medical", "health", "hospital"]),
    (["保险", "insurance"], ["保险", "insurance", "policy"]),
    (["证书", "certificate"], ["证书", "certificate"]),
    (["设计稿", "设计", "design", "mockup"], ["设计", "设计稿", "design", "mockup"]),
    (["密钥", "秘钥", "私钥", "公钥", "密码", "口令", "凭证", "api key", "apikey", "access key", "secret key", "private key", "ssh key", "token", "password", "credential", "secret"],
     ["密钥", "秘钥", "私钥", "密码", "凭证", "key", "keys", "api key", "apikey", "token", "secret", "credential", "password"]),
    ]

    /// The triggers, longest first (ties keep the table's order), Chinese and Japanese anywhere, Latin as whole
    /// words that may be plural (“lectures”, “invoices”).
    private static let concepts: [(NSRegularExpression, [String])] = {
        var entries: [(trigger: String, forms: [String], index: Int)] = []
        for (index, group) in conceptGroups.enumerated() {
            for trigger in group.triggers { entries.append((trigger, group.forms, index)) }
        }
        entries.sort { a, b in a.trigger.count != b.trigger.count ? a.trigger.count > b.trigger.count : a.index < b.index }
        return entries.map { entry -> (NSRegularExpression, [String]) in
            let escaped = NSRegularExpression.escapedPattern(for: entry.trigger).replacingOccurrences(of: " ", with: "\\s+")
            let latin = entry.trigger.unicodeScalars.allSatisfy { $0.isASCII }
            let pattern = latin ? "(?<![\\p{L}\\p{N}])" + escaped + "(?:s|es)?(?![\\p{L}\\p{N}])" : escaped
            return (regex(pattern), entry.forms)
        }
    }()

    /// Latin words of three letters or digits or fewer (“os”, “cv”, “uni”) are matched as whole words, since a
    /// prefix would find every “oscar” and “photos”.
    static func isShortLatin(_ form: String) -> Bool {
        form.count <= 3 && form.unicodeScalars.allSatisfy { $0.isASCII && CharacterSet.alphanumerics.contains($0) }
    }

    /// Extensions that are clearly file types (not words such as “key” or “pages”); spelling variants travel together.
    private static let extensionGroups: [[String]] = [
        ["jpg", "jpeg"], ["png"], ["heic"], ["gif"], ["webp"], ["tif", "tiff"], ["svg"], ["psd"], ["raw", "dng", "cr2", "nef", "arw"],
        ["mp4"], ["mov"], ["m4v"], ["avi"], ["mkv"], ["mp3"], ["m4a"], ["wav"], ["aac"], ["flac"], ["zip"], ["rar"], ["7z"], ["dmg"],
        ["docx"], ["xlsx"], ["xls"], ["csv"], ["pptx"], ["txt"], ["md", "markdown"], ["json"], ["swift"], ["py"], ["js"], ["ts"],
        ["html", "htm"], ["epub"], ["sketch"], ["fig"], ["srt"], ["ics"], ["vcf"],
    ]

    private static func regex(_ pattern: String) -> NSRegularExpression {
        try! NSRegularExpression(pattern: pattern, options: [.caseInsensitive])
    }
    private static let sizeUnit = "(\\d+(?:\\.\\d+)?)\\s*(?:(k|m|g|t)b?|(兆))(?![a-z])"
    private static let minSize = regex("(?:大于|超过|多于|高于|至少|不小于|>=?|bigger than|larger than|more than|greater than|over|above|at least)\\s*" + sizeUnit)
    private static let maxSize = regex("(?:小于|不到|少于|低于|不超过|不大于|<=?|smaller than|less than|under|below|at most)\\s*" + sizeUnit)
    private static let quoted = regex("[\"“”「」『』‘’]([^\"“”「」『』‘’]+)[\"“”「」『』‘’]")

    static func number(_ text: String) -> Int? {
        let text = text.lowercased().trimmingCharacters(in: .whitespaces)
        if let value = Int(text) { return value }
        let words = ["a": 1, "an": 1, "one": 1, "two": 2, "three": 3, "four": 4, "five": 5, "six": 6, "seven": 7, "eight": 8, "nine": 9,
                     "ten": 10, "a few": 3, "few": 3, "a couple of": 2, "couple of": 2, "几": 3]
        if let value = words[text] { return value }
        let digits: [Character: Int] = ["一": 1, "二": 2, "两": 2, "三": 3, "四": 4, "五": 5, "六": 6, "七": 7, "八": 8, "九": 9]
        let chars = Array(text)
        switch chars.count {
        case 1: return chars[0] == "十" ? 10 : digits[chars[0]]
        case 2 where chars[0] == "十": return digits[chars[1]].map { 10 + $0 }
        case 2 where chars[1] == "十": return digits[chars[0]].map { $0 * 10 }
        case 3 where chars[1] == "十": if let tens = digits[chars[0]], let ones = digits[chars[2]] { return tens * 10 + ones }; return nil
        default: return nil
        }
    }

    /// Reads a request with the built-in rules. Works on every Mac and gives the same plan for the same words.
    static func parse(_ request: String) -> Plan {
        var plan = Plan()
        var topic: [String] = []
        var wantsBig = false
        var text = request.applyingTransform(.fullwidthToHalfwidth, reverse: false) ?? request
        func matches(_ expression: NSRegularExpression) -> [NSTextCheckingResult] {
            expression.matches(in: text, range: NSRange(text.startIndex..., in: text))
        }
        func group(_ match: NSTextCheckingResult, _ index: Int) -> String? {
            guard index < match.numberOfRanges, let range = Range(match.range(at: index), in: text) else { return nil }
            return String(text[range])
        }
        /// Blanks what a rule has read, so it is not read again as a search word.
        func consume(_ expression: NSRegularExpression) {
            text = expression.stringByReplacingMatches(in: text, range: NSRange(text.startIndex..., in: text), withTemplate: " ")
        }

        for match in matches(quoted) {
            if let phrase = group(match, 1)?.trimmingCharacters(in: .whitespaces), !phrase.isEmpty {
                plan.terms.append(Term([phrase], exact: true))
                topic.append(phrase)
            }
        }
        consume(quoted)
        text = text.lowercased()
        // Separate Han and Latin runs, so “报价单pdf” reads as two words.
        text = regex("(\\p{Han}|\\p{Hiragana}|\\p{Katakana})([a-z0-9])").stringByReplacingMatches(in: text, range: NSRange(text.startIndex..., in: text), withTemplate: "$1 $2")
        text = regex("([a-z0-9])(\\p{Han}|\\p{Hiragana}|\\p{Katakana})").stringByReplacingMatches(in: text, range: NSRange(text.startIndex..., in: text), withTemplate: "$1 $2")

        for (expression, isMinimum) in [(minSize, true), (maxSize, false)] {
            guard let match = matches(expression).first, let value = group(match, 1).flatMap(Double.init) else { continue }
            // 兆 (group 3) is megabytes, like a bare “m”.
            let scale: Double = switch group(match, 2) ?? "m" {
            case "k": 1_000
            case "g": 1_000_000_000
            case "t": 1_000_000_000_000
            default: 1_000_000
            }
            let bytes = Int64(value * scale)
            if isMinimum { plan.minBytes = bytes } else { plan.maxBytes = bytes }
            consume(expression)
        }
        // “大图片”, “很大的视频”, “小文件”: 大/小 right before a kind or 的.
        for (expression, big) in [(regex("(?:很|超)?大(?=图片|照片|图|视频|影片|文件|文档|压缩包|的)"), true),
                                  (regex("(?:很)?小(?=图片|照片|图|视频|文件|文档|的)"), false)] where !matches(expression).isEmpty {
            if big { wantsBig = true } else { plan.maxBytes = plan.maxBytes ?? 1_000_000 }
            consume(expression)
        }
        plan.period = readTime(&text)
        for group in extensionGroups {
            let expression = regex("(?<![\\p{L}\\p{N}])\\.?(?:" + group.joined(separator: "|") + ")(?![\\p{L}\\p{N}])")
            guard !matches(expression).isEmpty else { continue }
            plan.extensions += group
            consume(expression)
        }
        // Concepts before the fixed words, so “找工作” isn't read as the filler “找” plus “工作”.
        for (expression, forms) in concepts where !matches(expression).isEmpty {
            if !plan.terms.contains(where: { $0.forms == forms }) { plan.terms.append(Term(forms)) }
            topic += matches(expression).compactMap { group($0, 0) }
            consume(expression)
        }
        for (expression, cue) in lexicon where !matches(expression).isEmpty {
            switch cue {
            case .kind(let kind): if !plan.kinds.contains(kind) { plan.kinds.append(kind) }
            case .place(let place): plan.place = plan.place ?? place
            case .field(let field): plan.dateField = field
            case .big: wantsBig = true
            case .small: plan.maxBytes = plan.maxBytes ?? 1_000_000
            case .received:
                plan.received = true
                plan.dateField = .added
            case .filler: break
            }
            consume(expression)
        }
        // A screenshot is an image already; asking for both means screenshots.
        if plan.kinds.contains(.screenshot) { plan.kinds.removeAll { $0 == .image } }
        // “Big” depends on the kind: a 20 MB photo is big, a 20 MB video is not.
        if wantsBig, plan.minBytes == nil {
            let small: Set<Kind> = [.image, .screenshot, .pdf, .document, .spreadsheet, .presentation, .text, .code]
            plan.minBytes = !plan.kinds.isEmpty && plan.kinds.allSatisfy(small.contains) ? 10_000_000 : 100_000_000
        }
        if !plan.extensions.isEmpty { plan.kinds.removeAll { $0 != .screenshot } }

        // Pieces of a time the reader couldn't place (“下午”, “那两天”) are not something the file mentions.
        let leftover = words(in: text).filter { !isTimeResidue($0) }
        for word in leftover where !plan.terms.contains(where: { $0.forms.contains(word) }) {
            plan.terms.append(Term([word]))
        }
        plan.terms = Array(plan.terms.prefix(6))
        plan.topic = (topic + leftover).joined(separator: " ")
        return plan
    }

    /// What is left after the rules: Latin words of two letters or more, numbers, and Chinese or Japanese words
    /// (split with the system tokenizer; a lone character stays only when it is all there is, as in “猫的照片”).
    static func words(in text: String) -> [String] {
        let separators = CharacterSet.whitespacesAndNewlines.union(.punctuationCharacters).union(.symbols)
        var result: [String] = []
        for piece in text.components(separatedBy: separators) where !piece.isEmpty {
            if piece.unicodeScalars.contains(where: { $0.properties.isIdeographic || (0x3040...0x30FF).contains($0.value) }) {
                let tokenizer = NLTokenizer(unit: .word)
                tokenizer.string = piece
                tokenizer.setLanguage(piece.unicodeScalars.contains { (0x3040...0x30FF).contains($0.value) } ? .japanese : .simplifiedChinese)
                let tokens = tokenizer.tokens(for: piece.startIndex..<piece.endIndex).map { String(piece[$0]) }
                let long = tokens.filter { $0.count >= 2 }
                result += long.isEmpty ? tokens : long
            } else if piece.count >= 2 || piece.allSatisfy(\.isNumber) {
                result.append(piece)
            }
        }
        var seen = Set<String>()
        return result.filter { seen.insert($0).inserted }
    }

    // MARK: - The on-device model

    enum ModelStatus: Equatable { case ready, off, notEligible, notReady, needsMacOS26 }

    static var modelStatus: ModelStatus {
        #if canImport(FoundationModels)
        if #available(macOS 26.0, *) {
            switch SystemLanguageModel.default.availability {
            case .available: return .ready
            case .unavailable(.appleIntelligenceNotEnabled): return .off
            case .unavailable(.deviceNotEligible): return .notEligible
            default: return .notReady
            }
        }
        #endif
        return .needsMacOS26
    }

    static var modelReady: Bool { modelStatus == .ready }

    /// Loads the model while the panel opens, so the first request doesn't wait for it.
    @MainActor static func prewarm() {
        #if canImport(FoundationModels)
        if #available(macOS 26.0, *), modelReady { FileSearchModel.prewarm() }
        #endif
    }

    /// Lets the on-device model choose the search words, keeping the rules' type, time, folder and size. Returns the
    /// plan unchanged when the model is unavailable, slow (over 3 s) or answers with nothing usable.
    @MainActor static func refine(_ plan: Plan, request: String) async -> Plan {
        #if canImport(FoundationModels)
        if #available(macOS 26.0, *), modelReady, let session = FileSearchModel.takeSession() {
            let forms = await withTimeout(3) { try await FileSearchModel.terms(for: request, in: session) }
            if let forms { return merge(plan, modelTerms: forms) }
        }
        #endif
        return plan
    }

    /// Keeps the model's words that are real search words: a word the rules read as a type, time, folder or filler
    /// (“PDF”, “last week”, “文件”) is dropped, as are duplicates and very long forms. Quoted phrases and the rules'
    /// concepts (“找工作” → résumés, cover letters…) stay; a model term that names the same thing is left out, so
    /// the strict query doesn't need both.
    static func merge(_ plan: Plan, modelTerms: [[String]]) -> Plan {
        var terms: [Term] = []
        var seen = Set<String>()
        for forms in modelTerms.prefix(5) {
            var kept: [String] = []
            for form in forms.prefix(6) {
                let form = form.trimmingCharacters(in: .whitespacesAndNewlines.union(.punctuationCharacters))
                guard !form.isEmpty, form.count <= 40, !parse(form).terms.isEmpty, seen.insert(form.lowercased()).inserted else { continue }
                kept.append(form)
            }
            if !kept.isEmpty { terms.append(Term(kept)) }
        }
        guard !terms.isEmpty else { return plan }
        var refined = plan
        // Quoted phrases are the user's own exact words; the model only adds to them.
        let kept = plan.terms.filter { $0.exact || concept(of: $0) != nil }
        let known = Set(kept.flatMap { $0.forms + (concept(of: $0)?.triggers ?? []) }.map { $0.lowercased() })
        refined.terms = Array((kept + terms.filter { term in !term.forms.contains { known.contains($0.lowercased()) } }).prefix(6))
        refined.understood = true
        return refined
    }

    /// The concept group a term came from, if the rules made it from one.
    private static func concept(of term: Term) -> (triggers: [String], forms: [String])? {
        term.exact ? nil : conceptGroups.first { $0.forms == term.forms }
    }

    static func withTimeout<T: Sendable>(_ seconds: Double, _ work: @escaping @Sendable () async throws -> T) async -> T? {
        await withTaskGroup(of: T?.self) { group in
            group.addTask { try? await work() }
            group.addTask { try? await Task.sleep(for: .seconds(seconds)); return nil }
            let first = await group.next() ?? nil
            group.cancelAll()
            return first
        }
    }

    // MARK: - Spotlight

    /// Spotlight's query language: a literal inside double quotes, with its own wildcard escaped.
    static func literal(_ text: String) -> String {
        text.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"").replacingOccurrences(of: "*", with: "\\*")
    }

    /// “invoices” also finds “invoice”: Latin plurals are looked up by their singular as well.
    static func stem(_ word: String) -> String {
        guard word.unicodeScalars.allSatisfy({ $0.isASCII }), word.count > 3 else { return word }
        if word.hasSuffix("ies"), word.count > 4 { return String(word.dropLast(3)) + "y" }
        if word.hasSuffix("s"), !word.hasSuffix("ss"), !word.hasSuffix("us"), !word.hasSuffix("is") { return String(word.dropLast()) }
        return word
    }

    private static func termClause(_ term: Term) -> String {
        var spellings: [String] = []
        for form in term.forms { for spelling in [form, stem(form)] where !spellings.contains(spelling) { spellings.append(spelling) } }
        let clauses = spellings.flatMap { spelling -> [String] in
            let word = literal(spelling)
            // Short Latin words (“os”, “cv”) are whole words everywhere: “*os*” would find every “photos”.
            if isShortLatin(spelling) {
                return ["kMDItemFSName", "kMDItemTitle", "kMDItemWhereFroms", "kMDItemAuthors", "kMDItemTextContent"]
                    .map { "\($0) == \"\(word)\"cdw" }
            }
            // Text is matched as a word prefix (`w`): Spotlight's word index covers Chinese and Japanese too, finds the
            // same files as a substring search and answers about ten times faster.
            return ["kMDItemFSName == \"*\(word)*\"cd", "kMDItemTitle == \"*\(word)*\"cd", "kMDItemWhereFroms == \"*\(word)*\"cd",
                    "kMDItemAuthors == \"*\(word)*\"cd", "kMDItemTextContent == \"\(word)*\"cdw"]
        }
        return "(" + clauses.joined(separator: " || ") + ")"
    }

    private static let iso: ISO8601DateFormatter = {
        let formatter = ISO8601DateFormatter()
        formatter.timeZone = TimeZone(identifier: "UTC")
        return formatter
    }()

    /// The Spotlight query for a plan: every filter, and every term (`strict`) or any one of them. Nil when the
    /// plan asks for nothing.
    static func queryString(_ plan: Plan, strict: Bool = true, now: Date = Date(), calendar: Calendar = .current) -> String? {
        var parts: [String] = []
        if !plan.extensions.isEmpty {
            parts.append("(" + plan.extensions.map { "kMDItemFSName == \"*.\(literal($0))\"c" }.joined(separator: " || ") + ")")
        } else if !plan.kinds.isEmpty {
            let kinds = plan.kinds.map { kind in
                kind == .screenshot ? "kMDItemIsScreenCapture == 1"
                    : "(" + kind.contentTypes.map { "kMDItemContentTypeTree == \"\($0)\"" }.joined(separator: " || ") + ")"
            }
            parts.append("(" + kinds.joined(separator: " || ") + ")")
        }
        if let period = plan.period {
            let range = period.window(now: now, calendar: calendar)
            parts.append("\(plan.dateField.attribute) >= $time.iso(\(iso.string(from: range.start)))")
            parts.append("\(plan.dateField.attribute) < $time.iso(\(iso.string(from: range.end)))")
        }
        if let minBytes = plan.minBytes { parts.append("kMDItemFSSize >= \(minBytes)") }
        if let maxBytes = plan.maxBytes { parts.append("kMDItemFSSize <= \(maxBytes)") }
        if plan.received, plan.place == nil { parts.append(receivedClause) }
        if !plan.terms.isEmpty {
            let terms = plan.terms.map(termClause)
            parts.append(strict ? terms.joined(separator: " && ") : "(" + terms.joined(separator: " || ") + ")")
        }
        return parts.isEmpty ? nil : parts.joined(separator: " && ")
    }

    /// Files that remember where they came from: browsers, Mail, AirDrop and Messages record the sender or address
    /// (kMDItemWhereFroms) or the download date when they save a file.
    static let receivedClause = "(kMDItemWhereFroms == \"*\" || kMDItemDownloadedDate >= $time.iso(1970-01-01T00:00:00Z))"

    static func scopes(_ plan: Plan) -> [Any] {
        if let place = plan.place { return [place.url] }
        var scopes: [Any] = [NSMetadataQueryUserHomeScope]
        if plan.kinds.contains(.app) { scopes += ["/Applications", "/System/Applications"] }
        return scopes
    }

    /// The Spotlight searches a plan needs: each query with where it looks. Usually one; “sent to me” is two, since
    /// a query can't test a path: files that say where they came from anywhere in the home folder, plus everything in
    /// Downloads (some apps and tools save there without that note). Results are merged by path.
    static func searches(_ plan: Plan, strict: Bool = true, now: Date = Date(), calendar: Calendar = .current) -> [(query: String, scopes: [Any])] {
        guard let query = queryString(plan, strict: strict, now: now, calendar: calendar) else { return [] }
        var searches: [(query: String, scopes: [Any])] = [(query, scopes(plan))]
        if plan.received, plan.place == nil {
            var downloads = plan
            downloads.received = false
            downloads.place = .downloads
            // Nothing else asked for (“领导发的”): everything in Downloads.
            searches.append((queryString(downloads, strict: strict, now: now, calendar: calendar) ?? "kMDItemFSName == \"*\"", scopes(downloads)))
        }
        return searches
    }

    /// Leaves out what people don't mean by “my files”: hidden items, the insides of apps, libraries and other
    /// packages, ~/Library (but not iCloud Drive), system folders and build output.
    static func isVisible(_ path: String, home: String = NSHomeDirectory()) -> Bool {
        let components = path.split(separator: "/")
        if components.contains(where: { $0.hasPrefix(".") || ["node_modules", "DerivedData", "Pods"].contains($0)
                                        || ["anaconda3", "miniconda3", "site-packages", "venv"].contains($0.lowercased()) }) { return false }
        let packages: Set<String> = ["app", "bundle", "framework", "plugin", "appex", "kext", "photoslibrary", "musiclibrary", "tvlibrary",
                                     "imovielibrary", "fcpbundle", "logicx", "xcarchive", "lrdata", "pkg", "key", "pages", "numbers", "rtfd"]
        if components.dropLast().contains(where: { packages.contains(($0.split(separator: ".").last ?? "").lowercased()) && $0.contains(".") }) { return false }
        if path.hasPrefix(home + "/Library/") { return path.hasPrefix(home + "/Library/Mobile Documents/") }
        if path.hasPrefix("/System/") { return path.hasPrefix("/System/Applications/") }
        return !["/Library/", "/private/", "/usr/", "/opt/", "/bin/", "/sbin/", "/cores/", "/Volumes/Recovery"].contains { path.hasPrefix($0) }
    }

    /// Whether a name or folder mentions a form: short Latin words (“os”, “cv”, “uni”) only as words of their own,
    /// so “photos” doesn't mention “os”; everything else anywhere (by its singular).
    static func mentions(_ text: String, _ form: String) -> Bool {
        guard isShortLatin(form) else { return text.localizedStandardContains(stem(form)) }
        let pattern = "(?<![\\p{L}])" + NSRegularExpression.escapedPattern(for: form) + "(?![\\p{L}])"
        return text.range(of: pattern, options: [.regularExpression, .caseInsensitive]) != nil
    }

    /// Machine files whose text happens to contain a word (source code, JSON, logs) rarely are the file someone means.
    private static let machineTypes: Set<String> = ["public.source-code", "public.json", "public.xml", "public.log", "public.yaml",
                                                     "public.script", "com.netscape.javascript-source"]

    /// Generated files people rarely look for by hand.
    private static let cacheFolders: Set<String> = ["cache", "caches", ".cache", "tmp", "temp", "render_tmp", "build", "dist", "out"]

    /// Orders hits: everything-matched first, then names that contain the words, then what was used or changed most
    /// recently, then shallower paths. Code and data files matched only by their text sink, unless code was asked for.
    static func rank(_ hits: [Hit], plan: Plan, now: Date = Date(), home: String = NSHomeDirectory(),
                     calendar: Calendar = .current) -> [Hit] {
        // The search looks a little wider than the time named; files inside it come first.
        let named = plan.period?.range(now: now, calendar: calendar)
        func score(_ hit: Hit) -> Double {
            var score = hit.strict ? 100.0 : 0
            if let named, let date = hit.date(for: plan.dateField), date >= named.start, date < named.end { score += 15 }
            // A folder named after the words (“~/工作”, “Uni”) counts for a little less than the name. Spotlight
            // can't match folders in the query, so here it only orders what the words found elsewhere.
            var folder = hit.url.deletingLastPathComponent().path
            if folder.hasPrefix(home) { folder = String(folder.dropFirst(home.count)) }
            var inName = 0, inFolder = 0
            for term in plan.terms {
                if term.forms.contains(where: { mentions(hit.name, $0) }) { inName += 1 }
                else if term.forms.contains(where: { mentions(folder, $0) }) { inFolder += 1 }
            }
            score += Double(inName) * 25 + Double(inFolder) * 12
            if !plan.terms.isEmpty, inName == plan.terms.count { score += 10 }
            if !plan.terms.isEmpty, inName == 0, !plan.kinds.contains(.code), hit.types.contains(where: machineTypes.contains) { score -= 60 }
            // Installers aren't what “工作内容” or “the quote” means.
            if ["dmg", "pkg"].contains(hit.url.pathExtension.lowercased()), plan.extensions.isEmpty,
               !plan.kinds.contains(.app), !plan.kinds.contains(.archive) { score -= 40 }
            // Closeness in meaning: nothing below “related”, up to 70 for a very close match.
            if hit.meaning > 0 { score += min(1, max(0, (hit.meaning - SemanticIndex.bonusFrom) / (SemanticIndex.bonusTo - SemanticIndex.bonusFrom))) * 70 }
            if let latest = hit.latest { score += 20 * exp(-max(0, now.timeIntervalSince(latest)) / 86_400 / 45) }
            if let used = hit.used, now.timeIntervalSince(used) < 14 * 86_400 { score += 5 }
            score -= Double(hit.url.pathComponents.count) * 0.3
            if hit.url.pathComponents.dropLast().contains(where: { cacheFolders.contains($0.lowercased()) }) { score -= 15 }
            return score
        }
        let scored = hits.map { ($0, score($0)) }
        return scored.sorted { a, b in
            if a.1 != b.1 { return a.1 > b.1 }
            if a.0.latest != b.0.latest { return (a.0.latest ?? .distantPast) > (b.0.latest ?? .distantPast) }
            return a.0.name.localizedStandardCompare(b.0.name) == .orderedAscending
        }.map(\.0)
    }

    /// Runs the plan against Spotlight. When the strict query finds few files, looser ones fill in below it: any of
    /// the words, then (when the request also named a type, folder or sender) the files that fit those alone, since
    /// abstract words (“工作内容”) often aren't in the file at all.
    @MainActor static func search(_ plan: Plan, limit: Int = 30, now: Date = Date()) async -> [Hit] {
        let strict = searches(plan, strict: true, now: now)
        guard !strict.isEmpty else { return [] }
        var hits: [Hit] = []
        var known = Set<URL>()
        func add(_ found: [Hit], strict: Bool) {
            for var hit in found where isVisible(hit.url.path) && known.insert(hit.url).inserted {
                hit.strict = strict
                hits.append(hit)
            }
        }
        add(await run(strict), strict: true)
        if hits.count < 8, plan.terms.count > 1, !Task.isCancelled { add(await run(searches(plan, strict: false, now: now)), strict: false) }
        // A time alone (“最近的大学文件”) would only bring unrelated recent files.
        if hits.count < 8, !plan.terms.isEmpty, plan.received || plan.place != nil || !plan.kinds.isEmpty || !plan.extensions.isEmpty,
           !Task.isCancelled {
            add(await run(searches(filtersOnly(plan), now: now)), strict: false)
        }
        if !plan.terms.isEmpty, !plan.topic.isEmpty, SemanticIndex.enabled, !Task.isCancelled {
            let closest = await Task.detached(priority: .userInitiated) {
                SemanticIndex.closest(topic: plan.topic, terms: plan.terms.map(\.forms)) { FileSearch.fits($0, plan: plan, now: now) }
            }.value
            hits = addMeaning(closest, to: hits)
        }
        return Array(rank(hits, plan: plan, now: now).prefix(limit))
    }

    /// The hits of each search, in order (a path may come more than once).
    @MainActor private static func run(_ searches: [(query: String, scopes: [Any])]) async -> [Hit] {
        var found: [Hit] = []
        for search in searches where !Task.isCancelled {
            guard let predicate = NSPredicate(fromMetadataQueryString: search.query) else { continue }
            found += await SpotlightQuery().run(predicate, scopes: search.scopes)
        }
        return found
    }

    /// Files close to the request in meaning: their similarity on the hits found by words, and the closest few the
    /// words missed (within the request's type, time, folder and sender), below the best keyword matches.
    static func addMeaning(_ closest: [(entry: SemanticIndex.Entry, similarity: Double)], to hits: [Hit]) -> [Hit] {
        guard let best = closest.first?.similarity else { return hits }
        let similarity = Dictionary(closest.map { ($0.entry.path, $0.similarity) }, uniquingKeysWith: max)
        var hits = hits.map { hit -> Hit in
            var hit = hit
            hit.meaning = similarity[hit.url.path] ?? 0
            return hit
        }
        var known = Set(hits.map(\.url.path)), added = 0
        for (entry, value) in closest {
            // Only clearly related files, and only those near the best one: a weak best match brings nothing.
            if value < SemanticIndex.related || value < best - SemanticIndex.margin || added >= SemanticIndex.maxExtra { break }
            var info = stat()
            guard !known.contains(entry.path), lstat(entry.path, &info) == 0 else { continue }
            known.insert(entry.path)
            let url = URL(fileURLWithPath: entry.path)
            hits.append(Hit(url: url, name: url.lastPathComponent, changed: entry.modified, added: entry.added, strict: false, meaning: value))
            added += 1
        }
        return hits
    }

    /// Whether an indexed file passes the request's type, size, folder, sender and time (the index only holds
    /// documents, and keeps no last-opened date: the newer of changed and added stands in for it).
    static func fits(_ entry: SemanticIndex.Entry, plan: Plan, now: Date = Date(), calendar: Calendar = .current) -> Bool {
        let ext = (entry.path as NSString).pathExtension.lowercased()
        if !plan.extensions.isEmpty, !plan.extensions.contains(ext) { return false }
        if plan.extensions.isEmpty, !plan.kinds.isEmpty {
            guard let type = UTType(filenameExtension: ext),
                  plan.kinds.contains(where: { $0 != .screenshot && $0.contentTypes.contains { UTType($0).map(type.conforms) ?? false } }) else { return false }
        }
        if let minBytes = plan.minBytes, entry.size < minBytes { return false }
        if let maxBytes = plan.maxBytes, entry.size > maxBytes { return false }
        if let place = plan.place, !entry.path.hasPrefix(place.url.path + "/") { return false }
        if plan.received, plan.place == nil, !entry.received, !entry.path.hasPrefix(Place.downloads.url.path + "/") { return false }
        if let period = plan.period {
            let range = period.window(now: now, calendar: calendar)
            let date: Date = switch plan.dateField {
            case .changed: entry.modified
            case .used: max(entry.modified, entry.added)
            case .added: entry.added
            case .created: entry.created
            }
            if date < range.start || date >= range.end { return false }
        }
        return true
    }

    /// The same plan without its words.
    static func filtersOnly(_ plan: Plan) -> Plan {
        var filters = plan
        filters.terms = []
        return filters
    }

    /// “~/Downloads · Yesterday” under a result. When the request named a time, the date shown is the one it
    /// filtered on (modified, opened or added), so “last week” never shows “yesterday”.
    static func describe(_ hit: Hit, plan: Plan? = nil, now: Date = Date()) -> String {
        let folder = (hit.url.deletingLastPathComponent().path as NSString).abbreviatingWithTildeInPath
        let filtered = plan.flatMap { $0.period == nil ? nil : hit.date(for: $0.dateField) }
        guard let date = filtered ?? hit.latest else { return folder }
        return folder + " · " + when(date, now: now)
    }

    /// “3 hours ago” today; after that calendar days (“Yesterday”, “25 Oct 2026”), since “1 day ago” for last
    /// Sunday night reads like the wrong week.
    static func when(_ date: Date, now: Date = Date(), calendar: Calendar = .current) -> String {
        let locale = Language.resolved.locale
        if calendar.isDate(date, inSameDayAs: now), date <= now {
            let relative = RelativeDateTimeFormatter()
            relative.unitsStyle = .full
            relative.locale = locale
            return relative.localizedString(for: date, relativeTo: now)
        }
        let formatter = DateFormatter()
        formatter.locale = locale
        formatter.calendar = calendar
        formatter.timeZone = calendar.timeZone
        formatter.doesRelativeDateFormatting = true
        formatter.dateStyle = .medium
        formatter.timeStyle = .none
        return formatter.string(from: date)
    }
}

extension FileSearch.ModelStatus {
    var title: String {
        switch self {
        case .ready: L("Apple Intelligence also reads your request, on this Mac.")
        case .off: L("Turn on Apple Intelligence in System Settings to have the on-device model read requests too. Until then the built-in rules read them.")
        case .notEligible: L("This Mac can't run Apple Intelligence, so the built-in rules read requests.")
        case .notReady: L("Apple Intelligence is still getting ready. The built-in rules read requests meanwhile.")
        case .needsMacOS26: L("On macOS 26 with Apple Intelligence, the on-device model can read requests too. Until then the built-in rules read them.")
        }
    }
}

extension Language {
    /// For dates and numbers written in the app's language rather than the system's.
    var locale: Locale {
        switch self {
        case .system: .current
        case .en: Locale(identifier: "en")
        case .zhHans: Locale(identifier: "zh-Hans")
        case .zhHant: Locale(identifier: "zh-Hant")
        case .ja: Locale(identifier: "ja")
        }
    }
}

/// One Spotlight query, gathered once (no live updates), with a time limit.
@MainActor
final class SpotlightQuery {
    private let query = NSMetadataQuery()
    /// Reads the first `limit` results and resumes the caller.
    private var done: ((Int) -> Void)?
    private var observer: NSObjectProtocol?
    private var timeout: Task<Void, Never>?

    func run(_ predicate: NSPredicate, scopes: [Any], limit: Int = 400, seconds: Double = 4) async -> [FileSearch.Hit] {
        await items(predicate, scopes: scopes, limit: limit, seconds: seconds) { item, path in
            FileSearch.Hit(url: URL(fileURLWithPath: path), name: item.value(forAttribute: NSMetadataItemFSNameKey) as? String ?? (path as NSString).lastPathComponent,
                           changed: item.value(forAttribute: NSMetadataItemFSContentChangeDateKey) as? Date,
                           used: item.value(forAttribute: "kMDItemLastUsedDate") as? Date,
                           added: item.value(forAttribute: "kMDItemDateAdded") as? Date,
                           types: item.value(forAttribute: "kMDItemContentTypeTree") as? [String] ?? [])
        }
    }

    /// The results, newest first, each made from its metadata item and path (nil leaves it out).
    func items<T: Sendable>(_ predicate: NSPredicate, scopes: [Any], limit: Int, seconds: Double,
                            _ make: @escaping (NSMetadataItem, String) -> T?) async -> [T] {
        await withTaskCancellationHandler {
            await withCheckedContinuation { (continuation: CheckedContinuation<[T], Never>) in
                done = { [query] count in
                    var results: [T] = []
                    for index in 0..<min(query.resultCount, count) {
                        guard let item = query.result(at: index) as? NSMetadataItem,
                              let path = item.value(forAttribute: NSMetadataItemPathKey) as? String, let result = make(item, path) else { continue }
                        results.append(result)
                    }
                    continuation.resume(returning: results)
                }
                query.predicate = predicate
                query.searchScopes = scopes
                query.sortDescriptors = [NSSortDescriptor(key: NSMetadataItemFSContentChangeDateKey, ascending: false)]
                observer = NotificationCenter.default.addObserver(forName: .NSMetadataQueryDidFinishGathering, object: query, queue: .main) { [weak self] _ in
                    MainActor.assumeIsolated { self?.finish(limit) }
                }
                timeout = Task { @MainActor [weak self] in
                    try? await Task.sleep(for: .seconds(seconds))
                    self?.finish(limit)
                }
                if !query.start() { finish(limit) }
            }
        } onCancel: {
            Task { @MainActor in self.finish(0) }
        }
    }

    private func finish(_ limit: Int) {
        guard let done else { return }
        self.done = nil
        timeout?.cancel()
        if let observer { NotificationCenter.default.removeObserver(observer) }
        query.disableUpdates()
        done(limit)
        query.stop()
    }
}

#if canImport(FoundationModels)
@available(macOS 26.0, *)
enum FileSearchModel {
    @Generable
    struct Reply {
        @Guide(description: "The distinctive things the wanted file's name or text probably contains: topics, names, people, companies, projects. For an abstract request, the concrete things such files are called or say instead. Leave out time, file type, size, folder, who sent it and filler words. Empty if nothing is left.", .maximumCount(5))
        var terms: [Term]
    }

    @Generable
    struct Term {
        @Guide(description: "The word as the user wrote it or a concrete name for it, the same word in English and in Simplified Chinese, then close synonyms or abbreviations. Each form is one or two words.", .maximumCount(6))
        var forms: [String]
    }

    static let instructions = """
        You help find a file on a Mac. Read the person's request and return only the words to look up in file names \
        and file contents. When the request is abstract, expand it into the concrete names and words such files have, \
        with their translations: 找工作 (job hunting) becomes resume, CV, cover letter, 简历, 推荐信; 领导给的工作内容 \
        becomes weekly report, meeting minutes, 周报, 会议纪要; 操作系统 becomes operating system, OS. Never return words \
        about when (last week, yesterday), what type (PDF, photo, video), how big, which folder (Downloads, Desktop), \
        who sent it (my boss, 领导给的) or filler (find, the, file, that one, 内容, 资料).
        """

    @MainActor private static var prepared: LanguageModelSession?

    @MainActor static func prewarm() {
        guard prepared == nil else { return }
        let session = LanguageModelSession(instructions: instructions)
        session.prewarm()
        prepared = session
    }

    /// A fresh session per request (a session keeps its transcript); the next one is warmed straight away.
    @MainActor static func takeSession() -> LanguageModelSession? {
        let session = prepared ?? LanguageModelSession(instructions: instructions)
        prepared = nil
        prewarm()
        return session
    }

    static func terms(for request: String, in session: LanguageModelSession) async throws -> [[String]] {
        let reply = try await session.respond(to: request, generating: Reply.self, options: GenerationOptions(sampling: .greedy))
        return reply.content.terms.map(\.forms)
    }
}
#endif
