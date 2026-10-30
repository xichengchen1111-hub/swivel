using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Swivel.Core;

/// One Explorer folder look: colour, optional surface, and either a short mark or one emoji.
public sealed record FolderStyle(int Color, string Badge = "", string Emoji = "", FolderSurface Surface = FolderSurface.Plain)
{
    public void Validate()
    {
        if (Color < 0 || Color >= FolderStyles.Colors.Count) throw new UserError("Choose one of the folder colours.");
        FolderStyles.ValidateBadge(Badge);
        if (!Enum.IsDefined(Surface)) throw new UserError("Choose one of the folder surfaces.");
        if (Emoji.Length != 0)
        {
            FolderStyles.ValidateEmoji(Emoji);
            if (Badge.Length != 0) throw new UserError("Choose either a text mark or an emoji badge.");
        }
    }
}

/// What an existing desktop.ini says about a folder.
public enum FolderStyleState { None, Swivel, Other, Unavailable }

/// A desktop.ini that Swivel wrote: its icon file and whether Swivel set the folder's
/// Read-only attribute (Explorer reads desktop.ini only in Read-only or System folders).
public sealed record SwivelFolderIni(string Icon, bool ReadOnlyAdded);

/// Explorer folder icons the Windows way: desktop.ini plus an icon file inside the folder.
/// Swivel only ever recognises, replaces or removes files it wrote itself, byte for byte.
public static class FolderStyles
{
    public const int MaxFolders = 20, MaxBadgeTextElements = 2, MaxBadgeUnits = 16, MaxIniBytes = 4096;
    public const int MaxEmojiUnits = 64;
    public const string IniName = "desktop.ini";
    public static readonly int[] IconSizes = { 16, 20, 24, 32, 40, 48, 64, 256 };

    public static readonly IReadOnlyList<(string Name, uint Rgb)> Colors = new (string, uint)[]
    {
        ("Blue", 0x3B82F6), ("Teal", 0x14B8A6), ("Green", 0x22C55E), ("Yellow", 0xEAB308),
        ("Orange", 0xF97316), ("Red", 0xEF4444), ("Pink", 0xEC4899), ("Purple", 0x8B5CF6),
        ("Brown", 0x92603A), ("Graphite", 0x6B7280),
    };

    private static readonly Regex IconPattern = new("^\\.swivel-folder-[0-9a-f]{8}\\.ico$", RegexOptions.CultureInvariant);

    public static string NewIconName() => ".swivel-folder-" + Guid.NewGuid().ToString("N")[..8] + ".ico";
    public static bool IsIconName(string name) => IconPattern.IsMatch(name);

    public static void ValidateBadge(string badge)
    {
        ArgumentNullException.ThrowIfNull(badge);
        if (badge.Length == 0) return;
        if (badge.Length > MaxBadgeUnits || badge.Trim().Length != badge.Length || new StringInfo(badge).LengthInTextElements > MaxBadgeTextElements)
            throw new UserError("Use up to two letters, digits or symbols for the badge, without spaces around them.");
        for (int i = 0; i < badge.Length; i++)
        {
            char c = badge[i];
            if (char.IsHighSurrogate(c) && i + 1 < badge.Length && char.IsLowSurrogate(badge[i + 1])) { i++; continue; }
            if (char.IsSurrogate(c)) throw new UserError("Use up to two letters, digits or symbols for the badge, without spaces around them.");
            var category = char.GetUnicodeCategory(c);
            // Joiners inside emoji sequences are allowed; every other invisible or control character is not.
            if (c != '\u200D' && category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator or UnicodeCategory.SpaceSeparator or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned)
                throw new UserError("Use up to two letters, digits or symbols for the badge, without spaces around them.");
        }
    }

