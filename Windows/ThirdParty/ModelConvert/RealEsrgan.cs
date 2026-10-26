using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

/// RRDBNet x4 (64 features, 23 blocks of three residual dense blocks, growth 32), the
/// architecture published with Real-ESRGAN, written out as ONNX opset 11 nodes.
internal static class RealEsrgan
{
    public const string CheckpointSha256 = "4fa0d38905f75ac06eb49a7951b426670021be3018265fd191d2125df9d682f1";

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
                if (used.Add(name + part)) Constant(name + part, tensor.Shape, tensor.Floats);
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
        // Height and width are left free so the app can pick the block size for the device.
        graph.Message(11, Value("image", "height", "width"));
        // The last node's output is renamed by an Identity so the graph's output has a stable name.
        var identity = new Proto(); identity.String(1, output); identity.String(2, "upscaled"); identity.String(3, "upscaled"); identity.String(4, "Identity");
        graph.Message(1, identity.ToArray());
        graph.Message(12, Value("upscaled", "height4", "width4"));

        var model = new Proto();
        model.Varint(1, 6); model.String(2, "Swivel"); model.String(3, "1");
        var opset = new Proto(); opset.String(1, ""); opset.Varint(2, 11);
        model.Message(8, opset.ToArray());
        model.Message(7, graph.ToArray());
        return model.ToArray();
    }

    private static byte[] Value(string name, string height, string width)
    {
        var shape = new Proto();
        foreach (int d in new[] { 1, 3 }) { var dim = new Proto(); dim.Varint(1, d); shape.Message(1, dim.ToArray()); }
        foreach (string d in new[] { height, width }) { var dim = new Proto(); dim.String(2, d); shape.Message(1, dim.ToArray()); }
        var tensor = new Proto(); tensor.Varint(1, 1); tensor.Message(2, shape.ToArray());
        var type = new Proto(); type.Message(1, tensor.ToArray());
        var value = new Proto(); value.String(1, name); value.Message(2, type.ToArray());
        return value.ToArray();
    }
}

