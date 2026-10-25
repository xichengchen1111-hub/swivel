using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Swivel.Core;
using Swivel.Windows;

internal static class QrToolsChecks
{
    public static void Run(string directory,Action<bool,string> check,Action<Action,string> reject)
    {
        string first = QrTools.Save("你好 🦜",directory,CancellationToken.None), second = QrTools.Save("second",directory,CancellationToken.None);
        check(Path.GetFileName(first) == "QR code.png" && Path.GetFileName(second) == "QR code 2.png","QR exclusive output uses selected folder and never replaces existing code");
        using (var image = ImageTools.Open(first,CancellationToken.None))
        {
            var pixels = ImageTools.ReadPixels(image.Bitmap,0,CancellationToken.None); int size = pixels.Width;
            check(size == 232 && pixels.Height == size,"QR PNG preserves exact integer module dimensions");
            check(Enumerable.Range(0,size*32).All(i => pixels.Bgra[i*4] == 255 && pixels.Bgra[i*4+1] == 255 && pixels.Bgra[i*4+2] == 255 && pixels.Bgra[i*4+3] == 255),"QR PNG retains opaque white quiet zone");
        }
        string longText = new('界',90);
        string dense = QrTools.Save(longText,directory,CancellationToken.None);
        using (var image = ImageTools.Open(dense,CancellationToken.None))
        {
            var pixels = ImageTools.ReadPixels(image.Bitmap,0,CancellationToken.None); int size = pixels.Width;
            check(size == 520 && pixels.Height == size,"Version 10 QR PNG keeps eight pixels per module and four-module borders");
            byte[] expected = QrCode.Encode(longText).Raster();
            check(Enumerable.Range(0,size*size).All(i => pixels.Bgra[i*4] == expected[i] && pixels.Bgra[i*4+1] == expected[i] && pixels.Bgra[i*4+2] == expected[i] && pixels.Bgra[i*4+3] == 255),"Dense QR PNG preserves every black/white module and opaque quiet zone through the native encoder");
            check(Enumerable.Range(0,size*size).Where(i => i/size < 32 || i/size >= size-32 || i%size < 32 || i%size >= size-32).All(i => pixels.Bgra[i*4] == 255),"Dense QR retains all four full quiet-zone edges");
        }
        var ascii = QrTools.Render(new string('x',270));
        check(ascii.IsFrozen && ascii.PixelWidth == 520 && ascii.PixelHeight == 520,"Maximum ASCII payload renders a frozen full-resolution clipboard image");
        var maximum=QrTools.Render(new string('x',QrCode.MaxBytes));
        check(maximum.IsFrozen&&maximum.PixelWidth==1480&&maximum.PixelHeight==1480,"Full Model2 version40 clipboard/PNG image retains eight pixels per module");
        string maximumPng=QrTools.Save(new string('x',QrCode.MaxBytes),directory,default);
        using(var fullImage=ImageTools.Open(maximumPng,default))
        {
            var fullPixels=ImageTools.ReadPixels(fullImage.Bitmap,0,default);byte[] expected=QrCode.Encode(new string('x',QrCode.MaxBytes)).Raster();
            check(fullPixels.Width==1480&&fullPixels.Height==1480&&Enumerable.Range(0,1480*1480).All(i=>fullPixels.Bgra[4*i]==expected[i]&&fullPixels.Bgra[4*i+1]==expected[i]&&fullPixels.Bgra[4*i+2]==expected[i]&&fullPixels.Bgra[4*i+3]==255),"Version40 native PNG preserves all full-resolution modules, alpha and quiet zone");
        }
        string mediumMax=new('x',QrCode.MaxMediumBytes);string mediumPng=QrTools.Save(mediumMax,QrCorrection.Medium,directory,default);
        using(var mediumImage=ImageTools.Open(mediumPng,default))
        {
            var pixels=ImageTools.ReadPixels(mediumImage.Bitmap,0,default);byte[] expected=QrCode.Encode(mediumMax,QrCorrection.Medium).Raster();
            check(pixels.Width==1480&&pixels.Height==1480&&Enumerable.Range(0,1480*1480).All(i=>pixels.Bgra[4*i]==expected[i]&&pixels.Bgra[4*i+1]==expected[i]&&pixels.Bgra[4*i+2]==expected[i]&&pixels.Bgra[4*i+3]==255),"Explicit M native PNG retains selected correction and all full-resolution modules");
        }
        foreach(var level in new[]{QrCorrection.Quartile,QrCorrection.High})
        {
            string payload=new('x',QrCode.Limit(level));string output=QrTools.Save(payload,level,directory,default);
            using var image=ImageTools.Open(output,default);var pixels=ImageTools.ReadPixels(image.Bitmap,0,default);byte[] expected=QrCode.Encode(payload,level).Raster();
            check(pixels.Width==1480&&pixels.Height==1480&&Enumerable.Range(0,1480*1480).All(i=>pixels.Bgra[4*i]==expected[i]&&pixels.Bgra[4*i+1]==expected[i]&&pixels.Bgra[4*i+2]==expected[i]&&pixels.Bgra[4*i+3]==255),"Q/H PNG retains the selected correction, exact pixels, alpha and quiet zone");
        }
        byte[] before = File.ReadAllBytes(first); int count = Directory.GetFiles(directory).Length;
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); reject(() => QrTools.Save("cancel",directory,cancelled.Token),"Cancelled QR save refused");
        reject(() => QrTools.Save("",directory,CancellationToken.None),"Invalid QR save refused");
        reject(() => QrTools.Save(new string('x',QrCode.MaxBytes+1),directory,CancellationToken.None),"Over-capacity QR save is refused without truncation");
        reject(() => NativeFiles.WriteInDirectory(directory,"bad/path","png",stream => stream.WriteByte(1),CancellationToken.None),"Directory output name cannot escape selected folder");
        reject(() => NativeFiles.WriteInDirectory(directory,"incomplete","png",stream => { stream.WriteByte(1); throw new IOException(); },CancellationToken.None),"Failed directory output cleaned by held handle");
        check(Directory.GetFiles(directory).Length == count && File.ReadAllBytes(first).SequenceEqual(before),"Failed QR saves leave no temporary file and preserve existing images");
        var entries=new[]{new QrEntry(Guid.NewGuid().ToString("N"),"Saved QR","你好\r\n🦜","Travel",QrCorrection.Medium)};
        string backup=QrLibraryFile.Save(entries,directory,default),backup2=QrLibraryFile.Save(entries,directory,default);
        check(backup.EndsWith("Swivel QR library.json") && backup2.EndsWith("Swivel QR library 2.json"),"QR backup is exclusive in chosen folder");
        byte[] original=File.ReadAllBytes(backup);
        using(var library=QrLibraryFile.Open(backup,default))
        {
            check(library.Entries.Count==1 && library.Entries[0].Text==entries[0].Text && library.Entries[0].Group=="Travel" && library.Entries[0].Correction==QrCorrection.Medium,"QR backup native exact content round trip");
            reject(()=>File.WriteAllText(backup,"changed"),"Import source remains pinned through preview");
        }
        count=Directory.GetFiles(directory).Length;reject(()=>QrLibraryFile.Save(entries,directory,new CancellationToken(true)),"Cancelled backup creates no file");
        reject(()=>QrLibraryFile.Open(backup,new CancellationToken(true)).Dispose(),"Cancelled import reads no file");
        check(Directory.GetFiles(directory).Length==count && File.ReadAllBytes(backup).SequenceEqual(original),"QR cancelled operations leave source and directory unchanged");
        var full=Enumerable.Range(0,32).Select(i=>new QrEntry(Guid.NewGuid().ToString("N"),new string((char)(0x4e00+i),48),new string('<',QrCode.MaxBytes),new string('組',32))).ToArray();
        string largeBackup=QrLibraryFile.Save(full,directory,default);
        check(new FileInfo(largeBackup).Length>65536 && new FileInfo(largeBackup).Length<=QrLibrary.MaxBytes,"Native backup accepts the expanded full library beyond the previous 64KiB budget");
        using(var library=QrLibraryFile.Open(largeBackup,default))
            check(library.Entries.Select(x=>(x.Name,x.Text,x.Group)).SequenceEqual(full.Select(x=>(x.Name,x.Text,x.Group))),"Expanded native QR library source round-trips every character without truncation");

