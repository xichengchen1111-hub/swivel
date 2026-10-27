using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class TextCaptureDialog:Window
{
    private Task pending=Task.CompletedTask;private CancellationTokenSource? cancellation;private bool closed;
    internal TextCaptureDialog(App app,TextCaptureDocument document)
    {
        Title=app.L("Text Capture");Width=850;Height=760;MinWidth=500;MinHeight=400;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(20)};var footer=new StackPanel();var status=UI.Text("");footer.Children.Add(status);var row=new WrapPanel();footer.Children.Add(row);DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        var body=new StackPanel();body.Children.Add(UI.Text(document.Path,12));body.Children.Add(UI.Text(app.L(TextCaptureDocument.Scope),12));body.Children.Add(UI.Text(app.L(OcrText.Limit),12));body.Children.Add(UI.Text(app.L("Review OCR text before copying. Saving a new tool or action can make Windows 0.49 and earlier reject settings; keep a pre-upgrade backup before downgrading."),12));
        var language=new ComboBox{ItemsSource=document.Languages,SelectedIndex=0,Margin=new Thickness(0,6,0,12)};AutomationProperties.SetName(language,app.L("OCR Language"));body.Children.Add(language);
        var editor=new TextBox{MaxLength=OcrText.MaxUnits,AcceptsReturn=true,AcceptsTab=true,Height=360,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,TextWrapping=TextWrapping.NoWrap};AutomationProperties.SetName(editor,app.L("Recognized Text"));body.Children.Add(editor);root.Children.Add(UI.Scroll(body));Content=root;
        var recognize=UI.Button(app.L("Recognize Text"),()=>{});row.Children.Add(recognize);row.Children.Add(UI.Button(app.L("Stop"),()=>cancellation?.Cancel()));var copy=UI.Button(app.L("Copy Reviewed Text"),()=>app.Try(()=>Clipboard.SetText(OcrText.Validate(editor.Text))));copy.IsEnabled=false;row.Children.Add(copy);
        var close=UI.Button(app.L("Close"),()=>{cancellation?.Cancel();DialogResult=false;});close.IsCancel=true;row.Children.Add(close);editor.TextChanged+=(_,_)=>copy.IsEnabled=pending.IsCompleted&&editor.Text.Length>0;
        recognize.Click+=(_,_)=>{if(pending.IsCompleted)pending=Recognize();};
        async Task Recognize()
        {
            var owned=cancellation=new CancellationTokenSource();recognize.IsEnabled=copy.IsEnabled=language.IsEnabled=editor.IsEnabled=false;
            try{string selected=(string)language.SelectedItem;string text=await Task.Run(()=>document.RecognizeAsync(selected,owned.Token),owned.Token);owned.Token.ThrowIfCancellationRequested();if(!closed){editor.Text=text;status.Text=app.L("Recognition finished. Review and edit the text before copying.");}}
            catch(OperationCanceledException){if(!closed)status.Text=app.L("Cancelled.");}
            catch(Exception error){if(!closed)status.Text=error is UserError user?app.L(user.Message):app.L("Windows OCR could not recognize this image.");}
            finally{if(ReferenceEquals(cancellation,owned))cancellation=null;owned.Dispose();if(!closed){recognize.IsEnabled=language.IsEnabled=editor.IsEnabled=true;copy.IsEnabled=editor.Text.Length>0;}}
        }
        Closed+=(_,_)=>{closed=true;cancellation?.Cancel();};SourceInitialized+=(_,_)=>WebRouteLayout.Fit(this,Owner??this);DpiChanged+=(_,_)=>WebRouteLayout.Fit(this,this);
    }
    internal async Task FinishAsync(){cancellation?.Cancel();await pending;}
}
