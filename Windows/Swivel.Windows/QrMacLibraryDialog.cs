using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class QrMacLibraryDialog : Window
{
    internal const string NoMerge="No Mac source selected. This export contains only Windows entries and will replace the Mac library when installed. Choose the current Mac library to keep its entries.";
    internal const string Merge="Choose current Mac library to merge…";
    internal const string PreviewTitle="Review full Mac library export";
    public bool ChooseMerge {get;private set;}
    public QrMacLibraryDialog(App app,QrMacLibraryPlan plan,bool merged)
    {
        Title=app.L(PreviewTitle); Width=740; Height=720; MinWidth=430; MinHeight=360;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel {Margin=new Thickness(20)};
        var footer=new WrapPanel();
        footer.Children.Add(UI.Button(app.L(Merge),()=>{ChooseMerge=true;DialogResult=true;}));
        var save=UI.Button(app.L("Choose QR Export Folder…"),()=>DialogResult=true);
        save.IsEnabled=plan.Rows.Any(x=>x.Output is not null); footer.Children.Add(save);
        var cancel=UI.Button(app.L("Cancel"),()=>DialogResult=false);cancel.IsCancel=true;footer.Children.Add(cancel);
        DockPanel.SetDock(footer,Dock.Bottom); root.Children.Add(footer);
        var body=new StackPanel();
        foreach(string note in new[]{QrMacLibraryExport.Scope,QrMacLibraryExport.Install,QrMacLibraryExport.Privacy,
            merged?"The selected Mac library is kept first. Windows entries are added only when value and collection are new. The selected file is not modified.":NoMerge})
            body.Children.Add(UI.Text(app.L(note),13));
        body.Children.Add(UI.Button(app.L("Copy Mac install steps"),()=>{
            try {Clipboard.SetText(app.L(QrMacLibraryExport.Install));} catch(Exception error){app.ShowError(error);}
        }));
        body.Children.Add(UI.Text(string.Format(app.L("Output rows: {0}. Omitted rows: {1}."),plan.Rows.Count(x=>x.Output is not null),plan.Rows.Count(x=>x.Output is null)),18));
        body.Children.Add(UI.Text(app.L("Hidden characters are shown as [U+XXXX] in this preview."),12));
        foreach(var row in plan.Rows)
        {
            body.Children.Add(UI.Text((row.FromMac?app.L("Mac source"):app.L("Windows source"))+" · "+LibraryJson.Visible(row.Name),18));
            body.Children.Add(UI.Text(app.L("Group")+": "+LibraryJson.Visible(row.Collection),12));
            var text=new TextBox {Text=LibraryJson.Visible(row.Value),IsReadOnly=true,TextWrapping=TextWrapping.Wrap,MaxHeight=140,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
            AutomationProperties.SetName(text,app.L("QR Text"));body.Children.Add(text);
            body.Children.Add(UI.Text(app.L(row.Output is null?"Not exported":row.Reasons.Count==0?"Exact":"Changed"),14));
            if(row.Output is { } output)
                body.Children.Add(UI.Text("id: "+output.Id.ToString("D")+" · created: "+output.Created.ToString("R",CultureInfo.InvariantCulture)+
                    " · "+app.L("Garden Season")+": "+output.Season+" · "+app.L("Garden Template")+": "+output.Template,12));
            foreach(string reason in row.Reasons) body.Children.Add(UI.Text(app.L(reason),12));
        }
        root.Children.Add(UI.Scroll(body));Content=root;
        SourceInitialized+=(_,_)=>WebRouteLayout.Fit(this,Owner??this);
        DpiChanged+=(_,_)=>WebRouteLayout.Fit(this,this);
    }
}

internal sealed class QrMacInstallDialog : Window
{
    public QrMacInstallDialog(App app,string output)
    {
        Title=app.L(QrMacLibraryExport.Title);Width=650;Height=450;MinWidth=400;MinHeight=280;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel {Margin=new Thickness(20)};
        string instructions=output+Environment.NewLine+Environment.NewLine+app.L(QrMacLibraryExport.Install);
        var footer=new WrapPanel();
        footer.Children.Add(UI.Button(app.L("Copy Mac install steps"),()=>{
            try {Clipboard.SetText(instructions);} catch(Exception error){app.ShowError(error);}
        }));
        var close=UI.Button(app.L("Close"),()=>Close());close.IsCancel=true;footer.Children.Add(close);
        DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        var text=new TextBox {Text=instructions,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        AutomationProperties.SetName(text,app.L("Mac install steps"));root.Children.Add(text);Content=root;
        SourceInitialized+=(_,_)=>WebRouteLayout.Fit(this,Owner??this);
        DpiChanged+=(_,_)=>WebRouteLayout.Fit(this,this);
    }
}
