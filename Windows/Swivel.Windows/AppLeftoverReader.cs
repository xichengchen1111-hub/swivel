using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

internal static class AppLeftoverReader
{
    private static readonly Guid LocalLowId = new("A520A1A4-1780-4FF6-BD18-167343C5AF16"); // FOLDERID_LocalAppDataLow
    private static readonly Guid ProgramsId = new("5CD7AEE2-2219-4A67-B85D-6C9CE15660CB"); // FOLDERID_UserProgramFiles

    /// Reads the installation records again, compares them with those shown earlier (opening
    /// each missing app's own earlier subkeys again) and looks for the removed apps' folders.
    /// Throws a UserError when the records now are partial.
    internal static AppLeftoverResult Read(InstalledAppsSnapshot before, CancellationToken token)
    {
        var after = InstalledAppsReader.Read(token); token.ThrowIfCancellationRequested();
        var removed = AppLeftovers.Removed(before, after, InstalledAppsReader.Recheck, token);
        var result = AppLeftovers.Read(removed, after, root => Reported(root, token), path => NativeFiles.MetadataDirectory.Open(path), token);
        if (result.Items.Count == 0 && removed.Apps.Count == 0) return result;
        var registered = RegisteredTargetReader.Read(token);
        return result with { Items = Array.AsReadOnly(result.Items.Select(item => item with
            { Registrations = RegisteredTargets.ForFolder(registered, item.Folder.Path, token) }).ToArray()),
            RegistrationSnapshot = registered, Registrations = Array.AsReadOnly(removed.Apps.SelectMany(app => app.InstallFolders.Count == 0
                ? new[] { new RemovedAppRegistrations(app.Name, null) }
                : app.InstallFolders.Select(folder => new RemovedAppRegistrations(app.Name, folder))).ToArray()) };
    }

    /// For one batch of moves: reads the installation records once now and returns a check that
    /// throws a UserError unless a folder's app is still removed under the search's rules.
    internal static Action<LeftoverFolder> RecordsCheck()
    {
        var now = InstalledAppsReader.Read(CancellationToken.None);
        return folder =>
        {
            AppLeftovers.StillRemoved(folder, now, InstalledAppsReader.Recheck);
            // A fresh registration sample for every move; a batch snapshot must not authorize a later move.
            RegisteredTargets.RequireTrash(RegisteredTargets.ForFolder(RegisteredTargetReader.Read(CancellationToken.None), folder.Path, CancellationToken.None));
        };
    }

    private static string? Reported(LeftoverBase root, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            return root switch
            {
                LeftoverBase.Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
                LeftoverBase.Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify),
                LeftoverBase.LocalLow => Known(LocalLowId, token),
                LeftoverBase.Programs => Known(ProgramsId, token),
                _ => throw new ArgumentOutOfRangeException(nameof(root)),
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or NotSupportedException or ArgumentException) { return null; }
    }

    /// A known folder's path as Windows reports it (not created, not verified), or null. Paths
    /// longer than 512 UTF-16 units are not used.
    private static string? Known(Guid id, CancellationToken token)
    {
        IntPtr pointer = IntPtr.Zero; bool initialized = false;
        try
        {
            int init = CoInitializeEx(IntPtr.Zero, 0); initialized = init >= 0;
            if (!initialized) return null;
            if (SHGetKnownFolderPath(in id, 0x4000, IntPtr.Zero, out pointer) != 0 || pointer == IntPtr.Zero) return null;
            // The OS contract supplies an allocated NUL-terminated string; this bounds the copy.
            var text = new StringBuilder(513);
            for (int i = 0; i <= 512; i++)
            {
                token.ThrowIfCancellationRequested();
                char c = (char)(ushort)Marshal.ReadInt16(pointer, i * 2);
                if (c == '\0') return text.ToString();
                text.Append(c);
            }
            return null;
        }
        finally
        {
            try { if (pointer != IntPtr.Zero) CoTaskMemFree(pointer); }
            finally { if (initialized) CoUninitialize(); }
        }
    }

    /// The running programs' paths, or null when they could not all be listed.
    internal static System.Collections.Generic.IReadOnlyList<string>? ProgramPaths() => AppLeftovers.ProgramPaths(AppMemoryReader.Processes, default);

    [DllImport("ole32.dll", ExactSpelling = true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int CoInitializeEx(IntPtr reserved, uint mode);
    [DllImport("ole32.dll", ExactSpelling = true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern void CoUninitialize();
    [DllImport("shell32.dll", ExactSpelling = true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int SHGetKnownFolderPath(in Guid id, uint flags, IntPtr token, out IntPtr path);
    [DllImport("ole32.dll", ExactSpelling = true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern void CoTaskMemFree(IntPtr memory);
}
