using System;
using System.Linq;

namespace Swivel.Core;

public enum AssistantProvider { ChatCompletions, ClaudeMessages }
public sealed record AssistantSettings(AssistantProvider Provider,string Endpoint,string Model,bool CompletionTokenLimit=false)
{
    public const string Invalid="Choose a complete HTTPS API endpoint (literal loopback HTTP is allowed), a protocol and an ASCII model identifier up to 128 characters. URL credentials, queries and fragments are not accepted.";
    public AssistantSettings Normalize()
    {
        if(!Enum.IsDefined(Provider)||string.IsNullOrEmpty(Model)||Model.Length>128||Model.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c is not '.' and not '-' and not '_' and not '/' and not ':' and not '@')||(Provider==AssistantProvider.ClaudeMessages&&CompletionTokenLimit))throw new UserError(Invalid);
        string endpoint=WebAddress.Normalize(Endpoint);var uri=new Uri(endpoint);if(uri.Query.Length!=0||uri.Fragment.Length!=0)throw new UserError(Invalid);
        return this with{Endpoint=endpoint};
    }
    public bool Local{get{var u=new Uri(Normalize().Endpoint);return u.Host.Equals("localhost",StringComparison.OrdinalIgnoreCase)||u.Host is "127.0.0.1" or "[::1]";}}
    public string CredentialTarget{get{var s=Normalize();var u=new Uri(s.Endpoint);return "Swivel.WindowsPreview.Assistant.v1/"+s.Provider+"/"+u.Scheme+"/"+u.Host.ToLowerInvariant()+":"+u.Port;}}
    public static void ValidateKey(string key)
    {if(key.Length is <1 or >2048||key.Any(c=>c<33||c>126))throw new UserError("Use an API key of 1–2048 visible ASCII characters without spaces.");}
}