    /// One extended grapheme with emoji-sequence syntax, never arbitrary text with a joiner.
    public static void ValidateEmoji(string emoji)
    {
        const string error = "Enter exactly one emoji, without text or spaces.";
        if (string.IsNullOrEmpty(emoji) || emoji.Length > MaxEmojiUnits ||
            new StringInfo(emoji).LengthInTextElements != 1) throw new UserError(error);
        // Strict UTF-16: EnumerateRunes would otherwise silently replace lone surrogates.
        try { _ = new UnicodeEncoding(false, false, true).GetByteCount(emoji); }
        catch (EncoderFallbackException) { throw new UserError(error); }
        int[] points = emoji.EnumerateRunes().Select(r => r.Value).ToArray();
        if (points.Length == 2 && points.All(IsRegional)) return;
        if (points.Length is 2 or 3 && (points[0] is >= '0' and <= '9' or '#' or '*') &&
            points[^1] == 0x20E3 && (points.Length == 2 || points[1] == 0xFE0F)) return;
        int i = 0;
        while (i < points.Length)
        {
            int basis = points[i++];
            if (!IsEmojiBase(basis)) throw new UserError(error);
            if (i < points.Length && points[i] == 0xFE0F) i++;
            if (i < points.Length && points[i] is >= 0x1F3FB and <= 0x1F3FF)
            {
                if (!IsModifierBase(basis)) throw new UserError(error);
                i++;
            }
            // Unicode subdivision flags: black flag + tag letters + cancel tag.
            if (basis == 0x1F3F4 && i < points.Length && points[i] is >= 0xE0061 and <= 0xE007A)
            {
                while (i < points.Length && points[i] is >= 0xE0061 and <= 0xE007A) i++;
                if (i == points.Length || points[i++] != 0xE007F) throw new UserError(error);
            }
            if (i == points.Length) return;
            if (points[i++] != 0x200D || i == points.Length) throw new UserError(error);
        }
        throw new UserError(error);
    }

    private static bool IsRegional(int p) => p is >= 0x1F1E6 and <= 0x1F1FF;
    private static bool IsModifierBase(int p) => p is 0x261D or 0x26F9 or >= 0x270A and <= 0x270D or 0x1F385
        or >= 0x1F3C2 and <= 0x1F3C4 or 0x1F3C7 or >= 0x1F3CA and <= 0x1F3CC
        or >= 0x1F442 and <= 0x1F443 or >= 0x1F446 and <= 0x1F450 or >= 0x1F466 and <= 0x1F478
        or 0x1F47C or >= 0x1F481 and <= 0x1F483 or >= 0x1F485 and <= 0x1F487 or 0x1F48F or 0x1F491 or 0x1F4AA
        or >= 0x1F574 and <= 0x1F575 or 0x1F57A or 0x1F590 or >= 0x1F595 and <= 0x1F596
        or >= 0x1F645 and <= 0x1F647 or >= 0x1F64B and <= 0x1F64F or 0x1F6A3
        or >= 0x1F6B4 and <= 0x1F6B6 or 0x1F6C0 or 0x1F6CC or 0x1F90C or 0x1F90F
        or >= 0x1F918 and <= 0x1F91F or 0x1F926 or >= 0x1F930 and <= 0x1F939
        or >= 0x1F93C and <= 0x1F93E or 0x1F977 or >= 0x1F9B5 and <= 0x1F9B6
        or >= 0x1F9B8 and <= 0x1F9B9 or 0x1F9BB or >= 0x1F9CD and <= 0x1F9CF
        or >= 0x1F9D1 and <= 0x1F9DD or >= 0x1FAC3 and <= 0x1FAC5 or >= 0x1FAF0 and <= 0x1FAF8;

