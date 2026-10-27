using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Threading;
using Swivel.Core;

internal static class WaveChecks
{
    public static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        foreach (int bits in new[] { 8,16,24,32 }) foreach (int channels in new[] { 1,2 })
        {
            int half = bits == 32 ? int.MaxValue : (1 << (bits-1))-1;
            int[] samples = Enumerable.Range(0,80*channels).Select(i => i%5 == 0 ? -half-1 : i%5 == 1 ? half : i%5 == 2 ? 0 : i%5 == 3 ? half/2 : -half/2).ToArray();
            byte[] bytes = Fixture(bits,channels,samples); using var input = new MemoryStream(bytes); var info = PcmWave.Read(input,default);
            check(info.Rate == 8000 && info.Channels == channels && info.Bits == bits && info.Frames == 80 && info.DataOffset == 44,"WAV parsed header "+bits+"/"+channels);
            using var exact = new MemoryStream(); PcmWave.Export(input,exact,new(0,info.Seconds),default);
            check(Decode(exact.ToArray()).SequenceEqual(samples),"WAV neutral preserves all integer bits including extremes "+bits);
            check(BinaryPrimitives.ReadUInt16LittleEndian(exact.ToArray().AsSpan(20)) == (bits > 16 ? 0xfffe : 1),"Output high precision uses EXTENSIBLE header");
            using var cut = new MemoryStream(); PcmWave.Export(input,cut,new(3d/8000,17d/8000),default);
            var cutInfo = PcmWave.Read(cut,default);
            check(cutInfo.Frames == 14 && cutInfo.Bits == bits && cutInfo.Channels == channels && cut.ToArray().AsSpan((int)cutInfo.DataOffset,(int)cutInfo.DataBytes).SequenceEqual(bytes.AsSpan(44+3*info.Block,14*info.Block)),"WAV cut sample alignment/order "+bits);
            using var silent = new MemoryStream(); PcmWave.Export(input,silent,new(0,info.Seconds,0),default);
            check(Decode(silent.ToArray()).All(x => x == 0),"WAV volume zero including unsigned PCM8 "+bits);
            using var halfVolume = new MemoryStream(); PcmWave.Export(input,halfVolume,new(0,info.Seconds,.5),default);
            check(Decode(halfVolume.ToArray()).SequenceEqual(samples.Select(x => (int)Math.Round(x*.5,MidpointRounding.AwayFromZero))),"WAV half amplitude "+bits);
            using var loud = new MemoryStream(); long clipped = PcmWave.Export(input,loud,new(0,info.Seconds,4),default);
            check(clipped > 0 && Decode(loud.ToArray())[0] == -half-1 && Decode(loud.ToArray())[1] == half,"WAV gain saturates not wraps "+bits);
            var peaks = PcmWave.Peaks(input,7,default); check(peaks.Length == 7 && peaks.All(x => x is > 0 and <= 1),"WAV envelope finite and bounded");
            using var preview = new MemoryStream(); PcmWave.Export(input,preview,new(0,info.Seconds),default,true); check(PcmWave.Read(preview,default).Bits == 16,"PCM16 preview for every source precision");
            check(Decode(preview.ToArray()).SequenceEqual(samples.Select(x=>(int)Math.Clamp(Math.Round(x/Math.Pow(2,bits-1)*32768,MidpointRounding.AwayFromZero),-32768,32767))),"Preview precision conversion rounds and saturates exact samples "+bits);
            check(input.CanRead && exact.CanWrite && input.ToArray().SequenceEqual(bytes),"WAV leaves streams owned and input unchanged");
        }
        byte[] level = Fixture(16,1,Enumerable.Repeat(16000,800).ToArray()); using var constant = new MemoryStream(level); var ci = PcmWave.Read(constant,default);
        using var fade = new MemoryStream(); PcmWave.Export(constant,fade,new(0,.1,1,.025,.025),default); int[] faded = Decode(fade.ToArray());
        check(faded[0] == 0 && faded[199] == 16000 && faded[600] == 16000 && faded[^1] == 0 && faded[400] == 16000,"Fade exact endpoints and plateau");
        check(faded.Take(200).SequenceEqual(faded.Reverse().Take(200)),"Symmetric fade envelopes");
        using var overlap = new MemoryStream(); PcmWave.Export(constant,overlap,new(0,.1,1,.1,.1),default); int[] overlapping = Decode(overlap.ToArray());
        check(overlapping[0] == 0 && overlapping[^1] == 0 && overlapping.Max() is >= 3990 and <= 4001,"Overlapping fades multiply without exceeding source");
        using var shortFade = new MemoryStream(); PcmWave.Export(constant,shortFade,new(0,3d/8000,1,1d/8000,1d/8000),default);
        check(Decode(shortFade.ToArray()).SequenceEqual(new[] {0,16000,0}),"Single-frame fade is deterministic");
        using var odd = new MemoryStream(Fixture(8,1,new[] {-128,0,127})); using var oddOut = new MemoryStream(); PcmWave.Export(odd,oddOut,new(0,3d/8000),default);
        check(oddOut.Length == 48 && oddOut.ToArray()[47] == 0 && PcmWave.Read(oddOut,default).Frames == 3,"Odd data pad excluded from frame count");
        byte[] longBytes = Fixture(16,2,Enumerable.Repeat(10000,8000*12*2).ToArray()); using var longInput = new MemoryStream(longBytes);
        using var shortOut = new MemoryStream(); PcmWave.Export(longInput,shortOut,new(0,12,1,0,1),default,true);
        check(PcmWave.Read(shortOut,default).Seconds == 10 && Decode(shortOut.ToArray())[^1] == 10000,"Preview bounded to 10 seconds without moving fade-out earlier");
        using var full = new MemoryStream(); PcmWave.Export(longInput,full,new(0,12,1,0,1),default); check(PcmWave.Read(full,default).Seconds == 12 && Decode(full.ToArray())[^1] == 0,"Export retains full range/fade");
        foreach (var edit in new[] { new AudioEdit(-1,.1),new(0,0),new(0,1),new(0,.1,4.1),new(0,.1,-.01),new(0,.1,1,.2),new(0,.1,1,0,-1),new(double.NaN,.1),new(0,double.PositiveInfinity),new(0,.1,double.NaN),new(0,.1,1,double.NaN),new(0,.1,1,0,double.NaN),new(0,.1/8000) })
            reject(() => edit.Range(ci),"WAV rejects invalid ranges/numbers/fades");
        check(new AudioEdit(.49/8000,1.51/8000).Range(ci).Count == 2,"WAV boundaries round to nearest frame");
        void Invalid(byte[] bytes,string name) { using var stream = new MemoryStream(bytes); reject(() => PcmWave.Read(stream,default),name); }
        void Mutation(int offset,byte value,string name) { var bytes=(byte[])level.Clone(); bytes[offset]=value; Invalid(bytes,name); }
        Mutation(0,(byte)'X',"No disguised non-RIFF input"); Mutation(8,(byte)'X',"No non-WAVE RIFF"); Mutation(4,1,"RIFF size must match stream"); Mutation(20,3,"Float WAV explicitly refused"); Mutation(22,6,"Multichannel WAV refused"); Mutation(24,0,"Inconsistent rate/average rejected"); Mutation(28,1,"Invalid byte rate"); Mutation(32,1,"Invalid block align"); Mutation(34,20,"Packed valid-bit formats refused"); Mutation(40,255,"Oversized chunk bounds");
        Invalid(level[..^1],"Truncated data/pad rejected");
        var metadata = ExtraChunk(level,"JUNK",new byte[] {1,2,3}); using(var m = new MemoryStream(metadata))
        {
            check(PcmWave.Read(m,default).Frames == 800,"Odd unknown chunks skipped with padding"); using var stripped = new MemoryStream(); PcmWave.Export(m,stripped,new(0,.1),default); check(stripped.ToArray().SequenceEqual(level),"Metadata omitted, audio exact");
        }
        Invalid(ExtraChunk(level,"fmt ",level[20..36]),"Duplicate fmt rejected"); Invalid(ExtraChunk(level,"data",new byte[2]),"Duplicate data rejected");
        var extended = Extensible(level,4); using(var e = new MemoryStream(extended)) check(PcmWave.Read(e,default).Bits == 16,"Extensible PCM GUID accepted");
        var stereoExt = Extensible(Fixture(24,2,new[]{0,1,2,3}),3); using(var e = new MemoryStream(stereoExt)) check(PcmWave.Read(e,default).Channels == 2,"Extensible stereo accepted");
        extended[44] = 3; Invalid(extended,"Extensible float GUID refused"); extended = Extensible(level,3); Invalid(extended,"Mismatched channel mask refused"); extended = Extensible(level,4); extended[38]=12; Invalid(extended,"Valid bits must equal container bits");
        using var oversized = new SizeStream(); reject(() => PcmWave.Read(oversized,default),"WAV byte budget before reading");
        foreach(int seconds in new[]{7200,7201})
        {
            byte[] header=Fixture(8,1,new[]{0})[..44];long length=44L+seconds*8000L;
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4),(int)length-8);BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40),seconds*8000);
            using var virtualWave=new DeclaredStream(header,length);
            if(seconds==7200)check(PcmWave.Read(virtualWave,default).Seconds==7200,"Exactly two hours accepted without whole-file allocation");
            else reject(()=>PcmWave.Read(virtualWave,default),"Two hours plus one second refused");
        }
        byte[] manyChunks=level;for(int i=0;i<1022;i++)manyChunks=ExtraChunk(manyChunks,"JUNK",Array.Empty<byte>());
        using(var maximumChunks=new MemoryStream(manyChunks))check(PcmWave.Read(maximumChunks,default).Frames==800,"Exactly 1024 chunks accepted");
        Invalid(ExtraChunk(manyChunks,"JUNK",Array.Empty<byte>()),"1025th chunk refused");
        reject(() => PcmWave.Read(constant,new CancellationToken(true)),"WAV initial cancellation");
        using var cancel = new CancellationTokenSource(); using var cancelling = new CancelStream(level,cancel); using var cancelledOutput = new MemoryStream();
        reject(() => PcmWave.Export(cancelling,cancelledOutput,new(0,.1),cancel.Token),"WAV cancellation during audio read");
        using var peakCancel=new CancellationTokenSource();using var peakInput=new CancelStream(level,peakCancel);reject(()=>PcmWave.Peaks(peakInput,600,peakCancel.Token),"Waveform scan observes cancellation after sample read");
        check(Catalog.Available(new Prefs(),new[] {"talk.WAV"}).Any(x => x.Id == "audio.wav") && !Catalog.Available(new Prefs(),new[] {"talk.mp3"}).Any(x => x.Id == "audio.wav") && !Catalog.Available(new Prefs(),new[] {"a.wav","b.wav"}).Any(x => x.Id == "audio.wav"),"WAV explicit single-file catalog gate");
        // External decoder fixtures, opt-in and wholly inside the development tree.
        string? fixtures = Environment.GetEnvironmentVariable("SWIVEL_WAVE_FIXTURES");
        if (fixtures is not null) { Directory.CreateDirectory(fixtures); File.WriteAllBytes(Path.Combine(fixtures,"trim-fade.wav"),fade.ToArray()); File.WriteAllBytes(Path.Combine(fixtures,"stereo-24.wav"),stereoExt); using var highInput=new MemoryStream(Fixture(32,2,new[]{int.MinValue,int.MaxValue,0,1234567}));using var highOutput=new MemoryStream();PcmWave.Export(highInput,highOutput,new(0,2d/8000),default);File.WriteAllBytes(Path.Combine(fixtures,"export-32.wav"),highOutput.ToArray()); }
    }
    internal static byte[] Fixture(int bits,int channels,int[] values)
    {
        int size=values.Length*(bits/8); byte[] result=new byte[44+size+(size&1)];
        "RIFF"u8.CopyTo(result); BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4),(uint)(result.Length-8)); "WAVEfmt "u8.CopyTo(result.AsSpan(8)); BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(16),16);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(20),1); BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(22),(ushort)channels); BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(24),8000); BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(28),8000*channels*(bits/8)); BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(32),(ushort)(channels*(bits/8))); BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(34),(ushort)bits); "data"u8.CopyTo(result.AsSpan(36)); BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(40),size);
        for(int i=0;i<values.Length;i++) for(int j=0;j<bits/8;j++) result[44+i*(bits/8)+j]=(byte)((bits==8?values[i]+128:values[i])>>(j*8)); return result;
    }
    internal static int[] Decode(byte[] bytes)
    {
        int bits=bytes[34], at=12;while(!bytes.AsSpan(at,4).SequenceEqual("data"u8)){int n=BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at+4));at+=8+n+(n&1);}int size=BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at+4));at+=8;var values=new int[size/(bits/8)];
        for(int i=0;i<values.Length;i++) { int p=at+i*(bits/8); values[i]=bits switch {8=>bytes[p]-128,16=>(short)(bytes[p]|bytes[p+1]<<8),24=>(bytes[p]|bytes[p+1]<<8|bytes[p+2]<<16)<<8>>8,_=>bytes[p]|bytes[p+1]<<8|bytes[p+2]<<16|bytes[p+3]<<24}; } return values;
    }
    private static byte[] ExtraChunk(byte[] source,string id,byte[] data)
    {
        using var stream=new MemoryStream(); stream.Write(source); using var writer=new BinaryWriter(stream,System.Text.Encoding.ASCII,true); writer.Write(System.Text.Encoding.ASCII.GetBytes(id)); writer.Write(data.Length); writer.Write(data); if((data.Length&1)!=0)writer.Write((byte)0); var result=stream.ToArray(); BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4),result.Length-8); return result;
    }
    private static byte[] Extensible(byte[] source,uint mask)
    {
        var result=new byte[source.Length+24]; source.AsSpan(0,36).CopyTo(result); source.AsSpan(36).CopyTo(result.AsSpan(60)); BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4),result.Length-8); BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(16),40); BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(20),0xfffe); BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(36),22); result[38]=source[34]; BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(40),mask); new byte[]{1,0,0,0,0,0,16,0,128,0,0,170,0,56,155,113}.CopyTo(result,44); return result;
    }
    private sealed class SizeStream : MemoryStream { public override long Length => PcmWave.MaxBytes+1; }
    private sealed class DeclaredStream(byte[] header,long length) : MemoryStream(header) { public override long Length => length; }
    private sealed class CancelStream(byte[] bytes,CancellationTokenSource source) : MemoryStream(bytes)
    {
        public override int Read(Span<byte> buffer) { int n=base.Read(buffer); if(Position>44)source.Cancel(); return n; }
    }
}
