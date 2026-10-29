using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Swivel.Core;

internal static class WebPChecks
{
    private static string Folder => Path.Combine(AppContext.BaseDirectory, "Fixtures", "webp");

    /// Least-significant-bit-first writer for hand-built lossless streams.
    private sealed class Bits
    {
        private readonly List<byte> bytes = new();
        private int used = 8;
        public void Write(long value, int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (used == 8) { bytes.Add(0); used = 0; }
                if ((value >> i & 1) != 0) bytes[^1] |= (byte)(1 << used);
                used++;
            }
        }
        /// A prefix-code symbol: its canonical code is read most significant bit first.
        public void Code(int code, int length) { for (int i = length - 1; i >= 0; i--) Write(code >> i & 1, 1); }
        public byte[] ToArray() => bytes.ToArray();
    }

    private static byte[] Riff(string tag, byte[] payload)
    {
        var chunk = new List<byte>();
        chunk.AddRange(System.Text.Encoding.ASCII.GetBytes(tag)); chunk.AddRange(BitConverter.GetBytes(payload.Length)); chunk.AddRange(payload);
        if ((payload.Length & 1) != 0) chunk.Add(0);
        var file = new List<byte>();
        file.AddRange("RIFF"u8.ToArray()); file.AddRange(BitConverter.GetBytes(chunk.Count + 4)); file.AddRange("WEBP"u8.ToArray()); file.AddRange(chunk);
        return file.ToArray();
    }

    private static byte[] LosslessHeader(int width, int height, int version = 0)
    {
        var bits = new Bits();
        bits.Write(0x2F, 8); bits.Write(width - 1, 14); bits.Write(height - 1, 14); bits.Write(0, 1); bits.Write(version, 3);
        return bits.ToArray();
    }

    /// Every symbol 0..255 with length 8: a code-length code whose only symbol is 8, so each of
    /// the lengths takes no bits.
    private static void EightBitCode(Bits bits, bool limit)
    {
        bits.Write(0, 1); bits.Write(8, 4);
        for (int i = 0; i < 12; i++) bits.Write(i == 11 ? 1 : 0, 3);   // order index 11 is length 8
        if (limit) { bits.Write(1, 1); bits.Write(3, 3); bits.Write(254, 8); } else bits.Write(0, 1);
    }

    private static void SingleSymbol(Bits bits) { bits.Write(1, 1); bits.Write(0, 1); bits.Write(0, 1); bits.Write(0, 1); }

    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        // ---- Decoded pixels against Pillow (libwebp 1.6.0) on fixtures made by makewebp.py ----
        int lossless = 0, lossy = 0;
        foreach (string path in Directory.GetFiles(Folder, "*.webp").Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (name == "animated") continue;
            var image = WebPImage.Read(File.ReadAllBytes(path), default);
            var expected = File.ReadAllBytes(Path.ChangeExtension(path, ".rgba"));
            check(image.Bgra.Length == expected.Length, "WebP fixture size matches: " + name);
            int worst = 0; bool alphaSame = true;
            for (int i = 0; i < expected.Length; i += 4)
            {
                for (int c = 0; c < 3; c++) worst = Math.Max(worst, Math.Abs(image.Bgra[i + 2 - c] - expected[i + c]));
                alphaSame &= image.Bgra[i + 3] == expected[i + 3];
            }
            if (name.StartsWith("ll-", StringComparison.Ordinal)) { check(worst == 0 && alphaSame, "Lossless WebP decodes exactly like libwebp: " + name); lossless++; }
            else
            {
                // Lossy colour conversion is our own rounding: within one level of libwebp; the
                // Y'CbCr planes themselves were checked bit-exact against FFmpeg (see TESTING).
                check(worst <= 1 && alphaSame, "Lossy WebP is within one level of libwebp, alpha exact: " + name);
                lossy++;
            }
        }
        check(lossless >= 11 && lossy >= 7, "All WebP fixtures were compared");
        var animated = File.ReadAllBytes(Path.Combine(Folder, "animated.webp"));
        string Message(Action action) { try { action(); return ""; } catch (UserError error) { return error.Message; } }
        check(Message(() => WebPImage.Read(animated, default)) == WebPImage.Animated, "An animated WebP is refused with its own message");

        // ---- Hostile and damaged input ----
        var sample = File.ReadAllBytes(Path.Combine(Folder, "ly-photo-63x47-q95-alpha.webp"));
        var losslessSample = File.ReadAllBytes(Path.Combine(Folder, "ll-photo-61x33-alpha-m4.webp"));
        var random = new Random(20261029);
        int refused = 0, decoded = 0;
        foreach (var original in new[] { sample, losslessSample })
            for (int round = 0; round < 300; round++)
            {
                var damaged = (byte[])original.Clone();
                if (round % 3 == 0) damaged = damaged[..random.Next(1, damaged.Length)];
                else for (int i = 0, n = random.Next(1, 6); i < n; i++) damaged[random.Next(12, damaged.Length)] ^= (byte)(1 << random.Next(8));
                try { WebPImage.Read(damaged, default); decoded++; }
                catch (UserError) { refused++; }
            }
        check(refused + decoded == 600 && refused > 100, "Damaged WebP files are refused with a message or decode; nothing else escapes");
        reject(() => WebPImage.Read(sample[..30], default), "A truncated WebP is refused");
        check(Message(() => WebPImage.Read(Riff("VP8L", LosslessHeader(16384, 16384)), default)) == "Images are limited to 16 megapixels and 16384 pixels per side.", "A WebP over 16 megapixels is refused before decoding");
        check(Message(() => WebPImage.Read(Riff("VP8L", LosslessHeader(4, 4, version: 1)), default)) == WebPImage.Invalid, "A lossless version other than 0 is refused");
        var canvas = new byte[10]; canvas[4] = 0xFF; canvas[5] = 0xFF; canvas[7] = 0xFF; canvas[8] = 0xFF;
        check(Message(() => WebPImage.Read(Riff("VP8X", canvas), default)) == "Images are limited to 16 megapixels and 16384 pixels per side.", "A huge extended canvas is refused");
        var animatedFlag = new byte[10]; animatedFlag[0] = 0x02;
        check(Message(() => WebPImage.Read(Riff("VP8X", animatedFlag), default)) == WebPImage.Animated, "The animation flag alone is refused as animation");
        var riff = (byte[])sample.Clone(); BitConverter.GetBytes(sample.Length).CopyTo(riff, 4);
        check(Message(() => WebPImage.Read(riff, default)) == WebPImage.Invalid, "A RIFF size beyond the file is refused");
        reject(() => WebPImage.Read("RIFF\0\0\0\0WEBPVP8 "u8.ToArray(), default), "A WebP without image data is refused");
        var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        bool stopped = false;
        try { WebPImage.Read(sample, cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
        check(stopped, "Decoding a WebP observes cancellation");

        // Prefix-code tables are bounded: 65,536 used groups of four 8-bit codes need about 134 M
        // table entries; the reader refuses past 16 M instead of allocating them.
        var bits = new Bits();
        foreach (byte b in LosslessHeader(1024, 1024)) bits.Write(b, 8);
        bits.Write(0, 1);            // no transform
        bits.Write(0, 1);            // no colour cache
        bits.Write(1, 1); bits.Write(0, 3);   // meta prefix codes, 4×4 blocks → a 256×256 entropy image
        bits.Write(0, 1);            // entropy image: no colour cache
        EightBitCode(bits, true); EightBitCode(bits, false); SingleSymbol(bits); SingleSymbol(bits); SingleSymbol(bits);
        for (int i = 0; i < 65536; i++) { bits.Code(i & 255, 8); bits.Code(i >> 8, 8); }   // green, red = group i
        for (int group = 0; group < 8200; group++)
        {
            EightBitCode(bits, true); EightBitCode(bits, false); EightBitCode(bits, false); EightBitCode(bits, false); SingleSymbol(bits);
        }
        check(Message(() => WebPImage.Read(Riff("VP8L", bits.ToArray()), default)) == WebPImage.TooComplex, "A lossless WebP needing more prefix-code tables than the budget is refused");

        // Even codes with a single symbol count: 65,536 used groups of them are refused too.
        bits = new Bits();
        foreach (byte b in LosslessHeader(2048, 512)) bits.Write(b, 8);
        bits.Write(0, 1); bits.Write(0, 1); bits.Write(1, 1); bits.Write(0, 3); bits.Write(0, 1);
        EightBitCode(bits, true); EightBitCode(bits, false); SingleSymbol(bits); SingleSymbol(bits); SingleSymbol(bits);
        for (int i = 0; i < 65536; i++) { bits.Code(i & 255, 8); bits.Code(i >> 8, 8); }
        for (int group = 0; group < 65536; group++) for (int code = 0; code < 5; code++) SingleSymbol(bits);
        check(Message(() => WebPImage.Read(Riff("VP8L", bits.ToArray()), default)) == WebPImage.TooComplex, "65,536 used groups of single-symbol codes are refused, not allocated");

        // ---- Wiring ----
        check(Catalog.IsImage(@"C:\p\photo.WEBP"), "WebP is an image for the wheel");
        foreach (string text in new[] { WebPImage.Invalid, WebPImage.Animated, WebPImage.TooComplex, WebPImage.ReadNotice, GifImage.InputHint })
            check(new[] { "zh-Hans", "zh-TW", "ja-JP" }.All(language => Strings.L(text, language) != text), "WebP text is translated: " + text);
    }
}
