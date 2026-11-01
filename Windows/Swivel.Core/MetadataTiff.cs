using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Swivel.Core;

// Strict relocation shared by selective EXIF and classic TIFF. Legacy rebuilders
// remain independent so their existing output bytes and permissive removal stay stable.
internal sealed class MetadataTiff
{
    private sealed class Entry
    {
        internal int Tag,Type,Offset,Length,Id=-1;internal uint Count;internal Node? Child;
    }
    private sealed class Node
    {
        internal readonly List<Entry> Entries=new();internal Node? Next;internal int Gate=-1;
        internal byte[]? Thumbnail;internal (int Offset,int Length)[]? Blocks;
    }
    private readonly byte[] source;private readonly bool little;private readonly Node root;
    private static readonly int[] Sizes={0,1,1,2,4,8,1,1,2,4,8,4,8};
    private static void Require(bool valid){if(!valid)throw new UserError(MetadataSelection.Invalid);}
    private ushort U16(int p)=>little?BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(p,2)):BinaryPrimitives.ReadUInt16BigEndian(source.AsSpan(p,2));
    private uint U32(int p)=>little?BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(p,4)):BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(p,4));
    internal MetadataTiff(byte[] source,MetadataSelection selection,string prefix,CancellationToken token,TiffMetadata.Page[]? pages=null)
    {
        this.source=source;Require(source.Length>=8);little=source[0]=='I'&&source[1]=='I';Require(little||source[0]=='M'&&source[1]=='M');Require(U16(2)==42);
        var areas=new List<(int Start,int End)>{(0,8)};var seen=new HashSet<uint>();int entries=0,pageIndex=0;
        void Area(int at,int length){Require(at>=8&&length>0&&(long)at+length<=source.Length);areas.Add((at,at+length));}
        Node Read(uint position,string role,string path,int depth)
        {
            token.ThrowIfCancellationRequested();Require(depth<=66&&position>=8&&(position&1)==0&&position<=source.Length-2&&seen.Add(position));
            int at=(int)position,count=U16(at);Require(count<=512&&(entries+=count)<=32768);Require((long)at+6+12L*count<=source.Length);Area(at,6+12*count);
            var node=new Node();bool page=role.StartsWith("IFD",StringComparison.Ordinal)&&pages is not null;int index=page?pageIndex++:0;
            if(page){Require(index<pages!.Length);node.Blocks=pages[index].Blocks;foreach(var block in node.Blocks)Area(block.Offset,block.Length);}
            int previous=-1;
            for(int k=0;k<count;k++)
            {
                token.ThrowIfCancellationRequested();int p=at+2+12*k,tag=U16(p),type=U16(p+2);uint n=U32(p+4);
                Require(tag>previous&&type is >=1 and <=12&&n>0);previous=tag;long length=(long)Sizes[type]*n;Require(length<=source.Length);
                uint offset=length<=4?(uint)(p+8):U32(p+8);Require((long)offset+length<=source.Length);if(length>4){Require((offset&1)==0);Area((int)offset,(int)length);}
                var e=new Entry{Tag=tag,Type=type,Count=n,Offset=(int)offset,Length=(int)length};node.Entries.Add(e);
                if(tag is 34665 or 34853 or 40965)
                {
                    Require(type==4&&n==1&&((tag is 34665 or 34853&&role.StartsWith("IFD",StringComparison.Ordinal))||(tag==40965&&role=="Exif")));
                    string childRole=tag==34665?"Exif":tag==34853?"GPS":"Interop";
                    e.Child=Read(U32(e.Offset),childRole,path+"/"+childRole,depth+1);continue;
                }
                Require(tag is not (288 or 289 or 330 or 519 or 520 or 521));
                Require(tag is not (513 or 514)||role=="Thumbnail");
                if(!page)Require((role=="Thumbnail"||tag is not (273 or 279))&&tag is not (324 or 325));
                bool structural=page&&pages![index].Entries.Any(a=>a.Tag==tag)&&TiffMetadata.IsRenderingTag(tag);
                if(role!="Thumbnail")e.Id=selection.Add(structural?"Image and color":MetadataSelection.Group(tag,role),MetadataSelection.Name(tag)+" 0x"+tag.ToString("X4"),prefix+"/"+path,source.AsSpan(e.Offset,e.Length),!structural);
            }
            uint next=U32(at+2+12*count);
            if(next!=0)
            {
                Require(role.StartsWith("IFD",StringComparison.Ordinal));
                if(pages is not null)node.Next=Read(next,"IFD"+pageIndex,"IFD"+pageIndex,depth+1);
                else
                {
                    Require(role=="IFD0");node.Next=Read(next,"Thumbnail","IFD1",depth+1);
                    var start=node.Next.Entries.FirstOrDefault(e=>e.Tag==513);var size=node.Next.Entries.FirstOrDefault(e=>e.Tag==514);
                    Require(!node.Next.Entries.Any(e=>e.Tag is 273 or 279)&&start is not null&&size is not null&&start.Type==4&&start.Count==1&&size.Type==4&&size.Count==1);
                    uint o=U32(start!.Offset),n=U32(size!.Offset);Require(o>=8&&n>=4&&(long)o+n<=source.Length&&source[o]==255&&source[o+1]==216&&source[o+n-2]==255&&source[o+n-1]==217);
                    Area((int)o,(int)n);node.Next.Thumbnail=source.AsSpan((int)o,(int)n).ToArray();node.Next.Gate=selection.Add("Thumbnail","Thumbnail",prefix+"/IFD1",node.Next.Thumbnail);
                    foreach(var e in node.Next.Entries)
                    {
                        bool structure=TiffMetadata.IsRenderingTag(e.Tag)||e.Tag is 513 or 514;
                        e.Id=selection.Add(structure?"Thumbnail":MetadataSelection.Group(e.Tag,"Thumbnail"),MetadataSelection.Name(e.Tag)+" 0x"+e.Tag.ToString("X4"),prefix+"/IFD1",source.AsSpan(e.Offset,e.Length),!structure);
                        selection.Parent(e.Id,node.Next.Gate);
                    }
                }
            }
            return node;
        }
        root=Read(U32(4),"IFD0","IFD0",0);if(pages is not null)Require(pageIndex==pages.Length);
        var sorted=areas.OrderBy(r=>r.Start).ToArray();for(int i=1;i<sorted.Length;i++){token.ThrowIfCancellationRequested();Require(sorted[i].Start>=sorted[i-1].End);}
    }
    internal byte[] Rebuild(HashSet<int> removed,CancellationToken token)
    {
        bool Active(Node node)=>node.Gate<0||!removed.Contains(node.Gate);
        bool Keep(Entry e)=>e.Id<0||!removed.Contains(e.Id);
        // Empty child directories lose their pointer as well, so removed GPS tags
        // do not leave an apparently present (but empty) GPS directory behind.
        bool HasValues(Node n)=>n.Entries.Any(e=>Keep(e)&&(e.Child is null||HasValues(e.Child)));
        Entry[] Retained(Node n)=>n.Entries.Where(e=>Keep(e)&&(e.Child is null||HasValues(e.Child))).ToArray();
        var nodes=new List<Node>();void Collect(Node n){nodes.Add(n);foreach(var e in Retained(n))if(e.Child is not null)Collect(e.Child);if(n.Next is not null&&Active(n.Next))Collect(n.Next);}Collect(root);
        int cursor=8;var nodeOffsets=new Dictionary<Node,int>();foreach(var n in nodes){nodeOffsets[n]=cursor;cursor=checked(cursor+6+12*Retained(n).Length);}
        using var output=new MemoryStream();output.SetLength(cursor);
        void Budget(){Require(output.Length<=32*1024*1024);}
        void W16(int value){Span<byte> b=stackalloc byte[2];if(little)BinaryPrimitives.WriteUInt16LittleEndian(b,(ushort)value);else BinaryPrimitives.WriteUInt16BigEndian(b,(ushort)value);output.Write(b);}
        void W32(uint value){Span<byte> b=stackalloc byte[4];if(little)BinaryPrimitives.WriteUInt32LittleEndian(b,value);else BinaryPrimitives.WriteUInt32BigEndian(b,value);output.Write(b);}
        int Append(ReadOnlySpan<byte> bytes)
        {
            int start=cursor;Require((long)cursor+bytes.Length+1<=32*1024*1024);output.Position=cursor;
            for(int k=0;k<bytes.Length;){token.ThrowIfCancellationRequested();int n=Math.Min(65536,bytes.Length-k);output.Write(bytes.Slice(k,n));k+=n;}
            cursor+=bytes.Length;if((cursor&1)!=0){output.WriteByte(0);cursor++;}return start;
        }
        output.Position=0;output.Write(little?"II"u8:"MM"u8);W16(42);W32((uint)nodeOffsets[root]);
        foreach(var node in nodes)
        {
            token.ThrowIfCancellationRequested();var es=Retained(node);int thumb=node.Thumbnail is null?0:Append(node.Thumbnail);
            var blocks=node.Blocks?.Select(b=>Append(source.AsSpan(b.Offset,b.Length))).ToArray();
            var values=new Dictionary<Entry,byte[]>();var offsets=new Dictionary<Entry,int>();
            foreach(var e in es)
            {
                token.ThrowIfCancellationRequested();byte[] value=source.AsSpan(e.Offset,e.Length).ToArray();
                if(blocks is not null&&e.Tag is 273 or 324)
                {
                    value=new byte[4*blocks.Length];for(int k=0;k<blocks.Length;k++){uint v=(uint)blocks[k];if(little)BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(4*k),v);else BinaryPrimitives.WriteUInt32BigEndian(value.AsSpan(4*k),v);}
                }
                values[e]=value;if(value.Length>4)offsets[e]=Append(value);
            }
            output.Position=nodeOffsets[node];W16(es.Length);
            foreach(var e in es)
            {
                token.ThrowIfCancellationRequested();bool block=blocks is not null&&e.Tag is 273 or 324;
                W16(e.Tag);W16(block?4:e.Type);W32(block?(uint)blocks!.Length:e.Count);
                if(e.Child is not null)W32((uint)nodeOffsets[e.Child]);
                else if(node.Thumbnail is not null&&e.Tag==513)W32((uint)thumb);
                else if(node.Thumbnail is not null&&e.Tag==514)W32((uint)node.Thumbnail.Length);
                else if(offsets.TryGetValue(e,out int offset))W32((uint)offset);
                else{var v=values[e];output.Write(v);for(int k=v.Length;k<4;k++)output.WriteByte(0);}
            }
            W32((uint)(node.Next is not null&&Active(node.Next)?nodeOffsets[node.Next]:0));Budget();
        }
        token.ThrowIfCancellationRequested();return output.ToArray();
    }
}
