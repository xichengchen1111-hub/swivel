using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class MarkupDialog : Window
{
    public IReadOnlyList<ImageMark> Marks { get; private set; }=Array.Empty<ImageMark>();
    public MarkupDialog(App app,BitmapSource bitmap,bool redact)
    {
        Title=app.L(redact?"Redact…":"Annotate…");Width=1000;Height=760;MinWidth=650;MinHeight=450;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(18)};var controls=new StackPanel();
        controls.Children.Add(UI.Text(app.L(redact?"Drag rectangles over private details. Covered pixels become solid black in a new PNG; the original stays unchanged.":"Choose a tool and draw on the image. Text is placed by clicking. Save creates a new PNG beside the original.")));
        var row=new WrapPanel();
        var kinds=redact?new[]{MarkKind.Redact}:new[]{MarkKind.Pen,MarkKind.Arrow,MarkKind.Rectangle,MarkKind.Ellipse,MarkKind.Text,MarkKind.Number};
        if (!redact) controls.Children.Add(UI.Text(app.L("Click to place numbered labels. Hold Shift for square rectangles, circles or arrows at 45-degree steps."),12));
        var kind=new ComboBox{ItemsSource=kinds.Select(k=>app.L(k.ToString())).ToArray(),SelectedIndex=0,Width=130,Margin=new Thickness(0,0,10,8)};AutomationProperties.SetName(kind,app.L("Drawing Tool"));row.Children.Add(kind);
        var colors=new uint[]{0xe53935,0xffffff,0x000000,0x1976d2,0xffc107};
        var color=new ComboBox{ItemsSource=new[]{app.L("Red"),app.L("White"),app.L("Black"),app.L("Blue"),app.L("Yellow")},SelectedIndex=0,Width=110,Margin=new Thickness(0,0,10,8),IsEnabled=!redact};AutomationProperties.SetName(color,app.L("Color"));row.Children.Add(color);
        var thickness=new Slider{Minimum=1,Maximum=100,Value=Math.Clamp(Math.Max(bitmap.PixelWidth,bitmap.PixelHeight)/150d,2,30),Width=150,Margin=new Thickness(0,0,10,8),IsEnabled=!redact};AutomationProperties.SetName(thickness,app.L("Stroke / Text Size"));row.Children.Add(thickness);
        var text=new TextBox{MaxLength=200,Width=230,MinHeight=28,Margin=new Thickness(0,0,10,8),IsEnabled=!redact};AutomationProperties.SetName(text,app.L("Annotation Text"));row.Children.Add(text);
        controls.Children.Add(row);var topScroll=UI.Scroll(controls);topScroll.MaxHeight=190;DockPanel.SetDock(topScroll,Dock.Top);root.Children.Add(topScroll);
        var surface=new MarkupSurface(bitmap,redact);
        var selectMode = new CheckBox { Content = app.L("Select / Move Marks"),Margin = new Thickness(0,0,0,8) }; controls.Children.Add(selectMode);
        controls.Children.Add(UI.Text(app.L("In selection mode, click a mark to move it. Drag its corner handle to resize; hold Shift to keep the shape ratio. Text size is changed in Edit Selected Mark. Escape cancels a drag."),12));
        selectMode.Click += (_,_) => { surface.SelectionMode = selectMode.IsChecked == true; row.IsEnabled = !surface.SelectionMode; };
        var footer=new StackPanel();var status=UI.Text("");footer.Children.Add(status);
        var selected = new ComboBox { MinHeight = 30,Margin = new Thickness(0,4,0,8) }; AutomationProperties.SetName(selected,app.L("Select a mark to edit")); footer.Children.Add(selected);
        var editRow = new WrapPanel();
        var edit = UI.Button(app.L("Edit Selected Mark…"),() => app.Try(() => {
            if (surface.SelectedIndex < 0) return;
            int index = surface.SelectedIndex; var dialog = new MarkEditDialog(app,surface.Snapshot()[index],bitmap.PixelWidth,bitmap.PixelHeight) { Owner = this };
            if (dialog.ShowDialog() == true) surface.Replace(index,dialog.Mark);
        })); editRow.Children.Add(edit);
        var remove = UI.Button(app.L("Delete Selected Mark"),surface.RemoveSelected); editRow.Children.Add(remove); footer.Children.Add(editRow);
        bool refreshing = false;
        selected.SelectionChanged += (_,_) => { if (!refreshing) { surface.Select(selected.SelectedIndex); edit.IsEnabled = remove.IsEnabled = surface.SelectedIndex >= 0; } };
        var actions=new WrapPanel();Button? undo=null,redo=null;
        undo=UI.Button(app.L("Undo"),surface.Undo);redo=UI.Button(app.L("Redo"),surface.Redo);actions.Children.Add(undo);actions.Children.Add(redo);
        actions.Children.Add(UI.Button(app.L("Clear Marks"),surface.Clear));
        var original=new CheckBox{Content=app.L("Show Original"),Margin=new Thickness(0,9,16,0)};original.Click+=(_,_)=>{surface.ShowOriginal=original.IsChecked==true;surface.InvalidateVisual();};actions.Children.Add(original);
        var save=UI.Button(app.L("Save Image"),()=>{Marks=surface.Snapshot();DialogResult=true;});actions.Children.Add(save);
        var cancel=UI.Button(app.L("Cancel"),()=>DialogResult=false);cancel.IsCancel=true;actions.Children.Add(cancel);
        footer.Children.Add(actions);var footerScroll=UI.Scroll(footer);footerScroll.MaxHeight=190;DockPanel.SetDock(footerScroll,Dock.Bottom);root.Children.Add(footerScroll);
        SizeChanged+=(_,_)=>{topScroll.MaxHeight=Math.Clamp(ActualHeight*.25,90,190);footerScroll.MaxHeight=Math.Clamp(ActualHeight*.25,100,190);};
        surface.Next=()=>new ImageMark(kinds[kind.SelectedIndex],Array.Empty<ImagePoint>(),colors[color.SelectedIndex],thickness.Value,text.Text);
        surface.Changed+=()=>{
            undo.IsEnabled=surface.CanUndo;redo.IsEnabled=surface.CanRedo;save.IsEnabled=surface.Count>0;
            status.Text=string.Format(app.L("{0} mark(s). Undo keeps the last 20 changes."),surface.Count);
            refreshing = true; selected.ItemsSource = surface.Snapshot().Select((mark,i) => (i+1)+" · "+app.L(mark.Kind.ToString())).ToArray(); selected.SelectedIndex = surface.SelectedIndex; refreshing = false;
            edit.IsEnabled = remove.IsEnabled = surface.SelectedIndex >= 0;
        };
        surface.Error+=message=>status.Text=app.L(message);
        undo.IsEnabled=false;redo.IsEnabled=false;save.IsEnabled=false;edit.IsEnabled=remove.IsEnabled=false;
        root.Children.Add(new Viewbox{Stretch=Stretch.Uniform,Child=surface,Margin=new Thickness(0,10,0,0)});Content=root;
        PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape && surface.HasDraft){surface.CancelInteraction();e.Handled=true;return;}if(Keyboard.Modifiers==ModifierKeys.Control && e.Key==Key.Z && Keyboard.FocusedElement is not TextBox){surface.Undo();e.Handled=true;}if(Keyboard.Modifiers==ModifierKeys.Control && e.Key==Key.Y && Keyboard.FocusedElement is not TextBox){surface.Redo();e.Handled=true;}};
    }
}
internal sealed class MarkupSurface:FrameworkElement
{
    private readonly BitmapSource bitmap;
    private readonly List<ImageMark> marks=new();private readonly List<ImageMark[]> history=new(),future=new();private ImageMark? drawing;private readonly List<ImagePoint> points=new();
    public Func<ImageMark> Next {get;set;}=null!;public event Action? Changed;public event Action<string>? Error;
    private bool showOriginal,selectionMode;
    private ImageMark? dragOriginal,dragPreview; private ImagePoint dragDown; private int dragIndex = -1; private bool dragResize;
    public bool HasDraft => drawing is not null || dragOriginal is not null;
    public bool ShowOriginal { get => showOriginal; set { CancelDraft();showOriginal=value;InvalidateVisual(); } }
    public bool SelectionMode { get => selectionMode; set { CancelDraft();selectionMode=value;Cursor=value?Cursors.Arrow:Cursors.Cross;InvalidateVisual(); } }
    public int Count=>marks.Count;public bool CanUndo=>history.Count>0;public bool CanRedo=>future.Count>0;
    public int SelectedIndex { get; private set; } = -1;
    public MarkupSurface(BitmapSource bitmap,bool redact)
    {
        this.bitmap=bitmap;if(redact)RenderOptions.SetEdgeMode(this,EdgeMode.Aliased);Width=bitmap.PixelWidth;Height=bitmap.PixelHeight;Cursor=Cursors.Cross;Focusable=true;
        MouseLeftButtonDown+=(_,e)=>{
            if(ShowOriginal)return;
            Focus();
            if (SelectionMode)
            {
                var point = Position(e); double grip = GripSize();
                bool resize = SelectedIndex >= 0 && HasHandle(marks[SelectedIndex]) && Handle(marks[SelectedIndex],grip).Contains(new Point(point.X,point.Y));
                int hit = resize ? SelectedIndex : Hit(point,grip/2);
                Select(hit);
                if (hit >= 0) { BeginManipulation(hit,point,resize); if(!CaptureMouse())CancelDraft(); }
                e.Handled=true;return;
            }
            if(marks.Count>=100){Error?.Invoke("Use up to 100 marks per image.");return;}
            drawing=Next();points.Clear();points.Add(Position(e));
            if(drawing.Kind==MarkKind.Number){drawing=drawing with{Text=MarkEdit.NextNumber(marks)};Commit();}
            else if(drawing.Kind==MarkKind.Text){if(string.IsNullOrWhiteSpace(drawing.Text)){drawing=null;Error?.Invoke("Enter annotation text first.");return;}Commit();}
            else {points.Add(points[0]);if(!CaptureMouse())drawing=null;}
            e.Handled=true;InvalidateVisual();
        };
        MouseMove+=(_,e)=>{if(dragOriginal is not null && e.LeftButton==MouseButtonState.Pressed){UpdateManipulation(Position(e),(Keyboard.Modifiers&ModifierKeys.Shift)!=0);e.Handled=true;return;}if(drawing is null||e.LeftButton!=MouseButtonState.Pressed)return;var point=EndPoint(e);if(drawing.Kind==MarkKind.Pen){if(points.Count<2048)points.Add(point);}else points[^1]=point;InvalidateVisual();};
        MouseLeftButtonUp+=(_,e)=>{if(dragOriginal is not null){UpdateManipulation(Position(e),(Keyboard.Modifiers&ModifierKeys.Shift)!=0);CommitManipulation();e.Handled=true;return;}if(drawing is not null){if(drawing.Kind!=MarkKind.Pen)points[^1]=EndPoint(e);Commit();}ReleaseMouseCapture();e.Handled=true;};
        LostMouseCapture+=(_,_)=>{drawing=null;dragOriginal=dragPreview=null;dragIndex=-1;InvalidateVisual();};
    }
    private ImagePoint Position(MouseEventArgs e){var p=e.GetPosition(this);return new(Math.Clamp(p.X,0,Width),Math.Clamp(p.Y,0,Height));}
    private ImagePoint EndPoint(MouseEventArgs e) => drawing is not null && (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? MarkGeometry.Constrain(points[0],Position(e),drawing.Kind,bitmap.PixelWidth,bitmap.PixelHeight) : Position(e);
    private void Commit()
    {
        if(drawing is null)return;
        var mark=drawing with {Points=points.ToArray()};
        if(mark.Kind==MarkKind.Redact){var r=mark.Rectangle(bitmap.PixelWidth,bitmap.PixelHeight);mark=mark with{Points=new[]{new ImagePoint(r.X,r.Y),new ImagePoint(r.X+r.Width,r.Y+r.Height)}};}
        try { mark.Validate(bitmap.PixelWidth,bitmap.PixelHeight); } catch(UserError ex) { drawing=null;Error?.Invoke(ex.Message);InvalidateVisual();return; }
        drawing=null;Add(mark);
    }
    public IReadOnlyList<ImageMark> Snapshot()=>marks.Select(m=>m with{Points=(ImagePoint[])m.Points.Clone()}).ToArray();
    private static void Store(List<ImageMark[]> states,ImageMark[] state) { states.Add(state); if (states.Count > 20) states.RemoveAt(0); }
    private void CancelDraft() { dragOriginal=dragPreview=null;dragIndex=-1;drawing = null; points.Clear(); if (IsMouseCaptured) ReleaseMouseCapture();InvalidateVisual(); }
    private void Remember() { Store(history,marks.ToArray());future.Clear(); }
    public void Add(ImageMark mark)
    {
        if (marks.Count >= 100) throw new UserError("Use up to 100 marks per image."); mark.Validate(bitmap.PixelWidth,bitmap.PixelHeight);
        Remember(); marks.Add(mark with { Points = (ImagePoint[])mark.Points.Clone() }); SelectedIndex = marks.Count-1; InvalidateVisual(); Changed?.Invoke();
    }
    public void Select(int index) { CancelDraft();int next=index>=0 && index<marks.Count?index:-1;if(next==SelectedIndex)return;SelectedIndex=next;InvalidateVisual();Changed?.Invoke(); }
    public void CancelInteraction() => CancelDraft();
    private static bool HasHandle(ImageMark mark) { var f=MarkEdit.Frame(mark);return mark.Kind is not MarkKind.Text and not MarkKind.Number && (f.Width>0 || f.Height>0); }
    private static Rect Handle(ImageMark mark,double grip) { var f=MarkEdit.Frame(mark);return new(f.X+f.Width-grip/2,f.Y+f.Height-grip/2,grip,grip); }
    private double GripSize()
    {
        // Keep the handle near 12 device-independent screen pixels through Viewbox scaling.
        var source=PresentationSource.FromVisual(this);if(source?.RootVisual is Visual root){var t=TransformToAncestor(root);var a=t.Transform(new Point(0,0));var b=t.Transform(new Point(1,0));return 12/Math.Max(.001,(b-a).Length);}return 12;
    }
    private int Hit(ImagePoint point,double tolerance)
    {
        for(int i=marks.Count-1;i>=0;i--){var bounds=MarkupTools.VisualBounds(marks[i]);bounds.Inflate(tolerance,tolerance);if(bounds.Contains(new Point(point.X,point.Y)))return i;}return -1;
    }
    internal void BeginManipulation(int index,ImagePoint point,bool resize)
    {
        CancelDraft();if(index<0||index>=marks.Count)return;SelectedIndex=index;dragIndex=index;dragOriginal=marks[index];dragPreview=dragOriginal;dragDown=point;dragResize=resize;
    }
    internal void UpdateManipulation(ImagePoint point,bool keepAspect)
    {
        if(dragOriginal is null)return;
        try { dragPreview=MarkDrag.Update(dragOriginal,dragDown,point,dragResize,keepAspect,bitmap.PixelWidth,bitmap.PixelHeight);InvalidateVisual(); }
        catch(UserError ex){Error?.Invoke(ex.Message);}
    }
    internal void CommitManipulation()
    {
        int index=dragIndex;var original=dragOriginal;var result=dragPreview;
        CancelDraft();
        if(index>=0 && original is not null && result is not null && !original.Points.SequenceEqual(result.Points))Replace(index,result);
        InvalidateVisual();
    }
    public void Replace(int index,ImageMark mark)
    {
        CancelDraft(); if (index < 0 || index >= marks.Count) return; mark.Validate(bitmap.PixelWidth,bitmap.PixelHeight);
        Remember(); marks[index] = mark with { Points = (ImagePoint[])mark.Points.Clone() }; SelectedIndex = index; InvalidateVisual();Changed?.Invoke();
    }
    public void RemoveSelected() { CancelDraft();if (SelectedIndex < 0)return;Remember();marks.RemoveAt(SelectedIndex);SelectedIndex=Math.Min(SelectedIndex,marks.Count-1);InvalidateVisual();Changed?.Invoke(); }
    public void Undo(){CancelDraft();if(!CanUndo)return;Store(future,marks.ToArray());marks.Clear();marks.AddRange(history[^1]);history.RemoveAt(history.Count-1);SelectedIndex=-1;InvalidateVisual();Changed?.Invoke();}
    public void Redo(){CancelDraft();if(!CanRedo)return;Store(history,marks.ToArray());marks.Clear();marks.AddRange(future[^1]);future.RemoveAt(future.Count-1);SelectedIndex=-1;InvalidateVisual();Changed?.Invoke();}
    public void Clear(){CancelDraft();if(marks.Count==0)return;Remember();marks.Clear();SelectedIndex=-1;InvalidateVisual();Changed?.Invoke();}
    protected override void OnRender(DrawingContext dc)
    {
        dc.PushClip(new RectangleGeometry(new Rect(0,0,Width,Height)));dc.DrawImage(bitmap,new Rect(0,0,Width,Height));
        if(!ShowOriginal){for(int i=0;i<marks.Count;i++)MarkupTools.Draw(dc,i==dragIndex && dragPreview is not null?dragPreview:marks[i]);if(drawing is not null && points.Count>1)MarkupTools.Draw(dc,drawing with{Points=points.ToArray()});}
        if (!ShowOriginal && SelectedIndex >= 0)
        {
            var selectedMark=dragPreview??marks[SelectedIndex];var box=MarkupTools.VisualBounds(selectedMark);double grip=GripSize();box.Inflate(grip/4,grip/4);
            dc.DrawRectangle(null,new Pen(Brushes.White,3),box);dc.DrawRectangle(null,new Pen(Brushes.Black,1) { DashStyle = DashStyles.Dash },box);
            if(SelectionMode && HasHandle(selectedMark))dc.DrawRectangle(Brushes.White,new Pen(Brushes.Black,grip/8),Handle(selectedMark,grip));
        }
        dc.Pop();
    }
}
