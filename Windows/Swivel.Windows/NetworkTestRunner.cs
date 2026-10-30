using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Swivel.Core;

namespace Swivel.Windows;

// Runs only from the explicit Test Connection / Measure Download Speed buttons.
// Contacts the default gateway (ping) and the fixed Microsoft URLs in NetworkTest,
// nothing else. No cookies, credentials, custom headers, redirects or saved data;
// exception messages, bodies and headers other than one redirect host never leave here.
internal sealed class NetworkTestRunner : IDisposable
{
    private readonly SocketsHttpHandler handler;
    private readonly HttpClient client;
    private readonly IWebProxy? proxy;
    internal NetworkTestRunner()
    {
        // The Windows proxy settings, as HttpClient uses them by default. Windows account
        // credentials are not sent to a proxy; a proxy that needs them answers 407.
        try { proxy = HttpClient.DefaultProxy; } catch (Exception error) when (error is InvalidOperationException or NotSupportedException or PlatformNotSupportedException) { proxy = null; }
        handler = new SocketsHttpHandler {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = proxy is not null, Proxy = proxy, Credentials = null, DefaultProxyCredentials = null, PreAuthenticate = false,
            AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(5), MaxResponseHeadersLength = 64, PooledConnectionLifetime = TimeSpan.Zero
        };
        client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    }
    public void Dispose() { client.Dispose(); handler.Dispose(); }

