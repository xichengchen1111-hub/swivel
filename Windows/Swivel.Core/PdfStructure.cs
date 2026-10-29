using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;

namespace Swivel.Core;

public enum PdfKind{Null,Bool,Number,Name,String,Array,Dict,Ref,Stream}

/// One parsed PDF value. Atoms keep their exact source bytes so copied strings,
/// names and numbers are written back unchanged; stream data is the raw (still
/// filtered) bytes, so content streams are copied byte for byte.
public sealed class PdfValue
{
    public PdfKind Kind{get;private init;}
    public byte[] Raw{get;private init;}=System.Array.Empty<byte>();
    public string Text{get;private init;}="";
    public List<PdfValue> Items{get;private init;}=new();
    public List<(byte[] RawKey,string Key,PdfValue Value)> Entries{get;private init;}=new();
    public int Number{get;private init;}
    public int Generation{get;private init;}
    public byte[] Data{get;private init;}=System.Array.Empty<byte>();
    public static readonly PdfValue Null=new(){Kind=PdfKind.Null,Raw="null"u8.ToArray(),Text="null"};
    internal static PdfValue Atom(PdfKind kind,byte[] raw,string text)=>new(){Kind=kind,Raw=raw,Text=text};
    internal static PdfValue Array(List<PdfValue> items)=>new(){Kind=PdfKind.Array,Items=items};
    internal static PdfValue Dict(List<(byte[],string,PdfValue)> entries)=>new(){Kind=PdfKind.Dict,Entries=entries};
    internal static PdfValue Ref(int number,int generation)=>new(){Kind=PdfKind.Ref,Number=number,Generation=generation};
    internal static PdfValue Stream(PdfValue dict,byte[] data)=>new(){Kind=PdfKind.Stream,Entries=dict.Entries,Data=data};
    internal static PdfValue Int(long value){string text=value.ToString(CultureInfo.InvariantCulture);return Atom(PdfKind.Number,Encoding.ASCII.GetBytes(text),text);}
    internal static PdfValue Name(string name)=>Atom(PdfKind.Name,Encoding.ASCII.GetBytes("/"+name),name);
    public bool IsDict=>Kind is PdfKind.Dict or PdfKind.Stream;
    public PdfValue? Get(string key){foreach(var e in Entries)if(e.Key==key)return e.Value;return null;}
    public bool TryInt(out long value){value=0;return Kind==PdfKind.Number&&!Text.Contains('.')&&long.TryParse(Text,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out value);}
    public bool TryDouble(out double value){value=0;return Kind==PdfKind.Number&&double.TryParse(Text,NumberStyles.AllowLeadingSign|NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out value)&&double.IsFinite(value);}
}

public sealed record PdfStructurePage(int Index,PdfValue Node,int ObjectNumber,PdfValue? Resources,double[] MediaBox,PdfValue MediaBoxValue,PdfValue? CropBox,int Rotate)
{
    public double Width=>Math.Abs(MediaBox[2]-MediaBox[0]);
    public double Height=>Math.Abs(MediaBox[3]-MediaBox[1]);
}

/// Original bounded reader for unencrypted PDF files: header, classic cross-reference
/// tables, cross-reference streams, hybrid /XRefStm sections, object streams (Flate,
/// optional PNG predictors), /Prev incremental-update chains and the page tree with
/// inherited Resources, MediaBox, CropBox and Rotate. It never repairs: a cross-reference
/// offset that does not hold the object it names, a cycle, an unsupported filter or any
/// bound being exceeded refuses the whole file.
public sealed class PdfStructure
{
    public const long MaxBytes=32L*1024*1024;
    public const int MaxDepth=64,MaxSections=64,MaxEntries=1_000_000,MaxObjectNumber=8_388_607,MaxObjectStreamObjects=100_000;
    public const long MaxStreamDecoded=64L*1024*1024,MaxDecodedTotal=256L*1024*1024;
    public const string Damaged="This PDF's structure could not be read exactly. Damaged files, files needing cross-reference repair and unsupported structures are refused rather than repaired. The original is kept unchanged.";
    public const string Encrypted="Encrypted or password-protected PDFs are not supported. The original is kept unchanged.";
    public const string Limit="Content-preserving PDF pages are limited to 10 files, 32 MiB each and 128 MiB in total, 1,000 source pages, 1,000 selected pages, 1,000,000 cross-reference entries, 64 updates and nesting depth, and 64 MiB per decompressed object or cross-reference stream (256 MiB per file).";

