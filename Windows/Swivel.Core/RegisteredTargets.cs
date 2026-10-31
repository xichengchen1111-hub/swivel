using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace Swivel.Core;

public sealed record RegisteredCommand(string Source, string Name, string? Command, bool LiteralPath = false, bool ExpandEnvironment = false);
public sealed record RegisteredTarget(string Source, string Name, string? Executable);
public sealed record RegisteredTargetSnapshot(IReadOnlyList<RegisteredTarget> Targets, IReadOnlyList<string> Issues);
public sealed record FolderRegistrations(string? Folder, IReadOnlyList<RegisteredTarget> Matches, IReadOnlyList<RegisteredTarget> Unresolved, IReadOnlyList<string> Issues)
{
    public bool CanTrash => Folder is not null && Matches.Count == 0 && Unresolved.Count == 0 && Issues.Count == 0;
}

/// Lexical executable evidence only; no PATH search, filesystem probing, shell execution or name matching.
public static class RegisteredTargets
{
    public const int MaxRows = 8192, MaxCommand = 2048;
    public const string LocationNotice = "This inspection also retains bounded reported InstallLocation text in memory for selected-app and post-uninstall registration checks. It is not included in the installation-record report. A reported path does not prove ownership or existence; missing or unsupported locations stay unknown.";
    public const string Title = "Still Registered Targets";
    public const string Notice = "Read-only executable-path evidence from Run/RunOnce, Win32 services, Task Scheduler executable actions, and current-user App Paths and shell open commands (32/64-bit views). Only .exe targets with strict absolute local paths (up to 240 UTF-16 units) inside the chosen folder match; names and arguments never establish ownership. Only Run/RunOnce and current-user App Paths/open commands expand variables using this process's environment. Service and task paths containing variables stay unresolved because their execution environment is unknown. Missing variables, ambiguous commands, short 8.3 paths, relative paths and unsupported actions stay unresolved. This does not prove ownership, that a target exists, or that an item is active. Nothing is changed in the registry, services or tasks.";
    public const string Limits = "One shared soft 10-second budget for registration sources; blocked calls and cleanup can take longer. Existing source caps apply, with at most 1,024 service configurations, 32 actions per task, 1,024 keys per registry location and 8,192 combined rows. Hidden tasks, silently permission-omitted services, other users and ARM32-specific views could not be read. Registry links and physical file identity are not resolved. No matches is not proof of no registrations. Unresolved entries or incomplete reads prevent offering a leftover folder for the Recycle Bin; a fresh check is required before every move.";
    public const string Blocked = "A registered target points inside this folder, or registration evidence is unresolved or incomplete. It cannot be moved to the Recycle Bin.";
    public const string RunFailed = "Run/RunOnce could not be read completely; access, value or scan limit.";
    public const string ServicesFailed = "Service configurations could not be read completely; access, value or scan limit.";
    public const string TasksFailed = "Scheduled-task actions could not be read completely; access, value or scan limit.";
    public const string AppPathsFailed = "Current-user App Paths could not be read completely; access, value or scan limit.";
    public const string ClassesFailed = "Current-user shell open commands could not be read completely; access, value or scan limit.";
    public const string NoFolder = "No supported absolute install folder was reported. Choose a folder explicitly to inspect its registrations.";
    public const string ReportLimit = "The registration report exceeds 4 MiB of UTF-8 text. Choose a smaller scope.";
    public const string Incomplete = "Registration source could not be read completely (access, value, row or soft time limit).";
    public const string Unresolved = "Unresolved target; not guessed or attributed to this app";
    public const string Matched = "Still registered: executable inside this folder";
    public const string NoMatches = "No parsed executable matched; unreadable or uninspected registrations may remain.";

    public static string? Folder(string? value)
    {
        if (value is null || value.Length > 240) return null;
        string? path = ReportedFolderPath.FromReported(value).Path;
        // Never claim an 8.3 spelling identifies its long-path counterpart.
        return path is not null && !path.Contains('~') ? path : null;
    }
    public static bool Contains(string folder, string executable)
    {
        string? root = Folder(folder), path = Folder(executable);
        return root is not null && path is not null && path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
    }

