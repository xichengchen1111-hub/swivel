using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Swivel;

// Reading codes from selected images, reviewing them, copying or opening after confirmation (QRCodes.swift), the
// shared window behaviour of the QR tools (FileToolWindow.swift) and the settings page (FileActionsPanes.swift).

/// One code found in a user-selected image.
sealed record DetectedCode(string Source, string Kind, string Value)
{
    /// A web link that may be offered for opening: http or https, with a host and without user name or password.
    /// Anything else (javascript:, file:, app schemes, plain text) is only ever shown and copied.
    public Uri? WebUrl
    {
        get
        {
            if (!Uri.TryCreate(Value.Trim(), UriKind.Absolute, out var url)) return null;
            if (url.Scheme is not ("http" or "https") || string.IsNullOrEmpty(url.Host) || url.UserInfo.Length > 0) return null;
            return url;
        }
    }

    /// The symbology's usual name (the Mac shows Vision's symbology identifier).
    public static string KindName(BarcodeFormat format) => format switch
    {
        BarcodeFormat.Qr => "QR",
        BarcodeFormat.Ean13 => "EAN-13",
        BarcodeFormat.Ean8 => "EAN-8",
        BarcodeFormat.UpcA => "UPC-A",
        BarcodeFormat.UpcE => "UPC-E",
        BarcodeFormat.Code128 => "Code 128",
        BarcodeFormat.Code39 => "Code 39",
        BarcodeFormat.Itf => "ITF",
        BarcodeFormat.Codabar => "Codabar",
        _ => format.ToString(),
    };
}

static class CodeRecognition
{
    /// Every readable code in the images, top to bottom and left to right per image, without duplicates. Nothing
    /// is written; an image that can't be read fails the whole read (as on the Mac).
    public static async Task<IReadOnlyList<DetectedCode>> Recognize(IReadOnlyList<string> paths, CancellationToken cancel = default)
    {
        var result = new List<DetectedCode>();
        foreach (var path in paths)
        {
            cancel.ThrowIfCancellationRequested();
            IReadOnlyList<DecodedCode> found;
            try { found = await QrCodec.ReadFile(path); }
            catch (Exception e) when (e is not ToolError and not OperationCanceledException)
            {
                throw new ToolError(L("Couldn't read %@", Path.GetFileName(path)));
            }
            var seen = new HashSet<string>();
            // Rows of codes (5 % of the image height apart) top to bottom, each left to right.
            double height = found.Count == 0 ? 1 : Math.Max(1, found.Max(c => c.Bounds.Bottom));
            var ordered = found
                .OrderBy(c => Math.Floor((c.Bounds.Top + c.Bounds.Height / 2) / (height * 0.05)))
                .ThenBy(c => c.Bounds.Left);
            foreach (var code in ordered)
            {
                if (string.IsNullOrEmpty(code.Text) || !seen.Add(code.Format + "\0" + code.Text)) continue;
                result.Add(new DetectedCode(path, DetectedCode.KindName(code.Format), code.Text));
            }
        }
        cancel.ThrowIfCancellationRequested();
        return result;
    }
}

/// What the QR windows share (FileToolWindow.swift): feature gating, one list of open windows, busy work that locks
/// the controls, and closing when the feature is turned off. Normal top-level windows, like the editors: topmost
/// until first activated, so they still appear when Windows refuses activation during a drop.
abstract class QrToolWindow
{
    public Window Window { get; }
    public bool IsBusy { get; private set; }
    readonly List<(UIElement Control, bool Enabled)> locked = [];
    readonly Action prefsChanged;

    protected QrToolWindow(string title, double width, double height)
    {
        Window = new Window
        {
            Title = title,
            Width = width,
            Height = height,
            MinWidth = Math.Min(740, width),
            MinHeight = Math.Min(560, height),
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
        };
        AppTypography.Apply(Window);
        Window.Activated += (_, _) => { if (Window.Topmost) Window.Topmost = false; };
        Window.Closing += (_, e) => { if (IsBusy) e.Cancel = true; };
        prefsChanged = () => Window.Dispatcher.BeginInvoke(() =>
        {
            if (!Feature.QrCodes.IsOn() && !IsBusy) Window.Close();
        });
        Prefs.Changed += prefsChanged;
        Window.Closed += (_, _) => Prefs.Changed -= prefsChanged;
    }

    /// Runs work with the window's controls locked. Errors go to the status line, and to an alert when the window is
    /// on screen (an NSAlert sheet on the Mac).
    protected async Task RunWork(Func<Task> body)
    {
        if (!Feature.QrCodes.IsOn() || IsBusy) return;
        IsBusy = true;
        Lock(Window.Content as DependencyObject);
        WorkStarted();
        string? failure = null;
        try
        {
            await body();
            Feedback.Done();
        }
        catch (Exception ex)
        {
            Feedback.Refuse();
            failure = ex.Message;
        }
        IsBusy = false;
        foreach (var (control, enabled) in locked) control.IsEnabled = enabled;
        locked.Clear();
        WorkFinished();
        if (failure != null)
        {
            ShowError(failure);
            if (Window.IsVisible) EditorDialog.Alert(Window, failure);
        }
        if (!Feature.QrCodes.IsOn()) Window.Close();
    }

