using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Swivel;

/// Feel & Text (FeelPane.swift): a tuning bench for motion, sound and text. Choices are saved as they are made, and
/// the trials never run a file action. Laid out as Windows 11 setting cards rather than the Mac's boxed groups.
sealed class FeelPage : Pane
{
    /// The Mac's sample actions; AirDrop is Share on Windows.
    static readonly string[] PreviewTitles = ["Copy", "Move", "Share", "Copy Path", "Terminal", "Hang"];
    const double ControlsWidth = 300, StageWidth = 360, StageHeight = 260, ColumnGap = 24;

    readonly ComboBox presets = new() { MinWidth = 180 };
    readonly ComboBox area = new() { MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Left };
    readonly ComboBox style = new() { MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Left };
    readonly ComboBox soundStyle = new() { MinWidth = 180 };
    readonly ComboBox textSize = new() { MinWidth = 180 };
    readonly Slider speed = MakeSlider(0.75, 1.5), bounce = MakeSlider(0, 1), volume = MakeSlider(0, 1);
    readonly TextBlock speedValue = ValueLabel(), bounceValue = ValueLabel(), volumeValue = ValueLabel();
    readonly ToggleSwitch sounds = new(), hoverSound = new(), bold = new();
    readonly PreviewFrame menuPreview = new();
    readonly FeelMotionSample motionSample = new();
    readonly WallpaperBackdrop sampleBackdrop = new();
    readonly TextBlock trialHint = Ui.Text("", 12, tone: Tone.Secondary);
    readonly TextBlock sample = Ui.Text(L("Swivel — comfortable at a glance. 文件 · 写真"), 15);
    readonly List<Button> trials = [];
    (MenuStyle Style, double Scale, bool Bold)? previewKey;
    bool refreshing;
    bool dragging;

