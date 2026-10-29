using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

namespace Swivel.Core;

public sealed record QrReadResult(string Text,int Version,QrCorrection Correction,int Mask,int PixelScale)
{
    // Orientation of the input raster, clockwise relative to the QR's upright grid.
    public int ClockwiseOrientationDegrees { get; init; }
    // Set only by QrPhotoRead: modules were sampled through a fitted geometry and data blocks
    // were error-corrected. PixelScale is then the rounded estimated module size.
    public bool Photo { get; init; }
    public int CorrectedCodewords { get; init; }
    public int CorrectedBlocks { get; init; }
}

/// A deliberately strict local raster reader, not a camera/finder or damage repair engine.
public static class QrFlatRead
{
    public const string Scope="Read one clean, square Model 2 QR image at 0°, 90°, 180° or 270°, with an exact four-module white border and uniform integer-sized pixels. Versions 1–40 and L/M/Q/H are supported. Opaque near-black/near-white pixels only; no cropped border, perspective, other rotation, mirrored image, blur, logo or error repair. Numeric, alphanumeric, ASCII bytes and explicit UTF-8 ECI 26 only; other byte encodings, Kanji, FNC1 and structured append are not supported. Parity is checked without correcting damage. This does not authenticate the text or its source.";
    public const string NoCode="No supported clean QR code was found. Use a square flat image with its exact four-module border; photographed, resized, damaged or unsupported codes need another reader.";
    public const string RecoveryScope="Explicit data-codeword recovery for a square, flat Model 2 QR image at 0°, 90°, 180° or 270°. Versions 1–40 and L/M/Q/H use the same exact four-module border, integer-sized uniform modules and opaque near-black/near-white pixel rules as strict reading. Only data and parity codewords can be changed, up to half the parity codewords per block. Finder, alignment, timing, format, version and remainder modules must already be exact. No border, BCH, perspective, blur, logo, mirror or other rotation repair. The original image is not changed. All blocks must pass parity checks and the supported text grammar before any text is shown. Numeric, alphanumeric, ASCII bytes and explicit UTF-8 ECI 26 only; other byte encodings, Kanji, FNC1 and structured append remain unsupported. Changed-codeword counts measure the difference from the accepted candidate, not the true amount of damage. Beyond the correction capacity a different valid code can be accepted. Neither successful recovery nor zero changes authenticates the text or its source.";
    public const string RecoveryNoCode="No supported flat QR data candidate could be recovered. The image may exceed data correction capacity, have invalid function modules or border, or use an unsupported image or text format.";
    public static QrReadResult Read(PixelImage image,CancellationToken token) => ReadRaster(image,token,false).Result;
    public static QrDataRecoveryResult ReadWithDataRecovery(PixelImage image,CancellationToken token) => ReadRaster(image,token,true);
    private static QrDataRecoveryResult ReadRaster(PixelImage image,CancellationToken token,bool recover)
    {
        token.ThrowIfCancellationRequested();image.Validate();
        if(image.Width!=image.Height)throw new UserError(recover?RecoveryNoCode:NoCode);
        QrDataRecoveryResult? result=null;
        for(int version=1;version<=40;version++)
        {
            token.ThrowIfCancellationRequested();int size=17+4*version,side=size+8;
            if(image.Width%side!=0)continue;int scale=image.Width/side;
            // Cheap finder corner probes avoid rescanning the full raster for most candidates.
            bool? Pixel(int x,int y)
            {
                int p=(y*image.Width+x)*4;var b=image.Bgra;
                if(b[p+3]!=255)return null;
                if(b[p]<=63&&b[p+1]<=63&&b[p+2]<=63)return true;
                if(b[p]>=192&&b[p+1]>=192&&b[p+2]>=192)return false;
                return null;
            }
            int low=4*scale,high=(side-5)*scale;
            int darkCorners=(Pixel(low,low)==true?1:0)+(Pixel(high,low)==true?1:0)
                +(Pixel(low,high)==true?1:0)+(Pixel(high,high)==true?1:0);
            if(darkCorners<3)continue;
            var grid=new bool[size,size];bool valid=true;
            for(int my=0;my<side&&valid;my++)
            {
                token.ThrowIfCancellationRequested();
                for(int mx=0;mx<side&&valid;mx++)
                {
                    bool? value=Pixel(mx*scale,my*scale);
                    if(value is null){valid=false;break;}
                    bool border=mx<4||my<4||mx>=side-4||my>=side-4;
                    if(border&&value.Value){valid=false;break;}
                    for(int dy=0;dy<scale&&valid;dy++)
                    {
                        token.ThrowIfCancellationRequested();
                        for(int dx=0;dx<scale;dx++)if(Pixel(mx*scale+dx,my*scale+dy)!=value){valid=false;break;}
                    }
                    if(!border)grid[mx-4,my-4]=value.Value;
                }
            }
            if(!valid)continue;
            // Raster validation happens once. Try four exact module permutations; there is no
            // interpolation, image mutation or mirrored fallback. Recovery changes only extracted codewords.
            for(int turns=0;turns<4;turns++)
            {
                token.ThrowIfCancellationRequested();
                QrDataRecoveryResult? decoded=QrCode.ReadGrid(grid,scale,token,recover);
                if(decoded is not null)
                {
                    if(result is not null)throw new UserError("The image has ambiguous QR grids. Choose one exact flat QR image.");
                    result=decoded with{Result=decoded.Result with{ClockwiseOrientationDegrees=(4-turns)%4*90}};
                }
                if(turns==3)break;
                var rotated=new bool[size,size];
                for(int y=0;y<size;y++)
                {
                    token.ThrowIfCancellationRequested();
                    for(int x=0;x<size;x++)rotated[size-1-y,x]=grid[x,y];
                }
                grid=rotated;
            }
        }
        token.ThrowIfCancellationRequested();return result??throw new UserError(recover?RecoveryNoCode:NoCode);
    }

