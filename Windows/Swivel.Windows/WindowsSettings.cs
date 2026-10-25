using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Controls;
using Swivel.Core;

namespace Swivel.Windows;

internal enum WindowsSettingsPage { Network, Proxy, Storage, Power }
internal static class WindowsSettings
{
    // Closed local Windows protocol targets, never built from a report, file,
    // model response or saved setting. Starting Settings does not run a repair.
    public static ProcessStartInfo StartInfo(WindowsSettingsPage page) => new(page switch {
        WindowsSettingsPage.Network => "ms-settings:network-status",
        WindowsSettingsPage.Proxy => "ms-settings:network-proxy",
        WindowsSettingsPage.Storage => "ms-settings:storagesense",
        WindowsSettingsPage.Power => "ms-settings:powersleep",
        _ => throw new ArgumentOutOfRangeException(nameof(page))
    }) { UseShellExecute = true };
    public static void Open(WindowsSettingsPage page)
    {
        var start = StartInfo(page);
        try { using var process = Process.Start(start); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException)
        { throw new UserError("Windows could not open this Settings page. It may be unavailable or restricted on this system."); }
    }
    public static WrapPanel Buttons(App app)
    {
        var row = new WrapPanel();
        row.Children.Add(UI.Button(app.L("Open Network Settings"), () => app.Try(() => Open(WindowsSettingsPage.Network))));
        row.Children.Add(UI.Button(app.L("Open Proxy Settings"), () => app.Try(() => Open(WindowsSettingsPage.Proxy))));
        row.Children.Add(UI.Button(app.L("Open Storage Settings"), () => app.Try(() => Open(WindowsSettingsPage.Storage))));
        row.Children.Add(UI.Button(app.L("Open Power Settings"), () => app.Try(() => Open(WindowsSettingsPage.Power))));
        return row;
    }
}
