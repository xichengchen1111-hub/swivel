using System;
using System.Collections.Generic;
using System.Linq;

namespace Swivel.Core;

public static class ClotheslineRules
{
    public const int Limit = 12;
    public static string[] Additions(IEnumerable<string> existing,IEnumerable<string> candidates)
    {
        var known = new HashSet<string>(existing,StringComparer.OrdinalIgnoreCase);
        var added = candidates.Where(path => !known.Contains(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (known.Count + added.Length > Limit) throw new UserError("Hang up to 12 images at a time. Remove a print before adding more.");
        foreach (string path in added) if (!Catalog.IsImage(path)) throw new UserError(GifImage.InputHint);
        return added;
    }
    public static MenuTile[] Layout(int count)
    {
        if (count is < 0 or > Limit) throw new ArgumentOutOfRangeException(nameof(count));
        return Enumerable.Range(0,count).Select(i => new MenuTile(12 + i % 6 * 164,50 + i / 6 * 185,144,145,i % 2 == 0 ? -1 : 1)).ToArray();
    }
}