    protected virtual void WorkStarted() { }
    protected virtual void WorkFinished() { }
    protected abstract void ShowError(string message);

    void Lock(DependencyObject? node)
    {
        if (node is null) return;
        if (node is ButtonBase or TextBox or ComboBox or ListBox or SelectorBar or QrGardenView)
        {
            var control = (UIElement)node;
            locked.Add((control, control.IsEnabled));
            control.IsEnabled = false;
            return;
        }
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Lock(VisualTreeHelper.GetChild(node, i));
        if (VisualTreeHelper.GetChildrenCount(node) == 0)
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) Lock(child);
    }
}

/// Lists every code in the selected images with its text and type. Content is reviewed before copying, and a web
/// link opens only after confirming it — never automatically.
sealed class QrCodesWindow : QrToolWindow
{
    static readonly List<QrCodesWindow> open = [];

    public IReadOnlyList<DetectedCode> Codes { get; private set; } = [];
    public ListBox List { get; } = new() { Height = 180 };
    public TextBox Content { get; } = new();
    public Button CopyButton { get; }
    public Button OpenButton { get; }
    public Button SaveButton { get; }
    readonly TextBlock status = ToolUI.Note(L("Reading codes…"));
    public string Status => status.Text;

    /// Where Copy puts the text (tests leave the user's clipboard alone).
    public Action<string> SetClipboard { get; set; } = FileActions.SetClipboardText;
    /// Asks before a link opens; true opens it.
    public Func<Uri, bool> ConfirmOpen { get; set; }
    /// Opens a confirmed link in the default browser; false when that failed.
    public Func<Uri, bool> OpenUrl { get; set; } = url =>
    {
        try { FileActions.Open(url.AbsoluteUri); return true; }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    };
    /// The library codes are saved to.
    public QrLibraryStore Store { get; set; } = new();
    public Task Loading { get; private set; } = Task.CompletedTask;

    public static void Present(IReadOnlyList<string> files)
    {
        if (!Feature.QrCodes.IsOn()) return;
        var controller = new QrCodesWindow();
        open.Add(controller);
        ToolUI.Show(controller.Window);
        controller.Start(files);
    }

    public static void CloseAll()
    {
        foreach (var controller in open.ToList())
        {
            if (controller.IsBusy) continue;
            controller.Window.Close();
        }
    }

    public QrCodesWindow() : base(L("QR & Barcodes"), 760, 610)
    {
        ConfirmOpen = url => EditorDialog.Show(Window, L("Open this link?"), url.AbsoluteUri, [L("Cancel"), L("Open")], cancelIndex: 0) == 1;
        ToolUI.AutomationName(List, L("Detected codes"));
        List.SelectionChanged += (_, _) => UpdateButtons();
        // Long content is trimmed in the list (the whole text is below), never scrolled sideways.
        ScrollViewer.SetHorizontalScrollBarVisibility(List, ScrollBarVisibility.Disabled);
        Content.IsReadOnly = true;
        Content.TextWrapping = TextWrapping.Wrap;
        Content.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Content.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        ToolUI.Scaled(Content, 13);
        Content.Padding = new Thickness(10);
        Content.MinHeight = 130;
        Content.VerticalContentAlignment = VerticalAlignment.Top;
        ToolUI.AutomationName(Content, L("Decoded content"));

        CopyButton = ToolUI.Button(L("Copy Content"), CopyCode);
        CopyButton.IsDefault = true;
        CopyButton.SetResourceReference(FrameworkElement.StyleProperty, "AccentButtonStyle");
        OpenButton = ToolUI.Button(L("Open Link…"), OpenLink);
        SaveButton = ToolUI.Button(L("Save to Library"), SaveCode);
        var library = ToolUI.Button(L("QR Library…"), () => QrLibraryWindow.Show(store: Store));
        status.VerticalAlignment = VerticalAlignment.Center;
        status.TextTrimming = TextTrimming.CharacterEllipsis;
        status.TextWrapping = TextWrapping.NoWrap;
        var footer = ToolUI.Bar([status], [OpenButton, CopyButton]);
        var note = ToolUI.Note(L("Read QR codes and barcodes from images on your PC. Review the decoded content before copying or opening a link."));
        var actions = ToolUI.Row([SaveButton, library], spacing: 8);

        var root = new DockPanel { Margin = new Thickness(20) };
        foreach (var (element, top) in new (FrameworkElement, double)[] { (note, 0), (List, 12), (actions, 12) })
        {
            element.Margin = new Thickness(0, top, 0, 0);
            DockPanel.SetDock(element, Dock.Top);
            root.Children.Add(element);
        }
        footer.Margin = new Thickness(0, 12, 0, 0);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        Content.Margin = new Thickness(0, 12, 0, 0);
        root.Children.Add(Content);
        Window.Content = root;
        Window.Closed += (_, _) => open.Remove(this);
        UpdateButtons();
    }

