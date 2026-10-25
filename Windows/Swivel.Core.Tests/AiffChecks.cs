using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using Swivel.Core;

internal static class AiffChecks
{
    internal static byte[] Fixture(int bits, int channels, int rate, int[] samples, string? codec = null, int offset = 0, string name = "", bool soundFirst = false)
    {
        using var stream=new MemoryStream();stream.Write("FORM"u8);stream.Write(new byte[4]);stream.Write(codec is null?"AIFF"u8:"AIFC"u8);
        void U16(Stream s,int n){Span<byte>b=stackalloc byte[2];BinaryPrimitives.WriteUInt16BigEndian(b,(ushort)n);s.Write(b);}
        void U32(Stream s,uint n){Span<byte>b=stackalloc byte[4];BinaryPrimitives.WriteUInt32BigEndian(b,n);s.Write(b);}
        void Chunk(string id,byte[] data){stream.Write(System.Text.Encoding.ASCII.GetBytes(id));U32(stream,(uint)data.Length);stream.Write(data);if((data.Length&1)!=0)stream.WriteByte(0);}
        using var comm=new MemoryStream();U16(comm,channels);U32(comm,(uint)(samples.Length/channels));U16(comm,bits);
        int exponent=BitOperations.Log2((uint)rate);U16(comm,16383+exponent);Span<byte>mant=stackalloc byte[8];BinaryPrimitives.WriteUInt64BigEndian(mant,(ulong)rate<<(63-exponent));comm.Write(mant);
        if(codec is not null){comm.Write(System.Text.Encoding.ASCII.GetBytes(codec));comm.WriteByte((byte)name.Length);comm.Write(System.Text.Encoding.ASCII.GetBytes(name));if(((name.Length+1)&1)!=0)comm.WriteByte(0);}
        using var sound=new MemoryStream();U32(sound,(uint)offset);U32(sound,0);sound.Write(new byte[offset]);
        foreach(int sample in samples){uint value=unchecked((uint)sample);for(int i=0;i<bits/8;i++){int index=codec=="sowt"?i:bits/8-1-i;sound.WriteByte((byte)(value>>(index*8)));}}
        if(soundFirst)Chunk("SSND",sound.ToArray());
        if(codec is not null){using var ver=new MemoryStream();U32(ver,0xa2805140);Chunk("FVER",ver.ToArray());}
        Chunk("COMM",comm.ToArray());if(!soundFirst)Chunk("SSND",sound.ToArray());
        byte[] bytes=stream.ToArray();BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4),bytes.Length-8);return bytes;
    }
    public static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        byte[] Render(byte[] bytes,bool preview=false){using var input=new MemoryStream(bytes);using var output=new MemoryStream();AiffWave.Export(input,output,default,preview);check(input.ToArray().SequenceEqual(bytes)&&input.CanRead&&output.CanWrite,"AIFF leaves source and streams owned");return output.ToArray();}
        var rng=new Random(3401);
        foreach(int bits in new[]{8,16,24,32})foreach(int channels in new[]{1,2})foreach(string? codec in new string?[]{null,"NONE","sowt"})foreach(int rate in new[]{8000,11025,44100,192000})
        {
            long scale=1L<<(bits-1);int[] samples=Enumerable.Range(0,4097*channels).Select(_=>(int)rng.NextInt64(-scale,scale)).ToArray();samples[0]=(int)-scale;samples[1]=(int)(scale-1);samples[2]=0;
            byte[] source=Fixture(bits,channels,rate,samples,codec,1,"codec",soundFirst:channels==2);using var input=new MemoryStream(source);var info=AiffWave.Read(input,default);
            check(info.Rate==rate&&info.Channels==channels&&info.Bits==bits&&info.Frames==4097&&info.LittleEndian==(codec=="sowt")&&info.CompressedForm==(codec is not null),"AIFF/AIFC facts with reordered chunks and offset");
            byte[] output=Render(source);check(WaveChecks.Decode(output).SequenceEqual(samples),"All original signed integer values and stereo order preserved across4096 boundary");
            using var saved=new MemoryStream(output);var wave=PcmWave.Read(saved,default);check(wave.Rate==rate&&wave.Channels==channels&&wave.Bits==bits&&wave.Frames==4097&&output.Length==info.OutputBytes(),"AIFF target format and complete RIFF budget agree");
            using var expected=new MemoryStream();PcmWave.Export(saved,expected,new(0,wave.Seconds),default,true);check(Render(source,true).SequenceEqual(expected.ToArray()),"AIFF preview equals saved integer quantization thenPCM16");
        }
        foreach(int count in new[]{1,2,3,80001})foreach(string? codec in new string?[]{null,"NONE","sowt"})foreach(int offset in new[]{0,1,7})
        {
            int[] samples=Enumerable.Range(0,count).Select(i=>i%256-128).ToArray();byte[] source=Fixture(8,1,8000,samples,codec,offset,new string('x',255));
            byte[] output=Render(source);check(WaveChecks.Decode(output).SequenceEqual(samples),"AIFF8 signed bias is correct with odd/even sound payloads and offsets");
            using var full=new MemoryStream(output);using var expected=new MemoryStream();PcmWave.Export(full,expected,new(0,(double)count/8000),default,true);check(Render(source,true).SequenceEqual(expected.ToArray()),"AIFF8 long preview is correct first10seconds");
        }
        foreach(var (codec,bits) in new[]{("twos",16),("in24",24),("in32",32)})
        {
            int[] values={-(1<<(bits-1)),(int)((1L<<(bits-1))-1),0,123};
            check(WaveChecks.Decode(Render(Fixture(bits,1,44100,values,codec))).SequenceEqual(values),"Common AIFC BE integer tags preserve all samples");
            int wrong=bits==16?24:16;using var bad=new MemoryStream(Fixture(wrong,1,8000,new[]{0},codec));reject(()=>AiffWave.Read(bad,default),"AIFC typed integer label must match width");
        }
        byte[] canonical=Fixture(16,1,8000,new[]{-32768,32767,0});
        void Bad(Action<byte[]> change,string label){byte[] source=canonical.ToArray();change(source);using var input=new MemoryStream(source);using var output=new MemoryStream();reject(()=>AiffWave.Export(input,output,default,true),label);check(output.Length==0,"Bad AIFF fails before output header");}
        Bad(x=>x[0]=0,"Wrong form rejected");Bad(x=>x[4]++,"FORM size mismatch rejected");Bad(x=>x[21]=3,"Multichannel rejected");Bad(x=>x[27]=20,"Non-byte PCM widths rejected");Bad(x=>x[25]=0,"Empty frames rejected");
        Bad(x=>x[28]|=0x80,"Negative extended80 rate rejected");Bad(x=>x[30]&=0x7f,"Unnormal extended80 significand rejected");Bad(x=>x[37]=1,"Sub-double-ulp fractional rate rejected without rounding");Bad(x=>{x[28]=0x7f;x[29]=0xff;},"Infinity/NaN rate rejected");Bad(x=>x[53]=1,"SSND block size nonzero rejected");Bad(x=>x[49]=99,"SSND offset beyond containing data rejected");
        foreach(int rate in new[]{7999,192001}){byte[] bytes=Fixture(16,1,rate,new[]{0});using var input=new MemoryStream(bytes);reject(()=>AiffWave.Read(input,default),"Rate outside8–192k rejected");}
        foreach(string codec in new[]{"fl32","fl64","ima4","ulaw","in24","in32","23ni","42ni","raw "}){using var input=new MemoryStream(Fixture(16,1,8000,new[]{0},codec));reject(()=>AiffWave.Read(input,default),"Closed integer encoding set rejects unsupported AIFC");}
        foreach(int length in new[]{0,1,2,254,255})using(var input=new MemoryStream(Fixture(16,1,8000,new[]{0},"NONE",name:new string('x',length))))check(AiffWave.Read(input,default).Frames==1,"Pascal codec name legal lengths include internal zero pad");
        byte[] aifc=Fixture(16,1,8000,new[]{0},"NONE");
        void BadC(Action<byte[]> change,string label){byte[] source=aifc.ToArray();change(source);using var input=new MemoryStream(source);reject(()=>AiffWave.Read(input,default),label);}
        BadC(x=>x[20]=0,"WrongFVER timestamp refused");BadC(x=>x[54]=2,"Pascal name longer than COMM refused");BadC(x=>x[55]=1,"Pascal internal pad must be zero");
        byte[] missing=aifc[..12].Concat(aifc[24..]).ToArray();BinaryPrimitives.WriteInt32BigEndian(missing.AsSpan(4),missing.Length-8);using(var input=new MemoryStream(missing))reject(()=>AiffWave.Read(input,default),"AIFC requiresFVER");
        byte[] Append(byte[] source,byte[] added){var bytes=source.Concat(added).ToArray();BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4),bytes.Length-8);return bytes;}
        foreach(byte[] added in new[]{canonical[12..38],canonical[38..],aifc[12..24]})using(var input=new MemoryStream(Append(canonical,added)))reject(()=>AiffWave.Read(input,default),"DuplicateCOMM/SSND or AIFF FVER refused");
        using(var input=new MemoryStream(Append(aifc,aifc[12..24])))reject(()=>AiffWave.Read(input,default),"Duplicate AIFC FVER refused");
        byte[] unknown={(byte)'A',(byte)'N',(byte)'N',(byte)'O',0,0,0,1,255,0};using(var input=new MemoryStream(Append(canonical,unknown)))check(AiffWave.Read(input,default).Frames==3,"Unknown metadata skipped without decoding");
        unknown[^1]=1;using(var input=new MemoryStream(Append(canonical,unknown)))reject(()=>AiffWave.Read(input,default),"Nonzero external pad refused");
        unknown[^1]=0;using(var input=new MemoryStream(Append(canonical,unknown[..^1])))reject(()=>AiffWave.Read(input,default),"Missing external pad refused");
        byte[] empty={(byte)'A',(byte)'N',(byte)'N',(byte)'O',0,0,0,0};using(var input=new MemoryStream(Append(canonical,Enumerable.Range(0,1022).SelectMany(_=>empty).ToArray())))check(AiffWave.Read(input,default).Frames==3,"1024 chunks accepted");using(var input=new MemoryStream(Append(canonical,Enumerable.Range(0,1023).SelectMany(_=>empty).ToArray())))reject(()=>AiffWave.Read(input,default),"1025 chunks refused");
        long frames=(PcmWave.MaxBytes-68)/4;check(new AiffInfo(192000,1,32,frames,54,false,false).OutputBytes()==PcmWave.MaxBytes,"Exact512MiB output includes extended header");reject(()=>new AiffInfo(192000,1,32,frames+1,54,false,false).OutputBytes(),"One frame beyond complete output refused");check(new AiffInfo(8000,1,8,8000L*7200,54,false,false).OutputBytes()>0,"Two hours accepted");reject(()=>new AiffInfo(8000,1,8,8000L*7200+1,54,false,false).OutputBytes(),"More than two hours refused");
        using(var same=new MemoryStream(canonical))reject(()=>AiffWave.Export(same,same,default),"Aliased AIFF streams refused");using(var input=new MemoryStream(canonical))using(var output=new MemoryStream()){reject(()=>AiffWave.Export(input,output,new CancellationToken(true)),"Precancelled AIFF export");check(output.Length==0,"Precancelled AIFF writes nothing");}
        using(var token=new CancellationTokenSource())using(var input=new MemoryStream(Fixture(32,2,8000,new int[20000])))using(var output=new CancelOutput(token))reject(()=>AiffWave.Export(input,output,token.Token),"AIFF write-block cancellation observed");
        using(var input=new BrokenInput(canonical))using(var output=new MemoryStream())reject(()=>AiffWave.Export(input,output,default),"AIFF damaged sample read propagates without padding audio");
        check(Catalog.Available(new Prefs(),new[]{"clip.AIFC"}).Any(x=>x.Id=="audio.aiff")&&!Catalog.Available(new Prefs(),new[]{"a.wav"}).Any(x=>x.Id=="audio.aiff")&&!Catalog.Available(new Prefs(),new[]{"a.aiff","b.aif"}).Any(x=>x.Id=="audio.aiff"),"AIFF action requires one explicitly supplied supported extension");
    }
    private sealed class CancelOutput(CancellationTokenSource token):MemoryStream{public override void Write(ReadOnlySpan<byte> bytes){base.Write(bytes);if(Length>100)token.Cancel();}}
    private sealed class BrokenInput(byte[] bytes):MemoryStream(bytes){public override int Read(Span<byte> bytes){if(Position>=54)throw new IOException("Injected sample read failure");return base.Read(bytes);}}
}
