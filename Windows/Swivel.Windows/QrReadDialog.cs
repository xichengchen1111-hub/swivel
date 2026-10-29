using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class QrReadDialog:Window
{
    internal const string CopyNotice="Decoded text is untrusted data. Controls and text-direction characters are shown as [U+XXXX]; literal text can also contain that notation. Copy Decoded Text copies the original characters, including any hidden ones. Nothing is opened, run, saved, added to a library or sent automatically.";
    internal const string NulCopyNotice="This text contains U+0000. Windows Unicode text clipboard treats that character as the end of text, so Copy Decoded Text is unavailable to avoid truncation. The complete decoded text is shown above with hidden characters marked as [U+XXXX]; literal text can also contain that notation. Nothing is opened, run, saved, added to a library or sent automatically.";
    internal const string RecoveryNotice="Data recovery produces a candidate, not verified text. The counts show changed encoded bytes, including error-correction bytes, and affected blocks; they are not proof of actual damage. Beyond the correction capacity, another valid but incorrect message may be accepted.";
    internal QrReadDialog(App app,QrReadDocument document)
        :this(app,document.Path,document.Result,document.Recovery,Clipboard.SetText) { }

    // A completed, immutable result is displayed here; the document owner keeps
    // its source lease. The copy callback also permits UI-only validation without
    // reading an image or changing the real clipboard.
    internal QrReadDialog(App app,string path,QrReadResult result,QrDataRecoveryResult? recovery,Action<string> copyText)
    {
        Title=app.L(recovery is null?"Read QR Image":"Recover Flat QR Text");Width=800;Height=760;MinWidth=480;MinHeight=360;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(20)};var footer=new WrapPanel();DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        string text=result.Text;
        bool canCopy=!text.Contains('\0');
        var copy=UI.Button(app.L("Copy Decoded Text"),()=>{if(canCopy)app.Try(()=>copyText(text));});copy.IsEnabled=canCopy;footer.Children.Add(copy);
        var close=UI.Button(app.L("Close"),()=>DialogResult=false);close.IsCancel=true;footer.Children.Add(close);
        var body=new StackPanel();root.Children.Add(UI.Scroll(body));Content=root;
        body.Children.Add(new TextBox{Text=DoctorFacts.VisibleBounded(path,512),IsReadOnly=true,TextWrapping=TextWrapping.Wrap,MaxHeight=70,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
        if(result.Photo)
        {
            // Photo results are estimates: module size and angle come from the fitted geometry.
            body.Children.Add(UI.Text(string.Format(app.L("Read from a photo or screenshot: QR version {0} · level {1} · about {2} pixels/module · {3} UTF-16 units"),result.Version,QrCode.Level(result.Correction),result.PixelScale,text.Length),14));
            body.Children.Add(UI.Text(string.Format(app.L("Code orientation: about {0}° clockwise from upright."),result.ClockwiseOrientationDegrees),12));
            body.Children.Add(UI.Text(string.Format(app.L("Error correction changed {0} encoded bytes in {1} blocks. Zero does not prove the photo was perfect, and beyond the correction capacity another valid message could be accepted."),result.CorrectedCodewords,result.CorrectedBlocks),12));
        }
        else
        {
            body.Children.Add(UI.Text(string.Format(app.L("QR version {0} · level {1} · {2} pixels/module · {3} UTF-16 units"),result.Version,QrCode.Level(result.Correction),result.PixelScale,text.Length),14));
            body.Children.Add(UI.Text(string.Format(app.L("Image orientation: {0}° clockwise from upright."),result.ClockwiseOrientationDegrees),12));
        }
        if(recovery is not null)
        {
            body.Children.Add(UI.Text(app.L("Data Recovery Mode"),16));
            body.Children.Add(UI.Text(string.Format(app.L("Changed encoded bytes: {0} · affected error-correction blocks: {1}"),recovery.CorrectedCodewords,recovery.CorrectedBlocks),13));
            body.Children.Add(UI.Text(app.L(RecoveryNotice),12));
        }
        var preview=new TextBox{Text=QrFlatRead.VisibleText(text),IsReadOnly=true,TextWrapping=TextWrapping.Wrap,Height=300,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        AutomationProperties.SetName(preview,app.L(recovery is null?"Decoded QR Text":"Recovered QR Text Candidate"));body.Children.Add(preview);
        body.Children.Add(UI.Text(app.L(canCopy?CopyNotice:NulCopyNotice),12));
        if(recovery is null)body.Children.Add(UI.Text(app.L(QrPhotoRead.Scope),12));
        else body.Children.Add(new Expander{Header=app.L("Recovery Scope and Limitations"),Content=UI.Text(app.L(QrFlatRead.RecoveryScope),12),IsExpanded=false,Margin=new Thickness(0,4,0,12)});
        SourceInitialized+=(_,_)=>WebRouteLayout.Fit(this,Owner??this);DpiChanged+=(_,_)=>WebRouteLayout.Fit(this,this);
    }
}
