using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using Swivel.Core;

internal static class IcoFileChecks
{
    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        // Which sizes a picture gets: every one up to its longer side, 16 at least.
        check(IcoFile.SizesFor(1000, 20).SequenceEqual(IcoFile.Sizes), "A large picture gets all six icon sizes");
        check(IcoFile.SizesFor(100, 60).SequenceEqual(new[] { 16, 32, 48, 64 }), "A 100×60 picture gets 16–64, never enlarged past its longer side");
        check(IcoFile.SizesFor(5, 3).SequenceEqual(new[] { 16 }) && IcoFile.SizesFor(256, 256).Last() == 256 && IcoFile.SizesFor(255, 1).Last() == 128, "A tiny picture still gets 16; 256 only from 256 pixels");
        reject(() => IcoFile.SizesFor(0, 5), "An empty picture has no icon sizes");

        // The longer side fills the square; the other keeps the proportion, at least one pixel.
        check(IcoFile.Fit(400, 200, 32) == (32, 16) && IcoFile.Fit(200, 400, 32) == (16, 32) && IcoFile.Fit(300, 300, 48) == (48, 48), "Fit keeps the proportion");
        check(IcoFile.Fit(1000, 1, 16) == (16, 1) && IcoFile.Fit(30, 10, 16) == (16, 5) && IcoFile.Fit(5, 3, 16) == (16, 10), "Fit rounds half up and never makes a side zero");

        // Centred on a transparent square.
        var red = new PixelImage(4, 2, Enumerable.Repeat(new byte[] { 0, 0, 255, 255 }, 8).SelectMany(p => p).ToArray());
        var square = IcoFile.Square(red, 6);
        bool Opaque(int x, int y) => square.Bgra[(y * 6 + x) * 4 + 3] == 255;
        check(square.Width == 6 && Enumerable.Range(0, 36).Count(i => square.Bgra[i * 4 + 3] == 255) == 8 && Opaque(1, 2) && Opaque(4, 3) && !Opaque(0, 2) && !Opaque(1, 1) && !Opaque(1, 4),
            "A 4×2 picture is centred on a 6×6 transparent square");
        var wide = IcoFile.Square(new PixelImage(5, 1, Enumerable.Range(0, 20).Select(i => (byte)i).ToArray()), 3);
        check(wide.Bgra.Skip(12).Take(12).SequenceEqual(Enumerable.Range(4, 12).Select(i => (byte)i)), "Anything wider than the square is clipped evenly");

        // A 32-bit bitmap: header, bottom-up rows, AND mask set where fully transparent.
        var pixels = new byte[3 * 3 * 4];
        for (int i = 0; i < 9; i++) { pixels[i * 4] = (byte)(10 * i); pixels[i * 4 + 3] = (byte)(i == 0 ? 0 : i == 4 ? 1 : 255); }
        byte[] bitmap = IcoFile.Bitmap(new PixelImage(3, 3, pixels));
        check(bitmap.Length == 40 + 36 + 12 && BinaryPrimitives.ReadInt32LittleEndian(bitmap.AsSpan(8)) == 6 && BinaryPrimitives.ReadUInt16LittleEndian(bitmap.AsSpan(14)) == 32
            && BinaryPrimitives.ReadInt32LittleEndian(bitmap.AsSpan(20)) == 48, "The icon bitmap header doubles the height and counts colour plus mask");
        check(bitmap[40] == 60 && bitmap[40 + 24] == 0 && bitmap[40 + 24 + 3] == 0, "Bitmap rows are stored bottom-up");
        check(bitmap[40 + 36] == 0 && bitmap[40 + 36 + 4] == 0 && bitmap[40 + 36 + 8] == 0x80 && bitmap.Skip(40 + 36).Count(b => b != 0) == 1,
            "The AND mask marks only the fully transparent pixel (top-left, in the last mask row); nearly transparent stays in");
        reject(() => IcoFile.Bitmap(new PixelImage(256, 256, new byte[256 * 256 * 4])), "256 is never stored as a bitmap");

        // Write then read back: directory, offsets, 256 as width 0, PNG entries recognised.
        byte[] fakePng = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3 };
        var b16 = IcoFile.Bitmap(IcoFile.Square(red, 16)); var b32 = IcoFile.Bitmap(IcoFile.Square(red, 32));
        using var memory = new MemoryStream();
        IcoFile.Write(memory, new[] { (16, b16), (32, b32), (256, fakePng) });
        byte[] file = memory.ToArray();
        var read = IcoFile.Read(file);
        check(file.Length == 6 + 48 + b16.Length + b32.Length + fakePng.Length && file[6 + 32] == 0 && file[6 + 33] == 0
            && read.Select(e => (e.Size, e.Png)).SequenceEqual(new[] { (16, false), (32, false), (256, true) }) && read[1].Data.SequenceEqual(b32) && read[2].Data.SequenceEqual(fakePng),
            "An icon file reads back with every size, its bytes and 256 stored as width 0");
        check(BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(6 + 12)) == 54 && BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(6 + 4)) == 1 && BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(6 + 6)) == 32,
            "The first image starts right after the directory, one plane, 32 bits");
        reject(() => IcoFile.Write(new MemoryStream(), Array.Empty<(int, byte[])>()), "An icon with no images is refused");
        var cut = file[..^1]; reject(() => IcoFile.Read(cut), "A cut-off icon file is refused");
        var lied = (byte[])file.Clone(); lied[6] = 17; reject(() => IcoFile.Read(lied), "An entry whose size disagrees with its bitmap is refused");
        reject(() => IcoFile.Write(new MemoryStream(), new[] { (16, b16) }, new System.Threading.CancellationToken(true)), "Writing an icon stops when cancelled");

        // Integrated with the Convert menu and the agent tools.
        check(ImageOutputInfo.Format("convert.ico") == "ico" && Catalog.Available(new Prefs(), new[] { "a.png" }).Any(a => a.Id == "convert.ico")
            && !Catalog.Available(new Prefs(), new[] { "a.mp4" }).Any(a => a.Id == "convert.ico"), "Convert to ICO is offered for images only");
        check(McpSession.Actions.Single(a => a.Id == "convert.ico").Extension == "ico", "The agent action names an .ico output");
        check(Strings.Table.ContainsKey("Convert to ICO") && Strings.Table.ContainsKey(IcoFile.Notice), "The ICO title and notice are translated");
    }
}
