using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

namespace Swivel.Core;

// Current-user MSIX/Store package inventory. The native adapter wraps the system
// package manager; every field read it cannot complete arrives as null (unavailable).
public enum PackageValueState { Present,Empty,Unavailable,TooLarge,Invalid,TimeLimit }
public sealed record PackageText(string? Value,PackageValueState State)
{
    public static PackageText Unknown(PackageValueState state)=>new(null,state);
}
public enum PackageTextField { DisplayName,PublisherDisplayName,FamilyName }
public enum PackageFlag { Framework,Resource,Bundle,Development }
public readonly record struct PackageVersion(ushort Major,ushort Minor,ushort Build,ushort Revision)
{
    public override string ToString()=>string.Join('.',new[]{Major,Minor,Build,Revision}.Select(x=>x.ToString(CultureInfo.InvariantCulture)));
}
public enum PackageNextKind { Item,End,Unavailable }
public sealed record PackageNext(PackageNextKind Kind,IStorePackageEntry? Entry=null);
public interface IStorePackageSource
{
    /// Null when the package manager cannot be used from this process.
    IStorePackageList? Open();
}
public interface IStorePackageList : IDisposable { PackageNext Next(); }
public interface IStorePackageEntry
{
    string? Text(PackageTextField field);
    PackageVersion? Version();
    uint? Architecture();
    bool? Flag(PackageFlag flag);
    DateTimeOffset? Installed();
}
[Flags] public enum PackageListIssue { None=0,Unavailable=1,EnumerationFailed=2,Limit=4,TimeLimit=8 }
public sealed record StorePackageRecord(int Ordinal,PackageText Name,PackageText Publisher,PackageVersion? Version,uint? Architecture,
    bool? Framework,bool? Resource,bool? Bundle,bool? Development,DateTimeOffset? Installed,PackageText Family,bool TimeLimited);
