using System;
using System.Collections.Generic;
using System.Linq;
using Swivel.Core;

internal static class WindowsDoctorChecks
{
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        var areas=WindowsDoctor.Areas;
        check(areas.Select(a=>a.Id).SequenceEqual(new[]{"summary","storage","caches","folders","duplicates","projects","performance","app","uninstall","drives","network","background","power"}),"Windows Doctor preserves all 13 Mac symptom areas as static Windows navigation");
        check(areas.Select(a=>a.Id).Distinct().Count()==13&&areas.Count(a=>a.Status==DoctorAreaStatus.Planned)==0,"Area IDs stable/unique; all 13 areas have explicitly limited read-only entries");
        var ids=areas.SelectMany(a=>a.ActionIds).Distinct().ToArray();
        var doctor=Catalog.Actions.Where(a=>a.Feature==Feature.Doctor).Select(a=>a.Id).ToArray();
        check(ids.Where(id=>id!="doctor.check").Order().SequenceEqual(doctor.Order()),"Every existing canonical Doctor action is reachable without adding hidden actions");
        check(!Catalog.Actions.Any(a=>a.Id=="doctor.check")&&WindowsDoctor.ResolveAction("doctor.check").Id=="doctor.check","Basic snapshot remains a Home-only entry, preserving My Wheel and assistant offered IDs");
        foreach(var area in areas)
        {
            check(!string.IsNullOrWhiteSpace(area.Title)&&!string.IsNullOrWhiteSpace(area.Description)&&Enum.IsDefined(area.Status),"Every symptom has visible status and limits");
            check(area.Status==DoctorAreaStatus.Planned?area.ActionIds.Count==0:area.ActionIds.Count>0,"Pending areas have no executable buttons");
            foreach(string id in area.ActionIds)check(WindowsDoctor.ResolveAction(id).Feature==Feature.Doctor&&!WindowsDoctor.ResolveAction(id).RequiresFiles,"Area routes only existing explicit-picker Doctor actions and Home snapshot");
        }
        reject(()=>((IList<WindowsDoctorArea>)areas).Clear(),"Shared Doctor area catalog cannot be mutated");
        reject(()=>((IList<string>)areas[0].ActionIds).Add("file.copy"),"Shared area action list cannot be mutated");
        var prefs=new Prefs();string before=prefs.Serialize();_ = WindowsDoctor.Areas[0].Description;
        check(prefs.Serialize()==before&&!prefs.IsOn(Feature.Doctor),"Building static Doctor navigation leaves switches/settings unchanged");
        var fact=new DoctorFact("System","{0}",new object[]{"snapshot"});
        foreach(string language in new[]{"en","zh-Hans","zh-Hant","ja"})
        {
            string report=DoctorReport.CopyText(new[]{fact},false,language,DateTimeOffset.UnixEpoch);
            check(report.StartsWith("Swivel · "+Strings.L("Windows Doctor",language)),"Visible report branding is Windows Doctor in all four languages");
        }
    }
}
