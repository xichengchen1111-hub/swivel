using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class ServiceDialog:Window
{
    internal string CurrentReport{get;}="";
    internal ServiceDialog(App app,ServiceSnapshot snapshot)
    {
        Title=app.L("Windows Service Status Snapshot");Width=1000;Height=780;MinWidth=520;MinHeight=400;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(20)};var footer=new WrapPanel();DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);footer.Children.Add(UI.Button(app.L("Copy Report"),()=>app.Try(()=>Clipboard.SetText(CurrentReport))));var close=UI.Button(app.L("Close"),()=>DialogResult=false);close.IsCancel=true;footer.Children.Add(close);
        var body=new StackPanel();root.Children.Add(UI.Scroll(body));Content=root;body.Children.Add(UI.Text(snapshot.Captured.ToString("g",CultureInfo.CurrentCulture),14));body.Children.Add(UI.Text(app.L("Reported Service Rows")+": "+snapshot.Services.Count.ToString(CultureInfo.CurrentCulture),14));
        var rows=new List<Row>();foreach(var service in snapshot.Services)rows.Add(new(ServiceRecords.Visible(service.Name),ServiceRecords.Visible(service.DisplayName),app.L(ServiceRecords.State(service.State))+" ("+service.State.ToString(CultureInfo.InvariantCulture)+")","0x"+service.Type.ToString("X8",CultureInfo.InvariantCulture),service.ProcessId.ToString(CultureInfo.InvariantCulture)));
        var tabs=new TabControl{Height=360};var list=new ListView{ItemsSource=rows,SelectionMode=SelectionMode.Single};var view=new GridView();
        foreach(var column in new[]{("Service Name","Name",210d),("Display Name","Display",280d),("Reported Service State","State",220d),("Reported Service Type","Type",140d),("Reported Service PID","Pid",150d)})view.Columns.Add(new GridViewColumn{Header=app.L(column.Item1),DisplayMemberBinding=new Binding(column.Item2),Width=column.Item3});
        list.View=view;VirtualizingPanel.SetIsVirtualizing(list,true);VirtualizingPanel.SetVirtualizationMode(list,VirtualizationMode.Recycling);ScrollViewer.SetCanContentScroll(list,true);AutomationProperties.SetName(list,app.L("Reported Windows Services"));tabs.Items.Add(new TabItem{Header=app.L("Reported Windows Services"),Content=list});
        var issueRows=new List<string>();foreach(var issue in snapshot.Issues)
        {
            var text=new StringBuilder(app.L(ServiceRecords.Phase(issue.Phase)));if(issue.Record is int index)text.Append(" · ").Append(app.L("Record")).Append(' ').Append(index.ToString(CultureInfo.InvariantCulture));if(issue.Error is uint error)text.Append(" · ").Append(app.L("Windows Error Code")).Append(' ').Append(error.ToString(CultureInfo.InvariantCulture));if(issue.NeededBytes is uint needed)text.Append(" · ").Append(app.L("Additional Service Bytes Reported")).Append(' ').Append(needed.ToString(CultureInfo.InvariantCulture));if(issue.ReportedRecords is uint returned)text.Append(" · ").Append(app.L("Native Service Rows Reported")).Append(' ').Append(returned.ToString(CultureInfo.InvariantCulture));issueRows.Add(text.ToString());
        }
        var issues=new ListBox{ItemsSource=issueRows};VirtualizingPanel.SetIsVirtualizing(issues,true);VirtualizingPanel.SetVirtualizationMode(issues,VirtualizationMode.Recycling);ScrollViewer.SetCanContentScroll(issues,true);AutomationProperties.SetName(issues,app.L("Service Read Issues"));tabs.Items.Add(new TabItem{Header=app.L("Service Read Issues")+" ("+issueRows.Count.ToString(CultureInfo.CurrentCulture)+")",Content=issues});body.Children.Add(tabs);
        var report=new StringBuilder();report.AppendLine(app.L("Windows Doctor")+" · "+Title);report.AppendLine(snapshot.Captured.ToString("g",CultureInfo.CurrentCulture));report.AppendLine(app.L("Reported Service Rows")+": "+rows.Count.ToString(CultureInfo.InvariantCulture));
        foreach(var row in rows){report.AppendLine();report.AppendLine(app.L("Service Name")+": "+row.Name);report.AppendLine(app.L("Display Name")+": "+row.Display);report.AppendLine(app.L("Reported Service State")+": "+row.State);report.AppendLine(app.L("Reported Service Type")+": "+row.Type);report.AppendLine(app.L("Reported Service PID")+": "+row.Pid);}report.AppendLine();report.AppendLine(app.L("Service Read Issues")+": "+issueRows.Count.ToString(CultureInfo.InvariantCulture));foreach(string issue in issueRows)report.AppendLine(issue);
        foreach(string note in new[]{ServiceRecords.Scope,ServiceRecords.Limits}){body.Children.Add(UI.Text(app.L(note),12));report.AppendLine();report.AppendLine(app.L(note));}CurrentReport=report.ToString();SourceInitialized+=(_,_)=>WebRouteLayout.Fit(this,Owner??this);DpiChanged+=(_,_)=>WebRouteLayout.Fit(this,this);
    }
    private sealed record Row(string Name,string Display,string State,string Type,string Pid);
}
