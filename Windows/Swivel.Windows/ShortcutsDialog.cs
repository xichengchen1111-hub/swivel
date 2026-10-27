using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class ShortcutsDialog : Window
{
    public ShortcutsDialog(App app)
    {
        Title = app.L("Change Shortcuts…"); Width = 540; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(UI.Text(app.L("Click a shortcut field, then press the new combination. Another application may already own it; Swivel will report a conflict after saving.")));
        var shortcuts = new[] { app.Prefs.Shortcuts.Wheel, app.Prefs.Shortcuts.Quick, app.Prefs.Shortcuts.Custom };
        var boxes = new TextBox[3]; var error = UI.Text("");
        for (int index = 0; index < 3; index++)
        {
            int i = index; string label = app.L(i == 0 ? "Wheel" : i == 1 ? "Quick Actions" : "My Wheel");
            panel.Children.Add(UI.Text(label)); var field = boxes[i] = new TextBox { Text = shortcuts[i].Label, IsReadOnly = true, MinHeight = 32, Margin = new Thickness(0, 0, 0, 12) };
            System.Windows.Automation.AutomationProperties.SetName(field, label); panel.Children.Add(field);
            field.PreviewKeyDown += (_, e) => {
                var key = e.Key == Key.System ? e.SystemKey : e.Key;
                if (key is Key.Tab or Key.Escape) return;
                e.Handled = true;
                if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift) return;
                var modifier = Keyboard.Modifiers;
                uint mods = (modifier.HasFlag(ModifierKeys.Alt) ? 1u : 0) | (modifier.HasFlag(ModifierKeys.Control) ? 2u : 0) | (modifier.HasFlag(ModifierKeys.Shift) ? 4u : 0) | (modifier.HasFlag(ModifierKeys.Windows) ? 8u : 0);
                try { var shortcut = new Shortcut(mods, (uint)KeyInterop.VirtualKeyFromKey(key)); shortcut.Validate(); shortcuts[i] = shortcut; field.Text = shortcut.Label; error.Text = ""; }
                catch (UserError ex) { error.Text = app.L(ex.Message); }
            };
        }
        panel.Children.Add(UI.Text(app.L("Use at least two of Ctrl, Alt and Shift, plus a letter, number, Space or F1–F24.")));
        panel.Children.Add(error); var row = new WrapPanel();
        row.Children.Add(UI.Button(app.L("Reset Shortcuts"), () => { var defaults = ShortcutSet.Default; shortcuts = new[] { defaults.Wheel, defaults.Quick, defaults.Custom }; for (int i = 0; i < 3; i++) boxes[i].Text = shortcuts[i].Label; error.Text = ""; }));
        row.Children.Add(UI.Button(app.L("Save Shortcuts"), () => { try { app.Prefs.SetShortcuts(new(shortcuts[0],shortcuts[1],shortcuts[2])); DialogResult = true; } catch (UserError ex) { error.Text = app.L(ex.Message); } }));
        var cancel = UI.Button(app.L("Cancel"), () => DialogResult = false); cancel.IsCancel = true; row.Children.Add(cancel); panel.Children.Add(row); Content = UI.Scroll(panel);
    }
}
