using System;
using System.Linq;
using System.Text;
using Swivel.Core;

internal static class QrLibraryChecks
{
    public static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        var entry=new QrEntry(Guid.NewGuid().ToString("N"),"中文QR","first\r\n🦜 \"quote\"","Work");var bytes=QrLibrary.Encode(new[]{entry});var decoded=QrLibrary.Decode(bytes);
        check(decoded.Length==1 && decoded[0].Id!=entry.Id && decoded[0].Name==entry.Name && decoded[0].Text==entry.Text && decoded[0].Group==entry.Group,"QR portable backup preserves exact Unicode/CRLF/quotes/group and generates a new local ID");
        string json=Encoding.UTF8.GetString(bytes);check(!json.Contains(entry.Id),"QR backup has no local identifiers or unrelated preferences");
        var prefs=new Prefs();int events=0;prefs.Changed+=()=>events++;prefs.ImportQr(decoded);
        check(events==1 && prefs.QrCodes.Single()==decoded[0] && !prefs.IsOn(Feature.QrCodes),"QR atomic import emits once and does not enable feature");
        prefs.ImportQr(QrLibrary.Decode(bytes));check(events==1 && prefs.QrCodes.Count==1,"Identical imported entries skipped without mutation");
        var conflict=decoded[0] with {Id=Guid.NewGuid().ToString("N"),Text="different"};var other=entry with {Id=Guid.NewGuid().ToString("N"),Name="another"};
        reject(()=>prefs.ImportQr(new[]{other,conflict}),"Conflicting import rejected");check(prefs.QrCodes.Count==1 && events==1,"Conflict after earlier candidate leaves all settings unchanged");
        check(Prefs.Parse(prefs.Serialize()).QrCodes.Single().Group=="Work","QR group persistence");
        string legacy=prefs.Serialize().Replace(",\"Group\":\"Work\"","");check(Prefs.Parse(legacy).QrCodes.Single().Group=="","Old QR entries default ungrouped");
        foreach(string group in new[]{new string('x',33)," bad","bad\u202e","bad\nline"})reject(()=>(entry with{Group=group}).Validate(),"QR group bounds and visible names");
        foreach(string invalid in new[]{"{}","[]","null",json.Replace("Swivel.QR","Other"),json.Replace("\"Version\": 1","\"Version\": 2"),json.Replace("\"Version\": 1","\"Version\": 1, \"Version\": 1"),json.Replace("\"Version\": 1","\"Version\": 1, \"Executable\": \"evil.exe\""),json.Replace("\"Group\": \"Work\"","\"Group\": null"),json.Replace("\"Entries\": [","\"Entries\": [null,"),json[..^1]})
            reject(()=>QrLibrary.Decode(Encoding.UTF8.GetBytes(invalid)),"Malformed/unknown/duplicate QR backup fields refused");
        reject(()=>QrLibrary.Decode(new byte[]{0xc0,0xaf}),"Malformed UTF8 library refused");reject(()=>QrLibrary.Decode(new byte[QrLibrary.MaxBytes+1]),"Library byte budget before parsing");
        reject(()=>QrLibrary.Encode(new[]{entry,entry with{Id=Guid.NewGuid().ToString("N")}}),"Duplicate names refused on backup");
        var full=Enumerable.Range(0,32).Select(i=>entry with{Id=Guid.NewGuid().ToString("N"),Name="entry "+i}).ToArray();check(QrLibrary.Decode(QrLibrary.Encode(full)).Length==32,"Full library round trip");
        reject(()=>QrLibrary.Merge(full,new[]{other}),"Merged library count bounded");
        check(QrLibrary.Decode(QrLibrary.Encode(Array.Empty<QrEntry>())).Length==0,"Empty backup is valid and inert");
        string Wrap(string value)=>"{\"Format\":\"Swivel.QR\",\"Version\":1,\"Entries\":["+value+"]}";
        foreach(string leaf in new[]{"{\"Name\":null,\"Text\":\"x\",\"Group\":\"\"}","{\"Name\":\"a\",\"Text\":null,\"Group\":\"\"}","{\"Name\":\"a\",\"Name\":\"b\",\"Text\":\"x\",\"Group\":\"\"}","{\"Name\":\"a\",\"Text\":\"x\",\"Text\":\"y\",\"Group\":\"\"}","{\"Name\":\"a\",\"Text\":\"x\",\"Group\":\"\",\"Group\":\"b\"}"})
            reject(()=>QrLibrary.Decode(Encoding.UTF8.GetBytes(Wrap(leaf))),"Null or repeated leaf fields rejected");
        string tooMany=Wrap(string.Join(',',Enumerable.Range(0,33).Select(i=>"{\"Name\":\""+i+"\",\"Text\":\"x\",\"Group\":\"\"}")));
        reject(()=>QrLibrary.Decode(Encoding.UTF8.GetBytes(tooMany)),"33 entries refused before constructing library");
        var fullPrefs=new Prefs();fullPrefs.ImportQr(full);string before=fullPrefs.Serialize();int changes=0;fullPrefs.Changed+=()=>changes++;
        reject(()=>fullPrefs.ImportQr(new[]{other}),"Full library rejects extra import");check(fullPrefs.Serialize()==before && changes==0,"Overflow import keeps byte-identical settings and emits no change");
        byte[] boundary=Enumerable.Repeat((byte)' ',65536).ToArray();bytes.CopyTo(boundary,0);check(QrLibrary.Decode(boundary).Length==1,"Exactly 64KB JSON accepted, including trailing whitespace");
        boundary=Enumerable.Repeat((byte)' ',QrLibrary.MaxBytes).ToArray();bytes.CopyTo(boundary,0);check(QrLibrary.Decode(boundary).Length==1,"Exactly 1MiB QR JSON accepted, including trailing whitespace");
        var medium=entry with { Correction=QrCorrection.Medium };byte[] mediumBytes=QrLibrary.Encode(new[]{medium});string mediumJson=Encoding.UTF8.GetString(mediumBytes);
        check(!json.Contains("Correction")&&QrLibrary.Decode(bytes).Single().Correction==QrCorrection.Low,"L backup preserves legacy shape and missing correction defaults L");
        check(mediumJson.Contains("\"Correction\": \"M\"")&&QrLibrary.Decode(mediumBytes).Single().Correction==QrCorrection.Medium,"M backup carries explicit closed correction field");
        check(QrLibrary.Decode(Encoding.UTF8.GetBytes(mediumJson.Replace("\"M\"","\"L\""))).Single().Correction==QrCorrection.Low,"Explicit L backup field is accepted");
        foreach(string malformed in new[]{"null","1","true","\"m\"","\"X\"","[]","{}"})
            reject(()=>QrLibrary.Decode(Encoding.UTF8.GetBytes(mediumJson.Replace("\"Correction\": \"M\"","\"Correction\": "+malformed))),"Unknown/null/wrong-type correction is refused instead of downgraded");
        reject(()=>QrLibrary.Decode(Encoding.UTF8.GetBytes(mediumJson.Replace("\"Correction\": \"M\"","\"Correction\": \"M\", \"Correction\": \"L\""))),"Duplicate correction field refused");
        var levelPrefs=new Prefs();levelPrefs.SaveQr(entry);string oldLevel=levelPrefs.Serialize();
        check(!oldLevel.Contains("Correction")&&Prefs.Parse(oldLevel).QrCodes.Single().Correction==QrCorrection.Low,"Old settings without correction retain L and same field shape");
        levelPrefs.SaveQr(medium);check(Prefs.Parse(levelPrefs.Serialize()).QrCodes.Single()==medium&&!levelPrefs.IsOn(Feature.QrCodes),"M settings roundtrip retains ID/content/group and default-off switch");
        levelPrefs.SaveQr(entry);string unchanged=levelPrefs.Serialize();int levelEvents=0;levelPrefs.Changed+=()=>levelEvents++;
        reject(()=>levelPrefs.ImportQr(new[]{other,medium with{Id=Guid.NewGuid().ToString("N")}}),"Same name/text/group with different correction is a whole-merge conflict");
        check(levelPrefs.Serialize()==unchanged&&levelEvents==0,"Correction conflict does not partly mutate earlier entries or switches");
        reject(()=>(medium with{Correction=(QrCorrection)5}).Validate(),"Saved correction enum is closed");
        var longest=Enumerable.Range(0,32).Select(i=>new QrEntry(Guid.NewGuid().ToString("N"),new string((char)(0x4e00+i),48),new string('<',QrCode.MaxBytes),new string('組',32))).ToArray();
        byte[] large=QrLibrary.Encode(longest);var recovered=QrLibrary.Decode(large);
        check(large.Length>65536 && large.Length<=QrLibrary.MaxBytes && recovered.Select(x=>(x.Name,x.Text,x.Group)).SequenceEqual(longest.Select(x=>(x.Name,x.Text,x.Group))),"All 32 maximum escaped payloads/names/groups back up completely beyond the former 64KiB limit");
        var populated=new Prefs();populated.ImportQr(longest);
        for(int i=0;i<16;i++)populated.SaveApp(new(Guid.NewGuid().ToString("N"),new string((char)(0x4e00+i),48),"C:\\"+new string('界',233)+".exe",string.Join(',',Enumerable.Range(0,16).Select(n=>new string('x',10)+n.ToString("D2"))),true));
        const double negative=-2.7470047388660944E-308, positive=2.7470047388660944E-308;
        var adjustment=new PhotoSettings(negative,negative,negative,negative,negative,negative,negative,negative,negative,negative,negative,positive,positive,positive,PhotoLook.Vivid,positive);
        for(int i=0;i<24;i++)populated.SavePreset(new(Guid.NewGuid().ToString("N"),new string((char)(0x4e00+i),48),new("tiff",8192,100,adjustment,true)));
        populated.SetCustomActions(Catalog.All(populated).Select(x=>x.Id).OrderByDescending(x=>x.Length).Take(80));
        for(int i=0;i<5;i++)populated.SetPalette((MenuTemplate)i,new(16777215,16777215,16777215,16777215));
        populated.SetShortcuts(new(new(7,135),new(7,134),new(7,133)));
        populated.SetLanguage("zh-Hans");populated.SetMenuPresentation(MenuTemplate.Clothesline,false);
        foreach(var feature in Enum.GetValues<Feature>())populated.Set(feature,true);
        string complete=populated.Serialize();var restored=Prefs.Parse(complete);
        check(complete.Length>131072 && complete.Length<=Prefs.MaxJsonLength && restored.QrCodes.SequenceEqual(longest) && restored.Apps.SequenceEqual(populated.Apps) && restored.Presets.SequenceEqual(populated.Presets),"Expanded full QR library plus maximum escaped apps/presets and longest finite numbers persists beyond old 128KiB settings limit");
        check(restored.Serialize()==complete,"Full settings roundtrip retains IDs, menu actions, palettes, shortcuts, language and switches");
        for(int i=0;i<16;i++)
        {
            var application=populated.Apps[i];
            populated.SaveApp(application with{Executable="", BrowserExecutable=application.Executable, WebAddress="https://example.com/"+new string('&',WebAddress.MaxLength-"https://example.com/".Length)});
        }
        string webComplete=populated.Serialize();var webRestored=Prefs.Parse(webComplete);
        check(webComplete.Length>393216 && webComplete.Length<=Prefs.MaxJsonLength && webRestored.Serialize()==webComplete,"Sixteen maximum JSON-escaped web addresses and browser paths plus full QR/presets/actions persist beyond old 160KiB bound without losing any settings");
        check(webRestored.Apps.All(x=>x.IsWebPage && x.CopyToClipboard && x.WebAddress!.Length==2048 && x.BrowserExecutable!.Length==240),"Maximum web preference regression retains canonical addresses and clipboard mode");
        check(Prefs.Parse(webComplete.PadRight(Prefs.MaxJsonLength)).QrCodes.Count==32,"Exactly 1MiB bounded settings JSON accepted");
        reject(()=>Prefs.Parse(webComplete.PadRight(Prefs.MaxJsonLength+1)),"Settings limit is enforced before parsing, even for otherwise valid JSON");
    }
}
