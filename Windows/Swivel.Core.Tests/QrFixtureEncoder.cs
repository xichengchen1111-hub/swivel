using System;
using System.Collections.Generic;
using System.Linq;
using Swivel.Core;

// Original independent fixture encoder: QR Model 2, version 1/L, alphanumeric, mask zero.
// No production QR encoder, decoder, RS helper or table is used here.
internal static class QrFixtureEncoder
{
    private const string Alphabet="0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";
    internal static PixelImage Render(string text,int scale=4)
    {
        if(text.Length>25||text.Any(c=>!Alphabet.Contains(c))||scale is <1 or >16)throw new ArgumentException("Fixture is outside version 1 alphanumeric scope.");
        var bits=new List<bool>();
        void Append(int value,int count){for(int i=count-1;i>=0;i--)bits.Add((value&(1<<i))!=0);}
        Append(2,4);Append(text.Length,9);
        for(int i=0;i<text.Length;i+=2)
        {
            int value=Alphabet.IndexOf(text[i]);
            if(i+1<text.Length){value=value*45+Alphabet.IndexOf(text[i+1]);Append(value,11);}else Append(value,6);
        }
        Append(0,Math.Min(4,152-bits.Count));while(bits.Count%8!=0)bits.Add(false);
        var data=new byte[19];
        for(int i=0;i<bits.Count;i++)if(bits[i])data[i/8]|=(byte)(1<<(7-i%8));
        for(int i=bits.Count/8;i<data.Length;i++)data[i]=(byte)((i-bits.Count/8)%2==0?0xec:0x11);
        byte[] ecc=Parity(data);var words=data.Concat(ecc).ToArray();
        var modules=new bool[21,21];var reserved=new bool[21,21];
        void Function(int x,int y,bool dark)
        {
            if(x is <0 or >=21||y is <0 or >=21)return;modules[x,y]=dark;reserved[x,y]=true;
        }
        void Finder(int left,int top)
        {
            for(int y=-1;y<=7;y++)for(int x=-1;x<=7;x++)
                Function(left+x,top+y,x is >=0 and <=6&&y is >=0 and <=6&&(x is 0 or 6||y is 0 or 6||x is >=2 and <=4&&y is >=2 and <=4));
        }
        Finder(0,0);Finder(14,0);Finder(0,14);
        for(int i=8;i<=12;i++){Function(i,6,i%2==0);Function(6,i,i%2==0);}
        // Format payload: L=01, mask=000. BCH(15,5), then fixed mask 0x5412.
        int format=8<<10,remainder=format;
        for(int shift=14;shift>=10;shift--)if((remainder&(1<<shift))!=0)remainder^=0x537<<(shift-10);
        format=(format|remainder)^0x5412;
        bool F(int bit)=>(format&(1<<bit))!=0;
        for(int i=0;i<=5;i++)Function(8,i,F(i));
        Function(8,7,F(6));Function(8,8,F(7));Function(7,8,F(8));
        for(int i=9;i<15;i++)Function(14-i,8,F(i));
        for(int i=0;i<8;i++)Function(20-i,8,F(i));
        for(int i=8;i<15;i++)Function(8,6+i,F(i));
        Function(8,13,true);
        int index=0;bool up=true;
        for(int right=20;right>=1;right-=2)
        {
            if(right==6)right--;
            for(int row=0;row<21;row++)
            {
                int y=up?20-row:row;
                for(int side=0;side<2;side++)
                {
                    int x=right-side;if(reserved[x,y])continue;
                    bool value=(words[index/8]&(1<<(7-index%8)))!=0;index++;
                    modules[x,y]=value^((x+y)%2==0);
                }
            }
            up=!up;
        }
        if(index!=208)throw new InvalidOperationException("Fixture did not place exactly 26 QR codewords.");
        int width=29*scale;var pixels=Enumerable.Repeat((byte)255,width*width*4).ToArray();
        for(int y=0;y<21;y++)for(int x=0;x<21;x++)if(modules[x,y])
            for(int dy=0;dy<scale;dy++)for(int dx=0;dx<scale;dx++)
            {int p=(((y+4)*scale+dy)*width+(x+4)*scale+dx)*4;pixels[p]=pixels[p+1]=pixels[p+2]=0;}
        return new(width,width,pixels);
    }

    private static byte Multiply(byte left,byte right)
    {
        int product=0,value=left;
        for(int bit=0;bit<8;bit++)
        {
            if((right&(1<<bit))!=0)product^=value;
            value<<=1;if(value>=256)value^=0x11d;
        }
        return (byte)product;
    }
    private static byte[] Parity(byte[] data)
    {
        // Descending-coefficient product (x-alpha^0)...(x-alpha^6), reduced in GF(256).
        byte[] divisor={1};byte root=1;
        for(int degree=0;degree<7;degree++)
        {
            var next=new byte[divisor.Length+1];
            for(int i=0;i<divisor.Length;i++){next[i]^=divisor[i];next[i+1]^=Multiply(divisor[i],root);}
            divisor=next;root=Multiply(root,2);
        }
        var dividend=new byte[data.Length+7];Buffer.BlockCopy(data,0,dividend,0,data.Length);
        for(int i=0;i<data.Length;i++)
        {
            byte leading=dividend[i];for(int j=0;j<divisor.Length;j++)dividend[i+j]^=Multiply(leading,divisor[j]);
        }
        return dividend[^7..];
    }
}
