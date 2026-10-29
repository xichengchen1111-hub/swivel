using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Swivel.Core;
using Swivel.Windows;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;

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
        // Real M4A files written by the Windows AAC and ALAC encoders pass the box gate and decode on the same path.
        foreach(var (name,profile) in new[]{("aac",MediaEncodingProfile.CreateM4a(AudioEncodingQuality.Medium)),("alac",MediaEncodingProfile.CreateAlac(AudioEncodingQuality.Medium))})
        {
            byte[] m4a=Task.Run(()=>Encode(path,profile)).GetAwaiter().GetResult();string encoded=Path.Combine(folder,"memo "+name+".m4a");File.WriteAllBytes(encoded,m4a);
            M4aInfo facts;using(var stream=new MemoryStream(m4a,false))facts=M4aAudio.Inspect(stream,default);
            Console.WriteLine($"Windows {name} M4A: {m4a.Length} bytes, {facts.Codec} {facts.Rate} Hz {facts.Channels} ch {facts.Seconds:0.000} s");
            check(facts.Codec==(name=="aac"?"AAC-LC":"ALAC")&&Math.Abs(facts.Seconds-80001/8000.0)<.25,"Windows-encoded "+name+" M4A passes the box gate with its codec and duration");
            using(var document=Task.Run(()=>SystemAudioDocument.OpenAsync(encoded,default)).GetAwaiter().GetResult())
            {
                Console.WriteLine($"Decoded {name} M4A: {document.Info.Rate} Hz {document.Info.Channels} ch {document.Info.Frames} frames");
                check(document.Info.Rate==facts.Rate&&document.Info.Channels==facts.Channels&&document.Info.Bits==16&&Math.Abs(document.Info.Seconds-facts.Seconds)<.25,"Decoded "+name+" M4A keeps the declared rate, channels and duration as PCM16");
                reject(()=>File.WriteAllBytes(encoded,new byte[1]),"M4A source held while decoded");
                string output=document.Save(default);long frames;using(var saved=File.OpenRead(output))frames=PcmWave.Read(saved,default).Frames;check(output.EndsWith("memo "+name+" audio PCM.wav")&&frames==document.Info.Frames,"Decoded "+name+" M4A saved beside the original");
            }
            using(var noise=Task.Run(()=>WaveDocument.OpenDecodedAsync(encoded,default)).GetAwaiter().GetResult())check(noise.Decoded&&noise.Info.Rate==facts.Rate,"Reduce Audio Noise opens a decoded "+name+" M4A");
            using(var speech=Task.Run(()=>TranscribeDocument.OpenAsync(encoded,default)).GetAwaiter().GetResult())check(Math.Abs(speech.Seconds-facts.Seconds)<.25,"Transcribe Speech prepares a decoded "+name+" M4A");
            check(File.ReadAllBytes(encoded).SequenceEqual(m4a),"M4A original intact after decoding");
            var qt=m4a.ToArray();"qt  "u8.CopyTo(qt.AsSpan(8));string renamed=Path.Combine(folder,"memo "+name+" qt.m4a");File.WriteAllBytes(renamed,qt);
            reject(()=>Task.Run(()=>SystemAudioDocument.OpenAsync(renamed,default)).GetAwaiter().GetResult().Dispose(),"QuickTime-branded "+name+" file refused before the native codec");File.Delete(renamed);
        }
        string unsupported=Path.Combine(folder,"reference.mp3");File.WriteAllText(unsupported,"http://example.invalid/audio");reject(()=>Task.Run(()=>SystemAudioDocument.OpenAsync(unsupported,default)).GetAwaiter().GetResult().Dispose(),"URI/playlist masquerade rejected before native codec");File.Delete(unsupported);
        reject(()=>Task.Run(()=>SystemAudioDocument.OpenAsync(path,new CancellationToken(true))).GetAwaiter().GetResult().Dispose(),"Precancel decoder open");
        if(!NativeIdentity.HasPackage)
        {
            reject(()=>TextCaptureDocument.Open(path,default).Dispose(),"Portable OCR identity gate");reject(()=>Task.Run(()=>PdfRasterDocument.OpenAsync(new[]{path},default)).GetAwaiter().GetResult().Dispose(),"Portable PDF identity gate");
        }
        Directory.Move(folder,folder+" released");check(Directory.Exists(folder+" released"),"Decoder leases released");
    }
    private static async Task<byte[]> Encode(string wav,MediaEncodingProfile profile)
    {
        using var source=File.OpenRead(wav);using var input=source.AsRandomAccessStream();using var target=new MemoryStream();using var output=target.AsRandomAccessStream();
        var preparation=await new MediaTranscoder{HardwareAccelerationEnabled=false}.PrepareStreamTranscodeAsync(input,output,profile);
        if(!preparation.CanTranscode)throw new InvalidOperationException("Windows cannot encode this M4A profile: "+preparation.FailureReason);
        await preparation.TranscodeAsync();await output.FlushAsync();return target.ToArray();
    }
}
