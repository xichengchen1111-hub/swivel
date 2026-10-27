# Filee 的 18 大类格式对照，以及 Swivel 这次的优化

来源：github.com/KnifeLemon/Filee（MIT，查看时的提交是 1d3887c，2026-10-27），读的是 README 的格式表和 docs/ENGINES.md。**没有复制任何代码**，只借鉴了分类思路和格式清单。

## 为什么不照搬全部 255+ 种格式

Filee 的格式覆盖面，主要靠第三方引擎：ImageMagick、FFmpeg、LibreOffice、Pandoc、Ghostscript、calibre、7-Zip、rhwp 等，其中很多是可选下载（几十 MB 到 1.3 GB）。Swivel 的规矩是**只用 macOS 自带框架、不加第三方运行时依赖**，所以这次只挑系统框架能做好、而且能验证结果正确的类别。

## 18 类逐项对照

| Filee 类别 | Filee 靠什么 | Swivel 之前 | 这次之后 |
|---|---|---|---|
| 图片 | ImageMagick | JPG/PNG/HEIC/TIFF/AVIF/GIF/BMP（WEBP 系统写不了） | **新增 JPEG 2000、PSD、TGA、EXR，以及多尺寸 ICO / ICNS 图标** |
| 矢量（SVG/EPS/AI…） | Svg.Skia、Ghostscript | 无 | 未做。macOS 14 起去掉了 PostScript；SVG 系统只能栅格化，下批再评估 |
| PDF | PDFsharp、PDFium、PdfPig | 已有（转图、文本、合并、拆分、压缩、整理页面） | 不变 |
| 文档 | 自写 DOCX/HWPX 写入器、LibreOffice | DOCX/DOC/RTF/ODT/TXT/HTML/Markdown | 不变 |
| 表格（XLSX/ODS） | ExcelDataReader | 无 | 部分：CSV / TSV 归入新的「数据文件」类；XLSX 没有系统库，未做 |
| 演示文稿 | rhwp / LibreOffice | 无 | 未做（没有系统库） |
| HWP（韩文文档） | rhwp | 无 | 不做（面向韩国用户） |
| 文本 / 源码 | Markdig | TXT/Markdown/HTML | 不变；GBK 等旧编码识别这次先用在字幕和数据文件上 |
| 电子书 | 自写、calibre | 无 | 未做；EPUB→TXT/HTML 可以用系统 bsdtar 解包，列为下一批 |
| 视频 | FFmpeg | AVFoundation 能处理的格式 | 不变（MKV/WEBM 等系统不支持） |
| 音频 | FFmpeg | M4A/WAV/AIFF/FLAC/ALAC/CAF/AAC | 不变 |
| **字幕** | 自写 | 只有内部 SRT（转写、烧录用） | **新增「字幕」类：SRT / VTT / ASS / SSA / LRC / SBV 互转，另可导出 TXT；GBK、Big5、Shift-JIS 等旧编码自动识别，统一输出 UTF-8** |
| 压缩包 | 7-Zip | 系统 bsdtar 能解的都支持 | 不变 |
| CAD（DWG/DXF） | ACadSharp | 无 | 不做 |
| **字体** | 自写 | 无 | **新增「字体」类：TTF / OTF ↔ WOFF / WOFF2。字形表逐字节原样保留；WOFF2 解码交给 CoreText** |
| **数据** | System.Text.Json、YamlDotNet 等 | 无 | **新增「数据文件」类：JSON / JSONL / PLIST / CSV / TSV 互转，另可导出 YAML** |
| **3D 模型** | SharpGLTF 等 | 无 | **新增「3D 模型」类：OBJ / STL / PLY / USD(A/C/Z) / ABC → USDZ、OBJ、STL、PLY、USDA，另可生成 PNG 预览图（Model I/O + SceneKit）** |
| 地理数据（GPX/KML/GeoJSON） | 自写 | 无 | 未做；纯解析就能实现，列为下一批 |

Filee 没有、Swivel 新增的 Mac 特有能力：**PLIST**（属性列表）和 **USDZ**（Apple AR / 快速查看用的 3D 格式）。

## 这次做了什么

