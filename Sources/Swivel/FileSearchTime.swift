import Foundation

/// Reading the time in a request (FileSearchTime.cs has the same reader). Times are put together from their parts
/// instead of being listed phrase by phrase, so a new way of saying one still reads:
/// - an amount of time: a direction (过去 / 最近 / 前 / 这 / 上 / last / past / within), an amount (3, 两, 几, 半,
///   一个半, 两三, 十多, a couple of), a unit (小时 … 季度 … 年, hour … quarter … year) and an ending (前 / 以前 / 内 /
///   以来 / ago / back), plus vagueness (左右 / 多 / 大概 / about / or so), which widens the search;
/// - a calendar unit counted back (上上周, 这个月, 去年, last month, the week before last) and a part of it
///   (月底, 上周末, 年初, 下半年, 去年年底, end of last month);
/// - a day and part of the day (昨晚, 今天上午, 前天下午, last night, this morning), a weekday (上周三, 周五,
///   last Friday), a date (10月14日, 去年3月, 14号, 2026-10-14, 14/10, 14 Oct, early March, 国庆);
/// - vague times (前阵子, 前些天, 刚才, a while ago, recently).
/// Everything that reads as time is taken out of the text, and leftover time pieces never become words to look for.
extension FileSearch {
    private static func time(_ pattern: String) -> NSRegularExpression {
        try! NSRegularExpression(pattern: pattern, options: [.caseInsensitive])
    }

    private static let amountText = #"(?:\d+(?:\.\d+)?|[零一二两三四五六七八九十百几]+|好几|半|a\s+few|a\s+couple(?:\s+of)?|couple(?:\s+of)?|few|several|"#
        + #"a|an|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve)"#
    private static let vagueText = #"(?:左右|上下|前后|大概|大约|大致|差不多|约莫|约|about|around|roughly|approximately|or\s+so|ish|くらい|ぐらい|頃|ごろ)"#

