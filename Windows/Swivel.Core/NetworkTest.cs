using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Swivel.Core;

// Closed failure classes. The native runner maps exceptions to these; no exception
// message, address, header or body text from the network reaches a report.
public enum NetError { None, Timeout, NameResolution, Connection, SecureConnection, Proxy, Protocol, Other }
public enum NetVerdict { Online, CaptivePortal, ProxyAuthentication, SecureFailed, NoGateway, ProxyProblem, DnsFailure, TimedOut, LocalOnly, Offline }
public enum NetStepStatus { Passed, Warning, Failed, NotRun }

/// <param name="Gateways">Default gateways reported by active adapters (0 = none).</param>
public sealed record GatewayProbe(int Gateways, int Sent, int Replies, long? BestMs);
public sealed record DnsProbe(bool Resolved, int Addresses, long Ms, NetError Error);
/// <param name="Status">HTTP status when a response arrived, otherwise null.</param>
/// <param name="RedirectHost">Host name only of a Location header; never the path or query.</param>
/// <param name="ProxyUsed">Whether the system proxy settings selected a proxy for this URL; null when unknown.</param>
public sealed record HttpProbe(int? Status, bool BodyMatches, string? RedirectHost, long Ms, NetError Error, bool? ProxyUsed);
public sealed record NetworkProbes(GatewayProbe Gateway, DnsProbe Dns, HttpProbe Connect, HttpProbe Secure);
/// <param name="Bytes">Body bytes received and discarded.</param>
/// <param name="Reason">Why reading stopped: the byte cap, the time cap, the end of the file, or an error.</param>
public sealed record SpeedProbe(int? Status, long Bytes, double Seconds, SpeedStop Reason, NetError Error, bool? ProxyUsed);
public enum SpeedStop { ByteLimit, TimeLimit, EndOfFile, Error }
/// A value shown as is, never looked up as a translation key (host names).
public sealed record NetLiteral(string Text) { public override string ToString() => Text; }
public sealed record NetStepLine(string Title, NetStepStatus Status, string Format, object[] Values);

public static class NetworkTest
{
    // Fixed Microsoft endpoints. Nothing here is built from a file, setting, report or model text.
    public const string DnsHost = "www.msftconnecttest.com";
    public const string ConnectUrl = "http://www.msftconnecttest.com/connecttest.txt";
    public const string ConnectBody = "Microsoft Connect Test";
    public const string SecureUrl = "https://www.microsoft.com/favicon.ico";
    // Pinned, versioned .NET runtime package (about 37 MB) on Microsoft's .NET download host.
    // Only the first MaxSpeedBytes are read into memory and discarded; nothing is saved.
    public const string SpeedUrl = "https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.13/dotnet-runtime-10.0.13-win-x64.zip";
    public const int PingTries = 4, PingTimeoutMs = 1000, DnsTimeoutMs = 5000, HttpTimeoutMs = 10000, MaxConnectBody = 1024;
    public const long MaxSpeedBytes = 25_000_000; public const int MaxSpeedSeconds = 15;

    public const string Scope = "Test Connection contacts the Internet: it pings your default gateway, resolves www.msftconnecttest.com, fetches Windows' own connectivity check page from that host over HTTP and requests a small icon from www.microsoft.com over HTTPS. Requests use the Windows proxy settings, as other apps do, send no cookies or user information and follow no redirects. Nothing is saved or sent anywhere else, and nothing is repaired.";
    public const string SpeedScope = "Measure Download Speed downloads up to 25 MB, for at most 15 seconds, of a fixed .NET runtime package from builds.dotnet.microsoft.com. The data is discarded, not saved. The result is a single rough sample of download speed from that server, not your plan's speed, upload speed or latency under load.";
    public const string Limits = "A single test from this PC at this moment. A gateway that ignores ping is common and not a fault by itself. A firewall, VPN, proxy or security software can affect each step differently. Windows' own network status may use other checks.";

    public static bool SecureOk(HttpProbe probe) => probe.Status is >= 200 and < 400 && probe.Error == NetError.None;
    public static bool ConnectOk(HttpProbe probe) => probe.Status == 200 && probe.BodyMatches && probe.Error == NetError.None;
    /// True when an answer arrived but it was not Microsoft's exact page: a sign-in page, a redirect or interception.
    public static bool Intercepted(HttpProbe probe) => probe.Status is int status && status != 407 && probe.Error == NetError.None && !(status == 200 && probe.BodyMatches);

