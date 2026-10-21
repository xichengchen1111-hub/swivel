using System.Windows;
using System.Windows.Threading;

namespace Swivel;

/// Owns the menu window: opens it at the pointer on a key tap, pages it, and runs the dropped-on item.
sealed class WheelController
{
    readonly WheelWindow panel = new();
    Toast Toast => Toast.Shared;
    WheelMode? mode;
    List<List<WheelItem>> pages = [];
    int page;
    public const int PerPage = 8;

    /// Demo only: a menu style to use instead of the saved one.
    public MenuStyle? StyleOverride { get; set; }

    /// The files of the drag in progress, once it has entered the menu window; null until then.
    IReadOnlyList<string>? dragFiles;
    /// The drag in progress entered the menu but carries no files (text, a picture from a browser…).
    bool notFiles;
    /// A key tapped before the drag's files were known: the menu to open when they are.
    WheelMode? pendingMode;
    Native.POINT pendingAt;

    public WheelController()
    {
        panel.View.Dropped = Perform;
        panel.View.OnDragEndedWithoutDrop = () =>
        {
            if (panel.IsShown && !panel.IsClosing && panel.View.Pressed == null) Close(WheelWindow.Dismissal.Cancel);
        };
        panel.FilesEntered = FilesEntered;
        // Create the window and draw a menu once offscreen, so fonts and shadows are cached before the first real one.
        Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            try
            {
                new System.Windows.Interop.WindowInteropHelper(panel).EnsureHandle();
                var warm = MenuPreview.Draw(MenuStyle.Wheel, ["JPG", "PNG", "HEIC", "PDF"], WheelMode.Convert, 0);
                warm.Redraw();
            }
            catch (Exception ex) { Log.Write($"Wheel warm-up: {ex.Message}"); }
        });
    }

    /// Exposed for tests and demos.
    internal WheelWindow Window => panel;
    internal IReadOnlyList<List<WheelItem>> Pages => pages;
    internal int CurrentPage => page;

    /// A trigger key was tapped during a drag. `files` is null for a real drag (the menu learns the dragged files
    /// when the drag enters its window); demos pass them.
    public void KeyTapped(WheelMode newMode, IReadOnlyList<string>? files)
    {
        if (newMode == mode && panel.IsShown && !panel.IsClosing)
        {
            if (pages.Count <= 1)
            {
                Feedback.Refuse();
                return;
            }
            page = (page + 1) % pages.Count;
            Refresh();
            panel.View.Rehover();
            return;
        }
        if (files == null)
        {
            if (notFiles) return;
            files = dragFiles;
        }
        if (files == null)
        {
            // A real drag whose files aren't known yet: put a drop target under the pointer and wait for the drag
            // to enter it.
            pendingMode = newMode;
            pendingAt = Native.CursorPixels();
            panel.Probe(pendingAt);
            return;
        }
        Open(newMode, files, Native.CursorPixels());
    }

    void FilesEntered(IReadOnlyList<string>? files)
    {
        if (files == null)
        {
            // Not a file drag: nothing to offer, so the probe goes quietly.
            if (dragFiles == null) notFiles = true;
            if (panel.IsProbing && !panel.IsShown) panel.HideNow();
            pendingMode = null;
            return;
        }
        dragFiles = files;
        if (pendingMode is { } wanted && panel.IsProbing)
        {
            pendingMode = null;
            Open(wanted, files, pendingAt);
        }
    }

    void Open(WheelMode newMode, IReadOnlyList<string> files, Native.POINT at)
    {
        var items = Catalog.Items(files, newMode);
        if (items.Count == 0)
        {
            if (panel.IsProbing && !panel.IsShown) panel.HideNow();
            Feedback.Refuse();
            var cursor = Native.CursorPixels();
            Toast.Show(L("Nothing to show for this file"), "nosign", near: new Point(cursor.X, cursor.Y));
            return;
        }
        mode = newMode;
        panel.View.MenuStyle = StyleOverride ?? Prefs.Style;
        pages = Paginate(items);
        page = 0;
        panel.View.Subtitle = files.Count == 1 ? System.IO.Path.GetFileName(files[0].TrimEnd('\\', '/')) : L("%d files", files.Count);
        Refresh();
        panel.ShowMenu(at);
    }

    /// Items split into pages of eight.
    public static List<List<WheelItem>> Paginate(IReadOnlyList<WheelItem> items)
    {
        var result = new List<List<WheelItem>>();
        for (int start = 0; start < items.Count; start += PerPage)
            result.Add(items.Skip(start).Take(PerPage).ToList());
        return result;
    }

    /// Demo only: shows an item as hovered (ignored if out of range).
    public void DemoHover(int index)
    {
        if (index < 0 || index >= panel.View.Items.Count) return;
        panel.View.Hovered = index;
    }

    /// Demo only: drops on an item without running it.
    public void DemoPress(int index)
    {
        if (index < 0 || index >= panel.View.Items.Count) return;
        panel.View.BeginConfirm(index);
        Feedback.Press();
        Close(WheelWindow.Dismissal.Confirm);
    }

    /// Demo only: turns to the next page as a second key tap would.
    public void DemoNextPage()
    {
        if (mode is not { } m || pages.Count <= 1) return;
        KeyTapped(m, []);
    }

    public void DragEnded()
    {
        dragFiles = null;
        notFiles = false;
        pendingMode = null;
        if (panel.IsProbing && !panel.IsShown)
        {
            panel.HideNow();
            return;
        }
        if (!panel.IsShown || panel.IsClosing) return;
        // Released on the hub or away from the menu: nothing will be dropped, so clear it away at once.
        if (panel.View.Hovered == null)
        {
            Close(WheelWindow.Dismissal.Cancel);
            return;
        }
        // Released over an item: the drop can arrive just after the button is seen going up.
        int token = panel.Generation;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.35) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (panel.Generation == token && panel.View.Pressed == null) Close(WheelWindow.Dismissal.Cancel);
        };
        timer.Start();
    }

    void Refresh()
    {
        if (mode is not { } m) return;
        panel.View.Configure(pages[page], m, page, pages.Count);
        panel.Relayout();
    }

    void Close(WheelWindow.Dismissal how)
    {
        panel.Dismiss(how);
        mode = null;
    }

    void Perform(WheelItem item, IReadOnlyList<string> files)
    {
        Close(WheelWindow.Dismissal.Confirm);
        int editorsBefore = Editor.Presented;
        // Slow actions get a progress toast; items that open their own window (editors) don't need one.
        bool finished = false;
        var busy = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.25) };
        busy.Tick += (_, _) =>
        {
            busy.Stop();
            if (finished || Editor.Presented != editorsBefore) return;
            Toast.Show(item.DisplayTitle + "…", "busy", autoHide: false, spinning: true);
        };
        busy.Start();
        _ = Run();

        async Task Run()
        {
            try
            {
                var result = await item.Run(files);
                finished = true;
                busy.Stop();
                if (result.Files.Count > 0) Feedback.Done();
                if (result.Message.Length == 0 && result.Files.Count == 0) Toast.Hide();
                else Toast.Show(L(result.Message), "ok", result.Files);
            }
            catch (Cancelled)
            {
                finished = true;
                busy.Stop();
                // Closing an editor window is its own confirmation; only say "Cancelled" for other actions.
                if (Editor.Presented == editorsBefore) Toast.Show(L("Cancelled"), "cancel", tint: ToastTint.Secondary);
                else Toast.Hide();
            }
            catch (Info info)
            {
                finished = true;
                busy.Stop();
                Toast.Show(info.Message, "info");
            }
            catch (Exception ex)
            {
                finished = true;
                busy.Stop();
                Log.Write($"{item.Title} failed: {ex}");
                Toast.Show(ex.Message, "error", tint: ToastTint.Red);
            }
        }
    }
}
