using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Swivel.Core;

internal static class ProjectFolderChecks
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        ProjectFolderResult Find(Fixture root, FolderScanLimits? limits = null) => ProjectFolders.Find(FolderSizes.Scan(root, default, limits), default);
        var rules = new (string Folder, string Marker, string? Inside, string Kind)[]
        {
            ("node_modules", "package.json", null, "Node.js dependencies"),
            ("target", "Cargo.toml", null, "Rust build output"),
            (".build", "Package.swift", null, "Swift build output"),
            ("build", "build.gradle", null, "Gradle build output"),
            ("build", "build.gradle.kts", null, "Gradle build output"),
            ("build", "settings.gradle", null, "Gradle build output"),
            (".gradle", "settings.gradle.kts", null, "Gradle project cache"),
            ("build", "CMakeLists.txt", "CMakeCache.txt", "CMake build output"),
            (".venv", "pyproject.toml", "pyvenv.cfg", "Python virtual environment"),
            ("venv", "requirements.txt", "pyvenv.cfg", "Python virtual environment"),
            ("venv", "setup.py", "pyvenv.cfg", "Python virtual environment"),
            ("bin", "app.csproj", null, ".NET build output"),
            ("obj", "app.fsproj", null, ".NET build output"),
            ("bin", "app.vbproj", null, ".NET build output"),
            (".next", "next.config.js", null, "Next.js build output"),
            (".next", "next.config.mjs", null, "Next.js build output"),
            (".next", "next.config.ts", null, "Next.js build output"),
            (".godot", "project.godot", null, "Godot project cache"),
            (".dart_tool", "pubspec.yaml", null, "Dart tool cache")
        };
        foreach (var rule in rules)
        {
            var content = new List<Fixture> { F("output", 77) }; if (rule.Inside is not null) content.Add(F(rule.Inside, 5));
            var root = D("root", F(rule.Marker, 1), D(rule.Folder, content.ToArray()));
            var result = Find(root); var row = result.Folders.Single();
            check(row.RelativePath == rule.Folder && row.ProjectPath == "." && row.Kind == rule.Kind && row.Bytes == (rule.Inside is null ? 77 : 82) && row.Files == content.Count && !row.Partial && result.Issues.Count == 0, "Recognize documented project rule using only adjacent ordinary marker names: " + rule.Folder + "/" + rule.Marker);
            check(row.Evidence.Contains(rule.Marker) && (rule.Inside is null || row.Evidence.Contains(rule.Folder + "\\" + rule.Inside)) && row.Evidence.Count == row.EvidenceCount, "Evidence reports actual sibling and internal relative marker paths");
            check(Strings.Table.ContainsKey(rule.Kind), "Project folder kind translated: " + rule.Kind);
            result = Find(D("root", F(rule.Marker.ToUpperInvariant()), D(rule.Folder.ToUpperInvariant(), content.Select(x => F(x.Name.ToUpperInvariant(), x.Bytes)).ToArray())));
            check(result.Folders.Count == 1 && result.Folders[0].Kind == rule.Kind && result.Folders[0].RelativePath == rule.Folder.ToUpperInvariant(), "Ordinal case-insensitive names recognize Windows spellings while preserving display names");
            result = Find(D("root", D(rule.Folder, content.ToArray())));
            check(result.Folders.Count == 0 && result.Issues.Any(x => x.RelativePath == rule.Folder && x.Reason == "Missing or unavailable project markers"), "An artifact-like name alone is never sufficient: " + rule.Folder);
            result = Find(D("root", D(rule.Marker), D(rule.Folder, content.ToArray())));
            check(result.Folders.Count == 0, "Directories with marker filenames are not ordinary marker files");
            foreach (uint flag in new uint[] { 0x4, 0x40, 0x400, 0x1000, 0x40000, 0x400000 })
            {
                result = Find(D("root", F(rule.Marker, 1, flag), D(rule.Folder, content.ToArray())));
                check(result.Folders.Count == 0 && result.Issues.Any(x => x.RelativePath == rule.Marker && x.Reason == "System, link, cloud or invalid item"), "System/device/link/offline/recall sibling markers are rejected and reported");
            }
            if (rule.Inside is not null)
            {
                result = Find(D("root", F(rule.Marker), D(rule.Folder, F("output", 77))));
                check(result.Folders.Count == 0 && result.Issues.Any(x => x.RelativePath == rule.Folder && x.Reason == "Missing or unavailable project markers"), "CMake and Python need the internal identifying marker too");
                foreach (uint flag in new uint[] { 0x4, 0x400, 0x1000, 0x40000, 0x400000 })
                {
                    result = Find(D("root", F(rule.Marker), D(rule.Folder, F(rule.Inside, 0, flag))));
                    check(result.Folders.Count == 0 && result.Issues.Any(x => x.RelativePath == rule.Folder + "\\" + rule.Inside), "Unsafe internal marker remains visible but cannot support classification");
                }
                result = Find(D("root", F(rule.Marker), D(rule.Folder, D(rule.Inside, F("data")))));
                check(result.Folders.Count == 0, "Internal identifying marker must itself be a regular file");
            }
        }
        foreach (string marker in new[] { ".csproj", ".fsproj", ".vbproj", "app.csproj.bak", "app.csprojx", "csproj" })
        {
            var result = Find(D("root", F(marker), D("bin", F("payload"))));
            check(result.Folders.Count == 0 && result.Issues.Count == 1, ".NET marker requires a nonempty stem and exact project-file suffix");
        }
        foreach (string folder in new[] { "build-old", "node_module", "node_modules.bak", "bın", "object" })
            check(Find(D("root", F("package.json"), F("app.csproj"), F("build.gradle"), D(folder))).Folders.Count == 0, "No fuzzy, prefix or culture-sensitive candidate-name matching");
        var ambiguous = Find(D("root", F("build.gradle"), F("CMakeLists.txt"), D("build", F("CMakeCache.txt"), F("payload", 30))));
        check(ambiguous.Folders.Count == 0 && ambiguous.Issues.Single().Reason == "Ambiguous project markers", "A build folder with two complete rule matches is visibly ambiguous, never first-rule-wins");
        var gradleOnly = Find(D("root", F("build.gradle"), F("CMakeLists.txt"), D("build", F("payload"))));
        check(gradleOnly.Folders.Single().Kind == "Gradle build output", "Incomplete CMake evidence is not an additional complete match");
        var mixed = Find(D("root", F("app.csproj"), F("package.json"), D("obj", F("cache", 20)), D("node_modules", F("dependency", 30)), D("source", F("user", 100))));
        check(mixed.Folders.Count == 2 && mixed.Folders.Sum(x => x.Bytes) == 50 && mixed.Folders.Select(x => x.RelativePath).SequenceEqual(new[] { "node_modules", "obj" }), "Independent siblings are sorted deterministically and omit ordinary source folders");
        check(Find(D("node_modules", F("dependency", 100), F("package.json"))).Folders.Count == 0, "Chosen root itself is never inferred from its name or imaginary outside siblings");
        var distant = Find(D("root", F("package.json"), D("project", D("node_modules", F("output")))));
        check(distant.Folders.Count == 0 && distant.Issues.Any(x => x.RelativePath == "project\\node_modules"), "Markers must be adjacent, not inherited from ancestors");
        var nested = Find(D("root", D("project", F("app.csproj"), D("obj", F("cache", 12)))));
        check(nested.Folders.Single().ProjectPath == "project" && nested.Folders[0].RelativePath == "project\\obj" && nested.Folders[0].Evidence.Single() == "project\\app.csproj", "Nested project and marker paths remain relative to the explicitly chosen root");
        var overlap = Find(D("root", F("package.json"), D("node_modules", F("root.dat", 10), D("nested", F("package.json", 2), D("node_modules", F("child.dat", 13))), F("unavailable", 500, 0x400))));
        check(overlap.Folders.Count == 1 && overlap.Folders[0].RelativePath == "node_modules" && overlap.Folders[0].Bytes == 25 && overlap.Folders[0].Partial, "Outermost recognized folder includes descendants once and is partial when a descendant is skipped");
        check(overlap.Issues.Any(x => x.RelativePath == "node_modules\\unavailable" && x.Reason == "System, link, cloud or invalid item"), "Outermost suppression never hides metadata skip reasons inside it");
        var parentPartial = Find(D("root", F("package.json"), F("cloud", 123, 0x40000), D("node_modules", F("dependency", 8))));
        check(parentPartial.Folders.Single().Partial && parentPartial.Issues.Any(x => x.RelativePath == "cloud"), "Incomplete parent snapshot marks an otherwise complete matching child partial");
        var denied = D("node_modules"); denied.Denied = true;
        var inaccessible = Find(D("root", F("package.json"), denied));
        check(inaccessible.Folders.Count == 0 && inaccessible.Issues.Single().Reason == "Unavailable or unsupported", "Unavailable candidate is a visible skip rather than a recognized zero-byte folder");
        var failed = D("node_modules", F("unread")); failed.FailAt = 0;
        var failedEmpty = Find(D("root", F("package.json"), failed));
        check(failedEmpty.Folders.Count == 0 && failedEmpty.Issues.Single().RelativePath == "node_modules" && failedEmpty.Issues[0].Reason == "Folder enumeration incomplete", "An empty prefix from failed enumeration is not a known empty candidate");
        var partialFolder = D("node_modules", F("prefix", 11), F("unread", 40)); partialFolder.FailAt = 1;
        var failedPrefix = Find(D("root", F("package.json"), partialFolder));
        check(failedPrefix.Folders.Single().Bytes == 11 && failedPrefix.Folders[0].Partial && failedPrefix.Issues.Any(x => x.Reason == "Folder enumeration incomplete"), "A useful enumerated prefix remains a partial candidate with the local failure visible");
        var parentFailed = D("root", F("package.json"), D("node_modules", F("out", 12)), F("unread")); parentFailed.FailAt = 2;
        var failedParent = Find(parentFailed);
        check(failedParent.Folders.Single().Partial && failedParent.Issues.Single().RelativePath == ".", "Parent enumeration failure is recorded at the root with partial candidate status");
        var depth = Find(D("root", F("package.json"), D("node_modules", F("payload"))), new(MaxDepth: 0));
        check(depth.Folders.Count == 0 && depth.Issues.Single().Reason == "Depth limit", "Unvisited depth-limited candidate is visible but not assumed empty");
        var limited = Find(D("root", D("node_modules"), F("package.json")), new(MaxEntries: 1));
        check(limited.Folders.Count == 0 && limited.Issues.Any(x => x.Reason == "Scan entry limit") && limited.Issues.Any(x => x.Reason == "Missing or unavailable project markers"), "Budget truncation cannot invent an unseen project marker");
        var limitedKnown = Find(D("root", F("package.json"), D("node_modules", F("first", 9), F("later", 80))), new(MaxEntries: 3));
        check(limitedKnown.Folders.Single().Bytes == 9 && limitedKnown.Folders[0].Partial && limitedKnown.Issues.Any(x => x.Reason == "Scan entry limit"), "Known evidence before a global limit produces explicitly partial counted bytes");
        var timed = ProjectFolders.Find(FolderSizes.Scan(D("root", F("package.json"), D("node_modules")), default, elapsed: () => TimeSpan.FromSeconds(30)), default);
        check(timed.Folders.Count == 0 && timed.Issues.Single().Reason == "Scan time limit", "Time-limited empty snapshot records the reason instead of claiming no projects");
        var manyMarkers = Enumerable.Range(0, 19998).Reverse().Select(i => F($"project{i:D5}.csproj")).Append(D("obj", F("payload", 55))).ToArray();
        var manyRoot = D("root", manyMarkers); var manyScan = FolderSizes.Scan(manyRoot, default); var many = ProjectFolders.Find(manyScan, default);
        check(manyScan.Entries == 20000 && !manyScan.EntryLimit && many.Folders.Single().EvidenceCount == 19998 && many.Folders[0].Evidence.Count == 8 && many.Folders[0].Evidence.SequenceEqual(Enumerable.Range(0, 8).Select(i => $"project{i:D5}.csproj")), "At 20K entries, all valid markers count but only deterministic first eight evidence paths are retained");
        // Use independently upper/lower spellings for four letters so all 16
        // paths are distinct in a case-sensitive directory's metadata snapshot.
        var caseMarkers = Enumerable.Range(0, 16).Select(i => F(new string("CMAKELists.txt".Select((c, at) => at < 4 && ((i >> at) & 1) != 0 ? char.ToLowerInvariant(c) : c).ToArray()))).ToArray();
        var boundedBoth = Find(D("root", caseMarkers.Append(D("build", F("CMakeCache.txt"))).ToArray())).Folders.Single();
        check(boundedBoth.EvidenceCount == 17 && boundedBoth.Evidence.Count == 8 && boundedBoth.Evidence.Contains("build\\CMakeCache.txt") && boundedBoth.Evidence.Any(x => x.Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase)), "Bounded evidence keeps both required roles even with many case-variant adjacent markers");
        var manyCandidates = D("root", Enumerable.Range(0, 5000).Select(i => D("p" + i, F("app.csproj"), D("obj", F("payload", i)))).ToArray());
        var broad = Find(manyCandidates);
        check(broad.Folders.Count == 5000 && broad.Folders.Sum(x => x.Bytes) == 4999m * 5000 / 2 && broad.Folders.All(x => x.EvidenceCount == 1), "Broad 20K-entry project shape keeps each candidate and its bounded direct evidence");
        reject(() => ProjectFolders.Find(manyScan, new CancellationToken(true)), "Cancellation before classification propagates");
        reject(() => ProjectFolders.Find(null!, default), "Missing metadata snapshot refused");
        var prefs = new Prefs();
        check(!Catalog.Available(prefs, Array.Empty<string>()).Any(x => x.Id == "doctor.projects"), "Project folder scan is opt-in off by default");
        prefs.Set(Feature.Doctor, true);
        check(Catalog.Available(prefs, Array.Empty<string>()).Any(x => x.Id == "doctor.projects"), "Project folder scan can choose its explicit root without supplied file input");
        foreach (string reason in new[] { "Missing or unavailable project markers", "Ambiguous project markers", "Folder enumeration incomplete", "Scan entry limit", "Scan time limit" })
            check(Strings.Table.ContainsKey(reason), "Project folder reason translated: " + reason);
    }
    private static Fixture F(string name, long bytes = 0, uint attributes = 0) => new(name, false, bytes, attributes);
    private static Fixture D(string name, params Fixture[] children) => new(name, true, 0, 0x10, children);
    private sealed class Fixture(string name, bool directory, long bytes, uint attributes, Fixture[]? children = null) : IFolderMetadata
    {
        public string Name => name;
        public long Bytes => bytes;
        public bool Denied;
        public int FailAt = -1;
        private Dictionary<string, Fixture>? childIndex;
        public IEnumerable<FolderEntry> Entries(CancellationToken token)
        {
            int count = 0;
            foreach (var child in children ?? Array.Empty<Fixture>())
            {
                token.ThrowIfCancellationRequested();
                if (count++ == FailAt) throw new IOException("Injected incomplete enumeration.");
                yield return new(child.Name, child.Directory, child.Bytes, child.Attributes);
            }
        }
        private bool Directory => directory;
        private uint Attributes => attributes;
        public IFolderMetadata OpenChild(string childName)
        {
            childIndex ??= children!.ToDictionary(x => x.Name, StringComparer.Ordinal);
            var child = childIndex[childName];
            return child.Denied ? throw new UnauthorizedAccessException() : child;
        }
        public void Dispose() { }
    }
}
