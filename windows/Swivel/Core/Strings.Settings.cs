namespace Swivel;

static partial class Strings
{
    /// Windows-only strings of the Settings module: English → [简体中文, 繁體中文, 日本語].
    static partial (string English, string[] Translations)[] Settings => [
        ("A bundle of small tools for Windows. Turn on the ones you want — each works on its own.",
            ["一组 Windows 小工具。打开你想用的就好，每个都能单独使用。", "一組 Windows 小工具。打開你想用的就好，每個都能單獨使用。", "Windows の小さなツール集です。使いたいものだけオンにしてください。それぞれ単独で動きます。"]),
        ("A bundle of small tools for Windows. Turn on the ones you want.",
            ["一组 Windows 小工具。打开你想用的就好。", "一組 Windows 小工具。打開你想用的就好。", "Windows の小さなツール集です。使いたいものだけオンにしてください。"]),
        ("You can also switch features on and off from the Swivel icon in the notification area.",
            ["也可以在通知区域的 Swivel 图标菜单里开关各个功能。", "也可以在通知區域的 Swivel 圖示選單裡開關各個功能。", "通知領域の Swivel アイコンからも各機能をオン・オフできます。"]),
        ("Picking a key that another menu uses swaps the two.",
            ["选了另一个菜单正在用的键，两者会互换。", "選了另一個選單正在用的鍵，兩者會互換。", "他のメニューが使っているキーを選ぶと、入れ替わります。"]),
        ("Starts in the notification area when you sign in to Windows.",
            ["登录 Windows 时在通知区域启动。", "登入 Windows 時在通知區域啟動。", "Windows にサインインすると通知領域で起動します。"]),
        ("Short sounds when a menu opens, an option is chosen or a task finishes.",
            ["菜单弹出、选中选项或任务完成时播放简短音效。", "選單彈出、選中選項或工作完成時播放簡短音效。", "メニューが開いたとき、項目を選んだとき、処理が終わったときに短い音を鳴らします。"]),
        ("The language of Swivel's menus and windows.",
            ["Swivel 菜单和窗口使用的语言。", "Swivel 選單和視窗使用的語言。", "Swivel のメニューとウインドウの言語。"]),
        ("1.  Start dragging any file in File Explorer and keep holding the mouse button.\n2.  Tap a key once — the menu opens under the pointer.\n3.  Drop the file on an option. Results are saved next to the original.\nTap the same key again for the next page. Drop on empty space to cancel.",
            ["1.  在文件资源管理器里开始拖动任意文件，鼠标不要松开。\n2.  按一下键，菜单就会在指针下方弹出。\n3.  把文件放到某个选项上，结果保存在原文件旁边。\n再按一次同一个键可以翻页。放到空白处即取消。",
             "1.  在檔案總管裡開始拖曳任意檔案，滑鼠不要放開。\n2.  按一下鍵，選單就會在指標下方彈出。\n3.  把檔案放到某個選項上，結果儲存在原檔案旁邊。\n再按一次同一個鍵可以翻頁。放到空白處即取消。",
             "1.  エクスプローラーでファイルをドラッグし、マウスボタンを押したままにします。\n2.  キーを一度押すと、ポインタの下にメニューが開きます。\n3.  項目の上でドロップします。結果は元のファイルの隣に保存されます。\n同じキーをもう一度押すと次のページ。何もない所でドロップするとキャンセル。"]),
        ("A rope hidden along the top of the screen. Screenshots hang on it instead of piling up in a folder; take one down when you need it.",
            ["一根藏在屏幕顶部的绳子。截图挂在上面，不再堆在文件夹里；要用时取下来就行。", "一條藏在螢幕頂端的繩子。截圖掛在上面，不再堆在資料夾裡；要用時取下來就行。", "画面上部に隠れたロープ。スクリーンショットはフォルダに溜まらずここに掛かり、必要なときに取り外せます。"]),
        ("New screenshots in your Screenshots folder (Print Screen, Win + Print Screen, or the Snipping Tool with automatic saving) appear as a card, ready to hang on the rope. The files stay in the Screenshots folder.",
            ["“屏幕截图”文件夹里的新截图（Print Screen、Win + Print Screen，或开启自动保存的截图工具）会以卡片出现，可以挂到绳子上。文件仍保存在“屏幕截图”文件夹中。",
             "「螢幕擷取畫面」資料夾裡的新截圖（Print Screen、Win + Print Screen，或開啟自動儲存的剪取工具）會以卡片出現，可以掛到繩子上。檔案仍儲存在「螢幕擷取畫面」資料夾中。",
             "「スクリーンショット」フォルダの新しいスクリーンショット（Print Screen、Win + Print Screen、または自動保存をオンにした Snipping Tool）がカードとして表示され、ロープに掛けられます。ファイルは「スクリーンショット」フォルダに残ります。"]),
        ("Also hang images you copy (for example with Win + Shift + S, or Copy in Photos).",
            ["复制的图片也挂上去（例如用 Win + Shift + S 截图，或在“照片”里复制）。", "複製的圖片也掛上去（例如用 Win + Shift + S 截圖，或在「相片」裡複製）。", "コピーした画像も掛けます（Win + Shift + S や「フォト」でのコピーなど）。"]),
        ("Open it in its default app", ["用默认应用打开", "用預設應用程式開啟", "既定のアプリで開く"]),
        ("Press and hold", ["按住", "按住", "長押し"]),
        ("Drag to the Recycle Bin, or click ×", ["拖到回收站，或点 ×", "拖到資源回收筒，或點 ×", "ごみ箱にドラッグ、または × をクリック"]),
        ("Drop files or folders onto the shelf. Use Ctrl-click or Shift-click to select several, then drag them out together. Press Space to preview.",
            ["拖入文件或文件夹，按住 Ctrl 或 Shift 点击多选后一起拖出。按空格可预览。", "拖入檔案或資料夾，按住 Ctrl 或 Shift 點選多個後一起拖出。按空白鍵可預覽。", "ファイルやフォルダをドロップ。Ctrl または Shift を押しながら複数選び、まとめてドラッグ。スペースでプレビュー。"]),
        ("Search enabled tools, choose files and press Enter. The panel also accepts files dragged from File Explorer.",
            ["搜索已开启的工具，选择文件后按回车。也可以直接从文件资源管理器拖入文件。", "搜尋已開啟的工具，選擇檔案後按 Enter。也可以直接從檔案總管拖入檔案。", "有効なツールを検索し、ファイルを選んで Enter。エクスプローラーからのドロップも受け付けます。"]),
        ("If another app uses the same shortcut, choose a different one. You can always reopen Swivel from the Start menu or the notification area to reach Settings.",
            ["如果与其他应用的快捷键冲突，请换一个。始终可以从开始菜单或通知区域重新打开 Swivel 进入设置。", "如果與其他應用程式的快速鍵衝突，請換一個。隨時可從開始功能表或通知區域重新開啟 Swivel 進入設定。", "他のアプリと重なる場合は別のキーを選んでください。スタート メニューや通知領域から Swivel を開き直せば設定に戻れます。"]),
    ];
}
