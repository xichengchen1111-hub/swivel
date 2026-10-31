using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class SlowMachineDialog:Window
{
    internal SlowAction Requested{get;private set;}
    internal string CurrentReport{get;}
    internal SlowMachineDialog(App app,SlowMachineSnapshot snapshot)
    {
        Title=app.L("Slow or Hot");Width=820;Height=780;MinWidth=480;MinHeight=360;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(20)};var footer=new WrapPanel();DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        footer.Children.Add(UI.Button(app.L("Copy Report"),()=>app.Try(()=>Clipboard.SetText(CurrentReport??""))));var close=UI.Button(app.L("Close"),()=>DialogResult=false);close.IsCancel=true;footer.Children.Add(close);
        var body=new StackPanel();root.Children.Add(UI.Scroll(body));Content=root;var report=new StringBuilder();
        void Text(string text){body.Children.Add(UI.Text(text));report.AppendLine(text);}
        Text(Title+" · "+DateTimeOffset.Now.ToString("g",CultureInfo.CurrentCulture));
        foreach(var finding in SlowMachine.Findings(snapshot))
        {
            Text(app.L(finding.Text));if(finding.Action==SlowAction.None)continue;
            string title=finding.Action switch{SlowAction.AppMemory=>"Inspect App Memory…",SlowAction.UsageMonitor=>"Monitor CPU and Memory…",SlowAction.CleanUp=>"Clean Up Files…",_=>"Open Windows Update"};
            body.Children.Add(UI.Button(app.L(title),()=>{Requested=finding.Action;DialogResult=true;}));
        }
        string Number(ulong? value)=>value?.ToString("N0",CultureInfo.CurrentCulture)??app.L("Unavailable");
        Text(string.Format(app.L("Physical memory load: {0}%; commit: {1} / {2} bytes; pagefile use: {3} / {4} bytes."),snapshot.MemoryLoad?.ToString("F0",CultureInfo.CurrentCulture)??app.L("Unavailable"),Number(snapshot.CommitUsed),Number(snapshot.CommitLimit),Number(snapshot.PagefileUsed),Number(snapshot.PagefileTotal)));
        Text(string.Format(app.L("System-drive free space: {0} / {1} bytes; uptime: {2:F1} days; pending restart: {3}."),Number(snapshot.SystemFree),Number(snapshot.SystemTotal),snapshot.Uptime.TotalDays,app.L(snapshot.PendingReboot is bool b?b?"Yes":"No":"Unavailable")));
        Text(app.L("Highest CPU shares (up to three processes)"));
        foreach(var p in snapshot.Processes.Where(p=>p.CpuShare.HasValue).OrderByDescending(p=>p.CpuShare).ThenBy(p=>p.Id).Take(3))Text(string.Format(app.L("{0} (PID {1}): {2:F1}% of total CPU"),Name(p),p.Id,p.CpuShare));
        Text(app.L("Largest working sets (up to three processes)"));
        foreach(var p in snapshot.Processes.Where(p=>p.WorkingSet.HasValue).OrderByDescending(p=>p.WorkingSet).ThenBy(p=>p.Id).Take(3))Text(string.Format(app.L("{0} (PID {1}): {2} bytes working set"),Name(p),p.Id,Number(p.WorkingSet)));
        foreach(string issue in snapshot.Issues)Text(app.L(issue));
        Text(app.L(SlowMachine.Thresholds));Text(app.L(SlowMachine.Limits));CurrentReport=report.ToString();
        string Name(SlowProcess p)=>p.Name.Length==0?app.L("Unavailable"):DoctorFacts.Visible(p.Name);
        SourceInitialized+=(_,_)=>WebRouteLayout.Fit(this,Owner??this);DpiChanged+=(_,_)=>WebRouteLayout.Fit(this,this);
    }
}
