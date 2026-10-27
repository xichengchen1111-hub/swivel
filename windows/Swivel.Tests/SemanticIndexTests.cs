using Swivel;

namespace Swivel.Tests;

/// The semantic half of file search: the tokenizer and model give the same tokens and vectors as the reference
/// (Hugging Face tokenizers + ONNX Runtime 1.19, run once on this model), and files found by meaning respect the
/// request's filters. Skipped when the model hasn't been fetched (windows/ThirdParty/MultilingualE5/fetch.ps1).
[TestClass]
public sealed class SemanticIndexTests
{
    [TestInitialize]
    public void Reset()
    {
        TestSetup.ResetPrefs();
        Prefs.LanguageId = "en";
    }

    static string Folder => System.IO.Path.Combine(TestSetup.SourceRoot, "..", "ThirdParty", "MultilingualE5");

    static readonly (string Text, int[] Ids, float[] Head)[] Reference =
    [
        ("query: 上周保存的领导给的工作内容", [0, 41, 1294, 12, 6, 143865, 40047, 43, 18674, 4766, 19558, 8105, 2], [0.06853f, -0.01479f, -0.00765f, -0.06231f, 0.05713f, -0.01375f, 0.00688f, 0.05964f]),
        ("passage: Lecture 23 – Reductions  Caution • Slides are designed", [0, 46692, 12, 75625, 6644, 1105, 46, 853, 77391, 7, 60570, 1363, 768, 160725, 7, 621, 82775, 2], [0.03768f, -0.02511f, -0.04954f, -0.06462f, 0.06953f, 0.0081f, 0.02605f, 0.01084f]),
        ("Glossy Plate 澳洲市场调研与墨尔本试销方案", [0, 46173, 46048, 22387, 13, 6, 71273, 4362, 119607, 1189, 51069, 5100, 1516, 27584, 29906, 18628, 2], [0.04355f, -0.0293f, -0.03058f, 0.00174f, 0.07556f, -0.02861f, 0.02292f, 0.01505f]),
        ("operating-systems-os-exam-cheat-sheet", [0, 172852, 9, 16751, 7, 9, 232, 9, 3355, 302, 9, 1430, 257, 9, 7816, 126, 2], [0.03509f, -0.05017f, -0.04342f, -0.02019f, 0.06738f, -0.03007f, 0.03664f, 0.04616f]),
        ("ｐｄｆ　ｏｖｅｒ　５ＭＢ 😀 naïve café", [0, 26198, 645, 190, 12689, 21119, 24, 9392, 272, 26216, 2], [0.0321f, -0.01287f, -0.05821f, -0.06261f, 0.07926f, -0.02503f, 0.00913f, 0.08324f]),
        ("スクリーンショット 2026-10-21 190432", [0, 6, 86053, 21711, 7740, 101073, 14670, 387, 4046, 52486, 3117, 96929, 6460, 2], [0.03663f, -0.01639f, -0.03567f, -0.04655f, 0.02532f, -0.03116f, 0.0114f, 0.04503f]),
    ];

    static void RequireModel()
    {
        if (!File.Exists(System.IO.Path.Combine(Folder, "model_quantized.onnx"))) Assert.Inconclusive("model not fetched");
    }

    [TestMethod]
    public void TokensMatchTheReferenceTokenizer()
    {
        RequireModel();
        var tokenizer = UnigramTokenizer.Load(System.IO.Path.Combine(Folder, "tokenizer.json"));
        foreach (var (text, ids, _) in Reference)
            CollectionAssert.AreEqual(ids, tokenizer.Encode(text, 512), text + ": " + string.Join(",", tokenizer.Encode(text, 512)));
        Assert.HasCount(16, tokenizer.Encode(string.Join(" ", Enumerable.Repeat("lecture", 40)), 16));
    }

    [TestMethod]
    public void VectorsMatchTheReferenceAndMeaningCrossesLanguages()
    {
        RequireModel();
        var model = new Embedder(System.IO.Path.Combine(Folder, "model_quantized.onnx"), System.IO.Path.Combine(Folder, "tokenizer.json"));
        foreach (var (text, _, head) in Reference)
        {
            var vector = model.Embed(text, 512);
            Assert.HasCount(384, vector);
            for (int i = 0; i < head.Length; i++) Assert.AreEqual(head[i], vector[i], 0.003, $"{text} [{i}]");
        }
        float similarity(string query, string passage)
        {
            var a = model.Embed("query: " + query, 64);
            var b = model.Embed("passage: " + passage, 256);
            return a.Zip(b, (x, y) => x * y).Sum();
        }
        // A Chinese request is closest to the English document about it.
        var exam = similarity("操作系统复习资料", "operating systems os exam cheat sheet key concepts and algorithms · file");
        var plate = similarity("操作系统复习资料", "Glossy Plate market research and Melbourne trial sales plan");
        Assert.IsTrue(exam > plate + 0.05, $"{exam} vs {plate}");
    }

    [TestMethod]
    public void PassagesAreNameFolderAndText()
    {
        var candidate = new SemanticIndex.Candidate(@"C:\Users\me\Downloads\Q3_sales-plan.pdf", DateTime.UtcNow, DateTime.UtcNow, 10, new string('x', 2000));
        var passage = SemanticIndex.Passage(candidate);
        Assert.StartsWith("Q3 sales plan · Downloads · xxx", passage);
        Assert.IsLessThan(1250, passage.Length);
    }
}
