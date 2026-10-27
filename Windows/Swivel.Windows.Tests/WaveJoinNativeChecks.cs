using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Swivel.Core;
using Swivel.Windows;

internal static class WaveJoinNativeChecks
{
    private static void Fixture(string path,int bits,int channels,int frames,int value)
    {
        int size=frames*channels*(bits/8);using var stream=File.Create(path);using var writer=new BinaryWriter(stream);
        writer.Write("RIFF"u8);writer.Write(36+size+(size&1));writer.Write("WAVEfmt "u8);writer.Write(16);writer.Write((ushort)1);writer.Write((ushort)channels);writer.Write(8000);writer.Write(8000*channels*(bits/8));writer.Write((ushort)(channels*(bits/8)));writer.Write((ushort)bits);writer.Write("data"u8);writer.Write(size);
        for(int i=0;i<frames*channels;i++){if(bits==8)writer.Write((byte)(value+128));else for(int b=0;b<bits/8;b++)writer.Write((byte)(value>>(b*8)));}if((size&1)!=0)writer.Write((byte)0);
    }
    internal static void Run(string directory,Action<bool,string> check,Action<Action,string> reject)
    {
        string folder=Path.Combine(directory,"wav-join");Directory.CreateDirectory(folder);var app=Application.Current as App??new App();
        foreach(int bits in new[]{8,16,24,32})foreach(int channels in new[]{1,2})
        {
            string left=Path.Combine(folder,bits+"-"+channels+" left");string right=Path.Combine(folder,bits+"-"+channels+" right");Directory.CreateDirectory(left);Directory.CreateDirectory(right);
            string a=Path.Combine(left,"先 🦜.wav"),b=Path.Combine(right,"后.wav");Fixture(a,bits,channels,3,-1);Fixture(b,bits,channels,80001,1);var originalA=File.ReadAllBytes(a);var originalB=File.ReadAllBytes(b);
            using(var document=WaveJoinDocument.Open(new[]{a,b},default))
            {
                check(document.Info.Frames==80004&&document.Info.Format.Bits==bits&&document.Paths.SequenceEqual(new[]{a,b}),"JoinNative source facts/order retained");
                reject(()=>File.WriteAllBytes(a,new byte[1]),"Join firstsource pinned");reject(()=>File.Move(b,b+" moved"),"Join lastsource pinned");reject(()=>Directory.Move(right,right+" moved"),"Join allsource ancestors pinned");
                var dialog=new WaveJoinDialog(app,document);var root=(DockPanel)dialog.Content;check(root.Children.OfType<ScrollViewer>().Count()==1&&root.Children.OfType<StackPanel>().Any(p=>DockPanel.GetDock(p)==Dock.Bottom),"Join native scroll/fixedfooter");
                var body=(StackPanel)root.Children.OfType<ScrollViewer>().Single().Content;var list=body.Children.OfType<ListBox>().Single();var moves=body.Children.OfType<WrapPanel>().Single();list.SelectedIndex=1;var up=moves.Children.OfType<Button>().First();up.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));check(list.Items[0].ToString()!.Contains(b)&&body.Children.OfType<TextBlock>().Any(t=>t.Text.Contains(b)),"Join MoveUp updates firstrow and full output source");dialog.Close();dialog.FinishPreviewAsync().GetAwaiter().GetResult();
                var order=new[]{1,0};byte[] preview=document.Preview(order,default);string first=document.Save(order,default),second=document.Save(order,default);
                check(Path.GetDirectoryName(first)==right&&first.EndsWith(" Joined.wav")&&second.EndsWith(" Joined 2.wav"),"Joinreordered first source sets exclusive output location");
                using(var saved=File.OpenRead(first))using(var expected=new MemoryStream())
                {var info=PcmWave.Read(saved,default);check(info.Frames==80004&&info.Bits==bits&&info.Channels==channels,"Joinnative output format/totalframe");PcmWave.Export(saved,expected,new(0,info.Seconds),default,true);check(expected.ToArray().SequenceEqual(preview),"Joinpreview matches saveddecode");}
                reject(()=>document.Save(new[]{0,0},default),"Join rejects duplicate/incomplete permutation");int count=Directory.GetFiles(right).Length;
                reject(()=>document.Save(order,new CancellationToken(true)),"Joinprecancelledsave");check(count==Directory.GetFiles(right).Length&&!Directory.GetFiles(right,".swivel-*.tmp").Any(),"Joincancelnostage/output");
            }
            check(File.ReadAllBytes(a).SequenceEqual(originalA)&&File.ReadAllBytes(b).SequenceEqual(originalB),"Join alloriginalbytes unchanged");Directory.Move(right,right+" released");check(Directory.Exists(right+" released"),"Join closes allsource/ancestorleases");
        }
        string valid=Path.Combine(folder,"valid.wav"),bad=Path.Combine(folder,"bad.wav");Fixture(valid,16,1,1,0);File.WriteAllBytes(bad,new byte[44]);
        foreach(int count in new[]{2,32}){var paths=Enumerable.Repeat(valid,count-1).Append(bad).ToArray();reject(()=>WaveJoinDocument.Open(paths,default).Dispose(),"Join lastsource load failure releases earlierleases");File.Move(valid,valid+" moved");File.Move(valid+" moved",valid);}
        reject(()=>WaveJoinDocument.Open(new[]{valid,valid},new CancellationToken(true)).Dispose(),"Joinpre-cancelledload");
        using(var doc=WaveJoinDocument.Open(new[]{valid,valid},default))check(doc.Info.Frames==2&&doc.Info.SourceBytes==new FileInfo(valid).Length*2,"Direct duplicate paths count/audio repeated; Appselection deduplicates beforethisflow");
        Directory.Move(folder,folder+" released");check(Directory.Exists(folder+" released"),"Joinfailure/precancel noleakedancestor");
    }
}
