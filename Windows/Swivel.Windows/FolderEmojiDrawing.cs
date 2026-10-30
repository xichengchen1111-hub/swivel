using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Swivel.Core;

namespace Swivel.Windows;

/// WPF shapes the installed Segoe UI Emoji font. Expand its COLR/CPAL palette layers
/// into WPF glyph drawings because DrawText alone paints those glyphs monochrome.
internal static class FolderEmojiDrawing
{
    private const string Unsupported = "This emoji cannot be drawn in colour with the installed Segoe UI Emoji font. Choose another emoji.";
    private static readonly Dictionary<Uri, Palette> palettes = new(); // UI/STA rendering only

    internal static void Draw(DrawingContext target, FormattedText text, Point origin)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawText(text, origin);
        int layers = Draw(target, VisualTreeHelper.GetDrawing(visual));
        if (layers == 0) throw new UserError(Unsupported);
    }

    private static int Draw(DrawingContext target, Drawing drawing)
    {
        if (drawing is DrawingGroup group)
        {
            target.PushTransform(group.Transform);
            if (group.ClipGeometry is not null) target.PushClip(group.ClipGeometry);
            target.PushOpacity(group.Opacity);
            int count = 0;
            foreach (var child in group.Children) count += Draw(target, child);
            target.Pop();
            if (group.ClipGeometry is not null) target.Pop();
            target.Pop();
            return count;
        }
        if (drawing is not GlyphRunDrawing glyph) return 0;
        GlyphRun run = glyph.GlyphRun;
        if (!palettes.TryGetValue(run.GlyphTypeface.FontUri, out var palette))
        {
            palette = new Palette(run.GlyphTypeface);
            palettes.Add(run.GlyphTypeface.FontUri, palette);
        }
        double advance = 0;
        int drawn = 0;
        for (int i = 0; i < run.GlyphIndices.Count; i++)
        {
            ushort id = run.GlyphIndices[i];
            var offset = run.GlyphOffsets is { Count: > 0 } ? run.GlyphOffsets[i] : new Point();
            var baseline = new Point(run.BaselineOrigin.X + advance + offset.X, run.BaselineOrigin.Y - offset.Y);
            if (!palette.Layers.TryGetValue(id, out var layers))
            {
                // Shaping may leave a zero-ink joiner/variation glyph; never substitute missing artwork.
                if (id == 0 || !run.GlyphTypeface.GetGlyphOutline(id, run.FontRenderingEmSize, run.FontRenderingEmSize).Bounds.IsEmpty)
                    throw new UserError(Unsupported);
            }
            else foreach (var (layer, color) in layers)
            {
                var colored = new GlyphRun(run.GlyphTypeface, 0, false, run.FontRenderingEmSize, run.PixelsPerDip,
                    new[] { layer }, baseline, new[] { run.AdvanceWidths[i] }, null, null, null, null, null, null);
                target.DrawGlyphRun(color == 0xFFFF ? Brushes.White : palette.Colors[color], colored);
                drawn++;
            }
            advance += run.AdvanceWidths[i];
        }
        return drawn;
    }

    private sealed class Palette
    {
        internal readonly Dictionary<ushort, (ushort Glyph, ushort Color)[]> Layers = new();
        internal readonly Brush[] Colors;

        internal Palette(GlyphTypeface font)
        {
            // Only the installed system font is opened, never a user-supplied font or remote URI.
            using var stream = font.GetFontStream();
            if (stream.Length is < 12 or > 64 * 1024 * 1024) throw new UserError(Unsupported);
            var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
            int U16(int p) => p >= 0 && p <= bytes.Length - 2 ? BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(p, 2)) : throw new UserError(Unsupported);
            int U32(int p) => p >= 0 && p <= bytes.Length - 4 ? checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(p, 4))) : throw new UserError(Unsupported);
            // Segoe UI Emoji is a standalone TrueType font, not a font collection.
            if (U32(0) != 0x00010000) throw new UserError(Unsupported);
            int colr = -1, cpal = -1;
            for (int i = 0; i < U16(4); i++)
            {
                int entry = 12 + i * 16, tag = U32(entry), start = U32(entry + 8), length = U32(entry + 12);
                if (start > bytes.Length - length) throw new UserError(Unsupported);
                if (tag == 0x434F4C52) colr = start;
                if (tag == 0x4350414C) cpal = start;
            }
            // Version 1 retains the v0 layer records; unsupported paint-only fonts are refused visibly.
            if (colr < 0 || cpal < 0 || U16(colr) > 1 || U16(cpal) > 1 || U16(cpal + 4) == 0) throw new UserError(Unsupported);
            int firstColor = U16(cpal + 12), colorCount = U16(cpal + 2), colorRecords = U32(cpal + 8) + cpal;
            if (firstColor + colorCount > U16(cpal + 6)) throw new UserError(Unsupported);
            Colors = new Brush[colorCount];
            for (int i = 0; i < colorCount; i++)
            {
                int p = colorRecords + (firstColor + i) * 4;
                if (p < 0 || p > bytes.Length - 4) throw new UserError(Unsupported);
                var brush = new SolidColorBrush(Color.FromArgb(bytes[p + 3], bytes[p + 2], bytes[p + 1], bytes[p]));
                brush.Freeze(); Colors[i] = brush;
            }
            int bases = colr + U32(colr + 4), layerRecords = colr + U32(colr + 8), layerCount = U16(colr + 12);
            for (int i = 0; i < U16(colr + 2); i++)
            {
                int p = bases + i * 6, first = U16(p + 2), count = U16(p + 4);
                if (count == 0 || first + count > layerCount) throw new UserError(Unsupported);
                var layers = new (ushort Glyph, ushort Color)[count];
                for (int j = 0; j < count; j++)
                {
                    int q = layerRecords + (first + j) * 4, color = U16(q + 2);
                    if (color != 0xFFFF && color >= colorCount) throw new UserError(Unsupported);
                    layers[j] = ((ushort)U16(q), (ushort)color);
                }
                Layers.Add((ushort)U16(p), layers);
            }
        }
    }
}
