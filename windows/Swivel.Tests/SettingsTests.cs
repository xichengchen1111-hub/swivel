using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Swivel;

namespace Swivel.Tests;

/// The settings window: every page builds and draws, switches and pickers write Prefs (and follow Prefs written
/// elsewhere), Customize saves what it shows, the preset form explains what's wrong, and a language change relabels
/// the window. Plus the Mac app's SettingsFeelTests that apply: template cards behave like buttons and reorder drops
/// land in the right place. Windows are built off screen (never activated) and closed afterwards.
[TestClass]
public sealed class SettingsTests
{
    [TestInitialize]
    public void Reset()
    {
        TestSetup.ResetPrefs();
        Prefs.SoundEffects = false; // no sounds on the owner's desktop
        Prefs.LanguageId = "en";
    }

    // MARK: Helpers

    static SettingsWindow Open(AppTab tab = AppTab.Overview)
    {
        var window = new SettingsWindow(offscreen: true) { Width = 1100, Height = 780 };
        window.Show();
        window.Select(tab);
        Pump(0.1);
        return window;
    }

    /// Runs the dispatcher (layout, Prefs refreshes, spring frames) for a while.
    static void Pump(double seconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    /// Draws the window's content over its theme background and saves a PNG; returns the pixels.
    static byte[] Render(SettingsWindow window, string name)
    {
        var root = (FrameworkElement)window.Content;
        int w = (int)Math.Ceiling(root.ActualWidth), h = (int)Math.Ceiling(root.ActualHeight);
        Assert.IsTrue(w > 600 && h > 400, $"{name}: {w}×{h}");
        var content = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        content.Render(root);
        var background = root.TryFindResource("ApplicationBackgroundBrush") as Brush ?? Brushes.White;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(background, null, new Rect(0, 0, w, h));
            dc.DrawImage(content, new Rect(0, 0, w, h));
        }
        var final = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        final.Render(visual);
        var folder = Environment.GetEnvironmentVariable("SWIVEL_TEST_PNG_DIR") is { Length: > 0 } dir ? dir : TestSetup.NewFolder("settings-png");
        Directory.CreateDirectory(folder);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(final));
        using (var file = File.Create(Path.Combine(folder, name + ".png"))) encoder.Save(file);
        var pixels = new byte[w * h * 4];
        final.CopyPixels(pixels, w * 4, 0);
        return pixels;
    }

    /// Enough different colours that something (text, cards, icons) was drawn, not just a flat background.
    static void AssertDrawn(byte[] pixels, string name)
    {
        var colours = new HashSet<int>();
        for (int i = 0; i < pixels.Length; i += 4 * 7) colours.Add(BitConverter.ToInt32(pixels, i));
        Assert.IsGreaterThan(40, colours.Count, $"{name} looks empty ({colours.Count} colours)");
    }

    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }

    static T Current<T>(SettingsWindow window) where T : FrameworkElement => (T)window.View.CurrentPage!;

    static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    // MARK: Pages

    [STATestMethod]
    public void EveryPageBuildsAndDraws()
    {
        var window = Open();
        try
        {
            foreach (var tab in Enum.GetValues<AppTab>())
            {
                window.Select(tab);
                Pump(0.7); // entrance springs settle
                AssertDrawn(Render(window, $"tab-{(int)tab}-{tab}"), tab.ToString());
                Assert.StartsWith("Swivel — ", window.Title);
            }
            window.ThemeMode = ThemeMode.Dark;
            window.Select(AppTab.Overview);
            Pump(0.7);
            AssertDrawn(Render(window, "overview-dark"), "overview-dark");
            window.ThemeMode = ThemeMode.Light;
            window.Select(AppTab.Style);
            Pump(0.7);
            AssertDrawn(Render(window, "style-light"), "style-light");
        }
        finally { window.Close(); }
    }

    [STATestMethod]
    public void TabsOpenTheirPagesAndWheelSections()
    {
        var window = Open();
        try
        {
            Assert.AreEqual(SettingsView.Page.Overview, window.View.Current);
            window.Select(AppTab.Customize);
            Assert.AreEqual(SettingsView.Page.Wheel, window.View.Current);
            Assert.AreEqual(2, Current<WheelPage>(window).SelectedSection);
            window.Select(AppTab.Style);
            Assert.AreEqual(1, Current<WheelPage>(window).SelectedSection);
            window.Select(AppTab.Shortcuts);
            Assert.AreEqual(0, Current<WheelPage>(window).SelectedSection);
            window.Select(AppTab.QuickActions);
            Assert.AreEqual(SettingsView.Page.QuickActions, window.View.Current);
            window.Select(AppTab.General);
            Assert.AreEqual(SettingsView.Page.General, window.View.Current);
            Assert.AreEqual("Swivel — General", window.Title);
        }
        finally { window.Close(); }
    }

    // MARK: Switches

    [STATestMethod]
    public void FeatureSwitchWritesPrefsAndFollowsChangesFromElsewhere()
    {
        var window = Open(AppTab.Overview);
        try
        {
            var card = Current<OverviewPage>(window).Cards.Single(c => AutomationPropertiesName(c.Switch) == Feature.TextCapture.Title());
            Assert.IsFalse(card.Switch.IsOn);
            card.Switch.Flip();
            Assert.IsTrue(Feature.TextCapture.IsOn());
            Pump(0.3);
            Assert.IsTrue(window.View.FindSidebarDot(SettingsView.Page.TextCapture));

            // Turned off from the tray menu (or anywhere): the card, the page header and the dot follow.
            Prefs.SetFeatureEnabled(Feature.TextCapture, false);
            Pump(0.3);
            Assert.IsFalse(card.Switch.IsOn);
            Assert.IsFalse(window.View.FindSidebarDot(SettingsView.Page.TextCapture));

            // The page's own header switch, and the pane's button that depends on it.
            window.Select(AppTab.TextCapture);
            Pump(0.2);
            var page = Current<FeaturePage>(window);
            Assert.IsFalse(((TextCapturePane)page.Content).Choose.IsEnabled);
            page.Header.Switch.Flip();
            Pump(0.2);
            Assert.IsTrue(Prefs.FeatureEnabled(Feature.TextCapture));
            Assert.IsTrue(((TextCapturePane)page.Content).Choose.IsEnabled);

            // UI Automation's Toggle (screen readers) flips the feature too, not just the picture.
            var peer = (IToggleProvider)UIElementAutomationPeer.CreatePeerForElement(page.Header.Switch);
            peer.Toggle();
            Assert.IsFalse(Prefs.FeatureEnabled(Feature.TextCapture));
        }
        finally { window.Close(); }
    }

    static string AutomationPropertiesName(DependencyObject d) => System.Windows.Automation.AutomationProperties.GetName(d);

    [STATestMethod]
    public void ToggleSwitchDrawsAndKeepsItsSize()
    {
        var on = new ToggleSwitch { IsChecked = true };
        var off = new ToggleSwitch();
        on.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        off.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        // The On/Off text is reserved at its widest, so the switch never shifts when it flips.
        Assert.AreEqual(on.DesiredSize, off.DesiredSize);
        Assert.IsGreaterThan(40.0, on.DesiredSize.Width);
        int raised = 0;
        off.Toggled += _ => raised++;
        off.SetQuietly(true);
        Assert.AreEqual(0, raised);
        off.Flip();
        Assert.AreEqual(1, raised);
        Assert.IsFalse(off.IsOn);
    }

    // MARK: Shortcuts

    [STATestMethod]
    public void KeyPickerSwapsWithTheMenuThatHadTheKey()
    {
        var window = Open(AppTab.Shortcuts);
        try
        {
            var pane = (ShortcutsPane)Current<WheelPage>(window).Section(0);
            Assert.AreEqual(Array.IndexOf(TriggerKeys.All, TriggerKey.Shift), pane.Picker(WheelMode.Convert).SelectedIndex);
            pane.Picker(WheelMode.Convert).SelectedIndex = Array.IndexOf(TriggerKeys.All, TriggerKey.Control);
            Assert.AreEqual(TriggerKey.Control, Prefs.Key(WheelMode.Convert));
            Assert.AreEqual(TriggerKey.Shift, Prefs.Key(WheelMode.Tools));
            Pump(0.2);
            Assert.AreEqual(Array.IndexOf(TriggerKeys.All, TriggerKey.Shift), pane.Picker(WheelMode.Tools).SelectedIndex);
            Assert.AreEqual(Array.IndexOf(TriggerKeys.All, TriggerKey.Alt), pane.Picker(WheelMode.Actions).SelectedIndex);

            var reset = Descendants<Button>(pane).Single(b => b.Content as string == "Restore Defaults");
            Click(reset);
            Pump(0.2);
            Assert.AreEqual(TriggerKey.Shift, Prefs.Key(WheelMode.Convert));
            Assert.AreEqual(Array.IndexOf(TriggerKeys.All, TriggerKey.Control), pane.Picker(WheelMode.Tools).SelectedIndex);
        }
        finally { window.Close(); }
    }

    // MARK: Style

    [STATestMethod]
    public void TemplateCardIsOneButtonAroundALivePreview()
    {
        var card = new TemplateCard(MenuStyle.List);
        var picks = new List<MenuStyle>();
        card.OnPick = picks.Add;

        // Labels and margins belong to the card; the preview area belongs to the preview.
        Assert.IsFalse(card.IsInPreview(new Point(118, 230)));
        Assert.IsTrue(card.IsInPreview(new Point(118, 80)));

        card.PointerDown(new Point(118, 230));
        Assert.IsEmpty(picks); // nothing is chosen until the release
        card.PointerUp(new Point(118, 230));
        CollectionAssert.AreEqual(new[] { MenuStyle.List }, picks);

        // Dragging off before letting go cancels.
        card.PointerDown(new Point(118, 230));
        card.PointerMove(new Point(400, 20));
        card.PointerUp(new Point(400, 20));
        CollectionAssert.AreEqual(new[] { MenuStyle.List }, picks);

        card.SetSelected(true);
        Assert.IsTrue(card.IsSelected);
        var peer = UIElementAutomationPeer.CreatePeerForElement(card);
        Assert.AreEqual(AutomationControlType.RadioButton, peer.GetAutomationControlType());
        Assert.IsTrue(((ISelectionItemProvider)peer).IsSelected);
        Assert.AreEqual(MenuStyle.List.Title(), peer.GetName());
    }

    [STATestMethod]
    public void PickingATemplateSavesTheStyle()
    {
        var window = Open(AppTab.Style);
        try
        {
            var pane = (StylePane)Current<WheelPage>(window).Section(1);
            Assert.HasCount(5, pane.Cards);
            Assert.IsTrue(pane.Cards.Single(c => c.MenuStyle == MenuStyle.Wheel).IsSelected);
            var notes = pane.Cards.Single(c => c.MenuStyle == MenuStyle.Notes);
            notes.PointerDown(new Point(100, 230));
            notes.PointerUp(new Point(100, 230));
            Assert.AreEqual(MenuStyle.Notes, Prefs.Style);
            Pump(0.2);
            Assert.IsTrue(notes.IsSelected);
            Assert.IsFalse(pane.Cards.Single(c => c.MenuStyle == MenuStyle.Wheel).IsSelected);
        }
        finally { window.Close(); }
    }

    [TestMethod]
    public void ClickPressesOnDownAndCommitsOnlyOnReleaseInside()
    {
        var click = new ClickTracker();
        Assert.IsFalse(click.ShowsPressed);
        click.Down();
        Assert.IsTrue(click.ShowsPressed);
        click.Drag(inside: false); // dragged off: looks released, a release here cancels
        Assert.IsFalse(click.ShowsPressed);
        click.Drag(inside: true); // back on: pressed again
        Assert.IsTrue(click.ShowsPressed);
        Assert.IsTrue(click.Up(inside: true));
        Assert.IsFalse(click.ShowsPressed || click.IsDown);

        click.Down();
        Assert.IsFalse(click.Up(inside: false));
        // A release without a press (the press began elsewhere) never commits.
        Assert.IsFalse(click.Up(inside: true));
        click.Drag(inside: true);
        Assert.IsFalse(click.ShowsPressed);
    }

    // MARK: Customize

    [TestMethod]
    public void ReorderDestinationMatchesArrayMove()
    {
        // Dropping above row `proposed` moves the row to the same index a remove + insert would.
        for (int count = 1; count <= 6; count++)
            for (int from = 0; from < count; from++)
                for (int proposed = 0; proposed <= count; proposed++)
                {
                    var items = Enumerable.Range(0, count).ToList();
                    int moved = items[from];
                    items.RemoveAt(from);
                    int to = DropSlot.DestinationFor(from, proposed);
                    Assert.IsTrue(to >= 0 && to < count);
                    items.Insert(to, moved);
                    var above = Enumerable.Range(0, proposed).Where(i => i != from).ToList();
                    CollectionAssert.AreEqual(above, items.Take(above.Count).ToList(), $"from {from} above {proposed} of {count}");
                }
        // The gaps just above and just below the dragged row both mean "where it was".
        Assert.AreEqual(2, DropSlot.DestinationFor(2, 2));
        Assert.AreEqual(2, DropSlot.DestinationFor(2, 3));
    }

    [TestMethod]
    public void ReorderDetentFiresOncePerNewPlace()
    {
        var slot = new DropSlot();
        Assert.IsFalse(slot.Move(2)); // where the drag starts
        Assert.IsFalse(slot.Move(2));
        Assert.IsTrue(slot.Move(3));
        Assert.IsFalse(slot.Move(3));
        Assert.IsTrue(slot.Move(0));
        slot.Reset();
        Assert.IsFalse(slot.Move(4)); // a new drag starts fresh
    }

    [TestMethod]
    public void CustomizeSaveKeepsStoredEntriesForItemsWhoseToolIsOff()
    {
        // Stored while Image Presets and Clothesline were on: the preset led the list and was hidden, Hang followed Copy.
        var merged = Arrangement.Merged([("Move", true), ("Copy", false), ("Share", true)],
            ["preset.X", "Copy", "Hang", "Move", "Share"], new HashSet<string> { "preset.X", "Move" });
        CollectionAssert.AreEqual(new[] { "preset.X", "Move", "Copy", "Hang", "Share" }, merged.Order);
        // The rows decide for themselves (Move is shown again); the preset stays hidden.
        Assert.IsTrue(merged.Hidden.SetEquals(["preset.X", "Copy"]));

        // A run of absent titles keeps its order after the title it followed, wherever that title moved to.
        var run = Arrangement.Merged([("B", true), ("A", true)], ["A", "X", "Y", "B", "Z"], new HashSet<string> { "Y", "Z" });
        CollectionAssert.AreEqual(new[] { "B", "Z", "A", "X", "Y" }, run.Order);
        Assert.IsTrue(run.Hidden.SetEquals(["Y", "Z"]));

        // With everything present (or nothing stored) the rows are saved exactly as shown.
        (string, bool)[] rows = [("A", true), ("B", false)];
        CollectionAssert.AreEqual(new[] { "A", "B" }, Arrangement.Merged(rows, [], new HashSet<string>()).Order);
        var present = Arrangement.Merged(rows, ["B", "A"], new HashSet<string> { "A" });
        CollectionAssert.AreEqual(new[] { "A", "B" }, present.Order);
        Assert.IsTrue(present.Hidden.SetEquals(["B"]));
        // A title stored twice is put back once.
        CollectionAssert.AreEqual(new[] { "X", "A", "B" }, Arrangement.Merged(rows, ["X", "A", "X"], new HashSet<string>()).Order);
    }

    [STATestMethod]
    public void CustomizeHideAndReorderWriteTheArrangement()
    {
        var window = Open(AppTab.Customize);
        try
        {
            var pane = (CustomizePane)Current<WheelPage>(window).Section(2);
            pane.ModePicker.SelectedIndex = 1; // File tools
            pane.KindPicker.SelectedIndex = 0; // Images
            Pump(0.2);
            Assert.AreEqual(WheelMode.Tools, pane.Mode);
            Assert.AreEqual(FileKind.Image, pane.Kind);
            var titles = pane.Rows.Select(r => r.Title).ToList();
            CollectionAssert.AreEqual(Catalog.AllItems(WheelMode.Tools, FileKind.Image), titles);
            Assert.IsGreaterThan(3, titles.Count);
            Assert.HasCount(titles.Count, pane.List.RowViews);

            // Untick the first row: hidden, and the preview gets a new menu.
            var before = pane.Preview.Preview;
            var box = pane.List.RowViews[0].Box;
            box.IsChecked = false;
            Click(box);
            Assert.Contains(titles[0], Prefs.Hidden(WheelMode.Tools, FileKind.Image));
            Assert.AreNotSame(before, pane.Preview.Preview);

            // Drag the second row down past two others.
            double start = ArrangeList.Pitch * 1 + 10;
            pane.List.BeginDrag(1, start);
            Assert.IsTrue(pane.List.IsDragging);
            pane.List.DragTo(start + ArrangeList.Pitch * 1.2);
            pane.List.DragTo(start + ArrangeList.Pitch * 2.1);
            pane.List.EndDrag();
            Assert.IsFalse(pane.List.IsDragging);
            var expected = titles.ToList();
            var moved = expected[1];
            expected.RemoveAt(1);
            expected.Insert(3, moved);
            CollectionAssert.AreEqual(expected, Prefs.Order(WheelMode.Tools, FileKind.Image));
            CollectionAssert.AreEqual(expected, pane.Rows.Select(r => r.Title).ToList());
            Pump(0.6);
            // The rows settle in their new slots.
            for (int i = 0; i < pane.List.RowViews.Count; i++)
                Assert.AreEqual(i * ArrangeList.Pitch, Canvas.GetTop(pane.List.RowViews[i]), 0.5);

            // Keyboard: Alt+Down on a row is the same move.
            pane.List.MoveRow(0, 1);
            Assert.AreEqual(expected[0], Prefs.Order(WheelMode.Tools, FileKind.Image)[1]);

            // Restore Defaults clears both, and the rows follow.
            var reset = Descendants<Button>(pane).Single(b => b.Content as string == "Restore Defaults");
            Click(reset);
            Pump(0.3);
            Assert.IsEmpty(Prefs.Order(WheelMode.Tools, FileKind.Image));
            Assert.IsEmpty(Prefs.Hidden(WheelMode.Tools, FileKind.Image));
            CollectionAssert.AreEqual(titles, pane.Rows.Select(r => r.Title).ToList());
            Assert.IsTrue(pane.List.RowViews.All(r => r.Box.IsChecked == true));
        }
        finally { window.Close(); }
    }

    // MARK: Image presets

    [STATestMethod]
    public void PresetFormPointsAtWhatIsWrong()
    {
        var saved = new List<ImagePreset>();
        var form = new PresetForm(null, saved.Add);
        Assert.AreEqual("My Preset", form.NameBox.Text);
        Assert.AreEqual("1600", form.EdgeBox.Text);

        form.NameBox.Text = "   ";
        Assert.IsFalse(form.TrySave());
        Assert.AreEqual("Give the preset a name of up to 60 characters.", form.ErrorText);
        // Once showing, the message follows the typing and clears when the field is fixed.
        form.NameBox.Text = "Thumbs";
        Assert.AreEqual("", form.ErrorText);

        form.EdgeBox.Text = "12";
        Assert.IsFalse(form.TrySave());
        Assert.AreEqual("Enter 0, or a size from 64 to 16384 px.", form.ErrorText);
        form.EdgeBox.Text = "abc";
        Assert.AreEqual("Enter 0, or a size from 64 to 16384 px.", form.ErrorText);
        form.NameBox.Text = new string('x', 61);
        Assert.AreEqual("Give the preset a name of up to 60 characters.", form.ErrorText);
        form.NameBox.Text = " Thumbs ";
        form.EdgeBox.Text = " 0 ";
        form.FormatBox.SelectedIndex = 1; // PNG: quality doesn't apply
        Assert.IsFalse(form.QualitySlider.IsEnabled);
        Assert.IsTrue(form.TrySave());
        Assert.HasCount(1, saved);
        Assert.AreEqual("Thumbs", saved[0].Name);
        Assert.IsNull(saved[0].NameKey);
        Assert.AreEqual(0, saved[0].LongEdge);
        Assert.AreEqual(ImagePreset.PresetFormat.Png, saved[0].Format);
        Assert.IsTrue(saved[0].IsValid);
        form.Close();

        // Editing a built-in preset without renaming it keeps its translated name.
        var web = ImagePreset.Starters[0];
        var edit = new PresetForm(web, saved.Add);
        edit.EdgeBox.Text = "2048";
        Assert.IsTrue(edit.TrySave());
        Assert.AreEqual("Web JPG", saved[1].NameKey);
        Assert.AreEqual(web.Id, saved[1].Id);
        Assert.AreEqual(2048, saved[1].LongEdge);
        edit.Close();

        Assert.AreEqual(50, PresetForm.QualitySnap(50.6));
        Assert.AreEqual(51, PresetForm.QualitySnap(51));
        Assert.AreEqual(9, PresetForm.QualitySnap(9));
        Assert.AreEqual(100, PresetForm.QualitySnap(99.4));
    }

    [STATestMethod]
    public void PresetListAddsRemovesAndUndoes()
    {
        Prefs.SetFeatureEnabled(Feature.ImagePresets, true);
        var window = Open(AppTab.ImagePresets);
        try
        {
            var pane = (ImagePresetsPane)Current<FeaturePage>(window).Content;
            Assert.HasCount(3, pane.Presets);
            Assert.IsTrue(pane.ApplyButton.IsEnabled);
            var added = new ImagePreset { Id = "test", Name = "Tiny", LongEdge = 64, Format = ImagePreset.PresetFormat.Png, Quality = 100 };
            pane.Saved(added);
            Assert.HasCount(4, Prefs.ImagePresets);
            Assert.AreEqual("test", Prefs.ImagePresets[3].Id);

            pane.Table.SelectedIndex = 0;
            pane.RemovePreset();
            Assert.HasCount(3, Prefs.ImagePresets);
            Assert.IsFalse(Prefs.ImagePresets.Any(p => p.Id == "web"));
            Pump(0.5);
            Assert.IsTrue(ApplicationCommands.Undo.CanExecute(null, pane));
            ApplicationCommands.Undo.Execute(null, pane);
            Assert.HasCount(4, Prefs.ImagePresets);
            Assert.AreEqual("web", Prefs.ImagePresets[0].Id);
            Pump(0.5);
            Assert.HasCount(4, pane.Table.Items);

            // Off: the presets stay, but can't be applied.
            Prefs.SetFeatureEnabled(Feature.ImagePresets, false);
            Pump(0.2);
            Assert.IsFalse(pane.ApplyButton.IsEnabled);
            Assert.HasCount(4, Prefs.ImagePresets);
        }
        finally { window.Close(); }
    }

    // MARK: Quick Actions, General

    [STATestMethod]
    public void QuickActionsShortcutPickerWritesPrefs()
    {
        var window = Open(AppTab.QuickActions);
        try
        {
            var pane = (QuickActionsPane)Current<FeaturePage>(window).Content;
            Assert.AreEqual("Turn this feature on above to get started.", pane.Availability.Text);
            pane.Shortcuts.SelectedIndex = Array.IndexOf(QuickShortcuts.All, QuickShortcut.K);
            Assert.AreEqual(QuickShortcut.K, Prefs.QuickShortcut);
        }
        finally { window.Close(); }
    }

    [STATestMethod]
    public void LanguageSwitchRelabelsTheWindow()
    {
        var window = Open(AppTab.General);
        try
        {
            Assert.AreEqual("General", window.View.SidebarLabel(SettingsView.Page.General));
            var page = Current<GeneralPage>(window);
            page.LanguagePicker.SelectedIndex = Array.IndexOf(Languages.All, Language.ZhHans);
            Assert.AreEqual("zh-Hans", Prefs.LanguageId);
            Pump(0.3);
            var chinese = Strings.Table["General"][0];
            Assert.AreEqual(chinese, window.View.SidebarLabel(SettingsView.Page.General));
            Assert.AreEqual(SettingsView.Page.General, window.View.Current);
            Assert.AreEqual(Loc.L("Swivel — %@", chinese), window.Title);
            AssertDrawn(Render(window, "general-zh-Hans"), "general-zh-Hans");
            Assert.AreEqual(Array.IndexOf(Languages.All, Language.ZhHans), Current<GeneralPage>(window).LanguagePicker.SelectedIndex);
        }
        finally
        {
            Prefs.LanguageId = "en";
            window.Close();
        }
    }

    [STATestMethod]
    public void SoundEffectsSwitchWritesPrefs()
    {
        var window = Open(AppTab.General);
        try
        {
            var page = Current<GeneralPage>(window);
            Assert.IsFalse(page.Sounds.IsOn);
            Prefs.SoundEffects = true;
            Pump(0.2);
            Assert.IsTrue(page.Sounds.IsOn);
            page.Sounds.Flip();
            Assert.IsFalse(Prefs.SoundEffects);
        }
        finally { window.Close(); }
    }
}
