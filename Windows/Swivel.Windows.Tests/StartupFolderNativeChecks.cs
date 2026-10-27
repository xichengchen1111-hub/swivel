using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Swivel.Core;
using Swivel.Windows;

internal static class StartupFolderNativeChecks
{
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        var choices=Enum.GetValues<StartupFolderKind>().Select(k=>StartupFolderChoice.FromReported(k,"C:\\"+k+"Startup")).ToArray();
        reject(()=>StartupFolderReader.ReadLocations(new CancellationToken(true)),"Precancelled startup-folder resolver reads no Windows paths");reject(()=>StartupFolderReader.ReadEntries(choices,new CancellationToken(true)),"Precancelled startup-folder entry scan opens no native directory");
        var app=Application.Current as App??new App(validationHost:true);string previous=app.Prefs.Language;
        var results=StartupFolders.Scan(choices,c=>new Metadata(),CancellationToken.None);
        try
        {
            foreach(string language in new[]{"en","zh-Hans","zh-Hant","ja"})
            {
                app.Prefs.SetLanguage(language);var chooser=new StartupFolderChooser(app,choices);var panel=(DockPanel)chooser.Content;var body=(StackPanel)panel.Children.OfType<ScrollViewer>().Single().Content;var boxes=body.Children.OfType<CheckBox>().ToArray();var read=panel.Children.OfType<WrapPanel>().Single().Children.OfType<Button>().First();
                check(chooser.Selected.Length==0&&!read.IsEnabled&&boxes.All(b=>b.IsChecked==false),"Startup chooser initially selects no folder or background scan");
                boxes[0].IsChecked=true;check(chooser.Selected.Length==1&&read.IsEnabled,"Read requires explicit eligible selection");boxes[1].IsChecked=true;check(chooser.Selected.Length==2,"Both reported locations may be explicitly chosen");boxes[0].IsChecked=false;boxes[1].IsChecked=false;check(!read.IsEnabled,"Cleared choices disable metadata reading");chooser.Close();
                var invalid=new StartupFolderChooser(app,new[]{choices[0],StartupFolderChoice.Unavailable(StartupFolderKind.Common)});var invalidBody=(StackPanel)((DockPanel)invalid.Content).Children.OfType<ScrollViewer>().Single().Content;check(!invalidBody.Children.OfType<CheckBox>().Last().IsEnabled,"Unavailable reported roots cannot be selected");invalid.Close();
                var dialog=new StartupFolderDialog(app,results);string report=dialog.CurrentReport;
                check(report.Contains("C:\\CurrentUserStartup\\App.lnk")&&report.Contains("C:\\CommonStartup\\App.lnk")&&report.Contains(app.L(StartupFolders.Limits))&&report.Contains(app.L(StartupFolders.Meaning)),"Report contains complete displayed source paths and metadata-only/budget/state limits");check(report.Contains(app.L("Shortcut filename only"))&&report.Contains(app.L("Depth limit"))&&report.Contains("—"),"Filename hint never hides folder depth reason or fabricates folder size");dialog.Close();
                var failed=new StartupFolderDialog(app,new[]{new StartupFolderResult(choices[0],null,StartupFolders.Unavailable,5,256,5,DateTimeOffset.UnixEpoch)});check(failed.CurrentReport.Contains(app.L(StartupFolders.Unavailable))&&failed.CurrentReport.Contains(app.L("Windows Error Code")+": 5"),"Root failures and captured native codes preserved even at zero entries");failed.Close();
            }
        }
        finally{app.Prefs.SetLanguage(previous);}
    }
    private sealed class Metadata:IFolderMetadata
    {
        public string Name=>"Startup";public IEnumerable<FolderEntry> Entries(CancellationToken token){yield return new("App.lnk",false,12,0x20);yield return new("subfolder",true,0,0x10);}public IFolderMetadata OpenChild(string name)=>throw new InvalidOperationException("No descendant should open.");public void Dispose(){}
    }
}
