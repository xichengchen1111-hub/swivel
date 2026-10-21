using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Swivel;

/// One row of the Quick Actions panel. `Id` is stable ("tools.Crop", "settings"), titles are translated.
sealed record QuickCommand(string Id, string Title, string Detail, string Glyph, Func<Task<ActionResult?>> Run);

/// Find an action from the keyboard (QuickActions.swift): a global shortcut opens a search panel listing the enabled
/// tools; Return runs the chosen one on the files chosen in (or dropped on) the panel.
sealed class QuickActions
{
    public static QuickActions Shared { get; } = new();

    /// Whether the chosen shortcut could be registered (false: another app holds it). Settings shows a warning then.
    public bool ShortcutAvailable { get; private set; } = true;

    /// Raised after the shortcut is registered again (on the UI thread), so Settings can refresh its warning.
    public event Action? ShortcutChanged;

    /// The shortcut that is registered now (or was tried last), null while the feature is off.
    public QuickShortcut? RegisteredShortcut => registeredShortcut;

    HotKey? hotKey;
    QuickShortcut? registeredShortcut;
    ToolPanel? window;
    TextBox search = null!;
    TextBlock placeholder = null!;
    StackPanel list = null!;
    ScrollViewer scroll = null!;
    TextBlock filesLabel = null!, hint = null!;
    Button runButton = null!, chooseButton = null!, clearButton = null!;
    FrameworkElement content = null!;
    DropRing ring = null!;
    List<string> chosenFiles = [];
    List<QuickCommand> commands = [], allCommands = [];
    readonly List<QuickRow> rows = [];
    int selected = -1;
    /// Changes on every show and hide, so a pending fade or confirm blink can't act on a newer panel.
    int generation;
    /// The chosen row is blinking before the panel closes; a second Return must not run it twice.
    bool confirming;
    bool placed;
    int runs;
    /// The run whose busy toast ("Title…") is up, so only that run takes it down.
    int? busyToastRun;
    Point? lastMouse;

    public bool IsVisible => window?.IsVisible == true;

    /// Registers or unregisters the global shortcut to match the settings.
    public void SyncSettings()
    {
        if (!Feature.QuickActions.IsOn())
        {
            hotKey?.Unregister();
            hotKey = null;
            registeredShortcut = null;
            Hide();
        }
        else if (registeredShortcut != Prefs.QuickShortcut)
        {
            hotKey?.Unregister();
            var key = Prefs.QuickShortcut;
            hotKey = new HotKey(key.Win32(), Toggle);
            registeredShortcut = key;
            ShortcutAvailable = hotKey.IsRegistered;
            ShortcutChanged?.Invoke();
        }
        if (window?.IsVisible == true && !confirming) Rebuild();
    }

    /// Appears over the app the user is in and takes typing at once. `files` replaces the chosen files.
    public void Show(IReadOnlyList<string>? files = null)
    {
        if (!Feature.QuickActions.IsOn()) return;
        window ??= MakePanel();
        generation++;
        confirming = false;
        if (files != null) chosenFiles = [.. files];
        search.Text = "";
        Rebuild();
        if (!window.IsVisible)
        {
            Place(window);
            // Used many times a day from the keyboard: a fade of a few frames, no movement or scale (so nothing to
            // drop with animations off).
            window.Present(focus: true);
            ToolUI.FadeIn(content, from: 0, duration: 0.08);
        }
        else
        {
            window.Present(focus: true);
        }
        FocusSearch();
        UpdateRowEmphasis();
    }

    /// Closes at once: exits are faster than entrances.
    public void Hide()
    {
        generation++;
        confirming = false;
        if (window is { IsVisible: true }) window.Dismiss();
        if (window != null) content.Opacity = 1;
    }

    /// The shortcut closes the panel when it has focus, and brings it back into focus when the user had clicked away.
    void Toggle()
    {
        if (window is not { IsVisible: true })
        {
            Show();
            return;
        }
        if (window.IsActive) Hide();
        else
        {
            window.Present(focus: true);
            FocusSearch();
        }
    }

