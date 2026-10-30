using System;
using System.Linq;
using System.Reflection;
using Swivel.Core;

// exFAT/FAT32 rules: file system names, storable names, case clashes, FAT32's size limit and
// which data streams a copy there may leave behind.
internal static class FatRulesChecks
{
    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        check(FatRules.Parse("NTFS") == VolumeFormat.Ntfs && FatRules.Parse("FAT32") == VolumeFormat.Fat && FatRules.Parse("FAT") == VolumeFormat.Fat && FatRules.Parse("exFAT") == VolumeFormat.ExFat, "Windows file system names map to formats");
        check(FatRules.Parse("ReFS") == VolumeFormat.Other && FatRules.Parse("ntfs") == VolumeFormat.Other && FatRules.Parse("EXFAT") == VolumeFormat.Other && FatRules.Parse(null) == VolumeFormat.Other && FatRules.Parse("") == VolumeFormat.Other, "Other and differently spelled file systems stay unsupported");
        check(FatRules.IsFat(VolumeFormat.Fat) && FatRules.IsFat(VolumeFormat.ExFat) && !FatRules.IsFat(VolumeFormat.Ntfs) && !FatRules.IsFat(VolumeFormat.Other), "Only FAT and exFAT count as FAT drives");

        foreach (string good in new[] { "photo.jpg", ".profile", "a b", "名前.txt", "x\U0001F600y.png", new string('a', 255), "a.b.c" })
            check(FatRules.NameProblem(good) is null, "FAT stores the name " + good.Length);
        foreach (string bad in new[] { "", ".", "..", "trailing.", "trailing ", "a<b", "a>b", "a:b", "a\"b", "a/b", "a\\b", "a|b", "a?b", "a*b", "tab\tname", "del\u007f", new string('a', 256), "lone\uD800", "lone\uDC00x" })
            check(FatRules.NameProblem(bad) == FatRules.BadName, "FAT refuses the name " + bad.Replace("\t", "\\t"));
        reject(() => FatRules.CheckName("bad?"), "CheckName throws for a forbidden character");
        try { FatRules.CheckName("end."); check(false, "CheckName refuses a trailing dot"); }
        catch (UserError error) { check(error.Template == FatRules.BadName && error.Values.SequenceEqual(new object[] { "end." }), "CheckName names the refused file"); }

        FatRules.CheckTree(new[] { "", "a", "a\\b.txt", "B", "c\\B.txt", "c\\d" });
        check(true, "Distinct relative paths pass the tree check");
        try { FatRules.CheckTree(new[] { "", "Sub", "Sub\\read.txt", "Sub\\READ.txt" }); check(false, "Case clash refused"); }
        catch (UserError error) { check(error.Template == FatRules.CaseClash && error.Values.SequenceEqual(new object[] { "Sub\\read.txt", "Sub\\READ.txt" }), "A case clash names both paths"); }
        reject(() => FatRules.CheckTree(new[] { "a", "A" }), "Folders differing only in case are refused");
        reject(() => FatRules.CheckTree(new[] { "ok", "ok\\bad." }), "A bad part deep in the tree is refused");

        FatRules.CheckSize(VolumeFormat.Fat, FatRules.Fat32MaxFile, "max");
        FatRules.CheckSize(VolumeFormat.ExFat, 5UL << 30, "big");
        FatRules.CheckSize(VolumeFormat.Ntfs, 5UL << 30, "big");
        check(FatRules.Fat32MaxFile == 4294967295UL, "FAT32 holds files up to 4 GiB minus one byte");
        try { FatRules.CheckSize(VolumeFormat.Fat, 4294967296UL, "movie.mkv"); check(false, "4 GiB refused on FAT32"); }
        catch (UserError error) { check(error.Template == FatRules.TooBig && error.Values.SequenceEqual(new object[] { "movie.mkv" }), "FAT32 refuses a 4 GiB file by name"); }

        var main = new[] { ("::$DATA", 10L) };
        var zoned = new[] { ("::$DATA", 10L), (":Zone.Identifier:$DATA", 26L) };
        var extra = new[] { ("::$DATA", 10L), (":Zone.Identifier:$DATA", 26L), (":thumb:$DATA", 4L) };
        check(!FatRules.CheckStreams(main, "a") && FatRules.CheckStreams(zoned, "a") && FatRules.CheckStreams(new[] { ("::$DATA", 1L), (":ZONE.IDENTIFIER:$DATA", 2L) }, "a"), "The download warning is recognised as the only droppable stream");
        try { FatRules.CheckStreams(extra, "x.bin"); check(false, "Extra stream refused"); }
        catch (UserError error) { check(error.Template == FatRules.OtherStreams && error.Values.SequenceEqual(new object[] { "x.bin" }), "Another named stream refuses the copy by name"); }
        try { FatRules.CopyStreams(zoned, false, "dl.zip"); check(false, "Zone loss needs consent"); }
        catch (UserError error) { check(error.Template == FatRules.ZoneRefused, "Without consent the download warning is never silently dropped"); }
        check(FatRules.CopyStreams(zoned, true, "dl.zip").SequenceEqual(main) && FatRules.CopyStreams(main, false, "a").SequenceEqual(main), "A FAT copy is expected to have the main stream alone, with the source's size");
        reject(() => FatRules.CopyStreams(extra, true, "x"), "Consent to lose the download warning does not cover other streams");

        foreach (var field in typeof(FatRules).GetFields(BindingFlags.Public | BindingFlags.Static).Where(x => x.IsLiteral && x.FieldType == typeof(string) && x.Name is not "MainStream" and not "ZoneStream"))
        {
            string text = (string)field.GetValue(null)!;
            check(Strings.Table.TryGetValue(text, out var translations) && translations.Length == 3, "FAT text translated: " + field.Name);
            foreach (string placeholder in new[] { "{0}", "{1}" }) if (text.Contains(placeholder)) check(translations!.All(x => x.Contains(placeholder)), "FAT text keeps " + placeholder + ": " + field.Name);
        }
    }
}
