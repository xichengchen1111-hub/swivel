using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Swivel;

/// An editor window: a canvas on top, controls below, Reset / Cancel / Apply at the bottom (port of Editor.swift).
/// `PresentAsync` completes with the saved files when the user applies, or throws `Cancelled` when they close it.
///
/// The UI thread stays free throughout: subclasses decode in `LoadAsync`, render previews with `LiveRender`, and in
/// `ApplyAsync` read their controls first, then hand the full-resolution work to `Sta.Run` / `Task.Run`.
abstract class Editor
{
    static readonly List<Editor> open = [];

    /// How many editors have been presented so far (the wheel uses it to tell whether an action opened a window).
    public static int Presented { get; protected set; }

    public Window Window { get; }
    public string Source { get; }
    /// The rows of controls under the canvas.
    protected readonly StackPanel Controls = new();
    /// Ctrl+Z / Ctrl+Y. Locked while saving.
    public EditorHistory History { get; } = new();

    readonly FrameworkElement canvas;
    readonly Grid root = new();
    readonly ProgressBar loadingSpinner = new() { IsIndeterminate = true, Width = 140, Visibility = Visibility.Collapsed };
    readonly ProgressBar savingSpinner = new() { IsIndeterminate = true, Width = 60, Visibility = Visibility.Collapsed };
    /// Covers the canvas while saving, so a click, drag or draw can't change what Apply already read.
    readonly Border shield = new() { Background = Brushes.Transparent, Cursor = Cursors.Arrow };
    readonly Grid well = new();
    readonly Button applyButton, resetButton, cancelButton;
    readonly List<DetentSlider> sliders = [];
    List<UIElement> disabledWhileSaving = [];
    TaskCompletionSource<List<string>>? completion;
    string title;
    string subtitle;
    bool closed, discardConfirmed, revealedWhileLoading, confirming, finishing;
    Spring canvasFade = new(1, response: 0.15, damping: 1);
    readonly FrameDriver fadeDriver = new();

    /// `LoadAsync` finished; until then the controls can move but nothing renders.
    public bool IsLoaded { get; private set; }
    public bool IsSaving { get; private set; }

