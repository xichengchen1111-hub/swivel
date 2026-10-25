using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace Swivel.Core;

public enum StartupApprovalArea { Run, Run32, StartupFolder }
public readonly record struct StartupApprovalLocation(RegistrationSource Source,StartupApprovalArea Area);
public enum StartupApprovalReadKind { Item,End,Unavailable,TooLarge }
public sealed record StartupApprovalRead(StartupApprovalReadKind Kind,string? Name=null,uint? Type=null,byte[]? Data=null);
public sealed record StartupApprovalOpen(IStartupApprovalRoot? Root,RegistrationOpenState State);
public interface IStartupApprovalSource { StartupApprovalOpen Open(StartupApprovalLocation location); }
public interface IStartupApprovalRoot:IDisposable { StartupApprovalRead At(uint index); }
[Flags] public enum StartupApprovalIssue { None=0,InvalidName=1,Duplicate=2,Unavailable=4,TooLarge=8 }
[Flags] public enum StartupApprovalRootIssue { None=0,Missing=1,Unavailable=2,Limit=4,TimeLimit=8,Duplicates=16 }
public sealed record StartupApprovalRecord(StartupApprovalLocation Location,int Ordinal,string? Name,uint? Type,string? HexData,StartupApprovalIssue Issues);
public sealed record StartupApprovalRootResult(StartupApprovalLocation Location,int Recorded,StartupApprovalRootIssue Issues);
public sealed record StartupApprovalSnapshot(DateTimeOffset RecordedUtc,IReadOnlyList<StartupApprovalRecord> Records,IReadOnlyList<StartupApprovalRootResult> Roots)
{
    public bool Partial=>Roots.Any(x=>(x.Issues&~StartupApprovalRootIssue.Missing)!=0)||Records.Any(x=>x.Issues!=StartupApprovalIssue.None||x.HexData is null);
}

