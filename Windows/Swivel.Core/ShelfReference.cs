using System;
using System.Collections.Generic;
using System.Threading;

namespace Swivel.Core;

public sealed record ShelfReference(string Path, bool IsFolder, bool IsOnlineOnly);

public static class ShelfReferences
{
    public const uint CloudAttributes = 0x1000 | 0x40000 | 0x400000;
    public const string OnlineOnlyRefusal = "This item is online only. Swivel will not download it. Download it yourself outside Swivel before using content actions.";
    public const string FolderPreviewRefusal = "Preview File requires a local file. Folder references can be dragged out or shown in Explorer.";
    public const string LocalOnly = "Choose a local regular file. Links, network drives and cloud placeholders are not supported yet.";
    public static void ValidatePath(string path)
    {
        if (path is { Length: 3 } && path[2] == '\\') WindowsPathPolicy.Validate(path + "_");
        else WindowsPathPolicy.Validate(path);
    }
    public static ShelfReference FromAttributes(string path, uint attributes, uint reparseTag)
    {
        ValidatePath(path);
        // Cloud tags are not name surrogates. Links, junctions and unknown
        // providers stay refused, including a link carrying an OFFLINE bit.
        bool cloudTag = (attributes & 0x400) != 0 && (reparseTag & 0xffff0fff) == 0x9000001a;
        if (attributes == uint.MaxValue || (attributes & 0x40) != 0 || ((attributes & 0x400) != 0 && !cloudTag))
            throw new UserError(LocalOnly);
        return new(path, (attributes & 0x10) != 0, cloudTag || (attributes & CloudAttributes) != 0);
    }
    public static ShelfReference[] Inspect(IEnumerable<string> paths, Func<string, CancellationToken, ShelfReference> inspect, CancellationToken token)
    {
        var result = new List<ShelfReference>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int candidates = 0;
        foreach (string path in paths)
        {
            token.ThrowIfCancellationRequested();
            if (++candidates > 100) throw new UserError("Choose up to 100 files at a time.");
            ValidatePath(path);
            if (seen.Add(path)) result.Add(inspect(path, token));
        }
        token.ThrowIfCancellationRequested(); return result.ToArray();
    }
    public static void RequireContent(ShelfReference reference)
    {
        if (reference.IsOnlineOnly) throw new UserError(OnlineOnlyRefusal);
        if (reference.IsFolder) throw new UserError(FolderPreviewRefusal);
    }
}
