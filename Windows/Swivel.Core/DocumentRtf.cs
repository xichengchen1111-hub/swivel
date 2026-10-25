using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;

namespace Swivel.Core;

// Original text-only RTF lexer. No renderer, object loading, font loading or
// platform code-page provider. Unknown encodings never become guessed text.
internal static class DocumentRtf
{
    internal const string Invalid="Choose a well-formed RTF 1 document. Limits: 8 MiB input, 2,000,000 extracted UTF-16 units, 128 group levels and 32-letter control words. Unicode escapes and Windows-1252 bytes are supported; other byte encodings and Unicode alternative groups are refused.";
    private const string EncodingError="This RTF uses an unsupported byte encoding or font charset with visible non-ASCII bytes. Save it as Unicode RTF or UTF-8 text. Font and symbol glyph mappings are not interpreted.";
    private const string Cp1252="€\u0081‚ƒ„…†‡ˆ‰Š‹Œ\u008dŽ\u008f\u0090‘’“”•–—˜™š›œ\u009džŸ";
    private struct State
    {
        internal int Uc;
        internal bool Skip,Hidden,Deleted,Start,FontTable;
    }
    private static readonly HashSet<string> Destinations=new(StringComparer.Ordinal)
    {
        "fonttbl","colortbl","stylesheet","info","pict","object","objdata","shp","shpinst","shprslt",
        "header","headerl","headerr","headerf","footer","footerl","footerr","footerf","footnote",
        "annotation","atnauthor","atnid","field","fldinst","fldrslt","listtable","listoverridetable",
        "listtext","pntext","pn","revtbl","rsidtbl","filetbl","generator","xmlnstbl","datastore",
        "themedata","colorschememapping","latentstyles","nonshppict","shppict","aftncn","ftncn",
        "aftnsep","aftnsepc","ftnsep","ftnsepc","background","private"
    };
    internal static string Read(byte[] bytes,CancellationToken token)
    {
        if(bytes.Length>DocumentText.MaxInputBytes||bytes.Length<7||!bytes.AsSpan(0,6).SequenceEqual("{\\rtf1"u8))throw new UserError(Invalid);
        var text=new StringBuilder();var stack=new Stack<State>();var state=new State{Uc=1};
        bool root=false,closed=false,header=false,unknownEncoding=false,sawHigh=false;
        int fallback=0,at=0;
        void Append(char c)
        {
            if(state.Skip||state.Hidden||state.Deleted)return;
            if(text.Length>=DocumentText.MaxCharacters)throw new UserError(Invalid);
            text.Append(c);
        }
        void Byte(byte b)
        {
            if(fallback>0){fallback--;return;}
            if(state.Skip||state.Hidden||state.Deleted)return;
            if(b>=128)
            {
                sawHigh=true;if(unknownEncoding)throw new UserError(EncodingError);
                Append(b<160?Cp1252[b-128]:(char)b);
            }
            else Append((char)b);
        }
        void UnknownEncoding()
        {unknownEncoding=true;if(sawHigh)throw new UserError(EncodingError);}
        while(at<bytes.Length)
        {
            token.ThrowIfCancellationRequested();byte b=bytes[at++];
            if(closed){if(b is not (9 or 10 or 13 or 32))throw new UserError(Invalid);continue;}
            if(b=='{')
            {
                fallback=0;if(stack.Count>=128)throw new UserError(Invalid);
                if(stack.Count==0){if(root)throw new UserError(Invalid);root=true;}
                stack.Push(state);state.Start=true;continue;
            }
            if(b=='}')
            {
                fallback=0;if(stack.Count==0||!header)throw new UserError(Invalid);
                state=stack.Pop();if(stack.Count==0)closed=true;continue;
            }
            if(stack.Count==0)throw new UserError(Invalid);
            if(b is 10 or 13)continue;
            if(b!='\\'){if(!header)throw new UserError(Invalid);state.Start=false;Byte(b);continue;}
            if(at>=bytes.Length)throw new UserError(Invalid);
            byte symbol=bytes[at++];
            if(!Letter(symbol))
            {
                if(!header)throw new UserError(Invalid);
                if(symbol=='\'')
                {
                    if(at+2>bytes.Length||Hex(bytes[at])<0||Hex(bytes[at+1])<0)throw new UserError(Invalid);
                    byte value=(byte)(Hex(bytes[at])*16+Hex(bytes[at+1]));at+=2;state.Start=false;Byte(value);continue;
                }
                if(fallback>0){fallback--;continue;}
                if(symbol=='*'){if(!state.Start)throw new UserError(Invalid);state.Skip=true;continue;}
                state.Start=false;
                if(symbol is (byte)'\\' or (byte)'{' or (byte)'}')Append((char)symbol);
                else if(symbol=='~')Append('\u00a0');else if(symbol=='_')Append('\u2011');else if(symbol=='-')Append('\u00ad');
                else if(symbol is 10 or 13){if(symbol==13&&at<bytes.Length&&bytes[at]==10)at++;Append('\n');}
                continue;
            }
            int start=at-1;while(at<bytes.Length&&Letter(bytes[at])){if(at-start>=32)throw new UserError(Invalid);at++;}
            string word=Encoding.ASCII.GetString(bytes,start,at-start);int numberStart=at;
            if(at<bytes.Length&&bytes[at]=='-')at++;
            int digits=at;
            while(at<bytes.Length&&bytes[at] is >=(byte)'0' and <=(byte)'9'){token.ThrowIfCancellationRequested();at++;}
            bool hasNumber=at>digits;
            if(numberStart!=digits&&!hasNumber)throw new UserError(Invalid);
            int numberLength=at-numberStart;
            if(at<bytes.Length&&bytes[at]==' ')at++;
            int Number()
            {
                if(!hasNumber||numberLength>12||!int.TryParse(Encoding.ASCII.GetString(bytes,numberStart,numberLength),NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out int n))throw new UserError(Invalid);
                return n;
            }
            if(!header)
            {
                if(word!="rtf"||Number()!=1||stack.Count!=1)throw new UserError(Invalid);
                header=true;state.Start=false;continue;
            }
            if(word=="rtf")throw new UserError(Invalid);
            // Binary payload is lexical even in discarded groups/fallback: its
            // braces and slashes must never change parser state.
            if(word=="bin")
            {
                int length=Number();if(length<0||length>bytes.Length-at)throw new UserError(Invalid);
                at+=length;if(fallback>0)fallback--;state.Start=false;continue;
            }
            if(fallback>0){fallback--;continue;}
            bool groupStart=state.Start;state.Start=false;
            if(Destinations.Contains(word))
            {
                if(!groupStart&&!state.Skip)throw new UserError(Invalid);
                state.Skip=true;if(word=="fonttbl")state.FontTable=true;continue;
            }
            if(state.FontTable&&word is "fcharset" or "cpg")
            {int n=Number();if(word=="cpg"?n!=1252:n is not 0 and not 1)UnknownEncoding();continue;}
            if(state.Skip)continue;
            if(word=="upr")throw new UserError(Invalid);
            if(word is "ansicpg" or "mac" or "pc" or "pca" or "ansi")
            {
                if(word=="ansicpg"){if(Number()!=1252)UnknownEncoding();}
                else if(word!="ansi")UnknownEncoding();
                // Unknown declarations remain conservative; a later ANSI
                // marker never erases an explicit unsupported code page.
                continue;
            }
            if(word=="uc"){int n=Number();if(n<0||n>16)throw new UserError(Invalid);state.Uc=n;continue;}
            if(word=="u")
            {
                int n=Number();if(n<short.MinValue||n>ushort.MaxValue)throw new UserError(Invalid);
                Append(unchecked((char)(ushort)n));fallback=state.Uc;continue;
            }
            if(word is "v" or "deleted")
            {
                bool enabled=!hasNumber;
                if(hasNumber){enabled=false;for(int i=digits;i<numberStart+numberLength;i++)if(bytes[i]!='0'){enabled=true;break;}}
                if(word=="v")state.Hidden=enabled;else state.Deleted=enabled;continue;
            }
            if(word=="plain"){state.Hidden=false;state.Deleted=false;continue;}
            switch(word)
            {
                case "par":case "line":case "page":case "sect":case "row":case "nestrow":Append('\n');break;
                case "tab":case "cell":case "nestcell":Append('\t');break;
                case "emdash":Append('\u2014');break;case "endash":Append('\u2013');break;case "bullet":Append('\u2022');break;
                case "lquote":Append('\u2018');break;case "rquote":Append('\u2019');break;case "ldblquote":Append('\u201c');break;case "rdblquote":Append('\u201d');break;
                case "zwj":Append('\u200d');break;case "zwnj":Append('\u200c');break;
                case "enspace":Append('\u2002');break;case "emspace":Append('\u2003');break;case "qmspace":Append('\u2005');break;
            }
        }
        if(!closed||stack.Count!=0)throw new UserError(Invalid);
        token.ThrowIfCancellationRequested();string result=text.ToString();DocumentText.Validate(result,token);return result;
    }
    private static bool Letter(byte b)=>b is >=(byte)'a' and <=(byte)'z' or >=(byte)'A' and <=(byte)'Z';
    private static int Hex(byte b)=>b is >=(byte)'0' and <=(byte)'9'?b-'0':b is >=(byte)'a' and <=(byte)'f'?b-'a'+10:b is >=(byte)'A' and <=(byte)'F'?b-'A'+10:-1;
}
