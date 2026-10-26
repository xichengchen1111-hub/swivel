using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;

namespace Swivel.Core;

/// One transcribed stretch of speech, in seconds from the start of the file.
public sealed record TranscriptSegment(double Start, double End, string Text);
public sealed record Transcript(string Language, IReadOnlyList<TranscriptSegment> Segments)
{
    public IReadOnlyList<SubtitleCue> Cues => Segments.Select(s => new SubtitleCue((long)Math.Round(s.Start * 1000), Math.Max((long)Math.Round(s.Start * 1000) + 1, (long)Math.Round(s.End * 1000)), s.Text)).ToArray();
}

/// Whisper's multilingual byte-level tokenizer: one line per token, the token's bytes in base64 and its rank.
public sealed class WhisperTokens
{
    public const int Count = 50257;
    private readonly byte[][] pieces;
    private WhisperTokens(byte[][] pieces) => this.pieces = pieces;

    public static WhisperTokens Parse(byte[] file)
    {
        var pieces = new byte[Count][];
        string text;
        try { text = new UTF8Encoding(false, true).GetString(file); } catch (DecoderFallbackException) { throw new InvalidDataException("The tokenizer is not text."); }
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != Count) throw new InvalidDataException("The tokenizer has the wrong number of tokens.");
        foreach (var line in lines)
        {
            var parts = line.Split(' ');
            if (parts.Length != 2 || !int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int rank) || rank >= Count || pieces[rank] is not null)
                throw new InvalidDataException("The tokenizer is malformed.");
            // The upstream file writes its last, unused ordinary token as "=" (no bytes).
            if (parts[0] == "=" && rank == Count - 1) { pieces[rank] = Array.Empty<byte>(); continue; }
            try { pieces[rank] = Convert.FromBase64String(parts[0]); } catch (FormatException) { throw new InvalidDataException("The tokenizer is malformed."); }
            if (pieces[rank].Length == 0) throw new InvalidDataException("The tokenizer is malformed.");
        }
        return new WhisperTokens(pieces);
    }

    /// The text of the ordinary tokens; special and timestamp tokens are left out. Incomplete
    /// UTF-8 at the edges becomes U+FFFD, as in Whisper.
    public string Decode(IEnumerable<int> tokens)
    {
        var bytes = new List<byte>();
        foreach (int t in tokens) if (t >= 0 && t < Count) bytes.AddRange(pieces[t]);
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}

/// Whisper speech recognition, following the published transcribe/decoding procedure (openai/whisper,
/// MIT): log-mel spectrogram of the whole file, 30-second windows moved by the predicted timestamps,
/// greedy decoding with temperature fallback, the previous text as a prompt. The networks are supplied
/// by the caller: <c>encode</c> takes one 80 × 3000 window and returns a function from a token prefix
/// to the next token's logits.
public static class Transcribe
{
    public const int SampleRate = 16000, MaxSeconds = 3600, Mels = 80, WindowFrames = 3000;
    private const int Hop = 160, Fft = 400, Bins = Fft / 2 + 1, Vocabulary = 51865, TextContext = 448, SampleLength = TextContext / 2;
    public const int Eot = 50257, Sot = 50258, FirstLanguage = 50259, Translate = 50358, TranscribeTask = 50359,
        StartOfLm = 50360, StartOfPrevious = 50361, NoSpeech = 50362, NoTimestamps = 50363, TimestampBegin = 50364;
    public const string TooLong = "Choose audio up to one hour long. Longer recordings can be split first.";
    // The pinned Whisper base model, converted at build time (ThirdParty/Whisper).
    public const string EncoderFile = "whisper-base-encoder.onnx", DecoderFile = "whisper-base-decoder.onnx", TokensFile = "whisper-multilingual.tiktoken";
    public const long EncoderBytes = 47_506_111, DecoderBytes = 98_544_010, TokensBytes = 816_730;
    public const string EncoderSha256 = "4AF8CAAA455D00DCA07D6EBC6333479295AD3A7D738639120D0458F44BF9891F",
        DecoderSha256 = "7D6F84754DF3A51D65A5469608CD75C2ADDB687DB51279ACBD9235E3DA41DAF1",
        TokensSha256 = "B34B360DBB493E781E479794586D661700670D65564001F23024971D1F2FA126";
    public const int CrossLength = 8 * 64 * 1500, Layers = 6;

