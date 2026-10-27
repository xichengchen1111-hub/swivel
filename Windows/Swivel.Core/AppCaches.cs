using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading;

namespace Swivel.Core;

public enum AppCacheBase { Local, Roaming }
public enum AppCacheProfiles { None, Chromium, Firefox }

/// One app's cache folders at their default places: `Folders` directly in `Container` (below
/// the user's local or roaming app data), and `ProfileFolders` in each profile folder of it.
/// A Chromium profile is a folder holding a Preferences file; every folder of a Firefox
/// Profiles folder is a profile. `Processes` are the app's process names (no ".exe").
public sealed record AppCacheApp(string Id, string Name, AppCacheBase Base, string Container, AppCacheProfiles Profiles,
    IReadOnlyList<string> Folders, IReadOnlyList<string> ProfileFolders, IReadOnlyList<string> Processes);

/// A cache folder found at its app's default place. Profile is null outside profiles.
public sealed record AppCacheFolder(AppCacheApp App, string? Profile, string Folder, string Path);
/// Bounded metadata measurement of a found folder; null on the item when the shared budget ran out first.
public sealed record AppCacheSize(decimal Bytes, int Files, int Entries, bool Partial, string? Issue);
public sealed record AppCacheItem(AppCacheFolder Folder, AppCacheSize? Size);
public enum AppCacheAppState { Found, NotFound, Unavailable }
/// Partial: a limit stopped the search in this app's folder. Unreadable: a folder in it could not be read.
public sealed record AppCacheAppResult(AppCacheApp App, string? Container, AppCacheAppState State, string? Issue, int Profiles, bool Partial, bool Unreadable);
public sealed record AppCacheResult(IReadOnlyList<AppCacheAppResult> Apps, IReadOnlyList<AppCacheItem> Items, bool FolderLimit, bool DiscoveryLimit,
    bool MeasureEntryLimit, bool MeasureTimeLimit, DateTimeOffset Recorded);
public sealed record AppCacheLimits(int DirectoryEntries = 4096, int DiscoveryEntries = 20000, int Profiles = 64, int Folders = 256,
    int MeasureEntries = 100000, int MeasureSeconds = 30);

public static class AppCaches
{
    public const string Notice = "Only the default cache folders of the apps listed below are looked for, in your local and roaming app data. Beta, Dev, Canary, Insiders and other editions, apps from the Microsoft Store, portable copies, other Windows users and custom cache folders are not found. Each listed folder holds data its app downloads or builds again when needed: moving it keeps bookmarks, passwords, history, cookies, extensions and settings, which are stored elsewhere, but pages and the app may load more slowly at first. Nothing is selected for you. A folder is moved, or put back with Undo, only while none of the programs named for its app is running: Edge and Chrome can keep running in the background after their windows close, any running node, python or dotnet program counts for npm, Yarn, pip and NuGet, and other users' programs count too. Before each move the folder must still be a plain local folder at the same place, on a fixed drive with a Recycle Bin; if it is too large for the Recycle Bin, Windows asks before deleting it permanently. Undo Move restores the last moved folders while this window is open, unless the app has made a new folder in their place. Afterwards, restore them from the Recycle Bin.";
    public const string Limits = "Search: at most 4,096 entries per folder, 64 profiles per app, 256 cache folders and 20,000 entries in all. Sizes: one shared metadata budget of 100,000 entries and about 30 soft seconds, shared fairly among the folders, with at most 20,000 entries and four levels below any one cache folder; an incomplete size is a lower bound. Sizes are logical bytes from directory records, not reclaimable space: links, cloud placeholders and system items inside are skipped.";
    public const string Unreadable = "This folder could not be measured.";
    public const string ContainerUnreadable = "This app's folder could not be read.";
    public const string SomeUnreadable = "Some folders in it could not be read.";

    private static AppCacheApp App(string id, string name, AppCacheBase root, string container, AppCacheProfiles profiles, string[] folders, string[] profileFolders, params string[] processes)
        => new(id, name, root, container, profiles, Array.AsReadOnly(folders), Array.AsReadOnly(profileFolders), Array.AsReadOnly(processes));
    private static readonly string[] ChromiumRoot = { "GrShaderCache", "ShaderCache", "GraphiteDawnCache" };
    private static readonly string[] ChromiumProfile = { "Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache" };
    private static readonly string[] Electron = { "Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache" };

