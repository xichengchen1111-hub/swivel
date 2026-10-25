using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Swivel.Core;
using Swivel.Windows;

internal static class MoveFilesDialogNativeChecks
{
    private static IEnumerable<T> Find<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T target) yield return target;
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            foreach (var descendant in Find<T>(child)) yield return descendant;
    }

    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        var app = Application.Current as App ?? throw new InvalidOperationException("An isolated validation App is required.");
        if (!app.IsValidationHost) throw new InvalidOperationException("Do not run this fixture in a normal product App.");
        // No real paths are opened and no filesystem operation is requested.
        // Exercise only the production preview using immutable synthetic pairs.
        var source = @"C:\Fixture\source\file.txt";
        var target = @"C:\Fixture\destination\file.txt";
        var pairs = new[] { new MovePair(source, target), new MovePair(@"C:\Fixture\destination\unchanged.txt", @"C:\Fixture\destination\unchanged.txt") };
        var dialog = new MoveFilesDialog(app, pairs);
        var list = Find<ListView>(dialog).Single();
        var cancel = Find<Button>(dialog).Single(button => Equals(button.Content, app.L("Cancel")));
        var apply = Find<Button>(dialog).Single(button => Equals(button.Content, app.L("Move Files")));
        check(cancel.IsDefault && cancel.IsCancel && !apply.IsDefault && apply.IsEnabled,
            "File move preview defaults Enter/Escape to Cancel and never defaults to Move");
        check(list.Items.Count == 2 && list.SelectedIndex == -1, "Move preview lists all files without a selected row");
        check(list.SelectionMode == SelectionMode.Single && VirtualizingPanel.GetIsVirtualizing(list),
            "Move preview uses a virtualized single-row details selection");
        check(Find<TextBlock>(dialog).Any(text => text.Text == string.Format(app.L("Files listed: {0}. To move: {1}. Unchanged: {2}."), 2, 1, 1)),
            "Move preview counts actual moves separately from same-folder unchanged rows");
        pairs[0] = new MovePair(@"C:\Changed\source.txt", @"C:\Changed\target.txt");
        check(dialog.Plan[0].Source == source && dialog.Plan[0].Destination == target,
            "Move preview freezes caller path pairs before confirmation");
        list.SelectedIndex = 0;
        var details = Find<TextBox>(dialog).Single();
        check(details.IsReadOnly && details.Text.Contains(source, StringComparison.Ordinal) && details.Text.Contains(target, StringComparison.Ordinal),
            "Move selected details retain both complete paths");
        check(details.VerticalScrollBarVisibility == ScrollBarVisibility.Auto && !string.IsNullOrEmpty(AutomationProperties.GetName(details)),
            "Move details are scrollable and have an accessible name");
        check(dialog.Plan.Count == 2 && apply.IsEnabled, "Selecting one move row does not narrow the operation");
        var root = (DockPanel)dialog.Content;
        check(root.Children[0] is WrapPanel footer && DockPanel.GetDock(footer) == Dock.Bottom && root.Children[1] is ScrollViewer,
            "Move confirmation footer remains outside the scrolling body");
        list.SelectedIndex = 1;
        check(details.Text.Contains(app.L("Unchanged"), StringComparison.Ordinal), "Same-folder row explicitly says unchanged");
        dialog.Close(); check(dialog.DialogResult != true, "Closing a move preview does not confirm it");

        var noChange = new MoveFilesDialog(app, new[] { new MovePair(source, source) });
        check(!Find<Button>(noChange).Single(button => Equals(button.Content, app.L("Move Files"))).IsEnabled,
            "All-unchanged preview disables Move");
        noChange.Close();
        var undo = new MoveFilesDialog(app, new[] { new MovePair(target, source) }, true);
        check(Find<Button>(undo).Single(button => Equals(button.Content, app.L("Cancel"))).IsDefault &&
              !Find<Button>(undo).Single(button => Equals(button.Content, app.L("Undo File Move"))).IsDefault,
            "Undo move preview also defaults to Cancel");
        Find<ListView>(undo).Single().SelectedIndex = 0;
        check(Find<TextBox>(undo).Single().Text.Contains(app.L("Original path to restore") + ":\n" + source, StringComparison.Ordinal),
            "Undo preview labels the exact original path to restore");
        undo.Close();

        var full = new MoveFilesDialog(app, Enumerable.Range(1, 100).Select(index => new MovePair(
            @"C:\Fixture\source\" + index + ".txt", @"C:\Fixture\destination\" + index + ".txt")).ToArray());
        check(Find<ListView>(full).Single().Items.Count == 100 && full.Plan.Count == 100, "Move preview preserves all 100 rows");
        full.Close();
        reject(() => new MoveFilesDialog(app, Array.Empty<MovePair>()), "Empty file move preview is refused");
        reject(() => new MoveFilesDialog(app, Enumerable.Repeat(new MovePair(source, target), 101).ToArray()), "Over-budget file move preview is refused");
        reject(() => new MoveFilesDialog(app, new[] { new MovePair("relative.txt", target) }), "Move preview refuses non-local path display input");

        MoveDirectorySnapshot Directory(string path) => new(path, new[] { new MoveDirectoryEntry(path, new MoveIdentity(1, 1, 1)) });
        MoveLocation Location(string start, string current, bool verified = true) => new(start, current,
            verified ? new MoveFileStamp(new MoveIdentity(1, 2, 3), 10, 1, 2, 3, 0, 1) : null,
            Directory(MoveRules.Parent(start)), Directory(MoveRules.Parent(current)));
        var noOpResult = new MoveFilesResultDialog(app, new MoveOutcome(new[] { Location(source, source) }, null, false));
        check(Find<TextBlock>(noOpResult).Any(text => text.Text == app.L("No files moved.")) &&
            !Find<TextBlock>(noOpResult).Any(text => text.Text == app.L("Files moved. Session Undo is available.")),
            "An unchanged result does not claim a move or available Undo");
        noOpResult.Close();
        var partial = new MoveFilesResultDialog(app, new MoveOutcome(new[] { Location(source, target, false) },
            new InvalidOperationException("Failed\n\u202Epath"), true));
        check(Find<TextBlock>(partial).Any(text => text.Text == app.L("The operation stopped. Some files remain at changed paths; review every current path.")),
            "Partial rollback is prominent in file move results");
        Find<ListView>(partial).Single().SelectedIndex = 0;
        var resultDetails = Find<TextBox>(partial).Single(text => AutomationProperties.GetName(text) == app.L("Selected File Paths"));
        check(resultDetails.Text.Contains(source, StringComparison.Ordinal) && resultDetails.Text.Contains(target, StringComparison.Ordinal),
            "Failed move results retain the exact start and current locations");
        check(Find<TextBox>(partial).Single(text => AutomationProperties.GetName(text) == app.L("Reported problem")).Text == "Failed[U+000A][U+202E]path",
            "Move results visibly escape controls and direction formatting in error messages");
        check(Find<TextBlock>(partial).Any(text => text.Text == app.L("Some file identities could not be verified after the operation. Their paths remain listed, but session Undo may refuse these entries.")),
            "Unverified post-move identities retain their paths and warn about Undo refusal");
        check(Find<Button>(partial).Count() == 1 && Find<Button>(partial).Single().IsDefault && Find<Button>(partial).Single().IsCancel,
            "Move results have only a default Close action and never retry automatically");
        partial.Close();
        var restored = new MoveFilesResultDialog(app, new MoveOutcome(new[] { Location(target, target) }, new OperationCanceledException(), true), true);
        check(Find<TextBlock>(restored).Any(text => text.Text == app.L("The operation stopped. All recorded paths were restored to their state before this operation.")),
            "Cancelled Undo restoration is measured against that Undo's starting paths");
        restored.Close();
        var undone = new MoveFilesResultDialog(app, new MoveOutcome(new[] { Location(target, source) }, null, false), true);
        check(Find<TextBlock>(undone).Any(text => text.Text == app.L("File move undone.")), "Completed Undo reports the correct action");
        undone.Close();

        var nestedProblems = MoveFilesLayout.Problems(new AggregateException(new UserError("first"),
            new AggregateException(new UserError("second"), new InvalidOperationException("third\n\u202E"))));
        string translatedProblems = MoveFilesLayout.ProblemText(nestedProblems, message => "localized:" + message);
        check(nestedProblems.Count == 3 && translatedProblems.Contains("localized:first", StringComparison.Ordinal) &&
              translatedProblems.Contains("localized:second", StringComparison.Ordinal) &&
              translatedProblems.Contains("localized:third[U+000A][U+202E]", StringComparison.Ordinal),
            "Move results translate each nested failure individually and escape its visible text");
        var manyProblems = MoveFilesLayout.Problems(new AggregateException(Enumerable.Range(0, 202).Select(index => new InvalidOperationException("failure-" + index))));
        check(manyProblems.Count == 202 && MoveFilesLayout.ProblemText(manyProblems, message => message).Contains("failure-201", StringComparison.Ordinal),
            "Additional move cleanup diagnostics are not silently truncated after 100 failures");
    }
}
