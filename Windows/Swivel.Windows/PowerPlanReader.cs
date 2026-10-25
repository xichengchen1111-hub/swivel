using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

internal static class PowerPlanReader
{
    internal static PowerPlanSnapshot Read(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();ReportedPowerState? state=null;uint? stateError=null,schemeError=null,cleanupError=null;Guid? scheme=null;bool missingPointer=false;
        if(GetSystemPowerStatus(out var raw))state=new(raw.AcLine,raw.BatteryFlags,raw.BatteryPercent,raw.BatterySaver,raw.BatteryLifeSeconds,raw.FullBatteryLifeSeconds);else stateError=unchecked((uint)Marshal.GetLastWin32Error());
        token.ThrowIfCancellationRequested();IntPtr pointer=IntPtr.Zero;
        try
        {
            uint result=PowerGetActiveScheme(IntPtr.Zero,out pointer);
            token.ThrowIfCancellationRequested();
            if(result!=0)schemeError=result;
            else if(pointer==IntPtr.Zero)missingPointer=true;
            else scheme=Marshal.PtrToStructure<Guid>(pointer);
        }
        finally
        {
            if(pointer!=IntPtr.Zero&&LocalFree(pointer)!=IntPtr.Zero)cleanupError=unchecked((uint)Marshal.GetLastWin32Error());
        }
        token.ThrowIfCancellationRequested();var values=new List<PowerTimeout>();
        if(scheme is Guid captured)
        {
            foreach(var setting in PowerPlans.Settings)
            {
                token.ThrowIfCancellationRequested();Guid subgroup=setting.Subgroup,key=setting.Setting;
                uint acResult=PowerReadACValueIndex(IntPtr.Zero,in captured,in subgroup,in key,out uint ac);token.ThrowIfCancellationRequested();
                uint dcResult=PowerReadDCValueIndex(IntPtr.Zero,in captured,in subgroup,in key,out uint dc);token.ThrowIfCancellationRequested();
                values.Add(new(setting.Kind,acResult==0?ac:null,dcResult==0?dc:null,acResult==0?null:acResult,dcResult==0?null:dcResult));
            }
        }
        token.ThrowIfCancellationRequested();return new(state,stateError,scheme,schemeError,missingPointer,cleanupError,values,DateTimeOffset.Now);
    }
    [StructLayout(LayoutKind.Sequential)]private struct SystemPowerStatus{public byte AcLine,BatteryFlags,BatteryPercent,BatterySaver;public uint BatteryLifeSeconds,FullBatteryLifeSeconds;}
    [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
    [DllImport("powrprof.dll",ExactSpelling=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]private static extern uint PowerGetActiveScheme(IntPtr root,out IntPtr guid);
    [DllImport("powrprof.dll",ExactSpelling=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]private static extern uint PowerReadACValueIndex(IntPtr root,in Guid scheme,in Guid subgroup,in Guid setting,out uint seconds);
    [DllImport("powrprof.dll",ExactSpelling=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]private static extern uint PowerReadDCValueIndex(IntPtr root,in Guid scheme,in Guid subgroup,in Guid setting,out uint seconds);
    [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]private static extern IntPtr LocalFree(IntPtr memory);
}
