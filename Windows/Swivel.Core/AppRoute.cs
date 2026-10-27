using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;

namespace Swivel.Core;

public sealed record AppRoute(string Id, string Name, string Executable, string Extensions = "", bool CopyToClipboard = false, string? WebAddress = null, string? BrowserExecutable = null)
{
    [JsonIgnore] public bool IsWebPage => WebAddress is not null;
    public string[] Types => Extensions.Split(',',StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.TrimStart('.').ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToArray();
    public static string ValidateBrowser(string path)
    {
        WindowsPathPolicy.Validate(path);
        if (!Path.GetExtension(path).Equals(".exe",StringComparison.OrdinalIgnoreCase)) throw new UserError("Choose a local Windows .exe application. Scripts and shortcuts are not supported.");
        return path;
    }
    public void Validate()
    {
        if (!Guid.TryParseExact(Id,"N",out var guid) || guid.ToString("N") != Id) throw new FormatException("Invalid application identifier.");
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 48 || Name != Name.Trim() || Name.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format))
            throw new UserError("Use an app name of 1–48 characters without control characters.");
        if (IsWebPage)
        {
            if (Executable != "") throw new UserError("Choose either a local application or a web page, not both.");
            Core.WebAddress.Normalize(WebAddress!);
            if (BrowserExecutable is not null) ValidateBrowser(BrowserExecutable);
        }
        else
        {
            if (BrowserExecutable is not null) throw new UserError("A browser selection is only available for web page actions.");
            WindowsPathPolicy.Validate(Executable);
            if (!Path.GetExtension(Executable).Equals(".exe",StringComparison.OrdinalIgnoreCase)) throw new UserError("Choose a local Windows .exe application. Scripts and shortcuts are not supported.");
        }
        if (Extensions is null || Extensions.Length > 256 || Types.Length > 16 || Types.Any(x => x.Length is < 1 or > 12 || x.Any(c => !char.IsAsciiLetterOrDigit(c))))
            throw new UserError("Enter up to 16 extensions separated by commas, such as png,jpg. Leave blank for any file.");
    }
}
