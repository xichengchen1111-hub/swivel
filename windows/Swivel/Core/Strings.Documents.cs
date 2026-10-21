namespace Swivel;

static partial class Strings
{
    /// Windows-only strings of the Documents & PDF module: English → [简体中文, 繁體中文, 日本語].
    static partial (string English, string[] Translations)[] Documents => [
        ("Couldn't open %@", ["无法打开“%@”", "無法打開「%@」", "「%@」を開けませんでした"]),
        ("%@ is password protected", ["“%@”受密码保护", "「%@」受密碼保護", "「%@」はパスワードで保護されています"]),
        ("Couldn't read this document", ["无法读取这个文稿", "無法讀取這份文件", "この書類を読み込めませんでした"]),
        ("Couldn't write %@", ["无法写入“%@”", "無法寫入「%@」", "「%@」を書き込めませんでした"]),
        ("Couldn't render page %d", ["无法渲染第 %d 页", "無法渲染第 %d 頁", "%d ページ目を描画できませんでした"]),
        ("No text found in this PDF", ["没有在这个 PDF 中找到文字", "沒有在這個 PDF 中找到文字", "この PDF にテキストが見つかりませんでした"]),
        ("This PDF has no pages", ["这个 PDF 没有页面", "這個 PDF 沒有頁面", "この PDF にはページがありません"]),
        ("%@: %d pages", ["%@：%d 页", "%@：%d 頁", "%@：%d ページ"]),
        ("%@: 1 page", ["%@：1 页", "%@：1 頁", "%@：1 ページ"]),
        ("%@: %d words, %d characters", ["%@：%d 个字词，%d 个字符", "%@：%d 個字詞，%d 個字元", "%@：%d 語、%d 文字"]),
        ("%@ is already as small as it can be", ["“%@”已经无法再压缩", "「%@」已經無法再壓縮", "「%@」はこれ以上小さくできません"]),
    ];
}
