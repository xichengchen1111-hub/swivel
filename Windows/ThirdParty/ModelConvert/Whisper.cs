using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

/// OpenAI Whisper (base: 6 + 6 layers, 512 wide, 8 heads) as two ONNX graphs, following the
/// published model (whisper/model.py, MIT): the encoder turns a 3000-frame log-mel window into
/// each decoder layer's cross-attention keys and values, and the decoder turns the tokens so far
/// into the next token's logits (no cache: the app re-runs it on the growing prefix). Weights stay
/// float16 as published and are cast to float32 inside the graph.
internal static class Whisper
{
    public const string CheckpointSha256 = "ed3a0b6b1c0edf879ad9b11b1af5a0e6ab5db9205f891f668f8b0e6c6326e34e";
    private const int Width = 512, Heads = 8, Depth = Width / Heads, Layers = 6, Vocabulary = 51865, AudioContext = 1500, TextContext = 448;

    public static byte[] Encoder(Dictionary<string, Tensor> weights)
    {
        var g = new Net(weights);
        g.B.Input("mel", GraphBuilder.Float, 1, 80, 3000);
        string x = g.Gelu(g.B.Node("Conv", new[] { "mel", g.W("encoder.conv1.weight"), g.W("encoder.conv1.bias") }, Attribute.Ints("kernel_shape", 3), Attribute.Ints("pads", 1, 1), Attribute.Ints("strides", 1)));
        x = g.Gelu(g.B.Node("Conv", new[] { x, g.W("encoder.conv2.weight"), g.W("encoder.conv2.bias") }, Attribute.Ints("kernel_shape", 3), Attribute.Ints("pads", 1, 1), Attribute.Ints("strides", 2)));
        x = g.B.Node("Add", new[] { g.B.Node("Transpose", new[] { x }, Attribute.Ints("perm", 0, 2, 1)), g.W("encoder.positional_embedding") });
        for (int i = 0; i < Layers; i++)
        {
            string p = $"encoder.blocks.{i}.";
            x = g.Add(x, g.Attention(g.Norm(x, p + "attn_ln"), p + "attn", null));
            x = g.Add(x, g.Mlp(g.Norm(x, p + "mlp_ln"), p + "mlp"));
        }
        string audio = g.Norm(x, "encoder.ln_post");
        // Cross-attention keys and values depend only on the audio: computed once per window here.
        for (int i = 0; i < Layers; i++)
        {
            string p = $"decoder.blocks.{i}.cross_attn.";
            g.B.Named("Identity", new[] { g.Scaled(g.Heads(g.Linear(audio, p + "key", bias: false), 0, 2, 3, 1)) }, $"cross_k_{i}");
            g.B.Named("Identity", new[] { g.Heads(g.Linear(audio, p + "value"), 0, 2, 1, 3) }, $"cross_v_{i}");
            g.B.Output($"cross_k_{i}", GraphBuilder.Float, 1, Heads, Depth, AudioContext);
            g.B.Output($"cross_v_{i}", GraphBuilder.Float, 1, Heads, AudioContext, Depth);
        }
        return g.Finish("whisper_base_encoder", weights.Keys.Where(k => k.StartsWith("encoder.", StringComparison.Ordinal) || k.Contains(".cross_attn.key.") || k.Contains(".cross_attn.value.")));
    }

