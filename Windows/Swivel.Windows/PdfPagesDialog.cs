using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class PdfPagesDialog:Window
{
    public PdfPageChoice[] Order{get;private set;}=Array.Empty<PdfPageChoice>();public int Turn{get;private set;}
    internal PdfPagesDialog(App app,PdfPagesDocument document)
    {
        Title=app.L("Select PDF Pages…");Width=820;Height=760;MinWidth=500;MinHeight=400;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(20)};var footer=new StackPanel();var status=UI.Text("");footer.Children.Add(status);var buttons=new WrapPanel();footer.Children.Add(buttons);DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);var body=new StackPanel();root.Children.Add(UI.Scroll(body));Content=root;
        body.Children.Add(UI.Text(app.L(PdfPageCopy.Fidelity)));body.Children.Add(UI.Text(app.L(PdfStructure.Limit),12));body.Children.Add(UI.Text(app.L("The new PDF is saved beside the first input. Page sizes are kept; rotation is added to each page's own rotation."),12));
        var list=new ListBox{Height=200};
        foreach(var row in document.Pages)list.Items.Add(row.Number+" · "+AppMemory.Visible(row.Path)+" · "+app.L("Page")+" "+row.Page+" · "+row.Width.ToString("0.#",CultureInfo.InvariantCulture)+" × "+row.Height.ToString("0.#",CultureInfo.InvariantCulture)+" pt"+(row.Rotate==0?"":" · "+row.Rotate.ToString(CultureInfo.InvariantCulture)+"°"));
        VirtualizingPanel.SetIsVirtualizing(list,true);ScrollViewer.SetCanContentScroll(list,true);AutomationProperties.SetName(list,app.L("PDF Source Pages"));body.Children.Add(list);
        body.Children.Add(UI.Text(app.L(PdfPageCopy.Selection),12));
        var selection=new TextBox{Text=document.Pages.Count==1?"1":"1-"+document.Pages.Count.ToString(CultureInfo.InvariantCulture),MaxLength=512,Margin=new Thickness(0,6,0,8)};AutomationProperties.SetName(selection,app.L("Page Selection"));body.Children.Add(selection);
        var options=new WrapPanel();body.Children.Add(options);options.Children.Add(UI.Text(app.L("Rotation")));
        var turn=new ComboBox{Width=100,ItemsSource=new[]{0,90,180,270},SelectedItem=0,Margin=new Thickness(8)};AutomationProperties.SetName(turn,app.L("Rotation"));options.Children.Add(turn);
        var save=UI.Button(app.L("Save PDF"),()=>{});buttons.Children.Add(save);var cancel=UI.Button(app.L("Cancel"),()=>DialogResult=false);cancel.IsCancel=true;buttons.Children.Add(cancel);
        selection.TextChanged+=(_,_)=>status.Text="";turn.SelectionChanged+=(_,_)=>status.Text="";
        save.Click+=(_,_)=>{try{Order=document.Plan(selection.Text);Turn=(int)turn.SelectedItem;DialogResult=true;}catch(UserError error){status.Text=app.L(error.Message);}};
        SourceInitialized+=(_,_)=>WebRouteLayout.Fit(this,Owner??this);DpiChanged+=(_,_)=>WebRouteLayout.Fit(this,this);
    }
}
