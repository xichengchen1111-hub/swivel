using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Swivel.Core;
using Swivel.Windows;

internal static class WindowsDoctorPageChecks
{
    internal static void Run(Action<bool,string> check)
    {
        var app=Application.Current as App??new App(validationHost:true);bool enabled=app.Prefs.IsOn(Feature.Doctor);string language=app.Prefs.Language;var owner=new Window();int reports=0;
        try
        {
            foreach(string locale in new[]{"en","zh-Hans","zh-Hant","ja"})foreach(bool on in new[]{false,true})
            {
                app.Prefs.SetLanguage(locale);app.Prefs.Set(Feature.Doctor,on);string before=app.Prefs.Serialize();
                var page=(StackPanel)WindowsDoctorPage.Build(app,owner,_=>reports++);var areas=page.Children.OfType<Expander>().ToArray();
                check(areas.Length==13&&areas.Count(x=>x.IsExpanded)==1&&areas[0].IsExpanded,"Windows Doctor creates all native symptom areas without starting checks");
                int buttons=0;
                for(int i=0;i<areas.Length;i++)
                {
                    var body=(StackPanel)areas[i].Content;var actions=body.Children.OfType<Button>().ToArray();buttons+=actions.Length;
                    check(actions.Length==WindowsDoctor.Areas[i].ActionIds.Count&&actions.All(b=>b.IsEnabled==on),"Area buttons match closed existing routes and default-off state");
                    check(((StackPanel)areas[i].Header).Children.OfType<TextBlock>().Any(x=>x.Text==app.L(WindowsDoctor.Areas[i].Status==DoctorAreaStatus.Planned?"Not Implemented Yet":"Available with Limits")),"Each native area has a translated visible status without color-only meaning");
                    areas[i].IsExpanded=true;
                }
                check(buttons==WindowsDoctor.Areas.Sum(x=>x.ActionIds.Count)&&reports==0&&app.Prefs.Serialize()==before&&!app.Busy,"Constructing/expanding all Doctor areas reads no report, starts no work and changes no preferences");
            }
        }
        finally{app.Prefs.SetLanguage(language);app.Prefs.Set(Feature.Doctor,enabled);owner.Close();}
    }
}
