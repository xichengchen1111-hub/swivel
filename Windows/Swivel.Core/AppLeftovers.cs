using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace Swivel.Core;

public enum LeftoverBase { Local, Roaming, LocalLow, Programs }

/// What opening an earlier installation record's own registry subkey again found: gone, still
/// there (renamed, or skipped while the list was read), or not known (its view or the key could
/// not be opened for another reason).
public enum RecordKeyState { Gone, Present, Unknown }
public sealed record RecordKey(RegistrationSource Source, string Key);
/// An app whose reported name was in the installation records read earlier and is in none of
/// the records now, and each of whose earlier records' own subkeys is gone, with the publishers
/// reported for it earlier and those subkeys.
public sealed record RemovedApp(string Name, IReadOnlyList<string> Publishers, IReadOnlyList<RecordKey> Keys)
{
    public IReadOnlyList<string> InstallFolders { get; init; } = Array.Empty<string>();
}
/// Unconfirmed: apps missing from the records now whose own earlier subkeys could still be
/// opened or could not be checked; they are not looked for, and their names and publishers
/// count as still recorded.
public sealed record RemovedApps(IReadOnlyList<RemovedApp> Apps, IReadOnlyList<RemovedApp> Unconfirmed)
{
    public static readonly RemovedApps None = new(Array.Empty<RemovedApp>(), Array.Empty<RemovedApp>());
}
/// A plain folder named exactly as a removed app, directly in a base folder or in a folder
/// named exactly as one of its publishers there (Publisher is then that folder's name).
public sealed record LeftoverFolder(RemovedApp App, LeftoverBase Base, string? Publisher, string Name, string Path);
public sealed record LeftoverItem(LeftoverFolder Folder, AppCacheSize? Size)
{
    public FolderRegistrations? Registrations { get; init; }
}
public enum LeftoverBaseState { Read, NotFound, Unavailable }
/// Partial: a limit stopped the listing. Unreadable: a publisher folder in it could not be read.
public sealed record LeftoverBaseResult(LeftoverBase Base, string? Path, LeftoverBaseState State, string? Issue, bool Partial, bool Unreadable);
public sealed record AppLeftoverResult(IReadOnlyList<RemovedApp> Removed, IReadOnlyList<RemovedApp> Unconfirmed, IReadOnlyList<LeftoverBaseResult> Bases, IReadOnlyList<LeftoverItem> Items,
    IReadOnlyList<string> Skipped, bool FolderLimit, bool DiscoveryLimit, bool MeasureEntryLimit, bool MeasureTimeLimit, DateTimeOffset Recorded)
{
    public RegisteredTargetSnapshot? RegistrationSnapshot { get; init; }
    public IReadOnlyList<RemovedAppRegistrations> Registrations { get; init; } = Array.Empty<RemovedAppRegistrations>();
}
public sealed record RemovedAppRegistrations(string App, string? Folder);
public sealed record AppLeftoverLimits(int DirectoryEntries = 4096, int DiscoveryEntries = 20000, int Folders = 256, int MeasureEntries = 100000, int MeasureSeconds = 30);

