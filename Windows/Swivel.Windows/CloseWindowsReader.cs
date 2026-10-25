using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Swivel.Core;

namespace Swivel.Windows;

internal static class CloseWindowsReader
{
    private const uint QueryAndSynchronize = 0x00101000;
    private static readonly HashSet<string> ShellNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Swivel.exe", "explorer.exe", "dwm.exe", "taskmgr.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe",
        "lsass.exe", "smss.exe", "fontdrvhost.exe", "sihost.exe", "ShellExperienceHost.exe", "StartMenuExperienceHost.exe",
        "SearchHost.exe", "SearchApp.exe", "RuntimeBroker.exe", "ApplicationFrameHost.exe", "conhost.exe", "OpenConsole.exe"
    };
    internal static CloseWindowsSession Read(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        uint self = (uint)Environment.ProcessId;
        using var own = OpenProcess(QueryAndSynchronize, false, self);
        if (own.IsInvalid || ScopeOf(own) is not { } scope)
            throw new UserError("The current Windows user and session could not be checked. No window requests are available.");
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
        try { WindowsPathPolicy.Validate(windows + "\\placeholder"); }
        catch (UserError) { throw new UserError("The Windows system folder could not be checked. No window requests are available."); }
        return CloseWindows.Scan(new Source(self, scope, windows), token);
    }
    // A dedicated MTA thread has no application windows or message loop. Never
    // attach input queues. No Task.WaitAsync: cancellation must join this native
    // call before the session can release its held process handles.
    internal static Task<CloseWindowsResult> RequestAsync(CloseWindowsSession session, int index, CancellationToken token)
    {
        var completion = new TaskCompletionSource<CloseWindowsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.TrySetResult(session.Request(index, token)); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { completion.TrySetCanceled(token); }
            catch (Exception error) { completion.TrySetException(error); }
        }) { IsBackground = true, Name = "Swivel window close request" };
        thread.SetApartmentState(ApartmentState.MTA); thread.Start(); return completion.Task;
    }
    private sealed record Scope(byte[] Sid, uint Session, bool Elevated);
    private sealed class Source(uint self, Scope scope, string windows) : ICloseWindowsSource
    {
        public CloseWindowsEnumeration Enumerate(CancellationToken token)
        {
            var handles = new List<nint>(); bool limited = false, time = false, stopped = false;
            var clock = Stopwatch.StartNew();
            bool Callback(nint window, nint unused)
            {
                // Never throw across the unmanaged callback boundary.
                if (token.IsCancellationRequested) { stopped = true; return false; }
                if (clock.Elapsed >= CloseWindows.SoftTimeLimit) { time = stopped = true; return false; }
                if (handles.Count == CloseWindows.MaxEnumerated) { limited = stopped = true; return false; }
                handles.Add(window); return true;
            }
            bool success = EnumWindows(Callback, 0); token.ThrowIfCancellationRequested();
            return new(handles.AsReadOnly(), limited, time, !success && !stopped);
        }
        public ICloseWindowLease? Open(nint window, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            uint thread = GetWindowThreadProcessId(window, out uint pid);
            if (thread == 0 || pid is 0 or 4 || pid == self) return null;
            var handle = OpenProcess(QueryAndSynchronize, false, pid);
            if (handle.IsInvalid) { handle.Dispose(); return null; }
            try
            {
                var info = Inspect(handle, window, self, scope, windows, token);
                if (info is null) return null;
                var lease = new Lease(handle, info, self, scope, windows); handle = null!; return lease;
            }
            finally { handle?.Dispose(); }
        }
    }
    private sealed class Lease(SafeProcessHandle handle, CloseWindowInfo info, uint self, Scope scope, string windows) : ICloseWindowLease
    {
        public CloseWindowInfo Info => info;
        public CloseWindowInfo? Inspect(CancellationToken token) => CloseWindowsReader.Inspect(handle, info.Handle, self, scope, windows, token);
        public CloseWindowsResult Send(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Inspect(token) != info) return CloseWindowsResult.Changed;
            token.ThrowIfCancellationRequested();
            // Fixed single-window WM_CLOSE only, no broadcast, parameters, force,
            // retry, elevation, process termination or AttachThreadInput.
            nint received = SendMessageTimeoutW(info.Handle, 0x0010, 0, 0, 0x0001 | 0x0002 | 0x0020,
                CloseWindows.SendTimeoutMilliseconds, out _);
            // Zero can mean timeout, access denial or window destruction during
            // processing. It does not prove that WM_CLOSE was never delivered.
            return received != 0 ? CloseWindowsResult.Processed : CloseWindowsResult.Unknown;
        }
        public void Dispose() => handle.Dispose();
    }
    private static CloseWindowInfo? Inspect(SafeProcessHandle handle, nint window, uint self, Scope scope, string windows, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (WaitForSingleObject(handle, 0) != 0x102) return null;
        uint thread = GetWindowThreadProcessId(window, out uint pid);
        if (thread == 0 || pid is 0 or 4 || pid == self || GetProcessId(handle) != pid || !OrdinaryWindow(window)) return null;
        if (!IsProcessCritical(handle, out bool critical) || critical) return null;
        var current = ScopeOf(handle);
        if (current is null || current.Elevated || current.Session != scope.Session || !current.Sid.SequenceEqual(scope.Sid)) return null;
        token.ThrowIfCancellationRequested();
        if (!GetProcessTimes(handle, out var created, out _, out _, out _) || created.Value == 0) return null;
        var path = new StringBuilder(CloseWindows.MaxPath + 1); uint length = (uint)path.Capacity;
        if (!QueryFullProcessImageNameW(handle, 0, path, ref length) || length == 0 || length > CloseWindows.MaxPath || path.Length != length) return null;
        string image = path.ToString();
        if (image.StartsWith(windows + "\\", StringComparison.OrdinalIgnoreCase) || ShellNames.Contains(Path.GetFileName(image))) return null;
        var caption = new StringBuilder(CloseWindows.MaxTitle + 2);
        int titleLength = GetWindowTextW(window, caption, caption.Capacity);
        if (titleLength is <= 0 or > CloseWindows.MaxTitle || caption.Length != titleLength) return null;
        var className = new StringBuilder(CloseWindows.MaxClass + 2);
        int classLength = GetClassNameW(window, className, className.Capacity);
        if (classLength is <= 0 or > CloseWindows.MaxClass || className.Length != classLength) return null;
        token.ThrowIfCancellationRequested();
        if (GetWindowThreadProcessId(window, out uint finalPid) != thread || finalPid != pid || !OrdinaryWindow(window) || WaitForSingleObject(handle, 0) != 0x102) return null;
        var result = new CloseWindowInfo(window, pid, thread, created.Value, current.Session, caption.ToString(), className.ToString(), image);
        return CloseWindows.ValidInfo(result) ? result : null;
    }
    private static bool OrdinaryWindow(nint window)
    {
        if (window == 0 || window == 0xffff || !IsWindowVisible(window) || GetAncestor(window, 2) != window || GetWindow(window, 4) != 0) return false;
        uint style = unchecked((uint)GetWindowLongW(window, -16)), extended = unchecked((uint)GetWindowLongW(window, -20));
        if ((style & 0x40000000) != 0 || (style & 0x00c00000) != 0x00c00000 || (extended & 0x80) != 0) return false;
        return DwmGetWindowAttribute(window, 14, out uint cloaked, sizeof(uint)) == 0 && cloaked == 0;
    }
    private static Scope? ScopeOf(SafeProcessHandle process)
    {
        if (!OpenProcessToken(process, 0x0008, out var token)) { token?.Dispose(); return null; }
        using (token)
        {
            nint buffer = Marshal.AllocHGlobal(256);
            try
            {
                // TOKEN_USER contains a SID pointer followed by attributes. The
                // returned SID must lie completely within this bounded buffer.
                if (!GetTokenInformation(token, 1, buffer, 256, out uint used) || used < IntPtr.Size + 4 || used > 256) return null;
                nint sid = Marshal.ReadIntPtr(buffer); long offset = sid.ToInt64() - buffer.ToInt64();
                if (offset < IntPtr.Size + 4 || offset > used - 8 || Marshal.ReadByte(sid) != 1) return null;
                int count = Marshal.ReadByte(sid, 1), bytes = 8 + count * 4;
                if (count > 15 || offset + bytes > used) return null;
                var copied = new byte[bytes]; Marshal.Copy(sid, copied, 0, bytes);
                if (!GetTokenInformation(token, 12, buffer, sizeof(uint), out used) || used != sizeof(uint)) return null;
                uint session = unchecked((uint)Marshal.ReadInt32(buffer));
                if (!GetTokenInformation(token, 20, buffer, sizeof(uint), out used) || used != sizeof(uint)) return null;
                return new(copied, session, Marshal.ReadInt32(buffer) != 0);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeTime { public uint Low, High; public readonly ulong Value => ((ulong)High << 32) | Low; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] [return: MarshalAs(UnmanagedType.Bool)] private delegate bool EnumCallback(nint window, nint parameter);
    [DllImport("user32.dll", SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumCallback callback, nint parameter);
    [DllImport("user32.dll", SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int GetWindowLongW(nint window, int index);
    [DllImport("dwmapi.dll")] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int DwmGetWindowAttribute(nint window, uint attribute, out uint value, uint size);
    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int GetWindowTextW(nint window, StringBuilder text, int capacity);
    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int GetClassNameW(nint window, StringBuilder text, int capacity);
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern nint SendMessageTimeoutW(nint window, uint message, nuint wParam, nint lParam, uint flags, uint timeout, out nuint result);
    [DllImport("kernel32.dll", SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint GetProcessId(SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsProcessCritical(SafeProcessHandle process, [MarshalAs(UnmanagedType.Bool)] out bool critical);
    [DllImport("kernel32.dll", SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(SafeProcessHandle process, out NativeTime created, out NativeTime exit, out NativeTime kernel, out NativeTime user);
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder path, ref uint length);
    [DllImport("advapi32.dll", SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, nint buffer, uint size, out uint returned);
}
