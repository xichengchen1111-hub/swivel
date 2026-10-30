using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Swivel.Core;
using Swivel.Windows;

// Runs the real Internet test: CI runners have Internet access. Results are classified,
// not compared with fixed timings, so a slow runner still passes.
internal static class NetworkTestNativeChecks
{
    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        var precancelled = new CancellationToken(true);
        using (var runner = new NetworkTestRunner())
        {
            foreach (var (name, start) in new (string, Func<Task>)[] {
                ("gateway", () => NetworkTestRunner.GatewayAsync(precancelled)), ("DNS", () => NetworkTestRunner.DnsAsync(precancelled)),
                ("HTTP", () => runner.ConnectAsync(precancelled)), ("HTTPS", () => runner.SecureAsync(precancelled)), ("speed", () => runner.SpeedAsync(precancelled)) })
                reject(() => start().GetAwaiter().GetResult(), "Precancelled " + name + " step sends nothing and reports cancellation");

            var gateway = NetworkTestRunner.GatewayAsync(default).GetAwaiter().GetResult();
            check(gateway.Replies <= gateway.Sent && gateway.Sent is 0 or NetworkTest.PingTries && (gateway.Gateways > 0 || gateway.Sent == 0), "Gateway probe is bounded to four pings and only when a gateway exists");
            var dns = NetworkTestRunner.DnsAsync(default).GetAwaiter().GetResult();
            var connect = runner.ConnectAsync(default).GetAwaiter().GetResult();
            var secure = runner.SecureAsync(default).GetAwaiter().GetResult();
            var probes = new NetworkProbes(gateway, dns, connect, secure);
            var verdict = NetworkTest.Classify(probes);
            Console.WriteLine("Network test on this runner: " + verdict + " · gateway " + gateway + " · dns " + dns + " · http " + connect + " · https " + secure);
            check(dns.Resolved && dns.Addresses > 0, "System resolver resolves the fixed host on an Internet-connected runner");
            check(connect.Status == 200 && connect.BodyMatches && connect.RedirectHost is null, "Microsoft's connectivity check page returns its exact text");
            check(NetworkTest.SecureOk(secure), "HTTPS request to www.microsoft.com completes");
            check(verdict == NetVerdict.Online, "Internet-connected runner is classified Online");
            check(NetworkTest.Steps(probes).Skip(1).All(x => x.Status == NetStepStatus.Passed), "Name, HTTP and HTTPS steps pass; a silent gateway may be a note");
            check(connect.ProxyUsed == runner.ProxyUsed(NetworkTest.ConnectUrl), "Proxy use is reported from the Windows proxy settings");

            // Cancelling mid-download stops promptly and reports cancellation, not a speed.
            using var stop = new CancellationTokenSource(); long atCancel = 0, last = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew(); bool cancelled = false;
            try { runner.SpeedAsync(stop.Token, bytes => { last = bytes; if (atCancel == 0) { atCancel = bytes; stop.Cancel(); } }).GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancelled = true; }
            check(cancelled && atCancel > 0 && last == atCancel && clock.Elapsed < TimeSpan.FromSeconds(12), "Cancelling mid-download stops reading at once and reports cancellation, not a speed");

            var speed = runner.SpeedAsync(default).GetAwaiter().GetResult();
            Console.WriteLine("Speed sample on this runner: " + speed);
            check(speed.Status == 200 && speed.Bytes > 0 && speed.Bytes <= NetworkTest.MaxSpeedBytes && speed.Seconds <= NetworkTest.MaxSpeedSeconds + 1, "Speed sample is bounded to 25 MB and about 15 seconds");
            check(speed.Reason is SpeedStop.ByteLimit or SpeedStop.TimeLimit && NetworkTest.Mbps(speed) is > 0, "Speed sample of the fixed .NET package gives a positive Mbit/s");
        }
        check(NetworkTestRunner.Map(new HttpRequestException(HttpRequestError.NameResolutionError)) == NetError.NameResolution && NetworkTestRunner.Map(new HttpRequestException(HttpRequestError.SecureConnectionError)) == NetError.SecureConnection
            && NetworkTestRunner.Map(new HttpRequestException(HttpRequestError.ProxyTunnelError)) == NetError.Proxy && NetworkTestRunner.Map(new InvalidOperationException()) is null, "Request errors map to closed classes; unexpected exceptions are not swallowed");

        var app = Application.Current as App ?? new App(validationHost: true); string previous = app.Prefs.Language;
        try
        {
            foreach (string language in new[] { "en", "zh-Hans", "zh-Hant", "ja" })
            {
                app.Prefs.SetLanguage(language);
                var dialog = new NetworkTestDialog(app);
                var buttons = ((DockPanel)dialog.Content).Children.OfType<StackPanel>().Single().Children.OfType<WrapPanel>().Single().Children.OfType<Button>().Select(x => x.Content).ToArray();
                check(buttons.SequenceEqual(new object[] { app.L("Copy Report"), app.L("Open Network Settings"), app.L("Open Proxy Settings"), app.L("Close") }), "Network test footer only copies, opens fixed Settings pages and closes: " + language);
                check(dialog.Verdict is null && dialog.Steps.Count == 0 && dialog.CurrentReport.Contains(app.L(NetworkTest.Scope)), "Opening the window runs no step: " + language);
                dialog.Close(); dialog.FinishAsync().GetAwaiter().GetResult();
            }
        }
        finally { app.Prefs.SetLanguage(previous); }
    }
}
