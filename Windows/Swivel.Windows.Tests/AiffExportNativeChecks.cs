using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Swivel.Core;
using Swivel.Windows;

internal static class AiffExportNativeChecks
{
    private static void Fixture(string path,int bits,int channels,int frames)
    {
        int size=frames*channels*(bits/8);using var stream=File.Create(path);using var writer=new BinaryWriter(stream);
        writer.Write("RIFF"u8);writer.Write(36+size+(size&1));writer.Write("WAVEfmt "u8);writer.Write(16);writer.Write((ushort)1);writer.Write((ushort)channels);writer.Write(8000);writer.Write(8000*channels*(bits/8));writer.Write((ushort)(channels*(bits/8)));writer.Write((ushort)bits);writer.Write("data"u8);writer.Write(size);
        for(int i=0;i<frames*channels;i++){int value=i%3-1;if(bits==8)writer.Write((byte)(value+128));else for(int b=0;b<bits/8;b++)writer.Write((byte)(value>>(b*8)));}if((size&1)!=0)writer.Write((byte)0);
    }
    internal static void Run(string directory,Action<bool,string> check,Action<Action,string> reject)
    {
        string folder=Path.Combine(directory,"aiff-export");Directory.CreateDirectory(folder);var app=Application.Current as App??new App();
        foreach(int bits in new[]{8,16,24,32})foreach(int channels in new[]{1,2})
        {
            string path=Path.Combine(folder,bits+"-"+channels+".wav");Fixture(path,bits,channels,8001);byte[] original=File.ReadAllBytes(path);
            using(var document=WavAiffDocument.Open(path,default))
            {
                check(document.Info.Bits==bits&&document.Info.Channels==channels&&document.Info.Frames==8001,"NativeAIFF export validates integer WAV facts");
                reject(()=>File.WriteAllBytes(path,new byte[1]),"WAV source pinned throughoutAIFF export");reject(()=>Directory.Move(folder,folder+" moved"),"WAV ancestors pinned throughoutAIFF export");
                var dialog=new WavAiffDialog(app,document);var root=(DockPanel)dialog.Content;check(root.Children.OfType<ScrollViewer>().Count()==1&&root.Children.OfType<StackPanel>().Any(p=>DockPanel.GetDock(p)==Dock.Bottom)&&dialog.MinHeight==400&&dialog.MinWidth==500,"AIFF export uses native scrollable body and fixed footer");dialog.Close();dialog.FinishPreviewAsync().GetAwaiter().GetResult();
                byte[] preview=document.Preview(default);string first=document.Save(default),second=document.Save(default);
                check(Path.GetDirectoryName(first)==folder&&first.EndsWith(" AIFF.aiff")&&second.EndsWith(" AIFF 2.aiff"),"AIFF output exclusively beside source with nonoverwriting ordinal");check(File.ReadAllBytes(first).SequenceEqual(File.ReadAllBytes(second)),"AIFF collisions leave earlier output intact");
                using(var input=File.OpenRead(first))using(var expected=new MemoryStream())
                {var info=AiffWave.Read(input,default);check(info.Bits==bits&&info.Channels==channels&&info.Rate==8000&&info.Frames==8001&&!info.CompressedForm,"Nativeexport plainAIFF carries original integer format");AiffWave.Export(input,expected,default,true);check(expected.ToArray().SequenceEqual(preview),"NativeAIFF exportpreview equals savedAIFF decodedPCM16");}
                using(var result=AiffDocument.Open(first,default))check(result.Info.Bits==bits,"AIFF result feeds existing reverse converter with held own lease");
                int count=Directory.GetFiles(folder).Length;reject(()=>document.Save(new CancellationToken(true)),"NativeAIFF export precancelled");check(Directory.GetFiles(folder).Length==count&&!Directory.GetFiles(folder,".swivel-*.tmp").Any(),"CancelledAIFF createsno output or stage");
            }
            check(File.ReadAllBytes(path).SequenceEqual(original),"AIFF export retains original WAV bytes");
        }
        string invalid=Path.Combine(folder,"bad.wav");File.WriteAllBytes(invalid,new byte[100]);reject(()=>WavAiffDocument.Open(invalid,default).Dispose(),"Bad WAV refused beforeAIFF window");File.Delete(invalid);
        string large=Path.Combine(folder,"cancel.wav");Fixture(large,32,2,4000000);
        using(var document=WavAiffDocument.Open(large,default))using(var cancellation=new CancellationTokenSource())
        {
            var saving=Task.Run(()=>document.Save(cancellation.Token));bool stage=SpinWait.SpinUntil(()=>Directory.GetFiles(folder,".swivel-*.tmp").Length>0||saving.IsCompleted,TimeSpan.FromSeconds(10))&&!saving.IsCompleted;
            cancellation.Cancel();bool cancelled=false;try{saving.GetAwaiter().GetResult();}catch(OperationCanceledException){cancelled=true;}
            check(stage&&cancelled,"AIFF export cancellation observed during exclusive staging");check(!Directory.GetFiles(folder,".swivel-*.tmp").Any()&&!Directory.GetFiles(folder,"cancel AIFF*.aiff").Any(),"AIFF export cancellation removes onlyowned stage without publication");
        }
        Directory.Move(folder,folder+" released");check(Directory.Exists(folder+" released"),"AIFF export leases release aftersuccess/failure/cancel");
    }
}
