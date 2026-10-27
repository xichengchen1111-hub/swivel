using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Swivel.Core;

internal static class DoctorSummaryChecks
{
    private sealed class Folder:IFolderMetadata
    {
        public string Name=>"PRIVATE-ROOT";
        public IEnumerable<FolderEntry> Entries(System.Threading.CancellationToken token)=>new[]{new FolderEntry("PRIVATE-A",false,100,0),new FolderEntry("PRIVATE-B",false,200,0)};
        public IFolderMetadata OpenChild(string name)=>throw new Exception("No child scan expected");public void Dispose(){}
    }
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        var scan=FolderSizes.Scan(new Folder(),default);
        var large=new LargeFileResult(new[]{new LargeFile("PRIVATE-FILE",100,true)},new[]{new LargeFileIssue("PRIVATE-PATH","PRIVATE-REASON")},true,1,100);
        var projects=new ProjectFolderResult(new[]{new ProjectFolder("PRIVATE-FOLDER","PRIVATE-PROJECT","PRIVATE-KIND",new[]{"PRIVATE-EVIDENCE"},1,100,1,true)},Array.Empty<ProjectFolderIssue>());
        var duplicate=new DuplicateScanResult(new[]{new DuplicateGroup(100,new[]{"PRIVATE-A","PRIVATE-B"},"PRIVATE-HASH")},Array.Empty<DuplicateIssue>(),2,2,200,false,false);
        var text=new TextSimilarityResult(new[]{new SimilarTextPair("PRIVATE-A","PRIVATE-B",8,10)},Array.Empty<DuplicateIssue>(),2,2,2,1,1,200,false,false,false,false);
        var photo=new PhotoSimilarityResult(new[]{new SimilarPhotoPair("PRIVATE-A","PRIVATE-B",6)},Array.Empty<DuplicateIssue>(),2,2,2,1,1,200,false,false,false);
        var processes=new List<AppMemoryProcess>{new(99,123,"PRIVATE-EXE",null,AppMemoryIssue.MemoryUnavailable)};
        var memory=new AppMemorySnapshot(DateTimeOffset.Now,processes,Array.Empty<AppMemoryGroup>(),false,false,false,false);
        var named=new RegistrationText("PRIVATE-NAME",RegistrationValueState.Present);
        var installed=new InstalledAppsSnapshot(DateTimeOffset.Now,new[]{new InstalledAppRecord(RegistrationSource.User64,0,"PRIVATE-KEY",named,named,named,RegistrationEntryIssue.None)},new[]{new RegistrationRootResult(RegistrationSource.User64,1,RegistrationRootIssue.None)});
        var startup=new StartupSnapshot(DateTimeOffset.Now,new[]{new StartupRecord(new(RegistrationSource.User64,false),0,"PRIVATE-STARTUP",new("PRIVATE-COMMAND",RegistrationValueState.Present),1,StartupIssue.None)},new[]{new StartupRootResult(new(RegistrationSource.User64,false),1,StartupRootIssue.None)});
        var snapshots=new[]{DoctorAssistantSummary.Folder(scan),DoctorAssistantSummary.Large(scan,large),DoctorAssistantSummary.Projects(scan,projects),DoctorAssistantSummary.Duplicates(scan,duplicate),DoctorAssistantSummary.Text(scan,text),DoctorAssistantSummary.Photos(scan,photo),DoctorAssistantSummary.Memory(memory),DoctorAssistantSummary.Installed(installed),DoctorAssistantSummary.Startup(startup)};
        var settings=new AssistantSettings(AssistantProvider.ChatCompletions,"https://example.com/v1/chat/completions","fixture");
        foreach(var snapshot in snapshots)
        {
            using var doc=JsonDocument.Parse(snapshot.Json);check(!snapshot.Json.Contains("PRIVATE")&&doc.RootElement.GetProperty("format").GetString()=="Swivel.Doctor.Aggregate.v1","Doctor aggregate schema contains no source strings/paths/hashes/commands");
            foreach(var provider in Enum.GetValues<AssistantProvider>())
            {var plan=AssistantPlan.Explain(settings with{Provider=provider},"Explain the recorded limits",snapshot);check(plan.ExplanationOnly&&plan.Choices.Count==0&&!plan.Json.Contains("PRIVATE"),"Both protocols only carry immutable safe aggregate");}
        }
        using(var doc=JsonDocument.Parse(snapshots[6].Json))check(doc.RootElement.GetProperty("summary").GetProperty("known_working_set_subtotal_bytes").ValueKind==JsonValueKind.Null&&doc.RootElement.GetProperty("summary").GetProperty("unavailable_working_sets").GetInt32()==1,"All unknown working sets remain null, never zero");
        string frozen=snapshots[6].Json;processes.Add(new(777,456,"PRIVATE-LATE",10,AppMemoryIssue.None));check(snapshots[6].Json==frozen&&!frozen.Contains("PRIVATE-LATE"),"Aggregates freeze before modal session and never reread original lists");
        using(var doc=JsonDocument.Parse(DoctorAssistantSummary.Memory(memory with{Processes=new[]{new AppMemoryProcess(1,null,null,0,AppMemoryIssue.None)}}).Json))check(doc.RootElement.GetProperty("summary").GetProperty("known_working_set_subtotal_bytes").GetDecimal()==0,"Measured zero differs from all-unknown subtotal");
        using(var doc=JsonDocument.Parse(snapshots[3].Json))check(doc.RootElement.GetProperty("summary").GetProperty("logical_repeated_bytes").GetDecimal()==100,"Duplicate repeated logical bytes exclude one keeper without choosing deletion");
        reject(()=>DoctorAssistantSummary.Folder(scan with{Entries=1}),"Known file count cannot exceed recorded entries");
        foreach(var count in new[]{-1,20001})reject(()=>DoctorAssistantSummary.Folder(scan with{Entries=count}),"Invalid metadata count refuses whole aggregate");
        reject(()=>DoctorAssistantSummary.Large(scan,large with{KnownBytes=-1}),"Negative logical subtotal refused");reject(()=>DoctorAssistantSummary.Large(scan,large with{KnownBytes=1.5m}),"Fractional byte subtotal refused");reject(()=>DoctorAssistantSummary.Large(scan,large with{KnownFiles=0}),"Impossible file count refused");
        reject(()=>DoctorAssistantSummary.Projects(scan,projects with{Folders=new[]{projects.Folders[0] with{Bytes=decimal.MaxValue}}}),"Oversize candidate cannot overflow aggregate");
        reject(()=>DoctorAssistantSummary.Duplicates(scan,duplicate with{Compared=1}),"Duplicate matches cannot exceed compared files");reject(()=>DoctorAssistantSummary.Duplicates(scan,duplicate with{BytesRead=2147483649}),"Duplicate content budget enforced");
        reject(()=>DoctorAssistantSummary.Text(scan,text with{Loaded=1}),"Impossible text pair relationship refused");reject(()=>DoctorAssistantSummary.Photos(scan,photo with{MatchingPairs=2}),"Impossible image match count refused");
        var missingInstalled=installed with{Records=Array.Empty<InstalledAppRecord>(),Roots=new[]{new RegistrationRootResult(RegistrationSource.User64,0,RegistrationRootIssue.Missing)}};
        using(var doc=JsonDocument.Parse(DoctorAssistantSummary.Installed(missingInstalled).Json))check(doc.RootElement.GetProperty("summary").GetProperty("missing_registry_views").GetInt32()==1,"Absent registry views remain distinct from returned empty views");
        var missingStartup=startup with{Records=Array.Empty<StartupRecord>(),Roots=new[]{new StartupRootResult(new(RegistrationSource.User64,false),0,StartupRootIssue.Missing)}};
        using(var doc=JsonDocument.Parse(DoctorAssistantSummary.Startup(missingStartup).Json))check(doc.RootElement.GetProperty("summary").GetProperty("missing_locations").GetInt32()==1,"Absent startup positions remain explicit");
        var point=new UsagePoint(2,25,75,3,4,CpuSampleStatus.Available);var monitor=DoctorAssistantSummary.Monitor(point);
        using(var doc=JsonDocument.Parse(monitor.Json)){var m=doc.RootElement.GetProperty("summary");check(m.GetProperty("physical_memory_available_bytes").GetUInt64()==1&&m.GetProperty("cpu_percent").GetDouble()==25&&monitor.SingleSample,"Monitor exact frozen CPU/memory values and sample scope");check(doc.RootElement.GetProperty("scope").GetString()!.Contains("no chart history"),"Monitor never presents whole chart as one sample");}
        foreach(var status in Enum.GetValues<CpuSampleStatus>())
        {using var doc=JsonDocument.Parse(DoctorAssistantSummary.Monitor(new(0,null,null,null,null,status)).Json);var m=doc.RootElement.GetProperty("summary");check(m.GetProperty("cpu_percent").ValueKind==JsonValueKind.Null&&m.GetProperty("physical_memory_total_bytes").ValueKind==JsonValueKind.Null,"Monitor unavailable values stay null");if(status==CpuSampleStatus.Available)check(m.GetProperty("cpu_status").GetString()=="needs_two_valid_samples","First CPU baseline is not zero or unavailable");}
        foreach(var bad in new[]{point with{Seconds=double.NaN},point with{Seconds=-1},point with{CpuPercent=double.PositiveInfinity},point with{CpuPercent=101},point with{CpuStatus=CpuSampleStatus.Unavailable},point with{CpuStatus=(CpuSampleStatus)999},point with{MemoryPercent=null},point with{TotalBytes=null},point with{TotalBytes=0},point with{UsedBytes=5},point with{MemoryPercent=74}})reject(()=>DoctorAssistantSummary.Monitor(bad),"Monitor impossible/nonfinite/incomplete sample refused");
        var memoryMaximum=new UsagePoint(double.MaxValue,null,100,ulong.MaxValue,ulong.MaxValue,CpuSampleStatus.MultipleProcessorGroups);using(var doc=JsonDocument.Parse(DoctorAssistantSummary.Monitor(memoryMaximum).Json))check(doc.RootElement.GetProperty("summary").GetProperty("physical_memory_available_bytes").GetUInt64()==0,"Monitor full unsigned byte range and finite elapsed supported");
        foreach(var provider in Enum.GetValues<AssistantProvider>())check(AssistantPlan.Explain(settings with{Provider=provider},"Explain",monitor).ExplanationOnly,"Monitor explanation remains action-free in both protocols");
    }
}