    private readonly byte[] data;private readonly Dictionary<int,(byte Type,long A,int B)> xref=new();
    private readonly Dictionary<int,PdfValue> cache=new();private readonly HashSet<int> loading=new();
    private readonly Dictionary<int,(byte[] Data,int[] Numbers,int[] Offsets,int First)> objectStreams=new();
    private long decoded;private readonly CancellationToken token;
    public string Version{get;private set;}="1.4";
    public PdfValue Trailer{get;private set;}=PdfValue.Null;
    public PdfValue Catalog{get;private set;}=PdfValue.Null;
    public int CatalogNumber{get;private set;}
    public IReadOnlyList<PdfStructurePage> Pages{get;private set;}=System.Array.Empty<PdfStructurePage>();
    /// Object numbers of the catalog and every page-tree node: references to them from
    /// copied content are back-references to the document and are cleared.
    public HashSet<int> TreeNodes{get;}=new();
    public int Updates{get;private set;}

    private PdfStructure(byte[] data,CancellationToken token){this.data=data;this.token=token;}

    public static PdfStructure Read(byte[] data,CancellationToken token)
    {
        if(data.Length>MaxBytes)throw new UserError(Limit);
        var pdf=new PdfStructure(data,token);
        try{pdf.Parse();}
        catch(Exception error) when (error is IndexOutOfRangeException or ArgumentException or FormatException or OverflowException or InvalidDataException or InvalidCastException or KeyNotFoundException){throw new UserError(Damaged);}
        return pdf;
    }

    private static UserError Bad()=>new(Damaged);

    private void Parse()
    {
        if(data.Length<16||!data.AsSpan().StartsWith("%PDF-"u8)||!IsDigit(data[5])||data[6]!='.'||!IsDigit(data[7])||(data.Length>8&&!IsWhite(data[8])&&data[8]!='%'))throw Bad();
        Version=Encoding.ASCII.GetString(data,5,3);
        // Some files carry unrelated bytes after %%EOF; only the last 64 KiB is searched.
        int tail=Math.Max(0,data.Length-65536),at=-1;
        for(int i=data.Length-9;i>=tail;i--)if(data.AsSpan(i).StartsWith("startxref"u8)){at=i;break;}
        if(at<0)throw Bad();
        var lex=new Lexer(data,at+9,this);long start=lex.Integer();
        int eof=IndexOf(data,"%%EOF"u8,lex.Position,data.Length);if(eof<0)throw Bad();
        var visited=new HashSet<long>();long offset=start;
        while(true)
        {
            token.ThrowIfCancellationRequested();
            if(offset<=0||offset>=data.Length||!visited.Add(offset))throw Bad();
            if(visited.Count>MaxSections)throw new UserError(Limit);
            var trailer=Section((int)offset);
            if(Updates==0)Trailer=trailer;Updates++;
            if(trailer.Get("Encrypt") is not null)throw new UserError(Encrypted);
            var prev=trailer.Get("Prev");if(prev is null)break;
            if(!prev.TryInt(out offset))throw Bad();
        }
        var rootRef=Trailer.Get("Root");if(rootRef?.Kind!=PdfKind.Ref)throw Bad();
        CatalogNumber=rootRef.Number;Catalog=Resolve(rootRef);if(Catalog.Kind!=PdfKind.Dict)throw Bad();
        var type=Catalog.Get("Type");if(type is not null&&!(type.Kind==PdfKind.Name&&type.Text=="Catalog"))throw Bad();
        if(Catalog.Get("Version") is {Kind:PdfKind.Name} v&&v.Text.Length==3&&IsDigit((byte)v.Text[0])&&v.Text[1]=='.'&&IsDigit((byte)v.Text[2])&&string.CompareOrdinal(v.Text,Version)>0)Version=v.Text;
        TreeNodes.Add(CatalogNumber);ReadPages();
    }

