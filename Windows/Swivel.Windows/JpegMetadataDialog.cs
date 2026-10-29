using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Swivel.Core;

namespace Swivel.Windows;

internal static class JpegMetadataNotices
{
    internal const string Scope="Remove every APP1 (EXIF, XMP or other data), APP13 (Photoshop or other data) and COM comment segment. Keep all other supported segments and compressed scans byte-for-byte in their original order. The photo is not recompressed; metadata payloads being removed are not interpreted.";
    internal const string Limits="Only the supported 8-bit baseline/progressive JPEG subset, one or three components: 32 MiB, 16 MP, 16,384 pixels per side, 65,536 markers including restarts, and 512 scans. These are this tool's limits. Framing checks and native decoding of the cleaned snapshot do not prove every compressed bit is valid. Arithmetic/lossless coding, CMYK, high precision, trailing data, APP11 and non-ICC APP2 are refused.";
    internal const string Retained="ICC APP2 profiles, APP0 thumbnails, Adobe APP14 and other retained data may still contain private information. This is not complete privacy sanitization or comprehensive HDR/credential detection. Removing APP1/APP13 can change orientation, color interpretation, cropping or display in other apps. Compressed samples are not rotated or rewritten; review the saved copy in your target app.";
    internal const string Export="Save creates a new JPEG beside the original without replacing it. Source file-system timestamps, attributes and alternate streams are not copied. Only the cleaned snapshot is sent to the native decoder; retained ICC and other supported data may still be interpreted. No automatic network or clipboard access, persistent history or external application launch is used.";
    internal const string Restart="Restart markers are counted in the marker total and kept inside the compressed-scan rows. Their sequence and a nonzero restart interval are checked, but MCU spacing and full progressive/Huffman semantics are left to native decoding. ICC pieces may appear out of order, but their sequence numbers must be complete and unique before the first scan.";
    internal const string Downgrade="If this action is saved in My Wheel, Windows 0.43 and earlier may reject those settings and fall back to defaults. Keep a pre-upgrade settings backup before downgrading.";
}
internal sealed class JpegMetadataDialog : Window
{
    private sealed record Row(string Type,int Count,string Bytes,string Result);
    internal bool KeepOrientation=>keep.IsChecked==true;
    private readonly CheckBox keep;
    internal JpegMetadataDialog(App app,JpegMetadataDocument document)
    {
        var plan=document.Plan;Title=app.L("Remove JPEG EXIF & Comments…");Width=850;Height=780;MinWidth=460;MinHeight=320;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(20)};var footer=new StackPanel();DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        keep=new CheckBox{Content=app.L("Keep orientation"),IsChecked=plan.Orientation.CanKeep,IsEnabled=plan.Orientation.CanKeep,Margin=new Thickness(0,4,0,8)};
        var summary=UI.Text("");footer.Children.Add(summary);
        void Summarize()=>summary.Text=plan.RemovedCount==0?app.L("No JPEG APP1, APP13 or comment segments were found. No copy was saved."):keep.IsChecked==true?string.Format(app.L("Remove {0:N0} segment(s), {1:N0} bytes; add a {2:N0}-byte orientation-only EXIF segment; new JPEG: {3:N0} bytes."),plan.RemovedCount,plan.RemovedBytes,JpegMetadata.OrientationSegmentLength,plan.OutputBytesKeeping(true)):string.Format(app.L("Remove {0:N0} segment(s), {1:N0} bytes; new JPEG: {2:N0} bytes."),plan.RemovedCount,plan.RemovedBytes,plan.OutputBytes);
        Summarize();keep.Checked+=(_,_)=>Summarize();keep.Unchecked+=(_,_)=>Summarize();
        var buttons=new WrapPanel();footer.Children.Add(buttons);var save=UI.Button(app.L("Save JPEG Copy"),()=>DialogResult=true);save.IsEnabled=plan.RemovedCount>0;buttons.Children.Add(save);var cancel=UI.Button(app.L("Cancel"),()=>DialogResult=false);cancel.IsCancel=true;buttons.Children.Add(cancel);
        var body=new StackPanel();root.Children.Add(UI.Scroll(body));Content=root;
        body.Children.Add(UI.Text(app.L("Source (JSON path)")));var path=new TextBox{Text=JsonSerializer.Serialize(document.Path),IsReadOnly=true,TextWrapping=TextWrapping.Wrap};AutomationProperties.SetName(path,app.L("Source (JSON path)"));body.Children.Add(path);
        body.Children.Add(UI.Text(string.Format(app.L("{0} × {1} pixels · source: {2:N0} bytes"),plan.Width,plan.Height,plan.SourceBytes)));
        body.Children.Add(UI.Text(app.L(plan.Progressive?"Progressive JPEG":"Baseline JPEG")));
        body.Children.Add(UI.Text(string.Format(app.L("{0:N0} scan(s) · {1:N0} marker(s), including {2:N0} restarts"),plan.ScanCount,plan.MarkerCount,plan.RestartCount)));
        body.Children.Add(UI.Text(OrientationNotices.Status(app,plan.Orientation)));body.Children.Add(keep);body.Children.Add(UI.Text(app.L(OrientationNotices.Keep),12));
        foreach(string note in new[]{JpegMetadataNotices.Scope,JpegMetadataNotices.Limits,JpegMetadataNotices.Retained,JpegMetadataNotices.Restart,JpegMetadataNotices.Export})body.Children.Add(UI.Text(app.L(note),12));
        body.Children.Add(UI.Text(app.L("JPEG Segment Summary"),18));var list=new ListView{Height=240,ItemsSource=plan.Segments.Select(c=>new Row(app.L(JpegMetadata.MarkerName(c.Marker)),c.Count,c.Bytes.ToString("N0"),app.L(c.Removed?"Remove":"Keep"))).ToArray()};
        VirtualizingPanel.SetIsVirtualizing(list,true);VirtualizingPanel.SetVirtualizationMode(list,VirtualizationMode.Recycling);ScrollViewer.SetCanContentScroll(list,true);AutomationProperties.SetName(list,app.L("JPEG Segment Summary"));
        var grid=new GridView();foreach(var col in new[]{("Segment Type","Type",110d),("Count","Count",85d),("Bytes (including framing)","Bytes",200d),("Result","Result",110d)})grid.Columns.Add(new GridViewColumn{Header=app.L(col.Item1),DisplayMemberBinding=new Binding(col.Item2),Width=col.Item3});list.View=grid;body.Children.Add(list);
        body.Children.Add(UI.Text(app.L(JpegMetadataNotices.Downgrade),12));
        SourceInitialized+=(_,_)=>WebRouteLayout.Fit(this,Owner??this);DpiChanged+=(_,_)=>WebRouteLayout.Fit(this,this);
    }
}
internal static class OrientationNotices
{
    internal const string Keep="Keep orientation writes a new minimal EXIF containing only the orientation tag (no camera, time, GPS, thumbnail or maker data) in place of the original EXIF. Every other retained byte is unchanged and the image is not recompressed. The orientation is read only from the source EXIF, never from XMP or text.";
    internal static string Status(App app,ExifOrientationInfo info)=>info.Status switch
    {
        ExifOrientationStatus.Found=>string.Format(app.L("Source EXIF orientation: {0} ({1})."),info.Value,app.L(ExifOrientation.Describe(info.Value))),
        ExifOrientationStatus.Malformed=>app.L("The source EXIF orientation could not be read (damaged or nonstandard EXIF), so it cannot be kept and is removed with the rest."),
        ExifOrientationStatus.Ambiguous=>app.L("The source has more than one EXIF block, so no single orientation can be kept; all of them are removed."),
        _=>app.L("No EXIF orientation was found in the source.")
    };
}
