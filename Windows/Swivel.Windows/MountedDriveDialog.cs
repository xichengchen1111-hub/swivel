using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class MountedDriveDialog:Window
{
    internal string CurrentReport{get;}="";
    internal MountedDriveDialog(App app,MountedDriveSnapshot snapshot)
    {
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
        foreach(string note in new[]{MountedDrives.Scope,MountedDrives.Limits}){body.Children.Add(UI.Text(app.L(note),12));report.AppendLine();report.AppendLine(app.L(note));}
        CurrentReport=report.ToString();SourceInitialized+=(_,_)=>WebRouteLayout.Fit(this,Owner??this);DpiChanged+=(_,_)=>WebRouteLayout.Fit(this,this);
    }
    private sealed record Row(string Root,string Type,string FileSystem,string ReadOnly,string Available,string Total,string Free,string Flags,string Issues);
}
