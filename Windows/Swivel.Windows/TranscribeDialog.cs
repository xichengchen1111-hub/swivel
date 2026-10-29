using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Swivel.Windows;

/// Language and outputs for Transcribe Speech…, with what the model does and costs.
internal sealed class TranscribeDialog : Window
{
    /// null asks the model to detect the language.
    public string? SpeechLanguage { get; private set; }
    public bool Text { get; private set; } = true;
    public bool Subtitles { get; private set; } = true;

    private static readonly (string Name, string? Code)[] Choices = { ("Detect Automatically", null), ("Chinese", "zh"), ("English", "en"), ("Japanese", "ja") };

    public TranscribeDialog(App app, TranscribeDocument document, bool graphics)
    {
        Title = app.L("Transcribe Speech…"); Width = 640; Height = 560; MinWidth = 420; MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) };
        var language = new ComboBox { MinHeight = 28, Margin = new Thickness(0, 4, 0, 8) };
        foreach (var choice in Choices) language.Items.Add(app.L(choice.Name));
        language.SelectedIndex = 0; AutomationProperties.SetName(language, app.L("Language"));
        var text = new CheckBox { Content = app.L("Text (.txt), one line per passage"), IsChecked = true, Margin = new Thickness(0, 4, 0, 4) };
        var subtitles = new CheckBox { Content = app.L("Subtitles (.srt) with times"), IsChecked = true, Margin = new Thickness(0, 4, 0, 8) };
        var buttons = new WrapPanel();
        var start = UI.Button(app.L("Transcribe"), () =>
        {
            SpeechLanguage = Choices[Math.Max(0, language.SelectedIndex)].Code; Text = text.IsChecked == true; Subtitles = subtitles.IsChecked == true;
            DialogResult = true;
        });
        start.IsDefault = true;
        void Update(object? sender, RoutedEventArgs e) => start.IsEnabled = text.IsChecked == true || subtitles.IsChecked == true;
        text.Checked += Update; text.Unchecked += Update; subtitles.Checked += Update; subtitles.Unchecked += Update;
        buttons.Children.Add(start);
        var cancel = UI.Button(app.L("Cancel"), () => DialogResult = false); cancel.IsCancel = true;
        buttons.Children.Add(cancel); DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        var body = new StackPanel();
        body.Children.Add(UI.Text(app.L("Transcribe Speech…"), 22));
        body.Children.Add(UI.Text(document.Path, 12));
        body.Children.Add(UI.Text(string.Format(app.L("Length: {0}"), TimeSpan.FromSeconds(Math.Round(document.Seconds)).ToString(document.Seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture)), 12));
        body.Children.Add(UI.Text(app.L("Language")));
        body.Children.Add(language);
        var outputs = new StackPanel(); AutomationProperties.SetName(outputs, app.L("Save"));
        outputs.Children.Add(text); outputs.Children.Add(subtitles); body.Children.Add(outputs);
        body.Children.Add(UI.Text(app.L("Uses the Whisper base model on this PC (no upload, no download). It can mishear names, numbers and quiet or overlapping speech, and may invent words during music or noise, so check the text before using it.")));
        body.Children.Add(UI.Text(app.L("Detect Automatically chooses one language for the whole recording from its first 30 seconds. Chinese often comes out in Traditional characters with this model.")));
        body.Children.Add(UI.Text(app.L(graphics
            ? "Windows ML runs the model on this PC's graphics card."
            : "Windows ML found no graphics card it can use, so the processor does the work. This can take about as long as the recording or longer. The time left is shown after the first 30 seconds.")));
        body.Children.Add(UI.Text(app.L("Up to one hour of audio. Integer PCM WAV up to 512 MB is converted by Swivel; other WAV, FLAC, MP3, ADTS AAC and audio-only M4A (AAC-LC or ALAC) files are decoded by Windows (up to 128 MB). Video files are not accepted here.")));
        body.Children.Add(UI.Text(app.L("New files are saved beside the original without replacing existing files. The original stays unchanged. Cancelling before the transcript is finished saves nothing.")));
        root.Children.Add(UI.Scroll(body)); Content = root;
        Loaded += (_, _) => language.Focus();
    }
}
