using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace Swivel.Core;

// Original source-markup subset, not an HTML5 DOM/renderer or sanitizer.
// Only caller-supplied bytes are read; no URI/resource/document engine is used.
public static class DocumentHtml
{
    public const int MaxDepth=128, MaxTokens=100_000, MaxTag=16_384, MaxAttributes=128, MaxName=64;
    public const string Invalid="Use a complete, explicitly closed HTML document with html and body elements. This source-markup reader does not repair browser HTML, omitted end tags or malformed attributes.";
    public const string Unsupported="This HTML uses unsupported elements, scripts, event-handler attributes or declarations. Only the documented static source-markup subset is supported; no browser or external resource is loaded.";
    public const string EncodingError="HTML input must be strict UTF-8, optionally with a UTF-8 BOM. Only UTF-8 charset declarations are accepted; legacy encodings, UTF-16 HTML and http-equiv metadata are not supported here.";
    public const string Limit="HTML is limited to 8 MiB input, 2,000,000 UTF-16 units each for decoded markup and extracted text, depth 128, 100,000 markup tokens, 16,384 units per tag, 128 attributes per tag and 64-character names.";
    public const string EntityError="HTML numeric references need a semicolon, at most eight digits, and a valid non-control Unicode scalar (tab, CR and LF are allowed). Unknown named references remain literal.";
    private static readonly HashSet<string> Void=Set("area base br col embed hr img input link meta param source track wbr");
    private static readonly HashSet<string> Blocks=Set("address article aside blockquote caption dd details dialog div dl dt fieldset figcaption figure footer form h1 h2 h3 h4 h5 h6 header hgroup hr legend li main menu nav ol p pre section summary table tbody td tfoot th thead tr ul");
    private static readonly HashSet<string> Omitted=Set("head style title template noscript object iframe canvas audio video select textarea");
    private static readonly HashSet<string> Raw=Set("style title textarea iframe");
    private static readonly HashSet<string> Head=Set("base link meta title style");
    private static readonly HashSet<string> Known=Set("html head body a abbr address area article aside audio b base bdi bdo blockquote br button canvas caption cite code col colgroup data datalist dd del details dfn dialog div dl dt em embed fieldset figcaption figure footer form h1 h2 h3 h4 h5 h6 header hgroup hr i iframe img input ins kbd label legend li link main map mark menu meta meter nav noscript object ol optgroup option output p param picture pre progress q rp rt ruby s samp section select slot small source span strong style sub summary sup table tbody td template textarea tfoot th thead time title tr track u ul var video wbr");
    private static HashSet<string> Set(string words)=>new(words.Split(' '),StringComparer.Ordinal);
    private sealed record Frame(string Name,bool Body,bool Visible,bool Pre,bool Excluded);
    private sealed record Tag(string Name,bool Closing,bool SelfClosing,bool Hidden,string? Charset,bool HttpEquiv);
    public static string Read(byte[] bytes,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();if(bytes.Length>DocumentText.MaxInputBytes)throw new UserError(Limit);
        int skip=bytes.AsSpan().StartsWith(new byte[]{0xef,0xbb,0xbf})?3:0;string source;
        try{source=new UTF8Encoding(false,true).GetString(bytes,skip,bytes.Length-skip);}catch(DecoderFallbackException){throw new UserError(EncodingError);}
        if(source.Length>DocumentText.MaxCharacters)throw new UserError(Limit);
        DocumentText.Validate(source,token);
        var output=new StringBuilder();var stack=new List<Frame>();int at=0,tokens=0;bool root=false,head=false,body=false,doctype=false,space=false,boundary=false;
        void Append(char c){if(output.Length>=DocumentText.MaxCharacters)throw new UserError(Limit);output.Append(c);}
        void Break(){space=false;boundary=true;}
        void Flush(char next)
        {
            if(output.Length>0)
            {
                char last=output[^1];
                if(boundary){if(last is not '\n' and not '\r'&&next is not '\n' and not '\r')Append('\n');}
                else if(space&&last is not '\n' and not '\r' and not ' ')Append(' ');
            }
            boundary=space=false;
        }
        void Text(ReadOnlySpan<char> raw,bool pre)
        {
            ValidateNumeric(raw,token);string decoded=WebUtility.HtmlDecode(raw.ToString());
            DocumentText.Validate(decoded,token);
            for(int i=0;i<decoded.Length;i++)
            {
                if((i&4095)==0)token.ThrowIfCancellationRequested();char c=decoded[i];
                if(!pre&&Space(c)){space=true;continue;}
                Flush(c);Append(c);
            }
        }
        while(at<source.Length)
        {
            token.ThrowIfCancellationRequested();
            if(stack.Count>0&&Raw.Contains(stack[^1].Name))at=RawEnd(source,at,stack[^1].Name,token);
            if(source[at]!='<')
            {
                int end=source.IndexOf('<',at);if(end<0)end=source.Length;
                var segment=source.AsSpan(at,end-at);var current=stack.Count==0?null:stack[^1];
                if(current?.Visible==true)Text(segment,current.Pre);
                else if(current?.Body!=true&&!Raw.Contains(current?.Name??"")&&!OnlySpace(segment))throw new UserError(Invalid);
                at=end;continue;
            }
            if(++tokens>MaxTokens)throw new UserError(Limit);
            if(source.AsSpan(at).StartsWith("<!--",StringComparison.Ordinal))
            {int end=source.IndexOf("-->",at+4,StringComparison.Ordinal);if(end<0)throw new UserError(Invalid);at=end+3;continue;}
            if(source.AsSpan(at).StartsWith("<!",StringComparison.Ordinal))
            {
                int end=source.IndexOf('>',at+2);if(end<0)throw new UserError(Invalid);if(end-at+1>MaxTag)throw new UserError(Limit);
                if(root||doctype||!source.AsSpan(at+2,end-at-2).Trim().Equals("doctype html",StringComparison.OrdinalIgnoreCase))throw new UserError(Unsupported);
                doctype=true;at=end+1;continue;
            }
            var tag=ReadTag(source,ref at,token);string name=tag.Name;
            if(!Known.Contains(name))throw new UserError(Unsupported);
            if(tag.Closing)
            {
                if(stack.Count==0||stack[^1].Name!=name||Void.Contains(name))throw new UserError(Invalid);
                var closing=stack[^1];if(closing.Visible&&Blocks.Contains(name))Break();stack.RemoveAt(stack.Count-1);continue;
            }
            var parent=stack.Count==0?null:stack[^1];
            if(name=="html") {if(root||parent is not null)throw new UserError(Invalid);root=true;}
            else if(parent is null)throw new UserError(Invalid);
            else if(name=="head") {if(parent.Name!="html"||head||body)throw new UserError(Invalid);head=true;}
            else if(name=="body") {if(parent.Name!="html"||body)throw new UserError(Invalid);body=true;}
            else if(parent.Name=="html"||parent.Name=="head"&&!Head.Contains(name))throw new UserError(Invalid);
            else if(Head.Contains(name)&&name!="style"&&parent.Name!="head")throw new UserError(Invalid);
            if(name=="meta"&&(tag.HttpEquiv||tag.Charset is not null&& !IsUtf8(WebUtility.HtmlDecode(tag.Charset).Trim())))throw new UserError(EncodingError);
            bool inBody=name=="body"||parent?.Body==true;
            bool excluded=tag.Hidden||Omitted.Contains(name)||parent?.Excluded==true;
            bool visible=inBody&&!excluded;
            bool pre=name=="pre"||parent?.Pre==true;
            if(name=="pre"&&parent?.Pre==true)throw new UserError(Invalid);
            if(visible)
            {
                if(name=="br"){space=boundary=false;Append('\n');}
                else if(Blocks.Contains(name))Break();
            }
            if(!Void.Contains(name))
            {
                if(tag.SelfClosing)throw new UserError(Invalid);
                if(stack.Count>=MaxDepth)throw new UserError(Limit);
                stack.Add(new(name,inBody,visible,pre,excluded));
            }
        }
        token.ThrowIfCancellationRequested();if(!root||!body||stack.Count!=0)throw new UserError(Invalid);
        string result=output.ToString();DocumentText.Validate(result,token);return result;
    }
    private static bool IsUtf8(string text)=>text.Equals("utf-8",StringComparison.OrdinalIgnoreCase)||text.Equals("utf8",StringComparison.OrdinalIgnoreCase);
    private static bool Space(char c)=>c is ' ' or '\t' or '\r' or '\n';
    private static bool OnlySpace(ReadOnlySpan<char> text){foreach(char c in text)if(!Space(c))return false;return true;}
    private static bool NameStart(char c)=>char.IsAsciiLetter(c)||c=='_';
    private static bool NamePart(char c)=>NameStart(c)||char.IsAsciiDigit(c)||c is '-' or ':' or '.';
    private static Tag ReadTag(string source,ref int at,CancellationToken token)
    {
        int start=at++;bool closing=at<source.Length&&source[at]=='/';if(closing)at++;
        int nameAt=at;if(at>=source.Length||!char.IsAsciiLetter(source[at]))throw new UserError(Invalid);
        while(at<source.Length&&NamePart(source[at]))at++;
        if(at-nameAt>MaxName)throw new UserError(Limit);string name=source[nameAt..at].ToLowerInvariant();
        bool hidden=false,http=false;string? charset=null;var attributes=new HashSet<string>(StringComparer.Ordinal);
        while(true)
        {
            token.ThrowIfCancellationRequested();if(at-start>=MaxTag)throw new UserError(Limit);
            int beforeSpace=at;while(at<source.Length&&Space(source[at]))at++;
            if(at>=source.Length)throw new UserError(Invalid);
            if(source[at]=='>'){at++;if(at-start>MaxTag)throw new UserError(Limit);return new(name,closing,false,hidden,charset,http);}
            if(source[at]=='/'&&at+1<source.Length&&source[at+1]=='>')
            {if(closing)throw new UserError(Invalid);at+=2;if(at-start>MaxTag)throw new UserError(Limit);return new(name,false,true,hidden,charset,http);}
            if(closing||beforeSpace==at||!NameStart(source[at]))throw new UserError(Invalid);
            int first=at;while(at<source.Length&&NamePart(source[at]))at++;
            if(at-first>MaxName)throw new UserError(Limit);string attr=source[first..at].ToLowerInvariant();
            if(!attributes.Add(attr))throw new UserError(Invalid);if(attributes.Count>MaxAttributes)throw new UserError(Limit);
            if(attr.StartsWith("on",StringComparison.Ordinal))throw new UserError(Unsupported);
            int afterName=at;while(at<source.Length&&Space(source[at]))at++;string? value=null;
            if(at<source.Length&&source[at]=='=')
            {
                at++;while(at<source.Length&&Space(source[at]))at++;if(at>=source.Length)throw new UserError(Invalid);
                int valueAt;
                if(source[at] is '\'' or '"')
                {
                    char quote=source[at++];valueAt=at;
                    while(at<source.Length&&source[at]!=quote){if(at-start>=MaxTag)throw new UserError(Limit);at++;}
                    if(at>=source.Length)throw new UserError(Invalid);if(attr=="charset")value=source[valueAt..at];at++;
                }
                else
                {
                    valueAt=at;
                    while(at<source.Length&&!Space(source[at])&&source[at]!='>')
                    {if(at-start>=MaxTag)throw new UserError(Limit);if(source[at] is '<' or '"' or '\'' or '=' or '`')throw new UserError(Invalid);at++;}
                    if(at==valueAt)throw new UserError(Invalid);if(attr=="charset")value=source[valueAt..at];
                }
            }
            else at=afterName;
            if(attr=="hidden")hidden=true;if(attr=="http-equiv")http=true;if(attr=="charset")charset=value??"";
        }
    }
    private static int RawEnd(string source,int at,string name,CancellationToken token)
    {
        string prefix="</"+name;
        for(;;)
        {
            token.ThrowIfCancellationRequested();int found=source.IndexOf(prefix,at,StringComparison.OrdinalIgnoreCase);
            if(found<0)throw new UserError(Invalid);int end=found+prefix.Length;
            if(end<source.Length&&(Space(source[end])||source[end] is '>' or '/'))return found;
            at=end;
        }
    }
    private static void ValidateNumeric(ReadOnlySpan<char> text,CancellationToken token)
    {
        for(int i=0;i<text.Length;i++)
        {
            if((i&4095)==0)token.ThrowIfCancellationRequested();if(text[i]!='&'||i+1>=text.Length||text[i+1]!='#')continue;
            int at=i+2,radix=10;if(at<text.Length&&text[at] is 'x' or 'X'){radix=16;at++;}
            int value=0,digits=0;
            while(at<text.Length&&text[at]!=';')
            {
                char c=text[at++];int digit=c is >= '0' and <= '9'?c-'0':c is >= 'a' and <= 'f'?c-'a'+10:c is >= 'A' and <= 'F'?c-'A'+10:-1;
                if(digit<0||digit>=radix||++digits>8||value>(0x10ffff-digit)/radix)throw new UserError(EntityError);value=value*radix+digit;
            }
            if(digits==0||at>=text.Length||!Rune.IsValid(value)||(Rune.IsControl(new Rune(value))&&value is not 9 and not 10 and not 13))throw new UserError(EntityError);
            i=at;
        }
    }
}
