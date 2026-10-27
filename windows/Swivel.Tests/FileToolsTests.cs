using System.Runtime.ExceptionServices;
using System.Text;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Swivel;

namespace Swivel.Tests;

/// Batch Rename and PDF Pages (a port of the file-tool cases in FileActionsTests.swift), plus the clothesline card's
/// accessible hang button.
[TestClass]
public sealed class FileToolsTests
{
    [TestInitialize]
    public void Reset()
    {
        TestSetup.ResetPrefs();
        Prefs.LanguageId = "en";
        Prefs.SoundEffects = false;
    }

    // MARK: Helpers

    static string File(string dir, string name, string value = "original")
    {
        var path = Path.Combine(dir, name);
        System.IO.File.WriteAllText(path, value);
        return path;
    }

    static string Content(string path) => System.IO.File.ReadAllText(path);

    static List<string> Names(string dir) => Directory.EnumerateFileSystemEntries(dir).Select(p => Path.GetFileName(p)!).Order(StringComparer.Ordinal).ToList();

    /// A PDF whose pages say `labels` (one shared font), page i being 300 + 10·i points wide.
    static string Pdf(string path, params string[] labels)
    {
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", "", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>" };
        var kids = new List<string>();
        foreach (var (label, i) in labels.Select((l, i) => (l, i)))
        {
            var content = $"BT /F1 20 Tf 30 100 Td ({label}) Tj ET";
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
            int contentNum = objects.Count;
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {300 + i * 10} 400] /Resources << /Font << /F1 3 0 R >> >> /Contents {contentNum} 0 R >>");
            kids.Add($"{objects.Count} 0 R");
        }
        objects[1] = $"<< /Type /Pages /Kids [{string.Join(" ", kids)}] /Count {labels.Length} >>";
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(sb.ToString()));
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        int xref = Encoding.Latin1.GetByteCount(sb.ToString());
        sb.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        System.IO.File.WriteAllBytes(path, Encoding.Latin1.GetBytes(sb.ToString()));
        return path;
    }

    static string PageText(PdfFile file, int index) => Encoding.Latin1.GetString(file.PageContent(file.Pages[index]));

    /// Runs `body` on an STA thread with a WPF dispatcher, so awaits in the windows continue on it.
    static void Sta(Action body)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try { body(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    static void Wait(Task task, int seconds = 20)
    {
        for (int i = 0; i < seconds * 50 && !task.IsCompleted; i++) Pump(20);
        Assert.IsTrue(task.IsCompleted, "The work finished in time.");
        task.GetAwaiter().GetResult();
    }

    static string? RenderFolder => Environment.GetEnvironmentVariable("SWIVEL_TEST_RENDER") is { Length: > 0 } dir ? Directory.CreateDirectory(dir).FullName : null;

    static void ShowOffscreen(Window window)
    {
        window.Left = -32000;
        window.Top = -32000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Show();
    }

    static void SaveRender(FileToolWindow tool, string name, bool dark)
    {
        if (RenderFolder is not { } folder) return;
        var content = tool.Content;
        tool.Window.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(content);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(content.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var dc = background.RenderOpen())
            dc.DrawRectangle(new SolidColorBrush(dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3)), null,
                new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        bitmap.Render(background);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = System.IO.File.Create(Path.Combine(folder, name + ".png"));
        encoder.Save(file);
    }

    // MARK: Rename rules

    [TestMethod]
    public void RenameCombinesRulesAndKeepsExtensionsAndFolderNames()
    {
        var dir = TestSetup.NewFolder("rename-rules");
        var photo = File(dir, "照片-raw.PNG");
        var rules = new RenameRules { Find = "raw", Replacement = "final", Prefix = "项目-", Suffix = "-好", Number = true, Start = 7, Digits = 3 };
        Assert.AreEqual("项目-照片-final-好-007.PNG", rules.Name(photo, 0));
        var sub = Directory.CreateDirectory(Path.Combine(dir, "project.assets")).FullName;
        Assert.AreEqual("new-project.assets", new RenameRules { Prefix = "new-" }.Name(sub, 0));
        Assert.AreEqual("item-10.PNG", new RenameRules { Base = "item", Number = true, Start = 9, Digits = 2 }.Name(photo, 1));
        Assert.AreEqual("", new RenameRules { Number = true, Start = long.MaxValue }.Name(photo, 1), "Overflowing numbers give no name.");
    }

    [TestMethod]
    public void RenamePreviewFindsDuplicateNamesExternalConflictsAndInvalidNames()
    {
        var dir = TestSetup.NewFolder("rename-preview");
        string a = File(dir, "a.txt"), b = File(dir, "b.txt");
        Assert.IsFalse(RenamePlan.Preview([a, b], new RenameRules { Base = "same" }).CanApply);
        File(dir, "new-a.txt", "keep");
        var blocked = RenamePlan.Preview([a], new RenameRules { Prefix = "new-" });
        Assert.IsFalse(blocked.CanApply);
        Assert.AreEqual(Loc.L("A file with this name already exists"), blocked.Entries[0].Issue);
        // The Mac's rules, and what Windows refuses on top of them.
        foreach (var bad in new[] { "", ".", "..", ".hidden", "a/b", "a\\b", "a:b", "a\0b", "a\nb", "a*b", "a?b", "a\"b", "a<b", "a>b", "a|b",
                                    "a\tb", "name.", "name ", "CON", "con.txt", "NUL", "Com1.log", "LPT9", "aux .txt", new string('长', 256) })
            Assert.IsFalse(RenamePlan.ValidName(bad), bad);
        foreach (var good in new[] { "a.txt", "照片 1.PNG", "CONSOLE.txt", "nul-ish", "COM10", new string('长', 255) })
            Assert.IsTrue(RenamePlan.ValidName(good), good);
        Assert.IsFalse(RenamePlan.Preview([a], new RenameRules { Number = true, Start = -1 }).CanApply);
        Assert.IsFalse(RenamePlan.Preview([a], new RenameRules { Base = "CON" }).CanApply, "A device name is a conflict.");
        Assert.HasCount(1, RenamePlan.Preview([a, a, a.ToUpperInvariant()], new RenameRules { Prefix = "x-" }).Entries, "One entry per file, whatever the case.");
        Assert.IsFalse(RenamePlan.Preview([a], new RenameRules()).CanApply, "Nothing changes.");
    }

    [TestMethod]
    public void DestinationsDifferingOnlyInCaseAreDuplicates()
    {
        var dir = TestSetup.NewFolder("rename-case-dup");
        string a = File(dir, "one.txt"), b = File(dir, "two.txt");
        var rows = new List<RenameEntry>
        {
            new(a, Path.Combine(dir, "Same.txt"), FileIdentity.Read(a)),
            new(b, Path.Combine(dir, "same.TXT"), FileIdentity.Read(b)),
        };
        var plan = RenamePlan.Validate(rows);
        Assert.IsFalse(plan.CanApply);
        Assert.IsTrue(plan.Entries.All(e => e.Issue == Loc.L("Duplicate destination name")));
    }

    [TestMethod]
    public void RenameRejectsMissingAndParentChildSelections()
    {
        var dir = TestSetup.NewFolder("rename-parent");
        var a = File(dir, "a");
        Assert.IsFalse(RenamePlan.Preview([dir, a], new RenameRules { Prefix = "x" }).CanApply);
        var missing = RenamePlan.Preview([Path.Combine(dir, "missing")], new RenameRules { Prefix = "x" });
        Assert.IsFalse(missing.CanApply);
        Assert.AreEqual(Loc.L("File is missing or unavailable"), missing.Entries[0].Issue);
    }

    [TestMethod]
    public void RenameAndUndoPreserveDataAcrossDirectories()
    {
        string aDir = TestSetup.NewFolder("rename-a"), bDir = TestSetup.NewFolder("rename-b");
        string a = File(aDir, "one.txt", "alpha"), b = File(bDir, "two.txt", "beta");
        var plan = RenamePlan.Preview([a, b], new RenameRules { Base = "invoice", Number = true });
        var receipt = BatchRenamer.Execute(plan);
        Assert.AreEqual("alpha", Content(plan.Entries[0].Destination));
        Assert.AreEqual("beta", Content(plan.Entries[1].Destination));
        Assert.AreEqual("invoice-001.txt", Path.GetFileName(plan.Entries[0].Destination));
        Assert.AreEqual("invoice-002.txt", Path.GetFileName(plan.Entries[1].Destination));
        receipt.Undo();
        Assert.AreEqual("alpha", Content(a));
        Assert.AreEqual("beta", Content(b));
        CollectionAssert.AreEqual(new[] { "one.txt" }, Names(aDir));
    }

    [TestMethod]
    public void RenameSupportsSwapsAndCaseOnlyChanges()
    {
        var dir = TestSetup.NewFolder("rename-swap");
        string a = File(dir, "Alpha.txt", "alpha"), b = File(dir, "Beta.txt", "beta");
        var rows = new List<RenameEntry> { new(a, b, FileIdentity.Read(a)), new(b, a, FileIdentity.Read(b)) };
        var receipt = BatchRenamer.Execute(RenamePlan.Validate(rows));
        Assert.AreEqual("beta", Content(a));
        Assert.AreEqual("alpha", Content(b));
        receipt.Undo();
        Assert.AreEqual("alpha", Content(a));
        // Windows names are case-insensitive: "alpha.txt" is the same file, renamed through a temporary name.
        var plan = RenamePlan.Preview([a], new RenameRules { Find = "Alpha", Replacement = "alpha" });
        Assert.IsTrue(plan.CanApply, plan.Entries[0].Issue);
        var caseOnly = BatchRenamer.Execute(plan);
        CollectionAssert.AreEqual(new[] { "Beta.txt", "alpha.txt" }, Names(dir), "The new spelling is on disk.");
        Assert.AreEqual("alpha", Content(Path.Combine(dir, "alpha.txt")));
        caseOnly.Undo();
        CollectionAssert.AreEqual(new[] { "Alpha.txt", "Beta.txt" }, Names(dir));
    }

    [TestMethod]
    public void FoldersRenameWithTheirContents()
    {
        var dir = TestSetup.NewFolder("rename-folder");
        var folder = Directory.CreateDirectory(Path.Combine(dir, "photos.2024")).FullName;
        File(folder, "inside.txt", "kept");
        var receipt = BatchRenamer.Execute(RenamePlan.Preview([folder + "\\"], new RenameRules { Prefix = "old " }));
        Assert.AreEqual("kept", Content(Path.Combine(dir, "old photos.2024", "inside.txt")));
        receipt.Undo();
        Assert.AreEqual("kept", Content(Path.Combine(folder, "inside.txt")));
    }

    [TestMethod]
    public void StaleRenamePreviewAndLateDestinationCannotReplaceFiles()
    {
        var dir = TestSetup.NewFolder("rename-stale");
        var a = File(dir, "a.txt", "old");
        var plan = RenamePlan.Preview([a], new RenameRules { Prefix = "new-" });
        System.IO.File.Move(a, Path.Combine(dir, "saved-old"));
        System.IO.File.WriteAllText(a, "replacement");
        Assert.ThrowsExactly<ToolError>(() => BatchRenamer.Execute(plan));
        Assert.AreEqual("replacement", Content(a));
        var fresh = RenamePlan.Preview([a], new RenameRules { Prefix = "new-" });
        File(dir, "new-a.txt", "blocker");
        Assert.ThrowsExactly<ToolError>(() => BatchRenamer.Execute(fresh));
        Assert.AreEqual("replacement", Content(a));
        Assert.AreEqual("blocker", Content(Path.Combine(dir, "new-a.txt")));
    }

    [TestMethod]
    public void RenameRollsBackFailuresInStagingAndCommit()
    {
        foreach (var failAt in new[] { 2, 4 })
        {
            var dir = TestSetup.NewFolder("rename-rollback");
            string a = File(dir, "a.txt", "alpha"), b = File(dir, "b.txt", "beta");
            var plan = RenamePlan.Preview([a, b], new RenameRules { Prefix = "new-" });
            int calls = 0;
            Assert.ThrowsExactly<ToolError>(() => BatchRenamer.Execute(plan, (from, to) =>
            {
                if (++calls == failAt) throw new ToolError("injected failure");
                BatchRenamer.Move(from, to);
            }));
            Assert.AreEqual("alpha", Content(a));
            Assert.AreEqual("beta", Content(b));
            CollectionAssert.AreEqual(new[] { "a.txt", "b.txt" }, Names(dir), $"fail at {failAt}");
        }
    }

    [TestMethod]
    public void RenameNoReplaceIsAtomicAndUndoRejectsConflicts()
    {
        var dir = TestSetup.NewFolder("rename-noreplace");
        string a = File(dir, "a.txt", "alpha"), b = File(dir, "b.txt", "beta");
        Assert.ThrowsExactly<ToolError>(() => BatchRenamer.Move(a, b));
        Assert.AreEqual("alpha", Content(a));
        Assert.AreEqual("beta", Content(b));
        var receipt = BatchRenamer.Execute(RenamePlan.Preview([a], new RenameRules { Prefix = "new-" }));
        File(dir, "a.txt", "new-original");
        Assert.ThrowsExactly<ToolError>(() => receipt.Undo());
        Assert.AreEqual("new-original", Content(a));
        Assert.AreEqual("alpha", Content(receipt.Entries[0].Destination));
    }

    [TestMethod]
    public void UndoRefusesAFileReplacedAfterTheRename()
    {
        var dir = TestSetup.NewFolder("rename-undo-stale");
        var a = File(dir, "a.txt", "alpha");
        var receipt = BatchRenamer.Execute(RenamePlan.Preview([a], new RenameRules { Prefix = "new-" }));
        var renamed = receipt.Entries[0].Destination;
        System.IO.File.Move(renamed, Path.Combine(dir, "elsewhere.txt"));
        System.IO.File.WriteAllText(renamed, "impostor");
        Assert.ThrowsExactly<ToolError>(() => receipt.Undo());
        Assert.AreEqual("impostor", Content(renamed));
        Assert.IsFalse(System.IO.File.Exists(a));
    }

    [TestMethod]
    public void RenameReportsRecoveryLocationsWithoutOverwritingANewOriginal()
    {
        var dir = TestSetup.NewFolder("rename-recovery");
        string a = File(dir, "a.txt", "alpha"), b = File(dir, "b.txt", "beta");
        var plan = RenamePlan.Preview([a, b], new RenameRules { Prefix = "new-" });
        int calls = 0;
        var error = Assert.ThrowsExactly<ToolError>(() => BatchRenamer.Execute(plan, (from, to) =>
        {
            if (++calls == 3)
            {
                System.IO.File.WriteAllText(a, "blocker");
                throw new ToolError("injected failure");
            }
            BatchRenamer.Move(from, to);
        }));
        StringAssert.Contains(error.Message, ".swivel-rename-");
        Assert.AreEqual("blocker", Content(a));
        Assert.AreEqual("beta", Content(b));
        var staged = Directory.EnumerateFiles(dir, ".swivel-rename-*").ToList();
        Assert.HasCount(1, staged);
        Assert.AreEqual("alpha", Content(staged[0]));
    }

    [TestMethod]
    public void RenameWindowPreviewsAppliesAndUndoes()
    {
        Prefs.SetFeatureEnabled(Feature.BatchRename, true);
        var dir = TestSetup.NewFolder("rename-window");
        string a = File(dir, "photo-one.txt", "first"), b = File(dir, "photo-two.txt", "second");
        Sta(() =>
        {
            var tool = new BatchRenameWindow([a, b]);
            tool.ShowError = (_, message) => Assert.Fail(message);
            try
            {
                Assert.IsFalse(tool.ApplyButton.IsEnabled, "Nothing to rename yet.");
                tool.BaseName.Text = "trip";
                tool.Numbering.IsChecked = true;
                Assert.IsTrue(tool.StartAt.IsEnabled);
                Assert.IsTrue(tool.ApplyButton.IsEnabled);
                Assert.AreEqual(Loc.L("%d files will be renamed", 2), tool.Status);
                CollectionAssert.AreEqual(new[] { "trip-001.txt", "trip-002.txt" }, tool.Plan.Entries.Select(e => Path.GetFileName(e.Destination)).ToList());
                tool.StartAt.Text = "x";
                Assert.IsFalse(tool.ApplyButton.IsEnabled, "A bad start number blocks renaming.");
                Assert.AreEqual(Loc.L("Resolve the highlighted conflicts first."), tool.Status);
                tool.StartAt.Text = "1";
                foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
                {
                    tool.Window.ThemeMode = theme;
                    if (!tool.Window.IsVisible) ShowOffscreen(tool.Window);
                    Pump(300);
                    SaveRender(tool, "batch-rename-" + (theme == ThemeMode.Dark ? "dark" : "light"), theme == ThemeMode.Dark);
                }
                Wait(tool.ApplyRename());
                CollectionAssert.AreEqual(new[] { "trip-001.txt", "trip-002.txt" }, Names(dir));
                Assert.AreEqual(Loc.L("Renamed %d files. Undo is available in this window.", 2), tool.Status);
                Assert.IsTrue(tool.UndoButton.IsEnabled);
                Wait(tool.UndoRename());
                CollectionAssert.AreEqual(new[] { "photo-one.txt", "photo-two.txt" }, Names(dir));
                Assert.AreEqual(Loc.L("Original names restored"), tool.Status);
                Assert.IsFalse(tool.UndoButton.IsEnabled);
                // A conflict shows in the preview, in red, before anything changes.
                File(dir, "trip-001.txt", "blocker");
                tool.RefreshPreview();
                Assert.IsFalse(tool.ApplyButton.IsEnabled);
                Assert.AreEqual(Loc.L("A file with this name already exists"), tool.Plan.Entries[0].Issue);
                Pump(200);
                SaveRender(tool, "batch-rename-conflict-dark", dark: true);
                // Turning the feature off closes the window.
                Feature.BatchRename.SetOn(false);
                Pump(100);
                Assert.IsFalse(tool.Window.IsVisible);
            }
            finally { tool.Window.Close(); }
        });
        Assert.AreEqual("first", Content(a));
    }

    // MARK: PDF pages

    [TestMethod]
    public void PdfRangeValidationAndPageMovement()
    {
        CollectionAssert.AreEqual(new[] { 0, 2, 3, 4 }, PdfPageOrder.Parse("1, 3-5，3", 6).ToList());
        foreach (var invalid in new[] { "", "0", "7", "5-3", "1,", "x", "1--2", "1-99999999999", "-2", "2-" })
            Assert.ThrowsExactly<ToolError>(() => PdfPageOrder.Parse(invalid, 6), invalid);
        var pages = Enumerable.Range(0, 5).Select(i => new PdfPageItem(@"C:\a.pdf", i)).ToList();
        CollectionAssert.AreEqual(new[] { 0, 3, 4, 1, 2 }, PdfPageOrder.Moving(pages, [1, 2], 5).Select(p => p.Page).ToList());
        CollectionAssert.AreEqual(new[] { 3, 4, 0, 1, 2 }, PdfPageOrder.Moving(pages, [3, 4], 0).Select(p => p.Page).ToList());
        Assert.IsTrue(PdfPageOrder.Moving(pages, [1, 2], 2).SequenceEqual(pages));
    }

    [TestMethod]
    public void PdfExportsExactOrderRotationSelectionAndUniqueFiles()
    {
        var dir = TestSetup.NewFolder("pdf-export");
        var source = Pdf(Path.Combine(dir, "source.pdf"), "PAGE A", "PAGE B", "PAGE C");
        var original = System.IO.File.ReadAllBytes(source);
        var snapshot = PdfPagesSnapshot.Load([source]);
        var rotated = snapshot.Items[2] with { Rotation = 90 };
        var output = snapshot.Export([rotated, snapshot.Items[0]]);
        var second = snapshot.Export([snapshot.Items[1]]);
        Assert.AreEqual("source arranged.pdf", Path.GetFileName(output));
        Assert.AreEqual("source arranged 2.pdf", Path.GetFileName(second));
        var read = PdfFile.Open(output);
        Assert.HasCount(2, read.Pages);
        // Page content is copied byte for byte, not re-rendered; sizes come along.
        var src = PdfFile.Open(source);
        Assert.AreEqual(PageText(src, 2), PageText(read, 0));
        Assert.AreEqual(PageText(src, 0), PageText(read, 1));
        StringAssert.Contains(PageText(read, 0), "(PAGE C)");
        Assert.AreEqual(90, read.Pages[0].Rotate);
        Assert.AreEqual(0, read.Pages[1].Rotate);
        Assert.AreEqual(320, read.Pages[0].Media.X1);
        Assert.AreEqual(300, read.Pages[1].Media.X1);
        // The shared font is written once.
        Assert.AreEqual(1, Encoding.Latin1.GetString(System.IO.File.ReadAllBytes(output)).Split("/BaseFont /Helvetica").Length - 1);
        Assert.HasCount(1, PdfFile.Open(second).Pages);
        CollectionAssert.AreEqual(original, System.IO.File.ReadAllBytes(source), "The original is unchanged.");
        Assert.AreEqual(dir, Path.GetDirectoryName(output));
        Assert.IsFalse(Directory.EnumerateFiles(dir, "*.tmp").Any(), "No temporary file is left behind.");
        Assert.ThrowsExactly<ToolError>(() => snapshot.Export([]));
    }

    [TestMethod]
    public void PdfSnapshotsSurviveExternalEditsAndCombineDifferentSources()
    {
        var dir = TestSetup.NewFolder("pdf-snapshot");
        var a = Pdf(Path.Combine(dir, "a.pdf"), "ALPHA", "ALPHA TWO");
        var b = Pdf(Path.Combine(dir, "b.pdf"), "BETA");
        var snapshot = PdfPagesSnapshot.Load([a, b, a]);
        Assert.HasCount(3, snapshot.Items, "A file chosen twice counts once.");
        System.IO.File.WriteAllText(a, "changed");
        // Interleaved sources: b, a2 rotated twice, a1.
        var output = snapshot.Export([snapshot.Items[2], snapshot.Items[1] with { Rotation = 180 }, snapshot.Items[0]]);
        var result = PdfFile.Open(output);
        StringAssert.Contains(PageText(result, 0), "(BETA)");
        StringAssert.Contains(PageText(result, 1), "(ALPHA TWO)");
        StringAssert.Contains(PageText(result, 2), "(ALPHA)");
        Assert.AreEqual(180, result.Pages[1].Rotate);
        Assert.AreEqual("a arranged.pdf", Path.GetFileName(output), "Saved beside the first original.");
        Assert.ThrowsExactly<ToolError>(() => PdfPagesSnapshot.Load([a]));
        Assert.ThrowsExactly<ToolError>(() => PdfPagesSnapshot.Load([]));
    }

    [TestMethod]
    public void PdfWindowSelectsRotatesReordersUndoesAndSaves()
    {
        Prefs.SetFeatureEnabled(Feature.PdfPages, true);
        var dir = TestSetup.NewFolder("pdf-window");
        var source = Pdf(Path.Combine(dir, "pages.pdf"), "PAGE ONE", "PAGE TWO", "PAGE THREE", "PAGE FOUR");
        Sta(() =>
        {
            var tool = new PdfPagesWindow([source]);
            tool.ShowError = (_, message) => Assert.Fail(message);
            var saved = new List<string>();
            tool.ShowSaved = saved.Add;
            try
            {
                ShowOffscreen(tool.Window);
                Wait(tool.Loading);
                Assert.HasCount(4, tool.Items);
                Assert.AreEqual(Loc.L("%d pages · %d selected", 4, 1), tool.Status);
                tool.Range.Text = "2-3";
                tool.SelectRange();
                CollectionAssert.AreEqual(new[] { 1, 2 }, tool.List.SelectedIndexes.ToList());
                tool.RotatePages();
                Assert.AreEqual(90, tool.Items[1].Rotation);
                tool.MoveUp();
                CollectionAssert.AreEqual(new[] { 1, 2, 0, 3 }, tool.Items.Select(i => i.Page).ToList());
                CollectionAssert.AreEqual(new[] { 0, 1 }, tool.List.SelectedIndexes.ToList(), "The moved pages stay selected.");
                // A drop moves the dragged rows (here: the first row to the end).
                tool.List.Moved!([0], 4);
                CollectionAssert.AreEqual(new[] { 2, 0, 3, 1 }, tool.Items.Select(i => i.Page).ToList());
                tool.UndoEdit();
                CollectionAssert.AreEqual(new[] { 1, 2, 0, 3 }, tool.Items.Select(i => i.Page).ToList());
                tool.Range.Text = "9";
                tool.SelectRange();
                Assert.AreEqual(Loc.L("Use page numbers such as 1, 3-5 within this document."), tool.Status);
                for (int i = 0; i < 100 && tool.Thumbnails!.Rendered < 4; i++) Pump(50);
                Assert.AreEqual(4, tool.Thumbnails!.Rendered, "Every visible page has a thumbnail.");
                foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
                {
                    tool.Window.ThemeMode = theme;
                    tool.List.SelectIndexes([0, 1]);
                    Pump(400);
                    SaveRender(tool, "pdf-pages-" + (theme == ThemeMode.Dark ? "dark" : "light"), theme == ThemeMode.Dark);
                }
                tool.List.SelectIndexes([3]);
                Wait(tool.ExportSelected());
                Wait(tool.SaveAll());
                Assert.HasCount(2, saved);
                var selection = PdfFile.Open(saved[0]);
                Assert.HasCount(1, selection.Pages);
                StringAssert.Contains(PageText(selection, 0), "(PAGE FOUR)");
                var all = PdfFile.Open(saved[1]);
                CollectionAssert.AreEqual(new[] { "(PAGE TWO)", "(PAGE THREE)", "(PAGE ONE)", "(PAGE FOUR)" },
                    Enumerable.Range(0, 4).Select(i => PageText(all, i)[(PageText(all, i).IndexOf('('))..(PageText(all, i).IndexOf(')') + 1)]).ToList());
                CollectionAssert.AreEqual(new[] { 90, 90, 0, 0 }, all.Pages.Select(p => p.Rotate).ToList());
                tool.RemovePages();
                Assert.HasCount(3, tool.Items);
            }
            finally { tool.Window.Close(); }
        });
    }

    [TestMethod]
    public void PdfWindowReportsUnreadableFiles()
    {
        Prefs.SetFeatureEnabled(Feature.PdfPages, true);
        var dir = TestSetup.NewFolder("pdf-broken");
        var broken = File(dir, "broken.pdf", "not a pdf");
        Sta(() =>
        {
            string? shown = null;
            var tool = new PdfPagesWindow([broken]);
            tool.ShowError = (_, message) => shown = message;
            try
            {
                for (int i = 0; i < 200 && tool.IsBusy; i++) Pump(20);
                Assert.AreEqual(Loc.L("Could not open %@. Unlock password-protected PDFs first.", "broken.pdf"), shown);
                Assert.AreEqual(Loc.L("Choose a PDF to get started."), tool.Status);
                Assert.IsFalse(tool.SaveButton.IsEnabled);
            }
            finally { tool.Window.Close(); }
        });
    }

    [TestMethod]
    public void PanesFollowTheirSwitches()
    {
        Sta(() =>
        {
            Prefs.SetFeatureEnabled(Feature.BatchRename, false);
            Prefs.SetFeatureEnabled(Feature.PdfPages, true);
            var rename = new BatchRenamePane();
            var pdf = new PdfPagesPane();
            Assert.IsFalse(rename.Choose.IsEnabled);
            Assert.IsTrue(pdf.Choose.IsEnabled);
        });
    }

    // MARK: Clothesline card

    [TestMethod]
    public void IncomingCardIsAnAccessibleHangButtonOnlyWhileAScreenshotWaits()
    {
        Sta(() =>
        {
            var view = new IncomingView(IncomingCard.Shared);
            var peer = UIElementAutomationPeer.CreatePeerForElement(view);
            Assert.AreEqual(AutomationControlType.Button, peer.GetAutomationControlType());
            Assert.AreEqual(Loc.L("Hang Screenshot"), peer.GetName());
            Assert.AreEqual(Loc.L("Click to hang this screenshot on the clothesline."), peer.GetHelpText());
            var invoke = peer.GetPattern(PatternInterface.Invoke) as IInvokeProvider;
            Assert.IsNotNull(invoke);
            Assert.IsFalse(peer.IsEnabled(), "No screenshot is waiting.");
            Assert.ThrowsExactly<System.Windows.Automation.ElementNotEnabledException>(() => invoke.Invoke());
        });
    }
}
