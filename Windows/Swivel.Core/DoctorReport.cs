using System;
using System.Collections.Generic;
using System.Linq;

namespace Swivel.Core;

public sealed record DoctorFact(string Title, string Format, object[] Values, bool TranslateValue = false,
    string Section = "Overview", bool PrivateDetails = false);

public static class DoctorReport
{
    public static DoctorFact[] Visible(IReadOnlyList<DoctorFact> facts, bool includeAddresses)
        => facts.Where(x => includeAddresses || !x.PrivateDetails).ToArray();
    public static string Text(DoctorFact fact, string language)
        => string.Format(Strings.L(fact.Format, language), fact.Values.Select(x => fact.TranslateValue && x is string value ? Strings.L(value, language) : x).ToArray());
    public static string CopyText(IReadOnlyList<DoctorFact> facts, bool includeAddresses, string language, DateTimeOffset captured)
    {
        var groups = Visible(facts, includeAddresses).GroupBy(x => x.Section);
        return "Swivel · " + Strings.L("Windows Doctor", language) + " · " + captured.ToString("g") + Environment.NewLine + Environment.NewLine +
            string.Join(Environment.NewLine + Environment.NewLine, groups.Select(section => Strings.L(section.Key, language) + Environment.NewLine +
                string.Join(Environment.NewLine + Environment.NewLine, section.Select(fact => Strings.L(fact.Title, language) + Environment.NewLine + Text(fact, language)))));
    }
}