    protected Editor(string title, string source, FrameworkElement canvas)
    {
        Source = source;
        this.canvas = canvas;
        this.title = title;
        subtitle = System.IO.Path.GetFileName(source);
        Window = new Window
        {
            Width = 580,
            Height = 700,
            MinWidth = 480,
            MinHeight = 580,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowInTaskbar = true,
            // Topmost until it is first activated: opened from a drop while Explorer is active, Windows may refuse to
            // bring it to the front, and the editor must still show up (the Mac's `.floating` → `.normal` trick).
            Topmost = true,
        };
        try { Window.Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Swivel;component/Swivel.ico")); }
        catch (Exception) { /* No pack resources (tests): the default icon. */ }
        UpdateTitle();

        resetButton = new Button { Content = L("Reset"), MinWidth = 80, IsEnabled = false };
        cancelButton = new Button { Content = L("Cancel"), MinWidth = 80, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        applyButton = new Button { Content = L("Apply"), MinWidth = 80, IsDefault = true, IsEnabled = false, Margin = new Thickness(8, 0, 0, 0) };
        applyButton.SetResourceReference(FrameworkElement.StyleProperty, "AccentButtonStyle");
        resetButton.Click += (_, _) => ResetClicked();
        cancelButton.Click += (_, _) => Window.Close();
        applyButton.Click += (_, _) => ApplyClicked();

        // Windows convention: secondary action on the left, the default action and Cancel on the right.
        var bar = new Grid();
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        savingSpinner.VerticalAlignment = VerticalAlignment.Center;
        savingSpinner.Margin = new Thickness(0, 0, 4, 0);
        Place(bar, resetButton, 0);
        Place(bar, savingSpinner, 2);
        Place(bar, applyButton, 3);
        Place(bar, cancelButton, 4);

        // The canvas sits in a recessed well.
        well.Children.Add(canvas);
        loadingSpinner.HorizontalAlignment = HorizontalAlignment.Center;
        loadingSpinner.VerticalAlignment = VerticalAlignment.Center;
        well.Children.Add(loadingSpinner);
        var wellBorder = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = well, MinHeight = 280, ClipToBounds = true };
        wellBorder.SetResourceReference(Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        wellBorder.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorSecondaryBrush");

        var separator = new Border { Height = 1, Margin = new Thickness(0, 16, 0, 0) };
        separator.SetResourceReference(Border.BackgroundProperty, "DividerStrokeColorDefaultBrush");
        if (separator.Background is null) separator.Background = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80));
        Controls.Margin = new Thickness(0, 16, 0, 0);
        bar.Margin = new Thickness(0, 12, 0, 0);

        root.Margin = new Thickness(20, 16, 20, 18);
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(wellBorder, 0);
        Grid.SetRow(Controls, 1);
        Grid.SetRow(separator, 2);
        Grid.SetRow(bar, 3);
        root.Children.Add(wellBorder);
        root.Children.Add(Controls);
        root.Children.Add(separator);
        root.Children.Add(bar);
        Window.Content = root;

        // Wide enough for "Saving…", so the buttons don't shift when Apply is pressed.
        Window.Loaded += (_, _) =>
        {
            var content = applyButton.Content;
            applyButton.Content = L("Saving…");
            applyButton.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            applyButton.MinWidth = Math.Max(80, applyButton.DesiredSize.Width);
            applyButton.Content = content;
        };
        Window.Activated += (_, _) => { if (Window.Topmost) Window.Topmost = false; };
        Window.Closing += OnClosing;
        Window.Closed += (_, _) =>
        {
            closed = true;
            Finish(null, new Cancelled());
        };
        // Ctrl+Enter applies even from a text field; Enter alone reaches Apply as the default button.
        Window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                e.Handled = true;
                ApplyClicked();
            }
        };
        Window.CommandBindings.Add(new CommandBinding(ApplicationCommands.Undo, (_, _) => History.Undo(), (_, e) => e.CanExecute = History.CanUndo));
        Window.CommandBindings.Add(new CommandBinding(ApplicationCommands.Redo, (_, _) => History.Redo(), (_, e) => e.CanExecute = History.CanRedo));
        Window.InputBindings.Add(new KeyBinding(ApplicationCommands.Redo, Key.Z, ModifierKeys.Control | ModifierKeys.Shift));

        fadeDriver.OnFrame = dt =>
        {
            canvasFade.Step(dt);
            canvas.Opacity = Math.Clamp(canvasFade.Value, 0, 1);
            return !canvasFade.IsSettled;
        };
    }

    static void Place(Grid grid, UIElement element, int column)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    /// The root of the window's content (for offscreen rendering in tests).
    public FrameworkElement Content => root;

    public void SetTitle(string englishTitle)
    {
        title = englishTitle;
        UpdateTitle();
    }

    /// The second part of the title bar (the file name, or "3 images" for a collage).
    public void SetSubtitle(string text)
    {
        subtitle = text;
        UpdateTitle();
    }

    void UpdateTitle()
    {
        // Windows marks unsaved work with an asterisk in the title (the Mac uses the close button's dot).
        var mark = IsLoaded && HasUnsavedWork ? "*" : "";
        Window.Title = $"{mark}{L(title)} – {subtitle}";
    }

    public async Task<List<string>> PresentAsync()
    {
        open.Add(this);
        Presented++;
        // Large photos take a moment to decode. If it's more than a blink, open the window with a spinner in the well
        // rather than leave nothing on screen after the drop.
        var early = new DispatcherTimer(DispatcherPriority.Normal, Window.Dispatcher) { Interval = TimeSpan.FromSeconds(0.12) };
        early.Tick += (_, _) =>
        {
            early.Stop();
            Reveal(loading: true);
        };
        early.Start();
        try
        {
            await LoadAsync();
            early.Stop();
            if (closed) throw new Cancelled();
        }
        catch (Exception)
        {
            early.Stop();
            Abandon();
            throw;
        }
        IsLoaded = true;
        applyButton.IsEnabled = true;
        resetButton.IsEnabled = true;
        completion = new TaskCompletionSource<List<string>>();
        Update();
        // Already open with a spinner: swap in the canvas. Not open yet: open it now.
        if (revealedWhileLoading) FinishLoading(); else Reveal(loading: false);
        return await completion!.Task;
    }

    // MARK: Overridable

    /// Decodes what the editor shows. Runs before the window has content; do the heavy part off the UI thread.
    protected virtual Task LoadAsync() => Task.CompletedTask;
    /// Saves the result and returns the written files.
    protected virtual Task<List<string>> ApplyAsync() => Task.FromResult(new List<string>());
    protected virtual void Reset() { }
    /// Called whenever a control changes (only once loaded).
    protected virtual void Update() { }
    /// A slider drag ended: a good moment for a final, exact preview.
    protected virtual void TrackingEnded() { }
    /// Work that closing without applying would lose; while true, the title shows "*" and Cancel / Esc / closing ask first.
    public virtual bool HasUnsavedWork => false;
    /// The window is closing, either way: stop animations, renders and playback.
    public virtual void DidClose() { }

    /// Call after a change that `HasUnsavedWork` depends on.
    protected void NoteEdited() => UpdateTitle();

    /// For tests and the command line: runs `LoadAsync` without showing the window.
    public async Task LoadForTesting()
    {
        await LoadAsync();
        IsLoaded = true;
    }

    /// For tests: Apply's work, without the window's saving state.
    public Task<List<string>> ApplyForTesting() => ApplyAsync();

    /// For tests: runs Update as a control change would.
    public void UpdateForTesting() => Update();

    // MARK: Plumbing

    void Reveal(bool loading)
    {
        if (closed || Window.IsVisible) return;
        if (loading)
        {
            revealedWhileLoading = true;
            canvasFade.Snap(0);
            canvas.Opacity = 0;
            loadingSpinner.Visibility = Visibility.Visible;
        }
        Window.Show();
        Window.Activate();
        // The window is the feedback now; a busy toast from the action that opened it would otherwise spin on.
        Toast.Shared.HideIfSpinning();
    }

    void FinishLoading()
    {
        loadingSpinner.Visibility = Visibility.Collapsed;
        if (!Window.IsVisible || Motion.Reduced)
        {
            canvasFade.Snap(1);
            canvas.Opacity = 1;
            return;
        }
        canvasFade.Target = 1;
        fadeDriver.Start();
    }

    /// Loading failed or the window was closed during it: tidy up without a result to deliver.
    void Abandon()
    {
        finishing = true;
        Window.Closing -= OnClosing;
        if (Window.IsVisible) Window.Close();
        open.Remove(this);
        fadeDriver.Stop();
        DidClose();
    }

    void ResetClicked()
    {
        if (!IsLoaded || IsSaving) return;
        Reset();
        Update();
        NoteEdited();
    }

    async void ApplyClicked()
    {
        if (!IsLoaded || IsSaving || completion is null) return;
        // Ctrl+Enter pressed mid-drag on the canvas: the drag would go on changing the picture after Apply read it.
        if (Mouse.LeftButton == MouseButtonState.Pressed && !applyButton.IsMouseOver)
        {
            Feedback.Refuse();
            return;
        }
        // A half-typed value (W/H) has to reach the editor before it reads its controls.
        if (Keyboard.FocusedElement is NumberField field) field.Commit();
        SetSaving(true);
        try
        {
            var files = await ApplyAsync();
            // Windows has no trackpad haptics; the success chime comes from the toast that reports the files.
            Finish(files, null);
        }
        catch (Exception ex)
        {
            SetSaving(false);
            if (closed) return;
            Feedback.Refuse();
            Log.Write($"Editor apply failed: {ex}");
            EditorDialog.Alert(Window, ex.Message);
        }
    }

    /// Apply's pressed state lasts as long as the save: a spinner, "Saving…", and nothing else to click — the
    /// controls are disabled, the canvas is covered and Ctrl+Z / Ctrl+Y wait, because `ApplyAsync` has read them all.
    public void SetSaving(bool saving)
    {
        IsSaving = saving;
        applyButton.Content = saving ? L("Saving…") : L("Apply");
        History.IsLocked = saving;
        // In the tree only while saving (a collapsed element can keep answering hit tests until the next render).
        if (saving && !well.Children.Contains(shield)) well.Children.Add(shield);
        else if (!saving) well.Children.Remove(shield);
        savingSpinner.Visibility = saving ? Visibility.Visible : Visibility.Collapsed;
        if (saving)
        {
            disabledWhileSaving = [.. Descendants(Controls).Where(c => c.IsEnabled), resetButton, cancelButton, applyButton];
            disabledWhileSaving = disabledWhileSaving.Where(c => c.IsEnabled).ToList();
            foreach (var control in disabledWhileSaving) control.IsEnabled = false;
        }
        else
        {
            foreach (var control in disabledWhileSaving) control.IsEnabled = true;
            disabledWhileSaving = [];
        }
        CommandManager.InvalidateRequerySuggested();
    }

    static IEnumerable<Control> Descendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is Control control) yield return control;
            else foreach (var inner in Descendants(child)) yield return inner;
        }
    }

    /// A control changed (only acts once loaded).
    protected void ControlChanged()
    {
        if (IsLoaded) Update();
    }

    void Finish(List<string>? files, Exception? error)
    {
        if (completion is not { } pending) return;
        completion = null;
        open.Remove(this);
        fadeDriver.Stop();
        DidClose();
        if (error != null) pending.TrySetException(error);
        else pending.TrySetResult(files ?? []);
        if (error == null)
        {
            // Saved: the window gives way to the toast. (Windows animates the close itself; a non-layered WPF window
            // can't fade its frame the way the Mac editor fades out.)
            finishing = true;
            Window.Close();
        }
    }

    void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (finishing) return;
        // A save can't be abandoned halfway: its file would still appear after "Cancelled".
        if (IsSaving)
        {
            e.Cancel = true;
            return;
        }
        if (!IsLoaded || !HasUnsavedWork || discardConfirmed) return;
        e.Cancel = true;
        if (confirming) return;
        confirming = true;
        Window.Dispatcher.BeginInvoke(() =>
        {
            int choice = EditorDialog.Show(Window, L("Discard your changes?"), null, [L("Keep Editing"), L("Discard")], cancelIndex: 0);
            confirming = false;
            if (choice != 1) return;
            discardConfirmed = true;
            Window.Close();
        });
    }

    // MARK: Control builders

    /// Marks a view to take the row's spare width.
    protected static T Stretch<T>(T view) where T : FrameworkElement
    {
        view.Tag = StretchTag;
        return view;
    }

    static readonly object StretchTag = new();

    /// A row with a right-aligned "Label:" column, as in Settings and inspector panels. `title` is English.
    protected Grid AddRow(string title, params FrameworkElement[] views)
    {
        var row = new Grid { Margin = new Thickness(0, Controls.Children.Count == 0 ? 0 : 10, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        var label = new TextBlock
        {
            Text = title.Length == 0 ? "" : L(title) + (Languages.Resolved == Language.En ? ":" : "："),
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(label, 0);
        row.Children.Add(label);
        for (int i = 0; i < views.Length; i++)
        {
            var view = views[i];
            bool stretch = view is Slider || ReferenceEquals(view.Tag, StretchTag);
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = stretch ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            view.VerticalAlignment = VerticalAlignment.Center;
            if (i > 0) view.Margin = new Thickness(8, view.Margin.Top, view.Margin.Right, view.Margin.Bottom);
            Grid.SetColumn(view, i + 1);
            row.Children.Add(view);
        }
        // Nothing stretches: the last column takes the rest, so the row stays left-aligned.
        if (!row.ColumnDefinitions.Skip(1).Any(c => c.Width.IsStar))
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Controls.Children.Add(row);
        return row;
    }

    /// A slider whose starting value is a soft detent (see `DetentSlider`).
    protected DetentSlider Slider(double min, double max, double value)
    {
        var slider = new DetentSlider { Minimum = min, Maximum = max, Value = value, Neutral = value, MinWidth = 120 };
        slider.ValueChanged += (_, _) => ControlChanged();
        slider.TrackingEnded += () => { if (IsLoaded) TrackingEnded(); };
        sliders.Add(slider);
        return slider;
    }

    /// A slider is being dragged right now.
    protected bool IsTracking => sliders.Any(s => s.IsTracking);

    protected Segments Segments(IReadOnlyList<string> labels)
    {
        var control = new Segments(labels.Select(l => L(l)).ToList());
        control.Changed += ControlChanged;
        return control;
    }

    protected ComboBox Popup(IEnumerable<string> items)
    {
        var popup = new ComboBox { MinWidth = 150 };
        foreach (var item in items) popup.Items.Add(item);
        popup.SelectedIndex = 0;
        popup.SelectionChanged += (_, _) => ControlChanged();
        return popup;
    }

    protected CheckBox Checkbox(string title, bool on)
    {
        var box = new CheckBox { Content = L(title), IsChecked = on, MinWidth = 0 };
        box.Checked += (_, _) => ControlChanged();
        box.Unchecked += (_, _) => ControlChanged();
        return box;
    }

    protected NumberField NumberField(int value) => new() { Value = value, Width = 80 };

    protected ColorWell ColorWell(Color color)
    {
        var well = new ColorWell { Color = color };
        well.Changed += ControlChanged;
        return well;
    }

    /// A secondary-text label.
    protected static TextBlock Label(string text = "", bool secondary = true)
    {
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        if (secondary) label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return label;
    }
}

// MARK: - Undo

/// The editor window's undo history (Ctrl+Z / Ctrl+Y, Ctrl+Shift+Z). An action registered while undoing becomes the
/// redo, as with NSUndoManager. While a save runs it's locked: undo and redo do nothing, so the picture can't change
/// after Apply read it. Registering still works, so a change that lands meanwhile keeps its history.
sealed class EditorHistory
{
    readonly List<Action> undo = [], redo = [];
    bool undoing, redoing;

    public bool IsLocked { get; set; }
    public bool CanUndo => !IsLocked && undo.Count > 0;
    public bool CanRedo => !IsLocked && redo.Count > 0;

    public void Register(Action action)
    {
        if (undoing) redo.Add(action);
        else
        {
            undo.Add(action);
            if (!redoing) redo.Clear();
        }
    }

    public void Undo()
    {
        if (!CanUndo) return;
        var action = undo[^1];
        undo.RemoveAt(undo.Count - 1);
        undoing = true;
        try { action(); } finally { undoing = false; }
    }

    public void Redo()
    {
        if (!CanRedo) return;
        var action = redo[^1];
        redo.RemoveAt(redo.Count - 1);
        redoing = true;
        try { action(); } finally { redoing = false; }
    }

    public void RemoveAll()
    {
        undo.Clear();
        redo.Clear();
    }
}

// MARK: - Slider detent

/// Where a slider sticks while dragged: within `Zone` of `Neutral` the value is pulled onto it, and the step that
/// enters the zone (or jumps across it in one event) asks for one tap. Leaving re-arms only past `Rearm` (twice the
/// zone), so resting on the boundary doesn't buzz. Pure, so it can be tested.
///
/// A default at either end of the slider's travel is an end stop instead (`ForSlider`): the slider's own clamp
/// already holds the value there, so there's no capture band — every value up to the end stays reachable — only a tap
/// as the value arrives exactly at the end.
struct Detent
{
    public double Neutral { get; }
    public double Zone { get; }
    public double Rearm { get; }
    public bool Engaged { get; private set; }
    double last;

    public Detent(double neutral, double range, double at, double fraction = 0.015)
        : this(neutral, Math.Abs(range) * fraction, Math.Abs(range) * fraction * 2, at, true) { }

    Detent(double neutral, double zone, double rearm, double at, bool _)
    {
        Neutral = neutral;
        Zone = zone;
        Rearm = rearm;
        last = at;
        // Starts already engaged if it rests in the detent, so the first move doesn't tap.
        Engaged = Math.Abs(at - neutral) <= zone;
    }

    /// An end stop at `end`: values are never moved; arriving exactly at the end taps once, and it re-arms after
    /// moving `fraction` of the range away.
    public static Detent EndStop(double end, double range, double at, double fraction = 0.015) =>
        new(end, 0, Math.Abs(range) * fraction, at, true);

    /// The detent for a drag starting at `value` on a slider from `min` to `max` whose default is `neutral`.
    public static Detent ForSlider(double neutral, double min, double max, double at) =>
        neutral <= min || neutral >= max ? EndStop(neutral, max - min, at) : new Detent(neutral, max - min, at);

    /// The value to show for a raw slider value, and whether this step should tap.
    public (double Value, bool Tick) Filter(double raw)
    {
        double distance = Math.Abs(raw - Neutral);
        bool crossed = (last - Neutral) * (raw - Neutral) < 0;
        last = raw;
        if (distance <= Zone)
        {
            bool tick = !Engaged;
            Engaged = true;
            return (Neutral, tick);
        }
        bool crossTick = crossed && !Engaged;
        if (distance > Rearm) Engaged = false;
        return (raw, crossTick);
    }
}

/// A slider with a soft detent at its default: while dragging it sticks to the default within ±1.5 % of its range and
/// plays the snap sound once on the way in. A default at one end of the range only snaps on arrival (see `Detent`).
/// Ctrl while dragging turns the detent off (the Mac uses ⌘), as it turns off snapping in Crop. Double-click resets.
/// A press on the track jumps there and keeps dragging, like the Mac slider; a press on the thumb keeps its offset.
sealed class DetentSlider : Slider
{
    public double? Neutral { get; set; }
    /// The mouse went up after a drag (or a double-click reset).
    public event Action? TrackingEnded;
    public bool IsTracking { get; private set; }
    Detent? detent;
    double grabOffset;
    Track? track;

    public DetentSlider()
    {
        IsMoveToPointEnabled = false;
        SmallChange = 0.01;
        Focusable = true;
    }

    protected override void OnMinimumChanged(double oldMinimum, double newMinimum)
    {
        base.OnMinimumChanged(oldMinimum, newMinimum);
        UpdateSteps();
    }

    protected override void OnMaximumChanged(double oldMaximum, double newMaximum)
    {
        base.OnMaximumChanged(oldMaximum, newMaximum);
        UpdateSteps();
    }

    void UpdateSteps()
    {
        // Arrow keys step by 1 % of the range, Page keys by 10 %.
        SmallChange = (Maximum - Minimum) / 100;
        LargeChange = (Maximum - Minimum) / 10;
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        track = GetTemplateChild("PART_Track") as Track;
    }

    double ValueAt(Point p)
    {
        if (track != null && track.ActualWidth > 0) return track.ValueFromPoint(p);
        double width = Math.Max(ActualWidth, 1);
        return Minimum + Math.Clamp(p.X / width, 0, 1) * (Maximum - Minimum);
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!IsEnabled) return;
        e.Handled = true;
        Focus();
        if (e.ClickCount == 2 && Neutral is double neutral)
        {
            Value = neutral;
            TrackingEnded?.Invoke();
            return;
        }
        var p = e.GetPosition((IInputElement?)track ?? this);
        double atPointer = ValueAt(p);
        grabOffset = track?.Thumb is { IsMouseOver: true } ? Value - atPointer : 0;
        IsTracking = true;
        // Built per drag, from the range as it is now (Trim learns its clip's length only after opening).
        detent = Neutral is double n ? Detent.ForSlider(n, Minimum, Maximum, Value) : null;
        CaptureMouse();
        MoveTo(atPointer + grabOffset);
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        if (!IsTracking || !IsMouseCaptured) return;
        MoveTo(ValueAt(e.GetPosition((IInputElement?)track ?? this)) + grabOffset);
        e.Handled = true;
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        if (!IsTracking) return;
        e.Handled = true;
        ReleaseMouseCapture();
        EndTracking();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (IsTracking) EndTracking();
    }

    void EndTracking()
    {
        if (!IsTracking) return;
        IsTracking = false;
        detent = null;
        TrackingEnded?.Invoke();
    }

    /// Only a drag is magnetic: arrow keys and screen readers must be able to step off the default.
    void MoveTo(double raw)
    {
        raw = Math.Clamp(raw, Minimum, Maximum);
        if (detent is Detent d)
        {
            var (value, tick) = d.Filter(raw);
            detent = d;
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                raw = value;
                if (tick) Feedback.Snap();
            }
        }
        if (raw != Value) Value = raw;
    }
}

