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

internal static class PngMetadataNativeChecks
{
    public static void Run(string directory,Action<bool,string> check,Action<Action,string> reject)
    {
        string folder=Path.Combine(directory,"png-metadata");Directory.CreateDirectory(folder);
        string path=Path.Combine(folder,"private.png");
        var pixels=Enumerable.Range(0,7*3*4).Select(i=>(byte)(i%4==3?255:i*13)).ToArray();
        var bitmap=BitmapSource.Create(7,3,96,96,PixelFormats.Bgra32,null,pixels,28);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        byte[] original;using(var memory=new MemoryStream()){encoder.Save(memory);original=memory.ToArray();}
        var unknown=Chunk("vpAg",new byte[]{1,2,3});
        var strip=new[]{Chunk("tEXt",Encoding.ASCII.GetBytes("Author\0PRIVATE_AUTHOR")),Chunk("zTXt",new byte[]{65,0,0,255}),Chunk("iTXt",new byte[]{65,0,1,0,0,0,255}),Chunk("eXIf",new byte[]{73,73,42}),Chunk("tIME",new byte[]{7,234,10,24,1,2,3})};
        byte[] decorated=original[..33].Concat(strip.SelectMany(x=>x)).Concat(unknown).Concat(original[33..]).ToArray();File.WriteAllBytes(path,decorated);
        byte[] expected=original[..33].Concat(unknown).Concat(original[33..]).ToArray();string first;
        using(var document=PngMetadataDocument.Open(path,default))
        {
            check(document.Plan.RemovedCount==5&&document.Plan.OutputBytes==expected.Length,"Native prepares a full cleanup plan without parsing invalid compressed text or EXIF");
            reject(()=>File.Move(path,path+" moved"),"PNG source remains pinned during full preview/save session");reject(()=>Directory.Move(folder,folder+" moved"),"PNG ancestor remains pinned");
            var app=Application.Current as App??new App();var dialog=new PngMetadataDialog(app,document);var children=Children(dialog).ToArray();
            check(dialog.Content is DockPanel&&children.OfType<ListView>().Single().Items.Count==document.Plan.Chunks.Count&&children.OfType<Button>().Single(b=>Equals(b.Content,app.L("Save PNG Copy"))).IsEnabled,"Native Fluent chunk summary is complete and Save explicitly available");
            check(children.OfType<TextBox>().All(t=>t.IsReadOnly)&&children.OfType<TextBlock>().All(t=>!t.Text.Contains("PRIVATE_AUTHOR")),"Preview does not display raw private payloads and source path is readonly JSON");dialog.Close();
            check(!document.Plan.Orientation.CanKeep&&!dialog.KeepOrientation&&!children.OfType<CheckBox>().Single().IsEnabled,"Without a keepable source orientation the Keep orientation option is off and disabled");
            check(!document.CanRemoveLocation&&!string.IsNullOrEmpty(document.LocationUnavailableReason)&&!children.OfType<RadioButton>().Single(b=>Equals(b.Content,app.L("Remove location only"))).IsEnabled,"Malformed source EXIF disables location mode with a reason");
            reject(()=>document.Save(false,true,default),"Unavailable native location save is refused");
            first=document.Save(default);string second=document.Save(default);
            check(first!=second&&File.ReadAllBytes(first).SequenceEqual(expected)&&File.ReadAllBytes(second).SequenceEqual(expected)&&File.ReadAllBytes(path).SequenceEqual(decorated),"Exclusive PNG copies preserve every retained byte and never replace source/previous copy");
            var clean=ImageTools.DecodeSnapshot(File.ReadAllBytes(first),first,default);var actual=new byte[pixels.Length];clean.CopyPixels(actual,28,0);
            check(clean.PixelWidth==7&&clean.PixelHeight==3&&actual.SequenceEqual(pixels),"Native PNG/WIC decoding of saved copy preserves unrotated source samples");
            int before=Directory.GetFiles(folder).Length;reject(()=>document.Save(new CancellationToken(true)),"Canceled cleanup does not publish output");check(Directory.GetFiles(folder).Length==before,"Canceled save creates no file");
        }
        {
            var tiff=new byte[]{73,73,42,0,8,0,0,0,2,0,0x0f,1,2,0,6,0,0,0,38,0,0,0,0x12,1,3,0,1,0,0,0,6,0,0,0,0,0,0,0,80,82,73,86,65,84};
            byte[] source=original[..33].Concat(Chunk("eXIf",tiff)).Concat(strip[0]).Concat(original[33..]).ToArray();
            byte[] want=original[..33].Concat(PngMetadata.OrientationChunk(6)).Concat(original[33..]).ToArray();
            string rotated=Path.Combine(folder,"rotated.png");File.WriteAllBytes(rotated,source);
            using var document=PngMetadataDocument.Open(rotated,default);
            check(document.Plan.Orientation==new ExifOrientationInfo(ExifOrientationStatus.Found,6),"Native reads source EXIF orientation 6 (little-endian IFD0)");
            var app=Application.Current as App??new App();var dialog=new PngMetadataDialog(app,document);var box=Children(dialog).OfType<CheckBox>().Single();
            check(box.IsEnabled&&box.IsChecked==true&&dialog.KeepOrientation,"Keep orientation is on by default for a non-1 source orientation");box.IsChecked=false;check(!dialog.KeepOrientation,"Unchecking turns the option off");dialog.Close();
            string kept=document.Save(true,default);string dropped=document.Save(false,default);
            check(File.ReadAllBytes(kept).SequenceEqual(want)&&File.ReadAllBytes(rotated).SequenceEqual(source),"Kept copy is the source with only a minimal orientation EXIF in place of the original; source unchanged");
            check(PngMetadata.Read(new MemoryStream(File.ReadAllBytes(dropped)),default).Orientation==ExifOrientationInfo.None&&PngMetadata.Read(new MemoryStream(File.ReadAllBytes(kept)),default).Orientation.Value==6,"Unchecked copy has no orientation, kept copy re-reads as 6");
            var decoded=ImageTools.DecodeSnapshot(File.ReadAllBytes(kept),kept,default);check(document.CanKeepOrientation&&((decoded.PixelWidth==3&&decoded.PixelHeight==7)||(decoded.PixelWidth==7&&decoded.PixelHeight==3)),"Orientation 6 PNG kept copy passes validation; WIC may or may not apply eXIf orientation");
            var modes=new PngMetadataDialog(app,document);var options=Children(modes).OfType<RadioButton>().ToArray();
            check(options.Single(b=>Equals(b.Content,app.L("Remove all metadata"))).IsChecked==true&&!modes.LocationOnly,"Full removal remains the default mode");
            check(document.CanRemoveLocation,"Strict source EXIF location variant passes native validation");
            options.Single(b=>Equals(b.Content,app.L("Remove location only"))).IsChecked=true;
            check(modes.LocationOnly&&!Children(modes).OfType<CheckBox>().Single().IsEnabled,"Explicit location choice disables the separate orientation-only option");
            options.Single(b=>Equals(b.Content,app.L("Remove all metadata"))).IsChecked=true;
            check(!modes.LocationOnly&&Children(modes).OfType<CheckBox>().Single().IsEnabled,"Switching back restores the existing orientation option");modes.Close();
            using var locationExpected=new MemoryStream();document.Plan.WriteLocationOnly(locationExpected,default);
            string location1=document.Save(false,true,default),location2=document.Save(true,true,default);
            check(location1!=location2&&File.ReadAllBytes(location1).SequenceEqual(locationExpected.ToArray())&&File.ReadAllBytes(location2).SequenceEqual(locationExpected.ToArray())&&File.ReadAllBytes(rotated).SequenceEqual(source),"Native location saves are exclusive, independent of orientation checkbox, and preserve source");
            int beforeLocationCancel=Directory.GetFiles(folder).Length;reject(()=>document.Save(false,true,new CancellationToken(true)),"Canceled native location save refused");
            check(Directory.GetFiles(folder).Length==beforeLocationCancel,"Canceled location save publishes nothing");
        }
        using(var noMatch=PngMetadataDocument.Open(first,default))
        {
            check(noMatch.Plan.RemovedCount==0,"Saved copy no longer contains matching text/EXIF/time blocks");
            var app=Application.Current as App??new App();var dialog=new PngMetadataDialog(app,noMatch);
            check(!Children(dialog).OfType<Button>().Single(b=>Equals(b.Content,app.L("Save PNG Copy"))).IsEnabled,"No-match preview disables Save");dialog.Close();
            int before=Directory.GetFiles(folder).Length;reject(()=>noMatch.Save(default),"Programmatic no-match save also refused");check(Directory.GetFiles(folder).Length==before,"No-match refuses without staging/output");
        }
        using(var disposed=PngMetadataDocument.Open(path,default)){disposed.Dispose();reject(()=>disposed.Save(default),"Disposed source cannot publish stale prepared bytes");}
        var corrupt=(byte[])decorated.Clone();corrupt[40]^=1;string damaged=Path.Combine(folder,"damaged.png");File.WriteAllBytes(damaged,corrupt);
        reject(()=>PngMetadataDocument.Open(damaged,default),"CRC corruption fails preparation");File.Move(damaged,damaged+" released");
        string bad=Path.Combine(folder,"bad-image.png");byte[] idat=Chunk("IDAT",new byte[]{1,2,3});File.WriteAllBytes(bad,original[..33].Concat(strip[0]).Concat(idat).Concat(original[^12..]).ToArray());
        reject(()=>PngMetadataDocument.Open(bad,default),"Valid container with undecodable image data fails native validation before review/save");File.Move(bad,bad+" released");
        reject(()=>PngMetadataDocument.Open(path,new CancellationToken(true)),"Pre-cancel precedes native input preparation");
        check(!Directory.GetFiles(folder,".swivel-*.tmp").Any(),"No failed/canceled PNG stages left behind");
        Directory.Move(folder,folder+" released");check(Directory.Exists(folder+" released"),"All success/failure/disposal input and ancestor leases released");
        // No button is invoked; this fixture never opens Explorer or writes clipboard.
    }
    private static byte[] Chunk(string type,byte[] payload)
    {
        var b=new byte[payload.Length+12];BinaryPrimitives.WriteUInt32BigEndian(b,(uint)payload.Length);Encoding.ASCII.GetBytes(type).CopyTo(b,4);payload.CopyTo(b,8);uint c=uint.MaxValue;
        foreach(byte x in b.AsSpan(4,payload.Length+4)){c^=x;for(int i=0;i<8;i++)c=(c&1)==0?c>>1:(c>>1)^0xedb88320;}BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(b.Length-4),~c);return b;
    }
    private static IEnumerable<DependencyObject> Children(DependencyObject root)
    {yield return root;foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())foreach(var item in Children(child))yield return item;}
}
