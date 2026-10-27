using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Swivel.Core;

internal static class DoctorLabelChecks
{
    private static InstalledAppRecord App(string? text,RegistrationValueState state=RegistrationValueState.Present)=>new(RegistrationSource.User64,0,"PRIVATE-SUBKEY",new(text,state),new("PRIVATE-VERSION",RegistrationValueState.Present),new("PRIVATE-PUBLISHER",RegistrationValueState.Present),RegistrationEntryIssue.None);
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        var records=new List<InstalledAppRecord>{App("App"),App("app"),App("App"),App(null,RegistrationValueState.Missing),App("PRIVATE-BAD\n"),App("PRIVATE-BAD\u2028"),App("PRIVATE-BAD\U000E0001"),App(new string('x',161))};
        var source=new InstalledAppsSnapshot(default,records,Array.Empty<RegistrationRootResult>());var labels=DoctorLabels.Installed(source);
        check(labels.Rows.Count==2&&labels.Rows[0].Text=="App"&&labels.Rows[1].Text=="app"&&labels.Rows[1].SourceIndex==1,"Frozen labels retain ordinal case distinctions and original source indices");
        check(labels.Duplicates==1&&labels.Missing==1&&labels.Invalid==4,"Missing, invalid and duplicate label omissions are separate");
        records[0]=App("PRIVATE-LATE");check(labels.Rows[0].Text=="App","Label candidates freeze before the modal session");
        var report=DoctorAssistantSummary.Installed(source);int[] selected={1};var included=DoctorLabels.Include(report,labels,selected);selected[0]=0;
        using(var doc=JsonDocument.Parse(included.Json)){var list=doc.RootElement.GetProperty("selected_reported_labels");check(list.GetArrayLength()==1&&list[0].GetProperty("text").GetString()=="app","Only exact explicitly selected immutable label enters report");check(!included.Json.Contains("PRIVATE-"),"Other records/versions/publishers/subkeys never join optional context");using var numeric=JsonDocument.Parse(report.Json);check(JsonSerializer.Serialize(doc.RootElement.GetProperty("report"))==JsonSerializer.Serialize(numeric.RootElement),"Numeric report remains complete beneath selected-label subset");}
        foreach(int[] invalid in new[]{Array.Empty<int>(),new[]{-1},new[]{2},new[]{0,0},Enumerable.Repeat(0,41).ToArray()})reject(()=>DoctorLabels.Include(report,labels,invalid),"Empty/duplicate/out-of-range/over40 selection refuses whole report");
        var many=DoctorLabels.Installed(source with{Records=Enumerable.Range(0,4096).Select(i=>App("Label-"+i)).ToArray()});check(many.Rows.Count==4096&&many.Rows[^1].SourceIndex==4095,"No top-N truncation of valid candidate list");check(DoctorLabels.Include(report,many,new[]{4095}).Json.Contains("Label-4095"),"User may explicitly select the last retained candidate");reject(()=>DoctorLabels.Installed(source with{Records=Enumerable.Range(0,4097).Select(i=>App("Label-"+i)).ToArray()}),"Over-budget source refuses without taking a prefix");
        var large=DoctorLabels.Installed(source with{Records=Enumerable.Range(0,40).Select(i=>App(i+new string('中',158))).ToArray()});int[] all=Enumerable.Range(0,40).ToArray();reject(()=>DoctorLabels.Include(report,large,all),"Complete escaped label report budget refuses, no truncation");check(all.Length==40,"Budget refusal never changes user selection");
        foreach(var path in new[]{@"C:\PRIVATE\app.exe",@"\\server\share\app.exe",@"\\?\C:\PRIVATE\app.exe",@"\\?\UNC\server\share\app.exe"})
        {var set=DoctorLabels.Memory(new(default,new[]{new AppMemoryProcess(77,123,path,1,AppMemoryIssue.None)},Array.Empty<AppMemoryGroup>(),false,false,false,false));check(set.Rows.Count==1&&set.Rows[0].Text=="app.exe","Windows basename extraction is independent of host OS");}
        foreach(string? path in new[]{null,@"C:\",@"C:",@"\Device\HarddiskVolume1\app.exe",@"\\.\pipe\app.exe",@"C:\PRIVATE/app.exe",@"\\server\share",@"C:\PRIVATE\",@"PRIVATE.exe"})
        {var set=DoctorLabels.Memory(new(default,new[]{new AppMemoryProcess(77,123,path,1,AppMemoryIssue.None)},Array.Empty<AppMemoryGroup>(),false,false,false,false));check(set.Rows.Count==0&&set.Missing==1,"Missing/ambiguous path cannot fall back to path or PID");}
        var facts=new[]{new DoctorFact("Largest working sets","{0}: {1} MB resident memory",new object[]{"Displayed app",1d}),new DoctorFact("Largest working sets","{0}: {1} MB resident memory",new object[]{"PRIVATE-HIDDEN",1d},PrivateDetails:true),new DoctorFact("Unknown","{0}",new object[]{"PRIVATE-UNKNOWN"})};
        var factLabels=DoctorLabels.Facts(facts);check(factLabels.Rows.Count==1&&factLabels.Rows[0].Text=="Displayed app","Only exact eligible non-private process label schema is offered");
        var startup=new StartupSnapshot(default,new[]{new StartupRecord(new(RegistrationSource.User64,false),0,null,new("PRIVATE-COMMAND",RegistrationValueState.Present),1,StartupIssue.None)},Array.Empty<StartupRootResult>());check(DoctorLabels.Startup(startup).Rows.Count==0,"Missing startup name never falls back to command");
        var raw=DoctorLabels.Installed(source with{Records=new[]{App(@"C:\PRIVATE\reported label")}});check(DoctorLabels.Include(report,raw,new[]{0}).Json.Contains("PRIVATE"),"Explicit label can itself contain private paths; UI must warn accurately");
        foreach(var protocol in Enum.GetValues<AssistantProvider>())
        {var settings=new AssistantSettings(protocol,"https://example.com/api","fixture");var plan=AssistantPlan.Explain(settings,"Explain the selected labels",included);check(plan.ExplanationOnly&&plan.Choices.Count==0,"Selected Doctor labels never enable executable suggestions");}
    }
}