    /// “过去两周”, “两三个月前”, “一年多以前”, “半个月内”, “last 3 days”, “a couple of weeks ago”, “2週間前”.
    /// (ICU look-behinds need a bounded length, hence `第\s{0,4}`.)
    private static let amountOfTime = time(
        #"(?<v1>\#(vagueText))?\s*(?<pre>过去的?|最近的?|近|前|这|上|(?<![a-z])(?:(?:in|within|over|during|for)\s+)?(?:the\s+)?(?:last|past|previous)|(?<![a-z])within)?\s*的?\s*"#
        + #"(?<![a-z0-9.])(?<!第\s{0,4})(?!(?<=[上下])半)(?<n>\#(amountText))(?:\s*(?:-|~|–|到|至|或|or|to)\s*(?<n2>\#(amountText)))?\s*(?<more>多|来|余|出头)?\s*(?<ge>个|個|ヶ|か|カ)?\s*(?<half>半)?\s*(?<more2>多)?\s*"#
        + #"(?<u>小时|钟头|小時|天|日|周|星期|礼拜|週間|週|月|季度|年|hours?|hrs?|days?|weeks?|wks?|fortnights?|months?|quarters?|years?|yrs?)(?![a-z])"#
        + #"\s*(?<half2>半|and\s+a\s+half)?\s*(?<more3>多)?\s*(?<v3>\#(vagueText))?\s*(?<post>以内|之内|内|里|来|以来|之间|以前|之前|前に|ほど前|前|ago|back|before|earlier)?(?![a-z])[\s-]*(?<v2>\#(vagueText))?"#)

    private static let weekdayZh = time(#"(?<k>上上个?|上个?|这个?|本)?\s*(?:周|星期|礼拜)\s*(?<d>[一二三四五六日天1-7])"#)
    private static let weekdayEn = time(#"(?<![a-z])(?:(?<k>last|this|past|previous)\s+)?(?<d>monday|tuesday|wednesday|thursday|friday|saturday|sunday)s?(?![a-z])"#)
    private static let weekdayJa = time(#"(?<k>先週|今週)?\s*(?<d>[月火水木金土日])曜日?"#)
    private static let weekdayNames = ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"]

    private static let monthText = #"(?<m>1[0-2]|0?[1-9]|十[一二]?|[一二三四五六七八九])"#
    private static let dayText = #"(?<d>3[01]|[12]\d|0?[1-9]|三十一?|[一二]?十[一二三四五六七八九]?|[一二三四五六七八九])"#
    private static let numericDate = time(#"(?<![\d.])(?<y>(?:19|20)\d{2})\s*[-/.年]\s*(?<m>1[0-2]|0?[1-9])\s*[-/.月]\s*(?<d>3[01]|[12]\d|0?[1-9])\s*[日号]?(?!\d)"#)
    private static let monthDayZh = time(
        #"(?:(?<y>(?:19|20)\d{2})\s*年|(?<yk>今年|去年|前年|大前年))?\s*的?\s*\#(monthText)\s*月份?\s*(?:\#(dayText)\s*[日号])?\s*(?<p>上旬|中旬|下旬|初|中|底|末)?(?!个|個|报|報)"#)
    private static let dayOnly = time(#"(?<![\d月])(?<d>3[01]|[12]\d|0?[1-9])\s*号"#)
    private static let slashDate = time(#"(?<![\d/.a-z])(?<a>\d{1,2})\s*(?<s>[/.])\s*(?<b>\d{1,2})(?:\s*\k<s>\s*(?<y>(?:19|20)?\d{2}))?(?![\d/.])"#)
    private static let monthNames = ["january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december"]
    private static let monthEn = time(
        #"(?<![a-z])(?:(?<p>early|mid|middle|late|end\s+of|beginning\s+of|start\s+of|the\s+end\s+of|the\s+beginning\s+of)[\s-]+)?(?:(?<d1>3[01]|[12]\d|0?[1-9])(?:st|nd|rd|th)?\s+(?:of\s+)?)?"#
        + #"(?<mon>january|february|march|april|may|june|july|august|september|october|november|december|jan|feb|mar|apr|jun|jul|aug|sept|sep|oct|nov|dec)(?![a-z])"#
        + #"(?:\s+(?<d2>3[01]|[12]\d|0?[1-9])(?:st|nd|rd|th)?(?![\d:]))?(?:,?\s+(?<y>(?:19|20)\d{2}))?"#)
    private static let monthPreposition = time(#"(?<![a-z])(?:in|since|from|during|of|until|before|after)\s+$"#)
    /// ICU has no repeated group names: the second spelling of the season is `s2`.
    private static let seasonZh = time(#"(?:(?<y>(?:19|20)\d{2})\s*年|(?<yk>今年|去年|前年))?\s*的?\s*(?:(?<s>春|夏|秋|冬)(?:天|季)|(?<=年\s{0,3}的?\s{0,3})(?<s2>春|夏|秋|冬)(?![\p{L}]))"#)
    private static let seasonEn = time(#"(?<![a-z])(?:(?<k>this|last|past)\s+|in\s+(?:the\s+)?)(?<s>spring|summer|autumn|fall|winter)(?:\s+(?<y>(?:19|20)\d{2}))?(?![a-z])"#)

    /// The first month of each season where the user lives (the seasons are turned round south of the equator).
    static func seasonStart(_ season: String, region: String? = Locale.current.region?.identifier) -> Int {
        let north = switch season.lowercased() {
        case "春", "spring": 3
        case "夏", "summer": 6
        case "秋", "autumn", "fall": 9
        default: 12
        }
        let south = ["AU", "NZ", "ZA", "AR", "CL", "UY", "PY", "BR"].contains(region ?? "")
        return south ? (north + 5) % 12 + 1 : north
    }

    private static let yearZh = time(#"(?<y>(?:19|20)\d{2})\s*年\s*(?<p>上半年|下半年|年?初|年中|年?底|年?末)?"#)
    private static let yearEn = time(#"(?<![a-z])(?:in|from|during|since|of|back\s+in)\s+(?<y>(?:19|20)\d{2})(?!\d)"#)
    private static let holidays = time(#"国庆节?|國慶節?|十一假期|元旦|圣诞节?|聖誕節?|christmas|new\s+year'?s?\s+day"#)

    private static let dayZh = time(#"(?<d>大前天|前天|昨天|昨日|昨儿|今天|今日|今儿|一昨日|おととい|昨|今)\s*(?<p>凌晨|早上|早晨|清晨|上午|中午|下午|傍晚|晚上|夜里|半夜|晚|早|夜|朝|晩)?"#)
    private static let dayEn = time(
        #"(?<![a-z])(?:(?<d>today|yesterday|(?:the\s+)?day\s+before\s+yesterday)(?:\s+(?<p>morning|afternoon|evening|night))?|this\s+(?<p2>morning|afternoon|evening)|tonight|last\s+night|earlier\s+today)(?![a-z])"#)

    private static let calendarZh = time(
        #"(?<k>上上上|上上|上|这|本|今|去|大前|前|先々|先)\s*(?:一)?\s*(?:个|個)?\s*(?<u>周|星期|礼拜|週|月|季度|年)\s*(?<p>上半年|下半年|上旬|中旬|下旬|年?初|年?中|年?底|年?末)?"#)
    /// “月底”, “周末”, “下半年” (the three spellings have their own group names).
    private static let calendarPart = time(#"(?<u1>月|年)\s*(?<p1>初|中|底|末)|(?<u2>周|週)\s*(?<p2>末)|(?<p3>上半年|下半年)"#)
    private static let calendarEn = time(
        #"(?<![a-z])(?:(?<p>beginning|start|early|first\s+half|second\s+half|middle|mid|end|late)\s+(?:of\s+)?)?(?:(?<earlier>earlier\s+)?(?<roll>(?:in|over|within|during|for)\s+)?(?<k>this|last|previous|past|the\s+previous|the\s+last|the\s+past)\s+)?"#
        + #"(?<u>weekend|fortnight|week|month|quarter|year)(?<before>\s+before\s+last)?(?![a-z])"#)

    /// Times said without a number or a calendar word.
    private static let vagueTimes: [(NSRegularExpression, Period)] = [
        (time(#"前几天|前些天|这几天|近几天|这些天|前两天|这阵子|这段时间|前不久|不久前|没多久前|早些时候|之前不久|(?<![a-z])(?:the\s+other\s+day|a\s+few\s+days\s+ago)(?![a-z])"#), .recent),
        (time(#"前阵子|前一阵子?|前段时间|前些日子|之前一段时间|一段时间前|那段时间|有段时间|(?<![a-z])(?:a\s+while\s+(?:ago|back)|some\s+time\s+ago|not\s+long\s+ago)(?![a-z])"#),
         Period(.past, 2, .month, loose: true)),
        (time(#"刚刚|刚才|刚(?=下载|存|保存|收到|拍|截|改|建|发|传|打开|做|弄|写)|(?<![a-z])(?:just\s+now|a\s+moment\s+ago)(?![a-z])|さっき"#), Period(.past, 6, .hour)),
    ]
    private static let sinceWords = time(#"(?:(?<![a-z])since|自从|从|自)\s*$"#)
    private static let onwardWords = time(#"^\s*(?:以来|开始|起|至今|到现在|到今天|(?<![a-z])(?:onwards?|until\s+now|till\s+now|to\s+now|to\s+date)(?![a-z]))"#)
    private static let recentWords = time(#"最近|近期|近来|最新的?|(?<![a-z])(?:recently|lately|recent|latest|newest|most\s+recent)(?![a-z])"#)

    /// A match and the text it was found in, with its named groups (empty when a group took no part).
    private struct TimeMatch {
        let result: NSTextCheckingResult
        let text: NSString
        func has(_ name: String) -> Bool { result.range(withName: name).location != NSNotFound }
        subscript(_ name: String) -> String { has(name) ? text.substring(with: result.range(withName: name)) : "" }
        var value: String { text.substring(with: result.range) }
        var before: String { text.substring(to: result.range.location) }
    }

    /// Reads (and removes from `text`) every time in it. The first one found, in the order below, is the request's.
    static func readTime(_ text: inout String) -> Period? {
        var found: Period?
        var within = text as NSString
        func each(_ pattern: NSRegularExpression, _ read: (TimeMatch) -> Period?) {
            var from = 0
            while from <= within.length,
                  let result = pattern.firstMatch(in: within as String, options: [.withTransparentBounds],
                                                  range: NSRange(location: from, length: within.length - from)) {
                guard result.range.length > 0, var period = read(TimeMatch(result: result, text: within)) else {
                    from = result.range.location + 1
                    continue
                }
                // “从上个月开始”, “上个月以来”, “since March”: from then until now.
                var start = result.range.location, end = NSMaxRange(result.range)
                let since = sinceWords.firstMatch(in: within as String, range: NSRange(location: 0, length: start))
                let onward = onwardWords.firstMatch(in: within as String, range: NSRange(location: end, length: within.length - end))
                if since != nil || onward != nil {
                    if period.kind != .past, period.kind != .recent { period.onward = true }
                    if let since { start = since.range.location }
                    if let onward { end = NSMaxRange(onward.range) }
                }
                found = found ?? period
                within = within.replacingCharacters(in: NSRange(location: start, length: end - start), with: " ") as NSString
                from = 0
            }
        }

        for (pattern, period) in vagueTimes { each(pattern) { _ in period } }
        each(amountOfTime, fromAmount)

        each(weekdayZh) { m in
            let d = m["d"]
            let day = "一二三四五六".firstIndex(of: Character(d)).map { "一二三四五六".distance(from: "一二三四五六".startIndex, to: $0) + 1 }
                ?? (d == "日" || d == "天" ? 0 : (Int(d) ?? 0) % 7)
            var k = m["k"]
            while k.hasSuffix("个") { k.removeLast() }
            return Period(.weekday, k == "上上" ? -2 : k == "上" ? -1 : 0, weekday: day)
        }
        each(weekdayEn) { m in
            Period(.weekday, ["last", "previous", "past"].contains(m["k"].lowercased()) ? -1 : 0,
                   weekday: weekdayNames.firstIndex(of: m["d"].lowercased()) ?? 0)
        }
        each(weekdayJa) { m in
            Period(.weekday, m["k"] == "先週" ? -1 : 0, weekday: Array("日月火水木金土").firstIndex(of: Character(m["d"])) ?? 0)
        }

        each(numericDate) { m in Period(.date, Int(m["y"]) ?? 0, month: Int(m["m"]) ?? 0, day: Int(m["d"]) ?? 0) }
        each(monthDayZh) { m in
            // 去年 / 前年 count back from this year (−1, −2), a written year is that year, none is the latest.
            let year = m.has("y") ? Int(m["y"]) ?? 0 : ["去年": -1, "前年": -2, "大前年": -3][m["yk"]] ?? 0
            return Period(.date, year, month: number(m["m"]) ?? 0, day: m.has("d") ? number(m["d"]) ?? 0 : 0,
                          part: m.has("p") ? partOf(m["p"]) : .whole)
        }
        each(dayOnly) { m in Period(.date, day: Int(m["d"]) ?? 0) }
        each(slashDate) { m in
            // 14/10 or 10/14: in the order the user's own date format uses (a 13 or more can only be the day).
            let format = DateFormatter.dateFormat(fromTemplate: "dM", options: 0, locale: .current) ?? "M/d"
            let dayFirst = (format.firstIndex(of: "d") ?? format.endIndex) < (format.firstIndex(of: "M") ?? format.endIndex)
            let a = Int(m["a"]) ?? 0, b = Int(m["b"]) ?? 0
            var (month, day) = dayFirst ? (b, a) : (a, b)
            if month > 12, day <= 12 { (month, day) = (day, month) }
            guard (1...12).contains(month), (1...31).contains(day) else { return nil }
            let year = m.has("y") ? Int(m["y"]) ?? 0 : 0
            return Period(.date, year > 0 && year < 100 ? 2000 + year : year, month: month, day: day)
        }
        each(monthEn) { m in
            let name = m["mon"].lowercased()
            let hasDay = m.has("d1") || m.has("d2")
            // “may”, “march”, “jan”… are also ordinary words: a date needs a day, year, part or a preposition.
            if ["may", "march", "mar", "jan", "dec", "sat"].contains(name), !hasDay, !m.has("p"), !m.has("y"),
               monthPreposition.firstMatch(in: m.before, range: NSRange(location: 0, length: (m.before as NSString).length)) == nil {
                return nil
            }
            return Period(.date, m.has("y") ? Int(m["y"]) ?? 0 : 0,
                          month: (monthNames.firstIndex { $0.hasPrefix(String(name.prefix(3))) } ?? -1) + 1,
                          day: hasDay ? Int(m[m.has("d1") ? "d1" : "d2"]) ?? 0 : 0,
                          part: m.has("p") ? partOf(m["p"]) : .whole)
        }
        each(holidays) { m in
            let holiday = m.value.lowercased()
            if holiday.hasPrefix("国") || holiday.hasPrefix("國") || holiday.hasPrefix("十") { return Period(.date, month: 10, part: .early, loose: true) }
            if holiday.hasPrefix("元") || holiday.hasPrefix("new") { return Period(.date, month: 1, day: 1) }
            return Period(.date, month: 12, part: .late, loose: true)
        }
        each(seasonZh) { m in
            Period(.season, m.has("y") ? Int(m["y"]) ?? 0 : ["去年": -1, "前年": -2][m["yk"]] ?? 0,
                   month: seasonStart(m.has("s") ? m["s"] : m["s2"]))
        }
        each(seasonEn) { m in
            Period(.season, m.has("y") ? Int(m["y"]) ?? 0 : ["last", "past"].contains(m["k"].lowercased()) ? -1 : 0, month: seasonStart(m["s"]))
        }
        each(yearZh) { m in
            m.has("p") ? Period(.date, Int(m["y"]) ?? 0, part: partOf(m["p"])) : .year(Int(m["y"]) ?? 0)
        }
        each(yearEn) { m in .year(Int(m["y"]) ?? 0) }

        each(dayZh) { m in
            let day = m["d"]
            // 昨 / 今 alone only with a part of the day (昨晚, 今早): “今” starts many words.
            if day == "昨" || day == "今", !m.has("p") { return nil }
            let offset = switch day {
            case "大前天": -3
            case "前天", "一昨日", "おととい": -2
            case "今天", "今日", "今儿", "今": 0
            default: -1
            }
            return dayPeriod(offset, m.has("p") ? partOf(m["p"]) : .whole)
        }
        each(dayEn) { m in
            let words = m.value.lowercased()
            if words.contains("earlier today") { return .today }
            let offset = words.contains("before") ? -2 : words.contains("yesterday") || words.contains("last night") ? -1 : 0
            let part: Part = m.has("p") ? partOf(m["p"]) : m.has("p2") ? partOf(m["p2"]) : words.contains("night") ? .late : .whole
            return dayPeriod(offset, part)
        }

        each(calendarZh) { m in
            let k = m["k"]
            let unit = unitOfCalendar(m["u"])
            // 去 / 前 / 大前 only make years (去年, 前年, 大前年); 今 and 先 also make 今月 / 今週 / 先月 / 先週.
            if ["去", "前", "大前"].contains(k), unit != .year { return nil }
            let offset = switch k {
            case "上上上", "大前": -3
            case "上上", "前", "先々": -2
            case "上", "去", "先": -1
            default: 0
            }
            return calendarPeriod(unit, offset, m.has("p") ? partOf(m["p"]) : .whole)
        }
        each(calendarPart) { m in
            if m.has("p3") { return calendarPeriod(.year, 0, partOf(m["p3"])) }
            if m.has("p2") { return calendarPeriod(unitOfCalendar(m["u2"]), 0, partOf(m["p2"])) }
            return calendarPeriod(unitOfCalendar(m["u1"]), 0, partOf(m["p1"]))
        }
        each(calendarEn) { m in
            let k = m["k"].lowercased().replacingOccurrences(of: "the ", with: "").trimmingCharacters(in: .whitespaces)
            let u = m["u"].lowercased()
            let before = m.has("before"), part = m.has("p")
            // A bare “week” or “year” is a word, not a time.
            if k.isEmpty, !before, !part, u != "weekend" { return nil }
            let unit: Unit = switch u {
            case "weekend", "week", "fortnight": .week
            case "month": .month
            case "quarter": .quarter
            default: .year
            }
            // “the past week”, “over the last month”, “last fortnight” count back from now; “last week” is the
            // calendar week before this one.
            if k == "past" || m.has("roll") || u == "fortnight", !part, u != "weekend" { return .past(u == "fortnight" ? 2 : 1, unit) }
            let offset = before ? -2 : ["last", "previous", "past"].contains(k) ? -1 : 0
            return calendarPeriod(unit, offset, u == "weekend" ? .late : part ? partOf(m["p"]) : .whole)
        }

        each(recentWords) { _ in .recent }
        text = within as String
        return found
    }

    /// “两三个月前”, “过去 3 天”, “一个半月内”, “a couple of weeks ago”; nil for “3月” (March) or “14日” (the 14th).
    private static func fromAmount(_ m: TimeMatch) -> Period? {
        let u = m["u"].lowercased(), n = m["n"], pre = m["pre"].lowercased()
        let counted = m.has("ge")
        // Without 个 or a direction, “3月” is March, “2024年” a year and “14日” a day of the month (“半年” is half a year).
        let ago = ["以前", "之前", "前", "前に", "ほど前", "ago", "back", "before", "earlier"].contains(m["post"].lowercased())
        if ["月", "年", "日"].contains(u), !counted, pre.isEmpty, !ago, n != "半" { return nil }
        // 上半年 / 上半月 are halves, read with the calendar.
        if pre == "上", n == "半" { return nil }
        guard var amount = amountValue(n) else { return nil }
        var upper = m.has("n2") ? amountValue(m["n2"]) ?? amount : amount
        let range = upper != amount
        if m.has("half") || m.has("half2"), amount >= 1 { amount += 0.5; upper += 0.5 }
        let more = m.has("more") || m.has("more2") || m.has("more3")
        let loose = more || range || m.has("v1") || m.has("v2") || m.has("v3") || n.contains("几")
        let (named, scale): (Unit, Double) = switch u {
        case "小时", "钟头", "小時", "hour", "hours", "hr", "hrs": (.hour, 1)
        case "天", "日", "day", "days": (.day, 1)
        case "fortnight", "fortnights": (.week, 2)
        case "月", "month", "months": (.month, 1)
        case "季度", "quarter", "quarters": (.month, 3)
        case "年", "year", "years", "yr", "yrs": (.year, 1)
        default: (.week, 1)
        }
        var unit = named
        // “上一周”, “上一个月” are the calendar week / month before this one.
        if pre == "上", amount == 1, !ago, [.week, .month, .year].contains(unit) { return calendarPeriod(unit, -1, .whole) }
        // Rolling (“过去两三周”) reaches the farther end; “两三周前” is around the middle.
        var value = (ago ? (amount + upper) / 2 : upper) * scale
        // “两三周前” is about three weeks ago (in weeks, not 17.5 days to the hour).
        if ago, range { value = value.rounded(.toNearestOrAwayFromZero) }
        if more, !ago { value += 0.5 }
        // Fractions move to a smaller unit: 半个月 → 15 days, 一年半 → 18 months.
        while value != value.rounded(.down), unit != .hour {
            switch unit {
            case .year: (unit, value) = (.month, value * 12)
            case .month: (unit, value) = (.day, value * 30)
            case .week: (unit, value) = (.day, value * 7)
            default: (unit, value) = (.hour, value * 24)
            }
        }
        return Period(ago ? .ago : .past, max(1, Int(value.rounded(.toNearestOrEven))), unit, loose: loose)
    }

    /// 3, 3.5, 两, 十四, 两三 (the larger), 十几 (≈15), 几十 (≈30), 半, 几 / a few (3), a couple (2), several (4).
    static func amountValue(_ text: String) -> Double? {
        let text = text.lowercased().trimmingCharacters(in: .whitespaces)
            .replacingOccurrences(of: #"\s+"#, with: " ", options: .regularExpression)
        switch text {
        case "半": return 0.5
        case "几", "few", "a few": return 3
        case "好几", "several": return 4
        case "couple", "a couple", "couple of", "a couple of": return 2
        case "eleven": return 11
        case "twelve": return 12
        default: break
        }
        if text.unicodeScalars.allSatisfy({ ("0"..."9").contains($0) || $0 == "." }), let value = Double(text) { return value }
        if text.contains("几") {
            let plain = text.replacingOccurrences(of: "几", with: "")
            if text.hasSuffix("几") { return Double((plain.isEmpty ? 10 : number(plain) ?? 10) + 5) }
            return Double((plain.isEmpty ? 1 : number(plain.replacingOccurrences(of: "十", with: "")) ?? 1) * 30)
        }
        let chars = Array(text)
        if chars.count == 2, chars.allSatisfy({ "一二两三四五六七八九".contains($0) }), let larger = number(String(chars[1])) { return Double(larger) }
        if text.contains("百") {
            let parts = text.components(separatedBy: "百")
            let hundreds = parts[0].isEmpty ? 1 : number(parts[0]) ?? 1
            let rest = parts.count > 1 && !parts[1].isEmpty ? number(parts[1]) ?? 0 : 0
            return Double(hundreds * 100 + rest)
        }
        return number(text).map(Double.init)
    }

    private static func partOf(_ word: String) -> Part {
        switch word.lowercased().replacingOccurrences(of: "年", with: "") {
        case "上半", "first half": .firstHalf
        case "下半", "second half": .secondHalf
        case "初", "上旬", "early", "beginning", "start", "beginning of", "start of", "the beginning of",
             "凌晨", "早上", "早晨", "清晨", "上午", "早", "朝", "morning": .early
        case "中", "中旬", "mid", "middle", "中午", "下午", "afternoon": .middle
        default: .late
        }
    }

    private static func unitOfCalendar(_ word: String) -> Unit {
        switch word {
        case "月": .month
        case "年": .year
        case "季度": .quarter
        default: .week
        }
    }

    /// Today / yesterday keep their own names; other days and parts of a day are calendar days.
    private static func dayPeriod(_ offset: Int, _ part: Part) -> Period {
        switch (offset, part) {
        case (0, .whole): .today
        case (-1, .whole): .yesterday
        case (-2, .whole): .dayBeforeYesterday
        default: Period(.calendar, offset, .day, part: part)
        }
    }

    private static func calendarPeriod(_ unit: Unit, _ offset: Int, _ part: Part) -> Period {
        switch (unit, offset, part) {
        case (.week, 0, .whole): .thisWeek
        case (.week, -1, .whole): .lastWeek
        case (.month, 0, .whole): .thisMonth
        case (.month, -1, .whole): .lastMonth
        case (.year, 0, .whole): .thisYear
        case (.year, -1, .whole): .lastYear
        default: Period(.calendar, offset, unit, part: part)
        }
    }

    /// Words made only of time pieces (“下午”, “那两天”, “十月”) that the reader didn't place.
    private static let timeResidue = time(
        #"^[前后上下这今去昨大近过最内里来以之的个個几两半多零一二三四五六七八九十百天日周週星期礼拜月年号時时小钟分早晚午夜旬初底末季度凌晨傍中刚才些段阵那]+$"#)
    private static let timeUnitChar = time(#"[天日周週星期礼拜月年号時时午夜晨旬季]"#)

    static func isTimeResidue(_ word: String) -> Bool {
        let range = NSRange(location: 0, length: (word as NSString).length)
        return timeResidue.firstMatch(in: word, range: range) != nil && timeUnitChar.firstMatch(in: word, range: range) != nil
    }
}
