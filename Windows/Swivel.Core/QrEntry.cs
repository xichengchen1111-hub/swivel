using System;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;

namespace Swivel.Core;

public sealed record QrEntry(string Id, string Name, string Text, string Group = "", [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingDefault)] QrCorrection Correction = QrCorrection.Low,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingDefault)] QrGardenTemplate Template=QrGardenTemplate.Tree,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingDefault)] QrGardenSeason Season=QrGardenSeason.Summer)
{
    public void Validate()
    {
        QrGarden.Validate(Template,Season);
        if (!Guid.TryParseExact(Id,"N",out var id) || id.ToString("N") != Id) throw new FormatException("Invalid QR identifier.");
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 48 || Name != Name.Trim() || Name.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format))
            throw new UserError("Use a QR name of 1 to 48 characters without control characters.");
        QrCode.Payload(Text,Correction);
        if (Group is null || Group.Length > 32 || Group != Group.Trim() || Group.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format))
            throw new UserError("Use a QR group of up to 32 characters without control characters, or leave it empty.");
    }
}