    void FocusSearch()
    {
        search.Focus();
        Keyboard.Focus(search);
    }

    /// Centres the panel a little above the middle of the screen with the pointer, as Windows Search does. On that
    /// same screen it then stays wherever the user moves it.
    void Place(ToolPanel panel)
    {
        if (ToolPanel.OffscreenForTests) return;
        var cursor = Native.CursorPixels();
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(cursor.X, cursor.Y));
        var hwnd = new System.Windows.Interop.WindowInteropHelper(panel).EnsureHandle();
        if (placed && ToolNative.GetWindowRect(hwnd, out var current))
        {
            var centre = new System.Drawing.Point((current.Left + current.Right) / 2, (current.Top + current.Bottom) / 2);
            if (screen.Bounds.Contains(centre)) return;
        }
        placed = true;
        var area = screen.WorkingArea;
        // The size in this screen's pixels: WPF sizes are 96-dpi units.
        double scale = DpiOf(screen) / 96.0;
        int width = (int)Math.Min(panel.Width * scale, area.Width - 40 * scale);
        int height = (int)Math.Min(panel.Height * scale, area.Height - 40 * scale);
        int x = area.Left + (area.Width - width) / 2;
        int y = (int)Math.Max(area.Top + 20 * scale, area.Top + area.Height * 0.2);
        ToolNative.SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, ToolNative.SWP_NOZORDER | ToolNative.SWP_NOACTIVATE);
    }

    static double DpiOf(System.Windows.Forms.Screen screen)
    {
        try
        {
            var monitor = ToolNative.MonitorFromPoint(new Native.POINT { X = screen.Bounds.Left + 1, Y = screen.Bounds.Top + 1 }, 2);
            if (ToolNative.GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0) return dpi;
        }
        catch (Exception) { }
        return 96;
    }

    ToolPanel MakePanel()
    {
        var panel = new ToolPanel
        {
            Title = L("Quick Actions"),
            Width = 620, Height = 500, MinWidth = 540, MinHeight = 380,
            AllowDrop = true,
        };

        search = new TextBox { FontSize = 16, Padding = new Thickness(34, 8, 8, 8), VerticalContentAlignment = VerticalAlignment.Center };
        ToolUI.AutomationName(search, L("Search actions…"));
        placeholder = new TextBlock
        {
            Text = L("Search actions…"), FontSize = 16, Opacity = 0.5, IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(36, 0, 0, 0),
        };
        var magnifier = ToolUI.Glyph("", 15);
        magnifier.IsHitTestVisible = false;
        magnifier.Opacity = 0.6;
        magnifier.HorizontalAlignment = HorizontalAlignment.Left;
        magnifier.Margin = new Thickness(12, 0, 0, 0);
        var searchBox = new Grid();
        searchBox.Children.Add(search);
        searchBox.Children.Add(magnifier);
        searchBox.Children.Add(placeholder);
        search.TextChanged += (_, _) =>
        {
            placeholder.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (!confirming) Filter();
        };
        search.PreviewKeyDown += SearchKey;

        filesLabel = ToolUI.Note("");
        filesLabel.TextWrapping = TextWrapping.NoWrap;
        filesLabel.TextTrimming = TextTrimming.CharacterEllipsis;
        filesLabel.VerticalAlignment = VerticalAlignment.Center;
        chooseButton = ToolUI.Button(L("Choose Files…"), ChooseFiles);
        clearButton = ToolUI.Button(L("Clear"), () => { chosenFiles = []; Rebuild(); FocusSearch(); });
        var fileRow = ToolUI.Bar([filesLabel], [clearButton, chooseButton]);

        list = new StackPanel();
        scroll = new ScrollViewer
        {
            Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false, MinHeight = 200,
        };
        // Moving the pointer over a row selects it; Return then runs what's under the pointer.
        list.MouseMove += (_, e) =>
        {
            var position = e.GetPosition(list);
            if (lastMouse == position) return;
            lastMouse = position;
            if (confirming) return;
            if (RowIndex(e.OriginalSource) is int row && row != selected) Select(row, scrollIntoView: false);
        };
        list.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (RowIndex(e.OriginalSource) is not int row || confirming) return;
            Select(row, scrollIntoView: false);
            // Only a double-click on a row runs it; one on the empty space below the rows does nothing.
            if (e.ClickCount == 2) RunSelected();
            e.Handled = true;
            FocusSearch();
        };

        runButton = ToolUI.Button(L("Run"), RunSelected);
        hint = ToolUI.Note(L("↑ ↓ to select · Return to run · Esc to close"));
        hint.VerticalAlignment = VerticalAlignment.Center;
        var footer = ToolUI.Bar([hint], [runButton]);
        var separator = new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)) };

        var root = new DockPanel { Margin = new Thickness(20) };
        foreach (var (view, dock) in new (FrameworkElement, Dock)[] { (searchBox, Dock.Top), (fileRow, Dock.Top), (separator, Dock.Top), (footer, Dock.Bottom) })
        {
            DockPanel.SetDock(view, dock);
            view.Margin = dock == Dock.Top ? new Thickness(0, 0, 0, 12) : new Thickness(0, 12, 0, 0);
            root.Children.Add(view);
        }
        root.Children.Add(scroll);
        ring = new DropRing();
        var layers = new Grid { Background = Brushes.Transparent };
        layers.Children.Add(root);
        layers.Children.Add(ring);
        content = layers;
        panel.Content = layers;

        panel.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Hide(); }
        };
        panel.Activated += (_, _) => UpdateRowEmphasis();
        panel.Deactivated += (_, _) =>
        {
            // A chosen command moved focus on (its Open dialog, a message box or window): the panel's job is done,
            // so don't leave it sitting behind that until the blink ends.
            if (confirming) { Hide(); return; }
            UpdateRowEmphasis();
        };
        panel.DragEnter += (_, e) => DragOver(e, entered: true);
        panel.DragOver += (_, e) => DragOver(e, entered: false);
        panel.DragLeave += (_, _) => DropTargeted(false);
        panel.Drop += (_, e) =>
        {
            DropTargeted(false);
            var paths = ToolUI.DroppedFiles(e.Data);
            if (!Feature.QuickActions.IsOn() || paths.Count == 0) { e.Effects = DragDropEffects.None; return; }
            e.Effects = ToolUI.DropEffect(e.AllowedEffects);
            e.Handled = true;
            chosenFiles = paths;
            Rebuild();
        };
        return panel;
    }

    int? RowIndex(object source)
    {
        for (var element = source as DependencyObject; element != null && element != list; element = VisualTreeHelper.GetParent(element))
            if (element is QuickRow row) return rows.IndexOf(row) is var i and >= 0 ? i : null;
        return null;
    }

    // MARK: Commands

    /// Does not read another app's selection; files arrive through a picker or an explicit drop.
    public static List<QuickCommand> MakeCommands(IReadOnlyList<string> files)
    {
        var list = new List<QuickCommand>
        {
            new("settings", L("Open Swivel…"), L("Settings"), "", () =>
            {
                AppWindow.Shared.Show();
                return Task.FromResult<ActionResult?>(null);
            }),
        };
        if (Feature.Shelf.IsOn())
            list.Add(new("shelf", L("Show Shelf"), Feature.Shelf.Title(), Feature.Shelf.Glyph(), () =>
            {
                Shelf.Shared.Show();
                return Task.FromResult<ActionResult?>(null);
            }));
        if (Feature.Clothesline.IsOn())
            list.Add(new("rope", L("Show Clothesline"), Feature.Clothesline.Title(), Feature.Clothesline.Glyph(), () =>
            {
                // The Clothesline module offers Toggle; the panel is closed, so this lowers the rope.
                Clothesline.Shared.Reveal();
                return Task.FromResult<ActionResult?>(null);
            }));
        if (files.Count == 0)
        {
            if (Feature.TextCapture.IsOn())
                list.Add(new("chooseOCR", L("Extract Text"), L("Choose an image…"), Feature.TextCapture.Glyph(), () =>
                {
                    if (ToolUI.ChooseFiles(imagesOnly: true, multiple: false).FirstOrDefault() is { } file) TextCaptureWindow.Present(file);
                    return Task.FromResult<ActionResult?>(null);
                }));
            return list;
        }
        foreach (var mode in WheelModes.All)
        {
            var items = Feature.Wheel.IsOn() ? Catalog.Items(files, mode) : Catalog.StandaloneItems(files, mode);
            list.AddRange(items.Select(item => new QuickCommand($"{mode.Id()}.{item.Title}", item.DisplayTitle, mode.Title(), mode.Glyph(),
                async () => (ActionResult?)await item.Run(files))));
        }
        return list;
    }

    static readonly CompareInfo Compare = CultureInfo.CurrentCulture.CompareInfo;
    const CompareOptions Loose = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreWidth | CompareOptions.IgnoreKanaType;

    /// Like the Mac's localizedStandardContains: case-, accent- and width-insensitive.
    static bool Has(string text, string term) => Compare.IndexOf(text, term, Loose) >= 0;

    /// Every search word must appear in the title, the detail (menu name) or the id. Matches keep their order, but
    /// rows whose title starts with the search come first, then titles containing every word, then the rest
    /// (matched by menu name or id only).
    public static List<QuickCommand> Matching(IReadOnlyList<QuickCommand> commands, string query)
    {
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0) return [.. commands];
        var phrase = string.Join(" ", terms);
        return commands
            .Select((command, order) => (command, order))
            .Where(x => terms.All(t => Has(x.command.Title, t) || Has(x.command.Detail, t) || Has(x.command.Id, t)))
            .OrderBy(x => Compare.IsPrefix(x.command.Title, phrase, Loose) ? 0
                        : WordStarts(x.command.Title, terms) ? 1
                        : terms.All(t => Has(x.command.Title, t)) ? 2 : 3)
            .ThenBy(x => x.order)
            .Select(x => x.command)
            .ToList();
    }

    /// Every term starts a word of the title ("pdf" in "Make PDF").
    static bool WordStarts(string title, string[] terms)
    {
        var words = title.Split([' ', '-', '_', '.', '…'], StringSplitOptions.RemoveEmptyEntries);
        return terms.All(t => words.Any(w => Compare.IsPrefix(w, t, Loose)));
    }

    void Rebuild()
    {
        filesLabel.Text = FilesDescription;
        filesLabel.ToolTip = chosenFiles.Count == 0 ? null : string.Join("\n", chosenFiles);
        clearButton.IsEnabled = chosenFiles.Count > 0;
        allCommands = MakeCommands(chosenFiles);
        Filter();
    }

    string FilesDescription => chosenFiles.Count == 0 ? L("Choose files or drop them here")
        : chosenFiles.Count == 1 ? System.IO.Path.GetFileName(chosenFiles[0]) : L("%d files", chosenFiles.Count);

    void Filter()
    {
        commands = Matching(allCommands, search.Text);
        list.Children.Clear();
        rows.Clear();
        foreach (var command in commands)
        {
            var row = new QuickRow(command);
            rows.Add(row);
            list.Children.Add(row);
        }
        selected = -1;
        if (commands.Count > 0) Select(0, scrollIntoView: true);
        runButton.IsEnabled = commands.Count > 0;
        hint.Text = commands.Count == 0 ? L("No matching actions. Try another search.") : L("↑ ↓ to select · Return to run · Esc to close");
    }

    void Select(int row, bool scrollIntoView)
    {
        if (selected >= 0 && selected < rows.Count) rows[selected].IsSelected = false;
        selected = row;
        if (row < 0 || row >= rows.Count) return;
        rows[row].IsSelected = true;
        rows[row].Active = window?.IsActive == true;
        if (scrollIntoView) rows[row].BringIntoView();
    }

    /// The selection shows in the accent colour while the panel has focus, even though typing goes to the search
    /// field, so it's always clear what Return will run.
    void UpdateRowEmphasis()
    {
        bool active = window?.IsActive == true;
        foreach (var row in rows) row.Active = active;
    }

    void SearchKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down or Key.Up:
                e.Handled = true;
                if (commands.Count == 0 || confirming) return;
                int delta = e.Key == Key.Down ? 1 : -1;
                Select(Math.Clamp(selected + delta, 0, commands.Count - 1), scrollIntoView: true);
                break;
            case Key.Return:
                e.Handled = true;
                RunSelected();
                break;
            case Key.Escape:
                e.Handled = true;
                Hide();
                break;
        }
    }

    /// The panel stays up above its own picker (the dialog is owned by it): the files are for it. Afterwards it takes
    /// the keyboard back, so typing goes on into the search field.
    void ChooseFiles()
    {
        var files = ToolUI.ChooseFiles(imagesOnly: false, multiple: true, owner: window);
        if (window is { IsVisible: true })
        {
            window.Present(focus: true);
            FocusSearch();
        }
        if (files.Count > 0)
        {
            chosenFiles = files;
            Rebuild();
        }
    }

    void RunSelected()
    {
        if (!Feature.QuickActions.IsOn() || confirming) return;
        if (selected < 0 || selected >= commands.Count)
        {
            ToolUI.Refuse(search);
            return;
        }
        int row = selected;
        var command = commands[row];
        // Re-resolve against enabled features, so a stale panel cannot run a disabled action.
        if (!MakeCommands(chosenFiles).Any(c => c.Id == command.Id))
        {
            Rebuild();
            return;
        }
        Confirm(row);
        Perform(command);
    }

    /// Blinks the chosen row off and on, the way a menu item confirms a choice, then closes. It stays under 150 ms
    /// because the keyboard started it; the command itself has already begun. The blink runs on dispatcher timers,
    /// which keep firing while a command's Open dialog or message box runs its modal loop, so the panel still
    /// closes behind that; and it goes at once if that window takes focus first.
    void Confirm(int row)
    {
        confirming = true;
        int token = ++generation;
        if (row < rows.Count) rows[row].IsSelected = false;
        ToolUI.After(0.05, () =>
        {
            if (generation != token || row >= rows.Count) return;
            rows[row].IsSelected = true;
        });
        ToolUI.After(0.12, () =>
        {
            if (generation != token) return;
            Hide();
        });
    }

    /// Runs a command as the panel goes. Slow ones get a "Title…" toast so the wait isn't silent; commands that open
    /// their own window (editors) don't. Errors use a red toast.
    async void Perform(QuickCommand command)
    {
        int run = ++runs;
        int editorsBefore = Editor.Presented;
        using var busy = new CancellationTokenSource();
        _ = ShowBusyLater(command, run, editorsBefore, busy.Token);
        try
        {
            var result = await command.Run();
            busy.Cancel();
            if (result is { } r && !(r.Message.Length == 0 && r.Files.Count == 0))
            {
                if (r.Files.Count > 0) Feedback.Done();
                Toast.Shared.Show(r.Message, "ok", r.Files);
            }
            else if (busyToastRun == run) Toast.Shared.Hide();
        }
        catch (Cancelled)
        {
            busy.Cancel();
            if (busyToastRun == run) Toast.Shared.Hide();
        }
        catch (Info info)
        {
            busy.Cancel();
            Toast.Shared.Show(info.Message, "info");
        }
        catch (Exception ex)
        {
            busy.Cancel();
            Feedback.Refuse();
            Toast.Shared.Show(ex.Message, "error", tint: ToastTint.Red);
        }
        finally
        {
            if (busyToastRun == run) busyToastRun = null;
        }
    }

    async Task ShowBusyLater(QuickCommand command, int run, int editorsBefore, CancellationToken cancel)
    {
        try { await Task.Delay(350, cancel); } catch (TaskCanceledException) { return; }
        if (cancel.IsCancellationRequested || Editor.Presented != editorsBefore) return;
        busyToastRun = run;
        Toast.Shared.Show(command.Title + "…", "busy", autoHide: false, spinning: true);
    }

    // MARK: Files dropped on the panel

    void DragOver(DragEventArgs e, bool entered)
    {
        bool accept = Feature.QuickActions.IsOn() && ToolUI.DroppedFiles(e.Data).Count > 0;
        e.Effects = accept ? ToolUI.DropEffect(e.AllowedEffects) : DragDropEffects.None;
        if (entered || accept) DropTargeted(accept);
        e.Handled = true;
    }

    bool targeted;

    /// Files hovering over the panel: say what letting go will do.
    void DropTargeted(bool value)
    {
        if (value == targeted) return;
        targeted = value;
        ring.SetShown(value);
        if (value) ToolUI.UseAccent(filesLabel, TextBlock.ForegroundProperty);
        else filesLabel.ClearValue(TextBlock.ForegroundProperty);
        filesLabel.Opacity = value ? 1 : 0.68;
        var text = value ? L("Drop to use these files") : FilesDescription;
        if (filesLabel.Text != text)
        {
            filesLabel.Text = text;
            ToolUI.FadeIn(filesLabel, from: 0.2, duration: 0.15);
        }
    }

    // MARK: Test hooks

    /// The rows the panel lists now (titles), for tests.
    internal IReadOnlyList<QuickCommand> VisibleCommands => commands;
    internal int SelectedRow => selected;
    internal ToolPanel? Panel => window;
    internal void SearchForTests(string text) => search.Text = text;
}

