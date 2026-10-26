using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Swivel.Core;

namespace Swivel.Windows;

/// What a Doctor results window moves to the Recycle Bin: how each item is named in messages
/// and in the confirmation, and how it is moved (returning where it went) and put back.
internal sealed record RecycleBinItems<T>(
    Func<T, string> Name,
    Func<T, string> ConfirmLine,
    Func<T, IntPtr, string> Recycle,
    Action<T, string> Restore,
    string AskOne, string AskMany, string Warning);

/// Move Selected to Recycle Bin… and Undo Move for a Doctor results window: a confirmation
/// listing full paths, one STA worker per batch (the shell's Restore needs one), Undo of the
/// last batch while the window is open, and a window that waits for a running batch before
/// it closes (Quit or turning the feature off closes it once the batch ends).
internal sealed class RecycleBinSession<T> where T : notnull
{
    private readonly App app;
    private readonly Window window;
    private readonly RecycleBinItems<T> items;
    private readonly Func<IReadOnlyList<T>> selected;
    private readonly Action changed;
    private List<(T Item, string Stored)> last = new();
    private bool busy, closeRequested;

    public Button Move { get; }
    public Button Undo { get; }
    public TextBlock Status { get; }
    /// Items gone from their place in this window; Removed are those that did not reach the
    /// Recycle Bin (or could not be found there).
    public HashSet<T> Gone { get; } = new();
    public HashSet<T> Removed { get; } = new();
    public int Recycled => Gone.Count - Removed.Count;
    public bool Idle => !busy && !closeRequested;
    /// The items of the batch being moved (or last moved), for checks that must not rely on them.
    public IReadOnlyList<T> Batch { get; private set; } = Array.Empty<T>();

    public RecycleBinSession(App app, Window window, RecycleBinItems<T> items, Func<IReadOnlyList<T>> selected, Action changed)
    {
        this.app = app; this.window = window; this.items = items; this.selected = selected; this.changed = changed;
        Move = UI.Button(app.L("Move Selected to Recycle Bin…"), () => _ = RecycleAsync());
        Undo = UI.Button(app.L("Undo Move"), () => _ = UndoAsync());
        Status = UI.Text("", 13); AutomationProperties.SetName(Status, app.L("Recycle Bin"));
        window.Closing += (_, e) => { if (busy) { closeRequested = true; e.Cancel = true; } };
        UpdateButtons();
    }

    public void UpdateButtons()
    {
        Move.IsEnabled = Idle && selected().Count > 0;
        Undo.IsEnabled = Idle && last.Count > 0;
    }

    /// The report lines for what this window moved.
    public void AppendReport(StringBuilder text, IEnumerable<T> inOrder)
    {
        var order = inOrder.ToList();
        if (Recycled > 0)
        {
            text.AppendLine(string.Format(app.L("Moved to the Recycle Bin in this window: {0}"), Recycled));
            foreach (var item in order.Where(item => Gone.Contains(item) && !Removed.Contains(item))) text.AppendLine("  " + items.Name(item));
        }
        if (Removed.Count > 0)
        {
            text.AppendLine(string.Format(app.L("Removed without the Recycle Bin, or not found there, in this window: {0}"), Removed.Count));
            foreach (var item in order.Where(Removed.Contains)) text.AppendLine("  " + items.Name(item));
        }
    }

    private async Task RecycleAsync()
    {
        var chosen = selected().ToArray();
        if (!Idle || chosen.Length == 0) return;
        var list = new StringBuilder();
        foreach (var item in chosen.Take(20)) list.AppendLine(items.ConfirmLine(item));
        if (chosen.Length > 20) list.AppendLine(string.Format(app.L("and {0} more"), chosen.Length - 20));
        string question = (chosen.Length == 1 ? app.L(items.AskOne) : string.Format(app.L(items.AskMany), chosen.Length)) + "\n\n" + list + "\n" + app.L(items.Warning);
        if (MessageBox.Show(window, question, app.L("Move Selected to Recycle Bin…"), MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK
            || !Idle || !window.IsVisible) return;
        Batch = chosen;
        var done = new List<(T Item, string Stored)>();
        var gone = new List<T>(); // removed, or moved where Undo cannot find it
        var problems = new List<string>();
        var owner = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        string? failure = await Work(() =>
        {
            foreach (var item in chosen)
            {
                string? problem = null;
                try { done.Add((item, items.Recycle(item, owner))); }
                catch (UserError error)
                {
                    if (error.Message is NativeFiles.RecycledUntracked or NativeFiles.RemovedNotRecycled) gone.Add(item);
                    problem = error.Message;
                }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or System.IO.IOException or UnauthorizedAccessException) { problem = NativeFiles.NotRecycled; }
                if (problem is not null) problems.Add(items.Name(item) + " · " + app.L(problem));
            }
        });
        foreach (var item in done) Gone.Add(item.Item);
        foreach (var item in gone) { Gone.Add(item); Removed.Add(item); }
        // Undo always means the batch just run, even when none of it can be restored.
        if (done.Count + gone.Count > 0) last = done;
        if (failure is not null) problems.Add(failure);
        Finish(string.Format(app.L("Moved to the Recycle Bin: {0} of {1}"), done.Count, chosen.Length), problems);
    }

    private async Task UndoAsync()
    {
        if (!Idle || last.Count == 0) return;
        var batch = last;
        var restored = new List<T>();
        var problems = new List<string>();
        string? failure = await Work(() =>
        {
            foreach (var (item, stored) in batch)
            {
                try { items.Restore(item, stored); restored.Add(item); }
                catch (UserError error) { problems.Add(items.Name(item) + " · " + app.L(error.Message)); }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or System.IO.IOException or UnauthorizedAccessException or System.Reflection.TargetInvocationException or System.Runtime.InteropServices.COMException)
                { problems.Add(items.Name(item) + " · " + app.L(NativeFiles.NotRestored)); }
            }
        });
        foreach (var item in restored) Gone.Remove(item);
        // Items that were not restored stay in the batch, so Undo can be tried again.
        last = batch.Where(entry => !restored.Contains(entry.Item)).ToList();
        if (failure is not null) problems.Add(failure);
        Finish(string.Format(app.L("Restored from the Recycle Bin: {0} of {1}"), restored.Count, batch.Count), problems);
    }

    private async Task<string?> Work(Action action)
    {
        busy = true; UpdateButtons(); changed();
        Status.Text = app.L("Working with the Recycle Bin…");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); completion.SetResult(); } catch (Exception error) { completion.SetException(error); } })
        { IsBackground = true, Name = "Swivel Recycle Bin" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        try { await completion.Task; return null; }
        catch (Exception error) { return app.L(error is UserError ? error.Message : "Windows stopped the Recycle Bin work. Some items may not have been handled."); }
        finally { busy = false; }
    }

    // After the bookkeeping, so the app reads the final counts once the window closes.
    private void Finish(string summary, List<string> problems)
    {
        var text = new StringBuilder(summary);
        foreach (string problem in problems) text.AppendLine().Append(problem);
        Status.Text = text.ToString();
        UpdateButtons(); changed();
        if (closeRequested) window.Close();
    }
}
