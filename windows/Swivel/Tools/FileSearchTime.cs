using System.Globalization;
using System.Text.RegularExpressions;

namespace Swivel;

/// Reading the time in a request (FileSearch.swift has the same reader). Times are put together from their parts
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
static partial class FileSearch
{
    const string AmountText = @"(?:\d+(?:\.\d+)?|[零一二两三四五六七八九十百几]+|好几|半|a\s+few|a\s+couple(?:\s+of)?|couple(?:\s+of)?|few|several|"
                              + @"a|an|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve)";
    const string VagueText = @"(?:左右|上下|前后|大概|大约|大致|差不多|约莫|约|about|around|roughly|approximately|or\s+so|ish|くらい|ぐらい|頃|ごろ)";

    /// “过去两周”, “两三个月前”, “一年多以前”, “半个月内”, “last 3 days”, “a couple of weeks ago”, “2週間前”.
    static readonly Regex AmountOfTime = new(
        $@"(?<v1>{VagueText})?\s*(?<pre>过去的?|最近的?|近|前|这|上|(?<![a-z])(?:(?:in|within|over|during|for)\s+)?(?:the\s+)?(?:last|past|previous)|(?<![a-z])within)?\s*的?\s*"
        + $@"(?<![a-z0-9.])(?<!第\s*)(?!(?<=[上下])半)(?<n>{AmountText})(?:\s*(?:-|~|–|到|至|或|or|to)\s*(?<n2>{AmountText}))?\s*(?<more>多|来|余|出头)?\s*(?<ge>个|個|ヶ|か|カ)?\s*(?<half>半)?\s*(?<more2>多)?\s*"
        + @"(?<u>小时|钟头|小時|天|日|周|星期|礼拜|週間|週|月|季度|年|hours?|hrs?|days?|weeks?|wks?|fortnights?|months?|quarters?|years?|yrs?)(?![a-z])"
        + $@"\s*(?<half2>半|and\s+a\s+half)?\s*(?<more3>多)?\s*(?<v3>{VagueText})?\s*(?<post>以内|之内|内|里|来|以来|之间|以前|之前|前に|ほど前|前|ago|back|before|earlier)?(?![a-z])[\s-]*(?<v2>{VagueText})?",
        Options);

    static readonly Regex WeekdayZh = new(@"(?<k>上上个?|上个?|这个?|本)?\s*(?:周|星期|礼拜)\s*(?<d>[一二三四五六日天1-7])", Options);
    static readonly Regex WeekdayEn = new(@"(?<![a-z])(?:(?<k>last|this|past|previous)\s+)?(?<d>monday|tuesday|wednesday|thursday|friday|saturday|sunday)s?(?![a-z])", Options);
    static readonly Regex WeekdayJa = new(@"(?<k>先週|今週)?\s*(?<d>[月火水木金土日])曜日?", Options);