public sealed record StorePackagesSnapshot(DateTimeOffset RecordedUtc,IReadOnlyList<StorePackageRecord> Records,PackageListIssue Issues)
{
    public bool Partial=>Issues!=PackageListIssue.None||Records.Any(x=>x.TimeLimited||x.Version is null||x.Installed is null||x.Architecture is null||x.Framework is null||x.Resource is null||x.Bundle is null||x.Development is null||
        new[]{x.Name,x.Publisher,x.Family}.Any(v=>v.State is not PackageValueState.Present and not PackageValueState.Empty));
}
public static class StorePackages
{
    public const int MaxPackages=4096,MaxTextUnits=1024,MaxReportBytes=4*1024*1024;
    public static readonly TimeSpan SoftTimeLimit=TimeSpan.FromSeconds(10);
    public const string Scope="Explicit read-only list of MSIX and Microsoft Store packages registered for the current Windows user, read from the system package manager. Up to 4,096 packages, 1,024 UTF-16 units per text field and about 10 seconds. A blocked system call can return after the soft time limit.";
    public const string Limits="Only the current user's registered packages are listed: other users' packages and packages provisioned for new users are not read. Reported display names can be unresolved resource references. Framework, resource, bundle and development (sideloaded or registered from a folder) flags are as reported by Windows. Install locations, files, sizes, signatures, Store licences and update state are not inspected. This does not prove app publisher identity, that a package works, or that it is safe to remove.";
    public const string Privacy="Reported names, publishers and versions appear in the list and explicit copies. Package family names are hidden by default; select Show Package Family Names to include them in selected details and copies. No automatic refresh, report saving, network, elevation, package removal or settings changes. Copy Report is limited to 4 MiB of UTF-8 text; oversized copies are refused whole.";
    public const string ReportLimit="The package report exceeds 4 MiB of UTF-8 text. Copy one selected package instead.";
    public static StorePackagesSnapshot Scan(IStorePackageSource source,CancellationToken token)
    {var clock=Stopwatch.StartNew();return ScanWithClock(source,()=>clock.Elapsed,token);}
    public static StorePackagesSnapshot ScanWithClock(IStorePackageSource source,Func<TimeSpan> elapsed,CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(source);ArgumentNullException.ThrowIfNull(elapsed);token.ThrowIfCancellationRequested();
        var rows=new List<StorePackageRecord>();var issue=PackageListIssue.None;
        using(var list=source.Open())
        {
            token.ThrowIfCancellationRequested();
            if(list is null)issue|=PackageListIssue.Unavailable;
            else for(int index=0;;index++)
            {
                if(elapsed()>=SoftTimeLimit){issue|=PackageListIssue.TimeLimit;break;}
                if(index==MaxPackages){issue|=PackageListIssue.Limit;break;}
                var next=list.Next();token.ThrowIfCancellationRequested();
                if(next.Kind==PackageNextKind.End)break;
                if(next.Kind!=PackageNextKind.Item||next.Entry is null){issue|=PackageListIssue.EnumerationFailed;break;}
                rows.Add(Read(next.Entry,index,elapsed,token));
                if(rows[^1].TimeLimited){issue|=PackageListIssue.TimeLimit;break;}
            }
        }
        token.ThrowIfCancellationRequested();
        var ordered=rows.OrderBy(x=>x.Name.State==PackageValueState.Present?0:1).ThenBy(x=>x.Name.Value??"",StringComparer.OrdinalIgnoreCase).ThenBy(x=>x.Ordinal).ToArray();
        return new(DateTimeOffset.UtcNow,Array.AsReadOnly(ordered),issue);
    }
    private static StorePackageRecord Read(IStorePackageEntry entry,int index,Func<TimeSpan> elapsed,CancellationToken token)
    {
        bool timed=false;
        bool Stop(){token.ThrowIfCancellationRequested();if(!timed&&elapsed()>=SoftTimeLimit)timed=true;return timed;}
        T? Get<T>(Func<T?> read)where T:struct{if(Stop())return null;var value=read();token.ThrowIfCancellationRequested();return value;}
        PackageText Text(PackageTextField field){if(Stop())return PackageText.Unknown(PackageValueState.TimeLimit);var value=entry.Text(field);token.ThrowIfCancellationRequested();return Validate(value);}
        var name=Text(PackageTextField.DisplayName);var publisher=Text(PackageTextField.PublisherDisplayName);
        var version=Get(entry.Version);var architecture=Get(entry.Architecture);
        bool? framework=Get(()=>entry.Flag(PackageFlag.Framework)),resource=Get(()=>entry.Flag(PackageFlag.Resource)),bundle=Get(()=>entry.Flag(PackageFlag.Bundle)),development=Get(()=>entry.Flag(PackageFlag.Development));
        var installed=Get(entry.Installed);var family=Text(PackageTextField.FamilyName);
        return new(index,name,publisher,version,architecture,framework,resource,bundle,development,installed,family,timed);
    }
    public static PackageText Validate(string? value)
    {
        if(value is null)return PackageText.Unknown(PackageValueState.Unavailable);
        if(value.Contains('\0')||!ValidUnicode(value))return PackageText.Unknown(PackageValueState.Invalid);
        if(value.Length>MaxTextUnits)return PackageText.Unknown(PackageValueState.TooLarge);
        return string.IsNullOrWhiteSpace(value)?PackageText.Unknown(PackageValueState.Empty):new(value,PackageValueState.Present);
    }
    private static bool ValidUnicode(string value)
    {for(int at=0;at<value.Length;){if(Rune.DecodeFromUtf16(value.AsSpan(at),out _,out int used)!=OperationStatus.Done)return false;at+=used;}return true;}
    public static string Value(PackageText text,Func<string,string> l)=>text.State==PackageValueState.Present?AppMemory.Visible(text.Value!):l(text.State switch{
        PackageValueState.Empty=>"Reported text is empty",PackageValueState.Unavailable=>"Not available from the package manager",PackageValueState.TooLarge=>"Package text exceeds the length limit",
        PackageValueState.Invalid=>"Invalid package text",PackageValueState.TimeLimit=>"Not queried because of the time limit",_=>throw new ArgumentOutOfRangeException(nameof(text))});
    public static string Version(PackageVersion? version,Func<string,string> l)=>version?.ToString()??l("Not available from the package manager");
    // Windows.System.ProcessorArchitecture values.
    public static string Architecture(uint? value,Func<string,string> l)=>value switch
    {
        null=>l("Not available from the package manager"),0=>"x86",5=>"ARM",9=>"x64",11=>l("Neutral"),12=>"ARM64",14=>l("x86 on ARM64"),65535=>l("Unknown"),
        _=>string.Format(l("Unrecognized architecture value {0}"),value.Value.ToString(CultureInfo.InvariantCulture))
    };
    public static string Kind(StorePackageRecord row,Func<string,string> l)
    {
        var parts=new List<string>();
        foreach(var(flag,label)in new[]{(row.Framework,"Framework"),(row.Resource,"Resource"),(row.Bundle,"Bundle"),(row.Development,"Development (sideloaded)")})
        {if(flag==true)parts.Add(l(label));else if(flag is null)parts.Add(string.Format(l("{0}: unknown"),l(label)));}
        return parts.Count==0?l("App package"):string.Join(", ",parts);
    }
    public static string Installed(DateTimeOffset? value,Func<string,string> l)=>value is DateTimeOffset date&&date.Year>1601?date.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'",CultureInfo.InvariantCulture):l(value is null?"Not available from the package manager":"Not reported");
    public static string Status(PackageListIssue issue,Func<string,string> l)
    {
        var parts=new List<string>();
        foreach(var pair in new[]{(PackageListIssue.Unavailable,"The package manager is unavailable in this process; no packages were read"),(PackageListIssue.EnumerationFailed,"Package enumeration stopped with an error; the list may be incomplete"),(PackageListIssue.Limit,"Package limit reached; the list may be incomplete"),(PackageListIssue.TimeLimit,"Package list not completed because of the time limit")})if(issue.HasFlag(pair.Item1))parts.Add(l(pair.Item2));
        return parts.Count==0?l("Package enumeration reached the end of the current user's list"):string.Join("; ",parts);
    }
    public static string Describe(StorePackageRecord row,bool family,Func<string,string> l)=>string.Join(Environment.NewLine,
        l("Reported Package Name")+": "+Value(row.Name,l),l("Reported Publisher")+": "+Value(row.Publisher,l),l("Package Version")+": "+Version(row.Version,l),
        l("Architecture")+": "+Architecture(row.Architecture,l),l("Package Kind")+": "+Kind(row,l),l("Installed")+": "+Installed(row.Installed,l),
        string.Format(l("Enumeration position: {0}"),row.Ordinal),l("Package Family Name")+": "+(family?Value(row.Family,l):l("Hidden")),
        row.TimeLimited?l("Package not completed because of the time limit"):l("No recorded package-read issue; inspect each reported field"));
    public static string Header(StorePackagesSnapshot snapshot,bool family,Func<string,string> l)=>string.Join(Environment.NewLine,
        l("Store and MSIX Packages (Current User)"),snapshot.RecordedUtc.ToString("O",CultureInfo.InvariantCulture),l(snapshot.Partial?"Package snapshot is partial.":"The current user's package list was read within the limits."),
        string.Format(l("Recorded packages: {0} (includes frameworks and resource packages)"),snapshot.Records.Count),Status(snapshot.Issues,l),l(Scope),l(Limits),l(Privacy),
        l(family?"Package family names are included.":"Package family names are hidden."));
    public static string Report(StorePackagesSnapshot snapshot,bool family,Func<string,string> l,CancellationToken token)
    {
        var text=new StringBuilder();int bytes=0;
        void Line(string line){token.ThrowIfCancellationRequested();int length=Encoding.UTF8.GetByteCount(line)+Encoding.UTF8.GetByteCount(Environment.NewLine);if(length>MaxReportBytes-bytes)throw new UserError(ReportLimit);bytes+=length;text.AppendLine(line);}
        Line(Header(snapshot,family,l));foreach(var row in snapshot.Records){Line("");Line(Describe(row,family,l));}return text.ToString();
    }
}
