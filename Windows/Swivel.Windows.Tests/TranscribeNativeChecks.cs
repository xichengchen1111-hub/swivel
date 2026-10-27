using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Swivel.Core;
using Swivel.Windows;
using Windows.Media.SpeechSynthesis;

internal static class TranscribeNativeChecks
{
    internal static void Run(string directory, Action<bool, string> check, Action<Action, string> reject)
    {
        // Windows-only: the converted Whisper base model through Windows ML.
        string root = Path.Combine(directory, "transcribe"); Directory.CreateDirectory(root);
        string real = SpeechModel.Encoder.Path, tokens = SpeechModel.TokensPath;
        check(File.Exists(real) && File.Exists(SpeechModel.Decoder.Path) && File.Exists(tokens), "The pinned speech model and tokenizer are copied beside the app: " + real);
        SpeechModel.Encoder.Path = Path.Combine(root, "none.onnx");
        reject(() => SpeechModel.Encoder.UsesGraphics(default), "A missing speech model is refused");
        string fake = Path.Combine(root, "fake.onnx"); File.WriteAllBytes(fake, new byte[4096]);
        SpeechModel.Encoder.Path = fake;
        reject(() => SpeechModel.Encoder.UsesGraphics(default), "A speech model of the wrong size is refused");
        SpeechModel.Encoder.Path = real;
        SpeechModel.TokensPath = fake;
        reject(() => SpeechModel.Tokens(default), "A damaged tokenizer is refused");
        SpeechModel.TokensPath = tokens;
        if (!File.Exists(real)) return;

        // Ten seconds of silence: nothing is recognised and nothing is saved.
        string quiet = Path.Combine(root, "quiet.wav");
        WriteWave(quiet, new byte[44100 * 2 * 10], 44100);
        var clock = Stopwatch.StartNew();
        using (var document = TranscribeDocument.OpenAsync(quiet, default).GetAwaiter().GetResult())
        {
            var silent = document.Run(null, null, default);
            Console.WriteLine($"Transcribe 10 s of silence (first run, model load included): {clock.ElapsedMilliseconds} ms, {SpeechModel.Encoder.LastRun}");
            check(Math.Abs(document.Seconds - 10) < 0.01 && silent.Segments.Count == 0, "Transcribe finds no speech in silence");
            reject(() => document.Save(silent, true, true, default), "Transcribe saves nothing when there is no speech");
        }

        // English speech from Windows' own synthesizer, when this Windows has a voice.
        if (SpeechSynthesizer.AllVoices.Count == 0) { Console.WriteLine("Transcribe speech check skipped: this Windows has no speech synthesis voice."); return; }
        string speech = Path.Combine(root, "speech.wav");
        using (var synthesizer = new SpeechSynthesizer())
        using (var stream = synthesizer.SynthesizeTextToStreamAsync("The quick brown fox jumps over the lazy dog. Please meet me at the station at three o'clock tomorrow afternoon.").AsTask().GetAwaiter().GetResult())
        using (var file = File.Create(speech)) stream.AsStreamForRead().CopyTo(file);
        clock.Restart();
        using (var document = TranscribeDocument.OpenAsync(speech, default).GetAwaiter().GetResult())
        {
            int steps = 0;
            var transcript = document.Run(null, (done, total, left) => steps++, default);
            string all = string.Join(" ", transcript.Segments.Select(s => s.Text));
            Console.WriteLine($"Transcribe {document.Seconds:0.0} s of synthesized speech: {clock.ElapsedMilliseconds} ms, {SpeechModel.Decoder.LastRun}, language {transcript.Language}: {all}");
            check(transcript.Language == "en" && all.Contains("fox", StringComparison.OrdinalIgnoreCase) && all.Contains("station", StringComparison.OrdinalIgnoreCase) && steps >= 2,
                "Transcribe recognises synthesized English speech");
            check(transcript.Segments.All(s => s.Start >= 0 && s.End > s.Start && s.End <= document.Seconds + 1), "Transcribe segment times lie inside the recording");
            var saved = document.Save(transcript, true, true, default);
            check(saved.SequenceEqual(new[] { Path.Combine(root, "speech.txt"), Path.Combine(root, "speech.srt") }) && File.ReadAllText(saved[1]).StartsWith("1\r\n00:00:", StringComparison.Ordinal)
                && File.ReadAllText(saved[0]).Contains("fox", StringComparison.OrdinalIgnoreCase), "Transcribe saves text and subtitles beside the original");
            check(document.Save(transcript, false, true, default).Single() == Path.Combine(root, "speech 2.srt"), "Transcribe never overwrites an earlier result");
            // The same segments drive music lowering for the speech WAV itself.
            using (var main = File.OpenRead(speech))
            {
                var info = PcmWave.Read(main, default);
                var activity = WaveActivity.Analyze(main, new(0, info.Seconds), new WaveDucking(-35, .2, transcript.Segments), default);
                var first = transcript.Segments[0];
                check(activity.IsActive((long)((first.Start + first.End) / 2 * info.Rate)) && (transcript.Segments[^1].End + 1 >= info.Seconds || !activity.IsActive(activity.Frames - 1)),
                    "Transcribed speech drives music lowering");
            }
            var english = document.Run("en", null, default);
            check(english.Language == "en" && english.Segments.Count > 0, "Transcribe with a chosen language");
        }
    }

    private static void WriteWave(string path, byte[] data, int rate)
    {
        using var file = new BinaryWriter(File.Create(path));
        file.Write("RIFF"u8); file.Write(36 + data.Length); file.Write("WAVEfmt "u8); file.Write(16); file.Write((short)1); file.Write((short)1);
        file.Write(rate); file.Write(rate * 2); file.Write((short)2); file.Write((short)16); file.Write("data"u8); file.Write(data.Length); file.Write(data);
    }
}
