using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;
using Swivel.Windows;
using Windows.Management.Deployment;

internal static class StorePackageRemovalNativeChecks
{
    private const string ProbeName="SwivelTest.UninstallProbe";
    // Unsigned packages carry this publisher attribute (Windows 11 / Server 2022 and later).
    private const string ProbePublisher="CN=Swivel Test, OID.2.25.311729368913984317654407730594956997722=1";

    internal static void Run(string directory,Action<bool,string> check,Action<Action,string> reject)
    {
        // Eligibility on the runner's real packages; nothing is removed here.
        var live=StorePackagesReader.Read(CancellationToken.None);
        check(StorePackageRemover.OwnFamily() is null,"The unpackaged test host has no package family of its own");
        var blocks=live.Records.Select(x=>(Row:x,Block:StorePackageRemoval.Check(x,live,null))).ToArray();
        Console.WriteLine("Store package uninstall offers: "+string.Join(", ",blocks.GroupBy(x=>x.Block).OrderBy(g=>g.Key).Select(g=>g.Key+" "+g.Count())));
        Console.WriteLine("Store packages with full name/signature/status/dependencies read: "+live.Records.Count(x=>x.FullName?.State==PackageValueState.Present)+"/"+live.Records.Count(x=>x.Signature is not null)+"/"+live.Records.Count(x=>x.StatusOk is not null)+"/"+live.Records.Count(x=>x.DependsOn is not null)+" of "+live.Records.Count);
        check(live.Records.Where(x=>x.Bundle==false&&x.FullName?.State==PackageValueState.Present).All(x=>StorePackageRemoval.ValidFullName(x.FullName!.Value)&&StorePackageRemoval.Consistent(x)),"Every real full name read is valid and matches its family and version");
        check(blocks.Where(x=>x.Block==RemovalBlock.None).All(x=>x.Row.Framework==false&&x.Row.Resource==false&&x.Row.Bundle==false&&x.Row.Signature!=4&&x.Row.StatusOk==true),"No real framework, resource, bundle, system-signed or not-OK package is offered");
        check(blocks.Where(x=>x.Row.Framework==true).All(x=>x.Block!=RemovalBlock.None)&&blocks.Where(x=>x.Row.Signature==4).All(x=>x.Block!=RemovalBlock.None),"Real frameworks and system packages are blocked");
        var depended=new HashSet<string>(live.Records.Where(x=>x.Bundle!=true&&x.DependsOn is not null).SelectMany(x=>x.DependsOn!),StringComparer.OrdinalIgnoreCase);
        check(blocks.Where(x=>x.Block==RemovalBlock.None).All(x=>!depended.Contains(x.Row.FullName!.Value!)),"No real package another package depends on is offered");
        foreach(var x in blocks.Where(x=>x.Block==RemovalBlock.None))
        {
            var(state,_)=StorePackageRemoval.Recheck(x.Row,live,null);
            check(state==RemovalRecheck.Ready,"An offered real package rechecks ready against the same list");
        }
        foreach(var x in live.Records.Where(x=>x.Family.State==PackageValueState.Present).Take(5))
            check(StorePackageRemover.FamilyInstalled(x.Family.Value!)!=false,"An installed package's family is reported installed");
        check(StorePackageRemover.FamilyInstalled("Swivel.NotInstalled.Probe_8wekyb3d8bbwe")==false&&StorePackageRemover.FamilyInstalled("../x") is null,"A family that is not installed is reported missing; an invalid family is unknown");
        var look=StorePackageRemover.Look("Swivel.NotInstalled.Probe_8wekyb3d8bbwe");
        Console.WriteLine("Running-program look: opened "+look.Opened+", identity unreadable "+look.Unreadable+" (last status "+look.LastStatus+")");
        check(look.Running==false&&look.Opened>0,"Running-program check lists processes and finds no unknown family");
        check(StorePackageRemoval.DataFolder(StorePackageRemover.LocalAppData(),"Swivel.NotInstalled.Probe_8wekyb3d8bbwe") is string folder&&folder.EndsWith(@"\Packages\Swivel.NotInstalled.Probe_8wekyb3d8bbwe",StringComparison.Ordinal),"Local app data Packages path built from the real profile");

        // The dialog offers Uninstall… only for an eligible selected package.
        var app=Application.Current as App??new App();app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
        var eligible=Row("Contoso.Notes");var framework=Row("Contoso.Runtime") with{Framework=true};
        var dialog=new StorePackagesDialog(app,new(DateTimeOffset.UtcNow,new[]{eligible,framework},PackageListIssue.None));
        try
        {
            var footer=((DockPanel)dialog.Content).Children.OfType<WrapPanel>().Single();var uninstall=footer.Children.OfType<Button>().Single(x=>(x.Content as string)==app.L("Uninstall…"));
            var list=Descendants(dialog).OfType<ListView>().Single();
            check(!uninstall.IsEnabled&&dialog.SelectedBlock is null,"Nothing selected: Uninstall… disabled");
            list.SelectedIndex=0;check(uninstall.IsEnabled&&dialog.SelectedBlock==RemovalBlock.None&&Descendants(dialog).OfType<TextBlock>().Any(t=>t.Text==StorePackageRemoval.Reason(RemovalBlock.None,app.L)),"Eligible package: Uninstall… enabled and the reason shown");
            list.SelectedIndex=1;check(!uninstall.IsEnabled&&dialog.SelectedBlock==RemovalBlock.Framework&&Descendants(dialog).OfType<TextBlock>().Any(t=>t.Text==StorePackageRemoval.Reason(RemovalBlock.Framework,app.L)),"Framework: Uninstall… disabled with the reason shown");
            check(dialog.Uninstalled==0&&dialog.CurrentReport==StorePackages.Report(dialog.Snapshot,false,app.L,default),"Unused Uninstall… leaves the report exactly as before");
            check(footer.Children.OfType<Button>().Where(x=>x.Content as string is var t&&(t==app.L("Move Selected to Recycle Bin…")||t==app.L("Undo Move")||t==app.L("Cancel Uninstall"))).All(x=>x.Visibility==Visibility.Collapsed),"Data and cancel buttons hidden until an uninstall needs them");
        }
        finally{dialog.Close();}

        RoundTrip(directory,check,reject);
    }

