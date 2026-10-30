using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Swivel.Core;
using Swivel.Windows;

internal static class BatterySleepNativeChecks
{
    private static uint CtlCode(uint deviceType, uint function, uint method, uint access) => (deviceType << 16) | (access << 14) | (function << 2) | method;

    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        // IOCTL codes and structure marshalling against the documented Windows layouts.
        check(BatteryReader.QueryTag == CtlCode(0x29, 0x10, 0, 1) && BatteryReader.QueryInformation == CtlCode(0x29, 0x11, 0, 1) && BatteryReader.QueryStatus == CtlCode(0x29, 0x13, 0, 1), "Battery IOCTLs are FILE_DEVICE_BATTERY, METHOD_BUFFERED, FILE_READ_ACCESS only");
        check(BatteryReader.BatteryClass == new Guid("72631e54-78a4-11d0-bcf7-00aa00b7b32a"), "GUID_DEVCLASS_BATTERY / GUID_DEVICE_BATTERY");
        Type Nested(string name) => typeof(BatteryReader).GetNestedType(name, BindingFlags.NonPublic)!;
        check(Marshal.SizeOf(Nested("DeviceInterfaceData")) == (IntPtr.Size == 8 ? 32 : 28), "SP_DEVICE_INTERFACE_DATA size");
        check(Marshal.SizeOf(Nested("NativeQueryInformation")) == 12, "BATTERY_QUERY_INFORMATION is 12 bytes");
        var info = Nested("NativeBatteryInformation");
        check(Marshal.SizeOf(info) == 36, "BATTERY_INFORMATION is 36 bytes");
        foreach (var (field, offset) in new[] { ("Capabilities", 0), ("Technology", 4), ("Chemistry0", 8), ("DesignedCapacity", 12), ("FullChargedCapacity", 16), ("CriticalBias", 28), ("CycleCount", 32) })
            check(Marshal.OffsetOf(info, field).ToInt32() == offset, "BATTERY_INFORMATION offset " + field);
        check(Marshal.SizeOf(Nested("NativeWaitStatus")) == 20, "BATTERY_WAIT_STATUS is 20 bytes");
        var status = Nested("NativeBatteryStatus");
        check(Marshal.SizeOf(status) == 16 && Marshal.OffsetOf(status, "Rate").ToInt32() == 12, "BATTERY_STATUS is 16 bytes with signed Rate last");
        // Managed layout used by MemoryMarshal equals the native layout.
        var fill = new byte[36]; for (int i = 0; i < fill.Length; i++) fill[i] = (byte)i;
        var raw = MemoryMarshal.Read<BatteryReader.NativeBatteryInformation>(fill);
        check(raw.Capabilities == BitConverter.ToUInt32(fill, 0) && raw.Chemistry0 == 8 && raw.DesignedCapacity == BitConverter.ToUInt32(fill, 12) && raw.CycleCount == BitConverter.ToUInt32(fill, 32), "MemoryMarshal reads BATTERY_INFORMATION at the native offsets");

        // Real enumeration on this machine: a runner normally has no battery; a laptop returns values.
        reject(() => BatteryReader.Read(new CancellationToken(true)), "Precancelled battery read calls no native API");
        var clock = Stopwatch.StartNew();
        BatterySnapshot? snapshot = null;
        try { snapshot = BatteryReader.Read(default); } catch (Exception e) { check(false, "Battery enumeration threw " + e.GetType().Name); }
        if (snapshot is not null)
        {
            Console.WriteLine("Battery read on this machine: " + (snapshot.NoBattery ? "no battery" : snapshot.Batteries.Count + " batteries") + " · enumeration error " + (snapshot.EnumerationError?.ToString() ?? "none") + " · empty bays " + snapshot.EmptySlots + " · " + clock.ElapsedMilliseconds + " ms");
            foreach (var b in snapshot.Batteries) Console.WriteLine("  battery " + b.Index + ": health " + BatteryHealth.Classify(b.Information) + " · status " + b.Status);
            check(snapshot.Batteries.Count <= BatteryHealth.MaxBatteries && clock.Elapsed < TimeSpan.FromSeconds(30), "Battery enumeration bounded and returns 'no battery' or values without throwing");
        }

