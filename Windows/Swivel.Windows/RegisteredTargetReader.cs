using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Swivel.Core;

namespace Swivel.Windows;

internal static class RegisteredTargetReader
{
    internal const string AppPaths = @"Software\Microsoft\Windows\CurrentVersion\App Paths", Classes = @"Software\Classes";
    internal static RegisteredTargetSnapshot Read(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var watch = Stopwatch.StartNew();
        bool Stop() { token.ThrowIfCancellationRequested(); return watch.Elapsed >= InstalledApps.SoftTimeLimit; }
        var rows = new List<RegisteredCommand>(); var issues = new List<string>();
        var run = StartupRecords.ScanWithClock(new StartupReader.Source(), () => watch.Elapsed, token);
        if (run.Partial) issues.Add(RegisteredTargets.RunFailed);
        foreach (var row in run.Records)
            rows.Add(new("Run / RunOnce", StartupRecords.Origin(row.Location, s => s) + " · " + (row.Name ?? "?"), row.Issues == StartupIssue.None && row.Command.State == RegistrationValueState.Present ? row.Command.Value : null));
        if (!Stop())
        {
            var serviceIssues = new List<string>(); rows.AddRange(ServiceReader.Commands(token, Stop, serviceIssues));
            if (serviceIssues.Count > 0) issues.Add(RegisteredTargets.ServicesFailed);
        }
        else issues.Add(RegisteredTargets.ServicesFailed);
        if (!Stop())
        {
            // Translate the existing task scanner's 15-second clock to this scan's 10-second deadline.
            var tasks = ScheduledTaskReader.ReadAsync(token, new ScheduledTaskAutomation(), true, () => watch.Elapsed + TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            if (tasks.Partial) issues.Add(RegisteredTargets.TasksFailed);
            foreach (var row in tasks.Tasks)
            {
                if (row.Executables is null) rows.Add(new("Scheduled Tasks", row.Path ?? row.Name ?? "?", null, true));
                else foreach (string? target in row.Executables)
                {
                    if (rows.Count >= RegisteredTargets.MaxRows) { issues.Add(RegisteredTargets.TasksFailed); break; }
                    rows.Add(new("Scheduled Tasks", row.Path ?? row.Name ?? "?", target, true));
                }
            }
        }
        else issues.Add(RegisteredTargets.TasksFailed);
        foreach (var source in new[] { RegistrationSource.User64, RegistrationSource.User32 })
        {
            Registry(source, AppPaths, true);
            Registry(source, Classes, false);
            Registry(source, Classes + @"\Applications", false);
        }
        var env = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables()) if (entry.Key is string key && entry.Value is string value) env[key] = value;
        return RegisteredTargets.Resolve(rows, issues.Distinct().ToArray(), env, token);

        void Registry(RegistrationSource source, string path, bool literal)
        {
            if (Stop()) { issues.Add(literal ? RegisteredTargets.AppPathsFailed : RegisteredTargets.ClassesFailed); return; }
            using var hive = InstalledAppsReader.Predefined(source); uint view = InstalledAppsReader.View(source);
            int code = InstalledAppsReader.OpenKey(hive, path, InstalledAppsReader.Enumerate | view, out var handle);
            using var root = handle is null ? null : new InstalledAppsReader.Root(handle, view);
            token.ThrowIfCancellationRequested();
            if (code != 0 || root is null) { if (code != 2) issues.Add(literal ? RegisteredTargets.AppPathsFailed : RegisteredTargets.ClassesFailed); return; }
            for (int i = 0; i <= InstalledApps.MaxPerRoot; i++)
            {
                if (Stop() || rows.Count >= RegisteredTargets.MaxRows) { issues.Add(literal ? RegisteredTargets.AppPathsFailed : RegisteredTargets.ClassesFailed); break; }
                var key = root.At((uint)i); token.ThrowIfCancellationRequested();
                if (key.Kind == RegistrationKeyKind.End) break;
                if (i == InstalledApps.MaxPerRoot) { issues.Add(literal ? RegisteredTargets.AppPathsFailed : RegisteredTargets.ClassesFailed); break; }
                if (key.Kind != RegistrationKeyKind.Item) { issues.Add(literal ? RegisteredTargets.AppPathsFailed : RegisteredTargets.ClassesFailed); if (key.Kind == RegistrationKeyKind.Unavailable) break; continue; }
                // Fixed suffix; returned key names pass the existing strict no-separator validation.
                string target = path + "\\" + key.Name + (literal ? "" : @"\shell\open\command");
                code = InstalledAppsReader.OpenKey(hive, target, InstalledAppsReader.Query | view, out SafeRegistryHandle? entry);
                using (entry)
                {
                    token.ThrowIfCancellationRequested();
                    if (code == 2) continue;
                    string origin = InstalledApps.Origin(source, s => s) + " · " + path + "\\" + key.Name;
                    if (code != 0 || entry is null) { issues.Add(literal ? RegisteredTargets.AppPathsFailed : RegisteredTargets.ClassesFailed); rows.Add(new("Current-user App Paths / open commands", origin, null, literal)); continue; }
                    var value = InstalledAppsReader.ReadText(entry, ""); token.ThrowIfCancellationRequested();
                    if (value.State is RegistrationValueState.Missing or RegistrationValueState.Empty) continue;
                    rows.Add(new("Current-user App Paths / open commands", origin, value.State == RegistrationValueState.Present ? value.Value : null, literal));
                }
            }
        }
    }
}
