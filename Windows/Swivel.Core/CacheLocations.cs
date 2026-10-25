using System;
using System.Collections.Generic;
using System.Buffers;
using System.Globalization;
using System.Text;

namespace Swivel.Core;

public enum CacheLocationKind { Temporary, LegacyInternetCache }
public sealed class CacheLocation
{
    public CacheLocationKind Kind{get;}public string ReportedPath{get;}public string? Path{get;}public string? Issue{get;}
    public bool CanAttempt=>Path is not null;
    internal CacheLocation(CacheLocationKind kind,string reported,string? path,string? issue){Kind=kind;ReportedPath=reported;Path=path;Issue=issue;}
}
public static class CacheLocations
{
    public const string Scope="Review Windows-reported locations, then choose one explicitly before reading directory metadata. The temporary path can be redirected by environment variables; the legacy WinINET location is not a full modern-browser cache inventory. A valid-looking path is not proof of access or cache ownership. Protected/system folders, links, cloud placeholders and unavailable roots can still be refused by the existing local-folder checks. No folder is created or searched while this list is shown.";
    public const string Warning="Temporary and cache locations can contain work in progress or files an app still needs. This tool does not classify files as safe to delete, identify unused items or estimate reclaimable space. It only measures one confirmed location with the existing bounded read-only size map; file and folder names become visible. No cleanup, deletion, permission change, automatic assistant upload or undo operation is provided.";
    public const string ResultContext="One confirmed Windows-reported temporary or legacy WinINET location only; environment and policy can redirect it. This is not a complete Windows or modern-browser cache inventory, or proof that every item is cache. Files can still be in use or contain work in progress; nothing is classified as safe to delete or reclaimable. Copy Current View explicitly includes the displayed names and path. No cleanup, deletion, permission change or assistant upload is performed.";
    public const string Unreported=ReportedFolderPath.Unreported;
    public static string Name(CacheLocationKind kind)=>kind switch{CacheLocationKind.Temporary=>"Configured Temporary Folder",CacheLocationKind.LegacyInternetCache=>"Legacy WinINET Cache Folder",_=>throw new ArgumentOutOfRangeException(nameof(kind))};
    public static CacheLocation FromReported(CacheLocationKind kind,string? raw)
    {
        _=Name(kind);var resolved=ReportedFolderPath.FromReported(raw);
        return new(kind,resolved.ReportedPath,resolved.Path,resolved.Issue);
    }
    public static string VisiblePath(string text)
    {
        int limit=Math.Min(512,text.Length);if(limit<text.Length&&char.IsHighSurrogate(text[limit-1])&&char.IsLowSurrogate(text[limit]))limit--;
        var visible=new StringBuilder();int position=0;
        while(position<limit)
        {
            var status=Rune.DecodeFromUtf16(text.AsSpan(position,limit-position),out var rune,out int consumed);
            if(status!=OperationStatus.Done){visible.Append("[U+").Append(((int)text[position]).ToString("X4",CultureInfo.InvariantCulture)).Append(']');position++;continue;}
            if(Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)visible.Append("[U+").Append(rune.Value.ToString("X4",CultureInfo.InvariantCulture)).Append(']');else visible.Append(rune.ToString());position+=consumed;
        }
        if(limit<text.Length)visible.Append('…');return visible.ToString();
    }
    public static IReadOnlyList<CacheLocation> Choices(string? temporary,string? internetCache)=>Array.AsReadOnly(new[]{FromReported(CacheLocationKind.Temporary,temporary),FromReported(CacheLocationKind.LegacyInternetCache,internetCache)});
}
