using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Swivel.Core;

internal static class FolderSizeChecks
{
    public static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        FolderEntry File(string name,long bytes,uint attributes=0) => new(name,false,bytes,attributes);
        FolderEntry Dir(string name,uint attributes=0x10) => new(name,true,0,attributes);
        var child=new MemoryFolder("child",new[]{File("image",300),File("empty",0)});
        var root=new MemoryFolder("root",new[]{File("first",10),Dir("child"),File("second",20)},new(){["child"]=child});
        var scan=FolderSizes.Scan(root,default);
        check(scan.Root.Bytes==330 && scan.Root.Files==4 && !scan.Root.Incomplete && scan.Entries==5,"Folder sizes sum only regular file logical lengths across nested folders");
        check(child.Disposed && !root.Disposed,"Child metadata lease is released; caller still owns root");
        var unsafeEntries=new List<FolderEntry>{File("ordinary",10)};
        foreach(uint flag in new uint[]{0x4,0x40,0x400,0x1000,0x40000,0x400000}) unsafeEntries.Add(Dir("skip"+flag,0x10|flag));
        unsafeEntries.Add(File("negative",-1));unsafeEntries.Add(File("bad-layout",15,0x10));unsafeEntries.Add(Dir("bad/name"));
        var unsafeRoot=new MemoryFolder("root",unsafeEntries);
        scan=FolderSizes.Scan(unsafeRoot,default);
        check(scan.Root.Bytes==10 && scan.Root.Files==1 && scan.Root.Incomplete && scan.Root.Skipped==9 && unsafeRoot.Opened.Count==0,"Unsafe flags, negative metadata, mismatched kind and unsafe names never open child directories");
        check(scan.Root.Children.Count==10 && scan.Root.Children.Count(x=>x.Issue is not null)==9,"Skipped entries retain visible reasons and unknown size rather than pretending empty");
        foreach (string issue in new[] { "System, link, cloud or invalid item", "Depth limit", "Unavailable or unsupported" }) check(Strings.Table.ContainsKey(issue),"Closed folder skip reason is translated: "+issue);
        var missingRoot=new MemoryFolder("root",new[]{File("kept",99),Dir("denied")});
        scan=FolderSizes.Scan(missingRoot,default);
        check(scan.Root.Bytes==99 && scan.Root.Skipped==1 && scan.Root.Incomplete && scan.Root.Children.Single(x=>x.Name=="denied").Issue=="Unavailable or unsupported","Open failure preserves counted bytes with partial status");
        var failed=new MemoryFolder("root",new[]{File("before",5),File("after",9)}){FailAt=1};
        scan=FolderSizes.Scan(failed,default);
        check(scan.Root.Bytes==5 && scan.Root.Incomplete && scan.Root.Skipped==1,"Enumeration failure retains known prefix and marks unavailable remainder");
        MemoryFolder Chain(int depth) => depth==0 ? new("leaf",new[]{File("inside",5)}) : new("depth"+depth,new[]{File("local",1),Dir("nested")},new(){["nested"]=Chain(depth-1)});
        scan=FolderSizes.Scan(Chain(5),default);
        check(scan.Root.Bytes==5 && scan.Root.Files==5 && scan.Root.Skipped==1 && scan.Root.Incomplete,"Depth-four scan includes root through depth four and marks deeper subtree partial");
        scan=FolderSizes.Scan(new MemoryFolder("root",new[]{File("file",2),Dir("nested")}),default,new(MaxDepth:0));
        check(scan.Root.Bytes==2 && scan.Root.Skipped==1 && scan.Root.Children.Single(x=>x.Name=="nested").Issue=="Depth limit","Depth limit counts root files and labels skipped folder");
        scan=FolderSizes.Scan(new MemoryFolder("root",Enumerable.Range(0,20001).Select(i=>File("file"+i,1))),default);
        check(scan.Entries==20000 && scan.Root.Files==20000 && scan.Root.Bytes==20000 && scan.EntryLimit && scan.Root.Incomplete,"Global 20000-entry budget stops and marks partial");
        scan=FolderSizes.Scan(new MemoryFolder("root",new[]{File("only",1)}),default,new(MaxEntries:1));
        check(scan.Entries==1 && !scan.EntryLimit && !scan.Root.Incomplete,"Exactly-at-budget complete folder is not falsely truncated");
        var boundaryChild=new MemoryFolder("child",new[]{File("one",1),File("two",2)});
        scan=FolderSizes.Scan(new MemoryFolder("root",new[]{Dir("child"),File("root-after",9)},new(){["child"]=boundaryChild}),default,new(MaxEntries:2));
        check(scan.Entries==2 && scan.Root.Bytes==1 && scan.Root.Incomplete && scan.EntryLimit && boundaryChild.Disposed,"Entry budget is shared across recursion and releases active child");
        var neverRead=new MemoryFolder("root",new[]{File("not-read",1)});
        scan=FolderSizes.Scan(neverRead,default,elapsed:()=>TimeSpan.FromSeconds(30));
        check(scan.TimeLimit && scan.Root.Incomplete && scan.Root.Bytes==0 && neverRead.Reads==0,"Deadline checks before any directory enumeration");
        int ticks=0;
        scan=FolderSizes.Scan(new MemoryFolder("root",new[]{File("first",1),File("later",100)}),default,new(Seconds:1),()=>TimeSpan.FromMilliseconds(++ticks*300));
        check(scan.TimeLimit && scan.Root.Incomplete && scan.Root.Bytes==1,"Time deadline preserves partial counts after a read");
        using(var cancellation=new CancellationTokenSource())
        {
            var cancellingChild=new MemoryFolder("child",new[]{File("first",1),File("next",2)}){OnRead=i=>{if(i==1)cancellation.Cancel();}};
            var cancellingRoot=new MemoryFolder("root",new[]{Dir("child")},new(){["child"]=cancellingChild});
            reject(()=>FolderSizes.Scan(cancellingRoot,cancellation.Token),"Cancellation inside enumeration propagates rather than returning success");
            check(cancellingChild.Disposed && !cancellingRoot.Disposed,"Cancellation releases descendant handles without taking root ownership");
        }
        reject(()=>FolderSizes.Scan(new MemoryFolder("root",Array.Empty<FolderEntry>()),new CancellationToken(true)),"Initial folder cancellation");
        foreach(var limits in new[]{new FolderScanLimits(MaxEntries:0),new(MaxEntries:20001),new(MaxDepth:5),new(MaxDepth:-1),new(Seconds:0),new(Seconds:31)}) reject(()=>FolderSizes.Scan(root,default,limits),"Invalid scan budgets refused");
        scan=FolderSizes.Scan(new MemoryFolder("huge",new[]{File("a",long.MaxValue),File("b",long.MaxValue)}),default);
        check(scan.Root.Bytes==(decimal)long.MaxValue*2,"Logical totals do not overflow signed or unsigned 64-bit accounting");
        var many=FolderSizes.Scan(new MemoryFolder("root",Enumerable.Range(0,60).Select(i=>File("f"+i,i))),default).Root;
        var display=FolderSizes.Display(many);
        check(display.Count==24 && display[^1].Aggregate && display.Sum(x=>x.Bytes)==many.Bytes && display.Sum(x=>x.Files)==60,"Top 23 plus Other preserves all bytes and file counts");
        var next=FolderSizes.Display(display[^1]);
        check(next.Count==24 && next.Sum(x=>x.Bytes)==display[^1].Bytes,"Other remains navigable with bounded native row count");
        var allNames=new List<string>();void Flatten(FolderSizeNode node){foreach(var n in FolderSizes.Display(node)){if(n.Aggregate)Flatten(n);else allNames.Add(n.Name);}}Flatten(many);
        check(allNames.Count==60 && allNames.Distinct().Count()==60,"Repeated Other navigation reaches every child exactly once");
        var first=Record("图像 🦜.png",0,12345,true);var second=Record("folder",0x10,0,false);byte[] combined=first.Concat(second).ToArray();
        var parsed=FolderDirectoryBuffer.Parse(combined);
        check(parsed.Count==2 && parsed[0].Name=="图像 🦜.png" && parsed[0].Bytes==12345 && parsed[1].Directory,"Independent packed FILE_FULL_DIR_INFO Unicode/multiple records parse correctly");
        check(FolderDirectoryBuffer.Parse(second.Concat(new byte[4096]).ToArray()).Count==1,"Last native record stops before zeroed capacity padding");
        foreach(int offset in new[]{0,60})
        {
            foreach(uint value in new uint[]{1,3,67,69,65536,uint.MaxValue})
            {
                byte[] bad=(byte[])combined.Clone();BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(offset),value);reject(()=>FolderDirectoryBuffer.Parse(bad),"Native record lengths/offsets bounded and aligned");
            }
        }
        reject(()=>FolderDirectoryBuffer.Parse(new byte[69]),"Short native record refused");
        byte[] malformed=Record("x",0,0,false);malformed[68]=0;malformed[69]=0xd8;reject(()=>FolderDirectoryBuffer.Parse(malformed),"Unpaired surrogate metadata rejected before path construction");
        var random=new Random(7123);
        for(int run=0;run<300;run++)
        {
            int count=random.Next(1,25);var node=FolderSizes.Scan(new MemoryFolder("root",Enumerable.Range(0,count).Select(i=>File("n"+i,random.Next(0,1000000)))),default).Root;
            var rows=FolderSizes.Display(node);double w=random.Next(1,2000),h=random.Next(1,2000);var tiles=FolderMapLayout.Build(rows,w,h);
            check(tiles.Count==rows.Count(x=>x.Bytes>0) && tiles.All(t=>double.IsFinite(t.X+t.Y+t.Width+t.Height) && t.X>=0 && t.Y>=0 && t.Width>=0 && t.Height>=0 && t.X+t.Width<=w+1e-8 && t.Y+t.Height<=h+1e-8),"Map random partition stays finite and within bounds");
            check(Math.Abs(tiles.Sum(t=>t.Width*t.Height)-w*h)<1e-5,"Map rectangles exactly cover nonempty chart area");
            bool overlap=false;for(int a=0;a<tiles.Count;a++)for(int b=a+1;b<tiles.Count;b++)if(Math.Min(tiles[a].X+tiles[a].Width,tiles[b].X+tiles[b].Width)-Math.Max(tiles[a].X,tiles[b].X)>1e-8 && Math.Min(tiles[a].Y+tiles[a].Height,tiles[b].Y+tiles[b].Height)-Math.Max(tiles[a].Y,tiles[b].Y)>1e-8)overlap=true;
            check(!overlap,"Map rectangles do not overlap");
            check(tiles.All(t=>Math.Abs(t.Width*t.Height/(w*h)-(double)(rows[t.Index].Bytes/node.Bytes))<1e-12),"Each tile area is proportional to counted logical bytes");
        }
        var empty=FolderSizes.Scan(new MemoryFolder("empty",new[]{File("zero",0)}),default).Root;
        check(FolderMapLayout.Build(FolderSizes.Display(empty),500,200).Count==0,"Zero-byte files remain list entries without invented map area");
        foreach(double bad in new[]{-1,double.NaN,double.PositiveInfinity}) reject(()=>FolderMapLayout.Build(display,bad,10),"Invalid map geometry rejected");
        var prefs=new Prefs();check(!Catalog.Available(prefs,Array.Empty<string>()).Any(x=>x.Id=="doctor.folder"),"Folder scan opt-in off by default");prefs.Set(Feature.Doctor,true);
        check(Catalog.Available(prefs,Array.Empty<string>()).Any(x=>x.Id=="doctor.folder"),"Explicit folder picker action available without supplied files when enabled");
        check(DoctorFacts.VisibleBounded(new string('a',255),512)==new string('a',255) && DoctorFacts.VisibleBounded("x\u202ey",512)=="x[U+202E]y" && DoctorFacts.Visible(new string('a',255)).EndsWith('…'),"Folder labels retain full supported names and escaped invisible text while existing Doctor limits stay unchanged");
        check(new[]{"192.0.2.1","203.0.113.1"}.Select(DoctorFacts.Visible).SequenceEqual(new[]{"192.0.2.1","203.0.113.1"}),"Existing method-group selection must remain non-indexed for network address display");
    }
    private static byte[] Record(string name,uint attributes,long bytes,bool more)
    {
        byte[] text=Encoding.Unicode.GetBytes(name);int length=68+text.Length;int aligned=(length+7)/8*8;byte[] result=new byte[aligned];
        BinaryPrimitives.WriteUInt32LittleEndian(result,more?(uint)aligned:0);BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(40),bytes);BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(56),attributes);BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(60),(uint)text.Length);text.CopyTo(result,68);return result;
    }
    private sealed class MemoryFolder(string name,IEnumerable<FolderEntry> entries,Dictionary<string,MemoryFolder>? children=null):IFolderMetadata
    {
        public string Name=>name;public bool Disposed{get;private set;}public int Reads{get;private set;}public int FailAt=-1;public Action<int>? OnRead;public List<string> Opened=new();
        public IEnumerable<FolderEntry> Entries(CancellationToken token)
        {
            int i=0;foreach(var item in entries){if(Disposed)throw new ObjectDisposedException(name);OnRead?.Invoke(i);token.ThrowIfCancellationRequested();if(i++==FailAt)throw new IOException("Injected enumeration failure");Reads++;yield return item;}
        }
        public IFolderMetadata OpenChild(string child){if(Disposed)throw new ObjectDisposedException(name);Opened.Add(child);return children is not null && children.TryGetValue(child,out var folder)?folder:throw new UnauthorizedAccessException();}
        public void Dispose()=>Disposed=true;
    }
}
