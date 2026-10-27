using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;
using Swivel.Windows;

internal static class GifExportNativeChecks
{
    private static void Png(string path,int width,byte[] pixels)
    {var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width,1,96,96,PixelFormats.Bgra32,null,pixels,width*4)));using var output=File.Create(path);encoder.Save(output);}
    internal static void Run(string directory,Action<bool,string> check,Action<Action,string> reject)
    {
        string folder=Path.Combine(directory,"gif-export");Directory.CreateDirectory(folder);string path=Path.Combine(folder,"色彩 🦜.png");
        byte[] pixels={0,0,0,255,0,0,0,0,0,0,255,255,0,255,0,255,255,0,0,255};Png(path,5,pixels);byte[] original=File.ReadAllBytes(path);
        using(var document=ImageTools.Open(path,default))
        {
            reject(()=>File.WriteAllBytes(path,new byte[1]),"GIFexport source pinned");reject(()=>Directory.Move(folder,folder+" moved"),"GIFexport source ancestors pinned");
            string first=ImageTools.Export(document,"convert.gif",1920,default),second=ImageTools.Export(document,"convert.gif",1920,default);
            check(first.EndsWith(" converted.gif")&&second.EndsWith(" converted 2.gif")&&Path.GetDirectoryName(first)==folder,"GIFexport exclusive beside names");
            check(File.ReadAllBytes(first).SequenceEqual(File.ReadAllBytes(second)),"GIFexport stable contents across collision suffixes");
            using(var input=File.OpenRead(first))check(GifImage.Read(input,default).Bgra.SequenceEqual(pixels),"GIFexport native output coredecoder exactpalette/alpha/blackdistinction");
            using(var input=File.OpenRead(first))
            {
                var decoder=new GifBitmapDecoder(input,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);check(decoder.Frames.Count==1&&decoder.Frames[0].PixelWidth==5,"GIFexport WPFbuiltin seesoneframe/fullcanvas");
                var converted=new FormatConvertedBitmap(decoder.Frames[0],PixelFormats.Bgra32,null,0);var decoded=new byte[pixels.Length];converted.CopyPixels(decoded,20,0);
                check(Enumerable.Range(0,5).All(i=>decoded[i*4+3]==pixels[i*4+3]&&(pixels[i*4+3]==0||decoded.AsSpan(i*4,3).SequenceEqual(pixels.AsSpan(i*4,3)))),"GIFexport WPFbuiltin retains allvisibleRGB/binaryalpha");
            }
            var app=Application.Current as App??new App();var action=Catalog.Actions.Single(a=>a.Id=="convert.gif");var dialog=new ConversionDialog(app,action,new[]{path},"gif");
            var body=(StackPanel)((DockPanel)dialog.Content).Children.OfType<ScrollViewer>().Single().Content;
            check(body.Children.OfType<TextBlock>().Any(t=>t.Text==app.L(GifExport.Notice)),"GIFexport native conversion review includes explicit palette/alpha/noquantization limits");dialog.Close();
            int before=Directory.GetFiles(folder).Length;reject(()=>ImageTools.Export(document,"convert.gif",1920,new CancellationToken(true)),"GIFexportnative precancel");check(Directory.GetFiles(folder).Length==before&&!Directory.GetFiles(folder,".swivel-*.tmp").Any(),"GIFexportcancelnostageorpublication");
        }
        string partial=Path.Combine(folder,"partial.png"),colors=Path.Combine(folder,"257colors.png");Png(partial,1,new byte[]{0,0,255,128});var many=new byte[257*4];for(int i=0;i<257;i++){many[i*4]=(byte)i;many[i*4+1]=(byte)(i>>8);many[i*4+3]=255;}Png(colors,257,many);
        foreach(string source in new[]{partial,colors}){int before=Directory.GetFiles(folder).Length;reject(()=>ImageTools.Run(source,"convert.gif",1920,default),"GIFexport unsupportedcolor/alpha refused");check(Directory.GetFiles(folder).Length==before&&!Directory.GetFiles(folder,".swivel-*.tmp").Any(),"GIFexport refuseswithoutpartialpublishedfile/stage");}
        var batch=ImageBatch.Run(new[]{path,partial,path},source=>ImageTools.Run(source,"convert.gif",1920,default),default);
        check(batch.Select(e=>e.State).SequenceEqual(new[]{BatchState.Saved,BatchState.Failed,BatchState.NotStarted})&&File.Exists(batch[0].Output!),"GIFexport batch stopsafterfirsterror andkeepscompletedoutput");
        check(File.ReadAllBytes(path).SequenceEqual(original),"GIFexport originalbytes unchanged aftersuccess/refusal/cancel");Directory.Move(folder,folder+" released");check(Directory.Exists(folder+" released"),"GIFexport leasecleanup onallpaths");
    }
}
