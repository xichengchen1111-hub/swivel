using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Swivel.Core;

// "Check Why It Won't Open": pure classification of facts gathered elsewhere (WinVerifyTrust
// results, the Zone.Identifier stream, PE imports and DLL existence). Nothing here runs,
// loads or changes the program, and nothing suggests turning off a Windows protection.

public enum TrustSource { None, Embedded, Catalog }
public enum TrustVerdict { Trusted, TrustedExpiredTimestamped, NotSigned, Changed, UntrustedRoot, Expired, Revoked, Distrusted, RevocationUnknown, Other }
/// Hresult is WinVerifyTrust's return value; the other fields come from the verified signer.
public sealed record TrustFacts(int Hresult, TrustSource Source, string? Subject, string? Issuer, DateTime? TimestampUtc, DateTime? NotAfterUtc, bool RevocationChecked);

public enum ArchFit { Native, Compatible, NotSupported, Unknown }
public enum ImportState { AppFolder, SystemFolder, WindowsFolder, ApiSet, NotFound, UnusualName }
public sealed record ImportFact(string Name, ImportState State);
public sealed record PeImports(IReadOnlyList<string> Names, bool Truncated, bool Partial);

public sealed record ZoneFacts(int? ZoneId, string? HostUrl, string? ReferrerUrl, bool Unreadable);

public enum FindingLevel { Problem, Warning, Note }
public sealed record AppFinding(FindingLevel Level, string Text, params string[] Arguments);

public static class AppCheck
{
    public const string Extensions = "Programs and installers (*.exe;*.msi;*.dll)|*.exe;*.msi;*.dll";
    public const int MaxImports = 256, MaxImportName = 128;
    public const string Scope = "Read-only check. Swivel never runs or loads the chosen file. The signature check uses Windows' own verification without contacting the internet (only certificates already on this PC are used, and revocation is not checked unless you press Check Revocation Online). Results describe this file now; they are not a malware scan and do not replace Microsoft Defender, SmartScreen or Smart App Control.";
    public const string ImportScope = "Imported DLLs are read from the file's import table (at most 256 names). Each name is only looked up as a file in the app's folder, the Windows system folder and the Windows folder; nothing is loaded. Delay-loaded DLLs, DLLs found through PATH, side-by-side manifests or the program's own loading code are not checked, and a found file is not checked for the right processor type.";
    public const string SmartScreen = "\"Windows protected your PC\" is Microsoft Defender SmartScreen. It appears for files that came from the internet when Microsoft has not yet seen the file or its publisher often enough to trust it. It is a warning, not proof that the file is harmful. Swivel never offers a way around it.";
    public const string UnblockSteps = "Windows' own steps: right-click the file in File Explorer, choose Properties, select Unblock on the General tab, then OK. Do this only for a file from a publisher you trust.";
    public const string RemoveRisk = "Remove the download warning from this file? Windows and SmartScreen will then treat it as if it had not come from the internet. Its signature is currently valid and trusted, but that only tells you who published it, not that it is safe for you. This cannot be undone from Swivel.";

    public static TrustVerdict Classify(TrustFacts facts, DateTime nowUtc)
    {
        uint code = unchecked((uint)facts.Hresult);
        if (code == 0)
            return facts.NotAfterUtc is DateTime end && end < nowUtc && facts.TimestampUtc is not null ? TrustVerdict.TrustedExpiredTimestamped : TrustVerdict.Trusted;
        return code switch
        {
            0x800B0100 or 0x800B0003 or 0x800B0001 => TrustVerdict.NotSigned, // NOSIGNATURE, SUBJECT_FORM_UNKNOWN, PROVIDER_UNKNOWN
            0x80096010 or 0x80091007 => TrustVerdict.Changed, // TRUST_E_BAD_DIGEST, CRYPT_E_HASH_VALUE
            0x800B0109 or 0x800B010A or 0x800B010D or 0x800B0112 => TrustVerdict.UntrustedRoot, // UNTRUSTEDROOT, CHAINING, UNTRUSTEDTESTROOT, UNTRUSTEDCA
            0x800B0101 => TrustVerdict.Expired,
            0x800B010C => TrustVerdict.Revoked,
            0x800B0111 => TrustVerdict.Distrusted,
            0x80092012 or 0x80092013 => TrustVerdict.RevocationUnknown,
            _ => TrustVerdict.Other,
        };
    }

