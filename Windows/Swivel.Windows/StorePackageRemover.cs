using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Swivel.Core;
using Windows.Foundation;
using Windows.Management.Deployment;

namespace Swivel.Windows;

/// The result of one Uninstall…: Recheck says why nothing was removed (anything but Ready);
/// otherwise Outcome comes from the list read afterwards, which is After.
internal sealed record StoreRemovalRun(RemovalRecheck Recheck,RemovalBlock Block,bool RunningRefused,bool Started,bool CancelRequested,int? Error,string? ErrorText,
    RemovalOutcome? Outcome,StorePackagesSnapshot? After);

/// Removes one current-user package with PackageManager.RemovePackageAsync(fullName): the
/// current user only, no RemovalOptions (no AllUsers, no deprovisioning, no data kept), no
/// elevation. Nothing is closed by Swivel: a running app refuses the removal.
internal static class StorePackageRemover
{
    /// Swivel's own package family when it runs packaged, else null.
    internal static string? OwnFamily()
    {
        try
        {
            uint length=0;if(GetCurrentPackageFamilyName(ref length,null)!=122||length is 0 or >256)return null;
            var name=new StringBuilder((int)length);
            return GetCurrentPackageFamilyName(ref length,name)==0?name.ToString():null;
        }
        catch(Exception error)when(error is DllNotFoundException or EntryPointNotFoundException){return null;}
    }

    /// Whether a process with this package family runs (any of the current user's processes that
    /// can be opened for limited queries), or null when the processes could not be listed.
    internal static bool? Running(string family)
    {
        try
        {
            var ids=new uint[AppMemory.MaxProcesses];uint bytes=(uint)ids.Length*4;
            if(!K32EnumProcesses(ids,bytes,out uint written)||written==0||written>bytes||written%4!=0||written==bytes)return null;
            for(int i=0;i<written/4;i++)
            {
                if(ids[i]==0)continue;
                using var handle=OpenProcess(0x1000,false,ids[i]);if(handle.IsInvalid)continue;
                uint length=256;var name=new StringBuilder(256);
                int status=GetPackageFamilyName(handle,ref length,name);
                if(status==15700)continue; // APPMODEL_ERROR_NO_PACKAGE
                if(status!=0)return null;
                if(string.Equals(name.ToString(),family,StringComparison.OrdinalIgnoreCase))return true;
            }
            return false;
        }
        catch(Exception error)when(error is DllNotFoundException or EntryPointNotFoundException){return null;}
    }

    /// Whether any package of this family is installed for the current user, or null when the
    /// package manager could not answer.
    internal static bool? FamilyInstalled(string family)
    {
        if(!StorePackageRemoval.ValidFamily(family))return null;
        try{return new PackageManager().FindPackagesForUser(string.Empty,family).Any();}
        catch(Exception error)when(StorePackagesReader.Unavailable(error)){return null;}
    }

    internal static string? LocalAppData()
    {
        try{return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,Environment.SpecialFolderOption.DoNotVerify);}
        catch(Exception error)when(error is PlatformNotSupportedException or ArgumentException){return null;}
    }

    /// Rechecks `listed` against a fresh list and that the app is not running, then removes it.
    /// `cancel` before the system call means nothing is removed; afterwards it asks the system
    /// operation to cancel, which Windows may ignore, and the list is read again either way.
    internal static async Task<StoreRemovalRun> RemoveAsync(StorePackageRecord listed,Action<uint> progress,CancellationToken cancel)
    {
        string? own=OwnFamily();
        var now=await Task.Run(()=>StorePackagesReader.Read(cancel),cancel);
        var(state,block)=StorePackageRemoval.Recheck(listed,now,own);
        if(state!=RemovalRecheck.Ready)return new(state,block,false,false,false,null,null,null,now);
        string full=listed.FullName!.Value!,family=listed.Family.Value!;
        if(await Task.Run(()=>Running(family),cancel)!=false)return new(state,block,true,false,false,null,null,null,now);
        cancel.ThrowIfCancellationRequested();
        int? error=null;string? errorText=null;bool cancelled=false;
        IAsyncOperationWithProgress<DeploymentResult,DeploymentProgress> operation=new PackageManager().RemovePackageAsync(full);
        try
        {
            operation.Progress=(_,value)=>progress(value.percentage);
            using var registration=cancel.Register(()=>{cancelled=true;try{operation.Cancel();}catch(Exception){/* A completed operation may reject cancellation. */}});
            try
            {
                var result=await operation.AsTask();
                if(result.ExtendedErrorCode is Exception extended&&extended.HResult!=0){error=extended.HResult;errorText=result.ErrorText;}
            }
            catch(TaskCanceledException){cancelled=true;}
            catch(Exception failure)when(failure is COMException or UnauthorizedAccessException or InvalidOperationException or System.IO.IOException or ArgumentException)
            {error=failure.HResult;errorText=failure.Message;}
        }
        finally{operation.Close();}
        var after=await Task.Run(()=>StorePackagesReader.Read(CancellationToken.None));
        return new(state,block,false,true,cancelled,error,errorText,StorePackageRemoval.Outcome(full,after),after);
    }

    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetCurrentPackageFamilyName(ref uint length,StringBuilder? name);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetPackageFamilyName(SafeProcessHandle process,ref uint length,StringBuilder name);
    [DllImport("kernel32.dll",SetLastError=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return:MarshalAs(UnmanagedType.Bool)] private static extern bool K32EnumProcesses([Out] uint[] ids,uint bytes,out uint written);
    [DllImport("kernel32.dll",SetLastError=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeProcessHandle OpenProcess(uint access,[MarshalAs(UnmanagedType.Bool)] bool inherit,uint pid);
}
