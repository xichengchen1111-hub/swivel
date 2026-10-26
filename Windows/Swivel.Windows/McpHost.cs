using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using Swivel.Core;

namespace Swivel.Windows;

/// `Swivel.exe --mcp`: the agent server on stdin/stdout only (no port, no network, no window).
/// It runs on the STA main thread without starting the app, so it never shows settings,
/// tray or hotkeys and does not take the single-instance lock.
public sealed class McpHost : IMcpHost
{
    private const string Key = @"Software\Swivel\WindowsPreview";
    private readonly Func<bool>? enabled;
    private readonly string historyValue;
    /// Tests pass their own switch and history value, never the user's.
    internal McpHost(Func<bool>? enabled = null, string historyValue = "AgentHistory") { this.enabled = enabled; this.historyValue = historyValue; }

    public static int Run()
    {
        var input = Console.OpenStandardInput();
        var output = Console.OpenStandardOutput();
        if (input == Stream.Null || output == Stream.Null) return 2; // started without pipes, e.g. from a shortcut
        string version = typeof(McpHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0";
        var session = new McpSession(new McpHost(), version);
        using var reader = new StreamReader(input, new UTF8Encoding(false, true), false, 65536);
        using var writer = new StreamWriter(output, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
        while (true)
        {
            string? line;
            try { line = ReadLine(reader); }
            catch (DecoderFallbackException) { writer.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{\"code\":-32700,\"message\":\"Invalid UTF-8.\"}}"); return 1; }
            if (line is null) return 0; // end of input: the host closed the server
            if (line.Length == 0) continue;
            string? reply = session.Handle(line);
            if (reply is not null) writer.WriteLine(reply);
        }
    }

    /// A line with an upper bound, so one endless line cannot exhaust memory.
    private static string? ReadLine(StreamReader reader)
    {
        var line = new StringBuilder();
        while (true)
        {
            int c = reader.Read();
            if (c < 0) return line.Length == 0 ? null : line.ToString();
            if (c == '\n') return line.ToString().TrimEnd('\r');
            if (line.Length > McpSession.MaxLineChars)
            {
                while ((c = reader.Read()) >= 0 && c != '\n') { } // discard the rest; Handle reports it too large
                return line.ToString();
            }
            line.Append((char)c);
        }
    }

    public bool Enabled => enabled?.Invoke() ?? (SettingsStore.Load(out bool failed) is var prefs && !failed && prefs.IsOn(Feature.AgentTools));
    public DateTime UtcNow => DateTime.UtcNow;
    public FileStamp Inspect(string path) => NativeFiles.InspectStamp(path);

    public string Run(string action, string path, CancellationToken token)
    {
        if (!System.Linq.Enumerable.Any(McpSession.Actions, a => a.Id == action)) throw new McpToolError("Unknown action.");
        return ImageTools.Run(path, action, 0, token);
    }

    public FileStamp? Stamp(string path)
    {
        try { return NativeFiles.InspectStamp(path); }
        catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { return null; }
    }

    public void Recycle(string path, FileStamp expected)
    {
        // The folder stays held until the file is gone, so it cannot be swapped for a junction
        // between the identity check and the Recycle Bin move.
        WindowsPathPolicy.Validate(path);
        using var parent = NativeFiles.DirectoryLease.Open(Path.GetDirectoryName(path)!);
        if (!NativeFiles.InspectStamp(path).SameObjectAndContent(expected)) throw new McpToolError("The file changed; it was left alone.");
        string root = Path.GetPathRoot(path)!;
        // Only fixed drives with a Recycle Bin; elsewhere Windows would delete permanently.
        if (GetDriveType(root) != 3) throw new McpToolError("This drive has no Recycle Bin, so the file was left alone.");
        var info = new RecycleBinInfo { Size = Marshal.SizeOf<RecycleBinInfo>() };
        if (SHQueryRecycleBin(root, ref info) != 0) throw new McpToolError("This drive has no Recycle Bin, so the file was left alone.");
        var operation = new FileOperation
        {
            Function = 3, // FO_DELETE
            From = path + "\0\0",
            // Undoable, no progress or error UI; Windows still asks if a file is too big to recycle.
            Flags = 0x40 | 0x10 | 0x400 | 0x4 | 0x4000,
        };
        int result = SHFileOperation(ref operation);
        if (result != 0 || operation.Aborted) throw new McpToolError("Windows did not move the file to the Recycle Bin; it was left alone.");
        if (Stamp(path) is not null) throw new McpToolError("Windows did not move the file to the Recycle Bin; it was left alone.");
    }

    public IDisposable LockHistory()
    {
        var mutex = new Mutex(false, @"Local\Swivel.WindowsPreview.AgentHistory." + historyValue);
        try { if (!mutex.WaitOne(TimeSpan.FromSeconds(30))) throw new McpToolError("Another Swivel agent server is busy with the history. Try again."); }
        catch (AbandonedMutexException) { } // the other server ended without releasing it; the history itself is still checked on load
        catch { mutex.Dispose(); throw; }
        return new Held(mutex);
    }
    private sealed class Held(Mutex mutex) : IDisposable
    {
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }

    public string? LoadHistory()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key);
        return key?.GetValue(historyValue) switch { null => null, string json => json, _ => "" };
    }

    public void SaveHistory(string json)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        key.SetValue(historyValue, json, RegistryValueKind.String);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileOperation
    {
        public IntPtr Window;
        public uint Function;
        public string From;
        public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool Aborted;
        public IntPtr NameMappings;
        public string? ProgressTitle;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct RecycleBinInfo { public int Size; public long Bytes, Items; }

    [DllImport("shell32.dll", EntryPoint = "SHFileOperationW", CharSet = CharSet.Unicode)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int SHFileOperation(ref FileOperation operation);
    [DllImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW", CharSet = CharSet.Unicode)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int SHQueryRecycleBin(string root, ref RecycleBinInfo info);
    [DllImport("kernel32.dll", EntryPoint = "GetDriveTypeW", CharSet = CharSet.Unicode)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetDriveType(string root);
}
