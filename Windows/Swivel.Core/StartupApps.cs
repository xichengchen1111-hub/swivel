using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

namespace Swivel.Core;

public enum StartupAppState { Enabled,Disabled,NoEntry,NotCovered,Unknown }
public enum StartupAppKind { Registry,Folder,ApprovalOnly }
public sealed record StartupApprovalMeaning(StartupAppState State,byte? Flag,DateTimeOffset? DisabledUtc,string Reason);
public sealed record StartupAppRow(StartupAppKind Kind,string? Name,string Where,StartupApprovalLocation? Approval,StartupApprovalMeaning Meaning,int Matches);
public sealed record StartupAppSource(string Where,string Status);
public sealed record StartupAppsResult(DateTimeOffset RecordedUtc,IReadOnlyList<StartupAppRow> Rows,IReadOnlyList<StartupAppSource> Sources,bool FoldersInspected);

// Combines the existing bounded Run/RunOnce, StartupApproved and Startup-folder snapshots. Nothing new
// is read here. The StartupApproved layout is not documented by Microsoft: Task Manager is publicly
// observed to write 12-byte REG_BINARY values whose first DWORD is 2 (or 6) when enabled and 3 (or 7)
// when disabled, followed by an 8-byte FILETIME that is nonzero when disabled. Only those exact forms
// are interpreted; every other value stays unknown with its raw bytes visible in the raw snapshot.
public static class StartupApps
{
    public const int MaxReportBytes=4*1024*1024;
    public const string Scope="Read-only combination of the existing Run/RunOnce records, StartupApproved values and, if you choose, Startup-folder entry names. Each Run value and Startup-folder item is matched to the StartupApproved value with the same name, ignoring letter case like the registry, in the corresponding key: current-user Run, machine Run, machine Run32 for the 32-bit machine view, and the current-user or machine StartupFolder key. Both registry views of StartupApproved are consulted.";
    public const string Meaning="Microsoft does not document StartupApproved. Values are interpreted only in the publicly observed Task Manager form: 12 bytes of REG_BINARY whose first four bytes are 02, 06 (enabled) or 03, 07 (disabled) and whose last eight bytes are a FILETIME, usually the time it was disabled. Any other form, conflicting values, malformed or incomplete data are shown as unknown. An item without a StartupApproved value is usually shown as enabled by Task Manager, but that is not verified here. RunOnce is not covered by StartupApproved. The result does not prove that an item runs, exists, is trusted or slows startup.";
    public const string Privacy="Names are visible and can contain private app names or paths. Commands and file contents are not shown or read; use Logon Startup Records for commands. Nothing is enabled, disabled, deleted, saved automatically or sent over the network. Copy Report and Copy Selected Row are explicit and limited to 4 MiB of UTF-8 text; oversized copies are refused whole.";
    public const string ReportLimit="The Startup Apps report exceeds 4 MiB of UTF-8 text. Copy one selected row instead.";
    public const string OrphanNote="No matching startup item was recorded; the item may be missing, in an uninspected location or beyond a limit.";
    public const long MaxFileTime=2650467743999999999;

    public static StartupApprovalLocation? ApprovalFor(StartupLocation location)=>location.Once?null:new StartupApprovalLocation(Hive(location.Source),location.Source==RegistrationSource.Machine32?StartupApprovalArea.Run32:StartupApprovalArea.Run);
    public static StartupApprovalLocation ApprovalFor(StartupFolderKind kind)=>kind switch{StartupFolderKind.CurrentUser=>new(RegistrationSource.User64,StartupApprovalArea.StartupFolder),StartupFolderKind.Common=>new(RegistrationSource.Machine64,StartupApprovalArea.StartupFolder),_=>throw new ArgumentOutOfRangeException(nameof(kind))};
    private static RegistrationSource Hive(RegistrationSource source)=>source switch{RegistrationSource.User64 or RegistrationSource.User32=>RegistrationSource.User64,RegistrationSource.Machine64 or RegistrationSource.Machine32=>RegistrationSource.Machine64,_=>throw new ArgumentOutOfRangeException(nameof(source))};
    private static bool SameKey(StartupApprovalLocation actual,StartupApprovalLocation wanted)=>Hive(actual.Source)==wanted.Source&&actual.Area==wanted.Area;

