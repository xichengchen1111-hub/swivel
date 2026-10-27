using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace Swivel;

/// One monitor in physical pixels, with its DPI scale (1 = 96 DPI). Windows' counterpart of the NSScreen the Mac
/// code asks for: `Bounds` is the whole monitor, `Work` excludes the taskbar.
readonly record struct RopeMonitor(Int32Rect Bounds, Int32Rect Work, double Scale, IntPtr Handle)
{
    public bool Contains(Native.POINT p) =>
        p.X >= Bounds.X && p.X < Bounds.X + Bounds.Width && p.Y >= Bounds.Y && p.Y < Bounds.Y + Bounds.Height;
}

/// Win32 calls only the clothesline needs (Core's `Native` has the shared ones).
static partial class RopeNative
{
    // MARK: Monitors

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public int Size; public RECT Monitor; public RECT Work; public uint Flags; }

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromPoint(Native.POINT pt, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    /// The monitor under a point (or the nearest one).
    public static RopeMonitor MonitorAt(Native.POINT point)
    {
        var handle = MonitorFromPoint(point, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (handle == IntPtr.Zero || !GetMonitorInfo(handle, ref info))
        {
            var w = (int)SystemParameters.PrimaryScreenWidth; var h = (int)SystemParameters.PrimaryScreenHeight;
            return new RopeMonitor(new Int32Rect(0, 0, w, h), new Int32Rect(0, 0, w, h), 1, IntPtr.Zero);
        }
        double scale = GetDpiForMonitor(handle, 0 /* MDT_EFFECTIVE_DPI */, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1;
        static Int32Rect R(RECT r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        return new RopeMonitor(R(info.Monitor), R(info.Work), scale, handle);
    }

    public static RopeMonitor MonitorUnderPointer() => MonitorAt(Native.CursorPixels());

    // MARK: Windows

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    private static partial IntPtr WindowFromPoint(Native.POINT point);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int GetClassName(IntPtr hwnd, char* name, int max);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    public static partial uint GetDoubleClickTime();

    /// Puts a window exactly on a rectangle of physical pixels, topmost, without activating it. The first move
    /// may cross onto a monitor with another DPI (WPF then rescales the window), so it is placed a second time.
    public static void Place(Window window, Int32Rect rect)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        const uint flags = 0x0010 /* SWP_NOACTIVATE */ | 0x0200 /* SWP_NOOWNERZORDER */;
        var topmost = new IntPtr(-1);
        SetWindowPos(hwnd, topmost, rect.X, rect.Y, rect.Width, rect.Height, flags);
        SetWindowPos(hwnd, topmost, rect.X, rect.Y, rect.Width, rect.Height, flags);
    }

    /// Moves a window without resizing it.
    public static void Move(Window window, int x, int y)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, new IntPtr(-1), x, y, 0, 0, 0x0001 /* SWP_NOSIZE */ | 0x0010 | 0x0200);
    }

    /// The top-level window under a screen point.
    public static IntPtr TopWindowAt(Native.POINT point)
    {
        var hwnd = WindowFromPoint(point);
        return hwnd == IntPtr.Zero ? hwnd : GetAncestor(hwnd, 2 /* GA_ROOT */);
    }

    public static unsafe string ClassName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        char* buffer = stackalloc char[256];
        int n = GetClassName(hwnd, buffer, 256);
        return n > 0 ? new string(buffer, 0, n) : "";
    }

    /// Whether a drop at this point went into a folder: a File Explorer window or the desktop. (Windows doesn't tell
    /// the drag source where its files went; this stands in for the Mac's "moved into a folder" drag operation.)
    public static bool IsFolderWindowAt(Native.POINT point) =>
        ClassName(TopWindowAt(point)) is "CabinetWClass" or "ExploreWClass" or "Progman" or "WorkerW";

    // MARK: Hotkeys and the clipboard

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(IntPtr hwnd, int id);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AddClipboardFormatListener(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RemoveClipboardFormatListener(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    public static partial uint GetClipboardSequenceNumber();

    // MARK: Known folders

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(in Guid id, uint flags, IntPtr token, out IntPtr path);

    static readonly Guid ScreenshotsFolderId = new("b7bede81-df94-4682-a7d8-57a52620b86f");

    static readonly Guid DownloadsFolderId = new("374de290-123f-4565-9164-39c4925e467b");

    /// The user's Screenshots folder (where Win + Print Screen and the Snipping Tool save), or Pictures\Screenshots.
    public static string ScreenshotsFolder() =>
        KnownFolder(ScreenshotsFolderId) ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");

    /// The user's Downloads folder (it can be moved, so not always UserProfile\Downloads). Used by file search.
    public static string DownloadsFolder() =>
        KnownFolder(DownloadsFolderId) ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    static string? KnownFolder(Guid id)
    {
        try
        {
            if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var p) == 0 && p != IntPtr.Zero)
            {
                try
                {
                    var path = Marshal.PtrToStringUni(p);
                    if (!string.IsNullOrEmpty(path)) return path;
                }
                finally { Marshal.FreeCoTaskMem(p); }
            }
        }
        catch (Exception) { }
        return null;
    }

    // MARK: Drag images (the shell's drag-image helper, so the print itself follows the pointer)

    [StructLayout(LayoutKind.Sequential)]
    internal struct SHDRAGIMAGE
    {
        public int Width, Height;
        public int OffsetX, OffsetY;
        public IntPtr Bitmap;
        public uint ColorKey;
    }

    [GeneratedComInterface]
    [Guid("DE5BF786-477A-11D2-839D-00C04FD918D0")]
    internal partial interface IDragSourceHelper
    {
        [PreserveSig] int InitializeFromBitmap(ref SHDRAGIMAGE image, IntPtr dataObject);
        [PreserveSig] int InitializeFromWindow(IntPtr hwnd, ref Native.POINT point, IntPtr dataObject);
    }

    [GeneratedComInterface]
    [Guid("4657278B-411B-11D2-839A-00C04FD918D0")]
    internal partial interface IDropTargetHelper
    {
        [PreserveSig] int DragEnter(IntPtr hwnd, IntPtr dataObject, ref Native.POINT point, int effect);
        [PreserveSig] int DragLeave();
        [PreserveSig] int DragOver(ref Native.POINT point, int effect);
        [PreserveSig] int Drop(IntPtr dataObject, ref Native.POINT point, int effect);
        [PreserveSig] int Show([MarshalAs(UnmanagedType.Bool)] bool show);
    }

    static readonly Guid DragDropHelperClass = new("4657278A-411B-11D2-839A-00C04FD918D0");

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, IntPtr outer, uint context, in Guid iid, out IntPtr instance);

    static readonly StrategyBasedComWrappers wrappers = new();

    static T? Helper<T>() where T : class
    {
        try
        {
            var iid = typeof(T).GUID;
            if (CoCreateInstance(DragDropHelperClass, IntPtr.Zero, 1 /* CLSCTX_INPROC_SERVER */, iid, out var unknown) != 0) return null;
            try { return (T)wrappers.GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.None); }
            finally { Marshal.Release(unknown); }
        }
        catch (Exception) { return null; }
    }

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateDIBSection(IntPtr hdc, in BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    /// The managed WPF DataObject as the COM IDataObject the shell helpers expect.
    static IntPtr ComPointer(object data)
    {
        if (data is not ComDataObject com) return IntPtr.Zero;
        return Marshal.GetComInterfaceForObject(com, typeof(ComDataObject));
    }

    /// Attaches a picture (premultiplied BGRA, any DPI) to a drag's data, with the pointer at `hotspot` pixels.
    /// Without it, Windows shows only the pointer; any failure leaves the drag working without the picture.
    public static void SetDragImage(System.Windows.DataObject data, BitmapSource image, Point hotspot)
    {
        IntPtr bitmap = IntPtr.Zero, com = IntPtr.Zero;
        try
        {
            var helper = Helper<IDragSourceHelper>();
            if (helper is null) return;
            var source = image.Format == PixelFormats.Pbgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Pbgra32, null, 0);
            int w = source.PixelWidth, h = source.PixelHeight, stride = w * 4;
            var pixels = new byte[stride * h];
            source.CopyPixels(pixels, stride, 0);
            var header = new BITMAPINFOHEADER { Size = Marshal.SizeOf<BITMAPINFOHEADER>(), Width = w, Height = h, Planes = 1, BitCount = 32 };
            bitmap = CreateDIBSection(IntPtr.Zero, header, 0, out var bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero) return;
            // Bottom-up rows, as the helper expects.
            for (int y = 0; y < h; y++) Marshal.Copy(pixels, y * stride, bits + (h - 1 - y) * stride, stride);
            var drag = new SHDRAGIMAGE
            {
                Width = w, Height = h,
                OffsetX = (int)Math.Clamp(hotspot.X, 0, w - 1), OffsetY = (int)Math.Clamp(hotspot.Y, 0, h - 1),
                Bitmap = bitmap, ColorKey = 0xFFFFFFFF,
            };
            com = ComPointer(data);
            if (com == IntPtr.Zero) return;
            if (helper.InitializeFromBitmap(ref drag, com) == 0) bitmap = IntPtr.Zero; // the helper owns it now
        }
        catch (Exception ex) { Log.Write($"Clothesline drag image: {ex.Message}"); }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (com != IntPtr.Zero) Marshal.Release(com);
        }
    }

    /// Keeps the shell's drag image drawn while a drag passes over one of Swivel's own windows.
    public sealed class DropImage
    {
        IDropTargetHelper? helper;

        public void Enter(Window window, object data, int effect)
        {
            IntPtr com = IntPtr.Zero;
            try
            {
                helper ??= Helper<IDropTargetHelper>();
                com = ComPointer(data);
                if (helper is null || com == IntPtr.Zero) return;
                var p = Native.CursorPixels();
                helper.DragEnter(new WindowInteropHelper(window).Handle, com, ref p, effect);
            }
            catch (Exception) { }
            finally { if (com != IntPtr.Zero) Marshal.Release(com); }
        }

        public void Over(int effect)
        {
            try { var p = Native.CursorPixels(); helper?.DragOver(ref p, effect); } catch (Exception) { }
        }

        public void Leave()
        {
            try { helper?.DragLeave(); } catch (Exception) { }
        }

        public void Drop(object data, int effect)
        {
            IntPtr com = IntPtr.Zero;
            try
            {
                com = ComPointer(data);
                var p = Native.CursorPixels();
                if (com != IntPtr.Zero) helper?.Drop(com, ref p, effect);
            }
            catch (Exception) { }
            finally { if (com != IntPtr.Zero) Marshal.Release(com); }
        }
    }
}

