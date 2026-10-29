using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class AppMemoryDialog : Window
{
    internal const string LiveNotice="Refresh Every 2 Seconds reads the same process metadata again about every 2 seconds while this window is visible and not minimized, and adds a CPU column. It pauses while the pointer is over a list, while any app is ticked and during a quit request. Closing waits for any read in progress. Nothing is saved or sent.";
    private readonly App app;
    private AppMemorySnapshot snapshot;
    private AppMemorySnapshot? previous;
    private TimeSpan cpuElapsed;
    private TimeSpan? lastRead;
    private readonly Func<CancellationToken,AppMemorySnapshot> read;
    private readonly CancellationTokenSource cancellation;
    private readonly CancellationTokenRegistration registration;
    private readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromSeconds(2)};
    private readonly Stopwatch clock=Stopwatch.StartNew();
    private readonly CheckBox paths=new(){Margin=new Thickness(0,8,0,12)},helpers=new(){Margin=new Thickness(0,4,0,4)},live=new(){Margin=new Thickness(0,4,0,4)};
    private readonly TabItem groupTab=new();
    private readonly TabControl tabs=new();
    private readonly ListView groups=NativeList(),processes=NativeList();
    private readonly TextBox groupDetails=Details(),processDetails=Details();
    private readonly TextBox outcomes=new(){IsReadOnly=true,TextWrapping=TextWrapping.Wrap,MinHeight=80,MaxHeight=220,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Visibility=Visibility.Collapsed,Margin=new Thickness(0,8,0,0)};
    private readonly TextBlock status,counts,subtotal,liveStatus;
    private readonly GridViewColumn cpuColumn;
    private readonly Button copySelected,quit;
    private Task pending=Task.CompletedTask,reading=Task.CompletedTask;
    private Task? finishing;
    private Window? confirmation;
    private bool closed,quitting;
    private long readVersion;
    private readonly uint self=(uint)Environment.ProcessId,session;
    private readonly string windowsFolder;
    internal bool IncludePaths=>paths.IsChecked==true;
    internal bool GroupHelpers=>helpers.IsChecked==true&&snapshot.HelperGroups is not null;
    internal bool Live=>live.IsChecked==true;
    /// True once any close request was sent from this window.
    internal bool Asked{get;private set;}
    internal AppMemorySnapshot Snapshot=>snapshot;
    internal int Ticked=>Rows.Count(x=>x.Ticked);
    private IEnumerable<GroupRow> Rows=>groups.ItemsSource as GroupRow[]??Array.Empty<GroupRow>();
    private IReadOnlyList<AppMemoryGroup> CurrentGroups=>AppMemory.GroupsFor(snapshot,GroupHelpers);
    internal string CurrentReport=>AppMemory.Report(snapshot,IncludePaths,app.L,default,GroupHelpers);
    internal string? SelectedReport
    {
        get
        {
            string? details=tabs.SelectedIndex==0?SelectedGroup():SelectedProcess();
            return details is null?null:AppMemory.Header(snapshot,IncludePaths,app.L,GroupHelpers)+Environment.NewLine+details;
        }
    }
    internal AppMemoryDialog(App app,AppMemorySnapshot snapshot,CancellationToken token=default,Func<CancellationToken,AppMemorySnapshot>? read=null,uint? session=null,string? windowsFolder=null)
    {
        this.app=app;this.snapshot=snapshot;this.read=read??AppMemoryReader.Read;this.session=session??AppQuitReader.Session;this.windowsFolder=windowsFolder??AppQuitReader.WindowsFolder;
        cancellation=CancellationTokenSource.CreateLinkedTokenSource(token);
        Title=app.L("App Memory Snapshot");Width=1000;Height=920;MinWidth=560;MinHeight=440;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(20)};var footer=new WrapPanel();DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        footer.Children.Add(UI.Button(app.L("Copy Report"),()=>app.Try(()=>Clipboard.SetText(CurrentReport))));
        copySelected=UI.Button(app.L("Copy Selected Details"),()=>app.Try(()=>{if(SelectedReport is string text)Clipboard.SetText(text);}));copySelected.IsEnabled=false;footer.Children.Add(copySelected);
        quit=UI.Button(app.L(AppQuit.Title),()=>{});quit.IsEnabled=false;quit.Click+=(_,_)=>{if(!closed&&!quitting&&pending.IsCompleted)pending=QuitAsync();};footer.Children.Add(quit);
        var close=UI.Button(app.L("Close"),()=>DialogResult=false);close.IsCancel=true;footer.Children.Add(close);
        var body=new StackPanel();root.Children.Add(UI.Scroll(body));Content=root;
        body.Children.Add(UI.Text(snapshot.RecordedUtc.ToString("O",CultureInfo.InvariantCulture),12));
        status=UI.Text("",14);counts=UI.Text("",14);subtotal=UI.Text("",14);body.Children.Add(status);body.Children.Add(counts);body.Children.Add(subtotal);
        foreach(string notice in new[]{AppMemory.Scope,AppMemory.Grouping,AppMemory.Measurement,AppMemory.Privacy})body.Children.Add(UI.Text(app.L(notice),12));
        if(snapshot.Processes.Count==0)body.Children.Add(UI.Text(app.L("No process information was returned."),14));
        body.Children.Add(UI.Text(app.L("Values in MB use 1,000,000 bytes; selected details and copies retain exact bytes."),12));
        paths.Content=app.L("Show Full Executable Paths");AutomationProperties.SetName(paths,app.L("Show Full Executable Paths"));body.Children.Add(paths);
        body.Children.Add(UI.Text(app.L("Names in the lists may be shortened. Select a row for its complete visible name and recorded details. Full paths appear only in selected details when enabled."),12));
        cpuColumn=new GridViewColumn{Header=app.L("CPU (% of One Processor)"),DisplayMemberBinding=new Binding("Cpu"),Width=180};
        var view=Columns(("Executable Name","Name",260),("Processes","Count",90),("Known Working Set Subtotal (MB)","Bytes",230),("Unavailable Working Sets","Missing",160));
        view.Columns.Insert(0,TickColumn());groups.View=view;
        groups.AddHandler(ToggleButton.CheckedEvent,new RoutedEventHandler(Tick));groups.AddHandler(ToggleButton.UncheckedEvent,new RoutedEventHandler(Tick));
        helpers.Content=app.L("Group Helper Processes With the App That Started Them");AutomationProperties.SetName(helpers,app.L("Group Helper Processes With the App That Started Them"));
        helpers.IsEnabled=snapshot.HelperGroups is not null;body.Children.Add(helpers);
        body.Children.Add(UI.Text(AppMemory.ParentStatus(snapshot,app.L),12));body.Children.Add(UI.Text(app.L(AppMemory.HelperGrouping),12));
        live.Content=app.L("Refresh Every 2 Seconds");AutomationProperties.SetName(live,app.L("Refresh Every 2 Seconds"));body.Children.Add(live);
        liveStatus=UI.Text(app.L("Live refresh is off. The list shows the recorded snapshot."),12);body.Children.Add(liveStatus);
        body.Children.Add(UI.Text(app.L(LiveNotice),12));body.Children.Add(UI.Text(app.L(AppCpu.Notice),12));body.Children.Add(UI.Text(app.L(AppQuit.Scope),12));
        FillGroups();
        processes.View=Columns(("Executable Name","Name",230),("Process Identifiers","Pid",110),("Working Set (MB)","Bytes",160),("Query Issues","Issues",400));
        FillProcesses();FillHeader();
        AutomationProperties.SetName(processes,app.L("Recorded Processes"));
        AutomationProperties.SetName(groupDetails,app.L("Selected Group Details"));AutomationProperties.SetName(processDetails,app.L("Selected Process Details"));
        AutomationProperties.SetName(outcomes,app.L("Quit Request Results"));
        var groupBody=new StackPanel();groupBody.Children.Add(groups);groupBody.Children.Add(UI.Text(app.L("Selected Group Details"),13));groupBody.Children.Add(groupDetails);
        var processBody=new StackPanel();processBody.Children.Add(processes);processBody.Children.Add(UI.Text(app.L("Selected Process Details"),13));processBody.Children.Add(processDetails);
        groupTab.Content=groupBody;tabs.Items.Add(groupTab);tabs.Items.Add(new TabItem{Header=app.L("Recorded Processes"),Content=processBody});tabs.SelectedIndex=0;body.Children.Add(tabs);
        body.Children.Add(outcomes);
        groups.SelectionChanged+=(_,_)=>RefreshDetails();processes.SelectionChanged+=(_,_)=>RefreshDetails();tabs.SelectionChanged+=(_,e)=>{if(ReferenceEquals(e.Source,tabs))RefreshDetails();};
        paths.Checked+=(_,_)=>RefreshDetails();paths.Unchecked+=(_,_)=>RefreshDetails();
        helpers.Checked+=(_,_)=>{FillGroups();RefreshDetails();};helpers.Unchecked+=(_,_)=>{FillGroups();RefreshDetails();};RefreshDetails();
        live.Checked+=(_,_)=>SetLive(true);live.Unchecked+=(_,_)=>SetLive(false);
        timer.Tick+=TimerTick;
        Closed+=(_,_)=>Stop();
        registration=cancellation.Token.Register(()=>Dispatcher.BeginInvoke(new Action(()=>{if(!closed){confirmation?.Close();Close();}})));
        SourceInitialized+=(_,_)=>FitWorkArea(Owner??this);DpiChanged+=(_,_)=>FitWorkArea(this);
    }
    private void FillHeader()
    {
        status.Text=AppMemory.Status(snapshot,app.L);
        counts.Text=string.Format(app.L("Recorded processes: {0} · groups: {1} · unavailable working sets: {2}"),snapshot.Processes.Count,snapshot.Groups.Count,snapshot.Unmeasured);
        subtotal.Text=app.L("Known Working Set Subtotal (MB)")+": "+Megabytes(snapshot.KnownBytes,snapshot.Unmeasured==snapshot.Processes.Count);
    }
    private GridViewColumn TickColumn()
    {
        var box=new FrameworkElementFactory(typeof(CheckBox));
        box.SetBinding(ToggleButton.IsCheckedProperty,new Binding(nameof(GroupRow.Ticked)){Mode=BindingMode.OneWay});
        box.SetBinding(IsEnabledProperty,new Binding(nameof(GroupRow.Offered)){Mode=BindingMode.OneWay});
        box.SetBinding(AutomationProperties.NameProperty,new Binding(nameof(GroupRow.TickName)){Mode=BindingMode.OneWay});
        return new GridViewColumn{Header=app.L("Quit"),CellTemplate=new DataTemplate{VisualTree=box},Width=60};
    }
    private void Tick(object sender,RoutedEventArgs args)
    {
        if(args.OriginalSource is CheckBox box&&box.DataContext is GroupRow row&&SetTick(row,box.IsChecked==true)!=(box.IsChecked==true))box.IsChecked=row.Ticked;
    }
    private bool SetTick(GroupRow row,bool value)
    {
        // Ticks only count on rows that are still offered and current.
        row.Ticked=value&&row.Offered&&!quitting&&!closed&&ReferenceEquals(row.Source,CurrentGroups);
        UpdateQuit();UpdateLiveStatus();return row.Ticked;
    }
    internal IReadOnlyList<bool> OfferedRows=>Rows.Select(x=>x.Offered).ToArray();
    internal IReadOnlyList<string> CpuTexts=>Rows.Select(x=>x.Cpu).ToArray();
    internal bool QuitEnabled=>quit.IsEnabled;
    internal bool CpuColumnShown=>((GridView)groups.View).Columns.Contains(cpuColumn);
    /// The same path as clicking a row's tick box.
    internal bool Tick(int index,bool value)=>SetTick(Rows.ElementAt(index),value);
    private void UpdateQuit()=>quit.IsEnabled=!closed&&!quitting&&Ticked>0;
    private void FillGroups()
    {
        // A new grouping or snapshot replaces the rows: no selection or tick survives it.
        string title=app.L(GroupHelpers?"Helper Process Groups":"Executable Path Groups");groupTab.Header=title;AutomationProperties.SetName(groups,title);
        string? selected=(groups.SelectedItem as GroupRow)?.Key;groups.SelectedItem=null;var current=CurrentGroups;
        groups.ItemsSource=current.Select((g,i)=>
        {
            string name=ListName(g.Processes[0]);var (cpu,_)=AppCpu.Group(g,previous,cpuElapsed);
            return new GroupRow(i,current,Key(g),name,g.Count.ToString(CultureInfo.CurrentCulture),Megabytes(g.KnownBytes,g.Unmeasured==g.Count),g.Unmeasured.ToString(CultureInfo.CurrentCulture),
                cpu is double value?value.ToString("N1",CultureInfo.CurrentCulture):app.L("Unknown"),AppQuit.Eligibility(g,self,session,windowsFolder)==AppQuitBlock.None,string.Format(app.L("Quit {0}"),name));
        }).ToArray();
        if(selected is not null)groups.SelectedItem=Rows.FirstOrDefault(x=>x.Key==selected);
        UpdateQuit();
    }
    private void FillProcesses()
    {
        uint? selected=(processes.SelectedItem as ProcessRow)?.Pid;processes.SelectedItem=null;
        processes.ItemsSource=snapshot.Processes.Select((p,i)=>new ProcessRow(i,p.Pid,ListName(p),p.Pid.ToString(CultureInfo.CurrentCulture),Megabytes(p.WorkingSet.HasValue?(decimal)p.WorkingSet.Value:0,!p.WorkingSet.HasValue),AppMemory.Issues(p.Issues,app.L))).ToArray();
        if(selected is not null)processes.SelectedItem=(processes.ItemsSource as ProcessRow[])!.FirstOrDefault(x=>x.Pid==selected);
    }
    private static string Key(AppMemoryGroup group)=>group.Path is null?"pid:"+group.Processes[0].Pid.ToString(CultureInfo.InvariantCulture):"path:"+group.Path;
    private string? SelectedGroup()=>groups.SelectedItem is GroupRow row&&ReferenceEquals(row.Source,CurrentGroups)?AppMemory.Describe(row.Source[row.Index],IncludePaths,app.L):null;
    private string? SelectedProcess()=>processes.SelectedItem is ProcessRow row&&row.Index<snapshot.Processes.Count&&snapshot.Processes[row.Index].Pid==row.Pid?AppMemory.Describe(snapshot.Processes[row.Index],IncludePaths,app.L):null;
    private void RefreshDetails()
    {
        // Refresh both tabs, including inactive text, whenever privacy changes.
        groupDetails.Text=SelectedGroup()??app.L("Select a group to inspect its recorded subtotal and process identifiers.");
        processDetails.Text=SelectedProcess()??app.L("Select a process to inspect its recorded values and query issues.");
        copySelected.IsEnabled=SelectedReport is not null;
    }
    // Live refresh.
    private bool Paused(out string reason)
    {
        reason=quitting?"Live refresh is paused during the quit request.":Ticked>0?"Live refresh is paused while apps are ticked."
            :groups.IsMouseOver||processes.IsMouseOver?"Live refresh is paused while the pointer is over a list."
            :!IsVisible||WindowState==WindowState.Minimized?"Live refresh is paused while this window is hidden or minimized.":"";
        return reason.Length>0;
    }
    private void UpdateLiveStatus()
    {
        if(!Live){liveStatus.Text=app.L("Live refresh is off. The list shows the recorded snapshot.");return;}
        liveStatus.Text=Paused(out string reason)?app.L(reason):string.Format(app.L("Live refresh is on. Last updated {0}."),snapshot.RecordedUtc.ToLocalTime().ToString("T",CultureInfo.CurrentCulture));
    }
    private void SetLive(bool on)
    {
        readVersion++;previous=null;lastRead=null;
        var columns=((GridView)groups.View).Columns;
        if(on){if(!columns.Contains(cpuColumn))columns.Add(cpuColumn);timer.Start();StartRead();}
        else{timer.Stop();columns.Remove(cpuColumn);FillGroups();}
        UpdateLiveStatus();
    }
    private void TimerTick(object? sender,EventArgs args){UpdateLiveStatus();StartRead();}
    private void StartRead()
    {
        if(closed||!Live||!reading.IsCompleted||Paused(out _)||cancellation.IsCancellationRequested)return;
        reading=ReadAsync(readVersion);
    }
    private async Task ReadAsync(long version)
    {
        var token=cancellation.Token;var started=clock.Elapsed;
        try
        {
            var next=await Task.Run(()=>read(token),token);
            // A tick, hover, quit or switch that arrived during the read discards it.
            if(closed||version!=readVersion||!Live||Paused(out _))return;
            Apply(next,started);
        }
        catch(OperationCanceledException){}
        catch(Exception error)when(error is not OutOfMemoryException)
        {if(!closed)liveStatus.Text=app.L("Live refresh could not read process metadata. The last list is kept.");return;}
        if(!closed)UpdateLiveStatus();
    }
    /// Replaces the list with a newer snapshot read at `started` (time since this window opened).
    internal void Apply(AppMemorySnapshot next,TimeSpan started)
    {
        ArgumentNullException.ThrowIfNull(next);
        // The first live reading is the CPU baseline: the recorded snapshot has no known read time.
        previous=lastRead is TimeSpan before&&started>before?snapshot:null;cpuElapsed=previous is null?TimeSpan.Zero:started-lastRead!.Value;lastRead=started;
        snapshot=next;helpers.IsEnabled=snapshot.HelperGroups is not null&&!quitting;
        FillGroups();FillProcesses();FillHeader();RefreshDetails();
    }
    // Quit.
    private async Task QuitAsync()
    {
        var chosen=Rows.Where(x=>x.Ticked&&x.Offered&&ReferenceEquals(x.Source,CurrentGroups)).Select(x=>x.Source[x.Index]).ToArray();
        if(chosen.Length==0)return;
        var token=cancellation.Token;quitting=true;readVersion++;quit.IsEnabled=false;groups.IsEnabled=false;helpers.IsEnabled=false;live.IsEnabled=false;
        outcomes.Visibility=Visibility.Collapsed;outcomes.Text="";UpdateLiveStatus();
        CloseWindowsSession? windows=null;
        try
        {
            liveStatus.Text=app.L("Reading listed desktop windows…");
            windows=await AppQuitReader.ReadWindowsAsync(token);token.ThrowIfCancellationRequested();
            var plan=AppQuitReader.Plan(chosen,windows,token);string details=AppQuit.Details(plan,windows.Windows,app.L);
            if(plan.Windows.Count==0){Show(app.L(AppQuit.Nothing)+Environment.NewLine+details);return;}
            var confirm=new AppQuitConfirmDialog(app,details){Owner=this};confirmation=confirm;bool accepted;
            try{accepted=confirm.ShowDialog()==true;}finally{confirmation=null;}
            if(!accepted||closed||token.IsCancellationRequested){if(!closed)Show(app.L("No close request was sent."));return;}
            liveStatus.Text=app.L("Asking the selected apps to quit, then waiting briefly…");
            Asked=true;
            var outcome=await AppQuitReader.RunAsync(windows,plan,new AppQuitReader.Probe(),token);
            if(!closed)Show(CloseWindows.Summary(outcome.Results,app.L)+Environment.NewLine+CloseWindows.ResultLines(outcome.Windows.Select(i=>windows.Windows[i]).ToArray(),outcome.Results,app.L)
                +Environment.NewLine+AppQuit.Outcome(outcome.Targets,outcome.States,app.L));
        }
        catch(OperationCanceledException){}
        catch(Exception error)when(error is not OutOfMemoryException){if(!closed)app.ShowError(error);}
        finally
        {
            // The requests have returned (RunAsync awaits the worker), so the handles can be released.
            try{windows?.Dispose();}catch(Exception error)when(error is AggregateException or InvalidOperationException){}
            quitting=false;
            if(!closed){groups.IsEnabled=true;live.IsEnabled=true;helpers.IsEnabled=snapshot.HelperGroups is not null;FillGroups();UpdateLiveStatus();StartRead();}
        }
    }
    private void Show(string text){outcomes.Text=text;outcomes.Visibility=Visibility.Visible;}
    private void Stop()
    {
        if(closed)return;closed=true;readVersion++;timer.Stop();timer.Tick-=TimerTick;cancellation.Cancel();confirmation?.Close();quit.IsEnabled=false;
    }
    /// Cancels any read or quit request and waits for it, so native handles are released before the operation ends.
    internal Task FinishAsync()=>finishing??=FinishCoreAsync();
    private async Task FinishCoreAsync()
    {
        Stop();
        try{await pending;await reading;}
        finally{registration.Dispose();cancellation.Dispose();}
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
    private sealed class GroupRow(int index,IReadOnlyList<AppMemoryGroup> source,string key,string name,string count,string bytes,string missing,string cpu,bool offered,string tickName)
    {
        public int Index=>index;public IReadOnlyList<AppMemoryGroup> Source=>source;public string Key=>key;
        public string Name=>name;public string Count=>count;public string Bytes=>bytes;public string Missing=>missing;public string Cpu=>cpu;
        public bool Offered=>offered;public string TickName=>tickName;public bool Ticked{get;set;}
        public override string ToString()=>Name+" · "+Count+" · "+Bytes+" · "+Missing;
    }
    private sealed record ProcessRow(int Index,uint Pid,string Name,string PidText,string Bytes,string Issues)
    {public override string ToString()=>Name+" · "+PidText+" · "+Bytes+" · "+Issues;}
    private void FitWorkArea(Window reference)
    {
        var area=System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(reference).Handle).WorkingArea;var dpi=VisualTreeHelper.GetDpi(reference);
        double height=Math.Max(1,area.Height/dpi.DpiScaleY-16),width=Math.Max(1,area.Width/dpi.DpiScaleX-16);
        MinHeight=Math.Min(440,height);MinWidth=Math.Min(560,width);MaxHeight=height;MaxWidth=width;Height=Math.Min(Height,height);Width=Math.Min(Width,width);
    }
}

