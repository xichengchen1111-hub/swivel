using System;
using System.IO;
using System.Security.Cryptography;

// Build-time converters from official PyTorch checkpoints to ONNX graphs for Windows ML, without
// Python. Only the handful of pickle instructions a plain state dict uses are understood;
// anything else is refused. Outputs are byte-for-byte deterministic, so the build pins them.
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            switch (args)
            {
                case ["realesrgan", var input, var output]:
                    Write(output, RealEsrgan.Build(Checkpoint.Read(Pinned(input, RealEsrgan.CheckpointSha256), "params_ema")));
                    return 0;
                case ["whisper", var input, var encoder, var decoder]:
                    {
                        var weights = Checkpoint.Read(Pinned(input, Whisper.CheckpointSha256), "model_state_dict");
                        Write(encoder, Whisper.Encoder(weights)); Write(decoder, Whisper.Decoder(weights));
                        return 0;
                    }
                default:
                    Console.Error.WriteLine("usage: ModelConvert realesrgan <RealESRGAN_x4plus.pth> <output.onnx>\n       ModelConvert whisper <base.pt> <encoder.onnx> <decoder.onnx>");
                    return 2;
            }
        }
        catch (InvalidDataException error) { Console.Error.WriteLine(error.Message); return 1; }
    }

    private static byte[] Pinned(string path, string sha256)
    {
        byte[] file = File.ReadAllBytes(path);
        if (!Convert.ToHexString(SHA256.HashData(file)).Equals(sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The checkpoint does not match the pinned SHA-256.");
        return file;
    }

    private static void Write(string path, byte[] model)
    {
        string part = path + ".part";
        File.WriteAllBytes(part, model);
        File.Move(part, path, true);
        Console.WriteLine(Convert.ToHexString(SHA256.HashData(model)) + "  " + Path.GetFileName(path));
    }
}
