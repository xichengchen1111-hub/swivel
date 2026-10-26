using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Swivel.Core;
using Windows.AI.MachineLearning;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;

namespace Swivel.Windows;

/// The pinned Whisper base model (two graphs) and its tokenizer, converted from the official
/// checkpoint at build time and run on this PC with Windows ML.
internal static class SpeechModel
{
    private const string Missing = "The speech recognition model is not included in this copy of Swivel. Use a complete package.",
        Damaged = "The speech recognition model in this copy of Swivel is damaged. Use a complete package.",
        Unavailable = "Windows ML could not run the speech recognition model on this PC.";
    internal static readonly PinnedModel Encoder = new(Transcribe.EncoderFile, Transcribe.EncoderBytes, Transcribe.EncoderSha256, Missing, Damaged, Unavailable);
    internal static readonly PinnedModel Decoder = new(Transcribe.DecoderFile, Transcribe.DecoderBytes, Transcribe.DecoderSha256, Missing, Damaged, Unavailable);
    private static readonly object gate = new();
    private static WhisperTokens? tokens;
    private static readonly string[] Keys = Enumerable.Range(0, Transcribe.Layers).Select(i => $"cross_k_{i}").ToArray(),
        Values = Enumerable.Range(0, Transcribe.Layers).Select(i => $"cross_v_{i}").ToArray();

    /// Tests point this at their own copy; the app uses the Models folder beside Swivel.exe.
    internal static string TokensPath = Path.Combine(AppContext.BaseDirectory, "Models", Transcribe.TokensFile);

    /// Loads everything and reports whether Windows ML found a graphics card for the encoder.
    public static bool UsesGraphics(CancellationToken token)
    {
        Tokens(token);
        bool encoder = Encoder.UsesGraphics(token), decoder = Decoder.UsesGraphics(token);
        return encoder && decoder;
    }

    internal static WhisperTokens Tokens(CancellationToken token)
    {
        lock (gate)
        {
            if (tokens is not null) return tokens;
            byte[] data;
            try { data = File.ReadAllBytes(TokensPath); }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { throw new UserError(Missing); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new UserError(Damaged); }
            token.ThrowIfCancellationRequested();
            if (data.Length != Transcribe.TokensBytes || !Convert.ToHexString(SHA256.HashData(data)).Equals(Transcribe.TokensSha256, StringComparison.OrdinalIgnoreCase)) throw new UserError(Damaged);
            try { return tokens = WhisperTokens.Parse(data); } catch (InvalidDataException) { throw new UserError(Damaged); }
        }
    }

    /// Transcribes one prepared spectrogram; each 30-second window's cross-attention tensors are
    /// made once and shared by every decoder step of that window.
    public static Transcript Run(float[] mel, int frames, string? language, Action<int, int>? progress, CancellationToken token)
    {
        var words = Tokens(token);
        TensorFloat[]? cross = null;
        void Release() { if (cross is not null) foreach (var t in cross) t.Dispose(); cross = null; }
        try
        {
            return Transcribe.Run(mel, frames, language, window =>
            {
                Release();
                using (var input = TensorFloat.CreateFromArray(new long[] { 1, Transcribe.Mels, Transcribe.WindowFrames }, window))
                {
                    var outputs = Encoder.Evaluate(binding => binding.Bind("mel", input),
                        output => Keys.Concat(Values).Select(name => output(name, Transcribe.CrossLength)).ToArray(), token);
                    cross = new TensorFloat[outputs.Length];
                    for (int i = 0; i < outputs.Length; i++)
                        cross[i] = TensorFloat.CreateFromArray(i < Transcribe.Layers ? new long[] { 1, 8, 64, 1500 } : new long[] { 1, 8, 1500, 64 }, outputs[i]);
                }
                var shared = cross;
                return prefix =>
                {
                    using var ids = TensorInt64Bit.CreateFromArray(new long[] { 1, prefix.Length }, prefix.Select(t => (long)t).ToArray());
                    return Decoder.Evaluate(binding =>
                    {
                        binding.Bind("tokens", ids);
                        for (int i = 0; i < Transcribe.Layers; i++) { binding.Bind(Keys[i], shared[i]); binding.Bind(Values[i], shared[Transcribe.Layers + i]); }
                    }, output => output("logits", 51865), token);
                };
            }, words, progress, token);
        }
        catch (InvalidDataException) { throw new UserError(Unavailable); }
        finally { Release(); }
    }
}

