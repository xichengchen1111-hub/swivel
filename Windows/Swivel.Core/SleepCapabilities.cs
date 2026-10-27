using System;
using System.Collections.Generic;
using System.Linq;

namespace Swivel.Core;

public enum SleepCapability { PowerButton, SleepButton, Lid, S1, S2, S3, S4, S5, HibernationFile, HybridSleep, WakeAlarm, LowPowerIdle }
public sealed record ReportedSleepFlag(SleepCapability Kind,byte Raw);
public sealed class SleepCapabilitySnapshot
{
    public IReadOnlyList<ReportedSleepFlag> Flags{get;}
    public byte? HibernationFileType{get;}public uint? Error{get;}public DateTimeOffset Captured{get;}
    public SleepCapabilitySnapshot(IEnumerable<ReportedSleepFlag> flags,byte? hibernationFileType,uint? error,DateTimeOffset captured)
    {
        var rows=flags.ToArray();
        if(error is null ? hibernationFileType is null||rows.Length!=SleepCapabilities.Fields.Count||rows.Any(x=>!Enum.IsDefined(x.Kind))||rows.Select(x=>x.Kind).Distinct().Count()!=rows.Length : hibernationFileType is not null||rows.Length!=0)
            throw new ArgumentException("Invalid sleep capability snapshot.");
        Flags=Array.AsReadOnly(rows.OrderBy(x=>x.Kind).ToArray());HibernationFileType=hibernationFileType;Error=error;Captured=captured;
    }
}
public static class SleepCapabilities
{
    public static IReadOnlyList<SleepCapability> Fields{get;}=Array.AsReadOnly(Enum.GetValues<SleepCapability>());
    public const string Scope="One explicit read-only Windows power-capability sample: reported S1-S5, S0 low power idle, hibernation-file type and selected power-button, lid, hybrid-sleep and wake-alarm flags. A nonzero capability flag is reported as supported or present; all original byte values remain visible. Failed reads are unavailable, not unsupported.";
    public const string Limits="These are Windows-reported capabilities at capture time, not a guarantee that this PC can enter or wake from a state now. Drivers, firmware, policy and configuration can change availability. S5 means soft off, not sleep. A hibernation file or its type alone does not establish usable hibernation, enabled Fast Startup or a repair recommendation. This does not provide powercfg /a exclusion reasons, sleep-blocking processes, wake history, battery health or effective timeout behavior. No plan, file or setting is changed; no sleep, hibernation, restart, shutdown, administrator command, network test or assistant upload is requested.";
    public static string Name(SleepCapability field)=>field switch
    {
        SleepCapability.PowerButton=>"Reported Power-button Presence",SleepCapability.SleepButton=>"Reported Sleep-button Presence",SleepCapability.Lid=>"Reported Lid-switch Presence",
        SleepCapability.S1=>"Reported S1 Sleep Support",SleepCapability.S2=>"Reported S2 Sleep Support",SleepCapability.S3=>"Reported S3 Sleep Support",SleepCapability.S4=>"Reported S4 Hibernation Support",SleepCapability.S5=>"Reported S5 Soft-off Support",
        SleepCapability.HibernationFile=>"Reported Hibernation-file Presence",SleepCapability.HybridSleep=>"Reported Hybrid Sleep Support",SleepCapability.WakeAlarm=>"Reported Wake-alarm Support",SleepCapability.LowPowerIdle=>"Reported S0 Low Power Idle Support",_=>throw new ArgumentOutOfRangeException(nameof(field))
    };
    public static string FileType(byte raw)=>raw switch{0=>"No Hibernation File Reported",1=>"Reduced Hibernation File Reported",2=>"Full Hibernation File Reported",_=>"Unknown Hibernation-file Type"};
}