    // Registers a tiny unsigned test package for this user, uninstalls it with Swivel's own code
    // path and checks the result. Machine settings are never changed: when Windows refuses the
    // registration (for example developer mode is off), the round trip is reported unverified.
    private static void RoundTrip(string directory,Action<bool,string> check,Action<Action,string> reject)
    {
        string layout=Path.Combine(directory,"msix-probe");Directory.CreateDirectory(layout);
        File.Copy(Path.Combine(Environment.SystemDirectory,"whoami.exe"),Path.Combine(layout,"probe.exe"));
        var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(BitmapSource.Create(44,44,96,96,PixelFormats.Bgra32,null,new byte[44*44*4],44*4)));
        using(var stream=File.Create(Path.Combine(layout,"logo.png")))png.Save(stream);
        File.WriteAllText(Path.Combine(layout,"AppxManifest.xml"),$"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10" xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10" xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities" IgnorableNamespaces="uap rescap">
              <Identity Name="{ProbeName}" Publisher="{ProbePublisher}" Version="1.0.0.0" ProcessorArchitecture="neutral"/>
              <Properties><DisplayName>Swivel Uninstall Probe</DisplayName><PublisherDisplayName>Swivel Test</PublisherDisplayName><Logo>logo.png</Logo></Properties>
              <Dependencies><TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.17763.0" MaxVersionTested="10.0.26100.0"/></Dependencies>
              <Resources><Resource Language="en-us"/></Resources>
              <Applications><Application Id="Probe" Executable="probe.exe" EntryPoint="Windows.FullTrustApplication"><uap:VisualElements DisplayName="Swivel Uninstall Probe" Description="Swivel test package" BackgroundColor="transparent" Square150x150Logo="logo.png" Square44x44Logo="logo.png" AppListEntry="none"/></Application></Applications>
              <Capabilities><rescap:Capability Name="runFullTrust"/></Capabilities>
            </Package>
            """);
        var manager=new PackageManager();
        string? registered=null;var errors=new List<string>();
        foreach(var(label,options)in new[]{("unsigned",new RegisterPackageOptions{AllowUnsigned=true}),("development mode",new RegisterPackageOptions{DeveloperMode=true})})
        {
            try
            {
                var result=Task.Run(async()=>await manager.RegisterPackageByUriAsync(new Uri(Path.Combine(layout,"AppxManifest.xml")),options)).GetAwaiter().GetResult();
                if(result.ExtendedErrorCode is Exception e&&e.HResult!=0){errors.Add(label+": 0x"+e.HResult.ToString("X8")+" "+result.ErrorText);continue;}
                registered=label;break;
            }
            catch(Exception e){errors.Add(label+": "+e.GetType().Name+" 0x"+e.HResult.ToString("X8")+" "+e.Message);}
        }
        if(registered is null){Console.WriteLine("UNVERIFIED: Store package uninstall round trip; Windows refused to register the unsigned test package for this user ("+string.Join(" | ",errors)+"). No machine settings were changed.");return;}
        Console.WriteLine("Store package round trip: test package registered ("+registered+")");
        string? family=null;
        try
        {
            var snapshot=StorePackagesReader.Read(CancellationToken.None);
            var probe=snapshot.Records.SingleOrDefault(x=>x.Family.Value?.StartsWith(ProbeName+"_",StringComparison.Ordinal)==true);
            check(probe is not null,"The registered test package appears in the current user's list");
            if(probe is null)return;
            family=probe.Family.Value!;
            var block=StorePackageRemoval.Check(probe,snapshot,null);
            Console.WriteLine("Test package: "+probe.FullName?.Value+" signature "+probe.Signature+" development "+probe.Development+" status "+probe.StatusOk+" offer "+block);
            check(block==RemovalBlock.None,"The registered test package is offered for Uninstall…");
            check(StorePackageRemoval.Check(probe,snapshot,family)==RemovalBlock.Swivel,"The same package is never offered as Swivel's own family");
            check(StorePackageRemover.FamilyInstalled(family)==true&&StorePackageRemover.Running(family)==false,"Family reported installed and not running");
            // A listing whose version no longer matches: nothing removed.
            var stale=probe with{Version=new(0,9,0,0),FullName=new(probe.FullName!.Value!.Replace("_1.0.0.0_","_0.9.0.0_"),PackageValueState.Present)};
            var refused=Task.Run(()=>StorePackageRemover.RemoveAsync(stale,_=>{},CancellationToken.None)).GetAwaiter().GetResult();
            check(refused.Recheck==RemovalRecheck.Changed&&!refused.Started&&StorePackageRemover.FamilyInstalled(family)==true,"A changed version is refused before any removal; the package stays installed");
            using(var cancelled=new CancellationTokenSource()){cancelled.Cancel();reject(()=>Task.Run(()=>StorePackageRemover.RemoveAsync(probe,_=>{},cancelled.Token)).GetAwaiter().GetResult(),"Cancelled before removal: nothing removed");}
            check(StorePackageRemover.FamilyInstalled(family)==true,"Still installed after the cancelled attempt");
            uint last=0;
            var run=Task.Run(()=>StorePackageRemover.RemoveAsync(probe,p=>last=Math.Max(last,p),CancellationToken.None)).GetAwaiter().GetResult();
            Console.WriteLine("Test package removal: recheck "+run.Recheck+" started "+run.Started+" error "+run.Error+" outcome "+run.Outcome+" progress "+last);
            check(run.Recheck==RemovalRecheck.Ready&&run.Started&&run.Error is null&&run.Outcome==RemovalOutcome.Removed,"Uninstall removes the test package and the list read afterwards no longer has it");
            check(StorePackageRemover.FamilyInstalled(family)==false&&run.After is not null&&StorePackageRemoval.FamilyGone(family,run.After),"Its family is gone for this user");
            if(run.Outcome!=RemovalOutcome.Removed)return;
            var again=Task.Run(()=>StorePackageRemover.RemoveAsync(probe,_=>{},CancellationToken.None)).GetAwaiter().GetResult();
            check(again.Recheck==RemovalRecheck.Gone&&!again.Started,"Uninstalling an already removed package is refused as gone");

            // Leftover data: Windows usually removes the folder itself; a test-made folder stands in for a kept one.
            string data=StorePackageRemoval.DataFolder(StorePackageRemover.LocalAppData(),family)!;
            Console.WriteLine("Data folder after removal exists: "+Directory.Exists(data));
            bool made=!Directory.Exists(data);
            if(made){Directory.CreateDirectory(Path.Combine(data,"LocalState"));File.WriteAllText(Path.Combine(data,@"LocalState\a.txt"),"probe");}
            try
            {
                if(!RecycleBin.Available(data)){Console.WriteLine("SKIP: no Recycle Bin for the data folder.");return;}
                reject(()=>NativeFiles.RecyclePackageData(data,()=>throw new UserError(StorePackageRemoval.ReinstalledData)),"Data folder never moved while its family check refuses");
                check(Directory.Exists(data),"Refused data folder stays in place");
                string stored=Run(()=>NativeFiles.RecyclePackageData(data,()=>{if(StorePackageRemover.FamilyInstalled(family)!=false)throw new UserError(StorePackageRemoval.ReinstalledData);}));
                check(!Directory.Exists(data)&&stored.Contains("$Recycle.Bin",StringComparison.OrdinalIgnoreCase),"Data folder of the uninstalled family moves to the Recycle Bin");
                Run(()=>{NativeFiles.RestoreRecycled(data,stored,true);return 0;});
                check(File.Exists(Path.Combine(data,@"LocalState\a.txt")),"Undo puts the data folder back with its files");
            }
            finally{if(made&&Directory.Exists(data))Directory.Delete(data,true);}
        }
        finally
        {
            // Never leave the test package behind, whatever failed above.
            if(family is not null&&StorePackageRemover.FamilyInstalled(family)!=false)
                foreach(var package in manager.FindPackagesForUser(string.Empty,family).ToArray())
                    try{Task.Run(async()=>await manager.RemovePackageAsync(package.Id.FullName)).GetAwaiter().GetResult();}catch(Exception e){Console.WriteLine("Cleanup of the test package failed: "+e.Message);}
        }
    }

    // The Recycle Bin restore needs an STA thread, as in the app's own worker.
    private static T Run<T>(Func<T> work)
    {
        T result=default!;Exception? failure=null;
        var thread=new Thread(()=>{try{result=work();}catch(Exception e){failure=e;}});thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(failure is not null)throw failure;return result;
    }

    private static StorePackageRecord Row(string name)
    {
        const string pub="8wekyb3d8bbwe";
        return new(0,new(name,PackageValueState.Present),new("Pub",PackageValueState.Present),new(1,0,0,0),9,false,false,false,false,DateTimeOffset.UnixEpoch,new(name+"_"+pub,PackageValueState.Present),false,
            new(name+"_1.0.0.0_x64__"+pub,PackageValueState.Present),3,true,Array.Empty<string>());
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {foreach(var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()){yield return child;foreach(var descendant in Descendants(child))yield return descendant;}}
}
