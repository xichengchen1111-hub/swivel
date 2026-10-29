using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Swivel.Core;
using Swivel.Windows;

internal static class FloatWaveNativeChecks
{
    private static void Fixture(string path, int bits, int channels, int frames, bool clipping = false, bool nonfinite = false)
    {
        using var stream = File.Create(path); using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(36 + frames * channels * bits / 8); writer.Write("WAVEfmt "u8); writer.Write(16);
        writer.Write((ushort)3); writer.Write((ushort)channels); writer.Write(8000); writer.Write(8000 * channels * bits / 8);
        writer.Write((ushort)(channels * bits / 8)); writer.Write((ushort)bits); writer.Write("data"u8); writer.Write(frames * channels * bits / 8);
        for (int i = 0; i < frames * channels; i++)
        {double value = nonfinite && i == frames * channels - 1 ? double.NaN : clipping && i == 0 ? 2 : Math.Sin(i / 19d) * .8;if(bits == 32)writer.Write((float)value);else writer.Write(value);}
    }
    internal static void Run(string directory, Action<bool,string> check, Action<Action,string> reject)
    {
        string folder=Path.Combine(directory,"float-wav");Directory.CreateDirectory(folder);
        var app=Application.Current as App??new App();
        foreach(int bits in new[]{32,64})
        {
            string path=Path.Combine(folder,"float"+bits+".wav");Fixture(path,bits,2,8000);
            byte[] original=File.ReadAllBytes(path);
            using(var document=FloatWaveDocument.Open(path,default))
            {
                check(document.Info.Bits==bits&&document.Info.Frames==8000&&document.Stats.Samples==16000&&document.Stats.OutsideUnit==0,"Native float document scans complete interleaved source");
                reject(()=>File.WriteAllBytes(path,new byte[1]),"Float source remains pinned");reject(()=>Directory.Move(folder,folder+" moved"),"Float ancestors remain pinned");
                var dialog=new FloatWaveDialog(app,document);var root=(DockPanel)dialog.Content;
                var body=(StackPanel)root.Children.OfType<ScrollViewer>().Single().Content;var depth=body.Children.OfType<ComboBox>().Single();var clipping=body.Children.OfType<CheckBox>().Single();
                check((int)depth.SelectedItem==24&&clipping.IsChecked==false&&!clipping.IsEnabled&&dialog.Options==new FloatPcmOptions(),"Float dialog starts24-bit without clipping and no preference mutation");
                check(root.Children.OfType<StackPanel>().Any(x=>DockPanel.GetDock(x)==Dock.Bottom)&&dialog.MinHeight==400&&dialog.MinWidth==500,"Float dialog has fixed footer and scrollable body");
                check(System.Windows.Automation.AutomationProperties.GetName(depth)==app.L("Integer PCM Bit Depth"),"Float native depth has accessible name");
                dialog.Close();dialog.FinishPreviewAsync().GetAwaiter().GetResult();
                var options=new FloatPcmOptions();byte[] preview=document.Preview(options,default);string first=document.Save(options,default),second=document.Save(options,default);
                check(Path.GetDirectoryName(first)==folder&&first.EndsWith(" integer.wav")&&second.EndsWith(" integer 2.wav"),"Float outputs exclusively beside original without replacing collision");
                check(File.ReadAllBytes(first).SequenceEqual(File.ReadAllBytes(second)),"Float collisions preserve deterministic output");
                using(var input=File.OpenRead(first))using(var expected=new MemoryStream())
                {var info=PcmWave.Read(input,default);check(info.Bits==24&&info.Rate==8000&&info.Channels==2&&info.Frames==8000,"Native float output is accepted by existing integer editor");PcmWave.Export(input,expected,new(0,1),default,true);check(expected.ToArray().SequenceEqual(preview),"Native float preview matches saved target quantization");}
                int before=Directory.GetFiles(folder).Length;reject(()=>document.Save(new(8),default),"Invalid float depth before stage");reject(()=>document.Save(options,new CancellationToken(true)),"Precancelled float export");
                check(Directory.GetFiles(folder).Length==before&&!Directory.GetFiles(folder,".swivel-*.tmp").Any(),"Float invalid/cancelled export creates no stage/output");
            }
            WaveToolsChecks.CheckNoiseInput(path,(s,o)=>FloatWave.Export(s,o,new(),default),24,check,reject);
            check(File.ReadAllBytes(path).SequenceEqual(original),"Float original unchanged after session");
        }
        string over=Path.Combine(folder,"over.wav");Fixture(over,64,1,8000,true);
        reject(()=>WaveDocument.OpenNoise(over,default).Dispose(),"Noise rejects float clipping without an implicit permission");
        using(var document=FloatWaveDocument.Open(over,default))
        {
            var dialog=new FloatWaveDialog(app,document);var root=(DockPanel)dialog.Content;var body=(StackPanel)root.Children.OfType<ScrollViewer>().Single().Content;var clipping=body.Children.OfType<CheckBox>().Single();
            check(clipping.IsChecked==false&&clipping.IsEnabled&&document.Stats.OutsideUnit==1,"Over-range file requires initially unchecked explicit clipping choice");
            int before=Directory.GetFiles(folder).Length;reject(()=>document.Save(new(),default),"Native unapproved clipping refused");reject(()=>document.Preview(new(),default),"Native unapproved preview clipping refused");check(Directory.GetFiles(folder).Length==before,"Clipping refusal publishes nothing");
            dialog.Close();dialog.FinishPreviewAsync().GetAwaiter().GetResult();using var saved=File.OpenRead(document.Save(new(16,true),default));check(PcmWave.Read(saved,default).Frames==8000,"Approved clipping produces complete integer result");
        }
        string bad=Path.Combine(folder,"nonfinite.wav");Fixture(bad,32,1,80001,nonfinite:true);reject(()=>FloatWaveDocument.Open(bad,default).Dispose(),"Native full scan rejects NaN after preview prefix before dialog");reject(()=>WaveDocument.OpenNoise(bad,default).Dispose(),"Noise full scan rejects nonfinite samples beyond preview");File.Delete(bad);
        string large=Path.Combine(folder,"cancel.wav");Fixture(large,64,2,2000000);
        using(var document=FloatWaveDocument.Open(large,default))using(var cancellation=new CancellationTokenSource())
        {
            var pending=Task.Run(()=>document.Save(new(32),cancellation.Token));
            bool stage=SpinWait.SpinUntil(()=>Directory.GetFiles(folder,".swivel-*.tmp").Length>0||pending.IsCompleted,TimeSpan.FromSeconds(10))&&!pending.IsCompleted;
            cancellation.Cancel();bool cancelled=false;try{pending.GetAwaiter().GetResult();}catch(OperationCanceledException){cancelled=true;}
            check(stage&&cancelled,"Float conversion observes cancellation after exclusive staging starts");
            check(!Directory.GetFiles(folder,".swivel-*.tmp").Any()&&!Directory.GetFiles(folder,"cancel integer*.wav").Any(),"Cancelled float export removes only owned staging and publishes nothing");
        }
        Directory.Move(folder,folder+" released");check(Directory.Exists(folder+" released"),"Float leases release on success/refusal/cancellation");
    }
}
