using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace Swivel.Core;

public enum ScheduledTaskPhase { OpenService, OpenFolder, TaskCollection, TaskCount, TaskItem, Name, Path, State, Enabled, LastResult, FolderCollection, FolderCount, FolderItem, FolderPath, Cleanup, Validation, Limit, Actions }
public enum ScheduledTaskStopReason { Completed, FolderLimit, TaskLimit, TextLimit, TimeLimit }
public sealed record ScheduledTaskRow(int RowIndex, string? Name, string? Path, int? State, bool? Enabled, uint? LastResult)
{
    public IReadOnlyList<string?>? Executables { get; init; }
}
public sealed record ScheduledTaskIssue(int? RowIndex, string? NamespacePath, ScheduledTaskPhase Phase, uint? ErrorCode, string Reason);
public sealed record ScheduledTaskFolder(string Path, int Depth, int? ReportedTasks, int AttemptedTasks, int? ReportedFolders, int AttemptedChildren, bool Complete);
public sealed class ScheduledTaskSnapshot
{
    public IReadOnlyList<ScheduledTaskRow> Tasks { get; }
    public IReadOnlyList<ScheduledTaskFolder> Folders { get; }
    public IReadOnlyList<ScheduledTaskIssue> Issues { get; }
    public DateTimeOffset Captured { get; }
    public TimeSpan Elapsed { get; }
    public int FolderAttempts { get; }
    public int TaskAttempts { get; }
    public int RetainedTextUnits { get; }
    public int OmittedIssues { get; }
    public ScheduledTaskStopReason StopReason { get; }
    public bool Partial => Issues.Count != 0 || OmittedIssues != 0 || StopReason != ScheduledTaskStopReason.Completed;
    public ScheduledTaskSnapshot(IEnumerable<ScheduledTaskRow> tasks, IEnumerable<ScheduledTaskFolder> folders, IEnumerable<ScheduledTaskIssue> issues,
        DateTimeOffset captured, TimeSpan elapsed, int folderAttempts, int taskAttempts, int retainedTextUnits, int omittedIssues, ScheduledTaskStopReason stopReason)
    {
        var rows = tasks.ToArray(); var roots = folders.ToArray(); var problems = issues.ToArray();
        if (rows.Length > ScheduledTasks.MaxTasks || roots.Length > ScheduledTasks.MaxFolders || problems.Length > ScheduledTasks.MaxIssues ||
            folderAttempts is < 0 or > ScheduledTasks.MaxFolders || taskAttempts is < 0 or > ScheduledTasks.MaxTasks ||
            retainedTextUnits is < 0 or > ScheduledTasks.MaxTextUnits || omittedIssues < 0 || !Enum.IsDefined(stopReason) || elapsed < TimeSpan.Zero ||
            rows.Any(x => x.RowIndex < 1 || x.RowIndex > taskAttempts || x.Name?.Length > ScheduledTasks.MaxValueUnits || x.Path?.Length > ScheduledTasks.MaxValueUnits || x.Executables is { } commands && (commands.Count > 32 || commands.Any(c => c?.Length > ScheduledTasks.MaxValueUnits || c?.Contains('\0') == true))) ||
            rows.Select(x => x.RowIndex).Distinct().Count() != rows.Length || roots.Any(x => x.Path.Length > ScheduledTasks.MaxValueUnits || x.Depth is < 0 or > ScheduledTasks.MaxDepth) ||
            problems.Any(x => !Enum.IsDefined(x.Phase) || x.NamespacePath?.Length > ScheduledTasks.MaxValueUnits)) throw new ArgumentException("Invalid scheduled-task snapshot.");
        Tasks = Array.AsReadOnly(rows.Select(row => row.Executables is null ? row : row with { Executables = Array.AsReadOnly(row.Executables.ToArray()) }).ToArray()); Folders = Array.AsReadOnly(roots); Issues = Array.AsReadOnly(problems);
        Captured = captured; Elapsed = elapsed; FolderAttempts = folderAttempts; TaskAttempts = taskAttempts;
        RetainedTextUnits = retainedTextUnits; OmittedIssues = omittedIssues; StopReason = stopReason;
    }
}
public interface IScheduledTaskSource : IDisposable { IScheduledTaskFolder OpenFolder(string path); }
public interface IScheduledTaskFolder : IDisposable { IScheduledTaskItems GetTasks(); IScheduledTaskFolders GetFolders(); }
public interface IScheduledTaskItems : IDisposable { int Count(); IScheduledTaskItem At(int index); }
public interface IScheduledTaskFolders : IDisposable { int Count(); IScheduledTaskChild At(int index); }
public interface IScheduledTaskChild : IDisposable { string Path(); }
public interface IScheduledTaskItem : IDisposable
{
    string Name(); string Path(); int State(); bool Enabled(); uint LastResult();
    IReadOnlyList<string?> ExecutablePaths() => throw new NotSupportedException();
}
public sealed class ScheduledTaskLimitException : Exception { }
public sealed class ScheduledTaskValueException : Exception { }

