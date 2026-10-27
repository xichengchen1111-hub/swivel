using Swivel;

namespace Swivel.Tests;

/// Quick Actions' file search (port of FileSearchTests.swift): reading requests, building Windows Search queries,
/// ranking and what is left out. None of these touch the index; `Swivel --find "<request>"` runs a real search.
[TestClass]
public sealed class FileSearchTests
{
    [TestInitialize]
    public void Reset()
    {
        TestSetup.ResetPrefs();
        Prefs.LanguageId = "en";
    }

    /// Tuesday 27 October 2026, 15:00.
    static readonly DateTime Now = new(2026, 10, 27, 15, 0, 0);
    static List<string> Words(FileSearch.Plan plan) => plan.Terms.SelectMany(t => t.Forms).ToList();
    static FileSearch.Period Period(FileSearch.PeriodKind kind, int count = 0, FileSearch.Unit unit = FileSearch.Unit.Day) => new(kind, count, unit);

    [TestMethod]
    public void ReadsAChineseRequestWithTypeTimeAndSource()
    {
        var plan = FileSearch.Parse("上周从微信下载的那个报价单 PDF");
        CollectionAssert.AreEqual(new[] { FileSearch.Kind.Pdf }, plan.Kinds);
        Assert.AreEqual(Period(FileSearch.PeriodKind.LastWeek), plan.Period);
        Assert.AreEqual(FileSearch.DateField.Added, plan.DateField);
        CollectionAssert.Contains(Words(plan), "微信");
        Assert.IsTrue(Words(plan).Any(w => w.StartsWith("报价")), string.Join(",", Words(plan)));
        Assert.IsFalse(Words(plan).Any(w => w is "的" or "那个" or "下载" or "pdf" or "上周"), string.Join(",", Words(plan)));
    }

    [TestMethod]
    public void ReadsEnglishRequests()
    {
        var screenshots = FileSearch.Parse("screenshots from yesterday");
        CollectionAssert.AreEqual(new[] { FileSearch.Kind.Screenshot }, screenshots.Kinds);
        Assert.AreEqual(Period(FileSearch.PeriodKind.Yesterday), screenshots.Period);
        Assert.IsEmpty(screenshots.Terms);

        var videos = FileSearch.Parse("big videos in downloads");
        CollectionAssert.AreEqual(new[] { FileSearch.Kind.Video }, videos.Kinds);
        Assert.AreEqual(FileSearch.Place.Downloads, videos.Place);
        Assert.AreEqual(100_000_000L, videos.MinBytes);
        Assert.IsEmpty(videos.Terms);

        var invoices = FileSearch.Parse("invoices over 5 MB from 2024");
        CollectionAssert.IsSubsetOf(new[] { "invoice", "发票", "receipt" }, invoices.Terms.Single().Forms.ToList());
        Assert.AreEqual(5_000_000L, invoices.MinBytes);
        Assert.AreEqual(Period(FileSearch.PeriodKind.Year, 2024), invoices.Period);

        var recent = FileSearch.Parse("photos I opened in the past few days");
        CollectionAssert.AreEqual(new[] { FileSearch.Kind.Image }, recent.Kinds);
        Assert.AreEqual(Period(FileSearch.PeriodKind.Past, 3, FileSearch.Unit.Day), recent.Period);
        Assert.AreEqual(FileSearch.DateField.Used, recent.DateField);
        Assert.IsEmpty(recent.Terms);
    }

