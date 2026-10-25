using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Swivel.Core;

// Original static palette GIF writer. No quantization, animation or external codec.
// Format facts: CompuServe GIF specification (attribution in Windows/README.md).
public static class GifExport
{
    public const string Colors = "GIF export supports at most 256 palette entries, including one transparent entry. This image has too many colors. No color quantization or dithering is applied; choose PNG instead.";
    public const string Alpha = "GIF export supports only fully opaque or fully transparent pixels. Partial transparency is refused rather than flattened. Choose PNG to preserve it.";
    public const string Notice = "Static GIF preserves normalized 8-bit SDR RGB values only when the image fits 256 palette entries, including one transparent entry. Partial transparency and excess colors are refused; no quantization or dithering is applied. Hidden RGB under transparent pixels becomes zero. Animation, metadata and color profiles are not saved. Choose PNG for more colors or partial transparency.";
    public static void Write(PixelImage image, Stream output, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(image); ArgumentNullException.ThrowIfNull(output);
        image.Validate(); if (!output.CanWrite) throw new ArgumentException("A writable output is required.", nameof(output));
        // Preflight all pixels before the header. Indices are a stable snapshot for
        // encoding; caller mutation after preflight cannot change the output palette.
        int count = checked(image.Width * image.Height), transparent = -1;
        var indices = new byte[count]; var palette = new List<int>(256); var colors = new Dictionary<int, byte>(256);
        for (int i = 0; i < count; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            int offset = i * 4, alpha = image.Bgra[offset + 3];
            if (alpha is not 0 and not 255) throw new UserError(Alpha);
            int color = alpha == 0 ? -1 : image.Bgra[offset] | image.Bgra[offset + 1] << 8 | image.Bgra[offset + 2] << 16;
            if (!colors.TryGetValue(color, out byte index))
            {
                if (palette.Count == 256) throw new UserError(Colors);
                index = (byte)palette.Count; colors.Add(color, index); palette.Add(color);
                if (color == -1) transparent = index;
            }
            indices[i] = index;
        }
        token.ThrowIfCancellationRequested();
        int paletteBits = 1; while ((1 << paletteBits) < palette.Count) paletteBits++;
        Span<byte> header = stackalloc byte[13]; header.Clear(); "GIF89a"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], (ushort)image.Width); BinaryPrimitives.WriteUInt16LittleEndian(header[8..], (ushort)image.Height);
        header[10] = (byte)(0xf0 | (paletteBits - 1)); output.Write(header);
        for (int i = 0; i < (1 << paletteBits); i++)
        {
            int color = i < palette.Count && palette[i] >= 0 ? palette[i] : 0;
            output.WriteByte((byte)(color >> 16)); output.WriteByte((byte)(color >> 8)); output.WriteByte((byte)color);
        }
        if (transparent >= 0) output.Write(new byte[] { 0x21, 0xf9, 4, 1, 0, 0, (byte)transparent, 0 });
        Span<byte> descriptor = stackalloc byte[10]; descriptor.Clear(); descriptor[0] = 0x2c;
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[5..], (ushort)image.Width); BinaryPrimitives.WriteUInt16LittleEndian(descriptor[7..], (ushort)image.Height); output.Write(descriptor);
        int minimum = Math.Max(2, paletteBits); output.WriteByte((byte)minimum);
        Encode(indices, output, minimum, token); output.WriteByte(0x3b); token.ThrowIfCancellationRequested();
    }
    private static void Encode(byte[] indices, Stream output, int minimum, CancellationToken token)
    {
        int clear = 1 << minimum, end = clear + 1, next = end + 1;
        var dictionary = new Dictionary<int, int>(4096); var data = new Codes(output, minimum, token);
        data.Code(clear); int prefix = indices[0];
        for (int i = 1; i < indices.Length; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            int value = indices[i], key = (prefix << 8) | value;
            if (dictionary.TryGetValue(key, out int found)) { prefix = found; continue; }
            data.Code(prefix);
            if (next < 4096) dictionary.Add(key, next++);
            else { data.Code(clear); dictionary.Clear(); next = end + 1; }
            prefix = value;
        }
        data.Code(prefix); data.Code(end); data.Finish();
    }
    // Track the decoder's dictionary separately: the encoder learns its next
    // phrase one emitted code earlier, especially at 3→4 through11→12 bits.
    private sealed class Codes(Stream output, int minimum, CancellationToken token)
    {
        private readonly byte[] block = new byte[255];
        private uint bits;
        private int bitCount, used, size = minimum + 1, next = (1 << minimum) + 2;
        private bool previous;
        public void Code(int value)
        {
            token.ThrowIfCancellationRequested();
            bits |= (uint)value << bitCount; bitCount += size;
            while (bitCount >= 8) { Byte((byte)bits); bits >>= 8; bitCount -= 8; }
            int clear = 1 << minimum;
            if (value == clear) { size = minimum + 1; next = clear + 2; previous = false; }
            else if (value != clear + 1)
            {
                if (previous && next < 4096) { next++; if (next == (1 << size) && size < 12) size++; }
                previous = true;
            }
        }
        private void Byte(byte value) { block[used++] = value; if (used == 255) Flush(); }
        private void Flush() { token.ThrowIfCancellationRequested(); output.WriteByte((byte)used); output.Write(block.AsSpan(0, used)); used = 0; }
        public void Finish() { if (bitCount > 0) Byte((byte)bits); if (used > 0) Flush(); token.ThrowIfCancellationRequested(); output.WriteByte(0); }
    }
}