    public FeelPage()
    {
        Add(Ui.Title(L("Feel & Text")));
        Add(Ui.Text(L("Too bouncy, too slow or too noisy? Make Swivel comfortable for you. Changes are saved as you go."), tone: Tone.Secondary), 4);

        // Preset
        presets.Items.Add(L("Custom"));
        foreach (var preset in FeelChoices.Presets) presets.Items.Add(preset.Title());
        Describe(presets, "Feel preset");
        presets.SelectionChanged += (_, _) => PresetChosen();
        Add(Ui.Section(L("Start with a preset")));
        Add(new SettingsCard("", L("Preset"), // Equalizer
            L("Original · Calm · Snappy · Playful · Quiet. Presets keep your text size and feedback switches."), presets));

        // Motion: one area at a time, with a live trial beside the sliders.
        Add(Ui.Section(L("Motion")));
        Add(MotionCard());

        // Sound. (The Mac's "Haptic Feedback" and "Gentle trackpad taps" switches are left out: Windows has no public
        // trackpad haptics API. The setting's value is kept in the shared settings shape.)
        Add(Ui.Section(L("Sound")));
        sounds.Toggled += on =>
        {
            Prefs.SoundEffects = on;
            Feedback.Prepare();
        };
        foreach (var choice in FeelChoices.SoundStyles) soundStyle.Items.Add(choice.Title());
        Describe(soundStyle, "Sound style");
        soundStyle.SelectionChanged += (_, _) => AudioChanged(prepare: true);
        Describe(volume, "Sound level");
        volume.ValueChanged += (_, _) => AudioChanged(prepare: !dragging);
        hoverSound.Toggled += _ => AudioChanged(prepare: false);
        var tries = new WrapPanel();
        foreach (var (title, play) in new (string, Action)[]
                 {
                     ("Selection", () => Feedback.Hover()), ("Page turn", Feedback.Page), ("Confirm", Feedback.Press), ("Completion", Feedback.Done),
                 })
        {
            var button = Ui.Button(L(title), play);
            button.Margin = new Thickness(0, 0, 8, 0);
            trials.Add(button);
            tries.Children.Add(button);
        }
        Add(new CardGroup(
            new SettingsCard("", L("Sound effects"), L("Short sounds when a menu opens, an option is chosen or a task finishes."), sounds), // Volume
            new SettingsCard("", L("Sound style"), null, soundStyle), // Audio
            new SettingsCard("", L("Sound level"), null, SliderRow(volume, volumeValue)), // Volume2
            new SettingsCard("", L("Play sounds when selecting items"), null, hoverSound),
            new SettingsCard(null, null, null) { Child = tries, MinHeight = 60 }));
        Add(Note(L("Sound level is relative to Swivel's volume in the Windows volume mixer. Muting Windows or turning off Sound effects silences Swivel.")));

        // Text
        Add(Ui.Section(L("Text")));
        foreach (var size in FeelChoices.TextSizes) textSize.Items.Add(size.Title());
        Describe(textSize, "Interface text size");
        textSize.SelectionChanged += (_, _) => TextChanged();
        bold.Toggled += _ => TextChanged();
        Add(new CardGroup(
            new SettingsCard("", L("Text size"), null, textSize), // Font
            new SettingsCard("", L("Bold interface text"), null, bold), // Bold
            new SettingsCard(null, null, null) { Child = sample, MinHeight = 60 }));
        Add(Note(L("Applies immediately to Swivel windows, tools and menu labels. Text inside exported images is unchanged.")));

        Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { Ui.Button(L("Restore tuning defaults"), Reset) } }, 24);
        Refresh();
        ShowPreview(replay: false);
    }

    FrameworkElement MotionCard()
    {
        foreach (var choice in FeelChoices.Areas) area.Items.Add(choice.Title());
        area.SelectedIndex = 0;
        Describe(area, "Motion area");
        area.SelectionChanged += (_, _) =>
        {
            Refresh();
            ShowPreview(replay: true);
        };
        foreach (var menu in MenuStyles.All) style.Items.Add(menu.Title());
        style.SelectedIndex = Math.Max(Array.IndexOf(MenuStyles.All, Prefs.Style), 0);
        Describe(style, "Preview layout");
        style.SelectionChanged += (_, _) => ShowPreview(replay: false);
        Describe(speed, "Animation speed");
        Describe(bounce, "Animation bounce");
        speed.ValueChanged += (_, _) => MotionChanged();
        bounce.ValueChanged += (_, _) => MotionChanged();

        var controls = new StackPanel { Width = ControlsWidth, HorizontalAlignment = HorizontalAlignment.Left };
        controls.Children.Add(Ui.Text(L("Adjust one area"), 14, FontWeights.SemiBold));
        area.Margin = new Thickness(0, 8, 0, 0);
        controls.Children.Add(area);
        foreach (var (title, slider, value) in new[] { ("Speed", speed, speedValue), ("Bounce", bounce, bounceValue) })
        {
            var label = Ui.Text(L(title), tone: Tone.Secondary);
            label.Margin = new Thickness(0, 16, 0, 0);
            controls.Children.Add(label);
            var row = SliderRow(slider, value);
            row.Margin = new Thickness(0, 4, 0, 0);
            controls.Children.Add(row);
        }
        var hint = Ui.Text(L("Less bounce feels steadier. More speed finishes movement sooner."), 12, tone: Tone.Secondary);
        hint.Margin = new Thickness(0, 12, 0, 0);
        controls.Children.Add(hint);

        // The trial: the live menu for pop-up menus, the replayable sample for the other two areas.
        var stage = new Grid { Width = StageWidth, Height = StageHeight };
        stage.Children.Add(menuPreview);
        sampleBackdrop.Child = motionSample;
        stage.Children.Add(sampleBackdrop);
        style.Margin = new Thickness(0, 10, 0, 0);
        trialHint.Margin = new Thickness(2, 6, 0, 0);
        var trialColumn = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, Children = { stage, style, trialHint } };

        // Side by side when there is room; the trial goes under the sliders in a narrow window.
        var split = new Grid();
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        split.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        split.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        split.Children.Add(controls);
        split.Children.Add(trialColumn);
        void layout(double width)
        {
            bool side = width >= ControlsWidth + ColumnGap + StageWidth;
            Grid.SetColumn(trialColumn, side ? 1 : 0);
            Grid.SetRow(trialColumn, side ? 0 : 1);
            Grid.SetColumnSpan(trialColumn, side ? 1 : 2);
            trialColumn.Margin = side ? new Thickness(ColumnGap, 0, 0, 0) : new Thickness(0, 20, 0, 0);
        }
        split.SizeChanged += (_, e) => layout(e.NewSize.Width);
        layout(ControlsWidth + ColumnGap + StageWidth);

        var footnote = Ui.Text(L("Animation effects in Windows always take priority. Try the menu above, or click the sample to replay."), 12, tone: Tone.Secondary);
        footnote.Margin = new Thickness(0, 16, 0, 0);
        return new SettingsCard(null, null, null)
        {
            Child = new StackPanel { Children = { split, footnote } },
            Padding = new Thickness(20, 18, 20, 16),
        };
    }

    static Slider MakeSlider(double min, double max)
    {
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            SmallChange = 0.05,
            LargeChange = 0.25,
            Width = 200,
            IsMoveToPointEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
        };
        return slider;
    }

    static TextBlock ValueLabel()
    {
        var label = Ui.Text("", 14, wrap: false);
        label.MinWidth = 56;
        label.Margin = new Thickness(12, 0, 0, 0);
        label.VerticalAlignment = VerticalAlignment.Center;
        // Tabular digits, so the value doesn't shift as it changes (the Mac's monospaced-digit font).
        System.Windows.Documents.Typography.SetNumeralAlignment(label, FontNumeralAlignment.Tabular);
        return label;
    }

    StackPanel SliderRow(Slider slider, TextBlock value)
    {
        // A drag writes as it goes; the motion sample replays and the sounds re-tune once it is let go.
        slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => dragging = true));
        slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        {
            dragging = false;
            if (slider == volume) Feedback.Prepare();
            else ReplayTrial();
        }));
        return new StackPanel { Orientation = Orientation.Horizontal, Children = { slider, value } };
    }

    static TextBlock Note(string text)
    {
        var note = Ui.Text(text, 12, tone: Tone.Secondary);
        note.Margin = new Thickness(2, 8, 0, 0);
        return note;
    }

    /// The accessible name of a control without a visible label of its own.
    static void Describe(Control control, string name) => AutomationProperties.SetName(control, L(name));

    FeelArea SelectedArea => FeelChoices.Areas[Math.Max(area.SelectedIndex, 0)];
    MenuStyle PreviewStyle => MenuStyles.All[Math.Max(style.SelectedIndex, 0)];

    internal ComboBox PresetPicker => presets;
    internal ComboBox AreaPicker => area;
    internal ComboBox StylePicker => style;
    internal ComboBox SoundStylePicker => soundStyle;
    internal ComboBox TextSizePicker => textSize;
    internal Slider Speed => speed;
    internal Slider Bounce => bounce;
    internal Slider Volume => volume;
    internal ToggleSwitch Sounds => sounds;
    internal ToggleSwitch HoverSound => hoverSound;
    internal ToggleSwitch Bold => bold;
    internal TextBlock Sample => sample;
    internal TextBlock SpeedValue => speedValue;
    internal PreviewFrame MenuTrial => menuPreview;
    internal FeelMotionSample MotionSample => motionSample;
    internal IReadOnlyList<Button> Trials => trials;

    public override void Refresh()
    {
        refreshing = true;
        var settings = Prefs.Interaction;
        presets.SelectedIndex = settings.MatchingPreset is { } match ? Array.IndexOf(FeelChoices.Presets, match) + 1 : 0;
        var tuning = settings[SelectedArea];
        Show(speed, tuning.Speed);
        Show(bounce, tuning.Bounce);
        Show(volume, settings.Volume);
        speedValue.Text = tuning.Speed.ToString("0.00", CultureInfo.InvariantCulture) + "×";
        bounceValue.Text = L("%d%%", (int)Math.Round(tuning.Bounce * 100));
        volumeValue.Text = L("%d%%", (int)Math.Round(settings.Volume * 100));
        soundStyle.SelectedIndex = Array.IndexOf(FeelChoices.SoundStyles, settings.SoundStyle);
        textSize.SelectedIndex = Math.Max(Array.IndexOf(FeelChoices.TextSizes, settings.TextSize), 0);
        bold.SetQuietly(settings.BoldText);
        sounds.SetQuietly(Prefs.SoundEffects);
        hoverSound.SetQuietly(settings.HoverSound);
        soundStyle.IsEnabled = volume.IsEnabled = hoverSound.IsEnabled = Prefs.SoundEffects;
        refreshing = false;
        // The menu re-lays its labels when the text settings change.
        if (previewKey != null) ShowPreview(replay: false);
    }

    /// Moves a slider to a stored value unless it is already there (a drag's own rounding must not nudge the thumb).
    static void Show(Slider slider, double value)
    {
        if (Math.Abs(slider.Value - value) > 0.004) slider.Value = value;
    }

    void PresetChosen()
    {
        if (refreshing) return;
        if (presets.SelectedIndex <= 0)
        {
            // "Custom" is what the settings are, not something to pick: show the truth again.
            Dispatcher.Later(Refresh);
            return;
        }
        Prefs.Interaction = Prefs.Interaction.Applying(FeelChoices.Presets[presets.SelectedIndex - 1]);
        ReplayTrial();
    }

    void ShowPreview(bool replay)
    {
        bool isMenu = SelectedArea == FeelArea.Menu;
        trialHint.Text = isMenu ? L("Move the pointer over the menu to try it") : L("Click the sample to replay");
        menuPreview.Visibility = isMenu ? Visibility.Visible : Visibility.Collapsed;
        sampleBackdrop.Visibility = isMenu ? Visibility.Collapsed : Visibility.Visible;
        style.Visibility = isMenu ? Visibility.Visible : Visibility.Collapsed;
        var key = (PreviewStyle, AppTypography.Scale, Prefs.Interaction.BoldText);
        if (key != previewKey)
        {
            bool crossfade = previewKey != null;
            previewKey = key;
            menuPreview.Show(MenuPreview.Create(PreviewStyle, PreviewTitles, WheelMode.Actions), crossfade);
        }
        if (!isMenu)
        {
            motionSample.Area = SelectedArea;
            if (replay) motionSample.Replay();
        }
    }

    void MotionChanged()
    {
        if (refreshing) return;
        var tuning = new MotionTuning { Speed = Math.Round(speed.Value, 2), Bounce = Math.Round(bounce.Value, 2) };
        Prefs.Interaction = Prefs.Interaction.With(SelectedArea, tuning);
        if (!dragging) ReplayTrial();
    }

    /// Shows the new feel: the menu opens again (silently), or the sample replays. The menu reads the saved
    /// "Pop-up menus" tuning live, so it needs no override here: every change is saved as it is made.
    void ReplayTrial()
    {
        if (SelectedArea == FeelArea.Menu) (menuPreview.Preview as PreviewHost)?.Replay();
        else motionSample.Replay();
    }

    void AudioChanged(bool prepare)
    {
        if (refreshing) return;
        Prefs.Interaction = Prefs.Interaction with
        {
            SoundStyle = FeelChoices.SoundStyles[Math.Max(soundStyle.SelectedIndex, 0)],
            Volume = Math.Round(volume.Value, 2),
            HoverSound = hoverSound.IsOn,
        };
        if (prepare) Feedback.Prepare();
    }

    void TextChanged()
    {
        if (refreshing) return;
        Prefs.Interaction = Prefs.Interaction with
        {
            TextSize = FeelChoices.TextSizes[Math.Max(textSize.SelectedIndex, 0)],
            BoldText = bold.IsOn,
        };
    }

    internal void Reset()
    {
        Prefs.Interaction = new InteractionSettings();
        Feedback.Prepare();
        ReplayTrial();
    }
}

