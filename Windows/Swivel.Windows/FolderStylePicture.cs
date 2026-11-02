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
        // The face is 11:6. Exact-ratio bounds below 512 keep cover detail before downsampling.
        using var image = ImageTools.OpenTile(path, 506, 276, true, token);
        token.ThrowIfCancellationRequested();
        return image.Bitmap;
    }
}