    [TestMethod]
    public void ReadsNumbersUnitsSizesAndQuotes()
    {
        var sheets = FileSearch.Parse("过去3天打开过的 excel");
        CollectionAssert.AreEqual(new[] { FileSearch.Kind.Spreadsheet }, sheets.Kinds);
        Assert.AreEqual(Period(FileSearch.PeriodKind.Past, 3, FileSearch.Unit.Day), sheets.Period);
        Assert.AreEqual(FileSearch.DateField.Used, sheets.DateField);
        Assert.IsEmpty(sheets.Terms);
        Assert.AreEqual(Period(FileSearch.PeriodKind.Ago, 2, FileSearch.Unit.Week), FileSearch.Parse("两周前的照片").Period);
        Assert.AreEqual(500_000L, FileSearch.Parse("小于 500kb 的图片").MaxBytes);
        Assert.AreEqual(2_000_000_000L, FileSearch.Parse("大于2G的视频").MinBytes);
        Assert.AreEqual(10_000_000L, FileSearch.Parse("大图片").MinBytes);
        Assert.AreEqual(5_000_000L, FileSearch.Parse("ｐｄｆ　ｏｖｅｒ　５ＭＢ").MinBytes, "full-width text reads like ASCII");

        var cat = FileSearch.Parse("猫的照片");
        CollectionAssert.AreEqual(new[] { "猫" }, Words(cat));
        CollectionAssert.AreEqual(new[] { FileSearch.Kind.Image }, cat.Kinds);
        CollectionAssert.AreEqual(new[] { "微信" }, Words(FileSearch.Parse("微信里的")), "the segmenter's single characters are joined back");

        var quoted = FileSearch.Parse("find “Q3 plan” jpg");
        Assert.AreEqual(new FileSearch.Term(["Q3 plan"], Exact: true), quoted.Terms.Single());
        CollectionAssert.AreEqual(new[] { "jpg", "jpeg" }, quoted.Extensions);
        Assert.IsEmpty(quoted.Kinds);
        Assert.IsTrue(FileSearch.Parse("the").IsEmpty);
        Assert.IsTrue(FileSearch.Parse("帮我找一下那个文件").IsEmpty);

        foreach (var (text, value) in new[] { ("十二", 12), ("二十", 20), ("三十五", 35), ("两", 2), ("a few", 3), ("7", 7) })
            Assert.AreEqual(value, FileSearch.ParseNumber(text), text);
    }

    [TestMethod]
    public void ReadsAbstractRequestsAsConceptsSendersAndTranslations()
    {
        var work = FileSearch.Parse("上周保存的领导给的工作内容");
        Assert.AreEqual(Period(FileSearch.PeriodKind.LastWeek), work.Period);
        Assert.IsTrue(work.Received);
        Assert.AreEqual(FileSearch.DateField.Added, work.DateField);
        var concept = work.Terms.Single();
        CollectionAssert.IsSubsetOf(new[] { "工作", "周报", "会议", "report", "meeting" }, concept.Forms.ToList());
        Assert.IsFalse(Words(work).Any(w => w is "领导" or "内容"), string.Join(",", Words(work)));

        var jobs = FileSearch.Parse("找工作相关的东西");
        CollectionAssert.IsSubsetOf(new[] { "简历", "resume", "cv", "cover letter", "推荐信" }, jobs.Terms.Single().Forms.ToList());

        var os = FileSearch.Parse("操作系统复习资料");
        Assert.HasCount(2, os.Terms);
        CollectionAssert.Contains(os.Terms[0].Forms.ToList(), "operating system");
        CollectionAssert.Contains(os.Terms[1].Forms.ToList(), "cheat sheet");

        var uni = FileSearch.Parse("最近的大学文件");
        Assert.AreEqual(Period(FileSearch.PeriodKind.Recent), uni.Period);
        CollectionAssert.IsSubsetOf(new[] { "lecture", "assignment", "uni", "作业" }, uni.Terms.Single().Forms.ToList());
        CollectionAssert.Contains(FileSearch.Parse("lecture slides from last week").Terms.Single().Forms.ToList(), "课件");
        Assert.IsTrue(FileSearch.Parse("files my boss sent").Received);
        CollectionAssert.Contains(FileSearch.Parse("移民要用的材料").Terms.Single().Forms.ToList(), "visa");
    }

    [TestMethod]
    public void MeaningComesFromTheTopicAloneAndTimeIsAFilter()
    {
        var key = FileSearch.Parse("前两周存的一个密钥");
        Assert.AreEqual(new FileSearch.Period(FileSearch.PeriodKind.Past, 2, FileSearch.Unit.Week), key.Period);
        Assert.AreEqual(FileSearch.DateField.Added, key.DateField);
        CollectionAssert.IsSubsetOf(new[] { "key", "api key", "token", "password" }, key.Terms.Single().Forms.ToList());
        Assert.AreEqual("密钥", key.Topic);
        // Keys are looked for in names and folders, not in every document that says “key”.
        Assert.IsFalse(FileSearch.QueryString(key)!.Contains("CONTAINS(*"), FileSearch.QueryString(key));

        Assert.AreEqual("工作内容", FileSearch.Parse("上周保存的领导给的工作内容").Topic);
        Assert.AreEqual("", FileSearch.Parse("昨天的截图").Topic);
        Assert.AreEqual(new FileSearch.Period(FileSearch.PeriodKind.Past, 3, FileSearch.Unit.Month), FileSearch.Parse("前三个月的发票").Period);
        Assert.AreEqual(Period(FileSearch.PeriodKind.Recent), FileSearch.Parse("前几天的照片").Period);
        Assert.AreEqual(Period(FileSearch.PeriodKind.DayBeforeYesterday), FileSearch.Parse("前天的文件").Period);
    }