拖文件时，新增的 4 类在「转换」菜单里出现，在设置窗口的「自定义」页里也能为它们排序或隐藏：

- **字幕**：`Sources/Swivel/SubtitleFormats.swift`。保留文字和时间；样式、颜色、位置会去掉（转换前有提示）。
- **数据文件**：`Sources/Swivel/DataFormats.swift`。不猜类型：CSV 的单元格一律保留为文本，第一行当列名；JSON 里的 null 存不进 plist 时直接拒绝，不会悄悄丢掉；plist 的日期转成 ISO 8601、二进制数据转成 Base64（转换前有提示）；导出的 CSV / TSV 带 BOM，Excel 和 Numbers 打开中文不会乱码。
- **3D 模型**：`Sources/Swivel/Models3D.swift`。**只转几何形状**，材质、贴图、动画都去掉。这是安全上的取舍：模型文件可以用绝对路径引用 Mac 上任意一张图片，打包成 USDZ 时会把它带进去。
- **字体**：`Sources/Swivel/FontFormats.swift`。WOFF2 用的是标准允许的 null transform；字体集合（TTC）暂不支持。
- **图标**：`Images.makeIcon`。图片居中放到透明正方形上，生成多个尺寸，不放大到超过原图。ICO 最多 6 档（ImageIO 的上限）；ICNS 写的是标准 iconset 尺寸。
- 新转换都走 MCP（`plan_action` / `apply_plan`），输出文件名可以预测。
- 所有新文字都有简中 / 繁中 / 日文翻译。

## 另外加的：AirDrop → 文件暂存架

设置 →「文件暂存架」新增一个勾选项「AirDrop 收到的文件也放进暂存架」，默认关闭。

- 打开后，Swivel 监视「下载」文件夹。只有隔离属性的来源是 `sharingd`（AirDrop），而且是开始监视之后才收到的文件，才会放进暂存架。改名、或者把旧的 AirDrop 文件挪回来，都不会被当成新文件。
- 会等文件大小停止变化后才放入，所以传到一半的文件不会出现。只看文件夹条目和隔离属性，不打开、也不读取文件内容。
- 第一次打开这个选项时，macOS 会询问一次是否允许访问「下载」。
- 代码在 `Sources/Swivel/AirDropShelf.swift`。

## 验证

- `Tests/SwivelTests/FormatKindsTests.swift`，共 10 个测试：
  - 新类别的识别、菜单和 MCP 映射；
  - 字幕时间解析和各格式往返，GB18030 → UTF-8；
  - CSV 按 RFC 4180 解析，表格经 JSON 往返；
  - null 被拒绝，日期和二进制数据如何转换，YAML 的转义；
  - 系统里的 TTF 和 OTF 经 WOFF / WOFF2 往返后，除 head 外所有表逐字节一致，整体校验值等于 0xB1B0AFBA；
  - OBJ 转成每种 3D 格式后重新读回，几何体都还在；
  - ICO / ICNS 的尺寸，JPEG 2000 / PSD / TGA 的尺寸；
  - AirDrop：在临时文件夹里模拟。只有新的 `sharingd` 文件被送进暂存架；Safari 下载、监视前就有的文件、改名后的文件都不会。
- 完整的 `./test.sh --no-parallel`：426 个测试全部通过。`./build.sh` 成功，签名严格校验通过。

## 没有验证的

- 一次真实的 AirDrop 传输（需要另一台设备），以及真机上 macOS 询问「下载」权限的弹窗。
- 从 Finder 真实拖文件到新菜单项。
- 带变换的真实 WOFF2 文件（Google 编码器产出的那种）解码：走的是 CoreText，但没有这样的样本文件可测。
- 大型 3D 模型或 USDZ 的性能；EXR 的高位深往返。
- 应用没有安装，也没有推送。

## 下一批候选

按「纯解析就能做、系统框架就够」排：

1. 地理数据 GPX / KML / GeoJSON ↔ CSV
2. EPUB → TXT / HTML
3. EML 邮件 → HTML / TXT
4. 文本文件统一转成 UTF-8
5. SVG → PNG / PDF（先评估系统渲染的质量）
