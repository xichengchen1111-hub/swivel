using System;
using System.IO;
using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

/// Convert Subtitles to …: the held original is read once (at most 8 MB), converted in memory and
/// written beside it as `name.<format>`, or `name 2.<format>`… when that name is taken.
internal static class SubtitleConvert
{
    public static string Run(string path, SubtitleFormats.Format format, CancellationToken token, FileStamp? expected = null)
    {
        token.ThrowIfCancellationRequested();
        using var input = NativeFiles.OpenRead(path);
        if (expected is { } stamp && !input.Stamp.SameObjectAndContent(stamp)) throw new McpToolError(McpSession.Changed);
        if (input.Stream.Length > SubtitleFormats.MaxBytes) throw new UserError(SubtitleFormats.TooLarge);
        var bytes = new byte[input.Stream.Length];
        input.Stream.ReadExactly(bytes);
        var output = SubtitleFormats.Convert(bytes, Path.GetExtension(path), format, token);
        token.ThrowIfCancellationRequested();
        return NativeFiles.WriteBeside(input, Path.GetFileNameWithoutExtension(path), format.Extension, stream => stream.Write(output), token);
    }
}
