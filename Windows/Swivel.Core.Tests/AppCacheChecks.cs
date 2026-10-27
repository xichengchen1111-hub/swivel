using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Swivel.Core;

internal static class AppCacheChecks
{
    private const string Local = @"C:\Users\me\AppData\Local", Roaming = @"C:\Users\me\AppData\Roaming";

    internal static void Run(Action<bool,string> check, Action<Action,string> reject)
    {
        check(AppCaches.Apps.Select(a => a.Id).Distinct().Count() == AppCaches.Apps.Count && AppCaches.Apps.All(a => a.Processes.Count > 0 && a.Processes.All(p => p.Length > 0 && !p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))),
            "Cache catalogue has unique app IDs and process names without extensions");
        bool namesValid = true;
        foreach (var app in AppCaches.Apps)
            foreach (string name in app.Container.Split('\\').Concat(app.Folders).Concat(app.ProfileFolders))
                try { WindowsPathPolicy.ValidateName(name); } catch (UserError) { namesValid = false; }
        check(namesValid && AppCaches.Apps.All(a => (a.Profiles == AppCacheProfiles.None) == (a.ProfileFolders.Count == 0) && a.Folders.Count + a.ProfileFolders.Count > 0),
            "Every catalogue container and cache folder is a plain relative Windows name, and profile apps list profile folders");
        check(!AppCaches.Apps.SelectMany(a => a.Folders.Concat(a.ProfileFolders)).Any(f => f is "Network" or "Local Storage" or "IndexedDB" or "Service Worker" or "Cookies" or "User Data" or "Profiles" or "htmlcache"),
            "Catalogue never names site data, cookies, whole profiles or Steam's whole browser data folder");
        reject(() => ((IList<AppCacheApp>)AppCaches.Apps).Clear(), "Cache catalogue is immutable");

        var tree = Tree();
        var result = AppCaches.Read(Reported, tree.Open, CancellationToken.None);
        AppCacheItem Item(string path) => result.Items.Single(i => i.Folder.Path == path);
        string chrome = Local + @"\Google\Chrome\User Data", vivaldi = Local + @"\Vivaldi\User Data";
        var paths = result.Items.Select(i => i.Folder.Path).ToArray();
        check(paths.SequenceEqual(new[]
        {
            chrome + @"\GrShaderCache", chrome + @"\Default\Cache", chrome + @"\Default\Code Cache", chrome + @"\Default\GPUCache", chrome + @"\Profile 1\Cache",
            vivaldi + @"\GrShaderCache", vivaldi + @"\ShaderCache\Cache",
            Local + @"\Mozilla\Firefox\Profiles\abc.default-release\cache2", Roaming + @"\Code\Cache", Roaming + @"\Code\code cache", Roaming + @"\discord\Cache",
            Local + @"\Steam\htmlcache\Cache",
        }), "Found exactly the catalogue folders at their default places, in catalogue order, keeping the folder's own name: " + string.Join(" | ", paths));
        check(!paths.Any(p => p.Contains("Crashpad") || p.Contains("Guest Profile") || p.Contains("Zzz") || p == chrome + @"\ShaderCache" || p == vivaldi + @"\ShaderCache" || p.Contains("Network") || p.Contains("Cookies") || p.Contains("_cacache") || p.EndsWith(@"\htmlcache")),
            "A folder without a Preferences file is no Chromium profile; a profile with a cache folder's name is searched as a profile; links, site data, Steam's whole browser data and a cache name used by a file are never listed");
        var apps = result.Apps.ToDictionary(a => a.App.Id);
        check(apps["chrome"].State == AppCacheAppState.Found && apps["chrome"].Profiles == 2 && !apps["chrome"].Partial && apps["edge"].State == AppCacheAppState.NotFound && apps["edge"].Issue is null && apps["vivaldi"].Profiles == 1,
            "Chromium profiles are counted and a missing app is reported as not found, not as an empty cache");
        check(apps["chrome"].Unreadable && apps["chrome"].Issue is null && !apps["firefox"].Unreadable, "A folder that cannot be read during the search is reported as unreadable, not as a limit");
        check(apps["brave"].State == AppCacheAppState.Unavailable && apps["brave"].Issue == "Choose a local folder without links, system attributes or cloud placeholders.",
            "An app folder Windows reports as a link stays visible with its reason and is never searched");
        check(apps["firefox"].Profiles == 2 && apps["npm"].State == AppCacheAppState.Found && !result.Items.Any(i => i.Folder.App.Id == "npm"), "Firefox counts every profile folder; a found app with no cache folder lists nothing");
        check(Item(chrome + @"\Default\Cache").Folder.Profile == "Default" && Item(chrome + @"\GrShaderCache").Folder.Profile is null, "Profile names are kept per folder");
        var cache = Item(chrome + @"\Default\Cache").Size!;
        check(cache.Bytes == 300 && cache.Files == 2 && !cache.Partial && cache.Issue is null && Item(chrome + @"\Profile 1\Cache").Size!.Bytes == 0, "Each found folder is measured with directory records only");
        var discord = Item(Roaming + @"\discord\Cache").Size!;
        check(discord.Issue == AppCaches.Unreadable && discord.Partial && discord.Bytes == 0, "A folder that cannot be measured stays listed with its reason");
        check(!result.FolderLimit && !result.DiscoveryLimit && !result.MeasureEntryLimit && !result.MeasureTimeLimit, "No limit reached on the small fixture");
        check(tree.Opened > 0 && tree.Opened == tree.Disposed, "Every opened folder is released: " + tree.Opened + "/" + tree.Disposed);

