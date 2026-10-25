using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Swivel.Core;

internal static class TextSimilarityChecks
{
    public static void Run(Action<bool,string> check, Action<Action,string> reject)
    {
        var source = new Source();
        TextSimilarityResult Run(Dictionary<string,byte[]> data, TextSimilarityLimits? limits = null)
        { source = new Source(data); return TextSimilarity.Scan(data.Select(x => new DuplicateCandidate(x.Key,x.Value.Length)).Reverse().ToArray(),source.Open,default,limits); }
        byte[] Text(string value) => new UTF8Encoding(false,true).GetBytes(value);
        string Five = "one\ntwo\nthree\nfour\nfive";
        var scan = Run(new() { ["a.txt"] = Text(Five), ["b.TXT"] = Text(" five \r\n\tthree\t\rone\ntwo\rfour\nfour\n\u3000\n") });
        check(scan.Pairs.Count==1 && scan.Pairs[0]==new SimilarTextPair("a.txt","b.TXT",5,5) && scan.Pairs[0].Percent==100 && scan.Loaded==2 && scan.ComparedPairs==1,"Ordinal lines trim Unicode whitespace, ignore order/repetition/blank rows, split CRLF/CR/LF; extension case ignored");
        check(scan.BytesRead==source.Bytes.Values.Sum(x=>x.Length) && source.Active==0 && source.Opened==source.Disposed && source.MaxActive==1,"One exact bounded read per file; at most one held input and every lease disposed");
        scan=Run(new(){["a.md"]=Text(Five),["b.markdown"]=Text(Five),["c.txt"]=Text(Five),["d.TXT"]=Text(Five.ToUpperInvariant()),["e.bin"]=Text(Five)});
        check(scan.Pairs.Count==0 && scan.Loaded==4 && scan.ComparedPairs==1 && source.Opened==4 && scan.Issues.Single().Reason=="Not a supported plain-text extension","Extension aliases remain separate, text case is significant, unsupported extension never opens");
        string Lines(int n)=>string.Join('\n',Enumerable.Range(0,n).Select(i=>"line "+i));
        scan=Run(new(){["a.txt"]=Text(Lines(8)),["b.txt"]=Text(Lines(10)),["c.txt"]=Text(Lines(11))});
        check(scan.Pairs.Any(p=>p.First=="a.txt"&&p.Second=="b.txt"&&p.Percent==80) && !scan.Pairs.Any(p=>p.First=="a.txt"&&p.Second=="c.txt"),"Exact 80 percent qualifies and just-below never rounds into a match");
        check(new SimilarTextPair("a","b",19999,20000).Percent==99.99m,"Near equality is truncated, never mislabeled 100 percent");
        scan=Run(new(){["a.txt"]=Text("a\nb\nc\nd\na\na"),["b.txt"]=Text(Five),["empty.txt"]=Array.Empty<byte>(),["bad.txt"]=new byte[]{0xc0,0xaf},["control.txt"]=new byte[]{0,0},["big.txt"]=new byte[1024*1024+1]});
        check(scan.Loaded==1 && scan.Pairs.Count==0 && scan.Issues.Count==5 && source.Opened==4,"Too few distinct lines, empty/oversize and invalid encodings/controls never enter pair comparison");
        foreach(var encoding in new Encoding[]{new UTF8Encoding(true,true),new UnicodeEncoding(false,true,true),new UnicodeEncoding(true,true,true)})
        {
            scan=Run(new(){["a.txt"]=Text(Five),["b.txt"]=encoding.GetPreamble().Concat(encoding.GetBytes(Five)).ToArray()});
            check(scan.Pairs.Count==1 && scan.Pairs[0].Percent==100,"Strict BOM decode equivalent: "+encoding.WebName);
        }
        foreach(byte[] bytes in new[]{new byte[]{0xff,0xfe,0,0,65,0,0,0},new byte[]{0xfe,0xff,0xd8,0},new byte[]{0xff,0xfe,65}})
        {scan=Run(new(){["a.txt"]=bytes});check(scan.Loaded==0 && scan.Issues.Single().Reason=="Invalid UTF-8 or BOM-marked UTF-16 plain text","UTF32/invalid surrogate/odd UTF16 refused, no replacement");}
        scan=Run(new(){["a.txt"]=Text(Five),["b.txt"]=Text(Five),["c.txt"]=Text(Five)},new(MaxFiles:2));
        check(scan.FileLimit && scan.Attempted==2 && scan.Loaded==2 && scan.Pairs.Count==1 && source.Opened==2,"File attempt cap includes path-ordered first candidates");
        int length=Text(Five).Length;
        scan=Run(new(){["a.txt"]=Text(Five),["b.txt"]=Text(Five)},new(MaxReadBytes:2*length));
        check(!scan.ByteLimit && scan.BytesRead==2*length && scan.Pairs.Count==1,"Exact byte budget is inclusive");
        scan=Run(new(){["a.txt"]=Text(Five),["b.txt"]=Text(Five)},new(MaxReadBytes:2*length-1));
        check(scan.ByteLimit && source.Opened==1 && scan.BytesRead==length && scan.Issues.Single().Reason=="Text read budget limit","Would exceed read budget skipped before opening, no partial source admitted");
        scan=Run(new(){["a.txt"]=Text(Five),["b.txt"]=Text(Five)},new(MaxRetainedLines:9));
        check(scan.LineLimit && scan.Loaded==1 && scan.Pairs.Count==0 && scan.Issues.Single().Reason=="Retained text line budget limit","Whole-file set acceptance at global retained-line budget, no partial second set");
        scan=Run(new(){["a.txt"]=Text(Five),["b.txt"]=Text(Five)},new(MaxRetainedLines:10,MaxLines:5));
        check(scan.Loaded==2 && scan.Pairs.Count==1 && !scan.LineLimit,"Exact retained and file-line boundaries inclusive");
        scan=Run(new(){["a.txt"]=Text(Five+"\n")},new(MaxLines:5));
        check(scan.Loaded==0 && scan.Issues.Single().Reason=="Text line limit","Trailing separator starts an empty logical line and respects whole-file row cap");
        var many=Enumerable.Range(0,50).ToDictionary(i=>$"f{i:000}.txt",i=>Text(Five));scan=Run(many);
        check(scan.Pairs.Count==1000 && scan.MatchingPairs==1225 && scan.ComparedPairs==1225 && scan.Pairs[0].First=="f000.txt" && scan.Pairs[0].Second=="f001.txt","Pair cap retains deterministic path-order prefix with complete matching count");
        // Compare the capped sequence against an independently enumerated pair list.
        var names=many.Keys.Order(StringComparer.Ordinal).ToArray();var all=(from a in Enumerable.Range(0,names.Length) from b in Enumerable.Range(a+1,names.Length-a-1) select (names[a],names[b])).ToArray();
        check(scan.Pairs.Select(p=>(p.First,p.Second)).SequenceEqual(all.Take(1000)),"1000-pair prefix equals independently enumerated ordinal pairs");
        foreach(string kind in new[]{"short","eof","seek","stamp","dispose","throw","invalid-read"})
        {
            source=new Source(new(){["a.txt"]=Text(Five),["b.txt"]=Text(Five)});source.Kind=kind;
            scan=TextSimilarity.Scan(source.Candidates,source.Open,default);
            check(source.Active==0 && source.Opened==source.Disposed,"Failure/short-read releases source: "+kind);
            check(kind=="short"?scan.Pairs.Count==1:scan.Pairs.Count==0 && scan.Issues.Count==2,"Invalid reads/stamps/disposal never leave a match: "+kind);
            check(scan.BytesRead<=2*length,"Read reservation cannot exceed full candidate lengths: "+kind);
        }
        source=new Source(new(){["a.txt"]=Text(Five),["b.txt"]=Text(Five),["c.txt"]=Text(Five)}){SameIdentity=true};
        scan=TextSimilarity.Scan(source.Candidates,source.Open,default);
        check(scan.Loaded==1 && scan.Pairs.Count==0 && scan.Issues.Count==2 && scan.BytesRead==length,"Hardlinks read once and never create independent matching pair");
        source=new Source(new(){["a.txt"]=Text(Five),["b.txt"]=Text(Five)}){SameIdentity=true,ChangedAlias=true};
        scan=TextSimilarity.Scan(source.Candidates,source.Open,default);
        check(scan.Loaded==0 && scan.Issues.Count==2 && scan.Issues.All(i=>i.Reason=="File changed during comparison"),"Later changed identity invalidates earlier retained set");
        var invalid=new[]{new DuplicateCandidate("..\\a.txt",length),new("C:\\a.txt",length),new("a.txt:ads",length),new("a/b.txt",length),new("CON.txt",length)};
        int opens=0;scan=TextSimilarity.Scan(invalid,c=>{opens++;throw new Exception();},default);
        check(opens==0 && scan.Issues.Count==invalid.Length,"Invalid local relative paths never reach opener");
        source=new Source(new(){["a.txt"]=Text(Five)});var dup=source.Candidates.Concat(new[]{new DuplicateCandidate("A.TXT",length)}).ToArray();scan=TextSimilarity.Scan(dup,source.Open,default);
        check(source.Opened==1&&scan.Issues.Single().Reason=="Repeated file path","Case variants are one Windows candidate path");
        source=new Source(new(){["a.txt"]=Text(Five)});reject(()=>TextSimilarity.Scan(source.Candidates,source.Open,new CancellationToken(true)),"Pre-cancel rejects before content open");check(source.Opened==0,"No source read on pre-cancel");
        using(var cancel=new CancellationTokenSource())
        {source=new Source(new(){["a.txt"]=Text(Five)});source.OnRead=()=>cancel.Cancel();reject(()=>TextSimilarity.Scan(source.Candidates,source.Open,cancel.Token),"Cancel during read propagates");check(source.Active==0,"Cancel disposes held source");}
        source=new Source(new(){["a.txt"]=Text(Five)});scan=TextSimilarity.Scan(source.Candidates,source.Open,default,elapsed:()=>TimeSpan.FromSeconds(60));
        check(scan.TimeLimit && source.Opened==0 && scan.Issues.Single().Reason=="Text comparison time limit","Time limit before open is visible without native read");
        source=new Source(new(){["a.txt"]=Text(Five),["b.txt"]=Text(Five)});
        scan=TextSimilarity.Scan(source.Candidates,source.Open,default,elapsed:()=>source.Disposed==2?TimeSpan.FromSeconds(60):TimeSpan.Zero);
        check(scan.Loaded==2 && scan.TimeLimit && scan.ComparedPairs==0 && scan.Pairs.Count==0,"Time limit also covers pair phase without claiming completed comparisons");
        foreach(var limits in new[]{new TextSimilarityLimits(MaxFiles:201),new(MaxFileBytes:1048577),new(MaxReadBytes:16777217),new(MaxLines:4),new(MaxRetainedLines:200001),new(MaxPairs:1001),new(Seconds:61)})reject(()=>TextSimilarity.Scan(Array.Empty<DuplicateCandidate>(),c=>throw new Exception(),default,limits),"Reject unbounded configuration");
        var random=new Random(40);
        for(int n=0;n<100;n++)
        {
            var a=Enumerable.Range(0,15).Where(_=>random.Next(3)>0).Select(i=>"r"+i).ToHashSet(StringComparer.Ordinal);
            var b=Enumerable.Range(0,15).Where(_=>random.Next(3)>0).Select(i=>"r"+i).ToHashSet(StringComparer.Ordinal);
            scan=Run(new(){["a.txt"]=Text(string.Join('\n',a)),["b.txt"]=Text(string.Join('\n',b))});
            int common=a.Intersect(b,StringComparer.Ordinal).Count(),union=a.Union(b,StringComparer.Ordinal).Count();bool expected=a.Count>=5&&b.Count>=5&&common*5>=union*4;
            check(scan.Pairs.Count==(expected?1:0),"Random independent line-set membership and exact integer Jaccard oracle "+n);
            if(expected)check(scan.Pairs[0].CommonLines==common&&scan.Pairs[0].UnionLines==union,"Random shared/union counts "+n);
        }
        var prefs=new Prefs();check(!Catalog.Offered(prefs).Any(a=>a.Id=="doctor.textsimilar"),"Text similarity unavailable while Doctor is off");prefs.Set(Feature.Doctor,true);
        check(Catalog.Offered(prefs).Any(a=>a.Id=="doctor.textsimilar"),"Stable no-file action offered with Doctor opt-in");
        prefs.SetCustomActions(new[]{"doctor.textsimilar","doctor.duplicates"});var restored=Prefs.Parse(prefs.Serialize());
        check(restored.CustomActionIds.SequenceEqual(prefs.CustomActionIds)&&restored.IsOn(Feature.Doctor),"Stable text action persists through My Wheel settings without losing existing selections");
        prefs.Set(Feature.Doctor,false);check(!Catalog.Offered(prefs).Any(a=>a.Id=="doctor.textsimilar")&&prefs.CustomActionIds.Contains("doctor.textsimilar"),"Feature off hides new action but preserves configured menu ID");
    }
    private sealed class Source
    {
        public Dictionary<string,byte[]> Bytes=new(); public string Kind=""; public bool SameIdentity,ChangedAlias;public Action? OnRead;
        public int Opened,Disposed,Active,MaxActive;
        public Source(){} public Source(Dictionary<string,byte[]> bytes){Bytes=bytes;}
        public DuplicateCandidate[] Candidates=>Bytes.Select(x=>new DuplicateCandidate(x.Key,x.Value.Length)).ToArray();
        public IDuplicateInput Open(DuplicateCandidate candidate)
        {Opened++;Active++;MaxActive=Math.Max(MaxActive,Active);return new Input(this,candidate);}
        private sealed class Input:IDuplicateInput
        {
            readonly Source source;readonly Probe stream;int refresh;bool disposed;
            public Input(Source source,DuplicateCandidate c){this.source=source;stream=new Probe(source,source.Bytes.First(x=>x.Key.Equals(c.RelativePath,StringComparison.OrdinalIgnoreCase)).Value);Stamp=new(source.SameIdentity?"same":c.RelativePath,c.Length,source.ChangedAlias&&c.RelativePath=="b.txt"?2:1);}
            public Stream Stream=>stream;public DuplicateStamp Stamp{get;}
            public DuplicateStamp Refresh()=>source.Kind=="stamp"&&++refresh>=2?Stamp with{LastWrite=7}:Stamp;
            public void Dispose(){if(disposed)return;disposed=true;stream.Dispose();source.Active--;source.Disposed++;if(source.Kind=="dispose")throw new IOException();}
        }
        private sealed class Probe(Source source,byte[] bytes):MemoryStream(bytes,false)
        {
            public override int Read(byte[] buffer,int offset,int count)
            {source.OnRead?.Invoke();if(source.Kind=="throw")throw new IOException();if(source.Kind=="invalid-read")return count+1;if(source.Kind=="eof")return 0;return base.Read(buffer,offset,source.Kind=="short"?Math.Min(count,3):count);}
            public override long Seek(long offset,SeekOrigin origin)=>source.Kind=="seek"?offset+1:base.Seek(offset,origin);
        }
    }
}
