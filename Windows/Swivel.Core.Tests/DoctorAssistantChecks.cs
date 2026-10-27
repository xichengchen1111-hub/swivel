using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using Swivel.Core;

internal static class DoctorAssistantChecks
{
    private static DoctorFact Fact(string section,string title,string format,object[] values,bool isPrivate=false)=>new(title,format,values,Section:section,PrivateDetails:isPrivate);
    private sealed class Hostile { public override string ToString()=>throw new Exception("Source values must never be rendered"); }
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        var systemValues=new object[]{"PRIVATE-OS","Arm64",8};
        var facts=new[]{
            Fact("Overview","System","{0} · {1} · {2} logical processors",systemValues),
            Fact("Overview","Memory","{0} GB available of {1} GB; {2}% in use",new object[]{4d,16d,75u}),
            Fact("Overview","Fixed Drives","{0}: {1} GB available to this user of {2} GB",new object[]{"PRIVATE-DRIVE",12d,100d}),
            Fact("Overview","Largest working sets","{0}: {1} MB resident memory",new object[]{"PRIVATE-PROCESS",22d}),
            Fact("Network & Proxy","Address configuration","IPv4: {0} · IPv6: {1} · gateways: {2} · DNS servers: {3}",new object[]{1,2,1,2}),
            Fact("Network & Proxy","Private adapter","{0}",new object[]{"PRIVATE-ADDRESS"},true),
            Fact("Unknown","PRIVATE-TITLE","{0}",new object[]{new Hostile()})};
        var snapshot=DoctorAssistantReport.Capture(facts);using(var report=JsonDocument.Parse(snapshot.Json))
        {
            check(report.RootElement.GetProperty("metrics").GetArrayLength()==5&&report.RootElement.GetProperty("omitted_facts").GetInt32()==2,"Doctor exact schemas only, private/unknown fields omitted");
            check(!snapshot.Json.Contains("PRIVATE"),"Doctor names, paths and private values never enter safe snapshot");
        }
        systemValues[1]="PRIVATE-MUTATION";systemValues[2]=4097;check(snapshot.Json.Contains("Arm64")&&!snapshot.Json.Contains("PRIVATE-MUTATION"),"Doctor captured report independent of mutable source values");
        foreach(object value in new object[]{new Hostile(),"123",-1,double.NaN,double.PositiveInfinity,1000000000001d})
        {var raw=DoctorAssistantReport.Create(new[]{Fact("Overview","Uptime","{0} hours since the last system start",new[]{value})});using var doc=JsonDocument.Parse(raw);check(doc.RootElement.GetProperty("metrics").GetArrayLength()==0,"Doctor malformed numeric values omitted without ToString");}
        foreach(var values in new[]{new object[]{17d,16d,5u},new object[]{0d,0d,0u},new object[]{4d,16d,101u}})
        {using var doc=JsonDocument.Parse(DoctorAssistantReport.Create(new[]{Fact("Overview","Memory","{0} GB available of {1} GB; {2}% in use",values)}));check(doc.RootElement.GetProperty("omitted_facts").GetInt32()==1,"Doctor numeric relationships must be possible");}
        var privateFacts=Enumerable.Repeat(Fact("Overview","Uptime","{0} hours since the last system start",new object[]{1d},true),512).ToArray();check(DoctorAssistantReport.Create(privateFacts).Contains("\"omitted_facts\": 512"),"Doctor exact fact bound retains omission count");reject(()=>DoctorAssistantReport.Create(privateFacts.Append(privateFacts[0]).ToArray()),"Doctor fact limit refuses whole report");
        var settings=new AssistantSettings(AssistantProvider.ChatCompletions,"https://example.com/v1/chat/completions","fixture");
        foreach(var provider in Enum.GetValues<AssistantProvider>())
        {
            var plan=AssistantPlan.Explain(settings with{Provider=provider},"Explain this snapshot",snapshot);using var body=JsonDocument.Parse(plan.Json);var messages=body.RootElement.GetProperty("messages");using var prompt=JsonDocument.Parse(messages[messages.GetArrayLength()-1].GetProperty("content").GetString()!);
            using var preview=JsonDocument.Parse(snapshot.Json);check(prompt.RootElement.GetProperty("redacted_report").GetRawText()==preview.RootElement.GetRawText().Replace("\r","")||JsonSerializer.Serialize(prompt.RootElement.GetProperty("redacted_report"))==JsonSerializer.Serialize(preview.RootElement),"Doctor displayed snapshot and nested service report agree");
            check(plan.ExplanationOnly&&plan.Choices.Count==0&&!plan.Json.Contains("PRIVATE"),"Doctor explanation exposes no executable choice");
            byte[] Reply(string text,string? action=null)
            {string inner=JsonSerializer.Serialize(new{action_id=action,explanation=text});return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(provider==AssistantProvider.ClaudeMessages?(object)new{role="assistant",stop_reason="end_turn",content=new[]{new{type="text",text=inner}}}:new{choices=new[]{new{finish_reason="stop",message=new{role="assistant",content=inner}}}}));}
            foreach(string text in new[]{new string('中',4096),string.Concat(Enumerable.Repeat("😀",2048)),"first\nsecond\tthird\r\n", "a\u202eb", "a\U000E0001b"})
            {var parsed=plan.Parse(Reply(text));check(parsed.ActionId is null&&parsed.Explanation==(text.Contains('\u202e')?"a[U+202E]b":text.Contains("\U000E0001")?"a[U+E0001]b":text),"Doctor full escaped explanation and safe line/format display");}
            reject(()=>plan.Parse(Reply(new string('中',4097))),"Doctor explanation over bound refuses rather than truncates");reject(()=>plan.Parse(Reply("Do a fix","doctor.check")),"Doctor cannot return action IDs");
        }
        var prefs=new Prefs();prefs.SavePreset(new(Guid.NewGuid().ToString("N"),"PRIVATE-PRESET",new("png",0,90)));prefs.Set(Feature.ImagePresets,true);
        var fixedOnly=AssistantPlan.Offered(prefs,new[]{"C:\\private.png"});var opted=AssistantPlan.Offered(prefs,new[]{"C:\\private.png"},true);
        check(fixedOnly.All(x=>!x.Title.Contains("PRIVATE"))&&opted.Any(x=>x.Title.Contains("PRIVATE")),"Custom labels require explicit per-request opt-in");
        var fixedPlan=AssistantPlan.Create(settings,"Choose an action",fixedOnly,new[]{"C:\\private.png"});var customPlan=AssistantPlan.Create(settings,"Choose an action",opted,new[]{"C:\\private.png"});check(!fixedPlan.Json.Contains("PRIVATE-PRESET")&&customPlan.Json.Contains("PRIVATE-PRESET")&&!customPlan.Json.Contains("private.png"),"Only opted-in labels join body, source paths still excluded");
    }
}
