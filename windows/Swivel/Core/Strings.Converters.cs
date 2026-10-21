namespace Swivel;

static partial class Strings
{
    /// Windows-only strings of the Converters module: English → [简体中文, 繁體中文, 日本語].
    static partial (string English, string[] Translations)[] Converters => [
        ("Couldn't read %@", ["无法读取“%@”", "無法讀取「%@」", "「%@」を読み込めませんでした"]),
        ("Couldn't save %@", ["无法存储“%@”", "無法儲存「%@」", "「%@」を保存できませんでした"]),
        ("Couldn't convert %@", ["无法转换“%@”", "無法轉換「%@」", "「%@」を変換できませんでした"]),
        ("Couldn't extract %@", ["无法解压“%@”", "無法解壓縮「%@」", "「%@」を展開できませんでした"]),
        ("This PC can't write %@ images", ["这台电脑无法输出 %@ 图片", "這部電腦無法輸出 %@ 圖片", "この PC では %@ 画像を書き出せません"]),
        ("Couldn't encode the image", ["无法编码图片", "無法編碼圖片", "画像をエンコードできませんでした"]),
        ("Couldn't write PDF", ["无法写入 PDF", "無法寫入 PDF", "PDF を書き出せませんでした"]),
        ("This PC has no HEVC video encoder. Install HEVC Video Extensions from the Microsoft Store.",
            ["这台电脑没有 HEVC 视频编码器。请从 Microsoft Store 安装“HEVC 视频扩展”。",
             "這部電腦沒有 HEVC 視訊編碼器。請從 Microsoft Store 安裝「HEVC 視訊延伸模組」。",
             "この PC には HEVC ビデオエンコーダがありません。Microsoft Store から「HEVC ビデオ拡張機能」をインストールしてください。"]),
        ("Can't merge these files", ["无法合并这些文件", "無法合併這些檔案", "これらのファイルを結合できません"]),
        ("No video track", ["没有视频轨道", "沒有視訊軌道", "ビデオトラックがありません"]),
        ("No audio track", ["没有音频轨道", "沒有音訊軌道", "オーディオトラックがありません"]),
        ("Couldn't create GIF", ["无法生成 GIF", "無法製作 GIF", "GIF を作成できませんでした"]),
        ("This file can't be converted that way", ["这个文件无法这样转换", "這個檔案無法這樣轉換", "このファイルはこの方法では変換できません"]),
        ("This PC doesn't have the codec for %@", ["这台电脑没有“%@”所需的编解码器", "這部電腦沒有「%@」所需的編解碼器", "この PC には「%@」に必要なコーデックがありません"]),
        ("Couldn't create the archive", ["无法创建压缩文件", "無法建立壓縮檔", "アーカイブを作成できませんでした"]),
        ("This PC can't open %@ archives", ["这台电脑无法打开 %@ 压缩文件", "這部電腦無法開啟 %@ 壓縮檔", "この PC では %@ アーカイブを開けません"]),
        ("%@ contains files that would be saved outside its folder",
            ["“%@”包含会被存到所在文件夹之外的文件", "「%@」包含會被存到所在資料夾之外的檔案", "「%@」には、フォルダの外に保存されるファイルが含まれています"]),
    ];
}
