using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Swivel;

/// Finds files from a plain sentence such as “the PDF about the quote I downloaded last week” or “上周的截图”
/// (FileSearch.swift). The same built-in rules read the type, time, folder and size; the files come from the index
/// Windows Search already keeps (SystemIndex, queried through ADO, which ships with Windows) instead of Spotlight.
/// Nothing leaves the PC and no permission is needed. Windows has no on-device language model on most PCs (the Mac
/// uses Apple Intelligence when it can), so the rules alone read requests here.
static partial class FileSearch
{
    public enum Kind { Image, Screenshot, Pdf, Document, Spreadsheet, Presentation, Video, Audio, Archive, Folder, App, Code, Text }

    /// Which date a time in the request refers to. Windows keeps no “date added”; a downloaded or received file's
    /// creation time is when it arrived, so Added reads the creation date.
    public enum DateField { Changed, Created, Used, Added }

    public enum Place { Downloads, Desktop, Documents, Pictures, Videos, Music, Applications }

    public enum Unit { Day, Week, Month, Year, Hour, Quarter }

    /// Which part of a day, week, month or year: morning / start of the week / 上旬 / 年初 (Early), afternoon / 中旬
    /// (Middle), evening / weekend / 月底 / 年底 (Late).
    public enum Part { Whole, Early, Middle, Late, FirstHalf, SecondHalf }

    /// `Calendar`: a whole calendar unit counted back from the current one (`Count` 0 = this, −1 = last, −2 = the one
    /// before). `Date`: a month or day of a year (`Count` = the year, 0 = the latest that has begun). `Weekday`: a
    /// weekday (`Count` 0 = the latest, −1 = in last week).
    public enum PeriodKind { Today, Yesterday, DayBeforeYesterday, ThisWeek, LastWeek, ThisMonth, LastMonth, ThisYear, LastYear, Recent, Past, Ago, Year,
        Calendar, Date, Weekday, Season }

    /// A time the request names, kept unresolved so the same plan gives the same dates in tests. `Count` is the N of
    /// “past N days” / “N weeks ago”, or the year.
    public sealed record Period(PeriodKind Kind, int Count = 0, Unit Unit = Unit.Day)
    {
        public int Month { get; init; }
        public int Day { get; init; }
        public DayOfWeek Weekday { get; init; }
        public Part Part { get; init; }
        /// “左右”, “about”, “两周多”: soft edges, so the search looks wider and files near the middle rank higher.
        public bool Loose { get; init; }
        /// “从上个月开始”, “since March”: from the start of the time until now.
        public bool Onward { get; init; }

        /// The range to search: the named range, widened where the words were vague (“最近”, “过去两周”, “两周前”,
        /// anything with “左右”), so a time read a little differently from what was meant still finds the file.
        public (DateTime Start, DateTime End) Window(DateTime now, DayOfWeek firstDay)
        {
            var (start, end) = Range(now, firstDay);
            if (!Loose && Kind is not (PeriodKind.Past or PeriodKind.Recent or PeriodKind.Ago)) return (start, end);
            var span = end - start;
            var margin = TimeSpan.FromTicks(Math.Clamp(span.Ticks / (Loose ? 2 : 4), TimeSpan.FromHours(12).Ticks, TimeSpan.FromDays(Loose ? 60 : 30).Ticks));
            var soon = now.AddMinutes(1);
            return (start - margin, end >= soon ? end : (end + margin < soon ? end + margin : soon));
        }

        /// Local start (inclusive) and end (exclusive).
        public (DateTime Start, DateTime End) Range(DateTime now, DayOfWeek firstDay)
        {
            if (Onward) return ((this with { Onward = false }).Range(now, firstDay).Start, now.AddMinutes(1));
            var today = now.Date;
            DateTime weekStart(DateTime day) => day.AddDays(-(((int)day.DayOfWeek - (int)firstDay + 7) % 7)).Date;
            DateTime monthStart(DateTime day) => new(day.Year, day.Month, 1);
            DateTime shifted(int n, Unit unit, DateTime from) => unit switch
            {
                Unit.Hour => from.AddHours(n),
                Unit.Day => from.AddDays(n),
                Unit.Week => from.AddDays(7 * n),
                Unit.Month => from.AddMonths(n),
                Unit.Quarter => from.AddMonths(3 * n),
                _ => from.AddYears(n),
            };
            DateTime align(Unit unit, DateTime day) => unit switch
            {
                Unit.Hour or Unit.Day => day,
                Unit.Week => weekStart(day),
                Unit.Month => monthStart(day),
                Unit.Quarter => new(day.Year, (day.Month - 1) / 3 * 3 + 1, 1),
                _ => new(day.Year, 1, 1),
            };
            // Weekends and “start of the week” go by Monday whatever the first day of the week is.
            DateTime monday(DateTime start) => start.AddDays(((int)DayOfWeek.Monday - (int)start.DayOfWeek + 7) % 7 is var d && d > 3 ? d - 7 : d);
            (DateTime, DateTime) portion(DateTime start, DateTime end, Unit unit)
            {
                if (Part == Part.Whole) return (start, end);
                if (Part is Part.FirstHalf or Part.SecondHalf)
                {
                    var middle = unit switch
                    {
                        Unit.Year => start.AddMonths(6),
                        Unit.Quarter => start.AddDays(45),
                        Unit.Month => start.AddDays(15),
                        Unit.Week => monday(start).AddDays(3),
                        _ => start.AddHours(12),
                    };
                    return Part == Part.FirstHalf ? (unit == Unit.Week ? monday(start) : start, middle) : (middle, unit == Unit.Week ? monday(start).AddDays(7) : end);
                }
                int i = (int)Part - 1;
                switch (unit)
                {
                    case Unit.Day:
                        double[] hours = [0, 12, 18, 28];
                        return (start.AddHours(hours[i]), start.AddHours(hours[i + 1]));
                    case Unit.Week:
                        var first = monday(start);
                        int[] days = [0, 2, 5, 7];
                        return (first.AddDays(Part == Part.Early ? 0 : days[i]), first.AddDays(Part == Part.Early ? 3 : days[i + 1]));
                    case Unit.Month:
                        return Part == Part.Late ? (start.AddDays(20), end) : (start.AddDays(10 * i), start.AddDays(10 * i + 10));
                    case Unit.Quarter:
                        return (start.AddMonths(i), start.AddMonths(i + 1));
                    default:
                        int[] months = [0, 3, 9, 12];
                        return (start.AddMonths(months[i]), start.AddMonths(months[i + 1]));
                }
            }
            var soon = now.AddMinutes(1);
            (DateTime, DateTime) capped((DateTime Start, DateTime End) range) => (range.Start, range.End > soon ? soon : range.End);
            switch (Kind)
            {
                case PeriodKind.Calendar:
                    // “周末” on a Wednesday is last weekend; “月底” on the 5th is last month's end.
                    for (int back = 0; ; back++)
                    {
                        var start = shifted(Count - back, Unit, align(Unit, today));
                        var range = portion(start, shifted(1, Unit, start), Unit);
                        if (range.Item1 <= now || back > 0) return capped(range);
                    }
                case PeriodKind.Date when Month == 0 && Day > 0:
                    // “14号”: the latest 14th that has come.
                    for (int back = 0; back < 13; back++)
                    {
                        var month = monthStart(today).AddMonths(-back);
                        if (Day > DateTime.DaysInMonth(month.Year, month.Month)) continue;
                        var day = month.AddDays(Day - 1);
                        if (day <= now) return (day, day.AddDays(1));
                    }
                    return (today, today.AddDays(1));
                case PeriodKind.Date:
                    // Count: the year, −N for “去年” / “前年”, 0 for the latest that has begun.
                    for (int year = Count > 0 ? Count : today.Year + Count; ; year--)
                    {
                        var start = new DateTime(Math.Clamp(year, 1, 9998), Month is >= 1 and <= 12 ? Month : 1,
                            Day > 0 && Month is >= 1 and <= 12 ? Math.Min(Day, DateTime.DaysInMonth(Math.Clamp(year, 1, 9998), Month)) : 1);
                        var unit = Month == 0 ? Unit.Year : Day == 0 ? Unit.Month : Unit.Day;
                        var range = portion(start, shifted(1, unit, start), unit);
                        if (range.Item1 <= now || Count != 0 || year < today.Year - 1) return capped(range);
                    }
                case PeriodKind.Season:
                    // `Month` is the season's first month; Count as for Date. Winter runs into the next year.
                    for (int year = Count > 0 ? Count : today.Year + Count; ; year--)
                    {
                        var start = new DateTime(Math.Clamp(year, 1, 9998), Math.Clamp(Month, 1, 12), 1);
                        if (start <= now || Count != 0 || year < today.Year - 1) return capped((start, start.AddMonths(3)));
                    }
                case PeriodKind.Weekday:
                {
                    var day = Count == 0 ? today : monday(weekStart(today)).AddDays(7 * Count + ((int)Weekday + 6) % 7);
                    if (Count == 0) while (day.DayOfWeek != Weekday) day = day.AddDays(-1);
                    return (day, day.AddDays(1));
                }
                case PeriodKind.Today: return (today, today.AddDays(1));
                case PeriodKind.Yesterday: return (today.AddDays(-1), today);
                case PeriodKind.DayBeforeYesterday: return (today.AddDays(-2), today.AddDays(-1));
                case PeriodKind.ThisWeek: return (weekStart(today), soon);
                case PeriodKind.LastWeek: return (weekStart(today).AddDays(-7), weekStart(today));
                case PeriodKind.ThisMonth: return (monthStart(today), soon);
                case PeriodKind.LastMonth: return (monthStart(today).AddMonths(-1), monthStart(today));
                case PeriodKind.ThisYear: return (new DateTime(today.Year, 1, 1), soon);
                case PeriodKind.LastYear: return (new DateTime(today.Year - 1, 1, 1), new DateTime(today.Year, 1, 1));
                case PeriodKind.Recent: return (today.AddDays(-14), soon);
                case PeriodKind.Past: return (shifted(-Math.Max(1, Count), Unit, now), soon);
                case PeriodKind.Ago when Unit == Unit.Day:
                    // “3天前” is that day; “十几天前”, “半个月前” are a few days either side.
                    var agoDay = today.AddDays(-Count);
                    int slack = Count <= 3 ? 0 : Math.Max(1, Count / 4);
                    return (agoDay.AddDays(-slack), agoDay.AddDays(1 + slack));
                case PeriodKind.Ago:
                    // “Two weeks ago” is loose: anything between one and three weeks back.
                    return (shifted(-(Count + 1), Unit, now), shifted(-Math.Max(0, Count - 1), Unit, now));
                default:
                    var january = new DateTime(Math.Clamp(Count, 1, 9998), 1, 1);
                    return (january, january.AddYears(1));
            }
        }

