using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Swivel;

/// The frosted backdrop behind the wheel and the list (the Mac's NSGlassEffectView / NSVisualEffectView).
///
/// WPF windows can't use the system acrylic for a shaped, per-pixel-transparent window (DWM backdrops fill the whole
/// window rectangle), so the menu takes a small snapshot of the desktop under itself as it opens, blurs it once on
/// the CPU at quarter resolution, and draws it clipped to the disc or rounded rect under a light tint. The snapshot
/// excludes layered windows (the drag image, the menu itself). With "Transparency effects" off it draws a solid fill.
sealed partial class GlassBackdrop : FrameworkElement
{
    /// The shape, in this element's coordinates.
    public Rect Shape { get; set; }
    public double Radius { get; set; }
    /// The blurred desktop and where it goes in this element's coordinates.
    public ImageSource? Snapshot { get; set; }
    public Rect SnapshotRect { get; set; }
    public bool Dark { get; set; }
    public bool Ellipse { get; set; }

    protected override void OnRender(DrawingContext dc)
    {
        if (Shape.IsEmpty || Shape.Width <= 0) return;
        Geometry shape = Ellipse
            ? new EllipseGeometry(Shape)
            : new RectangleGeometry(Shape, Radius, Radius);
        shape.Freeze();
        if (Native.TransparencyOff)
        {
            dc.DrawGeometry(Look.Brush(Dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3)), null, shape);
            return;
        }
        bool blurred = Snapshot != null;
        if (blurred)
        {
            dc.PushClip(shape);
            dc.DrawImage(Snapshot, SnapshotRect);
            dc.Pop();
        }
        // A light tint like a HUD over the blur; stronger without a snapshot so labels stay legible.
        var tint = Dark ? Look.Gray(0.12, blurred ? 0.42 : 0.72) : Look.Gray(0.98, blurred ? 0.4 : 0.72);
        var edge = Dark ? Look.Gray(1, 0.12) : Look.Gray(1, 0.55);
        dc.DrawGeometry(Look.Brush(tint), Painter.Pen(edge, 1), shape);
    }

    // MARK: Snapshot

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [LibraryImport("user32.dll")] private static partial IntPtr GetDC(IntPtr hwnd);
    [LibraryImport("user32.dll")] private static partial int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [LibraryImport("gdi32.dll")] private static partial IntPtr CreateCompatibleDC(IntPtr dc);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteDC(IntPtr dc);
    [LibraryImport("gdi32.dll")] private static partial IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteObject(IntPtr obj);
    [LibraryImport("gdi32.dll")] private static partial int SetStretchBltMode(IntPtr dc, int mode);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool StretchBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, uint rop);
    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    /// A blurred quarter-size snapshot of a screen rectangle (physical pixels), or null if the screen can't be read.
    public static BitmapSource? Capture(int x, int y, int width, int height)
    {
        int w = Math.Max(width / 4, 1), h = Math.Max(height / 4, 1);
        IntPtr screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return null;
        IntPtr memory = CreateCompatibleDC(screen), bitmap = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            var info = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32,
            };
            bitmap = CreateDIBSection(screen, ref info, 0, out var bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero) return null;
            old = SelectObject(memory, bitmap);
            SetStretchBltMode(memory, 4 /* HALFTONE */);
            // SRCCOPY without CAPTUREBLT: layered windows (the drag image, this menu) aren't in the picture.
            if (!StretchBlt(memory, 0, 0, w, h, screen, x, y, width, height, 0x00CC0020)) return null;
            var pixels = new byte[w * h * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            for (int pass = 0; pass < 3; pass++) BoxBlur(pixels, w, h, 4);
            var source = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, pixels, w * 4);
            source.Freeze();
            return source;
        }
        catch (Exception) { return null; }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(memory, old);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// One horizontal and one vertical box-blur pass (edges clamp). Three passes approximate a Gaussian.
    public static void BoxBlur(byte[] px, int w, int h, int r)
    {
        var line = new int[Math.Max(w, h) * 4];
        void Pass(int count, int length, Func<int, int, int> index)
        {
            for (int a = 0; a < count; a++)
            {
                for (int i = 0; i < length; i++)
                {
                    int o = index(a, i);
                    line[i * 4] = px[o]; line[i * 4 + 1] = px[o + 1]; line[i * 4 + 2] = px[o + 2];
                }
                int s0 = 0, s1 = 0, s2 = 0, n = 2 * r + 1;
                for (int k = -r; k <= r; k++)
                {
                    int j = Math.Clamp(k, 0, length - 1) * 4;
                    s0 += line[j]; s1 += line[j + 1]; s2 += line[j + 2];
                }
                for (int i = 0; i < length; i++)
                {
                    int o = index(a, i);
                    px[o] = (byte)(s0 / n); px[o + 1] = (byte)(s1 / n); px[o + 2] = (byte)(s2 / n); px[o + 3] = 255;
                    int add = Math.Min(i + r + 1, length - 1) * 4, remove = Math.Max(i - r, 0) * 4;
                    s0 += line[add] - line[remove]; s1 += line[add + 1] - line[remove + 1]; s2 += line[add + 2] - line[remove + 2];
                }
            }
        }
        Pass(h, w, (row, i) => (row * w + i) * 4);
        Pass(w, h, (col, i) => (i * w + col) * 4);
    }
}
