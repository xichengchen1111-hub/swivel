using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Swivel.Core;

namespace Swivel.Windows;

internal static class WindowsDoctorPage
{
    internal static UIElement Build(App app,Window owner,Action<string> report)
    {
        var page=new StackPanel{Margin=new Thickness(4)};page.Children.Add(UI.Text(app.L("Windows Doctor"),22));
        page.Children.Add(UI.Text(app.L(WindowsDoctor.Intro)));page.Children.Add(UI.Text(app.L(WindowsDoctor.Scope),12));
        bool enabled=app.Prefs.IsOn(Feature.Doctor);if(!enabled)page.Children.Add(UI.Text(app.L("Enable Windows Doctor in Settings first.")));
        foreach(var area in WindowsDoctor.Areas)
        {
            string status=app.L(area.Status==DoctorAreaStatus.Planned?"Not Implemented Yet":"Available with Limits");
            var header=new StackPanel();header.Children.Add(UI.Text(app.L(area.Title),16));header.Children.Add(UI.Text(status,12));
            var content=new StackPanel{Margin=new Thickness(12,4,12,12)};content.Children.Add(UI.Text(app.L(area.Description)));
            if(area.Status==DoctorAreaStatus.Planned)content.Children.Add(UI.Text(app.L(WindowsDoctor.Missing),12));
            foreach(string id in area.ActionIds)
            {
                var action=WindowsDoctor.ResolveAction(id);
                var button=UI.Button(app.L(action.Title),async()=>await app.RunActionAsync(action,Array.Empty<string>(),owner,report));button.IsEnabled=enabled;content.Children.Add(button);
            }
            var expander=new Expander{Header=header,Content=content,IsExpanded=area.Id=="summary",Margin=new Thickness(0,8,0,0),HorizontalContentAlignment=HorizontalAlignment.Stretch};
            AutomationProperties.SetName(expander,app.L(area.Title)+" · "+status);page.Children.Add(expander);
        }
        page.Children.Add(UI.Text(app.L("These buttons only open the named Windows Settings pages. Swivel does not apply fixes."),12));
        var settings=WindowsSettings.Buttons(app);settings.IsEnabled=enabled;page.Children.Add(settings);return page;
    }
}
