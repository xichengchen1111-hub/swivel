using System;
using System.Linq;
using Swivel.Core;

internal static class NetworkTestChecks
{
    public static void Run(Action<bool, string> check)
    {
        var gateway = new GatewayProbe(1, 4, 4, 2);
        var dns = new DnsProbe(true, 2, 18, NetError.None);
        var connect = new HttpProbe(200, true, null, 40, NetError.None, false);
        var secure = new HttpProbe(200, false, null, 60, NetError.None, false);
        var ok = new NetworkProbes(gateway, dns, connect, secure);
        var failed = new HttpProbe(null, false, null, 10000, NetError.Timeout, false);
        var noName = new HttpProbe(null, false, null, 3, NetError.NameResolution, false);
        void Verdict(NetworkProbes probes, NetVerdict expected, string label) => check(NetworkTest.Classify(probes) == expected, "Network verdict " + expected + ": " + label);

        Verdict(ok, NetVerdict.Online, "every step passed");
        Verdict(ok with { Gateway = gateway with { Replies = 0, BestMs = null } }, NetVerdict.Online, "gateway ignores ping but the Internet works");
        var noPing = NetworkTest.Steps(ok with { Gateway = gateway with { Replies = 0, BestMs = null } })[0];
        check(noPing.Status == NetStepStatus.Warning && noPing.Format.Contains("not a fault"), "Silent gateway is a note, not a problem, when the Internet works");
        check(NetworkTest.Steps(ok with { Gateway = gateway with { Replies = 0 }, Connect = failed, Secure = failed })[0].Status == NetStepStatus.Failed, "Silent gateway is a problem when nothing else worked");
        Verdict(ok with { Connect = connect with { Status = 302, BodyMatches = false, RedirectHost = "login.hotel.example" } }, NetVerdict.CaptivePortal, "redirect to a sign-in host");
        Verdict(ok with { Connect = connect with { BodyMatches = false } }, NetVerdict.CaptivePortal, "200 with a different body");
        Verdict(ok with { Connect = connect with { Status = 403, BodyMatches = false }, Secure = failed }, NetVerdict.CaptivePortal, "filter page blocks the check");
        Verdict(ok with { Connect = connect with { Status = 407, BodyMatches = false } }, NetVerdict.ProxyAuthentication, "proxy sign-in on HTTP");
        Verdict(ok with { Secure = new(null, false, null, 30, NetError.ProxyAuthentication, true) }, NetVerdict.ProxyAuthentication, "proxy refuses the HTTPS tunnel with 407 (thrown, not returned)");
        Verdict(ok with { Secure = new(null, false, null, 30, NetError.Proxy, true) }, NetVerdict.ProxyProblem, "proxy allows HTTP but refuses the HTTPS tunnel");
        Verdict(ok with { Secure = new(null, false, null, 50, NetError.SecureConnection, false) }, NetVerdict.SecureFailed, "TLS failure while HTTP works");
        Verdict(ok with { Secure = secure with { Status = 403 } }, NetVerdict.Online, "any HTTPS status proves the secure connection worked");
        check(NetworkTest.Steps(ok with { Secure = secure with { Status = 403 } })[3].Status == NetStepStatus.Warning, "Non-2xx HTTPS answer is a note, not a TLS problem");
        Verdict(ok with { Secure = new(null, false, null, 10000, NetError.Timeout, false) }, NetVerdict.SecureFailed, "HTTPS times out while HTTP works");
        var sinkholed = ok with { Connect = new(null, false, null, 4, NetError.Connection, false) };
        Verdict(sinkholed, NetVerdict.HttpBlocked, "blocklist sinkholes the HTTP check host while HTTPS works");
        Verdict(ok with { Dns = new(false, 0, 3, NetError.NameResolution), Connect = noName }, NetVerdict.HttpBlocked, "NXDOMAIN blocklist for the check host while HTTPS works");
        Verdict(ok with { Connect = failed }, NetVerdict.HttpBlocked, "port 80 dropped while HTTPS works");
        var blockedSteps = NetworkTest.Steps(ok with { Dns = new(false, 0, 3, NetError.NameResolution), Connect = noName, Gateway = gateway with { Replies = 0 } });
        check(blockedSteps.Take(3).All(x => x.Status == NetStepStatus.Warning) && blockedSteps[3].Status == NetStepStatus.Passed, "Blocked HTTP check and its name are notes when HTTPS reached the Internet");
        check(blockedSteps[1].Format.Contains("intermittent") && !blockedSteps[1].Format.Contains("proxy"), "Direct DNS note does not mention a proxy");
        Verdict(ok with { Connect = connect with { Status = 502, BodyMatches = false, ProxyUsed = true }, Secure = failed with { ProxyUsed = true } }, NetVerdict.ProxyProblem, "proxy answers 502 for its upstream");
        Verdict(ok with { Connect = new(null, false, null, 4, NetError.NameResolution, true), Secure = new(null, false, null, 4, NetError.NameResolution, true) }, NetVerdict.ProxyProblem, "proxy host name does not resolve");
        Verdict(ok with { Connect = new(null, false, null, 4, NetError.Protocol, false), Secure = failed }, NetVerdict.CaptivePortal, "garbage from a middlebox is treated as interception");
        Verdict(new(new(0, 0, 0, null), new(false, 0, 2, NetError.NameResolution), noName, noName), NetVerdict.NoGateway, "no gateway and no names");
        Verdict(new(new(0, 0, 0, null), dns, failed, failed), NetVerdict.TimedOut, "no gateway reported but names resolved (VPN) and requests timed out");
        Verdict(ok with { Dns = new(false, 0, 5000, NetError.Timeout), Connect = noName, Secure = noName }, NetVerdict.DnsFailure, "DNS failure");
        Verdict(ok with { Dns = new(false, 0, 40, NetError.NameResolution), Connect = connect with { ProxyUsed = true }, Secure = secure with { ProxyUsed = true } }, NetVerdict.Online, "local DNS fails but the proxy resolves names");
        var proxyDns = NetworkTest.Steps(ok with { Dns = new(false, 0, 40, NetError.NameResolution), Connect = connect with { ProxyUsed = true } })[1];
        check(proxyDns.Status == NetStepStatus.Warning && proxyDns.Format.Contains("proxy"), "DNS failure through a working proxy is explained as a note");
        Verdict(ok with { Connect = failed with { ProxyUsed = true }, Secure = failed with { ProxyUsed = true } }, NetVerdict.ProxyProblem, "proxy timed out");
        Verdict(ok with { Connect = new(null, false, null, 4, NetError.Proxy, true), Secure = failed }, NetVerdict.ProxyProblem, "proxy tunnel error");
        Verdict(ok with { Connect = new(null, false, null, 4, NetError.Connection, true), Secure = failed }, NetVerdict.ProxyProblem, "proxy refused the connection");
        Verdict(ok with { Connect = failed, Secure = failed }, NetVerdict.TimedOut, "direct requests time out");
        Verdict(ok with { Connect = new(null, false, null, 4, NetError.Connection, false), Secure = failed }, NetVerdict.LocalOnly, "gateway answers, Internet refused");
        Verdict(new(gateway with { Replies = 0 }, dns, new(null, false, null, 4, NetError.Connection, false), failed), NetVerdict.Offline, "nothing answered");
        Verdict(ok with { Connect = new(null, false, null, 4, NetError.Connection, null), Secure = failed }, NetVerdict.LocalOnly, "unknown proxy use does not invent a proxy fault");

        foreach (var verdict in Enum.GetValues<NetVerdict>()) check(Strings.Table.ContainsKey(NetworkTest.Summary(verdict)), "Verdict translated: " + verdict);
        foreach (var error in Enum.GetValues<NetError>()) check(Strings.Table.ContainsKey(NetworkTest.ErrorText(error)), "Error class translated: " + error);
        foreach (var status in Enum.GetValues<NetStepStatus>()) check(Strings.Table.ContainsKey(NetworkTest.StatusText(status)), "Step status translated: " + status);
        foreach (var stop in Enum.GetValues<SpeedStop>()) check(Strings.Table.ContainsKey(NetworkTest.StopText(stop)), "Speed stop translated: " + stop);
        foreach (bool? used in new bool?[] { true, false, null }) check(Strings.Table.ContainsKey(NetworkTest.Proxy(used)), "Proxy use translated: " + used);
        foreach (string note in new[] { NetworkTest.Scope, NetworkTest.SpeedScope, NetworkTest.Limits, "Internet Connection Test", "Windows Doctor" }) check(Strings.Table.ContainsKey(note), "Network test notice translated: " + note[..Math.Min(30, note.Length)]);

        // Every reachable step shape uses translated titles/formats and only literal or translated values.
        var shapes = new[] {
            ok, ok with { Gateway = new(0, 0, 0, null) }, ok with { Gateway = gateway with { Replies = 0 } }, ok with { Gateway = gateway with { Replies = 0 }, Connect = failed },
            ok with { Dns = new(false, 0, 9, NetError.Other) }, ok with { Dns = new(false, 0, 9, NetError.None), Connect = failed },
            ok with { Connect = connect with { Status = 302, BodyMatches = false, RedirectHost = "portal.example‮" } }, ok with { Connect = connect with { BodyMatches = false } },
            ok with { Connect = connect with { Status = 500, BodyMatches = false } }, ok with { Secure = secure with { Status = 403 } }, ok with { Secure = failed }, sinkholed, ok with { Dns = new(false, 0, 9, NetError.Timeout) }
        };
        foreach (var shape in shapes)
            foreach (var step in NetworkTest.Steps(shape))
                check(Strings.Table.ContainsKey(step.Title) && Strings.Table.ContainsKey(step.Format) && step.Values.All(v => v is NetLiteral or int or long or double || v is string s && Strings.Table.ContainsKey(s)), "Step uses closed translated keys: " + step.Title + " / " + step.Format);
        var redirect = NetworkTest.Steps(shapes[6])[2];
        string redirectText = NetworkTest.Text(redirect, x => x);
        check(redirectText.Contains("portal.example[U+202E]") && !redirectText.Contains('‮'), "Redirect host is shown with invisible characters made visible");
        check(NetworkTest.Steps(ok).Count == 4 && NetworkTest.Steps(ok).All(x => x.Status == NetStepStatus.Passed), "Four steps, all passing when online");

        // Speed: bounded sample, Mbit/s arithmetic and refusal to report a meaningless figure.
        var speed = new SpeedProbe(200, 25_000_000, 10, SpeedStop.ByteLimit, NetError.None, false);
        check(NetworkTest.Mbps(speed) == 20, "25 MB in 10 s is 20 Mbit/s (decimal megabits)");
        check(NetworkTest.Mbps(speed with { Bytes = 100_000 }) is null && NetworkTest.Mbps(speed with { Seconds = 0.01 }) is null, "Tiny samples give no speed");
        check(NetworkTest.Mbps(speed with { Seconds = 0.18 }) is > 1000, "A full 25 MB sample on a gigabit link still reports a speed");
        check(NetworkTest.Mbps(speed with { Status = 404 }) is null && NetworkTest.Speed(speed with { Status = 404 }).Status == NetStepStatus.Failed, "Non-200 download gives no speed");
        check(NetworkTest.Mbps(speed with { Reason = SpeedStop.Error, Error = NetError.Connection, Bytes = 5_000_000, Seconds = 2 }) == 20, "A sample interrupted by an error still reports what arrived");
        check(NetworkTest.Speed(speed with { Bytes = 0, Error = NetError.Timeout, Reason = SpeedStop.Error }).Status == NetStepStatus.Failed, "Timeout with no data is a failure");
        foreach (var probe in new[] { speed, speed with { Status = 403 }, speed with { Bytes = 0, Error = NetError.Timeout }, speed with { Bytes = 0 } })
        {
            var line = NetworkTest.Speed(probe);
            check(Strings.Table.ContainsKey(line.Format) && line.Values.All(v => v is int or double || v is string s && Strings.Table.ContainsKey(s)), "Speed line uses closed translated keys: " + line.Format);
        }
        check(NetworkTest.MaxSpeedBytes <= 25_000_000 && NetworkTest.MaxSpeedSeconds <= 15 && NetworkTest.PingTries == 4 && NetworkTest.PingTimeoutMs == 1000, "Fixed test bounds match the visible description");
        foreach (string url in new[] { NetworkTest.ConnectUrl, NetworkTest.SecureUrl, NetworkTest.SpeedUrl })
        {
            var uri = new Uri(url);
            check(uri.Host is "www.msftconnecttest.com" or "www.microsoft.com" or "builds.dotnet.microsoft.com" && uri.Query.Length == 0 && uri.UserInfo.Length == 0, "Fixed Microsoft endpoint with no query or user data: " + uri.Host);
        }
        check(new Uri(NetworkTest.SecureUrl).Scheme == "https" && new Uri(NetworkTest.SpeedUrl).Scheme == "https" && new Uri(NetworkTest.ConnectUrl).Scheme == "http", "Only the captive-portal check uses plain HTTP");

        var captured = DateTimeOffset.Parse("2026-10-30T09:00:00Z");
        foreach (string language in new[] { "en", "zh-Hans", "zh-Hant", "ja" })
        {
            string report = NetworkTest.Report(NetVerdict.CaptivePortal, NetworkTest.Steps(shapes[6]), NetworkTest.Speed(speed), captured, x => Strings.L(x, language));
            check(report.Contains(Strings.L(NetworkTest.Summary(NetVerdict.CaptivePortal), language)) && report.Contains("portal.example") && report.Contains("20") && report.Contains(Strings.L(NetworkTest.Scope, language)), "Localized network report includes verdict, redirect host, speed and scope: " + language);
        }
        string empty = NetworkTest.Report(null, Array.Empty<NetStepLine>(), null, captured, x => x);
        check(empty.Contains(NetworkTest.Scope) && !empty.Contains("OK"), "Report before any test only holds the scope");
    }
}
