using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Swivel.Core;

internal static class StartupFolderChecks
{
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        StartupFolderChoice[] Choices()=>Enum.GetValues<StartupFolderKind>().Select(k=>StartupFolderChoice.FromReported(k,"C:\\"+k+"Startup")).ToArray();
        int live=0,maxLive=0,closed=0,childOpens=0;var choices=Choices();
        var results=StartupFolders.Scan(choices,c=>{live++;maxLive=Math.Max(maxLive,live);return new Root(Entries(),()=>{live--;closed++;},()=>childOpens++);},CancellationToken.None);
        check(results.Count==2&&live==0&&maxLive==1&&closed==2&&childOpens==0,"Startup inventory is serial, holds one root at a time, closes before return and never opens subfolders");
        check(results.All(r=>r.Scan!.Root.Children.Count==6&&r.Scan.Root.Incomplete&&r.Scan.Root.Skipped==3),"Direct files retained alongside unexpanded, system and link issues");
        foreach(var result in results)
        {
            var nodes=result.Scan!.Root.Children;check(StartupFolders.Hint(nodes[0])=="Shortcut filename only"&&StartupFolders.Hint(nodes[1])=="Internet-shortcut filename only"&&StartupFolders.Hint(nodes[2])=="File metadata only","Hints use only final case-insensitive endings, not target/content or double-extension guesses");check(StartupFolders.Hint(nodes[3])=="Folder not expanded"&&nodes[3].Issue=="Depth limit"&&nodes[3].Children.Count==0,"Depth zero records folder without opening it");check(StartupFolders.Hint(nodes[4])=="Not classified"&&StartupFolders.Hint(nodes[5])=="Not classified","Skipped metadata is not classified by filename hint");
        }
        reject(()=>((IList<StartupFolderResult>)results).Clear(),"Startup results immutable");
        foreach(int budget in new[]{1,2,3,512})
        {
            var limited=StartupFolders.Scan(Choices(),c=>new Root(Many(600)),CancellationToken.None,maxEntries:budget);check(limited.Sum(r=>r.Scan?.Entries??0)==budget,"One shared counted-entry budget, not one budget per root");check(limited[0].EntryQuota==Math.Max(1,budget/2)&&(budget==1?limited[1].Issue==StartupFolders.NoEntries:limited[1].Scan!.EntryLimit),"Fair allocation and unattempted roots visible");
        }
        foreach(int budget in new[]{10,512})
        {
            int first=budget/2;var failed=StartupFolders.Scan(Choices(),c=>new Root(Many(first),after:()=>throw new System.Security.SecurityException("metadata denied")),CancellationToken.None,maxEntries:budget);
            check(failed.All(r=>r.Issue==StartupFolders.Unavailable)&&failed.Sum(r=>r.EntryQuota)==budget,"Expected escaped scan failure conservatively consumes reserved quota");
        }
        var refunded=StartupFolders.Scan(Choices(),c=>new Root(c.Kind==StartupFolderKind.CurrentUser?Array.Empty<FolderEntry>():Many(20)),CancellationToken.None,maxEntries:10);check(refunded[0].Scan!.Entries==0&&refunded[1].EntryQuota==10&&refunded[1].Scan!.Entries==10,"Unused completed share passes to next root");
        int opens=0;var failedOpen=StartupFolders.Scan(Choices(),c=>{opens++;if(c.Kind==StartupFolderKind.CurrentUser)throw new System.ComponentModel.Win32Exception(5);return new Root(Many(20));},CancellationToken.None,maxEntries:10);check(opens==2&&failedOpen[0].Error==5&&failedOpen[1].EntryQuota==10&&failedOpen[1].Scan!.Entries==10,"Failed opening does not spend entries; later root and captured native code remain");
        double elapsed=0;opens=0;var timed=StartupFolders.Scan(Choices(),c=>{opens++;elapsed=10;return new Root();},CancellationToken.None,elapsed:()=>TimeSpan.FromSeconds(elapsed));check(opens==1&&timed[0].Scan!.TimeLimit&&timed[1].Issue==StartupFolders.NoTime,"Root open time belongs to shared soft clock; later root never opened");
        var same=Choices();same[1]=StartupFolderChoice.FromReported(StartupFolderKind.Common,same[0].Path+"\\");opens=0;var repeated=StartupFolders.Scan(same,c=>{opens++;return new Root();},CancellationToken.None);check(opens==1&&repeated[1].Issue==StartupFolders.Repeated,"Exact guarded path repeat visibly skipped");
        same[1]=StartupFolderChoice.FromReported(StartupFolderKind.Common,same[0].Path!.ToLowerInvariant());opens=0;_=StartupFolders.Scan(same,c=>{opens++;return new Root();},CancellationToken.None);check(opens==2,"Case variants not silently merged");
        opens=0;int disposeAttempts=0;bool aggregate=false;try{_=StartupFolders.Scan(Choices(),c=>{opens++;return new Root(dispose:()=>{disposeAttempts++;throw new IOException("dispose failure");});},CancellationToken.None);}catch(AggregateException){aggregate=true;}
        check(aggregate&&opens==1&&disposeAttempts==1,"Throwing disposal terminates batch rather than opening another root or adding duplicate success/failure rows");
        using(var cancel=new CancellationTokenSource())
        {
            opens=0;closed=0;reject(()=>StartupFolders.Scan(Choices(),c=>{opens++;return new Root(dispose:()=>{closed++;cancel.Cancel();throw new IOException();});},cancel.Token),"Cancel plus disposal fault still stops batch");check(opens==1&&closed==1,"No next root after cancellation during cleanup");
        }
        using(var cancel=new CancellationTokenSource())
        {
            closed=0;reject(()=>StartupFolders.Scan(Choices(),c=>{cancel.Cancel();return new Root(dispose:()=>closed++);},cancel.Token),"Cancel immediately after open");check(closed==1,"Newly opened root belongs to finally before cancellation check");
        }
        int invalidOpens=0;IFolderMetadata Invalid(StartupFolderChoice c){invalidOpens++;return new Root();}
        reject(()=>StartupFolders.Scan(Array.Empty<StartupFolderChoice>(),Invalid,CancellationToken.None),"Empty roots refused");reject(()=>StartupFolders.Scan(new[]{choices[0],choices[0]},Invalid,CancellationToken.None),"Repeated kind refused");reject(()=>StartupFolders.Scan(new[]{StartupFolderChoice.FromReported(StartupFolderKind.Common,"\\\\server\\share")},Invalid,CancellationToken.None),"Unsupported path cannot become a target");
        foreach(int value in new[]{0,513})reject(()=>StartupFolders.Scan(Choices(),Invalid,CancellationToken.None,maxEntries:value),"Increased/invalid entry budget refused");foreach(int value in new[]{0,11})reject(()=>StartupFolders.Scan(Choices(),Invalid,CancellationToken.None,seconds:value),"Increased/invalid time budget refused");reject(()=>StartupFolders.Scan(Choices(),Invalid,new CancellationToken(true)),"Precancelled startup scan opens nothing");check(invalidOpens==0,"Validate entire selection/limits before any I/O");
        reject(()=>StartupFolderChoice.FromReported((StartupFolderKind)2,"C:\\valid"),"Closed two-kind catalog");check(!StartupFolderChoice.Unavailable(StartupFolderKind.CurrentUser).CanAttempt,"Resolver unavailable cannot be selected");check(StartupFolderChoice.FromReported(StartupFolderKind.CurrentUser,"C:\\valid\\").Path=="C:\\valid","Original one-trailing-separator guard retained");
        foreach(string path in new[]{"C:\\", "C:\\a\\..\\b", "C:\\a\u202e", "C:\\a\ud800", "C:\\a\U000e0020"})check(!StartupFolderChoice.FromReported(StartupFolderKind.CurrentUser,path).CanAttempt,"Original path/unicode protections retained");
        var prefs=new Prefs();var action=Catalog.Actions.Single(a=>a.Id=="doctor.startupfolders");check(action.Feature==Feature.Doctor&&!action.RequiresFiles&&!Catalog.Available(prefs,Array.Empty<string>()).Contains(action),"Startup metadata action default off and ignores files");prefs.Set(Feature.Doctor,true);check(Catalog.Available(prefs,Array.Empty<string>()).Contains(action)&&WindowsDoctor.Areas.Single(a=>a.Id=="background").ActionIds.Contains(action.Id),"Startup action reachable from Background Items only after explicit feature enablement");
    }
    private static FolderEntry[] Entries()=>new[]{new FolderEntry("App.LNK",false,12,0x20),new FolderEntry("Site.Url",false,13,0x20),new FolderEntry("app.lnk.txt",false,14,0x20),new FolderEntry("subfolder",true,0,0x10),new FolderEntry("system.lnk",false,12,0x24),new FolderEntry("link.lnk",false,12,0x420)};
    private static FolderEntry[] Many(int count)=>Enumerable.Range(0,count).Select(i=>new FolderEntry(i+".lnk",false,12,0x20)).ToArray();
    private sealed class Root(FolderEntry[]? entries=null,Action? dispose=null,Action? child=null,Action? after=null):IFolderMetadata
    {
        public string Name=>"Startup";public IEnumerable<FolderEntry> Entries(CancellationToken token){foreach(var entry in entries??Array.Empty<FolderEntry>()){token.ThrowIfCancellationRequested();yield return entry;}after?.Invoke();}public IFolderMetadata OpenChild(string name){child?.Invoke();throw new InvalidOperationException("No descendant open expected.");}public void Dispose()=>dispose?.Invoke();
    }
}
