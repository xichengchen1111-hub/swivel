using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Swivel.Core;

internal static class QrDataRecoveryChecks
{
    private static readonly Type RepairType=typeof(QrCode).Assembly.GetType("Swivel.Core.QrDataRecovery")!;
    private static readonly MethodInfo RepairMethod=RepairType.GetMethod("TryRepair",BindingFlags.Static|BindingFlags.NonPublic)!;
    private static (bool Ok,byte[]? Data,int Changed) Repair(byte[] input,int parity,CancellationToken token=default)
    {object?[] args={input,parity,token,null,0};bool ok=(bool)RepairMethod.Invoke(null,args)!;return(ok,(byte[]?)args[3],(int)args[4]!);}
    private static byte[] Encoded(byte[] data,int parity)
    {var remainder=(byte[])typeof(QrCode).GetMethod("Remainder",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,new object[]{data,parity})!;return data.Concat(remainder).ToArray();}
    private static int[] Table(QrCorrection level,string suffix)
    {string prefix=level switch{QrCorrection.Low=>"",QrCorrection.Medium=>"Medium",QrCorrection.Quartile=>"Quartile",_=>"High"};return(int[])typeof(QrCode).GetField(prefix+suffix,BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;}
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        var shapes=new HashSet<(int Data,int Parity)>();var random=new Random(810026);
        foreach(var level in Enum.GetValues<QrCorrection>())for(int version=1;version<=40;version++)
        {
            int data=Table(level,"DataWords")[version-1],blocks=Table(level,"BlockCounts")[version-1],parity=Table(level,"CheckWords")[version-1];
            shapes.Add((data/blocks,parity));if(data%blocks!=0)shapes.Add((data/blocks+1,parity));
        }
        foreach(var shape in shapes)
        {
            var bytes=new byte[shape.Data];random.NextBytes(bytes);bytes[0]=0;
            var original=Encoded(bytes,shape.Parity);
            for(int errors=0;errors<=shape.Parity/2;errors++)
            {
                var received=(byte[])original.Clone();var used=new HashSet<int>();
                while(used.Count<errors)used.Add(random.Next(received.Length));
                foreach(int index in used)received[index]^=(byte)random.Next(1,256);
                var before=(byte[])received.Clone();var actual=Repair(received,shape.Parity);
                check(actual.Ok&&actual.Data!.SequenceEqual(original)&&actual.Changed==errors,"Every standard QR short/long block shape recovers zero through its exact per-block symbol capacity");
                check(received.SequenceEqual(before)&&!ReferenceEquals(received,actual.Data),"RS input is never mutated and even a clean result is an independent buffer");
            }
            var parityOnly=(byte[])original.Clone();for(int i=0;i<shape.Parity/2;i++)parityOnly[shape.Data+i]^=(byte)(i+1);
            var fixedParity=Repair(parityOnly,shape.Parity);
            check(fixedParity.Ok&&fixedParity.Data!.SequenceEqual(original)&&fixedParity.Changed==shape.Parity/2,"Parity-only damage counts and repairs encoded bytes just like data damage");
            var zeroS0=(byte[])original.Clone();zeroS0[0]^=91;zeroS0[^1]^=91;var fixedZero=Repair(zeroS0,shape.Parity);
            check(fixedZero.Ok&&fixedZero.Data!.SequenceEqual(original)&&fixedZero.Changed==2,"Zero S0 is not mistaken for a clean block when later syndromes reveal two errors");
        }
        foreach(int parity in new[]{7,10,13,30})
        {
            var original=Encoded(Enumerable.Range(0,34).Select(x=>(byte)x).ToArray(),parity);
            foreach(int position in new[]{0,33,34,original.Length-1})for(int magnitude=1;magnitude<=255;magnitude++)
            {
                var received=(byte[])original.Clone();received[position]^=(byte)magnitude;var actual=Repair(received,parity);
                check(actual.Ok&&actual.Changed==1&&actual.Data!.SequenceEqual(original),"Every nonzero magnitude at first/last data and parity position is recovered");
            }
        }
        // Construct damage relative to A which is one symbol away from a different valid B.
        // A decoder cannot identify the historical original from parity alone.
        var first=Encoded(new byte[19],7);var otherData=new byte[19];otherData[0]=1;var second=Encoded(otherData,7);
        var misleading=(byte[])second.Clone();misleading[^1]^=13;var wrong=Repair(misleading,7);
        check(first.Where((x,i)=>x!=misleading[i]).Count()>3&&wrong.Ok&&wrong.Changed==1&&wrong.Data!.SequenceEqual(second),"Beyond-capacity input can decode to another valid candidate; correction count is not proof of original damage or text authenticity");
        var virtualData=new byte[20];virtualData[0]=1;var virtualLeading=Encoded(virtualData,7).Skip(1).ToArray();
        var virtualRoot=Repair(virtualLeading,7);
        check(!virtualRoot.Ok&&virtualRoot.Data==null&&virtualRoot.Changed==0,"A locator root at a shortened block's absent leading position cannot be counted as a received-byte repair");
        var uncorrectable=(byte[])first.Clone();for(int i=0;i<4;i++)uncorrectable[i]^=(byte)(i+21);var refused=Repair(uncorrectable,7);
        check(!refused.Ok&&refused.Data==null&&refused.Changed==0,"A specific uncorrectable block refuses without leaking partial corrected bytes");
        reject(()=>Repair(first,7,new CancellationToken(true)),"Pre-cancelled RS work refuses before reading bytes");
        foreach(var invalid in new[]{(new byte[7],7),(new byte[256],7),(new byte[40],31),(new byte[40],0)})
            reject(()=>Repair(invalid.Item1,invalid.Item2),"Invalid RS size or parity budget is refused");

        var masks=new HashSet<int>();int mixed=0;
        foreach(var level in Enum.GetValues<QrCorrection>())for(int version=1;version<=40;version++)
        {
            int data=Table(level,"DataWords")[version-1],blocks=Table(level,"BlockCounts")[version-1],parity=Table(level,"CheckWords")[version-1];
            string text=new('x',data-(version<10?3:4));var code=QrCode.Encode(text,level);masks.Add(code.Mask);
            var clean=Image(code);var strict=QrFlatRead.Read(clean,default);var unchanged=QrFlatRead.ReadWithDataRecovery(clean,default);
            check(strict==unchanged.Result&&unchanged.CorrectedCodewords==0&&unchanged.CorrectedBlocks==0,"Recovery of every clean version and level has exactly the old strict result and zero changes");
            var positions=DataPositions(code);var wordMap=BlockWordIndices(data,blocks,parity);var damaged=new PixelImage(clean.Width,clean.Height,(byte[])clean.Bgra.Clone());
            if(data%blocks!=0)mixed++;
            foreach(var block in wordMap)
            {
                int count=block.Length-parity;var selected=new HashSet<int>{0,count-1,count,block.Length-1};
                while(selected.Count>parity/2)selected.Remove(selected.Last());
                for(int i=0;selected.Count<parity/2;i++)selected.Add(i);
                foreach(int index in selected)XorWord(damaged,positions,block[index],(byte)(1+index%255));
            }
            var before=(byte[])damaged.Bgra.Clone();var recovered=QrFlatRead.ReadWithDataRecovery(damaged,default);
            check(recovered.Result==strict&&recovered.CorrectedCodewords==blocks*(parity/2)&&recovered.CorrectedBlocks==blocks,"Every QR version and level recovers exactly its independent per-block limit through deinterleaving and payload grammar");
            check(before.SequenceEqual(damaged.Bgra),"Recovery does not mutate the supplied raster");
            reject(()=>QrFlatRead.Read(damaged,default),"The unchanged strict API still refuses every data-damaged recovery fixture");
            if(version is 1 or 7 or 10 or 27 or 40)for(int turns=1;turns<4;turns++)
            {
                var turn=Rotate(damaged,turns);var result=QrFlatRead.ReadWithDataRecovery(turn,default);
                check(result.Result.Text==text&&result.Result.ClockwiseOrientationDegrees==turns*90&&result.CorrectedCodewords==recovered.CorrectedCodewords&&result.CorrectedBlocks==blocks,"Recovery preserves exact quarter-turn orientation and counts");
            }
            if(positions.Count>(data+blocks*parity)*8)
            {
                var badRemainder=new PixelImage(clean.Width,clean.Height,(byte[])clean.Bgra.Clone());FlipModule(badRemainder,positions[^1]);
                reject(()=>QrFlatRead.ReadWithDataRecovery(badRemainder,default),"Unprotected QR remainder bits are never silently corrected");
            }
        }
        check(mixed>0,"Integration exercises mixed short and long block layouts");
        check(masks.Count>=4,"Recovery fixture set covers multiple QR mask patterns");
        foreach(string text in new[]{"中文 繁體 日本語 😀","https://example.com/one?x=1&y=2","first\r\nsecond\tline"})foreach(int scale in new[]{1,2,4})
        {
            var code=QrCode.Encode(text,QrCorrection.High);var image=Image(code,scale);XorWord(image,DataPositions(code),0,255,scale);
            var result=QrFlatRead.ReadWithDataRecovery(image,default);
            check(result.Result.Text==text&&result.Result.PixelScale==scale&&result.CorrectedCodewords==1,"Recovery retains exact Unicode, controls and URL text without interpreting it");
        }
        // A recovered, valid RS block must still pass the existing payload grammar.
        var grammarCode=QrCode.Encode("x");var grammarPositions=DataPositions(grammarCode);
        var invalidGrammar=new byte[19];invalidGrammar[0]=0x80;var grammarWords=Encoded(invalidGrammar,7);
        var grammarImage=Image(grammarCode);WriteWords(grammarImage,grammarCode,grammarPositions,grammarWords);
        XorWord(grammarImage,grammarPositions,grammarWords.Length-1,1);
        reject(()=>QrFlatRead.ReadWithDataRecovery(grammarImage,default),"Parity recovery cannot authorize Kanji or another unsupported payload mode");
        var strictGrid=typeof(QrCode).GetMethod("ReadCleanGrid",BindingFlags.Static|BindingFlags.NonPublic)!;
        var strictFixture=QrCode.Encode("strict unchanged");var grid=new bool[strictFixture.Size,strictFixture.Size];
        for(int y=0;y<strictFixture.Size;y++)for(int x=0;x<strictFixture.Size;x++)grid[x,y]=strictFixture[x,y];
        var firstData=DataPositions(strictFixture)[0];grid[firstData.X,firstData.Y]=!grid[firstData.X,firstData.Y];
        check(strictGrid.Invoke(null,new object[]{grid,1,CancellationToken.None})==null,"ReadCleanGrid itself stays strict without an implicit recovery fallback");
        var mirror=Image(grammarCode);var mirroredPixels=new byte[mirror.Bgra.Length];
        for(int y=0;y<mirror.Height;y++)for(int x=0;x<mirror.Width;x++)Array.Copy(mirror.Bgra,(y*mirror.Width+mirror.Width-1-x)*4,mirroredPixels,(y*mirror.Width+x)*4,4);
        for(int turn=0;turn<4;turn++)
        {
            var mirrored=new PixelImage(mirror.Width,mirror.Height,mirroredPixels);if(turn!=0)mirrored=Rotate(mirrored,turn);
            reject(()=>QrFlatRead.ReadWithDataRecovery(mirrored,default),"Recovery does not add a mirrored-image fallback");
        }
        var large=Image(QrCode.Encode(new string('x',QrCode.MaxBytes)),16);
        var largeHash=System.Security.Cryptography.SHA256.HashData(large.Bgra);
        using(var cancellation=new CancellationTokenSource())
        {
            // Timer resolution differs (about 15 ms on Windows), so keep reading until the cancel lands;
            // every completed read must be whole, and the cancelled one must throw.
            cancellation.CancelAfter(1);bool cancelled=false;
            for(int attempt=0;attempt<10000&&!cancelled;attempt++)
            {
                try{check(QrFlatRead.ReadWithDataRecovery(large,cancellation.Token) is not null,"Completed recovery read returns a whole result");}
                catch(OperationCanceledException){cancelled=true;}
            }
            check(cancelled,"Cancellation during a full valid raster read stops without returning a partial recovery result");
        }
        check(System.Security.Cryptography.SHA256.HashData(large.Bgra).SequenceEqual(largeHash),"Cancelled recovery keeps the entire supplied raster unchanged");
        var baseCode=QrCode.Encode(new string('x',180));var baseline=Image(baseCode,2);
        foreach(var module in new[]{(0,0),(8,0),(6,9),(baseCode.Size-11,0),(baseCode.Size-7,baseCode.Size-7)})
        {
            var changed=new PixelImage(baseline.Width,baseline.Height,(byte[])baseline.Bgra.Clone());FlipModule(changed,module,2);
            reject(()=>QrFlatRead.ReadWithDataRecovery(changed,default),"Finder, format, timing, version and alignment modules must be exact in recovery mode");
        }
        foreach(int component in new[]{0,3})
        {
            var pixels=(byte[])baseline.Bgra.Clone();pixels[(4*2*baseline.Width+4*2)*4+component]=component==3?(byte)0:(byte)100;
            reject(()=>QrFlatRead.ReadWithDataRecovery(new(baseline.Width,baseline.Height,pixels),default),"Partial alpha or intermediate colors never become erasures");
        }
        var border=(byte[])baseline.Bgra.Clone();border[0]=0;
        reject(()=>QrFlatRead.ReadWithDataRecovery(new(baseline.Width,baseline.Height,border),default),"Quiet zone must remain exact");
        var uneven=(byte[])baseline.Bgra.Clone();int at=((baseline.Width-10)*baseline.Width+baseline.Width-10)*4;for(int i=0;i<3;i++)uneven[at+i]=(byte)(255-uneven[at+i]);
        reject(()=>QrFlatRead.ReadWithDataRecovery(new(baseline.Width,baseline.Height,uneven),default),"Nonuniform submodule pixels are not repaired");
        reject(()=>QrFlatRead.ReadWithDataRecovery(baseline,new CancellationToken(true)),"Pre-cancelled recovery performs no raster work");
        reject(()=>QrFlatRead.ReadWithDataRecovery(new(1,2,new byte[8]),default),"Nonsquare recovery input remains refused");
        reject(()=>QrFlatRead.ReadWithDataRecovery(new(29,29,new byte[1]),default),"Malformed recovery pixel buffer remains refused");
    }
    private static PixelImage Image(QrCode code,int scale=1)
    {
        var mono=code.Raster(scale);var pixels=new byte[mono.Length*4];for(int i=0;i<mono.Length;i++){pixels[4*i]=pixels[4*i+1]=pixels[4*i+2]=mono[i];pixels[4*i+3]=255;}int side=(code.Size+8)*scale;return new(side,side,pixels);
    }
    private static PixelImage Rotate(PixelImage image,int turns)
    {
        int side=image.Width;var pixels=new byte[image.Bgra.Length];for(int y=0;y<side;y++)for(int x=0;x<side;x++)
        {var(sx,sy)=turns switch{1=>(y,side-1-x),2=>(side-1-x,side-1-y),_=>(side-1-y,x)};Array.Copy(image.Bgra,(sy*side+sx)*4,pixels,(y*side+x)*4,4);}return new(side,side,pixels);
    }
    private static List<(int X,int Y)> DataPositions(QrCode code)
    {
        int size=code.Size;var reserved=new bool[size,size];
        void Box(int left,int top,int width,int height){for(int y=top;y<top+height;y++)for(int x=left;x<left+width;x++)reserved[x,y]=true;}
        Box(0,0,9,9);Box(size-8,0,8,9);Box(0,size-8,9,8);
        for(int i=8;i<size-8;i++)reserved[6,i]=reserved[i,6]=true;
        var centers=((int[][])typeof(QrCode).GetField("AlignmentCenters",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!)[code.Version-1];
        foreach(int x in centers)foreach(int y in centers)if(!(x==6&&(y==6||y==size-7)||y==6&&x==size-7))Box(x-2,y-2,5,5);
        if(code.Version>=7){Box(size-11,0,3,6);Box(0,size-11,6,3);}
        var result=new List<(int,int)>();bool up=true;
        for(int right=size-1;right>=1;right-=2){if(right==6)right--;for(int row=0;row<size;row++){int y=up?size-1-row:row;for(int d=0;d<2;d++)if(!reserved[right-d,y])result.Add((right-d,y));}up=!up;}return result;
    }
    private static int[][] BlockWordIndices(int data,int blocks,int parity)
    {
        int shortLength=data/blocks,shortCount=blocks-data%blocks,index=0;var maps=Enumerable.Range(0,blocks).Select(b=>new int[shortLength+(b<shortCount?0:1)+parity]).ToArray();
        for(int column=0;column<=shortLength;column++)for(int b=0;b<blocks;b++)if(column<maps[b].Length-parity)maps[b][column]=index++;
        for(int column=0;column<parity;column++)for(int b=0;b<blocks;b++)maps[b][maps[b].Length-parity+column]=index++;return maps;
    }
    private static void WriteWords(PixelImage image,QrCode code,List<(int X,int Y)> positions,byte[] words)
    {
        var mask=(Func<int,int,int,bool>)typeof(QrCode).GetMethod("MaskAt",BindingFlags.Static|BindingFlags.NonPublic)!.CreateDelegate(typeof(Func<int,int,int,bool>));
        for(int bit=0;bit<words.Length*8;bit++)
        {
            var p=positions[bit];bool dark=((words[bit/8]&(1<<(7-bit%8)))!=0)^mask(code.Mask,p.X,p.Y);
            int offset=((p.Y+4)*image.Width+p.X+4)*4;for(int c=0;c<3;c++)image.Bgra[offset+c]=dark?(byte)0:(byte)255;
        }
    }
    private static void XorWord(PixelImage image,List<(int X,int Y)> positions,int word,byte difference,int scale=1)
    {for(int bit=0;bit<8;bit++)if((difference&(1<<(7-bit)))!=0)FlipModule(image,positions[word*8+bit],scale);}
    private static void FlipModule(PixelImage image,(int X,int Y) position,int scale=1)
    {for(int y=0;y<scale;y++)for(int x=0;x<scale;x++){int offset=(((position.Y+4)*scale+y)*image.Width+(position.X+4)*scale+x)*4;for(int component=0;component<3;component++)image.Bgra[offset+component]=(byte)(255-image.Bgra[offset+component]);}}
}