    // Display untrusted controls and bidi/format characters visibly; explicit copy retains exact text.
    public static string VisibleText(string text)
    {
        var output=new StringBuilder();
        foreach(var rune in text.EnumerateRunes())
        {
            var category=Rune.GetUnicodeCategory(rune);
            if(category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                output.Append("[U+").Append(rune.Value.ToString("X4",CultureInfo.InvariantCulture)).Append(']');
            else output.Append(rune.ToString());
        }
        return output.ToString();
    }
}

public sealed partial class QrCode
{
    // Shares only the original encoder's standard tables/function-bit facts and GF remainder.
    // Encode/Raster and all existing output paths stay unchanged.
    internal static QrReadResult? ReadCleanGrid(bool[,] grid,int scale,CancellationToken token) => ReadGrid(grid,scale,token,false)?.Result;
    internal static QrDataRecoveryResult? ReadGrid(bool[,] grid,int scale,CancellationToken token,bool recover) => DecodeGrid(grid,scale,token,recover,false);
    // Photo sampling: nearest valid format/version information (BCH distance <=3, unique),
    // other function and remainder modules are not required to match, and data blocks are
    // corrected within their parity capacity. The strict path above is unchanged.
    internal static QrDataRecoveryResult? ReadSampledGrid(bool[,] grid,int scale,CancellationToken token) => DecodeGrid(grid,scale,token,true,true);
    internal static int? ReadVersionInformation(bool[,] grid,CancellationToken token)
    {
        int size=grid.GetLength(0),best=int.MaxValue,count=0,found=0;
        if(size<45||size>177)return null;
        for(int version=7;version<=40;version++)
        {
            token.ThrowIfCancellationRequested();int index=0,first=0,second=0;
            VersionInformation((x,y,value)=>{if(x>=0&&y>=0&&x<size&&y<size&&grid[x,y]!=value){if(index%2==0)first++;else second++;}index++;},size,version);
            int distance=Math.Min(first,second);
            if(distance<best){best=distance;found=version;count=1;}else if(distance==best)count++;
        }
        return best<=3&&count==1?found:null;
    }
    private static QrDataRecoveryResult? DecodeGrid(bool[,] grid,int scale,CancellationToken token,bool recover,bool tolerant)
    {
        int size=grid.GetLength(0),version=(size-17)/4;
        var expected=new bool[size,size];var reserved=new bool[size,size];
        void Set(int x,int y,bool value){if(x<0||y<0||x>=size||y>=size)return;expected[x,y]=value;reserved[x,y]=true;}
        void Finder(int x,int y)
        {
            for(int dy=-1;dy<=7;dy++)for(int dx=-1;dx<=7;dx++)
                Set(x+dx,y+dy,dx is >=0 and <=6&&dy is >=0 and <=6&&(dx is 0 or 6||dy is 0 or 6||dx is >=2 and <=4&&dy is >=2 and <=4));
        }
        Finder(0,0);Finder(size-7,0);Finder(0,size-7);
        for(int i=8;i<size-8;i++){Set(6,i,i%2==0);Set(i,6,i%2==0);}
        foreach(int cy in AlignmentCenters[version-1])foreach(int cx in AlignmentCenters[version-1])
        {
            if(cx==6&&(cy==6||cy==size-7)||cy==6&&cx==size-7)continue;
            for(int y=-2;y<=2;y++)for(int x=-2;x<=2;x++)Set(cx+x,cy+y,Math.Max(Math.Abs(x),Math.Abs(y))!=1);
        }
        if(version>=7)VersionInformation(Set,size,version);
        QrCorrection? correction=null;int mask=-1;
        if(tolerant)
        {
            Format(Set,size,0,QrCorrection.Low);int best=int.MaxValue,ties=0;
            foreach(var level in Enum.GetValues<QrCorrection>())for(int candidate=0;candidate<8;candidate++)
            {
                token.ThrowIfCancellationRequested();int index=0,first=0,second=0;
                Format((x,y,value)=>{if(index<30&&grid[x,y]!=value){if(index<15)first++;else second++;}index++;},size,candidate,level);
                int distance=Math.Min(first,second);
                if(distance<best){best=distance;correction=level;mask=candidate;ties=1;}else if(distance==best)ties++;
            }
            if(best>3||ties!=1)return null;
            if(version>=7&&ReadVersionInformation(grid,token)!=version)return null;
        }
        // Both format copies and all other function modules must match exactly: no BCH repair.
        else foreach(var level in Enum.GetValues<QrCorrection>())for(int candidate=0;candidate<8;candidate++)
        {
            token.ThrowIfCancellationRequested();Format(Set,size,candidate,level);bool same=true;
            for(int y=0;y<size&&same;y++)for(int x=0;x<size;x++)if(reserved[x,y]&&grid[x,y]!=expected[x,y]){same=false;break;}
            if(same){correction=level;mask=candidate;}
        }
        if(correction is null)return null;
        int dataCount=(correction.Value switch{QrCorrection.Low=>DataWords,QrCorrection.Medium=>MediumDataWords,QrCorrection.Quartile=>QuartileDataWords,_=>HighDataWords})[version-1];
        int parity=(correction.Value switch{QrCorrection.Low=>CheckWords,QrCorrection.Medium=>MediumCheckWords,QrCorrection.Quartile=>QuartileCheckWords,_=>HighCheckWords})[version-1];
        int blocks=(correction.Value switch{QrCorrection.Low=>BlockCounts,QrCorrection.Medium=>MediumBlockCounts,QrCorrection.Quartile=>QuartileBlockCounts,_=>HighBlockCounts})[version-1];
        var words=new byte[dataCount+parity*blocks];int bit=0;bool upward=true;
        for(int right=size-1;right>=1;right-=2)
        {
            token.ThrowIfCancellationRequested();if(right==6)right--;
            for(int row=0;row<size;row++)
            {
                int y=upward?size-1-row:row;
                for(int d=0;d<2;d++)
                {
                    int x=right-d;if(reserved[x,y])continue;bool value=grid[x,y]^MaskAt(mask,x,y);
                    if(bit<words.Length*8){if(value)words[bit/8]|=(byte)(1<<(7-bit%8));}
                    else if(value&&!tolerant)return null;bit++;
                }
            }
            upward=!upward;
        }
        if(bit<words.Length*8)return null;
        int shortLength=dataCount/blocks,shortCount=blocks-dataCount%blocks,offset=0;
        var data=new byte[blocks][];var checks=new byte[blocks][];
        for(int b=0;b<blocks;b++){data[b]=new byte[shortLength+(b<shortCount?0:1)];checks[b]=new byte[parity];}
        for(int column=0;column<=shortLength;column++)for(int b=0;b<blocks;b++)if(column<data[b].Length)data[b][column]=words[offset++];
        for(int column=0;column<parity;column++)for(int b=0;b<blocks;b++)checks[b][column]=words[offset++];
        var payload=new byte[dataCount];offset=0;int correctedWords=0,correctedBlocks=0;
        for(int b=0;b<blocks;b++)
        {
            token.ThrowIfCancellationRequested();
            if(!Remainder(data[b],parity).SequenceEqual(checks[b]))
            {
                if(!recover)return null;
                var received=new byte[data[b].Length+parity];
                data[b].CopyTo(received,0);checks[b].CopyTo(received,data[b].Length);
                if(!QrDataRecovery.TryRepair(received,parity,token,out var repaired,out int changed))return null;
                Array.Copy(repaired!,0,data[b],0,data[b].Length);Array.Copy(repaired!,data[b].Length,checks[b],0,parity);
                // Independent existing encoder remainder validation follows all-syndrome validation.
                if(changed==0||!Remainder(data[b],parity).SequenceEqual(checks[b]))return null;
                correctedWords+=changed;correctedBlocks++;
            }
            data[b].CopyTo(payload,offset);offset+=data[b].Length;
        }
        string? text=ReadText(payload,version,token);return text is null?null:new(new(text,version,correction.Value,mask,scale),correctedWords,correctedBlocks);
    }