        // Event log: bounded, cancellable, access denied reported rather than thrown.
        reject(() => SleepHistoryReader.ReadHistory(new CancellationToken(true)), "Precancelled event-log read opens nothing");
        reject(() => SleepHistoryReader.ReadHistory(SleepHistory.Query(), 0, SleepHistory.TimeLimit, default), "Zero event bound rejected");
        reject(() => SleepHistoryReader.ReadHistory(SleepHistory.Query(), SleepHistory.MaxEvents + 1, SleepHistory.TimeLimit, default), "Event bound above 200 rejected");
        clock.Restart();
        var history = SleepHistoryReader.ReadHistory(default);
        Console.WriteLine("Sleep history on this machine: " + history.Status + " · " + history.Events.Count + " events · truncated " + history.Truncated + " · timed out " + history.TimedOut + " · " + clock.ElapsedMilliseconds + " ms");
        check(history.Events.Count <= SleepHistory.MaxEvents && clock.Elapsed < SleepHistory.TimeLimit + TimeSpan.FromSeconds(5), "Real event-log query bounded to 200 events and about 10 seconds");
        check(history.Status is SleepLogStatus.Read or SleepLogStatus.AccessDenied, "System log readable or access denied reported");
        check(history.Events.All(e => e.Time >= history.WindowStart.AddMinutes(-5)), "Only events from the last 7 days");
        var one = SleepHistoryReader.ReadHistory(SleepHistory.Query(), 1, SleepHistory.TimeLimit, default);
        check(one.Events.Count <= 1 && (one.Truncated == (history.Events.Count > 1 || history.Truncated) || one.Status != SleepLogStatus.Read), "Event bound stops reading and reports truncation");
        // A broad query over every System event exercises the time limit and cancellation paths.
        const string all = "*[System[TimeCreated[timediff(@SystemTime) <= 604800000]]]";
        var limited = SleepHistoryReader.ReadHistory(all, SleepHistory.MaxEvents, TimeSpan.FromTicks(1), default);
        check(limited.TimedOut || limited.Status != SleepLogStatus.Read, "Time limit stops reading and is reported");
        using (var stop = new CancellationTokenSource())
        {
            stop.CancelAfter(TimeSpan.FromMilliseconds(30)); clock.Restart(); bool cancelled = false;
            try { SleepHistoryReader.ReadHistory(all, SleepHistory.MaxEvents, TimeSpan.FromSeconds(60), stop.Token); } catch (OperationCanceledException) { cancelled = true; }
            check(cancelled && clock.Elapsed < TimeSpan.FromSeconds(10), "Cancelling a long event-log read stops promptly with cancellation");
        }

        reject(() => SleepHistoryReader.ReadStayAwake(false, false, null, new CancellationToken(true)), "Precancelled execution-state read calls nothing");
        var awake = SleepHistoryReader.ReadStayAwake(true, true, DateTimeOffset.UnixEpoch, default);
        Console.WriteLine("Execution state on this machine: " + (awake.ExecutionState is uint s ? "0x" + s.ToString("X8") : "unavailable, NTSTATUS " + awake.ExecutionStateStatus));
        check(awake.ExecutionState is not null && awake.ExecutionStateStatus is null, "CallNtPowerInformation SystemExecutionState answers without administrator rights");
        check(awake.KeepAwakeActive && awake.KeepAwakeDisplay && awake.KeepAwakeUntil == DateTimeOffset.UnixEpoch && SleepHistoryReader.ReadStayAwake(false, true, DateTimeOffset.UnixEpoch, default) is { KeepAwakeDisplay: false, KeepAwakeUntil: null }, "Keep Awake state passed through; inactive state carries no stale details");

