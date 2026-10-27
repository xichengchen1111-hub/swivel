# Windows 功能进度

更新：2026-10-27。Windows当前 **0.104**（0.99之后为0.100–0.103；MSIX版本号不允许1.00，1.0留待真实Windows验收）。0.82补普通ZIP安全解压（B08待做→部分），0.83补复制到文件夹（B06仍部分），0.84补TAR/gzip解压（B08仍部分），0.85补资源管理器文件夹样式（B09待做→部分），0.86补Windows MCP（G02待做→部分），0.87补移除背景/背景虚化（C10待做→部分），0.88补证件照（C10部分→限定实现），0.89补AI图片放大（C11待做→部分），0.90补本机语音转文字（D06仍部分），0.91补字幕烧录进视频（D06仍部分），0.92补按说话压低配乐（D06部分→已实现限定范围），0.93补项目构建文件夹复核后移入回收站及撤销（F05部分→已实现限定范围），0.94补大文件复核后移入回收站及撤销（F06仍部分），0.95补重复文件保留已复核副本后移入回收站及撤销（F04仍部分），0.96补常用目录跨根精确重复的同样清理（F04仍部分），0.97补单文件夹相似文字／图片配对中移走一个文件（F04仍部分），0.98补常用目录跨根相似配对的同样清理（F04部分→已实现限定范围），0.99补临时文件夹中7天未动项目移入回收站（F06仍部分）。0.100补复制结果窗口撤销副本、跨驱动器移动改为复制并保留原文件（B06仍部分）。0.101补ZIP64解压（B08仍部分）。0.102补浏览器与应用默认缓存文件夹在应用关闭时复核后移入回收站及撤销（F06部分→已实现限定范围）。0.103补同一程序路径全部已列窗口的逐个正常关闭请求、缓存窗口环形图（F07仍部分）。0.104补在 Windows 设置中卸载入口和卸载后同名残留文件夹的复核移入回收站（F08仍部分）。0.74–0.81八批开始前分别pull，均Already up to date，Mac仍为1.12.7 / `6cbfdc1`。连续补齐改名大小写／交换／连锁、GIF抖色、二维码直角方向与Mac兼容PNG、挂图长按、正常窗口关闭请求、跨常用目录文字／图片相似比较；0.77补同卷NTFS移动及会话撤销；0.78补明确启动的新到达图片卡片；0.79补明确启动的modifier/主鼠标键手势与非激活Drop入口；0.80补本机可访问非隐藏计划任务的有界只读信息；0.81补显式平面QR数据码字纠错及原生候选展示。固定52项为23/27/0/2，普通剩余27；所属大项仍有下列缺口，真实Windows验收另算。

**固定清单共 52 项：27 项已实现限定范围、23 项部分实现、0 项待实现、2 项平台差异。普通功能还需补齐 23 项，另外 2 项需评估 Windows 替代体验。** 这些是功能项数量，不是工时、剩余批次数或“完整兼容百分比”；不同项工作量差异很大。所有已实现项仍处于预览，Windows 实机验收另算。

| 功能域 | 已实现 | 部分实现 | 待实现 | 平台差异 | 合计 |
| --- | ---: | ---: | ---: | ---: | ---: |
| A 系统交互与工作台 | 4 | 3 | 0 | 2 | 9 |
| B 文件与应用动作 | 5 | 4 | 0 | 0 | 9 |
| C 图片与识别 | 6 | 5 | 0 | 0 | 11 |
| D 音视频与字幕 | 4 | 2 | 0 | 0 | 6 |
| E PDF、文档与二维码 | 1 | 5 | 0 | 0 | 6 |
| F Windows Doctor | 6 | 3 | 0 | 0 | 9 |
| G 助手与 Agent 工具 | 1 | 1 | 0 | 0 | 2 |
| **总计** | **27** | **23** | **0** | **2** | **52** |

