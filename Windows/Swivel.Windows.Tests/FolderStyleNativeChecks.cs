using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;
using Swivel.Windows;

internal static class FolderStyleNativeChecks
{
    private static string IconDifference(byte[] actual,byte[] expected)
    {
        int first=0;while(first<Math.Min(actual.Length,expected.Length)&&actual[first]==expected[first])first++;
        var details=new List<string>{$"ICO bytes={actual.Length}/{expected.Length}, first difference={first}"};
        for(int frame=0;frame<FolderStyles.IconSizes.Length;frame++)
        {
            int entry=6+frame*16,size=FolderStyles.IconSizes[frame];
            int aLength=BitConverter.ToInt32(actual,entry+8),aOffset=BitConverter.ToInt32(actual,entry+12);
            int bLength=BitConverter.ToInt32(expected,entry+8),bOffset=BitConverter.ToInt32(expected,entry+12);
            if(actual.AsSpan(aOffset,aLength).SequenceEqual(expected.AsSpan(bOffset,bLength)))continue;
            static byte[] Pixels(byte[] icon,int offset,int length,int size)
            {
                using var stream=new MemoryStream(icon,offset,length);
                var source=new PngBitmapDecoder(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0];
                var bitmap=new FormatConvertedBitmap(source,PixelFormats.Pbgra32,null,0);
                var result=new byte[size*size*4];bitmap.CopyPixels(result,size*4,0);return result;
            }
            byte[] a=Pixels(actual,aOffset,aLength,size),b=Pixels(expected,bOffset,bLength,size);
            int changed=0,maxDelta=0,firstPixel=-1;
            for(int i=0;i<a.Length;i+=4)
            {
                bool differs=false;for(int c=0;c<4;c++){int delta=Math.Abs(a[i+c]-b[i+c]);maxDelta=Math.Max(maxDelta,delta);differs|=delta!=0;}
                if(differs){changed++;if(firstPixel<0)firstPixel=i;}
            }
            string sample=firstPixel<0?"none":$"({firstPixel/4%size},{firstPixel/4/size}) BGRA={Convert.ToHexString(a.AsSpan(firstPixel,4))}/{Convert.ToHexString(b.AsSpan(firstPixel,4))}";
            details.Add($"{size}px PNG bytes={aLength}/{bLength}, different pixels={changed}/{size*size}, max channel delta={maxDelta}, first={sample}");
        }
        return string.Join("; ",details);
    }
    internal static void Run(string directory, Action<bool, string> check, Action<Action, string> reject)
    {
        // Windows/NTFS-only. Cross-compiling this fixture is not execution.
        string root = Path.Combine(directory, "folder-style"); Directory.CreateDirectory(root);
        string plain = Path.Combine(root, "Photos"); Directory.CreateDirectory(plain);
        File.WriteAllText(Path.Combine(plain, "keep.txt"), "keep");
        check(NativeFiles.InspectFolderStyle(plain) == FolderStyleState.None, "Native folder style: plain folder has none");
        byte[] icon = FolderStyles.Icon(new[] { (16, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 9 }) });
        NativeFiles.ApplyFolderStyle(plain, icon, default);
        string ini = Path.Combine(plain, "desktop.ini");
        var icons = Directory.GetFiles(plain, ".swivel-folder-*.ico");
        check(File.Exists(ini) && icons.Length == 1 && File.ReadAllBytes(icons[0]).SequenceEqual(icon), "Native folder style writes desktop.ini and one icon");
        var parsed = FolderStyles.ParseOurs(File.ReadAllBytes(ini));
        check(parsed is { ReadOnlyAdded: true } && parsed.Icon == Path.GetFileName(icons[0]), "Native desktop.ini is Swivel's and records the Read-only change");
        check((File.GetAttributes(ini) & (FileAttributes.Hidden | FileAttributes.System)) == (FileAttributes.Hidden | FileAttributes.System)
            && (File.GetAttributes(icons[0]) & FileAttributes.Hidden) != 0, "Native style files are hidden system files");
        check((File.GetAttributes(plain) & FileAttributes.ReadOnly) != 0, "Native styled folder is Read-only for Explorer");
        check(NativeFiles.InspectFolderStyle(plain) == FolderStyleState.Swivel, "Native inspect sees Swivel's style");
        check(!Directory.EnumerateFiles(plain, ".swivel-*.tmp").Any(), "Native folder style leaves no staging files");

