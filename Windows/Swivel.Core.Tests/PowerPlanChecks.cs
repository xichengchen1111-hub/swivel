using System;
using System.Collections.Generic;
using System.Linq;
using Swivel.Core;

internal static class PowerPlanChecks
{
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        var settings=PowerPlans.Settings;check(settings.Select(s=>s.Kind).SequenceEqual(Enum.GetValues<IdlePowerSetting>()),"Three power settings have a closed stable order");
        check(settings[0].Subgroup==new Guid("7516b95f-f776-4464-8c53-06167f40cc99")&&settings[0].Setting==new Guid("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e"),"Display timeout GUID pair matches Microsoft published setting");
        check(settings[1].Subgroup==new Guid("238c9fa8-0aad-41ed-83f4-97be242c8f20")&&settings[1].Setting==new Guid("29f6c1db-86da-48c5-9fdb-f2b67b1f44da")&&settings[2].Subgroup==settings[1].Subgroup&&settings[2].Setting==new Guid("9d7815a6-7ee4-497e-8888-515a05f02364"),"Sleep and hibernate GUID pairs match separate Microsoft settings");
        check(PowerPlans.Zero(IdlePowerSetting.Hibernate).Contains("Adaptive")&&!PowerPlans.Zero(IdlePowerSetting.Hibernate).Contains("Never")&&PowerPlans.Zero(IdlePowerSetting.Sleep).Contains("No Idle Sleep")&&PowerPlans.Zero(IdlePowerSetting.DisplayOff).Contains("No Idle Display-off"),"Hibernate zero is adaptive, not disabled like stored sleep/display idle timeout");
        reject(()=>PowerPlans.Name((IdlePowerSetting)99),"Unknown power setting name rejected");reject(()=>PowerPlans.Zero((IdlePowerSetting)99),"Unknown zero-timeout semantics rejected");reject(()=>((IList<IdlePowerDescriptor>)settings).Clear(),"Fixed setting GUID catalog immutable");
        var state=new ReportedPowerState(255,255,255,255,uint.MaxValue,uint.MaxValue);var rows=new[]{new PowerTimeout(IdlePowerSetting.DisplayOff,0,uint.MaxValue,null,null),new PowerTimeout(IdlePowerSetting.Sleep,null,3600,5,null),new PowerTimeout(IdlePowerSetting.Hibernate,0,null,null,2)};
        var scheme=Guid.NewGuid();var snapshot=new PowerPlanSnapshot(state,null,scheme,null,false,123,rows,DateTimeOffset.UnixEpoch);rows[0]=rows[1];
        check(snapshot.Timeouts[0].AcSeconds==0&&snapshot.Timeouts[0].DcSeconds==uint.MaxValue&&snapshot.Timeouts[1].AcSeconds is null&&snapshot.Timeouts[1].AcError==5,"Zero/max unsigned seconds and per-source missing/error states preserved without fallback");
        check(snapshot.State==state&&snapshot.CleanupError==123&&snapshot.Scheme==scheme&&snapshot.Captured==DateTimeOffset.UnixEpoch,"Raw battery sentinel bytes/times/captured GUID/cleanup issue retained");
        reject(()=>((IList<PowerTimeout>)snapshot.Timeouts).Clear(),"Snapshot copies and protects timeout list");
        reject(()=>new PowerPlanSnapshot(null,5,null,2,false,null,new[]{new PowerTimeout((IdlePowerSetting)99,0,0,null,null)},default),"Unknown snapshot setting refused");
        reject(()=>new PowerPlanSnapshot(null,5,null,2,false,null,new[]{new PowerTimeout(IdlePowerSetting.Sleep,0,0,null,null),new PowerTimeout(IdlePowerSetting.Sleep,1,1,null,null)},default),"Duplicate setting snapshot refused");
        reject(()=>new PowerPlanSnapshot(null,5,null,2,false,null,new[]{new PowerTimeout(IdlePowerSetting.Sleep,null,0,null,null)},default),"Missing value without error is not fabricated as zero");
        reject(()=>new PowerPlanSnapshot(null,5,null,2,false,null,new[]{new PowerTimeout(IdlePowerSetting.Sleep,0,0,5,null)},default),"Ambiguous success-and-error snapshot refused");
        var prefs=new Prefs();var action=Catalog.Actions.Single(x=>x.Id=="doctor.power");check(action.Feature==Feature.Doctor&&!action.RequiresFiles&&!Catalog.Available(prefs,Array.Empty<string>()).Any(x=>x.Id==action.Id),"Power check independently default-off and does not need unrelated files");
        prefs.Set(Feature.Doctor,true);check(Catalog.Available(prefs,Array.Empty<string>()).Any(x=>x.Id==action.Id)&&WindowsDoctor.Areas.Single(a=>a.Id=="power").ActionIds.SequenceEqual(new[]{"doctor.power","doctor.sleepstates","doctor.check"}),"Power area retains Home-only basic snapshot and adds explicit dedicated check");
    }
}
