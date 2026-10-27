using System;
using System.Linq;
using Swivel.Core;

internal static class MoveIntegrationChecks
{
    internal static void Run(Action<bool,string> check, Action<Action,string> reject)
    {
        var prefs = new Prefs(); string before = prefs.Serialize();
        check(!prefs.IsOn(Feature.FileMoves) && !before.Contains("FileMoves",StringComparison.Ordinal), "Move defaults off without adding saved fields");
        var selected = new[] { @"C:\from\one.txt", @"C:\from\two.txt" };
        check(!Catalog.Available(prefs,selected).Any(x=>x.Id=="file.move"), "Move is absent until independently enabled");
        prefs.Set(Feature.FileMoves,true);
        var offered = Catalog.Available(prefs,selected).Single(x=>x.Id=="file.move");
        check(offered.Feature==Feature.FileMoves && offered.RequiresFiles && !offered.SingleFile, "Move preserves explicit multi-file action scope");
        check(!Catalog.Available(prefs,Array.Empty<string>()).Any(x=>x.Id=="file.move"), "Move never substitutes files for an empty selection");
        prefs.SetCustomActions(new[]{"file.move"}); prefs.Set(Feature.MyWheel,true);
        var restored = Prefs.Parse(prefs.Serialize());
        check(restored.IsOn(Feature.FileMoves) && restored.CustomActionIds.Single()=="file.move", "Independent move choice and stable menu ID survive save/reload");
        restored.Set(Feature.FileMoves,false);
        check(!Catalog.ForCustomWheel(restored,selected).Any(x=>x.Id=="file.move") && restored.CustomActionIds.Single()=="file.move", "Disabling move hides its menu entry without deleting the choice");
        check(!restored.IsOn(Feature.BatchRename) && !restored.IsOn(Feature.Archives), "Move does not enable rename or archive tools");
        prefs.Set(Feature.FileMoves,false); prefs.Set(Feature.MyWheel,false); prefs.SetCustomActions(Array.Empty<string>());
        check(prefs.Serialize()==before, "Turning move off and clearing the explicitly changed menu keeps previous settings bytes");
        reject(()=>MoveRules.Validate(new[]{new MovePair(@"C:\from\a.txt",@"C:\to\a.txt"),new MovePair(@"C:\from\A.txt",@"C:\to\A.txt")}), "Explicit case-distinct selections are refused together instead of silently losing one file");
    }
}