    const string MonthText = @"(?<m>1[0-2]|0?[1-9]|十[一二]?|[一二三四五六七八九])";
    const string DayText = @"(?<d>3[01]|[12]\d|0?[1-9]|三十一?|[一二]?十[一二三四五六七八九]?|[一二三四五六七八九])";
    static readonly Regex NumericDate = new(@"(?<![\d.])(?<y>(?:19|20)\d{2})\s*[-/.年]\s*(?<m>1[0-2]|0?[1-9])\s*[-/.月]\s*(?<d>3[01]|[12]\d|0?[1-9])\s*[日号]?(?!\d)", Options);
    static readonly Regex MonthDayZh = new(
        $@"(?:(?<y>(?:19|20)\d{{2}})\s*年|(?<yk>今年|去年|前年|大前年))?\s*的?\s*{MonthText}\s*月份?\s*(?:{DayText}\s*[日号])?\s*(?<p>上旬|中旬|下旬|初|中|底|末)?(?!个|個|报|報)",
        Options);
    static readonly Regex DayOnly = new(@"(?<![\d月])(?<d>3[01]|[12]\d|0?[1-9])\s*号", Options);
    static readonly Regex SlashDate = new(@"(?<![\d/.a-z])(?<a>\d{1,2})\s*(?<s>[/.])\s*(?<b>\d{1,2})(?:\s*\k<s>\s*(?<y>(?:19|20)?\d{2}))?(?![\d/.])", Options);
    static readonly string[] MonthNames = ["january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december"];
    static readonly Regex MonthEn = new(
        @"(?<![a-z])(?:(?<p>early|mid|middle|late|end\s+of|beginning\s+of|start\s+of|the\s+end\s+of|the\s+beginning\s+of)[\s-]+)?(?:(?<d1>3[01]|[12]\d|0?[1-9])(?:st|nd|rd|th)?\s+(?:of\s+)?)?"
        + @"(?<mon>january|february|march|april|may|june|july|august|september|october|november|december|jan|feb|mar|apr|jun|jul|aug|sept|sep|oct|nov|dec)(?![a-z])"
        + @"(?:\s+(?<d2>3[01]|[12]\d|0?[1-9])(?:st|nd|rd|th)?(?![\d:]))?(?:,?\s+(?<y>(?:19|20)\d{2}))?", Options);
    static readonly Regex SeasonZh = new(@"(?:(?<y>(?:19|20)\d{2})\s*年|(?<yk>今年|去年|前年))?\s*的?\s*(?:(?<s>春|夏|秋|冬)(?:天|季)|(?<=年\s*的?\s*)(?<s>春|夏|秋|冬)(?![\p{L}]))", Options);
    static readonly Regex SeasonEn = new(@"(?<![a-z])(?:(?<k>this|last|past)\s+|in\s+(?:the\s+)?)(?<s>spring|summer|autumn|fall|winter)(?:\s+(?<y>(?:19|20)\d{2}))?(?![a-z])", Options);

    /// The first month of each season where the user lives (the seasons are turned round south of the equator).
    static int SeasonStart(string season)
    {
        int north = season.ToLowerInvariant() switch { "春" or "spring" => 3, "夏" or "summer" => 6, "秋" or "autumn" or "fall" => 9, _ => 12 };
        bool south = RegionInfo.CurrentRegion.TwoLetterISORegionName is "AU" or "NZ" or "ZA" or "AR" or "CL" or "UY" or "PY" or "BR";
        return south ? (north + 5) % 12 + 1 : north;
    }

    static readonly Regex YearZh = new(@"(?<y>(?:19|20)\d{2})\s*年\s*(?<p>上半年|下半年|年?初|年中|年?底|年?末)?", Options);
    static readonly Regex YearEn = new(@"(?<![a-z])(?:in|from|during|since|of|back\s+in)\s+(?<y>(?:19|20)\d{2})(?!\d)", Options);
    static readonly Regex Holidays = new(@"国庆节?|國慶節?|十一假期|元旦|圣诞节?|聖誕節?|christmas|new\s+year'?s?\s+day", Options);

    static readonly Regex DayZh = new(@"(?<d>大前天|前天|昨天|昨日|昨儿|今天|今日|今儿|一昨日|おととい|昨|今)\s*(?<p>凌晨|早上|早晨|清晨|上午|中午|下午|傍晚|晚上|夜里|半夜|晚|早|夜|朝|晩)?", Options);
    static readonly Regex DayEn = new(
        @"(?<![a-z])(?:(?<d>today|yesterday|(?:the\s+)?day\s+before\s+yesterday)(?:\s+(?<p>morning|afternoon|evening|night))?|this\s+(?<p2>morning|afternoon|evening)|tonight|last\s+night|earlier\s+today)(?![a-z])",
        Options);

    static readonly Regex CalendarZh = new(
        @"(?<k>上上上|上上|上|这|本|今|去|大前|前|先々|先)\s*(?:一)?\s*(?:个|個)?\s*(?<u>周|星期|礼拜|週|月|季度|年)\s*(?<p>上半年|下半年|上旬|中旬|下旬|年?初|年?中|年?底|年?末)?", Options);
    static readonly Regex CalendarPart = new(@"(?<u>月|年)\s*(?<p>初|中|底|末)|(?<u>周|週)\s*(?<p>末)|(?<p>上半年|下半年)", Options);
    static readonly Regex CalendarEn = new(
        @"(?<![a-z])(?:(?<p>beginning|start|early|first\s+half|second\s+half|middle|mid|end|late)\s+(?:of\s+)?)?(?:(?<earlier>earlier\s+)?(?<roll>(?:in|over|within|during|for)\s+)?(?<k>this|last|previous|past|the\s+previous|the\s+last|the\s+past)\s+)?"
        + @"(?<u>weekend|fortnight|week|month|quarter|year)(?<before>\s+before\s+last)?(?![a-z])", Options);

