using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Swivel.Core;

namespace Swivel.Windows;

internal static class StartupReader
{
    internal static string Key(bool once)=>@"Software\Microsoft\Windows\CurrentVersion\"+(once?"RunOnce":"Run");
    internal static StartupSnapshot Read(CancellationToken token)=>StartupRecords.Scan(new Source(),token);
    internal static (IntPtr Hive,string Key,uint Access) Request(StartupLocation location)
    {
        bool machine=location.Source is RegistrationSource.Machine64 or RegistrationSource.Machine32;
        uint view=location.Source switch{RegistrationSource.Machine64 or RegistrationSource.User64=>0x0100u,RegistrationSource.Machine32 or RegistrationSource.User32=>0x0200u,_=>throw new ArgumentOutOfRangeException(nameof(location))};
        return(new IntPtr(unchecked((int)(machine?0x80000002u:0x80000001u))),Key(location.Once),0x0001|view);
    }
    private sealed class Source:IStartupSource
    {
        public StartupOpen Open(StartupLocation location)
        {
            var request=Request(location);
            using var predefined=new SafeRegistryHandle(request.Hive,false);SafeRegistryHandle? handle=null;
            try
            {
                int code=RegOpenKeyExW(predefined,request.Key,0,request.Access,out handle);
                if(code!=0||handle is null||handle.IsInvalid){handle?.Dispose();return new(null,code==2?RegistrationOpenState.Missing:RegistrationOpenState.Unavailable);}
                return new(new Root(handle),RegistrationOpenState.Open);
            }
            catch(Exception e)when(Unavailable(e)){handle?.Dispose();return new(null,RegistrationOpenState.Unavailable);}
        }
    }
    private sealed class Root(SafeRegistryHandle handle):IStartupRoot
    {
        public StartupRead At(uint index)
        {
            var name=new char[StartupRecords.MaxNameUnits+1];var data=new byte[(StartupRecords.MaxCommandUnits+1)*2];uint chars=(uint)name.Length,bytes=(uint)data.Length,type;int result;
            try{result=RegEnumValueW(handle,index,name,ref chars,IntPtr.Zero,out type,data,ref bytes);}
            catch(Exception e)when(Unavailable(e)){return new(StartupReadKind.Unavailable);}
            if(result==259)return new(StartupReadKind.End);
            // Both buffers may be incomplete. Never expose a returned prefix.
            if(result==234)return new(StartupReadKind.TooLarge);
            if(result!=0)return new(StartupReadKind.Unavailable);
            if(chars>StartupRecords.MaxNameUnits)return new(StartupReadKind.TooLarge);
            return new(StartupReadKind.Item,new string(name,0,(int)chars),Decode(type,data,bytes),type);
        }
        public void Dispose()=>handle.Dispose();
    }
    internal static RegistrationText Decode(uint type,byte[] data,uint bytes)
    {
        if(type is not 1 and not 2)return RegistrationText.Unknown(RegistrationValueState.Unsupported);
        if(bytes>data.Length||(bytes&1)!=0)return RegistrationText.Unknown(RegistrationValueState.Invalid);
        if(bytes>(StartupRecords.MaxCommandUnits+1)*2)return RegistrationText.Unknown(RegistrationValueState.TooLarge);
        string value;try{value=new UnicodeEncoding(false,false,true).GetString(data,0,(int)bytes);}catch(DecoderFallbackException){return RegistrationText.Unknown(RegistrationValueState.Invalid);}
        if(value.EndsWith('\0'))value=value[..^1];return StartupRecords.Validate(new(value,RegistrationValueState.Present));
    }
    private static bool Unavailable(Exception e)=>e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or PlatformNotSupportedException;
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegOpenKeyExW(SafeRegistryHandle parent,string key,uint options,uint access,out SafeRegistryHandle handle);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegEnumValueW(SafeRegistryHandle key,uint index,[Out]char[] name,ref uint chars,IntPtr reserved,out uint type,[Out]byte[] data,ref uint bytes);
}
