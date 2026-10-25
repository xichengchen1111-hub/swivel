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

internal static class AiffNativeChecks
{
    private static void Fixture(string path, int bits, int channels, int frames, string? codec = null)
    {
        using var output=File.Create(path);
        void U16(int value){Span<byte>b=stackalloc byte[2];BinaryPrimitives.WriteUInt16BigEndian(b,(ushort)value);output.Write(b);}
        void U32(uint value){Span<byte>b=stackalloc byte[4];BinaryPrimitives.WriteUInt32BigEndian(b,value);output.Write(b);}
        int payload=frames*channels*(bits/8),size=54+payload+(payload&1)+(codec is null?0:18);
        output.Write("FORM"u8);U32((uint)size-8);output.Write(codec is null?"AIFF"u8:"AIFC"u8);
        if(codec is not null){output.Write("FVER"u8);U32(4);U32(0xa2805140);}
        output.Write("COMM"u8);U32(codec is null?18u:24u);U16(channels);U32((uint)frames);U16(bits);output.Write(Convert.FromHexString("400BFA00000000000000"));
        if(codec is not null){output.Write(System.Text.Encoding.ASCII.GetBytes(codec));output.Write(new byte[2]);}
        output.Write("SSND"u8);U32((uint)payload+8);U32(0);U32(0);
        for(int i=0;i<frames*channels;i++){int value=i%3==0?-1:i%3==1?0:1;for(int b=0;b<bits/8;b++){int index=codec=="sowt"?b:bits/8-1-b;output.WriteByte((byte)(value>>(index*8)));}}
        if((payload&1)!=0)output.WriteByte(0);
    }
    internal static void Run(string directory, Action<bool,string> check, Action<Action,string> reject)
    {
        string folder=Path.Combine(directory,"aiff");Directory.CreateDirectory(folder);var app=Application.Current as App??new App();
        foreach(int bits in new[]{8,16,24,32})foreach(string? codec in new string?[]{null,"NONE","sowt",bits==8?"NONE":bits==16?"twos":bits==24?"in24":"in32"}.Distinct())
        {
            string path=Path.Combine(folder,bits+"-"+(codec??"AIFF")+".aiff");Fixture(path,bits,2,8001,codec);byte[] original=File.ReadAllBytes(path);
            using(var document=AiffDocument.Open(path,default))
            {
                check(document.Info.Bits==bits&&document.Info.Channels==2&&document.Info.Frames==8001&&document.Info.Rate==8000,"Native AIFF parser retains integer format facts");
                reject(()=>File.WriteAllBytes(path,new byte[1]),"AIFF source remains pinned");reject(()=>Directory.Move(folder,folder+" moved"),"AIFF ancestors remain pinned");
                var dialog=new AiffDialog(app,document);var root=(DockPanel)dialog.Content;
                check(root.Children.OfType<ScrollViewer>().Count()==1&&root.Children.OfType<StackPanel>().Any(p=>DockPanel.GetDock(p)==Dock.Bottom)&&dialog.MinHeight==400&&dialog.MinWidth==500,"AIFF dialog uses scrollable native body and fixed footer");dialog.Close();dialog.FinishPreviewAsync().GetAwaiter().GetResult();
                byte[] preview=document.Preview(default);string first=document.Save(default),second=document.Save(default);
                check(Path.GetDirectoryName(first)==folder&&first.EndsWith(" PCM.wav")&&second.EndsWith(" PCM 2.wav"),"AIFF converts beside original with nonoverwriting collision names");check(File.ReadAllBytes(first).SequenceEqual(File.ReadAllBytes(second)),"AIFF deterministic collision output leaves first intact");
                using(var input=File.OpenRead(first))using(var expected=new MemoryStream())
                {var info=PcmWave.Read(input,default);check(info.Bits==bits&&info.Rate==8000&&info.Channels==2&&info.Frames==8001,"Native AIFF WAV accepted by existing integer tools");PcmWave.Export(input,expected,new(0,info.Seconds),default,true);check(expected.ToArray().SequenceEqual(preview),"Native AIFF preview matches saved WAV quantized toPCM16");}
                int count=Directory.GetFiles(folder).Length;reject(()=>document.Save(new CancellationToken(true)),"Precancelled AIFF export refused");check(Directory.GetFiles(folder).Length==count&&!Directory.GetFiles(folder,".swivel-*.tmp").Any(),"Precancelled AIFF creates no stage/output");
            }
            check(File.ReadAllBytes(path).SequenceEqual(original),"AIFF original unchanged");
        }
        string bad=Path.Combine(folder,"float.aifc");Fixture(bad,32,1,1,"fl32");reject(()=>AiffDocument.Open(bad,default).Dispose(),"Unsupported AIFC refuses without silently interpreting integers");File.Delete(bad);
        string large=Path.Combine(folder,"cancel.aiff");Fixture(large,32,2,4000000);
        using(var document=AiffDocument.Open(large,default))using(var token=new CancellationTokenSource())
        {
            var saving=Task.Run(()=>document.Save(token.Token));bool stage=SpinWait.SpinUntil(()=>Directory.GetFiles(folder,".swivel-*.tmp").Length>0||saving.IsCompleted,TimeSpan.FromSeconds(10))&&!saving.IsCompleted;
            token.Cancel();bool cancelled=false;try{saving.GetAwaiter().GetResult();}catch(OperationCanceledException){cancelled=true;}
            check(stage&&cancelled,"AIFF can cancel after staging starts");check(!Directory.GetFiles(folder,".swivel-*.tmp").Any()&&!Directory.GetFiles(folder,"cancel PCM*.wav").Any(),"Cancelled AIFF removes owned stage and publishes nothing");
        }
        Directory.Move(folder,folder+" released");check(Directory.Exists(folder+" released"),"AIFF leases released after success/error/cancellation");
    }
}
