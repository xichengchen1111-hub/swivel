using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Swivel.Core;

// Raw values copied from the battery class driver's BATTERY_INFORMATION and BATTERY_STATUS.
// Nothing here is converted to zero: unknown sentinels stay visible as unknown.
public sealed record BatteryInformation(uint Capabilities, byte Technology, string Chemistry, uint DesignedCapacity, uint FullChargedCapacity, uint CycleCount);
public sealed record BatteryStatus(uint PowerState, uint Capacity, uint Voltage, int Rate);
// A string level of IOCTL_BATTERY_QUERY_INFORMATION: either text or the Windows error code.
public sealed record BatteryText(string? Value, uint? Error);
public sealed record BatteryReading(int Index, uint? OpenError, uint? TagError, BatteryInformation? Information, uint? InformationError, BatteryStatus? Status, uint? StatusError, BatteryText? DeviceName, BatteryText? Manufacturer, BatteryText? SerialNumber);

public sealed class BatterySnapshot
{
    public IReadOnlyList<BatteryReading> Batteries { get; }
    public uint? EnumerationError { get; }
    public bool Truncated { get; }
    // Interfaces that answered with battery tag zero: an empty bay, not a battery.
    public int EmptySlots { get; }
    public DateTimeOffset Captured { get; }
    public BatterySnapshot(IEnumerable<BatteryReading> batteries, uint? enumerationError, bool truncated, int emptySlots, DateTimeOffset captured)
    {
        var rows = batteries.ToArray();
        if (rows.Length > BatteryHealth.MaxBatteries || rows.Select(x => x.Index).Distinct().Count() != rows.Length || emptySlots is < 0 or > BatteryHealth.MaxBatteries) throw new ArgumentException("Invalid battery snapshot.");
        Batteries = Array.AsReadOnly(rows.OrderBy(x => x.Index).ToArray()); EnumerationError = enumerationError; Truncated = truncated; EmptySlots = emptySlots; Captured = captured;
    }
    // Enumeration succeeded and Windows listed no battery interface: typical for desktops.
    public bool NoBattery => EnumerationError is null && Batteries.Count == 0;
}

public enum BatteryHealthClass { Unknown, Good, Reduced, Worn }
public enum BatteryUnknownReason { None, NoInformation, DesignUnknown, FullUnknown, RelativeCapacity }
public sealed record BatteryHealthResult(BatteryHealthClass Class, double? Percent, BatteryUnknownReason Reason);

public static class BatteryHealth
{
    public const int MaxBatteries = 16;
    public const int MaxTextChars = 256;
    public const uint UnknownCapacity = 0xFFFFFFFF, UnknownVoltage = 0xFFFFFFFF;
    public const int UnknownRate = unchecked((int)0x80000000);
    public const uint SystemBattery = 0x80000000, CapacityRelative = 0x40000000, ShortTerm = 0x20000000;
    public const uint PowerOnLine = 1, Discharging = 2, Charging = 4, Critical = 8;
    public const double GoodAtLeast = 80, ReducedAtLeast = 60;

    public const string Scope = "One explicit read-only sample of each battery the Windows battery class driver lists: designed and full-charge capacity, cycle count, chemistry and current charge status. Device name, manufacturer and serial number are read but hidden until you show them. No battery setting, charge limit or calibration is changed; no administrator command, battery report or network request is used.";
    public const string Limits = "Health is full-charge capacity divided by designed capacity, as reported by the battery firmware at capture time. Many batteries report zero or unknown values, and some only report relative capacity; those are shown as unknown, never as 0%. A full-charge value above design is reported as is. Cycle count zero often means not reported. This is not a calibration, safety or warranty verdict, and values from external batteries or UPS units may differ in meaning.";

    public static bool Known(uint value) => value != 0 && value != UnknownCapacity;

    public static BatteryHealthResult Classify(BatteryInformation? info)
    {
        if (info is null) return new(BatteryHealthClass.Unknown, null, BatteryUnknownReason.NoInformation);
        if ((info.Capabilities & CapacityRelative) != 0) return new(BatteryHealthClass.Unknown, null, BatteryUnknownReason.RelativeCapacity);
        if (!Known(info.DesignedCapacity)) return new(BatteryHealthClass.Unknown, null, BatteryUnknownReason.DesignUnknown);
        if (!Known(info.FullChargedCapacity)) return new(BatteryHealthClass.Unknown, null, BatteryUnknownReason.FullUnknown);
        // Classify on the one-decimal value that is shown, so 79.96% (shown 80.0%) is Good.
        double percent = Math.Round(info.FullChargedCapacity * 100.0 / info.DesignedCapacity, 1, MidpointRounding.AwayFromZero);
        return new(percent >= GoodAtLeast ? BatteryHealthClass.Good : percent >= ReducedAtLeast ? BatteryHealthClass.Reduced : BatteryHealthClass.Worn, percent, BatteryUnknownReason.None);
    }

