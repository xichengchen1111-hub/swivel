using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class DriveDeviceReader:IDriveEjectSource
{
    private static readonly Guid DiskClass=new("53f56307-b6bf-11d0-94f2-00a0c91efb8b");
    private static readonly PropertyKey InstanceKey=new(){Format=new("78c34fc8-104a-4aca-9ea4-524d52996e57"),Id=256};
    public Task<MountedDriveSnapshot> ReadAsync(CancellationToken token)=>MountedDriveReader.ReadAsync(token);
    public async Task<DriveEjectReply> RequestAsync(MountedDrive drive,CancellationToken token)
    {
        // Fresh metadata uses the same isolated error-mode worker as the first read.
        var snapshot=await MountedDriveReader.ReadAsync(token).ConfigureAwait(false);token.ThrowIfCancellationRequested();
        var fresh=snapshot.Drives.SingleOrDefault(d=>d.Letter==drive.Letter);
        if(fresh is null||!DriveEject.Same(drive,fresh)||fresh.Identity is null)return new(false,0,"");
        return await Task.Run(()=>
        {
            token.ThrowIfCancellationRequested();
            if(CM_Locate_DevNodeW(out uint node,fresh.Identity.Ancestor,0)!=0)return new DriveEjectReply(false,0,"");
            var vetoName=new StringBuilder(512);token.ThrowIfCancellationRequested();
            uint result=CM_Request_Device_EjectW(node,out uint veto,vetoName,(uint)vetoName.Capacity,0);
            // Join the actual call even if cancellation arrives after the request was sent.
            return new DriveEjectReply(result==0,veto,vetoName.ToString());
        },token).ConfigureAwait(false);
    }

    internal static MountedDrive Read(MountedDrive drive,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();drive=drive with{Identity=null,Protected=null,Hardware=null,Label=null,VolumeName=null,HardwareIssue=null};
        if(!MountedDrives.QueryMetadata(drive.Type))return drive;
        string? volume=VolumeName(drive.Root);if(volume is null)return drive with{HardwareIssue="Device identity is unavailable; eject is disabled."};
        drive=drive with{VolumeName=volume};
        using var handle=Open(volume.TrimEnd('\\'));
        if(handle.IsInvalid)return drive with{HardwareIssue="Device information is partial; unavailable facts are not proof of write access or safe removal."};
        uint? bus=null,writeError=null;bool? removable=null,writable=null;string? vendor=null,product=null;
        var descriptor=Io(handle,0x2d1400,new byte[12],65536);
        if(descriptor is not null&&DriveDeviceData.Descriptor(descriptor) is {} hardware)
        {bus=hardware.Bus;removable=hardware.Removable;vendor=hardware.Vendor;product=hardware.Product;}
        token.ThrowIfCancellationRequested();
        if(DeviceIoControl(handle,0x70024,null,0,Array.Empty<byte>(),0,out _,IntPtr.Zero))writable=true;
        else{writeError=unchecked((uint)Marshal.GetLastWin32Error());if(writeError==19)writable=false;}
        var label=new StringBuilder(261);var fs=new StringBuilder(65);
        string? name=null;uint? flags=drive.FileSystemFlags;
        if(GetVolumeInformationW(volume,label,(uint)label.Capacity,IntPtr.Zero,IntPtr.Zero,out uint f,fs,(uint)fs.Capacity)){name=label.ToString();flags=f;}
        token.ThrowIfCancellationRequested();var number=DeviceNumber(handle);DriveIdentity? identity=null;bool? protect=null;
        var extents=VolumeExtents(handle);bool multiExtent=extents is {Length:>=4}&&U32(extents,0)>1;
        if(number is {Type:7})
        {
            string? instance=extents is null?null:ResolveDisk(number,extents,token)?.Instance;
            string? ancestor=instance is null?null:RemovableAncestor(instance,token);
            if(instance is not null&&ancestor is not null)
            {
                identity=new(volume,instance,ancestor,number.Disk,number.Partition);
                protect=ProtectedAncestor(ancestor,token);
            }
        }
        // Never permit the Windows volume itself, even if the PnP/layout query is incomplete.
        string? windows=WindowsVolume();if(windows is null)protect=null;else if(string.Equals(volume,windows,StringComparison.OrdinalIgnoreCase))protect=true;
        token.ThrowIfCancellationRequested();
        if(!string.Equals(volume,VolumeName(drive.Root),StringComparison.OrdinalIgnoreCase)){identity=null;protect=null;drive=drive with{VolumeName=null};}
        return drive with{Label=name,FileSystemFlags=flags,Hardware=new(bus,removable,vendor,product,writable,writeError),Identity=identity,Protected=protect,
            HardwareIssue=multiExtent?"This volume has multiple disk extents; eject is disabled.":bus is null||writable is null||name is null||identity is null||protect is null?"Device information is partial; unavailable facts are not proof of write access or safe removal.":null};
    }
    internal static DriveDiskInterface? ResolveDisk(string volume,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();using var handle=Open(volume.TrimEnd('\\'));if(handle.IsInvalid)return null;
        var number=DeviceNumber(handle);var extents=VolumeExtents(handle);
        return number is null||extents is null?null:ResolveDisk(number,extents,token);
    }
    private static DriveDiskInterface? ResolveDisk(DriveDeviceNumber number,byte[] extents,CancellationToken token)
    {
        var paths=Interfaces(DiskClass,token);if(paths is null)return null;
        IEnumerable<DriveDiskInterface> Disks()
        {
            foreach(string path in paths)
            {
                token.ThrowIfCancellationRequested();using var disk=Open(path);if(disk.IsInvalid)continue;
                var found=DeviceNumber(disk);if(found is null)continue;
                if(found.Type==number.Type&&found.Disk==number.Disk)yield return new(found,Instance(path)??"");
            }
        }
        return DriveDeviceData.MatchDisk(number,extents,Disks());
    }
    private static bool? ProtectedAncestor(string ancestor,CancellationToken token)
    {
        string? windows=WindowsVolume();if(windows is null)return null;
        using var win=Open(windows.TrimEnd('\\'));if(win.IsInvalid)return null;var windowsDisk=DeviceNumber(win);var extents=VolumeExtents(win);
        if(windowsDisk is not {Type:7}||extents is null||DriveDeviceData.SingleExtentDisk(extents)!=windowsDisk.Disk)return null;
        var disks=Interfaces(DiskClass,token);if(disks is null)return null;bool found=false;
        foreach(string path in disks)
        {
            token.ThrowIfCancellationRequested();string? id=Instance(path);if(id is null)return null;
            bool? related=HasAncestor(id,ancestor,token);if(related is null)return null;if(related!=true)continue;
            found=true;using var disk=Open(path);if(disk.IsInvalid)return null;var number=DeviceNumber(disk);if(number is null)return null;
            if(number.Type==windowsDisk.Type&&number.Disk==windowsDisk.Disk)return true;
            var layout=Io(disk,0x70050,null,65536);bool? protectedDisk=layout is null?null:DriveDeviceData.ContainsSystemPartition(layout);
            if(protectedDisk!=false)return protectedDisk;
        }
        return found?false:null;
    }
    private static string? RemovableAncestor(string instance,CancellationToken token)=>DriveDeviceData.RemovableAncestor(AncestorChain(instance,token));
    private static IEnumerable<DriveDeviceNode> AncestorChain(string instance,CancellationToken token)
    {
        if(CM_Locate_DevNodeW(out uint node,instance,0)!=0)yield break;
        for(int depth=0;depth<32;depth++)
        {
            token.ThrowIfCancellationRequested();var caps=new byte[4];uint size=4;
            if(CM_Get_DevNode_Registry_PropertyW(node,0x10,out uint type,caps,ref size,0)!=0||type!=4||size!=4)yield break;
            string? id=NodeId(node);if(id is null)yield break;
            if((U32(caps,0)&4)!=0)
            {
                var service=new byte[512];size=(uint)service.Length;
                if(CM_Get_DevNode_Registry_PropertyW(node,5,out type,service,ref size,0)!=0||type!=1||size<2||size>service.Length||size%2!=0)yield break;
                string name=Encoding.Unicode.GetString(service,0,(int)size).TrimEnd('\0');
                yield return new(id,U32(caps,0),name);yield break;
            }
            yield return new(id,U32(caps,0),null);
            if(CM_Get_Parent(out uint parent,node,0)!=0)yield break;node=parent;
        }
    }
    private static bool? HasAncestor(string instance,string ancestor,CancellationToken token)
    {
        if(CM_Locate_DevNodeW(out uint node,instance,0)!=0)return null;
        for(int depth=0;depth<32;depth++)
        {
            token.ThrowIfCancellationRequested();string? id=NodeId(node);if(id is null)return null;
            if(string.Equals(id,ancestor,StringComparison.OrdinalIgnoreCase))return true;
            uint result=CM_Get_Parent(out uint parent,node,0);if(result==0x0d)return false;if(result!=0)return null;node=parent;
        }
        return null;
    }
    private static string? NodeId(uint node){var name=new StringBuilder(201);return CM_Get_Device_IDW(node,name,(uint)name.Capacity,0)==0&&name.Length>0?name.ToString():null;}
    private static string? Instance(string path)
    {
        var key=InstanceKey;var data=new byte[402];uint size=(uint)data.Length;
        if(CM_Get_Device_Interface_PropertyW(path,ref key,out uint type,data,ref size,0)!=0||type!=0x12||size<4||size>data.Length||size%2!=0||data[size-1]!=0||data[size-2]!=0)return null;
        string id=Encoding.Unicode.GetString(data,0,(int)size-2);return id.Contains('\0')?null:id;
    }
    private static string[]? Interfaces(Guid category,CancellationToken token)
    {
        for(int attempt=0;attempt<3;attempt++)
        {
            token.ThrowIfCancellationRequested();if(CM_Get_Device_Interface_List_SizeW(out uint size,ref category,null,0)!=0||size<2||size>262144)return null;
            var buffer=new char[size];uint result=CM_Get_Device_Interface_ListW(ref category,null,buffer,size,0);if(result==0x1a)continue;
            if(result!=0||buffer[^1]!='\0'||buffer[^2]!='\0')return null;
            var paths=new string(buffer).Split('\0',StringSplitOptions.RemoveEmptyEntries);return paths.Length<=256?paths:null;
        }
        return null;
    }
    private static string? VolumeName(string root){var b=new StringBuilder(50);return GetVolumeNameForVolumeMountPointW(root,b,(uint)b.Capacity)&&b.ToString().StartsWith(@"\\?\Volume{",StringComparison.Ordinal)&&b.Length==49?b.ToString():null;}
    internal static string? WindowsVolume(){var b=new StringBuilder(32768);uint n=GetSystemWindowsDirectoryW(b,(uint)b.Capacity);if(n==0||n>=b.Capacity)return null;var root=new StringBuilder(32768);return GetVolumePathNameW(b.ToString(),root,(uint)root.Capacity)?VolumeName(root.ToString()):null;}
    private static DriveDeviceNumber? DeviceNumber(SafeFileHandle handle){var b=Io(handle,0x2d1080,null,12);return b is {Length:12}?new(U32(b,0),U32(b,4),U32(b,8)):null;}
    private static byte[]? VolumeExtents(SafeFileHandle handle)
    {
        var data=new byte[32];bool ok=DeviceIoControl(handle,0x560000,null,0,data,(uint)data.Length,out uint written,IntPtr.Zero);
        // ERROR_MORE_DATA still returns the count; a spanned volume must not use its first disk for eject.
        return (ok||Marshal.GetLastWin32Error()==234)&&written>=4&&written<=data.Length?data.AsSpan(0,ok?(int)written:4).ToArray():null;
    }
    private static byte[]? Io(SafeFileHandle h,uint code,byte[]? input,int size){var b=new byte[size];return DeviceIoControl(h,code,input,(uint)(input?.Length??0),b,(uint)b.Length,out uint written,IntPtr.Zero)&&written<=b.Length?b.AsSpan(0,(int)written).ToArray():null;}
    private static uint U32(byte[] b,int offset)=>BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(offset,4));
    private static SafeFileHandle Open(string path)=>CreateFileW(path,0,7,IntPtr.Zero,3,0,IntPtr.Zero);
    [StructLayout(LayoutKind.Sequential)]private struct PropertyKey{public Guid Format;public uint Id;}
    [DllImport("kernel32.dll",ExactSpelling=true,CharSet=CharSet.Unicode,SetLastError=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]private static extern SafeFileHandle CreateFileW(string path,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool DeviceIoControl(SafeFileHandle h,uint code,byte[]? input,uint inputSize,[Out]byte[] output,uint outputSize,out uint returned,IntPtr overlapped);
    [DllImport("kernel32.dll",ExactSpelling=true,CharSet=CharSet.Unicode,SetLastError=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetVolumeNameForVolumeMountPointW(string root,StringBuilder name,uint length);
    [DllImport("kernel32.dll",ExactSpelling=true,CharSet=CharSet.Unicode,SetLastError=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetVolumeInformationW(string root,StringBuilder label,uint labelLength,IntPtr serial,IntPtr component,out uint flags,StringBuilder fs,uint fsLength);
    [DllImport("kernel32.dll",ExactSpelling=true,CharSet=CharSet.Unicode)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]private static extern uint GetSystemWindowsDirectoryW(StringBuilder buffer,uint size);
    [DllImport("kernel32.dll",ExactSpelling=true,CharSet=CharSet.Unicode)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetVolumePathNameW(string path,StringBuilder root,uint length);
    [DllImport("cfgmgr32.dll",ExactSpelling=true,CharSet=CharSet.Unicode)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]private static extern uint CM_Get_Device_Interface_List_SizeW(out uint size,ref Guid category,string? id,uint flags);
    [DllImport("cfgmgr32.dll",ExactSpelling=true,CharSet=CharSet.Unicode)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]private static extern uint CM_Get_Device_Interface_ListW(ref Guid category,string? id,[Out]char[] buffer,uint length,uint flags);
    [DllImport("cfgmgr32.dll",ExactSpelling=true,CharSet=CharSet.Unicode)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]private static extern uint CM_Get_Device_Interface_PropertyW(string path,ref PropertyKey key,out uint type,[Out]byte[] buffer,ref uint size,uint flags);
    [DllImport("cfgmgr32.dll",ExactSpelling=true,CharSet=CharSet.Unicode)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]private static extern uint CM_Locate_DevNodeW(out uint node,string id,uint flags);
    [DllImport("cfgmgr32.dll",ExactSpelling=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]private static extern uint CM_Get_Parent(out uint parent,uint node,uint flags);
    [DllImport("cfgmgr32.dll",ExactSpelling=true,CharSet=CharSet.Unicode)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]private static extern uint CM_Get_Device_IDW(uint node,StringBuilder buffer,uint size,uint flags);
    [DllImport("cfgmgr32.dll",ExactSpelling=true,CharSet=CharSet.Unicode)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]private static extern uint CM_Get_DevNode_Registry_PropertyW(uint node,uint property,out uint type,[Out]byte[] buffer,ref uint length,uint flags);
    [DllImport("cfgmgr32.dll",ExactSpelling=true,CharSet=CharSet.Unicode)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]private static extern uint CM_Request_Device_EjectW(uint node,out uint veto,StringBuilder vetoName,uint length,uint flags);
}