    /// Whisper base's 99 languages, in token order (language token = 50259 + index).
    public static readonly string[] Languages = ("en zh de es ru ko fr ja pt tr pl ca nl ar sv it id hi fi vi he uk el ms cs ro da hu ta no th ur hr bg lt la mi ml cy sk te fa lv bn sr az sl kn et mk br eu is hy ne mn bs kk sq sw gl mr pa si km sn yo so af oc ka be tg sd gu am yi lo uz fo ht ps tk nn mt sa lb my bo tl mg as tt haw ln ha ba jw su").Split(' ');

    // Whisper's non_speech_tokens for this tokenizer (speaker tags, music notes, brackets…), plus the
    // task and control tokens it always suppresses.
    private static readonly int[] Suppressed = {
        1, 2, 7, 8, 9, 10, 14, 25, 26, 27, 28, 29, 31, 58, 59, 60, 61, 62, 63, 90, 91, 92, 93, 359, 503, 522, 542, 873, 893, 902, 918, 922, 931, 1350, 1853, 1982, 2460, 2627, 3246, 3253, 3268, 3536, 3846, 3961, 4183, 4667, 6585, 6647, 7273, 9061, 9383, 10428, 10929, 11938, 12033, 12331, 12562, 13793, 14157, 14635, 15265, 15618, 16553, 16604, 18362, 18956, 20075, 21675, 22520, 26130, 26161, 26435, 28279, 29464, 31650, 32302, 32470, 36865, 42863, 47425, 49870, 50254,
        TranscribeTask, Translate, Sot, StartOfPrevious, StartOfLm, NoSpeech };
    private const int Blank = 220; // " "
    private static readonly double[] Temperatures = { 0, 0.2, 0.4, 0.6, 0.8, 1.0 };

    // --- Log-mel spectrogram ---------------------------------------------------------------

    /// librosa's Slaney-style mel filter bank (16 kHz, 400-point FFT, 80 bands), as Whisper ships it.
    public static float[] MelFilters()
    {
        static double ToMel(double hz) => hz >= 1000 ? 15 + Math.Log(hz / 1000) / (Math.Log(6.4) / 27) : hz / (200.0 / 3);
        static double ToHz(double mel) => mel >= 15 ? 1000 * Math.Exp(Math.Log(6.4) / 27 * (mel - 15)) : mel * (200.0 / 3);
        var points = new double[Mels + 2];
        double top = ToMel(SampleRate / 2.0);
        for (int i = 0; i < points.Length; i++) points[i] = ToHz(top * i / (Mels + 1));
        var filters = new float[Mels * Bins];
        for (int m = 0; m < Mels; m++)
        {
            double norm = 2 / (points[m + 2] - points[m]);
            for (int k = 0; k < Bins; k++)
            {
                double f = k * (SampleRate / 2.0) / (Bins - 1);
                double lower = (f - points[m]) / (points[m + 1] - points[m]), upper = (points[m + 2] - f) / (points[m + 2] - points[m + 1]);
                filters[m * Bins + k] = (float)(Math.Max(0, Math.Min(lower, upper)) * norm);
            }
        }
        return filters;
    }

