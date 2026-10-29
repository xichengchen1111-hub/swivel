using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Swivel.Core;

internal static class TgaPsdChecks
{
    private static PixelImage Image(int width, int height, Func<int, int, (byte B, byte G, byte R, byte A)> pixel)
    {
        var bytes = new byte[width * height * 4];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) { var p = pixel(x, y); int i = (y * width + x) * 4; bytes[i] = p.B; bytes[i + 1] = p.G; bytes[i + 2] = p.R; bytes[i + 3] = p.A; }
        return new PixelImage(width, height, bytes);
    }

    private static byte[] Tga(int type, int width, int height, int depth, int descriptor, byte[] body, byte[]? map = null, int mapFirst = 0, int mapBits = 24, byte[]? tail = null)
    {
        var header = new byte[18];
        header[1] = (byte)(map is null ? 0 : 1); header[2] = (byte)type;
        if (map is not null) { BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(3), (ushort)mapFirst); BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(5), (ushort)(map.Length / (mapBits / 8))); header[7] = (byte)mapBits; }
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), (ushort)width); BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), (ushort)height);
        header[16] = (byte)depth; header[17] = (byte)descriptor;
        return header.Concat(map ?? Array.Empty<byte>()).Concat(body).Concat(tail ?? Array.Empty<byte>()).ToArray();
    }

    /// A TGA 2.0 extension area with the given attributes type, then the footer pointing at it.
    private static byte[] Footer(int offset, byte attributes)
    {
        var area = new byte[495]; BinaryPrimitives.WriteUInt16LittleEndian(area, 495); area[494] = attributes;
        var footer = new byte[26]; BinaryPrimitives.WriteUInt32LittleEndian(footer, (uint)offset); "TRUEVISION-XFILE.\0"u8.CopyTo(footer.AsSpan(8));
        return area.Concat(footer).ToArray();
    }

    private static PixelImage ReadTga(byte[] file) => TgaImage.Read(file, default);
    private static PixelImage ReadPsd(byte[] file) => PsdImage.Read(file, default);
    private static string Hex(PixelImage image) => Convert.ToHexString(image.Bgra);

    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        // ---- TGA reading ----
        // 2×2 true-colour, bottom-left origin (descriptor 0): the first stored row is the bottom row.
        var bottomUp = ReadTga(Tga(2, 2, 2, 24, 0, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }));
        check(Hex(bottomUp) == "070809FF0A0B0CFF010203FF040506FF", "A bottom-left TGA is read with its first stored row at the bottom");
        var topRight = ReadTga(Tga(2, 2, 1, 24, 0x30, new byte[] { 1, 2, 3, 4, 5, 6 }));
        check(Hex(topRight) == "040506FF010203FF", "Right-to-left order is honoured");
        // 32-bit with real alpha (descriptor alpha bits 8) and a run-length packet that crosses a row.
        var rle = ReadTga(Tga(10, 3, 2, 32, 0x28, new byte[] { 0x83, 10, 20, 30, 40, 0x01, 1, 2, 3, 4, 5, 6, 7, 8 }));
        check(Hex(rle) == "0A141E280A141E280A141E28" + "0A141E280102030405060708", "A run-length TGA whose repeat packet crosses rows decodes in stream order");
        // Colour-mapped with a first index of 5 and greyscale with and without alpha.
        var mapped = ReadTga(Tga(1, 2, 1, 8, 0x20, new byte[] { 5, 6 }, map: new byte[] { 9, 8, 7, 1, 2, 3 }, mapFirst: 5));
        check(Hex(mapped) == "090807FF010203FF", "A colour-mapped TGA honours the map's first index");
        reject(() => ReadTga(Tga(1, 1, 1, 8, 0x20, new byte[] { 4 }, map: new byte[] { 9, 8, 7 }, mapFirst: 5)), "An index before the colour map's first entry is refused");
        reject(() => ReadTga(Tga(1, 1, 1, 8, 0x20, new byte[] { 6 }, map: new byte[] { 9, 8, 7 }, mapFirst: 5)), "An index past the colour map is refused");
        check(Hex(ReadTga(Tga(3, 2, 1, 8, 0x20, new byte[] { 0, 200 }))) == "000000FFC8C8C8FF", "An 8-bit greyscale TGA is grey and opaque");
        check(Hex(ReadTga(Tga(11, 2, 1, 16, 0x28, new byte[] { 0x81, 100, 50 }))) == "64646432" + "64646432", "A run-length greyscale TGA with alpha keeps its alpha");
        // 16-bit 5-5-5 with an attribute bit.
        check(Hex(ReadTga(Tga(2, 2, 1, 16, 0x21, new byte[] { 0x1F, 0x80, 0xE0, 0x03 }))) == "FF0000FF" + "00FF0000", "A 16-bit TGA expands 5-bit channels and reads the attribute bit as alpha");
        check(Hex(ReadTga(Tga(2, 1, 1, 16, 0x20, new byte[] { 0x00, 0x7C }))) == "0000FFFF", "A 16-bit TGA without attribute bits is opaque");
        // Alpha that is all zero without a TGA 2.0 attribute is read as opaque; with attribute 3 it stays.
        var zeroAlpha = new byte[] { 1, 2, 3, 0, 4, 5, 6, 0 };
        check(Hex(ReadTga(Tga(2, 2, 1, 32, 0x28, zeroAlpha))) == "010203FF040506FF", "An all-zero alpha channel without a TGA 2.0 attribute is read as opaque");
        check(Hex(ReadTga(Tga(2, 2, 1, 32, 0x28, zeroAlpha, tail: Footer(18 + 8, 3)))) == "0102030004050600", "Attributes type 3 keeps a fully transparent picture transparent");
        check(Hex(ReadTga(Tga(2, 2, 1, 32, 0x28, new byte[] { 1, 2, 3, 9, 4, 5, 6, 9 }, tail: Footer(18 + 8, 0)))) == "010203FF040506FF", "Attributes type 0 says the alpha byte is not transparency");
        check(Hex(ReadTga(Tga(2, 1, 1, 32, 0x28, new byte[] { 64, 32, 0, 128 }, tail: Footer(18 + 4, 4)))) == "80400080", "Attributes type 4 (premultiplied) is unpremultiplied");
        check(Hex(ReadTga(Tga(2, 1, 1, 32, 0x28, new byte[] { 1, 2, 3, 9 }, tail: Footer(9999, 0)))) == "01020309", "An extension offset outside the file is ignored rather than trusted");
        // Damaged and unsupported files.
        reject(() => ReadTga(Tga(2, 2, 2, 24, 0, new byte[11])), "A truncated true-colour TGA is refused");
        reject(() => ReadTga(Tga(10, 2, 1, 24, 0, new byte[] { 0x82, 1, 2, 3 })), "A run that overruns the picture is refused");
        reject(() => ReadTga(Tga(10, 2, 1, 24, 0, new byte[] { 0x01, 1, 2, 3 })), "A literal packet cut short is refused");
        reject(() => ReadTga(Tga(2, 1, 1, 24, 0x40, new byte[3])), "Interleaved TGA rows are refused");
        reject(() => ReadTga(Tga(32, 1, 1, 24, 0, new byte[3])), "Compressed (Huffman) TGA types are refused");
        reject(() => ReadTga(Tga(1, 1, 1, 8, 0, new byte[1])), "A colour-mapped TGA without a map is refused");
        reject(() => ReadTga(Tga(2, 17000, 1, 24, 0, new byte[17000 * 3])), "A TGA over 16384 pixels wide is refused");
        reject(() => ReadTga(new byte[10]), "A file shorter than the TGA header is refused");
        string? message = null;
        try { ReadTga(Tga(2, 1, 1, 8, 0, new byte[1])); } catch (UserError error) { message = error.Message; }
        check(message == TgaImage.Unsupported, "An 8-bit true-colour TGA gets the unsupported-layout message");

        // ---- TGA writing ----
        var opaque = Image(3, 2, (x, y) => ((byte)(x * 40), (byte)(y * 90), 7, 255));
        var stream = new MemoryStream(); TgaImage.Write(opaque, stream);
        byte[] written = stream.ToArray();
        check(written[2] == 2 && written[16] == 24 && written[17] == 0x20 && written.Length == 18 + 18 + 495 + 26 && written[18 + 18 + 494] == 0, "An opaque picture is written as uncompressed 24-bit, top-left, attributes type 0");
        check(Hex(ReadTga(written)) == Hex(opaque), "An opaque TGA reads back exactly");
        var clear = Image(2, 2, (x, y) => (1, 2, 3, (byte)(x == 0 && y == 0 ? 0 : 200)));
        stream = new MemoryStream(); TgaImage.Write(clear, stream); written = stream.ToArray();
        check(written[16] == 32 && written[17] == 0x28 && written[18 + 16 + 494] == 3 && Hex(ReadTga(written)) == Hex(clear), "A transparent picture is written as 32-bit with attributes type 3 and reads back exactly");
        check(written.AsSpan(written.Length - 18).SequenceEqual("TRUEVISION-XFILE.\0"u8) && BinaryPrimitives.ReadUInt32LittleEndian(written.AsSpan(written.Length - 26)) == 18 + 16, "The TGA 2.0 footer points at the extension area");
        var allClear = Image(2, 1, (x, y) => (5, 6, 7, 0));
        stream = new MemoryStream(); TgaImage.Write(allClear, stream);
        check(Hex(ReadTga(stream.ToArray())) == Hex(allClear), "A fully transparent picture stays transparent through Swivel's own TGA");

        // ---- PSD writing and reading ----
        var random = new Random(7);
        var noisy = Image(131, 3, (x, y) => (x < 70 ? (byte)9 : (byte)random.Next(256), (byte)random.Next(256), (byte)(y * 50), 255));
        stream = new MemoryStream(); PsdImage.Write(noisy, stream); written = stream.ToArray();
        check(written.AsSpan(0, 4).SequenceEqual("8BPS"u8) && BinaryPrimitives.ReadUInt16BigEndian(written.AsSpan(12)) == 3 && BinaryPrimitives.ReadUInt16BigEndian(written.AsSpan(24)) == 3,
            "An opaque picture is written as a 3-channel 8-bit RGB PSD");
        check(Hex(ReadPsd(written)) == Hex(noisy), "An opaque PSD with long runs and literal stretches reads back exactly");
        check(written.Length < 26 + 12 + 2 + 3 * 3 * 2 + 3 * 3 * 131, "PackBits keeps the opaque PSD smaller than raw");
        var one = Image(1, 1, (x, y) => (1, 2, 3, 255));
        stream = new MemoryStream(); PsdImage.Write(one, stream);
        check(Hex(ReadPsd(stream.ToArray())) == "010203FF", "A 1×1 PSD reads back");
        var glass = Image(4, 2, (x, y) => ((byte)(x * 60), 100, 200, (byte)(x == 0 ? 0 : x == 1 ? 128 : x == 2 ? 255 : 254)));
        stream = new MemoryStream(); PsdImage.Write(glass, stream); written = stream.ToArray();
        var back = ReadPsd(written);
        bool close = true;
        for (int i = 0; i < 8; i++)
        {
            int a = glass.Bgra[i * 4 + 3];
            close &= back.Bgra[i * 4 + 3] == a;
            for (int c = 0; c < 3; c++) close &= a == 0 ? back.Bgra[i * 4 + c] == 0 : Math.Abs(back.Bgra[i * 4 + c] - glass.Bgra[i * 4 + c]) <= 1;
        }
        check(BinaryPrimitives.ReadUInt16BigEndian(written.AsSpan(12)) == 4 && close, "A transparent PSD keeps alpha exactly and colour within one level (fully transparent reads as zero)");
        // The flattened picture is stored blended over white: a half-transparent pure red pixel is pink there.
        check(written.AsSpan().IndexOf("8BIMnorm"u8) > 0 && written.AsSpan().IndexOf("\u0007Layer 1"u8) > 0, "A transparent PSD carries one normal layer named Layer 1");

        // Hand-built PSD files.
        byte[] Psd(int channels, int width, int height, int depth, int mode, short? layerCount, int compression, byte[] data, int version = 1)
        {
            var file = new List<byte>();
            void U16(int v) { file.Add((byte)(v >> 8)); file.Add((byte)v); }
            void U32(long v) { U16((int)(v >> 16)); U16((int)(v & 0xFFFF)); }
            file.AddRange("8BPS"u8.ToArray()); U16(version); file.AddRange(new byte[6]); U16(channels); U32(height); U32(width); U16(depth); U16(mode);
            U32(0); U32(0);
            if (layerCount is { } count) { U32(10); U32(2); U16((ushort)count); U32(0); } else U32(0);
            U16(compression); file.AddRange(data);
            return file.ToArray();
        }
        var rawRgb = Psd(3, 2, 1, 8, 3, null, 0, new byte[] { 10, 20, 30, 40, 50, 60 });
        check(Hex(ReadPsd(rawRgb)) == "321E0AFF3C2814FF", "A raw RGB PSD is read plane by plane");
        check(Hex(ReadPsd(Psd(1, 2, 1, 8, 1, null, 0, new byte[] { 0, 99 }))) == "000000FF636363FF", "A raw greyscale PSD is read as grey");
        // A fourth channel is transparency only when the layer count is negative.
        var four = new byte[] { 255, 255, 255, 191, 191, 191, 127, 127, 127, 0, 128, 255 };
        check(Hex(ReadPsd(Psd(4, 3, 1, 8, 3, 1, 0, four))) == "7FBFFFFF7FBFFFFF7FBFFFFF", "With a positive layer count an extra channel is a saved selection, not transparency");
        var unmatted = ReadPsd(Psd(4, 3, 1, 8, 3, -1, 0, four));
        check(Hex(unmatted) == "00000000" + "0080FF80" + "7FBFFFFF", "With a negative layer count the extra channel is transparency and colour is taken off the white background");
        // Colours below 255 − alpha cannot have been blended over white: premultiplied (as macOS writes) is undone instead,
        // and colours that fit neither blend are kept as stored.
        check(Hex(ReadPsd(Psd(4, 2, 1, 8, 3, -1, 0, new byte[] { 64, 0, 32, 0, 0, 0, 128, 0 }))) == "00408080" + "00000000", "A premultiplied flattened picture is unpremultiplied");
        check(Hex(ReadPsd(Psd(4, 1, 1, 8, 3, -1, 0, new byte[] { 200, 10, 0, 128 }))) == "000AC880", "Colours that fit neither blend are taken as stored");
        // Swivel's own transparent PSD reads back exactly through its single plain layer, even at alpha 1.
        var faint = Image(3, 1, (x, y) => (40, 0, (byte)(x * 100), (byte)(x + 1)));
        stream = new MemoryStream(); PsdImage.Write(faint, stream);
        check(Hex(ReadPsd(stream.ToArray())) == Hex(faint), "A nearly transparent picture survives Swivel's own PSD exactly, read from its plain layer");
        var hidden = stream.ToArray(); int flags = hidden.AsSpan().IndexOf("8BIMnorm"u8) + 10; hidden[flags] |= 2;
        var fromComposite = ReadPsd(hidden);
        check(fromComposite.Bgra.Where((_, i) => i % 4 == 3).SequenceEqual(faint.Bgra.Where((_, i) => i % 4 == 3)) && Hex(fromComposite) != Hex(faint), "A hidden layer is not used; the flattened picture (blended over white) is read instead");
        // PackBits rows: counts first, then data.
        byte[] packed = { 0, 2, 0, 2, 0, 2, 0xFE, 7, 0xFE, 8, 0xFE, 9 };
        check(Hex(ReadPsd(Psd(3, 3, 1, 8, 3, null, 1, packed))) == "090807FF090807FF090807FF", "A PackBits RGB PSD decodes each row");
        reject(() => ReadPsd(Psd(3, 2, 1, 8, 3, null, 1, new byte[] { 0, 2, 0, 2, 0, 2, 0xFD, 7, 0xFE, 8, 0xFE, 9 })), "A PackBits row that comes out too long is refused");
        reject(() => ReadPsd(Psd(3, 3, 1, 8, 3, null, 1, new byte[] { 0, 2, 0, 3, 0, 3, 0xFF, 7, 0xFE, 8, 0xFE, 9 })), "A PackBits row that comes out too short is refused");
        reject(() => ReadPsd(Psd(3, 2, 1, 8, 3, null, 0, new byte[5])), "A truncated raw PSD is refused");
        reject(() => ReadPsd(Psd(3, 2, 1, 8, 3, null, 1, new byte[] { 0, 2, 0, 2 })), "A truncated PackBits PSD is refused");
        string? Error(byte[] file) { try { ReadPsd(file); return null; } catch (UserError error) { return error.Message; } }
        check(Error(Psd(3, 1, 1, 16, 3, null, 0, new byte[6])) == PsdImage.Unsupported, "A 16-bit PSD is refused as unsupported");
        check(Error(Psd(4, 1, 1, 8, 4, null, 0, new byte[4])) == PsdImage.Unsupported, "A CMYK PSD is refused as unsupported");
        check(Error(Psd(3, 1, 1, 8, 3, null, 2, new byte[16])) == PsdImage.Unsupported, "A ZIP-compressed PSD is refused as unsupported");
        check(Error(Psd(3, 1, 1, 8, 3, null, 0, new byte[3], version: 2)) == PsdImage.Unsupported, "A large-document PSB is refused as unsupported");
        check(Error(Psd(3, 1, 1, 8, 3, null, 0, new byte[3], version: 3)) == PsdImage.Invalid, "An unknown PSD version is invalid");
        check(Error(Psd(2, 1, 1, 8, 3, null, 0, new byte[2])) == PsdImage.Unsupported, "An RGB PSD with fewer than three channels is refused");
        check(Error(Psd(3, 20000, 1, 8, 3, null, 0, new byte[60000])) == "Images are limited to 16 megapixels and 16384 pixels per side.", "A PSD over 16384 pixels wide is refused");
        var badSection = Psd(3, 1, 1, 8, 3, null, 0, new byte[3]); badSection[29] = 0xFF;
        reject(() => ReadPsd(badSection), "A section length past the end of the file is refused");

        // Catalog and agent wiring.
        check(Catalog.IsImage("a.TGA") && Catalog.IsImage("b.psd") && ImageOutputInfo.Format("convert.tga") == "tga" && ImageOutputInfo.Format("convert.psd") == "psd"
            && Catalog.Available(new Prefs(), new[] { "a.tga", "b.psd" }).Any(x => x.Id == "convert.png") && Catalog.Available(new Prefs(), new[] { "a.png" }).Count(x => x.Id is "convert.tga" or "convert.psd") == 2,
            "TGA and PSD are images that convert to PNG, and any image can become TGA or PSD");
        check(McpSession.Actions.Any(a => a.Id == "convert.tga") && McpSession.Actions.Any(a => a.Id == "convert.psd" && a.Accepts("x.png") && a.Accepts("y.PSD")), "Agents can convert to and from TGA and PSD");
    }
}
