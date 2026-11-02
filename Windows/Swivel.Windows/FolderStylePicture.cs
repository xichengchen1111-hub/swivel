using System.Threading;
using System.Windows.Media.Imaging;

namespace Swivel.Windows;

/// A session-only, bounded pixel snapshot. No path survives decoding, and the input is never copied.
internal static class FolderStylePicture
{
    internal const string Unsupported = "Choose a supported static local image. Animated, damaged or unsupported pictures cannot be used.";

    internal static BitmapSource Load(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // The shared loader rejects animation, unsafe local paths and oversized/unsupported input.
        using var image = ImageTools.OpenTile(path, 512, 512, false, token);
        token.ThrowIfCancellationRequested();
        return image.Bitmap;
    }
}
