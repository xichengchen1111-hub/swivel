using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Swivel.Core;
using Swivel.Windows;

internal static class StartupNativeChecks
{
    private static IEnumerable<T> Find<T>(DependencyObject root)where T:DependencyObject
    {if(root is T t)yield return t;foreach(var c in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())foreach(var x in Find<T>(c))yield return x;}
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        var imports=typeof(StartupReader).GetMethods(BindingFlags.Static|BindingFlags.NonPublic).Where(m=>m.GetCustomAttribute<DllImportAttribute>()is not null).ToArray();
        check(imports.Length==2&&imports.All(m=>m.GetCustomAttribute<DllImportAttribute>()!.Value=="advapi32.dll"&&m.GetCustomAttribute<DefaultDllImportSearchPathsAttribute>()?.Paths==DllImportSearchPath.System32),"Startup exactly two System32-only registry imports");
        foreach(var source in Enum.GetValues<RegistrationSource>())foreach(bool once in new[]{false,true})
        {
            var request=StartupReader.Request(new(source,once));bool machine=source is RegistrationSource.Machine32 or RegistrationSource.Machine64;uint view=source is RegistrationSource.Machine64 or RegistrationSource.User64?0x100u:0x200u;
            check(request.Hive==new IntPtr(unchecked((int)(machine?0x80000002u:0x80000001u)))&&request.Access==(1|view),"Startup signed predefined handle and exclusive query/view flags");
            check(request.Key==@"Software\Microsoft\Windows\CurrentVersion\"+(once?"RunOnce":"Run"),"Startup only fixed local Run/RunOnce paths");
        }
        reject(()=>StartupReader.Request(new((RegistrationSource)999,false)),"Startup unknown view refused");reject(()=>StartupReader.Read(new CancellationToken(true)),"Startup precancel avoids actual registry access");
        foreach(uint type in new uint[]{1,2})foreach(bool nul in new[]{false,true})
        {
            string literal="\ufeff%TEMP%\\private 🦜 --key=sentinel";var bytes=Encoding.Unicode.GetBytes(literal+(nul?"\0":""));var text=StartupReader.Decode(type,bytes,(uint)bytes.Length);check(text.State==RegistrationValueState.Present&&text.Value==literal,"Startup strict Unicode retains BOM/environment text, optional terminator");
        }
        var max=Encoding.Unicode.GetBytes(new string('界',2048)+"\0");check(StartupReader.Decode(1,max,(uint)max.Length).Value==new string('界',2048),"Startup exactly2048units plus terminator");
        foreach(string invalid in new[]{"a\0b","a\0\0","\ud800",new string('x',2049)}){var bytes=new UnicodeEncoding(false,false,false).GetBytes(invalid);if(invalid=="\ud800")bytes=new byte[]{0,0xd8};check(StartupReader.Decode(1,bytes,(uint)bytes.Length).State is RegistrationValueState.Invalid or RegistrationValueState.TooLarge,"Startup malformed or oversize UTF16 whole refusal");}
        check(StartupReader.Decode(1,new byte[1],1).State==RegistrationValueState.Invalid&&StartupReader.Decode(1,new byte[2],100).State==RegistrationValueState.Invalid&&StartupReader.Decode(3,new byte[1],1).State==RegistrationValueState.Unsupported,"Startup odd/countoverflow/type handling");
        var location=new StartupLocation(RegistrationSource.User64,false);var row=new StartupRecord(location,0,"startup\u202e name",new("C:\\private-SENTINEL --key=SECRET %TEMP%",RegistrationValueState.Present),2,StartupIssue.None);
        var snapshot=new StartupSnapshot(DateTimeOffset.UtcNow,new[]{row},new[]{new StartupRootResult(location,1,StartupRootIssue.None)});var app=Application.Current as App??throw new InvalidOperationException("Use validation App.");
        var dialog=new StartupDialog(app,snapshot);check(!dialog.IncludeCommands&&!dialog.CurrentReport.Contains("SENTINEL")&&dialog.SelectedReport is null,"Startup native defaulthidden and no selected copy");
        var tabs=Find<TabControl>(dialog).Single();var lists=Find<ListView>(dialog).ToArray();var records=lists.First();records.SelectedIndex=0;
        check(dialog.SelectedReport is string hidden&&!hidden.Contains("SENTINEL")&&Find<TextBox>(dialog).All(b=>!b.Text.Contains("SENTINEL")),"Startup selected details and copy share hidden flag");
        check(records.Items.Cast<object>().All(i=>!i.ToString()!.Contains("SENTINEL")),"Startup list never stores command text");var toggle=Find<CheckBox>(dialog).Single();toggle.IsChecked=true;
        check(dialog.IncludeCommands&&dialog.CurrentReport.Contains("SENTINEL")&&dialog.SelectedReport!.Contains("%TEMP%")&&Find<TextBox>(dialog).Any(b=>b.Text.Contains("SENTINEL")),"Startup explicit privacy checkbox includes original command in details/copies");
        toggle.IsChecked=false;check(!dialog.CurrentReport.Contains("SENTINEL")&&!dialog.SelectedReport!.Contains("SENTINEL")&&Find<TextBox>(dialog).All(b=>!b.Text.Contains("SENTINEL")),"Startup re-hide clears command from details and reports");
        tabs.SelectedIndex=1;check(dialog.SelectedReport is null,"Startup root-status tab disables selected-entry copy");var root=(DockPanel)dialog.Content;check(root.Children.OfType<ScrollViewer>().Count()==1&&root.Children.OfType<WrapPanel>().Any(p=>DockPanel.GetDock(p)==Dock.Bottom),"Startup fixedfooter/scroll body");
        check(lists.All(l=>VirtualizingPanel.GetIsVirtualizing(l)),"Startup native result lists virtualized");dialog.Close();
        var empty=new StartupDialog(app,snapshot with{Records=Array.Empty<StartupRecord>(),Roots=new[]{new StartupRootResult(location,0,StartupRootIssue.Unavailable)}});check(empty.CurrentReport.Contains(app.L("Registry location or enumeration unavailable"))&&!empty.CurrentReport.Contains("SENTINEL"),"Startup zero result keeps root failures visible/private");empty.Close();
    }
}
