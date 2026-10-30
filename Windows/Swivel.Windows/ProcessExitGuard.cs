using System;
using System.Runtime.InteropServices;

namespace Swivel.Windows;

// Windows.Data.Pdf keeps a Direct3D device in a static that it destroys while the DLL unloads at
// process exit. On the software renderer (no GPU) that flush starts a thread pool task during loader
// shutdown, and ntdll answers with an unhandled c000000d, so a finished process can report a crash
// (stack in Windows/TESTING.md, 0.137). Only when that DLL is loaded,
// end the process without running DLL detach. Everything of ours is closed by then.
internal static class ProcessExitGuard
{
    internal static int Finish(int code)
    {
        if (!OperatingSystem.IsWindows() || GetModuleHandleW("Windows.Data.Pdf.dll") == IntPtr.Zero) return code;
        Console.Out.Flush();
        Console.Error.Flush();
        TerminateProcess(GetCurrentProcess(), unchecked((uint)code));
        return code; // TerminateProcess failed: exit the ordinary way.
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetModuleHandleW(string name);
    [DllImport("kernel32.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint code);
}