// MARK: - Live preview

/// Renders previews off the UI thread, newest request wins: while one render runs, later requests replace each other
/// and only the latest runs next. Results arrive in order on the UI thread, and the preview keeps showing the last
/// one meanwhile, so a slider never waits for a render.
sealed class LiveRender<T>
{
    readonly Action<T> deliver;
    readonly Dispatcher dispatcher;
    bool busy;
    Func<T>? queued;
    int generation;

    public LiveRender(Action<T> deliver)
    {
        this.deliver = deliver;
        dispatcher = Dispatcher.CurrentDispatcher;
    }

    public bool IsBusy => busy || queued != null;

    public void Request(Func<T> job)
    {
        if (busy) queued = job;
        else Run(job);
    }

    /// Drops queued work and any result still on its way.
    public void Cancel()
    {
        queued = null;
        generation++;
    }

    void Run(Func<T> job)
    {
        busy = true;
        int token = generation;
        Task.Run(() =>
        {
            T output;
            try { output = job(); }
            catch (Exception ex)
            {
                Log.Write($"Preview render failed: {ex.Message}");
                output = default!;
            }
            dispatcher.BeginInvoke(() => Finished(output, token));
        });
    }

    void Finished(T output, int token)
    {
        busy = false;
        if (token == generation && output is not null) deliver(output);
        if (queued is { } job)
        {
            queued = null;
            Run(job);
        }
    }
}

