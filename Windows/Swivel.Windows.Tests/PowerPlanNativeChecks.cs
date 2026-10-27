using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Swivel.Core;
using Swivel.Windows;

internal static class PowerPlanNativeChecks
{
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        reject(()=>PowerPlanReader.Read(new CancellationToken(true)),"Precancelled power check calls no API and allocates no native GUID");
        var rows=new[]{new PowerTimeout(IdlePowerSetting.DisplayOff,0,uint.MaxValue,null,null),new PowerTimeout(IdlePowerSetting.Sleep,null,0,5,null),new PowerTimeout(IdlePowerSetting.Hibernate,0,null,null,2)};
        var snapshot=new PowerPlanSnapshot(new(255,255,255,255,uint.MaxValue,uint.MaxValue),null,Guid.Empty,null,false,123,rows,DateTimeOffset.UnixEpoch);
        var app=Application.Current as App??new App(validationHost:true);string previous=app.Prefs.Language;
        try
        {
            foreach(string language in new[]{"en","zh-Hans","zh-Hant","ja"})
            {
                app.Prefs.SetLanguage(language);var dialog=new PowerPlanDialog(app,snapshot);var root=(DockPanel)dialog.Content;
                check(root.Children.OfType<WrapPanel>().Single().Children.OfType<Button>().Count()==2,"Power snapshot only Copy/Close, no apply/sleep/hibernate commands");
                check(dialog.CurrentReport.Contains(app.L(PowerPlans.Zero(IdlePowerSetting.Hibernate)))&&dialog.CurrentReport.Contains(app.L(PowerPlans.Zero(IdlePowerSetting.DisplayOff)))&&dialog.CurrentReport.Contains(app.L(PowerPlans.Zero(IdlePowerSetting.Sleep))),"Native full report preserves distinct adaptive hibernate and no-idle display/sleep zero meanings");
                check(dialog.CurrentReport.Contains("0xFFFFFFFF")&&dialog.CurrentReport.Contains(app.L("Power Plan Memory Cleanup Failed"))&&dialog.CurrentReport.Contains(Guid.Empty.ToString("D")),"Unknown battery life and raw captured GUID/cleanup failure remain explicit");
                check(dialog.CurrentReport.Contains(app.L(PowerPlans.Scope))&&dialog.CurrentReport.Contains(app.L(PowerPlans.Limits)),"Copied power report contains complete policy/health/runtime limits");dialog.Close();
            }
            var partial=new PowerPlanDialog(app,new(null,5,null,null,true,null,Array.Empty<PowerTimeout>(),DateTimeOffset.UnixEpoch));
            check(partial.CurrentReport.Contains(app.L("Active plan returned no GUID (unavailable).")),"Null-success GUID is unavailable, never a fabricated default power plan");partial.Close();
        }
        finally{app.Prefs.SetLanguage(previous);}
    }
}
