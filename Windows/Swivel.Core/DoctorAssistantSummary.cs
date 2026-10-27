using System;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Swivel.Core;

// Closed projections only: never pass a source record, path, name, hash, command,
// exception or human-readable report to the serializer.
public static class DoctorAssistantSummary
{
    private const decimal MaxLogicalBytes=long.MaxValue*20000m;
    private static void Require(bool valid){if(!valid)throw new UserError(DoctorAssistantReport.Limit);}
    private static void Count(int value,int max=20000)=>Require(value>=0&&value<=max);
    private static void Bytes(decimal value,decimal max=MaxLogicalBytes)=>Require(value>=0&&value<=max&&decimal.Truncate(value)==value);
    private static DoctorReportSnapshot Build(string kind,object summary,string limitation,bool singleSample=false)
    {
        string json=JsonSerializer.Serialize(new{format="Swivel.Doctor.Aggregate.v1",kind,scope=singleSample?"One frozen latest recorded monitor sample; no chart history or new read.":"Complete recorded result, independent of current selection, filters and navigation.",summary,
            limitations=new[]{"Historical bounded read-only summary; missing and partial results do not prove absence, health or safety.","No names, paths, identifiers, hashes, registry commands, contents or individual records are included. No new scan or automatic fix is performed.",limitation}},new JsonSerializerOptions{WriteIndented=true});
        if(Encoding.UTF8.GetByteCount(json)>32768)throw new UserError(DoctorAssistantReport.Limit);return new(json,singleSample);
    }
    private static object FolderData(FolderScanResult scan)
    {
        Count(scan.Entries);Count(scan.Root.Files);Require(scan.Root.Files<=scan.Entries);Count(scan.Root.Skipped,40000);Bytes(scan.Root.Bytes);
        return new{metadata_entries=scan.Entries,known_file_count=scan.Root.Files,known_logical_bytes=scan.Root.Bytes,skipped_items=scan.Root.Skipped,
            partial=scan.Root.Incomplete||scan.Root.Issue is not null||scan.EntryLimit||scan.TimeLimit,entry_limit=scan.EntryLimit,time_limit=scan.TimeLimit};
    }
    private const string Logical="Known logical main-stream bytes are not physical or reclaimable space; hard links may repeat, alternate streams and filesystem overhead are excluded. Unexamined items can contain additional results.";
    public static DoctorReportSnapshot Folder(FolderScanResult scan)=>Build("folder_metadata",FolderData(scan),Logical);
    public static DoctorReportSnapshot Large(FolderScanResult scan,LargeFileResult result)
    {
        Count(result.Files.Count);Count(result.Issues.Count,40000);Count(result.KnownFiles);Bytes(result.KnownBytes);
        Require(result.Files.Count==result.KnownFiles);
        return Build("large_file_metadata",new{folder=FolderData(scan),known_file_count=result.KnownFiles,known_logical_bytes=result.KnownBytes,recorded_files=result.Files.Count,recorded_issues=result.Issues.Count,partial=result.Incomplete},Logical+" Filename suffix hints do not identify installed applications or safe deletion candidates.");
    }
    public static DoctorReportSnapshot Projects(FolderScanResult scan,ProjectFolderResult result)
    {
        Count(result.Folders.Count);Count(result.Issues.Count,40000);decimal knownBytes=0;int knownFiles=0;
        foreach(var row in result.Folders){Bytes(row.Bytes);Count(row.Files);knownBytes+=row.Bytes;knownFiles=checked(knownFiles+row.Files);}
        Bytes(knownBytes);Count(knownFiles);
        return Build("project_folder_hints",new{folder=FolderData(scan),candidate_folders=result.Folders.Count,partial_candidates=result.Folders.Count(x=>x.Partial),known_candidate_files=knownFiles,known_candidate_logical_bytes=knownBytes,recorded_issues=result.Issues.Count},Logical+" Candidates are outermost filename heuristics, not validated projects or safe-to-delete outputs.");
    }
    public static DoctorReportSnapshot Duplicates(FolderScanResult scan,DuplicateScanResult result)
    {
        Count(result.Candidates);Count(result.Compared);Require(result.Compared<=result.Candidates);Count(result.Groups.Count,10000);Count(result.Issues.Count,40000);Bytes(result.BytesRead,2L*1024*1024*1024);
        int matchedFiles=0;decimal repeatedBytes=0;
        foreach(var group in result.Groups){Count(group.Paths.Count);Require(group.Paths.Count>=2);Bytes(group.Length,512L*1024*1024);Require(group.Length>0);matchedFiles=checked(matchedFiles+group.Paths.Count);repeatedBytes+=(decimal)group.Length*(group.Paths.Count-1);}
        Require(matchedFiles<=result.Compared);Bytes(repeatedBytes);
        return Build("duplicate_content_comparison",new{folder=FolderData(scan),candidates=result.Candidates,compared=result.Compared,matching_groups=result.Groups.Count,matching_files=matchedFiles,logical_repeated_bytes=repeatedBytes,content_bytes_read=result.BytesRead,recorded_issues=result.Issues.Count,byte_limit=result.ByteLimit,time_limit=result.TimeLimit},Logical+" Matches describe historical per-file main-stream hashes, not current atomic equality; no retained keeper or deletion has been chosen.");
    }
    private static void Similar(int candidates,int attempted,int loaded,int compared,int matching,int displayed,int issues,long read,long maxRead)
    {Count(candidates);Count(attempted,200);Count(loaded,200);Count(compared,19900);Count(matching,19900);Count(displayed,1000);Count(issues,40000);Bytes(read,maxRead);Require(loaded<=attempted&&attempted<=candidates&&matching<=compared&&compared<=loaded*(loaded-1)/2&&displayed<=matching);}
    public static DoctorReportSnapshot Text(FolderScanResult scan,TextSimilarityResult result)
    {
        Similar(result.Candidates,result.Attempted,result.Loaded,result.ComparedPairs,result.MatchingPairs,result.Pairs.Count,result.Issues.Count,result.BytesRead,16L*1024*1024);
        return Build("text_similarity_hints",new{folder=FolderData(scan),candidates=result.Candidates,attempted=result.Attempted,loaded=result.Loaded,compared_pairs=result.ComparedPairs,matching_pairs=result.MatchingPairs,displayed_pairs=result.Pairs.Count,content_bytes_read=result.BytesRead,recorded_issues=result.Issues.Count,file_limit=result.FileLimit,byte_limit=result.ByteLimit,line_limit=result.LineLimit,time_limit=result.TimeLimit},Logical+" Same-extension distinct-line overlap hints, at least 80%, are independent pairs, not byte identity, semantic relatedness, transitive groups or deletion advice. Only the first 1000 ordinal matches are retained.");
    }
    public static DoctorReportSnapshot Photos(FolderScanResult scan,PhotoSimilarityResult result)
    {
        Similar(result.Candidates,result.Attempted,result.Loaded,result.ComparedPairs,result.MatchingPairs,result.Pairs.Count,result.Issues.Count,result.BytesRead,128L*1024*1024);
        return Build("image_grid_similarity_hints",new{folder=FolderData(scan),candidates=result.Candidates,attempted=result.Attempted,loaded=result.Loaded,compared_pairs=result.ComparedPairs,matching_pairs=result.MatchingPairs,displayed_pairs=result.Pairs.Count,content_bytes_read=result.BytesRead,recorded_issues=result.Issues.Count,file_limit=result.FileLimit,byte_limit=result.ByteLimit,time_limit=result.TimeLimit},Logical+" At most 6 differing bits in a 64-bit brightness grid is a limited visual hint, not image identity, semantic/face recognition, transitive groups or deletion advice. Low-detail/unsupported images are skipped; only first 1000 ordinal matches are retained.");
    }
    public static DoctorReportSnapshot Memory(AppMemorySnapshot snapshot)
    {
        Count(snapshot.Processes.Count,4096);Count(snapshot.Groups.Count,4096);Require(snapshot.Groups.Count<=snapshot.Processes.Count);decimal total=0;int known=0,issues=0;
        foreach(var row in snapshot.Processes){if(row.WorkingSet is ulong value){total+=value;known++;}if(row.Issues!=AppMemoryIssue.None)issues++;}
        return Build("process_working_set_metadata",new{recorded_processes=snapshot.Processes.Count,reported_path_groups=snapshot.Groups.Count,known_working_sets=known,unavailable_working_sets=snapshot.Processes.Count-known,known_working_set_subtotal_bytes=known==0?(decimal?)null:total,processes_with_issues=issues,enumeration_limited=snapshot.EnumerationLimited,enumeration_unavailable=snapshot.EnumerationUnavailable,time_limit=snapshot.TimeLimited,duplicate_identifiers_omitted=snapshot.DuplicatesOmitted},"Working sets can share pages and are not private, unique or reclaimable RAM. Reported executable paths group samples only; helper/application ownership and leaks cannot be inferred. Missing measurements are null/unknown, not zero.");
    }
    public static DoctorReportSnapshot Installed(InstalledAppsSnapshot snapshot)
    {
        Count(snapshot.Records.Count,4096);Count(snapshot.Roots.Count,4);
        int unknown=0;foreach(var row in snapshot.Records)
        {if(row.Issues!=RegistrationEntryIssue.None||new[]{row.Name.State,row.Version.State,row.Publisher.State}.Any(x=>x is not RegistrationValueState.Present and not RegistrationValueState.Missing and not RegistrationValueState.Empty))unknown++;}
        return Build("desktop_installation_registry_records",new{recorded_entries=snapshot.Records.Count,queried_registry_views=snapshot.Roots.Count,missing_registry_views=snapshot.Roots.Count(x=>(x.Issues&RegistrationRootIssue.Missing)!=0),views_with_issues=snapshot.Roots.Count(x=>(x.Issues&~RegistrationRootIssue.Missing)!=0),unavailable_or_partial_entries=unknown},"Registry entries can repeat across views and do not prove unique installed apps, actual architecture, ownership, current installation or safe uninstall. Store/MSIX/portable/other users are outside this snapshot. No reported names, versions, publishers or subkeys are included.");
    }
    public static DoctorReportSnapshot Startup(StartupSnapshot snapshot)
    {
        Count(snapshot.Records.Count,2048);Count(snapshot.Roots.Count,8);
        int unknown=0;foreach(var row in snapshot.Records)if(row.Issues!=StartupIssue.None||row.Command.State is not RegistrationValueState.Present and not RegistrationValueState.Empty)unknown++;
        return Build("logon_run_runonce_registry_records",new{recorded_positions=snapshot.Records.Count,queried_locations=snapshot.Roots.Count,missing_locations=snapshot.Roots.Count(x=>(x.Issues&StartupRootIssue.Missing)!=0),locations_with_issues=snapshot.Roots.Count(x=>(x.Issues&~StartupRootIssue.Missing)!=0),unavailable_or_partial_records=unknown},"Run/RunOnce records are positions, not a count of unique, enabled, trusted or executing apps. StartupApproved, startup folders, tasks, services and actual execution are outside this snapshot. No names, commands, value types or subkeys are included.");
    }
    public static DoctorReportSnapshot Monitor(UsagePoint point)
    {
        Require(double.IsFinite(point.Seconds)&&point.Seconds>=0&&Enum.IsDefined(point.CpuStatus));
        static bool Percent(double? value)=>value is null||double.IsFinite(value.Value)&&value>=0&&value<=100;
        Require(Percent(point.CpuPercent)&&Percent(point.MemoryPercent));Require(point.CpuPercent is null||point.CpuStatus==CpuSampleStatus.Available);
        bool memory=point.MemoryPercent.HasValue;
        Require(memory==point.UsedBytes.HasValue&&memory==point.TotalBytes.HasValue);
        if(memory){Require(point.TotalBytes>0&&point.UsedBytes<=point.TotalBytes);double expected=(double)((decimal)point.UsedBytes!.Value*100/point.TotalBytes!.Value);Require(Math.Abs(expected-point.MemoryPercent!.Value)<=1e-10);}
        string cpuStatus=point.CpuStatus switch{CpuSampleStatus.MultipleProcessorGroups=>"multiple_processor_groups_unavailable",CpuSampleStatus.Unavailable=>"unavailable",_=>point.CpuPercent.HasValue?"sampled_interval":"needs_two_valid_samples"};
        return Build("cpu_physical_memory_frozen_sample",new{sample_seconds_since_monitor_opened=point.Seconds,cpu_percent=point.CpuPercent,cpu_status=cpuStatus,physical_memory_percent=point.MemoryPercent,physical_memory_used_bytes=point.UsedBytes,physical_memory_total_bytes=point.TotalBytes,physical_memory_available_bytes=memory?point.TotalBytes-point.UsedBytes:null},"One frozen sample, not the history or a new read. CPU is a time-counter delta needing two valid samples, unavailable with multiple processor groups. Physical memory is total minus available including standby/free/zero lists, not application ownership, working-set sums, private or reclaimable RAM, leak or cleanup evidence. Missing values stay null. Monitoring remains paused until explicit Resume.",true);
    }
}
