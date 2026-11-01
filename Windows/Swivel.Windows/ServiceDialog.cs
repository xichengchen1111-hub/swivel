using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class ServiceDialog:Window
{
    private readonly App app;
    private readonly ServiceSnapshot snapshot;
    private readonly ListView list=new(){SelectionMode=SelectionMode.Single};
    private readonly TextBox details=new(){IsReadOnly=true,TextWrapping=TextWrapping.Wrap,Height=240,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Margin=new Thickness(0,8,0,12)};
    private readonly string[] issues;
    private bool reveal;
    internal string CurrentReport
    {
        get
        {
            var report=new StringBuilder();
            void Line(string value){if(value.Length>ScheduledTaskDialog.CopyLimit-report.Length-Environment.NewLine.Length)throw new UserError("The service report exceeds the complete-report limit.");report.AppendLine(value);}
            Line(app.L("Windows Doctor")+" · "+Title);Line(snapshot.Captured.ToString("g",CultureInfo.CurrentCulture));Line(app.L("Reported Service Rows")+": "+snapshot.Services.Count.ToString(CultureInfo.InvariantCulture));
            Line(app.L(snapshot.Partial?"Partial Service Snapshot":"No collection issue was recorded within the stated scope."));
            foreach(var row in snapshot.Services){Line("");Line(Describe(row));}
            Line(app.L("Service Read Issues")+": "+issues.Length.ToString(CultureInfo.InvariantCulture));foreach(string issue in issues)Line(issue);
            foreach(string note in Notes())Line(app.L(note));return report.ToString();
        }
    }
    internal ServiceDialog(App app,ServiceSnapshot snapshot)
    {
        this.app=app;this.snapshot=snapshot;
        Title=app.L("Windows Service Status Snapshot");Width=1080;Height=850;MinWidth=520;MinHeight=400;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(20)};var footer=new WrapPanel();DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);footer.Children.Add(UI.Button(app.L("Copy Report"),()=>app.Try(()=>Clipboard.SetText(CurrentReport))));var close=UI.Button(app.L("Close"),()=>Close());close.IsCancel=true;close.IsDefault=true;footer.Children.Add(close);
        var body=new StackPanel();root.Children.Add(UI.Scroll(body));Content=root;body.Children.Add(UI.Text(snapshot.Captured.ToString("g",CultureInfo.CurrentCulture),14));body.Children.Add(UI.Text(app.L("Reported Service Rows")+": "+snapshot.Services.Count.ToString(CultureInfo.CurrentCulture),14));
        body.Children.Add(UI.Text(app.L(snapshot.Partial?"Partial Service Snapshot":"No collection issue was recorded within the stated scope."),13));
        var choice=new CheckBox{Content=app.L(ConfigurationDetails.Reveal),IsChecked=false,Margin=new Thickness(0,8,0,12)};
        AutomationProperties.SetName(choice,app.L(ConfigurationDetails.Reveal));choice.Checked+=(_,_)=>{reveal=true;UpdateDetails();};choice.Unchecked+=(_,_)=>{reveal=false;UpdateDetails();};body.Children.Add(choice);
        var tabs=new TabControl{Height=300};list.ItemsSource=snapshot.Services.Select(s=>new Row(this,s)).ToArray();var view=new GridView();
        foreach(var column in new[]{("Service Name","Name",190d),("Display Name","Display",240d),("Reported Service State","State",220d),("Reported Service Type","Type",130d),("Reported Service PID","Pid",120d),("Startup Type","Start",130d),("Service configuration","Configuration",160d)})view.Columns.Add(new GridViewColumn{Header=app.L(column.Item1),DisplayMemberBinding=new Binding(column.Item2),Width=column.Item3});
        list.View=view;VirtualizingPanel.SetIsVirtualizing(list,true);VirtualizingPanel.SetVirtualizationMode(list,VirtualizationMode.Recycling);ScrollViewer.SetCanContentScroll(list,true);AutomationProperties.SetName(list,app.L("Reported Windows Services"));tabs.Items.Add(new TabItem{Header=app.L("Reported Windows Services"),Content=list});
        list.SelectionChanged+=(_,_)=>UpdateDetails();
        var issueRows=new List<string>();foreach(var issue in snapshot.Issues)
        {
            var text=new StringBuilder(app.L(ServiceRecords.Phase(issue.Phase)));if(issue.Record is int index)text.Append(" · ").Append(app.L("Record")).Append(' ').Append(index.ToString(CultureInfo.InvariantCulture));if(issue.Error is uint error)text.Append(" · ").Append(app.L("Windows Error Code")).Append(' ').Append(error.ToString(CultureInfo.InvariantCulture));if(issue.NeededBytes is uint needed)text.Append(" · ").Append(app.L("Additional Service Bytes Reported")).Append(' ').Append(needed.ToString(CultureInfo.InvariantCulture));if(issue.ReportedRecords is uint returned)text.Append(" · ").Append(app.L("Native Service Rows Reported")).Append(' ').Append(returned.ToString(CultureInfo.InvariantCulture));issueRows.Add(text.ToString());
        }
        issues=issueRows.ToArray();var issueList=new ListBox{ItemsSource=issues};VirtualizingPanel.SetIsVirtualizing(issueList,true);VirtualizingPanel.SetVirtualizationMode(issueList,VirtualizationMode.Recycling);ScrollViewer.SetCanContentScroll(issueList,true);AutomationProperties.SetName(issueList,app.L("Service Read Issues"));tabs.Items.Add(new TabItem{Header=app.L("Service Read Issues")+" ("+issues.Length.ToString(CultureInfo.CurrentCulture)+")",Content=issueList});body.Children.Add(tabs);
        AutomationProperties.SetName(details,app.L("Selected Service Configuration"));body.Children.Add(details);
        var scope=new StackPanel();foreach(string note in Notes())scope.Children.Add(UI.Text(app.L(note),12));body.Children.Add(new Expander{Header=app.L("Scope, Privacy and Collection Limits"),Content=scope});
        SourceInitialized+=(_,_)=>WebRouteLayout.Fit(this,Owner??this);DpiChanged+=(_,_)=>WebRouteLayout.Fit(this,this);
    }
    private static IEnumerable<string> Notes(){yield return ServiceRecords.Scope;yield return ServiceRecords.Limits;yield return ConfigurationDiagnosis.Limits;}
    private void UpdateDetails()=>details.Text=list.SelectedItem is Row row?row.ToString():"";
    private string Describe(ServiceRecord row)=>string.Join("\n",new[]{app.L("Service Name")+": "+ServiceRecords.Visible(row.Name),app.L("Display Name")+": "+ServiceRecords.Visible(row.DisplayName),app.L("Reported Service State")+": "+app.L(ServiceRecords.State(row.State))+" ("+row.State.ToString(CultureInfo.InvariantCulture)+")",app.L("Reported Service Type")+": 0x"+row.Type.ToString("X8",CultureInfo.InvariantCulture),app.L("Reported Service PID")+": "+row.ProcessId.ToString(CultureInfo.InvariantCulture),ConfigurationDetails.Service(row,snapshot.Services,app.L,reveal)});
    private sealed class Row(ServiceDialog owner,ServiceRecord source)
    {
        public string Name=>ServiceRecords.Visible(source.Name);public string Display=>ServiceRecords.Visible(source.DisplayName);public string State=>owner.app.L(ServiceRecords.State(source.State))+" ("+source.State.ToString(CultureInfo.InvariantCulture)+")";
        public string Type=>"0x"+source.Type.ToString("X8",CultureInfo.InvariantCulture);public string Pid=>source.ProcessId.ToString(CultureInfo.InvariantCulture);
        public string Start=>source.Configuration is {} config?owner.app.L(ConfigurationDiagnosis.Start(config.StartType)):owner.app.L("Not reported");
        public string Configuration=>owner.app.L(source.ConfigurationErrors.Count>0?"Partial":source.Configuration is null?"Not reported":"Reported");
        public override string ToString()=>owner.Describe(source);
    }
}
