using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using Swivel.Core;

namespace Swivel.Windows;

/// The folder Windows kept for an uninstalled package family under Local app data.
internal sealed record PackageData(string Family,string Path);

internal sealed class StorePackagesDialog:Window
{
    private readonly App app;
    private StorePackagesSnapshot snapshot;
    private readonly string? ownFamily;
    private readonly ListView packages=new(){Height=360,SelectionMode=SelectionMode.Single};
    private readonly CheckBox family=new(){Margin=new Thickness(0,8,0,12)};
    private readonly TextBox details=new(){IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.NoWrap,Height=200,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto};
    private readonly Button copySelected,uninstall,cancelUninstall;
    private readonly TextBlock removal=UI.Text("",13),removalStatus=UI.Text("",13),dataText=UI.Text("",13),countText=UI.Text("",14),statusText=UI.Text("",14),partialText=UI.Text("",14),recordedText=UI.Text("",12);
    private readonly RecycleBinSession<PackageData> bin;
    private readonly List<PackageData> data=new();
    private readonly List<string> uninstalled=new();
    private CancellationTokenSource? removing;
    private bool closeRequested;
    internal bool IncludeFamily=>family.IsChecked==true;
    internal bool Busy=>removing is not null;
    /// Packages this window's Uninstall… left out of the package list read afterwards.
    internal int Uninstalled=>uninstalled.Count;
    internal int DataMoved=>bin.Gone.Count;
    internal StorePackagesSnapshot Snapshot=>snapshot;
    internal string CurrentReport
    {
        get
        {
            string report=StorePackages.Report(snapshot,IncludeFamily,app.L,default);
            if(uninstalled.Count==0&&bin.Gone.Count==0)return report;
            var text=new StringBuilder(report);
            foreach(string line in uninstalled)text.AppendLine(string.Format(app.L("Uninstalled in this window: {0}"),line));
            bin.AppendReport(text,data);return text.ToString();
        }
    }
    internal string? SelectedReport=>packages.SelectedItem is Row selected?StorePackages.Header(snapshot,IncludeFamily,app.L)+Environment.NewLine+StorePackages.Describe(snapshot.Records[selected.Index],IncludeFamily,app.L):null;
    internal StorePackagesDialog(App app,StorePackagesSnapshot snapshot,string? ownFamily=null)
    {
        this.app=app;this.snapshot=snapshot;this.ownFamily=ownFamily;Title=app.L("Store and MSIX Packages (Current User)");Width=1100;Height=900;MinWidth=560;MinHeight=440;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(20)};var footer=new WrapPanel();DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        bin=new RecycleBinSession<PackageData>(app,this,new(
            item=>AppMemory.Visible(item.Path),item=>AppMemory.Visible(item.Path),
            (item,owner)=>NativeFiles.RecyclePackageData(item.Path,()=>{if(StorePackageRemover.FamilyInstalled(item.Family)!=false)throw new UserError(StorePackageRemoval.ReinstalledData);},owner),
            (item,stored)=>{if(StorePackageRemover.FamilyInstalled(item.Family)!=false)throw new UserError(StorePackageRemoval.ReinstalledRestore);NativeFiles.RestoreRecycled(item.Path,stored,true);},
            "Move the data folder of the uninstalled app to the Recycle Bin?","Move {0} data folders of uninstalled apps to the Recycle Bin?",
            "The folder can hold the app's settings, saved files or games. Check the path first. If it is too large for the Recycle Bin, Windows asks before deleting it permanently."),
            ()=>Busy?Array.Empty<PackageData>():data.Where(x=>!bin!.Gone.Contains(x)).ToArray(),Refresh);
        uninstall=UI.Button(app.L("Uninstall…"),()=>_=UninstallAsync());footer.Children.Add(uninstall);
        cancelUninstall=UI.Button(app.L("Cancel Uninstall"),()=>removing?.Cancel());cancelUninstall.Visibility=Visibility.Collapsed;footer.Children.Add(cancelUninstall);
        bin.Move.Visibility=Visibility.Collapsed;bin.Undo.Visibility=Visibility.Collapsed;footer.Children.Add(bin.Move);footer.Children.Add(bin.Undo);
        footer.Children.Add(UI.Button(app.L("Copy Report"),()=>app.Try(()=>Clipboard.SetText(CurrentReport))));
        copySelected=UI.Button(app.L("Copy Selected Details"),()=>app.Try(()=>{if(SelectedReport is string text)Clipboard.SetText(text);}));copySelected.IsEnabled=false;footer.Children.Add(copySelected);
        var close=UI.Button(app.L("Close"),()=>{if(Busy)removing!.Cancel();else if(bin.Idle)DialogResult=false;});close.IsCancel=true;footer.Children.Add(close);
        Closing+=(_,e)=>{if(Busy){closeRequested=true;removing!.Cancel();e.Cancel=true;}};
        var body=new StackPanel();root.Children.Add(UI.Scroll(body));Content=root;
        body.Children.Add(recordedText);body.Children.Add(partialText);body.Children.Add(countText);body.Children.Add(statusText);
        foreach(string notice in new[]{StorePackages.Scope,StorePackages.Limits,StorePackages.Privacy,StorePackageRemoval.Notice})body.Children.Add(UI.Text(app.L(notice),12));
        family.Content=app.L("Show Package Family Names");AutomationProperties.SetName(family,app.L("Show Package Family Names"));body.Children.Add(family);
        body.Children.Add(UI.Text(app.L("List text may be shortened. Select a package for complete reported text. Package family names are never used as a fallback name."),12));
        var view=new GridView();
        foreach(var(key,property,width)in new[]{("Reported Package Name","Name",260.0),("Package Version","Version",130.0),("Reported Publisher","Publisher",200.0),("Architecture","Architecture",100.0),("Package Kind","Kind",190.0),("Installed","Installed",150.0)})
            view.Columns.Add(new GridViewColumn{Header=app.L(key),DisplayMemberBinding=new Binding(property),Width=width});
        packages.View=view;VirtualizingPanel.SetIsVirtualizing(packages,true);VirtualizingPanel.SetVirtualizationMode(packages,VirtualizationMode.Recycling);ScrollViewer.SetCanContentScroll(packages,true);
        AutomationProperties.SetName(packages,app.L("Packages"));AutomationProperties.SetName(details,app.L("Selected Package"));
        AutomationProperties.SetName(removal,app.L("Uninstall…"));AutomationProperties.SetName(removalStatus,app.L("Uninstall Status"));
        body.Children.Add(packages);body.Children.Add(UI.Text(app.L("Selected Package"),13));body.Children.Add(details);body.Children.Add(removal);body.Children.Add(removalStatus);
        dataText.Visibility=Visibility.Collapsed;body.Children.Add(dataText);body.Children.Add(bin.Status);
        packages.SelectionChanged+=(_,_)=>Refresh();family.Checked+=(_,_)=>Refresh();family.Unchecked+=(_,_)=>Refresh();
        Show(snapshot);
        SourceInitialized+=(_,_)=>FitWorkArea(Owner??this);DpiChanged+=(_,_)=>FitWorkArea(this);
    }
    /// The offer for the selected package; null when nothing is selected.
    internal RemovalBlock? SelectedBlock=>packages.SelectedItem is Row row?StorePackageRemoval.Check(snapshot.Records[row.Index],snapshot,ownFamily):null;
    private TextBlock? empty;
    private void Show(StorePackagesSnapshot next)
    {
        snapshot=next;
        recordedText.Text=snapshot.RecordedUtc.ToString("O",CultureInfo.InvariantCulture);
        partialText.Text=app.L(snapshot.Partial?"Package snapshot is partial.":"The current user's package list was read within the limits.");
        countText.Text=string.Format(app.L("Recorded packages: {0} (includes frameworks and resource packages)"),snapshot.Records.Count);
        statusText.Text=StorePackages.Status(snapshot.Issues,app.L);
        packages.ItemsSource=snapshot.Records.Select((x,i)=>new Row(i,Short(x.Name),StorePackages.Version(x.Version,app.L),Short(x.Publisher),StorePackages.Architecture(x.Architecture,app.L),StorePackages.Kind(x,app.L),StorePackages.Installed(x.Installed,app.L))).ToArray();
        if(empty is not null){((Panel)empty.Parent).Children.Remove(empty);empty=null;}
        if(snapshot.Records.Count==0){empty=UI.Text(app.L("No packages were returned. This does not prove that no Store or MSIX apps are installed."),14);((Panel)packages.Parent).Children.Insert(((Panel)packages.Parent).Children.IndexOf(packages)+1,empty);}
        Refresh();
    }
    private void Refresh()
    {
        details.Text=packages.SelectedItem is Row row?StorePackages.Describe(snapshot.Records[row.Index],IncludeFamily,app.L):app.L("Select a package to inspect its reported fields.");copySelected.IsEnabled=SelectedReport is not null;
        var block=SelectedBlock;removal.Text=block is RemovalBlock b?StorePackageRemoval.Reason(b,app.L):"";
        uninstall.IsEnabled=!Busy&&bin.Idle&&block==RemovalBlock.None;packages.IsEnabled=!Busy;
        cancelUninstall.Visibility=Busy?Visibility.Visible:Visibility.Collapsed;
        bin.UpdateButtons();if(Busy)bin.Undo.IsEnabled=false;
    }
    private async Task UninstallAsync()
    {
        if(Busy||!bin.Idle||packages.SelectedItem is not Row row)return;
        var record=snapshot.Records[row.Index];
        if(StorePackageRemoval.Check(record,snapshot,ownFamily)!=RemovalBlock.None)return;
        if(MessageBox.Show(this,StorePackageRemoval.Confirmation(record,app.L),app.L("Uninstall…"),MessageBoxButton.OKCancel,MessageBoxImage.Warning,MessageBoxResult.Cancel)!=MessageBoxResult.OK||Busy||!bin.Idle||!IsVisible)return;
        var cancel=new CancellationTokenSource();removing=cancel;
        removalStatus.Text=app.L("Checking the package list again…");Refresh();
        StoreRemovalRun? run=null;string? failed=null;
        try{run=await StorePackageRemover.RemoveAsync(record,percent=>Dispatcher.BeginInvoke(()=>{if(removing==cancel)removalStatus.Text=StorePackageRemoval.Progress(percent,app.L);}),cancel.Token);}
        catch(OperationCanceledException){failed=app.L("Cancelled before removal started. Nothing was removed.");}
        // Before the system call nothing was changed; after it, RemoveAsync reports failures itself.
        catch(Exception error)when(error is not OutOfMemoryException){failed=app.L("The package list could not be read again. Nothing was removed.");}
        if(failed is not null){removing=null;cancel.Dispose();removalStatus.Text=failed;Refresh();if(closeRequested)Close();return;}
        // Windows' deployment service may still be deleting the family's data folder: look again
        // for up to about 3 seconds and offer it only if it is still there.
        string? kept=null;
        if(run is {Outcome:RemovalOutcome.Removed,After:{} done}&&StorePackageRemoval.FamilyGone(record.Family.Value!,done)
            &&StorePackageRemoval.DataFolder(StorePackageRemover.LocalAppData(),record.Family.Value!) is string path)
        {
            kept=path;
            for(int look=0;look<6&&kept is not null;look++){await Task.Delay(500);if(!System.IO.Directory.Exists(path))kept=null;}
        }
        removing=null;cancel.Dispose();
        removalStatus.Text=failed??Describe(run!,record,kept);
        if(run?.After is StorePackagesSnapshot after)Show(after);else Refresh();
        if(closeRequested)Close();
    }
    private string Describe(StoreRemovalRun run,StorePackageRecord record,string? path)
    {
        if(run.Recheck!=RemovalRecheck.Ready)return StorePackageRemoval.RecheckText(run.Recheck,run.Block,app.L);
        if(run.RunningRefused)return app.L(StorePackageRemoval.Running);
        var lines=new List<string>{StorePackageRemoval.OutcomeText(run.Outcome!.Value,app.L)};
        if(run.Error is int error)lines.Add(StorePackageRemoval.Error(error,run.ErrorText,app.L));
        if(run.CancelRequested)lines.Add(app.L("Cancellation was requested; Windows may have finished the removal anyway. The list above was read afterwards."));
        string familyName=record.Family.Value!;
        if(run.Outcome==RemovalOutcome.Removed)
        {
            uninstalled.Add(StorePackages.Value(record.Name,app.L)+" · "+AppMemory.Visible(record.FullName!.Value!));
            bool looked=StorePackageRemoval.FamilyGone(familyName,run.After!)&&StorePackageRemoval.DataFolder(StorePackageRemover.LocalAppData(),familyName) is not null;
            if(path is not null&&!data.Any(x=>string.Equals(x.Path,path,StringComparison.OrdinalIgnoreCase)))
            {
                data.Add(new(familyName,path));bin.Move.Visibility=Visibility.Visible;bin.Undo.Visibility=Visibility.Visible;dataText.Visibility=Visibility.Visible;
                dataText.Text=app.L(StorePackageRemoval.DataNotice)+Environment.NewLine+string.Join(Environment.NewLine,data.Select(x=>AppMemory.Visible(x.Path)));
                lines.Add(app.L("A data folder Windows kept for this app is still there; it is listed below."));
            }
            else lines.Add(app.L(!looked?"Leftover data was not looked for: another package of this family may still be installed, or the list or the Local app data path could not be read.":"No data folder for this app remains under Local app data (Packages)."));
        }
        return string.Join(Environment.NewLine,lines);
    }
    private string Short(PackageText text)
    {
        if(text.State!=PackageValueState.Present)return StorePackages.Value(text,app.L);
        string value=text.Value!;int length=Math.Min(128,value.Length);if(length<value.Length&&char.IsHighSurrogate(value[length-1]))length--;
        return AppMemory.Visible(value[..length])+(length<value.Length?"…":"");
    }
    private sealed record Row(int Index,string Name,string Version,string Publisher,string Architecture,string Kind,string Installed)
    {public override string ToString()=>Name+" · "+Version+" · "+Publisher+" · "+Architecture+" · "+Kind;}
    private void FitWorkArea(Window reference)
    {
        var area=System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(reference).Handle).WorkingArea;var dpi=VisualTreeHelper.GetDpi(reference);
        double height=Math.Max(1,area.Height/dpi.DpiScaleY-16),width=Math.Max(1,area.Width/dpi.DpiScaleX-16);
        MinHeight=Math.Min(440,height);MinWidth=Math.Min(560,width);MaxHeight=height;MaxWidth=width;Height=Math.Min(Height,height);Width=Math.Min(Width,width);
    }
}
