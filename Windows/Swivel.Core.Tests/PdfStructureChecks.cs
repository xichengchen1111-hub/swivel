using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Swivel.Core;

internal static class PdfStructureChecks
{
    // A fixture object: dictionary/value text plus optional raw stream bytes.
    internal sealed record O(int Number,string Body,byte[]? Stream=null);
    internal static byte[] Ascii(string s)=>Encoding.Latin1.GetBytes(s);
    internal static byte[] Zlib(byte[] data){using var m=new MemoryStream();using(var z=new ZLibStream(m,CompressionLevel.Optimal,true))z.Write(data);return m.ToArray();}

    private static void Emit(MemoryStream m,O o,Dictionary<int,long> offsets)
    {
        offsets[o.Number]=m.Position;m.Write(Ascii($"{o.Number} 0 obj\n"));
        if(o.Stream is null)m.Write(Ascii(o.Body));
        else{m.Write(Ascii(o.Body.TrimEnd().TrimEnd('>').TrimEnd('>')+$" /Length {o.Stream.Length} >>\nstream\n"));m.Write(o.Stream);m.Write(Ascii("\nendstream"));}
        m.Write(Ascii("\nendobj\n"));
    }

    /// Classic cross-reference table covering 0..max (absent numbers are free).
    internal static byte[] Classic(IEnumerable<O> objects,string trailerExtra="",string version="1.4")
    {
        var m=new MemoryStream();m.Write(Ascii($"%PDF-{version}\n%âãÏÓ\n"));var offsets=new Dictionary<int,long>();
        foreach(var o in objects)Emit(m,o,offsets);
        int size=offsets.Keys.Max()+1;long start=m.Position;var t=new StringBuilder($"xref\n0 {size}\n0000000000 65535 f \n");
        for(int n=1;n<size;n++)t.Append(offsets.TryGetValue(n,out long at)?$"{at:D10} 00000 n \n":"0000000000 00001 f \n");
        t.Append($"trailer\n<< /Size {size} /Root 1 0 R {trailerExtra}>>\nstartxref\n{start}\n%%EOF\n");m.Write(Ascii(t.ToString()));return m.ToArray();
    }

    /// Appends an incremental update section (classic) listing only the given objects and freed numbers.
    internal static byte[] Update(byte[] previous,IEnumerable<O> objects,int[] freed,int size)
    {
        string text=Encoding.Latin1.GetString(previous);int at=text.LastIndexOf("startxref",StringComparison.Ordinal);long prev=long.Parse(text[(at+9)..].Trim().Split('\n')[0].Trim());
        var m=new MemoryStream();m.Write(previous);var offsets=new Dictionary<int,long>();foreach(var o in objects)Emit(m,o,offsets);
        long start=m.Position;var t=new StringBuilder("xref\n");
        foreach(var (n,offset) in offsets.OrderBy(x=>x.Key))t.Append($"{n} 1\n{offset:D10} 00000 n \n");
        foreach(int n in freed)t.Append($"{n} 1\n0000000000 00001 f \n");
        t.Append($"trailer\n<< /Size {size} /Root 1 0 R /Prev {prev} >>\nstartxref\n{start}\n%%EOF\n");m.Write(Ascii(t.ToString()));return m.ToArray();
    }

