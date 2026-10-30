using System;
using System.Collections.Generic;
using System.Linq;
using Swivel.Core;

internal static class StorePackageRemovalChecks
{
    const string Pub="8wekyb3d8bbwe";
    static PackageText P(string v)=>new(v,PackageValueState.Present);
    internal static StorePackageRecord Row(string name,string version="1.2.3.4",string arch="x64",bool framework=false,bool resource=false,bool bundle=false,uint? signature=3,bool? ok=true,
        IReadOnlyList<string>? depends=null,string publisherId=Pub,string? display=null,bool timed=false)
    {
        var v=version.Split('.').Select(ushort.Parse).ToArray();
        return new(0,P(display??name),P("Pub"),new(v[0],v[1],v[2],v[3]),9,framework,resource,bundle,false,null,P(name+"_"+publisherId),timed,
            P(name+"_"+version+"_"+arch+"__"+publisherId),signature,ok,depends??Array.Empty<string>());
    }
    static StorePackagesSnapshot Snap(params StorePackageRecord[] rows)=>new(DateTimeOffset.UtcNow,rows,PackageListIssue.None);
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        var app=Row("Contoso.Notes");var snap=Snap(app);
        check(StorePackageRemoval.Check(app,snap,null)==RemovalBlock.None,"Plain Store-signed app package with complete list is offered");
        check(StorePackageRemoval.Check(Row("Contoso.Notes",framework:true),Snap(Row("Contoso.Notes",framework:true)),null)==RemovalBlock.Framework,"Framework never offered");
        var unknownKind=app with{Framework=null};check(StorePackageRemoval.Check(unknownKind,Snap(unknownKind),null)==RemovalBlock.Framework,"Unknown framework flag never offered");
        var res=Row("Contoso.Notes",resource:true);check(StorePackageRemoval.Check(res,Snap(res),null)==RemovalBlock.Resource,"Resource package never offered");
        var bun=Row("Contoso.Notes",bundle:true);check(StorePackageRemoval.Check(bun,Snap(bun),null)==RemovalBlock.Bundle,"Bundle-only row never offered");
        foreach(uint? s in new uint?[]{4,5,null}){var x=Row("Contoso.Notes",signature:s);check(StorePackageRemoval.Check(x,Snap(x),null)==RemovalBlock.System,"System-signed or unknown signature never offered: "+s);}
        foreach(uint s in new uint[]{0,1,2,3}){var x=Row("Contoso.Notes",signature:s);check(StorePackageRemoval.Check(x,Snap(x),null)==RemovalBlock.None,"Non-system signature kinds offered: "+s);}
        foreach(bool? ok in new bool?[]{false,null}){var x=Row("Contoso.Notes",ok:ok);check(StorePackageRemoval.Check(x,Snap(x),null)==RemovalBlock.Status,"Status not OK or unknown never offered");}
        check(StorePackageRemoval.Check(app,snap,"contoso.notes_"+Pub)==RemovalBlock.Swivel&&StorePackageRemoval.Check(app,snap,"Other_"+Pub)==RemovalBlock.None,"Swivel's own family never offered (case-insensitive), others unaffected");
        foreach(string name in new[]{"Microsoft.WindowsStore","Microsoft.DesktopAppInstaller","Microsoft.MicrosoftEdge.Stable","Microsoft.SecHealthUI","Microsoft.Windows.Photos","MicrosoftWindows.Client.CBS","windows.immersivecontrolpanel","Microsoft.VCLibs.140.00","Microsoft.UI.Xaml.2.8","Microsoft.NET.Native.Runtime.2.2"})
        {var x=Row(name);check(StorePackageRemoval.Check(x,Snap(x),null)==RemovalBlock.Protected,"Protected package never offered: "+name);}
        check(StorePackageRemoval.Check(Row("Microsoft.WindowsCalculator"),Snap(Row("Microsoft.WindowsCalculator")),null)==RemovalBlock.None,"Ordinary Microsoft Store app (Calculator) is offered");
        // Identity consistency.
        check(StorePackageRemoval.Check(app with{FullName=null},Snap(app with{FullName=null}),null)==RemovalBlock.Identity,"Unread full name never offered");
        var otherVersion=app with{Version=new(9,9,9,9)};check(StorePackageRemoval.Check(otherVersion,Snap(otherVersion),null)==RemovalBlock.Identity,"Full name version must equal the listed version");
        var otherFamily=app with{Family=P("Other.App_"+Pub)};check(StorePackageRemoval.Check(otherFamily,Snap(otherFamily),null)==RemovalBlock.Identity,"Full name must belong to the listed family");
        var timed=Row("Contoso.Notes",timed:true);check(StorePackageRemoval.Check(timed,Snap(timed),null)==RemovalBlock.Identity,"Time-limited row never offered");
        // List completeness and dependents.
        check(StorePackageRemoval.Check(app,snap with{Issues=PackageListIssue.Limit},null)==RemovalBlock.ListIncomplete,"Incomplete list offers nothing");
        var unread=Row("Other.App") with{DependsOn=null};check(StorePackageRemoval.Check(app,Snap(app,unread),null)==RemovalBlock.ListIncomplete,"Another package's unreadable dependencies block the offer");
        var optional=Row("Contoso.Notes.Extra",depends:new[]{"Contoso.Notes_1.2.3.4_x64__"+Pub});check(StorePackageRemoval.Check(app,Snap(app,optional),null)==RemovalBlock.Dependents,"Package another depends on by full name never offered");
        var byFamily=Row("Contoso.Notes.Extra",depends:new[]{"Contoso.Notes_1.0.0.0_neutral__"+Pub});check(StorePackageRemoval.Check(app,Snap(app,byFamily),null)==RemovalBlock.Dependents,"Package another depends on by family (other version) never offered");
        var bundleDep=Row("Contoso.Notes",bundle:true,depends:new[]{"Contoso.Notes_1.2.3.4_x64__"+Pub}) with{FullName=P("Contoso.Notes_1.2.3.4_neutral_~_"+Pub)};
        check(StorePackageRemoval.Check(app,Snap(app,bundleDep),null)==RemovalBlock.None,"A bundle containing the app does not count as a dependent");
        var duplicate=app with{Ordinal=1};check(StorePackageRemoval.Check(app,Snap(app,duplicate),null)==RemovalBlock.ListIncomplete,"Duplicate full name in the list offers nothing");
        var framework=Row("Microsoft.VCLibs.140.00",framework:true);var user=Row("Contoso.Notes",depends:new[]{"Microsoft.VCLibs.140.00_14.0.33519.0_x64__"+Pub});
        check(StorePackageRemoval.Check(framework,Snap(framework,user),null)!=RemovalBlock.None&&StorePackageRemoval.Check(user,Snap(framework,user),null)==RemovalBlock.None,"App using a framework is offered; the framework is not");
        // Name validation.
        check(StorePackageRemoval.ValidFamily("Microsoft.WindowsCalculator_"+Pub)&&!StorePackageRemoval.ValidFamily("a_"+Pub)&&!StorePackageRemoval.ValidFamily("..x_"+Pub)&&!StorePackageRemoval.ValidFamily("App_8WEKYB3D8BBWE")&&!StorePackageRemoval.ValidFamily("App_8wekyb3d8bbwi")
            &&!StorePackageRemoval.ValidFamily("A\\b_"+Pub)&&!StorePackageRemoval.ValidFamily("App_x_"+Pub)&&!StorePackageRemoval.ValidFamily(null)&&!StorePackageRemoval.ValidFamily("App"),"Family names validated strictly (name chars, 13-char lowercase publisher ID, one underscore)");
        check(StorePackageRemoval.ValidFullName("Contoso.Notes_1.2.3.4_x64__"+Pub)&&StorePackageRemoval.ValidFullName("C.N_1.0.0.0_neutral_split.scale-200_"+Pub)&&!StorePackageRemoval.ValidFullName("C.N_1.0.0_x64__"+Pub)
            &&!StorePackageRemoval.ValidFullName("C.N_1.0.0.70000_x64__"+Pub)&&!StorePackageRemoval.ValidFullName("C.N_1.0.0.0_mips__"+Pub)&&!StorePackageRemoval.ValidFullName("C.N_1.0.0.0_x64__"+Pub+"_x"),"Full names validated strictly");
        check(StorePackageRemoval.Dependencies(null) is null&&StorePackageRemoval.Dependencies(new[]{"bad"}) is null&&StorePackageRemoval.Dependencies(Enumerable.Repeat("C.N_1.0.0.0_x64__"+Pub,65).ToArray()) is null
            &&StorePackageRemoval.Dependencies(Enumerable.Repeat("C.N_1.0.0.0_x64__"+Pub,64).ToArray())?.Count==64,"Dependency lists: invalid or over 64 entries are unknown, never truncated");
        // Recheck just before removal.
        check(StorePackageRemoval.Recheck(app,snap,null)==(RemovalRecheck.Ready,RemovalBlock.None),"Unchanged package rechecks ready");
        var updated=Row("Contoso.Notes",version:"1.2.3.5");check(StorePackageRemoval.Recheck(app,Snap(updated),null).State==RemovalRecheck.Changed,"New version of the same family: changed, nothing removed");
        check(StorePackageRemoval.Recheck(app,Snap(Row("Other.App")),null).State==RemovalRecheck.Gone,"Family no longer installed: gone, nothing removed");
        check(StorePackageRemoval.Recheck(app,Snap(Row("Other.App")) with{Issues=PackageListIssue.TimeLimit},null)==(RemovalRecheck.Blocked,RemovalBlock.ListIncomplete),"Missing from an incomplete fresh list: blocked, never called gone");
        check(StorePackageRemoval.Recheck(app,Snap(app with{Publisher=P("Someone else")}),null).State==RemovalRecheck.Changed,"Changed publisher under the same full name: changed");
        check(StorePackageRemoval.Recheck(app,Snap(app with{Framework=true}),null).State==RemovalRecheck.Changed,"Changed kind: changed");
        check(StorePackageRemoval.Recheck(app,Snap(app,optional),null)==(RemovalRecheck.Blocked,RemovalBlock.Dependents),"New dependent since listing: blocked");
        check(StorePackageRemoval.Recheck(app,Snap(app with{StatusOk=false}),null)==(RemovalRecheck.Blocked,RemovalBlock.Status),"Status no longer OK: blocked");
        check(StorePackageRemoval.Recheck(app,snap,"Contoso.Notes_"+Pub)==(RemovalRecheck.Blocked,RemovalBlock.Swivel),"Recheck applies the Swivel rule too");
        check(StorePackageRemoval.Recheck(app with{FullName=null},snap,null)==(RemovalRecheck.Blocked,RemovalBlock.Identity),"Listed row without a full name never rechecks ready");
        // Outcome and data folder.
        check(StorePackageRemoval.Outcome(app.FullName!.Value!,Snap(Row("Other.App")))==RemovalOutcome.Removed&&StorePackageRemoval.Outcome(app.FullName.Value!,snap)==RemovalOutcome.StillListed
            &&StorePackageRemoval.Outcome(app.FullName.Value!,Snap(Row("Other.App")) with{Issues=PackageListIssue.EnumerationFailed})==RemovalOutcome.Unknown,"Outcome from the list read afterwards: removed, still listed, unknown when incomplete");
        string family="Contoso.Notes_"+Pub;
        check(StorePackageRemoval.FamilyGone(family,Snap(Row("Other.App")))&&!StorePackageRemoval.FamilyGone(family,Snap(updated))&&!StorePackageRemoval.FamilyGone(family,Snap(Row("Other.App")) with{Issues=PackageListIssue.Limit})
            &&!StorePackageRemoval.FamilyGone(family,Snap(Row("Other.App") with{Family=PackageText.Unknown(PackageValueState.Unavailable)})),"Data offered only when no package of the family remains in a complete list");
        check(StorePackageRemoval.DataFolder(@"C:\Users\a\AppData\Local",family)==@"C:\Users\a\AppData\Local\Packages\"+family&&StorePackageRemoval.DataFolder(@"C:\Users\a\AppData\Local\",family)==@"C:\Users\a\AppData\Local\Packages\"+family,"Data folder is Local app data\\Packages\\family");
        foreach(string? root in new[]{null,"",@"\\server\share\Local",@"C:relative",@"C:\a\..\b",@"C:\a\\b","C:/Users/a","relative"})check(StorePackageRemoval.DataFolder(root,family) is null,"Unusable Local app data path refused: "+root);
        foreach(string? bad in new[]{null,"..",@"..\x_"+Pub,"App_"+Pub+@"\..","App"})check(StorePackageRemoval.DataFolder(@"C:\Users\a\AppData\Local",bad) is null,"Invalid family never becomes a path: "+bad);
        // Texts.
        string confirm=StorePackageRemoval.Confirmation(app with{Name=P("Notes‮x")},x=>x);
        check(confirm.Contains(StorePackageRemoval.Warning)&&confirm.Contains("cannot be undone from Swivel")&&confirm.Contains("Microsoft Store")&&confirm.Contains("Contoso.Notes_1.2.3.4_x64__"+Pub)&&confirm.Contains("Notes[U+202E]x"),"Confirmation names the package, its full name, says it cannot be undone and escapes text");
        foreach(var b in Enum.GetValues<RemovalBlock>())check(!string.IsNullOrEmpty(StorePackageRemoval.Reason(b,x=>x)),"Every block has a reason");
        foreach(var s in Enum.GetValues<RemovalRecheck>())check(!string.IsNullOrEmpty(StorePackageRemoval.RecheckText(s,RemovalBlock.Status,x=>x)),"Every recheck state has text");
        foreach(var o in Enum.GetValues<RemovalOutcome>())check(!string.IsNullOrEmpty(StorePackageRemoval.OutcomeText(o,x=>x)),"Every outcome has text");
        check(StorePackageRemoval.Error(unchecked((int)0x80073CFA),"bad\u202Ething",x=>x)=="Windows reported an error: 0x80073CFA · bad[U+202E]thing"&&StorePackageRemoval.Error(5,new string('x',2000),x=>x)=="Windows reported an error: 0x00000005","Error code shown; system text escaped, oversized text omitted");
        check(StorePackageRemoval.Progress(150,x=>x)=="Uninstalling… 100%","Progress clamped");
        // The read-only list and its report are unchanged by the removal-only fields.
        var plain=new StorePackageRecord(0,P("A"),P("Pub"),new(1,0,0,0),9,false,false,false,false,DateTimeOffset.UnixEpoch,P("A.B_"+Pub),false);
        var full=plain with{FullName=P("A.B_1.0.0.0_x64__"+Pub),Signature=3,StatusOk=true,DependsOn=Array.Empty<string>()};
        check(StorePackages.Describe(plain,true,x=>x)==StorePackages.Describe(full,true,x=>x)&&!new StorePackagesSnapshot(DateTimeOffset.UtcNow,new[]{plain},PackageListIssue.None).Partial
            &&!StorePackages.Report(new(DateTimeOffset.UtcNow,new[]{full},PackageListIssue.None),true,x=>x,default).Contains("x64__"),"Removal-only fields never appear in details or reports and never make the list partial");
        foreach(string text in new[]{StorePackageRemoval.Confirm,StorePackageRemoval.Warning,StorePackageRemoval.Notice,StorePackageRemoval.DataNotice,StorePackageRemoval.Running,StorePackageRemoval.ReinstalledData,StorePackageRemoval.ReinstalledRestore,StorePackageRemoval.DataNotRecycled,
            "Uninstall…","Cancel Uninstall","Uninstall Status","Package Full Name","Uninstalling… {0}%","Windows reported an error: {0}","Uninstalled in this window: {0}","Checking the package list again…","Cancelled before removal started. Nothing was removed.",
            "The package list could not be read again. Nothing was removed.","Cancellation was requested; Windows may have finished the removal anyway. The list above was read afterwards.","A data folder Windows kept for this app is still there; it is listed below.",
            "Leftover data was not looked for: another package of this family may still be installed, or the list or the Local app data path could not be read.","No data folder for this app remains under Local app data (Packages).",
            "Move the data folder of the uninstalled app to the Recycle Bin?","Move {0} data folders of uninstalled apps to the Recycle Bin?","The folder can hold the app's settings, saved files or games. Check the path first. If it is too large for the Recycle Bin, Windows asks before deleting it permanently.",
            "Store packages uninstalled: {0} · app data folders moved to the Recycle Bin or removed: {1}"}
            .Concat(Enum.GetValues<RemovalBlock>().Select(b=>StorePackageRemoval.Reason(b,x=>x)))
            .Concat(Enum.GetValues<RemovalOutcome>().Select(o=>StorePackageRemoval.OutcomeText(o,x=>x)))
            .Concat(new[]{"The package is unchanged.","The package is no longer installed for your account. Nothing was removed.","The package changed since the list was read (for example it was updated). Nothing was removed. Read the package list again.","Nothing was removed."}))
            check(Strings.Table.TryGetValue(text,out var row)&&row.Length==3&&row.All(t=>t.Length>0&&(!text.Contains("{0}")||t.Contains("{0}"))&&(!text.Contains("{1}")||t.Contains("{1}"))),"Uninstall text translated: "+text[..Math.Min(40,text.Length)]);
    }
}
