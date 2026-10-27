using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;

namespace Swivel.Windows;

public sealed class CollageWork:IDisposable
{
    private readonly List<ImageTools.Document> tiles=new();
    public BitmapSource Bitmap {get;private set;}=null!;
    private CollageWork(){}
    public static CollageWork Open(IReadOnlyList<string> paths,CollageSettings settings,CancellationToken token)
    {
        var frames=settings.Layout(paths.Count);var work=new CollageWork();
        try
        {
            var visual=new DrawingVisual();
            using(var dc=visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb((byte)(settings.Background>>16),(byte)(settings.Background>>8),(byte)settings.Background)),null,new Rect(0,0,settings.Width,settings.Height));
                for(int i=0;i<paths.Count;i++)
                {
                    token.ThrowIfCancellationRequested();var rect=frames[i];
                    // Decode one bounded source at a time, then retain only cell-sized pixels
                    // and the file lease. Total retained tile pixels cannot exceed the canvas.
                    var tile=ImageTools.OpenTile(paths[i],rect.Width,rect.Height,settings.Fill,token);work.tiles.Add(tile);
                    double scale=settings.Fill ? Math.Max((double)rect.Width/tile.Bitmap.PixelWidth,(double)rect.Height/tile.Bitmap.PixelHeight) : Math.Min((double)rect.Width/tile.Bitmap.PixelWidth,(double)rect.Height/tile.Bitmap.PixelHeight);
                    double width=tile.Bitmap.PixelWidth*scale,height=tile.Bitmap.PixelHeight*scale;
                    dc.PushClip(new RectangleGeometry(new Rect(rect.X,rect.Y,rect.Width,rect.Height)));
                    dc.DrawImage(tile.Bitmap,new Rect(rect.X+(rect.Width-width)/2,rect.Y+(rect.Height-height)/2,width,height));
                    dc.Pop();
                }
            }
            token.ThrowIfCancellationRequested();
            var result=new RenderTargetBitmap(settings.Width,settings.Height,96,96,PixelFormats.Pbgra32);result.Render(visual);result.Freeze();work.Bitmap=result;
            return work;
        }
        catch{work.Dispose();throw;}
    }
    public string Save(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(Bitmap));
        var first=tiles[0].Input;return NativeFiles.WriteBeside(first,Path.GetFileNameWithoutExtension(first.Path)+" collage","png",encoder.Save,token);
    }
    public void Dispose(){foreach(var tile in tiles)tile.Dispose();tiles.Clear();}
}