    /// Cross-reference stream (W [1 4 2], PNG Up predictor) with the non-stream objects in `packed` placed in one object stream.
    internal static byte[] Compressed(IEnumerable<O> objects,ISet<int> packed,int objStm,bool predictor=true,string trailerExtra="")
    {
        var list=objects.ToList();var m=new MemoryStream();m.Write(Ascii("%PDF-1.5\n%âãÏÓ\n"));var offsets=new Dictionary<int,long>();
        var inside=list.Where(o=>packed.Contains(o.Number)).ToList();
        var header=new StringBuilder();var body=new StringBuilder();
        foreach(var o in inside){header.Append($"{o.Number} {body.Length} ");body.Append(o.Body).Append('\n');}
        string first=header.ToString();var content=Ascii(first+body);
        foreach(var o in list.Where(o=>!packed.Contains(o.Number)))Emit(m,o,offsets);
        Emit(m,new O(objStm,$"<< /Type /ObjStm /N {inside.Count} /First {first.Length} /Filter /FlateDecode >>",Zlib(content)),offsets);
        int xrefNumber=Math.Max(objStm,list.Max(o=>o.Number))+1,size=xrefNumber+1;long start=m.Position;offsets[xrefNumber]=start;
        var rows=new List<byte[]>();
        for(int n=0;n<size;n++)
        {
            var row=new byte[7];int index=inside.FindIndex(o=>o.Number==n);
            if(index>=0){row[0]=2;row[1]=(byte)(objStm>>24);row[2]=(byte)(objStm>>16);row[3]=(byte)(objStm>>8);row[4]=(byte)objStm;row[6]=(byte)index;}
            else if(offsets.TryGetValue(n,out long at)){row[0]=1;for(int k=0;k<4;k++)row[1+k]=(byte)(at>>(24-8*k));}
            rows.Add(row);
        }
        byte[] raw;
        if(predictor){var encoded=new List<byte>();var prev=new byte[7];foreach(var row in rows){encoded.Add(2);for(int k=0;k<7;k++)encoded.Add((byte)(row[k]-prev[k]));prev=row;}raw=encoded.ToArray();}
        else raw=rows.SelectMany(r=>r).ToArray();
        string parms=predictor?" /DecodeParms << /Predictor 12 /Columns 7 >>":"";
        var stream=Zlib(raw);
        m.Write(Ascii($"{xrefNumber} 0 obj\n<< /Type /XRef /Size {size} /W [1 4 2] /Root 1 0 R /Filter /FlateDecode{parms} {trailerExtra}/Length {stream.Length} >>\nstream\n"));m.Write(stream);m.Write(Ascii("\nendstream\nendobj\n"));
        m.Write(Ascii($"startxref\n{start}\n%%EOF\n"));return m.ToArray();
    }

    internal static readonly byte[] Content1=Ascii("BT /F1 24 Tf 72 700 Td (Page one text) Tj ET\n0 0 1 rg 100 100 200 50 re f\n");
    internal static readonly byte[] Content2Raw=Ascii("BT /F1 18 Tf 72 600 Td (Second page) Tj ET\n1 0 0 RG 50 50 m 300 300 l S\n");
    internal static readonly byte[] Content2=Zlib(Content2Raw);
    internal static readonly byte[] Content3=Ascii("BT /F1 12 Tf 72 72 Td (Third page) Tj ET\n");

