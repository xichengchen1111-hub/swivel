using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Swivel.Core;

public enum ConfigurationPathState { Unknown, Present, Missing }
public sealed record ConfigurationTarget(string? Path, bool UnquotedSpaces = false);
public sealed record ConfigurationField(string Name, string Value);
public sealed record ServiceConfiguration(uint StartType,string Account,string Binary,IReadOnlyList<string> Dependencies)
{
    public bool? DelayedAuto { get; init; }
    public string? Description { get; init; }
    public uint? FailureResetSeconds { get; init; }
    public string? FailureCommand { get; init; }
    public string? RebootMessage { get; init; }
    public IReadOnlyList<ServiceFailureAction>? FailureActions { get; init; }
}
public sealed record ServiceFailureAction(uint Type,uint DelayMilliseconds);
public sealed record ServiceConfigurationError(string Field,uint Error);
public sealed record TaskActionConfiguration(string Type,string? Path,string? Arguments,string? WorkingDirectory)
{
    public ConfigurationPathState TargetState { get; init; }
}
public sealed record TaskConfiguration(int TriggerCount,IReadOnlyList<TaskActionConfiguration> Actions,IReadOnlyList<ConfigurationField> Fields)
{
    public string Fingerprint { get; init; } = "";
    public const int MaxXmlUnits=64*1024,MaxFields=256;
    public static TaskConfiguration Parse(string xml,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(xml.Length>MaxXmlUnits)throw new ScheduledTaskValueException();
        using var reader=XmlReader.Create(new StringReader(xml),new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=MaxXmlUnits});
        var document=new XDocument();var stack=new Stack<XElement>();int elements=0;
        while(reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if(reader.Depth>32||++elements>8192)throw new ScheduledTaskValueException();
            if(reader.NodeType==XmlNodeType.Element)
            {
                var element=new XElement(XName.Get(reader.LocalName,reader.NamespaceURI));
                if(stack.Count==0)document.Add(element);else stack.Peek().Add(element);
                if(!reader.IsEmptyElement)stack.Push(element);
            }
            else if(reader.NodeType==XmlNodeType.EndElement)stack.Pop();
            else if(reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace)
            {if(stack.Count>0)stack.Peek().Add(new XText(reader.Value));}
        }
        XNamespace ns="http://schemas.microsoft.com/windows/2004/02/mit/task";
        var root=document.Root;if(root?.Name!=ns+"Task")throw new ScheduledTaskValueException();
        foreach(string section in new[]{"Triggers","Actions","Principals","Settings"})if(root.Elements(ns+section).Count()>1)throw new ScheduledTaskValueException();
        var triggers=root.Element(ns+"Triggers")?.Elements().ToArray()??Array.Empty<XElement>();
        var actions=root.Element(ns+"Actions")?.Elements().ToArray()??Array.Empty<XElement>();
        if(triggers.Length>32||actions.Length>32)throw new ScheduledTaskValueException();
        string Bound(string text){if(text.Length>ScheduledTasks.MaxValueUnits||text.Contains('\0'))throw new ScheduledTaskValueException();return text;}
        var fields=new List<ConfigurationField>();
        foreach(string section in new[]{"Triggers","Principals","Settings"})
        foreach(var element in root.Element(ns+section)?.Descendants()??Enumerable.Empty<XElement>())
        {
            token.ThrowIfCancellationRequested();
            if(element.Name.Namespace!=ns)throw new ScheduledTaskValueException();
            if(!element.HasElements)
            {
                if(fields.Count==MaxFields)throw new ScheduledTaskValueException();
                fields.Add(new(Bound(string.Join("/",element.AncestorsAndSelf().Reverse().Skip(1).Select(e=>e.Name.LocalName))),Bound(element.Value)));
            }
        }
        string? Value(XElement element,string name)
        {var values=element.Elements(ns+name).ToArray();if(values.Length>1)throw new ScheduledTaskValueException();return values.Length==0?null:Bound(values[0].Value);}
        var output=new List<TaskActionConfiguration>();
        foreach(var action in actions)
        {
            token.ThrowIfCancellationRequested();if(action.Name.Namespace!=ns)throw new ScheduledTaskValueException();
            output.Add(new(Bound(action.Name.LocalName),action.Name==ns+"Exec"?Value(action,"Command"):null,action.Name==ns+"Exec"?Value(action,"Arguments"):null,action.Name==ns+"Exec"?Value(action,"WorkingDirectory"):null));
        }
        return new(triggers.Length,Array.AsReadOnly(output.ToArray()),Array.AsReadOnly(fields.ToArray())){Fingerprint=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(xml)))};
    }
    public static int Units(TaskConfiguration value)=>value.Actions.Sum(a=>a.Type.Length+(a.Path?.Length??0)+(a.Arguments?.Length??0)+(a.WorkingDirectory?.Length??0))+value.Fields.Sum(f=>f.Name.Length+f.Value.Length);
    public static bool Valid(TaskConfiguration value)=>value.Fingerprint.Length<=64&&value.TriggerCount is >=0 and <=32&&value.Actions.Count<=32&&value.Fields.Count<=MaxFields&&
        value.Actions.All(a=>Text(a.Type)&&Text(a.Path)&&Text(a.Arguments)&&Text(a.WorkingDirectory)&&Enum.IsDefined(a.TargetState))&&value.Fields.All(f=>Text(f.Name)&&Text(f.Value));
    private static bool Text(string? value)=>value is null||value.Length<=1024&&!value.Contains('\0');
    public TaskConfiguration Freeze()=>this with{Actions=Array.AsReadOnly(Actions.ToArray()),Fields=Array.AsReadOnly(Fields.ToArray())};
}

