using System;
using System.IO;
using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

/// Convert Subtitles / Data / Font to …: the held original is read once (within the format's size
/// limit), converted in memory and written beside it as `name.<format>`, or `name 2.<format>`…
/// when that name is taken.
internal static class SubtitleConvert
{
    public static string Run(string path, SubtitleFormats.Format format, CancellationToken token, FileStamp? expected = null)
        => Convert(path, SubtitleFormats.MaxBytes, SubtitleFormats.TooLarge, bytes => (SubtitleFormats.Convert(bytes, Path.GetExtension(path), format, token), format.Extension), token, expected);

    public static string Run(string path, DataFileFormats.Format format, CancellationToken token, FileStamp? expected = null)
        => Convert(path, DataFileFormats.MaxBytes, DataFileFormats.TooLarge, bytes => (DataFileFormats.Convert(bytes, Path.GetExtension(path), format, token), format.Extension), token, expected);

    /// Unpacking a web font gives .ttf or .otf, whichever its outlines are.
    public static string Run(string path, FontFileFormats.Format format, CancellationToken token, FileStamp? expected = null)
        => Convert(path, FontFileFormats.MaxBytes, FontFileFormats.TooLarge, bytes => FontFileFormats.Convert(bytes, format, token), token, expected);

    private static string Convert(string path, int maxBytes, string tooLarge, Func<byte[], (byte[] Bytes, string Extension)> convert, CancellationToken token, FileStamp? expected)
    {
        token.ThrowIfCancellationRequested();
        using var input = NativeFiles.OpenRead(path);
        if (expected is { } stamp && !input.Stamp.SameObjectAndContent(stamp)) throw new McpToolError(McpSession.Changed);
        if (input.Stream.Length > maxBytes) throw new UserError(tooLarge);
        var bytes = new byte[input.Stream.Length];
        input.Stream.ReadExactly(bytes);
        var (output, extension) = convert(bytes);
        token.ThrowIfCancellationRequested();
        return NativeFiles.WriteBeside(input, Path.GetFileNameWithoutExtension(path), extension, stream => stream.Write(output), token);
    }
}
