using System;
using System.Collections.Generic;
using System.Diagnostics;
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

internal static class AppMemoryHelperNativeChecks
{
    private sealed class Source : IAppMemorySource
    {
        public AppMemoryIds Enumerate()=>new(new uint[]{1,2,3});
        public IAppMemoryProcess Open(uint pid)=>new Item(pid);
        public AppMemoryParentIds ParentIds()=>new(new Dictionary<uint,uint>{[1]=0,[2]=1,[3]=1});
    }
    private sealed class Item(uint pid) : IAppMemoryProcess
    {
        public ulong? CreationTime()=>133_000_000_000_000_000UL+pid;
        public string ImagePath()=>pid==3?@"C:\Users\private-helper-owner\Other\tool.exe":@"C:\Users\private-helper-owner\App\"+(pid==1?"app.exe":@"sub\helper.exe");
        public ulong? WorkingSet()=>pid*1_000_000UL;
        public void Dispose(){}
    }
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        check(IntPtr.Size==8&&Marshal.SizeOf<ProcessParentReader.ProcessEntry>()==568&&Marshal.OffsetOf<ProcessParentReader.ProcessEntry>(nameof(ProcessParentReader.ProcessEntry.ProcessId)).ToInt32()==8
            &&Marshal.OffsetOf<ProcessParentReader.ProcessEntry>(nameof(ProcessParentReader.ProcessEntry.ParentProcessId)).ToInt32()==32&&Marshal.OffsetOf<ProcessParentReader.ProcessEntry>(nameof(ProcessParentReader.ProcessEntry.ExeFile)).ToInt32()==44,
            "PROCESSENTRY32W ABI is 568 bytes with the parent identifier at offset 32 on 64-bit Windows");
        var imports=typeof(ProcessParentReader).GetMethods(BindingFlags.Static|BindingFlags.NonPublic).Where(x=>x.GetCustomAttribute<DllImportAttribute>() is not null).ToArray();
        check(imports.Length==4&&imports.All(x=>x.GetCustomAttribute<DllImportAttribute>()!.Value=="kernel32.dll"&&x.GetCustomAttribute<DefaultDllImportSearchPathsAttribute>()?.Paths==DllImportSearchPath.System32)
            &&imports.Select(x=>x.Name).OrderBy(x=>x,StringComparer.Ordinal).SequenceEqual(new[]{"CloseHandle","CreateToolhelp32Snapshot","Process32FirstW","Process32NextW"}),"Parent reader uses only four System32 Toolhelp32 imports");

        var parents=ProcessParentReader.Read();uint self=(uint)Environment.ProcessId;
        check(!parents.Unavailable&&parents.Parents.Count is >1 and <=AppMemory.MaxProcesses&&parents.Parents.ContainsKey(self),"Toolhelp32 snapshot lists this test process");
        check(parents.ExcludedDirectories is {Count:1}&&string.Equals(parents.ExcludedDirectories[0],Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase),"Windows folder is excluded as a helper parent");

        string folder=Path.GetDirectoryName(Environment.ProcessPath!)!,helperFolder=Path.Combine(folder,"swivel-helper-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(helperFolder);string helperPath=Path.Combine(helperFolder,"helper-cmd.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory,"cmd.exe"),helperPath);
        Process? helper=null,outside=null;
        try
        {
            // cmd waits on its redirected input, so both children stay alive until killed.
            helper=Process.Start(new ProcessStartInfo(helperPath,"/d /q /k"){UseShellExecute=false,RedirectStandardInput=true,CreateNoWindow=true})!;
            outside=Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"cmd.exe"),"/d /q /k"){UseShellExecute=false,RedirectStandardInput=true,CreateNoWindow=true})!;
            Thread.Sleep(500);
            var snapshot=AppMemoryReader.Read(default);
            check(snapshot.HelperGroups is not null&&!snapshot.ParentIssues.HasFlag(AppMemoryParentIssue.NotRead)&&!snapshot.ParentIssues.HasFlag(AppMemoryParentIssue.Unavailable),"Live snapshot reads parent identifiers");
            var child=snapshot.Processes.SingleOrDefault(p=>p.Pid==(uint)helper.Id);var outsider=snapshot.Processes.SingleOrDefault(p=>p.Pid==(uint)outside.Id);
            check(child?.Parent==self&&outsider?.Parent==self,"Live children report this test process as parent");
            var group=snapshot.HelperGroups?.SingleOrDefault(g=>g.Processes.Any(p=>p.Pid==self));
            check(group is not null&&group.Processes.Any(p=>p.Pid==(uint)helper.Id)&&group.Helpers!.Contains(new AppMemoryHelper((uint)helper.Id,self)),"A child started from a subfolder of the test's folder joins the test's helper group");
            check(group is not null&&group.Processes.All(p=>p.Pid!=(uint)outside.Id),"A child in the Windows folder stays separate");
            check(snapshot.Groups.Single(g=>g.Processes.Any(p=>p.Pid==self)).Processes.All(p=>p.Pid!=(uint)helper.Id),"Exact-path groups still keep the helper separate");
        }
        finally
        {
            foreach(var process in new[]{helper,outside})if(process is not null){try{process.Kill();process.WaitForExit(5000);}catch(InvalidOperationException){}process.Dispose();}
            for(int attempt=0;attempt<10;attempt++){try{Directory.Delete(helperFolder,true);break;}catch(IOException){Thread.Sleep(200);}catch(UnauthorizedAccessException){Thread.Sleep(200);}}
        }

        var fake=AppMemory.Scan(new Source(),default);var app=Application.Current as App??new App();app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
        var dialog=new AppMemoryDialog(app,fake);
        try
        {
            var nodes=Descendants(dialog).ToArray();var groups=nodes.OfType<ListView>().First();
            var toggle=nodes.OfType<CheckBox>().Single(x=>AutomationProperties.GetName(x)==app.L("Group Helper Processes With the App That Started Them"));
            check(toggle.IsEnabled&&toggle.IsChecked!=true&&!dialog.GroupHelpers&&groups.Items.Count==3&&AutomationProperties.GetName(groups)==app.L("Executable Path Groups"),"Helper grouping is optional and off by default");
            groups.SelectedIndex=0;toggle.IsChecked=true;
            check(dialog.GroupHelpers&&groups.Items.Count==2&&groups.SelectedItem is null&&dialog.SelectedReport is null&&AutomationProperties.GetName(groups)==app.L("Helper Process Groups"),"Turning helper grouping on replaces rows and clears the old selection");
            check(dialog.CurrentReport.Contains(app.L(AppMemory.HelperGrouping))&&!dialog.CurrentReport.Contains("private-helper-owner"),"Helper report states its rules and keeps paths hidden");
            groups.SelectedIndex=0;check(dialog.SelectedReport is string selected&&selected.Contains(app.L("Attached helper processes")),"Selected helper group lists attached helpers");
            toggle.IsChecked=false;check(groups.Items.Count==3&&!dialog.CurrentReport.Contains(app.L(AppMemory.HelperGrouping)),"Turning it off restores exact-path groups");
        }
        finally{dialog.Close();}
        var plain=new AppMemoryDialog(app,new(default,Array.Empty<AppMemoryProcess>(),Array.Empty<AppMemoryGroup>(),false,false,false,false));
        try{check(!Descendants(plain).OfType<CheckBox>().Single(x=>AutomationProperties.GetName(x)==app.L("Group Helper Processes With the App That Started Them")).IsEnabled,"Helper option is disabled when parents were not read");}
        finally{plain.Close();}
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        foreach(var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
        {yield return child;foreach(var descendant in Descendants(child))yield return descendant;}
    }
}
