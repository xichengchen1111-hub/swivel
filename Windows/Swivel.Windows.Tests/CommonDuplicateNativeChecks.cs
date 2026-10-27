using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Swivel.Core;
using Swivel.Windows;

internal static class CommonDuplicateNativeChecks
{
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        var choices=CommonFolders.Kinds.Select(k=>CommonFolderChoice.FromReported(k,"C:\\"+k)).ToArray();
        reject(()=>CommonDuplicateReader.Read(choices.Take(2).ToArray(),new CancellationToken(true)),"Precancelled comparison opens no native root");
        var app=Application.Current as App??new App(validationHost:true);string previous=app.Prefs.Language;
        var result=CommonDuplicates.Scan(choices.Take(2).ToArray(),c=>new Root(c.Kind.ToString()),CancellationToken.None);
        try
        {
            foreach(string language in new[]{"en","zh-Hans","zh-Hant","ja"})
            {
                app.Prefs.SetLanguage(language);var measure=new CommonFolderChooser(app,choices);var compare=new CommonFolderChooser(app,choices,CommonFolderChooserMode.Compare);
                check(measure.Title==app.L("Choose Common Folders to Measure")&&compare.Title==app.L("Choose Common Folders to Compare"),"Default measure chooser preserved; comparison is explicit separate mode");
                var panel=(DockPanel)compare.Content;var body=(StackPanel)panel.Children.OfType<ScrollViewer>().Single().Content;var buttons=panel.Children.OfType<WrapPanel>().Single().Children.OfType<Button>().ToArray();
                check(body.Children.OfType<TextBlock>().Any(t=>t.Text==app.L(CommonDuplicates.Limits))&&!body.Children.OfType<TextBlock>().Any(t=>t.Text==app.L(CommonFolders.Limits)),"Comparison chooser declares shared content budget, never metadata-only per-root measure budget");
                var boxes=body.Children.OfType<CheckBox>().ToArray();check(compare.Selected.Length==0&&!buttons[0].IsEnabled,"No default content selection");boxes[0].IsChecked=true;boxes[1].IsChecked=true;check(compare.Selected.Select(c=>c.Kind).SequenceEqual(choices.Take(2).Select(c=>c.Kind))&&buttons[0].IsEnabled,"Explicit selections preserve source order");
                var consent=new CommonDuplicateConfirmation(app,compare.Selected);var consentBody=(StackPanel)((DockPanel)consent.Content).Children.OfType<ScrollViewer>().Single().Content;
                check(consentBody.Children.OfType<TextBox>().Select(t=>t.Text).SequenceEqual(choices.Take(2).Select(c=>c.Path)),"Full content confirmation contains every selected root path");
                var dialog=new CommonDuplicatesDialog(app,result);string report=dialog.CurrentReport;
                check(report.Contains("C:\\Desktop\\same.txt")&&report.Contains("C:\\Downloads\\same.txt")&&report.Contains(app.L(CommonDuplicates.Limits))&&report.Contains(app.L(CommonDuplicates.Snapshot)),"Combined group report resolves opaque file keys to complete displayed source paths and retains limits");
                check(!report.Contains("0\\same.txt")&&!report.Contains("1\\same.txt")&&report.Contains("16"),"Opaque namespace never leaks as file path; original shared sample/full-hash byte accounting shown");
                dialog.Close();consent.Close();compare.Close();measure.Close();
            }
            {
                var dialog=new CommonDuplicatesDialog(app,result);var panel=(DockPanel)dialog.Content;var buttons=panel.Children.OfType<WrapPanel>().Single().Children.OfType<Button>().ToArray();
                var move=buttons.Single(b=>(string)b.Content==app.L("Move Selected to Recycle Bin…"));var undo=buttons.Single(b=>(string)b.Content==app.L("Undo Move"));
                var matches=(StackPanel)((ScrollViewer)((TabItem)panel.Children.OfType<TabControl>().Single().Items[1]).Content).Content;var lists=matches.Children.OfType<ListView>().ToArray();
                check(!move.IsEnabled&&!undo.IsEnabled&&buttons.Last().IsCancel&&matches.Children.OfType<TextBlock>().Any(t=>t.Text==app.L(DuplicateNotices.Recycle)),"Common duplicate results offer Recycle Bin cleanup, disabled until a selection, with the keep-a-copy notice");
                lists[0].SelectedIndex=0;lists[1].SelectAll();check(lists[1].Items.Count==2&&!move.IsEnabled,"Selecting every file of a cross-root group cannot move them: one copy must stay");
                lists[1].SelectedIndex=1;check(move.IsEnabled&&lists[1].SelectionMode==SelectionMode.Extended,"Selecting fewer files than the group holds enables the move");
                check(!dialog.CurrentReport.Contains(app.L("moved or removed in this window"))&&dialog.Moved==0,"Nothing is reported moved before a confirmed move");dialog.Close();
            }
            var root=result.Roots[0];string relative=new string('x',235);var sources=new Dictionary<string,CommonDuplicateFileRef>(StringComparer.Ordinal);var paths=new List<string>();
            for(int i=0;i<20000;i++){string path=relative+i.ToString("D5");string key="0\\"+path;sources.Add(key,new(root.Choice.Kind,path,4));paths.Add(key);}
            var huge=new CommonDuplicateResult(Array.AsReadOnly(new[]{root}),new(Array.AsReadOnly(new[]{new DuplicateGroup(4,paths.AsReadOnly(),new string('A',64))}),Array.Empty<DuplicateIssue>(),20000,20000,160000,false,false),new ReadOnlyDictionary<string,CommonDuplicateFileRef>(sources));
            var largeDialog=new CommonDuplicatesDialog(app,huge);reject(()=>_ = largeDialog.CurrentReport,"Oversized complete copy is refused without truncated clipboard content");largeDialog.Close();
            reject(()=>new CommonFolderChooser(app,choices,(CommonFolderChooserMode)9),"Unknown chooser mode refused");
        }
        finally{app.Prefs.SetLanguage(previous);}
    }
    private sealed class Root(string id):ICommonDuplicateRoot
    {
        public string Name=>"root";public IEnumerable<FolderEntry> Entries(CancellationToken token){yield return new("same.txt",false,4,0x20);}public IFolderMetadata OpenChild(string name)=>throw new InvalidOperationException();public void Dispose(){}
        public IDuplicateInput OpenDuplicate(DuplicateCandidate candidate)=>new Input(id);
    }
    private sealed class Input(string id):IDuplicateInput
    {
        public System.IO.Stream Stream{get;}=new System.IO.MemoryStream(new byte[]{1,2,3,4},false);public DuplicateStamp Stamp=>new(id,4,1);public DuplicateStamp Refresh()=>Stamp;public void Dispose()=>Stream.Dispose();
    }
}
