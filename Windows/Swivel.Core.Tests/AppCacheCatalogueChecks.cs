using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Swivel.Core;

internal static class AppCacheCatalogueChecks
{
    internal static void Run(Action<bool, string> check)
    {
        const string Local = @"C:\Users\me\AppData\Local", Roaming = @"C:\Users\me\AppData\Roaming";
        string[] root = { "GrShaderCache", "ShaderCache", "GraphiteDawnCache" };
        string[] profile = { "Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache" };
        string[] denied = { "Login Data", "Cookies", "History", "Local Storage", "IndexedDB", "Extensions", "Service Worker", "Session Storage", "Sessions", "Sync Data", "Network", "Preferences", "User", "Data", "Storage" };
        var additions = new (string Id, string Name, AppCacheBase Base, string Path, string Process, bool Chromium, bool RootProfile)[]
        {
            ("edge-beta", "Microsoft Edge Beta", AppCacheBase.Local, @"Microsoft\Edge Beta\User Data", "msedge", true, false),
            ("edge-dev", "Microsoft Edge Dev", AppCacheBase.Local, @"Microsoft\Edge Dev\User Data", "msedge", true, false),
            ("edge-canary", "Microsoft Edge Canary", AppCacheBase.Local, @"Microsoft\Edge SxS\User Data", "msedge", true, false),
            ("chrome-beta", "Google Chrome Beta", AppCacheBase.Local, @"Google\Chrome Beta\User Data", "chrome", true, false),
            ("chrome-dev", "Google Chrome Dev", AppCacheBase.Local, @"Google\Chrome Dev\User Data", "chrome", true, false),
            ("chrome-canary", "Google Chrome Canary", AppCacheBase.Local, @"Google\Chrome SxS\User Data", "chrome", true, false),
            ("chromium", "Chromium", AppCacheBase.Local, @"Chromium\User Data", "chrome", true, false),
            ("vscode-insiders", "Visual Studio Code Insiders", AppCacheBase.Roaming, "Code - Insiders", "Code - Insiders", false, false),
            ("vscodium", "VSCodium", AppCacheBase.Roaming, "VSCodium", "VSCodium", false, false),
            ("opera", "Opera", AppCacheBase.Roaming, @"Opera Software\Opera Stable", "opera", true, true),
            ("opera-local", "Opera local cache", AppCacheBase.Local, @"Opera Software\Opera Stable", "opera", false, true),
            ("opera-default", "Opera Default cache", AppCacheBase.Local, @"Opera Software\Opera Stable\Default", "opera", false, true),
            ("opera-gx", "Opera GX", AppCacheBase.Roaming, @"Opera Software\Opera GX Stable", "opera", true, true),
            ("opera-gx-local", "Opera GX local cache", AppCacheBase.Local, @"Opera Software\Opera GX Stable", "opera", false, true),
            ("opera-gx-default", "Opera GX Default cache", AppCacheBase.Local, @"Opera Software\Opera GX Stable\Default", "opera", false, true),
        };
        foreach (var expected in additions)
        {
            var app = AppCaches.Apps.SingleOrDefault(a => a.Id == expected.Id);
            check(app is not null, "New cache app exists: " + expected.Id);
            if (app is null) continue;
            string[] folders = expected.RootProfile ? root.Concat(profile).ToArray() : expected.Chromium ? root : profile.Append("CachedData").ToArray();
            check(app.Name == expected.Name && app.Base == expected.Base && app.Container == expected.Path
                && app.Profiles == (expected.Chromium ? AppCacheProfiles.Chromium : AppCacheProfiles.None)
                && app.Folders.SequenceEqual(folders) && app.ProfileFolders.SequenceEqual(expected.Chromium ? profile : Array.Empty<string>()),
                "Exact base, relative path and cache names: " + expected.Id);
            check(app.Processes.SequenceEqual(new[] { expected.Process })
                && AppCaches.IsRunning(app, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { expected.Process.ToUpperInvariant() })
                && AppCaches.IsRunning(app, null) && !AppCaches.IsRunning(app, new HashSet<string> { expected.Process + "-unrelated" }),
                "Exact owner processes block cleanup, unknown processes fail closed: " + expected.Id);
            check(Strings.Table.TryGetValue(app.Name, out var labels) && labels.Length == 3 && labels.All(l => !string.IsNullOrWhiteSpace(l)), "New app label translated: " + app.Name);

            // Independent tree per location: no shared scan/folder budget can hide a catalogue mistake.
            var children = folders.Select(f => AppCacheChecks.D(f)).Concat(denied.Select(f => AppCacheChecks.F(f))).ToList();
            if (expected.Chromium)
            {
                children.Add(AppCacheChecks.D("Profile 7", new[] { AppCacheChecks.F("Preferences") }.Concat(profile.Select(f => AppCacheChecks.D(f)))
                    .Concat(denied.Where(f => f != "Preferences").Select(f => AppCacheChecks.D(f))).ToArray()));
                children.Add(AppCacheChecks.D("Unproven profile", AppCacheChecks.D("Cache")));
            }
            string[] parts = expected.Path.Split('\\');
            var node = AppCacheChecks.D(parts[^1], children.ToArray());
            foreach (string part in parts.Reverse().Skip(1)) node = AppCacheChecks.D(part, node);
            var tree = new AppCacheChecks.Fake(AppCacheChecks.D("AppData", AppCacheChecks.D(expected.Base == AppCacheBase.Local ? "Local" : "Roaming", node)));
            string full = (expected.Base == AppCacheBase.Local ? Local : Roaming) + "\\" + expected.Path;
            // Other catalogue locations are absent for this fixture, even when their paths overlap.
            IFolderMetadata Open(string path) => path == full || path.StartsWith(full + "\\", StringComparison.Ordinal) ? tree.Open(path) : throw new DirectoryNotFoundException();
            var result = AppCaches.Read(b => b == AppCacheBase.Local ? Local : Roaming, Open, CancellationToken.None);
            var paths = result.Items.Where(i => i.Folder.App.Id == expected.Id).Select(i => i.Folder.Path).ToArray();
            var wanted = folders.Select(f => full + "\\" + f).Concat(expected.Chromium ? profile.Select(f => full + @"\Profile 7\" + f) : Array.Empty<string>());
            check(paths.SequenceEqual(wanted), "Only exact rebuildable folders resolve at the expected base: " + expected.Id);
            check(tree.Opened == tree.Disposed, "All catalogue fixture leases released: " + expected.Id);
        }
        check(AppCaches.Apps.All(a => !a.Folders.Concat(a.ProfileFolders).Any(f => denied.Contains(f, StringComparer.OrdinalIgnoreCase)))
            && AppCaches.Apps.All(a => !a.Container.Split('\\').Contains("Packages", StringComparer.OrdinalIgnoreCase)), "No credentials, history, site/session/sync data or Store package cache rules");

        string Signature(AppCacheApp a) => string.Join("|", a.Id, a.Name, a.Base, a.Container, a.Profiles, string.Join(",", a.Folders), string.Join(",", a.ProfileFolders), string.Join(",", a.Processes));
        string[] original =
        {
            @"chrome|Google Chrome|Local|Google\Chrome\User Data|Chromium|GrShaderCache,ShaderCache,GraphiteDawnCache|Cache,Code Cache,GPUCache,DawnCache,DawnGraphiteCache,DawnWebGPUCache|chrome",
            @"edge|Microsoft Edge|Local|Microsoft\Edge\User Data|Chromium|GrShaderCache,ShaderCache,GraphiteDawnCache|Cache,Code Cache,GPUCache,DawnCache,DawnGraphiteCache,DawnWebGPUCache|msedge",
            @"brave|Brave|Local|BraveSoftware\Brave-Browser\User Data|Chromium|GrShaderCache,ShaderCache,GraphiteDawnCache|Cache,Code Cache,GPUCache,DawnCache,DawnGraphiteCache,DawnWebGPUCache|brave",
            @"vivaldi|Vivaldi|Local|Vivaldi\User Data|Chromium|GrShaderCache,ShaderCache,GraphiteDawnCache|Cache,Code Cache,GPUCache,DawnCache,DawnGraphiteCache,DawnWebGPUCache|vivaldi",
            @"firefox|Firefox|Local|Mozilla\Firefox\Profiles|Firefox||cache2,startupCache|firefox",
            @"vscode|Visual Studio Code|Roaming|Code|None|Cache,Code Cache,GPUCache,DawnCache,DawnGraphiteCache,DawnWebGPUCache,CachedData||Code",
            @"discord|Discord|Roaming|discord|None|Cache,Code Cache,GPUCache,DawnCache,DawnGraphiteCache,DawnWebGPUCache||Discord",
            @"slack|Slack|Roaming|Slack|None|Cache,Code Cache,GPUCache,DawnCache,DawnGraphiteCache,DawnWebGPUCache||slack",
            @"steam|Steam|Local|Steam\htmlcache|None|Cache,Code Cache,GPUCache,DawnCache,DawnGraphiteCache,DawnWebGPUCache||steam,steamwebhelper",
            @"npm|npm|Local|npm-cache|None|_cacache||node",
            @"pip|pip|Local|pip|None|Cache||python,pythonw,pip",
            @"yarn|Yarn|Local|Yarn|None|Cache||node",
            @"nuget|NuGet|Local|NuGet|None|v3-cache,plugins-cache||dotnet,nuget,MSBuild,devenv",
        };
        check(AppCaches.Apps.Take(original.Length).Select(Signature).SequenceEqual(original), "All thirteen original cache rules and their order are unchanged");
    }
}
