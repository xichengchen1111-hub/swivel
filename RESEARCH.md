# Swivel 新功能调研：1.3.3 之后

调研更新：2026-10-21（Australia/Brisbane）。通过 GitHub 连接读取私有仓库，确认远端 main 与本地同为 `7512c705b6c55087fa42e84bfffbef71a4506bd0`（1.3.3）。调研之后已完成 1.4.0 实现，详见 README 和下方交付说明。

## 1.4.0 交付范围

- 批量重命名、PDF 选页排序旋转、图片二维码/条形码识别；原生 AppKit 界面，四种语言，独立开关。
- 应用圆盘支持自选 App 与后缀过滤；Codex/Claude Code 按用户选择使用官方新草稿链接，附本地文件引用，不自动上传或发送。
- 二维码库支持手动保存、新增、编辑、分组、搜索、删除和撤销；原创树形视图可切换为有效二维码，灵感来自 Tree.ICQR。
- 新增应用/二维码拖拽圆盘和可开关的 ⌃⌥Space 快捷键圆盘；保留已有自定义按键。

## 当前结论

用户已选定 **5 批量重命名、7 PDF 选页与排序、9 二维码识别**。这三项已实现并可独立开关；另增加应用衔接、二维码库和快捷键圆盘。下面保留选型时的使用量与需求证据。最初提出的 1/2/3 优先建议由此次选择取代，其他提案保留供以后参考。

当前已经有图片 OCR、多选暂存架、图片参数预设和快捷操作面板；下面不重复推荐基础版本。也已核对 `Catalog.swift`、`Documents.swift`、`PhotoEditors.swift`、`ImagePresets.swift`、`Shelf.swift`：PDF 合并、逐页拆分、压缩、整体旋转也已存在；Add Background 是加衬底、留白和阴影，并非主体抠图。图片预设当前是尺寸、格式、质量和元数据参数，并非可串接多个工具的流程。

以下排名与工作量是结合现有代码的产品和工程判断，不是竞品承诺，也没有实测竞品。S/M/L 表示小/中/大相对工作量，均包含四种语言与关键流程验证；不是交付日期。每项的“无新增权限”只指不新增辅助功能、屏幕录制或完全磁盘访问，普通文件访问与系统提示仍适用。

## 本轮来源核查

### 用户选定后的使用量补查（2026-10-21）

**没有找到可直接比较“macOS 批量重命名 / PDF 选页排序 / 图片二维码识别”的公开、同口径使用量统计。** 不把整个应用的下载量、GitHub 星数、网页排名、手机扫码人数或单条用户评论当成功能调用频率；下面严格区分平台自报数据、产品能力证明与产品判断。