        public string Title => (Loose ? "≈ " : "") + Kind switch
        {
            _ when Onward || Kind is PeriodKind.Calendar or PeriodKind.Date or PeriodKind.Weekday or PeriodKind.Season => Dates(DateTime.Now),
            PeriodKind.Past when Unit == Unit.Hour => L("Past %d hours", Count),
            PeriodKind.Past or PeriodKind.Ago when Unit == Unit.Quarter => Dates(DateTime.Now),
            PeriodKind.Today => L("Today"),
            PeriodKind.Yesterday => L("Yesterday"),
            PeriodKind.DayBeforeYesterday => L("The day before yesterday"),
            PeriodKind.ThisWeek => L("This week"),
            PeriodKind.LastWeek => L("Last week"),
            PeriodKind.ThisMonth => L("This month"),
            PeriodKind.LastMonth => L("Last month"),
            PeriodKind.ThisYear => L("This year"),
            PeriodKind.LastYear => L("Last year"),
            PeriodKind.Recent => L("Recently"),
            PeriodKind.Past when Count == 1 => Unit switch
            {
                Unit.Day => L("Past day"),
                Unit.Week => L("Past week"),
                Unit.Month => L("Past month"),
                _ => L("Past year"),
            },
            PeriodKind.Ago when Count == 1 => Unit switch
            {
                Unit.Hour => L("An hour ago"),
                Unit.Day => L("A day ago"),
                Unit.Week => L("A week ago"),
                Unit.Month => L("A month ago"),
                _ => L("A year ago"),
            },
            PeriodKind.Ago when Unit == Unit.Hour => L("%d hours ago", Count),
            PeriodKind.Past => Unit switch
            {
                Unit.Day => L("Past %d days", Count),
                Unit.Week => L("Past %d weeks", Count),
                Unit.Month => L("Past %d months", Count),
                _ => L("Past %d years", Count),
            },
            PeriodKind.Ago => Unit switch
            {
                Unit.Day => L("%d days ago", Count),
                Unit.Week => L("%d weeks ago", Count),
                Unit.Month => L("%d months ago", Count),
                _ => L("%d years ago", Count),
            },
            _ => Count.ToString(CultureInfo.InvariantCulture),
        };

        /// The range in the user's own date format (“10月1日 – 10月14日”, “27 October 18:00 – 04:00”).
        string Dates(DateTime now)
        {
            var culture = CultureInfo.CurrentCulture;
            var (start, end) = Range(now, culture.DateTimeFormat.FirstDayOfWeek);
            var last = end.AddTicks(-1);
            bool wholeDays = start.TimeOfDay == TimeSpan.Zero && end.TimeOfDay == TimeSpan.Zero;
            // A range that runs until now ends today.
            if (end > now) (end, last, wholeDays) = (now.Date.AddDays(1), now.Date, start.TimeOfDay == TimeSpan.Zero);
            bool otherYear = start.Year != now.Year || last.Year != now.Year;
            string day(DateTime d) => d.ToString(otherYear ? "d" : "m", culture);
            if (!wholeDays) return day(start) + " " + start.ToString("t", culture) + " – " + end.ToString("t", culture);
            return start.Date == last.Date ? day(start) : day(start) + " – " + day(last);
        }
    }

    /// One thing the file should mention. Any of its forms (the word, a translation, a synonym) is enough.
    /// `Exact`: typed in quotes, the user's own words.
    public sealed record Term(IReadOnlyList<string> Forms, bool Exact = false)
    {
        public bool Equals(Term? other) => other is not null && Exact == other.Exact && Forms.SequenceEqual(other.Forms);
        public override int GetHashCode() => HashCode.Combine(Exact, string.Join("\u0001", Forms));
    }

    public sealed class Plan
    {
        /// What the file is about, in the user's own words: the request without its time, sender, place, type and
        /// filler, for finding files by meaning (SemanticIndex). Those parts are filters; compared as meaning, the
        /// “周” in “前两周存的密钥” would find weekly reports (周报).
        public string Topic { get; set; } = "";
        public List<Term> Terms { get; set; } = [];
        public List<Kind> Kinds { get; set; } = [];
        public List<string> Extensions { get; set; } = [];
        public Period? Period { get; set; }
        public DateField DateField { get; set; } = DateField.Changed;
        public Place? Place { get; set; }
        public long? MinBytes { get; set; }
        public long? MaxBytes { get; set; }
        /// “领导给的”, “sent to me”: files someone else sent, which arrive in Downloads or a chat app's folder.
        public bool Received { get; set; }

        public bool IsEmpty => Terms.Count == 0 && Kinds.Count == 0 && Extensions.Count == 0 && Period is null && Place is null
                               && MinBytes is null && MaxBytes is null && !Received;

        /// Anything besides the words: a type, time, folder, size or “sent to me”.
        public bool HasFilters => Kinds.Count > 0 || Extensions.Count > 0 || Period is not null || Place is not null
                                  || MinBytes is not null || MaxBytes is not null || Received;

