using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Swivel.Core;
using Swivel.Windows;

internal static class CommonFolderNativeChecks
{
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        reject(()=>CommonFolderReader.Read(new CancellationToken(true)),"Precancelled location reading does not initialize COM or resolve folders");
        var desktop=CommonFolderChoice.FromReported(CommonFolderKind.Desktop,"C:\\Desktop");var documents=CommonFolderChoice.FromReported(CommonFolderKind.Documents,"C:\\Documents");
        reject(()=>CommonFolderReader.Measure(desktop,new CancellationToken(true)),"Precancelled common-root measurement opens no metadata directory");
        var scan=FolderSizes.Scan(new Metadata(),CancellationToken.None);var app=Application.Current as App??new App(validationHost:true);string previous=app.Prefs.Language;
        try
        {
            foreach(string language in new[]{"en","zh-Hans","zh-Hant","ja"})
            {
                app.Prefs.SetLanguage(language);var choices=CommonFolders.Kinds.Select(k=>k==CommonFolderKind.Videos?CommonFolderChoice.Unavailable(k,"Windows could not report this folder location.",0x80010106):CommonFolderChoice.FromReported(k,"C:\\"+k)).ToArray();
                var chooser=new CommonFolderChooser(app,choices);var root=(DockPanel)chooser.Content;var footer=root.Children.OfType<WrapPanel>().Single();var body=(StackPanel)((ScrollViewer)root.Children.Cast<UIElement>().Single(x=>x is ScrollViewer)).Content;
                var boxes=body.Children.OfType<CheckBox>().ToArray();var measure=footer.Children.OfType<Button>().First();check(chooser.Selected.Length==0&&!measure.IsEnabled&&boxes.All(b=>b.IsChecked!=true)&&!boxes[^1].IsEnabled,"Six locations have no default selection and unavailable locations cannot be selected");boxes[0].IsChecked=true;check(chooser.Selected.Length==1&&measure.IsEnabled&&chooser.Selected[0].Kind==CommonFolderKind.Desktop,"Only explicit selected eligible folder enters immutable selection snapshot");boxes[0].IsChecked=false;check(!measure.IsEnabled&&chooser.Selected.Length==0,"Clearing choices disables measurement again");chooser.Close();
                var results=new[]{new CommonFolderResult(desktop,scan,recorded:DateTimeOffset.UnixEpoch),new CommonFolderResult(documents,null,"This reported folder could not be measured.",5,DateTimeOffset.UnixEpoch)};var summary=new CommonFolderResultsDialog(app,results,new CancellationToken(true));
                check(summary.CurrentReport.Contains("C:\\Desktop")&&summary.CurrentReport.Contains("C:\\Documents")&&summary.CurrentReport.Contains(app.L(CommonFolders.Limits))&&summary.CurrentReport.Contains(app.L("Windows Error Code")+" 5"),"Summary preserves separate selected-root paths, failed root and full overlap/budget limits");check(summary.CurrentReport.Contains("—"),"Failed root is unavailable, never a fabricated zero size");summary.Close();
                var plain=new FolderMapDialog(app,desktop.Path!,scan);var cache=new FolderMapDialog(app,desktop.Path!,scan,CacheLocations.ResultContext,CacheLocations.Name(CacheLocationKind.Temporary));var common=new FolderMapDialog(app,desktop.Path!,scan,CommonFolders.MapContext,CommonFolders.Name(desktop.Kind),"Common Folder Size Map");
                check(plain.CurrentReport.StartsWith(app.L("Folder Size Map"))&&cache.CurrentReport.StartsWith(app.L("User Cache Location Size Map"))&&common.CurrentReport.StartsWith(app.L("Common Folder Size Map")),"Optional common map title preserves old null/cache titles and reports");check(common.CurrentReport.Contains(app.L(CommonFolders.MapContext))&&!common.CurrentReport.Contains(app.L(CacheLocations.ResultContext)),"Common-root map is not mislabeled as cache or safe-delete inventory");plain.Close();cache.Close();common.Close();
            }
        }
        finally{app.Prefs.SetLanguage(previous);}
        reject(()=>new CommonFolderResult(desktop,null),"Common root result must contain either a scan or a failure/skip");reject(()=>new CommonFolderResult(desktop,scan,"bad"),"A successful scan cannot also be a failure");reject(()=>new CommonFolderResult(CommonFolderChoice.FromReported(CommonFolderKind.Desktop,"\\\\server\\share"),scan),"Invalid reported location never enters a root result");
    }
    private sealed class Metadata:IFolderMetadata
    {
        public string Name=>"Folder";public System.Collections.Generic.IEnumerable<FolderEntry> Entries(CancellationToken token){token.ThrowIfCancellationRequested();yield return new("one.txt",false,3,0x20);}public IFolderMetadata OpenChild(string name)=>throw new InvalidOperationException();public void Dispose(){}
    }
}