    /// Times said without a number or a calendar word.
    static readonly (Regex Pattern, Period Period)[] VagueTimes =
    [
        (new(@"前几天|前些天|这几天|近几天|这些天|前两天|这阵子|这段时间|前不久|不久前|没多久前|早些时候|之前不久|(?<![a-z])(?:the\s+other\s+day|a\s+few\s+days\s+ago)(?![a-z])", Options), new Period(PeriodKind.Recent)),
        (new(@"前阵子|前一阵子?|前段时间|前些日子|之前一段时间|一段时间前|那段时间|有段时间|(?<![a-z])(?:a\s+while\s+(?:ago|back)|some\s+time\s+ago|not\s+long\s+ago)(?![a-z])", Options),
         new Period(PeriodKind.Past, 2, Unit.Month) { Loose = true }),
        (new(@"刚刚|刚才|刚(?=下载|存|保存|收到|拍|截|改|建|发|传|打开|做|弄|写)|(?<![a-z])(?:just\s+now|a\s+moment\s+ago)(?![a-z])|さっき", Options), new Period(PeriodKind.Past, 6, Unit.Hour)),
    ];
    static readonly Regex Since = new(@"(?:(?<![a-z])since|自从|从|自)\s*$", Options);
    static readonly Regex Onward = new(@"^\s*(?:以来|开始|起|至今|到现在|到今天|(?<![a-z])(?:onwards?|until\s+now|till\s+now|to\s+now|to\s+date)(?![a-z]))", Options);
    static readonly Regex RecentWords = new(@"最近|近期|近来|最新的?|(?<![a-z])(?:recently|lately|recent|latest|newest|most\s+recent)(?![a-z])", Options);

