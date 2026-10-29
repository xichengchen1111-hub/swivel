using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class StartupAppsDialog:Window
{
    private readonly App app;private readonly StartupAppsResult result;private readonly ListView list;private readonly TextBox details;private readonly Button copySelected;
    internal string CurrentReport=>StartupApps.Report(result,null,app.L,default);
    internal string? SelectedReport=>list.SelectedItem is Row row?StartupApps.Report(result,row.Record,app.L,default):null;
    internal StartupAppsDialog(App app,StartupAppsResult result)
    {
        this.app=app;this.result=result;Title=app.L("Startup Apps");Width=1080;Height=860;MinWidth=560;MinHeight=460;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(20)};var footer=new WrapPanel();DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        footer.Children.Add(UI.Button(app.L("Copy Report"),()=>app.Try(()=>Clipboard.SetText(CurrentReport))));
        copySelected=UI.Button(app.L("Copy Selected Row"),()=>app.Try(()=>{if(SelectedReport is string text)Clipboard.SetText(text);}));copySelected.IsEnabled=false;footer.Children.Add(copySelected);
        var close=UI.Button(app.L("Close"),()=>DialogResult=false);close.IsCancel=true;footer.Children.Add(close);
        var body=new StackPanel();root.Children.Add(UI.Scroll(body));Content=root;
        body.Children.Add(UI.Text(string.Format(app.L("Listed rows: {0} (not a count of unique or running apps)"),result.Rows.Count),14));
        foreach(string text in new[]{StartupApps.Scope,StartupApps.Meaning,StartupApps.Privacy})body.Children.Add(UI.Text(app.L(text),12));
        body.Children.Add(UI.Text(app.L(result.FoldersInspected?"Startup-folder items were included as you chose.":"Startup folders were not inspected."),12));
        list=List(360);var view=new GridView();
        foreach(var col in new[]{("Name","Name",240d),("Approval state","State",190d),("Recorded disabled time (UTC)","Time",170d),("Kind","Kind",170d),("Location","Where",280d)})view.Columns.Add(new GridViewColumn{Header=app.L(col.Item1),DisplayMemberBinding=new Binding(col.Item2),Width=col.Item3});
        list.View=view;list.ItemsSource=result.Rows.Select(r=>new Row(app,r)).ToArray();AutomationProperties.SetName(list,app.L("Startup Apps"));body.Children.Add(list);
        if(result.Rows.Count==0)body.Children.Add(UI.Text(app.L("No startup items or StartupApproved values were recorded. This does not prove that no apps start at logon."),14));
        details=new TextBox{IsReadOnly=true,TextWrapping=TextWrapping.Wrap,MaxHeight=180,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};AutomationProperties.SetName(details,app.L("Selected Row"));body.Children.Add(details);
        list.SelectionChanged+=(_,_)=>{copySelected.IsEnabled=list.SelectedItem is Row;details.Text=list.SelectedItem is Row row?StartupApps.Describe(row.Record,app.L):"";};
        body.Children.Add(UI.Text(app.L("Sources"),13));var sources=List(200);var sourceView=new GridView();
        foreach(var col in new[]{("Location","Where",360d),("Status","Status",560d)})sourceView.Columns.Add(new GridViewColumn{Header=app.L(col.Item1),DisplayMemberBinding=new Binding(col.Item2),Width=col.Item3});
        sources.View=sourceView;sources.ItemsSource=result.Sources;AutomationProperties.SetName(sources,app.L("Sources"));body.Children.Add(sources);
        body.Children.Add(UI.Text(app.L("If this action is saved in My Wheel, older Windows versions may reject those settings and fall back to defaults. Keep a pre-upgrade settings backup before downgrading."),12));
        SourceInitialized+=(_,_)=>WebRouteLayout.Fit(this,Owner??this);DpiChanged+=(_,_)=>WebRouteLayout.Fit(this,this);
    }
    private static ListView List(int height){var list=new ListView{Height=height,SelectionMode=SelectionMode.Single};VirtualizingPanel.SetIsVirtualizing(list,true);VirtualizingPanel.SetVirtualizationMode(list,VirtualizationMode.Recycling);ScrollViewer.SetCanContentScroll(list,true);return list;}
    private sealed record Row(App Owner,StartupAppRow Record)
    {
        public string Name=>Short(StartupApps.Name(Record,Owner.L));public string State=>StartupApps.State(Record.Meaning.State,Owner.L);public string Time=>StartupApps.Time(Record,Owner.L);
        public string Kind=>StartupApps.Kind(Record.Kind,Owner.L);public string Where=>Record.Where;public override string ToString()=>Name+" · "+State+" · "+Kind+" · "+Where;
        private static string Short(string value){int n=Math.Min(128,value.Length);if(n<value.Length&&char.IsHighSurrogate(value[n-1]))n--;return value[..n]+(n<value.Length?"…":"");}
    }
}
