namespace Swivel;

static partial class Strings
{
    /// Windows-only strings of App Actions and the tool wheel: English → [简体中文, 繁體中文, 日本語].
    static partial (string English, string[] Translations)[] Apps => [
        ("Open files starts the app with the selected files. Copy files and open app prepares attachments for you to paste. This only changes Swivel’s actions; your default apps stay the same.",
            ["“打开文件”会带着所选文件启动应用；“复制文件并打开应用”供你粘贴附件。这里只设置 Swivel 的动作，不更改默认应用。",
             "「開啟檔案」會帶著所選檔案啟動 App；「複製檔案並開啟 App」供你貼上附件。此處僅設定 Swivel 動作，不更改預設 App。",
             "「ファイルを開く」は選択したファイルを渡してアプリを起動します。「コピーして開く」は貼り付け用です。既定のアプリは変更しません。"]),
        ("No app on this PC opens %@ links. Install %@, then try again.",
            ["此电脑上没有能打开 %@ 链接的应用。请安装 %@ 后重试。", "此電腦上沒有能開啟 %@ 連結的 App。請安裝 %@ 後再試一次。", "この PC には %@ リンクを開けるアプリがありません。%@ をインストールしてからもう一度お試しください。"]),
        ("Too many files for one app launch. Choose fewer files.",
            ["文件过多，无法一次交给应用，请减少所选文件。", "檔案過多，無法一次交給 App，請減少所選檔案。", "一度にアプリへ渡すにはファイルが多すぎます。選択を減らしてください。"]),
        ("Could not open %@.", ["无法打开 %@。", "無法開啟 %@。", "%@ を開けませんでした。"]),
        ("Applications", ["应用程序", "應用程式", "アプリケーション"]),
    ];
}
