using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Ocr;
using WinImaging = Windows.Graphics.Imaging;

namespace Swivel;

/// On-device OCR of an image file with Windows.Media.Ocr (the Mac app uses Vision). No screen capture is performed.
static class TextRecognition
{
    /// The languages Swivel reads, in the Mac app's order.
    public static readonly string[] Languages = ["en", "zh-Hans", "zh-Hant", "ja"];

    /// Which of `Languages` a recognizer language tag is ("en-US" → "en", "zh-CN" → "zh-Hans"), or null.
    public static string? Classify(string tag)
    {
        if (tag.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return "en";
        if (tag.StartsWith("ja", StringComparison.OrdinalIgnoreCase)) return "ja";
        if (tag.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            if (tag.Contains("Hant", StringComparison.OrdinalIgnoreCase) || tag.EndsWith("TW", StringComparison.OrdinalIgnoreCase) ||
                tag.EndsWith("HK", StringComparison.OrdinalIgnoreCase) || tag.EndsWith("MO", StringComparison.OrdinalIgnoreCase)) return "zh-Hant";
            return "zh-Hans";
        }
        return null;
    }

    /// The installed OCR languages among English, Chinese and Japanese (tag, engine). When none of them is installed
    /// but another OCR language is, that one is used rather than failing.
    public static List<(string Language, OcrEngine Engine)> Engines()
    {
        var found = new List<(string, OcrEngine)>();
        var available = OcrEngine.AvailableRecognizerLanguages.ToList();
        foreach (var wanted in Languages)
        {
            var language = available.FirstOrDefault(l => Classify(l.LanguageTag) == wanted);
            if (language != null && OcrEngine.TryCreateFromLanguage(language) is { } engine) found.Add((wanted, engine));
        }
        if (found.Count == 0 && available.Count > 0)
        {
            var engine = OcrEngine.TryCreateFromUserProfileLanguages() ?? OcrEngine.TryCreateFromLanguage(available[0]);
            if (engine != null) found.Add((engine.RecognizerLanguage.LanguageTag, engine));
        }
        return found;
    }

    public static string NoLanguageMessage =>
        L("Text recognition needs an OCR language. Add English, Chinese or Japanese in Settings › Time & language › Language & region, then try again.");

    /// Input is an existing user-selected image. Every installed OCR language among en, zh-Hans, zh-Hant and ja
    /// reads it, and the most plausible reading wins (`PickBest`).
    public static Task<string> Recognize(string path) => Recognize(path, CancellationToken.None);

    public static Task<string> Recognize(string path, CancellationToken cancel) => Task.Run(async () =>
    {
        var name = System.IO.Path.GetFileName(path);
        if (!File.Exists(path)) throw new ToolError(L("Could not read %@", name));
        var engines = Engines();
        if (engines.Count == 0) throw new ToolError(NoLanguageMessage);
        cancel.ThrowIfCancellationRequested();
        using var bitmap = await Decode(path) ?? throw new ToolError(L("Could not read %@", name));
        var readings = new List<(string Language, string Text)>();
        foreach (var (language, engine) in engines)
        {
            cancel.ThrowIfCancellationRequested();
            var result = await engine.RecognizeAsync(bitmap);
            readings.Add((language, Join(result)));
        }
        cancel.ThrowIfCancellationRequested();
        return PickBest(readings, Preferred());
    }, cancel);

    /// The app's language among the four, for breaking ties between the Chinese and Japanese readings.
    static string Preferred() => Swivel.Languages.Resolved switch
    {
        Language.ZhHans => "zh-Hans",
        Language.ZhHant => "zh-Hant",
        Language.Ja => "ja",
        _ => Classify(CultureInfo.CurrentUICulture.Name) ?? "en",
    };

    /// Decodes upright (EXIF orientation), as BGRA8 composited on white (transparent text would read as black on
    /// black), sized for the recognizer: at most OcrEngine.MaxImageDimension, and small images doubled, since
    /// Windows OCR misses text under about 10 px tall.
    static async Task<WinImaging.SoftwareBitmap?> Decode(string path)
    {
        WinImaging.SoftwareBitmap? bitmap = null;
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var decoder = await WinImaging.BitmapDecoder.CreateAsync(stream);
            double factor = Factor(decoder.PixelWidth, decoder.PixelHeight);
            var transform = new WinImaging.BitmapTransform
            {
                ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * factor)),
                ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * factor)),
                InterpolationMode = WinImaging.BitmapInterpolationMode.Fant,
            };
            bitmap = await decoder.GetSoftwareBitmapAsync(WinImaging.BitmapPixelFormat.Bgra8, WinImaging.BitmapAlphaMode.Premultiplied,
                transform, WinImaging.ExifOrientationMode.RespectExifOrientation, WinImaging.ColorManagementMode.DoNotColorManage);
        }
        catch (Exception)
        {
            // Formats the WinRT decoder doesn't take: the app's own loader (WPF / WIC).
            try
            {
                var image = Images.Load(path, (int)OcrEngine.MaxImageDimension);
                double factor = Factor((uint)image.PixelWidth, (uint)image.PixelHeight);
                if (factor > 1)
                {
                    var scaled = new TransformedBitmap(image, new ScaleTransform(factor, factor));
                    scaled.Freeze();
                    image = scaled;
                }
                bitmap = Bitmaps.ToSoftwareBitmap(image);
            }
            catch (Exception) { return null; }
        }
        return OnWhite(bitmap);
    }

    static double Factor(uint width, uint height)
    {
        double longEdge = Math.Max(width, height), max = OcrEngine.MaxImageDimension;
        if (longEdge <= 0) return 1;
        if (longEdge > max) return max / longEdge;
        return longEdge < 1500 ? Math.Min(2, max / longEdge) : 1;
    }

    static WinImaging.SoftwareBitmap OnWhite(WinImaging.SoftwareBitmap bitmap)
    {
        var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyToBuffer(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(bytes));
        bool changed = false;
        for (int i = 0; i < bytes.Length; i += 4)
        {
            int a = bytes[i + 3];
            if (a == 255) continue;
            // Premultiplied: colour + white × (1 − alpha).
            int add = 255 - a;
            bytes[i] = (byte)Math.Min(255, bytes[i] + add);
            bytes[i + 1] = (byte)Math.Min(255, bytes[i + 1] + add);
            bytes[i + 2] = (byte)Math.Min(255, bytes[i + 2] + add);
            bytes[i + 3] = 255;
            changed = true;
        }
        if (!changed) return bitmap;
        var flat = WinImaging.SoftwareBitmap.CreateCopyFromBuffer(
            System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(bytes),
            WinImaging.BitmapPixelFormat.Bgra8, bitmap.PixelWidth, bitmap.PixelHeight, WinImaging.BitmapAlphaMode.Premultiplied);
        bitmap.Dispose();
        return flat;
    }

    /// One line per OCR line, words joined with spaces except between two CJK characters (Windows OCR reports every
    /// Chinese or Japanese character as its own word).
    public static string Join(OcrResult result) =>
        string.Join("\n", result.Lines.Select(line => JoinWords(line.Words.Select(w => w.Text))).Where(l => l.Length > 0));

    public static string JoinWords(IEnumerable<string> words)
    {
        var sb = new StringBuilder();
        Rune? last = null;
        foreach (var raw in words)
        {
            var word = raw.Trim();
            if (word.Length == 0) continue;
            var first = Rune.GetRuneAt(word, 0);
            if (last is { } previous && !(IsCjk(previous) && IsCjk(first))) sb.Append(' ');
            sb.Append(word);
            last = word.EnumerateRunes().Last();
        }
        return sb.ToString();
    }

    /// Han, kana, Hangul and full-width CJK punctuation: no spaces between them.
    public static bool IsCjk(Rune rune)
    {
        int c = rune.Value;
        return c is >= 0x2E80 and <= 0x2FDF      // radicals
            or >= 0x3000 and <= 0x303F           // CJK punctuation
            or >= 0x3040 and <= 0x30FF           // hiragana, katakana
            or >= 0x3100 and <= 0x312F           // bopomofo
            or >= 0x3190 and <= 0x31FF           // kanbun, katakana extensions
            or >= 0x3400 and <= 0x4DBF           // extension A
            or >= 0x4E00 and <= 0x9FFF           // unified ideographs
            or >= 0xAC00 and <= 0xD7AF           // Hangul
            or >= 0xF900 and <= 0xFAFF           // compatibility ideographs
            or >= 0xFF00 and <= 0xFFEF           // full-width forms
            or >= 0x20000 and <= 0x3134F;        // extensions B–G
    }

    static bool IsHan(int c) => c is >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x3134F;
    static bool IsKana(int c) => c is >= 0x3040 and <= 0x30FF or >= 0x31F0 and <= 0x31FF or >= 0xFF66 and <= 0xFF9F;

    /// How plausible a reading is for the language that produced it. Windows OCR gives no confidence, so this counts
    /// what each recognizer can actually read: an English recognizer turns Chinese into stray Latin letters and
    /// symbols, a Chinese one reads Latin a little worse than the English one. CJK characters weigh double (one
    /// character carries what a short Latin word does); stray symbols count against.
    public static double Score(string language, string text)
    {
        double latin = 0, han = 0, kana = 0, other = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            int c = rune.Value;
            if (Rune.IsWhiteSpace(rune)) continue;
            if (Rune.IsLetterOrDigit(rune) && c < 0x2E80) latin++;
            else if (IsHan(c)) han++;
            else if (IsKana(c)) kana++;
            else if (!Rune.IsPunctuation(rune)) other++;
        }
        return language switch
        {
            "en" => latin - 2 * (han + kana) - 0.5 * other,
            "ja" => 2 * han + 2 * kana + 0.9 * latin - 0.5 * other,
            "zh-Hans" or "zh-Hant" => 2 * han + 0.9 * latin - kana - 0.5 * other,
            _ => latin + 2 * (han + kana) - 0.5 * other,
        };
    }

    /// The best reading; ties go to the app's language, then to the order of `Languages`.
    public static string PickBest(IReadOnlyList<(string Language, string Text)> readings, string preferred = "en")
    {
        if (readings.Count == 0) return "";
        return readings
            .Select((r, order) => (r.Text, Score: Score(r.Language, r.Text), Preferred: r.Language == preferred, order))
            .OrderByDescending(r => Math.Round(r.Score, 3))
            .ThenByDescending(r => r.Preferred)
            .ThenBy(r => r.order)
            .First().Text;
    }
}