/// <summary>Shared scan controls used before and after every native call. Cleanup never calls Checkpoint.</summary>
public sealed class ScheduledTaskAccess
{
    private readonly Func<TimeSpan> elapsed;
    private readonly Action<ScheduledTaskIssue> report;
    public CancellationToken Token { get; }
    public ScheduledTaskStopReason StopReason { get; private set; }
    public bool MustStop => StopReason is ScheduledTaskStopReason.TaskLimit or ScheduledTaskStopReason.TextLimit or ScheduledTaskStopReason.TimeLimit;
    internal ScheduledTaskAccess(CancellationToken token, Func<TimeSpan> elapsed, Action<ScheduledTaskIssue> report)
    { Token = token; this.elapsed = elapsed; this.report = report; }
    public void Checkpoint()
    {
        Token.ThrowIfCancellationRequested();
        if (elapsed() >= ScheduledTasks.SoftTimeLimit) StopReason = ScheduledTaskStopReason.TimeLimit;
        if (MustStop) throw new ScheduledTaskLimitException();
    }
    public void Report(ScheduledTaskIssue issue) => report(issue);
    internal void Limit(ScheduledTaskStopReason reason)
    {
        if (StopReason == ScheduledTaskStopReason.Completed || reason != ScheduledTaskStopReason.FolderLimit) StopReason = reason;
    }
}

