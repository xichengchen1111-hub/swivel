using System.Diagnostics;
using System.Numerics;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Swivel;

namespace Swivel.Tests;

/// The QR tools (1.4): the library store, the scan and library windows and the 3D scenes (ports of the QR checks in
/// FileActionsTests.swift). Checks marked "needs the codec" read or encode real QR codes; they report Inconclusive
/// while Qr/QrCodec.cs is still the placeholder.
[TestClass]
public sealed class QrToolsTests
{
    [TestInitialize]
    public void Reset()
    {
        TestSetup.ResetPrefs();
        Prefs.LanguageId = "en";
        Prefs.SoundEffects = false;
        Prefs.SetFeatureEnabled(Feature.QrCodes, true);
    }

    // MARK: Helpers

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

    /// Waits on the dispatcher (so awaits inside the windows continue) until the task is done.
    static void Wait(Task task, int seconds = 30)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted && watch.Elapsed.TotalSeconds < seconds) Pump(10);
        Assert.IsTrue(task.IsCompleted, "timed out");
        task.GetAwaiter().GetResult();
    }

    /// Whether the real QR codec has landed (the placeholder encodes nothing).
    static bool CodecReady
    {
        get
        {
            try
            {
                var m = QrCodec.Encode("codec check");
                for (int y = 0; y < m.Size; y++) for (int x = 0; x < m.Size; x++) if (m[x, y]) return true;
                return false;
            }
            catch (Exception) { return false; }
        }
    }

    static void RequireCodec()
    {
        if (!CodecReady) Assert.Inconclusive("Needs the real QR codec (Qr/QrCodec.cs is still the placeholder).");
    }

    static void WritePng(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    static BitmapSource Blank(int size)
    {
        var pixels = Enumerable.Repeat((byte)255, size * size).ToArray();
        return BitmapSource.Create(size, size, 96, 96, PixelFormats.Gray8, null, pixels, size);
    }

    /// The image on an opaque light background (the Mac test draws white 0.97 behind the snapshot).
    static BitmapSource OnBackground(BitmapSource image)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(247, 247, 247)), null, new Rect(0, 0, image.PixelWidth, image.PixelHeight));
            dc.DrawImage(image, new Rect(0, 0, image.PixelWidth, image.PixelHeight));
        }
        var bitmap = new RenderTargetBitmap(image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    static float Luminance(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;

    // MARK: Store

    [TestMethod]
    public void LibraryPersistsAcrossRestartsAndDeduplicatesWithoutNeedingOriginals()
    {
        var dir = TestSetup.NewFolder("qr14-store");
        var store = new QrLibraryStore(dir);
        var first = SavedQrCode.New("Website", "https://example.com", "Work", QrSeason.Autumn);
        Assert.IsEmpty(store.Load());
        var saved = store.Add(first);
        var same = store.Add(SavedQrCode.New("Duplicate", first.Value, "Work"));
        Assert.AreEqual(saved.Id, same.Id);
        var reloaded = new QrLibraryStore(dir).Load();
        CollectionAssert.AreEqual(new[] { first }, reloaded.ToArray());
        Assert.AreEqual(QrSeason.Autumn, reloaded[0].Season);
        // Another collection is another entry.
        store.Add(SavedQrCode.New("Personal copy", first.Value, "Personal"));
        Assert.HasCount(2, store.Load());
        Assert.IsFalse(Directory.EnumerateFiles(dir, "*.tmp").Any(), "No temp files left behind.");
    }

    [TestMethod]
    public void UndoPreservesOtherWindowsChangesAndRefusesConflictingEdits()
    {
        var store = new QrLibraryStore(TestSetup.NewFolder("qr14-undo"));
        var a = SavedQrCode.New("A", "alpha");
        var b = SavedQrCode.New("B", "beta");
        store.Save([a]);
        store.Save([]);              // A deleted in one window.
        store.Add(b);                // B added by a second window.
        store.Revert([a], []);
        CollectionAssert.AreEquivalent(new[] { a.Id, b.Id }, store.Load().Select(v => v.Id).ToArray());
        var changed = a with { Name = "Edited elsewhere" };
        store.Save([changed, b]);
        Assert.ThrowsExactly<ToolError>(() => store.Revert([], [a]));
        Assert.Contains(changed, store.Load());
    }

    [TestMethod]
    public void InvalidAndCorruptedDataIsNeverSilentlyOverwritten()
    {
        var store = new QrLibraryStore(TestSetup.NewFolder("qr14-corrupt"));
        var a = SavedQrCode.New("Keep", "https://example.com");
        store.Save([a]);
        var before = File.ReadAllBytes(store.FilePath);
        Assert.ThrowsExactly<ToolError>(() => store.Save([SavedQrCode.New("", "")]));
        Assert.ThrowsExactly<ToolError>(() => store.Save([a, a]), "Duplicate ids are refused.");
        CollectionAssert.AreEqual(before, File.ReadAllBytes(store.FilePath));
        File.WriteAllText(store.FilePath, "broken data");
        var error = Assert.ThrowsExactly<ToolError>(() => store.Add(a));
        Assert.AreEqual(Loc.L("The QR library could not be read. Its saved data was kept."), error.Message);
        Assert.AreEqual("broken data", File.ReadAllText(store.FilePath));
        File.WriteAllText(store.FilePath, """[{"id":"not a guid","name":"x","value":"y"}]""");
        Assert.ThrowsExactly<ToolError>(() => store.Load());
        File.WriteAllText(store.FilePath, """[{"id":"7C9E6679-7425-40DE-944B-E07FC1F90AE7","name":" ","value":"y"}]""");
        Assert.ThrowsExactly<ToolError>(() => store.Load(), "Invalid saved entries are an unreadable library.");
    }

    [TestMethod]
    public void LibrariesSavedBeforeTemplatesStillLoad()
    {
        var store = new QrLibraryStore(TestSetup.NewFolder("qr14-old"));
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        File.WriteAllText(store.FilePath,
            """[{"id":"7C9E6679-7425-40DE-944B-E07FC1F90AE7","name":"Old","value":"https://example.com","collection":"","season":"spring","created":700000000}]""");
        var values = store.Load();
        Assert.HasCount(1, values);
        Assert.AreEqual(QrTemplate.Tree, values[0].Template);
        Assert.AreEqual(QrSeason.Spring, values[0].Season);
        Assert.AreEqual(new DateTime(2023, 3, 8), values[0].Created.Date, "Mac dates (seconds since 2001) are read.");
        var island = values[0] with { Template = QrTemplate.Island };
        store.Save([island]);
        Assert.AreEqual(island, store.Load()[0]);
        // A later version's template, a missing season and collection: defaults.
        File.WriteAllText(store.FilePath, """[{"id":"7C9E6679-7425-40DE-944B-E07FC1F90AE7","name":"New","value":"v","template":"volcano"}]""");
        var future = store.Load()[0];
        Assert.AreEqual(QrTemplate.Tree, future.Template);
        Assert.AreEqual(QrSeason.Summer, future.Season);
        Assert.AreEqual("", future.Collection);
    }

    [TestMethod]
    public void QrImagesRefuseEmptyAndOversizedContentAndSeedsAreStable()
    {
        Assert.ThrowsExactly<ToolError>(() => QrImage.Png(""));
        Assert.ThrowsExactly<ToolError>(() => QrImage.Png(new string('a', 2001)));
        Assert.ThrowsExactly<ToolError>(() => QrImage.Raw(new string('资', 667)), "2,001 UTF-8 bytes.");
        Assert.AreEqual(QrGardenView.Seed("same"), QrGardenView.Seed("same"));
        Assert.AreNotEqual(QrGardenView.Seed("same"), QrGardenView.Seed("different"));
        Assert.AreEqual("a-b-c.png", QrImage.FileName("a/b:c"));
        Assert.IsFalse(SavedQrCode.New(new string('x', 121), "v").IsValid);
        Assert.IsTrue(SavedQrCode.New(new string('x', 120), "v").IsValid);
        Assert.IsFalse(SavedQrCode.New("n", "v", new string('c', 81)).IsValid);
    }

    [TestMethod]
    public void GeneratedLibraryImagesRoundTripUnicodeAndExportNeverOverwrites()
    {
        RequireCodec();
        var dir = TestSetup.NewFolder("qr14-png");
        var value = "https://example.com/资料?a=1&b=two#hello";
        var output = Path.Combine(dir, "code.png");
        QrImage.Export(value, output);
        var recognized = CodeRecognition.Recognize([output]).GetAwaiter().GetResult();
        CollectionAssert.AreEqual(new[] { value }, recognized.Select(c => c.Value).ToArray());
        var bytes = File.ReadAllBytes(output);
        Assert.ThrowsExactly<ToolError>(() => QrImage.Export("other", output));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(output));
        var image = QrImage.Bitmap(value);
        var matrix = QrImage.Raw(value);
        Assert.AreEqual((matrix.Size + 8) * 8, image.PixelWidth, "Eight pixels per module and a four-module quiet zone.");
    }

    // MARK: Recognition and the scan window

    [TestMethod]
    public void OnlyPlainWebLinksCanBeOpened()
    {
        foreach (var value in new[] { "javascript:alert(1)", "file:///C:/Windows/win.ini", "claude://code/new", "https://user:secret@example.com", "just text", "ftp://example.com" })
            Assert.IsNull(new DetectedCode("x.png", "QR", value).WebUrl, value);
        Assert.IsNotNull(new DetectedCode("x.png", "QR", "https://example.com/你好?q=a%26b").WebUrl);
        Assert.IsNotNull(new DetectedCode("x.png", "QR", "  http://example.com  ").WebUrl);
    }

    [TestMethod]
    public void BlankImagesHaveNoCodesAndBrokenImagesFail()
    {
        var dir = TestSetup.NewFolder("qr14-blank");
        var blank = Path.Combine(dir, "blank.png");
        Sta(() => WritePng(Blank(300), blank));
        Assert.IsEmpty(CodeRecognition.Recognize([blank]).GetAwaiter().GetResult());
        RequireCodec();
        var broken = Path.Combine(dir, "broken.png");
        File.WriteAllText(broken, "original");
        Assert.Throws<Exception>(() => CodeRecognition.Recognize([broken]).GetAwaiter().GetResult());
        Assert.AreEqual("original", File.ReadAllText(broken));
    }

    [TestMethod]
    public void ScanWindowReviewsBeforeCopyingAndConfirmsBeforeOpeningALink()
    {
        Sta(() =>
        {
            var dir = TestSetup.NewFolder("qr14-scan");
            var window = new QrCodesWindow { Store = new QrLibraryStore(Path.Combine(dir, "library")) };
            string? clipboard = null;
            var opened = new List<Uri>();
            bool answer = false;
            int asked = 0;
            window.SetClipboard = v => clipboard = v;
            window.ConfirmOpen = _ => { asked++; return answer; };
            window.OpenUrl = url => { opened.Add(url); return true; };
            try
            {
                Assert.IsFalse(window.CopyButton.IsEnabled);
                window.ShowCodes([new DetectedCode(@"C:\a\menu.png", "QR", "https://example.com/menu"), new DetectedCode(@"C:\a\note.png", "EAN-13", "4006381333931")]);
                Assert.HasCount(2, window.List.Items);
                Assert.AreEqual(0, window.List.SelectedIndex);
                Assert.AreEqual("https://example.com/menu", window.Content.Text);
                Assert.IsTrue(window.Content.IsReadOnly);
                Assert.AreEqual(Loc.L("%d codes found", 2), window.Status);
                Assert.IsTrue(window.OpenButton.IsEnabled);
                Assert.IsEmpty(opened, "Never opened automatically.");
                window.OpenLink();
                Assert.AreEqual(1, asked);
                Assert.IsEmpty(opened, "Declining opens nothing.");
                answer = true;
                window.OpenLink();
                Assert.HasCount(1, opened);
                Assert.AreEqual("https://example.com/menu", opened[0].AbsoluteUri);
                window.CopyCode();
                Assert.AreEqual("https://example.com/menu", clipboard);
                Assert.AreEqual(Loc.L("Copied"), window.Status);
                window.List.SelectedIndex = 1;
                Assert.IsFalse(window.OpenButton.IsEnabled, "A barcode number is not a link.");
                window.OpenLink();
                Assert.AreEqual(2, asked);
                Assert.IsTrue(window.SaveButton.IsEnabled);
                window.ShowCodes([]);
                Assert.AreEqual(Loc.L("No readable QR codes or barcodes found."), window.Status);
                Assert.IsFalse(window.CopyButton.IsEnabled);
            }
            finally { window.Window.Close(); }
        });
    }

    [TestMethod]
    public void ScanWindowListsEveryCodeInTheSelectedImages()
    {
        RequireCodec();
        Sta(() =>
        {
            var dir = TestSetup.NewFolder("qr14-scan-real");
            var link = Path.Combine(dir, "link.png");
            var text = Path.Combine(dir, "text.png");
            QrImage.Export("https://example.com/path", link);
            QrImage.Export("SWIVEL TEST", text);
            var library = Path.Combine(dir, "library");
            var window = new QrCodesWindow { Store = new QrLibraryStore(library) };
            try
            {
                window.Start([link, text]);
                Assert.IsTrue(window.IsBusy);
                Assert.AreEqual(Loc.L("Reading codes…"), window.Status);
                Wait(window.Loading);
                CollectionAssert.AreEqual(new[] { "https://example.com/path", "SWIVEL TEST" }, window.Codes.Select(c => c.Value).ToArray());
                Assert.AreEqual("QR", window.Codes[0].Kind);
                Assert.AreEqual(Loc.L("%d codes found", 2), window.Status);
                Assert.IsTrue(window.CopyButton.IsEnabled, "Controls come back after reading.");
                window.List.SelectedIndex = 1;
                Assert.IsNull(window.Codes[1].WebUrl);
                Assert.IsFalse(window.OpenButton.IsEnabled);
            }
            finally
            {
                window.Window.Close();
                QrLibraryWindow.CloseAll();
            }
        });
    }

    // MARK: The library window

    [TestMethod]
    public void EmptyLibraryShowsCreationActionsAndRevealsSavedScenes()
    {
        Sta(() =>
        {
            var store = new QrLibraryStore(TestSetup.NewFolder("qr14-empty"));
            var library = new QrLibraryWindow(store);
            try
            {
                Assert.AreEqual(Visibility.Collapsed, library.Garden.Visibility);
                Assert.AreEqual(Visibility.Visible, library.EmptyState.Visibility);
                Assert.HasCount(2, library.NewButtons);
                Assert.IsTrue(library.NewButtons.All(b => b.IsEnabled));
                Assert.AreEqual(Loc.L("Save your first QR code"), library.EmptyTitleText);
                Assert.AreEqual(Loc.L("%d saved codes · stored on this PC", 0), library.Status);
                Assert.IsFalse(library.UndoButton.IsEnabled);
                Assert.IsFalse(library.CopyButton.IsEnabled);
                // Another window saves: this one follows at once.
                store.Add(SavedQrCode.New("Saved", "https://example.com"));
                Assert.AreEqual(Visibility.Visible, library.Garden.Visibility);
                Assert.AreEqual("Saved", library.Selected?.Name);
                Assert.AreEqual("Saved", library.TitleText);
                store.Save([]);
                Assert.AreEqual(Visibility.Collapsed, library.Garden.Visibility);
                // A different library file is not this window's business.
                new QrLibraryStore(TestSetup.NewFolder("qr14-other")).Add(SavedQrCode.New("Other", "other"));
                Assert.IsNull(library.Selected);
            }
            finally { library.Window.Close(); }
        });
    }

    [TestMethod]
    public void LibrarySearchesGroupsSavesClearsTheSearchAndUndoesItsOwnChanges()
    {
        Sta(() =>
        {
            var store = new QrLibraryStore(TestSetup.NewFolder("qr14-search"));
            var work = SavedQrCode.New("Swivel", "https://example.com/swivel", "Work");
            var note = SavedQrCode.New("Notes", "Local test note", "", QrSeason.Spring);
            store.Save([work, note]);
            var library = new QrLibraryWindow(store, note.Id);
            string? copied = null;
            library.SetClipboardImage = _ => copied = "image";
            try
            {
                Assert.AreEqual(note.Id, library.Selected?.Id);
                // "Ungrouped" first, then Work: two headers and two codes.
                Assert.HasCount(4, library.List.Items);
                Assert.AreEqual(0, library.Season.SelectedIndex, "Spring");
                library.Search.Text = "EXAMPLE.com";
                Assert.AreEqual(work.Id, library.Selected?.Id, "Search matches content, ignoring case.");
                Assert.HasCount(2, library.List.Items);
                library.Search.Text = "nothing like it";
                Assert.IsNull(library.Selected);
                Assert.AreEqual(Loc.L("No matching codes"), library.EmptyTitleText);
                var added = SavedQrCode.New("Added", "https://example.com/added", "Work");
                library.SaveEntry(added);
                Assert.AreEqual("", library.Search.Text, "Saving clears the search so the code shows.");
                Assert.AreEqual(added.Id, library.Selected?.Id);
                Assert.HasCount(3, store.Load());
                library.Template.SelectedIndex = 2;
                Assert.AreEqual(QrTemplate.Island, store.Load().First(e => e.Id == added.Id).Template);
                Assert.AreEqual(QrTemplate.Island, library.Garden.Entry?.Template);
                library.CopyImage();
                if (CodecReady) Assert.AreEqual("image", copied);
                library.RemoveCode();
                Assert.HasCount(2, store.Load());
                Assert.IsTrue(library.UndoButton.IsEnabled);
                library.UndoChange();
                Assert.AreEqual(QrTemplate.Island, store.Load().First(e => e.Id == added.Id).Template, "Undo brings the removed code back.");
                library.UndoChange();
                library.UndoChange();
                Assert.IsFalse(store.Load().Any(e => e.Id == added.Id));
                Assert.IsFalse(library.UndoButton.IsEnabled);
                // Undo refuses to change what another window edited since.
                library.SaveEntry(work with { Name = "Renamed" });
                store.Save(store.Load().Select(e => e.Id == work.Id ? e with { Name = "Elsewhere" } : e).ToList());
                library.UndoChange();
                Assert.AreEqual(Loc.L("This code changed in another window. Refresh before undoing."), library.Status);
                Assert.AreEqual("Elsewhere", store.Load().First(e => e.Id == work.Id).Name);
            }
            finally { library.Window.Close(); }
        });
    }

    [TestMethod]
    public void ImportingAnImageWithoutCodesKeepsTheEmptyResultMessageAndNoUndoStep()
    {
        Sta(() =>
        {
            var dir = TestSetup.NewFolder("qr14-import-blank");
            var image = Path.Combine(dir, "blank.png");
            WritePng(Blank(300), image);
            var store = new QrLibraryStore(Path.Combine(dir, "library"));
            var library = new QrLibraryWindow(store);
            try
            {
                Wait(library.IngestImages([image]));
                Assert.IsFalse(library.IsBusy);
                Assert.IsEmpty(store.Load());
                Assert.IsFalse(File.Exists(store.FilePath));
                Assert.AreEqual(Loc.L("No readable QR codes or barcodes found."), library.Status);
                Assert.IsFalse(library.UndoButton.IsEnabled);
                Assert.IsTrue(library.NewButtons.All(b => b.IsEnabled), "Controls come back after the work.");
            }
            finally { library.Window.Close(); }
        });
    }

    [TestMethod]
    public void ImportingTheSameCodeTwiceAddsNothingAndClearsTheSearch()
    {
        RequireCodec();
        Sta(() =>
        {
            var dir = TestSetup.NewFolder("qr14-import");
            var image = Path.Combine(dir, "Menu card.png");
            QrImage.Export("https://example.com/menu", image);
            var store = new QrLibraryStore(Path.Combine(dir, "library"));
            var library = new QrLibraryWindow(store);
            try
            {
                library.Search.Text = "zzz";
                Wait(library.IngestImages([image]));
                Assert.AreEqual("", library.Search.Text);
                var saved = store.Load();
                Assert.HasCount(1, saved);
                Assert.AreEqual("Menu card", saved[0].Name);
                Assert.AreEqual(saved[0].Id, library.Selected?.Id);
                Assert.IsTrue(library.CanUndo);
                File.Delete(image);
                Assert.HasCount(1, store.Load(), "The original image isn't needed any more.");
                QrImage.Export("https://example.com/menu", image);
                Wait(library.IngestImages([image]));
                Assert.AreEqual(Loc.L("These codes are already in your library."), library.Status);
                Assert.HasCount(1, store.Load());
                library.UndoChange();
                Assert.IsEmpty(store.Load());
            }
            finally { library.Window.Close(); }
        });
    }

    [TestMethod]
    public void SettingsPaneFollowsTheSwitch()
    {
        Sta(() =>
        {
            var pane = new QrCodesPane();
            Assert.IsTrue(pane.Choose.IsEnabled);
            Assert.IsTrue(pane.Library.IsEnabled);
            Prefs.SetFeatureEnabled(Feature.QrCodes, false);
            pane.Refresh();
            Assert.IsFalse(pane.Choose.IsEnabled);
            Assert.IsFalse(pane.Library.IsEnabled);
            // Off: nothing opens.
            QrLibraryWindow.Show();
            QrCodesWindow.Present(["x.png"]);
            Assert.IsEmpty(Application.Current?.Windows.Cast<Window>().ToList() ?? []);
        });
    }

    // MARK: Scenes

    /// Seen from straight above, every template shows exactly the code: dark modules covered by dark surfaces, light
    /// modules by light ones, with clear contrast between them.
    [TestMethod]
    public void EveryTemplateTopSurfaceMatchesTheCode()
    {
        RequireCodec();
        var dark = QrModules.Matrix("https://example.com/swivel");
        Assert.HasCount(25, dark);
        Assert.IsTrue(dark[0][0] && dark[0][6] && !dark[1][1]);
        foreach (var template in QrTemplates.All)
            foreach (var season in QrSeasons.All)
            {
                var model = QrGardenModel.Build(dark, template, season, 42);
                float darkest = 0, lightest = 1;
                for (int r = 0; r < model.Size; r++)
                    for (int c = 0; c < model.Size; c++)
                    {
                        float l = Luminance(model.TopColor(r, c) ?? new Vector3(0.93f));
                        if (dark[r][c]) darkest = Math.Max(darkest, l); else lightest = Math.Min(lightest, l);
                    }
                Assert.IsGreaterThan(0.12f, lightest - darkest, $"{template} {season}: dark ≤ {darkest}, light ≥ {lightest}");
                Assert.IsNotEmpty(model.Structure, $"{template} {season} has something that fades away");
            }
    }

    /// How far a point is from the outline of a block, seen from above.
    static float EdgeDistance(QrVoxel v, float x, float z)
    {
        float dx = MathF.Abs(v.X - x) - v.Size / 2, dz = MathF.Abs(v.Z - z) - (v.Depth ?? v.Size) / 2;
        if (dx <= 0 && dz <= 0) return Math.Min(-dx, -dz);
        return MathF.Sqrt(MathF.Max(dx, 0) * MathF.Max(dx, 0) + MathF.Max(dz, 0) * MathF.Max(dz, 0));
    }

    /// The straight-down render shows each module's top face in its exact (quantized) colour, and nothing else.
    [TestMethod]
    public void TopViewRenderShowsEachModulesExactColour()
    {
        RequireCodec();
        Sta(() =>
        {
            var view = new QrGardenView();
            foreach (var template in QrTemplates.All)
                foreach (var season in QrSeasons.All)
                {
                    var entry = SavedQrCode.New("Swivel", "https://example.com/swivel", season: season, template: template);
                    view.Display(entry);
                    var model = view.Model!;
                    const int width = 620, height = 520;
                    var image = view.Snapshot(width, height, showsCode: true);
                    var pixels = new byte[width * height * 4];
                    image.CopyPixels(pixels, width * 4, 0);
                    float half = (model.Size - 1) / 2f;
                    int wrong = 0;
                    var details = new List<string>();
                    var surfaces = model.Ground.Concat(model.Cover).ToList();
                    for (int r = 0; r < model.Size; r++)
                        for (int c = 0; c < model.Size; c++)
                        {
                            // A point of the module at least 0.12 modules from any block's edge, so the smoothing of
                            // edges in the render can't blend two colours there.
                            float x = c - half, z = r - half;
                            var nearby = surfaces.Where(v => MathF.Abs(v.X - x) < v.Size / 2 + 0.5f && MathF.Abs(v.Z - z) < (v.Depth ?? v.Size) / 2 + 0.5f).ToList();
                            (float X, float Z)? point = null;
                            for (float dz = -0.35f; dz <= 0.36f && point is null; dz += 0.07f)
                                for (float dx = -0.35f; dx <= 0.36f && point is null; dx += 0.07f)
                                {
                                    float px = x + dx, pz = z + dz;
                                    if (nearby.All(v => EdgeDistance(v, px, pz) > 0.12f))
                                        point = (px, pz);
                                }
                            Assert.IsNotNull(point, $"{template} {season}: no clear point in r{r} c{c}");
                            var top = nearby.Where(v => MathF.Abs(v.X - point.Value.X) < v.Size / 2 && MathF.Abs(v.Z - point.Value.Z) < (v.Depth ?? v.Size) / 2)
                                .MaxBy(v => v.Y1);
                            var expected = QrGardenGeometry.Quantize(top.Color);
                            var at = view.Project(new Vector3(point.Value.X, 0, point.Value.Z), width, height, 1);
                            int i = ((int)at.Y * width + (int)at.X) * 4;
                            Color seen = Color.FromRgb(pixels[i + 2], pixels[i + 1], pixels[i]);
                            if (Math.Abs(seen.R - expected.R) > 3 || Math.Abs(seen.G - expected.G) > 3 || Math.Abs(seen.B - expected.B) > 3)
                            {
                                wrong++;
                                if (details.Count < 6) details.Add($"r{r} c{c} at {at.X:0.0},{at.Y:0.0}: {seen} vs {expected}");
                            }
                        }
                    Assert.AreEqual(0, wrong, $"{template} {season}: {wrong} modules show another colour from above: " + string.Join("; ", details));
                }
        });
    }

    [TestMethod]
    public void NearestBloomSearchMatchesTheLinearSearch()
    {
        var random = new Random(3);
        var points = Enumerable.Range(0, 400).Select(_ => ((float)(random.NextDouble() * 120 - 60), (float)(random.NextDouble() * 120 - 60))).ToList();
        points.Add(points[10]);  // an exact tie keeps the first
        var grid = new QrGardenBuilder.NearestPoint(points);
        for (int i = 0; i < 2000; i++)
        {
            float x = (float)(random.NextDouble() * 180 - 90), z = (float)(random.NextDouble() * 180 - 90);
            int linear = 0;
            float best = float.PositiveInfinity;
            for (int j = 0; j < points.Count; j++)
            {
                float e = MathF.Sqrt((points[j].Item1 - x) * (points[j].Item1 - x) + (points[j].Item2 - z) * (points[j].Item2 - z));
                if (e < best) { best = e; linear = j; }
            }
            Assert.AreEqual(linear, grid.Find(x, z), $"({x}, {z})");
        }
    }

    [TestMethod]
    public void SceneTransitionAndReducedMotionRules()
    {
        // Straight above: looking down, code rows running down the screen, columns to the right.
        var (right, up, forward) = QrGardenView.Orientation(1);
        Assert.IsLessThan(1e-5f, Vector3.Distance(right, Vector3.UnitX));
        Assert.IsLessThan(1e-5f, Vector3.Distance(up, -Vector3.UnitZ));
        Assert.IsLessThan(1e-5f, Vector3.Distance(forward, -Vector3.UnitY));
        // Three-quarter view: 32° down, from the +x +z corner.
        (_, _, forward) = QrGardenView.Orientation(0);
        Assert.AreEqual(-MathF.Sin(32 * MathF.PI / 180), forward.Y, 1e-5f);
        Assert.AreEqual(forward.X, forward.Z, 1e-5f);
        Assert.AreEqual(0, QrGardenView.Smoothstep(0.45f, 0.9f, 0.4f));
        Assert.AreEqual(1, QrGardenView.Smoothstep(0.45f, 0.9f, 0.95f));
        Sta(() =>
        {
            var view = new QrGardenView();
            bool? toggled = null;
            view.OnToggle = code => toggled = code;
            view.SetShowsCode(true);
            Assert.IsTrue(view.ShowsCode);
            Assert.IsTrue(toggled);
            // Off screen it settles at once; a code that can't be generated leaves the view as it was.
            var before = view.Entry;
            Assert.ThrowsExactly<ToolError>(() => view.Display(SavedQrCode.New("Too long", new string('a', 2001))));
            Assert.AreEqual(before, view.Entry);
        });
    }

    [TestMethod]
    public void SceneForTheLargestCodeBuildsQuickly()
    {
        RequireCodec();
        var random = new Random(7);
        var value = new string(Enumerable.Range(0, 2000).Select(_ => (char)random.Next(33, 127)).ToArray());
        Assert.AreEqual(2000, Encoding.UTF8.GetByteCount(value));
        Sta(() =>
        {
            var view = new QrGardenView();
            foreach (var template in QrTemplates.All)
            {
                var watch = Stopwatch.StartNew();
                view.Display(SavedQrCode.New("Long", value, template: template));
                var built = watch.ElapsedMilliseconds;
                var model = view.Model!;
                int voxels = model.Ground.Count + model.Cover.Count + model.Structure.Count;
                Console.WriteLine($"{template}: {model.Size} modules, {voxels} voxels, built in {built} ms");
                Assert.IsLessThan(4000, built, $"{template} took {built} ms");
            }
        });
    }

    static string? Folder(string variable) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } dir ? Directory.CreateDirectory(dir).FullName : null;

    /// Renders every template and season in both views (SWIVEL_FILE_ACTIONS_RENDERS=<dir>) and checks that the view
    /// from above scans.
    [TestMethod]
    public void RenderScenesAndCheckTheyScanFromAbove()
    {
        if (Folder("SWIVEL_FILE_ACTIONS_RENDERS") is not { } folder) return;
        var unreadable = new List<string>();
        Sta(() =>
        {
            var view = new QrGardenView();
            foreach (var template in QrTemplates.All)
                foreach (var season in QrSeasons.All)
                {
                    var entry = SavedQrCode.New("Swivel", "https://example.com/swivel", season: season, template: template);
                    view.Display(entry);
                    foreach (var showCode in new[] { false, true })
                    {
                        var picture = OnBackground(view.Snapshot(900, 760, showCode));
                        WritePng(picture, Path.Combine(folder, $"{template.Key()}-{season.Key()}-{(showCode ? "qr" : "scene")}.png"));
                        if (showCode && CodecReady && QrCodec.Read(picture).Select(c => c.Text).ToArray() is var read &&
                            !read.SequenceEqual([entry.Value]))
                            unreadable.Add($"{template} {season}");
                    }
                }
        });
        Assert.IsEmpty(unreadable, "Not scannable from above: " + string.Join(", ", unreadable));
    }

    /// Renders the transition (SWIVEL_QR_FILMSTRIP=<dir>) for a short and a long code and times it.
    [TestMethod]
    public void RenderTransitionFilmstripWhenRequested()
    {
        if (Folder("SWIVEL_QR_FILMSTRIP") is not { } folder) return;
        Sta(() =>
        {
            var view = new QrGardenView();
            foreach (var (name, value) in new[] { ("short", "https://example.com/swivel"), ("long", string.Concat(Enumerable.Repeat("https://example.com/a-long-path?", 30))) })
            {
                var watch = Stopwatch.StartNew();
                view.Display(SavedQrCode.New(name, value));
                Console.WriteLine($"{name}: build {watch.ElapsedMilliseconds} ms");
                watch.Restart();
                for (int i = 0; i <= 8; i++)
                    WritePng(OnBackground(view.Snapshot(600, 500, i / 8.0)), Path.Combine(folder, $"{name}-{i}.png"));
                Console.WriteLine($"{name}: 9 frames {watch.ElapsedMilliseconds} ms");
            }
        });
    }
}
