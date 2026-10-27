using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Windows.AI.MachineLearning;

namespace Swivel;

/// Finds files by what they are about, not only by the words they contain: “上周领导给的工作内容” finds the client
/// brief even when it never says 工作. Each document's name, folder and the text Windows Search already extracted
/// (System.Search.AutoSummary, about the first thousand characters) become one vector, made on this PC by
/// multilingual-e5-small (ThirdParty/MultilingualE5, MIT) running in Windows' own ONNX runtime. A request becomes a
/// vector the same way and the closest files are offered beside the keyword results. Files are never opened (so
/// OneDrive placeholders stay online-only), the index keeps paths, dates and vectors but no text, and nothing leaves
/// the PC. Chinese and English share one vector space, so “操作系统复习” finds an English cheat sheet.
static class SemanticIndex
{
    const int Dimensions = 384;
    /// Below this a file is only somewhat related; e5's similarities sit between about 0.7 and 0.95.
    public const float Related = 0.80f;

    /// The model files and their SHA-256 (UPSTREAM.md); anything else is refused.
    public static readonly (string Name, string Sha256)[] ModelFiles =
    [
        ("model_quantized.onnx", "f80102d3f2a1229f387d3c81909990d8945513e347b0eab049f7de3c6f98c193"),
        ("tokenizer.json", "0b44a9d7b51c3c62626640cda0e2c2f70fdacdc25bbbd68038369d14ebdf4c39"),
    ];

    public static string ModelFolder => System.IO.Path.Combine(AppContext.BaseDirectory, "Models", "MultilingualE5");
    public static bool ModelInstalled => ModelFiles.All(f => File.Exists(System.IO.Path.Combine(ModelFolder, f.Name)));

    /// Documents Windows Search extracts text from; pictures and media have none worth embedding.
    static readonly string[] Extensions = [".pdf", ".doc", ".docx", ".rtf", ".odt", ".wps", ".txt", ".md", ".ppt", ".pptx", ".odp", ".dps",
        ".xls", ".xlsx", ".csv", ".ods", ".et", ".pages", ".key", ".numbers", ".epub", ".eml", ".msg"];
    const int MaxFiles = 20000;

    public sealed record Entry(string Path, DateTime Modified, DateTime Created, long Size, Half[] Vector);

    static readonly object gate = new();
    static List<Entry>? entries;
    static Dictionary<string, Entry>? byPath;
    static Embedder? embedder;
    static Task? refreshing;
    static DateTime refreshedAt = DateTime.MinValue;

    /// What Settings shows: (files ready, files left to read in the current pass), or null before the first pass.
    public static (int Ready, int Left)? Progress { get; private set; }

    public static event Action? Changed;

    public static bool IsReady { get { lock (gate) return entries is { Count: > 0 }; } }

    public static bool Enabled => Prefs.FileSearch && Prefs.SemanticSearch && ModelInstalled;

    static string IndexFile => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Swivel", "SemanticIndex", "index-v1.bin");

    // MARK: Updating

    /// Brings the index up to date in the background, at most every ten minutes (Quick Actions calls this when it opens).
    public static void RefreshSoon(bool force = false)
    {
        if (!Enabled) return;
        lock (gate)
        {
            if (refreshing is { IsCompleted: false }) return;
            if (!force && DateTime.UtcNow - refreshedAt < TimeSpan.FromMinutes(10)) return;
            refreshedAt = DateTime.UtcNow;
            refreshing = RunLowPriority(() => Refresh(CancellationToken.None));
        }
    }

    /// Waits for a full pass (the `--index` command line).
    public static Task RefreshNow(CancellationToken cancel = default)
    {
        lock (gate)
        {
            refreshedAt = DateTime.UtcNow;
            refreshing = RunLowPriority(() => Refresh(cancel));
            return refreshing;
        }
    }