    public static StartupApprovalMeaning Decode(StartupApprovalRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if(record.Issues!=StartupApprovalIssue.None||record.HexData is null)return Unknown("StartupApproved value is malformed, repeated, oversized or unavailable.");
        if(record.Type!=3)return Unknown("StartupApproved value is not REG_BINARY.");
        byte[] data;try{data=Convert.FromHexString(record.HexData);}catch(FormatException){return Unknown("StartupApproved value is malformed, repeated, oversized or unavailable.");}
        if(data.Length!=12)return Unknown("StartupApproved value does not have the observed 12-byte form.");
        byte flag=data[0];if(data[1]!=0||data[2]!=0||data[3]!=0)return new(StartupAppState.Unknown,flag,null,"Unrecognized StartupApproved flag value.");
        var state=flag switch{2 or 6=>StartupAppState.Enabled,3 or 7=>StartupAppState.Disabled,_=>StartupAppState.Unknown};
        if(state==StartupAppState.Unknown)return new(state,flag,null,"Unrecognized StartupApproved flag value.");
        ulong time=BitConverter.ToUInt64(data,4);if(!BitConverter.IsLittleEndian)time=System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(time);
        if(state==StartupAppState.Enabled)return new(state,flag,null,time==0?"Observed enabled form.":"Observed enabled flag; the time field is not zero and is not interpreted.");
        if(time==0)return new(state,flag,null,"Observed disabled flag without a recorded time.");
        if(time>MaxFileTime)return new(state,flag,null,"Observed disabled flag; the time field is out of range.");
        return new(state,flag,new DateTimeOffset(DateTime.FromFileTimeUtc((long)time)),"Observed disabled form with the recorded time.");
    }
    private static StartupApprovalMeaning Unknown(string reason)=>new(StartupAppState.Unknown,null,null,reason);