/// Runs work that needs WPF objects (DrawingVisual, RenderTargetBitmap, FormattedText) on its own STA thread.
static class Sta
{
    public static Task<T> Run<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { done.SetResult(work()); }
            catch (Exception ex) { done.SetException(ex); }
            finally { Dispatcher.FromThread(Thread.CurrentThread)?.InvokeShutdown(); }
        }) { IsBackground = true, Name = "Swivel render" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    /// The same, blocking (for LiveRender jobs, which already run off the UI thread).
    public static T Invoke<T>(Func<T> work) => Run(work).GetAwaiter().GetResult();
}

// MARK: - Pixels and geometry

static class Pixels
{
    /// The image as straight (non-premultiplied) BGRA bytes, top row first.
    public static byte[] Bgra(BitmapSource source, out int width, out int height)
    {
        BitmapSource converted = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        width = converted.PixelWidth;
        height = converted.PixelHeight;
        var pixels = new byte[width * height * 4];
        converted.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    public static BitmapSource FromBgra(byte[] pixels, int width, int height)
    {
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }

    /// Draws into a new bitmap of `width` × `height` pixels (1 DIP = 1 pixel). Call on an STA thread.
    public static BitmapSource Render(int width, int height, Action<DrawingContext> draw, Action<DrawingVisual>? configure = null)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        RenderOptions.SetEdgeMode(visual, EdgeMode.Unspecified);
        configure?.Invoke(visual);
        using (var dc = visual.RenderOpen()) draw(dc);
        var target = new RenderTargetBitmap(Math.Max(1, width), Math.Max(1, height), 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    /// Renders a visual tree (already arranged) to a bitmap.
    public static BitmapSource Render(Visual visual, int width, int height)
    {
        var target = new RenderTargetBitmap(Math.Max(1, width), Math.Max(1, height), 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    /// A copy scaled so its long edge is at most `maxPixels` (or the image itself when it's already small enough).
    public static BitmapSource Downscaled(BitmapSource image, int maxPixels)
    {
        int longEdge = Math.Max(image.PixelWidth, image.PixelHeight);
        if (longEdge <= maxPixels) return image;
        double scale = (double)maxPixels / longEdge;
        var scaled = new TransformedBitmap(image, new ScaleTransform(scale, scale));
        var copy = new WriteableBitmap(scaled);
        copy.Freeze();
        return copy;
    }

    /// Writes bytes next to their final name, then moves them into place, so a failure leaves no half-written file.
    public static void WriteAtomically(string path, byte[] data)
    {
        var temp = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path) ?? "", $".{System.IO.Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temp, data);
            File.Move(temp, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}

static class EditorGeometry
{
    /// `size` scaled to fit `bounds`, centred. Empty when either is empty.
    public static Rect Fit(Size size, Rect bounds)
    {
        if (size.Width <= 0 || size.Height <= 0 || bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return Rect.Empty;
        double scale = Math.Min(bounds.Width / size.Width, bounds.Height / size.Height);
        double w = size.Width * scale, h = size.Height * scale;
        return new Rect(bounds.X + (bounds.Width - w) / 2, bounds.Y + (bounds.Height - h) / 2, w, h);
    }

    /// `rect` inset by `d` on every side (never negative).
    public static Rect Inset(Rect rect, double d) =>
        rect.IsEmpty ? rect : new Rect(rect.X + d, rect.Y + d, Math.Max(0, rect.Width - 2 * d), Math.Max(0, rect.Height - 2 * d));

    /// Edges rounded to whole pixels (not outward, which would push a snapped edge off its line).
    public static Rect RoundedToPixels(Rect r)
    {
        double x0 = Math.Round(r.Left, MidpointRounding.AwayFromZero), y0 = Math.Round(r.Top, MidpointRounding.AwayFromZero);
        double x1 = Math.Round(r.Right, MidpointRounding.AwayFromZero), y1 = Math.Round(r.Bottom, MidpointRounding.AwayFromZero);
        return new Rect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }

    /// Builds a rect from edges that may come in either order.
    public static Rect FromEdges(double x0, double y0, double x1, double y1) =>
        new(Math.Min(x0, x1), Math.Min(y0, y1), Math.Abs(x1 - x0), Math.Abs(y1 - y0));
}

static class EditorFormat
{
    /// A file size the way File Explorer's details pane counts (decimal units, like the Mac's ByteCountFormatter):
    /// "850 bytes", "345 KB", "1.2 MB", "2.35 GB".
    public static string Bytes(long bytes)
    {
        var culture = CultureInfo.CurrentCulture;
        if (bytes < 1000) return L("%d bytes", bytes);
        if (bytes < 1_000_000) return Math.Round(bytes / 1000.0).ToString("0", culture) + " KB";
        if (bytes < 1_000_000_000) return (bytes / 1_000_000.0).ToString("0.#", culture) + " MB";
        return (bytes / 1_000_000_000.0).ToString("0.##", culture) + " GB";
    }

    /// "+0.7", "−0.3", or "0" at neutral, with a real minus sign.
    public static string Signed(double value, int decimals)
    {
        var text = Math.Abs(value).ToString("F" + decimals, CultureInfo.CurrentCulture);
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var parsed) && parsed == 0) return "0";
        return (value > 0 ? "+" : "−") + text;
    }
}

/// Decoded copies of one source, per size cap (0 = full size), shared by Compress's estimates and its Apply. Whoever
/// needs a size first decodes it; anyone asking meanwhile waits for that decode rather than starting a second one.
sealed class DecodedSources
{
    readonly object gate = new();
    readonly Dictionary<int, BitmapSource> images = [];

    /// How many decodes have run (for tests).
    public int DecodeCount { get; private set; }

    public BitmapSource Image(string source, int maxPixels)
    {
        lock (gate)
        {
            if (images.TryGetValue(maxPixels, out var image)) return image;
            image = Images.Load(source, maxPixels == 0 ? null : maxPixels);
            images[maxPixels] = image;
            DecodeCount++;
            return image;
        }
    }
}

// MARK: - Canvas pieces

/// Shows an image aspect-fit with a soft shadow. When `Original` is set, pressing and holding shows it instead, to
/// compare. The images are GPU textures, so a new preview costs an upload, not a CPU resample.
sealed class ImagePreview : Grid
{
    readonly Image back = new() { Stretch = Stretch.Uniform, Margin = new Thickness(12) };
    readonly Image front = new() { Stretch = Stretch.Uniform, Margin = new Thickness(12) };
    BitmapSource? image, original;
    bool comparing;
    Spring fade = new(1, response: 0.15, damping: 1);
    readonly FrameDriver driver = new();

    public ImagePreview()
    {
        Background = Brushes.Transparent;
        foreach (var layer in new[] { back, front })
        {
            RenderOptions.SetBitmapScalingMode(layer, BitmapScalingMode.HighQuality);
            layer.Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.25, BlurRadius = 10, ShadowDepth = 3, Direction = 270 };
            layer.IsHitTestVisible = false;
            Children.Add(layer);
        }
        driver.OnFrame = dt =>
        {
            fade.Step(dt);
            front.Opacity = Math.Clamp(fade.Value, 0, 1);
            if (fade.IsSettled) back.Source = null;
            return !fade.IsSettled;
        };
    }

    public BitmapSource? Image
    {
        get => image;
        set
        {
            image = value;
            Refresh();
        }
    }

    public BitmapSource? Original
    {
        get => original;
        set
        {
            original = value;
            ToolTip = value is null ? null : L("Press and hold to compare with the original");
        }
    }

    /// Where the image is drawn, in this view's coordinates.
    public Rect ImageRect
    {
        get
        {
            var shown = image ?? original;
            if (shown is null) return Rect.Empty;
            return EditorGeometry.Fit(new Size(shown.PixelWidth, shown.PixelHeight), EditorGeometry.Inset(new Rect(RenderSize), 12));
        }
    }

    /// Replaces the image with a short crossfade, for changes that rearrange the picture (a new layout).
    public void SetImage(BitmapSource? newImage, bool fadeIn)
    {
        if (fadeIn && !Motion.Reduced && front.Source != null && newImage != null && !comparing)
        {
            back.Source = front.Source;
            image = newImage;
            front.Source = newImage;
            fade.Snap(0);
            front.Opacity = 0;
            fade.Target = 1;
            driver.Start();
            return;
        }
        Image = newImage;
    }

    void Refresh()
    {
        front.Source = comparing ? (original ?? image) : image;
        if (!driver.IsRunning) front.Opacity = 1;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (original is null) return;
        comparing = true;
        CaptureMouse();
        Refresh();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!comparing) return;
        comparing = false;
        ReleaseMouseCapture();
        Refresh();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (!comparing) return;
        comparing = false;
        Refresh();
    }

    public void Stop() => driver.Stop();
}

/// A canvas that holds a picture (drawn only when it or the layout changes; the GPU scales it) and an overlay redrawn
/// as often as needed (guides, handles, the shape being drawn). The whole area takes the pointer.
abstract class PictureCanvas : FrameworkElement
{
    protected readonly DrawingVisual PictureLayer = new();
    protected readonly ContainerVisual MiddleLayer = new();
    protected readonly DrawingVisual OverlayLayer = new();
    readonly VisualCollection layers;
    BitmapSource? picture;

    protected PictureCanvas()
    {
        layers = new VisualCollection(this) { PictureLayer, MiddleLayer, OverlayLayer };
        RenderOptions.SetBitmapScalingMode(PictureLayer, BitmapScalingMode.HighQuality);
        ClipToBounds = true;
        Focusable = true;
        FocusVisualStyle = null;
    }

    protected virtual double Inset => 12;

    /// The picture shown (often a smaller proxy of the real image).
    public BitmapSource? Picture
    {
        get => picture;
        set
        {
            picture = value;
            OnLayoutChanged();
        }
    }

    /// The size whose aspect the picture is fitted by (the full image's pixels; the proxy has the same shape).
    protected virtual Size PictureSize => picture is null ? Size.Empty : new Size(picture.PixelWidth, picture.PixelHeight);

    /// Where the picture is drawn, in this view's coordinates.
    public Rect ImageRect => picture is null ? Rect.Empty : EditorGeometry.Fit(PictureSize, EditorGeometry.Inset(new Rect(RenderSize), Inset));

    protected override int VisualChildrenCount => layers.Count;

    protected override Visual GetVisualChild(int index) => layers[index];

    protected override void OnRender(DrawingContext dc) => dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        OnLayoutChanged();
    }

    protected virtual void OnLayoutChanged()
    {
        RedrawPicture();
        RedrawOverlay();
    }

    protected void RedrawPicture()
    {
        using var dc = PictureLayer.RenderOpen();
        if (picture != null && !ImageRect.IsEmpty) dc.DrawImage(picture, ImageRect);
    }

    public void RedrawOverlay()
    {
        using var dc = OverlayLayer.RenderOpen();
        DrawOverlay(dc);
    }

    protected abstract void DrawOverlay(DrawingContext dc);

    protected static Color Accent => SystemColors.AccentColor;

    protected static Color WithAlpha(Color c, double alpha) => Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * c.A), c.R, c.G, c.B);

    protected static SolidColorBrush Brush(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }

    protected static Pen Pen(Color c, double width, PenLineCap cap = PenLineCap.Flat)
    {
        var pen = new Pen(Brush(c), width) { StartLineCap = cap, EndLineCap = cap, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }
}

