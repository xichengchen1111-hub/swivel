using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class RetouchDialog : Window
{
    public IReadOnlyList<RetouchStroke> Strokes { get; private set; } = Array.Empty<RetouchStroke>();
    private readonly SemaphoreSlim gate = new(1,1);
    private CancellationTokenSource? cancellation;
    private bool closed;
    private int revision;
    public RetouchDialog(App app,PixelImage preview,int sourceWidth,int sourceHeight)
    {
        Title = app.L("Retouch"); Width = 1000; Height = 760; MinWidth = 620; MinHeight = 440; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(18) }; var top = new StackPanel();
        top.Children.Add(UI.Text(app.L("Paint over an object to repair the background. Small areas on repeating backgrounds work best. This local repair does not use AI.")));
        top.Children.Add(UI.Text(app.L("Preview is reduced; full-size repair may look different. Only painted pixels in the normalized SDR image change. Save creates a new PNG."),12));
        var brushRow = new WrapPanel(); brushRow.Children.Add(UI.Text(app.L("Brush Size"))); var brush = new Slider { Minimum = .003,Maximum = .15,Value = .025,Width = 230,Margin = new Thickness(12,8,12,8) }; brushRow.Children.Add(brush); System.Windows.Automation.AutomationProperties.SetName(brush,app.L("Brush Size")); top.Children.Add(brushRow); DockPanel.SetDock(top,Dock.Top); root.Children.Add(top);
        var surface = new RetouchSurface(ToBitmap(preview)); brush.ValueChanged += (_,_) => surface.Diameter = brush.Value;
        var footer = new StackPanel(); var status = UI.Text(""); footer.Children.Add(status); var actions = new WrapPanel(); footer.Children.Add(actions);
        var undo = UI.Button(app.L("Undo"),surface.Undo); var redo = UI.Button(app.L("Redo"),surface.Redo); actions.Children.Add(undo); actions.Children.Add(redo); actions.Children.Add(UI.Button(app.L("Clear Marks"),surface.Clear));
        var original = new CheckBox { Content = app.L("Show Original"),Margin = new Thickness(0,8,12,0) }; original.Click += (_,_) => { surface.ShowOriginal = original.IsChecked == true; surface.InvalidateVisual(); }; actions.Children.Add(original);
        var save = UI.Button(app.L("Save Repair"),() => { Strokes = surface.Snapshot(); DialogResult = true; }); save.IsEnabled = false; actions.Children.Add(save);
        var cancel = UI.Button(app.L("Cancel"),() => DialogResult = false); cancel.IsCancel = true; actions.Children.Add(cancel); DockPanel.SetDock(footer,Dock.Bottom); root.Children.Add(footer);
        root.Children.Add(new Viewbox { Child = surface,Stretch = Stretch.Uniform,Margin = new Thickness(0,8,0,8) }); Content = root;
        void Invalidate() { revision++; cancellation?.Cancel(); save.IsEnabled = false; }
        surface.Begin += Invalidate;
        surface.Error += message => status.Text = app.L(message);
        surface.Changed += Render; undo.IsEnabled = redo.IsEnabled = false;
        Closed += (_,_) => { closed = true; Invalidate(); };
        PreviewKeyDown += (_,e) => { if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z) { surface.Undo(); e.Handled = true; } if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y) { surface.Redo(); e.Handled = true; } };
        async void Render()
        {
            Invalidate(); int request = revision; var owned = cancellation = new CancellationTokenSource(); var token = owned.Token; var snapshot = surface.Snapshot();
            undo.IsEnabled = surface.CanUndo; redo.IsEnabled = surface.CanRedo; status.Text = app.L("Updating preview…");
            try
            {
                // Validate full-size region budgets as well as the reduced preview before enabling Save.
                Retouch.Regions(sourceWidth,sourceHeight,snapshot,token);
                await gate.WaitAsync(token); PixelImage result;
                try { result = await Task.Run(() => Retouch.Apply(preview,snapshot,token),token); }
                finally { gate.Release(); }
                if (closed || request != revision) return;
                surface.Preview = ToBitmap(result); surface.InvalidateVisual(); save.IsEnabled = snapshot.Count > 0;
                status.Text = string.Format(app.L("{0} repair stroke(s). Undo removes the last stroke."),snapshot.Count);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!closed && request == revision) { status.Text = ex is UserError error ? app.L(error.Message) : app.L("The repair preview could not finish. Undo the last stroke and try a smaller area."); save.IsEnabled = false; } }
            finally { if (ReferenceEquals(cancellation,owned)) cancellation = null; owned.Dispose(); }
        }
    }
    private static BitmapSource ToBitmap(PixelImage pixels) { var bitmap = BitmapSource.Create(pixels.Width,pixels.Height,96,96,PixelFormats.Bgra32,null,pixels.Bgra,pixels.Width*4); bitmap.Freeze(); return bitmap; }
}

