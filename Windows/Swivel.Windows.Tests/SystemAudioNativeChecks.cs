using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Swivel.Core;
using Swivel.Windows;

internal static class SystemAudioNativeChecks
{
    internal static void Run(string directory,Action<bool,string> check,Action<Action,string> reject)
    {
        string folder=Path.Combine(directory,"system-audio");Directory.CreateDirectory(folder);string path=Path.Combine(folder,"audio 🦜.wav");
        using(var file=File.Create(path))
        {
            using var writer=new BinaryWriter(file,System.Text.Encoding.UTF8,true);writer.Write("RIFF"u8);writer.Write(36+80001*4);writer.Write("WAVEfmt "u8);writer.Write(16);writer.Write((ushort)1);writer.Write((ushort)2);writer.Write(8000);writer.Write(32000);writer.Write((ushort)4);writer.Write((ushort)16);writer.Write("data"u8);writer.Write(80001*4);for(int i=0;i<80001*2;i++){short v=(short)(i*397);file.WriteByte((byte)v);file.WriteByte((byte)(v>>8));}
        }
        byte[] original=File.ReadAllBytes(path);
        using(var document=Task.Run(()=>SystemAudioDocument.OpenAsync(path,default)).GetAwaiter().GetResult())
        {
            check(document.Info.Rate==8000&&document.Info.Channels==2&&document.Info.Bits==16&&document.Info.Frames==80001,"Windows decoder requested source timebase and PCM16");
            reject(()=>File.WriteAllBytes(path,new byte[1]),"System decoder source stays held after adapters close");reject(()=>Directory.Move(folder,folder+" moved"),"System decoder ancestors held");
            var dialog=new SystemAudioDialog((App)Application.Current,document);var root=(DockPanel)dialog.Content;check(root.Children.OfType<ScrollViewer>().Count()==1&&root.Children.OfType<StackPanel>().Any(p=>DockPanel.GetDock(p)==Dock.Bottom),"Native audio decoded facts use fixedfooter/scroll");dialog.Close();dialog.FinishPreviewAsync().GetAwaiter().GetResult();
            var preview=document.Preview(default);var first=document.Save(default);var second=document.Save(default);check(first.EndsWith(" audio PCM.wav")&&second.EndsWith(" audio PCM 2.wav")&&File.ReadAllBytes(first).SequenceEqual(File.ReadAllBytes(second)),"Decoded output exclusive deterministic beside source");
            using(var decoded=File.OpenRead(first))using(var expected=new MemoryStream()){var info=PcmWave.Read(decoded,default);PcmWave.Export(decoded,expected,new(0,info.Seconds),default,true);check(preview.SequenceEqual(expected.ToArray()),"Native codec preview and save share identical prepared audio");}
            using(var source=new MemoryStream(original))using(var expected=new MemoryStream()){var info=PcmWave.Read(source,default);PcmWave.Export(source,expected,new(0,info.Seconds),default);check(File.ReadAllBytes(first).SequenceEqual(expected.ToArray()),"PCM16 input roundtrip does not change decoded samples");}
            int count=Directory.GetFiles(folder).Length;reject(()=>document.Save(new CancellationToken(true)),"Precancel decoder save");check(count==Directory.GetFiles(folder).Length&&!Directory.GetFiles(folder,".swivel-*.tmp").Any(),"Precancel decoder save has no stage");
        }
        check(File.ReadAllBytes(path).SequenceEqual(original),"System decoder source intact");
        // Reduce Audio Noise on a file Windows decodes first: the same denoised bytes as from the plain PCM16 WAV, saved beside the original.
        using(var decoded=Task.Run(()=>WaveDocument.OpenDecodedAsync(path,default)).GetAwaiter().GetResult())
        using(var plain=WaveDocument.Open(path,default))
        {
            var settings=new WaveNoise(0,.25);
            check(decoded.Decoded&&!plain.Decoded&&decoded.Info==plain.Info&&decoded.Peaks.SequenceEqual(plain.Peaks),"A decoded document has the same PCM facts and waveform as the plain WAV");
            reject(()=>File.WriteAllBytes(path,new byte[1]),"The decoded document keeps the original held");
            check(decoded.NoisePreview(settings,default).SequenceEqual(plain.NoisePreview(settings,default)),"Noise preview from decoded audio matches the plain WAV");
            string cleaned=decoded.SaveNoise(settings,default),reference=plain.SaveNoise(settings,default);
            check(cleaned.EndsWith(" clean.wav")&&reference.EndsWith(" clean 2.wav")&&File.ReadAllBytes(cleaned).SequenceEqual(File.ReadAllBytes(reference)),"Decoded noise reduction saves beside the original with the plain WAV's exact result");
        }
        check(File.ReadAllBytes(path).SequenceEqual(original),"Noise reduction leaves the original intact");
        string unsupported=Path.Combine(folder,"reference.mp3");File.WriteAllText(unsupported,"http://example.invalid/audio");reject(()=>Task.Run(()=>SystemAudioDocument.OpenAsync(unsupported,default)).GetAwaiter().GetResult().Dispose(),"URI/playlist masquerade rejected before native codec");File.Delete(unsupported);
        reject(()=>Task.Run(()=>SystemAudioDocument.OpenAsync(path,new CancellationToken(true))).GetAwaiter().GetResult().Dispose(),"Precancel decoder open");
        if(!NativeIdentity.HasPackage)
        {
            reject(()=>TextCaptureDocument.Open(path,default).Dispose(),"Portable OCR identity gate");reject(()=>Task.Run(()=>PdfRasterDocument.OpenAsync(new[]{path},default)).GetAwaiter().GetResult().Dispose(),"Portable PDF identity gate");
        }
        Directory.Move(folder,folder+" released");check(Directory.Exists(folder+" released"),"Decoder leases released");
    }
}
