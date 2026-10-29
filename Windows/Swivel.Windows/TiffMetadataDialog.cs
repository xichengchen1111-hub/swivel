using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Swivel.Core;

namespace Swivel.Windows;

internal static class TiffMetadataNotices
{
    internal const string Scope="Remove descriptive and private TIFF tags from every page: document and page names, description, camera make and model, software, date, artist, host computer, copyright, page number and position, rating, XMP, IPTC, Photoshop data, EXIF, GPS and interoperability pointers, and every other private tag except the ICC profile. The file is rebuilt with a new header and new tag tables; strip and tile bytes are copied unchanged and are not recompressed.";
    internal const string Limits="Classic TIFF only (BigTIFF is refused), either byte order: 32 MiB, 64 pages, 16,384 pixels per side and 16 MP in all pages together. Supported pages are 1–16-bit unsigned gray, palette or RGB, uncompressed, LZW, Deflate or PackBits, in strips or tiles. Tags must be sorted and every structural tag known; overlapping or repeated pages, out-of-range data, sub-images, JPEG-in-TIFF, YCbCr and CMYK are refused. These checks and native decoding of every rebuilt page do not prove every compressed bit is valid.";
    internal const string Retained="Every page is kept, including reduced-resolution pages, which may themselves show private content. The ICC profile, resolution, color and other rendering tags are retained and may still contain private information. Image pixels, the filename and file-system information can also reveal details. This is not complete privacy sanitization.";
    internal const string Orientation="Removing the Orientation tag may change how other apps rotate or mirror the image. Pixel samples are not rotated or rewritten. Review the saved copy in your target app.";
    internal const string Export="Save creates a new TIFF beside the original without replacing it. Source file-system timestamps, attributes and alternate streams are not copied. Only the rebuilt snapshot is sent to the built-in TIFF decoder. No network, clipboard access or persistent history is used.";
    internal const string Downgrade="If this action is saved in My Wheel, earlier Windows versions may reject those settings and fall back to defaults. Keep a pre-upgrade settings backup before downgrading.";
    internal const string KeepTiff="Keep orientation keeps each page's own baseline Orientation tag and nothing else that is removed. The orientation is read only from that tag, never from EXIF or XMP.";
    internal static string Status(App app,ExifOrientationInfo info,bool keepable)=>info.Status switch
    {
        ExifOrientationStatus.Found=>string.Format(app.L("Source TIFF orientation: {0} ({1})."),info.Value,app.L(ExifOrientation.Describe(info.Value)))+" "+app.L(info.Value==1?"Orientation 1 shows the image as stored, so there is nothing to keep.":keepable?"Keep orientation is available.":"The copy with orientation kept could not be validated, so only the plain copy is offered."),
        ExifOrientationStatus.Malformed=>app.L("A page's TIFF orientation tag could not be read, so orientation cannot be kept and is removed."),
        ExifOrientationStatus.Ambiguous=>app.L("Pages have different TIFF orientations, so orientation cannot be kept and is removed from every page."),
        _=>app.L("No TIFF orientation tag was found in the source.")
    };
}
internal sealed class TiffMetadataDialog : Window
{
    private sealed record Row(string Tag,string Name,int Count,string Bytes,string Result);
    internal bool KeepOrientation=>keep.IsChecked==true;
    private readonly CheckBox keep;
    internal TiffMetadataDialog(App app,TiffMetadataDocument document)
    {
        var plan=document.Plan;Title=app.L("Remove TIFF Metadata…");Width=850;Height=780;MinWidth=460;MinHeight=320;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(20)};var footer=new StackPanel();DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        keep=new CheckBox{Content=app.L("Keep orientation"),IsChecked=document.CanKeepOrientation,IsEnabled=document.CanKeepOrientation,Margin=new Thickness(0,4,0,8)};
        var summary=UI.Text("");footer.Children.Add(summary);
        void Summarize()=>summary.Text=plan.RemovedCount==0?app.L("No removable TIFF metadata tags were found. No copy was saved."):string.Format(app.L("Remove {0:N0} tag(s) from {1:N0} page(s); new TIFF: {2:N0} bytes."),plan.RemovedCount-(keep.IsChecked==true?plan.Tags.Where(t=>t.Tag==274).Sum(t=>t.Count):0),plan.PageCount,plan.OutputBytesKeeping(keep.IsChecked==true));
        Summarize();keep.Checked+=(_,_)=>Summarize();keep.Unchecked+=(_,_)=>Summarize();
        var buttons=new WrapPanel();footer.Children.Add(buttons);var save=UI.Button(app.L("Save TIFF Copy"),()=>DialogResult=true);save.IsEnabled=plan.RemovedCount>0;buttons.Children.Add(save);var cancel=UI.Button(app.L("Cancel"),()=>DialogResult=false);cancel.IsCancel=true;buttons.Children.Add(cancel);
        var body=new StackPanel();root.Children.Add(UI.Scroll(body));Content=root;
        body.Children.Add(UI.Text(app.L("Source (JSON path)")));var path=new TextBox{Text=JsonSerializer.Serialize(document.Path),IsReadOnly=true,TextWrapping=TextWrapping.Wrap};AutomationProperties.SetName(path,app.L("Source (JSON path)"));body.Children.Add(path);
        body.Children.Add(UI.Text(string.Format(app.L("{0} × {1} pixels · source: {2:N0} bytes"),plan.Width,plan.Height,plan.SourceBytes)));
        body.Children.Add(UI.Text(string.Format(app.L("{0:N0} page(s) · {1} byte order · {2:N0} bytes of strip or tile data copied unchanged"),plan.PageCount,plan.LittleEndian?"II":"MM",plan.ImageDataBytes)));
        body.Children.Add(UI.Text(TiffMetadataNotices.Status(app,plan.Orientation,document.CanKeepOrientation)));body.Children.Add(keep);body.Children.Add(UI.Text(app.L(TiffMetadataNotices.KeepTiff),12));
        foreach(string note in new[]{TiffMetadataNotices.Scope,TiffMetadataNotices.Limits,TiffMetadataNotices.Retained})body.Children.Add(UI.Text(app.L(note),12));
        var loss=UI.Text(app.L(TiffMetadataNotices.Orientation),12);body.Children.Add(loss);void Loss()=>loss.Visibility=keep.IsChecked==true?Visibility.Collapsed:Visibility.Visible;Loss();keep.Checked+=(_,_)=>Loss();keep.Unchecked+=(_,_)=>Loss();
        body.Children.Add(UI.Text(app.L(TiffMetadataNotices.Export),12));
        body.Children.Add(UI.Text(app.L("TIFF Tag Summary"),18));var list=new ListView{Height=240,ItemsSource=plan.Tags.Select(t=>new Row(t.Tag.ToString(),t.Name,t.Count,t.Bytes.ToString("N0"),app.L(t.Removed?"Remove":"Keep"))).ToArray()};
        VirtualizingPanel.SetIsVirtualizing(list,true);VirtualizingPanel.SetVirtualizationMode(list,VirtualizationMode.Recycling);ScrollViewer.SetCanContentScroll(list,true);AutomationProperties.SetName(list,app.L("TIFF Tag Summary"));
        var grid=new GridView();foreach(var col in new[]{("Tag","Tag",70d),("Name","Name",190d),("Pages","Count",70d),("Value bytes","Bytes",130d),("Result","Result",110d)})grid.Columns.Add(new GridViewColumn{Header=app.L(col.Item1),DisplayMemberBinding=new Binding(col.Item2),Width=col.Item3});list.View=grid;body.Children.Add(list);
        body.Children.Add(UI.Text(app.L(TiffMetadataNotices.Downgrade),12));
        SourceInitialized+=(_,_)=>WebRouteLayout.Fit(this,Owner??this);DpiChanged+=(_,_)=>WebRouteLayout.Fit(this,this);
    }
}
