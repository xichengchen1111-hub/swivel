using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

namespace Swivel.Core;

public readonly record struct StartupLocation(RegistrationSource Source,bool Once);
public enum StartupReadKind { Item,End,Unavailable,TooLarge }
public sealed record StartupRead(StartupReadKind Kind,string? Name=null,RegistrationText? Command=null,uint? Type=null);
public sealed record StartupOpen(IStartupRoot? Root,RegistrationOpenState State);
public interface IStartupSource {StartupOpen Open(StartupLocation location);}
public interface IStartupRoot:IDisposable {StartupRead At(uint index);}
[Flags] public enum StartupIssue {None=0,InvalidName=1,Duplicate=2,Unavailable=4,TooLarge=8}
[Flags] public enum StartupRootIssue {None=0,Missing=1,Unavailable=2,Limit=4,TimeLimit=8,Duplicates=16}
public sealed record StartupRecord(StartupLocation Location,int Ordinal,string? Name,RegistrationText Command,uint? Type,StartupIssue Issues);
public sealed record StartupRootResult(StartupLocation Location,int Recorded,StartupRootIssue Issues);
public sealed record StartupSnapshot(DateTimeOffset RecordedUtc,IReadOnlyList<StartupRecord> Records,IReadOnlyList<StartupRootResult> Roots)
{
    public bool Partial=>Roots.Any(x=>(x.Issues&~StartupRootIssue.Missing)!=0)||Records.Any(x=>x.Issues!=StartupIssue.None||x.Command.State is not RegistrationValueState.Present and not RegistrationValueState.Empty);
}
public static class StartupRecords
{
    public const int MaxPerRoot=256,MaxNameUnits=256,MaxCommandUnits=2048,MaxReportBytes=4*1024*1024;
    public static readonly TimeSpan SoftTimeLimit=TimeSpan.FromSeconds(10);
    public const string Scope="Read-only logon startup records from Run and RunOnce in current-user and machine registry locations, requesting 32-bit and 64-bit views. Up to 256 positions in each of eight requested locations, 256 UTF-16 units per name, 2,048 per command and about 10 seconds. Missing, unavailable, oversized and limited results remain visible. A blocked system call can return after the soft time limit.";
    public const string Limits="These are reported registry values, not proof that an app exists, is enabled, will run, is trusted or slows startup. RunOnce behavior depends on Windows conditions. StartupApproved, startup folders, scheduled tasks, services, Store apps, other users, signatures and executable files are not inspected. No environment expansion, command parsing or execution is performed. Views may share entries or resolve through registry links; key identity and link targets are not verified, records are not merged and ARM32-specific views may be missing.";
    public const string Privacy="Reported value names appear in the list. Command text can contain personal paths or secrets and is hidden by default. Show Reported Commands includes the complete validated text in selected details and explicit copies. Whitespace-only commands are reported as empty. Nothing is disabled, deleted, saved, refreshed automatically or sent over the network. Copy Report is limited to 4 MiB of UTF-8 text; oversized copies are refused whole.";
    public const string ReportLimit="The startup-record report exceeds 4 MiB of UTF-8 text. Copy one selected record instead.";
    public static StartupSnapshot Scan(IStartupSource source,CancellationToken token){var clock=Stopwatch.StartNew();return ScanWithClock(source,()=>clock.Elapsed,token);}
    public static StartupSnapshot ScanWithClock(IStartupSource source,Func<TimeSpan> elapsed,CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(source);ArgumentNullException.ThrowIfNull(elapsed);token.ThrowIfCancellationRequested();
        var rows=new List<StartupRecord>();var roots=new List<StartupRootResult>();bool timed=false;
        bool Stop(){token.ThrowIfCancellationRequested();if(timed)return true;if(elapsed()<SoftTimeLimit)return false;timed=true;return true;}
        foreach(var origin in Enum.GetValues<RegistrationSource>())foreach(bool once in new[]{false,true})
        {
            var location=new StartupLocation(origin,once);StartupRootIssue issue=StartupRootIssue.None;int recorded=0;
            if(Stop()){roots.Add(new(location,0,StartupRootIssue.TimeLimit));continue;}
            var opened=source.Open(location);using var root=opened.Root;token.ThrowIfCancellationRequested();
            bool lateOpen=Stop();
            if(opened.State!=RegistrationOpenState.Open||root is null){roots.Add(new(location,0,(opened.State==RegistrationOpenState.Missing?StartupRootIssue.Missing:StartupRootIssue.Unavailable)|(lateOpen?StartupRootIssue.TimeLimit:StartupRootIssue.None)));continue;}
            if(lateOpen){roots.Add(new(location,0,StartupRootIssue.TimeLimit));continue;}
            var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for(int index=0;index<MaxPerRoot;index++)
            {
                if(Stop()){issue|=StartupRootIssue.TimeLimit;break;}
                var read=root.At((uint)index);token.ThrowIfCancellationRequested();bool late=Stop();
                if(read.Kind==StartupReadKind.End){if(late)issue|=StartupRootIssue.TimeLimit;break;}
                StartupIssue rowIssue=StartupIssue.None;string? name=null;RegistrationText command;uint? type=null;
                if(read.Kind==StartupReadKind.Item)
                {
                    name=ValidName(read.Name)?read.Name:null;if(name is null)rowIssue|=StartupIssue.InvalidName;
                    else if(!seen.Add(name)){rowIssue|=StartupIssue.Duplicate;issue|=StartupRootIssue.Duplicates;}
                    type=read.Type;command=Validate(read.Command??RegistrationText.Unknown(RegistrationValueState.Unavailable));
                    if(type is not 1 and not 2)command=RegistrationText.Unknown(RegistrationValueState.Unsupported);
                }
                else if(read.Kind==StartupReadKind.TooLarge){rowIssue|=StartupIssue.TooLarge;command=RegistrationText.Unknown(RegistrationValueState.TooLarge);}
                else{rowIssue|=StartupIssue.Unavailable;issue|=StartupRootIssue.Unavailable;command=RegistrationText.Unknown(RegistrationValueState.Unavailable);}
                rows.Add(new(location,index,name,command,type,rowIssue));recorded++;
                if(late){issue|=StartupRootIssue.TimeLimit;break;}
                if(rowIssue.HasFlag(StartupIssue.Unavailable))break;
                if(index==MaxPerRoot-1)issue|=StartupRootIssue.Limit;
            }
            roots.Add(new(location,recorded,issue));
        }
        token.ThrowIfCancellationRequested();return new(DateTimeOffset.UtcNow,rows.AsReadOnly(),roots.AsReadOnly());
    }
    public static bool ValidName(string? value)=>value is not null&&value.Length<=MaxNameUnits&&!value.Contains('\0')&&Unicode(value);
    public static RegistrationText Validate(RegistrationText value)
    {
        if(!Enum.IsDefined(value.State))return RegistrationText.Unknown(RegistrationValueState.Invalid);
        if(value.State!=RegistrationValueState.Present)return RegistrationText.Unknown(value.State);
        if(value.Value is not string text||text.Contains('\0')||!Unicode(text))return RegistrationText.Unknown(RegistrationValueState.Invalid);
        if(text.Length>MaxCommandUnits)return RegistrationText.Unknown(RegistrationValueState.TooLarge);
        return string.IsNullOrWhiteSpace(text)?RegistrationText.Unknown(RegistrationValueState.Empty):value;
    }
    private static bool Unicode(string value){for(int at=0;at<value.Length;){if(Rune.DecodeFromUtf16(value.AsSpan(at),out _,out int used)!=OperationStatus.Done)return false;at+=used;}return true;}
    public static string Origin(StartupLocation location,Func<string,string> l)=>InstalledApps.Origin(location.Source,l)+" · "+(location.Once?"RunOnce":"Run");
    public static string Name(StartupRecord row,Func<string,string> l)=>row.Name is null?l("Startup value name unavailable"):row.Name.Length==0?l("Unnamed registry value"):AppMemory.Visible(row.Name);
    public static string Type(uint? type,Func<string,string> l)=>type switch{1=>"REG_SZ",2=>"REG_EXPAND_SZ",null=>l("Unknown"),_=>string.Format(l("Registry value type: {0}"),type.Value)};
    public static string EntryStatus(StartupRecord row,Func<string,string> l)
    {
        var parts=new List<string>();if(row.Issues.HasFlag(StartupIssue.InvalidName))parts.Add(l("Invalid startup value name"));if(row.Issues.HasFlag(StartupIssue.Duplicate))parts.Add(l("Repeated value name in this registry view"));if(row.Issues.HasFlag(StartupIssue.Unavailable))parts.Add(l("Registry entry unavailable"));if(row.Issues.HasFlag(StartupIssue.TooLarge))parts.Add(l("Startup name or data exceeds the limit"));
        parts.Add(row.Command.State==RegistrationValueState.Present?l("Reported command text available"):InstalledApps.Value(row.Command,l));return string.Join("; ",parts);
    }
    public static string RootStatus(StartupRootIssue issue,Func<string,string> l)
    {
        var parts=new List<string>();foreach(var p in new[]{(StartupRootIssue.Missing,"Registry location not present"),(StartupRootIssue.Unavailable,"Registry location or enumeration unavailable"),(StartupRootIssue.Limit,"Entry limit reached; this view may be incomplete"),(StartupRootIssue.TimeLimit,"Registry view not completed because of the time limit"),(StartupRootIssue.Duplicates,"Repeated value names were recorded without merging")})if(issue.HasFlag(p.Item1))parts.Add(l(p.Item2));return parts.Count==0?l("Enumeration reached the end of this registry view"):string.Join("; ",parts);
    }
    public static string Describe(StartupRecord row,bool commands,Func<string,string> l)=>string.Join(Environment.NewLine,
        l("Reported Startup Name")+": "+Name(row,l),Origin(row.Location,l),string.Format(l("Enumeration position: {0}"),row.Ordinal),Type(row.Type,l),EntryStatus(row,l),
        l("Reported Command")+": "+(commands?InstalledApps.Value(row.Command,l):l("Hidden")));
    public static string Header(StartupSnapshot snapshot,bool commands,Func<string,string> l)=>string.Join(Environment.NewLine,l("Logon Startup Records"),snapshot.RecordedUtc.ToString("O",CultureInfo.InvariantCulture),
        l(snapshot.Partial?"Startup-record snapshot is partial.":"All requested available startup locations were enumerated within the limits."),string.Format(l("Recorded startup positions: {0} (not a count of unique or enabled apps)"),snapshot.Records.Count),l(Scope),l(Limits),l(Privacy),l(commands?"Reported commands are included.":"Reported commands are hidden."));
    public static string Report(StartupSnapshot snapshot,bool commands,Func<string,string> l,CancellationToken token)
    {
        var text=new StringBuilder();int bytes=0;
        void Line(string line){token.ThrowIfCancellationRequested();int count=Encoding.UTF8.GetByteCount(line)+Encoding.UTF8.GetByteCount(Environment.NewLine);if(count>MaxReportBytes-bytes)throw new UserError(ReportLimit);bytes+=count;text.AppendLine(line);}
        Line(Header(snapshot,commands,l));foreach(var root in snapshot.Roots)Line(Origin(root.Location,l)+": "+root.Recorded.ToString(CultureInfo.InvariantCulture)+" · "+RootStatus(root.Issues,l));foreach(var row in snapshot.Records){Line("");Line(Describe(row,commands,l));}token.ThrowIfCancellationRequested();return text.ToString();
    }
}
