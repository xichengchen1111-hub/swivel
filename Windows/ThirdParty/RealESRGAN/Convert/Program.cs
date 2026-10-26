using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

// Converts the official RealESRGAN_x4plus.pth (a PyTorch zip checkpoint) into a fixed-size
// ONNX graph for Windows ML, without Python. Only the handful of pickle instructions a plain
// state dict uses are understood; anything else is refused. The output is byte-for-byte
// deterministic, so the build pins its SHA-256.
internal static class Program
{
    private const string CheckpointSha256 = "4fa0d38905f75ac06eb49a7951b426670021be3018265fd191d2125df9d682f1";
    public const int Tile = 256;

    private static int Main(string[] args)
    {
        if (args.Length != 2) { Console.Error.WriteLine("usage: RealEsrganConvert <RealESRGAN_x4plus.pth> <output.onnx>"); return 2; }
        byte[] checkpoint = File.ReadAllBytes(args[0]);
        if (!Convert.ToHexString(SHA256.HashData(checkpoint)).Equals(CheckpointSha256, StringComparison.OrdinalIgnoreCase))
        { Console.Error.WriteLine("The checkpoint does not match the pinned SHA-256."); return 1; }
        var weights = Checkpoint.Read(checkpoint);
        byte[] model = Graph.Build(weights);
        string part = args[1] + ".part";
        File.WriteAllBytes(part, model);
        File.Move(part, args[1], true);
        Console.WriteLine(Convert.ToHexString(SHA256.HashData(model)));
        return 0;
    }
}

internal sealed record Tensor(int[] Shape, float[] Data);

/// A minimal reader for torch.save's zip format: data.pkl names each tensor's storage file.
internal static class Checkpoint
{
    private sealed record Global(string Module, string Name);
    private sealed record Storage(string Key);
    private sealed record RawTensor(string Key, int Offset, int[] Size, int[] Stride);
    private sealed class Mark { public static readonly Mark Value = new(); }

    public static Dictionary<string, Tensor> Read(byte[] file)
    {
        using var zip = new ZipArchive(new MemoryStream(file), ZipArchiveMode.Read);
        var pickle = zip.Entries.Single(e => e.FullName.EndsWith("/data.pkl", StringComparison.Ordinal));
        string prefix = pickle.FullName[..^"data.pkl".Length];
        byte[] code = ReadAll(pickle);
        if (Unpickle(code) is not Dictionary<object, object> top || !top.TryGetValue("params_ema", out var state) || state is not Dictionary<object, object> dict)
            throw new InvalidDataException("Expected a dictionary with params_ema.");
        var result = new Dictionary<string, Tensor>(StringComparer.Ordinal);
        foreach (var (key, value) in dict)
        {
            if (key is not string name || value is not RawTensor raw) throw new InvalidDataException("Unexpected state entry.");
            int count = raw.Size.Aggregate(1, (a, b) => checked(a * b));
            // Only contiguous tensors that start their storage are accepted.
            int expected = 1;
            for (int d = raw.Size.Length - 1; d >= 0; d--) { if (raw.Stride[d] != expected) throw new InvalidDataException(name + " is not contiguous."); expected *= raw.Size[d]; }
            if (raw.Offset != 0) throw new InvalidDataException(name + " has a storage offset.");
            byte[] bytes = ReadAll(zip.GetEntry(prefix + "data/" + raw.Key) ?? throw new InvalidDataException("Missing storage " + raw.Key));
            if (bytes.Length != count * 4) throw new InvalidDataException(name + " storage has the wrong size.");
            var data = new float[count];
            Buffer.BlockCopy(bytes, 0, data, 0, bytes.Length);
            if (!BitConverter.IsLittleEndian || data.Any(v => !float.IsFinite(v))) throw new InvalidDataException(name + " is not finite.");
            result.Add(name, new Tensor(raw.Size, data));
        }
        return result;
    }

    private static byte[] ReadAll(ZipArchiveEntry entry)
    {
        using var stream = entry.Open(); using var memory = new MemoryStream(); stream.CopyTo(memory); return memory.ToArray();
    }

