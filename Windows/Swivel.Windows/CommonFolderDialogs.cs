using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Swivel.Core;

namespace Swivel.Windows;

internal enum CommonFolderChooserMode { Measure, Compare }
internal sealed class CommonFolderChooser:Window
{
    private readonly CommonFolderChoice[] choices;private readonly HashSet<CommonFolderKind> selected=new();
    internal CommonFolderChoice[] Selected=>choices.Where(c=>c.CanAttempt&&selected.Contains(c.Kind)).ToArray();
    internal CommonFolderChooser(App app,IReadOnlyList<CommonFolderChoice> source,CommonFolderChooserMode mode=CommonFolderChooserMode.Measure)
    {
        if(!Enum.IsDefined(mode))throw new ArgumentOutOfRangeException(nameof(mode));bool compare=mode==CommonFolderChooserMode.Compare;
        choices=source.ToArray();if(choices.Length!=6||choices.Select(c=>c.Kind).Distinct().Count()!=6)throw new ArgumentException("Invalid common folder choices.");
        Title=app.L(compare?"Choose Common Folders to Compare":"Choose Common Folders to Measure");Width=880;Height=800;MinWidth=500;MinHeight=400;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(20)};var footer=new WrapPanel();DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);var scan=UI.Button(app.L(compare?"Review Selected Comparison…":"Measure Selected Folders"),()=>{if(Selected.Length>0)DialogResult=true;});scan.IsEnabled=false;footer.Children.Add(scan);var cancel=UI.Button(app.L("Cancel"),()=>DialogResult=false);cancel.IsCancel=true;footer.Children.Add(cancel);
        var body=new StackPanel();root.Children.Add(UI.Scroll(body));Content=root;body.Children.Add(UI.Text(app.L(compare?CommonDuplicates.Scope:CommonFolders.Scope),12));body.Children.Add(UI.Text(app.L(compare?CommonDuplicates.Limits:CommonFolders.Limits),12));
        foreach(var choice in choices)
        {
            var box=new CheckBox{Content=app.L(CommonFolders.Name(choice.Kind)),IsEnabled=choice.CanAttempt,IsChecked=false,Margin=new Thickness(0,16,0,4)};AutomationProperties.SetName(box,app.L(CommonFolders.Name(choice.Kind)));
            box.Checked+=(_,_)=>{if(choice.CanAttempt)selected.Add(choice.Kind);scan.IsEnabled=Selected.Length>0;};box.Unchecked+=(_,_)=>{selected.Remove(choice.Kind);scan.IsEnabled=Selected.Length>0;};body.Children.Add(box);
            body.Children.Add(new TextBox{Text=CacheLocations.VisiblePath(choice.ReportedPath),IsReadOnly=true,TextWrapping=TextWrapping.Wrap,MaxHeight=90,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
            string status=app.L(choice.Issue??(compare?"Path passed basic checks; access and safety are checked only after you confirm content comparison.":"Path passed basic checks; access and safety are checked only after you choose Measure."));if(choice.NativeError is uint error)status+=" · "+app.L("Folder Lookup HRESULT")+" 0x"+error.ToString("X8",CultureInfo.InvariantCulture);if(choice.Truncated)status+=" · "+app.L(compare?"Reported path preview was truncated; this location cannot be compared.":"Reported path preview was truncated; this location cannot be measured.");body.Children.Add(UI.Text(status,12));
        }
        SourceInitialized+=(_,_)=>WebRouteLayout.Fit(this,Owner??this);DpiChanged+=(_,_)=>WebRouteLayout.Fit(this,this);
    }
}

