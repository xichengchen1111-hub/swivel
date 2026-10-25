using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class SleepCapabilityDialog:Window
{
    internal string CurrentReport{get;}="";
    internal SleepCapabilityDialog(App app,SleepCapabilitySnapshot snapshot)
    {
        Title=app.L("Reported Sleep Capabilities");Width=900;Height=760;MinWidth=500;MinHeight=380;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(20)};var footer=new WrapPanel();DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        footer.Children.Add(UI.Button(app.L("Copy Report"),()=>app.Try(()=>Clipboard.SetText(CurrentReport))));var close=UI.Button(app.L("Close"),()=>DialogResult=false);close.IsCancel=true;footer.Children.Add(close);
        var body=new StackPanel();root.Children.Add(UI.Scroll(body));Content=root;body.Children.Add(UI.Text(snapshot.Captured.ToString("g",CultureInfo.CurrentCulture),14));var rows=new List<Row>();
        if(snapshot.Error is uint error)rows.Add(new(app.L("Sleep Capabilities Unavailable"),app.L("Windows Error Code")+" "+error.ToString(CultureInfo.InvariantCulture)));
        else
        {
            foreach(var flag in snapshot.Flags)rows.Add(new(app.L(SleepCapabilities.Name(flag.Kind)),app.L(flag.Raw==0?"No (reported flag)":"Yes (reported flag)")+" ("+flag.Raw.ToString(CultureInfo.InvariantCulture)+")"));
            byte type=snapshot.HibernationFileType!.Value;rows.Add(new(app.L("Reported Hibernation-file Type"),app.L(SleepCapabilities.FileType(type))+" ("+type.ToString(CultureInfo.InvariantCulture)+")"));
        }
        var list=new ListView{Height=360,ItemsSource=rows,SelectionMode=SelectionMode.Single};var view=new GridView();view.Columns.Add(new GridViewColumn{Header=app.L("Field"),DisplayMemberBinding=new Binding("Name"),Width=425});view.Columns.Add(new GridViewColumn{Header=app.L("Value"),DisplayMemberBinding=new Binding("Value"),Width=400});list.View=view;VirtualizingPanel.SetIsVirtualizing(list,true);VirtualizingPanel.SetVirtualizationMode(list,VirtualizationMode.Recycling);ScrollViewer.SetCanContentScroll(list,true);AutomationProperties.SetName(list,Title);body.Children.Add(list);
        var report=new StringBuilder();report.AppendLine(app.L("Windows Doctor")+" · "+Title);report.AppendLine(snapshot.Captured.ToString("g",CultureInfo.CurrentCulture));foreach(var row in rows)report.AppendLine(row.Name+": "+row.Value);
        foreach(string note in new[]{SleepCapabilities.Scope,SleepCapabilities.Limits}){body.Children.Add(UI.Text(app.L(note),12));report.AppendLine();report.AppendLine(app.L(note));}CurrentReport=report.ToString();
        SourceInitialized+=(_,_)=>WebRouteLayout.Fit(this,Owner??this);DpiChanged+=(_,_)=>WebRouteLayout.Fit(this,this);
    }
    private sealed record Row(string Name,string Value);
}
