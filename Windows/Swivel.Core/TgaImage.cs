using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;

namespace Swivel.Core;

/// TGA (Truevision) images, as Mac 1.14.x reads and writes them: an original bounded reader for
/// uncompressed and run-length colour-mapped, true-colour and greyscale images, and a writer for
/// uncompressed 24-bit (opaque) or 32-bit (with transparency) images. The layout follows the
/// Truevision TGA 2.0 specification; no third-party code.
public static class TgaImage
{
    public const string Notice = "TGA saves uncompressed 24-bit colour, or 32-bit colour when the picture has transparency, without a colour profile or other metadata.";
    public const string Invalid = "The TGA data is invalid or incomplete.";
    public const string Unsupported = "This TGA uses an unsupported pixel layout. Use a colour-mapped, true-colour or greyscale TGA, or save a PNG.";
    private static readonly byte[] Signature = "TRUEVISION-XFILE.\0"u8.ToArray();

    public static PixelImage Read(Stream input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!input.CanRead || !input.CanSeek) throw new ArgumentException("A seekable readable stream is required.", nameof(input));
        if (input.Length > ImageRules.MaxInputBytes) throw new UserError("Image files are limited to 128 MB.");
        var file = new byte[input.Length];
        input.Position = 0; input.ReadExactly(file);
        return Read(file, token);
    }

    public static PixelImage Read(byte[] file, CancellationToken token)
    {
        if (file.Length < 18) throw new UserError(Invalid);
        int idLength = file[0], mapType = file[1], type = file[2];
        int mapFirst = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(3)), mapLength = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(5)), mapBits = file[7];
        int width = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(12)), height = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(14));
        int depth = file[16], descriptor = file[17], alphaBits = descriptor & 15;
        if (mapType > 1 || (descriptor & 0xC0) != 0) throw new UserError(Unsupported);
        bool rle = type is 9 or 10 or 11, mapped = type is 1 or 9, grey = type is 3 or 11;
        if (!mapped && !grey && type is not 2 and not 10) throw new UserError(Unsupported);
        if (mapped ? mapType != 1 || depth != 8 || mapLength == 0 : grey ? depth != 8 && !(depth == 16 && alphaBits == 8) : depth is not 15 and not 16 and not 24 and not 32)
            throw new UserError(Unsupported);
        if (mapType == 1 && mapBits is not 15 and not 16 and not 24 and not 32) throw new UserError(Unsupported);
        ImageRules.Validate(width, height);
        long offset = 18L + idLength;
        int mapEntry = (mapBits + 7) / 8;
        // A colour map in a true-colour or greyscale file is allowed by the format and skipped.
        byte[]? palette = null;
        if (mapType == 1)
        {
            if (offset + (long)mapLength * mapEntry > file.Length) throw new UserError(Invalid);
            if (mapped)
            {
                palette = new byte[mapLength * 4];
                for (int index = 0; index < mapLength; index++) Colour(file, (int)offset + index * mapEntry, mapBits, mapBits == 32 || mapBits == 16 && alphaBits == 1, palette, index * 4);
            }
            offset += (long)mapLength * mapEntry;
        }
        int bytes = (depth + 7) / 8;
        long pixels = (long)width * height;
        // Read every pixel in stream order (run-length packets may run across rows).
        var raw = new byte[pixels * bytes];
        if (!rle)
        {
            if (offset + raw.Length > file.Length) throw new UserError(Invalid);
            Buffer.BlockCopy(file, (int)offset, raw, 0, raw.Length);
            offset += raw.Length;
        }
        else
        {
            long written = 0;
            while (written < pixels)
            {
                if ((written & 0xFFFF) == 0) token.ThrowIfCancellationRequested();
                if (offset >= file.Length) throw new UserError(Invalid);
                int header = file[offset++], count = (header & 127) + 1;
                if (written + count > pixels) throw new UserError(Invalid);
                if ((header & 128) != 0)
                {
                    if (offset + bytes > file.Length) throw new UserError(Invalid);
                    for (int repeat = 0; repeat < count; repeat++) Buffer.BlockCopy(file, (int)offset, raw, (int)((written + repeat) * bytes), bytes);
                    offset += bytes;
                }
                else
                {
                    if (offset + (long)count * bytes > file.Length) throw new UserError(Invalid);
                    Buffer.BlockCopy(file, (int)offset, raw, (int)(written * bytes), count * bytes);
                    offset += (long)count * bytes;
                }
                written += count;
            }
        }
        bool alpha = !mapped && !grey && (depth == 32 && alphaBits == 8 || depth == 16 && alphaBits == 1) || grey && depth == 16
            || mapped && (mapBits == 32 || mapBits == 16 && alphaBits == 1);
        int attributes = Attributes(file);
        if (attributes is 0 or 1 or 2) alpha = false;
        var bgra = new byte[pixels * 4];
        bool top = (descriptor & 0x20) != 0, right = (descriptor & 0x10) != 0;
        for (int y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            int row = top ? y : height - 1 - y;
            for (int x = 0; x < width; x++)
            {
                int column = right ? width - 1 - x : x;
                int source = (y * width + x) * bytes, target = (row * width + column) * 4;
                if (mapped)
                {
                    int index = raw[source] - mapFirst;
                    if (index < 0 || index >= mapLength) throw new UserError(Invalid);
                    Buffer.BlockCopy(palette!, index * 4, bgra, target, 4);
                }
                else if (grey) { bgra[target] = bgra[target + 1] = bgra[target + 2] = raw[source]; bgra[target + 3] = depth == 16 ? raw[source + 1] : (byte)255; }
                else Colour(raw, source, depth, alpha, bgra, target);
                if (!alpha) bgra[target + 3] = 255;
            }
        }
        // Many older writers mark 32-bit files as having alpha but leave every alpha value at
        // zero; without a TGA 2.0 attribute saying otherwise, such a file is read as opaque.
        if (alpha && attributes < 0 && AllTransparent(bgra)) for (int index = 3; index < bgra.Length; index += 4) bgra[index] = 255;
        if (alpha && attributes == 4) Unpremultiply(bgra);
        return new PixelImage(width, height, bgra);
    }

    /// The TGA 2.0 extension area's attributes type, or -1 without a footer or extension area.
    private static int Attributes(byte[] file)
    {
        if (file.Length < 18 + 26 || !file.AsSpan(file.Length - 18).SequenceEqual(Signature)) return -1;
        long extension = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(file.Length - 26));
        // An extension area that is missing or of another size says nothing about alpha.
        if (extension < 18 || extension + 495 > file.Length - 26 || BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan((int)extension)) != 495) return -1;
        return file[extension + 494];
    }

    private static void Colour(byte[] data, int at, int bits, bool alpha, byte[] bgra, int target)
    {
        if (bits is 15 or 16)
        {
            int value = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at));
            static byte Five(int v) => (byte)(v << 3 | v >> 2);
            bgra[target] = Five(value & 31); bgra[target + 1] = Five(value >> 5 & 31); bgra[target + 2] = Five(value >> 10 & 31);
            bgra[target + 3] = alpha ? (byte)((value & 0x8000) != 0 ? 255 : 0) : (byte)255;
            return;
        }
        bgra[target] = data[at]; bgra[target + 1] = data[at + 1]; bgra[target + 2] = data[at + 2];
        bgra[target + 3] = bits == 32 && alpha ? data[at + 3] : (byte)255;
    }

    private static bool AllTransparent(byte[] bgra)
    {
        for (int index = 3; index < bgra.Length; index += 4) if (bgra[index] != 0) return false;
        return true;
    }

    private static void Unpremultiply(byte[] bgra)
    {
        for (int index = 0; index < bgra.Length; index += 4)
        {
            int a = bgra[index + 3];
            for (int channel = 0; channel < 3; channel++)
                bgra[index + channel] = a == 0 ? (byte)0 : (byte)Math.Min(255, (bgra[index + channel] * 255 + a / 2) / a);
        }
    }

    /// Uncompressed, top-left origin, 24-bit when every pixel is opaque and 32-bit otherwise,
    /// followed by a TGA 2.0 extension area whose attributes type says whether alpha is real.
    public static void Write(PixelImage image, Stream output, CancellationToken token = default)
    {
        ImageRules.Validate(image.Width, image.Height);
        if (image.Bgra.Length != image.Width * image.Height * 4) throw new ArgumentException("Bad pixels.", nameof(image));
        bool opaque = true;
        for (int index = 3; index < image.Bgra.Length && opaque; index += 4) opaque = image.Bgra[index] == 255;
        int bytes = opaque ? 3 : 4;
        var header = new byte[18];
        header[2] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), (ushort)image.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), (ushort)image.Height);
        header[16] = (byte)(bytes * 8); header[17] = (byte)(opaque ? 0x20 : 0x28);
        output.Write(header);
        var row = new byte[image.Width * bytes];
        for (int y = 0; y < image.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < image.Width; x++) Buffer.BlockCopy(image.Bgra, (y * image.Width + x) * 4, row, x * bytes, bytes);
            output.Write(row);
        }
        long extension = 18 + (long)row.Length * image.Height;
        var area = new byte[495];
        BinaryPrimitives.WriteUInt16LittleEndian(area, 495);
        area[494] = (byte)(opaque ? 0 : 3);
        output.Write(area);
        var footer = new byte[26];
        BinaryPrimitives.WriteUInt32LittleEndian(footer, checked((uint)extension));
        Signature.CopyTo(footer, 8);
        output.Write(footer);
    }
}
