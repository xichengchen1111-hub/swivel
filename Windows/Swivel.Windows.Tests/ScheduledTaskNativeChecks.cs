using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Swivel.Core;
using Swivel.Windows;

internal static class ScheduledTaskNativeChecks
{
    internal static void Run(Action<bool, string> check, Action<Action, string> reject) => RunAsync(check, reject).GetAwaiter().GetResult();
    private static async Task RunAsync(Action<bool, string> check, Action<Action, string> reject)
    {
        var native = new Fake(); var report = await ScheduledTaskReader.ReadAsync(default, native);
        check(!report.Partial && report.Tasks.Count == 1 && report.Tasks[0].LastResult == uint.MaxValue && report.Tasks[0].State == -7 && report.Tasks[0].Enabled == false, "Actual reader preserves five fixed scalar values and raw signed 32-bit patterns");
        check(native.Thread is { IsAlive: false } && native.Initializes == 1 && native.Uninitializes == 1 && native.Balanced, "Reader task completes after all acquisitions release, COM uninitializes and worker actually joins");
        check(native.ThreadIds.Count == 1 && native.ThreadIds.Single() == native.Thread!.ManagedThreadId, "Every acquisition, invocation and release uses the one dedicated worker");
        check(native.Calls.Any(x => x == "Connect:False:4") && native.ConnectWasEmpty && native.FlagsWereZero && native.ItemWasOneBased, "Connect uses four empty object arguments; collection flags are zero and Item is one-based");
        check(native.Calls.Select(x => x.Split(':')[0]).All(x => new[] { "Connect", "GetFolder", "GetTasks", "GetFolders", "Count", "Item", "Name", "Path", "State", "Enabled", "LastTaskResult" }.Contains(x)), "Actual late-bound reader never requests definitions, actions, scripts, XML or ownership");
        check(ScheduledTaskAutomation.ClassId == new Guid("0F87369F-A4E5-4CFC-BD3E-73E6154572DD"), "Task Scheduler 2.0 system class ID is explicit");
        foreach (var method in typeof(ScheduledTaskAutomation).GetMethods(BindingFlags.Static | BindingFlags.NonPublic).Where(x => x.GetCustomAttribute<DllImportAttribute>() != null))
            check(method.GetCustomAttribute<DllImportAttribute>()!.Value == "ole32.dll" && method.GetCustomAttribute<DefaultDllImportSearchPathsAttribute>()?.Paths == DllImportSearchPath.System32, "COM apartment imports are System32 restricted");
        int baselineCalls = native.Returns;
        for (int cancelAt = 1; cancelAt <= baselineCalls; cancelAt++)
        {
            using var cancellation = new CancellationTokenSource(); int selected = cancelAt;
            var interrupted = new Fake { AfterReturn = (count, _) => { if (count == selected) cancellation.Cancel(); } };
            reject(() => ScheduledTaskReader.ReadAsync(cancellation.Token, interrupted).GetAwaiter().GetResult(), "Cancellation after native acquisition or scalar return propagates");
            check(interrupted.Balanced && interrupted.Initializes == interrupted.Uninitializes && interrupted.Thread is { IsAlive: false }, "Returned COM object is owned before post-call cancellation, then every resource joins");
        }
        native = new Fake();
        reject(() => ScheduledTaskReader.ReadAsync(new CancellationToken(true), native).GetAwaiter().GetResult(), "Pre-cancellation refuses native worker creation");
        check(native.Thread == null && native.Initializes == 0, "Pre-cancellation does not configure a thread or call COM");
        native = new Fake { InitResult = 1 }; report = await ScheduledTaskReader.ReadAsync(default, native);
        check(native.Uninitializes == 1 && !report.Partial, "S_FALSE apartment initialization still pairs with CoUninitialize");
        native = new Fake { InitResult = unchecked((int)0x80010106) }; report = await ScheduledTaskReader.ReadAsync(default, native);
        check(native.Uninitializes == 0 && native.Acquisitions == 0 && report.Partial && report.Issues.Single().ErrorCode == 0x80010106u, "Failed COM initialization never uninitializes an unowned apartment");
        foreach (string phase in new[] { "Create", "Connect", "GetFolder", "GetTasks", "Count", "Item", "Name", "Path", "State", "Enabled", "LastTaskResult", "GetFolders" })
        {
            native = new Fake { FailMember = phase }; report = await ScheduledTaskReader.ReadAsync(default, native);
            check(report.Partial && native.Balanced && native.Uninitializes == 1 && native.Thread is { IsAlive: false }, "Failure at each COM phase releases every earlier acquisition: " + phase);
            check(report.Issues.All(x => !x.Reason.Contains("SECRET") && (!x.ErrorCode.HasValue || x.ErrorCode == 0x80070005u)), "COM messages containing private namespaces are not retained; HRESULT survives unwrapping");
        }
        foreach (string member in new[] { "Connect", "Count", "Name", "Path", "State", "Enabled", "LastTaskResult" })
        {
            native = new Fake { ReturnComFor = member }; report = await ScheduledTaskReader.ReadAsync(default, native);
            check(report.Partial && native.Balanced && native.PeakAliasCount >= 2 && native.Uninitializes == 1, "Unexpected COM return in a scalar/void slot is acquired and released separately from the same live RCW: " + member);
        }
        foreach (var entry in new (string Member, object? Value)[] { ("Name", null), ("Path", 1), ("State", "Ready"), ("Enabled", 1), ("LastTaskResult", 0L), ("Count", -1) })
        {
            native = new Fake { ReplaceMember = entry.Member, Replacement = entry.Value }; report = await ScheduledTaskReader.ReadAsync(default, native);
            check(report.Partial && native.Balanced && report.Issues.Any(x => x.Reason == ScheduledTasks.InvalidValue), "Wrong native VARIANT type is missing with a diagnostic: " + entry.Member);
        }
        native = new Fake { RepeatedTasks = 3 }; report = await ScheduledTaskReader.ReadAsync(default, native);
        check(report.Tasks.Count == 3 && native.Balanced && native.ItemAcquisitions == 3 && report.Partial, "Repeated collection Item returns release once per acquisition even when aliases refer to the same RCW");
        native = new Fake { ReleaseFails = true, UninitializeFails = true }; report = await ScheduledTaskReader.ReadAsync(default, native);
        check(report.Partial && native.ReleaseCalls == native.Acquisitions && native.Uninitializes == 1 && report.Folders.All(x => !x.Complete), "Cleanup failures do not prevent later reference releases or COM uninitialization");
        check(report.Issues.Count(x => x.Phase == ScheduledTaskPhase.Cleanup) == native.ReleaseCalls + 1 && report.Issues.All(x => !x.Reason.Contains("SECRET")), "All bounded cleanup failures remain visible with fixed non-private diagnostics");
        using (var entered = new ManualResetEventSlim()) using (var release = new ManualResetEventSlim()) using (var cancellation = new CancellationTokenSource())
        {
            native = new Fake { BeforeInvoke = member => { if (member == "Name") { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); } } };
            var read = ScheduledTaskReader.ReadAsync(cancellation.Token, native);
            try
            {
                check(entered.Wait(TimeSpan.FromSeconds(5)), "Blocked COM call entered on its worker"); cancellation.Cancel();
                check(!read.IsCompleted && native.Thread is { IsAlive: true }, "Cancellation does not pretend an outstanding COM call has returned or terminate its worker");
            }
            finally { release.Set(); }
            try { await read; check(false, "Blocked read must observe cancellation after returning"); } catch (OperationCanceledException) { check(true, "Blocked read observes cancellation once COM returns"); }
            check(native.Balanced && native.Thread is { IsAlive: false } && native.Uninitializes == 1, "Cancelled blocked read finally releases all resources and joins");
        }
        using (var entered = new ManualResetEventSlim()) using (var release = new ManualResetEventSlim())
        {
            native = new Fake { DuringUninitialize = () => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); } };
            var read = ScheduledTaskReader.ReadAsync(default, native);
            try { check(entered.Wait(TimeSpan.FromSeconds(5)) && !read.IsCompleted, "Completion waits for final apartment cleanup"); }
            finally { release.Set(); }
            await read; check(native.Thread is { IsAlive: false } && native.Balanced, "Completion after cleanup also waits for true Thread.Join");
        }
    }
    private sealed class Com(string kind) { internal readonly string Kind = kind; internal int Refs; }
    private sealed class Fake : IScheduledTaskAutomation
    {
        internal Thread? Thread; internal readonly HashSet<int> ThreadIds = new(); internal readonly List<string> Calls = new();
        private readonly Com service = new("service"), folder = new("folder"), tasks = new("tasks"), folders = new("folders"), task = new("task");
        internal int Initializes, Uninitializes, Acquisitions, ReleaseCalls, Returns, PeakAliasCount, ItemAcquisitions, InitResult, RepeatedTasks = 1;
        internal bool ConnectWasEmpty, FlagsWereZero = true, ItemWasOneBased = true, ReleaseFails, UninitializeFails;
        internal string? FailMember, ReturnComFor, ReplaceMember; internal object? Replacement;
        internal Action<int, object?>? AfterReturn; internal Action<string>? BeforeInvoke; internal Action? DuringUninitialize;
        internal bool Balanced => new[] { service, folder, tasks, folders, task }.All(x => x.Refs == 0) && Acquisitions == ReleaseCalls;
        private void Here() => ThreadIds.Add(Environment.CurrentManagedThreadId);
        public void ConfigureThread(Thread thread) => Thread = thread;
        public int InitializeMta() { Here(); Initializes++; return InitResult; }
        public void Uninitialize() { Here(); Uninitializes++; DuringUninitialize?.Invoke(); if (UninitializeFails) throw Error(); }
        public object? Create() { Here(); if (FailMember == "Create") throw Error(); return Returned(service); }
        public object? Invoke(object target, string member, bool property, object?[] arguments)
        {
            Here(); var com = (Com)target; if (com.Refs < 1) throw new InvalidOperationException("Using released COM acquisition");
            Calls.Add(member + ":" + property + ":" + arguments.Length); BeforeInvoke?.Invoke(member);
            if (FailMember == member) throw new TargetInvocationException(Error());
            if (member == "Connect") ConnectWasEmpty = !property && arguments.Length == 4 && arguments.All(x => x == null);
            if (member is "GetTasks" or "GetFolders") FlagsWereZero &= !property && arguments.Length == 1 && arguments[0] is int i && i == 0;
            if (member == "Item") { ItemWasOneBased &= property && arguments.Length == 1 && arguments[0] is int i && i >= 1; ItemAcquisitions++; }
            if (ReturnComFor == member) return Returned(target);
            if (ReplaceMember == member) return Returned(Replacement);
            object? result = member switch
            {
                "Connect" => null, "GetFolder" => folder, "GetTasks" => tasks, "GetFolders" => folders,
                "Count" => com.Kind == "tasks" ? RepeatedTasks : 0, "Item" => task,
                "Name" => "Task", "Path" => @"\Task", "State" => -7, "Enabled" => false, "LastTaskResult" => -1,
                _ => throw new InvalidOperationException("Unexpected member")
            };
            return Returned(result);
        }
        private object? Returned(object? value)
        {
            if (value is Com com) { com.Refs++; Acquisitions++; PeakAliasCount = Math.Max(PeakAliasCount, com.Refs); }
            Returns++; AfterReturn?.Invoke(Returns, value); return value;
        }
        public bool IsCom(object value) => value is Com;
        public int Release(object value)
        {
            Here(); var com = (Com)value; if (com.Refs < 1) throw new InvalidOperationException("Double COM release");
            ReleaseCalls++; int remaining = --com.Refs; if (ReleaseFails) throw Error(); return remaining;
        }
        private static Exception Error() => new COMException("SECRET private namespace returned by COM", unchecked((int)0x80070005));
    }
}
