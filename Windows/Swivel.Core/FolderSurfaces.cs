using System;

namespace Swivel.Core;

public enum FolderSurface { Plain, Linen, PaperGrain, WoodStripes, Dots, Grid, Gradient, BrushedMetal, Stars }

/// Original, low-opacity monochrome overlays. Pbgra32 lets WPF composite over any folder colour.
public static class FolderSurfaces
{
    public static byte[] Pixels(FolderSurface surface, int size, uint seed = 0x5349564C)
    {
        if (!Enum.IsDefined(surface)) throw new ArgumentOutOfRangeException(nameof(surface));
        if (size is < 16 or > 256) throw new ArgumentOutOfRangeException(nameof(size));
        var pixels = new byte[size * size * 4];
        if (surface == FolderSurface.Plain) return pixels;
        uint state = seed == 0 ? 1 : seed;
        uint Next() { state ^= state << 13; state ^= state >> 17; state ^= state << 5; return state; }
        int step = Math.Max(3, size / 12), fine = Math.Max(2, size / 48);
        // A seeded row profile makes horizontal brushing continuous instead of white noise.
        var rows = new int[size];
        for (int y = 0; y < size; y++) rows[y] = (int)(Next() % 25) - 12;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int grain = (int)(Next() % 13) - 6;
            int dx = x % step - step / 2, dy = y % step - step / 2;
            int value = surface switch
            {
                FolderSurface.Linen => (x % fine == 0 ? 14 : -3) + (y % fine == 0 ? -12 : 3) + grain / 2,
                FolderSurface.PaperGrain => grain * 3,
                FolderSurface.WoodStripes => (int)(14 * Math.Sin(x * 0.75 / fine + 0.5 * Math.Sin(y * 2.0 / size))) + grain / 2,
                FolderSurface.Dots => dx * dx + dy * dy <= Math.Max(1, step * step / 20) ? 26 : 0,
                FolderSurface.Grid => x % step == 0 || y % step == 0 ? 22 : 0,
                FolderSurface.Gradient => (x + y) * 32 / (2 * (size - 1)) - 16,
                FolderSurface.BrushedMetal => rows[y] + grain / 3,
                FolderSurface.Stars => (Math.Abs(dx) + Math.Abs(dy) <= Math.Max(1, step / 3) && (dx == 0 || dy == 0)) ? 30 : 0,
                _ => 0,
            };
            int i = (y * size + x) * 4;
            byte alpha = (byte)Math.Abs(value);
            pixels[i] = pixels[i + 1] = pixels[i + 2] = value > 0 ? alpha : (byte)0;
            pixels[i + 3] = alpha;
        }
        return pixels;
    }
}
