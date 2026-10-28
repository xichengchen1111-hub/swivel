using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Swivel.Core;

/// Convert to ICO (as Mac 1.14.4): the picture at 16, 32, 48, 64, 128 and 256 pixels, as many
/// as fit within its longer side (16 at least), each centred on a transparent square and scaled
/// to that square's side, never stretched or cropped. Sizes below 256 are stored as 32-bit
/// bitmaps with an AND mask (every Windows version reads them), 256 as PNG (Windows Vista and
/// later). An original writer; the layout follows Microsoft's ICONDIR / BITMAPINFOHEADER.
public static class IcoFile
{
    public static readonly IReadOnlyList<int> Sizes = new[] { 16, 32, 48, 64, 128, 256 };
    public const string Notice = "ICO saves the picture at 16, 32, 48, 64, 128 and 256 pixels, as many as fit within its longer side (16 at least). Each size is centred on a transparent square without stretching or cropping. Sizes up to 128 are 32-bit bitmaps, 256 is PNG.";
    private static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    public static int[] SizesFor(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        int longest = Math.Max(width, height);
        var fit = Sizes.Where(size => size <= longest).ToArray();
        return fit.Length == 0 ? new[] { Sizes[0] } : fit;
    }

    /// The picture's size inside a square of `size`: the longer side fills it, the other keeps the
    /// proportion (at least one pixel).
    public static (int Width, int Height) Fit(int width, int height, int size)
    {
        if (width <= 0 || height <= 0 || size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        int longest = Math.Max(width, height);
        int Side(int side) => side == longest ? size : Math.Clamp((int)Math.Round((double)side * size / longest, MidpointRounding.AwayFromZero), 1, size);
        return (Side(width), Side(height));
    }

    /// Centres an already scaled picture (straight-alpha BGRA) on a transparent square; anything
    /// beyond the square is clipped evenly.
    public static PixelImage Square(PixelImage scaled, int size)
    {
        if (size <= 0 || size > 256) throw new ArgumentOutOfRangeException(nameof(size));
        if (scaled.Width <= 0 || scaled.Height <= 0 || scaled.Bgra.Length != scaled.Width * scaled.Height * 4) throw new ArgumentException("Bad pixels.", nameof(scaled));
        var square = new byte[size * size * 4];
        int dx = (size - scaled.Width) / 2, dy = (size - scaled.Height) / 2;
        for (int y = 0; y < size; y++)
        {
            int sy = y - dy; if (sy < 0 || sy >= scaled.Height) continue;
            int x0 = Math.Max(0, dx), x1 = Math.Min(size, dx + scaled.Width);
            if (x1 <= x0) continue;
            Buffer.BlockCopy(scaled.Bgra, (sy * scaled.Width + (x0 - dx)) * 4, square, (y * size + x0) * 4, (x1 - x0) * 4);
        }
        return new PixelImage(size, size, square);
    }

    /// A 32-bit icon bitmap: BITMAPINFOHEADER (height doubled), bottom-up BGRA rows, then the
    /// 1-bit AND mask (set where the pixel is fully transparent), rows padded to 4 bytes.
    public static byte[] Bitmap(PixelImage square)
    {
        int size = square.Width;
        if (size <= 0 || size >= 256 || square.Height != size || square.Bgra.Length != size * size * 4) throw new ArgumentException("Bad icon square.", nameof(square));
        int maskStride = (size + 31) / 32 * 4, colour = size * size * 4, mask = maskStride * size;
        var data = new byte[40 + colour + mask];
        var span = data.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(span, 40);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], size);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], size * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[14..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(span[20..], colour + mask);
        for (int row = 0; row < size; row++)
        {
            int source = (size - 1 - row) * size * 4;
            Buffer.BlockCopy(square.Bgra, source, data, 40 + row * size * 4, size * 4);
            for (int x = 0; x < size; x++)
                if (square.Bgra[source + x * 4 + 3] == 0) data[40 + colour + row * maskStride + x / 8] |= (byte)(0x80 >> (x % 8));
        }
        return data;
    }

    /// Writes the icon directory and its images in the order given (each a bitmap from `Bitmap`
    /// or a PNG of a square of that size).
    public static void Write(Stream output, IReadOnlyList<(int Size, byte[] Data)> images, CancellationToken token = default)
    {
        if (images.Count == 0 || images.Count > Sizes.Count) throw new ArgumentException("Bad icon image count.", nameof(images));
        var header = new byte[6 + 16 * images.Count];
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), (ushort)images.Count);
        int offset = header.Length;
        for (int index = 0; index < images.Count; index++)
        {
            var (size, data) = images[index];
            if (size <= 0 || size > 256 || data.Length == 0) throw new ArgumentException("Bad icon image.", nameof(images));
            var entry = header.AsSpan(6 + 16 * index);
            entry[0] = entry[1] = (byte)(size == 256 ? 0 : size);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[4..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[6..], 32);
            BinaryPrimitives.WriteInt32LittleEndian(entry[8..], data.Length);
            BinaryPrimitives.WriteInt32LittleEndian(entry[12..], offset);
            offset = checked(offset + data.Length);
        }
        output.Write(header);
        foreach (var (_, data) in images) { token.ThrowIfCancellationRequested(); output.Write(data); }
    }

    /// Reads an icon file's directory back: each entry's size, whether it is PNG, and its bytes.
    /// Refuses a damaged file (used to check what was written).
    public static IReadOnlyList<(int Size, bool Png, byte[] Data)> Read(byte[] file)
    {
        if (file.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(file) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(2)) != 1)
            throw new InvalidDataException("Not an icon file.");
        int count = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(4));
        if (count == 0 || file.Length < 6 + 16L * count) throw new InvalidDataException("Damaged icon directory.");
        var list = new List<(int, bool, byte[])>();
        for (int index = 0; index < count; index++)
        {
            var entry = file.AsSpan(6 + 16 * index, 16);
            int size = entry[0] == 0 ? 256 : entry[0];
            long length = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]), start = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            if (entry[1] != entry[0] || length == 0 || start < 6 + 16L * count || start + length > file.Length) throw new InvalidDataException("Damaged icon entry.");
            var data = file.AsSpan((int)start, (int)length).ToArray();
            bool png = data.AsSpan().StartsWith(PngSignature);
            if (!png && (data.Length < 40 || BinaryPrimitives.ReadInt32LittleEndian(data) != 40 || BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4)) != size
                || BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8)) != size * 2 || BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(14)) != 32
                || data.Length != 40 + size * size * 4 + (size + 31) / 32 * 4 * size))
                throw new InvalidDataException("Damaged icon bitmap.");
            list.Add((size, png, data));
        }
        return list;
    }
}