/// Pure observations only. Every message is fixed text: paths, arguments and account names cannot leak through findings.
public static class ConfigurationDiagnosis
{
    public const string AutoStopped="Automatic-start service is not running and reports a non-zero exit code. This sample does not establish why.";
    public const string BinaryMissing="The parsed service binary was not found during the read-only local path check.";
    public const string Unquoted="The unquoted service command contains spaces and may contain a spaced executable path. The executable target is ambiguous and was not probed.";
    public const string DependencyAbsent="A named service dependency was not in this sample; it may be missing, unreadable or omitted.";
    public const string DependencyDisabled="A named service dependency reports Disabled startup.";
    public const string UserAccount="The configured service account is a user or managed account rather than a recognized built-in or virtual service account. This does not establish ownership or risk.";
    public const string NoTrigger="The task is enabled but its readable definition contains no triggers. It can still be started on demand or by another task.";
    public const string ActionMissing="A task executable action target was not found during the read-only local path check.";
    public const string TaskDisabled="The task reports that it is disabled.";
    public const string LastFailure="The task reports a non-zero last result outside the known informational status range. One result cannot establish repeated failures.";
    public const string RepeatedFailure="At least two distinct task instances report non-zero action-completion results in the bounded history sample, and the last task result is non-zero. Application-defined codes may have other meanings; this does not prove every run failed.";
    public const string UnknownResult="Unrecognized or application-defined result; no meaning inferred.";
    public const string Limits="Configuration is read only with current permissions. Commands, arguments, account names, paths, descriptions and definition values are hidden until revealed, including in copies. Names remain visible and may be private. File checks use only local fixed-drive metadata with held non-reparse ancestors; inaccessible, cloud, network, relative, ambiguous and unsupported paths stay unknown. Only SystemRoot is resolved using the Windows directory; other environment variables are not guessed. No PATH search, payload inspection or command execution. A single historical result cannot prove repeated failures; only up to 200 action-completion events from the last seven days are read without enabling logging. Distinct instance IDs with non-zero codes are counted, not assumed consecutive failures. Missing/disabled history, limits and log rotation can omit evidence. History text shares the scan budget. Definitions are limited to 64 Ki UTF-16 units, 32 triggers/actions, 256 leaf fields and XML depth 32. Results are non-atomic and are not trust, malware, ownership or removal advice. No service or task is changed and this snapshot is not sent to an assistant.";
    public static ConfigurationTarget ServiceTarget(string? command,string windowsDirectory)
    {
        if(string.IsNullOrWhiteSpace(command)||command.Length>2048||command.Any(c=>char.IsControl(c)||char.GetUnicodeCategory(c)==UnicodeCategory.Format))return new(null);
        string text=command.Trim();string target;
        if(text[0]=='"')
        {
            int end=text.IndexOf('"',1);if(end<0||end+1<text.Length&&!char.IsWhiteSpace(text[end+1]))return new(null);
            target=text[1..end];
        }
        else
        {
            int end=text.IndexOfAny([' ','\t']);if(end<0)end=text.Length;
            target=text[..end];
            string tail=text[end..].TrimStart();
            if(tail.Length>0&&tail[0] is not '-' and not '/'&&(tail.Contains(".exe",StringComparison.OrdinalIgnoreCase)||tail.Contains(".sys",StringComparison.OrdinalIgnoreCase)))return new(null,true);
            if(!target.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)&&!target.EndsWith(".sys",StringComparison.OrdinalIgnoreCase))
            {
                // Detect only a clearly delimited extension before arguments; never select a guessed candidate.
                int extension=text.IndexOf(".exe",StringComparison.OrdinalIgnoreCase);
                if(extension<0)extension=text.IndexOf(".sys",StringComparison.OrdinalIgnoreCase);
                bool spaces=extension>=0&&(extension+4==text.Length||char.IsWhiteSpace(text[extension+4]))&&text[..extension].Contains(' ');
                return new(null,spaces);
            }
        }
        string? resolved=LocalTarget(target,windowsDirectory);
        if(text[0]!='"'&&resolved?.Contains(' ')==true)return new(null,true);
        return new(resolved);
    }
    public static string? LocalTarget(string? value,string windowsDirectory)
    {
        if(string.IsNullOrWhiteSpace(value)||value.Length>2048)return null;
        string text=value;
        if(text.Length>=2&&text[0]=='"'&&text[^1]=='"')text=text[1..^1];
        if(text.StartsWith(@"\??\",StringComparison.Ordinal))text=text[4..];
        if(text.StartsWith(@"\SystemRoot\",StringComparison.OrdinalIgnoreCase))text=windowsDirectory+text[11..];
        if(text.StartsWith("%SystemRoot%\\",StringComparison.OrdinalIgnoreCase))text=windowsDirectory+text[12..];
        if(text.Contains('%'))return null;
        try{return WindowsPathPolicy.Validate(text);}catch(UserError){return null;}
    }
    public static IReadOnlyList<string> Services(ServiceRecord row,IReadOnlyList<ServiceRecord> all)
    {
        var result=new List<string>();var config=row.Configuration;if(config is null)return result;
        if(config.StartType==2&&row.State!=4&&row.Win32ExitCode!=0)result.Add(AutoStopped);
        if(row.BinaryState==ConfigurationPathState.Missing)result.Add(BinaryMissing);
        if(ServiceTarget(config.Binary,@"C:\Windows").UnquotedSpaces)result.Add(Unquoted);
        var names=new Dictionary<string,ServiceRecord>(StringComparer.OrdinalIgnoreCase);foreach(var service in all)names.TryAdd(service.Name,service);
        foreach(string dependency in config.Dependencies.Where(d=>!d.StartsWith('+')))
        {names.TryGetValue(dependency,out var match);if(match is null){if(!result.Contains(DependencyAbsent))result.Add(DependencyAbsent);}else if(match.Configuration?.StartType==4&&!result.Contains(DependencyDisabled))result.Add(DependencyDisabled);}
        string account=config.Account;
        if(account.Length>0&&!new[]{"LocalSystem",@"NT AUTHORITY\SYSTEM",@"NT AUTHORITY\LocalService",@"NT AUTHORITY\NetworkService",@"NT AUTHORITY\LOCAL SERVICE",@"NT AUTHORITY\NETWORK SERVICE"}.Contains(account,StringComparer.OrdinalIgnoreCase)&&!account.StartsWith(@"NT SERVICE\",StringComparison.OrdinalIgnoreCase))result.Add(UserAccount);
        return result.AsReadOnly();
    }
    public static IReadOnlyList<string> Task(ScheduledTaskRow row)
    {
        var result=new List<string>();
        if(row.Enabled==false)result.Add(TaskDisabled);
        if(row.Enabled==true&&row.Configuration?.TriggerCount==0)result.Add(NoTrigger);
        if(row.Configuration?.Actions.Any(a=>a.TargetState==ConfigurationPathState.Missing)==true)result.Add(ActionMissing);
        if(row.LastResult is uint code&&Failure(code))result.Add(LastFailure);
        if(row.HistoricalFailureInstances>=2&&row.LastResult is uint last&&Failure(last))result.Add(RepeatedFailure);
        return result.AsReadOnly();
    }
    public static bool Failure(uint code)=>code!=0&&code is not (>=0x41300 and <=0x413ff);
    public static string Result(uint code)=>code switch{
        0=>"The task reports a zero result.",0x41300=>"Task is ready (reported status).",0x41301=>"Task is running (reported status).",0x41302=>"Task is disabled (reported status).",0x41303=>"Task has not yet run (reported status).",0x41304=>"No more runs are scheduled (reported status).",0x41305=>"Task is not scheduled (reported status).",0x41306=>"Task was terminated (reported status).",0x41307=>"Task has no valid triggers (reported status).",0x41308=>"Event trigger has no scheduled run time (reported status).",
        2 or 0x80070002=>"The system could not find the file (reported result).",3 or 0x80070003=>"The system could not find the path (reported result).",5 or 0x80070005=>"Access was denied (reported result).",0x8007052e=>"Logon failed (reported result).",0x8004130f=>"Task account information is not set (reported result).",0x8004131f=>"An instance is already running (reported result).",0x80041326=>"Task is disabled (reported result).",_=>UnknownResult};
    public static string Start(uint raw)=>raw switch{0=>"Boot",1=>"System",2=>"Automatic",3=>"Manual",4=>"Disabled",_=>"Unknown"};
    public static string PathState(ConfigurationPathState state)=>state switch{ConfigurationPathState.Present=>"Present (local metadata)",ConfigurationPathState.Missing=>"Missing (local metadata)",_=>"Unknown or not checked"};
}
