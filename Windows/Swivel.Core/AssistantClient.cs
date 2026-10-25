using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Swivel.Core;

public static class AssistantClient
{
    public const string Failure="The assistant request failed or returned an unsupported HTTP response. No action was run. Check the endpoint, protocol, model and key; server error text is not shown.";
    public static SocketsHttpHandler Handler()=>new(){AllowAutoRedirect=false,UseCookies=false,UseProxy=false,Credentials=null,ConnectTimeout=TimeSpan.FromSeconds(10),MaxResponseHeadersLength=16,AutomaticDecompression=DecompressionMethods.None};
    public static async Task<AssistantSuggestion> SendAsync(AssistantPlan plan,string? key,CancellationToken token,HttpMessageHandler? testHandler=null)
    {
        token.ThrowIfCancellationRequested();if(key is not null)AssistantSettings.ValidateKey(key);if(key is null&&!plan.Settings.Local)throw new UserError("Save an API key for this service, or use a literal loopback service without a key.");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var client=new HttpClient(testHandler??Handler(),true){Timeout=Timeout.InfiniteTimeSpan};
        using var request=new HttpRequestMessage(HttpMethod.Post,plan.Settings.Endpoint){Version=HttpVersion.Version11,VersionPolicy=HttpVersionPolicy.RequestVersionExact,Content=new StringContent(plan.Json,Encoding.UTF8,"application/json")};
        request.Headers.Accept.Add(new("application/json"));
        if(plan.Settings.Provider==AssistantProvider.ClaudeMessages){request.Headers.Add("anthropic-version","2023-06-01");if(key is not null)request.Headers.Add("x-api-key",key);}
        else if(key is not null)request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",key);
        try
        {
            using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token).ConfigureAwait(false);
            if(!response.IsSuccessStatusCode||response.Content.Headers.ContentLength>AssistantPlan.MaxResponse||response.Content.Headers.ContentEncoding.Count!=0)throw new UserError(Failure);
            using var input=await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);using var bytes=new MemoryStream();byte[] buffer=new byte[4096];
            while(true){int read=await input.ReadAsync(buffer.AsMemory(0,(int)Math.Min(buffer.Length,AssistantPlan.MaxResponse+1-bytes.Length)),timeout.Token).ConfigureAwait(false);if(read==0)break;bytes.Write(buffer,0,read);if(bytes.Length>AssistantPlan.MaxResponse)throw new UserError(Failure);}
            token.ThrowIfCancellationRequested();return plan.Parse(bytes.ToArray());
        }
        catch(OperationCanceledException)when(!token.IsCancellationRequested){throw new UserError("The assistant request timed out. No action was run.");}
        catch(HttpRequestException){throw new UserError(Failure);}
        catch(IOException){throw new UserError(Failure);}
    }
}