    public static byte[] Decoder(Dictionary<string, Tensor> weights)
    {
        var g = new Net(weights);
        g.B.Input("tokens", GraphBuilder.Int64, 1, "n");
        for (int i = 0; i < Layers; i++)
        {
            g.B.Input($"cross_k_{i}", GraphBuilder.Float, 1, Heads, Depth, AudioContext);
            g.B.Input($"cross_v_{i}", GraphBuilder.Float, 1, Heads, AudioContext, Depth);
        }
        string embedding = g.W("decoder.token_embedding.weight");
        // n = the token count, as a one-element tensor for Slice.
        string n = g.B.Node("Slice", new[] { g.B.Node("Shape", new[] { "tokens" }), g.B.Longs("one", 1), g.B.Longs("two", 2), g.B.Longs("zero", 0) });
        string x = g.B.Node("Add", new[] { g.B.Node("Gather", new[] { embedding, "tokens" }, Attribute.Int("axis", 0)),
            g.B.Node("Slice", new[] { g.W("decoder.positional_embedding"), "zero", n, "zero" }) });
        // The causal mask: −10⁹ above the diagonal, cut to n × n.
        var mask = new float[TextContext * TextContext];
        for (int r = 0; r < TextContext; r++) for (int c = r + 1; c < TextContext; c++) mask[r * TextContext + c] = -1e9f;
        g.B.Floats("mask", new[] { TextContext, TextContext }, mask);
        string causal = g.B.Node("Slice", new[] { "mask", g.B.Longs("zeros", 0, 0), g.B.Node("Concat", new[] { n, n }, Attribute.Int("axis", 0)), g.B.Longs("axes", 0, 1) });
        for (int i = 0; i < Layers; i++)
        {
            string p = $"decoder.blocks.{i}.";
            x = g.Add(x, g.Attention(g.Norm(x, p + "attn_ln"), p + "attn", causal));
            x = g.Add(x, g.Cross(g.Norm(x, p + "cross_attn_ln"), p + "cross_attn", $"cross_k_{i}", $"cross_v_{i}"));
            x = g.Add(x, g.Mlp(g.Norm(x, p + "mlp_ln"), p + "mlp"));
        }
        x = g.Norm(x, "decoder.ln");
        string last = g.B.Node("Gather", new[] { x, g.B.Initializer("last", Array.Empty<int>(), GraphBuilder.Int64, BitConverter.GetBytes(-1L)) }, Attribute.Int("axis", 1));
        g.B.Named("MatMul", new[] { last, g.B.Node("Transpose", new[] { embedding }, Attribute.Ints("perm", 1, 0)) }, "logits");
        g.B.Output("logits", GraphBuilder.Float, 1, Vocabulary);
        return g.Finish("whisper_base_decoder", weights.Keys.Where(k => k.StartsWith("decoder.", StringComparison.Ordinal) && !k.Contains(".cross_attn.key.") && !k.Contains(".cross_attn.value.")));
    }

    private sealed class Net(Dictionary<string, Tensor> weights)
    {
        public readonly GraphBuilder B = new();
        private readonly Dictionary<string, string> cast = new(StringComparer.Ordinal);
        private readonly HashSet<string> used = new(StringComparer.Ordinal);
        private string? half, one, scale, root2, eps, shape, merge;

        private Tensor Get(string key) => weights.TryGetValue(key, out var t) && t.Half ? t : throw new InvalidDataException("Missing float16 tensor " + key);

        /// A published float16 tensor, cast to float32.
        public string W(string key)
        {
            if (cast.TryGetValue(key, out var done)) return done;
            var t = Get(key); used.Add(key);
            B.Initializer(key, t.Shape, GraphBuilder.Float16, t.Raw);
            return cast[key] = B.Node("Cast", new[] { key }, Attribute.Int("to", GraphBuilder.Float));
        }

        /// A linear layer's [out, in] weight stored transposed as [in, out] for MatMul.
        private string Transposed(string key)
        {
            var t = Get(key); used.Add(key);
            if (t.Shape is not [int rows, int columns]) throw new InvalidDataException(key + " is not a matrix.");
            var raw = new byte[t.Raw.Length];
            for (int r = 0; r < rows; r++) for (int c = 0; c < columns; c++)
                { raw[(c * rows + r) * 2] = t.Raw[(r * columns + c) * 2]; raw[(c * rows + r) * 2 + 1] = t.Raw[(r * columns + c) * 2 + 1]; }
            B.Initializer(key + ".T", new[] { columns, rows }, GraphBuilder.Float16, raw);
            return B.Node("Cast", new[] { key + ".T" }, Attribute.Int("to", GraphBuilder.Float));
        }

        public string Linear(string x, string prefix, bool bias = true)
        {
            string y = B.Node("MatMul", new[] { x, Transposed(prefix + ".weight") });
            return bias ? B.Node("Add", new[] { y, W(prefix + ".bias") }) : y;
        }

