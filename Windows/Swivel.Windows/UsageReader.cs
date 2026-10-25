using System;
using System.Runtime.InteropServices;
using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

internal static class UsageReader
{
    internal static UsageReading Read(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); CpuTimes? cpu = null; PhysicalMemory? memory = null;
        CpuSampleStatus status = CpuSampleStatus.Unavailable;
        try
        {
            ushort groups = GetActiveProcessorGroupCount();
            if (groups > 1) status = CpuSampleStatus.MultipleProcessorGroups;
            else if (groups == 1 && GetSystemTimes(out var idle, out var kernel, out var user))
            { cpu = new(idle.Value, kernel.Value, user.Value); status = CpuSampleStatus.Available; }
        }
        catch (Exception error) when (Unavailable(error)) { }
        token.ThrowIfCancellationRequested();
        try
        {
            var value = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (GlobalMemoryStatusEx(ref value)) memory = new(value.TotalPhysical, value.AvailablePhysical);
        }
        catch (Exception error) when (Unavailable(error)) { }
        token.ThrowIfCancellationRequested(); return new(cpu, memory, status);
    }
    private static bool Unavailable(Exception error) => error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or PlatformNotSupportedException;
    [StructLayout(LayoutKind.Sequential)] internal struct NativeTime { public uint Low, High; public readonly ulong Value => ((ulong)High << 32) | Low; }
    [StructLayout(LayoutKind.Sequential)] internal struct MemoryStatus
    { public uint Length, Load; public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, ExtendedVirtual; }
    [DllImport("kernel32.dll", SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetSystemTimes(out NativeTime idle, out NativeTime kernel, out NativeTime user);
    [DllImport("kernel32.dll")] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern ushort GetActiveProcessorGroupCount();
    [DllImport("kernel32.dll", SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus memory);
}
