using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Swivel.Core;

public sealed record AdapterSnapshot(string Name, OperationalStatus Status, NetworkInterfaceType Type,
    string[] Addresses, string[] Gateways, string[] Dns, bool? Dhcp);
public sealed record UserProxyFlags(bool AutoDetect, bool SetupScript, bool ManualProxy);

public static class DoctorNetwork
{
    public static string State(OperationalStatus status) => status switch {
        OperationalStatus.Up => "Link up", OperationalStatus.Down => "Link down",
        OperationalStatus.Dormant => "Dormant", OperationalStatus.NotPresent => "Not present",
        OperationalStatus.LowerLayerDown => "Lower layer down", OperationalStatus.Testing => "Testing", _ => "Unknown"
    };
    public static string Type(NetworkInterfaceType type) => type switch {
        NetworkInterfaceType.Wireless80211 => "Wi-Fi", NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => "Ethernet",
        NetworkInterfaceType.Loopback => "Loopback", NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp => "Tunnel / point-to-point", _ => "Other"
    };
    public static bool LinkLocal(string address)
    {
        if (!IPAddress.TryParse(address, out var ip)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6) return ip.IsIPv6LinkLocal;
        byte[] bytes = ip.GetAddressBytes(); return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    }
    public static IReadOnlyList<DoctorFact> Describe(AdapterSnapshot adapter)
    {
        var facts = new List<DoctorFact>();
        void Add(string title, string format, params object[] values) => facts.Add(new(title, format, values, Section: "Network & Proxy"));
        void Value(string title, string value) => facts.Add(new(title, "{0}", new object[] { value }, true, "Network & Proxy"));
        Add("Network adapter", "{0}", DoctorFacts.Visible(adapter.Name));
        facts.Add(new("Adapter state", "{0} · {1}", new object[] { State(adapter.Status), Type(adapter.Type) }, true, "Network & Proxy"));
        var valid = adapter.Addresses.Where(x => IPAddress.TryParse(x, out _)).ToArray();
        Add("Address configuration", "IPv4: {0} · IPv6: {1} · gateways: {2} · DNS servers: {3}",
            valid.Count(x => IPAddress.Parse(x).AddressFamily == AddressFamily.InterNetwork), valid.Count(x => IPAddress.Parse(x).AddressFamily == AddressFamily.InterNetworkV6), adapter.Gateways.Length, adapter.Dns.Length);
        Value("IPv4 DHCP", adapter.Dhcp is true ? "On" : adapter.Dhcp is false ? "Off" : "Unavailable");
        if (valid.Any(LinkLocal)) Value("Link-local configuration", "Link-local addresses were found. This alone does not prove a network problem.");
        void Addresses(string title, string[] values)
        {
            string text = string.Join(Environment.NewLine, values.Take(16).Select(DoctorFacts.Visible));
            facts.Add(new(title, "{0}", new object[] { text.Length == 0 ? "—" : text }, Section: "Network & Proxy", PrivateDetails: true));
        }
        Addresses("Local IP addresses", adapter.Addresses); Addresses("Gateway addresses", adapter.Gateways); Addresses("DNS server addresses", adapter.Dns);
        return facts;
    }
    public static IReadOnlyList<DoctorFact> DescribeProxy(UserProxyFlags flags) => new[] {
        new DoctorFact("Automatically detect proxy", "{0}", new object[] { flags.AutoDetect ? "On" : "Off" }, true, "Network & Proxy"),
        new DoctorFact("Proxy setup script", "{0}", new object[] { flags.SetupScript ? "Configured" : "Not configured" }, true, "Network & Proxy"),
        new DoctorFact("Manual user proxy", "{0}", new object[] { flags.ManualProxy ? "Configured" : "Not configured" }, true, "Network & Proxy"),
        new DoctorFact("Proxy scope", "{0}", new object[] { "These are current-user Internet proxy settings for the active connection. Apps and services may use different settings. Proxy URLs, credentials and bypass lists are not displayed or copied. No script was downloaded or evaluated." }, true, "Network & Proxy")
    };
}
