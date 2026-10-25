using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Swivel.Core;

internal static class AssistantChecks
{
    private sealed class Fake(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> run):HttpMessageHandler
    {public int Calls;public bool Disposed;protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken t){Calls++;return run(r,t);}protected override void Dispose(bool d){Disposed=true;base.Dispose(d);}}
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        var service=new AssistantSettings(AssistantProvider.ChatCompletions,"https://example.com/v1/chat/completions","model-1");
        var choices=new[]{new AssistantChoice("convert.png","Convert to PNG"),new AssistantChoice("file.paths","Copy Path")};
        AssistantPlan Plan(AssistantSettings? settings=null,string request="Make a PNG")=>AssistantPlan.Create(settings??service,request,choices,new[]{"C:\\secret-user\\private.png"});
        string Answer(string inner,bool claude=false)=>JsonSerializer.Serialize(claude?new{role="assistant",stop_reason="end_turn",content=new[]{new{type="text",text=inner}}}:(object)new{choices=new[]{new{finish_reason="stop",message=new{role="assistant",content=inner}}}});
        string good="{\"action_id\":\"convert.png\",\"explanation\":\"Make a new PNG.\"}";
        foreach(var s in new[]{service,service with{CompletionTokenLimit=true},service with{Provider=AssistantProvider.ClaudeMessages,Endpoint="https://example.com/v1/messages"}})
        {
            var p=Plan(s);using var json=JsonDocument.Parse(p.Json);check(json.RootElement.GetProperty("model").GetString()==s.Model&&!p.Json.Contains("secret-user")&&!p.Json.Contains("private.png"),"Assistant request only includes fixed file-kind counts");
            check(p.Parse(Encoding.UTF8.GetBytes(Answer(good,s.Provider==AssistantProvider.ClaudeMessages))).ActionId=="convert.png","Assistant protocol parses closed suggestion");
            var none=p.Parse(Encoding.UTF8.GetBytes(Answer("{\"action_id\":null,\"explanation\":\"No action fits.\"}",s.Provider==AssistantProvider.ClaudeMessages)));check(none.ActionId is null,"Assistant no-match is not an action");
        }
        var settingsList=new[]{service,service with{Endpoint="https://EXAMPLE.com:443/other"},service with{Endpoint="https://example.com:444/other"},service with{Provider=AssistantProvider.ClaudeMessages},service with{Endpoint="http://localhost:11434/v1/chat/completions"},service with{Endpoint="https://localhost:11434/v1/chat/completions"}};
        check(settingsList[0].CredentialTarget==settingsList[1].CredentialTarget&&settingsList.Skip(2).Select(x=>x.CredentialTarget).Distinct().Count()==4,"Credential scope includes protocol/scheme/host/effectiveport but not path");
        foreach(string host in new[]{"http://localhost:1234/v1/chat/completions","http://127.0.0.1:1234/v1/chat/completions","http://[::1]:1234/v1/chat/completions"})check((service with{Endpoint=host}).Local,"Exact allowed loopback names");
        foreach(string address in new[]{"http://example.com/x","http://127.1/x","http://2130706433/x","http://0x7f000001/x","http://127.0.0.2/x","https://u:p@example.com/x","https://example.com/x?q=secret","https://example.com/x#secret","https://example.com/%0a","https://example.com/%E2%80%AE","https://ex ample.com/x","https://example.com/x\\y"})reject(()=>(service with{Endpoint=address}).Normalize(),"Unsafe endpoint refused");
        foreach(string model in new[]{"",new string('x',129),"bad model","bad\r\nkey","中","bad\u202e"})reject(()=>(service with{Model=model}).Normalize(),"Closed ASCII model input");
        foreach(string key in new[]{"",new string('k',2049),"bad key","bad\nkey","秘密"})reject(()=>AssistantSettings.ValidateKey(key),"Header-safe key limits");
        AssistantSettings.ValidateKey(new string('k',2048));check(true,"Exact key bound");
        var plan=Plan();foreach(string bad in new[]{"{}","[]","null",good.Replace("convert.png","unknown"),good.Replace("convert.png","Convert.png"),good[..^1]+",\"args\":[]}",good.Replace("\"action_id\":","\"action_id\":null,\"action_id\":"),good.Replace("Make a new PNG.",""),good.Replace("Make a new PNG.",new string('x',513)),"```json\n"+good+"\n```"})reject(()=>plan.Parse(Encoding.UTF8.GetBytes(Answer(bad))),"Malformed or non-offered assistant selection refused");
        foreach(string bad in new[]{Answer(good).Replace("stop","length"),Answer(good).Replace("assistant","user"),Answer(good).Replace("\"content\":","\"tool_calls\":[],\"content\":"),Answer(good).Replace("\"content\":","\"refusal\":\"no\",\"content\":"),Answer(good).Replace("\"choices\":","\"choices\":[],\"choices\":"),"{"})reject(()=>plan.Parse(Encoding.UTF8.GetBytes(bad)),"Incomplete/refusal/tool/duplicate protocol envelope refused");
        reject(()=>plan.Parse(new byte[AssistantPlan.MaxResponse+1]),"Response cap before parse");reject(()=>Plan(request:new string('x',2001)),"Request characters not truncated");reject(()=>Plan(request:"\ud800"),"Bad UTF16 input refused");reject(()=>AssistantPlan.Create(service,"x",choices.Concat(choices).ToArray(),Array.Empty<string>()),"Duplicate candidate IDs refused");
        var prefs=new Prefs();string old=prefs.Serialize();check(!old.Contains("Assistant")&&!prefs.IsOn(Feature.Assistant),"Assistant absent and default off");prefs.SaveAssistant(service);using(var saved=JsonDocument.Parse(prefs.Serialize()))check(Prefs.Parse(prefs.Serialize()).Assistant==service&&!prefs.IsOn(Feature.Assistant)&&!saved.RootElement.GetProperty("Assistant").GetRawText().Contains("key",StringComparison.OrdinalIgnoreCase),"Only non-secret assistant settings persist");
        prefs.SavePreset(new(Guid.NewGuid().ToString("N"),"PRIVATE-PRESET",new("png",0,90)));prefs.Set(Feature.ImagePresets,true);prefs.SaveApp(new(Guid.NewGuid().ToString("N"),"PRIVATE-APP","C:\\private.exe","",false));prefs.Set(Feature.AppRouting,true);
        check(!AssistantPlan.Offered(prefs,new[]{"C:\\a.png"}).Any(x=>x.Title.Contains("PRIVATE")),"Custom app/preset labels not offered to model");
        var fake=new Fake(async(r,t)=>{check(r.RequestUri!.AbsoluteUri==service.Endpoint&&r.Method==HttpMethod.Post&&r.Headers.Authorization?.ToString()=="Bearer TEST-KEY","HTTP exact endpoint/post/auth");check(await r.Content!.ReadAsStringAsync(t)==plan.Json&&!plan.Json.Contains("TEST-KEY"),"HTTP body byte-content equals complete secret-free preview");return new(HttpStatusCode.OK){Content=new StringContent(Answer(good))};});
        check(AssistantClient.SendAsync(plan,"TEST-KEY",default,fake).GetAwaiter().GetResult().ActionId=="convert.png"&&fake.Calls==1&&fake.Disposed,"Fake HTTP single request and disposal");
        foreach(var status in new[]{301,302,307,308,401,429,500})
        {var h=new Fake((r,t)=>Task.FromResult(new HttpResponseMessage((HttpStatusCode)status){Content=new StringContent("SECRET-ERROR"),Headers={Location=new Uri("https://evil.example/")}}));reject(()=>AssistantClient.SendAsync(plan,"KEY",default,h).GetAwaiter().GetResult(),"HTTP redirects/errors refused");check(h.Calls==1&&h.Disposed,"No redirect/error retry");}
        var large=new Fake((r,t)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(new byte[AssistantPlan.MaxResponse+1])}));reject(()=>AssistantClient.SendAsync(plan,"KEY",default,large).GetAwaiter().GetResult(),"HTTP size cap");
        var local=Plan(service with{Endpoint="http://127.0.0.1:11434/v1/chat/completions"});var localFake=new Fake((r,t)=>{check(r.Headers.Authorization is null,"Local no-key sends no authorization");return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(Answer(good))});});AssistantClient.SendAsync(local,null,default,localFake).GetAwaiter().GetResult();
        using(var cancel=new CancellationTokenSource())
        {var delayed=new Fake(async(r,t)=>{cancel.Cancel();await Task.Delay(Timeout.Infinite,t);throw new Exception();});reject(()=>AssistantClient.SendAsync(plan,"KEY",cancel.Token,delayed).GetAwaiter().GetResult(),"In-flight HTTP cancelled and joined");check(delayed.Disposed,"Cancellation disposes request client");}
        using(var handler=AssistantClient.Handler())check(!handler.AllowAutoRedirect&&!handler.UseCookies&&!handler.UseProxy&&handler.Credentials is null&&handler.MaxResponseHeadersLength==16,"Production handler forbids redirect/cookies/proxy/integrated credentials");
    }
}