    /// Catalog with outlines, names, AcroForm, OpenAction and Info; two-level page tree with
    /// inherited MediaBox/Resources/Rotate; shared font; annotations of each class; an
    /// unreachable image and a font with a back-reference to a page.
    internal static List<O> Document()=>new()
    {
        new(1,"<< /Type /Catalog /Pages 2 0 R /Outlines 20 0 R /OpenAction << /S /JavaScript /JS (app.alert\\(1\\)) >> /AcroForm << /Fields [16 0 R] >> /Names << /Dests 21 0 R >> /Metadata 22 0 R >>"),
        new(2,"<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 3 /MediaBox [0 0 612 792] /Resources 10 0 R >>"),
        new(3,"<< /Type /Pages /Parent 2 0 R /Kids [5 0 R 6 0 R] /Count 2 /Rotate 90 >>"),
        new(4,"<< /Type /Page /Parent 2 0 R /Contents 9 0 R /MediaBox [0 0 300 400] /CropBox [10 10 290 390] /Resources << /Font << /F1 11 0 R >> >> >>"),
        new(5,"<< /Type /Page /Parent 3 0 R /Contents 7 0 R /Annots [12 0 R 13 0 R 14 0 R 15 0 R 16 0 R] /AA << /O << /S /JavaScript /JS (x) >> >> /StructParents 0 >>"),
        new(6,"<< /Type /Page /Parent 3 0 R /Contents [8 0 R] /Rotate 180 >>"),
        new(7,"<< >>",Content1),
        new(8,"<< /Filter /FlateDecode >>",Content2),
        new(9,"<< >>",Content3),
        new(10,"<< /Font << /F1 11 0 R >> /ProcSet [/PDF /Text] >>"),
        new(11,"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Owner 5 0 R >>"),
        new(12,"<< /Type /Annot /Subtype /Text /Rect [10 10 30 30] /Contents (Sticky note) /P 5 0 R /Popup 17 0 R >>"),
        new(13,"<< /Type /Annot /Subtype /Link /Rect [0 0 50 50] /Dest [6 0 R /Fit] >>"),
        new(14,"<< /Type /Annot /Subtype /Link /Rect [0 0 60 60] /A << /S /URI /URI (https://example.com/) /Next 23 0 R >> >>"),
        new(15,"<< /Type /Annot /Subtype /Link /Rect [0 0 70 70] /A << /S /Launch /F (calc.exe) >> >>"),
        new(16,"<< /Type /Annot /Subtype /Widget /FT /Tx /T (field) /Rect [0 0 80 80] >>"),
        new(17,"<< /Type /Annot /Subtype /Popup /Parent 12 0 R /Rect [40 40 90 90] >>"),
        new(18,"<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 >>",new byte[]{7}),
        new(19,"<< /Title (UnreachableSecretInfo) /Author (Someone) >>"),
        new(20,"<< /Type /Outlines /First 24 0 R /Count 1 >>"),
        new(21,"<< /Names [(chapter) [6 0 R /Fit]] >>"),
        new(22,"<< /Type /Metadata /Subtype /XML >>",Ascii("<x:xmpmeta>UnreachableXmp</x:xmpmeta>")),
        new(23,"<< /S /JavaScript /JS (UnreachableNextAction) >>"),
        new(24,"<< /Title (UnreachableOutline) /Parent 20 0 R /Dest [5 0 R /Fit] >>"),
    };

    private static PdfStructure Read(byte[] bytes)=>PdfStructure.Read(bytes,default);
    private static byte[] Copy(PdfStructure pdf,int[] order,int turn,out PdfCopyReport report)=>PdfPageCopy.Write(new[]{pdf},order.Select(p=>new PdfPageChoice(0,p)).ToArray(),turn,default,out report);
    private static bool Has(byte[] data,byte[] needle)=>data.AsSpan().IndexOf(needle)>=0;
    private static int Count(byte[] data,byte[] needle){int c=0,at=0;while(true){int i=data.AsSpan(at).IndexOf(needle);if(i<0)return c;c++;at+=i+needle.Length;}}

    // Raw stream bytes of each output page's content, in page order.
    private static byte[][] PageContents(PdfStructure pdf)=>pdf.Pages.Select(p=>
    {
        var c=pdf.Resolve(p.Node.Get("Contents"));if(c.Kind==PdfKind.Array)c=pdf.Resolve(c.Items.Single());return c.Data;
    }).ToArray();

    public static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        // Classic table: page tree, inheritance and every dropped/kept item.
        var classic=Classic(Document(),"/Info 19 0 R ");var source=Read(classic);
        check(source.Pages.Count==3&&source.Version=="1.4","Classic xref page tree reads three leaves");
        check(source.Pages[0].Rotate==90&&source.Pages[1].Rotate==180&&source.Pages[2].Rotate==0,"Rotate inherited from intermediate node, own value overrides");
        check(source.Pages[0].Width==612&&source.Pages[0].Height==792&&source.Pages[2].Width==300&&source.Pages[2].Height==400,"MediaBox inherited from root, own value overrides");
        check(source.Pages[0].Resources?.Kind==PdfKind.Ref&&source.Pages[0].Resources!.Number==10,"Resources inherited by reference");