    [TestMethod]
    public void AbstractTermsMatchFoldersSendersAndShortWordsExactly()
    {
        var plan = FileSearch.Parse("微信收到的操作系统 pdf");
        var query = FileSearch.QueryString(plan)!;
        StringAssert.Contains(query, "CONTAINS(System.ItemFolderPathDisplay, ");
        StringAssert.Contains(query, "\"operating system*\" OR \"os\"");
        Assert.IsFalse(query.Contains("\"os*\""), query);
        StringAssert.Contains(query, "SCOPE='file:" + FileSearch.PlaceFolder(FileSearch.Place.Downloads).Replace('\\', '/') + "' OR CONTAINS(System.ItemPathDisplay, '\"xwechat_files\"");
        // A folder the user named for the topic counts, below the name itself.
        var work = FileSearch.Parse("工作周报");
        var now = DateTime.UtcNow;
        var ranked = FileSearch.Rank([
            new(@"C:\Users\me\Documents\notes.docx", "notes.docx", now),
            new(FileSearch.Home + @"\OneDrive\工作\week3.xlsx", "week3.xlsx", now),
        ], FileSearch.Parse("工作"), now);
        Assert.AreEqual("week3.xlsx", ranked[0].Name);
        Assert.IsNotEmpty(work.Terms);
        Assert.IsFalse(FileSearch.IsVisible(@"C:\Users\me\anaconda3\pkgs\a\test_data\www.unimelb.edu.au", @"C:\Users\me"));
        var installer = FileSearch.Rank([
            new(@"C:\Users\me\Downloads\WorkBuddy-setup.exe", "WorkBuddy-setup.exe", now),
            new(@"C:\Users\me\Downloads\brief.pptx", "brief.pptx", now.AddDays(-3)),
        ], FileSearch.Parse("上周收到的工作内容"), now);
        Assert.AreEqual("brief.pptx", installer[0].Name);
    }

    [TestMethod]
    public void MeaningOutranksWordsThatOnlyHappenToBeInTheText()
    {
        // “帮家人办签证要交的东西”: a résumé mentions 签证 somewhere (strict, content only); the visa checklist is about it.
        var plan = FileSearch.Parse("帮家人办签证要交的东西");
        var now = DateTime.UtcNow;
        var ranked = FileSearch.Rank([
            new(@"C:\Users\me\Desktop\Resume.pdf", "Resume.pdf", now, Strict: true, Meaning: 0.86f),
            new(@"C:\Users\me\Documents\801签证材料清单.docx", "801签证材料清单.docx", now.AddDays(-70), Strict: false, Meaning: 0.91f),
            new(@"C:\Users\me\Documents\script.pdf", "script.pdf", now, Strict: true, Meaning: 0.79f),
        ], plan, now);
        CollectionAssert.AreEqual(new[] { "801签证材料清单.docx", "Resume.pdf", "script.pdf" }, ranked.Select(h => h.Name).ToList());
        // Without a meaning index the words decide, as before.
        var plain = FileSearch.Rank([
            new(@"C:\Users\me\Desktop\Resume.pdf", "Resume.pdf", now, Strict: true),
            new(@"C:\Users\me\Documents\notes.docx", "notes.docx", now, Strict: false),
        ], plan, now);
        Assert.AreEqual("Resume.pdf", plain[0].Name);
    }

