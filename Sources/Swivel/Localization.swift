import Foundation

/// The app's display language, chosen in General. "System" follows macOS's preferred languages.
enum Language: String, CaseIterable {
    case system, en, zhHans = "zh-Hans", zhHant = "zh-Hant", ja

    var title: String {
        switch self {
        case .system: L("System Default")
        case .en: "English"
        case .zhHans: "简体中文"
        case .zhHant: "繁體中文"
        case .ja: "日本語"
        }
    }

    static var chosen: Language {
        Language(rawValue: UserDefaults.standard.string(forKey: "language") ?? "") ?? .system
    }

    /// The language actually used for text.
    static var resolved: Language {
        let choice = chosen
        guard choice == .system else { return choice }
        for id in Locale.preferredLanguages {
            if id.hasPrefix("zh-Hant") || id.hasPrefix("zh-TW") || id.hasPrefix("zh-HK") || id.hasPrefix("zh-MO") { return .zhHant }
            if id.hasPrefix("zh") { return .zhHans }
            if id.hasPrefix("ja") { return .ja }
            if id.hasPrefix("en") { return .en }
        }
        return .en
    }

    /// Column in `Strings.table`, or nil for English.
    var column: Int? {
        switch self {
        case .zhHans: 0
        case .zhHant: 1
        case .ja: 2
        default: nil
        }
    }
}

/// Translates an English UI string. Unknown strings (and English) come back unchanged.
func L(_ english: String) -> String {
    guard let column = Language.resolved.column, let row = Strings.table[english] else { return english }
    return row[column]
}

/// Translates a format string, then fills in the arguments.
func L(_ english: String, _ args: CVarArg...) -> String {
    String(format: L(english), arguments: args)
}

