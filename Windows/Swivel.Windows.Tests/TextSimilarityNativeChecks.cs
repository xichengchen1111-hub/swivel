using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Swivel.Core;
using Swivel.Windows;

internal static class TextSimilarityNativeChecks
{
    public static void Run(string directory,Action<bool,string> check,Action<Action,string> reject)
    {
        string folder=Path.Combine(directory,"similar-text");Directory.CreateDirectory(folder);Directory.CreateDirectory(Path.Combine(folder,"child"));
        string content="private-content-one\nprivate-content-two\nprivate-content-three\nprivate-content-four\nprivate-content-five";
        string a=Path.Combine(folder,"a.txt"),b=Path.Combine(folder,"child","b.TXT");
        File.WriteAllText(a,content,new UTF8Encoding(false,true));File.WriteAllText(b," "+content.Replace("\n"," \r\n ")+" ",new UnicodeEncoding(false,true,true));
        File.WriteAllText(Path.Combine(folder,"plain.md"),content);File.WriteAllText(Path.Combine(folder,"unknown.bin"),content);File.WriteAllBytes(Path.Combine(folder,"empty.txt"),Array.Empty<byte>());
        var before=Directory.GetFiles(folder,"*",SearchOption.AllDirectories).ToDictionary(p=>p,File.ReadAllBytes);
        var result=DoctorTextSimilarity.Run(folder,default);
        check(result.Similar.Pairs.Count==1&&result.Similar.Pairs[0].First=="a.txt"&&result.Similar.Pairs[0].Second==@"child\b.TXT"&&result.Similar.Pairs[0].Percent==100,"Native held-source strict UTF8/UTF16 comparison across descendants and extension case");
        check(result.Similar.Issues.Count==2&&result.Similar.Issues.Any(i=>i.RelativePath=="unknown.bin")&&result.Similar.Issues.Any(i=>i.RelativePath=="empty.txt"),"Unsupported and empty files remain recorded");
        using(var root=NativeFiles.MetadataDirectory.Open(folder))
        using(var input=root.OpenDuplicate(new("a.txt",before[a].Length)))
        {
            reject(()=>File.Open(a,FileMode.Open,FileAccess.Write,FileShare.ReadWrite).Dispose(),"Text comparison lease blocks writer");
            reject(()=>File.Move(a,a+" moved"),"Text comparison lease blocks source rename");
            reject(()=>Directory.Move(folder,folder+" moved"),"Text chosen root and ancestors pinned");
        }
        using(var locked=File.Open(b,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
        {
            var unavailable=DoctorTextSimilarity.Run(folder,default);
            check(unavailable.Similar.Pairs.Count==0&&unavailable.Similar.Issues.Any(i=>i.RelativePath==@"child\b.TXT"),"Unavailable source is visible and never a partial-text match");
        }
        string system=Path.Combine(folder,"system.txt");File.WriteAllText(system,content);File.SetAttributes(system,FileAttributes.System);
        try
        {
            var partial=DoctorTextSimilarity.Run(folder,default);
            check(partial.Folder.Root.Incomplete&&partial.Folder.Root.Skipped==1&&!partial.Similar.Pairs.Any(p=>p.First=="system.txt"||p.Second=="system.txt"),"System file remains a metadata skip and is never read");
            var app=Application.Current as App??new App();var dialog=new TextSimilarityDialog(app,folder,partial.Folder,partial.Similar);
            check(dialog.Content is DockPanel&&dialog.CurrentReport.Contains(folder)&&dialog.CurrentReport.Contains("system.txt")&&dialog.CurrentReport.Contains("unknown.bin")&&dialog.CurrentReport.Contains("empty.txt"),"Report retains metadata and content skips with chosen root");
            check(!dialog.CurrentReport.Contains("private-content-one")&&dialog.CurrentReport.Contains(b),"Report includes full paths but never original or matching text lines");
            var lists=Children(dialog).OfType<ListView>().ToArray();var pairs=lists.Single(v=>AutomationProperties.GetName(v)==app.L("Matching Text Pairs"));
            var selectedButton=Children(dialog).OfType<Button>().Single(v=>Equals(v.Content,app.L("Copy Selected Pair")));
            check(lists.Length==3&&lists.All(v=>VirtualizingPanel.GetIsVirtualizing(v)&&VirtualizingPanel.GetVirtualizationMode(v)==VirtualizationMode.Recycling)&&!selectedButton.IsEnabled,"Native virtual result/issue lists; no automatic selected-pair copying");
            pairs.SelectedIndex=0;
            check(selectedButton.IsEnabled&&dialog.SelectedReport.Contains(a)&&dialog.SelectedReport.Contains(b)&&!dialog.SelectedReport.Contains("system.txt")&&!dialog.SelectedReport.Contains("private-content-one"),"Explicit selected pair report contains its paths without unrelated skips/content");
            var move=Children(dialog).OfType<Button>().Single(v=>Equals(v.Content,app.L("Move Selected to Recycle Bin…")));var files=lists.Single(v=>AutomationProperties.GetName(v)==app.L("Files in Selected Pair"));
            check(!move.IsEnabled&&files.Items.Count==2,"Selecting a pair lists its two files; nothing is moved until one file is chosen");
            files.SelectedIndex=1;check(move.IsEnabled,"Choosing one file of a compared pair enables the move");files.SelectedIndex=-1;
            pairs.SelectedIndex=-1;check(!selectedButton.IsEnabled&&!move.IsEnabled&&files.Items.Count==0,"Clearing selection disables stale copy and move actions");dialog.Close();
            var empty=new TextSimilarityDialog(app,folder,partial.Folder,partial.Similar with{Pairs=Array.Empty<SimilarTextPair>(),MatchingPairs=0});
            check(empty.CurrentReport.Contains("system.txt")&&empty.CurrentReport.Contains(app.L("No similar pairs in the comparisons completed. Skipped or unexamined files may still be similar.")),"Zero matches never hide partial scan or recorded issues");empty.Close();
            var confirm=new TextSimilarityConfirmation(app,folder);check(confirm.Content is DockPanel&&Children(confirm).OfType<Button>().Any(v=>v.IsCancel)&&Children(confirm).OfType<TextBlock>().Any(v=>v.Text==app.L(TextSimilarityNotices.Rules)),"Native confirmation declares line-set rules and explicit cancel");confirm.Close();
        }
        finally{File.SetAttributes(system,FileAttributes.Normal);File.Delete(system);}
        reject(()=>DoctorTextSimilarity.Run(folder,new CancellationToken(true)),"Pre-cancel occurs before native root access");
        check(before.All(x=>File.ReadAllBytes(x.Key).SequenceEqual(x.Value))&&!Directory.GetFiles(folder,".swivel-*.tmp",SearchOption.AllDirectories).Any(),"Read-only operation preserves all source bytes and produces no staging files");
        Directory.Move(folder,folder+" released");check(Directory.Exists(folder+" released"),"Completion releases root and descendant handles");

        // Moving one file of a similar pair to the Recycle Bin, keeping the other.
        string moved=folder+" released",ka=Path.Combine(moved,"a.txt"),kb=Path.Combine(moved,"child","b.TXT");var stamps=result.Similar.Stamps;var sa=stamps["a.txt"];var sb=stamps[@"child\b.TXT"];
        check(stamps.Count==3&&stamps.ContainsKey("plain.md"),"The comparison records the identity, size and modified time of each compared file");
        reject(()=>NativeFiles.RecycleSimilar(kb,sb,kb,sb),"Similar cleanup never keeps the file it moves");
        reject(()=>NativeFiles.RecycleSimilar(kb,sb,Path.Combine(moved,"plain.md"),sa),"Similar cleanup refuses when the kept file is not the compared one");
        using(File.Open(ka,FileMode.Open,FileAccess.ReadWrite,FileShare.ReadWrite))
            reject(()=>NativeFiles.RecycleSimilar(kb,sb,ka,sa),"Similar cleanup refuses while the kept file is open for writing");
        var written=File.GetLastWriteTimeUtc(kb);File.SetLastWriteTimeUtc(kb,written.AddHours(1));
        reject(()=>NativeFiles.RecycleSimilar(kb,sb,ka,sa),"Similar cleanup refuses a file changed since the comparison");
        File.SetLastWriteTimeUtc(kb,written);check(File.Exists(ka)&&File.Exists(kb),"Refused similar files stay in place");
        if(!RecycleBin.Available(kb)){Console.WriteLine("SKIP: no Recycle Bin on the test drive.");return;}
        string stored=NativeFiles.RecycleSimilar(kb,sb,ka,sa);
        check(!File.Exists(kb)&&File.ReadAllBytes(ka).SequenceEqual(before[a])&&stored.Contains("$Recycle.Bin",StringComparison.OrdinalIgnoreCase),"Similar cleanup moves one file of the pair to the Recycle Bin and keeps the other: "+stored);
        NativeFiles.RestoreRecycled(kb,stored,false);check(File.ReadAllBytes(kb).SequenceEqual(before[b]),"Undo restores the similar file from the Recycle Bin");
    }
    private static IEnumerable<DependencyObject> Children(DependencyObject root)
    {
        yield return root;foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())foreach(var descendant in Children(child))yield return descendant;
    }
}
