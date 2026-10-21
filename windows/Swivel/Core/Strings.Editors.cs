namespace Swivel;

static partial class Strings
{
    /// Windows-only strings of the Editors module: English → [简体中文, 繁體中文, 日本語].
    static partial (string English, string[] Translations)[] Editors => [
        ("W", ["宽", "寬", "幅"]),
        ("H", ["高", "高", "高さ"]),
        ("%d bytes", ["%d 字节", "%d 位元組", "%d バイト"]),
        ("Colors", ["颜色", "顏色", "カラー"]),
        ("More Colors…", ["更多颜色…", "更多顏色…", "その他の色…"]),
        ("Play", ["播放", "播放", "再生"]),
        ("Pause", ["暂停", "暫停", "一時停止"]),
        ("Couldn't crop", ["无法裁剪", "無法裁切", "切り取れませんでした"]),
        ("Couldn't save", ["无法保存", "無法儲存", "保存できませんでした"]),
        ("Can't write this format", ["无法写入这种格式", "無法寫入這種格式", "この形式では書き出せません"]),
        ("Can't trim this file", ["无法剪辑这个文件", "無法修剪這個檔案", "このファイルはトリミングできません"]),
        ("This PDF has no pages", ["这个 PDF 没有页面", "這個 PDF 沒有頁面", "この PDF にはページがありません"]),
        ("This file has no duration", ["这个文件没有时长", "這個檔案沒有時間長度", "このファイルには長さがありません"]),
        ("Preview isn't available for this file.", ["这个文件无法预览。", "這個檔案無法預覽。", "このファイルはプレビューできません。"]),
        ("Face detection isn't available on this PC.", ["这台电脑无法检测人脸。", "這台電腦無法偵測人臉。", "この PC では顔を検出できません。"]),
        ("Text recognition isn't available on this PC.", ["这台电脑无法识别文字。", "這台電腦無法辨識文字。", "この PC では文字を認識できません。"]),
    ];
}
