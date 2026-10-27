using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class InstalledAppsDialog:Window
{
    private readonly App app;
    private readonly InstalledAppsSnapshot snapshot;
    private readonly TabControl tabs=new();
    private readonly ListView records=NativeList(300),roots=NativeList(160);
    private readonly CheckBox keys=new(){Margin=new Thickness(0,8,0,12)};
    private readonly TextBox details=new(){IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.NoWrap,Height=180,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto};
    private readonly Button copySelected;
    internal bool IncludeKeys=>keys.IsChecked==true;
    /// Set when the window closed to look for leftovers of apps removed since it was read.
    internal bool LeftoversRequested{get;private set;}
    internal string CurrentReport=>InstalledApps.Report(snapshot,IncludeKeys,app.L,default);
    internal string? SelectedReport=>tabs.SelectedIndex==0&&records.SelectedItem is RecordRow selected?InstalledApps.Header(snapshot,IncludeKeys,app.L)+Environment.NewLine+
        string.Join(Environment.NewLine,snapshot.Roots.Select(r=>InstalledApps.Origin(r.Source,app.L)+": "+r.Recorded.ToString(CultureInfo.InvariantCulture)+" · "+InstalledApps.RootStatus(r.Issues,app.L)))+Environment.NewLine+InstalledApps.Describe(snapshot.Records[selected.Index],IncludeKeys,app.L):null;
    internal InstalledAppsDialog(App app,InstalledAppsSnapshot snapshot)
    {
        this.app=app;this.snapshot=snapshot;Title=app.L("Desktop Installation Records");Width=1000;Height=900;MinWidth=560;MinHeight=440;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(20)};var footer=new WrapPanel();DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        // Windows Settings does the uninstalling; Swivel never runs an uninstall command.
        footer.Children.Add(UI.Button(app.L("Open Installed Apps in Settings"),()=>app.Try(()=>WindowsSettings.Open(WindowsSettingsPage.Apps))));
        footer.Children.Add(UI.Button(app.L("Find Leftovers of Apps Removed Since This List Was Read…"),()=>{LeftoversRequested=true;DialogResult=true;}));
        footer.Children.Add(UI.Button(app.L("Copy Report"),()=>app.Try(()=>Clipboard.SetText(CurrentReport))));
        copySelected=UI.Button(app.L("Copy Selected Details"),()=>app.Try(()=>{if(SelectedReport is string text)Clipboard.SetText(text);}));copySelected.IsEnabled=false;footer.Children.Add(copySelected);
        var close=UI.Button(app.L("Close"),()=>DialogResult=false);close.IsCancel=true;footer.Children.Add(close);
        var body=new StackPanel();root.Children.Add(UI.Scroll(body));Content=root;
        body.Children.Add(UI.Text(snapshot.RecordedUtc.ToString("O",CultureInfo.InvariantCulture),12));
        body.Children.Add(UI.Text(app.L(snapshot.Partial?"Installation-record snapshot is partial.":"All available registry views were enumerated within the limits."),14));
        body.Children.Add(UI.Text(string.Format(app.L("Recorded registry entries: {0} (not a count of unique installed apps)"),snapshot.Records.Count),14));
        foreach(string notice in new[]{InstalledApps.Scope,InstalledApps.Limits,InstalledApps.Privacy,InstalledApps.Uninstall})body.Children.Add(UI.Text(app.L(notice),12));
        keys.Content=app.L("Show Registry Subkey Names");AutomationProperties.SetName(keys,app.L("Show Registry Subkey Names"));body.Children.Add(keys);
        body.Children.Add(UI.Text(app.L("List text may be shortened. Select a record for complete reported text and field status. Subkey names are never used as a fallback app name."),12));
        records.View=Columns(("Reported App Name","Name",250),("Reported Version","Version",140),("Reported Publisher","Publisher",250),("Registry View","Source",240));
        records.ItemsSource=snapshot.Records.Select((x,i)=>new RecordRow(i,Short(x.Name),Short(x.Version),Short(x.Publisher),InstalledApps.Origin(x.Source,app.L))).ToArray();
        roots.View=Columns(("Registry View","Source",250),("Recorded Entries","Count",110),("Registry View Status","Status",550));
        roots.ItemsSource=snapshot.Roots.Select(x=>new RootRow(InstalledApps.Origin(x.Source,app.L),x.Recorded.ToString(CultureInfo.CurrentCulture),InstalledApps.RootStatus(x.Issues,app.L))).ToArray();
        AutomationProperties.SetName(records,app.L("Installation Records"));AutomationProperties.SetName(roots,app.L("Registry View Status"));AutomationProperties.SetName(details,app.L("Selected Installation Record"));
        var recordBody=new StackPanel();recordBody.Children.Add(records);recordBody.Children.Add(UI.Text(app.L("Selected Installation Record"),13));recordBody.Children.Add(details);
        tabs.Items.Add(new TabItem{Header=app.L("Installation Records"),Content=recordBody});tabs.Items.Add(new TabItem{Header=app.L("Registry View Status"),Content=roots});tabs.SelectedIndex=0;body.Children.Add(tabs);
        if(snapshot.Records.Count==0)body.Children.Add(UI.Text(app.L("No installation records were returned. This does not prove that no apps are installed."),14));
        void Refresh(){details.Text=records.SelectedItem is RecordRow row?InstalledApps.Describe(snapshot.Records[row.Index],IncludeKeys,app.L):app.L("Select a recorded entry to inspect its reported text and query status.");copySelected.IsEnabled=SelectedReport is not null;}
        records.SelectionChanged+=(_,_)=>Refresh();tabs.SelectionChanged+=(_,e)=>{if(ReferenceEquals(e.Source,tabs))Refresh();};keys.Checked+=(_,_)=>Refresh();keys.Unchecked+=(_,_)=>Refresh();Refresh();
        SourceInitialized+=(_,_)=>FitWorkArea(Owner??this);DpiChanged+=(_,_)=>FitWorkArea(this);
    }
    private string Short(RegistrationText text)
    {
        if(text.State!=RegistrationValueState.Present)return InstalledApps.Value(text,app.L);
        string value=text.Value!;int length=Math.Min(128,value.Length);if(length<value.Length&&char.IsHighSurrogate(value[length-1]))length--;
        return AppMemory.Visible(value[..length])+(length<value.Length?"…":"");
    }
    private GridView Columns(params(string Key,string Property,double Width)[] columns)
    {var view=new GridView();foreach(var c in columns)view.Columns.Add(new GridViewColumn{Header=app.L(c.Key),DisplayMemberBinding=new Binding(c.Property),Width=c.Width});return view;}
    private static ListView NativeList(double height)
    {var list=new ListView{Height=height,SelectionMode=SelectionMode.Single};VirtualizingPanel.SetIsVirtualizing(list,true);VirtualizingPanel.SetVirtualizationMode(list,VirtualizationMode.Recycling);ScrollViewer.SetCanContentScroll(list,true);return list;}
    private sealed record RecordRow(int Index,string Name,string Version,string Publisher,string Source)
    {public override string ToString()=>Name+" · "+Version+" · "+Publisher+" · "+Source;}
    private sealed record RootRow(string Source,string Count,string Status)
    {public override string ToString()=>Source+" · "+Count+" · "+Status;}
    private void FitWorkArea(Window reference)
    {
        var area=System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(reference).Handle).WorkingArea;var dpi=VisualTreeHelper.GetDpi(reference);
        double height=Math.Max(1,area.Height/dpi.DpiScaleY-16),width=Math.Max(1,area.Width/dpi.DpiScaleX-16);
        MinHeight=Math.Min(440,height);MinWidth=Math.Min(560,width);MaxHeight=height;MaxWidth=width;Height=Math.Min(Height,height);Width=Math.Min(Width,width);
    }
}
