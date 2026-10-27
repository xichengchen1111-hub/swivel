using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Threading;
using Swivel.Core;

internal static class WaveMusicRateChecks
{
    public static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        byte[] Fixture(int rate,int bits,int channels,int frames,int seed)
        {
            long scale=1L<<(bits-1);var random=new Random(seed);
            var values=Enumerable.Range(0,frames*channels).Select(i=>i%23==0?(int)-scale:i%29==0?(int)(scale-1):(int)random.NextInt64(-scale/2,scale/2)).ToArray();
            byte[] data=WaveChecks.Fixture(bits,channels,values);BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(24),rate);BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(28),rate*channels*(bits/8));return data;
        }
        foreach(var rates in new[]{(8000,11025),(11025,8000),(8000,192000),(192000,8000),(44100,48000)})foreach(int bits in new[]{8,16,24,32})foreach(int channels in new[]{1,2})
        {
            int from=rates.Item1,to=rates.Item2;byte[] music=Fixture(from,bits,channels,803,bits+channels),primary=Fixture(to,24,2,2021,58);
            using var source=new MemoryStream(music);var original=PcmWave.Read(source,default);var plan=WaveMusicRatePlan.Create(original,to);
            using var prepared=PreparedWaveMusic.Prepare(source,to,default,original);using var converted=new MemoryStream();
            long clipped=WaveConverter.Export(source,converted,new(to,channels,bits),default);
            check(prepared.Info.Rate==to&&prepared.Info.Channels==channels&&prepared.Info.Bits==bits&&prepared.Info.Frames==plan.Frames&&prepared.Bytes==plan.Bytes&&prepared.Bytes==converted.Length&&prepared.ClippedSamples==clipped,"Prepared music matches complete converter plan, preserved layout/depth and clipping count");
            check(source.CanRead&&source.ToArray().SequenceEqual(music),"Preparation never mutates or owns original source");
            foreach(var settings in new[]{new WaveMusic(Gain:1),new(0,.7,true,LoopCrossfadeMilliseconds:2.0*1000/to),new(0,.4,true,Ducking:new(-35,.2))})foreach(bool preview in new[]{false,true})
            {
                using var main=new MemoryStream(primary);var info=PcmWave.Read(main,default);var edit=new AudioEdit(0,info.Seconds,.8);
                using var actual=new MemoryStream();using var expected=new MemoryStream();
                long a=prepared.ExportMix(main,actual,edit,settings,default,preview);long e=WaveMixer.Export(main,converted,expected,edit,settings,default,preview);
                check(a==e&&actual.ToArray().SequenceEqual(expected.ToArray()),"Mix/loop/ducking/preview all reuse the identical full prepared bytes");
            }
            reject(()=>PreparedWaveMusic.Prepare(source,to,default,original with{Rate=from==8000?11025:8000}),"Confirmed source metadata mismatch refuses preparation");
        }
        foreach(int bits in new[]{8,16,24,32})foreach(int channels in new[]{1,2})
        {
            int block=channels*(bits/8),header=bits>16?68:44;long frames=(WaveMusicRatePlan.MaxBytes-header)/block;
            var info=new WaveInfo(192000,channels,bits,header,frames*block);var plan=WaveMusicRatePlan.Create(info,192000);
            check(plan.Bytes<=WaveMusicRatePlan.MaxBytes&&WaveMusicRatePlan.MaxBytes-plan.Bytes<block+2,"128MiB budget includes full header/data/odd padding before allocation");
            reject(()=>WaveMusicRatePlan.Create(info with{DataBytes=(frames+1)*block},192000),"First over-budget full output refused without allocating source/target");
        }
        byte[] inputBytes=Fixture(8000,16,1,800,1);
        using(var input=new MemoryStream(inputBytes))
        {
            reject(()=>PreparedWaveMusic.Prepare(input,11025,new CancellationToken(true)),"Pre-cancelled prepare returns no partial object");
            using var owned=PreparedWaveMusic.Prepare(input,11025,default);using var main=new MemoryStream(Fixture(11025,16,1,800,2));
            reject(()=>owned.ExportMix(main,main,new(0,.05),new(),default),"Prepared mix refuses input/output alias before writing");
            owned.Dispose();using var output=new MemoryStream();reject(()=>owned.ExportMix(main,output,new(0,.05),new(),default),"Disposed preparation cannot mix stale data");
            check(input.CanRead,"Prepared disposal does not close caller source");
        }
        using(var cancel=new CancellationTokenSource())using(var input=new CancelInput(inputBytes,cancel))
        {
            reject(()=>PreparedWaveMusic.Prepare(input,11025,cancel.Token),"Read-time cancellation returns no partial preparation");check(input.CanRead,"Cancellation preserves input ownership");
        }
        foreach(int rate in new[]{0,7999,192001,int.MaxValue})using(var input=new MemoryStream(inputBytes))reject(()=>PreparedWaveMusic.Prepare(input,rate,default),"Invalid target rate rejected before output");
    }
    private sealed class CancelInput(byte[] bytes,CancellationTokenSource cancel):MemoryStream(bytes)
    {
        public override int Read(Span<byte> buffer){int n=base.Read(buffer);if(Position>44)cancel.Cancel();return n;}
    }
}