计数规则：每行是一个稳定交付范围，ID 后续保留。**已实现**只表示该行明示范围已有实现，不等于 Windows 运行通过或与 Mac 像素一致；**部分实现**表示同一工作流已有入口和实用能力，但表中仍有未补齐范围；**待实现**不表示平台做不到；**平台差异**仅用于目前没有可直接复用的 Apple 系统体验。所有范围均继承 [README 的文件与平台边界](README.md#文件及平台边界)。源码链接用于复核实现，未实现项同时引用 Mac 功能及 [移植清单](PORTING.md)。

## GitHub 保存检查点：0.81

2026-10-25保存当前Windows源码、构建脚本、四语界面、测试与兼容说明。0.81平面二维码数据恢复已完成独立agent check；连续前八批改名／图片／挂图／移动／手势／Doctor／二维码增量均在下表和PORTING保留具体范围。常规核心4,518,659项通过，原生套件仅交叉编译0警告／错误；0.81独立检查114,273项，无未解决确认P1/P2。x64／ARM64便携包已在本地构建并核对ZIP、文档与程序集0.81.0.0；构建产物保持ignored，不放进源码提交。Windows真实执行、界面、文件系统和两架构验收仍未做。

剩余28项按领域归纳如下；这是完整功能项的未完成范围，不代表28个同等大小的小任务：

| 领域 | 剩余项数 | 主要还需完成 |
| --- | ---: | --- |
| 系统交互与工作台 | 3 | 更广文件预览、系统截图／顶边挂图／绳索物理、全局拖放识别与焦点体验 |
| 文件与应用动作 | 4 | 更广批量改名、安全Copy to／跨卷／目录移动、解压与目录归档、Explorer文件夹样式 |
| 图片与识别 | 6 | 扩展格式／动画／HDR、更多元数据保真、完整预设双向迁移、OCR部署验收、抠图／证件照、AI超分 |
| 音视频与字幕 | 3 | 更广降噪、压缩／浮点与视频编辑、语音识别及字幕工作流 |
| PDF、文档与二维码 | 5 | PDF保真页面编辑、更多文档格式／排版、完整二维码库反向迁移、拍照扫码／其他条码、完整3D造型和动画 |
| Windows Doctor | 6 | 重复／相似清理、构建产物清理、缓存清理与撤销、完整应用分组退出、卸载及残留、各分区深诊断与安全修复 |
| Agent工具 | 1 | 完整MCP plan–apply–undo、身份核验及可靠撤销 |
| **合计** | **28** | 详见下方固定ID清单；Windows实机验收另算 |

另有AirDrop、Mac硬件Force Touch两项平台差异，需要Windows替代体验；未实现项不能直接称为平台不兼容。AI／OCR／PDF／媒体等受本地运行时、身份或模型条件影响，具体限制保留在各行。

0.82已交付 **B08普通ZIP解压**（限定范围见B08行）。按用户2026-10-25要求，28项能兼容的全部补齐后再交给朋友在真实Windows上测试；AI类（C10抠图／证件照、C11超分、D06转写）获准打包定版本地模型，经Windows自带DirectML／Windows ML运行。之后继续安全文件操作／清理撤销、原生系统交互及剩余媒体与识别能力。每批继续先pull检查Mac新改动，再实现并独立agent check，通过后commit并push到Windows分支。

## A 系统交互与工作台

| ID | 交付项 | 状态 | 当前范围、剩余内容与依据 |
| --- | --- | --- | --- |
| A01 | 托盘、设置、独立开关与原生导航 | 已实现 | WPF Fluent、系统标题栏、保留页面输入、四语言、独立设置；主题/Narrator/多 DPI 属实机验收。[App](Swivel.Windows/App.cs)、[导航](Swivel.Windows/NavigationShell.cs)、[UI](Swivel.Windows/UI.cs) |
| A02 | 五种 Wheel 模板及外观 | 已实现 | Wheel/List/Bookshelf/Sticky Notes/Clothesline，分页、四配色、温和弹簧及减少动画；全局手势另列 A07，挂绳完整物理列 A05。[模板](Swivel.Windows/MenuPresentation.cs)、[动画](Swivel.Core/MenuMotion.cs)、[配色](Swivel.Core/MenuPalette.cs) |
| A03 | Quick Actions、My Wheel 与快捷键 | 已实现 | 显式文件搜索、Return 执行、稳定动作选择/排序、三组可配置快捷键；助手另列 G01。[动作目录](Swivel.Core/Models.cs)、[快捷键](Swivel.Windows/ShortcutsDialog.cs)、[系统注册](Swivel.Windows/DesktopServices.cs) |
| A04 | File Shelf 与文件预览 | 部分实现 | 会话引用/主窗和浮动架子/多选拖出，图片原只读入口；0.59明确单文件文档提取文字（原预算/全文统计/65536UTF16安全展示/无导出区）及整数PCM WAV完整波形/手动前10秒PCM16试听/Stop/Close，来源祖先持有/取消真join，无后台读取/自动播放。Shelf按钮来源绑定Shelf关闭取消，通用预览按owner及自身来源；新MyWheel ID旧版拒设置风险已标注。文件夹/虚拟云引用、其他格式/系统预览与非激活焦点仍待做。[浮动架子](Swivel.Windows/FloatingShelf.cs)、[主窗](Swivel.Windows/UI.cs)、[文档](Swivel.Windows/DocumentTextDialog.cs)、[音频](Swivel.Windows/WavePreviewDialog.cs)、[范围](PORTING.md#059已交付) |
| A05 | Clothesline 挂图与截图流程 | 部分实现 | 手动最多12图、复制/预览/拖出；0.75补600ms长按预览，单击等待系统双击窗口、长按/双击/拖动不会额外复制；隐藏/刷新/关闭撤销待发手势。0.78在默认关闭的Clothesline下新增明确选择目录、手动Start的本层新增/改名图片路径卡片，单飞有界当前内容缩略图；不初扫/递归/改截图设置/读剪贴板，不证明事件对象或真截图，通知可遗漏；Stop/关窗/关功能/Quit等待清理，无补扫。已有挂图路径提示先移除再挂。系统截图接管、顶边唤出、完整绳索物理未做。[到达状态机](Swivel.Core/ImageArrivals.cs)、[监听](Swivel.Windows/ImageArrivalSession.cs)、[卡片窗口](Swivel.Windows/ImageArrivalWindow.cs)、[挂图窗口](Swivel.Windows/ClotheslineWindow.cs)、[Mac 流程](../Sources/Swivel/Clothesline.swift) |
| A06 | Keep Awake 防休眠 | 已实现 | 30 分钟/1/2/5 小时或手动结束，可保持屏幕；关闭功能/退出释放，真实睡眠恢复待验。[系统服务](Swivel.Windows/DesktopServices.cs) |
| A07 | 全局拖动中按键唤出与焦点交接 | 部分实现 | 0.79在Wheel下明确Start/Stop的会话手势，Ctrl/Shift/Alt新按下+系统主鼠标键，单pending/500ms时效，显示约5秒不激活小型Drop目标；实际FileDrop后异步完整预检再打开普通Wheel供选动作，无自动执行/剪贴板读取/吞键/捕获鼠标/持久自动启用。重复/仅大小写路径整拒，关窗/关Wheel/Quit取消并join。输入组合不证明文件拖动，hook可静默失效；直接拖到动作、完整全局识别和焦点交接仍待补，真实Windows/OLE/DPI/权限/ARM64待验。[核心](Swivel.Core/WheelGesture.cs)、[监听](Swivel.Windows/WheelGestureSession.cs)、[控制窗](Swivel.Windows/WheelGestureWindow.cs)、[Drop目标](Swivel.Windows/GestureDropWindow.cs)、[Mac DragWatcher](../Sources/Swivel/DragWatcher.swift) |
| A08 | AirDrop 体验 | 平台差异 | Apple AirDrop 不能直接移植；Windows 分享/Nearby Sharing 替代入口尚未实现，不称为 AirDrop。[兼容状态](Swivel.Core/Models.cs)、[兼容矩阵](../reports/Windows与macOS兼容调研.md#功能兼容矩阵) |
| A09 | Mac 触控板 Force Touch 反馈 | 平台差异 | Mac 私有触控板执行方式不能直接复用；已有视觉弹簧不代表硬件触感。Windows 硬件替代与系统声音体验仍待评估。[Mac Feel](../Sources/Swivel/Feel.swift)、[兼容状态](Swivel.Core/Models.cs) |

## B 文件与应用动作

| ID | 交付项 | 状态 | 当前范围、剩余内容与依据 |
| --- | --- | --- | --- |
| B01 | 文件引用/路径复制、定位与终端入口 | 已实现 | 明确复制文件引用/路径、Explorer 定位；系统 Command Prompt 只用固定 `/D` 和文件所在工作目录。这里的复制不是复制磁盘文件到新目录，后者列 B06。[动作目录](Swivel.Core/Models.cs)、[Explorer](Swivel.Windows/DesktopServices.cs)、[终端](Swivel.Windows/TerminalLaunch.cs) |
| B02 | 本地 EXE 应用动作 | 已实现 | 与网页共用最多 16 项和稳定 `app.*` ID；扩展名过滤、完整路径确认、独立参数；可选明确复制引用后无文件参数启动，手动粘贴，原有 EXE 配置保持。[模型](Swivel.Core/AppRoute.cs)、[启动](Swivel.Windows/AppLaunch.cs)、[原生配置](Swivel.Windows/AppRouteDialogs.cs) |
| B03 | 已保存网页应用动作 | 已实现 | 0.17 补最多 2048 字符的严格 HTTPS 地址，以及字面 `localhost`/`127.0.0.1`/`[::1]` 的 HTTP；完整预览后打开默认浏览器，0.21另可选可信本地浏览器EXE并保持句柄至启动（单URL参数、无额外开关），可选明确复制本地文件引用。与 EXE 共用 16 项及稳定 ID，设置预算 1 MiB。仅 CF_HDROP 引用，无图像字节/交接卡/草稿；网页不一定支持粘贴，需手动文件选择，不自动上传。[地址规则](Swivel.Core/WebAddress.cs)、[浏览器交接](Swivel.Windows/WebLaunch.cs)、[原生配置](Swivel.Windows/WebRouteDialogs.cs) |
| B04 | Codex / Claude Code 草稿链接 | 已实现 | 0.42默认关闭App Routing下显式本地普通文件、四语言静态只读请求/JSON可见转义、完整预览和明确Copy/Open；12000 UTF8完整草稿/保守2048 ASCII URI（超链接限仍可Copy全文），codex/claude闭集单query，无文件内容/附件/工作区参数/自动发送。注册handler身份/版本/启动、接收app上下文/权限及真实Windows交接仍属验收；Mac窄workspace不自动移植。不是G01内置助手。[草稿核心](Swivel.Core/AssistantDraft.cs)、[受控交接](Swivel.Windows/AssistantDraftSession.cs)、[原生预览](Swivel.Windows/AssistantDraftDialog.cs)、[Mac对照](../Sources/Swivel/AppRouting.swift)、[限制](PORTING.md#尚未完成不能写成平台不兼容) |
| B05 | 批量重命名、回滚与撤销 | 部分实现 | NTFS 普通文件、100 项、预览、不覆盖、失败回滚和 20 批会话撤销；0.74补仅大小写/互换/连锁改名，两阶段同目录非覆盖过渡与回滚/精确恢复路径；Shelf/主窗/Wheel映射按原快照一次更新。临时名称也须满足240字符限制，异常退出可能留下临时名称，会话撤销不跨进程。硬链接/只读及其他文件系统仍缺。[核心](Swivel.Core/BatchRename.cs)、[原生实现](Swivel.Windows/BatchRename.cs) |
| B06 | Copy to / Move to 安全文件操作 | 部分实现 | 0.77独立默认关闭Move Files，明确1–100普通文件、完整路径确认、同卷NTFS按持有句柄移动，不覆盖/不隐式改名；原对象保留所有数据流与下载来源标记、原权限不保证继承目标。全部源/两侧祖先持有，完整卷/文件/目录身份及元数据核验、失败反向回滚/准确当前路径、最多20批会话Undo/显式结果复核；同目录不变，跨卷/硬链/只读/云/链接拒绝，崩溃无持久恢复。仅检查记录元数据，不证明所有流内容/权限未变。0.83补Copy to Folder：1–100文件到本地NTFS，CopyFile2保留全部数据流/Web标记/属性/修改时间，不覆盖（名称 N），按CopyFile2创建对象身份核对流名称与大小、不符经句柄删除，失败/取消保留已完成。0.100补复制结果窗口：逐个副本与原文件、状态；选中副本后核对仍是CopyFile2创建的同一对象、复制后记录的大小/修改时间不变、单链接，再移入回收站撤销复制，窗口打开期间可放回；未验证、带系统等属性或所在盘无回收站的副本不提供并注明，原文件不打开。Move Files在所选文件全部位于另一驱动器时改为复制并保留全部原文件（同Mac“跨卷复制后不删源”），先单独审阅；同盘与他盘混合拒绝。目录树复制/移动、非NTFS目标仍待做。[复制结果](Swivel.Windows/CopyFilesResultDialog.cs)[规则与收据](Swivel.Core/MoveFiles.cs)、[复制](Swivel.Windows/NativeFiles.Copy.cs)、[原生移动](Swivel.Windows/NativeFiles.Move.cs)、[预览结果](Swivel.Windows/MoveFilesDialog.cs) |
| B07 | ZIP/TAR/GZip 创建 | 已实现 | 明确选择普通文件，最多 100 个/总 2 GiB；ZIP/TAR 保存文件名与内容，GZip 单文件。目录树、时间戳、权限/ADS 保真未纳入此限定范围。[归档规则](Swivel.Core/Archives.cs)、[文件会话](Swivel.Windows/FileTools.cs) |
| B08 | 归档解压及目录归档扩展 | 部分实现 | 0.82单个本地NTFS上的ZIP32 Store/Deflate完整普通目录树解压：全量预检名称／重名（大小写与NFC）／链接与特殊项／加密／ZIP64／分卷／数据重叠，10,000项／2GB／32层；新建旁边文件夹不复用、逐文件私有暂存＋大小与CRC-32＋Web标记max(3,源ZoneId)＋独占发布，保留修改时间；非事务，失败／取消保留已出现文件并报告。0.84加TAR（POSIX ustar/pax、GNU长名）、.tar.gz/.tgz及单文件.gz：同一预检／限额／名称规则，拒绝链接／设备／稀疏／分卷／未知类型，跳过已识别macOS `._`属性文件并计数；gzip核对尾部CRC-32/ISIZE，拒截断／多段／附加数据；TAR无文件校验，审查时记录CRC-32，解压时逐文件核对。不还原所有者／权限／扩展属性。0.101补ZIP64：定位记录与56字节ZIP64结束记录须紧接目录之后、结束记录之前，32位字段为标记或与64位值一致，条目0x0001字段只含被标记的值（多、少、重复按损坏拒绝），本地头同规则，数据描述符按64位最短长度参与重叠检查；版本需求放宽到4.5并拒绝补丁数据；限额不变（2GB/10,000项）。7z/RAR解压、加密、文件夹树归档和ADS/ACL还原仍待做。[TAR](Swivel.Core/TarExtract.cs)、[解析](Swivel.Core/ZipExtract.cs)、[发布](Swivel.Windows/NativeFiles.Extract.cs)、[对话框](Swivel.Windows/ZipExtractDialog.cs)、[Mac Documents](../Sources/Swivel/Documents.swift) |
| B09 | Explorer 文件夹 Change Style | 部分实现 | 0.85新增默认关闭Folder Style、显式选择1–20个本地文件夹：10种颜色+可选两字符标记，原创WPF绘制8尺寸PNG图标；文件夹内写入隐藏系统`desktop.ini`（UTF-16，带Swivel标记，逐字节可复原）和`.swivel-folder-<8hex>.ico`，原非只读时设只读并记录。全部经持有句柄暂存+相对改名、不覆盖；已有别人的desktop.ini整夹不动。“恢复默认”只删Swivel自己的两文件、仅在Swivel设过时清只读。Mac 23种材质/图片/彩色emoji、Explorer刷新时机与图标缓存未实现/未验证。[规则](Swivel.Core/FolderStyle.cs)、[写入](Swivel.Windows/NativeFiles.FolderStyle.cs)、[对话框](Swivel.Windows/FolderStyleDialog.cs)、[Mac FolderStyle](../Sources/Swivel/FolderStyle.swift) |

## C 图片与识别

| ID | 交付项 | 状态 | 当前范围、剩余内容与依据 |
| --- | --- | --- | --- |
| C01 | 标准图片转换、裁剪、尺寸与压缩 | 已实现 | 单帧 SDR PNG/JPEG/BMP/TIFF；旋转/翻转/灰度、像素裁剪、多模式缩放、明确放大、JPEG 质量/体积上限。扩展格式列 C02。[图片管线](Swivel.Windows/ImageTools.cs)、[尺寸](Swivel.Core/ResizeOptions.cs)、[原生表单](Swivel.Windows/ImageDialogs.cs) |
| C02 | 扩展格式、动画/HDR 与色彩保真 | 部分实现 | 0.32补GIF87a/89a单画面完整canvas/方形像素输入，原创有界palette/LZW、二值透明/交错行，既有图片工具共享；0.38另补独立静态GIF89a写出，规范化RGB<=256索引含透明、alpha0/255；半透明/超色整拒，无quantization/dither，原件旁独占/批量首错停止，0.39另补受限GIF预设，见C07。[GIF写出](Swivel.Core/GifExport.cs)。动画/偏移/局部canvas/PlainText/ICC及未知扩展明确拒绝。0.47补独立明确有损量化：5bit RGB桶/加权box、16–256条目含透明槽，fullsize准备/<=960nearest预览/同结果保存；无dither/nearestcolor，色带/颜色损失明确、半透明仍拒、低色保RGB，旧writer/预设不改。[量化核心](Swivel.Core/GifPalette.cs)、[源会话](Swivel.Windows/GifPaletteDocument.cs)、[原生预览](Swivel.Windows/GifPaletteDialog.cs)。0.74另补默认off的Floyd–Steinberg蛇形抖色、原palette精确最近RGB/双行误差，透明保留、低色像素不改、旧None输出保持，完整准备结果共享预览/保存。更完整GIF/动画、HEIC/WebP/AVIF、多帧/HDR/高位深/CMYK与完整保真仍未开放，不静默降为SDR。[GIF解码](Swivel.Core/GifImage.cs)、[输入集成](Swivel.Windows/ImageTools.cs)、[文件及平台边界](README.md#文件及平台边界)、[平台验证](PORTING.md#需要专项平台验证) |
| C03 | 照片调节、外观、Auto | 已实现 | 14 项 CPU 调节、8 种外观/强度、原图比较、逐图 Auto；算法与 Mac Core Image 不承诺同像素，真实照片质量另验。[像素算法](Swivel.Core/PhotoAdjust.cs)、[编辑器](Swivel.Windows/PhotoDialog.cs) |
| C04 | 实色遮挡与可编辑标注 | 已实现 | 笔/箭头/形状/文字/编号、Shift 约束、列表再编辑、画布移动/角手柄缩放、20 步撤销，保存平面 PNG。[标注模型](Swivel.Core/Markup.cs)、[拖动](Swivel.Core/MarkDrag.cs)、[窗口](Swivel.Windows/MarkupDialog.cs) |
| C05 | 拼图与画布背景 | 已实现 | Grid/Row/Column/Featured、Fit/Fill，背景/渐变/圆角/阴影；画布背景不等于主体抠图。[拼图](Swivel.Core/Collage.cs)、[画布](Swivel.Core/ImageFrame.cs)、[窗口](Swivel.Windows/CollageDialog.cs) |
| C06 | 元数据查看与清除 | 部分实现 | 常见标签查看/去元数据SDR PNG；0.43补静态PNG文字/EXIF/time移除，0.44补8bit基线/渐进JPEG APP1/APP13/COM移除而不重压缩、只clean原生验证、完整概要及独占旁输出。JPEG32MiB/65536markers/512scans，非ICC APP2/APP11/其他编码等拒；保留ICC/缩略图/unknown可能含私隐，EXIF/APP13移除可能改方向/颜色/显示，非全privacy/HDR/凭证/损坏检测。选择性tag、TIFF及完整保真仍缺。[JPEG核心](Swivel.Core/JpegMetadata.cs)、[JPEG文件会话](Swivel.Windows/JpegMetadataDocument.cs)、[JPEG原生预览](Swivel.Windows/JpegMetadataDialog.cs)、[PNG核心](Swivel.Core/PngMetadata.cs)、[PNG文件会话](Swivel.Windows/PngMetadataDocument.cs)、[PNG预览](Swivel.Windows/PngMetadataDialog.cs)、[既有工具](Swivel.Windows/ImageFrameTools.cs)、[范围说明](README.md) |
| C07 | 图片预设与 Mac 迁移 | 部分实现 | 24 配方、稳定 ID、照片配方/Auto、Windows 备份和兼容 Mac JSON 单向导入；0.39补WindowsGIF配方、后处理最终像素限制、原生固定footer/DPI、完整备份及降级说明，旧四格式和Mac导入闭集保持。Mac HEIC/保留元数据/超限尺寸名称仍逐行标注，跨平台等效与反向迁移未完成；Mac目前无预设JSON导入UI，不能把不可直接使用的JSON宣称为完整迁移。[配方](Swivel.Core/ImageRecipes.cs)、[备份/迁移](Swivel.Core/PresetLibrary.cs) |
| C08 | Retouch 局部修复 | 已实现 | 原创有界 exemplar、笔画/ROI 预算、逐笔撤销、异步取消预览与 PNG；自然照片效果、透明边缘及 Windows 性能仍待验。[算法](Swivel.Core/Retouch.cs)、[窗口](Swivel.Windows/RetouchDialog.cs) |
| C09 | Text Capture 本地 OCR | 部分实现 | 0.50新增独立默认关闭、显式本机语言识别/可编辑/明确复制，白底透明合成，原尺寸超系统边长/262144 UTF16整拒。**需MSIX身份，普通ZIP不可用**；语言质量/布局还原/真实安装执行未验收，保留原件。[源会话](Swivel.Windows/TextCaptureDocument.cs)、[原生窗口](Swivel.Windows/TextCaptureDialog.cs)、[身份与限制](PORTING.md#050已交付) |
| C10 | 抠图、主体背景模糊、证件照 | 已实现 | 0.87：Remove Background／Blur Background（可批量、PNG、原图旁不覆盖、MCP 可用），用户批准的定版 U²-Net ONNX（Apache-2.0 权重/rembg MIT 导出，175,997,641 B、SHA-256 校验后才交给系统 Windows ML，DirectX 失败回退 CPU，本机运行不联网）。前后处理按 rembg 行为自写（Lanczos3、最大值归一、ImageNet 均值/方差、min–max 蒙版），Mac 参考比对 7 张照片蒙版 IoU 0.991–0.9998；虚化按 1−蒙版加权、无主体色晕。320×320 判断，细发丝/近色背景/非显著主体可能不准；0.88 证件照：4 预设+自定义像素/毫米、DPI 72–1200、白/蓝/红/自选底色、Zoom/Vertical 预览，JPEG 写 DPI、原图旁不覆盖；人物蒙版+系统 FaceDetector（单脸），头顶由蒙版、下巴由脸框估计（无五官定位，较 Mac 粗），底色按 (1−α)(新−旧) 替换。人脸检测在各 PC 的可用性、GPU/驱动差异与真实照片质量待验。[核心](Swivel.Core/Cutout.cs)、[证件照](Swivel.Core/IdPhoto.cs)、[模型](Swivel.Windows/CutoutModel.cs)、[来源](ThirdParty/U2Net/UPSTREAM.md)、[Mac Cutout](../Sources/Swivel/Cutout.swift)、[IDPhoto](../Sources/Swivel/IDPhoto.swift) |
| C11 | AI 图片/视频超分 | 部分实现 | 0.89：图片 Upscale…（2×/4×，可批量，原图旁不覆盖，JPEG 保持 JPEG、其他 PNG）。与 Mac 同一官方 Real-ESRGAN x4plus 权重（BSD-3-Clause，SHA-256 固定），构建时由自写 C# 转换器生成确定性 ONNX（IR6/opset11，SHA-256 固定），Mac 上与 PyTorch 同权重比对最大差 0.0014/255；经系统 Windows ML 运行，DirectX 失败回退 CPU。256 块/64 上下文/128 有效中心、镜像外延，与 Mac 几何相同；2× 为 4× 2×2 平均（Mac 为逐块缩小），透明度单独 Lanczos 放大。输入 ≤16 MP、输出 ≤64 MP/16384。视频放大待做；无显卡时只能用处理器，每块约 10–20 秒（CI 2–4 逻辑核实测，为模型本身计算量，已排除读出瓶颈；缩小上下文会出接缝、增大块需数 GB 内存，故不改），对话框说明设备并显示剩余时间；生成式纹理、GPU/驱动差异与真实照片质量待验。[核心](Swivel.Core/Upscale.cs)、[运行](Swivel.Windows/Upscaler.cs)、[转换器](ThirdParty/ModelConvert/RealEsrgan.cs)、[来源](ThirdParty/RealESRGAN/UPSTREAM.md)、[Mac RealESRGAN](../Sources/Swivel/RealESRGAN.swift) |

## D 音视频与字幕

| ID | 交付项 | 状态 | 当前范围、剩余内容与依据 |
| --- | --- | --- | --- |
| D01 | 整数 PCM WAV 编辑与格式转换 | 已实现 | 裁剪/音量/淡入淡出/波形/10 秒试听，采样率/位深/单双声道转换；512 MiB/2 小时上限。0.30补50–200%整数速度且音调联动，保持原rate/channels/bits、完整时长/字节预算和10输出秒试听；Mac保音调变速及压缩/浮点仍列D04。[PCM](Swivel.Core/PcmWave.cs)、[转换](Swivel.Core/WaveFormat.cs)、[速度](Swivel.Core/WaveSpeed.cs)、[会话](Swivel.Windows/WaveTools.cs) |
| D02 | 整数 WAV 背景音乐 | 已实现 | 两源/开始时间/音量/循环/独立fade/保主格式，RMS活动压低见D06；0.46显式0–1000ms线性循环交叉淡化、最近至少2帧/不超音乐半长，周期缩短/相位抵消/响度变化已标注。0.58异采样率先原生完整确认，明确准备完整只读内存匹配率WAV（含头/pad<=128MiB，工作内存额外），保音乐声道/位深，最近帧时长/量化削波可变，显示削波数；同率旧流程保持。试听/循环/ducking/save共用同一结果，原源祖先固定、取消真join/原件旁独占输出。等功率/节拍匹配、压缩音乐/视频配乐与语音分类驱动仍待补。[准备核心](Swivel.Core/WaveMusicRate.cs)、[确认](Swivel.Windows/MusicRateDialog.cs)、[会话](Swivel.Windows/WaveTools.cs)、[循环](Swivel.Core/WaveLoop.cs)、[混音](Swivel.Core/WaveMusic.cs)、[窗口](Swivel.Windows/WaveDialog.cs)、[范围](PORTING.md#058已交付) |
| D03 | 本地音频降噪 | 部分实现 | 整数WAV显式0.05–2秒样本、原创STFT/三种试听；0.57新增明确查找当前长度的固定不重叠窗+尾窗、精确UInt128全声道能量/tie最早，候选独立试听/明确Use，不自动修改/保存，取消单飞真join。安静可能仍有人声/音乐，非纯噪声分类；压缩/浮点/视频降噪、真实录音质量仍待做。[建议](Swivel.Core/WaveQuietSample.cs)、[算法](Swivel.Core/WaveNoise.cs)、[窗口](Swivel.Windows/WaveNoiseDialog.cs)、[范围](PORTING.md#057已交付) |
| D04 | 压缩/浮点媒体及视频编辑管线 | 部分实现 | 0.33补独立IEEE float32/64 RIFF WAV→16/24/32整数PCM、全源finite/峰值/超幅样本数、显式削波、目标量化后10秒试听与完整输出预算；保rate/channels/frames、去metadata、独占旁输出。输入/输出各512MiB/2h，mono/stereo8–192k，原生Fluent表单；直接浮点编辑仍待做。0.34再补AIFF/AIFC NONE/sowt/twos/in24/in32整数PCM→WAV，保每个整数样本/原格式和一致试听，精确整数rate/块及完整预算、其他编码明确拒绝。0.35补整数WAV导出plainAIFF并保每个样本/原格式，54byte头/oddpad完整预算与同源10秒PCM16试听，移除metadata/loops、无AIFC输出。0.37补2–32同格式整数WAV保样本顺序接合、原生完整路径重排/第一源旁输出/整体10秒试听，聚合源与完整目标512MiB/2h，metadata去除/接点可click，压缩/浮点/视频接合待做。[WAV接合](Swivel.Core/WaveJoin.cs)、[接合会话](Swivel.Windows/WaveJoinDocument.cs)、[接合窗口](Swivel.Windows/WaveJoinDialog.cs)、[AIFF写出](Swivel.Core/AiffExport.cs)、[导出会话](Swivel.Windows/WavAiffDocument.cs)、[导出窗口](Swivel.Windows/WavAiffDialog.cs)、[AIFF读取](Swivel.Core/AiffWave.cs)、[AIFF会话](Swivel.Windows/AiffDocument.cs)、[AIFF窗口](Swivel.Windows/AiffDialog.cs)、[浮点转换](Swivel.Core/FloatWave.cs)、[源会话](Swivel.Windows/FloatWaveDocument.cs)、[窗口](Swivel.Windows/FloatWaveDialog.cs)。0.49另补严格RIFF G.711 A-law/mu-law 8bit码字→PCM16，18bytefmt/cb0/单fact帧数/完整pad、mono-stereo8–192k与512MiB输入/完整输出/2h预算，10秒试听同保存前缀。缺fmt18/fact/pad及raw/RTP/Extensible/RF64等拒，不恢复压扩损失/不重采样；其他压缩codec未做。[G.711核心](Swivel.Core/G711Wave.cs)、[源会话](Swivel.Windows/G711Document.cs)、[原生窗口](Swivel.Windows/G711Dialog.cs)。0.50再补系统codec WAV/MP3/ADTS AAC/FLAC完整解码PCM16、128MiB双预算/2h与相同准备结果试听保存，系统可用性/精度/priming/padding限制已标注。M4A/MP4/播放列表仍拒。[系统会话](Swivel.Windows/SystemAudioDocument.cs)、[原生窗口](Swivel.Windows/SystemAudioDialog.cs)。仍缺： 其他媒体解码/转换、视频裁剪/画面/颜色/变速、保音调时间伸缩（Mac0.25×–4×，Windows0.30仅整数WAV50–200%音调联动）、同步预览导出、视频配乐；其他媒体/视频的系统编解码、固定流、取消/输出和长媒体预算待实现；0.50受限音频路径已有。[平台验证](PORTING.md#需要专项平台验证)、[Mac VideoEdit](../Sources/Swivel/VideoEdit.swift) |
| D05 | SRT 字幕编辑 | 已实现 | UTF-8 编辑/校验、偏移/时间速度、SRT/TXT 另存，2 MiB/1 万条；不是语音识别或烧录。[SRT](Swivel.Core/SubRip.cs)、[文件会话](Swivel.Windows/FileTools.cs) |
| D06 | 语音转写、字幕烧录与 speech ducking | 已实现 | 0.90补本机语音转文字 Transcribe Speech…：OpenAI Whisper base（MIT，官方检查点 SHA-256 固定，构建时自写转换为两个确定性 ONNX，Mac 上与 PyTorch 比对 logits 最大差 1e-4），经 Windows ML 运行，DirectX 失败回退 CPU；自动识别（99 种语言，按前 30 秒）/中/英/日，输出 TXT/SRT 于原文件旁不覆盖；频谱/解码/分段按 whisper transcribe() 默认（温度回退、无人声跳过、前文提示），Mac 上对 6 段测试音频与官方输出分段文字和时间完全一致。整数 PCM WAV 自转、其他 WAV/FLAC/MP3/ADTS AAC 经 Windows 解码（≤128 MB），≤1 小时；中文常为繁体、可能听错或在噪声中编造，处理器上可能与录音同长或更久。0.91补 Burn In Subtitles…：MP4/MOV/M4V/WMV 经 Windows.Media.Editing MediaComposition 逐条字幕图片叠加、重新编码 H.264/AAC MP4 于原文件旁（临时文件夹渲染后独占复制），字幕来自视频声音的 Whisper 转写（≤1 小时，可另存 SRT）或 SRT 文件；三档字号、最多三行、重叠缩短、<0.1 s 不显示；≤3840×2160/2 小时/偶数宽高，旋转视频拒绝，HDR→SDR、额外音轨/章节/元数据不保留。0.45另有既有整数WAV配乐的默认关闭RMS活动压低（不识别人声）。0.92在既有 WAV 配乐窗口加“只在有人说话时压低”：Find Speech 用同一 Whisper 在本机转写主音频（≤1 小时），找到的说话区间（<0.5 s 停顿连成一段，按源时间、随裁剪）驱动既有 20 ms 压低/250 ms 恢复包络，不用阈值、不受 10 分钟限制；Whisper 可能漏小声/重叠、把歌声或噪声当说话。与 Mac 差异：Mac 在视频编辑器里给视频配乐并按 SoundAnalysis 人声分类压低，Windows 只在整数 PCM WAV 配乐里；M4A 输入、旋转视频仍未支持（已标注）；真实电脑速度/准确率/编码质量待验。[烧录核心](Swivel.Core/SubtitleBurn.cs)、[烧录](Swivel.Windows/VideoSubtitles.cs)、[烧录窗口](Swivel.Windows/BurnSubtitlesDialog.cs)。[核心](Swivel.Core/Transcribe.cs)、[运行](Swivel.Windows/Transcriber.cs)、[窗口](Swivel.Windows/TranscribeDialog.cs)、[转换器](ThirdParty/ModelConvert/Whisper.cs)、[来源](ThirdParty/Whisper/UPSTREAM.md)、[活动分析](Swivel.Core/WaveDucking.cs)、[Mac Subtitles](../Sources/Swivel/Subtitles.swift)、[Music](../Sources/Swivel/Music.swift) |

## E PDF、文档与二维码

| ID | 交付项 | 状态 | 当前范围、剩余内容与依据 |
| --- | --- | --- | --- |
| E01 | 图片合成 PDF | 已实现 | 1–50 图/总 100 MP、页序/A4/Letter/按图尺寸、边距/预览、RGB 像素 PDF；透明填白，不嵌 OCR/元数据。[PDF 写入](Swivel.Core/ImagePdf.cs)、[原生会话](Swivel.Windows/ImagePdfTools.cs) |
| E02 | 已有 PDF 页面工具 | 部分实现 | 0.50条件性系统渲染、页序/倒序/重复/旋转、合并或逐页栅格PDF/PNG，DIP正确换算纸张点。10源/32MiB每源/128MiB合计/1000页；50选页/16MP每页/100MP合计。**需MSIX身份，普通ZIP不可用**；栅格化丢文字/矢量/交互/metadata/签名，保真编辑及实际安装验收待做。[计划](Swivel.Core/PdfRasterPlan.cs)、[写入](Swivel.Core/PdfRasterExport.cs)、[源会话](Swivel.Windows/PdfRasterDocument.cs)、[窗口](Swivel.Windows/PdfRasterDialog.cs)、[限制](PORTING.md#050已交付) |
| E03 | 文档转换与字数统计 | 部分实现 | 0.20补严格UTF8/BOM UTF16文字与受限DOCX正文XML、Unicode空白段/字素/标量/行/字节统计、原生只读有限预览和全量纯TXT/RTF旁输出；0.22新增纯文字DOCX/静态HTML、明确换行规范及DOCX完整XML预算。8MiB/2M UTF16、XML另有限额；无格式/图像/宏保真，不等同Office渲染。0.25补固定content.xml的受限ODT段落/列表/表格正文、空白折叠与显式符号、明确字段/对象/样式等损失。0.26再补纯ODT旁文件、显式空白/最小3项包和双预算；共五格式。0.27再补Unicode/1252受限RTF正文、显式隐藏/字段/资源省略与编码拒绝；0.36补受限静态HTML正文读取（非DOM/渲染，严格UTF8/配平/实体一次/明确省略与预算），[HTML读取](Swivel.Core/DocumentHtml.cs)。DOC读取、完整HTML/其他格式/排版保真/PDF转换仍缺。[核心](Swivel.Core/DocumentText.cs)、[ODT读取](Swivel.Core/DocumentOdt.cs)、[RTF读取](Swivel.Core/DocumentRtf.cs)、[ODT写出](Swivel.Core/DocumentOdtExport.cs)、[会话](Swivel.Windows/DocumentTextFile.cs)、[窗口](Swivel.Windows/DocumentTextDialog.cs)、[Mac](../Sources/Swivel/Documents.swift) |
| E04 | QR 生成、库与迁移 | 部分实现 | 0.23扩v1–40/L、UTF-8 ECI26/2952字节、32项/分组、1MiB备份和设置；兼容Mac源<=2000byte单向导入。0.24补M级/2330byte、每项级别与备份，Mac导入M且不同级别同名整批冲突；0.28补Q/H、1662/1272bytes和全部四级预览/库/备份，L/M旧输出保持；0.51追加可选花园模板/季节持久化及Mac已知选择，旧默认字节保持、未知逐行标出；完整反向迁移尚缺。[编码](Swivel.Core/QrCode.cs)、[库](Swivel.Core/QrLibrary.cs)、[Mac 导入](Swivel.Core/MacQrLibrary.cs)0.74提供M级≤2000UTF8条目的显式PNG批量导出，可在Mac现有Import Images使用；原名称/分组/场景/日期不迁移、重复/规范等价内容可能合并，仍非完整双向库迁移。[迁移](Swivel.Core/QrMacExport.cs) |
| E05 | 图片扫码与其他条码 | 部分实现 | 清晰平面QR，精确四模块白边／整数近黑白不透明网格，v1–40／L-M-Q-H／四直角方向；numeric／alpha／ASCII或显式ECI26 UTF8。旧严格读取保持；0.81另增明确数据与parity码字恢复，每块≤半数parity，全部结构仍须精确。结果是候选，超界可误纠；显示改动数非原损坏证明。源祖先持有，仅完整预览与明确复制，NUL禁copy避免截断，不自动开链接。照片透视／任意角度／模糊／镜像／结构修复／更多编码／多码与其他条码仍缺。[读取](Swivel.Core/QrFlatRead.cs)、[恢复](Swivel.Core/QrDataRecovery.cs)、[来源](Swivel.Windows/QrReadDocument.cs)、[窗口](Swivel.Windows/QrReadDialog.cs)、[Mac](../Sources/Swivel/QRCodes.swift)、[范围](PORTING.md#081-已实现显式平面二维码数据恢复) |
| E06 | QR Garden / 3D 场景 | 部分实现 | 0.51原创Windows模块浮雕Tree/Bouquet/Island×三季节，原生相机/完整fit/后台单飞生成与关闭等待、全部QR版本/级别；库与备份保存选择、已知Mac选择单向迁移。形状/动画不等同Mac SceneKit，3D扫码/渲染/性能未验收；平面复制/PNG保持。完整Mac造型与动画等仍待做。[模型](Swivel.Core/QrGarden.cs)、[原生场景](Swivel.Windows/QrGardenDialog.cs)、[设置](Swivel.Core/QrEntry.cs)、[备份](Swivel.Core/QrLibrary.cs)、[边界](PORTING.md#051已交付) |

## F Windows Doctor

| ID | 交付项 | 状态 | 当前范围、剩余内容与依据 |
| --- | --- | --- | --- |
| F01 | 本机基础只读快照 | 已实现 | 按钮触发系统/内存/固定盘容量/电源/进程 working set；一次快照，不是泄漏或慢机诊断，不会结束进程。[PCDoctor](Swivel.Windows/PCDoctor.cs)、[事实规则](Swivel.Core/DoctorFacts.cs) |
| F02 | 本地网络/代理快照与设置入口 | 已实现 | IP/DNS/网关/DHCP、当前用户代理标志；地址默认隐藏，四个固定系统设置入口；不联网/执行 PAC，不证明实际可达性。[读取](Swivel.Windows/DoctorNetworkReader.cs)、[报告](Swivel.Core/DoctorReport.cs)、[设置](Swivel.Windows/WindowsSettings.cs) |
| F03 | 只读文件夹空间图 | 已实现 | 根 +4 层/2 万项/约 30 秒软限，只读元数据、Partial/跳过原因；逻辑字节，不是占用/可释放量。[扫描](Swivel.Core/FolderSizes.cs)、[原生空间图](Swivel.Windows/FolderMapDialog.cs) |
| F04 | 重复与相似文件工作流 | 已实现限定范围 | 受限同大小→首尾→全量 SHA-256、硬链接去重、只读分组；0.40另补严格纯文本同扩展名行集合>=80%配对，1MiB/200尝试/16MiB/20K原始行及200K保留行/60秒软限、前1000对、全部跳过理由；100%不证明字节相同，不传递分组。0.41另补受限静态SDR图片的整数9×8明暗网格/64位比较、<=6位差独立配对，低细节/格式/预算明确跳过；16MiB/图、128MiB/200尝试/60秒软限、前1000对。100%不证明图片相同，不是对象识别，不分组。0.69补常用目录明确选择/完整内容确认后的一次跨根精确比较，共享预算、完整volume+file身份去重、失败/partial/未尝试根可见；独立agent发现并修复metadata异常未扣预算P2。[协调](Swivel.Core/CommonDuplicates.cs)、[原生适配](Swivel.Windows/CommonDuplicateReader.cs)、[结果](Swivel.Windows/CommonDuplicateDialogs.cs)。0.76补常用目录明确选择1–6根并再次确认内容读取后的文字／图片相似比较，共享20K/30s元数据与原单次内容预算；根祖先贯穿、身份去重、失败/未尝试可见。[协调](Swivel.Core/CommonSimilarity.cs)、[读取](Swivel.Windows/CommonSimilarityReader.cs)、[原生窗口](Swivel.Windows/CommonSimilarityDialogs.cs)。0.95补单文件夹精确重复组清理：组内多选、至少保留一个，每次移动前重读另一个成员SHA-256并以只读共享锁住到移动结束，被移文件也重算SHA-256、单链接、不同文件身份，复核后移入回收站，窗口内撤销最近一批（共用[回收站会话](Swivel.Windows/RecycleBinSession.cs)）。0.96同一清理扩展到常用目录跨根精确重复：保留副本可在任一所选根、可在另一磁盘，被移文件须在有回收站的固定盘。0.97补单文件夹相似文字／图片配对清理：选中一对后选其中一个文件，保留另一个；两者都须与比较时记录的文件身份、大小、修改时间一致（不重读内容），保留者移动期间只读锁住，且在窗口内不能再经其他配对移走，单链接、复核后移入回收站，可撤销。0.98同一配对清理扩展到常用目录跨根相似文字／图片：键映射到所读根真实路径，保留者可在其他根或磁盘。精确重复与相似配对的单文件夹及跨根清理均已具备，F04按限定范围实现；不重读相似文件内容、仅窗口内撤销最近一批等限制见PORTING。[图片相似核心](Swivel.Core/PhotoSimilarity.cs)、[图片安全读取](Swivel.Windows/DoctorPhotoSimilarity.cs)、[图片配对窗口](Swivel.Windows/PhotoSimilarityDialog.cs)。[相似文本核心](Swivel.Core/TextSimilarity.cs)、[安全读取](Swivel.Windows/DoctorTextSimilarity.cs)、[结果窗口](Swivel.Windows/TextSimilarityDialog.cs)。[Windows 比较](Swivel.Core/DuplicateFiles.cs)、[原生读取](Swivel.Windows/DoctorDuplicates.cs)、[Mac DoctorExplore](../Sources/Swivel/DoctorExplore.swift) |
| F05 | 项目构建产物工作流 | 已实现 | 限定：常见项目目录名称/相邻与内部标记线索、证据/部分结果；0.93 多选后列出完整路径确认，逐个复核（普通本地文件夹、非链接/系统/云端、列出的项目文件仍在），只在有回收站的固定盘移入回收站（过大时由 Windows 询问是否永久删除），窗口打开期间可撤销最近一批（原位置被占用时拒绝）。与 Mac 相同只认已列项目文件；自定义构建路径/未列工具不识别，内容不与扫描比较，Windows 实机未验收。[原生移动/撤销](Swivel.Windows/NativeFiles.ProjectFolders.cs)、[Windows 规则](Swivel.Core/ProjectFolders.cs)、[原生入口](Swivel.Windows/DoctorProjects.cs)、[Mac DoctorExplore](../Sources/Swivel/DoctorExplore.swift) |
| F06 | 存储/大文件/缓存清理与撤销 | 已实现限定范围 | 0.18补显式选一个文件夹、只读元数据的大文件降序列表/十进制阈值与安装包扩展名线索、全部问题/Partial及明确复制，不重扫；不是可释放空间或删除建议。0.65另补两个用户临时/旧WinINET位置选择与只读地图，不是完整缓存分类或可删建议。0.68另补六常用位置预览/明确选择、分根只读地图及失败/partial/重复可见，不合计重叠。[常用位置](Swivel.Core/CommonFolders.cs)、[读取](Swivel.Windows/CommonFolderReader.cs)、[界面](Swivel.Windows/CommonFolderDialogs.cs)。0.69精确比较另补跨常用目录，共享一次预算/不删除；0.76补文字／图片相似比较跨常用目录入口；0.94大文件列表可多选，逐个复核（普通本地文件、大小与记录一致、无其他硬链接）后移入回收站，窗口打开期间可撤销最近一批；与0.93项目文件夹共用同一回收站会话。0.99补临时文件夹清理：测量配置的临时文件夹后列出直接项目中创建、写入、改动时间及其中所有内容都在7天以前的（复制或解压进来的新文件即使修改日期旧也算最近），不预选，移动前文件核对大小/修改时间/单链接、文件夹重新完整测量；Windows只移走部分时如实报告；可撤销。[规则](Swivel.Core/TempCleanup.cs)、[窗口](Swivel.Windows/TempCleanupDialog.cs)。0.102补浏览器与应用缓存：固定清单（Chrome/Edge/Brave/Vivaldi配置文件及User Data着色器缓存，只认带Preferences文件的配置文件夹，与缓存同名但带Preferences的按配置文件；Firefox配置文件cache2/startupCache；VS Code/Discord/Slack的Electron缓存；Steam内置浏览器htmlcache下的Electron缓存子文件夹（htmlcache本身含Cookie不列）；npm `_cacache`；pip/Yarn Cache；NuGet v3-cache/plugins-cache），只读目录记录查找（每文件夹4,096项、每应用64个配置文件、共256个文件夹/20,000项）与共享测量（100,000项/约30秒公平分配，每文件夹≤20,000项、四层，打开成功后才扣预算，不完整大小为下限）；列出查找的应用及检查的程序文件名，未找到/找到但无缓存/读取失败/达到上限分开说明；不预选；为该应用列出的程序都没在运行（含后台与其他用户，node/python/dotnet计入npm/Yarn/pip/NuGet，列不出进程即不动）且仍为固定盘原位置普通本地文件夹时才移入回收站，撤销放回前同样检查。[清单](Swivel.Core/AppCaches.cs)、[窗口](Swivel.Windows/AppCacheDialog.cs)。仍缺：旧WinINET位置清理、Beta/Canary/Insiders等版本、Store应用/便携版/自定义缓存位置、更多应用、Windows磁盘清理类系统项目。[回收站会话](Swivel.Windows/RecycleBinSession.cs)、[核心](Swivel.Core/LargeFiles.cs)、[原生入口](Swivel.Windows/DoctorLargeFiles.cs)、[界面](Swivel.Windows/LargeFilesDialog.cs)、[Mac清理](../Sources/Swivel/DoctorCleanup.swift) |
| F07 | 应用内存分组、正常退出与动态图表 | 部分实现 | 0.19补明确打开的CPU时间占比/物理内存曲线、约2秒单飞采样、相对最后刷新120秒/61点、暂停/最小化/关闭停止与等待、缺失值和恢复基线、明确快照复制。0.72为最新CPU/物理内存读数增加系统色环形百分比；0.73为PC Doctor固定磁盘只读快照添加每卷环形用量，不新增扫描、采样或持久化。磁盘值表示caller可用空间，非物理或可回收量。多processor group的CPU暂不可用；物理内存非应用汇总。0.29补显式进程元数据快照、Ordinal完整报告路径分组、已知工作集小计/未知/4K-PID与10s软限、虚拟双表及路径默认隐藏/明确复制；不同EXE helper归属不猜，工作集非可释放/私有RAM。0.75另补最多64合格同用户同会话窗口、完整标题/EXE审阅、默认Cancel的逐次WM_CLOSE请求；每条一次，重查身份但HWND检查/发送非原子，不强杀、不保证保存/应用退出/释放内存。[窗口会话](Swivel.Core/CloseWindows.cs)、[原生请求](Swivel.Windows/CloseWindowsReader.cs)。0.103补“同一EXE路径全部已列窗口”逐个请求正常关闭（每窗一次、变化跳过、汇总已处理/变化/不确定/未发送，不含helper/对话框/未列出窗口，不强杀、不保证退出或释放内存），以及应用缓存窗口按应用测得大小的系统色环形图与图例（选中行高亮、居中显示所选或总量、部分值标“至少”）。可靠应用/helper关联仍缺。[分组](Swivel.Core/AppMemory.cs)、[进程读取](Swivel.Windows/AppMemoryReader.cs)、[快照窗口](Swivel.Windows/AppMemoryDialog.cs)、[核心](Swivel.Core/UsageMonitor.cs)、[原生读取](Swivel.Windows/UsageReader.cs)、[窗口](Swivel.Windows/UsageMonitorDialog.cs)、[Mac](../Sources/Swivel/DoctorCharts.swift) |
| F08 | 应用卸载及残留 | 部分实现 | 0.31补显式传统桌面安装记录（HKLM/HKCU请求32/64视图）、仅报告名称/版本/发布者、4096条/每字段1024UTF16/10s软限、原生虚拟表及明确复制。子key默认隐藏；视图共享/重定向/注册表链接不核验，不能证明实际安装/架构/归属。0.104补“在设置中打开已安装的应用”（Windows 与应用自己的卸载程序执行，Swivel 不运行卸载命令），以及记录窗口打开后从全部记录中消失、且之前每条记录自己的子键都已不存在的应用名的同名残留文件夹：Local/Roaming/LocalLow/用户 Programs 直下或发布者同名文件夹内，排除共享名、仍记录的名称/发布者、未确认应用之前的名称/发布者及被它们延伸的名称（读不出版本号不影响比较）；不预选、无全选，每批重新读取安装记录、按查找时的规则确认应用名、发布者及所在发布者文件夹都未重新出现，逐项复核为普通本地文件夹且无运行程序位于其中才移入回收站，窗口内可撤销；同一份最初列表可反复回到记录窗口继续卸载与查找。同名仍不能证明归属；Store/MSIX/portable/其他用户/ARM32额外视图、注册表/服务/计划任务残留、真正归属判定仍待做。[残留](Swivel.Core/AppLeftovers.cs)、[核心](Swivel.Core/InstalledApps.cs)、[读取](Swivel.Windows/InstalledAppsReader.cs)、[窗口](Swivel.Windows/InstalledAppsDialog.cs)、[Mac DoctorExplore](../Sources/Swivel/DoctorExplore.swift)、[对齐说明](PORTING.md#mac-医生新功能来源mangodisk2026-10-23供-pc-doctor-对齐) |
| F09 | Windows Doctor 分区与深诊断 | 部分实现 | 0.80新增当前进程权限可读的本机非隐藏计划任务：Name、namespace Path、raw State、可空Enabled和raw32 LastTaskResult；路径默认隐藏贯穿列表/详情/问题/复制，不代表当前用户所有，不读定义/命令/动作/触发器，不运行修改。根加4层/128文件夹/1024task尝试/256Ki文字预算/15秒软限及partial可见，取消等待COM清理和真线程退出。[核心](Swivel.Core/ScheduledTasks.cs)、[读取](Swivel.Windows/ScheduledTaskReader.cs)、[界面](Swivel.Windows/ScheduledTaskDialog.cs)、[范围](PORTING.md#080-已实现计划任务只读元数据)。0.62统一Windows Doctor名称/13症状原生分区，已可用限制和待做逐区显示/浏览不扫描；复用11既有动作和Home-only基本快照，默认关闭/无新读取或清理。[工作台模型](Swivel.Core/WindowsDoctor.cs)、[原生分区](Swivel.Windows/WindowsDoctorPage.cs)。0.63补A–Z已挂载盘符type/FS/只读声明/caller quota容量/volume空闲/分阶段原错误，专用线程抑制插盘错误窗并恢复mode/取消真等；无文件扫描/写盘/eject，不推USB身份或权限，盘符重映射可导致远程系统metadata。[磁盘事实](Swivel.Core/MountedDrives.cs)、[读取](Swivel.Windows/MountedDriveReader.cs)、[结果](Swivel.Windows/MountedDriveDialog.cs)。0.71补StartupApproved只读raw snapshot：固定Run/Run32/StartupFolder×当前用户/本机32/64位视图，12位置/每处256值/每值64B/约10秒，不解释私有数据、不匹配启动项/推启用。[原始快照](Swivel.Core/StartupApprovalSnapshot.cs)、[读取](Swivel.Windows/StartupApprovalReader.cs)、[界面](Swivel.Windows/StartupApprovalDialog.cs)、[范围](PORTING.md#071已交付)。语义关联仍缺；计划任务仅补本行0.80元数据范围。0.70补当前用户/公共Startup路径审阅及明确只读直接子项，共512条/10秒软限，文件名hint不解析目标/状态，不运行/禁用/删除，旧registry/services保持。[启动目录](Swivel.Core/StartupFolders.cs)、[读取](Swivel.Windows/StartupFolderReader.cs)、[界面](Swivel.Windows/StartupFolderDialogs.cs)、[范围](PORTING.md#070已交付)。0.69补跨常用目录精确内容比较，完整确认/共同预算/所有根cleanup/失败和未尝试可见，仍无清理。[范围](PORTING.md#069已交付)。0.68六常用位置按根地图，重定向/保护仍有限、COM与root lease清理、错误继续/取消优先、旧cache/地图保持，无清理/助手上传。[范围](PORTING.md#068已交付)。0.67补本机Win32服务有界单次状态，权限遗漏/partial/错误/原state-type-PID、坏行及预算可见；仅CopyClose，不启停/禁用/配置，不推可信/归属。[服务模型](Swivel.Core/ServiceRecords.cs)、[读取](Swivel.Windows/ServiceReader.cs)、[窗口](Swivel.Windows/ServiceDialog.cs)。0.66独立只读GetPwrCapabilities/S1–S5/S0低功耗空闲/休眠文件type/原byte/失败不填false，76字节现代结构/U1返回；只CopyClose，不保证当前能睡，不请求睡眠/关机或改设置。[能力模型](Swivel.Core/SleepCapabilities.cs)、[读取](Swivel.Windows/SleepCapabilityReader.cs)、[窗口](Swivel.Windows/SleepCapabilityDialog.cs)。0.65再补两个已报告用户缓存位置、先选再只读有界测量、无默认选择/删除判断，13问题区域均有限入口；只读Doctor读取取消后不弹迟到错误，保留非取消错误与成功lease所有权。[缓存范围](Swivel.Core/CacheLocations.cs)、[读取](Swivel.Windows/CacheLocationReader.cs)、[选择](Swivel.Windows/CacheLocationDialog.cs)。0.64再补明确供电/电量/节电/系统估计续航及捕获计划AC-DC熄屏/睡眠/休眠秒值，值/error独立，GUID finally释放/取消真等；hibernate0 adaptive非Never，不推生效或电池健康，不改设置/请求睡眠。[电源事实](Swivel.Core/PowerPlans.cs)、[读取](Swivel.Windows/PowerPlanReader.cs)、[结果](Swivel.Windows/PowerPlanDialog.cs)。0.48显式只读Run/RunOnce四注册表视图/字段状态/默认隐藏命令，不证明启用或可信；0.60再补自选EXE有限PE32/PE32+处理器/子系统/flag/CLR目录声明与最多64证书项头，成功读入<=8536主流头字节，不读取证书正文/运行/验签/查trust/加载器或依赖，unknown原值与证书坏声明partial明确。原来源祖先持有/取消真join/只Copy与Close。StartupApproved原始字节的语义解释及与启动项关联、启动目录目标与实际启用状态/任务/服务配置与安全启停禁用、完整缓存分类与安全清理、签名信任/更完整打不开应用诊断、未挂载/USB身份/写权限/eject/慢机/电池健康/睡眠阻塞-状态排除原因-历史/主动网络测试及修复仍缺。[启动核心](Swivel.Core/StartupRecords.cs)、[读取](Swivel.Windows/StartupReader.cs)、[PE核心](Swivel.Core/PeInspection.cs)、[PE来源](Swivel.Windows/ExecutableStructureFile.cs)、[PE窗口](Swivel.Windows/ExecutableStructureDialog.cs)、[Mac对照](../Sources/Swivel/DoctorSystem.swift)、[范围](PORTING.md#060已交付) |

## Windows Doctor：13个分区的具体进度

目前提供24个正式动作及Home基本快照；多数为有明确边界的只读入口；单窗口关闭另需明确确认，下面不是完整诊断或修复完成率。

| 分区 | 已有入口 | 仍需补齐 |
| --- | --- | --- |
| Summary | 一次系统/内存/磁盘/电源/网络快照 | 完整根因诊断、健康判断 |
| Storage | 单目录地图、大文件、六常用目录地图 | 更完整System Data归属、清理与撤销 |
| Caches | 两个用户临时/旧WinINET位置地图 | 完整缓存分类、安全清理与撤销 |
| Folder Sizes | 有界只读元数据空间图 | 受保护/长路径/云等更广范围与真实Windows验收 |
| Duplicate & Similar Files | 单目录及跨常用目录精确/图片/文字比较 | 保留副本复核清理及撤销 |
| Project Builds | 有界项目构建目录线索 | 更完整配置/自定义路径、清理及撤销 |
| Slow or Hot | CPU/物理内存曲线与环图、固定盘环图、EXE工作集分组、单窗口正常关闭请求 | helper归属、整应用退出、缓存环图、温度与慢因 |
| Apps That Will Not Open | EXE头部声明 | 完整签名信任、依赖/SmartScreen/修复 |
| Uninstall Apps | 传统桌面安装记录 | Store/MSIX等、真实卸载、残留归属及恢复 |
| External Drives | 已挂载盘符类型/文件系统/容量声明 | 未挂载/USB身份、写权限诊断、安全弹出 |
| Network | 网卡IP/DNS/网关/代理配置 | 实际可达性、应用有效代理及修复 |
| Background Items | Run/RunOnce注册表、服务状态、0.70启动文件夹直接条目、0.71 StartupApproved原始注册表快照、0.80计划任务5字段元数据 | StartupApproved字节解释/与启动项核对、计划任务定义及实际启用状态关联、安全禁用移除 |
| Battery & Sleep | 电源/计划时间值、系统睡眠能力 | 电池健康、睡眠阻塞/排除原因/历史 |

## G 助手与 Agent 工具

| ID | 交付项 | 状态 | 当前范围、剩余内容与依据 |
| --- | --- | --- | --- |
| G01 | 可选 Assistant 与 Doctor 报告解释 | 已实现 | 限定：默认关闭/服务隔离凭据/完整请求审阅；动作建议只选行手动Return、自定义动作标签逐次允许；基本/九静态汇总/冻结monitor解释（保持暂停至Resume）；四来源Doctor标签默认off逐条多选1–40、无topN/截短、换选重审、双32KiB整拒，标签可含私人文字但不赋动作/修复权。更完整诊断列F09、真实Windows/服务验收另计。[设置](Swivel.Core/AssistantSettings.cs)、[计划](Swivel.Core/AssistantPlan.cs)、[脱敏](Swivel.Core/DoctorAssistantReport.cs)、[汇总](Swivel.Core/DoctorAssistantSummary.cs)、[标签](Swivel.Core/DoctorLabels.cs)、[会话](Swivel.Windows/DoctorAssistantSession.cs)、[监控](Swivel.Windows/UsageMonitorDialog.cs)、[限制](PORTING.md#056已交付) |
| G02 | Windows MCP / Agent plan–apply–undo | 部分 | 0.86：`Swivel.exe --mcp`（MSIX 别名 `swivel-preview.exe`）仅 stdio JSON-RPC，Agent Tools 默认关闭；list_actions/inspect_files/plan_action/apply_plan/undo/history。只开放在原图旁新建文件的图片转换/旋转/翻转/灰度；计划一次性、1 小时、输入身份/大小/时间变化即拒绝；每次应用记入 HKCU 历史，undo 只把未改动的输出移入固定盘回收站。缺：OCR/读码/目标大小/改名/移动等工具、MCPB 打包、真实 Claude/Codex 主机验证。[Mac MCPTools](../Sources/Swivel/MCPTools.swift)、[Mcp.cs](Swivel.Core/Mcp.cs)、[McpHost.cs](Swivel.Windows/McpHost.cs) |

## Mac 新增功能差距与本次同步

0.38先pull返回Already up to date（当时origin/main b9d6020/Mac1.12.3）。本地并发Mac随后提交7bf2fff/1.12.4安全修复：TAR的@名字及解压外链、草稿宽目录限制、Clothesline有界正规图片、similar keeper/卸载共享identifier及MCP16并发；实际baseline HEAD为7bf2fff。后续63685ed中性fixture及de1f7f8/ba54ebc归档链接/同描述符读取/MCP旧ID兼容等并发修复已核对，差异原样保留并外置记录；Windows未写/运行/构建/安装Mac，不能声称Mac文件全部未变。0.17已经核对相对`2fc19d1`的新增提交，涵盖 Mac Doctor 的存储/内存清理、图表、重复/相似文件、项目构建产物和卸载；[提交](https://github.com/xichengchen1111-hub/swivel/commit/bcb7bb92930dbf04aaddb710b74a1c819d42ff39)可复核新增范围。

- 已对齐受限只读版本：空间图 F03、精确重复查找 F04、项目目录线索 F05。
- 继续跟踪差距：相似图片完整覆盖与安全删除 F04、大文件/缓存与回收站撤销 F06、可靠应用/helper分组 F07、卸载/残留 F08。
- 0.93补F05项目构建文件夹复核后移入回收站与窗口内撤销。
- 0.94补F06大文件复核后移入回收站与窗口内撤销；缓存规则/清理仍待做。
- 0.95补F04单文件夹精确重复组保留已复核副本后移入回收站与窗口内撤销；常用目录与相似配对清理仍待做。
- 0.18补F06的只读大文件/安装包名称线索；清理和可靠撤销仍待做。
- 0.19补F07 CPU/物理内存曲线，0.29再补报告EXE路径的只读工作集分组；0.72–0.75补CPU/内存/固定盘环图与单窗口关闭请求；可靠helper归属、整应用退出与缓存环图仍待做；0.103补同一EXE全部已列窗口逐个关闭请求与缓存环图，可靠helper归属仍待做。
- 0.41补F04受限相似图片独立配对，格式/低细节/漏检及100%非图片身份已标注；0.76已补常用目录批量入口；清理/撤销仍待做。
- 0.40补F04只读相似文本配对；不等于字节重复或图片相似，0.76已补常用目录批量入口；删除/撤销仍待做。
- 0.31补F08传统桌面安装记录清单；真正卸载/残留归属、MSIX等来源与可靠撤销仍待做。
- 0.104补F08在设置中卸载入口与卸载后同名残留文件夹（不预选、运行程序检查、回收站撤销）；残留归属判定、注册表/服务/计划任务残留、MSIX等来源仍待做。
- MangoDisk 仅提供功能思路；其 GPL-3.0 代码、规则库、素材未采用。Windows 后续继续独立实现。Mac 源码/构建/安装不属于 Windows 批次的编辑范围。

## 验收与发行进度（独立于 52 项功能计数）

| 验收层 | 当前状态 | 还需完成 |
| --- | --- | --- |
| 跨平台核心规则执行 | 0.80 为 4,510,958 项常规核心检查通过 | 每批对新增算法/规则运行对应检查；结果见 [TESTING](TESTING.md) |
| 独立 agent check | 0.1–0.80分批复核；本批具体证据见TESTING | 每批继续独立复核，不替代真实Windows运行 |
| Windows 项目交叉编译与便携包 | 0.81应用/原生检查与两架构包实际状态以TESTING及外置 BUILD-REPORT-0.81.0.json 为准 | 交叉编译及便携包不能当Windows运行通过 |
| Windows x64 原生测试 | **尚未执行** | 文件锁/身份、编码、系统 API、取消/退出、失败清理/不覆盖 |
| Windows ARM64 原生测试 | **尚未执行** | 真实 ARM64 执行及代表性人工用例，不能以 x64 或仿真结果替代 |
| Windows 真实界面与外部集成 | **尚未执行** | 拖放/焦点/热键、主题/高对比/Narrator/多 DPI/四语言；播放/音质、扫码/打印、外部应用/浏览器及 Windows 设置 |
| 正式发行 | **未完成** | 上述实机验收、正式签名与可信分发；当前仍是预览包，不因功能项减少就自动转正式版 |

0.77降级提醒：新的 `FileMoves` 启用值和保存到 My Wheel 的 `file.move` ID，都可能使旧版整份设置读取失败、恢复默认并在后续保存时覆盖原值；降级前保留升级前设置备份。设置版本号未变不代表旧版可读取新枚举或动作。

完整验收条件以 [TESTING.md](TESTING.md#release-gates) 为准。没有 Windows 执行证据之前，不能把任何行的“已实现”读成“实机验收完成”。后续更新本表时只调整确实交付的行及总计，并保留限制；新增 Mac 能力另记录，避免范围变化被隐去。


0.45验证：220513常规核心检查通过；独立agent最终证据见[TESTING](TESTING.md)。原生套件只交叉编译，未执行Windows/听感/性能/GUI/FS/ARM64验收；D06只是音量活动替代部分，不等于完整语音支持。


0.46验证：224647常规核心检查通过；独立agent570,766断言（563,788.NET+6,978Python）、21翻译/占位和206文件链接（本表179）通过，最终无未解决确定P1/P2，证据见[TESTING](TESTING.md)。原生仅编译，Windows真实听感/播放/性能/FS/WPF/ARM64仍待验收；D02新增循环接点增强，52项21/16/13/2、普通剩余29不变。


0.47验证：369902常规核心通过；独立agent288,976断言（5,221.NET+283,755Python）、57翻译/占位和211文件链接（本表183）通过，新增原生异步测试隔离P2已修复并完成源码/编译复核，最终无未解决确定P1/P2，证据见[TESTING](TESTING.md)。原生仅编译，Windows视觉/性能/内存/FS/WPF/ARM64仍待验收。C02补显式GIF减色，52项21/16/13/2、普通29不变，新MyWheel动作的降级备份风险已标注。

0.48验证：370067常规核心通过；独立agent297,617断言（297,381.NET+236Python）、93翻译/占位和217文件链接（本表186）通过，另29原生/UI源码不变量单列，最终无未解决确定P1/P2，证据见[TESTING](TESTING.md)。原生仅编译0warning/error，Windows注册表/句柄/剪贴板/GUI/DPI/Narrator/ARM64仍未执行。F09仅只读登录配置转部分，52项21/17/12/2、普通剩余29；StartupApproved/启动目录/任务/服务及实际启停/修复未做，旧版新动作降级风险已标注。

0.49验证：542659常规核心通过；独立agent5,824断言（3,892.NET+1,932Python）、42翻译/占位和224文件链接（本表190）通过，另30项原生/UI源码不变量单列，最终无未解决确定P1/P2，证据见[TESTING](TESTING.md)；原生0warning/error仅交叉编译，Windows播放/FS/GUI/DPI/Narrator/ARM64未执行。D04补严格G.711显式解码入口，其他压缩codec/直接编码编辑/保音高处理及视频仍待做；52项21/17/12/2、普通29不变。96旧ZIP与122Mac基线保留，新MyWheel动作降级备份风险已标注。

0.50：C09/E02由待做转为需MSIX身份的条件部分，普通ZIP仍不可用；D04系统codec音频解码增强。52项21/19/10/2、普通29，另2平台替代。Windows实机/签名安装验收未做，详见[TESTING](TESTING.md)。

0.51：E06新增原创Windows三维浮雕而转部分，仍待Mac完整形状/动画及Windows显示/扫码验收；52项21/20/9/2普通29。0.50PDF/OCR需MSIX身份，普通ZIP仍不可用，签名安装及全部原生验收未做。