    private static object Unpickle(byte[] code)
    {
        var stack = new List<object>(); var memo = new Dictionary<int, object>();
        object Pop() { var v = stack[^1]; stack.RemoveAt(stack.Count - 1); return v; }
        List<object> PopMark()
        {
            int m = stack.FindLastIndex(v => v is Mark);
            if (m < 0) throw new InvalidDataException("Missing mark.");
            var items = stack.GetRange(m + 1, stack.Count - m - 1); stack.RemoveRange(m, stack.Count - m); return items;
        }
        int i = 0;
        int U8() => code[i++];
        int U16() { int v = code[i] | code[i + 1] << 8; i += 2; return v; }
        int I32() { int v = BitConverter.ToInt32(code, i); i += 4; return v; }
        string Line() { int end = Array.IndexOf(code, (byte)'\n', i); var s = Encoding.ASCII.GetString(code, i, end - i); i = end + 1; return s; }
        while (true)
        {
            byte op = code[i++];
            switch (op)
            {
                case 0x80: U8(); break;                                   // PROTO
                case (byte)'}': stack.Add(new Dictionary<object, object>()); break; // EMPTY_DICT
                case (byte)')': stack.Add(Array.Empty<object>()); break;  // EMPTY_TUPLE
                case (byte)'(': stack.Add(Mark.Value); break;             // MARK
                case (byte)'t': stack.Add(PopMark().ToArray()); break;    // TUPLE
                case 0x85: stack.Add(new[] { Pop() }); break;             // TUPLE1
                case (byte)'q': memo[U8()] = stack[^1]; break;            // BINPUT
                case (byte)'r': memo[I32()] = stack[^1]; break;           // LONG_BINPUT
                case (byte)'h': stack.Add(memo[U8()]); break;             // BINGET
                case (byte)'j': stack.Add(memo[I32()]); break;            // LONG_BINGET
                case (byte)'K': stack.Add(U8()); break;                   // BININT1
                case (byte)'M': stack.Add(U16()); break;                  // BININT2
                case (byte)'J': stack.Add(I32()); break;                  // BININT
                case 0x89: stack.Add(false); break;                       // NEWFALSE
                case (byte)'X': { int n = I32(); stack.Add(Encoding.UTF8.GetString(code, i, n)); i += n; break; } // BINUNICODE
                case (byte)'c': { string module = Line(), name = Line(); stack.Add(new Global(module, name)); break; } // GLOBAL
                case (byte)'Q':                                            // BINPERSID
                    {
                        if (Pop() is not object[] { Length: 5 } id || id[0] is not "storage" || id[1] is not Global { Module: "torch", Name: "FloatStorage" } || id[2] is not string key)
                            throw new InvalidDataException("Only float storages are supported.");
                        stack.Add(new Storage(key)); break;
                    }
                case (byte)'R':                                            // REDUCE
                    {
                        var arguments = (object[])Pop(); var callable = Pop();
                        stack.Add(callable switch
                        {
                            Global { Module: "collections", Name: "OrderedDict" } when arguments.Length == 0 => new Dictionary<object, object>(),
                            Global { Module: "torch._utils", Name: "_rebuild_tensor_v2" } when arguments is [Storage s, int offset, object[] size, object[] stride, false, Dictionary<object, object> { Count: 0 }]
                                => new RawTensor(s.Key, offset, size.Cast<int>().ToArray(), stride.Cast<int>().ToArray()),
                            _ => throw new InvalidDataException("Unsupported pickle call."),
                        });
                        break;
                    }
                case (byte)'s': { var value = Pop(); var key = Pop(); ((Dictionary<object, object>)stack[^1]).Add(key, value); break; } // SETITEM
                case (byte)'u':                                            // SETITEMS
                    {
                        var items = PopMark(); var dict = (Dictionary<object, object>)stack[^1];
                        for (int k = 0; k < items.Count; k += 2) dict.Add(items[k], items[k + 1]);
                        break;
                    }
                case (byte)'b': Pop(); break;                              // BUILD (the state dict's _metadata), ignored
                case (byte)'.': return Pop();                              // STOP
                default: throw new InvalidDataException($"Unsupported pickle instruction 0x{op:x2}.");
            }
        }
    }
}

