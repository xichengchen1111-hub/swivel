using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Swivel.Core;

namespace Swivel.Windows;

/// Folders named exactly as apps removed since the installation records were shown, to
/// review and move to the Recycle Bin while no program runs from them. Nothing is selected.
/// Back to Installation Records closes it with DialogResult true.
internal sealed class AppLeftoversDialog : Window
{
    private readonly App app;
    private readonly AppLeftoverResult result;
    private readonly Func<IReadOnlyList<string>?> programPaths;
    private readonly Func<Action<LeftoverFolder>> recordsCheck;
    private (object? Batch, Action<LeftoverFolder>? Check) records; // read once per batch, on its worker
    private readonly ListView items;
    private readonly RecycleBinSession<LeftoverItem> bin;
    private readonly TextBlock summary, running;
    private readonly Button checkRunning, back;
    private IReadOnlyList<string>? programs;
    private Dictionary<string, bool> fixedDrives = new(StringComparer.OrdinalIgnoreCase);
    internal int Moved => bin.Gone.Count; // also those Windows removed outright

    internal string CurrentReport
    {
        get
        {
            var text = new StringBuilder();
            text.AppendLine(app.L("Leftovers of Removed Apps")); text.AppendLine(Summary());
            text.AppendLine(app.L(AppLeftovers.Notice)); text.AppendLine(app.L(AppLeftovers.Limits));
            foreach (string line in Lines()) text.AppendLine(line);
            foreach (var item in Listed()) text.AppendLine(Line(item));
            bin.AppendReport(text, result.Items);
            return text.ToString();
        }
    }

