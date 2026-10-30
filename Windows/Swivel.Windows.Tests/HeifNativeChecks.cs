using System;
using System.IO;
using System.Linq;
using Swivel.Core;
using Swivel.Windows;

/// HEIC/AVIF through the image tools on a real Windows machine. Which branch runs depends on
/// whether Microsoft's HEIF decoder and HEVC/AV1 video decoders are installed; the report line
/// says so. The not-installed messages are exercised everywhere through the availability override.
internal static class HeifNativeChecks
{
    internal static void Run(string directory, Action<bool, string> check, Action<Action, string> reject)
    {
        string folder = Path.Combine(directory, "heif"); Directory.CreateDirectory(folder);
        var facts = HeifDecoder.Describe();
        int hevc = HeifDecoder.VideoDecoders(HeifCodec.Hevc), av1 = HeifDecoder.VideoDecoders(HeifCodec.Av1);
        int hevcEncoders = HeifDecoder.VideoEncoders(HeifCodec.Hevc);
        Console.WriteLine($"HEIF REPORT: WIC HEIF decoder present={facts.Present} trusted={facts.Trusted} problem={facts.Problem ?? "none"} clsid={facts.Clsid} vendor={facts.Vendor} author=\"{facts.Author}\" name=\"{facts.FriendlyName}\" version=\"{facts.Version}\" extensions=\"{facts.Extensions}\" container={facts.Container}");
        Console.WriteLine($"HEIF REPORT: Media Foundation video decoders HEVC={hevc} AV1={av1}; HEVC encoders={hevcEncoders}");
        check(!facts.Present || facts.Clsid == HeifDecoder.HeifDecoderClsid, "A registered HEIF decoder reports Microsoft's CLSID");

        string Message(string path, string action = "convert.png")
        {
            try { ImageTools.Run(path, action, 1920, default); return "accepted"; }
            catch (UserError error) { return error.Message; }
        }
        bool NothingWritten(string name) => !Directory.EnumerateFiles(folder, name + " converted*").Any();
        string Write(string name, byte[] data) { string path = Path.Combine(folder, name); File.WriteAllBytes(path, data); return path; }

        // A structurally valid HEIC/AVIF whose coded data is placeholder bytes.
        string heic = Write("照片 still.heic", HeifChecks.File(new HeifChecks.Options { Width = 64, Height = 48 }));
        string avif = Write("web.avif", HeifChecks.File(new HeifChecks.Options { Major = "avif", Compatible = new[] { "avif", "mif1" }, Codec = "av01", Width = 64, Height = 48 }));
        byte[] original = File.ReadAllBytes(heic);

        // Not installed: the clear Store message, before WIC is asked for anything.
        HeifDecoder.AvailabilityOverride = _ => false;
        try
        {
            check(Message(heic) == HeifImage.HeicMissing && NothingWritten("照片 still"), "Without an HEVC decoder a HEIC names the Microsoft Store extensions and writes nothing");
            check(Message(avif) == HeifImage.AvifMissing && NothingWritten("web"), "Without an AV1 decoder an AVIF names the Microsoft Store extensions and writes nothing");
            string? read = null;
            try { ImageTools.OpenForReading(heic, default).Dispose(); } catch (UserError error) { read = error.Message; }
            check(read == HeifImage.HeicMissing, "Reading-only tools give the same message");
        }
        finally { HeifDecoder.AvailabilityOverride = null; }

        // The real machine: placeholder data is either missing-codec or a decoder failure, never output.
        string expectHeic = hevc == 0 || !facts.Present ? HeifImage.HeicMissing : facts.Trusted ? HeifImage.DecodeFailed : HeifDecoder.NotMicrosoft;
        string actual = Message(heic);
        check(actual == expectHeic || facts.Trusted && hevc > 0 && actual == HeifImage.Unsupported, "Placeholder HEIC on this machine: " + actual);
        check(NothingWritten("照片 still") && File.ReadAllBytes(heic).SequenceEqual(original), "Nothing is written and the original is unchanged");

        // The box gate answers before any codec, on every machine.
        check(Message(Write("burst.heic", HeifChecks.File(new HeifChecks.Options { Major = "msf1", Compatible = new[] { "msf1" } }))) == HeifImage.Sequence, "An image sequence is refused by the gate");
        check(Message(Write("moving.avif", HeifChecks.File(new HeifChecks.Options { Major = "avis", Compatible = new[] { "avis", "avif" }, Codec = "av01" }))) == HeifImage.Sequence, "An animated AVIF is refused by the gate");
        check(Message(Write("huge.heic", HeifChecks.File(new HeifChecks.Options { Width = 5000, Height = 4000 }))) == "Images are limited to 16 megapixels and 16384 pixels per side.", "An over-16 MP ispe is refused before decoding");
        var upsideDown = new HeifChecks.Options(); upsideDown.Extra.Add(HeifChecks.Box("irot", HeifChecks.B(2)));
        check(Message(Write("upside down.heic", HeifChecks.File(upsideDown))) == HeifImage.Ambiguous, "A half-turned photo is refused rather than guessed");
        var mirrored = new HeifChecks.Options(); mirrored.Extra.Add(HeifChecks.Box("irot", HeifChecks.B(1))); mirrored.Extra.Add(HeifChecks.Box("imir", HeifChecks.B(0)));
        check(Message(Write("mirrored.heic", HeifChecks.File(mirrored))) == HeifImage.Ambiguous, "A mirrored quarter-turned photo is refused rather than guessed");
        var square = new HeifChecks.Options { Width = 512, Height = 512 }; square.Extra.Add(HeifChecks.Box("irot", HeifChecks.B(3)));
        check(Message(Write("square.heic", HeifChecks.File(square))) == HeifImage.AmbiguousSquare, "A quarter-turned square photo is refused with its own message");
        check(Message(Write("p3.avif", HeifChecks.File(new HeifChecks.Options { Major = "avif", Compatible = new[] { "avif" }, Codec = "av01", Primaries = 12 }))) == HeifImage.WideGamut, "A P3 AVIF without a profile is refused for saving");
        check(Message(Write("deep.heic", HeifChecks.File(new HeifChecks.Options { Depth = 10 }))) == HeifImage.HighDepth, "A 10-bit HEIC is refused for saving");
        check(Message(Write("pq.avif", HeifChecks.File(new HeifChecks.Options { Major = "avif", Compatible = new[] { "avif" }, Codec = "av01", Transfer = 16 }))) == HeifImage.HighDepth, "A PQ (HDR) AVIF is refused for saving");
        check(Message(Write("cut.heic", original[..200])) == HeifImage.Invalid, "A truncated HEIC is refused");
        reject(() => ImageTools.Run(heic, "convert.png", 1920, new System.Threading.CancellationToken(true)), "A cancelled HEIC conversion stops");
        check(Catalog.Available(new Prefs(), new[] { heic }).Any(a => a.Id == "convert.png") && Catalog.Available(new Prefs(), new[] { avif }).Any(a => a.Id == "image.rotate"), "The wheel offers image actions for HEIC and AVIF");

        // A real picture, only when Microsoft's HEIF encoder (and an HEVC encoder) exist on this machine.
        if (!facts.Trusted || hevc == 0) { Console.WriteLine("HEIF REPORT: real-picture decode not exercised (no trusted HEIF decoder or no HEVC decoder)."); return; }
        var source = new PixelImage(64, 48, Enumerable.Range(0, 64 * 48).SelectMany(i => new byte[] { (byte)(i % 64 * 4), (byte)(i / 64 * 5), 128, 255 }).ToArray());
        byte[]? encoded = hevcEncoders > 0 ? HeifDecoder.EncodeForTests(source) : null;
        if (encoded is null) { Console.WriteLine("HEIF REPORT: real-picture decode not exercised (Microsoft's HEIF encoder unavailable)."); return; }
        string real = Write("made here.heic", encoded);
        var info = HeifImage.Inspect(encoded, default);
        Console.WriteLine($"HEIF REPORT: encoded {encoded.Length} B, gate sees {info.Width}×{info.Height} {info.Codec} {info.BitDepth}-bit grid={info.IsGrid} orientation={info.Orientation}");
        string png = ImageTools.Run(real, "convert.png", 1920, default);
        using var document = ImageTools.Open(png, default);
        var pixels = ImageTools.ReadPixels(document.Bitmap, 0, default);
        int worst = 0;
        for (int i = 0; i < source.Bgra.Length; i += 4) for (int c = 0; c < 3; c++) worst = Math.Max(worst, Math.Abs(pixels.Bgra[i + c] - source.Bgra[i + c]));
        Console.WriteLine($"HEIF REPORT: real HEIC decoded {pixels.Width}×{pixels.Height}, worst channel difference {worst}");
        check(pixels.Width == 64 && pixels.Height == 48 && worst <= 48, "A HEIC made by Microsoft's encoder decodes through the image tools at its size with close colours");
    }
}