        // Dialogs: no action buttons beyond copy/close, serials hidden by default, admin commands as text.
        var app = Application.Current as App ?? new App(validationHost: true); string previous = app.Prefs.Language;
        string[] keys = { "Health", "Why Unknown", "Battery Information", "Designed Capacity", "Full-charge Capacity", "Cycle Count", "Chemistry", "Technology", "Battery Kind", "Current Status", "Power State", "Remaining Capacity", "Voltage", "Charge or Discharge Rate", "Device Name", "Manufacturer", "Serial Number", "Battery Unavailable",
            "Windows could not list battery devices.", "Windows reports no battery in this PC.", "Battery health could not be determined from the reported values.", "A battery holds much less charge than when new.", "A battery holds noticeably less charge than when new.", "Reported battery capacity looks normal.",
            "Lists the apps, services and drivers currently asking the PC or display to stay awake.", "Shows what last woke the PC from sleep.", "(relative units)" };
        foreach (string key in keys) check(Strings.Table.ContainsKey(key), "Battery/sleep dialog key translated: " + key);
        try
        {
            foreach (string language in new[] { "en", "zh-Hans", "zh-Hant", "ja" })
            {
                app.Prefs.SetLanguage(language);
                var fixture = new BatterySnapshot(new[]
                {
                    new BatteryReading(0, null, null, new(BatteryHealth.SystemBattery, 1, "LION", 57_000, 41_000, 0), null, new(BatteryHealth.Discharging, 20_500, 11_800, -6_200), null, new("DELL ABC", null), new("SMP", null), new("SERIAL-12345", null)),
                    new BatteryReading(1, null, null, new(BatteryHealth.SystemBattery | BatteryHealth.CapacityRelative, 1, "LiP", 100, 92, 5), null, new(BatteryHealth.PowerOnLine, BatteryHealth.UnknownCapacity, BatteryHealth.UnknownVoltage, BatteryHealth.UnknownRate), null, new(null, 1), new("", null), new(null, 50)),
                    new BatteryReading(2, null, null, new(BatteryHealth.SystemBattery, 1, "LION", 0, 0, 0), null, null, 31, null, null, null),
                    new BatteryReading(3, 5, null, null, null, null, null, null, null, null)
                }, null, false, 1, DateTimeOffset.UnixEpoch);
                var dialog = new BatteryHealthDialog(app, fixture);
                var footer = ((DockPanel)dialog.Content).Children.OfType<WrapPanel>().Single().Children.OfType<Button>().ToArray();
                check(footer.Length == 2, "Battery window only copies and closes: " + language);
                check(dialog.Reveal.IsChecked == false && !dialog.CurrentReport.Contains("SERIAL-12345") && !dialog.CurrentReport.Contains("DELL ABC") && !dialog.CurrentReport.Contains("SMP"), "Serial, name and maker hidden by default: " + language);
                check(dialog.CurrentReport.Contains("71.9%") && dialog.CurrentReport.Contains(app.L(BatteryHealth.Name(BatteryHealthClass.Reduced))), "Health percentage from full ÷ design: " + language);
                check(dialog.CurrentReport.Contains(app.L(BatteryHealth.Reason(BatteryUnknownReason.RelativeCapacity))) && dialog.CurrentReport.Contains(app.L(BatteryHealth.Reason(BatteryUnknownReason.DesignUnknown))) && !dialog.CurrentReport.Contains("0.0%"), "Relative and zero-design batteries are unknown, never 0%: " + language);
                check(dialog.CurrentReport.Contains(app.L("Not reported") + " (0)") && dialog.CurrentReport.Contains(app.L("Empty Battery Bays")) && dialog.CurrentReport.Contains(app.L("Windows Error Code") + " 5") && dialog.CurrentReport.Contains(app.L("Windows Error Code") + " 31"), "Zero cycles, empty bays and per-battery errors visible: " + language);
                dialog.Reveal.IsChecked = true;
                check(dialog.CurrentReport.Contains("SERIAL-12345") && dialog.CurrentReport.Contains("DELL ABC") && dialog.CurrentReport.Contains(app.L("Windows Error Code") + " 50"), "Explicit reveal shows names, serials and their errors: " + language);
                dialog.Reveal.IsChecked = false;
                check(!dialog.CurrentReport.Contains("SERIAL-12345"), "Hiding again removes serials from the report: " + language);
                if (language != "en") check(!dialog.CurrentReport.Contains("Designed Capacity") && !dialog.CurrentReport.Contains("Unknown"), "Battery report translated: " + language);
                dialog.Close();
                var none = new BatteryHealthDialog(app, new BatterySnapshot(Array.Empty<BatteryReading>(), null, false, 0, DateTimeOffset.UnixEpoch));
                check(none.CurrentReport.Contains(app.L("Windows reports no battery in this PC.")) && none.CurrentReport.Contains(app.L("None reported")), "Desktop without battery stated plainly: " + language);
                none.Close();

                var t0 = new DateTimeOffset(2026, 10, 20, 22, 0, 0, TimeSpan.Zero);
                var events = new[]
                {
                    new SleepEvent(t0, SleepEventKind.EnteringSleep, 4, 0, null, null, null),
                    new SleepEvent(t0.AddHours(8), SleepEventKind.Resumed, null, null, null, null, null),
                    new SleepEvent(t0.AddHours(8), SleepEventKind.ReturnedFromLowPower, null, null, "Device -USB Keyboard", t0, t0.AddHours(8)),
                };
                var sleep = new SleepHistoryDialog(app, new StayAwakeSnapshot(0x80000003, null, true, true, null, t0), new SleepHistorySnapshot(events, SleepLogStatus.Read, null, true, false, t0.AddDays(-7), t0));
                var sleepFooter = ((DockPanel)sleep.Content).Children.OfType<WrapPanel>().Single().Children.OfType<Button>().ToArray();
                check(sleepFooter.Length == 2 && sleep.CommandButtons.Count == 2, "Sleep window: Copy Report/Close plus two Copy Command buttons, nothing that runs: " + language);
                check(sleep.CurrentReport.Contains("powercfg /requests") && sleep.CurrentReport.Contains("powercfg /lastwake") && sleep.CurrentReport.Contains(app.L(SleepHistory.AdminNote)), "Administrator commands shown as text with never-run note: " + language);
                check(sleep.EventRows.Count == 3 && sleep.CurrentReport.Contains("Device -USB Keyboard") && sleep.CurrentReport.Contains(string.Format(app.L("{0} h {1} min"), 8, 0)) && sleep.CurrentReport.Contains(string.Format(app.L("Only the newest {0} events are shown."), 200)), "Events, wake source, longest sleep and truncation in report: " + language);
                check(sleep.CurrentReport.Contains("0x80000003") && sleep.CurrentReport.Contains(app.L("Continuous")) && sleep.CurrentReport.Contains(app.L("On until turned off")) && sleep.CurrentReport.Contains(app.L("Screen kept on")), "Execution state and Keep Awake in report: " + language);
                sleep.Close();
                var denied = new SleepHistoryDialog(app, new StayAwakeSnapshot(null, unchecked((int)0xC0000022), false, false, null, t0), new SleepHistorySnapshot(Array.Empty<SleepEvent>(), SleepLogStatus.AccessDenied, 5, false, false, t0, t0));
                check(denied.CurrentReport.Contains(app.L(SleepHistory.Status(SleepLogStatus.AccessDenied))) && denied.EventRows.Count == 0 && !denied.CurrentReport.Contains(app.L("No sleep or wake events were recorded in this period.")) && denied.CurrentReport.Contains("0xC0000022"), "Access denied stated; not shown as empty history: " + language);
                denied.Close();
            }
        }
        finally { app.Prefs.SetLanguage(previous); }
    }
}
