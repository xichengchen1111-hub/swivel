using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Swivel.Core;
using Swivel.Windows;

internal static class SlowMachineNativeChecks
{
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        reject(()=>SlowMachineReader.SampleAsync(new CancellationToken(true)),"Precancelled slow check starts no worker");
        var sample=SlowMachineReader.SampleAsync(default,TimeSpan.FromMilliseconds(20)).GetAwaiter().GetResult();
        check(sample.Processes.Count>0&&sample.Processes.Count<=AppMemory.MaxProcesses,"Real short sample completes with bounded process metadata");
        check(sample.Processes.All(p=>p.CpuShare is null or >=0 and <=100)&&sample.Uptime>=TimeSpan.Zero,"Real CPU shares stay machine percentages, uptime recorded");
        check(sample.CommitUsed is null||sample.CommitLimit>=sample.CommitUsed,"Commit is used against limit, not pagefile usage");
        using(var cancel=new CancellationTokenSource())
        {
            var pending=SlowMachineReader.SampleAsync(cancel.Token,TimeSpan.FromSeconds(5));cancel.Cancel();
            try{pending.GetAwaiter().GetResult();check(false,"Expected cancellation");}catch(OperationCanceledException){check(pending.IsCompleted,"Cancellation joins real process reads and releases all local handles before completion");}
        }
        using(var cancel=new CancellationTokenSource())
        {
            var pending=SlowMachineReader.SampleAsync(cancel.Token,TimeSpan.FromSeconds(5));cancel.CancelAfter(30);
            try{pending.GetAwaiter().GetResult();check(false,"Expected delayed cancellation");}catch(OperationCanceledException){check(pending.IsCompleted,"Delayed cancellation completes and joins a real sample");}
        }
        var app=Application.Current as App??new App(validationHost:true);string previous=app.Prefs.Language;
        try
        {
            foreach(string language in new[]{"en","zh-Hans","zh-Hant","ja"})
            {
                app.Prefs.SetLanguage(language);var dialog=new SlowMachineDialog(app,sample with{Processes=new[]{new SlowProcess(123,"bad\n\u202Ename",50,1000)},MemoryLoad=95,PendingReboot=true,SystemFree=0,SystemTotal=100});
                check(dialog.CurrentReport.Contains("[U+000A]")&&dialog.CurrentReport.Contains("[U+202E]")&&dialog.CurrentReport.Contains(app.L(SlowMachine.Thresholds))&&dialog.CurrentReport.Contains(app.L(SlowMachine.Limits)),"Slow sample report sanitizes process names and carries thresholds and moment limits in every language");dialog.Close();
            }
        }
        finally{app.Prefs.SetLanguage(previous);}
    }
}
