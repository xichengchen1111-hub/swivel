using System;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Swivel.Core;

namespace Swivel.Windows;

public sealed class MacQrLibraryFile : IDisposable
{
    private readonly NativeFiles.Input input;
    public MacQrImport Import { get; }
    private MacQrLibraryFile(NativeFiles.Input input, MacQrImport import) { this.input = input; Import = import; }
    public static MacQrLibraryFile Open(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var input = NativeFiles.OpenRead(path);
        try
        {
            if (input.Stream.Length > MacQrLibrary.MaxBytes) throw new UserError("Choose a valid Mac Swivel QR library JSON (256 KB, up to 128 entries).");
            byte[] bytes = new byte[checked((int)input.Stream.Length)]; input.Stream.ReadExactly(bytes);
            token.ThrowIfCancellationRequested(); return new(input, MacQrLibrary.Decode(bytes));
        }
        catch { input.Dispose(); throw; }
    }
    public void Dispose() => input.Dispose();
}

internal sealed class MacQrLibraryPreview : Window
{
    public MacQrLibraryPreview(App app, MacQrImport import, int additions, string? mergeIssue)
    {
        Title = app.L("Import Mac QR Library…"); Width = 700; Height = 650; MinWidth = 440; MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) };
        var actions = new WrapPanel();
        var apply = UI.Button(app.L("Import Compatible QR Entries"), () => DialogResult = true);
        apply.IsEnabled = additions > 0 && mergeIssue is null; actions.Children.Add(apply);
        var cancel = UI.Button(app.L("Cancel"), () => DialogResult = false); cancel.IsCancel = true; actions.Children.Add(cancel);
        DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
        var body = new StackPanel();
        body.Children.Add(UI.Text(app.L("Review the full Mac QR library before importing. Only compatible names, content and collections are added; nothing is shortened. Windows uses new local IDs and keeps existing entries and feature switches.")));
        body.Children.Add(UI.Text(app.L("Known Mac seasons and templates are imported as Windows garden choices; creation dates are not imported. Unknown choices are marked per entry. Windows relief scenes differ from the Mac 3D shapes. Compatible entries retain M error correction and exact content; no links open.")));
        body.Children.Add(UI.Text(app.L("Hidden characters are shown as [U+XXXX] in this preview."), 12));
        if (mergeIssue is not null) body.Children.Add(UI.Text(app.L(mergeIssue)));
        else body.Children.Add(UI.Text(string.Format(app.L("{0} new QR entries will be added."), additions), 18));
        foreach (var row in import.Rows)
        {
            body.Children.Add(UI.Text(LibraryJson.Visible(row.Name), 20));
            if (row.Group.Length > 0) body.Children.Add(UI.Text(LibraryJson.Visible(row.Group), 12));
            var text = new TextBox { Text = LibraryJson.Visible(row.Text), IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                MaxHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 0, 10) };
            AutomationProperties.SetName(text, app.L("QR Text")); body.Children.Add(text);
            body.Children.Add(UI.Text(app.L("Garden Season")+": "+LibraryJson.Visible(row.Season)+" · "+app.L("Garden Template")+": "+LibraryJson.Visible(row.Template),12));
            body.Children.Add(UI.Text(row.Issue is null ? app.L("Compatible") : app.L("Not imported") + " · " + app.L(row.Issue)));
        }
        root.Children.Add(UI.Scroll(body)); Content = root;
    }
}
