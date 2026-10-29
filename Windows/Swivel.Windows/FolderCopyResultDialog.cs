using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Swivel.Core;

namespace Swivel.Windows;

/// After Copy Folder to…: the new folder, any problem, and undoing the copy by moving the new
/// folder to the Recycle Bin while this window is open (only while it is unchanged since copying).
internal sealed class FolderCopyResultDialog : Window
{
    private readonly App app;
    private readonly FolderCopyOutcome outcome;
    private readonly RecycleBinSession<FolderCopyOutcome> bin;
    private readonly TextBlock state;
    private readonly bool offered;
    internal bool Moved => bin.Gone.Count > 0; // also when Windows removed it outright
    internal string Summary { get; }

    internal string CurrentReport
    {
        get
        {
            var text = new StringBuilder();
            text.AppendLine(app.L("Copy Folder Results")); text.AppendLine(Summary);
            text.AppendLine(LibraryJson.Visible(outcome.Folder!) + " · " + State());
            if (outcome.Error is not null) text.AppendLine(MoveFilesLayout.ProblemText(MoveFilesLayout.Problems(outcome.Error), app.L));
            bin.AppendReport(text, new[] { outcome });
            return text.ToString();
        }
    }

    public FolderCopyResultDialog(App app, FolderCopyOutcome outcome, string summary)
    {
        ArgumentNullException.ThrowIfNull(outcome.Folder);
        this.app = app; this.outcome = outcome; Summary = summary;
        // Only the drive type here (asking the Recycle Bin itself can be slow); the move checks again.
        offered = outcome.Recorded && outcome.RootIdentity is not null && outcome.Entries.Count > 0
            && new System.IO.DriveInfo(System.IO.Path.GetPathRoot(outcome.Folder)!).DriveType == System.IO.DriveType.Fixed;
        Title = app.L("Copy Folder Results"); Width = 760; Height = 560; MinWidth = 420; MinHeight = 330;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SourceInitialized += (_, _) => WebRouteLayout.Fit(this, Owner ?? this);
        DpiChanged += (_, _) => WebRouteLayout.Fit(this, this);
        var root = new DockPanel { Margin = new Thickness(20) };
        var footer = new WrapPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        bin = new RecycleBinSession<FolderCopyOutcome>(app, this, new(
            copy => LibraryJson.Visible(copy.Folder!),
            copy => LibraryJson.Visible(copy.Folder!),
            (copy, owner) => NativeFiles.RecycleCopiedFolder(copy.Folder!, copy.RootIdentity!, copy.Entries, owner),
            (copy, stored) => NativeFiles.RestoreRecycled(copy.Folder!, stored, true),
            "Move the new copied folder to the Recycle Bin?", "Move {0} copies to the Recycle Bin?", "Only the new copy is moved; the original folder stays where it is."),
            () => offered && !bin!.Gone.Contains(outcome) ? new[] { outcome } : Array.Empty<FolderCopyOutcome>(), Refresh);
        bin.Move.Content = app.L("Move Copy to Recycle Bin…");
        footer.Children.Add(bin.Move); footer.Children.Add(bin.Undo);
        footer.Children.Add(UI.Button(app.L("Copy Report"), () => app.Try(() => Clipboard.SetText(CurrentReport))));
        var close = UI.Button(app.L("Close"), () => DialogResult = false); close.IsCancel = true; footer.Children.Add(close);

        var body = new StackPanel();
        body.Children.Add(UI.Text(summary, 18));
        var path = MoveFilesLayout.Details(app, "New Folder", 60); path.Text = LibraryJson.Visible(outcome.Folder); body.Children.Add(path);
        state = UI.Text("", 13); body.Children.Add(state);
        if (outcome.Error is not null)
        {
            var problems = MoveFilesLayout.Problems(outcome.Error);
            body.Children.Add(UI.Text(string.Format(CultureInfo.CurrentCulture, app.L("The following problems were reported ({0})."), problems.Count), 14));
            var problem = MoveFilesLayout.Details(app, "Reported problem", 90);
            problem.Text = MoveFilesLayout.ProblemText(problems, app.L); body.Children.Add(problem);
        }
        body.Children.Add(UI.Text(app.L(FolderCopyUndo.Notice), 12));
        body.Children.Add(bin.Status);
        root.Children.Add(UI.Scroll(body)); Content = root;
        Refresh();
        Loaded += (_, _) => close.Focus();
    }

    private string State() => app.L(bin.Gone.Contains(outcome) ? "moved or removed in this window"
        : !outcome.Recorded || outcome.RootIdentity is null ? FolderCopyUndo.NotOffered
        : !offered ? "This drive has no Recycle Bin; not offered for undo" : "Can be moved to the Recycle Bin");

    private void Refresh() { state.Text = app.L("Status") + ": " + State(); bin.UpdateButtons(); }
}
