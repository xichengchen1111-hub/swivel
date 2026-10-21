namespace Swivel;

static partial class Strings
{
    /// Windows-only strings of the Clothesline module: English → [简体中文, 繁體中文, 日本語].
    static partial (string English, string[] Translations)[] Clothesline => [
        ("Click: copy · Double-click: open · Hold: preview · Drag: take it down",
            ["单击：复制 · 双击：打开 · 长按：预览 · 拖动：取下", "按一下：複製 · 按兩下：開啟 · 長按：預覽 · 拖曳：取下", "クリック：コピー · ダブルクリック：開く · 長押し：プレビュー · ドラッグ：取り外す"]),
        ("Show in File Explorer", ["在文件资源管理器中显示", "在檔案總管中顯示", "エクスプローラーで表示"]),
        ("Move to Recycle Bin", ["移到回收站", "移到資源回收筒", "ごみ箱に移動"]),
        ("Remove from Clothesline", ["从晾衣绳上取下", "從曬衣繩上取下", "物干しロープから外す"]),
    ];
}
