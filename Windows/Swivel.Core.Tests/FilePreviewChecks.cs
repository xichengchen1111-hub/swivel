using System;
using System.Linq;
using Swivel.Core;

internal static class FilePreviewChecks
{
    public static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        var prefs=new Prefs();var action=Catalog.Actions.Single(x=>x.Id=="file.preview");
        check(action.Feature is null&&action.SingleFile&&action.RequiresFiles&&!action.ImageOnly,"Explicit preview is independent, single-file and non-image");
        check(action.Extensions!.Order().SequenceEqual(DocumentText.Extensions.Append("wav").Order()),"Non-image preview accepts only existing closed document types plus WAV");
        foreach(string extension in DocumentText.Extensions.Append("wav"))foreach(string suffix in new[]{extension,extension.ToUpperInvariant()})
        {
            check(Catalog.Available(prefs,new[]{"chosen."+suffix}).Any(x=>x.Id==action.Id),"Supported explicit preview extension is offered");
            check(!Catalog.Available(prefs,new[]{"a."+suffix,"b."+suffix}).Any(x=>x.Id==action.Id),"Multiple references never trigger bulk preview reads");
        }
        foreach(string extension in new[]{"png","jpg","gif","pdf","exe","ppt","mp3","flac","aac","zip","lnk",""})
            check(!Catalog.Available(prefs,new[]{"chosen."+extension}).Any(x=>x.Id==action.Id),"Unsupported/non-document types have no generic non-image preview");
        check(!Catalog.Available(prefs,Array.Empty<string>()).Any(x=>x.Id==action.Id),"No-file preview cannot be offered");
        prefs.SetCustomActions(new[]{action.Id});prefs.Set(Feature.MyWheel,true);var restored=Prefs.Parse(prefs.Serialize());
        check(restored.CustomActionIds.SequenceEqual(new[]{action.Id})&&Catalog.ForCustomWheel(restored,new[]{"explicit.txt"}).Single().Id==action.Id,"Preview has a stable optional My Wheel action identity");
        reject(()=>prefs.SetCustomActions(new[]{"file.preview.unknown"}),"Preview uses the existing closed action schema");
    }
}
