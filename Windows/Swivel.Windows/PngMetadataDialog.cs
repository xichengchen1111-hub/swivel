using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Swivel.Core;

namespace Swivel.Windows;

internal static class PngMetadataNotices
{
    internal const string Scope="Remove all tEXt, zTXt, iTXt, eXIf and tIME chunks from this PNG. Compressed image data, palette, transparency and all other retained chunks stay byte-for-byte in their original order; the image is not recompressed.";
    internal const string Limits="Static SDR PNG only: source up to 32 MiB, 16 MP, 16,384 pixels per side and 4,096 chunks. CRC and container checks precede native validation of the cleaned snapshot. Animation, high precision, HDR signaling, recognized signatures/content credentials and unknown critical chunks are refused. Removed text and EXIF are not decoded or inflated.";
    internal const string Retained="Other chunks, including color profiles and unknown ancillary chunks, are retained and may contain private information. Image pixels, the filename and file-system information can also reveal details. This is not complete privacy sanitization; selective tags, TIFF and broader metadata handling remain separate work. JPEG has a separate limited tool.";
    internal const string Orientation="Removing EXIF also removes any stored orientation and may change how other apps rotate or mirror the image. Pixel samples are not rotated or rewritten. Review the saved copy in your target app.";
    internal const string Export="Save creates a new PNG beside the original without replacing it. Embedded retained chunk bytes are preserved; source file-system timestamps, attributes and alternate streams are not copied. No network, clipboard access or persistent history is used.";
    internal const string Downgrade="If this action is saved in My Wheel, Windows 0.42 and earlier may reject those settings and fall back to defaults. Keep a pre-upgrade settings backup before downgrading.";
}
internal sealed class PngMetadataDialog : Window
{
    private sealed record Row(string Type,int Count,string Bytes,string Result);
    internal bool KeepOrientation=>keep.IsChecked==true;
    private readonly CheckBox keep;
    internal PngMetadataDialog(App app,PngMetadataDocument document)
    {
        var plan=document.Plan;Title=app.L("Remove PNG Text & EXIF…");Width=850;Height=780;MinWidth=460;MinHeight=320;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(20)};var footer=new StackPanel();DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        keep=new CheckBox{Content=app.L("Keep orientation"),IsChecked=document.CanKeepOrientation,IsEnabled=document.CanKeepOrientation,Margin=new Thickness(0,4,0,8)};
        var summary=UI.Text("");footer.Children.Add(summary);
        void Summarize()=>summary.Text=plan.RemovedCount==0?app.L("No matching PNG text, EXIF or time chunks were found. No copy was saved."):keep.IsChecked==true?string.Format(app.L("Remove {0:N0} chunk(s), {1:N0} bytes; add a {2:N0}-byte orientation-only eXIf chunk; new PNG: {3:N0} bytes."),plan.RemovedCount,plan.RemovedBytes,PngMetadata.OrientationChunkLength,plan.OutputBytesKeeping(true)):string.Format(app.L("Remove {0:N0} chunk(s), {1:N0} bytes; new PNG: {2:N0} bytes."),plan.RemovedCount,plan.RemovedBytes,plan.OutputBytes);
        Summarize();keep.Checked+=(_,_)=>Summarize();keep.Unchecked+=(_,_)=>Summarize();
        var buttons=new WrapPanel();footer.Children.Add(buttons);var save=UI.Button(app.L("Save PNG Copy"),()=>DialogResult=true);save.IsEnabled=plan.RemovedCount>0;buttons.Children.Add(save);var cancel=UI.Button(app.L("Cancel"),()=>DialogResult=false);cancel.IsCancel=true;buttons.Children.Add(cancel);
        var body=new StackPanel();root.Children.Add(UI.Scroll(body));Content=root;
        body.Children.Add(UI.Text(app.L("Source (JSON path)")));var path=new TextBox{Text=JsonSerializer.Serialize(document.Path),IsReadOnly=true,TextWrapping=TextWrapping.Wrap};AutomationProperties.SetName(path,app.L("Source (JSON path)"));body.Children.Add(path);
        body.Children.Add(UI.Text(string.Format(app.L("{0} × {1} pixels · source: {2:N0} bytes"),plan.Width,plan.Height,plan.SourceBytes)));
        body.Children.Add(UI.Text(OrientationNotices.Status(app,plan.Orientation,document.CanKeepOrientation)));body.Children.Add(keep);body.Children.Add(UI.Text(app.L(OrientationNotices.Keep),12));
        foreach(string note in new[]{PngMetadataNotices.Scope,PngMetadataNotices.Limits,PngMetadataNotices.Retained})body.Children.Add(UI.Text(app.L(note),12));
        // The orientation-loss warning applies only while orientation is not being kept.
        var loss=UI.Text(app.L(PngMetadataNotices.Orientation),12);body.Children.Add(loss);void Loss()=>loss.Visibility=keep.IsChecked==true?Visibility.Collapsed:Visibility.Visible;Loss();keep.Checked+=(_,_)=>Loss();keep.Unchecked+=(_,_)=>Loss();
        body.Children.Add(UI.Text(app.L(PngMetadataNotices.Export),12));
        body.Children.Add(UI.Text(app.L("PNG Chunk Summary"),18));var list=new ListView{Height=240,ItemsSource=plan.Chunks.Select(c=>new Row(c.Type,c.Count,c.Bytes.ToString("N0"),app.L(c.Removed?"Remove":"Keep"))).ToArray()};
        VirtualizingPanel.SetIsVirtualizing(list,true);VirtualizingPanel.SetVirtualizationMode(list,VirtualizationMode.Recycling);ScrollViewer.SetCanContentScroll(list,true);AutomationProperties.SetName(list,app.L("PNG Chunk Summary"));
        var grid=new GridView();foreach(var col in new[]{("Chunk Type","Type",110d),("Count","Count",85d),("Bytes (including framing)","Bytes",200d),("Result","Result",110d)})grid.Columns.Add(new GridViewColumn{Header=app.L(col.Item1),DisplayMemberBinding=new Binding(col.Item2),Width=col.Item3});list.View=grid;body.Children.Add(list);
        body.Children.Add(UI.Text(app.L(PngMetadataNotices.Downgrade),12));
        SourceInitialized+=(_,_)=>WebRouteLayout.Fit(this,Owner??this);DpiChanged+=(_,_)=>WebRouteLayout.Fit(this,this);
    }
}
