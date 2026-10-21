using System.Globalization;
using System.Text;

namespace Swivel;

/// The app's display language, chosen in General. "System" follows Windows' display language.
enum Language { System, En, ZhHans, ZhHant, Ja }

static class Languages
{
    public static readonly Language[] All = [Language.System, Language.En, Language.ZhHans, Language.ZhHant, Language.Ja];

    public static string Id(this Language language) => language switch
    {
        Language.En => "en",
        Language.ZhHans => "zh-Hans",
        Language.ZhHant => "zh-Hant",
        Language.Ja => "ja",
        _ => "system",
    };

    public static Language FromId(string? id) => All.FirstOrDefault(l => l.Id() == id, Language.System);

    public static string Title(this Language language) => language switch
    {
        Language.En => "English",
        Language.ZhHans => "简体中文",
        Language.ZhHant => "繁體中文",
        Language.Ja => "日本語",
        _ => L("System Default"),
    };

    public static Language Chosen => FromId(Prefs.LanguageId);

    /// The language actually used for text.
    public static Language Resolved
    {
        get
        {
            var choice = Chosen;
            return choice != Language.System ? choice : FromCulture(CultureInfo.CurrentUICulture.Name);
        }
    }

    public static Language FromCulture(string name)
    {
        if (name.StartsWith("zh-Hant") || name is "zh-TW" or "zh-HK" or "zh-MO") return Language.ZhHant;
        if (name.StartsWith("zh")) return Language.ZhHans;
        if (name.StartsWith("ja")) return Language.Ja;
        return Language.En;
    }

    /// Column in `Strings.Table`, or -1 for English.
    public static int Column(this Language language) => language switch
    {
        Language.ZhHans => 0,
        Language.ZhHant => 1,
        Language.Ja => 2,
        _ => -1,
    };
}

/// `L("English")` translates a UI string; unknown strings (and English) come back unchanged. Strings keep the Mac
/// app's printf placeholders (`%@`, `%d`, `%%`), so both apps share one table.
static class Loc
{
    public static string L(string english)
    {
        int column = Languages.Resolved.Column();
        if (column < 0 || !Strings.Table.TryGetValue(english, out var row)) return english;
        return row[column];
    }

    public static string L(string english, params object?[] args) => Format(L(english), args);

    /// A small printf: `%@`, `%s`, `%d`, `%i`, `%ld`, `%u`, `%f` / `%.Nf`, `%%`, and positional `%1$@`.
    public static string Format(string format, params object?[] args)
    {
        var sb = new StringBuilder(format.Length + 16);
        int next = 0;
        for (int i = 0; i < format.Length; i++)
        {
            char c = format[i];
            if (c != '%' || i + 1 >= format.Length) { sb.Append(c); continue; }
            int j = i + 1;
            if (format[j] == '%') { sb.Append('%'); i = j; continue; }
            int? position = null;
            int start = j;
            while (j < format.Length && char.IsDigit(format[j])) j++;
            if (j < format.Length && format[j] == '$' && j > start)
            {
                position = int.Parse(format.AsSpan(start, j - start)) - 1;
                j++;
            }
            else j = start;
            int? precision = null;
            if (j < format.Length && format[j] == '.')
            {
                int p = ++j;
                while (j < format.Length && char.IsDigit(format[j])) j++;
                precision = j > p ? int.Parse(format.AsSpan(p, j - p)) : 0;
            }
            while (j < format.Length && format[j] is 'l' or 'h' or 'q' or 'z') j++;
            if (j >= format.Length) { sb.Append(format, i, format.Length - i); break; }
            char spec = format[j];
            int index = position ?? next++;
            object? arg = index < args.Length ? args[index] : null;
            switch (spec)
            {
                case '@' or 's':
                    sb.Append(arg?.ToString() ?? "");
                    break;
                case 'd' or 'i' or 'u':
                    sb.Append(Convert.ToInt64(arg ?? 0, CultureInfo.InvariantCulture).ToString(CultureInfo.CurrentCulture));
                    break;
                case 'f':
                    sb.Append(Convert.ToDouble(arg ?? 0, CultureInfo.InvariantCulture)
                        .ToString("F" + (precision ?? 6), CultureInfo.CurrentCulture));
                    break;
                default:
                    sb.Append(format, i, j - i + 1);
                    break;
            }
            i = j;
        }
        return sb.ToString();
    }
}

/// English → [简体中文, 繁體中文, 日本語]. `Mac` is generated from the Mac app's table; each Windows module adds its own
/// strings in its own `Strings.*.cs` file, so the modules never edit the same file. Later rows win.
static partial class Strings
{
    static partial (string English, string[] Translations)[] Mac { get; }
    static partial (string English, string[] Translations)[] Windows { get; }
    static partial (string English, string[] Translations)[] Converters { get; }
    static partial (string English, string[] Translations)[] Documents { get; }
    static partial (string English, string[] Translations)[] Wheel { get; }
    static partial (string English, string[] Translations)[] Editors { get; }
    static partial (string English, string[] Translations)[] Settings { get; }
    static partial (string English, string[] Translations)[] Tools { get; }
    static partial (string English, string[] Translations)[] Clothesline { get; }

    static Dictionary<string, string[]>? table;

    public static IReadOnlyDictionary<string, string[]> Table => table ??= Build();

    /// Every row from every module, for the strings check in the tests.
    public static IEnumerable<(string English, string[] Translations)> AllRows =>
        Mac.Concat(Windows).Concat(Converters).Concat(Documents).Concat(Wheel).Concat(Editors).Concat(Settings).Concat(Tools).Concat(Clothesline);

    static Dictionary<string, string[]> Build()
    {
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var (english, translations) in AllRows)
            if (translations.Length == 3) result[english] = translations;
        return result;
    }
}
