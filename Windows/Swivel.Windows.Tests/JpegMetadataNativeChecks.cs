using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;
using Swivel.Windows;

internal static class JpegMetadataNativeChecks
{
    public static void Run(string directory,Action<bool,string> check,Action<Action,string> reject)
    {
        string folder=Path.Combine(directory,"jpeg-metadata");Directory.CreateDirectory(folder);
        string path=Path.Combine(folder,"private.jpg");
        var pixels=Enumerable.Range(0,7*3*4).Select(i=>(byte)(i%4==3?255:i*13)).ToArray();
        var bitmap=BitmapSource.Create(7,3,96,96,PixelFormats.Bgra32,null,pixels,28);var encoder=new JpegBitmapEncoder { QualityLevel=90 };encoder.Frames.Add(BitmapFrame.Create(bitmap));
        byte[] original;using(var memory=new MemoryStream()){encoder.Save(memory);original=memory.ToArray();}
        var unknown=Segment(0xe3,new byte[]{1,2,3});
        var strip=new[]{Segment(0xe1,Encoding.ASCII.GetBytes("Exif\0\0PRIVATE_AUTHOR")),Segment(0xed,new byte[]{255,0xd9,1}),Segment(0xfe,new byte[]{65,0,255})};
        byte[] decorated=original[..2].Concat(strip.SelectMany(x=>x)).Concat(unknown).Concat(original[2..]).ToArray();File.WriteAllBytes(path,decorated);
        byte[] expected=original[..2].Concat(unknown).Concat(original[2..]).ToArray();string first;
        var baseline=ImageTools.DecodeSnapshot(original,path,default);var baselinePixels=new byte[pixels.Length];baseline.CopyPixels(baselinePixels,28,0);
        using(var document=JpegMetadataDocument.Open(path,default))
        {
            check(document.Plan.RemovedCount==3&&document.Plan.OutputBytes==expected.Length,"Native prepares a full cleanup plan without parsing removed APP1/APP13/COM");
            reject(()=>File.Move(path,path+" moved"),"JPEG source remains pinned during full preview/save session");reject(()=>Directory.Move(folder,folder+" moved"),"JPEG ancestor remains pinned");
            var app=Application.Current as App??new App();var dialog=new JpegMetadataDialog(app,document);var children=Children(dialog).ToArray();
            check(dialog.Content is DockPanel&&children.OfType<ListView>().Single().Items.Count==document.Plan.Segments.Count&&children.OfType<Button>().Single(b=>Equals(b.Content,app.L("Save JPEG Copy"))).IsEnabled,"Native Fluent chunk summary is complete and Save explicitly available");
            check(children.OfType<TextBox>().All(t=>t.IsReadOnly)&&children.OfType<TextBlock>().All(t=>!t.Text.Contains("PRIVATE_AUTHOR")),"Preview does not display raw private payloads and source path is readonly JSON");dialog.Close();
            check(!document.Plan.Orientation.CanKeep&&!dialog.KeepOrientation&&!children.OfType<CheckBox>().Single().IsEnabled,"Without a keepable source orientation the Keep orientation option is off and disabled");
            first=document.Save(default);string second=document.Save(default);
            check(first!=second&&File.ReadAllBytes(first).SequenceEqual(expected)&&File.ReadAllBytes(second).SequenceEqual(expected)&&File.ReadAllBytes(path).SequenceEqual(decorated),"Exclusive JPEG copies preserve every retained byte and never replace source/previous copy");
            var clean=ImageTools.DecodeSnapshot(File.ReadAllBytes(first),first,default);var actual=new byte[pixels.Length];clean.CopyPixels(actual,28,0);
            check(clean.PixelWidth==7&&clean.PixelHeight==3&&actual.SequenceEqual(baselinePixels),"Native JPEG/WIC decoding of saved copy preserves the base JPEG decoded samples without further recompression");
            int before=Directory.GetFiles(folder).Length;reject(()=>document.Save(new CancellationToken(true)),"Canceled cleanup does not publish output");check(Directory.GetFiles(folder).Length==before,"Canceled save creates no file");
        }
        {
            var tiff=new byte[]{73,73,42,0,8,0,0,0,2,0,0x0f,1,2,0,6,0,0,0,38,0,0,0,0x12,1,3,0,1,0,0,0,6,0,0,0,0,0,0,0,80,82,73,86,65,84};
            byte[] source=original[..2].Concat(Segment(0xe1,Encoding.ASCII.GetBytes("Exif\0\0").Concat(tiff).ToArray())).Concat(strip[2]).Concat(original[2..]).ToArray();
            byte[] want=original[..2].Concat(JpegMetadata.OrientationSegment(6)).Concat(original[2..]).ToArray();
            string rotated=Path.Combine(folder,"rotated.jpg");File.WriteAllBytes(rotated,source);
            using var document=JpegMetadataDocument.Open(rotated,default);
            check(document.Plan.Orientation==new ExifOrientationInfo(ExifOrientationStatus.Found,6),"Native reads source EXIF orientation 6 (little-endian IFD0)");
            var app=Application.Current as App??new App();var dialog=new JpegMetadataDialog(app,document);var box=Children(dialog).OfType<CheckBox>().Single();
            check(box.IsEnabled&&box.IsChecked==true&&dialog.KeepOrientation,"Keep orientation is on by default for a non-1 source orientation");box.IsChecked=false;check(!dialog.KeepOrientation,"Unchecking turns the option off");dialog.Close();
            string kept=document.Save(true,default);string dropped=document.Save(false,default);
            check(File.ReadAllBytes(kept).SequenceEqual(want)&&File.ReadAllBytes(rotated).SequenceEqual(source),"Kept copy is the source with only a minimal orientation EXIF in place of the original; source unchanged");
            check(JpegMetadata.Read(new MemoryStream(File.ReadAllBytes(dropped)),default).Orientation==ExifOrientationInfo.None&&JpegMetadata.Read(new MemoryStream(File.ReadAllBytes(kept)),default).Orientation.Value==6,"Unchecked copy has no orientation, kept copy re-reads as 6");
            var decoded=ImageTools.DecodeSnapshot(File.ReadAllBytes(kept),kept,default);check(document.CanKeepOrientation&&decoded.PixelWidth==3&&decoded.PixelHeight==7,"Orientation 6 kept copy passes validation and the shared decoder applies it (7×3 stored → 3×7 shown)");
        }
        using(var noMatch=JpegMetadataDocument.Open(first,default))
        {
            check(noMatch.Plan.RemovedCount==0,"Saved copy no longer contains matching APP1/APP13/COM segments");
            var app=Application.Current as App??new App();var dialog=new JpegMetadataDialog(app,noMatch);
            check(!Children(dialog).OfType<Button>().Single(b=>Equals(b.Content,app.L("Save JPEG Copy"))).IsEnabled,"No-match preview disables Save");dialog.Close();
            int before=Directory.GetFiles(folder).Length;reject(()=>noMatch.Save(default),"Programmatic no-match save also refused");check(Directory.GetFiles(folder).Length==before,"No-match refuses without staging/output");
        }
        using(var disposed=JpegMetadataDocument.Open(path,default)){disposed.Dispose();reject(()=>disposed.Save(default),"Disposed source cannot publish stale prepared bytes");}
        var corrupt=(byte[])decorated.Clone();corrupt[0]^=1;string damaged=Path.Combine(folder,"damaged.jpg");File.WriteAllBytes(damaged,corrupt);
        reject(()=>JpegMetadataDocument.Open(damaged,default),"Invalid JPEG signature fails preparation");File.Move(damaged,damaged+" released");
        string bad=Path.Combine(folder,"bad-image.jpg");File.WriteAllBytes(bad,new byte[]{255,0xd8}.Concat(Segment(0xc0,new byte[]{8,0,3,0,7,1,1,17,0})).Concat(strip[0]).Concat(Segment(0xda,new byte[]{1,1,0,0,63,0})).Concat(new byte[]{0,255,0xd9}).ToArray());
        reject(()=>JpegMetadataDocument.Open(bad,default),"Valid marker subset with undecodable image data fails native validation before review/save");File.Move(bad,bad+" released");
        reject(()=>JpegMetadataDocument.Open(path,new CancellationToken(true)),"Pre-cancel precedes native input preparation");
        check(!Directory.GetFiles(folder,".swivel-*.tmp").Any(),"No failed/canceled JPEG stages left behind");
        Directory.Move(folder,folder+" released");check(Directory.Exists(folder+" released"),"All success/failure/disposal input and ancestor leases released");
        // No button is invoked; this fixture never opens Explorer or writes clipboard.
    }
    private static byte[] Segment(int marker,byte[] payload)
    {var bytes=new byte[payload.Length+4];bytes[0]=255;bytes[1]=(byte)marker;BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2),(ushort)(payload.Length+2));payload.CopyTo(bytes,4);return bytes;}
    private static IEnumerable<DependencyObject> Children(DependencyObject root)
    {yield return root;foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())foreach(var item in Children(child))yield return item;}
}
