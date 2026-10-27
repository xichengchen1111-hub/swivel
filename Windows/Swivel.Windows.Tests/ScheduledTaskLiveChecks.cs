using System;
using System.Linq;
using System.Threading;
using Swivel.Core;
using Swivel.Windows;

internal static class ScheduledTaskLiveChecks
{
    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        // Windows-only, explicit test-run metadata sample. No task is created, run, edited or deleted.
        // An empty or partial inventory can be valid under this process's permissions.
        reject(() => ScheduledTaskReader.ReadAsync(new CancellationToken(true)).GetAwaiter().GetResult(), "Live pre-cancelled Task Scheduler read does not connect");
        var result = ScheduledTaskReader.ReadAsync(default).GetAwaiter().GetResult();
        check(result.FolderAttempts <= ScheduledTasks.MaxFolders && result.TaskAttempts <= ScheduledTasks.MaxTasks && result.RetainedTextUnits <= ScheduledTasks.MaxTextUnits && result.Issues.Count <= ScheduledTasks.MaxIssues, "Live Task Scheduler sample stays inside retained-result budgets");
        check(result.Tasks.Count <= result.TaskAttempts && result.Tasks.All(x => x.RowIndex >= 1 && x.RowIndex <= result.TaskAttempts && (x.Name == null || x.Name.Length <= ScheduledTasks.MaxValueUnits) && (x.Path == null || x.Path.Length <= ScheduledTasks.MaxValueUnits)), "Live sample keeps bounded historical rows including unavailable fields");
        check(result.Issues.All(x => x.Reason == ScheduledTasks.ReadFailed || x.Reason == ScheduledTasks.InvalidValue || x.Reason == ScheduledTasks.NotRead || x.Reason == ScheduledTasks.CleanupFailed || x.Reason == "Child folders were not inspected at the depth limit." || x.Reason == "Child folders were not inspected at the folder-entry limit." || x.Reason == "A repeated task-folder path was not inspected again." || x.Reason == "A repeated task path is retained as a separate historical row." || x.Reason == "Task Scheduler COM initialization could not be released."), "Live issues contain fixed diagnostics, not private COM exception messages");
    }
}