        // Restyle replaces Swivel's own files and keeps the original Read-only record.
        NativeFiles.ApplyFolderStyle(plain, icon, default);
        var again = Directory.GetFiles(plain, ".swivel-folder-*.ico");
        check(again.Length == 1 && again[0] != icons[0] && FolderStyles.ParseOurs(File.ReadAllBytes(ini)) is { ReadOnlyAdded: true }, "Native restyle swaps the icon and keeps the Read-only record");

        NativeFiles.RemoveFolderStyle(plain, default);
        check(!File.Exists(ini) && !Directory.GetFiles(plain, ".swivel-folder-*.ico").Any() && File.ReadAllText(Path.Combine(plain, "keep.txt")) == "keep", "Native restore removes exactly Swivel's files");
        check((File.GetAttributes(plain) & FileAttributes.ReadOnly) == 0, "Native restore clears Read-only Swivel added");
        reject(() => NativeFiles.RemoveFolderStyle(plain, default), "Native restore without a style refused");

        // A folder that was Read-only already stays Read-only after restore.
        string already = Path.Combine(root, "Already"); Directory.CreateDirectory(already);
        File.SetAttributes(already, File.GetAttributes(already) | FileAttributes.ReadOnly);
        NativeFiles.ApplyFolderStyle(already, icon, default);
        check(FolderStyles.ParseOurs(File.ReadAllBytes(Path.Combine(already, "desktop.ini"))) is { ReadOnlyAdded: false }, "Native style records an existing Read-only folder");
        NativeFiles.RemoveFolderStyle(already, default);
        check((File.GetAttributes(already) & FileAttributes.ReadOnly) != 0, "Native restore keeps the user's own Read-only");
        // Styled while already Read-only, the user clears Read-only, then restyles: Swivel's own change is recorded.
        NativeFiles.ApplyFolderStyle(already, icon, default);
        File.SetAttributes(already, File.GetAttributes(already) & ~FileAttributes.ReadOnly);
        NativeFiles.ApplyFolderStyle(already, icon, default);
        check(FolderStyles.ParseOurs(File.ReadAllBytes(Path.Combine(already, "desktop.ini"))) is { ReadOnlyAdded: true } && !Directory.EnumerateFiles(already, ".swivel-*.tmp").Any(), "Native restyle records Read-only it adds again and leaves no moved-aside file");
        NativeFiles.RemoveFolderStyle(already, default);
        check((File.GetAttributes(already) & FileAttributes.ReadOnly) == 0, "Native restore then clears that Read-only");
        File.SetAttributes(already, File.GetAttributes(already) | FileAttributes.ReadOnly);

        // Someone else's desktop.ini is never touched.
        string foreign = Path.Combine(root, "Foreign"); Directory.CreateDirectory(foreign);
        string theirs = Path.Combine(foreign, "desktop.ini");
        File.WriteAllText(theirs, "[.ShellClassInfo]\r\nIconResource=C:\\Windows\\System32\\shell32.dll,3\r\n", Encoding.Unicode);
        byte[] before = File.ReadAllBytes(theirs);
        check(NativeFiles.InspectFolderStyle(foreign) == FolderStyleState.Other, "Native inspect sees a foreign desktop.ini");
        reject(() => NativeFiles.ApplyFolderStyle(foreign, icon, default), "Native style refuses a foreign desktop.ini");
        reject(() => NativeFiles.RemoveFolderStyle(foreign, default), "Native restore refuses a foreign desktop.ini");
        check(File.ReadAllBytes(theirs).SequenceEqual(before) && !Directory.GetFiles(foreign, ".swivel-*").Any(), "Native foreign desktop.ini and folder left unchanged");

        reject(() => NativeFiles.ApplyFolderStyle(Path.GetPathRoot(root)!, icon, default), "Native style refuses a whole drive");
        reject(() => NativeFiles.ApplyFolderStyle(plain, icon, new CancellationToken(true)), "Native style pre-cancel writes nothing");
        check(!File.Exists(ini), "Native cancelled style left no desktop.ini");
        File.SetAttributes(already, File.GetAttributes(already) & ~FileAttributes.ReadOnly); // let the test folder be cleaned up

