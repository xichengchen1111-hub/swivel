using System;
using System.Collections.Generic;
using System.Linq;

namespace Swivel.Core;

public enum IdlePowerSetting { DisplayOff, Sleep, Hibernate }
public sealed record IdlePowerDescriptor(IdlePowerSetting Kind,Guid Subgroup,Guid Setting);
public sealed record PowerTimeout(IdlePowerSetting Kind,uint? AcSeconds,uint? DcSeconds,uint? AcError,uint? DcError);
public sealed record ReportedPowerState(byte AcLine,byte BatteryFlags,byte BatteryPercent,byte BatterySaver,uint BatteryLifeSeconds,uint FullBatteryLifeSeconds);
public sealed class PowerPlanSnapshot
{
    public ReportedPowerState? State{get;}public uint? StateError{get;}
    public Guid? Scheme{get;}public uint? SchemeError{get;}public bool MissingSchemePointer{get;}public uint? CleanupError{get;}
    public IReadOnlyList<PowerTimeout> Timeouts{get;}public DateTimeOffset Captured{get;}
    public PowerPlanSnapshot(ReportedPowerState? state,uint? stateError,Guid? scheme,uint? schemeError,bool missingSchemePointer,uint? cleanupError,IEnumerable<PowerTimeout> timeouts,DateTimeOffset captured)
    {
        var rows=timeouts.ToArray();
        if(rows.Length>3||rows.Select(x=>x.Kind).Distinct().Count()!=rows.Length||rows.Any(x=>!Enum.IsDefined(x.Kind)||(x.AcSeconds is null)==(x.AcError is null)||(x.DcSeconds is null)==(x.DcError is null)))throw new ArgumentException("Invalid power-plan snapshot.");
        State=state;StateError=stateError;Scheme=scheme;SchemeError=schemeError;MissingSchemePointer=missingSchemePointer;CleanupError=cleanupError;Timeouts=Array.AsReadOnly(rows);Captured=captured;
    }
}
public static class PowerPlans
{
    public const string Scope="Explicit read-only reported power state and three stored timeout values from one captured active-plan GUID, for plugged-in and battery power. No plan name, battery serial number, sleep history or application request is read. Calls are separate historical samples, not an atomic current-state or effective-policy verdict. Group Policy, power-mode overlays, adaptive behavior and Modern Standby can change what actually happens.";
    public const string Limits="Display and sleep timeout zero mean no idle timeout in this stored plan. Hibernate timeout zero is an adaptive hibernate threshold setting, not a Never verdict. Missing values are unavailable, not zero. Battery-life seconds are Windows estimates, not battery wear or guaranteed runtime. Battery health, sleep blockers, supported sleep states and the cause of wake/sleep failures are not diagnosed. No settings are changed, no sleep/hibernate request, command, administrator action, network test or assistant upload is performed.";
    public static IReadOnlyList<IdlePowerDescriptor> Settings{get;}=Array.AsReadOnly(new[]{
        new IdlePowerDescriptor(IdlePowerSetting.DisplayOff,new("7516b95f-f776-4464-8c53-06167f40cc99"),new("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e")),
        new IdlePowerDescriptor(IdlePowerSetting.Sleep,new("238c9fa8-0aad-41ed-83f4-97be242c8f20"),new("29f6c1db-86da-48c5-9fdb-f2b67b1f44da")),
        new IdlePowerDescriptor(IdlePowerSetting.Hibernate,new("238c9fa8-0aad-41ed-83f4-97be242c8f20"),new("9d7815a6-7ee4-497e-8888-515a05f02364"))
    });
    public static string Name(IdlePowerSetting setting)=>setting switch{IdlePowerSetting.DisplayOff=>"Configured Display-off Timeout",IdlePowerSetting.Sleep=>"Configured Sleep Timeout",IdlePowerSetting.Hibernate=>"Configured Hibernate Timeout",_=>throw new ArgumentOutOfRangeException(nameof(setting))};
    public static string Zero(IdlePowerSetting setting)=>setting switch{IdlePowerSetting.DisplayOff=>"No Idle Display-off by This Plan (0 seconds)",IdlePowerSetting.Sleep=>"No Idle Sleep by This Plan (0 seconds)",IdlePowerSetting.Hibernate=>"0 seconds (Adaptive Hibernate Threshold)",_=>throw new ArgumentOutOfRangeException(nameof(setting))};
}
