using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Swivel.Core;

public sealed record DriveHardware(uint? Bus,bool? Removable,string? Vendor,string? Product,bool? Writable,uint? WriteError);
public sealed record DriveIdentity(string Volume,string Instance,string Ancestor,uint Disk,uint Partition);
public sealed record DriveEjectReply(bool Accepted,uint Veto,string VetoName);
public enum DriveEjectStatus { Cancelled, Ineligible, Changed, Vetoed, Gone, StillPresent }
public sealed record DriveEjectResult(DriveEjectStatus Status,uint Veto=0,string VetoName="");
public interface IDriveEjectSource
{
    Task<MountedDriveSnapshot> ReadAsync(CancellationToken token);
    Task<DriveEjectReply> RequestAsync(MountedDrive drive,CancellationToken token);
}
public static class DriveEject
{
    public static bool Eligible(MountedDrive d)=>d.Type is 2 or 3&&d.Protected==false&&d.Label is not null&&d.VolumeName==d.Identity?.Volume&&d.Identity is {Volume.Length:>0,Instance.Length:>0,Ancestor.Length:>0}&&d.Hardware is {Bus:>0 and <20} h&&(h.Bus is 7 or 12 or 13||h.Removable==true);
    public static bool Same(MountedDrive a,MountedDrive b)=>Eligible(b)&&a.Letter==b.Letter&&a.Identity==b.Identity&&a.Label==b.Label&&a.Hardware==b.Hardware;
    public static string BusName(uint? bus)=>bus switch{1=>"SCSI",2=>"ATAPI",3=>"ATA",4=>"IEEE 1394",5=>"SSA",6=>"Fibre Channel",7=>"USB",8=>"RAID",9=>"iSCSI",10=>"SAS",11=>"SATA",12=>"SD",13=>"MMC",14=>"Virtual",15=>"File-backed Virtual",16=>"Storage Spaces",17=>"NVMe",18=>"SCM",19=>"UFS",_=>"Unknown"};
    public static string Veto(uint type)=>type switch{
        1=>"This device does not support safe removal.",2=>"Windows is waiting for a file or device to close. Try again shortly.",3 or 5=>"A program is using it. Close its files and try again.",
        4=>"A service is using it.",6=>"A device cannot safely be removed now.",7 or 11=>"A driver is preventing removal.",8=>"This device does not support the removal request.",
        9=>"There is not enough power to remove the device safely.",10=>"This device cannot be disabled.",12=>"Windows did not grant permission to eject it.",13=>"Windows reports that the device was already removed; refresh the drive list.",
        _=>"Windows could not safely remove the device. Try again later."};
    public static async Task<DriveEjectResult> RunAsync(MountedDrive selected,IDriveEjectSource source,Func<MountedDrive,Task<bool>> confirm,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();if(!Eligible(selected))return new(DriveEjectStatus.Ineligible);
        if(!await confirm(selected).ConfigureAwait(false))return new(DriveEjectStatus.Cancelled);
        token.ThrowIfCancellationRequested();var current=(await source.ReadAsync(token).ConfigureAwait(false)).Drives.SingleOrDefault(d=>d.Letter==selected.Letter);
        token.ThrowIfCancellationRequested();if(current is null||!Same(selected,current))return new(DriveEjectStatus.Changed);
        var reply=await source.RequestAsync(current,token).ConfigureAwait(false);token.ThrowIfCancellationRequested();
        if(!reply.Accepted)return new(DriveEjectStatus.Vetoed,reply.Veto,reply.VetoName);
        for(int i=0;i<5;i++)
        {
            var after=await source.ReadAsync(token).ConfigureAwait(false);token.ThrowIfCancellationRequested();
            // Enumeration failure is not evidence of removal. A replacement at the same letter is not success either.
            if(after.Issues.Count==0&&!after.UnknownMaskBits&&!after.Drives.Any(d=>d.Letter==selected.Letter||d.VolumeName==selected.VolumeName||d.Type!=4&&d.VolumeName is null))return new(DriveEjectStatus.Gone);
            if(i<4)await Task.Delay(250,token).ConfigureAwait(false);
        }
        return new(DriveEjectStatus.StillPresent);
    }
}
