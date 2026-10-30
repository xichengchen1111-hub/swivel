using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Swivel.Core;

namespace Swivel.Windows;

/// exFAT and FAT32 keep no lasting file identity: FILE_ID_INFO is not supported, and the file
/// index Windows reports is the position of the directory entry, which a rename to a longer name
/// or a move changes (and which a new file can take over). An open handle, though, stays bound
/// to its object wherever it goes. So on these drives an item's identity is a random token bound
/// to a handle Swivel keeps: attribute-only, sharing everything, so other apps can still read,
/// edit, move and delete the item; while it is held a deleted name cannot be taken by a new file.
/// Undo reopens the very object from that handle and checks it is still where the receipt says.
public static partial class NativeFiles
{
    /// Marks a MoveIdentity whose Low part is a held-handle token (a file or a moved folder).
    internal const ulong FatToken = 0x5357_4641_5448_4C44; // "SWFATHLD"
    /// Marks a MoveIdentity whose Low part is a FAT directory entry position (held ancestors only).
    internal const ulong FatPosition = 0x5357_4641_5450_4F53; // "SWFATPOS"
    private static readonly Dictionary<ulong, SafeFileHandle> fatHolds = new();

    internal static VolumeFormat Format(SafeFileHandle handle)
    {
        var name = new StringBuilder(64);
        if (!MoveVolumeInformation(handle, null, 0, out _, out _, out _, name, name.Capacity)) ThrowLast();
        return FatRules.Parse(name.ToString());
    }
    internal static VolumeFormat Format(string path)
    {
        var name = new StringBuilder(64);
        if (!GetVolumeInformation(path[..3], null, 0, out _, out _, out _, name, name.Capacity)) ThrowLast();
        return FatRules.Parse(name.ToString());
    }

    internal static ulong NewFatToken()
    {
        Span<byte> bytes = stackalloc byte[8];
        for (;;) { RandomNumberGenerator.Fill(bytes); ulong token = BitConverter.ToUInt64(bytes); if (token != 0) return token; }
    }

    /// The directory entry position of a held folder on FAT, which stays put while the folder
    /// itself is held without delete sharing.
    internal static MoveIdentity FatPositionIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) ThrowLast();
        return new(info.Volume, ((ulong)info.IndexHigh << 32) | info.IndexLow, FatPosition);
    }
    internal static uint FatVolume(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) ThrowLast();
        return info.Volume;
    }

    /// Keeps an attribute-only, share-everything handle to the object `handle` refers to under
    /// `token`. ReOpenFile opens the same object, never a path.
    internal static void HoldFat(ulong token, SafeFileHandle handle, bool directory)
    {
        var held = Reopen(handle, ReadAttributes, 7, directory, out int error);
        if (held.IsInvalid) { held.Dispose(); throw new Win32Exception(error); }
        SafeFileHandle? old;
        lock (fatHolds) { fatHolds.TryGetValue(token, out old); fatHolds[token] = held; }
        old?.Dispose();
    }

    /// A new handle with `access` to the object held under `token`, which must still be at
    /// `path` (exactly, as the file system reports it).
    internal static SafeFileHandle ReopenFat(ulong token, string path, uint access, uint share, bool directory)
    {
        SafeFileHandle? held;
        lock (fatHolds) fatHolds.TryGetValue(token, out held);
        if (held is null) throw new UserError(FatRules.Released);
        var handle = Reopen(held, access, share, directory, out int error);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            // A deleted item cannot be reopened (access denied); one in use by another app is shared out.
            if (error == 32) throw new UserError(directory ? FolderMove.InUse : "A file is in use by another app. Nothing was changed.");
            throw new UserError(FatRules.MovedElsewhere);
        }
        try
        {
            if (!string.Equals(FinalPath(handle), @"\\?\" + path, StringComparison.Ordinal)) throw new UserError(FatRules.MovedElsewhere);
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    /// An item opened by path on FAT must be named exactly as stored: a short 8.3 name or other
    /// letter case would let two chosen paths name one item, which no identity could reveal.
    internal static void RequireExactFatName(SafeFileHandle handle, string path)
    {
        if (!string.Equals(FinalPath(handle), @"\\?\" + path, StringComparison.Ordinal)) throw new UserError(FatRules.ExactName);
    }
    /// The path a FAT item has now, as the file system reports it.
    internal static string FatCurrentPath(SafeFileHandle handle)
    {
        string final = FinalPath(handle);
        return final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..] : final;
    }

    /// Releases every held FAT reference no undo history still needs.
    public static void RetainFatHolds(IEnumerable<ulong> tokens)
    {
        var keep = tokens.ToHashSet(); List<SafeFileHandle> release;
        lock (fatHolds)
        {
            release = fatHolds.Where(x => !keep.Contains(x.Key)).Select(x => x.Value).ToList();
            foreach (var key in fatHolds.Keys.Where(x => !keep.Contains(x)).ToArray()) fatHolds.Remove(key);
        }
        foreach (var handle in release) handle.Dispose();
    }
    internal static int FatHoldCount { get { lock (fatHolds) return fatHolds.Count; } }

    /// The volume a handle is on, for choosing between moving and copying: FILE_ID_INFO's 64-bit
    /// serial on NTFS; on drives without it, the 32-bit serial marked so it never equals one.
    internal static ulong VolumeKey(SafeFileHandle handle)
    {
        try { return ReadIdentity(handle).Volume; }
        catch (Win32Exception error) when (error.NativeErrorCode == 87)
        {
            if (!GetFileInformationByHandle(handle, out var info)) ThrowLast();
            return (1UL << 63) | info.Volume;
        }
    }

    // A new handle to the very object `original` refers to. ReOpenFile does that for files but
    // refuses directories; for those the same is done by opening an empty name relative to the
    // held handle (NtOpenFile), which also never resolves a path.
    private static SafeFileHandle Reopen(SafeFileHandle original, uint access, uint share, bool directory, out int error)
    {
        error = 0;
        if (!directory)
        {
            var file = ReOpenFile(original, access, share, OpenReparse);
            if (file.IsInvalid) error = Marshal.GetLastWin32Error();
            return file;
        }
        bool retained = false;
        try
        {
            original.DangerousAddRef(ref retained);
            var name = new UnicodeString();
            var attributes = new ObjectAttributes { Length = Marshal.SizeOf<ObjectAttributes>(), RootDirectory = original.DangerousGetHandle(), ObjectName = IntPtr.Zero, Attributes = 0x40 }; // OBJ_CASE_INSENSITIVE
            IntPtr namePointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            try
            {
                Marshal.StructureToPtr(name, namePointer, false); attributes.ObjectName = namePointer;
                // SYNCHRONIZE; directory, synchronous I/O as CreateFile gives, backup intent, no reparse following.
                int status = NtOpenFile(out var handle, access | 0x100000, ref attributes, out _, share, 0x1 | 0x20 | 0x4000 | 0x200000);
                if (status < 0) { handle.Dispose(); error = (int)RtlNtStatusToDosError(status); return new SafeFileHandle(IntPtr.Zero, true); }
                return handle;
            }
            finally { Marshal.FreeHGlobal(namePointer); }
        }
        finally { if (retained) original.DangerousRelease(); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)] private struct ObjectAttributes
    {
        public int Length; public IntPtr RootDirectory, ObjectName; public uint Attributes; public IntPtr SecurityDescriptor, SecurityQualityOfService;
    }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtOpenFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes, out IoStatusBlock status, uint share, uint options);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle ReOpenFile(SafeFileHandle original, uint access, uint share, uint flags);
}