    /// Reads (and removes from `text`) every time in it. The first one found, in the order below, is the request's.
    static Period? ReadTime(ref string text)
    {
        Period? found = null;
        void each(Regex pattern, ref string within, Func<Match, string, Period?> read)
        {
            int from = 0;
            while (from <= within.Length && pattern.Match(within, from) is { Success: true } match)
            {
                if (match.Length > 0 && read(match, within) is { } period)
                {
                    // “从上个月开始”, “上个月以来”, “since March”: from then until now.
                    int start = match.Index, end = match.Index + match.Length;
                    var since = Since.Match(within[..start]);
                    var onward = Onward.Match(within[end..]);
                    if (since.Success || onward.Success)
                    {
                        if (period.Kind is not (PeriodKind.Past or PeriodKind.Recent)) period = period with { Onward = true };
                        if (since.Success) start = since.Index;
                        if (onward.Success) end += onward.Length;
                    }
                    found ??= period;
                    within = within.Remove(start, end - start).Insert(start, " ");
                    from = 0;
                }
                else from = match.Index + 1;
            }
        }

        foreach (var (pattern, period) in VagueTimes) each(pattern, ref text, (_, _) => period);
        each(AmountOfTime, ref text, (m, _) => FromAmount(m));

        each(WeekdayZh, ref text, (m, _) =>
        {
            int index = "一二三四五六".IndexOf(m.Groups["d"].Value[0]);
            int day = index >= 0 ? index + 1 : m.Groups["d"].Value[0] is '日' or '天' ? 0 : int.Parse(m.Groups["d"].Value) % 7;
            int offset = m.Groups["k"].Value.TrimEnd('个') switch { "上上" => -2, "上" => -1, _ => 0 };
            return new Period(PeriodKind.Weekday, offset) { Weekday = (DayOfWeek)day };
        });
        each(WeekdayEn, ref text, (m, _) => new Period(PeriodKind.Weekday, m.Groups["k"].Value.ToLowerInvariant() is "last" or "previous" or "past" ? -1 : 0)
        {
            Weekday = Enum.Parse<DayOfWeek>(m.Groups["d"].Value, ignoreCase: true),
        });
        each(WeekdayJa, ref text, (m, _) => new Period(PeriodKind.Weekday, m.Groups["k"].Value == "先週" ? -1 : 0)
        {
            Weekday = (DayOfWeek)"日月火水木金土".IndexOf(m.Groups["d"].Value[0]),
        });

        each(NumericDate, ref text, (m, _) => new Period(PeriodKind.Date, int.Parse(m.Groups["y"].Value))
        {
            Month = int.Parse(m.Groups["m"].Value),
            Day = int.Parse(m.Groups["d"].Value),
        });
        each(MonthDayZh, ref text, (m, _) =>
        {
            // 去年 / 前年 count back from this year (−1, −2), a written year is that year, none is the latest.
            int year = m.Groups["y"].Success ? int.Parse(m.Groups["y"].Value)
                : m.Groups["yk"].Value switch { "去年" => -1, "前年" => -2, "大前年" => -3, _ => 0 };
            return new Period(PeriodKind.Date, year)
            {
                Month = ParseNumber(m.Groups["m"].Value) ?? 0,
                Day = m.Groups["d"].Success ? ParseNumber(m.Groups["d"].Value) ?? 0 : 0,
                Part = m.Groups["p"].Success ? PartOf(m.Groups["p"].Value) : Part.Whole,
            };
        });
        each(DayOnly, ref text, (m, _) => new Period(PeriodKind.Date) { Day = int.Parse(m.Groups["d"].Value) });
        each(SlashDate, ref text, (m, _) =>
        {
            // 14/10 or 10/14: in the order the user's own date format uses (a 13 or more can only be the day).
            bool dayFirst = CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern.TrimStart().StartsWith('d');
            int a = int.Parse(m.Groups["a"].Value), b = int.Parse(m.Groups["b"].Value);
            var (month, day) = dayFirst ? (b, a) : (a, b);
            if (month > 12 && day <= 12) (month, day) = (day, month);
            if (month is < 1 or > 12 || day is < 1 or > 31) return null;
            int year = m.Groups["y"].Success ? int.Parse(m.Groups["y"].Value) : 0;
            return new Period(PeriodKind.Date, year is > 0 and < 100 ? 2000 + year : year) { Month = month, Day = day };
        });
        each(MonthEn, ref text, (m, before) =>
        {
            var name = m.Groups["mon"].Value.ToLowerInvariant();
            bool day = m.Groups["d1"].Success || m.Groups["d2"].Success;
            // “may”, “march”, “jan”… are also ordinary words: a date needs a day, year, part or a preposition.
            if (name is "may" or "march" or "mar" or "jan" or "dec" or "sat" && !day && !m.Groups["p"].Success && !m.Groups["y"].Success
                && !Regex.IsMatch(before[..m.Index], @"(?<![a-z])(?:in|since|from|during|of|until|before|after)\s+$", Options)) return null;
            return new Period(PeriodKind.Date, m.Groups["y"].Success ? int.Parse(m.Groups["y"].Value) : 0)
            {
                Month = Array.FindIndex(MonthNames, n => n.StartsWith(name[..3], StringComparison.Ordinal)) + 1,
                Day = day ? int.Parse(m.Groups[m.Groups["d1"].Success ? "d1" : "d2"].Value) : 0,
                Part = m.Groups["p"].Success ? PartOf(m.Groups["p"].Value) : Part.Whole,
            };
        });
        each(Holidays, ref text, (m, _) => m.Value.ToLowerInvariant() switch
        {
            var h when h.StartsWith('国') || h.StartsWith('國') || h.StartsWith('十') => new Period(PeriodKind.Date) { Month = 10, Part = Part.Early, Loose = true },
            var h when h.StartsWith('元') || h.StartsWith("new", StringComparison.Ordinal) => new Period(PeriodKind.Date) { Month = 1, Day = 1 },
            _ => new Period(PeriodKind.Date) { Month = 12, Part = Part.Late, Loose = true },
        });
        each(SeasonZh, ref text, (m, _) => new Period(PeriodKind.Season,
            m.Groups["y"].Success ? int.Parse(m.Groups["y"].Value) : m.Groups["yk"].Value switch { "去年" => -1, "前年" => -2, _ => 0 })
        {
            Month = SeasonStart(m.Groups["s"].Value),
        });
        each(SeasonEn, ref text, (m, _) => new Period(PeriodKind.Season,
            m.Groups["y"].Success ? int.Parse(m.Groups["y"].Value) : m.Groups["k"].Value is "last" or "past" ? -1 : 0)
        {
            Month = SeasonStart(m.Groups["s"].Value),
        });
        each(YearZh, ref text, (m, _) => m.Groups["p"].Success
            ? new Period(PeriodKind.Date, int.Parse(m.Groups["y"].Value)) { Part = PartOf(m.Groups["p"].Value) }
            : new Period(PeriodKind.Year, int.Parse(m.Groups["y"].Value)));
        each(YearEn, ref text, (m, _) => new Period(PeriodKind.Year, int.Parse(m.Groups["y"].Value)));

        each(DayZh, ref text, (m, _) =>
        {
            var day = m.Groups["d"].Value;
            // 昨 / 今 alone only with a part of the day (昨晚, 今早): “今” starts many words.
            if (day is "昨" or "今" && !m.Groups["p"].Success) return null;
            int offset = day switch { "大前天" => -3, "前天" or "一昨日" or "おととい" => -2, "今天" or "今日" or "今儿" or "今" => 0, _ => -1 };
            return DayPeriod(offset, m.Groups["p"].Success ? PartOf(m.Groups["p"].Value) : Part.Whole);
        });
        each(DayEn, ref text, (m, _) =>
        {
            var words = m.Value.ToLowerInvariant();
            if (words.Contains("earlier today")) return new Period(PeriodKind.Today);
            int offset = words.Contains("before") ? -2 : words.Contains("yesterday") || words.Contains("last night") ? -1 : 0;
            var part = m.Groups["p"].Success ? PartOf(m.Groups["p"].Value) : m.Groups["p2"].Success ? PartOf(m.Groups["p2"].Value)
                : words.Contains("night") ? Part.Late : Part.Whole;
            return DayPeriod(offset, part);
        });

        each(CalendarZh, ref text, (m, _) =>
        {
            var k = m.Groups["k"].Value;
            var unit = UnitOfCalendar(m.Groups["u"].Value);
            // 去 / 前 / 大前 only make years (去年, 前年, 大前年); 今 and 先 also make 今月 / 今週 / 先月 / 先週.
            if (k is "去" or "前" or "大前" && unit != Unit.Year) return null;
            int offset = k switch { "上上上" or "大前" => -3, "上上" or "前" or "先々" => -2, "上" or "去" or "先" => -1, _ => 0 };
            return CalendarPeriod(unit, offset, m.Groups["p"].Success ? PartOf(m.Groups["p"].Value) : Part.Whole);
        });
        each(CalendarPart, ref text, (m, _) =>
        {
            var p = m.Groups["p"].Value;
            return CalendarPeriod(p.EndsWith("半年") ? Unit.Year : UnitOfCalendar(m.Groups["u"].Value), 0, PartOf(p));
        });
        each(CalendarEn, ref text, (m, _) =>
        {
            var k = m.Groups["k"].Value.ToLowerInvariant().Replace("the ", "").Trim();
            var u = m.Groups["u"].Value.ToLowerInvariant();
            bool before = m.Groups["before"].Success, part = m.Groups["p"].Success;
            // A bare “week” or “year” is a word, not a time.
            if (k.Length == 0 && !before && !part && u != "weekend") return null;
            var unit = u switch { "weekend" or "week" or "fortnight" => Unit.Week, "month" => Unit.Month, "quarter" => Unit.Quarter, _ => Unit.Year };
            // “the past week”, “over the last month”, “last fortnight” count back from now; “last week” is the
            // calendar week before this one.
            if ((k == "past" || m.Groups["roll"].Success || u == "fortnight") && !part && u != "weekend")
                return new Period(PeriodKind.Past, u == "fortnight" ? 2 : 1, unit);
            int offset = before ? -2 : k is "last" or "previous" or "past" ? -1 : 0;
            return CalendarPeriod(unit, offset, u == "weekend" ? Part.Late : part ? PartOf(m.Groups["p"].Value) : Part.Whole);
        });

        each(RecentWords, ref text, (_, _) => new Period(PeriodKind.Recent));
        return found;
    }

