using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Threading;
using Swivel.Core;

internal static class CommonSimilarityChecks
{
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        CommonFolderChoice[] Choices(int count)=>CommonFolders.Kinds.Take(count).Select(k=>CommonFolderChoice.FromReported(k,"C:\\"+k)).ToArray();
        byte[] text=Encoding.UTF8.GetBytes("private-one\nprivate-two\nprivate-three\nprivate-four\nprivate-five");
        foreach(var kind in Enum.GetValues<CommonSimilarityKind>())
        {
            string extension=kind==CommonSimilarityKind.Text?".txt":".png";byte[] content=kind==CommonSimilarityKind.Text?text:new byte[]{5,9,7,3};
            CommonSimilarityResult Scan(CommonFolderChoice[] choices,Func<CommonFolderChoice,ICommonDuplicateRoot> open,CancellationToken token=default,CommonSimilarityLimits? metadata=null,int files=200,int bytes=0,int pairs=1000,Func<TimeSpan>? metadataClock=null,Func<TimeSpan>? contentClock=null)
                =>kind==CommonSimilarityKind.Text?CommonSimilarity.ScanText(choices,open,token,metadata,new(MaxFiles:files,MaxReadBytes:bytes==0?16*1024*1024:bytes,MaxPairs:pairs),metadataClock,contentClock)
                    :CommonSimilarity.ScanImages(choices,open,(data,_,c,checkpoint)=>{c.ThrowIfCancellationRequested();checkpoint();check(data.SequenceEqual(content),"Whole image snapshots reach one shared fingerprint pass");return 0x0f0f0f0f0f0f0f0f;},token,metadata,new(MaxFiles:files,MaxReadBytes:bytes==0?128*1024*1024:bytes,MaxPairs:pairs),metadataClock,contentClock);
            IReadOnlyList<DuplicateIssue> Issues(CommonSimilarityResult r)=>r.Text?.Issues??r.Images!.Issues;
            int Loaded(CommonSimilarityResult r)=>r.Text?.Loaded??r.Images!.Loaded;
            int Attempts(CommonSimilarityResult r)=>r.Text?.Attempted??r.Images!.Attempted;
            int Matches(CommonSimilarityResult r)=>r.Text?.MatchingPairs??r.Images!.MatchingPairs;
            int Pairs(CommonSimilarityResult r)=>r.Text?.Pairs.Count??r.Images!.Pairs.Count;
            long Bytes(CommonSimilarityResult r)=>r.Text?.BytesRead??r.Images!.BytesRead;
            FolderEntry File(string name)=>new(name+extension,false,content.Length,0x20);
            int live=0,closed=0,inputs=0,maxInputs=0;var selected=Choices(6);
            var result=Scan(selected,choice=>{live++;return new Root(new[]{File("same")},candidate=>{
                check(live==6&&candidate.RelativePath=="same"+extension,"All six roots remain held and opaque keys never reach native I/O");inputs++;maxInputs=Math.Max(maxInputs,inputs);
                return new Input(content,new("volume-and-file-"+choice.Kind,content.Length,1),()=>inputs--);
            },()=>{live--;closed++;});});
            check(result.Kind==kind&&result.Roots.Count==6&&Loaded(result)==6&&Matches(result)==15&&Pairs(result)==15&&Bytes(result)==6L*content.Length,"One shared cross-root pass compares all distinct pairs without rereading per root");
            check(live==0&&closed==6&&inputs==0&&maxInputs==1,"Return occurs after every root/data lease cleanup, at most one data input at a time");
            check(result.Sources.Count==6&&result.Sources.Values.All(s=>s.RelativePath=="same"+extension),"Opaque source routing records original relative paths");
            reject(()=>((IList<CommonDuplicateRootResult>)result.Roots).Clear(),"Common similarity root list immutable");reject(()=>((IDictionary<string,CommonDuplicateFileRef>)result.Sources).Clear(),"Common similarity source map immutable");
            var limited=Scan(selected,choice=>new Root(Enumerable.Range(0,5).Select(i=>File(i.ToString())).ToArray(),c=>new Input(content,new(choice.Kind+c.RelativePath,content.Length,1))),metadata:new(6),bytes:content.Length*2);
            check(limited.Roots.Sum(r=>r.Scan!.Entries)==6&&limited.Roots.All(r=>r.EntryQuota==1&&r.Scan!.EntryLimit),"Shared metadata quota is divided across six roots, not multiplied");
            check(Loaded(limited)==2&&Bytes(limited)==content.Length*2&&Issues(limited).Count==4,"Shared content byte budget covers all roots once");
            var attempt=Scan(selected,c=>new Root(new[]{File("same")},_=>new Input(content,new(c.Kind.ToString(),content.Length,1))),files:2);
            check(Attempts(attempt)==2&&Loaded(attempt)==2&&Issues(attempt).Count==4&&Issues(attempt).All(i=>i.Reason.Contains("attempt limit",StringComparison.Ordinal)),"File attempt budget is shared across roots");
            var pairLimited=Scan(selected,c=>new Root(new[]{File("same")},_=>new Input(content,new(c.Kind.ToString(),content.Length,1))),pairs:1);
            check(Matches(pairLimited)==15&&Pairs(pairLimited)==1,"Shared pair display limit retains full completed match count");
            foreach(bool changed in new[]{false,true})
            {
                var alias=Scan(Choices(2),c=>new Root(new[]{File("same")},_=>new Input(content,new("same-full-volume-file-id",content.Length,changed&&c.Kind==CommonFolderKind.Downloads?2:1))));
                check(Pairs(alias)==0&&Loaded(alias)==(changed?0:1)&&Issues(alias).Count==(changed?2:1),"Cross-root hardlink identity deduplicates; later changed stamp invalidates earlier signature");
                check(Issues(alias).All(i=>i.Reason==(changed?"File changed during comparison":"Same file identity (hard link)")),"Cross-root identity reasons stay visible");
            }
            var repeatedChoices=Choices(3);repeatedChoices[1]=CommonFolderChoice.FromReported(CommonFolderKind.Downloads,repeatedChoices[0].Path+"\\");int opened=0;
            var repeated=Scan(repeatedChoices,c=>{opened++;return new Root();});check(opened==2&&repeated.Roots[1].Issue==CommonDuplicates.RepeatedRoot,"Exact repeated roots skipped visibly without opening again");
            var variants=Choices(2);variants[1]=CommonFolderChoice.FromReported(CommonFolderKind.Downloads,variants[0].Path!.ToLowerInvariant());opened=0;_ = Scan(variants,c=>{opened++;return new Root();});check(opened==2,"Case-variant roots preserve native identity check instead of lexical conflation");
            var failures=Scan(Choices(3),c=>c.Kind==CommonFolderKind.Desktop?throw new Win32Exception(5):new Root());check(failures.Roots[0].Error==5&&failures.Roots[0].Issue==CommonDuplicates.Unavailable&&failures.Roots.Skip(1).All(r=>r.Scan is not null),"One failed root retains error and later roots are still attempted");
            var allFail=Scan(selected,_=>throw new IOException("fixture"));check(allFail.Roots.Count==6&&allFail.Roots.All(r=>r.Issue==CommonDuplicates.Unavailable)&&Loaded(allFail)==0,"All roots failing remains a complete six-root result, not an empty success");
            var emptyBudget=Scan(Choices(3),_=>new Root(new[]{File("same")}),metadata:new(1));check(emptyBudget.Roots[0].Scan!.Entries==1&&emptyBudget.Roots.Skip(1).All(r=>r.Issue==CommonDuplicates.NoEntries),"Entry-exhausted roots remain explicitly unattempted");
            int enumerated=0;int[] quotas={1,1,2,2,2,2};var failedScans=Scan(selected,c=>new Root(Enumerable.Range(0,quotas[(int)c.Kind]).Select(i=>{enumerated++;return File(i.ToString());}).ToArray(),failAfter:true),metadata:new(10));
            check(enumerated==10&&failedScans.Roots.Select(r=>r.EntryQuota).SequenceEqual(quotas)&&failedScans.Roots.All(r=>r.Issue==CommonDuplicates.Unavailable),"Escaped metadata failure spends reserved quota and cannot multiply entry budget");
            double elapsed=0;opened=0;var noTime=Scan(Choices(3),_=>{opened++;elapsed=30;return new Root();},metadataClock:()=>TimeSpan.FromSeconds(elapsed));check(opened==1&&noTime.Roots[0].Scan!.TimeLimit&&noTime.Roots.Skip(1).All(r=>r.Issue==CommonDuplicates.NoTime),"Root opening consumes shared metadata clock and later roots remain visible");
            var clock=Scan(Choices(3),_=>new Root(),metadataClock:()=>TimeSpan.FromSeconds(6));check(clock.Roots.Select(r=>r.DeadlineSeconds).SequenceEqual(new[]{14,18,30}),"All deadlines refer to one metadata clock");
            elapsed=0;var stopped=Scan(selected,c=>new Root(new[]{File("same")},_=>new Input(content,new(c.Kind.ToString(),content.Length,1),onRead:()=>elapsed=60)),contentClock:()=>TimeSpan.FromSeconds(elapsed));
            check(Loaded(stopped)==0&&Issues(stopped).Count==6&&(stopped.Text?.TimeLimit??stopped.Images!.TimeLimit),"One content clock stops every root and partial reads cannot become matches");
            using(var cancel=new CancellationTokenSource())
            {
                closed=0;reject(()=>Scan(Choices(2),c=>{cancel.Cancel();return new Root(dispose:()=>closed++);},cancel.Token),"Cancellation after opening owns and releases just-opened root");check(closed==1,"Cancelled root open cleanup completes");
            }
            using(var cancel=new CancellationTokenSource())
            {
                closed=0;reject(()=>Scan(selected,c=>new Root(new[]{File("same")},_=>new Input(content,new(c.Kind.ToString(),content.Length,1),onRead:()=>cancel.Cancel()),()=>closed++),cancel.Token),"Content cancellation propagates after root/data cleanup");check(closed==6,"Cancellation releases every held root");
            }
            closed=0;reject(()=>Scan(selected,_=>new Root(dispose:()=>{closed++;if(closed%2==0)throw new IOException("dispose");})),"Cleanup failure surfaced only after every release attempt");check(closed==6,"One failed Dispose does not strand another root");
            opened=0;foreach(var invalid in new[]{new CommonSimilarityLimits(0),new CommonSimilarityLimits(20001),new CommonSimilarityLimits(Depth:5),new CommonSimilarityLimits(MetadataSeconds:31)})reject(()=>Scan(Choices(2),_=>{opened++;return new Root();},metadata:invalid),"Increased metadata budgets refused before I/O");
            reject(()=>Scan(Array.Empty<CommonFolderChoice>(),_=>{opened++;return new Root();}),"Empty selection refused");reject(()=>Scan(new[]{selected[0],selected[0]},_=>{opened++;return new Root();}),"Repeated root kind refused");reject(()=>Scan(selected,_=>{opened++;return new Root();},new CancellationToken(true)),"Pre-cancelled comparison opens no root");reject(()=>Scan(selected,_=>{opened++;return new Root();},files:201),"Content attempt increase rejected before opening any root");check(opened==0,"All invalid selections/budgets/pre-cancel perform no root I/O");
        }
    }
    private sealed class Root(FolderEntry[]? entries=null,Func<DuplicateCandidate,IDuplicateInput>? input=null,Action? dispose=null,bool failAfter=false):ICommonDuplicateRoot
    {
        public string Name=>"root";public IEnumerable<FolderEntry> Entries(CancellationToken token){foreach(var entry in entries??Array.Empty<FolderEntry>()){token.ThrowIfCancellationRequested();yield return entry;}if(failAfter)throw new SecurityException("fixture");}
        public IFolderMetadata OpenChild(string name)=>throw new IOException();public IDuplicateInput OpenDuplicate(DuplicateCandidate candidate)=>input is null?throw new IOException():input(candidate);public void Dispose()=>dispose?.Invoke();
    }
    private sealed class Input:IDuplicateInput
    {
        public Stream Stream{get;}public DuplicateStamp Stamp{get;}private readonly Action? cleanup;
        internal Input(byte[] content,DuplicateStamp stamp,Action? dispose=null,Action? onRead=null){Stream=new Data(content,onRead);Stamp=stamp;cleanup=dispose;}
        public DuplicateStamp Refresh()=>Stamp;public void Dispose(){Stream.Dispose();cleanup?.Invoke();}
    }
    private sealed class Data(byte[] bytes,Action? onRead):MemoryStream(bytes,false){public override int Read(byte[] buffer,int offset,int count){onRead?.Invoke();return base.Read(buffer,offset,count);}}
}
