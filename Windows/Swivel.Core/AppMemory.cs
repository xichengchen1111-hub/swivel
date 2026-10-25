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
public interface IAppMemorySource
{
    AppMemoryIds Enumerate();
    IAppMemoryProcess? Open(uint pid);
}
public interface IAppMemoryProcess : IDisposable
{
    ulong? CreationTime();
    string? ImagePath();
    ulong? WorkingSet();
}
[Flags] public enum AppMemoryIssue { None=0,ProcessUnavailable=1,CreationUnavailable=2,PathUnavailable=4,MemoryUnavailable=8,TimeLimit=16 }
public sealed record AppMemoryProcess(uint Pid,ulong? Created,string? Path,ulong? WorkingSet,AppMemoryIssue Issues);
public sealed record AppMemoryGroup(string? Path,IReadOnlyList<AppMemoryProcess> Processes,decimal KnownBytes,int Unmeasured)
{
    public int Count=>Processes.Count;
}
public sealed record AppMemorySnapshot(DateTimeOffset RecordedUtc,IReadOnlyList<AppMemoryProcess> Processes,IReadOnlyList<AppMemoryGroup> Groups,
    bool EnumerationLimited,bool EnumerationUnavailable,bool TimeLimited,bool DuplicatesOmitted)
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
        var groups=new List<AppMemoryGroup>();
        // Prefixes separate raw paths from unknown-PID keys. Never normalize or
        // fold case: case-sensitive Windows directories can hold distinct files.
        foreach(var group in rows.GroupBy(x=>x.Path is null?"pid:"+x.Pid.ToString(CultureInfo.InvariantCulture):"path:"+x.Path,StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();var members=group.OrderBy(x=>x.Pid).ToArray();
            groups.Add(new(members[0].Path,Array.AsReadOnly(members),members.Sum(x=>x.WorkingSet.HasValue?(decimal)x.WorkingSet.Value:0),members.Count(x=>!x.WorkingSet.HasValue)));
        }
        var ordered=groups.OrderBy(x=>x.Unmeasured==x.Count?1:0).ThenByDescending(x=>x.KnownBytes).ThenBy(x=>x.Path??"",StringComparer.Ordinal).ThenBy(x=>x.Processes[0].Pid).ToArray();
        token.ThrowIfCancellationRequested();return new(DateTimeOffset.UtcNow,Array.AsReadOnly(rows.OrderBy(x=>x.Pid).ToArray()),Array.AsReadOnly(ordered),ids.Limited,ids.Unavailable,time,duplicates);
    }
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
            string.Format(localize("Recorded start time (UTC): {0}"),start),localize("Working Set Bytes")+": "+Bytes(process.WorkingSet,localize),
            localize("Reported Executable Path")+": "+(paths?(process.Path is null?localize("Unknown"):Visible(process.Path)):localize("Hidden")),Issues(process.Issues,localize));
    }
    public static string Describe(AppMemoryGroup group,bool paths,Func<string,string> localize)=>string.Join(Environment.NewLine,Name(group,localize),
        string.Format(localize("Processes: {0} · unavailable working sets: {1}"),group.Count,group.Unmeasured),localize("Known Working Set Subtotal (Bytes)")+": "+GroupBytes(group,localize),
        localize("Reported Executable Path")+": "+(paths?(group.Path is null?localize("Unknown"):Visible(group.Path)):localize("Hidden")),
        localize("Process Identifiers")+": "+string.Join(", ",group.Processes.Select(x=>x.Pid.ToString(CultureInfo.InvariantCulture))));
    public static string Header(AppMemorySnapshot snapshot,bool paths,Func<string,string> localize)=>string.Join(Environment.NewLine,
        localize("App Memory Snapshot"),snapshot.RecordedUtc.ToString("O",CultureInfo.InvariantCulture),Status(snapshot,localize),
        string.Format(localize("Recorded processes: {0} · groups: {1} · unavailable working sets: {2}"),snapshot.Processes.Count,snapshot.Groups.Count,snapshot.Unmeasured),
        localize("Known Working Set Subtotal (Bytes)")+": "+(snapshot.Unmeasured==snapshot.Processes.Count?localize("Unknown"):snapshot.KnownBytes.ToString("N0",CultureInfo.CurrentCulture)),
        localize(Scope),localize(Grouping),localize(Measurement),localize(Privacy),localize(paths?"Full executable paths are included.":"Full executable paths are hidden."));
    public static string Report(AppMemorySnapshot snapshot,bool paths,Func<string,string> localize,CancellationToken token)
    {
        var text=new StringBuilder();int bytes=0;
        void Line(string line)
        {
            token.ThrowIfCancellationRequested();int count=Encoding.UTF8.GetByteCount(line)+Encoding.UTF8.GetByteCount(Environment.NewLine);
            if(count>MaxReportBytes-bytes)throw new UserError(ReportLimit);bytes+=count;text.AppendLine(line);
        }
        Line(Header(snapshot,paths,localize));
        Line(localize("Executable Path Groups"));
        foreach(var group in snapshot.Groups){Line(Describe(group,paths,localize));Line("");}
        Line(localize("Recorded Processes"));
        foreach(var process in snapshot.Processes){Line(Describe(process,paths,localize));Line("");}
        return text.ToString();
    }
}
