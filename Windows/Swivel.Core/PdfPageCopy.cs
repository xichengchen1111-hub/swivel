using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Swivel.Core;

public sealed record PdfPageChoice(int Source,int Page);
public sealed record PdfCopyReport(int Pages,int Objects,int DroppedAnnotations,int ClearedReferences,long Bytes);

/// Writes a new PDF from chosen pages of one or more parsed sources. Every page keeps its
/// own objects (content streams, fonts, images, shared resources) copied without decoding,
/// renumbered, with a fresh classic cross-reference table. Only objects reachable from the
/// chosen pages are written. Document-level items are not carried over.
public static class PdfPageCopy
{
    public const int MaxSelected=1000,MaxObjects=500_000;
    public const long MaxOutput=512L*1024*1024;
    public const string Selection="Enter 1–1,000 page numbers or ranges, such as 1,3-5,2. Only ASCII numbers are accepted; repeats and descending ranges are allowed. The listed order is the output order.";
    public const string Fidelity="Each selected page keeps its own text, vector drawings, images, fonts and appearance-only annotations (comments, highlights, stamps, drawings and web links). Not carried over: bookmarks, links to pages, named destinations, form fields, JavaScript and open actions, attachments, tags and accessibility structure, page labels, layers configuration, document information and XMP metadata, and digital signatures. Rotation is added to each page's own rotation. The originals are kept unchanged.";

    private static readonly HashSet<string> SafeAnnotations=new(StringComparer.Ordinal){"Text","FreeText","Line","Square","Circle","Polygon","PolyLine","Highlight","Underline","Squiggly","StrikeOut","Stamp","Caret","Ink","Link"};
    // Page keys that point back into the document, run code, or only make sense with
    // document-level structures that are not copied.
    private static readonly HashSet<string> DroppedPageKeys=new(StringComparer.Ordinal){"Parent","Annots","AA","B","StructParents","Metadata","PieceInfo","Rotate","Thumb","ID","PZ","SeparationInfo","PresSteps","Trans","Dur","Resources","MediaBox","CropBox"};
    private static readonly HashSet<string> DroppedAnnotationKeys=new(StringComparer.Ordinal){"P","Parent","Popup","IRT","AA","A","Dest","PA","StructParent","RT"};

    public static int[] Parse(string text,int count)
    {
        if(count is <1 or >PdfRasterPlan.MaxPages||text.Length is <1 or >512)throw new UserError(Selection);var pages=new List<int>();
        int Number(string s){s=s.Trim(' ','\t');if(s.Length==0||s.Length>4||s.Any(c=>c is <'0' or >'9'))throw new UserError(Selection);int n=int.Parse(s,CultureInfo.InvariantCulture);if(n<1||n>count)throw new UserError(Selection);return n-1;}
        void Add(int n){if(pages.Count>=MaxSelected)throw new UserError(Selection);pages.Add(n);}
        foreach(string part in text.Split(','))
        {
            var range=part.Split('-');if(range.Length==1)Add(Number(part));else if(range.Length==2){int start=Number(range[0]),end=Number(range[1]),step=start<=end?1:-1;for(int n=start;;n+=step){Add(n);if(n==end)break;}}else throw new UserError(Selection);
        }
        return pages.ToArray();
    }

