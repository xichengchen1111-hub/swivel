using System;
using System.Reflection;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Swivel.Core;

namespace Swivel.Windows;

internal static class ScheduledTaskReader
{
    internal static Task<ScheduledTaskSnapshot> ReadAsync(CancellationToken token)
        => ReadAsync(token, new ScheduledTaskAutomation());
    internal static Task<ScheduledTaskSnapshot> ReadAsync(CancellationToken token, IScheduledTaskAutomation automation, bool includeActions = false, Func<TimeSpan>? elapsed = null)
    {
        if (token.IsCancellationRequested) return Task.FromCanceled<ScheduledTaskSnapshot>(token);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ScheduledTaskSnapshot? result = null; Exception? failure = null;
        var worker = new Thread(() =>
        {
            try { var watch = Stopwatch.StartNew(); result = ScheduledTasks.ScanWithClock(access => new Source(automation, access), elapsed ?? (() => watch.Elapsed), token, includeActions); }
            catch (Exception error) { failure = error; }
            finally { ended.TrySetResult(); }
        }) { IsBackground = true, Name = "Swivel scheduled-task metadata" };
        automation.ConfigureThread(worker);
        worker.Start();
        return Join();
        async Task<ScheduledTaskSnapshot> Join()
        {
            await ended.Task.ConfigureAwait(false);
            worker.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            token.ThrowIfCancellationRequested();
            return result ?? throw new InvalidOperationException("The scheduled-task read did not produce a result.");
        }
    }
    private sealed class Source : IScheduledTaskSource
    {
        private readonly IScheduledTaskAutomation automation;
        private readonly ScheduledTaskAccess access;
        private Owned? service;
        private bool initialized, disposed;
        internal Source(IScheduledTaskAutomation automation, ScheduledTaskAccess access)
        {
            this.automation = automation; this.access = access;
            try
            {
                access.Checkpoint(); int init = automation.InitializeMta(); initialized = init >= 0;
                if (init < 0) throw new COMException("Task Scheduler COM initialization failed.", init);
                access.Checkpoint();
                service = Call(null, null, false, Array.Empty<object?>(), null); service.RequireCom();
                // Four null object arguments marshal to VT_EMPTY, not VT_NULL or missing parameters.
                using var connected = Call(service, "Connect", false, new object?[] { null, null, null, null }, null);
                if (connected.Value != null) throw new ScheduledTaskValueException();
            }
            catch { Dispose(); throw; }
        }
        public IScheduledTaskFolder OpenFolder(string path)
        {
            Owned? owned = Call(service!, "GetFolder", false, new object?[] { path }, path);
            try { owned.RequireCom(); var folder = new Folder(this, owned, path); owned = null; return folder; }
            finally { owned?.Dispose(); }
        }
        private Owned Call(Owned? target, string? member, bool property, object?[] arguments, string? path)
        {
            access.Checkpoint();
            object? value;
            try { value = member == null ? automation.Create() : automation.Invoke(target!.RequireCom(), member, property, arguments); }
            catch (TargetInvocationException error) when (error.InnerException != null)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
            // Own the result before post-call cancellation, even for a scalar getter which
            // unexpectedly returns a COM interface. Each acquisition gets exactly one release.
            var owned = new Owned(automation, value, issue => access.Report(issue), path);
            try { access.Checkpoint(); return owned; }
            catch { owned.Dispose(); throw; }
        }
        private T Scalar<T>(Owned target, string member, string? path)
        {
            using var value = Call(target, member, true, Array.Empty<object?>(), path);
            return value.Value is T typed ? typed : throw new ScheduledTaskValueException();
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            try { service?.Dispose(); }
            finally
            {
                service = null;
                if (initialized)
                {
                    initialized = false;
                    try { automation.Uninitialize(); }
                    catch (Exception error) { access.Report(new(null, null, ScheduledTaskPhase.Cleanup, unchecked((uint)error.HResult), "Task Scheduler COM initialization could not be released.")); }
                }
            }
        }
        private sealed class Folder(Source source, Owned owned, string path) : IScheduledTaskFolder
        {
            public IScheduledTaskItems GetTasks()
            {
                Owned? value = source.Call(owned, "GetTasks", false, new object?[] { 0 }, path);
                try { value.RequireCom(); var items = new Items(source, value, path); value = null; return items; }
                finally { value?.Dispose(); }
            }
            public IScheduledTaskFolders GetFolders()
            {
                Owned? value = source.Call(owned, "GetFolders", false, new object?[] { 0 }, path);
                try { value.RequireCom(); var folders = new Children(source, value, path); value = null; return folders; }
                finally { value?.Dispose(); }
            }
            public void Dispose() => owned.Dispose();
        }
        private sealed class Items(Source source, Owned owned, string path) : IScheduledTaskItems
        {
            public int Count() => source.Scalar<int>(owned, "Count", path);
            public IScheduledTaskItem At(int index)
            {
                Owned? value = source.Call(owned, "Item", true, new object?[] { index }, path);
                try { value.RequireCom(); var item = new Item(source, value, path); value = null; return item; }
                finally { value?.Dispose(); }
            }
            public void Dispose() => owned.Dispose();
        }
        private sealed class Children(Source source, Owned owned, string path) : IScheduledTaskFolders
        {
            public int Count() => source.Scalar<int>(owned, "Count", path);
            public IScheduledTaskChild At(int index)
            {
                Owned? value = source.Call(owned, "Item", true, new object?[] { index }, path);
                try { value.RequireCom(); var item = new Child(source, value, path); value = null; return item; }
                finally { value?.Dispose(); }
            }
            public void Dispose() => owned.Dispose();
        }
        private sealed class Child(Source source, Owned owned, string path) : IScheduledTaskChild
        {
            public string Path() => source.Scalar<string>(owned, "Path", path);
            public void Dispose() => owned.Dispose();
        }
        private sealed class Item(Source source, Owned owned, string path) : IScheduledTaskItem
        {
            public string Name() => source.Scalar<string>(owned, "Name", path);
            public string Path() => source.Scalar<string>(owned, "Path", path);
            public int State() => source.Scalar<int>(owned, "State", path);
            public bool Enabled() => source.Scalar<bool>(owned, "Enabled", path);
            public uint LastResult() => unchecked((uint)source.Scalar<int>(owned, "LastTaskResult", path));
            public IReadOnlyList<string?> ExecutablePaths()
            {
                using var definition = source.Call(owned, "Definition", true, Array.Empty<object?>(), path);
                using var actions = source.Call(definition, "Actions", true, Array.Empty<object?>(), path);
                int count = source.Scalar<int>(actions, "Count", path);
                if (count is < 0 or > 32) throw new ScheduledTaskValueException();
                var paths = new List<string?>();
                for (int i = 1; i <= count; i++)
                {
                    using var action = source.Call(actions, "Item", true, new object?[] { i }, path);
                    int type = source.Scalar<int>(action, "Type", path);
                    // Non-executable actions have no executable path; never inspect script/COM payloads.
                    string? target = type == 0 ? source.Scalar<string>(action, "Path", path) : null;
                    if (target?.Length > ScheduledTasks.MaxValueUnits) throw new ScheduledTaskValueException();
                    paths.Add(target);
                }
                return paths.AsReadOnly();
            }
            public void Dispose() => owned.Dispose();
        }
    }
    private sealed class Owned(IScheduledTaskAutomation automation, object? value, Action<ScheduledTaskIssue> report, string? path) : IDisposable
    {
        private object? value = value;
        internal object? Value => value;
        internal object RequireCom() => value != null && automation.IsCom(value) ? value : throw new ScheduledTaskValueException();
        public void Dispose()
        {
            var acquired = Interlocked.Exchange(ref value, null);
            if (acquired == null) return;
            try { if (automation.IsCom(acquired)) _ = automation.Release(acquired); }
            catch (Exception error) { report(new(null, path, ScheduledTaskPhase.Cleanup, unchecked((uint)error.HResult), ScheduledTasks.CleanupFailed)); }
        }
    }
}

