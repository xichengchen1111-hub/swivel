# multilingual-e5-small (ONNX, int8) — Windows semantic file search

Used only by Quick Actions' file search on Windows (`Tools/SemanticIndex.cs`) to turn a request and each indexed
file's text into vectors, on the PC. Approved by the owner on 2026-10-27 (“都帮我做了吧” for the semantic step, then
an explicit yes to downloading these files). Nothing is downloaded at run time; no file content leaves the PC.

| File | Bytes | SHA-256 |
| --- | --- | --- |
| `model_quantized.onnx` | 118308185 | `f80102d3f2a1229f387d3c81909990d8945513e347b0eab049f7de3c6f98c193` |
| `tokenizer.json` | 17082730 | `0b44a9d7b51c3c62626640cda0e2c2f70fdacdc25bbbd68038369d14ebdf4c39` |

- Source: https://huggingface.co/Xenova/multilingual-e5-small at commit `761b726dd34fb83930e26aab4e9ac3899aa1fa78`
  (`onnx/model_quantized.onnx`, `tokenizer.json`; `config.json` and `tokenizer_config.json` are kept for reference).
  It is the ONNX export of https://huggingface.co/intfloat/multilingual-e5-small (commit
  `614241f622f53c4eeff9890bdc4f31cfecc418b3`), whose model card declares the MIT licence (see `LICENSE`).
- The two large files are not in git (`.gitignore`); `fetch.ps1` downloads them from that pinned commit and refuses
  any file whose hash differs. The app checks the same hashes before loading (`SemanticIndex.ModelFiles`).
- Runtime: Windows' own `Windows.AI.MachineLearning` (ONNX Runtime inside Windows 10/11); no NuGet package.
- Changed: nothing. The tokenizer (XLM-R SentencePiece unigram) is reimplemented in C# from `tokenizer.json`.
- Paper: Wang et al., “Multilingual E5 Text Embeddings: A Technical Report”, 2024.