    public static NetVerdict Classify(NetworkProbes p)
    {
        if (ConnectOk(p.Connect)) return SecureOk(p.Secure) ? NetVerdict.Online : p.Secure.Status == 407 ? NetVerdict.ProxyAuthentication : NetVerdict.SecureFailed;
        if (p.Connect.Status == 407 || p.Secure.Status == 407) return NetVerdict.ProxyAuthentication;
        if (Intercepted(p.Connect)) return NetVerdict.CaptivePortal;
        // No answer to the plain HTTP check at all.
        if (p.Connect.Error == NetError.Proxy || (p.Connect.ProxyUsed == true && p.Connect.Error is NetError.Connection or NetError.Timeout)) return NetVerdict.ProxyProblem;
        if (p.Gateway.Gateways == 0 && !p.Dns.Resolved) return NetVerdict.NoGateway;
        if (!p.Dns.Resolved || p.Connect.Error == NetError.NameResolution) return NetVerdict.DnsFailure;
        if (p.Connect.Error == NetError.Timeout) return NetVerdict.TimedOut;
        if (p.Gateway.Replies > 0) return NetVerdict.LocalOnly;
        return NetVerdict.Offline;
    }

    public static string Summary(NetVerdict verdict) => verdict switch {
        NetVerdict.Online => "This PC reached the Internet. Plain and secure web requests both worked.",
        NetVerdict.CaptivePortal => "The network answered with a different page instead of Microsoft's connectivity check. This usually means a sign-in page (hotel, airport, office Wi-Fi) or a filter. Open a browser and sign in, then test again.",
        NetVerdict.ProxyAuthentication => "The proxy asked for a sign-in. This test does not send your Windows account to a proxy, so it cannot pass; apps that sign in to the proxy may still work. Ask whoever manages this network if apps also fail.",
        NetVerdict.SecureFailed => "Plain web requests worked but the secure (HTTPS) request failed. Check the date and time, security software that inspects HTTPS, or a firewall that blocks secure connections.",
        NetVerdict.NoGateway => "No default gateway was found and names could not be resolved. This PC does not seem to be connected to a network. Check Wi-Fi or the cable.",
        NetVerdict.ProxyProblem => "The proxy selected by the Windows proxy settings did not answer. Check the proxy settings or ask whoever manages this network.",
        NetVerdict.DnsFailure => "Internet names could not be resolved. The DNS server may be unreachable or misconfigured. Restarting the router or checking DNS settings may help.",
        NetVerdict.TimedOut => "The Internet requests timed out. The connection may be very slow, overloaded or blocked by a firewall.",
        NetVerdict.LocalOnly => "The local network answered but the Internet could not be reached. The router or the Internet connection beyond it may be down.",
        _ => "The Internet could not be reached. Check the network connection and test again."
    };

    public static string ErrorText(NetError error) => error switch {
        NetError.None => "No error", NetError.Timeout => "Timed out", NetError.NameResolution => "Name could not be resolved",
        NetError.Connection => "Connection failed", NetError.SecureConnection => "Secure connection failed", NetError.Proxy => "Proxy failed",
        NetError.Protocol => "Unexpected response", _ => "Request failed"
    };
    public static string Proxy(bool? used) => used switch { true => "Through a proxy", false => "Direct (no proxy)", _ => "Proxy use unknown" };

    public static IReadOnlyList<NetStepLine> Steps(NetworkProbes p)
    {
        var steps = new List<NetStepLine>();
        var g = p.Gateway; bool online = ConnectOk(p.Connect);
        if (g.Gateways == 0) steps.Add(new("Default gateway", NetStepStatus.Warning, "{0}", new object[] { "No default gateway reported" }));
        else if (g.Replies > 0) steps.Add(new("Default gateway", NetStepStatus.Passed, "Answered {0} of {1} pings; best {2} ms", new object[] { g.Replies, g.Sent, g.BestMs ?? 0 }));
        else steps.Add(new("Default gateway", online ? NetStepStatus.Warning : NetStepStatus.Failed, online
            ? "No answer to {0} pings. Many routers ignore ping; the Internet was still reachable, so this is not a fault."
            : "No answer to {0} pings. Many routers ignore ping, so this alone does not prove a fault.", new object[] { g.Sent }));
        var d = p.Dns;
        steps.Add(d.Resolved
            ? new("Name resolution", NetStepStatus.Passed, "{0} resolved to {1} address(es) in {2} ms", new object[] { new NetLiteral(DnsHost), d.Addresses, d.Ms })
            : new("Name resolution", online ? NetStepStatus.Warning : NetStepStatus.Failed, online
                ? "{0}: {1} after {2} ms. Requests still worked through the proxy, which resolves names itself."
                : "{0}: {1} after {2} ms", new object[] { new NetLiteral(DnsHost), ErrorText(d.Error == NetError.None ? NetError.NameResolution : d.Error), d.Ms }));
        var c = p.Connect;
        if (ConnectOk(c)) steps.Add(new("Connectivity check (HTTP)", NetStepStatus.Passed, "Microsoft's expected text arrived in {0} ms · {1}", new object[] { c.Ms, Proxy(c.ProxyUsed) }));
        else if (c.Status is int status && c.RedirectHost is string host) steps.Add(new("Connectivity check (HTTP)", NetStepStatus.Failed, "HTTP {0}: redirected to {1} · {2}", new object[] { status, new NetLiteral(DoctorFacts.VisibleBounded(host, 80)), Proxy(c.ProxyUsed) }));
        else if (c.Status is int other) steps.Add(new("Connectivity check (HTTP)", NetStepStatus.Failed, other == 200 ? "HTTP {0}, but the page was not Microsoft's expected text · {1}" : "HTTP {0} instead of Microsoft's expected text · {1}", new object[] { other, Proxy(c.ProxyUsed) }));
        else steps.Add(new("Connectivity check (HTTP)", NetStepStatus.Failed, "{0} after {1} ms · {2}", new object[] { ErrorText(c.Error), c.Ms, Proxy(c.ProxyUsed) }));
        var s = p.Secure;
        if (SecureOk(s)) steps.Add(new("Secure connection (HTTPS)", NetStepStatus.Passed, "HTTP {0} from www.microsoft.com in {1} ms · {2}", new object[] { s.Status!.Value, s.Ms, Proxy(s.ProxyUsed) }));
        else if (s.Status is int code) steps.Add(new("Secure connection (HTTPS)", NetStepStatus.Failed, "HTTP {0} from www.microsoft.com after {1} ms · {2}", new object[] { code, s.Ms, Proxy(s.ProxyUsed) }));
        else steps.Add(new("Secure connection (HTTPS)", NetStepStatus.Failed, "{0} after {1} ms · {2}", new object[] { ErrorText(s.Error), s.Ms, Proxy(s.ProxyUsed) }));
        return steps.AsReadOnly();
    }

