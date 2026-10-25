using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

internal static class DoctorNetworkReader
{
    // Local OS configuration only. Never call DNS, Ping, socket/HTTP, WPAD
    // discovery, WinHttpGetProxyForUrl, PAC evaluation, registry writes or a shell.
    public static IReadOnlyList<DoctorFact> Read(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var facts = new List<DoctorFact>();
        void Add(string title, string format, params object[] values) => facts.Add(new(title, format, values, Section: "Network & Proxy"));
        void Value(string title, string value) => facts.Add(new(title, "{0}", new object[] { value }, true, "Network & Proxy"));
        Value("Snapshot limits", "This is local configuration, not an Internet or DNS test. Up to 64 adapters and the first 16 addresses of each kind are shown. VPNs, virtual adapters and enterprise policy need separate interpretation.");
        try
        {
            var adapters = NetworkInterface.GetAllNetworkInterfaces();
            Add("Network adapters", "{0} adapter(s) reported; {1} active non-loopback adapter(s); {2} beyond the display limit.", adapters.Length,
                adapters.Count(x => x.NetworkInterfaceType != NetworkInterfaceType.Loopback && x.OperationalStatus == OperationalStatus.Up), Math.Max(0, adapters.Length - 64));
            foreach (var adapter in adapters.Take(64))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var properties = adapter.GetIPProperties(); bool? dhcp = null;
                    try { if (adapter.Supports(NetworkInterfaceComponent.IPv4)) dhcp = properties.GetIPv4Properties()?.IsDhcpEnabled; }
                    catch (Exception ex) when (Expected(ex)) { /* Unknown is not Off. */ }
                    facts.AddRange(DoctorNetwork.Describe(new(adapter.Name, adapter.OperationalStatus, adapter.NetworkInterfaceType,
                        properties.UnicastAddresses.Select(x => x.Address.ToString()).ToArray(), properties.GatewayAddresses.Select(x => x.Address.ToString()).ToArray(),
                        properties.DnsAddresses.Select(x => x.ToString()).ToArray(), dhcp)));
                }
                catch (Exception ex) when (Expected(ex)) { Add("Network adapter", "{0}: unavailable", DoctorFacts.Visible(adapter.Name)); }
            }
        }
        catch (Exception ex) when (Expected(ex)) { Value("Network adapters", "Unavailable"); }
        token.ThrowIfCancellationRequested();
        try { facts.AddRange(DoctorNetwork.DescribeProxy(ProxyFlags())); }
        catch (Exception ex) when (Expected(ex)) { Value("User proxy settings", "Unavailable"); }
        token.ThrowIfCancellationRequested(); return facts.AsReadOnly();
    }
    private static bool Expected(Exception error) => error is Win32Exception or NetworkInformationException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException;
    private static UserProxyFlags ProxyFlags()
    {
        var config = new ProxyConfig();
        try
        {
            if (!WinHttpGetIEProxyConfigForCurrentUser(ref config)) throw new Win32Exception(Marshal.GetLastWin32Error());
            // Only inspect whether a string is empty. Do not bring proxy/PAC URLs,
            // credentials or bypass lists into managed memory or the report.
            static bool HasText(IntPtr pointer) => pointer != IntPtr.Zero && Marshal.ReadInt16(pointer) != 0;
            return new(config.AutoDetect != 0, HasText(config.AutoConfigUrl), HasText(config.Proxy));
        }
        finally
        {
            if (config.AutoConfigUrl != IntPtr.Zero) GlobalFree(config.AutoConfigUrl);
            if (config.Proxy != IntPtr.Zero) GlobalFree(config.Proxy);
            if (config.ProxyBypass != IntPtr.Zero) GlobalFree(config.ProxyBypass);
        }
    }
    [StructLayout(LayoutKind.Sequential)] internal struct ProxyConfig { public int AutoDetect; public IntPtr AutoConfigUrl, Proxy, ProxyBypass; }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winhttp.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinHttpGetIEProxyConfigForCurrentUser(ref ProxyConfig config);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
}
