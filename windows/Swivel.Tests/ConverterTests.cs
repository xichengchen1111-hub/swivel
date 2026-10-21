using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace Swivel.Tests;

/// Images, video/audio and archives (Converters/*.cs). Fixtures are generated at test time.
[TestClass]
public sealed class ConverterTests
{
    [TestInitialize]
    public void Reset() => TestSetup.ResetPrefs();

    // MARK: Fixtures

    const int Red = unchecked((int)0xFFFF0000), Blue = unchecked((int)0xFF0000FF), Green = unchecked((int)0xFF00FF00),
        White = unchecked((int)0xFFFFFFFF), Clear = 0;

    /// Left half red, right half blue, a green 8 × 8 block in the top-left corner.
    static int[] Pattern(int w, int h, bool transparentRight = false)
    {
        var data = new int[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                data[y * w + x] = x < 8 && y < 8 ? Green : x < w / 2 ? Red : transparentRight ? Clear : Blue;
        return data;
    }

    static BitmapSource Bitmap(int w, int h, int[] data, bool alpha)
    {
        var b = BitmapSource.Create(w, h, 96, 96, alpha ? PixelFormats.Bgra32 : PixelFormats.Bgr32, null, data, w * 4);
        b.Freeze();
        return b;
    }

    static string MakeImage(string dir, string name, int w = 80, int h = 40, bool transparentRight = false, ushort orientation = 1)
    {
        var path = Path.Combine(dir, name);
        var source = Bitmap(w, h, Pattern(w, h, transparentRight), transparentRight);
        BitmapEncoder encoder = Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".jpg" => new JpegBitmapEncoder { QualityLevel = 100 },
            ".jxr" => new WmpBitmapEncoder(),
            ".tif" or ".tiff" => new TiffBitmapEncoder(),
            _ => new PngBitmapEncoder(),
        };
        BitmapMetadata? metadata = null;
        if (orientation != 1)
        {
            metadata = new BitmapMetadata("jpg");
            metadata.SetQuery("/app1/ifd/{ushort=274}", orientation);
        }
        encoder.Frames.Add(BitmapFrame.Create(source, null, metadata, null));
        using var file = File.Create(path);
        encoder.Save(file);
        return path;
    }

