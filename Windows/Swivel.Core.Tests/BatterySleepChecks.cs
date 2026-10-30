using System;
using System.Collections.Generic;
using System.Linq;
using Swivel.Core;

internal static class BatterySleepChecks
{
    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        static BatteryInformation Info(uint design, uint full, uint caps = BatteryHealth.SystemBattery, uint cycles = 0) => new(caps, 1, "LION", design, full, cycles);

        // Health classification: plain thresholds on full ÷ design.
        var good = BatteryHealth.Classify(Info(50_000, 45_000));
        check(good.Class == BatteryHealthClass.Good && Math.Abs(good.Percent!.Value - 90) < 1e-9, "90% of design is Good");
        check(BatteryHealth.Classify(Info(50_000, 40_000)).Class == BatteryHealthClass.Good, "Exactly 80% is Good");
        check(BatteryHealth.Classify(Info(50_000, 39_950)).Class == BatteryHealthClass.Reduced, "79.9% is Reduced");
        check(BatteryHealth.Classify(Info(50_000, 30_000)).Class == BatteryHealthClass.Reduced, "Exactly 60% is Reduced");
        check(BatteryHealth.Classify(Info(50_000, 29_950)).Class == BatteryHealthClass.Worn, "59.9% is Worn");
        var above = BatteryHealth.Classify(Info(50_000, 52_000));
        check(above.Class == BatteryHealthClass.Good && above.Percent > 100 && BatteryHealth.Percent(above.Percent) == "104.0%", "Full charge above design is reported as is, not capped");
        check(BatteryHealth.Classify(Info(uint.MaxValue, uint.MaxValue)).Class == BatteryHealthClass.Unknown, "All-ones values are unknown sentinels, not 100%");

        // Unknown values never become 0%.
        foreach (var (info, reason) in new (BatteryInformation?, BatteryUnknownReason)[] {
            (null, BatteryUnknownReason.NoInformation), (Info(0, 40_000), BatteryUnknownReason.DesignUnknown), (Info(BatteryHealth.UnknownCapacity, 40_000), BatteryUnknownReason.DesignUnknown),
            (Info(50_000, 0), BatteryUnknownReason.FullUnknown), (Info(50_000, BatteryHealth.UnknownCapacity), BatteryUnknownReason.FullUnknown),
            (Info(100, 90, BatteryHealth.SystemBattery | BatteryHealth.CapacityRelative), BatteryUnknownReason.RelativeCapacity) })
        {
            var result = BatteryHealth.Classify(info);
            check(result.Class == BatteryHealthClass.Unknown && result.Percent is null && result.Reason == reason && BatteryHealth.Percent(result.Percent) == "Unknown" && !string.IsNullOrEmpty(BatteryHealth.Reason(reason)), "Unknown health has a reason and no percentage: " + reason);
        }
        check(BatteryHealth.Reason(BatteryUnknownReason.None) == "", "Known health has no unknown reason");
        reject(() => BatteryHealth.Name((BatteryHealthClass)9), "Unknown health class rejected");
        reject(() => BatteryHealth.Reason((BatteryUnknownReason)9), "Unknown reason rejected");