    public static string? Executable(string? command, bool literalPath, IReadOnlyDictionary<string,string> environment, bool expandEnvironment = true)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Length > MaxCommand || command.Any(c => c < 32 && c != '\t' || c == 127 || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format)) return null;
        if (StartupRecords.Validate(new(command, RegistrationValueState.Present)).State != RegistrationValueState.Present) return null;
        string text = command.Trim(' ', '\t'); string path; bool unquoted = !literalPath && text[0] != '"';
        if (literalPath)
        {
            path = text;
            if (path.Length >= 2 && path[0] == '"' && path[^1] == '"') path = path[1..^1];
        }
        else
        {
            // First-token parsing is conservative: an unquoted path with spaces is ambiguous.
            int end;
            if (text[0] == '"')
            {
                end = text.IndexOf('"', 1);
                if (end < 0 || end + 1 < text.Length && text[end + 1] is not ' ' and not '\t') return null;
                path = text[1..end]; end++;
            }
            else { end = text.IndexOfAny([' ', '\t']); if (end < 0) end = text.Length; path = text[..end]; }
            bool quoted = false; int slashes = 0;
            foreach (char c in text[end..])
            {
                if (c == '"' && slashes % 2 == 0) quoted = !quoted;
                slashes = c == '\\' ? slashes + 1 : 0;
            }
            if (quoted) return null;
        }
        var expanded = new StringBuilder();
        for (int i = 0; i < path.Length; i++)
        {
            if (path[i] != '%') expanded.Append(path[i]);
            else
            {
                if (!expandEnvironment) return null; // Other execution contexts need not share this process's environment.
                int end = path.IndexOf('%', i + 1);
                if (end < 0 || end == i + 1) return null;
                string key = path[(i + 1)..end];
                string? replacement = environment.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
                if (replacement is null || replacement.Length > 240 || replacement.Contains('%')) return null;
                expanded.Append(replacement); i = end;
            }
            if (expanded.Length > 240) return null;
        }
        string result = expanded.ToString();
        if (unquoted && result.IndexOfAny([' ', '\t']) >= 0) return null;
        return result.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && Folder(result) is not null ? result : null;
    }
    public static RegisteredTargetSnapshot Resolve(IEnumerable<RegisteredCommand> commands, IReadOnlyList<string> issues, IReadOnlyDictionary<string,string> environment, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var rows = new List<RegisteredTarget>(); var problems = issues.Take(MaxRows).ToList();
        foreach (var row in commands)
        {
            token.ThrowIfCancellationRequested();
            if (rows.Count == MaxRows) { problems.Add(Incomplete); break; }
            static string Bound(string value) => value.Length <= 1024 ? value : value[..1024];
            rows.Add(new(Bound(row.Source), Bound(row.Name), Executable(row.Command, row.LiteralPath, environment, row.ExpandEnvironment)));
        }
        token.ThrowIfCancellationRequested(); return new(rows.AsReadOnly(), problems.AsReadOnly());
    }
    public static FolderRegistrations ForFolder(RegisteredTargetSnapshot snapshot, string? folder, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); string? root = Folder(folder);
        var matches = new List<RegisteredTarget>(); var unresolved = new List<RegisteredTarget>();
        foreach (var row in snapshot.Targets)
        {
            token.ThrowIfCancellationRequested();
            if (row.Executable is null) unresolved.Add(row);
            else if (root is not null && Contains(root, row.Executable)) matches.Add(row);
        }
        return new(root, matches.AsReadOnly(), unresolved.AsReadOnly(), snapshot.Issues);
    }
    public static void RequireTrash(FolderRegistrations result) { if (!result.CanTrash) throw new UserError(Blocked); }
    public static string Report(FolderRegistrations result, Func<string,string> l)
    {
        var lines = new List<string> { l(Title), l(Notice), l(Limits), l("Folder") + ": " + ServiceRecords.Visible(result.Folder ?? l("Unknown")), l(result.Folder is null ? NoFolder : result.Matches.Count > 0 ? Matched : NoMatches) };
        foreach (var row in result.Matches) lines.Add(l(row.Source) + " · " + ServiceRecords.Visible(row.Name) + " · " + ServiceRecords.Visible(row.Executable!));
        foreach (var row in result.Unresolved) lines.Add(l(row.Source) + " · " + ServiceRecords.Visible(row.Name) + " · " + l(Unresolved));
        foreach (string issue in result.Issues) lines.Add(l(issue));
        int bytes = 0;
        foreach (string line in lines)
        {
            int count = Encoding.UTF8.GetByteCount(line) + Encoding.UTF8.GetByteCount(Environment.NewLine);
            if (count > InstalledApps.MaxReportBytes - bytes) throw new UserError(ReportLimit);
            bytes += count;
        }
        return string.Join(Environment.NewLine, lines);
    }
}
