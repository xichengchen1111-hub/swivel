using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Swivel.Core;

internal static class TranscribeChecks
{
    const int Ts = Transcribe.TimestampBegin;

    // Tokens 0–255 are single bytes, so lowercase letters decode to themselves.
    static byte[] TokenFile(Func<int, string?>? line = null, int count = WhisperTokens.Count)
    {
        var text = new StringBuilder();
        for (int r = 0; r < count; r++)
            text.Append(line?.Invoke(r) ?? (r == WhisperTokens.Count - 1 ? "=" : Convert.ToBase64String(r < 256 ? new[] { (byte)r } : Encoding.ASCII.GetBytes("#" + r)))).Append(' ').Append(r).Append('\n');
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    static int[] Text(string s) => s.Select(c => (int)c).ToArray();

    // A stand-in model: after [SOT] it names Chinese (or says "no speech"); afterwards it plays back the
    // planned tokens of the current window, one per call, with a clear margin.
    sealed class Fake
    {
        public readonly List<int[]> Plans = new();
        public readonly HashSet<int> Silent = new();
        public readonly List<int[]> Prefixes = new();
        public int Windows, Starts, Longest;
        public float Margin = 20;

        public Func<int[], float[]> Encode(float[] window)
        {
            if (window.Length != Transcribe.Mels * Transcribe.WindowFrames) throw new InvalidOperationException("window size");
            int index = Windows++;
            var plan = index < Plans.Count ? Plans[index] : new[] { Ts, Transcribe.Eot };
            return tokens =>
            {
                Longest = Math.Max(Longest, tokens.Length);
                var l = new float[51865];
                if (tokens[^1] == Transcribe.Sot)
                {
                    Prefixes.Add(tokens);
                    l[Transcribe.FirstLanguage + 1] = 20; // zh
                    if (Silent.Contains(index)) l[Transcribe.NoSpeech] = 40;
                    return l;
                }
                int begin = Array.LastIndexOf(tokens, Transcribe.TranscribeTask) + 1, n = tokens.Length - begin;
                if (n == 0) Starts++;
                l[plan[n % plan.Length]] = Silent.Contains(index) ? 8 : Margin;
                return l;
            };
        }
    }

    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        // Mel filters and spectrogram against Whisper's own (mel_filters.npz, log_mel_spectrogram).
        var f = Transcribe.MelFilters();
        check(f.Length == 80 * 201 && Math.Abs(f.Sum() - 1.9990241527557373) < 1e-5 && Math.Abs(f[1] - 0.024862595) < 1e-7 && f[10 * 201 + 12] == 0
            && Math.Abs(f[79 * 201 + 190] - 0.002232055) < 1e-7 && Math.Abs(f.Max() - 0.025880683213472366) < 1e-7, "Transcribe mel filters match Whisper's");
        var tone = new short[16000];
        for (int i = 0; i < tone.Length; i++)
            tone[i] = (short)Math.Round((Math.Sin(2 * Math.PI * 440 * i / 16000.0) * 0.3 + Math.Sin(2 * Math.PI * 1234 * i / 16000.0) * 0.1) * 32768);
        var mel = Transcribe.LogMel(tone, out int frames, default);
        float M(int m, int t) => mel[m * frames + t];
        check(frames == 3100 && Math.Abs(M(0, 0) - 0.8960037f) < 2e-4 && Math.Abs(M(10, 50) - 1.2378135f) < 2e-4 && Math.Abs(M(40, 50) + 0.6727185f) < 2e-4
            && Math.Abs(M(20, 100) - 0.4412741f) < 2e-4 && Math.Abs(M(5, 2000) + 0.6727185f) < 2e-4 && Math.Abs(mel.Max() - 1.3272815f) < 2e-4 && Math.Abs(mel.Min() + 0.6727185f) < 2e-4,
            "Transcribe log-mel spectrogram matches Whisper's");
        var window = Transcribe.Window(mel, frames, 200, 3000);
        check(window.Length == 240000 && window[0] == M(0, 200) && window[79 * 3000 + 2899] == M(79, 3099) && window[79 * 3000 + 2900] == 0 && window[2999] == 0, "Transcribe windows are zero padded past the end");
        reject(() => Transcribe.LogMel(Array.Empty<short>(), out _, default), "Transcribe refuses empty audio");
        reject(() => Transcribe.LogMel(new short[Transcribe.MaxSeconds * Transcribe.SampleRate + 1], out _, default), "Transcribe refuses more than an hour");
        var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        reject(() => Transcribe.LogMel(tone, out _, cancelled.Token), "Transcribe spectrogram stops when cancelled");

        // Tokenizer.
        var tokens = WhisperTokens.Parse(TokenFile());
        check(tokens.Decode(Text("hello")) == "hello" && tokens.Decode(new[] { 0xE4, 0xBD, 0xA0, Transcribe.Eot, Ts }) == "你" && tokens.Decode(new[] { 0xE4, 0xBD }) == "�"
            && tokens.Decode(new[] { 300 }) == "#300" && tokens.Decode(new[] { WhisperTokens.Count - 1 }) == "", "Transcribe tokenizer decodes bytes and skips special tokens");
        reject(() => WhisperTokens.Parse(TokenFile(count: 100)), "Transcribe tokenizer refuses a wrong token count");
        reject(() => WhisperTokens.Parse(TokenFile(r => r == 5 ? "!!" : null)), "Transcribe tokenizer refuses bad base64");
        reject(() => WhisperTokens.Parse(TokenFile(r => r == 5 ? "=" : null)), "Transcribe tokenizer refuses an empty token other than the last");
        reject(() => WhisperTokens.Parse(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(TokenFile()).Replace(" 7\n", " 6\n"))), "Transcribe tokenizer refuses a repeated rank");
        reject(() => WhisperTokens.Parse(new byte[] { 0xFF, 0xFE }), "Transcribe tokenizer refuses non-UTF-8");