        /// What the search looks for, for the hint under the results.
        public string Summary
        {
            get
            {
                var parts = new List<string>();
                if (Extensions.Count > 0) parts.Add(string.Join("/", Extensions.Take(2).Select(e => e.ToUpperInvariant())));
                else if (Kinds.Count > 0) parts.Add(string.Join("/", Kinds.Select(Title)));
                if (Period is { } period) parts.Add(period.Title);
                if (Place is { } place) parts.Add(Title(place));
                else if (Received) parts.Add(L("Sent to you"));
                if (MinBytes is { } min) parts.Add("> " + Bytes(min));
                if (MaxBytes is { } max) parts.Add("< " + Bytes(max));
                parts.AddRange(Terms.Select(t => "“" + string.Join(" / ", t.Forms.Take(3)) + "”"));
                return string.Join(" · ", parts);
            }
        }
    }

    /// `Meaning`: how close the file is to the request in meaning (0 when it isn't in the semantic index).
    public sealed record Hit(string Path, string Name, DateTime? Changed = null, DateTime? Used = null, DateTime? Added = null, bool Strict = true,
                             float Meaning = 0)
    {
        public DateTime? Latest => new[] { Changed, Used, Added }.Where(d => d.HasValue).Max();

        /// The date a time in the request was about.
        public DateTime? DateFor(DateField field) => field switch
        {
            DateField.Changed => Changed,
            DateField.Used => Used,
            _ => Added,
        };
    }

    public static string Title(Kind kind) => kind switch
    {
        Kind.Image => L("Images"),
        Kind.Screenshot => L("Screenshots"),
        Kind.Pdf => "PDF",
        Kind.Document => L("Documents"),
        Kind.Spreadsheet => L("Spreadsheets"),
        Kind.Presentation => L("Presentations"),
        Kind.Video => L("Videos"),
        Kind.Audio => L("Audio"),
        Kind.Archive => L("Archives"),
        Kind.Folder => L("Folders"),
        Kind.App => L("Apps"),
        Kind.Code => L("Code"),
        _ => L("Text files"),
    };

    public static string Title(Place place) => place switch
    {
        Place.Downloads => L("In Downloads"),
        Place.Desktop => L("On the Desktop"),
        Place.Documents => L("In Documents"),
        Place.Pictures => L("In Pictures"),
        Place.Videos => L("In Videos"),
        Place.Music => L("In Music"),
        _ => L("In the Start menu"),
    };