    /// Times are read from their parts, so any way of saying one works, not a list of phrases. Now is Tuesday
    /// 27 October 2026, 15:00.
    [TestMethod]
    public void TimesAreReadFromTheirPartsHoweverTheyAreSaid()
    {
        DateTime d(int month, int day, int hour = 0, int year = 2026) => new(year, month, day, hour, 0, 0);
        var cases = new (string Phrase, DateTime Start, DateTime? End)[]
        {
            ("前两周", Now.AddDays(-14), null),
            ("过去两周", Now.AddDays(-14), null),
            ("最近两个礼拜", Now.AddDays(-14), null),
            ("这两周", Now.AddDays(-14), null),
            ("近14天", Now.AddDays(-14), null),
            ("两周内", Now.AddDays(-14), null),
            ("过去两三周", Now.AddDays(-21), null),
            ("1-2周内", Now.AddDays(-14), null),
            ("last 3 days", Now.AddDays(-3), null),
            ("in the past couple of weeks", Now.AddDays(-14), null),
            ("past week", Now.AddDays(-7), null),
            ("最近一个月", Now.AddMonths(-1), null),
            ("近三个月", Now.AddMonths(-3), null),
            ("一个半月内", Now.AddDays(-45), null),
            ("半年内", Now.AddMonths(-6), null),
            ("刚才", Now.AddHours(-6), null),
            ("前阵子", Now.AddMonths(-2), null),
            ("两周前", Now.AddDays(-21), Now.AddDays(-7)),
            ("两个多星期前", Now.AddDays(-21), Now.AddDays(-7)),
            ("a couple of weeks ago", Now.AddDays(-21), Now.AddDays(-7)),
            ("2週間前", Now.AddDays(-21), Now.AddDays(-7)),
            ("两三周前", Now.AddDays(-28), Now.AddDays(-14)),
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
            ("下半年", d(7, 1), null),
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
            ("两个月左右前", Now.AddMonths(-3), Now.AddMonths(-1)),
            ("十来天前", d(10, 15), d(10, 20)),
            ("10.14", d(10, 14), d(10, 15)),
            ("自从3月以来", d(3, 1), null),
            ("从上个月开始", d(9, 1), null),
            ("since last month", d(9, 1), null),
            ("over the last fortnight", Now.AddDays(-14), null),
            ("a year and a half ago", Now.AddMonths(-19), Now.AddMonths(-17)),
            ("前不久", d(10, 13), null),
        };
        var failures = new List<string>();
        foreach (var (phrase, start, end) in cases)
        {
            var plan = FileSearch.Parse(phrase + "的合同");
            if (plan.Period is not { } period) { failures.Add($"{phrase}: no time"); continue; }
            var range = period.Range(Now, DayOfWeek.Monday);
            if (range.Start != start || (end is { } e ? range.End != e : range.End <= Now))
                failures.Add($"{phrase}: {period} → {range.Start:MM-dd HH:mm} – {range.End:MM-dd HH:mm}");
            // Nothing of the time is left to look for: only the contract.
            if (plan.Terms.Count != 1 || !plan.Terms[0].Forms.Contains("contract")) failures.Add($"{phrase}: words {string.Join(" | ", plan.Terms.Select(t => t.Forms[0]))}");
        }
        Assert.IsEmpty(failures, string.Join("\n", failures));

        // Words that only look like time stay words.
        foreach (var phrase in new[] { "周报", "月报", "日本的照片", "年终总结", "第3周的作业", "weekly report", "may be useful" })
            Assert.IsNull(FileSearch.Parse(phrase).Period, phrase);
        // Vague words widen the search; a named day doesn't.
        var vague = FileSearch.Parse("两周前左右").Period!;
        Assert.IsTrue(vague.Window(Now, DayOfWeek.Monday).Start < vague.Range(Now, DayOfWeek.Monday).Start);
        var named = FileSearch.Parse("昨天").Period!;
        Assert.AreEqual(named.Range(Now, DayOfWeek.Monday), named.Window(Now, DayOfWeek.Monday));
    }

    [TestMethod]
    public void PeriodsResolveToCalendarRanges()
    {
        var monday = DayOfWeek.Monday;
        Assert.AreEqual((new DateTime(2026, 10, 19), new DateTime(2026, 10, 26)), Period(FileSearch.PeriodKind.LastWeek).Range(Now, monday));
        Assert.AreEqual((new DateTime(2026, 10, 18), new DateTime(2026, 10, 25)), Period(FileSearch.PeriodKind.LastWeek).Range(Now, DayOfWeek.Sunday));
        Assert.AreEqual((new DateTime(2026, 10, 26), new DateTime(2026, 10, 27)), Period(FileSearch.PeriodKind.Yesterday).Range(Now, monday));
        Assert.AreEqual((new DateTime(2026, 10, 24), new DateTime(2026, 10, 25)), Period(FileSearch.PeriodKind.Ago, 3).Range(Now, monday));
        Assert.AreEqual((new DateTime(2026, 9, 1), new DateTime(2026, 10, 1)), Period(FileSearch.PeriodKind.LastMonth).Range(Now, monday));
        Assert.AreEqual((new DateTime(2024, 1, 1), new DateTime(2025, 1, 1)), Period(FileSearch.PeriodKind.Year, 2024).Range(Now, monday));
        var past = Period(FileSearch.PeriodKind.Past, 2, FileSearch.Unit.Week).Range(Now, monday);
        Assert.AreEqual(Now.AddDays(-14), past.Start);
        Assert.IsTrue(past.End > Now);
    }

