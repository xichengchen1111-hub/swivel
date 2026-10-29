using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Swivel.Core;

internal static class StartupAppsChecks
{
    private static StartupApprovalRecord Approval(RegistrationSource source,StartupApprovalArea area,string? name,string? hex,uint? type=3,StartupApprovalIssue issue=StartupApprovalIssue.None,int ordinal=0)=>new(new(source,area),ordinal,name,type,hex,issue);
    private static StartupRecord Run(RegistrationSource source,string? name,bool once=false,string command="app.exe")=>new(new(source,once),0,name,new RegistrationText(command,RegistrationValueState.Present),1,name is null?StartupIssue.InvalidName:StartupIssue.None);
    private static StartupApprovalSnapshot Approvals(params StartupApprovalRecord[] rows)=>new(DateTimeOffset.UtcNow,rows,StartupApprovalSnapshotReader.Locations().Select(l=>new StartupApprovalRootResult(l,rows.Count(r=>r.Location==l),StartupApprovalRootIssue.None)).ToArray());
    private static StartupSnapshot Runs(params StartupRecord[] rows)=>new(DateTimeOffset.UtcNow,rows,Array.Empty<StartupRootResult>());
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        string l(string k)=>Strings.L(k,"en");
        const string disabledHex="03000000"+"00D07E2B5C10DB01";long expectedTicks=0x01DB105C2B7ED000;
        StartupApprovalMeaning D(string? hex,uint? type=3,StartupApprovalIssue issue=StartupApprovalIssue.None)=>StartupApps.Decode(Approval(RegistrationSource.User64,StartupApprovalArea.Run,"x",hex,type,issue));
        check(D("020000000000000000000000") is {State:StartupAppState.Enabled,Flag:2,DisabledUtc:null},"02 with zero time is the observed enabled form");
        check(D("060000000000000000000000").State==StartupAppState.Enabled,"06 is an observed enabled form");
        check(D(disabledHex) is {State:StartupAppState.Disabled,Flag:3} d&&d.DisabledUtc==new DateTimeOffset(DateTime.FromFileTimeUtc(expectedTicks)),"03 decodes as disabled with its little-endian FILETIME");
        check(D("070000000000000000000000") is {State:StartupAppState.Disabled,DisabledUtc:null},"07 is disabled; a zero time is not invented");
        check(D("03000000FFFFFFFFFFFFFFFF") is {State:StartupAppState.Disabled,DisabledUtc:null},"Out-of-range FILETIME keeps the flag but no time");
        check(D("020000000100000000000000") is {State:StartupAppState.Enabled,DisabledUtc:null},"Nonzero time on an enabled form is not interpreted as a date");
        foreach(string hex in new[]{"000000000000000000000000","010000000000000000000000","040000000000000000000000","FF0000000000000000000000","020100000000000000000000","02000001000000000000000000"[..24]})
            check(D(hex).State==StartupAppState.Unknown,"Unrecognized or nonzero-padded flag stays unknown: "+hex);
        foreach(string hex in new[]{"","02","0200000000000000000000","02000000000000000000000000"})check(D(hex).State==StartupAppState.Unknown,"Wrong length stays unknown: "+hex.Length/2);
        check(D("020000000000000000000000",1).State==StartupAppState.Unknown&&D("020000000000000000000000",null).State==StartupAppState.Unknown,"Non-binary registry type stays unknown");
        check(D(null).State==StartupAppState.Unknown&&D("ZZ").State==StartupAppState.Unknown,"Missing or non-hex data stays unknown");
        check(D("020000000000000000000000",3,StartupApprovalIssue.Duplicate).State==StartupAppState.Unknown,"Rows with reader issues are never interpreted");
        reject(()=>StartupApps.Decode(null!),"Null approval refused");