        // Raw field formatting keeps sentinels and units honest.
        check(BatteryHealth.Capacity(0, BatteryHealth.SystemBattery) == "Unknown (0)" && BatteryHealth.Capacity(BatteryHealth.UnknownCapacity, 0) == "Unknown (4294967295)", "Zero and unknown capacities shown as unknown with raw value");
        check(BatteryHealth.Capacity(48_000, BatteryHealth.SystemBattery) == "48,000 mWh" && BatteryHealth.Capacity(100, BatteryHealth.CapacityRelative).EndsWith("(relative units)"), "Absolute mWh vs relative units");
        check(BatteryHealth.RemainingCapacity(0, 0) == "0 mWh" && BatteryHealth.RemainingCapacity(BatteryHealth.UnknownCapacity, 0) == "Unknown", "Remaining charge zero is a real value; sentinel is unknown");
        check(BatteryHealth.Voltage(12_345) == "12.345 V" && BatteryHealth.Voltage(BatteryHealth.UnknownVoltage) == "Unknown", "Voltage mV to V; unknown sentinel");
        check(BatteryHealth.Rate(-12_000, 0) == "-12,000 mW" && BatteryHealth.Rate(BatteryHealth.UnknownRate, 0) == "Unknown", "Discharge rate sign kept; unknown sentinel");
        check(BatteryHealth.Classify(Info(100_000, 79_960)).Class == BatteryHealthClass.Good && BatteryHealth.Classify(Info(100_000, 59_970)).Class == BatteryHealthClass.Reduced && BatteryHealth.Classify(Info(100_000, 79_940)).Class == BatteryHealthClass.Reduced, "Class follows the displayed one-decimal percentage");
        check(BatteryHealth.RemainingCapacity(5, null) == "5 (units unknown)" && BatteryHealth.Rate(-5, null) == "-5 (units unknown)", "Units unknown when battery information failed");
        check(BatteryHealth.Cycles(0) == "Not reported (0)" && BatteryHealth.Cycles(312) == "312", "Cycle count zero is not reported");
        check(BatteryHealth.ChargePercent(Info(50_000, 40_000), new(BatteryHealth.Discharging, 20_000, 12_000, -5_000)) == 50 && BatteryHealth.ChargePercent(Info(50_000, 0), new(0, 20_000, 0, 0)) is null && BatteryHealth.ChargePercent(Info(50_000, 40_000), new(0, BatteryHealth.UnknownCapacity, 0, 0)) is null, "Charge percent only from known values");
        check(BatteryHealth.ChargePercent(Info(50_000, 40_000), new(0, 41_000, 0, 0)) == 100, "Charge percent capped at 100");
        check(BatteryHealth.PowerStates(BatteryHealth.PowerOnLine | BatteryHealth.Charging).SequenceEqual(new[] { "Plugged in", "Charging" }) && BatteryHealth.PowerStates(0).Count == 0, "Power-state flags decoded");
        check(BatteryHealth.Kind(0) != BatteryHealth.Kind(BatteryHealth.SystemBattery) && BatteryHealth.Kind(BatteryHealth.SystemBattery | BatteryHealth.ShortTerm) == "Short-term system battery", "Battery kind from capability bits");
        check(BatteryHealth.ChemistryText(new byte[] { (byte)'L', (byte)'I', (byte)'O', (byte)'N' }) == "LION" && BatteryHealth.ChemistryText(new byte[] { (byte)'L', (byte)'i', (byte)'P', 0 }) == "LiP" && BatteryHealth.ChemistryText(new byte[] { 0x01, 0xFF, (byte)'A', (byte)' ' }) == "??A", "Chemistry ASCII, NUL-cut, nonprintables escaped");
        check(BatteryHealth.DriverText("ABC\0junk".AsSpan()) == "ABC" && BatteryHealth.DriverText("A‮B".AsSpan()) == "A[U+202E]B" && BatteryHealth.DriverText(new string('x', 600).AsSpan()).Length <= BatteryHealth.MaxTextChars + 1, "Driver strings cut at NUL, bounded, controls visible");

        var none = new BatterySnapshot(Array.Empty<BatteryReading>(), null, false, 0, default);
        check(new BatterySnapshot(Array.Empty<BatteryReading>(), null, false, 2, default).NoBattery, "Empty battery bays alone are still no battery");
        reject(() => new BatterySnapshot(Array.Empty<BatteryReading>(), null, false, -1, default), "Negative empty-slot count rejected");
        check(none.NoBattery, "Successful enumeration with no batteries means no battery (desktop)");
        check(!new BatterySnapshot(Array.Empty<BatteryReading>(), 5, false, 0, default).NoBattery, "Enumeration failure is not 'no battery'");
        var two = new BatterySnapshot(new[] { Reading(1), Reading(0) }, null, false, 0, default);
        check(two.Batteries.Select(x => x.Index).SequenceEqual(new[] { 0, 1 }) && !two.NoBattery, "Multiple batteries kept in order");
        reject(() => new BatterySnapshot(new[] { Reading(0), Reading(0) }, null, false, 0, default), "Duplicate battery index rejected");
        reject(() => new BatterySnapshot(Enumerable.Range(0, BatteryHealth.MaxBatteries + 1).Select(Reading), null, false, 0, default), "Battery count bounded");
        reject(() => ((IList<BatteryReading>)two.Batteries).Clear(), "Battery snapshot immutable");

