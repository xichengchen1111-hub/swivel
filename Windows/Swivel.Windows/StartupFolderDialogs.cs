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

internal sealed class StartupFolderChooser:Window
{
    private readonly StartupFolderChoice[] choices;private readonly HashSet<StartupFolderKind> selected=new();
    internal StartupFolderChoice[] Selected=>choices.Where(c=>c.CanAttempt&&selected.Contains(c.Kind)).ToArray();
    internal bool Skipped{get;private set;}
    internal StartupFolderChooser(App app,IReadOnlyList<StartupFolderChoice> source,bool allowSkip=false)
    {
        choices=source.ToArray();if(choices.Length!=2||choices.Select(c=>c.Kind).Distinct().Count()!=2)throw new ArgumentException("Invalid startup folder choices.");
        Title=app.L("Review Startup Folder Locations");Width=860;Height=740;MinWidth=500;MinHeight=400;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(20)};var footer=new WrapPanel();DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        var read=UI.Button(app.L("Read Selected Entry Metadata"),()=>{if(Selected.Length>0)DialogResult=true;});read.IsEnabled=false;footer.Children.Add(read);if(allowSkip)footer.Children.Add(UI.Button(app.L("Continue Without Startup Folders"),()=>{Skipped=true;DialogResult=true;}));var cancel=UI.Button(app.L("Cancel"),()=>DialogResult=false);cancel.IsCancel=true;footer.Children.Add(cancel);
        var body=new StackPanel();root.Children.Add(UI.Scroll(body));Content=root;foreach(string text in new[]{StartupFolders.Scope,StartupFolders.Limits,StartupFolders.Meaning})body.Children.Add(UI.Text(app.L(text),12));
        foreach(var choice in choices)
        {
            var box=new CheckBox{Content=app.L(StartupFolders.Name(choice.Kind)),IsEnabled=choice.CanAttempt,IsChecked=false,Margin=new Thickness(0,16,0,4)};AutomationProperties.SetName(box,app.L(StartupFolders.Name(choice.Kind)));
            box.Checked+=(_,_)=>{if(choice.CanAttempt)selected.Add(choice.Kind);read.IsEnabled=Selected.Length>0;};box.Unchecked+=(_,_)=>{selected.Remove(choice.Kind);read.IsEnabled=Selected.Length>0;};body.Children.Add(box);
            body.Children.Add(new TextBox{Text=CacheLocations.VisiblePath(choice.ReportedPath),IsReadOnly=true,TextWrapping=TextWrapping.Wrap,MaxHeight=90,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});body.Children.Add(UI.Text(app.L(choice.Issue??"Path passed basic checks; access is checked only after you choose Read Selected Entry Metadata."),12));
        }
        SourceInitialized+=(_,_)=>WebRouteLayout.Fit(this,Owner??this);DpiChanged+=(_,_)=>WebRouteLayout.Fit(this,this);
    }
}