/// Review and copy the text of one image (TextCapture.swift). The window comes up at once; the picture and the
/// text fill in as they're ready. Another image can be dropped on the window or chosen to read that one instead.
sealed class TextCaptureWindow
{
    static readonly List<TextCaptureWindow> open = [];

    public Window Window { get; }
    /// The editable text. Read-only until recognition finishes.
    public TextBox Text { get; } = new();
    public Button CopyButton { get; }
    /// Where Copy puts the text. Tests replace it so they leave the user's clipboard alone.
    public Action<string> SetClipboard { get; set; } = FileActions.SetClipboardText;

    readonly Image preview = new() { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
    readonly TextBlock status;
    readonly ProgressBar progress = new() { IsIndeterminate = true, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
    readonly DropRing ring = new();
    CancellationTokenSource? task;
    /// Changes on every copy, so an older "Copied" can't put the title back early.
    int copyGeneration;
    bool showingCopied;
    string path;

    public static void Present(string path)
    {
        if (!Feature.TextCapture.IsOn()) return;
        var controller = new TextCaptureWindow(path);
        open.Add(controller);
        ToolUI.Show(controller.Window);
        controller.Start(path);
    }

    public static void CloseAll()
    {
        foreach (var controller in open.ToList()) controller.Window.Close();
    }

    /// Builds the window without showing it; `Present` shows it and starts recognition.
    public TextCaptureWindow(string path)
    {
        this.path = path;
        Window = new Window
        {
            Width = 780, Height = 540, MinWidth = 640, MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            AllowDrop = true,
        };
        UpdateTitle();

        ToolUI.AutomationName(preview, L("Source image"));
        var well = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)),
            Background = new SolidColorBrush(Color.FromArgb(0x18, 0x80, 0x80, 0x80)),
            Padding = new Thickness(8),
            Child = preview,
        };

