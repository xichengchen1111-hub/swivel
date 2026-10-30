using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using Swivel.Core;

internal static class PhotoSimilarityChecks
{
    public static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        PixelImage Pixels(int width,int height,Func<int,int,(byte R,byte G,byte B,byte A)> color)
        {
            var bytes=new byte[width*height*4];for(int y=0;y<height;y++)for(int x=0;x<width;x++){var c=color(x,y);int at=(y*width+x)*4;bytes[at]=c.B;bytes[at+1]=c.G;bytes[at+2]=c.R;bytes[at+3]=c.A;}return new(width,height,bytes);
        }
        var checker=Pixels(9,8,(x,y)=>(x%2==0?(byte)220:(byte)20,x%2==0?(byte)220:(byte)20,x%2==0?(byte)220:(byte)20,(byte)255));
        ulong expected=0x5555555555555555UL;
        check(PhotoSimilarity.Fingerprint(checker,default)==expected,"9x8 grid exact horizontal > comparison uses row-major least-significant bits and equality as zero");
        var block=Pixels(27,24,(x,y)=>{byte v=(x/3)%2==0?(byte)220:(byte)20;return(v,v,v,(byte)255);});
        check(PhotoSimilarity.Fingerprint(block,default)==expected,"Proportional integer cell areas preserve expanded source signature");
        check(PhotoSimilarity.Fingerprint(Pixels(8,8,(_,_)=>((byte)0,(byte)0,(byte)0,(byte)255)),default) is null && PhotoSimilarity.Fingerprint(Pixels(9,7,(_,_)=>((byte)0,(byte)0,(byte)0,(byte)255)),default) is null,"Each grid cell must contain pixels: dimensions below9x8 refused");
        foreach(byte a in new byte[]{0,1,128,254,255})
        {
            var transparent=Pixels(9,8,(x,y)=>(x%2==0?(byte)220:(byte)20,x%2==0?(byte)220:(byte)20,x%2==0?(byte)220:(byte)20,a));
            var white=Pixels(9,8,(x,y)=>{int c=x%2==0?220:20;byte v=(byte)((c*a+255*(255-a)+127)/255);return(v,v,v,(byte)255);});
            check(PhotoSimilarity.Fingerprint(transparent,default)==PhotoSimilarity.Fingerprint(white,default),"Alpha compositing has exact independent integer white-background equivalence: "+a);
        }
        var hidden=Pixels(9,8,(x,y)=>((byte)(x*27),(byte)(y*31),(byte)55,(byte)0));
        check(PhotoSimilarity.Fingerprint(hidden,default) is null,"Invisible RGB never creates a signal on transparent image");
        foreach(int range in new[]{15,16})
        {
            var low=Pixels(9,8,(x,y)=>{byte v=(byte)(100+(x%2)*range);return(v,v,v,(byte)255);});
            check((PhotoSimilarity.Fingerprint(low,default) is not null)==(range==16),"Grid brightness range threshold exact16 vs15: "+range);
        }
        foreach(int set in new[]{0,3,4,60,61,64})
        {
            // Build independent rows with a step sequence matching an arbitrary
            // bit vector. Values stay in [20,180], differences are all nonzero.
            ulong bits=set==64?ulong.MaxValue:(1UL<<set)-1;
            var image=Pixels(9,8,(x,y)=>{int v=100;for(int j=0;j<x;j++)v+=((bits>>(y*8+j))&1)!=0?-10:10;return((byte)v,(byte)v,(byte)v,(byte)255);});
            var hash=PhotoSimilarity.Fingerprint(image,default);
            check(set is >=4 and <=60?hash==bits:hash is null,"Directional popcount heuristic boundaries: "+set);
        }
        var shifted=Pixels(9,8,(x,y)=>{byte v=x%2==0?(byte)200:(byte)40;return(v,v,v,(byte)255);});
        check(PhotoSimilarity.Fingerprint(shifted,default)==expected&&!checker.Bgra.SequenceEqual(shifted.Bgra),"Identical signature does not prove pixel identity");
        var original=(byte[])checker.Bgra.Clone();using(var cancel=new CancellationTokenSource()){int steps=0;reject(()=>PhotoSimilarity.Fingerprint(checker,cancel.Token,()=>{if(++steps==3)cancel.Cancel();}),"Cancellation/checkpoint inside pixel visits propagates");check(checker.Bgra.SequenceEqual(original),"Fingerprint never mutates source pixels even on cancellation");}
        check(new SimilarPhotoPair("a","b",0).Percent==100&&new SimilarPhotoPair("a","b",6).Percent==90.62m&&new SimilarPhotoPair("a","b",7).Percent==89.06m&&new SimilarPhotoPair("a","b",64).Percent==0,"Displayed agreement truncation matches exact64 comparison bits");