        var output=Copy(source,new[]{2,0,1,0},90,out var report);var result=Read(output);
        check(result.Pages.Count==4&&report.Pages==4,"Selected order with repeat gives four pages");
        check(result.Pages.Select(p=>p.Rotate).SequenceEqual(new[]{90,180,270,180}),"90-degree turn added to each page's own rotation");
        check(result.Pages[0].MediaBox.SequenceEqual(new double[]{0,0,300,400})&&result.Pages[1].MediaBox.SequenceEqual(new double[]{0,0,612,792}),"MediaBox explicit on every output page");
        check(result.Pages[0].CropBox is not null&&result.Pages[1].CropBox is null,"CropBox kept only where the source page has one");
        var contents=PageContents(result);
        check(contents[0].SequenceEqual(Content3)&&contents[1].SequenceEqual(Content1)&&contents[2].SequenceEqual(Content2)&&contents[3].SequenceEqual(Content1),"Content streams byte-identical in chosen order");
        check(Count(output,Content1)==1,"Repeated page shares one content stream object");
        check(Count(output,Ascii("/BaseFont /Helvetica"))==1,"Shared font written once");
        check(result.Pages[1].Node.Get("Resources")?.Kind==PdfKind.Ref&&result.Pages[3].Node.Get("Resources")?.Number==result.Pages[1].Node.Get("Resources")?.Number,"Shared inherited Resources stay one object");
        foreach(string gone in new[]{"UnreachableSecretInfo","UnreachableXmp","UnreachableNextAction","UnreachableOutline","/Outlines","/AcroForm","/OpenAction","JavaScript","/Widget","calc.exe","/Popup","/StructParents","/Info","/Metadata","/Dests","/Image","/Launch","/Owner 5"})
            check(!Has(output,Ascii(gone)),"Dropped or unreachable item absent: "+gone);
        check(Has(output,Ascii("(Sticky note)"))&&Has(output,Ascii("(https://example.com/)"))&&Has(output,Ascii("/Owner null")),"Text note and web link kept; back-reference to a page cleared");
        var annots=result.Resolve(result.Pages[1].Node.Get("Annots"));
        check(annots.Kind==PdfKind.Array&&annots.Items.Count==2&&report.DroppedAnnotations==6&&report.ClearedReferences==1,"Two of five annotations kept on each copy, others dropped and counted");
        check(result.Resolve(annots.Items[0]).Number!=result.Resolve(result.Resolve(result.Pages[3].Node.Get("Annots")).Items[0]).Number||annots.Items[0].Number!=result.Resolve(result.Pages[3].Node.Get("Annots")).Items[0].Number,"Repeated page gets its own annotation objects");
        check(output.AsSpan().StartsWith("%PDF-1.4\n"u8)&&Encoding.ASCII.GetString(output).Contains("\nxref\n0 ")&&!Has(output,Ascii("/XRef")),"Fresh classic cross-reference table");
        var rendered=PdfPageCopy.Write(new[]{source},new[]{new PdfPageChoice(0,0)},0,default);check(Read(rendered).Pages[0].Rotate==90,"Zero turn keeps inherited rotation explicitly");
        // Deterministic output.
        check(Copy(source,new[]{2,0,1,0},90,out _).SequenceEqual(output),"Same choice gives identical bytes");

        // Cross-reference stream + object stream, with and without PNG predictor.
        foreach(bool predictor in new[]{true,false})
        {
            var packed=new HashSet<int>(Document().Where(o=>o.Stream is null).Select(o=>o.Number));
            var compressed=Compressed(Document(),packed,30,predictor);var cs=Read(compressed);
            check(cs.Pages.Count==3&&cs.Version=="1.5"&&cs.Pages[0].Rotate==90&&cs.Pages[2].Width==300,"Xref stream + object stream page tree (predictor "+predictor+")");
            var co=Copy(cs,new[]{1,2},270,out _);var cr=Read(co);var cc=PageContents(cr);
            check(cc[0].SequenceEqual(Content2)&&cc[1].SequenceEqual(Content3)&&cr.Pages[0].Rotate==90&&cr.Pages[1].Rotate==270,"Object-stream source copies raw content and rotation (predictor "+predictor+")");
            check(!Has(co,Ascii("/ObjStm"))&&!Has(co,Ascii("UnreachableOutline"))&&Has(co,Ascii("/BaseFont /Helvetica")),"Objects unpacked from object stream; unreachable absent");
        }

