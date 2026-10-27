using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Swivel.Core;

namespace Swivel.Windows;

/// After Copy to Folder: every copy beside its original, any problem, and undoing the copy by
/// moving selected copies to the Recycle Bin while this window is open.
internal sealed class CopyFilesResultDialog : Window
{
    internal const string Notice = "To undo the copy, select copies (or Select All Copies) and move them with Move Selected to Recycle Bin…, which asks first. A copy is moved only while it is still the file Swivel created, with the size and modified time recorded right after copying and no other hard link; a copy that was renamed or replaced, or whose size, modified time or links changed, is left alone (its contents are not read again). Originals are never opened or changed. Only fixed drives with a Recycle Bin are used; if a copy is too large for the Recycle Bin, Windows asks before deleting it permanently. Undo Move puts the last moved copies back while this window is open, unless something now exists at their place; afterwards, restore them from the Recycle Bin.";

    private readonly App app;
    private readonly CopyOutcome outcome;
    private readonly bool crossDrive;
    private readonly ListView list;
    private readonly TextBox details;
    private readonly RecycleBinSession<CopyResult> bin;
    private readonly Button selectAll;
    // All copies are in one folder: on a drive without a Recycle Bin (a removable one), none is offered.
    private readonly bool canRecycle;
    internal int Moved => bin.Gone.Count; // also those Windows removed outright
    internal IReadOnlyList<string> Remaining => outcome.Copied.Where(copy => !bin.Gone.Contains(copy)).Select(copy => copy.Destination).ToArray();

    internal string CurrentReport
    {
        get
        {
            var text = new StringBuilder();
            text.AppendLine(app.L("Copy Results")); text.AppendLine(Summary());
            foreach (var copy in outcome.Copied) text.AppendLine(Visible(copy.Destination) + " ← " + Visible(copy.Source) + " · " + State(copy));
            if (outcome.Error is not null) text.AppendLine(MoveFilesLayout.ProblemText(MoveFilesLayout.Problems(outcome.Error), app.L));
            bin.AppendReport(text, outcome.Copied);
            return text.ToString();
        }
    }

    public CopyFilesResultDialog(App app, CopyOutcome outcome, bool crossDrive = false)
    {
        this.app = app; this.outcome = outcome; this.crossDrive = crossDrive;
        // Only the drive type here (asking the Recycle Bin itself can be slow); each move checks again.
        canRecycle = outcome.Copied.Count > 0 && new System.IO.DriveInfo(System.IO.Path.GetPathRoot(outcome.Copied[0].Destination)!).DriveType == System.IO.DriveType.Fixed;
        Title = app.L("Copy Results"); MoveFilesLayout.Configure(this);
        var root = new DockPanel { Margin = new Thickness(20) };
        var footer = new WrapPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        list = new ListView { Height = 260, SelectionMode = SelectionMode.Extended, Margin = new Thickness(0, 8, 0, 8) };
        VirtualizingPanel.SetIsVirtualizing(list, true); VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(list, true); ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        bin = new RecycleBinSession<CopyResult>(app, this, new(
            copy => Visible(copy.Destination),
            copy => Visible(copy.Destination),
            (copy, owner) => NativeFiles.RecycleCopy(copy.Destination, copy.Stamp!, owner),
            (copy, stored) => NativeFiles.RestoreRecycled(copy.Destination, stored, false),
            "Move 1 copy to the Recycle Bin?", "Move {0} copies to the Recycle Bin?", "Only the copies are moved; their originals stay where they are."),
            () => list.SelectedItems.OfType<Row>().Select(row => row.Copy).Where(copy => Offered(copy) && !bin!.Gone.Contains(copy)).ToArray(), Refresh);
        selectAll = UI.Button(app.L("Select All Copies"), () => list.SelectAll());
        footer.Children.Add(selectAll); footer.Children.Add(bin.Move); footer.Children.Add(bin.Undo);
        footer.Children.Add(UI.Button(app.L("Copy Report"), () => app.Try(() => Clipboard.SetText(CurrentReport))));
        var close = UI.Button(app.L("Close"), () => DialogResult = false); close.IsCancel = true; footer.Children.Add(close);

        var body = new StackPanel();
        body.Children.Add(UI.Text(Summary(), 18));
        if (outcome.Error is not null)
        {
            var problems = MoveFilesLayout.Problems(outcome.Error);
            body.Children.Add(UI.Text(string.Format(CultureInfo.CurrentCulture, app.L("The following problems were reported ({0})."), problems.Count), 14));
            var problem = MoveFilesLayout.Details(app, "Reported problem", 90);
            problem.Text = MoveFilesLayout.ProblemText(problems, app.L); body.Children.Add(problem);
        }
        body.Children.Add(UI.Text(app.L(Notice), 12));
        var view = new GridView();
        foreach (var (title, property, width) in new[] { ("Copied File", "Destination", 330.0), ("Original File", "Source", 330.0), ("Status", "Status", 220.0) })
            view.Columns.Add(new GridViewColumn { Header = app.L(title), DisplayMemberBinding = new Binding(property), Width = width });
        list.View = view; AutomationProperties.SetName(list, app.L("Copy Results"));
        details = MoveFilesLayout.Details(app, "Selected File Paths", 110);
        list.SelectionChanged += (_, _) => { ShowSelected(); bin.UpdateButtons(); };
        body.Children.Add(list); body.Children.Add(details); body.Children.Add(bin.Status);
        root.Children.Add(UI.Scroll(body)); Content = root;
        Refresh();
        Loaded += (_, _) => close.Focus();
    }

    private string Summary()
    {
        string text = string.Format(app.L("Copied {0} of {1} file(s). Originals were not changed."), outcome.Copied.Count, outcome.Total);
        if (crossDrive) text = app.L("The destination is on another drive, so the files were copied instead of moved.") + " " + text;
        if (outcome.Cancelled) text += " " + app.L("Cancelled. Completed files were kept.");
        return text;
    }

    private bool Offered(CopyResult copy) => canRecycle && copy.Stamp is not null;

    private string State(CopyResult copy) => app.L(bin.Gone.Contains(copy) ? "moved or removed in this window"
        : !copy.Verified ? "Not verified; not offered for undo"
        : copy.Stamp is null ? "Verified; its attributes (such as System) keep it out of undo"
        : !canRecycle ? "This drive has no Recycle Bin; not offered for undo" : "Can be moved to the Recycle Bin");

    private void Refresh()
    {
        var chosen = list.SelectedItems.OfType<Row>().Select(row => row.Copy).ToHashSet();
        list.ItemsSource = outcome.Copied.Select(copy => new Row(copy, Visible(copy.Destination), Visible(copy.Source), State(copy))).ToArray();
        foreach (var row in list.Items.OfType<Row>().Where(row => chosen.Contains(row.Copy))) list.SelectedItems.Add(row);
        list.IsEnabled = selectAll.IsEnabled = bin.Idle;
        ShowSelected(); bin.UpdateButtons();
    }

    private void ShowSelected() => details.Text = list.SelectedItems.Count == 1 && list.SelectedItem is Row row
        ? app.L("Copied File") + ":\n" + row.Destination + "\n\n" + app.L("Original File") + ":\n" + row.Source + "\n\n" + app.L("Status") + ": " + row.Status : "";

    private static string Visible(string value) => LibraryJson.Visible(value);

    private sealed record Row(CopyResult Copy, string Destination, string Source, string Status)
    {
        public override string ToString() => Destination + " ← " + Source + " · " + Status;
    }
}