public static class AppLeftovers
{
    public const string Notice = "After you uninstall apps in Windows Settings, this looks for folders they may have left in your app data: a plain folder named exactly as an app that was in the installation records when that window opened and is in none of them now, directly in Local, Roaming or LocalLow app data or your own Programs folder, or in a folder there named exactly as that app's publisher. An app counts as removed only when each of its own earlier installation records can no longer be opened. A matching name does not prove that a folder belonged to the app: another app, a newer edition or a portable copy can use the same name, and the folder can hold saved games, documents or settings you still want. Names also used by an app that is still recorded or by its publisher, names that a recorded app's name begins with (\"Zoom\" while \"Zoom Workplace\" is recorded), and shared folders such as Microsoft, Packages, Programs and Temp are never listed. A publisher's own folder is never listed, only the app's folder inside it, and a publisher folder named as an app is not searched. Nothing is selected for you. Before each move the app must still be missing from the installation records, the folder must still be a plain local folder at the same place, on a fixed drive with a Recycle Bin, and no running program whose path Windows reports may be inside it (protected programs may report none, and short 8.3 paths are not matched). If a folder is too large for the Recycle Bin, Windows asks before deleting it permanently. Undo Move puts back the last moved folders while this window is open, unless something new is in their place. Afterwards, restore them from the Recycle Bin.";
    public const string Limits = "Search: at most 4,096 entries per folder, 256 folders and 20,000 entries in all. Sizes: one shared metadata budget of 100,000 entries and about 30 soft seconds, shared fairly among the folders, with at most 20,000 entries and four levels below any one folder; an incomplete size is a lower bound. Sizes are logical bytes from directory records, not reclaimable space: links, cloud placeholders and system items inside are skipped. Apps from the Microsoft Store, other Windows users, other drives are not searched for folders. Registration targets are inspected separately under Still Registered Targets with their own limits.";
    public const string NotComplete = "The installation records could not all be read just now, so apps that are still installed cannot be told apart from removed ones. Nothing was searched. The list read earlier is kept: choose Find Leftovers again in a moment. If the installation records window itself reports a partial snapshot (for example an entry that cannot be read, or more than 1,024 entries in one view), this keeps happening and leftovers cannot be looked for on this PC.";
    public const string RecordedAgain = "Its app is in the installation records again, or they could not all be read or checked just now, so it was left alone.";
    public const string Unreadable = "This folder could not be read.";
    public const string SomeUnreadable = "Some publisher folders in it could not be read.";
    public const string ProgramInside = "A running program is inside it, or Swivel could not list the running programs, so it was left alone. Quit that program and try again.";
    public const string NotRecycled = "Windows did not move it to the Recycle Bin. If Windows removed part of it, that part cannot be restored with Undo.";

