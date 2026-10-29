using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;

namespace Swivel.Core;

// Original bounded static-GIF reader, not an animation renderer. Format attribution
// and accepted/rejected subsets are recorded in Windows/README.md and PORTING.md.
public static class GifImage
{
    public const int MaxExtensions=1024,MaxExtensionBytes=65536,MaxSubblocks=1_048_576;
    public const string InputHint="Use a PNG, JPEG, BMP, TIFF, TGA, PSD or supported static GIF image.";
    public const string Invalid="The GIF structure or LZW data is invalid or incomplete.";
    public const string Layout="Use a single-frame GIF that covers its whole canvas with square pixels. Animated, offset and partial-canvas GIFs are not supported yet.";
    public const string Extensions="This GIF uses unsupported text, user-input, color-profile or application extensions. Save a standard static GIF or PNG.";
    public const string Limit="GIF extension data, sub-blocks or LZW codes exceed the supported limits.";
    public const string Tail="Additional GIF data after the LZW end code is not supported. Save a standard static GIF or PNG.";
    public static PixelImage Read(Stream input,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(!input.CanRead||!input.CanSeek)throw new ArgumentException("A seekable readable stream is required.",nameof(input));
        if(input.Length>ImageRules.MaxInputBytes)throw new UserError("Image files are limited to 128 MB.");
        input.Position=0;
        try
        {
            var read=new Reader(input,token);Span<byte> header=stackalloc byte[13];read.Exact(header);
            if(!header[..6].SequenceEqual("GIF87a"u8)&&!header[..6].SequenceEqual("GIF89a"u8))throw new UserError(Invalid);
            int width=BinaryPrimitives.ReadUInt16LittleEndian(header[6..]),height=BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);ImageRules.Validate(width,height);
            if(header[12] is not 0 and not 49)throw new UserError(Layout);
            byte[]? global=(header[10]&128)!=0?read.Palette(2<<(header[10]&7)):null;
            if(global is not null&&header[11]>=global.Length/4)throw new UserError(Invalid);
            Span<byte> name=stackalloc byte[11],descriptor=stackalloc byte[9];
            byte[]? pixels=null;int transparent=-1,extensions=0;bool control=false,loop=false;
            while(true)
            {
                token.ThrowIfCancellationRequested();int marker=read.Byte();
                if(marker==0x3b)
                {
                    if(pixels is null||control||input.Position!=input.Length)throw new UserError(Invalid);
                    token.ThrowIfCancellationRequested();input.Position=0;return new(width,height,pixels);
                }
                if(marker==0x21)
                {
                    if(++extensions>MaxExtensions)throw new UserError(Limit);
                    switch(read.Byte())
                    {
                        case 0xf9:
                            if(control||pixels is not null||read.Byte()!=4)throw new UserError(Invalid);
                            read.Metadata(4);int flags=read.Byte();read.Byte();read.Byte();int index=read.Byte();
                            if((flags&0xe0)!=0||((flags>>2)&7)>3||read.Byte()!=0)throw new UserError(Invalid);
                            if((flags&2)!=0)throw new UserError(Extensions);
                            transparent=(flags&1)!=0?index:-1;control=true;break;
                        case 0xfe:read.SkipComments();break;
                        case 0xff:
                            if(loop||read.Byte()!=11)throw new UserError(Extensions);
                            read.Metadata(11);read.Exact(name);
                            if(!name.SequenceEqual("NETSCAPE2.0"u8)&&!name.SequenceEqual("ANIMEXTS1.0"u8))throw new UserError(Extensions);
                            if(read.BlockLength()!=3)throw new UserError(Invalid);read.Metadata(3);
                            if(read.Byte()!=1)throw new UserError(Invalid);read.Byte();read.Byte();
                            if(read.BlockLength()!=0)throw new UserError(Invalid);loop=true;break;
                        default:throw new UserError(Extensions);
                    }
                    continue;
                }
                if(marker!=0x2c)throw new UserError(Invalid);
                if(pixels is not null)throw new UserError(Layout);
                read.Exact(descriptor);
                int left=BinaryPrimitives.ReadUInt16LittleEndian(descriptor),top=BinaryPrimitives.ReadUInt16LittleEndian(descriptor[2..]);
                int frameWidth=BinaryPrimitives.ReadUInt16LittleEndian(descriptor[4..]),frameHeight=BinaryPrimitives.ReadUInt16LittleEndian(descriptor[6..]);
                int packed=descriptor[8];
                if((packed&0x18)!=0)throw new UserError(Invalid);
                if(left!=0||top!=0||frameWidth!=width||frameHeight!=height)throw new UserError(Layout);
                byte[] palette=(packed&128)!=0?read.Palette(2<<(packed&7)):global??throw new UserError(Invalid);
                if(transparent>=palette.Length/4)throw new UserError(Invalid);
                if(transparent>=0)
                {
                    palette=(byte[])palette.Clone();palette.AsSpan(transparent*4,4).Clear();
                }
                int minimum=read.Byte();if(minimum is <2 or >8)throw new UserError(Invalid);
                pixels=Decode(read,width,height,palette,(packed&64)!=0,minimum,token);control=false;
            }
        }
        catch(EndOfStreamException){throw new UserError(Invalid);}
    }
    private static byte[] Decode(Reader read,int width,int height,byte[] palette,bool interlace,int minimum,CancellationToken token)
    {
        int count=checked(width*height),clear=1<<minimum,end=clear+1,next=clear+2,size=minimum+1,previous=-1,first=0;
        var prefix=new ushort[4096];var suffix=new byte[4096];var stack=new byte[4096];var output=new byte[checked(count*4)];
        var data=new Data(read);uint bits=0;int bitCount=0,produced=0,codes=0,x=0,y=0,pass=0;
        int Code()
        {
            if(++codes>count*2+4096)throw new UserError(Limit);token.ThrowIfCancellationRequested();
            while(bitCount<size){int value=data.Byte();if(value<0)throw new UserError(Invalid);bits|=(uint)value<<bitCount;bitCount+=8;}
            int code=(int)(bits&((1u<<size)-1));bits>>=size;bitCount-=size;return code;
        }
        void Pixel(int index)
        {
            if((produced&4095)==0)token.ThrowIfCancellationRequested();
            if(produced>=count||index>=palette.Length/4)throw new UserError(Invalid);
            palette.AsSpan(index*4,4).CopyTo(output.AsSpan((y*width+x)*4,4));produced++;
            if(++x<width)return;x=0;
            if(!interlace){y++;return;}
            y+=pass<2?8:pass==2?4:2;
            while(y>=height&&pass<3){pass++;y=pass==1?4:pass==2?2:1;}
        }
        while(true)
        {
            int code=Code();
            if(code==clear){next=clear+2;size=minimum+1;previous=-1;continue;}
            if(code==end)
            {
                if(produced!=count)throw new UserError(Invalid);data.Finish();token.ThrowIfCancellationRequested();return output;
            }
            if(previous<0)
            {
                if(code>=clear)throw new UserError(Invalid);Pixel(code);first=code;previous=code;continue;
            }
            if(code>next)throw new UserError(Invalid);
            int current=code,used=0;
            if(code==next){stack[used++]=(byte)first;code=previous;}
            while(code>=clear)
            {
                if((used&255)==0)token.ThrowIfCancellationRequested();
                if(code<clear+2||code>=next||used>=stack.Length)throw new UserError(Invalid);
                stack[used++]=suffix[code];code=prefix[code];
            }
            first=code;if(used>=stack.Length)throw new UserError(Invalid);stack[used++]=(byte)first;
            while(used>0)Pixel(stack[--used]);
            if(next<4096)
            {
                prefix[next]=(ushort)previous;suffix[next]=(byte)first;next++;
                if(next==(1<<size)&&size<12)size++;
            }
            previous=current;
        }
    }
    private sealed class Reader(Stream input,CancellationToken token)
    {
        private int metadata,subblocks;
        public int Byte(){token.ThrowIfCancellationRequested();int value=input.ReadByte();if(value<0)throw new EndOfStreamException();return value;}
        public void Exact(Span<byte> bytes){token.ThrowIfCancellationRequested();input.ReadExactly(bytes);token.ThrowIfCancellationRequested();}
        public int BlockLength(){if(++subblocks>MaxSubblocks)throw new UserError(Limit);return Byte();}
        public void Metadata(int count){if(count>MaxExtensionBytes-metadata)throw new UserError(Limit);metadata+=count;}
        public byte[] Palette(int count)
        {
            var rgb=new byte[count*3];Exact(rgb);var bgra=new byte[count*4];
            for(int i=0;i<count;i++){bgra[i*4]=rgb[i*3+2];bgra[i*4+1]=rgb[i*3+1];bgra[i*4+2]=rgb[i*3];bgra[i*4+3]=255;}return bgra;
        }
        public void SkipComments()
        {
            for(int length=BlockLength();length!=0;length=BlockLength())
            {Metadata(length);token.ThrowIfCancellationRequested();if(length>input.Length-input.Position)throw new EndOfStreamException();input.Position+=length;}
        }
    }
    private sealed class Data(Reader read)
    {
        private readonly byte[] bytes=new byte[255];private int at,length;private bool ended;
        public int Byte()
        {
            if(ended)return -1;
            if(at==length)
            {
                length=read.BlockLength();at=0;
                if(length==0){ended=true;return -1;}
                read.Exact(bytes.AsSpan(0,length));
            }
            return bytes[at++];
        }
        public void Finish()
        {
            // Only the unused bits in the final byte are padding. Reject extra
            // payload bytes or blocks rather than silently discarding them.
            if(at!=length||read.BlockLength()!=0)throw new UserError(Tail);ended=true;
        }
    }
}