    internal bool? ProxyUsed(string url)
    {
        if (proxy is null) return false;
        try { var uri = new Uri(url); return !proxy.IsBypassed(uri) && proxy.GetProxy(uri) is Uri used && used != uri; }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or PlatformNotSupportedException or System.ComponentModel.Win32Exception or UriFormatException) { return null; }
    }

    internal static async Task<GatewayProbe> GatewayAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        IPAddress[] gateways;
        try
        {
            gateways = NetworkInterface.GetAllNetworkInterfaces()
                .Where(x => x.OperationalStatus == OperationalStatus.Up && x.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp))
                .SelectMany(x => { try { return x.GetIPProperties().GatewayAddresses.Select(g => g.Address).ToArray(); } catch (NetworkInformationException) { return Array.Empty<IPAddress>(); } })
                .Where(x => !x.Equals(IPAddress.Any) && !x.Equals(IPAddress.IPv6Any)).Distinct().ToArray();
        }
        catch (NetworkInformationException) { gateways = Array.Empty<IPAddress>(); }
        if (gateways.Length == 0) return new(0, 0, 0, null);
        var target = gateways.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork) ?? gateways[0];
        int replies = 0; long? best = null; byte[] buffer = new byte[32];
        using var ping = new Ping();
        for (int i = 0; i < NetworkTest.PingTries; i++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var reply = await ping.SendPingAsync(target, TimeSpan.FromMilliseconds(NetworkTest.PingTimeoutMs), buffer, new PingOptions(64, true), token);
                if (reply.Status == IPStatus.Success) { replies++; best = Math.Min(best ?? long.MaxValue, reply.RoundtripTime); }
            }
            catch (PingException) { /* No reply counted. */ }
            catch (InvalidOperationException) { /* No reply counted. */ }
        }
        return new(gateways.Length, NetworkTest.PingTries, replies, best);
    }

    internal static async Task<DnsProbe> DnsAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(NetworkTest.DnsTimeoutMs);
        var clock = Stopwatch.StartNew();
        try { var addresses = await Dns.GetHostAddressesAsync(NetworkTest.DnsHost, limit.Token); return new(addresses.Length > 0, addresses.Length, clock.ElapsedMilliseconds, addresses.Length > 0 ? NetError.None : NetError.NameResolution); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(false, 0, clock.ElapsedMilliseconds, NetError.Timeout); }
        catch (SocketException error) { return new(false, 0, clock.ElapsedMilliseconds, error.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain ? NetError.NameResolution : NetError.Other); }
        catch (ArgumentException) { return new(false, 0, clock.ElapsedMilliseconds, NetError.Other); }
    }

    internal Task<HttpProbe> ConnectAsync(CancellationToken token) => RequestAsync(NetworkTest.ConnectUrl, true, token);
    internal Task<HttpProbe> SecureAsync(CancellationToken token) => RequestAsync(NetworkTest.SecureUrl, false, token);

    private async Task<HttpProbe> RequestAsync(string url, bool readBody, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        bool? viaProxy = ProxyUsed(url);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(NetworkTest.HttpTimeoutMs);
        var clock = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
            int status = (int)response.StatusCode; string? redirect = null; bool matches = false;
            if (status is >= 300 and < 400 && response.Headers.Location is Uri location && location.IsAbsoluteUri) redirect = location.Host;
            if (readBody && status == 200)
            {
                byte[] body = await ReadBoundedAsync(response, NetworkTest.MaxConnectBody + 1, limit.Token);
                matches = body.AsSpan().SequenceEqual(Encoding.ASCII.GetBytes(NetworkTest.ConnectBody));
            }
            return new(status, matches, redirect, clock.ElapsedMilliseconds, NetError.None, viaProxy);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(null, false, null, clock.ElapsedMilliseconds, NetError.Timeout, viaProxy); }
        // The time limit can surface as an I/O error on a cancelled stream.
        catch (Exception error) when (!token.IsCancellationRequested && limit.IsCancellationRequested && Map(error) is not null) { return new(null, false, null, clock.ElapsedMilliseconds, NetError.Timeout, viaProxy); }
        catch (Exception error) when (!token.IsCancellationRequested && Map(error) is NetError mapped) { return new(null, false, null, clock.ElapsedMilliseconds, mapped, viaProxy); }
        // A cancelled stream may surface as an I/O error; the user stopped it either way.
        catch (Exception error) when (token.IsCancellationRequested && error is not OperationCanceledException && Map(error) is not null) { throw new OperationCanceledException(token); }
    }

    /// <param name="received">Called after each read with the running byte count (tests use it to cancel mid-download).</param>
    internal async Task<SpeedProbe> SpeedAsync(CancellationToken token, Action<long>? received = null)
    {
        token.ThrowIfCancellationRequested();
        bool? viaProxy = ProxyUsed(NetworkTest.SpeedUrl); long bytes = 0; int? status = null; var clock = new Stopwatch();
        using var headers = CancellationTokenSource.CreateLinkedTokenSource(token); headers.CancelAfter(NetworkTest.HttpTimeoutMs);
        using var body = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, NetworkTest.SpeedUrl);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token);
            status = (int)response.StatusCode;
            if (status != 200) return new(status, 0, 0, SpeedStop.Error, NetError.None, viaProxy);
            body.CancelAfter(TimeSpan.FromSeconds(NetworkTest.MaxSpeedSeconds)); clock.Start();
            await using var stream = await response.Content.ReadAsStreamAsync(body.Token);
            byte[] buffer = new byte[65536];
            while (bytes < NetworkTest.MaxSpeedBytes)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, NetworkTest.MaxSpeedBytes - bytes)), body.Token);
                if (read == 0) return new(status, bytes, clock.Elapsed.TotalSeconds, SpeedStop.EndOfFile, NetError.None, viaProxy);
                bytes += read;
                received?.Invoke(bytes);
            }
            return new(status, bytes, clock.Elapsed.TotalSeconds, SpeedStop.ByteLimit, NetError.None, viaProxy);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return clock.IsRunning ? new(status, bytes, clock.Elapsed.TotalSeconds, SpeedStop.TimeLimit, NetError.None, viaProxy) : new(status, 0, 0, SpeedStop.Error, NetError.Timeout, viaProxy);
        }
        catch (Exception error) when (!token.IsCancellationRequested && body.IsCancellationRequested && clock.IsRunning && Map(error) is not null) { return new(status, bytes, clock.Elapsed.TotalSeconds, SpeedStop.TimeLimit, NetError.None, viaProxy); }
        catch (Exception error) when (!token.IsCancellationRequested && Map(error) is NetError mapped) { return new(status, bytes, clock.Elapsed.TotalSeconds, SpeedStop.Error, mapped, viaProxy); }
        // A cancelled stream may surface as an I/O error; the user stopped it either way.
        catch (Exception error) when (token.IsCancellationRequested && error is not OperationCanceledException && Map(error) is not null) { throw new OperationCanceledException(token); }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int max, CancellationToken token)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        byte[] buffer = new byte[max]; int total = 0;
        while (total < max) { int read = await stream.ReadAsync(buffer.AsMemory(total, max - total), token); if (read == 0) break; total += read; }
        return buffer[..total];
    }

    internal static NetError? Map(Exception error) => error switch {
        HttpRequestException http => http.HttpRequestError switch {
            HttpRequestError.NameResolutionError => NetError.NameResolution, HttpRequestError.ConnectionError => NetError.Connection,
            HttpRequestError.SecureConnectionError => NetError.SecureConnection, HttpRequestError.ProxyTunnelError => http.StatusCode == HttpStatusCode.ProxyAuthenticationRequired ? NetError.ProxyAuthentication : NetError.Proxy,
            HttpRequestError.InvalidResponse or HttpRequestError.ResponseEnded or HttpRequestError.ConfigurationLimitExceeded or HttpRequestError.HttpProtocolError => NetError.Protocol,
            _ => http.InnerException is System.Security.Authentication.AuthenticationException ? NetError.SecureConnection : NetError.Other },
        IOException or SocketException => NetError.Connection,
        _ => null
    };
}
