using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Swivel.Core;

namespace Swivel.Windows;

public sealed class QrLibraryFile : IDisposable
{
    private readonly NativeFiles.Input input;
    public IReadOnlyList<QrEntry> Entries { get; }
    private QrLibraryFile(NativeFiles.Input input,QrEntry[] entries){this.input=input;Entries=Array.AsReadOnly(entries);}
    public static QrLibraryFile Open(string path,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();var input=NativeFiles.OpenRead(path);
        try
        {
            if(input.Stream.Length>QrLibrary.MaxBytes)throw new UserError(QrLibrary.Invalid);
            byte[] bytes=new byte[checked((int)input.Stream.Length)];input.Stream.ReadExactly(bytes);token.ThrowIfCancellationRequested();return new(input,QrLibrary.Decode(bytes));
        }
        catch{input.Dispose();throw;}
    }
    public static string Save(IReadOnlyList<QrEntry> entries,string folder,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();byte[] bytes=QrLibrary.Encode(entries);return NativeFiles.WriteInDirectory(folder,"Swivel QR library","json",s=>s.Write(bytes),token);
    }
    public void Dispose()=>input.Dispose();
}
internal sealed class QrLibraryPreview : Window
{
    public QrLibraryPreview(App app,IReadOnlyList<QrEntry> entries,bool importing,int additions=0)
    {
        Title=app.L(importing?"Import QR Library…":"Export QR Library…");Width=670;Height=620;MinWidth=420;MinHeight=350;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var panel=new StackPanel {Margin=new Thickness(24)};
        panel.Children.Add(UI.Text(app.L(importing?"Review every entry before importing. Matching entries are skipped; conflicting names are refused. No links open and no QR files are created.":"This backup contains the full QR text in plain JSON. Review the content before saving it in your chosen folder.")));
        if(importing)panel.Children.Add(UI.Text(string.Format(app.L("{0} new QR entries will be added."),additions),18));
        foreach(var entry in entries)
        {
            panel.Children.Add(UI.Text(entry.Name,18));panel.Children.Add(UI.Text(string.Format(app.L("Error correction: {0}"),QrCode.Level(entry.Correction)),12));if(entry.Group.Length>0)panel.Children.Add(UI.Text(entry.Group,12));
            panel.Children.Add(UI.Text(app.L(QrGarden.TemplateName(entry.Template))+" · "+app.L(QrGarden.SeasonName(entry.Season)),12));
            var text=new TextBox {Text=entry.Text,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,MaxHeight=120,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Margin=new Thickness(0,0,0,12)};System.Windows.Automation.AutomationProperties.SetName(text,app.L("QR Text"));panel.Children.Add(text);
        }
        var actions=new WrapPanel();var apply=UI.Button(app.L(importing?"Import Entries":"Choose Backup Folder…"),()=>DialogResult=true);apply.IsEnabled=!importing || additions>0;actions.Children.Add(apply);
        var cancel=UI.Button(app.L("Cancel"),()=>DialogResult=false);cancel.IsCancel=true;actions.Children.Add(cancel);panel.Children.Add(actions);Content=UI.Scroll(panel);
    }
}
