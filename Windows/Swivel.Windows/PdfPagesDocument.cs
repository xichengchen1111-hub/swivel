using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

public sealed record PdfPagesRow(int Number,string Path,int Page,double Width,double Height,int Rotate);

/// Holds every chosen PDF open (no writers, no deletes) from parsing until the new file is
/// published beside the first one. Parsing is the core structure reader; no Windows PDF
/// renderer and no package identity is involved, so this also works from the plain ZIP.
public sealed class PdfPagesDocument:IDisposable
{
    private readonly List<NativeFiles.Input> inputs=new();private readonly List<PdfStructure> sources=new();
    private readonly List<PdfPagesRow> rows=new();private readonly List<PdfPageChoice> choices=new();
    public IReadOnlyList<PdfPagesRow> Pages=>rows.AsReadOnly();
    private PdfPagesDocument(){}

    public static PdfPagesDocument Open(IReadOnlyList<string> paths,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();if(paths.Count is <1 or >10)throw new UserError(PdfStructure.Limit);
        var work=new PdfPagesDocument();long total=0;var budget=new PdfBudget();
        try
        {
            foreach(string path in paths)
            {
                token.ThrowIfCancellationRequested();var input=NativeFiles.OpenRead(path);work.inputs.Add(input);
                long length=input.Stream.Length;total+=length;
                if(length>PdfStructure.MaxBytes||total>128L*1024*1024)throw new UserError(PdfStructure.Limit);
                var bytes=new byte[length];input.Stream.Position=0;
                for(int at=0;at<bytes.Length;){token.ThrowIfCancellationRequested();int read=input.Stream.Read(bytes,at,Math.Min(1<<20,bytes.Length-at));if(read<=0)throw new UserError(PdfStructure.Damaged);at+=read;}
                var pdf=PdfStructure.Read(bytes,token,budget);
                if(work.rows.Count+pdf.Pages.Count>PdfRasterPlan.MaxPages)throw new UserError(PdfStructure.Limit);
                int source=work.sources.Count;work.sources.Add(pdf);
                foreach(var page in pdf.Pages){work.choices.Add(new(source,page.Index));work.rows.Add(new(work.rows.Count+1,path,page.Index+1,page.Width,page.Height,page.Rotate));}
            }
            token.ThrowIfCancellationRequested();return work;
        }
        catch{work.Dispose();throw;}
    }

    public PdfPageChoice[] Plan(string selection)=>PdfPageCopy.Parse(selection,rows.Count).Select(i=>choices[i]).ToArray();

    public string Save(IReadOnlyList<PdfPageChoice> order,int turn,CancellationToken token)
    {
        var bytes=PdfPageCopy.Write(sources,order,turn,token);token.ThrowIfCancellationRequested();
        return NativeFiles.WriteBeside(inputs[0],Path.GetFileNameWithoutExtension(inputs[0].Path)+" pages","pdf",output=>output.Write(bytes),token);
    }

    public void Dispose(){foreach(var input in inputs)input.Dispose();inputs.Clear();sources.Clear();}
}
