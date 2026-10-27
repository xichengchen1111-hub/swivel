namespace Swivel;

static partial class Strings
{
    /// Windows-only strings of the Wheel module: English → [简体中文, 繁體中文, 日本語].
    static partial (string English, string[] Translations)[] Wheel => [
        // The keyboard tool wheel (1.4): Windows says Enter where the Mac says Return.
        ("Click or Enter", ["点击或回车", "點選或 Enter", "クリックまたは Enter"]),
        ("Click or use arrows · Enter · Space for next page",
            ["点击或方向键选择 · 回车确认 · 空格翻页", "點選或方向鍵選取 · Enter 確認 · 空格換頁", "クリックまたは矢印で選択・Enter で実行・Space で次のページ"]),
    ];
}
