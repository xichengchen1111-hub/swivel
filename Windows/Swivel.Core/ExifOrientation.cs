using System;
using System.Buffers.Binary;

namespace Swivel.Core;

public enum ExifOrientationStatus { None, Found, Malformed, Ambiguous }

// What the metadata tools learned about the source's EXIF Orientation (tag 0x0112).
// Value is 1–8 only when Status is Found.
public readonly record struct ExifOrientationInfo(ExifOrientationStatus Status, int Value)
{
    public static readonly ExifOrientationInfo None = new(ExifOrientationStatus.None, 0);
    public static readonly ExifOrientationInfo Malformed = new(ExifOrientationStatus.Malformed, 0);
    public static readonly ExifOrientationInfo Ambiguous = new(ExifOrientationStatus.Ambiguous, 0);
    // Only a non-default orientation is worth keeping; 1 means "as stored".
    public bool CanKeep => Status == ExifOrientationStatus.Found && Value is >= 2 and <= 8;
}

// Original strict reader of one TIFF IFD0 Orientation entry, and writer of a
// minimal replacement TIFF holding only that entry. Nothing else in the source
// EXIF (other tags, sub-IFDs, thumbnails, maker notes, GPS) is interpreted or kept.
public static class ExifOrientation
{
    public const ushort Tag = 0x0112;
    // Header 8 + entry count 2 + one 12-byte entry + next-IFD offset 4.
    public const int TiffLength = 26;

    // Reads IFD0 of a TIFF stream (starting at its byte-order mark). Returns None when
    // IFD0 is well formed but has no Orientation, Found for one SHORT×1 value 1–8,
    // and Malformed for any framing, type, count, value or duplicate-tag problem.
    public static ExifOrientationInfo Read(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8) return ExifOrientationInfo.Malformed;
        bool little;
        if (tiff[0] == (byte)'I' && tiff[1] == (byte)'I') little = true;
        else if (tiff[0] == (byte)'M' && tiff[1] == (byte)'M') little = false;
        else return ExifOrientationInfo.Malformed;
        if (U16(tiff, little, 2) != 42) return ExifOrientationInfo.Malformed;
        uint ifd = U32(tiff, little, 4);
        if (ifd < 8 || ifd > (uint)tiff.Length - 2) return ExifOrientationInfo.Malformed;
        int count = U16(tiff, little, (int)ifd);
        // The entries and the 4-byte next-IFD offset must all lie inside the block.
        if (count == 0 || (long)ifd + 2 + 12L * count + 4 > tiff.Length) return ExifOrientationInfo.Malformed;
        int value = 0;
        for (int i = 0; i < count; i++)
        {
            int entry = (int)ifd + 2 + 12 * i;
            if (U16(tiff, little, entry) != Tag) continue;
            if (value != 0 || U16(tiff, little, entry + 2) != 3 || U32(tiff, little, entry + 4) != 1) return ExifOrientationInfo.Malformed;
            int v = U16(tiff, little, entry + 8);
            if (v is < 1 or > 8) return ExifOrientationInfo.Malformed;
            value = v;
        }
        return value == 0 ? ExifOrientationInfo.None : new(ExifOrientationStatus.Found, value);
    }

    private static ushort U16(ReadOnlySpan<byte> tiff, bool little, int at) => little ? BinaryPrimitives.ReadUInt16LittleEndian(tiff.Slice(at, 2)) : BinaryPrimitives.ReadUInt16BigEndian(tiff.Slice(at, 2));
    private static uint U32(ReadOnlySpan<byte> tiff, bool little, int at) => little ? BinaryPrimitives.ReadUInt32LittleEndian(tiff.Slice(at, 4)) : BinaryPrimitives.ReadUInt32BigEndian(tiff.Slice(at, 4));

    // Big-endian TIFF: "MM", 42, IFD0 at 8 with one SHORT Orientation entry, no next IFD.
    public static byte[] Tiff(int orientation)
    {
        if (orientation is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(orientation));
        var bytes = new byte[TiffLength];
        bytes[0] = (byte)'M'; bytes[1] = (byte)'M';
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(10), Tag);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(12), 3);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(14), 1);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(18), (ushort)orientation);
        // bytes 20–21 pad the 4-byte value field; 22–25 next-IFD offset 0.
        return bytes;
    }

    public static string Describe(int orientation) => orientation switch
    {
        1 => "Normal",
        2 => "Mirrored horizontally",
        3 => "Rotated 180°",
        4 => "Mirrored vertically",
        5 => "Mirrored horizontally, rotated 270° clockwise",
        6 => "Rotated 90° clockwise",
        7 => "Mirrored horizontally, rotated 90° clockwise",
        8 => "Rotated 270° clockwise",
        _ => throw new ArgumentOutOfRangeException(nameof(orientation))
    };
}