        // Incremental update: page 1 content replaced, Info freed, Prev chain followed.
        var updatedContent=Ascii("BT /F1 24 Tf 72 700 Td (Updated page one) Tj ET\n");
        var updated=Update(classic,new[]{new O(7,"<< >>",updatedContent),new O(25,"<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>"),new O(10,"<< /Font << /F1 11 0 R /F2 25 0 R >> >>")},new[]{19},26);
        var us=Read(updated);check(us.Updates==2,"Incremental update chain has two sections");
        var uo=Copy(us,new[]{0},0,out _);var ur=Read(uo);
        check(PageContents(ur)[0].SequenceEqual(updatedContent)&&!Has(uo,Content1)&&Has(uo,Ascii("/BaseFont /Courier")),"Newest object versions win; superseded content absent");
        check(us.Load(19,0).Kind==PdfKind.Null,"Freed object in update reads as null");
        // Classic update on top of a compressed base (hybrid chain).
        var cbase=Compressed(Document(),new HashSet<int>{10,11,12,13},30);
        var cupd=Update(cbase,new[]{new O(9,"<< >>",updatedContent)},Array.Empty<int>(),32);var cus=Read(cupd);
        check(PageContents(Read(Copy(cus,new[]{2},0,out _)))[0].SequenceEqual(updatedContent),"Classic update over xref-stream base");

        // Hybrid file: table marks packed objects free, /XRefStm supplies them.
        {
            var objs=Document();var packedSet=new HashSet<int>{10,11};var inner=Compressed(objs,packedSet,30);
            string text=Encoding.Latin1.GetString(inner);int xs=text.LastIndexOf("startxref",StringComparison.Ordinal);long streamAt=long.Parse(text[(xs+9)..].Trim().Split('\n')[0]);
            var cut=inner[..xs];var t=new StringBuilder("xref\n0 1\n0000000000 65535 f \n");
            // Table lists 1..9 normally (offsets read from the xref stream source), 10/11 free.
            var m=new MemoryStream();m.Write(cut);long start=m.Position;
            var stmPdf=Read(inner);
            t.Append("10 2\n0000000000 00001 f \n0000000000 00001 f \n");
            t.Append($"trailer\n<< /Size 32 /Root 1 0 R /XRefStm {streamAt} /Prev {streamAt} >>\nstartxref\n{start}\n%%EOF\n");m.Write(Ascii(t.ToString()));
            var hybrid=m.ToArray();var hs=Read(hybrid);var ho=Copy(hs,new[]{0},0,out _);
            check(stmPdf.Pages.Count==3&&Read(ho).Pages.Count==1&&Has(ho,Ascii("/BaseFont /Helvetica")),"Hybrid /XRefStm supplies objects the table marks free");
        }

        // Several sources renumbered into one file.
        {
            var a=Read(classic);var b=Read(Compressed(Document(),new HashSet<int>{10,11},30));
            var mixed=PdfPageCopy.Write(new[]{a,b},new[]{new PdfPageChoice(1,2),new PdfPageChoice(0,0)},0,default,out var mr);var mo=Read(mixed);
            check(mo.Pages.Count==2&&PageContents(mo)[0].SequenceEqual(Content3)&&PageContents(mo)[1].SequenceEqual(Content1)&&Count(mixed,Ascii("/BaseFont /Helvetica"))==2,"Two sources merged with separate object namespaces");
        }

        // Optional evidence for independent readers outside this suite.
        if(Environment.GetEnvironmentVariable("SWIVEL_PDF_PAGES_OUT") is {Length:>0} dump)
        {
            Directory.CreateDirectory(dump);
            var packedAll=new HashSet<int>(Document().Where(o=>o.Stream is null).Select(o=>o.Number));var cmp=Compressed(Document(),packedAll,30);
            foreach(var (name,bytes) in new[]{("classic",classic),("classic-out",output),("compressed",cmp),("compressed-out",Copy(Read(cmp),new[]{1,2},270,out _)),("updated",updated),("updated-out",uo)})File.WriteAllBytes(Path.Combine(dump,name+".pdf"),bytes);
        }