    // One cross-reference section. Entries already recorded by a newer section win;
    // in a hybrid section the /XRefStm entries replace only free or absent table entries.
    private PdfValue Section(int offset)
    {
        var section=new Dictionary<int,(byte,long,int)>();PdfValue trailer;
        if(data.AsSpan(offset).StartsWith("xref"u8))
        {
            var lex=new Lexer(data,offset+4,this);
            while(true)
            {
                token.ThrowIfCancellationRequested();lex.SkipWhite();
                if(data.AsSpan(lex.Position).StartsWith("trailer"u8)){lex.Position+=7;break;}
                long first=lex.Integer(),count=lex.Integer();
                if(first<0||count<0||first+count>MaxObjectNumber+1L||xref.Count+section.Count+count>MaxEntries)throw new UserError(Limit);
                for(long i=0;i<count;i++)
                {
                    lex.SkipWhite();int p=lex.Position;
                    if(p+18>data.Length)throw Bad();
                    for(int k=0;k<10;k++)if(!IsDigit(data[p+k]))throw Bad();
                    for(int k=11;k<16;k++)if(!IsDigit(data[p+k]))throw Bad();
                    if(data[p+10]!=' '||data[p+16]!=' '||(data[p+17]!='n'&&data[p+17]!='f')||(p+18<data.Length&&!IsWhite(data[p+18])))throw Bad();
                    long a=long.Parse(Encoding.ASCII.GetString(data,p,10),CultureInfo.InvariantCulture);int g=int.Parse(Encoding.ASCII.GetString(data,p+11,5),CultureInfo.InvariantCulture);
                    int number=(int)(first+i);if(!section.ContainsKey(number))section[number]=(data[p+17]=='n'?(byte)1:(byte)0,a,g);lex.Position=p+18;
                }
            }
            trailer=lex.Value(0);if(trailer.Kind!=PdfKind.Dict)throw Bad();
            if(trailer.Get("XRefStm") is {} stm)
            {
                if(!stm.TryInt(out long at)||at<=0||at>=data.Length)throw Bad();
                var extra=new Dictionary<int,(byte,long,int)>();XrefStream((int)at,extra);
                foreach(var (n,e) in extra)if(!section.TryGetValue(n,out var t)||t.Item1==0)section[n]=e;
            }
        }
        else trailer=XrefStream(offset,section);
        foreach(var (n,e) in section)if(!xref.ContainsKey(n)){if(xref.Count>=MaxEntries)throw new UserError(Limit);xref[n]=e;}
        return trailer;
    }

    private PdfValue XrefStream(int offset,Dictionary<int,(byte,long,int)> section)
    {
        var lex=new Lexer(data,offset,this);long number=lex.Integer(),generation=lex.Integer();if(number<1||number>MaxObjectNumber||generation<0||!lex.Keyword("obj"))throw Bad();
        var stream=lex.Value(0,allowStream:true);if(stream.Kind!=PdfKind.Stream||stream.Get("Type") is not {Kind:PdfKind.Name,Text:"XRef"})throw Bad();
        var w=stream.Get("W");if(w?.Kind!=PdfKind.Array||w.Items.Count!=3)throw Bad();
        var widths=new int[3];for(int i=0;i<3;i++){if(!w.Items[i].TryInt(out long x)||x<0||x>8)throw Bad();widths[i]=(int)x;}
        int row=widths.Sum();if(row<1||widths[1]<1)throw Bad();
        if(!(stream.Get("Size")?.TryInt(out long size)??false)||size<0||size>MaxObjectNumber+1L)throw Bad();
        var ranges=new List<(long,long)>();
        if(stream.Get("Index") is {} index)
        {
            if(index.Kind!=PdfKind.Array||index.Items.Count%2!=0||index.Items.Count==0)throw Bad();
            for(int i=0;i<index.Items.Count;i+=2){if(!index.Items[i].TryInt(out long f)||!index.Items[i+1].TryInt(out long c)||f<0||c<0||f+c>MaxObjectNumber+1L)throw Bad();ranges.Add((f,c));}
        }
        else ranges.Add((0,size));
        long total=ranges.Sum(r=>r.Item2);if(total+xref.Count+section.Count>MaxEntries)throw new UserError(Limit);
        var bytes=Decode(stream,row);if(bytes.LongLength<total*row)throw Bad();
        int at=0;
        foreach(var (first,count) in ranges)for(long i=0;i<count;i++)
        {
            long F(int k){long v=0;for(int j=0;j<widths[k];j++)v=(v<<8)|bytes[at++];return v;}
            long type=widths[0]==0?1:F(0),a=F(1),b=F(2);int n=(int)(first+i);
            if(type is 0 or 1 or 2&&!section.ContainsKey(n))
            {
                if(b>int.MaxValue)throw Bad();
                section[n]=((byte)type,a,(int)b);
            }
        }
        return stream;
    }

