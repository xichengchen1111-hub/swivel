using System.Text.RegularExpressions;
using Swivel;

namespace Swivel.Tests;

[TestClass]
public sealed class CoreTests
{
    [TestInitialize]
    public void Reset() => TestSetup.ResetPrefs();

    [TestMethod]
    public void FormatFillsMacPlaceholders()
    {
        Assert.AreEqual("Saved a.png", Loc.Format("Saved %@", "a.png"));
        Assert.AreEqual("3 files, 50%", Loc.Format("%d files, %d%%", 3, 50));
        Assert.AreEqual("b then a", Loc.Format("%2$@ then %1$@", "a", "b"));
        Assert.AreEqual("1.5 s", Loc.Format("%.1f s", 1.5));
    }

    [TestMethod]
    public void TranslatesThroughTheSharedTable()
    {
        Prefs.LanguageId = "zh-Hans";
        Assert.AreEqual("跟随系统", Loc.L("System Default"));
        Prefs.LanguageId = "ja";
        Assert.AreEqual("システムに従う", Loc.L("System Default"));
        Prefs.LanguageId = "en";
        Assert.AreEqual("System Default", Loc.L("System Default"));
        Assert.AreEqual("Not in the table", Loc.L("Not in the table"));
    }

    [TestMethod]
    public void EveryTranslationKeepsThePlaceholders()
    {
        static string Shape(string s) => string.Join(",", Regex.Matches(s, @"%(\d\$)?[@dfs%]").Select(m => m.Value).Order());
        foreach (var (english, translations) in Strings.AllRows)
        {
            Assert.HasCount(3, translations, english);
            foreach (var t in translations)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(t), $"Empty translation for “{english}”");
                // Positional placeholders may reorder; the set of kinds must match.
                Assert.AreEqual(Shape(english).Replace("1$", "").Replace("2$", ""), Shape(t).Replace("1$", "").Replace("2$", ""), $"“{english}” → “{t}”");
            }
        }
    }

    /// Every literal passed to L(…) in the app's source has a row in the table (like the Mac app's strings check).
    [TestMethod]
    public void EveryUserFacingStringIsTranslated()
    {
        var source = TestSetup.SourceRoot;
        Assert.IsTrue(Directory.Exists(source), source);
        var literal = @"""((?:[^""\\]|\\.)*)""";
        var direct = new Regex(@"\bL\(\s*" + literal);
        var ternary = new Regex(@"\bL\(\s*[^()""\n]+?\?\s*" + literal + @"\s*:\s*" + literal);
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) ||
                file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) ||
                Path.GetFileName(file).StartsWith("Strings.")) continue;
            var text = File.ReadAllText(file);
            var found = direct.Matches(text).Select(m => m.Groups[1].Value)
                .Concat(ternary.Matches(text).SelectMany(m => new[] { m.Groups[1].Value, m.Groups[2].Value }));
            foreach (var raw in found)
            {
                var english = Regex.Unescape(raw);
                if (english.Any(char.IsLetter) && !Strings.Table.ContainsKey(english)) missing.Add($"{english}    ({Path.GetFileName(file)})");
            }
        }
        Assert.IsEmpty(missing, "Add translations for:\n" + string.Join("\n", missing));
    }

    [TestMethod]
    public void SettingKeyTakesOverFromTheOtherMenu()
    {
        Assert.AreEqual(TriggerKey.Shift, Prefs.Key(WheelMode.Convert));
        Prefs.SetKey(TriggerKey.Control, WheelMode.Convert);
        Assert.AreEqual(TriggerKey.Control, Prefs.Key(WheelMode.Convert));
        Assert.AreEqual(TriggerKey.Shift, Prefs.Key(WheelMode.Tools));
        Prefs.ResetKeys();
        Assert.AreEqual(TriggerKey.Control, Prefs.Key(WheelMode.Tools));
    }

    [TestMethod]
    public void ArrangeAppliesOrderAndHidden()
    {
        var items = new[] { "A", "B", "C", "D" }.Select(t => new WheelItem(t, _ => Task.FromResult(new ActionResult("")))).ToList();
        Prefs.SetArrangement(["C", "A"], ["B"], WheelMode.Convert, FileKind.Image);
        CollectionAssert.AreEqual(new[] { "C", "A", "D" }, Prefs.Arrange(items, WheelMode.Convert, FileKind.Image).Select(i => i.Title).ToList());
        Prefs.ResetContents(WheelMode.Convert, FileKind.Image);
        Assert.HasCount(4, Prefs.Arrange(items, WheelMode.Convert, FileKind.Image));
    }

    [TestMethod]
    public void SettingsSurviveAReload()
    {
        Prefs.Style = MenuStyle.Notes;
        Prefs.ImagePresets = [.. ImagePreset.Starters.Take(1)];
        Prefs.Reload();
        Assert.AreEqual(MenuStyle.Notes, Prefs.Style);
        Assert.HasCount(1, Prefs.ImagePresets);
        Assert.AreEqual("web", Prefs.ImagePresets[0].Id);
    }

    [TestMethod]
    public void OutputNamesNeverOverwrite()
    {
        var dir = TestSetup.NewFolder("paths");
        var source = Path.Combine(dir, "photo.heic");
        File.WriteAllText(source, "");
        Assert.AreEqual(Path.Combine(dir, "photo.png"), Paths.Output(source, "png"));
        File.WriteAllText(Path.Combine(dir, "photo.png"), "");
        Assert.AreEqual(Path.Combine(dir, "photo 2.png"), Paths.Output(source, "png"));
        Assert.AreEqual(Path.Combine(dir, "photo small.heic"), Paths.Output(source, "heic", " small"));
        File.WriteAllText(Path.Combine(dir, "Archive.tar.gz"), "");
        Assert.AreEqual(Path.Combine(dir, "Archive 2.tar.gz"), Paths.Unique(Path.Combine(dir, "Archive.tar.gz")));
    }

    [TestMethod]
    public void FileKindsFollowExtensions()
    {
        Assert.AreEqual(FileKind.Image, FileKinds.Of(@"C:\x\a.HEIC"));
        Assert.AreEqual(FileKind.Video, FileKinds.Of(@"C:\x\a.mp4"));
        Assert.AreEqual(FileKind.Audio, FileKinds.Of(@"C:\x\a.flac"));
        Assert.AreEqual(FileKind.Pdf, FileKinds.Of(@"C:\x\a.pdf"));
        Assert.AreEqual(FileKind.Document, FileKinds.Of(@"C:\x\a.md"));
        Assert.AreEqual(FileKind.Archive, FileKinds.Of(@"C:\x\a.zip"));
        Assert.AreEqual(FileKind.Folder, FileKinds.Of(TestSetup.Scratch));
        Assert.AreEqual(FileKind.Other, FileKinds.Of([@"C:\x\a.jpg", @"C:\x\a.mp4"]));
    }

    [TestMethod]
    public void ActionsMenuFollowsFeatureSwitches()
    {
        var files = FileKind.Image.SampleFiles();
        Prefs.SetFeatureEnabled(Feature.Shelf, false);
        var titles = Catalog.Items(files, WheelMode.Actions).Select(i => i.Title).ToList();
        CollectionAssert.DoesNotContain(titles, "Float");
        CollectionAssert.Contains(titles, "Share");
        Prefs.SetFeatureEnabled(Feature.Shelf, true);
        CollectionAssert.Contains(Catalog.Items(files, WheelMode.Actions).Select(i => i.Title).ToList(), "Float");
    }
}
