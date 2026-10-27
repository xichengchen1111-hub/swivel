using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Swivel.Core;
using Swivel.Windows;

internal static class CacheLocationNativeChecks
{
    internal static void Run(string directory,Action<bool,string> check,Action<Action,string> reject)
    {
        reject(()=>CacheLocationReader.Read(new CancellationToken(true)),"Precancelled location discovery resolves no paths");
        var app=Application.Current as App??new App(validationHost:true);var choices=CacheLocations.Choices(Path.Combine(directory,"cache-location"),@"\\server\cache");
        var preview=new CacheLocationDialog(app,choices);var root=(DockPanel)preview.Content;var footer=root.Children.OfType<WrapPanel>().Single();
        check(preview.Selected is null&&!footer.Children.OfType<Button>().First().IsEnabled,"Cache chooser starts without selected path or enabled Measure");
        var body=(StackPanel)((ScrollViewer)root.Children[1]).Content;var radios=body.Children.OfType<RadioButton>().ToArray();check(radios.Length==2&&radios[0].IsEnabled&&!radios[1].IsEnabled,"Refused path stays visible but disabled");radios[0].IsChecked=true;
        check(ReferenceEquals(preview.Selected,choices[0])&&footer.Children.OfType<Button>().First().IsEnabled,"Only explicit selection authorizes the exact shown immutable candidate");preview.Close();
        string folder=choices[0].Path!;Directory.CreateDirectory(folder);string path=Path.Combine(folder,"not-safe-delete.txt");File.WriteAllText(path,"abc");
        FolderScanResult scan;
        using(var exclusive=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None))scan=CacheLocationReader.Scan(choices[0],default);
        check(scan.Root.Bytes==3&&scan.Root.Files==1,"Location scan reads directory metadata despite exclusive child-file lock; content is not opened");
        var standard=new FolderMapDialog(app,folder,scan);string normal=standard.CurrentReport;standard.Close();var explicitNull=new FolderMapDialog(app,folder,scan,null,null);check(normal==explicitNull.CurrentReport,"Null optional context keeps original folder map report byte-identical");explicitNull.Close();
        var cache=new FolderMapDialog(app,folder,scan,CacheLocations.ResultContext,CacheLocations.Name(choices[0].Kind));
        check(cache.CurrentReport.Contains(app.L(CacheLocations.ResultContext))&&cache.CurrentReport.Contains(app.L(CacheLocations.Name(choices[0].Kind)))&&cache.CurrentReport.Contains(folder),"Cache result copy includes source kind/path and complete no-safe-delete/no-reclaimable/context limits");cache.Close();
        check(File.ReadAllText(path)=="abc"&&Directory.GetFiles(folder).Length==1,"Cache inspection never creates or deletes entries");
        reject(()=>CacheLocationReader.Scan(choices[0],new CancellationToken(true)),"Precancelled cache scan opens no metadata source");reject(()=>CacheLocationReader.Scan(choices[1],default),"Refused path cannot enter native scan");
        Directory.Move(folder,folder+" released");check(Directory.Exists(folder+" released"),"Read-only scan releases root and ancestor handles after snapshot");
    }
}
