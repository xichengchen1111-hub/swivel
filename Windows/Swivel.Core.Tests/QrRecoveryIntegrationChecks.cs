using System;
using System.Linq;
using Swivel.Core;

internal static class QrRecoveryIntegrationChecks
{
    internal static void Run(Action<bool,string> check)
    {
        var prefs=new Prefs();string original=prefs.Serialize();
        var recovery=Catalog.Actions.Single(x=>x.Id=="qr.recoverflat");
        var strict=Catalog.Actions.Single(x=>x.Id=="qr.readflat");
        check(recovery.Feature==Feature.QrCodes&&!recovery.RequiresFiles&&!recovery.SingleFile&&!recovery.ImageOnly,
            "QR recovery uses its own explicit file picker under QR Codes");
        check(!Catalog.Available(prefs,Array.Empty<string>()).Contains(recovery),"QR recovery remains unavailable while QR Codes is off by default");
        prefs.Set(Feature.QrCodes,true);
        check(Catalog.Available(prefs,Array.Empty<string>()).Contains(recovery)&&Catalog.Available(prefs,Array.Empty<string>()).Contains(strict),
            "Strict read and recovery remain separate explicit choices");
        check(Catalog.Available(prefs,new[]{"unrelated.txt"}).Contains(recovery),"Unrelated selected files are not QR recovery input");
        prefs.SetCustomActions(new[]{strict.Id,recovery.Id});prefs.Set(Feature.MyWheel,true);
        var loaded=Prefs.Parse(prefs.Serialize());
        check(loaded.CustomActionIds.SequenceEqual(new[]{strict.Id,recovery.Id}),"Strict and recovery action IDs independently roundtrip");
        loaded.Set(Feature.QrCodes,false);
        check(!Catalog.ForCustomWheel(loaded,Array.Empty<string>()).Any(x=>x.Id==recovery.Id||x.Id==strict.Id)&&loaded.CustomActionIds.Count==2,
            "QR off hides both actions without discarding menu choices");
        prefs.SetCustomActions(Array.Empty<string>());prefs.Set(Feature.MyWheel,false);prefs.Set(Feature.QrCodes,false);
        check(prefs.Serialize()==original,"Recovery adds no persisted fallback setting or decoded text");
    }
}
