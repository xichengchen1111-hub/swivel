using System;
using System.Diagnostics;
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

/// A model file shipped with Swivel, run on this PC with the Windows ML runtime built into
/// Windows. Its bytes are read once, checked against the compiled-in size and SHA-256, and only
/// those verified bytes reach Windows ML; nothing is downloaded. Evaluations run one at a time.
internal sealed class PinnedModel(string file, long bytes, string sha256, string missing, string damaged, string unavailable)
{
    private readonly object gate = new();
    private LearningModel? model;
    private LearningModelSession? session;
    private bool onGraphics;
    private string inputName = "", outputName = "";

    /// Diagnostics for tests: the device and time of the last evaluation, and a switch to the
    /// slower element-by-element output read for comparison.
    internal string LastRun = "";
    internal bool VectorViewRead;

    /// Tests point this at their own copy; the app uses the Models folder beside Swivel.exe.
    public string Path = System.IO.Path.Combine(AppContext.BaseDirectory, "Models", file);

    /// Whether evaluations run on a graphics card (loads the model and opens the session).
    public bool UsesGraphics(CancellationToken token)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            model ??= Load(token);
            try { if (session is null) Open(); }
            catch (Exception error) when (Native(error)) { throw new UserError(unavailable); }
            return onGraphics;
        }
    }

    /// One input named by the model's first input, read back from its first output.
    public float[] Predict(float[] input, long[] inputShape, int outputLength, CancellationToken token)
    {
        if (input.Length != inputShape.Aggregate(1L, (a, b) => a * b)) throw new ArgumentException("Invalid model input.");
        using var tensorIn = TensorFloat.CreateFromArray(inputShape, input);
        return Evaluate(binding => binding.Bind(inputName, tensorIn), output => output(outputName, outputLength), token);
    }

    /// One evaluation: <paramref name="bind"/> binds the inputs by name (and may run again on the
    /// processor), and <paramref name="read"/> copies the float outputs it needs by name and length.
    /// The caller keeps and disposes its input tensors; every output is released here.
    public T Evaluate<T>(Action<LearningModelBinding> bind, Func<Func<string, int, float[]>, T> read, CancellationToken token)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            model ??= Load(token);
            token.ThrowIfCancellationRequested();
            try
            {
                if (session is null) Open();
                try { return Evaluate(session!, bind, read, token); }
                // Some graphics drivers accept the session but fail to run it; the processor still can.
                catch (Exception error) when (onGraphics && Native(error)) { OpenOnProcessor(); return Evaluate(session!, bind, read, token); }
            }
            catch (Exception error) when (Native(error)) { throw new UserError(unavailable); }
        }
    }

    /// Windows ML reports failures as COM errors that the projection maps to many exception
    /// types (invalid argument, not implemented…); all of them mean "cannot run here".
    private static bool Native(Exception error) => error is not (OperationCanceledException or UserError or OutOfMemoryException);

    private T Evaluate<T>(LearningModelSession current, Action<LearningModelBinding> bind, Func<Func<string, int, float[]>, T> read, CancellationToken token)
    {
        var binding = new LearningModelBinding(current);
        LearningModelEvaluationResult? result = null;
        try
        {
            bind(binding);
            var clock = Stopwatch.StartNew();
            result = current.Evaluate(binding, "swivel");
            double evaluate = clock.Elapsed.TotalMilliseconds;
            if (!result.Succeeded) throw new UserError(unavailable);
            clock.Restart();
            var outputs = result.Outputs;
            float[] Output(string name, int length)
            {
                if (!outputs.TryGetValue(name, out var value) || value is not TensorFloat tensor) throw new UserError(unavailable);
                float[] output;
                // The direct read is checked only on the processor so far; anything unexpected uses the vector view.
                try { output = VectorViewRead ? tensor.GetAsVectorView().ToArray() : Read(tensor, length); }
                catch (Exception error) when (error is not OutOfMemoryException) { output = tensor.GetAsVectorView().ToArray(); }
                if (output.Length != length) throw new UserError(unavailable);
                return output;
            }
            var values = read(Output);
            LastRun = $"{(onGraphics ? "graphics" : "processor")}: evaluate {evaluate:0} ms, read {clock.Elapsed.TotalMilliseconds:0} ms";
            token.ThrowIfCancellationRequested();
            return values;
        }
        finally
        {
            // Every output, including those Swivel does not read, is released now rather than by the finaliser.
            if (result is not null) foreach (var pair in result.Outputs) (pair.Value as IDisposable)?.Dispose();
            binding.Clear();
        }
    }

    // The tensor's own memory, copied in one go. Reading through the WinRT vector view costs a
    // cross-boundary call per value (3 million per upscaling tile).
    private static float[] Read(TensorFloat tensor, int length)
    {
        using var reference = tensor.CreateReference();
        IntPtr unknown = ((WinRT.IWinRTObject)reference).NativeObject.ThisPtr;
        var iid = new Guid("5b0d3235-4dba-4d44-865e-8f1d0e4fd04d"); // IMemoryBufferByteAccess
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out IntPtr access));
        try
        {
            // IUnknown's three methods come first; GetBuffer is the fourth slot.
            var getBuffer = Marshal.GetDelegateForFunctionPointer<GetBuffer>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(access), 3 * IntPtr.Size));
            Marshal.ThrowExceptionForHR(getBuffer(access, out IntPtr data, out uint capacity));
            if (data == IntPtr.Zero || capacity < (ulong)length * sizeof(float)) throw new InvalidOperationException("The model output is smaller than expected.");
            var output = new float[length];
            Marshal.Copy(data, output, 0, length);
            return output;
        }
        finally { Marshal.Release(access); }
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetBuffer(IntPtr self, out IntPtr value, out uint capacity);

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
            if (loaded.InputFeatures.Count < 1 || loaded.OutputFeatures.Count < 1) { loaded.Dispose(); throw new UserError(damaged); }
            inputName = loaded.InputFeatures[0].Name; outputName = loaded.OutputFeatures[0].Name;
            return loaded;
        }
        catch (Exception error) when (Native(error)) { throw new UserError(unavailable); }
    }
}