    /// Decimal units, as File Explorer's size column and the Mac app write them.
    static string Bytes(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1000 && unit < units.Length - 1) { value /= 1000; unit++; }
        return (unit == 0 ? value.ToString("0", CultureInfo.CurrentCulture) : value.ToString("0.#", CultureInfo.CurrentCulture)) + " " + units[unit];
    }

    // MARK: Reading the request

    enum CueKind { Kind, Place, Field, Period, Big, Small, Received, Filler }
    readonly record struct Cue(CueKind What, Kind Kind = default, Place Place = default, DateField Field = default, Period? Period = null);

    static Cue K(Kind kind) => new(CueKind.Kind, Kind: kind);
    static Cue P(Place place) => new(CueKind.Place, Place: place);
    static Cue F(DateField field) => new(CueKind.Field, Field: field);

    /// Words and phrases with a fixed meaning, longest first so “screen recording” wins over “recording”. Chinese
    /// and Japanese are matched anywhere, Latin words only as whole words. The same lists as the Mac app, with
    /// Windows' folder names.
    static readonly (Regex Pattern, Cue Cue)[] Lexicon = BuildLexicon();

    static (Regex, Cue)[] BuildLexicon()
    {
        var entries = new List<(string, Cue)>();
        void add(Cue cue, params string[] phrases) => entries.AddRange(phrases.Select(p => (p, cue)));
        add(P(Place.Downloads), "下载文件夹", "下载目录", "下载里", "下载中", "在下载", "downloads folder", "in downloads", "in my downloads", "downloads");
        add(P(Place.Desktop), "桌面上", "桌面", "desktop");
        add(P(Place.Documents), "文档文件夹", "文稿文件夹", "文档里", "文稿里", "文稿中", "在文稿", "documents folder", "in documents", "in my documents");
        add(P(Place.Pictures), "图片文件夹", "pictures folder");
        add(P(Place.Videos), "视频文件夹", "影片文件夹", "videos folder", "movies folder");
        add(P(Place.Music), "音乐文件夹", "music folder");
        add(P(Place.Applications), "开始菜单", "应用程序文件夹", "start menu", "applications folder");
        add(K(Kind.Screenshot), "截图", "截屏", "屏幕快照", "屏幕截图", "スクリーンショット", "screenshot", "screenshots", "screen shot", "screen shots");
        add(K(Kind.Image), "图片", "照片", "相片", "图像", "画像", "写真", "image", "images", "photo", "photos", "picture", "pictures", "pic", "pics");
        add(K(Kind.Pdf), "pdf", "pdfs");
        add(K(Kind.Document), "word 文档", "文档", "文書", "word", "doc", "docs", "document", "documents");
        add(K(Kind.Spreadsheet), "电子表格", "表格", "excel", "spreadsheet", "spreadsheets");
        add(K(Kind.Presentation), "演示文稿", "幻灯片", "ppt", "powerpoint", "keynote", "slides", "slide deck", "deck", "presentation", "presentations");
        add(K(Kind.Video), "视频", "影片", "录屏", "录像", "电影", "動画", "video", "videos", "movie", "movies", "screen recording", "screen recordings");
        add(K(Kind.Audio), "音频", "音乐", "歌曲", "录音", "语音", "audio", "music", "song", "songs", "recording", "recordings", "voice memo", "voice memos", "podcast");
        add(K(Kind.Archive), "压缩包", "压缩文件", "安装包", "archive", "archives", "zip file", "zip files");
        add(K(Kind.Folder), "文件夹", "目录", "フォルダ", "folder", "folders", "directory");
        add(K(Kind.App), "应用程序", "应用", "软件", "程序", "アプリ", "app", "apps", "application", "applications", "program", "programs");
        add(K(Kind.Code), "源代码", "代码", "脚本", "source code", "code", "script", "scripts");
        add(K(Kind.Text), "纯文本", "文本文件", "text file", "text files");
        add(F(DateField.Used), "打开过的", "打开过", "打开的", "用过的", "用过", "看过的", "看过", "opened", "used", "viewed");
        add(F(DateField.Changed), "修改过的", "修改过", "改过的", "改过", "编辑过", "modified", "changed", "edited", "updated");
        add(F(DateField.Created), "创建的", "新建的", "拍摄的", "拍的", "created", "made", "taken", "shot");
        add(F(DateField.Added), "下载的", "下载", "下的", "保存的", "存的", "存下来的", "downloaded", "saved", "got");
        // Someone else sent it: who it was doesn't appear in the file, where it arrived does.
        add(new Cue(CueKind.Received), "领导给的", "领导发的", "领导给我的", "领导发给我的", "领导发来的", "老板给的", "老板发的", "老板发来的",
            "上司给的", "上司发的", "经理给的", "经理发的", "主管给的", "主管发的", "老师给的", "老师发的", "老师发来的", "教授发的",
            "同事给的", "同事发的", "同学给的", "同学发的", "客户给的", "客户发的", "朋友给的", "朋友发的", "别人给的", "别人发的",
            "别人传的", "发给我的", "发我的", "传给我的", "发来的", "收到的", "接收的",
            "sent to me", "sent me", "someone sent", "shared with me", "from my boss", "from my manager", "from my teacher",
            "from my professor", "my boss sent", "boss sent", "manager sent", "teacher sent", "received");
        add(new Cue(CueKind.Big), "大文件", "很大的", "big files", "big file", "large files", "large file", "huge", "big", "large");
        add(new Cue(CueKind.Small), "小文件", "small files", "small file", "tiny");
        add(new Cue(CueKind.Filler), "帮我找一下", "帮我找", "帮我搜", "帮我", "给我", "找一下", "搜一下", "找到", "查找", "搜索", "寻找", "找找", "找", "搜",
            "一下", "那个", "那些", "那份", "那张", "那几个", "这个", "这些", "我的", "我们的", "有关", "关于", "相关的", "相关",
            "一个", "一份", "一张", "文件", "东西", "里面的", "里面", "里的", "上的", "中的", "以前的", "之前的", "时候", "的", "了", "吗",
            "内容", "资料", "材料", "方面的", "方面", "要用的", "要用", "用的", "需要的", "要交的", "相关内容", "什么的", "之类的", "那种",
            "呢", "和", "跟", "与", "或者", "还是", "从", "在", "是", "有", "ファイル", "探して",
            "那会儿", "那会", "那时候", "那时", "的时候", "左右", "前后", "上次", "上回", "那次", "大概", "大约", "差不多",
            "find", "search", "look", "looking", "for", "show", "me", "my", "mine", "our", "the", "a", "an", "that", "this", "those",
            "these", "some", "file", "files", "from", "in", "on", "at", "of", "with", "about", "regarding", "which", "what", "where",
            "i", "we", "you", "it", "is", "was", "were", "are", "named", "called", "titled", "please", "can", "could", "get", "open",
            "thing", "stuff", "one", "ones", "to", "and", "or", "by", "there", "had", "have", "all", "any", "ago");
        return entries.OrderByDescending(e => e.Item1.Length).Select(e =>
        {
            var escaped = Regex.Escape(e.Item1).Replace("\\ ", "\\s+");
            bool latin = e.Item1.All(c => c < 128);
            var pattern = latin ? @"(?<![\p{L}\p{N}])" + escaped + @"(?![\p{L}\p{N}])" : escaped;
            return (new Regex(pattern, Options), e.Item2);
        }).ToArray();
    }

    const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// Keys and passwords: matched in names and folders only. “key”, “token” and “password” appear in the text of
    /// countless documents (Keynote, “key concepts”), and a file holding a random key string has no meaning to compare.
    static readonly string[] SecretForms = ["密钥", "秘钥", "私钥", "密码", "凭证", "key", "keys", "api key", "apikey", "token", "secret", "credential", "password"];

    static bool NamesOnly(Term term) => term.Forms.SequenceEqual(SecretForms);

    /// What people call a kind of file versus what the files are named or say (FileSearch.swift has the same table):
    /// “找工作” finds résumés and cover letters, “操作系统” English “operating system” notes. When a request names one
    /// of the left-hand phrases, the search looks for any of the right-hand words instead (one term, any form).
    /// Latin words of three letters or fewer are matched as whole words (“os”, “cv”), longer ones as prefixes.
    static readonly (string[] Triggers, string[] Forms)[] ConceptGroups =
    [
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
        (["密钥", "秘钥", "私钥", "公钥", "密码", "口令", "凭证", "api key", "apikey", "access key", "secret key", "private key", "ssh key",
          "token", "password", "credential", "secret"], SecretForms),
    ];

    static readonly (Regex Pattern, string[] Forms)[] Concepts = ConceptGroups
        .SelectMany(g => g.Triggers.Select(t => (Trigger: t, g.Forms)))
        .OrderByDescending(e => e.Trigger.Length)
        .Select(e =>
        {
            var escaped = Regex.Escape(e.Trigger).Replace("\\ ", "\\s+");
            // Latin triggers are whole words and may be plural (“lectures”, “invoices”).
            var pattern = e.Trigger.All(c => c < 128) ? @"(?<![\p{L}\p{N}])" + escaped + @"(?:s|es)?(?![\p{L}\p{N}])" : escaped;
            return (new Regex(pattern, Options), e.Forms);
        }).ToArray();

    static bool IsShortLatin(string form) => form.Length <= 3 && form.All(c => c < 128 && char.IsLetterOrDigit(c));

    /// Extensions that are clearly file types (not words such as “key” or “pages”); spelling variants travel together.
    static readonly string[][] ExtensionGroups =
    [
        ["jpg", "jpeg"], ["png"], ["heic"], ["gif"], ["webp"], ["tif", "tiff"], ["svg"], ["psd"], ["raw", "dng", "cr2", "nef", "arw"],
        ["mp4"], ["mov"], ["m4v"], ["avi"], ["mkv"], ["mp3"], ["m4a"], ["wav"], ["aac"], ["flac"], ["zip"], ["rar"], ["7z"], ["dmg"],
        ["docx"], ["xlsx"], ["xls"], ["csv"], ["pptx"], ["txt"], ["md", "markdown"], ["json"], ["swift"], ["py"], ["js"], ["ts"],
        ["html", "htm"], ["epub"], ["sketch"], ["fig"], ["srt"], ["ics"], ["vcf"], ["exe"], ["msi"],
    ];
    static readonly Regex[] ExtensionPatterns = ExtensionGroups
        .Select(g => new Regex(@"(?<![\p{L}\p{N}])\.?(?:" + string.Join("|", g) + @")(?![\p{L}\p{N}])", Options)).ToArray();

    const string SizeUnit = @"(\d+(?:\.\d+)?)\s*(?:(k|m|g|t)b?|(兆))(?![a-z])";
    static readonly Regex MinSize = new(@"(?:大于|超过|多于|高于|至少|不小于|>=?|bigger than|larger than|more than|greater than|over|above|at least)\s*" + SizeUnit, Options);
    static readonly Regex MaxSize = new(@"(?:小于|不到|少于|低于|不超过|不大于|<=?|smaller than|less than|under|below|at most)\s*" + SizeUnit, Options);
    static readonly Regex BigBefore = new(@"(?:很|超)?大(?=图片|照片|图|视频|影片|文件|文档|压缩包|的)", Options);
    static readonly Regex SmallBefore = new(@"(?:很)?小(?=图片|照片|图|视频|文件|文档|的)", Options);
    static readonly Regex Quoted = new(@"[""“”「」『』‘’]([^""“”「」『』‘’]+)[""“”「」『』‘’]", Options);
    const string Cjk = @"\p{IsCJKUnifiedIdeographs}\p{IsHiragana}\p{IsKatakana}";
    static readonly Regex CjkThenLatin = new("([" + Cjk + "])([a-z0-9])", Options);
    static readonly Regex LatinThenCjk = new("([a-z0-9])([" + Cjk + "])", Options);

    public static int? ParseNumber(string text)
    {
        text = text.Trim().ToLowerInvariant();
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)) return value;
        var words = new Dictionary<string, int>
        {
            ["a"] = 1, ["an"] = 1, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7,
            ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["a few"] = 3, ["few"] = 3, ["a couple of"] = 2, ["couple of"] = 2, ["几"] = 3,
        };
        if (words.TryGetValue(text, out value)) return value;
        var digits = new Dictionary<char, int> { ['一'] = 1, ['二'] = 2, ['两'] = 2, ['三'] = 3, ['四'] = 4, ['五'] = 5, ['六'] = 6, ['七'] = 7, ['八'] = 8, ['九'] = 9 };
        int? digit(char c) => digits.TryGetValue(c, out var d) ? d : null;
        return text.Length switch
        {
            1 => text[0] == '十' ? 10 : digit(text[0]),
            2 when text[0] == '十' => digit(text[1]) + 10,
            2 when text[1] == '十' => digit(text[0]) * 10,
            3 when text[1] == '十' => digit(text[0]) * 10 + digit(text[2]),
            _ => null,
        };
    }

    /// Reads a request with the built-in rules. Gives the same plan for the same words.
    public static Plan Parse(string request)
    {
        var plan = new Plan();
        var topic = new List<string>();
        bool wantsBig = false;
        // Full-width letters and digits (ｐｄｆ, １０) read like ASCII.
        var text = request.Normalize(NormalizationForm.FormKC);
        string consume(Regex pattern, string from) => pattern.Replace(from, " ");

        foreach (Match match in Quoted.Matches(text))
            if (match.Groups[1].Value.Trim() is { Length: > 0 } phrase) { plan.Terms.Add(new Term([phrase], Exact: true)); topic.Add(phrase); }
        text = consume(Quoted, text).ToLowerInvariant();
        // Separate Han and Latin runs, so “报价单pdf” reads as two words.
        text = LatinThenCjk.Replace(CjkThenLatin.Replace(text, "$1 $2"), "$1 $2");

        foreach (var (pattern, isMinimum) in new[] { (MinSize, true), (MaxSize, false) })
        {
            var match = pattern.Match(text);
            if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) continue;
            // 兆 (group 3) is megabytes, like a bare “m”.
            double scale = (match.Groups[2].Success ? match.Groups[2].Value : "m") switch
            {
                "k" => 1e3,
                "g" => 1e9,
                "t" => 1e12,
                _ => 1e6,
            };
            if (isMinimum) plan.MinBytes = (long)(value * scale); else plan.MaxBytes = (long)(value * scale);
            text = consume(pattern, text);
        }
        // “大图片”, “很大的视频”, “小文件”: 大/小 right before a kind or 的.
        if (BigBefore.IsMatch(text)) { wantsBig = true; text = consume(BigBefore, text); }
        if (SmallBefore.IsMatch(text)) { plan.MaxBytes ??= 1_000_000; text = consume(SmallBefore, text); }
        plan.Period = ReadTime(ref text);
        for (int i = 0; i < ExtensionGroups.Length; i++)
        {
            if (!ExtensionPatterns[i].IsMatch(text)) continue;
            plan.Extensions.AddRange(ExtensionGroups[i]);
            text = consume(ExtensionPatterns[i], text);
        }
        // Concepts before the fixed words, so “找工作” isn't read as the filler “找” plus “工作”.
        foreach (var (pattern, forms) in Concepts)
        {
            if (pattern.Match(text) is not { Success: true } match) continue;
            if (!plan.Terms.Any(t => t.Forms.SequenceEqual(forms))) plan.Terms.Add(new Term(forms));
            topic.Add(match.Value);
            text = consume(pattern, text);
        }
        foreach (var (pattern, cue) in Lexicon)
        {
            if (!pattern.IsMatch(text)) continue;
            switch (cue.What)
            {
                case CueKind.Kind: if (!plan.Kinds.Contains(cue.Kind)) plan.Kinds.Add(cue.Kind); break;
                case CueKind.Place: plan.Place ??= cue.Place; break;
                case CueKind.Field: plan.DateField = cue.Field; break;
                case CueKind.Period: plan.Period ??= cue.Period; break;
                case CueKind.Big: wantsBig = true; break;
                case CueKind.Small: plan.MaxBytes ??= 1_000_000; break;
                case CueKind.Received:
                    plan.Received = true;
                    plan.DateField = DateField.Added;
                    break;
            }
            text = consume(pattern, text);
        }
        // A screenshot is an image already; asking for both means screenshots.
        if (plan.Kinds.Contains(Kind.Screenshot)) plan.Kinds.Remove(Kind.Image);
        if (plan.Extensions.Count > 0) plan.Kinds.RemoveAll(k => k != Kind.Screenshot);
        // “Big” depends on the kind: a 20 MB photo is big, a 20 MB video is not.
        if (wantsBig && plan.MinBytes is null)
        {
            Kind[] small = [Kind.Image, Kind.Screenshot, Kind.Pdf, Kind.Document, Kind.Spreadsheet, Kind.Presentation, Kind.Text, Kind.Code];
            plan.MinBytes = plan.Kinds.Count > 0 && plan.Kinds.All(small.Contains) ? 10_000_000 : 100_000_000;
        }
        foreach (var word in Words(text))
        {
            // Pieces of a time the reader couldn't place (“下午”, “那两天”) are not something the file mentions.
            if (IsTimeResidue(word)) continue;
            if (!plan.Terms.Any(t => t.Forms.Contains(word))) plan.Terms.Add(new Term([word]));
            if (!topic.Contains(word)) topic.Add(word);
        }
        if (plan.Terms.Count > 6) plan.Terms = plan.Terms.Take(6).ToList();
        plan.Topic = string.Join(" ", topic);
        return plan;
    }

    static bool IsCjk(char c) => c is >= '぀' and <= 'ヿ' or >= '㐀' and <= '䶿' or >= '一' and <= '鿿' or >= '豈' and <= '﫿';
    static bool IsKana(char c) => c is >= '぀' and <= 'ヿ';

    /// What is left after the rules: Latin words of two letters or more, numbers, and Chinese or Japanese words
    /// (split with Windows' word segmenter; a lone character stays only when it is all there is, as in “猫的照片”).
    public static List<string> Words(string text)
    {
        var result = new List<string>();
        var piece = new StringBuilder();
        void flush()
        {
            if (piece.Length == 0) return;
            var word = piece.ToString();
            piece.Clear();
            if (word.Any(IsCjk))
            {
                var tokens = Segment(word, word.Any(IsKana) ? "ja" : "zh-Hans");
                var longer = tokens.Where(t => t.Length >= 2).ToList();
                result.AddRange(longer.Count > 0 ? longer : tokens);
            }
            else if (word.Length >= 2 || word.All(char.IsDigit)) result.Add(word);
        }
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c)) flush();
            else piece.Append(c);
        }
        flush();
        return result.Distinct().ToList();
    }

    static List<string> Segment(string text, string language)
    {
        try
        {
            var tokens = new Windows.Data.Text.WordsSegmenter(language).GetTokens(text).Select(t => t.Text).Where(t => t.Length > 0).ToList();
            // The segmenter splits words it doesn't know (“微信” → 微 / 信): runs of single characters are joined back.
            var joined = new List<(string Text, bool Singles)>();
            foreach (var token in tokens)
            {
                if (token.Length == 1 && joined.Count > 0 && joined[^1].Singles) joined[^1] = (joined[^1].Text + token, true);
                else joined.Add((token, token.Length == 1));
            }
            if (joined.Count > 0) return joined.Select(j => j.Text).ToList();
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or TypeLoadException) { }
        return [text];
    }

    /// “invoices” also finds “invoice”: Latin plurals are looked up by their singular as well.
    public static string Stem(string word)
    {
        if (!word.All(c => c < 128) || word.Length <= 3) return word;
        if (word.EndsWith("ies", StringComparison.Ordinal) && word.Length > 4) return word[..^3] + "y";
        if (word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal) && !word.EndsWith("us", StringComparison.Ordinal)
            && !word.EndsWith("is", StringComparison.Ordinal)) return word[..^1];
        return word;
    }

    // MARK: Windows Search

    static readonly Dictionary<Kind, string[]> KindExtensions = new()
    {
        [Kind.Pdf] = [".pdf"],
        [Kind.Document] = [".doc", ".docx", ".rtf", ".odt", ".pages", ".wps"],
        [Kind.Spreadsheet] = [".xls", ".xlsx", ".csv", ".ods", ".numbers", ".et"],
        [Kind.Presentation] = [".ppt", ".pptx", ".odp", ".key", ".dps"],
        [Kind.Archive] = [".zip", ".rar", ".7z", ".tar", ".gz", ".iso", ".cab"],
        [Kind.Code] = [".cs", ".swift", ".py", ".js", ".ts", ".java", ".c", ".cpp", ".h", ".go", ".rs", ".rb", ".php", ".sh", ".ps1", ".kt"],
        [Kind.Text] = [".txt", ".md", ".markdown", ".log"],
    };

    /// System.Kind values for the kinds Windows itself classifies.
    static readonly Dictionary<Kind, string> SystemKinds = new()
    {
        [Kind.Image] = "picture", [Kind.Video] = "video", [Kind.Audio] = "music", [Kind.Folder] = "folder", [Kind.App] = "program",
    };

    /// Windows Search SQL: a literal in single quotes; inside CONTAINS, words are double-quoted prefixes, so quotes,
    /// wildcards and the SQL quote are dropped from them.
    static string Word(string text) => new string(text.Where(c => c is not ('"' or '\'' or '*' or '%' or '[' or ']')).ToArray()).Trim();
    static string Sql(string text) => text.Replace("'", "''");

    static string ExtensionClause(IEnumerable<string> extensions) =>
        "(" + string.Join(" OR ", extensions.Select(e => $"System.FileExtension = '{Sql(e.StartsWith('.') ? e : "." + e)}'")) + ")";

    static string KindClause(Kind kind)
    {
        if (kind == Kind.Screenshot)
        {
            // Windows marks no screenshot attribute: Snipping Tool, Print Screen and OEM tools save into a
            // “Screenshots” / “…截图” folder or name the file “Screenshot …” / “屏幕截图 …”.
            const string names = "'\"screenshot*\" OR \"截图*\" OR \"截屏*\" OR \"屏幕截图*\" OR \"スクリーンショット*\"'";
            return $"(System.Kind = 'picture' AND (CONTAINS(System.ItemFolderNameDisplay, {names}) OR CONTAINS(System.FileName, {names})))";
        }
        if (SystemKinds.TryGetValue(kind, out var systemKind))
            return kind == Kind.App ? "(System.Kind = 'program' OR System.FileExtension = '.lnk')" : $"System.Kind = '{systemKind}'";
        return ExtensionClause(KindExtensions[kind]);
    }

    /// Files a chat app saved, found by the folder they sit in (Windows keeps no “where from” for them).
    static readonly Dictionary<string, string> SourceFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["微信"] = "\"xwechat_files\" OR \"WeChat Files\"", ["wechat"] = "\"xwechat_files\" OR \"WeChat Files\"",
        ["weixin"] = "\"xwechat_files\" OR \"WeChat Files\"", ["qq"] = "\"Tencent Files\"", ["钉钉"] = "\"DingTalk\"",
    };

    static string TermClause(Term term)
    {
        var spellings = new List<string>();
        foreach (var form in term.Forms)
            foreach (var spelling in new[] { form, Stem(form) })
                if (Word(spelling) is { Length: > 0 } word && !spellings.Contains(word)) spellings.Add(word);
        if (spellings.Count == 0) return "(1 = 0)";
        // A word prefix in the name, the folders it sits in, or anywhere in the indexed text and properties
        // (CONTAINS(*) leaves out the name). Short Latin words are whole words: “os*” would find every “oscar”.
        var condition = Sql(string.Join(" OR ", spellings.Select(s => IsShortLatin(s) ? $"\"{s}\"" : $"\"{s}*\"")));
        var clauses = new List<string> { $"CONTAINS(System.FileName, '{condition}')", $"CONTAINS(System.ItemFolderPathDisplay, '{condition}')" };
        if (!NamesOnly(term)) clauses.Add($"CONTAINS(*, '{condition}')");
        foreach (var form in term.Forms)
            if (SourceFolders.TryGetValue(form, out var folders)) clauses.Add($"CONTAINS(System.ItemPathDisplay, '{Sql(folders)}')");
        return "(" + string.Join(" OR ", clauses.Distinct()) + ")";
    }

    static string DateColumn(DateField field) => field switch
    {
        DateField.Changed => "System.DateModified",
        DateField.Used => "System.DateAccessed",
        _ => "System.DateCreated",
    };

    /// The WHERE conditions for a plan (without the scope): every filter, and every term (`strict`) or any one of
    /// them. Null when the plan asks for nothing. Dates are written in UTC, as the index stores them.
    public static string? Conditions(Plan plan, bool strict = true, DateTime? now = null, DayOfWeek? firstDay = null, TimeZoneInfo? zone = null)
    {
        var parts = new List<string>();
        if (plan.Extensions.Count > 0) parts.Add(ExtensionClause(plan.Extensions));
        else if (plan.Kinds.Count > 0) parts.Add("(" + string.Join(" OR ", plan.Kinds.Select(KindClause)) + ")");
        if (plan.Period is { } period)
        {
            zone ??= TimeZoneInfo.Local;
            var local = now ?? TimeZoneInfo.ConvertTime(DateTime.UtcNow, zone);
            var (start, end) = period.Window(local, firstDay ?? CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek);
            string utc(DateTime time) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(time, DateTimeKind.Unspecified), zone)
                .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            parts.Add($"{DateColumn(plan.DateField)} >= '{utc(start)}'");
            parts.Add($"{DateColumn(plan.DateField)} < '{utc(end)}'");
        }
        if (plan.MinBytes is { } min) parts.Add($"System.Size >= {min}");
        if (plan.MaxBytes is { } max) parts.Add($"System.Size <= {max}");
        if (plan.Received && plan.Place is null) parts.Add(ReceivedClause());
        if (plan.Terms.Count > 0)
        {
            var terms = plan.Terms.Select(TermClause);
            parts.Add(strict ? string.Join(" AND ", terms) : "(" + string.Join(" OR ", terms) + ")");
        }
        return parts.Count == 0 ? null : string.Join(" AND ", parts);
    }

    /// Where files other people send end up: Downloads (mail, browsers, Teams) and the chat apps' own folders.
    static string ReceivedClause()
    {
        var chats = string.Join(" OR ", ChatMarkers.Select(m => $"\"{m}\""));
        return $"(SCOPE='file:{Sql(PlaceFolder(Place.Downloads).Replace('\\', '/'))}' OR CONTAINS(System.ItemPathDisplay, '{Sql(chats)}'))";
    }

    static readonly string[] ChatMarkers = ["xwechat_files", "WeChat Files", "Tencent Files", "DingTalk", "Feishu", "Lark"];

    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static IReadOnlyList<string> Scopes(Plan plan)
    {
        if (plan.Place is Place.Applications)
            return [Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)];
        if (plan.Place is { } place) return [PlaceFolder(place)];
        var scopes = new List<string> { Home };
        if (plan.Kinds.Contains(Kind.App))
            scopes.AddRange([Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)]);
        return scopes;
    }

    /// The real folder (Desktop and Documents may live in OneDrive).
    public static string PlaceFolder(Place place) => place switch
    {
        Place.Downloads => RopeNative.DownloadsFolder(),
        Place.Desktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Place.Documents => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        Place.Pictures => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        Place.Videos => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        Place.Music => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        _ => Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
    };

    public static string? QueryString(Plan plan, bool strict = true, int limit = 400, DateTime? now = null, DayOfWeek? firstDay = null, TimeZoneInfo? zone = null)
    {
        if (Conditions(plan, strict, now, firstDay, zone) is not { } conditions) return null;
        var scopes = string.Join(" OR ", Scopes(plan).Where(s => s.Length > 0).Select(s => $"SCOPE='file:{Sql(s.Replace('\\', '/'))}'"));
        return $"SELECT TOP {limit} System.ItemUrl, System.FileName, System.DateModified, System.DateAccessed, System.DateCreated " +
               $"FROM SystemIndex WHERE ({scopes}) AND {conditions} ORDER BY System.DateModified DESC";
    }

    /// Leaves out what people don't mean by “my files”: hidden items, AppData, the Recycle Bin, build output,
    /// and chat apps' encrypted image caches.
    public static bool IsVisible(string path, string? home = null)
    {
        home ??= Home;
        var components = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (components.Any(c => c.StartsWith('.') || c is "node_modules" or "__pycache__" or "$Recycle.Bin" or "bin" or "obj"
                                || c.Equals("anaconda3", StringComparison.OrdinalIgnoreCase) || c.Equals("miniconda3", StringComparison.OrdinalIgnoreCase)
                                || c.Equals("site-packages", StringComparison.OrdinalIgnoreCase) || c.Equals("venv", StringComparison.OrdinalIgnoreCase)
                                // WeCom's built-in browser profile (cookies, “Trust Tokens”).
                                || c.Equals("qtCef", StringComparison.OrdinalIgnoreCase) || c.EndsWith("CefCache", StringComparison.OrdinalIgnoreCase))) return false;
        if (path.Contains("\\WXWork\\Default\\", StringComparison.OrdinalIgnoreCase)) return false;
        if (path.StartsWith(System.IO.Path.Combine(home, "AppData") + "\\", StringComparison.OrdinalIgnoreCase)) return false;
        var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".dat" or ".tmp" or ".crdownload" or ".part") return false;
        // Inside WeChat's own folder only the files and videos people were sent are theirs; the rest (stickers,
        // thumbnails, resources) is the app's.
        if (ChatFolder(path) is { } inside)
            return inside.Contains(@"\msg\file\", StringComparison.OrdinalIgnoreCase) || inside.Contains(@"\msg\video\", StringComparison.OrdinalIgnoreCase)
                   || inside.Contains(@"\FileStorage\File\", StringComparison.OrdinalIgnoreCase) || inside.Contains(@"\FileStorage\Video\", StringComparison.OrdinalIgnoreCase);
        return true;
    }

    /// The part of a path from WeChat's data folder on (“…\xwechat_files\wxid…\msg\file\2026-10\a.pdf”), or null.
    static string? ChatFolder(string path)
    {
        foreach (var marker in new[] { @"\xwechat_files\", @"\WeChat Files\" })
        {
            int at = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at >= 0) return path[at..];
        }
        return null;
    }

    /// Machine files whose text happens to contain a word (source code, JSON, logs) rarely are the file someone means.
    static readonly HashSet<string> MachineExtensions = [".cs", ".swift", ".py", ".js", ".ts", ".java", ".c", ".cpp", ".h", ".go", ".rs", ".json",
        ".xml", ".log", ".yaml", ".yml", ".tl", ".hpp", ".css", ".html", ".map"];
    /// Generated files people rarely look for by hand.
    static readonly HashSet<string> CacheFolders = new(StringComparer.OrdinalIgnoreCase) { "cache", "caches", ".cache", "tmp", "temp", "render_tmp", "build", "dist", "out" };

    static bool Has(string text, string word) => CultureInfo.InvariantCulture.CompareInfo.IndexOf(text, word,
        CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreWidth | CompareOptions.IgnoreKanaType) >= 0;

    /// Whether a name or folder mentions a form: short Latin words (“os”, “cv”, “uni”) only as words of their own,
    /// so “photos” doesn't mention “os”; everything else anywhere (by its singular).
    static bool Mentions(string text, string form)
    {
        if (!IsShortLatin(form)) return Has(text, Stem(form));
        return Regex.IsMatch(text, @"(?<![\p{L}])" + Regex.Escape(form) + @"(?![\p{L}])", Options);
    }

    /// Orders hits: everything-matched first, then names that contain the words, then what was used or changed most
    /// recently, then shallower paths. Code and data files matched only by their text sink, unless code was asked for.
    public static List<Hit> Rank(IEnumerable<Hit> hits, Plan plan, DateTime? now = null)
    {
        var utcNow = now ?? DateTime.UtcNow;
        hits = hits.ToList();
        float closest = hits.Select(h => h.Meaning).DefaultIfEmpty(0).Max();
        // The search looks a little wider than the time named; files inside it come first.
        var window = plan.Period?.Range(utcNow.ToLocalTime(), CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek);
        double score(Hit hit)
        {
            double s = 0;
            if (window is var (from, to) && hit.DateFor(plan.DateField) is { } date && date.ToLocalTime() is var local && local >= from && local < to) s += 15;
            return s + scoreRest(hit);
        }
        double scoreRest(Hit hit)
        {
            double s = hit.Strict ? 100 : 0;
            int inName = plan.Terms.Count(t => t.Forms.Any(f => Mentions(hit.Name, f)));
            s += inName * 25;
            // A folder named after the words (“OneDrive\工作”, “Uni”) counts for a little less than the name.
            var folder = System.IO.Path.GetDirectoryName(hit.Path) ?? "";
            if (folder.StartsWith(Home, StringComparison.OrdinalIgnoreCase)) folder = folder[Home.Length..];
            s += plan.Terms.Count(t => !t.Forms.Any(f => Mentions(hit.Name, f)) && t.Forms.Any(f => Mentions(folder, f))) * 12;
            if (plan.Terms.Count > 0 && inName == plan.Terms.Count) s += 10;
            var extension = System.IO.Path.GetExtension(hit.Path).ToLowerInvariant();
            if (plan.Terms.Count > 0 && inName == 0 && !plan.Kinds.Contains(Kind.Code) && MachineExtensions.Contains(extension)) s -= 60;
            // Installers aren't what “工作内容” or “the quote” means.
            if (extension is ".exe" or ".msi" or ".msix" or ".appx" && !plan.Kinds.Contains(Kind.App) && plan.Extensions.Count == 0) s -= 40;
            // Closeness in meaning: nothing below “related”, up to 90 for a near-perfect match. When the meaning is
            // known, words found only somewhere in a document's text (not its name or folder) count for less: “签证”
            // in a résumé is a coincidence, the visa checklist is what was meant. Unrelated ones drop further.
            if (hit.Meaning > 0)
            {
                // Measured against the closest file for this request: e5's similarities crowd together (most
                // documents sit at 0.80–0.90), so what counts is being near the top, not the number itself.
                s += Math.Clamp((hit.Meaning - (closest - 0.08)) / 0.08, 0, 1) * 90;
                bool named = plan.Terms.Any(t => t.Forms.Any(f => Mentions(hit.Name, f) || Mentions(folder, f)));
                if (!named && hit.Strict) s -= 60;
                if (!named && hit.Meaning < SemanticIndex.Related) s -= 40;
            }
            if (hit.Latest is { } latest) s += 20 * Math.Exp(-Math.Max(0, (utcNow - latest).TotalDays) / 45);
            if (hit.Used is { } used && (utcNow - used).TotalDays < 14) s += 5;
            var components = hit.Path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            s -= components.Length * 0.3;
            if (components.SkipLast(1).Any(CacheFolders.Contains)) s -= 15;
            return s;
        }
        return hits.Select(h => (hit: h, score: score(h)))
            .OrderByDescending(x => x.score)
            .ThenByDescending(x => x.hit.Latest ?? DateTime.MinValue)
            .ThenBy(x => x.hit.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(x => x.hit).ToList();
    }

    /// Runs the plan against the Windows Search index. When the strict query finds few files, looser ones fill in
    /// below it: any of the words, then (when the request also named a time, type, folder or sender) the files that
    /// fit those alone, since abstract words (“工作内容”) often aren't in the file at all.
    public static async Task<List<Hit>> Search(Plan plan, int limit = 30, CancellationToken cancel = default)
    {
        if (QueryString(plan, strict: true) is not { } strict) return [];
        var hits = (await Task.Run(() => Query(strict, cancel), cancel)).Where(h => IsVisible(h.Path)).ToList();
        var known = hits.Select(h => h.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        async Task fill(string? sql)
        {
            if (sql is null || hits.Count >= 8 || cancel.IsCancellationRequested) return;
            foreach (var hit in await Task.Run(() => Query(sql, cancel), cancel))
                if (IsVisible(hit.Path) && known.Add(hit.Path)) hits.Add(hit with { Strict = false });
        }
        if (plan.Terms.Count > 1) await fill(QueryString(plan, strict: false));
        // A time alone (“最近的大学文件”) would only bring unrelated recent files.
        if (plan.Terms.Count > 0 && (plan.Received || plan.Place is not null || plan.Kinds.Count > 0 || plan.Extensions.Count > 0))
            await fill(QueryString(FiltersOnly(plan), strict: true));
        if (plan.Terms.Any(t => !NamesOnly(t)) && SemanticIndex.Enabled && !cancel.IsCancellationRequested)
            await Task.Run(() => AddMeaning(hits, known, plan), cancel);
        return Rank(hits, plan).Take(limit).ToList();
    }

    /// Files close to the request in meaning: their similarity on the hits found by words, and the closest few the
    /// words missed (within the request's type, time, folder and sender), below the best keyword matches.
    static void AddMeaning(List<Hit> hits, HashSet<string> known, Plan plan)
    {
        var words = string.Join(" ", plan.Terms.SelectMany(t => t.Forms.Take(6)));
        if (SemanticIndex.Query(plan.Topic, words) is not { } query) return;
        for (int i = 0; i < hits.Count; i++)
            if (SemanticIndex.Similarity(query, hits[i].Path) is { } value) hits[i] = hits[i] with { Meaning = value };
        var closest = SemanticIndex.Closest(query, entry => Fits(entry, plan));
        if (closest.Count == 0) return;
        float best = closest[0].Similarity;
        int added = 0;
        foreach (var (entry, value) in closest)
        {
            // Only clearly related files, and only those near the best one: a weak best match brings nothing.
            if (value < SemanticIndex.Related || value < best - 0.05f || added >= 12) break;
            if (known.Contains(entry.Path) || !File.Exists(entry.Path)) continue;
            known.Add(entry.Path);
            hits.Add(new Hit(entry.Path, System.IO.Path.GetFileName(entry.Path), entry.Modified, null, entry.Created, Strict: false, Meaning: value));
            added++;
        }
    }

    /// Whether an indexed file passes the request's filters (the index only holds documents).
    static bool Fits(SemanticIndex.Entry entry, Plan plan, DateTime? now = null)
    {
        var extension = System.IO.Path.GetExtension(entry.Path).ToLowerInvariant();
        if (plan.Extensions.Count > 0 && !plan.Extensions.Any(e => "." + e.TrimStart('.') == extension)) return false;
        if (plan.Extensions.Count == 0 && plan.Kinds.Count > 0
            && !plan.Kinds.Any(k => KindExtensions.TryGetValue(k, out var list) && list.Contains(extension))) return false;
        if (plan.MinBytes is { } min && entry.Size < min) return false;
        if (plan.MaxBytes is { } max && entry.Size > max) return false;
        if (plan.Place is { } place && place != Place.Applications
            && !entry.Path.StartsWith(PlaceFolder(place) + "\\", StringComparison.OrdinalIgnoreCase)) return false;
        if (plan.Received && plan.Place is null && !entry.Path.StartsWith(PlaceFolder(Place.Downloads) + "\\", StringComparison.OrdinalIgnoreCase)
            && !ChatMarkers.Any(m => entry.Path.Contains("\\" + m + "\\", StringComparison.OrdinalIgnoreCase))) return false;
        if (plan.Period is { } period)
        {
            var local = now ?? DateTime.Now;
            var (start, end) = period.Window(local, CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek);
            // The index keeps no last-opened date; the newer of changed and created stands in for it.
            var date = plan.DateField switch
            {
                DateField.Changed => entry.Modified,
                DateField.Used => entry.Modified > entry.Created ? entry.Modified : entry.Created,
                _ => entry.Created,
            };
            var when = date.ToLocalTime();
            if (when < start || when >= end) return false;
        }
        return true;
    }

    /// The same plan without its words.
    static Plan FiltersOnly(Plan plan) => new()
    {
        Kinds = plan.Kinds, Extensions = plan.Extensions, Period = plan.Period, DateField = plan.DateField, Place = plan.Place,
        MinBytes = plan.MinBytes, MaxBytes = plan.MaxBytes, Received = plan.Received,
    };

    /// Opens the index once in the background, so the first search doesn't wait for the provider to load.
    public static void Prewarm() => _ = Task.Run(() => Query("SELECT TOP 1 System.ItemUrl FROM SystemIndex WHERE System.FileName = 'swivel-prewarm'", default));

    /// False after the index could not be opened (the Windows Search service is off); Settings says so.
    public static bool IndexReachable { get; private set; } = true;

    /// One query through ADO (msado15, part of Windows). Returns nothing when Windows Search is off or the query fails.
    static List<Hit> Query(string sql, CancellationToken cancel)
    {
        var hits = new List<Hit>();
        foreach (var row in Rows(sql, 5, 5, cancel) ?? [])
        {
            if (row[0] is not string url || !url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) continue;
            var path = url[5..].Replace('/', '\\');
            hits.Add(new Hit(path, row[1] as string ?? System.IO.Path.GetFileName(path), Utc(row[2]), Utc(row[3]), Utc(row[4])));
        }
        return hits;
    }

    /// The first `columns` values of every row, or null when the index could not be opened (Windows Search is off).
    public static List<object?[]>? Rows(string sql, int columns, int timeout, CancellationToken cancel = default)
    {
        var rows = new List<object?[]>();
        dynamic? connection = null;
        try
        {
            var type = Type.GetTypeFromProgID("ADODB.Connection");
            if (type is null) { IndexReachable = false; return null; }
            connection = Activator.CreateInstance(type)!;
            connection.CommandTimeout = timeout;
            try { connection.Open("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';"); }
            catch (COMException) { IndexReachable = false; return null; }
            IndexReachable = true;
            dynamic records = connection.Execute(sql);
            while (!(bool)records.EOF && !cancel.IsCancellationRequested)
            {
                var row = new object?[columns];
                for (int i = 0; i < columns; i++) row[i] = Field(records, i);
                rows.Add(row);
                records.MoveNext();
            }
            records.Close();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        finally
        {
            try { connection?.Close(); } catch (Exception) { }
        }
        return rows;
    }

    static object? Field(dynamic records, int index)
    {
        object? value = records.Fields.Item(index).Value;
        return value is DBNull ? null : value;
    }

    static DateTime? Utc(object? value) => value is DateTime time ? DateTime.SpecifyKind(time, DateTimeKind.Utc) : null;

    // MARK: Showing results

    static CultureInfo Culture => Languages.Resolved switch
    {
        Language.ZhHans => new CultureInfo("zh-Hans"),
        Language.ZhHant => new CultureInfo("zh-Hant"),
        Language.Ja => new CultureInfo("ja"),
        Language.En => new CultureInfo("en-US"),
        _ => CultureInfo.CurrentCulture,
    };

    /// “OneDrive\Documents · Yesterday” under a result: the folder (from the user folder down) and, when the request
    /// named a time, the date it filtered on, so “last week” never shows “yesterday”.
    public static string Describe(Hit hit, Plan? plan = null, DateTime? now = null)
    {
        var folder = System.IO.Path.GetDirectoryName(hit.Path) ?? hit.Path;
        var home = Home;
        // WeChat's folders are long ids: “WeChat\file\2026-10” says where it came from.
        if (ChatFolder(folder + "\\") is { } chat && chat.IndexOf(@"\msg\", StringComparison.OrdinalIgnoreCase) is var msg and >= 0)
            folder = System.IO.Path.Combine(L("WeChat"), chat[(msg + 5)..].TrimEnd('\\'));
        else if (ChatFolder(folder + "\\") is { } storage && storage.IndexOf(@"\FileStorage\", StringComparison.OrdinalIgnoreCase) is var at and >= 0)
            folder = System.IO.Path.Combine(L("WeChat"), storage[(at + 13)..].TrimEnd('\\'));
        else if (folder.StartsWith(home + "\\", StringComparison.OrdinalIgnoreCase)) folder = folder[(home.Length + 1)..];
        var date = (plan?.Period != null ? hit.DateFor(plan.DateField) : null) ?? hit.Latest;
        return date is { } d ? folder + " · " + When(d, now) : folder;
    }

    /// “3 hours ago” today; after that calendar days (“Yesterday”, a date), since “1 day ago” for last Sunday night
    /// reads like the wrong week.
    public static string When(DateTime utc, DateTime? nowUtc = null, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var now = TimeZoneInfo.ConvertTimeFromUtc(nowUtc ?? DateTime.UtcNow, zone);
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
        if (local.Date == now.Date && local <= now)
        {
            var minutes = (int)(now - local).TotalMinutes;
            return minutes < 1 ? L("Just now") : minutes < 60 ? L("%d minutes ago", minutes) : L("%d hours ago", minutes / 60);
        }
        if (local.Date == now.Date.AddDays(-1)) return L("Yesterday");
        return local.ToString("d", Culture);
    }
}
