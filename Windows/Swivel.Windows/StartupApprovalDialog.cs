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

internal sealed class StartupApprovalDialog:Window
{
    private readonly App app;private readonly StartupApprovalSnapshot snapshot;private readonly ListView list;private readonly TextBox details;private readonly Button copySelected;
    internal string CurrentReport=>Report(null);
    internal StartupApprovalDialog(App app,StartupApprovalSnapshot snapshot)
    {
        this.app=app;this.snapshot=snapshot;Title=app.L("StartupApproved Raw Registry Snapshot");Width=1050;Height=820;MinWidth=560;MinHeight=460;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(20)};var footer=new WrapPanel();DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        footer.Children.Add(UI.Button(app.L("Copy Report"),()=>app.Try(()=>Clipboard.SetText(CurrentReport))));copySelected=UI.Button(app.L("Copy Selected Value"),()=>{if(list?.SelectedItem is Row row)app.Try(()=>Clipboard.SetText(Report(row.Record)));});copySelected.IsEnabled=false;footer.Children.Add(copySelected);var close=UI.Button(app.L("Close"),()=>DialogResult=false);close.IsCancel=true;footer.Children.Add(close);
        var body=new StackPanel();root.Children.Add(UI.Scroll(body));Content=root;
        foreach(string text in new[]{StartupApprovalSnapshotReader.Scope,StartupApprovalSnapshotReader.Meaning,StartupApprovalSnapshotReader.Privacy})body.Children.Add(UI.Text(app.L(text),12));
        var rootList=new ListView{Height=132,SelectionMode=SelectionMode.Single};VirtualizingPanel.SetIsVirtualizing(rootList,true);VirtualizingPanel.SetVirtualizationMode(rootList,VirtualizationMode.Recycling);ScrollViewer.SetCanContentScroll(rootList,true);
        var rootView=new GridView();foreach(var col in new[]{("Registry view","Source",250d),("Location","Area",180d),("Values","Count",90d),("Status","Status",380d)})rootView.Columns.Add(new GridViewColumn{Header=app.L(col.Item1),DisplayMemberBinding=new Binding(col.Item2),Width=col.Item3});rootList.View=rootView;
        rootList.ItemsSource=snapshot.Roots.Select(r=>new RootRow(app,r)).ToArray();AutomationProperties.SetName(rootList,app.L("StartupApproved registry locations"));body.Children.Add(rootList);
        list=new ListView{Height=340,SelectionMode=SelectionMode.Single};VirtualizingPanel.SetIsVirtualizing(list,true);VirtualizingPanel.SetVirtualizationMode(list,VirtualizationMode.Recycling);ScrollViewer.SetCanContentScroll(list,true);
        var view=new GridView();foreach(var col in new[]{("Registry view","Source",220d),("Location","Area",170d),("Value name","Name",250d),("Type","Type",65d),("Raw bytes (hex)","Hex",230d),("Status","Status",180d)})view.Columns.Add(new GridViewColumn{Header=app.L(col.Item1),DisplayMemberBinding=new Binding(col.Item2),Width=col.Item3});list.View=view;
        var rows=snapshot.Records.Select(r=>new Row(app,r)).ToArray();list.ItemsSource=rows;AutomationProperties.SetName(list,app.L("StartupApproved values"));body.Children.Add(list);
        details=new TextBox{IsReadOnly=true,TextWrapping=TextWrapping.Wrap,MaxHeight=130,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};body.Children.Add(details);
        list.SelectionChanged+=(_,_)=>{copySelected.IsEnabled=list.SelectedItem is Row;details.Text=list.SelectedItem is Row row?StartupApprovalSnapshotReader.Describe(row.Record,app.L):"";};
        SourceInitialized+=(_,_)=>WebRouteLayout.Fit(this,Owner??this);DpiChanged+=(_,_)=>WebRouteLayout.Fit(this,this);
    }
    private string Report(StartupApprovalRecord? selected)
    {
        var text=new StringBuilder();void Line(string value){if(value.Length>StartupApprovalSnapshotReader.MaxReportChars-Environment.NewLine.Length-text.Length)throw new UserError(StartupApprovalSnapshotReader.ReportLimit);text.Append(value).Append(Environment.NewLine);}
        Line(app.L("Windows Doctor")+" · "+Title);Line(StartupApprovalSnapshotReader.Header(snapshot,app.L));
        if(selected is not null){Line(StartupApprovalSnapshotReader.Describe(selected,app.L));return text.ToString();}
        foreach(var root in snapshot.Roots)Line(StartupApprovalSnapshotReader.SourceName(root.Location.Source,app.L)+" · "+app.L(StartupApprovalSnapshotReader.AreaName(root.Location.Area))+" · "+root.Recorded.ToString(CultureInfo.InvariantCulture)+" · "+app.L(StartupApprovalSnapshotReader.RootStatus(root.Issues,app.L)));
        foreach(var row in snapshot.Records)Line(StartupApprovalSnapshotReader.Describe(row,app.L));return text.ToString();
    }
    private sealed record RootRow(App Owner,StartupApprovalRootResult Result)
    {public string Source=>StartupApprovalSnapshotReader.SourceName(Result.Location.Source,Owner.L);public string Area=>Owner.L(StartupApprovalSnapshotReader.AreaLabel(Result.Location.Area));public string Count=>Result.Recorded.ToString(CultureInfo.CurrentCulture);public string Status=>StartupApprovalSnapshotReader.RootStatus(Result.Issues,Owner.L);}
    private sealed record Row(App Owner,StartupApprovalRecord Record)
    {public string Source=>StartupApprovalSnapshotReader.SourceName(Record.Location.Source,Owner.L);public string Area=>Owner.L(StartupApprovalSnapshotReader.AreaLabel(Record.Location.Area));public string Name=>DoctorFacts.Visible(Record.Name??Owner.L("Invalid or oversized value name"));public string Type=>Record.Type?.ToString(CultureInfo.InvariantCulture)??"—";public string Hex=>Record.HexData??Owner.L("Unavailable or over 64 bytes");public string Status=>StartupApprovalSnapshotReader.RowStatus(Record.Issues,Owner.L);}
}
