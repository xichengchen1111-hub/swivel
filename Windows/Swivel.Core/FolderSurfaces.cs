using System;
using System.Collections.Generic;

namespace Swivel.Core;

public enum FolderSurface { Plain, Linen, PaperGrain, WoodStripes, Dots, Grid, Gradient, BrushedMetal, Stars,
    Chalkboard, Leather, Denim, Marble, Holographic, Sunset, NeonGrid, Blueprint, RetroPoster,
    Banknote, FilmStrip, Moss, Camouflage, Terrazzo, Gingham, CandyStripes, Galaxy, Notebook, EmojiPattern, Kraft
}

/// Original legacy overlays and opaque materials, all in premultiplied BGRA32.
public static class FolderSurfaces
{
    // Enum order is an existing contract. Three materials reuse their original Windows surface.
    public static readonly IReadOnlyList<string> Names = Array.AsReadOnly(new[] {
        "Plain", "Linen", "Paper grain", "Wood (wood stripes)", "Polka dots", "Grid", "Gradient", "Brushed metal", "Stars",
        "Chalkboard", "Leather", "Denim", "Marble", "Holographic", "Sunset", "Neon grid", "Blueprint", "Retro poster",
        "Banknote", "Film strip", "Moss", "Camouflage", "Terrazzo", "Gingham", "Candy stripes", "Galaxy", "Notebook", "Emoji pattern", "Kraft paper"
    });

    public static bool IsMaterial(FolderSurface surface) => surface >= FolderSurface.Chalkboard;

