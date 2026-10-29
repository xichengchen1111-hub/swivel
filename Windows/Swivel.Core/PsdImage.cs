using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Swivel.Core;

/// Photoshop PSD images, as Mac 1.14.x reads and writes them. Reading takes only the flattened
/// picture Photoshop stores after the layers (8-bit RGB or greyscale, raw or PackBits); layers,
/// text and effects are not rendered. Writing stores one flattened 8-bit RGB picture, plus one
/// normal layer holding the same pixels when the picture has transparency, so Photoshop shows
/// it. An original implementation of Adobe's published PSD layout; no third-party code.
public static class PsdImage
{
    public const string Notice = "PSD saves one flattened 8-bit RGB picture (with a single layer when it has transparency), without a colour profile. Photoshop and other editors open it; nothing else of the original is kept.";
    public const string ReadNotice = "Swivel reads a PSD's flattened picture, the one Photoshop saves with Maximize Compatibility; layers, text and effects are not rendered separately, and an embedded colour profile is not applied.";
    public const string Invalid = "The PSD data is invalid or incomplete.";
    public const string NoComposite = "This PSD was saved without Maximize Compatibility, so it holds no flattened picture. Save it again with Maximize Compatibility on, or as PNG.";
    public const string Unsupported = "This PSD is not supported. Swivel reads 8-bit RGB and greyscale PSD files stored raw or with PackBits; save a copy in that form, or as PNG.";
    private static readonly byte[] Layer = "8BIMnorm"u8.ToArray();

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
        if (file.Length < 26 || !file.AsSpan(0, 4).SequenceEqual("8BPS"u8)) throw new UserError(Invalid);
        int version = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(4));
        if (version == 2) throw new UserError(Unsupported);
        if (version != 1) throw new UserError(Invalid);
        int channels = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(12));
        long height = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(14)), width = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(18));
        int depth = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(22)), mode = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(24));
        if (channels is < 1 or > 56 || width is < 1 or > 30_000 || height is < 1 or > 30_000) throw new UserError(Invalid);
        int colours = mode switch { 3 => 3, 1 => 1, _ => 0 };
        if (depth != 8 || colours == 0 || channels < colours) throw new UserError(Unsupported);
        ImageRules.Validate((int)width, (int)height);
        long offset = 26;
        long Section()
        {
            if (offset + 4 > file.Length) throw new UserError(Invalid);
            long length = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan((int)offset));
            long start = offset + 4;
            if (start + length > file.Length) throw new UserError(Invalid);
            offset = start + length;
            return start;
        }
        Section();
        long resources = Section();
        var (photoshop, merged) = VersionInfo(file, resources, offset);
        // A negative layer count means the first extra channel is the flattened picture's transparency.
        long layers = Section(), layersEnd = offset;
        bool mergedAlpha = false;
        if (offset - layers >= 10 && BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan((int)layers)) >= 2)
            mergedAlpha = BinaryPrimitives.ReadInt16BigEndian(file.AsSpan((int)layers + 4)) < 0;
        bool alpha = mergedAlpha && channels > colours;
        if (offset + 2 > file.Length) throw new UserError(Invalid);
        int compression = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan((int)offset));
        offset += 2;
        if (compression is 2 or 3) throw new UserError(Unsupported);
        if (compression > 1) throw new UserError(Invalid);
        int w = (int)width, h = (int)height, wanted = colours + (alpha ? 1 : 0);
        var planes = new byte[wanted][];
        if (compression == 0)
        {
            long plane = (long)w * h;
            if (offset + plane * channels > file.Length) throw new UserError(Invalid);
            for (int channel = 0; channel < wanted; channel++)
            {
                token.ThrowIfCancellationRequested();
                planes[channel] = file.AsSpan((int)(offset + plane * channel), (int)plane).ToArray();
            }
        }
        else
        {
            long counts = offset, data = offset + 2L * channels * h;
            if (data > file.Length) throw new UserError(Invalid);
            for (int channel = 0; channel < wanted; channel++)
            {
                var plane = planes[channel] = new byte[(long)w * h];
                for (int row = 0; row < h; row++)
                {
                    if ((row & 255) == 0) token.ThrowIfCancellationRequested();
                    int count = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan((int)(counts + 2L * (channel * h + row))));
                    if (data + count > file.Length) throw new UserError(Invalid);
                    Unpack(file.AsSpan((int)data, count), plane.AsSpan(row * w, w));
                    data += count;
                }
            }
        }
        // How a transparent flattened picture is stored differs between programs (Photoshop blends it
        // over white, macOS stores straight colours), so when the file is one plain full-size layer
        // whose transparency matches the flattened one, that layer's own straight colours are used.
        bool straight = false;
        if (SingleLayer(file, layers, layersEnd, w, h, colours, token) is { } layer && (!merged || alpha && layer[colours].AsSpan().SequenceEqual(planes[colours])))
        {
            planes = layer; straight = alpha = true;
        }
        // Photoshop saved without Maximize Compatibility stores a placeholder, not the picture.
        else if (!merged) throw new UserError(NoComposite);
        // Only a file Photoshop wrote (it carries its version information) is taken off white,
        // and only when every partly transparent colour could have been blended over white.
        bool white = photoshop;
        if (alpha && !straight && white)
            for (int pixel = 0; pixel < w * h && white; pixel++)
            {
                int a = planes[colours][pixel];
                if (a is 0 or 255) continue;
                for (int channel = 0; channel < colours; channel++) white &= planes[channel][pixel] >= 255 - a - 1;
            }
        var bgra = new byte[(long)w * h * 4];
        for (int index = 0, pixel = 0; pixel < w * h; pixel++, index += 4)
        {
            if ((pixel & 0xFFFF) == 0) token.ThrowIfCancellationRequested();
            byte r = planes[0][pixel], g = colours == 3 ? planes[1][pixel] : r, b = colours == 3 ? planes[2][pixel] : r;
            byte a = alpha ? planes[colours][pixel] : (byte)255;
            if (a == 0) r = g = b = 0;
            else if (a < 255 && !straight && white) { r = Unmatte(r, a); g = Unmatte(g, a); b = Unmatte(b, a); }
            bgra[index] = b; bgra[index + 1] = g; bgra[index + 2] = r; bgra[index + 3] = a;
        }
        return new PixelImage(w, h, bgra);
    }

    /// Image resource 1057 (version info), which Photoshop writes: whether it is there, and its
    /// "has real merged data" flag (true when absent). A damaged resource list is ignored.
    private static (bool Photoshop, bool Merged) VersionInfo(byte[] file, long start, long end)
    {
        long at = start;
        while (at + 12 <= end)
        {
            if (!file.AsSpan((int)at, 4).SequenceEqual("8BIM"u8)) break;
            int id = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan((int)at + 4));
            long name = (1 + file[at + 6] + 1) / 2 * 2, size = at + 6 + name + 4 <= end ? BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan((int)(at + 6 + name))) : -1;
            long data = at + 6 + name + 4;
            if (size < 0 || data + size > end) break;
            if (id == 1057 && size >= 5) return (true, file[data + 4] != 0);
            at = data + (size + 1) / 2 * 2;
        }
        return (false, true);
    }

    private static readonly string[] PlainLayerKeys = { "luni", "lyid", "lnsr", "clbl", "infx", "knko", "lspf", "lclr", "shmd", "fxrp", "cust", "tsly" };

    /// The planes (colours, then transparency) of a file holding exactly one visible, normal,
    /// fully opaque, unmasked layer covering the canvas with only naming and bookkeeping extras
    /// (no effects, text or smart objects); null for anything else.
    private static byte[][]? SingleLayer(byte[] file, long start, long end, int w, int h, int colours, CancellationToken token)
    {
        try
        {
            var section = file.AsSpan((int)start, (int)(end - start));
            if (section.Length < 6) return null;
            long info = BinaryPrimitives.ReadUInt32BigEndian(section);
            if (info + 4 > section.Length || BinaryPrimitives.ReadInt16BigEndian(section[4..]) != -1) return null;
            var record = section[..(int)(4 + info)];
            int at = 6;
            if (BinaryPrimitives.ReadInt32BigEndian(record[at..]) != 0 || BinaryPrimitives.ReadInt32BigEndian(record[(at + 4)..]) != 0
                || BinaryPrimitives.ReadInt32BigEndian(record[(at + 8)..]) != h || BinaryPrimitives.ReadInt32BigEndian(record[(at + 12)..]) != w) return null;
            at += 16;
            int count = BinaryPrimitives.ReadUInt16BigEndian(record[at..]); at += 2;
            if (count != colours + 1) return null;
            var ids = new int[count]; var lengths = new long[count];
            for (int index = 0; index < count; index++)
            {
                ids[index] = BinaryPrimitives.ReadInt16BigEndian(record[at..]); lengths[index] = BinaryPrimitives.ReadUInt32BigEndian(record[(at + 2)..]); at += 6;
                if (ids[index] < -1 || ids[index] >= colours || Array.IndexOf(ids, ids[index], 0, index) >= 0) return null;
            }
            if (at + 16 > record.Length || !record.Slice(at, 8).SequenceEqual(Layer) || record[at + 8] != 255 || record[at + 9] != 0 || (record[at + 10] & 2) != 0) return null;
            at += 12;
            long extra = BinaryPrimitives.ReadUInt32BigEndian(record[at..]); at += 4;
            var extras = record.Slice(at, (int)extra); at += (int)extra;
            if (extras.Length < 9 || BinaryPrimitives.ReadUInt32BigEndian(extras) != 0) return null;
            long ranges = BinaryPrimitives.ReadUInt32BigEndian(extras[4..]);
            if (8 + ranges >= extras.Length) return null;
            int e = 8 + (int)ranges;
            e += (1 + extras[e] + 3) / 4 * 4;
            while (e < extras.Length)
            {
                if (e + 12 > extras.Length || !(extras.Slice(e, 4).SequenceEqual("8BIM"u8) || extras.Slice(e, 4).SequenceEqual("8B64"u8))) return null;
                string key = System.Text.Encoding.ASCII.GetString(extras.Slice(e + 4, 4));
                long length = BinaryPrimitives.ReadUInt32BigEndian(extras[(e + 8)..]);
                if (Array.IndexOf(PlainLayerKeys, key) < 0 || e + 12 + length > extras.Length) return null;
                e += 12 + (int)length;
            }
            var planes = new byte[colours + 1][];
            for (int index = 0; index < count; index++)
            {
                token.ThrowIfCancellationRequested();
                var data = record.Slice(at, (int)lengths[index]); at += (int)lengths[index];
                var plane = new byte[(long)w * h];
                int compression = BinaryPrimitives.ReadUInt16BigEndian(data);
                if (compression == 0) { if (data.Length - 2 != plane.Length) return null; data[2..].CopyTo(plane); }
                else if (compression == 1)
                {
                    int rows = 2 + 2 * h;
                    for (int row = 0; row < h; row++)
                    {
                        if ((row & 255) == 0) token.ThrowIfCancellationRequested();
                        int size = BinaryPrimitives.ReadUInt16BigEndian(data[(2 + 2 * row)..]);
                        Unpack(data.Slice(rows, size), plane.AsSpan(row * w, w)); rows += size;
                    }
                    if (rows != data.Length) return null;
                }
                else return null;
                planes[ids[index] == -1 ? colours : ids[index]] = plane;
            }
            return planes;
        }
        catch (Exception error) when (error is ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException or UserError) { return null; }
    }

    private static byte Unmatte(byte value, byte alpha) =>
        alpha == 0 ? (byte)0 : (byte)Math.Clamp(((value - 255 + alpha) * 255 + alpha / 2) / alpha, 0, 255);

    private static byte Matte(byte value, byte alpha) => (byte)((value * alpha + 255 * (255 - alpha) + 127) / 255);

    /// PackBits: a row must come out exactly the width long.
    private static void Unpack(ReadOnlySpan<byte> packed, Span<byte> row)
    {
        int at = 0, written = 0;
        while (written < row.Length)
        {
            if (at >= packed.Length) throw new UserError(Invalid);
            int header = (sbyte)packed[at++];
            if (header == -128) continue;
            if (header >= 0)
            {
                int count = header + 1;
                if (at + count > packed.Length || written + count > row.Length) throw new UserError(Invalid);
                packed.Slice(at, count).CopyTo(row[written..]); at += count; written += count;
            }
            else
            {
                int count = 1 - header;
                if (at >= packed.Length || written + count > row.Length) throw new UserError(Invalid);
                row.Slice(written, count).Fill(packed[at++]); written += count;
            }
        }
    }

    /// PackBits one row into `output`; returns the length. Repeats start at three equal bytes, so a
    /// row never takes more than width + width / 128 + 1 bytes.
    private static int Pack(ReadOnlySpan<byte> row, byte[] output)
    {
        int at = 0, length = 0;
        while (at < row.Length)
        {
            int run = 1;
            while (at + run < row.Length && run < 128 && row[at + run] == row[at]) run++;
            if (run >= 3) { output[length++] = (byte)(sbyte)(1 - run); output[length++] = row[at]; at += run; continue; }
            int start = at;
            while (at < row.Length && at - start < 128 && !(at + 2 < row.Length && row[at + 1] == row[at] && row[at + 2] == row[at])) at++;
            output[length++] = (byte)(at - start - 1);
            row[start..at].CopyTo(output.AsSpan(length)); length += at - start;
        }
        return length;
    }

    /// PackBits-compressed channels in order: per-row byte counts, then the rows.
    private static byte[] Channels(IReadOnlyList<byte[]> planes, int width, int height, CancellationToken token)
    {
        var counts = new byte[2 * planes.Count * height];
        var data = new MemoryStream();
        var row = new byte[width + width / 128 + 2];
        for (int channel = 0; channel < planes.Count; channel++)
            for (int line = 0; line < height; line++)
            {
                if ((line & 255) == 0) token.ThrowIfCancellationRequested();
                int length = Pack(planes[channel].AsSpan(line * width, width), row);
                data.Write(row, 0, length);
                BinaryPrimitives.WriteUInt16BigEndian(counts.AsSpan(2 * (channel * height + line)), checked((ushort)length));
            }
        var result = new byte[counts.Length + data.Length];
        counts.CopyTo(result, 0); data.GetBuffer().AsSpan(0, (int)data.Length).CopyTo(result.AsSpan(counts.Length));
        return result;
    }

    public static void Write(PixelImage image, Stream output, CancellationToken token = default)
    {
        int w = image.Width, h = image.Height;
        ImageRules.Validate(w, h);
        if (image.Bgra.Length != w * h * 4) throw new ArgumentException("Bad pixels.", nameof(image));
        bool opaque = true;
        for (int index = 3; index < image.Bgra.Length && opaque; index += 4) opaque = image.Bgra[index] == 255;
        byte[] Plane(int channel, bool matte)
        {
            var plane = new byte[w * h];
            for (int pixel = 0; pixel < plane.Length; pixel++)
            {
                byte value = image.Bgra[pixel * 4 + channel];
                plane[pixel] = matte ? Matte(value, image.Bgra[pixel * 4 + 3]) : value;
            }
            return plane;
        }
        var header = new byte[26];
        "8BPS"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(12), (ushort)(opaque ? 3 : 4));
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(14), (uint)h);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(18), (uint)w);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(22), 8);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(24), 3);
        output.Write(header);
        var number = new byte[4];
        void U32(long value) { BinaryPrimitives.WriteUInt32BigEndian(number, checked((uint)value)); output.Write(number); }
        U32(0); U32(0); // colour mode data, image resources
        if (opaque) U32(0);
        else
        {
            // One normal layer the size of the picture: channels -1 (transparency), 0, 1, 2 (red, green, blue).
            int[] order = { 3, 2, 1, 0 };
            var data = new byte[4][];
            for (int index = 0; index < 4; index++) data[index] = Channels(new[] { Plane(order[index], false) }, w, h, token);
            var record = new MemoryStream();
            void Put(ReadOnlySpan<byte> bytes) => record.Write(bytes);
            var field = new byte[4];
            void Int(int value) { BinaryPrimitives.WriteInt32BigEndian(field, value); Put(field); }
            void Short(short value) { BinaryPrimitives.WriteInt16BigEndian(field, value); Put(field.AsSpan(0, 2)); }
            Short(-1);
            Int(0); Int(0); Int(h); Int(w);
            Short(4);
            for (int index = 0; index < 4; index++) { Short((short)(index - 1)); Int(checked(2 + data[index].Length)); }
            Put(Layer);
            Put(new byte[] { 255, 0, 0, 0 });
            Int(16); Int(0); Int(0);
            Put("\u0007Layer 1"u8);
            foreach (var channel in data) { Short(1); Put(channel); }
            if (record.Length % 2 == 1) record.WriteByte(0);
            U32(4 + record.Length + 4);
            U32(record.Length);
            record.Position = 0; record.CopyTo(output);
            U32(0); // global layer mask
        }
        output.Write(new byte[] { 0, 1 });
        var merged = new List<byte[]> { Plane(2, !opaque), Plane(1, !opaque), Plane(0, !opaque) };
        if (!opaque) merged.Add(Plane(3, false));
        output.Write(Channels(merged, w, h, token));
    }
}
