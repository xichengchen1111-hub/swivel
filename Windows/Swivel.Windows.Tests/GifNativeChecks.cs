using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;
using Swivel.Windows;

internal static class GifNativeChecks
{
    internal static void Run(string directory,Action<bool,string> check,Action<Action,string> reject)
    {
        string folder=Path.Combine(directory,"static-gif");Directory.CreateDirectory(folder);string path=Path.Combine(folder,"palette.GIF");
        byte[] original=Convert.FromBase64String("R0lGODlhBAACAIEAAP8AAAD/AAAA/////yH5BAEAAAIALAAAAAAEAAIAAAIHBENxHMUQBQA7");File.WriteAllBytes(path,original);
        using(var document=ImageTools.Open(path,default))
        {
            check(document.Bitmap.IsFrozen&&document.Bitmap.PixelWidth==4&&document.Bitmap.PixelHeight==2&&document.Bitmap.Format==PixelFormats.Bgra32,"GIF native image document freezes exact full canvasBGRA pixels");
            byte[] pixels=new byte[32];document.Bitmap.CopyPixels(pixels,16,0);
            check(pixels.AsSpan(0,4).SequenceEqual(new byte[]{0,0,255,255})&&pixels.AsSpan(4,4).SequenceEqual(new byte[]{0,255,0,255})&&pixels.AsSpan(8,4).SequenceEqual(new byte[]{0,0,0,0}),"GIF palette and canonical transparent pixels survive WPF normalization");
            reject(()=>File.WriteAllBytes(path,new byte[1]),"GIF source stays pinned through editing");reject(()=>Directory.Move(folder,folder+" moved"),"GIF ancestors stay pinned");
            var metadata=ImageTools.Metadata(document,default);check(metadata.Count==2&&metadata.Any(x=>x.Label=="Pixel dimensions"&&x.Value=="4 × 2"),"GIF metadata offers only validated dimensions and file length without invoking another codec");
            string first=ImageTools.Export(document,"convert.png",1920,default),second=ImageTools.Export(document,"convert.png",1920,default);
            check(Path.GetDirectoryName(first)==folder&&first.EndsWith("palette converted.png")&&second.EndsWith("palette converted 2.png"),"GIF converts beside original with non-overwriting collision names");
            using(var stream=File.OpenRead(first))
            {
                var decoded=new PngBitmapDecoder(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0];var bitmap=new FormatConvertedBitmap(decoded,PixelFormats.Bgra32,null,0);var saved=new byte[32];bitmap.CopyPixels(saved,16,0);
                check(saved.SequenceEqual(pixels),"GIF full visible palette/binary alpha roundtrips toPNG without silently flattening");
            }
            string crop=ImageTools.Export(document,"image.crop",1920,default,crop:new PixelCrop(1,0,2,2));using(var stream=File.OpenRead(crop))check(new PngBitmapDecoder(stream,BitmapCreateOptions.None,BitmapCacheOption.OnLoad).Frames[0].PixelWidth==2,"GIF uses existing image editor/export pipeline");
            int count=Directory.GetFiles(folder).Length;reject(()=>ImageTools.Export(document,"convert.png",1920,new CancellationToken(true)),"Cancelled GIF export");check(Directory.GetFiles(folder).Length==count&&!Directory.GetFiles(folder,".swivel-*.tmp").Any(),"GIF cancelled output leaves no published or staging file");
        }
        using(var tile=ImageTools.OpenTile(path,2,2,false,default))check(tile.Bitmap.IsFrozen&&tile.Bitmap.PixelWidth==2&&tile.Bitmap.PixelHeight==1,"GIF read-only thumbnails keep shared image-size rules");
        string bad=Path.Combine(folder,"animated.gif");File.WriteAllBytes(bad,Convert.FromBase64String("R0lGODlhBAACAIEAAP8AAAD/AAAA/////yH5BAEAAAIALAAAAAAEAAIAAAIHBENxHMUQBQAsAAAAAAQAAgAAAgcEQ3EcxRAFADs="));
        int before=Directory.GetFiles(folder).Length;reject(()=>ImageTools.Run(bad,"convert.png",1920,default),"GIF animation refused without publishing first frame");check(Directory.GetFiles(folder).Length==before&&!Directory.GetFiles(folder,".swivel-*.tmp").Any(),"Rejected GIF creates no partial output");
        check(File.ReadAllBytes(path).SequenceEqual(original),"GIF original bytes retained");File.Delete(bad);Directory.Move(folder,folder+" released");check(Directory.Exists(folder+" released"),"GIF source and ancestors released after success/failure");
    }
}
