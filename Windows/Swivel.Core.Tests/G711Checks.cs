using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Swivel.Core;

internal static class G711Checks
{
    internal static byte[] Fixture(byte[] data,bool mu=false,int channels=1,int rate=8000,string order="fcd",int fmtLength=18,uint? frames=null)
    {
        using var output=new MemoryStream();using var w=new BinaryWriter(output);w.Write("RIFF"u8);w.Write(0);w.Write("WAVE"u8);
        foreach(char part in order)
        {
            if(part=='f'){w.Write("fmt "u8);w.Write(fmtLength);w.Write((ushort)(mu?7:6));w.Write((ushort)channels);w.Write(rate);w.Write(rate*channels);w.Write((ushort)channels);w.Write((ushort)8);if(fmtLength>=18)w.Write((ushort)0);for(int i=18;i<fmtLength;i++)w.Write((byte)0);}
            else if(part=='c'){w.Write("fact"u8);w.Write(4);w.Write(frames??(uint)(data.Length/channels));}
            else if(part=='d'){w.Write("data"u8);w.Write(data.Length);w.Write(data);if((data.Length&1)!=0)w.Write((byte)99);}
            else{w.Write("JUNK"u8);w.Write(3);w.Write(new byte[]{1,2,3,123});}
        }
        var bytes=output.ToArray();BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4),bytes.Length-8);return bytes;
    }
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        byte[] Convert(byte[] source,bool preview=false){using var input=new MemoryStream(source);using var output=new MemoryStream();G711Wave.Export(input,output,default,preview);check(input.ToArray().SequenceEqual(source)&&input.CanRead&&output.CanWrite,"G711 caller streams and original bytes preserved");return output.ToArray();}
        foreach(bool mu in new[]{false,true})foreach(int channels in new[]{1,2})foreach(string order in new[]{"fcd","fdc","cfd","cdf","dfc","dcf","xfcdx"})
        {
            var payload=Enumerable.Range(0,channels*4097).Select(i=>(byte)i).ToArray();var source=Fixture(payload,mu,channels,11025,order);using var input=new MemoryStream(source);var info=G711Wave.Read(input,default);
            check(info.MuLaw==mu&&info.Channels==channels&&info.Rate==11025&&info.Frames==4097&&info.DataBytes==payload.Length,"G711 fmt/fact/data orders, oddpad, source facts and4096 boundary");
            var saved=Convert(source);check(saved.Length==info.OutputBytes(),"G711 full44byte header plus double data budget");var decoded=WaveChecks.Decode(saved);
            for(int i=0;i<payload.Length;i++)check(decoded[i]==G711Wave.Decode(payload[i],mu),"G711 every ordered codeword retains channel interleave");
            using var pcm=new MemoryStream(saved);using var expected=new MemoryStream();PcmWave.Export(pcm,expected,new(0,info.Seconds),default,true);check(Convert(source,true).SequenceEqual(expected.ToArray()),"G711 preview matches saved PCM16 prefix exactly");
        }
        check(G711Wave.Decode(0xd5,false)==8&&G711Wave.Decode(0x55,false)==-8&&G711Wave.Decode(0xaa,false)==32256&&G711Wave.Decode(0x2a,false)==-32256,"A-law minima and signed maxima no invented exact zero");
        check(G711Wave.Decode(0x7f,true)==0&&G711Wave.Decode(0xff,true)==0&&G711Wave.Decode(0x80,true)==32124&&G711Wave.Decode(0,true)==-32124,"Mu-law signed zero and maxima without gain");
        foreach(bool mu in new[]{false,true})foreach(int code in Enumerable.Range(0,128))check(G711Wave.Decode((byte)code,mu)==-G711Wave.Decode((byte)(code^128),mu),"G711 sign inversion symmetry all codewords");
        var longSource=Fixture(Enumerable.Range(0,80001*2).Select(i=>(byte)i).ToArray(),true,2);var full=Convert(longSource);var preview=Convert(longSource,true);
        check(preview.Length==44+80000*2*2&&preview.AsSpan(44).SequenceEqual(full.AsSpan(44,preview.Length-44)),"G71110s exact PCM payload prefix, stereo fact counts frames not samples");
        byte[] basic=Fixture(new byte[]{1,2,3,4});
        void Bad(Action<byte[]> mutation,string name){var b=basic.ToArray();mutation(b);using var input=new MemoryStream(b);using var output=new MemoryStream();reject(()=>G711Wave.Export(input,output,default),name);check(output.Length==0,"Bad G711 structure writes no output header");}
        Bad(b=>b[0]=(byte)'X',"NonRIFF refused");Bad(b=>b[8]=(byte)'X',"NonWAVE refused");Bad(b=>b[4]--,"ExactRIFF total required");
        Bad(b=>b[20]=1,"IntegerPCM not reinterpreted");Bad(b=>b[20]=3,"Float not reinterpreted");Bad(b=>b[20]=2,"ADPCM not reinterpreted");Bad(b=>b[22]=3,"Otherchannels refused");Bad(b=>b[24]=0,"Bad rate refused");Bad(b=>b[28]++,"Average must equal rate times channels");Bad(b=>b[32]++,"Blockalign onecodeword per channel");Bad(b=>b[34]=16,"Not16bitcodewords");Bad(b=>b[36]=1,"Extra fmt bytes refused");Bad(b=>b[46]=9,"Factframe mismatch");Bad(b=>b[54]=0,"Empty data refused");Bad(b=>b[42]=3,"Fact shorter than4 refused");
        foreach(string order in new[]{"fd","fc","cd","ffcd","fccd","fcdd"}){using var input=new MemoryStream(Fixture(new byte[]{1,2},order:order));reject(()=>G711Wave.Read(input,default),"Missing/duplicate fmt/fact/data refused");}
        using(var input=new MemoryStream(Fixture(new byte[]{1,2},fmtLength:16)))reject(()=>G711Wave.Read(input,default),"Legacyfmt16 not silently accepted");
        using(var input=new MemoryStream(Fixture(new byte[]{1,2},fmtLength:20)))reject(()=>G711Wave.Read(input,default),"Extendedfmt not silently accepted");
        using(var input=new MemoryStream(Fixture(new byte[]{1,2,3},channels:2)))reject(()=>G711Wave.Read(input,default),"Partialstereoframe refused");
        var odd=Fixture(new byte[]{1});Array.Resize(ref odd,odd.Length-1);BinaryPrimitives.WriteInt32LittleEndian(odd.AsSpan(4),odd.Length-8);using(var input=new MemoryStream(odd))reject(()=>G711Wave.Read(input,default),"Missing odddata pad refused");
        byte[] Append(byte[] src,byte[] add){var all=src.Concat(add).ToArray();BinaryPrimitives.WriteInt32LittleEndian(all.AsSpan(4),all.Length-8);return all;}
        foreach(byte[] addition in new[]{Encoding.ASCII.GetBytes("LIST\x04\0\0\0wavl"),Encoding.ASCII.GetBytes("slnt\0\0\0\0")}){using var input=new MemoryStream(Append(basic,addition));reject(()=>G711Wave.Read(input,default),"Wave list/silence structures explicitly unsupported");}
        var zeros=Encoding.ASCII.GetBytes("JUNK\0\0\0\0");using(var input=new MemoryStream(Append(basic,Enumerable.Range(0,1021).SelectMany(_=>zeros).ToArray())))check(G711Wave.Read(input,default).Frames==4,"Exactly1024chunks accepted");
        using(var input=new MemoryStream(Append(basic,Enumerable.Range(0,1022).SelectMany(_=>zeros).ToArray())))reject(()=>G711Wave.Read(input,default),"1025chunks refused");
        long exact=(PcmWave.MaxBytes-44)/2;check(new G711Info(192000,2,false,58,exact).OutputBytes()==PcmWave.MaxBytes,"Exact512MiB PCM target including header accepted");
        foreach(bool pre in new[]{false,true})using(var input=new Sparse(exact+2))using(var output=new MemoryStream()){reject(()=>G711Wave.Export(input,output,default,pre),"Full PCM budget checked even for preview");check(output.Length==0&&input.BodyReads==0,"Overbudget G711 refuses before output header or audio reads");}
        check(new G711Info(8000,1,false,58,8000L*7200).OutputBytes()>0,"Exactly2h accepted");reject(()=>new G711Info(8000,1,false,58,8000L*7200+1).OutputBytes(),"2hplusframe refused");
        using(var input=new MemoryStream(basic))using(var output=new MemoryStream()){reject(()=>G711Wave.Export(input,output,new CancellationToken(true)),"Precancelled G711");check(output.Length==0,"Precancel writes no header");}
        using(var same=new MemoryStream(basic))reject(()=>G711Wave.Export(same,same,default),"Aliasedstream refused");
        using(var token=new CancellationTokenSource())using(var input=new CancelInput(longSource,token))using(var output=new MemoryStream())reject(()=>G711Wave.Export(input,output,token.Token),"Cancellation on encoded block read observed");
        using(var token=new CancellationTokenSource())using(var input=new MemoryStream(longSource))using(var output=new CancelOutput(token))reject(()=>G711Wave.Export(input,output,token.Token),"Cancellation on PCM block write observed");
        using(var input=new ShortInput(basic))using(var output=new MemoryStream()){G711Wave.Export(input,output,default);check(output.ToArray().SequenceEqual(Convert(basic)),"ReadExactly supports short reads");}
        foreach(string language in new[]{"en","zh-Hans","zh-Hant","ja"})foreach(string key in new[]{G711Wave.Invalid,G711Wave.Unsupported,G711Wave.Limit,G711Wave.Scope,G711Wave.Privacy,"Decode G.711 WAV…"})check(!string.IsNullOrWhiteSpace(Strings.L(key,language)),"G711 localized notices present");
        check(Catalog.Available(new Prefs(),new[]{"a.WAV"}).Any(x=>x.Id=="audio.g711")&&!Catalog.Available(new Prefs(),new[]{"a.mp3"}).Any(x=>x.Id=="audio.g711")&&!Catalog.Available(new Prefs(),new[]{"a.wav","b.wav"}).Any(x=>x.Id=="audio.g711"),"G711 requires exactly one WAV");var prefs=new Prefs();prefs.SetCustomActions(new[]{"audio.g711"});check(Prefs.Parse(prefs.Serialize()).CustomActionIds.Single()=="audio.g711","G711 stableMyWheel ID roundtrip");
    }
    private sealed class ShortInput(byte[] bytes):MemoryStream(bytes){public override int Read(Span<byte> buffer)=>base.Read(buffer[..Math.Min(3,buffer.Length)]);}
    private sealed class CancelInput(byte[] bytes,CancellationTokenSource token):MemoryStream(bytes){public override int Read(Span<byte> buffer){int n=base.Read(buffer);if(Position>=58+4096)token.Cancel();return n;}}
    private sealed class CancelOutput(CancellationTokenSource token):MemoryStream{public override void Write(ReadOnlySpan<byte> buffer){base.Write(buffer);if(Length>44)token.Cancel();}}
    private sealed class Sparse:Stream
    {
        private readonly byte[] head;private readonly long length;public int BodyReads;
        internal Sparse(long data){head=Fixture(new byte[]{0,0},channels:2,rate:192000)[..58];BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(4),(uint)(50+data+(data&1)));BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(46),(uint)(data/2));BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(54),(uint)data);length=58+data+(data&1);}
        public override int Read(Span<byte> buffer){int n=(int)Math.Min(buffer.Length,Length-Position);for(int i=0;i<n;i++){if(Position>=head.Length)BodyReads++;buffer[i]=Position<head.Length?head[(int)Position]:(byte)0;Position++;}return n;}
        public override int Read(byte[] b,int o,int c)=>Read(b.AsSpan(o,c));public override bool CanRead=>true;public override bool CanSeek=>true;public override bool CanWrite=>false;public override long Length=>length;public override long Position{get;set;}public override long Seek(long o,SeekOrigin s)=>Position=s==SeekOrigin.Begin?o:s==SeekOrigin.Current?Position+o:Length+o;public override void Flush(){}public override void SetLength(long n)=>throw new NotSupportedException();public override void Write(byte[] b,int o,int c)=>throw new NotSupportedException();
    }
}