    /// “两三个月前”, “过去 3 天”, “一个半月内”, “a couple of weeks ago”; null for “3月” (March) or “14日” (the 14th).
    static Period? FromAmount(Match match)
    {
        var u = match.Groups["u"].Value.ToLowerInvariant();
        var n = match.Groups["n"].Value;
        var pre = match.Groups["pre"].Value.ToLowerInvariant();
        bool counted = match.Groups["ge"].Success;
        // Without 个 or a direction, “3月” is March, “2024年” a year and “14日” a day of the month (“半年” is half a year).
        var ending = match.Groups["post"].Value.ToLowerInvariant();
        bool ago = ending is "以前" or "之前" or "前" or "前に" or "ほど前" or "ago" or "back" or "before" or "earlier";
        if (u is "月" or "年" or "日" && !counted && pre.Length == 0 && !ago && n != "半") return null;
        // 上半年 / 上半月 are halves, read with the calendar.
        if (pre == "上" && n == "半") return null;
        if (AmountValue(n) is not double amount) return null;
        double upper = match.Groups["n2"].Success && AmountValue(match.Groups["n2"].Value) is double second ? second : amount;
        bool range = upper != amount;
        bool half = match.Groups["half"].Success || match.Groups["half2"].Success;
        if (half && amount >= 1) { amount += 0.5; upper += 0.5; }
        bool more = match.Groups["more"].Success || match.Groups["more2"].Success || match.Groups["more3"].Success;
        bool loose = more || range || match.Groups["v1"].Success || match.Groups["v2"].Success || match.Groups["v3"].Success || n.Contains('几');
        var (unit, scale) = u switch
        {
            "小时" or "钟头" or "小時" or "hour" or "hours" or "hr" or "hrs" => (Unit.Hour, 1.0),
            "天" or "日" or "day" or "days" => (Unit.Day, 1.0),
            "fortnight" or "fortnights" => (Unit.Week, 2.0),
            "月" or "month" or "months" => (Unit.Month, 1.0),
            "季度" or "quarter" or "quarters" => (Unit.Month, 3.0),
            "年" or "year" or "years" or "yr" or "yrs" => (Unit.Year, 1.0),
            _ => (Unit.Week, 1.0),
        };
        // “上一周”, “上一个月” are the calendar week / month before this one.
        if (pre == "上" && amount == 1 && !ago && unit is Unit.Week or Unit.Month or Unit.Year) return CalendarPeriod(unit, -1, Part.Whole);
        // Rolling (“过去两三周”) reaches the farther end; “两三周前” is around the middle.
        double value = (ago ? (amount + upper) / 2 : upper) * scale;
        // “两三周前” is about three weeks ago (in weeks, not 17.5 days to the hour).
        if (ago && range) value = Math.Round(value, MidpointRounding.AwayFromZero);
        if (more && !ago) value += 0.5;
        // Fractions move to a smaller unit: 半个月 → 15 days, 一年半 → 18 months.
        while (value != Math.Floor(value) && unit != Unit.Hour)
            (unit, value) = unit switch
            {
                Unit.Year => (Unit.Month, value * 12),
                Unit.Month => (Unit.Day, value * 30),
                Unit.Week => (Unit.Day, value * 7),
                _ => (Unit.Hour, value * 24),
            };
        return new Period(ago ? PeriodKind.Ago : PeriodKind.Past, Math.Max(1, (int)Math.Round(value)), unit) { Loose = loose };
    }

