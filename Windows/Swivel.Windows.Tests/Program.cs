using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;
using Swivel.Windows;

internal static class Program
{
    private static int checks;
    private static void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; }
    private static void Reject(Action action, string name) { bool failed = false; try { action(); } catch { failed = true; } Check(failed, name); }
    [STAThread]
    public static int Main()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Run these tests on Windows.");
        string directory = Path.Combine(Path.GetTempPath(), "swivel-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var validationApp=new App(validationHost:true);
        try
        {
            string source = Path.Combine(directory, "图像 🦜.png");
            Save(source, new PngBitmapEncoder(), PixelFormats.Bgra32, 40, 20);
            byte[] original = File.ReadAllBytes(source);
            string output = ImageTools.Run(source, "convert.jpg", 1920, CancellationToken.None);
            var image = Decode(output);
            Check(image.PixelWidth == 40 && image.PixelHeight == 20, "JPEG dimensions");
            byte[] pixel = new byte[4]; new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
            Check(pixel[0] > 245 && pixel[1] > 245 && pixel[2] > 245, "Transparent JPEG flattened to white");
            string second = ImageTools.Run(source, "convert.jpg", 1920, CancellationToken.None);
            Check(second.EndsWith(" converted 2.jpg") && File.ReadAllBytes(source).SequenceEqual(original), "No overwrite and original unchanged");
            var small = Decode(ImageTools.Run(source, "image.resize", 16, CancellationToken.None));
            Check(small.PixelWidth == 16 && small.PixelHeight == 8, "Resize exported pixels");
            var rotated = Decode(ImageTools.Run(source, "image.rotate", 1920, CancellationToken.None));
            Check(rotated.PixelWidth == 20 && rotated.PixelHeight == 40, "Rotation exported pixels");
            foreach (string action in new[] { "convert.png", "convert.bmp", "convert.tiff" })
                Check(Decode(ImageTools.Run(source, action, 1920, CancellationToken.None)).PixelWidth == 40, "Codec " + action);

            string oriented = Path.Combine(directory, "oriented.tiff");
            var metadata = new BitmapMetadata("tiff"); metadata.SetQuery("/ifd/{ushort=274}", (ushort)6);
            var orientationEncoder = new TiffBitmapEncoder();
            orientationEncoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(40, 20, 96, 96, PixelFormats.Bgr24, null, new byte[40 * 20 * 3], 40 * 3), null, metadata, null));
            using (var stream = File.Create(oriented)) orientationEncoder.Save(stream);
            var corrected = Decode(ImageTools.Run(oriented, "convert.png", 1920, CancellationToken.None));
            Check(corrected.PixelWidth == 20 && corrected.PixelHeight == 40, "EXIF orientation baked");

            foreach (var format in new[] { PixelFormats.Gray16, PixelFormats.Gray32Float, PixelFormats.Bgr101010, PixelFormats.Rgb48, PixelFormats.Cmyk32 })
                Check(!ImageTools.IsSdrFormat(format), "Reject high precision: " + format);
            string high = Path.Combine(directory, "gray16.png"); Save(high, new PngBitmapEncoder(), PixelFormats.Gray16, 40, 20);
            Reject(() => ImageTools.Run(high, "convert.png", 1920, CancellationToken.None), "16-bit PNG refused");
            string multi = Path.Combine(directory, "multiple.tiff");
            var multiEncoder = new TiffBitmapEncoder(); multiEncoder.Frames.Add(Decode(source)); multiEncoder.Frames.Add(Decode(source));
            using (var stream = File.Create(multi)) multiEncoder.Save(stream);
            Reject(() => ImageTools.Run(multi, "convert.png", 1920, CancellationToken.None), "Multi-page TIFF refused");
            string fake = Path.Combine(directory, "bad.png"); File.WriteAllText(fake, "<html>Not an image</html>");
            Reject(() => ImageTools.Run(fake, "convert.png", 1920, CancellationToken.None), "Malformed image refused");
            string animated = Path.Combine(directory, "animated.png");
            File.WriteAllBytes(animated, AnimatedPng(original));
            bool animationRejected = false;
            try { ImageTools.Run(animated, "convert.png", 1920, CancellationToken.None); }
            catch (UserError error) { animationRejected = error.Message == "Multi-frame images are not supported yet."; }
            Check(animationRejected, "APNG gets an explicit animation refusal, not a generic decode failure");

            using (var input = NativeFiles.OpenRead(source))
            {
                Reject(() => File.Move(source, source + ".moved"), "Input locked against replacement");
                Reject(() => Directory.Move(directory, directory + ".moved"), "Parent directory pinned");
                int before = Directory.GetFiles(directory).Length;
                Reject(() => NativeFiles.WriteBeside(input, "failure", "png", stream => { stream.Write(new byte[32]); throw new IOException("Injected failure"); }, CancellationToken.None), "Injected producer failure");
                Check(Directory.GetFiles(directory).Length == before, "Failure cleaned only staging file");
                using var cancel = new CancellationTokenSource();
                Reject(() => NativeFiles.WriteBeside(input, "cancelled", "png", stream => { stream.Write(new byte[32]); cancel.Cancel(); }, cancel.Token), "Cancel before publish");
                Check(Directory.GetFiles(directory).Length == before, "Cancelled staging cleaned");
            }
            var concurrent = Enumerable.Range(0, 16).Select(i => Task.Run(() => {
                using var input = NativeFiles.OpenRead(source);
                return NativeFiles.WriteBeside(input, "concurrent", "dat", stream => stream.WriteByte((byte)i), CancellationToken.None);
            })).ToArray();
            Task.WaitAll(concurrent);
            Check(concurrent.Select(x => x.Result).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 16, "Concurrent names never replace");
            Check(concurrent.Select(x => File.ReadAllBytes(x.Result)[0]).Distinct().Count() == 16, "Concurrent payloads all retained");
            Check(File.ReadAllBytes(source).SequenceEqual(original), "Original unchanged after all operations");
            Check(!Directory.GetFiles(directory, ".swivel-*.tmp").Any(), "No leaked staging");
            // Symlink creation may require Developer Mode. A skipped privilege-dependent check
            // is reported explicitly; the manual matrix also covers junctions and cloud files.
            string link = Path.Combine(directory, "linked.png");
            bool linkCreated = false;
            try { File.CreateSymbolicLink(link, source); linkCreated = true; }
            catch (UnauthorizedAccessException) { Console.WriteLine("SKIP: symlink creation requires Developer Mode or suitable privileges."); }
            catch (IOException) { Console.WriteLine("SKIP: this filesystem cannot create test symlinks."); }
            if (linkCreated) { Reject(() => NativeFiles.OpenRead(link).Dispose(), "Reject symlink input"); File.Delete(link); }
            using (var awake = new KeepAwake()) { awake.Start(TimeSpan.FromMinutes(1), false); Check(awake.Active, "Wake request started"); awake.Stop(); Check(!awake.Active, "Wake request released"); }
            ImageRecipeChecks.Run(directory, Check, Reject);
            PdfToolsChecks.Run(directory, Check, Reject);
            RetouchToolsChecks.Run(directory, Check, Reject);
            QrToolsChecks.Run(directory, Check, Reject);
            QrReadNativeChecks.Run(directory,Check,Reject);
            QrRecoverySourceNativeChecks.Run(directory,Check,Reject);
            QrRecoveryDialogNativeChecks.Run(Check,Reject);
            QrMacExportNativeChecks.Run(directory,Check,Reject);
            ClotheslineGestureNativeChecks.Run(Check,Reject);
            CommonSimilarityNativeChecks.Run(directory,Check,Reject);
            CloseWindowsNativeChecks.Run(Check,Reject);
            MoveFilesDialogNativeChecks.Run(Check,Reject);
            MoveFilesNativeChecks.Run(directory,Check,Reject);
            ZipExtractNativeChecks.Run(directory,Check,Reject);
            CopyFilesNativeChecks.Run(directory,Check,Reject);
            ImageArrivalNativeChecks.Run(directory,Check,Reject);
            ImageArrivalWindowNativeChecks.Run(Check,Reject);
            WheelGestureNativeChecks.Run(Check,Reject);
            WheelGestureLiveChecks.Run(Check,Reject);
            WheelGestureWindowNativeChecks.Run(Check,Reject);
            RenameChecks.Run(directory, Check, Reject);
            PhotoToolsChecks.Run(directory, Check, Reject);
            FileToolChecks.Run(directory, Check, Reject);
            ImageExtraChecks.Run(directory, Check, Reject);
            AppRouteChecks.Run(directory, Check, Reject);
            WebRouteChecks.Run(directory, Check, Reject);
            MigrationFileChecks.Run(directory, Check, Reject);
            RecipeControlChecks.Run(Check);
            DoctorControlChecks.Run(Check, Reject);
            MarkHistoryChecks.Run(Check);
            ExecutableStructureChecks.Run(directory,Check,Reject);
            WindowsDoctorPageChecks.Run(Check);
            MountedDriveNativeChecks.Run(Check,Reject);
            PowerPlanNativeChecks.Run(Check,Reject);
            SleepCapabilityNativeChecks.Run(Check,Reject);
            ServiceNativeChecks.Run(Check,Reject);
            ScheduledTaskNativeChecks.Run(Check,Reject);
            ScheduledTaskLiveChecks.Run(Check,Reject);
            ScheduledTaskDialogNativeChecks.Run(Check,Reject);
            CommonFolderNativeChecks.Run(Check,Reject);
            CommonDuplicateNativeChecks.Run(Check,Reject);
            StartupFolderNativeChecks.Run(Check,Reject);
            StartupApprovalNativeChecks.Run(Check,Reject);
            CacheLocationNativeChecks.Run(directory,Check,Reject);
            WaveToolsChecks.Run(directory,Check,Reject);
            FolderSizeNativeChecks.Run(directory,Check,Reject);
            DuplicateNativeChecks.Run(directory,Check,Reject);
            TextSimilarityNativeChecks.Run(directory,Check,Reject);
            PhotoSimilarityNativeChecks.Run(directory,Check,Reject);
            AssistantDraftNativeChecks.Run(directory,Check,Reject);
            PngMetadataNativeChecks.Run(directory,Check,Reject);
            JpegMetadataNativeChecks.Run(directory,Check,Reject);
            ProjectFolderNativeChecks.Run(directory,Check,Reject);
            LargeFileNativeChecks.Run(directory,Check,Reject);
            LargeFilesChecks.Run(Check);
            UsageNativeChecks.Run(Check,Reject);
            AppMemoryNativeChecks.Run(Check,Reject);
            InstalledAppsNativeChecks.Run(Check,Reject);
            StartupNativeChecks.Run(Check,Reject);
            GifNativeChecks.Run(directory,Check,Reject);
            GifExportNativeChecks.Run(directory,Check,Reject);
            GifPaletteNativeChecks.Run(directory,Check,Reject);
            QrGardenNativeChecks.Run(Check,Reject);
            AssistantNativeChecks.Run(Check,Reject);
            GifPresetNativeChecks.Run(directory,Check,Reject);
            FloatWaveNativeChecks.Run(directory,Check,Reject);
            SystemAudioNativeChecks.Run(directory,Check,Reject);
            G711NativeChecks.Run(directory,Check,Reject);
            AiffNativeChecks.Run(directory,Check,Reject);
            AiffExportNativeChecks.Run(directory,Check,Reject);
            WaveJoinNativeChecks.Run(directory,Check,Reject);
            WaveDuckingNativeChecks.Run(directory,Check,Reject);
            WaveLoopNativeChecks.Run(directory,Check,Reject);
            DocumentTextNativeChecks.Run(directory,Check,Reject);
            DocumentHtmlNativeChecks.Run(directory,Check,Reject);
            Reject(() => PCDoctor.Run(new CancellationToken(true)), "Doctor cancelled before system reads");
            if (Environment.GetEnvironmentVariable("SWIVEL_DOCTOR_LIVE") == "1")
            {
                var facts = PCDoctor.Run(CancellationToken.None);
                Check(facts.Count > 0, "Explicit live doctor snapshot");
                foreach (var fact in facts) Check(Strings.Table.ContainsKey(fact.Title) && Strings.Table.ContainsKey(fact.Format), "Live doctor labels translated");
            }
            else Console.WriteLine("SKIP: set SWIVEL_DOCTOR_LIVE=1 for explicit read-only system checks.");
            Console.WriteLine($"PASS: {checks} Windows native checks. Explorer interaction/DPI/accessibility remain manual.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { try { Directory.Delete(directory, true); } finally { validationApp.Shutdown(); } }
    }
    private static BitmapFrame Decode(string path)
    {
        using var stream = File.OpenRead(path);
        return BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
    }
    private static void Save(string path, BitmapEncoder encoder, PixelFormat format, int width, int height)
    {
        int stride = (width * format.BitsPerPixel + 7) / 8;
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, format, null, new byte[stride * height], stride)));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    private static byte[] AnimatedPng(byte[] png)
    {
        // Two actual frames using the fixture's compressed pixels, with valid CRCs and
        // sequential frame/data control numbers. A corrupt PNG cannot satisfy this test.
        using var output = new MemoryStream(); output.Write(png.AsSpan(0, 33));
        var control = new byte[8]; BinaryPrimitives.WriteUInt32BigEndian(control, 2);
        Chunk("acTL", control);
        uint sequence = 0;
        for (int frame = 0; frame < 2; frame++)
        {
            var fc = new byte[26]; BinaryPrimitives.WriteUInt32BigEndian(fc, sequence++);
            png.AsSpan(16, 8).CopyTo(fc.AsSpan(4));
            BinaryPrimitives.WriteUInt16BigEndian(fc.AsSpan(20), 1); BinaryPrimitives.WriteUInt16BigEndian(fc.AsSpan(22), 10);
            Chunk("fcTL", fc);
            for (int offset = 33; offset < png.Length;)
            {
                int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4)));
                if (png.AsSpan(offset + 4, 4).SequenceEqual("IDAT"u8))
                {
                    var payload = png.AsSpan(offset + 8, length).ToArray();
                    if (frame == 0) Chunk("IDAT", payload);
                    else
                    {
                        var data = new byte[length + 4]; BinaryPrimitives.WriteUInt32BigEndian(data, sequence++);
                        payload.CopyTo(data, 4); Chunk("fdAT", data);
                    }
                }
                offset += length + 12;
            }
        }
        Chunk("IEND", Array.Empty<byte>());
        return output.ToArray();
        void Chunk(string type, byte[] data)
        {
            byte[] name = Encoding.ASCII.GetBytes(type), word = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(word, (uint)data.Length); output.Write(word); output.Write(name); output.Write(data);
            uint crc = uint.MaxValue;
            foreach (byte value in name.Concat(data))
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
            }
            BinaryPrimitives.WriteUInt32BigEndian(word, ~crc); output.Write(word);
        }
    }
}