        // Event log query and summarisation from injected events.
        string query = SleepHistory.Query();
        check(query.Contains("EventID=42") && query.Contains("EventID=107") && query.Contains("EventID=1)") && query.Contains(SleepHistory.KernelPower) && query.Contains(SleepHistory.Troubleshooter) && query.Contains("<= 604800000"), "Query limited to the three event kinds within 7 days");
        reject(() => SleepHistory.Query(0), "Zero-day query rejected"); reject(() => SleepHistory.Query(32), "Over-long query rejected");
        check(SleepHistory.Kind(SleepHistory.KernelPower, 42) == SleepEventKind.EnteringSleep && SleepHistory.Kind(SleepHistory.KernelPower, 107) == SleepEventKind.Resumed && SleepHistory.Kind(SleepHistory.Troubleshooter, 1) == SleepEventKind.ReturnedFromLowPower, "Event kinds mapped");
        check(SleepHistory.Kind(SleepHistory.KernelPower, 1) is null && SleepHistory.Kind(SleepHistory.Troubleshooter, 42) is null && SleepHistory.Kind(null, 42) is null && SleepHistory.Kind("Other", 107) is null, "Other providers or IDs ignored");

        var t0 = new DateTimeOffset(2026, 10, 20, 22, 0, 0, TimeSpan.Zero);
        var events = new List<SleepEvent>
        {
            new(t0, SleepEventKind.EnteringSleep, 4, 0, null, null, null),
            new(t0.AddHours(8), SleepEventKind.Resumed, null, null, null, null, null),
            new(t0.AddHours(8).AddSeconds(5), SleepEventKind.ReturnedFromLowPower, null, null, "Device -HID-compliant mouse", t0, t0.AddHours(8)),
            new(t0.AddDays(1), SleepEventKind.EnteringSleep, 4, 7, null, null, null),
            new(t0.AddDays(1).AddHours(1), SleepEventKind.ReturnedFromLowPower, null, null, "Power Button", t0.AddDays(1), t0.AddDays(1).AddHours(1)),
            new(t0.AddDays(2), SleepEventKind.ReturnedFromLowPower, null, null, "Device -HID-compliant mouse", null, null),
            new(t0.AddDays(2).AddMinutes(1), SleepEventKind.ReturnedFromLowPower, null, null, "  ", t0.AddDays(2), t0.AddDays(1)),
        };
        var summary = SleepHistory.Summarize(events);
        check(summary.Sleeps == 2 && summary.Resumes == 1 && summary.Wakes == 4, "Event counts per kind");
        check(summary.LastSleep == t0.AddDays(1) && summary.LastResume == t0.AddDays(2).AddMinutes(1), "Last sleep and last wake from newest events");
        check(summary.LastWakeSource == "Not recorded", "Blank newest wake source shown as not recorded");
        check(summary.LongestSleep == TimeSpan.FromHours(8), "Longest sleep from paired sleep/wake times; reversed pairs ignored");
        check(summary.WakeSources.Count == 3 && summary.WakeSources[0] == new WakeSourceCount("Device -HID-compliant mouse", 2), "Wake sources counted, most frequent first");
        var empty = SleepHistory.Summarize(Array.Empty<SleepEvent>());
        check(empty.Sleeps == 0 && empty.LastSleep is null && empty.LastResume is null && empty.LastWakeSource is null && empty.LongestSleep is null && empty.WakeSources.Count == 0, "Empty history gives no fabricated times");
        var many = Enumerable.Range(0, 20).Select(i => new SleepEvent(t0.AddMinutes(i), SleepEventKind.ReturnedFromLowPower, null, null, "S" + i, null, null)).ToArray();
        check(SleepHistory.Summarize(many).WakeSources.Count == SleepHistory.MaxWakeSources, "Wake-source list bounded");
        check(SleepHistory.Source("A\u0007B") == "A[U+0007]B" && SleepHistory.Source(new string('w', 400)).Length <= SleepHistory.MaxSourceChars + 1 && SleepHistory.Source(null) == "Not recorded", "Wake-source text escaped and bounded");

