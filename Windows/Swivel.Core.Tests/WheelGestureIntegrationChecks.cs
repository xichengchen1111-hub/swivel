using System;
using System.Linq;
using Swivel.Core;

internal static class WheelGestureIntegrationChecks
{
    internal static void Run(Action<bool,string> check)
    {
        var prefs = new Prefs(); string initial = prefs.Serialize();
        var action = Catalog.Available(prefs, Array.Empty<string>()).Single(x => x.Id == "wheel.gesture");
        check(action.Feature == Feature.Wheel && !action.RequiresFiles && !action.ImageOnly && !action.SingleFile,
            "Gesture control opens without selected files; starting remains a separate window choice");
        check(Catalog.Available(prefs, new[] { "unrelated.txt" }).Any(x => x.Id == action.Id),
            "Unrelated selected paths are not gesture input");
        prefs.SetCustomActions(new[] { action.Id }); prefs.Set(Feature.MyWheel, true);
        var loaded = Prefs.Parse(prefs.Serialize());
        check(loaded.CustomActionIds.SequenceEqual(new[] { action.Id }), "Gesture action ID roundtrips in My Wheel");
        loaded.Set(Feature.Wheel, false);
        check(!Catalog.Offered(loaded).Any(x => x.Id == action.Id), "Wheel off hides gesture entry");
        check(!Catalog.ForCustomWheel(loaded, Array.Empty<string>()).Any(x => x.Id == action.Id), "Custom menu respects Wheel gate");
        check(loaded.CustomActionIds.SequenceEqual(new[] { action.Id }), "Disabling Wheel preserves menu choice without starting a hook");
        prefs.SetCustomActions(Array.Empty<string>()); prefs.Set(Feature.MyWheel, false);
        check(initial == prefs.Serialize(), "No persisted gesture switch, modifier or running session added");
    }
}
