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

internal sealed class AppMemoryDialog : Window
{
    private readonly App app;
    private readonly AppMemorySnapshot snapshot;
    private readonly CheckBox paths=new(){Margin=new Thickness(0,8,0,12)},helpers=new(){Margin=new Thickness(0,4,0,4)};
    private readonly TabItem groupTab=new();
    private readonly TabControl tabs=new();
    private readonly ListView groups=NativeList(),processes=NativeList();
    private readonly TextBox groupDetails=Details(),processDetails=Details();
    private readonly Button copySelected;
    internal bool IncludePaths=>paths.IsChecked==true;
    internal bool GroupHelpers=>helpers.IsChecked==true&&snapshot.HelperGroups is not null;
    private System.Collections.Generic.IReadOnlyList<AppMemoryGroup> CurrentGroups=>AppMemory.GroupsFor(snapshot,GroupHelpers);
    internal string CurrentReport=>AppMemory.Report(snapshot,IncludePaths,app.L,default,GroupHelpers);
    internal string? SelectedReport
    {
        get
        {
            string? details=tabs.SelectedIndex==0?SelectedGroup():SelectedProcess();
            return details is null?null:AppMemory.Header(snapshot,IncludePaths,app.L,GroupHelpers)+Environment.NewLine+details;
        }
    }
    internal AppMemoryDialog(App app,AppMemorySnapshot snapshot)
    {
        this.app=app;this.snapshot=snapshot;Title=app.L("App Memory Snapshot");Width=1000;Height=920;MinWidth=560;MinHeight=440;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(20)};var footer=new WrapPanel();DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        footer.Children.Add(UI.Button(app.L("Copy Report"),()=>app.Try(()=>Clipboard.SetText(CurrentReport))));
        copySelected=UI.Button(app.L("Copy Selected Details"),()=>app.Try(()=>{if(SelectedReport is string text)Clipboard.SetText(text);}));copySelected.IsEnabled=false;footer.Children.Add(copySelected);
        var close=UI.Button(app.L("Close"),()=>DialogResult=false);close.IsCancel=true;footer.Children.Add(close);
        var body=new StackPanel();root.Children.Add(UI.Scroll(body));Content=root;
        body.Children.Add(UI.Text(snapshot.RecordedUtc.ToString("O",CultureInfo.InvariantCulture),12));body.Children.Add(UI.Text(AppMemory.Status(snapshot,app.L),14));
        body.Children.Add(UI.Text(string.Format(app.L("Recorded processes: {0} · groups: {1} · unavailable working sets: {2}"),snapshot.Processes.Count,snapshot.Groups.Count,snapshot.Unmeasured),14));
        body.Children.Add(UI.Text(app.L("Known Working Set Subtotal (MB)")+": "+Megabytes(snapshot.KnownBytes,snapshot.Unmeasured==snapshot.Processes.Count),14));
        foreach(string notice in new[]{AppMemory.Scope,AppMemory.Grouping,AppMemory.Measurement,AppMemory.Privacy})body.Children.Add(UI.Text(app.L(notice),12));
        if(snapshot.Processes.Count==0)body.Children.Add(UI.Text(app.L("No process information was returned."),14));
        body.Children.Add(UI.Text(app.L("Values in MB use 1,000,000 bytes; selected details and copies retain exact bytes."),12));
        paths.Content=app.L("Show Full Executable Paths");AutomationProperties.SetName(paths,app.L("Show Full Executable Paths"));body.Children.Add(paths);
        body.Children.Add(UI.Text(app.L("Names in the lists may be shortened. Select a row for its complete visible name and recorded details. Full paths appear only in selected details when enabled."),12));
        groups.View=Columns(("Executable Name","Name",260),("Processes","Count",90),("Known Working Set Subtotal (MB)","Bytes",230),("Unavailable Working Sets","Missing",160));
        helpers.Content=app.L("Group Helper Processes With the App That Started Them");AutomationProperties.SetName(helpers,app.L("Group Helper Processes With the App That Started Them"));
        helpers.IsEnabled=snapshot.HelperGroups is not null;body.Children.Add(helpers);
        body.Children.Add(UI.Text(AppMemory.ParentStatus(snapshot,app.L),12));body.Children.Add(UI.Text(app.L(AppMemory.HelperGrouping),12));
        FillGroups();
        processes.View=Columns(("Executable Name","Name",230),("Process Identifiers","Pid",110),("Working Set (MB)","Bytes",160),("Query Issues","Issues",400));
        processes.ItemsSource=snapshot.Processes.Select((p,i)=>new ProcessRow(i,ListName(p),p.Pid.ToString(CultureInfo.CurrentCulture),Megabytes(p.WorkingSet.HasValue?(decimal)p.WorkingSet.Value:0,!p.WorkingSet.HasValue),AppMemory.Issues(p.Issues,app.L))).ToArray();
        AutomationProperties.SetName(processes,app.L("Recorded Processes"));
        AutomationProperties.SetName(groupDetails,app.L("Selected Group Details"));AutomationProperties.SetName(processDetails,app.L("Selected Process Details"));
        var groupBody=new StackPanel();groupBody.Children.Add(groups);groupBody.Children.Add(UI.Text(app.L("Selected Group Details"),13));groupBody.Children.Add(groupDetails);
        var processBody=new StackPanel();processBody.Children.Add(processes);processBody.Children.Add(UI.Text(app.L("Selected Process Details"),13));processBody.Children.Add(processDetails);
        groupTab.Content=groupBody;tabs.Items.Add(groupTab);tabs.Items.Add(new TabItem{Header=app.L("Recorded Processes"),Content=processBody});tabs.SelectedIndex=0;body.Children.Add(tabs);
        groups.SelectionChanged+=(_,_)=>RefreshDetails();processes.SelectionChanged+=(_,_)=>RefreshDetails();tabs.SelectionChanged+=(_,e)=>{if(ReferenceEquals(e.Source,tabs))RefreshDetails();};
        paths.Checked+=(_,_)=>RefreshDetails();paths.Unchecked+=(_,_)=>RefreshDetails();
        helpers.Checked+=(_,_)=>{FillGroups();RefreshDetails();};helpers.Unchecked+=(_,_)=>{FillGroups();RefreshDetails();};RefreshDetails();
        SourceInitialized+=(_,_)=>FitWorkArea(Owner??this);DpiChanged+=(_,_)=>FitWorkArea(this);
    }
    private void FillGroups()
    {
        // A new grouping replaces the rows, so no selection from the other grouping survives.
        string title=app.L(GroupHelpers?"Helper Process Groups":"Executable Path Groups");groupTab.Header=title;AutomationProperties.SetName(groups,title);
        groups.SelectedItem=null;var current=CurrentGroups;
        groups.ItemsSource=current.Select((g,i)=>new GroupRow(i,current,ListName(g.Processes[0]),g.Count.ToString(CultureInfo.CurrentCulture),Megabytes(g.KnownBytes,g.Unmeasured==g.Count),g.Unmeasured.ToString(CultureInfo.CurrentCulture))).ToArray();
    }
    private string? SelectedGroup()=>groups.SelectedItem is GroupRow row&&ReferenceEquals(row.Source,CurrentGroups)?AppMemory.Describe(row.Source[row.Index],IncludePaths,app.L):null;
    private string? SelectedProcess()=>processes.SelectedItem is ProcessRow row?AppMemory.Describe(snapshot.Processes[row.Index],IncludePaths,app.L):null;
    private void RefreshDetails()
    {
        // Refresh both tabs, including inactive text, whenever privacy changes.
        groupDetails.Text=SelectedGroup()??app.L("Select a group to inspect its recorded subtotal and process identifiers.");
        processDetails.Text=SelectedProcess()??app.L("Select a process to inspect its recorded values and query issues.");
        copySelected.IsEnabled=SelectedReport is not null;
    }
    private string Megabytes(decimal value,bool unknown)=>unknown?app.L("Unknown"):(value/1_000_000m).ToString("N1",CultureInfo.CurrentCulture);
    private string ListName(AppMemoryProcess process)
    {
        if(process.Path is null)return AppMemory.Name(process,app.L);
        string name=process.Path[(process.Path.LastIndexOf('\\')+1)..];int length=Math.Min(128,name.Length);
        if(length<name.Length&&char.IsHighSurrogate(name[length-1]))length--;
        return AppMemory.Visible(name[..length])+(length<name.Length?"…":"");
    }
    private GridView Columns(params (string Key,string Property,double Width)[] columns)
    {
        var view=new GridView();foreach(var c in columns)view.Columns.Add(new GridViewColumn{Header=app.L(c.Key),DisplayMemberBinding=new Binding(c.Property),Width=c.Width});return view;
    }
    private static ListView NativeList()
    {
        var list=new ListView{Height=270,SelectionMode=SelectionMode.Single};VirtualizingPanel.SetIsVirtualizing(list,true);VirtualizingPanel.SetVirtualizationMode(list,VirtualizationMode.Recycling);ScrollViewer.SetCanContentScroll(list,true);return list;
    }
    private static TextBox Details()=>new(){IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.NoWrap,Height=150,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto};
    // UI Automation/text search must never fall back to a raw model record's
    // synthesized ToString, which would expose the hidden executable path.
    private sealed record GroupRow(int Index,System.Collections.Generic.IReadOnlyList<AppMemoryGroup> Source,string Name,string Count,string Bytes,string Missing)
    {public override string ToString()=>Name+" · "+Count+" · "+Bytes+" · "+Missing;}
    private sealed record ProcessRow(int Index,string Name,string Pid,string Bytes,string Issues)
    {public override string ToString()=>Name+" · "+Pid+" · "+Bytes+" · "+Issues;}
    private void FitWorkArea(Window reference)
    {
        var area=System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(reference).Handle).WorkingArea;var dpi=VisualTreeHelper.GetDpi(reference);
        double height=Math.Max(1,area.Height/dpi.DpiScaleY-16),width=Math.Max(1,area.Width/dpi.DpiScaleX-16);
        MinHeight=Math.Min(440,height);MinWidth=Math.Min(560,width);MaxHeight=height;MaxWidth=width;Height=Math.Min(Height,height);Width=Math.Min(Width,width);
    }
}