    [TestMethod]
    public void BuildsEscapedWindowsSearchQueries()
    {
        Assert.IsNull(FileSearch.QueryString(new FileSearch.Plan()));
        var plan = new FileSearch.Plan
        {
            Terms = [new(["o'brien\"*%"]), new(["invoices", "发票"]), new(["微信"])],
            Kinds = [FileSearch.Kind.Screenshot],
            Period = Period(FileSearch.PeriodKind.Yesterday),
            DateField = FileSearch.DateField.Used,
        };
        var utc = TimeZoneInfo.Utc;
        var strict = FileSearch.QueryString(plan, now: Now, firstDay: DayOfWeek.Monday, zone: utc)!;
        // Quotes, apostrophes and wildcards are dropped, so a request can't break out of the CONTAINS literal.
        StringAssert.Contains(strict, "CONTAINS(System.FileName, '\"obrien*\"')");
        StringAssert.Contains(strict, "\"invoices*\" OR \"invoice*\" OR \"发票*\"");
        StringAssert.Contains(strict, "CONTAINS(System.ItemPathDisplay, '\"xwechat_files\" OR \"WeChat Files\"')");
        StringAssert.Contains(strict, "System.Kind = 'picture' AND (CONTAINS(System.ItemFolderNameDisplay");
        StringAssert.Contains(strict, "System.DateAccessed >= '2026-10-26 00:00:00'");
        StringAssert.Contains(strict, "System.DateAccessed < '2026-10-27 00:00:00'");
        StringAssert.Contains(strict, ") AND (");
        StringAssert.Contains(strict, $"SCOPE='file:{FileSearch.Home.Replace('\\', '/')}'");
        StringAssert.Contains(FileSearch.QueryString(plan, strict: false, now: Now, zone: utc)!, ") OR (");

        // The local day is converted to the UTC the index stores (Melbourne is UTC+11 in October).
        var melbourne = TimeZoneInfo.FindSystemTimeZoneById("AUS Eastern Standard Time");
        StringAssert.Contains(FileSearch.QueryString(plan, now: Now, zone: melbourne)!, "System.DateAccessed >= '2026-10-25 13:00:00'");

        var types = new FileSearch.Plan { Kinds = [FileSearch.Kind.Pdf, FileSearch.Kind.Presentation], MinBytes = 1000, Place = FileSearch.Place.Desktop };
        var query = FileSearch.QueryString(types)!;
        StringAssert.Contains(query, "System.FileExtension = '.pdf'");
        StringAssert.Contains(query, "System.FileExtension = '.pptx'");
        StringAssert.Contains(query, "System.Size >= 1000");
        StringAssert.Contains(query, "SCOPE='file:" + Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory).Replace('\\', '/') + "'");
        Assert.AreEqual("invoice", FileSearch.Stem("invoices"));
        Assert.AreEqual("study", FileSearch.Stem("studies"));
        Assert.AreEqual("class", FileSearch.Stem("class"));
    }

    [TestMethod]
    public void LeavesOutHiddenSystemAndChatAppFiles()
    {
        var home = @"C:\Users\me";
        Assert.IsTrue(FileSearch.IsVisible(@"C:\Users\me\OneDrive\Desktop\plan.pdf", home));
        Assert.IsTrue(FileSearch.IsVisible(@"C:\Users\me\OneDrive\Documents\xwechat_files\wxid_1\msg\file\2026-10\报价.pdf", home));
        Assert.IsTrue(FileSearch.IsVisible(@"C:\Users\me\Documents\WeChat Files\wxid_1\FileStorage\File\2023-01\a.docx", home));
        Assert.IsFalse(FileSearch.IsVisible(@"C:\Users\me\OneDrive\Documents\xwechat_files\wxid_1\business\emoticon\Thumb\04", home));
        Assert.IsFalse(FileSearch.IsVisible(@"C:\Users\me\OneDrive\Documents\xwechat_files\wxid_1\msg\attach\x\Img\a.dat", home));
        Assert.IsFalse(FileSearch.IsVisible(@"C:\Users\me\AppData\Local\Temp\x.png", home));
        Assert.IsFalse(FileSearch.IsVisible(@"C:\Users\me\code\.git\config", home));
        Assert.IsFalse(FileSearch.IsVisible(@"C:\Users\me\web\node_modules\a\readme.md", home));
        Assert.IsFalse(FileSearch.IsVisible(@"C:\Users\me\Downloads\movie.mp4.crdownload", home));
    }

    [TestMethod]
    public void RanksNamesAndRecentFilesFirst()
    {
        var plan = new FileSearch.Plan { Terms = [new(["invoice"])] };
        var now = DateTime.UtcNow;
        FileSearch.Hit hit(string path, double days, bool strict = true) =>
            new(path, System.IO.Path.GetFileName(path), now.AddDays(-days), Strict: strict);
        var named = hit(@"C:\Users\me\Documents\Invoice March.pdf", 200);
        var content = hit(@"C:\Users\me\Documents\scan.pdf", 1);
        var code = hit(@"C:\Users\me\src\api\Invoice.cs", 1);
        var codeByText = hit(@"C:\Users\me\src\api\payments.cs", 0);
        var loose = hit(@"C:\Users\me\invoice-notes.txt", 0, strict: false);
        var ranked = FileSearch.Rank([codeByText, loose, content, code, named], plan, now).Select(h => h.Name).ToList();
        CollectionAssert.IsSubsetOf(new[] { "Invoice March.pdf", "Invoice.cs" }, ranked.Take(2).ToList());
        Assert.IsTrue(ranked.IndexOf("scan.pdf") < ranked.IndexOf("payments.cs"));
        Assert.IsTrue(ranked.IndexOf("scan.pdf") < ranked.IndexOf("invoice-notes.txt"));

        var cached = FileSearch.Rank([hit(@"C:\Users\me\app\cache\invoice.pdf", 0), hit(@"C:\Users\me\Documents\invoice.pdf", 3)], plan, now);
        Assert.AreEqual(@"C:\Users\me\Documents\invoice.pdf", cached[0].Path);

        var filtersOnly = new FileSearch.Plan { Kinds = [FileSearch.Kind.Pdf] };
        var byDate = FileSearch.Rank([hit(@"C:\a\old.pdf", 30), hit(@"C:\a\new.pdf", 1)], filtersOnly, now);
        CollectionAssert.AreEqual(new[] { "new.pdf", "old.pdf" }, byDate.Select(h => h.Name).ToList());
    }

    [TestMethod]
    public void ResultsShowTheFilteredDateAndShortChatFolders()
    {
        var now = new DateTime(2026, 10, 27, 4, 0, 0, DateTimeKind.Utc);
        var utc = TimeZoneInfo.Utc;
        Assert.AreEqual("3 hours ago", FileSearch.When(now.AddHours(-3), now, utc));
        Assert.AreEqual("Yesterday", FileSearch.When(now.AddDays(-1), now, utc));
        Assert.AreEqual("Just now", FileSearch.When(now, now, utc));

        var lastWeek = new FileSearch.Plan { Period = Period(FileSearch.PeriodKind.LastWeek) };
        var opened = new FileSearch.Hit(@"C:\a\report.pdf", "report.pdf", Changed: DateTime.UtcNow.AddDays(-6), Used: DateTime.UtcNow.AddMinutes(-30));
        Assert.AreNotEqual(FileSearch.Describe(opened), FileSearch.Describe(opened, lastWeek));

        var chat = new FileSearch.Hit(FileSearch.Home + @"\OneDrive\Documents\xwechat_files\wxid_abc\msg\file\2026-10\报价.pdf", "报价.pdf");
        Assert.AreEqual(@"WeChat\file\2026-10", FileSearch.Describe(chat));
        var mine = new FileSearch.Hit(FileSearch.Home + @"\OneDrive\Desktop\a.pdf", "a.pdf");
        Assert.AreEqual(@"OneDrive\Desktop", FileSearch.Describe(mine));
    }

    [TestMethod]
    public async Task FileRowsOpenOnlyFilesThatStillExist()
    {
        var gone = System.IO.Path.Combine(TestSetup.Scratch, "gone.pdf");
        var command = QuickActions.FileCommand(new FileSearch.Hit(gone, "gone.pdf"));
        Assert.AreEqual("file:" + gone, command.Id);
        Assert.AreEqual(gone, command.File);
        await Assert.ThrowsExactlyAsync<Info>(() => command.Run());
        Assert.HasCount(1, QuickActions.Matching([command], "gone"));
    }
}