// StartupApproved's binary payload is deliberately reported as raw bounded evidence. Its
// private/undocumented layout is not decoded or correlated with Run/Startup-folder entries.
public static class StartupApprovalSnapshotReader
{
    public const int MaxPerRoot=256,MaxNameUnits=256,MaxDataBytes=64;
    public static readonly TimeSpan SoftTimeLimit=TimeSpan.FromSeconds(10);
    public const string Scope="Read-only snapshot of fixed current-user and machine StartupApproved registry locations in requested 32-bit and 64-bit views. It reports value names, registry types and at most 64 raw data bytes; up to 256 values per location and about 10 seconds. Missing, unavailable, oversized and limited results remain visible. A blocked system call can return after the soft limit.";
    public const string Meaning="StartupApproved is reported only as raw registry evidence. Its bytes are not interpreted, and values are not matched to Run entries or Startup-folder shortcuts. This does not establish whether an item is enabled, will run, is trusted or affects startup. Registry views may share entries or resolve through links; key identity is not verified. Nothing is changed, disabled, deleted, saved automatically or sent to an assistant.";
    public const string Privacy="Registry value names can contain private paths or app names and are visible in the list. Raw bytes are shown as hexadecimal only when the complete value fits the 64-byte limit. Copying is explicit and includes the full bounded snapshot; no clipboard access occurs automatically.";
    public const string ReportLimit="This StartupApproved snapshot exceeds the copy limit. Copy one selected registry value instead.";
    public const int MaxReportChars=4*1024*1024;
    public static string AreaName(StartupApprovalArea area)=>area switch{StartupApprovalArea.Run=>"Run",StartupApprovalArea.Run32=>"Run32",StartupApprovalArea.StartupFolder=>"StartupFolder",_=>throw new ArgumentOutOfRangeException(nameof(area))};
    public static string AreaLabel(StartupApprovalArea area)=>area switch{StartupApprovalArea.Run=>"Run approval key",StartupApprovalArea.Run32=>"Run32 approval key",StartupApprovalArea.StartupFolder=>"Startup Folder approval key",_=>throw new ArgumentOutOfRangeException(nameof(area))};
    public static IReadOnlyList<StartupApprovalLocation> Locations()=>Array.AsReadOnly((from source in Enum.GetValues<RegistrationSource>() from area in Enum.GetValues<StartupApprovalArea>() select new StartupApprovalLocation(source,area)).ToArray());
    public static StartupApprovalSnapshot Scan(IStartupApprovalSource source,CancellationToken token)=>ScanWithClock(source,()=>{var watch=Stopwatch.StartNew();return watch.Elapsed;},token);
    public static StartupApprovalSnapshot ScanWithClock(IStartupApprovalSource source,Func<TimeSpan> elapsed,CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(source);ArgumentNullException.ThrowIfNull(elapsed);token.ThrowIfCancellationRequested();
        var rows=new List<StartupApprovalRecord>();var roots=new List<StartupApprovalRootResult>();bool timed=false;
        bool Stop(){token.ThrowIfCancellationRequested();if(timed)return true;if(elapsed()<SoftTimeLimit)return false;timed=true;return true;}
        var locations=Locations();
        foreach(var location in locations)
        {
            if(Stop()){roots.Add(new(location,0,StartupApprovalRootIssue.TimeLimit));continue;}
            var opened=source.Open(location);using var root=opened.Root;token.ThrowIfCancellationRequested();bool lateOpen=Stop();
            if(opened.State!=RegistrationOpenState.Open||root is null){roots.Add(new(location,0,(opened.State==RegistrationOpenState.Missing?StartupApprovalRootIssue.Missing:StartupApprovalRootIssue.Unavailable)|(lateOpen?StartupApprovalRootIssue.TimeLimit:StartupApprovalRootIssue.None)));continue;}
            if(lateOpen){roots.Add(new(location,0,StartupApprovalRootIssue.TimeLimit));continue;}
            StartupApprovalRootIssue issue=StartupApprovalRootIssue.None;var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);int count=0;
            for(int index=0;index<MaxPerRoot;index++)
            {
                if(Stop()){issue|=StartupApprovalRootIssue.TimeLimit;break;}
                var read=root.At((uint)index);token.ThrowIfCancellationRequested();bool late=Stop();
                if(read.Kind==StartupApprovalReadKind.End){if(late)issue|=StartupApprovalRootIssue.TimeLimit;break;}
                string? name=null;string? hex=null;StartupApprovalIssue rowIssue=StartupApprovalIssue.None;
                if(read.Kind==StartupApprovalReadKind.Item)
                {
                    if(!StartupRecords.ValidName(read.Name)||read.Name!.Length>MaxNameUnits){rowIssue|=StartupApprovalIssue.InvalidName;}
                    else{name=read.Name;if(!names.Add(name)){rowIssue|=StartupApprovalIssue.Duplicate;issue|=StartupApprovalRootIssue.Duplicates;}}
                    if(read.Data is null||read.Data.Length>MaxDataBytes){rowIssue|=StartupApprovalIssue.TooLarge;issue|=StartupApprovalRootIssue.Limit;}
                    else hex=Convert.ToHexString(read.Data);
                }
                else if(read.Kind==StartupApprovalReadKind.TooLarge){rowIssue|=StartupApprovalIssue.TooLarge;issue|=StartupApprovalRootIssue.Limit;}
                else{rowIssue|=StartupApprovalIssue.Unavailable;issue|=StartupApprovalRootIssue.Unavailable;}
                rows.Add(new(location,index,name,read.Type,hex,rowIssue));count++;
                if(late){issue|=StartupApprovalRootIssue.TimeLimit;break;}
                if(rowIssue.HasFlag(StartupApprovalIssue.Unavailable))break;
                if(index==MaxPerRoot-1)issue|=StartupApprovalRootIssue.Limit;
            }
            roots.Add(new(location,count,issue));
        }
        token.ThrowIfCancellationRequested();return new(DateTimeOffset.UtcNow,rows.AsReadOnly(),roots.AsReadOnly());
    }
    public static string SourceName(RegistrationSource source,Func<string,string> l)=>source switch{RegistrationSource.User64=>l("Current user · 64-bit view"),RegistrationSource.User32=>l("Current user · 32-bit view"),RegistrationSource.Machine64=>l("Local machine · 64-bit view"),RegistrationSource.Machine32=>l("Local machine · 32-bit view"),_=>throw new ArgumentOutOfRangeException(nameof(source))};
    public static string RootStatus(StartupApprovalRootIssue issue,Func<string,string> l)
    {
        var parts=new List<string>();foreach(var item in new[]{(StartupApprovalRootIssue.Missing,"Registry location not present"),(StartupApprovalRootIssue.Unavailable,"Registry location or enumeration unavailable"),(StartupApprovalRootIssue.Limit,"Entry or data limit reached; this location may be incomplete"),(StartupApprovalRootIssue.TimeLimit,"Registry location not completed because of the time limit"),(StartupApprovalRootIssue.Duplicates,"Repeated value names were recorded without merging")})if(issue.HasFlag(item.Item1))parts.Add(l(item.Item2));return parts.Count==0?l("Enumeration reached the end of this registry location"):string.Join("; ",parts);
    }
    public static string RowStatus(StartupApprovalIssue issue,Func<string,string> l)
    {
        if(issue==StartupApprovalIssue.None)return l("Recorded as returned");var parts=new List<string>();foreach(var item in new[]{(StartupApprovalIssue.InvalidName,"Invalid value name; omitted"),(StartupApprovalIssue.Duplicate,"Repeated value name; retained"),(StartupApprovalIssue.Unavailable,"Registry value unavailable"),(StartupApprovalIssue.TooLarge,"Value name or data exceeds its limit or was incomplete")})if(issue.HasFlag(item.Item1))parts.Add(l(item.Item2));return string.Join("; ",parts);
    }
    public static string Describe(StartupApprovalRecord row,Func<string,string> l)=>string.Join(Environment.NewLine,SourceName(row.Location.Source,l)+" · "+l(AreaLabel(row.Location.Area)),l("Reported Registry Value")+": "+DoctorFacts.Visible(row.Name??l("Invalid or oversized value name")),l("Enumeration position")+": "+row.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture),l("Registry value type")+": "+(row.Type?.ToString(System.Globalization.CultureInfo.InvariantCulture)??l("Unavailable")),l("Raw data (hex; not interpreted)")+": "+(row.HexData??l("Unavailable or over 64 bytes")),l("Row status")+": "+RowStatus(row.Issues,l));
    public static string Header(StartupApprovalSnapshot snapshot,Func<string,string> l)=>string.Join(Environment.NewLine,l("StartupApproved Raw Registry Snapshot"),snapshot.RecordedUtc.ToString("O",System.Globalization.CultureInfo.InvariantCulture),l(snapshot.Partial?"StartupApproved snapshot is partial.":"All requested locations were enumerated within the limits."),string.Format(l("Recorded registry values: {0} (not a count of enabled apps)"),snapshot.Records.Count),l(Scope),l(Meaning),l(Privacy));
}
