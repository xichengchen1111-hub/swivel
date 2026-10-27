import Foundation
import Testing
@testable import Swivel

/// Quick Actions' file search: reading requests, building Spotlight queries, ranking and the model's words. None of
/// these touch Spotlight; `Swivel --find "<request>"` runs a real search.
struct FileSearchTests {
    private static let calendar: Calendar = {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "UTC")!
        calendar.firstWeekday = 2
        return calendar
    }()
    /// Tuesday 27 October 2026, 15:00 UTC.
    private static let now = calendar.date(from: DateComponents(year: 2026, month: 10, day: 27, hour: 15))!
    private static func day(_ month: Int, _ day: Int, _ year: Int = 2026) -> Date {
        calendar.date(from: DateComponents(year: year, month: month, day: day))!
    }
    private func words(_ plan: FileSearch.Plan) -> [String] { plan.terms.flatMap(\.forms) }

    @Test func readsAChineseRequestWithTypeTimeAndSource() {
        let plan = FileSearch.parse("上周从微信下载的那个报价单 PDF")
        #expect(plan.kinds == [.pdf])
        #expect(plan.period == .lastWeek)
        #expect(plan.dateField == .added)
        #expect(words(plan).contains("微信"))
        #expect(words(plan).contains { $0.hasPrefix("报价") })
        #expect(!words(plan).contains { ["的", "那个", "下载", "pdf", "上周"].contains($0) })
    }

    @Test func readsEnglishRequests() {
        let screenshots = FileSearch.parse("screenshots from yesterday")
        #expect(screenshots.kinds == [.screenshot] && screenshots.period == .yesterday && screenshots.terms.isEmpty)

        let videos = FileSearch.parse("big videos in downloads")
        #expect(videos.kinds == [.video] && videos.place == .downloads && videos.minBytes == 100_000_000 && videos.terms.isEmpty)

        let invoices = FileSearch.parse("invoices over 5 MB from 2024")
        #expect(invoices.terms.count == 1 && invoices.minBytes == 5_000_000 && invoices.period == .year(2024))
        #expect(Set(["invoice", "发票", "receipt"]).isSubset(of: invoices.terms.first?.forms ?? []))

        let recent = FileSearch.parse("photos I opened in the past few days")
        #expect(recent.kinds == [.image] && recent.period == .past(3, .day) && recent.dateField == .used && recent.terms.isEmpty)
    }

    @Test func readsNumbersUnitsSizesAndQuotes() {
        let sheets = FileSearch.parse("过去3天打开过的 excel")
        #expect(sheets.kinds == [.spreadsheet] && sheets.period == .past(3, .day) && sheets.dateField == .used && sheets.terms.isEmpty)
        #expect(FileSearch.parse("两周前的照片").period == .ago(2, .week))
        #expect(FileSearch.parse("小于 500kb 的图片").maxBytes == 500_000)
        #expect(FileSearch.parse("大于2G的视频").minBytes == 2_000_000_000)
        #expect(FileSearch.parse("大图片").minBytes == 10_000_000)

        let cat = FileSearch.parse("猫的照片")
        #expect(words(cat) == ["猫"] && cat.kinds == [.image])

        let quoted = FileSearch.parse("find “Q3 plan” jpg")
        #expect(quoted.terms == [FileSearch.Term(["Q3 plan"], exact: true)])
        #expect(quoted.extensions == ["jpg", "jpeg"] && quoted.kinds.isEmpty)
        #expect(FileSearch.parse("the").isEmpty && FileSearch.parse("帮我找一下那个文件").isEmpty)

        for (text, value) in [("十二", 12), ("二十", 20), ("三十五", 35), ("两", 2), ("a few", 3), ("7", 7)] {
            #expect(FileSearch.number(text) == value, "\(text)")
        }
    }

    @Test func readsAbstractRequestsAsConceptsSendersAndTranslations() throws {
        let work = FileSearch.parse("上周保存的领导给的工作内容")
        #expect(work.period == .lastWeek && work.received && work.dateField == .added)
        let concept = try #require(work.terms.count == 1 ? work.terms.first : nil, "\(words(work))")
        #expect(Set(["工作", "周报", "会议", "report", "meeting"]).isSubset(of: concept.forms))
        #expect(!words(work).contains { ["领导", "内容"].contains($0) }, "\(words(work))")
        #expect(work.summary.contains(L("Sent to you")))

        let jobs = FileSearch.parse("找工作相关的东西")
        #expect(jobs.terms.count == 1 && Set(["简历", "resume", "cv", "cover letter", "推荐信"]).isSubset(of: jobs.terms.first?.forms ?? []))

        let os = FileSearch.parse("操作系统复习资料")
        #expect(os.terms.count == 2, "\(words(os))")
        #expect(os.terms.first?.forms.contains("operating system") == true && os.terms.last?.forms.contains("cheat sheet") == true)

        let uni = FileSearch.parse("最近的大学文件")
        #expect(uni.period == .recent && uni.terms.count == 1)
        #expect(Set(["lecture", "assignment", "uni", "作业"]).isSubset(of: uni.terms.first?.forms ?? []))
        #expect(FileSearch.parse("lecture slides from last week").terms.first?.forms.contains("课件") == true)
        #expect(FileSearch.parse("files my boss sent").received)
        #expect(FileSearch.parse("移民要用的材料").terms.first?.forms.contains("visa") == true)
        #expect(!FileSearch.parse("领导发的").isEmpty && FileSearch.parse("领导发的").hasFilters)
    }

    @Test func theTopicLeavesOutTimeSenderAndFiller() throws {
        // “前两周” is the past two weeks, “存的” the date added, “一个” filler: what is left is the 密钥.
        let key = FileSearch.parse("前两周存的一个密钥")
        #expect(key.period == .past(2, .week) && key.dateField == .added && key.topic == "密钥")
        #expect(Set(["密钥", "api key", "token", "password"]).isSubset(of: key.terms.first?.forms ?? []) && key.terms.count == 1)
        #expect(FileSearch.parse("前3天的截图").period == .past(3, .day) && FileSearch.parse("前几天的截图").period == .recent)
        #expect(FileSearch.parse("my ssh key").terms.first?.forms.contains("secret") == true)

        let work = FileSearch.parse("上周保存的领导给的工作内容")
        #expect(work.topic == "工作内容")
        #expect(FileSearch.parse("找“Q3 plan”和发票 pdf").topic == "Q3 plan 发票")
        #expect(FileSearch.parse("上周的截图").topic.isEmpty && FileSearch.parse("那篇讲 agent 的论文").topic.contains("agent"))
    }

    @Test func abstractTermsMatchFoldersSendersAndShortWordsExactly() throws {
        let plan = FileSearch.parse("微信收到的操作系统 pdf")
        #expect(plan.received && plan.kinds == [.pdf])
        let searches = FileSearch.searches(plan)
        #expect(searches.count == 2)
        let query = try #require(searches.first?.query)
        // Short Latin words are whole words: “os*” would find every “oscar” and “photos”.
        #expect(query.contains(#"kMDItemTextContent == "os"cdw"#) && query.contains(#"kMDItemFSName == "os"cdw"#))
        #expect(!query.contains(#""os*""#) && !query.contains(#""*os*""#))
        #expect(query.contains(#"kMDItemTextContent == "operating system*"cdw"#))
        // Sent to me: files that say where they came from anywhere at home, plus everything in Downloads.
        #expect(query.contains(FileSearch.receivedClause))
        let downloads = try #require(searches.last)
        #expect(!downloads.query.contains("kMDItemWhereFroms == \"*\"") && (downloads.scopes.first as? URL) == FileSearch.Place.downloads.url)
        for search in searches { #expect(NSPredicate(fromMetadataQueryString: search.query) != nil, "\(search.query)") }
        let alone = FileSearch.searches(FileSearch.parse("领导发的"))
        #expect(alone.count == 2 && alone.allSatisfy { NSPredicate(fromMetadataQueryString: $0.query) != nil })
        #expect(FileSearch.filtersOnly(plan).terms.isEmpty && FileSearch.filtersOnly(plan).received)

        #expect(FileSearch.mentions("OS week 3.pdf", "os") && !FileSearch.mentions("photos.pdf", "os") && FileSearch.mentions("Invoices.pdf", "invoices"))

        // A folder the user named for the topic counts, below the name itself.
        let home = "/Users/me", now = Self.now
        func hit(_ path: String, days: Double = 0) -> FileSearch.Hit {
            FileSearch.Hit(url: URL(fileURLWithPath: path), name: (path as NSString).lastPathComponent, changed: now.addingTimeInterval(-days * 86_400))
        }
        let ranked = FileSearch.rank([hit("/Users/me/Documents/notes.docx"), hit("/Users/me/Documents/工作/week3.xlsx")],
                                     plan: FileSearch.parse("工作"), now: now, home: home)
        #expect(ranked.first?.name == "week3.xlsx")
        #expect(!FileSearch.parse("工作周报").terms.isEmpty)
        #expect(!FileSearch.isVisible("/Users/me/anaconda3/pkgs/a/test_data/www.unimelb.edu.au", home: home))
        #expect(!FileSearch.isVisible("/Users/me/code/venv/lib/python3.12/site-packages/a/readme.md", home: home))
        let installer = FileSearch.rank([hit("/Users/me/Downloads/WorkBuddy.dmg"), hit("/Users/me/Downloads/brief.pptx", days: 3)],
                                        plan: FileSearch.parse("上周收到的工作内容"), now: now, home: home)
        #expect(installer.first?.name == "brief.pptx")
    }

    @Test func periodsResolveToCalendarRanges() {
        let calendar = Self.calendar, now = Self.now
        #expect(FileSearch.Period.lastWeek.range(now: now, calendar: calendar) == (Self.day(10, 19), Self.day(10, 26)))
        #expect(FileSearch.Period.yesterday.range(now: now, calendar: calendar) == (Self.day(10, 26), Self.day(10, 27)))
        #expect(FileSearch.Period.ago(3, .day).range(now: now, calendar: calendar) == (Self.day(10, 24), Self.day(10, 25)))
        #expect(FileSearch.Period.lastMonth.range(now: now, calendar: calendar) == (Self.day(9, 1), Self.day(10, 1)))
        #expect(FileSearch.Period.year(2024).range(now: now, calendar: calendar) == (Self.day(1, 1, 2024), Self.day(1, 1, 2025)))
        let past = FileSearch.Period.past(2, .week).range(now: now, calendar: calendar)
        #expect(past.start == calendar.date(byAdding: .day, value: -14, to: now) && past.end > now)
    }

    /// Times are read from their parts, so any way of saying one works, not a list of phrases (the same cases as
    /// FileSearchTests.cs). Now is Tuesday 27 October 2026, 15:00.
    @Test func timesAreReadFromTheirPartsHoweverTheyAreSaid() {
        let calendar = Self.calendar, now = Self.now
        func d(_ month: Int, _ day: Int, _ hour: Int = 0, year: Int = 2026) -> Date {
            calendar.date(from: DateComponents(year: year, month: month, day: day, hour: hour))!
        }
        func back(_ component: Calendar.Component, _ value: Int) -> Date { calendar.date(byAdding: component, value: -value, to: now)! }
        let cases: [(String, Date, Date?)] = [
            ("前两周", back(.day, 14), nil),
            ("过去两周", back(.day, 14), nil),
            ("最近两个礼拜", back(.day, 14), nil),
            ("这两周", back(.day, 14), nil),
            ("近14天", back(.day, 14), nil),
            ("两周内", back(.day, 14), nil),
            ("过去两三周", back(.day, 21), nil),
            ("1-2周内", back(.day, 14), nil),
            ("last 3 days", back(.day, 3), nil),
            ("in the past couple of weeks", back(.day, 14), nil),
            ("past week", back(.day, 7), nil),
            ("最近一个月", back(.month, 1), nil),
            ("近三个月", back(.month, 3), nil),
            ("一个半月内", back(.day, 45), nil),
            ("半年内", back(.month, 6), nil),
            ("刚才", back(.hour, 6), nil),
            ("前阵子", back(.month, 2), nil),
            ("两周前", back(.day, 21), back(.day, 7)),
            ("两个多星期前", back(.day, 21), back(.day, 7)),
            ("a couple of weeks ago", back(.day, 21), back(.day, 7)),
            ("2週間前", back(.day, 21), back(.day, 7)),
            ("两三周前", back(.day, 28), back(.day, 14)),
            ("三天前", d(10, 24), d(10, 25)),
            ("3日前", d(10, 24), d(10, 25)),
            ("半个月前", d(10, 9), d(10, 16)),
            ("上周", d(10, 19), d(10, 26)),
            ("上一周", d(10, 19), d(10, 26)),
            ("先週", d(10, 19), d(10, 26)),
            ("上上周", d(10, 12), d(10, 19)),
            ("the week before last", d(10, 12), d(10, 19)),
            ("上周末", d(10, 24), d(10, 26)),
            ("周末", d(10, 24), d(10, 26)),
            ("last weekend", d(10, 24), d(10, 26)),
            ("上周三", d(10, 21), d(10, 22)),
            ("周五", d(10, 23), d(10, 24)),
            ("星期天", d(10, 25), d(10, 26)),
            ("last friday", d(10, 23), d(10, 24)),
            ("昨晚", d(10, 26, 18), d(10, 27, 4)),
            ("last night", d(10, 26, 18), d(10, 27, 4)),
            ("今天上午", d(10, 27), d(10, 27, 12)),
            ("this morning", d(10, 27), d(10, 27, 12)),
            ("前天下午", d(10, 25, 12), d(10, 25, 18)),
            ("上个月底", d(9, 21), d(10, 1)),
            ("end of last month", d(9, 21), d(10, 1)),
            ("月初", d(10, 1), d(10, 11)),
            ("上个季度", d(7, 1), d(10, 1)),
            ("去年年底", d(10, 1, year: 2025), d(1, 1)),
            ("2024年底", d(10, 1, year: 2024), d(1, 1, year: 2025)),
            ("今年上半年", d(1, 1), d(7, 1)),
            ("下半年", d(7, 1), nil),
            ("10月14日", d(10, 14), d(10, 15)),
            ("十月十四号", d(10, 14), d(10, 15)),
            ("14号", d(10, 14), d(10, 15)),
            ("2026-10-14", d(10, 14), d(10, 15)),
            ("14 oct", d(10, 14), d(10, 15)),
            ("去年3月", d(3, 1, year: 2025), d(4, 1, year: 2025)),
            ("去年的3月", d(3, 1, year: 2025), d(4, 1, year: 2025)),
            ("3月", d(3, 1), d(4, 1)),
            ("十二月", d(12, 1, year: 2025), d(1, 1)),
            ("early march", d(3, 1), d(3, 11)),
            ("in may", d(5, 1), d(6, 1)),
            ("国庆", d(10, 1), d(10, 11)),
            ("两个月左右前", back(.month, 3), back(.month, 1)),
            ("十来天前", d(10, 15), d(10, 20)),
            ("10.14", d(10, 14), d(10, 15)),
            ("自从3月以来", d(3, 1), nil),
            ("从上个月开始", d(9, 1), nil),
            ("since last month", d(9, 1), nil),
            ("over the last fortnight", back(.day, 14), nil),
            ("a year and a half ago", back(.month, 19), back(.month, 17)),
            ("前不久", d(10, 13), nil),
        ]
        var failures: [String] = []
        for (phrase, start, end) in cases {
            let plan = FileSearch.parse(phrase + "的合同")
            guard let period = plan.period else { failures.append("\(phrase): no time"); continue }
            let range = period.range(now: now, calendar: calendar)
            if range.start != start || (end.map { range.end != $0 } ?? (range.end <= now)) {
                failures.append("\(phrase): \(period) → \(range.start) – \(range.end)")
            }
            // Nothing of the time is left to look for: only the contract.
            if plan.terms.count != 1 || !plan.terms[0].forms.contains("contract") {
                failures.append("\(phrase): words \(plan.terms.map { $0.forms[0] })")
            }
        }
        #expect(failures.isEmpty, "\(failures.joined(separator: "\n"))")

        // Words that only look like time stay words.
        for phrase in ["周报", "月报", "日本的照片", "年终总结", "第3周的作业", "weekly report", "may be useful"] {
            #expect(FileSearch.parse(phrase).period == nil, "\(phrase)")
        }
        // Vague words widen the search; a named day doesn't.
        let vague = FileSearch.parse("两周前左右").period
        #expect(vague.map { $0.window(now: now, calendar: calendar).start < $0.range(now: now, calendar: calendar).start } == true)
        let named = FileSearch.parse("昨天").period
        #expect(named.map { $0.window(now: now, calendar: calendar) == $0.range(now: now, calendar: calendar) } == true)
        // South of the equator summer starts in December.
        #expect(FileSearch.seasonStart("夏", region: "AU") == 12 && FileSearch.seasonStart("summer", region: "CN") == 6)
        #expect(FileSearch.isTimeResidue("下午") && !FileSearch.isTimeResidue("合同") && !FileSearch.isTimeResidue("那个"))
    }

    @Test func buildsEscapedSpotlightQueries() throws {
        #expect(FileSearch.queryString(FileSearch.Plan()) == nil)
        var plan = FileSearch.Plan()
        plan.terms = [FileSearch.Term(["a\"b*"]), FileSearch.Term(["invoices", "发票"])]
        plan.kinds = [.screenshot]
        plan.period = .yesterday
        plan.dateField = .used
        let strict = try #require(FileSearch.queryString(plan, now: Self.now, calendar: Self.calendar))
        #expect(strict.contains(#"kMDItemFSName == "*a\"b\**"cd"#))
        #expect(strict.contains(#"kMDItemTextContent == "invoice*"cdw"#) && strict.contains(#"kMDItemTextContent == "发票*"cdw"#))
        #expect(strict.contains("kMDItemIsScreenCapture == 1"))
        #expect(strict.contains("kMDItemLastUsedDate >= $time.iso(2026-10-26T00:00:00Z)"))
        #expect(strict.contains(") && ("))
        let loose = try #require(FileSearch.queryString(plan, strict: false, now: Self.now, calendar: Self.calendar))
        #expect(loose.contains(") || ("))
        #expect(NSPredicate(fromMetadataQueryString: strict) != nil && NSPredicate(fromMetadataQueryString: loose) != nil)

        var types = FileSearch.Plan()
        types.kinds = [.pdf, .presentation]
        types.minBytes = 1_000
        let query = try #require(FileSearch.queryString(types))
        #expect(query.contains(#"kMDItemContentTypeTree == "com.adobe.pdf""#) && query.contains("kMDItemFSSize >= 1000"))
        #expect(FileSearch.stem("invoices") == "invoice" && FileSearch.stem("studies") == "study" && FileSearch.stem("class") == "class")
    }

    @Test func keepsOnlyTheModelsRealSearchWords() throws {
        let plan = FileSearch.parse("上周的 Acme PDF")
        let merged = FileSearch.merge(plan, modelTerms: [["PDF"], ["Acme", "艾克米", "Acme"], ["last week"], ["Acme"], ["  "]])
        #expect(merged.terms == [FileSearch.Term(["Acme", "艾克米"])])
        #expect(merged.understood && merged.kinds == [.pdf] && merged.period == .lastWeek)

        // The rules' concepts stay; a model term for the same thing is left out, a new one is added.
        let quote = FileSearch.parse("上周的报价单 PDF")
        let concept = try #require(quote.terms.first)
        #expect(quote.terms.count == 1 && concept.forms.contains("quotation"))
        let withConcept = FileSearch.merge(quote, modelTerms: [["报价单", "quote"], ["Acme"]])
        #expect(withConcept.terms == [concept, FileSearch.Term(["Acme"])])
        #expect(FileSearch.merge(plan, modelTerms: []) == plan)
        #expect(FileSearch.merge(plan, modelTerms: [["文件"], ["find"]]) == plan)

        let quoted = FileSearch.parse("“Q3 plan” slides")
        let withModel = FileSearch.merge(quoted, modelTerms: [["Q3 plan"], ["roadmap", "路线图"]])
        #expect(withModel.terms == [FileSearch.Term(["Q3 plan"], exact: true), FileSearch.Term(["roadmap", "路线图"])])
    }

    @Test func leavesOutHiddenAndSystemFiles() {
        let home = "/Users/me"
        #expect(FileSearch.isVisible("/Users/me/Desktop/plan.pdf", home: home))
        #expect(FileSearch.isVisible("/Users/me/Library/Mobile Documents/com~apple~CloudDocs/plan.pdf", home: home))
        #expect(FileSearch.isVisible("/Applications/Safari.app", home: home))
        #expect(!FileSearch.isVisible("/Users/me/Library/Caches/x.png", home: home))
        #expect(!FileSearch.isVisible("/Users/me/.Trash/old.pdf", home: home))
        #expect(!FileSearch.isVisible("/Users/me/code/.git/config", home: home))
        #expect(!FileSearch.isVisible("/Applications/Safari.app/Contents/Resources/x.png", home: home))
        #expect(!FileSearch.isVisible("/Users/me/Pictures/Photos Library.photoslibrary/originals/a.heic", home: home))
        #expect(!FileSearch.isVisible("/Users/me/web/node_modules/a/readme.md", home: home))
        #expect(!FileSearch.isVisible("/System/Library/x.pdf", home: home))
    }

    @Test func ranksNamesAndRecentFilesFirst() {
        var plan = FileSearch.Plan()
        plan.terms = [FileSearch.Term(["invoice"])]
        let now = Self.now
        func hit(_ path: String, days: Double, types: [String] = ["com.adobe.pdf"], strict: Bool = true) -> FileSearch.Hit {
            FileSearch.Hit(url: URL(fileURLWithPath: path), name: (path as NSString).lastPathComponent,
                           changed: now.addingTimeInterval(-days * 86_400), types: types, strict: strict)
        }
        let named = hit("/Users/me/Documents/Invoice March.pdf", days: 200)
        let content = hit("/Users/me/Documents/scan.pdf", days: 1)
        let code = hit("/Users/me/src/api/Invoice.swift", days: 1, types: ["public.swift-source", "public.source-code"])
        let codeByText = hit("/Users/me/src/api/payments.swift", days: 0, types: ["public.swift-source", "public.source-code"])
        let loose = hit("/Users/me/invoice-notes.txt", days: 0, types: ["public.plain-text"], strict: false)
        let ranked = FileSearch.rank([codeByText, loose, content, code, named], plan: plan, now: now).map(\.name)
        #expect(ranked.prefix(2).contains("Invoice March.pdf") && ranked.prefix(2).contains("Invoice.swift"))
        #expect(ranked.firstIndex(of: "scan.pdf")! < ranked.firstIndex(of: "payments.swift")!)
        #expect(ranked.firstIndex(of: "scan.pdf")! < ranked.firstIndex(of: "invoice-notes.txt")!)

        let cached = FileSearch.rank([hit("/Users/me/app/cache/invoice.pdf", days: 0), hit("/Users/me/Documents/invoice.pdf", days: 3)], plan: plan, now: now)
        #expect(cached.first?.url.path == "/Users/me/Documents/invoice.pdf")

        // The date under a result is the one the request filtered on, not a more recent open.
        var lastWeek = FileSearch.Plan()
        lastWeek.period = .lastWeek
        var opened = hit("/a/report.pdf", days: 6)
        opened.used = now.addingTimeInterval(-3_600)
        #expect(FileSearch.describe(opened, plan: lastWeek, now: now) == FileSearch.describe(hit("/a/report.pdf", days: 6), now: now))
        #expect(FileSearch.describe(opened, now: now) != FileSearch.describe(opened, plan: lastWeek, now: now))

        var filtersOnly = FileSearch.Plan()
        filtersOnly.kinds = [.pdf]
        let byDate = FileSearch.rank([hit("/a/old.pdf", days: 30), hit("/a/new.pdf", days: 1)], plan: filtersOnly, now: now)
        #expect(byDate.map(\.name) == ["new.pdf", "old.pdf"])
    }

    @MainActor @Test func fileRowsOpenOnlyFilesThatStillExist() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("swivel-search-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let gone = directory.appendingPathComponent("gone.pdf")
        let command = QuickActions.fileCommand(FileSearch.Hit(url: gone, name: "gone.pdf", changed: Self.now))
        #expect(command.id == "file:" + gone.path && command.file == gone && command.title == "gone.pdf")
        await #expect(throws: Info.self) { _ = try await command.run() }
        #expect(QuickActions.matching([command], query: "gone").count == 1)
    }

    @Test func everySearchStringIsTranslatedOnceWithMatchingPlaceholders() throws {
        #expect(Strings.search.values.allSatisfy { $0.count == 3 })
        #expect(Set(Strings.search.keys).isDisjoint(with: Strings.table.keys))
        #expect(Set(Strings.search.keys).isDisjoint(with: Strings.doctor.keys))
        let pattern = try NSRegularExpression(pattern: "%(?:\\d+\\$)?[@d%]")
        func placeholders(_ text: String) -> [String] {
            pattern.matches(in: text, range: NSRange(text.startIndex..., in: text)).map { (text as NSString).substring(with: $0.range) }
        }
        for (english, translations) in Strings.search {
            for translation in translations { #expect(placeholders(translation) == placeholders(english), "\(english) → \(translation)") }
        }
        // Every string the search code shows has a translation somewhere.
        let sources = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
            .appendingPathComponent("Sources/Swivel")
        let literal = try NSRegularExpression(pattern: #"\bL\("((?:[^"\\]|\\.)*)""#)
        var missing: [String] = []
        for name in ["FileSearch.swift", "SemanticIndex.swift"] {
            let text = try String(contentsOf: sources.appendingPathComponent(name), encoding: .utf8)
            for match in literal.matches(in: text, range: NSRange(text.startIndex..., in: text)) {
                let key = (text as NSString).substring(with: match.range(at: 1))
                if Strings.table[key] == nil, Strings.doctor[key] == nil, Strings.search[key] == nil { missing.append(key) }
            }
        }
        #expect(missing.isEmpty, "\(missing)")
    }
}
