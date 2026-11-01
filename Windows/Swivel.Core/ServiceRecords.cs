using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Swivel.Core;

public sealed record ServiceRecord(string Name,string DisplayName,uint Type,uint State,uint ProcessId)
{
    public uint Win32ExitCode { get; init; }
    public uint ServiceExitCode { get; init; }
    public ServiceConfiguration? Configuration { get; init; }
    public IReadOnlyList<ServiceConfigurationError> ConfigurationErrors { get; init; } = Array.Empty<ServiceConfigurationError>();
    public ConfigurationPathState BinaryState { get; init; }
}
public enum ServiceIssuePhase { Open, Enumerate, BufferLimit, RecordLimit, InvalidRecord, DuplicateName, NameBudget, Close, ConfigurationLimit }
public sealed record ServiceReadIssue(ServiceIssuePhase Phase,uint? Error=null,int? Record=null,uint? NeededBytes=null,uint? ReportedRecords=null);
public sealed class ServiceSnapshot
{
    public IReadOnlyList<ServiceRecord> Services{get;}public IReadOnlyList<ServiceReadIssue> Issues{get;}
    public DateTimeOffset Captured{get;}public bool Partial=>Issues.Count!=0||Services.Any(s=>s.ConfigurationErrors.Count!=0);
    public ServiceSnapshot(IEnumerable<ServiceRecord> services,IEnumerable<ServiceReadIssue> issues,DateTimeOffset captured)
    {
        var rows=services.Take(ServiceRecords.MaxRecords+1).ToArray();var skipped=issues.Take(ServiceRecords.MaxRecords+5).ToArray();
        if(rows.Length>ServiceRecords.MaxRecords||skipped.Length>ServiceRecords.MaxRecords+4||rows.Any(x=>string.IsNullOrEmpty(x.Name)||x.Name.Length>ServiceRecords.MaxName||x.DisplayName is null||x.DisplayName.Length>ServiceRecords.MaxName)||rows.Sum(x=>x.Name.Length+x.DisplayName.Length)>ServiceRecords.MaxNameUnits||rows.Select(x=>x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=rows.Length||skipped.Any(x=>!Enum.IsDefined(x.Phase)||x.Record is int n&&(n<1||n>ServiceRecords.MaxRecords)))throw new ArgumentException("Invalid service snapshot.");
        if(rows.Any(x=>!Enum.IsDefined(x.BinaryState)||x.ConfigurationErrors.Count>8||x.ConfigurationErrors.Any(e=>e.Field.Length>128||e.Field.Contains('\0'))||x.Configuration is {} c&&!Valid(c))||rows.Sum(x=>Units(x.Configuration))>ServiceRecords.MaxNameUnits)throw new ArgumentException("Invalid service configuration.");
        rows=rows.Select(x=>x with{ConfigurationErrors=Array.AsReadOnly(x.ConfigurationErrors.ToArray()),Configuration=x.Configuration is {} c?c with{Dependencies=Array.AsReadOnly(c.Dependencies.ToArray()),FailureActions=c.FailureActions is {} a?Array.AsReadOnly(a.ToArray()):null}:null}).ToArray();
        Services=Array.AsReadOnly(rows.OrderBy(x=>x.Name,StringComparer.OrdinalIgnoreCase).ThenBy(x=>x.Name,StringComparer.Ordinal).ToArray());Issues=Array.AsReadOnly(skipped);Captured=captured;
    }
    public static int Units(ServiceConfiguration? c)=>c is null?0:c.Account.Length+c.Binary.Length+c.Dependencies.Sum(d=>d.Length)+(c.Description?.Length??0)+(c.FailureCommand?.Length??0)+(c.RebootMessage?.Length??0);
    private static bool Valid(ServiceConfiguration c)=>c.Account.Length<=2048&&!c.Account.Contains('\0')&&c.Binary.Length<=2048&&!c.Binary.Contains('\0')&&c.Dependencies.Count<=64&&c.Dependencies.All(d=>d.Length<=1024&&!d.Contains('\0'))&&c.Description?.Length is not >2048&&c.FailureCommand?.Length is not >2048&&c.RebootMessage?.Length is not >2048&&c.FailureActions?.Count is not >64;
}
public static class ServiceRecords
{
    public const int MaxRecords=4096,MaxName=1024,MaxNameUnits=256*1024;
    public const string Scope="One explicit read-only local Windows service-control-manager sample, limited to Win32 services in any reported state. Driver services are excluded. Readable startup type, delayed start, account, binary command, dependencies, description and failure actions are requested without changing them. Names can identify private software; only an explicit Copy Report writes them to the clipboard. Nothing is sent to an assistant.";
    public const string Limits="One 256 KiB native enumeration buffer, at most 4,096 records and 1,024 UTF-16 units per name and 262,144 copied name units overall. Configuration adds a separate 262,144-unit text budget, 8 KiB per native buffer, 2,048 units per field, 1,024 per dependency, 64 dependencies/failure actions per service and about 15 seconds. More-data, malformed records and API or cleanup errors stay visible as partial results. Windows silently omits services for which this user lacks query-status access, so even a successful empty result is not a complete inventory. Names and reported state/PID are historical, not trust, ownership, enabled-startup, slowdown or safe-removal evidence. A stopped service is not necessarily disabled; PIDs can be reused and do not establish app groups. Calls may block and cancellation waits for cleanup. No service is started, stopped, disabled, deleted or reconfigured; no administrator command, file scan, network test or settings change is requested.";
    public static string State(uint raw)=>raw switch{1=>"Stopped (reported)",2=>"Start Pending (reported)",3=>"Stop Pending (reported)",4=>"Running (reported)",5=>"Continue Pending (reported)",6=>"Pause Pending (reported)",7=>"Paused (reported)",_=>"Unknown Service State"};
    public static string Phase(ServiceIssuePhase phase)=>phase switch{ServiceIssuePhase.ConfigurationLimit=>"Service Configuration Limit Reached",ServiceIssuePhase.Open=>"Service Manager Open Failed",ServiceIssuePhase.Enumerate=>"Service Enumeration Failed",ServiceIssuePhase.BufferLimit=>"Service Enumeration Buffer Limit",ServiceIssuePhase.RecordLimit=>"Service Record Limit Reached",ServiceIssuePhase.InvalidRecord=>"Invalid Service Record Skipped",ServiceIssuePhase.DuplicateName=>"Duplicate Service Name Skipped",ServiceIssuePhase.NameBudget=>"Copied Service-name Budget Reached",ServiceIssuePhase.Close=>"Service Manager Handle Cleanup Failed",_=>throw new ArgumentOutOfRangeException(nameof(phase))};
    public static string Visible(string value)
    {
        var result=new StringBuilder();
        for(int i=0;i<value.Length;i++)
        {
            char c=value[i];Rune rune;
            if(char.IsHighSurrogate(c)&&i+1<value.Length&&char.IsLowSurrogate(value[i+1]))rune=new Rune(c,value[++i]);
            else if(char.IsSurrogate(c)){result.Append("[U+").Append(((int)c).ToString("X4",CultureInfo.InvariantCulture)).Append(']');continue;}
            else rune=new Rune(c);
            var category=Rune.GetUnicodeCategory(rune);if(category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)result.Append("[U+").Append(rune.Value.ToString("X4",CultureInfo.InvariantCulture)).Append(']');else result.Append(rune.ToString());
        }
        return result.ToString();
    }
}
