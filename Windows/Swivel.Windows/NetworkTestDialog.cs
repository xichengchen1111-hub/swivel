using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Swivel.Core;

namespace Swivel.Windows;

// Opening this window contacts nothing. Each network step starts only from a button
// whose label says it contacts the Internet; Stop, Close, feature-off and Quit cancel
// the run, and FinishAsync waits for it before the window's owner continues.
internal sealed class NetworkTestDialog : Window
{
    private readonly App app;
    private readonly CancellationTokenSource cancellation;
    private readonly Button test, speed, stop;
    private readonly TextBlock verdictText, progress, speedText;
    private readonly TextBlock[] stepTexts = new TextBlock[4];
    private readonly NetworkTestRunner runner = new();
    private Task pending = Task.CompletedTask;
    private CancellationTokenSource? run;
    private bool closed;
    private DateTimeOffset captured = DateTimeOffset.Now;
    internal NetVerdict? Verdict { get; private set; }
    internal IReadOnlyList<NetStepLine> Steps { get; private set; } = Array.Empty<NetStepLine>();
    internal NetStepLine? SpeedLine { get; private set; }
    internal string CurrentReport => NetworkTest.Report(Verdict, Steps, SpeedLine, captured, app.L);
    private static readonly string[] StepTitles = { "Default gateway", "Name resolution", "Connectivity check (HTTP)", "Secure connection (HTTPS)" };