    public static string Name(BatteryHealthClass value) => value switch
    {
        BatteryHealthClass.Good => "Good (80% or more of design capacity)",
        BatteryHealthClass.Reduced => "Reduced (60–79% of design capacity)",
        BatteryHealthClass.Worn => "Worn (below 60% of design capacity)",
        BatteryHealthClass.Unknown => "Unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
    public static string Reason(BatteryUnknownReason reason) => reason switch
    {
        BatteryUnknownReason.None => "",
        BatteryUnknownReason.NoInformation => "Battery information could not be read.",
        BatteryUnknownReason.DesignUnknown => "Designed capacity is not reported (zero or unknown).",
        BatteryUnknownReason.FullUnknown => "Full-charge capacity is not reported (zero or unknown).",
        BatteryUnknownReason.RelativeCapacity => "This battery reports relative capacity only, so wear cannot be measured.",
        _ => throw new ArgumentOutOfRangeException(nameof(reason))
    };

    // Percent with one decimal, invariant digits; null stays "Unknown" rather than 0%.
    public static string Percent(double? value) => value is double v ? v.ToString("0.0", CultureInfo.InvariantCulture) + "%" : "Unknown";

    // "mWh" only for absolute-capacity batteries; relative units are named as such.
    public static string Capacity(uint value, uint capabilities) => !Known(value) ? "Unknown (" + value.ToString(CultureInfo.InvariantCulture) + ")"
        : value.ToString("N0", CultureInfo.InvariantCulture) + ((capabilities & CapacityRelative) != 0 ? " (relative units)" : " mWh");
    // Null capabilities: BATTERY_INFORMATION failed, so mWh versus relative units is unknown.
    public static string RemainingCapacity(uint value, uint? capabilities) => value == UnknownCapacity ? "Unknown"
        : value.ToString("N0", CultureInfo.InvariantCulture) + Unit(capabilities, " mWh");
    public static string Voltage(uint value) => value == UnknownVoltage ? "Unknown" : (value / 1000.0).ToString("0.000", CultureInfo.InvariantCulture) + " V";
    public static string Rate(int value, uint? capabilities) => value == UnknownRate ? "Unknown"
        : value.ToString("N0", CultureInfo.InvariantCulture) + Unit(capabilities, " mW");
    private static string Unit(uint? capabilities, string absolute) => capabilities is not uint c ? " (units unknown)" : (c & CapacityRelative) != 0 ? " (relative units)" : absolute;
    public static string Cycles(uint value) => value == 0 ? "Not reported (0)" : value.ToString(CultureInfo.InvariantCulture);
    public static double? ChargePercent(BatteryInformation? info, BatteryStatus? status)
    {
        if (info is null || status is null || status.Capacity == UnknownCapacity || !Known(info.FullChargedCapacity)) return null;
        return Math.Min(100.0, status.Capacity * 100.0 / info.FullChargedCapacity);
    }
    public static IReadOnlyList<string> PowerStates(uint state)
    {
        var names = new List<string>();
        if ((state & PowerOnLine) != 0) names.Add("Plugged in");
        if ((state & Discharging) != 0) names.Add("Discharging");
        if ((state & Charging) != 0) names.Add("Charging");
        if ((state & Critical) != 0) names.Add("Critical");
        return names;
    }
    public static string Kind(uint capabilities) => (capabilities & SystemBattery) == 0 ? "Not a system battery (for example a UPS)"
        : (capabilities & ShortTerm) != 0 ? "Short-term system battery" : "System battery";
    public static string Technology(byte value) => value switch { 0 => "Non-rechargeable", 1 => "Rechargeable", _ => "Unknown" };
    // The driver reports four ASCII characters such as "LION"; anything else is shown escaped.
    public static string ChemistryText(ReadOnlySpan<byte> raw)
    {
        var text = new StringBuilder();
        foreach (byte b in raw) { if (b == 0) break; text.Append(b is >= 0x20 and < 0x7F ? (char)b : '?'); }
        return text.ToString().TrimEnd();
    }
    // Battery strings are returned without a guaranteed terminator; cut at the first NUL and bound.
    public static string DriverText(ReadOnlySpan<char> raw)
    {
        int end = raw.IndexOf('\0'); if (end >= 0) raw = raw[..end];
        if (raw.Length > MaxTextChars) raw = raw[..MaxTextChars];
        return DoctorFacts.VisibleBounded(raw.ToString().Trim(), MaxTextChars);
    }
}
