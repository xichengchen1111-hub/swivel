using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Swivel.Core;

internal static class ConfigurationDiagnosisChecks
{
    internal static void Run(Action<bool,string> check, Action<Action,string> reject)
    {
        check(ConfigurationDiagnosis.ServiceTarget("\"C:\\Program Files\\App\\a.exe\" -x", @"C:\Windows").Path == @"C:\Program Files\App\a.exe", "Quoted service executable excludes arguments");
        check(ConfigurationDiagnosis.ServiceTarget(@"C:\Windows\System32\svchost.exe -k netsvcs", @"C:\Windows").Path == @"C:\Windows\System32\svchost.exe", "svchost arguments never become a file target");
        check(ConfigurationDiagnosis.ServiceTarget(@"C:\Program Files\App\a.exe -x", @"C:\Windows") is { Path:null, UnquotedSpaces:true }, "Unquoted space path is visible but never guessed for probing");
        foreach(string path in new[]{@"\SystemRoot\System32\drivers\a.sys", @"%SystemRoot%\System32\drivers\a.sys", @"\??\C:\Windows\System32\drivers\a.sys"})
            check(ConfigurationDiagnosis.ServiceTarget(path, @"C:\Windows").Path == @"C:\Windows\System32\drivers\a.sys", "Driver/system-root/NT prefix lexical normalization");
        foreach(string path in new[]{"", "\"C:\\a.exe", @"%USERPROFILE%\a.exe", @"\\server\a.exe", @"C:\a.exe"+"\0", new string('x',2049), @"C:\a.exeX -k", @"relative.exe", @"C:\name.exe folder\service.exe"})
            check(ConfigurationDiagnosis.ServiceTarget(path,@"C:\Windows").Path is null,"Ambiguous, malformed or unsupported service target stays unknown: "+path);
        check(ConfigurationDiagnosis.ServiceTarget(@"C:\Program Files\driver.sys",@"C:\Windows").UnquotedSpaces&&ConfigurationDiagnosis.ServiceTarget(@"%SystemRoot%\System32\a.exe -k",@"C:\Windows Folder").UnquotedSpaces,"Driver and expanded-system-root spaces remain unprobed");
        check(ConfigurationDiagnosis.ServiceTarget(@"C:\a.exe\b.exe",@"C:\Windows").Path==@"C:\a.exe\b.exe","Executable-named directory is a valid lexical component");
        var dep=new ServiceRecord("dep","",0x10,1,0){Configuration=new(4,"LocalSystem",@"C:\dep.exe",Array.Empty<string>())};
        var row=new ServiceRecord("a","",0x10,1,0){Win32ExitCode=5,Configuration=new(2,@"DOMAIN\user",@"C:\Program Files\a.exe",new[]{"dep","absent","+group"}),BinaryState=ConfigurationPathState.Missing};
        var findings=ConfigurationDiagnosis.Services(row,new[]{row,dep});
        foreach(string rule in new[]{ConfigurationDiagnosis.AutoStopped,ConfigurationDiagnosis.BinaryMissing,ConfigurationDiagnosis.Unquoted,ConfigurationDiagnosis.DependencyDisabled,ConfigurationDiagnosis.DependencyAbsent,ConfigurationDiagnosis.UserAccount})check(findings.Contains(rule),"Service diagnostic fixture covers each rule");
        check(ConfigurationDiagnosis.Services(dep,new[]{dep}).Count==0,"Stopped manual/disabled built-in-account service is not diagnosed as failed automatic startup");
        check(!ConfigurationDiagnosis.Services(row with{State=4},new[]{row,dep}).Contains(ConfigurationDiagnosis.AutoStopped),"Running automatic service has no stopped finding");
        check(!ConfigurationDiagnosis.Services(row with{Win32ExitCode=0,ServiceExitCode=5},new[]{row,dep}).Contains(ConfigurationDiagnosis.AutoStopped),"Zero exit is not a failure");
        foreach(string account in new[]{"LocalSystem",@"NT AUTHORITY\LocalService",@"NT AUTHORITY\NetworkService",@"NT SERVICE\a"})check(!ConfigurationDiagnosis.Services(row with{Configuration=row.Configuration! with{Account=account}},new[]{row,dep}).Contains(ConfigurationDiagnosis.UserAccount),"Built-in and virtual accounts are not user-account findings");
        const string xml="<Task xmlns='http://schemas.microsoft.com/windows/2004/02/mit/task'><Triggers/><Principals><Principal><UserId>PRIVATE</UserId><RunLevel>LeastPrivilege</RunLevel></Principal></Principals><Settings><Hidden>false</Hidden></Settings><Actions><Exec><Command>C:\\missing.exe</Command><Arguments>SECRET</Arguments></Exec><ComHandler><ClassId>PRIVATE</ClassId></ComHandler></Actions></Task>";
        var definition=TaskConfiguration.Parse(xml,CancellationToken.None);
        check(definition.TriggerCount==0&&definition.Actions.Count==2&&definition.Actions[0].Arguments=="SECRET"&&definition.Fields.Any(x=>x.Value=="PRIVATE"),"Task XML preserves bounded actions/principal/settings");
        var task=new ScheduledTaskRow(1,"private",@"\private",3,true,0x80070002){Configuration=definition with{Actions=Array.AsReadOnly(new[]{definition.Actions[0] with{TargetState=ConfigurationPathState.Missing}})}};
        var tf=ConfigurationDiagnosis.Task(task);
        check(tf.Contains(ConfigurationDiagnosis.NoTrigger)&&tf.Contains(ConfigurationDiagnosis.ActionMissing)&&tf.Contains(ConfigurationDiagnosis.LastFailure),"Task no-trigger, missing-target and nonzero-result fixtures");
        check(ConfigurationDiagnosis.Task(task with{Enabled=false}).Contains(ConfigurationDiagnosis.TaskDisabled),"Disabled task finding");
        check(!ConfigurationDiagnosis.Task(task with{LastResult=0}).Contains(ConfigurationDiagnosis.LastFailure),"Success is not failure");
        foreach(uint result in new uint[]{0x41300,0x41301,0x41302,0x41303,0x41304,0x41305,0x41306,0x41307,0x41308,0x4131b,0x4131c,0x41325})check(!ConfigurationDiagnosis.Task(task with{LastResult=result}).Contains(ConfigurationDiagnosis.LastFailure),"Scheduler informational status is not a failure");
        check(ConfigurationDiagnosis.Result(0x80070002)=="The system could not find the file (reported result)."&&ConfigurationDiagnosis.Result(0xdeadbeef)==ConfigurationDiagnosis.UnknownResult,"Closed result decoding never invents an error meaning");
        foreach(string bad in new[]{"<Task/>", "<!DOCTYPE Task [<!ENTITY x 'x'>]><Task>&x;</Task>",xml.Replace("SECRET",new string('x',1025)),xml.Replace("<Triggers/>","<Triggers>"+string.Concat(Enumerable.Repeat("<TimeTrigger/>",33))+"</Triggers>"),new string('x',TaskConfiguration.MaxXmlUnits+1),xml.Replace("<Triggers/>","<Triggers>"+string.Concat(Enumerable.Repeat("<TimeTrigger>",34))+string.Concat(Enumerable.Repeat("</TimeTrigger>",34))+"</Triggers>"),xml.Replace("<Triggers/>","<Settings>"+string.Concat(Enumerable.Repeat("<Value>x</Value>",257))+"</Settings>"),xml.Replace("<Triggers/>","<Triggers/><Triggers/>")})reject(()=>TaskConfiguration.Parse(bad,default),"Hostile/oversized/duplicate task definition rejected");
        reject(()=>TaskConfiguration.Parse(xml,new CancellationToken(true)),"Task parser cancellation real");
        reject(()=>TaskConfiguration.Parse("<Task xmlns='http://schemas.microsoft.com/windows/2004/02/mit/task'><Settings>"+string.Concat(Enumerable.Repeat("<Value>x</Value>",257))+"</Settings></Task>",default),"Task definition leaf-field count bounded");
        reject(()=>TaskConfiguration.Parse("<Task xmlns='http://schemas.microsoft.com/windows/2004/02/mit/task'><Actions>"+string.Concat(Enumerable.Repeat("<Exec/>",33))+"</Actions></Task>",default),"Task definition action count bounded");
        check(!ConfigurationDiagnosis.Task(task with{Enabled=false}).Contains(ConfigurationDiagnosis.NoTrigger),"Disabled task with no trigger is not an enabled-no-trigger finding");
        var dependencies=new[]{"dep"};var failureActions=new[]{new ServiceFailureAction(1,1000)};
        var frozen=new ServiceSnapshot(new[]{row with{Configuration=row.Configuration! with{Dependencies=dependencies,FailureActions=failureActions},ConfigurationErrors=new[]{new ServiceConfigurationError("Service configuration",5)}}},Array.Empty<ServiceReadIssue>(),default);
        dependencies[0]="CHANGED";failureActions[0]=new(3,0);
        check(frozen.Partial&&frozen.Services.Single().Configuration!.Dependencies.Single()=="dep"&&frozen.Services.Single().Configuration!.FailureActions!.Single().Type==1,"Denied per-item row stays visible and snapshot deeply freezes configuration lists");
        reject(()=>new ServiceSnapshot(new[]{row with{Configuration=row.Configuration! with{Dependencies=Enumerable.Repeat("dep",65).ToArray()}}},Array.Empty<ServiceReadIssue>(),default),"Service dependency count cap");
        reject(()=>new ScheduledTaskSnapshot(new[]{task with{Configuration=definition with{Actions=Enumerable.Repeat(definition.Actions[0],33).ToArray()}}},Array.Empty<ScheduledTaskFolder>(),Array.Empty<ScheduledTaskIssue>(),default,TimeSpan.Zero,0,1,1,0,ScheduledTaskStopReason.Completed),"Malformed task record action count rejected before display");
        var history=new TaskFailureHistory();Guid instance=Guid.NewGuid();history.Observe(instance,5);history.Observe(instance,5);history.Observe(Guid.Empty,5);history.Observe(Guid.NewGuid(),0);history.Observe(Guid.NewGuid(),0x41301);
        check(history.Count==1,"Only distinct nonempty failed instance IDs count; repeated events/status/success do not");
        check(TaskFailureHistory.Instance(instance,out var typed)&&typed==instance&&TaskFailureHistory.Instance(instance.ToString(),out var written)&&written==instance&&!TaskFailureHistory.Instance(Guid.Empty,out _)&&!TaskFailureHistory.Instance(new string('x',65),out _),"Native GUID and bounded string GUID events both match distinct instances");
        history.Observe(Guid.NewGuid(),2);
        check(ConfigurationDiagnosis.Task(task with{HistoricalFailureInstances=history.Count}).Contains(ConfigurationDiagnosis.RepeatedFailure)&&!ConfigurationDiagnosis.Task(task with{HistoricalFailureInstances=history.Count,LastResult=0}).Contains(ConfigurationDiagnosis.RepeatedFailure),"Repeated action-result evidence still requires nonzero last task result");
        foreach(object raw in new object[]{2u,2,2L,"2","0x80070002",-1})check(TaskFailureHistory.Code(raw,out _),"Native numeric/hex task result decoded without culture or overflow");
        foreach(object raw in new object[]{long.MaxValue,"-2","18446744073709551616","SECRET",new object()})check(!TaskFailureHistory.Code(raw,out _),"Malformed event code rejected");
        for(int i=0;i<250;i++)history.Observe(Guid.NewGuid(),5);check(history.Count==200,"History evidence cap independent of input volume");
        reject(()=>new ServiceSnapshot(new[]{row with{Configuration=row.Configuration! with{Binary=new string('x',2049)}}},Array.Empty<ServiceReadIssue>(),default),"Oversized service config refused");
    }
}
