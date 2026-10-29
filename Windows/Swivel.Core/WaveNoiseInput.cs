using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;

namespace Swivel.Core;

// Local container routing only. Each existing reader still validates the entire input
// and complete output budget. The caller keeps the original source lease throughout.
public static class WaveNoiseInput
{
    public const string Clipping = "The float WAV contains samples outside [-1,1]. Use Convert Float WAV to PCM… first and explicitly allow clipping before reducing noise.";
    public const string Hint = "Integer PCM WAV is processed directly. Float32/64 WAV is converted to 24-bit PCM first; samples outside [-1,1], NaN and infinity are refused. Supported integer AIFF/AIFC and G.711 WAV are converted to PCM in memory. Local input and complete PCM output are each limited to 512 MiB and two hours, mono/stereo, 8–192 kHz. MP3, AAC, FLAC and M4A use Windows decoding with its separate 128 MiB limit. Save creates an integer PCM WAV beside the original.";

    // Null means use the original integer PCM stream, without rewriting its bytes.
    // A returned stream belongs to the caller; this method never owns the input.
    public static MemoryStream? Prepare(Stream input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!input.CanRead || !input.CanSeek) throw new ArgumentException("A seekable readable stream is required.");
        int tag = 0;
        if (input.Length >= 12 && input.Length <= PcmWave.MaxBytes)
        {
            input.Position = 0; Span<byte> head = stackalloc byte[12]; input.ReadExactly(head);
            if (head[..4].SequenceEqual("FORM"u8)) tag = -1;
            else if (head[..4].SequenceEqual("RIFF"u8) && head[8..].SequenceEqual("WAVE"u8))
            {
                Span<byte> chunk = stackalloc byte[8]; Span<byte> fmt = stackalloc byte[40];
                for (int count = 0; count < 1024 && input.Length - input.Position >= 8; count++)
                {
                    token.ThrowIfCancellationRequested(); input.ReadExactly(chunk);
                    uint length = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
                    long next = input.Position + length + (length & 1);
                    if (next > input.Length) break;
                    if (chunk[..4].SequenceEqual("fmt "u8) && length >= 2)
                    {
                        int bytes = (int)Math.Min(length, 40u); input.ReadExactly(fmt[..bytes]);
                        tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
                        if (tag == 0xfffe && bytes == 40 && fmt[24..40].SequenceEqual(new byte[] {3,0,0,0,0,0,16,0,128,0,0,170,0,56,155,113})) tag = 3;
                        break;
                    }
                    input.Position = next;
                }
            }
        }
        if (tag is not -1 and not 3 and not 6 and not 7) { PcmWave.Read(input, token); return null; }
        var output = new MemoryStream();
        try
        {
            if (tag == -1) AiffWave.Export(input, output, token);
            else if (tag is 6 or 7) G711Wave.Export(input, output, token);
            else
            {
                try { FloatWave.Export(input, output, new FloatPcmOptions(), token); }
                catch (UserError error) when (error.Message == FloatWave.Clipping) { throw new UserError(Clipping); }
            }
            PcmWave.Read(output, token); output.Position = 0;
            token.ThrowIfCancellationRequested(); return output;
        }
        catch { output.Dispose(); throw; }
    }
}
