using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Threading;
using Swivel.Core;
using Windows.AI.MachineLearning;
using Windows.Storage.Streams;

namespace Swivel.Windows;

/// The pinned U²-Net model, run on this PC with the Windows ML runtime built into Windows.
/// Its bytes are read once, checked against the compiled-in size and SHA-256, and only those
/// verified bytes reach Windows ML; nothing is downloaded. Evaluations run one at a time.
internal static class CutoutModel
{
    private const string Missing = "The background model is not included in this copy of Swivel. Use a complete package.";
    private const string Damaged = "The background model in this copy of Swivel is damaged. Use a complete package.";
    private const string Unavailable = "Windows ML could not run the background model on this PC.";
    private static readonly object Gate = new();
    private static LearningModel? model;
    private static LearningModelSession? session;
    private static bool onGraphics;
    private static string inputName = "", outputName = "";

    /// Tests point this at their own copy; the app uses the Models folder beside Swivel.exe.
    internal static string ModelPath = Path.Combine(AppContext.BaseDirectory, "Models", Cutout.ModelFile);

    public static float[] Predict(float[] input, CancellationToken token)
    {
        if (input.Length != 3 * Cutout.Side * Cutout.Side) throw new ArgumentException("Invalid model input.");
        lock (Gate)
        {
            token.ThrowIfCancellationRequested();
            model ??= Load(token);
            token.ThrowIfCancellationRequested();
            try
            {
                if (session is null) Open();
                try { return Evaluate(session!, input, token); }
                // Some graphics drivers accept the session but fail to run it; the processor still can.
                catch (Exception error) when (onGraphics && Native(error)) { OpenOnProcessor(); return Evaluate(session!, input, token); }
            }
            catch (Exception error) when (Native(error)) { throw new UserError(Unavailable); }
        }
    }

    /// Windows ML reports failures as COM errors that the projection maps to many exception
    /// types (invalid argument, not implemented…); all of them mean "cannot run here".
    private static bool Native(Exception error) => error is not (OperationCanceledException or UserError or OutOfMemoryException);

    private static float[] Evaluate(LearningModelSession current, float[] input, CancellationToken token)
    {
        var binding = new LearningModelBinding(current);
        using var tensorIn = TensorFloat.CreateFromArray(new long[] { 1, 3, Cutout.Side, Cutout.Side }, input);
        LearningModelEvaluationResult? result = null;
        try
        {
            binding.Bind(inputName, tensorIn);
            result = current.Evaluate(binding, "swivel");
            if (!result.Succeeded || !result.Outputs.TryGetValue(outputName, out var value) || value is not TensorFloat tensor) throw new UserError(Unavailable);
            var output = tensor.GetAsVectorView().ToArray();
            token.ThrowIfCancellationRequested();
            return output;
        }
        finally
        {
            // Every output, including the six Swivel does not read, is released now rather than by the finaliser.
            if (result is not null) foreach (var pair in result.Outputs) (pair.Value as IDisposable)?.Dispose();
            binding.Clear();
        }
    }

    // The graphics card when Windows offers one, otherwise the processor.
    private static void Open()
    {
        try { session = new LearningModelSession(model, new LearningModelDevice(LearningModelDeviceKind.DirectXHighPerformance)); onGraphics = true; }
        catch (Exception error) when (Native(error)) { OpenOnProcessor(); }
    }
    private static void OpenOnProcessor()
    {
        session?.Dispose(); session = null; onGraphics = false;
        session = new LearningModelSession(model, new LearningModelDevice(LearningModelDeviceKind.Cpu));
    }

    private static LearningModel Load(CancellationToken token)
    {
        byte[] bytes;
        try
        {
            using var file = new FileStream(ModelPath, new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read, Options = FileOptions.SequentialScan });
            if (file.Length != Cutout.ModelBytes) throw new UserError(Damaged);
            bytes = new byte[Cutout.ModelBytes];
            int read = 0;
            while (read < bytes.Length)
            {
                token.ThrowIfCancellationRequested();
                int count = file.Read(bytes, read, Math.Min(1 << 20, bytes.Length - read));
                if (count == 0) throw new UserError(Damaged);
                read += count;
            }
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { throw new UserError(Missing); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new UserError(Damaged); }
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(Cutout.ModelSha256, StringComparison.OrdinalIgnoreCase)) throw new UserError(Damaged);
        token.ThrowIfCancellationRequested();
        try
        {
            LearningModel loaded;
            // A Windows stream that can be cloned; a wrapped .NET stream cannot, and Windows ML clones it.
            using (var stream = new InMemoryRandomAccessStream())
            {
                for (int offset = 0; offset < bytes.Length; offset += 16 << 20)
                    stream.WriteAsync(bytes.AsBuffer(offset, Math.Min(16 << 20, bytes.Length - offset))).AsTask().GetAwaiter().GetResult();
                stream.Seek(0);
                loaded = LearningModel.LoadFromStream(RandomAccessStreamReference.CreateFromStream(stream));
            }
            if (loaded.InputFeatures.Count != 1 || loaded.OutputFeatures.Count < 1) { loaded.Dispose(); throw new UserError(Damaged); }
            inputName = loaded.InputFeatures[0].Name; outputName = loaded.OutputFeatures[0].Name;
            return loaded;
        }
        catch (Exception error) when (Native(error)) { throw new UserError(Unavailable); }
    }
}
