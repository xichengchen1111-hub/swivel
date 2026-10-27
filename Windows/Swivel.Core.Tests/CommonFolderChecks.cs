using System;
using System.Collections.Generic;
using System.Linq;
using Swivel.Core;

internal static class CommonFolderChecks
{
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        check(CommonFolders.Kinds.SequenceEqual(new[]{CommonFolderKind.Desktop,CommonFolderKind.Downloads,CommonFolderKind.Documents,CommonFolderKind.Pictures,CommonFolderKind.Music,CommonFolderKind.Videos}),"Six fixed per-user location kinds and order");check(CommonFolders.DownloadsId==new Guid("374de290-123f-4565-9164-39c4925e467b"),"Downloads known-folder GUID matches Microsoft definition");reject(()=>((IList<CommonFolderKind>)CommonFolders.Kinds).Clear(),"Location catalog immutable");reject(()=>CommonFolders.Name((CommonFolderKind)99),"Unknown common-folder name refused");
        var normal=ReportedFolderPath.FromReported("C:\\Users\\Person\\Documents\\");check(normal.Path=="C:\\Users\\Person\\Documents"&&normal.CanAttempt&&normal.Issue is null,"Reported location removes one final separator, not path canonicalization");
        foreach(string? path in new string?[]{null,"","C:\\","C:\\Users\\Person\\Documents\\\\","C:\\Users\\..\\Documents","\\\\server\\share","C:\\Users\\x:stream","C:\\Users\\\ud800","C:\\Users\\\U000e0020"})check(!ReportedFolderPath.FromReported(path).CanAttempt,"Invalid or unavailable reported location never becomes a scan target");
        var full=CommonFolderChoice.FromReported(CommonFolderKind.Documents,"C:\\Users\\Person\\😀");check(full.CanAttempt&&!full.Truncated&&full.NativeError is null&&full.ReportedPath.EndsWith("😀"),"Ordinary Unicode remains usable when source is complete and lexical checks pass");
        foreach(var kind in CommonFolders.Kinds)
        {
            var partial=CommonFolderChoice.FromReported(kind,"C:\\short",truncated:true);check(!partial.CanAttempt&&partial.Truncated&&partial.Issue=="Reported folder path exceeds preview limits.","Truncated native prefix can never become an eligible short path");
            foreach(uint code in new[]{0u,1u,0x80010106u,uint.MaxValue}){var failed=CommonFolderChoice.Unavailable(kind,"Windows could not report this folder location.",code);check(!failed.CanAttempt&&failed.Path is null&&failed.NativeError==code&&failed.Kind==kind,"Native HRESULT bytes and unavailable status retained without fallback path");}
        }
        reject(()=>CommonFolderChoice.FromReported((CommonFolderKind)99,"C:\\valid"),"Unknown choice kind rejected");reject(()=>CommonFolderChoice.Unavailable(CommonFolderKind.Desktop,"arbitrary input"),"Unavailable issue keys closed");
        check(ReportedFolderPath.FromReported(null).Issue==CacheLocations.Unreported&&ReportedFolderPath.FromReported("").ReportedPath=="","Unreported state retains original cache-compatible issue and raw empty path");
        var prefs=new Prefs();var action=Catalog.Actions.Single(a=>a.Id=="doctor.commonfolders");check(action.Feature==Feature.Doctor&&!action.RequiresFiles&&!Catalog.Available(prefs,Array.Empty<string>()).Any(a=>a.Id==action.Id),"Common-folder maps default off and do not consume unrelated files");prefs.Set(Feature.Doctor,true);check(Catalog.Available(prefs,Array.Empty<string>()).Any(a=>a.Id==action.Id)&&WindowsDoctor.Areas.Where(a=>a.Id is "storage" or "folders").All(a=>a.ActionIds.Contains(action.Id)),"One stable action reachable from Storage and Folder Sizes");
        check(CommonFolders.Limits.Contains("must not be added")&&CommonFolders.Limits.Contains("six roots"),"Separate overlapping root counts are not aggregate physical/profile/reclaimable usage");
    }
}
