using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Swivel.Core;

internal static class QrMacLibraryExportChecks
{
    internal static void Run(Action<bool,string> check, Action<Action,string> reject)
    {
        QrEntry Entry(int i, string text="中文 🦜", QrCorrection level=QrCorrection.Medium) =>
            new(i.ToString("x32"), "名称 " + i, text, "組", level, (QrGardenTemplate)(i%3), (QrGardenSeason)(i%3));
        var entries = Enumerable.Range(1,3).Select(i=>Entry(i,"值 " + i,(QrCorrection)i)).ToArray();
        var plan = QrMacLibraryExport.Prepare(entries);
        using var json = JsonDocument.Parse(plan.Encode());
        check(json.RootElement.GetArrayLength()==3, "Full Mac export is a bare SavedQRCode array");
        for(int i=0;i<3;i++)
        {
            var row=json.RootElement[i];
            check(row.EnumerateObject().Count()==7 && row.GetProperty("value").GetString()==entries[i].Text && row.GetProperty("collection").GetString()==entries[i].Group, "Full Mac export exact content and seven Foundation fields");
            check(row.GetProperty("created").GetDouble()==0 && Guid.Parse(row.GetProperty("id").GetString()!)==Guid.Parse(entries[i].Id), "Full Mac export retains UUID and explicitly uses Foundation epoch for missing dates");
        }
        check(MacQrLibrary.Decode(plan.Encode()).Compatible.Select(x=>x.Text).SequenceEqual(entries.Select(x=>x.Text)), "Full Mac export round trip through existing Mac importer keeps exact values");
        check(plan.Rows.All(x=>x.Reasons.Contains(QrMacLibraryExport.MissingDate)), "Every absent Windows creation date is disclosed per row");
        check(plan.Rows[1].Reasons.Contains(QrMacLibraryExport.CorrectionChanged), "Non-M correction conversion is disclosed");
        check(plan.Encode().SequenceEqual(QrMacLibraryExport.Prepare(entries).Encode()), "Repeated exports are deterministic");
        var invalid = QrMacLibraryExport.Prepare(new[]{Entry(1,new string('a',2001)), Entry(2) with {Name=new string('a',121)}, Entry(3) with {Group=new string('a',81)}, Entry(4,"\ud800"), Entry(5) with {Season=(QrGardenSeason)99}});
        check(invalid.Rows.All(x=>x.Output is null) && JsonDocument.Parse(invalid.Encode()).RootElement.GetArrayLength()==0,"Invalid rows are listed and omitted without truncation");
        var generated=QrMacLibraryExport.Prepare(new[]{Entry(1) with {Id="legacy-id"}});
        check(generated.Rows[0].Reasons.Contains(QrMacLibraryExport.GeneratedId) && generated.Encode().SequenceEqual(QrMacLibraryExport.Prepare(new[]{Entry(1) with {Id="legacy-id"}}).Encode()),"Legacy IDs receive stable deterministic UUIDs");
        var merge=QrMacLibraryExport.Prepare(new[]{Entry(1,"new"),Entry(2,"e\u0301"),Entry(3,"é") with {Group="different"}}, plan.Encode());
        check(merge.Rows[0].Output!.Value==entries[0].Text && merge.Rows[0].Reasons.Count==0, "Mac merge retains original rows first and exact");
        check(merge.Rows[3].Reasons.Contains(QrMacLibraryExport.IdCollision),"Mac merge discloses deterministic UUID collision resolution");
        var canonical=QrMacLibraryExport.Prepare(new[]{Entry(2,"e\u0301")},QrMacLibraryExport.Prepare(new[]{Entry(1,"é")}).Encode());
        check(canonical.Rows[^1].Output is null && canonical.Rows[^1].Reasons.Contains(QrMacLibraryExport.Duplicate), "Mac merge uses canonical equivalence on value and collection without rewriting content");
        foreach(string bad in new[]{"not JSON","{}","[{\"id\":\"no\"}]",Encoding.UTF8.GetString(plan.Encode()).Replace("\"created\": 0","\"created\": \"bad\"")})
            reject(()=>QrMacLibraryExport.Prepare(entries,Encoding.UTF8.GetBytes(bad)),"Unreadable merge is refused wholesale");
        check(plan.Rows.Select(x=>x.Output!.Season).SequenceEqual(new[]{"spring","autumn","summer"}) && plan.Rows.Select(x=>x.Output!.Template).SequenceEqual(new[]{"bouquet","island","tree"}), "All seasons and templates map exactly without loss");
        foreach(var level in Enum.GetValues<QrCorrection>())
        {
            var levels=QrMacLibraryExport.Prepare(new[]{Entry(1,"same payload",level)});
            check(levels.Rows[0].Output!.Value=="same payload" && levels.Rows[0].Reasons.Contains(QrMacLibraryExport.CorrectionChanged)==(level!=QrCorrection.Medium),"Every QR level retains exact value and explicitly discloses only correction changes");
        }
        check(QrMacLibraryExport.Prepare(new[]{Entry(1,new string('中',666)+"ab")}).Rows[0].Output is not null,"Mac export accepts exact 2000 UTF8 byte boundary");
        check(QrMacLibraryExport.Prepare(new[]{Entry(1,new string('中',667))}).Rows[0].Reasons.Contains(QrMacLibraryExport.ValueLimit),"Mac export refuses 2001 multibyte bytes");
        var scalar=QrMacLibraryExport.Prepare(new[]{Entry(1) with {Name=string.Concat(Enumerable.Repeat("🦜",120)),Group=string.Concat(Enumerable.Repeat("🦜",80))}});
        check(scalar.Rows[0].Output is not null,"Conservative scalar limits accept 120 and 80 supplementary characters");
        check(QrMacLibraryExport.Prepare(new[]{Entry(1) with {Name=string.Concat(Enumerable.Repeat("e\u0301",61))}}).Rows[0].Reasons.Contains(QrMacLibraryExport.NameLimit),"Conservative scalar bound refuses combining clusters rather than guessing Swift segmentation");
        foreach(var broken in new[]{Entry(1) with {Id="\ud800"},Entry(1) with {Id=""},Entry(1) with {Name=" "},Entry(1) with {Text=""},Entry(1) with {Group="\ud800"},Entry(1) with {Correction=(QrCorrection)99}})
            check(QrMacLibraryExport.Prepare(new[]{broken}).Rows[0].Output is null,"Invalid Windows fields remain per-row refusals");
        byte[] old=Encoding.UTF8.GetBytes("[{\"id\":\"00000000-0000-0000-0000-000000000001\",\"name\":\"旧库\",\"value\":\"e\\u0301\"}]");
        var oldPlan=QrMacLibraryExport.Prepare(Array.Empty<QrEntry>(),old);
        check(oldPlan.Rows[0].Output!.Created==0 && oldPlan.Rows[0].Output!.Template=="tree" && oldPlan.Rows[0].Output!.Season=="summer" && oldPlan.Rows[0].Reasons.Contains(QrMacLibraryExport.MissingDate) && oldPlan.Rows[0].Reasons.Contains(QrMacLibraryExport.DefaultScene),"Old Mac missing fields receive explicit stable defaults");
        var dated=Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(plan.Encode()).Replace("\"created\": 0","\"created\": 123456789.125"));
        var kept=QrMacLibraryExport.Prepare(Array.Empty<QrEntry>(),dated);
        check(kept.Rows.All(x=>x.Output!.Created==123456789.125 && x.Reasons.Count==0),"Real Mac Foundation fractional dates are retained exactly");
        var sameId=QrMacLibraryExport.Prepare(new[]{Entry(1,"first"),Entry(1,"second")});
        check(sameId.Rows[1].Reasons.Contains(QrMacLibraryExport.IdCollision) && sameId.Rows.Select(x=>x.Output!.Id).Distinct().Count()==2 && sameId.Encode().SequenceEqual(QrMacLibraryExport.Prepare(new[]{Entry(1,"first"),Entry(1,"second")}).Encode()),"Duplicate Windows UUIDs resolve stably with disclosure");
        foreach(string bad in new[]{
            "[{\"id\":\"00000000-0000-0000-0000-000000000001\",\"name\":\"n\",\"value\":\"\\ud800\"}]",
            Encoding.UTF8.GetString(plan.Encode()).Replace("\"template\": \"bouquet\"","\"template\": \"unknown\""),
            Encoding.UTF8.GetString(plan.Encode()).Replace("\"created\": 0","\"created\": 1e999"),
            Encoding.UTF8.GetString(plan.Encode()).Replace("\"created\": 0","\"created\": 0, \"extra\": 1"),
            Encoding.UTF8.GetString(plan.Encode()).Replace("00000000-0000-0000-0000-000000000002","00000000-0000-0000-0000-000000000001")})
            reject(()=>QrMacLibraryExport.Prepare(entries,Encoding.UTF8.GetBytes(bad)),"Malformed merge date/Unicode/scene/field/duplicate UUID refuses whole file");
        reject(()=>QrMacLibraryExport.Prepare(entries,new byte[QrMacLibraryExport.MaxMergeBytes+1]),"Mac merge byte budget refuses whole file");
        reject(()=>QrMacLibraryExport.Prepare(entries,new byte[]{91,34,0xff,34,93}),"Mac merge refuses malformed raw UTF8");
        var many=Enumerable.Range(1,32).Select(i=>Entry(i,"value "+i)).ToArray();
        check(JsonDocument.Parse(QrMacLibraryExport.Prepare(many).Encode()).RootElement.GetArrayLength()==32,"Full 32 entry Windows library exports completely");
        reject(()=>QrMacLibraryExport.Prepare(many.Append(Entry(33)).ToArray()),"Mac export respects Windows library count");
        byte[] MacRows(int count)=>JsonSerializer.SerializeToUtf8Bytes(Enumerable.Range(1,count).Select(i=>new {id=new Guid(i,0,0,new byte[8]).ToString("D"),name="Mac " + i,value="Mac value "+i,collection="",season="autumn",template="island",created=-123.5}));
        var fullMerge=QrMacLibraryExport.Prepare(many,MacRows(128));
        check(fullMerge.Rows.Count==160 && fullMerge.Rows.All(x=>x.Output is not null) && fullMerge.Rows.Take(128).All(x=>x.FromMac && x.Output!.Created==-123.5),"Full bounded Mac plus Windows merge retains all 160 valid rows in source order");
        reject(()=>QrMacLibraryExport.Prepare(many,MacRows(129)),"Above 128 Mac source rows refuses the entire merge");
        reject(()=>MacQrLibrary.Decode(fullMerge.Encode()),"More than 128 merged output rows explicitly exceed the existing Windows reverse importer scope");
        var rawZero=QrMacLibraryExport.Prepare(new[]{Entry(1,"a\0b\r\n🦜")});
        check(MacQrLibrary.Decode(rawZero.Encode()).Rows[0].Text=="a\0b\r\n🦜","JSON escaping retains NUL, CRLF and supplementary payload bytes");
        var groups=QrMacLibraryExport.Prepare(new[]{Entry(2,"e\u0301") with {Group="e\u0301"},Entry(3,"é") with {Group="É"}},QrMacLibraryExport.Prepare(new[]{Entry(1,"é") with {Group="é"}}).Encode());
        check(groups.Rows[1].Reasons.Contains(QrMacLibraryExport.Duplicate) && groups.Rows[2].Output is not null,"Mac duplicate rule normalizes both content and group but remains case sensitive");
        byte[] copy=plan.Encode();copy[0]=0;
        check(plan.Encode()[0]==(byte)'[',"Reviewed output bytes are immutable to callers");
        var fixture=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Fixtures","qr-mac-library-v1.json"));
        check(fixture.SequenceEqual(plan.Encode()),"Golden fixture matches complete non-ASCII three season/template export");
    }
}
