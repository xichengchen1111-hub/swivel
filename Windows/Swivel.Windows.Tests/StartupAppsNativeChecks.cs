using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Swivel.Core;
using Swivel.Windows;

internal static class StartupAppsNativeChecks
{
    private static IEnumerable<T> Find<T>(DependencyObject root)where T:DependencyObject
    {if(root is T t)yield return t;foreach(var c in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())foreach(var x in Find<T>(c))yield return x;}
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        var app=Application.Current as App??throw new InvalidOperationException("Use validation App.");
        // Real HKCU round trip: one Run value and its StartupApproved twin under a unique name, removed in finally.
        string name="SwivelStartupAppsCheck-"+Guid.NewGuid().ToString("N");
        const string runKey=@"Software\Microsoft\Windows\CurrentVersion\Run";string approvedKey=StartupApprovalReader.Key(StartupApprovalArea.Run);
        var when=new DateTime(2026,10,1,12,0,0,DateTimeKind.Utc);var data=new byte[12];data[0]=3;BitConverter.GetBytes(when.ToFileTimeUtc()).CopyTo(data,4);
        bool created=false;
        try
        {
            using(var run=Registry.CurrentUser.CreateSubKey(runKey))run.SetValue(name,@"C:\SwivelNoSuchFolder\does-not-exist.exe",RegistryValueKind.String);
            using(var approved=Registry.CurrentUser.CreateSubKey(approvedKey))approved.SetValue(name,data,RegistryValueKind.Binary);
            created=true;
            var result=StartupApps.Combine(StartupReader.Read(default),StartupApprovalReader.Read(default),null,app.L,default);
            var rows=result.Rows.Where(r=>r.Name==name).ToArray();
            check(rows.Length==1&&rows[0].Kind==StartupAppKind.Registry,"Native HKCU Run test value is listed once across both registry views");
            check(rows.Length==1&&rows[0].Meaning.State==StartupAppState.Disabled&&rows[0].Meaning.DisabledUtc==new DateTimeOffset(when),"Native StartupApproved test value decodes as disabled with its exact FILETIME");
            check(!StartupApps.Report(result,rows.FirstOrDefault(),app.L,default).Contains("does-not-exist.exe"),"Native report row omits the Run command");
            data[0]=2;Array.Clear(data,4,8);using(var approved=Registry.CurrentUser.OpenSubKey(approvedKey,true)!)approved.SetValue(name,data,RegistryValueKind.Binary);
            var enabled=StartupApps.Combine(StartupReader.Read(default),StartupApprovalReader.Read(default),null,app.L,default).Rows.Where(r=>r.Name==name).ToArray();
            check(enabled.Length==1&&enabled[0].Meaning.State==StartupAppState.Enabled,"Native StartupApproved 02 form decodes as enabled");
            using(var approved=Registry.CurrentUser.OpenSubKey(approvedKey,true)!)approved.DeleteValue(name,false);
            var none=StartupApps.Combine(StartupReader.Read(default),StartupApprovalReader.Read(default),null,app.L,default).Rows.Where(r=>r.Name==name).ToArray();
            check(none.Length==1&&none[0].Meaning.State is StartupAppState.NoEntry or StartupAppState.Unknown,"Native Run value without StartupApproved is not reported as enabled or disabled");
        }
        finally
        {
            using(var run=Registry.CurrentUser.OpenSubKey(runKey,true))run?.DeleteValue(name,false);
            using(var approved=Registry.CurrentUser.OpenSubKey(approvedKey,true))approved?.DeleteValue(name,false);
        }
        using(var run=Registry.CurrentUser.OpenSubKey(runKey))using(var approved=Registry.CurrentUser.OpenSubKey(approvedKey))
            check(created&&run?.GetValue(name) is null&&approved?.GetValue(name) is null,"Native test values are removed after the check");

        var loc=new StartupLocation(RegistrationSource.User64,false);
        var row=new StartupAppRow(StartupAppKind.Registry,"Run\u202eSentinel",StartupRecords.Origin(loc,app.L),StartupApps.ApprovalFor(loc),new(StartupAppState.Disabled,3,new DateTimeOffset(when),"Observed disabled form with the recorded time."),1);
        var fake=new StartupAppsResult(DateTimeOffset.UtcNow,new[]{row},new[]{new StartupAppSource("x","y")},false);
        var dialog=new StartupAppsDialog(app,fake);
        check(dialog.CurrentReport.Contains("Microsoft does not document StartupApproved")&&dialog.CurrentReport.Contains("2026-10-01 12:00:00Z"),"Startup Apps report includes uncertainty and recorded time");
        check(dialog.SelectedReport is null,"Nothing is selected or copied initially");
        var list=Find<ListView>(dialog).First();list.SelectedIndex=0;check(dialog.SelectedReport is string selected&&selected.Contains("[U+202E]"),"Selected row copy escapes bidi controls");
        check(Find<ListView>(dialog).All(VirtualizingPanel.GetIsVirtualizing),"Startup Apps lists are virtualized");dialog.Close();

        var choices=new[]{StartupFolderChoice.FromReported(StartupFolderKind.CurrentUser,@"C:\Users\x\Startup"),StartupFolderChoice.Unavailable(StartupFolderKind.Common)};
        var chooser=new StartupFolderChooser(app,choices,allowSkip:true);
        check(Find<Button>(chooser).Any(b=>(b.Content as string)==app.L("Continue Without Startup Folders"))&&!chooser.Skipped&&chooser.Selected.Length==0,"Chooser offers an explicit skip and selects nothing initially");chooser.Close();
        var plain=new StartupFolderChooser(app,choices);check(!Find<Button>(plain).Any(b=>(b.Content as string)==app.L("Continue Without Startup Folders")),"Existing Startup-folder action keeps its original buttons");plain.Close();
    }
}
