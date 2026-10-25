using System;
using System.Collections.Generic;
using System.Linq;
using Swivel.Core;

internal static class SleepCapabilityChecks
{
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        var fields=SleepCapabilities.Fields;check(fields.Count==12&&fields.Distinct().Count()==12,"Closed sleep capability sample has twelve distinct byte flags");
        reject(()=>((IList<SleepCapability>)fields).Clear(),"Sleep field catalog immutable");reject(()=>SleepCapabilities.Name((SleepCapability)99),"Unknown capability name rejected");
        for(int b=0;b<256;b++)
        {
            var rows=fields.Select(f=>new ReportedSleepFlag(f,(byte)b)).Reverse().ToArray();var snapshot=new SleepCapabilitySnapshot(rows,(byte)b,null,DateTimeOffset.UnixEpoch);rows[0]=new(fields[0],0);
            check(snapshot.Flags.Select(f=>f.Kind).SequenceEqual(fields)&&snapshot.Flags.All(f=>f.Raw==b)&&snapshot.HibernationFileType==b&&snapshot.Error is null&&snapshot.Captured==DateTimeOffset.UnixEpoch,"All raw bytes retained without silent bool normalization; copied stable closed order");
            check(SleepCapabilities.FileType((byte)b)==(b switch{0=>"No Hibernation File Reported",1=>"Reduced Hibernation File Reported",2=>"Full Hibernation File Reported",_=>"Unknown Hibernation-file Type"}),"Unknown hibernation type stays unknown, not full or reduced");
        }
        foreach(uint error in new[]{0u,5u,uint.MaxValue}){var failed=new SleepCapabilitySnapshot(Array.Empty<ReportedSleepFlag>(),null,error,default);check(failed.Flags.Count==0&&failed.HibernationFileType is null&&failed.Error==error,"API failure does not fabricate unsupported flags, including LastError zero");}
        var complete=fields.Select(f=>new ReportedSleepFlag(f,0)).ToArray();
        reject(()=>new SleepCapabilitySnapshot(complete,null,null,default),"Success requires hibernation type even zero");
        reject(()=>new SleepCapabilitySnapshot(complete,0,5,default),"Error must not retain default or stale success fields");
        reject(()=>new SleepCapabilitySnapshot(Array.Empty<ReportedSleepFlag>(),0,0,default),"Error does not expose hibernation file type");
        reject(()=>new SleepCapabilitySnapshot(complete.Take(11),0,null,default),"Success cannot silently omit a supported-state field");
        var duplicate=complete.ToArray();duplicate[11]=duplicate[0];reject(()=>new SleepCapabilitySnapshot(duplicate,0,null,default),"Duplicate flag rejected");duplicate[11]=new((SleepCapability)99,1);reject(()=>new SleepCapabilitySnapshot(duplicate,0,null,default),"Unknown snapshot flag rejected");
        reject(()=>((IList<ReportedSleepFlag>)new SleepCapabilitySnapshot(complete,0,null,default).Flags).Clear(),"Success snapshot immutable");
        var prefs=new Prefs();var action=Catalog.Actions.Single(x=>x.Id=="doctor.sleepstates");check(action.Feature==Feature.Doctor&&!action.RequiresFiles&&!Catalog.Available(prefs,Array.Empty<string>()).Any(x=>x.Id==action.Id),"Sleep capabilities default off, no unrelated files read");
        prefs.Set(Feature.Doctor,true);check(Catalog.Available(prefs,Array.Empty<string>()).Any(x=>x.Id==action.Id)&&WindowsDoctor.Areas.Single(a=>a.Id=="power").ActionIds.Contains(action.Id),"Explicit sleep capability check reachable from power area only after Doctor enabled");
    }
}