internal interface IScheduledTaskAutomation
{
    void ConfigureThread(Thread thread);
    int InitializeMta();
    void Uninitialize();
    object? Create();
    object? Invoke(object target, string member, bool property, object?[] arguments);
    bool IsCom(object value);
    int Release(object value);
}
internal sealed class ScheduledTaskAutomation : IScheduledTaskAutomation
{
    internal static readonly Guid ClassId = new("0F87369F-A4E5-4CFC-BD3E-73E6154572DD");
    public void ConfigureThread(Thread thread) => thread.SetApartmentState(ApartmentState.MTA);
    public int InitializeMta() => CoInitializeEx(IntPtr.Zero, 0);
    public void Uninitialize() => CoUninitialize();
    public object? Create() => Activator.CreateInstance(Type.GetTypeFromCLSID(ClassId, throwOnError: true)!);
    public object? Invoke(object target, string member, bool property, object?[] arguments)
        => target.GetType().InvokeMember(member, BindingFlags.Public | BindingFlags.Instance | (property ? BindingFlags.GetProperty : BindingFlags.InvokeMethod), null, target, arguments, System.Globalization.CultureInfo.InvariantCulture);
    public bool IsCom(object value) => Marshal.IsComObject(value);
    public int Release(object value) => Marshal.ReleaseComObject(value);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr reserved, uint mode);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();
}
