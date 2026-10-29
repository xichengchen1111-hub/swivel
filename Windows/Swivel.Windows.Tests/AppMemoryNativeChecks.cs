using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Swivel.Core;
using Swivel.Windows;

internal static class AppMemoryNativeChecks
{
    private sealed class Source : IAppMemorySource
    {
        public AppMemoryIds Enumerate()=>new(new uint[]{1,2,3});
        public IAppMemoryProcess Open(uint pid)=>new Item(pid);
    }
    private sealed class Item(uint pid) : IAppMemoryProcess
    {
        public ulong? CreationTime()=>133_000_000_000_000_000UL+pid;
        public string ImagePath()=>@"C:\Users\private-memory-owner\app"+pid+@"\tool.exe";
        public ulong? WorkingSet()=>pid==3?null:(ulong)pid*1_000_000;
        public void Dispose(){}
    }
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        check(IntPtr.Size==8&&Marshal.SizeOf<AppMemoryReader.MemoryCounters>()==72&&Marshal.OffsetOf<AppMemoryReader.MemoryCounters>(nameof(AppMemoryReader.MemoryCounters.WorkingSetSize)).ToInt32()==16&&Marshal.OffsetOf<AppMemoryReader.MemoryCounters>(nameof(AppMemoryReader.MemoryCounters.PeakPagefileUsage)).ToInt32()==64,"App memory counter ABI is72bytes with native SIZE_T offsets on supported64bit architectures");
        var imports=typeof(AppMemoryReader).GetMethods(BindingFlags.Static|BindingFlags.NonPublic).Where(x=>x.GetCustomAttribute<DllImportAttribute>() is not null).ToArray();
        check(imports.Length==7&&imports.All(x=>x.GetCustomAttribute<DllImportAttribute>()!.Value=="kernel32.dll"&&x.GetCustomAttribute<DefaultDllImportSearchPathsAttribute>()?.Paths==DllImportSearchPath.System32),"All seven process metadata imports restrict native loading to System32");
        reject(()=>AppMemoryReader.Read(new CancellationToken(true)),"Process reader precancel avoids all native queries");
        var snapshot=AppMemory.Scan(new Source(),default);var app=Application.Current as App??new App();app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
        var dialog=new AppMemoryDialog(app,snapshot);
        try
        {
            var nodes=Descendants(dialog).ToArray();var lists=nodes.OfType<ListView>().ToArray();
            var groups=lists.Single(x=>AutomationProperties.GetName(x)==app.L("Executable Path Groups"));var processes=lists.Single(x=>AutomationProperties.GetName(x)==app.L("Recorded Processes"));
            var paths=nodes.OfType<CheckBox>().Single(x=>AutomationProperties.GetName(x)==app.L("Show Full Executable Paths"));var details=nodes.OfType<TextBox>().Where(x=>AutomationProperties.GetName(x)==app.L("Selected Group Details")||AutomationProperties.GetName(x)==app.L("Selected Process Details")).ToArray();
            check(!dialog.IncludePaths&&paths.IsChecked!=true&&!dialog.CurrentReport.Contains("private-memory-owner"),"App memory full paths default hidden in all copies");
            check(lists.Length==2&&lists.All(x=>VirtualizingPanel.GetIsVirtualizing(x)&&VirtualizingPanel.GetVirtualizationMode(x)==VirtualizationMode.Recycling&&x.SelectionMode==SelectionMode.Single),"Both native process lists remain virtualized and single-select");
            check(groups.Items.Cast<object>().Concat(processes.Items.Cast<object>()).All(x=>!x.ToString()!.Contains("private-memory-owner")),"UIAutomation default row text never exposes a raw record path");
            check(dialog.SelectedReport is null,"No implicit selection or selected-copy output");
            groups.SelectedIndex=0;processes.SelectedIndex=0;
            check(details.All(x=>!x.Text.Contains("private-memory-owner"))&&dialog.SelectedReport is string selected&&!selected.Contains("private-memory-owner")&&selected.Contains(app.L(AppMemory.Measurement)),"Selected details/copies share privacy gate and complete measurement scope");
            paths.IsChecked=true;check(details.Length==2&&details.All(x=>x.Text.Contains("private-memory-owner"))&&dialog.CurrentReport.Contains("private-memory-owner"),"Explicit path checkbox updates both tabs and full copy");
            paths.IsChecked=false;check(details.All(x=>!x.Text.Contains("private-memory-owner"))&&!dialog.CurrentReport.Contains("private-memory-owner"),"Hiding paths also clears inactive-tab details");
            nodes.OfType<TabControl>().Single().SelectedIndex=1;
            check(dialog.SelectedReport is string processReport&&processReport.Contains(string.Format(app.L("Process identifier: {0}"),1)),"Selected copy follows active tab and exact selected process");
            var footer=((DockPanel)dialog.Content).Children.OfType<WrapPanel>().Single();
            check(DockPanel.GetDock(footer)==Dock.Bottom&&footer.Children.OfType<Button>().Count()==4,"App memory native footer provides explicit CopyReport/CopySelected/Quit/Close");
        }
        finally{dialog.Close();}
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        foreach(var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
        {yield return child;foreach(var descendant in Descendants(child))yield return descendant;}
    }
}
