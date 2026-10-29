using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

namespace Swivel.Core;

public sealed record AppMemoryIds(IReadOnlyList<uint> Ids,bool Limited=false,bool Unavailable=false);
/// Process identifier → reported parent identifier from one process snapshot.
/// Excluded directories (the Windows folder) are never used as helper parents.
public sealed record AppMemoryParentIds(IReadOnlyDictionary<uint,uint> Parents,bool Limited=false,bool Unavailable=false,IReadOnlyList<string>? ExcludedDirectories=null);
public interface IAppMemorySource
{
    AppMemoryIds Enumerate();
    IAppMemoryProcess? Open(uint pid);
    /// Null when this source cannot report parents; helper grouping is then unavailable.
    AppMemoryParentIds? ParentIds()=>null;
}
public interface IAppMemoryProcess : IDisposable
{
    ulong? CreationTime();
    string? ImagePath();
    ulong? WorkingSet();
}
[Flags] public enum AppMemoryIssue { None=0,ProcessUnavailable=1,CreationUnavailable=2,PathUnavailable=4,MemoryUnavailable=8,TimeLimit=16 }
[Flags] public enum AppMemoryParentIssue { None=0,NotRead=1,Limited=2,Unavailable=4,Changed=8,TimeLimit=16 }
/// Parent is the identifier reported identically before and after the check, or null.
public sealed record AppMemoryProcess(uint Pid,ulong? Created,string? Path,ulong? WorkingSet,AppMemoryIssue Issues,uint? Parent=null);
public sealed record AppMemoryHelper(uint Pid,uint Parent);
public sealed record AppMemoryGroup(string? Path,IReadOnlyList<AppMemoryProcess> Processes,decimal KnownBytes,int Unmeasured,IReadOnlyList<AppMemoryHelper>? Helpers=null)
{
    public int Count=>Processes.Count;
}
public sealed record AppMemorySnapshot(DateTimeOffset RecordedUtc,IReadOnlyList<AppMemoryProcess> Processes,IReadOnlyList<AppMemoryGroup> Groups,
    bool EnumerationLimited,bool EnumerationUnavailable,bool TimeLimited,bool DuplicatesOmitted,
    IReadOnlyList<AppMemoryGroup>? HelperGroups=null,AppMemoryParentIssue ParentIssues=AppMemoryParentIssue.NotRead)
{
    public decimal KnownBytes=>Groups.Sum(x=>x.KnownBytes);
    public int Unmeasured=>Processes.Count(x=>!x.WorkingSet.HasValue);
    public bool Partial=>EnumerationLimited||EnumerationUnavailable||TimeLimited||DuplicatesOmitted||Processes.Any(x=>x.Issues!=AppMemoryIssue.None);
}
public static class AppMemory
{
    public const int MaxProcesses=4096,MaxPathUnits=4096,MaxReportBytes=4*1024*1024;
    public static readonly TimeSpan SoftTimeLimit=TimeSpan.FromSeconds(10);
    public const string Scope="Explicit read-only process metadata, up to 4,096 process identifiers and about 10 seconds. This is a recorded, non-simultaneous snapshot; processes can change or exit while checking. The time limit is soft: a blocked system call may return later. No automatic refresh, process memory contents, executable files or folders are read, and no process is closed or changed.";
    public const string Grouping="Groups mean exactly the same reported executable path, compared case-sensitively. Different executable helpers are not assigned to a parent app. Identical names at different paths stay separate; aliases, hard links, replaced binaries, publishers and app ownership are not resolved. Unknown paths remain separate by process identifier.";
    public const string HelperGrouping="Optional helper grouping attaches a process to the app that started it only when the parent process identifier from a process snapshot was the same before and after the check, the parent is in this snapshot with a known executable path and start time, the helper did not start earlier than the parent, and the helper's executable folder is the parent's folder or inside it (compared case-sensitively). Parents in the Windows folder or directly at a drive or share root are never used. Chains follow the same rules; cycles and unknown or unqueried processes stay separate. Helpers started through a launcher or broker, or installed in another folder, are not attached, and a reused process identifier can still be misattributed when its times happen to fit. This is not proof of app ownership.";
    public const int MaxExcludedDirectories=16;
    public const string Measurement="Known working sets are added as a subtotal; unavailable measurements stay unknown. Shared pages can be counted more than once. This is not private memory, total physical RAM, a leak diagnosis or an amount that can be freed by quitting.";
    public const string Privacy="Executable names and process identifiers are shown and included in explicit copies. Full executable paths are hidden by default and are shown or copied only when Show Full Executable Paths is selected. No report is saved or sent automatically. Copy Report is limited to 4 MiB of UTF-8 text; an oversized report is refused, never truncated.";
    private const string ReportLimit="The report exceeds 4 MiB of UTF-8 text. Hide full paths or copy one selected item instead.";
    private static readonly ulong MaxFileTime=(ulong)DateTime.SpecifyKind(DateTime.MaxValue,DateTimeKind.Utc).ToFileTimeUtc();
    public static AppMemorySnapshot Scan(IAppMemorySource source,CancellationToken token)
    {
        var clock=Stopwatch.StartNew();return ScanWithClock(source,()=>clock.Elapsed,token);
    }
    public static AppMemorySnapshot ScanWithClock(IAppMemorySource source,Func<TimeSpan> elapsed,CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(source);ArgumentNullException.ThrowIfNull(elapsed);token.ThrowIfCancellationRequested();
        var ids=source.Enumerate();token.ThrowIfCancellationRequested();
        if(ids.Ids.Count>MaxProcesses)throw new ArgumentException("Process source exceeded its identifier budget.",nameof(source));
        var seen=new HashSet<uint>();var rows=new List<AppMemoryProcess>();bool time=false,duplicates=false;
        bool Stop(){token.ThrowIfCancellationRequested();if(time)return true;if(elapsed()<SoftTimeLimit)return false;time=true;return true;}
        // Parents are read before and after the per-process queries; only an
        // identical answer is used, which narrows (not removes) PID reuse races.
        AppMemoryParentIds? Parents(){var result=source.ParentIds();token.ThrowIfCancellationRequested();if(result is not null)CheckParents(result);return result;}
        bool beforeTime=Stop();var before=beforeTime?null:Parents();
        foreach(uint pid in ids.Ids)
        {
            token.ThrowIfCancellationRequested();if(!seen.Add(pid)){duplicates=true;continue;}
            ulong? created=null,memory=null;string? path=null;AppMemoryIssue issues=AppMemoryIssue.None;
            if(Stop())issues=AppMemoryIssue.TimeLimit;
            else
            {
                using var process=source.Open(pid);token.ThrowIfCancellationRequested();
                if(process is null)issues=AppMemoryIssue.ProcessUnavailable;
                else
                {
                    if(!Stop()){created=process.CreationTime();token.ThrowIfCancellationRequested();if(created is not >0||created>MaxFileTime){created=null;issues|=AppMemoryIssue.CreationUnavailable;}}
                    else issues|=AppMemoryIssue.TimeLimit;
                    if(!Stop()){path=process.ImagePath();token.ThrowIfCancellationRequested();if(!ValidPath(path)){path=null;issues|=AppMemoryIssue.PathUnavailable;}}
                    else issues|=AppMemoryIssue.TimeLimit;
                    if(!Stop()){memory=process.WorkingSet();token.ThrowIfCancellationRequested();if(!memory.HasValue)issues|=AppMemoryIssue.MemoryUnavailable;}
                    else issues|=AppMemoryIssue.TimeLimit;
                }
            }
            rows.Add(new(pid,created,path,memory,issues));
        }
        token.ThrowIfCancellationRequested();
        bool afterTime=!beforeTime&&before is not null&&Stop();var after=beforeTime||before is null||afterTime?null:Parents();
        AppMemoryParentIssue parentIssues=AppMemoryParentIssue.None;
        if(beforeTime||afterTime)parentIssues=AppMemoryParentIssue.TimeLimit;
        else if(before is null||after is null)parentIssues=AppMemoryParentIssue.NotRead;
        else
        {
            if(before.Limited||after.Limited)parentIssues|=AppMemoryParentIssue.Limited;
            if(before.Unavailable||after.Unavailable)parentIssues|=AppMemoryParentIssue.Unavailable;
            for(int i=0;i<rows.Count;i++)
            {
                token.ThrowIfCancellationRequested();var row=rows[i];
                bool a=before.Parents.TryGetValue(row.Pid,out uint first),b=after.Parents.TryGetValue(row.Pid,out uint second);
                if(a&&b&&first==second)rows[i]=row with{Parent=first};
                else if(!before.Unavailable&&!after.Unavailable)parentIssues|=AppMemoryParentIssue.Changed;
            }
        }
        var groups=new List<AppMemoryGroup>();
        // Prefixes separate raw paths from unknown-PID keys. Never normalize or
        // fold case: case-sensitive Windows directories can hold distinct files.
        foreach(var group in rows.GroupBy(x=>x.Path is null?"pid:"+x.Pid.ToString(CultureInfo.InvariantCulture):"path:"+x.Path,StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();var members=group.OrderBy(x=>x.Pid).ToArray();
            groups.Add(new(members[0].Path,Array.AsReadOnly(members),members.Sum(x=>x.WorkingSet.HasValue?(decimal)x.WorkingSet.Value:0),members.Count(x=>!x.WorkingSet.HasValue)));
        }
        var ordered=Order(groups);
        var helpers=parentIssues.HasFlag(AppMemoryParentIssue.NotRead)||parentIssues.HasFlag(AppMemoryParentIssue.TimeLimit)?null:HelperGroups(rows,before!.ExcludedDirectories??Array.Empty<string>(),token);
        token.ThrowIfCancellationRequested();return new(DateTimeOffset.UtcNow,Array.AsReadOnly(rows.OrderBy(x=>x.Pid).ToArray()),ordered,ids.Limited,ids.Unavailable,time,duplicates,helpers,parentIssues);
    }
    private static IReadOnlyList<AppMemoryGroup> Order(IEnumerable<AppMemoryGroup> groups)=>Array.AsReadOnly(groups.OrderBy(x=>x.Unmeasured==x.Count?1:0).ThenByDescending(x=>x.KnownBytes).ThenBy(x=>x.Path??"",StringComparer.Ordinal).ThenBy(x=>x.Processes[0].Pid).ToArray());
    private static void CheckParents(AppMemoryParentIds parents)
    {
        if(parents.Parents is null||parents.Parents.Count>MaxProcesses)throw new ArgumentException("Process source exceeded its parent budget.",nameof(parents));
        var excluded=parents.ExcludedDirectories;
        if(excluded is not null&&(excluded.Count>MaxExcludedDirectories||excluded.Any(x=>!ValidPath(x+"\\x"))))throw new ArgumentException("Excluded parent folders are invalid.",nameof(parents));
    }
    /// Groups each process with the root of its chain of accepted parent links
    /// (see HelperGrouping); everything else keeps the exact-path grouping.
    public static IReadOnlyList<AppMemoryGroup> HelperGroups(IReadOnlyList<AppMemoryProcess> rows,IReadOnlyList<string> excluded,CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(rows);ArgumentNullException.ThrowIfNull(excluded);
        if(rows.Count>MaxProcesses||excluded.Count>MaxExcludedDirectories)throw new ArgumentException("Helper grouping exceeded its budget.");
        var byPid=new Dictionary<uint,AppMemoryProcess>();foreach(var row in rows)if(!byPid.TryAdd(row.Pid,row))throw new ArgumentException("Repeated process identifier.",nameof(rows));
        var excludedFolders=excluded.Select(x=>x.TrimEnd('\\')).ToArray();
        uint? Link(AppMemoryProcess child)
        {
            if(child.Parent is not uint pid||pid==0||pid==child.Pid||!byPid.TryGetValue(pid,out var parent))return null;
            if(child.Path is null||parent.Path is null||child.Created is not ulong born||parent.Created is not ulong parentBorn||born<parentBorn)return null;
            string? childFolder=Folder(child.Path),parentFolder=Folder(parent.Path);
            if(childFolder is null||parentFolder is null||IsRoot(parentFolder))return null;
            // Exclusion folds case (excludes more); containment does not (attaches less).
            if(excludedFolders.Any(x=>Inside(parentFolder,x,StringComparison.OrdinalIgnoreCase)))return null;
            return Inside(childFolder,parentFolder,StringComparison.Ordinal)?pid:null;
        }
        var root=new Dictionary<uint,AppMemoryProcess>();var links=new Dictionary<uint,uint>();
        foreach(var row in rows)
        {
            token.ThrowIfCancellationRequested();var current=row;var visited=new HashSet<uint>{row.Pid};
            while(Link(current) is uint parent)
            {
                token.ThrowIfCancellationRequested();
                if(!visited.Add(parent)){current=row;break;} // A cycle, or a chain into one: stay separate.
                current=byPid[parent];
            }
            root[row.Pid]=current;
            if(!ReferenceEquals(current,row))links[row.Pid]=row.Parent!.Value;
        }
        var groups=new List<AppMemoryGroup>();
        foreach(var group in rows.GroupBy(x=>root[x.Pid] is var r&&r.Path is not null?"path:"+r.Path:"pid:"+r.Pid.ToString(CultureInfo.InvariantCulture),StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();var members=group.OrderBy(x=>x.Pid).ToArray();
            var lead=members.FirstOrDefault(x=>!links.ContainsKey(x.Pid))??members[0];
            var attached=members.Where(x=>links.ContainsKey(x.Pid)).Select(x=>new AppMemoryHelper(x.Pid,links[x.Pid])).ToArray();
            groups.Add(new(root[lead.Pid].Path,Array.AsReadOnly(members),members.Sum(x=>x.WorkingSet.HasValue?(decimal)x.WorkingSet.Value:0),members.Count(x=>!x.WorkingSet.HasValue),Array.AsReadOnly(attached)));
        }
        token.ThrowIfCancellationRequested();return Order(groups);
    }
    private static string? Folder(string path)
    {
        int at=path.LastIndexOf('\\');if(at<=0)return null;string folder=path[..at];
        return folder.Split('\\').Any(x=>x is "." or "..")?null:folder;
    }
    private static bool IsRoot(string folder)=>folder.StartsWith("\\\\",StringComparison.Ordinal)?folder[2..].Split('\\').Length<=2:folder.Length<=2;
    private static bool Inside(string folder,string parent,StringComparison comparison)=>folder.Equals(parent,comparison)||folder.Length>parent.Length&&folder[parent.Length]=='\\'&&folder.StartsWith(parent,comparison);
    private static bool ValidPath(string? path)
    {
        if(string.IsNullOrEmpty(path)||path.Length>MaxPathUnits||path[^1]=='\\'||!(path.StartsWith("\\\\",StringComparison.Ordinal)||path.Length>=3&&char.IsAsciiLetter(path[0])&&path[1]==':'&&path[2]=='\\'))return false;
        for(int at=0;at<path.Length;)
        {if(Rune.DecodeFromUtf16(path.AsSpan(at),out var rune,out int used)!=OperationStatus.Done||Rune.IsControl(rune))return false;at+=used;}
        return true;
    }
    public static string Name(AppMemoryProcess process,Func<string,string> localize)=>process.Path is null?string.Format(localize("Process {0}"),process.Pid):Visible(process.Path[(process.Path.LastIndexOf('\\')+1)..]);
    public static string Name(AppMemoryGroup group,Func<string,string> localize)=>Name(group.Processes[0],localize);
    public static string Bytes(ulong? value,Func<string,string> localize)=>value.HasValue?value.Value.ToString("N0",CultureInfo.CurrentCulture):localize("Unknown");
    public static string GroupBytes(AppMemoryGroup group,Func<string,string> localize)=>group.Unmeasured==group.Count?localize("Unknown"):group.KnownBytes.ToString("N0",CultureInfo.CurrentCulture);
    public static string Visible(string value)
    {
        var result=new StringBuilder();
        foreach(var rune in value.EnumerateRunes())
        {
            var category=Rune.GetUnicodeCategory(rune);int c=rune.Value;
            bool hidden=category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                ||category==UnicodeCategory.SpaceSeparator&&c!=32||c is >=0xfe00 and <=0xfe0f or >=0xe0100 and <=0xe01ef or 0x2800 or 0x115f or 0x1160 or 0x3164 or 0xffa0 or 0x1d159;
            if(hidden)result.Append("[U+").Append(c.ToString("X4",CultureInfo.InvariantCulture)).Append(']');else result.Append(rune.ToString());
        }
        return result.ToString();
    }
    public static string Status(AppMemorySnapshot snapshot,Func<string,string> localize)
    {
        var parts=new List<string>{localize(snapshot.Partial?"Process snapshot is partial.":"All enumerated process metadata was queried.")};
        if(snapshot.EnumerationLimited)parts.Add(localize("The process identifier list may be incomplete."));
        if(snapshot.EnumerationUnavailable)parts.Add(localize("Process enumeration is unavailable."));
        if(snapshot.TimeLimited)parts.Add(localize("The process query time limit was reached."));
        if(snapshot.DuplicatesOmitted)parts.Add(localize("Repeated process identifiers were omitted."));
        return string.Join(" ",parts);
    }
    public static string ParentStatus(AppMemorySnapshot snapshot,Func<string,string> localize)
    {
        var issues=snapshot.ParentIssues;
        if(issues.HasFlag(AppMemoryParentIssue.TimeLimit))return localize("Parent process identifiers were not read because of the time limit; helper grouping is unavailable.");
        if(issues.HasFlag(AppMemoryParentIssue.NotRead))return localize("Parent process identifiers were not read; helper grouping is unavailable.");
        var parts=new List<string>{localize(issues==AppMemoryParentIssue.None?"Parent process identifiers were read before and after the check.":"Parent process identifiers are partial.")};
        if(issues.HasFlag(AppMemoryParentIssue.Unavailable))parts.Add(localize("The parent process snapshot is unavailable."));
        if(issues.HasFlag(AppMemoryParentIssue.Limited))parts.Add(localize("The parent process list may be incomplete."));
        if(issues.HasFlag(AppMemoryParentIssue.Changed))parts.Add(localize("Some parent process identifiers were missing or changed during the check and are not used."));
        return string.Join(" ",parts);
    }
    public static IReadOnlyList<AppMemoryGroup> GroupsFor(AppMemorySnapshot snapshot,bool helpers)=>helpers&&snapshot.HelperGroups is {} grouped?grouped:snapshot.Groups;
    public static string Issues(AppMemoryIssue issues,Func<string,string> localize)
    {
        var result=new List<string>();
        if(issues.HasFlag(AppMemoryIssue.ProcessUnavailable))result.Add(localize("Process unavailable or access denied"));
        if(issues.HasFlag(AppMemoryIssue.CreationUnavailable))result.Add(localize("Process start time unavailable"));
        if(issues.HasFlag(AppMemoryIssue.PathUnavailable))result.Add(localize("Executable path unavailable or unsupported"));
        if(issues.HasFlag(AppMemoryIssue.MemoryUnavailable))result.Add(localize("Working set unavailable"));
        if(issues.HasFlag(AppMemoryIssue.TimeLimit))result.Add(localize("Not fully queried because of the time limit"));
        return result.Count==0?localize("No recorded query issue"):string.Join("; ",result);
    }
    public static string Describe(AppMemoryProcess process,bool paths,Func<string,string> localize)
    {
        string start=process.Created is >0&&process.Created<=MaxFileTime?DateTime.FromFileTimeUtc((long)process.Created.Value).ToString("O",CultureInfo.InvariantCulture):localize("Unknown");
        return string.Join(Environment.NewLine,Name(process,localize),string.Format(localize("Process identifier: {0}"),process.Pid),
            string.Format(localize("Recorded start time (UTC): {0}"),start),
            string.Format(localize("Reported parent process identifier: {0}"),process.Parent.HasValue?process.Parent.Value.ToString(CultureInfo.InvariantCulture):localize("Unknown")),localize("Working Set Bytes")+": "+Bytes(process.WorkingSet,localize),
            localize("Reported Executable Path")+": "+(paths?(process.Path is null?localize("Unknown"):Visible(process.Path)):localize("Hidden")),Issues(process.Issues,localize));
    }
    public static string Describe(AppMemoryGroup group,bool paths,Func<string,string> localize)=>string.Join(Environment.NewLine,Name(group,localize),
        string.Format(localize("Processes: {0} · unavailable working sets: {1}"),group.Count,group.Unmeasured),localize("Known Working Set Subtotal (Bytes)")+": "+GroupBytes(group,localize),
        localize("Reported Executable Path")+": "+(paths?(group.Path is null?localize("Unknown"):Visible(group.Path)):localize("Hidden")),
        localize("Process Identifiers")+": "+string.Join(", ",group.Processes.Select(x=>x.Pid.ToString(CultureInfo.InvariantCulture)))
        +(group.Helpers is {Count:>0} helpers?Environment.NewLine+localize("Attached helper processes")+": "+string.Join(", ",helpers.Select(h=>string.Format(localize("{0} {1} (started by {2})"),
            Name(group.Processes.First(x=>x.Pid==h.Pid),localize),h.Pid.ToString(CultureInfo.InvariantCulture),h.Parent.ToString(CultureInfo.InvariantCulture)))):""));
    public static string Header(AppMemorySnapshot snapshot,bool paths,Func<string,string> localize,bool helpers=false)=>string.Join(Environment.NewLine,
        localize("App Memory Snapshot"),snapshot.RecordedUtc.ToString("O",CultureInfo.InvariantCulture),Status(snapshot,localize),
        string.Format(localize("Recorded processes: {0} · groups: {1} · unavailable working sets: {2}"),snapshot.Processes.Count,snapshot.Groups.Count,snapshot.Unmeasured),
        localize("Known Working Set Subtotal (Bytes)")+": "+(snapshot.Unmeasured==snapshot.Processes.Count?localize("Unknown"):snapshot.KnownBytes.ToString("N0",CultureInfo.CurrentCulture)),
        localize(Scope),localize(Grouping),localize(Measurement),localize(Privacy),localize(paths?"Full executable paths are included.":"Full executable paths are hidden."))
        +(helpers&&snapshot.HelperGroups is not null?Environment.NewLine+string.Join(Environment.NewLine,localize("Helper grouping is on."),localize(HelperGrouping),ParentStatus(snapshot,localize)):"");
    public static string Report(AppMemorySnapshot snapshot,bool paths,Func<string,string> localize,CancellationToken token,bool helpers=false)
    {
        helpers&=snapshot.HelperGroups is not null;
        var text=new StringBuilder();int bytes=0;
        void Line(string line)
        {
            token.ThrowIfCancellationRequested();int count=Encoding.UTF8.GetByteCount(line)+Encoding.UTF8.GetByteCount(Environment.NewLine);
            if(count>MaxReportBytes-bytes)throw new UserError(ReportLimit);bytes+=count;text.AppendLine(line);
        }
        Line(Header(snapshot,paths,localize,helpers));
        Line(localize(helpers?"Helper Process Groups":"Executable Path Groups"));
        foreach(var group in GroupsFor(snapshot,helpers)){Line(Describe(group,paths,localize));Line("");}
        Line(localize("Recorded Processes"));
        foreach(var process in snapshot.Processes){Line(Describe(process,paths,localize));Line("");}
        return text.ToString();
    }
}
