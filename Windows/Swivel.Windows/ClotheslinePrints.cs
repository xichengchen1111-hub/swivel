using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Swivel.Core;

namespace Swivel.Windows;

/// The hung prints' buttons and their interactions (click copy, double-click or hold to
/// preview, drag out, stitch by holding over another print, context menu), shared by the
/// Clothesline window and the top-edge rope.
internal sealed class ClotheslinePrints : IDisposable
{
    internal const string PrintFormat = "Swivel.ClotheslinePrint";
    // One drag at a time across every host, so a print dragged from the rope can stitch in the window and back.
    private static string? dragSource;
    private static (string Top,string Bottom)? pendingStitch;
    private readonly App app;
    private readonly Window host;
    private readonly Func<bool> available;
    private readonly Action<string> report;
    private readonly List<ClotheslinePrintButton> buttons = new();
    private readonly StitchHover hover = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly DispatcherTimer hoverTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
    private (Button Button, Brush Brush, Thickness Thickness)? marked;
    private (Button Button, string Target, StitchHoverState State)? shown;
    private int running;
    /// Width of each thumbnail in the stitch preview.
    internal double PreviewWidth { get; init; } = 72;
    internal readonly Border StitchPreview = new() { Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left };

    internal ClotheslinePrints(App app,Window host,Func<bool> available,Action<string> report)
    {
        this.app = app; this.host = host; this.available = available; this.report = report;
        hoverTimer.Tick += (_,_) => { if (hover.Target is null || shown is null) hoverTimer.Stop(); else if (hover.IsReady(clock.Elapsed) && shown is { } s) Show(s.Button, s.Target, StitchHoverState.Ready); };
    }

    /// A print is pressed, being dragged, has its menu open, or started an action that is still running.
    internal bool Engaged => running > 0 || dragSource is not null || buttons.Any(b => b.IsMouseCaptured || b.ContextMenu?.IsOpen == true);

    internal ClotheslinePrintButton Create(HungPrint print,MenuPalette? palette,double width,double height,double thumbnail)
    {
        var body = new StackPanel(); body.Children.Add(new Image { Source = print.Thumbnail,Width = thumbnail,Height = thumbnail * 103 / 124,Stretch = Stretch.Uniform });
        body.Children.Add(new TextBlock { Text = System.IO.Path.GetFileName(print.Path),TextTrimming = TextTrimming.CharacterEllipsis,FontSize = 12,Margin = new Thickness(0,6,0,0) });
        var button = new ClotheslinePrintButton(available,
            () => Run("file.copy","Copy Files",print.Path),
            () => Run("image.preview","Preview Image",print.Path),
            source => {
                CancelGestures();
                app.Try(() => {
                    var paths = app.Validate(new[] { print.Path });
                    var data = new DataObject(DataFormats.FileDrop,paths); data.SetData(PrintFormat,print.Path);
                    pendingStitch = null; dragSource = print.Path;
                    try { DragDrop.DoDragDrop(source,data,DragDropEffects.Copy); } finally { dragSource = null; }
                });
                // Released while still holding (too early): say why nothing happened.
                bool early = shown is { State: StitchHoverState.Holding };
                ClearHover();
                if (early) report(app.L("Not stitched: hold over the lower half of a print for half a second before releasing."));
                // Run after the drag has ended, never inside the drag loop.
                if (pendingStitch is { } stitch) { pendingStitch = null; Stitch(stitch.Top,stitch.Bottom); }
            }) { ToolTip = print.Path, MinHeight = 36 };
        buttons.Add(button);
        button.Content = body; button.Width = width; button.Height = height; button.Padding = new Thickness(8); button.Margin = new Thickness(0);
        MenuColors.Apply(button,palette);
        System.Windows.Automation.AutomationProperties.SetName(button,System.IO.Path.GetFileName(print.Path));
        var context = new ContextMenu();
        void Item(string label,Action action) { var entry = new MenuItem { Header = app.L(label) }; entry.Click += (_,_) => action(); context.Items.Add(entry); }
        Item("Preview Image",() => Run("image.preview","Preview Image",print.Path)); Item("Copy Files",() => Run("file.copy","Copy Files",print.Path)); Item("Copy Path",() => Run("file.paths","Copy Path",print.Path)); Item("Show in Explorer",() => Run("file.reveal","Show in Explorer",print.Path));
        var below = new MenuItem { Header = app.L("Stitch Another Print Below") };
        foreach (var other in app.Prints.Where(p => !ReferenceEquals(p,print)))
        { var choice = new MenuItem { Header = new TextBlock { Text = System.IO.Path.GetFileName(other.Path) },ToolTip = other.Path }; choice.Click += (_,_) => Stitch(print.Path,other.Path); below.Items.Add(choice); }
        below.IsEnabled = below.Items.Count > 0; context.Items.Add(below);
        Item("Remove Print",() => app.Prints.Remove(print)); button.ContextMenu = context;
        button.AllowDrop = true;
        button.DragEnter += (_,e) => Hover(e,print,button); button.DragOver += (_,e) => Hover(e,print,button);
        button.DragLeave += (_,e) => Leave(e,button);
        button.Drop += (_,e) => DropOn(e,print,button);
        return button;
    }