    // Only Flate (or no filter) is decoded, and only for structure streams.
    private byte[] Decode(PdfValue stream,int? columns=null)
    {
        token.ThrowIfCancellationRequested();
        var filter=stream.Get("Filter");var parms=stream.Get("DecodeParms");
        if(filter?.Kind==PdfKind.Array){if(filter.Items.Count>1)throw Bad();filter=filter.Items.Count==1?filter.Items[0]:null;}
        if(parms?.Kind==PdfKind.Array){if(parms.Items.Count>1)throw Bad();parms=parms.Items.Count==1?parms.Items[0]:null;}
        if(parms?.Kind==PdfKind.Null)parms=null;
        byte[] raw;
        if(filter is null){if(parms is not null)throw Bad();raw=stream.Data;}
        else
        {
            if(filter.Kind!=PdfKind.Name||filter.Text!="FlateDecode")throw Bad();
            using var input=new MemoryStream(stream.Data,false);using var zip=new ZLibStream(input,CompressionMode.Decompress);using var output=new MemoryStream();
            var buffer=new byte[65536];int read;
            while((read=zip.Read(buffer))>0){token.ThrowIfCancellationRequested();if(output.Length+read>MaxStreamDecoded||decoded+output.Length+read>MaxDecodedTotal)throw new UserError(Limit);output.Write(buffer,0,read);}
            raw=output.ToArray();
        }
        decoded+=raw.Length;if(decoded>MaxDecodedTotal)throw new UserError(Limit);
        if(parms is null)return raw;
        if(parms.Kind!=PdfKind.Dict)throw Bad();
        long predictor=1,colors=1,bits=8,cols=1;
        foreach(var (_,key,value) in parms.Entries)
        {
            if(!value.TryInt(out long x))throw Bad();
            switch(key){case "Predictor":predictor=x;break;case "Colors":colors=x;break;case "BitsPerComponent":bits=x;break;case "Columns":cols=x;break;default:throw Bad();}
        }
        if(predictor==1)return raw;
        if(predictor<10||predictor>15||colors!=1||bits!=8||cols<1||cols>64||(columns is int expected&&cols!=expected))throw Bad();
        int width=(int)cols,stride=width+1;if(raw.Length%stride!=0)throw Bad();
        var result=new byte[raw.Length/stride*width];var previous=new byte[width];
        for(int r=0;r<raw.Length/stride;r++)
        {
            int kind=raw[r*stride];var line=result.AsSpan(r*width,width);
            for(int i=0;i<width;i++)
            {
                int x=raw[r*stride+1+i],left=i>0?line[i-1]:0,up=previous[i],ul=i>0?previous[i-1]:0;
                line[i]=(byte)(kind switch{0=>x,1=>x+left,2=>x+up,3=>x+((left+up)>>1),4=>x+Paeth(left,up,ul),_=>throw Bad()});
            }
            line.CopyTo(previous);
        }
        return result;
    }
    private static int Paeth(int a,int b,int c){int p=a+b-c,pa=Math.Abs(p-a),pb=Math.Abs(p-b),pc=Math.Abs(p-c);return pa<=pb&&pa<=pc?a:pb<=pc?b:c;}

