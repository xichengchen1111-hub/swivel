using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Swivel.Core;

internal static class ScheduledTasksChecks
{
    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        var sample = new Tree(); sample.Root.Tasks.Add(new TaskData { Name = "Private task", Path = @"\Private task", State = -9, Enabled = false, Result = uint.MaxValue });
        var report = sample.Scan();
        check(!report.Partial && report.Folders.Single().Complete && report.Tasks.Single() == new ScheduledTaskRow(1, "Private task", @"\Private task", -9, false, uint.MaxValue), "Five raw fields survive without inventing task health or ownership");
        check(report.TaskAttempts == 1 && report.FolderAttempts == 1 && report.RetainedTextUnits == 26 && sample.Balanced, "One root and one task are charged and all handles disposed");
        check(ScheduledTasks.State(-9) == "Unknown task state value" && ScheduledTasks.State(0) == "Unknown (reported)", "Unknown raw states have explicit labels");
        reject(() => ((IList<ScheduledTaskRow>)report.Tasks).Clear(), "Task snapshot is readonly");
        reject(() => ((IList<ScheduledTaskFolder>)report.Folders).Clear(), "Folder snapshot is readonly");
        reject(() => ((IList<ScheduledTaskIssue>)report.Issues).Clear(), "Issue snapshot is readonly");
        foreach (string phase in new[] { "Item", "Name", "Path", "State", "Enabled", "LastResult" })
        {
            var tree = new Tree(); tree.Root.Tasks.Add(new TaskData { Failure = phase }); var result = tree.Scan();
            check(result.Partial && !result.Folders.Single().Complete && result.TaskAttempts == 1 && result.Tasks.Count == 1, "Failed task or field leaves a partial folder and attempted historical row: " + phase);
            var row = result.Tasks.Single();
            check(phase != "Item" || row == new ScheduledTaskRow(1, null, null, null, null, null), "Failed Item never becomes an absent row or false/zero data");
            check(phase != "Enabled" || row.Enabled == null, "Unavailable Enabled is not false");
            check(phase != "LastResult" || row.LastResult == null, "Unavailable last result is not zero");
            check(result.Issues.All(x => !x.Reason.Contains("SECRET") && x.ErrorCode == 0x80070005u) && tree.Balanced, "Failure has fixed reason and raw HRESULT, with deterministic disposal");
        }
        foreach (string phase in new[] { "Open", "Tasks", "TaskCount", "Folders", "FolderCount", "DisposeFolder", "DisposeTasks", "DisposeFolders", "DisposeSource", "DisposeTask" })
        {
            var tree = new Tree { Failure = phase }; tree.Root.Tasks.Add(new TaskData()); var result = tree.Scan();
            check(result.Partial && !result.Folders.Single().Complete && tree.Balanced, "Connection/collection/cleanup error prevents a complete folder: " + phase);
        }
        var failedTasks = new Tree(); failedTasks.Root.ReportedTasks = int.MaxValue; failedTasks.Root.FailedItems = true;
        report = failedTasks.Scan();
        check(report.TaskAttempts == 1024 && report.Tasks.Count == 1024 && report.StopReason == ScheduledTaskStopReason.TaskLimit && report.Issues.Count == 256 && report.OmittedIssues > 0 && failedTasks.Balanced, "Failed items consume shared task and diagnostic budgets");
        check(report.Tasks.All(x => x.Name == null && x.Path == null && x.Enabled == null && x.State == null && x.LastResult == null), "No failed task is reported as disabled or successful");
        var failedFolders = new Tree(); failedFolders.Root.ReportedFolders = int.MaxValue; failedFolders.Root.FailedChildren = true;
        report = failedFolders.Scan();
        check(report.FolderAttempts == 128 && report.Folders.Count == 1 && report.Folders[0].AttemptedChildren == 127 && report.StopReason == ScheduledTaskStopReason.FolderLimit && failedFolders.Balanced, "Failed child Item calls consume the global folder budget");
        var wide = new Tree();
        for (int i = 0; i < 160; i++) { var child = wide.Add(@"\" + i); child.Tasks.Add(new TaskData { Path = child.Path + "\\Task" }); }
        report = wide.Scan();
        check(report.Folders.Count == 128 && report.TaskAttempts == 127 && report.FolderAttempts == 128 && wide.Opened.Count == 128 && wide.Balanced, "Already queued folders still have their tasks inspected after folder discovery budget is reached");
        var deep = new Tree(); string current = "\\";
        for (int i = 0; i < 8; i++) { var child = deep.Add((current == "\\" ? current : current + "\\") + i); current = child.Path; }
        report = deep.Scan();
        check(report.Folders.Select(x => x.Depth).SequenceEqual(new[] { 0, 1, 2, 3, 4 }) && report.Partial && deep.Opened.Count == 5 && deep.Balanced, "Only root plus four child levels are visited");
        var bfs = new Tree(); bfs.Add(@"\a"); bfs.Add(@"\b"); bfs.Add(@"\a\c"); bfs.Add(@"\b\d"); bfs.Scan();
        check(bfs.Opened.SequenceEqual(new[] { "\\", @"\a", @"\b", @"\a\c", @"\b\d" }), "Traversal is breadth first");
        var invalid = new Tree(); invalid.Add(@"\a"); invalid.Root.Children.Add(@"\A"); invalid.Root.Children.Add(@"\a\child"); invalid.Root.Children.Add(@"\.."); invalid.Root.Children.Add(@"other");
        report = invalid.Scan();
        check(report.FolderAttempts == 6 && report.Folders.Count == 2 && report.Partial && invalid.Balanced, "Duplicate and malformed child paths charge attempts but are not traversed");
        var repeated = new Tree(); repeated.Root.Tasks.Add(new TaskData()); repeated.Root.Tasks.Add(new TaskData()); report = repeated.Scan();
        check(report.Tasks.Count == 2 && report.Partial && !report.Folders.Single().Complete, "Concurrent repeated task paths remain separate historical rows with an issue");
        foreach (string value in new[] { "", "a\0b", new string('x', 1025) })
        {
            var invalidText = new Tree(); invalidText.Root.Tasks.Add(new TaskData { Name = value }); report = invalidText.Scan();
            check(report.Tasks.Single().Name == null && report.Tasks.Single().Path != null && report.Partial, "Invalid or overlong text is missing with a reason, never silently truncated");
        }
        var text = new Tree();
        for (int i = 0; i < 300; i++) text.Root.Tasks.Add(new TaskData { Name = new string('n', 1024), Path = "\\" + i.ToString("D4") + new string('p', 1019) });
        report = text.Scan();
        check(report.StopReason == ScheduledTaskStopReason.TextLimit && report.RetainedTextUnits == 261121 && report.TaskAttempts == 128 && report.Tasks[^1].Name != null && report.Tasks[^1].Path == null && text.Balanced, "Whole shared text budget refuses an over-limit field without truncating it");
        foreach (string field in new[] { "Name", "Path", "State", "Enabled", "LastResult" })
        {
            var timed = new Tree(); TimeSpan elapsed = TimeSpan.Zero;
            timed.Root.Tasks.Add(new TaskData { AfterRead = p => { if (p == field) elapsed = TimeSpan.FromSeconds(15); } });
            report = ScheduledTasks.ScanWithClock(_ => timed.Open(), () => elapsed, default); var row = report.Tasks.Single();
            check(report.StopReason == ScheduledTaskStopReason.TimeLimit && report.Partial && timed.Balanced, "Deadline after a returned field stops and cleans up: " + field);
            check(field switch { "Name" => row.Name == null, "Path" => row.Path == null, "State" => row.State == null, "Enabled" => row.Enabled == null, _ => row.LastResult == null }, "A post-deadline field is not retained while reported unread");
        }
        var notStarted = new Tree();
        reject(() => ScheduledTasks.Scan(_ => notStarted.Open(), new CancellationToken(true)), "Pre-cancelled scan does not create its source");
        check(notStarted.Created == 0, "Pre-cancellation performs no source read");
        foreach (string field in new[] { "Name", "Path", "State", "Enabled", "LastResult" })
        {
            using var cancelled = new CancellationTokenSource(); var tree = new Tree();
            tree.Root.Tasks.Add(new TaskData { AfterRead = p => { if (p == field) cancelled.Cancel(); } });
            reject(() => ScheduledTasks.Scan(_ => tree.Open(), cancelled.Token), "Cancellation after a field propagates");
            check(tree.Balanced, "Cancellation still disposes task, collections, folder and source");
        }
        var badCount = new Tree(); badCount.Root.ReportedTasks = -1; report = badCount.Scan();
        check(report.Tasks.Count == 0 && report.Folders.Single().ReportedTasks == null && report.Partial, "A negative task count is unavailable rather than an invented empty success");
        var denied = ScheduledTasks.Scan(_ => throw Error(), default);
        check(denied.Partial && denied.Folders.Count == 0 && denied.TaskAttempts == 0 && denied.Issues.Single().Phase == ScheduledTaskPhase.OpenService, "Connection failure remains visible even with no rows");
    }
    private static Exception Error() => new COMException("SECRET namespace path should never appear in the reason", unchecked((int)0x80070005));
    private sealed class TaskData
    {
        internal string Name = "Task", Path = @"\Task"; internal int State = 3; internal bool Enabled = true; internal uint Result = 0;
        internal string? Failure; internal Action<string>? AfterRead;
        internal T Read<T>(string phase, T value) { if (Failure == phase) throw Error(); AfterRead?.Invoke(phase); return value; }
    }
    private sealed class FolderData(string path)
    {
        internal readonly string Path = path; internal readonly List<TaskData> Tasks = new(); internal readonly List<string> Children = new();
        internal int? ReportedTasks, ReportedFolders; internal bool FailedItems, FailedChildren;
    }
    private sealed class Tree
    {
        internal readonly FolderData Root = new("\\"); internal readonly Dictionary<string, FolderData> Folders = new(StringComparer.OrdinalIgnoreCase);
        internal readonly List<string> Opened = new(); internal int Created, Disposed; internal string? Failure;
        internal bool Balanced => Created == Disposed;
        internal Tree() { Folders.Add("\\", Root); }
        internal FolderData Add(string path)
        { var folder = new FolderData(path); Folders.Add(path, folder); int slash = path.LastIndexOf('\\'); Folders[slash == 0 ? "\\" : path[..slash]].Children.Add(path); return folder; }
        internal IScheduledTaskSource Open() => new Source(this);
        internal ScheduledTaskSnapshot Scan() => ScheduledTasks.ScanWithClock(_ => Open(), () => TimeSpan.Zero, default);
        internal void Fail(string phase) { if (Failure == phase) throw Error(); }
        private abstract class Lease : IDisposable
        {
            protected readonly Tree tree; private readonly string kind; private bool disposed;
            protected Lease(Tree tree, string kind) { this.tree = tree; this.kind = kind; tree.Created++; }
            public void Dispose() { if (disposed) throw new InvalidOperationException("Repeated dispose"); disposed = true; tree.Disposed++; tree.Fail("Dispose" + kind); }
        }
        private sealed class Source(Tree tree) : Lease(tree, "Source"), IScheduledTaskSource
        { public IScheduledTaskFolder OpenFolder(string path) { tree.Fail("Open"); tree.Opened.Add(path); return new Folder(tree, tree.Folders[path]); } }
        private sealed class Folder(Tree tree, FolderData data) : Lease(tree, "Folder"), IScheduledTaskFolder
        {
            public IScheduledTaskItems GetTasks() { tree.Fail("Tasks"); return new Items(tree, data); }
            public IScheduledTaskFolders GetFolders() { tree.Fail("Folders"); return new Children(tree, data); }
        }
        private sealed class Items(Tree tree, FolderData data) : Lease(tree, "Tasks"), IScheduledTaskItems
        {
            public int Count() { tree.Fail("TaskCount"); return data.ReportedTasks ?? data.Tasks.Count; }
            public IScheduledTaskItem At(int index) { if (data.FailedItems || data.Tasks[index - 1].Failure == "Item") throw Error(); return new Item(tree, data.Tasks[index - 1]); }
        }
        private sealed class Children(Tree tree, FolderData data) : Lease(tree, "Folders"), IScheduledTaskFolders
        {
            public int Count() { tree.Fail("FolderCount"); return data.ReportedFolders ?? data.Children.Count; }
            public IScheduledTaskChild At(int index) { if (data.FailedChildren) throw Error(); return new Child(tree, data.Children[index - 1]); }
        }
        private sealed class Child(Tree tree, string path) : Lease(tree, "Child"), IScheduledTaskChild { public string Path() => path; }
        private sealed class Item(Tree tree, TaskData data) : Lease(tree, "Task"), IScheduledTaskItem
        {
            public string Name() => data.Read("Name", data.Name); public string Path() => data.Read("Path", data.Path);
            public int State() => data.Read("State", data.State); public bool Enabled() => data.Read("Enabled", data.Enabled); public uint LastResult() => data.Read("LastResult", data.Result);
        }
    }
}
