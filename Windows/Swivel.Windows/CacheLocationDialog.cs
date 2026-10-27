using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class CacheLocationDialog:Window
{
    internal CacheLocation? Selected{get;private set;}
    internal CacheLocationDialog(App app,IReadOnlyList<CacheLocation> choices)
    {
        Title=app.L("Inspect User Cache Locations");Width=840;Height=760;MinWidth=480;MinHeight=380;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(20)};var footer=new WrapPanel();DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        var scan=UI.Button(app.L("Measure Selected Location"),()=>{if(Selected?.CanAttempt==true)DialogResult=true;});scan.IsEnabled=false;footer.Children.Add(scan);
        var cancel=UI.Button(app.L("Cancel"),()=>DialogResult=false);cancel.IsCancel=true;footer.Children.Add(cancel);
        var body=new StackPanel();root.Children.Add(UI.Scroll(body));Content=root;body.Children.Add(UI.Text(app.L(CacheLocations.Scope),12));body.Children.Add(UI.Text(app.L(CacheLocations.Warning),12));
        foreach(var choice in choices)
        {
            var radio=new RadioButton{Content=app.L(CacheLocations.Name(choice.Kind)),GroupName="CacheLocation",IsEnabled=choice.CanAttempt,IsChecked=false,Margin=new Thickness(0,18,0,4)};
            radio.Checked+=(_,_)=>{if(!choice.CanAttempt)return;Selected=choice;scan.IsEnabled=true;};AutomationProperties.SetName(radio,app.L(CacheLocations.Name(choice.Kind)));body.Children.Add(radio);
            body.Children.Add(new TextBox{Text=CacheLocations.VisiblePath(choice.ReportedPath),IsReadOnly=true,TextWrapping=TextWrapping.Wrap,MaxHeight=110,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
            body.Children.Add(UI.Text(app.L(choice.Issue??"Path passed basic checks; access and safety are checked only after you choose Measure."),12));
        }
        SourceInitialized+=(_,_)=>WebRouteLayout.Fit(this,Owner??this);DpiChanged+=(_,_)=>WebRouteLayout.Fit(this,this);
    }
}
