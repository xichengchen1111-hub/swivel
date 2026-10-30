using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Swivel.Core;

namespace Swivel.Windows;

public static partial class NativeFiles
{
    // References never request FILE_READ_DATA/LIST_DIRECTORY. Do not follow a
    // reparse point or ask a provider to recall data, including in ancestors.
    internal static ShelfReference InspectShelfReference(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ShelfReferences.ValidatePath(path);
        string root = path[..3];
        if (GetDriveType(root) is not 2 and not 3 and not 6) throw new UserError(LocalOnly);
        var handles = new List<SafeFileHandle>();
        try
        {
            string current = root; bool onlineOnly = false;
            ShelfReference Read(string candidate)
            {
                token.ThrowIfCancellationRequested();
                var handle = handles.Count == 0
                    ? Open(candidate, ReadAttributes, 3, 3, OpenReparse | BackupSemantics | 0x00100000) // OPEN_NO_RECALL
                    : OpenShelfChild(handles[^1], System.IO.Path.GetFileName(candidate));
                handles.Add(handle);
                if (GetFileType(handle) != 1) throw new UserError(LocalOnly);
                if (!GetShelfTag(handle, 9, out var info, 8)) ThrowLast(); // FileAttributeTagInfo
                var reference = ShelfReferences.FromAttributes(candidate, info.Attributes, info.Tag);
                onlineOnly |= reference.IsOnlineOnly;
                return reference with { Path = candidate, IsOnlineOnly = onlineOnly };
            }
            var rootReference = Read(current);
            if (!rootReference.IsFolder) throw new UserError(LocalOnly);
            if (path.Length == 3) return rootReference;
            string[] parts = path[3..].Split('\\');
            for (int i = 0; i < parts.Length; i++)
            {
                current = System.IO.Path.Combine(current, parts[i]);
                var reference = Read(current);
                if (i == parts.Length - 1) { token.ThrowIfCancellationRequested(); return reference; }
                if (!reference.IsFolder) throw new UserError(LocalOnly);
            }
            throw new UserError(LocalOnly);
        }
        finally { for (int i = handles.Count - 1; i >= 0; i--) handles[i].Dispose(); }
    }
    // Attribute-only handles do not prevent ancestor renames. Open one leaf
    // relative to the checked parent so a path swap cannot redirect the probe.
    private static SafeFileHandle OpenShelfChild(SafeFileHandle parent, string leaf)
    {
        WindowsPathPolicy.ValidateName(leaf);
        bool retained = false;
        IntPtr buffer = Marshal.StringToHGlobalUni(leaf), namePointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        try
        {
            parent.DangerousAddRef(ref retained);
            var name = new UnicodeString { Length = checked((ushort)(leaf.Length * 2)), MaximumLength = checked((ushort)(leaf.Length * 2 + 2)), Buffer = buffer };
            Marshal.StructureToPtr(name, namePointer, false);
            var attributes = new ObjectAttributes { Length = Marshal.SizeOf<ObjectAttributes>(), RootDirectory = parent.DangerousGetHandle(), ObjectName = namePointer, Attributes = 0x40 };
            // Backup intent, open reparse point, no recall; no data/list access.
            int status = NtOpenFile(out var handle, ReadAttributes, ref attributes, out _, 3, 0x4000 | 0x200000 | 0x400000);
            if (status >= 0) return handle;
            handle.Dispose(); throw new System.ComponentModel.Win32Exception((int)RtlNtStatusToDosError(status));
        }
        finally { if (retained) parent.DangerousRelease(); Marshal.FreeHGlobal(namePointer); Marshal.FreeHGlobal(buffer); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct ShelfTagInfo { public uint Attributes, Tag; }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetShelfTag(SafeFileHandle handle, int kind, out ShelfTagInfo info, uint length);
}
