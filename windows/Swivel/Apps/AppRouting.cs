using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Swivel;

/// How an app action hands files over (AppRouting.swift's FileAppTarget.Delivery; stored by the same names).
enum AppDelivery { OpenFiles, CodexDraft, ClaudeCodeDraft, CopyFiles }

static class AppDeliveries
{
    public static readonly AppDelivery[] All = [AppDelivery.OpenFiles, AppDelivery.CodexDraft, AppDelivery.ClaudeCodeDraft, AppDelivery.CopyFiles];

    public static string Id(this AppDelivery delivery) => delivery switch
    {
        AppDelivery.CodexDraft => "codexDraft",
        AppDelivery.ClaudeCodeDraft => "claudeCodeDraft",
        AppDelivery.CopyFiles => "copyFiles",
        _ => "openFiles",
    };

    public static AppDelivery? FromId(string? id) => All.Cast<AppDelivery?>().FirstOrDefault(d => d!.Value.Id() == id);

    public static string Title(this AppDelivery delivery) => delivery switch
    {
        AppDelivery.CodexDraft => L("Codex draft"),
        AppDelivery.ClaudeCodeDraft => L("Claude Code draft"),
        AppDelivery.CopyFiles => L("Copy files and open app"),
        _ => L("Open files"),
    };

    public static bool IsDraft(this AppDelivery delivery) => delivery is AppDelivery.CodexDraft or AppDelivery.ClaudeCodeDraft;

    /// The deep-link scheme of a draft delivery ("codex", "claude"); null for the others.
    public static string? Scheme(this AppDelivery delivery) => delivery switch
    {
        AppDelivery.CodexDraft => "codex",
        AppDelivery.ClaudeCodeDraft => "claude",
        _ => null,
    };
}

/// One rule of App Actions: an app in the Apps menu, how files reach it and which extensions it takes. Stored in
/// `appTargets.v1` with the Mac's JSON shape (id, name, bundleID, appPath, delivery, extensions, enabled).
/// On Windows `bundleID` holds a packaged app's AppUserModelID (e.g. "Microsoft.Paint_8wekyb3d8bbwe!App") and
/// `appPath` a desktop app's .exe; one of them is set.
sealed record FileAppTarget
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("bundleID")] public string BundleId { get; init; } = "";
    [JsonPropertyName("appPath")] public string AppPath { get; init; } = "";

    [JsonPropertyName("delivery")]
    public string DeliveryId
    {
        get => Delivery.Id();
        init
        {
            Delivery = AppDeliveries.FromId(value) ?? AppDelivery.OpenFiles;
            UnknownDelivery = AppDeliveries.FromId(value) is null;
        }
    }

    [JsonIgnore] public AppDelivery Delivery { get; init; }
    /// A stored delivery this version doesn't know: the rule is dropped rather than silently opening files.
    [JsonIgnore] bool UnknownDelivery { get; init; }

    [JsonPropertyName("extensions")] public IReadOnlyList<string> Extensions { get; init; } = [];
    [JsonPropertyName("enabled")] public bool Enabled { get; init; } = true;

    [JsonIgnore] public string ActionId => "app." + Id;

    [JsonIgnore]
    public bool IsValid => Id.Length > 0 && !UnknownDelivery && Name.Trim().Length > 0 && Name.Length <= 80;

    /// Every file has one of the extensions (any file when there are none). Folders have no extension.
    public bool Accepts(IReadOnlyList<string> files) =>
        files.Count > 0 && files.All(f => Extensions.Count == 0 || Extensions.Contains(FileKinds.Ext(f)));

    public bool Equals(FileAppTarget? other) =>
        other is not null && Id == other.Id && Name == other.Name && BundleId == other.BundleId && AppPath == other.AppPath
        && Delivery == other.Delivery && Enabled == other.Enabled && Extensions.SequenceEqual(other.Extensions);

    public override int GetHashCode() => HashCode.Combine(Id, Name, BundleId, AppPath, Delivery, Enabled, Extensions.Count);

    /// Codex and Claude Code, ready on first launch. (The Mac's Claude starter names its bundle ID; Windows finds both
    /// through their registered link schemes.)
    public static readonly FileAppTarget[] Starters =
    [
        new() { Id = "codex", Name = "Codex", Delivery = AppDelivery.CodexDraft },
        new() { Id = "claude-code", Name = "Claude Code", Delivery = AppDelivery.ClaudeCodeDraft },
    ];

    /// "PNG, .jpg，pdf  png" → ["png", "jpg", "pdf"]: lower case, no dots, letters and digits only, no repeats.
    public static List<string> NormalizedExtensions(string text)
    {
        var seen = new HashSet<string>();
        return System.Text.RegularExpressions.Regex.Split(text.ToLowerInvariant(), @"[,，\s]+")
            .Select(part => part.Trim('.'))
            .Where(part => part.Length > 0 && part.All(char.IsLetterOrDigit) && seen.Add(part))
            .ToList();
    }
}