    public AppLeftoversDialog(App app, AppLeftoverResult result, Func<IReadOnlyList<string>?> programPaths, Func<Action<LeftoverFolder>> recordsCheck)
    {
        this.app = app; this.result = result; this.programPaths = programPaths; this.recordsCheck = recordsCheck;
        Title = app.L("Leftovers of Removed Apps"); Width = 1040; Height = 780; MinWidth = 560; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) };
        var footer = new WrapPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        items = new ListView { Height = 320, SelectionMode = SelectionMode.Extended, Margin = new Thickness(0, 8, 0, 8) };
        VirtualizingPanel.SetIsVirtualizing(items, true); VirtualizingPanel.SetVirtualizationMode(items, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(items, true); ScrollViewer.SetHorizontalScrollBarVisibility(items, ScrollBarVisibility.Auto);
        bin = new RecycleBinSession<LeftoverItem>(app, this, new(
            item => Visible(item.Folder.Path),
            item => Visible(item.Folder.App.Name) + " · " + Visible(item.Folder.Path) + " · " + AppCaches.Size(item.Size, app.L),
            (item, owner) => NativeFiles.RecycleLeftover(item.Folder.Path, () => Check(item), owner),
            (item, stored) => NativeFiles.RestoreRecycled(item.Folder.Path, stored, true),
            "Move 1 leftover folder to the Recycle Bin?", "Move {0} leftover folders to the Recycle Bin?",
            "A matching name does not prove that a folder belonged to the removed app. Check each path, and keep any folder that holds saved games, documents or settings you still want. If a folder is too large for the Recycle Bin, Windows asks before deleting it permanently."),
            () => items.SelectedItems.OfType<Row>().Select(row => row.Item).Where(item => !bin!.Gone.Contains(item) && item.Registrations?.CanTrash != false).ToArray(), Refresh);
        checkRunning = UI.Button(app.L("Check Running Programs Again"), Refresh);
        var evidence = UI.Button(app.L(RegisteredTargets.Title), () =>
        {
            if (items.SelectedItem is Row row && row.Item.Registrations is { } registrations)
                app.Try(() => new RegisteredTargetsDialog(app, registrations) { Owner = this }.ShowDialog());
        });
        evidence.IsEnabled = false; footer.Children.Add(evidence);
        footer.Children.Add(bin.Move); footer.Children.Add(bin.Undo); footer.Children.Add(checkRunning);
        footer.Children.Add(UI.Button(app.L("Copy Report"), () => app.Try(() => Clipboard.SetText(CurrentReport))));
        back = UI.Button(app.L("Back to Installation Records"), () => { if (bin.Idle) DialogResult = true; }); footer.Children.Add(back);
        var close = UI.Button(app.L("Close"), () => DialogResult = false); close.IsCancel = true; footer.Children.Add(close);

        var body = new StackPanel();
        summary = UI.Text("", 14); body.Children.Add(summary);
        running = UI.Text("", 13); body.Children.Add(running);
        body.Children.Add(UI.Text(app.L(AppLeftovers.Notice), 12));
        body.Children.Add(UI.Text(app.L(AppLeftovers.Limits), 12));
        foreach (string line in Lines()) body.Children.Add(UI.Text(line, 12));
        if (result.Removed.Count == 0) body.Children.Add(UI.Text(app.L("No app from the earlier list is confirmed removed from the installation records now. Go back to the installation records, uninstall an app with Open Installed Apps in Settings, wait for its uninstaller to finish, then choose Find Leftovers again.")));
        else if (result.Items.Count == 0) body.Children.Add(UI.Text(app.L("No folder named exactly as a removed app was found in the places looked at.")));
        if (result.Registrations.Count > 0 && result.RegistrationSnapshot is not null)
        {
            var removed = new ComboBox { ItemsSource = result.Registrations.Select(entry => new RemovedRow(entry)).ToArray(), SelectedIndex = 0, MaxDropDownHeight = 240 };
            AutomationProperties.SetName(removed, app.L("Inspect Removed App Registrations…"));
            body.Children.Add(removed);
            body.Children.Add(UI.Button(app.L("Inspect Removed App Registrations…"), () => app.Try(() =>
            {
                if (removed.SelectedItem is RemovedRow selected) new RegisteredTargetsDialog(app, RegisteredTargets.ForFolder(result.RegistrationSnapshot, selected.Entry.Folder, default)) { Owner = this }.ShowDialog();
            })));
        }
        var view = new GridView();
        foreach (var (title, property, width) in new[] { ("App", "App", 200.0), ("Place", "Place", 170.0), ("Folder", "Folder", 250.0), ("Size", "Size", 150.0), ("Status", "Status", 260.0) })
            view.Columns.Add(new GridViewColumn { Header = app.L(title), DisplayMemberBinding = new Binding(property), Width = width });
        items.View = view; AutomationProperties.SetName(items, app.L("Leftovers of Removed Apps"));
        items.SelectionChanged += (_, _) => { evidence.IsEnabled = bin.Idle && items.SelectedItem is Row row && row.Item.Registrations is not null; bin.UpdateButtons(); };
        body.Children.Add(items); body.Children.Add(bin.Status);
        root.Children.Add(UI.Scroll(body)); Content = root;
        Refresh();
        SourceInitialized += (_, _) => WebRouteLayout.Fit(this, Owner ?? this); DpiChanged += (_, _) => WebRouteLayout.Fit(this, this);
    }

    private IEnumerable<LeftoverItem> Listed() => result.Items.Where(item => !bin.Gone.Contains(item));

    private string Summary()
    {
        var listed = Listed().ToArray();
        var text = string.Format(app.L("Apps removed since the list was read: {0} · folders listed: {1} · measured {2:N0} bytes"), result.Removed.Count, listed.Length, listed.Sum(item => item.Size?.Bytes ?? 0));
        if (listed.Any(item => item.Size is null || item.Size.Partial) || result.MeasureEntryLimit || result.MeasureTimeLimit) text += " · " + app.L("Some sizes are partial or not measured.");
        if (result.FolderLimit || result.DiscoveryLimit || result.Bases.Any(b => b.Partial)) text += " · " + app.L("The search stopped at a limit; some folders may not be listed.");
        if (result.Bases.Any(b => b.Unreadable || b.State == LeftoverBaseState.Unavailable)) text += " · " + app.L("Some places could not be read; some folders may not be listed.");
        return text;
    }

    // The removed apps, the names not looked for, and every place with how it was read.
    private IEnumerable<string> Lines()
    {
        if (result.Removed.Count > 0)
            yield return string.Format(app.L("Removed since the list was read: {0}"), string.Join("; ", result.Removed.Select(a => Visible(a.Name))));
        if (result.Unconfirmed.Count > 0)
            yield return string.Format(app.L("Missing from the list now but not looked for, because its own installation record is still there or could not be checked (an uninstaller may still be running): {0}"), string.Join("; ", result.Unconfirmed.Select(a => Visible(a.Name))));
        if (result.Skipped.Count > 0)
            yield return string.Format(app.L("Not looked for (a shared folder name, a name still used by a recorded app or publisher, or not a valid folder name): {0}"), string.Join("; ", result.Skipped.Select(Visible)));
        foreach (var place in result.Bases)
        {
            string state = place.State switch
            {
                LeftoverBaseState.NotFound => app.L("Not found"),
                LeftoverBaseState.Unavailable => app.L(place.Issue ?? AppLeftovers.Unreadable),
                _ => place.Issue is not null ? app.L(place.Issue) : place.Unreadable ? app.L(AppLeftovers.SomeUnreadable) : place.Partial ? app.L("Listed up to a limit") : app.L("Listed in full"),
            };
            yield return app.L(AppLeftovers.Place(place.Base)) + " · " + Visible(place.Path ?? "") + " · " + state;
        }
    }

    private void Refresh()
    {
        if (bin.Idle)
        {
            programs = programPaths();
            // Only the drive type here; the Recycle Bin itself is queried on the worker before each move.
            fixedDrives = result.Items.Select(item => System.IO.Path.GetPathRoot(item.Folder.Path) ?? "").Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(root => root, Fixed, StringComparer.OrdinalIgnoreCase);
        }
        running.Text = programs is null ? app.L("Swivel could not list the running programs, so no folder can be moved now.")
            : Listed().SelectMany(item => AppLeftovers.Inside(programs, item.Folder.Path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() is { Length: > 0 } open
                ? string.Format(app.L("Running from a listed folder now (quit them first): {0}"), string.Join("; ", open.Select(Visible)))
                : app.L("No running program was found inside the listed folders.");
        items.ItemsSource = Listed().Select(item => new Row(this, item)).ToArray();
        items.IsEnabled = checkRunning.IsEnabled = back.IsEnabled = bin.Idle;
        summary.Text = Summary();
        bin.UpdateButtons();
    }

    // On the batch's worker, just before each move: the app is still missing from the records
    // (read once per batch) and no program runs from the folder.
    internal void Check(LeftoverItem item)
    {
        if (!ReferenceEquals(records.Batch, bin.Batch) || records.Check is null) records = (bin.Batch, recordsCheck());
        if (item.Registrations is { } registrations) RegisteredTargets.RequireTrash(registrations);
        records.Check(item.Folder);
        if (AppLeftovers.InUse(programPaths(), item.Folder.Path)) throw new UserError(AppLeftovers.ProgramInside);
    }

    private static bool Fixed(string root)
    {
        try { return root.Length > 0 && new System.IO.DriveInfo(root).DriveType == System.IO.DriveType.Fixed; }
        catch (Exception error) when (error is ArgumentException or System.IO.IOException or UnauthorizedAccessException) { return false; }
    }

    private string Status(LeftoverItem item) => item.Registrations is { CanTrash: false } ? app.L(item.Registrations.Matches.Count > 0 ? RegisteredTargets.Matched : RegisteredTargets.Blocked)
        : item.Size?.Issue is { } issue ? app.L(issue)
        : !fixedDrives.GetValueOrDefault(System.IO.Path.GetPathRoot(item.Folder.Path) ?? "") ? app.L("Not on a fixed drive; it cannot be moved")
        : programs is null ? app.L("Running programs could not be listed; it cannot be moved now")
        : AppLeftovers.Inside(programs, item.Folder.Path).Count > 0 ? app.L("A program is running from it; quit it first")
        : app.L("Can be moved to the Recycle Bin");
    private string Line(LeftoverItem item) => Visible(item.Folder.App.Name) + " · " + Visible(item.Folder.Path) + " · " + AppCaches.Size(item.Size, app.L) + " · " + Status(item);
    private static string Visible(string value) => DoctorFacts.VisibleBounded(value, 512);

    private sealed record RemovedRow(RemovedAppRegistrations Entry)
    {
        public override string ToString() => Visible(Entry.App) + " · " + Visible(Entry.Folder ?? "?");
    }
    private sealed record Row(AppLeftoversDialog Owner, LeftoverItem Item)
    {
        public string App => Visible(Item.Folder.App.Name);
        public string Place => Owner.app.L(AppLeftovers.Place(Item.Folder.Base));
        public string Folder => Visible(Item.Folder.Publisher is { } publisher ? publisher + "\\" + Item.Folder.Name : Item.Folder.Name);
        public string Size => AppCaches.Size(Item.Size, Owner.app.L);
        public string Status => Owner.Status(Item);
        public override string ToString() => App + " · " + Place + " · " + Folder + " · " + Size + " · " + Status;
    }
}
