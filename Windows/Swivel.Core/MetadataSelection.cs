using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Swivel.Core;

public sealed record MetadataField(int Id,string Group,string Name,string Directory,int Bytes,string Preview,bool CanRemove)
{
    public int? RemovalParent { get; internal init; }
}

// Session-local IDs deliberately never enter preferences or the action catalog.
public sealed class MetadataSelection
{
    public const string Invalid="Selective removal is unavailable: metadata has unsupported types, offsets, thumbnails, overlapping areas, duplicate tags, cycles or excessive entries. Full removal is still available.";
    public const string Notice="Tick individual tags or whole blocks to remove. Unchecked metadata value bytes and ICC are kept byte-identical; TIFF storage offsets are rebuilt. Image pixels, strips and tiles are not rewritten. Orientation is kept unless explicitly ticked. XMP and IPTC (including Photoshop blocks) are selectable only as whole blocks. Value previews are bounded and escaped; opaque or compressed values are shown as bytes. This is not complete privacy sanitization.";
    public const string DuplicateWarning="Best-effort warning: a retained XMP, IPTC, PNG text or opaque metadata block might repeat a removed value. Block contents are not exhaustively interpreted; remove the block too if needed. Retained MakerNote bytes are unchanged, but proprietary internal offsets may no longer be meaningful after relocation.";
    public const string Empty="Select at least one metadata tag or block to remove. No copy was saved.";
    public const int MaxFields=8192;
    private readonly List<MetadataField> fields=new();
    private Action<Stream,HashSet<int>,CancellationToken>? writer;
    public IReadOnlyList<MetadataField> Items=>fields.AsReadOnly();
    internal int Add(string group,string name,string directory,ReadOnlySpan<byte> value,bool canRemove=true)
    {
        if(fields.Count>=MaxFields)throw new UserError(Invalid);
        int id=fields.Count;int n=Math.Min(value.Length,96);
        var shown=value[..n];if(shown.Length>0&&shown[^1]==0)shown=shown[..^1];
        string preview=System.Text.Json.JsonSerializer.Serialize(Encoding.UTF8.GetString(shown));
        bool binary=shown.AnyNonText();if(binary)preview=Convert.ToHexString(value[..Math.Min(n,32)]);
        if((binary?Math.Min(n,32):n)<value.Length)preview+="…";
        fields.Add(new(id,group,name,directory,value.Length,preview,canRemove));return id;
    }
    internal static void Copy(Stream output,byte[] bytes,CancellationToken token)
    {
        for(int at=0;at<bytes.Length;){token.ThrowIfCancellationRequested();int n=Math.Min(65536,bytes.Length-at);output.Write(bytes.AsSpan(at,n));at+=n;}
    }
    internal void Parent(int id,int parent)=>fields[id]=fields[id] with {RemovalParent=parent};
    internal MetadataSelection Writer(Action<Stream,HashSet<int>,CancellationToken> write){writer=write;return this;}
    public bool MayDuplicate(IReadOnlyCollection<int> removed)
    {
        if(removed.Count==0)return false;var ids=new HashSet<int>(removed);
        return fields.Any(f=>!ids.Contains(f.Id)&&!(f.RemovalParent is int parent&&ids.Contains(parent))&&f.Group is "XMP" or "IPTC" or "PNG text" or "Maker note" or "Other metadata");
    }
    public void Write(Stream output,IReadOnlyCollection<int> removedIds,CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(output);ArgumentNullException.ThrowIfNull(removedIds);token.ThrowIfCancellationRequested();
        if(!output.CanWrite)throw new ArgumentException("A writable output stream is required.",nameof(output));
        if(removedIds.Count==0)throw new UserError(Empty);
        if(removedIds.Count>fields.Count)throw new ArgumentException("Invalid metadata selection.",nameof(removedIds));
        var ids=new HashSet<int>(removedIds);
        if(ids.Any(id=>id<0||id>=fields.Count||!fields[id].CanRemove))throw new ArgumentException("Invalid metadata selection.",nameof(removedIds));
        writer!(output,ids,token);token.ThrowIfCancellationRequested();
    }
    internal static string Group(int tag,string role)=>role=="GPS"?"Location":tag switch
    {
        274=>"Orientation",271 or 272 or 316 or 42032 or 42033 or 42034 or 42035 or 42036 or 42037=>"Camera and device",
        306 or 36867 or 36868 or 36880 or 36881 or 36882 or 37520 or 37521 or 37522=>"Dates",
        315 or 33432 or 40093=>"Author and copyright",269 or 270 or 285 or 37510 or 40091 or 40092 or 40094 or 40095=>"Comments and descriptions",
        305 or 11=>"Software",37500=>"Maker note",700=>"XMP",33723 or 34377=>"IPTC",286 or 287=>"Location",_=>"Other metadata"
    };
    internal static string Name(int tag)=>tag switch
    {
        274=>"Orientation",271=>"Make",272=>"Model",306=>"DateTime",315=>"Artist",33432=>"Copyright",270=>"ImageDescription",305=>"Software",37500=>"MakerNote",37510=>"UserComment",36867=>"DateTimeOriginal",36868=>"DateTimeDigitized",700=>"XMP",33723=>"IPTC",34377=>"Photoshop",_=>"Tag"
    };
}
internal static class MetadataPreview
{
    internal static bool AnyNonText(this ReadOnlySpan<byte> bytes){foreach(byte b in bytes)if(b<32||b==127)return true;return false;}
}