        public string Add(string a, string b) => B.Node("Add", new[] { a, b });

        public string Norm(string x, string prefix)
        {
            eps ??= B.Floats("eps", Array.Empty<int>(), 1e-5f);
            string mean = B.Node("ReduceMean", new[] { x }, Attribute.Ints("axes", -1), Attribute.Int("keepdims", 1));
            string d = B.Node("Sub", new[] { x, mean });
            string variance = B.Node("ReduceMean", new[] { B.Node("Mul", new[] { d, d }) }, Attribute.Ints("axes", -1), Attribute.Int("keepdims", 1));
            string y = B.Node("Div", new[] { d, B.Node("Sqrt", new[] { B.Node("Add", new[] { variance, eps }) }) });
            return B.Node("Add", new[] { B.Node("Mul", new[] { y, W(prefix + ".weight") }), W(prefix + ".bias") });
        }

        // Exact GELU: x · ½ · (1 + erf(x / √2)).
        public string Gelu(string x)
        {
            half ??= B.Floats("half", Array.Empty<int>(), 0.5f);
            root2 ??= B.Floats("inverse_root2", Array.Empty<int>(), (float)(1 / Math.Sqrt(2)));
            one ??= B.Floats("one_f", Array.Empty<int>(), 1f);
            return B.Node("Mul", new[] { B.Node("Mul", new[] { x, half }), B.Node("Add", new[] { one, B.Node("Erf", new[] { B.Node("Mul", new[] { x, root2 }) }) }) });
        }

        public string Mlp(string x, string prefix) => Linear(Gelu(Linear(x, prefix + ".0")), prefix + ".2");

        /// [1, t, 512] → [1, 8, t, 64] then the given permutation of (batch, time, head, depth).
        public string Heads(string x, params long[] perm)
        {
            shape ??= B.Longs("heads", 0, 0, Whisper.Heads, Depth);
            return B.Node("Transpose", new[] { B.Node("Reshape", new[] { x, shape }) }, Attribute.Ints("perm", perm));
        }

        // Queries and keys are each scaled by depth^−¼, as in the published model.
        public string Scaled(string x)
        {
            scale ??= B.Floats("scale", Array.Empty<int>(), (float)Math.Pow(Depth, -0.25));
            return B.Node("Mul", new[] { x, scale });
        }

        private string Merge(string weights, string values, string prefix)
        {
            string o = B.Node("Transpose", new[] { B.Node("MatMul", new[] { weights, values }) }, Attribute.Ints("perm", 0, 2, 1, 3));
            return Linear(B.Node("Reshape", new[] { o, merge ??= B.Longs("merge", 0, 0, Width) }), prefix + ".out");
        }

        public string Attention(string x, string prefix, string? mask)
        {
            string q = Scaled(Heads(Linear(x, prefix + ".query"), 0, 2, 1, 3));
            string k = Scaled(Heads(Linear(x, prefix + ".key", bias: false), 0, 2, 3, 1));
            string v = Heads(Linear(x, prefix + ".value"), 0, 2, 1, 3);
            string scores = B.Node("MatMul", new[] { q, k });
            if (mask is not null) scores = B.Node("Add", new[] { scores, mask });
            return Merge(B.Node("Softmax", new[] { scores }, Attribute.Int("axis", 3)), v, prefix);
        }

        public string Cross(string x, string prefix, string keys, string values)
        {
            string q = Scaled(Heads(Linear(x, prefix + ".query"), 0, 2, 1, 3));
            return Merge(B.Node("Softmax", new[] { B.Node("MatMul", new[] { q, keys }) }, Attribute.Int("axis", 3)), values, prefix);
        }

        public byte[] Finish(string name, IEnumerable<string> expected)
        {
            var missing = expected.Where(k => !used.Contains(k)).ToArray();
            if (missing.Length > 0 || used.Any(k => !expected.Contains(k))) throw new InvalidDataException("Unused or unexpected tensors: " + string.Join(", ", missing));
            return B.Build(name);
        }
    }
}