    static int Pixel(BitmapSource image, int x, int y)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixel = new int[1];
        converted.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel[0];
    }

    static bool Near(int actual, int expected, int tolerance = 40)
    {
        for (int shift = 0; shift < 32; shift += 8)
            if (Math.Abs(((actual >> shift) & 0xFF) - ((expected >> shift) & 0xFF)) > tolerance) return false;
        return true;
    }

    static void AssertNear(int expected, int actual, string what, int tolerance = 40) =>
        Assert.IsTrue(Near(actual, expected, tolerance), $"{what}: expected {expected:X8}, got {actual:X8}");

    /// One second of a 440 Hz tone, 16-bit stereo 44.1 kHz.
    static string MakeWav(string dir, string name = "tone.wav", double seconds = 1, int channels = 2, int rate = 44100)
    {
        int frames = (int)(rate * seconds);
        var data = new byte[frames * channels * 2];
        for (int i = 0; i < frames; i++)
        {
            short v = (short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 12000);
            for (int c = 0; c < channels; c++) BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan((i * channels + c) * 2), v);
        }
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, AudioContainers.Wav(new AudioContainers.Pcm(channels, rate, 16, data)));
        return path;
    }

    static string[] Listing(string dir) => Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName).Order().ToArray()!;

    // MARK: Images

    [TestMethod]
    public void OutputFormatsAreListedInMacOrderAndAlwaysIncludeTheBuiltIns()
    {
        var names = Images.OutputFormats.Select(f => f.Name).ToList();
        foreach (var builtIn in new[] { "JPG", "PNG", "TIFF", "GIF", "BMP" }) CollectionAssert.Contains(names, builtIn);
        string[] macOrder = ["JPG", "PNG", "HEIC", "WEBP", "TIFF", "AVIF", "GIF", "BMP"];
        CollectionAssert.AreEqual(macOrder.Where(names.Contains).ToList(), names);
        // The convert menu hides the source's own format (and every alias of it), and always ends with PDF.
        var dir = TestSetup.NewFolder("formats");
        var png = MakeImage(dir, "a.png");
        var titles = Catalog.Items([png], WheelMode.Convert).Select(i => i.Title).ToList();
        CollectionAssert.DoesNotContain(titles, "PNG");
        CollectionAssert.Contains(titles, "JPG");
        Assert.AreEqual("PDF", titles[^1]);
        var jpeg = MakeImage(dir, "b.jpeg");
        CollectionAssert.DoesNotContain(Catalog.Items([jpeg], WheelMode.Convert).Select(i => i.Title).ToList(), "JPG");
    }

    [TestMethod]
    public async Task EveryOutputFormatRoundTrips()
    {
        var dir = TestSetup.NewFolder("roundtrip");
        var source = MakeImage(dir, "sample.png", 80, 40);
        foreach (var format in Images.OutputFormats)
        {
            var outputs = await Images.Convert(source, format);
            Assert.HasCount(1, outputs, format.Name);
            Assert.AreEqual(Path.Combine(dir, format.Name == "PNG" ? "sample 2.png" : "sample." + format.Ext), outputs[0], format.Name);
            var image = Images.Load(outputs[0]);
            Assert.AreEqual(80, image.PixelWidth, format.Name);
            Assert.AreEqual(40, image.PixelHeight, format.Name);
            int tolerance = format.Name is "PNG" or "TIFF" or "BMP" or "GIF" ? 2 : 60;
            AssertNear(Red, Pixel(image, 30, 20), format.Name + " left", tolerance);
            AssertNear(Blue, Pixel(image, 60, 20), format.Name + " right", tolerance);
            AssertNear(Green, Pixel(image, 2, 2), format.Name + " marker", tolerance + 20);
        }
        Assert.IsFalse(Directory.EnumerateFiles(dir, ".swivel-*").Any(), "temp files left behind");
    }

    [TestMethod]
    public void LoadAppliesExifOrientationAndCapsTheLongEdge()
    {
        var dir = TestSetup.NewFolder("orientation");
        var rotated = MakeImage(dir, "rotated.jpg", 80, 40, orientation: 6);
        var image = Images.Load(rotated);
        Assert.AreEqual(40, image.PixelWidth);
        Assert.AreEqual(80, image.PixelHeight);
        // Orientation 6 = the camera was turned right: the stored top-left marker is shown top-right.
        AssertNear(Green, Pixel(image, 37, 2), "marker after rotation");
        AssertNear(Red, Pixel(image, 20, 30), "upper half is the stored left half");
        Assert.AreEqual((40, 80), Images.PixelSize(rotated));

        var small = Images.Load(rotated, maxPixels: 20);
        Assert.AreEqual(10, small.PixelWidth);
        Assert.AreEqual(20, small.PixelHeight);
        Assert.IsTrue(small.IsFrozen);
        // Never enlarges.
        var big = Images.Load(rotated, maxPixels: 4000);
        Assert.AreEqual(80, big.PixelHeight);

        foreach (var (o, w, h) in new[] { (2, 80, 40), (3, 80, 40), (4, 80, 40), (5, 40, 80), (7, 40, 80), (8, 40, 80) })
        {
            var path = MakeImage(dir, $"o{o}.jpg", 80, 40, orientation: (ushort)o);
            var loaded = Images.Load(path);
            Assert.AreEqual((w, h), (loaded.PixelWidth, loaded.PixelHeight), $"orientation {o}");
        }
        // 3 = upside down: the marker moves to the bottom-right.
        AssertNear(Green, Pixel(Images.Load(Path.Combine(dir, "o3.jpg")), 77, 37), "orientation 3 marker");
        // 8 = turned left: the marker moves to the bottom-left.
        AssertNear(Green, Pixel(Images.Load(Path.Combine(dir, "o8.jpg")), 2, 77), "orientation 8 marker");
    }

    [TestMethod]
    public async Task JpgAndBmpFlattenTransparencyOntoWhite()
    {
        var dir = TestSetup.NewFolder("alpha");
        var source = MakeImage(dir, "cutout.png", 80, 40, transparentRight: true);
        var loaded = Images.Load(source);
        Assert.IsTrue(Images.HasAlpha(loaded));
        Assert.IsFalse(Images.HasAlpha(Images.Load(MakeImage(dir, "opaque.png"))));
        foreach (var name in new[] { "JPG", "BMP" })
        {
            var output = (await Images.Convert(source, Images.OutputFormats.First(f => f.Name == name)))[0];
            var image = Images.Load(output);
            Assert.IsFalse(Images.HasAlpha(image), name);
            AssertNear(White, Pixel(image, 70, 20), name + " transparent area", 6);
            AssertNear(Red, Pixel(image, 20, 20), name + " opaque area", 30);
        }
        // PNG keeps the transparency.
        var bytes = Images.Encode(loaded, "png", 1);
        using var stream = new MemoryStream(bytes);
        var decoded = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        Assert.AreEqual(0, (Pixel(decoded, 70, 20) >> 24) & 0xFF);
    }

    [TestMethod]
    public async Task OutputsNeverOverwriteAndLeaveTheOriginalAlone()
    {
        var dir = TestSetup.NewFolder("unique");
        var source = MakeImage(dir, "photo.png");
        var original = File.ReadAllBytes(source);
        var jpg = Images.OutputFormats.First(f => f.Name == "JPG");
        var first = (await Images.Convert(source, jpg))[0];
        var firstBytes = File.ReadAllBytes(first);
        var second = (await Images.Convert(source, jpg))[0];
        Assert.AreEqual(Path.Combine(dir, "photo.jpg"), first);
        Assert.AreEqual(Path.Combine(dir, "photo 2.jpg"), second);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(source));
        CollectionAssert.AreEqual(firstBytes, File.ReadAllBytes(first));
        Assert.ThrowsExactly<ToolError>(() => Images.Write(Images.Load(source), first, "png", 1));
        CollectionAssert.AreEqual(firstBytes, File.ReadAllBytes(first));
    }

    [TestMethod]
    public async Task InvalidImagesFailWithoutOutput()
    {
        var dir = TestSetup.NewFolder("broken");
        var bad = Path.Combine(dir, "broken.png");
        File.WriteAllText(bad, "not an image");
        var error = await Assert.ThrowsExactlyAsync<ToolError>(() => Images.Convert(bad, Images.OutputFormats[0]));
        StringAssert.Contains(error.Message, "broken.png");
        foreach (var tool in Images.ToolItems) await Assert.ThrowsExactlyAsync<ToolError>(() => tool.Run(bad));
        await Assert.ThrowsExactlyAsync<ToolError>(() => Images.MakePdf([bad]));
        CollectionAssert.AreEqual(new[] { "broken.png" }, Listing(dir));
    }

    [TestMethod]
    public async Task ToolsRotateFlipGrayAndResize()
    {
        var dir = TestSetup.NewFolder("tools");
        var source = MakeImage(dir, "pic.png", 80, 40);
        async Task<BitmapSource> Run(string title, string expectedName)
        {
            var outputs = await Images.ToolItems.First(t => t.Title == title).Run(source);
            Assert.AreEqual(Path.Combine(dir, expectedName), outputs.Single(), title);
            return Images.Load(outputs[0]);
        }

        var rotated = await Run("Rotate", "pic rotated.png");
        Assert.AreEqual((40, 80), (rotated.PixelWidth, rotated.PixelHeight));
        AssertNear(Green, Pixel(rotated, 37, 2), "rotated clockwise", 2);
        AssertNear(Blue, Pixel(rotated, 20, 70), "rotated bottom", 2);

        var flipped = await Run("Flip", "pic flipped.png");
        Assert.AreEqual((80, 40), (flipped.PixelWidth, flipped.PixelHeight));
        AssertNear(Green, Pixel(flipped, 77, 2), "flipped marker", 2);
        AssertNear(Blue, Pixel(flipped, 10, 20), "flipped left", 2);

        var gray = await Run("Grayscale", "pic gray.png");
        int g = Pixel(gray, 20, 20);
        Assert.AreEqual(g & 0xFF, (g >> 8) & 0xFF);
        Assert.AreEqual(g & 0xFF, (g >> 16) & 0xFF);
        Assert.AreEqual(76, g & 0xFF, 2);   // 0.299 × 255

        var small = await Run("Resize 50%", "pic small.png");
        Assert.AreEqual((40, 20), (small.PixelWidth, small.PixelHeight));
        AssertNear(Red, Pixel(small, 10, 10), "resized red", 2);
        AssertNear(Blue, Pixel(small, 30, 10), "resized blue", 2);

        // The source's format is kept when writable (JPG stays JPG), else PNG.
        var jpg = MakeImage(dir, "shot.jpg");
        Assert.AreEqual(Path.Combine(dir, "shot rotated.jpg"), (await Images.ToolItems.First(t => t.Title == "Rotate").Run(jpg)).Single());
        var jxr = MakeImage(dir, "wide.jxr");
        Assert.AreEqual("png", Images.WritableExt(jxr));
        Assert.AreEqual(Path.Combine(dir, "wide flipped.png"), (await Images.ToolItems.First(t => t.Title == "Flip").Run(jxr)).Single());
        Assert.AreEqual("JPG", Images.WritableExt(@"C:\x\a.JPG"));
    }

    [TestMethod]
    public async Task MakePdfWritesOnePagePerImageAtPixelSize()
    {
        var dir = TestSetup.NewFolder("pdf");
        var opaque = MakeImage(dir, "one.jpg", 120, 60);
        var cutout = MakeImage(dir, "two.png", 50, 90, transparentRight: true);
        var rotated = MakeImage(dir, "three.jpg", 80, 40, orientation: 6);
        var outputs = await Images.MakePdf([opaque, cutout, rotated]);
        Assert.AreEqual(Path.Combine(dir, "one.pdf"), outputs.Single());

        var text = Encoding.Latin1.GetString(File.ReadAllBytes(outputs[0]));
        Assert.StartsWith("%PDF-1.4", text);
        Assert.AreEqual(3, System.Text.RegularExpressions.Regex.Matches(text, @"/Type /Page\b(?!s)").Count);
        StringAssert.Contains(text, "/MediaBox [0 0 120 60]");
        StringAssert.Contains(text, "/MediaBox [0 0 50 90]");
        StringAssert.Contains(text, "/MediaBox [0 0 40 80]");
        StringAssert.Contains(text, "/DCTDecode");
        StringAssert.Contains(text, "/SMask");
        // startxref points at the cross-reference table, and every offset there starts its object.
        int startxref = int.Parse(System.Text.RegularExpressions.Regex.Match(text, @"startxref\n(\d+)").Groups[1].Value);
        Assert.AreEqual("xref", text.Substring(startxref, 4));
        var entries = System.Text.RegularExpressions.Regex.Matches(text[startxref..], @"(\d{10}) 00000 n");
        for (int i = 0; i < entries.Count; i++)
            Assert.StartsWith($"{i + 1} 0 obj", text[int.Parse(entries[i].Groups[1].Value)..]);

        // Windows' own PDF renderer opens it.
        var file = await StorageFile.GetFileFromPathAsync(outputs[0]);
        var pdf = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
        Assert.AreEqual(3u, pdf.PageCount);
        using var page = pdf.GetPage(1);
        // PdfPage.Size is in DIPs (96 per inch); the page is 50 × 90 points.
        Assert.AreEqual(50 * 96 / 72.0, page.Size.Width, 0.5);
        Assert.AreEqual(90 * 96 / 72.0, page.Size.Height, 0.5);

        // Opaque JPEGs that need no rotation are embedded as they are.
        var jpegBytes = File.ReadAllBytes(opaque);
        Assert.IsTrue(text.Contains(Encoding.Latin1.GetString(jpegBytes)), "the original JPEG data is embedded");
    }

    [TestMethod]
    public void GifWriterMakesALoopingAnimationWithExactPalettes()
    {
        var dir = TestSetup.NewFolder("gif");
        var path = Path.Combine(dir, "anim.gif");
        int[] colors = [Red, Green, Blue];
        using (var file = File.Create(path))
        {
            var writer = new GifWriter(file, 30, 20, loopForever: true);
            for (int i = 0; i < colors.Length; i++)
                writer.AddFrame(new Raster(30, 20, Enumerable.Repeat(colors[i], 600).ToArray(), false), i == 0 ? 8 : 9, dither: false);
            writer.Finish();
        }
        var bytes = File.ReadAllBytes(path);
        var text = Encoding.ASCII.GetString(bytes);
        Assert.StartsWith("GIF89a", text);
        int loop = text.IndexOf("NETSCAPE2.0", StringComparison.Ordinal);
        Assert.IsGreaterThan(0, loop);
        Assert.AreEqual(0, bytes[loop + 13] | bytes[loop + 14] << 8, "loops forever");

        using var stream = File.OpenRead(path);
        var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Assert.HasCount(3, decoder.Frames);
        for (int i = 0; i < colors.Length; i++) Assert.AreEqual(colors[i], Pixel(decoder.Frames[i], 15, 10));
        Assert.AreEqual((ushort)9, (ushort)(decoder.Frames[1].Metadata as BitmapMetadata)!.GetQuery("/grctlext/Delay"));

        // A still with a gradient keeps its colours close (median cut + dithering) and its transparency.
        var gradient = new int[256 * 64];
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 256; x++)
                gradient[y * 256 + x] = x >= 240 ? 0 : unchecked((int)0xFF000000) | (x << 16) | ((255 - x) << 8) | (y * 4);
        var still = GifWriter.EncodeStill(new Raster(256, 64, gradient));
        using var s2 = new MemoryStream(still);
        var frame = new GifBitmapDecoder(s2, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        long error = 0;
        for (int x = 0; x < 240; x += 7)
        {
            int p = Pixel(frame, x, 32);
            error += Math.Abs(((p >> 16) & 0xFF) - x) + Math.Abs(((p >> 8) & 0xFF) - (255 - x)) + Math.Abs((p & 0xFF) - 128);
        }
        Assert.IsLessThan(30.0, error / (240.0 / 7) / 3, "average channel error");
        Assert.AreEqual(0, (Pixel(frame, 250, 10) >> 24) & 0xFF, "transparent stays transparent");
    }

    [TestMethod]
    public void ResizeAveragesAreasAndKeepsEdgesClean()
    {
        // 4 × 1: black, white, black, white → 2 × 1 is mid-gray; transparent neighbours don't darken.
        var r = new Raster(4, 1, [unchecked((int)0xFF000000), White, unchecked((int)0xFF000000), White]).Resized(2, 1);
        Assert.AreEqual(128, r.Data[0] & 0xFF, 1);
        var edge = new Raster(2, 1, [Red, 0]).Resized(1, 1);
        Assert.AreEqual(0xFF, (edge.Data[0] >> 16) & 0xFF, "premultiplied: no dark fringe");
        Assert.AreEqual(128, (edge.Data[0] >> 24) & 0xFF, 1);
    }

    // MARK: Audio

    [TestMethod]
    public async Task AudioConvertsToEveryFormatWithValidHeaders()
    {
        var dir = TestSetup.NewFolder("audio");
        var wav = MakeWav(dir);
        var titles = Catalog.Items([wav], WheelMode.Convert).Select(i => i.Title).ToList();
        CollectionAssert.DoesNotContain(titles, "WAV");
        CollectionAssert.Contains(titles, "ALAC");
        CollectionAssert.Contains(titles, "AIFF");

        async Task<byte[]> Convert(string name, string expected)
        {
            var output = (await Audio.Convert(wav, Audio.Formats.First(f => f.Name == name))).Single();
            Assert.AreEqual(Path.Combine(dir, expected), output, name);
            return File.ReadAllBytes(output);
        }

        var flac = await Convert("FLAC", "tone.flac");
        Assert.AreEqual("fLaC", Encoding.ASCII.GetString(flac, 0, 4));

        var aiff = await Convert("AIFF", "tone.aiff");
        Assert.AreEqual("FORM", Encoding.ASCII.GetString(aiff, 0, 4));
        Assert.AreEqual("AIFF", Encoding.ASCII.GetString(aiff, 8, 4));
        Assert.AreEqual(aiff.Length - 8, (int)BinaryPrimitives.ReadUInt32BigEndian(aiff.AsSpan(4)));
        Assert.AreEqual("COMM", Encoding.ASCII.GetString(aiff, 12, 4));
        Assert.AreEqual(2, BinaryPrimitives.ReadUInt16BigEndian(aiff.AsSpan(20)));
        Assert.AreEqual(44100u, BinaryPrimitives.ReadUInt32BigEndian(aiff.AsSpan(22)), 300u);
        Assert.AreEqual(16, BinaryPrimitives.ReadUInt16BigEndian(aiff.AsSpan(26)));
        Assert.AreEqual(44100, AudioContainers.FromExtended(aiff.AsSpan(28, 10)), 0.001);
        Assert.AreEqual("SSND", Encoding.ASCII.GetString(aiff, 38, 4));
        // Big-endian samples: the second frame's left sample equals the source's.
        short expectedSample = (short)(Math.Sin(2 * Math.PI * 440 * 1 / 44100.0) * 12000);
        Assert.AreEqual(expectedSample, BinaryPrimitives.ReadInt16BigEndian(aiff.AsSpan(54 + 4)), 2);

        var caf = await Convert("CAF", "tone.caf");
        Assert.AreEqual("caff", Encoding.ASCII.GetString(caf, 0, 4));
        Assert.AreEqual("desc", Encoding.ASCII.GetString(caf, 8, 4));
        Assert.AreEqual(44100.0, BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(caf.AsSpan(20))));
        Assert.AreEqual("lpcm", Encoding.ASCII.GetString(caf, 28, 4));
        Assert.AreEqual("data", Encoding.ASCII.GetString(caf, 52, 4));
        Assert.AreEqual(caf.Length - 68, BinaryPrimitives.ReadInt64BigEndian(caf.AsSpan(56)) - 4);

        var m4a = await Convert("M4A", "tone.m4a");
        Assert.AreEqual("ftyp", Encoding.ASCII.GetString(m4a, 4, 4));
        var alac = await Convert("ALAC", "tone lossless.m4a");
        Assert.IsTrue(Encoding.ASCII.GetString(alac).Contains("alac"), "ALAC sample entry");

        var aac = await Convert("AAC", "tone.aac");
        int frames = 0, at = 0;
        while (at < aac.Length)
        {
            Assert.AreEqual(0xFFF, (aac[at] << 4) | (aac[at + 1] >> 4), $"ADTS sync at {at}");
            int length = ((aac[at + 3] & 3) << 11) | (aac[at + 4] << 3) | (aac[at + 5] >> 5);
            Assert.IsGreaterThan(7, length);
            at += length; frames++;
        }
        Assert.AreEqual(aac.Length, at);
        Assert.IsGreaterThan(40, frames);   // ~43 frames of 1024 samples per second

        var mp3 = await Convert("MP3", "tone.mp3");
        Assert.IsTrue(Encoding.ASCII.GetString(mp3, 0, 3) == "ID3" || (mp3[0] == 0xFF && (mp3[1] & 0xE0) == 0xE0), "MP3 header");

        // A second conversion gets a new name.
        Assert.AreEqual(Path.Combine(dir, "tone 2.flac"), (await Audio.Convert(wav, Audio.Formats.First(f => f.Name == "FLAC"))).Single());
        Assert.IsFalse(Directory.EnumerateFiles(dir, ".swivel-*", new EnumerationOptions { AttributesToSkip = 0 }).Any(), "temp files left behind");
    }

    static async Task<MediaEncodingProfile> Probe(string path) =>
        await MediaEncodingProfile.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(path));

    [TestMethod]
    public async Task AudioToolsAndMerge()
    {
        var dir = TestSetup.NewFolder("audio-tools");
        var wav = MakeWav(dir);
        foreach (var (title, suffix, channels) in new[] { ("Compress", " compressed", 2u), ("Mono", " mono", 1u), ("Voice Memo", " voice", 1u) })
        {
            var output = (await Audio.ToolItems.First(t => t.Title == title).Run(wav)).Single();
            Assert.AreEqual(Path.Combine(dir, "tone" + suffix + ".m4a"), output);
            var profile = await Probe(output);
            Assert.AreEqual(channels, profile.Audio.ChannelCount, title);
            if (title == "Voice Memo") Assert.AreEqual(22050u, profile.Audio.SampleRate, "voice memos are 22.05 kHz like the Mac");
        }

        var second = MakeWav(dir, "second.wav", seconds: 0.5, channels: 1, rate: 22050);
        var merged = await Video.Merge([wav, second], audioOnly: true);
        Assert.AreEqual(Path.Combine(dir, "Merged.m4a"), merged);
        var properties = await (await StorageFile.GetFileFromPathAsync(merged)).Properties.GetMusicPropertiesAsync();
        Assert.AreEqual(1.5, properties.Duration.TotalSeconds, 0.15);
        Assert.AreEqual(Path.Combine(dir, "Merged 2.m4a"), await Video.Merge([wav, second], audioOnly: true));
    }

    // MARK: Video

    /// A 3-second 1280 × 720 MP4: 2 s of a picture (top red, bottom blue), then 1 s of green, with a tone.
    static async Task<string> MakeVideo(string dir, string name = "clip.mp4", int width = 1280, int height = 720)
    {
        var picture = Path.Combine(dir, "frame-source.png");
        var data = new int[width * height];
        for (int i = 0; i < data.Length; i++) data[i] = i / width < height / 2 ? Red : Blue;
        using (var f = File.Create(picture))
        {
            var e = new PngBitmapEncoder();
            e.Frames.Add(BitmapFrame.Create(Bitmap(width, height, data, false)));
            e.Save(f);
        }
        var tone = MakeWav(dir, "video-tone.wav", seconds: 3);
        var composition = new MediaComposition();
        composition.Clips.Add(await MediaClip.CreateFromImageFileAsync(await StorageFile.GetFileFromPathAsync(picture), TimeSpan.FromSeconds(2)));
        composition.Clips.Add(MediaClip.CreateFromColor(Windows.UI.Color.FromArgb(255, 0, 255, 0), TimeSpan.FromSeconds(1)));
        composition.BackgroundAudioTracks.Add(await BackgroundAudioTrack.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(tone)));
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD720p);
        profile.Video.Width = (uint)width;
        profile.Video.Height = (uint)height;
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, []);
        var result = await composition.RenderToFileAsync(await StorageFile.GetFileFromPathAsync(path), MediaTrimmingPreference.Precise, profile);
        Assert.AreEqual(Windows.Media.Transcoding.TranscodeFailureReason.None, result);
        File.Delete(picture);
        File.Delete(tone);
        return path;
    }

    [TestMethod]
    public async Task VideoFramesGifAndThumbnail()
    {
        var dir = TestSetup.NewFolder("video");
        var video = await MakeVideo(dir);

        var frame = (await Video.ToolItems.First(t => t.Title == "Frame").Run(video)).Single();
        Assert.AreEqual(Path.Combine(dir, "clip frame.png"), frame);
        var image = Images.Load(frame);
        Assert.AreEqual((1280, 720), (image.PixelWidth, image.PixelHeight));
        AssertNear(Red, Pixel(image, 640, 100), "frame top");
        AssertNear(Blue, Pixel(image, 640, 620), "frame bottom");

        var thumb = await Video.Thumbnail(video, 200);
        Assert.AreEqual((200, 113), (thumb.PixelWidth, thumb.PixelHeight));
        Assert.IsTrue(thumb.IsFrozen);

        var gif = (await Video.ToolItems.First(t => t.Title == "GIF").Run(video)).Single();
        Assert.AreEqual(Path.Combine(dir, "clip.gif"), gif);
        using var stream = File.OpenRead(gif);
        var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Assert.AreEqual(36, decoder.Frames.Count, 1);   // 3 s × 12 fps
        Assert.AreEqual(540, decoder.Frames[0].PixelWidth);
        Assert.AreEqual(304, decoder.Frames[0].PixelHeight);
        AssertNear(Red, Pixel(decoder.Frames[0], 270, 40), "first GIF frame");
        AssertNear(Green, Pixel(decoder.Frames[^1], 270, 150), "last GIF frame");
        Assert.IsTrue(Encoding.ASCII.GetString(File.ReadAllBytes(gif)).Contains("NETSCAPE2.0"));
        int total = decoder.Frames.Sum(f => (int)(ushort)(f.Metadata as BitmapMetadata)!.GetQuery("/grctlext/Delay"));
        Assert.AreEqual(decoder.Frames.Count * 100 / 12.0, total, 1.0, "delays add up to the clip length");
    }

    [TestMethod]
    public async Task VideoConvertAndTools()
    {
        var dir = TestSetup.NewFolder("video-tools");
        var video = await MakeVideo(dir);
        var titles = Video.ConvertItems(new HashSet<string> { "mp4" }).Select(o => o.Title).ToList();
        CollectionAssert.DoesNotContain(titles, "MP4");
        CollectionAssert.DoesNotContain(titles, "MOV");
        CollectionAssert.IsSubsetOf(new[] { "M4V", "GIF", "M4A", "WAV", "PNG Frame" }, titles);

        async Task<string> Run(IEnumerable<FileOption> options, string title, string expected)
        {
            var output = (await options.First(o => o.Title == title).Run(video)).Single();
            Assert.AreEqual(Path.Combine(dir, expected), output, title);
            return output;
        }

        var m4v = await Run(Video.ConvertItems(new HashSet<string> { "mp4" }), "M4V", "clip.m4v");
        var p = await Probe(m4v);
        Assert.AreEqual((1280u, 720u), (p.Video.Width, p.Video.Height));
        Assert.IsNotNull(p.Audio);

        var p480 = await Probe(await Run(Video.ToolItems, "480p", "clip 480p.mp4"));
        Assert.AreEqual((640u, 360u), (p480.Video.Width, p480.Video.Height));
        var p1080 = await Probe(await Run(Video.ToolItems, "1080p", "clip 1080p.mp4"));
        Assert.AreEqual((1280u, 720u), (p1080.Video.Width, p1080.Video.Height), "never enlarged");
        var compressed = await Run(Video.ToolItems, "Compress", "clip compressed.mp4");
        Assert.IsLessThan(new FileInfo(video).Length * 2, new FileInfo(compressed).Length);
        Assert.AreEqual(960u, (await Probe(compressed)).Video.Width);

        var muted = await Probe(await Run(Video.ToolItems, "Mute", "clip muted.mp4"));
        Assert.IsNull(muted.Audio, "no sound track");
        Assert.IsNotNull(muted.Video);

        var audio = await Probe(await Run(Video.ToolItems, "Extract Audio", "clip.m4a"));
        Assert.IsNull(audio.Video);
        Assert.IsNotNull(audio.Audio);
        var wav = await Run(Video.ConvertItems(new HashSet<string> { "mp4" }), "WAV", "clip.wav");
        Assert.AreEqual("RIFF", Encoding.ASCII.GetString(File.ReadAllBytes(wav), 0, 4));

        var hevc = Video.ConvertItems(new HashSet<string> { "mp4" }).FirstOrDefault(o => o.Title == "HEVC");
        if (hevc != null)
        {
            try
            {
                var output = (await hevc.Run(video)).Single();
                Assert.AreEqual(Path.Combine(dir, "clip hevc.mp4"), output);
                Assert.AreEqual("HEVC", (await Probe(output)).Video.Subtype);
            }
            catch (ToolError e) { StringAssert.Contains(e.Message, "HEVC"); }  // no encoder on this PC: a clear message
        }

        var second = await MakeVideo(dir, "second.mp4", 640, 360);
        var merged = await Video.Merge([video, second], audioOnly: false);
        Assert.AreEqual(Path.Combine(dir, "Merged.mp4"), merged);
        var properties = await (await StorageFile.GetFileFromPathAsync(merged)).Properties.GetVideoPropertiesAsync();
        Assert.AreEqual(6, properties.Duration.TotalSeconds, 0.3);
        Assert.AreEqual(1280u, properties.Width);
        Assert.IsFalse(Directory.EnumerateFiles(dir, ".swivel-*", new EnumerationOptions { AttributesToSkip = 0 }).Any(), "temp files left behind");
    }

    /// Sets the video track's display matrix (tkhd) to a clockwise turn, like a phone held upright.
    static void SetDisplayRotation(string path, int clockwise)
    {
        var file = File.ReadAllBytes(path);
        var moov = AudioContainers.Child(file, 0, file.Length, "moov")!.Value;
        foreach (var trak in AudioContainers.Children(file, moov.Start, moov.End, "trak"))
        {
            var mdia = AudioContainers.Child(file, trak.Start, trak.End, "mdia")!.Value;
            var hdlr = AudioContainers.Child(file, mdia.Start, mdia.End, "hdlr")!.Value;
            if (Encoding.ASCII.GetString(file, hdlr.Start + 8, 4) != "vide") continue;
            var tkhd = AudioContainers.Child(file, trak.Start, trak.End, "tkhd")!.Value;
            int matrix = tkhd.Start + (file[tkhd.Start] == 1 ? 52 : 40);
            int one = 0x10000;
            int[] values = clockwise switch
            {
                90 => [0, one, 0, -one, 0, 0, 0, 0, 0x40000000],
                180 => [-one, 0, 0, 0, -one, 0, 0, 0, 0x40000000],
                270 => [0, -one, 0, one, 0, 0, 0, 0, 0x40000000],
                _ => [one, 0, 0, 0, one, 0, 0, 0, 0x40000000],
            };
            for (int i = 0; i < 9; i++) BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(matrix + i * 4), values[i]);
        }
        File.WriteAllBytes(path, file);
    }

    [TestMethod]
    public async Task RotatedVideoComesOutUpright()
    {
        var dir = TestSetup.NewFolder("rotated");
        var original = await MakeVideo(dir, "phone.mp4", 640, 360);
        foreach (var (degrees, size, redAt, blueAt) in new[]
        {
            (90, (360, 640), (300, 320), (60, 320)),     // turned clockwise: the picture's top (red) is on the right
            (180, (640, 360), (320, 300), (320, 60)),    // upside down: red at the bottom
            (270, (360, 640), (60, 320), (300, 320)),    // turned counter-clockwise: red on the left
        })
        {
            var turned = Path.Combine(dir, $"turned{degrees}.mp4");
            File.Copy(original, turned);
            SetDisplayRotation(turned, degrees);
            using var frames = new MediaFrames(turned);
            Console.WriteLine($"{degrees}: track {frames.TrackRotation}, Swivel turns {frames.Rotation}, display {frames.DisplayWidth}×{frames.DisplayHeight}");
            var frame = frames.FrameAt(TimeSpan.FromSeconds(0.5)).ToBitmap();
            Assert.AreEqual(size, (frame.PixelWidth, frame.PixelHeight), $"{degrees}°");
            AssertNear(Red, Pixel(frame, redAt.Item1, redAt.Item2), $"{degrees}° red");
            AssertNear(Blue, Pixel(frame, blueAt.Item1, blueAt.Item2), $"{degrees}° blue");
        }
        var video = Path.Combine(dir, "turned90.mp4");

        var output = (await Video.ToolItems.First(t => t.Title == "480p").Run(video)).Single();
        using (var frames = new MediaFrames(output))
        {
            var frame = frames.FrameAt(TimeSpan.FromSeconds(0.5)).ToBitmap();
            Console.WriteLine($"480p output rotation {frames.Rotation}, frame {frame.PixelWidth}×{frame.PixelHeight}");
            Assert.AreEqual(360, frame.PixelWidth);
            Assert.AreEqual(640, frame.PixelHeight);
            AssertNear(Red, Pixel(frame, 300, 320), "output right side is the top");
        }
    }

    // MARK: Archives

    static string MakeTree(string dir)
    {
        var folder = Path.Combine(dir, "Project");
        Directory.CreateDirectory(Path.Combine(folder, "sub", "empty"));
        File.WriteAllText(Path.Combine(folder, "readme.txt"), "hello");
        File.WriteAllText(Path.Combine(folder, "sub", "中文.txt"), "你好");
        return folder;
    }

    [TestMethod]
    public async Task ZipAndTarRoundTripFilesAndFolders()
    {
        var dir = TestSetup.NewFolder("archives");
        var folder = MakeTree(dir);
        var note = Path.Combine(dir, "note.md");
        File.WriteAllText(note, "# note");

        var titles = Catalog.Items([folder], WheelMode.Convert).Select(i => i.Title).ToList();
        CollectionAssert.AreEqual(new[] { "ZIP", "TAR.GZ" }, titles);

        var single = await Archives.Zip([folder]);
        Assert.AreEqual(Path.Combine(dir, "Project.zip"), single);
        using (var zip = ZipFile.OpenRead(single))
        {
            var names = zip.Entries.Select(e => e.FullName).ToList();
            CollectionAssert.Contains(names, "Project/readme.txt");
            CollectionAssert.Contains(names, "Project/sub/中文.txt");
            CollectionAssert.Contains(names, "Project/sub/empty/");
        }
        var several = await Archives.Zip([folder, note]);
        Assert.AreEqual(Path.Combine(dir, "Archive.zip"), several);
        Assert.AreEqual(Path.Combine(dir, "Archive 2.zip"), await Archives.Zip([folder, note]));
        var tar = await Archives.TarGz([folder, note]);
        Assert.AreEqual(Path.Combine(dir, "Archive.tar.gz"), tar);
        Assert.AreEqual(Path.Combine(dir, "Archive 2.tar.gz"), await Archives.TarGz([folder, note]));
        Assert.AreEqual(Path.Combine(dir, "note.zip"), await Archives.Zip([note]));

        // Extracting lands each top-level item next to the archive, under free names.
        foreach (var archive in new[] { several, tar })
        {
            var outputs = await Archives.Extract(archive);
            Assert.HasCount(2, outputs, archive);
            var project = outputs.Single(o => Path.GetFileName(o).StartsWith("Project"));
            Assert.AreNotEqual(folder, project, "the original folder is never overwritten");
            Assert.AreEqual("你好", File.ReadAllText(Path.Combine(project, "sub", "中文.txt")));
            Assert.IsTrue(Directory.Exists(Path.Combine(project, "sub", "empty")));
            Assert.AreEqual("# note", File.ReadAllText(outputs.Single(o => Path.GetFileName(o).StartsWith("note"))));
        }
        Assert.IsTrue(Directory.Exists(Path.Combine(dir, "Project 2")));
        Assert.IsTrue(Directory.Exists(Path.Combine(dir, "Project 3")));
        Assert.IsFalse(Directory.EnumerateFileSystemEntries(dir, ".swivel-*", new EnumerationOptions { AttributesToSkip = 0 }).Any(), "staging left behind");
        Assert.AreEqual("hello", File.ReadAllText(Path.Combine(folder, "readme.txt")));
    }

    [TestMethod]
    public async Task ExtractSkipsMacOsxAndHandlesGzAndOddNames()
    {
        var dir = TestSetup.NewFolder("extract");
        var path = Path.Combine(dir, "mac.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            zip.CreateEntry("photos/").ToString();
            using (var w = new StreamWriter(zip.CreateEntry("photos/a:b.txt").Open())) w.Write("colon");
            using (var w = new StreamWriter(zip.CreateEntry("__MACOSX/photos/._a.txt").Open())) w.Write("fork");
            using (var w = new StreamWriter(zip.CreateEntry("./top.txt").Open())) w.Write("top");
        }
        var outputs = await Archives.Extract(path);
        CollectionAssert.AreEquivalent(new[] { Path.Combine(dir, "photos"), Path.Combine(dir, "top.txt") }, outputs);
        Assert.AreEqual("colon", File.ReadAllText(Path.Combine(dir, "photos", "a_b.txt")));
        Assert.IsFalse(Directory.Exists(Path.Combine(dir, "__MACOSX")));

        // A bare .gz is one file named without .gz.
        var gz = Path.Combine(dir, "log.txt.gz");
        using (var f = File.Create(gz))
        using (var g = new GZipStream(f, CompressionLevel.Optimal))
            g.Write("gzipped"u8);
        Assert.AreEqual(Path.Combine(dir, "log.txt"), (await Archives.Extract(gz)).Single());
        Assert.AreEqual("gzipped", File.ReadAllText(Path.Combine(dir, "log.txt")));

        // A plain .tar.
        var tarPath = Path.Combine(dir, "plain.tar");
        using (var f = File.Create(tarPath))
        using (var t = new TarWriter(f))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, "inside.txt") { DataStream = new MemoryStream("tar"u8.ToArray()) };
            t.WriteEntry(entry);
        }
        Assert.AreEqual(Path.Combine(dir, "inside.txt"), (await Archives.Extract(tarPath)).Single());
        Assert.IsFalse(Directory.EnumerateFileSystemEntries(dir, ".swivel-*", new EnumerationOptions { AttributesToSkip = 0 }).Any(), "staging left behind");
    }

    [TestMethod]
    public async Task OtherFormatsGoThroughWindowsTar()
    {
        var tar = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
        if (!File.Exists(tar)) Assert.Inconclusive("This PC has no tar.exe");
        var dir = TestSetup.NewFolder("systemtar");
        var source = Path.Combine(dir, "stuff");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "a.txt"), "alpha");
        int tested = 0;
        foreach (var ext in new[] { "7z", "tar.bz2", "tar.xz" })
        {
            var archive = Path.Combine(dir, "pack." + ext);
            var info = new System.Diagnostics.ProcessStartInfo(tar) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var a in new[] { "-a", "-cf", archive, "-C", dir, "stuff" }) info.ArgumentList.Add(a);
            using (var p = System.Diagnostics.Process.Start(info)!)
            {
                await p.WaitForExitAsync();
                if (p.ExitCode != 0) { Console.WriteLine($"tar.exe can't write .{ext} here"); continue; }
            }
            var outputs = await Archives.Extract(archive);
            var folder = outputs.Single();
            Assert.AreEqual("alpha", File.ReadAllText(Path.Combine(folder, "a.txt")), ext);
            Directory.Delete(folder, true);
            tested++;
        }
        Assert.IsGreaterThan(0, tested, "no format tar.exe can write");
        Assert.IsFalse(Directory.EnumerateFileSystemEntries(dir, ".swivel-*", new EnumerationOptions { AttributesToSkip = 0 }).Any(), "staging left behind");

        // Not an archive at all: a clear error, nothing left behind.
        var junk = Path.Combine(dir, "junk.7z");
        File.WriteAllText(junk, "not an archive");
        await Assert.ThrowsExactlyAsync<ToolError>(() => Archives.Extract(junk));
        Assert.IsFalse(Directory.EnumerateFileSystemEntries(dir, ".swivel-*", new EnumerationOptions { AttributesToSkip = 0 }).Any(), "staging left behind");
    }

    [TestMethod]
    public async Task ZipSlipIsRejectedAndNothingLands()
    {
        var dir = TestSetup.NewFolder("slip");
        var inner = Path.Combine(dir, "inner");
        Directory.CreateDirectory(inner);
        foreach (var evil in new[] { "../evil.txt", "ok/../../evil.txt", "/abs.txt", "C:/abs.txt" })
        {
            var path = Path.Combine(inner, "evil.zip");
            File.Delete(path);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                using (var w = new StreamWriter(zip.CreateEntry("fine.txt").Open())) w.Write("fine");
                using (var w = new StreamWriter(zip.CreateEntry(evil).Open())) w.Write("evil");
            }
            await Assert.ThrowsExactlyAsync<ToolError>(() => Archives.Extract(path), evil);
            Assert.IsFalse(File.Exists(Path.Combine(dir, "evil.txt")), evil);
            CollectionAssert.AreEqual(new[] { "evil.zip" }, Listing(inner), evil);
        }

        var tarPath = Path.Combine(inner, "evil.tar");
        using (var f = File.Create(tarPath))
        using (var t = new TarWriter(f))
            t.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "../evil.txt") { DataStream = new MemoryStream("x"u8.ToArray()) });
        await Assert.ThrowsExactlyAsync<ToolError>(() => Archives.Extract(tarPath));
        Assert.IsFalse(File.Exists(Path.Combine(dir, "evil.txt")));
    }
}