/// The saved App Actions rules (Prefs.fileAppTargets on the Mac).
static class AppTargets
{
    const string Key = "appTargets.v1";

    /// Starters until something is saved; invalid rules and repeated ids are left out.
    public static IReadOnlyList<FileAppTarget> All
    {
        get
        {
            if (!Prefs.Has(Key)) return FileAppTarget.Starters;
            List<FileAppTarget>? values;
            try { values = Prefs.Get<List<FileAppTarget>>(Key); }
            catch (Exception) { values = null; }
            if (values is null) return FileAppTarget.Starters;
            var ids = new HashSet<string>();
            return values.Where(t => t is not null && t.IsValid && ids.Add(t.Id)).ToList();
        }
        set
        {
            var ids = new HashSet<string>();
            Prefs.Set(Key, value.Where(t => t.IsValid && ids.Add(t.Id)).ToList());
        }
    }

    /// Adds a rule or replaces the one with the same id.
    public static void Save(FileAppTarget target)
    {
        var latest = All.ToList();
        int index = latest.FindIndex(t => t.Id == target.Id);
        if (index >= 0) latest[index] = target; else latest.Add(target);
        All = latest;
    }
}

/// Codex and Claude Code drafts: their official deep links with a prompt naming the local files. File contents are
/// never read, uploaded or sent; the user reviews and sends in the app.
static class AssistantDraft
{
    // JSON strings keep quotes, backslashes, newlines and unusual names unambiguous; non-ASCII names stay readable.
    static readonly JsonSerializerOptions PathJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string ReferenceText(IReadOnlyList<string> files) =>
        L("Selected local files:") + "\n" + string.Join("\n", files.Select(f => "- " + JsonSerializer.Serialize(f, PathJson)));

