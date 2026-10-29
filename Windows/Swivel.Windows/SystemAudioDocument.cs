using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Swivel.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;

namespace Swivel.Windows;

public sealed class SystemAudioDocument:IDisposable
{
    public const long MaxBytes=128L*1024*1024;
    public const string Scope="Decode the selected local file with Windows audio codecs to PCM16 WAV. Video, metadata, artwork, cues and loops are omitted. The decoder can change precision, priming or padding; this is not an archival or lossless copy. No codec, model or language resource is downloaded.";
    public const string Limits="Input and prepared output are each limited to 128 MiB; decoded audio is limited to two hours, mono/stereo and 8–192 kHz. The declared audio rate and channel count are retained, without an additional resampling step. Missing codecs, DRM, unsupported streams or changed output formats are refused. System codec calls may take time to cancel. These byte budgets are not a limit on total codec memory.";
    public const string Unsupported="Windows could not decode this file with the required PCM16 format and limits. No output was published. Installed codec availability can differ between Windows editions and machines.";
    private readonly NativeFiles.Input input;private readonly byte[] prepared;
    public WaveInfo Info{get;}
    public string Path=>input.Path;
    private SystemAudioDocument(NativeFiles.Input input,byte[] prepared,WaveInfo info){this.input=input;this.prepared=prepared;Info=info;}
    public static async Task<SystemAudioDocument> OpenAsync(string path,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();var input=NativeFiles.OpenRead(path);
        try
        {
            if(input.Stream.Length>MaxBytes)throw new UserError(Limits);
            SystemAudioInput.Identify(input.Stream,token);
            using var sourceView=new BoundedStream(input.Stream,MaxBytes,false,token);using var source=sourceView.AsRandomAccessStream();
            if(source.CanWrite||!source.CanRead)throw new UserError(Unsupported);
            var profile=await NativeAsync.Join(MediaEncodingProfile.CreateFromStreamAsync(source),token).ConfigureAwait(false);
            var audio=profile.Audio;if(audio is null||audio.ChannelCount is not 1 and not 2||audio.SampleRate is <8000 or >192000)throw new UserError(Unsupported);
            int rate=(int)audio.SampleRate,channels=(int)audio.ChannelCount;source.Seek(0);token.ThrowIfCancellationRequested();
            var targetProfile=MediaEncodingProfile.CreateWav(AudioEncodingQuality.Auto);targetProfile.Audio=AudioEncodingProperties.CreatePcm((uint)rate,(uint)channels,16);
            using var decoded=new MemoryStream();using var targetView=new BoundedStream(decoded,MaxBytes,true,token);using var target=targetView.AsRandomAccessStream();
            var transcoder=new MediaTranscoder{AlwaysReencode=true,HardwareAccelerationEnabled=false};
            var preparation=await NativeAsync.Join(transcoder.PrepareStreamTranscodeAsync(source,target,targetProfile),token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();if(!preparation.CanTranscode)throw new UserError(Unsupported);
            await NativeAsync.Join(preparation.TranscodeAsync(),token).ConfigureAwait(false);token.ThrowIfCancellationRequested();
            // Reject decoder substitution and validate the complete resulting WAV before
            // any file staging. Canonical export below strips native metadata chunks.
            var info=PcmWave.Read(decoded,token);if(info.Rate!=rate||info.Channels!=channels||info.Bits!=16)throw new UserError(Unsupported);
            using var canonical=new MemoryStream();PcmWave.Export(decoded,canonical,new(0,info.Seconds),token);
            if(canonical.Length>MaxBytes)throw new UserError(Limits);byte[] bytes=canonical.ToArray();using var check=new MemoryStream(bytes,false);var canonicalInfo=PcmWave.Read(check,token);
            token.ThrowIfCancellationRequested();return new(input,bytes,canonicalInfo);
        }
        catch{input.Dispose();throw;}
    }
    public byte[] Preview(CancellationToken token)
    {using var source=new MemoryStream(prepared,false);using var output=new MemoryStream();PcmWave.Export(source,output,new(0,Info.Seconds),token,preview:true);return output.ToArray();}
    public string Save(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();return NativeFiles.WriteBeside(input,System.IO.Path.GetFileNameWithoutExtension(input.Path)+" audio PCM","wav",output=>
        {using var source=new MemoryStream(prepared,false);PcmWave.Export(source,output,new(0,Info.Seconds),token);},token);
    }
    private bool detached;
    /// Hands the held original and the decoded PCM16 WAV to another document, which then owns the input.
    internal (NativeFiles.Input Input,byte[] Wav) Detach(){detached=true;return(input,prepared);}
    public void Dispose(){if(!detached)input.Dispose();}
}
