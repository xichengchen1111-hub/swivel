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
    private enum Marker { Node, Cargo, Swift, Gradle, CMake, CMakeCache, Python, PythonConfig, DotNet, Next, Godot, Dart }
    private enum Candidate { Node, Cargo, Swift, Build, Gradle, Python, DotNet, Next, Godot, Dart }
    private static readonly Dictionary<string, Candidate> Candidates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["node_modules"] = Candidate.Node, ["target"] = Candidate.Cargo, [".build"] = Candidate.Swift,
        ["build"] = Candidate.Build, [".gradle"] = Candidate.Gradle, [".venv"] = Candidate.Python,
        ["venv"] = Candidate.Python, ["bin"] = Candidate.DotNet, ["obj"] = Candidate.DotNet,
        [".next"] = Candidate.Next, [".godot"] = Candidate.Godot, [".dart_tool"] = Candidate.Dart
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
        ["project.godot"] = Marker.Godot, ["pubspec.yaml"] = Marker.Dart
    };
    private static readonly string[] DotNetExtensions = { ".csproj", ".fsproj", ".vbproj" };
    private sealed class EvidenceSet
    {
        public int Count { get; private set; }
        public List<string> Paths { get; } = new();
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
                        case Candidate.Cargo: Match("Rust build output", Marker.Cargo); break;
                        case Candidate.Swift: Match("Swift build output", Marker.Swift); break;
                        case Candidate.Build:
                            Match("Gradle build output", Marker.Gradle);
                            MatchInside("CMake build output", Marker.CMake, Marker.CMakeCache);
                            break;
                        case Candidate.Gradle: Match("Gradle project cache", Marker.Gradle); break;
                        case Candidate.Python: MatchInside("Python virtual environment", Marker.Python, Marker.PythonConfig); break;
                        case Candidate.DotNet: Match(".NET build output", Marker.DotNet); break;
                        case Candidate.Next: Match("Next.js build output", Marker.Next); break;
                        case Candidate.Godot: Match("Godot project cache", Marker.Godot); break;
                        case Candidate.Dart: Match("Dart tool cache", Marker.Dart); break;
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
        foreach (string extension in DotNetExtensions)
            if (name.Length > extension.Length && name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return Marker.DotNet;
        return null;
    }
}
