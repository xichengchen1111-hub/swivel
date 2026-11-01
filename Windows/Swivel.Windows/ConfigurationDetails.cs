using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Swivel.Core;

namespace Swivel.Windows;

internal static class ConfigurationDetails
{
    internal const string Reveal="Show Configuration Commands, Accounts and Paths in This View and Copies";
    internal static string Service(ServiceRecord row,IReadOnlyList<ServiceRecord> all,Func<string,string> l,bool reveal)
    {
        var lines=new List<string>();
        void Field(string label,string? value,bool sensitive=false)=>lines.Add(l(label)+": "+(sensitive&&!reveal?l("Hidden"):value is null?l("Not reported"):ServiceRecords.Visible(value)));
        var config=row.Configuration;
        Field("Service configuration",l(config is null?"Not reported":row.ConfigurationErrors.Count>0?"Partial":"Reported"));
        Field("Service Win32 Exit Code",row.Win32ExitCode.ToString(CultureInfo.InvariantCulture));Field("Service-specific Exit Code",row.ServiceExitCode.ToString(CultureInfo.InvariantCulture));
        if(config is not null)
        {
            Field("Startup Type",l(ConfigurationDiagnosis.Start(config.StartType))+" ("+config.StartType.ToString(CultureInfo.InvariantCulture)+")");
            Field("Delayed automatic start",config.DelayedAuto.HasValue?l(config.DelayedAuto.Value?"True":"False"):null);
            Field("Service Account",config.Account,true);Field("Service Binary Command",config.Binary,true);
            Field("Service Binary Check",l(ConfigurationDiagnosis.PathState(row.BinaryState)));
            Field("Dependencies",string.Join(" · ",config.Dependencies),true);Field("Service description",config.Description,true);
            Field("Failure Reset Seconds",config.FailureResetSeconds?.ToString(CultureInfo.InvariantCulture));
            Field("Failure Command",config.FailureCommand,true);Field("Reboot Message",config.RebootMessage,true);
            Field("Service failure actions",config.FailureActions is null?null:string.Join(" · ",config.FailureActions.Select(a=>l(FailureType(a.Type))+" ("+a.Type.ToString(CultureInfo.InvariantCulture)+"), "+a.DelayMilliseconds.ToString(CultureInfo.InvariantCulture)+" "+l("milliseconds"))));
        }
        foreach(var error in row.ConfigurationErrors)Field(error.Field,l("Windows Error Code")+" "+error.Error.ToString(CultureInfo.InvariantCulture));
        lines.Add(l("Findings"));foreach(string finding in ConfigurationDiagnosis.Services(row,all))lines.Add(l(finding));
        return string.Join("\n",lines);
    }
    private static string FailureType(uint raw)=>raw switch{0=>"No action",1=>"Restart service",2=>"Restart computer",3=>"Run command",_=>"Unknown"};
    internal static string Task(ScheduledTaskRow row,Func<string,string> l,bool reveal)
    {
        var lines=new List<string>();
        void Field(string label,string? value,bool sensitive=false)=>lines.Add(l(label)+": "+(sensitive&&!reveal?l("Hidden"):value is null?l("Not reported"):ServiceRecords.Visible(value)));
        Field("Next task run",row.NextRun is DateTime next&&next.Year>1900?next.ToString("g",CultureInfo.CurrentCulture):null);
        Field("Observed Non-zero Action-result Instances",row.HistoricalFailureInstances?.ToString(CultureInfo.InvariantCulture));
        Field("Last Result Meaning",row.LastResult is uint code?l(ConfigurationDiagnosis.Result(code)):null);
        Field("Task configuration",row.Configuration is null?l("Not reported"):l("Reported"));
        if(row.Configuration is {} config)
        {
            Field("Trigger Count",config.TriggerCount.ToString(CultureInfo.InvariantCulture));
            foreach(var action in config.Actions)
            {
                Field("Action Type",action.Type);Field("Action Target",action.Path,true);Field("Action Arguments",action.Arguments,true);Field("Action Working Folder",action.WorkingDirectory,true);
                Field("Action Target Check",l(ConfigurationDiagnosis.PathState(action.TargetState)));
            }
            Field("Triggers, Principal and Settings",reveal?string.Join("\n",config.Fields.Select(f=>ServiceRecords.Visible(f.Name)+": "+ServiceRecords.Visible(f.Value))):null,true);
        }
        lines.Add(l("Findings"));foreach(string finding in ConfigurationDiagnosis.Task(row))lines.Add(l(finding));
        return string.Join("\n",lines);
    }
}
