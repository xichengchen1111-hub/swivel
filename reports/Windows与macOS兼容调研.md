# Swivel：Windows 与 macOS 兼容调研及分批实现

日期：2026-10-23。基线：本地 `codex/media-batch2`，macOS 1.9.0（`f95e657`）。本次目标是先做可落地的 Windows 版本，把差异明确列出，继续分批补齐；不以完整兼容作为第一批的前置条件。

**当前阶段：0.81 多批功能扩展预览。** 原生Windows执行仍未完成，全部剩余项目见[执行清单](../Windows/PORTING.md)。下文首批/第二批记录是历史；矩阵包含后续0.7–0.81增量范围，固定52项剩余进度见[进度表](../Windows/PROGRESS.md)。

0.81补明确平面QR数据恢复：四级／40版本／四直角方向，数据和parity码字有限纠错，边框及功能模块仍须精确。旧strict不自动fallback；原生窗口标明候选／改动数，超界可能误纠到不同有效文字，不证明原内容。仅展示与明确复制；含NUL禁止Copy避免Windows剪贴板截断，完整转义预览保留。E05仍部分、普通剩余28，核心4,518,659通过／native仅编译，独立检查见[TESTING](../Windows/TESTING.md#081-验证记录)。本批pull无新Mac，Mac1.12.7／6cbfdc1保持，真实Windows／ARM64仍未验。

0.80补Windows Doctor计划任务信息：明确一次读取本机当前token可访问的非隐藏任务名称、命名空间路径、原state／Enabled／LastTaskResult，路径默认隐藏贯穿显示和复制；不推账户归属、健康或实际执行，不读定义／脚本／动作／触发器，不运行或修改任务。根加4层／128文件夹／1024条目尝试／256Ki文字预算／15秒软限及部分结果可见，取消等待COM清理与实际线程退出。F09仍部分，固定52项22／23／5／2、普通剩余28；真实Windows和ARM64仍未验收，详细证据见[TESTING](../Windows/TESTING.md#080-验证记录)。本批pull仍无新Mac，保留1.12.7／6cbfdc1及原Mac实现。

0.79补明确启动的Ctrl/Shift/Alt＋主鼠标键会话手势，非激活原生Drop目标约5秒；实际接收1–100 FileDrop引用后，回调外完整本地预检才打开普通Wheel，用户自己选择动作。不能证明全局文件拖动；系统可能静默移除hook，权限/远程/焦点/DPI/OLE均仍待实机。无剪贴板/文字记录/自动动作/自动启用，Stop/关闭/禁用/退出等待线程及文件检查。A07转部分，固定52项22限定实现／23部分／5待做／2平台差异，普通剩余28，详细验证见[TESTING](../Windows/TESTING.md)。本批先pull无新Mac1.12.7／6cbfdc1。

0.77 新增独立默认关闭的同卷 NTFS 文件移动与最多20批会话撤销；Copy to、跨卷和目录树仍待做。固定52项为22限定实现／22部分／6待做／2平台差异，普通剩余28。常规核心4,509,046通过，原生仅交叉编译0警告／错误；最终0.77.1两架构包的实际构建／校验状态以外置 `build/windows/BUILD-REPORT-0.77.1.json` 为准，0.77.0为内部中间产物。

0.78补新到达图片卡片：在Clothesline下明确选择本地目录并Start，单飞读取此后本层新增/改名图片路径的当前内容，包含旧图改名及路径替换，不声称识别真截图或事件对象。无初始扫描/递归/补扫/剪贴板/系统截图设置更改；12卡/64队列/512通知/4次尝试/512MiB按源长度预扣（非实际I/O硬限），普通单文件失败可见并继续，限额/监听或内部故障停止可见。原生窗口Stop/Close/关功能/Quit等待清理，Hang保留既有动作/同路径提示，卡片会话内有效。固定52项22/22/6/2、普通28，A05仍部分；系统截图接管/顶边唤出/完整物理继续待做，实机验收另算。本批先pull无新Mac，Mac1.12.7／6cbfdc1保持；[实现与验证](../Windows/PORTING.md)。

## 结论

Swivel 可以做 Windows 版，但不是把现有 Swift 应用换一个编译目标。当前 77 个 Swift 源文件大量使用 AppKit、Vision、Core Image、AVFoundation、PDFKit、Speech、Core ML、IOKit、Carbon 等 Apple 平台能力。可以迁移的是功能设计、文件安全规则、动作 ID、参数、纯算法和测试样例；系统集成及 UI 需要独立实现。

本次选择 **C# / .NET 10 + WPF + Win32**，放在 `Windows/` 下，保留现有 Mac 代码、构建和安装方式。WPF 是微软维护的 Windows 桌面框架，支持系统控件、向量绘制和 .NET API，但只能在 Windows 运行；因此两端原生界面分别维护，不把 Mac UI 换成跨平台壳。[微软 WPF 概览](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/)

首批是明确标记的 **0.1 Windows Preview**：托盘、显式文件拖入/选择、简化快捷轮盘、图片转换/缩小/旋转、复制文件/路径、Explorer 定位、会话文件架子、快捷搜索、防休眠、独立开关、四语言及兼容页面。视频、AI、系统级截图/拖拽及诊断继续分批做。

0.43历史批次：新增受限静态PNG文字/EXIF/时间块移除且不重压缩IDAT，保留色彩/未知块、EXIF方向变化及非完整隐私清除明确标注。93820常规核心通过，原生仅交叉编译；最终独立agent记录见[TESTING](../Windows/TESTING.md)。52项21限定实现/15部分/14待做/2平台差异，普通剩余29（C06仍partial）；84旧ZIP保持。开始pull已同步到5b83c0c/Mac1.12.6，无相对0.42新Mac实现；本批不修改/运行Mac。

0.73历史批次：在0.72 CPU/物理内存环形读数后，为 PC Doctor 固定卷容量快照增加静态磁盘用量环图；只用现有单次 GetDiskFreeSpaceEx 结果，不扫描、不加历史或助手字段。caller quota可影响容量，环图不表示物理占用或可回收空间。4,436,288项核心检查通过，native仅交叉编译；52项22/21/7/2，普通剩余28。批次前GitHub pull Already up to date，Mac保持1.12.7提交6cbfdc1；本批未改Mac源码。

历史Windows0.69：常用目录明确选择/完整内容确认，共享预算跨根精确查重；重叠硬链接去重、失败/partial/未尝试可见，无删除清理。4436105核心/native仅编译；52项22/21/7/2普通28，122Mac136旧ZIP保持，pull无新Mac。

历史Windows0.68：六常用位置先预览/无默认选择，1–6分根串行历史元数据图、失败/partial/精确重复可见，不合计重叠或放宽保护。4435981核心/native仅编译，F06F09partial/52项22/21/7/2普通28；122Mac134旧ZIP保持，pull无新Mac。

历史Windows0.67：Windows Doctor新增本机Win32服务有界单次状态/名称/原type-state-PID/全部问题，权限可能静默遗漏、成功空不代表完整；只CopyClose，无服务启停禁用配置/文件扫描/命令。4435874核心/native仅编译，F09partial/52项22/21/7/2普通28；122Mac132旧ZIP保持，pull无新Mac。

历史Windows0.66：Windows Doctor新增单独睡眠能力快照，12原flag与休眠文件type/未知error、现代76byte/U1互操作；报告支持不保证当前能睡/唤醒，无睡眠关机/设置改变/命令。4435778核心/native仅编译，F09partial/52项22/21/7/2普通28；122Mac130旧ZIP保持，pull无新Mac。

历史Windows0.65：Windows Doctor两个当前用户临时/旧WinINET位置先选再有限只读测量，13问题区域均有有限只读入口但清理/卸载/撤销仍待做；修复只读Doctor取消后迟到错误，保留非取消异常/成功lease。4435209核心/native仅编译，F09partial/52项22/21/7/2普通28；122Mac128旧ZIP保持，pull无新Mac。

历史Windows0.64：Windows Doctor新增供电/电量/节电/估计续航与捕获计划AC-DC熄屏/睡眠/休眠秒值，hibernate0 adaptive非Never，原值/error/Guid清理明确。只读完整CopyClose，不改设置/请求睡眠/推health或生效策略；4435157核心/native仅编译，F09partial/52项22/21/7/2普通28；122Mac126旧ZIP保持，pull无新Mac。

历史Windows0.63：Windows Doctor新增明确已挂载盘符metadata，type/FS/readonly声明/quota-aware容量/分阶段错误原值，专用线程取消清理真等；不读取文件/写盘/eject，不冒充USB身份或写权限。盘符重映射/系统provider可能远程metadata，明确非原子；4435097核心/native仅编译，F09partial/52项22/21/7/2普通28；122Mac124旧ZIP保持，pull无新Mac。

历史Windows0.62：按用户要求适配Windows Doctor，13症状原生分区/逐区限制与缺项/默认关闭/明确只读操作，浏览不扫描。复用11动作和Home-only快照/原设置与保护，无新读取/清理/卸载。入口P1已修复并独立复核见TESTING；4434979核心/native仅编译，F09仍partial/52项22/21/7/2普通28；122Mac122旧ZIP保持，pull无新Mac。

历史Windows0.61：明确本地清晰正向平面QR读取，v1–40四级/精确四模块边框/整数近黑白不透明网格，严格parity不修复/numeric-alpha-ASCII或ECI26UTF8/只读文字和手动复制，不自动打开链接。照片旋转容错其他编码条码仍待做。4434908核心/native仅编译，E05转partial/52项22/21/7/2普通28；122Mac120旧ZIP保持，pull无新Mac。

历史Windows0.60：Doctor明确选EXE有限PE头部诊断，原架构/子系统/CLR声明及最多64证书头数值/partial，只读<=8536成功头字节、不执行/验证trust/判断能运行。4434529核心/native仅编译，F09仍partial/52项22/20/8/2普通28；122Mac118旧ZIP保持，pull无新Mac。

历史Windows0.59：Shelf明确单文件文档文字/整数WAV只读预览，无Save/自动播放，来源绑定Shelf取消/真join、旧Inspect/图片和编辑流程保持；新MyWheel ID降级风险已标注。4434087核心/native仅编译，A04部分/52项22/20/8/2普通28不变，122Mac116旧ZIP保持，pull无新Mac。

历史Windows0.58：整数WAV异采样率配乐先明确完整确认，再准备匹配率只读内存WAV（完整128MiB/工作内存额外），保音乐声道位深，显示量化削波；主格式不变/同率旧路径保持/同一结果试听保存。4433990核心/native仅编译，52项22/20/8/2普通28不变；122Mac114旧ZIP保持，pull无新Mac。

历史Windows0.57：整数WAV降噪明确查找安静候选、试听后明确Use，精确全声道能量/固定窗+尾窗，非纯噪声识别；不采用时手动输出保持。4433582核心/native仅编译，D03仍部分，52项22/20/8/2普通28；122Mac112旧ZIP保持，pull无新Mac。

历史Windows0.56：四来源已读Doctor标签默认off，用户逐条多选1–40并完整重审，超量/双32KiB整拒保留选择，不自动topN。标签自身可能私人，scope明确；模型只解释无修复。4432524核心通过，G01限定实现，52项22/20/8/2普通28；F09完整诊断/真实Windows另计。122Mac/110旧ZIP保持，pull无新Mac，无Mac写入/运行。

历史Windows0.55：冻结monitor最新一条CPU/物理内存采样解释，不发送history/名字路径，先暂停/完整审阅，关闭后明确Resume才恢复；旧UI代次的成功/Expected失败都隔离。4432466核心通过，52项21/21/8/2普通29，可选Doctor名称继续；122Mac/108旧ZIP保持，pull无新Mac，无Mac写入/运行。

历史Windows0.54：九个静态Doctor页加入完整记录的immutable脱敏汇总解释，只有数量/已知字节/未知和限额，不含名字路径内容hash/命令，先完整审阅后发送，模型不执行。4432438核心通过，52项21/21/8/2普通29，实时monitor解释/可选Doctor名称继续。122Mac/106旧ZIP保持，pull无新Mac，无Mac写入/运行。

历史Windows0.53：基本Doctor精确白名单脱敏快照、单独完整报告和请求审阅后解释，模型无动作/修复；逐请求明确勾选才发自定义应用/预设名称。双32KiB整拒、旧默认请求字节保持，更多Doctor报告/真实Windows验收继续。4432386核心通过，52项仍21/21/8/2普通29；122Mac/104旧ZIP保持，无Mac写入/运行。

历史Windows0.52：默认关闭内置Assistant、完整JSON/目标地址预览、服务隔离Windows凭据；只发用户输入/固定内置动作/文件类型计数，不自动执行，手动Return。无redirect/cookie/proxy/Windows集成认证/自动retry，明确协议与容量限制，Doctor解释与真实Windows/服务验收待补。4432330核心通过，独立见[TESTING](../Windows/TESTING.md)。G01部分，52项21/21/8/2普通29；122Mac/102旧ZIP保持，pull仍c2b72a7/Mac1.12.6(34)，无Mac写入或执行。

历史Windows0.51：原创三模板/三季节QR Garden浮雕、原生相机完整fit、后台单飞与关闭等待，库/备份选择保存和已知Mac选择单向迁移。平面QR全部版本/级别/复制PNG不变；非Mac完整SceneKit造型/动画，实际Windows渲染/扫码/性能未验收。4432151核心通过，原生仅编译；相机宽屏裁切P2已修复，独立见[TESTING](../Windows/TESTING.md)。E06转部分，52项21/20/9/2普通29，仍不称整体完成。100旧ZIP/122Mac保持，先pull仍c2b72a7/Mac1.12.6(34)，无Mac写入或执行。<=0.50可能重写设置丢新选择，并拒绝新字段备份，降级保留升级前备份。

历史Windows0.50：系统音频codec完整PCM16准备及一致试听/独占保存；条件性的已有PDF栅格页面排序/旋转/合并/拆分/PNG，独立默认关闭本地OCR识别/编辑/明确复制。**PDF/OCR需实际MSIX身份，普通ZIP明确不可用**；可选Windows SDK MakeAppx构建脚本未运行/签名/安装，不能当成可用MSIX。新增微软SDK.NET.Ref10.0.22000.57两DLL并附SDK/REDIST/CsWinRT许可，.NETruntime10.0.13保持；不加第三方应用/模型。DIP转PDF点的P2已修复，553971核心通过，原生仅编译，独立见[TESTING](../Windows/TESTING.md)。C09/E02转条件部分，52项21/19/10/2普通仍29，Windows实机/API/播放/OCR/PDF/GUI/ARM64未执行。98旧ZIP/122Mac保留，先pull仍c2b72a7/Mac1.12.6(34)，本批无Mac写入/运行/构建/安装。新Feature/动作ID的<=0.49整设置拒绝/默认/覆盖风险需降级备份。

历史Windows0.49：严格G.711 A-law/mu-law RIFF WAV→PCM16，原生Fluent格式/完整限制/明确试听和原件旁独占保存，前10秒样本与保存一致。fmt18/cb0/单fact帧数/完整pad，缺结构exporter变体明确拒绝；源与完整PCM输出各512MiB/2h，保留rate/channels不恢复有损细节。542659核心通过，原生仅编译，独立见[TESTING](../Windows/TESTING.md)。D04仍partial，52项21/17/12/2普通29，其他压缩codec/直接编码编辑/视频待补。新MyWheelID使<=0.48有整设置拒绝/default/覆盖降级风险。96旧ZIP/122Mac保留，先pull仍c2b72a7/Mac1.12.6(34)，无新Mac实现，本批无MacSwivel写入/执行。

历史Windows0.48：0.48 登录启动记录：显式只读 HKLM/HKCU 的 Run/RunOnce、请求32/64位八个位置；保留逐位置状态和字段问题，命令默认隐藏，明确显示后才进入详情与复制。不会执行、展开或修改命令，不判断应用是否启用/存在/可信/影响启动。每根256/共2048、名称256/命令2048 UTF16、约10秒软时限，原生Fluent记录/根状态页和明确复制，最终UTF8报告4MiB整拒。370067常规核心通过，原生只编译，独立见[TESTING](../Windows/TESTING.md)。F09转partial，52项21/17/12/2普通剩29；StartupApproved/启动目录/任务/服务与启停/修复仍待做。新MyWheelID有<=0.47整设置拒绝/默认/覆盖降级风险。94旧ZIP/122Mac保留，先pull仍c2b72a7/Mac1.12.6(34)，无新Mac实现，无Mac写入或执行。

历史Windows0.47：明确有损GIF减色，fullsize准备/原生before-after缩图预览后同结果保存，5bitRGB桶与加权颜色盒/16–256条目含透明槽；半透明拒绝，无抖色或视觉质量承诺，低色保RGB/旧GIF直接转换及预设不改。369902核心通过，原生仅编译，独立见[TESTING](../Windows/TESTING.md)。C02仍partial，52项21/16/13/2普通29；新MyWheel动作降级风险明确。92旧ZIP/122Mac保留，开始pull仍c2b72a7/Mac1.12.6(34)，无新Mac实现，无Mac写入或执行。

历史Windows0.46：WAV配乐可选循环交叉淡化，0默认保留旧输出；0–1000ms且取最近至少两帧/不超半音乐长。线性尾首重叠、周期缩短及相位抵消/响度变化/不保证无缝明确。224647核心检查通过，原生只编译，独立证据见[TESTING](../Windows/TESTING.md)。52项21/16/13/2、普通29不变；90旧ZIP保留。开始pull仍c2b72a7/Mac1.12.6(34)，无新Mac实现，本批无Mac写入/执行。

历史Windows0.45：现有同采样率整数WAV配乐补默认关闭的RMS活动压低，主选区10分钟/128MiB、全trim分析及20ms/250ms完整深度包络；不识别人声，噪声/音调/音乐误触发及轻声漏检明确标注，语音转写/字幕烧录仍待补。220513核心检查通过，原生仅编译；D06转partial，52项21/16/13/2、普通剩余29，88旧ZIP保持。先pull c2b72a7，较0.44无新Mac实现，无Mac写入或执行；最终独立证据见[TESTING](../Windows/TESTING.md)。

历史Windows0.44：受限8bit基线/渐进JPEG APP1/APP13/COM移除，不重编码扫描；保留ICC/缩略图/unknown及方向/色彩/显示变化、非完整隐私/HDR/凭证/损坏检测均有说明。93966核心检查通过，原生只编译，独立最终记录见[TESTING](../Windows/TESTING.md)。C06仍partial，52项21/15/14/2、普通剩余29不变；86旧ZIP保持。先pull c2b72a7，Mac实现无相对0.43新改，本批不写/运行Mac。

## 技术路线比较

以下是针对本仓库的工程判断，非通用性能排名。

| 路线 | 可复用与代价 | 结论 |
| --- | --- | --- |
| 现有 Swift 直接编译 Windows | Swift 语言与 Foundation 的可用性不能提供 AppKit/Vision 等 Apple 框架；大多数文件仍要重写 | 不采用“直接编译即可”的假设 |
| C++ / Win32 | 与系统最贴近，文件句柄/OLE 自由度高；UI、字符串和工具窗口实现量大 | 保留为必要底层接口，首批使用 P/Invoke 调用 |
| C# / WPF / Win32 | 微软桌面框架，原生系统整合成熟；Core 规则可在 Mac 测试；能交叉编译，运行验证仍需 Windows | 本次采用 |
| WinUI 3 | 可作为后续现代视觉方案；首批会多出 Windows App SDK 与打包/部署组合 | 本次暂不引入，避免把 UI 平台迁移和所有功能移植绑在一起 |
| Electron / Avalonia / Qt 等 | 新增第三方运行时或框架；仍不能复用 Apple 媒体实现，需要额外许可与系统交互评估 | 当前不引入 |

自包含发布包自带微软 .NET 运行时，换来更大的文件体积；用户不用安装开发 SDK。不是把整个开发工具链打进去。发布内容保留运行时许可声明。[微软部署说明](https://learn.microsoft.com/en-us/dotnet/core/deploying/)

## 功能兼容矩阵

“部分实现”表示这一批有可执行代码，但尚不能等同 Mac 体验；“待实现”不代表 Windows 永远做不到。所有实机结论以 [Windows 验收清单](../Windows/TESTING.md) 为准。

| macOS 功能 | Windows 可行路径与主要差异 | 当前状态 / 后续 |
| --- | --- | --- |
| 菜单栏常驻、设置、单实例 | 通知区域托盘 + WPF；Windows 没有 macOS 全局菜单栏 | 已实现托盘、设置、退出及单实例；真实壳层交互待验 |
| Finder 拖动时按修饰键弹轮盘 | macOS 读取 drag pasteboard；Windows OLE 的拖动数据交付给注册的目标窗口，不能简单全局轮询剪贴板替代 | 0.79明确Start的modifier＋主鼠标键会话手势，用低级键盘hook显示非激活Drop目标，实际Drop后再普通Wheel选动作；不证明文件拖动，系统可静默移除hook，真实焦点/OLE待验。保留快捷键/托盘，无剪贴板轮询/注入/提权 |
| Wheel 五模板、翻页、手感 | WPF 可以重绘；布局和 physics 数值可以迁移，FrameDriver 要换成 Windows 渲染时钟 | 0.4补齐五模板、分页、直接投放与温和spring；0.5每模板独立四配色、自动文字对比和高对比覆盖；完整物理待做 |
| Shortcut Wheel / My Wheel | RegisterHotKey +稳定动作ID、独立默认关闭的My Wheel | 0.3已实现选择/排序动作、预设和应用稳定ID、3个可配置快捷键及冲突提示；Windows焦点/注册待验 |
| File Shelf | CF_HDROP/OLE 文件引用；虚拟文件、文件夹、OneDrive 项目另需定义 | 0.4补齐共享主窗/浮动架子、多选复制拖出、改名选中恢复；Mac非激活焦点仍待适配  0.59另补明确文档提取文字/整数WAV只读预览，无导出或自动播放，Shelf来源关闭取消/真join；虚拟云/其他格式与非激活焦点仍待做。 |
| Quick Actions | 独立搜索窗 + RegisterHotKey；Windows 的键盘焦点机制与 AppKit ToolPanel 不同 | 部分实现 Ctrl+Shift+Space、搜索、↑↓/Return；当前会获取焦点；0.52已补限定内置动作建议，Doctor解释及实机仍待验收 |
| 基础图片转换、缩小、旋转 | 微软 WIC 的 PNG/JPEG/BMP/TIFF 编解码器；不能假定 HEIC/WebP codec 在所有机器存在 | 已实现受限单帧 SDR，0.32增加原创完整画布静态GIF输入（palette/二值透明/interlace）；0.38再补256索引内/二值透明的原创静态GIF导出，不自动quantize/dither；0.39另补受限GIF预设，旧四配方保持，动画/特殊布局/ICC等明确拒绝，0.47另补明确独立减色GIF工具：完整规范化图5bitRGB桶/加权盒、16–256palette含透明/同prepared结果保存、<=960nearest预览及颜色损失；半透明仍拒，旧直接GIF/预设不改，不声称质量保真。其他格式/HDR仍待做 |
| 图片压缩到目标体积、裁剪、编辑/滤镜、涂鸦、拼图、元数据 | 自有CPU调节与WPF绘制，WIC编码；不承诺与Core Image等效 | 0.3已补目标字节JPEG、宽/高/百分比缩放、14项调节/8种外观、遮挡/标注、3布局拼图、画布背景及常见元数据检查；颜色/修饰效果和大图性能待视觉验收；0.4已补Featured、编号/Shift约束、调节配方；0.5已补列表选择再编辑位置/尺寸/颜色/文字、删除与20步撤销；0.6补画布移动/角手柄缩放、单步撤销与取消，实机触达待验；0.43补受限PNG全部text/EXIF/time块移除而保留IDAT/其他块原字节，32MiB/4096/clean-only原生验证；不是全部隐私清除，EXIF方向变化/保留ICC和未知块隐私已标注，TIFF/选择性标签仍缺；0.44另补JPEG APP1/APP13/COM移除保留压缩scans和其他segment原字节，32MiB/8bitSOF0/2/原生clean验证；ICC/缩略图等可能保留私隐，方向/色彩/显示变化及非全面HDR/凭证检测已标注 |
| Image Presets | 配方模型和 stable ID 可移植；HEIC、色彩、元数据规则需按平台约束 | 0.2 已补独立默认关闭的预设：格式/可选长边/JPEG 质量，最多24个，改名保持稳定 ID；0.4加入固定照片调节/外观强度及从实际预览另存；0.5已补逐图Auto（四项覆盖且保留原手动值）；0.8补完整Windows备份及兼容Mac原始JSON导入；元数据保留/HEIC/超限配方逐行标注，完整效果仍待验 |
| Text Capture | Windows.Media.Ocr 本地引擎，检查已装语言、图片限制及真实package identity | 0.50条件部分实现：独立默认关闭、显式识别/编辑/复制、原尺寸上限/完整文本界限；需MSIX身份，普通ZIP不可用。实际Windows识别/语言/安装质量未验收，无云OCR或资源下载 |
| Remove BG / Blur BG / ID Photo | Apple Vision 人像/前景实例模型不可直接带到 Windows；需授权的本地模型或 Windows 能力 | 待做；证件照裁剪数学可移植，人像质量需要专门对照 |
| Retouch | 自有 exemplar 算法能重写/迁移；CoreGraphics / Accelerate / 像素缓冲布局不通用 | 0.5已移植自有ROI exemplar、多笔画笔/逐笔撤销、异步取消预览与不覆盖PNG；纹理MAE 0/未涂抹像素/边界/取消核心验证完成；真实照片质量与Windows性能待验 |
| AI Upscale / Real-ESRGAN | 当前资源是 Core ML `.mlpackage`，Windows 无 Core ML；Windows ML / ONNX 可成为本地候选 | **不能直接兼容**。另做模型转换、哈希/许可、CPU/GPU/ARM64及质量/分块验证；本次不搬运、不下载运行时模型 |
| 视频/音频格式转换、Trim / Picture / Speed / Color | Windows Media Foundation 或 Windows 媒体编辑 API；容器、codec、变速保调、GPU与 HDR支持需逐项证明 | 0.6补整数PCM WAV流式裁剪/音量/淡入淡出/试听；0.11补整数WAV采样率/位深/声道显式转换及抗混叠滤波；0.30补50–200%音调联动整数WAV变速，0.33补32/64float WAV显式转整数PCM、全源finite/超幅确认/一致试听；0.34补AIFF/AIFC显式整数PCM→WAV且保原样本/格式；0.35再补整数WAV导出plainAIFF，保原样本/格式、去metadata；0.37补2–32同格式整数PCM WAV顺序保样本接合/原生重排/一致试听与聚合预算，压缩/浮点/视频接合仍缺；0.49补严格G.711 tag6/7 WAV显式解码PCM16，保容器rate/声道/帧数及完整双预算，fmt16/无fact/缺pad等变体明确拒绝，不恢复压扩精度。0.50补WAV/LayerIII MP3/ADTS AAC/FLAC系统codec→完整PCM16，128MiB源/目标/2h、mono/stereo原rate，缺codec/DRM/换格式拒绝，精度/priming/padding可能变化；M4A/MP4/播放列表拒。Mac保音调0.25×–4×、直接浮点编辑/其他压缩与视频仍待做，不引入 ffmpeg 作为隐藏依赖，不宣称与Mac媒体管线等价 |
| Subtitles / Transcribe | SRT规则可迁；Apple SpeechAnalyzer和语言资源不是Windows模型 | 0.3已实现UTF-8 SRT编辑、时间偏移/速度及TXT导出；语音转写和视频烧录仍待原生管线，不申请麦克风权限替代 |
| Reduce Noise / Music / speech ducking | STFT、能量/ZCR与包络算法可迁；Accelerate/AVAudio 解码混音、SoundAnalysis不能照搬 | 0.9已补同采样率整数WAV背景音乐/开始位置/循环/独立fade、主格式保留及混合试听；0.11补独立整数WAV重采样，可先手动转换音乐；0.12补显式噪声样本整数WAV STFT降噪及原声/处理后试听；0.45补默认关闭的RMS主音频活动配乐压低（10分钟/128MiB，约20ms全声道窗/全trim分析、完整压低20ms/恢复250ms），不是人声识别，噪声/音乐误触发及轻声漏检明确；0.46再补显式循环接点线性尾首交叉淡化（0默认/0–1000ms/至少2帧且不超半长），周期缩短、相位/响度变化及不保证无缝已标注；0.57补明确安静能量候选查找/独立试听/明确采用，固定窗+尾窗精确全声道平方和，不自动改样本，安静非纯噪声；压缩/浮点及视频降噪、0.58再补异采样率整数配乐明确确认后完整内存重采样、128MiB完整WAV预算/显示削波/同率旧路保持；语音分类驱动ducking/压缩配乐/等功率节拍匹配与视频同步仍待做 |
| Clothesline / Screenshot takeover | Windows Snipping Tool / 截图保存路径不同；没有 `com.apple.screencapture` 对等开关 | 0.4手动挂图/复制/预览/拖出/会话缓存，0.78明确启动目录新增/改名路径的当前图片卡片已实现；无初扫，不证明事件对象或截图，不改截图设置、不抓屏。系统截图接管、顶边唤出与完整绳索物理待做 |
| Quick Look / Force Touch | Windows 无统一等价的 Mac Quick Look/Force Touch执行方式 | 0.4图片预览、0.59受限文档文字/整数WAV预览、0.75挂图长按预览已实现；其他类型/系统预览待做，触感作为平台差异保留，不使用未证明的硬件 API |
| AirDrop | Apple 的系统传输机制不可直接移植 | **无直接兼容**；后续可评估 Windows 分享/Nearby Sharing，但不冒充 AirDrop |
| Batch Rename | Windows名称、ADS、文件锁及文件系统身份规则独立处理 | 0.3已实现NTFS普通文件规则预览、不覆盖改名、失败回滚、身份核验Undo和部分恢复记录；0.74补名称互换/仅大小写/连锁同目录临时过渡；硬链接/只读/exFAT/ReFS暂不开放 |
| Copy to / Move to | Windows 同卷移动可以保留原对象；复制／跨卷需要完整流复制、输出身份与失败清理的独立设计 | 0.77部分实现：默认关闭Move Files，1–100普通文件，同卷NTFS、完整路径确认、原名／所有流及原ACL保持、不覆盖、不承诺目标ACL继承；失败回滚可部分、准确路径及20批会话Undo。记录元数据核验非内容／ADS／ACL完整证明，崩溃无持久恢复；Copy to、跨卷／目录树与真实Windows验收仍待做。 |
| PDF Pages / 文档转换 / 压缩与解包 | WinRT PDF偏渲染；.NET内置ZIP/TAR/GZip只从持有源流读取 | 0.3已实现普通文件ZIP/TAR/GZip创建（2GiB/100文件），不保留目录/元数据；0.4补图片PDF生成/页序/页型/预览；0.20补受限TXT/DOCX正文文字/Unicode统计及纯TXT/RTF旁文件；0.22追加纯文字DOCX和静态HTML（明确换行及XML预算），0.25补固定content.xml的受限ODT正文/空白折叠和逐项损失，0.26再加纯ODT副本（共五种纯文字输出），0.27补Unicode/1252的受限RTF正文；0.36补严格UTF-8受限静态HTML源码正文，要求明确配平html/body、标注预算与省略，不是完整DOM或浏览器渲染。0.50已有PDF条件性栅格页工具（排序/旋转/合并/逐页PDF或PNG）需MSIX身份，普通ZIP不可用，白底像素/丢矢量文字交互metadata签名；DOC、完整网页/其他格式保真、PDF保真和解压仍待做，解压须路径/链接/预算/Zone.Identifier一起完成 |
| QR/barcode、QR Library、QR Garden | Vision/CoreImage/SceneKit均需替代；数据格式可迁 | 0.23自有QR编码扩至UTF-8 ECI26/v1–40/L/≤2952字节，0.24再加M/≤2330字节和每项级别，0.28补Q/≤1662与H/≤1272（完整L/M/Q/H）（0.5初版105，0.16为270）；原生容量反馈、复制/PNG、32项本地库增改删/分组、1MiB JSON预览导入/导出；0.8起支持兼容Mac库单向导入和逐行限制预览。0.61/0.74补严格平面QR四直角方向读取，0.74补M级≤2000UTF8兼容PNG供Mac图片导入（名称/分组/场景/日期不迁移）；照片透视/纠错/其他条码/完整Mac造型动画/完整双向库迁移待做，无第三方依赖；0.51支持原创Windows三模板/三季节模块浮雕、原生完整相机和选择保存/备份/Mac已知选择迁移，未知逐行标出；完整Mac造型/动画及Windows实际显示/扫码仍待验收 |
| App Actions / draft links / Terminal | 本地EXE用独立参数；严格HTTP(S)网页用系统协议处理器或显式浏览器EXE单URL参数 | 0.3应用条目/类型过滤/完整路径确认/稳定ID已实现，支持用户选定的可信EXE；不认证发布者。0.5补系统cmd.exe /D空白命令提示符（路径仅作工作目录）；0.6补明确复制到剪贴板后无文件参数打开应用；0.17补已保存HTTPS/字面环回HTTP网页、完整确认及可选文件引用复制，默认浏览器打开，不自动粘贴/上传；0.21自选可信本地浏览器EXE、完整路径确认及句柄持有至启动；无额外参数、不认证浏览器身份/发布者。0.42补Codex/Claude Code受限完整预览路径草稿、单query协议闭集和明确Copy/Open，12000UTF8完整草稿/2048ASCII地址门限，无附件/工作区参数/自动发送；注册handler及真实Windows交接尚未验收。交接卡/局域网HTTP仍待做 |
| Keep Awake | SetThreadExecutionState，可选择系统/显示保持唤醒；需同线程申请释放 | 已实现 30分/1/2/5小时/手动关闭；关开关/退出释放，真实睡眠恢复待验 |
| Assistant | 独立HTTP/JSON、服务隔离Windows凭据、完整请求审阅 | 0.56限定实现当前动作建议+基本/九静态/冻结单点Doctor解释+四来源逐次所选标签，默认off/无自动topN或动作修复/双32KiB整拒。名称可含私人文字、完整重审；真实Windows/服务验收和更完整诊断另列 |
| Mac Doctor | IOKit、launchd、Gatekeeper、ClearDisk的Mac路径不可照搬；Windows闭集系统API | 0.3 PC Doctor已补按钮触发的系统/内存/固定盘容量/电源/进程工作集/网卡状态；无扫描/网络测试/清理。0.10补本地IP/DNS/网关/DHCP与用户代理标志、地址可选显示/复制、固定系统设置入口；0.13补明确选本地文件夹的受限只读空间图/逻辑字节/跳过原因/当前视图复制；0.14补明确确认的受限只读重复文件查找/原生分组/全量SHA256及硬链接去重；0.15补项目构建文件夹名称线索/证据与Partial显示；0.18补明确选目录后的只读大文件降序列表/阈值与安装包扩展名线索；0.19补明确按钮打开的CPU时间/物理内存曲线、有限历史与暂停/恢复；多processor group CPU标不可用。0.29补最多4096PID/10s软限的报告路径精确分组、已知工作集小计/未知及完整路径默认隐藏的原生列表/明确复制。0.31补显式传统桌面安装记录的四请求视图/三项报告字符串、有界枚举/未知与原始子key隐私；不是完整应用清单，不执行卸载。0.48补显式只读HKLM/HKCU Run/RunOnce八请求位置、有界枚举/逐根状态及默认隐藏命令、统一显示/复制；不判断启用或执行。0.72–0.75补CPU/物理内存/固定盘环图及受限单窗口关闭请求，0.76补跨常用目录文字/图片比较；可靠helper归属、整应用退出、缓存环图、联网测试、重复文件删除/撤销、项目产物清理/卸载及缓存/启动项启停/签名/修复仍待Windows专项  0.60明确自选EXE有限PE头/证书记录头声明，只读原数值/partial，不运行或验证签名信任；完整打不开应用诊断仍待做。 |
| MCP / Agent工具 | stdio JSON-RPC可实现；安全计划、file ID、MTIME、Trash/Recycle Bin undo均需Windows版本 | 待做，避免不完整写入工具；CLI也不开放任意执行模式 |
| 本地化、无障碍 | 复用英语ID与四语言语义，使用Windows标准控件、系统颜色、DPI布局 | 已实现资源与静态检查；Narrator/高对比度/多屏/四语言真实布局待验 |

Windows OLE 文档描述的是逐窗口的 drop target 回调。因此“先打开目标再拖入”是当前可证实路径；是否能在不注入/不监听隐私数据前提下接近 Mac 全局手势，需要另外做原型，不能从普通按键检测直接推导成功。[RegisterDragDrop](https://learn.microsoft.com/en-us/windows/win32/api/ole2/nf-ole2-registerdragdrop)

Windows 的固定热键注册可能与其他程序冲突，Windows键组合还有系统保留约束。本版选择 Ctrl+Alt+W 和 Ctrl+Shift+Space，检查注册失败并保留托盘入口。[RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey)

Windows OCR 虽是本地引擎，但微软把相关类型列在 desktop app 的 package identity约束区域；语言可用性还必须运行时检查。因此当前便携版不能只复制几行 OCR API 就宣称与 Vision 等价。[桌面 WinRT约束](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/winrt-api-desktop-app-support)、[OcrEngine](https://learn.microsoft.com/en-us/uwp/api/windows.media.ocr.ocrengine?view=winrt-26100)

Windows ML 是可运行 ONNX 模型的本地推理候选，硬件加速和运行时部署有多种组合；这是后续可行性方向，不代表现有 Core ML 模型已迁移或有相同输出。[Windows ML概览](https://learn.microsoft.com/en-us/windows/ai/new-windows-ml/overview)

## 首版文件安全设计

Windows 不只是换路径分隔符。UNC、映射网络盘、`\\?\`设备命名空间、NTFS备用数据流、保留名、尾空格/尾点、junction、symlink和云 reparse point都可能改变读写对象。首批只允许最多240字符的普通本地绝对文件路径；词法检查之后，再使用Win32句柄验证实际对象，所有父目录逐级打开并保持到操作结束。[Windows路径规则](https://learn.microsoft.com/en-us/windows/win32/fileio/naming-a-file)

输出先使用 `CREATE_NEW` 在原文件旁创建 `.swivel-<random>.tmp`。句柄不共享写入/删除，完成、flush且未取消之后，以 `SetFileInformationByHandle(FILE_RENAME_INFO)` 的 `ReplaceIfExists=FALSE`发布。冲突只重试新编号；异常和取消用同一handle设置删除，不按不可信路径删除。没有覆盖式Move，也没有先Exists后写。[CreateFileW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew)、[FILE_RENAME_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_rename_info)

输入保持只读并拒绝替换。属性探测句柄保持到数据句柄打开后，避免检查后换成链接的间隙；本地图片只走明确的微软PNG/JPEG/BMP/TIFF解码器。格式上限、精度白名单和输出行为均显示在应用中。WIC支持可选扩展codec，不等于所有Windows设备都有这些扩展，所以首批不把HEIC/WebP列为已支持。[微软WIC格式](https://learn.microsoft.com/en-us/windows/win32/wic/native-wic-codecs)

原生编解码调用不能在任意时刻中断；取消在调用边界检查，退出等候工作线程收尾。强杀或断电留下的本应用隐藏临时文件不自动扫描清理，避免启动时扫描用户目录。多文件批次不是整体事务，已完成输出保留并列明。

防休眠在UI线程申请、计时、释放，定时到期按实际UTC时钟判断；它不承诺阻止显式睡眠或合盖。微软 API 也要求使用 `ES_CONTINUOUS`保持状态，关闭时恢复为只有该flag。[SetThreadExecutionState](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-setthreadexecutionstate)

## 初始路线（历史，当前状态以上述0.6矩阵为准）

1. **本次基础预览**：独立Windows构建、文件安全、最基础的媒体和工具、兼容页面、测试及agent检查。先让原生基本流程能进入Windows验收。
2. **Windows实机与交互补齐**：NTFS/exFAT、x64/ARM64、多屏DPI、托盘/焦点/多选拖放、热键冲突。修完真实问题后再增加菜单模板、悬浮架子、弹簧和可访问性。
3. **较独立的功能**：图片预设、裁剪/压缩/标注、Batch Rename及可靠撤销、OCR/MSIX、PDF/QR可行性。每项完成独立开关和native测试，不阻塞其他项。
4. **媒体与AI**：先Windows音视频解码/导出，再编辑/音频算法/SRT；AI模型单独做许可、转换、质量、资源和取消验证。保持处理本地，不用上传服务当兼容层。
5. **系统与Agent集成**：Clothesline来源、应用分发、PC Doctor、MCP计划/执行/撤销。逐项定义权限与副作用，再开放入口。

这些是首次调研时的建议顺序，部分项目已在0.2–0.6完成，以本文当前矩阵和PORTING清单为准；未安排无人值守的后续任务。Windows正式签名、发布、安装、云CI触发与推送尚未执行。

## 验证与agent检查记录

- 主应用首轮在Mac上交叉编译通过，0 warning / 0 error。微软明确支持非Windows宿主使用 `EnableWindowsTargeting`下载引用包并构建；这并不提供Windows运行时或GUI验收能力。[NETSDK1100](https://learn.microsoft.com/en-us/dotnet/core/tools/sdk-errors/netsdk1100)
- `Swivel.Core.Tests` 首轮302项检查在Mac通过，覆盖默认值、设置、Windows路径契约、功能目录、尺寸与四语言。
- `Swivel.Windows.Tests`为Windows上执行的独立原生检查，不在Mac上伪装运行；完整状态在最终构建记录补充。
- 独立agent第一轮确认3项P2：多选拖动在mouse-down被默认ListBox收缩；仅按bit-per-pixel判断会漏过Gray16/Gray32Float/Bgr101010；已失效文件引用打开轮盘可能有未捕获异常。已分别用延迟选择、SDR白名单、统一错误边界修复，并提交复核。
- 同时修复输入probe生命周期、快捷搜索事件重复挂载、modal对话框导致的嵌套操作重入和工作状态所有权问题。
- 不修改macOS源代码/Package.swift/build.sh，不替换已安装Mac应用，不修改截图设置；本次不声称跑过未修改的整套Mac媒体回归。

## 最终记录（2026-10-23）

- `Windows/build.sh win-x64` 和 `win-arm64` 均已在本机成功执行。最终 **305 项核心断言通过**；Windows应用及原生测试项目编译通过，编译器0警告/0错误。
- 生成 `build/windows/Swivel-Windows-0.1.0-win-x64.zip`（约74 MB）和 `build/windows/Swivel-Windows-0.1.0-win-arm64.zip`（约69 MB），含微软.NET Desktop 10.0.13运行时、四语言、许可、说明与验收表。无需另装.NET，但必须解压整个包。
- 已检查两个EXE分别是x86-64/ARM64 Windows GUI PE，manifest为`asInvoker`及PerMonitorV2；依赖清单只有Swivel/Core与微软Core/Desktop运行时。四份runtime nupkg与缓存SHA-512一致；开发SDK也对照微软release feed校验了SHA-512。
- ZIP CRC/条目完整性通过，必要的EXE/DLL、运行时、使用说明、测试表和许可证齐全。最终ZIP校验值写入相邻 `SHA256SUMS.txt`；逐源文件哈希及验证边界在 `BUILD-REPORT.json`。
- 独立agent完成首次检查与修复复核：3项确定P2已修复，复核未发现新的确定P1/P2。另补充APNG动画显式拒绝，避免WIC只读默认帧而静默丢弃动画；该回归已加入原生套件，但和其他原生用例一样尚未在Windows运行。
- 一次sandbox内的多进程原生测试构建在5分钟后退出，未给出编译错误；改为单进程且禁用build servers后，完整流程成功。这是Mac构建环境问题，不能解释为Windows测试失败或通过。
- **仍未完成：Windows原生套件执行、GUI启动和Explorer真实拖放、焦点、DPI/多屏、NTFS/exFAT/junction/云文件、ARM64实机、正式签名与安装验收。** 这些均列于 `Windows/TESTING.md`；两包仍为未签名开发预览。
- 本次没有安装/启动Windows程序、没有推送、没有触发云CI、没有修改Mac媒体源代码或替换已安装Mac应用。

## 第二批 0.2：图片工具（2026-10-23）

用户要求继续补齐能兼容的功能，并保留兼容差异，不影响 Mac 版。本批选择不需要新依赖/模型的三个能力：

- **裁剪**：一次一张图，鼠标选区、常用宽高比、整数像素坐标、全图重置；保存新 PNG。读入先做 ICC/sRGB 和 EXIF 方向规范化，裁剪坐标基于转正后的像素。预览持有原件和父目录只读 lease，共用冻结像素导出；退出编辑才释放。首次解码放在 STA 工作线程；取消/退出后不再弹出迟到的编辑窗口。
- **JPEG 质量压缩**：批次统一设置 1–100，尺寸不变、透明填白、移除元数据。这是有损质量调节，不是目标 KB 压缩，不保证结果更小。普通 Convert to JPEG 保持原有质量 90。
- **图片预设**：独立默认关闭，保存格式/长边/质量配方，可用于 Files、Wheel、Quick Actions；最多24个。显示名和 `preset.<GUID>` 分离，改名/改参数/重启保持 ID。已打开的动作窗口随设置刷新，执行时重新按 ID 检查可用性；批次使用不可变配方快照。设置 v1→v2 保留原语言和开关，新功能默认关闭；v2 保存后不支持直接降级到0.1读取。

裁剪使用微软 `CroppedBitmap` 在规范化位图上明确指定像素矩形；JPEG 编码参数范围按官方 `QualityLevel` 定义限制为 1–100。没有借用第三方图片库或 Apple 模型。[CroppedBitmap](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.imaging.croppedbitmap?view=windowsdesktop-10.0)、[JPEG QualityLevel](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.imaging.jpegbitmapencoder.qualitylevel?view=windowsdesktop-10.0)

仍明确标注：裁剪无多图统一裁框、旋转/透视联动及撤销历史；质量压缩没有结果对比预览和目标体积算法；预设不能保留元数据，不支持直接导入 Mac 配方。HEIC/HDR/动画、完整照片编辑、OCR、Batch Rename、安全撤销、PDF/QR、视频/AI、全局拖动和系统功能继续待处理。待处理功能不会阻塞本批输出，也没有被标为完整兼容。

本批验证记录：

- 451 项核心断言在 Mac 通过；其中 5 项各检查 200 组随机/边界裁剪输入，覆盖极细图、反向拖拽、越界和宽高比。补充 v1 设置迁移、预设限界/重复 ID/改名/删除及显示名不被误翻译。
- Windows 应用和原生测试项目编译通过，0 warning / 0 error。新增原生用例覆盖逐像素裁剪、透明度、彩色 EXIF 6 映射、原件锁定与释放、取消无输出、JPEG 质量编码差异及预设四格式。
- 独立 agent 完成预审和最终实现检查，未发现新的确定 P1/P2；确认原有输出不覆盖规则继续使用。此结论是代码审查，不是 Windows 实机通过证明。
- 0.2 交付目标为 x64 / ARM64 自包含 ZIP，准确文件大小、SHA-256、逐源文件哈希与打包验证结果保存在 `build/windows/BUILD-REPORT-0.2.0.json`；0.1 ZIP 保留。
- 本批只写入 Windows 代码、Windows 构建配置及相关文档。未写入 Mac 源码/测试/构建脚本，未安装/替换 Mac 应用；工作区另有并行会话的 Mac 改动，未回退或覆盖。未推送、未触发云 CI。

**原生 Windows 套件、裁剪拖动与缩放、Narrator/四语言真实窗口、模态取消/托盘退出、x64/ARM64 真机仍未执行。** Mac 上的交叉编译不替代这些检查，细项见 Windows/TESTING.md。


## 0.3：七批扩展与独立检查（2026-10-23）

1. 批量重命名：NTFS专用身份和DELETE句柄、不覆盖、回滚/撤销；修复agent发现的改名后文件选择丢失。
2. 照片/标注/拼图：本地像素链、revision与取消控制预览、最终实色遮挡；修复Fill覆盖缩放仍留白。
3. 归档/SRT：内置流式归档、受限UTF-8字幕；修复Unicode数字时码抛未处理异常，以及功能关闭于异步载入期间没有取消整个操作。
4. 图片补齐：目标字节JPEG（最多9候选）、多模式缩放/明确放大、画布背景、常见元数据与去元数据另存。
5. My Wheel/快捷键：独立开关、动作排序、稳定预设ID、三组可配置热键。
6. PC Doctor基础：仅按钮启动、本地只读API、进程资源finally释放、未知值保留、控制字符可见、报告明确复制。
7. 应用路由：16个本地EXE/可选扩展名、稳定ID、完整路径确认、EXE固定到Process.Start、独立参数不经shell。所选文件交接后由外部应用自行读取/编辑/联网。

每批均已独立审查并复核，当前没有剩余确定P1/P2；这是代码审查结论，不是Windows运行验收。**1250项核心断言在Mac通过**，Windows应用及原生测试交叉编译通过、0编译警告/错误。新增原生用例尚未执行；可选系统live检查必须明确设置SWIVEL_DOCTOR_LIVE=1。真实x64/ARM64、DPI、Explorer、焦点、编码效果及应用启动仍待验收。

0.3两个自包含ZIP的准确大小/SHA-256/依赖/源文件哈希记录在build/windows/BUILD-REPORT-0.3.0.json，0.1/0.2包保留。未推送、触发云CI、安装、运行Windows程序或修改Mac源代码/构建/已安装应用。工作区另有会话修改Mac文件，不回退或覆盖；不能把整个仓库当前差异归因于本轮。

采用系统接口的依据：[NTFS文件身份](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/ns-fileapi-by_handle_file_information)、[不覆盖句柄改名](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_rename_info)、[ZIP流条目](https://learn.microsoft.com/en-us/dotnet/api/system.io.compression.ziparchive?view=net-10.0)、[TAR流条目](https://learn.microsoft.com/en-us/dotnet/api/system.formats.tar.tarwriter.writeentry?view=net-10.0)。PC Doctor字段按[内存状态](https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/ns-sysinfoapi-memorystatusex)和[电源状态](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-system_power_status)定义保留未知值，不臆测诊断。

0.3时的后续计划包含浮动Shelf/手动Clothesline/模板与手感、安全文件操作、QR/PDF基础、Retouch、Assistant/MCP及原生媒体；其中部分已在0.4/0.5实现，当前剩余以PORTING为准。不得把普通“尚未实现”改写成“不兼容”，也不把SRT编辑说成语音转写、基础Doctor说成完整诊断。

## 0.4阶段构建

五批功能、审查修复与验证记录见[Windows清单](../Windows/PORTING.md)及[验收矩阵](../Windows/TESTING.md)。本次没有修改Mac源代码、测试、构建脚本或安装；工作区其他会话的Mac变更保留。此阶段不表示全部功能已移植；可移植待办和平台适配难点分别标注，不把尚未实现伪称为不兼容。

## 0.5阶段补齐与验收范围

本轮五批是Retouch、QR生成与库、文件目录Command Prompt、独立配色/逐图Auto、标注对象再编辑。每批agent check已完成，发现并修复：无文件QR强制选择器、Auto丢失原手动值、Night色板悬停低对比、Windows CRLF多行标注无法保存；编号按数值去重也已补齐。没有修改Mac实现、构建或安装；并发Mac工作保留。

QR独立编码遵循DENSO的[版本容量](https://www.qrcode.com/en/about/versionPage/versionPage1_10.html)和[四模块静区](https://www.qrcode.com/en/howto/code.html)，未复制第三方运行时代码。16个中英日/emoji/版本边界样本由系统Vision独立解码为完全相同文字，另一路独立检查RS纠错/格式/剩余位。只证明生成数据有效，不代替Windows图片导出、手机或打印验收。终端固定`/D`依据[微软cmd文档](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/cmd)，不会执行用户文件或拼接命令。

尚未完成的原生部分包括：已有PDF和音视频管线、OCR/语音/AI本地能力、安全移动复制及解压、完整系统交互、Assistant与MCP。它们列在PORTING中等待继续实现或专项验证；不是已判定全部不兼容。所有Windows原生测试与窗口交互仍未在Windows执行。

## 0.6：WAV、标注手柄、应用剪贴板、QR库

WAV限定512MiB、两小时、8–192kHz、mono/stereo整数8/16/24/32位。输入按[RIFF块及补齐规则](https://learn.microsoft.com/en-us/windows/win32/xaudio2/resource-interchange-file-format--riff-)解析，校验长度/byte rate/block align；高位深输出用[WAVEFORMATEXTENSIBLE](https://learn.microsoft.com/en-us/windows/win32/api/mmreg/ns-mmreg-waveformatextensible)。样本流式裁切、饱和增益、按所选全长计算淡入淡出，试听只截前10秒且不提前移动fade-out。独立Python逐样本核对16位淡入淡出、24/32位头和样本，系统afinfo识别通过；不是Windows播放证明。

标注手柄保留原始几何，拖动中不改变导出快照，松开只记一步，Escape和失去捕获取消。两项P2经独立agent发现、修复并复核：无移动小数点重算清空Redo、贴边比例缩放1ulp越界。应用剪贴板模式不复制磁盘文件，只在明确确认后复制文件引用，无参数启动可信EXE，手动粘贴；旧设置仍直接打开。

QR备份是Windows专用闭schema JSON，完整文字/分组预览、64KB/32项预算，输入源保持lease，合并全部验证后一次更新；相同项跳过、同名异文或超额不写入。导出明文内容已显示，选择目录不覆盖。Mac库互通、扫码及3D仍是待办。

以上四批与其原生测试均需Windows实机验收。没有触碰Mac实现/测试/构建/安装；并行会话的Mac文件不回退。

## 0.7：上游同步与 Windows 原生界面

本轮 pull 确认与 origin/main 同步于 2fc19d1。检查新增文件夹样式（Finder专用写入）、文件保真提示、文件安全和媒体修正。Windows新增微软WPF自带Fluent资源与左侧导航，采用标准控件和系统标题栏；主题资源接入依据[微软WPF Fluent说明](https://github.com/dotnet/wpf/blob/main/Documentation/docs/using-fluent.md)，不添加第三方UI库。相关主题能力与限制参见[微软.NET 10 WPF说明](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/whats-new/net100)。真实Windows主题/高对比/键盘/DPI效果仍待验收。

另一批新增图片转换前输出说明，以及逐文件进度/成功/失败/取消/未开始结果，首个错误后停止。现有的输出安全不变。Explorer文件夹样式已在应用Compatibility和清单标为尚未实现：需独立设计图标和原配置恢复，不能将Finder的资源文件写法套到Windows。两个实现批次分别agent check；测试及未验证项见Windows清单。Mac实现、构建和安装不在本轮修改范围。

## 0.8：配方和已保存二维码迁移

补齐Windows原生配方备份/导入和两种Mac原始JSON的单向受限导入。每项导入前完整预览，不兼容项显示原因，不截短名称/文字或偷偷改变“保留元数据”意图。既有Windows动作ID、配方、库项和功能开关保持，只有新项产生新ID；相同项跳过、冲突/总量超限整批拒绝。输出继续通过原有句柄固定及不覆盖发布。

平台差异：Mac预设支持HEIC、保留元数据、16384长边及更长字素名称，Windows本轮只导入可等效表达的配方；JPEG编码器和照片算法仍不保证相同。Mac QR库可用2000字节、较长名称/分组和3D模板；Windows105字节/平面L级边界保持，季节/模板/日期不迁移。Windows备份没有被宣称为Mac可直接导入格式。Mac预设Data没有独立导出UI，本轮也没有改动Mac实现；输入文件来源见Windows README。

独立agent发现并修复UTF-16源字段长度误拒绝合法Swift扩展字素名称的P2，同类配方限制一并修正。Swift Foundation JSONEncoder实际夹具及核心合并/非法schema/取消检查已补齐，Windows native测试只编译，未实机运行。使用现有Fluent系统控件及原生文件/文件夹选择，不引入第三方运行时或权限。

## 0.9：同采样率WAV背景音乐

补本地整数PCM双流混音，音乐开始位置相对于裁剪后输出，音量0–100%、单次/循环、独立淡入淡出。单声道复制到双声道，双声道转单声道取平均；反相声道可能抵消，不做响度匹配。保留主音轨时长/采样率/声道/位深，输出在主音频旁且不覆盖。两个输入句柄贯穿试听/保存，固定内存分块，不下载模型或引入第三方运行时。

兼容边界保持明确：音乐须同采样率，整数PCM 8/16/24/32位、mono/stereo；每份源≤512MiB/2小时。循环是直接重复，只有整段音乐起止fade，尚无接点crossfade、自动speech ducking、重采样、压缩格式、降噪或视频配乐。10秒试听转16位并使用完整导出时间包络。四位深/声道矩阵与独立Python样本核对通过，系统识别24位头通过；Windows播放/音质、长文件速度和界面仍未实机验收。

本批两路独立agent check，加载返回后迟到弹选择器的取消P2已修复复核。原生窗口保持Fluent及系统文件选择，底部操作固定、内容滚动。Mac实现/构建/安装不在本轮写入范围，其他会话的Mac改动保留。

## 0.10：本地网络／代理快照与Windows设置

PC Doctor继续仅按钮启动；使用[.NET本地接口配置](https://learn.microsoft.com/en-us/dotnet/api/system.net.networkinformation.networkinterface.getipproperties?view=net-10.0)读取网卡状态、IP家族/网关/DNS数量和IPv4 DHCP，保留未知。最多64网卡、每类16详细地址；地址默认不显示或复制，用户切换只过滤内存快照，不重新查询。报告包含两页且遵循当前显示选项，网卡/进程名称也会进入复制内容。

代理按[WinHttpGetIEProxyConfigForCurrentUser](https://learn.microsoft.com/en-us/windows/win32/api/winhttp/nf-winhttp-winhttpgetieproxyconfigforcurrentuser)的当前用户/活动连接范围，只读配置标志。URL和bypass正文不进入托管报告，3个非空指针均按API要求GlobalFree；DLL限定System32。未调用代理发现/URL代理计算、DNS、HTTP或PAC执行。应用/服务可采用不同设置，不能从标志推出实际互联网路径或可达性。

新增四个固定[Windows设置URI](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings)，仅由按钮打开网络、代理、存储、电源页，不接受报告或文件提供的URI、不执行修复。原生Fluent双Tab和固定底部操作适配设计已完成，两路agent只读检查无确定P1/P2；核心规则执行和原生交叉编译记录见Windows/TESTING.md。真实Windows配置、故障注入、设置打开、DPI/窄窗/Narrator仍待验收。Mac实现/构建/安装不由本轮修改，其他会话工作保留。


## 0.14：只读重复文件查找

先从GitHub pull，2fc19d1已同步，无新远程Mac提交。继续对齐本地Mac Doctor的新功能方向，在Windows独立实现显式确认的本地重复文件查找：同大小→首尾64KiB→全量SHA-256，识别硬链接为同一文件，源在每阶段固定并核验身份/长度/mtime。只比较默认数据流，没有删除；根层+4层/20K目录项与30秒枚举软限，内容比较512MiB单文件/2GiB读取预算/60秒软限。失败、过大、变化和预算受限逐项标注，不把未扫描部分说成没有重复。

原生Fluent分组/跳过原因页和虚拟化列表，用户选择组查看全部路径、明确复制才使用剪贴板。历史哈希快照不代表同时存在的相同文件、完整元数据一致或可释放空间；ADS、权限、稀疏/压缩/共享块不计算。文件身份依据微软[FILE_ID_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_id_info)，卷号64位+文件ID128位，接口不支持即跳过；Windows各文件系统/云提供程序、实际共享锁和GUI仍待实机验证。MangoDisk仅功能思路，自行实现，未复制GPL代码/规则/素材。

5715项常规核心检查和13,832项独立agent探针通过，原生项目0警告/0错误交叉编译；agent最终无确定P1/P2。重复文件安全删除/回收站撤销、项目构建产物、卸载残留、其他移植待办继续列在PORTING，没有称为平台不可兼容。旧0.1–0.13包保留，不写Mac实现/测试/构建/安装，其他会话改动保留。


## 0.15：项目构建文件夹线索

先pull GitHub，无新提交，仍2fc19d1；保留未提交Mac工作。本批使用已有安全目录元数据扫描，独立实现常见Node/Rust/Swift/Gradle/CMake/Python/.NET/Next/Godot/Dart输出与缓存目录名称规则；相邻标记须是已统计普通文件，CMake/Python还需内部标记。原生Fluent候选/问题列表给出实际依据、逻辑字节、Partial/Unknown，缺失/不安全/歧义/受限项目有说明；仅列最外层候选但内部跳过记录仍可见。

这些是名称线索，不读取配置、不运行构建/安装/删除命令，也不能断言目录可重建或安全删除。根+4层/20K项/30秒软限、路径/cloud/同卷限制沿用；选择根本身不向外读取父目录。自定义输出位置、无next.config的Next.js、独立Python环境、Pods及未列工具仍待扩展，不等于平台不能兼容。每条最多8证据但保留必需类别，完整数量另列；复制仅用户按钮触发。具体规则及各工具官方依据见[Windows移植清单](../Windows/PORTING.md)。MangoDisk仅功能思路，未使用其GPL代码/规则/素材。

6097项常规核心检查通过，Windows程序及原生测试0警告/0错误交叉编译；独立agent最终无确定P1/P2，60,607项独立核心探针和147项翻译/占位符检查通过，限制记录在TESTING/构建报告。真正的Windows文件系统、共享锁/云提供程序、GUI/DPI/ARM64仍未执行。项目清理/可靠撤销、卸载残留及其他待办继续分批实现；0.1–0.14包保持原样，本批未写Mac实现、测试、构建或安装。

## 0.16：较长二维码与备份容量

GitHub pull仍为`2fc19d1`、Already up to date，无新增远程Mac提交；本地并行Mac改动原样保留。Windows二维码现支持v1–10/L、UTF-8 ECI26和270字节；实际容量与四模块白边核对[DENSO容量表](https://www.qrcode.com/en/about/versionPage/versionPage1_10.html)及[留白规则](https://www.qrcode.com/en/howto/code.html)，算法保持自有实现，无新依赖。Mac库中106–270字节行现在可明确预览后导入；超过270字节等行仍标注，不静默截短。

原生界面显示用量/版本/模块数，超限粘贴保留原文并禁用所有输出，恢复有效后重新生成；PNG及剪贴板最大520×520、8px/模块。32项Windows库备份预算提升至128KiB，设置整体预算提升至160KiB，旧格式/ID/开关保持；旧Windows105字节/64KiB限制不能读取全部新内容，降级前应保留旧备份。QR读取/其他条码/3D、更长内容与Mac M级纠错仍有差异，具体范围见PORTING。实机扫码、打印、DPI与原生Windows执行未验收。

本批10671项核心断言通过、原生项目编译0警告/0错误；独立53505项矩阵/RS检查、1081个Vision准确解码与421个旧图一致。agent check发现的满配置超旧读取预算P2已修复、独立复核后无未解决P1/P2。Windows和ARM64实际运行仍待完成。

## 0.17：网页应用动作与进度盘点

开始先pull，返回Already up to date于`bcb7bb9`（Mac 1.12.2 (30)）；该提交在pull前已在本地，不是本次pull快进。相对上一Windows基线`2fc19d1`新增Doctor存储/缓存清理、应用内存及正常退出、图表、重复/相似文件、项目产物、卸载残留，已逐项记录Windows差距。固定[52项进度表](../Windows/PROGRESS.md)现为20项已实现限定范围、9部分、21待做、2平台差异；普通功能还有30项需补齐，另2项评估替代体验，不代表剩余批次/时间或完整兼容百分比。

网页与EXE共用16项/稳定ID，地址原始及规范化后均限2048字符；HTTPS，HTTP仅字面localhost/127.0.0.1/[::1]，拒绝凭据、模糊数字主机、控制/不可见字符等。ASCII/xn--主机和Unicode路径；明确预览后默认浏览器打开，无附加文件信息。可选复制1–100份文件引用后自行粘贴/选择，不保证网页支持粘贴，不自动上传；仅开网页不检查已选无关文件或改剪贴板。浏览器后续可能联网。原生Fluent编辑/确认支持四语言和完整长地址。局域网HTTP、自选浏览器、交接卡/图像字节、Codex/Claude草稿仍待做。

设置v1/v2迁移和旧EXE/稳定ID/开关保持；整体预算提升至384KiB，满足最大网页项与QR/配方/自定义动作同时存储，独立304912字符夹具往返通过。旧版本可能拒绝新网页字段或扩大配置，降级前保留旧备份。10842常规核心检查、10257独立探针及84翻译检查通过，最终agent结论见[验收记录](../Windows/TESTING.md)。原生项目交叉编译0警告/0错误；真实Windows/ARM64/GUI和浏览器/剪贴板交接尚未执行。Mac实现、测试、构建、安装未改，0.1–0.16包保留。

## 0.18：只读大文件查找

开始先pull，Already up to date，仍为`bcb7bb9`，相较0.17没有新增Mac提交。PC Doctor新增明确选一个本地文件夹的大文件查找：复用受保护的目录元数据扫描，根+4层/2万条/约30秒软限，不打开文件内容，按逻辑字节降序。Fluent原生列表/详情/问题页签、默认100 MB的十进制包含边界阈值、安装包/磁盘镜像末尾扩展名筛选均作用于已有快照，无重扫。问题/Partial不随筛选消失；仅明确复制报告或选中详情时复制路径，无自动持久化或网络。

安装包只是名称线索，不验证内容/签名或安全删除；大小不是物理占用/可释放空间，硬链接可重复，ADS/开销不计，数据可能已经变化。未开放清理/执行/打开文件，也未完成常用目录批量入口、缓存规则、回收站撤销。F06由待做转部分，因此52项现为20已实现限定范围、10部分、20待实现、2平台差异；普通剩余仍30项（10+20），但大文件只读子范围已补齐。详细结果和实机待办见[验证记录](../Windows/TESTING.md)。Mac实现/构建/安装与全部旧Windows包保持独立。

本批11593项核心检查通过，22759项独立探针及93项翻译检查通过；应用/原生测试交叉编译0警告/0错误，agent最终代码/文档复核完成，无未解决的确定P1/P2，记录见TESTING。原生Windows测试、GUI/DPI/Narrator/剪贴板及ARM64实机尚未执行。

## 0.19：CPU和物理内存曲线

先pull，Already up to date，仍`bcb7bb9`，相较0.18无新Mac提交。新增Doctor显式监测窗口：微软本地API约2秒单飞读取CPU累计时间和物理内存，原生Fluent控件/系统色双曲线、数值、暂停/继续及明确复制快照。可见且未最小化才采样，暂停/隐藏/最小化留断点、旧在途结果失效，关闭/退出取消并等待读者返回。单调时间，最多相对最后刷新120秒/61点，暂停冻结图；首CPU值/失败/恢复重新基线，未知不显示成0%。

GetSystemTimes的kernel含idle；多processor group下只代表primary group，因此Windows本批明确CPU不可用。物理内存是总量减可用量，包含Windows可用分类，并非应用用量/泄漏/清理诊断；无进程扫描、网络、自动保存、强制退出或新依赖。具体API来源和限制见[移植记录](../Windows/PORTING.md)。52项进度变为20已实现限定范围/11部分/19待实现/2平台差异，普通剩余30。12285核心检查、54709独立探针及72翻译检查通过，agent最终复核无未解决的确定P1/P2；Windows实机待办见[TESTING](../Windows/TESTING.md)。Mac实现/测试/构建/安装及旧Windows包保持独立。

## 0.20：文档文字与统计

2026-10-24先pull，Already up to date于`bcb7bb9`，相对0.19无新Mac。新增基本单文件文档动作：严格UTF8/BOM UTF16文本及受限DOCX正文，Unicode空白分段/字素/标量/行/字节统计，Fluent只读预览，完整提取文字明确另存TXT/原创转义RTF。原件8MiB/文字2M UTF16；DOCX只固定正文ZIP项，实际解压/XML字符/深度/条目另有限额，禁止DTD/外部解析，无解包/执行/资源获取。格式、页眉脚注、图片/对象/宏不保留，隐藏文字/字段结果可出现，不等同Word渲染。预览限65536，统计/输出不截短，完整源/祖先lease跨modal和导出，继续独占旁输出。

12384常规核心、132681独立探针、系统textutil额外1次RTF Unicode回转和84翻译检查通过，最终agent结论见TESTING；Windowsnative/UI/ARM64尚未执行。E03推进部分，52项=20已实现限定范围/12部分/18待做/2平台差异，普通剩余30。其余文档格式读取/保真转换及PDF仍待做，Mac实现/测试/构建/安装与全部老包独立。

## 0.21增量

开始先pull，仍bcb7bb9，无新Mac。网页动作可独立选可信本地浏览器EXE；只传一个规范化URL参数、不经shell，默认浏览器方式保留。NativeFiles源/祖先持有至完整确认和启动，取消/失败释放；PE格式检查不验证发布者/浏览器身份。不能指定脚本/快捷方式/商店别名/链接云路径/额外开关，浏览器需接受单URL。Fluent选择器与完整路径、工作区/DPI限高。旧字段缺失/null默认，旧版忽略选择回退默认，降级前备份。独立agent检查见TESTING；Windows启动/剪贴板/UI/FS/ARM64仍待实机。进度52项20已实现限定/12部分/18待做/2平台，普通剩余30不变。

## 0.22增量

本批先pull仍bcb7bb9，无新Mac。文档文字流程追加纯文字DOCX/静态HTML，仍源lease和旁文件独占；不保留排版/图片/链接/宏/元数据。DOCX仅固定最小三项包、内部主文档关系和XmlWriter转义正文，无Office依赖；完整序列化XML2M UTF16预算，超限/非法XML字符明确拒绝，可选其他格式，不截短。HTML转义原脚本和标签为文字，pre/span保留首LF、不自动打开。两者CRLF/CR归LF，DOCX读回加末段换行。依据[微软包结构](https://learn.microsoft.com/en-us/office/open-xml/general/how-to-create-a-package)与[HTML pre规则](https://html.spec.whatwg.org/multipage/grouping-content.html#the-pre-element)，无新第三方依赖。独立agent/验证见TESTING；实际Office/浏览器/Windows UI/FS/ARM64未验收。E03仍部分，52项20/12/18/2、普通剩余30不变。

## 0.23增量

本批先pull仍bcb7bb9，无新Mac。QR编码覆盖Model2 v1–40/L/ECI26、2952byte；数据码字2956减UTF8 ECI及byte长度头32bit，与无ECI的2953容量有差别。标准坐标/RS参数事实核对[DENSO](https://www.qrcode.com/en/about/version.html)与[标准表记录](https://github.com/zxing/zxing/blob/master/core/src/main/java/com/google/zxing/qrcode/decoder/Version.java)，无第三方代码移植/新依赖。Fluent预览2–4DIP/module<=380、水平查看与固定footer，PNG/Clipboard8px/module最大1480。QrLibrary及Prefs各1MiB，兼容Mac源2000byte内容，保持源256KiB/128行及名字/分组逐行限制。M级/3D/反向迁移/扫码仍待做。156039独立结构断言、1107旧像素保持、1379系统Vision准确解码不代表Windows扫码/打印验收；详细见TESTING。旧版不识别长内容/大配置，降级备份明确。进度52项20/12/18/2、普通剩余30，44旧ZIP/Mac保持。

## 0.24增量

先pull仍bcb7bb9，无新Mac。QR全40版本新增M/ECI26最大2330byte，保留L2952及原输出。Fluent每项级别/动态用量，切级别超限保留文字并禁旧图；PNG/clipboard/库/备份一致，Mac<=2000byte兼容项导入M以匹配Mac纠错级别，编码外观仍可异。旧条目缺字段L，M追加可选字段；L备份保留三字段，M备份用可选字符串Correction，闭schema拒绝未知/null/类型/重复，级别差异同名冲突整批不变。旧版可能拒M备份或忽略设置级别回L，降级先备份。无新依赖/无Mac编辑；反向迁移/3D/扫码及Q/H未补。独立294428结构断言、3174库/边界、120参数、1379旧L像素保持、1369准确系统Vision解码及33翻译检查通过，不代表WindowsUI/FS/剪贴板/打印/ARM64实机；记录见TESTING。52项20/12/18/2普通剩余30，46旧ZIP保持。

## 0.25增量

先pull仍bcb7bb9，无新Mac。ODT仅content.xml受限正文，1000entries/256Ki名字、8MiB原件/实际XML、2M XML字符/提取UTF16、深度128，DTD禁止/resolver=null，不加载其他ZIP项/样式/资源或运行Office。按[ODF1.3正文与空白规则](https://docs.oasis-open.org/office/OpenDocument/v1.3/OpenDocument-v1.3-part3-schema.html)折叠普通空白，保留显式s/tab/br及每段LF；list/table段落按存储顺序一次，重复行列/编号不展开。未知inline/fields/hidden元素/对象/脚注/修订仓/拼音注释省略，正文插入字保留；样式及条件隐藏不求值，隐藏字可保留。沿用文档统计、预览、四格式副本与sourcelease/旁文件独占；原格式保真及其他读取/PDF仍待做。19559常规核心，独立agent见TESTING；Windows实机/Office/UI/FS/ARM64未验收。52项20/12/18/2普通剩余30，48旧ZIP/Mac保持。

## 0.26增量

先pull仍bcb7bb9，无新Mac。文档导出追加ODT第五格式，固定最小三项包，mimetype首项Stored/无extra/ASCII，manifest根和content.xml/ODF1.3声明，无源样式/宏/资源继承。依据[ODF1.3包规范](https://docs.oasis-open.org/office/OpenDocument/v1.3/OpenDocument-v1.3-part2-packages.html)。显式text:s/tab/line-break保持首尾空白，CRLF/CR归LF且本app读回末段加LF。外部应用软换行可映射不同Unicode分隔符，如系统textutil U+2028，不承诺跨应用字节一致。完整XML2M UTF16/8MiB和正文+末LF2M双预算预检，XML非法/膨胀/非空nonseek输出拒绝前不写，超限不截短；native独占新空stage输出符合stream约束。源/祖先持有、旁发布/取消保持。19614核心，独立agent见TESTING；Windows/Office/UI/FS/ARM64未运行。52项20/12/18/2普通剩余30，50旧ZIP/Mac保持，不等同排版保真或完整移植。

## 0.27增量

开始先pull仍bcb7bb9，无新Mac。原创受限RTF 1字节lexer，依据[微软RTF规范](https://go.microsoft.com/fwlink/?LinkId=120924)的词法/Unicode规则独立实现，无第三方parser/新依赖。Unicode有符号及TextEdit正数代理兼容、CP1252原/hex字节；其他codepage/charset有可见高字节时整文拒绝，ASCII/Unicode可读，不猜字体字形。8MiB/2M UTF16/128组/32字母control，uc0..16组栈、fallback lexical count、bin块先跳、真括号终止fallback。仅正文和明确tab/LF，无额外末LF；资源/字段含结果/页眉脚/注脚批注/显式隐藏删除省略，样式隐藏和字体符号不解释；未知body controls可能省略，upr替代组拒绝。不加载Office/对象/外部资源。UI和复制统计说明同一限制，五格式副本/lease/独占旁发布保持。独立agent与真实系统产物验证见TESTING；Windows/Office/UI/FS/ARM64未执行。52项20/12/18/2普通剩余30，52旧ZIP和Mac保持，E03仍部分。

## 0.28增量

开始先pull仍bcb7bb9，无新Mac。原创QR Model2全40版本加入Q/H/ECI26，1662/1272byte，保持L/M2952/2330和旧像素。只以[标准RS参数事实表](https://github.com/zxing/zxing/blob/master/core/src/main/java/com/google/zxing/qrcode/decoder/Version.java)交叉核对data/ecc/block数量，不移植第三方算法或添加包。Fluent四级选择、字节/版本/实际级别和ready输出一致，超限原文保持、清旧图；双format BCH覆盖。库/设置追加枚举值，0/1保留，备份可选精确Q/H而L旧shape不变；同名不同级别完整合并拒绝。Mac导入M、1MiB/32项/ID/默认关闭/目录句柄与独占输出保持。0.24–0.27拒Q/H，0.23更早可能忽略回L并丢选择，降级先备份。独立agent/标准参数/真实系统解码与旧像素证据见TESTING，不代表WindowsUI/FS/剪贴板/ARM64/破损恢复/打印实机验收。52项20/12/18/2普通剩余30，54旧ZIP和Mac保持；读取/3D/反向迁移仍未完成。

## 0.29增量

先pull仍bcb7bb9，无新Mac。显式PC Doctor只读进程内存快照，最多4096PID/约10s软限/4096UTF16路径；不自动刷新或修改进程。按[微软进程API](https://learn.microsoft.com/en-us/windows/win32/api/psapi/nf-psapi-enumprocesses)、[查询权限与工作集](https://learn.microsoft.com/en-us/windows/win32/api/psapi/nf-psapi-getprocessmemoryinfo)、[报告路径](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-queryfullprocessimagenamew)独立实现，5个System32入口，Open仅QUERY_LIMITED/noinherit，出生FILETIME（不依未定义exit判活）、路径和基本72byte内存计数经同一handle，结束即释放。不读进程内容/EXE内容/文件夹、不联网/提权/退出程序。枚举满/重复/不可用/限时保留Partial，取消每阶段检查，功能关闭不晚弹窗。

仅Ordinal相同报告路径归组，不折大小写/同名不同路径/不同EXE helper；别名/硬链/替换程序/发布者/可靠应用归属不求值。decimal已知小计/未知计数，全未知Unknown；工作集共享页可能重复，非私有/物理总量或可释放RAM。Fluent虚拟双表/MB与精确bytes详情，完整路径默认隐藏、勾选才选中详情与明确复制，同一formatter、两个tab均清旧值，安全行/ToString无原路径。完整可见转义、不用Mac宿主basename函数；整份复制逐块4MiB UTF8预算整拒，无持久化。29864核心，独立4953注入/规则探针与150翻译，最终agent见TESTING；不代表WindowsAPI/UI/剪贴板/ARM64执行。52项20/12/18/2普通剩余30，56旧ZIP/Mac保持。可靠应用/helper归属、正常退出/环图仍未完成，不称Mac Doctor完整对等。

## 0.30增量

先pull仍bcb7bb9，无新Mac。新增单个整数PCM WAV50–200%整数速度、默认100%、输出时长/采样帧显示，原采样率/声道/位深不变。复用原有原创sinc内核，有理percent/100步进、最近帧半向上，快放低通防alias、慢放插值，100%保留样本；峰值可能削波，无dither。Windows本批音调随速度变化，Mac现有TrimEditor/VideoEdit为保音调0.25×–4×，因此不能称同等变速能力；保音调、更宽范围、压缩/浮点及视频继续待做。

完整最终输出含RIFF头/补齐最多512MiB/两小时，慢放超限整拒不截短。10输出秒PCM16预览与保存使用同一全源滤波/源边界和目标量化，source/ancestorlease贯穿modal及导出，取消/退出等待预览结束，NativeFiles独占旁文件不覆盖。Fluent数字框、长度显示、正文滚动/固定按钮/工作区DPI、四语言，无新schema/第三方依赖/模型/网络。30312常规核心检查，独立agent见TESTING；真实Windows播放/音质/性能/UI/文件系统/ARM64未验收。52项仍20/12/18/2普通剩余30，D01限定范围扩大、D04保音调等仍待做，58旧ZIP/Mac保持。

## 0.31增量

先pull仍bcb7bb9，无新Mac。PCDoctor新增明确按钮的传统桌面安装记录清单：固定当前用户/计算机Uninstall的请求32/64视图，仅三项报告字符串（名称/版本/发布者），不读卸载命令/安装位置/程序文件，不执行/联网/提权/卸载。根据微软[记录字段](https://learn.microsoft.com/en-us/windows/win32/msi/uninstall-registry-key)、[注册表字符串返回约束](https://learn.microsoft.com/en-us/windows/win32/api/winreg/nf-winreg-regqueryvalueexw)及[请求视图](https://learn.microsoft.com/en-us/windows/win32/winprog64/accessing-an-alternate-registry-view)独立实现。3个System32 Advapi32进口、borrowed符号扩展根/owned SafeRegistryHandle、root枚举及child查询最小权限/同viewflag。固定buffer逐项枚举，每view1024/总4096、每字段1024UTF16/10s软限，MORE_DATA不读截短前缀，strictUnicode/NUL/类型/缺失/空白/超长/失败/限时逐项标注，取消每阶段，已记录保留、未知剩余不造数。

记录非同时/可过期/可由安装器任意提供，动态增删可能漏项；视图可共享/经注册表链接，不核验底层key身份/目标，不合并或推断唯一app/架构/发布者身分/残留归属。不覆盖Store/MSIX/portable/其他用户/ARM32额外view。Fluent虚拟记录/视图状态页、完整详情、4MiB UTF8整拒与明确复制；原始子key默认隐藏，reported名字/版本/发布者仍显示且可能有私人信息，safeRow不泄rawkey、不用key回退名称。无持久化/设置schema/依赖变更。30443核心、131275独立探针/150翻译、最终agent无未解决确定P1/P2（详细限制见TESTING）；Windows注册表/API/UI/ARM64未验收。52项改为20已实现限定/13部分/17待做/2平台，F08从待做推进部分，普通剩余仍30。实际卸载/残留/可靠撤销仍待做，Mac与60旧ZIP保留。

## 0.32增量

开始先pull仍bcb7bb9，无新Mac。GIF87a/89a的完整canvas/方形像素单画面输入，原创有界palette/LZW解码到BGRA，支持二值透明（隐藏RGB归0）及interlace。根据[GIF89a格式](https://www.w3.org/Graphics/GIF/spec-gif89a.txt)独立实现，代码不来自第三方；致谢随README发布。原有图片预览/转换/编辑/预设/拼图/图片PDF/挂图流程可读受支持GIF，原件/祖先lease与旁输出不覆盖保持；输出仍PNG/JPEG/BMP/TIFF，未新增GIF writer或设置schema/依赖。

动画/多画面、偏移/局部canvas、非方形像素、PlainText/userinput、ICC和unknownAppExt整份明确拒绝，不取firstframe/丢文字/色彩降级；只识别两种闭集循环格式而不保留循环/延时，metadata去除、只报告尺寸/大小。色值按SDR sRGB，完整色彩/浏览器容错不保证。128MiB/16MP/16384边、1024扩展/64KiB扩展payload/全流1M变长子块/2*pixels+4096码、固定字典与BGRA，EOI/像素数/终止块/trailer完整，EOI末字节padding外的多余payload明确不支持。40548核心、原生项目编译0warn/error，独立agent见TESTING；WindowsWPF/PNG编码/透明/GUI/DPI/ARM64未执行。52项20限定实现/14部分/16待做/2平台，C02推进部分，普通剩余30；GIF输出/完整动画及其他格式仍待补，Mac和62旧ZIP保持。


## 0.33增量

先pull仍bcb7bb9，无新Mac。独立原创受限IEEE float32/64 RIFF WAV转16/24/32整数PCM（默认24），不放宽原PcmWave、不依赖设备codec。字段范围见[WAVEFORMATEX](https://learn.microsoft.com/en-us/windows/win32/api/mmreg/ns-mmreg-waveformatex)，严格ExtensiblefloatGUID/validbits/mask或普通tag3；mono/stereo8–192k，精确长度/data/block/byterate/可选fact，input1024chunk/512MiB/2h。全源finite扫描含10秒外异常，NaN/Inf整拒；离散peak/超单位样本数明确非truepeak/响度，超幅需默认未勾选许可，先clamp避免有限极值算术溢出，再halfAway量化，无自动normalize/gain/resample/dither。保留rate/channels/frames、去metadata，±1映整数端点，浮点动态范围/小信号细节会减少。

完整PCM输出含header/pad512MiB/2h先检查，即使10秒试听也不绕过（float32头44→PCM32头68恰界回归）。每次preview/save先完整Scan增加读取成本但内存4096frame，试听target量化后PCM16前10s，与保存前缀一致。Fluent原生选项/显式clipping/滚动/固定操作/工作区DPI及四语言，NativeFiles源祖先贯穿并等待preview清理，原件旁exclusive不覆盖。无Mac源/测试/构建/安装改动、无schema/新依赖/模型/联网。float64离线支持不是设备播放保证；直接浮点编辑、压缩/RF64/多声道/视频/保音调/ducking仍待做。90162核心与原生项目编译0warn/error；独立agent见TESTING，Windows播放/听感/性能/GUI/FS/ARM64未执行。D04转部分，52项20限定实现/15部分/15待做/2平台，普通剩余30；64旧ZIP哈希不变；本批未写Mac。本地并发Mac截图卡显示检测/重建、相关测试和1.12.3(31)版本同步（Info.plist/mcpb）已只读核对并保留，不回退、不当作本批pull的新提交或Windows已实现能力；具体时点哈希见外置审计。


## 0.34增量

开始先pull仍bcb7bb9，无新远端Mac，已有本地Mac1.12.3(31)截图卡修复/测试/版本同步保留。独立原创AIFF整数PCM及AIFC NONE/sowt（8/16/24/32）、twos16/in24/in32对应24/32→WAV，mono/stereo8–192k整数Hz，保存保每个整数sample/rate/channelorder/frames/bits，8bit有符号转WAV无符号、多byte按端序读写；移除metadata/markers/loops/name，无gain/normalize/resample/dither，不是全文件归档。依据[Apple原始Sound手册](https://www.mmsp.ece.mcgill.ca/Documents/AudioFormats/AIFF/Docs/MacOS_Sound-extract.pdf)格式事实自行实现，不采用第三方decoder或系统codec；不改原有PCM/float WAV合同。

精确FORM、COMM/Pascal名和内外补零、AIFC唯一FVER、SSND唯一/blocksize0/offset及frame字节数；80bitrate用整数位运算验正normal/leading1/精确整数，不舍入微小分数。1024chunks/512MiB/2h输入，含44/68header/pad的完整WAV512MiB/2h先验，即使10sPCM16preview也不绕过。4096frame缓冲，preview与Save→PCM16一致，NativeFiles源祖先持续/预览取消join/旁输出exclusive和Fluent固定footer/scroll/DPI/四语言，schema/依赖/模型/网络不变。其他编码标签（23ni/42ni/float/压缩）、分数rate/nonbytebits/多声道/特殊块仍明确待做；不是平台不可实现。90983常规核心、原生项目编译0warn/error，独立见TESTING，Windows播放/音质/GUI/FS/ARM64未执行。52项20/15/15/2普通30不变，D04继续部分；66旧ZIP保留，未写Mac实现/测试/构建/安装，具体时点并发审计另存。


## 0.35增量

开始先pull仍bcb7bb9，无新远端Mac，已有本地Mac1.12.3(31)截图卡修复/测试/版本同步保留。原创plainAIFF writer和单WAV独立Fluent入口，输入为既有mono/stereo8–192k/8/16/24/32整数PCM WAV；保每个整数样本值/rate/channelorder/frames/bits，8unsigned→signedbias和多字节BE，不改变音量/量化/重采样，无dither。固定FORM/COMM/SSND54byte头，80bit rate按整数Log2规范编码，外pad0；移除metadata/markers/loops，不是全文件归档或AIFC输出。格式事实来自[Apple原始Sound手册](https://www.mmsp.ece.mcgill.ca/Documents/AudioFormats/AIFF/Docs/MacOS_Sound-extract.pdf)，代码自行实现，不引入其codec/外部应用。

源与完整输出512MiB/2h，含54header/oddpad预算在Open/Corepreview/Save前检查，WAV44短头扩10bytes可超限；前10sPCM16preview与保存AIFF→PCM16一致，4096frame流式可取消。源祖先持续/预览取消join/独占旁输出，Fluent固定footer/scroll/workareaDPI/四语言，旧WAV/AIFF读取器与NativeFiles不改，无schema/依赖/模型/联网/Mac写入。91351核心、应用/原生项目编译0warn/error；独立见TESTING，Windows播放/音质/GUI/FS/ARM64未执行。52项20/15/15/2普通30不变，D04继续部分；68旧ZIP保留。0.34文档rate遍历数185001为文字笔误，实际184001，总837023真实不变；保留旧包并在当前TESTING/外置审计勘误。


## 0.36增量

先pull仍Already up to date/bcb7bb9，无新远端Mac，本地1.12.3(31)并发截图卡/测试/版本同步保留。新增显式html/htm静态源码正文读取，原创严格UTF8/完整html-body/配平子集，8MiB/2M源码和文字/depth128/100K标记token/16Ktag/128attr/64名字。不是完整HTML5 DOM或浏览器错误修复，未知/脚本/on前缀属性/legacycharset/http-equiv/复杂doctype明确拒绝；省略资源/模板/多媒体及hidden属性子树，CSS/aria-hidden不求值可能保留隐藏文字。不运行脚本、不跟随URL、不加载外部资源或Office/浏览器。普通空白折叠、块LF/br、PRE保留存储首LF/CRLF、系统实体只解码一次；完整限制见PORTING及UI/复制报告。

沿用Fluent统计/有限预览/五格式纯文字副本及源祖先lease/独占旁输出；旧四reader/五writer不改，无schema/新依赖/Mac实现构建安装写入。依据仅[WHATWG语法事实](https://html.spec.whatwg.org/multipage/syntax.html)和[微软HtmlDecode API](https://learn.microsoft.com/en-us/dotnet/api/system.net.webutility.htmldecode?view=net-10.0)，未复制第三方解析器。91431核心通过、独立53174断言与最终agent见TESTING，原生只编译；Windows实际UI/FS/Office/browser/ARM64未执行。52项20限定实现/15部分/15待做/2差异、普通剩余30，E03仍部分，DOC/完整网页及保真/PDF转换待做，70旧ZIP保持。


## 0.37增量

先pull返回Already up to date，实际HEAD/origin/main已为b9d6020/Mac1.12.3(31)。已核对相较bcb7bb9截图卡0.4秒显示自检/一次重建恢复、测试/版本同步新提交，其字节与此前本地并发Mac工作相同并保留；Windows自动截图仍待做。本批对齐Mac音频merge意图，新增2–32同格式整数PCM WAV顺序接合；原生Fluent完整路径列表/UpDown/首源输出位置/前10秒整体PCM16试听。逐样本/原rate/channels/bits/frames保持，无gap/resample/gain/crossfade，突变接点可click，metadata/markers/loops移除。普通与Extensible同语义格式可混合，浮点/压缩/视频接合与保音调媒体仍待做。

原创data-only流式复制4096frame，仅最终oddpad，完整聚合sourceLength（含metadata/headers/pad）与完整WAV输出各≤512MiB，sumFrames整数≤2h；所有source及完整预算先验再写任何header，即使preview。源/祖先全程持有，失败/取消全部清理，原件旁独占新WAV以审核顺序第一项为准。旧PCM/AIFF/float算法/NativeFiles不改，无schema/新依赖/Mac实现/构建/安装写入。91766常规核心及独立agent见TESTING；Windows播放/接点听感/GUI/FS/ARM64仅编译未执行。52项20限定实现/15部分/15待做/2差异、普通仍30，D04部分，72旧ZIP不改。


## 0.38增量

本批先pull返回Already up to date，origin/main b9d6020/Mac1.12.3；本地Mac会话随后提交1.12.4安全修复7bf2fff（实际baseline），已只读核对TAR @名字/解压外链、草稿宽目录、图片剪贴板、similar keeper/卸载共享identifier和MCP16并发。后续63685ed中性fixture及de1f7f8/ba54ebc归档逐级/顶层链接约束、同描述符剪贴板读取/MCP旧ID兼容等并发修复已核对保留；最终差异以外置审计为准，不回退Mac工作。Windows尚未实现的相关能力继续待做，无本批Mac实现/测试/构建/安装写入，74旧ZIP保留。

新增原创GIF89a静态writer及convert.gif，规范化8bit SDR RGB在<=256索引含透明且alpha0/255内精确保留；透明hiddenRGB归0，partialalpha/超色在输出头前整拒，不quantize/dither/铺底，PNG替代。全局palette/有限4096LZW/decoder独立码宽/255子块，16MP/16384边长、索引快照预检及取消、源祖先lease/独占旁输出、Fluent转换说明/四语言/首错停止保留完成结果；旧GIF读取/四writer/四预设不改。无animation/metadata/profile/loop/delay，不宣称原格式归档或完整GIF兼容。依据[GIF89a格式事实](https://www.w3.org/Graphics/GIF/spec-gif89a.txt)独立编写，无第三方代码/新依赖。92921核心/3289独立断言，828writer产物Pillow与ImageIO均精确像素，15翻译，最终agent证据见TESTING；WindowsWPF/GUI/FS/DPI/ARM64未执行。52项20限定实现/15部分/15待做/2差异、普通仍30，C02部分。


## 0.39增量

期间Mac另有keeper身份/Trash未确认回执/窄工作区、归档展开预算/MCP撤销身份/旧日志提示及测试并发修复，均只读核对并保留；最终时点文件差异见外置审计，不声称Mac全部未变。

先pull Already up to date，origin/main仍b9d6020/Mac1.12.3，本地HEAD38689a7为0.38已检查保留的Mac1.12.4安全系列，无新远端Mac。Windows默认关闭ImagePresets增加GIF，复用0.38同一writer/最终像素检查，不改旧四格式/Mac导入闭集/NativeFiles。调节/Auto/缩放可能增加色与半透明，最终<=256索引含透明且alpha0/255才能写，不换PNG不降色；Fluent编辑/库预览/运行确认明确限制，编辑固定footer/工作区DPI。GIF quality仅存值不影响输出，描述不显示JPEG百分比。

64KiB预设库与1Mi UTF16设置预算分别不变，24项/v1-v2/稳定ID/原子合并/默认关闭保留；旧版Windows可能拒gif而整份设置加载失败回默认、保存可能覆盖，降级前保留旧备份，含GIF新库也需此版以后。Mac本身只jpeg/png/heic/tiff，伪gif源仍整拒，不宣称MacGIF配方迁移。92947核心/原生只编译，独立agent证据见TESTING；Windows真实WPF/色彩/FS/DPI/ARM64未执行。52项20限定实现/15部分/15待做/2平台差异、普通仍30，C07部分；76旧ZIP与Mac并发工作按时点保留，无本批Mac实现/测试/构建/安装写入。


## 0.40增量

先pull GitHub Alreadyuptodate，origin/main b9d6020/local76fbccb（上轮核对的Mac1.12.4安全修复），无新远端Mac。PC Doctor新增默认关闭开关下的显式相似文本入口；先选目录、完整确认，再复用held metadata与普通本地同卷源读取。原创strictUTF8/BOMUTF16行集合：TXT/TEXT/MD/MARKDOWN/CSV/TSV/LOG/SRT同扩展名、5不同非空行起、首尾Unicode空白Trim、忽略重复/顺序，行内容Ordinal；共有/并集>=80%整数判定，分数截断。不是语义或字节重复，即使100%可不同顺序/频次，每对独立不作传递分组。

200打开尝试（含失败）、1MiB/文件、16MiB请求读取、20K原始行/文件、200K累计各文件不同行数之和（跨文件共有行重复计）及约60秒软限，metadata另有root+4/20K/30秒。身份/长度/mtime前后核验和单源/祖先lease至解码后；硬链接一次，后见变stamp使先前集合失效。原生Fluent虚拟配对/问题列表、固定footer与DPI、前1000条Ordinal路径序配对且不是最佳1000，零匹配仍保留Partial/所有跳过。明确复制路径统计原因、绝无原文，不保存/联网/打开/删除。93,185核心/原生仅编译，独立检查见TESTING；Windows真实FS/GUI/ARM64未执行。52项仍20限定实现/15部分/15待做/2平台差异；F04仍部分，图片相似/常用目录/清理撤销继续待做。78旧ZIP和Mac并发工作按时点审计保留，无本批Mac实现/测试/构建/安装写入。

0.40降级限制：若将`doctor.textsimilar`加入My Wheel，旧版Windows不认识该动作ID，可能整份设置加载失败回默认、下次保存覆盖；降级前保留升级前设置备份。设置schema未变不代表旧版能读新动作。

与Mac的差异：Windows本批只列独立文件对，不合并关联分组；不会先剔除精确重复文件。支持扩展名/严格解码/扫描预算也不同，因此结果、百分比和排序不承诺跨平台一致；Mac另有代码、JSON/XML/HTML等文本扩展名，Windows本批尚未加入，而非平台不兼容。


## 0.41增量

先pull Alreadyuptodate originb9d6020/local76fbccb，无新远端Mac；122Mac/80旧ZIP按开始及最终时点保留。新增默认关闭Doctor的显式相似图片配对，先选目录/确认再有限读取。PNG/JPEG/BMP/TIFF/支持静态GIF规范化SDR，16MP/16384，每图16MiB/总128MiB/200尝试/60秒软限（包含解码，阻塞codec可晚回）；metadata另root+4/20K/30秒。完整byte快照后WPF只看MemoryStream，源/root/祖先至解码指纹最终stamp，硬链接一次/变stamp失效旧签名，Native安全层不变。

原创9×8整数格均值/64横向明暗比较位，alpha先铺白，<=6位不同（>=90.625%，显示截断两位）即配对；过小/均值范围<16/popcount不足4或>60跳过，会漏平坦图/单向渐变，100%也可不同颜色/细节/比例，不是对象/语义识别或图片身份。跨支持格式，旋转/裁剪等会漏，HEIC/WebP/AVIF/动画/HDR/高精度仍待做或拒绝。Mac自身网格/阈值/限制及group/先排精确重复流程不同，不保证同结果。Fluent原生虚拟配对/问题tabs、固定footer/DPI、前1000Ordinal路径对而非最佳，所有issues/Partial零匹配仍可见；只明确复制路径/位差/原因，绝无图片内容/打开/删除/保存/联网。

93312常规核心通过，43481独立探针和坏decoder FormatException P2修复复核通过；翻译/链接与最终agent/native仅编译证据见TESTING。Windows实机WIC/FS/GUI/ARM64未执行；未写Mac实现/测试/构建/安装。MyWheel含新ID旧0.40可能整份设置加载失败回默认，降级前保留旧备份。52项仍20限定实现/15部分/15待做/2平台差异、普通剩余30；F04部分，常用目录入口/清理/可靠回收站撤销仍需做，不把未完成当不兼容。

期间另一个Mac会话提交3f4e3a7安全补强与8db4e8d体积优化：剪贴板完整快照/累计预算、缩略图像素上限、草稿云目录规则、Doctor/MCP身份与历史保护，另去除未用代码/字符串并调整根macOS build.sh符号处理。已只读核对并原样保留，未运行该脚本或Mac app/test/build/install；相对本批122文件起点当前112相同/10并发变化（含根build.sh），最终差异以外置时点审计为准。


## 0.42增量

先pull Alreadyuptodate originb9d6020/local4bd7470 Mac1.12.5，无新远端Mac；0.41已核对Mac并发安全/体积及版本，122Mac/82旧ZIP按时点保留，不写/运行Mac实现/测试/构建/安装。AppRouting默认关闭下新增完整预览文件路径交接草稿：1–100普通本地源全部输入/祖先hold、只取路径不读内容；严格UTF16验证、JSON可见ASCII转义、完整四语只读请求，12000 UTF8字节预算整拒无截断。Codex/ClaudeCode闭集单值URL编码，2048 ASCII地址保守门限，过长仅禁Open、Copy保留完整draft；固定Windows协议交接，无cmd/额外目录参数/附件/自动发送，handler不可用保留窗口可copy。接收app已有上下文/权限/网络不受此请求文字强制约束，用户在接收端再次审核发送；源会在关闭窗口后可变，路径不是内容快照。

原生Fluent Combo/完整JSON、草稿及可打开地址readonlyscroll/fixedbuttons/status/DPI，MyWheel新ID旧0.41拒绝可能整设置重置/覆盖，需降级前旧备份。无schema/依赖/原EXE或Web功能改动，Mac窄workspace不自动移植；G01助手客户端/凭据与G02MCP仍未做。依据[OpenAI官方深链](https://learn.chatgpt.com/docs/reference/commands)和[Claude官方深链](https://support.claude.com/en/articles/14729294-open-claude-desktop-with-a-link)，日期2026-10-24；当前未实测Windowshandler/剪贴板/GUI/ARM64，93523Core通过、最终独立检查见TESTING。B04原构建/全预览/URL编码/闭集范围限定实现，52项21实现/15部分/14待做/2差异，普通剩余29，不等于整体兼容/实机完成。

## Windows 0.43 PNG无重压缩元数据子范围

先git pull --ff-only origin main返回Already up to date，HEAD及origin/main均5b83c0c/Mac1.12.6(34)。相对0.42没有新Mac实现；远端已包含上批只读核对过的7a16d7f绳索/正规文件复核/reopen token及5b83c0c版本。保存0.42 Windows、122Mac文件和84旧ZIP基线；本批不写/构建/测试/运行/安装Mac，任何并发改动按最终时点审计保留。

稳定image.pngmetadata显式单个本地PNG，由Files/Quick Actions/My Wheel进入。原创容器编辑器32MiB/4096块（含IHDR/IDAT/IEND）、16MP/每边16384、支持SDR精度；全输入私有快照、全块CRC（包括移除块）、ASCII类型/reserved位、IHDR字段、PLTE/tRNS基本约束、连续IDAT及非空总负载、最终零长IEND/无尾随数据。动画acTL/fcTL/fdAT、dSIG/caBX已识别签名/凭证、cICP/mDCV/cLLI复杂HDR、未知critical/16bit/损坏边界整拒；这是有界结构子集，不是全部ancillary语义/PNG标准认证。

只移除全部tEXt/zTXt/iTXt/eXIf/tIME块，不解析/展示/解压其payload；保留IHDR/PLTE/tRNS/IDAT及其他块原字节、CRC、顺序和相对IDAT位置，不重新编码图像。ICC/色彩/未知辅助块仍保留且可能含私人信息，图像/文件名/文件系统信息也可能泄露；不是完整隐私清除。EXIF方向被移除可能改变其他应用的旋转/镜像显示，原像素不旋转。依据[W3C PNG3](https://www.w3.org/TR/png-3/)的chunk/CRC及仅改ancillary的保留规则事实，未复制实现/添加依赖。

NativeFiles.Input和所有祖先全程held，先Core完整计划及clean snapshot，再由现有ImageTools.DecodeSnapshot仅对clean快照执行原built-in PNG/单帧/SDR/尺寸/色彩验证；保留的ICC等仍可能由原生decoder解释。被移除的text/EXIF不会送进WIC；不能称完全不解析元数据。取消检查贯穿快照/CRC/输出和STA边界，Quit等待收尾。原生Fluent只读JSON路径/四字符chunk类型/组数量/含12字节framing的总大小/去留，固定Save/Cancel、滚动、workarea/DPI及四语言；不显示raw私密payload、不用归一化图冒充原显示预览。没有匹配块时UI禁Save且服务再次拒绝。独占旁输出“text-exif removed.png”，不覆盖、不回写源，不复制文件系统时间戳/属性/ADS、无剪贴板/网络/持久历史；失败/取消只清自有stage。

93820常规核心通过；原生应用/测试交叉编译0警告/0错误，独立agent最终结果见[TESTING](../Windows/TESTING.md)。Windows/WIC真实色彩/方向/显示/FS锁/取消与UI/Narrator/DPI/ARM64未实机执行。C06仍partial（选择性tag/无损JPEG/TIFF/完整清除保真仍缺），52项21限定实现/15partial/14pending/2platform、普通剩余29不变，不把子范围提升冒充整项完成。新MyWheel ID使0.42及更旧版可能拒整份prefs、回默认并后续保存覆盖；降级前保留升级前备份。NativeFiles/ImageTools旧算法/Prefs schema/开关/依赖不变，84旧ZIP保留。


## Windows 0.44 JPEG无重压缩元数据子范围

先git pull --ff-only origin main返回Already up to date，HEAD及origin/main均c2b72a7/Mac1.12.6(34)，与0.43结束时相同，无新Mac实现。c2b72a7只新增根README的Mac下载链接，已在上批核对并保留。本批起始保存0.43Windows、122Mac和86旧ZIP基线，不写/运行/测试/构建/安装Mac；任何并发变化按最终时点外置审计保留。

稳定image.jpegmetadata显式单个jpg/jpeg。原创有界容器编辑器：源32MiB、16MP/每边16384、8bit SOF0 baseline或SOF2 progressive、1/3组件（采样H/V1–4，采样块总和<=10），本工具最多65536标记（含SOI/EOI/SOS/RST）/512 scans，不是JPEG标准上限。检查SOF/SOS精确长度/组件唯一/选择/谱区间/近似字段；扫描FF00单字节stuffing、真实marker前FF填充、连续扫描和精确末尾EOI/无尾随。RST须启用非零DRI且RST0–7循环，每次SOS编号从0重置、DRI跨scan保持，DRI0关闭；检查序号不等于检查MCU间隔。填充FF归后续真实marker，移除该marker时一起移除，保留scan中的stuffedFF/restart字节不重写。不存在PNG式逐块CRC，结构+native decode不能保证发现每个压缩位损坏或完成完整JPEG/Huffman/渐进语义认证。

只移全部APP1（EXIF/XMP/其他）、APP13（Photoshop/其他资源）和COM，不解读其payload或跟随任何内容链接；marker-like payload当不透明字节。其他支持的marker/表/帧/SOS/压缩扫描保持原字节原顺序，不重新编码。ICC APP2仅接受ICC_PROFILE\0头，序号1..N唯一完整/总数一致且第一scan前；分片物理乱序允许，不重排、不解析ICC正文。非ICC APP2（MPF/增益图等）及全部APP11（可能JUMBF/凭证）整拒；同时拒算术/无损/层次/差分/CMYK/高精度/其他编码marker/多帧及尾随数据。不是全面识别HDR/凭证：ICC、APP0缩略图、Adobe APP14、其他保留数据仍可能含HDR/私人信息；移除APP1/APP13可能改变方向/色彩解释/裁剪/应用显示，不保证渲染像素或视觉一致，也不是全部隐私清除。只承诺保留的压缩数据与segment bytes不变。依据[T.81 JPEG事实](https://www.w3.org/Graphics/JPEG/itu-t81.pdf)及[ICC嵌入规则](https://www.color.org/profile_embedding/)，未复制第三方实现/新增依赖。

NativeFiles.Input及祖先贯穿完整load/preview/save，私有snapshot→Core全结构预检→clean bytes→现有ImageTools.DecodeSnapshot只验证clean（原built-in JPEG/单帧/SDR精度/尺寸/色彩gate保持）；保留的ICC等仍可能被WIC解释，不是完全不解析元数据，也不能从8bit推导全面非HDR。Fluent完整只读JSON路径/类型-数量-含framing大小-去留概要、baseline/progressive/scans/markers/restarts、固定Save/Cancel、滚动/workarea-DPI/四语言；不展示rawpayload、不用归一化图冒充原始显示预览。RST计入总marker和scan数据行，不另加虚构summary行。0matches界面和服务双拒保存。保存原件旁“exif-comments removed.jpg”独占副本，原件与旧副本不覆盖，不复制文件系统时间戳/属性/ADS。取消贯穿字节页/marker/输出/STA边界，Quit等待清理；无网络/自动剪贴板/外部启动/持久历史。PNG仅当前说明文字修正JPEG已有受限入口，PNG Core/Document和NativeFiles/ImageTools算法均不变。

93966常规核心通过，应用/原生检查交叉编译0warnings/0errors；独立agent最终证据见[TESTING](../Windows/TESTING.md)。真实WindowsWIC/颜色/方向/显示/文件锁/失败清理/GUI/DPI/Narrator/ARM64未执行。C06仍partial：选择性tag、无损TIFF、完整元数据/隐私保真及更完整JPEG类型尚缺；52项21限定实现/15partial/14pending/2platform，普通剩余29不变。新MyWheel ID旧0.43及更早可能拒整份设置/默认并后续保存覆盖，降级前保留升级前备份；Prefs schema/默认开关/依赖不变，86旧ZIP保留。



## Windows 0.45：主音频活动配乐压低（2026-10-24）

批次开始GitHub pull已是最新c2b72a7/Mac1.12.6(34)，相对0.44没有新Mac实现；保存Windows/122Mac/88旧ZIP基线，保留根README原下载链接。本批只改Windows及Windows相关文档，不运行/构建/测试/安装Mac。

既有WAV音乐窗口可选默认关闭的RMS活动压低，主选区<=600秒且128MiB PCM，约20ms全声道平方RMS在主增益/完整fade后、削波量化前计算，阈值相等活动；末窗用实际长度。将音乐已设音量乘以0..100%，完整压低约20ms/恢复约250ms，部分反转继承状态；preview/save各自先分析完整trim再从trim0混音，10秒试听不缩短分析/fade。关闭时旧混音byte保持，无新限额/读取/倍率分支。Fluent原生checkbox/数字字段/四语/固定footer，源/祖先两份lease及独占原件旁输出保持，参数变更取消试听且App等待cleanup。无新增设置schema/ID/依赖/模型/麦克风/网络。

这只是音量活动近似：噪声/音调/音乐也触发，轻声可能漏检，窗口内可能提前响应；不是Mac人声分类、语音转写/字幕烧录或保音高。两源仍要求相同采样率整数PCM，自动重采样/交叉淡化/压缩音频/视频配乐仍待做，不称平台不兼容。220513核心检查通过，独立检查见Windows/TESTING；原生仅编译，Windows播放/听感/性能/文件系统/WPF/DPI/Narrator/ARM64仍待验收。D06改为部分实现，固定52项21限定实现/16部分/13待做/2平台差异，普通剩余29，不是工时或完成百分比。


## Windows 0.46：可选WAV配乐循环交叉淡化（2026-10-24）

开始先git pull，Already up to date at c2b72a7/Mac1.12.6(34)，较0.45无新Mac实现。保留0.45Windows/122Mac/90旧ZIP基线，Mac代码/测试/构建/安装不写也不执行，根Mac下载链接保留。

audio.music原生Fluent窗口可选Loop Crossfade(ms)，0默认沿用旧mix/活动压低；Loop启用才解析，有限0..1000ms、最近整数帧至少2且不超过音乐半长。P=N-C首次播放原[0,P)，随后每P帧先尾[P,P+C)与头[0,C)线性权重叠加（精确端点），再播放[C,P)。周期减少C帧；不变调/自动节拍匹配，反相可能抵消、响度/节奏改变，不能保证无咔哒或无缝。先各本源声道浮点混合，后旧mono/stereo/整段musicfade/duck/最终量化；主时长/格式保留。短overlay可能未到第一次接点；preview使用同历史、最多10秒PCM16，高位深保持旧直接PCM16舍入。

新路径只有<=4096帧短音乐缓存、两份4096帧raw及固定double块，大音乐按周期/overlap/块边界读取，不逐样本I/O。非零无效设置在duck内容/header前整拒。两源/祖先持有、NativeFiles独占旁输出/自有stage清理、Stop/关闭及App等待preview保持。四语/数字字段Loop启用联动/滚动固定footer/DPI，无新ID/Prefs/schema/依赖/联网/模型/Mac写入。224647核心通过，独立agent见TESTING，原生只编译；Windows实际播放/听感/性能/文件系统/GUI/Narrator/ARM64仍待执行。D02补实用增强，固定52项21限定实现/16部分/13待做/2平台差异、普通29不变，不换算总体完成百分比。自动混音重采样/等功率/节拍匹配、压缩/浮点/视频配乐、语音分类/转写/字幕烧录仍待补。


## Windows 0.47：明确有损GIF减色与完整准备预览（2026-10-24）

先pull Already up to date c2b72a7/Mac1.12.6(34)，相对0.46无新Mac实现，保存0.46Windows/122Mac/92旧ZIP基线，Mac实现/测试/构建/安装不写或执行。新单图image.gifpalette原生Fluent选16–256最大条目/明确Generate/原图与减色tab/固定SaveStopCancel/四语言，完整尺寸处理后同一Result保存，<=960最近邻缩图不参与算法/统计且可能隐藏色带。

原创5bit每轴32768桶/intcount+longRGBsum及加权medianbox，固定轴RGB/binId/生成顺序tie、clamp非空、原8bit均值halfup、bin-to-box映射，不做nearestcolor/dither。完整alpha校验，半透明拒绝，透明独立槽且hiddenRGB归0，输入已符合上限保留opaqueRGB/旧GIF bytes；实际palette可少于目标。只报actualpalette/max/changedopaque，不伪称原色总数或感知质量。图像可色带/颜色改变，输入仍是受限静态8bit SDR/16MP/16384/128MiB源文件(非总RAM)。Gif不含metadata/profile，旧GifExport/转换/预设/安全层/解码边界保持。

Result私有像素/缩图副本、Preparation绑定doc Owner；源/祖先贯穿加载/fullpreview/save及取消，参数变/Stop/失败清旧并禁Save，token/revision拒晚结果，App等待Finish再保存/Dispose/Quit。独占原件旁reduced colors.gif，无覆盖/网络/剪贴板/历史/model/第三方依赖或Prefs schema变化。新ID保存MyWheel后旧0.46及以前可能整份设置拒绝/回默认并后续覆盖，降级前保留升级前备份。369902核心通过、独立见TESTING，native仅编译；Windows真实WIC/量化质量/内存速度/FS/WPF/DPI/Narrator/ARM64未执行。C02仍partial，动画/HDR/高位深/CMYK/扩展格式/抖色/预设量化仍待做，不写平台不兼容。52项21/16/13/2、普通29不变，92旧ZIP保留。

## Windows 0.48 登录启动记录兼容范围

0.48 登录启动记录：显式只读 HKLM/HKCU 的 Run/RunOnce、请求32/64位八个位置；保留逐位置状态和字段问题，命令默认隐藏，明确显示后才进入详情与复制。不会执行、展开或修改命令，不判断应用是否启用/存在/可信/影响启动。

请求视图可能共享或经注册表链接，底层键身份/目标未验证，不跨位置合并；ARM32特定视图不覆盖。枚举不是原子快照，位置不稳定，根缺失/权限或字段失败/超限/重复全部可见。不检查StartupApproved、Startup文件夹、计划任务、服务、Store、其他用户、签名或可执行文件；深诊断/实际启停和修复仍待补，不声明平台不能兼容。名称直接显示可能含私隐，命令需明确开关才进入详情/复制；空白记空、其余保留并可见转义，不展开环境或解析执行。微软对Run命令的260字符描述不构成本工具2048UTF16只读预算的可执行保证。[Run/RunOnce](https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys)、[原生枚举契约](https://learn.microsoft.com/en-us/windows/win32/api/winreg/nf-winreg-regenumvaluew)、[WOW64视图](https://learn.microsoft.com/en-us/windows/win32/winprog64/accessing-an-alternate-registry-view)。

独立实现和检查仅Windows及Windows文档，旧安装记录报告保持。Windows API、注册表别名/并发、原生GUI/复制/DPI/Narrator/x64/ARM64仍未执行；核心和交叉编译不算实机验收。52项21限定实现/17部分/12待做/2平台差异，普通剩29，参见[进度表](../Windows/PROGRESS.md)和[0.48移植记录](../Windows/PORTING.md#048已交付)。

## Windows 0.49 G.711 兼容范围

显式严格RIFF WAVE的G.711 A-law/mu-law（tag6/7）8bit码字→PCM16，保留容器rate/channels/framecount，不能恢复压扩损失。ITU推荐的电信rate为8k，本工具可保留8–192k容器rate，但不承诺高rate电话互通/恢复原带宽。实现依据[ITU-T G.711](https://www.itu.int/rec/T-REC-G.711-198811-I/en)、[WAVEFORMATEX](https://learn.microsoft.com/en-us/windows/win32/api/mmreg/ns-mmreg-waveformatex)、[RIFF](https://learn.microsoft.com/en-us/windows/win32/xaudio2/resource-interchange-file-format--riff-)与[格式登记](https://www.iana.org/assignments/wave-avi-codec-registry)；原创算法，无第三方应用codec依赖。

严格18bytefmt/cb0/fact唯一且frame=dataBytes/channels/data唯一非空整帧/精确RIFF及oddpad/1024chunk。部分producer输出fmt16/无fact/缺pad，当前拒绝，不猜测修复；raw/RTP/AIFC/Extensible/RF64/ADPCM/MP3/AAC等未开放，不宣称平台不可兼容。只转换主data，metadata/markers/loops/unknown丢弃，44+2*dataBytes完整目标512MiB/2h预算也约束试听；无增益/归一/重采样/dither/编码。NativeFiles源及祖先贯穿原生modal/试听/旁输出，前10秒与保存PCM16前缀相同，退出前等待清理，不影响旧整数/浮点/AIFF流程或Mac实现。Windows真实文件/API/播放/GUI/DPI/Narrator/ARM64仍未执行，开发机算法/系统decoder/编译均不是实机验收。52项21/17/12/2、普通剩29，见[进度](../Windows/PROGRESS.md)与[0.49边界](../Windows/PORTING.md#049已交付)。

## Windows 0.50 条件能力与系统组件

audio.systemdecode 使用系统MediaTranscoder流式输入准备完整PCM16，静态文件头只开放RIFF/WAVE、LayerIII MP3、ADTS AAC和FLAC，ID3头严格版本/flag/synchsafe/32MiB界限。输入/准备目标各128MiB、mono/stereo8–192k/2h；目标完整验格式/预算后才允许试听和独占旁输出。输出去metadata/视频/封面/cues/loops，系统decoder精度/priming/padding可变化；M4A/MP4/播放列表/其他容器待做，缺codec/DRM/替换rate/ch拒绝，无自动下载/麦克风/网络。系统版别/真实文件互通及性能未验收。

pdf.raster 使用Windows.Data.Pdf只读固定源流，10源/每源32MiB/总128MiB/1000页；Parse ASCII页序允许倒序与重复，50选页/16MP每页/100MP整计划/400–4000长边，0/90/180/270转向；Save前重验计划与真实来源几何，PNG渲染流80MiB、完整PDF输出512MiB。DIP不是PDF点，统一FromDip×0.75，Letter816×1056DIP→612×792pt并含A4/旋转回归。原创栅格PDF写入保物理纸张尺寸；白底，所有矢量/文字搜索/结构/交互/metadata/签名不保，渲染/色彩/透明/批注可能变化，不冒充保真PDF编辑器。所有输出在首源旁；多文件过程中失败/取消保留已经发布的输出。

TextCapture新增枚举末尾默认关闭；text.ocr仅显式图片、本机已装语言和Recognize，结果可编辑后明确复制；完整UTF16/noNUL/262144上限，不截断。WPF图片加载沿用OnSta，白底合成，原尺寸超过OcrEngine.MaxImageDimension拒绝，不暗缩。NativeAsync先请求Cancel，再await不绑定token的真实终态，最后Close，任何源/bitmap/stream不会因token任务提前完成而被释放；Close/Quit/feature-off等待清理。此软取消不能保证系统调用立即结束。BoundedStream写预算覆盖Seek/Position/SetLength/Write，兼容write-only staging，dispose视图不dispose调用方流。

**PDF/OCR目前只实现有身份的条件路径**：按[微软桌面WinRT支持列表](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/winrt-api-desktop-app-support)，实际GetCurrentPackageFullName在打开PDF/OCR输入前检查；无身份ZIP明确不可用。并非实验已证明所有无身份调用必然失败。可选Windows/Packaging/build-msix.ps1只用本机Windows SDK MakeAppx产生未签名MSIX：Win11最小22000、唯一runFullTrust、四语言、原创三枚图标，源目录及子项拒reparse、LiteralPath含隐藏项复制、独立stage、File.Move不覆盖，不下载/签名/信任/安装。无实际MSIX产物/MakeAppx/真实身份验收；runFullTrust不提供网络隔离。用户需在Windows完成匹配publisher签名/证书信任/安装及功能验收，不能把编译当ZIP可用性证明。

TFM net10.0-windows10.0.22000.0，新增微软SDK.NET.Ref10.0.22000.57 Windows profile中的Microsoft.Windows.SDK.NET.dll和WinRT.Runtime.dll2.2.0，仅两项投影运行库；SDK工具/winmd/源生成器/WinUI/WindowsAppSDK不分发。独立核对[SDK REDIST](https://learn.microsoft.com/en-us/legal/windows-sdk/redist#microsoftwindowssdknetref)明确列两DLL，附未修改许可RTF/官方REDIST HTML/准确CsWinRT release许可，运行库需逐字节匹配官方nupkg。.NETCore/Desktop runtime仍10.0.13，避免全局RuntimeFrameworkVersion误用于SDK.Ref。新Feature/MyWheel ID可能令<=0.49整Prefs拒绝/default/随后覆盖，降级保留升级前备份。

553971常规核心通过，原生套件交叉编译0warnings/errors仅；独立agent见TESTING，一个DIP→point P2已修复回归。WindowsAPI/codec/PDF/OCR/GUI/FS/播放/DPI/Narrator/ARM64和PS/MakeAppx/签名/安装均未执行。C09/E02从待做到条件部分，52项21/19/10/2、普通剩29；全部媒体、保真PDF、多语言OCR质量仍待补，不称整体完成。


详情见[0.50范围](../Windows/PORTING.md#050已交付)及[进度](../Windows/PROGRESS.md)。

## Windows0.51 QR Garden边界

原生模板Tree/Bouquet/Island、季节Summer/Spring/Autumn、明确打开3D Garden。原创模块浮雕不是直接复制Mac SceneKit Builder，也不冒充完整Mac树/花束/岛屿或季节动画；全部v1–40/L/M/Q/H复用原QR模块，无暗中级别转换/文本截短。深色模块单位方柱坐标与原矩阵逐格一致，白色底板含完整四模块quietzone，所有额外装饰位于板下；高度/配色由模块坐标确定，不读时间/随机种子/ID。最多n²+6boxes，分颜色合并mesh，每盒24vertices/36indices，冻结完整Model3DGroup后跨线程交付；真实WPF/3D显示与性能未执行，预算不是GPU/RAM硬限制。

正交相机旋转/20–90°观察角、Top View/Reset View，单次后台生成/Stop/Close，控件和状态四语、正文滚动/固定footer/workareaDPI。修复P2：原camera.Width只按横向会裁掉宽viewport中的上下模块；现在Core.ViewWidth=(n+8)×1.65×max(1,w/h)，覆盖所有box原点球界，SizeChanged刷新。独立角点投影覆盖宽/窄/正方形窗口、yaw/elevation以及v40；这是数学/源码检查，不能当实际渲染或扫码。光照/视角/屏幕和密集码可能不可扫，继续用原平面复制/PNG分享打印；不导出3D模型。

QrDialog在子modal前先设置TCS，子finally等待生成结束，父Appfinally等待QrDialog.FinishAsync，feature-off/Quit与关闭等待生成并抑制迟到model。Loaded单飞，取消后只清私有模型，不触碰输入文件/剪贴板/网络。旧QR编码/平面renderer/PNG/目录独占安全层不改。

QrEntry追加可选枚举Template/Season，默认Tree/Summer用WhenWritingDefault省略，旧默认prefs/backup字节保持；非默认与稳定ID/文字/分组/Correction一起保存。QrLibrary仍Version1/32/1MiB，闭集可选小写字符串字段，未知/重复/null/错误类型整份拒绝；同名任何choice不同整批冲突，不悄悄覆盖，Preview展示选择。Mac导入已知tree/bouquet/island和summer/spring/autumn，缺失/null/空用Tree/Summer，未知每行标issue并保存raw值预览，其他兼容条目可导入；创建日期不迁移。<=0.50设置解析可忽略新字段并在重写时丢选择，含新字段备份被旧闭集reader拒绝，降级须升级前备份；不夸大成所有旧设置都会拒绝。

4432151常规核心通过、native套件编译0warning/error仅，独立最终见TESTING。实际WindowsWPF/mesh/渲染/焦点/DPI/主题/Narrator/扫码/性能/ARM64均未执行；只源码检查的原生fixture新增冻结mesh/indices/normals/单次生成/关闭等待及隔离host。E06从pending到partial，21/20/9/2普通29，完整Mac造型/动画和其他未完成项继续跟踪，不称全兼容。


见[0.51完整范围](../Windows/PORTING.md#051已交付)。

## Windows0.52 Assistant边界

（枚举末尾）、独立配置页；Quick Actions中Ask，首await前workFeature绑定、统一Busy/ownerclosed/feature-off/Quit取消。AssistantPlan.Offered只取当前enabled/文件条件匹配的固定Catalog.Actions，不提供自定义App/预设标题/QRpayload。文件只作固定类型计数，无内容读取，不添加名字/路径/任意扩展名；输入请求2000UTF16原样发送（用户可自己输入敏感内容，预览必读），最多160候选、完整JSON32KiB。发送前显示精确body及canonicalendpoint，复选确认+显式Send，任何请求更改使旧preview/result不可用。回复只SelectSuggestedAction→当前候选二次解析/文件snapshot核验/搜索清空重排；用户仍须Return/RunSelected，模型不携带参数、不执行任何操作。

服务完整endpoint使用已有WebAddress严格authority/escape规则，额外拒绝query/fragment/凭据；HTTPS或明确localhost/127.0.0.1/[::1]HTTP。模型闭集ASCII字符128、key可见ASCII1–2048无空格/CRLF，输入框不截断长paste。Chat Completions或Claude Messages，不调用模型列表/自动keytest/下载；max_tokens1024或Chat显式max_completion_tokens1024，Claude header2023-06-01。直接HttpClient/SocketsHandler：无redirect/cookies/proxy/defaultWindowscredentials/压缩、16KiB响应header、10秒connect、60秒整体linkedtoken，成功正文流式64KiB上限，失败正文不显示；无应用层retry/fallback。公司代理/其他认证、SSE/多选/tool_calls/tool_use/refusal/length/max_tokens等非完整回复不支持。严格全层duplicate检查/单assistant文本/stop或end_turn，内层恰action_id(null或closedID)+explanation<=512，外包Markdown/未知字段/参数拒绝，不打开链接。

APIkey只明确SaveKey进入本机当前用户Windows Generic Credential，target按provider+scheme+host+effectiveport，同服务path共用；Read只发生Send操作，改host找不到旧key，不把其转发别处。CredWrite/Read/Delete/Free仅System32 advapi32，CREDENTIALW x64/ARM64 size80，PersistLOCAL_MACHINE（当前用户本机），native blob临时内存清零/free，管理string抹除不保证。通用凭据不是每应用隔离；测试不触碰真实credentialstore。非秘密AssistantSettings为Prefs可选null省略，旧默认byte保持；新Feature使<=0.51可能整设置拒绝/回默认/随后覆盖，不启Feature旧版也可能丢新服务配置，降级保留升级前备份。

常规Core4432330通过，native仅编译0warning/error，独立最终见TESTING；fakeHTTP和假native互操作库不是实际服务/Windows凭据/GUI/ARM64验收。SDK/runtime/Microsoft依赖及8notice保持，无新应用依赖/模型。G01pending→partial，52项21/21/8/2普通29；Doctor完整脱敏解释、动态动作与真实环境继续补。

协议依据：[Chat Completions](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create)、[Claude Messages](https://platform.claude.com/docs/en/api/messages/create)、[CredRead](https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-credreadw)、[CredWrite](https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-credwritew)、[CREDENTIALW](https://learn.microsoft.com/en-us/windows/win32/api/wincred/ns-wincred-credentialw)、[拒绝redirect](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.socketshttphandler.allowautoredirect?view=net-10.0)。不做实际外部API调用作为开发测试。


见[0.52完整范围](../Windows/PORTING.md#052已交付)。

## Windows0.53 Assistant边界

2026-10-25再次实际pull Already up to date c2b72a7/Mac1.12.6(34)，无新Mac。122Mac文件/104旧ZIP基线保留，无Mac实现/测试/构建/安装写入或运行。

PC Doctor基本快照窗口新增Explain Redacted Snapshot…，继承默认关闭Assistant、明确准备完整JSON/endpoint预览+reviewcheckbox+Send及0.52凭据/直连HTTP边界。DoctorAssistantReport精确Section/Title/Format白名单，PrivateDetails先跳过，只接受int/uint/long/ulong/double有限非负范围及闭集arch/power/DHCP/proxy状态。数值字段另验容量关系、整数计数/采样限度；无任意object.ToString/raw标题格式回显。OS描述、进程/网卡名、盘符、路径、地址、未知/private行剔除；保留省略数和匿名样本序号，无对应身份。GB/MB原始采样使用GiB/MiB，报告明确标记；working sets可共享，逻辑容量不是可回收空间，不推断泄漏/应用归属/联网。

DoctorReportSnapshot immutable字符串，在展示/准备请求间不会因原Values修改而变化；报告最多512事实/32KiB，最终双层JSON独立32KiB限额，任一超限整体拒绝不截短。另显示完整脱敏JSON和完整服务body；输入问题仍原样发送（用户自己可键入私密内容）。返回仅action_id:null+plain explanation<=4096UTF16；原动作模式512/8192限制及默认body字节保持。Doctor内层escapedJSON容许32768字符，以支持4096CJK\uXXXX；完整响应仍64KiB。正常LF/CR/TAB保留，格式控制符可见转义，不开链接、不执行命令、修复或动作；明确Copy Explanation才访问剪贴板。

Quick Actions提供单请求默认false勾选，只有明确同意才加入自定义应用/预设标签（可能含私人信息），否则固定内置候选与0.52字节一致。来源仍当前Catalog.Available/稳定ID，不加文件名路径内容/QRpayload；勾选与请求变化invalidatepreview/review/suggestion，正在Send时锁定。接受建议前完整Prefs.Serialize比对、文件snapshot/候选重验证；只选行，Return/RunSelected手动。偏好变化时不自动重试发送。

Doctor父窗口持有child+completionTask，App finally真awaitFinishAsync；设置Assistant/Doctor关闭立即关闭child并cancel，父关闭/owner/token/Quit均joinkeyread与HTTP，Busy在cleanup后释放，无嵌套RunActionAsync。无新设置schema/依赖/模型，沿用0.52降级风险及升级前备份要求。

常规Core4432386通过；native应用/测试0warning/error仅编译，独立最终见TESTING。基本报告外的FolderMap/duplicates/projects/largefiles/similar/monitor/appmemory/startup/installedapps解释继续待接；G01仍partial，52项21限定实现/21部分/8待做/2平台，普通29。真实Windows凭据/HTTP/TLS/GUI/DPI/Narrator/ARM64未运行，不以fakeHTTP/交叉编译作验收。


## Windows0.54 Doctor汇总边界

再次实际pull Already up to date c2b72a7/Mac1.12.6(34)，无新Mac提交；122Mac/106旧ZIP保持，无Mac源码/测试/构建/安装写入或运行。

九个静态Doctor结果窗口接入显式Explain Redacted Snapshot…：FolderMap、大文件、项目目录、重复文件、文本相似、图片相似、AppMemory、桌面安装记录、Run/RunOnce启动记录。DoctorAssistantSummary闭集typed工厂只序列化固定字段的primitive数量/decimal字节/布尔状态，private Build不接外部任意strings/dictionary；原record/Report/Text/名字/路径/标记/哈希/命令/内容/PID/时间戳从不序列化。根扫描已知逻辑小计/partial/entry-time限额、内容读取限额/问题数量、历史匹配数、已知工作集与未知数量、缺失注册表根均分别保留。全未知working set小计为null，测得0是0；不是实际物理/可回收RAM、安装/启用/可信/唯一应用数、可删除候选或文件当前相等证明。

在打开结果页前从完整记录计算immutable DoctorReportSnapshot；助手会话只保留该安全字符串，没有source或延迟factory，当前filter/selected/navigation变化不影响发送，不新扫描。报告明确完整已记录范围/限制，和最终服务JSON分别完整显示，继承默认关闭Assistant、准备/审阅checkbox/显式Send、只action_id:null/解释4096/明确复制、32KiB报告+最终HTTP独立整拒及64KiB响应。正常换行与Rune格式符显示沿用0.53；输入问题仍原样发送。

DoctorAssistantSession单次completion在Show前占有，持有parent/immutable snapshot/token；App九分支统一ShowDoctorOperationAsync try/finally真await FinishAsync，父Close/Doctor-off/Quit取消与子Assistant-off关闭后join凭据读取和HTTP，事件注销/关闭后按钮失效，无嵌套RunActionAsync，不释放Busy给未完成请求。使用现有Fluent Button/WrapPanel/滚动预览，Mac及旧扫描/文件读取/比较算法未改。

常规核心4432438通过，native应用/套件0warning/error仅编译；独立最终见TESTING。新增3key/9翻译，1557旧key/value保持；无Prefs/schema/依赖/模型变化，继承0.52降级备份风险。实时monitor解释、可选Doctor名称和更完整Windows诊断仍待做，G01仍部分，52项21限定实现/21部分/8待做/2平台、普通29。真实WindowsAPI/凭据/HTTP/TLS/GUI/DPI/Narrator/ARM64未执行，不把交叉编译/managed模拟当实机验收。

## Windows0.55 监控解释边界

实际批次先pull Already up to date c2b72a7/Mac1.12.6(34)，无新Mac提交。122Mac/108旧ZIP基线保留，无Mac源码/测试/构建/安装写入或运行。

CPU/Physical Memory窗口增加Explain Redacted Snapshot…，首条已记录采样前不可用。显式点击只捕获latest局部immutable UsagePoint，先占monitor completion再暂停UpdateSampling失效旧代次；不加history、进程名、路径、PID或系统时间戳，不新读API。DoctorAssistantSummary.Monitor只允许finite/nonnegative elapsed、closedCPUstatus、finite0..100%、CPU需两样本的nullable基线状态；memory%/used/total须全null或成套、total>0/used<=total、decimal百分比一致，available用ulong差。全未知保留null，不是0；字节支持完整ulong，不凭空限制长时间开窗。报告与窗口SingleSample标记明确一条采样，不能误称完整图表。

发送沿用默认关闭Assistant、独立完整snapshot+body/endpoint审阅、checkbox+Send、null-action/4096解释、双32KiB整拒/64KiB响应、明确Copy，不自动执行修复。监控解释期间Pause/Explain禁用至cleanup结束，防止子窗已关闭但凭据/HTTP尚待join时提前恢复；结束仍paused，只有Resume Monitoring明确操作启动新CPU基线。关闭/Quit/Doctor-off/Assistant-off均join子请求，StopAsync同时发起助手关闭与sampler停止，通过try/finally等待两路，即使一方失败也不遗留另一读取；阻塞系统API可能稍后才返回。

修复已有UI采样代次缺口：UsageSampler内部generation只覆盖返回前，PollAsync过去在Expected异常catch后可能于pause→resume时接受旧失败；成功结果排队等待UI回调也有相同窗口。本批NativePoll在await前捕获samplingVersion，每次真正采样开关变化递增，success/Expectedcatch两路回到UI均核验，旧代次整丢弃；CoreSampler算法/错误行为保持。新的Session.OpenAsync在同步进入ShowDialog前先赋自身completion，保留九静态窗口单飞/取消/真join。UsageNotices改“无自动网络”，明确助手审阅发送；AppMemory路径分组已存在的旧说明一并修正。

常规核心4432466通过；native应用/套件0warning/error仅编译。原生fixture新增受控fake读+Dispatcher pump、改变UIepoch后成功/Expected失败都不改history（不Show、不启动dialog timer、不读真实API）；单样本preview/范围说明、footer四按钮/首采样禁用也编译验证。独立最终见TESTING；6新key/18翻译，1560旧values保持。无新Prefs/schema/依赖/模型；现有0.52降级备份边界保留。G01仍部分：可选Doctor名称及更完整Windows诊断仍待补，52项21限定实现/21部分/8待做/2平台，普通29。真实WindowsAPI/GUI/DPI/Narrator/凭据/HTTP/TLS/ARM64未执行，交叉编译与managed stand-ins不是实机验收。

## Windows0.56 逐次所选名称边界

实际批次先pull Already up to date c2b72a7/Mac1.12.6(34)，无新Mac提交；122Mac/110旧ZIP保留，无Mac源码/测试/构建/安装写入或运行。

Doctor四种已有报告来源提供可选名称：基本快照中已经显示的process label、AppMemory已读exe路径的Windows字面basename、状态Present的安装记录名称、已知Run/RunOnce值名。DoctorLabels不新读文件/进程/注册表，不取版本/发布者/subkey/命令，也不在缺名时fallback PID/整路径。Windows路径按反斜杠解析，支持明确drive/UNC/extended drive/UNC；NT Device/pipe/仅根/尾分隔符/混合斜杠等不猜名称。基本process label继承原报告已有显示缩短，不额外截短。

候选和原source索引同步冻结，单行<=160UTF16、非仅空白、有效Unicode、无Control/Cf/行段分隔符；Cf按Rune覆盖补充平面。每来源最多4096（startup2048/facts512），超过来源预算整拒，不取prefix；所有合法候选都保留，按kind+原字符串Ordinal区分大小写去重，缺失/无效/重复计数分别显示。这里不保证组合符/变体符单独出现时肉眼可见，完整JSON会转义。候选仅本次内存，不存Prefs/历史/clipboard。

原生默认off checkbox展开虚拟ListView，用Ctrl/Shift由用户逐条明确多选1–40，初始无选中；没有自动top40/选前N。checkbox或选择改变即清空裸report+完整body、review、回复和复制/发送许可，选41项或双JSON容量超限保留原选择并整拒；不会偷偷缩短或取子集。发送前完整名称列表、精确report及最终body/endpoint均可审阅。传输中锁住列表/checkbox/query，关闭/feature-off/Quit仍取消和真join。Report只加所选{kind,text}及明确子集/不关联匿名指标的限制，不携带source索引/标识。原数值报告仍完整，默认off的safeJson/request与0.55字节保持。

名称字段本身可能包含路径/地址/秘密或其他私人文字：选择后切换准确NamedDoctorScope、预览可访问名称改为Doctor Report with Selected Labels，隐藏原来“名字/路径剔除”的默认说明。用户问题仍原样发送；字段只是untrusted data，严格解释模式action_id:null，模型不获得工具、修复/动作权限，不自动复制或打开链接。每次对话checkbox与选择重置，不保存同意。报告32KiB和最终HTTP32KiB分别整拒，响应64KiB/解释4096、服务凭据/直连/超时/降级备份等0.52–0.55边界保持。

常规核心4432524通过；native应用/套件0warning/error仅编译。独立最终见TESTING；15新keys/45翻译、1566旧values保持，无新Prefs/schema/依赖/模型。G01依据明示有限范围转已实现（动作建议+当前Doctor解释+逐次选择标签）；更完整诊断仍F09，MCP仍G02，真实Windows/服务验收另计。固定52项22限定实现/20部分/8待做/2平台，普通28；不是工时/批数/整体验收百分比。真实WindowsGUI/选择行为/DPI/Narrator/凭据/HTTP/TLS/ARM64未执行，managed替身与交叉编译不是实机验收。

## Windows0.57 安静样本辅助边界

每批开始先实际pull：Already up to date c2b72a7/Mac1.12.6(34)，无新Mac改动。122Mac/112旧ZIP保留，无Mac源码/测试/构建/安装写入或运行。

既有整数PCM WAV降噪窗口新增明确Find Quiet Sample、Play Suggested Sample、Use Suggested Sample。以当前合法噪声样本最近帧长度（0.05–2秒；11025Hz/0.05秒为551帧）作为固定窗，遍历从0开始的不重叠完整窗，最后补一个对齐源结尾的完整窗，起点重复不再计；不是滑动全位置搜索。全部声道逐样本平方和使用精确UInt128（32bit样本平方<=2^62，最大窗总和<=2^82），同长度比较/精确tie选最早，反相声道不抵消。RMS只为归一化指标，不参与候选比较。源仍限512MiB/2小时、mono/stereo8/16/24/32bit、8–192kHz；固定4096帧缓冲<=32KiB，读取数据不超过源数据加一个样本窗，忽略原有metadata内容，沿用严格RIFF验证。

查找只在明确点击后执行，不改当前输入范围、不自动试听/采用/处理/保存/复制，不添加开关/Prefs/schema/依赖/模型。安静片段可能是静音、轻声或音乐，不能称纯噪声/语音分类或Mac算法完全等效。界面明确建议先试听再用；Use把精确frame/rate边界以R文本写回现有字段，由原手动Range校验，不缩短候选。当前样本/强度变更会清候选、取消准备及停止播放；新Find先清旧候选。Find/所有试听共用pending单飞和CTS；占流时禁用Find/Use/试听/Save，Stop可取消，真正任务结束才恢复。关闭/关闭功能/Quit仍由既有App finally等待FinishPreviewAsync，源/祖先租约贯穿Find/试听/导出，不与保存抢stream位置。

原生Fluent按钮/文本、滚动主体与固定footer，工作区/DPI适配；候选显示准确起止和检查窗数。手动样本仍完全可用，不采用候选时原有降噪输出字节保持。无麦克风/模型/网络/新文件；输出仍原件旁独占非覆盖，旧安全层未改。真实录音质量/长文件时间/播放/WPF/Narrator/多DPI/WindowsFS/ARM64未运行，不能用managed替身或编译当实机验收。

常规核心4433582通过；native应用/测试套件0警告错误仅交叉编译。8新keys/24翻译、1581旧values保持，独立最终结果见TESTING。D03仍部分：只有能量候选辅助，不是自动纯噪声识别，压缩/浮点/视频降噪与真实听感待做。固定52项22限定实现/20部分/8待做/2平台差异，普通仍28，不是剩余批数/工时/完整兼容百分比。

## Windows0.58 配乐采样率准备边界

每批开始实际pull：Already up to date c2b72a7/Mac1.12.6(34)，无新Mac。122Mac/114旧ZIP保留，无Mac源码/测试/构建/安装写入或运行。

整数PCM WAV配乐选择后，采样率不同时先显示原生确认窗：两个完整来源路径、音乐原格式、目标主采样率、预计完整帧数/含头与pad字节，以及声音变化/削波/预算说明。取消不打开混音编辑器，不自动准备。明确Prepare Music后复用未改WaveConverter，完整准备为主采样率，保音乐原声道/位深；原Info始终描述原流，MusicInfo单独描述准备格式。匹配率路径不出现额外确认/转换，原默认输入/消息/输出保持。

WaveMusicRatePlan先校验来源数值/目标格式，按最近目标帧计算完整WAV（44/68字节头+数据+odd pad）并整拒>128MiB；未分配数组前预检。PreparedWaveMusic重新严格读原RIFF并核对确认时Info（rate/channels/bits/frames/dataOffset/dataBytes），精确长度byte[]+固定写流，无增长/ToArray副本；转换后实际Info/frames/length再次与计划核对，取消/错误不发布半准备对象。私有只读MemoryStream不暴露，只由ExportMix交原WaveMixer使用；显式拒primary/output别名。128MiB仅完整准备WAV，既有resampler表/工作缓冲额外最多约27MiB，不是整个进程内存峰值封顶。

主音频格式/时长保持，音乐时长按最近目标帧可能有微小变化；窗口化sinc滤波及原位深量化可能改变声音/削波，无dither/响度boost，编辑器显示准备率/帧/削波样本数。不是无损/保音调速度变化/节拍匹配。试听、循环（含交叉淡化）、活动压低及保存共同复用同一完整准备样本，试听仍PCM16前10输出秒；原手动转换与同率混音算法不改。音乐过大可明确先单独Convert WAV Format，压缩/浮点音乐和视频混音仍未开放。

原来源/祖先租约贯穿确认、准备、编辑、试听和保存；App准备await前后检查取消，feature-off/Quit不会迟到弹编辑器。既有pending/CTS单飞避免preview/save同时占流，App finally先join试听再释放准备内存和原input；准备等待Task真正结束后才Dispose，不新增临时文件、输出或网络。最终混音依旧第一来源旁独占非覆盖/owned staging清理，无新Feature/Prefs/schema/依赖/模型。

常规核心4433990通过；native应用/套件0警告错误仅交叉编译，独立最终见TESTING。11新keys/33翻译、1589旧values保持。D02限定整数WAV配乐扩展，不改变固定52项22限定实现/20部分/8待做/2平台差异、普通28。真实Windows/ARM64播放/音质/长文件性能/GUI/Narrator/DPI/文件系统尚未执行；managed替身与交叉编译不是实机验收。

## Windows0.59 只读文件预览边界

本批先实际pull：Already up to date c2b72a7/Mac1.12.6(34)，无新Mac。122Mac/116旧ZIP保留，无Mac源码/测试/构建/安装写入或运行。

主窗和浮动File Shelf新增明确Preview File按钮。只选一份引用；未选中沿用“全部引用”规则，但多份会拒绝，不做批量预览读取。单图片委托原image.preview，非图片新稳定file.preview只提供DocumentText.Extensions（TXT/TEXT/MD/MARKDOWN/CSV/TSV/LOG/SRT/DOCX/ODT/RTF/HTML/HTM）及WAV，Quick Actions/My Wheel可明确运行。扩展名只是路由，真正读入仍由现有严格解析器验证；不声称支持PDF、DOC、MP3、float/compressed WAV等。Shelf刷新/选择/拖入不触发预览、识别、播放或外部启动。

文档复用DocumentTextFile（8MiB源/2M提取文字及原ZIP/XML/RTF/HTML闭集预算），原件租约到modal关闭；DocumentTextDialog新增previewOnly=false默认，普通Inspect五格式导出UI/统计/保存逻辑保持。true时控件树无Save/格式选择/导出区，只有明确Copy Statistics及Close；统计针对全部提取文字，展示最多前65536UTF16且不切代理对，截断提示不再提导出。Copy Statistics手动按钮复制既有统计report（含显示来源路径/格式限制），文字可按系统TextBox手动选择复制。不是Office/浏览器布局，不执行脚本/宏、取资源、打开链接或生成文件；DOCX/ODT/RTF/HTML隐藏/字段/布局等既有限制完整可见。

整数PCM WAV复用WaveDocument，原512MiB/2h、8–192kHz/单双声道8/16/24/32bit验证/完整波形，来源祖先固定。新Fluent原生预览仅完整格式/波形、Play Original/Stop/Close，无编辑/Save/自动播放。明确Play才准备前10源秒PCM16，pending/CTS单飞，关闭/取消停止播放并等待真正SoundPlayer.Load/准备结束后才Dispose；App finally await FinishPreviewAsync再释放来源。小窗/多DPI按工作区fit、正文滚动和固定footer，实际Windows播放/质量/焦点/Narrator/DPI/文件系统/ARM64未验收。

file.preview作为通用动作无新独立开关，直接QuickActions/MyWheel的关联面板关闭由owner取消；主窗普通Files操作不依赖Shelf开关。只有两个Shelf按钮通过PreviewShelfFileAsync携带shelfPreview来源，canonical动作解析后、首await前要求Shelf已开并workFeature=Shelf（图片委托同样绑定），所以从主窗架子发起也会在Shelf关闭时取消/关闭并join。不要笼统称任意无关开关都会取消通用预览。Owner关闭/Cancel Operation/Quit继续统一Busy/取消/等待。App只读分支无Save调用，即使modal返回true也不导出。

无新Feature/Prefs字段/schema/依赖/模型/网络或后台扫描。默认Prefs字节保持；只有显式把新file.preview加入MyWheel才持久化新ID，<=0.58会拒未知ID，可能默认并下次覆盖设置，降级须保留升级前备份。常规核心4434087通过/native应用套件0警告错误仅交叉编译，8新keys/24翻译、1600旧values保持，独立最终见TESTING。A04非图片预览增强但仍部分：目录/虚拟云引用、其他格式/系统QuickLook与非激活焦点待做，不能称平台不兼容。固定52项22限定实现/20部分/8待做/2平台，普通28不变。

## Windows0.60 EXE结构诊断边界

本批先实际pull：Already up to date c2b72a7/Mac1.12.6(34)，无新Mac。122Mac/118旧ZIP保留，无Mac源码/测试/构建/安装写入或运行。

PC Doctor默认关闭，新增Inspect EXE Structure…和稳定doctor.executable。动作自己明确打开单本地EXE选择器，忽略无关当前files；workFeature=Doctor在选择器/首await前绑定，选择器返回和每次加载后检查取消，停止后不迟到读入/弹报告。NativeFiles.Input及祖先从严格来源检查一直持有到只读报告关闭，失败/取消释放。原AppLaunch PE启动验证不改，新工具绝不加载/运行/打开程序或修改权限/设置，不用Shell/新PInvoke/证书存储/网络/模型/依赖。

原创BCL流PE32/PE32+头部读取，标准字段依据[微软PE格式](https://learn.microsoft.com/en-us/windows/win32/debug/pe-format)，未复制第三方解析器代码。只读Machine/Subsystem/版本/Characteristics/sectioncount和CLR目录三态声明，不跟CLR RVA、不解运行时/AnyCPU/混合镜像/仿真/依赖/manifest/校验和/SmartScreen或Windows加载器接受性，不能据此说本机能运行。未知machine保留原ushort值/hex，不当平台不兼容。

策略来源<=512MiB、PE头偏移64..1MiB、optional<=4096、sections<=96；按实际optional长度核NumberOfRvaAndSizes，不静默截断。所有偏移/加法用long，头/SizeOfHeaders/section原始数据范围越界或不一致拒本有限预览，不等于完整加载器判决。单次缓冲<=4096、成功读入的主数据流头部字节总量最多8536（64+24+4096+3840+64×8），不是ReadExactly短读时反复传入的请求count之和，更不是物理磁盘读取量；DOS stub、section正文不读。EOF/I-O失败及取消不发布半报告，blocked选择器/系统读取可能返回后才完成取消。

Certificate目录index4按文件偏移而非RVA，只核目录边界/8字节对齐/不与image header或raw sections重叠，再遍历最多64个8字节WIN_CERTIFICATE头。每条原offset/length/revision/type完整保留，下一条按length向上8对齐；不读证书正文或证书padding，未知revision/type不丢。64条正好终点为完整覆盖，仍余声明范围才RecordLimit。未声明、矛盾、越界、重叠、记录上限分别显示，坏证书声明仍保独立PE头事实/明确partial；“头长度覆盖表”仅结构声明，不是签名验证。PKCS#7 type2头不代表合法签名，缺表不代表未签名（未检查外部catalog）；没有哈希/证书链/信任/吊销/恶意软件判断。

Fluent原生虚拟ListView/GridView显示事实/最多64证书头原数值、只读路径，正文滚动/固定Copy Report及Close/工作区DPI适配，无Run/Fix/Open/Save按钮。仅明确Copy写剪贴板，报告含显示路径、全部已记录事实/证书头（带列名）和完整限制，不自动保存或发送，也不接助手上传。当前字段是持有来源时的记录，不是释放后文件当前状态或安全证明。真实Windows文件系统/界面/主题/Narrator/多DPI/ARM64未运行。

常规核心4434529通过/native应用套件0警告错误仅交叉编译；45新keys/135翻译、1608旧values保持，独立最终见TESTING。纯核心可在Mac只读解析SDK生成的旧x64/ARM64 PE文件，不是运行Windows程序或Mac app。无新Feature/Prefs字段/schema/默认开关；新ID只有明确存MyWheel才持久化，<=0.59未知动作可能整设置拒/default/下次覆盖，降级保留升级前备份。F09仍部分，真实签名信任/打不开应用原因、运行时/依赖/修复及其他深诊断待做；固定52项22限定实现/20部分/8待做/2平台、普通28不变。

## Windows0.61 平面QR读取边界

本批开始实际pull Already up to date c2b72a7/Mac1.12.6(34)，无新Mac。122项Mac源码/构建/测试基线和120个0.1–0.60旧ZIP保持，无Mac写入/执行；仅Windows实现和相关文档。

默认关闭QR & Barcodes新增Read Flat QR Image…/稳定qr.readflat，自己明确选择一个本地普通图片，忽略其他当前选择。workFeature=QrCodes在选择器/首await前绑定；选择器返回和STA读取后检查取消，统一Busy/ownerclose/feature-off/Quit，等待真正完成再释放来源。复用ImageTools单帧SDR/128MB/16MP与NativeFiles.Input祖先lease，全尺寸规范化BGRA保持到只读结果窗口关闭；失败/取消释放。系统图片解码或原生选择器可能返回后才完成取消，不承诺即时中断。无相机/屏幕/剪贴板自动读取/后台扫描/模型/网络。

原创受限平面QR读取：正向（不含90度旋转）、正方形、完整精确四模块白色quietzone、每模块统一整数像素，所有像素alpha255/RGB各<=63为黑或各>=192为白，中间色/模块内不一致整张拒绝。复用原encoder标准表/format-version位事实/有限域余数，原Encode/Raster函数未改；QrCode仅加partial声明。候选版本最多40、原图最多16MP，先探finder角再逐像素核每候选；最多40次候选扫描是上限，不是实时性能保证。输出保留完整text/version/L-M-Q-H/mask/整数scale。

v1–40/L/M/Q/H函数图形（finder/隔离/timing/alignment/fixeddark）、两份format BCH和v7+两份version BCH全部精确匹配，不作BCH修复；unmask/之字遍历、remainder bits必须0，按短长block还原交织，所有RS parity精确比较，不进行任何损坏修复。数字/字母数字可多段，Byte模式支持明确UTF8 ECI26或无ECI纯ASCII，UTF8每Byte段独立严格解码；ECI其他值、未声明高位字节、跨段拆分UTF8字符、Kanji/Hanzi/FNC1/structuredappend等拒绝，不猜编码/丢数据/静默转码。闭集terminator/0对齐/交替EC11pad完整核对。文本最多8000 UTF16（标准最大numeric7089）；其他有效但不在此窄范围的码仍需外部读取工具，不标为平台无法兼容。

原生Fluent窗口只读来源/完整显示文字/版本级别scale长度，控制/格式/bidi/行段分隔字符显示[U+XXXX]，原文也可能含该字面标记，UI明确说明。只有明确Copy Decoded Text才复制原始全部字符（包括隐藏字符）；无自动open URL/run/save/library/send，不判来源可信，不把QR校验当身份认证。正文滚动、固定Copy/Close、工作区/DPI适配；原Windows图片编码器/QR生成/库/花园和旧输出保持。真实Windows/WIC/主题/剪贴板/Narrator/DPI/性能/ARM64仍未执行。拍照透视/旋转识别/边框容忍/缩放抗锯齿/颜色/透明/噪声/损坏修复/其他条码、更多编码与QR反向迁移仍待做。

4434908常规核心通过，应用/原生套件0警告0错误仅交叉编译；独立agent结果见TESTING。13新keys/39翻译，1653旧key-values不改，无Feature/Prefs字段/schema/第三方依赖变化。新动作只有明确存MyWheel才持久化，<=0.60可能拒未知ID/整设置回默认并下次覆盖，降级保留升级前设置备份。E05由待实现转部分；52项22限定实现/21部分/7待做/2平台，普通剩余28不变，非完整兼容或工时百分比。

## Windows0.62 Windows Doctor适配

按用户新增要求优先将Mac Doctor适配为Windows Doctor。本批先实际pull Already up to date c2b72a7/Mac1.12.6(34)，无新Mac；只读对照DoctorModel.swift/DoctorPane.swift的13症状分区，不复制Mac框架实现/缓存路径或第三方清理规则。122项Mac基线/122个0.1–0.61旧ZIP保留，未写入或运行Mac源码/测试/构建/安装。

现有PC Doctor用户显示统一为Windows Doctor（简中Windows医生/繁中Windows醫生/日语Windows診断）：侧栏、设置开关、基本报告标题、禁用提示、兼容条目和助手页面介绍。Feature.Doctor枚举/Doctor设置键/注册表位置、11个既有Catalog action IDs、PCDoctor后台类名及旧AssistantPlan请求指令字节保持，旧配置无需迁移。助手协议中的历史PC Doctor内部名称保留，不改变候选/网络正文；非当前界面品牌。1666旧翻译键值保留，新38keys/114翻译。新增通用分区标题翻译也会让中文/日语旧报告里此前未翻译的同名标题本地化；字段数值、私隐筛选及助手正文保持。

新增原创静态WindowsDoctor.Areas与原生Expander工作台：Summary/Storage/Caches/Folder Sizes/Duplicate & Similar Files/Project Builds/Slow or Hot/Apps That Will Not Open/Uninstall Apps/External Drives/Network/Background Items/Battery & Sleep，顺序对照Mac13分区，使用Windows说明/状态。所有分区明确显示Available with Limits或Not Implemented Yet，不靠颜色表意；仅Summary初始展开，可逐区键盘展开和滚动。展开/浏览不读系统或文件、不启动检测、不更改偏好。缓存和外接盘当前无动作/明确未实现；卸载分区仅安装记录，按钮名称不冒充卸载。其他分区逐一标出保真/数量/诊断限制：逻辑非可释放字节、哈希非删除建议、marker非配置、working-set非所有权/泄漏/热原因、PE/证书头非能运行或签名信任、Run记录非启用/可信、proxy配置非连通、充电非电池健康/睡眠阻塞。

按钮复用已有11个canonical Doctor动作；Summary/Network/Power另复用原Home-only临时doctor.check，不把它加入Catalog/MyWheel/助手候选或持久化，以保持原G01协议/配置。独立agent预审发现P1：最初对Home-only doctor.check作Catalog.Single会在主窗口构建时抛异常；已用WindowsDoctor.ResolveAction显式闭集分支修复，其他11动作仍canonical Doctor过滤。核心/真实源managed UI均须覆盖默认关闭时主窗所有分区构建、13区展开、每按钮路由、临时入口不扩候选，最终复核见TESTING。

Doctor默认关闭；关闭时分区/缺项仍可读，动作和原有固定Windows设置按钮均禁用。启用仅解锁明确点击，没有自动全机检查或健康分数。点击仍进入既有RunActionAsync统一Busy/ownerclose/feature-off/Quit、原选择器/内容比较确认、源祖先持有和取消真join；无新reader/PInvoke/外部进程/管理员权限/网络/清理/卸载/eject/删除/undo。可选助手仍独立开关、完整正文预览/明确Send/每次标签选择，无自动执行。打开设置只跳既有四个闭集页面，不改变设置。

4434979常规核心通过；应用/原生套件0警告错误仅交叉编译，独立agent最终见TESTING。Windows真实主题/焦点/Narrator/高对比/DPI/键盘/文件系统/ARM64未运行。原Windows读取器、文件保护、QR/图片/文档/音频算法、所有Catalog IDs/Feature/Prefs schema/默认开关/第三方依赖不变；不声称Windows Doctor已经具备全部Mac能力。F09仍部分，固定52项22限定实现/21部分/7待做/2平台，普通28不变。后续继续外接盘/深诊断/清理及可靠撤销等；不可把未实现写成平台无法兼容。

## Windows0.63 Windows Doctor磁盘查看

继续用户指定的Windows Doctor适配。开始实际pull Already up to date c2b72a7/Mac1.12.6(34)，无新Mac；122项Mac基线和124个0.1–0.62旧ZIP保持，无Mac源码/测试/构建/安装写入或运行。

默认关闭Doctor新增稳定doctor.drives/Inspect Mounted Drives…，忽略无关files，明确点击后才读取。WindowsDoctor的External Drives分区由planned转Available with Limits，说明只是已挂载盘符，不冒充外接USB身份/未挂载设备或安全弹出。共有12个canonical Doctor动作及原Home-only doctor.check；13导航ID/Feature.Doctor/原设置键/registry/schema保持，浏览工作台无I-O。

六个System32限定kernel32导入：[GetLogicalDrives](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getlogicaldrives)、[GetDriveTypeW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getdrivetypew)、[GetVolumeInformationW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getvolumeinformationw)、[GetDiskFreeSpaceExW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getdiskfreespaceexw)、GetThreadErrorMode、[SetThreadErrorMode](https://learn.microsoft.com/en-us/windows/win32/api/errhandlingapi/nf-errhandlingapi-setthreaderrormode)。原生声明BOOL32/DWORD32/三个ULONG64/Unicode/LastError；不引入依赖、WMI、shell、管理员权限或设备IOCTL。

最多26个A–Z根字符串，mask高位仅标unknown/partial。按先前报告的type门控：2removable/3fixed/5CD-DVD/6RAM才读文件系统和容量，4network/0unknown/1no-root/未来unknown值保留原uint类型并跳过这两次查询。GetVolumeInformation只给65UTF16缓冲（最多64内容）/flags，volume label/serial/max-component指针全null；不请求目录条目/文件正文/卷名/序列号。FILE_READ_ONLY_VOLUME 0x80000为声明true/false/unknown，不测试写入，false不证明有写权限。GetDiskFreeSpaceEx三个ulong不改为signed/double：available-to-caller、total-for-caller均可能受quota影响，volume-total-free另列，不据此算物理盘总容量、USB身份或可释放空间。未知值/API失败不填0；volume和space失败各保留即时Win32 uint原error，UI/复制分别加Filesystem Query/Capacity Query阶段。

这是多次历史系统调用，不持有物理设备身份，也不是一致原子快照。盘符可在type与后续调用间重指向；只能承诺“当时报告network类型则不调用FS/容量”，不能承诺系统绝无SMB/远程provider元数据访问。也不识别folder mount points/未挂载设备/USB/外接硬件；媒体可能被唤醒，系统调用可能阻塞且取消后才返回。没有主动HTTP/DNS/测速请求或文件读取扫描。

专用非线程池后台Thread，先取原thread-error-mode，再Set旧mode|SEM_FAILCRITICALERRORS，避免空光驱/可移动介质插盘错误对话框；setup失败立即完整标不可用，不继续查询。所有native调用均在同一同步worker，try/finally恢复返回的previous，不改变进程全局或用户设置，也不污染UI/线程池。恢复失败保留独立partial错误，专用线程结束；枚举返回0仍是失败（即使GetLastError0），不是“无磁盘”结论。取消在阶段前后检查，finally恢复完成之后Read结束/TCS才完成；没有用超时放弃真实native调用。App workFeature绑定首await前，await真实ReadAsync并再查token，feature-off/ownerclose/Quit不弹迟到窗口；等待的是读取/清理完成，不宣称使用Thread.Join或可强制中断设备驱动。

原生Fluent虚拟ListView/GridView完整盘符/type原code/FS/readonly/caller容量/volume free/rawflags/分阶段issue；选择详情显示全部列，正文滚动、固定CopyReport/Close与工作区DPI。无eject/write/fix/open/format/Save。复制明确点击才含全部记录/采样时间/错误/限制；无记录也显示并复制不能证明没盘的提示。没有自动持久化、剪贴板读取/写入、助手上传、修复或清理；只是explicitCopy写剪贴板。

4435097常规核心通过/native应用与套件0警告错误仅交叉编译，独立最终见TESTING。37newkeys/111翻译、1704旧values保持；原reader/NativeFiles/文件处理/助手协议schema不改，新动作会在Doctor启用时成为原助手可建议候选，仍手动Return，不等同旧候选正文完全相同。MyWheel可明确存新ID，<=0.62可能未知ID整Prefs拒/default/下次覆盖，降级留升级前备份。F09仍部分，固定52项22限定实现/21部分/7待做/2平台普通28不变。Windows真实API/媒体阻塞弹窗/热插拔/FS权限/GUI/Narrator/DPI/ARM64仍未执行，managed替身和编译不替代实机验收；未挂载/USB身份/eject/写权限/电池睡眠/清理undo等继续待做。

## Windows0.64 Windows Doctor电源与睡眠

继续用户指定的Windows Doctor适配，开始实际pull Already up to date c2b72a7/Mac1.12.6(34)，无新Mac。122项Mac基线和126个0.1–0.63旧ZIP保持，无Mac源码/测试/构建/安装写入或执行。

默认关闭Doctor新增稳定doctor.power/Inspect Power & Sleep Settings…，原Battery & Sleep分区保留Home-only基本快照并加入专用检查，13canonical Doctor动作+原临时doctor.check，不改变13导航ID/Feature.Doctor/Prefs字段/schema/注册表/defaultoff。无关files不读取；浏览分区无I-O，只明确点击后做固定数量的本机只读调用。workFeature在首await前绑定，Task.Run真实Read与资源清理完成后再查token，取消/feature-off/ownerclose/Quit不弹迟到报告。没有自动采样/定时器/持久化或后台网络。

系统状态使用[GetSystemPowerStatus](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getsystempowerstatus)和[12字节SYSTEM_POWER_STATUS](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-system_power_status)：四byte+两个DWORD，保留AC原code、BatteryFlags/Percentage原byte、BatterySaver原code与两项uint秒。0xff flag/percent未知、0x80无系统电池优先、AC255未知；寿命0xffffffff仅未知/接电，不展示成42亿秒。其他原值不静默清0；BatterySaver仅0Off/1On/其他Unknown+rawcode，不冒充完整Windows电源模式或能耗分析。续航是系统估计，不能算电池磨损/健康/保证续航，当前无battery serial/name/容量设计值采集。

电源计划使用[PowerGetActiveScheme](https://learn.microsoft.com/en-us/windows/win32/api/powersetting/nf-powersetting-powergetactivescheme)一次，RootPowerKey为NULL；out指针复制16字节GUID后finally [LocalFree](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-localfree)，非空失败/取消路径也释放，null-success明确unavailable而非虚构默认GUID，LocalFree失败另保留原错误partial，不重试或假装释放成功。后续API只用复制的Guid值，不再借用已释放指针。各阶段取消先等真实调用/清理，无超时抛弃native工作。

对一个捕获的scheme GUID固定查询DisplayOff/Sleep/Hibernate的AC/DC六值，使用[PowerReadACValueIndex](https://learn.microsoft.com/en-us/windows/win32/api/powrprof/nf-powrprof-powerreadacvalueindex)/[DC](https://learn.microsoft.com/en-us/windows/win32/api/powrprof/nf-powrprof-powerreaddcvalueindex)。三对subgroup/setting GUID依据微软公开定义，只有闭集这些字段，不读计划名称或枚举任意registry。powrprof的DWORD返回值本身是错误码，不能读旧LastError；每个AC/DC独立保留成功uint值或原错误，不把不支持填0。五个kernel32/powrprof导入均限定System32、BOOL32/DWORD32/GUID16/native指针，默认64位平台形状不缩为32位，无新依赖、shell、powercfg、WMI、elevation、设置写入或睡眠请求。

具体设置含义分别核对[熄屏](https://learn.microsoft.com/en-us/windows-hardware/customize/power-settings/display-settings-display-idle-timeout)、[睡眠](https://learn.microsoft.com/en-us/windows-hardware/customize/power-settings/sleep-settings-sleep-idle-timeout)、[休眠](https://learn.microsoft.com/en-us/windows-hardware/customize/power-settings/sleep-settings-hibernate-idle-timeout)官方资料：单位是秒，前两者0是在该保存计划里没有idle timeout；**休眠0是adaptive hibernate threshold，不是Never**。计划uint.MaxValue仍是合法保存整数，不作电池寿命的unknown哨兵。标签明确configured/plugged-in/battery，与SystemPowerStatus未知电池秒分开。多次调用非一致原子状态，捕获后活动计划/该计划值可以变化，GroupPolicy/overlay/adaptive/ModernStandby或不支持的sleep state可改变实际生效行为；本工具不判断当前机器一定会睡眠/休眠或发生过什么。

Fluent原生虚拟字段列表，完整原code/flags/GUID/AC-DC计划值/分项error、只CopyReport/Close、滚动/固定footer/工作区DPI。复制明确点击才包括全部采样事实/时间/限制，不自动打开设置、改变计划、睡眠/休眠、保存文件、使用助手或网络。其他医生入口/固定设置页面/保护机制保持。

4435157常规核心通过/native应用与套件0警告错误仅交叉编译，独立最终见TESTING。25新keys/75翻译、1741旧values保持；旧文件/图片/音频/文档/QR/助手协议schema不变。新动作可加入启用Doctor的建议候选，仍手动Return/不自动发送；新MyWheel ID <=0.63可能导致未知ID整Prefs拒/default/下次覆盖，降级保存升级前备份。F09仍部分，固定52项22限定实现/21部分/7待做/2平台普通28不变。真实Windows API/实际策略/ModernStandby/电池质量/资源释放/GUI/焦点/Narrator/DPI/ARM64未运行；电池健康、sleep blockers、supported states、睡眠历史/故障原因与修复继续待做，不冒充完整Mac Doctor。

## Windows0.65 Windows Doctor缓存位置

2026-10-25。实际pull Already up to date c2b72a7/Mac1.12.6(34)，无新Mac。122项Mac文件和128个0.1–0.64旧ZIP保持，无Mac源码/测试/构建/安装写入或执行。

默认关闭Doctor新增稳定doctor.caches / Inspect User Cache Locations…，第13个尚planned的Caches分区加入限定只读入口。现有13个Mac同序症状区域均有有限只读入口；14个canonical Doctor动作+原Home-only doctor.check。这不代表13类问题全部解决或Windows移植完成；固定52项22限定实现/21部分/7待做/2平台，普通剩余28，F09仍部分。

只在明确点击后解析两个当前用户位置：[Path.GetTempPath](https://learn.microsoft.com/en-us/dotnet/api/system.io.path.gettemppath?view=net-10.0)报告的Temporary Folder（可能受环境变量重定向，不校验存在/权限），和[Environment.SpecialFolder.InternetCache](https://learn.microsoft.com/en-us/dotnet/api/system.environment.specialfolder?view=net-10.0)旧式WinINET缓存，使用[DoNotVerify](https://learn.microsoft.com/en-us/dotnet/api/system.environment.specialfolderoption?view=net-10.0)，不Create。逐项预期IO/权限/安全/不支持/非法环境路径错误分别显示不可用，不使另一位置消失。不是所有现代浏览器或所有app缓存清单，不判断文件可安全删除。

只去一个结尾反斜杠再走原WindowsPathPolicy（240字符、普通盘符本地路径）；整个盘根、UNC、重复尾分隔、dot/ADS/device/长路径/控制字符都拒绝；另对这两个位置拒绝无效UTF16与Unicode Format字符，含补充平面，不扩大共享路径规则。词法通过仅表示可尝试，不表示可访问或可信。预览完整可尝试路径；被拒绝路径最多512原UTF16单位、保留surrogate边界，控制/格式/行段分隔/非法surrogate可见[U+XXXX]而不是执行排版，普通emoji保留。

Fluent两个RadioButton，无默认选择，Measure初始禁用，只有用户选中可尝试项才继续。浏览Doctor/展开分区不扫描；打开位置选择框不枚举子项。确认后使用原NativeFiles.MetadataDirectory与FolderSizes：root+4层/20K条目/~30秒soft，持有根与祖先，system/reparse/cloud/不完整/拒绝项保留原限制，零输出不冒充空目录。旧WinINET目录若带system属性仍拒绝，不削弱NativeFiles。只读目录项元数据，不开子文件内容，不写文件/属性/权限，不清理或移到回收站。系统提供者可能重定向，native调用可能阻塞，soft时间不是强制超时。只报告逻辑历史字节；hardlink可重复，ADS/物理/可回收空间不包含。临时内容可能正在使用或是工作文件，不能从目录名称推断删除建议。

复用FolderMapDialog，新增可选上下文仅缓存分支启用，报告清楚标记来源位置和上述限制。旧null上下文的文件夹地图输出保留；缓存地图仅明确复制当前视图报告，无自动复制/保存/网络/助手上传。测量后句柄清理完才显示历史结果，不重新扫描。新缓存dialog及地图工作区/DPI适配，真实Windows未验收。

独立agent确认并复现P2：目录打开阻塞期间取消后，随后UserError/IOException/Win32Exception仍会落入真实App外层ShowError而弹迟到错误。缓存Scan在using清理完成后优先取消；同时仅对15个只读Doctor读取await加入AwaitDoctorReadAsync，真实等待原任务与清理，只有已经取消且任务失败才转OperationCanceledException；未取消时保留原异常。成功结果交回调用者，EXE的using先接管资源再tokencheck，不丢弃/泄漏成功返回的lease。Task.Run和STA机制保持；folder/basic增加postawaitcheck。全局写入错误策略不变，不吞写文件/清理失败。feature-off/owner-close/Quit取消不弹迟到结果或错误，非取消真实错误仍显示。任务阻塞时仍等待，绝无超时抛弃native工作。

4435209常规核心通过，native应用与套件0警告错误仅交叉编译，agent最终见TESTING。15新keys/45翻译、1766旧values保持；旧图片/音频/文档/QR/助手协议、Feature.Doctor/Prefs/schema/注册表/默认关闭/依赖不变。新启用Doctor动作可作手动Return候选；新MyWheel ID <=0.64可能使旧Prefs未知ID拒绝/default/覆盖，降级保留升级前备份。实际Windows目录环境/ACL/WinINET系统属性/锁定/阻塞/句柄释放/Clipboard/WPF/焦点/Narrator/DPI/ARM64未执行。缓存清理/RecycleBin安全撤销/卸载/修复继续待做，不把未做写作平台不兼容。

## Windows0.66 Windows Doctor睡眠能力

2026-10-25。每批实际pull Already up to date c2b72a7/Mac1.12.6(34)，无新Mac。122项Mac文件和130个0.1–0.65旧ZIP保持，无Mac源码/测试/构建/安装写入或执行。

Windows Doctor Battery & Sleep分区新增稳定doctor.sleepstates / Inspect Sleep Capabilities…，独立明确点击且Doctor仍默认关闭。原doctor.power读取/模型/报告不变；现15canonical Doctor动作+Home-only doctor.check，13区域均有限只读入口，绝非完整Mac Doctor。无新开关/Prefs字段/schema/注册表/依赖。只读网络、地址或图片/文件选择不参与本检查，不读取调用传来的无关files；浏览/展开不触发任何检查。

一次[GetPwrCapabilities](https://learn.microsoft.com/en-us/windows/win32/api/powerbase/nf-powerbase-getpwrcapabilities)只读调用，System32限定powrprof.dll/ExactSpelling/SetLastError，返回BOOLEAN按U1而非四字节BOOL。失败保留原uint LastError，包括0，只有不可用行、不默认填12个False。成功保留12个原byte，非零（包括2/255）显示报告Yes，0显示No；每项显示原值，不推当前有效行为。HiberFileType额外保留byte0无/1Reduced/2Full/其他Unknown+raw；不因文件存在/类型就判断完整休眠可用、快速启动已开或建议修复。

[微软结构说明](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-system_power_capabilities)含未预处理旧版条件，不能照显示把所有spare同时拼入。已核对[Microsoft Win32 metadata官方winnt.h](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/winnt.h) Windows10+ NTDDI_WINXP/NTDDI_WINTHRESHOLD分支：完整SYSTEM_POWER_CAPABILITIES为76字节，所有BOOLEAN单byte。只使用PowerButton/SleepButton/Lid在0/1/2、S1–S5在3–7、HiberFile8、FastSystemS4在17、WakeAlarm19、AoAc20及HiberFileType22；结构Explicit Size76，偏移72字节处保留未解释的DWORD字段以镜像原4字节对齐；没暴露的reserved/BatteryScale/wake-state字段仍保留完整输出空间，避免native写越界。BatteryScale位32、尾五32bit枚举56–72，x64/ARM64该结构无指针。不是沿用Learn旧NTDDI分支或截短到23字节。仅必要互操作声明/标准事实，未引入第三方运行包。

展示Windows报告的S1/S2/S3睡眠、S4休眠、S5soft-off（不是睡眠）、S0低功耗空闲，以及按钮/盖子存在、混合睡眠和唤醒闹钟支持。不读取/推断温度、电池磨损/序列号、阻塞进程、历史或powercfg /a排除理由；不从多flag自行合成“能睡眠/不能睡眠”的结论。硬件/驱动/固件/策略/配置可以改变当前可用性，采集是历史单次结果，不保证立即进入/唤醒状态，也不与另一电源快照拼成原子状态。

workFeature在首await前绑定，前后tokencheck，使用0.65只读Doctor AwaitDoctorReadAsync，真实Task.Run/native完成才响应取消，不丢后台工作，不弹迟到结果/错误，未取消真实错误仍显示。新native调用没有资源分配/需要释放的pointer。Fluent原生虚拟表、完整时间/raw值/错误/限制，固定CopyReport/Close，工作区/DPI边界；只明确Copy才写剪贴板，无自动复制/持久化/助手上传。无PowerSet、文件写入、睡眠/休眠/重启/关机、shell/powercfg/WMI/elevation或网络请求。

4435778常规核心检查通过，native应用/套件0警告错误仅交叉编译，agent最终见TESTING。27新keys/81翻译、1781旧values保持；原power报告不改，唯一powerarea描述新增，旧动作IDs/设置字段保持。新Doctor建议候选只手动Return；新MyWheel ID <=0.65可能使旧Prefs未知ID拒/default/覆盖，降级保留升级前备份。固定52项22限定实现/21部分/7待做/2平台，普通剩余28不变；F09仍部分，sleep blockers/history/battery health、缓存清理、卸载、可靠撤销和修复继续待做。Windows实际API/固件驱动ModernStandby/false或不一致声明/失败取消/GUI/clipboard/焦点/Narrator/DPI/ARM64执行未完成；托管替身与交叉编译不是实机验收。

## Windows0.67 Windows Doctor本机服务状态

2026-10-25。每批实际pull Already up to date c2b72a7/Mac1.12.6(34)，无新Mac。122项Mac文件和132个0.1–0.66旧ZIP保持，无Mac源码/测试/构建/安装写入或执行。

Windows Doctor Background Items新增稳定doctor.services / Inspect Windows Services…，显式单次只读本机Win32服务状态，Doctor仍默认关闭。原Run/RunOnce读取/模型/窗口，以及power/sleep能力检查不改。现16canonical Doctor动作+Home-only doctor.check，13区域均有限只读入口；并非完整Mac诊断。Feature.Doctor/Prefs字段/schema/注册表/依赖不变，浏览分区无读取；无关files不访问，启用Doctor建议候选仍手动Return。

三System32限定advapi32声明。[OpenSCManagerW](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-openscmanagerw) machine/database均NULL，只请求SC_MANAGER_ENUMERATE_SERVICE(4)，API隐含CONNECT，不用ALL_ACCESS或管理员重试。[EnumServicesStatusExW](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-enumservicesstatusexw) info0、SERVICE_WIN32(0x30)、SERVICE_STATE_ALL(3)、groupNULL、resume初始0，仅一次固定256KiB缓冲调用，不自动续扫/探测/轮询。只有成功或ERROR_MORE_DATA234才解析returned记录；234保留partial/neededbytes，其他失败（LastError0也一样）完全忽略native stale outputs，明确不可用，不填完整空清单。权限缺少SERVICE_QUERY_STATUS时系统会默默省略服务，因此成功或0行也不证明清单完整。驱动服务、配置/命令行/账户/启动类型/二进制路径不查询，native只本机SCM/可能本机IPC，不主动远程连接/网络测试。

[ENUM_SERVICE_STATUS_PROCESSW](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/ns-winsvc-enum_service_status_processw)为两个指针加[SERVICE_STATUS_PROCESS](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/ns-winsvc-service_status_process)九DWORD：x64/ARM64指针0/8，Type16/State20/PID44/Flags48，总56字节（含对齐）。先检查完整reportedcount*stride不超过256KiB；物理不可能的count单独InvalidRecord+原count，合法缓冲却超过本地4096行预算单独RecordLimit+原count，均不越界读/截短冒充完整。随后每个string指针须在完整header数组之后、缓冲内、2字节对齐，并在剩余区和最多1024UTF16+NUL内找到终止符；无无界PtrToStringUni。官方名字通常限256字符，本实现1024是防御读取预算，不称微软最大。空key/无效pointer/越界/未终止记录index跳过可见；空display允许，不捏造同名。OrdinalIgnoreCase重复key显式skip，不能静默合并信任/归属。

总复制name+display不超过262144 UTF16单位，超限逐index提示；模型4096行/4100issues、完整防御复制、稳定排序、闭集issuephase，未知state/type/PID/error均保留uint。只展示Name/Display/rawType/rawState/rawPID，不暴露未解释的接受控制、退出码、checkpoint等字段。State1–7分别Stopped/StartPending/StopPending/Running/ContinuePending/PausePending/Paused，其他Unknown+raw。停止不证明禁用、自启动类型或性能问题，PID可复用，不证明有效进程身份或app/helper归属；不打开进程/路径或推断谁可安全结束。

原生HGlobal固定zero初始化，finally释放；嵌套finally确保CloseServiceHandle即使前段清理失败仍被尝试，[CloseServiceHandle](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-closeservicehandle)失败保留原error为partial，不假装关闭成功/反复关闭。所有pointer仅在缓冲有效期间读并复制到有界托管字符串，报告不持有nativepointer。token在native阶段前后检查，workFeature首await前绑定，使用只读Doctor AwaitDoctorReadAsync真实等待native/cleanup，无超时抛弃；取消/feature-off/owner-close/Quit不弹迟到结果或错误，未取消真实异常仍显示。native调用可能阻塞，不能声称固定超时退出。

Fluent虚拟Services/Issues双tab，原state/type/PID、完整安全可见name与全部issues/capturedtime/limits进入明确CopyReport；名字可能包含私人软件，只有明确copy才写剪贴板，未接入助手发送/持久化。Controls/Cf/行段分隔/无效surrogate以原[U+XXXX]可见，正常emoji/文字保留，不让名称改报告行或方向。固定Copy/Close、工作区/DPI适配。无OpenService/StartService/ControlService/DeleteService/ChangeServiceConfig或服务启停/禁用/卸载、文件扫描、命令/shell/WMI/elevation/设置改变/远程机器参数。权限不足提示不能靠elevate兜底。

4435874常规核心通过，native应用与套件0警告错误仅交叉编译，agent最终见TESTING。34新keys/102翻译、1808旧values保持；新MyWheel ID <=0.66可能使旧Prefs未知ID拒/default/覆盖，降级保留升级前备份。F09仍部分，固定52项22限定实现/21部分/7待做/2平台普通28不变。真实WindowsSCM权限/服务实例/字符串及布局/cleanup/失败取消/GUI/Clipboard/Narrator/焦点/DPI/ARM64未运行；托管替身和编译不是实机验收。StartupApproved/启动文件夹/计划任务、服务配置与启停/安全禁用/清理撤销，以及卸载/深诊断继续待做，不把未做写成Windows不兼容。

## Windows0.68 Windows Doctor常用目录

2026-10-25。实际pull Already up to date c2b72a7/Mac1.12.6(34)，无新Mac。122项Mac文件和134个0.1–0.67旧ZIP保持，无Mac源码/测试/构建/安装写入或执行。

默认关闭Doctor新增稳定doctor.commonfolders / Measure Common Folders…，Storage与Folder Sizes共用，17canonical Doctor动作+Home-only doctor.check/13有限只读区域。先预览Desktop、Downloads、Documents、Pictures、Music、Videos六位置；初始无勾选/Measure禁用，不可用项禁用且原因可见。明确选1–6后复制选择数组，才开始分根读取。Feature/Prefs字段/schema/注册表/依赖不变，无关files不访问，浏览区域不扫描。

五位置使用[Environment.GetFolderPath](https://learn.microsoft.com/en-us/dotnet/api/system.environment.getfolderpath?view=net-10.0)/DoNotVerify、不Create；预期解析错误逐位置保留，其余继续。Downloads没有BCL SpecialFolder枚举，窄[SHGetKnownFolderPath](https://learn.microsoft.com/en-us/windows/win32/api/shlobj_core/nf-shlobj_core-shgetknownfolderpath)使用固定[Downloads GUID](https://learn.microsoft.com/en-us/windows/win32/shell/knownfolderid)374DE290-123F-4565-9164-39C4925E467B、[DONT_VERIFY](https://learn.microsoft.com/en-us/windows/win32/api/shlobj_core/ne-shlobj_core-known_folder_flag)0x4000/current-user tokenNULL，不CREATE/INIT/DEFAULT_PATH，NO_ALIAS对Path无效故不加。不猜UserProfile/Downloads，不fallback到其他目录；重定向可能涉及网络/云，DONT_VERIFY不证明存在/权限，不承诺OS提供者零网络活动，但本工具不发网络测试/远程扫描。

按[Known Folders](https://learn.microsoft.com/en-us/windows/win32/shell/known-folders)要求，路径读取使用已有OnSta真实线程，Downloads先CoInitializeEx(NULL,2)且立即记录initialized=HRESULT>=0，再查token；S_OK/S_FALSE都配对Uninitialize，ChangedMode等失败不查SH/不误Uninit。只有SH HRESULT==0才读pointer，非0保原uintHRESULT/不可用，不读失败pointer；成功nullpointer单独无路径。所有非null pointer成功/失败/取消都finally CoTaskMemFree，嵌套finally确保成功初始化后CoUninitialize仍被尝试。四导入System32/ExactSpelling，GUID16/nativepointer/HRESULT32，不用LastError。依OS保证已分配NUL字符串，只读至多513个UTF16单位；超限显式Truncated且Path强制null，不能把截短前缀作有效路径。此策略限制复制，不证明任意失效nativepointer安全。有效可测路径完整<=240，拒绝路径最多512单位安全显示。

ReportedFolderPath纯抽取原Cache.FromReported词法guard；原Kind检查、字段、错误key、VisiblePath行为保持。只去一个尾反斜杠，原WindowsPathPolicy/strictUTF16/补充Cf拒绝不变，不canonicalize/修复UNC或dot。系统/reparse/cloud/不可访问源仍按原held NativeFiles拒绝。长路径、UNC及合法含ZWJ等Format路径仍受预览保护限制，不说Windows本身不支持，也不放宽guard强行扫描。

所选根串行，每次只持有一个根与祖先，原MetadataDirectory.Open/FolderSizes.Scan：根+4层/20K/~30s soft EACH、最多6根（最多120K元数据条目，不保证180秒硬结束）。workFeature首await前绑定，前后token/只读Doctor helper真实等native与清理；预期UserError/IO/权限/安全/不支持/Win32错误逐根保留、其余继续。取消优先，不把取消作普通失败/迟到弹窗；未取消意外异常仍显示。Win32码取捕获异常，不用过期LastError或原始异常Message。无子文件内容读取、写入、删除/权限更改/清理撤销/助手上传。

去重对经guard去一个尾分隔符后的choice.Path作Ordinal精确、区分大小写比较；重复明确skip，不证明目录身份。大小写不同路径仍各测，避免错过大小写敏感目录。根可嵌套/重叠，hardlink可重复；绝不合计为profile大小/物理占用/可释放量。各根Counted/Partial/失败/skip和限额独立，失败—不是0。Result Recorded仅结果记录时间，不是原子采集。结果UI前所有root lease已释放，留历史快照。

Fluent虚拟根表/详情、明确Copy Summary（仅所选根路径、各自逻辑字节、失败及限制）/View Selected Map/Close，无自动复制或持久保存。子图Owner=结果窗，仅旧快照导航/no rescan，feature-off/ownerclose/Quit关闭owned链，真实WPF未验收。FolderMap尾部optional contextTitle保留旧null/cache标题/报告/控件/导航/DPI行为，新common图单独标题与overlap限制。Map仅Copy Current View，无单独详情复制/助手包装，复用安全可见formatter，未选位置不进结果摘要。

4435981核心通过，native应用/套件0警告错误仅交叉编译，agent最终见TESTING。29新keys/87翻译、1842旧values保持，新MyWheel ID <=0.67可能令旧Prefs拒/default/覆盖，降级保存升级前备份。F06/F09仍部分，固定52项22限定实现/21部分/7待做/2平台普通28不变；重复/相似比较的常用目录批量、完整缓存分类、安全清理、可靠撤销、卸载与深诊断待做。实际Windows COM/KnownFolders/重定向/系统属性/ACL/NativeFiles/ownedWPF/Clipboard/Narrator/DPI/ARM64未运行，托管替身和编译不是实机验收。

## Windows0.69 Windows Doctor跨常用目录查重

2026-10-25。实际pull Already up to date c2b72a7/Mac1.12.6(34)，无新Mac。122项Mac和136个0.1–0.68旧ZIP保持，无Mac源码/测试/构建/安装写入或执行。

Doctor默认关闭；新增稳定doctor.commonduplicates / Compare Common Folders…，Duplicate & Similar Files分区，18canonical+Home-only doctor.check/13有限只读区域。忽略无关files，首await前绑定Doctor。复用0.68六位置读取/路径保护，初始无选择；Compare闭集chooser模式是尾部optional参数，默认Measure标题/按钮/范围/限额不改。Compare先审阅路径，再完整内容读取确认列出所有1–6所选根；取消不扫描。Feature/Prefs/schema/注册表/依赖不变。新MyWheel ID使<=0.68可能拒Prefs/default/后续覆盖，降级须保留升级前备份。

CommonDuplicates纯协调原FolderSizes+一次DuplicateFiles.Scan，不改旧单根DoctorDuplicates、DuplicateFiles、NativeDuplicates、CommonFolderReader、ReportedFolderPath/WindowsPathPolicy/NativeFiles guard。所有所选根共享TOTAL20K metadata条目/~30秒soft，每根深度4；按其余不同路径分配剩余额度/同一全局clock deadline，成功退未用条目、供后根，不重扫之前partial。失败Open不扣条目；扫描前预留quota，扫描在返回count前异常则保守消耗该份额，修复独立agent复现的共享预算P2（原六根SecurityException会23>10）。计数不等于native枚举缓冲区/系统工作量硬上限。阻塞调用可超时，取消等真正结束。未尝试根/失败/局部EntryLimit-TimeLimit/精确重复都留可见行，不把失败当0或无重复。

精确去重只对经旧guard去一个尾分隔符后的choice.Path作Ordinal相等，大小写不同路径可分别扫以保留大小写敏感目录。最多6根串行打开/metadata，各根祖先一直持有到整个内容比较完成；不把只读操作转成可写模式。原同大小→不重叠首尾各64KiB→完整SHA256只跑一次，共享512MiB/file、2GiB requested-read（含samples/full）、60秒soft预算，不逐根乘六。每次最多一个data input，仍由原NativeDuplicate打开子祖先/属性probe→datahandle验证/同根volume/identity-length-mtime前后核对/无FileStream read-ahead；系统/reparse/cloud/offline/不支持identity继续拒绝。

跨根沿用完整[FILE_ID_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_id_info)的64bit volume+128bit file ID，组合在同一电脑上标识文件，不加根目录prefix破坏硬链接/嵌套根去重；不同volume同fileID仍区分。每个输入只在其自己的已持有根volume上读。opaque候选key为单数字kind加分隔符和relative；只经精确只读Sources map与length匹配路由到原Root.OpenDuplicate，不解析标签成路径。长根+叶名不保证完整路径<=240，过长opaque或native完整路径拒绝均保留issue，不崩整批，不静默丢行。叶元数据可被记录并不证明内容可读。

入口验证1–6/不同kind/全部CanAttempt并冻结选择后才I/O；nativeadapter创建异常会Dispose刚打开root。cleanup错误容器在开根前预分配，reverse finally尝试每根即使某个Dispose失败，完成后才抛聚合错误；取消优先，App只读helper真join/不出迟到UI或MessageBox。结果仅纯数据/只读roots与Sources，不持有root/providerdelegate，所有leases结束后才显示。每根记录时间不是原子采集；哈希是历史主数据流，不是当前/同时完整相同或安全删除证明。ADS/metadata/permissions不比较，逻辑重复字节不是物理或可释放空间，根大小绝不合计profile使用量。

原生Fluent三虚拟页：所选根/匹配组与成员/全部记录问题，逐根配额/deadline/Partial/失败/skip与原Win32码保留。路径逐根/逐段安全转义、懒显示，正常来源路径完整，无自动打开/复制/保存/助手上传。明确Copy Report、Copy Selected Group、Copy Selected Path；前两种含根/范围与完整限制。4Mi UTF16累计预算，超限整拒，剪贴板不先写截断内容，可改选小组或单路径。原单目录重复/默认Measure/缓存与地图保持。

4436105核心通过；native应用/套件0warnings/errors仅交叉编译。独立agent修复后最终见TESTING，23新keys/69翻译、1871旧values保持。F04/F06/F09仍部分，固定52项22限定实现/21部分/7待做/2平台，普通28不变；常用目录精确跨根比较已补，相似比较常用批量、保留副本复核清理、可靠回收站撤销/卸载/深诊断仍待做。实际Windows KnownFolders/COM/NativeFS/ACL/不同卷/硬链接/大小写敏感/阻塞取消/GUI/Clipboard/Narrator/DPI/ARM64未运行，托管替身不是实机验收。

## Windows0.70 Windows Doctor启动文件夹元数据

2026-10-25。实际pull Already up to date c2b72a7/Mac1.12.6(34)，无新Mac。122Mac文件/138个0.1–0.69旧ZIP保持，无Mac源码/测试/构建/安装写入或执行。

Doctor默认关闭；新增稳定doctor.startupfolders / Inspect Startup Folder Entries…，Background Items分区，19canonical+Home-only doctor.check/13有限只读区域。旧Run/RunOnce注册表、服务和常用目录/查重流程独立保持。只读取[Environment.SpecialFolder.Startup/CommonStartup](https://learn.microsoft.com/en-us/dotnet/api/system.environment.specialfolder?view=net-10.0)对应当前用户/公共登录Startup位置，GetFolderPath DoNotVerify、不Create/猜UserProfile或ProgramData。预期单位置解析错误保留/另一项继续，原ReportedFolderPath词法/240/UTF16-Cf/本地路径保护不改。系统提供者可解析网络/云重定向，不承诺OS零网络，但本工具不发网络测试/远程扫描；不支持位置按旧NativeFiles拒绝并说明，未放宽guard。

首await前绑定Doctor；OnSta解析两位置/只读helper真join，初始无选择/Read禁用，不可用项禁用；明确选1–2冻结数组后才Task.Run读取metadata。完整路径/Scope/Meaning/限额先展示，不读取子文件内容，所以无内容比较确认。不会读取无关files/后台启动扫描，也不读剪贴板。取消/feature-off/ownerclose/Quit等真正原生/清理完成，不弹迟到结果或错误；意外非取消错误仍显示。

一个共享预算TOTAL512计入条目/~10秒soft，Depth0只列直接子项；分配剩余quota/同一全局clock deadline，成功退未用条目，失败扫描保守消耗预留，Open失败不扣条目；沿用0.69异常扣账经验。Native枚举64KiB缓冲/预取可额外获取metadata，这不是OS工作量硬上限。阻塞调用可更久。精确Ordinal路径重复明确skip，大小写不同仍各试。每次最多一个根及祖先，先释放后下一个/结果UI；无OpenChild/子文件内容句柄。

原FolderSizes及MetadataDirectory保留系统/reparse/offline/cloud/同卷/持有祖先检查。目录Depth limit不展开，系统/链接/无效/不可访问/局部限额与枚举失败保留；失败/未尝试根不显示为0或完整空列表。扫描成功Record放在清理后，Dispose异常即使IOException也单独聚合并终止本批，不被Expected根读取错误吞下后继续开第二根；取消优先。agent设计预审发现的风险已在初稿阶段修正，无确认产品P1/P2。

只按最终大小写不敏感.lnk/.url文件名给Shortcut/Internet-shortcut hint，其它只File metadata，跳过文件Not classified、目录Folder not expanded，理由独立显示。不解析shortcut/URL/target，不开文件/运行命令，不推StartupApproved/实际启用、可信、目标存在/启动慢因，不查询任务/Store启动任务/其他用户。根尺寸不合计profile/物理/可释放；每根Recorded是结果记录时间，非原子采集。不改注册表、启停/禁用/删除、权限、文件或助手上下文。

Fluent虚拟条目表/根状态/选中只读详情、明确Copy Report/Copy Selected Item/Close。所有根错误/配额/deadline/partial/原Win32码/根枚举失败可见，完整正常native路径按根与名称安全转义，合法native名字<=255UTF16；非native畸形长值安全省略预览，不作为I/O目标。目录和跳过项逻辑大小—非0，普通文件保历史主流字节。Copy完整范围/限制/所选根和全部已记子项，或单项+所属根限制；4Mi UTF16累计预算超限整拒，无先写截断剪贴板/自动复制。无打开/定位/解析目标/删除/刷新/保存/助手包装；工作区/DPI Fit用原窗口布局。

4436208核心通过，native应用/套件0警告错误仅交叉编译；独立agent最终见TESTING。29新keys/87译、1894旧values保留（旧背景描述key也保留），Feature/Prefs/schema/依赖不变；新MyWheel ID<=0.69可能拒Prefs/default/后续覆盖，降级保存升级前备份。F09仍部分，52项22限定实现/21部分/7待做/2平台普通28不变。StartupApproved/计划任务/完整启用与目标归属、安全禁用/删除及撤销仍待做，不称平台不兼容。真实Windows KnownFolders/重定向/原生FS/ACL/系统属性/阻塞清理/WPF/Clipboard/Narrator/DPI/ARM64未运行；托管替身不是实机验收。

## Windows 0.74–0.76 连续增量

三批先pull均Already up to date `6cbfdc1` / Mac1.12.7。0.74补批量改名大小写、交换与连锁的同目录两阶段非覆盖移动及回滚；异常退出可能留下临时名，仍不支持硬链接/只读/非NTFS。GIF减色另提供默认关闭的原创Floyd–Steinberg蛇形抖色，原默认输出不变。平面QR支持四种直角方向，严格网格/结构/parity核验不变；兼容M级≤2000UTF8条目可明确批量导出PNG，通过Mac现有Import Images使用，但原名称/分组/场景/日期不迁移、规范等价内容可能合并。Mac目前无预设JSON导入UI，不能称双向预设迁移已完成。

0.75补Clothesline长按预览和独立的单窗口正常关闭请求。关闭仅固定WM_CLOSE、同用户同会话受限窗口、完整路径审阅后明确确认；保存/应用退出/内存释放不保证，HWND核验与发送非原子、1.5秒为软限制、每条不自动重试；默认Cancel，无强杀。0.76补跨明确选择的1–6常用位置的文字/图片相似比较，所有根共享一次元数据和内容预算，保留全部跳过/失败/未尝试信息；不删除、不改动、不加入助手请求。

核心4,508,615项通过，独立检查及原生编译证据见[TESTING](../Windows/TESTING.md)，详细边界见[PORTING](../Windows/PORTING.md)。这些属于现有部分大项补齐，固定52项仍22限定实现、21部分、7待做、2平台差异，普通剩余28。真实Windows/UI/文件系统/x64/ARM64未运行，未与Mac完全对等。


## Windows 0.77：同卷文件移动及会话撤销

本轮第四批开始前再次pull，Already up to date `6cbfdc1`／Mac1.12.7，无Mac源码、测试、构建或安装改动。独立默认关闭的Move Files明确选择1–100文件及目标目录，先完整预检再以Windows原生表格审阅全量路径，默认Cancel；同目录按身份不变，同名冲突拒绝、不自动编号。只支持同卷NTFS普通文件，保留原文件对象的全部数据流（包括Zone.Identifier下载来源标记）和原ACL，不复制或扫描内容、不重新应用目标ACL。跨卷、硬链接、只读、云、链接路径和目录树不支持。

源／两侧祖先持有、相对目录句柄非替换移动；失败或取消尝试反向回滚，部分恢复及元数据读取失败仍记录准确当前路径。所有租约清理、关闭／关功能／Quit真等待，最近结果可只读复核；最多20批LIFO会话撤销，退出丢历史、无崩溃日志。仅核验身份和已记录元数据，无法证明全部内容、ADS或ACL未变；ChangeTime也不能独占归因于本应用的移动。

独立复核修复了大小写输入被通用去重静默丢弃，以及连续移动后ChangeTime导致更早有效Undo失败的两项P2。最终复核无未解决确定P1/P2；常规核心4,509,046通过，复核运行1,102,786项的分项、交叠和实际UI源码托管替身边界见[TESTING](../Windows/TESTING.md#077-验证记录)。完整native套件0警告／错误仅交叉编译；NTFS、ADS／MOTW／ACL、取消回滚、WPF、x64／ARM64均未实机执行。

新的FileMoves启用值或My Wheel内的file.move ID都可能导致旧版整份Prefs拒绝、回默认并在后续保存时覆盖；降级前保留升级前备份。B06由待做转部分，固定52=22／22／6／2、普通剩余28；Copy to、跨卷移动、目录树继续实现，不能将未完成写成平台不兼容。最终0.77.1两架构包的实际构建、校验和SHA256以外置 `build/windows/BUILD-REPORT-0.77.1.json` 为准；0.77.0保留为内部中间产物，不覆盖旧包。