    /// `codex://threads/new?prompt=…&path=…` or `claude://code/new?q=…&folder=…`. The folder is given only when every
    /// file is in the same one (a folder counts as itself): never a broad common root. Claude's file parameter is not
    /// supported, so files are named in the text only.
    public static string Url(AppDelivery delivery, IReadOnlyList<string> files)
    {
        if (files.Count == 0 || !delivery.IsDraft() || files.Any(f => !System.IO.Path.IsPathFullyQualified(f)))
            throw new ToolError(L("Choose files for this draft."));
        var text = ReferenceText(files);
        if (Encoding.UTF8.GetByteCount(text) > 12000) throw new ToolError(L("Too many file paths for one draft. Choose fewer files."));
        bool codex = delivery == AppDelivery.CodexDraft;
        var url = new StringBuilder(codex ? "codex://threads/new" : "claude://code/new");
        url.Append(codex ? "?prompt=" : "?q=").Append(Uri.EscapeDataString(text));
        var folders = files.Select(f => Directory.Exists(f) ? f : System.IO.Path.GetDirectoryName(f) ?? f)
            .Select(f => System.IO.Path.TrimEndingDirectorySeparator(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (folders.Count == 1) url.Append(codex ? "&path=" : "&folder=").Append(Uri.EscapeDataString(folders[0]));
        // Windows hands the link to its app on a command line (at most 32 767 characters).
        if (url.Length > 30000) throw new ToolError(L("Too many file paths for one draft. Choose fewer files."));
        return url.ToString();
    }
}

/// Where an app action's app is: a desktop .exe, a packaged app (its AppUserModelID), or the app registered for a
/// draft's link scheme.
sealed record AppLocation(AppLocation.Kinds Kind, string Value, string DisplayName)
{
    public enum Kinds { Executable, Packaged, LinkHandler }
}

/// Starting apps, opening links and the clipboard, behind one seam so the tests never launch anything.
class AppLauncher
{
    public static AppLauncher Default { get; } = new();

    /// Starts the app with the files (none: just starts it). Returns once it has launched.
    public virtual Task Start(AppLocation app, IReadOnlyList<string> files) => app.Kind switch
    {
        AppLocation.Kinds.Packaged => PackagedApps.Activate(app.Value, files),
        _ => Task.Run(() => StartExecutable(app.Value, files)),
    };

    /// Opens a deep link with the app Windows has for its scheme.
    public virtual Task OpenUrl(string url)
    {
        using var _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    /// The friendly name of the app registered for a link scheme, or null when none is.
    public virtual string? SchemeHandler(string scheme) => Association.ForScheme(scheme);

    public virtual bool PackagedAppExists(string appUserModelId) => PackagedApps.Exists(appUserModelId);

    public virtual bool ExecutableExists(string path) => File.Exists(path);

    /// Puts the files on the clipboard as a file list, as Explorer's Copy does.
    public virtual bool CopyFiles(IReadOnlyList<string> files)
    {
        var list = new System.Collections.Specialized.StringCollection();
        list.AddRange([.. files]);
        // Another app can hold the clipboard for a moment; try a few times.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                System.Windows.Clipboard.SetFileDropList(list);
                return true;
            }
            catch (ExternalException) { Thread.Sleep(40); }
        }
        return false;
    }

    static void StartExecutable(string exe, IReadOnlyList<string> files)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = System.IO.Path.GetDirectoryName(exe) ?? "",
        };
        foreach (var file in files) info.ArgumentList.Add(file);
        using var _ = Process.Start(info);
    }
}

/// Runs an app action (FileAppRouting in AppRouting.swift).
static class FileAppRouting
{
    /// Windows starts an app with its files on one command line of at most 32 767 characters.
    internal const int CommandLineLimit = 32000;

    /// Where the target's app is now, or null when it isn't installed (for a draft: when nothing on this PC opens its
    /// links). A draft always goes to the app registered for its scheme, as the Mac opens the link with the app that
    /// declares it.
    public static AppLocation? Locate(FileAppTarget target, AppLauncher? launcher = null)
    {
        launcher ??= AppLauncher.Default;
        if (target.Delivery.Scheme() is { } scheme)
            return launcher.SchemeHandler(scheme) is { } handler ? new AppLocation(AppLocation.Kinds.LinkHandler, scheme, handler) : null;
        if (target.BundleId.Length > 0)
            return launcher.PackagedAppExists(target.BundleId) ? new AppLocation(AppLocation.Kinds.Packaged, target.BundleId, target.Name) : null;
        if (target.AppPath.Length > 0 && !IsSwivel(target.AppPath) && launcher.ExecutableExists(target.AppPath))
            return new AppLocation(AppLocation.Kinds.Executable, target.AppPath, System.IO.Path.GetFileNameWithoutExtension(target.AppPath));
        return null;
    }