    /// Megabits per second (1,000,000 bits), or null when the sample is too small to mean anything.
    public static double? Mbps(SpeedProbe probe) => probe.Status == 200 && probe.Bytes >= 262_144 && probe.Seconds >= 0.2 ? probe.Bytes * 8 / probe.Seconds / 1_000_000 : null;
    public static NetStepLine Speed(SpeedProbe probe)
    {
        if (Mbps(probe) is double mbps)
            return new("Download speed", NetStepStatus.Passed, "About {0} Mbit/s ({1} MB in {2} s, {3}) · {4}", new object[] {
                Math.Round(mbps, 1), Math.Round(probe.Bytes / 1_000_000.0, 1), Math.Round(probe.Seconds, 1), StopText(probe.Reason), Proxy(probe.ProxyUsed) });
        if (probe.Status is int status && status != 200) return new("Download speed", NetStepStatus.Failed, "HTTP {0} from builds.dotnet.microsoft.com · {1}", new object[] { status, Proxy(probe.ProxyUsed) });
        if (probe.Error != NetError.None) return new("Download speed", NetStepStatus.Failed, "{0} after {1} MB · {2}", new object[] { ErrorText(probe.Error), Math.Round(probe.Bytes / 1_000_000.0, 1), Proxy(probe.ProxyUsed) });
        return new("Download speed", NetStepStatus.Failed, "{0}", new object[] { "Too little data arrived to estimate a speed" });
    }
    public static string StopText(SpeedStop stop) => stop switch {
        SpeedStop.ByteLimit => "stopped at the 25 MB limit", SpeedStop.TimeLimit => "stopped at the 15 second limit",
        SpeedStop.EndOfFile => "whole file", _ => "stopped by an error"
    };
    public static string StatusText(NetStepStatus status) => status switch {
        NetStepStatus.Passed => "OK", NetStepStatus.Warning => "Note", NetStepStatus.Failed => "Problem", _ => "Not run"
    };

    /// Formats a step with translated format and translated string values; numbers keep invariant digits.
    public static string Text(NetStepLine line, Func<string, string> l) =>
        string.Format(CultureInfo.InvariantCulture, l(line.Format), line.Values.Select(v => v is string s ? (object)l(s) : v).ToArray());

    public static string Report(NetVerdict? verdict, IReadOnlyList<NetStepLine> steps, NetStepLine? speed, DateTimeOffset captured, Func<string, string> l)
    {
        var lines = new List<string> { l("Windows Doctor") + " · " + l("Internet Connection Test"), captured.ToString("u", CultureInfo.InvariantCulture) };
        if (verdict is NetVerdict v) { lines.Add(""); lines.Add(l(Summary(v))); }
        if (steps.Count > 0) lines.Add("");
        foreach (var step in steps) lines.Add(l(step.Title) + " — " + l(StatusText(step.Status)) + ": " + Text(step, l));
        if (speed is not null) { lines.Add(""); lines.Add(l(speed.Title) + " — " + l(StatusText(speed.Status)) + ": " + Text(speed, l)); }
        lines.Add(""); lines.Add(l(Scope)); lines.Add(l(SpeedScope)); lines.Add(l(Limits));
        return string.Join(Environment.NewLine, lines);
    }
}