    /// Closed list, written for Windows from each app's documented or standard default paths.
    public static IReadOnlyList<AppCacheApp> Apps { get; } = Array.AsReadOnly(new[]
    {
        App("chrome", "Google Chrome", AppCacheBase.Local, @"Google\Chrome\User Data", AppCacheProfiles.Chromium, ChromiumRoot, ChromiumProfile, "chrome"),
        App("edge", "Microsoft Edge", AppCacheBase.Local, @"Microsoft\Edge\User Data", AppCacheProfiles.Chromium, ChromiumRoot, ChromiumProfile, "msedge"),
        App("brave", "Brave", AppCacheBase.Local, @"BraveSoftware\Brave-Browser\User Data", AppCacheProfiles.Chromium, ChromiumRoot, ChromiumProfile, "brave"),
        App("vivaldi", "Vivaldi", AppCacheBase.Local, @"Vivaldi\User Data", AppCacheProfiles.Chromium, ChromiumRoot, ChromiumProfile, "vivaldi"),
        App("firefox", "Firefox", AppCacheBase.Local, @"Mozilla\Firefox\Profiles", AppCacheProfiles.Firefox, Array.Empty<string>(), new[] { "cache2", "startupCache" }, "firefox"),
        App("vscode", "Visual Studio Code", AppCacheBase.Roaming, "Code", AppCacheProfiles.None, Electron.Append("CachedData").ToArray(), Array.Empty<string>(), "Code"),
        App("discord", "Discord", AppCacheBase.Roaming, "discord", AppCacheProfiles.None, Electron, Array.Empty<string>(), "Discord"),
        App("slack", "Slack", AppCacheBase.Roaming, "Slack", AppCacheProfiles.None, Electron, Array.Empty<string>(), "slack"),
        // htmlcache is the data folder of Steam's built-in browser (with its cookies): only its cache folders.
        App("steam", "Steam", AppCacheBase.Local, @"Steam\htmlcache", AppCacheProfiles.None, Electron, Array.Empty<string>(), "steam", "steamwebhelper"),
        App("npm", "npm", AppCacheBase.Local, "npm-cache", AppCacheProfiles.None, new[] { "_cacache" }, Array.Empty<string>(), "node"),
        App("pip", "pip", AppCacheBase.Local, "pip", AppCacheProfiles.None, new[] { "Cache" }, Array.Empty<string>(), "python", "pythonw", "pip"),
        App("yarn", "Yarn", AppCacheBase.Local, "Yarn", AppCacheProfiles.None, new[] { "Cache" }, Array.Empty<string>(), "node"),
        App("nuget", "NuGet", AppCacheBase.Local, "NuGet", AppCacheProfiles.None, new[] { "v3-cache", "plugins-cache" }, Array.Empty<string>(), "dotnet", "nuget", "MSBuild", "devenv"),
    });

    /// Whether any of the app's processes is in `running` (process names, any case); true
    /// when the running processes could not be listed.
    public static bool IsRunning(AppCacheApp app, IReadOnlySet<string>? running) => running is null || app.Processes.Any(running.Contains);
    /// The program files checked for the app, or only those in `running` when given.
    public static string Programs(AppCacheApp app, IReadOnlySet<string>? running = null)
        => string.Join(", ", app.Processes.Where(p => running is null || running.Contains(p)).Select(p => p + ".exe"));

    public static string Size(AppCacheSize? size, Func<string, string> L)
    {
        if (size is null) return L("Not measured");
        if (size.Issue is not null) return L("Could not be measured");
        string bytes = size.Bytes.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
        return size.Partial ? string.Format(L("at least {0}"), bytes) : bytes;
    }