    public static StartupAppsResult Combine(StartupSnapshot run,StartupApprovalSnapshot approval,IReadOnlyList<StartupFolderResult>? folders,Func<string,string> l,CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(run);ArgumentNullException.ThrowIfNull(approval);ArgumentNullException.ThrowIfNull(l);token.ThrowIfCancellationRequested();
        var used=new HashSet<StartupApprovalRecord>(ReferenceEqualityComparer.Instance);var rows=new List<StartupAppRow>();
        var byKey=approval.Records.GroupBy(r=>(Hive(r.Location.Source),r.Location.Area)).ToDictionary(g=>g.Key,g=>g.ToArray());
        bool Incomplete(StartupApprovalLocation wanted)=>approval.Roots.Any(r=>SameKey(r.Location,wanted)&&(r.Issues&~(StartupApprovalRootIssue.Missing|StartupApprovalRootIssue.Duplicates))!=0)
            ||(byKey.TryGetValue((wanted.Source,wanted.Area),out var all)&&all.Any(r=>r.Name is null));
        (StartupApprovalMeaning Meaning,int Matches) Match(string? name,StartupApprovalLocation? wanted)
        {
            token.ThrowIfCancellationRequested();
            if(wanted is not StartupApprovalLocation key)return(new(StartupAppState.NotCovered,null,null,"RunOnce values are not covered by StartupApproved."),0);
            if(name is null)return(Unknown("The startup name is unavailable, so it cannot be matched."),0);
            var found=byKey.TryGetValue((key.Source,key.Area),out var all)?all.Where(r=>r.Name is not null&&string.Equals(r.Name,name,StringComparison.OrdinalIgnoreCase)).ToArray():Array.Empty<StartupApprovalRecord>();
            foreach(var f in found)used.Add(f);
            if(found.Length==0)return Incomplete(key)?(Unknown("No matching StartupApproved value was recorded, but that StartupApproved data is incomplete."),0):(new(StartupAppState.NoEntry,null,null,"No StartupApproved value with this name. Task Manager usually shows such items as enabled; this is not verified."),0);
            var meanings=found.Select(Decode).Distinct().ToArray();
            if(meanings.Length>1)return(Unknown("Different StartupApproved values with this name were recorded in the registry views."),found.Length);
            return(meanings[0],found.Length);
        }
        // Current-user Run is shared by both registry views, so identical rows are listed once.
        var seenUser=new HashSet<(string,bool,string)>();
        foreach(var record in run.Records)
        {
            token.ThrowIfCancellationRequested();var loc=record.Location;
            if(loc.Source is RegistrationSource.User64 or RegistrationSource.User32&&record.Name is not null&&record.Issues==StartupIssue.None&&!seenUser.Add((record.Name,loc.Once,record.Command.State+"\0"+(record.Command.Value??"")+"\0"+record.Type)))continue;
            var wanted=ApprovalFor(loc);var (meaning,matches)=Match(record.Name,wanted);
            rows.Add(new(StartupAppKind.Registry,record.Name,StartupRecords.Origin(loc,l),wanted,meaning,matches));
        }
        bool inspected=folders is not null;
        foreach(var result in folders??Array.Empty<StartupFolderResult>())
        {
            token.ThrowIfCancellationRequested();if(result.Scan is null)continue;var wanted=ApprovalFor(result.Choice.Kind);
            foreach(var node in result.Scan.Root.Children)
            {
                var (meaning,matches)=node.Directory?(new StartupApprovalMeaning(StartupAppState.NotCovered,null,null,"Folders inside a Startup folder are not matched."),0):Match(node.Name,wanted);
                rows.Add(new(StartupAppKind.Folder,node.Name,l(StartupFolders.Name(result.Choice.Kind)),wanted,meaning,matches));
            }
        }
        // StartupApproved values without a recorded item stay visible; identical values from both views are listed once.
        var seenOrphan=new HashSet<(RegistrationSource,StartupApprovalArea,string,string)>();
        foreach(var record in approval.Records)
        {
            token.ThrowIfCancellationRequested();if(used.Contains(record))continue;var key=new StartupApprovalLocation(Hive(record.Location.Source),record.Location.Area);
            if(record.Name is not null&&!seenOrphan.Add((key.Source,key.Area,record.Name.ToUpperInvariant(),record.HexData+"\0"+record.Type+"\0"+record.Issues)))continue;
            var decoded=Decode(record);
            rows.Add(new(StartupAppKind.ApprovalOnly,record.Name,StartupApprovalSnapshotReader.SourceName(record.Location.Source,l)+" · "+l(StartupApprovalSnapshotReader.AreaLabel(record.Location.Area)),key,decoded,1));
        }
        var sources=new List<StartupAppSource>();
        foreach(var root in run.Roots)sources.Add(new(StartupRecords.Origin(root.Location,l),StartupRecords.RootStatus(root.Issues,l)));
        foreach(var root in approval.Roots)sources.Add(new(StartupApprovalSnapshotReader.SourceName(root.Location.Source,l)+" · "+l(StartupApprovalSnapshotReader.AreaLabel(root.Location.Area)),StartupApprovalSnapshotReader.RootStatus(root.Issues,l)));
        foreach(var kind in Enum.GetValues<StartupFolderKind>())
        {
            var result=folders?.FirstOrDefault(r=>r.Choice.Kind==kind);
            sources.Add(new(l(StartupFolders.Name(kind)),result is null?l("Not inspected; its items and matching StartupApproved values are not listed as items."):result.Scan is null?l(result.Issue??StartupFolders.Unavailable):l(result.Scan.Root.Incomplete||result.Scan.EntryLimit||result.Scan.TimeLimit?"Partial":"Counted")));
        }
        token.ThrowIfCancellationRequested();return new(DateTimeOffset.UtcNow,rows.AsReadOnly(),sources.AsReadOnly(),inspected);
    }