        Text.FontSize = 15;
        Text.AcceptsReturn = true;
        Text.AcceptsTab = false;
        Text.TextWrapping = TextWrapping.Wrap;
        Text.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Text.IsReadOnly = true;
        Text.Padding = new Thickness(10);
        Text.VerticalContentAlignment = VerticalAlignment.Top;
        ToolUI.AutomationName(Text, L("Recognized text"));
        Text.TextChanged += (_, _) => UpdateCopy();
        Text.SelectionChanged += (_, _) => UpdateCopyTitle();

        var columns = new Grid { MinHeight = 260 };
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(Text, 2);
        columns.Children.Add(well);
        columns.Children.Add(Text);

        status = ToolUI.Note(L("Recognizing text…"));
        status.TextTrimming = TextTrimming.CharacterEllipsis;
        status.TextWrapping = TextWrapping.NoWrap;
        status.VerticalAlignment = VerticalAlignment.Center;
        CopyButton = ToolUI.Button(L("Copy Text"), CopyText);
        CopyButton.IsDefault = true;
        CopyButton.IsEnabled = false;
        CopyButton.ToolTip = L("Press Ctrl+Enter to copy while editing the text.");
        ToolUI.ReserveWidth(CopyButton, [L("Copy Text"), L("Copy Selection"), CopiedContent()]);
        var close = ToolUI.Button(L("Close"), () => Window.Close());
        close.IsCancel = true;
        var choose = ToolUI.Button(L("Choose an Image…"), ChooseImage);
        var footer = ToolUI.Bar([progress, status], [choose, close, CopyButton]);
        var note = ToolUI.Note(L("Review the recognized text before copying. You can edit it here."));