    /// 3, 3.5, 两, 十四, 两三 (the larger), 十几 (≈15), 几十 (≈30), 半, 几 / a few (3), a couple (2), several (4).
    static double? AmountValue(string text)
    {
        text = Regex.Replace(text.Trim().ToLowerInvariant(), @"\s+", " ");
        switch (text)
        {
            case "半": return 0.5;
            case "几" or "few" or "a few": return 3;
            case "好几" or "several": return 4;
            case "couple" or "a couple" or "couple of" or "a couple of": return 2;
            case "eleven": return 11;
            case "twelve": return 12;
        }
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return number;
        if (text.Contains('几'))
        {
            var plain = text.Replace("几", "");
            if (text.EndsWith('几')) return (plain.Length == 0 ? 10 : ParseNumber(plain) ?? 10) + 5;
            return (plain.Length == 0 ? 1 : ParseNumber(plain.Replace("十", "")) ?? 1) * 30;
        }
        if (text.Length == 2 && text.All("一二两三四五六七八九".Contains) && ParseNumber(text[1].ToString()) is int larger) return larger;
        if (text.Contains('百'))
        {
            var parts = text.Split('百');
            return (parts[0].Length == 0 ? 1 : ParseNumber(parts[0]) ?? 1) * 100 + (parts[1].Length > 0 ? ParseNumber(parts[1]) ?? 0 : 0);
        }
        return ParseNumber(text);
    }

