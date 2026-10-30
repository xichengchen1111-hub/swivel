using System;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using Swivel.Core;

internal static class FolderStyleChecks
{
    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        string icon = FolderStyles.NewIconName();
        check(FolderStyles.IsIconName(icon) && !FolderStyles.IsIconName(".swivel-folder-ABCDEF12.ico") && !FolderStyles.IsIconName("x.ico") && !FolderStyles.IsIconName(icon + "\\..\\x"), "Swivel icon names are generated and recognised strictly");
        foreach (bool flag in new[] { false, true })
        {
            var ini = new SwivelFolderIni(icon, flag);
            byte[] bytes = FolderStyles.IniBytes(ini);
            check(bytes[0] == 0xFF && bytes[1] == 0xFE && FolderStyles.ParseOurs(bytes) == ini && FolderStyles.Classify(bytes) == FolderStyleState.Swivel, "Swivel desktop.ini round trip: ReadOnlyAdded " + flag);
            string text = FolderStyles.IniText(ini);
            check(text.Contains("[.ShellClassInfo]\r\nIconResource=" + icon + ",0\r\n", StringComparison.Ordinal), "desktop.ini names the icon for Explorer");
            // Any change at all makes it someone else's file.
            foreach (string altered in new[] { text + "\r\n", text.Replace("Version=1", "Version=2"), text.Replace("[.ShellClassInfo]", "[.ShellClassInfo]\r\nInfoTip=x"), text.ToUpperInvariant(), " " + text })
                check(FolderStyles.ParseOurs(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(altered)).ToArray()) is null, "Altered desktop.ini is not treated as Swivel's");
        }
        byte[] valid = FolderStyles.IniBytes(new SwivelFolderIni(icon, false));
        check(FolderStyles.ParseOurs(valid.Skip(2).ToArray()) is null, "desktop.ini without a byte-order mark is not Swivel's");
        check(FolderStyles.ParseOurs(Encoding.UTF8.GetBytes(FolderStyles.IniText(new SwivelFolderIni(icon, false)))) is null, "UTF-8 desktop.ini is not Swivel's");
        check(FolderStyles.ParseOurs(valid.Take(valid.Length - 1).ToArray()) is null, "Odd-length desktop.ini is not Swivel's");
        var lone = (byte[])valid.Clone(); lone[^2] = 0x00; lone[^1] = 0xD8; // unpaired surrogate
        check(FolderStyles.ParseOurs(lone) is null, "desktop.ini with invalid UTF-16 is not Swivel's");
        string other = "[.ShellClassInfo]\r\nIconResource=" + icon + ",0\r\n[Swivel]\r\nVersion=1\r\nIcon=" + icon + "\r\nReadOnlyAdded=1\r\n";
        check(FolderStyles.Classify(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(other)).ToArray()) == FolderStyleState.Other, "A look-alike without the exact header is someone else's");
        check(FolderStyles.Classify(null) == FolderStyleState.None && FolderStyles.Classify(new byte[FolderStyles.MaxIniBytes + 1]) == FolderStyleState.Other, "No desktop.ini, and an oversized one, classified");
        reject(() => FolderStyles.IniText(new SwivelFolderIni("..\\evil.ico", false)), "desktop.ini never names a foreign icon");

        foreach (string badge in new[] { "", "A", "7z", "旅", "工作", "🦜", "👩‍💻", "❤️" }) FolderStyles.ValidateBadge(badge);
        check(true, "Short badges, CJK and emoji sequences accepted");
        foreach (string badge in new[] { "ABC", " A", "A ", "​", "a‮b", "\t", "", "\uD800", new string('x', 17) })
            reject(() => FolderStyles.ValidateBadge(badge), "Badge refused: " + string.Join(" ", badge.Select(c => ((int)c).ToString("X4"))));
        reject(() => new FolderStyle(FolderStyles.Colors.Count).Validate(), "Unknown colour refused");
        reject(() => new FolderStyle(-1).Validate(), "Negative colour refused");
        new FolderStyle(0, "A").Validate();

        foreach (string emoji in new[] { "😀", "👩‍💻", "👨‍👩‍👧‍👦", "🇨🇳", "👍🏽", "👩🏽‍🚀", "❤️", "1️⃣", "🏳️‍🌈" })
        {
            FolderStyles.ValidateEmoji(emoji);
            new FolderStyle(0, Emoji: emoji, Surface: FolderSurface.Linen).Validate();
            check(true, "Single emoji accepted: " + emoji);
        }
        foreach (string emoji in new[] { "", "hello", "中", "A", "1", "#", "😀😀", "🇨", "🏻", "😀\n", "\u200D", "A‍😀", "😀‍A", "😀\u200B", "😀\uFE0E", "\uD800", "😀🏻", "👍🏽🏽", "😀‍", "😀\u0301", "\U0001F6F1", "\U0001F6F2", "\U0001FA7F", "😀️️", "\t", "\n", "\0", "\u202E" })
            reject(() => FolderStyles.ValidateEmoji(emoji), "Invalid emoji refused: " + emoji);
        reject(() => new FolderStyle(0, "A", "😀").Validate(), "Text and emoji cannot be combined");
        reject(() => new FolderStyle(0, Surface: (FolderSurface)99).Validate(), "Unknown surface refused");
        foreach (int size in FolderStyles.IconSizes)
        {
            var hashes = new System.Collections.Generic.HashSet<string>();
            foreach (FolderSurface surface in Enum.GetValues<FolderSurface>())
            {
                byte[] pixels = FolderSurfaces.Pixels(surface, size, 123);
                string hash = Convert.ToHexString(SHA256.HashData(pixels));
                check(pixels.Length == size * size * 4 && hash == Convert.ToHexString(SHA256.HashData(FolderSurfaces.Pixels(surface, size, 123))), "Surface hash repeatable: " + surface + " / " + size);
                check(hashes.Add(hash), "Surface pixels differ: " + surface + " / " + size);
                check(pixels.Where((_, i) => i % 4 == 3).All(a => a <= 40), "Surface stays subtle: " + surface);
            }
            check(FolderSurfaces.Pixels(FolderSurface.Plain, size, 123).All(b => b == 0), "Plain surface has no overlay");
            check(!FolderSurfaces.Pixels(FolderSurface.PaperGrain, size, 123).SequenceEqual(FolderSurfaces.Pixels(FolderSurface.PaperGrain, size, 124)), "Seed changes grain");
        }
        reject(() => FolderSurfaces.Pixels(FolderSurface.Linen, 0, 1), "Zero surface size refused");
        reject(() => FolderSurfaces.Pixels(FolderSurface.Linen, 257, 1), "Oversize surface refused");

        byte[] png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };
        byte[] ico = FolderStyles.Icon(new[] { (16, png), (256, png) });
        check(Convert.ToHexString(SHA256.HashData(ico)) == "F96DF06C02668680AFDEC2A9AF417C3D5034430AF942C2C5C882404816862635", "Existing ICO bytes are unchanged");
        check(BitConverter.ToUInt16(ico, 0) == 0 && BitConverter.ToUInt16(ico, 2) == 1 && BitConverter.ToUInt16(ico, 4) == 2, "ICO header: reserved, type 1, two images");
        check(ico[6] == 16 && ico[22] == 0 && BitConverter.ToUInt16(ico, 12) == 32 && BitConverter.ToUInt32(ico, 14) == png.Length && BitConverter.ToUInt32(ico, 18) == 38 && BitConverter.ToUInt32(ico, 34) == 38 + png.Length, "ICO entries: sizes (256 stored as 0), 32 bpp, lengths and offsets");
        check(ico.Length == 38 + 2 * png.Length && ico.Skip(38).Take(png.Length).SequenceEqual(png), "ICO image data follows the directory");
        reject(() => FolderStyles.Icon(new[] { (16, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }) }), "ICO accepts PNG images only");
        reject(() => FolderStyles.Icon(new[] { (257, png) }), "ICO refuses sizes above 256");
    }
}