        var root = new DockPanel { Margin = new Thickness(20) };
        DockPanel.SetDock(footer, Dock.Bottom);
        DockPanel.SetDock(note, Dock.Bottom);
        note.Margin = new Thickness(0, 12, 0, 12);
        root.Children.Add(footer);
        root.Children.Add(note);
        root.Children.Add(columns);
        var layers = new Grid();
        layers.Children.Add(root);
        layers.Children.Add(ring);
        Window.Content = layers;

        // Left alone, focus would go into the text box (the only control that takes it), and there Return types a
        // new line instead of reaching Copy. Starting on Copy keeps it off the text.
        System.Windows.Input.FocusManager.SetFocusedElement(Window, CopyButton);

        // Ctrl+Enter copies from anywhere, including while editing, where plain Return types a new line.
        Window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Return && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                CopyText();
            }
        };
        Window.DragEnter += (_, e) => DragOver(e);
        Window.DragOver += (_, e) => DragOver(e);
        Window.DragLeave += (_, _) => ring.SetShown(false);
        Window.Drop += (_, e) =>
        {
            ring.SetShown(false);
            if (ToolUI.DroppedFiles(e.Data).FirstOrDefault(p => FileKinds.Of(p) == FileKind.Image) is { } image)
            {
                e.Effects = DragDropEffects.Copy;
                Start(image);
            }
        };
        Window.Closed += (_, _) =>
        {
            task?.Cancel();
            open.Remove(this);
        };
    }

    void DragOver(DragEventArgs e)
    {
        bool image = Feature.TextCapture.IsOn() && ToolUI.DroppedFiles(e.Data).Any(p => FileKinds.Of(p) == FileKind.Image);
        e.Effects = image ? ToolUI.DropEffect(e.AllowedEffects) : DragDropEffects.None;
        ring.SetShown(image);
        e.Handled = true;
    }

    void UpdateTitle() => Window.Title = $"{L("Text Capture")} — {System.IO.Path.GetFileName(path)}";

    void ChooseImage()
    {
        if (ToolUI.ChooseFiles(imagesOnly: true, multiple: false, owner: Window).FirstOrDefault() is { } file) Start(file);
    }

    /// Shows the image and reads its text (again, for a newly dropped or chosen image).
    public void Start(string image)
    {
        path = image;
        UpdateTitle();
        task?.Cancel();
        Text.IsReadOnly = true;
        Text.Text = "";
        status.Text = L("Recognizing text…");
        progress.Visibility = Visibility.Visible;
        UpdateCopy();
        LoadPreview(image);
        Recognize(image);
    }

    /// Decodes the preview off the UI thread (a large HEIC takes a noticeable moment) and fades it in.
    async void LoadPreview(string image)
    {
        try
        {
            var bitmap = await Task.Run(() =>
            {
                var loaded = Images.Load(image, 1800);
                if (loaded.CanFreeze) loaded.Freeze();
                return loaded;
            });
            if (path != image) return;
            preview.Source = bitmap;
            ToolUI.FadeIn(preview);
        }
        catch (Exception) { preview.Source = null; }
    }

    async void Recognize(string image)
    {
        var cancel = new CancellationTokenSource();
        task = cancel;
        try
        {
            var value = await TextRecognition.Recognize(image, cancel.Token);
            if (cancel.IsCancellationRequested) return;
            ShowRecognized(value);
        }
        catch (Exception ex)
        {
            if (cancel.IsCancellationRequested) return;
            progress.Visibility = Visibility.Collapsed;
            status.Text = L("Could not recognize text: %@", ex.Message);
        }
    }

    /// The text is ready to review. Focus stays off it, so Return copies straight away (Copy is the default button).
    /// Once the user clicks or tabs into the text, Return types a new line there and Ctrl+Enter copies, as the Copy
    /// button's tooltip says.
    public void ShowRecognized(string value)
    {
        Text.Text = value;
        Text.IsReadOnly = false;
        Text.Select(0, 0);
        Text.ScrollToHome();
        // A click into the empty box while waiting would otherwise leave focus there.
        if (Text.IsKeyboardFocusWithin || System.Windows.Input.FocusManager.GetFocusedElement(Window) == Text)
        {
            System.Windows.Input.FocusManager.SetFocusedElement(Window, CopyButton);
            if (Window.IsActive) Keyboard.Focus(CopyButton);
        }
        status.Text = value.Length == 0 ? L("No text found in this image.") : L("Ready to copy");
        progress.Visibility = Visibility.Collapsed;
        UpdateCopy();
        // Settle in softly.
        if (value.Length > 0) ToolUI.FadeIn(Text, from: 0.4);
    }

    void UpdateCopy() => CopyButton.IsEnabled = !Text.IsReadOnly && Text.Text.Trim().Length > 0;

    /// "Copy Selection" while part of the text is selected; that's all that gets copied then.
    void UpdateCopyTitle()
    {
        if (showingCopied) return;
        CopyButton.Content = Text.SelectionLength > 0 ? L("Copy Selection") : L("Copy Text");
    }

    static object CopiedContent() => ToolUI.Row([ToolUI.Glyph("", 12), new TextBlock { Text = L("Copied") }], spacing: 6);

    public void CopyText()
    {
        if (!Feature.TextCapture.IsOn() || !CopyButton.IsEnabled) return;
        var value = Text.SelectionLength > 0 ? Text.SelectedText : Text.Text;
        SetClipboard(value);
        status.Text = L("Text copied");
        Feedback.Done();
        // The button itself says it worked, for a moment.
        int token = ++copyGeneration;
        showingCopied = true;
        CopyButton.Content = CopiedContent();
        ToolUI.After(1.5, () =>
        {
            if (copyGeneration != token) return;
            showingCopied = false;
            UpdateCopyTitle();
        });
    }

    public string Status => status.Text;
}