public static class ScheduledTasks
{
    public const int MaxFolders = 128, MaxTasks = 1024, MaxDepth = 4, MaxTextUnits = 256 * 1024, MaxValueUnits = 1024, MaxIssues = 256;
    public static readonly TimeSpan SoftTimeLimit = TimeSpan.FromSeconds(15);
    public const string Scope = "One explicit read-only sample of local Task Scheduler metadata readable with this process's current permissions. Hidden tasks are excluded. This is not a list of tasks owned only by you. Task names may contain private information; namespace paths are hidden by default. Only an explicit copy includes the currently shown details. Nothing is sent to an assistant.";
    public const string Limits = "Up to 128 folder-entry attempts, 1,024 task-entry attempts including failures, four child levels and about 15 seconds. The shared name/path text budget is 262,144 UTF-16 units and 1,024 per value. Read text remains charged even if a later check rejects it. Windows may allocate collections or strings before these limits can be checked. Calls and cleanup can take longer; cancellation waits for completion. Permission omissions, concurrent changes and hidden tasks prevent a complete inventory. Only names, namespace paths, reported state, enabled flag and raw last-result codes are requested. Definitions, actions, scripts, triggers, principals and XML are not requested. Nothing is run, changed, enabled, disabled or deleted.";
    public const string ReadFailed = "Windows could not read this scheduled-task metadata field or collection.";
    public const string InvalidValue = "Windows returned an unexpected scheduled-task metadata value.";
    public const string NotRead = "This metadata was not retained because the scan limit was reached.";
    public const string CleanupFailed = "A scheduled-task COM reference could not be released.";
    public static string Phase(ScheduledTaskPhase phase) => phase switch
    {
        ScheduledTaskPhase.Actions => "Task executable actions", ScheduledTaskPhase.OpenService => "Task Scheduler connection", ScheduledTaskPhase.OpenFolder => "Task folder open",
        ScheduledTaskPhase.TaskCollection => "Task collection", ScheduledTaskPhase.TaskCount => "Reported task count",
        ScheduledTaskPhase.TaskItem => "Task entry", ScheduledTaskPhase.Name => "Task name", ScheduledTaskPhase.Path => "Task namespace path",
        ScheduledTaskPhase.State => "Reported task state", ScheduledTaskPhase.Enabled => "Reported enabled flag", ScheduledTaskPhase.LastResult => "Raw last task result",
        ScheduledTaskPhase.FolderCollection => "Child-folder collection", ScheduledTaskPhase.FolderCount => "Reported child-folder count",
        ScheduledTaskPhase.FolderItem => "Child-folder entry", ScheduledTaskPhase.FolderPath => "Child-folder namespace path",
        ScheduledTaskPhase.Cleanup => "Task Scheduler cleanup", ScheduledTaskPhase.Validation => "Task metadata validation", ScheduledTaskPhase.Limit => "Task scan limit",
        _ => throw new ArgumentOutOfRangeException(nameof(phase))
    };
    public static string State(int raw) => raw switch { 0 => "Unknown (reported)", 1 => "Disabled (reported)", 2 => "Queued (reported)", 3 => "Ready (reported)", 4 => "Running (reported)", _ => "Unknown task state value" };
    public static ScheduledTaskSnapshot Scan(Func<ScheduledTaskAccess, IScheduledTaskSource> open, CancellationToken token)
    { var watch = Stopwatch.StartNew(); return ScanWithClock(open, () => watch.Elapsed, token); }
    public static ScheduledTaskSnapshot ScanWithClock(Func<ScheduledTaskAccess, IScheduledTaskSource> open, Func<TimeSpan> elapsed, CancellationToken token, bool includeActions = false)
        => new ScanState(open, elapsed, token, includeActions).Run();

