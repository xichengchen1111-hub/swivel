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

internal static class TiffMetadataNativeChecks
{
    public static void Run(string directory,Action<bool,string> check,Action<Action,string> reject)
    {
        string folder=Path.Combine(directory,"tiff-metadata");Directory.CreateDirectory(folder);
        string path=Path.Combine(folder,"private.tif");
        var gray=Enumerable.Range(0,15).Select(i=>(byte)(i*17)).ToArray();var rgb=Enumerable.Range(0,24).Select(i=>(byte)(i*11+3)).ToArray();
        byte[] source=Build(new[]{(5,3,1,gray,6),(4,2,3,rgb,0)});File.WriteAllBytes(path,source);
        var before=Frames(source);
        string plain,kept;
        using(var document=TiffMetadataDocument.Open(path,default))
        {
            check(document.Plan.PageCount==2&&document.Plan.RemovedCount==2*4+1&&document.CanKeepOrientation,"Native prepares a two-page plan, validates both rebuilt pages and offers Keep orientation");
            reject(()=>File.Move(path,path+" moved"),"TIFF source remains pinned during preview/save");reject(()=>Directory.Move(folder,folder+" moved"),"TIFF ancestor remains pinned");
            var app=Application.Current as App??new App();var dialog=new TiffMetadataDialog(app,document);var children=Children(dialog).ToArray();
            check(dialog.Content is DockPanel&&children.OfType<ListView>().Single().Items.Count==document.Plan.Tags.Count&&children.OfType<Button>().Single(b=>Equals(b.Content,app.L("Save TIFF Copy"))).IsEnabled,"Native tag summary is complete and Save explicitly available");
            check(children.OfType<TextBox>().All(t=>t.IsReadOnly)&&children.OfType<TextBlock>().All(t=>!t.Text.Contains("PRIVATE")),"Preview never displays removed payloads");
            var box=children.OfType<CheckBox>().Single();check(box.IsEnabled&&dialog.KeepOrientation,"Keep orientation on by default for orientation 6");dialog.Close();
            plain=document.Save(false,default);kept=document.Save(true,default);
            check(plain!=kept&&Path.GetExtension(plain)==".tif"&&File.ReadAllBytes(path).SequenceEqual(source),"Two exclusive TIFF copies beside the unchanged original");
            int count=Directory.GetFiles(folder).Length;reject(()=>document.Save(false,new CancellationToken(true)),"Canceled save does not publish");check(Directory.GetFiles(folder).Length==count,"Canceled save creates no file");
        }
        foreach(var output in new[]{plain,kept})
        {
            var bytes=File.ReadAllBytes(output);var after=Frames(bytes);
            check(!Encoding.ASCII.GetString(bytes).Contains("PRIVATE")&&after.Count==2&&after.Zip(before).All(x=>x.First.SequenceEqual(x.Second)),"Native WIC decodes every saved page with identical pixels and no removed payload");
        }
        check(TiffMetadata.Read(new MemoryStream(File.ReadAllBytes(kept)),default).Orientation.Value==6&&TiffMetadata.Read(new MemoryStream(File.ReadAllBytes(plain)),default).Orientation==ExifOrientationInfo.None,"Kept copy re-reads orientation 6; plain copy has none");
        using(var noMatch=TiffMetadataDocument.Open(plain,default))
        {
            var app=Application.Current as App??new App();var dialog=new TiffMetadataDialog(app,noMatch);
            check(noMatch.Plan.RemovedCount==0&&!Children(dialog).OfType<Button>().Single(b=>Equals(b.Content,app.L("Save TIFF Copy"))).IsEnabled,"No-match preview disables Save");dialog.Close();
            reject(()=>noMatch.Save(false,default),"Programmatic no-match save refused");
        }
        using(var disposed=TiffMetadataDocument.Open(path,default)){disposed.Dispose();reject(()=>disposed.Save(false,default),"Disposed source cannot publish");}
        string bad=Path.Combine(folder,"bad.tif");File.WriteAllBytes(bad,source[..^30]);
        reject(()=>TiffMetadataDocument.Open(bad,default),"Truncated TIFF refused before review");File.Move(bad,bad+" released");
        reject(()=>TiffMetadataDocument.Open(path,new CancellationToken(true)),"Pre-cancel precedes native preparation");
        check(!Directory.GetFiles(folder,".swivel-*.tmp").Any(),"No failed/canceled TIFF stages left behind");
        Directory.Move(folder,folder+" released");check(Directory.Exists(folder+" released"),"All TIFF input and ancestor leases released");
    }
    private static List<byte[]> Frames(byte[] bytes)
    {
        var decoder=new TiffBitmapDecoder(new MemoryStream(bytes),BitmapCreateOptions.PreservePixelFormat|BitmapCreateOptions.IgnoreImageCache,BitmapCacheOption.OnLoad);
        return decoder.Frames.Select(f=>{var c=new FormatConvertedBitmap(f,PixelFormats.Bgra32,null,0);var p=new byte[c.PixelWidth*c.PixelHeight*4];c.CopyPixels(p,c.PixelWidth*4,0);return p;}).ToList();
    }
    // Little-endian uncompressed 8-bit pages, one strip each, with private descriptive tags.
    private static byte[] Build((int W,int H,int Spp,byte[] Data,int Orientation)[] pages)
    {
        var s=new MemoryStream();var w=new BinaryWriter(s);w.Write("II*\0"u8);w.Write(0u);long link=4;
        foreach(var page in pages)
        {
            long data=s.Position;w.Write(page.Data);if((s.Position&1)!=0)w.Write((byte)0);
            long bits=s.Position;for(int i=0;i<page.Spp;i++)w.Write((ushort)8);
            long text=s.Position;w.Write("PRIVATE_SECRET\0\0"u8);
            var entries=new List<(ushort Tag,ushort Type,uint Count,uint Value)>{(256,3,1,(uint)page.W),(257,3,1,(uint)page.H),(258,3,(uint)page.Spp,page.Spp>2?(uint)bits:8u),(259,3,1,1),(262,3,1,page.Spp==3?2u:1u),(270,2,15,(uint)text),(271,2,15,(uint)text),(273,4,1,(uint)data),(277,3,1,(uint)page.Spp),(278,3,1,(uint)page.H),(279,4,1,(uint)page.Data.Length),(305,2,15,(uint)text),(40000,7,15,(uint)text)};
            if(page.Orientation!=0)entries.Add((274,3,1,(uint)page.Orientation));
            entries.Sort((a,b)=>a.Tag.CompareTo(b.Tag));
            long ifd=s.Position;s.Position=link;w.Write((uint)ifd);s.Position=ifd;w.Write((ushort)entries.Count);
            foreach(var e in entries){w.Write(e.Tag);w.Write(e.Type);w.Write(e.Count);if(e.Type==3&&e.Count==1){w.Write((ushort)e.Value);w.Write((ushort)0);}else w.Write(e.Value);}
            link=s.Position;w.Write(0u);
        }
        return s.ToArray();
    }
    private static IEnumerable<DependencyObject> Children(DependencyObject root)
    {yield return root;foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())foreach(var item in Children(child))yield return item;}
}