    /// Swivel itself is never a target (it would open another copy of the app).
    public static bool IsSwivel(string path)
    {
        try
        {
            return Environment.ProcessPath is { } own && string.Equals(System.IO.Path.GetFullPath(path), own, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }

    /// Opens the files in the target's app, or prepares its draft. Checks the rule is still saved and on, and that the
    /// files still exist; the clipboard is filled only after the app has started.
    public static async Task<ActionResult> Run(FileAppTarget target, IReadOnlyList<string> files, AppLauncher? launcher = null)
    {
        launcher ??= AppLauncher.Default;
        if (!Feature.AppRouting.IsOn() || !target.Enabled || !AppTargets.All.Contains(target) || !target.Accepts(files))
            throw new ToolError(L("This app action is unavailable or turned off."));
        foreach (var file in files)
            if (!File.Exists(file) && !Directory.Exists(file)) throw new ToolError(L("File is no longer available"));
        var app = Locate(target, launcher) ?? throw new ToolError(target.Delivery.Scheme() is { } scheme
            ? L("No app on this PC opens %@ links. Install %@, then try again.", scheme + "://", target.Name)
            : L("%@ is not installed. Choose its application in App Actions.", target.Name));
        if (target.Delivery == AppDelivery.OpenFiles && app.Kind == AppLocation.Kinds.Executable
            && app.Value.Length + files.Sum(f => f.Length + 3) > CommandLineLimit)
            throw new ToolError(L("Too many files for one app launch. Choose fewer files."));
        try
        {
            switch (target.Delivery)
            {
                case AppDelivery.CodexDraft or AppDelivery.ClaudeCodeDraft:
                    await launcher.OpenUrl(AssistantDraft.Url(target.Delivery, files));
                    break;
                case AppDelivery.CopyFiles:
                    await launcher.Start(app, []);
                    break;
                default:
                    await launcher.Start(app, files);
                    break;
            }
        }
        catch (ToolError) { throw; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or COMException or InvalidOperationException or IOException)
        {
            Log.Write($"App action {target.Id} failed: {ex}");
            throw new ToolError(L("Could not open %@.", target.Name));
        }
        if (target.Delivery == AppDelivery.CopyFiles && !launcher.CopyFiles(files))
            throw new ToolError(L("Could not copy the selected files."));
        return new ActionResult(target.Delivery switch
        {
            AppDelivery.CodexDraft or AppDelivery.ClaudeCodeDraft => L("Opened %@ draft with file references. Review and send there.", target.Name),
            AppDelivery.CopyFiles => L("Files copied. Paste them in %@.", target.Name),
            _ => L("Opened files in %@", target.Name),
        });
    }
}

/// Which app Windows has for a link scheme (AssocQueryString, which also knows packaged apps' protocols).
static unsafe partial class Association
{
    const uint ASSOCF_NOTRUNCATE = 0x20, ASSOCF_IS_PROTOCOL = 0x1000;
    const int ASSOCSTR_EXECUTABLE = 2, ASSOCSTR_FRIENDLYAPPNAME = 4;

    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int AssocQueryStringW(uint flags, int str, string assoc, string? extra, char* output, ref uint size);

    static string? Query(string scheme, int what)
    {
        uint size = 1024;
        var buffer = stackalloc char[1024];
        int hr = AssocQueryStringW(ASSOCF_IS_PROTOCOL | ASSOCF_NOTRUNCATE, what, scheme, null, buffer, ref size);
        if (hr != 0 || size <= 1) return null;
        var text = new string(buffer, 0, (int)size - 1).Trim();
        return text.Length > 0 ? text : null;
    }

    /// The app's friendly name (or its .exe), null when no app is registered for `scheme`.
    public static string? ForScheme(string scheme)
    {
        try
        {
            var exe = Query(scheme, ASSOCSTR_EXECUTABLE);
            var name = Query(scheme, ASSOCSTR_FRIENDLYAPPNAME);
            if (exe is null && name is null) return null;
            // An unregistered scheme can come back as Windows' "look for an app" handler; that isn't an app for it.
            if (exe != null && System.IO.Path.GetFileName(exe).Equals("OpenWith.exe", StringComparison.OrdinalIgnoreCase)) return null;
            return name ?? System.IO.Path.GetFileNameWithoutExtension(exe);
        }
        catch (Exception) { return null; }
    }
}

/// Packaged (Microsoft Store / MSIX) apps, addressed by AppUserModelID through shell:AppsFolder.
static partial class PackagedApps
{
    static readonly Guid IID_IShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    static readonly Guid CLSID_ApplicationActivationManager = new("45BA127D-10A8-46EA-8AB7-56EA9078943C");

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingName(string path, IntPtr bindContext, in Guid riid, out IntPtr item);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr ILCreateFromPathW(string path);

    [LibraryImport("shell32.dll")]
    private static partial void ILFree(IntPtr pidl);

    [LibraryImport("shell32.dll")]
    private static partial int SHCreateShellItemArrayFromIDLists(uint count, IntPtr[] pidls, out IntPtr array);

    public static bool Exists(string appUserModelId)
    {
        if (appUserModelId.Length == 0 || appUserModelId.IndexOfAny(['\\', '/']) >= 0) return false;
        if (SHCreateItemFromParsingName(@"shell:AppsFolder\" + appUserModelId, IntPtr.Zero, IID_IShellItem, out var item) != 0) return false;
        Marshal.Release(item);
        return true;
    }

    /// Starts the app, with the files if there are any (IApplicationActivationManager, on its own STA thread).
    public static Task Activate(string appUserModelId, IReadOnlyList<string> files) => Sta.Run<bool>(() =>
    {
        var manager = (IApplicationActivationManager)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_ApplicationActivationManager)!)!;
        try
        {
            if (files.Count == 0)
            {
                manager.ActivateApplication(appUserModelId, null, 0, out _);
                return true;
            }
            var pidls = files.Select(ILCreateFromPathW).ToArray();
            try
            {
                if (pidls.Any(p => p == IntPtr.Zero)) throw new IOException(L("File is no longer available"));
                Marshal.ThrowExceptionForHR(SHCreateShellItemArrayFromIDLists((uint)pidls.Length, pidls, out var array));
                try { manager.ActivateForFile(appUserModelId, array, "open", out _); }
                finally { Marshal.Release(array); }
                return true;
            }
            finally
            {
                foreach (var pidl in pidls) if (pidl != IntPtr.Zero) ILFree(pidl);
            }
        }
        finally { Marshal.ReleaseComObject(manager); }
    });

    [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IApplicationActivationManager
    {
        void ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string? arguments, int options, out uint processId);
        void ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, IntPtr itemArray, [MarshalAs(UnmanagedType.LPWStr)] string? verb, out uint processId);
        void ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, IntPtr itemArray, out uint processId);
    }
}

