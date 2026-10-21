namespace Swivel;

static partial class Strings
{
    /// Windows-only strings of the Tools module: English → [简体中文, 繁體中文, 日本語].
    static partial (string English, string[] Translations)[] Tools => [
        ("Preview", ["预览", "預覽", "プレビュー"]),
        ("Open", ["打开", "打開", "開く"]),
        ("Show in File Explorer", ["在文件资源管理器中显示", "在檔案總管中顯示", "エクスプローラーで表示"]),
        ("Add Folder…", ["添加文件夹…", "加入資料夾…", "フォルダを追加…"]),
        ("%d removed · Ctrl+Z to undo", ["已移除 %d 项 · 按 Ctrl+Z 撤销", "已移除 %d 項 · 按 Ctrl+Z 還原", "%d 件を取り除きました · Ctrl+Z で取り消す"]),
        ("%d of %d", ["第 %d 个，共 %d 个", "第 %d 個，共 %d 個", "%d / %d"]),
        ("All files", ["所有文件", "所有檔案", "すべてのファイル"]),
        ("Press Ctrl+Enter to copy while editing the text.", ["编辑文字时，按 Ctrl+Enter 复制。", "編輯文字時，按 Ctrl+Enter 複製。", "テキストの編集中は Ctrl+Enter でコピーできます。"]),
        ("This image format is not available on this PC.", ["此电脑不支持输出这种图片格式。", "此電腦不支援輸出這種圖片格式。", "この PC ではこの画像形式で保存できません。"]),
        ("Text recognition needs an OCR language. Add English, Chinese or Japanese in Settings › Time & language › Language & region, then try again.",
            ["文字识别需要 OCR 语言。请在“设置 › 时间和语言 › 语言和区域”中添加英语、中文或日语，然后重试。",
             "文字辨識需要 OCR 語言。請在「設定 › 時間與語言 › 語言與地區」中新增英文、中文或日文，然後再試一次。",
             "文字認識には OCR 言語が必要です。「設定 › 時刻と言語 › 言語と地域」で英語、中国語、または日本語を追加してから、もう一度お試しください。"]),
    ];
}