/// One recording held open for Transcribe: its audio as 16 kHz mono 16-bit samples, made by
/// Swivel's own converter for PCM WAV or by Windows' decoders for the other accepted formats.
public sealed class TranscribeDocument : IDisposable
{
    public const long MaxBytes = 128L * 1024 * 1024;
    public const string Unsupported = "Choose an integer PCM WAV up to 512 MB, or a WAV, FLAC, MP3 or ADTS AAC file up to 128 MB that Windows can decode, up to one hour long.";
    private readonly NativeFiles.Input input;
    private readonly byte[] wave; private readonly WaveInfo info;
    public string Path => input.Path;
    public double Seconds => info.Seconds;

    private TranscribeDocument(NativeFiles.Input input, byte[] wave, WaveInfo info) { this.input = input; this.wave = wave; this.info = info; }

    public static async Task<TranscribeDocument> OpenAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var input = NativeFiles.OpenRead(path);
        try
        {
            if (input.Stream.Length > PcmWave.MaxBytes) throw new UserError(Unsupported);
            byte[] wave;
            WaveInfo? source = null;
            try { source = PcmWave.Read(input.Stream, token); } catch (UserError) { }
            if (source is not null)
            {
                if (source.Seconds > Transcribe.MaxSeconds) throw new UserError(Transcribe.TooLong);
                input.Stream.Position = 0;
                using var converted = new MemoryStream();
                WaveConverter.Export(input.Stream, converted, new WaveFormat(Transcribe.SampleRate, 1, 16), token);
                wave = converted.ToArray();
            }
            else wave = await DecodeAsync(input.Stream, token).ConfigureAwait(false);
            using var check = new MemoryStream(wave, false);
            var info = PcmWave.Read(check, token);
            if (info.Rate != Transcribe.SampleRate || info.Channels != 1 || info.Bits != 16) throw new UserError(Unsupported);
            if (info.Seconds > Transcribe.MaxSeconds) throw new UserError(Transcribe.TooLong);
            if (info.Frames == 0) throw new UserError("The audio is empty.");
            return new TranscribeDocument(input, wave, info);
        }
        catch { input.Dispose(); throw; }
    }

    // Windows' own decoders, asked for 16 kHz mono PCM16 directly (they resample and mix down).
    private static async Task<byte[]> DecodeAsync(Stream stream, CancellationToken token)
    {
        if (stream.Length > MaxBytes) throw new UserError(Unsupported);
        stream.Position = 0;
        SystemAudioInput.Identify(stream, token);
        using var sourceView = new BoundedStream(stream, MaxBytes, false, token); using var source = sourceView.AsRandomAccessStream();
        if (source.CanWrite || !source.CanRead) throw new UserError(Unsupported);
        var target = MediaEncodingProfile.CreateWav(AudioEncodingQuality.Auto);
        target.Audio = AudioEncodingProperties.CreatePcm(Transcribe.SampleRate, 1, 16);
        using var decoded = new MemoryStream(); using var targetView = new BoundedStream(decoded, MaxBytes, true, token); using var output = targetView.AsRandomAccessStream();
        var transcoder = new MediaTranscoder { AlwaysReencode = true, HardwareAccelerationEnabled = false };
        var preparation = await NativeAsync.Join(transcoder.PrepareStreamTranscodeAsync(source, output, target), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!preparation.CanTranscode) throw new UserError(Unsupported);
        await NativeAsync.Join(preparation.TranscodeAsync(), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        // Only the data chunk is kept, in a canonical WAV the checks above read again.
        var info = PcmWave.Read(decoded, token);
        using var canonical = new MemoryStream();
        PcmWave.Export(decoded, canonical, new(0, info.Seconds), token);
        return canonical.ToArray();
    }

    public Transcript Run(string? language, Action<int, int, TimeSpan?>? progress, CancellationToken token)
    {
        var samples = MemoryMarshal.Cast<byte, short>(wave.AsSpan((int)info.DataOffset, (int)info.DataBytes));
        var mel = Transcribe.LogMel(samples, out int frames, token);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        return SpeechModel.Run(mel, frames, language, (done, total) => progress?.Invoke(done, total, done > 0 && done < total ? clock.Elapsed / done * (total - done) : null), token);
    }

    /// The text (one line per segment) and/or SubRip subtitles, beside the original.
    public string[] Save(Transcript transcript, bool text, bool subtitles, CancellationToken token)
    {
        if (transcript.Segments.Count == 0) throw new UserError("No speech was recognised in this recording. Nothing was saved.");
        var cues = transcript.Cues; string stem = System.IO.Path.GetFileNameWithoutExtension(input.Path);
        var saved = new System.Collections.Generic.List<string>();
        void Write(string extension, string content)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            saved.Add(NativeFiles.WriteBeside(input, stem, extension, stream => stream.Write(bytes), token));
        }
        if (text) Write("txt", SubRip.Transcript(cues));
        if (subtitles) Write("srt", SubRip.Write(cues));
        return saved.ToArray();
    }

    public void Dispose() => input.Dispose();
}
