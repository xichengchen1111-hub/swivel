using System;
using System.Globalization;
using System.Linq;

namespace Swivel.Core;

/// Lexical checks are deliberately platform independent so the Windows contract can be tested on macOS.
/// NativeFiles additionally checks every component through non-following, held Windows handles.
public static class WindowsPathPolicy
{
    public static string Validate(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 240 || path.Length < 4 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\')
            throw new UserError("Choose local files with plain absolute paths (up to 240 characters).");
        foreach (var part in path[3..].Split('\\')) ValidateName(part);
        return path;
    }
    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.EndsWith(' ') || name.EndsWith('.') ||
            name.Any(c => c < 32 || c == 127 || "<>:\"/\\|?*".Contains(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format))
            throw new UserError("This file name is not supported in the Windows preview.");
        string stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            ((stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem.Length == 4 && "123456789¹²³".Contains(stem[3])))
            throw new UserError("This file name is not supported in the Windows preview.");
    }
    public static string OutputName(string stem, string extension, int ordinal)
    {
        if (ordinal < 1 || ordinal > 10000 || extension.Length == 0 || extension.Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new ArgumentException("Invalid output name.");
        string name = stem + (ordinal == 1 ? "" : " " + ordinal.ToString(CultureInfo.InvariantCulture)) + "." + extension;
        ValidateName(name);
        return name;
    }
}
