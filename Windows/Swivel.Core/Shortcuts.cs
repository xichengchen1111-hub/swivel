using System;
using System.Collections.Generic;
using System.Linq;

namespace Swivel.Core;

public sealed record Shortcut(uint Modifiers, uint Key)
{
    public void Validate()
    {
        int count = ((Modifiers & 1) != 0 ? 1 : 0) + ((Modifiers & 2) != 0 ? 1 : 0) + ((Modifiers & 4) != 0 ? 1 : 0);
        if ((Modifiers & ~7u) != 0 || count < 2 || !(Key is >= 0x41 and <= 0x5a or >= 0x30 and <= 0x39 or >= 0x70 and <= 0x87 or 0x20))
            throw new UserError("Use at least two of Ctrl, Alt and Shift, plus a letter, number, Space or F1–F24.");
    }
    public string Label => string.Join("+", new[] { (Modifiers & 2) != 0 ? "Ctrl" : null, (Modifiers & 1) != 0 ? "Alt" : null, (Modifiers & 4) != 0 ? "Shift" : null,
        Key == 0x20 ? "Space" : Key >= 0x70 ? "F" + (Key - 0x6f) : ((char)Key).ToString() }.Where(x => x is not null));
}
public sealed record ShortcutSet(Shortcut Wheel, Shortcut Quick, Shortcut Custom)
{
    public static ShortcutSet Default => new(new(3,0x57), new(6,0x20), new(3,0x4d));
    public void Validate()
    {
        if (Wheel is null || Quick is null || Custom is null) throw new FormatException("Invalid shortcut settings.");
        foreach (var key in new[] { Wheel, Quick, Custom }) key.Validate();
        if (Wheel == Quick || Quick == Custom || Custom == Wheel) throw new UserError("Choose a different shortcut for each tool.");
    }
}
public static class CustomActions
{
    public static string[] Validate(IEnumerable<string> ids, IEnumerable<ToolAction> actions)
    {
        var result = ids.Take(81).ToArray(); var known = actions.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        if (result.Length > 80 || result.Distinct(StringComparer.Ordinal).Count() != result.Length || result.Any(x => x is null || !known.Contains(x)))
            throw new UserError("Choose up to 80 different actions from the available tools.");
        return result;
    }
}
