using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Swivel.Core;

namespace Swivel.Windows;

internal static class MacPresetInstall
{
    internal static string Text(App app) => app.L(MacPresetExport.InstallStart) + "\n\n" + MacPresetExport.Command + "\n\n" + app.L(MacPresetExport.InstallEnd);
    internal static TextBox Steps(App app)
    {
        var box = new TextBox { Text = Text(app), IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 250, MinHeight = 80 };
        AutomationProperties.SetName(box, app.L("Mac Install Steps")); return box;
    }
}

internal sealed class MacPresetExportDialog : Window
{
    internal bool MergeRequested { get; private set; }
    internal MacPresetExportDialog(App app, MacPresetExportPlan plan, string? macPath, CancellationToken token)
    {
        Title = app.L(MacPresetExport.Title); Width = 760; Height = 760; MinWidth = 440; MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) };
        var footer = new WrapPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var save = UI.Button(app.L("Choose Mac Export Folder…"), () => DialogResult = true); save.IsEnabled = plan.Count > 0; footer.Children.Add(save);
        footer.Children.Add(UI.Button(app.L("Choose Mac List to Merge…"), () => { MergeRequested = true; DialogResult = true; }));
        footer.Children.Add(UI.Button(app.L("Copy Mac Install Steps"), () => app.Try(() => { token.ThrowIfCancellationRequested(); Clipboard.SetText(MacPresetInstall.Text(app)); })));
        var cancel = UI.Button(app.L("Cancel"), () => DialogResult = false); cancel.IsCancel = true; footer.Children.Add(cancel);
        var body = new StackPanel(); root.Children.Add(UI.Scroll(body)); Content = root;
        foreach (string note in new[] { MacPresetExport.Scope, MacPresetExport.Replace, MacPresetExport.Equivalence, MacPresetExport.Bounds })
            body.Children.Add(UI.Text(app.L(note), 13));
        body.Children.Add(UI.Text(macPath is null ? app.L("No Mac list selected. Installation will keep only the exported Windows rows.") : app.L("Kept Mac list:") + " " + LibraryJson.Visible(macPath), 14));
        body.Children.Add(UI.Text(app.L(TextSimilarityNotices.Downgrade), 12));
        body.Children.Add(MacPresetInstall.Steps(app));
        body.Children.Add(UI.Text(string.Format(app.L("Presets to write: {0}. Refused Windows rows: {1}."), plan.Count, plan.Rows.Count(x => !x.Exported)), 18));
        if (plan.Count == 0) body.Children.Add(UI.Text(app.L(MacPresetExport.Empty)));
        body.Children.Add(UI.Text(app.L("Hidden characters are shown as [U+XXXX] in this preview."), 12));
        foreach (var row in plan.Rows)
        {
            body.Children.Add(UI.Text(LibraryJson.Visible(row.Name), 20));
            body.Children.Add(UI.Text("ID: " + LibraryJson.Visible(row.Id), 12));
            body.Children.Add(UI.Text(row.Description, 12));
            body.Children.Add(UI.Text(app.L(row.Kept ? "Kept unchanged from Mac" : row.Exported ? "Recipe values preserved for Mac" : "Not exported")));
            foreach (string issue in row.Issues) body.Children.Add(UI.Text(app.L(issue), 12));
        }
        SourceInitialized += (_, _) => WebRouteLayout.Fit(this, Owner ?? this); DpiChanged += (_, _) => WebRouteLayout.Fit(this, this);
    }
}

internal sealed class MacPresetInstallDialog : Window
{
    internal MacPresetInstallDialog(App app, string output, CancellationToken token)
    {
        Title = app.L("Mac Install Steps"); Width = 720; Height = 560; MinWidth = 440; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) };
        var footer = new WrapPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(UI.Button(app.L("Copy Mac Install Steps"), () => app.Try(() => { token.ThrowIfCancellationRequested(); Clipboard.SetText(MacPresetInstall.Text(app)); })));
        var close = UI.Button(app.L("Close"), () => DialogResult = false); close.IsCancel = true; footer.Children.Add(close);
        var body = new StackPanel(); body.Children.Add(UI.Text(app.L("Mac preset export saved."), 20));
        var path = new TextBox { Text = output, IsReadOnly = true, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(path, app.L("Exported JSON file")); body.Children.Add(path);
        body.Children.Add(UI.Text(app.L(MacPresetExport.Replace), 13)); body.Children.Add(MacPresetInstall.Steps(app));
        root.Children.Add(UI.Scroll(body)); Content = root;
        SourceInitialized += (_, _) => WebRouteLayout.Fit(this, Owner ?? this); DpiChanged += (_, _) => WebRouteLayout.Fit(this, this);
    }
}
