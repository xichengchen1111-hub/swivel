using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace Swivel.Core;

// Original bounded reader for Word 97-2003 binary documents, written from the
// public [MS-CFB] and [MS-DOC] specifications. Only the root WordDocument and
// table streams are read: the piece table for the main document range and the
// paragraph/character property pages for table marks, hidden and deleted text.
// Never reads macros, OLE objects, pictures, the Data stream, embedded storages
// or external resources, and never decrypts.
public static class DocumentDoc
{
    public const string Invalid="Choose a well-formed Word 97-2003 DOC document. Limits: 8 MiB input, 2,000,000 extracted UTF-16 units, 16,384 compound-file directory entries and 128 nested fields. Damaged, truncated or inconsistent files are refused.";
    public const string Encrypted="This Word document is password-protected (encrypted or obfuscated). Swivel does not decrypt documents; save an unprotected copy in Word first.";
    public const string Old="This is a Word 6, Word 95 or older document. Only Word 97-2003 DOC files are read; open and save it in a newer format first.";
    private const uint End=0xFFFFFFFE, NoStream=0xFFFFFFFF;
    private const int MaxEntries=16384, MaxFields=128, MaxCp=16*1024*1024;
    private const byte Ttp=1, InnerTtp=2, InnerCell=4, Hidden=8, Deleted=16, Spec=32;
    private static readonly byte[] Signature={0xD0,0xCF,0x11,0xE0,0xA1,0xB1,0x1A,0xE1};
    private const string Cp1252="€\u0081‚ƒ„…†‡ˆ‰Š‹Œ\u008DŽ\u008F\u0090‘’“”•–—˜™š›œ\u009DžŸ";

