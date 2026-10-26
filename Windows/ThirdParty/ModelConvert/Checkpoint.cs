using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

/// A checkpoint tensor: little-endian float32 or float16 values in their stored order.
internal sealed record Tensor(int[] Shape, byte[] Raw, bool Half)
{
    public int Count => Raw.Length / (Half ? 2 : 4);
    public float[] Floats
    {
        get
        {
            var data = new float[Count];
            if (Half) for (int i = 0; i < data.Length; i++) data[i] = (float)BitConverter.ToHalf(Raw, i * 2);
            else Buffer.BlockCopy(Raw, 0, data, 0, Raw.Length);
            return data;
        }
    }
}

/// A minimal reader for torch.save's zip format: data.pkl names each tensor's storage file.
internal static class Checkpoint
{
    private sealed record Global(string Module, string Name);
    private sealed record Storage(string Key, bool Half);
    private sealed record RawTensor(Storage Storage, int Offset, int[] Size, int[] Stride);
    private sealed class Mark { public static readonly Mark Value = new(); }

    public static Dictionary<string, Tensor> Read(byte[] file, string stateKey)
    {
        using var zip = new ZipArchive(new MemoryStream(file), ZipArchiveMode.Read);
        var pickle = zip.Entries.Single(e => e.FullName.EndsWith("/data.pkl", StringComparison.Ordinal));
        string prefix = pickle.FullName[..^"data.pkl".Length];
        byte[] code = ReadAll(pickle);
        if (Unpickle(code) is not Dictionary<object, object> top || !top.TryGetValue(stateKey, out var state) || state is not Dictionary<object, object> dict)
            throw new InvalidDataException("Expected a dictionary with " + stateKey + ".");
        var result = new Dictionary<string, Tensor>(StringComparer.Ordinal);
        foreach (var (key, value) in dict)
        {
            if (key is not string name || value is not RawTensor raw) throw new InvalidDataException("Unexpected state entry.");
            if (raw.Size.Length != raw.Stride.Length || raw.Size.Any(v => v <= 0)) throw new InvalidDataException(name + " has an invalid shape.");
            int count = raw.Size.Aggregate(1, (a, b) => checked(a * b));
            // Strided views (such as a stored transpose) are gathered into a contiguous copy;
            // every element they address must lie inside the storage.
            int unit = raw.Storage.Half ? 2 : 4;
            byte[] storage = ReadAll(zip.GetEntry(prefix + "data/" + raw.Storage.Key) ?? throw new InvalidDataException("Missing storage " + raw.Storage.Key));
            if (storage.Length % unit != 0 || raw.Offset < 0 || raw.Stride.Any(v => v < 0)) throw new InvalidDataException(name + " storage is malformed.");
            long last = raw.Offset;
            for (int d = 0; d < raw.Size.Length; d++) last += (long)(raw.Size[d] - 1) * raw.Stride[d];
            if (count == 0 || last >= storage.Length / unit) throw new InvalidDataException(name + " lies outside its storage.");
            var bytes = new byte[checked(count * unit)];
            var index = new int[raw.Size.Length];
            for (int e = 0; e < count; e++)
            {
                long source = raw.Offset;
                for (int d = 0; d < index.Length; d++) source += (long)index[d] * raw.Stride[d];
                Buffer.BlockCopy(storage, (int)(source * unit), bytes, e * unit, unit);
                for (int d = index.Length - 1; d >= 0 && ++index[d] == raw.Size[d]; d--) index[d] = 0;
            }
            var tensor = new Tensor(raw.Size, bytes, raw.Storage.Half);
            if (!BitConverter.IsLittleEndian || tensor.Floats.Any(v => !float.IsFinite(v))) throw new InvalidDataException(name + " is not finite.");
            result.Add(name, tensor);
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
                case 0x86: { var b = Pop(); var a = Pop(); stack.Add(new[] { a, b }); break; } // TUPLE2
                case 0x87: { var c = Pop(); var b = Pop(); var a = Pop(); stack.Add(new[] { a, b, c }); break; } // TUPLE3
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
                        if (Pop() is not object[] { Length: 5 } id || id[0] is not "storage" || id[1] is not Global { Module: "torch", Name: "FloatStorage" or "HalfStorage" } kind || id[2] is not string key)
                            throw new InvalidDataException("Only float and half storages are supported.");
                        stack.Add(new Storage(key, kind.Name == "HalfStorage")); break;
                    }
                case (byte)'R':                                            // REDUCE
                    {
                        var arguments = (object[])Pop(); var callable = Pop();
                        stack.Add(callable switch
                        {
                            Global { Module: "collections", Name: "OrderedDict" } when arguments.Length == 0 => new Dictionary<object, object>(),
                            Global { Module: "torch._utils", Name: "_rebuild_tensor_v2" } when arguments is [Storage s, int offset, object[] size, object[] stride, false, Dictionary<object, object> { Count: 0 }]
                                => new RawTensor(s, offset, size.Cast<int>().ToArray(), stride.Cast<int>().ToArray()),
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