        var snapshot = new SleepHistorySnapshot(events, SleepLogStatus.Read, null, false, false, t0.AddDays(-7), t0);
        check(snapshot.Events[0].Time == t0.AddDays(2).AddMinutes(1) && snapshot.Events.Count == events.Count, "History newest first");
        var denied = new SleepHistorySnapshot(Array.Empty<SleepEvent>(), SleepLogStatus.AccessDenied, 5, false, false, t0, t0);
        check(denied.Events.Count == 0 && denied.Status == SleepLogStatus.AccessDenied && SleepHistory.Status(denied.Status).Contains("denied"), "Access denied reported as such, not as no history");
        reject(() => new SleepHistorySnapshot(Array.Empty<SleepEvent>(), SleepLogStatus.Read, 5, false, false, t0, t0), "Read status cannot carry an error code");
        reject(() => new SleepHistorySnapshot(Array.Empty<SleepEvent>(), SleepLogStatus.Failed, null, false, false, t0, t0), "Failure needs its error code");
        reject(() => new SleepHistorySnapshot(Enumerable.Repeat(events[0], SleepHistory.MaxEvents + 1), SleepLogStatus.Read, null, true, false, t0, t0), "Event count bounded to 200");
        reject(() => SleepHistory.Name((SleepEventKind)7), "Unknown event kind rejected");

        check(SleepHistory.ExecutionFlags(0x80000003).SequenceEqual(new[] { "System required", "Display required", "Continuous" }) && SleepHistory.ExecutionFlags(0).Count == 0 && SleepHistory.ExecutionFlags(0x100).SequenceEqual(new[] { "Other bits" }), "Execution-state flags decoded, unknown bits kept visible");
        check(SleepHistory.RequestsCommand == "powercfg /requests" && SleepHistory.LastWakeCommand == "powercfg /lastwake", "Admin commands are fixed display text");

        var prefs = new Prefs();
        foreach (string id in new[] { "doctor.battery", "doctor.sleephistory" })
        {
            var action = Catalog.Actions.Single(x => x.Id == id);
            check(action.Feature == Feature.Doctor && !action.RequiresFiles && !Catalog.Available(prefs, Array.Empty<string>()).Any(x => x.Id == id), "Default off and no files: " + id);
        }
        prefs.Set(Feature.Doctor, true);
        check(new[] { "doctor.battery", "doctor.sleephistory" }.All(id => Catalog.Available(prefs, Array.Empty<string>()).Any(x => x.Id == id)), "Available once Doctor is on");

        foreach (string text in new[] { BatteryHealth.Scope, BatteryHealth.Limits, SleepHistory.Scope, SleepHistory.Limits, SleepHistory.AdminNote, SleepHistory.ExecutionStateNote, "Unknown", "Not recorded", "Plugged in", "Discharging", "Charging", "Critical", "System required", "Display required", "User present", "Away mode required", "Continuous", "Other bits", "Read", "(units unknown)",
            BatteryHealth.Kind(0), BatteryHealth.Kind(BatteryHealth.SystemBattery), BatteryHealth.Kind(BatteryHealth.SystemBattery | BatteryHealth.ShortTerm), BatteryHealth.Technology(0), BatteryHealth.Technology(1) })
            check(Strings.Table.ContainsKey(text), "Battery/sleep text translated: " + text[..Math.Min(40, text.Length)]);
        foreach (var c in Enum.GetValues<BatteryHealthClass>()) check(Strings.Table.ContainsKey(BatteryHealth.Name(c)), "Health class translated: " + c);
        foreach (var r in Enum.GetValues<BatteryUnknownReason>()) if (r != BatteryUnknownReason.None) check(Strings.Table.ContainsKey(BatteryHealth.Reason(r)), "Unknown reason translated: " + r);
        foreach (var k in Enum.GetValues<SleepEventKind>()) check(Strings.Table.ContainsKey(SleepHistory.Name(k)), "Event kind translated: " + k);
        foreach (var s in Enum.GetValues<SleepLogStatus>()) check(Strings.Table.ContainsKey(SleepHistory.Status(s)), "Log status translated: " + s);
    }

    private static BatteryReading Reading(int index) => new(index, null, null, new(BatteryHealth.SystemBattery, 1, "LION", 50_000, 45_000, 100), null, new(BatteryHealth.PowerOnLine, 40_000, 12_000, 0), null, null, null, null);
}