// MARK: - Controls

/// A row of mutually exclusive toggle buttons (NSSegmentedControl). `Changed` fires when the user picks one.
sealed class Segments : StackPanel
{
    readonly List<System.Windows.Controls.Primitives.ToggleButton> buttons = [];
    int selected;
    public event Action? Changed;

    public Segments(IReadOnlyList<string> labels)
    {
        Orientation = Orientation.Horizontal;
        for (int i = 0; i < labels.Count; i++)
        {
            int index = i;
            var button = new System.Windows.Controls.Primitives.ToggleButton
            {
                Content = labels[i],
                MinWidth = 44,
                Padding = new Thickness(10, 4, 10, 5),
                Margin = new Thickness(i == 0 ? 0 : 2, 0, 0, 0),
                IsChecked = i == 0,
            };
            button.Click += (_, _) =>
            {
                bool changed = selected != index;
                SelectedIndex = index;
                if (changed) Changed?.Invoke();
            };
            buttons.Add(button);
            Children.Add(button);
        }
    }

    public int Count => buttons.Count;

    public int SelectedIndex
    {
        get => selected;
        set
        {
            selected = Math.Clamp(value, 0, Math.Max(0, buttons.Count - 1));
            for (int i = 0; i < buttons.Count; i++) buttons[i].IsChecked = i == selected;
        }
    }
}