        var noRoaming = AppCaches.Read(b => b == AppCacheBase.Local ? Local : null, Tree().Open, CancellationToken.None);
        check(noRoaming.Apps.Where(a => a.App.Base == AppCacheBase.Roaming).All(a => a.State == AppCacheAppState.Unavailable && a.Issue == ReportedFolderPath.Unreported)
            && noRoaming.Items.All(i => i.Folder.App.Base == AppCacheBase.Local) && noRoaming.Items.Count == 9, "An unreported roaming folder only removes the roaming apps");
        var unsafeBase = AppCaches.Read(b => b == AppCacheBase.Local ? @"\\server\share\Local" : Roaming, Tree().Open, CancellationToken.None);
        check(unsafeBase.Apps.Where(a => a.App.Base == AppCacheBase.Local).All(a => a.State == AppCacheAppState.Unavailable && a.Issue is not null), "A network or unsafe app-data path is never opened");

        var few = AppCaches.Read(Reported, Tree().Open, CancellationToken.None, new(DirectoryEntries: 2));
        check(few.Apps.Single(a => a.App.Id == "chrome").Partial, "The per-folder entry limit marks the app's search partial");
        check(few.Apps.Single(a => a.App.Id == "vivaldi").Partial && !few.Items.Any(i => i.Folder.Path.StartsWith(Local + @"\Vivaldi\User Data\ShaderCache", StringComparison.Ordinal)),
            "A cache-named folder whose listing was cut short is never listed whole, since its Preferences file may be among the entries not read");
        var oneProfile = AppCaches.Read(Reported, Tree().Open, CancellationToken.None, new(Profiles: 1));
        check(oneProfile.Apps.Single(a => a.App.Id == "chrome").Profiles == 1 && oneProfile.Apps.Single(a => a.App.Id == "chrome").Partial, "The profile limit stops the search and says so");
        var twoProfiles = AppCaches.Read(Reported, Tree().Open, CancellationToken.None, new(Profiles: 2)).Apps.Single(a => a.App.Id == "chrome");
        check(twoProfiles.Profiles == 2 && !twoProfiles.Partial, "Folders after the last allowed profile that are no profile do not mark the search partial");
        var twoFolders = AppCaches.Read(Reported, Tree().Open, CancellationToken.None, new(Folders: 2));
        check(twoFolders.Items.Count == 2 && twoFolders.FolderLimit, "The folder limit keeps the first folders and says so");
        var shortSearch = AppCaches.Read(Reported, Tree().Open, CancellationToken.None, new(DiscoveryEntries: 3));
        check(shortSearch.DiscoveryLimit, "The total search entry limit is reported");

