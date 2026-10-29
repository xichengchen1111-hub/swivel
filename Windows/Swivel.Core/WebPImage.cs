using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;

namespace Swivel.Core;

/// Still WebP images, read by Swivel's own bounded decoder (never an installed codec): the RIFF
/// container with a simple lossy ('VP8 '), simple lossless ('VP8L') or extended ('VP8X') layout,
/// and the alpha chunk ('ALPH') of extended lossy files. Written from the public specifications
/// (RFC 9649 for the container, lossless format and alpha; RFC 6386 for the lossy key frame);
/// no third-party code. Animation is refused; ICC profiles, EXIF and XMP are not used.
public static class WebPImage
{
    public const string Invalid = "The WebP data is invalid or incomplete.";
    public const string Animated = "Animated WebP images are not supported. Save one frame as PNG, or use a still WebP.";
    public const string TooComplex = "This WebP uses more prefix codes than Swivel decodes. Save it again with another encoder, or as PNG.";
    public const string ReadNotice = "Swivel reads still WebP pictures, lossy or lossless, with their transparency; an embedded colour profile, EXIF orientation and other metadata are not applied.";

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
        token.ThrowIfCancellationRequested();
        if (file.Length > ImageRules.MaxInputBytes) throw new UserError("Image files are limited to 128 MB.");
        if (file.Length < 20 || !Tag(file, 0, "RIFF") || !Tag(file, 8, "WEBP")) throw new UserError(Invalid);
        long riffEnd = 8L + BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4));
        // Bytes after the RIFF chunk are ignored; a RIFF chunk longer than the file is truncated.
        if (riffEnd > file.Length || riffEnd < 20) throw new UserError(Invalid);
        int end = (int)riffEnd, at = 12;
        var first = Chunk(file, ref at, end);
        if (first.Tag == "VP8 ") return Lossy(file.AsSpan(first.Offset, first.Length), null, 0, 0, token);
        if (first.Tag == "VP8L") return Lossless(file.AsSpan(first.Offset, first.Length), 0, 0, token);
        if (first.Tag != "VP8X" || first.Length < 10) throw new UserError(Invalid);
        int flags = file[first.Offset];
        if ((flags & 0x02) != 0) throw new UserError(Animated);
        int canvasWidth = (int)(Uint24(file, first.Offset + 4) + 1), canvasHeight = (int)(Uint24(file, first.Offset + 7) + 1);
        ImageRules.Validate(canvasWidth, canvasHeight);
        (int Offset, int Length)? alpha = null;
        while (at < end)
        {
            var chunk = Chunk(file, ref at, end);
            switch (chunk.Tag)
            {
                case "ANIM" or "ANMF": throw new UserError(Animated);
                case "ALPH": alpha ??= (chunk.Offset, chunk.Length); break;
                case "VP8 ": return Lossy(file.AsSpan(chunk.Offset, chunk.Length), alpha is { } a ? file.AsSpan(a.Offset, a.Length).ToArray() : null, canvasWidth, canvasHeight, token);
                case "VP8L": return Lossless(file.AsSpan(chunk.Offset, chunk.Length), canvasWidth, canvasHeight, token);
                // ICCP, EXIF, XMP and unknown chunks are skipped.
            }
        }
        throw new UserError(Invalid);
    }

    private static PixelImage Lossless(ReadOnlySpan<byte> chunk, int canvasWidth, int canvasHeight, CancellationToken token)
    {
        var argb = WebPLossless.Read(chunk, out int width, out int height, token);
        if (canvasWidth != 0 && (width != canvasWidth || height != canvasHeight)) throw new UserError(Invalid);
        var bgra = new byte[argb.LongLength * 4];
        for (long i = 0; i < argb.LongLength; i++)
        {
            if ((i & 0xFFFF) == 0) token.ThrowIfCancellationRequested();
            BinaryPrimitives.WriteUInt32LittleEndian(bgra.AsSpan((int)(i * 4)), argb[i]);
        }
        return new PixelImage(width, height, bgra);
    }

    private static PixelImage Lossy(ReadOnlySpan<byte> chunk, byte[]? alpha, int canvasWidth, int canvasHeight, CancellationToken token)
    {
        var image = WebPLossy.Read(chunk, token);
        if (canvasWidth != 0 && (image.Width != canvasWidth || image.Height != canvasHeight)) throw new UserError(Invalid);
        if (alpha is not null)
        {
            var values = Alpha(alpha, image.Width, image.Height, token);
            for (long i = 0; i < values.LongLength; i++) image.Bgra[i * 4 + 3] = values[i];
        }
        return image;
    }

    /// The 'ALPH' chunk: one header byte (pre-processing, filtering, compression), then raw or
    /// lossless-coded values (green channel), then the filter is undone.
    internal static byte[] Alpha(byte[] chunk, int width, int height, CancellationToken token)
    {
        if (chunk.Length < 1) throw new UserError(Invalid);
        int filter = chunk[0] >> 2 & 3, compression = chunk[0] & 3;
        long count = (long)width * height;
        var values = new byte[count];
        if (compression == 0)
        {
            if (chunk.Length - 1 < count) throw new UserError(Invalid);
            Buffer.BlockCopy(chunk, 1, values, 0, (int)count);
        }
        else if (compression == 1)
        {
            var argb = WebPLossless.ReadHeaderless(chunk[1..], width, height, token);
            for (long i = 0; i < count; i++) values[i] = (byte)(argb[i] >> 8);
        }
        else throw new UserError(Invalid);
        if (filter == 0) return values;
        for (int y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                long at = (long)y * width + x;
                int predictor;
                if (x == 0 && y == 0) predictor = 0;
                else if (y == 0) predictor = values[at - 1];
                else if (x == 0) predictor = values[at - width];
                else predictor = filter switch
                {
                    1 => values[at - 1],
                    2 => values[at - width],
                    _ => Math.Clamp(values[at - 1] + values[at - width] - values[at - width - 1], 0, 255)
                };
                values[at] = (byte)(values[at] + predictor);
            }
        }
        return values;
    }

    private static bool Tag(byte[] file, int at, string tag)
    {
        for (int i = 0; i < 4; i++) if (file[at + i] != tag[i]) return false;
        return true;
    }

    private static uint Uint24(byte[] file, int at) => (uint)(file[at] | file[at + 1] << 8 | file[at + 2] << 16);

    private static (string Tag, int Offset, int Length) Chunk(byte[] file, ref int at, int end)
    {
        if (end - at < 8) throw new UserError(Invalid);
        string tag = System.Text.Encoding.ASCII.GetString(file, at, 4);
        long length = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at + 4));
        int offset = at + 8;
        if (length > end - offset) throw new UserError(Invalid);
        long next = offset + length + (length & 1);
        at = (int)Math.Min(next, end);
        return (tag, offset, (int)length);
    }
}
