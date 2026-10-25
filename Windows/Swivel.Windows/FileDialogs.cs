using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class ArchiveDialog : Window
{
    public ArchiveDialog(App app, ArchiveWork work, ArchiveFormat format)
    {
        Title = app.L("Create Archive"); Width = 630; Height = 520; MinWidth = 450; MinHeight = 320; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(UI.Text(format.ToString() + " · " + string.Format(app.L("{0} file(s), {1} MB of source data"), work.Paths.Count, (work.TotalBytes / 1048576.0).ToString("0.##", CultureInfo.CurrentCulture)), 20));
        panel.Children.Add(UI.Text(app.L("ZIP/TAR keep file contents and names; GZip stores compressed content without the original name. Folder structure, timestamps, permissions and alternate data streams are not archived. The new archive is saved beside the first file. Originals stay unchanged.")));
        foreach (string path in work.Paths) panel.Children.Add(UI.Text(path));
        var row = new WrapPanel(); row.Children.Add(UI.Button(app.L("Create Archive"), () => DialogResult = true));
        var cancel = UI.Button(app.L("Cancel"), () => DialogResult = false); cancel.IsCancel = true; row.Children.Add(cancel); panel.Children.Add(row); Content = UI.Scroll(panel);
    }
}

internal sealed class SubtitleDialog : Window
{
    public string EditedText { get; private set; } = "";
    public bool Transcript { get; private set; }
    public SubtitleDialog(App app, string text)
    {
        Title = app.L("Edit SRT…"); Width = 930; Height = 740; MinWidth = 650; MinHeight = 460; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(22) };
        var top = new StackPanel(); top.Children.Add(UI.Text(app.L("Edit text and cue times locally. Tags are shown as plain text. Saving creates a new file beside the original.")));
        var timing = new WrapPanel(); top.Children.Add(timing);
        TextBox Field(string label, string value) { timing.Children.Add(UI.Text(app.L(label))); var field = new TextBox { Text = value, Width = 85, Height = 30, Margin = new Thickness(8, 0, 12, 8) }; AutomationProperties.SetName(field, app.L(label)); timing.Children.Add(field); return field; }
        var offset = Field("Offset (seconds)", "0"); var speed = Field("Speed", "1");
        var editor = new TextBox { Text = text, AcceptsReturn = true, AcceptsTab = true, MaxLength = SubRip.MaxBytes, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 14 };
        AutomationProperties.SetName(editor, app.L("SRT text"));
        var footer = new StackPanel(); var status = UI.Text(""); footer.Children.Add(status); var buttons = new WrapPanel(); footer.Children.Add(buttons);
        void Attempt(Action action) { try { action(); } catch (UserError ex) { status.Text = app.L(ex.Message); } }
        void Validate() { var cues = SubRip.Parse(editor.Text); status.Text = string.Format(app.L("{0} valid cue(s). Overlapping cues are kept."), cues.Count); }
        timing.Children.Add(UI.Button(app.L("Apply Timing"), () => Attempt(() => {
            if (!double.TryParse(offset.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double seconds) || !double.TryParse(speed.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double rate)) throw new UserError("Choose an offset within 10 hours and speed from 0.25× to 4×.");
            string changed = SubRip.Write(SubRip.Shift(SubRip.Parse(editor.Text), seconds, rate)); SubRip.Parse(changed);
            editor.BeginChange(); try { editor.SelectAll(); editor.SelectedText = changed; } finally { editor.EndChange(); } offset.Text = "0"; speed.Text = "1"; Validate();
        })));
        top.Children.Add(UI.Text(app.L("Timing: new time = original time ÷ speed + offset. Apply updates the text; Ctrl+Z undoes text edits."), 12));
        buttons.Children.Add(UI.Button(app.L("Check SRT"), () => Attempt(Validate)));
        void Save(bool transcript) => Attempt(() => { var cues = SubRip.Parse(editor.Text); EditedText = SubRip.Write(cues); SubRip.Parse(EditedText); Transcript = transcript; DialogResult = true; });
        buttons.Children.Add(UI.Button(app.L("Save SRT"), () => Save(false))); buttons.Children.Add(UI.Button(app.L("Export Plain Text"), () => Save(true)));
        var cancel = UI.Button(app.L("Cancel"), () => DialogResult = false); cancel.IsCancel = true; buttons.Children.Add(cancel);
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); root.Children.Add(editor); Content = root;
    }
}