    public static byte[] Pixels(FolderSurface surface, int size, uint seed = 0x5349564C)
    {
        if (!Enum.IsDefined(surface)) throw new ArgumentOutOfRangeException(nameof(surface));
        if (size is < 16 or > 256) throw new ArgumentOutOfRangeException(nameof(size));
        if (IsMaterial(surface)) return MaterialPixels(surface, size, seed);
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
    // Original analytic patterns, sampled at pixel centres, with a coordinate hash for grain.
    // No font or image assets: the emoji pattern is a repeated geometric smiling face.
    private static byte[] MaterialPixels(FolderSurface surface, int size, uint seed)
    {
        var pixels = new byte[size * size * 4];
        uint Noise(int x, int y)
        {
            uint n = seed ^ (uint)surface * 0x9E3779B9u ^ (uint)x * 0x85EBCA6Bu ^ (uint)y * 0xC2B2AE35u;
            n ^= n >> 16; n *= 0x7FEB352Du; n ^= n >> 15; n *= 0x846CA68Bu; return n ^ (n >> 16);
        }
        static uint Mix(uint a, uint b, double weight)
        {
            weight = Math.Clamp(weight, 0, 1);
            uint Channel(int shift) => (uint)Math.Round(((a >> shift) & 255) * (1 - weight) + ((b >> shift) & 255) * weight);
            return Channel(16) << 16 | Channel(8) << 8 | Channel(0);
        }
        // Keep thin strokes sampleable and repeated cells at least four pixels wide.
        bool Line(double distance, double halfWidth) => Math.Abs(distance) <= Math.Max(halfWidth, 0.6 / size);
        double divisions = Math.Min(8, size / 4);
        int stitchSteps = Math.Min(64, size);
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            double u = (x + 0.5) / size, v = (y + 0.5) / size;
            double tx = u * divisions - Math.Floor(u * divisions) - 0.5, ty = v * divisions - Math.Floor(v * divisions) - 0.5;
            uint noise = Noise(x, y), rgb;
            switch (surface)
            {
                case FolderSurface.Kraft:
                    rgb = Mix(0xC4A578u, 0x775537u, (noise % 19) / 120.0);
                    if ((Noise(x / 3, y) % 31) == 0) rgb = Mix(rgb, 0x705130u, 0.35);
                    break;
                case FolderSurface.Chalkboard:
                    rgb = Mix(0x173F36u, 0x668379u, 0.12 + 0.08 * Math.Sin(u * 13 + v * 9));
                    if (noise % 53 == 0) rgb = 0xA1B3A4u;
                    break;
                case FolderSurface.Leather:
                    rgb = Mix(0x653E2Au, 0xB77E50u, (noise % 31) / 90.0);
                    if ((Line(u - 0.105, 0.015) || Line(u - 0.895, 0.015)) && (int)(v * stitchSteps) % 4 < 2 ||
                        (Line(v - 0.405, 0.015) || Line(v - 0.795, 0.015)) && (int)(u * stitchSteps) % 4 < 2) rgb = 0xE1BB89u;
                    break;
                case FolderSurface.Denim:
                    rgb = Mix(0x204878u, 0x87A9C2u, ((x + 2 * y) % 5 == 0 ? 0.55 : 0.10) + (noise % 9) / 60.0);
                    break;
                case FolderSurface.Marble:
                    double vein = Math.Abs(Math.Sin(u * 17 + v * 13 + Math.Sin(v * 20) * 0.6));
                    rgb = Mix(0xEFEDE8u, 0x667C83u, Math.Pow(1 - vein, 10) * 0.8);
                    break;
                case FolderSurface.Holographic:
                    rgb = Mix(Mix(0x7AE8DAu, 0xD18CF0u, (Math.Sin(u * 12 + v * 5) + 1) / 2), 0xF9F2BBu, (Math.Sin(v * 18 - u * 6) + 1) / 4);
                    break;
                case FolderSurface.Sunset:
                    rgb = Mix(0xC7528Bu, 0xFFCF82u, v);
                    if (Math.Pow(u - 0.64, 2) + Math.Pow(v - 0.56, 2) < 0.024) rgb = 0xFFE3A0u;
                    if (v > 0.72 + 0.035 * Math.Sin(u * 18)) rgb = 0x594D86u;
                    break;
                case FolderSurface.NeonGrid:
                    rgb = Line((0.5 - Math.Abs(tx)) / divisions, 0.07 / divisions) || Line((0.5 - Math.Abs(ty)) / divisions, 0.07 / divisions) ? 0x4DE2E0u : 0x1B173Cu;
                    if (Line(u + v - 1.1, 0.02)) rgb = 0xF466D3u;
                    break;
                case FolderSurface.Blueprint:
                    rgb = Line((0.5 - Math.Abs(tx)) / divisions, 0.04 / divisions) || Line((0.5 - Math.Abs(ty)) / divisions, 0.04 / divisions) ? 0x709BC8u : 0x22568Fu;
                    if (Line(Math.Sqrt(Math.Pow(u - 0.52, 2) + Math.Pow(v - 0.60, 2)) - 0.17, 0.009) ||
                        Line(u - 0.52, 0.004) || Line(v - 0.60, 0.004)) rgb = 0xE1EFF5u;
                    break;
                case FolderSurface.RetroPoster:
                    rgb = ((int)((u + v * 0.55) * 9) % 3) switch { 0 => 0xE8C38Cu, 1 => 0xC45A40u, _ => 0x315C66u };
                    if (noise % 19 == 0) rgb = Mix(rgb, 0xF4DCB4u, 0.4);
                    break;
                case FolderSurface.Banknote:
                    rgb = Mix(0xA8C6A2u, 0x365F4Bu, Math.Pow(Math.Abs(Math.Sin(u * 93 + Math.Sin(v * 29) * 4)), 18) * 0.55);
                    if (Line(u - 0.1, 0.012) || Line(u - 0.9, 0.012) || Line(v - 0.4, 0.01) || Line(v - 0.8, 0.01)) rgb = 0x486E51u;
                    break;
                case FolderSurface.FilmStrip:
                    rgb = 0x282D32u;
                    if (u is > 0.16 and < 0.84) rgb = (int)(v * 7) % 2 == 0 ? 0x65757Du : 0x48565Eu;
                    else if (Math.Abs(ty) < 0.23) rgb = 0xE6DFCAu;
                    break;
                case FolderSurface.Moss:
                    rgb = Mix(0x2E4B27u, 0xA6AF53u, (Math.Sin(u * 32) * Math.Cos(v * 41) + 1) / 3 + (noise % 11) / 35.0);
                    break;
                case FolderSurface.Camouflage:
                    double patch = Math.Sin(u * 21 + Math.Sin(v * 14)) + Math.Cos(v * 27 + Math.Cos(u * 12));
                    rgb = patch < -0.7 ? 0x373F2Du : patch < 0.2 ? 0x6A7651u : patch < 1 ? 0xA8A67Du : 0x56543Cu;
                    break;
                case FolderSurface.Terrazzo:
                    uint cell = Noise((int)(u * 18), (int)(v * 18));
                    double cx = u * 18 % 1 - 0.5, cy = v * 18 % 1 - 0.5;
                    rgb = Math.Abs(cx) + Math.Abs(cy * 1.4) < 0.18 + (cell % 12) / 50.0
                        ? (cell % 3) switch { 0 => 0xB87162u, 1 => 0x6E9494u, _ => 0xD1B37Eu } : 0xE8DFD1u;
                    break;
                case FolderSurface.Gingham:
                    rgb = Mix(0xFFF1E5u, 0xCD6865u, (((int)(u * 14) % 2) + ((int)(v * 14) % 2)) * 0.35);
                    break;
                case FolderSurface.CandyStripes:
                    rgb = ((int)((u + v) * 12) % 3) switch { 0 => 0xF18C9Eu, 1 => 0xFFF1D8u, _ => 0x83CCD1u };
                    break;
                case FolderSurface.Galaxy:
                    rgb = Mix(0x151C40u, 0x945BA8u, Math.Exp(-Math.Pow((u + v * 0.55 - 0.8) * 6, 2)) * (0.3 + (noise % 31) / 60.0));
                    if (noise % 97 < 2) rgb = 0xF7F3D8u;
                    break;
                case FolderSurface.Notebook:
                    rgb = Line((0.5 - Math.Abs(ty)) / divisions, 0.04 / divisions) ? 0x92B9CEu : 0xF5EDD8u;
                    if (Line(u - 0.20, 0.008)) rgb = 0xCC8F8Fu;
                    break;
                case FolderSurface.EmojiPattern:
                    rgb = 0x7DBEB3u;
                    // Expressions need at least eight pixels per face, including eyes and smile.
                    double faces = Math.Min(8, size / 8);
                    double fx = u * faces % 1 - 0.5, fy = v * faces % 1 - 0.5;
                    double radius = fx * fx + fy * fy;
                    if (radius < 0.15) rgb = 0xFFDB70u;
                    bool eye = Line((Math.Abs(fx) - 0.13) / faces, 0.055 / faces) && Line((fy + 0.1) / faces, 0.06 / faces);
                    bool smile = fy > 0.05 && Line((Math.Sqrt(fx * fx + Math.Pow(fy - 0.02, 2)) - 0.21) / faces, 0.035 / faces);
                    if (radius < 0.15 && (eye || smile)) rgb = 0x4C433Bu;
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(surface));
            }
            int i = (y * size + x) * 4;
            pixels[i] = (byte)rgb; pixels[i + 1] = (byte)(rgb >> 8); pixels[i + 2] = (byte)(rgb >> 16); pixels[i + 3] = 255;
        }
        return pixels;
    }

}