        check(StartupApps.ApprovalFor(new StartupLocation(RegistrationSource.User64,false))==new StartupApprovalLocation(RegistrationSource.User64,StartupApprovalArea.Run)&&StartupApps.ApprovalFor(new StartupLocation(RegistrationSource.User32,false))==new StartupApprovalLocation(RegistrationSource.User64,StartupApprovalArea.Run),"Current-user Run (both views) maps to HKCU StartupApproved\\Run");
        check(StartupApps.ApprovalFor(new StartupLocation(RegistrationSource.Machine64,false))==new StartupApprovalLocation(RegistrationSource.Machine64,StartupApprovalArea.Run)&&StartupApps.ApprovalFor(new StartupLocation(RegistrationSource.Machine32,false))==new StartupApprovalLocation(RegistrationSource.Machine64,StartupApprovalArea.Run32),"Machine Run maps to Run, the 32-bit machine view to Run32");
        check(StartupApps.ApprovalFor(new StartupLocation(RegistrationSource.User64,true)) is null,"RunOnce has no StartupApproved key");
        check(StartupApps.ApprovalFor(StartupFolderKind.CurrentUser).Area==StartupApprovalArea.StartupFolder&&StartupApps.ApprovalFor(StartupFolderKind.Common).Source==RegistrationSource.Machine64,"Startup folders map to the StartupFolder keys of their hive");

        var run=Runs(Run(RegistrationSource.User64,"Alpha"),Run(RegistrationSource.User32,"Alpha"),Run(RegistrationSource.User64,"Beta"),Run(RegistrationSource.Machine64,"Gamma"),Run(RegistrationSource.Machine32,"Gamma"),Run(RegistrationSource.User64,"Once",once:true),Run(RegistrationSource.User64,null),Run(RegistrationSource.Machine64,"Delta"));
        var approval=Approvals(
            Approval(RegistrationSource.User64,StartupApprovalArea.Run,"alpha",disabledHex),Approval(RegistrationSource.User32,StartupApprovalArea.Run,"ALPHA",disabledHex),
            Approval(RegistrationSource.User64,StartupApprovalArea.Run,"Beta","020000000000000000000000"),
            Approval(RegistrationSource.Machine64,StartupApprovalArea.Run32,"Gamma","030000000000000000000000"),
            Approval(RegistrationSource.Machine64,StartupApprovalArea.Run,"Delta","020000000000000000000000"),Approval(RegistrationSource.Machine32,StartupApprovalArea.Run,"Delta","030000000000000000000000"),
            Approval(RegistrationSource.User64,StartupApprovalArea.Run,"Orphan","070000000000000000000000"),Approval(RegistrationSource.User32,StartupApprovalArea.Run,"Orphan","070000000000000000000000"),
            Approval(RegistrationSource.User64,StartupApprovalArea.StartupFolder,"Tool.lnk","030000000000000000000000"),
            Approval(RegistrationSource.User64,StartupApprovalArea.Run32,"Alpha","020000000000000000000000"));
        var folder=new FolderSizeNode("Startup",true);var entries=new[]{new FolderSizeNode("tool.LNK",false),new FolderSizeNode("Other.lnk",false),new FolderSizeNode("Sub",true)};
        typeof(FolderSizeNode).GetProperty("Children")!.SetValue(folder,entries);
        var folders=new[]{new StartupFolderResult(StartupFolderChoice.FromReported(StartupFolderKind.CurrentUser,"C:\\Startup"),new FolderScanResult(folder,3,false,false),null,null,512,10,DateTimeOffset.Now)};
        var result=StartupApps.Combine(run,approval,folders,l,default);
        StartupAppRow One(StartupAppKind kind,string? name)=>result.Rows.Single(r=>r.Kind==kind&&r.Name==name);
        check(result.Rows.Count(r=>r.Kind==StartupAppKind.Registry&&r.Name=="Alpha")==1,"Identical current-user Run rows from both views are listed once");
        check(One(StartupAppKind.Registry,"Alpha") is {Meaning.State:StartupAppState.Disabled,Matches:2} a&&a.Meaning.DisabledUtc is not null,"Case-insensitive match across both views of the same value decodes once");
        check(One(StartupAppKind.Registry,"Beta").Meaning.State==StartupAppState.Enabled,"Exact name match in HKCU Run decodes enabled");
        var gammas=result.Rows.Where(r=>r.Kind==StartupAppKind.Registry&&r.Name=="Gamma").ToArray();
        check(gammas.Length==2&&gammas.Count(g=>g.Meaning.State==StartupAppState.Disabled&&g.Approval!.Value.Area==StartupApprovalArea.Run32)==1&&gammas.Count(g=>g.Meaning.State==StartupAppState.NoEntry)==1,"Machine 32-bit Run matches only Run32; the 64-bit value is not borrowed");
        check(One(StartupAppKind.Registry,"Delta").Meaning is {State:StartupAppState.Unknown} delta&&delta.Reason.Contains("Different"),"Conflicting values across registry views are unknown");
        check(One(StartupAppKind.Registry,"Once").Meaning.State==StartupAppState.NotCovered,"RunOnce is reported as not covered");
        check(One(StartupAppKind.Registry,null).Meaning.State==StartupAppState.Unknown,"Unavailable startup names stay visible as unknown");
        check(One(StartupAppKind.Folder,"tool.LNK").Meaning.State==StartupAppState.Disabled&&One(StartupAppKind.Folder,"Other.lnk").Meaning.State==StartupAppState.NoEntry&&One(StartupAppKind.Folder,"Sub").Meaning.State==StartupAppState.NotCovered,"Startup-folder files match the StartupFolder key by file name; folders are not matched");
        check(result.Rows.Count(r=>r.Kind==StartupAppKind.ApprovalOnly&&r.Name=="Orphan")==1&&One(StartupAppKind.ApprovalOnly,"Orphan").Meaning.State==StartupAppState.Disabled,"Unmatched approval values stay visible once with their decoded state");
        check(One(StartupAppKind.ApprovalOnly,"Alpha").Approval!.Value.Area==StartupApprovalArea.Run32,"A current-user Run32 value is not used for current-user Run and stays visible");
        check(result.FoldersInspected&&result.Sources.Count==run.Roots.Count+approval.Roots.Count+2,"Every source root and both startup folders have a status row");

