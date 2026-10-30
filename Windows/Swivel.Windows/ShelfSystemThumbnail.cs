using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;

namespace Swivel.Windows;

internal static class ShelfSystemThumbnail
{
    internal const int Edge = 256;
    // https://learn.microsoft.com/windows/win32/api/shobjidl_core/nf-shobjidl_core-ishellitemimagefactory-getimage
    // Never extract a thumbnail or fall back to an icon, even for local items.
    internal const uint Flags = 0x08 | 0x10; // THUMBNAILONLY | INCACHEONLY
    internal static BitmapSource? Load(string path, CancellationToken token)
        => Load(path, token, NativeFiles.InspectShelfReference, CachedImage);
    internal static BitmapSource? Load(string path, CancellationToken token,
        Func<string, CancellationToken, ShelfReference> inspect, Func<string, uint, int, CancellationToken, BitmapSource?> cachedImage)
    {
        token.ThrowIfCancellationRequested(); ShelfReferences.ValidatePath(path);
        try
        {
            inspect(path, token); // Metadata only; placeholders are allowed for cache lookup.
            token.ThrowIfCancellationRequested();
            var bitmap = cachedImage(path, Flags, Edge, token);
            token.ThrowIfCancellationRequested(); return bitmap;
        }
        catch when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
    }
    private static BitmapSource? CachedImage(string path, uint flags, int edge, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        int initialized = CoInitializeEx(IntPtr.Zero, 2);
        if (initialized < 0) return null;
        IShellItemImageFactory? factory = null; IntPtr bitmap = IntPtr.Zero;
        try
        {
            token.ThrowIfCancellationRequested(); var iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory) < 0 || factory is null) return null;
            token.ThrowIfCancellationRequested();
            if (factory.GetImage(new Size(edge, edge), flags, out bitmap) < 0 || bitmap == IntPtr.Zero) return null;
            token.ThrowIfCancellationRequested();
            // Check the native dimensions before copying untrusted shell output.
            if (GetObject(bitmap, Marshal.SizeOf<NativeBitmap>(), out var info) == 0 ||
                info.Width < 1 || info.Height < 1 || info.Width > edge || info.Height > edge) return null;
            var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze(); token.ThrowIfCancellationRequested(); return image;
        }
        catch (Exception error) when (error is COMException or ArgumentException or InvalidOperationException or NotSupportedException)
        { token.ThrowIfCancellationRequested(); return null; }
        finally
        {
            try { if (bitmap != IntPtr.Zero) DeleteObject(bitmap); }
            finally { try { if (factory is not null) Marshal.FinalReleaseComObject(factory); } finally { CoUninitialize(); } }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private readonly struct Size(int width, int height) { public readonly int Width = width, Height = height; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeBitmap
    {
        public int Type, Width, Height, WidthBytes;
        public ushort Planes, BitsPixel;
        public IntPtr Bits;
    }
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(Size size, uint flags, out IntPtr bitmap);
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? item);
    [DllImport("ole32.dll", ExactSpelling = true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int CoInitializeEx(IntPtr reserved, uint mode);
    [DllImport("ole32.dll", ExactSpelling = true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern void CoUninitialize();
    [DllImport("gdi32.dll", EntryPoint = "GetObjectW", ExactSpelling = true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int GetObject(IntPtr bitmap, int size, out NativeBitmap info);
    [DllImport("gdi32.dll", ExactSpelling = true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr bitmap);
}

internal sealed class ShelfSystemPreviewDialog : Window
{
    private readonly Image image;
    internal ShelfSystemPreviewDialog(App app, string path, BitmapSource bitmap)
    {
        Title = app.L("System Preview…"); Width = 520; Height = 480; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(18) };
        var footer = new StackPanel(); footer.Children.Add(UI.Text(path));
        footer.Children.Add(UI.Text(app.L("Cached system thumbnail only. It may be old. Swivel does not download online-only items or load preview handlers."), 12));
        var close = UI.Button(app.L("Close"), () => Close()); close.IsCancel = true; footer.Children.Add(close);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        image = new Image { Source = bitmap, Stretch = Stretch.Uniform, MaxWidth = ShelfSystemThumbnail.Edge, MaxHeight = ShelfSystemThumbnail.Edge };
        root.Children.Add(image); Content = root;
        Closed += (_, _) => ReleaseImage();
    }
    internal void ReleaseImage() => image.Source = null;
}