        // Segments from paired timestamps, the language from the first window, the prompt carried over.
        var silence = new short[16000 * 40];
        mel = Transcribe.LogMel(silence, out frames, default);
        var fake = new Fake();
        fake.Plans.Add(new[] { Ts }.Concat(Text(" ab")).Append(Ts + 50).Append(Ts + 50).Concat(Text(" cd")).Append(Ts + 100).Append(Ts + 100).Append(Transcribe.Eot).ToArray());
        fake.Plans.Add(new[] { Ts }.Concat(Text(" e")).Append(Ts + 50).Append(Transcribe.Eot).ToArray());
        fake.Silent.Add(2);
        var progress = new List<(int, int)>();
        var result = Transcribe.Run(mel, frames, null, fake.Encode, tokens, (a, b) => progress.Add((a, b)), default);
        var s = result.Segments;
        check(result.Language == "zh" && s.Count == 3 && s[0] == new TranscriptSegment(0, 1, "ab") && s[1] == new TranscriptSegment(1, 2, "cd")
            && Math.Abs(s[2].Start - 2) < 1e-9 && Math.Abs(s[2].End - 3) < 1e-9 && s[2].Text == "e", "Transcribe splits segments at timestamp pairs and moves the window by the last one");
        check(fake.Windows == 3 && progress.Select(p => p.Item1).SequenceEqual(new[] { 0, 200, 3200, 4000 }) && progress.All(p => p.Item2 == 4000), "Transcribe windows follow Whisper's seek and skip silence");
        check(fake.Prefixes.Count == 4 && fake.Prefixes[0].SequenceEqual(new[] { Transcribe.Sot }) && fake.Prefixes[2][0] == Transcribe.StartOfPrevious && fake.Prefixes[2].Contains('a')
            && fake.Prefixes[2][^1] == Transcribe.Sot, "Transcribe detects the language once and prompts with the previous text");
        var cues = result.Cues;
        check(cues.Count == 3 && cues[0] == new SubtitleCue(0, 1000, "ab") && cues[2].Start == 2000 && cues[2].End == 3000, "Transcribe segments become subtitle cues");
        check(SubRip.Write(cues).StartsWith("1\r\n00:00:00,000 --> 00:00:01,000\r\nab\r\n", StringComparison.Ordinal), "Transcribe cues write as SubRip");

        // A chosen language skips detection; repetitive text falls back through every temperature; length cap.
        fake = new Fake();
        fake.Plans.Add(new[] { Ts }.Concat(Enumerable.Repeat(Text(" ab"), 200).SelectMany(x => x)).ToArray());
        mel = Transcribe.LogMel(new short[16000 * 5], out frames, default);
        result = Transcribe.Run(mel, frames, "ja", fake.Encode, tokens, null, default);
        check(result.Language == "ja" && fake.Prefixes.Count == 1 && fake.Starts == 6 && fake.Longest <= 448, "Transcribe falls back through six temperatures on repetitive text");
        check(result.Segments.Count == 1 && result.Segments[0].Start == 0 && Math.Abs(result.Segments[0].End - 5) < 1e-9 && result.Segments[0].Text.Length < 230, "Transcribe stops a window at 224 tokens");
        reject(() => Transcribe.Run(mel, frames, "xx", new Fake().Encode, tokens, null, default), "Transcribe refuses an unknown language");
        reject(() => Transcribe.Run(mel, frames, null, new Fake().Encode, tokens, null, cancelled.Token), "Transcribe stops when cancelled");
        reject(() => Transcribe.Run(mel, frames, null, _ => _ => new float[10], tokens, null, default), "Transcribe refuses a model with the wrong vocabulary");
    }
}