    private bool CanStitch => host.IsVisible && app.Prefs.IsOn(Feature.Clothesline) && !app.Busy;

    /// Only a drag that started from a hung print, still hung, can stitch.
    private string? Source(DragEventArgs e) => e.Data.GetData(PrintFormat) is string source && dragSource is { } active
        && string.Equals(source,active,StringComparison.OrdinalIgnoreCase) && app.Prints.Any(p => string.Equals(p.Path,source,StringComparison.OrdinalIgnoreCase)) ? source : null;

    private void Hover(DragEventArgs e,HungPrint target,Button button)
    {
        if (!e.Data.GetDataPresent(PrintFormat)) return; // Files from elsewhere hang as usual.
        e.Handled = true;
        var state = CanStitch && Source(e) is { } source ? hover.Update(source,target.Path,e.GetPosition(button).Y,button.ActualHeight,clock.Elapsed) : StitchHoverState.None;
        Show(button,target.Path,state);
        e.Effects = state == StitchHoverState.Ready ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void Leave(DragEventArgs e,Button button)
    {
        if (!e.Data.GetDataPresent(PrintFormat)) return;
        e.Handled = true;
        // WPF also reports leaving the thumbnail or caption inside the print: only leaving the print counts.
        var point = e.GetPosition(button);
        if (point.X > 0 && point.Y > 0 && point.X < button.ActualWidth && point.Y < button.ActualHeight) return;
        bool wasHolding = shown is { State: StitchHoverState.Holding };
        ClearHover();
        if (wasHolding) report(app.L("Not stitched: hold over the lower half of a print for half a second before releasing."));
    }

    private void DropOn(DragEventArgs e,HungPrint target,Button button)
    {
        if (!e.Data.GetDataPresent(PrintFormat)) return;
        e.Handled = true;
        var source = CanStitch ? Source(e) : null;
        var state = source is not null ? hover.Update(source,target.Path,e.GetPosition(button).Y,button.ActualHeight,clock.Elapsed) : StitchHoverState.None;
        ClearHover();
        if (state == StitchHoverState.Ready) { pendingStitch = (target.Path,source!); e.Effects = DragDropEffects.Copy; }
        else e.Effects = DragDropEffects.None;
    }

    /// Highlights the print and shows the two-thumbnail preview; rebuilt only when something changes.
    private void Show(Button button,string target,StitchHoverState state)
    {
        if (state == StitchHoverState.None || dragSource is not { } source) { ClearHover(); return; }
        if (shown is { } last && ReferenceEquals(last.Button,button) && last.Target == target && last.State == state) return;
        if (marked is { } m && !ReferenceEquals(m.Button,button)) Unmark();
        marked ??= (button,button.BorderBrush,button.BorderThickness);
        button.BorderBrush = SystemColors.HighlightBrush;
        button.BorderThickness = new Thickness(0,0,0,state == StitchHoverState.Ready ? 6 : 2);
        BitmapSource? Thumb(string path) => app.Prints.FirstOrDefault(p => string.Equals(p.Path,path,StringComparison.OrdinalIgnoreCase))?.Thumbnail;
        var stack = new StackPanel();
        foreach (var image in new[] { Thumb(target),Thumb(source) }) if (image is not null) stack.Children.Add(new Image { Source = image,Width = PreviewWidth,Stretch = Stretch.Uniform });
        StitchPreview.Child = stack; StitchPreview.Visibility = Visibility.Visible;
        StitchPreview.Opacity = state == StitchHoverState.Ready ? 1 : 0.5;
        report(string.Format(app.L(state == StitchHoverState.Ready ? "Release to save {1} below {0} as a new image." : "Keep holding to stitch {1} below {0}…"),
            System.IO.Path.GetFileName(target),System.IO.Path.GetFileName(source)));
        shown = (button,target,state);
        // The timer only turns a held hover into Ready while the pointer rests.
        if (state == StitchHoverState.Holding) hoverTimer.Start(); else hoverTimer.Stop();
    }

    private void Unmark() { if (marked is { } m) { m.Button.BorderBrush = m.Brush; m.Button.BorderThickness = m.Thickness; marked = null; } }

    internal void ClearHover()
    {
        bool showing = shown is not null;
        hover.Reset(); hoverTimer.Stop(); Unmark(); shown = null;
        StitchPreview.Visibility = Visibility.Collapsed; StitchPreview.Child = null;
        if (showing) report("");
    }

    private async void Stitch(string top,string bottom)
    {
        CancelGestures();
        if (!CanStitch) return;
        await Running(app.RunActionAsync(new("clothesline.stitch","Stitch Images"),new[] { top,bottom },host,report));
    }

    private async void Run(string id,string title,string path)
    {
        CancelGestures();
        if (!host.IsVisible || !app.Prefs.IsOn(Feature.Clothesline)) return;
        await Running(app.RunActionAsync(new(id,title),new[] { path },host,report));
    }

    private async Task Running(Task action) { running++; try { await action; } finally { running--; } }

    internal void CancelGestures() { foreach (var button in buttons) button.CancelPending(); }
    /// Disposes the current buttons before a rebuild.
    internal void Clear() { ClearHover(); foreach (var button in buttons) button.Dispose(); buttons.Clear(); }
    public void Dispose() { Clear(); hoverTimer.Stop(); }
}
