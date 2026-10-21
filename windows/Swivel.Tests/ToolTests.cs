using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Swivel;

namespace Swivel.Tests;

/// The tools: File Shelf references and undo, Quick Actions commands, search and gating, the global hotkey, Text
/// Capture OCR and its window, and applying image presets (ports of NativeToolsTests.swift and ToolsFeelTests.swift).
[TestClass]
public sealed class ToolTests
{
    [TestInitialize]
    public void Reset()
    {
        TestSetup.ResetPrefs();
        Prefs.LanguageId = "en";
    }

    // MARK: Helpers

    /// Runs `body` on an STA thread with a WPF dispatcher (windows, hotkeys, RenderTargetBitmap).
    static void Sta(Action body)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            // Awaits inside the tools continue on this thread's dispatcher, as they would on the app's UI thread.
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

    /// Lets the dispatcher run (layout, background icon loads, timers) for a while.
    static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    /// Where offscreen renders go for a person to look at (set SWIVEL_TEST_RENDER to a folder), or null.
    static string? RenderFolder => Environment.GetEnvironmentVariable("SWIVEL_TEST_RENDER") is { Length: > 0 } dir ? Directory.CreateDirectory(dir).FullName : null;

    static void SaveRender(Window window, string name)
    {
        if (RenderFolder is not { } folder || window.Content is not FrameworkElement content) return;
        window.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(content);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(content.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var dc = background.RenderOpen())
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        bitmap.Render(background);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(folder, name + ".png"));
        encoder.Save(file);
    }

    static List<string> MakeFiles(string folder, params string[] names)
    {
        var paths = names.Select(n => Path.Combine(folder, n)).ToList();
        foreach (var path in paths) File.WriteAllText(path, Path.GetFileName(path));
        return paths;
    }

    /// An image with optional text, metadata (Author, GPS) and EXIF orientation, written as PNG or JPG.
    static void MakeImage(string path, int width = 800, int height = 400, string? text = null, string font = "Segoe UI",
                          ushort orientation = 1, string? author = null, bool gps = false, bool transparent = false, bool noise = false)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            if (!transparent) dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            else dc.DrawRectangle(Brushes.Red, null, new Rect(width / 4.0, height / 4.0, width / 2.0, height / 2.0));
            if (noise)
            {
                var random = new Random(4);
                for (int i = 0; i < 1500; i++)
                    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256))), null,
                        new Rect(random.Next(width), random.Next(height), 6, 6));
            }
            if (text != null)
            {
                var formatted = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(font), 48, Brushes.Black, 1);
                dc.DrawText(formatted, new Point(30, height / 2.0 - formatted.Height / 2));
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bool jpeg = path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase);
        BitmapEncoder encoder = jpeg ? new JpegBitmapEncoder { QualityLevel = 95 } : new PngBitmapEncoder();
        BitmapMetadata? metadata = null;
        if (jpeg && (orientation != 1 || author != null || gps))
        {
            metadata = new BitmapMetadata("jpg");
            if (orientation != 1) metadata.SetQuery("/app1/ifd/{ushort=274}", orientation);
            if (author != null) metadata.Author = new System.Collections.ObjectModel.ReadOnlyCollection<string>([author]);
            if (gps)
            {
                metadata.SetQuery("/app1/ifd/gps/{ushort=1}", "S");
                metadata.SetQuery("/app1/ifd/gps/{ushort=2}", new ulong[] { 27UL | (1UL << 32), 24UL | (1UL << 32), 0UL | (1UL << 32) });
            }
        }
        encoder.Frames.Add(BitmapFrame.Create(jpeg ? new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0) : bitmap, null, metadata, null));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    static BitmapFrame Decode(string path)
    {
        using var stream = File.OpenRead(path);
        return BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
    }

    /// The converters module provides Images.Load; until it is in place the preset checks can't run.
    static void RequireImages()
    {
        try { Images.Load(Path.Combine(TestSetup.Scratch, "missing.png")); }
        catch (NotImplementedException) { Assert.Inconclusive("Images.Load is not implemented yet (Converters module)."); }
        catch (Exception) { }
    }

    // MARK: Shelf

    [TestMethod]
    public void ShelfReferencesDeduplicateAndNeverDeleteOriginals()
    {
        var folder = TestSetup.NewFolder("shelf");
        var (a, b) = (Path.Combine(folder, "a.txt"), Path.Combine(folder, "b.txt"));
        File.WriteAllText(a, "a");
        File.WriteAllText(b, "b");
        var shelf = new ShelfContents();
        shelf.Add([a, a.ToUpperInvariant(), b, folder + "\\", folder, Path.Combine(folder, "missing"), "relative.txt", ""]);
        CollectionAssert.AreEqual(new[] { a, b, folder }, shelf.Paths.ToList());
        shelf.Remove([0, 2]);
        CollectionAssert.AreEqual(new[] { b }, shelf.Paths.ToList());
        shelf.Clear();
        Assert.IsEmpty(shelf.Paths);
        Assert.AreEqual("a", File.ReadAllText(a));
        Assert.AreEqual("b", File.ReadAllText(b));
    }

    [TestMethod]
    public void ShelfReportsWhatArrivedAndWhatWasAlreadyThere()
    {
        var folder = TestSetup.NewFolder("shelf");
        var files = MakeFiles(folder, "a.txt", "b.txt", "c.txt");
        var shelf = new ShelfContents();
        var first = shelf.Add([files[0], files[1], files[0]]);
        CollectionAssert.AreEqual(new[] { 0, 1 }, first.Added.ToList());
        Assert.IsEmpty(first.Existing);
        var second = shelf.Add([files[1], files[2], Path.Combine(folder, "missing.txt")]);
        CollectionAssert.AreEqual(new[] { 2 }, second.Added.ToList());
        CollectionAssert.AreEqual(new[] { 1 }, second.Existing.ToList());
        CollectionAssert.AreEqual(files, shelf.Paths.ToList());
    }

    [TestMethod]
    public void ShelfUndoPutsReferencesBackInPlace()
    {
        var folder = TestSetup.NewFolder("shelf");
        var files = MakeFiles(folder, "0.txt", "1.txt", "2.txt", "3.txt");
        var shelf = new ShelfContents();
        shelf.Add(files);
        shelf.Remove([0, 2]);
        CollectionAssert.AreEqual(new[] { files[1], files[3] }, shelf.Paths.ToList());
        var rows = shelf.Restore([(0, files[0]), (2, files[2])]);
        CollectionAssert.AreEqual(files, shelf.Paths.ToList());
        CollectionAssert.AreEqual(new[] { 0, 2 }, rows.ToList());
    }

    [TestMethod]
    public void ShelfUndoSkipsFilesAddedAgainAndClampsPositions()
    {
        var folder = TestSetup.NewFolder("shelf");
        var files = MakeFiles(folder, "0.txt", "1.txt", "2.txt");
        var shelf = new ShelfContents();
        shelf.Add(files);
        shelf.Clear();
        shelf.Add([files[1]]);
        var rows = shelf.Restore([(0, files[0]), (1, files[1]), (2, files[2])]);
        CollectionAssert.AreEqual(files, shelf.Paths.ToList());
        CollectionAssert.AreEqual(new[] { 0, 2 }, rows.ToList());
        var empty = new ShelfContents();
        CollectionAssert.AreEqual(new[] { 0 }, empty.Restore([(5, files[2])]).ToList());
        CollectionAssert.AreEqual(new[] { files[2] }, empty.Paths.ToList());
    }

    [TestMethod]
    public void UndoStackRedoesWhatWasUndone()
    {
        var undo = new UndoStack();
        var log = new List<string>();
        void remove() { log.Add("remove"); undo.Register(restore); }
        void restore() { log.Add("restore"); undo.Register(remove); }
        undo.Register(restore);
        undo.Undo();
        Assert.IsTrue(undo.CanRedo);
        undo.Redo();
        undo.Undo();
        CollectionAssert.AreEqual(new[] { "restore", "remove", "restore" }, log);
        undo.Register(() => log.Add("new"));
        Assert.IsFalse(undo.CanRedo, "A new action clears the redo stack.");
    }

    /// The real shelf panel, offscreen: adding, a duplicate pointed out instead of added, remove with Ctrl+Z undo.
    [TestMethod]
    public void ShelfPanelAddsRemovesAndUndoes()
    {
        var folder = TestSetup.NewFolder("shelfpanel");
        var files = MakeFiles(folder, "notes.txt", "list.csv");
        var image = Path.Combine(folder, "photo.png");
        Sta(() =>
        {
            MakeImage(image, 400, 300, text: "Shelf");
            files.Add(image);
            Prefs.SetFeatureEnabled(Feature.Shelf, true);
            ToolPanel.OffscreenForTests = true;
            var shelf = new Shelf();
            try
            {
                shelf.Add(files);
                Assert.IsTrue(shelf.IsVisible);
                CollectionAssert.AreEqual(files, shelf.Items.ToList());
                shelf.Add([files[0]]);
                Assert.HasCount(3, shelf.Items, "A duplicate is pointed out, not added.");
                Assert.AreEqual(Loc.L("Already on the shelf"), shelf.StatusText);
                shelf.SelectRows([0, 2]);
                Pump(600);
                SaveRender(shelf.Panel!, "shelf");
                shelf.RemoveSelectedForTests();
                CollectionAssert.AreEqual(new[] { files[1] }, shelf.Items.ToList());
                StringAssert.StartsWith(shelf.StatusText, "2 removed");
                shelf.UndoForTests();
                CollectionAssert.AreEqual(files, shelf.Items.ToList());
                Assert.IsTrue(File.Exists(files[0]) && File.Exists(image), "Originals stay where they are.");
                Prefs.SetFeatureEnabled(Feature.Shelf, false);
                shelf.Add([Path.Combine(folder, "list.csv")]);
                Assert.HasCount(3, shelf.Items);
            }
            finally
            {
                shelf.Hide();
                shelf.Panel?.Close();
                ToolPanel.OffscreenForTests = false;
            }
        });
    }

    // MARK: Quick Actions

    [TestMethod]
    public void FeatureGatesAndSearchRespectChoices()
    {
        var image = @"Z:\nonexistent\image.png";
        foreach (var feature in new[] { Feature.TextCapture, Feature.Shelf, Feature.ImagePresets, Feature.QuickActions })
            Prefs.SetFeatureEnabled(feature, false);
        Assert.IsFalse(Catalog.DefaultItems([image], WheelMode.Tools).Any(i => i.Title == "Extract Text" || i.Title.StartsWith("preset.")));
        Assert.IsFalse(Catalog.DefaultItems([image], WheelMode.Actions).Any(i => i.Title == "Float"));
        Assert.IsFalse(QuickActions.MakeCommands([]).Any(c => c.Id is "shelf" or "chooseOCR"));
        Prefs.SetFeatureEnabled(Feature.TextCapture, true);
        Prefs.SetFeatureEnabled(Feature.ImagePresets, true);
        Prefs.ImagePresets = [.. ImagePreset.Starters];
        Prefs.IsEnabled = false;
        var commands = QuickActions.MakeCommands([image]);
        Assert.IsTrue(commands.Any(c => c.Id == "tools.Extract Text"));
        Assert.IsTrue(commands.Any(c => c.Id == "tools.preset.web"));
        Assert.IsFalse(commands.Any(c => c.Id == "tools.Crop"), "With the Wheel off only the standalone tools are offered.");
        Assert.HasCount(1, QuickActions.Matching(commands, "preset.web"));
        Assert.IsEmpty(QuickActions.Matching(commands, "NONEXISTENT_ACTION"));
        Assert.IsTrue(Catalog.DefaultItems([image, image], WheelMode.Tools).All(i => i.Title != "Extract Text"));
        Assert.IsTrue(QuickActions.MakeCommands([]).Any(c => c.Id == "chooseOCR"));
        Prefs.SetFeatureEnabled(Feature.Shelf, true);
        Assert.IsTrue(QuickActions.MakeCommands([]).Any(c => c.Id == "shelf"));
        Prefs.SetFeatureEnabled(Feature.ImagePresets, false);
        Assert.HasCount(3, Prefs.ImagePresets, "Turning the feature off keeps the presets.");
        Assert.IsFalse(QuickActions.MakeCommands([image]).Any(c => c.Id.StartsWith("tools.preset.")));
    }

    [TestMethod]
    public void SearchRanksTitleMatchesFirstAndIgnoresCaseAndAccents()
    {
        static QuickCommand C(string id, string title, string detail) => new(id, title, detail, "", () => Task.FromResult<ActionResult?>(null));
        var commands = new List<QuickCommand>
        {
            C("tools.Compress", "Compress", "Tools"),
            C("tools.Make PDF", "Make PDF", "Tools"),
            C("convert.PDF", "PDF", "Convert"),
            C("tools.Crop", "Crop", "Tools"),
            C("actions.Café", "Café Menu", "Actions"),
        };
        CollectionAssert.AreEqual(new[] { "convert.PDF", "tools.Make PDF" }, QuickActions.Matching(commands, "pdf").Select(c => c.Id).ToList());
        CollectionAssert.AreEqual(new[] { "convert.PDF" }, QuickActions.Matching(commands, "conv").Select(c => c.Id).ToList());
        // Every word must match somewhere; title matches come before menu-name matches.
        CollectionAssert.AreEqual(new[] { "tools.Compress", "tools.Crop" }, QuickActions.Matching(commands, "  tools  c ").Select(c => c.Id).ToList());
        Assert.HasCount(1, QuickActions.Matching(commands, "CAFE"));
        Assert.HasCount(5, QuickActions.Matching(commands, "   "));
        CollectionAssert.AreEqual(new[] { "tools.Crop" }, QuickActions.Matching(commands, "cr tools").Select(c => c.Id).ToList());
    }

    /// The panel itself, offscreen: commands for the chosen files, filtering as you type.
    [TestMethod]
    public void QuickActionsPanelListsCommandsForChosenFiles()
    {
        var folder = TestSetup.NewFolder("quick");
        var image = Path.Combine(folder, "photo.png");
        Sta(() =>
        {
            MakeImage(image, 300, 200);
            Prefs.SetFeatureEnabled(Feature.QuickActions, true);
            Prefs.SetFeatureEnabled(Feature.TextCapture, true);
            Prefs.SetFeatureEnabled(Feature.Shelf, true);
            ToolPanel.OffscreenForTests = true;
            var quick = new QuickActions();
            try
            {
                quick.Show([image]);
                Assert.IsTrue(quick.IsVisible);
                Assert.IsTrue(quick.VisibleCommands.Any(c => c.Id == "tools.Extract Text"));
                Assert.AreEqual(0, quick.SelectedRow);
                quick.SearchForTests("extract");
                Assert.AreEqual("tools.Extract Text", quick.VisibleCommands[0].Id);
                quick.SearchForTests("");
                Pump(300);
                SaveRender(quick.Panel!, "quick-actions");
                quick.SearchForTests("NONEXISTENT_ACTION");
                Assert.IsEmpty(quick.VisibleCommands);
                Prefs.SetFeatureEnabled(Feature.QuickActions, false);
                quick.SyncSettings();
                Assert.IsFalse(quick.IsVisible, "Turning the feature off closes the panel.");
            }
            finally
            {
                quick.Hide();
                quick.Panel?.Close();
                ToolPanel.OffscreenForTests = false;
            }
        });
    }

    [TestMethod]
    public void TwoHotkeysDoNotReplaceEachOtherAndConflictsAreReported()
    {
        Sta(() =>
        {
            const uint mods = HotKey.MOD_CONTROL | HotKey.MOD_ALT | HotKey.MOD_SHIFT;
            using var first = new HotKey(mods, 0x86 /* F23 */, () => { });
            using var second = new HotKey(mods, 0x87 /* F24 */, () => { });
            using var duplicate = new HotKey(mods, 0x86, () => { });
            Assert.IsTrue(first.IsRegistered, $"status {first.Status}");
            Assert.AreEqual(0, first.Status);
            Assert.IsTrue(second.IsRegistered, $"status {second.Status}");
            Assert.IsFalse(duplicate.IsRegistered, "The same combination from another window must fail.");
            Assert.AreEqual(1409, duplicate.Status, "ERROR_HOTKEY_ALREADY_REGISTERED");
            first.Unregister();
            using var again = new HotKey(mods, 0x86, () => { });
            Assert.IsTrue(again.IsRegistered, "Unregistering frees the combination.");
        });
    }

    [TestMethod]
    public void QuickActionsShortcutFollowsTheSettings()
    {
        Sta(() =>
        {
            var quick = new QuickActions();
            int changes = 0;
            quick.ShortcutChanged += () => changes++;
            try
            {
                Prefs.SetFeatureEnabled(Feature.QuickActions, true);
                Prefs.QuickShortcut = QuickShortcut.P;
                quick.SyncSettings();
                Assert.AreEqual(1, changes);
                Assert.AreEqual(QuickShortcut.P, quick.RegisteredShortcut);
                // Whatever holds Ctrl+Shift+P on this PC, the panel reports what the registration did.
                using (var probe = new HotKey(QuickShortcut.P.Win32(), () => { }))
                    Assert.IsFalse(quick.ShortcutAvailable && probe.IsRegistered, "Either Swivel holds it, or it was unavailable.");
                quick.SyncSettings();
                Assert.AreEqual(1, changes, "An unchanged shortcut is not registered again.");
                Prefs.QuickShortcut = QuickShortcut.K;
                quick.SyncSettings();
                Assert.AreEqual(2, changes);
                Assert.AreEqual(QuickShortcut.K, quick.RegisteredShortcut);
            }
            finally
            {
                Prefs.SetFeatureEnabled(Feature.QuickActions, false);
                quick.SyncSettings();
                Assert.IsNull(quick.RegisteredShortcut);
            }
        });
    }

    [TestMethod]
    public void ActivationGoesBackOnlyWhenItShould()
    {
        var other = ToolNative.GetShellWindow();
        if (other == IntPtr.Zero) Assert.Inconclusive("No shell window to hand activation to.");
        var handBack = new ActivationReturn();
        handBack.Remember(other);
        Assert.AreEqual(other, handBack.Take(swivelIsActive: true));
        Assert.AreEqual(IntPtr.Zero, handBack.Take(swivelIsActive: true), "Only once.");
        // The user clicked into another app meanwhile: leave activation where they put it.
        handBack.Remember(other);
        Assert.AreEqual(IntPtr.Zero, handBack.Take(swivelIsActive: false));
        Assert.AreEqual(IntPtr.Zero, handBack.Window);
        handBack.Remember(IntPtr.Zero);
        Assert.AreEqual(IntPtr.Zero, handBack.Take(swivelIsActive: true));
        Sta(() =>
        {
            var own = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("own"));
            try
            {
                var mine = new ActivationReturn();
                mine.Remember(own.Handle);
                Assert.AreEqual(IntPtr.Zero, mine.Window, "Swivel's own windows are never remembered.");
            }
            finally { own.Dispose(); }
        });
    }

    // MARK: Text Capture

    [TestMethod]
    public void JoinsCjkWithoutSpacesAndLatinWithSpaces()
    {
        Assert.AreEqual("Hello world", TextRecognition.JoinWords(["Hello", "world"]));
        Assert.AreEqual("你好世界", TextRecognition.JoinWords(["你", "好", "世", "界"]));
        Assert.AreEqual("使用 Windows 系统", TextRecognition.JoinWords(["使", "用", "Windows", "系", "统"]));
        Assert.AreEqual("こんにちは、世界", TextRecognition.JoinWords(["こ", "ん", "に", "ち", "は", "、", "世", "界"]));
        Assert.AreEqual("", TextRecognition.JoinWords([" ", ""]));
    }

    [TestMethod]
    public void PicksTheReadingThatFitsItsScript()
    {
        // English text: the English reading wins over a Chinese recognizer's near-identical one.
        Assert.AreEqual("SWIVEL 3410", TextRecognition.PickBest([("en", "SWIVEL 3410"), ("zh-Hans", "SWlVEL 341O"), ("ja", "SWIVEL 3410")], "zh-Hans"));
        // Chinese text: the English recognizer's stray letters lose.
        Assert.AreEqual("你好世界", TextRecognition.PickBest([("en", "T{ii Ilt"), ("zh-Hans", "你好世界"), ("ja", "你好世")]));
        // Japanese text with kana: the Japanese reading wins.
        Assert.AreEqual("こんにちは世界", TextRecognition.PickBest([("en", "C h IC 5 l"), ("zh-Hans", "世界"), ("ja", "こんにちは世界")]));
        // A tie between the two Chinese readings goes to the app's language.
        Assert.AreEqual("繁", TextRecognition.PickBest([("zh-Hans", "简"), ("zh-Hant", "繁")], "zh-Hant"));
        Assert.AreEqual("简", TextRecognition.PickBest([("zh-Hans", "简"), ("zh-Hant", "繁")], "zh-Hans"));
        Assert.AreEqual("", TextRecognition.PickBest([]));
        Assert.AreEqual("zh-Hant", TextRecognition.Classify("zh-TW"));
        Assert.AreEqual("zh-Hans", TextRecognition.Classify("zh-Hans-CN"));
        Assert.AreEqual("en", TextRecognition.Classify("en-GB"));
        Assert.IsNull(TextRecognition.Classify("de-DE"));
    }

    [TestMethod]
    public async Task FileOcrFindsTextAndHandlesBlankAndInvalidImages()
    {
        if (TextRecognition.Engines().Count == 0) Assert.Inconclusive("No OCR language is installed on this PC.");
        var folder = TestSetup.NewFolder("ocr");
        var text = Path.Combine(folder, "text.png");
        var blank = Path.Combine(folder, "blank.png");
        Sta(() =>
        {
            MakeImage(text, 1000, 400, text: "SWIVEL 3410");
            MakeImage(blank);
        });
        var result = await TextRecognition.Recognize(text);
        StringAssert.Contains(result.ToUpperInvariant(), "SWIVEL", result);
        StringAssert.Contains(result, "3410", result);
        Assert.AreEqual("", await TextRecognition.Recognize(blank));
        await Assert.ThrowsAsync<ToolError>(() => TextRecognition.Recognize(Path.Combine(folder, "missing.png")));
        var broken = Path.Combine(folder, "broken.png");
        File.WriteAllText(broken, "not an image");
        await Assert.ThrowsAsync<ToolError>(() => TextRecognition.Recognize(broken));
    }

    [TestMethod]
    public async Task FileOcrReadsChineseAndJapaneseWhenInstalled()
    {
        var languages = TextRecognition.Engines().Select(e => e.Language).ToList();
        var folder = TestSetup.NewFolder("ocrcjk");
        var checkedAny = false;
        foreach (var (language, sample, font) in new[] { ("zh-Hans", "你好世界文字识别", "Microsoft YaHei"), ("ja", "こんにちは世界", "Yu Gothic") })
        {
            if (!languages.Contains(language)) continue;
            var path = Path.Combine(folder, language + ".png");
            Sta(() => MakeImage(path, 1000, 300, text: sample, font: font));
            var result = await TextRecognition.Recognize(path);
            var compact = result.Replace(" ", "").Replace("\n", "");
            StringAssert.Contains(compact, sample[..4], $"{language}: {result}");
            Assert.IsFalse(result.Contains(sample[0] + " " + sample[1]), "No spaces between CJK characters.");
            checkedAny = true;
        }
        if (!checkedAny) Assert.Inconclusive("Neither the Chinese (Simplified) nor the Japanese OCR language is installed.");
    }

    /// Return reaches the default Copy button once the text is ready; Copy takes the selection when there is one.
    [TestMethod]
    public void TextCaptureCopiesTheTextOrTheSelection()
    {
        var folder = TestSetup.NewFolder("capture");
        var image = Path.Combine(folder, "capture.png");
        Sta(() =>
        {
            MakeImage(image, 600, 300, text: "Hello world");
            Prefs.SetFeatureEnabled(Feature.TextCapture, true);
            Prefs.SoundEffects = false;
            var capture = new TextCaptureWindow(image);
            string? clipboard = null;
            capture.SetClipboard = value => clipboard = value;
            var window = capture.Window;
            try
            {
                Assert.AreEqual(capture.CopyButton, System.Windows.Input.FocusManager.GetFocusedElement(window));
                Assert.IsTrue(capture.CopyButton.IsDefault);
                Assert.IsFalse(capture.CopyButton.IsEnabled, "Nothing to copy while recognizing.");
                System.Windows.Input.FocusManager.SetFocusedElement(window, capture.Text);
                capture.ShowRecognized("Hello world");
                Assert.AreEqual(capture.CopyButton, System.Windows.Input.FocusManager.GetFocusedElement(window), "Focus leaves the text once it's in.");
                Assert.IsTrue(capture.CopyButton.IsEnabled);
                Assert.IsFalse(capture.Text.IsReadOnly);
                Assert.AreEqual(Loc.L("Ready to copy"), capture.Status);
                capture.CopyText();
                Assert.AreEqual("Hello world", clipboard);
                Assert.AreEqual(Loc.L("Text copied"), capture.Status);
                capture.Text.Select(0, 5);
                capture.CopyText();
                Assert.AreEqual("Hello", clipboard);
                capture.ShowRecognized("");
                Assert.AreEqual(Loc.L("No text found in this image."), capture.Status);
                Assert.IsFalse(capture.CopyButton.IsEnabled);
                Prefs.LanguageId = "zh-Hans";
                var chinese = new TextCaptureWindow(image);
                StringAssert.StartsWith(chinese.Window.Title, "提取文字");
                chinese.Window.Close();
                Prefs.LanguageId = "en";
                window.Left = -32000;
                window.Top = -32000;
                window.ShowActivated = false;
                window.ShowInTaskbar = false;
                window.Show();
                if (TextRecognition.Engines().Count > 0)
                {
                    // The whole flow: preview and recognition fill in as they're ready.
                    capture.Start(image);
                    for (int i = 0; i < 40 && capture.Text.IsReadOnly; i++) Pump(100);
                    StringAssert.Contains(capture.Text.Text, "Hello", capture.Status);
                    Assert.AreEqual(Loc.L("Ready to copy"), capture.Status);
                }
                Pump(300);
                SaveRender(window, "text-capture");
            }
            finally { window.Close(); }
        });
    }

    // MARK: Image presets

    static ImagePreset Preset(int longEdge, ImagePreset.PresetFormat format, int quality, bool strip) =>
        new() { Id = "test", Name = "Test", LongEdge = longEdge, Format = format, Quality = quality, StripMetadata = strip };

    [TestMethod]
    public void PresetKeepsOriginalAndAvoidsCollisions()
    {
        RequireImages();
        var folder = TestSetup.NewFolder("preset");
        var source = Path.Combine(folder, "sample.jpg");
        Sta(() =>
        {
            MakeImage(source, author: "Swivel fixture", gps: true);
            var original = File.ReadAllBytes(source);
            var preset = Preset(200, ImagePreset.PresetFormat.Jpeg, 80, strip: true);
            var first = preset.Apply(source);
            var firstData = File.ReadAllBytes(first);
            var second = preset.Apply(source);
            Assert.AreNotEqual(first, second);
            Assert.AreEqual(folder, Path.GetDirectoryName(first));
            Assert.AreEqual(folder, Path.GetDirectoryName(second));
            Assert.AreEqual("sample processed.jpg", Path.GetFileName(first));
            Assert.AreEqual("sample processed 2.jpg", Path.GetFileName(second));
            CollectionAssert.AreEqual(original, File.ReadAllBytes(source));
            CollectionAssert.AreEqual(firstData, File.ReadAllBytes(first));
            var decoded = Decode(first);
            Assert.AreEqual(200, decoded.PixelWidth);
            Assert.AreEqual(100, decoded.PixelHeight);
            var metadata = decoded.Metadata as BitmapMetadata;
            Assert.IsTrue(metadata is null || metadata.Author is null || metadata.Author.Count == 0, "Author removed");
            Assert.IsFalse(metadata?.ContainsQuery("/app1/ifd/gps") == true, "GPS removed");
            Assert.HasCount(3, Directory.GetFiles(folder), "No temp files left behind.");
        });
    }

    [TestMethod]
    public void SmallImagesAreNotEnlargedAndMetadataCanBeKept()
    {
        RequireImages();
        var folder = TestSetup.NewFolder("preset");
        var source = Path.Combine(folder, "small.jpg");
        Sta(() =>
        {
            MakeImage(source, 120, 60, author: "Swivel fixture", gps: true);
            var png = Preset(1600, ImagePreset.PresetFormat.Png, 90, strip: false).Apply(source);
            var image = Decode(png);
            Assert.AreEqual(120, image.PixelWidth);
            Assert.AreEqual(60, image.PixelHeight);
            Assert.AreEqual("Swivel fixture", (image.Metadata as BitmapMetadata)?.Author?.FirstOrDefault(), "Author carried into the PNG");
            var jpg = Preset(1600, ImagePreset.PresetFormat.Jpeg, 90, strip: false).Apply(source);
            var kept = (BitmapMetadata)Decode(jpg).Metadata;
            Assert.AreEqual("Swivel fixture", kept.Author?.FirstOrDefault());
            Assert.IsTrue(kept.ContainsQuery("/app1/ifd/gps"), "GPS kept when metadata is kept");
        });
    }

    [TestMethod]
    public void PresetCorrectsOrientationAndRejectsInvalidInputWithoutOutput()
    {
        RequireImages();
        var folder = TestSetup.NewFolder("preset");
        var source = Path.Combine(folder, "rotated.jpg");
        Sta(() =>
        {
            MakeImage(source, 800, 400, orientation: 6);
            var result = Preset(200, ImagePreset.PresetFormat.Jpeg, 90, strip: false).Apply(source);
            var image = Decode(result);
            Assert.AreEqual(100, image.PixelWidth);
            Assert.AreEqual(200, image.PixelHeight);
            var orientation = (image.Metadata as BitmapMetadata)?.GetQuery("/app1/ifd/{ushort=274}");
            Assert.IsTrue(orientation is null || Convert.ToInt32(orientation) == 1, $"orientation {orientation}");
            var broken = Path.Combine(folder, "broken.png");
            File.WriteAllText(broken, "not an image");
            Assert.ThrowsExactly<ToolError>(() => Preset(200, ImagePreset.PresetFormat.Png, 90, false).Apply(broken));
            Assert.IsFalse(File.Exists(Path.Combine(folder, "broken processed.png")));
            Assert.ThrowsExactly<ToolError>(() => Preset(200, ImagePreset.PresetFormat.Png, 0, false).Apply(source), "Quality out of range");
            Assert.ThrowsExactly<ToolError>(() => Preset(10, ImagePreset.PresetFormat.Png, 90, false).Apply(source), "Size out of range");
            Assert.HasCount(3, Directory.GetFiles(folder));
        });
    }

    [TestMethod]
    public void JpegPresetsPutTransparencyOnWhiteAndFollowQuality()
    {
        RequireImages();
        var folder = TestSetup.NewFolder("preset");
        var clear = Path.Combine(folder, "clear.png");
        var noisy = Path.Combine(folder, "noisy.png");
        Sta(() =>
        {
            MakeImage(clear, 200, 200, transparent: true);
            MakeImage(noisy, 600, 400, noise: true);
            var jpg = Decode(Preset(0, ImagePreset.PresetFormat.Jpeg, 90, strip: true).Apply(clear));
            var pixels = new FormatConvertedBitmap(jpg, PixelFormats.Bgra32, null, 0);
            var corner = new byte[4];
            pixels.CopyPixels(new Int32Rect(2, 2, 1, 1), corner, 4, 0);
            Assert.IsTrue(corner[0] > 245 && corner[1] > 245 && corner[2] > 245, $"corner {string.Join(",", corner)}");
            var centre = new byte[4];
            pixels.CopyPixels(new Int32Rect(100, 100, 1, 1), centre, 4, 0);
            Assert.IsTrue(centre[2] > 200 && centre[1] < 60, "The red square stays red.");
            var png = Decode(Preset(0, ImagePreset.PresetFormat.Png, 90, strip: true).Apply(clear));
            Assert.IsTrue(png.Format == PixelFormats.Bgra32 || png.Format == PixelFormats.Pbgra32 || png.Format.Masks.Count == 4, "PNG keeps transparency");
            var low = new FileInfo(Preset(0, ImagePreset.PresetFormat.Jpeg, 10, strip: true).Apply(noisy)).Length;
            var high = new FileInfo(Preset(0, ImagePreset.PresetFormat.Jpeg, 95, strip: true).Apply(noisy)).Length;
            Assert.IsLessThan(high, low, $"q10 {low} B, q95 {high} B");
            var tiff = Preset(300, ImagePreset.PresetFormat.Tiff, 100, strip: true).Apply(noisy);
            Assert.AreEqual(".tiff", Path.GetExtension(tiff));
            Assert.AreEqual(300, Decode(tiff).PixelWidth);
        });
    }

    [TestMethod]
    public async Task PresetActionsReportFailuresAndRespectTheSwitch()
    {
        RequireImages();
        var folder = TestSetup.NewFolder("presetrun");
        var good = Path.Combine(folder, "good.png");
        var bad = Path.Combine(folder, "bad.png");
        Sta(() => MakeImage(good, 300, 150));
        File.WriteAllText(bad, "nope");
        var preset = Preset(100, ImagePreset.PresetFormat.Png, 90, strip: true);
        Prefs.SetFeatureEnabled(Feature.ImagePresets, false);
        await Assert.ThrowsAsync<ToolError>(() => PresetActions.Run(preset, [good]));
        Prefs.SetFeatureEnabled(Feature.ImagePresets, true);
        var reported = new List<string>();
        var before = PresetActions.ReportFailures;
        PresetActions.ReportFailures = (title, details) => reported.Add(title + "|" + details);
        var progress = new List<(int, int)>();
        try
        {
            var result = await PresetActions.Run(preset, [good, bad], (done, total) => progress.Add((done, total)));
            Assert.HasCount(1, result.Files);
            Assert.AreEqual("Saved 1 files; 1 could not be processed.", result.Message);
            Assert.HasCount(1, reported);
            StringAssert.Contains(reported[0], "bad.png");
            CollectionAssert.AreEqual(new[] { (0, 2), (1, 2) }, progress);
            var single = await PresetActions.Run(preset, [good]);
            Assert.AreEqual("Saved good processed 2.png", single.Message);
        }
        finally { PresetActions.ReportFailures = before; }
    }

    [TestMethod]
    public void PresetValidationAndStableIdentifiers()
    {
        var preset = ImagePreset.Starters[0];
        var id = preset.ActionId;
        preset = preset with { Name = "更改名字", NameKey = null };
        Assert.IsTrue(preset.IsValid);
        Assert.AreEqual(id, preset.ActionId);
        var json = System.Text.Json.JsonSerializer.Serialize(preset);
        Assert.AreEqual(preset, System.Text.Json.JsonSerializer.Deserialize<ImagePreset>(json));
        Assert.IsFalse((preset with { LongEdge = -1 }).IsValid);
        Assert.IsFalse((preset with { LongEdge = 63 }).IsValid);
        Assert.IsTrue((preset with { LongEdge = 0 }).IsValid);
        Assert.IsFalse((preset with { Name = " " }).IsValid);
        Assert.IsNull(ImagePreset.FormIssueFor("Web", "1600"));
        Assert.IsNull(ImagePreset.FormIssueFor("Web", " 0 "));
        Assert.AreEqual(ImagePreset.FormIssue.Name, ImagePreset.FormIssueFor("  ", "1600"));
        Assert.AreEqual(ImagePreset.FormIssue.Name, ImagePreset.FormIssueFor(new string('x', 61), "1600"));
        Assert.AreEqual(ImagePreset.FormIssue.Name, ImagePreset.FormIssueFor("", "abc"));
        Assert.AreEqual(ImagePreset.FormIssue.Size, ImagePreset.FormIssueFor("Web", "63"));
        Assert.AreEqual(ImagePreset.FormIssue.Size, ImagePreset.FormIssueFor("Web", "16385"));
        Assert.AreEqual(ImagePreset.FormIssue.Size, ImagePreset.FormIssueFor("Web", "1,600"));
        Assert.AreEqual(ImagePreset.FormIssue.Size, ImagePreset.FormIssueFor("Web", ""));
        Assert.IsTrue((ImagePreset.Starters[0] with { Name = "Web", LongEdge = ImagePreset.LongEdgeFrom(" 64 ") ?? -1 }).IsValid);
    }

    [TestMethod]
    public void ImageFilterListsTheKnownImageTypes()
    {
        var filter = ToolUI.ImageFilter;
        StringAssert.Contains(filter, "*.jpg");
        StringAssert.Contains(filter, "*.heic");
        Assert.AreEqual(4, filter.Split('|').Length, "Images|patterns|All files|*.*");
    }
}