        // Construct only: do not run the app, display a window, touch settings,
        // invoke the picker or copy to the clipboard.
        var app = Application.Current as App ?? new App();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var dialog = new QrDialog(app);
        try
        {
            var nodes = Descendants(dialog).ToArray();
            var field = nodes.OfType<TextBox>().Single(x => AutomationProperties.GetName(x) == app.L("QR Text"));
            var actions = nodes.OfType<Button>().Where(x => Equals(x.Content,app.L("Copy Image")) || Equals(x.Content,app.L("Save PNG…")) || Equals(x.Content,app.L("Save to QR Library"))).ToArray();
            var preview = nodes.OfType<Image>().Single();
            check(field.MaxLength == 0 && actions.Length == 3,"QR input has no silent character truncation and exposes three native output actions");
            field.Text = new string('x',QrCode.MaxBytes+1);
            check(field.Text.Length == QrCode.MaxBytes+1 && actions.All(x => !x.IsEnabled) && preview.Source is null,"Over-capacity input stays intact and disables every stale output action");
            check(nodes.OfType<TextBlock>().Any(x => x.Text == string.Format(app.L("{0} / {1} UTF-8 bytes"),QrCode.MaxBytes+1,QrCode.MaxBytes)),"QR capacity feedback includes invalid but bounded over-budget input");
            field.Text = new string('x',10000);
            check(field.Text.Length == 10000 && actions.All(x => !x.IsEnabled) && preview.Source is null,"Large pasted content remains editable without being encoded or truncated");
            field.Text=new string('x',QrCode.MaxBytes);
            check(actions.All(x=>x.IsEnabled)&&preview.Source is not null&&preview.Width==370&&preview.Height==370,"Maximum valid input has full raster with bounded integer-module native preview");
            var footer=((DockPanel)dialog.Content).Children.OfType<WrapPanel>().Single();
            check(DockPanel.GetDock(footer)==Dock.Bottom&&footer.Children.OfType<Button>().Count()==3,"QR Copy/Save/Close remain in fixed native footer");
            var correction=nodes.OfType<ComboBox>().Single(x=>AutomationProperties.GetName(x)==app.L("QR Error Correction"));
            check(correction.SelectedIndex==0,"New QR defaults to existing L behavior");
            correction.SelectedIndex=1;
            check(field.Text.Length==QrCode.MaxBytes&&actions.All(x=>!x.IsEnabled)&&preview.Source is null,"Switching max L payload to M preserves text and invalidates stale outputs");
            check(nodes.OfType<TextBlock>().Any(x=>x.Text==string.Format(app.L("{0} / {1} UTF-8 bytes"),QrCode.MaxBytes,QrCode.MaxMediumBytes)),"M capacity feedback uses its own byte ceiling");
            field.Text=mediumMax;check(actions.All(x=>x.IsEnabled)&&preview.Source is System.Windows.Media.Imaging.BitmapSource mediumBitmap&&mediumBitmap.PixelWidth==1480,"Valid max M regenerates the selected correction image");
            check(correction.Items.Count==4,"QR native selector exposes all four standard correction levels");
            foreach(var level in new[]{QrCorrection.Quartile,QrCorrection.High})
            {
                field.Text=new string('x',QrCode.MaxBytes);correction.SelectedIndex=(int)level;
                check(field.Text.Length==QrCode.MaxBytes&&actions.All(x=>!x.IsEnabled)&&preview.Source is null,"Q/H switch never truncates large text or leaves stale outputs");
                check(nodes.OfType<TextBlock>().Any(x=>x.Text==string.Format(app.L("{0} / {1} UTF-8 bytes"),QrCode.MaxBytes,QrCode.Limit(level))),"Q/H feedback uses selected byte capacity");
                field.Text=new string('x',QrCode.Limit(level));
                check(actions.All(x=>x.IsEnabled)&&preview.Source is System.Windows.Media.Imaging.BitmapSource qhBitmap&&qhBitmap.PixelWidth==1480,"Q/H max payload restores every explicit output with full dense raster");
                check(nodes.OfType<TextBlock>().Any(x=>x.Text==string.Format(app.L("QR version {0} · {1} × {1} modules · {2} error correction"),40,177,QrCode.Level(level))),"Q/H native details carry actual correction level");
            }
            field.Text=mediumMax;
            correction.SelectedIndex=0;check(preview.Source is System.Windows.Media.Imaging.BitmapSource lowBitmap&&lowBitmap.PixelWidth<1480,"Switch back to L regenerates a smaller valid version without changing text");
            field.Text = longText;
            check(actions.All(x => x.IsEnabled) && preview.Source is not null && preview.Width == 260 && preview.Height == 260,"Valid long Unicode restores output with an integer-module preview that fits the minimum window width");
            check(nodes.OfType<TextBlock>().Any(x => x.Text == string.Format(app.L("QR version {0} · {1} × {1} modules · {2} error correction"),10,57,"L")),"QR preview identifies version, module count and error correction");
            field.Text = "short";
            check(actions.All(x => x.IsEnabled) && preview.Width == 116 && preview.Height == 116,"Editing a dense code back to a short payload refreshes the complete preview");
        }
        finally { dialog.Close(); }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
        { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }
}