        var skipped=StartupApps.Combine(run,approval,null,l,default);
        check(!skipped.FoldersInspected&&skipped.Rows.All(r=>r.Kind!=StartupAppKind.Folder)&&skipped.Rows.Any(r=>r.Kind==StartupAppKind.ApprovalOnly&&r.Name=="Tool.lnk")&&skipped.Sources.Count(s=>s.Status.StartsWith("Not inspected"))==2,"Skipped folders keep their approval values visible and say they were not inspected");

        var partialRoots=StartupApprovalSnapshotReader.Locations().Select(x=>new StartupApprovalRootResult(x,0,x==new StartupApprovalLocation(RegistrationSource.User32,StartupApprovalArea.Run)?StartupApprovalRootIssue.TimeLimit:StartupApprovalRootIssue.Missing)).ToArray();
        var partial=StartupApps.Combine(Runs(Run(RegistrationSource.User64,"Beta"),Run(RegistrationSource.Machine64,"Beta")),new(DateTimeOffset.UtcNow,Array.Empty<StartupApprovalRecord>(),partialRoots),null,l,default);
        check(partial.Rows.Single(r=>r.Where.Contains("Current-user")).Meaning.State==StartupAppState.Unknown&&partial.Rows.Single(r=>r.Where.Contains("Machine")).Meaning.State==StartupAppState.NoEntry,"Missing match in an incomplete approval key is unknown; a complete missing key is no entry");
        var badName=StartupApps.Combine(Runs(Run(RegistrationSource.User64,"Beta")),Approvals(Approval(RegistrationSource.User64,StartupApprovalArea.Run,null,null,3,StartupApprovalIssue.InvalidName)),null,l,default);
        check(badName.Rows.Single(r=>r.Kind==StartupAppKind.Registry).Meaning.State==StartupAppState.Unknown&&badName.Rows.Single(r=>r.Kind==StartupAppKind.ApprovalOnly).Meaning.State==StartupAppState.Unknown,"An unreadable approval name makes no-match unknown and stays visible itself");