        var budget = AppCaches.Read(Reported, Tree().Open, CancellationToken.None, new(MeasureEntries: 1));
        check(budget.MeasureEntryLimit && budget.Items.Count == 12 && budget.Items.Count(i => i.Size is null) > 0 && budget.Items.All(i => i.Size is null || i.Size.Entries <= 1),
            "The shared size budget leaves later folders unmeasured but listed");
        check(AppCaches.Size(budget.Items.First(i => i.Size is null).Size, s => s) == "Not measured", "An unmeasured folder shows no size");
        var late = AppCaches.Read(Reported, Tree().Open, CancellationToken.None, elapsed: () => TimeSpan.FromSeconds(31));
        check(late.MeasureTimeLimit && late.Items.All(i => i.Size is null), "The shared time budget is reported");
        // A folder that cannot be opened uses none of the budget: the next one gets its share.
        var shared = Tree();
        var unopened = AppCaches.Read(Reported, path => path == chrome + @"\GrShaderCache" ? throw new UnauthorizedAccessException() : shared.Open(path), CancellationToken.None, new(Folders: 2, MeasureEntries: 2));
        var full = unopened.Items.Single(i => i.Folder.Path == chrome + @"\Default\Cache").Size!;
        check(unopened.Items.Single(i => i.Folder.Path == chrome + @"\GrShaderCache").Size!.Issue == AppCaches.Unreadable && full.Bytes == 300 && !full.Partial && !unopened.MeasureEntryLimit,
            "A folder that cannot be opened leaves its share of the size budget to the next folder");
        check(AppCaches.Size(new(1234, 2, 3, true, null), s => s).StartsWith("at least ", StringComparison.Ordinal) && AppCaches.Size(new(0, 0, 0, true, "x"), s => s) == "Could not be measured", "Partial sizes read as lower bounds");

        var untouched = Tree();
        reject(() => AppCaches.Read(Reported, untouched.Open, new CancellationToken(true)), "A cancelled search stops before reading");
        check(untouched.Opened == 0, "A search cancelled beforehand opens no folder");
        var stop = new CancellationTokenSource(); var stopped = Tree();
        reject(() => AppCaches.Read(Reported, path => { if (path == chrome + @"\Default\Cache") stop.Cancel(); return stopped.Open(path); }, stop.Token), "Cancelling while measuring stops the read");
        check(stopped.Opened > 0 && stopped.Opened == stopped.Disposed, "Cancelling while measuring releases every opened folder: " + stopped.Opened + "/" + stopped.Disposed);
        foreach (var bad in new AppCacheLimits[] { new(DirectoryEntries: 0), new(DirectoryEntries: 4097), new(DiscoveryEntries: 20001), new(Profiles: 65), new(Folders: 257), new(MeasureEntries: 100001), new(MeasureSeconds: 31) })
            reject(() => AppCaches.Read(Reported, Tree().Open, CancellationToken.None, bad), "Out-of-range cache search limits rejected");