    private sealed class ScanState
    {
        private readonly Func<ScheduledTaskAccess, IScheduledTaskSource> open;
        private readonly Func<TimeSpan> elapsed;
        private readonly ScheduledTaskAccess access;
        private readonly List<ScheduledTaskRow> rows = new();
        private readonly List<ScheduledTaskFolder> folders = new();
        private readonly List<ScheduledTaskIssue> issues = new();
        private readonly Queue<(string Path, int Depth)> pending = new();
        private readonly HashSet<string> seenFolders = new(StringComparer.OrdinalIgnoreCase), seenTasks = new(StringComparer.OrdinalIgnoreCase);
        private int folderAttempts, taskAttempts, retained = 1, omitted;
        private long issueCount;
        private readonly bool includeActions;
        internal ScanState(Func<ScheduledTaskAccess, IScheduledTaskSource> open, Func<TimeSpan> elapsed, CancellationToken token, bool includeActions)
        {
            this.open = open ?? throw new ArgumentNullException(nameof(open)); this.elapsed = elapsed ?? throw new ArgumentNullException(nameof(elapsed));
            access = new(token, elapsed, Add); this.includeActions = includeActions;
        }
        private void Add(ScheduledTaskIssue issue)
        { issueCount++; if (issues.Count < MaxIssues) issues.Add(issue); else if (omitted < int.MaxValue) omitted++; }
        private void Problem(ScheduledTaskPhase phase, string? path, int? row, Exception? error = null, string? reason = null)
            => Add(new(row, path, phase, error == null || error is ScheduledTaskValueException or ScheduledTaskLimitException ? null : unchecked((uint)error.HResult), reason ?? (error is ScheduledTaskValueException ? InvalidValue : error is ScheduledTaskLimitException ? NotRead : ReadFailed)));
        private void Release(IDisposable? value, string? path, int? row)
        { try { value?.Dispose(); } catch (Exception error) { Problem(ScheduledTaskPhase.Cleanup, path, row, error, CleanupFailed); } }
        private string Keep(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > MaxValueUnits || value.Contains('\0')) throw new ScheduledTaskValueException();
            if (value.Length > MaxTextUnits - retained) { access.Limit(ScheduledTaskStopReason.TextLimit); throw new ScheduledTaskLimitException(); }
            retained += value.Length; return value;
        }
        internal ScheduledTaskSnapshot Run()
        {
            access.Token.ThrowIfCancellationRequested(); IScheduledTaskSource? source = null;
            try
            {
                access.Checkpoint(); source = open(access); access.Checkpoint();
                pending.Enqueue(("\\", 0)); seenFolders.Add("\\"); folderAttempts = 1;
                while (pending.TryDequeue(out var next))
                {
                    if (access.MustStop)
                    {
                        folders.Add(new(next.Path, next.Depth, null, 0, null, 0, false));
                        Problem(ScheduledTaskPhase.OpenFolder, next.Path, null, reason: NotRead); continue;
                    }
                    ReadFolder(source, next.Path, next.Depth);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { Problem(ScheduledTaskPhase.OpenService, null, null, error); }
            finally
            {
                long beforeCleanup = issueCount; Release(source, null, null);
                if (issueCount != beforeCleanup)
                    for (int i = 0; i < folders.Count; i++) folders[i] = folders[i] with { Complete = false };
            }
            access.Token.ThrowIfCancellationRequested();
            return new(rows, folders, issues, DateTimeOffset.Now, elapsed(), folderAttempts, taskAttempts, retained, omitted, access.StopReason);
        }
        private void ReadFolder(IScheduledTaskSource source, string path, int depth)
        {
            long startingIssues = issueCount;
            IScheduledTaskFolder? folder = null; int? taskCount = null, childCount = null; int attempted = 0, children = 0; bool complete = true;
            try
            {
                access.Checkpoint(); folder = source.OpenFolder(path); access.Checkpoint();
                IScheduledTaskItems? tasks = null;
                try
                {
                    access.Checkpoint(); tasks = folder.GetTasks(); access.Checkpoint();
                    try { taskCount = Count(tasks.Count); }
                    catch (ScheduledTaskLimitException) { throw; }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error) { complete = false; Problem(ScheduledTaskPhase.TaskCount, path, null, error); }
                    for (int index = 1; index <= taskCount; index++)
                    {
                        access.Checkpoint();
                        if (taskAttempts == MaxTasks) { access.Limit(ScheduledTaskStopReason.TaskLimit); throw new ScheduledTaskLimitException(); }
                        int row = ++taskAttempts; attempted++; ReadTask(tasks, index, row, path);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (ScheduledTaskLimitException) { throw; }
                catch (Exception error) { complete = false; Problem(ScheduledTaskPhase.TaskCollection, path, null, error); }
                finally { Release(tasks, path, null); }
                access.Checkpoint();
                if (depth == MaxDepth)
                { complete = false; Problem(ScheduledTaskPhase.Limit, path, null, reason: "Child folders were not inspected at the depth limit."); }
                else if (folderAttempts == MaxFolders)
                {
                    complete = false; access.Limit(ScheduledTaskStopReason.FolderLimit);
                    Problem(ScheduledTaskPhase.Limit, path, null, reason: "Child folders were not inspected at the folder-entry limit.");
                }
                else
                {
                    IScheduledTaskFolders? childFolders = null;
                    try
                    {
                        access.Checkpoint(); childFolders = folder.GetFolders(); access.Checkpoint();
                        try { childCount = Count(childFolders.Count); }
                        catch (ScheduledTaskLimitException) { throw; }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception error) { complete = false; Problem(ScheduledTaskPhase.FolderCount, path, null, error); }
                        for (int index = 1; index <= childCount; index++)
                        {
                            access.Checkpoint();
                            if (folderAttempts == MaxFolders)
                            {
                                complete = false; access.Limit(ScheduledTaskStopReason.FolderLimit);
                                Problem(ScheduledTaskPhase.Limit, path, null, reason: "Child folders were not inspected at the folder-entry limit."); break;
                            }
                            folderAttempts++; children++; IScheduledTaskChild? child = null;
                            try
                            {
                                child = childFolders.At(index); access.Checkpoint();
                                string childPath = Text(child.Path);
                                string prefix = path == "\\" ? path : path + "\\";
                                if (!childPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || childPath.Length <= prefix.Length || childPath.AsSpan(prefix.Length).Contains('\\') || childPath[prefix.Length..] is "." or "..") throw new ScheduledTaskValueException();
                                if (!seenFolders.Add(childPath)) { complete = false; Problem(ScheduledTaskPhase.Validation, childPath, null, reason: "A repeated task-folder path was not inspected again."); }
                                else pending.Enqueue((childPath, depth + 1));
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (ScheduledTaskLimitException) { throw; }
                            catch (Exception error) { complete = false; Problem(child == null ? ScheduledTaskPhase.FolderItem : ScheduledTaskPhase.FolderPath, path, null, error); }
                            finally { Release(child, path, null); }
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (ScheduledTaskLimitException) { throw; }
                    catch (Exception error) { complete = false; Problem(ScheduledTaskPhase.FolderCollection, path, null, error); }
                    finally { Release(childFolders, path, null); }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { complete = false; Problem(error is ScheduledTaskLimitException ? ScheduledTaskPhase.Limit : ScheduledTaskPhase.OpenFolder, path, null, error); }
            finally { Release(folder, path, null); folders.Add(new(path, depth, taskCount, attempted, childCount, children, complete && issueCount == startingIssues)); }
        }
        private int Count(Func<int> get)
        { access.Checkpoint(); int count = get(); access.Checkpoint(); if (count < 0) throw new ScheduledTaskValueException(); return count; }
        private T Scalar<T>(Func<T> get)
        { access.Checkpoint(); T value = get(); access.Checkpoint(); return value; }
        private string Text(Func<string> get)
        { access.Checkpoint(); string value = get(); access.Checkpoint(); return Keep(value); }
        private void ReadTask(IScheduledTaskItems tasks, int index, int row, string path)
        {
            IScheduledTaskItem? task = null; string? name = null, taskPath = null; int? state = null; bool? enabled = null; uint? result = null; IReadOnlyList<string?>? executables = null;
            try
            {
                access.Checkpoint(); task = tasks.At(index); access.Checkpoint();
                foreach (var field in new[] { ScheduledTaskPhase.Name, ScheduledTaskPhase.Path, ScheduledTaskPhase.State, ScheduledTaskPhase.Enabled, ScheduledTaskPhase.LastResult })
                {
                    try
                    {
                        access.Checkpoint();
                        switch (field)
                        {
                            case ScheduledTaskPhase.Name: name = Text(task.Name); break;
                            case ScheduledTaskPhase.Path: taskPath = Text(task.Path); break;
                            case ScheduledTaskPhase.State: state = Scalar(task.State); break;
                            case ScheduledTaskPhase.Enabled: enabled = Scalar(task.Enabled); break;
                            case ScheduledTaskPhase.LastResult: result = Scalar(task.LastResult); break;
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error) { Problem(field, path, row, error); }
                }
                if (includeActions)
                {
                    try
                    {
                        access.Checkpoint(); var paths = task.ExecutablePaths(); access.Checkpoint();
                        if (paths.Count > 32) throw new ScheduledTaskValueException();
                        executables = Array.AsReadOnly(paths.Select(p => p is null ? null : Keep(p)).ToArray());
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error) { Problem(ScheduledTaskPhase.Actions, path, row, error); }
                }
                if (taskPath != null && !seenTasks.Add(taskPath)) Problem(ScheduledTaskPhase.Validation, path, row, reason: "A repeated task path is retained as a separate historical row.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { Problem(ScheduledTaskPhase.TaskItem, path, row, error); }
            finally { Release(task, path, row); rows.Add(new(row, name, taskPath, state, enabled, result) { Executables = executables }); }
        }
    }
}