        // Selection parsing.
        check(PdfPageCopy.Parse("3-1,2,2",3).SequenceEqual(new[]{2,1,0,1,1}),"Descending range and repeats kept in order");
        check(PdfPageCopy.Parse("1-1000",1000).Length==1000,"1,000 selected pages allowed");
        foreach(string bad in new[]{"","0","4","1,,2","1-2-3","１","-1","+1","1 2",new string('1',513)})reject(()=>PdfPageCopy.Parse(bad,3),"Bounded page selection: "+bad);
        reject(()=>PdfPageCopy.Parse("1-1000,1",1000),"More than 1,000 selected pages refused");
        reject(()=>PdfPageCopy.Write(new[]{source},new[]{new PdfPageChoice(0,3)},0,default),"Out-of-range page refused");
        reject(()=>PdfPageCopy.Write(new[]{source},new[]{new PdfPageChoice(0,0)},45,default),"Non-quarter turn refused");

        // Refusals.
        void Refuse(byte[] bytes,string name,string? message=null)
        {
            try{Read(bytes);check(false,name);}catch(UserError e){check(message is null||e.Message==message,name);}
        }
        Refuse(Classic(Document(),"/Encrypt << /Filter /Standard >> "),"Encrypted PDF refused",PdfStructure.Encrypted);
        Refuse(Ascii("%PDF-1.4\nnot a pdf\n%%EOF\n"),"No startxref refused",PdfStructure.Damaged);
        Refuse(Ascii("GIF89a").Concat(classic).ToArray(),"Missing header refused");
        {var bad=(byte[])classic.Clone();int i=Encoding.Latin1.GetString(bad).IndexOf("\n0000000",Encoding.Latin1.GetString(bad).LastIndexOf("xref\n0 ",StringComparison.Ordinal)+10,StringComparison.Ordinal);bad[i+10]=(byte)'9';Refuse(bad,"Shifted xref offset refused (no repair)",PdfStructure.Damaged);}
        {
            string text=Encoding.Latin1.GetString(classic);int xs=text.LastIndexOf("startxref",StringComparison.Ordinal);long at=long.Parse(text[(xs+9)..].Trim().Split('\n')[0]);
            var loop=Ascii(text.Replace("/Size 25 /Root 1 0 R",$"/Size 25 /Root 1 0 R /Prev {at}"));Refuse(loop,"Prev cycle refused",PdfStructure.Damaged);
        }
        {
            var objs=Document();objs[1]=new(2,"<< /Type /Pages /Kids [3 0 R 4 0 R 2 0 R] /MediaBox [0 0 612 792] >>");Refuse(Classic(objs),"Page tree cycle refused",PdfStructure.Damaged);
        }
        {
            var objs=Document();objs[3]=new(4,"<< /Type /Page /Parent 2 0 R /Contents 9 0 R /MediaBox [0 0 0 400] >>");Refuse(Classic(objs),"Zero-size MediaBox refused");
            objs=Document();objs[3]=new(4,"<< /Type /Page /Parent 2 0 R /Contents 9 0 R /Rotate 45 >>");Refuse(Classic(objs),"Non-quarter Rotate refused");
        }
        {
            var many=new List<O>{new(1,"<< /Type /Catalog /Pages 2 0 R >>"),new(2,"<< /Type /Pages /Kids ["+string.Join(" ",Enumerable.Range(4,1001).Select(n=>$"{n} 0 R"))+"] /MediaBox [0 0 100 100] /Resources << >> >>"),new(3,"<< >>",Ascii("q Q"))};
            for(int n=4;n<1005;n++)many.Add(new(n,"<< /Type /Page /Parent 2 0 R /Contents 3 0 R >>"));
            Refuse(Classic(many),"1,001 source pages refused",PdfStructure.Limit);
            many[1]=new(2,"<< /Type /Pages /Kids ["+string.Join(" ",Enumerable.Range(4,1000).Select(n=>$"{n} 0 R"))+"] /MediaBox [0 0 100 100] >>");many.RemoveAt(many.Count-1);
            check(Read(Classic(many)).Pages.Count==1000,"1,000 source pages read");
        }
        {
            string deep=new string('[',70)+new string(']',70);var objs=Document();objs[9]=new(10,"<< /Font << /F1 11 0 R >> /Deep "+deep+" >>");
            var bytes=Classic(objs);try{var p=Read(bytes);p.Load(10,0);check(false,"Nesting bound");}catch(UserError e){check(e.Message==PdfStructure.Limit,"Nesting deeper than 64 refused");}
        }
        {
            var bomb=Zlib(new byte[65*1024*1024]);
            // The catalog (object 2) lives in an object stream that inflates beyond 64 MiB.
            var xs=new MemoryStream();xs.Write(Ascii("%PDF-1.5\n"));long o1=xs.Position;xs.Write(Ascii($"1 0 obj\n<< /Type /ObjStm /N 1 /First 4 /Filter /FlateDecode /Length {bomb.Length} >>\nstream\n"));xs.Write(bomb);xs.Write(Ascii("\nendstream\nendobj\n"));
            long o3=xs.Position;var rows=new byte[]{0,0,0,0,0,0,0, 1,(byte)(o1>>24),(byte)(o1>>16),(byte)(o1>>8),(byte)o1,0,0, 2,0,0,0,1,0,0, 1,(byte)(o3>>24),(byte)(o3>>16),(byte)(o3>>8),(byte)o3,0,0};
            xs.Write(Ascii($"3 0 obj\n<< /Type /XRef /Size 4 /W [1 4 2] /Root 2 0 R /Length {rows.Length} >>\nstream\n"));xs.Write(rows);xs.Write(Ascii("\nendstream\nendobj\n"));xs.Write(Ascii($"startxref\n{o3}\n%%EOF\n"));
            Refuse(xs.ToArray(),"Object stream decompressing beyond 64 MiB refused",PdfStructure.Limit);
        }
        {
            var objs=Document();var packed=new HashSet<int>{10};var lzw=Compressed(objs,packed,30);
            var swapped=Ascii(System.Text.RegularExpressions.Regex.Replace(Encoding.Latin1.GetString(lzw),@"(/Type /ObjStm /N 1 /First \d+ /Filter )/FlateDecode","$1/LZWDecode  "));
            check(!swapped.SequenceEqual(lzw),"LZW fixture altered");
            reject(()=>{var p=Read(swapped);PdfPageCopy.Write(new[]{p},new[]{new PdfPageChoice(0,0)},0,default);},"Unsupported object-stream filter refused");
        }
        // Every truncated prefix is refused (never repaired) and never throws a non-UserError.
        int nonUser=0;
        for(int cut=0;cut<classic.Length;cut+=3)
        {
            try{Read(classic[..cut]);}catch(UserError){}catch{nonUser++;}
        }
        check(nonUser==0,"Truncated prefixes fail only with the plain refusal");
        // Byte mutations: only UserError, or a successful read that also copies cleanly.
        var random=new Random(20261029);int mutations=0,odd=0;
        foreach(var basis in new[]{classic,Compressed(Document(),new HashSet<int>(Document().Where(o=>o.Stream is null).Select(o=>o.Number)),30)})
        for(int i=0;i<1500;i++)
        {
            var bytes=(byte[])basis.Clone();int k=random.Next(1,4);for(int j=0;j<k;j++)bytes[random.Next(bytes.Length)]=(byte)random.Next(256);
            try{var p=Read(bytes);PdfPageCopy.Write(new[]{p},Enumerable.Range(0,p.Pages.Count).Select(n=>new PdfPageChoice(0,n)).ToArray(),90,default);}
            catch(UserError){}catch(Exception e){odd++;Console.Error.WriteLine("PDF mutation: "+e.GetType().Name+" "+e.Message);}
            mutations++;
        }
        check(mutations==3000&&odd==0,"3,000 byte mutations end in success or the plain refusal");
        // Generation mismatch reads as null instead of the wrong object.
        check(source.Load(10,3).Kind==PdfKind.Null&&source.Load(10,0).Kind==PdfKind.Dict,"Generation-mismatched reference reads as null");
        reject(()=>PdfStructure.Read(new byte[PdfStructure.MaxBytes+1],default),"Over 32 MiB refused");
    }
}