        var edge = AppCaches.Apps.Single(a => a.Id == "edge");
        check(AppCaches.IsRunning(edge, null) && AppCaches.IsRunning(edge, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MSEDGE" })
            && !AppCaches.IsRunning(edge, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "msedgewebview2", "chrome" }), "An app counts as running by its exact process names, and as running when processes could not be listed");
        var nuget = AppCaches.Apps.Single(a => a.Id == "nuget");
        check(AppCaches.Programs(AppCaches.Apps.Single(a => a.Id == "npm")) == "node.exe" && AppCaches.Programs(nuget, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "DOTNET", "chrome" }) == "dotnet.exe",
            "The checked programs are named with their file names, and only the running ones when asked");

        var prefs = new Prefs(); var action = Catalog.Actions.Single(a => a.Id == "doctor.appcaches");
        check(action.Feature == Feature.Doctor && !action.RequiresFiles && !Catalog.Available(prefs, Array.Empty<string>()).Contains(action), "App-cache review is off by default and uses no selected files");
        prefs.Set(Feature.Doctor, true);
        check(Catalog.Available(prefs, Array.Empty<string>()).Contains(action) && WindowsDoctor.Areas.Single(a => a.Id == "caches").ActionIds.Contains(action.Id), "App-cache review is reachable from the Caches area");
        foreach (string text in new[] { AppCaches.Notice, AppCaches.Limits, AppCaches.Unreadable, AppCaches.ContainerUnreadable, AppCaches.SomeUnreadable, "Not measured", "Could not be measured", "at least {0}" })
            check(Strings.Table.ContainsKey(text), "App-cache text translated: " + text[..Math.Min(40, text.Length)]);
    }

    private static string? Reported(AppCacheBase root) => root == AppCacheBase.Local ? Local + "\\" : Roaming;

    private static Fake Tree()
    {
        const uint Dir = 0x10, Link = 0x10 | 0x400;
        var local = D("Local",
            D("Google", D("Chrome", D("User Data",
                D("GrShaderCache", F("data_0", 10)),
                N("ShaderCache", Link),
                D("Default", F("Preferences"), D("Cache", F("a", 100), F("b", 200)), D("Code Cache"), D("GPUCache"), D("Network", F("Cookies"))),
                D("Profile 1", F("Preferences"), D("Cache")),
                D("Crashpad", D("Cache")),
                D("Guest Profile", D("Preferences"), D("Cache")),
                N("Locked", Dir, denied: true),
                D("Zzz", D("Cache"))))),
            D("BraveSoftware", N("Brave-Browser", Link)),
            D("Vivaldi", D("User Data", D("GrShaderCache", F("data_0", 1)), D("ShaderCache", D("Cache", F("v", 6)), D("Extensions"), F("Preferences")))),
            D("Mozilla", D("Firefox", D("Profiles", D("abc.default-release", D("cache2", F("entry", 5))), D("xyz.dev", D("thumbnails"))))),
            D("npm-cache", F("_cacache")),
            D("Steam", D("htmlcache", D("Cache", F("s", 4)), D("Network", F("Cookies")), F("Cookies"))));
        var roaming = D("Roaming",
            D("Code", D("Cache", F("x", 1)), D("code cache", F("y", 2), F("z", 3), F("w", 4)), D("User", F("settings.json"))),
            D("discord", N("Cache", Dir, denied: true)));
        return new(D("AppData", local, roaming));
    }
    private static Fake F(string name, long bytes = 0) => new(name, false, bytes, 0x20);
    private static Fake D(string name, params Fake[] children) => new(name, true, 0, 0x10, children);
    private static Fake N(string name, uint attributes, bool denied = false) => new(name, true, 0, attributes) { Denied = denied };

    private sealed class Fake : IFolderMetadata
    {
        private readonly bool directory; private readonly long bytes; private readonly uint attributes; private readonly Fake[] children;
        public string Name { get; }
        public bool Denied;
        public int Opened, Disposed;
        public Fake(string name, bool directory, long bytes, uint attributes, Fake[]? children = null)
        { Name = name; this.directory = directory; this.bytes = bytes; this.attributes = attributes; this.children = children ?? Array.Empty<Fake>(); }
        public Fake(Fake root) : this("C:", true, 0, 0x10, new[] { new Fake("Users", true, 0, 0x10, new[] { new Fake("me", true, 0, 0x10, new[] { root }) }) }) { }
        public IEnumerable<FolderEntry> Entries(CancellationToken token)
        {
            foreach (var child in children) { token.ThrowIfCancellationRequested(); yield return new(child.Name, child.directory, child.bytes, child.attributes); }
        }
        // Opens by full path the way the native reader does, refusing links and denied folders.
        public IFolderMetadata Open(string path)
        {
            var current = this;
            foreach (string part in path.Split('\\').Skip(1))
            {
                current = current.children.FirstOrDefault(c => string.Equals(c.Name, part, StringComparison.OrdinalIgnoreCase)) ?? throw new DirectoryNotFoundException(path);
                if ((current.attributes & 0x400) != 0) throw new UserError("Choose a local folder without links, system attributes or cloud placeholders.");
            }
            if (current.Denied) throw new UnauthorizedAccessException();
            if (!current.directory) throw new IOException("Not a directory.");
            Opened++; return new Lease(this, current);
        }
        public IFolderMetadata OpenChild(string childName)
        {
            var child = children.Single(c => c.Name == childName);
            if (child.Denied) throw new UnauthorizedAccessException();
            if ((child.attributes & 0x400) != 0 || !child.directory) throw new IOException("Not a plain directory.");
            return child;
        }
        public void Dispose() { }
        // Counts opens and releases, also of child folders, on the tree root.
        private sealed class Lease(Fake root, Fake folder) : IFolderMetadata
        {
            private bool disposed;
            public string Name => folder.Name;
            public IEnumerable<FolderEntry> Entries(CancellationToken token) => folder.Entries(token);
            public IFolderMetadata OpenChild(string childName) { var child = (Fake)folder.OpenChild(childName); root.Opened++; return new Lease(root, child); }
            public void Dispose() { if (!disposed) { disposed = true; root.Disposed++; } }
        }
    }
}
