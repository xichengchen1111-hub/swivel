using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Win32;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class AppRouteDialog : Window
{
    public AppRouteDialog(App app, AppRoute? existing = null)
    {
        Title = app.L(existing is null ? "Add Application…" : "Edit Application…"); Width = 620; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) };
        TextBox Field(string label,string text) { panel.Children.Add(UI.Text(app.L(label))); var box = new TextBox { Text = text, MinHeight = 30 }; AutomationProperties.SetName(box,app.L(label)); panel.Children.Add(box); return box; }
        var name = Field("Application Name",existing?.Name ?? ""); name.MaxLength = 48;
        var executable = Field("Application Path",existing?.Executable ?? ""); executable.IsReadOnly = true;
        panel.Children.Add(UI.Button(app.L("Choose Application…"), () => {
            var picker = new OpenFileDialog { Filter = app.L("Windows applications (*.exe)|*.exe"), Multiselect = false, CheckFileExists = true };
            if (picker.ShowDialog(this) == true) { executable.Text = picker.FileName; if (string.IsNullOrWhiteSpace(name.Text)) name.Text = System.IO.Path.GetFileNameWithoutExtension(picker.FileName); }
        }));
        var types = Field("File extensions (optional)",existing?.Extensions ?? ""); types.MaxLength = 256;
        var copy = new CheckBox { Content = app.L("Copy files to clipboard and open app"),IsChecked = existing?.CopyToClipboard ?? false,Margin = new Thickness(0,12,0,8) }; panel.Children.Add(copy);
        panel.Children.Add(UI.Text(app.L("With this option, the app receives no file arguments. Swivel replaces the clipboard with file references after confirmation; you paste them yourself. It does not duplicate files or paste automatically."),12));
        panel.Children.Add(UI.Text(app.L("Enter up to 16 extensions separated by commas, such as png,jpg. Leave blank for any file.")));
        panel.Children.Add(UI.Text(app.L("Add only an application you trust. Swivel does not verify its publisher. Saving this entry does not launch it; each launch shows the exact application and file paths.")));
        var error = UI.Text(""); panel.Children.Add(error); var row = new WrapPanel();
        row.Children.Add(UI.Button(app.L("Save Application"), () => { try {
            var route = new AppRoute(existing?.Id ?? Guid.NewGuid().ToString("N"),name.Text.Trim(),executable.Text,types.Text.Trim(),copy.IsChecked == true); route.Validate();
            using (var input = NativeFiles.OpenRead(route.Executable)) AppLaunch.ValidateExecutable(input);
            app.Prefs.SaveApp(route); DialogResult = true;
        } catch (Exception ex) { if (ex is UserError) error.Text = app.L(ex.Message); else app.ShowError(ex); } }));
        var cancel = UI.Button(app.L("Cancel"), () => DialogResult = false); cancel.IsCancel = true; row.Children.Add(cancel); panel.Children.Add(row); Content = UI.Scroll(panel);
    }
}
internal sealed class LaunchDialog : Window
{
    public LaunchDialog(App app, AppLaunch launch)
    {
        Title = string.Format(app.L("Open with {0}"),launch.Route.Name); Width = 700; Height = 540; MinWidth = 480; MinHeight = 320; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(UI.Text(launch.Route.Executable,18));
        panel.Children.Add(UI.Text(app.L(launch.Route.CopyToClipboard ? "These file references will replace the clipboard. Swivel opens this application with no file arguments; paste them yourself. If the app fails to open, the files remain on the clipboard." : "These paths will be passed directly to this application. It controls subsequent reads, edits and network access, and may modify the originals. No shell command or extra arguments are added.")));
        foreach (string file in launch.Files) panel.Children.Add(UI.Text(file));
        var row = new WrapPanel(); row.Children.Add(UI.Button(app.L(launch.Route.CopyToClipboard ? "Copy Files and Open Application" : "Open Files in Application"), () => DialogResult = true)); var cancel = UI.Button(app.L("Cancel"), () => DialogResult = false); cancel.IsCancel = true; row.Children.Add(cancel); panel.Children.Add(row); Content = UI.Scroll(panel);
    }
}
