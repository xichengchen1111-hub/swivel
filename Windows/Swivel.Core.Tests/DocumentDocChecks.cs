using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using Swivel.Core;

// Hand-built [MS-CFB]/[MS-DOC] fixtures plus two macOS textutil-written files.
internal static class DocumentDocChecks
{
    internal const uint End=0xFFFFFFFE, Free=0xFFFFFFFF, NoStream=0xFFFFFFFF;
    // macOS `textutil -convert doc` output (gzip, base64) for a table and for CJK/emoji text.
    private const string TextutilTable="H4sIAAAAAAACE+2cW0hUQRjH/+fsurte0nUrM7ttJV3oQmVBRIR2sRuUpZD0ZmUpaIoZ5UsIEURPRQ+9BFHUUxBEERFE1lMvUT0kFAQVvXR52IQIgzz9Z86sjqvWbnRR+n7L/5z5ZvbMnDMzhzPfYb998rjw1cXrJa+RwhoE0OdlI2TlOdTMpBEFZpu8Ps/zVFac8oQxxccr982AJmL3/FEu5KA6auAbqHzsIxiBIj0Pqo/6Wo4f43kFP00nadLbS8a6hJ+jvjMB6XOLyqMepBz3hXqPf8dr3lJxd8B+Q02EMNrpNvNJEMYONWhBK8ahDUuxlwqb1LL+VBlTYSzaprZNqMMhtOucZhym3Y5GHt3K/DYqjKNUhy5fy5rb+WnhN8dJR49W+IyPUDFqKjWPWkgtplZRVVQNtdusB3p7Prx92f344f07N6Tv0iZv+5YAdlDrthSPKis3dfznpMyBlSnzoJb6al/aZ9tI2MYH23hgG3dt47ZtDJpUp2zjuG0cs42ODAcjyHVrqVNRaqd4VRMuJ4w1X2dPKnUi3ZV0g1Ce21nUNbNT9ZTjDFK6ecO22Z/62+1l+Rer8vJqrXu72+6lp3LXjg4GRusPzBM36ecJ/yVODAXIobvfyL3LSaU+9OPLP3mu9udD2MYVXBvXcHXaJ3e1t6weHeoxUmUeJc2bsoNDK1+NivIe7yL36k3BetRjP2vxV41xVJk14wG9bUUD8yrZ1kGWprLcvCdQ75n01szaoFnB1Bo7yzeRLA+rM44OqsQvVwduDJ120lsiN/Ly67nwjbMz6nGE+516WVvHkwVWsB43jXqq+aRqxh4eqTqyLM3WK9g9jabzyzI+Y/VSLoglkZPFSDzhSTbo419sbZqjlHypZ7/ic3TvBKLOkNxkTjyARFRunV/kSqgrfBM30YnYkt9fu5qHswZuRY5TRLpcEARBEARBEASfPnrUwZyhXqXKeXXiQk/v9obo1TMRLJh747nyV4pNmVLUOBzvvkk/CoIgCIIgCIIgCMJY9f/dZ4+enV88JXr2HP3/hb3XxP8XBEEQBEEQBEEQhLGJY3z4APwfxSd/QR+G/78O2dznULnwo5tVJGc+VWDKC7mPUePhR9GrSPUiapIpn8x9CTWFmkpNo6ZTM0y50iwr3Sd/yfBXUYEMLToeY4OOvGjLMJyuCFlOsi41h0LZfihEl19cOdwxpVSnSS9FDeqwB02o/6X5m8/Za19POsfon8SbCIZdOrplH9ZzvxeHddjEcBEoI1ECV8VRIZP2YbWfhWrdarOOhunAZra+vz/iRkVXt+ggk5GYx/Zdc++m2350UPupV57Z+axk+5n2f7HVviAIgvBv+A4fAQ09AEwAAA==";
    private const string TextutilCjk="H4sIAAAAAAACE+2cXWgdRRTH//txt0nMTTapjdfUj7VKWzHKFhUi9kqCMdZo02gKvlgwbfOFuYnEVM2L5EFR+lQREUSUFvtUKxXTaivSWLCIoLZi4LYEjYJgRTH2LQ/N+p/Zuc1N0tibUmxjzm85O7MzO3PmnpndnVn23BPfVYzv/qj6Z8ziATiYiorh5aVZlFtyBz5wq0mbiqJIJQWUSFhU/LH3C9OhE5VH416uYKdaquOPUMqwnWAeqtQJaH0xli34d6Ko/KLxHD16v8cc7cHFUedci8I5RCmlHDNhjr/UiMaV409eUoE9fXyasgLC1c63s8aRIFz93Nn8avvBZ9Ivo9kOk/i1vabz3Z294fFwf3g4/Cq0wn3hUcrXocqtf+mHt9X5Nb0dO5N4ajSzf/jLABtPvn/s0++TeNP5jNsnzufOYeeI84EToNqtdeso93OrdRvd0A3wS+Jk4rfEqcTviQBrvAbvPm+dt9Zr8m73kkhn8WM6u3FsffbcTwE60IYMuvkkGOTMIp3tGksG6Wy33neOJaXrLg/mOT8pllgilG5qcvA45cGm1PQ874wY5n8C5+TJ46tRGtpPu7u8kaKJkiDZUj5UcWD5+Ao/VVc94+Tz/S8sSSwxwdKmEuUo4TDoZmjD0ds9QN3fka1C3h6a0Yd+zsTa9Jrc1qts9ehQj5EW8yjJbCh259a9HvV1Z6PdDNVdqQHtek63g/UMcEbXwng/pVPvn0UX0xqpq5e5s9li3hOo90x6b+5abt4MJpEXVzc0259ROH6TpQo87O0qaNhvplEybPRzbFgzwxcYPsEGKlP0Mv9e1mMXUE8rJ7AZbGVJZcC7C9ReT7N0G6MX2uINbGUPnud+gGW3sXSAxxjrpHEH9As6F2HRaylMnGDDu3Sdpx/tWa1k7t3A0scuHN+ak5pLCRxM+HIVXSJ7vZFlwxjGECrDy1+7Gpurpi9L9lORmFwQBEEQBEEQljJTXFG7JXNXlypl/JX3zk5u6vL3vV6EO9Z8fEqtUVImT4lvFhlnzokdBUEQBEEQBEEQBGGxrv/t0W9G37lrpf/GW1z/10x+KOt/QRAEQRAEQRAEQVicWGYN7yD+OF59Ra++lF+G+H8dihmWUK5B7N2s/C7LKOUmvwLKhwBYjtj7XnmqV1GuM/nXM1QuRyspN1BupNxEudnkK1mVF5+Sv2T4T1EODX3aH+Mh7XnRj8EFjZ8qJKxcXWoMecWxS8RInN14oTK3UYZMfB02ow1b0YP2Sxq/ZRy9+b+nkDL6M3jjtfCk9m7ZjgaG27BDO3xcyANlPqphW+oaWoh+5OlPoFVrzWhvmEE8Qu0d5z1ulBtHn3Y2mY+11G+ba7dQ/f4M/bN/+cLaU0v9C7V/Kk+/IAiCcGX4ByiI0QgATAAA";
    internal static byte[] Gunzip(string base64){using var input=new GZipStream(new MemoryStream(Convert.FromBase64String(base64)),CompressionMode.Decompress);using var output=new MemoryStream();input.CopyTo(output);return output.ToArray();}