    public static bool TrustedForUnblock(TrustVerdict verdict) => verdict is TrustVerdict.Trusted or TrustVerdict.TrustedExpiredTimestamped;
    /// The only state in which Swivel offers to remove the download warning.
    public static bool CanRemoveWarning(TrustVerdict verdict, ZoneFacts? zone) => TrustedForUnblock(verdict) && zone is not null;

    public static string VerdictText(TrustVerdict verdict) => verdict switch
    {
        TrustVerdict.Trusted => "Signed and trusted",
        TrustVerdict.TrustedExpiredTimestamped => "Signed and trusted (certificate since expired, signed while valid)",
        TrustVerdict.NotSigned => "Not signed",
        TrustVerdict.Changed => "Signature invalid: the file was changed after signing",
        TrustVerdict.UntrustedRoot => "Signed, but not by a certificate this PC trusts",
        TrustVerdict.Expired => "Signature certificate expired without a timestamp",
        TrustVerdict.Revoked => "Signature certificate revoked",
        TrustVerdict.Distrusted => "Signer explicitly distrusted on this PC",
        TrustVerdict.RevocationUnknown => "Signed, but revocation could not be checked",
        _ => "Signature could not be verified",
    };
    public static string SourceText(TrustSource source) => source switch { TrustSource.Embedded => "Embedded signature", TrustSource.Catalog => "Windows catalog signature", _ => "No signature found" };
    public static string Hex(int hresult) => "0x" + unchecked((uint)hresult).ToString("X8", CultureInfo.InvariantCulture);

    // ---- Mark of the Web ----

