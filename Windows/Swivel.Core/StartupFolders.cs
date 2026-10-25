using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading;

namespace Swivel.Core;

public enum StartupFolderKind { CurrentUser, Common }
public sealed class StartupFolderChoice
{
    public StartupFolderKind Kind{get;}public string ReportedPath{get;}public string? Path{get;}public string? Issue{get;}public bool CanAttempt=>Path is not null;
    private StartupFolderChoice(StartupFolderKind kind,string reported,string? path,string? issue){Kind=kind;ReportedPath=reported;Path=path;Issue=issue;}
    public static StartupFolderChoice FromReported(StartupFolderKind kind,string? raw)
    {
        _=StartupFolders.Name(kind);var resolved=ReportedFolderPath.FromReported(raw);return new(kind,resolved.ReportedPath,resolved.Path,resolved.Issue);
    }
    public static StartupFolderChoice Unavailable(StartupFolderKind kind){_=StartupFolders.Name(kind);return new(kind,"",null,"Windows could not report this folder location.");}
}
public sealed record StartupFolderResult(StartupFolderChoice Choice,FolderScanResult? Scan,string? Issue,uint? Error,int EntryQuota,int DeadlineSeconds,DateTimeOffset Recorded);
public static class StartupFolders
{
    public const string Scope="Review the current-user and common logon Startup-folder locations reported by Windows, then explicitly select one or both to list direct entry metadata. Nothing is selected initially. Reviewing paths does not enumerate children or create folders. Redirected, unavailable, network, system, link and cloud-backed locations may be refused by the existing local-folder protections.";
    public const string Limits="One shared budget of 512 counted entries and about 10 soft seconds, direct children only. Remaining entry/time shares are divided among distinct selected paths; completed scans refund unused entries, while failed scans conservatively consume their reserved share. Native directory buffers can fetch additional metadata. Blocked system calls may take longer; cancellation waits for cleanup. Repeated paths, failures, partial results, skipped items and unattempted roots remain visible.";
    public const string Meaning="This is a historical folder-entry list, not proof that an item is enabled, runs at logon, exists at its target, is trusted or slows startup. Shortcut and Internet-shortcut hints use only the filename ending. No child file contents or shortcut targets are opened, resolved or executed; subfolders are not expanded. StartupApproved, scheduled tasks, Store startup tasks, other users and effective startup state are not queried. Existing registry and service checks are separate. Nothing is changed, disabled, deleted, saved or sent to an assistant.";
    public const string Privacy="Names and full displayed paths are visible. Copy Report includes every selected root, its limits and all recorded direct entries or failures. Copy Selected Item includes that item and its root limits. Copies are limited to 4 Mi UTF-16 units and refused whole if larger; nothing is copied automatically. Logical sizes are historical main-stream bytes, not physical or reclaimable space, and root sizes are not added together.";
    public const string Repeated="Same reported path as an earlier selected startup folder; not listed again.";
    public const string NoEntries="Shared startup-folder entry budget exhausted; this root was not opened.";
    public const string NoTime="Shared startup-folder time budget exhausted; this root was not opened.";
    public const string Unavailable="This startup folder could not be inspected.";
    public static string Name(StartupFolderKind kind)=>kind switch{StartupFolderKind.CurrentUser=>"Current-user Startup Folder",StartupFolderKind.Common=>"Common Startup Folder",_=>throw new ArgumentOutOfRangeException(nameof(kind))};
    public static string Hint(FolderSizeNode node)=>node.Directory?"Folder not expanded":node.Issue is not null?"Not classified":node.Name.EndsWith(".lnk",StringComparison.OrdinalIgnoreCase)?"Shortcut filename only":node.Name.EndsWith(".url",StringComparison.OrdinalIgnoreCase)?"Internet-shortcut filename only":"File metadata only";

    public static IReadOnlyList<StartupFolderResult> Scan(IReadOnlyList<StartupFolderChoice> selection,Func<StartupFolderChoice,IFolderMetadata> open,CancellationToken token,
        int maxEntries=512,int seconds=10,Func<TimeSpan>? elapsed=null)
    {
        ArgumentNullException.ThrowIfNull(selection);ArgumentNullException.ThrowIfNull(open);var choices=selection.ToArray();
        if(choices.Length is <1 or >2||choices.Any(c=>c is null||!c.CanAttempt)||choices.Select(c=>c.Kind).Distinct().Count()!=choices.Length)throw new ArgumentException("Invalid startup-folder selection.");
        if(maxEntries is <1 or >512||seconds is <1 or >10)throw new ArgumentOutOfRangeException(nameof(maxEntries));
        token.ThrowIfCancellationRequested();var clock=Stopwatch.StartNew();elapsed??=()=>clock.Elapsed;
        int remaining=maxEntries,remainingRoots=choices.Select(c=>c.Path!).Distinct(StringComparer.Ordinal).Count();var seen=new HashSet<string>(StringComparer.Ordinal);var results=new List<StartupFolderResult>(2);
        foreach(var choice in choices)
        {
            token.ThrowIfCancellationRequested();int quota=0,deadline=0;
            void Record(FolderScanResult? scan,string? issue=null,uint? error=null)=>results.Add(new(choice,scan,issue,error,quota,deadline,DateTimeOffset.Now));
            if(!seen.Add(choice.Path!)){Record(null,Repeated);continue;}
            double time=elapsed().TotalSeconds;
            if(remaining==0){remainingRoots--;Record(null,NoEntries);continue;}
            if(time>=seconds){remainingRoots--;Record(null,NoTime);continue;}
            quota=Math.Max(1,remaining/remainingRoots);deadline=Math.Clamp((int)Math.Ceiling(time+(seconds-time)/remainingRoots),1,seconds);remainingRoots--;
            try
            {
                FolderScanResult scan;IFolderMetadata? root=null;
                try
                {
                    root=open(choice);
                    token.ThrowIfCancellationRequested();remaining-=quota;
                    scan=FolderSizes.Scan(root,token,new(quota,0,deadline),elapsed);remaining+=quota-scan.Entries;
                }
                finally
                {
                    try{root?.Dispose();}
                    catch(Exception error){token.ThrowIfCancellationRequested();throw new AggregateException("A startup-folder reader could not be released.",error);}
                }
                token.ThrowIfCancellationRequested();Record(scan);
            }
            catch(Exception error)when(!token.IsCancellationRequested&&(error is UserError or IOException or UnauthorizedAccessException or SecurityException or NotSupportedException or Win32Exception))
            {Record(null,Unavailable,error is Win32Exception native?unchecked((uint)native.NativeErrorCode):null);}
        }
        token.ThrowIfCancellationRequested();return results.AsReadOnly();
    }
}
