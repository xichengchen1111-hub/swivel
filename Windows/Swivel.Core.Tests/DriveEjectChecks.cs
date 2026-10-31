using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Swivel.Core;

internal static class DriveEjectChecks
{
    private sealed class Source(params MountedDriveSnapshot[] reads):IDriveEjectSource
    {
        internal int ReadCount,Requests;internal DriveEjectReply Reply=new(false,5,"busy\n\u202Edevice");
        public Task<MountedDriveSnapshot> ReadAsync(CancellationToken token){token.ThrowIfCancellationRequested();return Task.FromResult(reads[Math.Min(ReadCount++,reads.Length-1)]);}
        public Task<DriveEjectReply> RequestAsync(MountedDrive drive,CancellationToken token){token.ThrowIfCancellationRequested();Requests++;return Task.FromResult(Reply);}
    }
    private sealed class BlockedSource(MountedDrive drive):IDriveEjectSource
    {
        internal readonly TaskCompletionSource<DriveEjectReply> Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Cleaned;
        public Task<MountedDriveSnapshot> ReadAsync(CancellationToken token)=>Task.FromResult(Snapshot(drive));
        public async Task<DriveEjectReply> RequestAsync(MountedDrive d,CancellationToken token)
        {try{return await Release.Task.ConfigureAwait(false);}finally{Cleaned=true;}}
    }
    internal static MountedDrive EligibleDrive()=>new('E',3,"NTFS",0,null,null,null){Label="USB label",Hardware=new(7,false,"Vendor","Product",true,null),VolumeName="volume",Identity=new("volume","instance","ancestor",1,1),Protected=false};
    private static MountedDriveSnapshot Snapshot(params MountedDrive[] rows)=>new(rows,Array.Empty<DriveIssue>(),false,default);
    internal static void Run(Action<bool,string> check)
    {
        var drive=EligibleDrive();var source=new Source(Snapshot(drive));int confirms=0;
        Task<bool> Confirm(MountedDrive d){confirms++;check(d.Label==drive.Label&&d.Root==drive.Root,"Confirmation receives the actual drive and label");return Task.FromResult(true);}
        DriveEjectResult Run(Source s,Func<MountedDrive,Task<bool>> confirm)=>DriveEject.RunAsync(drive,s,confirm,default).GetAwaiter().GetResult();
        check(Run(source,_=>Task.FromResult(false)).Status==DriveEjectStatus.Cancelled&&source.ReadCount==0&&source.Requests==0,"Declined confirmation never reads or ejects");
        var veto=Run(source,Confirm);check(veto.Status==DriveEjectStatus.Vetoed&&veto.Veto==5&&veto.VetoName==source.Reply.VetoName&&source.Requests==1&&confirms==1,"Veto and vetoing device preserved after explicit confirmation");
        foreach(var changed in new[]{drive with{Identity=drive.Identity! with{Disk=2}},drive with{Identity=drive.Identity! with{Instance="replacement disk instance"}},drive with{VolumeName="replacement"},drive with{Identity=null},drive with{Protected=true},drive with{Label="replacement"}})
        {var fake=new Source(Snapshot(changed));check(Run(fake,Confirm).Status==DriveEjectStatus.Changed&&fake.Requests==0,"Any changed/unknown identity or protection refuses request");}
        var gone=new Source(Snapshot(drive),Snapshot()){Reply=new(true,0,"")};check(Run(gone,Confirm).Status==DriveEjectStatus.Gone&&gone.Requests==1&&gone.ReadCount==2,"Accepted request is success only after a successful absence read");
        var moved=new Source(Snapshot(drive),Snapshot(drive with{Letter='F',Identity=null})){Reply=new(true,0,"")};
        check(Run(moved,Confirm).Status==DriveEjectStatus.StillPresent,"Same volume with a new letter and partial PnP data is not removed");
        var unknown=new Source(Snapshot(drive),Snapshot(drive with{Letter='F',Identity=null,VolumeName=null})){Reply=new(true,0,"")};
        check(Run(unknown,Confirm).Status==DriveEjectStatus.StillPresent,"Unknown local volume identity cannot prove removal");
        foreach(uint type in new uint[]{0,1,7,255})
        {
            var uncertain=new Source(Snapshot(drive),Snapshot(drive with{Letter='F',Type=type,Identity=null,VolumeName=null})){Reply=new(true,0,"")};
            check(Run(uncertain,Confirm).Status==DriveEjectStatus.StillPresent,"An enumerated unknown or missing-root drive cannot prove removal");
        }
        var failed=new Source(Snapshot(drive),new(Array.Empty<MountedDrive>(),new[]{new DriveIssue(DriveIssueStage.Enumeration,5)},false,default)){Reply=new(true,0,"")};
        check(Run(failed,Confirm).Status==DriveEjectStatus.StillPresent,"Failed enumeration cannot prove removal");
        var cancelled=new Source(Snapshot(drive));try{DriveEject.RunAsync(drive,cancelled,Confirm,new(true)).GetAwaiter().GetResult();check(false,"Expected precancellation");}catch(OperationCanceledException){check(cancelled.Requests==0,"Precancelled eject sends nothing");}
        var unsupported=new Source(Snapshot(drive));check(DriveEject.RunAsync(drive with{Protected=true},unsupported,Confirm,default).GetAwaiter().GetResult().Status==DriveEjectStatus.Ineligible&&unsupported.ReadCount==0,"Protected drive does not open confirmation or read");
        using(var stop=new CancellationTokenSource())
        {
            var blocked=new BlockedSource(drive);var pending=DriveEject.RunAsync(drive,blocked,Confirm,stop.Token);stop.Cancel();
            check(!pending.IsCompleted&&!blocked.Cleaned,"Cancellation does not abandon an eject call already in progress");
            blocked.Release.SetResult(new(false,0,""));
            try{pending.GetAwaiter().GetResult();check(false,"Expected joined cancellation");}catch(OperationCanceledException){check(blocked.Cleaned&&pending.IsCompleted,"Eject cancellation completes only after the worker cleanup");}
        }
        string[] expected={"safely","support safe","waiting","program","service","program","device","driver","support the removal","power","disabled","driver","permission","already removed"};
        for(uint i=0;i<expected.Length;i++)check(DriveEject.Veto(i).Contains(expected[i],StringComparison.OrdinalIgnoreCase),"PNP veto numeric mapping "+i);
        var descriptor=new byte[64];Put(descriptor,4,64);Put(descriptor,12,36);Put(descriptor,16,50);Put(descriptor,28,7);descriptor[10]=1;descriptor[36]=(byte)'A';descriptor[37]=10;
        var parsed=DriveDeviceData.Descriptor(descriptor)!;check(parsed.Bus==7&&parsed.Removable==true&&DoctorFacts.Visible(parsed.Vendor!)=="A[U+000A]","Descriptor offsets preserve untrusted text for visible sanitation");
        foreach(int length in new[]{0,8,35})check(DriveDeviceData.Descriptor(new byte[length]) is null,"Short descriptors remain unknown");
        Put(descriptor,4,65);check(DriveDeviceData.Descriptor(descriptor) is null,"Oversized descriptor declaration refused");Put(descriptor,4,64);
        Put(descriptor,12,64);check(DriveDeviceData.Descriptor(descriptor)!.Vendor is null,"Out-of-range device string stays unknown");
        var layout=new byte[48+144];Put(layout,0,1);Put(layout,4,1);Put(layout,48,1);
        check(DriveDeviceData.ContainsSystemPartition(layout)==false,"Ordinary GPT partition unprotected");
        new Guid("c12a7328-f81f-11d2-ba4b-00a0c93ec93b").TryWriteBytes(layout.AsSpan(80,16));check(DriveDeviceData.ContainsSystemPartition(layout)==true,"EFI system partition protected");
        Array.Clear(layout,80,16);layout[77]=1;check(DriveDeviceData.ContainsSystemPartition(layout)==true,"Service partition protected");layout[77]=0;
        Put(layout,0,0);Put(layout,48,0);layout[81]=1;check(DriveDeviceData.ContainsSystemPartition(layout)==true,"Active MBR partition protected");
        Put(layout,4,2);check(DriveDeviceData.ContainsSystemPartition(layout) is null,"Incomplete drive layout cannot authorize eject");
        var number=new DriveDeviceNumber(7,3,1);var extents=new byte[32];Put(extents,0,1);Put(extents,8,3);BinaryPrimitives.WriteInt64LittleEndian(extents.AsSpan(24,8),4096);
        var disk=new DriveDiskInterface(new(7,3,0),@"USBSTOR\Disk&USB");
        var otherType=new DriveDiskInterface(new(2,3,0),@"CDROM\SameNumber");
        var otherDisk=new DriveDiskInterface(new(7,4,0),@"SCSI\OtherDisk");
        var chains=new Dictionary<string,DriveDeviceNode[]>{
            [@"STORAGE\Volume"]=[new(@"STORAGE\Volume",0,null),new(@"ROOT\volmgr",0,null),new("ROOT",0,null)],
            [disk.Instance]=[new(disk.Instance,0,"disk"),new(@"USB\PhysicalDevice",4,"USBSTOR"),new(@"USB\Hub",4,"USBHUB3")]
        };
        check(DriveDeviceData.RemovableAncestor(chains[@"STORAGE\Volume"]) is null,"Normal volume parent chain never includes the physical USB disk");
        var mapped=DriveDeviceData.MatchDisk(number,extents,new[]{otherType,otherDisk,disk});
        check(mapped==disk&&DriveDeviceData.RemovableAncestor(chains[mapped!.Instance])==@"USB\PhysicalDevice","Volume maps by device type and number to disk before walking its USB ancestor chain");
        check(DriveDeviceData.MatchDisk(number,extents,new[]{otherType,otherDisk}) is null,"Same numeric device number with a different type cannot match");
        check(DriveDeviceData.MatchDisk(number,extents,new[]{disk,disk with{Instance="ambiguous"}}) is null,"Ambiguous disk interface mapping refuses eject");
        Put(extents,0,2);check(DriveDeviceData.MatchDisk(number,extents,new[]{disk}) is null,"Multi-extent volume refuses eject even when the first disk matches");
        Put(extents,0,1);Put(extents,8,4);check(DriveDeviceData.MatchDisk(number,extents,new[]{disk}) is null,"Extent and storage device disk numbers must agree");Put(extents,8,3);
        check(DriveDeviceData.MatchDisk(number,extents[..31],new[]{disk}) is null,"Truncated extent data refuses eject");
        check(DriveDeviceData.MatchDisk(number with{Disk=uint.MaxValue},extents,new[]{disk}) is null,"MPIO sentinel number cannot identify a disk");
        check(DriveDeviceData.RemovableAncestor(new[]{new DriveDeviceNode("dock",12,"disk")}) is null,"Dock capability refuses ancestor removal");
        check(DriveDeviceData.RemovableAncestor(new[]{new DriveDeviceNode("hub",4,"USBHUB3")}) is null,"Removable hub cannot become a disk eject target");
        check(DriveDeviceData.RemovableAncestor(new[]{new DriveDeviceNode("unknown",null,null),new DriveDeviceNode("usb",4,"USBSTOR")}) is null,"Unknown capabilities do not authorize walking past an uncertain node");
    }
    private static void Put(byte[] data,int offset,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset,4),value);
}
