using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using Swivel.Core;

internal static class AssistantDraftChecks
{
    public static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        var original=new[]{@"C:\Projects\a+%23 & name.md",@"D:\文档\报告 🦜.txt"};
        foreach(string language in new[]{"en","zh-Hans","zh-Hant","ja"})
        foreach(DraftTarget target in Enum.GetValues<DraftTarget>())
        {
            var plan=AssistantDraft.Build(target,original,language,default);
            check(plan.Target==target&&plan.Paths.SequenceEqual(original)&&plan.Utf8Bytes==Encoding.UTF8.GetByteCount(plan.Text),"Provider/language immutable complete draft records exact UTF8 count");
            string[] lines=plan.Text.Split('\n');var encoded=lines.SkipWhile(l=>!l.StartsWith('"')).ToArray();
            check(encoded.Length==2&&encoded.Select(l=>JsonSerializer.Deserialize<string>(l)).SequenceEqual(original)&&encoded.All(l=>l.All(c=>c<128)),"JSON path lines are complete visible ASCII and independently restore each Unicode path");
            if(plan.Address is not null)
            {
                string prefix=target==DraftTarget.Codex?"codex://threads/new?prompt=":"claude://code/new?q=";
                check(plan.Address.StartsWith(prefix,StringComparison.Ordinal)&&plan.Address.All(c=>c<128)&&!plan.Address.Contains('+'),"Only closed target prefixes and escaped ASCII query, no ambiguous plus");
                check(Uri.UnescapeDataString(plan.Address[prefix.Length..])==plan.Text&&plan.Address.Count(c=>c=='?')==1&&!plan.Address.Contains('&')&&!plan.Address.Contains('#'),"Exactly one decode restores full prompt without extra parameters or fragment");
            }
        }
        var names=new[]{"\u0085","\u00A0","\u2028","\u2029","\uE000","\uFDD0","\uFE0F","\U000E0100","\u115F","\u3164","\u2800"};
        foreach(string name in names)
        {
            string path=@"C:\data\a"+name+".txt";var plan=AssistantDraft.Build(DraftTarget.Codex,new[]{path},"en",default);string line=plan.Text.Split('\n').Last();
            check(line.All(c=>c<128)&&JsonSerializer.Deserialize<string>(line)==path,"Invisible/PUA/noncharacter/variation data is escaped visibly, never normalized away");
        }
        foreach(string name in new[]{"\ud800","\udfff","x\ud800y","\udc00\ud800"})reject(()=>AssistantDraft.Build(DraftTarget.Codex,new[]{@"C:\data\a"+name+".txt"},"en",default),"Reject malformed UTF16 before JSON could replace it");
        foreach(string path in new[]{@"relative.txt",@"\\server\share\a.txt",@"C:\data\a.txt:ads",@"C:\data\..\a.txt",@"C:\CON.txt",@"C:\data\a"+"\u202E"+".txt"})reject(()=>AssistantDraft.Build(DraftTarget.Codex,new[]{path},"en",default),"Existing local lexical path policy remains intact");
        reject(()=>AssistantDraft.Build((DraftTarget)2,original,"en",default),"Unknown provider enum cannot create arbitrary scheme");
        reject(()=>AssistantDraft.Build(DraftTarget.Codex,Array.Empty<string>(),"en",default),"Empty draft selection refused");
        reject(()=>AssistantDraft.Build(DraftTarget.Codex,Enumerable.Range(0,101).Select(i=>$@"C:\f{i}.txt").ToArray(),"en",default),"Count bound before source references prepared");
        reject(()=>AssistantDraft.Build(DraftTarget.Codex,new[]{@"C:\a.txt",@"c:\A.TXT"},"en",default),"Case-insensitive repeated Windows path refused");
        reject(()=>AssistantDraft.Build(DraftTarget.Codex,original,"en",new CancellationToken(true)),"Pre-cancel rejects before draft work");
        var mutable=new List<string>{@"C:\a.txt"};var snapshot=AssistantDraft.Build(DraftTarget.Codex,mutable,"en",default);mutable[0]=@"C:\b.txt";
        check(snapshot.Paths[0]==@"C:\a.txt"&&JsonSerializer.Deserialize<string>(snapshot.Text.Split('\n').Last())==@"C:\a.txt","Caller mutation cannot alter reviewed paths/text/address");
        reject(()=>((IList<string>)snapshot.Paths)[0]=@"C:\replace.txt","Returned path collection cannot be modified");
        var hundred=Enumerable.Range(0,100).Select(i=>$@"C:\f{i:000}.txt").ToArray();var full=AssistantDraft.Build(DraftTarget.Codex,hundred,"en",default);
        check(full.Paths.Count==100&&full.Text.Split('\n').Count(l=>l.StartsWith('"'))==100&&full.Address is null,"100 short paths retained completely even when URI opening is disabled");
        reject(()=>AssistantDraft.Build(DraftTarget.ClaudeCode,Enumerable.Range(0,100).Select(i=>$@"C:\{i:000}"+new string('中',150)+".txt").ToArray(),"en",default),"Full prompt byte ceiling rejects without silent path truncation");
        // Find exact ASCII URI length boundary independently from percent-escaped bytes.
        bool found2048=false,found2049=false;
        for(int n=1;n<=100;n++)
        {
            var files=Enumerable.Range(0,n).Select(i=>$@"C:\f{i:000}.txt").ToArray();var draft=AssistantDraft.Build(DraftTarget.Codex,files,"en",default);
            int length="codex://threads/new?prompt=".Length+Uri.EscapeDataString(draft.Text).Length;
            check((draft.Address is not null)==(length<=2048),"Closed URI complete-length bound matches independent recomputation "+n);
        }
        // Grow a last name by one unreserved character to reach 2048 / 2049 exactly.
        for(int n=1;n<=15&&!(found2048&&found2049);n++)for(int length=1;length<=220;length++)
        {
            var files=Enumerable.Range(0,n).Select(i=>$@"C:\{i:00}"+new string('a',i==n-1?length:100)+".txt").ToArray();var plan=AssistantDraft.Build(DraftTarget.Codex,files,"en",default);
            int size="codex://threads/new?prompt=".Length+Uri.EscapeDataString(plan.Text).Length;
            if(size==2048){found2048=true;check(plan.Address?.Length==2048,"Exact2048 address remains fully openable");}
            if(size==2049){found2049=true;check(plan.Address is null&&plan.Text.EndsWith(JsonSerializer.Serialize(files.Last()),StringComparison.Ordinal),"Exact2049 address disabled but full draft retained");}
        }
        check(found2048&&found2049,"Boundary fixtures reached exact2048 and2049 rather than guessing vendor limits");
        var prefs=new Prefs();check(!Catalog.Available(prefs,original).Any(a=>a.Id=="file.assistantdraft"),"App Routing defaults off and gates draft action");prefs.Set(Feature.AppRouting,true);
        check(Catalog.Available(prefs,original).Any(a=>a.Id=="file.assistantdraft")&&!Catalog.Available(prefs,Array.Empty<string>()).Any(a=>a.Id=="file.assistantdraft"),"Enabled action still requires explicit files");
        prefs.SetCustomActions(new[]{"file.assistantdraft","file.copy"});var restored=Prefs.Parse(prefs.Serialize());check(restored.CustomActionIds.SequenceEqual(prefs.CustomActionIds)&&restored.IsOn(Feature.AppRouting),"New stable draft action persists beside old clipboard action without schema change");
    }
}