/// An app the picker offers: a desktop app from a Start menu shortcut, or a packaged app.
sealed record InstalledApp(string Name, string AppPath, string BundleId);

/// The apps in the Start menu: shortcuts in the all-users and personal Programs folders (resolved to their .exe), and
/// packaged apps from shell:AppsFolder. (The Mac lists /Applications.)
static class InstalledApps
{
    public static Task<List<InstalledApp>> Load() => Sta.Run(List);

    static List<InstalledApp> List()
    {
        var found = new List<InstalledApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), Environment.GetFolderPath(Environment.SpecialFolder.Programs) })
        {
            if (root.Length == 0 || !Directory.Exists(root)) continue;
            List<string> links;
            try { links = Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).ToList(); }
            catch (Exception) { continue; }
            foreach (var link in links)
            {
                var target = ShortcutTarget(link);
                if (target is null || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(target)) continue;
                var file = System.IO.Path.GetFileName(target);
                if (file.StartsWith("unins", StringComparison.OrdinalIgnoreCase) || file.Contains("uninst", StringComparison.OrdinalIgnoreCase)) continue;
                if (FileAppRouting.IsSwivel(target) || !seen.Add(target)) continue;
                found.Add(new InstalledApp(System.IO.Path.GetFileNameWithoutExtension(link), target, ""));
            }
        }
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            foreach (dynamic item in folder.Items())
            {
                string name = item.Name, id = item.Path;
                // Packaged apps' IDs are "PackageFamilyName!App"; desktop apps already came from their shortcuts.
                if (string.IsNullOrWhiteSpace(name) || id is null || !id.Contains('!') || !seen.Add(id)) continue;
                found.Add(new InstalledApp(name, "", id));
            }
        }
        catch (Exception ex) { Log.Write($"Packaged apps: {ex.Message}"); }
        return found
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    static string? ShortcutTarget(string link)
    {
        object? shortcut = null;
        try
        {
            shortcut = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"))!);
            if (shortcut is null) return null;
            ((System.Runtime.InteropServices.ComTypes.IPersistFile)shortcut).Load(link, 0);
            var path = new StringBuilder(1024);
            ((IShellLinkW)shortcut).GetPath(path, path.Capacity, IntPtr.Zero, 0);
            return path.Length > 0 ? path.ToString() : null;
        }
        catch (Exception) { return null; }
        finally { if (shortcut != null) Marshal.ReleaseComObject(shortcut); }
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int size, IntPtr findData, uint flags);
    }
}
