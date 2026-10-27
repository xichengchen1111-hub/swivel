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

/// Move Selected to Recycle Bin… for Similar Text Files and Similar Images: the two files of the
/// selected pair are listed, one of them can be moved while the other is kept unchanged.
internal sealed class SimilarPairCleanup
{
    internal const string Notice = "To clean up a pair, select it, then select one of its two files below and choose Move Selected to Recycle Bin…. Similar files are not identical: compare them yourself first. The other file of the pair is kept, and stays kept: it cannot be moved through another pair while this window is open. Before the move it must still be the same file with the size and modified time recorded by the comparison, and it is held open until the move ends so other programs cannot newly open it for writing, rename or delete it. The selected file must also be unchanged since the comparison (same file, size and modified time; its contents are not read again) and have no other hard link. Only fixed drives with a Recycle Bin are used; if a file is too large for the Recycle Bin, Windows asks before deleting it permanently. Undo Move restores the last moved file while this window is open, unless something now exists at its place; afterwards, restore it from the Recycle Bin.";

    private readonly App app;
    private readonly string root;
    private readonly IReadOnlyDictionary<string, DuplicateStamp> stamps;
    private readonly ListView pairs;
    private readonly Func<(string First, string Second)?> selectedPair;
    private readonly RecycleBinSession<Side> bin;

    public ListView Files { get; }
    public Button Move => bin.Move;
    public Button Undo => bin.Undo;
    public TextBlock Status => bin.Status;
    internal int Moved => bin.Gone.Count;
    /// After a move or Undo, so the window can refresh what it shows about the selected pair.
    internal event Action? Changed;

    public SimilarPairCleanup(App app, Window window, string root, IReadOnlyDictionary<string, DuplicateStamp> stamps, ListView pairs, Func<(string First, string Second)?> selectedPair)
    {
        this.app = app; this.root = root; this.stamps = stamps; this.pairs = pairs; this.selectedPair = selectedPair;
        Files = new ListView { Height = 90, SelectionMode = SelectionMode.Single, Margin = new Thickness(0, 6, 0, 8) };
        VirtualizingPanel.SetIsVirtualizing(Files, true); VirtualizingPanel.SetVirtualizationMode(Files, VirtualizationMode.Recycling);
        var view = new GridView(); view.Columns.Add(new GridViewColumn { Header = app.L("Files in Selected Pair"), DisplayMemberBinding = new Binding("Path"), Width = 760 });
        Files.View = view; AutomationProperties.SetName(Files, app.L("Files in Selected Pair"));
        bin = new RecycleBinSession<Side>(app, window, new(
            side => Visible(side.Relative),
            side => Visible(Absolute(side.Relative)),
            (side, owner) => NativeFiles.RecycleSimilar(Absolute(side.Relative), stamps[side.Relative], Absolute(side.Partner), stamps[side.Partner], owner),
            (side, stored) => NativeFiles.RestoreRecycled(Absolute(side.Relative), stored, false),
            "Move 1 similar file to the Recycle Bin?", "Move {0} similar files to the Recycle Bin?", "The other file of the pair is kept. Similar files are not identical; check which one you want to keep before moving."),
            Selected, Refresh);
        Files.SelectionChanged += (_, _) => bin.UpdateButtons();
        pairs.SelectionChanged += (_, _) => ShowPair();
    }

    /// The display line for a file, marked when it was moved in this window or kept for such a move.
    internal string Line(string relative)
        => Visible(Absolute(relative)) + (IsGone(relative) ? " · " + app.L("moved or removed in this window") : IsKept(relative) ? " · " + app.L("kept for a file moved in this window") : "");

    internal void AppendReport(StringBuilder text, IEnumerable<string> inOrder)
        => bin.AppendReport(text, inOrder.Distinct(StringComparer.Ordinal).Select(relative => new Side(relative, "")));

    private bool IsGone(string relative) => bin.Gone.Contains(new Side(relative, ""));
    // Gone keeps each moved file with the partner it was moved against; that partner stays.
    private bool IsKept(string relative) => bin.Gone.Any(side => StringComparer.Ordinal.Equals(side.Partner, relative));

    private void ShowPair()
    {
        if (selectedPair() is not var (first, second)) { Files.ItemsSource = null; bin.UpdateButtons(); return; }
        Files.ItemsSource = new[] { new FileRow(new Side(first, second), Line(first)), new FileRow(new Side(second, first), Line(second)) };
        bin.UpdateButtons();
    }

    // One file whose partner is still in place, both compared, and not itself kept for an
    // earlier move (pairs overlap, so it could otherwise go through another pair).
    private IReadOnlyList<Side> Selected()
    {
        if (Files.SelectedItem is not FileRow row || IsGone(row.Side.Relative) || IsGone(row.Side.Partner) || IsKept(row.Side.Relative)
            || !stamps.ContainsKey(row.Side.Relative) || !stamps.ContainsKey(row.Side.Partner)) return Array.Empty<Side>();
        return new[] { row.Side };
    }

    private void Refresh()
    {
        int index = Files.SelectedIndex;
        ShowPair();
        if (index >= 0) Files.SelectedIndex = index;
        pairs.IsEnabled = Files.IsEnabled = bin.Idle;
        Changed?.Invoke();
    }

    private string Absolute(string relative) => System.IO.Path.Combine(root, relative);
    private static string Visible(string value) => DoctorFacts.VisibleBounded(value, 512);

    /// A file of a pair, named by its relative path; equal for the same file whatever its partner.
    private sealed record Side(string Relative, string Partner)
    {
        public bool Equals(Side? other) => other is not null && StringComparer.Ordinal.Equals(Relative, other.Relative);
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Relative);
    }
    private sealed record FileRow(Side Side, string Path);
}
