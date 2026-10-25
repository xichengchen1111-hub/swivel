using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;
using Swivel.Windows;

internal static class PhotoToolsChecks
{
    internal static void Run(string directory,Action<bool,string> check,Action<Action,string> reject)
    {
        string file=Path.Combine(directory,"editing sample.png");int width=64,height=48;
        var pixels=new byte[width*height*4];
        for(int i=0;i<width*height;i++){pixels[i*4]=(byte)(i%200);pixels[i*4+1]=90;pixels[i*4+2]=160;pixels[i*4+3]=255;}
        var bitmap=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,pixels,width*4);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(file))encoder.Save(stream);
        byte[] original=File.ReadAllBytes(file);
        using(var document=ImageTools.Open(file,CancellationToken.None))
        {
            var adjusted=Decode(ImageTools.Export(document,"image.adjust",1920,CancellationToken.None,adjustments:new PhotoSettings(Exposure:.5,Warmth:.2)));
            check(adjusted.PixelWidth==width && adjusted.PixelHeight==height && !Bytes(adjusted).SequenceEqual(pixels),"Photo adjustment renders changed pixels at source dimensions");
            var gray=Bytes(Decode(ImageTools.Export(document,"image.gray",1920,CancellationToken.None)));
            check(Enumerable.Range(0,width*height).All(i=>gray[i*4]==gray[i*4+1] && gray[i*4+1]==gray[i*4+2]),"Grayscale export uses equal channels");
            var flipped=Bytes(Decode(ImageTools.Export(document,"image.flip",1920,CancellationToken.None)));
            check(flipped.AsSpan(0,4).SequenceEqual(pixels.AsSpan((width-1)*4,4)),"Horizontal flip maps first pixel to original right edge");
            var rectangle=new ImageMark(MarkKind.Redact,new[]{new ImagePoint(5,7),new ImagePoint(20,30)},0,2);
            var result=Bytes(Decode(ImageTools.Export(document,"image.redact",1920,CancellationToken.None,marks:new[]{rectangle})));
            bool correct=true;for(int y=0;y<height;y++)for(int x=0;x<width;x++){int i=(y*width+x)*4;bool inside=x>=5&&x<20&&y>=7&&y<30;correct&=inside?result[i]==0&&result[i+1]==0&&result[i+2]==0&&result[i+3]==255:result.AsSpan(i,4).SequenceEqual(pixels.AsSpan(i,4));}
            check(correct,"Saved redaction removes covered RGB and leaves every outside pixel intact");
            var arrow=new ImageMark(MarkKind.Arrow,new[]{new ImagePoint(10,10),new ImagePoint(50,35)},0xffffff,4);
            var text=new ImageMark(MarkKind.Text,new[]{new ImagePoint(10,10)},0,3,"Test 测试");
            var annotated=Decode(ImageTools.Export(document,"image.annotate",1920,CancellationToken.None,marks:new[]{arrow,text}));
            check(annotated.PixelWidth==width && !Bytes(annotated).SequenceEqual(pixels),"Arrow and text annotations are baked into PNG pixels");
            var number=new ImageMark(MarkKind.Number,new[]{new ImagePoint(32,24)},0xff0000,4,"12");
            var numbered=Bytes(Decode(ImageTools.Export(document,"image.annotate",1920,CancellationToken.None,marks:new[]{number})));
            check(numbered.AsSpan(0,4).SequenceEqual(pixels.AsSpan(0,4))&&!numbered.SequenceEqual(pixels),"Number label is rendered while distant pixels remain unchanged");
            using var cancelled=new CancellationTokenSource();cancelled.Cancel();int before=Directory.GetFiles(directory).Length;
            reject(()=>ImageTools.Export(document,"image.redact",1920,cancelled.Token,marks:new[]{rectangle}),"Cancelled redaction never publishes");
            check(Directory.GetFiles(directory).Length==before,"Cancelled new editor output has no leaked file");
        }
        foreach(bool fill in new[]{false,true})
        {
            using var collage=CollageWork.Open(new[]{file,file},new CollageSettings(640,480,12,CollageStyle.Grid,fill),CancellationToken.None);
            check(collage.Bitmap.IsFrozen && collage.Bitmap.PixelWidth==640 && collage.Bitmap.PixelHeight==480,"Collage preview frozen at final output dimensions");
            string output=collage.Save(CancellationToken.None);var decoded=Decode(output);
            check(decoded.PixelWidth==640 && decoded.PixelHeight==480 && output.StartsWith(Path.Combine(directory,"editing sample collage"),StringComparison.Ordinal),"Collage saved beside first input with exact canvas");
            check(Bytes(decoded).SequenceEqual(Bytes(collage.Bitmap)),"Collage preview and export have identical pixels");
            if(fill)
            {
                var values=Bytes(decoded);var frames=new CollageSettings(640,480,12,CollageStyle.Grid,true).Layout(2);
                check(frames.All(r=>values[((r.Y+1)*640+r.X+r.Width/2)*4+1]<250 && values[((r.Y+r.Height-2)*640+r.X+r.Width/2)*4+1]<250),"Fill covers the entire cell despite integer source crop rounding");
            }
        }
        check(File.ReadAllBytes(file).SequenceEqual(original),"All photo tools preserve original file bytes");
        using (var featured=CollageWork.Open(new[]{file,file,file},new CollageSettings(640,480,12,CollageStyle.Featured,true),CancellationToken.None))
        {
            string featuredPath=featured.Save(CancellationToken.None);check(Bytes(Decode(featuredPath)).SequenceEqual(Bytes(featured.Bitmap)),"Featured collage export uses the exact preview bitmap");
        }
        check(!Directory.GetFiles(directory,".swivel-*.tmp").Any(),"Photo tools leave no staging files");
    }
    private static BitmapFrame Decode(string path){using var stream=File.OpenRead(path);return BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0];}
    private static byte[] Bytes(BitmapSource image){var source=new FormatConvertedBitmap(image,PixelFormats.Bgra32,null,0);var pixels=new byte[source.PixelWidth*source.PixelHeight*4];source.CopyPixels(pixels,source.PixelWidth*4,0);return pixels;}
}