/// RRDBNet x4 (64 features, 23 blocks of three residual dense blocks, growth 32), the
/// architecture published with Real-ESRGAN, written out as ONNX opset 11 nodes.
internal static class Graph
{
    public static byte[] Build(Dictionary<string, Tensor> weights)
    {
        var nodes = new List<byte[]>(); var initializers = new List<byte[]>(); var used = new HashSet<string>(StringComparer.Ordinal);
        int counter = 0;
        string Fresh() => "t" + (counter++).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string Node(string op, string[] inputs, params byte[][] attributes)
        {
            string output = Fresh();
            var node = new Proto();
            foreach (var input in inputs) node.String(1, input);
            node.String(2, output); node.String(3, output); node.String(4, op);
            foreach (var attribute in attributes) node.Message(5, attribute);
            nodes.Add(node.ToArray());
            return output;
        }
        void Constant(string name, int[] shape, float[] data)
        {
            var tensor = new Proto();
            foreach (int d in shape) tensor.Varint(1, d);
            tensor.Varint(2, 1); tensor.String(8, name);
            var raw = new byte[data.Length * 4]; Buffer.BlockCopy(data, 0, raw, 0, raw.Length); tensor.Bytes(9, raw);
            initializers.Add(tensor.ToArray());
        }
        string Conv(string input, string name)
        {
            foreach (var part in new[] { ".weight", ".bias" })
            {
                var tensor = weights.TryGetValue(name + part, out var t) ? t : throw new InvalidDataException("Missing " + name + part);
                if (used.Add(name + part)) Constant(name + part, tensor.Shape, tensor.Data);
            }
            var w = weights[name + ".weight"];
            if (w.Shape is not [_, _, 3, 3]) throw new InvalidDataException(name + " is not a 3×3 convolution.");
            return Node("Conv", new[] { input, name + ".weight", name + ".bias" }, Attribute.Ints("kernel_shape", 3, 3), Attribute.Ints("pads", 1, 1, 1, 1), Attribute.Ints("strides", 1, 1));
        }
        string Lrelu(string x) => Node("LeakyRelu", new[] { x }, Attribute.Float("alpha", 0.2f));
        string Concat(params string[] x) => Node("Concat", x, Attribute.Int("axis", 1));
        string Residual(string x, string skip) => Node("Add", new[] { Node("Mul", new[] { x, "scale" }), skip });
        string Up(string x) => Node("Resize", new[] { x, "roi", "scales" }, Attribute.Text("coordinate_transformation_mode", "asymmetric"), Attribute.Text("mode", "nearest"), Attribute.Text("nearest_mode", "floor"));

        Constant("scale", Array.Empty<int>(), new[] { 0.2f });
        Constant("roi", new[] { 0 }, Array.Empty<float>());
        Constant("scales", new[] { 4 }, new[] { 1f, 1f, 2f, 2f });
        string feat = Conv("image", "conv_first"), body = feat;
        for (int b = 0; b < 23; b++)
        {
            string block = body;
            for (int r = 1; r <= 3; r++)
            {
                string p = $"body.{b}.rdb{r}.";
                var x = new List<string> { block };
                for (int c = 1; c <= 4; c++) x.Add(Lrelu(Conv(c == 1 ? block : Concat(x.ToArray()), p + "conv" + c)));
                block = Residual(Conv(Concat(x.ToArray()), p + "conv5"), block);
            }
            body = Residual(block, body);
        }
        feat = Node("Add", new[] { feat, Conv(body, "conv_body") });
        feat = Lrelu(Conv(Up(feat), "conv_up1"));
        feat = Lrelu(Conv(Up(feat), "conv_up2"));
        string output = Conv(Lrelu(Conv(feat, "conv_hr")), "conv_last");
        if (used.Count != weights.Count) throw new InvalidDataException("The checkpoint has tensors this graph does not use.");

        var graph = new Proto();
        foreach (var node in nodes) graph.Message(1, node);
        graph.String(2, "RealESRGAN_x4plus");
        foreach (var tensor in initializers) graph.Message(5, tensor);
        graph.Message(11, Value("image", Program.Tile));
        // The last node's output is renamed by an Identity so the graph's output has a stable name.
        var identity = new Proto(); identity.String(1, output); identity.String(2, "upscaled"); identity.String(3, "upscaled"); identity.String(4, "Identity");
        graph.Message(1, identity.ToArray());
        graph.Message(12, Value("upscaled", Program.Tile * 4));

        var model = new Proto();
        model.Varint(1, 6); model.String(2, "Swivel"); model.String(3, "1");
        var opset = new Proto(); opset.String(1, ""); opset.Varint(2, 11);
        model.Message(8, opset.ToArray());
        model.Message(7, graph.ToArray());
        return model.ToArray();
    }

    private static byte[] Value(string name, int side)
    {
        var shape = new Proto();
        foreach (int d in new[] { 1, 3, side, side }) { var dim = new Proto(); dim.Varint(1, d); shape.Message(1, dim.ToArray()); }
        var tensor = new Proto(); tensor.Varint(1, 1); tensor.Message(2, shape.ToArray());
        var type = new Proto(); type.Message(1, tensor.ToArray());
        var value = new Proto(); value.String(1, name); value.Message(2, type.ToArray());
        return value.ToArray();
    }
}

internal static class Attribute
{
    public static byte[] Float(string name, float value) { var a = Head(name, 1); a.Fixed32(2, value); return a.ToArray(); }
    public static byte[] Int(string name, long value) { var a = Head(name, 2); a.Varint(3, value); return a.ToArray(); }
    public static byte[] Text(string name, string value) { var a = Head(name, 3); a.String(4, value); return a.ToArray(); }
    public static byte[] Ints(string name, params long[] values) { var a = Head(name, 7); foreach (long v in values) a.Varint(8, v); return a.ToArray(); }
    private static Proto Head(string name, int type) { var a = new Proto(); a.String(1, name); a.Varint(20, type); return a; }
}

/// Protocol buffer wire encoding, written field by field in a fixed order.
internal sealed class Proto
{
    private readonly MemoryStream data = new();
    private void Key(int field, int wire) => Raw((ulong)(field << 3 | wire));
    private void Raw(ulong v) { while (v >= 0x80) { data.WriteByte((byte)(v | 0x80)); v >>= 7; } data.WriteByte((byte)v); }
    public void Varint(int field, long value) { Key(field, 0); Raw((ulong)value); }
    public void Fixed32(int field, float value) { Key(field, 5); data.Write(BitConverter.GetBytes(value)); }
    public void Bytes(int field, byte[] value) { Key(field, 2); Raw((ulong)value.Length); data.Write(value); }
    public void String(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));
    public void Message(int field, byte[] value) => Bytes(field, value);
    public byte[] ToArray() => data.ToArray();
}