    static Part PartOf(string word) => word.ToLowerInvariant().Replace("年", "") switch
    {
        "上半" or "first half" => Part.FirstHalf,
        "下半" or "second half" => Part.SecondHalf,
        "初" or "上旬" or "early" or "beginning" or "start" or "beginning of" or "start of" or "the beginning of"
            or "凌晨" or "早上" or "早晨" or "清晨" or "上午" or "早" or "朝" or "morning" => Part.Early,
        "中" or "中旬" or "mid" or "middle" or "中午" or "下午" or "afternoon" => Part.Middle,
        _ => Part.Late,
    };

    static Unit UnitOfCalendar(string word) => word switch
    {
        "月" => Unit.Month,
        "年" => Unit.Year,
        "季度" => Unit.Quarter,
        _ => Unit.Week,
    };

    /// Today / yesterday keep their own names; other days and parts of a day are calendar days.
    static Period DayPeriod(int offset, Part part) => (offset, part) switch
    {
        (0, Part.Whole) => new Period(PeriodKind.Today),
        (-1, Part.Whole) => new Period(PeriodKind.Yesterday),
        (-2, Part.Whole) => new Period(PeriodKind.DayBeforeYesterday),
        _ => new Period(PeriodKind.Calendar, offset, Unit.Day) { Part = part },
    };

    static Period CalendarPeriod(Unit unit, int offset, Part part) => (unit, offset, part) switch
    {
        (Unit.Week, 0, Part.Whole) => new Period(PeriodKind.ThisWeek),
        (Unit.Week, -1, Part.Whole) => new Period(PeriodKind.LastWeek),
        (Unit.Month, 0, Part.Whole) => new Period(PeriodKind.ThisMonth),
        (Unit.Month, -1, Part.Whole) => new Period(PeriodKind.LastMonth),
        (Unit.Year, 0, Part.Whole) => new Period(PeriodKind.ThisYear),
        (Unit.Year, -1, Part.Whole) => new Period(PeriodKind.LastYear),
        _ => new Period(PeriodKind.Calendar, offset, unit) { Part = part },
    };

    /// Words made only of time pieces (“下午”, “那两天”, “十月”) that the reader didn't place.
    static readonly Regex TimeResidue = new(
        @"^[前后上下这今去昨大近过最内里来以之的个個几两半多零一二三四五六七八九十百天日周週星期礼拜月年号時时小钟分早晚午夜旬初底末季度凌晨傍中刚才些段阵那]+$", Options);
    static readonly Regex TimeUnitChar = new(@"[天日周週星期礼拜月年号時时午夜晨旬季]", Options);

    static bool IsTimeResidue(string word) => TimeResidue.IsMatch(word) && TimeUnitChar.IsMatch(word);
}