        var source=new Source(new());PhotoSimilarityResult Run(Dictionary<string,byte[]> bytes,PhotoSimilarityLimits? limits=null,Func<byte[],string,CancellationToken,Action,ulong?>? fingerprint=null)
        {source=new Source(bytes);return PhotoSimilarity.Scan(source.Candidates.Reverse().ToArray(),source.Open,fingerprint??((data,_,_,checkpoint)=>{checkpoint();return BitConverter.ToUInt64(data);}),default,limits);}
        byte[] Hash(ulong value)=>BitConverter.GetBytes(value);
        var scan=Run(new(){["a.png"]=Hash(0),["b.JPG"]=Hash(63),["c.tiff"]=Hash(127),["d.gif"]=Hash(ulong.MaxValue)});
        check(scan.Pairs.Any(p=>p.First=="a.png"&&p.Second=="b.JPG"&&p.DifferentBits==6)&&!scan.Pairs.Any(p=>p.First=="a.png"&&p.Second=="c.tiff")&&scan.ComparedPairs==6,"Cross-format comparison exact6-bit acceptance and7-bit refusal");
        check(scan.BytesRead==32&&source.Active==0&&source.Opened==source.Disposed&&source.MaxActive==1,"Complete source snapshots read once, one held input at a time, all leases closed");
        scan=Run(new(){["a.png"]=Hash(1),["bad.jpg"]=Hash(2),["c.bmp"]=Hash(1)},fingerprint:(data,name,_,_)=>name=="bad.jpg"?throw new FormatException():BitConverter.ToUInt64(data));
        check(scan.Pairs.Count==1&&scan.Loaded==2&&scan.Issues.Single().RelativePath=="bad.jpg"&&source.Active==0,"Reviewed P2: malformed decoder FormatException is a visible per-image skip, later valid files still match");
        scan=Run(new(){["a.png"]=Hash(1),["b.png"]=Hash(2)},fingerprint:(_,_,_,_)=>null);
        check(scan.Pairs.Count==0&&scan.Issues.Count==2&&scan.Issues.All(i=>i.Reason=="Image is too small or lacks supported detail"),"Low detail/null signatures are not admitted as zero-bit matches");
        scan=Run(new(){["a.png"]=Hash(1),["b.png"]=Hash(1),["c.png"]=Hash(1)},new(MaxFiles:2));
        check(scan.Attempted==2&&scan.FileLimit&&scan.Pairs.Count==1&&source.Opened==2,"Attempt bound caps source opens including decoder failures");
        foreach(int budget in new[]{15,16})
        {scan=Run(new(){["a.png"]=Hash(1),["b.png"]=Hash(1)},new(MaxReadBytes:budget));check(scan.Pairs.Count==(budget==16?1:0)&&scan.ByteLimit==(budget==15)&&scan.BytesRead==(budget==16?16:8),"Whole source budget precheck exact/+1: "+budget);}
        scan=Run(new(){["huge.png"]=new byte[16*1024*1024+1],["empty.bmp"]=Array.Empty<byte>(),["unsupported.jxl"]=Hash(1)});
        check(source.Opened==0&&scan.Issues.Count==3,"Oversize/empty/unsupported formats never read data");
        scan=Run(new(){["a.png"]=new byte[16*1024*1024]},new(MaxReadBytes:16*1024*1024),(_,_,_,_)=>1);
        check(scan.Loaded==1&&scan.BytesRead==16*1024*1024&&!scan.ByteLimit,"Exactly16MiB source remains eligible under exact read budget");
        var many=Enumerable.Range(0,50).ToDictionary(i=>$"f{i:000}.png",i=>Hash(1));scan=Run(many);
        var names=many.Keys.Order(StringComparer.Ordinal).ToArray();var pairs=(from a in Enumerable.Range(0,50) from b in Enumerable.Range(a+1,49-a) select(names[a],names[b])).Take(1000).ToArray();
        check(scan.Pairs.Count==1000&&scan.MatchingPairs==1225&&scan.ComparedPairs==1225&&scan.Pairs.Select(p=>(p.First,p.Second)).SequenceEqual(pairs),"First1000 ordinal pairs retained, not score ranking, all1225 completed matches counted");
        foreach(string kind in new[]{"short","eof","seek","stamp","dispose","throw","invalid-read"})
        {
            source=new Source(new(){["a.png"]=Hash(1),["b.png"]=Hash(1)}){Kind=kind};scan=PhotoSimilarity.Scan(source.Candidates,source.Open,(_,_,_,_)=>1,default);
            check(source.Active==0&&source.Opened==source.Disposed,"All leases released on read/Dispose boundary: "+kind);
            check(kind=="short"?scan.Pairs.Count==1:scan.Pairs.Count==0&&scan.Issues.Count==2,"Failed/incomplete image snapshot never matched: "+kind);
            check(scan.BytesRead<=16,"Read reservation stays within planned byte budget even on failure: "+kind);
        }
        source=new Source(new(){["a.png"]=Hash(1),["b.jpg"]=Hash(1)}){SameIdentity=true};scan=PhotoSimilarity.Scan(source.Candidates,source.Open,(_,_,_,_)=>1,default);
        check(scan.Loaded==1&&scan.Pairs.Count==0&&scan.BytesRead==8&&scan.Issues.Single().Reason=="Same file identity (hard link)","Repeated hard-link identity read once across formats");
        source=new Source(new(){["a.png"]=Hash(1),["b.png"]=Hash(1)}){SameIdentity=true,ChangedAlias=true};scan=PhotoSimilarity.Scan(source.Candidates,source.Open,(_,_,_,_)=>1,default);
        check(scan.Loaded==0&&scan.Issues.Count==2&&scan.Issues.All(i=>i.Reason=="File changed during comparison"),"Later changed identity invalidates earlier retained image signature");
        source=new Source(new(){["a.png"]=Hash(1)});scan=PhotoSimilarity.Scan(source.Candidates,source.Open,(_,_,_,_)=>1,default,elapsed:()=>TimeSpan.FromSeconds(60));
        check(scan.TimeLimit&&source.Opened==0,"Content time limit before open skips safely");
        source=new Source(new(){["a.png"]=Hash(1),["b.png"]=Hash(1)});scan=PhotoSimilarity.Scan(source.Candidates,source.Open,(_,_,_,_)=>1,default,elapsed:()=>source.Disposed==2?TimeSpan.FromSeconds(60):TimeSpan.Zero);
        check(scan.Loaded==2&&scan.TimeLimit&&scan.ComparedPairs==0,"Comparison phase time limit preserves loaded count and excludes unfinished pairs");
        using(var cancel=new CancellationTokenSource())
        {source=new Source(new(){["a.png"]=Hash(1)});reject(()=>PhotoSimilarity.Scan(source.Candidates,source.Open,(_,_,_,checkpoint)=>{cancel.Cancel();checkpoint();return 1;},cancel.Token),"Cancellation during decoder propagates and prevents late results");check(source.Active==0,"Decoder cancellation releases held input");}
        int opens=0;scan=PhotoSimilarity.Scan(new[]{new DuplicateCandidate("..\\x.png",8),new("a.png:ads",8),new("CON.png",8)},_=>{opens++;throw new Exception();},(_,_,_,_)=>1,default);
        check(opens==0&&scan.Issues.Count==3,"Invalid escaping/ADS/device paths never reach image opener");
        foreach(var limit in new[]{new PhotoSimilarityLimits(MaxFiles:201),new(MaxFileBytes:16777217),new(MaxReadBytes:134217729),new(MaxPairs:1001),new(Seconds:61)})reject(()=>PhotoSimilarity.Scan(Array.Empty<DuplicateCandidate>(),_=>throw new Exception(),(_,_,_,_)=>1,default,limit),"Reject unbounded image scan configuration");
        source=new Source(new(){["a.png"]=Hash(1)});reject(()=>PhotoSimilarity.Scan(source.Candidates,source.Open,(_,_,_,_)=>1,new CancellationToken(true)),"Pre-cancel before image open");check(source.Opened==0,"Pre-cancel performs no source I/O");
        var prefs=new Prefs();check(!Catalog.Offered(prefs).Any(a=>a.Id=="doctor.photosimilar"),"Doctor opt-in gates new image action");prefs.Set(Feature.Doctor,true);prefs.SetCustomActions(new[]{"doctor.photosimilar","doctor.textsimilar"});var restored=Prefs.Parse(prefs.Serialize());
        check(restored.IsOn(Feature.Doctor)&&restored.CustomActionIds.SequenceEqual(prefs.CustomActionIds)&&Catalog.Offered(restored).Any(a=>a.Id=="doctor.photosimilar"),"Image action and existing text action persist with stable menu IDs");
    }
    private sealed class Source(Dictionary<string,byte[]> bytes)
    {
        public string Kind="";public bool SameIdentity,ChangedAlias;public int Opened,Disposed,Active,MaxActive;
        public DuplicateCandidate[] Candidates=>bytes.Select(x=>new DuplicateCandidate(x.Key,x.Value.Length)).ToArray();
        public IDuplicateInput Open(DuplicateCandidate c){Opened++;Active++;MaxActive=Math.Max(MaxActive,Active);return new Input(this,c,bytes[c.RelativePath]);}
        private sealed class Input:IDuplicateInput
        {
            readonly Source source;readonly Probe stream;int refresh;bool disposed;
            public Input(Source source,DuplicateCandidate c,byte[] bytes){this.source=source;stream=new Probe(source,bytes);Stamp=new(source.SameIdentity?"same":c.RelativePath,c.Length,source.ChangedAlias&&c.RelativePath=="b.png"?2:1);}
            public Stream Stream=>stream;public DuplicateStamp Stamp{get;}
            public DuplicateStamp Refresh()=>source.Kind=="stamp"&&++refresh>=3?Stamp with{LastWrite=7}:Stamp;
            public void Dispose(){if(disposed)return;disposed=true;stream.Dispose();source.Active--;source.Disposed++;if(source.Kind=="dispose")throw new IOException();}
        }
        private sealed class Probe(Source source,byte[] bytes):MemoryStream(bytes,false)
        {
            public override int Read(byte[] buffer,int offset,int count){if(source.Kind=="throw")throw new IOException();if(source.Kind=="invalid-read")return count+1;if(source.Kind=="eof")return 0;return base.Read(buffer,offset,source.Kind=="short"?Math.Min(count,3):count);}
            public override long Seek(long offset,SeekOrigin origin)=>source.Kind=="seek"?offset+1:base.Seek(offset,origin);
        }
    }
}