    internal NetworkTestDialog(App app, CancellationToken token = default)
    {
        this.app = app; cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        Title = app.L("Internet Connection Test"); Width = 860; Height = 760; MinWidth = 520; MinHeight = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) };
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(UI.Text(app.L("These buttons only open the named Windows Settings pages. Swivel does not apply fixes."), 12));
        var row = new WrapPanel();
        row.Children.Add(UI.Button(app.L("Copy Report"), () => app.Try(() => Clipboard.SetText(CurrentReport))));
        row.Children.Add(UI.Button(app.L("Open Network Settings"), () => app.Try(() => WindowsSettings.Open(WindowsSettingsPage.Network))));
        row.Children.Add(UI.Button(app.L("Open Proxy Settings"), () => app.Try(() => WindowsSettings.Open(WindowsSettingsPage.Proxy))));
        var close = UI.Button(app.L("Close"), () => DialogResult = false); close.IsCancel = true; row.Children.Add(close);
        footer.Children.Add(row);
        var body = new StackPanel(); root.Children.Add(UI.Scroll(body)); Content = root;
        body.Children.Add(UI.Text(app.L(NetworkTest.Scope), 13));
        var actions = new WrapPanel();
        test = UI.Button(app.L("Test Connection (Contacts the Internet)"), () => Start(false));
        speed = UI.Button(app.L("Measure Download Speed (Downloads up to 25 MB)"), () => Start(true));
        stop = UI.Button(app.L("Stop"), () => run?.Cancel()); stop.IsEnabled = false;
        actions.Children.Add(test); actions.Children.Add(speed); actions.Children.Add(stop); body.Children.Add(actions);
        progress = UI.Text(app.L("Nothing has been tested yet. Opening this window contacted nothing."), 13); body.Children.Add(progress);
        verdictText = UI.Text("", 18); AutomationProperties.SetName(verdictText, app.L("Test Result")); body.Children.Add(verdictText);
        for (int i = 0; i < stepTexts.Length; i++)
        {
            body.Children.Add(UI.Text(app.L(StepTitles[i]), 16));
            stepTexts[i] = UI.Text(app.L("Not run"), 13); AutomationProperties.SetName(stepTexts[i], app.L(StepTitles[i])); body.Children.Add(stepTexts[i]);
        }
        body.Children.Add(UI.Text(app.L("Download speed"), 16));
        speedText = UI.Text(app.L("Not run"), 13); AutomationProperties.SetName(speedText, app.L("Download speed")); body.Children.Add(speedText);
        body.Children.Add(UI.Text(app.L(NetworkTest.SpeedScope), 12));
        body.Children.Add(UI.Text(app.L(NetworkTest.Limits), 12));
        cancellation.Token.Register(() => Dispatcher.BeginInvoke(new Action(() => { if (!closed) Close(); })));
        Closed += (_, _) => { closed = true; run?.Cancel(); cancellation.Cancel(); };
        SourceInitialized += (_, _) => WebRouteLayout.Fit(this, Owner ?? this); DpiChanged += (_, _) => WebRouteLayout.Fit(this, this);
    }

    private void Start(bool measureSpeed)
    {
        if (closed || !pending.IsCompleted || cancellation.IsCancellationRequested) return;
        run = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        pending = measureSpeed ? SpeedAsync(run.Token) : TestAsync(run.Token);
    }

    private void Busy(bool busy) { test.IsEnabled = speed.IsEnabled = !busy && !closed; stop.IsEnabled = busy && !closed; }

    // Runs every probe off the UI thread: resolving the system proxy can itself take time.
    internal async Task TestAsync(CancellationToken token)
    {
        Busy(true); captured = DateTimeOffset.Now; Verdict = null; Steps = Array.Empty<NetStepLine>(); verdictText.Text = "";
        foreach (var text in stepTexts) text.Text = app.L("Not run");
        try
        {
            progress.Text = app.L("Pinging the default gateway…");
            var gateway = await Task.Run(() => NetworkTestRunner.GatewayAsync(token), token); token.ThrowIfCancellationRequested();
            stepTexts[0].Text = app.L(gateway.Gateways == 0 ? "No default gateway reported" : gateway.Replies > 0 ? "Answered" : "No answer");
            progress.Text = app.L("Resolving www.msftconnecttest.com…");
            var dns = await Task.Run(() => NetworkTestRunner.DnsAsync(token), token); token.ThrowIfCancellationRequested();
            stepTexts[1].Text = app.L(dns.Resolved ? "Resolved" : NetworkTest.ErrorText(dns.Error));
            progress.Text = app.L("Fetching Windows' connectivity check page…");
            var connect = await Task.Run(() => runner.ConnectAsync(token), token); token.ThrowIfCancellationRequested();
            stepTexts[2].Text = app.L(NetworkTest.ConnectOk(connect) ? "Expected text received" : "Unexpected or no answer");
            progress.Text = app.L("Requesting https://www.microsoft.com…");
            var secure = await Task.Run(() => runner.SecureAsync(token), token); token.ThrowIfCancellationRequested();
            var probes = new NetworkProbes(gateway, dns, connect, secure);
            Verdict = NetworkTest.Classify(probes); Steps = NetworkTest.Steps(probes);
            for (int i = 0; i < Steps.Count; i++) stepTexts[i].Text = app.L(NetworkTest.StatusText(Steps[i].Status)) + ": " + NetworkTest.Text(Steps[i], app.L);
            verdictText.Text = app.L(NetworkTest.Summary(Verdict.Value));
            progress.Text = app.L("Test finished. Nothing was changed.");
        }
        catch (OperationCanceledException) { if (!closed) progress.Text = app.L("Test stopped. Steps that did not finish show no result."); }
        catch (Exception error) when (!closed && error is not OperationCanceledException) { progress.Text = ""; app.ShowError(error); }
        finally { if (!closed) Busy(false); }
    }

    internal async Task SpeedAsync(CancellationToken token)
    {
        Busy(true); SpeedLine = null; speedText.Text = app.L("Not run");
        try
        {
            progress.Text = app.L("Downloading up to 25 MB from builds.dotnet.microsoft.com…");
            var probe = await Task.Run(() => runner.SpeedAsync(token), token); token.ThrowIfCancellationRequested();
            SpeedLine = NetworkTest.Speed(probe);
            speedText.Text = app.L(NetworkTest.StatusText(SpeedLine.Status)) + ": " + NetworkTest.Text(SpeedLine, app.L);
            progress.Text = app.L("Speed sample finished. The downloaded data was discarded.");
        }
        catch (OperationCanceledException) { if (!closed) progress.Text = app.L("Speed sample stopped. The downloaded data was discarded."); }
        catch (Exception error) when (!closed && error is not OperationCanceledException) { progress.Text = ""; app.ShowError(error); }
        finally { if (!closed) Busy(false); }
    }

    internal async Task FinishAsync()
    {
        run?.Cancel(); cancellation.Cancel();
        try { await pending; } catch (OperationCanceledException) { }
        runner.Dispose();
    }
}