        reject(() => NativeFiles.ApplyFolderStyle(plain, new byte[FolderStyles.MaxIconBytes + 1], default), "Native folder icon byte budget enforced before touching folder");
        check(!File.Exists(ini) && Directory.GetFiles(plain).Length == 1, "Over-budget icon writes nothing");
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            PictureChecks(root, plain, check, reject);
            DialogChecks(check);
            for (int color = 0; color < FolderStyles.Colors.Count; color++)
            foreach (string mark in new[] { "", "A", "工作", "👩‍💻" })
            {
                var legacy = new FolderStyle(color, mark);
                // Warm WPF glyph/text caches for each mark before the byte-exact comparison.
                _ = FolderStyleIcons.Icon(legacy);
                _ = LegacyFolderStyleIcons.Icon(legacy);
                byte[] actual=FolderStyleIcons.Icon(legacy),expected=LegacyFolderStyleIcons.Icon(legacy);
                bool identical=actual.SequenceEqual(expected);string detail="";
                if(!identical)
                {
                    // Diagnose repeatability; never use a retry to turn the original mismatch into a pass.
                    detail="; "+IconDifference(actual,expected)+$"; current repeat identical={actual.SequenceEqual(FolderStyleIcons.Icon(legacy))}; legacy repeat identical={expected.SequenceEqual(LegacyFolderStyleIcons.Icon(legacy))}";
                }
                check(identical, "Existing WPF icon bytes unchanged: " + color + "/" + mark + detail);
            }
            foreach (FolderSurface surface in Enum.GetValues<FolderSurface>())
            {
                var unmarked = new FolderStyle(2, Surface: surface);
                foreach (int size in FolderStyles.IconSizes)
                {
                    byte[] first = Pixels(FolderStyleIcons.Bitmap(unmarked, size));
                    check(first.SequenceEqual(Pixels(FolderStyleIcons.Bitmap(unmarked, size))), "Native material bitmap deterministic: " + surface + "/" + size);
                    if ((int)surface > 8 && size >= 24)
                    {
                        var marked = unmarked with { Badge = "A" };
                        _ = FolderStyleIcons.Bitmap(marked, size); // warm glyph cache
                        byte[] markPixels = Pixels(FolderStyleIcons.Bitmap(marked, size));
                        double min = 1, max = 0;
                        for (int y = (int)(size * 0.45); y < size * 0.75; y++)
                        for (int x = (int)(size * 0.16); x < size * 0.84; x++)
                        {
                            double luminance = Luminance(markPixels, (y * size + x) * 4);
                            min = Math.Min(min, luminance); max = Math.Max(max, luminance);
                        }
                        check((max + 0.05) / (min + 0.05) >= 4.5, "Text mark contrasts with material plate: " + surface + "/" + size);
                    }
                }
                var style = new FolderStyle(2, Emoji: "👩🏽‍💻", Surface: surface);
                byte[] rich = FolderStyleIcons.Icon(style);
                check(BitConverter.ToUInt16(rich, 4) == FolderStyles.IconSizes.Length, "Emoji/surface icon includes every size: " + surface);
                for (int i = 0; i < FolderStyles.IconSizes.Length; i++)
                {
                    int size = FolderStyles.IconSizes[i], entry = 6 + i * 16;
                    int count = BitConverter.ToInt32(rich, entry + 8), offset = BitConverter.ToInt32(rich, entry + 12);
                    using var png = new MemoryStream(rich, offset, count);
                    var decoder = new PngBitmapDecoder(png, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    check(decoder.Frames[0].PixelWidth == size && decoder.Frames[0].PixelHeight == size, "Emoji/surface PNG size: " + surface + "/" + size);
                    byte[] actual = new byte[size * size * 4], plainPixels = new byte[actual.Length];
                    FolderStyleIcons.Bitmap(style, size).CopyPixels(actual, size * 4, 0);
                    FolderStyleIcons.Bitmap(style with { Emoji = "" }, size).CopyPixels(plainPixels, size * 4, 0);
                    check(!actual.SequenceEqual(plainPixels), "Emoji visible even on small icons: " + size);
                }
                NativeFiles.ApplyFolderStyle(plain, rich, default);
                var richIni = FolderStyles.ParseOurs(File.ReadAllBytes(ini))!;
                check(File.ReadAllBytes(Path.Combine(plain, richIni.Icon)).SequenceEqual(rich), "Rich icon published intact through safe writer: " + surface);
                NativeFiles.ApplyFolderStyle(plain, rich, default);
                check(Directory.GetFiles(plain, ".swivel-folder-*.ico").Length == 1, "Rich restyle replaces only its old icon");
                NativeFiles.RemoveFolderStyle(plain, default);
                check(!File.Exists(ini) && !Directory.GetFiles(plain, ".swivel-folder-*.ico").Any() &&
                    File.ReadAllText(Path.Combine(plain, "keep.txt")) == "keep" && (File.GetAttributes(plain) & FileAttributes.ReadOnly) == 0,
                    "Rich restore leaves user files and original attributes intact: " + surface);
            }
            foreach (int size in FolderStyles.IconSizes)
            {
                var hashes = new HashSet<string>();
                foreach (FolderSurface surface in Enum.GetValues<FolderSurface>())
                    check(hashes.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Pixels(FolderStyleIcons.Bitmap(new FolderStyle(2, Surface: surface), size))))),
                        "Every material differs after clipping to folder face: " + surface + "/" + size);
            }
            foreach (string emoji in new[] { "😀", "❤️", "👍🏽", "👨‍👩‍👧‍👦" })
            {
                // Inspect emoji alone: the folder's own gradient must not fool the colour assertion.
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                    FolderEmojiDrawing.Draw(dc, new FormattedText(emoji, System.Globalization.CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, new Typeface("Segoe UI Emoji"), 64, Brushes.White, 1), new Point(0, 0));
                var bitmap = new RenderTargetBitmap(128, 128, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
                byte[] pixels = new byte[128 * 128 * 4]; bitmap.CopyPixels(pixels, 128 * 4, 0);
                check(Enumerable.Range(0, 128 * 128).Any(i => pixels[i * 4 + 3] > 100 &&
                    (pixels[i * 4] != pixels[i * 4 + 1] || pixels[i * 4 + 1] != pixels[i * 4 + 2])), "Installed emoji font renders coloured glyph layers: " + emoji);
            }
            byte[] drawn = FolderStyleIcons.Icon(new FolderStyle(3, "旅"));
            check(BitConverter.ToUInt16(drawn, 4) == FolderStyles.IconSizes.Length && drawn.Length > 1000, "Native folder icon renders every size as PNG: " + drawn.Length + " bytes");
        }
    }
    private static byte[] Pixels(BitmapSource bitmap)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0); return pixels;
    }

    private static double Luminance(byte[] pixels, int i)
    {
        static double Linear(byte b) { double c = b / 255.0; return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
        return Linear(pixels[i + 2]) * 0.2126 + Linear(pixels[i + 1]) * 0.7152 + Linear(pixels[i]) * 0.0722;
    }

    private static void PictureChecks(string root, string folder, Action<bool, string> check, Action<Action, string> reject)
    {
        string path = Path.Combine(root, "picture.png");
        // Red / green / blue bands: cover should retain the centre green band without stretching.
        var data = new byte[300 * 40 * 4];
        for (int y = 0; y < 40; y++) for (int x = 0; x < 300; x++)
        { int i = (y * 300 + x) * 4; data[i + (x < 100 ? 2 : x < 200 ? 1 : 0)] = 255; data[i + 3] = 255; }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(300, 40, 96, 96, PixelFormats.Bgra32, null, data, 1200)));
        using (var output = new FileStream(path, FileMode.CreateNew)) encoder.Save(output);
        byte[] original = File.ReadAllBytes(path);
        var picture = FolderStylePicture.Load(path, default);
        check(picture.IsFrozen && picture.PixelWidth <= 512 && picture.PixelHeight <= 512, "Picture loader returns bounded frozen pixel snapshot");
        check(File.ReadAllBytes(path).SequenceEqual(original), "Picture input unchanged");
        File.Delete(path); // rendering/export must work after the source is gone
        var style = new FolderStyle(0);
        foreach (int size in FolderStyles.IconSizes)
        {
            byte[] pixels = Pixels(FolderStyleIcons.Bitmap(style, size, picture));
            int centre = ((int)(size * 0.6) * size + size / 2) * 4;
            int left = ((int)(size * 0.6) * size + (int)(size * 0.2)) * 4;
            check(pixels[centre + 1] > 240 && pixels[centre] < 10 && pixels[centre + 2] < 10 && pixels[left + 1] > 240,
                "Picture uses centred cover instead of stretching the full source: " + size);
            check(pixels[3] == 0 && pixels[((int)(size * 0.26) * size + (int)(size * 0.15)) * 4 + 3] > 0,
                "Picture clipped to face, leaving tab and transparent outside intact: " + size);
            if (size >= 24)
            {
                var marked = style with { Badge = "A" };
                _ = FolderStyleIcons.Bitmap(marked, size, picture);
                byte[] mark = Pixels(FolderStyleIcons.Bitmap(marked, size, picture));
                check(!pixels.SequenceEqual(mark), "Picture supports an optional text mark: " + size);
                int backing = ((int)(size * 0.60) * size + (int)(size * 0.18)) * 4;
                check(Luminance(mark, backing) < 0.02, "Picture mark has opaque black contrast backing: " + size);
            }
        }
        foreach (FolderStyle chosen in new[] { style, style with { Badge = "A" }, style with { Emoji = "😀" } })
        {
            _ = FolderStyleIcons.Icon(chosen, picture); // warm each mark before exact comparisons
            byte[] icon = FolderStyleIcons.Icon(chosen, picture);
            check(icon.SequenceEqual(FolderStyleIcons.Icon(chosen, picture)) && icon.Length <= FolderStyles.MaxIconBytes,
                "Picture ICO is deterministic and within byte budget");
            var frames = IcoFile.Read(icon);
            check(frames.Select(f => f.Size).SequenceEqual(FolderStyles.IconSizes) && frames.All(f => f.Png), "Picture ICO has existing eight PNG sizes");
            NativeFiles.ApplyFolderStyle(folder, icon, default);
            var ini = FolderStyles.ParseOurs(File.ReadAllBytes(Path.Combine(folder, "desktop.ini")))!;
            check(File.ReadAllBytes(Path.Combine(folder, ini.Icon)).SequenceEqual(icon) && Directory.GetFiles(folder).Length == 3,
                "Picture publishes only ICO and desktop.ini, without original or path file");
            NativeFiles.RemoveFolderStyle(folder, default);
            check(Directory.GetFiles(folder).Select(Path.GetFileName).SequenceEqual(new[] { "keep.txt" }), "Picture restore leaves user files intact");
        }
        foreach (bool portrait in new[] { true, false })
        {
            int width = portrait ? 800 : 16000, height = portrait ? 16000 : 800;
            var crop = portrait ? new PixelCrop(0, 7782, 800, 436) : new PixelCrop(7267, 0, 1466, 799);
            var bands = new byte[width * height * 4];
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                bool retained = x >= crop.X && x < crop.X + crop.Width && y >= crop.Y && y < crop.Y + crop.Height;
                bands[i + (retained ? 1 : 2)] = 255;
                // A gradient records which part of the centred source area survived scaling.
                bands[i] = (byte)(255L * (portrait ? y - crop.Y : x - crop.X) / (portrait ? crop.Height : crop.Width));
                bands[i + 3] = 255;
            }
            string extremePath = Path.Combine(root, portrait ? "picture-tall.png" : "picture-wide.png");
            var extremeEncoder = new PngBitmapEncoder();
            extremeEncoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bands, width * 4)));
            using (var output = new FileStream(extremePath, FileMode.CreateNew)) extremeEncoder.Save(output);
            var snapshot = FolderStylePicture.Load(extremePath, default);
            check(snapshot.IsFrozen && snapshot.PixelWidth == 506 && snapshot.PixelHeight == 276,
                "Extreme picture crops before bounding the frozen snapshot: " + portrait);
            byte[] retainedPixels = Pixels(snapshot);
            check(Enumerable.Range(0, snapshot.PixelWidth * snapshot.PixelHeight).All(i => retainedPixels[i * 4 + 1] == 255 && retainedPixels[i * 4 + 2] == 0),
                "Extreme picture snapshot retains only the face crop, without outside bands: " + portrait);
            int first = portrait ? (snapshot.PixelWidth / 2) * 4 : (snapshot.PixelHeight / 2 * snapshot.PixelWidth) * 4;
            int last = portrait ? ((snapshot.PixelHeight - 1) * snapshot.PixelWidth + snapshot.PixelWidth / 2) * 4
                : (snapshot.PixelHeight / 2 * snapshot.PixelWidth + snapshot.PixelWidth - 1) * 4;
            check(retainedPixels[first] <= 2 && retainedPixels[last] >= 252,
                "Extreme picture snapshot spans the complete centred source crop: " + portrait);
            File.Delete(extremePath);
        }
        string animated = Path.Combine(root, "picture-animated.gif");
        File.WriteAllBytes(animated, Convert.FromBase64String("R0lGODlhBAACAIEAAP8AAAD/AAAA/////yH5BAEAAAIALAAAAAAEAAIAAAIHBENxHMUQBQAsAAAAAAQAAgAAAgcEQ3EcxRAFADs="));
        bool refusedAnimation = false;
        try { FolderStylePicture.Load(animated, default); }
        catch (UserError error) { refusedAnimation = Strings.Table.ContainsKey(error.Message); }
        check(refusedAnimation, "Animated picture refused with translated error");
        string unsupported = Path.Combine(root, "picture.svg"); File.WriteAllText(unsupported, "<svg/>");
        bool refusedUnsupported = false;
        try { FolderStylePicture.Load(unsupported, default); }
        catch (UserError error) { refusedUnsupported = Strings.Table.ContainsKey(error.Message); }
        check(refusedUnsupported, "Unsupported picture refused with translated error");
        reject(() => FolderStylePicture.Load(animated, new CancellationToken(true)), "Picture pre-cancellation checked before decoding");
        reject(() => FolderStylePicture.Load(@"https://example.com/picture.png", default), "Picture refuses network URL");
        string multi = Path.Combine(root, "picture-multiple.tiff");
        var tiff = new TiffBitmapEncoder(); tiff.Frames.Add(BitmapFrame.Create(picture)); tiff.Frames.Add(BitmapFrame.Create(picture));
        using (var output = new FileStream(multi, FileMode.CreateNew)) tiff.Save(output);
        reject(() => FolderStylePicture.Load(multi, default), "Picture refuses multi-page TIFF");
        string damaged = Path.Combine(root, "picture-damaged.png"); File.WriteAllBytes(damaged, new byte[20]);
        reject(() => FolderStylePicture.Load(damaged, default), "Picture refuses damaged PNG");
        // Worst-case entropy source: the eight-size output must still fit without dropping sizes.
        var noise = new byte[512 * 512 * 4]; uint state = 123;
        for (int i = 0; i < noise.Length; i++) { state ^= state << 13; state ^= state >> 17; state ^= state << 5; noise[i] = i % 4 == 3 ? (byte)255 : (byte)state; }
        var noisy = BitmapSource.Create(512, 512, 96, 96, PixelFormats.Bgra32, null, noise, 2048); noisy.Freeze();
        check(FolderStyleIcons.Icon(style, noisy).Length <= FolderStyles.MaxIconBytes, "High-entropy picture fits ICO byte budget");
    }

    private static void DialogChecks(Action<bool, string> check)
    {
        if (Application.Current is not App app) throw new InvalidOperationException("Folder dialog checks need the validation App.");
        var dialog = new FolderStyleDialog(app, new[] { (@"C:\Photos", FolderStyleState.None, (string?)null) });
        try
        {
            static IEnumerable<DependencyObject> Children(DependencyObject parent)
            {
                yield return parent;
                foreach (object child in LogicalTreeHelper.GetChildren(parent))
                    if (child is DependencyObject node) foreach (var item in Children(node)) yield return item;
            }
            dialog.Measure(new Size(420, 330)); dialog.Arrange(new Rect(0, 0, 420, 330)); dialog.UpdateLayout();
            var controls = Children(dialog).ToArray();
            var picker = controls.OfType<ComboBox>().Single(c => AutomationProperties.GetName(c) == app.L("Folder surface"));
            check(picker.Items.Count == FolderSurfaces.Names.Count && picker.MaxDropDownHeight <= 240,
                "Dialog exposes every named material in a bounded scrollable picker");
            check(picker.ActualWidth <= 340 && picker.ActualWidth > 0, "Material picker fits existing minimum window width");
            check(controls.OfType<Button>().Any(b => b.Content is string text && text == app.L("Choose Picture…")) &&
                controls.OfType<Button>().Any(b => b.Content is string text && text == app.L("Remove picture")), "Picture choose/remove controls available");
            foreach (string language in new[] { "zh-Hans", "zh-Hant", "ja" })
                check(FolderSurfaces.Names.All(n => Strings.L(n, language) != n), "Every material name translated: " + language);
            for (int i = 0; i < picker.Items.Count; i++) { picker.SelectedIndex = i; check(dialog.Chosen.Surface == (FolderSurface)i, "Material selection reaches preview and chosen style: " + i); }
        }
        finally { dialog.Close(); dialog.FinishAsync().GetAwaiter().GetResult(); }
    }

}
