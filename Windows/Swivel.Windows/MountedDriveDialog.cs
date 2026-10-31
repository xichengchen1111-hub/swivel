using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class MountedDriveDialog:Window
{
    internal string CurrentReport{get;private set;}="";
    private readonly App app;
    private readonly CancellationTokenSource cancellation;
    private readonly IDriveEjectSource source;
    private readonly TextBlock outcome=new(){TextWrapping=TextWrapping.Wrap};
    private readonly WrapPanel ejectButtons=new();
    private Task pending=Task.CompletedTask;
    private Task? finishing;
    private Window? confirmation;
    private bool closed;
    private readonly CancellationTokenRegistration registration;
    internal MountedDriveDialog(App app,MountedDriveSnapshot snapshot,CancellationToken token=default,IDriveEjectSource? source=null)
    {
        this.app=app;this.source=source??new DriveDeviceReader();cancellation=CancellationTokenSource.CreateLinkedTokenSource(token);
        registration=cancellation.Token.Register(()=>Dispatcher.BeginInvoke(()=>{confirmation?.Close();}));
        Closed+=(_,_)=>Stop();
        Title=app.L("Mounted Drives");Width=1000;Height=820;MinWidth=500;MinHeight=400;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(20)};var footer=new WrapPanel();DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        footer.Children.Add(UI.Button(app.L("Copy Report"),()=>app.Try(()=>Clipboard.SetText(CurrentReport))));var close=UI.Button(app.L("Close"),()=>DialogResult=false);close.IsCancel=true;footer.Children.Add(close);
        var body=new StackPanel();root.Children.Add(UI.Scroll(body));Content=root;
        body.Children.Add(UI.Text(app.L("Drive metadata captured at")+" "+snapshot.Captured.ToString("g",CultureInfo.CurrentCulture),14));
        var report=new StringBuilder();report.AppendLine(app.L("Windows Doctor")+" · "+Title);report.AppendLine(snapshot.Captured.ToString("g",CultureInfo.CurrentCulture));
        foreach(var issue in snapshot.Issues)
        {
            string stage=app.L(issue.Stage switch{DriveIssueStage.ErrorModeSetup=>"Drive Check Setup Unavailable",DriveIssueStage.Enumeration=>"Drive Letter Enumeration Unavailable",_=>"Drive Check Thread Cleanup Failed"});
            string line=stage+" · "+app.L("Windows Error Code")+" "+issue.Error.ToString(CultureInfo.InvariantCulture);body.Children.Add(UI.Text(line));report.AppendLine(line);
        }
        if(snapshot.UnknownMaskBits){body.Children.Add(UI.Text(app.L("Unknown drive-mask bits were skipped (partial).")));report.AppendLine(app.L("Unknown drive-mask bits were skipped (partial)."));}
        string Number(ulong? value)=>value?.ToString("N0",CultureInfo.CurrentCulture)??app.L("Unavailable");
        string Failure(uint? error)=>error is uint code?app.L("Windows Error Code")+" "+code.ToString(CultureInfo.InvariantCulture):"";
        var rows=snapshot.Drives.Select(d=>new Row(d.Root,app.L(MountedDrives.TypeName(d.Type))+" ("+d.Type.ToString(CultureInfo.InvariantCulture)+")",d.FileSystem is null?app.L("Unavailable"):DoctorFacts.VisibleBounded(d.FileSystem,64),
            d.DeclaredReadOnly is null?app.L("Unavailable"):app.L(d.DeclaredReadOnly.Value?"Declared Read-only":"Read-only Not Declared (Write Access Untested)"),
            Number(d.Space?.AvailableToCaller),Number(d.Space?.TotalForCaller),Number(d.Space?.TotalFree),d.FileSystemFlags is uint f?"0x"+f.ToString("X8",CultureInfo.InvariantCulture):app.L("Unavailable"),
            MountedDrives.QueryMetadata(d.Type)?string.Join(" · ",new[]{d.VolumeError is null?"":app.L("Filesystem Query")+": "+Failure(d.VolumeError),d.SpaceError is null?"":app.L("Capacity Query")+": "+Failure(d.SpaceError)}.Where(x=>x.Length>0)):app.L("Filesystem and capacity queries skipped for this drive type."))).ToArray();
        var columns=new[]{("Drive Letter","Root",100d),("Drive Type","Type",240d),("File System","FileSystem",150d),("Read-only Declaration","ReadOnly",280d),("Available to Caller (bytes)","Available",220d),("Total for Caller (bytes)","Total",220d),("Volume Total Free (bytes)","Free",220d),("Raw Filesystem Flags","Flags",190d),("Read Issues","Issues",350d)};
        string Detail(Row row)=>string.Join(Environment.NewLine,new[]{row.Root,app.L("Drive Type")+": "+row.Type,app.L("File System")+": "+row.FileSystem,app.L("Read-only Declaration")+": "+row.ReadOnly,app.L("Available to Caller (bytes)")+": "+row.Available,app.L("Total for Caller (bytes)")+": "+row.Total,app.L("Volume Total Free (bytes)")+": "+row.Free,app.L("Raw Filesystem Flags")+": "+row.Flags,app.L("Read Issues")+": "+row.Issues});
        var list=new ListView{Height=260,ItemsSource=rows,SelectionMode=SelectionMode.Single};var grid=new GridView();foreach(var c in columns)grid.Columns.Add(new GridViewColumn{Header=app.L(c.Item1),DisplayMemberBinding=new Binding(c.Item2),Width=c.Item3});list.View=grid;
        VirtualizingPanel.SetIsVirtualizing(list,true);VirtualizingPanel.SetVirtualizationMode(list,VirtualizationMode.Recycling);ScrollViewer.SetCanContentScroll(list,true);AutomationProperties.SetName(list,app.L("Mounted Drives"));body.Children.Add(list);
        var detail=new TextBox{IsReadOnly=true,TextWrapping=TextWrapping.Wrap,Height=190,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};AutomationProperties.SetName(detail,app.L("Selected Drive Details"));list.SelectionChanged+=(_,_)=>detail.Text=list.SelectedItem is Row row?Detail(row):"";body.Children.Add(detail);
        if(rows.Length==0){string empty=app.L("No drive letters were recorded. Review any reported issue; this does not prove no disks are connected.");body.Children.Add(UI.Text(empty));report.AppendLine(empty);}
        foreach(var row in rows){report.AppendLine();report.AppendLine(Detail(row));}
        body.Children.Add(ejectButtons);body.Children.Add(outcome);
        foreach(var drive in snapshot.Drives)
        {
            string label=DoctorFacts.Visible(drive.Label??app.L("Unavailable"));
            string hardware=string.Join(" · ",new[]{drive.Root,label,app.L(DriveEject.BusName(drive.Hardware?.Bus)),
                app.L("Vendor")+": "+DoctorFacts.Visible(drive.Hardware?.Vendor??app.L("Unavailable")),app.L("Product")+": "+DoctorFacts.Visible(drive.Hardware?.Product??app.L("Unavailable")),
                app.L("Removable")+": "+app.L(drive.Hardware?.Removable is bool r?r?"Yes":"No":"Unavailable"),
                app.L("Device Write Protection")+": "+app.L(drive.Hardware?.Writable is bool w?w?"Device reports writable (permissions untested).":"Device reports write protected.":"Unavailable")});
            body.Children.Add(UI.Text(hardware));report.AppendLine(hardware);
            if(drive.HardwareIssue is string issue){body.Children.Add(UI.Text(app.L(issue),12));report.AppendLine(app.L(issue));}
            if(drive.Hardware?.WriteError is uint error){string writeIssue=app.L("Windows Error Code")+" "+error.ToString(CultureInfo.InvariantCulture);body.Children.Add(UI.Text(writeIssue,12));report.AppendLine(writeIssue);}
            var eject=UI.Button(string.Format(app.L("Eject {0} ({1})…"),drive.Root,label),()=>StartEject(drive));eject.IsEnabled=DriveEject.Eligible(drive);ejectButtons.Children.Add(eject);
        }
        body.Children.Add(UI.Text(app.L("Eject is disabled for system disks and when device identity or safe-removal eligibility cannot be verified."),12));
        foreach(string note in new[]{MountedDrives.Scope,MountedDrives.Limits}){body.Children.Add(UI.Text(app.L(note),12));report.AppendLine();report.AppendLine(app.L(note));}
        CurrentReport=report.ToString();SourceInitialized+=(_,_)=>WebRouteLayout.Fit(this,Owner??this);DpiChanged+=(_,_)=>WebRouteLayout.Fit(this,this);
    }
    private void StartEject(MountedDrive drive)
    {
        if(closed||!pending.IsCompleted||cancellation.IsCancellationRequested)return;
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);pending=done.Task;ejectButtons.IsEnabled=false;
        _=RunEjectAsync(drive,done);
    }
    private async Task RunEjectAsync(MountedDrive drive,TaskCompletionSource done)
    {
        try
        {
            var result=await DriveEject.RunAsync(drive,source,Confirm,cancellation.Token);
            if(closed||cancellation.IsCancellationRequested)return;
            string message=app.L(result.Status switch{DriveEjectStatus.Cancelled=>"No eject request was sent.",DriveEjectStatus.Ineligible=>"This drive is not eligible for eject.",DriveEjectStatus.Changed=>"The drive identity changed. Close this window and read the drive list again.",DriveEjectStatus.Gone=>"The volume is gone after the eject request.",DriveEjectStatus.StillPresent=>"Windows accepted the request, but removal could not be confirmed. Do not unplug the drive yet.",_=>DriveEject.Veto(result.Veto)});
            if(result.VetoName.Length>0)message+=" "+app.L("Vetoing Device")+": "+DoctorFacts.Visible(result.VetoName);
            outcome.Text=message;CurrentReport+=Environment.NewLine+drive.Root+" · "+message;
        }
        catch(OperationCanceledException){}
        catch(Exception error)when(error is not OutOfMemoryException){if(!closed&&!cancellation.IsCancellationRequested)app.ShowError(error);}
        finally{if(!closed&&!cancellation.IsCancellationRequested)ejectButtons.IsEnabled=true;done.SetResult();}
    }
    private Task<bool> Confirm(MountedDrive drive)
    {
        cancellation.Token.ThrowIfCancellationRequested();
        var dialog=new Window{Title=app.L("Confirm Eject"),Owner=this,Width=540,Height=310,WindowStartupLocation=WindowStartupLocation.CenterOwner};confirmation=dialog;
        var panel=new StackPanel{Margin=new Thickness(20)};dialog.Content=panel;
        panel.Children.Add(UI.Text(string.Format(app.L("Eject {0} ({1})? Other volumes on this device may also disconnect. Windows can refuse if a program is using it."),drive.Root,DoctorFacts.Visible(drive.Label??app.L("Unavailable")))));
        var buttons=new WrapPanel();panel.Children.Add(buttons);buttons.Children.Add(UI.Button(app.L("Eject"),()=>dialog.DialogResult=true));
        var cancel=UI.Button(app.L("Cancel"),()=>dialog.DialogResult=false);cancel.IsDefault=true;cancel.IsCancel=true;buttons.Children.Add(cancel);
        dialog.SourceInitialized+=(_,_)=>WebRouteLayout.Fit(dialog,this);dialog.DpiChanged+=(_,_)=>WebRouteLayout.Fit(dialog,dialog);
        try{return Task.FromResult(dialog.ShowDialog()==true&&!cancellation.IsCancellationRequested);}finally{confirmation=null;}
    }
    private void Stop(){if(closed)return;closed=true;cancellation.Cancel();confirmation?.Close();ejectButtons.IsEnabled=false;}
    internal Task FinishAsync()=>finishing??=FinishCoreAsync();
    private async Task FinishCoreAsync(){Stop();try{await pending;}finally{registration.Dispose();cancellation.Dispose();}}
    private sealed record Row(string Root,string Type,string FileSystem,string ReadOnly,string Available,string Total,string Free,string Flags,string Issues);
}
