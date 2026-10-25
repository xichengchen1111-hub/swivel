using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class StartupDialog:Window
{
    private readonly App app;private readonly StartupSnapshot snapshot;
    private readonly CheckBox commands=new(){Margin=new Thickness(0,8,0,8)};
    private readonly TabControl tabs=new();
    private readonly ListView records=List(280),roots=List(200);
    private readonly TextBox details=new(){IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.NoWrap,Height=180,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto};
    private readonly Button copySelected;
    internal bool IncludeCommands=>commands.IsChecked==true;
    internal string CurrentReport=>StartupRecords.Report(snapshot,IncludeCommands,app.L,default);
    internal string? SelectedReport=>tabs.SelectedIndex==0&&records.SelectedItem is RecordRow row?StartupRecords.Header(snapshot,IncludeCommands,app.L)+Environment.NewLine+
        string.Join(Environment.NewLine,snapshot.Roots.Select(r=>StartupRecords.Origin(r.Location,app.L)+": "+r.Recorded.ToString(CultureInfo.InvariantCulture)+" · "+StartupRecords.RootStatus(r.Issues,app.L)))+Environment.NewLine+StartupRecords.Describe(snapshot.Records[row.Index],IncludeCommands,app.L):null;
    internal StartupDialog(App app,StartupSnapshot snapshot)
    {
        this.app=app;this.snapshot=snapshot;Title=app.L("Logon Startup Records");Width=1000;Height=880;MinWidth=500;MinHeight=400;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(20)};var footer=new WrapPanel();DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        footer.Children.Add(UI.Button(app.L("Copy Report"),()=>app.Try(()=>Clipboard.SetText(CurrentReport))));copySelected=UI.Button(app.L("Copy Selected Details"),()=>app.Try(()=>{if(SelectedReport is string text)Clipboard.SetText(text);}));copySelected.IsEnabled=false;footer.Children.Add(copySelected);
        var close=UI.Button(app.L("Close"),()=>DialogResult=false);close.IsCancel=true;footer.Children.Add(close);
        var body=new StackPanel();root.Children.Add(UI.Scroll(body));Content=root;
        body.Children.Add(UI.Text(snapshot.RecordedUtc.ToString("O",CultureInfo.InvariantCulture),12));body.Children.Add(UI.Text(app.L(snapshot.Partial?"Startup-record snapshot is partial.":"All requested available startup locations were enumerated within the limits."),14));
        body.Children.Add(UI.Text(string.Format(app.L("Recorded startup positions: {0} (not a count of unique or enabled apps)"),snapshot.Records.Count),14));
        foreach(string note in new[]{StartupRecords.Scope,StartupRecords.Limits,StartupRecords.Privacy})body.Children.Add(UI.Text(app.L(note),12));
        commands.Content=app.L("Show Reported Commands");AutomationProperties.SetName(commands,app.L("Show Reported Commands"));body.Children.Add(commands);
        body.Children.Add(UI.Text(app.L("List names may be shortened. Select a position for the complete visible name, field status and optional command. Enumeration positions are not stable identifiers, and concurrent changes can cause missed or repeated values."),12));
        records.View=Columns(("Reported Startup Name","Name",280),("Registry View","Origin",300),("Registry Value Type","Type",150),("Status","Status",350));
        records.ItemsSource=snapshot.Records.Select((r,i)=>new RecordRow(i,Short(StartupRecords.Name(r,app.L)),StartupRecords.Origin(r.Location,app.L),StartupRecords.Type(r.Type,app.L),StartupRecords.EntryStatus(r,app.L))).ToArray();
        roots.View=Columns(("Registry View","Origin",300),("Recorded Entries","Count",110),("Registry View Status","Status",500));
        roots.ItemsSource=snapshot.Roots.Select(r=>new RootRow(StartupRecords.Origin(r.Location,app.L),r.Recorded.ToString(CultureInfo.CurrentCulture),StartupRecords.RootStatus(r.Issues,app.L))).ToArray();
        AutomationProperties.SetName(records,app.L("Logon Startup Records"));AutomationProperties.SetName(roots,app.L("Registry View Status"));AutomationProperties.SetName(details,app.L("Selected Startup Position"));
        var recordBody=new StackPanel();recordBody.Children.Add(records);recordBody.Children.Add(UI.Text(app.L("Selected Startup Position"),13));recordBody.Children.Add(details);
        tabs.Items.Add(new TabItem{Header=app.L("Logon Startup Records"),Content=recordBody});tabs.Items.Add(new TabItem{Header=app.L("Registry View Status"),Content=roots});tabs.SelectedIndex=0;body.Children.Add(tabs);
        if(snapshot.Records.Count==0)body.Children.Add(UI.Text(app.L("No startup records were returned. This does not prove that no apps start at logon."),14));
        body.Children.Add(UI.Text(app.L("If this action is saved in My Wheel, Windows 0.47 and earlier may reject those settings and fall back to defaults. Keep a pre-upgrade settings backup before downgrading."),12));
        void Refresh(){details.Text=records.SelectedItem is RecordRow row?StartupRecords.Describe(snapshot.Records[row.Index],IncludeCommands,app.L):app.L("Select a startup position to inspect its reported fields.");copySelected.IsEnabled=SelectedReport is not null;}
        records.SelectionChanged+=(_,_)=>Refresh();tabs.SelectionChanged+=(_,e)=>{if(ReferenceEquals(e.Source,tabs))Refresh();};commands.Checked+=(_,_)=>Refresh();commands.Unchecked+=(_,_)=>Refresh();Refresh();
        SourceInitialized+=(_,_)=>WebRouteLayout.Fit(this,Owner??this);DpiChanged+=(_,_)=>WebRouteLayout.Fit(this,this);
    }
    private static string Short(string value){int n=Math.Min(128,value.Length);if(n<value.Length&&char.IsHighSurrogate(value[n-1]))n--;return value[..n]+(n<value.Length?"…":"");}
    private GridView Columns(params(string Label,string Property,double Width)[] columns){var grid=new GridView();foreach(var c in columns)grid.Columns.Add(new GridViewColumn{Header=app.L(c.Label),DisplayMemberBinding=new Binding(c.Property),Width=c.Width});return grid;}
    private static ListView List(int height){var list=new ListView{Height=height,SelectionMode=SelectionMode.Single};VirtualizingPanel.SetIsVirtualizing(list,true);VirtualizingPanel.SetVirtualizationMode(list,VirtualizationMode.Recycling);ScrollViewer.SetCanContentScroll(list,true);return list;}
    private sealed record RecordRow(int Index,string Name,string Origin,string Type,string Status){public override string ToString()=>Name+" · "+Origin+" · "+Type+" · "+Status;}
    private sealed record RootRow(string Origin,string Count,string Status){public override string ToString()=>Origin+" · "+Count+" · "+Status;}
}
