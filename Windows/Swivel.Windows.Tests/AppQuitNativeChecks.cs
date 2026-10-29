using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Swivel.Core;
using Swivel.Windows;

internal static class AppQuitNativeChecks
{
    private const string Launch="--swivel-quit-launch",AppMode="--swivel-quit-app",WindowMode="--swivel-quit-window";
    /// Child modes of the test executable: a launcher that starts the app and exits (so the app
    /// is not attached to the test process), an app that starts a helper, and the window itself.
    internal static int? Fixture(string[] args)
    {
        if(args.Length==0)return null;
        switch(args[0])
        {
            case Launch when args.Length==4:
                using(Process.Start(new ProcessStartInfo(args[1]){UseShellExecute=false,ArgumentList={AppMode,args[2],args[3]}})){}
                return 0;
            case AppMode when args.Length==3:
                using(Process.Start(new ProcessStartInfo(args[1]){UseShellExecute=false,ArgumentList={WindowMode,"helper",args[2]}})){}
                return ShowWindow("app",args[2]);
            case WindowMode when args.Length==3:
                return ShowWindow(args[1],args[2]);
            default:return null;
        }
    }
    private static int ShowWindow(string name,string markers)
    {
        var application=new Application{ShutdownMode=ShutdownMode.OnMainWindowClose};
        var window=new Window{Title="Swivel quit fixture "+name,Width=320,Height=200,ShowActivated=false,Content=new TextBlock{Text="Swivel quit fixture"}};
        window.Loaded+=(_,_)=>File.WriteAllText(Path.Combine(markers,name+".ready"),Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        // Written only when the window handled its normal close.
        window.Closed+=(_,_)=>File.WriteAllText(Path.Combine(markers,name+".closed"),"closed");
        application.Run(window);return 0;
    }
    private sealed class Source(Func<uint,ulong> cpu) : IAppMemorySource
    {
        public AppMemoryIds Enumerate()=>new(new uint[]{1,2,3,(uint)Environment.ProcessId});
        public IAppMemoryProcess Open(uint pid)=>new Item(pid,cpu(pid));
    }
    private sealed class Item(uint pid,ulong cpu) : IAppMemoryProcess
    {
        public ulong? CreationTime()=>133_000_000_000_000_000UL+pid;
        public string ImagePath()=>pid switch{1=>@"C:\Apps\One\one.exe",2=>@"C:\Windows\System32\two.exe",3=>@"C:\Apps\Three\explorer.exe",_=>@"C:\Apps\Swivel\self.exe"};
        public ulong? WorkingSet()=>pid*1_000_000UL;
        public ulong? CpuTime()=>cpu;
        public uint? Session()=>1;
        public void Dispose(){}
    }
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        var imports=typeof(AppQuitReader).GetMethods(BindingFlags.Static|BindingFlags.NonPublic).Where(x=>x.GetCustomAttribute<DllImportAttribute>() is not null).ToArray();
        check(imports.Length==4&&imports.All(x=>x.GetCustomAttribute<DllImportAttribute>()!.Value=="kernel32.dll"&&x.GetCustomAttribute<DefaultDllImportSearchPathsAttribute>()?.Paths==DllImportSearchPath.System32)
            &&!imports.Any(x=>x.Name.Contains("Terminate",StringComparison.Ordinal)),"Quit exit check uses four System32 reads and no termination import");
        Dialog(check);
        Live(check);
    }
    private static void Dialog(Action<bool,string> check)
    {
        var app=Application.Current as App??throw new InvalidOperationException("An isolated validation App is required.");
        var first=AppMemory.Scan(new Source(pid=>0),default);
        var dialog=new AppMemoryDialog(app,first,default,_=>first,1,@"C:\Windows");
        try
        {
            var expected=first.Groups.Select(g=>AppQuit.Eligibility(g,(uint)Environment.ProcessId,1,@"C:\Windows")==AppQuitBlock.None).ToArray();
            check(dialog.OfferedRows.SequenceEqual(expected)&&expected.Count(x=>x)==1,"Only the ordinary app is offered; the Windows folder, shell names and Swivel itself are not");
            check(!dialog.Live&&!dialog.CpuColumnShown&&!dialog.QuitEnabled&&dialog.Ticked==0,"Live refresh, CPU column, ticks and quit are all off by default");
            int offered=Array.IndexOf(expected,true),blocked=Array.IndexOf(expected,false);
            check(!dialog.Tick(blocked,true)&&!dialog.QuitEnabled,"A row that is not offered cannot be ticked");
            check(dialog.Tick(offered,true)&&dialog.QuitEnabled&&dialog.Ticked==1,"Ticking an offered app only enables the explicit quit button");
            check(!dialog.Tick(offered,false)&&!dialog.QuitEnabled,"Unticking disables quit again");
            var live=Descendants(dialog).OfType<CheckBox>().Single(x=>AutomationProperties.GetName(x)==app.L("Refresh Every 2 Seconds"));
            live.IsChecked=true;check(dialog.CpuColumnShown,"Live refresh adds the CPU column");
            dialog.Apply(first,TimeSpan.FromSeconds(1));
            check(dialog.CpuTexts.All(x=>x==app.L("Unknown")),"The first live reading is only the CPU baseline");
            var second=AppMemory.Scan(new Source(pid=>pid==1?20_000_000UL:0),default);
            dialog.Apply(second,TimeSpan.FromSeconds(3));
            string hundred=100.0.ToString("N1",CultureInfo.CurrentCulture),zero=0.0.ToString("N1",CultureInfo.CurrentCulture);
            var one=second.Groups.Select((g,i)=>(g,i)).Single(x=>x.g.Processes[0].Pid==1).i;
            check(dialog.CpuTexts[one]==hundred&&dialog.CpuTexts.Where((_,i)=>i!=one).All(x=>x==zero),"CPU per group is the time delta over the elapsed read interval");
            dialog.Tick(offered,true);live.IsChecked=false;
            check(!dialog.CpuColumnShown&&dialog.Ticked==0,"Turning live refresh off removes the CPU column and rebuilds rows without ticks");
            check(!dialog.CurrentReport.Contains(hundred+"%",StringComparison.Ordinal),"Reports are unchanged by the live options");
        }
        finally{dialog.Close();dialog.FinishAsync().GetAwaiter().GetResult();}
    }
    private static void Live(Action<bool,string> check)
    {
        string folder=Path.GetDirectoryName(Environment.ProcessPath!)!,id=Guid.NewGuid().ToString("N");
        string appExe=Path.Combine(folder,"swivel-quit-app-"+id+".exe"),helperExe=Path.Combine(folder,"swivel-quit-helper-"+id+".exe");
        string markers=Path.Combine(Path.GetTempPath(),"swivel-quit-"+id);Directory.CreateDirectory(markers);
        File.Copy(Environment.ProcessPath!,appExe);File.Copy(Environment.ProcessPath!,helperExe);
        Process? appProcess=null,helperProcess=null;
        try
        {
            using(var launcher=Process.Start(new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,ArgumentList={Launch,appExe,helperExe,markers}})!)
                check(launcher.WaitForExit(60_000)&&launcher.ExitCode==0,"Fixture launcher started the app and exited");
            uint appPid=Ready(markers,"app"),helperPid=Ready(markers,"helper");
            check(appPid!=0&&helperPid!=0,"Fixture app and helper windows are shown");
            if(appPid==0||helperPid==0)return;
            appProcess=Process.GetProcessById((int)appPid);helperProcess=Process.GetProcessById((int)helperPid);_=appProcess.Handle;_=helperProcess.Handle;
            Thread.Sleep(500);
            using(var self=Process.GetCurrentProcess())Console.WriteLine("AppQuit native: session "+self.SessionId.ToString(CultureInfo.InvariantCulture));

            var snapshot=AppMemoryReader.Read(default);
            var group=snapshot.HelperGroups?.SingleOrDefault(g=>g.Processes.Any(p=>p.Pid==appPid));
            check(group is not null&&group.Processes[0].Pid==appPid&&group.Helpers!.Contains(new AppMemoryHelper(helperPid,appPid)),"The fixture helper is grouped with its app");
            check(group is not null&&group.Processes.All(p=>p.Session==AppQuitReader.Session&&p.Cpu.HasValue),"Live snapshot records each fixture's session and CPU time");
            if(group is null)return;
            check(AppQuit.Eligibility(group,(uint)Environment.ProcessId,AppQuitReader.Session,AppQuitReader.WindowsFolder)==AppQuitBlock.None,"The fixture app is offered for quitting");
            var self2=snapshot.Groups.Single(g=>g.Processes.Any(p=>p.Pid==(uint)Environment.ProcessId));
            check(AppQuit.Eligibility(self2,(uint)Environment.ProcessId,AppQuitReader.Session,AppQuitReader.WindowsFolder)==AppQuitBlock.Self,"The running test (in place of Swivel) is never offered");

            // A recorded identity that no longer matches (as after PID reuse) is skipped: nothing is sent.
            var app=group.Processes[0];var replaced=new AppMemoryGroup(app.Path,new[]{app with{Created=app.Created+1}},0,0);
            var probe=new AppQuitReader.Probe();
            using(var windows=AppQuitReader.ReadWindowsAsync(default).GetAwaiter().GetResult())
            {
                Console.WriteLine("AppQuit native: eligible windows "+windows.Windows.Count.ToString(CultureInfo.InvariantCulture)+", fixture windows "+windows.Windows.Count(w=>w.ProcessId==appPid||w.ProcessId==helperPid).ToString(CultureInfo.InvariantCulture));
                var stale=AppQuitReader.Plan(new[]{replaced},windows,default);
                check(stale.Windows.Count==0&&stale.Apps.Single().Replaced>=1&&stale.Apps.Single().WithoutWindows.Single().Pid==appPid,"A process whose identifier was replaced has its windows skipped");
                var none=AppQuitReader.RunAsync(windows,stale,probe,default).GetAwaiter().GetResult();
                check(none.Results.Count==0&&none.States.Single()==AppQuitState.Exited&&!appProcess.HasExited&&!File.Exists(Path.Combine(markers,"app.closed")),"Nothing is sent for a replaced identity; the probe treats the recorded one as gone and the real app keeps running");
            }
            check(probe.State(new(appPid,app.Created!.Value,app.Path!,false))==AppQuitState.Running,"The exit check sees the running app with its recorded identity");

            using(var windows=AppQuitReader.ReadWindowsAsync(default).GetAwaiter().GetResult())
            {
                var plan=AppQuitReader.Plan(new[]{group},windows,default);
                check(plan.Windows.Count==2&&plan.Windows.Select(i=>windows.Windows[i].ProcessId).SequenceEqual(new[]{appPid,helperPid}),"The quit plan asks the app's window, then its helper's");
                string details=AppQuit.Details(plan,windows.Windows,s=>s);
                check(details.Contains("Swivel quit fixture app",StringComparison.Ordinal)&&details.Contains("Swivel quit fixture helper",StringComparison.Ordinal),"The confirmation lists both windows");
                var outcome=AppQuitReader.RunAsync(windows,plan,probe,default).GetAwaiter().GetResult();
                check(outcome.Results.All(r=>r==CloseWindowsResult.Processed),"Both normal close requests were processed");
                check(outcome.States.All(x=>x==AppQuitState.Exited),"Both processes are reported as exited after the wait");
            }
            check(appProcess.WaitForExit(10_000)&&helperProcess.WaitForExit(10_000)&&appProcess.ExitCode==0&&helperProcess.ExitCode==0,"App and helper exited normally with code 0");
            check(File.Exists(Path.Combine(markers,"app.closed"))&&File.Exists(Path.Combine(markers,"helper.closed")),"Both windows handled their normal close");
        }
        finally
        {
            // Test cleanup only: the product code never ends a process.
            foreach(var process in new[]{appProcess,helperProcess})if(process is not null){try{if(!process.HasExited){process.Kill();process.WaitForExit(5000);}}catch(InvalidOperationException){}process.Dispose();}
            foreach(string path in new[]{appExe,helperExe})for(int attempt=0;attempt<20;attempt++){try{File.Delete(path);break;}catch(Exception e)when(e is IOException or UnauthorizedAccessException){Thread.Sleep(250);}}
            try{Directory.Delete(markers,true);}catch(Exception e)when(e is IOException or UnauthorizedAccessException){}
        }
    }
    private static uint Ready(string markers,string name)
    {
        string path=Path.Combine(markers,name+".ready");
        for(int i=0;i<240;i++)
        {
            try{if(File.Exists(path)&&uint.TryParse(File.ReadAllText(path),NumberStyles.None,CultureInfo.InvariantCulture,out uint pid))return pid;}
            catch(IOException){}
            Thread.Sleep(250);
        }
        return 0;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        foreach(var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
        {yield return child;foreach(var descendant in Descendants(child))yield return descendant;}
    }
}