/// A whole-number field (≥ 1). Enter, Tab or clicking away commits; anything that isn't a number goes back to the
/// last good value. (The Mac field refuses to let go of an invalid value instead.)
sealed class NumberField : TextBox
{
    int value;
    public event Action? Committed;

    public NumberField()
    {
        TextAlignment = TextAlignment.Center;
        VerticalContentAlignment = VerticalAlignment.Center;
        MaxLength = 7;
    }

    public int Value
    {
        get => value;
        set
        {
            this.value = value;
            Text = value.ToString(CultureInfo.CurrentCulture);
        }
    }

    public void Commit()
    {
        if (int.TryParse(Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var parsed) && parsed >= 1)
        {
            bool changed = parsed != value;
            value = parsed;
            if (changed) Committed?.Invoke();
        }
        Text = value.ToString(CultureInfo.CurrentCulture);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            Commit();
            SelectAll();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        Commit();
    }
}

/// A colour swatch button (NSColorWell): a palette of common colours, and "More Colors…" for Windows' colour dialog.
sealed class ColorWell : Button
{
    static readonly Color[] Palette =
    [
        Color.FromRgb(0xFF, 0x3B, 0x30), Color.FromRgb(0xFF, 0x95, 0x00), Color.FromRgb(0xFF, 0xCC, 0x00), Color.FromRgb(0x34, 0xC7, 0x59),
        Color.FromRgb(0x30, 0xB0, 0xC7), Color.FromRgb(0x00, 0x7A, 0xFF), Color.FromRgb(0x58, 0x56, 0xD6), Color.FromRgb(0xAF, 0x52, 0xDE),
        Color.FromRgb(0xFF, 0x2D, 0x55), Color.FromRgb(0xA2, 0x84, 0x5E), Color.FromRgb(0x8E, 0x8E, 0x93), Color.FromRgb(0x1C, 0x1C, 0x1E),
        Colors.White, Color.FromRgb(0xF2, 0xF2, 0xF7), Color.FromRgb(0xD1, 0xD1, 0xD6), Colors.Black,
    ];

