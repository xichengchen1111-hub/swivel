using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;
using Swivel.Windows;

internal static class ImageExtraChecks
{
    public static void Run(string directory, Action<bool,string> check, Action<Action,string> reject)
    {
        string path = Path.Combine(directory, "image-extra.jpg");
        var metadata = new BitmapMetadata("jpg") { Title = "Test title", Comment = "text\u202evisible" };
        byte[] pixels = new byte[64 * 48 * 3]; for (int i = 0; i < pixels.Length; i += 3) pixels[i + 2] = 255;
        var encoder = new JpegBitmapEncoder { QualityLevel = 100 };
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(64,48,96,96,PixelFormats.Bgr24,null,pixels,64*3),null,metadata,null));
        using (var stream = File.Create(path)) encoder.Save(stream);
        byte[] original = File.ReadAllBytes(path);
        var resized = Decode(ImageTools.Run(path,"image.resize",1920,CancellationToken.None,resize:new(ResizeBasis.Height,96,true)));
        check(resized.PixelWidth == 128 && resized.PixelHeight == 96, "Native resize by height and explicit enlargement");
        string compressed = ImageTools.Run(path,"image.compress",1920,CancellationToken.None,targetBytes:4096);
        check(new FileInfo(compressed).Length <= 4096 && Decode(compressed).PixelWidth == 64, "Native byte ceiling with dimensions retained");
        using (var document = ImageTools.Open(path,CancellationToken.None))
        {
            var tags = ImageTools.Metadata(document,CancellationToken.None);
            check(tags.Any(x => x.Label == "Title" && x.Value == "Test title"), "Metadata title read from held source");
            check(tags.Any(x => x.Label == "Comment" && x.Value.Contains("[U+202E]")), "Metadata invisibles escaped");
            var stripped = Decode(ImageTools.Export(document,"convert.png",1920,CancellationToken.None));
            check(stripped.Metadata is not BitmapMetadata clean || string.IsNullOrEmpty(clean.Title), "New PNG contains no source title");
            var bitmap = ImageFrameTools.Render(document.Bitmap,new(128,128,16,8,false,FrameStyle.White),CancellationToken.None);
            string output = ImageFrameTools.Save(document,bitmap,CancellationToken.None); var saved = Decode(output);
            check(saved.PixelWidth == 128 && saved.PixelHeight == 128, "Frame dimensions");
            var previewPixels = ImageTools.ReadPixels(bitmap,0,CancellationToken.None); var savedPixels = ImageTools.ReadPixels(saved,0,CancellationToken.None);
            check(previewPixels.Bgra.SequenceEqual(savedPixels.Bgra), "Frame exports exactly the preview");
            check(savedPixels.Bgra[0] == 255 && savedPixels.Bgra[1] == 255 && savedPixels.Bgra[2] == 255, "Frame background pixels");
            int center = (64*128+64)*4; check(savedPixels.Bgra[center + 2] > 240 && savedPixels.Bgra[center] < 15, "Frame source centered and retained");
            int count = Directory.GetFiles(directory).Length;
            reject(() => ImageFrameTools.Save(document,bitmap,new CancellationToken(true)), "Frame cancel before publish");
            check(Directory.GetFiles(directory).Length == count, "Cancelled frame leaves no output");
        }
        check(File.ReadAllBytes(path).SequenceEqual(original), "Image extras leave source bytes unchanged");
    }
    private static BitmapFrame Decode(string path) { using var stream = File.OpenRead(path); return BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0]; }
}
