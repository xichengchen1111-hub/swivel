using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32.SafeHandles;
using Swivel.Core;

namespace Swivel.Windows;

public static partial class NativeFiles
{
    internal sealed partial class MetadataDirectory
    {
        // The caller retains the chosen root throughout both enumeration and
        // comparison. Reopen every descendant through held, rechecked directories.
        internal IDuplicateInput OpenDuplicate(DuplicateCandidate candidate)
        {
            var held = new List<MetadataDirectory>(); SafeFileHandle? data = null;
            try
            {
                if (string.IsNullOrEmpty(candidate.RelativePath)) throw new UserError(LocalOnly);
                string[] parts = candidate.RelativePath.Split('\\');
                foreach (string part in parts) WindowsPathPolicy.ValidateName(part);
                string fullPath = System.IO.Path.Combine(path, candidate.RelativePath);
                WindowsPathPolicy.Validate(fullPath);
                // The 64-bit FILE_ID_INFO volume is deliberately never compared
                // with the older BY_HANDLE_FILE_INFORMATION 32-bit serial.
                ulong rootVolume = ReadIdentity(handle).Volume;
                var parent = this;
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    parent = (MetadataDirectory)parent.OpenChild(parts[i]); held.Add(parent);
                    if (ReadIdentity(parent.handle).Volume != rootVolume) throw new UserError(LocalOnly);
                }
                using var probe = NativeFiles.Open(fullPath, ReadAttributes, 1, 3, OpenReparse);
                var before = ReadDuplicateStamp(probe, rootVolume);
                // Keep the attribute-only probe until the data handle and its
                // identity have been verified. Never hydrate a skipped placeholder.
                data = NativeFiles.Open(fullPath, GenericRead | ReadAttributes, 1, 3, OpenReparse);
                var after = ReadDuplicateStamp(data, rootVolume);
                if (before != after || after.Length != candidate.Length) throw new IOException("File changed during comparison.");
                // Disable FileStream read-ahead so the comparison's read budget
                // is also the requested amount of file data, including sampling.
                var stream = new FileStream(data, FileAccess.Read, 1); data = null;
                return new DuplicateInput(stream, after, rootVolume, held);
            }
            catch
            {
                data?.Dispose(); for (int i = held.Count - 1; i >= 0; i--) held[i].Dispose(); throw;
            }
        }
    }

    private sealed class DuplicateInput(FileStream stream, DuplicateStamp stamp, ulong volume, List<MetadataDirectory> held) : IDuplicateInput
    {
        public Stream Stream => stream;
        public DuplicateStamp Stamp => stamp;
        public DuplicateStamp Refresh() => ReadDuplicateStamp(stream.SafeFileHandle, volume);
        public void Dispose()
        {
            try { stream.Dispose(); }
            finally { for (int i = held.Count - 1; i >= 0; i--) held[i].Dispose(); held.Clear(); }
        }
    }
    private static (ulong Volume, string Identity) ReadIdentity(SafeFileHandle handle)
    {
        // FILE_ID_INFO: 64-bit volume serial followed by a 128-bit opaque ID.
        // Providers without this API are unsupported rather than guessed from paths.
        var bytes = new byte[24];
        if (!GetDirectoryInfo(handle, 18, bytes, 24)) ThrowLast();
        if (bytes.AsSpan(8).IndexOfAnyExcept((byte)0) < 0) throw new UserError(LocalOnly);
        return (BinaryPrimitives.ReadUInt64LittleEndian(bytes), Convert.ToHexString(bytes));
    }
    private static DuplicateStamp ReadDuplicateStamp(SafeFileHandle handle, ulong expectedVolume)
    {
        Check(handle, false);
        if (!GetFileInformationByHandle(handle, out var info)) ThrowLast();
        if ((info.Attributes & FolderSizes.SkippedAttributes) != 0) throw new UserError(LocalOnly);
        var identity = ReadIdentity(handle);
        if (identity.Volume != expectedVolume || info.SizeHigh > int.MaxValue) throw new UserError(LocalOnly);
        long size = ((long)info.SizeHigh << 32) | info.SizeLow;
        long write = unchecked(((long)(uint)info.Write.dwHighDateTime << 32) | (uint)info.Write.dwLowDateTime);
        return new(identity.Identity, size, write);
    }
}
