using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Swivel.Core;
using Swivel.Windows;

internal static class InstalledAppsNativeChecks
{
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        var imports=typeof(InstalledAppsReader).GetMethods(BindingFlags.Static|BindingFlags.NonPublic).Where(x=>x.GetCustomAttribute<DllImportAttribute>()is not null).ToArray();
        check(imports.Length==3&&imports.All(x=>x.GetCustomAttribute<DllImportAttribute>()!.Value=="advapi32.dll"&&x.GetCustomAttribute<DefaultDllImportSearchPathsAttribute>()?.Paths==DllImportSearchPath.System32),"All three registry metadata imports restrict DLL loading to System32");
        check(InstalledAppsReader.Query==1&&InstalledAppsReader.Enumerate==8&&InstalledAppsReader.View64==0x100&&InstalledAppsReader.View32==0x200,"Registry imports use fixed query/enumeration and exclusive WOW64 view bits");
        reject(()=>InstalledAppsReader.Read(new CancellationToken(true)),"Precancel installation reader performs no registry access");
        foreach(uint type in new uint[]{1,2})foreach(bool terminated in new[]{false,true})
        {
            byte[] bytes=Encoding.Unicode.GetBytes("literal %LOCALAPPDATA% 🐦"+(terminated?"\0":""));var value=InstalledAppsReader.Decode(type,bytes,(uint)bytes.Length);
            check(value.State==RegistrationValueState.Present&&value.Value=="literal %LOCALAPPDATA% 🐦","Both string types decode strict boundedUTF16 without variable expansion and optionalNUL");
        }
        byte[] max=Encoding.Unicode.GetBytes(new string('界',1024)+"\0");check(InstalledAppsReader.Decode(1,max,(uint)max.Length).Value==new string('界',1024),"Exact1024UTF16 payload plus optional terminator accepted");
        check(InstalledAppsReader.Decode(1,new byte[1],1).State==RegistrationValueState.Invalid&&InstalledAppsReader.Decode(1,new byte[]{0,0xd8},2).State==RegistrationValueState.Invalid,"Odd byte count and malformed surrogate rejected");
        foreach(string bad in new[]{"x\0y","x\0\0"}){byte[] data=Encoding.Unicode.GetBytes(bad);check(InstalledAppsReader.Decode(1,data,(uint)data.Length).State==RegistrationValueState.Invalid,"Embedded or extraNUL not silently truncated");}
        byte[] huge=Encoding.Unicode.GetBytes(new string('x',1025));check(InstalledAppsReader.Decode(1,huge,(uint)huge.Length).State==RegistrationValueState.TooLarge,"1025payloadunits refused even without terminator");
        check(InstalledAppsReader.Decode(7,new byte[4],4).State==RegistrationValueState.Unsupported&&InstalledAppsReader.Decode(1,new byte[2],4).State==RegistrationValueState.Invalid&&InstalledAppsReader.Decode(1,Array.Empty<byte>(),0).State==RegistrationValueState.Empty,"Type/returned length/empty states distinct");
        var snapshot=InstalledApps.Scan(new Source(),default);var app=Application.Current as App??new App();app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
        var dialog=new InstalledAppsDialog(app,snapshot);
        try
        {
            var nodes=Descendants(dialog).ToArray();var lists=nodes.OfType<ListView>().ToArray();var rows=lists.Single(x=>AutomationProperties.GetName(x)==app.L("Installation Records"));var keys=nodes.OfType<CheckBox>().Single();var detail=nodes.OfType<TextBox>().Single();
            check(lists.Length==2&&lists.All(x=>VirtualizingPanel.GetIsVirtualizing(x)&&VirtualizingPanel.GetVirtualizationMode(x)==VirtualizationMode.Recycling&&x.SelectionMode==SelectionMode.Single),"Record/root status lists use native virtualization and single selection");
            check(!dialog.IncludeKeys&&dialog.SelectedReport is null&&!dialog.CurrentReport.Contains("private-registry-owner"),"Raw subkeys hidden and no implicit selected-copy output");
            check(rows.Items.Cast<object>().All(x=>!x.ToString()!.Contains("private-registry-owner")),"Safe list rows do not expose raw key through UIAutomation/ToString");
            rows.SelectedIndex=0;check(!detail.Text.Contains("private-registry-owner")&&dialog.SelectedReport is string selected&&!selected.Contains("private-registry-owner")&&selected.Contains(app.L(InstalledApps.Limits)),"Selected copy/detail includes scope and same default privacy gate");
            keys.IsChecked=true;check(detail.Text.Contains("private-registry-owner")&&dialog.CurrentReport.Contains("private-registry-owner"),"Explicit checkbox includes key only in details/report");
            keys.IsChecked=false;check(!detail.Text.Contains("private-registry-owner")&&!dialog.CurrentReport.Contains("private-registry-owner"),"Hiding keys clears selected detail and report");
            nodes.OfType<TabControl>().Single().SelectedIndex=1;check(dialog.SelectedReport is null,"Selected record copy is disabled while only root-status tab is visible");
            nodes.OfType<TabControl>().Single().SelectedIndex=0;check(dialog.SelectedReport is not null,"Returning to record tab restores explicit selected-record copy");
            var footer=((DockPanel)dialog.Content).Children.OfType<WrapPanel>().Single();var buttons=footer.Children.OfType<Button>().Select(x=>x.Content as string).ToArray();
            check(DockPanel.GetDock(footer)==Dock.Bottom&&buttons.SequenceEqual(new[]{app.L("Open Installed Apps in Settings"),app.L("Find Leftovers of Apps Removed Since This List Was Read…"),app.L("Copy Report"),app.L("Copy Selected Details"),app.L("Close")})&&!dialog.LeftoversRequested,
                "Fixed footer offers Windows Settings for uninstalling, the leftover search, explicit copies and close; no uninstall command of its own and no leftover search until asked");
            check(Descendants(dialog).OfType<TextBlock>().Any(x=>x.Text==app.L(InstalledApps.Uninstall)),"The window says Windows does the uninstalling and how to find leftovers");
        }
        finally{dialog.Close();}
    }
    private sealed class Source:IInstalledAppsSource
    {public RegistrationOpen Open(RegistrationSource source)=>source==RegistrationSource.User64?new(new Root(),RegistrationOpenState.Open):new(null,RegistrationOpenState.Missing);}
    private sealed class Root:IRegistrationRoot
    {public RegistrationKey At(uint i)=>i==0?new(RegistrationKeyKind.Item,"private-registry-owner"):new(RegistrationKeyKind.End);public IRegistrationEntry Open(string key)=>new Entry();public void Dispose(){}}
    private sealed class Entry:IRegistrationEntry
    {public RegistrationText Read(RegistrationField field)=>new(field==RegistrationField.Name?"Sample app":field==RegistrationField.Version?"1":"Publisher",RegistrationValueState.Present);public void Dispose(){}}
    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {foreach(var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()){yield return child;foreach(var descendant in Descendants(child))yield return descendant;}}
}
