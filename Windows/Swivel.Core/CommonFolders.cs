using System;
using System.Collections.Generic;

namespace Swivel.Core;

public enum CommonFolderKind { Desktop, Downloads, Documents, Pictures, Music, Videos }
public sealed class CommonFolderChoice
{
    public CommonFolderKind Kind{get;}public string ReportedPath{get;}public string? Path{get;}public string? Issue{get;}public uint? NativeError{get;}public bool Truncated{get;}
    public bool CanAttempt=>Path is not null;
    private CommonFolderChoice(CommonFolderKind kind,string reported,string? path,string? issue,uint? nativeError,bool truncated){Kind=kind;ReportedPath=reported;Path=path;Issue=issue;NativeError=nativeError;Truncated=truncated;}
    public static CommonFolderChoice FromReported(CommonFolderKind kind,string? raw,bool truncated=false)
    {
        _=CommonFolders.Name(kind);var resolved=ReportedFolderPath.FromReported(raw);
        return new(kind,resolved.ReportedPath,truncated?null:resolved.Path,truncated?"Reported folder path exceeds preview limits.":resolved.Issue,null,truncated);
    }
    public static CommonFolderChoice Unavailable(CommonFolderKind kind,string issue,uint? error=null)
    {
        _=CommonFolders.Name(kind);if(issue is not ("Windows did not report this location." or "Windows could not report this folder location." or "Windows returned no path for this folder." or "COM initialization for folder lookup failed."))throw new ArgumentException("Unknown folder lookup issue.");
        return new(kind,"",null,issue,error,false);
    }
}
public static class CommonFolders
{
    public static Guid DownloadsId=>new("374de290-123f-4565-9164-39c4925e467b");
    public static IReadOnlyList<CommonFolderKind> Kinds{get;}=Array.AsReadOnly(Enum.GetValues<CommonFolderKind>());
    public const string Scope="Review six current-user locations reported by Windows, then explicitly select one to six folders for read-only metadata maps. No folder is selected initially. Locations can be redirected, unavailable, network-based or cloud-backed; a valid-looking path is not proof of access or ownership. This chooser does not create a folder or enumerate its children. Existing local path and system/link/cloud protections remain in force when measuring.";
    public const string Limits="Each selected root is measured separately, one at a time: up to four levels, 20,000 entries and about 30 soft seconds per root, at most six roots. A blocked system call may take longer; cancellation waits for cleanup. Folder roots can overlap, and hard links can repeat, so their logical historical bytes must not be added as profile usage, physical disk usage or reclaimable space. Exact repeated reported paths are visibly skipped. Partial and failed roots stay visible. No child file contents, cleanup, deletion, permission change, persistence, network test or assistant upload is requested.";
    public const string MapContext="One explicitly selected Windows-reported common-folder location only. This historical metadata map is separate from every other selected root; roots may overlap, so do not add their totals. System/link/cloud protections, depth/entry/time limits and skipped or partial items remain in force. Logical bytes are not physical or reclaimable space, and files are not classified as safe to delete. Copy Current View explicitly includes displayed names and path. No rescan, cleanup or assistant upload is performed.";
    public static string Name(CommonFolderKind kind)=>kind switch{CommonFolderKind.Desktop=>"Desktop",CommonFolderKind.Downloads=>"Downloads",CommonFolderKind.Documents=>"Documents",CommonFolderKind.Pictures=>"Pictures",CommonFolderKind.Music=>"Music",CommonFolderKind.Videos=>"Videos",_=>throw new ArgumentOutOfRangeException(nameof(kind))};
}