    public static byte[] Write(IReadOnlyList<PdfStructure> sources,IReadOnlyList<PdfPageChoice> order,int turn,CancellationToken token)=>Write(sources,order,turn,token,out _);
    public static byte[] Write(IReadOnlyList<PdfStructure> sources,IReadOnlyList<PdfPageChoice> order,int turn,CancellationToken token,out PdfCopyReport report)
    {
        if(sources.Count is <1 or >10||order.Count is <1 or >MaxSelected||turn is not 0 and not 90 and not 180 and not 270)throw new UserError(Selection);
        foreach(var choice in order)if(choice.Source<0||choice.Source>=sources.Count||choice.Page<0||choice.Page>=sources[choice.Source].Pages.Count)throw new UserError(Selection);
        var numbers=new Dictionary<(int,int),int>();var queue=new Queue<(int Source,int Number)>();
        int next=3+order.Count;var pending=new List<(int Number,Func<PdfValue> Build)>();
        int dropped=0,cleared=0;
        int Allocate(){if(next-3-order.Count>=MaxObjects)throw new UserError(PdfStructure.Limit);return next++;}
        PdfValue Copy(int source,PdfValue value,int depth)
        {
            if(depth>PdfStructure.MaxDepth)throw new UserError(PdfStructure.Limit);
            switch(value.Kind)
            {
                case PdfKind.Ref:
                {
                    var pdf=sources[source];
                    if(pdf.TreeNodes.Contains(value.Number)){cleared++;return PdfValue.Null;}
                    if(pdf.Resolve(value).Kind==PdfKind.Null)return PdfValue.Null;
                    if(!numbers.TryGetValue((source,value.Number),out int n)){n=Allocate();numbers[(source,value.Number)]=n;queue.Enqueue((source,value.Number));}
                    return PdfValue.Ref(n,0);
                }
                case PdfKind.Array:return PdfValue.Array(value.Items.Select(v=>Copy(source,v,depth+1)).ToList());
                case PdfKind.Dict:return PdfValue.Dict(value.Entries.Select(e=>(e.RawKey,e.Key,Copy(source,e.Value,depth+1))).ToList());
                case PdfKind.Stream:
                {
                    var entries=value.Entries.Where(e=>e.Key!="Length").Select(e=>(e.RawKey,e.Key,Copy(source,e.Value,depth+1))).ToList();
                    entries.Insert(0,("/Length"u8.ToArray(),"Length",PdfValue.Int(value.Data.Length)));
                    return PdfValue.Stream(PdfValue.Dict(entries),value.Data);
                }
                default:return value;
            }
        }
        PdfValue? Annotation(int source,PdfValue reference)
        {
            var pdf=sources[source];var annot=pdf.Resolve(reference);
            if(annot.Kind!=PdfKind.Dict||annot.Get("Subtype") is not {Kind:PdfKind.Name} subtype||!SafeAnnotations.Contains(subtype.Text)){dropped++;return null;}
            PdfValue? uri=null;
            if(subtype.Text=="Link")
            {
                var action=pdf.Resolve(annot.Get("A"));
                if(annot.Get("Dest") is not null||action.Kind!=PdfKind.Dict||action.Get("S") is not {Kind:PdfKind.Name,Text:"URI"}||pdf.Resolve(action.Get("URI")) is not {Kind:PdfKind.String} target){dropped++;return null;}
                uri=PdfValue.Dict(new(){("/S"u8.ToArray(),"S",PdfValue.Name("URI")),("/URI"u8.ToArray(),"URI",target)});
            }
            var entries=annot.Entries.Where(e=>!DroppedAnnotationKeys.Contains(e.Key)).Select(e=>(e.RawKey,e.Key,Copy(source,e.Value,1))).ToList();
            if(uri is not null)entries.Add(("/A"u8.ToArray(),"A",uri));
            return PdfValue.Dict(entries);
        }
        var pages=new PdfValue[order.Count];
        for(int i=0;i<order.Count;i++)
        {
            token.ThrowIfCancellationRequested();var (source,index)=(order[i].Source,order[i].Page);var page=sources[source].Pages[index];
            var entries=page.Node.Entries.Where(e=>!DroppedPageKeys.Contains(e.Key)).Select(e=>(e.RawKey,e.Key,Copy(source,e.Value,1))).ToList();
            entries.Add(("/Parent"u8.ToArray(),"Parent",PdfValue.Ref(2,0)));
            entries.Add(("/MediaBox"u8.ToArray(),"MediaBox",Copy(source,page.MediaBoxValue,1)));
            if(page.CropBox is not null)entries.Add(("/CropBox"u8.ToArray(),"CropBox",Copy(source,page.CropBox,1)));
            // A page without Resources inherits none; an empty dictionary says the same explicitly.
            entries.Add(("/Resources"u8.ToArray(),"Resources",page.Resources is null?PdfValue.Dict(new()):Copy(source,page.Resources,1)));
            int rotate=(page.Rotate+turn)%360;if(rotate!=0)entries.Add(("/Rotate"u8.ToArray(),"Rotate",PdfValue.Int(rotate)));
            var annots=sources[source].Resolve(page.Node.Get("Annots"));
            if(annots.Kind==PdfKind.Array)
            {
                var kept=new List<PdfValue>();
                foreach(var item in annots.Items)
                {
                    if(kept.Count>=MaxObjects)throw new UserError(PdfStructure.Limit);
                    var copy=Annotation(source,item);if(copy is null)continue;
                    int n=Allocate();pending.Add((n,()=>copy));kept.Add(PdfValue.Ref(n,0));
                }
                if(kept.Count>0)entries.Add(("/Annots"u8.ToArray(),"Annots",PdfValue.Array(kept)));
            }
            else if(annots.Kind!=PdfKind.Null)dropped++;
            pages[i]=PdfValue.Dict(entries);
        }
        var objects=new Dictionary<int,PdfValue>();
        foreach(var (n,build) in pending)objects[n]=build();
        while(queue.Count>0)
        {
            token.ThrowIfCancellationRequested();var (source,number)=queue.Dequeue();
            var value=sources[source].Resolve(PdfValue.Ref(number,Generation(sources[source],number)));
            objects[numbers[(source,number)]]=Copy(source,value,0);
        }
        using var output=new MemoryStream();
        var offsets=new long[next];
        void Text(string s){output.Write(Encoding.ASCII.GetBytes(s));}
        void Write(PdfValue value)
        {
            switch(value.Kind)
            {
                case PdfKind.Ref:Text($"{value.Number} 0 R");break;
                case PdfKind.Array:Text("[");for(int i=0;i<value.Items.Count;i++){if(i>0)Text(" ");Write(value.Items[i]);}Text("]");break;
                case PdfKind.Dict:case PdfKind.Stream:Text("<<");foreach(var (raw,_,v) in value.Entries){output.Write(raw);Text(" ");Write(v);Text("\n");}Text(">>");break;
                default:output.Write(value.Raw);break;
            }
            if(output.Length>MaxOutput)throw new UserError(PdfStructure.Limit);
        }
        void Object(int number,PdfValue value)
        {
            token.ThrowIfCancellationRequested();offsets[number]=output.Position;Text($"{number} 0 obj\n");Write(value);
            if(value.Kind==PdfKind.Stream){Text("\nstream\n");output.Write(value.Data);Text("\nendstream");}
            Text("\nendobj\n");
        }
        string version=sources.Select(s=>s.Version).Max(StringComparer.Ordinal)!;
        Text($"%PDF-{version}\n");output.Write(new byte[]{37,226,227,207,211,10});
        Object(1,PdfValue.Dict(new(){("/Type"u8.ToArray(),"Type",PdfValue.Name("Catalog")),("/Pages"u8.ToArray(),"Pages",PdfValue.Ref(2,0))}));
        Object(2,PdfValue.Dict(new(){("/Type"u8.ToArray(),"Type",PdfValue.Name("Pages")),("/Count"u8.ToArray(),"Count",PdfValue.Int(order.Count)),("/Kids"u8.ToArray(),"Kids",PdfValue.Array(Enumerable.Range(3,order.Count).Select(n=>PdfValue.Ref(n,0)).ToList()))}));
        for(int i=0;i<order.Count;i++)Object(3+i,pages[i]);
        for(int n=3+order.Count;n<next;n++)Object(n,objects[n]);
        long xref=output.Position;Text($"xref\n0 {next}\n0000000000 65535 f \n");
        for(int n=1;n<next;n++)Text(offsets[n].ToString("D10",CultureInfo.InvariantCulture)+" 00000 n \n");
        Text($"trailer\n<< /Size {next} /Root 1 0 R >>\nstartxref\n{xref.ToString(CultureInfo.InvariantCulture)}\n%%EOF\n");
        if(output.Length>MaxOutput)throw new UserError(PdfStructure.Limit);
        var bytes=output.ToArray();
        Verify(bytes,sources,order,turn,token);
        report=new(order.Count,next-1,dropped,cleared,bytes.LongLength);return bytes;
    }

    private static int Generation(PdfStructure pdf,int number)=>pdf.GenerationOf(number);

    // The written file is read back by the same strict reader: same page count, boxes and rotation.
    private static void Verify(byte[] bytes,IReadOnlyList<PdfStructure> sources,IReadOnlyList<PdfPageChoice> order,int turn,CancellationToken token)
    {
        PdfStructure check;try{check=PdfStructure.Read(bytes,token);}catch(UserError){throw new InvalidDataException("Written PDF did not read back.");}
        if(check.Pages.Count!=order.Count)throw new InvalidDataException("Written PDF page count changed.");
        for(int i=0;i<order.Count;i++)
        {
            var source=sources[order[i].Source].Pages[order[i].Page];var page=check.Pages[i];
            if(!page.MediaBox.SequenceEqual(source.MediaBox)||page.Rotate!=(source.Rotate+turn)%360)throw new InvalidDataException("Written PDF page geometry changed.");
        }
    }
}