/// English → [简体中文, 繁體中文, 日本語].
enum Strings {
    static let table: [String: [String]] = [
        // Language & general
        "System Default": ["跟随系统", "跟隨系統", "システムに従う"],
        "Language": ["语言", "語言", "言語"],
        "Overview": ["总览", "總覽", "概要"],
        "General": ["通用", "一般", "一般"],
        "Startup": ["启动", "啟動", "起動"],
        "Open Swivel at login": ["登录时打开 Swivel", "登入時開啟 Swivel", "ログイン時に Swivel を開く"],
        "A bundle of small Mac tools. Turn on the ones you want — each works on its own.":
            ["一组 Mac 小工具。打开你想用的就好，每个都能单独使用。", "一組 Mac 小工具。打開你想用的就好，每個都能單獨使用。", "Mac の小さなツール集です。使いたいものだけオンにしてください。それぞれ単独で動きます。"],
        "A bundle of small Mac tools. Turn on the ones you want.":
            ["一组 Mac 小工具。打开你想用的就好。", "一組 Mac 小工具。打開你想用的就好。", "Mac の小さなツール集です。使いたいものだけオンにしてください。"],
        "You can also switch features on and off from the ◎ menu in the menu bar.":
            ["也可以在菜单栏的 ◎ 菜单里开关各个功能。", "也可以在選單列的 ◎ 選單裡開關各個功能。", "メニューバーの ◎ メニューからも各機能をオン・オフできます。"],
        "Settings…": ["设置…", "設定…", "設定…"],
        "On": ["开", "開", "オン"],
        "Off": ["关", "關", "オフ"],
        "Show Shelf": ["显示收纳架", "顯示收納架", "シェルフを表示"],
        "Show Clothesline": ["显示晾衣绳", "顯示曬衣繩", "物干しロープを表示"],
        "Swivel — %@": ["Swivel — %@", "Swivel — %@", "Swivel — %@"],

        // Features
        "Wheel": ["轮盘", "輪盤", "ホイール"],
        "Clothesline": ["晾衣绳", "曬衣繩", "物干しロープ"],
        "Drag a file and tap a key: a menu pops up to convert it, edit it or send it somewhere.":
            ["拖住文件按一下键，弹出菜单，可以转换、编辑或发送文件。", "拖住檔案按一下鍵，彈出選單，可以轉換、編輯或傳送檔案。", "ファイルをドラッグしてキーを押すと、変換・編集・送信のメニューが開きます。"],
        "Screenshots hang on a rope under the menu bar instead of piling up on the desktop.":
            ["截图挂在菜单栏下方的绳子上，不再堆满桌面。", "截圖掛在選單列下方的繩子上，不再堆滿桌面。", "スクリーンショットはデスクトップに溜まらず、メニューバーの下のロープに掛かります。"],

        // Wheel: how it works
        "Wheel — drag a file, keep holding, then tap a key:":
            ["轮盘 — 拖住文件不松手，再按一下键：", "輪盤 — 拖住檔案不放，再按一下鍵：", "ホイール — ファイルをドラッグしたまま、キーを押す："],
        """
        1.  Start dragging any file in Finder and keep holding the mouse button.
        2.  Tap a key once — the menu opens under the pointer.
        3.  Drop the file on an option. Results are saved next to the original.
        Tap the same key again for the next page. Drop on empty space to cancel.
        """: ["""
        1.  在访达里开始拖动任意文件，鼠标不要松开。
        2.  按一下键，菜单就会在指针下方弹出。
        3.  把文件放到某个选项上，结果保存在原文件旁边。
        再按一次同一个键可以翻页。放到空白处即取消。
        """, """
        1.  在 Finder 裡開始拖曳任意檔案，滑鼠不要放開。
        2.  按一下鍵，選單就會在指標下方彈出。
        3.  把檔案放到某個選項上，結果儲存在原檔案旁邊。
        再按一次同一個鍵可以翻頁。放到空白處即取消。
        """, """
        1.  Finder でファイルをドラッグし、マウスボタンを押したままにします。
        2.  キーを一度押すと、ポインタの下にメニューが開きます。
        3.  項目の上でドロップします。結果は元のファイルの隣に保存されます。
        同じキーをもう一度押すと次のページ。何もない所でドロップするとキャンセル。
        """],
        "Tap the same key again for more options": ["再按一次同一个键查看更多选项", "再按一次同一個鍵查看更多選項", "同じキーをもう一度押すと他の項目"],
        "Convert": ["转换", "轉換", "変換"],
        "Tools": ["工具", "工具", "ツール"],
        "Actions": ["操作", "操作", "操作"],
        "Convert formats": ["转换格式", "轉換格式", "形式を変換"],
        "File tools": ["文件工具", "檔案工具", "ファイルツール"],
        "File actions": ["文件操作", "檔案操作", "ファイル操作"],
        "drop to apply": ["松手即应用", "放開即套用", "ドロップで実行"],
        "%@ next": ["%@ 下一页", "%@ 下一頁", "%@ 次へ"],
        "Menu Style: %@…": ["菜单样式：%@…", "選單樣式：%@…", "メニューのスタイル：%@…"],
        "Customize…": ["自定义…", "自訂…", "カスタマイズ…"],
        "Any file or folder": ["任意文件或文件夹", "任意檔案或資料夾", "すべてのファイルとフォルダ"],

        // Shortcuts
        "Shortcuts": ["快捷键", "快速鍵", "ショートカット"],
        "Choose which key opens each menu. Start dragging first, then tap the key.":
            ["选择用哪个键打开每个菜单。先开始拖动，再按键。", "選擇用哪個鍵開啟每個選單。先開始拖曳，再按鍵。", "各メニューを開くキーを選びます。先にドラッグを始めてからキーを押します。"],
        """
        Picking a key that another menu uses swaps the two.
        For fn / 🌐: System Settings → Keyboard →
        “Press 🌐 key to” → “Do Nothing”.
        """: ["""
        选了另一个菜单正在用的键，两者会互换。
        使用 fn / 🌐 键：系统设置 → 键盘 →
        “按下 🌐 键时” → “不执行任何操作”。
        """, """
        選了另一個選單正在用的鍵，兩者會互換。
        使用 fn / 🌐 鍵：系統設定 → 鍵盤 →
        「按下 🌐 鍵時」→「不執行任何動作」。
        """, """
        他のメニューが使っているキーを選ぶと、入れ替わります。
        fn / 🌐 を使う場合：システム設定 → キーボード →
        「🌐 キーを押して」→「何もしない」。
        """],
        "Restore Defaults": ["恢复默认", "回復預設值", "デフォルトに戻す"],
        "⇧ Shift": ["⇧ Shift", "⇧ Shift", "⇧ Shift"],

        // Style
        "Style": ["样式", "樣式", "スタイル"],
        "Choose how the menu looks when it pops up. Click a template to use it.":
            ["选择菜单弹出时的样子，点一下模板就会使用。", "選擇選單彈出時的樣子，點一下樣板就會使用。", "メニューの見た目を選びます。テンプレートをクリックすると使用します。"],
        "List": ["列表", "列表", "リスト"],
        "Bookshelf": ["书架", "書架", "本棚"],
        "Sticky Notes": ["便利贴", "便利貼", "付箋"],
        "A ring of keycaps around the cursor.": ["指针周围的一圈按键。", "指標周圍的一圈按鍵。", "カーソルを囲むキーの輪。"],
        "A tidy column, like a context menu.": ["整齐的一列，像右键菜单。", "整齊的一列，像右鍵選單。", "コンテキストメニューのような一列。"],
        "Book spines standing on a wooden shelf.": ["立在木架上的书脊。", "立在木架上的書背。", "木の棚に並ぶ本の背表紙。"],
        "Colourful sticky notes pinned in a grid.": ["排成网格的彩色便利贴。", "排成網格的彩色便利貼。", "格子状に貼られたカラフルな付箋。"],
        "Tags pegged on a rope, swaying gently.": ["夹在绳上轻轻摇晃的吊牌。", "夾在繩上輕輕搖晃的吊牌。", "ロープに留められ、そっと揺れるタグ。"],
        "Use the %@ template": ["使用“%@”模板", "使用「%@」樣板", "「%@」テンプレートを使う"],

        // Customize
        "Customize": ["自定义", "自訂", "カスタマイズ"],
        "Pick which options each menu shows and in what order. Untick to hide, drag to reorder.":
            ["选择每个菜单显示哪些选项、按什么顺序。取消勾选即隐藏，拖动可排序。", "選擇每個選單顯示哪些選項、依什麼順序。取消勾選即隱藏，拖曳可排序。", "各メニューに表示する項目と順番を選びます。チェックを外すと非表示、ドラッグで並べ替え。"],
        "Menu:": ["菜单：", "選單：", "メニュー："],
        "File type:": ["文件类型：", "檔案類型：", "ファイルの種類："],
        "Images": ["图片", "圖片", "画像"],
        "Video": ["视频", "影片", "ビデオ"],
        "Audio": ["音频", "音訊", "オーディオ"],
        "Documents": ["文档", "文件", "書類"],
        "Archives": ["压缩包", "壓縮檔", "アーカイブ"],
        "Folders": ["文件夹", "資料夾", "フォルダ"],
        "Other files": ["其他文件", "其他檔案", "その他のファイル"],

        // Clothesline settings
        "A rope hidden under the menu bar. Screenshots hang on it instead of piling up on the desktop; take one down when you need it.":
            ["一根藏在菜单栏下的绳子。截图挂在上面，不再堆满桌面；要用时取下来就行。", "一條藏在選單列下的繩子。截圖掛在上面，不再堆滿桌面；要用時取下來就行。", "メニューバーの下に隠れたロープ。スクリーンショットはデスクトップに溜まらずここに掛かり、必要なときに取り外せます。"],
        "Screenshots": ["截图", "截圖", "スクリーンショット"],
        "Hang new screenshots and screen recordings on the rope": ["把新截图和录屏挂到绳上", "把新截圖和螢幕錄影掛到繩上", "新しいスクリーンショットと画面収録をロープに掛ける"],
        "Turns off the floating thumbnail and saves screenshots to the rope's folder. Your own settings come back when this is off or Swivel quits.":
            ["会关闭浮动缩略图，并把截图存到绳子的文件夹。关闭此项或退出 Swivel 时恢复你原来的设置。", "會關閉浮動縮圖，並把截圖存到繩子的資料夾。關閉此項或結束 Swivel 時回復你原本的設定。", "フローティングサムネールをオフにし、スクリーンショットをロープのフォルダに保存します。オフにするか Swivel を終了すると元の設定に戻ります。"],
        "Clipboard": ["剪贴板", "剪貼簿", "クリップボード"],
        "Also hang images you copy (e.g. ⌃ screenshots, Copy in Preview)": ["复制的图片也挂上去（如按住 ⌃ 截图、在“预览”里拷贝）", "拷貝的圖片也掛上去（如按住 ⌃ 截圖、在「預覽程式」裡拷貝）", "コピーした画像も掛ける（⌃ を押したスクリーンショット、プレビューでのコピーなど）"],
        "Gestures": ["手势", "手勢", "ジェスチャ"],
        "Click": ["单击", "按一下", "クリック"],
        "Copy the image": ["拷贝图片", "拷貝圖片", "画像をコピー"],
        "Double-click": ["双击", "按兩下", "ダブルクリック"],
        "Open in Preview (QuickTime for recordings)": ["用“预览”打开（录屏用 QuickTime）", "用「預覽程式」開啟（錄影用 QuickTime）", "プレビューで開く（収録は QuickTime）"],
        "Hold or force click": ["长按或用力点按", "長按或用力按", "長押しまたは強めのクリック"],
        "Quick Look": ["快速查看", "快速查看", "クイックルック"],
        "Drag into an app": ["拖进 App", "拖進 App", "アプリにドラッグ"],
        "Send a copy — the original stays on the rope": ["送一份副本，原图留在绳上", "送一份副本，原圖留在繩上", "コピーを送る（元はロープに残る）"],
        "Drag into a folder": ["拖进文件夹", "拖進資料夾", "フォルダにドラッグ"],
        "Move it there — it comes off the rope": ["移到那里，从绳上取下", "移到那裡，從繩上取下", "そこへ移動（ロープから外れる）"],
        "Drag to the Trash, or click ×": ["拖到废纸篓，或点 ×", "拖到垃圾桶，或點 ×", "ゴミ箱にドラッグ、または × をクリック"],
        "Throw it away": ["丢掉", "丟掉", "捨てる"],
        "Rest on the menu bar": ["指针停在菜单栏", "指標停在選單列", "メニューバーにポインタを置く"],
        "Lower the rope": ["放下绳子", "放下繩子", "ロープを下ろす"],
        "Click the menu bar": ["点击菜单栏", "點按選單列", "メニューバーをクリック"],
        "Put the rope away": ["收起绳子", "收起繩子", "ロープをしまう"],
        "Show or hide the rope": ["显示或隐藏绳子", "顯示或隱藏繩子", "ロープの表示・非表示"],
        "Show Rope": ["显示绳子", "顯示繩子", "ロープを表示"],
        "Open Folder": ["打开文件夹", "打開資料夾", "フォルダを開く"],
        "Hang New Screenshots": ["挂上新截图", "掛上新截圖", "新しいスクリーンショットを掛ける"],
        "Hang Copied Images": ["挂上复制的图片", "掛上拷貝的圖片", "コピーした画像を掛ける"],
        "Hang your screenshots on the clothesline?": ["要把截图挂到晾衣绳上吗？", "要把截圖掛到曬衣繩上嗎？", "スクリーンショットを物干しロープに掛けますか？"],
        "New screenshots will skip the desktop and hang on a rope under the menu bar. Rest the pointer on the menu bar to lower it. The floating thumbnail is turned off while this is on; your own settings come back if you turn it off or quit Swivel.":
            ["新截图不再放到桌面，而是挂在菜单栏下的绳子上。指针停在菜单栏就能放下绳子。开启期间会关闭浮动缩略图；关闭此功能或退出 Swivel 时会恢复你原来的设置。", "新截圖不再放到桌面，而是掛在選單列下的繩子上。指標停在選單列就能放下繩子。開啟期間會關閉浮動縮圖；關閉此功能或結束 Swivel 時會回復你原本的設定。", "新しいスクリーンショットはデスクトップに置かれず、メニューバーの下のロープに掛かります。メニューバーにポインタを置くとロープが下ります。オンの間はフローティングサムネールがオフになり、オフにするか Swivel を終了すると元の設定に戻ります。"],
        "Take Over Screenshots": ["接管截图", "接管截圖", "スクリーンショットを引き継ぐ"],
        "Not Now": ["以后再说", "以後再說", "今はしない"],
        "Click: copy · Double-click: open · Hold: Quick Look · Drag: take it down":
            ["单击：拷贝 · 双击：打开 · 长按：快速查看 · 拖动：取下", "按一下：拷貝 · 按兩下：開啟 · 長按：快速查看 · 拖曳：取下", "クリック：コピー · ダブルクリック：開く · 長押し：クイックルック · ドラッグ：取り外す"],
        "Hung on the clothesline": ["已挂到晾衣绳上", "已掛到曬衣繩上", "物干しロープに掛けました"],
        "Hung %d images on the clothesline": ["已把 %d 张图挂到晾衣绳上", "已把 %d 張圖掛到曬衣繩上", "%d 枚の画像を物干しロープに掛けました"],

        // Menu bar
        "Open Swivel…": ["打开 Swivel…", "打開 Swivel…", "Swivel を開く…"],
        "Open at Login": ["登录时打开", "登入時開啟", "ログイン時に開く"],
        "Quit Swivel": ["退出 Swivel", "結束 Swivel", "Swivel を終了"],
        "About Swivel": ["关于 Swivel", "關於 Swivel", "Swivel について"],
        "Hide Swivel": ["隐藏 Swivel", "隱藏 Swivel", "Swivel を隠す"],
        "Edit": ["编辑", "編輯", "編集"],
        "Window": ["窗口", "視窗", "ウインドウ"],
        "Undo": ["撤销", "還原", "取り消す"],
        "Redo": ["重做", "重做", "やり直す"],
        "Cut": ["剪切", "剪下", "カット"],
        "Copy": ["拷贝", "拷貝", "コピー"],
        "Paste": ["粘贴", "貼上", "ペースト"],
        "Select All": ["全选", "全選", "すべてを選択"],
        "Close": ["关闭", "關閉", "閉じる"],
        "Minimize": ["最小化", "縮到最小", "しまう"],
        "Swivel — everything is off. Open this menu to turn a feature on.":
            ["Swivel — 所有功能都已关闭。打开此菜单来开启功能。", "Swivel — 所有功能都已關閉。打開此選單來開啟功能。", "Swivel — すべてオフです。このメニューから機能をオンにしてください。"],
        "Wheel: drag a file, then tap %@": ["轮盘：拖住文件，再按 %@", "輪盤：拖住檔案，再按 %@", "ホイール：ファイルをドラッグして %@ を押す"],
        "Clothesline: rest the pointer on the menu bar, or press ⌃⌥T": ["晾衣绳：指针停在菜单栏，或按 ⌃⌥T", "曬衣繩：指標停在選單列，或按 ⌃⌥T", "物干しロープ：メニューバーにポインタを置くか ⌃⌥T"],
        "%@ is on": ["%@ 已开启", "%@ 已開啟", "%@ をオンにしました"],
        "%@ is off": ["%@ 已关闭", "%@ 已關閉", "%@ をオフにしました"],
        "Swivel is running — drag a file, then tap %@": ["Swivel 正在运行 — 拖住文件，再按 %@", "Swivel 正在執行 — 拖住檔案，再按 %@", "Swivel 実行中 — ファイルをドラッグして %@ を押す"],

        // Wheel items
        "Crop": ["裁剪", "裁切", "切り取り"],
        "Compress": ["压缩", "壓縮", "圧縮"],
        "Add BG": ["加背景", "加背景", "背景を追加"],
        "Redact": ["打码", "遮蔽", "墨消し"],
        "Metadata": ["元数据", "後設資料", "メタデータ"],
        "Annotate": ["标注", "標註", "注釈"],
        "Resize 50%": ["缩小 50%", "縮小 50%", "50% に縮小"],
        "Rotate": ["旋转", "旋轉", "回転"],
        "Flip": ["翻转", "翻轉", "反転"],
        "Grayscale": ["黑白", "黑白", "グレースケール"],
        "Make PDF": ["生成 PDF", "製作 PDF", "PDF を作成"],
        "Collage": ["拼图", "拼貼", "コラージュ"],
        "Trim": ["剪辑", "修剪", "トリム"],
        "Merge": ["合并", "合併", "結合"],
        "Mute": ["静音", "靜音", "消音"],
        "Extract Audio": ["提取音频", "擷取音訊", "オーディオを抽出"],
        "Frame": ["截一帧", "擷取畫格", "フレーム"],
        "PNG Frame": ["PNG 画面", "PNG 畫面", "PNG フレーム"],
        "Mono": ["单声道", "單聲道", "モノラル"],
        "Voice Memo": ["语音备忘录", "語音備忘錄", "ボイスメモ"],
        "Word Count": ["字数统计", "字數統計", "単語数"],
        "Split Pages": ["拆分页面", "拆分頁面", "ページを分割"],
        "Page Count": ["页数", "頁數", "ページ数"],
        "Extract": ["解压", "解壓縮", "展開"],
        "AirDrop": ["隔空投送", "AirDrop", "AirDrop"],
        "Float": ["浮动", "浮動", "フロート"],
        "Move": ["移动", "移動", "移動"],
        "Copy Path": ["拷贝路径", "拷貝路徑", "パスをコピー"],
        "Terminal": ["终端", "終端機", "ターミナル"],
        "Hang": ["挂上绳", "掛上繩", "ロープに掛ける"],

        // Toasts and results
        "Done": ["完成", "完成", "完了"],
        "Show": ["显示", "顯示", "表示"],
        "Drag up to hang": ["往上拖，挂到绳上", "往上拖，掛到繩上", "上にドラッグして掛ける"],
        "Saved %@": ["已保存 %@", "已儲存 %@", "%@ を保存しました"],
        "Saved %d files": ["已保存 %d 个文件", "已儲存 %d 個檔案", "%d 個のファイルを保存しました"],
        "Copied %@": ["已拷贝 %@", "已拷貝 %@", "%@ をコピーしました"],
        "Copied %d files": ["已拷贝 %d 个文件", "已拷貝 %d 個檔案", "%d 個のファイルをコピーしました"],
        "Copied path": ["已拷贝路径", "已拷貝路徑", "パスをコピーしました"],
        "Copied %d paths": ["已拷贝 %d 个路径", "已拷貝 %d 個路徑", "%d 個のパスをコピーしました"],
        "Moved %@": ["已移动 %@", "已移動 %@", "%@ を移動しました"],
        "Moved %d files": ["已移动 %d 个文件", "已移動 %d 個檔案", "%d 個のファイルを移動しました"],
        "Move cancelled": ["已取消移动", "已取消移動", "移動をキャンセルしました"],
        "Move Here": ["移到这里", "移到這裡", "ここに移動"],
        "Opened AirDrop": ["已打开隔空投送", "已打開 AirDrop", "AirDrop を開きました"],
        "Added to Shelf": ["已放进收纳架", "已放進收納架", "シェルフに追加しました"],
        "Opened in Terminal": ["已在终端中打开", "已在終端機中開啟", "ターミナルで開きました"],
        "Cancelled": ["已取消", "已取消", "キャンセルしました"],
        "Nothing to show for this file": ["这个文件没有可用的选项", "這個檔案沒有可用的選項", "このファイルに使える項目はありません"],
        "Swivel is on": ["Swivel 已开启", "Swivel 已開啟", "Swivel をオンにしました"],

        // Editors
        "Apply": ["应用", "套用", "適用"],
        "Cancel": ["取消", "取消", "キャンセル"],
        "Reset": ["重置", "重設", "リセット"],
        "Crop Image": ["裁剪图片", "裁切圖片", "画像を切り取る"],
        "Crop PDF": ["裁剪 PDF", "裁切 PDF", "PDF を切り取る"],
        "Aspect ratio": ["比例", "比例", "縦横比"],
        "Free": ["自由", "自由", "自由"],
        "Size": ["尺寸", "尺寸", "サイズ"],
        "Compress Image": ["压缩图片", "壓縮圖片", "画像を圧縮"],
        "Quality": ["质量", "品質", "画質"],
        "Dimensions": ["尺寸", "尺寸", "サイズ"],
        "Format": ["格式", "格式", "フォーマット"],
        "Result": ["结果", "結果", "結果"],
        "Original size": ["原始尺寸", "原始尺寸", "元のサイズ"],
        "Max %d px": ["最大 %d 像素", "最大 %d 像素", "最大 %d px"],
        "Same (%@)": ["保持原格式（%@）", "維持原格式（%@）", "同じ（%@）"],
        "Calculating…": ["计算中…", "計算中…", "計算中…"],
        "Edit Photo": ["调整照片", "調整照片", "写真を編集"],
        "Exposure": ["曝光", "曝光", "露出"],
        "Contrast": ["对比度", "對比", "コントラスト"],
        "Saturation": ["饱和度", "飽和度", "彩度"],
        "Warmth": ["色温", "色溫", "暖かみ"],
        "Vignette": ["暗角", "暈影", "ビネット"],
        "Sharpen": ["锐化", "銳利化", "シャープ"],
        "Add Background": ["添加背景", "加入背景", "背景を追加"],
        "Background": ["背景", "背景", "背景"],
        "Spacing": ["间距", "間距", "余白"],
        "Corners": ["圆角", "圓角", "角の丸み"],
        "Drop shadow": ["投影", "陰影", "ドロップシャドウ"],
        "Sunset": ["日落", "日落", "夕焼け"],
        "Ocean": ["海洋", "海洋", "海"],
        "Mint": ["薄荷", "薄荷", "ミント"],
        "Candy": ["糖果", "糖果", "キャンディ"],
        "White": ["白色", "白色", "白"],
        "Black": ["黑色", "黑色", "黒"],
        "Custom": ["自定义", "自訂", "カスタム"],
        "Auto": ["自动", "自動", "自動"],
        "Redact Photo": ["照片打码", "照片遮蔽", "写真を墨消し"],
        "Detect": ["识别", "辨識", "検出"],
        "Faces": ["人脸", "人臉", "顔"],
        "Text": ["文字", "文字", "テキスト"],
        "Blur": ["模糊", "模糊", "ぼかし"],
        "Pixelate": ["像素化", "像素化", "モザイク"],
        "Solid": ["纯色", "純色", "塗りつぶし"],
        "Looking for faces and text…": ["正在查找人脸和文字…", "正在尋找人臉和文字…", "顔とテキストを探しています…"],
        "Found %d faces and %d text areas. Drag to add a box, click one to remove it.":
            ["找到 %d 张人脸、%d 处文字。拖动可加框，点框可删除。", "找到 %d 張人臉、%d 處文字。拖曳可加框，點框可刪除。", "顔 %d 件、テキスト %d 件が見つかりました。ドラッグで枠を追加、クリックで削除。"],
        "Edit Metadata": ["编辑元数据", "編輯後設資料", "メタデータを編集"],
        "Camera": ["相机", "相機", "カメラ"],
        "Lens": ["镜头", "鏡頭", "レンズ"],
        "Taken": ["拍摄时间", "拍攝時間", "撮影日時"],
        "Location": ["位置", "位置", "場所"],
        "Author": ["作者", "作者", "作成者"],
        "Remove": ["删除", "移除", "削除"],
        "Camera & lens": ["相机和镜头", "相機和鏡頭", "カメラとレンズ"],
        "Dates": ["日期", "日期", "日付"],
        "Remove all metadata": ["删除全部元数据", "移除全部後設資料", "すべてのメタデータを削除"],
        "Annotate Photo": ["标注照片", "標註照片", "写真に注釈"],
        "Tool": ["工具", "工具", "ツール"],
        "Arrow": ["箭头", "箭頭", "矢印"],
        "Box": ["方框", "方框", "四角"],
        "Circle": ["圆圈", "圓圈", "円"],
        "Number": ["编号", "編號", "番号"],
        "Note": ["备注", "備註", "メモ"],
        "Text to place": ["要放置的文字", "要放置的文字", "配置するテキスト"],
        "Drag on the photo to draw. Text and numbers are placed with a click.":
            ["在照片上拖动来画。文字和编号点一下即可放置。", "在照片上拖曳來畫。文字和編號點一下即可放置。", "写真上をドラッグして描きます。テキストと番号はクリックで配置。"],
        "Make a Collage": ["制作拼图", "製作拼貼", "コラージュを作成"],
        "Layout": ["布局", "版面", "レイアウト"],
        "Grid": ["网格", "網格", "グリッド"],
        "Row": ["横排", "橫排", "横並び"],
        "Column": ["竖排", "直排", "縦並び"],
        "Featured": ["主图", "主圖", "メイン"],
        "Trim Video": ["剪辑视频", "修剪影片", "ビデオをトリム"],
        "Trim Audio": ["剪辑音频", "修剪音訊", "オーディオをトリム"],
        "Start": ["开始", "開始", "開始"],
        "End": ["结束", "結束", "終了"],
        "Length": ["时长", "長度", "長さ"],
        "Clear": ["清空", "清除", "消去"],
        "Shelf": ["收纳架", "收納架", "シェルフ"],
        "Drag a file, tap ⌃, drop on Float.": ["拖住文件，按 ⌃，放到“浮动”上。", "拖住檔案，按 ⌃，放到「浮動」上。", "ファイルをドラッグし ⌃ を押して「フロート」にドロップ。"],
    ]
}