    /// A below-normal thread, so reading the index never competes with what the user is doing.
    static Task RunLowPriority(Action work)
    {
        var done = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            try { work(); done.SetResult(); }
            catch (Exception ex) { Log.Write($"Semantic index: {ex.Message}"); done.SetResult(); }
        }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Semantic index" };
        thread.Start();
        return done.Task;
    }

    static void Refresh(CancellationToken cancel)
    {
        Load();
        var model = Model();
        if (model is null) return;
        var candidates = Candidates();
        if (candidates is null) return;   // Windows Search is off: keep what we have.
        Dictionary<string, Entry> known;
        lock (gate) known = new(byPath ?? [], StringComparer.OrdinalIgnoreCase);
        var keep = new List<Entry>();
        var todo = new List<Candidate>();
        foreach (var candidate in candidates)
        {
            if (known.TryGetValue(candidate.Path, out var entry) && entry.Modified == candidate.Modified && entry.Size == candidate.Size)
                keep.Add(entry with { Created = candidate.Created });
            else todo.Add(candidate);
        }
        Publish(keep, todo.Count);
        var fresh = new List<Entry>(keep);
        int sinceSave = 0;
        foreach (var candidate in todo)
        {
            if (cancel.IsCancellationRequested || !Enabled) break;
            float[] vector;
            try { vector = model.Embed("passage: " + Passage(candidate), maxTokens: 256); }
            catch (Exception ex) { Log.Write($"Semantic index: {ex.Message}"); continue; }
            fresh.Add(new Entry(candidate.Path, candidate.Modified, candidate.Created, candidate.Size, vector.Select(v => (Half)v).ToArray()));
            if (++sinceSave >= 100)
            {
                sinceSave = 0;
                Publish(fresh, todo.Count - (fresh.Count - keep.Count));
                Save(fresh);
            }
        }
        Publish(fresh, 0);
        Save(fresh);
    }

    static void Publish(List<Entry> list, int left)
    {
        lock (gate)
        {
            entries = [.. list];
            byPath = new(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries) byPath[entry.Path] = entry;
            Progress = (entries.Count, left);
        }
        Changed?.Invoke();
    }

    /// The text a file is represented by: its name (as words), the folder it sits in and what Windows read from it.
    public static string Passage(Candidate candidate)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(candidate.Path).Replace('_', ' ').Replace('-', ' ');
        var folder = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(candidate.Path) ?? "");
        var text = new StringBuilder(name);
        if (folder.Length > 0) text.Append(" · ").Append(folder);
        if (candidate.Summary.Length > 0) text.Append(" · ").Append(candidate.Summary.Length > 1200 ? candidate.Summary[..1200] : candidate.Summary);
        return text.ToString();
    }

    public sealed record Candidate(string Path, DateTime Modified, DateTime Created, long Size, string Summary);

    /// Every document in the user's folders the index knows, newest first, or null when Windows Search is off.
    static List<Candidate>? Candidates()
    {
        var extensions = string.Join(" OR ", Extensions.Select(e => $"System.FileExtension = '{e}'"));
        var sql = $"SELECT TOP {MaxFiles} System.ItemUrl, System.DateModified, System.DateCreated, System.Size, System.Search.AutoSummary " +
                  $"FROM SystemIndex WHERE SCOPE='file:{FileSearch.Home.Replace('\\', '/').Replace("'", "''")}' AND ({extensions}) " +
                  "ORDER BY System.DateModified DESC";
        var rows = FileSearch.Rows(sql, 5, 120);
        if (rows is null) return null;
        var list = new List<Candidate>();
        foreach (var row in rows)
        {
            if (row[0] is not string url || !url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) continue;
            var path = url[5..].Replace('/', '\\');
            if (!FileSearch.IsVisible(path)) continue;
            list.Add(new Candidate(path, Utc(row[1]), Utc(row[2]), row[3] is IConvertible size ? Convert.ToInt64(size, CultureInfo.InvariantCulture) : 0,
                Clean(row[4] as string ?? "")));
        }
        return list;
    }

    static DateTime Utc(object? value) => value is DateTime time ? DateTime.SpecifyKind(time, DateTimeKind.Utc) : DateTime.MinValue;

    /// AutoSummary keeps line breaks, form feeds and runs of spaces from the document.
    static string Clean(string text)
    {
        var result = new StringBuilder(text.Length);
        bool space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c)) { space = result.Length > 0; continue; }
            if (space) result.Append(' ');
            space = false;
            result.Append(c);
        }
        return result.ToString();
    }

    // MARK: Asking

    /// The request as a vector, or null when there is no index or model. `words` (the words the rules found, with
    /// their translations) are blended in, so “最近的大学文件” leans towards lectures and assignments.
    public static float[]? Query(string request, string? words = null)
    {
        Load();
        lock (gate) if (entries is not { Count: > 0 }) return null;
        if (Model() is not { } model || request.Trim().Length == 0) return null;
        var query = model.Embed("query: " + request.Trim(), maxTokens: 64);
        if (!string.IsNullOrWhiteSpace(words))
        {
            var extra = model.Embed("query: " + words.Trim(), maxTokens: 64);
            for (int i = 0; i < query.Length; i++) query[i] += extra[i];
            double length = Math.Sqrt(query.Sum(v => (double)v * v));
            for (int i = 0; i < query.Length; i++) query[i] = (float)(query[i] / length);
        }
        return query;
    }

    /// How close each indexed file that passes `keep` is to the request, best first (see `Similarity`).
    public static List<(Entry Entry, float Similarity)> Closest(string request, Func<Entry, bool> keep, int limit = 60) =>
        Query(request) is { } query ? Closest(query, keep, limit) : [];

    public static List<(Entry Entry, float Similarity)> Closest(float[] query, Func<Entry, bool> keep, int limit = 60)
    {
        List<Entry>? list;
        lock (gate) list = entries;
        var scored = new List<(Entry, float)>();
        foreach (var entry in list ?? [])
            if (keep(entry)) scored.Add((entry, Similarity(query, entry)));
        return scored.OrderByDescending(s => s.Item2).Take(limit).ToList();
    }

    /// Cosine similarity of the request and the file (both have length 1).
    static float Similarity(float[] query, Entry entry)
    {
        float dot = 0;
        var vector = entry.Vector;
        for (int i = 0; i < Dimensions; i++) dot += query[i] * (float)vector[i];
        return dot;
    }

    /// How close one file is to the request, or null when it isn't indexed (pictures, media, folders).
    public static float? Similarity(float[] query, string path)
    {
        Entry? entry;
        lock (gate) entry = byPath != null && byPath.TryGetValue(path, out var found) ? found : null;
        return entry is null ? null : Similarity(query, entry);
    }

    /// Loads the model once (checking its files), or null when it is missing or refused.
    static Embedder? Model()
    {
        lock (gate)
        {
            if (embedder != null) return embedder;
            if (!ModelInstalled) return null;
            try
            {
                foreach (var (name, sha) in ModelFiles)
                {
                    using var stream = File.OpenRead(System.IO.Path.Combine(ModelFolder, name));
                    if (Convert.ToHexStringLower(SHA256.HashData(stream)) != sha) { Log.Write($"Semantic index: {name} does not match"); return null; }
                }
                embedder = new Embedder(System.IO.Path.Combine(ModelFolder, "model_quantized.onnx"), System.IO.Path.Combine(ModelFolder, "tokenizer.json"));
            }
            catch (Exception ex) { Log.Write($"Semantic index: model failed to load: {ex.Message}"); return null; }
            return embedder;
        }
    }

    /// Opens the model in the background, so the first search with Quick Actions doesn't wait for it.
    public static void Prewarm()
    {
        if (!Enabled) return;
        _ = Task.Run(() => { Load(); Model(); });
    }

    // MARK: Storage

    const string Magic = "SWSI1";

    static void Load()
    {
        lock (gate) if (entries != null) return;
        var list = new List<Entry>();
        try
        {
            if (File.Exists(IndexFile))
            {
                using var reader = new BinaryReader(File.OpenRead(IndexFile), Encoding.UTF8);
                if (reader.ReadString() == Magic && reader.ReadString() == ModelFiles[0].Sha256)
                {
                    int count = reader.ReadInt32();
                    for (int i = 0; i < count; i++)
                    {
                        var path = reader.ReadString();
                        var modified = DateTime.FromBinary(reader.ReadInt64());
                        var created = DateTime.FromBinary(reader.ReadInt64());
                        var size = reader.ReadInt64();
                        var vector = new Half[Dimensions];
                        for (int d = 0; d < Dimensions; d++) vector[d] = BitConverter.UInt16BitsToHalf(reader.ReadUInt16());
                        list.Add(new Entry(path, modified, created, size, vector));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or UnauthorizedAccessException) { list.Clear(); }
        lock (gate)
        {
            if (entries != null) return;
            entries = list;
            byPath = new(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in list) byPath[entry.Path] = entry;
            if (list.Count > 0) Progress = (list.Count, 0);
        }
    }

    /// Writes beside the index and moves it into place, so a crash never leaves half an index.
    static void Save(List<Entry> list)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(IndexFile)!);
            var temporary = IndexFile + ".tmp";
            using (var writer = new BinaryWriter(File.Create(temporary), Encoding.UTF8))
            {
                writer.Write(Magic);
                writer.Write(ModelFiles[0].Sha256);
                writer.Write(list.Count);
                foreach (var entry in list)
                {
                    writer.Write(entry.Path);
                    writer.Write(entry.Modified.ToBinary());
                    writer.Write(entry.Created.ToBinary());
                    writer.Write(entry.Size);
                    foreach (var value in entry.Vector) writer.Write(BitConverter.HalfToUInt16Bits(value));
                }
            }
            File.Move(temporary, IndexFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Write($"Semantic index: {ex.Message}"); }
    }

    /// Forgets the index (Settings, when the switch is turned off).
    public static void Clear()
    {
        lock (gate)
        {
            entries = [];
            byPath = new(StringComparer.OrdinalIgnoreCase);
            Progress = null;
        }
        try { File.Delete(IndexFile); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Changed?.Invoke();
    }
}

/// multilingual-e5-small through Windows.AI.MachineLearning: tokens in, the mean of the last hidden states out,
/// scaled to length 1.
sealed class Embedder
{
    readonly LearningModelSession session;
    readonly UnigramTokenizer tokenizer;
    readonly string output;
    readonly bool wantsTypes;
    readonly object gate = new();

    public Embedder(string modelPath, string tokenizerPath)
    {
        tokenizer = UnigramTokenizer.Load(tokenizerPath);
        var model = LearningModel.LoadFromFilePath(modelPath);
        session = new LearningModelSession(model, new LearningModelDevice(LearningModelDeviceKind.Cpu));
        output = model.OutputFeatures[0].Name;
        wantsTypes = model.InputFeatures.Any(f => f.Name == "token_type_ids");
    }

    public float[] Embed(string text, int maxTokens)
    {
        var ids = tokenizer.Encode(text, maxTokens).Select(i => (long)i).ToArray();
        long[] shape = [1, ids.Length];
        lock (gate)
        {
            var binding = new LearningModelBinding(session);
            binding.Bind("input_ids", TensorInt64Bit.CreateFromArray(shape, ids));
            binding.Bind("attention_mask", TensorInt64Bit.CreateFromArray(shape, Enumerable.Repeat(1L, ids.Length).ToArray()));
            if (wantsTypes) binding.Bind("token_type_ids", TensorInt64Bit.CreateFromArray(shape, new long[ids.Length]));
            var result = session.Evaluate(binding, "e5");
            var states = ((TensorFloat)result.Outputs[output]).GetAsVectorView();
            int width = states.Count / ids.Length;
            var mean = new float[width];
            for (int t = 0; t < ids.Length; t++)
                for (int d = 0; d < width; d++) mean[d] += states[t * width + d];
            double length = Math.Sqrt(mean.Sum(v => (double)v * v));
            for (int d = 0; d < width; d++) mean[d] = (float)(mean[d] / Math.Max(length, 1e-9));
            return mean;
        }
    }
}

/// XLM-RoBERTa's SentencePiece unigram tokenizer, read from tokenizer.json: normalise (NFKC, one space between
/// words), mark word starts with ▁, then pick the most likely split of each word into known pieces (Viterbi).
sealed class UnigramTokenizer
{
    const char Boundary = '▁';
    const int Start = 0, End = 2, Unknown = 3;
    readonly Dictionary<string, (int Id, float Score)> pieces;
    readonly int longest;
    readonly float unknownScore;

    UnigramTokenizer(Dictionary<string, (int, float)> pieces)
    {
        this.pieces = pieces;
        longest = Math.Min(32, pieces.Keys.Max(k => k.Length));
        unknownScore = pieces.Values.Min(p => p.Item2) - 10;
    }

    public static UnigramTokenizer Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var vocab = document.RootElement.GetProperty("model").GetProperty("vocab");
        var pieces = new Dictionary<string, (int, float)>(vocab.GetArrayLength(), StringComparer.Ordinal);
        int id = 0;
        foreach (var item in vocab.EnumerateArray())
        {
            var piece = item[0].GetString() ?? "";
            if (id > Unknown) pieces.TryAdd(piece, (id, (float)item[1].GetDouble()));
            id++;
        }
        return new UnigramTokenizer(pieces);
    }

    /// `<s>` pieces `</s>`, at most `maxTokens` in all.
    public List<int> Encode(string text, int maxTokens)
    {
        var ids = new List<int> { Start };
        foreach (var word in Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            ids.AddRange(Split(Boundary + word));
            if (ids.Count >= maxTokens - 1) break;
        }
        if (ids.Count > maxTokens - 1) ids.RemoveRange(maxTokens - 1, ids.Count - (maxTokens - 1));
        ids.Add(End);
        return ids;
    }

    static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormKC))
            builder.Append(char.IsWhiteSpace(c) ? ' ' : char.IsControl(c) ? '\0' : c);
        return builder.Replace("\0", "").ToString();
    }

    /// The highest-scoring split of one word; characters no piece covers become one `<unk>` per run.
    List<int> Split(string word)
    {
        int n = word.Length;
        var best = new float[n + 1];
        var from = new int[n + 1];
        var piece = new int[n + 1];
        Array.Fill(best, float.NegativeInfinity);
        best[0] = 0;
        for (int i = 0; i < n; i++)
        {
            if (float.IsNegativeInfinity(best[i]) || char.IsLowSurrogate(word[i])) continue;
            bool covered = false;
            for (int length = 1; length <= longest && i + length <= n; length++)
            {
                if (char.IsHighSurrogate(word[i + length - 1])) continue;
                if (!pieces.TryGetValue(word.Substring(i, length), out var known)) continue;
                covered |= length == 1 || (length == 2 && char.IsHighSurrogate(word[i]));
                var score = best[i] + known.Score;
                if (score > best[i + length]) { best[i + length] = score; from[i + length] = i; piece[i + length] = known.Id; }
            }
            if (!covered)
            {
                int length = char.IsHighSurrogate(word[i]) && i + 1 < n ? 2 : 1;
                var score = best[i] + unknownScore;
                if (score > best[i + length]) { best[i + length] = score; from[i + length] = i; piece[i + length] = Unknown; }
            }
        }
        var ids = new List<int>();
        for (int at = n; at > 0; at = from[at]) ids.Add(piece[at]);
        ids.Reverse();
        // Runs of unknown characters are one <unk>, as in the reference tokenizer.
        return ids.Where((id, i) => id != Unknown || i == 0 || ids[i - 1] != Unknown).ToList();
    }
}
