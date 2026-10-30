using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using Swivel.Core;
using Swivel.Windows;

internal static class AppCheckNativeChecks
{
    internal static void Run(string directory, Action<bool, string> check, Action<Action, string> reject)
    {
        string folder = Path.Combine(directory, "app-check"); Directory.CreateDirectory(folder);
        string system = Environment.SystemDirectory;
        PeStructure? Header(string path) { try { using var s = File.OpenRead(path); return PeInspection.Read(s, default); } catch { return null; } }
        var candidates = Directory.EnumerateFiles(system, "*.dll").Concat(Directory.EnumerateFiles(system, "*.exe")).Where(p => new FileInfo(p).Length is > 4096 and < 4_000_000).Take(400).ToArray();

        // 1. A Microsoft file signed through a Windows catalog (no embedded certificate table).
        string? catalogFile = candidates.FirstOrDefault(p => Header(p) is { Certificates.State: PeCertificateState.NotDeclared });
        check(catalogFile is not null, "System32 has a catalog-signed binary to check");
        if (catalogFile is not null)
            using (var file = AppCheckFile.Open(catalogFile, default))
            {
                var verdict = AppCheck.Classify(file.Trust, DateTime.UtcNow);
                Console.WriteLine("App check catalog sample: " + Path.GetFileName(catalogFile) + " " + verdict + " " + file.Trust.Source + " " + AppCheck.Hex(file.Trust.Hresult) + " " + file.Trust.Subject);
                check(verdict == TrustVerdict.Trusted && file.Trust.Source == TrustSource.Catalog && file.Trust.Subject?.Contains("Microsoft") == true, "Catalog-signed System32 file reports trusted Microsoft catalog signature");
            }

        // 2. An embedded-signed Microsoft file, copied so it can be changed safely.
        int ImportCount(string path) { try { using var s = File.OpenRead(path); return AppCheck.ReadImports(s, default).Names.Count; } catch { return 0; } }
        string? embedded = candidates.FirstOrDefault(p => Header(p) is { Certificates.State: PeCertificateState.HeadersCoverTable } h && h.Certificates.Headers.Count == 1 && ImportCount(p) > 0);
        check(embedded is not null, "System32 has an embedded-signed binary to copy");
        if (embedded is null) return;
        using (var file = AppCheckFile.Open(embedded, default))
        {
            Console.WriteLine("App check embedded sample: " + Path.GetFileName(embedded) + " " + AppCheck.Classify(file.Trust, DateTime.UtcNow) + " " + file.Trust.Subject + " / " + file.Trust.Issuer + " ts=" + file.Trust.TimestampUtc);
            check(AppCheck.TrustedForUnblock(AppCheck.Classify(file.Trust, DateTime.UtcNow)) && file.Trust.Source == TrustSource.Embedded && file.Trust.Subject is not null && file.Trust.Issuer is not null, "Embedded-signed system file trusted with signer and issuer");
            check(file.ImportFacts is { Count: > 0 } facts && facts.Any(x => x.State is ImportState.SystemFolder or ImportState.ApiSet) && file.Fit == ArchFit.Native, "System file imports found in Windows and processor native");
        }
        byte[] original = File.ReadAllBytes(embedded);
        var info = Header(embedded)!;

        string signed = Path.Combine(folder, "Signed.dll"); File.WriteAllBytes(signed, original);
        string tampered = Path.Combine(folder, "Tampered.dll"); byte[] changed = (byte[])original.Clone();
        int at = (int)Math.Min(info.Certificates.Offset - 16, 0x1000 + 32); changed[at] ^= 0x5A; File.WriteAllBytes(tampered, changed);
        string unsigned = Path.Combine(folder, "Unsigned.dll"); byte[] stripped = original[..(int)info.Certificates.Offset];
        int pe = BinaryPrimitives.ReadInt32LittleEndian(stripped.AsSpan(60)); int directories = pe + 24 + (info.Pe32Plus ? 112 : 96);
        BinaryPrimitives.WriteUInt64LittleEndian(stripped.AsSpan(directories + 4 * 8), 0);
        // Stripping alone keeps the Authenticode hash, so a catalog may still list the copy; change a byte too.
        stripped[at] ^= 0x5A; File.WriteAllBytes(unsigned, stripped);
        string zone = "[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl=https://example.test/private-page\r\nHostUrl=https://example.test/private-download.dll\r\n";
        foreach (string path in new[] { signed, tampered, unsigned }) File.WriteAllText(path + ":Zone.Identifier", zone);

        var app = Application.Current as App ?? new App();
        using (var file = AppCheckFile.Open(tampered, default))
        {
            check(AppCheck.Classify(file.Trust, DateTime.UtcNow) == TrustVerdict.Changed && unchecked((uint)file.Trust.Hresult) == 0x80096010, "Tampered signed copy reports the file changed after signing (TRUST_E_BAD_DIGEST)");
            var dialog = new AppCheckDialog(app, file) { ConfirmRemove = () => true };
            check(dialog.RemoveButton is null && !dialog.RemoveWarning() && File.Exists(tampered + ":Zone.Identifier"), "Tampered file never offered warning removal");
            dialog.Close();
        }
        using (var file = AppCheckFile.Open(unsigned, default))
        {
            Console.WriteLine("App check unsigned sample: " + AppCheck.Classify(file.Trust, DateTime.UtcNow) + " " + file.Trust.Source + " " + AppCheck.Hex(file.Trust.Hresult));
            check(AppCheck.Classify(file.Trust, DateTime.UtcNow) == TrustVerdict.NotSigned && file.Trust.Source == TrustSource.None, "Copy with the signature removed reports not signed");
            check(file.Zone is { ZoneId: 3 }, "Zone.Identifier stream read: zone 3");
            var dialog = new AppCheckDialog(app, file); bool asked = false; dialog.ConfirmRemove = () => asked = true;
            check(dialog.RemoveButton is null && !dialog.RemoveWarning() && !asked && File.Exists(unsigned + ":Zone.Identifier"), "Unsigned file never offered warning removal, no confirmation shown");
            reject(() => file.RemoveWarning(), "Direct removal refused for an unsigned file");
            check(File.Exists(unsigned + ":Zone.Identifier"), "Unsigned file keeps its download warning");
            check(dialog.CurrentReport.Contains(app.L("The file carries a download warning and is not validly signed by a trusted publisher, so SmartScreen will probably show \"Windows protected your PC\".")), "Unsigned downloaded file explains SmartScreen");
            dialog.Close();
        }
        using (var file = AppCheckFile.Open(signed, default))
        {
            check(AppCheck.TrustedForUnblock(AppCheck.Classify(file.Trust, DateTime.UtcNow)) && file.Zone is { ZoneId: 3 } && file.CanRemoveWarning, "Signed copy with a download mark is eligible");
            reject(() => File.WriteAllBytes(signed, new byte[1]), "Checked file stays write-protected while open");
            var dialog = new AppCheckDialog(app, file);
            check(!dialog.CurrentReport.Contains("private-download"), "Download addresses stay out of the report until revealed");
            int asked = 0; dialog.ConfirmRemove = () => { asked++; return false; };
            check(dialog.RemoveButton is not null && !dialog.RemoveWarning() && asked == 1 && File.Exists(signed + ":Zone.Identifier"), "Removal needs confirmation; declining keeps the warning");
            dialog.ConfirmRemove = () => { asked++; return true; };
            check(dialog.RemoveWarning() && asked == 2 && !File.Exists(signed + ":Zone.Identifier") && file.Zone is null && dialog.Removed && dialog.RemoveButton is null, "Confirmed removal deletes only the Zone.Identifier stream");
            dialog.Close();
        }
        check(File.ReadAllBytes(signed).SequenceEqual(original), "Removing the warning never changes the file's contents");

        // A small unsigned PE that imports a DLL that does not exist anywhere.
        string missing = Path.Combine(folder, "Missing.exe"); File.WriteAllBytes(missing, Fixture("swivel-app-check-missing.dll", "VCRUNTIME140_9.dll", "kernel32.dll"));
        using (var file = AppCheckFile.Open(missing, default))
        {
            Console.WriteLine("App check built PE: " + AppCheck.Classify(file.Trust, DateTime.UtcNow) + " " + AppCheck.Hex(file.Trust.Hresult));
            check(AppCheck.Classify(file.Trust, DateTime.UtcNow) is not TrustVerdict.Trusted and not TrustVerdict.TrustedExpiredTimestamped, "Built unsigned PE is not trusted");
            check(file.ImportFacts!.Single(x => x.Name == "swivel-app-check-missing.dll").State == ImportState.NotFound && file.ImportFacts!.Single(x => x.Name == "kernel32.dll").State == ImportState.SystemFolder, "Missing and System32 imports told apart without loading");
            var dialog = new AppCheckDialog(app, file); check(dialog.CurrentReport.Contains("swivel-app-check-missing.dll"), "Report lists missing DLL"); dialog.Close();
        }
        reject(() => AppCheckFile.Open(missing, new CancellationToken(true)).Dispose(), "Precancelled check opens no source");
        string text = Path.Combine(folder, "notes.txt"); File.WriteAllText(text, "x"); reject(() => AppCheckFile.Open(text, default).Dispose(), "Only EXE, MSI and DLL accepted");
        Console.WriteLine("DIAG app check done");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Console.WriteLine("DIAG process exit code " + Environment.ExitCode);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Console.WriteLine("DIAG unhandled " + e.ExceptionObject);
        Directory.Move(folder, folder + " released"); check(Directory.Exists(folder + " released"), "App check releases the file and its folders");
    }

    private static byte[] Fixture(params string[] names)
    {
        byte[] data = new byte[1024];
        void U16(int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(at), value);
        void U32(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at), value);
        data[0] = (byte)'M'; data[1] = (byte)'Z'; U32(60, 128); "PE\0\0"u8.CopyTo(data.AsSpan(128)); U16(132, 0x8664); U16(134, 1); U16(148, 240); U16(150, 2);
        U16(152, 0x20b); U16(152 + 68, 2); U32(152 + 60, 512); U32(152 + 108, 16); U32(152 + 112 + 8, 0x1000); U32(152 + 112 + 12, (uint)(20 * (names.Length + 1)));
        int section = 152 + 240; U32(section + 8, 512); U32(section + 12, 0x1000); U32(section + 16, 512); U32(section + 20, 512);
        int at = 20 * (names.Length + 1);
        for (int i = 0; i < names.Length; i++) { U32(512 + i * 20 + 12, (uint)(0x1000 + at)); Encoding.ASCII.GetBytes(names[i]).CopyTo(data, 512 + at); at += names[i].Length + 1; }
        return data;
    }
}
