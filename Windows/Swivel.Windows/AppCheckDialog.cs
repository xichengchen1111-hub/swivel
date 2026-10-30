using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Swivel.Core;

namespace Swivel.Windows;

// Shows what was found when the file was opened. Two buttons can do more, and each says so:
// Check Revocation Online (contacts the internet) and, only for a validly signed trusted file
// that carries a download warning, Remove Download Warning… after a confirmation.
internal sealed class AppCheckDialog : Window
{
    private readonly App app;
    private readonly AppCheckFile file;
    private readonly StackPanel findings = new(), zonePanel = new();
    private readonly ListView signature;
    private readonly TextBlock online;
    private readonly CheckBox reveal;
    private readonly TextBox addresses;
    private readonly Button onlineButton;
    private Task pending = Task.CompletedTask;
    private bool closed;
    internal Button? RemoveButton { get; private set; }
    /// Replaced by tests; the default asks with a warning that names the risk.
    internal Func<bool> ConfirmRemove { get; set; }
    internal bool Removed { get; private set; }

    internal AppCheckDialog(App app, AppCheckFile file)
    {
        this.app = app; this.file = file;
        ConfirmRemove = () => MessageBox.Show(this, app.L(AppCheck.RemoveRisk), app.L("Remove Download Warning…"), MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) == MessageBoxResult.OK;
        Title = app.L("Why an App Won't Open"); Width = 920; Height = 860; MinWidth = 520; MinHeight = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) }; var footer = new WrapPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        onlineButton = UI.Button(app.L("Check Revocation Online (Contacts the Internet)"), () => { if (pending.IsCompleted && !closed) pending = CheckOnlineAsync(); });
        footer.Children.Add(onlineButton);
        footer.Children.Add(UI.Button(app.L("Copy Report"), () => app.Try(() => Clipboard.SetText(CurrentReport))));
        var close = UI.Button(app.L("Close"), () => DialogResult = false); close.IsCancel = true; footer.Children.Add(close);
        var body = new StackPanel(); root.Children.Add(UI.Scroll(body)); Content = root;
        body.Children.Add(new TextBox { Text = DoctorFacts.VisibleBounded(file.Path, 512), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 80, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        body.Children.Add(UI.Text(app.L("What Swivel found"), 16)); AutomationProperties.SetName(findings, app.L("What Swivel found")); body.Children.Add(findings);

        body.Children.Add(UI.Text(app.L("Signature"), 16));
        signature = List(190); signature.View = Columns(("Field", "Name", 270), ("Value", "Value", 530)); AutomationProperties.SetName(signature, app.L("Signature")); body.Children.Add(signature);
        online = UI.Text(app.L("Revocation not checked. Press Check Revocation Online to ask the certificate authorities; this contacts the internet."), 12); body.Children.Add(online);

        body.Children.Add(UI.Text(app.L("Download warning"), 16)); body.Children.Add(zonePanel);
        reveal = new CheckBox { Content = app.L("Show download addresses (may be private)"), Margin = new Thickness(0, 4, 0, 4) };
        addresses = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 90, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(addresses, app.L("Download addresses"));
        reveal.Checked += (_, _) => { addresses.Text = Addresses(); addresses.Visibility = Visibility.Visible; };
        reveal.Unchecked += (_, _) => { addresses.Text = ""; addresses.Visibility = Visibility.Collapsed; };

        body.Children.Add(UI.Text(app.L("Processor"), 16));
        var arch = List(110); arch.View = Columns(("Field", "Name", 270), ("Value", "Value", 530)); AutomationProperties.SetName(arch, app.L("Processor")); arch.ItemsSource = ArchRows(); body.Children.Add(arch);

        body.Children.Add(UI.Text(app.L("Imported DLLs"), 16));
        if (file.ImportFacts is { } imports)
        {
            var list = List(240); list.View = Columns(("Name", "Name", 330), ("Status", "Value", 470)); AutomationProperties.SetName(list, app.L("Imported DLLs"));
            list.ItemsSource = imports.Select(x => new Row(x.Name, app.L(AppCheck.ImportText(x.State)))).ToArray(); body.Children.Add(list);
            if (file.Imports is { Truncated: true }) body.Children.Add(UI.Text(app.L("Only the first 256 imported DLLs are listed."), 12));
            if (file.Imports is { Partial: true }) body.Children.Add(UI.Text(app.L("The import table is damaged or unusual; the list may be incomplete."), 12));
        }
        else body.Children.Add(UI.Text(app.L(file.Installer ? "Not applicable to installers (.msi)." : "The program's headers could not be read, so its DLLs are not listed."), 12));
        body.Children.Add(UI.Text(app.L(AppCheck.ImportScope), 12));
        body.Children.Add(UI.Text(app.L(AppCheck.Scope), 12));
        Refresh();
        Closed += (_, _) => closed = true;
        SourceInitialized += (_, _) => WebRouteLayout.Fit(this, Owner ?? this); DpiChanged += (_, _) => WebRouteLayout.Fit(this, this);
    }

    private void Refresh()
    {
        findings.Children.Clear();
        foreach (var finding in AppCheck.Findings(file.Trust, DateTime.UtcNow, file.Zone, file.Pe?.Machine, file.Fit, file.Os, file.ImportFacts, file.Installer, file.Online))
            findings.Children.Add(UI.Text(app.L(Level(finding.Level)) + ": " + Format(finding), 13));
        signature.ItemsSource = SignatureRows(file.Trust);
        zonePanel.Children.Clear();
        zonePanel.Children.Add(UI.Text(app.L(AppCheck.ZoneText(file.Zone)), 13));
        if (file.Zone is { HostUrl: not null } or { ReferrerUrl: not null }) { zonePanel.Children.Add(reveal); zonePanel.Children.Add(addresses); if (reveal.IsChecked == true) addresses.Text = Addresses(); }
        else { reveal.IsChecked = false; }
        if (file.Zone is not null) zonePanel.Children.Add(UI.Text(app.L(AppCheck.SmartScreen), 12));
        RemoveButton = null;
        if (file.CanRemoveWarning)
        {
            zonePanel.Children.Add(UI.Text(app.L(AppCheck.UnblockSteps), 12));
            RemoveButton = UI.Button(app.L("Remove Download Warning…"), () => app.Try(() => RemoveWarning()));
            RemoveButton.HorizontalAlignment = HorizontalAlignment.Left; zonePanel.Children.Add(RemoveButton);
        }
        else if (file.Zone is not null) zonePanel.Children.Add(UI.Text(app.L("Swivel offers to remove the download warning only for a file that is validly signed by a trusted publisher."), 12));
        if (removedNote) zonePanel.Children.Add(UI.Text(app.L("The download warning was removed from this file."), 13));
    }
    private bool removedNote;

    /// Returns whether the warning was removed. Refuses unless the held file is still eligible.
    internal bool RemoveWarning()
    {
        if (!file.CanRemoveWarning || closed || !pending.IsCompleted) return false;
        if (!ConfirmRemove() || closed || !pending.IsCompleted) return false;
        file.RemoveWarning();
        Removed = removedNote = file.Zone is null;
        Refresh();
        return Removed;
    }

    internal async Task CheckOnlineAsync()
    {
        onlineButton.IsEnabled = false; online.Text = app.L("Checking revocation online…");
        try
        {
            var result = await Task.Run(file.CheckOnline);
            if (closed) return;
            online.Text = app.L("Online revocation check") + ": " + app.L(AppCheck.VerdictText(AppCheck.Classify(result, DateTime.UtcNow))) + " (" + AppCheck.Hex(result.Hresult) + ")";
            Refresh();
        }
        catch (Exception error) when (!closed) { online.Text = app.L("Online revocation check") + ": " + app.L("Failed") + " (" + error.GetType().Name + ")"; }
        finally { if (!closed) onlineButton.IsEnabled = true; }
    }
    /// The verification call cannot be interrupted; the owner waits for it before releasing the file.
    internal async Task FinishAsync() { try { await pending; } catch { } }

    private string Format(AppFinding finding) => finding.Arguments.Length == 0 ? app.L(finding.Text) : string.Format(CultureInfo.CurrentCulture, app.L(finding.Text), finding.Arguments.Cast<object>().ToArray());
    private static string Level(FindingLevel level) => level switch { FindingLevel.Problem => "Problem", FindingLevel.Warning => "Warning", _ => "Note" };
    private string Addresses()
    {
        var text = new StringBuilder();
        if (file.Zone?.HostUrl is string host) text.AppendLine(app.L("Downloaded from") + ": " + DoctorFacts.VisibleBounded(host, 512));
        if (file.Zone?.ReferrerUrl is string referrer) text.AppendLine(app.L("Referring page") + ": " + DoctorFacts.VisibleBounded(referrer, 512));
        return text.ToString().TrimEnd();
    }
    private Row[] SignatureRows(TrustFacts trust)
    {
        string Time(DateTime? value) => value is DateTime t ? t.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : app.L("None found");
        return new[]
        {
            new Row(app.L("Result"), app.L(AppCheck.VerdictText(AppCheck.Classify(trust, DateTime.UtcNow)))),
            new Row(app.L("Where the signature is"), app.L(AppCheck.SourceText(trust.Source))),
            new Row(app.L("Signed by"), trust.Subject ?? app.L("Unknown")),
            new Row(app.L("Certificate issued by"), trust.Issuer ?? app.L("Unknown")),
            new Row(app.L("Timestamp"), Time(trust.TimestampUtc)),
            new Row(app.L("Certificate valid until"), Time(trust.NotAfterUtc)),
            new Row(app.L("Windows result code"), AppCheck.Hex(trust.Hresult)),
        };
    }
    private Row[] ArchRows()
    {
        var rows = new List<Row> { new(app.L("This PC"), AppCheck.OsName(file.Os)) };
        if (file.Pe is { } pe)
        {
            rows.Add(new(app.L("Made for"), AppCheck.MachineName(pe.Machine) + (pe.ClrDirectory == PeDirectoryDeclaration.Declared ? " (" + app.L(".NET program") + ")" : "")));
            rows.Add(new(app.L("Fit"), app.L(AppCheck.FitText(file.Fit ?? ArchFit.Unknown))));
        }
        else rows.Add(new(app.L("Made for"), app.L(file.Installer ? "Not applicable to installers (.msi)." : "The program's headers could not be read.")));
        return rows.ToArray();
    }

    internal string CurrentReport
    {
        get
        {
            var report = new StringBuilder();
            report.AppendLine(DoctorFacts.VisibleBounded(file.Path, 512));
            foreach (var finding in AppCheck.Findings(file.Trust, DateTime.UtcNow, file.Zone, file.Pe?.Machine, file.Fit, file.Os, file.ImportFacts, file.Installer, file.Online)) report.AppendLine(app.L(Level(finding.Level)) + ": " + Format(finding));
            foreach (var row in SignatureRows(file.Trust)) report.AppendLine(row.Name + ": " + row.Value);
            report.AppendLine(online.Text);
            report.AppendLine(app.L("Download warning") + ": " + app.L(AppCheck.ZoneText(file.Zone)));
            if (reveal.IsChecked == true && Addresses() is { Length: > 0 } shown) report.AppendLine(shown);
            foreach (var row in ArchRows()) report.AppendLine(row.Name + ": " + row.Value);
            foreach (var import in file.ImportFacts ?? Array.Empty<ImportFact>()) report.AppendLine(import.Name + ": " + app.L(AppCheck.ImportText(import.State)));
            report.AppendLine(app.L(AppCheck.ImportScope)); report.AppendLine(app.L(AppCheck.Scope));
            return report.ToString();
        }
    }

    private static ListView List(int height) { var view = new ListView { Height = height, SelectionMode = SelectionMode.Single }; VirtualizingPanel.SetIsVirtualizing(view, true); VirtualizingPanel.SetVirtualizationMode(view, VirtualizationMode.Recycling); ScrollViewer.SetCanContentScroll(view, true); return view; }
    private GridView Columns(params (string Label, string Property, double Width)[] columns) { var grid = new GridView(); foreach (var c in columns) grid.Columns.Add(new GridViewColumn { Header = app.L(c.Label), DisplayMemberBinding = new Binding(c.Property), Width = c.Width }); return grid; }
    private sealed record Row(string Name, string Value);
}
