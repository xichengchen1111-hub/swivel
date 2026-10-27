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

internal static class WaveDuckingNativeChecks
{
    private static IEnumerable<T> Find<T>(DependencyObject root) where T:DependencyObject
    {if(root is T t)yield return t;foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())foreach(var item in Find<T>(child))yield return item;}
    private static void Fixture(string path,int bits,int channels,int value)
    {
        const int frames=8001;int size=frames*channels*(bits/8);using var s=File.Create(path);using var w=new BinaryWriter(s);
        w.Write("RIFF"u8);w.Write(36+size+(size&1));w.Write("WAVEfmt "u8);w.Write(16);w.Write((ushort)1);w.Write((ushort)channels);w.Write(8000);w.Write(8000*channels*(bits/8));w.Write((ushort)(channels*(bits/8)));w.Write((ushort)bits);w.Write("data"u8);w.Write(size);
        for(int i=0;i<frames*channels;i++){int sample=i/channels<1600?value:0;if(bits==8)w.Write((byte)(sample+128));else for(int b=0;b<bits/8;b++)w.Write((byte)(sample>>(b*8)));}if((size&1)!=0)w.Write((byte)0);
    }
    internal static void Run(string directory,Action<bool,string> check,Action<Action,string> reject)
    {
        string folder=Path.Combine(directory,"wav-activity");Directory.CreateDirectory(folder);var app=Application.Current as App??new App();
        foreach(int bits in new[]{8,16,24,32})foreach(int channels in new[]{1,2})
        {
            string a=Path.Combine(folder,bits+"-"+channels+" main.wav"),b=Path.Combine(folder,bits+"-"+channels+" track.wav");int value=(int)(Math.Pow(2,bits-1)/8);Fixture(a,bits,channels,value);Fixture(b,16,1,1024);byte[] originalA=File.ReadAllBytes(a),originalB=File.ReadAllBytes(b);
            using(var main=WaveDocument.Open(a,default))using(var music=WaveDocument.Open(b,default))
            {
                var edit=new AudioEdit(0,main.Info.Seconds);var settings=new WaveMusic(Loop:true,Ducking:new());
                reject(()=>File.WriteAllBytes(a,new byte[1]),"Activitynative primary pinned");reject(()=>File.Move(b,b+" moved"),"Activitynative music pinned");reject(()=>Directory.Move(folder,folder+" moved"),"Activitynative ancestors pinned");
                byte[] preview=main.MixPreview(music,edit,settings,default);string path=main.SaveMix(music,edit,settings,default),path2=main.SaveMix(music,edit,settings,default);
                using(var left=new MemoryStream(originalA))using(var right=new MemoryStream(originalB))using(var expected=new MemoryStream())
                {WaveMixer.Export(left,right,expected,edit,settings,default);check(File.ReadAllBytes(path).SequenceEqual(expected.ToArray()),"Activitynative SaveMix equals core output under held input");expected.SetLength(0);WaveMixer.Export(left,right,expected,edit,settings,default,true);check(preview.SequenceEqual(expected.ToArray()),"Activitynative MixPreview uses identical core preview");}
                check(path!=path2&&Path.GetDirectoryName(path)==folder,"Activitynative exclusive beside-input publication");int before=Directory.GetFiles(folder).Length;
                reject(()=>main.SaveMix(music,edit,settings,new CancellationToken(true)),"Activitynative cancelled save");check(before==Directory.GetFiles(folder).Length&&!Directory.GetFiles(folder,".swivel-*.tmp").Any(),"Activitynative cancellation creates no output/stage");
                var dialog=new WaveDialog(app,main,music);var duck=Find<CheckBox>(dialog).Single(x=>Equals(x.Content,app.L("Lower Music During Active Main Audio")));
                var threshold=Find<TextBox>(dialog).Single(x=>AutomationProperties.GetName(x)==app.L("Activity Threshold (dBFS)"));var level=Find<TextBox>(dialog).Single(x=>AutomationProperties.GetName(x)==app.L("Music During Activity (%)"));
                check(duck.IsChecked!=true&&!threshold.IsEnabled&&!level.IsEnabled&&dialog.MusicSettings.Ducking is null,"Activitynative default off and fields disabled");
                threshold.Text="invalid";level.Text="NaN";check(!Find<TextBlock>(dialog).Any(x=>x.Text==app.L(WaveDucking.Invalid)),"Activitynative disabled invalid settings ignored");
                duck.IsChecked=true;check(threshold.IsEnabled&&level.IsEnabled&&Find<TextBlock>(dialog).Any(x=>x.Text==app.L(WaveDucking.Invalid)),"Activitynative enabled invalid threshold visible");
                threshold.Text="-35";level.Text="20";check(!Find<TextBlock>(dialog).Any(x=>x.Text==app.L(WaveDucking.Invalid)),"Activitynative valid values clear error");duck.IsChecked=false;
                dialog.Close();dialog.FinishPreviewAsync().GetAwaiter().GetResult();
                var plain=new WaveDialog(app,main);check(!Find<CheckBox>(plain).Any(x=>Equals(x.Content,app.L("Lower Music During Active Main Audio"))),"Activitynative control only shown for music mixing");plain.Close();plain.FinishPreviewAsync().GetAwaiter().GetResult();
            }
            check(File.ReadAllBytes(a).SequenceEqual(originalA)&&File.ReadAllBytes(b).SequenceEqual(originalB),"Activitynative both source bytes preserved");
        }
        Directory.Move(folder,folder+" released");check(Directory.Exists(folder+" released"),"Activitynative all ancestor leases released");
    }
}
