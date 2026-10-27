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

internal static class WaveLoopNativeChecks
{
    private static IEnumerable<T> Find<T>(DependencyObject root) where T:DependencyObject
    {if(root is T t)yield return t;foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())foreach(var item in Find<T>(child))yield return item;}
    private static void Fixture(string path,int bits,int channels,int frames)
    {
        int size=frames*channels*(bits/8);using var s=File.Create(path);using var w=new BinaryWriter(s);
        w.Write("RIFF"u8);w.Write(36+size+(size&1));w.Write("WAVEfmt "u8);w.Write(16);w.Write((ushort)1);w.Write((ushort)channels);w.Write(8000);w.Write(8000*channels*(bits/8));w.Write((ushort)(channels*(bits/8)));w.Write((ushort)bits);w.Write("data"u8);w.Write(size);
        for(int i=0;i<frames*channels;i++){int value=(int)(Math.Pow(2,bits-1)*(i%11-5)/32);if(bits==8)w.Write((byte)(value+128));else for(int b=0;b<bits/8;b++)w.Write((byte)(value>>(b*8)));}if((size&1)!=0)w.Write((byte)0);
    }
    internal static void Run(string directory,Action<bool,string> check,Action<Action,string> reject)
    {
        string folder=Path.Combine(directory,"wav-loop");Directory.CreateDirectory(folder);var app=Application.Current as App??new App();
        foreach(int bits in new[]{8,16,24,32})foreach(int channels in new[]{1,2})foreach(int length in new[]{4096,4097})
        {
            string label=bits+"-"+channels+"-"+length;string a=Path.Combine(folder,label+" main.wav"),b=Path.Combine(folder,label+" music.wav");Fixture(a,bits,channels,12000);Fixture(b,24,2,length);byte[] originalA=File.ReadAllBytes(a),originalB=File.ReadAllBytes(b);
            using(var main=WaveDocument.Open(a,default))using(var music=WaveDocument.Open(b,default))
            {
                var edit=new AudioEdit(.01,1.4,.8,.05,.1);var settings=new WaveMusic(.015,.4,true,.05,.1,new(),100);
                reject(()=>File.WriteAllBytes(a,new byte[1]),"LoopNative main held");reject(()=>File.Move(b,b+" moved"),"LoopNative music held");reject(()=>Directory.Move(folder,folder+" moved"),"LoopNative ancestors held");
                byte[] preview=main.MixPreview(music,edit,settings,default);string path=main.SaveMix(music,edit,settings,default),second=main.SaveMix(music,edit,settings,default);
                using(var left=new MemoryStream(originalA))using(var right=new MemoryStream(originalB))using(var output=new MemoryStream())
                {WaveMixer.Export(left,right,output,edit,settings,default);check(File.ReadAllBytes(path).SequenceEqual(output.ToArray()),"LoopNative SaveMix matches core crossfade plus activity/fades");output.SetLength(0);WaveMixer.Export(left,right,output,edit,settings,default,true);check(output.ToArray().SequenceEqual(preview),"LoopNative MixPreview matches identical core path");}
                check(path!=second&&Path.GetDirectoryName(path)==folder,"LoopNative exclusive beside-main copies");int before=Directory.GetFiles(folder).Length;
                reject(()=>main.SaveMix(music,edit,settings with{LoopCrossfadeMilliseconds=1001},default),"LoopNative bad crossfade before stage");reject(()=>main.SaveMix(music,edit,settings,new CancellationToken(true)),"LoopNative precancelledsave");check(before==Directory.GetFiles(folder).Length&&!Directory.GetFiles(folder,".swivel-*.tmp").Any(),"LoopNative invalid/cancel no output or stage");
                var dialog=new WaveDialog(app,main,music);var loop=Find<CheckBox>(dialog).Single(x=>Equals(x.Content,app.L("Loop Background Music")));var field=Find<TextBox>(dialog).Single(x=>AutomationProperties.GetName(x)==app.L("Loop Crossfade (ms)"));
                check(loop.IsChecked!=true&&!field.IsEnabled&&field.Text=="0"&&dialog.MusicSettings.LoopCrossfadeMilliseconds==0,"LoopNative default zero and disabled");field.Text="invalid";
                check(!Find<TextBlock>(dialog).Any(x=>x.Text==app.L(WaveMusic.CrossfadeInvalid)),"LoopNative disabled invalid text ignored");loop.IsChecked=true;
                check(field.IsEnabled&&Find<TextBlock>(dialog).Any(x=>x.Text==app.L(WaveMusic.CrossfadeInvalid)),"LoopNative enable-invalid feedback");field.Text="100";check(!Find<TextBlock>(dialog).Any(x=>x.Text==app.L(WaveMusic.CrossfadeInvalid)),"LoopNative valid crossfade clears feedback");field.Text="1001";check(Find<TextBlock>(dialog).Any(x=>x.Text==app.L(WaveMusic.CrossfadeInvalid)),"LoopNative oversize feedback");loop.IsChecked=false;check(!field.IsEnabled&&!Find<TextBlock>(dialog).Any(x=>x.Text==app.L(WaveMusic.CrossfadeInvalid)),"LoopNative turningoff ignores retained invalid text");
                var root=(DockPanel)dialog.Content;check(root.Children.OfType<ScrollViewer>().Count()==1&&root.Children.OfType<StackPanel>().Any(x=>DockPanel.GetDock(x)==Dock.Bottom),"LoopNative scroll body and fixed footer");dialog.Close();dialog.FinishPreviewAsync().GetAwaiter().GetResult();
                var plain=new WaveDialog(app,main);check(!Find<TextBox>(plain).Any(x=>AutomationProperties.GetName(x)==app.L("Loop Crossfade (ms)")),"LoopNative ordinaryeditor has no loop crossfade");plain.Close();plain.FinishPreviewAsync().GetAwaiter().GetResult();
            }
            check(File.ReadAllBytes(a).SequenceEqual(originalA)&&File.ReadAllBytes(b).SequenceEqual(originalB),"LoopNative originals unchanged");
        }
        Directory.Move(folder,folder+" released");check(Directory.Exists(folder+" released"),"LoopNative all source/ancestor leases released");
    }
}
