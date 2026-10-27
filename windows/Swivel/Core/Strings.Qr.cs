namespace Swivel;

static partial class Strings
{
    /// Windows-only strings of QR & Barcodes and the QR Library: English → [简体中文, 繁體中文, 日本語].
    static partial (string English, string[] Translations)[] Qr => [
        // qr14: the scan window and the library (Mac wording with "PC" for "Mac").
        ("Read QR codes and barcodes from images on your PC. Review the decoded content before copying or opening a link.", ["在电脑本地读取图片中的二维码和条形码，检查内容后再复制或打开链接。", "在電腦本機讀取圖片中的 QR 碼與條碼，檢查內容後再複製或開啟連結。", "PC 上の画像からコードを読み取ります。内容を確認してからコピーやリンクの表示を行えます。"]),
        ("%d saved codes · stored on this PC", ["已保存 %d 个二维码 · 存储在本机", "已儲存 %d 個 QR 碼 · 存放於本機", "%d 個のコードをこの PC に保存"]),
        ("Enter a URL or text. Saved codes stay on this PC until you remove them.", ["输入网址或文字，保存的二维码会保留在本机，直到你删除。", "輸入網址或文字，儲存的 QR 碼會保留於本機，直到你移除。", "URL やテキストを入力してください。保存したコードは削除するまでこの PC に残ります。"]),
    ];
}
