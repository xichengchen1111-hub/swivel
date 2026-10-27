namespace Swivel;

static partial class Strings
{
    /// Windows-only strings of the core (Catalog, AppHost, Prefs, Features): English → [简体中文, 繁體中文, 日本語].
    static partial (string English, string[] Translations)[] Windows => [
        ("Swivel", ["Swivel", "Swivel", "Swivel"]),
        ("PDF", ["PDF", "PDF", "PDF"]),
        ("English", ["English", "English", "English"]),
        ("Shift", ["Shift", "Shift", "Shift"]),
        ("Ctrl", ["Ctrl", "Ctrl", "Ctrl"]),
        ("Alt", ["Alt", "Alt", "Alt"]),
        ("Tab", ["Tab", "Tab", "Tab"]),
        ("␣ Space", ["␣ 空格", "␣ 空格", "␣ Space"]),
        ("⇥ Tab", ["⇥ Tab", "⇥ Tab", "⇥ Tab"]),
        ("Read QR codes and barcodes from selected images, entirely on your PC.", ["在电脑本地识别所选图片中的二维码与条形码。", "在電腦本機辨識所選圖片中的 QR 碼與條碼。", "選択した画像の QR・バーコードをこの PC 上で読み取ります。"]),
        ("Share", ["共享", "分享", "共有"]),
        ("Opened Share", ["已打开共享", "已開啟分享", "共有を開きました"]),
        ("Choose where to move %@", ["选择要将“%@”移到哪里", "選擇要將「%@」移到哪裡", "「%@」の移動先を選択"]),
        ("Choose where to move %d files", ["选择要将 %d 个文件移到哪里", "選擇要將 %d 個檔案移到哪裡", "%d 個のファイルの移動先を選択"]),
        ("Clothesline: move to the top edge, or press Ctrl + Alt + T", ["晾衣绳：移到屏幕顶部，或按 Ctrl + Alt + T", "曬衣繩：移到螢幕頂端，或按 Ctrl + Alt + T", "ロープ：画面の上端へ移動するか Ctrl + Alt + T"]),
        ("Find an action from the keyboard, even when the taskbar is crowded.", ["用键盘找到操作，任务栏再拥挤也不怕。", "用鍵盤找到操作，工作列再擁擠也不怕。", "タスクバーが混んでいても、キーボードから操作を呼び出せます。"]),
        ("Screenshots hang on a rope along the top of the screen instead of piling up in a folder.", ["截图挂在屏幕顶部的绳子上，不再堆在文件夹里。", "截圖掛在螢幕頂端的繩子上，不再堆在資料夾裡。", "スクリーンショットはフォルダに溜まらず、画面上部のロープに掛かります。"]),
        ("Turn text in an image into editable, copyable text. Everything stays on your PC.", ["把图片里的文字变成可编辑、可复制的文本。全部在你的电脑上完成。", "把圖片裡的文字變成可編輯、可複製的文字。全部在你的電腦上完成。", "画像内の文字を編集・コピーできるテキストに。処理はすべてこの PC 上で行います。"]),
        ("New screenshots (Print Screen, Win + Print Screen, or the Snipping Tool with automatic saving) will appear as a card in the corner, ready to hang on a rope along the top of the screen. Move the pointer to the top edge of the screen to lower it. Screenshot files stay in your Screenshots folder.",
            ["新截图（Print Screen、Win + Print Screen，或开启自动保存的截图工具）会先以卡片出现在屏幕角落，可以挂到屏幕顶部的绳子上。把指针移到屏幕顶部即可放下绳子。截图文件仍保存在“屏幕截图”文件夹中。",
             "新截圖（Print Screen、Win + Print Screen，或開啟自動儲存的剪取工具）會先以卡片出現在螢幕角落，可以掛到螢幕頂端的繩子上。把指標移到螢幕頂端即可放下繩子。截圖檔案仍儲存在「螢幕擷取畫面」資料夾中。",
             "新しいスクリーンショット（Print Screen、Win + Print Screen、または自動保存をオンにした Snipping Tool）は、まず画面の隅にカードとして表示され、画面上部のロープに掛けられます。ポインタを画面の上端へ動かすとロープが下ります。ファイルは「スクリーンショット」フォルダに残ります。"]),
    ];
}
