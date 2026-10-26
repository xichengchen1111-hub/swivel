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

    /// Diagnostics for tests: the device and time of the last evaluation, and switches that
    /// force the processor or the slower element-by-element output read for comparison.
    internal string LastRun = "";
    internal bool ProcessorOnly, VectorViewRead;
    internal uint Threads; // 0 = Windows ML's own choice
    internal void ResetSession() { lock (gate) { session?.Dispose(); session = null; } }

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

    public float[] Predict(float[] input, long[] inputShape, int outputLength, CancellationToken token)
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
                try { return Evaluate(session!, input, inputShape, outputLength, token); }
                // Some graphics drivers accept the session but fail to run it; the processor still can.
                catch (Exception error) when (onGraphics && Native(error)) { OpenOnProcessor(); return Evaluate(session!, input, inputShape, outputLength, token); }
            }
            catch (Exception error) when (Native(error)) { throw new UserError(unavailable); }
        }
    }

    /// Windows ML reports failures as COM errors that the projection maps to many exception
    /// types (invalid argument, not implemented…); all of them mean "cannot run here".
    private static bool Native(Exception error) => error is not (OperationCanceledException or UserError or OutOfMemoryException);

    private float[] Evaluate(LearningModelSession current, float[] input, long[] inputShape, int outputLength, CancellationToken token)
    {
        var binding = new LearningModelBinding(current);
        using var tensorIn = TensorFloat.CreateFromArray(inputShape, input);
        LearningModelEvaluationResult? result = null;
        try
        {
            binding.Bind(inputName, tensorIn);
            var clock = Stopwatch.StartNew();
            result = current.Evaluate(binding, "swivel");
            double evaluate = clock.Elapsed.TotalMilliseconds;
            if (!result.Succeeded || !result.Outputs.TryGetValue(outputName, out var value) || value is not TensorFloat tensor) throw new UserError(unavailable);
            clock.Restart();
            var output = VectorViewRead ? tensor.GetAsVectorView().ToArray() : Read(tensor, outputLength);
            LastRun = $"{(onGraphics ? "graphics" : "processor")}: evaluate {evaluate:0} ms, read {clock.Elapsed.TotalMilliseconds:0} ms";
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
        if (ProcessorOnly) { OpenOnProcessor(); return; }
        try { session = new LearningModelSession(model, new LearningModelDevice(LearningModelDeviceKind.DirectXHighPerformance)); onGraphics = true; }
        catch (Exception error) when (Native(error)) { OpenOnProcessor(); }
    }
    private void OpenOnProcessor()
    {
        session?.Dispose(); session = null; onGraphics = false;
        var options = new LearningModelSessionOptions();
        if (Threads > 0)
        {
            // ILearningModelSessionOptionsNative::SetIntraOpNumThreadsOverride (Windows 10 2004 and later).
            IntPtr unknown = ((WinRT.IWinRTObject)options).NativeObject.ThisPtr;
            var iid = new Guid("c71e953f-37b4-4564-8658-d8396866db0d");
            if (Marshal.QueryInterface(unknown, in iid, out IntPtr native) >= 0)
                try { Marshal.GetDelegateForFunctionPointer<SetThreads>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(native), 3 * IntPtr.Size))(native, Threads); }
                finally { Marshal.Release(native); }
        }
        session = new LearningModelSession(model, new LearningModelDevice(LearningModelDeviceKind.Cpu), options);
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetThreads(IntPtr self, uint count);

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