internal sealed class RetouchSurface : FrameworkElement
{
    private readonly BitmapSource original;
    private readonly List<RetouchStroke> strokes = new(),undone = new();
    private List<ImagePoint>? draft;
    private double draftDiameter;
    public BitmapSource Preview { get; set; }
    public bool ShowOriginal { get; set; }
    public double Diameter { get; set; } = .025;
    public bool CanUndo => strokes.Count > 0;
    public bool CanRedo => undone.Count > 0;
    public event Action? Begin,Changed;
    public event Action<string>? Error;
    public RetouchSurface(BitmapSource original)
    {
        this.original = Preview = original; Width = original.PixelWidth; Height = original.PixelHeight; Cursor = Cursors.Cross; Focusable = true;
        MouseLeftButtonDown += (_,e) => {
            if (ShowOriginal) return;
            if (strokes.Count >= 100) { Error?.Invoke("Use up to 100 repair strokes per image."); return; }
            Focus(); if (!CaptureMouse()) return; Begin?.Invoke(); draft = new() { Position(e) }; draftDiameter = Diameter; e.Handled = true; InvalidateVisual();
        };
        MouseMove += (_,e) => { if (draft is null || e.LeftButton != MouseButtonState.Pressed) return; if (draft.Count < 2048) draft.Add(Position(e)); InvalidateVisual(); };
        MouseLeftButtonUp += (_,e) => {
            if (draft is null) return; strokes.Add(new(draft.ToArray(),draftDiameter)); draft = null; undone.Clear(); ReleaseMouseCapture(); InvalidateVisual(); Changed?.Invoke(); e.Handled = true;
        };
        LostMouseCapture += (_,_) => { if (draft is not null) { draft = null; InvalidateVisual(); Changed?.Invoke(); } };
    }
    private ImagePoint Position(MouseEventArgs e) { var p = e.GetPosition(this); return new(Math.Clamp(p.X/Width,0,1),Math.Clamp(p.Y/Height,0,1)); }
    public IReadOnlyList<RetouchStroke> Snapshot() => strokes.Select(s => s with { Points = (ImagePoint[])s.Points.Clone() }).ToArray();
    private void CancelDraft() { draft = null; ReleaseMouseCapture(); }
    public void Undo() { CancelDraft(); if (CanUndo) { undone.Add(strokes[^1]); strokes.RemoveAt(strokes.Count-1); } InvalidateVisual(); Changed?.Invoke(); }
    public void Redo() { CancelDraft(); if (CanRedo) { strokes.Add(undone[^1]); undone.RemoveAt(undone.Count-1); } InvalidateVisual(); Changed?.Invoke(); }
    public void Clear() { CancelDraft(); strokes.Clear(); undone.Clear(); InvalidateVisual(); Changed?.Invoke(); }
    protected override void OnRender(DrawingContext dc)
    {
        dc.PushClip(new RectangleGeometry(new Rect(0,0,Width,Height))); dc.DrawImage(ShowOriginal ? original : Preview,new Rect(0,0,Width,Height));
        if (draft is not null && !ShowOriginal)
        {
            var brush = new SolidColorBrush(Color.FromArgb(110,40,120,240)); double thickness = draftDiameter*Math.Max(Width,Height);
            var pen = new Pen(brush,thickness) { StartLineCap = PenLineCap.Round,EndLineCap = PenLineCap.Round,LineJoin = PenLineJoin.Round };
            Point Point(ImagePoint p) => new(p.X*Width,p.Y*Height);
            if (draft.Count == 1) dc.DrawEllipse(brush,null,Point(draft[0]),thickness/2,thickness/2);
            else { var geometry = new StreamGeometry(); using (var ctx = geometry.Open()) { ctx.BeginFigure(Point(draft[0]),false,false); ctx.PolyLineTo(draft.Skip(1).Select(Point).ToArray(),true,false); } dc.DrawGeometry(null,pen,geometry); }
        }
        dc.Pop();
    }
}
