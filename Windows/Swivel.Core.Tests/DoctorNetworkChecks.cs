using System;
using System.Linq;
using System.Net.NetworkInformation;
using Swivel.Core;

internal static class DoctorNetworkChecks
{
    public static void Run(Action<bool, string> check)
    {
        var adapter = new AdapterSnapshot("Ethernet 🦜\u202e", OperationalStatus.Up, NetworkInterfaceType.Ethernet,
            new[] { "192.0.2.11", "2001:db8::123", "fe80::1%2" }, new[] { "192.0.2.1" }, new[] { "203.0.113.53" }, true);
        var facts = DoctorNetwork.Describe(adapter).ToArray();
        check(facts.All(x => x.Section == "Network & Proxy") && facts.Count(x => x.PrivateDetails) == 3, "Network details are grouped and all address families default to private");
        var stamp = DateTimeOffset.Parse("2026-10-23T12:00:00Z");
        string hidden = DoctorReport.CopyText(facts, false, "en", stamp), visible = DoctorReport.CopyText(facts, true, "en", stamp);
        foreach (string value in adapter.Addresses.Concat(adapter.Gateways).Concat(adapter.Dns))
        {
            check(!hidden.Contains(value), "Default report excludes local/DNS/gateway address: " + value);
            check(visible.Contains(value), "Explicit address view includes address: " + value);
        }
        check(hidden.Contains("[U+202E]") && !hidden.Contains('\u202e'), "Untrusted adapter name format controls are visible");
        check(DoctorReport.Visible(facts, false).All(x => !x.PrivateDetails) && DoctorReport.Visible(facts, true).Length == facts.Length, "UI/report share the same private-detail filter");
        var config = facts.Single(x => x.Title == "Address configuration");
        check(config.Values.Cast<int>().SequenceEqual(new[] { 1, 2, 1, 1 }), "IPv4/IPv6/DNS/gateway configuration counts are independent of address visibility");
        check(facts.Single(x => x.Title == "IPv4 DHCP").Values.Single().Equals("On"), "DHCP true remains an explicit state");
        foreach (bool? dhcp in new bool?[] { false, null })
            check(DoctorNetwork.Describe(adapter with { Dhcp = dhcp }).Single(x => x.Title == "IPv4 DHCP").Values.Single().Equals(dhcp is null ? "Unavailable" : "Off"), "DHCP unknown is never fabricated as disabled");
        foreach (string address in new[] { "169.254.2.3", "169.254.255.255", "fe80::1234", "fe80::1%3" }) check(DoctorNetwork.LinkLocal(address), "Link-local address identified: " + address);
        foreach (string address in new[] { "192.168.1.1", "127.0.0.1", "2001:db8::1", "::1", "example.com", "", "999.1.1.1" }) check(!DoctorNetwork.LinkLocal(address), "No DNS lookup or false link-local classification: " + address);
        check(facts.Any(x => x.Title == "Link-local configuration") && !DoctorNetwork.Describe(adapter with { Addresses = new[] { "192.0.2.11" } }).Any(x => x.Title == "Link-local configuration"), "Link-local note only appears when supported by configuration");
        var many = DoctorNetwork.Describe(adapter with { Addresses = Enumerable.Range(0, 20).Select(i => "192.0.2." + i).ToArray() });
        string listed = many.Single(x => x.Title == "Local IP addresses").Values[0].ToString()!;
        check(listed.Split(Environment.NewLine).Length == 16 && !listed.Contains("192.0.2.16"), "Address display is bounded and overall counts retain full size");
        check(many.Single(x => x.Title == "Address configuration").Values[0].Equals(20), "Address count is not truncated to the displayed prefix");
        foreach (var status in Enum.GetValues<OperationalStatus>()) check(Strings.Table.ContainsKey(DoctorNetwork.State(status)), "Link state translated: " + status);
        check(DoctorNetwork.State((OperationalStatus)999) == "Unknown", "Future link states fail to unknown");
        foreach (var type in Enum.GetValues<NetworkInterfaceType>()) check(Strings.Table.ContainsKey(DoctorNetwork.Type(type)), "Adapter category translated: " + type);
        for (int mask = 0; mask < 8; mask++)
        {
            var proxy = DoctorNetwork.DescribeProxy(new((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0));
            check(proxy[0].Values[0].Equals((mask & 1) != 0 ? "On" : "Off") && proxy[1].Values[0].Equals((mask & 2) != 0 ? "Configured" : "Not configured") && proxy[2].Values[0].Equals((mask & 4) != 0 ? "Configured" : "Not configured"), "Proxy flags remain independent; no effective-proxy inference");
            foreach (var fact in proxy) check(Strings.Table.ContainsKey(fact.Title) && Strings.Table.ContainsKey(fact.Format) && fact.Values.OfType<string>().All(Strings.Table.ContainsKey), "Proxy facts use closed translated keys");
        }
        foreach (var fact in facts) check(Strings.Table.ContainsKey(fact.Title) && Strings.Table.ContainsKey(fact.Format), "Network fact keys translated: " + fact.Title + " / " + fact.Format);
        var reportFacts = new[] { new DoctorFact("System", "{0}", new object[] { "Fixture OS" }) }.Concat(facts).ToArray();
        foreach (string language in new[] { "en", "zh-Hans", "zh-Hant", "ja" })
        {
            string text = DoctorReport.CopyText(reportFacts, false, language, stamp);
            check(text.Contains("Fixture OS") && text.Contains(Strings.L("Network & Proxy", language)) && !text.Contains("203.0.113.53"), "Report grouping and privacy survive localization: " + language);
        }
    }
}
