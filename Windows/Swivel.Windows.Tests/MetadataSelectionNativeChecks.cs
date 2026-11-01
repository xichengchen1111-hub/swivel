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

internal static class MetadataSelectionNativeChecks
{
    public static void Run(string directory,Action<bool,string> check,Action<Action,string> reject)
    {
        string folder=Path.Combine(directory,"selective-metadata");Directory.CreateDirectory(folder);int assertions=0;
        void Check(bool ok,string name){check(ok,"Selective native: "+name);assertions++;}
        var bitmap=BitmapSource.Create(3,2,96,96,PixelFormats.Gray8,null,new byte[]{11,31,51,71,91,111},3);
        byte[] Encode(BitmapEncoder encoder){encoder.Frames.Add(BitmapFrame.Create(bitmap));using var m=new MemoryStream();encoder.Save(m);return m.ToArray();}
        byte[] jpeg=Encode(new JpegBitmapEncoder()),png=Encode(new PngBitmapEncoder());
        foreach(bool little in new[]{true,false})foreach(string format in new[]{"jpeg","png","tiff"})
        {
            byte[] tiff=MetadataSelectionChecks.Tiff(little,format=="tiff",format=="tiff"?2:1);
            // A real WIC-encoded thumbnail, independent of the sparse core fixture's
            // framing-only thumbnail. This test does not depend on installed codecs.
            if(format!="tiff")
            {
                jpeg.CopyTo(tiff,21000);int at=1024+2+4*12+8;
                if(little)BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(at),(uint)jpeg.Length);else BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(at),(uint)jpeg.Length);
            }
            byte[] source=format switch
            {
                "jpeg"=>jpeg[..2].Concat(JpegMetadataChecks.Segment(0xe1,"Exif\0\0"u8.ToArray().Concat(tiff).ToArray())).Concat(jpeg[2..]).ToArray(),
                "png"=>png[..33].Concat(PngMetadataChecks.Chunk("eXIf",tiff)).Concat(PngMetadataChecks.Chunk("tEXt","Title\0NATIVE_PNG_TEXT_VALUE"u8.ToArray())).Concat(png[33..]).ToArray(),
                _=>tiff
            };
            string path=Path.Combine(folder,format+little+(format=="jpeg"?".jpg":format=="png"?".png":".tif"));File.WriteAllBytes(path,source);
            byte[][] baseline;
            using(var original=File.OpenRead(path))
            {
                BitmapDecoder decoder=format switch
                {
                    "jpeg"=>new JpegBitmapDecoder(original,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad),
                    "png"=>new PngBitmapDecoder(original,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad),
                    _=>new TiffBitmapDecoder(original,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad)
                };
                baseline=decoder.Frames.Select(Pixels).ToArray();
            }
            // The document class owns the real input and ancestor leases; every
            // candidate passes its WIC validation before the exclusive file writer.
            IDisposable document;MetadataSelection? selection;Func<int[],CancellationToken,string> save;Window dialog;
            var app=Application.Current as App??new App();
            if(format=="jpeg"){var d=JpegMetadataDocument.Open(path,default);document=d;selection=d.Selection;save=d.SaveSelection;dialog=new JpegMetadataDialog(app,d);}
            else if(format=="png"){var d=PngMetadataDocument.Open(path,default);document=d;selection=d.Selection;save=d.SaveSelection;dialog=new PngMetadataDialog(app,d);}
            else{var d=TiffMetadataDocument.Open(path,default);document=d;selection=d.Selection;save=d.SaveSelection;dialog=new TiffMetadataDialog(app,d);}
            using(document)
            {
                Check(selection is not null,format+" prepares strict selection in both byte orders");var plan=selection!;
                var radio=Children(dialog).OfType<RadioButton>().Single(b=>Equals(b.Content,app.L("Select metadata to remove")));radio.IsChecked=true;
                var saveButton=Children(dialog).OfType<Button>().Single(b=>Equals(b.Content,app.L(format=="jpeg"?"Save JPEG Copy":format=="png"?"Save PNG Copy":"Save TIFF Copy")));
                Check(!saveButton.IsEnabled,format+" selective preview starts with no removal and save disabled");
                var group=Children(dialog).OfType<ComboBox>().Single();group.SelectedItem=app.L("Camera and device");
                var remove=Children(dialog).OfType<Button>().Single(b=>Equals(b.Content,app.L("Remove group")));remove.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Check(saveButton.IsEnabled,format+" group selection updates the preview and enables save");dialog.Close();
                reject(()=>File.Move(path,path+" moved"),"Selective source remains pinned");reject(()=>Directory.Move(folder,folder+" moved"),"Selective ancestor remains pinned");
                var before=MetadataSelectionChecks.ParseTiff(tiff,format=="tiff");
                foreach(string groupName in plan.Items.Where(f=>f.CanRemove).Select(f=>f.Group).Distinct())
                {
                    var ids=plan.Items.Where(f=>f.CanRemove&&f.Group==groupName).Select(f=>f.Id).ToArray();string output=System.Threading.Tasks.Task.Run(()=>save(ids,default)).GetAwaiter().GetResult();
                    var bytes=File.ReadAllBytes(output);byte[] body=format switch{"jpeg"=>ReadExif(bytes),"png"=>ReadChunks(bytes)["eXIf"].Single(),_=>bytes};var after=MetadataSelectionChecks.ParseTiff(body,format=="tiff");
                    foreach(var pair in before.Values)
                    {
                        bool removeTag=pair.Value.Group==groupName||(groupName=="Orientation"&&pair.Value.Tag==274);
                        if(removeTag)
                        {
                            Check(!after.Values.ContainsKey(pair.Key),format+" output tag absent "+pair.Key);
                            if(pair.Value.Bytes.Length>=8)Check(bytes.AsSpan().IndexOf(pair.Value.Bytes)<0,format+" selected value absent whole file "+pair.Key);
                        }
                        else Check(after.Values.TryGetValue(pair.Key,out var v)&&v.Type==pair.Value.Type&&v.Count==pair.Value.Count&&v.Bytes.SequenceEqual(pair.Value.Bytes),format+" unselected value exact "+pair.Key);
                    }
                    Check(before.Pixels.Count==after.Pixels.Count&&before.Pixels.All(p=>after.Pixels[p.Key].SequenceEqual(p.Value)),format+" every page strip exact");
                    if(format=="jpeg")Check(ScanBytes(jpeg).SequenceEqual(ScanBytes(bytes)),"JPEG compressed stream exact through real document writer");
                    if(format=="png")Check(ReadChunks(png)["IDAT"].SelectMany(b=>b).SequenceEqual(ReadChunks(bytes)["IDAT"].SelectMany(b=>b)),"PNG compressed data exact through real document writer");
                    using var stream=File.OpenRead(output);BitmapDecoder decoder=format switch
                    {
                        "jpeg"=>new JpegBitmapDecoder(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad),
                        "png"=>new PngBitmapDecoder(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad),
                        _=>new TiffBitmapDecoder(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad)
                    };
                    Check(decoder.Frames.Count==(format=="tiff"?2:1),format+" WIC reopens every real output page");
                    for(int i=0;i<decoder.Frames.Count;i++)
                    {
                        var frame=decoder.Frames[i];var raw=new byte[frame.PixelHeight*((frame.PixelWidth*frame.Format.BitsPerPixel+7)/8)];frame.CopyPixels(raw,(frame.PixelWidth*frame.Format.BitsPerPixel+7)/8,0);
                        Check(frame.PixelWidth==3&&frame.PixelHeight==2&&raw.Length>0&&Pixels(frame).SequenceEqual(baseline[i]),format+" WIC fully decodes identical output pixels on page "+i);
                        var metadata=frame.Metadata as BitmapMetadata;Check(metadata is not null,format+" WIC exposes metadata reader");
                        if(format!="png")
                        {
                            string query=(format=="jpeg"?"/app1":"")+"/ifd/{ushort=271}";
                            bool present=metadata!.ContainsQuery(query);Check(present!=(groupName=="Camera and device"),format+" WIC independently checks Make presence");
                            if(present)Check((metadata.GetQuery(query) as string)?.TrimEnd('\0')=="SEL_CAMERA_"+i,format+" WIC independently checks retained Make value");
                        }
                        else
                        {
                            const string query="/tEXt/{str=Title}";bool present=metadata!.ContainsQuery(query);Check(present!=(groupName=="PNG text"),"PNG WIC independently checks text removal");
                            if(present)Check(metadata.GetQuery(query) as string=="NATIVE_PNG_TEXT_VALUE","PNG WIC retained text exact");
                        }
                    }
                }
                int files=Directory.GetFiles(folder).Length;reject(()=>save(new[]{plan.Items.First(f=>f.CanRemove).Id},new CancellationToken(true)),"Selective cancelled save refused");Check(files==Directory.GetFiles(folder).Length,"Cancelled selection publishes nothing");
                Check(File.ReadAllBytes(path).SequenceEqual(source),format+" source remains byte-identical");
            }
            reject(()=>save(new[]{0},default),"Disposed selective document refused");
        }
        Check(!Directory.GetFiles(folder,".swivel-*.tmp").Any(),"No staging files left");
        Console.WriteLine("Selective metadata native assertions: "+assertions);
    }
    private static byte[] Pixels(BitmapSource frame)
    {
        var converted=new FormatConvertedBitmap(frame,PixelFormats.Bgra32,null,0);int stride=converted.PixelWidth*4;var bytes=new byte[stride*converted.PixelHeight];converted.CopyPixels(bytes,stride,0);return bytes;
    }
    private static byte[] ReadExif(byte[] b)
    {
        for(int at=2;at<b.Length;){while(b[at]==255)at++;int marker=b[at++];if(marker==0xda)break;int size=BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(at));if(marker==0xe1&&b.AsSpan(at+2,size-2).StartsWith("Exif\0\0"u8))return b.AsSpan(at+8,size-8).ToArray();at+=size;}throw new Exception("Missing EXIF");
    }
    private static byte[] ScanBytes(byte[] b)
    {
        for(int at=2;at<b.Length;){int start=at;while(b[at]==255)at++;int marker=b[at++];if(marker==0xda)return b[start..];int size=BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(at));at+=size;}throw new Exception("Missing scan");
    }
    private static Dictionary<string,List<byte[]>> ReadChunks(byte[] b)
    {
        var result=new Dictionary<string,List<byte[]>>();for(int at=8;at<b.Length;){int n=(int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at));string type=Encoding.ASCII.GetString(b,at+4,4);if(!result.TryGetValue(type,out var list))result[type]=list=new();list.Add(b.AsSpan(at+8,n).ToArray());at+=12+n;}return result;
    }
    private static IEnumerable<DependencyObject> Children(DependencyObject root)
    {yield return root;foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())foreach(var item in Children(child))yield return item;}
}
