using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Swivel.Core;
using Swivel.Windows;

internal static class UsageNativeChecks
{
    private static void Pump(Task task)
    {
        var frame=new DispatcherFrame();var clock=Stopwatch.StartNew();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(5)};
        timer.Tick+=(_,_)=>{if(task.IsCompleted||clock.Elapsed>TimeSpan.FromSeconds(20))frame.Continue=false;};timer.Start();try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}if(!task.IsCompleted)throw new TimeoutException("Monitor fixture did not settle.");task.GetAwaiter().GetResult();
    }
    public static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        check(Marshal.SizeOf<UsageReader.NativeTime>()==8 && Marshal.OffsetOf<UsageReader.NativeTime>("High").ToInt32()==4,"FILETIME layout is two unsigned 32-bit fields");
        check(new UsageReader.NativeTime {High=0xfedcba98,Low=0x76543210}.Value==0xfedcba9876543210UL,"Native CPU times preserve unsigned high bits");
        check(Marshal.SizeOf<UsageReader.MemoryStatus>()==64 && Marshal.OffsetOf<UsageReader.MemoryStatus>("TotalPhysical").ToInt32()==8
            && Marshal.OffsetOf<UsageReader.MemoryStatus>("AvailablePhysical").ToInt32()==16,"MEMORYSTATUSEX has native 64-byte layout on x64/ARM64");
        var imports=typeof(UsageReader).GetMethods(BindingFlags.NonPublic|BindingFlags.Static).Where(x=>x.GetCustomAttribute<DllImportAttribute>() is not null).ToArray();
        check(imports.Length==3 && imports.All(x=>x.GetCustomAttribute<DefaultDllImportSearchPathsAttribute>()?.Paths==DllImportSearchPath.System32),"Every usage API resolves only in System32");
        reject(()=>UsageReader.Read(new CancellationToken(true)),"Usage pre-cancellation occurs before native APIs");
        int reads=0;var app=Application.Current as App??new App();app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
        var dialog=new UsageMonitorDialog(app,default,_=>{reads++;return new(new(0,1,1),new(10,5),CpuSampleStatus.Available);});
        // Construct only. Never Show/ShowDialog, raise a timer, press Copy, read
        // real machine counters or change settings in this control fixture.
        try
        {
            check(reads==0 && !dialog.Sampling && dialog.History.Points.Count==0,"Constructing an unshown monitor performs no sampling");
            dialog.PollAsync().GetAwaiter().GetResult();check(reads==0,"Polling a hidden monitor cannot start the injected reader");
            var root=(DockPanel)dialog.Content;var footer=root.Children.OfType<WrapPanel>().Single();
            check(DockPanel.GetDock(footer)==Dock.Bottom && footer.Children.OfType<Button>().Count()==4 && dialog.MinWidth==500 && dialog.MinHeight==440,"Native monitor retains fixed Pause/Copy/Explain/Close footer and scrollable content");
            check(!footer.Children.OfType<Button>().Single(x=>Equals(x.Content,app.L("Explain Redacted Snapshot…"))).IsEnabled,"Monitor explanation requires a recorded sample");
            check(!footer.Children.OfType<Button>().Single(x=>Equals(x.Content,app.L("Copy Current Snapshot"))).IsEnabled,"Snapshot copying requires an actual sample");
            check(dialog.CurrentSnapshot.Contains(app.L(UsageNotices.Scope)) && dialog.CurrentSnapshot.Contains(app.L(UsageNotices.Limits)) && dialog.CurrentSnapshot.Contains(app.L(UsageNotices.Meaning)),"Explicit snapshot text retains all scope and interpretation limits");
            dialog.StopAsync().GetAwaiter().GetResult();dialog.PollAsync().GetAwaiter().GetResult();
            check(reads==0 && !dialog.Sampling,"Stopped hidden monitor cannot restart and has no outstanding worker");
        }
        finally {dialog.StopAsync().GetAwaiter().GetResult();dialog.Close();}
        var prior=SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            foreach(bool failure in new[]{false,true})
            {
                using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
                var stale=new UsageMonitorDialog(app,default,_=>{entered.Set();if(!release.Wait(TimeSpan.FromSeconds(10)))throw new TimeoutException();if(failure)throw new InvalidOperationException("Expected fixture failure");return new(null,new(10,5),CpuSampleStatus.Unavailable);});
                try
                {
                    var fields=typeof(UsageMonitorDialog);fields.GetField("sampling",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(stale,true);
                    ((UsageSampler)fields.GetField("sampler",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(stale)!).SetEnabled(true);
                    var polling=stale.PollAsync();check(entered.Wait(TimeSpan.FromSeconds(10)),"Injected monitor read entered");
                    // Represent a pause/resume while the old read/continuation is
                    // pending. Do not Show the window or run its polling timer.
                    fields.GetField("samplingVersion",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(stale,2L);release.Set();Pump(polling);
                    check(stale.History.Points.Count==0,"A prior UI epoch cannot publish "+(failure?"failure":"success"));
                }
                finally{release.Set();try{Pump(stale.StopAsync());}catch(InvalidOperationException)when(failure){}stale.Close();}
            }
        }
        finally{SynchronizationContext.SetSynchronizationContext(prior);}
        var history=new UsageHistory();history.Add(new(new(0,1,1),new(100,100),CpuSampleStatus.Available),0);
        history.Add(new(new(0,2,2),new(100,0),CpuSampleStatus.Available),2);history.Break(3);
        history.Add(new(null,null,CpuSampleStatus.Unavailable),4);
        var chart=new UsageChart();chart.Update(history,4);chart.Measure(new(400,170));chart.Arrange(new Rect(0,0,400,170));chart.UpdateLayout();
        var bitmap=new RenderTargetBitmap(400,170,96,96,PixelFormats.Pbgra32);bitmap.Render(chart);
        check(bitmap.PixelWidth==400 && bitmap.PixelHeight==170,"Bounded native graph renders zero/full percentages and missing data without a window");
    }
}