    // Minimal compound-file writer: streams under 4096 bytes go to the mini
    // stream; padSectors adds free sectors so the FAT needs DIFAT sectors.
    internal static byte[] Compound(IReadOnlyList<(string Name,byte[] Data)> streams,bool v4=false,int padSectors=0,Action<List<uint>,List<byte[]>,byte[]>? tamper=null)
    {
        int ss=v4?4096:512;var sectors=new List<byte[]>();var fat=new List<uint>();
        uint Chain(byte[] data)
        {
            if(data.Length==0)return End;uint start=(uint)sectors.Count;int n=(data.Length+ss-1)/ss;
            for(int i=0;i<n;i++){var sector=new byte[ss];Array.Copy(data,i*ss,sector,0,Math.Min(ss,data.Length-i*ss));sectors.Add(sector);fat.Add(i==n-1?End:(uint)(start+i+1));}
            return start;
        }
        var mini=new MemoryStream();var miniFat=new List<uint>();var placed=new List<(string Name,uint Start,int Length)>();
        foreach(var (name,data) in streams)
        {
            if(data.Length>=4096){placed.Add((name,Chain(data),data.Length));continue;}
            uint start=data.Length==0?End:(uint)(mini.Length/64);int n=(data.Length+63)/64;
            for(int i=0;i<n;i++)miniFat.Add(i==n-1?End:(uint)(start+i+1));
            mini.Write(data);mini.Write(new byte[n*64-data.Length]);placed.Add((name,start,data.Length));
        }
        for(int i=0;i<padSectors;i++){sectors.Add(new byte[ss]);fat.Add(Free);}
        uint miniStart=Chain(mini.ToArray());
        var miniFatBytes=new byte[miniFat.Count*4];for(int i=0;i<miniFat.Count;i++)BinaryPrimitives.WriteUInt32LittleEndian(miniFatBytes.AsSpan(4*i),miniFat[i]);
        uint miniFatStart=Chain(miniFatBytes);
        var dir=new byte[(placed.Count+1)*128];
        void Entry(int index,string name,byte type,uint child,uint right,uint start,long length)
        {
            int e=index*128;byte[] n=Encoding.Unicode.GetBytes(name);Array.Copy(n,0,dir,e,n.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(dir.AsSpan(e+0x40),(ushort)(n.Length+2));dir[e+0x42]=type;dir[e+0x43]=1;
            BinaryPrimitives.WriteUInt32LittleEndian(dir.AsSpan(e+0x44),NoStream);BinaryPrimitives.WriteUInt32LittleEndian(dir.AsSpan(e+0x48),right);
            BinaryPrimitives.WriteUInt32LittleEndian(dir.AsSpan(e+0x4C),child);BinaryPrimitives.WriteUInt32LittleEndian(dir.AsSpan(e+0x74),start);
            BinaryPrimitives.WriteUInt64LittleEndian(dir.AsSpan(e+0x78),(ulong)length);
        }
        Entry(0,"Root Entry",5,placed.Count>0?1u:NoStream,NoStream,miniStart,mini.Length);
        for(int i=0;i<placed.Count;i++)Entry(i+1,placed[i].Name,2,NoStream,i+1<placed.Count?(uint)(i+2):NoStream,placed[i].Start,placed[i].Length);
        uint dirStart=Chain(dir);
        int perSector=ss/4,fatCount=1,difatCount=0;
        for(;;){difatCount=fatCount>109?(fatCount-109+perSector-2)/(perSector-1):0;if((long)fatCount*perSector>=sectors.Count+fatCount+difatCount)break;fatCount++;}
        uint fatFirst=(uint)sectors.Count;for(int i=0;i<fatCount;i++){sectors.Add(new byte[ss]);fat.Add(0xFFFFFFFD);}
        uint difatFirst=(uint)sectors.Count;for(int i=0;i<difatCount;i++){sectors.Add(new byte[ss]);fat.Add(0xFFFFFFFC);}
        tamper?.Invoke(fat,sectors,dir);
        if(tamper is not null){var again=new byte[ss*((dir.Length+ss-1)/ss)];Array.Copy(dir,again,dir.Length);for(int i=0;i*ss<dir.Length;i++)Array.Copy(again,i*ss,sectors[(int)dirStart+i],0,ss);}
        while(fat.Count<fatCount*perSector)fat.Add(Free);
        for(int i=0;i<fat.Count;i++)BinaryPrimitives.WriteUInt32LittleEndian(sectors[(int)fatFirst+i/perSector].AsSpan(4*(i%perSector)),fat[i]);
        var header=new byte[ss];new byte[]{0xD0,0xCF,0x11,0xE0,0xA1,0xB1,0x1A,0xE1}.CopyTo(header,0);
        void H16(int at,int v)=>BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(at),(ushort)v);void H32(int at,uint v)=>BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(at),v);
        H16(0x18,0x3E);H16(0x1A,v4?4:3);H16(0x1C,0xFFFE);H16(0x1E,v4?12:9);H16(0x20,6);
        H32(0x28,v4?(uint)((dir.Length+ss-1)/ss):0);H32(0x2C,(uint)fatCount);H32(0x30,dirStart);H32(0x38,4096);
        H32(0x3C,miniFatStart);H32(0x40,(uint)((miniFatBytes.Length+ss-1)/ss));H32(0x44,difatCount>0?difatFirst:End);H32(0x48,(uint)difatCount);
        for(int i=0;i<109;i++)H32(0x4C+4*i,i<fatCount?fatFirst+(uint)i:Free);
        for(int d=0;d<difatCount;d++)
        {
            var s=sectors[(int)difatFirst+d];
            for(int j=0;j<perSector-1;j++){int k=109+d*(perSector-1)+j;BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(4*j),k<fatCount?fatFirst+(uint)k:Free);}
            BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(ss-4),d+1<difatCount?difatFirst+(uint)d+1:End);
        }
        using var output=new MemoryStream();output.Write(header);foreach(var s in sectors)output.Write(s);return output.ToArray();
    }

    internal sealed record Piece(string Text,bool Compressed);
    internal sealed record PropRun(int FirstCp,int LastCp,byte[] Grpprl);
    // WordDocument with a Word 97 FIB, the pieces stored in reverse order
    // (as fast saves do) and optional character/paragraph property pages.
    internal static byte[] Doc(Piece[] pieces,int? ccpText=null,ushort flags=0x0200,ushort ident=0xA5EC,ushort nFib=0xC1,PropRun[]? chpx=null,PropRun[]? papx=null,bool prc=true,bool v4=false,int padSectors=0,Action<byte[],byte[]>? patch=null,(string,byte[])[]? extra=null,string? tableName=null)
    {
        var word=new MemoryStream();word.Write(new byte[1024]);var fcs=new long[pieces.Length];
        for(int i=pieces.Length-1;i>=0;i--)
        {
            fcs[i]=word.Position;var p=pieces[i];
            word.Write(p.Compressed?p.Text.Select(c=>c switch{'“'=>(byte)0x93,'”'=>(byte)0x94,'€'=>(byte)0x80,'—'=>(byte)0x97,_=>c<256?(byte)c:throw new ArgumentException(p.Text)}).ToArray():Encoding.Unicode.GetBytes(p.Text));
        }
        var cps=new List<int>{0};foreach(var p in pieces)cps.Add(cps[^1]+p.Text.Length);
        long Fc(int cp){int i=0;while(cp>=cps[i+1]&&i<pieces.Length-1)i++;return fcs[i]+(cp-cps[i])*(pieces[i].Compressed?1:2);}
        var table=new MemoryStream();table.Write(new byte[16]);
        (uint,uint) Fkp(PropRun[]? runs,bool paragraph)
        {
            if(runs is null||runs.Length==0)return (0,0);
            while(word.Length%512!=0)word.WriteByte(0);
            var page=new byte[512];int crun=runs.Length,free=511;
            for(int j=0;j<crun;j++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(4*j),(uint)Fc(runs[j].FirstCp));
                byte[] g=runs[j].Grpprl;
                if(paragraph)
                {
                    var papxBytes=new List<byte>{0,0};papxBytes.AddRange(g);if(papxBytes.Count%2==1)papxBytes.Add(0);
                    free-=papxBytes.Count+2;free&=~1;page[free]=0;page[free+1]=(byte)(papxBytes.Count/2);papxBytes.ToArray().CopyTo(page,free+2);
                    page[4*(crun+1)+13*j]=(byte)(free/2);
                }
                else{free-=g.Length+1;free&=~1;page[free]=(byte)g.Length;g.CopyTo(page,free+1);page[4*(crun+1)+j]=(byte)(free/2);}
            }
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(4*crun),(uint)Fc(runs[^1].LastCp));
            page[511]=(byte)crun;uint pn=(uint)(word.Length/512);word.Write(page);
            uint at=(uint)table.Length;var plc=new byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(plc,(uint)Fc(runs[0].FirstCp));BinaryPrimitives.WriteUInt32LittleEndian(plc.AsSpan(4),BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(4*crun)));BinaryPrimitives.WriteUInt32LittleEndian(plc.AsSpan(8),pn);
            table.Write(plc);return (at,12);
        }
        var (fcChpx,lcbChpx)=Fkp(chpx,false);var (fcPapx,lcbPapx)=Fkp(papx,true);
        uint fcClx=(uint)table.Length;if(prc){table.WriteByte(1);table.Write(new byte[]{3,0,0x03,0x08,1});}
        table.WriteByte(2);var pcdt=new byte[4+12*pieces.Length+4];
        for(int i=0;i<=pieces.Length;i++)BinaryPrimitives.WriteUInt32LittleEndian(pcdt.AsSpan(4+4*i),(uint)cps[i]);
        for(int i=0;i<pieces.Length;i++){uint fc=pieces[i].Compressed?(uint)(fcs[i]*2)|0x40000000:(uint)fcs[i];BinaryPrimitives.WriteUInt32LittleEndian(pcdt.AsSpan(4+4*(pieces.Length+1)+8*i+2),fc);}
        BinaryPrimitives.WriteUInt32LittleEndian(pcdt,(uint)(pcdt.Length-4));table.Write(pcdt);uint lcbClx=(uint)table.Length-fcClx;
        while(word.Length<4096)word.WriteByte(0);
        byte[] w=word.ToArray();
        void W16(int at,int v)=>BinaryPrimitives.WriteUInt16LittleEndian(w.AsSpan(at),(ushort)v);void W32(int at,uint v)=>BinaryPrimitives.WriteUInt32LittleEndian(w.AsSpan(at),v);
        W16(0,ident);W16(2,nFib);W16(0x0A,flags);W16(0x20,14);W16(0x3E,22);W32(0x4C,(uint)(ccpText??cps[^1]));W16(0x98,0x5D);
        W32(0x9A+12*8,fcChpx);W32(0x9A+12*8+4,lcbChpx);W32(0x9A+13*8,fcPapx);W32(0x9A+13*8+4,lcbPapx);W32(0x1A2,fcClx);W32(0x1A6,lcbClx);
        byte[] t=table.ToArray();patch?.Invoke(w,t);
        var streams=new List<(string,byte[])>{("WordDocument",w),(tableName??((flags&0x0200)!=0?"1Table":"0Table"),t)};
        if(extra is not null)streams.AddRange(extra);
        return Compound(streams,v4,padSectors);
    }
    private static readonly byte[] HiddenOn={0x03,0x08,1},HiddenStyle={0x03,0x08,0x80},DeletedOn={0x00,0x08,1},TtpOn={0x17,0x24,1};

    public static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        TextDocument Read(byte[] bytes,CancellationToken token=default)=>DocumentText.Read(new MemoryStream(bytes),"DOC",token);
        string Text(byte[] bytes)=>Read(bytes).Text;
        void Refuse(byte[] bytes,string expected,string label)
        {
            try{Read(bytes);check(false,label+" (accepted)");}
            catch(UserError error){check(error.Message==expected,label+": "+error.Message);}
            catch(Exception error){check(false,label+": "+error.GetType().Name+" "+error.Message);}
        }
        string bad0=DocumentDoc.Invalid;
        var table=Read(Gunzip(TextutilTable));
        check(table.Doc&&!table.Docx&&!table.Rtf&&table.Encoding=="Word 97-2003 main document text","DOC flagged and labelled as main document text");
        check(table.Text=="Top\nr1c1\tr1c2\tr1c3\n中\t\tlast\nmulti\npara\tx\ty\nBottom\n","textutil DOC table: cells tab, rows line breaks: "+table.Text.Replace("\n","⏎").Replace("\t","⇥"));
        check(Text(Gunzip(TextutilCjk))=="中文段落一。\n日本語のテキスト、カタカナ。\n繁體中文測試\n한국어 텍스트\nΕλληνικά Кириллица עברית العربية\n😀👍🏽 family 👨\u200d👩\u200d👧\n","textutil DOC CJK, RTL, emoji and ZWJ sequences exact");
        check(DocumentText.Extensions.Contains("doc")&&McpSession.IsDocument(@"C:\a\B.DOC"),"DOC is a document extension for Inspect Document Text and extract_text");

        var mixed=Doc(new[]{new Piece("Quote “a” — 5€\r",true),new Piece("中文 😀 Ünï\r",false),new Piece("tail\r",true)});
        check(Text(mixed)=="Quote “a” — 5€\n中文 😀 Ünï\ntail\n","Compressed CP1252 and UTF-16 pieces in CP order, Prc skipped, reversed storage order");
        check(Text(Doc(new[]{new Piece("main\rFootnote text\r",true)},ccpText:5))=="main\n","Only the main document range (ccpText) is read");
        check(Text(Doc(new[]{new Piece("See \u0013 HYPERLINK \"http://x.invalid\" \u0014the site\u0015 now; \u0013 PAGE \u0015 page \u0013 IF \u0013 DATE \u00142026\u0015 = 1 \u0014outer \u0013 X \u0014inner\u0015 done\u0015.\r",true)}))=="See the site now;  page outer inner done.\n","Field codes skipped, results kept, nested fields, result-less fields omitted");
        check(Text(Doc(new[]{new Piece("a\u000Bb\u000Cc\u000Ed\te\u001Ef\u001Fg\u0001\u0008\u0002\u0005h\u00A0i\u007F\r",true)}))=="a\nb\nc\nd\te\u2011fgh\u00A0i\n","Line/page/column breaks, tab, hyphens, objects and controls mapped");
        check(Text(Doc(new[]{new Piece("a\u0007b\u0007\u0007c\u0007\u0007\u0007after\r",true)},papx:new[]{new PropRun(0,4,Array.Empty<byte>()),new PropRun(4,5,TtpOn),new PropRun(5,8,Array.Empty<byte>()),new PropRun(8,9,TtpOn),new PropRun(9,15,Array.Empty<byte>())}))=="a\tb\nc\t\nafter\n","Table cell marks become tabs and TTP row marks line breaks, including empty last cells");
        check(Text(Doc(new[]{new Piece("Shown hidden style del end\r",true)},chpx:new[]{new PropRun(0,6,Array.Empty<byte>()),new PropRun(6,13,HiddenOn),new PropRun(13,19,HiddenStyle),new PropRun(19,23,DeletedOn),new PropRun(23,27,Array.Empty<byte>())}))=="Shown style end\n","Direct hidden and tracked-deleted characters skipped; style-relative hidden kept");
        var hugeData=new byte[21];hugeData[16]=3;new byte[]{0x17,0x24,1}.CopyTo(hugeData,18);
        check(Text(Doc(new[]{new Piece("a\u0007b\u0007\u0007after\r",true)},papx:new[]{new PropRun(0,4,Array.Empty<byte>()),new PropRun(4,5,new byte[]{0x46,0x66,16,0,0,0}),new PropRun(5,11,Array.Empty<byte>())},extra:new[]{("Data",hugeData)}))=="a\tb\nafter\n","sprmPHugePapx row properties read from the Data stream");
        Refuse(Doc(new[]{new Piece("a\u0007\u0007\r",true)},papx:new[]{new PropRun(0,2,Array.Empty<byte>()),new PropRun(2,3,new byte[]{0x46,0x66,200,0,0,0}),new PropRun(3,4,Array.Empty<byte>())},extra:new[]{("Data",hugeData)}),bad0,"sprmPHugePapx offset outside the Data stream");
        byte[] defTableFirst={0x08,0xD6,5,0,9,9,9,9,0x17,0x24,1},tabsFirst={0x15,0xC6,0xFF,1,9,9,9,9,1,9,9,9,0x17,0x24,1};
        foreach(var (grpprl,label) in new[]{(defTableFirst,"sprmTDefTable"),(tabsFirst,"sprmPChgTabs cb 255")})
            check(Text(Doc(new[]{new Piece("a\u0007\u0007z\r",true)},papx:new[]{new PropRun(0,2,Array.Empty<byte>()),new PropRun(2,3,grpprl),new PropRun(3,5,Array.Empty<byte>())}))=="a\nz\n","Row flag after a variable-size "+label+" operand");
        check(Text(Doc(new[]{new Piece("i1\ri2\r\rafter\r",true)},papx:new[]{new PropRun(0,3,new byte[]{0x4B,0x24,1}),new PropRun(3,6,new byte[]{0x4B,0x24,1}),new PropRun(6,7,new byte[]{0x4C,0x24,1}),new PropRun(7,13,Array.Empty<byte>())}))=="i1\ti2\nafter\n","Nested-table cell and row paragraph marks");
        check(Text(Doc(new[]{new Piece("x(y\r",true)},chpx:new[]{new PropRun(0,1,Array.Empty<byte>()),new PropRun(1,2,new byte[]{0x55,0x08,1,0x09,0x6A,2,0,0x4A,0xF0}),new PropRun(2,4,Array.Empty<byte>())}))=="xy\n","Insert-Symbol characters (fSpec '(') omitted, ordinary '(' kept elsewhere");
        check(Text(Doc(new[]{new Piece("a\uD800b\uDC00\r",false)}))=="a\uFFFDb\uFFFD\n","Lone surrogates in UTF-16 pieces shown as U+FFFD");
        var docx=new MemoryStream();DocumentText.Write(docx,"from docx",TextExport.Docx,default);var renamed=Read(docx.ToArray());
        check(renamed.Docx&&!renamed.Doc&&renamed.Text=="from docx\n","A .doc that is really DOCX uses the DOCX reader");
        check(Text(Doc(new[]{new Piece("x\r",true)},flags:0,extra:new[]{("1Table",new byte[64])}))=="x\n","fWhichTblStm=0 reads 0Table, not 1Table");
        check(Text(Doc(new[]{new Piece("v4 sectors\r",true)},v4:true))=="v4 sectors\n","Version 4 compound file with 4096-byte sectors");
        var big=Doc(new[]{new Piece("difat\r",true)},padSectors:14100);
        check(big.Length<=DocumentText.MaxInputBytes&&Text(big)=="difat\n","FAT located through DIFAT sectors in a large file");
        check(Text(Doc(new[]{new Piece("",true),new Piece("\r",false)}.Skip(1).ToArray()))=="\n","Empty main document paragraph");
        var rtf=Read(Encoding.ASCII.GetBytes(@"{\rtf1\ansi Old RTF\par}"));
        check(rtf.Rtf&&!rtf.Doc&&rtf.Text=="Old RTF\n","A .doc that is really RTF uses the RTF reader");

        Refuse(Doc(new[]{new Piece("secret\r",true)},flags:0x0300),DocumentDoc.Encrypted,"fEncrypted refused");
        Refuse(Doc(new[]{new Piece("secret\r",true)},flags:0x8300),DocumentDoc.Encrypted,"fObfuscated with fEncrypted refused");
        check(Text(Doc(new[]{new Piece("plain\r",true)},flags:0x8200))=="plain\n","fObfuscated alone is ignored, as the FIB specifies");
        check(Text(Doc(new[]{new Piece("template\r",true)},nFib:0xC0))=="template\n","nFib 0x00C0 (Word 97 empty template) is read");
        Refuse(Doc(new[]{new Piece("old\r",true)},nFib:0xBF),DocumentDoc.Old,"nFib below 0x00C0 refused as old");
        Refuse(Doc(new[]{new Piece("old\r",true)},ident:0xA5DC,nFib:0x65),DocumentDoc.Old,"Word 6/95 identifier refused");
        Refuse(Doc(new[]{new Piece("old\r",true)},nFib:0x68),DocumentDoc.Old,"nFib below Word 97 refused");
        string bad=bad0;
        Refuse(new byte[]{0xDB,0xA5,0x2D,0,0,0},bad,"Word 2 / non-compound file refused");
        Refuse(Encoding.UTF8.GetBytes("<html><body>web page saved as .doc</body></html>"),bad,"HTML named .doc refused");
        var good=Doc(new[]{new Piece("good text\r",true)});
        for(int cut=0;cut<good.Length;cut+=good.Length/37+1)Refuse(good[..cut],bad,"Truncated at "+cut);
        Refuse(Doc(new[]{new Piece("x\r",true)},tableName:"Data"),bad,"Missing table stream");
        Refuse(Compound(new[]{("1Table",new byte[64])}),bad,"Missing WordDocument stream");
        Refuse(Compound(new[]{("WordDocument",new byte[5000]),("WORDDOCUMENT",new byte[10])}),bad,"Duplicate stream names (case-insensitive)");
        Refuse(Compound(new[]{("WordDocument",new byte[5000])},tamper:(fat,s,d)=>{fat[1]=0;}),bad,"FAT chain cycle");
        Refuse(Compound(new[]{("WordDocument",new byte[5000])},tamper:(fat,s,d)=>{fat[2]=0x7FFFFFFF;}),bad,"FAT entry past end of file");
        Refuse(Compound(new[]{("WordDocument",new byte[5000]),("1Table",new byte[10])},tamper:(fat,s,d)=>BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(2*128+0x48),1)),bad,"Directory sibling cycle");
        Refuse(Compound(new[]{("WordDocument",new byte[5000])},tamper:(fat,s,d)=>BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(128+0x78),9_000_000)),bad,"Stream longer than the 8 MiB bound");
        Refuse(Doc(new[]{new Piece("x\r",true)},patch:(w,t)=>BinaryPrimitives.WriteUInt32LittleEndian(w.AsSpan(0x4C),100)),bad,"ccpText past the last piece");
        Refuse(Doc(new[]{new Piece("x\r",true)},patch:(w,t)=>BinaryPrimitives.WriteUInt32LittleEndian(w.AsSpan(0x4C),20_000_000)),bad,"ccpText above the CP bound");
        Refuse(Doc(new[]{new Piece("x\r",true)},patch:(w,t)=>{int clx=(int)BinaryPrimitives.ReadUInt32LittleEndian(w.AsSpan(0x1A2));BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(clx+6+5+4+4+2),0x40000000|(9_000_000*2));}),bad,"Piece outside WordDocument");
        Refuse(Doc(new[]{new Piece("ab",true),new Piece("c\r",true)},patch:(w,t)=>{int clx=(int)BinaryPrimitives.ReadUInt32LittleEndian(w.AsSpan(0x1A2));BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(clx+6+5+4),0);}),bad,"Piece CPs not increasing");
        Refuse(Doc(new[]{new Piece("x\r",true)},patch:(w,t)=>BinaryPrimitives.WriteUInt16LittleEndian(w.AsSpan(0x20),13)),bad,"Unexpected FIB csw");
        Refuse(Doc(new[]{new Piece("a \u0014 b\r",true)}),bad,"Field separator without begin");
        Refuse(Doc(new[]{new Piece("a \u0013 code never ends\r",true)}),bad,"Unterminated field");
        Refuse(Doc(new[]{new Piece(string.Concat(Enumerable.Repeat("\u0013",129))+"x"+string.Concat(Enumerable.Repeat("\u0015",129))+"\r",true)}),bad,"More than 128 nested fields");
        Refuse(Doc(new[]{new Piece("x\r",true)},chpx:new[]{new PropRun(0,2,HiddenOn)},patch:(w,t)=>{int p=(int)BinaryPrimitives.ReadUInt32LittleEndian(t.AsSpan(24))*512;w[p+511]=0;}),bad,"Character property page with no runs");
        var shifted=Compound(new[]{("WordDocument",new byte[5000])});shifted[0x1E]=12;Refuse(shifted,bad,"Version 3 header with 4096-byte sector shift");
        string chunk=new('a',1_000_000);
        Refuse(Doc(new[]{new Piece(chunk,true),new Piece(chunk,true),new Piece(chunk+"\r",true)}),bad,"Output above 2,000,000 UTF-16 units");
        Refuse(new byte[DocumentText.MaxInputBytes+1],"Choose a valid UTF-8 text file, BOM-marked UTF-16 text file, DOC, DOCX, ODT, RTF, EPUB or supported static HTML document. At most 8 MiB input and 2,000,000 UTF-16 units of extracted text are supported.","Input above 8 MiB");
        reject(()=>Read(good,new CancellationToken(true)),"DOC read honours pre-cancellation");

        // Mutation and truncation fuzzing: only text or a UserError, never another exception.
        var rng=new Random(1997);int accepted=0,refused=0;
        byte[][] seeds={good,Gunzip(TextutilTable),Doc(new[]{new Piece("f\u0013 c \u0014r\u0015\u0007\u0007\r",true)},chpx:new[]{new PropRun(0,3,HiddenOn)},papx:new[]{new PropRun(0,9,TtpOn)})};
        for(int round=0;round<3000;round++)
        {
            byte[] sample=(byte[])seeds[round%seeds.Length].Clone();int edits=1+rng.Next(8);
            for(int e=0;e<edits;e++){int at=rng.Next(2)==0?rng.Next(Math.Min(4096,sample.Length)):rng.Next(sample.Length);sample[at]=rng.Next(3)==0?(byte)0xFF:(byte)rng.Next(256);}
            try{var r=Read(sample);DocumentText.Validate(r.Text,default);accepted++;}
            catch(UserError){refused++;}
            catch(Exception error){check(false,"DOC fuzz round "+round+": "+error.GetType().Name+" "+error.Message);break;}
        }
        check(accepted+refused==3000&&refused>0,"DOC fuzzing: "+accepted+" accepted, "+refused+" refused, no other exceptions");
    }
}
