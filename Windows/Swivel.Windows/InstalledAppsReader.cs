using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Swivel.Core;

namespace Swivel.Windows;

// Fixed local registry metadata only. No registry writes, MSI/WMI, commands,
// environment expansion, file handles or network requests. F08 can retain reported install locations.
internal static class InstalledAppsReader
{
    internal const string UninstallKey=@"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    internal const uint Query=0x0001,Enumerate=0x0008,View64=0x0100,View32=0x0200;
    private const int Success=0,NotFound=2,MoreData=234,NoMore=259;
    internal static InstalledAppsSnapshot Read(CancellationToken token,bool includeLocations=false)=>InstalledApps.Scan(new Source(),token,includeLocations);
    private sealed class Source:IInstalledAppsSource
    {
        public RegistrationOpen Open(RegistrationSource source)
        {
            uint view=View(source);
            using var predefined=Predefined(source);
            int error=OpenKey(predefined,UninstallKey,Enumerate|view,out var handle);
            if(error!=Success||handle is null)return new(null,error==NotFound?RegistrationOpenState.Missing:RegistrationOpenState.Unavailable);
            return new(new Root(handle,view),RegistrationOpenState.Open);
        }
    }
    /// Opens one earlier record's own subkey again in its view. Gone only when the view's
    /// Uninstall key opens and the subkey itself is reported as not found.
    internal static RecordKeyState Recheck(RegistrationSource source,string key)
    {
        if(!InstalledApps.ValidKey(key))return RecordKeyState.Unknown;
        uint view=View(source);
        using var predefined=Predefined(source);
        if(OpenKey(predefined,UninstallKey,Query|view,out var root)!=Success||root is null)return RecordKeyState.Unknown;
        using(root)
        {
            int error=OpenKey(root,key,Query|view,out var entry);entry?.Dispose();
            return error==Success?RecordKeyState.Present:error==NotFound?RecordKeyState.Gone:RecordKeyState.Unknown;
        }
    }
    internal static uint View(RegistrationSource source)=>source switch{RegistrationSource.Machine64 or RegistrationSource.User64=>View64,RegistrationSource.Machine32 or RegistrationSource.User32=>View32,_=>throw new ArgumentOutOfRangeException(nameof(source))};
    // A predefined key is borrowed, never closed.
    internal static SafeRegistryHandle Predefined(RegistrationSource source)
        =>new(new IntPtr(unchecked((int)(source is RegistrationSource.Machine64 or RegistrationSource.Machine32?0x80000002u:0x80000001u))),ownsHandle:false);
    internal sealed class Root(SafeRegistryHandle handle,uint view):IRegistrationRoot
    {
        public RegistrationKey At(uint index)
        {
            var name=new char[InstalledApps.MaxKeyUnits+1];uint length=(uint)name.Length;int result;
            try{result=RegEnumKeyExW(handle,index,name,ref length,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);}
            catch(Exception error)when(Unavailable(error)){return new(RegistrationKeyKind.Unavailable);}
            if(result==NoMore)return new(RegistrationKeyKind.End);
            if(result==MoreData)return new(RegistrationKeyKind.Invalid);
            if(result!=Success)return new(RegistrationKeyKind.Unavailable);
            if(length==0||length>InstalledApps.MaxKeyUnits)return new(RegistrationKeyKind.Invalid);
            string key=new(name,0,(int)length);return InstalledApps.ValidKey(key)?new(RegistrationKeyKind.Item,key):new(RegistrationKeyKind.Invalid);
        }
        public IRegistrationEntry? Open(string key)
        {
            if(!InstalledApps.ValidKey(key))throw new ArgumentException("Invalid registry subkey.",nameof(key));
            return OpenKey(handle,key,Query|view,out var entry)==Success&&entry is not null?new Entry(entry):null;
        }
        public void Dispose()=>handle.Dispose();
    }
    private sealed class Entry(SafeRegistryHandle handle):IRegistrationEntry
    {
        public RegistrationText Read(RegistrationField field)
        {
            string name=field switch{RegistrationField.Name=>"DisplayName",RegistrationField.Version=>"DisplayVersion",RegistrationField.Publisher=>"Publisher",RegistrationField.InstallLocation=>"InstallLocation",_=>throw new ArgumentOutOfRangeException(nameof(field))};
            return ReadText(handle,name);
        }
        public void Dispose()=>handle.Dispose();
    }
    internal static RegistrationText ReadText(SafeRegistryHandle handle,string name)
    {
        var data=new byte[(InstalledApps.MaxTextUnits+1)*2];uint bytes=(uint)data.Length,type;int result;
        try{result=RegQueryValueExW(handle,name,IntPtr.Zero,out type,data,ref bytes);}
        catch(Exception error)when(Unavailable(error)){return RegistrationText.Unknown(RegistrationValueState.Unavailable);}
        if(result==NotFound)return RegistrationText.Unknown(RegistrationValueState.Missing);
        if(result==MoreData)return RegistrationText.Unknown(RegistrationValueState.TooLarge);
        if(result!=Success)return RegistrationText.Unknown(RegistrationValueState.Unavailable);
        return Decode(type,data,bytes);
    }
    internal static RegistrationText Decode(uint type,byte[] data,uint bytes)
    {
        if(type is not 1 and not 2)return RegistrationText.Unknown(RegistrationValueState.Unsupported);
        if(bytes>data.Length||(bytes&1)!=0)return RegistrationText.Unknown(RegistrationValueState.Invalid);
        if(bytes>(InstalledApps.MaxTextUnits+1)*2)return RegistrationText.Unknown(RegistrationValueState.TooLarge);
        string value;
        try{value=new UnicodeEncoding(false,false,true).GetString(data,0,(int)bytes);}
        catch(DecoderFallbackException){return RegistrationText.Unknown(RegistrationValueState.Invalid);}
        if(value.EndsWith('\0'))value=value[..^1]; // A terminator is optional; strip at most one.
        return InstalledApps.Validate(new(value,RegistrationValueState.Present));
    }
    internal static int OpenKey(SafeRegistryHandle parent,string key,uint access,out SafeRegistryHandle? owned)
    {
        owned=null;SafeRegistryHandle? result=null;int error;
        try{error=RegOpenKeyExW(parent,key,0,access,out result);}
        catch(Exception failure)when(Unavailable(failure)){result?.Dispose();return 6;}
        if(error!=Success||result is null||result.IsInvalid){result?.Dispose();return error==Success?6:error;}
        owned=result;return Success;
    }
    private static bool Unavailable(Exception error)=>error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or PlatformNotSupportedException;
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegOpenKeyExW(SafeRegistryHandle key,string subkey,uint options,uint access,out SafeRegistryHandle result);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegEnumKeyExW(SafeRegistryHandle key,uint index,[Out]char[] name,ref uint length,IntPtr reserved,IntPtr keyClass,IntPtr classLength,IntPtr lastWrite);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegQueryValueExW(SafeRegistryHandle key,string name,IntPtr reserved,out uint type,[Out]byte[] data,ref uint bytes);
}
