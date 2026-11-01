using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

internal static class TaskFailureHistoryReader
{
    internal const string Channel="Microsoft-Windows-TaskScheduler/Operational";
    internal static ScheduledTaskSnapshot Read(ScheduledTaskSnapshot snapshot,Func<TimeSpan> elapsed,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var histories=new Dictionary<string,TaskFailureHistory>(StringComparer.OrdinalIgnoreCase);
        foreach(var row in snapshot.Tasks)if(row.Path is {} path)histories.TryAdd(path,new());
        if(histories.Count==0)return snapshot;
        var problems=snapshot.Issues.ToList();int omitted=snapshot.OmittedIssues,units=snapshot.RetainedTextUnits;
        bool available=false,limited=false,textLimited=false;
        void Issue(uint? error,string reason)
        {
            if(problems.Count<ScheduledTasks.MaxIssues)problems.Add(new(null,null,ScheduledTaskPhase.History,error,reason));else if(omitted<int.MaxValue)omitted++;
        }
        TimeSpan remaining=ScheduledTasks.SoftTimeLimit-elapsed();
        if(remaining<=TimeSpan.Zero){limited=true;Issue(null,ScheduledTasks.NotRead);}
        else
        {
            using var timeout=new CancellationTokenSource(remaining);
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,timeout.Token);
            try
            {
                // Fixed channel/query only. Never enable the log or interpolate task names into XPath.
                var query=new EventLogQuery(Channel,PathType.LogName,"*[System[Provider[@Name='Microsoft-Windows-TaskScheduler'] and EventID=201 and TimeCreated[timediff(@SystemTime) <= 604800000]]]"){ReverseDirection=true};
                using var reader=new EventLogReader(query);
                using var selector=new EventLogPropertySelector(new[]{"Event/EventData/Data[@Name='TaskName']","Event/EventData/Data[@Name='TaskInstanceId']","Event/EventData/Data[@Name='ResultCode']"});
                using var cancel=linked.Token.Register(()=>{try{reader.CancelReading();}catch(Exception){}});
                for(int index=0;;index++)
                {
                    token.ThrowIfCancellationRequested();remaining=ScheduledTasks.SoftTimeLimit-elapsed();
                    if(remaining<=TimeSpan.Zero||timeout.IsCancellationRequested){limited=true;Issue(null,ScheduledTasks.NotRead);break;}
                    using var record=reader.ReadEvent(remaining+TimeSpan.FromSeconds(2));
                    token.ThrowIfCancellationRequested();
                    available=true;
                    if(record is null)break;
                    if(index==TaskFailureHistory.MaxEvents){Issue(null,"The task action-result history event limit was reached.");break;}
                    if(record is not EventLogRecord log||record.Id!=201||record.ProviderName!="Microsoft-Windows-TaskScheduler"){Issue(null,ScheduledTasks.InvalidValue);continue;}
                    var values=log.GetPropertyValues(selector);
                    long copied=values.OfType<string>().Sum(s=>(long)s.Length);
                    if(copied>ScheduledTasks.MaxTextUnits-units){textLimited=true;Issue(null,ScheduledTasks.NotRead);break;}
                    units+=(int)copied;
                    if(values.Count!=3||values[0] is not string name||name.Length>ScheduledTasks.MaxValueUnits||name.Contains('\0')||!TaskFailureHistory.Instance(values[1],out var id)||!TaskFailureHistory.Code(values[2],out uint code))
                    {Issue(null,ScheduledTasks.InvalidValue);continue;}
                    if(histories.TryGetValue(name,out var history))history.Observe(id,code);
                }
            }
            catch(Exception error)when(error is EventLogException or UnauthorizedAccessException or OperationCanceledException)
            {
                token.ThrowIfCancellationRequested();limited=timeout.IsCancellationRequested||elapsed()>=ScheduledTasks.SoftTimeLimit;
                Issue(limited?null:unchecked((uint)error.HResult),limited?ScheduledTasks.NotRead:ScheduledTasks.ReadFailed);
            }
        }
        token.ThrowIfCancellationRequested();
        var rows=snapshot.Tasks.Select(row=>row with{HistoricalFailureInstances=available&&row.Path is {} path&&histories.TryGetValue(path,out var history)?history.Count:null}).ToArray();
        var reason=snapshot.StopReason==ScheduledTaskStopReason.Completed?(limited?ScheduledTaskStopReason.TimeLimit:textLimited?ScheduledTaskStopReason.TextLimit:snapshot.StopReason):snapshot.StopReason;
        return new(rows,snapshot.Folders,problems,snapshot.Captured,elapsed(),snapshot.FolderAttempts,snapshot.TaskAttempts,units,omitted,reason);
    }
}