    /// The normalized log-mel spectrogram of 16 kHz mono 16-bit samples followed by 30 seconds of
    /// silence, mel-major (80 rows × frames): Whisper's log_mel_spectrogram(audio, padding=480000).
    public static float[] LogMel(ReadOnlySpan<short> samples, out int frames, CancellationToken token)
    {
        if (samples.Length == 0) throw new UserError("The audio is empty.");
        if (samples.Length > (long)MaxSeconds * SampleRate) throw new UserError(TooLong);
        int length = samples.Length + WindowFrames * Hop;
        frames = length / Hop; // torch.stft's last frame is dropped
        // The padded signal: 200 reflected samples, the audio, 30 s of zeros, 200 reflected samples.
        float At(short[] audio, int i)
        {
            i -= Fft / 2;
            if (i < 0) i = -i; else if (i >= length) i = 2 * (length - 1) - i;
            return i < audio.Length ? audio[i] / 32768f : 0;
        }
        // Windowed cosine and sine rows of the DFT, so each bin is two dot products.
        var cos = new float[Bins * Fft]; var sin = new float[Bins * Fft];
        for (int k = 0; k < Bins; k++)
            for (int n = 0; n < Fft; n++)
            {
                double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * n / Fft), a = 2 * Math.PI * ((long)k * n % Fft) / Fft;
                cos[k * Fft + n] = (float)(w * Math.Cos(a)); sin[k * Fft + n] = (float)(w * Math.Sin(a));
            }
        var filters = MelFilters();
        int count = frames;
        var mel = new float[(long)Mels * count];
        var audio = samples.ToArray(); // a span cannot cross into the parallel workers
        int width = Vector<float>.Count;
        // Frames are independent; blocks of 256 run in parallel, each with its own buffers.
        System.Threading.Tasks.Parallel.For(0, (count + 255) / 256, new System.Threading.Tasks.ParallelOptions { CancellationToken = token }, block =>
        {
            var frame = new float[Fft]; var power = new float[Bins];
            for (int t = block * 256; t < Math.Min(count, block * 256 + 256); t++)
            {
                for (int n = 0; n < Fft; n++) frame[n] = At(audio, t * Hop + n);
                for (int k = 0; k < Bins; k++)
                {
                    var c = cos.AsSpan(k * Fft, Fft); var s = sin.AsSpan(k * Fft, Fft);
                    Vector<float> re = Vector<float>.Zero, im = Vector<float>.Zero;
                    int n = 0;
                    for (; n + width <= Fft; n += width) { var x = new Vector<float>(frame.AsSpan(n)); re += x * new Vector<float>(c[n..]); im += x * new Vector<float>(s[n..]); }
                    float r = Vector.Dot(re, Vector<float>.One), i = Vector.Dot(im, Vector<float>.One);
                    for (; n < Fft; n++) { r += frame[n] * c[n]; i += frame[n] * s[n]; }
                    power[k] = r * r + i * i;
                }
                for (int m = 0; m < Mels; m++)
                {
                    float sum = 0; var f = filters.AsSpan(m * Bins, Bins);
                    for (int k = 0; k < Bins; k++) sum += f[k] * power[k];
                    mel[(long)m * count + t] = MathF.Log10(Math.Max(sum, 1e-10f));
                }
            }
        });
        float max = float.NegativeInfinity;
        foreach (float v in mel) if (v > max) max = v;
        for (long i = 0; i < mel.LongLength; i++) mel[i] = (Math.Max(mel[i], max - 8) + 4) / 4;
        return mel;
    }

    /// Frames <c>first</c>…<c>first + count</c> of the spectrogram as one 80 × 3000 window, zero padded.
    public static float[] Window(float[] mel, int frames, int first, int count)
    {
        var window = new float[Mels * WindowFrames];
        count = Math.Min(Math.Min(count, WindowFrames), frames - first);
        for (int m = 0; m < Mels; m++) Array.Copy(mel, (long)m * frames + first, window, (long)m * WindowFrames, count);
        return window;
    }

    // --- Decoding ----------------------------------------------------------------------------

    private sealed record Result(List<int> Tokens, double AverageLogprob, double NoSpeech, double Compression, double Temperature);

    /// The spoken language from the first window: the most likely language token after [SOT].
    public static string Detect(Func<int[], float[]> logits)
    {
        var l = logits(new[] { Sot });
        int best = 0;
        for (int i = 1; i < Languages.Length; i++) if (l[FirstLanguage + i] > l[FirstLanguage + best]) best = i;
        return Languages[best];
    }

    public static Transcript Run(float[] mel, int frames, string? language, Func<float[], Func<int[], float[]>> encode, WhisperTokens tokenizer,
        Action<int, int>? progress, CancellationToken token)
    {
        if (language is not null && Array.IndexOf(Languages, language) < 0) throw new ArgumentException("Unknown language.", nameof(language));
        int content = frames - WindowFrames, seek = 0;
        var segments = new List<TranscriptSegment>();
        var all = new List<int>(); int promptFrom = 0;
        var random = new Random(0); // temperature fallback samples deterministically
        // Whisper detects on the first 30 s of the padded spectrogram; for a short clip that
        // includes the silence padding, unlike the zero-padded decoding window.
        if (language is null && content < WindowFrames) { token.ThrowIfCancellationRequested(); language = Detect(Checked(encode(Window(mel, frames, 0, WindowFrames)))); }
        while (seek < content)
        {
            token.ThrowIfCancellationRequested();
            progress?.Invoke(seek, content);
            int size = Math.Min(WindowFrames, content - seek);
            double offset = seek * Hop / (double)SampleRate;
            var logits = Checked(encode(Window(mel, frames, seek, size)));
            language ??= Detect(logits);
            var prompt = all.Skip(promptFrom).ToList();
            var prefix = new List<int>();
            if (prompt.Count > 0) { prefix.Add(StartOfPrevious); prefix.AddRange(prompt.Skip(Math.Max(0, prompt.Count - (TextContext / 2 - 1)))); }
            prefix.Add(Sot);
            var atSot = Softmax(logits(prefix.ToArray()));
            double noSpeech = atSot[NoSpeech];
            prefix.Add(FirstLanguage + Array.IndexOf(Languages, language)); prefix.Add(TranscribeTask);

            Result result = null!;
            foreach (double temperature in Temperatures)
            {
                result = Decode(logits, prefix, temperature, noSpeech, tokenizer, random, token);
                bool fallback = result.Compression > 2.4 || result.AverageLogprob < -1;
                if (noSpeech > 0.6 && result.AverageLogprob < -1) fallback = false; // silence
                if (!fallback) break;
            }
            if (noSpeech > 0.6 && !(result.AverageLogprob > -1)) { seek += size; continue; }

            var sampled = result.Tokens;
            bool Stamp(int i) => sampled[i] >= TimestampBegin;
            bool singleEnding = sampled.Count >= 2 && !Stamp(sampled.Count - 2) && Stamp(sampled.Count - 1);
            var consecutive = Enumerable.Range(1, Math.Max(0, sampled.Count - 1)).Where(i => Stamp(i - 1) && Stamp(i)).ToList();
            var current = new List<(TranscriptSegment Segment, List<int> Tokens)>();
            if (consecutive.Count > 0)
            {
                if (singleEnding) consecutive.Add(sampled.Count);
                int last = 0;
                foreach (int slice in consecutive)
                {
                    var part = sampled.GetRange(last, slice - last);
                    current.Add((new(offset + (part[0] - TimestampBegin) * 0.02, offset + (part[^1] - TimestampBegin) * 0.02, tokenizer.Decode(part.Where(t => t < Eot))), part));
                    last = slice;
                }
                seek += singleEnding ? size : (sampled[last - 1] - TimestampBegin) * 2;
            }
            else
            {
                double duration = size * Hop / (double)SampleRate;
                var stamps = sampled.Where(t => t >= TimestampBegin).ToList();
                if (stamps.Count > 0 && stamps[^1] != TimestampBegin) duration = (stamps[^1] - TimestampBegin) * 0.02;
                current.Add((new(offset, offset + duration, tokenizer.Decode(sampled.Where(t => t < Eot))), sampled));
                seek += size;
            }
            foreach (var (segment, part) in current)
            {
                // Instantaneous or empty segments are dropped, and their tokens are not used as a prompt.
                if (segment.Start == segment.End || string.IsNullOrWhiteSpace(Clean(segment.Text))) continue;
                segments.Add(segment with { Text = Clean(segment.Text) });
                all.AddRange(part);
            }
            if (result.Temperature > 0.5) promptFrom = all.Count;
        }
        progress?.Invoke(content, content);
        return new Transcript(language ?? "en", segments);
    }

    private static Func<int[], float[]> Checked(Func<int[], float[]> model) =>
        prefix => model(prefix) is { Length: Vocabulary } l ? l : throw new InvalidDataException("The speech model returned the wrong number of scores.");

    // SubRip cannot carry "-->" or line breaks inside a cue's text.
    private static string Clean(string text) => string.Join(" ", text.Replace("-->", "->").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();

    private static Result Decode(Func<int[], float[]> logits, List<int> prefix, double temperature, double noSpeech, WhisperTokens tokenizer, Random random, CancellationToken token)
    {
        var tokens = new List<int>(prefix); int begin = prefix.Count;
        double sum = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var l = logits(tokens.ToArray());
            Filter(l, tokens, begin);
            int next;
            if (temperature == 0) next = ArgMax(l);
            else
            {
                var p = Softmax(l, temperature); double u = random.NextDouble(), c = 0; next = p.Length - 1;
                for (int i = 0; i < p.Length; i++) { c += p[i]; if (u < c && p[i] > 0) { next = i; break; } }
            }
            sum += LogSoftmax(l, next);
            tokens.Add(next);
            if (next == Eot || tokens.Count - begin >= SampleLength || tokens.Count > TextContext) break;
        }
        var sampled = tokens.Skip(begin).TakeWhile(t => t != Eot).ToList();
        string text = tokenizer.Decode(sampled.Where(t => t < Eot));
        return new Result(sampled, sum / (sampled.Count + 1), noSpeech, Compression(text), temperature);
    }

    /// Whisper's logit filters: no blank start, no non-speech tokens, timestamp rules.
    private static void Filter(float[] l, List<int> tokens, int begin)
    {
        int count = tokens.Count - begin;
        if (count == 0) { l[Blank] = float.NegativeInfinity; l[Eot] = float.NegativeInfinity; }
        foreach (int t in Suppressed) l[t] = float.NegativeInfinity;
        l[NoTimestamps] = float.NegativeInfinity;
        bool last = count >= 1 && tokens[^1] >= TimestampBegin, penultimate = count < 2 || tokens[^2] >= TimestampBegin;
        if (last)
        {
            if (penultimate) Array.Fill(l, float.NegativeInfinity, TimestampBegin, Vocabulary - TimestampBegin);
            else Array.Fill(l, float.NegativeInfinity, 0, Eot);
        }
        int lastStamp = -1;
        for (int i = tokens.Count - 1; i >= begin; i--) if (tokens[i] >= TimestampBegin) { lastStamp = tokens[i]; break; }
        if (lastStamp >= 0)
        {
            int limit = last && !penultimate ? lastStamp : lastStamp + 1; // timestamps never decrease; segments have length
            Array.Fill(l, float.NegativeInfinity, TimestampBegin, Math.Min(limit, Vocabulary) - TimestampBegin);
        }
        if (count == 0)
        {
            Array.Fill(l, float.NegativeInfinity, 0, TimestampBegin);
            Array.Fill(l, float.NegativeInfinity, TimestampBegin + 51, Vocabulary - TimestampBegin - 51); // first timestamp at most 1 s
        }
        // If the timestamps together are likelier than any single text token, a timestamp comes next.
        double max = l.Max();
        if (double.IsNegativeInfinity(max)) return;
        double stamps = 0; for (int i = TimestampBegin; i < Vocabulary; i++) stamps += Math.Exp(l[i] - max);
        double bestText = double.NegativeInfinity; for (int i = 0; i < TimestampBegin; i++) bestText = Math.Max(bestText, l[i]);
        if (stamps > 0 && Math.Log(stamps) > bestText - max) Array.Fill(l, float.NegativeInfinity, 0, TimestampBegin);
    }

    private static int ArgMax(float[] l) { int best = 0; for (int i = 1; i < l.Length; i++) if (l[i] > l[best]) best = i; return best; }

    private static double[] Softmax(float[] l, double temperature = 1)
    {
        double max = l.Max(), total = 0; var p = new double[l.Length];
        for (int i = 0; i < l.Length; i++) total += p[i] = Math.Exp((l[i] - max) / temperature);
        for (int i = 0; i < l.Length; i++) p[i] /= total;
        return p;
    }

    private static double LogSoftmax(float[] l, int index)
    {
        double max = l.Max(), total = 0;
        foreach (float v in l) total += Math.Exp(v - max);
        return l[index] - max - Math.Log(total);
    }

    /// Text length over its zlib-compressed length; repetitive output compresses well.
    private static double Compression(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length == 0) return 0;
        using var memory = new MemoryStream();
        using (var zlib = new ZLibStream(memory, CompressionLevel.Optimal, true)) zlib.Write(bytes);
        return bytes.Length / (double)memory.Length;
    }
}
