using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

/// Clean Up Files' search: the existing Doctor readers one after another, each part bounded by
/// its own limits. A part that fails is reported and the others still run.
internal static class CleanupReader
{
    internal sealed record Result(IReadOnlyList<CleanupEntry> Entries, IReadOnlyList<CleanupPart> Parts);

    /// `progress` gets each part's name as it starts. Runs on an STA thread (the Downloads
    /// folder is read through the shell).
    internal static Result Find(Action<CleanupGroup> progress, CancellationToken token)
    {
        var entries = new List<CleanupEntry>(); var parts = new List<CleanupPart>();
        void Part(CleanupGroup group, Func<(IReadOnlyList<CleanupEntry> Found, bool Partial, string? Issue)> read)
        {
            token.ThrowIfCancellationRequested(); progress(group);
            (IReadOnlyList<CleanupEntry> Found, bool Partial, string? Issue) result;
            try { result = read(); }
            catch (UserError error) { result = (Array.Empty<CleanupEntry>(), true, error.Message); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or System.ComponentModel.Win32Exception or ArgumentException or NotSupportedException)
            { token.ThrowIfCancellationRequested(); result = (Array.Empty<CleanupEntry>(), true, "This place could not be read."); }
            token.ThrowIfCancellationRequested();
            entries.AddRange(result.Found);
            var total = CleanupFiles.Total(result.Found);
            parts.Add(new(group, total.Count, total.Bytes, result.Partial, result.Issue));
        }
        static bool Partial(FolderScanResult scan) => scan.Root.Incomplete || scan.EntryLimit || scan.TimeLimit;

        Part(CleanupGroup.AppCaches, () =>
        {
            var caches = AppCacheReader.Read(token);
            return (CleanupFiles.Caches(caches, AppCacheReader.RunningNames()), caches.FolderLimit || caches.DiscoveryLimit || caches.Apps.Any(a => a.Partial || a.Unreadable), null);
        });
        Part(CleanupGroup.OldTemporary, () =>
        {
            var temp = CacheLocationReader.Read(token).Single(l => l.Kind == CacheLocationKind.Temporary);
            if (temp.Path is not { } path) return (Array.Empty<CleanupEntry>(), true, temp.Issue ?? CacheLocations.Unreported);
            var scan = CacheLocationReader.Scan(temp, token);
            var plan = TempCleanup.Plan(scan, DateTime.UtcNow);
            return (CleanupFiles.Temporary(path, plan), Partial(scan) || plan.NotListed > 0, null);
        });
        var common = CommonFolderReader.Read(token);
        string? Folder(CommonFolderKind kind) => common.FirstOrDefault(c => c.Kind == kind)?.Path;
        Part(CleanupGroup.Installers, () =>
        {
            var choice = common.FirstOrDefault(c => c.Kind == CommonFolderKind.Downloads);
            if (choice?.Path is not { } downloads) return (Array.Empty<CleanupEntry>(), true, choice?.Issue ?? "Windows could not report this folder location.");
            var (scan, files) = DoctorLargeFiles.Run(downloads, token);
            return (CleanupFiles.Installers(downloads, files, token), Partial(scan), null);
        });
        Part(CleanupGroup.ProjectBuilds, () =>
        {
            var found = new List<CleanupEntry>(); bool partial = false, missing = false;
            string? source = null;
            try { source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify), "source"); }
            catch (Exception error) when (error is IOException or SecurityException or ArgumentException or NotSupportedException) { }
            var roots = new[] { Folder(CommonFolderKind.Documents), Folder(CommonFolderKind.Desktop), source };
            for (int i = 0; i < roots.Length; i++)
            {
                if (roots[i] is not { } root) { if (i < 2) missing = true; continue; }
                (FolderScanResult Scan, ProjectFolderResult Projects) result;
                try { result = DoctorProjects.Run(root, token); }
                // The source folder only exists for some developers; the others are reported.
                catch (Exception error) when (error is UserError or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or SecurityException or ArgumentException or NotSupportedException)
                { token.ThrowIfCancellationRequested(); if (i < 2) missing = true; continue; }
                partial |= Partial(result.Scan);
                found.AddRange(CleanupFiles.Builds(root, result.Projects));
            }
            return (found, partial || missing, missing ? "Documents or Desktop could not be read; build folders there are not listed." : null);
        });
        return new(CleanupFiles.Combine(entries), parts);
    }
}
