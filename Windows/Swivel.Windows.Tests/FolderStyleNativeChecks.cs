using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
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

        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            for (int color = 0; color < FolderStyles.Colors.Count; color++)
            foreach (string mark in new[] { "", "A", "工作", "👩‍💻" })
            {
                var legacy = new FolderStyle(color, mark);
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
}
