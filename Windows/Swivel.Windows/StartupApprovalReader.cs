using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Swivel.Core;

namespace Swivel.Windows;

internal static class StartupApprovalReader
{
    internal static string BaseKey=>@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
    internal static string Key(StartupApprovalArea area)=>BaseKey+"\\"+StartupApprovalSnapshotReader.AreaName(area);
    internal static (IntPtr Hive,string Key,uint Access) Request(StartupApprovalLocation location)
    {
        bool machine=location.Source is RegistrationSource.Machine64 or RegistrationSource.Machine32;
        uint view=location.Source switch{RegistrationSource.Machine64 or RegistrationSource.User64=>0x0100u,RegistrationSource.Machine32 or RegistrationSource.User32=>0x0200u,_=>throw new ArgumentOutOfRangeException(nameof(location))};
        string key=Key(location.Area);if(!Enum.IsDefined(location.Area))throw new ArgumentOutOfRangeException(nameof(location));
        return(new IntPtr(unchecked((int)(machine?0x80000002u:0x80000001u))),key,0x0001|view);
    }
    internal static StartupApprovalSnapshot Read(CancellationToken token)=>StartupApprovalSnapshotReader.Scan(new Source(),token);
    private sealed class Source:IStartupApprovalSource
    {
        public StartupApprovalOpen Open(StartupApprovalLocation location)
        {
            var request=Request(location);using var predefined=new SafeRegistryHandle(request.Hive,false);SafeRegistryHandle? handle=null;
            try
            {
                int code=RegOpenKeyExW(predefined,request.Key,0,request.Access,out handle);
                if(code!=0||handle is null||handle.IsInvalid){handle?.Dispose();return new(null,code==2?RegistrationOpenState.Missing:RegistrationOpenState.Unavailable);}
                return new(new Root(handle),RegistrationOpenState.Open);
            }
            catch(Exception e)when(Unavailable(e)){handle?.Dispose();return new(null,RegistrationOpenState.Unavailable);}
        }
    }
    private sealed class Root(SafeRegistryHandle handle):IStartupApprovalRoot
    {
        public StartupApprovalRead At(uint index)
        {
            var name=new char[StartupApprovalSnapshotReader.MaxNameUnits+1];var data=new byte[StartupApprovalSnapshotReader.MaxDataBytes+1];uint chars=(uint)name.Length,bytes=(uint)data.Length,type;int result;
            try{result=RegEnumValueW(handle,index,name,ref chars,IntPtr.Zero,out type,data,ref bytes);}
            catch(Exception e)when(Unavailable(e)){return new(StartupApprovalReadKind.Unavailable);}
            if(result==259)return new(StartupApprovalReadKind.End);
            if(result==234)return new(StartupApprovalReadKind.TooLarge);
            if(result!=0)return new(StartupApprovalReadKind.Unavailable);
            if(chars>StartupApprovalSnapshotReader.MaxNameUnits||bytes>StartupApprovalSnapshotReader.MaxDataBytes)return new(StartupApprovalReadKind.TooLarge);
            return new(StartupApprovalReadKind.Item,new string(name,0,(int)chars),type,data.AsSpan(0,(int)bytes).ToArray());
        }
        public void Dispose()=>handle.Dispose();
    }
    private static bool Unavailable(Exception e)=>e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or PlatformNotSupportedException;
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegOpenKeyExW(SafeRegistryHandle parent,string key,uint options,uint access,out SafeRegistryHandle handle);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegEnumValueW(SafeRegistryHandle key,uint index,[Out]char[] name,ref uint chars,IntPtr reserved,out uint type,[Out]byte[] data,ref uint bytes);
}