internal sealed class StartupFolderDialog:Window
{
    internal const int CopyLimit=4*1024*1024;
    private readonly App app;private readonly StartupFolderResult[] results;
    internal string CurrentReport=>Report(null);
    internal StartupFolderDialog(App app,IReadOnlyList<StartupFolderResult> source)
    {
        this.app=app;results=source.ToArray();if(results.Length is <1 or >2||results.Select(r=>r.Choice.Kind).Distinct().Count()!=results.Length)throw new ArgumentException("Invalid startup folder results.");
        Title=app.L("Startup Folder Entry Metadata");Width=1020;Height=820;MinWidth=540;MinHeight=440;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(20)};var footer=new WrapPanel();DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);var list=NativeList(360);
        footer.Children.Add(UI.Button(app.L("Copy Report"),()=>app.Try(()=>Clipboard.SetText(CurrentReport))));var copy=UI.Button(app.L("Copy Selected Item"),()=>{if(list.SelectedItem is Row row)app.Try(()=>Clipboard.SetText(Report(row)));});copy.IsEnabled=false;footer.Children.Add(copy);var close=UI.Button(app.L("Close"),()=>DialogResult=false);close.IsCancel=true;footer.Children.Add(close);
        var body=new StackPanel();root.Children.Add(UI.Scroll(body));Content=root;foreach(string text in new[]{StartupFolders.Scope,StartupFolders.Limits,StartupFolders.Meaning,StartupFolders.Privacy})body.Children.Add(UI.Text(app.L(text),12));
        foreach(var result in results)body.Children.Add(UI.Text(RootDetail(result),12));
        var rows=results.Where(r=>r.Scan is not null).SelectMany(r=>r.Scan!.Root.Children.Select(n=>new Row(this,r,n))).ToArray();
        var view=new GridView();foreach(var column in new[]{("Folder","Root",170d),("Name","Name",260d),("Filename Hint","Hint",210d),("Logical Bytes","Bytes",140d),("Reason","Issue",300d)})view.Columns.Add(new GridViewColumn{Header=app.L(column.Item1),DisplayMemberBinding=new Binding(column.Item2),Width=column.Item3});list.View=view;list.ItemsSource=rows;AutomationProperties.SetName(list,Title);body.Children.Add(list);
        if(rows.Length==0)body.Children.Add(UI.Text(app.L("No direct entries were recorded. Failed, partial or unexamined locations can still contain startup items."),12));
        var detail=new TextBox{IsReadOnly=true,TextWrapping=TextWrapping.Wrap,MaxHeight=220,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};body.Children.Add(detail);list.SelectionChanged+=(_,_)=>{copy.IsEnabled=list.SelectedItem is Row;detail.Text=list.SelectedItem is Row row?ItemDetail(row):"";};
        SourceInitialized+=(_,_)=>WebRouteLayout.Fit(this,Owner??this);DpiChanged+=(_,_)=>WebRouteLayout.Fit(this,this);
    }
    private string RootDetail(StartupFolderResult result)
    {
        var text=new StringBuilder(app.L(StartupFolders.Name(result.Choice.Kind))+Environment.NewLine+CacheLocations.VisiblePath(result.Choice.Path!)+Environment.NewLine);
        text.AppendLine(app.L(result.Issue??(result.Scan!.Root.Incomplete?"Partial":"Counted")));
        if(result.Error is uint error)text.AppendLine(app.L("Windows Error Code")+": "+error.ToString(CultureInfo.InvariantCulture));
        text.AppendLine(string.Format(app.L("Allocated metadata entries: {0} · shared-clock deadline: {1} seconds"),result.EntryQuota,result.DeadlineSeconds));
        if(result.Scan is {} scan)
        {
            text.AppendLine(app.L("Metadata Entries")+": "+scan.Entries.ToString(CultureInfo.CurrentCulture));
            if(scan.Root.Skipped>scan.Root.Children.Sum(c=>c.Skipped))text.AppendLine(app.L("Folder enumeration was incomplete"));
            if(scan.EntryLimit)text.AppendLine(app.L("Entry limit reached"));if(scan.TimeLimit)text.AppendLine(app.L("Folder scan time limit reached"));
        }
        text.Append(app.L("Result Recorded")).Append(": ").Append(result.Recorded.ToString("g",CultureInfo.CurrentCulture));return text.ToString();
    }
    private string ItemDetail(Row row)=>row.Root+Environment.NewLine+row.Path+Environment.NewLine+row.Hint+Environment.NewLine+app.L("Logical Bytes")+": "+row.Bytes+Environment.NewLine+row.Issue;
    private string Report(Row? selected)
    {
        var text=new StringBuilder();void Line(string value){if(value.Length>CopyLimit-Environment.NewLine.Length-text.Length)throw new UserError("This startup-folder copy exceeds the complete-report limit. Copy one selected item instead.");text.Append(value).Append(Environment.NewLine);}
        Line(app.L("Windows Doctor")+" · "+Title);foreach(string key in new[]{StartupFolders.Scope,StartupFolders.Limits,StartupFolders.Meaning,StartupFolders.Privacy})Line(app.L(key));
        if(selected is not null){Line(RootDetail(selected.Result));Line(ItemDetail(selected));}
        else foreach(var result in results)
        {
            Line(RootDetail(result));if(result.Scan is null)continue;
            foreach(var node in result.Scan.Root.Children)Line(ItemDetail(new(this,result,node)));
        }
        return text.ToString();
    }
    private static ListView NativeList(double height){var list=new ListView{Height=height,SelectionMode=SelectionMode.Single};VirtualizingPanel.SetIsVirtualizing(list,true);VirtualizingPanel.SetVirtualizationMode(list,VirtualizationMode.Recycling);ScrollViewer.SetCanContentScroll(list,true);return list;}
    private sealed record Row(StartupFolderDialog Owner,StartupFolderResult Result,FolderSizeNode Node)
    {
        public string Root=>Owner.app.L(StartupFolders.Name(Result.Choice.Kind));public string Name=>CacheLocations.VisiblePath(Node.Name);public string Path=>CacheLocations.VisiblePath(Result.Choice.Path!)+"\\"+Name;
        public string Hint=>Owner.app.L(StartupFolders.Hint(Node));public string Bytes=>Node.Directory||Node.Issue is not null?"—":Node.Bytes.ToString("N0",CultureInfo.CurrentCulture);public string Issue=>Node.Issue is string issue?Owner.app.L(issue):"";
    }
}
