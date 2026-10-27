using System;
using System.IO;
using System.Buffers.Binary;
using System.Linq;
using System.Numerics;
using System.Threading;
using Swivel.Core;

internal static class WaveQuietSampleChecks
{
    public static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        byte[] Fixture(int rate,int bits,int channels,int[] values)
        {
            var data=WaveChecks.Fixture(bits,channels,values);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(24),rate);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(28),rate*channels*(bits/8));return data;
        }
        var random=new Random(5701);
        foreach(int rate in new[]{8000,11025,44100,192000})foreach(int bits in new[]{8,16,24,32})foreach(int channels in new[]{1,2})
        for(int iteration=0;iteration<8;iteration++)
        {
            int window=(int)Math.Round(rate*.05,MidpointRounding.AwayFromZero),frames=window*(1+iteration%4)+iteration;
            long scale=1L<<(bits-1);int[] values=Enumerable.Range(0,frames*channels).Select(i=>(int)(random.NextInt64(-scale,scale))).ToArray();
            // Last tail can be strictly quieter than every complete grid window.
            if(iteration==7)Array.Clear(values,(frames-window)*channels,window*channels);
            byte[] data=Fixture(rate,bits,channels,values);using var source=new MemoryStream(data);source.Position=data.Length;
            var actual=WaveQuietSample.Find(source,window,default);var starts=Enumerable.Range(0,frames/window).Select(x=>(long)x*window).Append(frames-window).Distinct().ToArray();
            BigInteger Energy(long first)
            {BigInteger sum=0;for(long i=first*channels;i<(first+window)*channels;i++)sum+=(BigInteger)values[i]*values[i];return sum;}
            long expected=starts.OrderBy(Energy).ThenBy(x=>x).First();
            check(actual.First==expected&&actual.Count==window&&actual.Rate==rate&&actual.Windows==starts.Length,"Quiet candidate equals independent integer energy oracle across formats/tails");
            double rms=Math.Sqrt((double)Energy(expected)/(window*channels))/scale;
            check(Math.Abs(actual.RootMeanSquare-rms)<=2e-15&&actual.RootMeanSquare is >=0 and <=1,"RMS is finite normalized all-channel energy");
            var info=PcmWave.Read(source,default);check(new WaveNoise(actual.Start,actual.End).Range(info)==new NoiseSample(expected,window),"Suggested exact frame boundaries round-trip into manual noise range");
            check(source.CanRead&&source.ToArray().SequenceEqual(data),"Find never mutates or owns the input");
        }
        int[] opposite=new int[1200*2];for(int frame=0;frame<1200;frame++){int value=frame<400?20000:frame<800?5000:10000;opposite[frame*2]=value;opposite[frame*2+1]=-value;}
        using(var input=new MemoryStream(Fixture(8000,16,2,opposite)))check(WaveQuietSample.Find(input,400,default).First==400,"Opposite channels cannot cancel and masquerade as silence");
        // Both window energies round to the same double: exact UInt128 still sees a difference of one.
        int[] close=Enumerable.Repeat(int.MinValue,800).ToArray();close[0]=0;close[400]=1;
        using(var input=new MemoryStream(Fixture(8000,32,1,close)))check(WaveQuietSample.Find(input,400,default).First==0,"32-bit integer energy retains differences below double precision");
        Array.Reverse(close);
        using(var input=new MemoryStream(Fixture(8000,32,1,close)))check(WaveQuietSample.Find(input,400,default).First==400,"Later exact quieter result wins below double precision");
        byte[] silent=Fixture(11025,24,2,new int[2206]);
        using(var input=new MemoryStream(silent))check(WaveQuietSample.Find(input,551,default) is {First:0,Count:551,Windows:3,RootMeanSquare:0},"Exact ties choose earliest absolute frame and nonaligned tail counted once");
        foreach(long invalid in new[]{long.MinValue,-1L,0L,550L,1104L,22051L,long.MaxValue})
            using(var input=new MemoryStream(silent))reject(()=>WaveQuietSample.Find(input,invalid,default),"Invalid sample length refused before content scan");
        using(var input=new MemoryStream(Fixture(8000,8,1,new int[399])))reject(()=>WaveQuietSample.Find(input,400,default),"Too short recording refuses full candidate");
        using(var input=new MemoryStream(silent))reject(()=>WaveQuietSample.Find(input,551,new CancellationToken(true)),"Pre-cancelled search refused");
        using(var cancel=new CancellationTokenSource())using(var input=new ShortInput(silent,cancel))reject(()=>WaveQuietSample.Find(input,551,cancel.Token),"Content-read cancellation prevents returning late suggestion");
        using(var input=new ShortInput(silent,null))check(WaveQuietSample.Find(input,551,default).First==0&&input.MaximumRequest<=4096*6,"ReadExactly tolerates short reads within a fixed buffer");
        byte[] original=Fixture(8000,16,1,Enumerable.Range(0,2000).Select(x=>x%127).ToArray());
        byte[] Render(Stream input){using var output=new MemoryStream();WaveDenoiser.Export(input,output,new(0,.05),default);return output.ToArray();}
        using(var input=new MemoryStream(original)){byte[] before=Render(input);WaveQuietSample.Find(input,400,default);check(Render(input).SequenceEqual(before),"Finding without adoption leaves original manual denoise byte-for-byte unchanged");}
    }
    private sealed class ShortInput(byte[] bytes,CancellationTokenSource? cancel):MemoryStream(bytes)
    {
        public int MaximumRequest{get;private set;}
        public override int Read(Span<byte> buffer){MaximumRequest=Math.Max(MaximumRequest,buffer.Length);int n=base.Read(buffer[..Math.Min(buffer.Length,7)]);if(Position>68)cancel?.Cancel();return n;}
    }
}
