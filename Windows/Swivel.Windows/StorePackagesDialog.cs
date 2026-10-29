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

internal sealed class StorePackagesDialog:Window
{
    private readonly App app;
    private readonly StorePackagesSnapshot snapshot;
    private readonly ListView packages=new(){Height=360,SelectionMode=SelectionMode.Single};
    private readonly CheckBox family=new(){Margin=new Thickness(0,8,0,12)};
    private readonly TextBox details=new(){IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.NoWrap,Height=200,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto};
    private readonly Button copySelected;
    internal bool IncludeFamily=>family.IsChecked==true;
    internal string CurrentReport=>StorePackages.Report(snapshot,IncludeFamily,app.L,default);
    internal string? SelectedReport=>packages.SelectedItem is Row selected?StorePackages.Header(snapshot,IncludeFamily,app.L)+Environment.NewLine+StorePackages.Describe(snapshot.Records[selected.Index],IncludeFamily,app.L):null;
    internal StorePackagesDialog(App app,StorePackagesSnapshot snapshot)
    {
        this.app=app;this.snapshot=snapshot;Title=app.L("Store and MSIX Packages (Current User)");Width=1100;Height=900;MinWidth=560;MinHeight=440;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(20)};var footer=new WrapPanel();DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        footer.Children.Add(UI.Button(app.L("Copy Report"),()=>app.Try(()=>Clipboard.SetText(CurrentReport))));
        copySelected=UI.Button(app.L("Copy Selected Details"),()=>app.Try(()=>{if(SelectedReport is string text)Clipboard.SetText(text);}));copySelected.IsEnabled=false;footer.Children.Add(copySelected);
        var close=UI.Button(app.L("Close"),()=>DialogResult=false);close.IsCancel=true;footer.Children.Add(close);
        var body=new StackPanel();root.Children.Add(UI.Scroll(body));Content=root;
        body.Children.Add(UI.Text(snapshot.RecordedUtc.ToString("O",CultureInfo.InvariantCulture),12));
        body.Children.Add(UI.Text(app.L(snapshot.Partial?"Package snapshot is partial.":"The current user's package list was read within the limits."),14));
        body.Children.Add(UI.Text(string.Format(app.L("Recorded packages: {0} (includes frameworks and resource packages)"),snapshot.Records.Count),14));
        body.Children.Add(UI.Text(StorePackages.Status(snapshot.Issues,app.L),14));
        foreach(string notice in new[]{StorePackages.Scope,StorePackages.Limits,StorePackages.Privacy})body.Children.Add(UI.Text(app.L(notice),12));
        family.Content=app.L("Show Package Family Names");AutomationProperties.SetName(family,app.L("Show Package Family Names"));body.Children.Add(family);
        body.Children.Add(UI.Text(app.L("List text may be shortened. Select a package for complete reported text. Package family names are never used as a fallback name."),12));
        var view=new GridView();
        foreach(var(key,property,width)in new[]{("Reported Package Name","Name",260.0),("Package Version","Version",130.0),("Reported Publisher","Publisher",200.0),("Architecture","Architecture",100.0),("Package Kind","Kind",190.0),("Installed","Installed",150.0)})
            view.Columns.Add(new GridViewColumn{Header=app.L(key),DisplayMemberBinding=new Binding(property),Width=width});
        packages.View=view;VirtualizingPanel.SetIsVirtualizing(packages,true);VirtualizingPanel.SetVirtualizationMode(packages,VirtualizationMode.Recycling);ScrollViewer.SetCanContentScroll(packages,true);
        packages.ItemsSource=snapshot.Records.Select((x,i)=>new Row(i,Short(x.Name),StorePackages.Version(x.Version,app.L),Short(x.Publisher),StorePackages.Architecture(x.Architecture,app.L),StorePackages.Kind(x,app.L),StorePackages.Installed(x.Installed,app.L))).ToArray();
        AutomationProperties.SetName(packages,app.L("Packages"));AutomationProperties.SetName(details,app.L("Selected Package"));
        body.Children.Add(packages);body.Children.Add(UI.Text(app.L("Selected Package"),13));body.Children.Add(details);
        if(snapshot.Records.Count==0)body.Children.Add(UI.Text(app.L("No packages were returned. This does not prove that no Store or MSIX apps are installed."),14));
        void Refresh(){details.Text=packages.SelectedItem is Row row?StorePackages.Describe(snapshot.Records[row.Index],IncludeFamily,app.L):app.L("Select a package to inspect its reported fields.");copySelected.IsEnabled=SelectedReport is not null;}
        packages.SelectionChanged+=(_,_)=>Refresh();family.Checked+=(_,_)=>Refresh();family.Unchecked+=(_,_)=>Refresh();Refresh();
        SourceInitialized+=(_,_)=>FitWorkArea(Owner??this);DpiChanged+=(_,_)=>FitWorkArea(this);
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
