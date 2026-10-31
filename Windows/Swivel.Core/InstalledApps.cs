using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

namespace Swivel.Core;

public enum RegistrationSource { Machine64,Machine32,User64,User32 }
public enum RegistrationField { Name,Version,Publisher,InstallLocation }
public enum RegistrationValueState { Present,Missing,Empty,Unavailable,Unsupported,TooLarge,Invalid,TimeLimit }
public sealed record RegistrationText(string? Value,RegistrationValueState State)
{
    public static RegistrationText Unknown(RegistrationValueState state)=>new(null,state);
}
public enum RegistrationKeyKind { Item,End,Unavailable,Invalid }
public sealed record RegistrationKey(RegistrationKeyKind Kind,string? Name=null);
public enum RegistrationOpenState { Open,Missing,Unavailable }
public sealed record RegistrationOpen(IRegistrationRoot? Root,RegistrationOpenState State);
public interface IInstalledAppsSource { RegistrationOpen Open(RegistrationSource source); }
public interface IRegistrationRoot : IDisposable
{
    RegistrationKey At(uint index);
    IRegistrationEntry? Open(string key);
}
public interface IRegistrationEntry : IDisposable { RegistrationText Read(RegistrationField field); }
[Flags] public enum RegistrationRootIssue { None=0,Missing=1,Unavailable=2,Limit=4,TimeLimit=8,Duplicates=16 }
[Flags] public enum RegistrationEntryIssue { None=0,KeyInvalid=1,Unavailable=2,Duplicate=4,TimeLimit=8 }
public sealed record InstalledAppRecord(RegistrationSource Source,int Ordinal,string? Key,RegistrationText Name,RegistrationText Version,RegistrationText Publisher,RegistrationEntryIssue Issues)
{
    public RegistrationText? InstallLocation { get; init; }
}
public sealed record RegistrationRootResult(RegistrationSource Source,int Recorded,RegistrationRootIssue Issues);
public sealed record InstalledAppsSnapshot(DateTimeOffset RecordedUtc,IReadOnlyList<InstalledAppRecord> Records,IReadOnlyList<RegistrationRootResult> Roots)
{
    public bool Partial=>Roots.Any(x=>(x.Issues&~RegistrationRootIssue.Missing)!=0)||Records.Any(x=>x.Issues!=RegistrationEntryIssue.None||new[]{x.Name,x.Version,x.Publisher}.Any(v=>v.State is not RegistrationValueState.Present and not RegistrationValueState.Missing and not RegistrationValueState.Empty));
}
public static class InstalledApps
{
    public const int MaxPerRoot=1024,MaxTextUnits=1024,MaxKeyUnits=255,MaxReportBytes=4*1024*1024;
    public static readonly TimeSpan SoftTimeLimit=TimeSpan.FromSeconds(10);
    public const string Scope="Explicit read-only traditional desktop installation records from four fixed current-user/machine registry views. Up to 1,024 entries per view, 1,024 UTF-16 units per text field and about 10 seconds. This is not a complete or simultaneous list of installed apps: registry data can change, be stale, duplicate or be supplied by any installer. A blocked system call can return after the soft time limit.";
    public const string Limits="The standard installation-record report includes reported display name, version and publisher text. InstallLocation text may be retained separately for explicit registration-target checks. Environment variables are not expanded. No uninstall commands, executable files, folders, signatures, disk sizes, other users, Store/MSIX inventory or portable apps are inspected. Registry views can share entries or resolve through registry links; underlying key identity and link targets are not verified. Records are not merged and do not prove installation, architecture, publisher identity, app ownership or safe cleanup. ARM32-specific records may be absent.";
    public const string Privacy="Reported names, versions and publishers appear in the lists and explicit copies. Complete registry subkey names are hidden by default; select Show Registry Subkey Names to include them in selected details and copies. No automatic refresh, report saving, network, elevation, uninstall, registry changes or file deletion. Copy Report is limited to 4 MiB of UTF-8 text; oversized copies are refused whole.";
    public const string Uninstall="To uninstall an app, use Open Installed Apps in Settings: Windows and the app's own uninstaller do the work, and Swivel never runs an uninstall command. Keep this window open meanwhile, then choose Find Leftovers of Apps Removed Since This List Was Read… to look for folders named exactly as the apps that are gone from the records.";
    public const string Packages="Store and MSIX apps are not in these records. Choose Read Store and MSIX Packages… to list the current user's packages separately; this list is kept and opens again afterwards.";
    public const string ReportLimit="The installation-record report exceeds 4 MiB of UTF-8 text. Copy one selected record instead.";
    public static InstalledAppsSnapshot Scan(IInstalledAppsSource source,CancellationToken token,bool includeLocations=false)
    {var clock=Stopwatch.StartNew();return ScanWithClock(source,()=>clock.Elapsed,token,includeLocations);}
    public static InstalledAppsSnapshot ScanWithClock(IInstalledAppsSource source,Func<TimeSpan> elapsed,CancellationToken token,bool includeLocations=false)
    {
        ArgumentNullException.ThrowIfNull(source);ArgumentNullException.ThrowIfNull(elapsed);token.ThrowIfCancellationRequested();
        var rows=new List<InstalledAppRecord>();var roots=new List<RegistrationRootResult>();bool timed=false;
        bool Stop(){token.ThrowIfCancellationRequested();if(timed)return true;if(elapsed()<SoftTimeLimit)return false;timed=true;return true;}
        foreach(var origin in Enum.GetValues<RegistrationSource>())
        {
            RegistrationRootIssue issue=RegistrationRootIssue.None;int recorded=0;
            if(Stop()){roots.Add(new(origin,0,RegistrationRootIssue.TimeLimit));continue;}
            var result=source.Open(origin);using var root=result.Root;token.ThrowIfCancellationRequested();
            if(result.State!=RegistrationOpenState.Open||root is null)
            {roots.Add(new(origin,0,result.State==RegistrationOpenState.Missing?RegistrationRootIssue.Missing:RegistrationRootIssue.Unavailable));continue;}
            var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for(int index=0;index<MaxPerRoot;index++)
            {
                if(Stop()){issue|=RegistrationRootIssue.TimeLimit;break;}
                var key=root.At((uint)index);token.ThrowIfCancellationRequested();
                if(key.Kind==RegistrationKeyKind.End)break;
                if(key.Kind==RegistrationKeyKind.Unavailable){issue|=RegistrationRootIssue.Unavailable;break;}
                RegistrationEntryIssue entryIssue=RegistrationEntryIssue.None;
                string? name=key.Kind==RegistrationKeyKind.Item&&ValidKey(key.Name)?key.Name:null;
                var unknown=RegistrationText.Unknown(RegistrationValueState.Unavailable);RegistrationText display=unknown,version=unknown,publisher=unknown;RegistrationText? location=includeLocations?unknown:null;
                if(name is null)entryIssue|=RegistrationEntryIssue.KeyInvalid;
                else if(!seen.Add(name)){entryIssue|=RegistrationEntryIssue.Duplicate;issue|=RegistrationRootIssue.Duplicates;}
                else if(Stop())
                {entryIssue|=RegistrationEntryIssue.TimeLimit;display=version=publisher=RegistrationText.Unknown(RegistrationValueState.TimeLimit);}
                else
                {
                    using var entry=root.Open(name);token.ThrowIfCancellationRequested();
                    if(entry is null)entryIssue|=RegistrationEntryIssue.Unavailable;
                    else
                    {
                        RegistrationText Read(RegistrationField field)
                        {
                            if(Stop()){entryIssue|=RegistrationEntryIssue.TimeLimit;return RegistrationText.Unknown(RegistrationValueState.TimeLimit);}
                            var value=entry.Read(field);token.ThrowIfCancellationRequested();return Validate(value);
                        }
                        display=Read(RegistrationField.Name);version=Read(RegistrationField.Version);publisher=Read(RegistrationField.Publisher);if(includeLocations)location=Read(RegistrationField.InstallLocation);
                    }
                }
                if(entryIssue.HasFlag(RegistrationEntryIssue.TimeLimit))issue|=RegistrationRootIssue.TimeLimit;
                rows.Add(new(origin,index,name,display,version,publisher,entryIssue){InstallLocation=location});recorded++;
                if(index==MaxPerRoot-1)issue|=RegistrationRootIssue.Limit;
                if(timed){issue|=RegistrationRootIssue.TimeLimit;break;}
            }
            roots.Add(new(origin,recorded,issue));
        }
        token.ThrowIfCancellationRequested();var ordered=rows.OrderBy(x=>x.Name.State==RegistrationValueState.Present?0:1).ThenBy(x=>x.Name.Value??"",StringComparer.OrdinalIgnoreCase).ThenBy(x=>x.Source).ThenBy(x=>x.Ordinal).ToArray();
        return new(DateTimeOffset.UtcNow,Array.AsReadOnly(ordered),Array.AsReadOnly(roots.ToArray()));
    }
    public static bool ValidKey(string? key)=>!string.IsNullOrEmpty(key)&&key.Length<=MaxKeyUnits&&!key.Contains('\\')&&!key.Contains('\0')&&ValidUnicode(key);
    public static RegistrationText Validate(RegistrationText value)
    {
        if(value.State!=RegistrationValueState.Present)return RegistrationText.Unknown(Enum.IsDefined(value.State)?value.State:RegistrationValueState.Invalid);
        if(value.Value is null||value.Value.Contains('\0')||!ValidUnicode(value.Value))return RegistrationText.Unknown(RegistrationValueState.Invalid);
        if(value.Value.Length>MaxTextUnits)return RegistrationText.Unknown(RegistrationValueState.TooLarge);
        return string.IsNullOrWhiteSpace(value.Value)?RegistrationText.Unknown(RegistrationValueState.Empty):value;
    }
    private static bool ValidUnicode(string value)
    {for(int at=0;at<value.Length;){if(Rune.DecodeFromUtf16(value.AsSpan(at),out _,out int used)!=OperationStatus.Done)return false;at+=used;}return true;}
    public static string Origin(RegistrationSource source,Func<string,string> l)=>l(source switch{RegistrationSource.Machine64=>"Machine registry · 64-bit view",RegistrationSource.Machine32=>"Machine registry · 32-bit view",RegistrationSource.User64=>"Current-user registry · 64-bit view",RegistrationSource.User32=>"Current-user registry · 32-bit view",_=>throw new ArgumentOutOfRangeException(nameof(source))});
    public static string Value(RegistrationText text,Func<string,string> l)=>text.State==RegistrationValueState.Present?AppMemory.Visible(text.Value!):l(text.State switch{
        RegistrationValueState.Missing=>"Not reported",RegistrationValueState.Empty=>"Reported text is empty",RegistrationValueState.Unavailable=>"Registry value unavailable",RegistrationValueState.Unsupported=>"Unsupported registry value type",RegistrationValueState.TooLarge=>"Registry text exceeds the length limit",RegistrationValueState.Invalid=>"Invalid registry text",RegistrationValueState.TimeLimit=>"Not queried because of the time limit",_=>throw new ArgumentOutOfRangeException(nameof(text))});
    public static string RootStatus(RegistrationRootIssue issue,Func<string,string> l)
    {
        var parts=new List<string>();
        foreach(var pair in new[]{(RegistrationRootIssue.Missing,"Registry location not present"),(RegistrationRootIssue.Unavailable,"Registry location or enumeration unavailable"),(RegistrationRootIssue.Limit,"Entry limit reached; this view may be incomplete"),(RegistrationRootIssue.TimeLimit,"Registry view not completed because of the time limit"),(RegistrationRootIssue.Duplicates,"Repeated subkey names were recorded without querying them again")})if(issue.HasFlag(pair.Item1))parts.Add(l(pair.Item2));
        return parts.Count==0?l("Enumeration reached the end of this registry view"):string.Join("; ",parts);
    }
    public static string EntryStatus(RegistrationEntryIssue issue,Func<string,string> l)
    {
        var parts=new List<string>();foreach(var pair in new[]{(RegistrationEntryIssue.KeyInvalid,"Invalid or oversized registry subkey name"),(RegistrationEntryIssue.Unavailable,"Registry entry unavailable"),(RegistrationEntryIssue.Duplicate,"Repeated registry subkey; values not queried again"),(RegistrationEntryIssue.TimeLimit,"Registry entry not completed because of the time limit")})if(issue.HasFlag(pair.Item1))parts.Add(l(pair.Item2));
        return parts.Count==0?l("No recorded entry-open issue; inspect each reported field"):string.Join("; ",parts);
    }
    public static string Describe(InstalledAppRecord row,bool keys,Func<string,string> l)=>string.Join(Environment.NewLine,
        l("Reported App Name")+": "+Value(row.Name,l),l("Reported Version")+": "+Value(row.Version,l),l("Reported Publisher")+": "+Value(row.Publisher,l),
        Origin(row.Source,l),string.Format(l("Enumeration position: {0}"),row.Ordinal),l("Registry Subkey Name")+": "+(keys?(row.Key is null?l("Unknown"):AppMemory.Visible(row.Key)):l("Hidden")),EntryStatus(row.Issues,l));
    public static string Header(InstalledAppsSnapshot snapshot,bool keys,Func<string,string> l)=>string.Join(Environment.NewLine,
        l("Desktop Installation Records"),snapshot.RecordedUtc.ToString("O",CultureInfo.InvariantCulture),l(snapshot.Partial?"Installation-record snapshot is partial.":"All available registry views were enumerated within the limits."),
        string.Format(l("Recorded registry entries: {0} (not a count of unique installed apps)"),snapshot.Records.Count),l(Scope),l(Limits),l(Privacy),l(keys?"Registry subkey names are included.":"Registry subkey names are hidden."));
    public static string Report(InstalledAppsSnapshot snapshot,bool keys,Func<string,string> l,CancellationToken token)
    {
        var text=new StringBuilder();int bytes=0;
        void Line(string line){token.ThrowIfCancellationRequested();int length=Encoding.UTF8.GetByteCount(line)+Encoding.UTF8.GetByteCount(Environment.NewLine);if(length>MaxReportBytes-bytes)throw new UserError(ReportLimit);bytes+=length;text.AppendLine(line);}
        Line(Header(snapshot,keys,l));foreach(var root in snapshot.Roots)Line(Origin(root.Source,l)+": "+root.Recorded.ToString(CultureInfo.InvariantCulture)+" · "+RootStatus(root.Issues,l));
        foreach(var row in snapshot.Records){Line("");Line(Describe(row,keys,l));}return text.ToString();
    }
}
