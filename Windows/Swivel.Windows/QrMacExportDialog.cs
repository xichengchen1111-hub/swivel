using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class QrMacExportDialog : Window
{
    public QrMacExportDialog(App app, QrMacExportPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Title = app.L(QrMacExport.Title);
        Width = 740; Height = 720; MinWidth = 430; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) };
        var footer = new WrapPanel();
        var apply = UI.Button(app.L("Choose QR Export Folder…"), () => DialogResult = true);
        apply.IsEnabled = plan.Compatible.Count > 0;
        footer.Children.Add(apply);
        var cancel = UI.Button(app.L("Cancel"), () => DialogResult = false);
        cancel.IsCancel = true; footer.Children.Add(cancel);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var body = new StackPanel();
        foreach (string note in new[] { QrMacExport.Scope, QrMacExport.MetadataLimit, QrMacExport.CorrectionLimit,
                     QrMacExport.DuplicateLimit, QrMacExport.Privacy, QrMacExport.OutputLimit })
            body.Children.Add(UI.Text(app.L(note), 13));
        body.Children.Add(UI.Text(string.Format(CultureInfo.CurrentCulture,
            app.L("Compatible PNGs: {0}. Not exported: {1}."), plan.Compatible.Count, plan.Rows.Count - plan.Compatible.Count), 18));
        body.Children.Add(UI.Text(app.L("Hidden characters are shown as [U+XXXX] in this preview."), 12));
        foreach (var row in plan.Rows)
        {
            body.Children.Add(UI.Text(LibraryJson.Visible(row.Entry.Name), 20));
            body.Children.Add(UI.Text(app.L("Group") + ": " + LibraryJson.Visible(row.Entry.Group), 12));
            body.Children.Add(UI.Text(app.L("Garden Template") + ": " + app.L(QrGarden.TemplateName(row.Entry.Template)) +
                " · " + app.L("Garden Season") + ": " + app.L(QrGarden.SeasonName(row.Entry.Season)), 12));
            body.Children.Add(UI.Text(string.Format(CultureInfo.CurrentCulture,
                app.L("Error correction: {0}"), QrCode.Level(row.Entry.Correction)) + " · " +
                string.Format(CultureInfo.CurrentCulture, app.L("{0} UTF-8 bytes"), row.Utf8Bytes), 12));
            var text = new TextBox { Text = LibraryJson.Visible(row.Entry.Text), IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap, MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 0, 0, 10) };
            AutomationProperties.SetName(text, app.L("QR Text")); body.Children.Add(text);
            body.Children.Add(UI.Text(row.Compatible ? app.L("Compatible") + " · " + row.SuggestedFileName : app.L("Not exported")));
            foreach (string issue in row.Issues) body.Children.Add(UI.Text(app.L(issue), 12));
            if (row.Compatible && row.RepeatedPayload) body.Children.Add(UI.Text(app.L(QrMacExport.Repeated), 12));
        }
        root.Children.Add(UI.Scroll(body)); Content = root;
        SourceInitialized += (_, _) => WebRouteLayout.Fit(this, Owner ?? this);
        DpiChanged += (_, _) => WebRouteLayout.Fit(this, this);
    }
}