    // Unicode 17 Emoji bases, cross-checked against the system Unicode properties.
    // Digits/keycaps, regional indicators and modifiers are handled separately; this validates
    // sequence syntax, not the RGI recommendation list or installed-font coverage.
    private static bool IsEmojiBase(int p) => p is 0xA9 or 0xAE or 0x203C or 0x2049 or 0x2122 or 0x2139
        or >= 0x2194 and <= 0x2199 or >= 0x21A9 and <= 0x21AA or >= 0x231A and <= 0x231B or 0x2328 or 0x23CF
        or >= 0x23E9 and <= 0x23F3 or >= 0x23F8 and <= 0x23FA or 0x24C2 or >= 0x25AA and <= 0x25AB
        or 0x25B6 or 0x25C0 or >= 0x25FB and <= 0x25FE or >= 0x2600 and <= 0x2604 or 0x260E or 0x2611
        or >= 0x2614 and <= 0x2615 or 0x2618 or 0x261D or 0x2620 or >= 0x2622 and <= 0x2623 or 0x2626
        or 0x262A or >= 0x262E and <= 0x262F or >= 0x2638 and <= 0x263A or 0x2640 or 0x2642
        or >= 0x2648 and <= 0x2653 or >= 0x265F and <= 0x2660 or 0x2663 or >= 0x2665 and <= 0x2666
        or 0x2668 or 0x267B or >= 0x267E and <= 0x267F or >= 0x2692 and <= 0x2697 or 0x2699
        or >= 0x269B and <= 0x269C or >= 0x26A0 and <= 0x26A1 or 0x26A7 or >= 0x26AA and <= 0x26AB
        or >= 0x26B0 and <= 0x26B1 or >= 0x26BD and <= 0x26BE or >= 0x26C4 and <= 0x26C5 or 0x26C8
        or >= 0x26CE and <= 0x26CF or 0x26D1 or >= 0x26D3 and <= 0x26D4 or >= 0x26E9 and <= 0x26EA
        or >= 0x26F0 and <= 0x26F5 or >= 0x26F7 and <= 0x26FA or 0x26FD or 0x2702 or 0x2705
        or >= 0x2708 and <= 0x270D or 0x270F or 0x2712 or 0x2714 or 0x2716 or 0x271D or 0x2721 or 0x2728
        or >= 0x2733 and <= 0x2734 or 0x2744 or 0x2747 or 0x274C or 0x274E or >= 0x2753 and <= 0x2755
        or 0x2757 or >= 0x2763 and <= 0x2764 or >= 0x2795 and <= 0x2797 or 0x27A1 or 0x27B0 or 0x27BF
        or >= 0x2934 and <= 0x2935 or >= 0x2B05 and <= 0x2B07 or >= 0x2B1B and <= 0x2B1C or 0x2B50 or 0x2B55
        or 0x3030 or 0x303D or 0x3297 or 0x3299 or 0x1F004 or 0x1F0CF or >= 0x1F170 and <= 0x1F171
        or >= 0x1F17E and <= 0x1F17F or 0x1F18E or >= 0x1F191 and <= 0x1F19A or >= 0x1F201 and <= 0x1F202
        or 0x1F21A or 0x1F22F or >= 0x1F232 and <= 0x1F23A or >= 0x1F250 and <= 0x1F251
        or >= 0x1F300 and <= 0x1F321 or >= 0x1F324 and <= 0x1F393 or >= 0x1F396 and <= 0x1F397
        or >= 0x1F399 and <= 0x1F39B or >= 0x1F39E and <= 0x1F3F0 or >= 0x1F3F3 and <= 0x1F3F5
        or >= 0x1F3F7 and <= 0x1F3FA or >= 0x1F400 and <= 0x1F4FD or >= 0x1F4FF and <= 0x1F53D
        or >= 0x1F549 and <= 0x1F54E or >= 0x1F550 and <= 0x1F567 or >= 0x1F56F and <= 0x1F570
        or >= 0x1F573 and <= 0x1F57A or 0x1F587 or >= 0x1F58A and <= 0x1F58D or 0x1F590
        or >= 0x1F595 and <= 0x1F596 or >= 0x1F5A4 and <= 0x1F5A5 or 0x1F5A8 or >= 0x1F5B1 and <= 0x1F5B2
        or 0x1F5BC or >= 0x1F5C2 and <= 0x1F5C4 or >= 0x1F5D1 and <= 0x1F5D3 or >= 0x1F5DC and <= 0x1F5DE
        or 0x1F5E1 or 0x1F5E3 or 0x1F5E8 or 0x1F5EF or 0x1F5F3 or >= 0x1F5FA and <= 0x1F64F
        or >= 0x1F680 and <= 0x1F6C5 or >= 0x1F6CB and <= 0x1F6D2 or >= 0x1F6D5 and <= 0x1F6D8
        or >= 0x1F6DC and <= 0x1F6E5 or 0x1F6E9 or >= 0x1F6EB and <= 0x1F6EC or 0x1F6F0
        or >= 0x1F6F3 and <= 0x1F6FC or >= 0x1F7E0 and <= 0x1F7EB or 0x1F7F0
        or >= 0x1F90C and <= 0x1F93A or >= 0x1F93C and <= 0x1F945 or >= 0x1F947 and <= 0x1F9FF
        or >= 0x1FA70 and <= 0x1FA7C or >= 0x1FA80 and <= 0x1FA8A or >= 0x1FA8E and <= 0x1FAC6 or 0x1FAC8
        or >= 0x1FACD and <= 0x1FADC or >= 0x1FADF and <= 0x1FAEA or >= 0x1FAEF and <= 0x1FAF8;

