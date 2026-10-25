using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Swivel.Core;
using Swivel.Windows;

internal static class G711NativeChecks
{
    private static byte[] Fixture(bool mu,int channels,int frames)
    {
        using var output=new MemoryStream();using var w=new BinaryWriter(output);int bytes=frames*channels;
        w.Write("RIFF"u8);w.Write(50+bytes+(bytes&1));w.Write("WAVEfmt "u8);w.Write(18);w.Write((ushort)(mu?7:6));w.Write((ushort)channels);w.Write(8000);w.Write(8000*channels);w.Write((ushort)channels);w.Write((ushort)8);w.Write((ushort)0);w.Write("fact"u8);w.Write(4);w.Write(frames);w.Write("data"u8);w.Write(bytes);
        for(int i=0;i<bytes;i++)w.Write((byte)(i*31));if((bytes&1)!=0)w.Write((byte)0);return output.ToArray();
    }
    internal static void Run(string directory,Action<bool,string> check,Action<Action,string> reject)
    {
        string folder=Path.Combine(directory,"g711 🦜");Directory.CreateDirectory(folder);var app=(App)Application.Current;
        foreach(bool mu in new[]{false,true})foreach(int channels in new[]{1,2})
        {
            string path=Path.Combine(folder,(mu?"mu":"a")+channels+".wav");var original=Fixture(mu,channels,80001);File.WriteAllBytes(path,original);
            using(var document=G711Document.Open(path,default))
            {
                check(document.Info.MuLaw==mu&&document.Info.Rate==8000&&document.Info.Channels==channels&&document.Info.Frames==80001,"NativeG711 retains codeword/timebase facts");
                reject(()=>File.WriteAllBytes(path,new byte[1]),"G711source held");reject(()=>Directory.Move(folder,folder+" moved"),"G711ancestors held");
                var dialog=new G711Dialog(app,document);var root=(DockPanel)dialog.Content;
                check(root.Children.OfType<ScrollViewer>().Count()==1&&root.Children.OfType<StackPanel>().Any(p=>DockPanel.GetDock(p)==Dock.Bottom)&&dialog.MinHeight==400&&dialog.MinWidth==500,"G711Fluent scroll/fixedfooter/workarea controls");dialog.Close();dialog.FinishPreviewAsync().GetAwaiter().GetResult();
                var preview=document.Preview(default);string first=document.Save(default),second=document.Save(default);var saved=File.ReadAllBytes(first);
                check(Path.GetDirectoryName(first)==folder&&first.EndsWith(" decoded.wav")&&second.EndsWith(" decoded 2.wav")&&saved.SequenceEqual(File.ReadAllBytes(second)),"G711exclusive copies beside original without overwriting");
                using(var source=new MemoryStream(original))using(var expected=new MemoryStream()){G711Wave.Export(source,expected,default);check(saved.SequenceEqual(expected.ToArray()),"NativeG711 export same core bytes");}
                using(var pcm=new MemoryStream(saved))using(var expected=new MemoryStream()){var info=PcmWave.Read(pcm,default);check(info.Bits==16&&info.Rate==8000&&info.Channels==channels&&info.Frames==80001,"G711output accepted by existing editor");PcmWave.Export(pcm,expected,new(0,info.Seconds),default,true);check(preview.SequenceEqual(expected.ToArray()),"NativeG711 preview exact saved prefix");}
                int count=Directory.GetFiles(folder).Length;reject(()=>document.Save(new CancellationToken(true)),"G711precancel Save");reject(()=>document.Preview(new CancellationToken(true)),"G711precancel preview");check(count==Directory.GetFiles(folder).Length&&!Directory.GetFiles(folder,".swivel-*.tmp").Any(),"G711precancel noownedstage");
            }
            check(File.ReadAllBytes(path).SequenceEqual(original),"G711source unchanged");
        }
        string bad=Path.Combine(folder,"bad.wav");var invalid=Fixture(false,1,1);invalid[20]=1;File.WriteAllBytes(bad,invalid);reject(()=>G711Document.Open(bad,default).Dispose(),"G711invalid input cleanup");File.Delete(bad);
        string big=Path.Combine(folder,"cancel.wav");File.WriteAllBytes(big,Fixture(true,1,32000000));
        using(var document=G711Document.Open(big,default))using(var token=new CancellationTokenSource())
        {
            var saving=Task.Run(()=>document.Save(token.Token));bool stage=SpinWait.SpinUntil(()=>Directory.GetFiles(folder,".swivel-*.tmp").Length>0||saving.IsCompleted,TimeSpan.FromSeconds(10))&&!saving.IsCompleted;token.Cancel();bool cancelled=false;try{saving.GetAwaiter().GetResult();}catch(OperationCanceledException){cancelled=true;}
            check(stage&&cancelled,"G711can cancel after stage creation");check(!Directory.GetFiles(folder,".swivel-*.tmp").Any()&&!Directory.GetFiles(folder,"cancel decoded*.wav").Any(),"G711cancel removes owned stage and publishes nothing");
        }
        Directory.Move(folder,folder+" released");check(Directory.Exists(folder+" released"),"G711leases released after success/cancel/error");
    }
}