    public static string State(StartupAppState state,Func<string,string> l)=>l(state switch{StartupAppState.Enabled=>"Enabled (observed form)",StartupAppState.Disabled=>"Disabled (observed form)",StartupAppState.NoEntry=>"No StartupApproved value",StartupAppState.NotCovered=>"Not covered",StartupAppState.Unknown=>"Unknown",_=>throw new ArgumentOutOfRangeException(nameof(state))});
    public static string Kind(StartupAppKind kind,Func<string,string> l)=>l(kind switch{StartupAppKind.Registry=>"Run registry value",StartupAppKind.Folder=>"Startup-folder item",StartupAppKind.ApprovalOnly=>"StartupApproved value only",_=>throw new ArgumentOutOfRangeException(nameof(kind))});
    public static string Name(StartupAppRow row,Func<string,string> l)=>row.Name is null?l("Name unavailable"):row.Name.Length==0?l("Unnamed registry value"):AppMemory.Visible(row.Name);
    public static string Time(StartupAppRow row,Func<string,string> l)=>row.Meaning.DisabledUtc?.ToString("u",CultureInfo.InvariantCulture)??"—";
    public static string ApprovalKey(StartupApprovalLocation? key,Func<string,string> l)=>key is StartupApprovalLocation k?(k.Source==RegistrationSource.User64?"HKCU":"HKLM")+"\\…\\StartupApproved\\"+StartupApprovalSnapshotReader.AreaName(k.Area):l("None");
    public static string Describe(StartupAppRow row,Func<string,string> l)=>string.Join(Environment.NewLine,
        l("Name")+": "+Name(row,l),l("Kind")+": "+Kind(row.Kind,l),l("Location")+": "+row.Where,l("StartupApproved key")+": "+ApprovalKey(row.Approval,l),
        l("Approval state")+": "+State(row.Meaning.State,l),l("Flag byte")+": "+(row.Meaning.Flag is byte f?"0x"+f.ToString("X2",CultureInfo.InvariantCulture):"—"),
        l("Recorded disabled time (UTC)")+": "+Time(row,l),string.Format(l("Matching StartupApproved values: {0}"),row.Matches),l("Reason")+": "+l(row.Meaning.Reason)+(row.Kind==StartupAppKind.ApprovalOnly?" "+l(OrphanNote):""));
    public static string Header(StartupAppsResult result,Func<string,string> l)=>string.Join(Environment.NewLine,l("Startup Apps"),result.RecordedUtc.ToString("O",CultureInfo.InvariantCulture),
        string.Format(l("Listed rows: {0} (not a count of unique or running apps)"),result.Rows.Count),l(Scope),l(Meaning),l(Privacy),l(result.FoldersInspected?"Startup-folder items were included as you chose.":"Startup folders were not inspected."));
    public static string Report(StartupAppsResult result,StartupAppRow? selected,Func<string,string> l,CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(result);var text=new StringBuilder();int bytes=0;
        void Line(string line){token.ThrowIfCancellationRequested();int count=Encoding.UTF8.GetByteCount(line)+Encoding.UTF8.GetByteCount(Environment.NewLine);if(count>MaxReportBytes-bytes)throw new UserError(ReportLimit);bytes+=count;text.AppendLine(line);}
        Line(Header(result,l));foreach(var source in result.Sources)Line(source.Where+": "+source.Status);
        foreach(var row in selected is null?result.Rows:new[]{selected}){Line("");Line(Describe(row,l));}
        token.ThrowIfCancellationRequested();return text.ToString();
    }
}