/// A safe, local trial of the clothesline's fall and the tool spring (FeelMotionPreview): a card drops onto a rope,
/// or rises into place, with the speed and bounce chosen for that area. It never touches the real rope.
sealed class FeelMotionSample : FrameworkElement
{
    FeelArea area = FeelArea.Clothesline;
    Spring spring = new(1, response: 0.34, damping: 0.81);
    double drop, velocity;
    readonly FrameDriver driver = new();

    public FeelMotionSample()
    {
        Focusable = true;
        Cursor = Cursors.Hand;
        SetResourceReference(FocusVisualStyleProperty, "DefaultControlFocusVisualStyle");
        AutomationProperties.SetName(this, L("Replay motion sample"));
        ToolTip = L("Click the sample to replay");
        driver.OnFrame = Advance;
        ThemeWatch.Attach(this);
        Unloaded += (_, _) => Settle();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) Settle();
        };
    }

    public FeelArea Area
    {
        get => area;
        set
        {
            if (area == value) return;
            area = value;
            InvalidateVisual();
        }
    }

    public bool IsMoving => driver.IsRunning;

    void Settle()
    {
        driver.Stop();
        drop = 0;
        velocity = 0;
        spring.Snap(1);
        InvalidateVisual();
    }

    public void Replay()
    {
        if (!IsLoaded || !IsVisible) return;
        if (Motion.Reduced)
        {
            Settle();
            return;
        }
        drop = -80;
        velocity = 0;
        spring.Snap(0);
        spring.Target = 1;
        InvalidateVisual();
        driver.Start();
    }

    bool Advance(double dt)
    {
        if (Motion.Reduced)
        {
            Settle();
            return false;
        }
        var tuning = Prefs.Interaction[area];
        spring.Step(dt, tuning);
        var fall = RopeTuning.Fall.Tuned(tuning);
        double left = Math.Min(dt, 1.0 / 30);
        while (left > 1e-9)
        {
            double h = Math.Min(left, RopeTuning.Step);
            RopeTuning.Advance(ref drop, ref velocity, 0, fall, h: h);
            left -= h;
        }
        InvalidateVisual();
        return IsVisible && IsLoaded && (!spring.IsSettled || Math.Abs(drop) > 0.01 || Math.Abs(velocity) > 0.01);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        Replay();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is not (Key.Enter or Key.Space)) return;
        Replay();
        e.Handled = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // the whole stage takes the click
        bool rope = area == FeelArea.Clothesline;
        // y grows downward here: the card falls from above onto the rope, or rises from below into place.
        double y = h / 2 + (rope ? drop : -(spring.Value - 1) * 50);
        if (rope)
        {
            var path = new StreamGeometry();
            using (var ctx = path.Open())
            {
                ctx.BeginFigure(new Point(18, y - 50), false, false);
                ctx.BezierTo(new Point(100, y - 30), new Point(w - 100, y - 30), new Point(w - 18, y - 50), true, true);
            }
            path.Freeze();
            dc.DrawGeometry(null, new Pen(Ui.Brush(Color.FromArgb(204, 255, 255, 255)), 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, path);
        }
        var card = new Rect(w / 2 - 66, y - 46, 132, 84);
        var fill = Ui.Color(this, "SolidBackgroundFillColorBaseBrush", Color.FromRgb(0xF3, 0xF3, 0xF3));
        var edge = Ui.Color(this, "CardStrokeColorDefaultBrush", Color.FromArgb(0x0F, 0, 0, 0));
        dc.DrawRoundedRectangle(Ui.Brush(fill), new Pen(Ui.Brush(edge), 1), card, 12, 12);
        var title = new FormattedText(rope ? L("Clothesline") : L("Tools & settings"), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(System.Windows.Documents.TextElement.GetFontFamily(this), FontStyles.Normal, AppTypography.Weight(FontWeights.SemiBold), FontStretches.Normal),
            AppTypography.Size(13), Ui.Brush(Ui.Color(this, "TextFillColorPrimaryBrush", Color.FromArgb(0xE4, 0, 0, 0))),
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = card.Width - 16,
            TextAlignment = TextAlignment.Center,
        };
        dc.DrawText(title, new Point(card.X + 8, card.Y + (card.Height - title.Height) / 2));
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new SamplePeer(this);

    sealed class SamplePeer(FeelMotionSample sample) : FrameworkElementAutomationPeer(sample), IInvokeProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;
        protected override string GetClassNameCore() => "FeelMotionSample";
        protected override bool IsControlElementCore() => true;
        public override object? GetPattern(PatternInterface pattern) => pattern == PatternInterface.Invoke ? this : base.GetPattern(pattern);
        public void Invoke() => sample.Replay();
    }
}