/// The final review before asking ticked apps to quit. Enter and Escape cancel.
internal sealed class AppQuitConfirmDialog : Window
{
    internal AppQuitConfirmDialog(App app,string details)
    {
        Title=app.L("Confirm Quit Requests");Width=700;Height=600;MinWidth=380;MinHeight=320;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(20)};
        var buttons=new WrapPanel();DockPanel.SetDock(buttons,Dock.Bottom);root.Children.Add(buttons);
        var cancel=UI.Button(app.L("Cancel"),()=>DialogResult=false);cancel.IsCancel=true;cancel.IsDefault=true;buttons.Children.Add(cancel);
        buttons.Children.Add(UI.Button(app.L("Ask Apps to Quit"),()=>DialogResult=true));
        var body=new StackPanel();body.Children.Add(UI.Text(app.L(AppQuit.Confirmation)));
        var text=new TextBox{Text=details,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,MinHeight=150,MaxHeight=340};
        AutomationProperties.SetName(text,app.L("Windows to Ask"));body.Children.Add(text);
        root.Children.Add(UI.Scroll(body));Content=root;
        SourceInitialized+=(_,_)=>WebRouteLayout.Fit(this,Owner??this);DpiChanged+=(_,_)=>WebRouteLayout.Fit(this,this);
    }
}