    /// The current value of an indirect object, or null for free, missing or
    /// generation-mismatched references (as the format defines).
    public PdfValue Load(int number,int generation)
    {
        token.ThrowIfCancellationRequested();
        if(!xref.TryGetValue(number,out var entry)||entry.Type==0)return PdfValue.Null;
        if(entry.Type==1&&entry.B!=generation||entry.Type==2&&generation!=0)return PdfValue.Null;
        if(cache.TryGetValue(number,out var cached))return cached;
        if(!loading.Add(number)||loading.Count>MaxDepth)throw Bad();
        try
        {
            PdfValue value;
            if(entry.Type==1)
            {
                if(entry.A<=0||entry.A>=data.Length)throw Bad();
                var lex=new Lexer(data,(int)entry.A,this);long n=lex.Integer(),g=lex.Integer();
                if(n!=number||g!=generation||!lex.Keyword("obj"))throw Bad();
                value=lex.Value(0,allowStream:true);lex.SkipWhite();if(!lex.Keyword("endobj"))throw Bad();
            }
            else
            {
                if(entry.A<1||entry.A>MaxObjectNumber)throw Bad();
                var stream=ObjectStream((int)entry.A);if(entry.B<0||entry.B>=stream.Numbers.Length||stream.Numbers[entry.B]!=number)throw Bad();
                var lex=new Lexer(stream.Data,stream.First+stream.Offsets[entry.B],this);value=lex.Value(0);
            }
            cache[number]=value;return value;
        }
        catch(Exception error) when (error is IndexOutOfRangeException or ArgumentException or FormatException or OverflowException or InvalidDataException or InvalidCastException or KeyNotFoundException){throw Bad();}
        finally{loading.Remove(number);}
    }

    private (byte[] Data,int[] Numbers,int[] Offsets,int First) ObjectStream(int number)
    {
        if(objectStreams.TryGetValue(number,out var known))return known;
        if(!xref.TryGetValue(number,out var entry)||entry.Type!=1)throw Bad();
        var stream=Load(number,entry.B);if(stream.Kind!=PdfKind.Stream||stream.Get("Type") is not {Kind:PdfKind.Name,Text:"ObjStm"})throw Bad();
        // /Extends only groups object streams; every object is still found through the cross-reference.
        if(!(stream.Get("N")?.TryInt(out long n)??false)||!(stream.Get("First")?.TryInt(out long first)??false)||n<0||n>MaxObjectStreamObjects||first<0)throw Bad();
        var bytes=Decode(stream);if(first>bytes.Length)throw Bad();
        var lex=new Lexer(bytes,0,this);var numbers=new int[n];var offsets=new int[n];
        for(int i=0;i<n;i++){long o=lex.Integer(),at=lex.Integer();if(o<1||o>MaxObjectNumber||at<0||first+at>=bytes.Length||(i>0&&at<offsets[i-1])||lex.Position>first)throw Bad();numbers[i]=(int)o;offsets[i]=(int)at;}
        var result=(bytes,numbers,offsets,(int)first);objectStreams[number]=result;return result;
    }

    public int GenerationOf(int number)=>xref.TryGetValue(number,out var e)&&e.Type==1?e.B:0;

    public PdfValue Resolve(PdfValue? value)
    {
        value??=PdfValue.Null;
        for(int i=0;value.Kind==PdfKind.Ref;i++){if(i>=16)throw Bad();value=Load(value.Number,value.Generation);}
        return value;
    }