    private static UserError Bad()=>new(Invalid);
    private static ushort U16(byte[] b,long at)=>at>=0&&at+2<=b.Length?BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan((int)at,2)):throw Bad();
    private static uint U32(byte[] b,long at)=>at>=0&&at+4<=b.Length?BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan((int)at,4)):throw Bad();

    // [MS-CFB] compound file: header, DIFAT -> FAT, directory, mini stream.
    private sealed class Compound
    {
        private readonly byte[] file;private readonly int size;private readonly long sectors;private readonly uint[] fat;
        private readonly bool v4;private readonly Dictionary<string,(uint Start,ulong Length)> streams=new(StringComparer.OrdinalIgnoreCase);
        private byte[]? mini;private uint[]? miniFat;private readonly uint rootStart,firstMiniFat,miniFatCount;private readonly ulong rootLength;
        private readonly CancellationToken token;
        internal Compound(byte[] file,CancellationToken token)
        {
            this.file=file;this.token=token;
            if(file.Length<512||!file.AsSpan(0,8).SequenceEqual(Signature))throw Bad();
            ushort major=U16(file,0x1A);int shift=U16(file,0x1E);
            if(U16(file,0x1C)!=0xFFFE||!(major==3&&shift==9||major==4&&shift==12)||U16(file,0x20)!=6||U32(file,0x38)!=4096)throw Bad();
            v4=major==4;size=1<<shift;if(file.Length<size)throw Bad();
            sectors=(file.Length-size+size-1)/size;
            uint fatCount=U32(file,0x2C),firstDir=U32(file,0x30),firstDifat=U32(file,0x44),difatCount=U32(file,0x48);
            firstMiniFat=U32(file,0x3C);miniFatCount=U32(file,0x40);
            if(fatCount==0||fatCount>sectors||difatCount>sectors||miniFatCount>sectors)throw Bad();
            var fatSectors=new List<uint>((int)fatCount);var used=new HashSet<uint>();
            void Add(uint id){if(id>=sectors||!used.Add(id))throw Bad();fatSectors.Add(id);}
            for(int i=0;i<109&&fatSectors.Count<fatCount;i++)Add(U32(file,0x4C+4*i));
            uint next=firstDifat;var difats=new HashSet<uint>();
            while(fatSectors.Count<fatCount)
            {
                token.ThrowIfCancellationRequested();
                if(difats.Count>=difatCount||next>=sectors||!difats.Add(next)||used.Contains(next))throw Bad();
                long offset=Offset(next,size);
                for(int j=0;j<size/4-1&&fatSectors.Count<fatCount;j++)Add(U32(file,offset+4*j));
                next=U32(file,offset+size-4);
            }
            fat=new uint[fatCount*(size/4)];
            for(int i=0;i<fatSectors.Count;i++)
            {
                long offset=Offset(fatSectors[i],size);
                for(int j=0;j<size/4;j++)fat[i*(size/4)+j]=U32(file,offset+4*j);
            }
            // Directory: a chain of unknown length, bounded by the entry limit.
            var directory=new List<byte>();var seen=new bool[sectors];uint at=firstDir;
            while(at!=End)
            {
                token.ThrowIfCancellationRequested();
                if(at>=sectors||at>=fat.Length||seen[at]||directory.Count+size>MaxEntries*128)throw Bad();
                seen[at]=true;directory.AddRange(file.AsSpan((int)Offset(at,size),size).ToArray());at=fat[at];
            }
            byte[] entries=directory.ToArray();int count=entries.Length/128;
            if(count==0||entries[0x42]!=5)throw Bad();
            rootStart=U32(entries,0x74);rootLength=Length(entries,0);
            var stack=new Stack<uint>();var visited=new bool[count];stack.Push(U32(entries,0x4C));
            while(stack.Count>0)
            {
                token.ThrowIfCancellationRequested();uint id=stack.Pop();if(id==NoStream)continue;
                if(id>=count||id==0||visited[id])throw Bad();visited[id]=true;int e=(int)id*128;
                byte type=entries[e+0x42];int nameBytes=U16(entries,e+0x40);
                if(type is not (1 or 2)||nameBytes<2||nameBytes>64||nameBytes%2!=0)throw Bad();
                string name=Encoding.Unicode.GetString(entries,e,nameBytes-2);
                if(name.Length==0||name.Contains('\0')||streams.ContainsKey(name))throw Bad();
                streams[name]=(type==2?U32(entries,e+0x74):End,type==2?Length(entries,e):ulong.MaxValue);
                stack.Push(U32(entries,e+0x44));stack.Push(U32(entries,e+0x48));
            }
        }
        private ulong Length(byte[] entries,int e)=>v4?BinaryPrimitives.ReadUInt64LittleEndian(entries.AsSpan(e+0x78,8)):U32(entries,e+0x78);
        private long Offset(uint id,int need)
        {
            long offset=((long)id+1)*size;if(offset+need>file.Length)throw Bad();return offset;
        }
        private byte[] Chain(uint start,ulong length,bool small)
        {
            if(length>DocumentText.MaxInputBytes)throw Bad();
            var result=new byte[(int)length];int unit=small?64:size;uint[] table=small?miniFat!:fat;
            long limit=small?(mini!.Length+63)/64:sectors;var seen=new bool[Math.Min(limit,table.Length)];uint at=start;
            for(int done=0;done<result.Length;done+=unit)
            {
                if((done&0xFFFF)==0)token.ThrowIfCancellationRequested();
                if(at>=seen.Length||seen[at])throw Bad();seen[at]=true;int take=Math.Min(unit,result.Length-done);
                if(small){long offset=(long)at*64;if(offset+take>mini!.Length)throw Bad();Array.Copy(mini,offset,result,done,take);}
                else Array.Copy(file,Offset(at,take),result,done,take);
                at=table[at];
            }
            return result;
        }
        internal byte[]? Stream(string name)
        {
            if(!streams.TryGetValue(name,out var entry))return null;if(entry.Length==ulong.MaxValue)throw Bad();
            if(entry.Length>=4096)return Chain(entry.Start,entry.Length,false);
            if(mini is null)
            {
                mini=Chain(rootStart,rootLength,false);
                byte[] table=miniFatCount==0?Array.Empty<byte>():Chain(firstMiniFat,(ulong)miniFatCount*(ulong)size,false);
                miniFat=new uint[table.Length/4];for(int i=0;i<miniFat.Length;i++)miniFat[i]=U32(table,4*i);
            }
            return Chain(entry.Start,entry.Length,true);
        }
    }

    // Property pages (FKPs) give flags for byte ranges of the WordDocument stream.
    private sealed class Properties
    {
        private readonly byte[] word;private readonly bool paragraph;private readonly uint[] bounds,pns;private readonly Func<uint,byte>? huge;
        private uint start=1,end=0;private byte flags;
        internal Properties(byte[] word,byte[] table,uint fc,uint lcb,bool paragraph,Func<uint,byte>? huge=null)
        {this.word=word;this.paragraph=paragraph;this.huge=huge;bounds=Plc(table,fc,lcb,out pns,word);}
        private static uint[] Plc(byte[] table,uint fc,uint lcb,out uint[] pages,byte[] word)
        {
            pages=Array.Empty<uint>();if(lcb==0)return Array.Empty<uint>();
            if(lcb<12||(lcb-4)%8!=0||(ulong)fc+lcb>(ulong)table.Length)throw Bad();
            int n=(int)((lcb-4)/8);var fcs=new uint[n+1];pages=new uint[n];
            for(int i=0;i<=n;i++){fcs[i]=U32(table,fc+4L*i);if(i>0&&fcs[i]<fcs[i-1])throw Bad();}
            for(int i=0;i<n;i++){pages[i]=U32(table,fc+4L*(n+1)+4L*i)&0x3FFFFF;if(((long)pages[i]+1)*512>word.Length)throw Bad();}
            return fcs;
        }
        internal byte At(uint fc)
        {
            if(fc>=start&&fc<end)return flags;
            flags=0;start=0;end=uint.MaxValue;
            int lo=0,hi=bounds.Length-2,page=-1;
            while(lo<=hi){int mid=(lo+hi)/2;if(fc<bounds[mid])hi=mid-1;else if(fc>=bounds[mid+1])lo=mid+1;else{page=mid;break;}}
            if(page<0){start=end=fc;end++;return 0;}
            int p=(int)pns[page]*512;int crun=word[p+511];
            if(crun==0||(paragraph?4*(crun+1)+13*crun:4*(crun+1)+crun)>511)throw Bad();
            uint previous=U32(word,p);
            for(int j=0;j<crun;j++)
            {
                uint next=U32(word,p+4*(j+1));if(next<=previous)throw Bad();
                if(fc>=previous&&fc<next)
                {
                    start=previous;end=next;
                    int b=word[p+4*(crun+1)+(paragraph?13*j:j)];if(b==0)return flags;
                    int off=p+2*b,length,from;
                    if(paragraph){int cb=word[off];if(cb!=0){from=off+1;length=2*cb-1;}else{from=off+2;length=2*word[off+1];}if(length<2)throw Bad();from+=2;length-=2;}
                    else{from=off+1;length=word[off];}
                    if(from+length>p+511)throw Bad();
                    flags=Sprms(word.AsSpan(from,length),huge);return flags;
                }
                previous=next;
            }
            start=end=fc;end++;return 0;
        }
    }
    // huge resolves sprmPHugePapx: properties too large for their page live in the Data stream.
    private static byte Sprms(ReadOnlySpan<byte> g,Func<uint,byte>? huge=null)
    {
        byte flags=0;
        for(int at=0;at+2<=g.Length;)
        {
            int sprm=BinaryPrimitives.ReadUInt16LittleEndian(g[at..]);at+=2;
            int size=(sprm>>13) switch{0 or 1=>1,2 or 4 or 5=>2,3=>4,7=>3,_=>-1};
            if(size<0)
            {
                if(sprm==0xD608){if(at+2>g.Length)break;size=BinaryPrimitives.ReadUInt16LittleEndian(g[at..])+1;}
                else if(sprm==0xC615&&at<g.Length&&g[at]==255)
                {
                    // PChgTabsOperand: cb, then delete/close and add tab lists sized by their counts.
                    if(at+2>g.Length)break;int deleted=g[at+1];int add=at+2+4*deleted;if(add>=g.Length)break;size=1+(1+4*deleted)+(1+3*g[add]);
                }
                else{if(at>=g.Length)break;size=g[at]+1;}
            }
            if(at+size>g.Length)break;byte value=g[at];
            switch(sprm)
            {
                case 0x2417:flags=(byte)(value!=0?flags|Ttp:flags&~Ttp);break;
                case 0x244C:flags=(byte)(value!=0?flags|InnerTtp:flags&~InnerTtp);break;
                case 0x244B:flags=(byte)(value!=0?flags|InnerCell:flags&~InnerCell);break;
                // Toggle 0x80/0x81 depend on the style, which is not evaluated.
                case 0x0803:flags=(byte)(value is 1 or 0x81?flags|Hidden:flags&~Hidden);break;
                case 0x0800:flags=(byte)(value!=0?flags|Deleted:flags&~Deleted);break;
                case 0x0855:flags=(byte)(value!=0?flags|Spec:flags&~Spec);break;
                case 0x6646:if(huge is not null)flags|=huge(BinaryPrimitives.ReadUInt32LittleEndian(g[at..]));break;
            }
            at+=size;
        }
        return flags;
    }

    public static string Read(byte[] bytes,CancellationToken token)
    {
        try{return ReadChecked(bytes,token);}
        catch(Exception error)when(error is ArgumentException or IndexOutOfRangeException or OverflowException or DecoderFallbackException){throw Bad();}
    }
    private static string ReadChecked(byte[] bytes,CancellationToken token)
    {
        if(bytes.Length>DocumentText.MaxInputBytes)throw Bad();
        var compound=new Compound(bytes,token);
        byte[] word=compound.Stream("WordDocument")??throw Bad();
        if(word.Length<0x20)throw Bad();
        ushort ident=U16(word,0),nFib=U16(word,2),bits=U16(word,0x0A);
        if(ident==0xA5DC||ident==0xA5EC&&nFib<0xC0)throw new UserError(Old);
        if(ident!=0xA5EC)throw Bad();
        // fObfuscated only applies together with fEncrypted.
        if((bits&0x0100)!=0)throw new UserError(Encrypted);
        if(word.Length<0x9A||U16(word,0x20)!=14||U16(word,0x3E)!=22)throw Bad();
        int cbRgFcLcb=U16(word,0x98);if(cbRgFcLcb<0x5D||0x9A+8L*cbRgFcLcb>word.Length)throw Bad();
        uint ccpText=U32(word,0x4C);if(ccpText>MaxCp)throw Bad();
        byte[] table=compound.Stream((bits&0x0200)!=0?"1Table":"0Table")??throw Bad();
        uint fcClx=U32(word,0x1A2),lcbClx=U32(word,0x1A6);
        if(lcbClx<5||(ulong)fcClx+lcbClx>(ulong)table.Length)throw Bad();
        long at=fcClx,clxEnd=fcClx+(long)lcbClx;
        while(at<clxEnd&&table[at]==1){int cb=(short)U16(table,at+1);if(cb<0||cb>0x3FA2)throw Bad();at+=3+cb;}
        if(at+5>clxEnd||table[at]!=2)throw Bad();
        uint lcb=U32(table,at+1);at+=5;
        if(lcb<16||(lcb-4)%12!=0||at+lcb>clxEnd)throw Bad();
        int pieces=(int)((lcb-4)/12);var cps=new uint[pieces+1];
        for(int i=0;i<=pieces;i++){cps[i]=U32(table,at+4L*i);if(i==0?cps[i]!=0:cps[i]<=cps[i-1])throw Bad();}
        if(cps[pieces]<ccpText)throw Bad();
        var chpx=new Properties(word,table,U32(word,0x9A+12*8),U32(word,0x9A+12*8+4),false);
        byte[]? data=null;var hugePapx=new Dictionary<uint,byte>();
        byte Huge(uint offset)
        {
            if(hugePapx.TryGetValue(offset,out byte cached))return cached;
            data??=compound.Stream("Data")??throw Bad();int cb=U16(data,offset);
            if(cb>0x3FA2||(long)offset+2+cb>data.Length)throw Bad();
            return hugePapx[offset]=Sprms(data.AsSpan((int)offset+2,cb));
        }
        var papx=new Properties(word,table,U32(word,0x9A+13*8),U32(word,0x9A+13*8+4),true,Huge);
        var text=new StringBuilder();var fields=new Stack<bool>();int code=0;
        void Append(char c){if(text.Length>=DocumentText.MaxCharacters)throw Bad();text.Append(c);}
        void RowEnd(){if(text.Length>0&&text[^1]=='\t')text[^1]='\n';else Append('\n');}
        for(int piece=0;piece<pieces&&cps[piece]<ccpText;piece++)
        {
            uint fc=U32(table,at+4L*(pieces+1)+8L*piece+2);if((fc&0x80000000)!=0)throw Bad();
            bool compressed=(fc&0x40000000)!=0;long first=compressed?(fc&0x3FFFFFFF)/2:fc&0x3FFFFFFF;
            uint count=Math.Min(cps[piece+1],ccpText)-cps[piece];
            if(first+(long)count*(compressed?1:2)>word.Length)throw Bad();
            for(uint i=0;i<count;i++)
            {
                if((i&0xFFF)==0)token.ThrowIfCancellationRequested();
                long position=first+i*(compressed?1L:2L);
                char c=compressed?(word[position] is >=0x80 and <0xA0?Cp1252[word[position]-0x80]:(char)word[position]):(char)U16(word,position);
                // Field structure is followed even in hidden or deleted text.
                if(c==(char)0x13){if(fields.Count>=MaxFields)throw Bad();fields.Push(true);code++;continue;}
                if(c==(char)0x14){if(fields.Count==0||!fields.Peek())throw Bad();fields.Pop();fields.Push(false);code--;continue;}
                if(c==(char)0x15){if(fields.Count==0)throw Bad();if(fields.Pop())code--;continue;}
                byte character=chpx.At((uint)position);
                if(code>0||(character&Deleted)!=0)continue;
                if(c==(char)7){if((papx.At((uint)position)&Ttp)!=0)RowEnd();else Append('\t');continue;}
                if((character&Hidden)!=0)continue;
                // Insert-Symbol characters are stored as '(' plus a font glyph code; there is no portable text for them.
                if(c=='('&&(character&Spec)!=0)continue;
                if(c=='\r'){byte p=papx.At((uint)position);if((p&InnerTtp)!=0)RowEnd();else Append((p&InnerCell)!=0?'\t':'\n');}
                else if(c is (char)0x0B or (char)0x0C or (char)0x0E)Append('\n');
                else if(c=='\t')Append('\t');
                else if(c==(char)0x1E)Append('‑');
                else if(c>=' '&&c is not ((char)0x7F or (>= (char)0x80 and < (char)0xA0)))Append(c);
            }
        }
        if(fields.Count!=0)throw Bad();
        // A lone surrogate in a UTF-16 piece is damage, not text; show it as U+FFFD.
        for(int i=0;i<text.Length;i++)
        {
            if(char.IsHighSurrogate(text[i])&&i+1<text.Length&&char.IsLowSurrogate(text[i+1])){i++;continue;}
            if(char.IsSurrogate(text[i]))text[i]='\uFFFD';
        }
        string result=text.ToString();DocumentText.Validate(result,token);return result;
    }
}
