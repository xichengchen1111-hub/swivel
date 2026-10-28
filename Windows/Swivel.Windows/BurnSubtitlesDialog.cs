using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Swivel.Core;

namespace Swivel.Windows;

/// Where the subtitles come from (transcribe the video's speech, or an SRT file) and how large
/// they are drawn, for Burn In Subtitles….
internal sealed class BurnSubtitlesDialog : Window
{
    public bool Transcribe { get; private set; }
    /// null asks the model to detect the language.
    public string? SpeechLanguage { get; private set; }
    public bool SaveSubtitles { get; private set; }
    public string? SubtitleFile { get; private set; }
    public int TextSize { get; private set; } = 1;

    private static readonly (string Name, string? Code)[] Languages = { ("Detect Automatically", null), ("Chinese", "zh"), ("English", "en"), ("Japanese", "ja") };

    public BurnSubtitlesDialog(App app, VideoSubtitles video)
    {
        Title = app.L("Burn In Subtitles…"); Width = 660; Height = 620; MinWidth = 440; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) };
        bool speech = video.HasSound && video.Seconds <= Swivel.Core.Transcribe.MaxSeconds;
        var fromSpeech = new RadioButton { Content = app.L("Transcribe the video's speech on this PC"), GroupName = "source", IsChecked = speech, IsEnabled = speech, Margin = new Thickness(0, 4, 0, 4) };
        var fromFile = new RadioButton { Content = app.L("Use an SRT subtitle file"), GroupName = "source", IsChecked = !speech, Margin = new Thickness(0, 8, 0, 4) };
        var language = new ComboBox { MinHeight = 28, Margin = new Thickness(24, 4, 0, 4) };
        foreach (var choice in Languages) language.Items.Add(app.L(choice.Name));
        language.SelectedIndex = 0; AutomationProperties.SetName(language, app.L("Language"));
        var save = new CheckBox { Content = app.L("Also save the subtitles as an SRT file"), IsChecked = true, Margin = new Thickness(24, 4, 0, 4) };
        string beside = System.IO.Path.ChangeExtension(video.Path, ".srt");
        var file = new TextBox { IsReadOnly = true, MinHeight = 28, Text = File.Exists(beside) ? beside : "", Margin = new Thickness(24, 4, 8, 4), MinWidth = 300 };
        AutomationProperties.SetName(file, app.L("SRT file"));
        var browse = UI.Button(app.L("Choose…"), () =>
        {
            var picker = new Microsoft.Win32.OpenFileDialog { Filter = app.L("Subtitles") + " (*.srt)|*.srt", CheckFileExists = true, Multiselect = false };
            if (picker.ShowDialog(this) == true) { file.Text = picker.FileName; fromFile.IsChecked = true; }
        });
        var size = new ComboBox { MinHeight = 28, Margin = new Thickness(0, 4, 0, 8) };
        foreach (var name in SubtitleBurn.SizeNames) size.Items.Add(app.L(name));
        size.SelectedIndex = 1; AutomationProperties.SetName(size, app.L("Text Size"));

        var buttons = new WrapPanel();
        var start = UI.Button(app.L("Burn In"), () =>
        {
            Transcribe = fromSpeech.IsChecked == true; SpeechLanguage = Languages[Math.Max(0, language.SelectedIndex)].Code;
            SaveSubtitles = Transcribe && save.IsChecked == true; SubtitleFile = Transcribe ? null : file.Text; TextSize = Math.Max(0, size.SelectedIndex);
            DialogResult = true;
        });
        start.IsDefault = true;
        void Update()
        {
            bool transcribe = fromSpeech.IsChecked == true;
            language.IsEnabled = save.IsEnabled = transcribe;
            start.IsEnabled = transcribe || file.Text.Length > 0;
        }
        fromSpeech.Checked += (_, _) => Update(); fromFile.Checked += (_, _) => Update(); file.TextChanged += (_, _) => Update();
        buttons.Children.Add(start);
        var cancel = UI.Button(app.L("Cancel"), () => DialogResult = false); cancel.IsCancel = true;
        buttons.Children.Add(cancel); DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);

        var body = new StackPanel();
        body.Children.Add(UI.Text(app.L("Burn In Subtitles…"), 22));
        body.Children.Add(UI.Text(video.Path, 12));
        body.Children.Add(UI.Text(string.Format(app.L("{0} × {1} pixels, {2}"), video.Width, video.Height,
            TimeSpan.FromSeconds(Math.Round(video.Seconds)).ToString(video.Seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture)), 12));
        var source = new StackPanel(); AutomationProperties.SetName(source, app.L("Subtitles"));
        source.Children.Add(fromSpeech); source.Children.Add(language); source.Children.Add(save);
        if (!speech) source.Children.Add(UI.Text(app.L(video.HasSound ? "Transcription takes videos up to one hour long." : "This video has no sound track, so only an SRT file can be used."), 12));
        source.Children.Add(fromFile);
        var row = new WrapPanel(); row.Children.Add(file); row.Children.Add(browse); source.Children.Add(row);
        body.Children.Add(source);
        body.Children.Add(UI.Text(app.L("Text Size")));
        body.Children.Add(size);
        body.Children.Add(UI.Text(app.L("Subtitles are drawn into the picture (white text on a dark band near the bottom, at most three lines) and cannot be turned off afterwards. Overlapping subtitles are shortened so only one shows at a time.")));
        body.Children.Add(UI.Text(app.L("Transcription uses the Whisper base model on this PC (no upload, no download); check the text afterwards, or save it as SRT, correct it, and burn that file in instead.")));
        body.Children.Add(UI.Text(app.L("Windows encodes a new H.264/AAC MP4 at the original size and frame rate. The whole video is re-encoded, so it takes time and loses a little quality; HDR becomes SDR, and extra sound tracks, chapters and metadata are not kept. Up to 3840 × 2160 and two hours; phone videos stored sideways come out upright.")));
        body.Children.Add(UI.Text(app.L("The new file is saved beside the original as “name subtitled.mp4” without replacing existing files. The original stays unchanged. Cancelling saves no video.")));
        root.Children.Add(UI.Scroll(body)); Content = root;
        Loaded += (_, _) => { Update(); (speech ? (Control)language : browse).Focus(); };
    }
}