    private void ReadPages()
    {
        var pagesRef=Catalog.Get("Pages");if(pagesRef?.Kind!=PdfKind.Ref)throw Bad();
        var pages=new List<PdfStructurePage>();
        void Walk(PdfValue reference,PdfValue? resources,PdfValue? media,PdfValue? crop,PdfValue? rotate,int depth)
        {
            token.ThrowIfCancellationRequested();
            if(reference.Kind!=PdfKind.Ref||depth>MaxDepth)throw depth>MaxDepth?new UserError(Limit):Bad();
            if(!TreeNodes.Add(reference.Number))throw Bad();
            var node=Resolve(reference);if(node.Kind!=PdfKind.Dict)throw Bad();
            resources=node.Get("Resources")??resources;media=node.Get("MediaBox")??media;crop=node.Get("CropBox")??crop;rotate=node.Get("Rotate")??rotate;
            var type=node.Get("Type");var kids=node.Get("Kids");
            bool isPages=type is {Kind:PdfKind.Name,Text:"Pages"}||type is null&&kids is not null;
            if(isPages)
            {
                kids=Resolve(kids);if(kids.Kind!=PdfKind.Array)throw Bad();
                foreach(var kid in kids.Items)Walk(kid,resources,media,crop,rotate,depth+1);
                return;
            }
            if(type is not null&&!(type.Kind==PdfKind.Name&&type.Text=="Page"))throw Bad();
            if(pages.Count>=PdfRasterPlan.MaxPages)throw new UserError(Limit);
            var box=Box(media)??throw Bad();
            long turn=0;if(rotate is not null&&(!Resolve(rotate).TryInt(out turn)||turn%90!=0))throw Bad();
            if(crop is not null&&Box(crop) is null)crop=null;
            pages.Add(new(pages.Count,node,reference.Number,resources,box,media!,crop,(int)(((turn%360)+360)%360)));
        }
        Walk(pagesRef,null,null,null,null,0);
        if(pages.Count==0)throw Bad();
        Pages=pages.AsReadOnly();
    }

    public double[]? Box(PdfValue? value)
    {
        if(value is null)return null;var array=Resolve(value);if(array.Kind!=PdfKind.Array||array.Items.Count!=4)return null;
        var box=new double[4];for(int i=0;i<4;i++)if(!Resolve(array.Items[i]).TryDouble(out box[i]))return null;
        double w=Math.Abs(box[2]-box[0]),h=Math.Abs(box[3]-box[1]);
        return w>=1&&h>=1&&w<=14400&&h<=14400?box:null;
    }

    internal static bool IsWhite(byte b)=>b is 0 or 9 or 10 or 12 or 13 or 32;
    internal static bool IsDelimiter(byte b)=>b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';
    internal static bool IsDigit(byte b)=>b is >= (byte)'0' and <= (byte)'9';
    private static int IndexOf(byte[] data,ReadOnlySpan<byte> needle,int from,int to){int i=data.AsSpan(from,to-from).IndexOf(needle);return i<0?-1:from+i;}

    private sealed class Lexer(byte[] data,int position,PdfStructure owner)
    {
        public int Position=position;
        public void SkipWhite()
        {
            while(Position<data.Length)
            {
                if(IsWhite(data[Position]))Position++;
                else if(data[Position]=='%'){while(Position<data.Length&&data[Position]!='\n'&&data[Position]!='\r')Position++;}
                else break;
            }
        }
        private string Regular(){SkipWhite();int start=Position;while(Position<data.Length&&!IsWhite(data[Position])&&!IsDelimiter(data[Position]))Position++;if(Position==start)throw Bad();return Encoding.Latin1.GetString(data,start,Position-start);}
        public long Integer(){string s=Regular();if(s.Length>12||!s.All(c=>c is >='0' and <='9'))throw Bad();return long.Parse(s,CultureInfo.InvariantCulture);}
        public bool Keyword(string word){SkipWhite();int at=Position;var bytes=Encoding.ASCII.GetBytes(word);if(!data.AsSpan(at).StartsWith(bytes))return false;int end=at+bytes.Length;if(end<data.Length&&!IsWhite(data[end])&&!IsDelimiter(data[end]))return false;Position=end;return true;}