    private static string? ReadText(byte[] bytes,int version,CancellationToken token)
    {
        int bit=0;bool utf8=false;var result=new StringBuilder();
        int Read(int count)
        {
            if(count<0||bit+count>bytes.Length*8)throw new FormatException();
            int value=0;for(int i=0;i<count;i++,bit++)value=(value<<1)|((bytes[bit/8]>>(7-bit%8))&1);return value;
        }
        try
        {
            while(bytes.Length*8-bit>=4)
            {
                token.ThrowIfCancellationRequested();int mode=Read(4);if(mode==0)break;
                if(mode==7){int assignment=Read(8);if(assignment!=26)return null;utf8=true;continue;}
                int group=version<10?0:version<27?1:2;
                int count=mode switch{1=>Read(new[]{10,12,14}[group]),2=>Read(new[]{9,11,13}[group]),4=>Read(version<10?8:16),_=>-1};
                if(count<0)return null;
                if(mode==4)
                {
                    if(count>(bytes.Length*8-bit)/8)return null;
                    var segment=new byte[count];for(int i=0;i<count;i++)segment[i]=(byte)Read(8);
                    if(!utf8&&segment.Any(v=>v>127))return null;
                    result.Append(new UTF8Encoding(false,true).GetString(segment));
                }
                else if(mode==1)
                {
                    while(count>0){int digits=Math.Min(3,count),value=Read(digits==3?10:digits==2?7:4);if(value>=(digits==3?1000:digits==2?100:10))return null;result.Append(value.ToString("D"+digits,CultureInfo.InvariantCulture));count-=digits;}
                }
                else
                {
                    const string alphabet="0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";
                    while(count>=2){int value=Read(11);if(value>=45*45)return null;result.Append(alphabet[value/45]).Append(alphabet[value%45]);count-=2;}
                    if(count!=0){int value=Read(6);if(value>=45)return null;result.Append(alphabet[value]);}
                }
                if(result.Length>8000)return null;
            }
            // Terminator/padding bits and alternating pad words must be complete, not ignored data.
            while(bit%8!=0)if(Read(1)!=0)return null;
            int padding=0;while(bit<bytes.Length*8)if(Read(8)!=(padding++%2==0?0xec:0x11))return null;
            return result.ToString();
        }
        catch(Exception error)when(error is FormatException or DecoderFallbackException){return null;}
    }
}
