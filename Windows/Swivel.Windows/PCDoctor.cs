using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

public sealed record DoctorDriveSpace(string Root, ulong AvailableToCaller, ulong Capacity);
public sealed record PCDoctorReport(IReadOnlyList<DoctorFact> Facts, IReadOnlyList<DoctorDriveSpace> DriveSpace);

public static class PCDoctor
{
    // Called only by the explicit Run Read-only Checks action. No directory traversal,
    // file contents, application paths, clipboard, socket or shell command is used.
    public static IReadOnlyList<DoctorFact> Run(CancellationToken token) => RunDetailed(token).Facts;

    public static PCDoctorReport RunDetailed(CancellationToken token)
    {
        var facts = new List<DoctorFact>();
        var driveSpace = new List<DoctorDriveSpace>();
        void Add(string title, string format, params object[] values) => facts.Add(new(title, format, values));
        void Value(string title, string value) => facts.Add(new(title, "{0}", new object[] { value }, true));
        void Check(string section, Action run)
        {
            token.ThrowIfCancellationRequested();
            try { run(); }
            catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or NetworkInformationException or InvalidOperationException or NotSupportedException)
            { Value(section, "Unavailable"); }
        }
        Check("System", () => {
            Add("System", "{0} · {1} · {2} logical processors", DoctorFacts.Visible(RuntimeInformation.OSDescription), RuntimeInformation.OSArchitecture.ToString(), Environment.ProcessorCount);
            Add("Uptime", "{0} hours since the last system start", Math.Round(Environment.TickCount64 / 3600000.0, 1));
        });
        Check("Memory", () => {
            var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (!GlobalMemoryStatusEx(ref memory)) throw new Win32Exception(Marshal.GetLastWin32Error());
            Add("Memory", "{0} GB available of {1} GB; {2}% in use", GB(memory.AvailablePhysical), GB(memory.TotalPhysical), memory.Load);
            if (memory.Load >= 90) Value("Memory pressure", "Memory use is high at this moment. Review active apps; one snapshot does not identify a fault.");
        });
        Check("Battery & Power", () => {
            if (!GetSystemPowerStatus(out var power)) throw new Win32Exception(Marshal.GetLastWin32Error());
            Value("Power source", DoctorFacts.PowerSource(power.AC)); Value("Battery level", DoctorFacts.Battery(power.Battery, power.Percent));
            if (power.Saver == 1) Value("Battery saver", "On");
        });
        Check("Fixed Drives", () => {
            uint drives = GetLogicalDrives(); if (drives == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            for (int i = 0; i < 26; i++)
            {
                token.ThrowIfCancellationRequested(); if ((drives & (1u << i)) == 0) continue;
                string root = ((char)('A' + i)) + @":\";
                if (GetDriveType(root) != 3) continue;
                if (!GetDiskFreeSpaceEx(root, out ulong available, out ulong capacity, out _)) { Add("Fixed Drives", "{0}: unavailable", root); continue; }
                if (!DoctorFacts.HasValidCapacity(available, capacity)) { Add("Fixed Drives", "{0}: unavailable", root); continue; }
                driveSpace.Add(new(root, available, capacity));
                Add("Fixed Drives", "{0}: {1} GB available to this user of {2} GB", root, GB(available), GB(capacity));
                if (DoctorFacts.LowSpace(available,capacity)) Add("Low free space", "{0}: review storage in Windows Settings. Swivel has not scanned or removed any files.", root);
            }
        });
        Check("Processes", () => {
            var processes = Process.GetProcesses(); var samples = new List<(string Name,long Bytes)>(); int skipped = 0;
            try
            {
                foreach (var process in processes.Take(4096))
                {
                    token.ThrowIfCancellationRequested();
                    try { samples.Add((DoctorFacts.Visible(process.ProcessName), process.WorkingSet64)); }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { skipped++; }
                }
            }
            finally { foreach (var process in processes) process.Dispose(); }
            Add("Processes", "{0} processes reported; {1} unavailable or beyond the sampling limit", processes.Length, skipped + Math.Max(0,processes.Length - 4096));
            foreach (var sample in samples.OrderByDescending(x => x.Bytes).Take(10)) Add("Largest working sets", "{0}: {1} MB resident memory", sample.Name, Math.Round(sample.Bytes / 1048576.0, 1));
            Value("Processes", "Resident memory is a momentary working set, not a leak diagnosis. Shared memory can be counted by more than one process.");
        });
        facts.AddRange(DoctorNetworkReader.Read(token));
        token.ThrowIfCancellationRequested(); return new(facts.AsReadOnly(), driveSpace.AsReadOnly());
    }
    private static double GB(ulong bytes) => Math.Round(bytes / 1073741824.0, 2);
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus { public uint Length, Load; public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, ExtendedVirtual; }
    [StructLayout(LayoutKind.Sequential)] private struct PowerStatus { public byte AC, Battery, Percent, Saver; public uint Remaining, Full; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetSystemPowerStatus(out PowerStatus status);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetLogicalDrives();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetDriveTypeW")] private static extern uint GetDriveType(string root);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true)] private static extern bool GetDiskFreeSpaceEx(string root, out ulong available, out ulong capacity, out ulong free);
}