        public PdfValue Value(int depth,bool allowStream=false)
        {
            if(depth>MaxDepth)throw new UserError(Limit);
            owner.token.ThrowIfCancellationRequested();SkipWhite();if(Position>=data.Length)throw Bad();
            byte b=data[Position];
            if(b=='/')
            {
                int start=Position++;var name=new List<byte>();
                while(Position<data.Length&&!IsWhite(data[Position])&&!IsDelimiter(data[Position]))
                {
                    byte c=data[Position++];
                    if(c=='#'){if(Position+2>data.Length||!byte.TryParse(Encoding.ASCII.GetString(data,Position,2),NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out c)||c==0)throw Bad();Position+=2;}
                    name.Add(c);
                }
                return PdfValue.Atom(PdfKind.Name,data[start..Position],Encoding.Latin1.GetString(name.ToArray()));
            }
            if(b=='(')
            {
                int start=Position++,level=1;
                while(level>0){if(Position>=data.Length)throw Bad();byte c=data[Position++];if(c=='\\')Position++;else if(c=='(')level++;else if(c==')')level--;}
                if(Position>data.Length)throw Bad();
                return PdfValue.Atom(PdfKind.String,data[start..Position],"");
            }
            if(b=='<'&&Position+1<data.Length&&data[Position+1]=='<')
            {
                Position+=2;var entries=new List<(byte[],string,PdfValue)>();
                while(true)
                {
                    SkipWhite();if(Position+1<data.Length&&data[Position]=='>'&&data[Position+1]=='>'){Position+=2;break;}
                    var key=Value(depth+1);if(key.Kind!=PdfKind.Name)throw Bad();
                    var value=Value(depth+1);if(entries.Any(e=>e.Item2==key.Text))throw Bad();
                    // A null value is the same as an absent entry.
                    if(value.Kind!=PdfKind.Null)entries.Add((key.Raw,key.Text,value));
                }
                var dict=PdfValue.Dict(entries);
                if(!allowStream)return dict;
                int after=Position;SkipWhite();
                if(!Keyword("stream")){Position=after;return dict;}
                if(Position<data.Length&&data[Position]=='\r')Position++;
                if(Position<data.Length&&data[Position]=='\n')Position++;
                var lengthValue=dict.Get("Length");if(lengthValue is null)throw Bad();
                if(!owner.Resolve(lengthValue).TryInt(out long length)||length<0||Position+length>data.Length)throw Bad();
                int begin=Position;Position+=(int)length;
                SkipWhite();if(!Keyword("endstream"))throw Bad();
                return PdfValue.Stream(dict,data[begin..(begin+(int)length)]);
            }
            if(b=='<')
            {
                int start=Position++;
                while(true){if(Position>=data.Length)throw Bad();byte c=data[Position++];if(c=='>')break;if(!IsWhite(c)&&!Uri.IsHexDigit((char)c))throw Bad();}
                return PdfValue.Atom(PdfKind.String,data[start..Position],"");
            }
            if(b=='[')
            {
                Position++;var items=new List<PdfValue>();
                while(true){SkipWhite();if(Position<data.Length&&data[Position]==']'){Position++;break;}items.Add(Value(depth+1));}
                return PdfValue.Array(items);
            }
            if(IsDelimiter(b))throw Bad();
            int tokenStart=Position;string text=Regular();
            switch(text)
            {
                case "true":case "false":return PdfValue.Atom(PdfKind.Bool,data[tokenStart..Position],text);
                case "null":return PdfValue.Null;
            }
            if(!IsNumber(text))throw Bad();
            var number=PdfValue.Atom(PdfKind.Number,data[tokenStart..Position],text);
            if(text.All(c=>c is >='0' and <='9')&&text.Length<=7)
            {
                int save=Position;SkipWhite();int g0=Position;
                while(Position<data.Length&&IsDigit(data[Position]))Position++;
                if(Position>g0&&Position-g0<=5&&Position<data.Length&&!IsDelimiter(data[Position])&&IsWhite(data[Position]))
                {
                    int g=int.Parse(Encoding.ASCII.GetString(data,g0,Position-g0),CultureInfo.InvariantCulture);SkipWhite();
                    if(Position<data.Length&&data[Position]=='R'&&(Position+1>=data.Length||IsWhite(data[Position+1])||IsDelimiter(data[Position+1])))
                    {
                        Position++;int n=int.Parse(text,CultureInfo.InvariantCulture);if(n>MaxObjectNumber||g>65535)throw Bad();
                        // Object 0 is always free, so "0 0 R" is a null reference.
                        return n==0?PdfValue.Null:PdfValue.Ref(n,g);
                    }
                }
                Position=save;
            }
            return number;
        }
        private static bool IsNumber(string s)
        {
            int i=0;if(s.Length>0&&(s[0]=='+'||s[0]=='-'))i++;bool digits=false,dot=false;
            for(;i<s.Length;i++){if(s[i]=='.'){if(dot)return false;dot=true;}else if(s[i] is >='0' and <='9')digits=true;else return false;}
            return digits&&s.Length<=128;
        }
    }
}
