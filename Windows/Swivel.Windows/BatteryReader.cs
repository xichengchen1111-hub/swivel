using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Swivel.Core;

namespace Swivel.Windows;

// Read-only battery class driver queries, as in Microsoft's "Enumerating Battery Devices" sample:
// SetupAPI lists battery interfaces, each is opened for read access only and asked for its tag,
// BATTERY_INFORMATION, three string levels and BATTERY_STATUS with a zero wait. Every IOCTL here
// is defined with FILE_READ_ACCESS, so no write access, administrator rights or setting change is needed.
internal static class BatteryReader
{
    internal static readonly Guid BatteryClass = new("72631e54-78a4-11d0-bcf7-00aa00b7b32a");
    internal const uint QueryTag = 0x294040, QueryInformation = 0x294044, QueryStatus = 0x29404C;
    internal const int LevelInformation = 0, LevelDeviceName = 4, LevelManufactureName = 6, LevelSerialNumber = 8;
    private const uint DigcfPresent = 0x2, DigcfDeviceInterface = 0x10, ErrorNoMoreItems = 259, ErrorInsufficientBuffer = 122;
    private const int MaxDetailBytes = 4096 + 8, TextBytes = BatteryHealth.MaxTextChars * 2;

    internal static BatterySnapshot Read(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var guid = BatteryClass;
        IntPtr set = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == new IntPtr(-1)) return new(Array.Empty<BatteryReading>(), unchecked((uint)Marshal.GetLastWin32Error()), false, 0, DateTimeOffset.Now);
        var readings = new List<BatteryReading>(); int empty = 0; bool truncated = false; uint? enumError = null;
        try
        {
            for (uint index = 0; ; index++)
            {
                token.ThrowIfCancellationRequested();
                var data = new DeviceInterfaceData { Size = (uint)Marshal.SizeOf<DeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref data))
                {
                    uint error = unchecked((uint)Marshal.GetLastWin32Error());
                    if (error != ErrorNoMoreItems) enumError = error;
                    break;
                }
                // One extra interface past the bound is only probed to report truncation.
                if (index >= BatteryHealth.MaxBatteries) { truncated = true; break; }
                var reading = ReadOne((int)index, set, ref data, token, out bool emptySlot);
                if (emptySlot) empty++; else readings.Add(reading);
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        token.ThrowIfCancellationRequested();
        return new(readings, enumError, truncated, empty, DateTimeOffset.Now);
    }

    private static BatteryReading ReadOne(int index, IntPtr set, ref DeviceInterfaceData data, CancellationToken token, out bool emptySlot)
    {
        emptySlot = false;
        string? path = DevicePath(set, ref data, out uint pathError);
        if (path is null) return Failed(index, pathError);
        using var handle = CreateFileW(path, 0x80000000 /* GENERIC_READ */, 3 /* share read|write */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0x80, IntPtr.Zero);
        if (handle.IsInvalid) return Failed(index, unchecked((uint)Marshal.GetLastWin32Error()));
        token.ThrowIfCancellationRequested();
        var tagOut = new byte[4];
        if (!Ioctl(handle, QueryTag, BitConverter.GetBytes(0u), tagOut, out _, out uint tagError))
            return new(index, null, tagError, null, null, null, null, null, null, null);
        uint tag = BitConverter.ToUInt32(tagOut);
        if (tag == 0) { emptySlot = true; return new(index, null, null, null, null, null, null, null, null, null); }

        token.ThrowIfCancellationRequested();
        BatteryInformation? info = null; uint? infoError = null;
        var infoOut = new byte[Marshal.SizeOf<NativeBatteryInformation>()];
        if (Ioctl(handle, QueryInformation, Query(tag, LevelInformation), infoOut, out uint infoBytes, out uint e1) && infoBytes == infoOut.Length)
        {
            var raw = MemoryMarshal.Read<NativeBatteryInformation>(infoOut);
            info = new(raw.Capabilities, raw.Technology, BatteryHealth.ChemistryText(infoOut.AsSpan(8, 4)), raw.DesignedCapacity, raw.FullChargedCapacity, raw.CycleCount);
        }
        else infoError = infoBytes == infoOut.Length || e1 != 0 ? e1 : 24 /* ERROR_BAD_LENGTH */;

        token.ThrowIfCancellationRequested();
        BatteryStatus? status = null; uint? statusError = null;
        var wait = new NativeWaitStatus { BatteryTag = tag };
        var waitIn = new byte[Marshal.SizeOf<NativeWaitStatus>()]; MemoryMarshal.Write(waitIn, in wait);
        var statusOut = new byte[Marshal.SizeOf<NativeBatteryStatus>()];
        if (Ioctl(handle, QueryStatus, waitIn, statusOut, out uint statusBytes, out uint e2) && statusBytes == statusOut.Length)
        {
            var raw = MemoryMarshal.Read<NativeBatteryStatus>(statusOut);
            status = new(raw.PowerState, raw.Capacity, raw.Voltage, raw.Rate);
        }
        else statusError = statusBytes == statusOut.Length || e2 != 0 ? e2 : 24;

        token.ThrowIfCancellationRequested();
        var name = Text(handle, tag, LevelDeviceName); var maker = Text(handle, tag, LevelManufactureName); var serial = Text(handle, tag, LevelSerialNumber);
        return new(index, null, null, info, infoError, status, statusError, name, maker, serial);
    }