/// Glyph, title and where the command comes from; turns white on the accent selection like a menu item.
sealed class QuickRow : Border
{
    readonly TextBlock glyph, title, detail;
    bool isSelected, active;

    public QuickCommand Command { get; }

    public QuickRow(QuickCommand command)
    {
        Command = command;
        Height = 48;
        CornerRadius = new CornerRadius(6);
        Padding = new Thickness(10, 0, 10, 0);
        Margin = new Thickness(0, 0, 0, 2);
        Background = Brushes.Transparent;
        glyph = ToolUI.Glyph(command.Glyph, 18);
        glyph.Width = 28;
        title = new TextBlock { Text = command.Title, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis };
        detail = new TextBlock { Text = command.Detail, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        labels.Children.Add(title);
        labels.Children.Add(detail);
        var dock = new DockPanel();
        DockPanel.SetDock(glyph, Dock.Left);
        dock.Children.Add(glyph);
        dock.Children.Add(labels);
        Child = dock;
        ToolUI.AutomationName(this, $"{command.Title}, {command.Detail}");
        Recolor();
    }

    public bool IsSelected
    {
        get => isSelected;
        set { if (isSelected != value) { isSelected = value; Recolor(); } }
    }

    public bool Active
    {
        get => active;
        set { if (active != value) { active = value; Recolor(); } }
    }

    void Recolor()
    {
        bool emphasized = isSelected && active;
        if (emphasized)
        {
            ToolUI.UseAccent(this, BackgroundProperty);
            var white = Brushes.White;
            glyph.Foreground = title.Foreground = detail.Foreground = white;
            detail.Opacity = 0.85;
        }
        else
        {
            Background = isSelected ? new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)) : Brushes.Transparent;
            ToolUI.UseAccent(glyph, TextBlock.ForegroundProperty);
            title.ClearValue(TextBlock.ForegroundProperty);
            detail.ClearValue(TextBlock.ForegroundProperty);
            detail.Opacity = 0.68;
        }
    }
}
