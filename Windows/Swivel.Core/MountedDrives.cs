using System;
using System.Collections.Generic;
using System.Linq;

namespace Swivel.Core;

public sealed record DriveSpace(ulong AvailableToCaller,ulong TotalForCaller,ulong TotalFree);
public sealed record MountedDrive(char Letter,uint Type,string? FileSystem,uint? FileSystemFlags,DriveSpace? Space,uint? VolumeError,uint? SpaceError)
{
    public string Root=>Letter+@":\";
    public bool? DeclaredReadOnly=>FileSystemFlags is uint flags?(flags&0x80000)!=0:null;
}
public enum DriveIssueStage { ErrorModeSetup, Enumeration, ErrorModeRestore }
public sealed record DriveIssue(DriveIssueStage Stage,uint Error);
public sealed class MountedDriveSnapshot
{
    public DateTimeOffset Captured{get;}
    public IReadOnlyList<MountedDrive> Drives{get;}
    public IReadOnlyList<DriveIssue> Issues{get;}
    public bool UnknownMaskBits{get;}
    public MountedDriveSnapshot(IEnumerable<MountedDrive> drives,IEnumerable<DriveIssue> issues,bool unknownMaskBits,DateTimeOffset captured)
    {
        var rows=drives.ToArray();var problems=issues.ToArray();
        if(rows.Length>26||rows.Any(d=>d.Letter is <'A' or >'Z'||d.FileSystem?.Length>64)||rows.Select(d=>d.Letter).Distinct().Count()!=rows.Length||problems.Length>3||problems.Any(p=>!Enum.IsDefined(p.Stage)))throw new ArgumentException("Invalid mounted-drive snapshot.");
        Drives=Array.AsReadOnly(rows.OrderBy(d=>d.Letter).ToArray());Issues=Array.AsReadOnly(problems);UnknownMaskBits=unknownMaskBits;Captured=captured;
    }
}
public static class MountedDrives
{
    public const uint LetterMask=0x03ffffff;
    public const string Scope="Explicit read-only drive-letter metadata, at most A–Z. Only letters reported as removable, fixed, CD/DVD or RAM have filesystem/capacity queried; letters reported as network, missing or unknown are listed without those queries. No directory entries, file contents, volume labels or serial numbers are requested. Access can wake removable media, and a blocked system call may return after cancellation. Drive letters can change between calls, so this is not one authenticated or atomic device snapshot; a remapped letter or system provider can cause remote metadata access.";
    public const string Limits="Drive type does not identify USB or external hardware. Only mounted drive letters are covered, not folder mount points or unmounted devices. The read-only flag is a filesystem declaration, not a write-access test; its absence does not prove write permission. Available and total bytes for the caller can reflect quotas; total free bytes are a separate volume value. No writes, eject, repair, format, administrator action, network test or assistant upload is performed.";
    public static char[] Letters(uint mask)=>Enumerable.Range(0,26).Where(i=>(mask&(1u<<i))!=0).Select(i=>(char)('A'+i)).ToArray();
    public static bool QueryMetadata(uint type)=>type is 2 or 3 or 5 or 6;
    public static string TypeName(uint type)=>type switch{0=>"Unknown Drive Type",1=>"No Mounted Root",2=>"Removable Media",3=>"Fixed Media",4=>"Network Drive (Metadata Skipped)",5=>"CD/DVD Media",6=>"RAM Drive",_=>"Other Drive Type (Metadata Skipped)"};
}
