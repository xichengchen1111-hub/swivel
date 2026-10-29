using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Swivel.Core;
using Swivel.Windows;

internal static class WaveToolsChecks
{
    internal static void CheckNoiseInput(string path, Action<Stream,Stream> convert, int bits, Action<bool,string> check, Action<Action,string> reject)
    {
        byte[] original=File.ReadAllBytes(path);
        using var expected=new MemoryStream();
        using(var source=new MemoryStream(original)) convert(source,expected);
        var settings=new WaveNoise(0,.05,.75);
        using(var document=WaveDocument.OpenNoise(path,default))
        {
            check(document.Info.Bits==bits && !document.Decoded && document.Peaks.Length==600,"Local noise conversion exposes integer PCM without Windows-decoder notice");
            var app=System.Windows.Application.Current as App??new App();
            var dialog=new WaveNoiseDialog(app,document);
            var body=(System.Windows.Controls.StackPanel)((System.Windows.Controls.DockPanel)dialog.Content).Children.OfType<System.Windows.Controls.ScrollViewer>().Single().Content;
            check(body.Children.OfType<System.Windows.Controls.TextBlock>().Any(x=>x.Text==app.L(WaveNoiseInput.Hint)),"Noise dialog shows supported local inputs and conversion limits");
            dialog.Close();dialog.FinishPreviewAsync().GetAwaiter().GetResult();
            using var preview=new MemoryStream();WaveDenoiser.Export(expected,preview,settings,default,preview:true);
            check(document.NoisePreview(settings,default).SequenceEqual(preview.ToArray()),"Local noise preview matches existing converter then denoiser");
            using var full=new MemoryStream();WaveDenoiser.Export(expected,full,settings,default);
            string first=document.SaveNoise(settings,default),second=document.SaveNoise(settings,default);
            check(Path.GetDirectoryName(first)==Path.GetDirectoryName(path) && first.EndsWith(" clean.wav") && second.EndsWith(" clean 2.wav"),"Converted noise output exclusively published beside held original");
            check(File.ReadAllBytes(first).SequenceEqual(full.ToArray()) && File.ReadAllBytes(second).SequenceEqual(full.ToArray()),"Converted noise save keeps complete denoiser bytes and prior output");
            reject(()=>File.WriteAllBytes(path,new byte[1]),"Converted noise source stays pinned");
            string folder=Path.GetDirectoryName(path)!;reject(()=>Directory.Move(folder,folder+" moved"),"Converted noise ancestors stay pinned");
            int before=Directory.GetFiles(folder).Length;
            reject(()=>document.SaveNoise(settings,new CancellationToken(true)),"Converted noise cancellation publishes nothing");
            check(Directory.GetFiles(folder).Length==before && !Directory.GetFiles(folder,".swivel-*.tmp").Any(),"Converted noise cancellation leaves no staging");
        }
        check(File.ReadAllBytes(path).SequenceEqual(original),"Noise conversion keeps original bytes");
    }
    public static void Run(string directory,Action<bool,string> check,Action<Action,string> reject)
    {
        string folder=Path.Combine(directory,"wave"); Directory.CreateDirectory(folder); string source=Path.Combine(folder,"声音.wav");
        using(var stream=File.Create(source)) using(var writer=new BinaryWriter(stream,Encoding.ASCII,true))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36+16000); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(8000); writer.Write(16000); writer.Write((short)2); writer.Write((short)16); writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(16000); for(int i=0;i<8000;i++) writer.Write((short)(i%2==0?16000:-16000));
        }
        byte[] original=File.ReadAllBytes(source);
        using(var document=WaveDocument.Open(source,default))
        {
            check(document.Info.Seconds==1 && document.Peaks.Length==600,"WAV native source opened and envelope loaded");
            var previewApp=System.Windows.Application.Current as App??new App();var readOnly=new WavePreviewDialog(previewApp,document);
            var previewRoot=(System.Windows.Controls.DockPanel)readOnly.Content;var previewFooter=previewRoot.Children.OfType<System.Windows.Controls.StackPanel>().Single(x=>System.Windows.Controls.DockPanel.GetDock(x)==System.Windows.Controls.Dock.Bottom);
            var previewButtons=previewFooter.Children.OfType<System.Windows.Controls.WrapPanel>().Single().Children.OfType<System.Windows.Controls.Button>().ToArray();
            check(previewButtons.Length==3&&previewButtons.All(x=>(string)x.Content!=previewApp.L("Save WAV")),"Read-only WAV has only explicit Play/Stop/Close and no editing or saving");
            var previewBody=(System.Windows.Controls.StackPanel)previewRoot.Children.OfType<System.Windows.Controls.ScrollViewer>().Single().Content;
            check(previewBody.Children.OfType<System.Windows.Controls.TextBox>().All(x=>x.IsReadOnly),"WAV source text is read-only");
            readOnly.Close();readOnly.FinishPreviewAsync().GetAwaiter().GetResult();

            reject(()=>File.WriteAllBytes(source,new byte[1]),"Source lease prevents writes during edit");
            reject(()=>Directory.Move(folder,folder+" moved"),"WAV ancestors remain pinned");
            byte[] preview=document.Preview(new(.25,.75,.5),default); using(var p=new MemoryStream(preview)) check(PcmWave.Read(p,default).Frames==4000,"WAV bounded preview from selected range");
            string first=document.Save(new(.25,.75,.5),default),second=document.Save(new(.25,.75,.5),default);
            check(first.EndsWith("声音 edited.wav") && second.EndsWith("声音 edited 2.wav") && File.ReadAllBytes(first).SequenceEqual(File.ReadAllBytes(second)),"WAV exclusive naming and deterministic export");
            using(var saved=File.OpenRead(first)) check(PcmWave.Read(saved,default).Frames==4000,"WAV saved duration");
            int before=Directory.GetFiles(folder).Length;
            reject(()=>document.Save(new(0,1),new CancellationToken(true)),"WAV cancelled save");
            reject(()=>document.Save(new(1,0),default),"WAV invalid edit cannot create output");
            check(Directory.GetFiles(folder).Length==before && !Directory.GetFiles(folder,".swivel-*.tmp").Any(),"WAV failures create no partial or temp output");
        }
        using(var document = WaveDocument.Open(source, default))
        {
            var format = new WaveFormat(16000, 2, 8);
            var app = System.Windows.Application.Current as App ?? new App();
            var dialog = new WaveFormatDialog(app, document);
            check(dialog.Content is System.Windows.Controls.DockPanel && dialog.MinHeight == 400 && dialog.MinWidth == 500, "WAV conversion window has native scrollable form and fixed footer bounds");
            check(dialog.Format == new WaveFormat(8000,1,16), "Format dialog defaults preserve input format");
            dialog.Close(); dialog.FinishPreviewAsync().GetAwaiter().GetResult();
            byte[] preview = document.FormatPreview(format, default);
            string first = document.SaveFormat(format, default), second = document.SaveFormat(format, default);
            check(first.EndsWith("声音 converted.wav") && second.EndsWith("声音 converted 2.wav") && Path.GetDirectoryName(first) == folder, "WAV conversion exclusively publishes beside original");
            check(File.ReadAllBytes(first).SequenceEqual(File.ReadAllBytes(second)), "WAV conversion collision does not modify prior output");
            using(var saved = File.OpenRead(first)) using(var expected = new MemoryStream())
            {
                var info = PcmWave.Read(saved, default);
                check(info.Rate == 16000 && info.Channels == 2 && info.Bits == 8 && info.Frames == 16000, "Native WAV conversion chosen format");
                PcmWave.Export(saved, expected, new(0, info.Seconds), default, true);
                check(expected.ToArray().SequenceEqual(preview), "Native format preview includes target PCM8 quantization");
            }
            reject(()=>File.WriteAllBytes(source,new byte[1]), "Conversion keeps source pinned across preview and save");
            int before = Directory.GetFiles(folder).Length;
            reject(()=>document.SaveFormat(format,new CancellationToken(true)), "Pre-cancelled format conversion publishes nothing");
            reject(()=>document.SaveFormat(new(4000,2,16),default), "Invalid format rejected before staging");
            check(Directory.GetFiles(folder).Length == before && !Directory.GetFiles(folder,".swivel-*.tmp").Any(), "Conversion errors leave original and outputs intact");
        }
        using(var document = WaveDocument.OpenNoise(source, default))
        {
            var app = System.Windows.Application.Current as App ?? new App();
            var dialog = new WaveNoiseDialog(app, document);
            check(dialog.Content is System.Windows.Controls.DockPanel && dialog.MinHeight == 400 && dialog.MinWidth == 500, "Noise editor keeps a native scrollable form and fixed footer bounds");
            check(dialog.Settings == new WaveNoise(0,.25,.75), "Noise editor starts with a visible sample and 75 percent strength");
            var noiseRoot=(System.Windows.Controls.DockPanel)dialog.Content;
            var noisePanel=(System.Windows.Controls.StackPanel)noiseRoot.Children.OfType<System.Windows.Controls.ScrollViewer>().Single().Content;
            var quietButtons=noisePanel.Children.OfType<System.Windows.Controls.WrapPanel>().Single().Children.OfType<System.Windows.Controls.Button>().ToArray();
            check(quietButtons.Length==3&&quietButtons[0].IsEnabled&&!quietButtons[1].IsEnabled&&!quietButtons[2].IsEnabled,"Quiet search starts explicit with no candidate or automatic adoption/playback");
            var beforeSuggestion=dialog.Settings;int filesBeforeSuggestion=Directory.GetFiles(folder).Length;
            var suggested=document.FindQuietSample(2000,default);
            check(suggested.Count==2000&&new WaveNoise(suggested.Start,suggested.End).Range(document.Info).Count==2000&&dialog.Settings==beforeSuggestion,"Pinned quiet search returns exact manual range without changing editor");
            check(Directory.GetFiles(folder).Length==filesBeforeSuggestion,"Quiet search creates no output or temporary files");
            reject(()=>document.FindQuietSample(2000,new CancellationToken(true)),"Native quiet search observes pre-cancellation");

            dialog.Close(); dialog.FinishPreviewAsync().GetAwaiter().GetResult();
            var settings = new WaveNoise(0,.25,.75);
            byte[] preview = document.NoisePreview(settings, default);
            string first = document.SaveNoise(settings, default), second = document.SaveNoise(settings, default);
            check(first.EndsWith("声音 clean.wav") && second.EndsWith("声音 clean 2.wav") && Path.GetDirectoryName(first) == folder, "Denoise publishes exclusively beside original");
            check(File.ReadAllBytes(first).SequenceEqual(File.ReadAllBytes(second)), "Denoise collision keeps first output intact");
            using(var saved=File.OpenRead(first))
            {
                var info=PcmWave.Read(saved,default); check(info.Rate==8000 && info.Bits==16 && info.Channels==1 && info.Frames==8000, "Denoise preserves native WAV source format and duration");
            }
            check(File.ReadAllBytes(first).SequenceEqual(preview), "Short PCM16 denoise preview equals saved full output");
            using(var input=new MemoryStream(original)) using(var expected=new MemoryStream())
            {
                WaveDenoiser.Export(input,expected,settings,default);
                check(File.ReadAllBytes(first).SequenceEqual(expected.ToArray()),"Noise loading leaves existing integer WAV output bytes unchanged");
            }
            reject(()=>File.WriteAllBytes(source,new byte[1]), "Noise source stays pinned across profile/preview/save");
            reject(()=>Directory.Move(folder,folder+" moved"), "Noise source ancestors stay pinned");
            int before = Directory.GetFiles(folder).Length;
            reject(()=>document.SaveNoise(settings,new CancellationToken(true)), "Cancelled noise save publishes nothing");
            reject(()=>document.SaveNoise(new(0,.01),default), "Invalid noise sample refused before staging");
            check(Directory.GetFiles(folder).Length==before && !Directory.GetFiles(folder,".swivel-*.tmp").Any(), "Noise save failures leave no staging/output");
        }
        using(var document=WaveDocument.Open(source,default,scanPeaks:false))
        {
            check(document.Peaks.Length==0,"Speed loading skips unnecessary peak scan");
            var app=System.Windows.Application.Current as App??new App();var dialog=new WaveSpeedDialog(app,document);
            var root=(System.Windows.Controls.DockPanel)dialog.Content;
            check(dialog.Speed==new WaveSpeed(100,true)&&root.Children.OfType<System.Windows.Controls.StackPanel>().Any(x=>System.Windows.Controls.DockPanel.GetDock(x)==System.Windows.Controls.Dock.Bottom),"Speed defaults neutral with fixed native footer");
            var scroll=root.Children.OfType<System.Windows.Controls.ScrollViewer>().Single();var panel=(System.Windows.Controls.StackPanel)scroll.Content;
            var field=panel.Children.OfType<System.Windows.Controls.TextBox>().Single();
            check(field.Text=="100"&&System.Windows.Automation.AutomationProperties.GetName(field)==app.L("Playback Speed (%)"),"Speed field has explicit automation name and neutral value");
            var keep=panel.Children.OfType<System.Windows.Controls.CheckBox>().Single();
            check(keep.IsChecked==true&&System.Windows.Automation.AutomationProperties.GetName(keep)==app.L("Keep Pitch (time-stretch)"),"Keep Pitch is on by default with an explicit automation name");
            field.Text="201";check(dialog.Speed==new WaveSpeed(100,true),"Invalid editing cannot mutate committed speed");
            keep.IsChecked=false;check(dialog.Speed==new WaveSpeed(100,true),"Toggling Keep Pitch alone does not commit a speed");
            dialog.Close();dialog.FinishPreviewAsync().GetAwaiter().GetResult();
            var speed=new WaveSpeed(73);byte[] preview=document.SpeedPreview(speed,default);
            string first=document.SaveSpeed(speed,default),second=document.SaveSpeed(speed,default);
            check(first.EndsWith("声音 speed.wav")&&second.EndsWith("声音 speed 2.wav")&&Path.GetDirectoryName(first)==folder,"Speed exports exclusively beside original");
            check(File.ReadAllBytes(first).SequenceEqual(File.ReadAllBytes(second))&&preview.SequenceEqual(File.ReadAllBytes(first)),"Short PCM16 speed preview matches both deterministic collision outputs");
            using(var read=File.OpenRead(first))
            {var info=PcmWave.Read(read,default);check(info.Rate==8000&&info.Bits==16&&info.Channels==1&&info.Frames==(8000L*100+73/2)/73,"Speed output has rounded duration with original format");}
            var stretch=new WaveSpeed(37,true);byte[] stretchPreview=document.SpeedPreview(stretch,default);string third=document.SaveSpeed(stretch,default);
            check(third.EndsWith("声音 speed 3.wav")&&Path.GetDirectoryName(third)==folder&&stretchPreview.SequenceEqual(File.ReadAllBytes(third))&&!File.ReadAllBytes(third).SequenceEqual(File.ReadAllBytes(first)),"Keep-pitch preview equals its exclusive beside-original save");
            using(var read=File.OpenRead(third))
            {var info=PcmWave.Read(read,default);check(info.Rate==8000&&info.Bits==16&&info.Channels==1&&info.Frames==(8000L*100+37/2)/37,"Keep-pitch output has rounded duration with original format");}
            reject(()=>File.WriteAllBytes(source,new byte[1]),"Speed retains source lease through preview and save");
            reject(()=>Directory.Move(folder,folder+" moved"),"Speed retains ancestor lease");
            int count=Directory.GetFiles(folder).Length;
            reject(()=>document.SaveSpeed(new(49),default),"Invalid speed refused before staging");
            reject(()=>document.SaveSpeed(new(401,true),default),"Invalid keep-pitch speed refused before staging");
            reject(()=>document.SaveSpeed(speed,new CancellationToken(true)),"Pre-cancelled speed saves nothing");
            check(Directory.GetFiles(folder).Length==count&&!Directory.GetFiles(folder,".swivel-*.tmp").Any(),"Speed invalid/cancelled saves leave no stage/output");
        }
        check(File.ReadAllBytes(source).SequenceEqual(original),"WAV original bytes unchanged");
        string musicFolder = Path.Combine(folder, "music"); Directory.CreateDirectory(musicFolder);
        string musicPath = Path.Combine(musicFolder, "Background.wav"); File.WriteAllBytes(musicPath, original);
        using (var main = WaveDocument.Open(source, default)) using (var music = WaveDocument.Open(musicPath, default))
        {
            reject(() => File.WriteAllText(musicPath, "changed"), "Background music stays pinned through preview/export");
            reject(() => Directory.Move(musicFolder, musicFolder + " moved"), "Music source ancestors stay pinned");
            var edit = new AudioEdit(.25, .75); var mix = new WaveMusic(Gain: .15, Loop: true);
            byte[] preview = main.MixPreview(music, edit, mix, default);
            string mixed = main.SaveMix(music, edit, mix, default), again = main.SaveMix(music, edit, mix, default);
            check(Path.GetDirectoryName(mixed) == folder && mixed.EndsWith("声音 with music.wav") && again.EndsWith("声音 with music 2.wav"), "Music output beside main audio without replacing existing files");
            check(File.ReadAllBytes(mixed).SequenceEqual(preview) && File.ReadAllBytes(again).SequenceEqual(preview), "Short PCM16 mix preview equals saved output exactly");
            int count = Directory.GetFiles(folder).Length;
            reject(() => main.SaveMix(music, edit, mix, new CancellationToken(true)), "Cancelled music save publishes nothing");
            reject(() => main.SaveMix(music, edit, new WaveMusic(Start: 1), default), "Invalid music range refused before output creation");
            check(Directory.GetFiles(folder).Length == count && !Directory.GetFiles(folder, ".swivel-*.tmp").Any(), "Music save failure leaves no staging");
        }
        check(File.ReadAllBytes(source).SequenceEqual(original) && File.ReadAllBytes(musicPath).SequenceEqual(original), "Both WAV originals unchanged");
        string differentMusic=Path.Combine(musicFolder,"Different Rate.wav");byte[] differentBytes=(byte[])original.Clone();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(differentBytes.AsSpan(24),16000);System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(differentBytes.AsSpan(28),32000);File.WriteAllBytes(differentMusic,differentBytes);
        using(var main=WaveDocument.Open(source,default))using(var music=WaveDocument.Open(differentMusic,default))
        {
            var app=System.Windows.Application.Current as App??new App();var plan=WaveMusicRatePlan.Create(music.Info,main.Info.Rate);
            var confirmation=new MusicRateDialog(app,main,music,plan);check(music.PreparedMusicClippedSamples is null&&music.MusicInfo==music.Info&&confirmation.Content is System.Windows.Controls.DockPanel,"Rate confirmation itself never prepares or changes music");confirmation.Close();
            reject(()=>music.PrepareMusicRate(8000,new CancellationToken(true)),"Cancelled music preparation leaves no prepared state");check(music.PreparedMusicClippedSamples is null,"Failed prepare never publishes partial memory source");
            int count=Directory.GetFiles(folder).Length;music.PrepareMusicRate(8000,default);
            check(music.Info.Rate==16000&&music.Info.Frames==8000&&music.MusicInfo.Rate==8000&&music.MusicInfo.Frames==4000&&music.PreparedMusicClippedSamples is not null,"Original music Info is separate from effective prepared Info");
            check(Directory.GetFiles(folder).Length==count,"Preparing music never writes an intermediate file");
            reject(()=>music.PrepareMusicRate(8000,default),"No second preparation races or replaces an existing prepared source");
            var dialog=new WaveDialog(app,main,music);dialog.Close();dialog.FinishPreviewAsync().GetAwaiter().GetResult();
            var edit=new AudioEdit(0,1);var mix=new WaveMusic(Loop:true,Ducking:new(-35,.2),LoopCrossfadeMilliseconds:1);
            byte[] preview=main.MixPreview(music,edit,mix,default);string first=main.SaveMix(music,edit,mix,default),second=main.SaveMix(music,edit,mix,default);
            check(File.ReadAllBytes(first).SequenceEqual(preview)&&File.ReadAllBytes(second).SequenceEqual(preview),"Prepared rate preview/save/loop/activity lowering use one result and collision-safe publishing");
            reject(()=>File.WriteAllBytes(differentMusic,new byte[1]),"Original different-rate source remains pinned after memory preparation");
            check(File.ReadAllBytes(differentMusic).SequenceEqual(differentBytes)&&File.ReadAllBytes(source).SequenceEqual(original),"Both original source bytes stay unchanged after prepared mixing");
        }
        File.Move(musicPath, musicPath + ".released");
        check(File.Exists(musicPath + ".released"), "Music source lease released after editor/export lifetime");
        string large=Path.Combine(folder,"cancel-during-export.wav");byte[] header=original[..44];const int audioBytes=32*1024*1024;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4),36+audioBytes);System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40),audioBytes);
        using(var file=File.Create(large)){file.Write(header);file.SetLength(44+audioBytes);}
        using(var longDocument=WaveDocument.Open(large,default))using(var cancellation=new CancellationTokenSource())
        {
            var saving=Task.Run(()=>longDocument.Save(new(0,longDocument.Info.Seconds,2),cancellation.Token));
            bool sawStaging=SpinWait.SpinUntil(()=>Directory.GetFiles(folder,".swivel-*.tmp").Length>0 || saving.IsCompleted,TimeSpan.FromSeconds(5)) && !saving.IsCompleted;
            cancellation.Cancel();bool cancelled=false;
            try{saving.GetAwaiter().GetResult();}catch(OperationCanceledException){cancelled=true;}
            check(sawStaging && cancelled,"WAV cancellation arrives during export with owned staging");
            check(!Directory.GetFiles(folder,".swivel-*.tmp").Any() && !Directory.GetFiles(folder,"cancel-during-export edited*.wav").Any(),"Mid-export cancellation removes only its staging and publishes nothing");
        }
        using (var longDocument = WaveDocument.Open(large, default)) using (var music = WaveDocument.Open(source, default)) using (var cancellation = new CancellationTokenSource())
        {
            var saving = Task.Run(() => longDocument.SaveMix(music, new(0, longDocument.Info.Seconds), new(Loop: true), cancellation.Token));
            bool sawStaging = SpinWait.SpinUntil(() => Directory.GetFiles(folder, ".swivel-*.tmp").Length > 0 || saving.IsCompleted, TimeSpan.FromSeconds(5)) && !saving.IsCompleted;
            cancellation.Cancel(); bool cancelled = false;
            try { saving.GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancelled = true; }
            check(sawStaging && cancelled, "Mixed WAV cancellation arrives during export with both source leases held");
            check(!Directory.GetFiles(folder, ".swivel-*.tmp").Any() && !Directory.GetFiles(folder, "cancel-during-export with music*.wav").Any(), "Mixed WAV cancellation removes only owned staging");
        }
        using (var document = WaveDocument.Open(large, default)) using (var cancellation = new CancellationTokenSource())
        {
            var saving = Task.Run(()=>document.SaveFormat(new(11025,2,24),cancellation.Token));
            bool staging = SpinWait.SpinUntil(()=>Directory.GetFiles(folder,".swivel-*.tmp").Length > 0 || saving.IsCompleted,TimeSpan.FromSeconds(5)) && !saving.IsCompleted;
            cancellation.Cancel(); bool cancelled = false;
            try { saving.GetAwaiter().GetResult(); } catch(OperationCanceledException) { cancelled = true; }
            check(staging && cancelled, "Conversion can cancel while owned staging is present");
            check(!Directory.GetFiles(folder,".swivel-*.tmp").Any() && !Directory.GetFiles(folder,"cancel-during-export converted*.wav").Any(), "Mid-conversion cancellation removes owned staging and publishes nothing");
        }
        using(var document=WaveDocument.Open(large,default,scanPeaks:false))using(var cancellation=new CancellationTokenSource())
        {
            var saving=Task.Run(()=>document.SaveSpeed(new(73),cancellation.Token));
            bool staging=SpinWait.SpinUntil(()=>Directory.GetFiles(folder,".swivel-*.tmp").Length>0||saving.IsCompleted,TimeSpan.FromSeconds(5))&&!saving.IsCompleted;
            cancellation.Cancel();bool cancelled=false;try{saving.GetAwaiter().GetResult();}catch(OperationCanceledException){cancelled=true;}
            check(staging&&cancelled,"Speed can cancel during exclusive staging export");
            check(!Directory.GetFiles(folder,".swivel-*.tmp").Any()&&!Directory.GetFiles(folder,"cancel-during-export speed*.wav").Any(),"Mid-speed cancellation removes owned stage and publishes nothing");
        }
        using(var document=WaveDocument.Open(large,default,scanPeaks:false))using(var cancellation=new CancellationTokenSource())
        {
            var saving=Task.Run(()=>document.SaveSpeed(new(40,true),cancellation.Token));
            bool staging=SpinWait.SpinUntil(()=>Directory.GetFiles(folder,".swivel-*.tmp").Length>0||saving.IsCompleted,TimeSpan.FromSeconds(5))&&!saving.IsCompleted;
            cancellation.Cancel();bool cancelled=false;try{saving.GetAwaiter().GetResult();}catch(OperationCanceledException){cancelled=true;}
            check(staging&&cancelled,"Keep-pitch speed can cancel during exclusive staging export");
            check(!Directory.GetFiles(folder,".swivel-*.tmp").Any()&&!Directory.GetFiles(folder,"cancel-during-export speed*.wav").Any(),"Mid-keep-pitch cancellation removes owned stage and publishes nothing");
        }
        using(var document=WaveDocument.Open(large,default)) using(var cancellation=new CancellationTokenSource())
        {
            var saving=Task.Run(()=>document.SaveNoise(new(0,.25),cancellation.Token));
            bool staging=SpinWait.SpinUntil(()=>Directory.GetFiles(folder,".swivel-*.tmp").Length>0 || saving.IsCompleted,TimeSpan.FromSeconds(5)) && !saving.IsCompleted;
            cancellation.Cancel();bool cancelled=false;
            try{saving.GetAwaiter().GetResult();}catch(OperationCanceledException){cancelled=true;}
            check(staging && cancelled, "Denoise can cancel while owned staging is present");
            check(!Directory.GetFiles(folder,".swivel-*.tmp").Any() && !Directory.GetFiles(folder,"cancel-during-export clean*.wav").Any(), "Mid-denoise cancellation cleans owned staging without publishing");
        }
        string bad=Path.Combine(folder,"bad.wav"); File.WriteAllBytes(bad,new byte[100]); reject(()=>WaveDocument.Open(bad,default).Dispose(),"Bad WAV rejected before editor");
        // The failure released its file handle. Playback is a separate explicit manual check.
        File.Delete(bad); Directory.Move(folder,folder+" released"); check(Directory.Exists(folder+" released"),"WAV leases released after close/failure");
    }
}
