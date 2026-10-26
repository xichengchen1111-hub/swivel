using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Threading;
using Swivel.Core;
using Windows.AI.MachineLearning;
using Windows.Storage.Streams;

namespace Swivel.Windows;

/// A model file shipped with Swivel, run on this PC with the Windows ML runtime built into
/// Windows. Its bytes are read once, checked against the compiled-in size and SHA-256, and only
/// those verified bytes reach Windows ML; nothing is downloaded. Evaluations run one at a time.
internal sealed class PinnedModel(string file, long bytes, string sha256, long[] inputShape, int outputLength, string missing, string damaged, string unavailable)
{
    private readonly object gate = new();
    private LearningModel? model;
    private LearningModelSession? session;
    private bool onGraphics;
    private string inputName = "", outputName = "";

    /// Tests point this at their own copy; the app uses the Models folder beside Swivel.exe.
    public string Path = System.IO.Path.Combine(AppContext.BaseDirectory, "Models", file);

    public float[] Predict(float[] input, CancellationToken token)
    {
        if (input.Length != inputShape.Aggregate(1L, (a, b) => a * b)) throw new ArgumentException("Invalid model input.");
        lock (gate)
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
            catch (Exception error) when (Native(error)) { throw new UserError(unavailable); }
        }
    }

    /// Windows ML reports failures as COM errors that the projection maps to many exception
    /// types (invalid argument, not implemented…); all of them mean "cannot run here".
    private static bool Native(Exception error) => error is not (OperationCanceledException or UserError or OutOfMemoryException);

    private float[] Evaluate(LearningModelSession current, float[] input, CancellationToken token)
    {
        var binding = new LearningModelBinding(current);
        using var tensorIn = TensorFloat.CreateFromArray(inputShape, input);
        LearningModelEvaluationResult? result = null;
        try
        {
            binding.Bind(inputName, tensorIn);
            result = current.Evaluate(binding, "swivel");
            if (!result.Succeeded || !result.Outputs.TryGetValue(outputName, out var value) || value is not TensorFloat tensor) throw new UserError(unavailable);
            var output = tensor.GetAsVectorView().ToArray();
            if (output.Length != outputLength) throw new UserError(unavailable);
            token.ThrowIfCancellationRequested();
            return output;
        }
        finally
        {
            // Every output, including those Swivel does not read, is released now rather than by the finaliser.
            if (result is not null) foreach (var pair in result.Outputs) (pair.Value as IDisposable)?.Dispose();
            binding.Clear();
        }
    }

    // The graphics card when Windows offers one, otherwise the processor.
    private void Open()
    {
        try { session = new LearningModelSession(model, new LearningModelDevice(LearningModelDeviceKind.DirectXHighPerformance)); onGraphics = true; }
        catch (Exception error) when (Native(error)) { OpenOnProcessor(); }
    }
    private void OpenOnProcessor()
    {
        session?.Dispose(); session = null; onGraphics = false;
        session = new LearningModelSession(model, new LearningModelDevice(LearningModelDeviceKind.Cpu));
    }

    private LearningModel Load(CancellationToken token)
    {
        byte[] data;
        try
        {
            using var stream = new FileStream(Path, new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read, Options = FileOptions.SequentialScan });
            if (stream.Length != bytes) throw new UserError(damaged);
            data = new byte[bytes];
            int read = 0;
            while (read < data.Length)
            {
                token.ThrowIfCancellationRequested();
                int count = stream.Read(data, read, Math.Min(1 << 20, data.Length - read));
                if (count == 0) throw new UserError(damaged);
                read += count;
            }
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { throw new UserError(missing); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new UserError(damaged); }
        if (!Convert.ToHexString(SHA256.HashData(data)).Equals(sha256, StringComparison.OrdinalIgnoreCase)) throw new UserError(damaged);
        token.ThrowIfCancellationRequested();
        try
        {
            LearningModel loaded;
            // A Windows stream that can be cloned; a wrapped .NET stream cannot, and Windows ML clones it.
            using (var memory = new InMemoryRandomAccessStream())
            {
                for (int offset = 0; offset < data.Length; offset += 16 << 20)
                    memory.WriteAsync(data.AsBuffer(offset, Math.Min(16 << 20, data.Length - offset))).AsTask().GetAwaiter().GetResult();
                memory.Seek(0);
                loaded = LearningModel.LoadFromStream(RandomAccessStreamReference.CreateFromStream(memory));
            }
            if (loaded.InputFeatures.Count != 1 || loaded.OutputFeatures.Count < 1) { loaded.Dispose(); throw new UserError(damaged); }
            inputName = loaded.InputFeatures[0].Name; outputName = loaded.OutputFeatures[0].Name;
            return loaded;
        }
        catch (Exception error) when (Native(error)) { throw new UserError(unavailable); }
    }
}
