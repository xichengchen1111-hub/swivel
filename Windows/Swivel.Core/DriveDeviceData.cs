using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Swivel.Core;

public sealed record DriveDeviceNumber(uint Type,uint Disk,uint Partition);
public sealed record DriveDiskInterface(DriveDeviceNumber Number,string Instance);
public sealed record DriveDeviceNode(string Instance,uint? Capabilities,string? Service);

public static class DriveDeviceData
{
    public static DriveDiskInterface? MatchDisk(DriveDeviceNumber volume,byte[] extents,IEnumerable<DriveDiskInterface> disks)
    {
        if(volume.Type!=7||volume.Disk==uint.MaxValue||SingleExtentDisk(extents)!=volume.Disk)return null;
        DriveDiskInterface? match=null;
        foreach(var disk in disks)
        {
            if(disk.Number.Type!=volume.Type||disk.Number.Disk!=volume.Disk)continue;
            if(match is not null||string.IsNullOrEmpty(disk.Instance))return null;match=disk;
        }
        return match;
    }
    public static uint? SingleExtentDisk(byte[] extents)
    {
        // VOLUME_DISK_EXTENTS has one aligned 24-byte DISK_EXTENT at offset 8.
        if(extents.Length!=32||U32(extents,0)!=1||U32(extents,8)==uint.MaxValue||
            BinaryPrimitives.ReadInt64LittleEndian(extents.AsSpan(16,8))<0||BinaryPrimitives.ReadInt64LittleEndian(extents.AsSpan(24,8))<=0)return null;
        return U32(extents,8);
    }
    public static string? RemovableAncestor(IEnumerable<DriveDeviceNode> chain)
    {
        int depth=0;
        foreach(var node in chain)
        {
            if(depth++>=32||node.Capabilities is not uint caps||string.IsNullOrEmpty(node.Instance))return null;
            if((caps&4)==0)continue;
            if((caps&8)!=0||!new[]{"disk","USBSTOR","UASPStor","sdbus","sffdisk","sdstor"}.Contains(node.Service,StringComparer.OrdinalIgnoreCase))return null;
            return node.Instance;
        }
        return null;
    }
    public static DriveHardware? Descriptor(byte[] data)
    {
        if(data.Length<36)return null;uint length=U32(data,4);if(length<36||length>data.Length||data[10]>1)return null;
        string? Text(int field)
        {
            uint offset=U32(data,field);if(offset==0)return "";if(offset<36||offset>=length)return null;
            int end=Array.IndexOf(data,(byte)0,(int)offset,(int)(length-offset));
            return end<0?null:Encoding.ASCII.GetString(data,(int)offset,Math.Min(160,end-(int)offset)).Trim(' ');
        }
        return new(U32(data,28),data[10]!=0,Text(12),Text(16),null,null);
    }
    // Conservatively protect every EFI/service/active MBR partition, including ones not currently used to boot.
    public static bool? ContainsSystemPartition(byte[] data)
    {
        if(data.Length<48)return null;uint style=U32(data,0),count=U32(data,4);
        if(style>1||count==0||count>(data.Length-48)/144)return null;
        for(int i=0;i<count;i++)
        {
            int offset=48+i*144;uint partitionStyle=U32(data,offset);if(partitionStyle>1)return null;
            if(data[offset+29]!=0||partitionStyle==0&&data[offset+33]!=0||partitionStyle==1&&new Guid(data.AsSpan(offset+32,16))==new Guid("c12a7328-f81f-11d2-ba4b-00a0c93ec93b"))return true;
        }
        return false;
    }
    private static uint U32(byte[] data,int offset)=>BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset,4));
}
