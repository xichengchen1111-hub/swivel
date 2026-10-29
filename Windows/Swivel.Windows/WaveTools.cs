using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Swivel.Core;

namespace Swivel.Windows;

public sealed class WaveDocument : IDisposable
{
    private readonly NativeFiles.Input input;
    private PreparedWaveMusic? preparedMusic;
    public WaveInfo MusicInfo => preparedMusic?.Info ?? Info;
    public long? PreparedMusicClippedSamples => preparedMusic?.ClippedSamples;
    public WaveInfo Info { get; }
    public string Path => input.Path;
    public double[] Peaks { get; }
    // The PCM read and processed: the held file itself, or audio Windows decoded from it (the
    // file stays held either way, and outputs go beside it).
    private readonly Stream source;
    public bool Decoded { get; }
    public const string DecodedNotice = "Windows decoded this file to 16-bit PCM first (its codec can change precision, priming or padding; up to 128 MiB and two hours). The cleaned result is saved as a new WAV file beside it.";
    private WaveDocument(NativeFiles.Input input,WaveInfo info,double[] peaks,Stream? decoded = null) { this.input = input; source = decoded ?? input.Stream; Decoded = decoded is not null; Info = info; Peaks = peaks; }
    /// A compressed file (MP3, AAC, FLAC) decoded completely by Windows into PCM16 first, within
    /// the system decoder's own limits; the original stays held until the document is disposed.
    public static async Task<WaveDocument> OpenDecodedAsync(string path,CancellationToken token)
    {
        var decoded = await SystemAudioDocument.OpenAsync(path, token).ConfigureAwait(false);
        var (input, wav) = decoded.Detach();
        var stream = new MemoryStream(wav, false);
        try
        {
            var info = PcmWave.Read(stream, token);
            return new(input, info, PcmWave.Peaks(stream, 600, token), stream);
        }
        catch { stream.Dispose(); input.Dispose(); throw; }
    }
    public static WaveDocument Open(string path,CancellationToken token, bool scanPeaks = true)
    {
        token.ThrowIfCancellationRequested(); var input = NativeFiles.OpenRead(path);
        try { var info = PcmWave.Read(input.Stream,token); return new(input,info,scanPeaks ? PcmWave.Peaks(input.Stream,600,token) : Array.Empty<double>()); }
        catch { input.Dispose(); throw; }
    }
    // Caller serializes preview/export and awaits outstanding preview before Dispose.
    public byte[] Preview(AudioEdit edit,CancellationToken token)
    {
        using var output = new MemoryStream(); PcmWave.Export(source,output,edit,token,preview:true); return output.ToArray();
    }
    public string Save(AudioEdit edit,CancellationToken token)
    {
        edit.Range(Info); token.ThrowIfCancellationRequested();
        return NativeFiles.WriteBeside(input,System.IO.Path.GetFileNameWithoutExtension(input.Path)+" edited","wav",output => PcmWave.Export(source,output,edit,token),token);
    }
    public byte[] MixPreview(WaveDocument music, AudioEdit edit, WaveMusic settings, CancellationToken token)
    {
        using var output = new MemoryStream(); MixInto(music, output, edit, settings, token, preview: true); return output.ToArray();
    }
    public string SaveMix(WaveDocument music, AudioEdit edit, WaveMusic settings, CancellationToken token)
    {
        settings.Range(Info, music.MusicInfo, edit); token.ThrowIfCancellationRequested();
        return NativeFiles.WriteBeside(input, System.IO.Path.GetFileNameWithoutExtension(input.Path) + " with music", "wav",
            output => MixInto(music, output, edit, settings, token), token);
    }
    private long MixInto(WaveDocument music, Stream output, AudioEdit edit, WaveMusic settings, CancellationToken token, bool preview = false)
        => music.preparedMusic is { } prepared ? prepared.ExportMix(source, output, edit, settings, token, preview) : WaveMixer.Export(source, music.source, output, edit, settings, token, preview);
    // Called once, after explicit confirmation and before opening the mixing editor.
    public void PrepareMusicRate(int targetRate, CancellationToken token)
    {
        if (preparedMusic is not null) throw new InvalidOperationException("Music is already prepared.");
        var prepared = PreparedWaveMusic.Prepare(source, targetRate, token, Info);
        try { token.ThrowIfCancellationRequested(); preparedMusic = prepared; }
        catch { prepared.Dispose(); throw; }
    }
    public byte[] FormatPreview(WaveFormat format, CancellationToken token)
    {
        using var output = new MemoryStream(); WaveConverter.Export(source, output, format, token, preview: true); return output.ToArray();
    }
    public byte[] SpeedPreview(WaveSpeed speed, CancellationToken token)
    {
        using var output = new MemoryStream(); WaveConverter.ExportSpeed(source, output, speed, token, preview: true); return output.ToArray();
    }
    public string SaveSpeed(WaveSpeed speed, CancellationToken token)
    {
        speed.Frames(Info); token.ThrowIfCancellationRequested();
        return NativeFiles.WriteBeside(input, System.IO.Path.GetFileNameWithoutExtension(input.Path) + " speed", "wav",
            output => WaveConverter.ExportSpeed(source, output, speed, token), token);
    }
    public string SaveFormat(WaveFormat format, CancellationToken token)
    {
        format.Frames(Info); token.ThrowIfCancellationRequested();
        return NativeFiles.WriteBeside(input, System.IO.Path.GetFileNameWithoutExtension(input.Path) + " converted", "wav",
            output => WaveConverter.Export(source, output, format, token), token);
    }
    public byte[] NoisePreview(WaveNoise settings, CancellationToken token)
    {
        using var output = new MemoryStream(); WaveDenoiser.Export(source, output, settings, token, preview: true); return output.ToArray();
    }
    public QuietSampleCandidate FindQuietSample(long frames,CancellationToken token)=>WaveQuietSample.Find(source,frames,token);
    public string SaveNoise(WaveNoise settings, CancellationToken token)
    {
        settings.Range(Info); new WaveFormat(Info.Rate, Info.Channels, Info.Bits).Frames(Info); token.ThrowIfCancellationRequested();
        return NativeFiles.WriteBeside(input, System.IO.Path.GetFileNameWithoutExtension(input.Path) + " clean", "wav",
            output => WaveDenoiser.Export(source, output, settings, token), token);
    }
    public void Dispose() { try { preparedMusic?.Dispose(); } finally { input.Dispose(); } }
}
