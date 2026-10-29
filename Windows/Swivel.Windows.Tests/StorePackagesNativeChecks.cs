using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Swivel.Core;
using Swivel.Windows;

internal static class StorePackagesNativeChecks
{
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        reject(()=>StorePackagesReader.Read(new CancellationToken(true)),"Precancelled package read never opens the package manager");
        check(StorePackagesReader.Unavailable(new COMException())&&StorePackagesReader.Unavailable(new UnauthorizedAccessException())&&!StorePackagesReader.Unavailable(new NullReferenceException())&&!StorePackagesReader.Unavailable(new OperationCanceledException()),"Only expected package-manager failures become unavailable");
        var live=StorePackagesReader.Read(CancellationToken.None);
        Console.WriteLine("Store packages (current user): "+live.Records.Count+" · "+StorePackages.Status(live.Issues,x=>x));
        check(live.Issues.HasFlag(PackageListIssue.Unavailable)?live.Records.Count==0:live.Records.Count>0,"Portable process either lists the current user's packages or reports the package manager unavailable");
        if(live.Records.Count>0)
        {
            check(live.Records.All(x=>x.Ordinal>=0&&x.Ordinal<StorePackages.MaxPackages),"Live packages keep bounded enumeration positions");
            check(live.Records.Any(x=>x.Version is not null&&x.Architecture is not null&&x.Family.State==PackageValueState.Present),"Live packages report version, architecture and family name");
            check(live.Records.Any(x=>x.Framework==true),"Live list includes framework packages (for example VCLibs) as reported");
        }
        var app=Application.Current as App??new App();app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
        var snapshot=new StorePackagesSnapshot(DateTimeOffset.UtcNow,new[]{new StorePackageRecord(0,new("Sample‮App",PackageValueState.Present),new("Pub",PackageValueState.Present),new(1,2,3,4),9,false,false,false,true,null,new("private-family_xyz",PackageValueState.Present),false)},PackageListIssue.None);
        var dialog=new StorePackagesDialog(app,snapshot);
        try
        {
            var nodes=Descendants(dialog).ToArray();var list=nodes.OfType<ListView>().Single();var family=nodes.OfType<CheckBox>().Single();var detail=nodes.OfType<TextBox>().Single();
            check(VirtualizingPanel.GetIsVirtualizing(list)&&list.SelectionMode==SelectionMode.Single&&AutomationProperties.GetName(list)==app.L("Packages"),"Package list virtualized, single selection, named");
            check(!dialog.IncludeFamily&&dialog.SelectedReport is null&&!dialog.CurrentReport.Contains("private-family_xyz")&&list.Items.Cast<object>().All(x=>!x.ToString()!.Contains("private-family_xyz")),"Family names hidden by default in report and rows; no implicit selected copy");
            list.SelectedIndex=0;check(!detail.Text.Contains("private-family_xyz")&&detail.Text.Contains("Sample[U+202E]App")&&dialog.SelectedReport is string selected&&selected.Contains(app.L(StorePackages.Limits)),"Selected details escape text and carry limits");
            family.IsChecked=true;check(detail.Text.Contains("private-family_xyz")&&dialog.CurrentReport.Contains("private-family_xyz"),"Checkbox explicitly includes family names");
            family.IsChecked=false;check(!detail.Text.Contains("private-family_xyz"),"Unchecking hides family names again");
            var footer=((DockPanel)dialog.Content).Children.OfType<WrapPanel>().Single();
            check(footer.Children.OfType<Button>().Select(x=>x.Content as string).SequenceEqual(new[]{app.L("Copy Report"),app.L("Copy Selected Details"),app.L("Close")}),"Package footer offers only explicit copies and close: no remove, Store or settings action");
        }
        finally{dialog.Close();}
        var empty=new StorePackagesDialog(app,new(DateTimeOffset.UtcNow,Array.Empty<StorePackageRecord>(),PackageListIssue.Unavailable));
        try{check(Descendants(empty).OfType<TextBlock>().Any(x=>x.Text==StorePackages.Status(PackageListIssue.Unavailable,app.L)),"Unavailable package manager shown as a status in the window");}
        finally{empty.Close();}
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {foreach(var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()){yield return child;foreach(var descendant in Descendants(child))yield return descendant;}}
}
