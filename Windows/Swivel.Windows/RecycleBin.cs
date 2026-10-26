using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Swivel.Core;

namespace Swivel.Windows;

/// The Windows Recycle Bin: moving an item there (only on fixed drives that have one, so Windows
/// never deletes permanently instead) and putting it back through the shell's own Restore.
/// Callers hold the item's parent folder and recheck the item first.
internal static class RecycleBin
{
    /// Whether the drive holding `path` is a fixed drive with a Recycle Bin.
    internal static bool Available(string path)
    {
        string? root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root) || GetDriveType(root) != 3) return false;
        var info = new RecycleBinInfo { Size = Marshal.SizeOf<RecycleBinInfo>() };
        return SHQueryRecycleBin(root, ref info) == 0;
    }

    /// Asks Windows to move one file or folder to the Recycle Bin. False when Windows refused or
    /// the user cancelled its prompt (Windows asks before deleting an item too large to recycle;
    /// `owner` keeps that prompt above the window that asked).
    internal static bool Move(string path, IntPtr owner = default)
    {
        var operation = new FileOperation
        {
            Window = owner,
            Function = 3, // FO_DELETE
            From = path + "\0\0",
            // Undoable, no progress or error UI; Windows still asks if an item is too big to recycle.
            Flags = 0x40 | 0x10 | 0x400 | 0x4 | 0x4000,
        };
        return SHFileOperation(ref operation) == 0 && !operation.Aborted;
    }

    /// Restores the Recycle Bin item stored at `stored` with the shell's Restore. False when it
    /// is no longer there. Call on an STA thread.
    internal static bool Restore(string stored)
    {
        var held = new List<object>();
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application") ?? throw new UserError(RestoreUnavailable);
            object shell = Keep(held, Activator.CreateInstance(type)) ?? throw new UserError(RestoreUnavailable);
            object folder = Keep(held, Call(shell, "NameSpace", 10)) ?? throw new UserError(RestoreUnavailable); // ssfBITBUCKET
            object items = Keep(held, Call(folder, "Items")) ?? throw new UserError(RestoreUnavailable);
            int count = Get(items, "Count") is int n ? n : 0;
            for (int i = 0; i < count; i++)
            {
                object? item = Keep(held, Call(items, "Item", i));
                if (item is null || Get(item, "Path") is not string path || !string.Equals(path, stored, StringComparison.OrdinalIgnoreCase)) continue;
                Call(item, "InvokeVerb", "undelete"); // the language-independent verb for Restore
                return true;
            }
            return false;
        }
        finally { foreach (var com in held) if (Marshal.IsComObject(com)) Marshal.FinalReleaseComObject(com); }
    }
    private static object? Keep(List<object> held, object? com) { if (com is not null) held.Add(com); return com; }

    internal const string RestoreUnavailable = "Windows could not open the Recycle Bin to restore the folder.";
    private static object? Call(object target, string name, params object[] arguments)
        => target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, arguments);
    private static object? Get(object target, string name)
        => target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null);

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
