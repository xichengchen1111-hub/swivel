using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

/// Fonts for the PDF text export: a fixed list of font files that ship with Windows, read from
/// the system Fonts folder only when an earlier font lacks a character. Missing files are skipped.
internal static class PdfFonts
{
    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["segoeui.ttf"] = "Segoe UI", ["arial.ttf"] = "Arial", ["msyh.ttc"] = "Microsoft YaHei", ["msjh.ttc"] = "Microsoft JhengHei", ["YuGothR.ttc"] = "Yu Gothic",
        ["meiryo.ttc"] = "Meiryo", ["msgothic.ttc"] = "MS Gothic", ["malgun.ttf"] = "Malgun Gothic", ["simsun.ttc"] = "SimSun", ["seguisym.ttf"] = "Segoe UI Symbol", ["seguiemj.ttf"] = "Segoe UI Emoji",
    };
    internal static string Resolve(string? language) => language is null or "system" ? CultureInfo.CurrentUICulture.Name : language;
    internal static IReadOnlyList<PdfFontCandidate> For(string text, string language) =>
        DocumentPdf.FontOrder(text, language).Select(file => new PdfFontCandidate(Labels.GetValueOrDefault(file, file), token => Load(file, token))).ToArray();

    private static TrueTypeFace? Load(string file, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string folder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        if (folder.Length == 0) return null;
        byte[] data;
        try
        {
            using var stream = new FileStream(Path.Combine(folder, file), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            if (stream.Length > TrueTypeFace.MaxFontBytes) return null;
            data = new byte[stream.Length]; stream.ReadExactly(data);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or IOException) { return null; }
        token.ThrowIfCancellationRequested();
        return TrueTypeFace.Parse(data, 0);
    }
}