    /// Reads the images; the window is already on screen.
    public void Start(IReadOnlyList<string> files) => Loading = RunWork(async () =>
    {
        var codes = await CodeRecognition.Recognize(files);
        ShowCodes(codes);
    });

    /// Fills the list (also used by tests with codes of their own).
    public void ShowCodes(IReadOnlyList<DetectedCode> codes)
    {
        Codes = codes;
        List.Items.Clear();
        foreach (var code in codes) List.Items.Add(Row(code));
        if (codes.Count > 0) List.SelectedIndex = 0;
        UpdateButtons();
    }

    static ListBoxItem Row(DetectedCode code)
    {
        var value = new TextBlock { Text = code.Value.ReplaceLineEndings(" "), TextTrimming = TextTrimming.CharacterEllipsis };
        var detail = ToolUI.Note(Path.GetFileName(code.Source) + " · " + code.Kind);
        detail.TextWrapping = TextWrapping.NoWrap;
        detail.TextTrimming = TextTrimming.CharacterEllipsis;
        detail.Margin = new Thickness(0, 3, 0, 0);
        var stack = new StackPanel { Margin = new Thickness(2, 5, 2, 5) };
        stack.Children.Add(value);
        stack.Children.Add(detail);
        var item = new ListBoxItem { Content = stack, Tag = code };
        ToolUI.AutomationName(item, code.Value);
        return item;
    }

    DetectedCode? Selected => List.SelectedIndex >= 0 && List.SelectedIndex < Codes.Count ? Codes[List.SelectedIndex] : null;

    void UpdateButtons()
    {
        var selected = Selected;
        CopyButton.IsEnabled = !IsBusy && selected != null;
        OpenButton.IsEnabled = !IsBusy && selected?.WebUrl != null;
        SaveButton.IsEnabled = !IsBusy && selected != null;
        Content.Text = selected?.Value ?? "";
        status.Text = IsBusy ? L("Reading codes…") : Codes.Count == 0 ? L("No readable QR codes or barcodes found.") : L("%d codes found", Codes.Count);
    }

    protected override void WorkStarted() => UpdateButtons();
    protected override void WorkFinished() => UpdateButtons();
    protected override void ShowError(string message) => status.Text = message;

    public void CopyCode()
    {
        if (!Feature.QrCodes.IsOn() || IsBusy || Selected is not { } selected) return;
        SetClipboard(selected.Value);
        Feedback.Press();
        status.Text = L("Copied");
    }

    public void SaveCode()
    {
        if (!Feature.QrCodes.IsOn() || IsBusy || Selected is not { } selected) return;
        try
        {
            var value = Store.Add(SavedQrCode.New(Path.GetFileNameWithoutExtension(selected.Source), selected.Value));
            QrLibraryWindow.Show(value.Id, Store);
        }
        catch (ToolError e) { status.Text = e.Message; }
        catch (IOException e) { status.Text = e.Message; }
    }

    /// Opens the selected web link only after the user confirms it.
    public void OpenLink()
    {
        if (!Feature.QrCodes.IsOn() || IsBusy || Selected?.WebUrl is not { } url) return;
        if (!ConfirmOpen(url) || !Feature.QrCodes.IsOn()) return;
        if (!OpenUrl(url)) status.Text = L("Could not open the link.");
    }
}

/// The QR & Barcodes settings page (FileActionsPane for .qrCodes). The page title comes from FeaturePage.
sealed class QrCodesPane : Pane
{
    readonly Button choose, library;
    readonly TextBlock offHint;

    public QrCodesPane()
    {
        Add(Ui.Text(L("Choose images to read QR codes and barcodes locally. Copy the decoded content, or review a web link before opening it. No camera or screen capture is used."), tone: Tone.Secondary));
        choose = Ui.Button(L("Choose Files…"), () => FileTools.ChooseAndOpen(Feature.QrCodes), accent: true);
        library = Ui.Button(L("QR Library…"), () => QrLibraryWindow.Show());
        Add(Ui.Row(choose, library), 16);
        Add(Ui.Text(L("Also available in the Wheel’s Tools menu and Quick Actions."), 12, tone: Tone.Secondary), 16);
        offHint = Ui.Text(L("Turn this feature on above to get started."), 12, tone: Tone.Secondary);
        Add(offHint, 10);
        Refresh();
    }

    internal Button Choose => choose;
    internal Button Library => library;

    public override void Refresh()
    {
        bool on = Feature.QrCodes.IsOn();
        choose.IsEnabled = on;
        library.IsEnabled = on;
        offHint.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
    }
}