    // Folders shared by many apps or by Windows itself; never offered, whatever an app was called.
    private static readonly HashSet<string> Shared = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Windows", "Packages", "Programs", "Temp", "Publishers", "Comms", "ConnectedDevicesPlatform", "CrashDumps", "D3DSCache",
        "Package Cache", "PackageManagement", "PlaceholderTileLogoFolder", "VirtualStore", "IsolatedStorage", "Application Data", "History",
        "Temporary Internet Files", "Common Files", "Google", "Mozilla", "Apple", "Apple Computer", "Adobe", "Intel", "NVIDIA", "NVIDIA Corporation", "AMD", "Swivel",
    };

    /// Apps reported earlier and gone from every record now (names compared ignoring case, as
    /// Windows compares folder names), in name order, each confirmed by opening every one of its
    /// earlier records' own subkeys again with `recheck`. A name whose subkey is still there (a
    /// renamed app, or one skipped while a running uninstaller changed the list) or cannot be
    /// checked is only reported as unconfirmed. Throws when the records now are partial, since a
    /// missing name could then be one that was not read.
    public static RemovedApps Removed(InstalledAppsSnapshot before, InstalledAppsSnapshot after, Func<RegistrationSource, string, RecordKeyState> recheck, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(before); ArgumentNullException.ThrowIfNull(after); ArgumentNullException.ThrowIfNull(recheck);
        if (!Comparable(after)) throw new UserError(NotComplete);
        var still = new HashSet<string>(after.Records.Where(r => r.Name.State == RegistrationValueState.Present).Select(r => r.Name.Value!), StringComparer.OrdinalIgnoreCase);
        var apps = new List<RemovedApp>(); var unconfirmed = new List<RemovedApp>();
        foreach (var group in before.Records.Where(r => r.Name.State == RegistrationValueState.Present && !still.Contains(r.Name.Value!))
            .GroupBy(r => r.Name.Value!, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.First().Name.Value!, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            string name = group.First().Name.Value!;
            var keys = group.Where(r => r.Key is not null).Select(r => new RecordKey(r.Source, r.Key!)).Distinct().ToArray();
            bool gone = group.All(r => r.Key is not null && InstalledApps.ValidKey(r.Key)) && keys.All(k => recheck(k.Source, k.Key) == RecordKeyState.Gone);
            token.ThrowIfCancellationRequested();
            (gone ? apps : unconfirmed).Add(new(name, Array.AsReadOnly(group.Where(r => r.Publisher.State == RegistrationValueState.Present)
                .Select(r => r.Publisher.Value!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()), Array.AsReadOnly(keys))
                { InstallFolders = Array.AsReadOnly(group.Where(r => r.InstallLocation?.State == RegistrationValueState.Present)
                    .Select(r => RegisteredTargets.Folder(r.InstallLocation!.Value)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray()) });
        }
        return new(apps.AsReadOnly(), unconfirmed.AsReadOnly());
    }

    /// Complete enough to tell removed apps apart: every view enumerated to its end and every
    /// entry's name and publisher read. Unlike InstalledAppsSnapshot.Partial, a version field
    /// that cannot be read does not count, since versions are never compared here.
    public static bool Comparable(InstalledAppsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        static bool Read(RegistrationText text) => text.State is RegistrationValueState.Present or RegistrationValueState.Missing or RegistrationValueState.Empty;
        return !snapshot.Roots.Any(r => (r.Issues & ~RegistrationRootIssue.Missing) != 0)
            && snapshot.Records.All(r => r.Issues == RegistrationEntryIssue.None && Read(r.Name) && Read(r.Publisher));
    }

    /// Just before a move, the search's own rules against the installation records `now`, which
    /// must be comparable: no recorded name or publisher equal to the app's name or beginning
    /// with it, no recorded app named as the publisher folder it is in, and each of its earlier
    /// subkeys still gone. Throws a UserError otherwise.
    public static void StillRemoved(LeftoverFolder folder, InstalledAppsSnapshot now, Func<RegistrationSource, string, RecordKeyState> recheck)
    {
        ArgumentNullException.ThrowIfNull(folder); ArgumentNullException.ThrowIfNull(now); ArgumentNullException.ThrowIfNull(recheck);
        var app = folder.App;
        bool Taken(RegistrationText text) => text.State == RegistrationValueState.Present && (string.Equals(text.Value, app.Name, StringComparison.OrdinalIgnoreCase) || Extends(text.Value!, app.Name));
        if (!Comparable(now) || now.Records.Any(r => Taken(r.Name) || Taken(r.Publisher))
            || folder.Publisher is { } publisher && now.Records.Any(r => r.Name.State == RegistrationValueState.Present && string.Equals(r.Name.Value, publisher, StringComparison.OrdinalIgnoreCase))
            || app.Keys.Count == 0 || app.Keys.Any(k => recheck(k.Source, k.Key) != RecordKeyState.Gone))
            throw new UserError(RecordedAgain);
    }

    /// True when a running program is inside `folder`, or when the running programs could not all
    /// be listed (null).
    public static bool InUse(IReadOnlyList<string>? programs, string folder) => programs is null || Inside(programs, folder).Count > 0;

    // "Zoom Workplace" or "Zoom (x64)" may be a renamed edition of a removed "Zoom"; "Zoom2" is not.
    private static bool Extends(string other, string name) => other.Length > name.Length && other.StartsWith(name, StringComparison.OrdinalIgnoreCase) && !char.IsLetterOrDigit(other[name.Length]);

    /// Lists the folders named as removed apps (directory records only), then measures them with
    /// one shared budget. `reported` gives Windows' raw path of each base folder; `open` opens a
    /// held metadata directory by full path. `after` is the snapshot the apps were found missing
    /// from: names and publishers still recorded there are never matched.
    public static AppLeftoverResult Read(RemovedApps removed, InstalledAppsSnapshot after, Func<LeftoverBase, string?> reported,
        Func<string, IFolderMetadata> open, CancellationToken token, AppLeftoverLimits? limits = null, Func<TimeSpan>? elapsed = null)
    {
        ArgumentNullException.ThrowIfNull(removed); ArgumentNullException.ThrowIfNull(after); ArgumentNullException.ThrowIfNull(reported); ArgumentNullException.ThrowIfNull(open);
        limits ??= new();
        if (limits.DirectoryEntries is < 1 or > 4096 || limits.DiscoveryEntries is < 1 or > 20000 || limits.Folders is < 1 or > 256
            || limits.MeasureEntries is < 1 or > 100000 || limits.MeasureSeconds is < 1 or > 30) throw new ArgumentOutOfRangeException(nameof(limits));
        token.ThrowIfCancellationRequested();
        // Unconfirmed apps may still be installed (a record skipped while the list changed), so
        // their earlier names and publishers count as recorded too.
        var taken = new HashSet<string>(removed.Unconfirmed.SelectMany(a => a.Publishers.Append(a.Name)), StringComparer.OrdinalIgnoreCase);
        var recordedNames = new HashSet<string>(removed.Unconfirmed.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var record in after.Records)
        {
            if (record.Name.State == RegistrationValueState.Present) { taken.Add(record.Name.Value!); recordedNames.Add(record.Name.Value!); }
            if (record.Publisher.State == RegistrationValueState.Present) taken.Add(record.Publisher.Value!);
        }
        // App names, removed, unconfirmed or still recorded: a publisher folder named as one may be
        // that app's own folder (or already offered whole), so it is not searched.
        var appNames = new HashSet<string>(recordedNames.Concat(removed.Apps.Select(a => a.Name)), StringComparer.OrdinalIgnoreCase);
        // A name that can be a folder name and is not a shared folder. An app's own folder name
        // must also not be any name or publisher still recorded; a publisher folder may be a
        // recorded publisher's, since only the removed app's folder inside it is offered.
        bool Usable(string name, bool publisher)
        {
            try { WindowsPathPolicy.ValidateName(name); } catch (UserError) { return false; }
            return !Shared.Contains(name) && (publisher ? !appNames.Contains(name) : !taken.Contains(name) && !taken.Any(other => Extends(other, name)));
        }
        var apps = removed.Apps.Where(app => Usable(app.Name, false)).ToList();
        var skipped = removed.Apps.Where(app => !Usable(app.Name, false)).Select(app => app.Name).ToList();

        var bases = new List<LeftoverBaseResult>(); var found = new List<LeftoverFolder>(); var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int discovered = 0; bool folderLimit = false, discoveryLimit = false;
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
        static FolderEntry? Plain(List<FolderEntry> entries, string name)
            => entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase) && e.Directory && AppCaches.IsPlain(e));
        bool Add(RemovedApp app, LeftoverBase root, string? publisher, string name, string path)
        {
            if (!paths.Add(path)) return true;
            if (found.Count >= limits.Folders) { folderLimit = true; return false; }
            found.Add(new(app, root, publisher, name, path)); return true;
        }

        foreach (var root in Enum.GetValues<LeftoverBase>())
        {
            token.ThrowIfCancellationRequested();
            var place = ReportedFolderPath.FromReported(reported(root));
            if (!place.CanAttempt) { bases.Add(new(root, null, LeftoverBaseState.Unavailable, place.Issue, false, false)); continue; }
            IFolderMetadata directory;
            try { directory = open(place.Path!); }
            catch (Exception error) when (!token.IsCancellationRequested && AppCaches.Missing(error)) { bases.Add(new(root, place.Path, LeftoverBaseState.NotFound, null, false, false)); continue; }
            catch (Exception error) when (!token.IsCancellationRequested && AppCaches.Expected(error))
            { bases.Add(new(root, place.Path, LeftoverBaseState.Unavailable, error is UserError ? error.Message : Unreadable, false, false)); continue; }
            bool partial = false, unreadable = false; string? issue = null;
            using (directory)
            {
                try
                {
                    var entries = Listed(directory, ref partial);
                    foreach (var app in apps)
                        if (Plain(entries, app.Name) is { } match && !Add(app, root, null, match.Name, place.Path + "\\" + match.Name)) { partial = true; break; }
                    // Each publisher folder is listed once, for every removed app it may hold.
                    var byPublisher = apps.SelectMany(app => app.Publishers.Where(p => Usable(p, true)).Select(p => (Publisher: p, App: app)))
                        .GroupBy(pair => pair.Publisher, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.Ordinal);
                    foreach (var group in byPublisher)
                    {
                        token.ThrowIfCancellationRequested();
                        if (discoveryLimit || folderLimit) { partial = true; break; }
                        if (Plain(entries, group.Key) is not { } folder) continue;
                        List<FolderEntry> inside;
                        try { using var child = directory.OpenChild(folder.Name); bool cut = false; inside = Listed(child, ref cut); partial |= cut; }
                        catch (Exception error) when (!token.IsCancellationRequested && AppCaches.Expected(error)) { unreadable = true; continue; }
                        foreach (var app in group.Select(pair => pair.App).Distinct())
                            if (Plain(inside, app.Name) is { } match && !Add(app, root, folder.Name, match.Name, place.Path + "\\" + folder.Name + "\\" + match.Name)) { partial = true; break; }
                    }
                }
                catch (Exception error) when (!token.IsCancellationRequested && AppCaches.Expected(error)) { unreadable = true; issue = Unreadable; }
            }
            bases.Add(new(root, place.Path, LeftoverBaseState.Read, issue, partial, unreadable));
        }

        var watch = Stopwatch.StartNew(); elapsed ??= () => watch.Elapsed;
        var sizes = AppCaches.MeasureShared(found.Select(folder => folder.Path).ToArray(), open, token, limits.MeasureEntries, limits.MeasureSeconds, elapsed, out bool entryLimit, out bool timeLimit);
        var items = found.Select((folder, i) => new LeftoverItem(folder, sizes[i])).ToArray();
        return new(removed.Apps, removed.Unconfirmed, new ReadOnlyCollection<LeftoverBaseResult>(bases), Array.AsReadOnly(items), skipped.AsReadOnly(),
            folderLimit, discoveryLimit, entryLimit, timeLimit, DateTimeOffset.Now);
    }

    /// The program paths of the running processes, as Windows reports them, or null when the
    /// running processes could not all be listed. Processes whose path Windows does not report
    /// (often system or protected ones) are left out.
    public static IReadOnlyList<string>? ProgramPaths(IAppMemorySource source, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(source);
        var ids = source.Enumerate();
        if (ids.Unavailable || ids.Limited) return null;
        var paths = new List<string>();
        foreach (uint id in ids.Ids)
        {
            token.ThrowIfCancellationRequested();
            using var process = source.Open(id);
            if (process?.ImagePath() is { } path) paths.Add(path);
        }
        return paths.AsReadOnly();
    }

    /// The programs among `programs` that are inside `folder` (any case).
    public static IReadOnlyList<string> Inside(IReadOnlyList<string> programs, string folder)
    {
        ArgumentNullException.ThrowIfNull(programs); ArgumentException.ThrowIfNullOrEmpty(folder);
        string prefix = folder.TrimEnd('\\') + "\\";
        return Array.AsReadOnly(programs.Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray());
    }

    public static string Place(LeftoverBase root) => root switch
    {
        LeftoverBase.Local => "Local app data",
        LeftoverBase.Roaming => "Roaming app data",
        LeftoverBase.LocalLow => "LocalLow app data",
        LeftoverBase.Programs => "Your Programs folder",
        _ => throw new ArgumentOutOfRangeException(nameof(root)),
    };
}
