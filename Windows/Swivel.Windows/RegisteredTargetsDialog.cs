using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class RegisteredTargetsDialog : Window
{
    internal string CurrentReport { get; }
    internal RegisteredTargetsDialog(App app, FolderRegistrations result)
    {
        Title = app.L(RegisteredTargets.Title); Width = 1000; Height = 760; MinWidth = 520; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CurrentReport = RegisteredTargets.Report(result, app.L);
        var root = new DockPanel { Margin = new Thickness(20) };
        var footer = new WrapPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(UI.Button(app.L("Open Installed Apps in Settings"), () => app.Try(() => WindowsSettings.Open(WindowsSettingsPage.Apps))));
        footer.Children.Add(UI.Button(app.L("Copy Report"), () => app.Try(() => Clipboard.SetText(CurrentReport))));
        var close = UI.Button(app.L("Close"), () => DialogResult = false); close.IsCancel = true; footer.Children.Add(close);
        var text = new TextBox { Text = CurrentReport, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetName(text, app.L(RegisteredTargets.Title)); root.Children.Add(text); Content = root;
        SourceInitialized += (_, _) => WebRouteLayout.Fit(this, Owner ?? this); DpiChanged += (_, _) => WebRouteLayout.Fit(this, this);
    }
}
