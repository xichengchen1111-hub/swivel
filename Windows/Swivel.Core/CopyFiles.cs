using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Swivel.Core;

public sealed record CopyResult(string Source, string Destination)
{
    /// The copy as verified right after copying (identity, length, last write); null when it
    /// could not be verified or has attributes other tools skip (system, offline…), so it is
    /// never offered for undo.
    public DuplicateStamp? Stamp { get; init; }
    /// The copy's streams were checked against its original.
    public bool Verified { get; init; }
}
public sealed record CopyOutcome(IReadOnlyList<CopyResult> Copied, int Total, Exception? Error, bool Cancelled);

/// Copy to Folder: originals stay; each copy claims a free name in the chosen folder.
public static class CopyRules
{
    public const int MaxFiles = 100;
    public static IReadOnlyList<string> Validate(IReadOnlyList<string> sources, string directory)
    {
        if (sources is null || sources.Count is < 1 or > MaxFiles || sources.Any(x => x is null))
            throw new UserError("Choose 1–100 distinct files to copy.");
        MoveRules.DirectoryPath(directory);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string source in sources)
        {
            WindowsPathPolicy.Validate(source);
            if (!seen.Add(source)) throw new UserError("Choose 1–100 distinct files to copy.");
        }
        return sources.ToArray();
    }
    /// "photo.jpg", "photo 2.jpg"…; dot files keep their whole name as the stem.
    public static string Name(string fileName, int ordinal)
    {
        if (ordinal is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(ordinal));
        WindowsPathPolicy.ValidateName(fileName);
        if (ordinal == 1) return fileName;
        int dot = fileName.LastIndexOf('.');
        string stem = dot > 0 ? fileName[..dot] : fileName, extension = dot > 0 ? fileName[dot..] : "";
        string name = stem + " " + ordinal.ToString(CultureInfo.InvariantCulture) + extension;
        WindowsPathPolicy.ValidateName(name); return name;
    }
    /// Streams as (name, size), e.g. "::$DATA" and ":Zone.Identifier:$DATA". A copy must match exactly.
    public static bool SameStreams(IReadOnlyCollection<(string Name, long Size)> source, IReadOnlyCollection<(string Name, long Size)> copy)
        => source.Count == copy.Count && source.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Zip(copy.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            .All(pair => string.Equals(pair.First.Name, pair.Second.Name, StringComparison.OrdinalIgnoreCase) && pair.First.Size == pair.Second.Size);
    public static IReadOnlyList<(string Name, long Size)> ParseStreamInfo(ReadOnlySpan<byte> buffer)
    {
        var streams = new List<(string, long)>(); int at = 0;
        for (int guard = 0; guard < 4096; guard++)
        {
            if (at + 24 > buffer.Length) throw new UserError("The copy could not be verified.");
            uint next = BitConverter.ToUInt32(buffer[at..]), length = BitConverter.ToUInt32(buffer[(at + 4)..]);
            long size = BitConverter.ToInt64(buffer[(at + 8)..]);
            if (length % 2 != 0 || at + 24 + length > buffer.Length || size < 0) throw new UserError("The copy could not be verified.");
            streams.Add((new string(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, char>(buffer.Slice(at + 24, (int)length))), size));
            if (next == 0) return streams;
            if (next < 24 || at + next > buffer.Length) throw new UserError("The copy could not be verified.");
            at += (int)next;
        }
        throw new UserError("The copy could not be verified.");
    }
}