    private static BatteryReading Failed(int index, uint error) => new(index, error, null, null, null, null, null, null, null, null);

    private static BatteryText Text(SafeFileHandle handle, uint tag, int level)
    {
        var buffer = new byte[TextBytes];
        if (!Ioctl(handle, QueryInformation, Query(tag, level), buffer, out uint bytes, out uint error)) return new(null, error);
        int count = (int)Math.Min(bytes, (uint)buffer.Length) / 2;
        return new(BatteryHealth.DriverText(MemoryMarshal.Cast<byte, char>(buffer.AsSpan(0, count * 2))), null);
    }

    private static byte[] Query(uint tag, int level)
    {
        var query = new NativeQueryInformation { BatteryTag = tag, InformationLevel = level, AtRate = 0 };
        var bytes = new byte[Marshal.SizeOf<NativeQueryInformation>()]; MemoryMarshal.Write(bytes, in query); return bytes;
    }

    private static bool Ioctl(SafeFileHandle handle, uint code, byte[] input, byte[] output, out uint returned, out uint error)
    {
        bool ok = DeviceIoControl(handle, code, input, (uint)input.Length, output, (uint)output.Length, out returned, IntPtr.Zero);
        error = ok ? 0 : unchecked((uint)Marshal.GetLastWin32Error());
        if (!ok) returned = 0;
        return ok;
    }

    private static string? DevicePath(IntPtr set, ref DeviceInterfaceData data, out uint error)
    {
        error = 0;
        if (SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out uint required, IntPtr.Zero) || (error = unchecked((uint)Marshal.GetLastWin32Error())) != ErrorInsufficientBuffer)
            return null;
        if (required is < 8 or > MaxDetailBytes) { error = ErrorInsufficientBuffer; return null; }
        error = 0;
        IntPtr detail = Marshal.AllocHGlobal((int)required);
        try
        {
            // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize is the fixed header size: 8 on 64-bit, 6 on 32-bit.
            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetailW(set, ref data, detail, required, out _, IntPtr.Zero)) { error = unchecked((uint)Marshal.GetLastWin32Error()); return null; }
            string path = Marshal.PtrToStringUni(detail + 4, (int)(required - 4) / 2);
            int end = path.IndexOf('\0'); if (end >= 0) path = path[..end];
            return path.Length == 0 ? null : path;
        }
        finally { Marshal.FreeHGlobal(detail); }
    }

    // SP_DEVICE_INTERFACE_DATA: 32 bytes on 64-bit Windows.
    [StructLayout(LayoutKind.Sequential)] internal struct DeviceInterfaceData { public uint Size; public Guid InterfaceClassGuid; public uint Flags; public UIntPtr Reserved; }
    // BATTERY_QUERY_INFORMATION: tag, level (enum = int), AtRate.
    [StructLayout(LayoutKind.Sequential)] internal struct NativeQueryInformation { public uint BatteryTag; public int InformationLevel; public int AtRate; }
    // BATTERY_INFORMATION: 36 bytes; Chemistry is the four bytes at offset 8.
    [StructLayout(LayoutKind.Sequential)] internal struct NativeBatteryInformation
    {
        public uint Capabilities; public byte Technology; public byte Reserved0, Reserved1, Reserved2; public byte Chemistry0, Chemistry1, Chemistry2, Chemistry3;
        public uint DesignedCapacity, FullChargedCapacity, DefaultAlert1, DefaultAlert2, CriticalBias, CycleCount;
    }
    // BATTERY_WAIT_STATUS: zero timeout returns the current status at once.
    [StructLayout(LayoutKind.Sequential)] internal struct NativeWaitStatus { public uint BatteryTag, Timeout, PowerState, LowCapacity, HighCapacity; }
    [StructLayout(LayoutKind.Sequential)] internal struct NativeBatteryStatus { public uint PowerState, Capacity, Voltage; public int Rate; }

    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)][return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr deviceInfo, ref Guid interfaceGuid, uint index, ref DeviceInterfaceData data);
    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)][return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref DeviceInterfaceData data, IntPtr detail, uint detailSize, out uint required, IntPtr deviceInfo);
    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)][return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)][return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, [In] byte[] input, uint inputSize, [Out] byte[] output, uint outputSize, out uint returned, IntPtr overlapped);
}