    readonly Border swatch = new() { Width = 28, Height = 16, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1) };
    readonly System.Windows.Controls.Primitives.Popup popup = new() { StaysOpen = false, Placement = PlacementMode.Bottom, AllowsTransparency = true };
    Color color;
    public event Action? Changed;

    public ColorWell()
    {
        Padding = new Thickness(6, 4, 6, 4);
        swatch.BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0x80, 0x80, 0x80));
        Content = swatch;
        ToolTip = L("Colors");
        popup.PlacementTarget = this;
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 8, Margin = new Thickness(8) };
        foreach (var c in Palette)
        {
            var choice = new Button
            {
                Width = 26,
                Height = 26,
                Margin = new Thickness(2),
                Padding = new Thickness(0),
                Content = new Border { Background = new SolidColorBrush(c), Width = 18, Height = 18, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0x80, 0x80, 0x80)) },
            };
            choice.Click += (_, _) =>
            {
                popup.IsOpen = false;
                Pick(c);
            };
            grid.Children.Add(choice);
        }
        var more = new Button { Content = L("More Colors…"), Margin = new Thickness(8, 0, 8, 8), HorizontalAlignment = HorizontalAlignment.Stretch };
        more.Click += (_, _) =>
        {
            popup.IsOpen = false;
            using var dialog = new System.Windows.Forms.ColorDialog
            {
                FullOpen = true,
                AnyColor = true,
                Color = System.Drawing.Color.FromArgb(color.R, color.G, color.B),
            };
            var owner = Window.GetWindow(this);
            var handle = owner is null ? IntPtr.Zero : new System.Windows.Interop.WindowInteropHelper(owner).Handle;
            var result = handle == IntPtr.Zero ? dialog.ShowDialog() : dialog.ShowDialog(new Win32Owner(handle));
            if (result == System.Windows.Forms.DialogResult.OK) Pick(Color.FromRgb(dialog.Color.R, dialog.Color.G, dialog.Color.B));
        };
        var panel = new StackPanel();
        panel.Children.Add(grid);
        panel.Children.Add(more);
        var card = new Border { Child = panel, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Margin = new Thickness(0, 4, 0, 0) };
        card.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        if (card.Background is null) card.Background = Brushes.White;
        popup.Child = card;
        Click += (_, _) => popup.IsOpen = !popup.IsOpen;
    }

    public Color Color
    {
        get => color;
        set
        {
            color = value;
            swatch.Background = new SolidColorBrush(value);
        }
    }

    void Pick(Color c)
    {
        bool changed = c != color;
        Color = c;
        if (changed) Changed?.Invoke();
    }

    sealed class Win32Owner(IntPtr handle) : System.Windows.Forms.IWin32Window
    {
        public IntPtr Handle { get; } = handle;
    }
}

