using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Swivel.Core;

public static class DoctorFacts
{
    public static string PowerSource(byte ac) => ac switch { 0 => "On battery", 1 => "Plugged in", _ => "Unknown" };
    public static string Battery(byte flags, byte percent)
    {
        if (flags == 255) return "Unknown";
        if ((flags & 128) != 0) return "No system battery";
        return percent <= 100 ? percent.ToString(CultureInfo.InvariantCulture) + "%" : "Unknown";
    }
    public static bool HasValidCapacity(ulong available, ulong capacity) => capacity > 0 && available <= capacity;
    public static bool LowSpace(ulong available, ulong capacity) => HasValidCapacity(available, capacity) && (available < 5UL * 1024 * 1024 * 1024 || available / (double)capacity < .1);
    public static string Visible(string text) => VisibleBounded(text, 160);
    public static string VisibleBounded(string text, int max)
    {
        if (max is < 1 or > 512) throw new ArgumentOutOfRangeException(nameof(max));
        var result = new StringBuilder();
        foreach (char c in text.Take(max))
            if (char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format) result.Append($"[U+{(int)c:X4}]"); else result.Append(c);
        if (text.Length > max) result.Append('…'); return result.ToString();
    }
}
