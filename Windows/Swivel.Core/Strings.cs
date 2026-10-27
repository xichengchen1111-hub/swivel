using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Swivel.Core;

public static class Strings
{
    public static IReadOnlyDictionary<string, string[]> Table { get; } = Load();
    private static Dictionary<string, string[]> Load()
    {
        using var stream = typeof(Strings).Assembly.GetManifestResourceStream("Swivel.Core.Strings.json")!;
        return JsonSerializer.Deserialize<Dictionary<string, string[]>>(stream)!;
    }
    public static string L(string english, string language)
    {
        if (language == "system") language = CultureInfo.CurrentUICulture.Name;
        int column = language.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase) || language is "zh-TW" or "zh-HK" or "zh-MO" ? 1 :
            language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? 0 : language.StartsWith("ja", StringComparison.OrdinalIgnoreCase) ? 2 : -1;
        return column >= 0 && Table.TryGetValue(english, out var row) ? row[column] : english;
    }
}
