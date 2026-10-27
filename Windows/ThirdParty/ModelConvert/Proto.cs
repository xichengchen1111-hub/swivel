using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

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

/// Collects ONNX nodes, initializers, inputs and outputs and writes an IR 6 / opset 11 model.
internal sealed class GraphBuilder
{
    public const int Float = 1, Int64 = 7, Float16 = 10;
    private readonly List<byte[]> nodes = new(), initializers = new(), inputs = new(), outputs = new();
    private int counter;

    public string Node(string op, string[] inputs, params byte[][] attributes) => Named(op, inputs, "t" + (counter++).ToString(System.Globalization.CultureInfo.InvariantCulture), attributes);

    public string Named(string op, string[] inputs, string output, params byte[][] attributes)
    {
        var node = new Proto();
        foreach (var input in inputs) node.String(1, input);
        node.String(2, output); node.String(3, output); node.String(4, op);
        foreach (var attribute in attributes) node.Message(5, attribute);
        nodes.Add(node.ToArray());
        return output;
    }

    public string Initializer(string name, int[] shape, int type, byte[] raw)
    {
        var tensor = new Proto();
        foreach (int d in shape) tensor.Varint(1, d);
        tensor.Varint(2, type); tensor.String(8, name); tensor.Bytes(9, raw);
        initializers.Add(tensor.ToArray());
        return name;
    }

    public string Floats(string name, int[] shape, params float[] values)
    {
        var raw = new byte[values.Length * 4]; Buffer.BlockCopy(values, 0, raw, 0, raw.Length);
        return Initializer(name, shape, Float, raw);
    }

    public string Longs(string name, params long[] values)
    {
        var raw = new byte[values.Length * 8]; Buffer.BlockCopy(values, 0, raw, 0, raw.Length);
        return Initializer(name, new[] { values.Length }, Int64, raw);
    }

    /// A graph input or output; each dimension is a fixed int or a free name.
    public void Input(string name, int type, params object[] dims) => inputs.Add(Value(name, type, dims));
    public void Output(string name, int type, params object[] dims) => outputs.Add(Value(name, type, dims));

    private static byte[] Value(string name, int type, object[] dims)
    {
        var shape = new Proto();
        foreach (var d in dims)
        {
            var dim = new Proto();
            if (d is int size) dim.Varint(1, size); else dim.String(2, (string)d);
            shape.Message(1, dim.ToArray());
        }
        var tensor = new Proto(); tensor.Varint(1, type); tensor.Message(2, shape.ToArray());
        var typed = new Proto(); typed.Message(1, tensor.ToArray());
        var value = new Proto(); value.String(1, name); value.Message(2, typed.ToArray());
        return value.ToArray();
    }

    public byte[] Build(string name)
    {
        var graph = new Proto();
        foreach (var node in nodes) graph.Message(1, node);
        graph.String(2, name);
        foreach (var tensor in initializers) graph.Message(5, tensor);
        foreach (var input in inputs) graph.Message(11, input);
        foreach (var output in outputs) graph.Message(12, output);
        var model = new Proto();
        model.Varint(1, 6); model.String(2, "Swivel"); model.String(3, "1");
        var opset = new Proto(); opset.String(1, ""); opset.Varint(2, 11);
        model.Message(8, opset.ToArray());
        model.Message(7, graph.ToArray());
        return model.ToArray();
    }
}
