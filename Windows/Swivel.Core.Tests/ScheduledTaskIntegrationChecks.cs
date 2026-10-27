using System;
using System.Linq;
using Swivel.Core;

internal static class ScheduledTaskIntegrationChecks
{
    internal static void Run(Action<bool,string> check)
    {
        var prefs=new Prefs(); string original=prefs.Serialize();
        var action=Catalog.Actions.Single(a=>a.Id=="doctor.scheduledtasks");
        check(action.Feature==Feature.Doctor&&!action.RequiresFiles&&!action.SingleFile&&!action.ImageOnly,
            "Scheduled metadata is an explicit Doctor action without selected-file input");
        check(!Catalog.Available(prefs,Array.Empty<string>()).Contains(action),"Scheduled metadata respects Doctor default off");
        prefs.Set(Feature.Doctor,true);
        check(Catalog.Available(prefs,Array.Empty<string>()).Contains(action)&&Catalog.Available(prefs,new[]{"unrelated.txt"}).Contains(action),
            "Scheduled metadata availability ignores unrelated file selection");
        check(WindowsDoctor.Areas.Single(a=>a.Id=="background").ActionIds.Contains(action.Id),"Background Items exposes scheduled metadata explicitly");
        prefs.SetCustomActions(new[]{action.Id});prefs.Set(Feature.MyWheel,true);
        var loaded=Prefs.Parse(prefs.Serialize());
        check(loaded.CustomActionIds.SequenceEqual(new[]{action.Id}),"Scheduled metadata ID roundtrips in My Wheel");
        loaded.Set(Feature.Doctor,false);
        check(!Catalog.ForCustomWheel(loaded,Array.Empty<string>()).Any(a=>a.Id==action.Id)&&loaded.CustomActionIds.SequenceEqual(new[]{action.Id}),
            "Disabling Doctor hides scheduled metadata while retaining menu choice");
        prefs.SetCustomActions(Array.Empty<string>());prefs.Set(Feature.MyWheel,false);prefs.Set(Feature.Doctor,false);
        check(original==prefs.Serialize(),"Scheduled metadata adds no persisted task data or switch");
    }
}