        string report=StartupApps.Report(result,null,l,default);
        check(report.Contains("Microsoft does not document StartupApproved")&&report.Contains("Recorded disabled time (UTC)")&&!report.Contains("app.exe"),"Report carries the uncertainty note and never includes commands");
        var oneRow=StartupApps.Report(result,result.Rows[0],l,default);check(oneRow.Length<report.Length&&oneRow.Contains("Approval state"),"Selected-row copy contains only that row");
        var huge=Runs(Enumerable.Range(0,2048).Select(i=>Run(RegistrationSource.User64,new string('n',250)+i)).ToArray());var hugeResult=StartupApps.Combine(huge,Approvals(),null,l,default);
        check(StartupApps.Report(hugeResult,null,l,default).Length<StartupApps.MaxReportBytes,"Maximum Run snapshot stays within the copy limit");
        var spoof=StartupApps.Combine(Runs(Run(RegistrationSource.User64,"A\u202eB")),Approvals(),null,l,default);check(StartupApps.Describe(spoof.Rows[0],l).Contains("[U+202E]"),"Visible names escape bidi controls");
        using(var cancel=new CancellationTokenSource()){cancel.Cancel();reject(()=>StartupApps.Combine(run,approval,folders,l,cancel.Token),"Combine honours cancellation");reject(()=>StartupApps.Report(result,null,l,cancel.Token),"Report honours cancellation");}
        reject(()=>StartupApps.Combine(null!,approval,null,l,default),"Null snapshot refused");

        var prefs=new Prefs();var action=Catalog.Actions.Single(a=>a.Id=="doctor.startupapps");
        check(action.Feature==Feature.Doctor&&!action.RequiresFiles&&!Catalog.Available(prefs,Array.Empty<string>()).Contains(action),"Startup Apps action is Doctor-only, default off and needs no files");
        prefs.Set(Feature.Doctor,true);check(Catalog.Available(prefs,new[]{"unrelated.txt"}).Contains(action)&&WindowsDoctor.Areas.Single(a=>a.Id=="background").ActionIds.Contains(action.Id),"Startup Apps reachable from Background Items once Doctor is on");
        prefs.SetCustomActions(new[]{action.Id});check(Prefs.Parse(prefs.Serialize()).CustomActionIds.Single()==action.Id,"Startup Apps stable ID roundtrips in My Wheel");

        var texts=new List<string>{action.Title,WindowsDoctor.Areas.Single(a=>a.Id=="background").Description,StartupApps.Scope,StartupApps.Meaning,StartupApps.Privacy,StartupApps.ReportLimit,StartupApps.OrphanNote};
        var directory=new System.IO.DirectoryInfo(AppContext.BaseDirectory);while(directory is not null&&!System.IO.Directory.Exists(System.IO.Path.Combine(directory.FullName,"Swivel.Windows")))directory=directory.Parent;
        string source=string.Join("\n",new[]{"Swivel.Core/StartupApps.cs","Swivel.Windows/StartupAppsDialog.cs"}.Select(p=>System.IO.File.ReadAllText(System.IO.Path.Combine(directory!.FullName,p))));
        texts.AddRange(Regex.Matches(source,"\"([A-Z][^\"\\\\\n]* [^\"\\\\\n]*)\"").Select(m=>m.Groups[1].Value));
        foreach(string text in texts.Distinct())check(Strings.Table.TryGetValue(text,out var row)&&row.Length==3&&row.All(t=>!string.IsNullOrWhiteSpace(t)&&(!text.Contains("{0}")||t.Contains("{0}"))),"Startup Apps text translated: "+text[..Math.Min(48,text.Length)]);
    }
}
