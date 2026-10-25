using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class DoctorDialog : Window
{
    private readonly App app;
    private readonly IReadOnlyList<DoctorFact> facts;
    private readonly IReadOnlyList<DoctorDriveSpace> driveSpace;
    private readonly DateTimeOffset captured = DateTimeOffset.Now;
    private readonly CheckBox addresses;
    private readonly TabControl tabs = new();
    private Task assistantPending=Task.CompletedTask;private AssistantDialog? assistant;private bool closed;
    internal string CurrentReport => DoctorReport.CopyText(facts, addresses.IsChecked == true, app.Prefs.Language, captured);
    public DoctorDialog(App app, IReadOnlyList<DoctorFact> facts,CancellationToken token=default,IReadOnlyList<DoctorDriveSpace>? driveSpace=null)
    {
        this.app = app; this.facts = facts; this.driveSpace = driveSpace ?? Array.Empty<DoctorDriveSpace>();
        Title = app.L("Windows Doctor"); Width = 840; Height = 760; MinWidth = 540; MinHeight = 460; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) };
        var header = new StackPanel();
        header.Children.Add(UI.Text(app.L("Read-only local snapshot. Names are included in the report; network addresses are hidden unless you choose to show them. Copy Report includes both tabs with the current address choice. Nothing is sent automatically.")));
        addresses = new CheckBox { Content = app.L("Show network addresses and include them when copying"), IsChecked = false, Margin = new Thickness(0, 6, 0, 12) };
        header.Children.Add(addresses); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var footer = new StackPanel();
        footer.Children.Add(UI.Text(app.L("These buttons only open the named Windows Settings pages. Swivel does not apply fixes."), 12));
        footer.Children.Add(WindowsSettings.Buttons(app));
        var row = new WrapPanel(); row.Children.Add(UI.Button(app.L("Copy Report"), () => app.Try(() => Clipboard.SetText(CurrentReport))));
        var explain=UI.Button(app.L("Explain Redacted Snapshot…"),()=>{});row.Children.Add(explain);
        explain.Click+=async(_,_)=>
        {
            if(!assistantPending.IsCompleted||closed)return;var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);assistantPending=completion.Task;
            try
            {
                token.ThrowIfCancellationRequested();if(!app.Prefs.IsOn(Feature.Assistant))throw new UserError("Enable Assistant in Settings first.");var settings=app.Prefs.Assistant??throw new UserError("Configure the assistant service in Assistant Settings first.");
                assistant=new AssistantDialog(app,settings,Array.Empty<AssistantChoice>(),Array.Empty<string>(),app.L("Explain this redacted snapshot and its limits. Do not apply fixes."),token,doctorFacts:facts){Owner=this};
                try{assistant.ShowDialog();}finally{await assistant.FinishAsync();}
            }
            catch(OperationCanceledException){}
            catch(Exception e){if(!closed)app.ShowError(e);}
            finally{assistant=null;completion.TrySetResult();}
        };
        Closed+=(_,_)=>{closed=true;assistant?.RequestClose();};
        var close = UI.Button(app.L("Close"), () => DialogResult = false); close.IsCancel = true; row.Children.Add(close); footer.Children.Add(row);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        root.Children.Add(tabs); Content = root;
        void Refresh()
        {
            int selected = tabs.SelectedIndex;
            tabs.Items.Clear();
            foreach (var section in DoctorReport.Visible(facts, addresses.IsChecked == true).GroupBy(x => x.Section))
            {
                var list = new StackPanel { Margin = new Thickness(14) };
                if (string.Equals(section.Key, "Fixed Drives", StringComparison.Ordinal) && this.driveSpace.Count > 0)
                {
                    list.Children.Add(UI.Text(app.L("Drive space is a one-time snapshot. Available-to-this-user bytes do not equal physical usage or reclaimable space."), 12));
                    var gauges = new WrapPanel { Margin = new Thickness(0, 10, 0, 8) };
                    foreach (var drive in this.driveSpace)
                    {
                        double used = (double)(((decimal)drive.Capacity - drive.AvailableToCaller) * 100 / drive.Capacity);
                        var card = new StackPanel { Width = 150, Margin = new Thickness(6), HorizontalAlignment = HorizontalAlignment.Center };
                        card.Children.Add(UI.Text(DoctorFacts.VisibleBounded(drive.Root, 16), 16));
                        var gauge = new UsageRingGauge(); gauge.Update(used);
                        AutomationProperties.SetName(gauge, app.L("Disk Space Usage Gauge"));
                        AutomationProperties.SetHelpText(gauge, string.Format(app.L("{0}: {1:F1}% of reported capacity is not available to this user"), drive.Root, used));
                        card.Children.Add(gauge);
                        card.Children.Add(UI.Text(string.Format(app.L("{0:F2} GiB available to this user of {1:F2} GiB"), drive.AvailableToCaller / 1073741824.0, drive.Capacity / 1073741824.0), 12));
                        gauges.Children.Add(card);
                    }
                    list.Children.Add(gauges);
                }
                foreach (var fact in section) { list.Children.Add(UI.Text(app.L(fact.Title), 18)); list.Children.Add(UI.Text(DoctorReport.Text(fact, app.Prefs.Language))); }
                tabs.Items.Add(new TabItem { Header = app.L(section.Key), Content = UI.Scroll(list), Padding = new Thickness(12, 7, 12, 7) });
            }
            tabs.SelectedIndex = Math.Clamp(selected, 0, Math.Max(0, tabs.Items.Count - 1));
        }
        addresses.Checked += (_, _) => Refresh(); addresses.Unchecked += (_, _) => Refresh(); Refresh();
    }
    internal async Task FinishAsync(){assistant?.RequestClose();await assistantPending;}
}
