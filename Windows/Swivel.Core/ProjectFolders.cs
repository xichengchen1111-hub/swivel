using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Swivel.Core;

public sealed record ProjectFolder(string RelativePath, string ProjectPath, string Kind,
    IReadOnlyList<string> Evidence, int EvidenceCount, decimal Bytes, int Files, bool Partial);
public sealed record ProjectFolderIssue(string RelativePath, string Reason);
public sealed record ProjectFolderResult(IReadOnlyList<ProjectFolder> Folders, IReadOnlyList<ProjectFolderIssue> Issues);

// Original filename-only rules over an already bounded metadata snapshot. No
// project file is parsed, executed or opened, and a match never authorizes cleanup.
public static class ProjectFolders
{
    private enum Marker { Node, Cargo, Swift, Gradle, CMake, CMakeCache, Python, PythonConfig, DotNet, Next, Godot, Dart, Maven, Nuxt, Svelte, Elixir, Haskell, Zig, Terraform, UnityVersion, UnityProject, Unreal, Pods }
    private enum Candidate { Node, Cargo, Swift, Build, Gradle, Python, DotNet, Next, Godot, Dart, Nuxt, Svelte, Elixir, Haskell, Zig, Terraform, Unity, Unreal, Pods }
    private static readonly Dictionary<string, Candidate> Candidates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["node_modules"] = Candidate.Node, ["target"] = Candidate.Cargo, [".build"] = Candidate.Swift,
        ["build"] = Candidate.Build, [".gradle"] = Candidate.Gradle, [".venv"] = Candidate.Python,
        ["venv"] = Candidate.Python, ["bin"] = Candidate.DotNet, ["obj"] = Candidate.DotNet,
        [".next"] = Candidate.Next, [".godot"] = Candidate.Godot, [".dart_tool"] = Candidate.Dart,
        [".nuxt"] = Candidate.Nuxt, [".output"] = Candidate.Nuxt, [".svelte-kit"] = Candidate.Svelte,
        ["_build"] = Candidate.Elixir, ["deps"] = Candidate.Elixir, [".stack-work"] = Candidate.Haskell,
        [".zig-cache"] = Candidate.Zig, ["zig-cache"] = Candidate.Zig, ["zig-out"] = Candidate.Zig,
        [".terraform"] = Candidate.Terraform, ["Library"] = Candidate.Unity, ["Temp"] = Candidate.Unity,
        ["Intermediate"] = Candidate.Unreal,
        ["DerivedDataCache"] = Candidate.Unreal, ["Binaries"] = Candidate.Unreal, ["Pods"] = Candidate.Pods
    };
    private static readonly Dictionary<string, Marker> MarkerNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["package.json"] = Marker.Node, ["Cargo.toml"] = Marker.Cargo, ["Package.swift"] = Marker.Swift,
        ["build.gradle"] = Marker.Gradle, ["build.gradle.kts"] = Marker.Gradle,
        ["settings.gradle"] = Marker.Gradle, ["settings.gradle.kts"] = Marker.Gradle,
        ["CMakeLists.txt"] = Marker.CMake, ["CMakeCache.txt"] = Marker.CMakeCache,
        ["pyproject.toml"] = Marker.Python, ["requirements.txt"] = Marker.Python, ["setup.py"] = Marker.Python,
        ["pyvenv.cfg"] = Marker.PythonConfig, ["next.config.js"] = Marker.Next,
        ["next.config.mjs"] = Marker.Next, ["next.config.ts"] = Marker.Next,
        ["project.godot"] = Marker.Godot, ["pubspec.yaml"] = Marker.Dart,
        ["pom.xml"] = Marker.Maven, ["mix.exs"] = Marker.Elixir, ["stack.yaml"] = Marker.Haskell,
        ["build.zig"] = Marker.Zig, ["ProjectVersion.txt"] = Marker.UnityVersion, ["Podfile"] = Marker.Pods
    };
    private static readonly string[] ScriptExtensions = { "js", "mjs", "cjs", "ts", "mts", "cts" };
    private static readonly string[] DotNetExtensions = { ".csproj", ".fsproj", ".vbproj" };
    private sealed class EvidenceSet
    {
        public int Count { get; private set; }
        public List<string> Paths { get; } = new();
        public void Merge(EvidenceSet other)
        {
            foreach (string path in other.Paths) Add(path);
            Count += other.Count - other.Paths.Count;
        }
        public void Add(string path)
        {
            Count++;
            int index = Paths.BinarySearch(path, StringComparer.Ordinal);
            if (index < 0) index = ~index;
            if (index < 8) { Paths.Insert(index, path); if (Paths.Count > 8) Paths.RemoveAt(8); }
        }
    }

    public static ProjectFolderResult Find(FolderScanResult scan, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(scan);
        token.ThrowIfCancellationRequested();
        var folders = new List<ProjectFolder>();
        var issues = new HashSet<ProjectFolderIssue>();
        var indices = new Dictionary<FolderSizeNode, Dictionary<Marker, EvidenceSet>>();
        string Join(string parent, string name) => parent == "." ? name : parent + "\\" + name;
        void Issue(string path, string reason) => issues.Add(new(path, reason));
        // One pass indexes direct sibling markers and records every underlying
        // skip, even inside a later matched outermost folder. Each marker retains
        // at most eight evidence paths; many project files cannot inflate rows.
        void Index(FolderSizeNode node, string path)
        {
            token.ThrowIfCancellationRequested();
            if (node.Issue is not null) Issue(path, node.Issue);
            if (!node.Directory || node.Aggregate) return;
            var markers = new Dictionary<Marker, EvidenceSet>();
            indices.Add(node, markers);
            int childSkipped = 0;
            foreach (var child in node.Children)
            {
                token.ThrowIfCancellationRequested();
                string childPath = Join(path, child.Name);
                childSkipped += child.Skipped;
                if (!child.Directory && child.Files == 1 && child.Issue is null && !child.Incomplete && !child.Aggregate)
                {
                    var kind = MarkerFor(child.Name);
                    if (kind is not null)
                    {
                        if (!markers.TryGetValue(kind.Value, out var evidence)) markers.Add(kind.Value, evidence = new());
                        evidence.Add(childPath);
                    }
                }
                Index(child, childPath);
            }
            // Unity needs two ordinary sibling directories and a regular file
            // under ProjectSettings. Index once, without any additional I/O.
            // Retain file evidence for the existing native move recheck contract;
            // Assets is a metadata-only directory condition, never a file path.
            bool assets = false;
            var versions = new EvidenceSet();
            foreach (var child in node.Children)
            {
                token.ThrowIfCancellationRequested();
                if (!child.Directory || child.Aggregate || child.Issue is not null ||
                    (child.Children.Count == 0 && child.Skipped > 0)) continue;
                if (child.Name.Equals("Assets", StringComparison.OrdinalIgnoreCase)) assets = true;
                if (child.Name.Equals("ProjectSettings", StringComparison.OrdinalIgnoreCase) &&
                    indices.TryGetValue(child, out var settings) &&
                    settings.TryGetValue(Marker.UnityVersion, out var version)) versions.Merge(version);
            }
            if (assets && versions.Count > 0) markers.Add(Marker.UnityProject, versions);
            // The scanner increments Skipped locally on enumeration failure,
            // while descendant skips are already reported at their own paths.
            if (node.Issue is null && node.Skipped > childSkipped) Issue(path, "Folder enumeration incomplete");
        }
        Index(scan.Root, ".");
        if (scan.EntryLimit) Issue(".", "Scan entry limit");
        if (scan.TimeLimit) Issue(".", "Scan time limit");

        void Visit(FolderSizeNode parent, string parentPath)
        {
            token.ThrowIfCancellationRequested();
            if (!indices.TryGetValue(parent, out var siblings)) return;
            foreach (var node in parent.Children)
            {
                token.ThrowIfCancellationRequested();
                if (!node.Directory || node.Aggregate) continue;
                string path = Join(parentPath, node.Name);
                if (node.Issue is not null) continue;
                // An opened directory whose enumeration yielded no entries and
                // failed is unavailable, not a known empty build folder.
                if (node.Children.Count == 0 && node.Skipped > 0) continue;
                bool matched = false;
                if (Candidates.TryGetValue(node.Name, out var candidate))
                {
                    var inside = indices[node];
                    var matches = new List<(string Kind, EvidenceSet[] Evidence)>();
                    bool Has(Marker marker) => siblings.ContainsKey(marker);
                    void Match(string kind, Marker marker) { if (Has(marker)) matches.Add((kind, new[] { siblings[marker] })); }
                    void MatchInside(string kind, Marker marker, Marker contained)
                    {
                        if (Has(marker) && inside.TryGetValue(contained, out var evidence)) matches.Add((kind, new[] { siblings[marker], evidence }));
                    }
                    switch (candidate)
                    {
                        case Candidate.Node: Match("Node.js dependencies", Marker.Node); break;
                        case Candidate.Cargo:
                            Match("Rust build output", Marker.Cargo);
                            Match("Maven build output", Marker.Maven);
                            break;
                        case Candidate.Swift: Match("Swift build output", Marker.Swift); break;
                        case Candidate.Build:
                            Match("Gradle build output", Marker.Gradle);
                            MatchInside("CMake build output", Marker.CMake, Marker.CMakeCache);
                            break;
                        case Candidate.Gradle: Match("Gradle project cache", Marker.Gradle); break;
                        case Candidate.Python: MatchInside("Python virtual environment", Marker.Python, Marker.PythonConfig); break;
                        case Candidate.DotNet:
                            Match(".NET build output", Marker.DotNet);
                            if (node.Name.Equals("obj", StringComparison.OrdinalIgnoreCase)) Match("Unity project output", Marker.UnityProject);
                            break;
                        case Candidate.Next: Match("Next.js build output", Marker.Next); break;
                        case Candidate.Godot: Match("Godot project cache", Marker.Godot); break;
                        case Candidate.Dart: Match("Dart tool cache", Marker.Dart); break;
                        case Candidate.Nuxt: Match("Nuxt build output", Marker.Nuxt); break;
                        case Candidate.Svelte: Match("SvelteKit build output", Marker.Svelte); break;
                        case Candidate.Elixir: Match("Elixir build and dependencies", Marker.Elixir); break;
                        case Candidate.Haskell: Match("Haskell Stack build output", Marker.Haskell); break;
                        case Candidate.Zig: Match("Zig build output", Marker.Zig); break;
                        case Candidate.Terraform: Match("Terraform working folder", Marker.Terraform); break;
                        case Candidate.Unity: Match("Unity project output", Marker.UnityProject); break;
                        case Candidate.Unreal: Match("Unreal Engine project output", Marker.Unreal); break;
                        case Candidate.Pods: Match("CocoaPods dependencies", Marker.Pods); break;
                    }
                    if (matches.Count == 1)
                    {
                        var match = matches[0];
                        // Preserve at least one actual path from every required
                        // evidence set (adjacent and internal) before filling the
                        // eight-path cap. Case-sensitive Windows directories may
                        // contain many case variants of one marker filename.
                        var evidence = match.Evidence.Select(x => x.Paths[0])
                            .Concat(match.Evidence.SelectMany(x => x.Paths).OrderBy(x => x, StringComparer.Ordinal))
                            .Distinct(StringComparer.Ordinal).Take(8).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                        folders.Add(new(path, parentPath, match.Kind, Array.AsReadOnly(evidence), match.Evidence.Sum(x => x.Count), node.Bytes, node.Files, node.Incomplete || parent.Incomplete));
                        matched = true;
                    }
                    else Issue(path, matches.Count > 1 ? "Ambiguous project markers" : "Missing or unavailable project markers");
                }
                // Outermost candidates already include descendant sizes. Their
                // metadata skips remain visible from Index, without double totals.
                if (!matched) Visit(node, path);
            }
        }
        Visit(scan.Root, ".");
        token.ThrowIfCancellationRequested();
        return new(Array.AsReadOnly(folders.OrderBy(x => x.RelativePath, StringComparer.Ordinal).ToArray()),
            Array.AsReadOnly(issues.OrderBy(x => x.RelativePath, StringComparer.Ordinal).ThenBy(x => x.Reason, StringComparer.Ordinal).ToArray()));
    }

    private static Marker? MarkerFor(string name)
    {
        if (MarkerNames.TryGetValue(name, out var marker)) return marker;
        foreach (var (prefix, kind) in new[] { ("nuxt.config.", Marker.Nuxt), ("svelte.config.", Marker.Svelte) })
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                ScriptExtensions.Contains(name[prefix.Length..], StringComparer.OrdinalIgnoreCase)) return kind;
        if (name.Length > 3 && name.EndsWith(".tf", StringComparison.OrdinalIgnoreCase)) return Marker.Terraform;
        if (name.Length > 9 && name.EndsWith(".uproject", StringComparison.OrdinalIgnoreCase)) return Marker.Unreal;
        foreach (string extension in DotNetExtensions)
            if (name.Length > extension.Length && name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return Marker.DotNet;
        return null;
    }
}
