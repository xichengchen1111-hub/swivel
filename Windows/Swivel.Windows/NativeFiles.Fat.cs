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
        var held = ReOpenFile(handle, ReadAttributes, 7, OpenReparse | (directory ? BackupSemantics : 0));
        if (held.IsInvalid) { int error = Marshal.GetLastWin32Error(); held.Dispose(); throw new Win32Exception(error); }
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
        var handle = ReOpenFile(held, access, share, OpenReparse | (directory ? BackupSemantics : 0));
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error(); handle.Dispose();
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

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle ReOpenFile(SafeFileHandle original, uint access, uint share, uint flags);
}
