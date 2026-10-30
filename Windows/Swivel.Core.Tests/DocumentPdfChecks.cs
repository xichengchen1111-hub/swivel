using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Swivel.Core;

internal static class DocumentPdfChecks
{
    public static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        Layout(check,reject);
        Subsetter(check,reject);
        Pdf(check,reject);
        Order(check);
        Probe(check);
    }

    // MARK: layout model, independent of any font

    private static readonly PdfLayoutSettings Small=new(120,60,10,8,10,25,3); // line width 100, 4 lines per page
    private static (int,double)? Fake(int s)=>s=='z'?null:(PdfTextLayout.EastAsian(s)?(1,20):(0,10));
    private static PdfTextLayoutResult Lay(string text,PdfLayoutSettings? settings=null)=>PdfTextLayout.Layout(text,Fake,settings??Small,s=>new UserError(DocumentPdf.Missing,"U+"+s.ToString("X4")),default);
    private static string[] Lines(PdfTextLayoutResult r)=>r.Lines.Select(l=>string.Concat(l.Select(g=>char.ConvertFromUtf32(g.Scalar)))).ToArray();
    private static void Layout(Action<bool,string> check,Action<Action,string> reject)
    {
        check(Small.LinesPerPage==4&&Small.LineWidth==100,"PDF layout fixture: 100pt line, 4 lines per page");
        check(PdfLayoutSettings.A4.LinesPerPage==47&&Math.Abs(PdfLayoutSettings.A4.LineWidth-481.89)<0.01,"PDF A4 text area: 20mm margins, 47 lines of 15.4pt");
        var r=Lay("aaa bbb ccc");
        check(Lines(r).SequenceEqual(new[]{"aaa bbb","ccc"})&&r.Lines[1][0].X==0&&r.Lines[0][4].X==40,"PDF wraps at the last space, trims the trailing space and restarts at the margin");
        check(Lines(Lay("abcdefghijkl")).SequenceEqual(new[]{"abcdefghij","kl"}),"PDF breaks a word wider than the line between characters");
        check(Lines(Lay("abcdefghijabcdefghijabc")).SequenceEqual(new[]{"abcdefghij","abcdefghij","abc"}),"PDF character breaks repeat for very long words");
        check(Lines(Lay("aaaaaaaaaa   bb")).SequenceEqual(new[]{"aaaaaaaaaa","bb"})&&Lay("aaaaaaaaaa   bb").Lines[1][0].X==0,"PDF drops spaces that overflow the line, with the spaces after them");
        check(Lines(Lay("aaaa  bb")).SequenceEqual(new[]{"aaaa  bb"}),"PDF keeps inner spaces that fit");
        var tab=Lay("a\tb\t\tc");
        check(tab.Lines.Count==1&&tab.Lines[0].Select(g=>g.X).SequenceEqual(new float[]{0,25,75}),"PDF tabs advance to the next 25pt stop (tab glyphs are not drawn)");
        var late=Lay("aaaaaaaaaa\tb");
        check(Lines(late).SequenceEqual(new[]{"aaaaaaaaaa","b"})&&late.Lines[1][0].X==25,"PDF tab past the line end continues at the first stop of the next line");
        var only=Lay("\t\t\t\tb");
        check(only.Lines.Count==2&&only.Lines[0].Length==0&&only.Lines[1][0].X==0,"PDF glyph after tabs filling the line wraps instead of overflowing");
        var fill=Lay("aaaaaaaa\tb");
        check(Lines(fill).SequenceEqual(new[]{"aaaaaaaa","b"}),"PDF glyph after a tab to the line end wraps after the tab");
        check(Lines(Lay("中文中文中文")).SequenceEqual(new[]{"中文中文中","文"}),"PDF East Asian text breaks between characters");
        check(Lines(Lay("中文中文中。")).SequenceEqual(new[]{"中文中文","中。"}),"PDF closing punctuation never starts a line");
        check(Lines(Lay("中文中文「中文")).SequenceEqual(new[]{"中文中文","「中文"}),"PDF opening punctuation never ends a line");
        check(Lines(Lay("aaaa中文中文")).SequenceEqual(new[]{"aaaa中文中","文"}),"PDF Latin and East Asian characters may break between each other");
        check(Lines(Lay("a\r\nb\rc\nd\u2028e\u2029f")).SequenceEqual(new[]{"a","b","c","d","e","f"}),"PDF hard breaks: CRLF, CR, LF and Unicode separators");
        check(Lines(Lay("a\n\nb")).SequenceEqual(new[]{"a","","b"})&&Lines(Lay("a\n")).SequenceEqual(new[]{"a"})&&Lay("").Lines.Count==1&&Lay("").Pages==1,"PDF empty paragraphs stay; a final break adds no line; empty text is one blank page");
        check(Lay("1\n2\n3\n4\n5\n6\n7\n8\n9").Pages==3&&Lay("1\n2\n3\n4").Pages==1&&Lay("1\n2\n3\n4\n5").Pages==2,"PDF page breaks every 4 lines in the fixture");
        reject(()=>Lay("1\n2\n3\n4\n5\n6\n7\n8\n9\n10\n11\n12\n13"),"PDF text beyond the page limit is refused before writing");
        check(Lay("1\n2\n3\n4\n5\n6\n7\n8\n9\n10\n11\n12").Pages==3,"PDF exactly at the page limit is accepted");
        var joined=Lay("a‍b️­c");
        check(Lines(joined)[0]=="abc"&&joined.Lines[0].Select(g=>g.X).SequenceEqual(new float[]{0,10,20}),"PDF zero-width joiners, selectors and soft hyphens are not drawn and take no space");
        check(Lines(Lay("aaaaaaa​bbbbbb")).SequenceEqual(new[]{"aaaaaaa","bbbbbb"}),"PDF zero-width space is a break opportunity");
        check(Lines(Lay("ab😀cd")).SequenceEqual(new[]{"ab😀cd"})&&Lay("ab😀cd").Lines[0][3].X==40,"PDF non-BMP scalars lay out as one glyph");
        foreach(string bad in new[]{"שלום","مرحبا","नमस्ते","สวัสดี","é","가","x‮y"})reject(()=>Lay(bad),"PDF refuses complex/RTL scripts and combining marks: "+bad);
        try{Lay("abz");check(false,"PDF missing glyph refused");}catch(UserError error){check(error.Message.Contains("U+007A",StringComparison.Ordinal)&&error.Template==DocumentPdf.Missing,"PDF missing glyph names the character");}
        reject(()=>PdfTextLayout.Layout("a\u0001",Fake,Small,s=>new UserError("x"),default),"PDF layout validates the text first");
        reject(()=>PdfTextLayout.Layout("abc",Fake,Small,s=>new UserError("x"),new CancellationToken(true)),"PDF layout honours cancellation");
    }

    // MARK: a tiny TrueType font built here

    internal sealed record Glyph(byte[] Data,int Advance);
    private static byte[] Simple(short dx)
    {
        var b=new byte[29];BinaryPrimitives.WriteInt16BigEndian(b,1);BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(6),(short)(500+dx));BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(8),700);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(10),2);b[14]=b[15]=b[16]=1;
        short[] xs={dx,(short)(500+dx),(short)(250+dx)},ys={0,0,700};for(int i=0;i<3;i++){BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(17+i*2),xs[i]);BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(23+i*2),ys[i]);}
        return b;
    }
    private static byte[] Composite(params int[] parts)
    {
        var b=new byte[10+parts.Length*8];BinaryPrimitives.WriteInt16BigEndian(b,-1);BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(6),900);BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(8),700);
        for(int i=0;i<parts.Length;i++){int at=10+i*8;BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(at),(ushort)(0x0003|(i<parts.Length-1?0x0020:0)));BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(at+2),(ushort)parts[i]);BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(at+4),(short)(i*400));}
        return b;
    }
    /// Glyphs: 0 notdef, 1 A, 2 B, 3 C = A+B, 4 D (unused), 5 E = C (nested); cmap A..E plus U+1F600 → B and space → empty glyph 6.
    internal static byte[] TinyFont(int fsType=0,string name="SwivelTiny",IReadOnlyDictionary<int,int>? extra=null,Glyph[]? glyphs=null)
    {
        glyphs??=new[]{new Glyph(Simple(0),500),new Glyph(Simple(0),600),new Glyph(Simple(50),650),new Glyph(Composite(1,2),1000),new Glyph(Simple(10),550),new Glyph(Composite(3),1100),new Glyph(Array.Empty<byte>(),250)};
        var map=new SortedDictionary<int,int>{['A']=1,['B']=2,['C']=3,['D']=4,['E']=5,[' ']=6,[0x1F600]=2};
        if(extra is not null)foreach(var x in extra)map[x.Key]=x.Value;
        var glyf=new List<byte>();var loca=new byte[(glyphs.Length+1)*4];var hmtx=new byte[glyphs.Length*4];
        for(int i=0;i<glyphs.Length;i++){BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(i*4),(uint)glyf.Count);glyf.AddRange(glyphs[i].Data);while(glyf.Count%4!=0)glyf.Add(0);BinaryPrimitives.WriteUInt16BigEndian(hmtx.AsSpan(i*4),(ushort)glyphs[i].Advance);}
        BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(glyphs.Length*4),(uint)glyf.Count);
        var head=new byte[54];BinaryPrimitives.WriteUInt32BigEndian(head,0x00010000);BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(12),0x5F0F3CF5);BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(18),1000);
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(38),-200);BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(40),1000);BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(42),900);BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50),1);
        var hhea=new byte[36];BinaryPrimitives.WriteUInt32BigEndian(hhea,0x00010000);BinaryPrimitives.WriteInt16BigEndian(hhea.AsSpan(4),800);BinaryPrimitives.WriteInt16BigEndian(hhea.AsSpan(6),-200);BinaryPrimitives.WriteUInt16BigEndian(hhea.AsSpan(34),(ushort)glyphs.Length);
        var maxp=new byte[32];BinaryPrimitives.WriteUInt32BigEndian(maxp,0x00010000);BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(4),(ushort)glyphs.Length);
        var os2=new byte[96];BinaryPrimitives.WriteUInt16BigEndian(os2,4);BinaryPrimitives.WriteUInt16BigEndian(os2.AsSpan(8),(ushort)fsType);BinaryPrimitives.WriteInt16BigEndian(os2.AsSpan(88),700);
        // cmap: (3,1) format 4 one segment per BMP code, (3,10) format 12 for everything.
        var bmp=map.Where(x=>x.Key<0xFFFF).ToArray();int seg=bmp.Length+1;var f4=new byte[16+seg*8];
        BinaryPrimitives.WriteUInt16BigEndian(f4,4);BinaryPrimitives.WriteUInt16BigEndian(f4.AsSpan(2),(ushort)f4.Length);BinaryPrimitives.WriteUInt16BigEndian(f4.AsSpan(6),(ushort)(seg*2));
        for(int i=0;i<seg;i++){int code=i<bmp.Length?bmp[i].Key:0xFFFF;BinaryPrimitives.WriteUInt16BigEndian(f4.AsSpan(14+i*2),(ushort)code);BinaryPrimitives.WriteUInt16BigEndian(f4.AsSpan(16+seg*2+i*2),(ushort)code);BinaryPrimitives.WriteUInt16BigEndian(f4.AsSpan(16+seg*4+i*2),(ushort)(i<bmp.Length?(bmp[i].Value-code)&0xFFFF:1));}
        var f12=new byte[16+map.Count*12];BinaryPrimitives.WriteUInt16BigEndian(f12,12);BinaryPrimitives.WriteUInt32BigEndian(f12.AsSpan(4),(uint)f12.Length);BinaryPrimitives.WriteUInt32BigEndian(f12.AsSpan(12),(uint)map.Count);
        int k=0;foreach(var x in map){BinaryPrimitives.WriteUInt32BigEndian(f12.AsSpan(16+k*12),(uint)x.Key);BinaryPrimitives.WriteUInt32BigEndian(f12.AsSpan(20+k*12),(uint)x.Key);BinaryPrimitives.WriteUInt32BigEndian(f12.AsSpan(24+k*12),(uint)x.Value);k++;}
        var cmap=new byte[20+f4.Length+f12.Length];BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(2),2);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(4),3);BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(6),1);BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(8),20);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(12),3);BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(14),10);BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(16),(uint)(20+f4.Length));
        f4.CopyTo(cmap,20);f12.CopyTo(cmap,20+f4.Length);
        var nameBytes=Encoding.BigEndianUnicode.GetBytes(name);var nameTable=new byte[18+nameBytes.Length];BinaryPrimitives.WriteUInt16BigEndian(nameTable.AsSpan(2),1);BinaryPrimitives.WriteUInt16BigEndian(nameTable.AsSpan(4),18);
        BinaryPrimitives.WriteUInt16BigEndian(nameTable.AsSpan(6),3);BinaryPrimitives.WriteUInt16BigEndian(nameTable.AsSpan(8),1);BinaryPrimitives.WriteUInt16BigEndian(nameTable.AsSpan(10),0x409);BinaryPrimitives.WriteUInt16BigEndian(nameTable.AsSpan(12),6);BinaryPrimitives.WriteUInt16BigEndian(nameTable.AsSpan(14),(ushort)nameBytes.Length);
        nameBytes.CopyTo(nameTable,18);
        static uint T(string s)=>BinaryPrimitives.ReadUInt32BigEndian(Encoding.ASCII.GetBytes(s));
        return FontFileFormats.Sfnt(new(0x00010000,new List<FontFileFormats.Table>{new(T("head"),head),new(T("hhea"),hhea),new(T("maxp"),maxp),new(T("hmtx"),hmtx),new(T("loca"),loca),new(T("glyf"),glyf.ToArray()),new(T("cmap"),cmap),new(T("OS/2"),os2),new(T("name"),nameTable)}));
    }
    private static byte[] Table(byte[] font,string tag)
    {
        int count=BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));
        for(int i=0;i<count;i++){int at=12+i*16;if(Encoding.ASCII.GetString(font,at,4)==tag)return font.AsSpan((int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(at+8)),(int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(at+12))).ToArray();}
        return Array.Empty<byte>();
    }
    private static byte[] GlyphBytes(byte[] font,int glyph)
    {
        var loca=Table(font,"loca");var glyf=Table(font,"glyf");int a=(int)BinaryPrimitives.ReadUInt32BigEndian(loca.AsSpan(glyph*4)),b=(int)BinaryPrimitives.ReadUInt32BigEndian(loca.AsSpan(glyph*4+4));
        return glyf.AsSpan(a,b-a).ToArray();
    }
    private static byte[] Collection(byte[] font)
    {
        var ttc=new byte[16+font.Length];Encoding.ASCII.GetBytes("ttcf").CopyTo(ttc,0);BinaryPrimitives.WriteUInt32BigEndian(ttc.AsSpan(4),0x00010000);BinaryPrimitives.WriteUInt32BigEndian(ttc.AsSpan(8),1);BinaryPrimitives.WriteUInt32BigEndian(ttc.AsSpan(12),16);
        font.CopyTo(ttc,16);int count=BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));
        for(int i=0;i<count;i++){int at=16+12+i*16+8;BinaryPrimitives.WriteUInt32BigEndian(ttc.AsSpan(at),BinaryPrimitives.ReadUInt32BigEndian(ttc.AsSpan(at))+16);}
        return ttc;
    }
    private static void Subsetter(Action<bool,string> check,Action<Action,string> reject)
    {
        var bytes=TinyFont();var face=TrueTypeFace.Parse(bytes);
        check(face.GlyphCount==7&&face.UnitsPerEm==1000&&face.PostScriptName=="SwivelTiny"&&face.MayEmbedSubset,"Tiny TrueType parses: glyph count, em, PostScript name, installable");
        check(face.Glyph('A')==1&&face.Glyph('E')==5&&face.Glyph(0x1F600)==2&&face.Glyph('Z')==0&&face.Glyph(-1)==0&&face.Glyph(0x110000)==0&&face.Advance(5)==1100,"Tiny TrueType cmap (format 12 preferred) and advances");
        var subset=face.Build(new[]{5},new Dictionary<int,int>{['E']=5},default);
        check(subset.NewGlyphs.OrderBy(x=>x.Key).Select(x=>(x.Key,x.Value)).SequenceEqual(new[]{(0,0),(1,1),(2,2),(3,3),(5,4)}),"Subset keeps notdef and every nested composite dependency, renumbered in original order");
        var sub=TrueTypeFace.Parse(subset.Font);
        check(sub.GlyphCount==5&&sub.Glyph('E')==4&&sub.Glyph('A')==0&&sub.Advance(4)==1100&&sub.Advance(3)==1000&&subset.Advances.SequenceEqual(new[]{500,600,650,1000,1100}),"Subset font parses with its own cmap, metrics and glyph count");
        var e=GlyphBytes(subset.Font,4);var c=GlyphBytes(subset.Font,3);
        check(BinaryPrimitives.ReadUInt16BigEndian(e.AsSpan(12))==3&&BinaryPrimitives.ReadUInt16BigEndian(c.AsSpan(12))==1&&BinaryPrimitives.ReadUInt16BigEndian(c.AsSpan(20))==2,"Subset composite component indices are remapped");
        check(GlyphBytes(subset.Font,1).AsSpan(0,29).SequenceEqual(GlyphBytes(bytes,1).AsSpan(0,29))&&GlyphBytes(subset.Font,2).AsSpan(0,29).SequenceEqual(GlyphBytes(bytes,2).AsSpan(0,29)),"Subset simple glyph outlines are byte-identical");
        check(FontFileFormats.Checksum(subset.Font)==0xB1B0AFBA&&BinaryPrimitives.ReadInt16BigEndian(Table(subset.Font,"head").AsSpan(50))==1&&Table(subset.Font,"OS/2").Length==96&&Table(subset.Font,"post").Length==32,"Subset head checksum adjustment, long loca, copied OS/2 and format 3 post");
        var simple=face.Build(new[]{2},new Dictionary<int,int>{['B']=2,[0x1F600]=2},default);var simpleFace=TrueTypeFace.Parse(simple.Font);
        check(simple.NewGlyphs.Count==2&&simpleFace.Glyph('B')==1&&simpleFace.Glyph(0x1F600)==1,"Subset of one simple glyph keeps only notdef and that glyph; non-BMP cmap entry retained");
        check(TrueTypeFace.Parse(Collection(bytes),0).Glyph('C')==3,"TrueType collection face is read by its offset");
        reject(()=>TrueTypeFace.Parse(Collection(bytes),1),"TrueType collection face out of range refused");
        foreach(var (fs,ok) in new[]{(0,true),(4,true),(8,true),(2,false),(6,true),(0x102,false),(0x100,false),(0x200,false),(0x0204,false)})
        {
            var f=TrueTypeFace.Parse(TinyFont(fs));check(f.MayEmbedSubset==ok,"OS/2 fsType embedding permission: 0x"+fs.ToString("X"));
            if(!ok)reject(()=>f.Build(new[]{1},new Dictionary<int,int>(),default),"Restricted font subset refused: 0x"+fs.ToString("X"));
        }
        var cyclic=TinyFont(glyphs:new[]{new Glyph(Simple(0),500),new Glyph(Composite(1),600)});
        reject(()=>TrueTypeFace.Parse(cyclic).Build(new[]{1},new Dictionary<int,int>(),default),"Composite glyph referring to itself refused");
        var deep=Enumerable.Range(0,20).Select(i=>i==0?new Glyph(Simple(0),500):new Glyph(Composite(i-1),500)).ToArray();
        reject(()=>TrueTypeFace.Parse(TinyFont(glyphs:deep)).Build(new[]{19},new Dictionary<int,int>(),default),"Composite nesting deeper than 16 refused");
        var outside=TinyFont(glyphs:new[]{new Glyph(Simple(0),500),new Glyph(Composite(40),600)});
        reject(()=>TrueTypeFace.Parse(outside).Build(new[]{1},new Dictionary<int,int>(),default),"Composite component beyond the glyph count refused");
        var tiny=new Glyph(new byte[]{0,1,0,0},500);
        reject(()=>TrueTypeFace.Parse(TinyFont(glyphs:new[]{new Glyph(Simple(0),500),tiny})).Build(new[]{1},new Dictionary<int,int>(),default),"Glyph shorter than its header refused");
        reject(()=>TrueTypeFace.Parse(bytes.AsSpan(0,200).ToArray()),"Truncated font refused");
        reject(()=>TrueTypeFace.Parse(new byte[]{0x4F,0x54,0x54,0x4F,0,0,0,0,0,0,0,0}),"CFF (OTTO) font refused");
        reject(()=>TrueTypeFace.Parse(bytes,1),"Face index on a single font refused");
        var broken=(byte[])bytes.Clone();int loca=IndexOf(broken,"loca");BinaryPrimitives.WriteUInt32BigEndian(broken.AsSpan(loca+12),8);
        reject(()=>TrueTypeFace.Parse(broken),"Short loca table refused");
        var badMagic=(byte[])bytes.Clone();int head=(int)BinaryPrimitives.ReadUInt32BigEndian(badMagic.AsSpan(IndexOf(badMagic,"head")+8));badMagic[head+12]^=1;
        reject(()=>TrueTypeFace.Parse(badMagic),"Wrong head magic refused");
        var past=(byte[])bytes.Clone();int lo=(int)BinaryPrimitives.ReadUInt32BigEndian(past.AsSpan(IndexOf(past,"loca")+8));BinaryPrimitives.WriteUInt32BigEndian(past.AsSpan(lo+7*4),0x7FFFFF);
        reject(()=>TrueTypeFace.Parse(past).Build(new[]{6},new Dictionary<int,int>(),default),"loca offset beyond glyf refused");
        reject(()=>face.Build(new[]{1},new Dictionary<int,int>(),new CancellationToken(true)),"Subsetting honours cancellation");
    }
    private static int IndexOf(byte[] font,string tag){int count=BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));for(int i=0;i<count;i++)if(Encoding.ASCII.GetString(font,12+i*16,4)==tag)return 12+i*16;return -1;}

    // MARK: PDF structure

    internal sealed record PdfObject(int Id,string Dictionary,byte[]? Stream);
    /// Validates the classic xref structure and returns the objects with decompressed streams.
    internal static Dictionary<int,PdfObject> Validate(byte[] pdf,Action<bool,string> check,string label)
    {
        string text=Encoding.Latin1.GetString(pdf);
        check(text.StartsWith("%PDF-1.4\n",StringComparison.Ordinal)&&text.EndsWith("%%EOF\n",StringComparison.Ordinal),label+": PDF header and trailer marker");
        var start=Regex.Match(text,@"startxref\n(\d+)\n%%EOF\n$");int xref=int.Parse(start.Groups[1].Value);
        var table=Regex.Match(text[xref..],@"^xref\n0 (\d+)\n");int size=int.Parse(table.Groups[1].Value);
        var trailer=Regex.Match(text,@"trailer\n<< /Size (\d+) /Root 1 0 R >>");
        check(start.Success&&table.Success&&trailer.Success&&int.Parse(trailer.Groups[1].Value)==size,label+": xref and trailer sizes agree");
        int entries=xref+table.Length;var objects=new Dictionary<int,PdfObject>();bool offsetsOk=text.Substring(entries,20)=="0000000000 65535 f \n";
        for(int i=1;i<size;i++)
        {
            string entry=text.Substring(entries+i*20,20);int offset=int.Parse(entry[..10]);offsetsOk&=entry[10..]==" 00000 n \n"&&text.AsSpan(offset).StartsWith($"{i} 0 obj\n");
            int end=text.IndexOf("\nendobj\n",offset,StringComparison.Ordinal);string body=text.Substring(offset+$"{i} 0 obj\n".Length,end-offset-$"{i} 0 obj\n".Length);
            byte[]? stream=null;int s=body.IndexOf(">>\nstream\n",StringComparison.Ordinal);string dict=s<0?body:body[..(s+2)];
            if(s>=0)
            {
                int length=int.Parse(Regex.Match(dict,@"/Length (\d+) >>$").Groups[1].Value),data=offset+$"{i} 0 obj\n".Length+s+">>\nstream\n".Length;
                offsetsOk&=text.Substring(data+length,"\nendstream".Length)=="\nendstream"&&body.EndsWith("\nendstream",StringComparison.Ordinal)&&body.Length-(s+">>\nstream\n".Length)-"\nendstream".Length==length;
                stream=pdf.AsSpan(data,length).ToArray();
                if(dict.Contains("/FlateDecode",StringComparison.Ordinal)){using var z=new ZLibStream(new MemoryStream(stream),CompressionMode.Decompress);using var o=new MemoryStream();z.CopyTo(o);stream=o.ToArray();}
            }
            objects[i]=new(i,dict,stream);
        }
        check(offsetsOk&&Regex.Matches(text,@"(?m)^\d+ 0 obj$").Count==size-1,label+": every xref offset points at its object, stream lengths are exact, object count matches");
        return objects;
    }
    /// The text a reader extracts: each Tj's glyph IDs mapped through its font's ToUnicode CMap.
    internal static string Extract(Dictionary<int,PdfObject> objects)
    {
        var result=new StringBuilder();
        var pages=objects.Values.Where(o=>o.Dictionary.StartsWith("<< /Type /Page ",StringComparison.Ordinal)).OrderBy(o=>o.Id);
        foreach(var page in pages)
        {
            var fonts=Regex.Matches(page.Dictionary,@"/F(\d+) (\d+) 0 R").ToDictionary(m=>m.Groups[1].Value,m=>Cmap(objects,int.Parse(m.Groups[2].Value)));
            string content=Encoding.ASCII.GetString(objects[int.Parse(Regex.Match(page.Dictionary,@"/Contents (\d+) 0 R").Groups[1].Value)].Stream!);
            string font="";double lastY=double.NaN;
            foreach(Match m in Regex.Matches(content,@"/F(\d+) [\d.]+ Tf|1 0 0 1 ([\d.]+) ([\d.]+) Tm <([0-9A-F]*)> Tj"))
            {
                if(m.Groups[1].Success){font=m.Groups[1].Value;continue;}
                double y=double.Parse(m.Groups[3].Value,System.Globalization.CultureInfo.InvariantCulture);if(!double.IsNaN(lastY)&&y!=lastY)result.Append('\n');lastY=y;
                string hex=m.Groups[4].Value;for(int i=0;i<hex.Length;i+=4)result.Append(fonts[font][Convert.ToInt32(hex.Substring(i,4),16)]);
            }
            result.Append('\f');
        }
        return result.ToString();
    }
    private static Dictionary<int,string> Cmap(Dictionary<int,PdfObject> objects,int type0)
    {
        string cmap=Encoding.ASCII.GetString(objects[int.Parse(Regex.Match(objects[type0].Dictionary,@"/ToUnicode (\d+) 0 R").Groups[1].Value)].Stream!);
        return Regex.Matches(cmap,@"<([0-9A-F]{4})> <([0-9A-F]+)>").ToDictionary(m=>Convert.ToInt32(m.Groups[1].Value,16),m=>string.Concat(Enumerable.Range(0,m.Groups[2].Value.Length/4).Select(i=>(char)Convert.ToInt32(m.Groups[2].Value.Substring(i*4,4),16))));
    }
    private static byte[] Render(DocumentPdf pdf){using var stream=new MemoryStream();pdf.Write(stream,default);return stream.ToArray();}
    private static PdfFontCandidate Candidate(byte[] font)=>new("test",_=>TrueTypeFace.Parse(font));
    private static void Pdf(Action<bool,string> check,Action<Action,string> reject)
    {
        int loads=0;
        var restricted=new PdfFontCandidate("restricted",_=>{loads++;return TrueTypeFace.Parse(TinyFont(2,"Restricted"));});
        var absent=new PdfFontCandidate("absent",_=>null);
        var damaged=new PdfFontCandidate("damaged",_=>TrueTypeFace.Parse(new byte[40]));
        var other=TinyFont(0,"SwivelOther",new Dictionary<int,int>{['Z']=4,['中']=1});
        var fonts=new[]{absent,damaged,restricted,Candidate(TinyFont()),Candidate(other)};
        var plan=DocumentPdf.Prepare("ABC E\tA",fonts,default);
        check(plan.Pages==1&&plan.FontNames.SequenceEqual(new[]{"SwivelTiny"})&&loads==1,"PDF skips missing, damaged and restricted-license fonts, loading each at most once");
        var bytes=Render(plan);var objects=Validate(bytes,check,"Tiny PDF");
        check(Extract(objects)=="ABC EA\f","PDF text extracted through ToUnicode equals the laid-out text");
        var type0=objects.Values.Single(o=>o.Dictionary.Contains("/Subtype /Type0",StringComparison.Ordinal));
        check(Regex.IsMatch(type0.Dictionary,@"/BaseFont /[A-Z]{6}\+SwivelTiny /Encoding /Identity-H"),"PDF Type0 font uses a subset tag and Identity-H");
        var cid=objects.Values.Single(o=>o.Dictionary.Contains("/CIDFontType2",StringComparison.Ordinal));
        check(cid.Dictionary.Contains("/CIDToGIDMap /Identity",StringComparison.Ordinal)&&cid.Dictionary.Contains("/W [0 [500 600 650 1000 1100 250]]",StringComparison.Ordinal),"PDF CIDFont widths are the subset advances in 1/1000 em");
        var file=objects.Values.Single(o=>o.Dictionary.Contains("/Length1",StringComparison.Ordinal));
        var embedded=TrueTypeFace.Parse(file.Stream!);
        check(file.Dictionary.Contains($"/Length1 {file.Stream!.Length} ",StringComparison.Ordinal)&&embedded.GlyphCount==6&&embedded.Glyph('E')==4,"PDF FontFile2 is the parsed subset with exact Length1");
        var page=objects.Values.Single(o=>o.Dictionary.StartsWith("<< /Type /Page ",StringComparison.Ordinal));
        string content=Encoding.ASCII.GetString(objects[int.Parse(Regex.Match(page.Dictionary,@"/Contents (\d+) 0 R").Groups[1].Value)].Stream!);
        check(content.Contains("1 0 0 1 56.693 774.197 Tm <0001000200030005000400> Tj",StringComparison.Ordinal)||content.Contains("Tm <00010002000300050004> Tj",StringComparison.Ordinal),"PDF draws one run per contiguous font/position: "+content.Replace("\n"," | "));
        check(content.Contains("1 0 0 1 128.693 774.197 Tm <0001> Tj",StringComparison.Ordinal),"PDF tab moves the next run to the 0.5 inch stop");
        var two=DocumentPdf.Prepare("AZ\n中",fonts,default);var twoObjects=Validate(Render(two),check,"Two-font PDF");
        check(two.FontNames.SequenceEqual(new[]{"SwivelTiny","SwivelOther"})&&Extract(twoObjects)=="AZ\n中\f"&&twoObjects.Count==1+1+10+2,"PDF falls back per character to the next font and embeds both");
        var many=DocumentPdf.Prepare(string.Join("\n",Enumerable.Repeat("A",100)),fonts,default);var manyObjects=Validate(Render(many),check,"Three-page PDF");
        check(many.Pages==3&&Extract(manyObjects).Split('\f').Select(p=>p.Count(c=>c=='A')).SequenceEqual(new[]{47,47,6,0}),"PDF A4 pages hold 47 lines each");
        var empty=DocumentPdf.Prepare("",fonts,default);var emptyObjects=Validate(Render(empty),check,"Empty PDF");
        check(empty.Pages==1&&empty.FontNames.Count==0&&Extract(emptyObjects)=="\f","Empty text writes one blank page without fonts");
        try{DocumentPdf.Prepare("AQ",fonts,default);check(false,"PDF character without any glyph refused");}catch(UserError error){check(error.Message.Contains("U+0051",StringComparison.Ordinal),"PDF character without any glyph refused with its code");}
        reject(()=>DocumentPdf.Prepare("A",new[]{restricted},default),"PDF with only a restricted-license font refused");
        reject(()=>DocumentPdf.Prepare(string.Join("\n",Enumerable.Repeat("A",2001*47)),fonts,default),"PDF beyond 2,000 pages refused in preparation");
        reject(()=>DocumentPdf.Prepare("A",fonts,new CancellationToken(true)),"PDF preparation honours cancellation");
        reject(()=>plan.Write(new MemoryStream(),new CancellationToken(true)),"PDF writing honours cancellation");
        reject(()=>DocumentText.Write(new MemoryStream(),"A",TextExport.Pdf,default),"Generic text writer refuses PDF without prepared fonts");
        var big=string.Join("\n",Enumerable.Range(0,2000).Select(i=>new string('A',150)+" "+new string('E',40)));
        var bigPlan=DocumentPdf.Prepare(big,fonts,default);var bigObjects=Validate(Render(bigPlan),check,"Large PDF");
        check(Extract(bigObjects).Replace("\f","").Replace("\n","")==big.Replace("\n","").Replace(" ","")||Extract(bigObjects).Replace("\f","").Replace("\n","").Replace(" ","")==big.Replace("\n","").Replace(" ",""),"Large PDF keeps every character across wrapped lines and pages");
    }

    private static void Order(Action<bool,string> check)
    {
        var plain=DocumentPdf.FontOrder("Hello 中文","zh-Hans");var kana=DocumentPdf.FontOrder("ひらがな 漢字","en-US");var hangul=DocumentPdf.FontOrder("한국어","zh-Hans");
        check(plain.Take(3).SequenceEqual(new[]{"segoeui.ttf","arial.ttf","msyh.ttc"}),"PDF font order: Segoe UI, Arial, then Microsoft YaHei for Simplified Chinese");
        check(DocumentPdf.FontOrder("中文","zh-Hant")[2]=="msjh.ttc"&&DocumentPdf.FontOrder("漢字","ja")[2]=="YuGothR.ttc","PDF font order follows the UI language for Han characters");
        check(kana[2]=="YuGothR.ttc"&&hangul[2]=="malgun.ttf"&&kana.Distinct().Count()==kana.Count&&kana.Count==11,"PDF font order prefers Japanese fonts for kana and Malgun Gothic for Hangul");
    }

    /// Optional: writes PDFs with this Mac's Arial and Arial Unicode for an independent reader.
    private static void Probe(Action<bool,string> check)
    {
        string? folder=Environment.GetEnvironmentVariable("SWIVEL_PDF_PROBE_DIR");
        const string arial="/System/Library/Fonts/Supplemental/Arial.ttf",unicode="/System/Library/Fonts/Supplemental/Arial Unicode.ttf";
        if(folder is null||!File.Exists(arial)||!File.Exists(unicode))return;
        var fonts=new[]{new PdfFontCandidate("Arial",_=>TrueTypeFace.Parse(File.ReadAllBytes(arial))),new PdfFontCandidate("Arial Unicode",_=>TrueTypeFace.Parse(File.ReadAllBytes(unicode)))};
        var samples=new Dictionary<string,string>
        {
            ["latin"]="Hello, PDF world!\nSecond line with a tab:\tafter tab.\n\n"+string.Join(" ",Enumerable.Repeat("Wrapping words keep going across the whole line width until they wrap.",6)),
            ["cjk"]="中文段落：这是一段用于测试换行的中文文本，它应该在字符之间换行，而不会把句号放在行首。"+string.Concat(Enumerable.Repeat("汉字",60))+"\n日本語のひらがなとカタカナ。\n한국어 문장입니다.\nmix: café © ½",
            ["pages"]=string.Join("\n",Enumerable.Range(1,120).Select(i=>"Line "+i)),
        };
        Directory.CreateDirectory(folder);
        foreach(var (name,text) in samples)
        {
            var plan=DocumentPdf.Prepare(text,fonts,default);var bytes=Render(plan);Validate(bytes,check,"Probe "+name);
            File.WriteAllBytes(Path.Combine(folder,name+".pdf"),bytes);File.WriteAllText(Path.Combine(folder,name+".txt"),text);
        }
        check(true,"PDF probe files written to "+folder);
    }
}
