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

internal sealed class PowerPlanDialog:Window
{
    internal string CurrentReport{get;}="";
    internal PowerPlanDialog(App app,PowerPlanSnapshot snapshot)
    {
        Title=app.L("Power & Sleep Settings Snapshot");Width=900;Height=800;MinWidth=500;MinHeight=400;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(20)};var footer=new WrapPanel();DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        footer.Children.Add(UI.Button(app.L("Copy Report"),()=>app.Try(()=>Clipboard.SetText(CurrentReport))));var close=UI.Button(app.L("Close"),()=>DialogResult=false);close.IsCancel=true;footer.Children.Add(close);
        var body=new StackPanel();root.Children.Add(UI.Scroll(body));Content=root;body.Children.Add(UI.Text(snapshot.Captured.ToString("g",CultureInfo.CurrentCulture),14));
        var rows=new List<Row>();void Add(string label,string value)=>rows.Add(new(app.L(label),value));
        string Error(uint? code)=>code is uint error?app.L("Windows Error Code")+" "+error.ToString(CultureInfo.InvariantCulture):app.L("Unavailable");
        string Life(uint seconds)=>seconds==uint.MaxValue?app.L("Unknown")+" (0xFFFFFFFF)":seconds.ToString("N0",CultureInfo.CurrentCulture);
        if(snapshot.State is ReportedPowerState state)
        {
            Add("Power source",app.L(DoctorFacts.PowerSource(state.AcLine))+" ("+state.AcLine.ToString(CultureInfo.InvariantCulture)+")");
            Add("Battery level",app.L(DoctorFacts.Battery(state.BatteryFlags,state.BatteryPercent)));
            Add("Reported Battery Flags","0x"+state.BatteryFlags.ToString("X2",CultureInfo.InvariantCulture));Add("Reported Battery Percentage Byte",state.BatteryPercent.ToString(CultureInfo.InvariantCulture));
            Add("Reported Battery Saver State",app.L(state.BatterySaver switch{0=>"Off",1=>"On",_=>"Unknown"})+" ("+state.BatterySaver.ToString(CultureInfo.InvariantCulture)+")");
            Add("Reported Battery-life Estimate (seconds)",Life(state.BatteryLifeSeconds));Add("Reported Full-charge Lifetime (seconds)",Life(state.FullBatteryLifeSeconds));
        }
        else Add("Power State Unavailable",Error(snapshot.StateError));
        Add("Captured Active Plan GUID",snapshot.Scheme?.ToString("D")??(snapshot.MissingSchemePointer?app.L("Active plan returned no GUID (unavailable)."):Error(snapshot.SchemeError)));
        if(snapshot.CleanupError is uint cleanup)Add("Power Plan Memory Cleanup Failed",Error(cleanup));
        foreach(var timeout in snapshot.Timeouts)
        {
            string Value(uint? seconds,uint? error)=>seconds is uint n?n==0?app.L(PowerPlans.Zero(timeout.Kind)):n.ToString("N0",CultureInfo.CurrentCulture)+" "+app.L("seconds (configured)"):Error(error);
            string name=app.L(PowerPlans.Name(timeout.Kind));rows.Add(new(name+" · "+app.L("Plugged-in Power"),Value(timeout.AcSeconds,timeout.AcError)));rows.Add(new(name+" · "+app.L("Battery Power"),Value(timeout.DcSeconds,timeout.DcError)));
        }
        var list=new ListView{Height=390,ItemsSource=rows,SelectionMode=SelectionMode.Single};var view=new GridView();view.Columns.Add(new GridViewColumn{Header=app.L("Field"),DisplayMemberBinding=new Binding("Name"),Width=410});view.Columns.Add(new GridViewColumn{Header=app.L("Value"),DisplayMemberBinding=new Binding("Value"),Width=480});list.View=view;VirtualizingPanel.SetIsVirtualizing(list,true);VirtualizingPanel.SetVirtualizationMode(list,VirtualizationMode.Recycling);ScrollViewer.SetCanContentScroll(list,true);AutomationProperties.SetName(list,Title);body.Children.Add(list);
        var report=new StringBuilder();report.AppendLine(app.L("Windows Doctor")+" · "+Title);report.AppendLine(snapshot.Captured.ToString("g",CultureInfo.CurrentCulture));foreach(var row in rows)report.AppendLine(row.Name+": "+row.Value);
        foreach(string note in new[]{PowerPlans.Scope,PowerPlans.Limits}){body.Children.Add(UI.Text(app.L(note),12));report.AppendLine();report.AppendLine(app.L(note));}CurrentReport=report.ToString();
        SourceInitialized+=(_,_)=>WebRouteLayout.Fit(this,Owner??this);DpiChanged+=(_,_)=>WebRouteLayout.Fit(this,this);
    }
    private sealed record Row(string Name,string Value);
}
