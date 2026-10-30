using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Swivel.Core;

public enum FilePreviewKind { Unsupported, Image, Document, Subtitle, Data, LocalAudio, DecodedAudio }

// Routing is extension-only; each existing reader still validates the complete input.
public static class FilePreview
{
    public static readonly string[] ImageExtensions = { "png", "jpg", "jpeg", "bmp", "tif", "tiff", "gif", "tga", "psd", "webp", "heic", "heif", "avif" };
    public static readonly string[] LocalAudioExtensions = { "wav", "aif", "aiff", "aifc" };
    public static readonly string[] DecodedAudioExtensions = { "mp3", "aac", "flac", "m4a" };
    public static readonly string[] Extensions = ImageExtensions.Concat(DocumentText.Extensions)
        .Concat(SubtitleFormats.InputExtensions).Concat(DataFileFormats.InputExtensions)
        .Concat(LocalAudioExtensions).Concat(DecodedAudioExtensions).Distinct().ToArray();
    public static FilePreviewKind Kind(string path)
    {
        string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (ImageExtensions.Contains(ext)) return FilePreviewKind.Image;
        if (LocalAudioExtensions.Contains(ext)) return FilePreviewKind.LocalAudio;
        if (DecodedAudioExtensions.Contains(ext)) return FilePreviewKind.DecodedAudio;
        // Structured readers take priority over generic text for SRT, CSV and TSV.
        if (SubtitleFormats.InputExtensions.Contains(ext)) return FilePreviewKind.Subtitle;
        if (DataFileFormats.InputExtensions.Contains(ext)) return FilePreviewKind.Data;
        return DocumentText.Extensions.Contains(ext) ? FilePreviewKind.Document : FilePreviewKind.Unsupported;
    }

    public const string FloatClipping = "The float WAV contains samples outside [-1,1], so it cannot be previewed without clipping. Use Convert Float WAV to PCM… to make a PCM copy first.";
    public const string TextNotice = "Read-only subtitle or data source text, validated by the existing format reader. Only strict UTF-8 or BOM-marked UTF-16 is accepted. Subtitles are limited to 8 MiB and the existing cue/text limits; data files to 32 MiB and the existing structure limits. Binary plist is displayed as generated YAML. Nothing is executed, fetched or exported. The source stays held until this window closes.";
    // The native caller bounds allocation before reading, and holds the source lease.
    public static string ReadText(byte[] bytes, string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        string text;
        if (Kind(path) == FilePreviewKind.Subtitle)
        {
            text = SubtitleFormats.Decode(bytes);
            _ = SubtitleFormats.Parse(text, ext, token);
        }
        else if (Kind(path) == FilePreviewKind.Data)
        {
            var content = DataFileFormats.Read(bytes, ext, token);
            text = ext == "plist" && bytes.AsSpan().StartsWith("bplist00"u8)
                ? new UTF8Encoding(false, true).GetString(DataFileFormats.Write(content, "yaml", token))
                : DataFileFormats.Decode(bytes, false);
        }
        else throw new ArgumentException("Not a subtitle or data preview input.", nameof(path));
        token.ThrowIfCancellationRequested();
        return text;
    }
}
