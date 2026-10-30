using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class BatteryHealthDialog : Window
{
    private readonly App app;
    private readonly BatterySnapshot snapshot;
    private readonly ListView list = new() { Height = 420, SelectionMode = SelectionMode.Single };
    internal CheckBox Reveal { get; } = new() { Margin = new Thickness(0, 8, 0, 12) };
    internal string CurrentReport { get; private set; } = "";
    internal IReadOnlyList<Row> Rows { get; private set; } = Array.Empty<Row>();

    internal BatteryHealthDialog(App app, BatterySnapshot snapshot)
    {
        this.app = app; this.snapshot = snapshot;
        Title = app.L("Battery Health"); Width = 900; Height = 800; MinWidth = 500; MinHeight = 380; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) }; var footer = new WrapPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(UI.Button(app.L("Copy Report"), () => app.Try(() => Clipboard.SetText(CurrentReport))));
        var close = UI.Button(app.L("Close"), () => DialogResult = false); close.IsCancel = true; footer.Children.Add(close);
        var body = new StackPanel(); root.Children.Add(UI.Scroll(body)); Content = root;
        body.Children.Add(UI.Text(snapshot.Captured.ToString("g", CultureInfo.CurrentCulture), 14));
        body.Children.Add(UI.Text(app.L(Headline()), 16));
        Reveal.Content = app.L("Show device names, manufacturers and serial numbers"); Reveal.IsChecked = false;
        Reveal.Checked += (_, _) => Render(); Reveal.Unchecked += (_, _) => Render(); body.Children.Add(Reveal);
        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = app.L("Field"), DisplayMemberBinding = new Binding(nameof(Row.Name)), Width = 380 });
        view.Columns.Add(new GridViewColumn { Header = app.L("Value"), DisplayMemberBinding = new Binding(nameof(Row.Value)), Width = 440 });
        list.View = view; VirtualizingPanel.SetIsVirtualizing(list, true); VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling); ScrollViewer.SetCanContentScroll(list, true);
        AutomationProperties.SetName(list, Title); body.Children.Add(list);
        foreach (string note in new[] { BatteryHealth.Scope, BatteryHealth.Limits }) body.Children.Add(UI.Text(app.L(note), 12));
        Render();
        SourceInitialized += (_, _) => WebRouteLayout.Fit(this, Owner ?? this); DpiChanged += (_, _) => WebRouteLayout.Fit(this, this);
    }

    private string Headline()
    {
        if (snapshot.EnumerationError is not null && snapshot.Batteries.Count == 0) return "Windows could not list battery devices.";
        if (snapshot.NoBattery) return "Windows reports no battery in this PC.";
        var classes = snapshot.Batteries.Select(b => BatteryHealth.Classify(b.Information).Class).ToArray();
        if (classes.All(c => c == BatteryHealthClass.Unknown)) return "Battery health could not be determined from the reported values.";
        return classes.Any(c => c == BatteryHealthClass.Worn) ? "A battery holds much less charge than when new."
            : classes.Any(c => c == BatteryHealthClass.Reduced) ? "A battery holds noticeably less charge than when new."
            : "Reported battery capacity looks normal.";
    }

    private void Render()
    {
        bool reveal = Reveal.IsChecked == true; var rows = new List<Row>();
        // Formatted values carry only digits and units; their English words are translated here.
        string Tr(string text) => text.Replace("Not reported", app.L("Not reported")).Replace("(relative units)", app.L("(relative units)")).Replace("(units unknown)", app.L("(units unknown)")).Replace("Unknown", app.L("Unknown"));
        string Code(uint code) => app.L("Windows Error Code") + " " + code.ToString(CultureInfo.InvariantCulture);
        if (snapshot.EnumerationError is uint enumError) rows.Add(new(app.L("Battery Device List"), app.L("Incomplete") + " · " + Code(enumError)));
        if (snapshot.NoBattery) rows.Add(new(app.L("Batteries"), app.L("None reported")));
        if (snapshot.EmptySlots > 0) rows.Add(new(app.L("Empty Battery Bays"), snapshot.EmptySlots.ToString(CultureInfo.InvariantCulture)));
        if (snapshot.Truncated) rows.Add(new(app.L("Battery Device List"), string.Format(app.L("Only the first {0} batteries are shown."), BatteryHealth.MaxBatteries)));
        int number = 0;
        foreach (var battery in snapshot.Batteries)
        {
            string prefix = string.Format(app.L("Battery {0}"), ++number) + " · ";
            void Add(string name, string value) => rows.Add(new(prefix + app.L(name), value));
            if (battery.OpenError is uint open) { Add("Battery Unavailable", Code(open)); continue; }
            if (battery.TagError is uint tag) { Add("Battery Unavailable", Code(tag)); continue; }
            var info = battery.Information; var health = BatteryHealth.Classify(info);
            Add("Health", app.L(BatteryHealth.Name(health.Class)) + (health.Percent is double p ? " · " + BatteryHealth.Percent(p) : ""));
            if (health.Reason != BatteryUnknownReason.None) Add("Why Unknown", app.L(BatteryHealth.Reason(health.Reason)));
            if (info is null) Add("Battery Information", app.L("Unavailable") + (battery.InformationError is uint ie ? " · " + Code(ie) : ""));
            else
            {
                Add("Designed Capacity", Tr(BatteryHealth.Capacity(info.DesignedCapacity, info.Capabilities)));
                Add("Full-charge Capacity", Tr(BatteryHealth.Capacity(info.FullChargedCapacity, info.Capabilities)));
                Add("Cycle Count", Tr(BatteryHealth.Cycles(info.CycleCount)));
                Add("Chemistry", info.Chemistry.Length == 0 ? app.L("Not reported") : info.Chemistry);
                Add("Technology", app.L(BatteryHealth.Technology(info.Technology)));
                Add("Battery Kind", app.L(BatteryHealth.Kind(info.Capabilities)) + " (0x" + info.Capabilities.ToString("X8", CultureInfo.InvariantCulture) + ")");
            }
            if (battery.Status is not BatteryStatus status) Add("Current Status", app.L("Unavailable") + (battery.StatusError is uint se ? " · " + Code(se) : ""));
            else
            {
                var states = BatteryHealth.PowerStates(status.PowerState);
                Add("Power State", (states.Count == 0 ? app.L("Not reported") : string.Join(", ", states.Select(app.L))) + " (" + status.PowerState.ToString(CultureInfo.InvariantCulture) + ")");
                Add("Remaining Capacity", Tr(BatteryHealth.RemainingCapacity(status.Capacity, info?.Capabilities)) + (BatteryHealth.ChargePercent(info, status) is double c ? " · " + BatteryHealth.Percent(c) : ""));
                Add("Voltage", Tr(BatteryHealth.Voltage(status.Voltage)));
                Add("Charge or Discharge Rate", Tr(BatteryHealth.Rate(status.Rate, info?.Capabilities)));
            }
            foreach (var (name, text) in new[] { ("Device Name", battery.DeviceName), ("Manufacturer", battery.Manufacturer), ("Serial Number", battery.SerialNumber) })
                Add(name, !reveal ? app.L("Hidden") : text is null ? app.L("Unavailable") : text.Value is string v ? (v.Length == 0 ? app.L("Not reported") : v) : app.L("Not reported") + " · " + Code(text.Error ?? 0));
        }
        Rows = rows; list.ItemsSource = rows;
        var report = new StringBuilder();
        report.AppendLine(app.L("Windows Doctor") + " · " + Title); report.AppendLine(snapshot.Captured.ToString("g", CultureInfo.CurrentCulture)); report.AppendLine(app.L(Headline()));
        foreach (var row in rows) report.AppendLine(row.Name + ": " + row.Value);
        foreach (string note in new[] { BatteryHealth.Scope, BatteryHealth.Limits }) { report.AppendLine(); report.AppendLine(app.L(note)); }
        CurrentReport = report.ToString();
    }

    internal sealed record Row(string Name, string Value);
}
