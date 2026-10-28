using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed record HungPrint(string Path,BitmapSource Thumbnail);

internal sealed class ClotheslineWindow : Window
{
    private readonly App app;
    private readonly MenuMotion motion;
    private readonly TextBlock status = UI.Text("");
    private readonly List<ClotheslinePrintButton> printButtons = new();
    // Dragging one print onto the lower half of another (held half a second) stitches it below.
    internal const string PrintFormat = "Swivel.ClotheslinePrint";
    private readonly StitchHover hover = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly DispatcherTimer hoverTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly Border stitchPreview = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
    private (Button Button, Brush Brush, Thickness Thickness)? marked;
    private (string Top, string Bottom)? pendingStitch;
    public ClotheslineWindow(App app)
    {
        this.app = app; motion = new MenuMotion(() => app.Prefs.AnimateMenus);
        Width = 1060; Height = 650; MinWidth = 650; MinHeight = 440; Topmost = true; ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        app.Prints.CollectionChanged += Changed;
        hoverTimer.Tick += (_,_) => { if (hover.Target is null) hoverTimer.Stop(); else if (hover.IsReady(clock.Elapsed) && marked is { } m) Mark(m.Button, StitchHoverState.Ready); };
        Deactivated += (_,_) => CancelPrintGestures();
        IsVisibleChanged += (_,_) => { if (!IsVisible) CancelPrintGestures(); };
        Closed += (_,_) => { app.Prints.CollectionChanged -= Changed; ClearHover(); DisposePrintButtons(); motion.Dispose(); };
        PreviewKeyDown += (_,e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        Refresh();
    }
    private void Changed(object? sender,NotifyCollectionChangedEventArgs e) => Refresh();
    public void Refresh()
    {
        ClearHover(); DisposePrintButtons();
        motion.Reset(); Title = app.L("Clothesline");
        // Detach the shared status element before constructing its new parent.
        if (status.Parent is Panel previous) previous.Children.Remove(status);
        var root = new DockPanel { Margin = new Thickness(18) };
        var top = new StackPanel(); top.Children.Add(UI.Text(app.L("Drop images to hang them. Click to copy the file, double-click or hold for 0.6 seconds to preview, or drag out to copy. Right-click for more actions. A mouse click waits for the system double-click interval; Enter or Space copies immediately.")));
        top.Children.Add(UI.Text(app.L("To stitch two images, drag one onto the lower half of another and hold for half a second, then release: it is saved below that image as a new PNG. Or right-click a print and choose Stitch Another Print Below."),12));
        top.Children.Add(UI.Text(app.L("Thumbnails are snapshots from when you hang an image; file actions use its current path. Remove and hang again to refresh. Closing this window keeps the prints until Swivel quits."),12));
        var row = new WrapPanel();
        row.Children.Add(UI.Button(app.L("Hang Images…"),() => app.Try(() => Hang(UI.Pick(this)))));
        row.Children.Add(UI.Button(app.L("Image Arrival Cards…"),app.OpenImageArrivalCards));
        row.Children.Add(UI.Button(app.L("Clear Prints"),app.ClearPrints));
        row.Children.Add(UI.Button(app.L("Cancel Operation"),app.CancelWork));
        var stay = new CheckBox { Content = app.L("Stay on top"),IsChecked = Topmost,VerticalAlignment = VerticalAlignment.Center }; stay.Click += (_,_) => Topmost = stay.IsChecked == true; row.Children.Add(stay);
        top.Children.Add(row); DockPanel.SetDock(top,Dock.Top); root.Children.Add(top);
        var footer = new StackPanel(); footer.Children.Add(UI.Text(app.L("Manual images and explicitly started folder arrival cards are available. Screen-edge activation and full rope physics are still planned."),12)); footer.Children.Add(status); if (stitchPreview.Parent is Panel old) old.Children.Remove(stitchPreview); footer.Children.Add(stitchPreview); DockPanel.SetDock(footer,Dock.Bottom); root.Children.Add(footer);
        var canvas = new Canvas { Width = 1000,Height = 400 };
        var palette = MenuColors.Active(app,MenuTemplate.Clothesline);
        foreach (double y in new[] { 34d,219d }) canvas.Children.Add(new Line { X1 = 5,Y1 = y,X2 = 995,Y2 = y,Stroke = palette is null ? SystemColors.ControlDarkBrush : MenuColors.Brush(palette.Rope),StrokeThickness = 2 });
        var layout = ClotheslineRules.Layout(app.Prints.Count);
        for (int i = 0; i < app.Prints.Count; i++)
        {
            var print = app.Prints[i]; var tile = layout[i];
            var body = new StackPanel(); body.Children.Add(new Image { Source = print.Thumbnail,Width = 124,Height = 103,Stretch = Stretch.Uniform });
            body.Children.Add(new TextBlock { Text = System.IO.Path.GetFileName(print.Path),TextTrimming = TextTrimming.CharacterEllipsis,FontSize = 12,Margin = new Thickness(0,6,0,0) });
            var button = new ClotheslinePrintButton(
                () => IsActive && IsVisible && app.Prefs.IsOn(Feature.Clothesline) && !app.Busy,
                () => Run("file.copy","Copy Files",print.Path),
                () => Run("image.preview","Preview Image",print.Path),
                source => {
                    CancelPrintGestures();
                    app.Try(() => {
                        var paths = app.Validate(new[] { print.Path });
                        var data = new DataObject(DataFormats.FileDrop,paths); data.SetData(PrintFormat,print.Path);
                        pendingStitch = null;
                        DragDrop.DoDragDrop(source,data,DragDropEffects.Copy);
                    });
                    ClearHover();
                    // Run after the drag has ended, never inside the drag loop.
                    if (pendingStitch is { } stitch) { pendingStitch = null; Stitch(stitch.Top,stitch.Bottom); }
                }) { ToolTip = print.Path, MinHeight = 36 };
            printButtons.Add(button);
            button.Content = body; button.Width = tile.Width; button.Height = tile.Height; button.Padding = new Thickness(8); button.Margin = new Thickness(0);
            MenuColors.Apply(button,palette);
            System.Windows.Automation.AutomationProperties.SetName(button,System.IO.Path.GetFileName(print.Path));
            var context = new ContextMenu();
            void Item(string label,Action action) { var entry = new MenuItem { Header = app.L(label) }; entry.Click += (_,_) => action(); context.Items.Add(entry); }
            Item("Preview Image",() => Run("image.preview","Preview Image",print.Path)); Item("Copy Files",() => Run("file.copy","Copy Files",print.Path)); Item("Copy Path",() => Run("file.paths","Copy Path",print.Path)); Item("Show in Explorer",() => Run("file.reveal","Show in Explorer",print.Path));
            var below = new MenuItem { Header = app.L("Stitch Another Print Below") };
            foreach (var other in app.Prints.Where(p => !ReferenceEquals(p,print)))
            { var choice = new MenuItem { Header = System.IO.Path.GetFileName(other.Path) }; choice.Click += (_,_) => Stitch(print.Path,other.Path); below.Items.Add(choice); }
            below.IsEnabled = below.Items.Count > 0; context.Items.Add(below);
            Item("Remove Print",() => app.Prints.Remove(print)); button.ContextMenu = context;
            button.AllowDrop = true;
            button.DragEnter += (_,e) => Hover(e,print,button); button.DragOver += (_,e) => Hover(e,print,button);
            button.DragLeave += (_,e) => { if (e.Data.GetDataPresent(PrintFormat)) { ClearHover(); e.Handled = true; } };
            button.Drop += (_,e) => DropOn(e,print,button);
            motion.Attach(button,tile.Angle); Canvas.SetLeft(button,tile.X); Canvas.SetTop(button,tile.Y); canvas.Children.Add(button);
            var peg = new Rectangle { Width = 9,Height = 27,Fill = palette is null ? SystemColors.ControlDarkBrush : MenuColors.Brush(palette.Peg),IsHitTestVisible = false }; Canvas.SetLeft(peg,tile.X+tile.Width/2-4.5); Canvas.SetTop(peg,tile.Y-22); canvas.Children.Add(peg);
        }
        root.Children.Add(new Viewbox { Child = canvas,Stretch = Stretch.Uniform,StretchDirection = StretchDirection.DownOnly });
        UI.DropFiles(root,app,Hang); Content = root;
    }
    private bool CanStitch => IsVisible && app.Prefs.IsOn(Feature.Clothesline) && !app.Busy;

    private void Hover(DragEventArgs e,HungPrint target,Button button)
    {
        if (e.Data.GetData(PrintFormat) is not string source) return; // Files from elsewhere hang as usual.
        e.Handled = true;
        var state = CanStitch ? hover.Update(source,target.Path,e.GetPosition(button).Y,button.ActualHeight,clock.Elapsed) : StitchHoverState.None;
        if (state == StitchHoverState.None) ClearHover(); else { Mark(button,state); ShowPreview(target.Path,source,state); hoverTimer.Start(); }
        e.Effects = state == StitchHoverState.Ready ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void DropOn(DragEventArgs e,HungPrint target,Button button)
    {
        if (e.Data.GetData(PrintFormat) is not string source) return;
        e.Handled = true;
        var state = CanStitch ? hover.Update(source,target.Path,e.GetPosition(button).Y,button.ActualHeight,clock.Elapsed) : StitchHoverState.None;
        ClearHover();
        if (state == StitchHoverState.Ready) { pendingStitch = (target.Path,source); e.Effects = DragDropEffects.Copy; }
        else { e.Effects = DragDropEffects.None; if (!string.Equals(source,target.Path,StringComparison.OrdinalIgnoreCase)) status.Text = app.L("Not stitched: hold over the lower half of a print for half a second before releasing."); }
    }

    private void Mark(Button button,StitchHoverState state)
    {
        if (marked is { } m && !ReferenceEquals(m.Button,button)) Unmark();
        marked ??= (button,button.BorderBrush,button.BorderThickness);
        button.BorderBrush = SystemColors.HighlightBrush;
        button.BorderThickness = new Thickness(0,0,0,state == StitchHoverState.Ready ? 6 : 2);
        if (hover.Target is { } target && state == StitchHoverState.Ready && stitchPreview.Tag is string source && stitchPreview.Visibility == Visibility.Visible) ShowPreview(target,source,state);
    }

    private void Unmark() { if (marked is { } m) { m.Button.BorderBrush = m.Brush; m.Button.BorderThickness = m.Thickness; marked = null; } }

    private void ShowPreview(string top,string bottom,StitchHoverState state)
    {
        BitmapSource? Thumb(string path) => app.Prints.FirstOrDefault(p => string.Equals(p.Path,path,StringComparison.OrdinalIgnoreCase))?.Thumbnail;
        var stack = new StackPanel();
        foreach (var image in new[] { Thumb(top),Thumb(bottom) }) if (image is not null) stack.Children.Add(new Image { Source = image,Width = 72,Stretch = Stretch.Uniform });
        stitchPreview.Child = stack; stitchPreview.Tag = bottom; stitchPreview.Visibility = Visibility.Visible;
        stitchPreview.Opacity = state == StitchHoverState.Ready ? 1 : 0.5;
        status.Text = string.Format(app.L(state == StitchHoverState.Ready ? "Release to save {1} below {0} as a new image." : "Keep holding to stitch {1} below {0}…"),
            System.IO.Path.GetFileName(top),System.IO.Path.GetFileName(bottom));
    }

    private void ClearHover()
    {
        hover.Reset(); hoverTimer.Stop(); Unmark();
        stitchPreview.Visibility = Visibility.Collapsed; stitchPreview.Child = null; stitchPreview.Tag = null;
    }

    private async void Stitch(string top,string bottom)
    {
        CancelPrintGestures();
        if (!CanStitch) return;
        await app.RunActionAsync(new("clothesline.stitch","Stitch Images"),new[] { top,bottom },this,text => status.Text = text);
    }

    private void CancelPrintGestures() { foreach (var button in printButtons) button.CancelPending(); }
    private void DisposePrintButtons() { foreach (var button in printButtons) button.Dispose(); printButtons.Clear(); }
    private async void Run(string id,string title,string path)
    {
        CancelPrintGestures();
        if (!IsVisible || !app.Prefs.IsOn(Feature.Clothesline)) return;
        await app.RunActionAsync(new(id,title),new[] { path },this,text => status.Text = text);
    }
    private async void Hang(string[] paths) => await app.RunActionAsync(new("clothesline.add","Hang on Clothesline"),paths,this,text => status.Text = text);
}

// Retains native Button keyboard, automation, focus and mouse capture behaviour.
// The timer runs only while this print has a held press or a pending single click.
internal sealed class ClotheslinePrintButton : Button, IDisposable
{
    private readonly Func<bool> available;
    private readonly Action copy, preview;
    private readonly Action<Button> drag;
    private readonly ClotheslineGesture gesture;
    private readonly DispatcherTimer timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(25) };
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private Point start;
    private bool mouseRelease, disposed;

    internal ClotheslinePrintButton(Func<bool> available,Action copy,Action preview,Action<Button> drag)
    {
        this.available=available;this.copy=copy;this.preview=preview;this.drag=drag;
        gesture=new(TimeSpan.FromMilliseconds(Math.Clamp(System.Windows.Forms.SystemInformation.DoubleClickTime,1,10000)));
        ClickMode=ClickMode.Release;
        timer.Tick+=Tick;
        Unloaded+=(_,_)=>CancelPending();
        IsEnabledChanged+=(_,_)=>{if(!IsEnabled)CancelPending();};
    }

    internal void CancelPending()
    {
        gesture.Cancel();timer.Stop();
        if(IsMouseCaptured)ReleaseMouseCapture();
    }

    private void SyncTimer() { if(!disposed&&gesture.Waiting)timer.Start();else timer.Stop(); }
    private bool PointerWithin(Point point)=>point.X>=0&&point.Y>=0&&point.X<=ActualWidth&&point.Y<=ActualHeight;
    private void Tick(object? sender,EventArgs args)
    {
        if(disposed||!IsEnabled||!available()||gesture.Pressed&&(!IsMouseCaptured||Mouse.LeftButton!=MouseButtonState.Pressed)) { CancelPending();return; }
        // WPF can report IsMouseOver=true while the button owns capture even
        // outside its bounds. A held gesture needs the actual local coordinates.
        if(gesture.Pressed&&!PointerWithin(Mouse.GetPosition(this))){gesture.Leave();SyncTimer();return;}
        var action=gesture.Tick(clock.Elapsed);SyncTimer();
        if(action==ClotheslineGestureAction.None)return;
        CancelPending();
        if(action==ClotheslineGestureAction.Preview)preview();else copy();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if(disposed||!IsEnabled||!available()){CancelPending();e.Handled=true;return;}
        var action=gesture.Begin(clock.Elapsed,e.ClickCount==2);
        if(action==ClotheslineGestureAction.Preview)
        {
            CancelPending();e.Handled=true;Focus();preview();return;
        }
        start=e.GetPosition(this);
        base.OnMouseLeftButtonDown(e);
        if(!IsMouseCaptured)gesture.Cancel();SyncTimer();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        // ButtonBase releases capture before raising Click. That expected capture
        // loss must not cancel the pending mouse intent before OnClick receives it.
        mouseRelease=true;
        try { base.OnMouseLeftButtonUp(e); }
        finally { mouseRelease=false;gesture.EndPress();SyncTimer(); }
    }

    protected override void OnClick()
    {
        if(disposed||!IsEnabled||!available()){CancelPending();return;}
        base.OnClick();
        if(mouseRelease)
        {
            var action=gesture.CompleteClick(clock.Elapsed);SyncTimer();
            if(action==ClotheslineGestureAction.Preview){CancelPending();preview();}
        }
        else { CancelPending();copy(); } // Keyboard and UI Automation invoke.
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        if(!gesture.Pressed||!IsMouseCaptured||e.LeftButton!=MouseButtonState.Pressed)return;
        Point point=e.GetPosition(this);
        if(!PointerWithin(point)){gesture.Leave();SyncTimer();}
        if(Math.Abs(point.X-start.X)<SystemParameters.MinimumHorizontalDragDistance&&Math.Abs(point.Y-start.Y)<SystemParameters.MinimumVerticalDragDistance)return;
        bool canDrag=!disposed&&available();CancelPending();
        if(canDrag){e.Handled=true;drag(this);}
    }

    protected override void OnMouseLeave(MouseEventArgs e) { gesture.Leave();SyncTimer();base.OnMouseLeave(e); }
    protected override void OnLostMouseCapture(MouseEventArgs e) { if(!mouseRelease)CancelPending();base.OnLostMouseCapture(e); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { CancelPending();base.OnLostKeyboardFocus(e); }
    protected override void OnPreviewMouseRightButtonDown(MouseButtonEventArgs e) { CancelPending();base.OnPreviewMouseRightButtonDown(e); }
    protected override void OnPreviewKeyDown(KeyEventArgs e) { CancelPending();base.OnPreviewKeyDown(e); }
    protected override void OnContextMenuOpening(ContextMenuEventArgs e) { CancelPending();base.OnContextMenuOpening(e); }
    public void Dispose() { if(disposed)return;disposed=true;CancelPending();timer.Tick-=Tick;clock.Stop(); }
}
