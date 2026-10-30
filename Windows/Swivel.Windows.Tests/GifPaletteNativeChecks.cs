using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Swivel.Core;
using Swivel.Windows;

internal static class GifPaletteNativeChecks
{
    private static IEnumerable<T> Find<T>(DependencyObject root)where T:DependencyObject
    {if(root is T t)yield return t;foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())foreach(var item in Find<T>(child))yield return item;}
    private static void Pump(Func<bool> complete)
    {
        var frame=new DispatcherFrame();var clock=Stopwatch.StartNew();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(5)};
        timer.Tick+=(_,_)=>{if(complete()||clock.Elapsed>TimeSpan.FromSeconds(20))frame.Continue=false;};timer.Start();try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}if(!complete())throw new TimeoutException("Palette UI task did not settle.");
    }
    private static void Finish(GifPaletteDialog dialog){var task=dialog.FinishAsync();if(!task.IsCompleted)Pump(()=>task.IsCompleted);task.GetAwaiter().GetResult();}
    internal static void Run(string directory,Action<bool,string> check,Action<Action,string> reject)
    {
        var previous=SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            RunIsolated(directory,check,reject);
        }
        finally{SynchronizationContext.SetSynchronizationContext(previous);}
    }
    private static void RunIsolated(string directory,Action<bool,string> check,Action<Action,string> reject)
    {
        string folder=Path.Combine(directory,"gif-palette");Directory.CreateDirectory(folder);string path=Path.Combine(folder,"调色 🦜.png");var pixels=new byte[64*65*4];
        for(int i=0;i<pixels.Length;i+=4){pixels[i]=(byte)(i/4);pixels[i+1]=(byte)(i/256);pixels[i+2]=(byte)(i/16);pixels[i+3]=(byte)(i%68==0?0:255);}
        var bitmap=BitmapSource.Create(64,65,96,96,PixelFormats.Bgra32,null,pixels,64*4);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var s=File.Create(path))encoder.Save(s);byte[] original=File.ReadAllBytes(path);var app=Application.Current as App??throw new InvalidOperationException("An isolated validation App is required.");
        if(!app.IsValidationHost)throw new InvalidOperationException("The palette fixture cannot pump a normal product App.");
        using(var document=GifPaletteDocument.Open(path,default))
        {
            reject(()=>File.WriteAllBytes(path,new byte[1]),"Palette native source held");reject(()=>File.Move(path,path+" moved"),"Palette native source rename held");reject(()=>Directory.Move(folder,folder+" moved"),"Palette native ancestors held");
            var prepared=document.Prepare(16,default);using var expected=new MemoryStream();prepared.Result.Write(expected,default);string saved=document.Save(prepared,default),second=document.Save(prepared,default);
            check(saved!=second&&Path.GetDirectoryName(saved)==folder&&saved.EndsWith(" reduced colors.gif")&&File.ReadAllBytes(saved).SequenceEqual(expected.ToArray()),"Palette native saves exact same prepared full result, exclusive beside input");
            using(var s=File.OpenRead(saved)){var decoded=GifImage.Read(s,default);check(decoded.Width==64&&decoded.Height==65&&prepared.Result.Colors<=16,"Palette native saved GIF decodes at original dimensions");}
            var dithered=document.Prepare(16,GifDither.FloydSteinberg,default);using var expectedDither=new MemoryStream();dithered.Result.Write(expectedDither,default);
            string savedDither=document.Save(dithered,default);
            check(File.ReadAllBytes(savedDither).SequenceEqual(expectedDither.ToArray())&&dithered.Result.Dither==GifDither.FloydSteinberg,"Dither export uses exactly the prepared original-size result");
            using(var other=GifPaletteDocument.Open(path,default))reject(()=>other.Save(prepared,default),"Palette native cross-document preview rejected");
            int count=Directory.GetFiles(folder).Length;reject(()=>document.Save(prepared,new CancellationToken(true)),"Palette native precancelled save");check(count==Directory.GetFiles(folder).Length&&!Directory.GetFiles(folder,".swivel-*.tmp").Any(),"Palette native cancelled save leaves no stage/output");
            var dialog=new GifPaletteDialog(app,document);var combo=Find<ComboBox>(dialog).Single();var generate=Find<Button>(dialog).Single(b=>Equals(b.Content,app.L("Generate GIF Preview")));var save=Find<Button>(dialog).Single(b=>Equals(b.Content,app.L("Save GIF Copy")));
            check((int)combo.SelectedItem==256&&!save.IsEnabled&&dialog.Selection is null,"Palette native explicit preview required initially");generate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));check(!save.IsEnabled,"Palette native generation clears old save");Pump(()=>save.IsEnabled);
            check(app.Home is null&&app.Awake is null&&app.ShutdownMode==ShutdownMode.OnExplicitShutdown,"Palette native isolated host never starts product services");
            check(Find<Image>(dialog).Count(x=>x.Source is not null)==2&&Find<TextBlock>(dialog).Any(x=>x.Text.Contains("256")),"Palette native before/after previews and full-result palette facts");
            var dither=Find<CheckBox>(dialog).Single();check(dither.IsChecked==false,"Dither defaults off");dither.IsChecked=true;
            check(!save.IsEnabled&&Find<Image>(dialog).Count(x=>x.Source is not null)==1,"Dither checkbox invalidates old output immediately");
            generate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump(()=>save.IsEnabled);
            dither.IsChecked=false;check(!save.IsEnabled,"Disabling dither also invalidates prepared output");
            combo.SelectedItem=16;check(!save.IsEnabled&&dialog.Selection is null&&Find<Image>(dialog).Count(x=>x.Source is not null)==1,"Palette native parameter change invalidates old output immediately");generate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump(()=>save.IsEnabled);
            var root=(DockPanel)dialog.Content;check(root.Children.OfType<ScrollViewer>().Count()==1&&root.Children.OfType<StackPanel>().Any(x=>DockPanel.GetDock(x)==Dock.Bottom),"Palette native fixedfooter and scroll body");
            var stop=Find<Button>(dialog).Single(b=>Equals(b.Content,app.L("Stop")));stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));check(!save.IsEnabled,"Palette native Stop clears prepared output");
            generate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));combo.SelectedItem=32;Finish(dialog);check(!save.IsEnabled,"Palette native cancelled stale generation cannot enable save");dialog.Close();Finish(dialog);
            var late=new GifPaletteDialog(app,document);Find<Button>(late).Single(b=>Equals(b.Content,app.L("Generate GIF Preview"))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));late.Close();Finish(late);check(late.Selection is null,"Palette native close awaits generation without accepted selection");
        }
        check(File.ReadAllBytes(path).SequenceEqual(original),"Palette native original bytes unchanged");var disposed=GifPaletteDocument.Open(path,default);var prior=disposed.Prepare(16,default);disposed.Dispose();reject(()=>disposed.Save(prior,default),"Palette native disposedsave refused");reject(()=>disposed.Prepare(16,default),"Palette native disposedprepare refused");
        reject(()=>GifPaletteDocument.Open(path,new CancellationToken(true)).Dispose(),"Palette native pre-cancelledopen");reject(()=>GifPaletteDocument.Open(path+".jxl",default).Dispose(),"Palette native unsupported extension refused");Directory.Move(folder,folder+" released");check(Directory.Exists(folder+" released"),"Palette native all source/ancestor leases released");
    }
}