    public const int MaxZoneBytes = 4096;
    public static ZoneFacts? ParseZone(byte[]? bytes)
    {
        if (bytes is null) return null;
        string text;
        try
        {
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) text = new UnicodeEncoding(false, true, true).GetString(bytes, 2, bytes.Length - 2);
            else
            {
                int skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
                text = new UTF8Encoding(false, true).GetString(bytes, skip, bytes.Length - skip);
            }
        }
        catch (ArgumentException) { return new(null, null, null, true); }
        bool inSection = false; int? zone = null; string? host = null, referrer = null;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim().TrimEnd('\0');
            if (line.StartsWith('[')) { inSection = line.Equals("[ZoneTransfer]", StringComparison.OrdinalIgnoreCase); continue; }
            if (!inSection) continue;
            int eq = line.IndexOf('='); if (eq <= 0) continue;
            string key = line[..eq].Trim(), value = line[(eq + 1)..].Trim();
            if (key.Equals("ZoneId", StringComparison.OrdinalIgnoreCase) && zone is null && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int id) && id <= 100) zone = id;
            else if (key.Equals("HostUrl", StringComparison.OrdinalIgnoreCase) && host is null && value.Length > 0) host = value;
            else if (key.Equals("ReferrerUrl", StringComparison.OrdinalIgnoreCase) && referrer is null && value.Length > 0) referrer = value;
        }
        return new(zone, host, referrer, zone is null);
    }
    public static string ZoneText(ZoneFacts? zone) => zone is null ? "No download warning (no Mark of the Web)" : zone.ZoneId switch
    {
        0 => "Marked as from this computer (zone 0)",
        1 => "Marked as from the local intranet (zone 1)",
        2 => "Marked as from a trusted site (zone 2)",
        3 => "Marked as downloaded from the internet (zone 3)",
        4 => "Marked as from a restricted site (zone 4)",
        null => "Download mark present but unreadable",
        _ => "Download mark with an unknown zone",
    };
    static bool FromInternet(ZoneFacts? zone) => zone is not null && (zone.ZoneId is null or >= 3);

    // ---- Architecture ----

    public static string MachineName(ushort machine) => machine switch { 0x14c => "x86", 0x8664 => "x64", 0xaa64 => "ARM64", 0xa641 => "ARM64EC", 0xa64e => "ARM64X", 0x1c4 => "ARM32", 0x200 => "Itanium", _ => "0x" + machine.ToString("X4", CultureInfo.InvariantCulture) };
    public static string OsName(Architecture os) => os switch { Architecture.X64 => "x64", Architecture.Arm64 => "ARM64", Architecture.X86 => "x86", _ => os.ToString() };
    /// clr: managed images can declare x86 yet start as 64-bit (AnyCPU), so their header is not decisive.
    public static ArchFit Fit(ushort machine, bool clr, Architecture os)
    {
        if (clr && machine == 0x14c) return ArchFit.Unknown;
        return (machine, os) switch
        {
            (0x8664, Architecture.X64) or (0xaa64 or 0xa641 or 0xa64e, Architecture.Arm64) or (0x14c, Architecture.X86) => ArchFit.Native,
            (0x14c, Architecture.X64 or Architecture.Arm64) or (0x8664, Architecture.Arm64) => ArchFit.Compatible,
            (0xaa64 or 0xa641 or 0xa64e or 0x1c4 or 0x200, Architecture.X64 or Architecture.X86) or (0x8664, Architecture.X86) or (0x200 or 0x1c4, Architecture.Arm64) => ArchFit.NotSupported,
            _ => ArchFit.Unknown,
        };
    }
    public static string FitText(ArchFit fit) => fit switch
    {
        ArchFit.Native => "Made for this PC's processor",
        ArchFit.Compatible => "Runs on this PC through Windows' built-in compatibility layer or emulation",
        ArchFit.NotSupported => "Made for a different processor; it cannot run on this PC",
        _ => "Not decided from the header alone",
    };

    // ---- Imports ----

    static readonly string[] VisualCRuntime = { "msvcp140.dll", "msvcp140_1.dll", "msvcp140_2.dll", "msvcp140_atomic_wait.dll", "msvcp140_codecvt_ids.dll", "vcruntime140.dll", "vcruntime140_1.dll", "vcruntime140_threads.dll", "concrt140.dll", "vccorlib140.dll", "vcomp140.dll", "mfc140.dll", "mfc140u.dll", "mfcm140.dll", "mfcm140u.dll" };
    public static bool IsVisualCRuntime(string name) => VisualCRuntime.Contains(name.ToLowerInvariant());
    public static bool IsApiSet(string name) => name.StartsWith("api-ms-win-", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ext-ms-", StringComparison.OrdinalIgnoreCase);
    /// A plain file name only: no folders, drive, stream or wildcard, printable ASCII.
    public static bool PlainDllName(string name) => name.Length is > 0 and <= MaxImportName && name.All(c => c is > ' ' and < (char)0x7F && c is not '\\' and not '/' and not ':' and not '*' and not '?' and not '"' and not '<' and not '>' and not '|') && name != "." && name != "..";
    /// exists(folder index, name): 0 app folder, 1 system folder, 2 Windows folder.
    public static IReadOnlyList<ImportFact> Locate(IEnumerable<string> names, Func<int, string, bool> exists, CancellationToken token)
    {
        var list = new List<ImportFact>();
        foreach (string name in names)
        {
            token.ThrowIfCancellationRequested();
            if (!PlainDllName(name)) list.Add(new(name, ImportState.UnusualName));
            else if (IsApiSet(name)) list.Add(new(name, ImportState.ApiSet));
            else if (exists(0, name)) list.Add(new(name, ImportState.AppFolder));
            else if (exists(1, name)) list.Add(new(name, ImportState.SystemFolder));
            else if (exists(2, name)) list.Add(new(name, ImportState.WindowsFolder));
            else list.Add(new(name, ImportState.NotFound));
        }
        return list.AsReadOnly();
    }
    public static string ImportText(ImportState state) => state switch
    {
        ImportState.AppFolder => "Found in the app's folder",
        ImportState.SystemFolder => "Found in the Windows system folder",
        ImportState.WindowsFolder => "Found in the Windows folder",
        ImportState.ApiSet => "Windows API set (provided by Windows)",
        ImportState.NotFound => "Not found",
        _ => "Unusual name, not looked up",
    };

    /// Reads the normal import table's DLL names. Bounded: 256 descriptors, 128-byte names,
    /// only header and import-directory bytes; a damaged table gives a partial list.
    public static PeImports ReadImports(Stream input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        long length = input.Length; var names = new List<string>();
        byte[] ReadAt(long at, int count)
        {
            token.ThrowIfCancellationRequested();
            if (at < 0 || count < 0 || at > length || count > length - at) throw new UserError(PeInspection.Invalid);
            var bytes = new byte[count]; input.Position = at; input.ReadExactly(bytes); return bytes;
        }
        if (length < 64 || length > PeInspection.MaxBytes) throw new UserError(PeInspection.Invalid);
        byte[] dos = ReadAt(0, 64); if (dos[0] != 'M' || dos[1] != 'Z') throw new UserError(PeInspection.Invalid);
        long pe = BinaryPrimitives.ReadUInt32LittleEndian(dos.AsSpan(60)); if (pe < 64 || pe > PeInspection.MaxPeOffset) throw new UserError(PeInspection.Invalid);
        byte[] coff = ReadAt(pe, 24); if (!coff.AsSpan(0, 4).SequenceEqual("PE\0\0"u8)) throw new UserError(PeInspection.Invalid);
        int sections = BinaryPrimitives.ReadUInt16LittleEndian(coff.AsSpan(6)), optionalBytes = BinaryPrimitives.ReadUInt16LittleEndian(coff.AsSpan(20));
        if (sections > PeInspection.MaxSections || optionalBytes > PeInspection.MaxOptionalBytes || optionalBytes < 2) throw new UserError(PeInspection.Invalid);
        byte[] optional = ReadAt(pe + 24, optionalBytes); ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(optional);
        if (magic is not 0x10b and not 0x20b) throw new UserError(PeInspection.Invalid);
        int directoryBase = magic == 0x20b ? 112 : 96; if (optionalBytes < directoryBase + 16) return new(names.AsReadOnly(), false, false);
        uint directories = BinaryPrimitives.ReadUInt32LittleEndian(optional.AsSpan(directoryBase - 4));
        if (directories < 2) return new(names.AsReadOnly(), false, false);
        uint importRva = BinaryPrimitives.ReadUInt32LittleEndian(optional.AsSpan(directoryBase + 8));
        if (importRva == 0) return new(names.AsReadOnly(), false, false);
        byte[] table = ReadAt(pe + 24 + optionalBytes, sections * 40);
        long Offset(uint rva)
        {
            for (int i = 0; i < sections; i++)
            {
                uint va = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(i * 40 + 12)), virtualSize = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(i * 40 + 8));
                uint rawSize = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(i * 40 + 16)), raw = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(i * 40 + 20));
                uint span = Math.Max(virtualSize, rawSize);
                if (rva >= va && (ulong)rva < (ulong)va + span)
                {
                    ulong inside = rva - va; if (inside >= rawSize) return -1;
                    ulong at = raw + inside; return at < (ulong)length ? (long)at : -1;
                }
            }
            return -1;
        }
        long descriptor = Offset(importRva);
        if (descriptor < 0) return new(names.AsReadOnly(), false, true);
        for (int i = 0; ; i++)
        {
            if (i == MaxImports) return new(names.AsReadOnly(), true, false);
            long at = descriptor + i * 20L;
            if (at + 20 > length) return new(names.AsReadOnly(), false, true);
            byte[] entry = ReadAt(at, 20);
            if (entry.All(b => b == 0)) return new(names.AsReadOnly(), false, false);
            long nameAt = Offset(BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(12)));
            if (nameAt < 0) return new(names.AsReadOnly(), false, true);
            byte[] name = ReadAt(nameAt, (int)Math.Min(MaxImportName + 1, length - nameAt));
            int end = Array.IndexOf(name, (byte)0);
            if (end <= 0) return new(names.AsReadOnly(), false, true);
            string text = Encoding.Latin1.GetString(name, 0, end);
            if (!names.Contains(text, StringComparer.OrdinalIgnoreCase)) names.Add(text);
        }
    }

    // ---- Summary ----

    public static IReadOnlyList<AppFinding> Findings(TrustFacts trust, DateTime nowUtc, ZoneFacts? zone, ushort? machine, ArchFit? fit, Architecture os, IReadOnlyList<ImportFact>? imports, bool installer)
    {
        var list = new List<AppFinding>(); var verdict = Classify(trust, nowUtc);
        if (fit == ArchFit.NotSupported && machine is ushort m)
            list.Add(new(FindingLevel.Problem, "This program is made for {0} processors, but this PC uses {1}. Get the version made for this PC from the publisher.", MachineName(m), OsName(os)));
        switch (verdict)
        {
            case TrustVerdict.Changed: list.Add(new(FindingLevel.Problem, "The file was changed after it was signed, so Windows will not trust it. It may be damaged or tampered with. Do not run it; download a fresh copy from the publisher's official site.")); break;
            case TrustVerdict.Revoked: list.Add(new(FindingLevel.Problem, "The publisher's certificate was revoked. Windows treats this file as untrustworthy. Do not run it.")); break;
            case TrustVerdict.Distrusted: list.Add(new(FindingLevel.Problem, "This PC is set to distrust this signer. Do not run it unless your administrator says otherwise.")); break;
            case TrustVerdict.UntrustedRoot: list.Add(new(FindingLevel.Warning, "The file is signed, but the certificate does not lead to an authority this PC trusts. Windows may block or warn about it.")); break;
            case TrustVerdict.Expired: list.Add(new(FindingLevel.Warning, "The signing certificate has expired and the signature has no timestamp, so Windows may no longer trust it. Look for a newer version from the publisher.")); break;
            case TrustVerdict.NotSigned: list.Add(new(FindingLevel.Warning, "The file is not signed, so Windows cannot show who published it. SmartScreen or Smart App Control may block or warn about it. Only use it if you trust where it came from; Swivel does not offer to remove warnings for unsigned files.")); break;
            case TrustVerdict.RevocationUnknown: list.Add(new(FindingLevel.Note, "The signature is valid, but its revocation status could not be confirmed online.")); break;
            case TrustVerdict.Other: list.Add(new(FindingLevel.Warning, "Windows could not verify the signature (code {0}).", Hex(trust.Hresult))); break;
            case TrustVerdict.TrustedExpiredTimestamped: list.Add(new(FindingLevel.Note, "The signing certificate has expired, but the file was timestamped while it was valid, so the signature still counts.")); break;
        }
        if (FromInternet(zone))
            list.Add(TrustedForUnblock(verdict)
                ? new(FindingLevel.Note, "The file carries a download warning. SmartScreen may still ask before it opens and shows the verified publisher under More info.")
                : new(FindingLevel.Warning, "The file carries a download warning and is not validly signed by a trusted publisher, so SmartScreen will probably show \"Windows protected your PC\"."));
        if (imports is not null)
        {
            var missing = imports.Where(x => x.State == ImportState.NotFound).ToList();
            var runtime = missing.Where(x => IsVisualCRuntime(x.Name)).ToList();
            if (runtime.Count > 0)
                list.Add(new(FindingLevel.Problem, "Microsoft Visual C++ runtime files are missing: {0}. Install the latest supported Visual C++ Redistributable for {1} from Microsoft's website.", string.Join(", ", runtime.Select(x => x.Name)), machine is ushort r ? MachineName(r) : "?"));
            var other = missing.Where(x => !IsVisualCRuntime(x.Name)).ToList();
            if (other.Count > 0)
                list.Add(new(FindingLevel.Warning, "Some DLLs the program needs were not found next to it or in Windows: {0}. Reinstalling the program usually restores them. They might still be found through PATH.", string.Join(", ", other.Select(x => x.Name))));
        }
        if (list.Count == 0)
            list.Add(new(FindingLevel.Note, installer ? "Nothing obvious stops this installer: the signature and download mark look normal." : "Nothing obvious stops this program: signature, processor and the DLLs checked look normal. The cause may be inside the program (settings, missing data, permissions)."));
        return list.AsReadOnly();
    }
}
