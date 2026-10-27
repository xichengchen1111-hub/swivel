using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Swivel.Core;

/// One Explorer folder look: a colour from a fixed palette and an optional short badge.
public sealed record FolderStyle(int Color, string Badge = "")
{
    public void Validate()
    {
        if (Color < 0 || Color >= FolderStyles.Colors.Count) throw new UserError("Choose one of the folder colours.");
        FolderStyles.ValidateBadge(Badge);
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
