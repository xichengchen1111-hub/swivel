using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

// Holds the chosen file (read-only handle, no write or delete sharing, ancestors pinned) for
// the whole check, so the signature, download mark and imports all describe the same file.
// Nothing here runs or loads it. The only change it can make is RemoveWarning, which the
// dialog offers solely for a validly signed, trusted file after a confirmation.
internal sealed class AppCheckFile : IDisposable
{
    private readonly NativeFiles.Input input;
    internal string Path => input.Path;
    internal bool Installer { get; }
    internal PeStructure? Pe { get; }
    internal bool PeUnreadable { get; }
    internal PeImports? Imports { get; }
    internal IReadOnlyList<ImportFact>? ImportFacts { get; }
    internal Architecture Os { get; } = RuntimeInformation.OSArchitecture;
    internal ArchFit? Fit { get; }
    internal ZoneFacts? Zone { get; private set; }
    internal TrustFacts Trust { get; private set; }
    internal TrustFacts? Online { get; private set; }

    private AppCheckFile(NativeFiles.Input input, CancellationToken token)
    {
        this.input = input;
        Installer = System.IO.Path.GetExtension(input.Path).Equals(".msi", StringComparison.OrdinalIgnoreCase);
        if (!Installer)
        {
            try
            {
                Pe = PeInspection.Read(input.Stream, token);
                Imports = AppCheck.ReadImports(input.Stream, token);
            }
            catch (UserError) { PeUnreadable = true; }
            if (Pe is not null)
            {
                Fit = AppCheck.Fit(Pe.Machine, Pe.ClrDirectory == PeDirectoryDeclaration.Declared, Os);
                string app = System.IO.Path.GetDirectoryName(input.Path)!;
                string system = Pe.Machine == 0x14c && Environment.Is64BitOperatingSystem ? Environment.GetFolderPath(Environment.SpecialFolder.SystemX86) : Environment.SystemDirectory;
                string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                string[] folders = { app, system, windows };
                // Attribute lookups only; no file is opened or loaded.
                ImportFacts = AppCheck.Locate(Imports?.Names ?? Array.Empty<string>(), (i, name) => folders[i].Length > 0 && File.Exists(System.IO.Path.Combine(folders[i], name)), token);
            }
        }
        token.ThrowIfCancellationRequested();
        Zone = ReadZone();
        token.ThrowIfCancellationRequested();
        Trust = AppTrustReader.Verify(input.Stream.SafeFileHandle, input.Path, false);
        token.ThrowIfCancellationRequested();
    }

    internal static AppCheckFile Open(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (extension is not ".exe" and not ".msi" and not ".dll") throw new UserError("Choose one local program (.exe), installer (.msi) or DLL to check.");
        var input = NativeFiles.OpenRead(path);
        try { return new(input, token); } catch { input.Dispose(); throw; }
    }

    private ZoneFacts? ReadZone()
    {
        try { return AppCheck.ParseZone(NativeFiles.ReadZoneIdentifier(input)); }
        catch (Win32Exception error) when (error.NativeErrorCode is 123 or 1) { return null; } // no alternate streams on this drive
    }

    /// Whole-chain revocation; Windows may contact the certificate authorities' servers.
    /// A failed online result stays recorded and blocks warning removal for this window.
    internal TrustFacts CheckOnline()
    {
        var result = AppTrustReader.Verify(input.Stream.SafeFileHandle, input.Path, true);
        if (AppCheck.OnlineAllows(Online, DateTime.UtcNow)) Online = result; // a blocking result is never replaced
        return result;
    }

    internal bool CanRemoveWarning => AppCheck.CanRemoveWarning(AppCheck.Classify(Trust, DateTime.UtcNow), Zone) && AppCheck.OnlineAllows(Online, DateTime.UtcNow);

    /// Re-verifies the held file offline, then deletes only its Zone.Identifier stream.
    internal void RemoveWarning()
    {
        Trust = AppTrustReader.Verify(input.Stream.SafeFileHandle, input.Path, false);
        Zone = ReadZone();
        if (!CanRemoveWarning) throw new UserError("Only a file that is validly signed by a trusted publisher and still carries a download warning can have the warning removed here.");
        if (!DeleteFile(input.Path + ":Zone.Identifier"))
        {
            int error = Marshal.GetLastWin32Error();
            if (error is not 2 and not 3) throw new Win32Exception(error);
        }
        Zone = ReadZone();
    }

    public void Dispose() => input.Dispose();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "DeleteFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteFile(string path);
}
