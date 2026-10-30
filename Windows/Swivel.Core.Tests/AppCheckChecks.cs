using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Swivel.Core;

internal static class AppCheckChecks
{
    // A PE32+ fixture whose single section (VA 0x1000, file 512..1023+) holds an import table.
    internal static byte[] Imports(params string[] names)
    {
        int namesAt = 20 * (names.Length + 1), size = namesAt + names.Sum(n => n.Length + 1);
        int raw = (size + 511) / 512 * 512;
        byte[] data = new byte[512 + raw]; PeInspectionChecks.Fixture(true).AsSpan(0, 512).CopyTo(data);
        void U32(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at), value);
        int section = 152 + 240; U32(section + 8, (uint)raw); U32(section + 12, 0x1000); U32(section + 16, (uint)raw); U32(section + 20, 512);
        U32(152 + 112 + 8, 0x1000); U32(152 + 112 + 12, (uint)namesAt);
        int at = namesAt;
        for (int i = 0; i < names.Length; i++)
        {
            U32(512 + i * 20 + 12, (uint)(0x1000 + at));
            Encoding.ASCII.GetBytes(names[i]).CopyTo(data, 512 + at); at += names[i].Length + 1;
        }
        return data;
    }

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        var now = new DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc);
        TrustFacts Facts(uint code, TrustSource source = TrustSource.Embedded, DateTime? stamp = null, DateTime? end = null) => new(unchecked((int)code), source, "Contoso", "Contoso CA", stamp, end, false);
        TrustVerdict V(uint code, DateTime? stamp = null, DateTime? end = null) => AppCheck.Classify(Facts(code, TrustSource.Embedded, stamp, end), now);
        check(V(0) == TrustVerdict.Trusted && V(0, null, now.AddDays(10)) == TrustVerdict.Trusted, "Zero result with a current certificate is trusted");
        check(V(0, now.AddYears(-2), now.AddYears(-1)) == TrustVerdict.TrustedExpiredTimestamped, "Expired certificate with a timestamp still trusted, noted");
        check(V(0, null, now.AddYears(-1)) == TrustVerdict.Trusted, "Expired certificate without timestamp is not invented as expired when Windows accepted it");
        check(V(0x800B0100) == TrustVerdict.NotSigned && V(0x800B0003) == TrustVerdict.NotSigned, "No signature / unknown subject form is not signed");
        check(V(0x80096010) == TrustVerdict.Changed && V(0x80091007) == TrustVerdict.Changed, "Bad digest means the file changed after signing");
        check(V(0x800B0109) == TrustVerdict.UntrustedRoot && V(0x800B010A) == TrustVerdict.UntrustedRoot, "Untrusted root and broken chain");
        check(V(0x800B0101) == TrustVerdict.Expired && V(0x800B010C) == TrustVerdict.Revoked && V(0x800B0111) == TrustVerdict.Distrusted, "Expired, revoked and distrusted stay distinct");
        check(V(0x80092013) == TrustVerdict.RevocationUnknown && V(0x80092012) == TrustVerdict.RevocationUnknown, "Offline revocation server is unknown, not revoked");
        check(V(0x80004005) == TrustVerdict.Other && AppCheck.Hex(unchecked((int)0x80004005u)) == "0x80004005", "Unknown codes keep their raw HRESULT");

        // Mark of the Web
        check(AppCheck.ParseZone(null) is null, "No stream means no download warning");
        var z = AppCheck.ParseZone(Encoding.UTF8.GetBytes("[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl=https://example.test/page\r\nHostUrl=https://example.test/app.exe\r\n"))!;
        check(z.ZoneId == 3 && z.HostUrl == "https://example.test/app.exe" && z.ReferrerUrl == "https://example.test/page" && !z.Unreadable, "ZoneId, HostUrl and ReferrerUrl read");
        var u = AppCheck.ParseZone(new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("[ZoneTransfer]\nZoneId=4\n")).ToArray())!;
        check(u.ZoneId == 4, "UTF-16 zone stream read");
        check(AppCheck.ParseZone(Encoding.UTF8.GetBytes("[Other]\nZoneId=3\n"))!.ZoneId is null, "ZoneId outside [ZoneTransfer] ignored");
        check(AppCheck.ParseZone(new byte[] { 0xC3, 0x28 }) is { Unreadable: true, ZoneId: null }, "Invalid bytes reported as unreadable mark");
        check(AppCheck.ParseZone(Encoding.UTF8.GetBytes("[ZoneTransfer]\nZoneId=-1\nZoneId=x\n"))!.ZoneId is null, "Malformed ZoneId not guessed");
        check(AppCheck.ZoneText(null) != AppCheck.ZoneText(z) && AppCheck.ZoneText(z).Contains("zone 3"), "Zone text names the zone");

        // Remove-warning eligibility: only trusted signatures with a mark.
        foreach (TrustVerdict verdict in Enum.GetValues<TrustVerdict>())
        {
            bool allowed = AppCheck.CanRemoveWarning(verdict, z);
            check(allowed == (verdict is TrustVerdict.Trusted or TrustVerdict.TrustedExpiredTimestamped), "Remove warning offered only for trusted signatures: " + verdict);
            check(!AppCheck.CanRemoveWarning(verdict, null), "Nothing to remove without a mark: " + verdict);
        }

        // Architecture
        check(AppCheck.Fit(0x8664, false, Architecture.X64) == ArchFit.Native && AppCheck.Fit(0xaa64, false, Architecture.Arm64) == ArchFit.Native, "Native processor matches");
        check(AppCheck.Fit(0x14c, false, Architecture.X64) == ArchFit.Compatible && AppCheck.Fit(0x8664, false, Architecture.Arm64) == ArchFit.Compatible, "WOW64 and x64 emulation on ARM64 are compatible");
        check(AppCheck.Fit(0xaa64, false, Architecture.X64) == ArchFit.NotSupported && AppCheck.Fit(0x200, false, Architecture.X64) == ArchFit.NotSupported, "ARM64 or Itanium on x64 cannot run");
        check(AppCheck.Fit(0x14c, true, Architecture.Arm64) == ArchFit.Unknown && AppCheck.Fit(0xffff, false, Architecture.X64) == ArchFit.Unknown, "Managed x86 header and unknown codes stay undecided");

        // Import lookup
        var located = AppCheck.Locate(new[] { "app.dll", "KERNEL32.dll", "win.dll", "api-ms-win-crt-runtime-l1-1-0.dll", "VCRUNTIME140.dll", "..\\evil.dll", "a:b.dll" },
            (folder, name) => folder == 0 && name == "app.dll" || folder == 1 && name == "KERNEL32.dll" || folder == 2 && name == "win.dll", default);
        check(located.Select(x => x.State).SequenceEqual(new[] { ImportState.AppFolder, ImportState.SystemFolder, ImportState.WindowsFolder, ImportState.ApiSet, ImportState.NotFound, ImportState.UnusualName, ImportState.UnusualName }), "DLL lookup order and refusals of path-like names");
        int lookups = 0; AppCheck.Locate(new[] { "..", "x\\y.dll", "c:\\x.dll", "api-ms-win-core-l1.dll" }, (_, _) => { lookups++; return true; }, default);
        check(lookups == 0, "Path-like and API-set names are never looked up as files");
        reject(() => AppCheck.Locate(new[] { "a.dll" }, (_, _) => false, new CancellationToken(true)), "Cancelled lookup stops");
        check(AppCheck.IsVisualCRuntime("MSVCP140.dll") && !AppCheck.IsVisualCRuntime("msvcrt.dll"), "Visual C++ runtime names recognized, system msvcrt excluded");

        // Import table reading
        PeImports Read(byte[] bytes) { using var s = new MemoryStream(bytes); return AppCheck.ReadImports(s, default); }
        var imports = Read(Imports("KERNEL32.dll", "VCRUNTIME140.dll", "kernel32.DLL", "user32.dll"));
        check(imports.Names.SequenceEqual(new[] { "KERNEL32.dll", "VCRUNTIME140.dll", "user32.dll" }) && !imports.Truncated && !imports.Partial, "Import names read once each, in order");
        check(Read(PeInspectionChecks.Fixture(true)).Names.Count == 0, "No import directory gives an empty list");
        var many = Read(Imports(Enumerable.Range(0, 300).Select(i => "d" + i + ".dll").ToArray()));
        check(many.Truncated && many.Names.Count == AppCheck.MaxImports, "Import list bounded at 256 names");
        var damaged = Imports("a.dll"); BinaryPrimitives.WriteUInt32LittleEndian(damaged.AsSpan(512 + 12), 0x9000);
        check(Read(damaged).Partial, "Name outside every section gives a partial list");
        var unterminated = Imports(new string('x', 200));
        check(Read(unterminated) is { Partial: true } r && r.Names.Count == 0, "Overlong name is not read past the bound");
        var outside = Imports("a.dll"); BinaryPrimitives.WriteUInt32LittleEndian(outside.AsSpan(152 + 112 + 8), 0x50000);
        check(Read(outside).Partial, "Import directory outside sections gives a partial list");
        reject(() => Read(new byte[100]), "Non-PE refused");
        using (var s = new MemoryStream(Imports("a.dll"))) reject(() => AppCheck.ReadImports(s, new CancellationToken(true)), "Cancelled import read stops");

        // Findings
        var vc = AppCheck.Locate(new[] { "VCRUNTIME140.dll", "foo.dll" }, (_, _) => false, default);
        var findings = AppCheck.Findings(Facts(0x800B0100, TrustSource.None), now, z, 0x8664, ArchFit.Native, Architecture.X64, vc, false);
        check(findings.Any(f => f.Text.Contains("Visual C++") && f.Arguments[0] == "VCRUNTIME140.dll" && f.Arguments[1] == "x64"), "Missing VC++ runtime explained with the program's processor");
        check(findings.Any(f => f.Text.Contains("were not found") && f.Arguments[0] == "foo.dll"), "Other missing DLLs listed separately");
        check(findings.Any(f => f.Text.Contains("not signed")) && findings.Any(f => f.Text.Contains("Windows protected your PC")), "Unsigned downloaded file explains SmartScreen");
        var arm = AppCheck.Findings(Facts(0), now, null, 0xaa64, ArchFit.NotSupported, Architecture.X64, Array.Empty<ImportFact>(), false);
        check(arm[0].Level == FindingLevel.Problem && arm[0].Arguments.SequenceEqual(new[] { "ARM64", "x64" }), "Wrong processor is the first problem");
        var clean = AppCheck.Findings(Facts(0), now, null, 0x8664, ArchFit.Native, Architecture.X64, Array.Empty<ImportFact>(), false);
        check(clean.Count == 1 && clean[0].Level == FindingLevel.Note, "Nothing obvious reported when everything looks normal");
        check(AppCheck.Findings(Facts(0x80096010), now, z, null, null, Architecture.X64, null, true).First().Level == FindingLevel.Problem, "Tampered installer is a problem");
        var all = new[] { 0u, 0x800B0100, 0x80096010, 0x800B0109, 0x800B0101, 0x800B010C, 0x800B0111, 0x80092013, 0x80004005 };
        foreach (uint code in all) foreach (var zone in new[] { null, z }) foreach (bool installer in new[] { false, true })
        {
            foreach (var f in AppCheck.Findings(Facts(code, TrustSource.Embedded, now.AddYears(-2), now.AddYears(-1)), now, zone, 0xaa64, ArchFit.NotSupported, Architecture.X64, vc, installer).Concat(AppCheck.Findings(Facts(code), now, zone, 0x8664, ArchFit.Native, Architecture.X64, Array.Empty<ImportFact>(), installer)))
                check(Strings.Table.ContainsKey(f.Text) && f.Text.Count(c => c == '{') == f.Arguments.Length, "Finding translated with matching arguments: " + f.Text);
        }
        foreach (TrustVerdict v in Enum.GetValues<TrustVerdict>()) check(Strings.Table.ContainsKey(AppCheck.VerdictText(v)), "Verdict translated: " + v);
        foreach (TrustSource v in Enum.GetValues<TrustSource>()) check(Strings.Table.ContainsKey(AppCheck.SourceText(v)), "Source translated: " + v);
        foreach (ArchFit v in Enum.GetValues<ArchFit>()) check(Strings.Table.ContainsKey(AppCheck.FitText(v)), "Fit translated: " + v);
        foreach (ImportState v in Enum.GetValues<ImportState>()) check(Strings.Table.ContainsKey(AppCheck.ImportText(v)), "Import state translated: " + v);
        foreach (int? id in new int?[] { null, 0, 1, 2, 3, 4, 9 }) check(Strings.Table.ContainsKey(AppCheck.ZoneText(id is null ? null : new(id == 9 ? 9 : id, null, null, false))), "Zone translated");
        check(Strings.Table.ContainsKey(AppCheck.ZoneText(new(null, null, null, true))), "Unreadable zone translated");
        foreach (string text in new[] { AppCheck.Scope, AppCheck.ImportScope, AppCheck.SmartScreen, AppCheck.UnblockSteps, AppCheck.RemoveRisk, AppCheck.Extensions }) check(Strings.Table.ContainsKey(text), "App check text translated");
        check(!AppCheck.SmartScreen.Contains("Run anyway") && !AppCheck.UnblockSteps.Contains("SmartScreen") && AppCheck.SmartScreen.Contains("never offers a way around"), "No SmartScreen bypass suggested");

        var prefs = new Prefs(); check(!Catalog.Offered(prefs).Any(x => x.Id == "doctor.appcheck"), "App check stays behind default-off Doctor");
        prefs.Set(Feature.Doctor, true); check(Catalog.Available(prefs, Array.Empty<string>()).Any(x => x.Id == "doctor.appcheck"), "App check uses its own picker");
        check(WindowsDoctor.Areas.Single(a => a.Id == "app").ActionIds.SequenceEqual(new[] { "doctor.appcheck", "doctor.executable" }), "Apps That Will Not Open lists the check first, header view kept");
    }
}
