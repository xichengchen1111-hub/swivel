using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;
using Swivel.Windows;

internal static class CommonSimilarityNativeChecks
{
    internal static void Run(string directory, Action<bool,string> check, Action<Action,string> reject)
    {
        string folder=Path.Combine(directory,"common-similarity"),left=Path.Combine(folder,"left"),right=Path.Combine(folder,"right");Directory.CreateDirectory(left);Directory.CreateDirectory(right);
        string privateText="private-value-one\nprivate-value-two\nprivate-value-three\nprivate-value-four\nprivate-value-five";
        foreach(string target in new[]{left,right})
        {
            File.WriteAllText(Path.Combine(target,"same.txt"),privateText,new UTF8Encoding(false,true));
            var bytes=new byte[9*8*4];for(int y=0;y<8;y++)for(int x=0;x<9;x++){int at=(y*9+x)*4;bytes[at]=bytes[at+1]=bytes[at+2]=(byte)(x%2==0?0:255);bytes[at+3]=255;}
            var bitmap=BitmapSource.Create(9,8,96,96,PixelFormats.Bgra32,null,bytes,36);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var output=File.Create(Path.Combine(target,"same.png"));encoder.Save(output);
        }
        var before=Directory.GetFiles(folder,"*",SearchOption.AllDirectories).ToDictionary(p=>p,File.ReadAllBytes);
        var choices=new[]{CommonFolderChoice.FromReported(CommonFolderKind.Desktop,left),CommonFolderChoice.FromReported(CommonFolderKind.Downloads,right),CommonFolderChoice.FromReported(CommonFolderKind.Documents,folder)};
        var reported=CommonFolders.Kinds.Select(k=>choices.FirstOrDefault(c=>c.Kind==k)??CommonFolderChoice.Unavailable(k,"Windows did not report this location.")).ToArray();
        var app=Application.Current as App??new App(validationHost:true);string oldLanguage=app.Prefs.Language;
        try
        {
            foreach(var kind in Enum.GetValues<CommonSimilarityKind>())
            {
                reject(()=>CommonSimilarityReader.Read(kind,choices,new CancellationToken(true)),"Common similarity pre-cancellation occurs before native roots");
                var result=CommonSimilarityReader.Read(kind,choices,default);int loaded=result.Text?.Loaded??result.Images!.Loaded;int matches=result.Text?.Pairs.Count??result.Images!.Pairs.Count;
                check(loaded==2&&matches==1&&result.Roots.Count==3,"Native cross-root comparison uses one pair and deduplicates the overlapping parent root");
                var issues=result.Text?.Issues??result.Images!.Issues;check(issues.Count(i=>i.Reason=="Same file identity (hard link)")==2,"Native full volume/file IDs deduplicate overlapping relative paths");
                foreach(string language in new[]{"en","zh-Hans","zh-Hant","ja"})
                {
                    app.Prefs.SetLanguage(language);var chooser=new CommonSimilarityChooser(app,reported,kind);var children=Children(chooser).ToArray();var boxes=children.OfType<CheckBox>().ToArray();
                    var review=children.OfType<Button>().Single(b=>Equals(b.Content,app.L("Review Selected Comparison…")));
                    check(boxes.Length==6&&boxes.Count(b=>b.IsEnabled)==3&&boxes.All(b=>b.IsChecked==false)&&chooser.Selected.Length==0&&!review.IsEnabled,"Native common similarity starts with no selected locations and unavailable roots remain disabled");
                    boxes[0].IsChecked=true;boxes[1].IsChecked=true;check(chooser.Selected.Select(c=>c.Kind).SequenceEqual(choices.Take(2).Select(c=>c.Kind))&&review.IsEnabled,"Native explicit location choices preserve original order");
                    var consent=new CommonSimilarityConfirmation(app,chooser.Selected,kind);check(Children(consent).OfType<TextBox>().Select(t=>t.Text).SequenceEqual(new[]{left,right}),"Native final content confirmation names every full selected root");
                    check(Children(consent).OfType<TextBlock>().Any(t=>t.Text==app.L(kind==CommonSimilarityKind.Text?CommonSimilarityNotices.TextLimits:CommonSimilarityNotices.ImageLimits)),"Native confirmation declares one shared mode-specific content budget");
                    var dialog=new CommonSimilarityDialog(app,result);string report=dialog.CurrentReport;check(report.Contains(left)&&report.Contains(right)&&report.Contains(folder)&&report.Contains(app.L(CommonSimilarityNotices.Metadata))&&!report.Contains("private-value-one"),"Native report has all roots and shared limits but never text contents");
                    string pairName=app.L(kind==CommonSimilarityKind.Text?"Matching Text Pairs":"Matching Image Pairs");var pairList=Children(dialog).OfType<ListView>().Single(l=>AutomationProperties.GetName(l)==pairName);var copy=Children(dialog).OfType<Button>().Single(b=>Equals(b.Content,app.L("Copy Selected Pair")));
                    check(!copy.IsEnabled&&Children(dialog).OfType<ListView>().All(l=>VirtualizingPanel.GetIsVirtualizing(l)&&VirtualizingPanel.GetVirtualizationMode(l)==VirtualizationMode.Recycling),"Native lists virtualized and pair copy is explicit");pairList.SelectedIndex=0;
                    string suffix=kind==CommonSimilarityKind.Text?"same.txt":"same.png";check(copy.IsEnabled&&dialog.SelectedReport.Contains(Path.Combine(left,suffix))&&dialog.SelectedReport.Contains(Path.Combine(right,suffix))&&dialog.SelectedReport.Contains(app.L(CommonSimilarityNotices.Roots)),"Selected pair report resolves full original paths and retains all root limits");
                    var move=Children(dialog).OfType<Button>().Single(b=>Equals(b.Content,app.L("Move Selected to Recycle Bin…")));var files=Children(dialog).OfType<ListView>().Single(l=>AutomationProperties.GetName(l)==app.L("Files in Selected Pair"));
                    check(!move.IsEnabled&&files.Items.Count==2,"Cross-root pair lists both files; nothing moves until one is chosen");files.SelectedIndex=0;check(move.IsEnabled,"Choosing one file of a cross-root pair enables the move");
                    pairList.SelectedIndex=-1;check(!copy.IsEnabled&&!move.IsEnabled,"Cleared native selection disables stale copy and move");dialog.Close();consent.Close();chooser.Close();
                }
            }
            using(var locked=File.Open(Path.Combine(right,"same.txt"),FileMode.Open,FileAccess.ReadWrite,FileShare.None))
            {
                var unavailable=CommonSimilarityReader.Read(CommonSimilarityKind.Text,choices.Take(2).ToArray(),default);check(unavailable.Text!.Pairs.Count==0&&unavailable.Text.Issues.Any(i=>unavailable.Sources[i.RelativePath].Kind==CommonFolderKind.Downloads),"Unavailable cross-root file stays visible without a partial match");
                var dialog=new CommonSimilarityDialog(app,unavailable);check(dialog.CurrentReport.Contains(app.L("No similar pairs in the comparisons completed. Skipped or unexamined files may still be similar."))&&dialog.CurrentReport.Contains(right),"Zero-pair native report keeps root and skip information");dialog.Close();
            }
            var baseResult=CommonSimilarityReader.Read(CommonSimilarityKind.Text,choices.Take(1).ToArray(),default);var sources=new Dictionary<string,CommonDuplicateFileRef>(StringComparer.Ordinal);var many=new List<DuplicateIssue>();
            for(int i=0;i<20000;i++){string relative=new string('x',235)+i.ToString("D5")+".txt",key="0\\"+relative;sources.Add(key,new(CommonFolderKind.Desktop,relative,1));many.Add(new(key,"Unavailable or unsupported"));}
            var driveRoot=baseResult.Roots[0] with{Choice=CommonFolderChoice.FromReported(CommonFolderKind.Desktop,@"D:\")};var driveResult=baseResult with{Roots=Array.AsReadOnly(new[]{driveRoot})};var driveDialog=new CommonSimilarityDialog(app,driveResult);
            check(driveRoot.Choice.Path is null&&driveRoot.Choice.Issue is not null&&driveDialog.CurrentReport.Contains(@"D:\"),"Drive root is refused by the path policy and still shown as reported without a crash");driveDialog.Close();
            var huge=baseResult with{Sources=new ReadOnlyDictionary<string,CommonDuplicateFileRef>(sources),Text=baseResult.Text! with{Issues=many.AsReadOnly()}};var oversized=new CommonSimilarityDialog(app,huge);reject(()=>_ = oversized.CurrentReport,"Native complete report over 4Mi UTF16 is refused, never silently truncated");oversized.Close();
            reject(()=>CommonSimilarityReader.Read((CommonSimilarityKind)9,choices,default),"Unknown native similarity mode refused");
        }
        finally{app.Prefs.SetLanguage(oldLanguage);}
        check(before.All(p=>File.ReadAllBytes(p.Key).SequenceEqual(p.Value))&&!Directory.GetFiles(folder,".swivel-*.tmp",SearchOption.AllDirectories).Any(),"Common similarity preserves all original bytes and creates no output files");
        Directory.Move(folder,folder+" released");check(Directory.Exists(folder+" released"),"All native roots and ancestors are released before completion");

        // Moving one file of a cross-root similar pair to the Recycle Bin, keeping the other.
        string movedLeft=Path.Combine(folder+" released","left"),movedRight=Path.Combine(folder+" released","right");
        var cross=CommonSimilarityReader.Read(CommonSimilarityKind.Text,new[]{CommonFolderChoice.FromReported(CommonFolderKind.Desktop,movedLeft),CommonFolderChoice.FromReported(CommonFolderKind.Downloads,movedRight)},default);
        var crossPair=cross.Text!.Pairs.Single();var crossStamps=cross.Text.Stamps;
        string Real(string key){var source=cross.Sources[key];return Path.Combine(source.Kind==CommonFolderKind.Desktop?movedLeft:movedRight,source.RelativePath);}
        string first=Real(crossPair.First),second=Real(crossPair.Second);
        check(crossStamps.ContainsKey(crossPair.First)&&crossStamps.ContainsKey(crossPair.Second)&&first!=second,"Cross-root comparison records a scan stamp for each opaque file key");
        if(!RecycleBin.Available(second)){Console.WriteLine("SKIP: no Recycle Bin on the test drive.");return;}
        string stored=NativeFiles.RecycleSimilar(second,crossStamps[crossPair.Second],first,crossStamps[crossPair.First]);
        check(!File.Exists(second)&&File.ReadAllText(first)==privateText&&stored.Contains("$Recycle.Bin",StringComparison.OrdinalIgnoreCase),"Cross-root cleanup moves one file of the pair and keeps the other in another root: "+stored);
        NativeFiles.RestoreRecycled(second,stored,false);check(File.ReadAllText(second)==privateText,"Undo restores the cross-root file");
    }
    private static IEnumerable<DependencyObject> Children(DependencyObject root)
    {yield return root;foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())foreach(var item in Children(child))yield return item;}
}