    /// The exact text Swivel writes. Anything that does not reproduce it byte for byte is not ours.
    public static string IniText(SwivelFolderIni ini)
    {
        if (!IsIconName(ini.Icon)) throw new ArgumentException("Not a Swivel icon name.", nameof(ini));
        return "; Swivel folder style v1. Restore Default in Swivel removes this file and " + ini.Icon + ".\r\n"
            + "[.ShellClassInfo]\r\nIconResource=" + ini.Icon + ",0\r\n"
            + "[Swivel]\r\nVersion=1\r\nIcon=" + ini.Icon + "\r\nReadOnlyAdded=" + (ini.ReadOnlyAdded ? "1" : "0") + "\r\n";
    }

    /// UTF-16 little-endian with a byte-order mark, which Explorer reads with any file name.
    public static byte[] IniBytes(SwivelFolderIni ini) => Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(IniText(ini))).ToArray();

    /// Returns the recorded settings only when the file is exactly one Swivel wrote.
    public static SwivelFolderIni? ParseOurs(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 2 || bytes.Length > MaxIniBytes || bytes.Length % 2 != 0 || bytes[0] != 0xFF || bytes[1] != 0xFE) return null;
        string text;
        try { text = new UnicodeEncoding(false, false, true).GetString(bytes[2..]); }
        catch (DecoderFallbackException) { return null; }
        var match = Regex.Match(text, "\\[Swivel\\]\r\nVersion=1\r\nIcon=(?<icon>[^\r\n]*)\r\nReadOnlyAdded=(?<flag>[01])\r\n\\z", RegexOptions.CultureInvariant);
        if (!match.Success || !IsIconName(match.Groups["icon"].Value)) return null;
        var ini = new SwivelFolderIni(match.Groups["icon"].Value, match.Groups["flag"].Value == "1");
        return IniText(ini) == text ? ini : null;
    }

    public static FolderStyleState Classify(byte[]? existing)
        => existing is null ? FolderStyleState.None : ParseOurs(existing) is null ? FolderStyleState.Other : FolderStyleState.Swivel;

    /// An icon file whose images are all PNG-compressed (supported for every size since Windows Vista).
    public static byte[] Icon(IReadOnlyList<(int Size, byte[] Png)> images)
    {
        if (images.Count is < 1 or > 16) throw new ArgumentException("An icon needs 1–16 images.", nameof(images));
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)images.Count);
        int offset = 6 + 16 * images.Count;
        foreach (var (size, png) in images)
        {
            if (size is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(images));
            if (png.Length < 8 || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) throw new ArgumentException("Icon images must be PNG.", nameof(images));
            writer.Write((byte)(size == 256 ? 0 : size)); writer.Write((byte)(size == 256 ? 0 : size));
            writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32);
            writer.Write((uint)png.Length); writer.Write((uint)offset);
            offset += png.Length;
        }
        foreach (var (_, png) in images) writer.Write(png);
        writer.Flush();
        return output.ToArray();
    }
}
