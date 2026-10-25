using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class RenameDialog : Window
{
    public IReadOnlyList<RenamePair> Plan { get; private set; } = Array.Empty<RenamePair>();
    public RenameDialog(App app, IReadOnlyList<string> paths)
    {
        Title = app.L("Batch Rename"); Width = 850; Height = 700; MinWidth = 640; MinHeight = 550;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) };
        var controls = new StackPanel();
        controls.Children.Add(UI.Text(app.L("This changes original file names. Review every new name before renaming. Extensions are kept.")));
        TextBox Field(string label)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var title = new TextBlock { Text = app.L(label), Width = 130, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(title);
            var field = new TextBox { MaxLength = 120, MinHeight = 28 }; AutomationProperties.SetName(field, app.L(label));
            row.Children.Add(field); controls.Children.Add(row); return field;
        }
        var name = Field("Base name"); var find = Field("Find"); var replacement = Field("Replace with");
        var prefix = Field("Prefix"); var suffix = Field("Suffix");
        controls.Children.Add(UI.Text(app.L("Leave Base name empty to keep the original. Find is literal and case-sensitive. Numbering follows the displayed file order."), 12));
        var numbering = new CheckBox { Content = app.L("Add a number"), Margin = new Thickness(0, 7, 0, 7) }; controls.Children.Add(numbering);
        var start = Field("Start at"); start.Text = "1";
        var digits = Field("Digits"); digits.Text = "3";
        DockPanel.SetDock(controls, Dock.Top); root.Children.Add(controls);
        var footer = new StackPanel();
        var status = UI.Text(""); footer.Children.Add(status);
        footer.Children.Add(UI.Text(app.L("NTFS regular files only. Case-only changes, swaps and chains use temporary names in the same folder. Conflicts never overwrite files. If rollback fails, review the recovery paths. A crash may leave temporary names; undo history lasts only for this session."), 12));
        var buttons = new WrapPanel();
        var apply = UI.Button(app.L("Rename Files"), () => DialogResult = true); buttons.Children.Add(apply);
        var cancel = UI.Button(app.L("Cancel"), () => DialogResult = false); cancel.IsCancel = true; buttons.Children.Add(cancel);
        footer.Children.Add(buttons); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var list = new ListView(); var columns = new GridView();
        columns.Columns.Add(new GridViewColumn { Header = app.L("Current name"), DisplayMemberBinding = new Binding("Source"), Width = 355 });
        columns.Columns.Add(new GridViewColumn { Header = app.L("New name"), DisplayMemberBinding = new Binding("Destination"), Width = 355 });
        list.View = columns; root.Children.Add(list); Content = root;
        void Preview()
        {
            try
            {
                if (!int.TryParse(start.Text, out int first) || !int.TryParse(digits.Text, out int padding)) throw new UserError("Use text up to 120 characters, a start number from 0 to 99999999 and 1 to 8 digits.");
                Plan = new RenameRules(name.Text, find.Text, replacement.Text, prefix.Text, suffix.Text, numbering.IsChecked == true, first, padding).Build(paths);
                list.ItemsSource = Plan;
                int count = Plan.Count(x => x.Changed);
                apply.IsEnabled = count > 0;
                status.Text = string.Format(app.L("{0} file name(s) will change."), count);
            }
            catch (UserError ex) { Plan = Array.Empty<RenamePair>(); list.ItemsSource = null; apply.IsEnabled = false; status.Text = app.L(ex.Message); }
        }
        foreach (var field in new[] { name, find, replacement, prefix, suffix, start, digits }) field.TextChanged += (_, _) => Preview();
        numbering.Click += (_, _) => Preview(); Preview();
    }
}
internal sealed class UndoRenameDialog : Window
{
    public UndoRenameDialog(App app, IReadOnlyList<RenameLocation> receipt)
    {
        Title = app.L("Undo Rename"); Width = 760; Height = 460; MinWidth = 560; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) };
        var message = UI.Text(app.L("Review the names to restore. Undo refuses changed files and occupied original names."));
        DockPanel.SetDock(message, Dock.Top); root.Children.Add(message);
        var row = new WrapPanel();
        row.Children.Add(UI.Button(app.L("Undo Rename"), () => DialogResult = true));
        var cancel = UI.Button(app.L("Cancel"), () => DialogResult = false); cancel.IsCancel = true; row.Children.Add(cancel);
        DockPanel.SetDock(row, Dock.Bottom); root.Children.Add(row);
        var list = new StackPanel();
        foreach (var entry in receipt) list.Children.Add(UI.Text(entry.Current + "\n→ " + entry.Original));
        root.Children.Add(UI.Scroll(list)); Content = root;
    }
}