/// Small pictures of images and the first frame of movies, as the Mac's `Clothesline.thumbnail`.
static class RopeThumbnails
{
    public static bool IsMovie(string path) => FileKinds.Ext(path) is "mov" or "mp4";

    /// An image's thumbnail now (null for movies and unreadable files).
    public static BitmapSource? Image(string path, int maxPixels)
    {
        if (IsMovie(path)) return null;
        try
        {
            var image = Images.Load(path, maxPixels);
            if (image is { PixelWidth: > 0 }) { if (image.CanFreeze && !image.IsFrozen) image.Freeze(); return image; }
        }
        catch (Exception) { /* The converters' loader isn't there or can't read it: WPF's own decoder below. */ }
        return Decode(path, maxPixels);
    }

    /// Any thumbnail: images at once, movies through the video module.
    public static async Task<BitmapSource?> LoadAsync(string path, int maxPixels)
    {
        if (!IsMovie(path)) return Image(path, maxPixels);
        try
        {
            var frame = await Video.Thumbnail(path, maxPixels);
            if (frame.CanFreeze && !frame.IsFrozen) frame.Freeze();
            return frame;
        }
        catch (Exception) { return null; }
    }

    /// WPF's decoder, downscaled while decoding, with EXIF orientation applied.
    public static BitmapSource? Decode(string path, int maxPixels)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            BitmapSource frame = decoder.Frames[0];
            int orientation = 1;
            try
            {
                if (frame.Metadata is BitmapMetadata meta && meta.GetQuery("System.Photo.Orientation") is ushort o) orientation = o;
            }
            catch (Exception) { }
            double longest = Math.Max(frame.PixelWidth, frame.PixelHeight);
            if (longest > maxPixels && longest > 0)
            {
                double s = maxPixels / longest;
                frame = new TransformedBitmap(frame, new ScaleTransform(s, s));
            }
            double angle = orientation switch { 3 => 180, 6 => 90, 8 => 270, _ => 0 };
            if (angle != 0) frame = new TransformedBitmap(frame, new RotateTransform(angle));
            var result = new WriteableBitmap(new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0));
            result.Freeze();
            return result;
        }
        catch (Exception) { return null; }
    }

    /// A file's picture aspect from its header (no full decode), so a drop's gap matches the print to come.
    public static double Aspect(string path)
    {
        if (IsMovie(path)) return 16.0 / 9;
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0) return 1.5;
            int orientation = 1;
            try { if (frame.Metadata is BitmapMetadata meta && meta.GetQuery("System.Photo.Orientation") is ushort o) orientation = o; } catch (Exception) { }
            double w = frame.PixelWidth, h = frame.PixelHeight;
            return orientation >= 5 ? h / w : w / h;
        }
        catch (Exception) { return 1.5; }
    }
}
