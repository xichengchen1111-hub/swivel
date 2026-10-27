using System;
using System.Collections.Generic;
using System.Linq;
using Swivel.Core;

internal static class MountedDriveChecks
{
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        check(MountedDrives.Letters(0).Length==0&&MountedDrives.Letters(uint.MaxValue).SequenceEqual("ABCDEFGHIJKLMNOPQRSTUVWXYZ"),"Drive mask returns only A–Z, without synthesizing paths from high bits");
        for(int i=0;i<26;i++)check(MountedDrives.Letters(1u<<i).Single()==(char)('A'+i),"Each Windows drive mask bit maps to its bounded root");
        foreach(uint type in new uint[]{0,1,4,7,255,uint.MaxValue})check(!MountedDrives.QueryMetadata(type),"Network/missing/unknown drive types never authorize filesystem or capacity calls");
        foreach(uint type in new uint[]{2,3,5,6})check(MountedDrives.QueryMetadata(type),"Only documented local media types authorize metadata calls");
        var unknown=new MountedDrive('C',uint.MaxValue,null,null,null,5,21);
        var readOnly=new MountedDrive('D',2,"NTFS",0x80000,new(0,100,1000),null,null);
        var writableNotProven=new MountedDrive('Z',3,"ReFS",0xffffffff&~0x80000u,new(ulong.MaxValue,ulong.MaxValue,ulong.MaxValue),null,null);
        check(unknown.Root==@"C:\"&&unknown.DeclaredReadOnly is null&&readOnly.DeclaredReadOnly==true&&writableNotProven.DeclaredReadOnly==false,"Unknown/declared-readonly/notdeclared are distinct, no implicit write test");
        var rows=new[]{writableNotProven,readOnly,unknown};var issues=new[]{new DriveIssue(DriveIssueStage.Enumeration,uint.MaxValue)};
        var snapshot=new MountedDriveSnapshot(rows,issues,true,DateTimeOffset.UnixEpoch);rows[0]=unknown;issues[0]=new(DriveIssueStage.ErrorModeSetup,0);
        check(snapshot.Drives.Select(d=>d.Letter).SequenceEqual("CDZ")&&snapshot.Issues[0].Error==uint.MaxValue&&snapshot.UnknownMaskBits,"Drive snapshot copies/sorts collections and retains raw error/unknown mask facts");
        check(snapshot.Drives[1].Space==new DriveSpace(0,100,1000)&&snapshot.Drives[2].Space==new DriveSpace(ulong.MaxValue,ulong.MaxValue,ulong.MaxValue),"Zero caller quota/volume-free greater than caller total and UInt64 maximum are preserved without false physical capacity inference");
        reject(()=>((IList<MountedDrive>)snapshot.Drives).Clear(),"Snapshot drive list is immutable");reject(()=>((IList<DriveIssue>)snapshot.Issues).Clear(),"Snapshot issue list is immutable");
        reject(()=>new MountedDriveSnapshot(new[]{unknown,unknown},Array.Empty<DriveIssue>(),false,default),"Duplicate drive letters refused");
        reject(()=>new MountedDriveSnapshot(new[]{unknown with{Letter='a'}},Array.Empty<DriveIssue>(),false,default),"Noncanonical drive letter refused");
        reject(()=>new MountedDriveSnapshot(new[]{unknown with{FileSystem=new string('x',65)}},Array.Empty<DriveIssue>(),false,default),"Filesystem names remain bounded to native buffer content");
        reject(()=>new MountedDriveSnapshot(Array.Empty<MountedDrive>(),new[]{new DriveIssue((DriveIssueStage)99,0)},false,default),"Unknown issue stage refused");
        var prefs=new Prefs();var action=Catalog.Actions.Single(a=>a.Id=="doctor.drives");check(action.Feature==Feature.Doctor&&!action.RequiresFiles&&!Catalog.Available(prefs,Array.Empty<string>()).Any(a=>a.Id==action.Id),"New drive check is independently default-off and ignores unrelated files");
        prefs.Set(Feature.Doctor,true);check(Catalog.Available(prefs,Array.Empty<string>()).Any(a=>a.Id==action.Id)&&WindowsDoctor.Areas.Single(a=>a.Id=="drives").ActionIds.SequenceEqual(new[]{action.Id}),"Drive action exposed only by explicit enabled Doctor entry and limited area");
    }
}
