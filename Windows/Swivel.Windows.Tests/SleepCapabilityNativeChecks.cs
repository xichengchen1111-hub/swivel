using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Swivel.Core;
using Swivel.Windows;

internal static class SleepCapabilityNativeChecks
{
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        reject(()=>SleepCapabilityReader.Read(new CancellationToken(true)),"Precancelled sleep capability check calls no native API");
        var native=typeof(SleepCapabilityReader).GetNestedType("NativeCapabilities",BindingFlags.NonPublic)!;
        check(Marshal.SizeOf(native)==76,"SYSTEM_POWER_CAPABILITIES modern Windows output is 76 bytes, not a truncated field prefix");
        foreach(var pair in new[]{("PowerButton",0),("SleepButton",1),("Lid",2),("S1",3),("S2",4),("S3",5),("S4",6),("S5",7),("HibernationFile",8),("HybridSleep",17),("WakeAlarm",19),("LowPowerIdle",20),("HibernationFileType",22),("ReservedWakeState",72)})
            check(Marshal.OffsetOf(native,pair.Item1).ToInt32()==pair.Item2,"Modern Windows byte offset "+pair.Item1);
        var app=Application.Current as App??new App(validationHost:true);string previous=app.Prefs.Language;
        try
        {
            foreach(string language in new[]{"en","zh-Hans","zh-Hant","ja"})
            {
                app.Prefs.SetLanguage(language);var snapshot=new SleepCapabilitySnapshot(SleepCapabilities.Fields.Select((f,i)=>new ReportedSleepFlag(f,(byte)(i==0?0:i==1?2:255))),253,null,DateTimeOffset.UnixEpoch);var dialog=new SleepCapabilityDialog(app,snapshot);var root=(DockPanel)dialog.Content;
                check(root.Children.OfType<WrapPanel>().Single().Children.OfType<Button>().Count()==2,"Sleep report only Copy and Close, no sleep/hibernate/powercfg/repair");
                check(dialog.CurrentReport.Contains(app.L("Yes (reported flag)")+" (2)")&&dialog.CurrentReport.Contains(app.L("No (reported flag)")+" (0)")&&dialog.CurrentReport.Contains("(255)"),"All original BOOLEAN bytes retained; nonzero includes 2 and 255");
                check(dialog.CurrentReport.Contains(app.L("Unknown Hibernation-file Type")+" (253)")&&dialog.CurrentReport.Contains(app.L(SleepCapabilities.Scope))&&dialog.CurrentReport.Contains(app.L(SleepCapabilities.Limits)),"Unknown hibernation file type and full support-not-guarantee limits copied");dialog.Close();
                var failed=new SleepCapabilityDialog(app,new(Array.Empty<ReportedSleepFlag>(),null,0,DateTimeOffset.UnixEpoch));
                check(failed.CurrentReport.Contains(app.L("Sleep Capabilities Unavailable"))&&failed.CurrentReport.Contains(app.L("Windows Error Code")+" 0")&&!failed.CurrentReport.Contains(app.L("No (reported flag)")),"Failure with LastError zero remains unavailable, no fabricated unsupported values");failed.Close();
            }
        }
        finally{app.Prefs.SetLanguage(previous);}
    }
}