internal sealed class CommonFolderResultsDialog:Window
{
    internal string CurrentReport{get;}="";
    internal CommonFolderResultsDialog(App app,IReadOnlyList<CommonFolderResult> source,CancellationToken token)
    {
        var results=source.ToArray();if(results.Length is <1 or >6||results.Select(r=>r.Choice.Kind).Distinct().Count()!=results.Length)throw new ArgumentException("Invalid common folder results.");
        Title=app.L("Common Folder Measurement Results");Width=980;Height=760;MinWidth=520;MinHeight=400;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(20)};var footer=new WrapPanel();DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);var list=new ListView{Height=300,SelectionMode=SelectionMode.Single};
        var details=new TextBox{IsReadOnly=true,TextWrapping=TextWrapping.Wrap,MaxHeight=160,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};var map=UI.Button(app.L("View Selected Map"),()=>{
            if(token.IsCancellationRequested||list.SelectedItem is not Row {Result.Scan:not null} row)return;
            var dialog=new FolderMapDialog(app,row.Result.Choice.Path!,row.Result.Scan,CommonFolders.MapContext,CommonFolders.Name(row.Result.Choice.Kind),"Common Folder Size Map"){Owner=this};
            if(token.IsCancellationRequested)return;dialog.ShowDialog();
        });map.IsEnabled=false;footer.Children.Add(map);footer.Children.Add(UI.Button(app.L("Copy Summary"),()=>app.Try(()=>Clipboard.SetText(CurrentReport))));var close=UI.Button(app.L("Close"),()=>DialogResult=false);close.IsCancel=true;footer.Children.Add(close);
        var body=new StackPanel();root.Children.Add(UI.Scroll(body));Content=root;body.Children.Add(UI.Text(app.L(CommonFolders.Scope),12));body.Children.Add(UI.Text(app.L(CommonFolders.Limits),12));var rows=new List<Row>();var report=new StringBuilder(app.L("Windows Doctor")+" · "+Title+Environment.NewLine);report.AppendLine(app.L(CommonFolders.Scope));report.AppendLine(app.L(CommonFolders.Limits));
        foreach(var result in results)
        {
            string status=result.Issue is string issue?app.L(issue):app.L(result.Scan!.Root.Incomplete?"Partial":"Counted");if(result.Error is uint error)status+=" · "+app.L("Windows Error Code")+" "+error.ToString(CultureInfo.InvariantCulture);
            string name=app.L(CommonFolders.Name(result.Choice.Kind)),path=CacheLocations.VisiblePath(result.Choice.Path!),bytes=result.Scan is null?"—":result.Scan.Root.Bytes.ToString("N0",CultureInfo.CurrentCulture);var text=new StringBuilder(name+Environment.NewLine+path+Environment.NewLine+app.L("Logical Bytes")+": "+bytes+Environment.NewLine+status);
            if(result.Scan is { } scan)
            {
                text.AppendLine();text.Append(string.Format(app.L("Logical bytes: {0:N0} · counted files: {1} · skipped items/subtrees: {2}"),scan.Root.Bytes,scan.Root.Files,scan.Root.Skipped));if(scan.EntryLimit)text.Append(" · ").Append(app.L("Entry limit reached"));if(scan.TimeLimit)text.Append(" · ").Append(app.L("Time limit reached"));
            }
            text.AppendLine();text.Append(app.L("Result Recorded")).Append(": ").Append(result.Recorded.ToString("g",CultureInfo.CurrentCulture));string detail=text.ToString();rows.Add(new(result,name,bytes,status,detail));report.AppendLine();report.AppendLine(detail);
        }
        CurrentReport=report.ToString();var view=new GridView();foreach(var column in new[]{("Folder","Name",180d),("Logical Bytes","Bytes",170d),("Status","Status",520d)})view.Columns.Add(new GridViewColumn{Header=app.L(column.Item1),DisplayMemberBinding=new Binding(column.Item2),Width=column.Item3});list.View=view;list.ItemsSource=rows;VirtualizingPanel.SetIsVirtualizing(list,true);VirtualizingPanel.SetVirtualizationMode(list,VirtualizationMode.Recycling);ScrollViewer.SetCanContentScroll(list,true);AutomationProperties.SetName(list,Title);body.Children.Add(list);body.Children.Add(details);
        list.SelectionChanged+=(_,_)=>{var row=list.SelectedItem as Row;details.Text=row?.Detail??"";map.IsEnabled=row?.Result.Scan is not null&&!token.IsCancellationRequested;};
        SourceInitialized+=(_,_)=>WebRouteLayout.Fit(this,Owner??this);DpiChanged+=(_,_)=>WebRouteLayout.Fit(this,this);
    }
    private sealed record Row(CommonFolderResult Result,string Name,string Bytes,string Status,string Detail);
}
