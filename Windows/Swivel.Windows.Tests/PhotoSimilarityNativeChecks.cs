using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;
using Swivel.Windows;

internal static class PhotoSimilarityNativeChecks
{
    public static void Run(string directory,Action<bool,string> check,Action<Action,string> reject)
    {
        string folder=Path.Combine(directory,"similar-images");Directory.CreateDirectory(folder);Directory.CreateDirectory(Path.Combine(folder,"child"));
        byte[] pixels=new byte[27*24*4];for(int y=0;y<24;y++)for(int x=0;x<27;x++){int at=(y*27+x)*4;byte v=(x/3)%2==0?(byte)220:(byte)20;pixels[at]=pixels[at+1]=pixels[at+2]=v;pixels[at+3]=255;}
        var bitmap=BitmapSource.Create(27,24,96,96,PixelFormats.Bgra32,null,pixels,27*4);bitmap.Freeze();
        var encoders=new (string Name,BitmapEncoder Encoder)[]{("a.png",new PngBitmapEncoder()),(@"child\b.bmp",new BmpBitmapEncoder()),("c.tiff",new TiffBitmapEncoder()),("d.jpg",new JpegBitmapEncoder{QualityLevel=100})};
        foreach(var item in encoders){item.Encoder.Frames.Add(BitmapFrame.Create(bitmap));using var output=File.Create(Path.Combine(folder,item.Name));item.Encoder.Save(output);}
        string gif=Path.Combine(folder,"e.gif");using(var output=File.Create(gif))GifExport.Write(new PixelImage(27,24,pixels),output,default);
        foreach(var name in encoders.Select(x=>x.Name).Append("e.gif"))
        {
            string full=Path.Combine(folder,name);using var held=ImageTools.Open(full,default);var snapshot=ImageTools.DecodeSnapshot(File.ReadAllBytes(full),name,default);
            check(snapshot.IsFrozen&&snapshot.PixelWidth==held.Bitmap.PixelWidth&&snapshot.PixelHeight==held.Bitmap.PixelHeight&&ImageTools.ReadPixels(snapshot,0,default).Bgra.SequenceEqual(ImageTools.ReadPixels(held.Bitmap,0,default).Bgra),"Snapshot decoder equals existing held-input decoder and freezes fully copied pixels: "+name);
        }
        File.WriteAllBytes(Path.Combine(folder,"bad.png"),new byte[]{137,80,78,71,13,10,26,10});File.WriteAllBytes(Path.Combine(folder,"bad.jpg"),new byte[]{255,216,255,0});
        File.WriteAllText(Path.Combine(folder,"notes.txt"),"not an image");
        byte[] plain=new byte[27*24*4];for(int i=3;i<plain.Length;i+=4)plain[i]=255;
        var flat=new PngBitmapEncoder();flat.Frames.Add(BitmapFrame.Create(BitmapSource.Create(27,24,96,96,PixelFormats.Bgra32,null,plain,27*4)));using(var output=File.Create(Path.Combine(folder,"flat.png")))flat.Save(output);
        var high=new PngBitmapEncoder();high.Frames.Add(BitmapFrame.Create(BitmapSource.Create(27,24,96,96,PixelFormats.Gray16,null,new byte[27*24*2],27*2)));using(var output=File.Create(Path.Combine(folder,"high.png")))high.Save(output);
        var before=Directory.GetFiles(folder,"*",SearchOption.AllDirectories).ToDictionary(p=>p,File.ReadAllBytes);
        var result=DoctorPhotoSimilarity.Run(folder,default);
        check(result.Similar.Loaded==5&&result.Similar.Pairs.Count==10&&result.Similar.Pairs.All(p=>p.DifferentBits==0),"Native closed-codec cross-format canonical grid fixtures make ten independent pairs");
        check(result.Similar.Issues.Any(i=>i.RelativePath=="bad.png")&&result.Similar.Issues.Any(i=>i.RelativePath=="bad.jpg")&&result.Similar.Issues.Any(i=>i.RelativePath=="flat.png")&&result.Similar.Issues.Any(i=>i.RelativePath=="high.png")&&result.Similar.Issues.Any(i=>i.RelativePath=="notes.txt"),"Reviewed malformed PNG/JPEG, flat, high-bit-depth and unsupported sources skip without aborting later pairs");
        using(var locked=File.Open(Path.Combine(folder,"a.png"),FileMode.Open,FileAccess.ReadWrite,FileShare.None))
        {var blocked=DoctorPhotoSimilarity.Run(folder,default);check(blocked.Similar.Loaded==4&&blocked.Similar.Issues.Any(i=>i.RelativePath=="a.png"),"Unavailable image records issue while later ordinary sources still compare");}
        using(var root=NativeFiles.MetadataDirectory.Open(folder))
        {
            var file=new DuplicateCandidate("a.png",before[Path.Combine(folder,"a.png")].Length);
            var guarded=PhotoSimilarity.Scan(new[]{file},root.OpenDuplicate,(data,name,token,checkpoint)=>{
                reject(()=>File.Open(Path.Combine(folder,"a.png"),FileMode.Open,FileAccess.Write,FileShare.ReadWrite).Dispose(),"Input remains pinned during snapshot decoder");
                reject(()=>Directory.Move(folder,folder+" moved"),"Chosen root remains pinned during snapshot decoder");
                checkpoint();return PhotoSimilarity.Fingerprint(ImageTools.ReadPixels(ImageTools.DecodeSnapshot(data,name,token),0,token),token,checkpoint);
            },default);
            check(guarded.Loaded==1,"Snapshot decode/hash occurs before source/root leases release");
        }
        string system=Path.Combine(folder,"system.png");File.WriteAllBytes(system,before[Path.Combine(folder,"a.png")]);File.SetAttributes(system,FileAttributes.System);
        try
        {
            var partial=DoctorPhotoSimilarity.Run(folder,default);check(partial.Folder.Root.Incomplete&&partial.Folder.Root.Skipped==1,"System image remains metadata skip without content read");
            var app=Application.Current as App??new App();var dialog=new PhotoSimilarityDialog(app,folder,partial.Folder,partial.Similar);
            check(dialog.Content is DockPanel&&dialog.CurrentReport.Contains(folder)&&dialog.CurrentReport.Contains("system.png")&&dialog.CurrentReport.Contains("bad.jpg")&&dialog.CurrentReport.Contains("notes.txt"),"Native report preserves root, metadata issues and image skips");
            var lists=Children(dialog).OfType<ListView>().ToArray();var pairs=lists.Single(v=>AutomationProperties.GetName(v)==app.L("Matching Image Pairs"));var copy=Children(dialog).OfType<Button>().Single(v=>Equals(v.Content,app.L("Copy Selected Pair")));
            check(lists.Length==2&&lists.All(v=>VirtualizingPanel.GetIsVirtualizing(v)&&VirtualizingPanel.GetVirtualizationMode(v)==VirtualizationMode.Recycling)&&!copy.IsEnabled,"Image pairs/issues native virtual lists and no auto-selected copy");
            pairs.SelectedIndex=0;check(copy.IsEnabled&&dialog.SelectedReport.Contains(Path.Combine(folder,"a.png"))&&dialog.SelectedReport.Contains(Path.Combine(folder,"c.tiff"))&&!dialog.SelectedReport.Contains("bad.jpg"),"Selected report carries only selected safely displayed paths and agreement details");
            pairs.SelectedIndex=-1;check(!copy.IsEnabled,"Clearing image selection disables stale pair copy");dialog.Close();
            var empty=new PhotoSimilarityDialog(app,folder,partial.Folder,partial.Similar with{Pairs=Array.Empty<SimilarPhotoPair>(),MatchingPairs=0});check(empty.CurrentReport.Contains("system.png")&&empty.CurrentReport.Contains(app.L("No similar pairs in the comparisons completed. Skipped or unexamined files may still be similar.")),"Zero matches keep partial/issues and do not claim folder-wide dissimilarity");empty.Close();
            var confirmation=new PhotoSimilarityConfirmation(app,folder);check(confirmation.Content is DockPanel&&Children(confirmation).OfType<Button>().Any(v=>v.IsCancel)&&Children(confirmation).OfType<TextBlock>().Any(v=>v.Text==app.L(PhotoSimilarityNotices.Detail)),"Native confirmation declares low-detail omissions and offers Cancel");confirmation.Close();
        }
        finally{File.SetAttributes(system,FileAttributes.Normal);File.Delete(system);}
        reject(()=>DoctorPhotoSimilarity.Run(folder,new CancellationToken(true)),"Pre-cancel precedes native photo root access");
        check(before.All(x=>File.ReadAllBytes(x.Key).SequenceEqual(x.Value))&&!Directory.GetFiles(folder,".swivel-*.tmp",SearchOption.AllDirectories).Any(),"All image source bytes unchanged and no output staging produced");
        Directory.Move(folder,folder+" released");check(Directory.Exists(folder+" released"),"Image comparison and dialogs leave no root/descendant handles held");
    }
    private static IEnumerable<DependencyObject> Children(DependencyObject root)
    {yield return root;foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())foreach(var descendant in Children(child))yield return descendant;}
}