/// A small modal question or message over an editor (the Mac shows these as sheets).
static class EditorDialog
{
    /// Returns the index of the button pressed; Esc or closing returns `cancelIndex`.
    public static int Show(Window owner, string message, string? detail, IReadOnlyList<string> buttons, int cancelIndex)
    {
        int result = cancelIndex;
        var dialog = new Window
        {
            Owner = owner.IsVisible ? owner : null,
            Title = L("Swivel"),
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner.IsVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            ShowInTaskbar = false,
            MinWidth = 320,
            MaxWidth = 480,
        };
        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        panel.Children.Add(new TextBlock { Text = message, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(detail))
        {
            var text = new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            panel.Children.Add(text);
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        for (int i = 0; i < buttons.Count; i++)
        {
            int index = i;
            var button = new Button { Content = buttons[i], MinWidth = 90, Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0), IsCancel = i == cancelIndex, IsDefault = i == buttons.Count - 1 };
            if (i == buttons.Count - 1) button.SetResourceReference(FrameworkElement.StyleProperty, "AccentButtonStyle");
            button.Click += (_, _) =>
            {
                result = index;
                dialog.Close();
            };
            row.Children.Add(button);
        }
        panel.Children.Add(row);
        dialog.Content = panel;
        dialog.ShowDialog();
        return result;
    }

    public static void Alert(Window owner, string message) => Show(owner, message, null, [L("OK")], cancelIndex: 0);
}
