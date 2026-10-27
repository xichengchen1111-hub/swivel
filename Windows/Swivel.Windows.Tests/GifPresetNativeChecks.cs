using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;
using Swivel.Windows;

internal static class GifPresetNativeChecks
{
    private static void Png(string path,int width,byte[] pixels)
    {var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width,1,96,96,PixelFormats.Bgra32,null,pixels,width*4)));using var output=File.Create(path);encoder.Save(output);}
    internal static void Run(string directory,Action<bool,string> check,Action<Action,string> reject)
    {
        string folder=Path.Combine(directory,"gif-presets");Directory.CreateDirectory(folder);string path=Path.Combine(folder,"配方 🦜.png");byte[] pixels={0,0,0,255,0,0,0,0,255,0,0,255};Png(path,3,pixels);byte[] original=File.ReadAllBytes(path);var recipe=new ImageRecipe("gif",Quality:17);var preset=new ImagePreset(Guid.NewGuid().ToString("N"),"GIF",recipe);var app=Application.Current as App??new App();
        var dialog=new PresetDialog(app,preset);var root=(DockPanel)dialog.Content;var body=(StackPanel)root.Children.OfType<ScrollViewer>().Single().Content;
        var footer=root.Children.OfType<StackPanel>().Single(p=>DockPanel.GetDock(p)==Dock.Bottom);check(footer.Children.OfType<WrapPanel>().Count()==1&&footer.Children.OfType<TextBlock>().Count()==1,"GIFpreset native fixedSaveCancel andvalidationerror footer");
        body.Children.OfType<TextBox>().First().Text="";footer.Children.OfType<WrapPanel>().Single().Children.OfType<Button>().First().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));check(!string.IsNullOrEmpty(footer.Children.OfType<TextBlock>().Single().Text),"GIFpreset validationfailure visiblebesidefixedbuttons evenwithbodyscrolled");var format=body.Children.OfType<ComboBox>().Single();check(format.Items.Count==5&&format.SelectedIndex==4,"GIFpreset editing restoresfifthformat");
        var notes=body.Children.OfType<StackPanel>().Single(p=>p.Children.OfType<TextBlock>().Any(t=>t.Text==app.L(GifExport.Notice)));check(notes.Visibility==Visibility.Visible&&notes.Children.OfType<TextBlock>().Any(t=>t.Text==app.L(GifExport.Notice))&&notes.Children.OfType<TextBlock>().Any(t=>t.Text==app.L(ImageRecipe.GifDowngrade)),"GIFpreset limits/downgradevisibleduringedit");
        var quality=body.Children.OfType<TextBox>().Last();check(!quality.IsEnabled&&quality.Text=="17","GIFqualitystoredvaluevisiblebutnoteditable");format.SelectedIndex=1;check(quality.IsEnabled&&notes.Visibility==Visibility.Collapsed,"JPEGselection restoresquality/hidesGIFonlynotice");format.SelectedIndex=4;check(!quality.IsEnabled&&notes.Visibility==Visibility.Visible,"GIFselection restoredafterJPEG");dialog.Close();
        foreach(bool importing in new[]{false,true})
        {
            var import=new PresetImport(new[]{new PresetImportRow("GIF",PresetLibrary.Describe(recipe),preset)},false);var preview=new PresetLibraryPreview(app,import,importing,1);var panel=(StackPanel)((DockPanel)preview.Content).Children.OfType<ScrollViewer>().Single().Content;
            check(panel.Children.OfType<TextBlock>().Any(t=>t.Text==app.L(ImageRecipe.GifNotice))&&panel.Children.OfType<TextBlock>().Any(t=>t.Text==app.L(ImageRecipe.GifDowngrade)),"GIFbackup/importpreview showpostprocessanddowngradenotice");preview.Close();
        }
        var review=new ConversionDialog(app,new("preset."+preset.Id,preset.Name,Feature.ImagePresets),new[]{path},"gif");var reviewBody=(StackPanel)((DockPanel)review.Content).Children.OfType<ScrollViewer>().Single().Content;check(reviewBody.Children.OfType<TextBlock>().Any(t=>t.Text==app.L(ImageRecipe.GifNotice)),"GIFpreset runconfirmation showspostprocessinglimits");review.Close();
        using(var document=ImageTools.Open(path,default))
        {
            reject(()=>Directory.Move(folder,folder+" moved"),"GIFpreset ancestorsheld");string a=ImageTools.Export(document,"preset."+preset.Id,1920,default,recipe with{Quality=1}),b=ImageTools.Export(document,"preset."+preset.Id,1920,default,recipe with{Quality=100});
            check(Path.GetExtension(a)==".gif"&&File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b)),"GIFpreset usesrealGIFoutput andignoresJPEGquality");using var input=File.OpenRead(a);check(GifImage.Read(input,default).Bgra.SequenceEqual(pixels),"GIFpreset preservesallnormalizedpixels");
            int before=Directory.GetFiles(folder).Length;reject(()=>ImageTools.Export(document,"preset."+preset.Id,1920,new CancellationToken(true),recipe),"GIFpresetprecancel");check(Directory.GetFiles(folder).Length==before&&!Directory.GetFiles(folder,".swivel-*.tmp").Any(),"GIFpresetcancelnopublication");
        }
        string more=Path.Combine(folder,"many.png"),partial=Path.Combine(folder,"partial.png");var colors=new byte[257*4];for(int i=0;i<257;i++){colors[i*4]=(byte)i;colors[i*4+1]=(byte)(i>>8);colors[i*4+3]=255;}Png(more,257,colors);Png(partial,1,new byte[]{0,0,255,128});
        foreach(string source in new[]{more,partial}){int count=Directory.GetFiles(folder).Length;reject(()=>ImageTools.Run(source,"preset."+preset.Id,1920,default,recipe),"GIFpresetexcesscolors/partialalpha refusedwithoutfallback");check(Directory.GetFiles(folder).Length==count&&!Directory.GetFiles(folder,".swivel-*.tmp").Any(),"GIFpresetfailure noGIF/PNGfallback/stage");}
        string reduced=ImageTools.Run(more,"preset."+preset.Id,1920,default,recipe with{LongEdge=16});using(var input=File.OpenRead(reduced))check(GifImage.Read(input,default).Width==16,"GIFpreset checksfinalresizedpixels:>256sourcecolors canbecomevalid16pixeloutput");
        var batch=ImageBatch.Run(new[]{path,partial,path},source=>ImageTools.Run(source,"preset."+preset.Id,1920,default,recipe),default);check(batch.Select(e=>e.State).SequenceEqual(new[]{BatchState.Saved,BatchState.Failed,BatchState.NotStarted})&&File.Exists(batch[0].Output!),"GIFpresetbatch stopspreservescompleted");
        check(File.ReadAllBytes(path).SequenceEqual(original),"GIFpresetdoesnotchangeoriginal");Directory.Move(folder,folder+" released");check(Directory.Exists(folder+" released"),"GIFpresetallleasesreleased");
    }
}
