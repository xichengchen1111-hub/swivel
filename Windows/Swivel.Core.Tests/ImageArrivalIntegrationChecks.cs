using System;
using System.Linq;
using Swivel.Core;

internal static class ImageArrivalIntegrationChecks
{
    internal static void Run(Action<bool,string> check)
    {
        var prefs = new Prefs(); string before = prefs.Serialize();
        check(!prefs.IsOn(Feature.Clothesline) && !Catalog.Available(prefs,Array.Empty<string>()).Any(x=>x.Id=="clothesline.watch"), "Arrival window unavailable by default");
        prefs.Set(Feature.Clothesline,true); prefs.Set(Feature.Wheel,false);
        var action = Catalog.Available(prefs,Array.Empty<string>()).Single(x=>x.Id=="clothesline.watch");
        check(action.Feature==Feature.Clothesline && !action.RequiresFiles && !action.SingleFile && !action.ImageOnly, "Folder choice remains explicit and independent of Wheel or selected files");
        check(Catalog.Available(prefs,new[]{"unrelated.txt"}).Any(x=>x.Id==action.Id), "Unrelated file selection does not become watch input");
        prefs.SetCustomActions(new[]{action.Id}); prefs.Set(Feature.MyWheel,true);
        var roundtrip = Prefs.Parse(prefs.Serialize());
        check(roundtrip.CustomActionIds.Single()==action.Id && roundtrip.IsOn(Feature.Clothesline), "Explicit saved arrival action roundtrips");
        roundtrip.Set(Feature.Clothesline,false);
        check(!Catalog.ForCustomWheel(roundtrip,Array.Empty<string>()).Any(x=>x.Id==action.Id), "Disabled Clothesline hides arrival action");
        check(roundtrip.CustomActionIds.Single()==action.Id, "Disable preserves explicit menu choice without starting a session");
        prefs.Set(Feature.Clothesline,false); prefs.Set(Feature.Wheel,true); prefs.Set(Feature.MyWheel,false); prefs.SetCustomActions(Array.Empty<string>());
        check(prefs.Serialize()==before, "No additional saved watcher feature, folder or session state");
    }
}
