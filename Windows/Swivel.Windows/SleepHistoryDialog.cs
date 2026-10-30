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

internal sealed class SleepHistoryDialog : Window
{
    internal string CurrentReport { get; } = "";
    internal IReadOnlyList<Row> AwakeRows { get; }
    internal IReadOnlyList<Row> SummaryRows { get; }
    internal IReadOnlyList<EventRow> EventRows { get; }
    internal IReadOnlyList<Button> CommandButtons { get; }

    internal SleepHistoryDialog(App app, StayAwakeSnapshot awake, SleepHistorySnapshot history)
    {
        Title = app.L("Sleep and Wake History"); Width = 960; Height = 860; MinWidth = 520; MinHeight = 400; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) }; var footer = new WrapPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(UI.Button(app.L("Copy Report"), () => app.Try(() => Clipboard.SetText(CurrentReport))));
        var close = UI.Button(app.L("Close"), () => DialogResult = false); close.IsCancel = true; footer.Children.Add(close);
        var body = new StackPanel(); root.Children.Add(UI.Scroll(body)); Content = root;
        string When(DateTimeOffset t) => t.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        string Hex(uint v) => "0x" + v.ToString("X8", CultureInfo.InvariantCulture);
        string Duration(TimeSpan span) => span.TotalHours >= 1
            ? string.Format(app.L("{0} h {1} min"), (long)span.TotalHours, span.Minutes)
            : string.Format(app.L("{0} min {1} s"), span.Minutes, span.Seconds);

        var report = new StringBuilder();
        report.AppendLine(app.L("Windows Doctor") + " · " + Title); report.AppendLine(When(awake.Captured));
        body.Children.Add(UI.Text(When(awake.Captured), 14));

        // What may be keeping the PC awake.
        var awakeRows = new List<Row>();
        awakeRows.Add(new(app.L("Swivel Keep Awake"), !awake.KeepAwakeActive ? app.L("Off")
            : (awake.KeepAwakeUntil is DateTimeOffset until ? string.Format(app.L("On until {0}"), When(until)) : app.L("On until turned off"))
              + (awake.KeepAwakeDisplay ? " · " + app.L("Screen kept on") : "")));
        if (awake.ExecutionState is uint state)
        {
            var flags = SleepHistory.ExecutionFlags(state);
            awakeRows.Add(new(app.L("Reported System Execution State"), (flags.Count == 0 ? app.L("No flags set") : string.Join(", ", flags.Select(app.L))) + " (" + Hex(state) + ")"));
        }
        else awakeRows.Add(new(app.L("Reported System Execution State"), app.L("Unavailable") + " · NTSTATUS " + Hex(unchecked((uint)(awake.ExecutionStateStatus ?? 0)))));
        awakeRows.Add(new(app.L("Apps and Drivers Preventing Sleep"), app.L("Listed by Windows only to an administrator; not read here.")));
        AwakeRows = awakeRows;
        Section(app.L("What May Be Keeping This PC Awake"), awakeRows);
        body.Children.Add(UI.Text(app.L(SleepHistory.ExecutionStateNote), 12)); report.AppendLine(app.L(SleepHistory.ExecutionStateNote));

        // Administrator commands: text only, never run.
        body.Children.Add(UI.Text(app.L("Administrator Commands (Not Run)"), 16)); report.AppendLine(); report.AppendLine(app.L("Administrator Commands (Not Run)"));
        body.Children.Add(UI.Text(app.L(SleepHistory.AdminNote), 12)); report.AppendLine(app.L(SleepHistory.AdminNote));
        var buttons = new List<Button>();
        foreach (var (command, purpose) in new[] {
            (SleepHistory.RequestsCommand, "Lists the apps, services and drivers currently asking the PC or display to stay awake."),
            (SleepHistory.LastWakeCommand, "Shows what last woke the PC from sleep.") })
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 6) };
            var copy = UI.Button(app.L("Copy Command"), () => app.Try(() => Clipboard.SetText(command))); AutomationProperties.SetName(copy, app.L("Copy Command") + " " + command);
            DockPanel.SetDock(copy, Dock.Right); row.Children.Add(copy); buttons.Add(copy);
            var box = new TextBox { Text = command, IsReadOnly = true, FontFamily = new System.Windows.Media.FontFamily("Consolas"), VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            AutomationProperties.SetName(box, app.L(purpose)); row.Children.Add(box);
            body.Children.Add(row); body.Children.Add(UI.Text(app.L(purpose), 12));
            report.AppendLine(command + " — " + app.L(purpose));
        }
        CommandButtons = buttons;

        // Sleep and wake history.
        var summaryRows = new List<Row> { new(app.L("System Event Log"), app.L(SleepHistory.Status(history.Status)) + (history.ErrorCode is int code ? " · HRESULT " + Hex(unchecked((uint)code)) : "")) };
        var eventRows = new List<EventRow>();
        if (history.Status == SleepLogStatus.Read)
        {
            var summary = SleepHistory.Summarize(history.Events);
            summaryRows.Add(new(app.L("Period"), string.Format(app.L("Last {0} days, from {1}"), SleepHistory.Days, When(history.WindowStart))));
            summaryRows.Add(new(app.L("Times Entering Sleep"), summary.Sleeps.ToString(CultureInfo.InvariantCulture)));
            summaryRows.Add(new(app.L("Times Resumed"), summary.Resumes.ToString(CultureInfo.InvariantCulture)));
            summaryRows.Add(new(app.L("Low-power Returns with Wake Source"), summary.Wakes.ToString(CultureInfo.InvariantCulture)));
            summaryRows.Add(new(app.L("Last Entered Sleep"), summary.LastSleep is DateTimeOffset ls ? When(ls) : app.L("None recorded")));
            summaryRows.Add(new(app.L("Last Woke"), summary.LastResume is DateTimeOffset lw ? When(lw) : app.L("None recorded")));
            summaryRows.Add(new(app.L("Last Recorded Wake Source"), summary.LastWakeSource is string src ? Source(src) : app.L("None recorded")));
            summaryRows.Add(new(app.L("Longest Recorded Sleep"), summary.LongestSleep is TimeSpan longest ? Duration(longest) : app.L("None recorded")));
            foreach (var source in summary.WakeSources) summaryRows.Add(new(app.L("Wake Source"), Source(source.Source) + " · " + string.Format(app.L("{0} times"), source.Count)));
            if (history.Events.Count == 0) summaryRows.Add(new(app.L("Events"), app.L("No sleep or wake events were recorded in this period.")));
            if (history.Truncated) summaryRows.Add(new(app.L("Events"), string.Format(app.L("Only the newest {0} events are shown."), SleepHistory.MaxEvents)));
            if (history.TimedOut) summaryRows.Add(new(app.L("Events"), app.L("Reading stopped at the time limit; older events may be missing.")));
            foreach (var e in history.Events)
            {
                var details = new List<string>();
                if (e.WakeSource is not null) details.Add(app.L("Wake Source") + ": " + Source(SleepHistory.Source(e.WakeSource)));
                if (e.SleepTime is DateTimeOffset s && e.WakeTime is DateTimeOffset w && w >= s) details.Add(app.L("Slept") + ": " + Duration(w - s));
                if (e.TargetState is long target) details.Add(app.L("Target State (raw)") + ": " + target.ToString(CultureInfo.InvariantCulture));
                if (e.Reason is long reason) details.Add(app.L("Reason (raw)") + ": " + reason.ToString(CultureInfo.InvariantCulture));
                eventRows.Add(new(When(e.Time), app.L(SleepHistory.Name(e.Kind)), string.Join(" · ", details)));
            }
        }
        SummaryRows = summaryRows; EventRows = eventRows;
        Section(app.L("Sleep and Wake Summary"), summaryRows);

        var list = new ListView { Height = 300, ItemsSource = eventRows, SelectionMode = SelectionMode.Single };
        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = app.L("Time"), DisplayMemberBinding = new Binding(nameof(EventRow.Time)), Width = 170 });
        view.Columns.Add(new GridViewColumn { Header = app.L("Event"), DisplayMemberBinding = new Binding(nameof(EventRow.Event)), Width = 330 });
        view.Columns.Add(new GridViewColumn { Header = app.L("Details"), DisplayMemberBinding = new Binding(nameof(EventRow.Details)), Width = 380 });
        list.View = view; VirtualizingPanel.SetIsVirtualizing(list, true); VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling); ScrollViewer.SetCanContentScroll(list, true);
        AutomationProperties.SetName(list, app.L("Sleep and Wake Events")); body.Children.Add(UI.Text(app.L("Sleep and Wake Events"), 16)); body.Children.Add(list);
        report.AppendLine(); report.AppendLine(app.L("Sleep and Wake Events"));
        foreach (var row in eventRows) report.AppendLine(row.Time + " · " + row.Event + (row.Details.Length > 0 ? " · " + row.Details : ""));

        foreach (string note in new[] { SleepHistory.Scope, SleepHistory.Limits }) { body.Children.Add(UI.Text(app.L(note), 12)); report.AppendLine(); report.AppendLine(app.L(note)); }
        CurrentReport = report.ToString();
        SourceInitialized += (_, _) => WebRouteLayout.Fit(this, Owner ?? this); DpiChanged += (_, _) => WebRouteLayout.Fit(this, this);

        string Source(string text) => text == "Not recorded" ? app.L("Not recorded") : text;
        void Section(string heading, List<Row> rows)
        {
            body.Children.Add(UI.Text(heading, 16));
            var grid = new ListView { ItemsSource = rows, SelectionMode = SelectionMode.Single };
            var columns = new GridView();
            columns.Columns.Add(new GridViewColumn { Header = app.L("Field"), DisplayMemberBinding = new Binding(nameof(Row.Name)), Width = 330 });
            columns.Columns.Add(new GridViewColumn { Header = app.L("Value"), DisplayMemberBinding = new Binding(nameof(Row.Value)), Width = 550 });
            grid.View = columns; AutomationProperties.SetName(grid, heading); body.Children.Add(grid);
            report.AppendLine(); report.AppendLine(heading);
            foreach (var row in rows) report.AppendLine(row.Name + ": " + row.Value);
        }
    }

    internal sealed record Row(string Name, string Value);
    internal sealed record EventRow(string Time, string Event, string Details);
}
