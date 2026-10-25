using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

internal static class SleepCapabilityReader
{
    internal static SleepCapabilitySnapshot Read(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();bool success=GetPwrCapabilities(out var raw);uint? error=success?null:unchecked((uint)Marshal.GetLastWin32Error());
        token.ThrowIfCancellationRequested();return success?new(SleepCapabilities.Fields.Select(field=>new ReportedSleepFlag(field,raw.Value(field))),raw.HibernationFileType,null,DateTimeOffset.Now):new(Array.Empty<ReportedSleepFlag>(),null,error,DateTimeOffset.Now);
    }
    // Windows 10+ winnt.h NTDDI_WINXP/NTDDI_WINTHRESHOLD branches. BOOLEAN is
    // one byte; the complete native output is 76 bytes on x64 and ARM64.
    // Unused/reserved bytes, BatteryScale and wake-state enums are not exposed.
    [StructLayout(LayoutKind.Explicit,Size=76)]private struct NativeCapabilities
    {
        [FieldOffset(0)]public byte PowerButton;
        [FieldOffset(1)]public byte SleepButton;
        [FieldOffset(2)]public byte Lid;
        [FieldOffset(3)]public byte S1;
        [FieldOffset(4)]public byte S2;
        [FieldOffset(5)]public byte S3;
        [FieldOffset(6)]public byte S4;
        [FieldOffset(7)]public byte S5;
        [FieldOffset(8)]public byte HibernationFile;
        [FieldOffset(17)]public byte HybridSleep;
        [FieldOffset(19)]public byte WakeAlarm;
        [FieldOffset(20)]public byte LowPowerIdle;
        [FieldOffset(22)]public byte HibernationFileType;
        // Match the native DWORD alignment as well as its complete size.
        [FieldOffset(72)]public uint ReservedWakeState;
        internal readonly byte Value(SleepCapability field)=>field switch{SleepCapability.PowerButton=>PowerButton,SleepCapability.SleepButton=>SleepButton,SleepCapability.Lid=>Lid,SleepCapability.S1=>S1,SleepCapability.S2=>S2,SleepCapability.S3=>S3,SleepCapability.S4=>S4,SleepCapability.S5=>S5,SleepCapability.HibernationFile=>HibernationFile,SleepCapability.HybridSleep=>HybridSleep,SleepCapability.WakeAlarm=>WakeAlarm,SleepCapability.LowPowerIdle=>LowPowerIdle,_=>throw new ArgumentOutOfRangeException(nameof(field))};
    }
    [DllImport("powrprof.dll",ExactSpelling=true,SetLastError=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)][return:MarshalAs(UnmanagedType.U1)]private static extern bool GetPwrCapabilities(out NativeCapabilities capabilities);
}