    /// Finds the listed apps' cache folders (directory records only), then measures them with
    /// one shared budget. `reported` gives Windows' raw local/roaming app data path; `open`
    /// opens a held metadata directory by full path.
    public static AppCacheResult Read(Func<AppCacheBase, string?> reported, Func<string, IFolderMetadata> open, CancellationToken token,
        AppCacheLimits? limits = null, Func<TimeSpan>? elapsed = null)
    {
        ArgumentNullException.ThrowIfNull(reported); ArgumentNullException.ThrowIfNull(open);
        limits ??= new();
        if (limits.DirectoryEntries is < 1 or > 4096 || limits.DiscoveryEntries is < 1 or > 20000 || limits.Profiles is < 0 or > 64 || limits.Folders is < 1 or > 256
            || limits.MeasureEntries is < 1 or > 100000 || limits.MeasureSeconds is < 1 or > 30) throw new ArgumentOutOfRangeException(nameof(limits));
        token.ThrowIfCancellationRequested();
        var bases = new Dictionary<AppCacheBase, ReportedFolderPath>();
        foreach (var root in new[] { AppCacheBase.Local, AppCacheBase.Roaming }) { token.ThrowIfCancellationRequested(); bases[root] = ReportedFolderPath.FromReported(reported(root)); }
        var apps = new List<AppCacheAppResult>(); var found = new List<AppCacheFolder>();
        int discovered = 0; bool folderLimit = false, discoveryLimit = false;

        // Direct entries of one folder, within the per-folder and total limits.
        List<FolderEntry> Listed(IFolderMetadata directory, ref bool partial)
        {
            var entries = new List<FolderEntry>();
            foreach (var entry in directory.Entries(token))
            {
                if (entry.Name is "." or "..") continue;
                if (discovered >= limits.DiscoveryEntries) { discoveryLimit = partial = true; break; }
                if (entries.Count >= limits.DirectoryEntries) { partial = true; break; }
                discovered++; entries.Add(entry);
            }
            token.ThrowIfCancellationRequested();
            return entries;
        }
        // A plain directory record with exactly this name (Windows names ignore case).
        static FolderEntry? Plain(List<FolderEntry> entries, string name, bool directory)
            => entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase) && e.Directory == directory && IsPlain(e));
        bool Add(AppCacheApp app, string? profile, string folder, string path)
        {
            if (found.Count >= limits.Folders) { folderLimit = true; return false; }
            found.Add(new(app, profile, folder, path)); return true;
        }

        foreach (var app in Apps)
        {
            token.ThrowIfCancellationRequested();
            var root = bases[app.Base];
            if (!root.CanAttempt) { apps.Add(new(app, null, AppCacheAppState.Unavailable, root.Issue, 0, false, false)); continue; }
            string container = root.Path + "\\" + app.Container;
            IFolderMetadata directory;
            try { directory = open(container); }
            catch (Exception error) when (!token.IsCancellationRequested && Missing(error)) { apps.Add(new(app, container, AppCacheAppState.NotFound, null, 0, false, false)); continue; }
            catch (Exception error) when (!token.IsCancellationRequested && Expected(error)) { apps.Add(new(app, container, AppCacheAppState.Unavailable, error is UserError ? error.Message : ContainerUnreadable, 0, false, false)); continue; }
            int profiles = 0; bool partial = false, unreadable = false; string? issue = null;
            using (directory)
            {
                try
                {
                    var entries = Listed(directory, ref partial);
                    // Direct entries of a child folder; null when it cannot be read. `cut`: a limit
                    // stopped the listing, so a Preferences file may be among the entries not read.
                    List<FolderEntry>? Inside(string name, out bool cut)
                    {
                        cut = false;
                        try { using var child = directory.OpenChild(name); var inside = Listed(child, ref cut); partial |= cut; return inside; }
                        catch (Exception error) when (!token.IsCancellationRequested && Expected(error)) { unreadable = true; return null; }
                    }
                    var cacheRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string name in app.Folders)
                    {
                        if (Plain(entries, name, true) is not { } match) continue;
                        // A Chromium profile may carry a cache folder's name: it is searched as a profile instead.
                        if (app.Profiles == AppCacheProfiles.Chromium && (Inside(match.Name, out bool cut) is not { } inside || cut || Plain(inside, "Preferences", false) is not null)) continue;
                        if (!Add(app, null, match.Name, container + "\\" + match.Name)) { partial = true; break; }
                        cacheRoots.Add(match.Name);
                    }
                    if (app.Profiles != AppCacheProfiles.None && !folderLimit)
                    {
                        foreach (var entry in entries.Where(e => e.Directory && IsPlain(e) && !cacheRoots.Contains(e.Name)).OrderBy(e => e.Name, StringComparer.Ordinal))
                        {
                            token.ThrowIfCancellationRequested();
                            if (discoveryLimit || folderLimit) { partial = true; break; }
                            if (Inside(entry.Name, out _) is not { } inside) continue;
                            if (app.Profiles == AppCacheProfiles.Chromium && Plain(inside, "Preferences", false) is null) continue;
                            // Counted once a folder proves to be a profile, so other folders after the last allowed one do not mark the search partial.
                            if (profiles >= limits.Profiles) { partial = true; break; }
                            profiles++;
                            foreach (string name in app.ProfileFolders)
                                if (Plain(inside, name, true) is { } match && !Add(app, entry.Name, match.Name, container + "\\" + entry.Name + "\\" + match.Name)) { partial = true; break; }
                        }
                    }
                }
                catch (Exception error) when (!token.IsCancellationRequested && Expected(error)) { unreadable = true; issue = ContainerUnreadable; }
            }
            apps.Add(new(app, container, AppCacheAppState.Found, issue, profiles, partial, unreadable));
        }

        // One shared, fair metadata budget, as Compare Common Folders divides its own.
        var watch = Stopwatch.StartNew(); elapsed ??= () => watch.Elapsed;
        var items = new List<AppCacheItem>(found.Count); int remainingEntries = limits.MeasureEntries; bool entryLimit = false, timeLimit = false;
        for (int i = 0; i < found.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var folder = found[i]; int remainingFolders = found.Count - i; double seconds = elapsed().TotalSeconds;
            if (remainingEntries == 0) { entryLimit = true; items.Add(new(folder, null)); continue; }
            if (seconds >= limits.MeasureSeconds) { timeLimit = true; items.Add(new(folder, null)); continue; }
            int quota = Math.Clamp(remainingEntries / remainingFolders, 1, 20000);
            int deadline = Math.Clamp((int)Math.Ceiling(seconds + (limits.MeasureSeconds - seconds) / remainingFolders), 1, limits.MeasureSeconds);
            // Deducted once the folder opens, as Compare Common Folders does: a folder that cannot
            // be opened uses none of the budget, one whose scan then fails uses its whole share.
            IFolderMetadata directory;
            try { directory = open(folder.Path); }
            catch (Exception error) when (!token.IsCancellationRequested && Expected(error))
            { items.Add(new(folder, new(0, 0, 0, true, error is UserError ? error.Message : Unreadable))); continue; }
            remainingEntries -= quota;
            using (directory)
            {
                try
                {
                    var scan = FolderSizes.Scan(directory, token, new(quota, 4, deadline), elapsed);
                    remainingEntries += quota - scan.Entries;
                    entryLimit |= scan.EntryLimit; timeLimit |= scan.TimeLimit;
                    items.Add(new(folder, new(scan.Root.Bytes, scan.Root.Files, scan.Entries, scan.Root.Incomplete || scan.EntryLimit || scan.TimeLimit, null)));
                }
                catch (Exception error) when (!token.IsCancellationRequested && Expected(error))
                { items.Add(new(folder, new(0, 0, 0, true, error is UserError ? error.Message : Unreadable))); }
            }
        }
        token.ThrowIfCancellationRequested();
        return new(new ReadOnlyCollection<AppCacheAppResult>(apps), new ReadOnlyCollection<AppCacheItem>(items), folderLimit, discoveryLimit, entryLimit, timeLimit, DateTimeOffset.Now);
    }

    private static bool IsPlain(FolderEntry entry)
    {
        if ((entry.Attributes & FolderSizes.SkippedAttributes) != 0 || entry.Directory != ((entry.Attributes & 0x10) != 0)) return false;
        try { WindowsPathPolicy.ValidateName(entry.Name); return true; } catch (UserError) { return false; }
    }
    private static bool Missing(Exception error) => error is FileNotFoundException or DirectoryNotFoundException || error is Win32Exception { NativeErrorCode: 2 or 3 };
    private static bool Expected(Exception error) => error is IOException or UnauthorizedAccessException or Win32Exception or UserError or NotSupportedException or SecurityException;
}
