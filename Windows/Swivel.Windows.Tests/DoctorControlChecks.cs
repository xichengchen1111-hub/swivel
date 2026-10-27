using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Swivel.Core;
using Swivel.Windows;

internal static class DoctorControlChecks
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        reject(() => DoctorNetworkReader.Read(new CancellationToken(true)), "Network check cancels before any native query");
        check(Marshal.SizeOf<DoctorNetworkReader.ProxyConfig>() == (IntPtr.Size == 8 ? 32 : 16), "WinHTTP BOOL/pointer structure has the platform layout");
        foreach (var page in Enum.GetValues<WindowsSettingsPage>())
        {
            var start = WindowsSettings.StartInfo(page);
            check(start.UseShellExecute && start.FileName.StartsWith("ms-settings:", StringComparison.Ordinal) && start.ArgumentList.Count == 0 && start.Arguments.Length == 0 && start.Verb.Length == 0,
                "Settings action uses a fixed local protocol without arguments or elevation");
        }
        var destinations = Enum.GetValues<WindowsSettingsPage>().Select(page => WindowsSettings.StartInfo(page).FileName).ToHashSet(StringComparer.Ordinal);
        check(destinations.SetEquals(new[] { "ms-settings:network-status", "ms-settings:network-proxy", "ms-settings:storagesense", "ms-settings:powersleep", "ms-settings:appsfeatures" }), "Settings destinations stay within the five approved local pages");
        reject(() => WindowsSettings.StartInfo((WindowsSettingsPage)999), "Unknown Settings page refused without activation");
        // Synthetic snapshot only: no App.Run, clipboard access, settings activation
        // or live diagnosis occurs while constructing/toggling this window.
        var app = Application.Current as App ?? new App();
        var facts = new[] { new DoctorFact("System", "{0}", new object[] { "Synthetic OS" }) }.Concat(
            DoctorNetwork.Describe(new("Fixture adapter", System.Net.NetworkInformation.OperationalStatus.Up, System.Net.NetworkInformation.NetworkInterfaceType.Ethernet,
                new[] { "192.0.2.25" }, new[] { "192.0.2.1" }, new[] { "203.0.113.53" }, true))).ToArray();
        var dialog = new DoctorDialog(app, facts);
        var toggle = Descendants(dialog).OfType<CheckBox>().Single(x => Equals(x.Content, app.L("Show network addresses and include them when copying")));
        check(toggle.IsChecked == false && !dialog.CurrentReport.Contains("192.0.2.25"), "Doctor window starts with addresses excluded from copied report");
        check(!Descendants(dialog).OfType<TextBlock>().Any(x => x.Text.Contains("192.0.2.25")), "Hidden addresses are absent from rendered control text");
        toggle.IsChecked = true;
        check(dialog.CurrentReport.Contains("192.0.2.25") && Descendants(dialog).OfType<TextBlock>().Any(x => x.Text.Contains("192.0.2.25")), "Explicit address choice changes both UI and copy source");
        toggle.IsChecked = false;
        check(!dialog.CurrentReport.Contains("192.0.2.25") && !Descendants(dialog).OfType<TextBlock>().Any(x => x.Text.Contains("192.0.2.25")), "Turning addresses off removes them from UI and copy source again");
        check(Descendants(dialog).OfType<TabControl>().Single().Items.Count == 2, "Doctor snapshot uses two native tabs");
        dialog.Close();
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
        { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }
}
