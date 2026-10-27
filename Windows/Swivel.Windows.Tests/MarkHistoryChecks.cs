using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;
using Swivel.Windows;

internal static class MarkHistoryChecks
{
    public static void Run(Action<bool,string> check)
    {
        var bitmap = BitmapSource.Create(100,80,96,96,PixelFormats.Bgra32,null,new byte[100*80*4],400); bitmap.Freeze();
        var surface = new MarkupSurface(bitmap,false);
        var first = new ImageMark(MarkKind.Text,new[] { new ImagePoint(1,2) },0,2,"first"); surface.Add(first); first.Points[0] = new(99,79);
        var kept = surface.Snapshot()[0]; check(kept.Points[0] == new ImagePoint(1,2),"Mark surface owns immutable source geometry");
        surface.Replace(0,MarkEdit.Apply(kept,new(10,20,0,0),0xff0000,4,"edited\r\n中文",100,80));
        check(surface.Snapshot()[0].Text == "edited\n中文" && surface.SelectedIndex == 0,"Existing mark can be edited with Windows line endings");
        surface.Undo(); check(surface.Snapshot()[0].Text == "first" && surface.Snapshot()[0].Points[0] == new ImagePoint(1,2),"Undo restores exact previous text and geometry");
        surface.Redo(); check(surface.Snapshot()[0].Text == "edited\n中文","Redo restores normalized multilingual edit");
        surface.Select(0); surface.RemoveSelected(); check(surface.Count == 0 && surface.CanUndo,"Delete selected mark is undoable"); surface.Undo();
        surface.Clear(); check(surface.Count == 0 && surface.CanUndo,"Clear retains undo history"); surface.Undo(); check(surface.Count == 1,"Undo Clear restores marks");
        surface.Undo(); surface.Add(kept with { Text = "new branch" }); check(!surface.CanRedo,"New edits discard obsolete redo branch");
        for (int i = 0; i < 25; i++) surface.Replace(0,kept with { Text = "history " + i });
        int undos = 0; while (surface.CanUndo) { surface.Undo(); undos++; }
        check(undos == 20,"Annotation history is bounded at 20 changes");
        var gesture=new MarkupSurface(bitmap,false);var shape=new ImageMark(MarkKind.Rectangle,new[]{new ImagePoint(10,10),new ImagePoint(30,30)},0,2);gesture.Add(shape);
        gesture.SelectionMode=true;gesture.BeginManipulation(0,new(20,20),false);
        for(int i=0;i<20;i++)gesture.UpdateManipulation(new(20+i,20+i),false);
        check(gesture.Snapshot()[0].Points[0]==new ImagePoint(10,10),"In-progress drag leaves exported/undo geometry unchanged");
        gesture.CommitManipulation();check(gesture.Snapshot()[0].Points[0]==new ImagePoint(29,29),"Mouse gesture commits final geometry");
        gesture.Undo();check(gesture.Count==1 && gesture.Snapshot()[0].Points[0]==new ImagePoint(10,10),"One undo restores entire drag");gesture.Undo();check(gesture.Count==0,"Drag creates exactly one history step");gesture.Redo();
        gesture.BeginManipulation(0,new(10,10),false);gesture.UpdateManipulation(new(50,50),false);gesture.CancelInteraction();check(!gesture.HasDraft && gesture.Snapshot()[0].Points[0]==new ImagePoint(10,10),"Escape/lost capture cancel uncommitted geometry");
        gesture.BeginManipulation(0,new(10,10),false);gesture.UpdateManipulation(new(50,50),false);gesture.ShowOriginal=true;check(!gesture.HasDraft && gesture.Snapshot()[0].Points[0]==new ImagePoint(10,10),"Original comparison cancels active manipulation");gesture.ShowOriginal=false;
        gesture.BeginManipulation(0,new(10,10),false);gesture.CommitManipulation();gesture.Undo();check(gesture.Count==0,"Click without movement does not add history");
        var number=shape with{Kind=MarkKind.Number,Points=new[]{new ImagePoint(50,40)},Text="12",Width=4};var nb=MarkupTools.VisualBounds(number);
        check(nb.Contains(new System.Windows.Point(40,40)) && nb.Contains(new System.Windows.Point(60,40)),"Number hit box includes drawn disc");
        var textBounds=MarkupTools.VisualBounds(first);check(textBounds.Width>0 && textBounds.Height>0,"Text hit box uses actual formatted text rather than zero-size anchor");
        var fractional=shape with{Kind=MarkKind.Pen,Points=new[]{new ImagePoint(3.453560802154243,10),new ImagePoint(61.90100307576358,25),new ImagePoint(90.75148266274482,40)}};
        var precise=new MarkupSurface(bitmap,false);precise.Add(fractional);precise.Add(shape);precise.Undo();
        precise.Select(0);precise.BeginManipulation(0,new(50,20),false);precise.UpdateManipulation(new(50,20),false);precise.CommitManipulation();
        check(precise.CanRedo && precise.Snapshot()[0].Points[1]==fractional.Points[1],"Click-only fractional pen keeps redo history and exact coordinates");precise.Redo();check(precise.Count==2,"Redo after no-motion selection restores removed shape");
    }
}
