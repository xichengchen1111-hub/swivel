using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Swivel.Core;
using Swivel.Windows;

internal static class StartupApprovalNativeChecks
{
    private static System.Collections.Generic.IEnumerable<T> Find<T>(DependencyObject root)where T:DependencyObject
    {if(root is T t)yield return t;foreach(var c in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())foreach(var x in Find<T>(c))yield return x;}
    internal static void Run(System.Action<bool,string> check,System.Action<System.Action,string> reject)
    {
        var imports=typeof(StartupApprovalReader).GetMethods(BindingFlags.Static|BindingFlags.NonPublic).Where(m=>m.GetCustomAttribute<DllImportAttribute>()is not null).ToArray();
        check(imports.Length==2&&imports.All(m=>m.GetCustomAttribute<DllImportAttribute>()!.Value=="advapi32.dll"&&m.GetCustomAttribute<DefaultDllImportSearchPathsAttribute>()?.Paths==DllImportSearchPath.System32),"StartupApproved uses only two System32 registry calls");
        foreach(var itemLocation in StartupApprovalSnapshotReader.Locations())
        {
            var request=StartupApprovalReader.Request(itemLocation);bool machine=itemLocation.Source is RegistrationSource.Machine32 or RegistrationSource.Machine64;uint view=itemLocation.Source is RegistrationSource.Machine64 or RegistrationSource.User64?0x100u:0x200u;
            check(request.Hive==new System.IntPtr(unchecked((int)(machine?0x80000002u:0x80000001u)))&&request.Access==(1|view)&&request.Key==StartupApprovalReader.BaseKey+"\\"+StartupApprovalSnapshotReader.AreaName(itemLocation.Area),"StartupApproved opens only one fixed local key with query/view access");
        }
        reject(()=>StartupApprovalReader.Request(new((RegistrationSource)999,StartupApprovalArea.Run)),"StartupApproved unknown source rejected");reject(()=>StartupApprovalReader.Request(new(RegistrationSource.User64,(StartupApprovalArea)999)),"StartupApproved unknown area rejected");reject(()=>StartupApprovalReader.Read(new CancellationToken(true)),"StartupApproved pre-cancel prevents registry access");
        var app=Application.Current as App??throw new System.InvalidOperationException("Use validation App.");var location=new StartupApprovalLocation(RegistrationSource.User64,StartupApprovalArea.Run);var row=new StartupApprovalRecord(location,0,"Run‮Sentinel",1,"010203",StartupApprovalIssue.None);var snapshot=new StartupApprovalSnapshot(System.DateTimeOffset.UtcNow,new[]{row},new[]{new StartupApprovalRootResult(location,1,StartupApprovalRootIssue.None)});
        var dialog=new StartupApprovalDialog(app,snapshot);check(dialog.CurrentReport.Contains("010203")&&dialog.CurrentReport.Contains("not interpreted"),"StartupApproved explicit report includes raw data and semantic caveat");var list=Find<ListView>(dialog).Last();list.SelectedIndex=0;check(Find<TextBox>(dialog).Any(x=>x.Text.Contains("[U+202E]")),"StartupApproved visible value names escape bidi controls");check(Find<ListView>(dialog).All(x=>VirtualizingPanel.GetIsVirtualizing(x)),"StartupApproved native lists use virtualization");dialog.Close();
    }
}
