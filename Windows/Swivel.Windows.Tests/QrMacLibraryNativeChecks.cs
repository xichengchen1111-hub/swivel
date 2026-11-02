using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using Swivel.Core;
using Swivel.Windows;

internal static class QrMacLibraryNativeChecks
{
    internal static void Run(string directory,Action<bool,string> check,Action<Action,string> reject)
    {
        string folder=Path.Combine(directory,"Full Mac QR export");Directory.CreateDirectory(folder);
        var plan=QrMacLibraryExport.Prepare(new[]{new QrEntry("00000000000000000000000000000001","名称","原始 e\u0301 🦜","組",QrCorrection.Low)});
        string first=QrMacLibraryFiles.Save(plan,folder,default);
        string second=QrMacLibraryFiles.Save(plan,folder,default);
        check(Path.GetFileName(first)=="library-v1.json" && Path.GetFileName(second)=="library-v1 2.json","Full Mac export uses exact install name then a numbered exclusive name");
        check(File.ReadAllBytes(first).SequenceEqual(plan.Encode()) && File.ReadAllBytes(second).SequenceEqual(plan.Encode()),"Full Mac export never overwrites prior JSON and writes reviewed bytes");
        using(var source=QrMacMergeFile.Open(first,default))
        {
            reject(()=>File.Move(first,first+" moved"),"Full Mac merge pins the source file");
            reject(()=>Directory.Move(folder,folder+" moved"),"Full Mac merge pins ancestor directory");
            var merged=QrMacLibraryExport.Prepare(new[]{new QrEntry("00000000000000000000000000000002","Other","new")},source.Bytes);
            string third=QrMacLibraryFiles.Save(merged,folder,default);
            check(MacQrLibrary.Decode(File.ReadAllBytes(third)).Rows.Select(x=>x.Text).SequenceEqual(new[]{"原始 e\u0301 🦜","new"}),"Native full Mac merge/export/readback keeps original exact values first");
        }
        string invalid=Path.Combine(folder,"invalid.json");File.WriteAllText(invalid,"{}");
        reject(()=>QrMacMergeFile.Open(invalid,default),"Native invalid Mac merge refuses rather than exporting without it");
        File.Move(invalid,invalid+" moved");check(File.Exists(invalid+" moved"),"Failed Mac merge releases its source lease");
        using(var canceled=new CancellationTokenSource())
        {
            canceled.Cancel();int count=Directory.GetFiles(folder).Length;
            reject(()=>QrMacLibraryFiles.Save(plan,folder,canceled.Token),"Native full Mac export pre-cancel refuses");
            reject(()=>QrMacMergeFile.Open(first,canceled.Token),"Native full Mac merge pre-cancel refuses");
            check(Directory.GetFiles(folder).Length==count,"Cancelled full Mac export creates no files");
        }
        check(!Directory.GetFiles(folder,".swivel-*.tmp").Any(),"Full Mac export leaves no staging files");
        var app=Application.Current as App ?? throw new InvalidOperationException("An isolated validation App is required.");
        if(!app.IsValidationHost) throw new InvalidOperationException("Use a validation App.");
        string language=app.Prefs.Language;
        try
        {
            foreach(string locale in new[]{"en","zh-Hans","zh-Hant","ja"})
            {
                app.Prefs.SetLanguage(locale);
                var preview=new QrMacLibraryDialog(app,plan,false);
                try
                {
                    var root=(DockPanel)preview.Content;
                    var footer=root.Children.OfType<WrapPanel>().Single();
                    var scroll=root.Children.OfType<ScrollViewer>().Single();
                    var body=(StackPanel)scroll.Content;
                    check(DockPanel.GetDock(footer)==Dock.Bottom && root.Children.IndexOf(footer)<root.Children.IndexOf(scroll),"Full Mac preview keeps export/merge/cancel outside scroll");
                    check(footer.Children.OfType<Button>().Count()==3 && footer.Children.OfType<Button>().Single(x=>Equals(x.Content,app.L("Choose QR Export Folder…"))).IsEnabled,"Full Mac preview offers explicit merge and export");
                    check(body.Children.OfType<TextBlock>().Any(x=>x.Text==app.L(QrMacLibraryDialog.NoMerge)) && body.Children.OfType<TextBlock>().Any(x=>x.Text==app.L(QrMacLibraryExport.Install)),"Full Mac preview discloses replacement and optional merge before export");
                    check(body.Children.OfType<TextBox>().Single().Text==LibraryJson.Visible("原始 e\u0301 🦜") && body.Children.OfType<TextBox>().All(x=>x.IsReadOnly),"Full Mac preview displays complete unchanged text read-only");
                    check(body.Children.OfType<TextBlock>().Any(x=>x.Text==app.L(QrMacLibraryExport.MissingDate)) && body.Children.OfType<TextBlock>().Any(x=>x.Text==app.L(QrMacLibraryExport.CorrectionChanged)),"Full Mac preview discloses date and level changes per row");
                }
                finally {preview.Close();}
                var empty=new QrMacLibraryDialog(app,QrMacLibraryExport.Prepare(Array.Empty<QrEntry>()),false);
                try
                {
                    var footer=((DockPanel)empty.Content).Children.OfType<WrapPanel>().Single();
                    check(!footer.Children.OfType<Button>().Single(x=>Equals(x.Content,app.L("Choose QR Export Folder…"))).IsEnabled && footer.Children.OfType<Button>().Single(x=>Equals(x.Content,app.L(QrMacLibraryDialog.Merge))).IsEnabled,"Empty Mac output cannot export but may select a merge source");
                }
                finally {empty.Close();}
                var install=new QrMacInstallDialog(app,first);
                try
                {
                    var text=((DockPanel)install.Content).Children.OfType<TextBox>().Single();
                    check(text.IsReadOnly && text.Text.Contains(first,StringComparison.Ordinal) && text.Text.Contains(app.L(QrMacLibraryExport.Install),StringComparison.Ordinal) && AutomationProperties.GetName(text)==app.L("Mac install steps"),"Post-export install steps include actual numbered output and exact destination");
                }
                finally {install.Close();}
            }
        }
        finally {app.Prefs.SetLanguage(language);}

    }
}
