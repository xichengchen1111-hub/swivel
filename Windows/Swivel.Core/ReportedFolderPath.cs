using System;
using System.Globalization;
using System.Text;

namespace Swivel.Core;

// Lexical report validation only. Access, reparse/cloud state and identity are
// checked by the existing held metadata-directory layer after explicit selection.
public sealed class ReportedFolderPath
{
    public const string Unreported="Windows did not report this location.";
    public string ReportedPath{get;}public string? Path{get;}public string? Issue{get;}public bool CanAttempt=>Path is not null;
    private ReportedFolderPath(string reported,string? path,string? issue){ReportedPath=reported;Path=path;Issue=issue;}
    public static ReportedFolderPath FromReported(string? raw)
    {
        string reported=raw??"";if(reported.Length==0)return new(reported,null,Unreported);
        string path=reported.EndsWith('\\')?reported[..^1]:reported;
        try
        {
            WindowsPathPolicy.Validate(path);_=new UTF8Encoding(false,true).GetByteCount(path);
            foreach(var rune in path.EnumerateRunes())if(Rune.GetUnicodeCategory(rune)==UnicodeCategory.Format)throw new UserError("This file name is not supported in the Windows preview.");
            return new(reported,path,null);
        }
        catch(EncoderFallbackException){return new(reported,null,"This file name is not supported in the Windows preview.");}
        catch(UserError error){return new(reported,null,error.Message);}
    }
}