| 证据 | 能说明什么 | 不能说明什么 |
| --- | --- | --- |
| [Smallpdf《PDF Statistics & Usage in 2025》](https://smallpdf.com/pdf-statistics)将压缩 34%、转换 28% 列为其最常使用的工具，并将相关数据归入 2020–2025 内部数据；另一段列电子签名 19%、PDF 转 Word 16% | 在这个 PDF 平台自报样本里，压缩与转换具有较强的使用量信号 | 不是全市场、macOS 或 Swivel 用户统计；页面未给出足够清晰的计数单位、样本窗口和审计方法；类别可能重叠，不能相加为市场份额。压缩热门也不能证明选页排序占比最高 |
| [Smallpdf 首页](https://smallpdf.com/)的 Most Popular PDF Tools 包括 PDF 转 Word、合并、JPG 转 PDF、签名、编辑、压缩 | 官方把这些列为热门入口，可与前一项交叉参考需求方向 | 展示顺序不等于按使用次数排名；选页排序没有独立占比 |
| [iLovePDF 定价页](https://www.ilovepdf.com/pricing)自报每日处理 1,650 万份文档 | PDF 工具整体有较大处理规模，是另一平台的需求背景 | 是整个平台的文档处理量，不是独立用户数，也不是选页、排序或合并的单项用量 |
| [PowerToys 数据与隐私说明](https://github.com/microsoft/PowerToys/blob/main/DATA_AND_PRIVACY.md)列出 PowerRename 调用与重命名操作事件 | 该产品确实设计了功能频率统计，能力和计数事件可查 | 文档没有公布可以拿来与 PDF、QR 比较的调用量排名；PowerToys 是 Windows 产品 |
| [Apple Finder 重命名说明](https://support.apple.com/guide/mac-help/rename-files-folders-and-disks-on-mac-mchlp1144/mac)已有替换、加文字和更改名称格式 | Swivel 的批量重命名存在直接系统替代，应突出组合规则、整批预览、冲突检查和撤销 | 被系统提供不等于所有人都高频使用，也不能推出市场占比 |
| [Shottr 官方功能页](https://shottr.cc/)把二维码读取并入 OCR；[macshot](https://github.com/sw33tLie/macshot)也列出二维码与条码检测 | 在相似桌面图片工具里是有实际产品采用的能力，适合作为轻量图片动作 | 未找到这两个产品公开的二维码调用占比；不能据此说桌面二维码使用量高，也不能套用手机支付/扫码数据 |

Smallpdf 统计页同时出现不同的月用户口径，因此本报告不采用其用户总量来做精确比较；34% / 28% / 19% / 16% 仅保留为平台自报的方向信号，不视为经过独立审计的数据。

**对已选三项的投入顺序建议：7 → 5 → 9。这是依据适用场景与 Swivel 整合价值作出的产品判断，不是实测使用频率榜单。** PDF 文档处理有较多可核对的规模信号，但选页本身尚无独立统计；批量重命名贴合拖入多文件的场景；二维码适合作为成本较低的补充，可在开发排期中先交付，但并不因此变成最高频功能。

建议的功能范围保持清楚：

- **7 PDF：** 选定页码导出、缩略图排序和移除不需要的页，生成新文件；现有合并、压缩、逐页拆分继续沿用。扫描件 OCR 是后续独立扩展，本次选择不自动扩大成可搜索 PDF 引擎。
- **5 重命名：** 前后缀、替换与编号可组合，整批旧名→新名预览，明确标记冲突，执行后可撤销；不做跨目录自动整理。
- **9 二维码：** 从交入的图片识别，显示解码内容，明确复制或点击打开；支持同图多码，不开相机，不自动打开链接。

若以后要回答“Swivel 自己哪些功能用得最多”，可另行设计仅本机、可关闭的动作次数记录，用成功操作数、周使用天数与重复使用情况评估。这里仅提出研究方法，不在本次引入任何统计或上传。

星数为本轮 API 查询快照；日期是默认分支最近提交的 UTC 日期，不把 push 时间当作功能开发证据。六个仓库查询时均未 archived。许可证读取的是实际文件；只参考产品行为，不导入竞品代码或素材。

| 项目 | Stars | 许可证 | 默认分支最近提交与状态 | 可借鉴内容 |
| --- | ---: | --- | --- | --- |
| [PowerToys](https://github.com/microsoft/PowerToys) | 139,521 | [MIT](https://github.com/microsoft/PowerToys/blob/main/LICENSE) | [2026-10-20](https://github.com/microsoft/PowerToys/commit/3d6827a53a579ba9bedcf8d4bbda21348d78cbdb)：报告生成修复，近期维护 | PowerRename 的预览、筛选和冲突处理；Windows 实现不能直接移植 |
| [Maccy](https://github.com/p0deje/Maccy) | 22,008 | [MIT](https://github.com/p0deje/Maccy/blob/master/LICENSE) | [2026-10-19](https://github.com/p0deje/Maccy/commit/c700182f638d9d29bfa0aec9dc560422698854c6)：崩溃修复 | 固定、搜索和明确的历史管理；只用于 Swivel 已持有的文件范围 |
| [Kando](https://github.com/kando-menu/kando) | 6,497 | [源码 MIT，其他文件单独许可](https://github.com/kando-menu/kando/blob/main/LICENSE.md) | [2026-10-20](https://github.com/kando-menu/kando/commit/5a4340c25a822bfade2960dd5e3bca1682d88fda)：变更说明维护 | 自定义动作与稳定菜单入口；不引入 Electron |
| [macshot](https://github.com/sw33tLie/macshot) | 3,853 | [GPL v3](https://github.com/sw33tLie/macshot/blob/main/LICENSE) | [2026-10-14](https://github.com/sw33tLie/macshot/commit/6fdaa848f590b14b08479394576658e982e80bea)：录制整合、测试与本地化 | 前景抠图、二维码、浮动参考图、带动作的缩略图；独立实现 |
| [Clop](https://github.com/FuzzyIdeas/Clop) | 1,852 | [GPL v3](https://github.com/FuzzyIdeas/Clop/blob/main/LICENSE) | [2026-10-16](https://github.com/FuzzyIdeas/Clop/commit/4be5106951b490c9b61a53f6f15eb580a760bd69)：README 明确宣布公开提交暂停，后续私下开发 | 面向分享的压缩、缩小、处理后直接拖出；不能称为持续公开开发的优先依赖 |
| [DropPoint](https://github.com/GameGodS3/DropPoint) | 1,099 | [GPL v3](https://github.com/GameGodS3/DropPoint/blob/main/LICENSE) | [2026-08-02](https://github.com/GameGodS3/DropPoint/commit/137789de41104f969ee5c521558c8ee366156a82)：运行时与 CI 更新；维护连续性较弱 | 跨位置收集文件再一次拖出的简单模型 |

另外查看了三个产品的官方功能说明，只作为交互参考，不归入可复用开源项目：

- [Dropover](https://dropoverapp.com/)：命名/配色暂存架、最近与固定暂存架、Instant Actions、自定义动作、Shortcuts、系统分享入口。[官方技巧](https://dropoverapp.com/tips)也说明多选、停靠与脚本操作。
- [CleanShot](https://cleanshot.com/features)：截图角落操作区、恢复最近关闭的操作区、多显示器设置、可编辑标注项目文件。
- [Shottr](https://shottr.cc/)：二维码识别、独立置顶参考图、半透明叠图、图内取色与测量。

Clop 的 README 列出 pngquant、jpegoptim、ffmpeg、ghostscript 等第三方工具。Swivel 保留无第三方依赖的要求，只借鉴使用目标，不承诺达到相同压缩率。

## 排序后的十项建议

### 1. 一键处理流程（L）

- **体验：** 保存“发邮件图片”“发网页截图”等动作，例如缩到 1600 px → 转 JPG → 移除元数据 → 把结果放上暂存架，一次拖放完成。
- **与现状的差别：** 当前 Image Presets 已完成前三类图片参数；新增价值是处理结果自动交给后续工具，以及把多步骤保存为稳定菜单动作，不能仅把原预设换一个名字。
- **第一版：** 只串接内置动作与内置输出去向，先做图片处理后复制/暂存；不执行任意脚本。最终文件仍保存在原文件旁边，不额外生成一串可见中间文件。
- **来源：** Dropover 自定义/即时动作、Kando 自定义动作；链式流程的具体设计是结合 Swivel 提出的扩展，不声称两者完全相同。
- **权限：** 无新增权限。若以后支持调用他人 Shortcuts 或控制外部应用，另行评估相应动作的授权。
- **难点：** 动作类型兼容、步骤失败、取消、结果交接和预设改名后的稳定标识。

### 2. 按目标文件大小压缩（M，先做 JPG/HEIC）

- **体验：** 直接选“小于 500 KB / 1 MB / 2 MB”，显示原大小、结果大小与画面对比；适合上传附件和网页表单。
- **与现状的差别：** 当前压缩器和预设使用质量参数，用户仍需试数值；新功能把目标改为最终文件大小。
- **第一版：** JPG/HEIC 试编码寻找合适质量，在用户允许后再减像素；达不到目标时明确提示，不能显示虚假的达标状态。PNG 无损压缩、视频与 PDF 的目标大小不包含在本期。
- **来源：** Clop 的分享导向优化与快速缩小；指定字节上限是针对 Swivel 的提案，不声称 Clop 已有完全相同功能。
- **权限：** 无新增权限，使用 ImageIO，不引入外部编码器。
- **难点：** 质量底线、透明度、方向、色彩、批次性能，以及体积反而变大的处理。

### 3. 本地主体抠图（M）

- **体验：** 拖入人物或商品图，选择“移除背景”，预览透明棋盘格，导出透明 PNG；之后可接现有 Add Background。
- **与现状的差别：** 目前 Add Background 只给整张图加衬底与阴影，没有移除原背景。
- **来源：** macshot README 明确列出 Apple Vision foreground mask；系统接口为 [VNGenerateForegroundInstanceMaskRequest](https://developer.apple.com/documentation/vision/vngenerateforegroundinstancemaskrequest)。
- **权限：** 无新增权限，限定用户交入的本地图片。
- **难点：** 多主体选择、毛发和半透明边缘、识别失败、尺寸内存限制；第一版不承诺专业修图级边缘，也不加入手工精细蒙版编辑。

### 4. 截图队列与就地操作条（L）

- **体验：** 连续截几张图，角落仍能看见并切换最近项目；每张图旁可复制、标注、OCR、挂绳或加入暂存架。
- **与现状的差别：** 当前只有一个活动角落卡片；旧卡片会回截图目录，文件没有丢失。新功能减少重新找图的操作。
- **来源：** CleanShot Quick Access Overlay、macshot 浮动缩略图；多项队列是 Swivel 自身的扩展。
- **权限：** 无新增权限，继续接收系统生成的截图文件，不新增屏幕采集。
- **难点：** 卡片、编辑器与晾衣绳的文件归属，编辑时暂停超时，慢写入、多显示器与退出恢复。先补齐现有编辑器 Apply 的真实流程验证。

### 5. 批量重命名与预览（L）

- **体验：** 将一组文件变成“项目名-001、002…”；支持前后缀、查找替换与编号，先看旧名→新名，再执行，可在当前会话撤销。
- **来源：** [PowerRename 官方文档](https://learn.microsoft.com/windows/powertoys/powerrename)的实时预览与筛选。
- **权限：** 无新增权限，需要交入文件目录的普通写权限。
- **难点：** 这是改变原文件名的动作；必须处理重名、仅大小写变化、交换名称、部分失败及撤销时的冲突，不覆盖后来创建的文件。第一版不做跨目录整理。

### 6. 可恢复的分组暂存架（M–L）

- **体验：** 分“工作/个人/本次发送”等组，命名、固定，退出后可选择恢复；仍可多选拖出。
- **与现状的差别：** 当前 File Shelf 仅为会话引用列表，退出清空；不是重新增加已实现的多选。
- **来源：** Dropover 的命名、固定和最近暂存架；Maccy 的固定与搜索设计。
- **权限：** 不默认增加上述三项权限；需要处理外部文件引用的长期可访问性，不能把保存路径等同于永久权限。
- **难点：** 文件被移动/删除、外置盘未挂载、引用恢复与重复条目。默认可保持临时模式，仅恢复用户固定的组，不复制全部原文件。

### 7. PDF 选页与排序（M；扫描件 OCR 另计 L）

- **体验：** 缩略图中选第 2–5 页导出，拖动调整顺序，再生成新 PDF；后续可增加扫描 PDF 的批量识字。
- **与现状的差别：** 现有合并、全量逐页拆分、压缩和整体旋转不再作为新功能。当前 PDF 文本转换读取内嵌文字，不等于扫描件 OCR。
- **来源：** 主要来自现有 `PDFs` 操作缺口与 [Apple PDFDocument](https://developer.apple.com/documentation/pdfkit/pdfdocument) 的页面操作能力；扫描件识字可复用现有 Vision OCR。不是将竞品已有功能当作来源证据。
- **权限：** 无新增权限，限定交入文件。
- **难点：** 页顺序、旋转、加密 PDF、写出校验和内存；扫描件 OCR 第一阶段可导出 TXT，带准确文字层的可搜索 PDF 单独设计，不混为同一工作量。

### 8. 独立置顶参考图与半透明对照（M）

- **体验：** 从晾衣绳拉出一张图固定在桌面，调整透明度与缩放；对照设计稿或资料时不用一直展开绳子。
- **与现状的差别：** 当前 Quick Look 是查看入口；新增的是可持续存在的独立参考窗口。第一版不支持点击穿透。
- **来源：** Shottr 的 Pin Screenshots / Overlay Images，macshot 的 Pin to screen。
- **权限：** 无新增权限，只显示交入图片，不读取别的应用窗口。
- **难点：** 多显示器、全屏 Spaces、关闭/找回窗口、焦点行为；不要在工作区留下难以关闭的覆盖层。

### 9. 二维码/条形码识别（S）

- **体验：** 拖入二维码图片，直接显示解码文本，允许复制；URL 由用户点击打开。
- **来源：** Shottr 和 macshot 的 QR 功能；系统接口为 [VNDetectBarcodesRequest](https://developer.apple.com/documentation/vision/vndetectbarcodesrequest)。
- **权限：** 无新增权限，只读图，不开相机、不截屏。
- **难点：** 同图多码、旋转/低清晰度、链接展示；第一版不自动打开检测出的地址，也不扩充到所有码制的生成器。

### 10. 可查看结果的批处理队列（L）

- **体验：** 显示“正在处理 3/20”，逐项查看成功输出与失败原因，只重试失败项，停止剩余任务。
- **来源：** 对 `Catalog.each`、`PresetActions.run` 与现有反馈入口的代码分析；Clop 的处理后浮动结果仅是反馈呈现参考，不声称其具备本提案完整队列。
- **权限：** 无新增权限，与各文件动作一致。
- **难点：** 现有逐项执行遇到抛错可能结束整批；需要明确保留已完成输出，安全清理半成品。第一版只保证停止启动后续项目，不能把不支持中断的系统编码器包装成即时取消。

## 建议安排与暂缓范围

已按用户选择将下一轮范围确定为批量重命名、PDF 选页排序、二维码识别；投入优先级建议 PDF → 重命名 → 二维码，二维码可按低成本先交付。若以后再选择一键处理流程，队列状态应作为可靠性的实现基础，不必先发布一个独立复杂任务中心。所有新模块继续独立开关、默认本地处理，菜单可固定顺序与隐藏项目。

本轮暂缓完整剪贴板管理器、云盘/账号同步、窗口管理、录屏/长截图和任意脚本插件市场；它们的范围和维护成本明显高于现有文件处理主线。当前还有真实 Finder 拖放和编辑器 Apply 等既有端到端检查待补，不把已打开过窗口当作完成这些验证。

---

# 2026-10-20 历史调研（前四项已在 1.1 实现）

调研日期：2026-10-20（Australia/Brisbane）。项目基线：`4618ea5`。本次仅新增本文，不修改应用代码、设置或截图接管行为，也未运行或安装竞品。

## 建议先选什么

建议第一轮从 **1 图片提取文字、2 暂存架多选、3 常用处理预设** 中选一项。它们直接服务“拖入文件 → 快速处理 → 继续拖走”，可用原生系统框架实现，不增加辅助功能、屏幕录制或完全磁盘访问要求。如果菜单栏图标被遮挡是最困扰的问题，则把 **4 快捷操作面板** 提到第一位。

排序是结合当前代码的产品判断，不是按星数排名。优先考虑日常收益、现有代码的可复用程度、权限负担和验证成本。

## 已有能力与边界

阅读了根目录 `AGENTS.md`、`README.md` 及 `Sources/Swivel` 的菜单目录、暂存架、晾衣绳、截图接管、图片编辑器和执行流程：

- 已有转换、裁剪、压缩、调整、背景、标注、拼图、视频裁剪等；这些不重复算新功能。
- `PhotoEditors.swift` 已用 Vision 检测文字区域以便遮挡，但尚无“提取可复制文字”的菜单动作。
- `Shelf.swift` 已有文件列表和单项拖出；目前不是可直接拖入、多选后一起拖出的完整暂存架。
- 晾衣绳已通过自己的文件目录重新加载挂图；重启后恢复图片不是全新提案。已能处理复制的裸图片，并过滤部分私密/临时剪贴板类型。
- 已有 ⌃⌥T 显示晾衣绳、重新打开应用显示设置，以及菜单栏初始位置的处理。新增面板应扩展这些入口。
- 截图角落卡片目前只保留一个活动项目，新卡片出现时把上一张收回原截图目录；不是直接丢弃文件。队列提案改善的是连续操作的可见性。
- 当前执行流程有忙碌/成功/失败提示，但没有面向用户的批次进度列表和停止队列入口。

所有提案遵守：不引入第三方依赖；处理结果通过 `Paths.output` / `Paths.unique` 保存在原文件旁边，不覆盖、不新建结果文件夹；界面用 `L(...)` 并补齐简体中文、繁體中文、日本語；保留截图设置恢复；保持轻柔动画。内部状态和索引不属于转换结果，优先存 UserDefaults 或标准应用支持位置，不为每次处理创建目录。现有晾衣绳目录是已有架构，不据此扩大新功能的输出范围。

## 调研方法和项目表

直接核对 GitHub 仓库 API、README、LICENSE、默认分支最近三条提交及 `/releases/latest`。下表日期统一使用 GitHub 的 UTC 日期；星数是查询时快照，会变化。最近提交可能只是文档、翻译或依赖更新，不能等同于新功能；`pushed_at` 也不能证明默认分支仍在开发。Release 指该接口返回的最新正式发布，可能不含 beta 或其他发布渠道。未实测竞品，也未逐条审计其全部源码。

### 可作为开源调研对象的 12 个项目

| 项目 | 许可证（已读文件） | 功能及 Swivel 可借鉴的部分 |
| --- | --- | --- |
| [p0deje/Maccy](https://github.com/p0deje/Maccy) | [MIT](https://github.com/p0deje/Maccy/blob/master/LICENSE) | 轻量剪贴板历史；借鉴搜索、固定条目、忽略一次复制和暂停采集。只扩展 Swivel 的图片范围。 |
| [Clipy/Clipy](https://github.com/Clipy/Clipy) | [MIT](https://github.com/Clipy/Clipy/blob/develop/LICENSE) | 剪贴板历史和片段；借鉴独立设置快捷键，即使图标不可见仍有入口。 |
| [microsoft/PowerToys](https://github.com/microsoft/PowerToys) | [MIT](https://github.com/microsoft/PowerToys/blob/main/LICENSE) | Windows 工具合集；参考 Image Resizer、PowerRename、Text Extractor 和统一设置中的功能发现，不能直接搬用 Windows 实现。 |
| [alejandrobujan/tendedero](https://github.com/alejandrobujan/tendedero) | [源码 MIT；名称、图标及相关图像除外](https://github.com/alejandrobujan/tendedero/blob/main/LICENSE) | 截图晾衣绳；参考截图到临时承接区再继续使用的流程。Swivel 已有核心行为，避免重复提案。 |
| [kando-menu/kando](https://github.com/kando-menu/kando) | [源码 MIT；文档、配置和素材按各文件许可](https://github.com/kando-menu/kando/blob/main/LICENSE.md) | 跨平台环形菜单；借鉴自定义动作、键盘操作和稳定的动作位置。Electron 实现不适合引入本项目。 |
| [dwarvesf/hidden](https://github.com/dwarvesf/hidden) | [MIT](https://github.com/dwarvesf/hidden/blob/develop/LICENSE) | Hidden Bar 菜单栏收纳；学习处理图标不可达的问题。首选保证 Swivel 自身入口，不做全系统收纳器。 |
| [MrKai77/Loop](https://github.com/MrKai77/Loop) | [GPL v3](https://github.com/MrKai77/Loop/blob/develop/LICENSE) | 原生 macOS 环形窗口操作；借鉴执行前预览和清楚的方向反馈。仅作为交互参考。 |
| [GameGodS3/DropPoint](https://github.com/GameGodS3/DropPoint) | [GPL v3](https://github.com/GameGodS3/DropPoint/blob/main/LICENSE) | 跨平台拖放中转区；借鉴从不同位置收集文件再拖出的流程，不引入 Electron。 |
| [flameshot-org/flameshot](https://github.com/flameshot-org/flameshot) | [GPL v3](https://github.com/flameshot-org/flameshot/blob/master/LICENSE) | 跨平台截图与标注；借鉴截图后就近显示动作及键盘操作，不照搬代码。 |
| [sw33tLie/macshot](https://github.com/sw33tLie/macshot) | [GPL v3](https://github.com/sw33tLie/macshot/blob/main/LICENSE) | 原生 macOS 截图、OCR、标注、录屏；借鉴文件 OCR 和截图后的连续编辑流程。录屏、上传不在推荐最小范围。 |
| [jordanbaird/Ice](https://github.com/jordanbaird/Ice) | [GPL v3](https://github.com/jordanbaird/Ice/blob/main/LICENSE) | 菜单栏收纳、搜索、刘海下方显示隐藏图标；用于理解拥挤场景，不纳入 Swivel。 |
| [thaw-app/Thaw](https://github.com/thaw-app/Thaw) | [GPL v3](https://github.com/thaw-app/Thaw/blob/development/LICENSE) | 菜单栏管理与搜索；借鉴快捷入口和按需展示，避免接管其他应用的图标。 |

优先作为设计来源的是 Maccy、Clipy、PowerToys、Kando 和 Hidden Bar。Tendedero 的许可范围必须单独对待。此次统一采用独立实现，不引入上述项目的代码、依赖、品牌或素材。GPL 项目仅供产品行为与交互比较。

### 星数与活跃度快照

| 项目 | Stars | 默认分支最近提交（UTC） | 最新正式 Release（UTC） | 活跃度判断及限制 |
| --- | ---: | --- | --- | --- |
| [p0deje/Maccy](https://github.com/p0deje/Maccy) | 21,996 | [2026-10-19 · c700182](https://github.com/p0deje/Maccy/commit/c700182f638d9d29bfa0aec9dc560422698854c6) | [2.7.1](https://github.com/p0deje/Maccy/releases/tag/2.7.1) · 2026-08-10 | 近期有崩溃修复及构建维护，优先参考。 |
| [Clipy/Clipy](https://github.com/Clipy/Clipy) | 8,927 | [2026-10-18 · 71b0e82](https://github.com/Clipy/Clipy/commit/71b0e82623d5c73b4b717d81233eb78db8bbe258) | [1.4.0](https://github.com/Clipy/Clipy/releases/tag/1.4.0) · 2026-10-12 | 近期有功能修复和发布，不能按旧印象认定停更。 |
| [microsoft/PowerToys](https://github.com/microsoft/PowerToys) | 139,501 | [2026-10-20 · 5c9fddc](https://github.com/microsoft/PowerToys/commit/5c9fddcddd9f50401cd5ea6f56b8f421e0badd40) | [v0.101.2362.0](https://github.com/microsoft/PowerToys/releases/tag/v0.101.2362.0) · 2026-08-25 | 近期持续提交；是 Windows 项目，仅借鉴工具设计。 |
| [alejandrobujan/tendedero](https://github.com/alejandrobujan/tendedero) | 2,384 | [2026-10-18 · 32a3e47](https://github.com/alejandrobujan/tendedero/commit/32a3e47187fdea7b2fd43b3ab5a913f0ed33724a) | [v1.6.0](https://github.com/alejandrobujan/tendedero/releases/tag/v1.6.0) · 2026-10-18 | 近期发布且有截图交互修复。 |
| [kando-menu/kando](https://github.com/kando-menu/kando) | 6,486 | [2026-10-20 · 5a4340c](https://github.com/kando-menu/kando/commit/5a4340c25a822bfade2960dd5e3bca1682d88fda) | [v3.0.0](https://github.com/kando-menu/kando/releases/tag/v3.0.0) · 2026-09-23 | 近期有发布和修复；跨平台，需重新用 AppKit 实现。 |
| [dwarvesf/hidden](https://github.com/dwarvesf/hidden) | 15,223 | [2026-09-19 · 47937b9](https://github.com/dwarvesf/hidden/commit/47937b9d8aede0325ca87a7880af13433e7c5cf8) | [v1.11.1](https://github.com/dwarvesf/hidden/releases/tag/v1.11.1) · 2026-09-18 | 近期有刘海溢出功能提交；该提交晚于所列 Release，不视为正式版已具备。 |
| [MrKai77/Loop](https://github.com/MrKai77/Loop) | 11,814 | [2026-10-13 · 573ace3](https://github.com/MrKai77/Loop/commit/573ace3071641bbe002527ccfc0811842300ccba) | [1.4.2](https://github.com/mrkai77/Loop/releases/tag/1.4.2) · 2026-01-26 | 近期有窗口交互修复。 |
| [GameGodS3/DropPoint](https://github.com/GameGodS3/DropPoint) | 1,098 | [2026-08-02 · 137789d](https://github.com/GameGodS3/DropPoint/commit/137789de41104f969ee5c521558c8ee366156a82) | [v1.2.1](https://github.com/GameGodS3/DropPoint/releases/tag/v1.2.1) · 2022-09-12 | 2026-08 有现代化提交，但此前相邻提交在 2023 年；发布落后，维护连续性较弱。 |
| [flameshot-org/flameshot](https://github.com/flameshot-org/flameshot) | 31,261 | [2026-10-16 · 4bc3aca](https://github.com/flameshot-org/flameshot/commit/4bc3acacf0ddafe1782147389988c82698ee8158) | [v14.0.0](https://github.com/flameshot-org/flameshot/releases/tag/v14.0.0) · 2026-06-19 | 近期有翻译、交互及 macOS 打包修复。 |
| [sw33tLie/macshot](https://github.com/sw33tLie/macshot) | 3,844 | [2026-10-14 · 6fdaa84](https://github.com/sw33tLie/macshot/commit/6fdaa848f590b14b08479394576658e982e80bea) | [v4.3.0](https://github.com/sw33tLie/macshot/releases/tag/v4.3.0) · 2026-09-23 | 近期有录屏整合及测试维护。 |
| [jordanbaird/Ice](https://github.com/jordanbaird/Ice) | 29,826 | [2025-09-20 · 11edd39](https://github.com/jordanbaird/Ice/commit/11edd39115f3f43a83ae114b5348df6a0e1741cf) | [0.11.12](https://github.com/jordanbaird/Ice/releases/tag/0.11.12) · 2024-10-29 | 默认分支最新为 2025 年 issue 模板修改，正式发布更早；2026 年 push 时间不代表近期功能维护。 |
| [thaw-app/Thaw](https://github.com/thaw-app/Thaw) | 12,186 | [2026-10-19 · 5ab93e4](https://github.com/thaw-app/Thaw/commit/5ab93e49e52d9e3e1a8f91b13906af9e51a47c03) | [2.1.0](https://github.com/thaw-app/Thaw/releases/tag/2.1.0) · 2026-10-14 | 近期有依赖与变更说明提交、正式发布；是原仓库迁移后的有效地址。 |

以上 12 个源码仓库在查询时均未标记 archived；不把这个标志当作维护承诺。Kando 和 Tendedero 的 API 许可证标识均为 `NOASSERTION`，表内结论来自实际 LICENSE 内容，而非把它们误判为无许可。

### 搜索中发现但不作为可复用开源来源的条目

| 条目 | 核查结果 | 处理 |
| --- | --- | --- |
| [taozhiyuai/dropover-mini](https://github.com/taozhiyuai/dropover-mini/blob/master/README.md)（0 stars；最近提交 [2026-06-09](https://github.com/taozhiyuai/dropover-mini/commit/0f87fc7efd8947bd350793bf6a0da67eea6751ff)) | README 自称开源，但 API 未识别许可证，`/license` 未找到许可文件；不能据此认定允许复用。 | 仅参考公开功能描述：拖入、选择、拖出；不复制代码或资源。 |
| [sindresorhus/Actions](https://github.com/sindresorhus/Actions/blob/main/readme.md)（3,065 stars；最近提交 [2025-02-17](https://github.com/sindresorhus/Actions/commit/e2688bb446d6d90fbc10c32b972df7f6d6053ea3)) | 当前 README 明确说明已停止开放源码；本仓库只保留项目说明等内容。 | 不计入当前开源工具的推荐来源，也不建议依赖其历史代码。 |
| [stonerl/Thaw](https://github.com/stonerl/Thaw/blob/main/README.md)（24 stars；最近提交 [2026-10-14](https://github.com/stonerl/Thaw/commit/4f8d117fc1a49ccaf96fc9324a36a1011c4a3a03)) | README 指向 `thaw-app/Thaw`；当前只是迁移入口/更新信息，API 未识别许可证。 | 使用上表新地址核查源码许可和活跃度，不把镜像更新算作源码开发。 |

## 排序后的 10 个功能提案

工作量是单人熟悉项目后的粗估，含四种界面语言及基本端到端验证：**S 约 1–2 人日，M 约 3–5 人日，L 约 6–10 人日**。不是交付承诺；只覆盖下述最小范围，不包含补完所有既有编辑器测试。

以下“无新增权限”均指不要求辅助功能（Accessibility）、屏幕录制（Screen Recording）或完全磁盘访问（Full Disk Access）。仍需普通文件读写权；受保护目录、外置盘、网络卷以及系统版本可能产生文件或剪贴板访问提示，失败时应让用户选择文件/目录，不能索要完全磁盘访问作为默认解决办法。权限结论是针对提案范围的工程评估，尚未通过实际安装验证。

### 1. 从图片提取文字

- **用户行为：** 把图片拖到“提取文字”，或对挂图选择该操作；弹出可校正、可复制的文字预览。第一版支持单张图片、中英文和简单阅读顺序，不做翻译、全屏框选、复杂表格重建。
- **为什么适合：** 截图往往最终是为了引用其中的文字；已有 Vision 文字区域检测可作为基础，新增价值是输出文字。
- **来源：** [PowerToys Text Extractor](https://learn.microsoft.com/en-us/windows/powertoys/text-extractor)、[macshot 的 OCR 功能](https://github.com/sw33tLie/macshot#readme)。后者仅作为交互参考。
- **工作量：M。** 用系统 Vision 识别原图；验证旋转、Retina 截图、混合语言、无文字和识别失败。
- **权限：无新增权限。** 输入是用户交给 Swivel 的图片文件，不读取其他窗口画面。Apple 的 [Vision 说明](https://developer.apple.com/documentation/vision/recognizing-text-in-images)确认文字识别在设备本地运行。

### 2. 暂存架直接拖入、多选和整批拖出

- **用户行为：** 从多个 Finder 窗口分次拖入暂存架，点选/框选若干项，一次拖进邮件、聊天或目标目录；提供逐项移除、全部选择、在 Finder 显示和失效文件提示。移除仅取消暂存，不删除原文件。
- **为什么适合：** 目前已有 Float 入口和基本架子；补足多文件中转就能让它成为日常工作流。第一版只有一个架子、仅收文件/文件夹引用，不引入文本、网页、云端上传或项目目录。
- **来源：** [DropPoint](https://github.com/GameGodS3/DropPoint#readme) 的中转流程，以及 [dropover-mini](https://github.com/taozhiyuai/dropover-mini#readme) 的公开功能说明。两者均独立重写，后者不作为获许可源码来源。
- **工作量：M。** AppKit 原生多项拖放；测试重复文件、失效路径、跨 Spaces、目标应用只收一项时的反馈。
- **权限：无新增权限。** 仅处理用户拖入的 URL；暂存不扫描磁盘，也不申请 Finder 扩展权限。第一版不保证退出后保留引用，持久化另估。

### 3. 保存常用图片处理预设

- **用户行为：** 保存“长边 1600px + JPG 质量 80 + 去除元数据”等预设，显示在轮盘固定位置；多张图片一次处理。原文件不变，输出在各自原文件旁边并避开重名。
- **为什么适合：** 当前有大量转换和编辑能力，但每次重新设参数会打断拖放流程；这里新增的是参数保存与组合执行。第一版只组合缩放、格式/质量和元数据选项，不做任意脚本或复杂流水线。
- **来源：** [PowerToys Image Resizer](https://learn.microsoft.com/en-us/windows/powertoys/image-resizer) 的预设思想、[Kando](https://github.com/kando-menu/kando#readme) 的可配置动作。
- **工作量：M。** 定义版本化预设、稳定动作 ID、尺寸/质量预览；测试透明图转 JPG、EXIF 方向和不同来源目录。不承诺精确压到指定 KB。
- **权限：无新增权限。** 仅使用 ImageIO 等系统框架读写选中的文件。

### 4. 不依赖菜单栏图标的快捷操作面板

- **用户行为：** 可配置快捷键打开 Swivel 自己的面板，搜索功能、打开设置/暂存架/晾衣绳，或选择文件执行操作；支持方向键、回车和 Esc，动作顺序固定，保留现有拖放入口。
- **为什么适合：** 解决刘海挡住图标、选项太多和不方便拖动时的可达性。已有 ⌃⌥T 只是晾衣绳入口，重新打开应用目前只是显示设置。
- **来源：** [Clipy 的 Settings 快捷键](https://github.com/Clipy/Clipy#keyboard-shortcuts)、[Kando 的键盘交互](https://github.com/kando-menu/kando#readme)、[Hidden Bar](https://github.com/dwarvesf/hidden#readme) 暴露的菜单栏拥挤场景。
- **工作量：M。** 复用 Catalog 和系统文件选择器；支持快捷键冲突提示。第一版不读取其他应用的 Finder 选区。
- **权限：无新增权限。** 扩展现有 Carbon 全局热键机制，只操作 Swivel 自己的窗口；不注入键盘事件。注册失败必须提供重开应用的替代入口。

### 5. 截图卡片队列与就近编辑

- **用户行为：** 连续截图时显示待处理数量并可前后切换；卡片上就近提供复制、挂起、标注和收回原目录。悬停/编辑时暂停超时；未处理项最终回到原截图目录。
- **为什么适合：** 连接现有卡片、晾衣绳和编辑器，减少找图再拖一次的操作。旧卡片现在会被收回目录，本提案不是修复“文件丢失”。
- **来源：** [Tendedero](https://github.com/alejandrobujan/tendedero#readme) 的临时承接流程、[macshot](https://github.com/sw33tLie/macshot#readme) 的浮动缩略图动作；队列是结合 Swivel 当前单卡片限制提出的扩展。
- **工作量：L。** 引入有限长度队列、编辑期间的所有权与退出恢复；验证连续五次截图、慢写入、重名、退出、目标目录不可写。需要先完成现有 Apply 按钮的真实操作验证。
- **权限：无新增权限。** 继续消费 macOS 已生成的文件，不自己录屏；保留 `ScreenshotTakeover.restore()` 的关停路径，新增待处理队列不能阻碍恢复。

### 6. 挂图搜索、收藏与安全清理

- **用户行为：** 在挂图列表按文件名/日期查找、收藏常用图；一键收起未收藏项到原截图目录并显示结果，删除操作另走废纸篓。第一版手动清理，不自动删除、不全文 OCR 索引。
- **为什么适合：** 晾衣绳适合短暂放几张图，数量多时需要一个普通列表承接；避免在绳上塞更多缩略图和动画。
- **来源：** [Maccy 的搜索、固定和清理](https://github.com/p0deje/Maccy#readme)。这里只管理 Swivel 已持有的挂图，不扩展成全局文本剪贴板历史。
- **工作量：M。** 收藏是条目属性，不是当前让整根绳保持展开的 `pinned` 标志；需处理文件被外部移走、同名、重启后的标识和收藏恢复。
- **权限：无新增权限。** 不扫描全盘，只枚举现有挂图范围。文件移出目录前先确认成功，不能失败后静默移除列表记录。

### 7. 有预览的批量重命名

- **用户行为：** 拖入多文件，使用查找替换、前后缀、连续编号；先展示旧名→新名和冲突，再明确确认。第一版不做正则、不跨目录移动，提供当前会话撤销。
- **为什么适合：** 给转换或收集完的截图/文件起整齐名字，贴近文件动作菜单。不同于转换，此操作会修改原文件名，界面必须明确说明。
- **来源：** [PowerToys PowerRename](https://learn.microsoft.com/en-us/windows/powertoys/powerrename)、[Loop](https://github.com/MrKai77/Loop#readme) 的执行前预览思想。
- **工作量：L。** 重名、仅大小写变化、交换文件名、只读目录、部分失败和撤销冲突都需处理；第一版遇到重名就阻止，绝不覆盖。撤销不能覆盖后来新建的文件。
- **权限：无新增权限。** 需要用户交入文件所在目录的普通写权限；没有就报告具体项目，不能自动提升权限。

### 8. 收藏“用某应用打开”动作

- **用户行为：** 把 Preview、Photoshop、VS Code 等已安装应用设为固定动作，拖文件即可交给它打开；应用不可用或文件不匹配时明确提示。
- **为什么适合：** 目前动作目录有 AirDrop、复制、移动、Terminal，但没有用户选定的应用入口；挂图也主要按类型打开系统应用。
- **来源：** [Kando](https://github.com/kando-menu/kando#readme) 的自定义启动/文件动作。这里扩展为“将拖入文件交给收藏应用”，不包含脚本执行。
- **工作量：S。** 用系统应用选择和 `NSWorkspace`，先限制三个收藏应用；测试多个文件、应用被移走和关联失败。
- **权限：无新增权限。** [NSWorkspace 的打开文件接口](https://developer.apple.com/documentation/appkit/nsworkspace/open(_:withapplicationat:configuration:completionhandler:))交接文件，不用 AppleScript 控制其他应用，因而此范围不设计 Automation 授权。

### 9. 图片采集的暂停、忽略一次与来源排除

- **用户行为：** 对“挂起复制图片”提供忽略下一次、暂停十分钟/直到恢复，以及排除应用列表；状态清楚可见。截图接管与剪贴板图片采集分别控制。
- **为什么适合：** 已有裸图类型过滤和私密标记跳过，但缺少临时停止入口。适合演示、敏感图像和频繁复制设计稿的场景。
- **来源：** [Maccy 的忽略复制设置](https://github.com/p0deje/Maccy#ignore-copied-items)、[Clipy](https://github.com/Clipy/Clipy#readme) 的排除应用设计。
- **工作量：M。** 忽略下一次指“下一张本会采集的图片”，要在界面说明；保留既有私密/临时类型保护。前台应用不一定是真正的剪贴板来源，来源排除仅作尽力判断，不能作为安全保证。
- **权限：无新增上述三项权限。** 沿用现有剪贴板访问；相关系统访问提示仍取决于 macOS。不能为识别来源而读取其他应用的 UI。手动暂停比自动来源猜测更可靠。

### 10. 批量处理进度、停止及失败清单

- **用户行为：** 显示“正在处理 3/20”、已成功文件和失败原因；可以停止后续任务，只重试失败项，结束后打开输出位置。
- **为什么适合：** 批量转换已有顺序执行，但一个失败容易让用户不清楚前面哪些成功。预设、OCR 批次后尤其需要可见结果。
- **来源：** [PowerToys Image Resizer](https://learn.microsoft.com/en-us/windows/powertoys/image-resizer) 的批处理用途；进度/失败清单的具体方案来自对 Swivel 当前 `Catalog.each` / `Wheel.perform` 的分析，不声称竞品已有完全相同实现。
- **工作量：L。** 第一版只保证停止启动后续文件；正在执行项如不能安全中止，则完成该项后停止。只保留短期结果记录，不保存文件内容；检测半成品并安全清理，不删除已成功输出。
- **权限：无新增权限。** 与对应文件处理相同，不申请后台磁盘扫描权限。

## 不建议做

| 方向 | 原因 |
| --- | --- |
| 在 Swivel 中内置完整 Ice/Thaw 菜单栏管理器 | 管理其他应用图标扩大范围，常涉及辅助功能、画面采集和系统版本兼容成本。先做自己的可靠入口；Hidden Bar 的 MIT 也不意味着集成就没有维护成本。 |
| 加一套窗口平铺/调整窗口系统 | Loop 很适合研究环形交互，但操作其他应用窗口通常需要辅助功能授权，且偏离文件拖放主线。 |
| 自带全屏截图、长截图、录屏和麦克风录音 | 现有系统截图文件已能进入晾衣绳。自建采集涉及屏幕录制权限，麦克风另需授权；长截图还带来滚动控制和拼接复杂度。参见 [Apple ScreenCaptureKit](https://developer.apple.com/documentation/screencapturekit)。 |
| 完整文本/密码剪贴板历史、跨设备同步 | 扩大敏感数据留存范围、存储与同步复杂度；与现有图片工作流的收益不匹配。 |
| 云 OCR、AI 自动上传、生成分享链接 | 破坏默认本地处理体验，增加账号、网络、费用和数据流转问题。先用系统 Vision。 |
| 引入 Electron、第三方媒体工具或插件市场 | 与无第三方依赖规则冲突；任意脚本还会显著扩大执行和配置范围。 |
| 自动删除原图、自动大范围整理磁盘、默认索要完全磁盘访问 | 超出临时承接工具的边界，存在误删或移动用户文件的成本；手动预览、明确选定范围更适合。 |
| 更多菜单皮肤、强弹跳或自动改变常用动作位置 | 已有五种模板；继续增加皮肤价值低于工作流改进。高频变化的布局和更强动画也不符合轻柔、可预测的目标。 |
| 复用 GPL 源码、Tendedero 名称/图标或无许可证仓库的代码 | 与项目既定规则不符。借鉴行为后独立实现；当前报告不构成导入任何源码或资产的许可审计。 |

## 实施前应先补齐的验证

这不是要求本次实现或测试的任务，留给选定功能后的开发阶段：

1. 实际点完编辑器 Apply，确认结果可读、保留原文件、重名不会覆盖。
2. Finder 真实拖放经过 Bookshelf / Sticky Notes / Clothesline 三种模板；再测卡片向上挂图、向外拖出和重启。
3. 点按功能开关与语言选择；检查英语、简体中文、繁體中文、日本語的关键流程。
4. 截图接管前记录 `location` 与 `show-thumbnail`（包括原本未设值的情况），关闭功能/正常退出后逐项对比恢复；异常退出后的下次启动也要单独验证，不把正常退出恢复当作崩溃恢复保证。
5. 多显示器、刘海、全屏 Spaces、快捷键冲突和系统“减少动态效果”设置；不要求当前代码已经全部支持。

文件访问的实现边界参考 [Apple 文件访问文档](https://developer.apple.com/documentation/security/accessing-files-from-the-macos-app-sandbox)：用户选择文件与长期保存访问权是不同问题。若将来启用 App Sandbox 或跨启动持久化外部文件引用，需要重新评估书签与访问权；本报告不假定直接存路径就永远可访问。

## 核查来源快照

下列链接固定到查询时默认分支提交，方便复核当时的功能描述与许可证。星数、是否 archived 和 Release 信息来自对应仓库 API；活跃度判断使用上表提交链接，不依赖搜索摘要。

- p0deje/Maccy：[README](https://github.com/p0deje/Maccy/blob/c700182f638d9d29bfa0aec9dc560422698854c6/README.md) · [LICENSE](https://github.com/p0deje/Maccy/blob/c700182f638d9d29bfa0aec9dc560422698854c6/LICENSE) · [仓库元数据](https://api.github.com/repos/p0deje/Maccy)。
- Clipy/Clipy：[README](https://github.com/Clipy/Clipy/blob/71b0e82623d5c73b4b717d81233eb78db8bbe258/README.md) · [LICENSE](https://github.com/Clipy/Clipy/blob/71b0e82623d5c73b4b717d81233eb78db8bbe258/LICENSE) · [仓库元数据](https://api.github.com/repos/Clipy/Clipy)。
- microsoft/PowerToys：[README](https://github.com/microsoft/PowerToys/blob/5c9fddcddd9f50401cd5ea6f56b8f421e0badd40/README.md) · [LICENSE](https://github.com/microsoft/PowerToys/blob/5c9fddcddd9f50401cd5ea6f56b8f421e0badd40/LICENSE) · [仓库元数据](https://api.github.com/repos/microsoft/PowerToys)。
- alejandrobujan/tendedero：[README](https://github.com/alejandrobujan/tendedero/blob/32a3e47187fdea7b2fd43b3ab5a913f0ed33724a/README.md) · [LICENSE](https://github.com/alejandrobujan/tendedero/blob/32a3e47187fdea7b2fd43b3ab5a913f0ed33724a/LICENSE) · [仓库元数据](https://api.github.com/repos/alejandrobujan/tendedero)。
- kando-menu/kando：[README](https://github.com/kando-menu/kando/blob/5a4340c25a822bfade2960dd5e3bca1682d88fda/README.md) · [LICENSE](https://github.com/kando-menu/kando/blob/5a4340c25a822bfade2960dd5e3bca1682d88fda/LICENSE.md) · [仓库元数据](https://api.github.com/repos/kando-menu/kando)。
- dwarvesf/hidden：[README](https://github.com/dwarvesf/hidden/blob/47937b9d8aede0325ca87a7880af13433e7c5cf8/README.md) · [LICENSE](https://github.com/dwarvesf/hidden/blob/47937b9d8aede0325ca87a7880af13433e7c5cf8/LICENSE) · [仓库元数据](https://api.github.com/repos/dwarvesf/hidden)。
- MrKai77/Loop：[README](https://github.com/MrKai77/Loop/blob/573ace3071641bbe002527ccfc0811842300ccba/README.md) · [LICENSE](https://github.com/MrKai77/Loop/blob/573ace3071641bbe002527ccfc0811842300ccba/LICENSE) · [仓库元数据](https://api.github.com/repos/MrKai77/Loop)。
- GameGodS3/DropPoint：[README](https://github.com/GameGodS3/DropPoint/blob/137789de41104f969ee5c521558c8ee366156a82/README.md) · [LICENSE](https://github.com/GameGodS3/DropPoint/blob/137789de41104f969ee5c521558c8ee366156a82/LICENSE) · [仓库元数据](https://api.github.com/repos/GameGodS3/DropPoint)。
- flameshot-org/flameshot：[README](https://github.com/flameshot-org/flameshot/blob/4bc3acacf0ddafe1782147389988c82698ee8158/README.md) · [LICENSE](https://github.com/flameshot-org/flameshot/blob/4bc3acacf0ddafe1782147389988c82698ee8158/LICENSE) · [仓库元数据](https://api.github.com/repos/flameshot-org/flameshot)。
- sw33tLie/macshot：[README](https://github.com/sw33tLie/macshot/blob/6fdaa848f590b14b08479394576658e982e80bea/README.md) · [LICENSE](https://github.com/sw33tLie/macshot/blob/6fdaa848f590b14b08479394576658e982e80bea/LICENSE) · [仓库元数据](https://api.github.com/repos/sw33tLie/macshot)。
- jordanbaird/Ice：[README](https://github.com/jordanbaird/Ice/blob/11edd39115f3f43a83ae114b5348df6a0e1741cf/README.md) · [LICENSE](https://github.com/jordanbaird/Ice/blob/11edd39115f3f43a83ae114b5348df6a0e1741cf/LICENSE) · [仓库元数据](https://api.github.com/repos/jordanbaird/Ice)。
- thaw-app/Thaw：[README](https://github.com/thaw-app/Thaw/blob/5ab93e49e52d9e3e1a8f91b13906af9e51a47c03/README.md) · [LICENSE](https://github.com/thaw-app/Thaw/blob/5ab93e49e52d9e3e1a8f91b13906af9e51a47c03/LICENSE) · [仓库元数据](https://api.github.com/repos/thaw-app/Thaw)。
