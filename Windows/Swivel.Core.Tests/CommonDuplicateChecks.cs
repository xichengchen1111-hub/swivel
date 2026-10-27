using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Swivel.Core;

internal static class CommonDuplicateChecks
{
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        CommonFolderChoice[] Choices(int count)=>CommonFolders.Kinds.Take(count).Select(k=>CommonFolderChoice.FromReported(k,"C:\\"+k)).ToArray();
        var selected=Choices(6);int live=0,inputs=0,maxInputs=0,disposed=0,opens=0;var roots=new List<Root>();
        ICommonDuplicateRoot Open(CommonFolderChoice choice)
        {
            live++;opens++;var root=new Root(new[]{new FolderEntry("same.txt",false,4,0x20)},candidate=>
            {
                check(live==6&&candidate.RelativePath=="same.txt"&&candidate.Length==4,"Every root is pinned during one combined content scan; opaque namespace never reaches native I/O");
                inputs++;maxInputs=Math.Max(maxInputs,inputs);return new Input(new byte[]{1,2,3,4},new("volume"+choice.Kind+"-file",4,1),()=>inputs--);
            },()=>{live--;disposed++;});roots.Add(root);return root;
        }
        var result=CommonDuplicates.Scan(selected,Open,CancellationToken.None);
        check(result.Roots.Count==6&&result.Duplicates.Groups.Single().Paths.Count==6&&result.Duplicates.BytesRead==48,"One cross-root group and shared sample plus full-hash accounting");
        check(live==0&&inputs==0&&maxInputs==1&&disposed==6,"All root leases released before return, with at most one data lease");check(opens==6&&result.Sources.Count==6,"Each root opened once, immutable exact file routing retained");
        reject(()=>((IDictionary<string,CommonDuplicateFileRef>)result.Sources).Clear(),"File reference map immutable");reject(()=>((IList<CommonDuplicateRootResult>)result.Roots).Clear(),"Root result sequence immutable");
        foreach(int count in new[]{2,3,6})
        {
            var choices=Choices(count);int active=0,closed=0;
            var sliced=CommonDuplicates.Scan(choices,c=>{active++;return new Root(Enumerable.Range(0,5).Select(i=>new FolderEntry(i+".txt",false,4,0x20)).ToArray(),candidate=>new Input(new byte[]{1,2,3,4},new(c.Kind+candidate.RelativePath,4,1)),()=>{active--;closed++;});},CancellationToken.None,new(MetadataEntries:count,Content:new(MaxReadBytes:12)));
            check(sliced.Roots.Sum(r=>r.Scan!.Entries)==count&&sliced.Roots.All(r=>r.Scan!.EntryLimit&&r.EntryQuota==1),"Shared entry limit distributed fairly among remaining roots");check(sliced.Duplicates.Candidates==count&&sliced.Duplicates.BytesRead==12&&sliced.Duplicates.ByteLimit&&sliced.Duplicates.Groups.Count==0,"Shared content budget is not multiplied by root count");check(active==0&&closed==count,"Limited result still releases every root");
        }
        var same=Choices(2);same[1]=CommonFolderChoice.FromReported(CommonFolderKind.Downloads,same[0].Path+"\\");int repeatedOpens=0;
        var repeated=CommonDuplicates.Scan(same,c=>{repeatedOpens++;return new Root();},CancellationToken.None);
        check(repeatedOpens==1&&repeated.Roots[1].Issue==CommonDuplicates.RepeatedRoot&&repeated.Roots[1].EntryQuota==0,"One trailing separator normalization permits exact repeated-root skip without reopen");
        var variants=Choices(2);variants[1]=CommonFolderChoice.FromReported(CommonFolderKind.Downloads,variants[0].Path!.ToLowerInvariant());int variantOpens=0;
        _=CommonDuplicates.Scan(variants,c=>{variantOpens++;return new Root();},CancellationToken.None);check(variantOpens==2,"Case variants retain separate root attempts for case-sensitive directories");
        foreach(bool changed in new[]{false,true})
        {
            var alias=CommonDuplicates.Scan(Choices(2),c=>new Root(new[]{new FolderEntry("f.txt",false,4,0x20)},candidate=>new Input(new byte[]{1,2,3,4},new("same-full-volume-and-file-id",4,changed&&(int)c.Kind==1?2:1))),CancellationToken.None);
            check(alias.Duplicates.Groups.Count==0&&alias.Duplicates.Issues.Count==(changed?2:1),"Overlapping roots/hardlinks never become distinct copies; changed identity stamp invalidates previous sample");check(alias.Duplicates.Issues.All(i=>i.Reason==(changed?"File changed during comparison":"Same file identity (hard link)")),"Original shared identity and changed-file reasons retained");
        }
        var failure=CommonDuplicates.Scan(Choices(3),c=>c.Kind==CommonFolderKind.Desktop?throw new System.ComponentModel.Win32Exception(5):new Root(),CancellationToken.None);
        check(failure.Roots[0].Error==5&&failure.Roots[0].Issue==CommonDuplicates.Unavailable&&failure.Roots.Skip(1).All(r=>r.Scan is not null),"Expected root failure visible without hiding later roots or raw native code");
        var noEntries=CommonDuplicates.Scan(Choices(3),c=>new Root(new[]{new FolderEntry("a",false,1,0x20)}),CancellationToken.None,new(MetadataEntries:1));check(noEntries.Roots[0].Scan!.Entries==1&&noEntries.Roots.Skip(1).All(r=>r.Issue==CommonDuplicates.NoEntries),"Exhausted shared entry budget leaves unattempted roots visible");
        int failedEntries=0;int[] failedQuotas={1,1,2,2,2,2};var failedScans=CommonDuplicates.Scan(Choices(6),c=>new Root(Enumerable.Range(0,failedQuotas[(int)c.Kind]).Select(i=>{failedEntries++;return new FolderEntry(i+".txt",false,4,0x20);}).ToArray(),failAfterEntries:true),CancellationToken.None,new(MetadataEntries:10));
        check(failedEntries==10&&failedScans.Roots.Select(r=>r.EntryQuota).SequenceEqual(failedQuotas)&&failedScans.Roots.All(r=>r.Issue==CommonDuplicates.Unavailable),"Escaped expected metadata failure conservatively spends reserved quota rather than resetting shared budget");
        double elapsed=0;int timeOpens=0;var noTime=CommonDuplicates.Scan(Choices(3),c=>{timeOpens++;elapsed=30;return new Root();},CancellationToken.None,metadataElapsed:()=>TimeSpan.FromSeconds(elapsed));check(timeOpens==1&&noTime.Roots[0].Scan!.TimeLimit&&noTime.Roots.Skip(1).All(r=>r.Issue==CommonDuplicates.NoTime),"Slow root opening consumes shared soft clock; later roots not opened");
        var clock=CommonDuplicates.Scan(Choices(3),c=>new Root(),CancellationToken.None,metadataElapsed:()=>TimeSpan.FromSeconds(6));check(clock.Roots.Select(r=>r.DeadlineSeconds).SequenceEqual(new[]{14,18,30}),"Each root receives deadline from the same global clock, not a fresh 30 seconds");
        var longChoices=Choices(2);longChoices[0]=CommonFolderChoice.FromReported(CommonFolderKind.Desktop,"C:\\"+new string('r',230));
        var longPath=CommonDuplicates.Scan(longChoices,c=>new Root(new[]{new FolderEntry(new string('x',240),false,4,0x20)},candidate=>throw new Exception("Invalid opaque path must not be opened.")),CancellationToken.None);
        check(longPath.Sources.Count==2&&longPath.Duplicates.Issues.Count==2&&longPath.Duplicates.Issues.All(i=>i.Reason=="System, link, cloud or invalid item"),"Overlong opaque candidates stay visible as issues without opening or aborting report");
        var shortChoices=Choices(2);int fullPathRefused=0;var longFull=CommonDuplicates.Scan(shortChoices,c=>new Root(new[]{new FolderEntry(new string('y',235),false,4,0x20)},candidate=>{fullPathRefused++;throw new UserError("Only ordinary local files are supported.");}),CancellationToken.None);check(fullPathRefused==2&&longFull.Duplicates.Issues.Count==2,"Native full-path refusal is per-file even when opaque key fits 240");
        int before=0;reject(()=>CommonDuplicates.Scan(Array.Empty<CommonFolderChoice>(),c=>{before++;return new Root();},CancellationToken.None),"Empty selection refused");reject(()=>CommonDuplicates.Scan(new[]{selected[0],selected[0]},c=>{before++;return new Root();},CancellationToken.None),"Repeated kind refused");reject(()=>CommonDuplicates.Scan(new[]{CommonFolderChoice.FromReported(CommonFolderKind.Desktop,"\\\\server\\share")},c=>{before++;return new Root();},CancellationToken.None),"Invalid reported path refused");
        foreach(var invalid in new[]{new CommonDuplicateLimits(0),new CommonDuplicateLimits(20001),new CommonDuplicateLimits(Depth:5),new CommonDuplicateLimits(MetadataSeconds:31),new CommonDuplicateLimits(Content:new(MaxReadBytes:2147483649))})reject(()=>CommonDuplicates.Scan(Choices(2),c=>{before++;return new Root();},CancellationToken.None,invalid),"All global budget increases refused before I/O");reject(()=>CommonDuplicates.Scan(Choices(2),c=>{before++;return new Root();},new CancellationToken(true)),"Precancel before root open");check(before==0,"Invalid or precancelled requests open no root");
        using(var cancel=new CancellationTokenSource())
        {
            int closed=0;reject(()=>CommonDuplicates.Scan(Choices(2),c=>{cancel.Cancel();return new Root(dispose:()=>closed++);},cancel.Token),"Cancellation after root open still owns new lease");check(closed==1,"Root opened during cancellation released");
        }
        using(var cancel=new CancellationTokenSource())
        {
            int closed=0;reject(()=>CommonDuplicates.Scan(Choices(2),c=>new Root(new[]{new FolderEntry("a",false,4,0x20)},candidate=>new Input(new byte[]{1,2,3,4},new(c.Kind.ToString(),4,1),onRead:()=>cancel.Cancel()),()=>closed++),cancel.Token),"Cancellation inside content read propagates after cleanup");check(closed==2,"Cancellation releases all held roots");
        }
        int releaseAttempts=0;reject(()=>CommonDuplicates.Scan(Choices(6),c=>new Root(dispose:()=>{releaseAttempts++;if((int)c.Kind%2==0)throw new IOException("release failure");}),CancellationToken.None),"Cleanup failure surfaced after all release attempts");check(releaseAttempts==6,"One throwing root disposal does not strand other root leases");
        var prefs=new Prefs();var action=Catalog.Actions.Single(a=>a.Id=="doctor.commonduplicates");check(!action.RequiresFiles&&action.Feature==Feature.Doctor&&!Catalog.Available(prefs,Array.Empty<string>()).Contains(action),"Cross-root action default off, independent of selected input files");prefs.Set(Feature.Doctor,true);check(Catalog.Available(prefs,Array.Empty<string>()).Contains(action)&&WindowsDoctor.Areas.Single(a=>a.Id=="duplicates").ActionIds.Contains(action.Id),"Stable action reachable from Windows Doctor Duplicate area");
    }
    private sealed class Root(FolderEntry[]? entries=null,Func<DuplicateCandidate,IDuplicateInput>? input=null,Action? dispose=null,bool failAfterEntries=false):ICommonDuplicateRoot
    {
        public string Name=>"root";public IEnumerable<FolderEntry> Entries(CancellationToken token){foreach(var entry in entries??Array.Empty<FolderEntry>()){token.ThrowIfCancellationRequested();yield return entry;}if(failAfterEntries)throw new System.Security.SecurityException("enumeration refused");}
        public IFolderMetadata OpenChild(string name)=>throw new IOException();public IDuplicateInput OpenDuplicate(DuplicateCandidate candidate)=>input is null?throw new IOException():input(candidate);public void Dispose()=>dispose?.Invoke();
    }
    private sealed class Input:IDuplicateInput
    {
        public Stream Stream{get;}public DuplicateStamp Stamp{get;}private readonly Action? dispose;
        internal Input(byte[] content,DuplicateStamp stamp,Action? dispose=null,Action? onRead=null){Stream=new Data(content,onRead);Stamp=stamp;this.dispose=dispose;}
        public DuplicateStamp Refresh()=>Stamp;public void Dispose(){Stream.Dispose();dispose?.Invoke();}
    }
    private sealed class Data(byte[] data,Action? onRead):MemoryStream(data,false){public override int Read(byte[] buffer,int offset,int count){onRead?.Invoke();return base.Read(buffer,offset,count);}}
}
