# Windows 移植执行清单

本清单跟踪用户要求的“能兼容的逐项补齐，每批独立 agent check”。“尚未实现”不能写成“不兼容”；代码实现、编译、Windows 原生运行、人工体验分别记录。Windows 改动限 Windows/ 和相关文档，保留其他会话的 Mac 工作。

当前固定52项的状态、剩余数量和Mac新增差距见 [PROGRESS.md](PROGRESS.md)。

## 0.83 已实现：复制到文件夹

开始 `git fetch`，origin/main 仍为 `6cbfdc1`，无新 Mac 改动。在默认关闭的 FileMoves 下追加 `file.copyto`（与 `file.move` 共用开关和页面），无新 Feature／Prefs 字段／依赖。

`CopyRules`：1–100 个不同（忽略大小写）普通路径、目标目录过 `MoveRules.DirectoryPath`；名称 `stem N.ext`（点开头文件整体作 stem）。`CopySession.Open` 先持有目标祖先，要求 NTFS 与可用空间（主数据流＋每项 4 KiB＋64 MiB），再以 `NativeFiles.OpenRead` 持有每个源（仅共享读）。

`CopyInto` 调用 System32 `CopyFile2`（FAIL_IF_EXISTS|COPY_SYMLINK、pfCancel），80／183 换下一个序号，1235 为取消，非 Win32 HRESULT 经 `GetExceptionForHR`。进度回调在 STREAM_STARTED（流 1）从 `hDestinationFile` 记下卷号与文件索引；`Verify` 以共享 7 重新打开，身份不同则不动并报错；相同才比较 `FileStreamInfo` 的名称与大小，不一致用 `FileDispositionInfoEx`（DELETE|POSIX|IGNORE_READONLY）删除该对象。打开失败或未取得身份返回“未核对”警告，副本仍计入结果。首个错误／取消即停止。

B06 仍部分：跨卷移动、文件夹树、副本撤销待做。复核见 [TESTING](TESTING.md#083-验证记录)。真实 Windows NTFS／ADS／EFS／界面／x64 与 ARM64 未执行。

## 0.82 已实现：普通 ZIP 安全解压

第九批开始先 `git fetch`，origin/main 仍为 Mac 1.12.7／`6cbfdc1`，没有新的 Mac 改动。在默认关闭的 Archives 下追加单文件动作 `archive.extract`（仅 `.zip`），无新 Feature／Prefs 字段／schema／第三方依赖；原 ZIP／TAR／GZip 创建不变。

`ZipExtract.Read` 是原创 ZIP32 读取：结束记录须唯一且注释恰到文件尾，ZIP64 定位器／标记、分卷、加密（含强加密与 AES 额外字段）、版本需要 >2.0、非 Store/Deflate 一律拒绝；中央目录须恰好结束于结束记录。名称只接受 UTF-8 标志或纯 ASCII，旧式代码页及非 UTF-8 时的 0x7075 额外字段拒绝；`/` 分隔，每段过 `WindowsPathPolicy.ValidateName`，拒绝 `\`、开头 `/`、空段、>255 段长和 >32 层。Unix 链接／FIFO／设备、DOS 重解析属性拒绝。重名按 OrdinalIgnoreCase 与 NFC 等价检查，文件与文件夹同名拒绝；大小写不同的同一文件夹合并。规划阶段即读取每个本地头核对名称、方法、标志及（无数据描述符时）CRC／大小，并要求成员范围互不重叠且在目录之前。上限 10,000 项、解压后 2 GB、压缩包 2 GB。

`Extract` 每个成员单独定位，Deflate 使用 BCL `DeflateStream` 读取限定的压缩字节片，输出超过声明大小立即停止（防压缩炸弹），不足、CRC-32 不符或压缩数据损坏都拒绝。

原生 `NativeFiles.ExtractZip`：先检查 NTFS、最终路径 ≤240 字符（按最坏序号预留）、暂存路径 <260 及可用空间（数据 + 每项 4 KiB + 64 MiB）。`CreateDirectoryW` 独占创建新根（已存在则下一个序号），之后每个新建文件夹以不允许删除共享的句柄持有并核对非重解析、同卷；子文件夹意外已存在即停止。每个文件 CREATE_NEW 隐藏暂存、无共享写入，核对后写 `Zone.Identifier`（max(3, 源 ZoneId 0–4)，源 ADS 只读前 4 KiB），设普通属性和 DOS 修改时间，再以 ReplaceIfExists=false 重命名到确切名称；失败时以同一句柄删除暂存。压缩包句柄（仅共享读）与祖先在规划、对话框、解压期间一直持有。

不是事务：首个错误或取消即停止，`ZipOutcome` 保留新文件夹、已写数量和错误，App 显示汇总并弹出错误。原生对话框列出全部路径、数量、大小与 Web 标记说明，固定 Extract／Cancel，沿用工作区／DPI 适配。

B08 部分实现：0.84 加 TAR／.tar.gz／.tgz／单文件 .gz 解压（`Swivel.Core/TarExtract.cs` 把整个归档审查成同一 `ZipPlan`，记录每个文件 CRC-32；`NativeFiles.ExtractArchive` 接受 stem 与写入回调，ZIP 走原路径）。7z／RAR 解压、ZIP64、加密、文件夹树归档及 ADS／ACL 还原仍待做。52 项 22／24／4／2，普通剩余 28。复核记录见 [TESTING](TESTING.md#082-验证记录)。真实 Windows NTFS／ADS／界面／x64 与 ARM64 未执行。

## 0.81 已实现：显式平面二维码数据恢复

第八批开始先pull，仍Already up to date，Mac1.12.7／`6cbfdc1`。复用默认关闭QR功能，新增独立无文件动作`qr.recoverflat`与自身文件选择器；不读取无关已选文件，无新Feature／Prefs字段／schema／第三方依赖。严格`QrFlatRead.Read`／`QrCode.ReadCleanGrid`及旧`qr.readflat`不自动fallback，旧encoder输出不改。

`ReadWithDataRecovery`返回独立结果，含原QrReadResult及改动码字／受影响块计数。像素入口仍需正方形、精确4模块白边、均匀整数近黑白不透明像素，v1–40／L-M-Q-H／四直角方向；全部finder/alignment/timing/format/version/remainder结构必须精确，沿用多方向歧义拒绝。只对反交错后的每块数据与parity码字纠错，不改function/BCH/边框，不做透视／模糊／logo／镜像／任意角度修复。

原创GF(256)多项式0x11d、生成根从α^0开始；完整syndrome、Berlekamp–Massey定位、仅真实缩短块位置的Chien搜索、从S0开始的GF Vandermonde幅值求解。限制degree／实际根数／改动数一致且≤floor(parity/2)，幅值非零；所有syndrome归零且既有encoder parity重新一致后，所有块才交付原有严格文本grammar。S0单独为零不代表无损坏，缺失的缩短位置不能成为可修复根。原输入不变，循环检查取消，无部分文本交付。参考[公开Reed–Solomon原理](https://ntrs.nasa.gov/citations/19900019023)；该资料例子根起点为1，本实现QR根起点为0，独立推导索引，没有复制第三方代码或增加运行时。

改动计数仅为输入与被接受候选的差异（含parity），不是原损坏证明；超出容量可以误纠到另一有效信息，后置parity／语法检查也不证明真实内容或来源。原生Fluent结果窗独立标题、模式、计数、候选标识、完整限制折叠区、固定Copy/Close和workarea/DPI适配。沿用numeric／alphanumeric／ASCII byte／显式ECI26 UTF8；其他字节编码、Kanji、FNC1、structured append和其他条码仍不支持。

源及祖先lease贯穿读取、modal、复制与清理，不输出文件／自动开链接／存库／网络。feature在首await前绑定，取消后等待源任务清理，再映射迟到错误为取消；成功结果先绑定using再查取消。两模式含NUL时禁用Copy且回调guard，保留完整可见转义预览，不改变非NUL原复制行为；依据[CF_UNICODETEXT终止规则](https://learn.microsoft.com/en-us/windows/win32/dataxchg/standard-clipboard-formats)。

统一核心4,518,659通过、原生编译0警告／错误；独立review及修复回归见[TESTING](TESTING.md#081-验证记录)。Windows真实WIC／文件系统／Clipboard／WPF／x64及ARM64未执行。E05仍部分，52项22／23／5／2、普通28；没有用一个子功能虚减大项。新MyWheel ID降级风险保留，旧ZIP与Mac实现／测试／构建／安装不改，最终包以BUILD-REPORT-0.81.0.json记录为准。

## 0.80 已实现：计划任务只读元数据

本轮第七批开始先pull，仍Already up to date，Mac1.12.7／`6cbfdc1`。新增默认关闭Doctor下的`doctor.scheduledtasks`，无文件输入，无新Feature、Prefs字段、schema或依赖；后台分区只增加按钮，浏览不读。新MyWheel ID的旧版降级风险保持明确。

固定Task Scheduler COM CLSID、本机当前token，Connect四个显式null（VT_EMPTY）；GetTasks(0)排除隐藏任务，GetFolders(0)，集合Item从1开始，不使用COM枚举器。可访问列表可能包含其他账户或系统任务，不称为当前用户所有。每条只Name、Task Scheduler命名空间Path、raw State、可空Enabled声明、raw32 LastTaskResult。字段失败显示未知，保留原始状态／HRESULT阶段；不读Definition、XML、Action、Trigger、Principal、ACL和时间，不Run／修改／禁用／删除，不访问远程服务器或提交助手。[本地Connect](https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-itaskservice-connect)、[GetTasks标志](https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-itaskfolder-gettasks)、[任务读取权限](https://learn.microsoft.com/en-us/windows/win32/taskschd/security-contexts-for-running-tasks)。

广度优先，根加4层／128文件夹／1024次task Item尝试（失败扣额）／单字符串1024及总256Ki UTF-16文字预算／256问题加遗漏数／15秒软限。遗漏、权限失败、部分结果及未知原值可见，不把空列表当作完整无任务。文字取得后先扣额，后续验证或去重失败不退，不代表最终字符串总长度。系统返回的COM集合或封送字符串可能先分配；预算约束快照文字及后续调用，不是操作系统分配、RPC或阻塞时间的硬限制，快照也不是原子的。

专用MTA线程，CoInitializeEx／CoUninitialize配对，不改变全进程COM安全配置。固定late-binding成员，无外部包；每个返回的COM acquisition都拥有独立释放责任（包括同一RCW的重复返回和异常字段类型），不使用FinalReleaseComObject。每次调用前后检查取消，先接管成功返回值再检查；全部清理及真实Thread.Join完成后才完成读取Task。没有超时后遗留worker的后台返回。App在首await前绑定Doctor，等待读取后再核对取消再开窗口。[COM封送](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/default-marshalling-for-objects)、[ReleaseComObject](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.marshal.releasecomobject?view=net-10.0)。

原生Fluent虚拟列表／完整选中详情／问题页、固定Copy Report／Copy Selected／Close及workarea/DPI适配。路径开关初始false，并贯穿列表、详情、问题和复制；名称可含私人信息，不能据此声称匿名。Enabled是单独声明，LastTaskResult不是健康结论；无Open／Run／修改或自动复制。F09仍部分，52项22／23／5／2、普通28；实机任务权限／COM／取消阻塞／四语／高对比／Narrator／DPI／x64／ARM64仍待执行。常规核心4,510,958通过、原生编译0警告／错误；独立59,952项、Folder完整性P2修复及最终无未解决P1/P2的记录见[TESTING](TESTING.md#080-验证记录)，包状态以外置BUILD-REPORT-0.80.0.json为准。

## 0.79 已实现：Wheel 手势和非激活拖放入口

本轮第六批先pull仍Already up to date，Mac1.12.7／`6cbfdc1`。新`wheel.gesture`为Wheel下无文件入口；控制窗初始停止，显式Start/Stop、Ctrl/Shift/Alt，复用原开关，无Prefs字段/schema/运行时依赖/模型。保存新动作进My Wheel后旧版可能整份设置读取失败，降级前保留升级前备份。

专用线程安装键盘低级hook并运行消息泵，只保留所选modifier两侧held状态；重复、注入和其他键不触发。所有键继续CallNextHookEx，不吞键/保存文字/读取剪贴板/注入/提权。主鼠标键按SM_SWAPBUTTON选物理左右高位状态。单个待处理请求通过窗口消息交付，带会话、序号、单调时间和物理坐标；UI取出时核对500ms时效、当前主键/映射及session/状态。它不是文件拖动证明，普通按住选区也会触发。[低级hook契约](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc)、[主键映射](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getasynckeystate)。

临时目标使用原生主题系统色、ShowActivated=false及NOACTIVATE，不调用Focus/Activate/SetForegroundWindow/鼠标capture；Drop只接收提供者明确FileDrop+Copy许可，1–100路径快照、240UTF16/无空/NUL/OIC重复整拒。Drop返回后再异步NativeFiles.ValidateInput元数据预检，全批成功且token/代次/Wheel/Busy/退出状态仍允许才填充正常Wheel；后续实际动作重新取得原安全lease，预检不是内容快照。没有自动执行、文件复制/移动或外部数据查询补取。旧普通Wheel路径保留行为。

目标约5秒软TTL；松开鼠标不提前销毁，给OLE Drop正常返回机会。Stop/关闭/关功能/Quit立刻使旧交付失效；若OLE提供方重入，则等外层回调退出才实际关闭目标。等待该目标Completion、hook卸载和真实线程退出，以及迟到创建/文件检查；退出不能把Unhook返回当作全部callback结束。[Unhook契约](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-unhookwindowshookex)、[OLE消息泵](https://learn.microsoft.com/en-us/windows/win32/api/ole2/nf-ole2-registerdragdrop)。普通Wheel已打开后不由Stop关闭。

hook超时可静默被系统移除且无法可靠探测；本次手势默认停止，可手动Stop再Start，传统快捷键/托盘作可用入口。真实Explorer跨进程拖放、窗口激活/主键交换、DPI/高对比/Narrator/权限/远程/ARM64仍未执行。A07仅待做→部分，固定52=22/23/5/2、普通28；直接动作目标/更完整全局拖动识别及焦点体验继续待做，未完成不是平台不兼容。常规核心4,510,756通过，native编译0警告/错误；独立2,406,905项及OLE重入P2修复回归见[TESTING](TESTING.md#079-验证记录)，无未解决确定P1/P2。最终0.79.0包实际状态另见BUILD-REPORT-0.79.0.json。

## 0.78 已实现：新到达图片卡片

本轮第五批开始先pull，GitHub仍Already up to date，Mac1.12.7／`6cbfdc1`。复用默认关闭的Clothesline，新增`clothesline.watch`无文件动作、托盘/设置/挂图入口及独立非模态Fluent窗口。打开窗口不会读取无关选择或启动监听；用户明确选目录并Start，既有其他工具不因监听长期被Busy锁住。

授权范围是启动后新增或改名路径在处理时的当前图片，包括旧文件改名/复制移入/事件后路径替换，不证明事件对象、真正截图或完整到达记录。FileSystemWatcher只FileName/Created/Renamed、本层、不订阅Changed；无目录枚举/初始扫描/递归/错误补扫/自动重启，不改系统截图或剪贴板。根和所有祖先通过既有DirectoryLease贯穿监听/解码/清理，所有读取依旧NativeFiles.OpenRead，保留本地/重解析/云保护。

真实共用ArrivalCollector状态机采用单消费者、准确Ordinal路径每会话只排一次；重复也计符合格式通知上限512。最多64排队、12同时卡，每个路径最多4次（350/500/1000/2000ms间隔）尝试。源lease取得后、每次解码前按完整源长度预扣累计512MiB，失败重试不退款；不是实际读取字节硬上限。共享现有静态图片SDR/16MP/128MiB输入限制；单独STA读取并仅保存最多248×206冻结缩略像素，避免每卡保留完整源。限额/FSW错误停止且提示可能遗漏，无补扫；32条失败详情加遗漏计数。FSW通知不保证写入完成，有限重试可能漏慢写文件。[微软Created说明](https://learn.microsoft.com/en-us/dotnet/api/system.io.filesystemwatcher.created?view=net-10.0)、[Error/缓冲区说明](https://learn.microsoft.com/en-us/dotnet/api/system.io.filesystemwatcher.error?view=net-10.0)。

Stop先取消排队/重试，等待正在读取及所有已进入回调退出，再释放根；晚回调/旧会话不能投递新卡，UI通知合并避免Dispatcher无界积压。Stop后保留卡；再次Start明确清旧卡、重新预算，只听未来通知。关闭窗口、功能关闭、Quit都join，原生阻塞不能承诺即时中止。Preview仅历史缩略图；Hang走旧动作读取当前路径，同路径已挂出的旧缩略图需先手动移除。Hang卡不自动消失；Dismiss/Clear不写磁盘、不重置预算/去重。窗口自身Hang取消按workOwner准确归属，不取消其他窗口工作。

没有新Feature/Prefs字段/schema/依赖/模型；新动作存入My Wheel的旧版降级风险明确。A05仍部分，固定52项22/22/6/2、普通28；系统截图接管/顶部边缘/完整物理仍待做。核心、编译与独立复核最终数字见TESTING；Windows真实FSW/WIC/文件系统/界面/ARM64均尚未执行。Mac源/测试/构建/安装保持独立，旧包不可改写。

## 0.77 已实现：同卷 NTFS 移动与会话撤销

2026-10-25，本轮连续推进的第四批开始前再次 `git pull --ff-only origin main`，返回 Already up to date，Mac 仍为 1.12.7／`6cbfdc1`。新增独立默认关闭的 `Feature.FileMoves`、正式 `file.move` 动作和 Move Files 页面；`file.undoMove` 仅由该页明确操作，不加入无文件 Quick Actions 目录。Mac 源码、测试、构建和安装不属于本批改动。

用户明确选择 1–100 个普通文件及一个目标目录，先完成全量来源和目标预检，再显示全部路径、变更／不变计数与默认 Cancel 的确认窗。每个名称保持不变；同目录按已持有目录身份判断为不变，同名目标冲突整批拒绝，不自动编号、不覆盖。全部输入保留到规则校验，不用大小写折叠去重静默丢弃所选项；大小写敏感目录中的歧义名称仍拒绝，不称为完整支持。路径继续受 240 UTF-16 单元及本地普通路径规则限制。

只开放同卷 NTFS。源文件及来源／目标的祖先目录句柄跨预览和操作持有，核对 64 位卷号、128 位文件身份、目录身份及记录元数据；以已持有目标目录和单个合法文件名调用非替换的相对 `FILE_RENAME_INFO`。只移动原对象，不读取或复制内容、不删除或提权。原对象的所有数据流（包括 `Zone.Identifier` 下载来源信息）及原 ACL 随同保留，不重新应用或保证继承目标目录 ACL。跨卷、目录树、硬链接、只读、云占位、重解析／链接和网络路径仍拒绝。

失败或取消按相反顺序尝试恢复，原位置被占用时不替换。每次原生移动成功后立即记录准确当前位置，再尝试刷新元数据；后续读取或回滚失败也保留真实路径，无法核验的 stamp 明确未知。所有句柄都尝试释放，清理异常合并且不丢已完成结果。关闭、关功能和 Quit 等待当前调用与回滚／清理结束；已取消时不弹迟到执行窗，Move Files 页面仍能只读查看最近结果。结果逐项保留并翻译全部内层诊断，不以 100 项为由截掉额外清理失败。

最多 20 批仅本次进程的 LIFO 撤销，带明确 Forget；撤销再次核对文件、两侧目录与原位置占用。为支持同一文件连续移动后的逐批撤销，只有更早收据的完整 stamp（含 ChangeTime）与可信 before stamp 完全相同，且 before／after 除 ChangeTime 外所有字段完全相同、路径／目录也吻合时，才按本次 after 值刷新 ChangeTime。该时间变化不能独占归因于本操作；共享模式和元数据核验都**不是内容快照，也不是主流、全部 ADS 或 ACL 完整性证明**。不能保证检测所有编辑或成功还原。没有持久事务／崩溃日志，异常退出或断电可能留下新位置，退出后撤销历史丢失。

独立 agent check 发现并修复两项 P2：通用输入去重会静默丢掉显式选择的大小写不同路径；严格 ChangeTime 收据会阻止有效的连续 LIFO 撤销。修复后复核无未解决确定 P1/P2。常规核心 4,509,046 项通过；独立复核运行合计 1,102,786 项（分项与重叠说明见 [TESTING](TESTING.md#077-验证记录)）。完整应用与原生套件交叉编译 0 警告／错误；真实 NTFS、ADS／MOTW、ACL、回滚／取消、WPF、x64 和 ARM64 均未执行。

新 `FileMoves` 启用值或保存到 My Wheel 的 `file.move` ID，都可能使旧版整份 Prefs 读取失败、恢复默认并在随后保存时覆盖原值；降级前保留升级前备份。无第三方应用依赖、模型或新的后台扫描。B06 待做转部分，固定 52 项为 22 限定实现／22 部分／6 待做／2 平台差异，普通剩余 28。**Copy to、跨卷移动、目录树及完整 Windows 验收继续待做**，不能称 Windows 文件操作已全部兼容。

最终 0.77.1 x64／ARM64 预览包的实际构建、校验及 SHA256 状态以外置 `build/windows/BUILD-REPORT-0.77.1.json` 为准。0.77.0 是保留的内部中间产物，不覆盖它或其他旧版本包；未安装、未推送。

## 已完成并交付

- 0.1：托盘、显式文件输入、简化 Wheel、Shelf、Quick Actions、Keep Awake、图片转换/缩小/旋转，305 核心断言；独立检查修复3项P2。
- 0.2：单图裁剪、JPEG质量压缩、图片预设，451核心断言；独立检查完成。
- 两批 x64/ARM64 自包含包已交付；原生 Windows 程序与测试均仅交叉编译，未运行实机验收。

## 0.73已交付

本批前 `git pull --ff-only origin main` 返回Already up to date，Mac仍为1.12.7 / `6cbfdc1`。PC Doctor 固定磁盘检查在同一 `GetDiskFreeSpaceEx` 调用的caller-available与caller-capacity基础上，为每个有效固定卷加系统色用量环。仅当capacity非零且available不超过capacity才展示或评估低空间；无效/失败读数显示Unavailable，不形成错误低空间告警。环图是瞬时的当前用户容量视图，quota可能影响结果，不是物理占用或可回收空间；不扫描、不加采样、保存、复制报告或助手字段。

4,436,288项常规Core检查通过；WPF应用及Windows原生测试项目交叉编译0警告/0错误。独立agent复核的最终结论见[TESTING](TESTING.md)。x64/ARM64为Mac交叉构建预览包，哈希见外置 `build/windows/BUILD-REPORT-0.73.0.json`；Windows原生执行/真实quota/GUI仍待验。本批未修改Mac源文件。

## 0.72已交付

GitHub `origin/main` 在本批开始前 `git pull --ff-only origin main` 返回Already up to date，Mac版本1.12.7 / `6cbfdc1`。本批为 Windows PC Doctor CPU/物理内存监控增加最新样本的系统色环形读数，保留原趋势线、文本数值、两秒单飞采样及停止/等待生命周期；不新增采样、系统权限、偏好或持久化。0–100%作边界绘制，缺失值显示横线；独立无障碍名称使用四语言资源，HelpText与文本快照同步。F07仍部分：磁盘/缓存环图、可靠应用helper归属及安全正常退出仍待做。

4,436,274项常规核心检查通过；WPF应用与原生测试套件0警告/0错误交叉编译，Windows原生执行尚未进行。独立agent最终检查未发现确定P1/P2，检查范围与实机限制见[TESTING](TESTING.md)。x64/ARM64预览ZIP只是在Mac交叉构建；SHA记录见外置 `build/windows/BUILD-REPORT-0.72.0.json`。本批未修改Mac源文件。

## 0.71已交付

本批开始先执行 `git pull --ff-only origin main`，成功快进 `c2b72a7 → 6cbfdc1`（Mac 1.12.7）。新 Mac 改动位于 `Sources/Swivel/Feel.swift`：记录 `FrameDriver` 最近一次显示帧，停滞超过 0.25 秒后下次 `start()` 会替换旧 `CADisplayLink`；同步提交还更新了 `Info.plist`、MCP manifest 与 `AGENTS.md`。这是 Mac 动画恢复，与 Windows 工作无代码依赖；Windows 批次只编辑 `Windows/` 和相关报告文档。

- Windows Doctor Background Items 新增稳定动作 `doctor.startupapproved`。显式点击后只查询固定的当前用户/本机 `StartupApproved\Run`、`Run32`、`StartupFolder` 路径，并分别请求 32/64 位注册表视图；共 12 个固定位置。每处最多 256 个值、值名最多 256 UTF-16 单元、原始数据最多 64 字节、约 10 秒软限。缺失、访问失败、超长值、重复名、部分及未检查位置独立显示；只用 System32 `advapi32` 的 `RegOpenKeyExW` / `RegEnumValueW` 查询。
- 原始类型和字节以十六进制展示，不解释未公开布局，不与 Run/Run32/Startup Folder 项目关联，不推断启用、将运行、信任或启动影响。注册表不同视图可能共享或经链接解析，键身份未核验。值名可能含隐私信息；窗口只在明确点击复制时写剪贴板。没有写注册表、禁用、删除、自动保存或助手上传。
- F09仍为部分实现；普通剩余仍 28 项，52 项总计仍为 22 已实现限定范围、21 部分、7 待做、2 平台差异。StartupApproved 的语义关联、计划任务、启停/禁用与安全撤销仍待后续批次，属于未完成范围，不标为 Windows 不兼容。
- 验证：4,436,270 项 Core 检查通过；Windows WPF 应用与原生测试套件编译 0 warning / 0 error。独立 agent 复核未发现确定 P1/P2；其指出的超长值名状态文案已修正。新 `doctor.startupapproved` My Wheel ID 在旧版可能被拒、重置或保存时丢失；降级前保留升级前备份。Windows 注册表、真实 WPF/剪贴板、ARM64 与界面验收仍未执行。

## 0.3已交付

1. **批量重命名**：NTFS 普通文件、规则预览、不覆盖改名、失败回滚、身份核验撤销、会话恢复记录。agent check 发现并修复改名后选择丢失，复核通过。
2. **图片编辑**：本地14项像素调节与8种外观、原图对比、翻转、灰度、实色遮挡、笔/箭头/形状/文字标注、撤销重做、Grid/Row/Column拼图。agent check 发现并修复Fill格子留白，复核通过。
3. **归档/SRT**：ZIP/TAR/GZip创建；UTF-8 SRT编辑/校验/偏移/速度/纯文本导出。agent check 发现并修复Unicode数字异常与加载时关闭工具的竞态，复核通过。
4. **图片补齐**：按长边/宽/高/百分比缩放、明确选择放大、目标字节JPEG压缩、画布背景/圆角/阴影、常见元数据检查与去元数据另存。独立检查完成，无剩余确定P1/P2；原生效果仍待Windows验证。
5. **My Wheel/快捷键**：独立自定义动作选择/排序，稳定预设及应用ID，3个可配置RegisterHotKey。旧设置迁移和独立agent check通过。
6. **PC Doctor基础**：按钮触发系统/内存/固定盘容量/电源/进程working set/网卡状态，仅本地API，无扫描/网络/修复。独立agent check通过。
7. **应用路由**：最多16个显式本地EXE/扩展名过滤、完整路径确认、无shell参数列表、持有EXE至Process.Start。独立agent check通过；真实应用启动待Windows验收。
8. **阶段交付**：以上汇入0.3 x64/ARM64预览包，已完成交叉构建；包完整性及哈希记录见build/windows/BUILD-REPORT-0.3.0.json。后续仍继续补可实现项目。

每一项的原生Windows检查均只有交叉编译，尚无实机运行结果。上述“agent check通过”只表示代码审查，不能替代Windows验收。

## 0.4新一轮五批

1. 浮动Shelf、只读图片预览与明确复制图像；选择恢复、窗口关闭取消。agent check修复最小化恢复，复核通过。
2. 五种菜单模板、分页和解析弹簧、系统减少动画开关。独立agent check通过。
3. 手动Clothesline：最多12图、会话缩略图、复制/预览/拖出、改名引用同步、清空取消加载。agent check修复按钮拖出事件和空白区命中，复核通过；完整绳索物理/自动截图/屏幕顶边唤出未完成。
4. 图片合成PDF：排序/页型/边距预览、1–50图/总100MP、RGB zlib像素、源句柄至保存、首页旁不覆盖输出。核心结构/解压/取消检查及Poppler独立渲染RGB方向/透明白/半透明灰通过；独立agent check通过。原生输出及UI需Windows验收。自主实现依据[Adobe PDF 1.4规范](https://opensource.adobe.com/dc-acrobat-sdk-docs/pdfstandards/pdfreference1.4.pdf)，没有引入第三方PDF运行时。
5. 照片配方保存14项/外观强度、预览另存配方、先调节后缩放；Featured拼图、编号与Shift约束绘图。旧设置与非法值检查通过，独立agent check通过。

0.1–0.3 ZIP保持原样，0.4新包包含上述功能。构建记录区分核心执行、原生交叉编译和未做的Windows实机验收。

## 0.5新增五批

1. **Retouch**：自有 exemplar 像素算法、ROI/笔画预算、只取未涂抹供体、逐笔撤销、可取消异步预览和PNG独占保存。纹理夹具 MAE 0，未涂抹像素保持；自然照片效果/Windows速度待验。
2. **QR生成与库**：UTF-8 ECI26，版本1–5/L级，最大105字节，4模块静区；图片复制、自选目录不覆盖PNG、32项明确保存的本地库。16个多语言/容量边界样本由独立系统Vision解码与独立RS/格式拆码复核。修复无文件面板仍强制选文件的P2。
3. **Command Prompt**：明确确认后启动系统cmd.exe /D，工作目录作为进程字段，无命令/路径拼接或提权。原生启动待Windows验证。
4. **配色和逐图Auto**：5模板独立4色、自动文字对比、键盘焦点环、高对比覆盖；挂图同步配色。每图重新计算4个Auto值并保留手动配方。修复Auto保存清零旧值、深色默认hover低对比两个P2。
5. **标注再编辑**：对象列表选择、位置/尺寸/颜色/文字编辑、删除、20步Undo/Redo（含Clear）；保留箭头方向和整数遮挡区域。修复Windows CRLF文字保存和零填充编号去重；鼠标拖动缩放手柄尚未实现。

以上每批均完成独立agent check，问题修复后复核；原生检查仍仅交叉编译。0.1–0.4 ZIP保留不变，0.5另行打包。

## 0.6新增四批

1. **WAV音频**：受限整数PCM解析与流式裁剪/音量/淡入淡出，精度保持、不覆盖、原波形与10秒选区试听；高位深Extensible输出。独立样本解析/淡入淡出数学和系统音频读取通过。取消与源句柄生命周期完成agent check；Windows播放/原生清理仍未执行。
2. **标注拖动/缩放**：显式选择模式，真实绘制边界命中、角手柄、Shift比例、整数遮挡、一笔一步Undo，Esc/捕获丢失取消。两路agent发现并修复“无移动小数笔迹清空Redo”“小数等比边缘超界1ulp”；限高滚动保留画布，真实DPI待验。
3. **应用剪贴板模式**：原有直接打开默认不变；可选确认后复制文件引用、无文件参数启动EXE，手动粘贴。启动失败保留剪贴板在确认页明确说明。agent check通过，原生启动待验。
4. **二维码库**：32字可选分组、独立64KB/32项JSON备份，全内容预览、已有相同项跳过、同名异文或超额整批拒绝、不改功能开关。输出只到用户选择目录且不覆盖；Mac库格式互通仍未做。两路agent check通过，新增JSON异常/原子合并回归。

0.1–0.5包保留；0.6继续单独打包。每批代码审查不等于Windows实机验收。

## 0.7本轮两批

- 拉取核对：`git pull --ff-only origin main` 返回 Already up to date，当前与远程同为 `2fc19d1`。保留拉取前未提交文件及补丁；没有回退并行 Mac Doctor 工作。
- **Windows外观/导航**：框架自带 Fluent 控件，系统标题栏；左侧标准 ListBox 功能导航保留页面实例和输入，设置刷新继续恢复文件多选。支持资源由框架处理，真实主题切换/高对比/触屏/Narrator/缩放待实机。
- **图片转换说明/结果**：实际格式与编码器共享解析，提示8位SDR/不保留内嵌元数据和色彩配置、JPEG有损/透明填白；批次按顺序处理、首错停止、已完成保留。显示进度、逐文件成功/失败/取消/未开始记录，最近一批详情仅会话保存。已有单图编辑器的输出预览沿用原流程。
- 上游新 **Change Style** 依赖 Finder 图标资源和 NSWorkspace。Windows 文件夹样式尚未实现；需要单独完成 Explorer 图标/目录属性、原设置备份恢复和不覆盖写入方案，不能直接复用 Finder 路径。不是已判定不兼容。
- 上游文件安全和保真提示已对照：Windows依旧使用输入/父路径句柄与独占发布，不新增复制/移动。压缩视频、重采样及 HDR 仍保留专项待办。

两批分别由独立agent检查；最终审查及验证结果见 TESTING.md。0.1–0.6旧包保持不变，0.7单独发布开发ZIP；Windows原生套件仍未执行。

## 0.8本轮两批

1. **图片预设迁移**：Windows完整配方备份和预览导入、严格64KB/24项schema、新ID、原子合并/重复跳过/冲突拒绝，保留旧自定义动作及功能开关。Mac原始JSON数组可导入兼容的JPEG/PNG/TIFF、移除元数据、Windows范围内名称/尺寸；其余逐行标注，不改写为低配方。Mac本身没有独立导出按钮，取得JSON的来源限制已在README说明。
2. **Mac二维码库迁移**：显式选择256KB/128源项JSON，完整预览后仅导入兼容内容/分组；超长/同名及Windows32项总量限制可见。源句柄跨预览、取消和原子合并，3D季节日期不迁移，M/L外观差异明确，Windows备份仍非双向互通。隐藏字符可视化但不改内容。

两批分别agent check。修复“UTF-16猜测Swift字素长度导致整库拒绝”的P2，并将同类预设路径同步修正；复杂家庭emoji名称/分组仅逐行标注、兼容同行继续。Swift Foundation JSONEncoder实际样本检验可选字段省略、Unicode/CRLF、转义URL和参考日期数值；Mac应用没有启动或修改。原生文件锁/碰撞/取消检查已编译，Windows实机仍待完成。0.1–0.7包保留，0.8独立打包。

## 0.9本轮：WAV背景音乐

- 同采样率整数PCM WAV混音：音乐音量/裁剪后相对开始位置/循环/独立fade；保留主音轨格式与时长，单声道复制/立体声平均。每4096帧流式处理；极短循环缓存≤4096帧，避免逐样本文件读取。10秒PCM16试听共用混音路径与完整时间包络，不提前fadeout。
- Windows原生音乐文件选择和Fluent编辑，两个完整源路径可见，保存/取消/试听固定在底部。双源及祖先lease贯穿预览/导出；等待试听准备结束再释放。输出只在主音频旁独占另存。
- 每份源延续512MiB/2小时、8–192kHz、单/双声道、整数8/16/24/32位边界；不同采样率明确拒绝。压缩/浮点/重采样/自动交叉淡化/语音ducking/降噪/视频配乐继续待做。
- 两路独立agent check发现同一P2：取消发生在加载完成与UI恢复之间时仍可能打开音乐选择器。已在打开选择器前、第二音源返回后补取消检查并复核。
- 核心格式矩阵/数学/边界/取消与独立Python样本核对、系统afinfo读取24位输出通过；新增原生锁、路径、不覆盖和混合导出中途取消检查仅编译、未Windows实机执行。

0.1–0.8旧包保留；0.9独立打包。Mac实现、测试、构建与安装不由本轮修改，其他会话的Mac变更保留。

## 0.10本轮：PC Doctor本地网络／代理和设置入口

- 本机网卡状态／类型、IP家族／网关／DNS数量、DHCP三态及链路本地提示；64网卡／每类16详细地址显示上限，计数保留完整数量。网卡异常可单独降级Unavailable，未知状态不伪装为Off。
- 当前用户／活动连接的Internet代理配置标志；WinHTTP只读配置，不进行WPAD／DNS／HTTP／PAC活动，不将代理地址/凭据/bypass正文带入报告。三指针finally GlobalFree、系统DLL限定System32。
- Fluent原生概览／网络与代理Tab。详细地址默认隐藏，显示与复制共用筛选器；复制包括两页及当前地址选项，仅按钮才写剪贴板，不自动持久化快照。四个固定ms-settings入口仅打开网络／代理／存储／电源，输入不接受文件、报告或模型文本。
- 两路独立agent check未发现确定P1/P2。核心配置/隐私/四语言检查和原生控件/URI/内存布局检查已接入；后者仅交叉编译。真实网卡/代理/Windows设置、540×460/多DPI/Narrator、WinHTTP故障注入和读取中取消仍待Windows执行。
- 不是联网测试、全局有效代理判断或网络修复；VPN／虚拟网卡／企业策略仍需专项解释。其他深层Doctor、缓存/启动项/签名/修复项目继续待办。

0.1–0.9旧包保留，0.10另行打包；本轮不写Mac实现/测试/构建或安装，保留并行会话修改。

## 0.11本轮：整数WAV格式转换

- 原生Fluent格式窗口：明确选采样率/位深/单或双声道，保持默认原格式，预览/停止/保存/取消固定底部，主体可滚动。输入只读头即可显示，不为转换额外扫描全曲波形；源和祖先lease贯穿窗口、试听和导出，关闭后等待试听清理再释放。
- 原创有界窗化sinc重采样，降采样64零交叉/侧，升采样32；94%截止、1024线性插值相位、边缘常值延拓、4096帧双页缓存。512MiB输入/输出、两小时源、最大192k→8k约27MB表，取消可打断表建立/源读取/输出循环。中性格式保留整数样本，单转双复制/双转单平均；改位深无dither、滤波过冲会削波，不宣称恢复细节或专业母带品质。
- 独立agent发现近Nyquist混叠P2：原16零交叉在4040Hz/8080Hz约-20dB抑制；加强滤波后同一独立探针约-80dB，修复复核通过，当前无剩余确定P1/P2。新增目标Nyquist/其上1%实际转换回归，不仅测试远阻带。
- 独立格式转换已补齐；背景混音仍要求同采样率，界面提示先手动转换。不是压缩/浮点解码、变速、降噪、ducking或视频支持。音质/长文件性能和原生播放/UI仍待Windows验收。

0.1–0.10旧包保留；0.11独立打包。本轮未改Mac实现/测试/构建或安装。

## 0.12本轮：显式样本WAV本地降噪

- 开始前 `git pull --ff-only origin main`：Already up to date，本地与远程均为2fc19d1；没有新的Mac提交，保留未提交Mac改动和全部旧Windows包。按用户最新要求，每批开始都先pull并核对Mac改动。
- 原创有界复数FFT/STFT频谱减法、平方根Hann窗、75%重叠、逐频率平滑与归一化overlap/add；FFT大小按采样率512–8192，缓冲不随录音时长增长。噪声只从显式0.05–2秒片段估计，窗口不足时按有效窗能量修正，不读取选区外内容作为噪声样本。不是语音识别或Mac自动选样的等效迁移。
- Fluent原生数值框/强度Slider、噪声选区阴影、三种试听、固定底部保存/停止/取消。全源lease贯穿加载/编辑/试听/保存；等待pending试听结束再处置。处理试听与完整保存共用全源lookahead/历史和源位深量化，最多10秒PCM16；短样本和源起止点不丢帧，0%样本精确保留。另存原件旁，不覆盖，保存前验证完整输出预算。
- 常规核心规则、合成噪声/信号、格式/边界/取消已验证；独立agent完成核心/GUI生命周期与发布审查，并以独立FFT直接DFT和多采样率矩阵探针复核，无确定P1/P2。11025Hz/0.05秒最近帧舍入边界同步修正并复核。原生Windows新检查仅编译。
- 适合稳定噪声；样本内语音/音乐可能损失，高强度可能产生伪影，峰值变化可削波，不自动增益。突发噪声/风声/变化背景、真实人声质量、播放/长文件速度仍待Windows验证。压缩/浮点及视频降噪、自动选样/ducking继续待办。

0.1–0.11旧包保持原样；0.12独立打包，Mac实现/测试/构建/安装不由本轮修改。

## 0.13本轮：PC Doctor只读文件夹空间图

- 开始先pull GitHub：Already up to date，远程与本地2fc19d1；保留并行未提交Mac Doctor代码。已核对本地Mac新方向（空间图/重复/项目产物/卸载）；本批只实现空间图，其他继续明确待办。
- 明确选择本地非系统文件夹后才扫描，根层+向下4层、全局20,000目录项、约30秒软上限；同步系统调用返回后检查取消/时间，不宣称硬实时。只通过固定目录句柄读取目录项，不打开子文件数据，不跟随链接/云占位或跨卷。子目录再次核验属性，祖先保持至子扫描结束。系统、无效、长路径、权限/读取失败、深度受限均保留partial/未知原因；总大小只包含已知常规文件。
- 原创平衡二分面积布局；Windows原生Button图块/ListView/GridView、滚动主体/固定页脚/返回和键盘入口。每层23+Other可继续进入，不构造2万个控件，也不重新扫描；零字节及跳过项目仍经列表可达。逻辑字节不是占用或可释放空间，硬链接/ADS/稀疏压缩和活动文件的边界明确。复制仅当前视图及完整已选路径，默认不持久化/发送。
- 独立agent检查及330946项独立Core探针，发现并修复长名称辅助方法改签名误绑定LINQ索引选择、导致原网络诊断报错的P2；恢复旧单参数方法，新功能使用不同名VisibleBounded，独立复现/常规网络检查复核通过，当前无剩余确定P1/P2。最终常规计数和原生编译见TESTING.md。
- 采用微软 [FILE_FULL_DIR_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_full_dir_info) 与 [GetFileInformationByHandleEx](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getfileinformationbyhandleex) 的目录元数据接口；不采用Finder资源、Shell命令或第三方扫描库。只参考MangoDisk功能思路，按其GPL来源限制未使用代码/规则/素材，Windows README已致谢。
- Windows原生目录枚举、cloud/共享锁/卷行为和真实UI仍只编译、未执行。重复文件、项目构建产物清理、卸载+残留及回收站撤销没有随本批开放，不属于平台不可兼容。

0.1–0.12旧包保留；0.13独立发布开发ZIP。本轮不写Mac实现/测试/构建或安装。

## 0.14本轮：PC Doctor只读重复文件查找

- 开始先执行 `git pull --ff-only origin main`：Already up to date于2fc19d1，没有新远程Mac提交；首次沙盒拒绝FETCH_HEAD写入，按已授权请求重试成功。保留所有未提交Mac Doctor工作、开始前补丁/哈希与0.1–0.13旧ZIP。
- 明确选文件夹并确认读取内容，沿用原根层+4层/20K项/约30秒目录元数据范围；内容阶段另外512MiB/文件、2GiB读取预算、约60秒软限。先同大小、再不重叠首尾各最多64KiB、最后完整SHA-256；空文件/超限/硬链接别名/读取失败/变化都有可见原因。短读累积，提前EOF不成为匹配；抛异常的read保守扣除请求量，界面称“已用读取预算”。仅完整且身份不同的两份以上文件组成匹配组。
- 每次读取先固定并复核所有祖先目录及同卷，属性探针持有到内容句柄打开及身份核对完成；拒绝system/reparse/offline/recall，FileStream禁用预读以遵守预算。使用[FILE_ID_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_id_info)的64位卷号与128位ID，不与旧32位卷号混比；接口缺失即跳过，不能猜测身份。首尾和全量阶段前后均校验身份/长度/mtime，一次只保留一份内容句柄，选择根及祖先贯穿任务；取消/关闭功能/退出等待工作释放。
- 原生Fluent确认窗与分组/跳过原因Tab、有限高度虚拟化ListView/GridView、组内完整路径及SHA-256，底部复制/关闭固定。目录枚举失败及已记录跳过树也可查看。只有用户点复制才送往剪贴板，没有文件写入或清理；路径/名称转义不可见字符，四语言齐全。
- 分时读取的默认数据流哈希匹配，不是同时快照、不是清理推荐或可释放空间承诺；ADS/权限/元数据不比较，读取后可变化。硬链接身份只计一次，存储压缩/稀疏/共享块未计算。重复文件删除、回收站可靠撤销、项目构建产物清理、卸载与残留仍待实现，不标为平台不能兼容。
- 5715项常规核心检查通过；Windows程序与原生检查0警告/0错误交叉编译。独立agent最终无确定P1/P2，13,832项独立随机/故障探针通过。原生目录/文件身份/共享锁/cloud/主题/DPI/ARM64仍未执行，详情见TESTING；MangoDisk仅功能思路，未使用其代码/规则/素材。

0.1–0.13旧包保留；0.14单独打包。本轮未写Mac实现、测试、构建或安装，保留并行工作。

## 0.15本轮：PC Doctor项目构建文件夹线索

- 开始先pull：`git pull --ff-only origin main` 返回Already up to date，本地与origin/main均2fc19d1，无新远程Mac提交；保留并行本地Mac Doctor改动、开始前补丁/哈希及所有0.1–0.14 ZIP。
- 复用原有安全元数据扫描（根层+4层、20K项、约30秒软限），不新增原生文件I/O、不打开子文件内容。纯规则处理已完成快照；候选目录与相邻标记匹配忽略大小写，仅已统计普通文件可作标记，内部必需标记同样校验。选择根本身不利用范围外的父目录标记，未访问区域不补查。
- 一次索引每个目录的直接文件标记；每类证据只保留有序最多8条、总数独立，合并时每个必需证据类别至少保留1条。只列最外层匹配目录，避免嵌套大小重复；另外遍历记录底层跳过原因，不因已匹配而隐藏。证据缺失/不可用、完整多规则冲突、目录枚举失败和扫描限额明确列出；候选或直接父目录Partial则候选也Partial。
- 标准Fluent Tab/ListView/GridView、有限高度虚拟化列表、滚动详情和固定复制/关闭按钮，四语言。显示实际证据路径、来源项目、类别、已知文件数/逻辑字节及Partial/Unknown；只明确复制报告或候选详情，不复制磁盘文件。名称/路径不自动持久化或发送，没有清理或构建命令执行。
- 6097项常规核心检查通过，Windows程序及原生套件0警告/0错误交叉编译。独立agent最终无确定P1/P2，60,607项独立核心探针及147项翻译/占位符检查通过，细项见TESTING；Windows目录/卷/cloud/共享锁、GUI/主题/DPI/Narrator及ARM64仍未执行。

| 目录名（仅已选根内部） | 必需的相邻普通文件名 | 还需目录内普通文件 | 线索类别 |
| --- | --- | --- | --- |
| node_modules | package.json | — | Node.js依赖 |
| target | Cargo.toml | — | Rust构建输出 |
| .build | Package.swift | — | Swift构建输出 |
| build / .gradle | build.gradle / build.gradle.kts / settings.gradle / settings.gradle.kts 任一 | — | Gradle输出/项目缓存 |
| build | CMakeLists.txt | CMakeCache.txt | CMake输出；若又完整满足Gradle则不分类 |
| .venv / venv | pyproject.toml / requirements.txt / setup.py 任一 | pyvenv.cfg | Python虚拟环境 |
| bin / obj | 非空文件名前缀的 .csproj / .fsproj / .vbproj 任一 | — | .NET构建输出 |
| .next | next.config.js / next.config.mjs / next.config.ts 任一 | — | Next.js构建输出 |
| .godot | project.godot | — | Godot项目缓存 |
| .dart_tool | pubspec.yaml | — | Dart工具缓存 |

规则为自行实现的名称线索，不验证项目文件内容、配置或可重建性；目录可能包含用户资料，匹配不代表安全删除。逻辑字节不是物理占用/可释放空间，硬链接可能重复、ADS/文件系统开销不计，扫描不是同时快照。自定义输出位置、无配置文件的Next.js、独立Python环境、Pods及其他未列工具不在当前规则中；不把规则遗漏标为平台不兼容。项目清理/回收站可靠撤销、重复删除、卸载/残留仍待完整设计。

目录习惯参考各工具官方文档：[npm目录](https://docs.npmjs.com/cli/v11/configuring-npm/folders/)、[Cargo输出目录](https://doc.rust-lang.org/cargo/reference/build-cache.html)、[Python venv与pyvenv.cfg](https://docs.python.org/3/library/venv.html)、[MSBuild输出属性](https://learn.microsoft.com/en-us/visualstudio/msbuild/common-msbuild-project-properties?view=visualstudio)、[Gradle目录](https://docs.gradle.org/current/userguide/directory_layout.html)、[Next.js自定义输出](https://nextjs.org/docs/app/api-reference/config/next-config-js/distDir)。这些工具允许改配置，所以只用本地名称不能证明用途。MangoDisk仅功能思路，未读/复制其代码、规则或素材。

0.1–0.14旧包保留，0.15另行打包；本轮没有写Mac源码/测试/构建/安装。

## 0.16本轮：较长二维码与容量反馈

开始先执行`git pull --ff-only origin main`，结果Already up to date，HEAD仍为`2fc19d1`；没有新的远程Mac提交，本地并行Mac Doctor改动保留。

- 原创QR Model 2扩至版本1–10/L、UTF-8 ECI26，最大270字节；补多块Reed–Solomon保护/交错、v7+六个对齐图形和两份版本BCH、v10的16位字节计数。标准容量按[DENSO官方表](https://www.qrcode.com/en/about/versionPage/versionPage1_10.html)核对：v10/L有2192数据位，本编码的ECI/模式/长度头占32位，因此270字节；四模块白边遵循[DENSO留白说明](https://www.qrcode.com/en/howto/code.html)。没有新增第三方代码、运行时或模型。
- Windows原生输入不再按字符数静默截短粘贴；显示UTF-8用量及有效二维码版本/尺寸/L级，超限或非法内容清空旧预览并禁止输出。大于4096个UTF-16单元的输入保留原文，直接拒绝编码并略过完整字节计数。预览4 DIP/模块，复制/PNG仍8 px/模块（最大520×520，包含白边）。标准标题栏、Fluent控件与键盘行为；真实DPI/主题/扫码效果待Windows测试。
- 本地32项库、同名冲突整批拒绝和显式一次导入保留；Mac导入新增106–270字节兼容范围，超限行完整显示而不截短。Windows库JSON预算从64提升至128KiB，支持最大合法32项经转义后的完整备份，schema仍v1。设置schema仍v2、预算提升至160KiB，旧ID/开关保持。旧Windows版本会拒绝超其105字节/64KiB范围的内容；降级前保留旧备份，不能让旧版覆盖新库。
- 仍限制L级、平面码、270字节；Mac的2000字节/M级/季节/3D模板不是完全等效迁移。QR图片读取、其他条码、再长内容和3D仍待做，不是判定平台不兼容。二维码文本只在明确保存时进入本地设置；不读剪贴板、不联网、不自动打开链接，PNG/JSON继续采用选定目录lease及独占发布。

验证记录见TESTING。0.1–0.15旧包保留；0.16独立打包，本轮未修改Mac源码、构建或安装。

## 0.17本轮：已保存网页动作与完整进度清单

开始执行`git pull --ff-only origin main`，返回Already up to date，当前`bcb7bb9`（Mac 1.12.2 (30)）在pull前已位于本地分支。对比上一Windows批次基线`2fc19d1`，核对新增Doctor存储/缓存清理、应用内存汇总和正常退出、图表、重复/相似文件、项目产物与卸载残留；Windows差距逐项列入PROGRESS，未把只读检查计作清理完成。

- App Actions新增原生Fluent网页编辑器与运行确认页：完整地址预览、不静默截短粘贴、有效性反馈、滚动正文与固定操作，英语/简中/繁中/日语。网页和EXE共用16项及稳定`app.*` ID，编辑/移除不改变已有EXE行为；可用于Quick Actions/My Wheel。
- 地址最多2048字符（输入及规范化后均限），HTTPS或仅字面localhost/127.0.0.1/[::1]的HTTP。原始authority先验证再交System.Uri规范化，拒绝凭据、反斜杠、歧义数字IP、无效百分号/UTF-8、控制/格式字符，域名仅ASCII或显式xn--。不做DNS或连通探测；[System.Uri](https://learn.microsoft.com/en-us/dotnet/api/system.uri?view=net-10.0)与[浏览器数字主机规则](https://url.spec.whatwg.org/#ends-in-a-number-checker)用于核对解析差异。
- 仅开网页时无需文件，不检查/转交无关选择。可选复制1–100份明确本地文件引用和扩展名过滤，预览确认后复制CF_HDROP再通过系统默认浏览器打开。URL不追加文件信息；不自动粘贴或上传，网页可能需手动选文件。确认取消/关闭功能不启动；启动前再核对文件与取消。浏览器可能复用进程而不给句柄，不把null句柄误报为失败；启动失败时剪贴板引用保留。
- 网页字段为旧v2设置的可选字段，v1/v2旧EXE配置、ID和开关保留。整体读取预算由160增至384KiB，覆盖16份最大URL与32QR/24配方/80动作共同存在的转义JSON；独立极限夹具304912字符往返通过。旧版不保证读取新网页项/扩大配置，降级前保留旧备份。
- 已保存网页动作的限定范围已补齐；局域网HTTP、原始国际域名、交接卡/图像字节、Codex/Claude草稿链接仍待做，不称平台不兼容。没有新增第三方运行时、模型、自动网络请求或剪贴板读取。

10842项常规核心检查通过；独立10257项探针及84项翻译/占位检查通过，最终agent结论见TESTING。Windows原生检查仅编译，浏览器/剪贴板实际交接和UI验收未执行。0.1–0.16旧包保留，Windows源和文档独立变更，Mac实现/测试/构建/安装未改。

## 0.18本轮：只读大文件与安装包名称线索

开始执行`git pull --ff-only origin main`，返回Already up to date，HEAD仍`bcb7bb9`（Mac 1.12.2 (30)），相较0.17无新Mac提交。已有Doctor新功能差距继续按PROGRESS跟踪，未回退并行工作。

- 新增PC Doctor按钮、Quick Actions/My Wheel稳定动作`doctor.largefiles`，独立Doctor开关保持默认关闭。在文件夹选择器前绑定工作功能/取消，选择后和异步返回再次检查；关闭功能、取消或退出不会出现迟到结果窗口。
- 复用既有`NativeFiles.MetadataDirectory`与`FolderSizes`有界元数据扫描，不增加文件内容句柄、第三方包或系统调用。选定根/祖先在扫描期间固定，子目录同卷/链接/云属性复核；根+4层、2万条、约30秒软限保持。普通文件逻辑字节按降序、相同大小按Ordinal路径排序，不把目录大小当文件。
- 原生Fluent列表与问题页签、选中详情、阈值下拉和安装包名称勾选。默认至少100 MB，支持全部/10/100/500 MB/1 GB，十进制且含边界；筛选已记录快照不重扫、不静默截断结果。安装包/磁盘镜像仅末尾扩展名线索（exe/msi/msix/msixbundle/appx/appxbundle/cab/iso/img），不验证格式、签名或用途，更不是可删除建议。
- 系统/链接/云/非法/不可访问/深度/数量/时间及枚举失败均保留明确问题；筛选无结果仍保留问题和Partial，不能当成不存在大文件。大小不是物理或可释放空间，硬链接可重复，ADS/文件系统开销不计，扫描不是原子快照。
- Copy Report仅明确点击后复制当前筛选和全部问题，Copy Selected File Details只复制所选文件的元数据范围。报告含路径、可见化不可见字符，无自动持久化/网络/打开/执行/删除。常用目录批量入口、缓存规则、身份复核清理及回收站撤销仍待做；F06由待实现变为部分实现，不算清理完成。

核心/独立检查与原生仅编译状态见TESTING。Mac实现/测试/构建/安装保持不变，0.1–0.17旧包不可变；0.18单独构建。

## 0.19本轮：CPU与物理内存实时曲线

开始执行`git pull --ff-only origin main`，Already up to date，HEAD仍`bcb7bb9`（Mac 1.12.2 (30)），相较0.18没有新Mac提交。

- 新增稳定动作`doctor.monitor`，Doctor独立默认关闭。按钮/Quick Actions/My Wheel明确打开原生Fluent监测窗口，正文滚动、固定Pause/Resume/Copy/Close，系统色两张0–100%曲线和当前值，四语言。构造设置页/未显示窗口不会读取。
- 仅三个System32闭集API：GetActiveProcessorGroupCount、GetSystemTimes、GlobalMemoryStatusEx。CPU按两次idle/kernel/user差值计算，kernel包含idle；使用decimal处理unsigned累计和溢出，倒退/无差值/非法idle显示缺失。多processor group明确CPU不可用，避免把调用线程primary group冒充全机；内存独立继续。见[GetSystemTimes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getsystemtimes)。这是时间占比，不承诺与频率加权利用率相同。
- 物理内存使用量=TotalPhys−AvailPhys；拒绝0总量/可用大于总量，缺失不变成0%。可用包括standby/free/zero lists和所有NUMA节点，是瞬时系统值，不是每应用私有用量、泄漏诊断或清理建议。见[GlobalMemoryStatusEx](https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-globalmemorystatusex)和[MEMORYSTATUSEX](https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/ns-sysinfoapi-memorystatusex)。
- 约2秒单飞读取，窗口可见且未最小化才启动；Pause/隐藏/最小化使在途结果失效、停止新读取并留下断点，恢复重新建CPU基线。关闭/功能关闭/Quit后取消并await已开始的读取，不丢弃后台任务或出现迟到点。系统调用不能中途强断。使用Stopwatch单调时间，最多保留相对最后一次刷新120秒及61条；暂停冻结图，恢复刷新才再次裁剪。
- 仅按按钮复制当前显示值、样本经过时间和限制说明；无路径/进程名单、文件扫描、网络、自动持久化或额外偏好。F07推进为部分实现；应用内存分组、正常退出应用、磁盘/缓存/内存环图和清理尚未补齐。原生API/GUI/ARM64未执行，不能当成实机通过。

12285项常规核心检查、54709项独立数值/采样生命周期/interop探针通过；翻译和最终agent结论见TESTING。原生测试仅交叉编译；Mac实现/测试/构建/安装与0.1–0.18包保持独立。

## 0.20本轮：受限文档文字、统计与TXT/RTF副本

2026-10-24开始先pull，Already up to date，仍`bcb7bb9`（Mac1.12.2），相较0.19无新Mac。用户要求连续推进，每批先同步、独立agent check后继续。

- 基本单文件动作`document.text`，闭集txt/text/md/markdown/csv/tsv/log/srt/docx，不新增后台工具、偏好字段或schema。单个明确本地普通文件，NativeFiles.Input/祖先在读取、modal和保存期间保持，异步返回和导出前核对取消，输出走既有独占旁文件发布/自有staging清理。
- 原件8MiB/提取2M UTF16单元，严格UTF8或BOM UTF16LE/BE，拒绝非法Unicode/UTF32及除tab/CR/LF外的控制。不猜无标记编码。words=Unicode空白段、characters=.NET字素簇，另计标量/UTF8字节/行；CRLF一个换行，尾换行有最后空行，空文本0行。不同Unicode版本与Mac字素实现仍需对照；Markdown/SRT不做语法渲染/提取。
- DOCX仅固定正文ZIP项，拒绝重复/大小写别名；1000entries/256Ki名称单元，XML实际解压8MiB/2M XML字符（含标记）/XML深度128。XmlReader禁止DTD、resolver=null；不取外部关系、不解包附件、无Office/浏览器/对象渲染器。仅Word标准/strict namespace正文，t/tab/br/cr与段落次序；对象/drawing/pict/删除/移出块省略。隐藏run/字段显示结果和alternate choices仍可出现，不等同Word可见排版。见[XmlReaderSettings](https://learn.microsoft.com/en-us/dotnet/api/system.xml.xmlreadersettings?view=net-10.0)、[ZipArchiveEntry](https://learn.microsoft.com/en-us/dotnet/api/system.io.compression.ziparchiveentry?view=net-10.0)。
- Fluent只读统计/最多65536UTF16预览、完整内容另存TXT或原创ASCII RTF writer。TXT保留解码文本与换行、无BOM；RTF转义括号/反斜线，Unicode逐UTF16有符号\u并带?fallback，CRLF归一段落。格式/链接/图片/宏/元数据不保留且界面说明；复制仅明确按键，不自动读取剪贴板/发送。窗口在SourceInitialized按owner工作区，DPI变化按自身工作区限高；真实多屏仍待验。

12384常规核心、132681独立探针及系统textutil额外1次RTF Unicode回转通过；84翻译检查通过。未运行Mac应用，未改Mac实现/测试/构建/安装。Windows原生仅编译，GUI/FS/ARM64未运行。E03由待做推进部分；其他文档读写、原格式保真和PDF转换仍待补齐。

## 0.21本轮：网页动作自选本地浏览器

2026-10-24先pull，Already up to date，仍bcb7bb9；相较0.20无新Mac。

- AppRoute可选BrowserExecutable仅网页有效，null/缺失保持系统默认。共用16槽/稳定ID/v2/384KiB、所有原开关保持；最大地址/240字符高转义浏览器路径+QR/预设/动作的完整配置回归通过。旧版忽略新字段回退默认，保存可能丢失选择，降级请保留备份。
- 新选择仅显式本地普通EXE，NativeFiles.Input/祖先在PE检查、原生完整网址+路径确认、可选引用复制与Process.Start期间持有。失败/取消/功能关闭/Quit均Dispose；不自动发现浏览器、不验证发布者或真正浏览器身份。必须用户信任且支持单URL参数。
- 自选EXE UseShellExecute=false、固定SystemDirectory、一个ArgumentList规范化HTTP(S)网址；不加开关或文件数据。默认保持ShellExecute URL交接，null进程句柄可表示既有浏览器接受；自选进程null视失败。引用CF_HDROP手动粘贴、不自动上传规则不变。
- Fluent勾选+标准EXE选择器/完整只读路径，每次完整确认；工作区/DPI限高、固定footer。Store执行别名、链接/脚本/云路径、自定义浏览器参数、LAN HTTP、交接卡/图像字节/草稿仍未实现，不标平台不兼容。12413常规核心、10519独立探针及27翻译检查通过；最终agent复核见TESTING。原生仅编译，实际浏览器/剪贴板/Windows UI/FS/ARM64未执行。B03限定范围扩展，52项总计20已实现/12部分/18待做/2平台，普通剩余30不变。

## 0.22本轮：纯文字DOCX与静态HTML导出

2026-10-24先pull，Already up to date，仍bcb7bb9，相较0.21无新Mac。原生导出下拉框追加两个格式，输出仍由DocumentTextFile的原件/祖先lease与NativeFiles旁文件独占发布保护；无新依赖/设置变化、不运行Office或浏览器。

- DOCX最小固定ZIP三项：`[Content_Types].xml`、`_rels/.rels`、`word/document.xml`。只有包内主文档关系，固定1980时间，无源关系/图片/链接/字段/宏/元数据；原创XmlWriter正文一个w:p/w:r，文本xml:space=preserve，tab/br独立元素。CRLF/CR归LF，读回正文加末段换行，空文亦如此。不等同原格式保真。
- 完整序列化XML先在有界StringWriter中预检，2M UTF16含声明/属性/标签/转义/闭标记；UTF8另限8MiB（2M UTF16已在范围内）。不合法XML字符友好拒绝该格式，不收紧其他格式；标签/转义膨胀或大量tab/换行可超限，UI明确可选TXT/RTF/HTML，不截短。ZIP分块写入/取消由stage清理；核心直接流中取消可留下部分流，应用不发布它。
- HTML固定UTF8无BOM骨架、原生转义&/尖括号/引号，pre/span保留开头LF、固定换行样式；所有输入内容作为文字，无源活动内容/外部资源/链接继承。CRLF/CR归LF，不自动打开文件。TXT/RTF原分支保持。
- 格式依据仅结构：[Microsoft最小WordprocessingML包](https://learn.microsoft.com/en-us/office/open-xml/general/how-to-create-a-package)、[HTML pre规则](https://html.spec.whatwg.org/multipage/grouping-content.html#the-pre-element)。没有引入OpenXML SDK或HTML渲染器。12473常规核心通过，独立检查见TESTING；Windows原生只编译，Office/浏览器/Windows UI/FS/ARM64未运行。E03仍部分，52项20/12/18/2、普通剩余30不变。

## 0.23本轮：QR版本1–40及完整容量迁移

2026-10-24先pull，Already up to date，仍bcb7bb9，相较0.22无新Mac。

- 原创QR算法扩展标准参数到40版本，完整alignment坐标（特别v14+、v32）、RS混合块、版本BCH和余数：v2–6=7、v14–20=3、v21–27=4、v28–34=3，其他0。L/ECI26字节模式，最大2952byte；v40数据2956码字减32bit头，不把无ECI的2953容量误用于UTF8。1–270byte像素输出保持。
- 依据[DENSO版本/容量](https://www.qrcode.com/en/about/version.html)，标准参数事实交叉核对[ZXing的ISO表记录](https://github.com/zxing/zxing/blob/master/core/src/main/java/com/google/zxing/qrcode/decoder/Version.java)；仅参数事实，无第三方代码/算法移植或运行时依赖。保留既有原创RS/interleave/mask实现。
- Native Fluent标准输入/反馈、整2–4 DIP预览<=380、窄工作区可水平滚动、Copy/Save/Close固定footer及工作区/DPI适配。导出8px/module、四模块白边，最大1480像素；>4096UTF16早返不计数/编码，超限保留输入并禁旧输出。高密度真实扫码/打印和Windows性能未验收。
- QrLibrary和Prefs各1MiB，同schema/ID/switches。独立最大32×2952转义内容备份584350bytes，完整混合配置842833字符（1MiB内）；完整往返、恰好上界/+1拒绝。旧版会拒超原容量/预算的数据，降级前保留备份。Mac源仍256KiB/128行/2000byte，271–2000byte现可导入；名字/组/重复/控制字符逐行原因保持，Mac M级/3D/反向迁移仍缺。

15356常规核心、156039独立模块/格式/BCH/余数/RS/UTF8断言、120参数核对、1107旧输出逐像素、1379/1379独立系统Vision解码、9翻译检查通过，最终agent review无未解决确定P1/P2。未运行Mac Swivel；Windows原生只编译，GUI/扫码/打印/剪贴板/FS/ARM64待验。E04仍部分，52项20/12/18/2、普通剩余30不变，44旧ZIP保护。

## 0.24本轮：QR全40版本M纠错与每项级别

2026-10-24先pull，Already up to date，仍bcb7bb9，相较0.23无新Mac。

- 保留单参数Encode/Payload/Native Save的L签名和原输出，新增显式QrCorrection.Low/Medium闭enum；全部40版本M标准data/parity/blocks与格式BCH（M=00，L=01）。ECI26字节模式M最大2330，不用无ECI2331容量。L仍2952。标准参数事实沿用0.23来源，无新依赖/第三方代码移植。
- Fluent L/M ComboBox、动态字节上限/纠错提示；切换清旧ready，超容量保留文字并禁用输出。ReadyCorrection与文字/图像一起快照，PNG/clipboard/库持久化/App异步导出都用同一级别；列表/备份预览显式显示。默认新建L，旧配置缺字段L。
- QrEntry可选Correction，默认L字段省略保持旧L设置字节形状；M写枚举值。备份版本1/闭schema允许可选字符串Correction，L仍三字段旧格式，M写M；缺失/L/M接受，null/错误类型/重复/未知拒绝。合并相同名字文字组但不同级别也冲突，整批不变/不触发Changed。旧版可能忽略设置级别回L/丢失选择或拒绝M备份，降级必须先留备份。
- Mac源维持256KiB/128行/2000bytes，每项转M以对齐Mac QRLibrary.swift实际纠错；不保证编码外观相同。既有导入L条目再导入相同MacM会冲突，用户可先明确编辑既有项为M；不静默迁移。季节/3D/日期、反向迁移仍缺。1MiB预算/32项/ID/开关/NativeFiles输出保护保持。

19508常规核心、294428独立模块/RS断言、3174库/设置/边界、120参数、1379旧L逐像素、1369/1369系统Vision解码、33翻译检查通过；最终agent无未解决确定P1/P2。Windows原生只编译，GUI/剪贴板/扫码打印/FS/ARM64未运行；未运行Mac应用。E04仍部分，52项20/12/18/2普通剩余30、46旧ZIP保护。

## 0.25本轮：受限ODT正文文字

2026-10-24先pull，Already up to date，仍bcb7bb9，相较0.24无新Mac。

- ODT加入现有document.text单文件基本动作，不新增开关/schema/依赖；保持NativeFiles源/祖先lease、统计/预览/明确复制与四格式旁文件独占发布。TextDocument追加Odt默认false标识，原Docx路径和四输出实现不改；兼容页补齐当前实际DOCX/ODT及四格式范围。
- 仅固定规范content.xml，无解包/清单/样式/元数据/资源/关系读取，无Office或脚本运行。1000entries/256Ki名称单元、8MiB原件/实际XML、2M XML字符与提取UTF16、深度128；duplicate/casealias拒绝，DTD禁止/XmlResolver=null，加密/不合法XML/膨胀/超限明确拒绝，绝不截短。
- 要求office:document-content→direct office:body→office:text。读text:p/h，含list/table内，存储顺序各一次；重复行列/列表编号/版面不展开。普通XML空白跨支持span/a/meta/ruby-base折叠/去首尾；显式text:s（正整数、默认1）/tab/line-break最后保留；每段LF，空段也有。超量text:c先验剩余预算，含最终段LF。
- 受限inline白名单；field/hidden-text/hidden-paragraph元素、外来对象/绘图/annotation/note、text:tracked-changes、ruby-text、script/macro和未知inline省略；body的change-start/end间实际文字保留。样式/条件可见性不求值，隐藏文字可能仍出现。畸形或扩展table:tracked-changes亦防御性排除；这不是合法office:text表格常见回归P2，记录为边界加固。空bookmark/change类标记整树忽略。不打开href，只读链接显示字。
- 依据[OASIS OpenDocument1.3 Part3](https://docs.oasis-open.org/office/OpenDocument/v1.3/OpenDocument-v1.3-part3-schema.html)的正文/空白规则，无第三方解析器。UI/统计报告均明确损失，字段/样式/对象不等效Office视图；RTF/DOC/HTML读入与完整文档/PDF保真继续待做。

19559常规核心、21263独立探针（1515 ODF fixture/7576旧文档读取及输出对比）、18翻译检查通过，系统textutil生成ODT准确提取，最终agent无未解决确定P1/P2；原生编译0警告/0错误，见TESTING。Windows实际UI/FS/Office/ARM64未运行，不运行Mac Swivel。E03仍部分，52项20/12/18/2普通剩余30，48旧ZIP/Mac保护。

## 0.26本轮：纯文字ODT导出

2026-10-24先pull，Already up to date，仍bcb7bb9，相较0.25无新Mac。

- 既有文档窗口第五格式ODT，不新增依赖/偏好字段。独立原创writer，不改旧四格式或读取算法；NativeFiles仍持有源/祖先、原件旁独占发布/stage清理，App全会话Busy/取消边界保持。
- ODF1.3最小包精确三项：mimetype首项Stored/无local extra，ASCII MIME无BOM/换行；manifest仅根`/`与content.xml条目，版本1.3/MIME一致，不把mimetype或manifest自身列入；content.xml固定office:document-content/body/text/p。1980时戳，无源样式、脚本、宏、字段、链接/对象、关系或元数据。依据[ODF1.3 Part2包](https://docs.oasis-open.org/office/OpenDocument/v1.3/OpenDocument-v1.3-part2-packages.html)与[Part3正文](https://docs.oasis-open.org/office/OpenDocument/v1.3/OpenDocument-v1.3-part3-schema.html)，无第三方包/解析器。
- 全部ASCII空格用text:s（连续空格计数）、tab与line-break显式写出，Unicode/首尾空白保持；CRLF/CR规范LF，本app单段读回末LF，空文亦LF。外部读取软换行约定可异，系统textutil映射为U+2028/末段LF；仅按明确约定比较文字，不称跨应用字节一致。完整序列化XML先用有界StringWriter预检<=2M UTF16（含markup/escaping）及8MiB UTF8；规范化正文+末段LF也<=2M，尤其1999999显式空格可写/2M不能再加末LF。非法XML或膨胀不截短，可选TXT/RTF/HTML。
- 要求输出CanSeek且Position/Length=0，固定ODF首header位置；NativeStage为新空FileStream，符合要求。非法输入/超限/流状态拒绝前无ZIP字节。写中取消直接Core流可能残留，产品只清自有stage不发布。UI四语言明确双预算/换行/损失/五格式，统计不变。

19614核心通过，独立agent/原生编译见TESTING。Windows实际Office/UI/FS/ARM64未运行，不运行Mac Swivel。E03仍部分，52项20/12/18/2普通剩余30，50旧ZIP/Mac保护。

## 0.27已交付

开始先pull，Already up to date at bcb7bb9，无新Mac。原创RTF 1正文lexer，不调用Office/富文本渲染器；8MiB、2M UTF16、128层组、32字母控制字。Windows1252原字节/hex、Unicode-32768..65535及uc0..16；fallback按词法项计数，bin整块一项，遇真括号终止、uc按组恢复。仅有限正文/显式控制，字体/样式/对象/字段结果/页眉脚/注释省略；显式v/deleted省略，样式隐藏与字体符号不解释，未知body控制可能省略。不支持编码/charset加可见高byte整文拒绝，Unicode替代upr拒绝。段落/行/页LF、cell tab，无额外末LF。未知字体即使未用也保守阻止高byte，不猜codepage。旧RTF writer的大规模Unicode输出可超过此8MiB读取限额，可改TXT。真实TextEdit正数代理兼容，producer可能已NFC正规化，不能恢复原TXT组合序列。沿用Fluent统计/有限预览/五格式副本、源祖先lease和独占旁发布；没有新依赖/设置。独立agent结果见TESTING。52项20/12/18/2、普通剩余30不变，E03仍部分；Windows/Office/UI/FS/ARM64未执行，Mac和52个旧ZIP保留。

## 0.28已交付

开批pull Already up to date at bcb7bb9，无新Mac。原创全版本QR补Q/H，UTF8 ECI26上限1662/1272bytes，L/M保持2952/2330和旧像素。Q/H标准data/check/block参数独立核对，未移植第三方算法/包；format bits L1/M0/Q3/H2，两份BCH。Fluent四级选择/容量/预览/复制PNG/库保持同一readyCorrection，超限保留原文清旧图。enum追加Quartile/High而0/1不变；备份版本1可选Correction精确L/M/Q/H、缺失L/L仍不写，所有级别差异同名整批冲突。Mac导入M保持，1MiB/32项/ID/开关/目录lease独占输出保持。0.24–0.27拒Q/H；0.23及更早可能忽略字段回L，降级备份明确。独立agent结果见TESTING；Windows/GUI/FS/剪贴板/打印/ARM64未执行。52项20/12/18/2普通剩余30，E04仍部分（反向迁移未补），54旧ZIP和Mac保护。

## 0.29已交付

开批pull Already up to date at bcb7bb9，无新Mac。显式doctor.appmemory，Doctor默认off/不自动刷新；K32EnumProcesses、OpenProcess(QUERY_LIMITED_INFORMATION only/noinherit)、GetProcessTimes(createdonly)、QueryFullProcessImageNameW(flags0)、K32GetProcessMemoryInfo，DLL均System32。不使用未定义的未退出ExitTime判活，无VM_READ/文件内容或EXE打开/目录扫描/网络/进程修改。最多4096PID、10s软限、4096UTF16路径、枚举满/重复/字段不可用/时间未查全部保留Partial。每PID一个SafeProcessHandle、每字段后取消，结束前全关闭；workFeature首await前绑定，取消不弹晚窗口，退出沿原work等待。

精确Ordinal报告路径分组，不合并大小写/同名不同路径或猜不同EXE helper；无路径逐PID。不解析别名/硬链/同路径替换/发布者/应用身份，不把组当可靠应用归属。已知working-set小计decimal（4096*ulongMax仍安全），未知计数/全未知Unknown；共享页重复，不是私有/物理总量或退出可释放。Fluent两虚拟表/精确详情、十进制MB、复制bytes；安全显示行无原路径/安全ToString，路径checkbox初始false且同时刷新两个tab。可见转义完整名称/路径，Win反斜线取basename避免Mac宿主Path.GetFileName泄漏。复制整份逐块4MiB UTF8预算整拒，选中带全部范围说明；不自动保存/发送。49UI+1兼容描述新keys/150翻译，核心29864、独立4953探针，最终agent记录见TESTING。WindowsAPI/GUI/FS/剪贴板/ARM64未执行；56旧ZIP及Mac保护。52项20/12/18/2普通剩余30，F07仍部分（可靠helper归属/正常退出/其余图表未补）。

## 0.30已交付

先pull，Already up to date于bcb7bb9，相较0.29无新Mac。显式单WAV的50–200整数百分比播放变速，默认100%、时长/采样帧预览；保留原rate/channels/integerbits，变速会一起改音调，明确不等于Mac保音调的0.25×–4×速度。原有原创有限sinc重采样器提取共享内核，普通格式转换仍使用原精确rate比；速度使用percent/100的有理步进，帧数(sourceFrames*100+percent/2)/percent一次舍入。加速94%截止抗混叠、减速插值、源边缘延拓；100%保留样本，不加dither、峰值可削波。

全量输出先检查含header/padding的512MiB及两小时，超限整拒，预览也先验证完整输出。预览以相同滤波历史/源边界和目标位深量化后转PCM16、最多10输出秒。Windows原生Fluent数字框/实时输出长度、固定操作/可滚动正文/工作区DPI；workFeature先绑定、加载后取消检查、modal finally等预览结束后保存或Dispose，源/祖先lease及独占旁输出不变。无设置/依赖变化、无Mac实现/测试/构建/安装写入，58个旧ZIP保留。30312常规核心检查通过，独立复核与原生编译见TESTING；真实Windows/播放/音质/性能/UI/ARM64仍未执行。保音调、0.25×/4×、压缩/浮点/视频和其他移植继续待做。

## 0.31已交付

先pull仍bcb7bb9/Already up to date，无新Mac。PCDoctor显式doctor.installedapps只读传统桌面安装记录，HKLM/HKCU×请求32/64views、固定Software\Microsoft\Windows\CurrentVersion\Uninstall。每root/child保留同一WOW64flag，root仅ENUMERATE、child仅QUERY；3个System32 Advapi32入口、预定义HKEY从有符号32位常量符号扩展且borrowed不关闭，所有真实root/entry SafeRegistryHandle逐一using释放。逐项256char Enum，每view1,024条最多4,096；每字段固定2,050byte/最大1,024UTF16。仅三个闭集DisplayName/DisplayVersion/Publisher，字符串REG_SZ或REG_EXPAND_SZ原文、不展开变量/读取命令/安装位置，不Create或写注册表。

RegQueryValueEx成功不保证NUL，按返回偶数字节strictUTF16LE、最多剥1个末尾NUL，内嵌NUL/坏Unicode/类型/长值整字段标注，MORE_DATA不读未定义buffer，不拿前缀当完整值；全空白归Empty。缺失/空/不可用/超长/无效/限时分别保留。10s软限/每阶段取消，源调用阻塞可延迟，已枚举记录保留，之后views标未完成/未开始；数量仅已观测，不知剩余。Enum达到END才完整，恰满1024也标可能不全；重复子项OrdIgnoreCase观测并保留跳过值，不再次查；并发增删仍可能未检测漏项。跨view不去重，不能推断唯一app数量或程序架构。

Fluent虚拟记录/视图状态页、原生表/滚动正文/固定CopyReport/CopySelected/Close、工作区DPI边界。选中完整字段、控制/不可见文字可见转义，列表缩短不影响详情/复制。子key默认隐藏、勾选仅详情和复制，safeRow仅格式字段/索引无rawkey、无key fallback；名称/版本/发布者本身可能含隐私信息会显示。全量复制4MiB UTF8逐块预算整拒；无报告持久化/自动刷新。Doctor默认off，首await前绑定/取消后不晚弹窗/Quit等待。无schema/依赖/网络/模型、无Mac代码/测试/构建/安装写，60旧ZIP保留。

依据微软[安装记录字段](https://learn.microsoft.com/en-us/windows/win32/msi/uninstall-registry-key)、[逐项枚举](https://learn.microsoft.com/en-us/windows/win32/api/winreg/nf-winreg-regenumkeyexw)、[返回字符串长度](https://learn.microsoft.com/en-us/windows/win32/api/winreg/nf-winreg-regqueryvalueexw)、[只打开既有key](https://learn.microsoft.com/en-us/windows/win32/api/winreg/nf-winreg-regopenkeyexw)和[独立请求视图](https://learn.microsoft.com/en-us/windows/win32/winprog64/accessing-an-alternate-registry-view)独立实现。注册表链接/实际key身份不核验、记录可过期/任意安装器可写，不能证明实际安装/签名身份/应用归属或安全清理。Store/MSIX/portable/其他用户/ARM32额外view不是完整覆盖；卸载/残留归属/安全撤销仍待做。30443常规核心通过，独立检查/编译见TESTING；真实Windows注册表/API/GUI/ARM64未执行。

## 0.32已交付

开始先pull，Already up to date/bcb7bb9，无新Mac。GIF87a/89a受限静态输入，单ImageDescriptor且frame完整canvas/lefttop0、aspect0或49。原创Core parser/LZW到BGRA，不走任意WIC GIF插件；全局/局部RGBpalette、二值透明RGB归0、四pass交错行，LZW2–8最小码/增长至12bit、code==next与满4096字典延迟clear。依据[GIF89a格式](https://www.w3.org/Graphics/GIF/spec-gif89a.txt)自行实现，格式致谢见README；已核对[Mozilla生产实现](https://raw.githubusercontent.com/mozilla/gecko-dev/master/image/decoders/nsGIFDecoder2.cpp)中的两种循环扩展标识事实，但未复制代码/算法或引入其包。

支持范围刻意有界：动画/多画面、偏移/局部canvas、非方形像素、PlainText、用户交互、ICC及未知App扩展拒绝；不静默丢动画/文字/色彩配置。闭集NETSCAPE2.0/ANIMEXTS1.0单画面循环格式可读，循环/延时不继承；注释跳过/移除，GIF metadata只显示尺寸和file length。palette数值作为8bitSDR sRGB，完整色彩保真/浏览器容错不宣称。结束码必须在完整像素之后，结束码末字节≤7未用bits允许，剩余payloadbyte/额外data block明确Unsupported，无trailer/尾随字节/短图/多像素/非法索引/坏字典均拒绝。

128MiB/16MP/边长16384先检查，扩展count1024/扩展payload64KiB/变长subblocks全流1,048,576，codecount<=2*pixels+4096；固定4096prefix/suffix/stack、255byte data buffer及BGRA，像素/深链/每code/每块可取消。源/祖先lease不变，Core不打开其他文件/设备/链接/网络，Native冻结bitmap后共享所有现有图片工作流及独占旁输出；不新增GIF输出/预设格式/schema/第三方依赖。原生Fluent导航/标准编辑器和四语言错误提示保持。40548常规核心检查通过，Windows项目与原生检查交叉编译0warn/error，独立agent见TESTING；Windows实际WPF/编码器/alpha/DPI/ARM64未验收。C02从待做转部分，52项20/14/16/2、普通剩余30；其他扩展格式/HDR/完整GIF/动画继续待做，Mac及62旧ZIP保持。


## 0.33已交付

开始先pull，Already up to date/bcb7bb9，相较0.32无新Mac。新增audio.floatpcm独立单WAV动作，原创RIFF/IEEE float32/64 reader和量化器；不修改PcmWave整数合同或依赖设备codec。依据[WAVEFORMATEX](https://learn.microsoft.com/en-us/windows/win32/api/mmreg/ns-mmreg-waveformatex)和[WAVEFORMATEXTENSIBLE](https://learn.microsoft.com/en-us/windows/win32/api/mmreg/ns-mmreg-waveformatextensible)字段事实独立实现，float64仅离线处理，不宣称Windows设备直接播放。只接受精确little-endian RIFF/WAVE、tag3的fmt16或18/cb0，或fmt40/cb22/floatGUID/validbits=container32或64、mask0或mono4/stereo3；1/2ch、8–192kHz，blockalign/byterate精确。single非空对齐data、optional fact>=4首值须等于最终framecount且不重复，metadata未知chunk有界skip且不保留；fmt/data可先后，RF64/RIFX/其他format与坏结构明确拒绝。

全源512MiB/两小时/1024chunk，入窗前全量Scan，显示discreteabspeak和严格outside[-1,1]样本值数（非帧/truepeak/响度）。所有NaN/Infinity拒绝，即使在10s以后。默认PCM24，可选16/24/32；超幅必须明确默认unchecked AllowClipping，Core也拒绝未经许可的超幅。finite后先clamp再乘防doubleMax溢出，±1映射整数端点，不算超幅；其他roundAwayFromZero并饱和，无dither/normalize/gain/resample。保留rate/channels/frames，去metadata、损失浮点动态范围/小于整数刻度细节。完整目标含44/68byte头和oddpad先验512MiB/2h，包含float32短头→PCM32扩头导致的超限，preview也不绕过。

每次Export在任何header前再fullScan，既保证Core独立调用也不容预览前缀隐藏异常，增加整文件读取成本；每次4096frame缓冲，原生文件写入拒绝lease使扫描事实在处理时保持。前10秒先targetquantization再PCM16，与Save→既有整数试听byteexact。Native源/祖先跨加载/对话框/preview/save；参数改变/Stop/关闭取消，App finally等待preview完成再Dispose，Quit既有globalCancel/join流程。audio.floatpcm Feature=null，与其他内建媒体动作一样不依Wheel开关；无晚弹窗、无自动播放/联网/模型。Fluent ComboBox/CheckBox/固定footer/scroll/DPI及四语言，独占原件旁输出沿用NativeFiles，无schema/依赖/Mac实现变化。90162核心检查通过，原生项目编译0warn/error；独立复核见TESTING，实际Windows播放/音质/性能/GUI/FS/ARM64仍未执行。D04从待做转部分，52项20/15/15/2，普通仍30；直接浮点/压缩音频/视频/保音调/ducking继续待做。64旧ZIP哈希不变；本批未写Mac，本地并发截图卡显示检测/一次重建、相关测试与1.12.3(31)版本同步（Info.plist/mcpb）已只读核对保留，不属于本批移植；具体时点哈希见外置审计。


## 0.34已交付

开始先pull，Already up to date/bcb7bb9，相较0.33无新远端Mac；开始已有的本地Mac1.12.3(31)截图卡显示检测/重建、测试与版本同步原样保留。本批独立audio.aiff单aif/aiff/aifc动作，原创AIFF/AIFC整数PCM reader→WAV，不调用系统codec或改原有整数/float WAV实现。依据[Apple Mac OS Sound手册原件镜像](https://www.mmsp.ece.mcgill.ca/Documents/AudioFormats/AIFF/Docs/MacOS_Sound-extract.pdf)与[Apple编码标签](https://developer.apple.com/documentation/quicktime-file-format/sound_sample_descriptions)的格式事实独立实现，未复制第三方代码/算法；sowt宽度也核对[原作者AFsp的CopyAudio文档](https://www-mmsp.ece.mcgill.ca/Documents/Software/Packages/AFsp/AFsp/CopyAudio.html)，未引入其程序/库。

接受AIFF及AIFC NONE/sowt（8/16/24/32）、twos16/in24位24/in32位32严格对应，mono/stereo、8–192kHz精确整数Hz。FORM总长精确，COMM AIFF18/AIFC Pascal名精确长度/内部0pad，AIFC唯一FVER4和0xa2805140；AIFF出现FVER明确拒绝。SSND唯一且blocksize0，offset在所属chunk内跳过，剩余data与COMM frames*channels*bytes精确相等；任意块序、未知metadata有界skip、oddchunk外部0pad。80位rate限正normal、显式integerbit1、unbiased exponent12–17，再验低46–51bits全零；不借double舍入接受微小分数。非字节位深、其他标签/23ni/42ni/float/压缩、多声道/分数rate/特殊block明确受限拒绝，未完成不写成平台不兼容。

保存保每个整数样本值、rate/channels/order/frames/bits；8signed→unsigned WAV bias、多字节大小端转换、24bit符号扩展。无gain/normalize/resample/dither，移除metadata/markers/instrumentloops/codec名，不能称完整归档复制。input512MiB/2h/1024chunks，complete WAV44/68header+pad512MiB/2h先验，即使10秒PCM16preview也检查（高位深短AIFF头扩至WAV头）。4096帧流式缓冲；preview与Save→既有PcmWavePreview一致，源/祖先lease跨modal/preview/save、Stop/关闭/Quit取消+join，原件旁exclusive不覆盖。Fluent只读summary/明确Play/Stop/Save/Cancel/scroll/fixedfooter/workareaDPI和四语言，无设置schema/新依赖/Mac实现/测试/构建/安装改动。90983常规核心通过、原生项目编译0warn/error；独立agent见TESTING，Windows播放/听感/GUI/FS/ARM64仍未执行。D04继续partial，52项20/15/15/2普通30，不伪造进度减少；66旧ZIP保持，Mac范围按时点外置审计记录。


## 0.35已交付

开始先pull，Already up to date/bcb7bb9，相较0.34无新远端Mac；本地已有Mac1.12.3(31)截图卡修复/测试/版本同步保留。本批原创plainAIFF writer，独立audio.exportaiff单WAV动作及WavAiffDocument/Fluent窗口，不修改PcmWave/AiffWave/FloatWave/WaveTools/NativeFiles旧合同。根据[Apple原始AIFF格式事实](https://www.mmsp.ece.mcgill.ca/Documents/AudioFormats/AIFF/Docs/MacOS_Sound-extract.pdf)自行实现，未复制第三方codec，输出不依赖AIFF设备插件/外部程序。输入仍为8–192kHz/mono/stereo/8/16/24/32整数PCM WAV，浮点可先显式用0.33转换。

保存保每个整数样本值、rate/channels/order/frames/bits，8unsignedWAV→signedAIFF bias，16/24/32按低位字节bigendian写，不量化/增益/normalize/resample/dither。固定54byte header，FORM46+data+oddpad、COMM18、SSND8+data不计外pad；offset/block0，extended80用integerLog2+规范显式integerbit生成。移除metadata/markers/instrumentloops，不是完整归档复制；只plainAIFF，无AIFC输出。源及完整AIFF各512MiB/2h，完整目标54header/oddpad先验（WAV44→AIFF54可因+10超限），Open/preview/save不绕过，Core独立preview也检查。4096frame缓冲，10sPCM16preview与保存AIFF→PCM16完全相同；不需要扫描波形或整源内存副本。

Native源/祖先贯穿modal/preview/save，Stop/关闭/Quit取消并join后才Dispose/保存，exclusive旁输出.aiff不覆盖；加载后token guard防晚弹窗。Fluent只读source事实、PlayAudioPreview/Stop/SaveAIFF/Cancel/fixedfooter/scroll/workareaDPI、四语言；无schema/依赖/模型/网络/Mac实现或构建安装改动。91351核心通过、应用/原生检查编译0warn/error，独立agent见TESTING；Windows播放/听感/GUI/FS/ARM64仍未执行。52项20/15/15/2与普通30不变，D04仅继续扩展，不伪造工作量百分比。68旧ZIP保持；0.34包内rate遍历数量文字185001勘误为184001，总837023断言不变，旧ZIP不重写，勘误在TESTING及0.35外置审计。

## 0.36已交付

本批开始先git pull --ff-only origin main，Already up to date at bcb7bb9，无新远端Mac。本地已有1.12.3(31)截图卡修复、测试及版本同步保留；仅Windows与相关文档写入，不运行/构建/安装Mac Swivel。新增原创DocumentHtml有界source-markup reader，通过既有document.text动作、NativeFiles源/祖先lease、Fluent统计/预览/固定footer/滚动正文及五格式纯文字副本；旧四类读取/五writer/NativeFiles合同保持，无设置schema/依赖/Office/browser engine。

只接受显式html/htm，严格UTF8（可BOM），输入8MiB，decodedmarkup和extractedtext各2M UTF16；depth128、100000 markup tokens（开始/结束tag、注释和声明，不是正文段）、tag16384 UTF16、128attrs、64ASCII名字。根须html/body，head仅可在body前一次，标签严格配平、非void不能自闭，不修复HTML5省略endtag或错误属性。只允许简单<!doctype html>，未知/自定义/SVG/Math/frameset等拒绝。script元素及所有on前缀属性拒绝；meta限head，charset限UTF8，http-equiv一律拒绝。布尔hidden存在即抑制子树，含html/body及hidden=false；CSS/aria-hidden/动态条件不求值，样式隐藏文字可能留下，不称浏览器可见文本。

head/style/title/textarea/template/noscript/object/iframe/canvas/audio/video/select省略；style/title/textarea/iframe作为raw text只查真实结束边界，其他省略容器仍做结构/属性验证。链接仅正文，无属性值/alt/表单值，无URL跟随、脚本执行、外部资源或渲染。SP/TAB/CR/LF普通折叠、块界合并LF不添finalLF、br显式LF；PRE保留存储的首LF/CRLF（非浏览器换行归一化）。系统WebUtility.HtmlDecode一次，不重新解析解码后的标签，未知实体名保留；提取正文片段中的数字引用必须分号、最多8位、合法非控制scalar（TAB/CR/LF例外），省略文字不声称全体数字引用验证。系统实体集与浏览器HTML5有差异，静态子集不是完整HTML5 DOM/内容模型验证器。

源/祖先handle贯穿读取、modal及独占旁导出；不覆盖原件、不保存网页/样式/链接/metadata。完整限制同时出现在UI及明确复制的统计报告，五种纯文字writer保持原字节合同；HTML副本可因转义/标记超过此reader预算，TXT仍可用。91431常规核心检查通过，应用/原生检查交叉编译0警告/0错误，独立agent结果见TESTING。Windows实际UI/FS/Office/浏览器/ARM64未执行。52项仍20限定实现/15部分/15待做/2平台差异，普通剩余30；E03扩展但仍partial，DOC/完整网页/保真/PDF转换继续待做。70个旧ZIP保持不可改写。

## 0.37已交付

本批开始先pull，Already up to date，实际HEAD与origin/main为b9d6020/Mac1.12.3(31)。已检查相较bcb7bb9的截图卡0.4秒显示自检/一次窗口重建恢复、测试与版本同步新提交；源字节与此前本地并发工作相同并保留，Windows自动截图仍待做。本批对齐Mac TrimEditor.merge的音频顺序接合意图，独立audio.join动作补2–32整数PCM WAV；不是Mac M4A/视频通用合并。Catalog只在2–32全部WAV显示，App先验证/绑定再await、加载后token防晚弹窗；没有自动读取目录/网络/剪贴板/optional开关/schema/新依赖变化。

原创WaveJoin只复用既有PcmWave结构验证和header/sample helpers，不改旧音频算法。所有源rate/channels/bits须一致（普通与Extensible PCM语义格式可混合），mono/stereo、8/16/24/32bit、8–192kHz。每源沿用512MiB/2h/1024chunk，聚合完整Length包括metadata/headers/pad限512MiB；sumFrames用整数检查≤rate×7200，完整输出44/68header+data+finalpad也≤512MiB。任意source（含最后一个）、完整预算均在outputheader前校验，即使只试听；count/cancel先于集合clone，clone后再验数量。拒绝同一Stream对象重复及任何输出alias来源；App沿用OrdinalIgnoreCase路径去重，不承诺重复同一路径片段，Native不同hardlink路径作为列表项重复计入/播放，不做身份去重。

保存只顺序复制每个data区域，不含各源RIFF头/metadata/中间oddpad，最终仅合计data奇数时补一个0。保每个样本值/格式/声道次序/帧数，无gap/resample/gain/crossfade/silence检测，突变接点可click；不保markers/loops，非容器归档副本。4096帧缓冲，前10秒是整个顺序的总前缀，跨接点正常；PCM16量化用与旧PcmWave相同roundAway/饱和，等同保存再preview。所有源/祖先lease跨加载/modal/preview/save；第N源失败释放已有全部，关闭/Quit先取消并join预览再dispose。顺序完整排列快照用于试听和保存，重排先Stop；准备试听时禁保存和重排，第一条完整来源/输出位置同步变化，独占新WAV在第一源旁。Fluent系统ListBox/UpDown/固定footer/scroll/workareaDPI/四语言，无自动播放。

91766常规核心通过，应用/原生检查交叉编译0警告/0错误；独立agent结果见TESTING。Windows真实播放/接点听感/性能/FS/GUI/Narrator/ARM64仍未执行。52项仍20限定实现/15部分/15待做/2平台差异、普通剩余30；D04继续部分，压缩/浮点/视频接合及保音调媒体编辑待做。72个0.1–0.36旧ZIP不改；Windows本批未写Mac实现/测试/构建/安装。

## 0.38已交付

先git pull --ff-only origin main，Already up to date，当时origin/main仍b9d6020/Mac1.12.3。随后本地并发Mac会话提交7bf2fff/Mac1.12.4(32)：草稿不授予宽目录、TAR的@名字/解压外链、Clothesline仅有界正规图片字节、相似文件保留副本lstat、卸载共享identifier不预选、MCP最多16并发及文件夹菜单归位。已只读核对，Windows尚未实现的相应能力仍列待做；无为同步而stash/reset。0.38实际baseline HEAD为7bf2fff，其后63685ed中性fixture及de1f7f8/ba54ebc归档链接逐级/顶层约束、单描述符图片剪贴板读取和MCP旧动作ID兼容等并发修复已只读核对并保留；最终文件范围以外置审计为准，不误称Mac122文件全未变。本批只写Windows及相关文档，不运行/构建/安装Mac Swivel，74旧ZIP不可改写。

原创GifExport从已有归一化SDR BGRA导出单画面GIF89a，通过新稳定convert.gif动作接入ImageTools/共享ImageOutputInfo/Fluent转换确认及首错停止的逐文件结果。只支持<=256paletteentries（透明占1），全opaque或全transparent alpha0/255；透明隐藏RGB规范为0，透明黑/opaque黑分别索引。>256颜色或partialalpha在首个header之前整图拒绝，不做自动quantize/dither/铺底，PNG保留更多色/半透明。保的是规范化后8bit RGB，不是源ICC/HDR/metadata/容器；新GIF无animation/ICC/metadata/loop/delay。旧四writer/GifImage输入/四格式配方不改，无schema/新依赖。

全局palette首次出现稳定排序，补齐power2(min2)，minLZWcode=max2,palettebits；GCE只为透明索引，fullcanvas/无interlace/squareaspect0，单trailer无额外payload。原创有限LZW4096词典，独立模拟decoder的next/previous来决定码宽3–12，避免encoder词典领先一步的边界错误；每255byte子块写出、最后bits补零及块终止。输入沿用16MP/单边16384，预检生成1byte/pixel稳定indices+最多256palette，不在编码阶段重读可变BGRA；扫描/编码/块输出均可取消。数据长度最坏有界且低于既有128MiB输入reader预算；不为此调用系统GIFencoder或外部程序。

ImageTools持有原件/祖先lease至处理和独占发布结束；失败/取消只清自有stage，旁文件不覆盖。ConversionDialog用原生Fluent系统控件/滚动正文/固定操作说明5项新增英文keys及三译，旧格式说明不变；JPEG/BMP原铺白语义保留，GIF不会走铺白。92921常规核心/3289独立断言通过，828产物Pillow与ImageIO均精确解码，15翻译，最终复核见TESTING。Windows WPF显示/编码文件系统/DPI/Narrator/真实透明度/ARM64未执行，只有交叉编译与跨平台core/独立解码验证，详见TESTING。C02继续partial，52项20限定实现/15部分/15待做/2平台差异，普通仍30，完整动画/量化扩展/HEIC/WebP/AVIF/HDR待做。

## 0.39已交付

期间Mac另有keeper身份/Trash未确认回执/窄工作区、归档展开预算/MCP撤销身份/旧日志提示及测试并发修复，均只读核对并保留；最终时点文件差异见外置审计，不声称Mac全部未变。

开始先pull，Already up to date，origin/main仍b9d6020/Mac1.12.3；本地HEAD38689a7为0.38已经只读核对保留的Mac1.12.4安全修复系列，无新增远端Mac，本批不写Mac实现/测试/构建/安装。ImageRecipe.Formats末尾追加gif，保留旧四项索引/默认值，ImageTools已有preset→处理最终BGRA→GifExport分支直接复用，不修改四encoder/GifExport/GifImage/NativeFiles或Mac导入闭集。

Image Presets仍默认关闭、24项/稳定ID/快捷菜单及v1/v2迁移不变。GIF包含透明项最多256、alpha0/255，不quantize/dither/铺底/格式fallback；必须在Photo/Auto/缩放后的最终图验证，原件可超256色但缩小到16像素后成功，源本身合法也可能因处理新增色/partialalpha而拒绝。保存配方不预读图片。JPEGquality仅JPEG作用/可编辑，GIF仍保存合法1–100存值用于完整schema/相等性，描述不显示无意义百分比；仅Quality不同也仍按整个recipe冲突，不悄悄归一。预设编辑三说明（GIF本身/后处理/降级）、Windows库导入导出预览相同说明，运行preset GIF再明确后处理边界；原生固定footer/scroll/workareaDPI/四语言。

PresetLibrary64KiB字节、Prefs1Mi UTF16单元分别保持，未混称相同预算。新GIF配方/调整/Auto等完整Windows备份回转，新本地ID/同名同值skip/同名异值整批拒绝保原ID和开关。旧Windows0.38及更早可能因未知gif导致整份设置加载失败回默认、后续保存覆盖原数据；保留升级前设置/预设备份，含gif新备份也非降级备份。Mac合法格式仍jpeg/png/heic/tiff，伪gif源整拒，真正Mac兼容行保持；不新增MacGIF迁移。

92947常规核心通过，应用/原生检查交叉编译0警告/0错误；独立agent最终记录见TESTING。WindowsWPF/GUI/真实插值与图片色彩/FS/剪贴板/DPI/Narrator/ARM64未实机执行。52项20限定实现/15部分/15待做/2平台差异、普通剩余30；C07继续partial，HEIC/保metadata/完整反向迁移未做。76旧ZIP保持，Mac差异按时点审计保留，不回退并发工作。

## 尚未完成，不能写成平台不兼容

- 应用路由的交接卡/图像字节、文件复制到／跨卷移动／目录树、完整绳索手感、Mac配方的元数据保留/HEIC/超限等效支持、Explorer文件夹样式。0.77仅补同卷NTFS普通文件移动与会话撤销；Codex/Claude受限文件引用草稿已于0.42实现，不自动授予工作区，真实协议处理仍待Windows验收。
- Retouch已移植，仍需真实照片质量与Windows性能验收；它不是Core ML受限功能。
- 归档解压：路径/链接/ADS/解压预算/磁盘剩余/Zone.Identifier传播需成套完成。
- QR照片透视/旋转/非精确边框与缩放噪声/损坏修复/其他字节编码及条码（0.61已有清晰正向平面QR受限读取）/完整Mac 3D造型和动画（0.51已补原创Windows浮雕）/Windows库反向迁移到Mac、已有PDF保真页面编辑（0.50已有需身份的栅格页面副本，普通ZIP不可用）、直接浮点编辑（0.33已可先转整数PCM）、压缩音视频编辑、压缩音频/视频降噪、语音分类驱动ducking（0.45已有RMS音量活动近似）/压缩配乐（0.58已有整数WAV明确重采样准备）/视频配乐、字幕烧录。
- PC Doctor重复文件删除/回收站撤销、项目产物清理/自定义目录识别、卸载与深度诊断修复（0.60已有有限只读EXE头部，不是签名信任/加载器诊断）、更完整Doctor诊断（0.53–0.56已补当前报告解释与逐次所选名称）、MCP完整plan/apply/undo。

## 0.40已交付

PC Doctor默认关闭，新增稳定`doctor.textsimilar`动作及原生按钮，显式选目录/确认后才扫描；加载前绑定workFeature，feature-off/取消/Quit保持现有单次Busy及任务等待。复用未改的FolderSizes/MetadataDirectory.OpenDuplicate：根和祖先全程held、同卷/本地正规路径/reparse/cloud检查，单文件数据句柄阻止写入和替换，完整identity(64bit volume+128bit fileID)/length/mtime与Stream.Length前后核验，直到解码和完整行集合后最后复核再接纳。后来遇到同身份不同stamp使先前缓存也失效；硬链接只读一次。无新依赖/schema/开关默认值/注册表写入。

原创有界文本相似：仅TXT/TEXT/MD/MARKDOWN/CSV/TSV/LOG/SRT，strictUTF8或BOM UTF16；每文件1MiB、最多200次打开尝试（失败也计）、读取请求先预留/短读退款/抛错保留，累计16MiB；每文件20K原始CRLF/CR/LF行（含空/重复及末尾空行），最多保留各文件不同行数之和200K（跨文件共有行重复计），60秒软限覆盖读取/解析/逐行/配对（blocked native可能晚回）。Unicode Trim、忽略空行/重复/行顺序，Ordinal行文本不折叠大小写/Unicode形式；同扩展名OrdinalIgnoreCase，md与markdown仍不同。每文件>=5不同非空行，共有/并集整数判断>=80%；分数截断到小数2位，接近100不四舍五入为100。每对独立，不能传递分组；即使100%不证明字节相同。

最多显示按Ordinal路径对顺序前1000，不是最佳1000；完整匹配计数仅含已完成比较。Fluent虚拟ListView/问题tab、只读所选安全显示的完整路径及行数统计、固定显式复制/关闭、工作区/DPI；零结果仍有Partial及全部元数据/内容issues。结果只含路径/common/union/score与原因，不含原文；明确Copy才触及剪贴板，不保存/联网/打开/删除。93,185常规核心通过，原生应用/测试只交叉编译；独立agent最终结果见TESTING。Windows真实FS/锁/identity/GUI/DPI/Narrator/ARM64仍未执行。52项仍20限定实现/15部分/15待做/2平台差异，F04部分；图片相似/常用目录批量入口/删除/回收站可靠撤销仍待做，不能称平台不兼容。先pull Alreadyuptodate originb9d6020/local76fbccb，无新远端Mac，78旧包保留；本批未写Mac实现/测试/构建/安装，Mac文件差异按外置时点审计。

## 0.41已交付

默认关闭Doctor下独立稳定`doctor.photosimilar`，选目录/完整确认后再扫描，首次await前绑定workFeature，整个扫描在可等待的worker STA，关闭功能/Quit取消并join后不出late窗口。metadata沿用root+4层/20K/约30秒。图片阶段200打开尝试含失败、16MiB/源、累计128MiB按请求预留/短读退款/抛错保留，约60秒包含解码/指纹/比较，blocked codec/native可晚回。源先完整有界读为byte快照，WPF仅MemoryStream seek，root/单源/祖先贯穿解码和最终identity/length/mtime/Stream.Length复核；同卷正规路径/reparse/cloud/64bit-volume+128bit-ID检查均沿用。硬链接一次，后来同identity变stamp使已缓存签名失效；只留签名/路径，不返回像素。原件/安全层/精确重复/相似文本/设置schema及默认值不改。

原创整数指纹：现有规范化单帧SDR BGRA，alpha铺白`(c*a+255*(255-a)+127)/255`，亮度`(77R+150G+29B+128)>>8`；9×8格边界floor(g×size/grid)，每像素恰属一格，long累加、格均值`(sum+count/2)/count`，横向left>right为1（相等0），bit=y×8+x。>=9×8、格亮度范围>=16且popcount4…60才接纳，低细节/单向渐变会漏图。两签名XOR popcount<=6（真实一致率>=90.625%），显示向下截断2位；不表示对象/人脸/语义概率、像素/字节相同或删除建议，即使100%仍可颜色/细节/比例不同。跨既有PNG/JPEG/BMP/TIFF/静态完整画布GIF，16MP/16384；旋转/裁剪/特殊格式/动画/精度范围仍明确受限。

ImageTools解码仅提取私有Stream/path核心：既有Decode(Input)及Metadata的CreateDecoder(Input)保留wrapper，旧128MiB、PNG动画检查、原创GIF、指定WIC codec闭集、单帧/16MP/16384/SDR/ICC/EXIF朝向/完整像素冻结逻辑保持；新DecodeSnapshot只用MemoryStream，原编码和metadata逻辑不动。Fluent原生虚拟结果/问题tab、固定显式copy/close/scroll/workarea-DPI，前1000条Ordinal路径对而非最佳；已完成计数、Partial及全部issues零匹配仍可见。只明确复制安全显示路径/位差/原因，无图片/原内容、打开/删除/持久/网络。Mac比较使用自身规则并会分group/先排exact，Windows不保证同结果，常用目录批量入口、清理及可靠回收站撤销继续待做。

93312常规核心已通过，独立43481探针和坏图异常修复复核通过；57条翻译与185个本地文件链接检查、最终无未解决P1/P2的复核及原生0警告/0错误只编译记录见TESTING，Windows实机FS/WIC色彩/GUI/DPI/Narrator/ARM64仍未执行。新MyWheel动作ID使0.40及旧版可能整载设置失败/回默认/下次保存覆盖，降级需升级前备份。52项仍20限定实现/15部分/15待做/2差异、普通剩余30，F04继续部分。先pull无新远端Mac(originb9d6020/local76fbccb)，122Mac/80旧ZIP基线保留，本批不写/构建/安装/运行Mac app与测试。

期间另一个Mac会话提交3f4e3a7安全补强与8db4e8d体积优化：剪贴板完整快照/累计预算、缩略图像素上限、草稿云目录规则、Doctor/MCP身份与历史保护，另去除未用代码/字符串并调整根macOS build.sh符号处理。已只读核对并原样保留，未运行该脚本或Mac app/test/build/install；相对本批122文件起点当前112相同/10并发变化（含根build.sh），最终差异以外置时点审计为准。

## 0.42已交付

默认关闭AppRouting的稳定file.assistantdraft，Applications文件选择及动作/Quick/MyWheel显式选择1–100普通本地文件。Core严格Rune检查UTF16，不可静默JSON替换坏代理项；Default JSON编码使非ASCII/隐藏/C1/PUA/非字符/变体等路径可见且可完整还原。静态四语只读/不服从文件名内容指令/询问用户意图，12000 UTF8完整草稿预算，超出整拒不截断。Codex/ClaudeCode enum闭集构建codex://threads/new?prompt=或claude://code/new?q=，单独EscapeDataString编码唯一query值；+/%/&/#不会改变query，不传folder/path/file/originUrl/工作区目录。2048 ASCII完整URI是本工具保守上限，超出禁Open而Copy全文可用，并非供应商硬上限。B04原要求的构建/完整预览/URL编码/协议闭集已限定实现；G01内置模型/凭据客户端与G02MCP仍待做。

Native Session复制不可变paths，先完整Core预检，再逐个NativeFiles.OpenRead hold输入及祖先、不读内容；来源直到完整预览/明确Copy/Open/取消/失败清理保持，最后输入失败也释放此前全部，worker返回后token查、workFeature首次await前绑定。原安全层、EXE/Web路由/序列化/开关/schema均不改。Plan不可变含精确Text/Paths/Address，Session防换另一选择计划；ProcessStartInfo固定UseShellExecute/open/SystemDirectory，无args/cmd/自动粘贴。Windows注册handler身份/版本/可用性未验证，启动失败留modal可Copy，nullProcess可为现有应用接收；不下载安装/探测注册表/联网/自动发送消息。只读请求不是强制sandbox，接收app自行控制已有上下文/权限/网络；未传目录不代表消除其已有工作区。窗口关闭后文件可变，JSON引用不是内容快照。

Fluent原生目标Combo/完整JSON引用、完整草稿和可打开地址的readonlyscroll字段、固定Copy/Open/Close+状态错误、工作区/DPI。Copy显式包含完整路径，不含内容或附件；打开仅请求预填，需在接收端审核后发送。Mac可用窄workspace，Windows本批不传；路径转义/保守长度不同，不能宣称完全同一交接。官方2026-10-24核对：[OpenAI](https://learn.chatgpt.com/docs/reference/commands)、[Claude](https://support.claude.com/en/articles/14729294-open-claude-desktop-with-a-link)。没有在本Mac实际启动目标app或写剪贴板测试。

93523常规Core通过，独立agent最终统计见TESTING；Native仅交叉编译，真实Win32锁/失败释放/协议handler/目标app/Clipboard/GUI/DPI/Narrator/ARM64仍未执行。新增MyWheel ID会使0.41或更旧整载拒绝/默认/保存覆盖，降级需旧备份。52项现在21限定实现/15部分/14待做/2差异、普通剩余29，B04由待实现转限定实现；这不是实机验收或整体完成百分比。先pull Alreadyuptodate originb9d6020/local4bd7470 Mac1.12.5，无新远端Mac，122Mac/82旧ZIP基线保留，不写Mac实现/测试/构建/安装。

## 0.43已交付

先git pull --ff-only origin main返回Already up to date，HEAD及origin/main均5b83c0c/Mac1.12.6(34)。相对0.42没有新Mac实现；远端已包含上批只读核对过的7a16d7f绳索/正规文件复核/reopen token及5b83c0c版本。保存0.42 Windows、122Mac文件和84旧ZIP基线；本批不写/构建/测试/运行/安装Mac，任何并发改动按最终时点审计保留。

稳定image.pngmetadata显式单个本地PNG，由Files/Quick Actions/My Wheel进入。原创容器编辑器32MiB/4096块（含IHDR/IDAT/IEND）、16MP/每边16384、支持SDR精度；全输入私有快照、全块CRC（包括移除块）、ASCII类型/reserved位、IHDR字段、PLTE/tRNS基本约束、连续IDAT及非空总负载、最终零长IEND/无尾随数据。动画acTL/fcTL/fdAT、dSIG/caBX已识别签名/凭证、cICP/mDCV/cLLI复杂HDR、未知critical/16bit/损坏边界整拒；这是有界结构子集，不是全部ancillary语义/PNG标准认证。

只移除全部tEXt/zTXt/iTXt/eXIf/tIME块，不解析/展示/解压其payload；保留IHDR/PLTE/tRNS/IDAT及其他块原字节、CRC、顺序和相对IDAT位置，不重新编码图像。ICC/色彩/未知辅助块仍保留且可能含私人信息，图像/文件名/文件系统信息也可能泄露；不是完整隐私清除。EXIF方向被移除可能改变其他应用的旋转/镜像显示，原像素不旋转。依据[W3C PNG3](https://www.w3.org/TR/png-3/)的chunk/CRC及仅改ancillary的保留规则事实，未复制实现/添加依赖。

NativeFiles.Input和所有祖先全程held，先Core完整计划及clean snapshot，再由现有ImageTools.DecodeSnapshot仅对clean快照执行原built-in PNG/单帧/SDR/尺寸/色彩验证；保留的ICC等仍可能由原生decoder解释。被移除的text/EXIF不会送进WIC；不能称完全不解析元数据。取消检查贯穿快照/CRC/输出和STA边界，Quit等待收尾。原生Fluent只读JSON路径/四字符chunk类型/组数量/含12字节framing的总大小/去留，固定Save/Cancel、滚动、workarea/DPI及四语言；不显示raw私密payload、不用归一化图冒充原显示预览。没有匹配块时UI禁Save且服务再次拒绝。独占旁输出“text-exif removed.png”，不覆盖、不回写源，不复制文件系统时间戳/属性/ADS、无剪贴板/网络/持久历史；失败/取消只清自有stage。

93820常规核心通过；原生应用/测试交叉编译0警告/0错误，独立agent最终结果见TESTING。Windows/WIC真实色彩/方向/显示/FS锁/取消与UI/Narrator/DPI/ARM64未实机执行。C06仍partial（选择性tag/无损JPEG/TIFF/完整清除保真仍缺），52项21限定实现/15partial/14pending/2platform、普通剩余29不变，不把子范围提升冒充整项完成。新MyWheel ID使0.42及更旧版可能拒整份prefs、回默认并后续保存覆盖；降级前保留升级前备份。NativeFiles/ImageTools旧算法/Prefs schema/开关/依赖不变，84旧ZIP保留。

## 0.44已交付

先git pull --ff-only origin main返回Already up to date，HEAD及origin/main均c2b72a7/Mac1.12.6(34)，与0.43结束时相同，无新Mac实现。c2b72a7只新增根README的Mac下载链接，已在上批核对并保留。本批起始保存0.43Windows、122Mac和86旧ZIP基线，不写/运行/测试/构建/安装Mac；任何并发变化按最终时点外置审计保留。

稳定image.jpegmetadata显式单个jpg/jpeg。原创有界容器编辑器：源32MiB、16MP/每边16384、8bit SOF0 baseline或SOF2 progressive、1/3组件（采样H/V1–4，采样块总和<=10），本工具最多65536标记（含SOI/EOI/SOS/RST）/512 scans，不是JPEG标准上限。检查SOF/SOS精确长度/组件唯一/选择/谱区间/近似字段；扫描FF00单字节stuffing、真实marker前FF填充、连续扫描和精确末尾EOI/无尾随。RST须启用非零DRI且RST0–7循环，每次SOS编号从0重置、DRI跨scan保持，DRI0关闭；检查序号不等于检查MCU间隔。填充FF归后续真实marker，移除该marker时一起移除，保留scan中的stuffedFF/restart字节不重写。不存在PNG式逐块CRC，结构+native decode不能保证发现每个压缩位损坏或完成完整JPEG/Huffman/渐进语义认证。

只移全部APP1（EXIF/XMP/其他）、APP13（Photoshop/其他资源）和COM，不解读其payload或跟随任何内容链接；marker-like payload当不透明字节。其他支持的marker/表/帧/SOS/压缩扫描保持原字节原顺序，不重新编码。ICC APP2仅接受ICC_PROFILE\0头，序号1..N唯一完整/总数一致且第一scan前；分片物理乱序允许，不重排、不解析ICC正文。非ICC APP2（MPF/增益图等）及全部APP11（可能JUMBF/凭证）整拒；同时拒算术/无损/层次/差分/CMYK/高精度/其他编码marker/多帧及尾随数据。不是全面识别HDR/凭证：ICC、APP0缩略图、Adobe APP14、其他保留数据仍可能含HDR/私人信息；移除APP1/APP13可能改变方向/色彩解释/裁剪/应用显示，不保证渲染像素或视觉一致，也不是全部隐私清除。只承诺保留的压缩数据与segment bytes不变。依据[T.81 JPEG事实](https://www.w3.org/Graphics/JPEG/itu-t81.pdf)及[ICC嵌入规则](https://www.color.org/profile_embedding/)，未复制第三方实现/新增依赖。

NativeFiles.Input及祖先贯穿完整load/preview/save，私有snapshot→Core全结构预检→clean bytes→现有ImageTools.DecodeSnapshot只验证clean（原built-in JPEG/单帧/SDR精度/尺寸/色彩gate保持）；保留的ICC等仍可能被WIC解释，不是完全不解析元数据，也不能从8bit推导全面非HDR。Fluent完整只读JSON路径/类型-数量-含framing大小-去留概要、baseline/progressive/scans/markers/restarts、固定Save/Cancel、滚动/workarea-DPI/四语言；不展示rawpayload、不用归一化图冒充原始显示预览。RST计入总marker和scan数据行，不另加虚构summary行。0matches界面和服务双拒保存。保存原件旁“exif-comments removed.jpg”独占副本，原件与旧副本不覆盖，不复制文件系统时间戳/属性/ADS。取消贯穿字节页/marker/输出/STA边界，Quit等待清理；无网络/自动剪贴板/外部启动/持久历史。PNG仅当前说明文字修正JPEG已有受限入口，PNG Core/Document和NativeFiles/ImageTools算法均不变。

93966常规核心通过，应用/原生检查交叉编译0warnings/0errors；独立agent最终证据见TESTING。真实WindowsWIC/颜色/方向/显示/文件锁/失败清理/GUI/DPI/Narrator/ARM64未执行。C06仍partial：选择性tag、无损TIFF、完整元数据/隐私保真及更完整JPEG类型尚缺；52项21限定实现/15partial/14pending/2platform，普通剩余29不变。新MyWheel ID旧0.43及更早可能拒整份设置/默认并后续保存覆盖，降级前保留升级前备份；Prefs schema/默认开关/依赖不变，86旧ZIP保留。

## 0.45已交付

先git pull --ff-only origin main返回Already up to date，HEAD=origin/main c2b72a7/Mac1.12.6(34)，较0.44无新Mac实现。保存0.44 Windows、122 Mac文件、88旧ZIP基线；不写/运行/测试/构建/安装Mac，保留并发改动和根README下载链接。

既有audio.music原生Fluent窗口新增默认关闭的会话选项：Lower Music During Active Main Audio，阈值-60..-10dBFS、活动音乐0..100%（默认-35/20%），关闭时忽略禁用字段的无效文本。仅控制背景音乐相对已设Music Volume的比例，主音频保持原增益/淡入淡出。原创全声道平方RMS，读取所选trim全部主采样，在主增益和完整选区淡入淡出后、削波/量化前判定；分母含实际声道/末窗帧数，不先下混，反相立体声不会抵消。约20ms非重叠窗口取最近整数帧，阈值相等也活动，最后不足窗用实际长度；所有信号类型都可能触发，非Speech/SoundAnalysis模型。

完整选区最多600秒且128MiB PCM数据，超限在任何header/内容分析前拒绝，可缩短或关闭。每次preview/save重新先分析全选区（包括10秒试听以外），只存布尔窗及固定4096帧缓冲；增加读取/准备成本。窗口内响应可提前约一窗；线性降低约20ms、恢复约250ms指完整深度，部分反转耗时更短。整数公共单位逐帧累积深度、先步进再应用倍率、反转继承状态；从trim0计算，音乐尚未进入或已经结束仍维持主活动包络。完整trim fade与试听共享历史，16bit主源试听等于完整输出前缀；其他位深保留旧直接PCM16混音舍入路径，不承诺先save再转16bit逐字节相等。

Ducking=null时完全绕过分析/新限额/倍率乘法，旧同采样率混音、4096/4097缓存分界、循环/声道/剪切路径不改。NativeFiles两源/祖先持有贯穿对话框、试听和保存，分析后显式重置主流位置；参数修改/关闭取消试听，App finally等待再保存/释放。无需新动作ID/设置schema/持久参数/开关/运行时依赖；原件旁独占发布及失败只清自有stage沿用。四语/Fluent复选框/字段/固定footer/工作区DPI，无麦克风/联网/模型/自动播放。

220513常规核心通过；原生检查编译0warnings/0errors，独立agent结果见TESTING。音量活动替代不是语音分类：噪声/音调/已有音乐触发，轻声漏检，真实听感/速度/播放/Windows文件锁/WPF/Narrator/ARM64未执行。D06从pending变partial，52项21限定实现/16partial/13pending/2platform，普通仍29。语音分类/转写/字幕烧录、压缩音频/视频配乐、自动混音重采样/交叉淡化/保音高处理仍待做。88旧ZIP保留，Mac独立。

## 0.46已交付

开始git pull --ff-only origin main返回Already up to date，HEAD=origin/main c2b72a7/Mac1.12.6(34)，较0.45无新Mac实现；保存0.45 Windows/122Mac/90旧ZIP基线，保留Mac下载链接及并发改动，无Mac实现/测试/构建/安装写入或执行。

现有audio.music追加会话参数Loop Crossfade (ms)，默认0保留0.45直接循环及RMS活动压低的全部旧字节路径。只有Loop开启才解析字段；关闭后保留但忽略无效文本，改Loop/数值仍取消旧试听并刷新校验。有限0..1000ms，非零按AwayFromZero最近整数帧C>=2，且C<=floor(N/2)（N为音乐文件帧数）；非Loop Core带非零、负数、NaN/Infinity、少于两帧或超半长整拒，在任何duck内容分析/header前。无新CatalogID/Prefs/schema/持久设置/开关/依赖/联网/模型。

原创线性振幅尾首重叠，P=N-C。音乐相对输出起点t<P先播原sample[t]；t>=P令q=(t-P)%P，q<C时tail[P+q]*(1-q/(C-1))+head[q]*q/(C-1)，端点直接取tail/head，q>=C播放sample[q]。C<=P确保最多双重叠、半文件情形无空段；新循环周期P比文件少C帧，不是重采样/变调/节拍匹配。先每本源声道浮点合成，不提前整数化，再套旧mono/stereo、musicvolume/full-overlayfades和活动压低，最后与主音频相加并一次量化/削波。主时长/采样率/声道/位深保持。相位抵消/响度下陷/节奏变化可能，C2仅端点，不保证clickfree或与Mac听感相同；短overlay可能未到接点。

音乐<=4096frames缓存一次，大于阈值按初始P/周期P/overlapC/4096块界分段读取，最多两个固定4096frame原始缓冲和4096frame×声道double数组，不为长音乐分配全量PCM。重叠两端分开缓冲，读取前后取消，seek字节用long；非缓存路径不逐样本I/O。主/音乐两源及祖先持续持有；原件旁独占发布/自有stage取消失败清理、参数更改/关闭Stop、App finally等待试听再保存/Dispose/Quit保持原流程。0不创建新reader/缓存或乘法路径。试听使用完整相对音乐周期/整段fade/活动历史，仅限写10秒PCM16；16bit主试听等于保存前缀，其他深度保留旧直接PCM16混音舍入边界。

Fluent原生字段/Loop联动/四语完整说明、scroll/fixedfooter/workareaDPI不改风格。224647核心通过，原生编译0warning/error；独立agent最终证据见TESTING。Windows真实听感、音频设备/播放/性能/FS/GUI/DPI/Narrator/ARM64未执行。D02补限定范围，不更改计数：52项21限定实现/16partial/13pending/2platform，普通29；自动混音重采样、等功率/节拍匹配、压缩/浮点编辑/视频配乐、语音分类/转写/字幕烧录仍待补，不称平台不兼容。90旧ZIP保持。

## 0.47已交付

先git pull --ff-only origin main返回Already up to date，HEAD=origin/main c2b72a7/Mac1.12.6(34)，较0.46无新Mac实现；保存0.46Windows/122Mac/92旧ZIP基线，保留Mac下载链接及并发改动，无Mac实现/测试/构建/安装写入或执行。

独立稳定image.gifpalette，明确单图Reduce Colors to GIF…（旧convert.gif/GifExport/原预设完全不改）。既有NativeFiles+ImageTools.Open保持本地普通/held源祖先、built-in编解码器/SDR/静态/方向与色彩规范化边界；ReadPixels edge0取完整16MP/16384边像素，源文件<=128MiB而非总内存预算。原生Fluent调色板16/32/64/128/256下拉、Generate按钮、原图/减色图两个nearest预览tab、固定Save/Stop/Cancel、滚动/DPI/四语言，生成后显示actualpalette/max及changedopaque。预览<=960边最近邻，从完整结果缩图，可能隐藏色带/细节，保存同一完整结果，不对缩图计算量化。

原创量化：32768个5bitRGB桶，opaque count用int/原RGB sums用long；精确颜色集合最多max+1即停止，透明占一槽、alpha0隐藏RGB归0。先完整alpha校验，任何1..254整拒不铺底；若全部实际色(含透明)<=max保持opaque RGB/旧GIF bytes。否则opaqueLimit=max-透明槽；按(max轴5bit范围+1)*权重选可拆box，score相同按生成顺序，最大轴范围RGB顺序tie。唯一binId排序为轴值再binId；weightedmedian累加>=ceil(weight/2)，末端clamp确保两侧非空，parent退休/children追加。桶归box的原8bit加权RGB均值nearest-half-up，无nearest-color搜索/dither，均值重复合并实际条目，可低于limit。每桶/像素按页取消，<=32768排序前后取消而不从comparer抛，避免取消包装成错误；最大256box，不无限颜色集合/无限分裂。渐变/颜色损失明确，不宣称感知质量或精确source总色数，改变统计只看opaque RGB。

Core Result私有输出像素，Preview返回独立缩图、Write固定此结果；调用方改源/缩图不会改保存。Document Preparation绑定原doc Owner，拒跨图/Disposed保存，NativeFiles独占原件旁新GIF、原件旧副本不替换。UI每次参数改/Stop/新生成即清旧prepared+禁Save，token+revision拒晚结果；Save只接受当前已完整生成的对象，Closed取消，App finally awaitFinish后才写出/释放源，Quit保留既有join。无自动生成/自动复制/网络/历史/新依赖或model；GIF不存metadata/profile，文件系统属性/时间/ADS不复制。

369902常规核心通过；原生检查编译0warning/error，独立agent最终证据见TESTING。新增异步native测试的确定P2隔离问题已修：Main先建仅internal validation App，跳过真实Prefs/mutex/托盘/热键启动且ExplicitShutdown，palette fixture强制验证host并在第一次await前安装/最终恢复DispatcherSynchronizationContext；正常public App启动false路径不改，无CLI/env绕过。16MP longsum/dominant-bin、低色byteexact、透明/latealpha、反序hist不变、private结果/nearest预览及旧writer拒绝回归通过。Windows真实WIC/图像质量/内存速度/FS/GUI/DPI/Narrator/ARM64未执行，不把开发机解码/编译当验收。新MyWheel ID使0.46及更早整Prefs加载失败/回默认并可能下次保存覆盖，降级保留升级前备份；schema/默认开关/24preset/设置预算/依赖保持。C02仍partial，52项21/16/13/2、普通29；动画/完整色彩/HDR/扩展格式/抖色及GIF预设量化尚缺，92旧ZIP保持。

## 0.48已交付

开始git pull --ff-only origin main返回Already up to date，HEAD=origin/main c2b72a7/Mac1.12.6(34)，较0.47无新Mac实现；保存0.47Windows/122Mac/94旧ZIP基线，保留Mac下载链接与并发改动，无Mac实现/测试/构建/安装写入或执行。

新增独立稳定doctor.startup，Doctor默认关闭，无文件输入。App在第一次await之前绑定workFeature=Doctor，扫描前后及弹窗前检查取消，关闭功能/退出阻止迟到窗口。原生Fluent虚拟列表/逐根状态tab/只读完整详情/固定Copy与Close/scroll/workareaDPI/四语言，只有明确按钮才扫描一次，无后台或自动刷新。

只从HKLM/HKCU的Software\Microsoft\Windows\CurrentVersion\Run与RunOnce读取，同步请求各32/64位视图。RegOpenKeyExW仅KEY_QUERY_VALUE|单一WOW64位，RegEnumValueW一次取得同位置的名称/类型/数据，SafeRegistryHandle持有并释放；仅System32 advapi32两API，不递归/读文件/展开环境/解析执行命令/写注册表/联网/提权。每根256尝试、共2048，名称256/命令2048 UTF16单位、约10秒单调软时限；阻塞调用可能迟到，保留返回的完整条目及TimeLimit，后续根标未完成。恰256未见END保守标limit。MORE_DATA同时拒绝名称与数据，不使用部分缓冲；严格REG_SZ/EXPAND_SZ UTF16，允许去一个末尾NUL，内嵌或双NUL/坏代理拒绝，BOM保留可见转义，空白命令记Empty，其余不trim。

同根OrdinalIgnoreCase重复名称保留并标注，跨根不合并；视图可能共享/重定向/经注册表链接，未验证底层key身份/链接目标，ARM32特定视图缺失。实时可变、枚举顺序不保证，可能漏/重复；位置序号不是稳定ID。结果不证明可执行文件存在、实际启用/将运行、信任/签名或启动影响；RunOnce还受系统条件影响。StartupApproved、启动目录、任务、服务、Store应用、其他用户和修复未做。微软对运行命令的260字符说明不等于本工具2048单位的只读预算；可能读取超执行规范的历史/无效配置，不验证其可运行性。[Run/RunOnce](https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys)、[枚举契约](https://learn.microsoft.com/en-us/windows/win32/api/winreg/nf-winreg-regenumvaluew)、[注册表视图](https://learn.microsoft.com/en-us/windows/win32/winprog64/accessing-an-alternate-registry-view)。

名称直接出现在列表，可能有私隐；命令默认隐藏，Show Reported Commands统一控制选中详情/选中复制/全报告复制，列表/ToString/tooltip/错误不含命令。AppMemory.Visible转义不可见/控制字符，完整报告按转义与翻译之后的最终UTF8含换行限4MiB，超限整份拒绝；隐藏命令不占预算。无自动复制/持久历史，明确复制可能含秘密。不修改既有InstalledApps读取/报告、设置schema/默认开关/依赖；但保存doctor.startup到My Wheel使0.47及更早可整Prefs拒绝/默认/下次保存覆盖，降级前保留升级前备份。

370067常规核心通过；应用/原生检查编译0warning/error，独立最终证据见TESTING。Native新增Request/Decode/预取消和虚拟窗口测试，只编译不执行Windows。真实注册表/WOW64/ARM64/链接/并发变化/句柄及WPF/剪贴板/DPI/Narrator验收未执行。F09从待实现变部分，只交付受限只读登录配置线索，深诊断/启停/修复仍待做；52项21限定实现/17partial/12pending/2platform，普通剩29。94旧ZIP保持，Mac独立。

## 0.49已交付

先git pull --ff-only origin main返回Already up to date，HEAD=origin/main c2b72a7/Mac1.12.6(34)，相对0.48无新Mac实现。保留0.48Windows、122Mac与96旧ZIP基线，原README Mac下载链接及并发改动保留；不写/运行/测试/构建/安装Mac应用。

新增独立稳定audio.g711，显式单个wav → Decode G.711 WAV…，普通文件动作，无新可选开关/设置schema/依赖/模型。只读小端RIFF/WAVE tag6(A-law)/7(mu-law)；8bit码字、单/双声道、容器整数8–192kHz。fmt仅18字节且cb0，block=channels/avg=rate*channels；唯一fact至少4字节，首uint为每声道frame=dataBytes/channels而非总samples，唯一非空整帧data；三块任意顺序、RIFF长度精确、奇数块含pad，<=1024chunks。拒fmt16/extensible/RF64/raw/RTP/其他codecs及LIST wavl/slnt，其他块跳过不加载/解释。fact额外内容、metadata/cues/loops/未知块均不保留，不能称归档转换。部分真实系统producer输出fmt16/缺fact/缺oddpad，明确拒绝；独立测试可用相同codewords规范封装验证，但不算原始producer兼容。

原创按sign/segment/mantissa与偏置展开：A-law去even-bit inversion，传统PCM16 scale，最小幅值±8无exactzero/最大±32256；mu-law去全位取反，两个零码字均整数0、最大±32124。每个256码字都合法，不需要音频内容预扫描，但必须扫描完整RIFF结构/fact及源总预算。保留rate/channels/order/framecount，不恢复有损压扩前细节；输出16bit不是16bit原录音精度。无编码/往返无损承诺/normalize/gain/resample/filter/dither，8k以上只保留容器timebase，不承诺ITU电话8k标准互通或恢复带宽。实现依据[ITU-T G.711](https://www.itu.int/rec/T-REC-G.711-198811-I/en)、[WAVEFORMATEX](https://learn.microsoft.com/en-us/windows/win32/api/mmreg/ns-mmreg-waveformatex)、[RIFF容器](https://learn.microsoft.com/en-us/windows/win32/xaudio2/resource-interchange-file-format--riff-)及[6/7格式登记](https://www.iana.org/assignments/wave-avi-codec-registry)，未复制第三方codec源码/库或添加运行时依赖。

输入与完整输出各512MiB/2h，PCM目标总长44+2*dataBytes；先按DataBytes>(MaxBytes-44)/2防乘法溢出，Document.Open与Export均验证完整输出，试听也在任何PCMheader/音频body读前拒超限。4096frames编码输入/PCM输出固定缓冲，前后取消，第一帧到10秒PCM16和保存前缀一致，无额外量化。NativeFiles源/祖先持有贯穿modal、试听及保存，App加载后/弹窗前/等待试听后均检查token，finally等待FinishPreviewAsync再Save/dispose/Quit；原件旁独占decoded.wav，失败只清自有stage、不复制文件metadata/ADS/属性/时间。原生Fluent事实/完整限制/明确Play Stop Save Cancel、固定footer/scroll/workareaDPI/四语言；无自动播放/麦克风/联网/剪贴板/持久历史。

旧PcmWave/FloatWave/AiffWave与编辑/混音保持；兼容页更新，仅新audio.g711存入MyWheel会令<=0.48可能整Prefs拒绝/default/下次save覆盖，降级保留升级前备份。542659常规核心通过，应用/原生检查编译0warning/error，独立最终证据见TESTING。Windows真实G711文件/播放/文件锁/stage/低空间/WPF/DPI/Narrator/ARM64未执行。D04继续partial，52项21限定实现/17partial/12pending/2platform、普通29不变。直接编码编辑、其他压缩格式/MP3/AAC、自动rate转换、保音高速度、视频及完整格式兼容继续待做，不称平台不兼容；96旧包保持。

## 0.50已交付

2026-10-25：开始实际pull Already up to date，HEAD=origin/main c2b72a7/Mac1.12.6(34)，相对0.49无新Mac实现。0.49Windows/122Mac/98旧ZIP基线保留，不写/运行/构建/测试/安装Mac；完整新范围见[README](README.md)。

三条工作流：audio.systemdecode 使用系统MediaTranscoder流式输入准备完整PCM16，静态文件头只开放RIFF/WAVE、LayerIII MP3、ADTS AAC和FLAC，ID3头严格版本/flag/synchsafe/32MiB界限。输入/准备目标各128MiB、mono/stereo8–192k/2h；目标完整验格式/预算后才允许试听和独占旁输出。输出去metadata/视频/封面/cues/loops，系统decoder精度/priming/padding可变化；M4A/MP4/播放列表/其他容器待做，缺codec/DRM/替换rate/ch拒绝，无自动下载/麦克风/网络。系统版别/真实文件互通及性能未验收。

pdf.raster 使用Windows.Data.Pdf只读固定源流，10源/每源32MiB/总128MiB/1000页；Parse ASCII页序允许倒序与重复，50选页/16MP每页/100MP整计划/400–4000长边，0/90/180/270转向；Save前重验计划与真实来源几何，PNG渲染流80MiB、完整PDF输出512MiB。DIP不是PDF点，统一FromDip×0.75，Letter816×1056DIP→612×792pt并含A4/旋转回归。原创栅格PDF写入保物理纸张尺寸；白底，所有矢量/文字搜索/结构/交互/metadata/签名不保，渲染/色彩/透明/批注可能变化，不冒充保真PDF编辑器。所有输出在首源旁；多文件过程中失败/取消保留已经发布的输出。

TextCapture新增枚举末尾默认关闭；text.ocr仅显式图片、本机已装语言和Recognize，结果可编辑后明确复制；完整UTF16/noNUL/262144上限，不截断。WPF图片加载沿用OnSta，白底合成，原尺寸超过OcrEngine.MaxImageDimension拒绝，不暗缩。NativeAsync先请求Cancel，再await不绑定token的真实终态，最后Close，任何源/bitmap/stream不会因token任务提前完成而被释放；Close/Quit/feature-off等待清理。此软取消不能保证系统调用立即结束。BoundedStream写预算覆盖Seek/Position/SetLength/Write，兼容write-only staging，dispose视图不dispose调用方流。

**PDF/OCR目前只实现有身份的条件路径**：按[微软桌面WinRT支持列表](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/winrt-api-desktop-app-support)，实际GetCurrentPackageFullName在打开PDF/OCR输入前检查；无身份ZIP明确不可用。并非实验已证明所有无身份调用必然失败。可选Windows/Packaging/build-msix.ps1只用本机Windows SDK MakeAppx产生未签名MSIX：Win11最小22000、唯一runFullTrust、四语言、原创三枚图标，源目录及子项拒reparse、LiteralPath含隐藏项复制、独立stage、File.Move不覆盖，不下载/签名/信任/安装。无实际MSIX产物/MakeAppx/真实身份验收；runFullTrust不提供网络隔离。用户需在Windows完成匹配publisher签名/证书信任/安装及功能验收，不能把编译当ZIP可用性证明。

TFM net10.0-windows10.0.22000.0，新增微软SDK.NET.Ref10.0.22000.57 Windows profile中的Microsoft.Windows.SDK.NET.dll和WinRT.Runtime.dll2.2.0，仅两项投影运行库；SDK工具/winmd/源生成器/WinUI/WindowsAppSDK不分发。独立核对[SDK REDIST](https://learn.microsoft.com/en-us/legal/windows-sdk/redist#microsoftwindowssdknetref)明确列两DLL，附未修改许可RTF/官方REDIST HTML/准确CsWinRT release许可，运行库需逐字节匹配官方nupkg。.NETCore/Desktop runtime仍10.0.13，避免全局RuntimeFrameworkVersion误用于SDK.Ref。新Feature/MyWheel ID可能令<=0.49整Prefs拒绝/default/随后覆盖，降级保留升级前备份。

553971常规核心通过，原生套件交叉编译0warnings/errors仅；独立agent见TESTING，一个DIP→point P2已修复回归。WindowsAPI/codec/PDF/OCR/GUI/FS/播放/DPI/Narrator/ARM64和PS/MakeAppx/签名/安装均未执行。C09/E02从待做到条件部分，52项21/19/10/2、普通剩29；全部媒体、保真PDF、多语言OCR质量仍待补，不称整体完成。

## 0.51已交付

2026-10-25再次实际pull Already up to date，HEAD=origin/main c2b72a7/Mac1.12.6(34)，无相对0.50新Mac实现；保存0.50Windows/122Mac/100旧ZIP基线，无Mac源/测试/构建/安装写入或执行。沿用.NET10/WPF及现有Microsoft投影，无新增依赖/模型/资源下载，8许可notice保持。

QR创建/编辑新增原生模板Tree/Bouquet/Island、季节Summer/Spring/Autumn、明确打开3D Garden。原创模块浮雕不是直接复制Mac SceneKit Builder，也不冒充完整Mac树/花束/岛屿或季节动画；全部v1–40/L/M/Q/H复用原QR模块，无暗中级别转换/文本截短。深色模块单位方柱坐标与原矩阵逐格一致，白色底板含完整四模块quietzone，所有额外装饰位于板下；高度/配色由模块坐标确定，不读时间/随机种子/ID。最多n²+6boxes，分颜色合并mesh，每盒24vertices/36indices，冻结完整Model3DGroup后跨线程交付；真实WPF/3D显示与性能未执行，预算不是GPU/RAM硬限制。

正交相机旋转/20–90°观察角、Top View/Reset View，单次后台生成/Stop/Close，控件和状态四语、正文滚动/固定footer/workareaDPI。修复P2：原camera.Width只按横向会裁掉宽viewport中的上下模块；现在Core.ViewWidth=(n+8)×1.65×max(1,w/h)，覆盖所有box原点球界，SizeChanged刷新。独立角点投影覆盖宽/窄/正方形窗口、yaw/elevation以及v40；这是数学/源码检查，不能当实际渲染或扫码。光照/视角/屏幕和密集码可能不可扫，继续用原平面复制/PNG分享打印；不导出3D模型。

QrDialog在子modal前先设置TCS，子finally等待生成结束，父Appfinally等待QrDialog.FinishAsync，feature-off/Quit与关闭等待生成并抑制迟到model。Loaded单飞，取消后只清私有模型，不触碰输入文件/剪贴板/网络。旧QR编码/平面renderer/PNG/目录独占安全层不改。

QrEntry追加可选枚举Template/Season，默认Tree/Summer用WhenWritingDefault省略，旧默认prefs/backup字节保持；非默认与稳定ID/文字/分组/Correction一起保存。QrLibrary仍Version1/32/1MiB，闭集可选小写字符串字段，未知/重复/null/错误类型整份拒绝；同名任何choice不同整批冲突，不悄悄覆盖，Preview展示选择。Mac导入已知tree/bouquet/island和summer/spring/autumn，缺失/null/空用Tree/Summer，未知每行标issue并保存raw值预览，其他兼容条目可导入；创建日期不迁移。<=0.50设置解析可忽略新字段并在重写时丢选择，含新字段备份被旧闭集reader拒绝，降级须升级前备份；不夸大成所有旧设置都会拒绝。

4432151常规核心通过、native套件编译0warning/error仅，独立最终见TESTING。实际WindowsWPF/mesh/渲染/焦点/DPI/主题/Narrator/扫码/性能/ARM64均未执行；只源码检查的原生fixture新增冻结mesh/indices/normals/单次生成/关闭等待及隔离host。E06从pending到partial，21/20/9/2普通29，完整Mac造型/动画和其他未完成项继续跟踪，不称全兼容。

## 0.52已交付

2026-10-25再次pull Already up to date c2b72a7/Mac1.12.6(34)，相对0.51无新Mac；0.51Windows/122Mac/102旧ZIP基线保留，无Mac源码/测试/构建/安装写入或执行。

新增默认关闭Feature.Assistant（枚举末尾）、独立配置页；Quick Actions中Ask，首await前workFeature绑定、统一Busy/ownerclosed/feature-off/Quit取消。AssistantPlan.Offered只取当前enabled/文件条件匹配的固定Catalog.Actions，不提供自定义App/预设标题/QRpayload。文件只作固定类型计数，无内容读取，不添加名字/路径/任意扩展名；输入请求2000UTF16原样发送（用户可自己输入敏感内容，预览必读），最多160候选、完整JSON32KiB。发送前显示精确body及canonicalendpoint，复选确认+显式Send，任何请求更改使旧preview/result不可用。回复只SelectSuggestedAction→当前候选二次解析/文件snapshot核验/搜索清空重排；用户仍须Return/RunSelected，模型不携带参数、不执行任何操作。

服务完整endpoint使用已有WebAddress严格authority/escape规则，额外拒绝query/fragment/凭据；HTTPS或明确localhost/127.0.0.1/[::1]HTTP。模型闭集ASCII字符128、key可见ASCII1–2048无空格/CRLF，输入框不截断长paste。Chat Completions或Claude Messages，不调用模型列表/自动keytest/下载；max_tokens1024或Chat显式max_completion_tokens1024，Claude header2023-06-01。直接HttpClient/SocketsHandler：无redirect/cookies/proxy/defaultWindowscredentials/压缩、16KiB响应header、10秒connect、60秒整体linkedtoken，成功正文流式64KiB上限，失败正文不显示；无应用层retry/fallback。公司代理/其他认证、SSE/多选/tool_calls/tool_use/refusal/length/max_tokens等非完整回复不支持。严格全层duplicate检查/单assistant文本/stop或end_turn，内层恰action_id(null或closedID)+explanation<=512，外包Markdown/未知字段/参数拒绝，不打开链接。

APIkey只明确SaveKey进入本机当前用户Windows Generic Credential，target按provider+scheme+host+effectiveport，同服务path共用；Read只发生Send操作，改host找不到旧key，不把其转发别处。CredWrite/Read/Delete/Free仅System32 advapi32，CREDENTIALW x64/ARM64 size80，PersistLOCAL_MACHINE（当前用户本机），native blob临时内存清零/free，管理string抹除不保证。通用凭据不是每应用隔离；测试不触碰真实credentialstore。非秘密AssistantSettings为Prefs可选null省略，旧默认byte保持；新Feature使<=0.51可能整设置拒绝/回默认/随后覆盖，不启Feature旧版也可能丢新服务配置，降级保留升级前备份。

常规Core4432330通过，native仅编译0warning/error，独立最终见TESTING；fakeHTTP和假native互操作库不是实际服务/Windows凭据/GUI/ARM64验收。SDK/runtime/Microsoft依赖及8notice保持，无新应用依赖/模型。G01pending→partial，52项21/21/8/2普通29；Doctor完整脱敏解释、动态动作与真实环境继续补。

协议依据：[Chat Completions](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create)、[Claude Messages](https://platform.claude.com/docs/en/api/messages/create)、[CredRead](https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-credreadw)、[CredWrite](https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-credwritew)、[CREDENTIALW](https://learn.microsoft.com/en-us/windows/win32/api/wincred/ns-wincred-credentialw)、[拒绝redirect](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.socketshttphandler.allowautoredirect?view=net-10.0)。不做实际外部API调用作为开发测试。

## 0.53已交付

2026-10-25再次实际pull Already up to date c2b72a7/Mac1.12.6(34)，无新Mac。122Mac文件/104旧ZIP基线保留，无Mac实现/测试/构建/安装写入或运行。

PC Doctor基本快照窗口新增Explain Redacted Snapshot…，继承默认关闭Assistant、明确准备完整JSON/endpoint预览+reviewcheckbox+Send及0.52凭据/直连HTTP边界。DoctorAssistantReport精确Section/Title/Format白名单，PrivateDetails先跳过，只接受int/uint/long/ulong/double有限非负范围及闭集arch/power/DHCP/proxy状态。数值字段另验容量关系、整数计数/采样限度；无任意object.ToString/raw标题格式回显。OS描述、进程/网卡名、盘符、路径、地址、未知/private行剔除；保留省略数和匿名样本序号，无对应身份。GB/MB原始采样使用GiB/MiB，报告明确标记；working sets可共享，逻辑容量不是可回收空间，不推断泄漏/应用归属/联网。

DoctorReportSnapshot immutable字符串，在展示/准备请求间不会因原Values修改而变化；报告最多512事实/32KiB，最终双层JSON独立32KiB限额，任一超限整体拒绝不截短。另显示完整脱敏JSON和完整服务body；输入问题仍原样发送（用户自己可键入私密内容）。返回仅action_id:null+plain explanation<=4096UTF16；原动作模式512/8192限制及默认body字节保持。Doctor内层escapedJSON容许32768字符，以支持4096CJK\uXXXX；完整响应仍64KiB。正常LF/CR/TAB保留，格式控制符可见转义，不开链接、不执行命令、修复或动作；明确Copy Explanation才访问剪贴板。

Quick Actions提供单请求默认false勾选，只有明确同意才加入自定义应用/预设标签（可能含私人信息），否则固定内置候选与0.52字节一致。来源仍当前Catalog.Available/稳定ID，不加文件名路径内容/QRpayload；勾选与请求变化invalidatepreview/review/suggestion，正在Send时锁定。接受建议前完整Prefs.Serialize比对、文件snapshot/候选重验证；只选行，Return/RunSelected手动。偏好变化时不自动重试发送。

Doctor父窗口持有child+completionTask，App finally真awaitFinishAsync；设置Assistant/Doctor关闭立即关闭child并cancel，父关闭/owner/token/Quit均joinkeyread与HTTP，Busy在cleanup后释放，无嵌套RunActionAsync。无新设置schema/依赖/模型，沿用0.52降级风险及升级前备份要求。

常规Core4432386通过；native应用/测试0warning/error仅编译，独立最终见TESTING。基本报告外的FolderMap/duplicates/projects/largefiles/similar/monitor/appmemory/startup/installedapps解释继续待接；G01仍partial，52项21限定实现/21部分/8待做/2平台，普通29。真实Windows凭据/HTTP/TLS/GUI/DPI/Narrator/ARM64未运行，不以fakeHTTP/交叉编译作验收。

## 0.54已交付

2026-10-25。再次实际pull Already up to date c2b72a7/Mac1.12.6(34)，无新Mac提交；122Mac/106旧ZIP保持，无Mac源码/测试/构建/安装写入或运行。

九个静态Doctor结果窗口接入显式Explain Redacted Snapshot…：FolderMap、大文件、项目目录、重复文件、文本相似、图片相似、AppMemory、桌面安装记录、Run/RunOnce启动记录。DoctorAssistantSummary闭集typed工厂只序列化固定字段的primitive数量/decimal字节/布尔状态，private Build不接外部任意strings/dictionary；原record/Report/Text/名字/路径/标记/哈希/命令/内容/PID/时间戳从不序列化。根扫描已知逻辑小计/partial/entry-time限额、内容读取限额/问题数量、历史匹配数、已知工作集与未知数量、缺失注册表根均分别保留。全未知working set小计为null，测得0是0；不是实际物理/可回收RAM、安装/启用/可信/唯一应用数、可删除候选或文件当前相等证明。

在打开结果页前从完整记录计算immutable DoctorReportSnapshot；助手会话只保留该安全字符串，没有source或延迟factory，当前filter/selected/navigation变化不影响发送，不新扫描。报告明确完整已记录范围/限制，和最终服务JSON分别完整显示，继承默认关闭Assistant、准备/审阅checkbox/显式Send、只action_id:null/解释4096/明确复制、32KiB报告+最终HTTP独立整拒及64KiB响应。正常换行与Rune格式符显示沿用0.53；输入问题仍原样发送。

DoctorAssistantSession单次completion在Show前占有，持有parent/immutable snapshot/token；App九分支统一ShowDoctorOperationAsync try/finally真await FinishAsync，父Close/Doctor-off/Quit取消与子Assistant-off关闭后join凭据读取和HTTP，事件注销/关闭后按钮失效，无嵌套RunActionAsync，不释放Busy给未完成请求。使用现有Fluent Button/WrapPanel/滚动预览，Mac及旧扫描/文件读取/比较算法未改。

常规核心4432438通过，native应用/套件0warning/error仅编译；独立最终见TESTING。新增3key/9翻译，1557旧key/value保持；无Prefs/schema/依赖/模型变化，继承0.52降级备份风险。实时monitor解释、可选Doctor名称和更完整Windows诊断仍待做，G01仍部分，52项21限定实现/21部分/8待做/2平台、普通29。真实WindowsAPI/凭据/HTTP/TLS/GUI/DPI/Narrator/ARM64未执行，不把交叉编译/managed模拟当实机验收。

## 0.55已交付

2026-10-25。实际批次先pull Already up to date c2b72a7/Mac1.12.6(34)，无新Mac提交。122Mac/108旧ZIP基线保留，无Mac源码/测试/构建/安装写入或运行。

CPU/Physical Memory窗口增加Explain Redacted Snapshot…，首条已记录采样前不可用。显式点击只捕获latest局部immutable UsagePoint，先占monitor completion再暂停UpdateSampling失效旧代次；不加history、进程名、路径、PID或系统时间戳，不新读API。DoctorAssistantSummary.Monitor只允许finite/nonnegative elapsed、closedCPUstatus、finite0..100%、CPU需两样本的nullable基线状态；memory%/used/total须全null或成套、total>0/used<=total、decimal百分比一致，available用ulong差。全未知保留null，不是0；字节支持完整ulong，不凭空限制长时间开窗。报告与窗口SingleSample标记明确一条采样，不能误称完整图表。

发送沿用默认关闭Assistant、独立完整snapshot+body/endpoint审阅、checkbox+Send、null-action/4096解释、双32KiB整拒/64KiB响应、明确Copy，不自动执行修复。监控解释期间Pause/Explain禁用至cleanup结束，防止子窗已关闭但凭据/HTTP尚待join时提前恢复；结束仍paused，只有Resume Monitoring明确操作启动新CPU基线。关闭/Quit/Doctor-off/Assistant-off均join子请求，StopAsync同时发起助手关闭与sampler停止，通过try/finally等待两路，即使一方失败也不遗留另一读取；阻塞系统API可能稍后才返回。

修复已有UI采样代次缺口：UsageSampler内部generation只覆盖返回前，PollAsync过去在Expected异常catch后可能于pause→resume时接受旧失败；成功结果排队等待UI回调也有相同窗口。本批NativePoll在await前捕获samplingVersion，每次真正采样开关变化递增，success/Expectedcatch两路回到UI均核验，旧代次整丢弃；CoreSampler算法/错误行为保持。新的Session.OpenAsync在同步进入ShowDialog前先赋自身completion，保留九静态窗口单飞/取消/真join。UsageNotices改“无自动网络”，明确助手审阅发送；AppMemory路径分组已存在的旧说明一并修正。

常规核心4432466通过；native应用/套件0warning/error仅编译。原生fixture新增受控fake读+Dispatcher pump、改变UIepoch后成功/Expected失败都不改history（不Show、不启动dialog timer、不读真实API）；单样本preview/范围说明、footer四按钮/首采样禁用也编译验证。独立最终见TESTING；6新key/18翻译，1560旧values保持。无新Prefs/schema/依赖/模型；现有0.52降级备份边界保留。G01仍部分：可选Doctor名称及更完整Windows诊断仍待补，52项21限定实现/21部分/8待做/2平台，普通29。真实WindowsAPI/GUI/DPI/Narrator/凭据/HTTP/TLS/ARM64未执行，交叉编译与managed stand-ins不是实机验收。

## 0.56已交付

2026-10-25。实际批次先pull Already up to date c2b72a7/Mac1.12.6(34)，无新Mac提交；122Mac/110旧ZIP保留，无Mac源码/测试/构建/安装写入或运行。

Doctor四种已有报告来源提供可选名称：基本快照中已经显示的process label、AppMemory已读exe路径的Windows字面basename、状态Present的安装记录名称、已知Run/RunOnce值名。DoctorLabels不新读文件/进程/注册表，不取版本/发布者/subkey/命令，也不在缺名时fallback PID/整路径。Windows路径按反斜杠解析，支持明确drive/UNC/extended drive/UNC；NT Device/pipe/仅根/尾分隔符/混合斜杠等不猜名称。基本process label继承原报告已有显示缩短，不额外截短。

候选和原source索引同步冻结，单行<=160UTF16、非仅空白、有效Unicode、无Control/Cf/行段分隔符；Cf按Rune覆盖补充平面。每来源最多4096（startup2048/facts512），超过来源预算整拒，不取prefix；所有合法候选都保留，按kind+原字符串Ordinal区分大小写去重，缺失/无效/重复计数分别显示。这里不保证组合符/变体符单独出现时肉眼可见，完整JSON会转义。候选仅本次内存，不存Prefs/历史/clipboard。

原生默认off checkbox展开虚拟ListView，用Ctrl/Shift由用户逐条明确多选1–40，初始无选中；没有自动top40/选前N。checkbox或选择改变即清空裸report+完整body、review、回复和复制/发送许可，选41项或双JSON容量超限保留原选择并整拒；不会偷偷缩短或取子集。发送前完整名称列表、精确report及最终body/endpoint均可审阅。传输中锁住列表/checkbox/query，关闭/feature-off/Quit仍取消和真join。Report只加所选{kind,text}及明确子集/不关联匿名指标的限制，不携带source索引/标识。原数值报告仍完整，默认off的safeJson/request与0.55字节保持。

名称字段本身可能包含路径/地址/秘密或其他私人文字：选择后切换准确NamedDoctorScope、预览可访问名称改为Doctor Report with Selected Labels，隐藏原来“名字/路径剔除”的默认说明。用户问题仍原样发送；字段只是untrusted data，严格解释模式action_id:null，模型不获得工具、修复/动作权限，不自动复制或打开链接。每次对话checkbox与选择重置，不保存同意。报告32KiB和最终HTTP32KiB分别整拒，响应64KiB/解释4096、服务凭据/直连/超时/降级备份等0.52–0.55边界保持。

常规核心4432524通过；native应用/套件0warning/error仅编译。独立最终见TESTING；15新keys/45翻译、1566旧values保持，无新Prefs/schema/依赖/模型。G01依据明示有限范围转已实现（动作建议+当前Doctor解释+逐次选择标签）；更完整诊断仍F09，MCP仍G02，真实Windows/服务验收另计。固定52项22限定实现/20部分/8待做/2平台，普通28；不是工时/批数/整体验收百分比。真实WindowsGUI/选择行为/DPI/Narrator/凭据/HTTP/TLS/ARM64未执行，managed替身与交叉编译不是实机验收。

## 0.57已交付

2026-10-25。每批开始先实际pull：Already up to date c2b72a7/Mac1.12.6(34)，无新Mac改动。122Mac/112旧ZIP保留，无Mac源码/测试/构建/安装写入或运行。

既有整数PCM WAV降噪窗口新增明确Find Quiet Sample、Play Suggested Sample、Use Suggested Sample。以当前合法噪声样本最近帧长度（0.05–2秒；11025Hz/0.05秒为551帧）作为固定窗，遍历从0开始的不重叠完整窗，最后补一个对齐源结尾的完整窗，起点重复不再计；不是滑动全位置搜索。全部声道逐样本平方和使用精确UInt128（32bit样本平方<=2^62，最大窗总和<=2^82），同长度比较/精确tie选最早，反相声道不抵消。RMS只为归一化指标，不参与候选比较。源仍限512MiB/2小时、mono/stereo8/16/24/32bit、8–192kHz；固定4096帧缓冲<=32KiB，读取数据不超过源数据加一个样本窗，忽略原有metadata内容，沿用严格RIFF验证。

查找只在明确点击后执行，不改当前输入范围、不自动试听/采用/处理/保存/复制，不添加开关/Prefs/schema/依赖/模型。安静片段可能是静音、轻声或音乐，不能称纯噪声/语音分类或Mac算法完全等效。界面明确建议先试听再用；Use把精确frame/rate边界以R文本写回现有字段，由原手动Range校验，不缩短候选。当前样本/强度变更会清候选、取消准备及停止播放；新Find先清旧候选。Find/所有试听共用pending单飞和CTS；占流时禁用Find/Use/试听/Save，Stop可取消，真正任务结束才恢复。关闭/关闭功能/Quit仍由既有App finally等待FinishPreviewAsync，源/祖先租约贯穿Find/试听/导出，不与保存抢stream位置。

原生Fluent按钮/文本、滚动主体与固定footer，工作区/DPI适配；候选显示准确起止和检查窗数。手动样本仍完全可用，不采用候选时原有降噪输出字节保持。无麦克风/模型/网络/新文件；输出仍原件旁独占非覆盖，旧安全层未改。真实录音质量/长文件时间/播放/WPF/Narrator/多DPI/WindowsFS/ARM64未运行，不能用managed替身或编译当实机验收。

常规核心4433582通过；native应用/测试套件0警告错误仅交叉编译。8新keys/24翻译、1581旧values保持，独立最终结果见TESTING。D03仍部分：只有能量候选辅助，不是自动纯噪声识别，压缩/浮点/视频降噪与真实听感待做。固定52项22限定实现/20部分/8待做/2平台差异，普通仍28，不是剩余批数/工时/完整兼容百分比。

## 0.58已交付

2026-10-25。每批开始实际pull：Already up to date c2b72a7/Mac1.12.6(34)，无新Mac。122Mac/114旧ZIP保留，无Mac源码/测试/构建/安装写入或运行。

整数PCM WAV配乐选择后，采样率不同时先显示原生确认窗：两个完整来源路径、音乐原格式、目标主采样率、预计完整帧数/含头与pad字节，以及声音变化/削波/预算说明。取消不打开混音编辑器，不自动准备。明确Prepare Music后复用未改WaveConverter，完整准备为主采样率，保音乐原声道/位深；原Info始终描述原流，MusicInfo单独描述准备格式。匹配率路径不出现额外确认/转换，原默认输入/消息/输出保持。

WaveMusicRatePlan先校验来源数值/目标格式，按最近目标帧计算完整WAV（44/68字节头+数据+odd pad）并整拒>128MiB；未分配数组前预检。PreparedWaveMusic重新严格读原RIFF并核对确认时Info（rate/channels/bits/frames/dataOffset/dataBytes），精确长度byte[]+固定写流，无增长/ToArray副本；转换后实际Info/frames/length再次与计划核对，取消/错误不发布半准备对象。私有只读MemoryStream不暴露，只由ExportMix交原WaveMixer使用；显式拒primary/output别名。128MiB仅完整准备WAV，既有resampler表/工作缓冲额外最多约27MiB，不是整个进程内存峰值封顶。

主音频格式/时长保持，音乐时长按最近目标帧可能有微小变化；窗口化sinc滤波及原位深量化可能改变声音/削波，无dither/响度boost，编辑器显示准备率/帧/削波样本数。不是无损/保音调速度变化/节拍匹配。试听、循环（含交叉淡化）、活动压低及保存共同复用同一完整准备样本，试听仍PCM16前10输出秒；原手动转换与同率混音算法不改。音乐过大可明确先单独Convert WAV Format，压缩/浮点音乐和视频混音仍未开放。

原来源/祖先租约贯穿确认、准备、编辑、试听和保存；App准备await前后检查取消，feature-off/Quit不会迟到弹编辑器。既有pending/CTS单飞避免preview/save同时占流，App finally先join试听再释放准备内存和原input；准备等待Task真正结束后才Dispose，不新增临时文件、输出或网络。最终混音依旧第一来源旁独占非覆盖/owned staging清理，无新Feature/Prefs/schema/依赖/模型。

常规核心4433990通过；native应用/套件0警告错误仅交叉编译，独立最终见TESTING。11新keys/33翻译、1589旧values保持。D02限定整数WAV配乐扩展，不改变固定52项22限定实现/20部分/8待做/2平台差异、普通28。真实Windows/ARM64播放/音质/长文件性能/GUI/Narrator/DPI/文件系统尚未执行；managed替身与交叉编译不是实机验收。

## 0.59已交付

2026-10-25。本批先实际pull：Already up to date c2b72a7/Mac1.12.6(34)，无新Mac。122Mac/116旧ZIP保留，无Mac源码/测试/构建/安装写入或运行。

主窗和浮动File Shelf新增明确Preview File按钮。只选一份引用；未选中沿用“全部引用”规则，但多份会拒绝，不做批量预览读取。单图片委托原image.preview，非图片新稳定file.preview只提供DocumentText.Extensions（TXT/TEXT/MD/MARKDOWN/CSV/TSV/LOG/SRT/DOCX/ODT/RTF/HTML/HTM）及WAV，Quick Actions/My Wheel可明确运行。扩展名只是路由，真正读入仍由现有严格解析器验证；不声称支持PDF、DOC、MP3、float/compressed WAV等。Shelf刷新/选择/拖入不触发预览、识别、播放或外部启动。

文档复用DocumentTextFile（8MiB源/2M提取文字及原ZIP/XML/RTF/HTML闭集预算），原件租约到modal关闭；DocumentTextDialog新增previewOnly=false默认，普通Inspect五格式导出UI/统计/保存逻辑保持。true时控件树无Save/格式选择/导出区，只有明确Copy Statistics及Close；统计针对全部提取文字，展示最多前65536UTF16且不切代理对，截断提示不再提导出。Copy Statistics手动按钮复制既有统计report（含显示来源路径/格式限制），文字可按系统TextBox手动选择复制。不是Office/浏览器布局，不执行脚本/宏、取资源、打开链接或生成文件；DOCX/ODT/RTF/HTML隐藏/字段/布局等既有限制完整可见。

整数PCM WAV复用WaveDocument，原512MiB/2h、8–192kHz/单双声道8/16/24/32bit验证/完整波形，来源祖先固定。新Fluent原生预览仅完整格式/波形、Play Original/Stop/Close，无编辑/Save/自动播放。明确Play才准备前10源秒PCM16，pending/CTS单飞，关闭/取消停止播放并等待真正SoundPlayer.Load/准备结束后才Dispose；App finally await FinishPreviewAsync再释放来源。小窗/多DPI按工作区fit、正文滚动和固定footer，实际Windows播放/质量/焦点/Narrator/DPI/文件系统/ARM64未验收。

file.preview作为通用动作无新独立开关，直接QuickActions/MyWheel的关联面板关闭由owner取消；主窗普通Files操作不依赖Shelf开关。只有两个Shelf按钮通过PreviewShelfFileAsync携带shelfPreview来源，canonical动作解析后、首await前要求Shelf已开并workFeature=Shelf（图片委托同样绑定），所以从主窗架子发起也会在Shelf关闭时取消/关闭并join。不要笼统称任意无关开关都会取消通用预览。Owner关闭/Cancel Operation/Quit继续统一Busy/取消/等待。App只读分支无Save调用，即使modal返回true也不导出。

无新Feature/Prefs字段/schema/依赖/模型/网络或后台扫描。默认Prefs字节保持；只有显式把新file.preview加入MyWheel才持久化新ID，<=0.58会拒未知ID，可能默认并下次覆盖设置，降级须保留升级前备份。常规核心4434087通过/native应用套件0警告错误仅交叉编译，8新keys/24翻译、1600旧values保持，独立最终见TESTING。A04非图片预览增强但仍部分：目录/虚拟云引用、其他格式/系统QuickLook与非激活焦点待做，不能称平台不兼容。固定52项22限定实现/20部分/8待做/2平台，普通28不变。

## 0.60已交付

2026-10-25。本批先实际pull：Already up to date c2b72a7/Mac1.12.6(34)，无新Mac。122Mac/118旧ZIP保留，无Mac源码/测试/构建/安装写入或运行。

PC Doctor默认关闭，新增Inspect EXE Structure…和稳定doctor.executable。动作自己明确打开单本地EXE选择器，忽略无关当前files；workFeature=Doctor在选择器/首await前绑定，选择器返回和每次加载后检查取消，停止后不迟到读入/弹报告。NativeFiles.Input及祖先从严格来源检查一直持有到只读报告关闭，失败/取消释放。原AppLaunch PE启动验证不改，新工具绝不加载/运行/打开程序或修改权限/设置，不用Shell/新PInvoke/证书存储/网络/模型/依赖。

原创BCL流PE32/PE32+头部读取，标准字段依据[微软PE格式](https://learn.microsoft.com/en-us/windows/win32/debug/pe-format)，未复制第三方解析器代码。只读Machine/Subsystem/版本/Characteristics/sectioncount和CLR目录三态声明，不跟CLR RVA、不解运行时/AnyCPU/混合镜像/仿真/依赖/manifest/校验和/SmartScreen或Windows加载器接受性，不能据此说本机能运行。未知machine保留原ushort值/hex，不当平台不兼容。

策略来源<=512MiB、PE头偏移64..1MiB、optional<=4096、sections<=96；按实际optional长度核NumberOfRvaAndSizes，不静默截断。所有偏移/加法用long，头/SizeOfHeaders/section原始数据范围越界或不一致拒本有限预览，不等于完整加载器判决。单次缓冲<=4096、成功读入的主数据流头部字节总量最多8536（64+24+4096+3840+64×8），不是ReadExactly短读时反复传入的请求count之和，更不是物理磁盘读取量；DOS stub、section正文不读。EOF/I-O失败及取消不发布半报告，blocked选择器/系统读取可能返回后才完成取消。

Certificate目录index4按文件偏移而非RVA，只核目录边界/8字节对齐/不与image header或raw sections重叠，再遍历最多64个8字节WIN_CERTIFICATE头。每条原offset/length/revision/type完整保留，下一条按length向上8对齐；不读证书正文或证书padding，未知revision/type不丢。64条正好终点为完整覆盖，仍余声明范围才RecordLimit。未声明、矛盾、越界、重叠、记录上限分别显示，坏证书声明仍保独立PE头事实/明确partial；“头长度覆盖表”仅结构声明，不是签名验证。PKCS#7 type2头不代表合法签名，缺表不代表未签名（未检查外部catalog）；没有哈希/证书链/信任/吊销/恶意软件判断。

Fluent原生虚拟ListView/GridView显示事实/最多64证书头原数值、只读路径，正文滚动/固定Copy Report及Close/工作区DPI适配，无Run/Fix/Open/Save按钮。仅明确Copy写剪贴板，报告含显示路径、全部已记录事实/证书头（带列名）和完整限制，不自动保存或发送，也不接助手上传。当前字段是持有来源时的记录，不是释放后文件当前状态或安全证明。真实Windows文件系统/界面/主题/Narrator/多DPI/ARM64未运行。

常规核心4434529通过/native应用套件0警告错误仅交叉编译；45新keys/135翻译、1608旧values保持，独立最终见TESTING。纯核心可在Mac只读解析SDK生成的旧x64/ARM64 PE文件，不是运行Windows程序或Mac app。无新Feature/Prefs字段/schema/默认开关；新ID只有明确存MyWheel才持久化，<=0.59未知动作可能整设置拒/default/下次覆盖，降级保留升级前备份。F09仍部分，真实签名信任/打不开应用原因、运行时/依赖/修复及其他深诊断待做；固定52项22限定实现/20部分/8待做/2平台、普通28不变。

## 0.61已交付

2026-10-25。本批开始实际pull Already up to date c2b72a7/Mac1.12.6(34)，无新Mac。122项Mac源码/构建/测试基线和120个0.1–0.60旧ZIP保持，无Mac写入/执行；仅Windows实现和相关文档。

默认关闭QR & Barcodes新增Read Flat QR Image…/稳定qr.readflat，自己明确选择一个本地普通图片，忽略其他当前选择。workFeature=QrCodes在选择器/首await前绑定；选择器返回和STA读取后检查取消，统一Busy/ownerclose/feature-off/Quit，等待真正完成再释放来源。复用ImageTools单帧SDR/128MB/16MP与NativeFiles.Input祖先lease，全尺寸规范化BGRA保持到只读结果窗口关闭；失败/取消释放。系统图片解码或原生选择器可能返回后才完成取消，不承诺即时中断。无相机/屏幕/剪贴板自动读取/后台扫描/模型/网络。

原创受限平面QR读取：正向（不含90度旋转）、正方形、完整精确四模块白色quietzone、每模块统一整数像素，所有像素alpha255/RGB各<=63为黑或各>=192为白，中间色/模块内不一致整张拒绝。复用原encoder标准表/format-version位事实/有限域余数，原Encode/Raster函数未改；QrCode仅加partial声明。候选版本最多40、原图最多16MP，先探finder角再逐像素核每候选；最多40次候选扫描是上限，不是实时性能保证。输出保留完整text/version/L-M-Q-H/mask/整数scale。

v1–40/L/M/Q/H函数图形（finder/隔离/timing/alignment/fixeddark）、两份format BCH和v7+两份version BCH全部精确匹配，不作BCH修复；unmask/之字遍历、remainder bits必须0，按短长block还原交织，所有RS parity精确比较，不进行任何损坏修复。数字/字母数字可多段，Byte模式支持明确UTF8 ECI26或无ECI纯ASCII，UTF8每Byte段独立严格解码；ECI其他值、未声明高位字节、跨段拆分UTF8字符、Kanji/Hanzi/FNC1/structuredappend等拒绝，不猜编码/丢数据/静默转码。闭集terminator/0对齐/交替EC11pad完整核对。文本最多8000 UTF16（标准最大numeric7089）；其他有效但不在此窄范围的码仍需外部读取工具，不标为平台无法兼容。

原生Fluent窗口只读来源/完整显示文字/版本级别scale长度，控制/格式/bidi/行段分隔字符显示[U+XXXX]，原文也可能含该字面标记，UI明确说明。只有明确Copy Decoded Text才复制原始全部字符（包括隐藏字符）；无自动open URL/run/save/library/send，不判来源可信，不把QR校验当身份认证。正文滚动、固定Copy/Close、工作区/DPI适配；原Windows图片编码器/QR生成/库/花园和旧输出保持。真实Windows/WIC/主题/剪贴板/Narrator/DPI/性能/ARM64仍未执行。拍照透视/旋转识别/边框容忍/缩放抗锯齿/颜色/透明/噪声/损坏修复/其他条码、更多编码与QR反向迁移仍待做。

4434908常规核心通过，应用/原生套件0警告0错误仅交叉编译；独立agent结果见TESTING。13新keys/39翻译，1653旧key-values不改，无Feature/Prefs字段/schema/第三方依赖变化。新动作只有明确存MyWheel才持久化，<=0.60可能拒未知ID/整设置回默认并下次覆盖，降级保留升级前设置备份。E05由待实现转部分；52项22限定实现/21部分/7待做/2平台，普通剩余28不变，非完整兼容或工时百分比。

## 0.62已交付

2026-10-25。按用户新增要求优先将Mac Doctor适配为Windows Doctor。本批先实际pull Already up to date c2b72a7/Mac1.12.6(34)，无新Mac；只读对照DoctorModel.swift/DoctorPane.swift的13症状分区，不复制Mac框架实现/缓存路径或第三方清理规则。122项Mac基线/122个0.1–0.61旧ZIP保留，未写入或运行Mac源码/测试/构建/安装。

现有PC Doctor用户显示统一为Windows Doctor（简中Windows医生/繁中Windows醫生/日语Windows診断）：侧栏、设置开关、基本报告标题、禁用提示、兼容条目和助手页面介绍。Feature.Doctor枚举/Doctor设置键/注册表位置、11个既有Catalog action IDs、PCDoctor后台类名及旧AssistantPlan请求指令字节保持，旧配置无需迁移。助手协议中的历史PC Doctor内部名称保留，不改变候选/网络正文；非当前界面品牌。1666旧翻译键值保留，新38keys/114翻译。新增通用分区标题翻译也会让中文/日语旧报告里此前未翻译的同名标题本地化；字段数值、私隐筛选及助手正文保持。

新增原创静态WindowsDoctor.Areas与原生Expander工作台：Summary/Storage/Caches/Folder Sizes/Duplicate & Similar Files/Project Builds/Slow or Hot/Apps That Will Not Open/Uninstall Apps/External Drives/Network/Background Items/Battery & Sleep，顺序对照Mac13分区，使用Windows说明/状态。所有分区明确显示Available with Limits或Not Implemented Yet，不靠颜色表意；仅Summary初始展开，可逐区键盘展开和滚动。展开/浏览不读系统或文件、不启动检测、不更改偏好。缓存和外接盘当前无动作/明确未实现；卸载分区仅安装记录，按钮名称不冒充卸载。其他分区逐一标出保真/数量/诊断限制：逻辑非可释放字节、哈希非删除建议、marker非配置、working-set非所有权/泄漏/热原因、PE/证书头非能运行或签名信任、Run记录非启用/可信、proxy配置非连通、充电非电池健康/睡眠阻塞。

按钮复用已有11个canonical Doctor动作；Summary/Network/Power另复用原Home-only临时doctor.check，不把它加入Catalog/MyWheel/助手候选或持久化，以保持原G01协议/配置。独立agent预审发现P1：最初对Home-only doctor.check作Catalog.Single会在主窗口构建时抛异常；已用WindowsDoctor.ResolveAction显式闭集分支修复，其他11动作仍canonical Doctor过滤。核心/真实源managed UI均须覆盖默认关闭时主窗所有分区构建、13区展开、每按钮路由、临时入口不扩候选，最终复核见TESTING。

Doctor默认关闭；关闭时分区/缺项仍可读，动作和原有固定Windows设置按钮均禁用。启用仅解锁明确点击，没有自动全机检查或健康分数。点击仍进入既有RunActionAsync统一Busy/ownerclose/feature-off/Quit、原选择器/内容比较确认、源祖先持有和取消真join；无新reader/PInvoke/外部进程/管理员权限/网络/清理/卸载/eject/删除/undo。可选助手仍独立开关、完整正文预览/明确Send/每次标签选择，无自动执行。打开设置只跳既有四个闭集页面，不改变设置。

4434979常规核心通过；应用/原生套件0警告错误仅交叉编译，独立agent最终见TESTING。Windows真实主题/焦点/Narrator/高对比/DPI/键盘/文件系统/ARM64未运行。原Windows读取器、文件保护、QR/图片/文档/音频算法、所有Catalog IDs/Feature/Prefs schema/默认开关/第三方依赖不变；不声称Windows Doctor已经具备全部Mac能力。F09仍部分，固定52项22限定实现/21部分/7待做/2平台，普通28不变。后续继续外接盘/深诊断/清理及可靠撤销等；不可把未实现写成平台无法兼容。

## 0.63已交付

2026-10-25。继续用户指定的Windows Doctor适配。开始实际pull Already up to date c2b72a7/Mac1.12.6(34)，无新Mac；122项Mac基线和124个0.1–0.62旧ZIP保持，无Mac源码/测试/构建/安装写入或运行。

默认关闭Doctor新增稳定doctor.drives/Inspect Mounted Drives…，忽略无关files，明确点击后才读取。WindowsDoctor的External Drives分区由planned转Available with Limits，说明只是已挂载盘符，不冒充外接USB身份/未挂载设备或安全弹出。共有12个canonical Doctor动作及原Home-only doctor.check；13导航ID/Feature.Doctor/原设置键/registry/schema保持，浏览工作台无I-O。

六个System32限定kernel32导入：[GetLogicalDrives](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getlogicaldrives)、[GetDriveTypeW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getdrivetypew)、[GetVolumeInformationW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getvolumeinformationw)、[GetDiskFreeSpaceExW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getdiskfreespaceexw)、GetThreadErrorMode、[SetThreadErrorMode](https://learn.microsoft.com/en-us/windows/win32/api/errhandlingapi/nf-errhandlingapi-setthreaderrormode)。原生声明BOOL32/DWORD32/三个ULONG64/Unicode/LastError；不引入依赖、WMI、shell、管理员权限或设备IOCTL。

最多26个A–Z根字符串，mask高位仅标unknown/partial。按先前报告的type门控：2removable/3fixed/5CD-DVD/6RAM才读文件系统和容量，4network/0unknown/1no-root/未来unknown值保留原uint类型并跳过这两次查询。GetVolumeInformation只给65UTF16缓冲（最多64内容）/flags，volume label/serial/max-component指针全null；不请求目录条目/文件正文/卷名/序列号。FILE_READ_ONLY_VOLUME 0x80000为声明true/false/unknown，不测试写入，false不证明有写权限。GetDiskFreeSpaceEx三个ulong不改为signed/double：available-to-caller、total-for-caller均可能受quota影响，volume-total-free另列，不据此算物理盘总容量、USB身份或可释放空间。未知值/API失败不填0；volume和space失败各保留即时Win32 uint原error，UI/复制分别加Filesystem Query/Capacity Query阶段。

这是多次历史系统调用，不持有物理设备身份，也不是一致原子快照。盘符可在type与后续调用间重指向；只能承诺“当时报告network类型则不调用FS/容量”，不能承诺系统绝无SMB/远程provider元数据访问。也不识别folder mount points/未挂载设备/USB/外接硬件；媒体可能被唤醒，系统调用可能阻塞且取消后才返回。没有主动HTTP/DNS/测速请求或文件读取扫描。

专用非线程池后台Thread，先取原thread-error-mode，再Set旧mode|SEM_FAILCRITICALERRORS，避免空光驱/可移动介质插盘错误对话框；setup失败立即完整标不可用，不继续查询。所有native调用均在同一同步worker，try/finally恢复返回的previous，不改变进程全局或用户设置，也不污染UI/线程池。恢复失败保留独立partial错误，专用线程结束；枚举返回0仍是失败（即使GetLastError0），不是“无磁盘”结论。取消在阶段前后检查，finally恢复完成之后Read结束/TCS才完成；没有用超时放弃真实native调用。App workFeature绑定首await前，await真实ReadAsync并再查token，feature-off/ownerclose/Quit不弹迟到窗口；等待的是读取/清理完成，不宣称使用Thread.Join或可强制中断设备驱动。

原生Fluent虚拟ListView/GridView完整盘符/type原code/FS/readonly/caller容量/volume free/rawflags/分阶段issue；选择详情显示全部列，正文滚动、固定CopyReport/Close与工作区DPI。无eject/write/fix/open/format/Save。复制明确点击才含全部记录/采样时间/错误/限制；无记录也显示并复制不能证明没盘的提示。没有自动持久化、剪贴板读取/写入、助手上传、修复或清理；只是explicitCopy写剪贴板。

4435097常规核心通过/native应用与套件0警告错误仅交叉编译，独立最终见TESTING。37newkeys/111翻译、1704旧values保持；原reader/NativeFiles/文件处理/助手协议schema不改，新动作会在Doctor启用时成为原助手可建议候选，仍手动Return，不等同旧候选正文完全相同。MyWheel可明确存新ID，<=0.62可能未知ID整Prefs拒/default/下次覆盖，降级留升级前备份。F09仍部分，固定52项22限定实现/21部分/7待做/2平台普通28不变。Windows真实API/媒体阻塞弹窗/热插拔/FS权限/GUI/Narrator/DPI/ARM64仍未执行，managed替身和编译不替代实机验收；未挂载/USB身份/eject/写权限/电池睡眠/清理undo等继续待做。

## 0.64已交付

2026-10-25。继续用户指定的Windows Doctor适配，开始实际pull Already up to date c2b72a7/Mac1.12.6(34)，无新Mac。122项Mac基线和126个0.1–0.63旧ZIP保持，无Mac源码/测试/构建/安装写入或执行。

默认关闭Doctor新增稳定doctor.power/Inspect Power & Sleep Settings…，原Battery & Sleep分区保留Home-only基本快照并加入专用检查，13canonical Doctor动作+原临时doctor.check，不改变13导航ID/Feature.Doctor/Prefs字段/schema/注册表/defaultoff。无关files不读取；浏览分区无I-O，只明确点击后做固定数量的本机只读调用。workFeature在首await前绑定，Task.Run真实Read与资源清理完成后再查token，取消/feature-off/ownerclose/Quit不弹迟到报告。没有自动采样/定时器/持久化或后台网络。

系统状态使用[GetSystemPowerStatus](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getsystempowerstatus)和[12字节SYSTEM_POWER_STATUS](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-system_power_status)：四byte+两个DWORD，保留AC原code、BatteryFlags/Percentage原byte、BatterySaver原code与两项uint秒。0xff flag/percent未知、0x80无系统电池优先、AC255未知；寿命0xffffffff仅未知/接电，不展示成42亿秒。其他原值不静默清0；BatterySaver仅0Off/1On/其他Unknown+rawcode，不冒充完整Windows电源模式或能耗分析。续航是系统估计，不能算电池磨损/健康/保证续航，当前无battery serial/name/容量设计值采集。

电源计划使用[PowerGetActiveScheme](https://learn.microsoft.com/en-us/windows/win32/api/powersetting/nf-powersetting-powergetactivescheme)一次，RootPowerKey为NULL；out指针复制16字节GUID后finally [LocalFree](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-localfree)，非空失败/取消路径也释放，null-success明确unavailable而非虚构默认GUID，LocalFree失败另保留原错误partial，不重试或假装释放成功。后续API只用复制的Guid值，不再借用已释放指针。各阶段取消先等真实调用/清理，无超时抛弃native工作。

对一个捕获的scheme GUID固定查询DisplayOff/Sleep/Hibernate的AC/DC六值，使用[PowerReadACValueIndex](https://learn.microsoft.com/en-us/windows/win32/api/powrprof/nf-powrprof-powerreadacvalueindex)/[DC](https://learn.microsoft.com/en-us/windows/win32/api/powrprof/nf-powrprof-powerreaddcvalueindex)。三对subgroup/setting GUID依据微软公开定义，只有闭集这些字段，不读计划名称或枚举任意registry。powrprof的DWORD返回值本身是错误码，不能读旧LastError；每个AC/DC独立保留成功uint值或原错误，不把不支持填0。五个kernel32/powrprof导入均限定System32、BOOL32/DWORD32/GUID16/native指针，默认64位平台形状不缩为32位，无新依赖、shell、powercfg、WMI、elevation、设置写入或睡眠请求。

具体设置含义分别核对[熄屏](https://learn.microsoft.com/en-us/windows-hardware/customize/power-settings/display-settings-display-idle-timeout)、[睡眠](https://learn.microsoft.com/en-us/windows-hardware/customize/power-settings/sleep-settings-sleep-idle-timeout)、[休眠](https://learn.microsoft.com/en-us/windows-hardware/customize/power-settings/sleep-settings-hibernate-idle-timeout)官方资料：单位是秒，前两者0是在该保存计划里没有idle timeout；**休眠0是adaptive hibernate threshold，不是Never**。计划uint.MaxValue仍是合法保存整数，不作电池寿命的unknown哨兵。标签明确configured/plugged-in/battery，与SystemPowerStatus未知电池秒分开。多次调用非一致原子状态，捕获后活动计划/该计划值可以变化，GroupPolicy/overlay/adaptive/ModernStandby或不支持的sleep state可改变实际生效行为；本工具不判断当前机器一定会睡眠/休眠或发生过什么。

Fluent原生虚拟字段列表，完整原code/flags/GUID/AC-DC计划值/分项error、只CopyReport/Close、滚动/固定footer/工作区DPI。复制明确点击才包括全部采样事实/时间/限制，不自动打开设置、改变计划、睡眠/休眠、保存文件、使用助手或网络。其他医生入口/固定设置页面/保护机制保持。

4435157常规核心通过/native应用与套件0警告错误仅交叉编译，独立最终见TESTING。25新keys/75翻译、1741旧values保持；旧文件/图片/音频/文档/QR/助手协议schema不变。新动作可加入启用Doctor的建议候选，仍手动Return/不自动发送；新MyWheel ID <=0.63可能导致未知ID整Prefs拒/default/下次覆盖，降级保存升级前备份。F09仍部分，固定52项22限定实现/21部分/7待做/2平台普通28不变。真实Windows API/实际策略/ModernStandby/电池质量/资源释放/GUI/焦点/Narrator/DPI/ARM64未运行；电池健康、sleep blockers、supported states、睡眠历史/故障原因与修复继续待做，不冒充完整Mac Doctor。

## 0.65已交付

2026-10-25。实际pull Already up to date c2b72a7/Mac1.12.6(34)，无新Mac。122项Mac文件和128个0.1–0.64旧ZIP保持，无Mac源码/测试/构建/安装写入或执行。

默认关闭Doctor新增稳定doctor.caches / Inspect User Cache Locations…，第13个尚planned的Caches分区加入限定只读入口。现有13个Mac同序症状区域均有有限只读入口；14个canonical Doctor动作+原Home-only doctor.check。这不代表13类问题全部解决或Windows移植完成；固定52项22限定实现/21部分/7待做/2平台，普通剩余28，F09仍部分。

只在明确点击后解析两个当前用户位置：[Path.GetTempPath](https://learn.microsoft.com/en-us/dotnet/api/system.io.path.gettemppath?view=net-10.0)报告的Temporary Folder（可能受环境变量重定向，不校验存在/权限），和[Environment.SpecialFolder.InternetCache](https://learn.microsoft.com/en-us/dotnet/api/system.environment.specialfolder?view=net-10.0)旧式WinINET缓存，使用[DoNotVerify](https://learn.microsoft.com/en-us/dotnet/api/system.environment.specialfolderoption?view=net-10.0)，不Create。逐项预期IO/权限/安全/不支持/非法环境路径错误分别显示不可用，不使另一位置消失。不是所有现代浏览器或所有app缓存清单，不判断文件可安全删除。

只去一个结尾反斜杠再走原WindowsPathPolicy（240字符、普通盘符本地路径）；整个盘根、UNC、重复尾分隔、dot/ADS/device/长路径/控制字符都拒绝；另对这两个位置拒绝无效UTF16与Unicode Format字符，含补充平面，不扩大共享路径规则。词法通过仅表示可尝试，不表示可访问或可信。预览完整可尝试路径；被拒绝路径最多512原UTF16单位、保留surrogate边界，控制/格式/行段分隔/非法surrogate可见[U+XXXX]而不是执行排版，普通emoji保留。

Fluent两个RadioButton，无默认选择，Measure初始禁用，只有用户选中可尝试项才继续。浏览Doctor/展开分区不扫描；打开位置选择框不枚举子项。确认后使用原NativeFiles.MetadataDirectory与FolderSizes：root+4层/20K条目/~30秒soft，持有根与祖先，system/reparse/cloud/不完整/拒绝项保留原限制，零输出不冒充空目录。旧WinINET目录若带system属性仍拒绝，不削弱NativeFiles。只读目录项元数据，不开子文件内容，不写文件/属性/权限，不清理或移到回收站。系统提供者可能重定向，native调用可能阻塞，soft时间不是强制超时。只报告逻辑历史字节；hardlink可重复，ADS/物理/可回收空间不包含。临时内容可能正在使用或是工作文件，不能从目录名称推断删除建议。

复用FolderMapDialog，新增可选上下文仅缓存分支启用，报告清楚标记来源位置和上述限制。旧null上下文的文件夹地图输出保留；缓存地图仅明确复制当前视图报告，无自动复制/保存/网络/助手上传。测量后句柄清理完才显示历史结果，不重新扫描。新缓存dialog及地图工作区/DPI适配，真实Windows未验收。

独立agent确认并复现P2：目录打开阻塞期间取消后，随后UserError/IOException/Win32Exception仍会落入真实App外层ShowError而弹迟到错误。缓存Scan在using清理完成后优先取消；同时仅对15个只读Doctor读取await加入AwaitDoctorReadAsync，真实等待原任务与清理，只有已经取消且任务失败才转OperationCanceledException；未取消时保留原异常。成功结果交回调用者，EXE的using先接管资源再tokencheck，不丢弃/泄漏成功返回的lease。Task.Run和STA机制保持；folder/basic增加postawaitcheck。全局写入错误策略不变，不吞写文件/清理失败。feature-off/owner-close/Quit取消不弹迟到结果或错误，非取消真实错误仍显示。任务阻塞时仍等待，绝无超时抛弃native工作。

4435209常规核心通过，native应用与套件0警告错误仅交叉编译，agent最终见TESTING。15新keys/45翻译、1766旧values保持；旧图片/音频/文档/QR/助手协议、Feature.Doctor/Prefs/schema/注册表/默认关闭/依赖不变。新启用Doctor动作可作手动Return候选；新MyWheel ID <=0.64可能使旧Prefs未知ID拒绝/default/覆盖，降级保留升级前备份。实际Windows目录环境/ACL/WinINET系统属性/锁定/阻塞/句柄释放/Clipboard/WPF/焦点/Narrator/DPI/ARM64未执行。缓存清理/RecycleBin安全撤销/卸载/修复继续待做，不把未做写作平台不兼容。

## 0.66已交付

2026-10-25。每批实际pull Already up to date c2b72a7/Mac1.12.6(34)，无新Mac。122项Mac文件和130个0.1–0.65旧ZIP保持，无Mac源码/测试/构建/安装写入或执行。

Windows Doctor Battery & Sleep分区新增稳定doctor.sleepstates / Inspect Sleep Capabilities…，独立明确点击且Doctor仍默认关闭。原doctor.power读取/模型/报告不变；现15canonical Doctor动作+Home-only doctor.check，13区域均有限只读入口，绝非完整Mac Doctor。无新开关/Prefs字段/schema/注册表/依赖。只读网络、地址或图片/文件选择不参与本检查，不读取调用传来的无关files；浏览/展开不触发任何检查。

一次[GetPwrCapabilities](https://learn.microsoft.com/en-us/windows/win32/api/powerbase/nf-powerbase-getpwrcapabilities)只读调用，System32限定powrprof.dll/ExactSpelling/SetLastError，返回BOOLEAN按U1而非四字节BOOL。失败保留原uint LastError，包括0，只有不可用行、不默认填12个False。成功保留12个原byte，非零（包括2/255）显示报告Yes，0显示No；每项显示原值，不推当前有效行为。HiberFileType额外保留byte0无/1Reduced/2Full/其他Unknown+raw；不因文件存在/类型就判断完整休眠可用、快速启动已开或建议修复。

[微软结构说明](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-system_power_capabilities)含未预处理旧版条件，不能照显示把所有spare同时拼入。已核对[Microsoft Win32 metadata官方winnt.h](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/winnt.h) Windows10+ NTDDI_WINXP/NTDDI_WINTHRESHOLD分支：完整SYSTEM_POWER_CAPABILITIES为76字节，所有BOOLEAN单byte。只使用PowerButton/SleepButton/Lid在0/1/2、S1–S5在3–7、HiberFile8、FastSystemS4在17、WakeAlarm19、AoAc20及HiberFileType22；结构Explicit Size76，偏移72字节处保留未解释的DWORD字段以镜像原4字节对齐；没暴露的reserved/BatteryScale/wake-state字段仍保留完整输出空间，避免native写越界。BatteryScale位32、尾五32bit枚举56–72，x64/ARM64该结构无指针。不是沿用Learn旧NTDDI分支或截短到23字节。仅必要互操作声明/标准事实，未引入第三方运行包。

展示Windows报告的S1/S2/S3睡眠、S4休眠、S5soft-off（不是睡眠）、S0低功耗空闲，以及按钮/盖子存在、混合睡眠和唤醒闹钟支持。不读取/推断温度、电池磨损/序列号、阻塞进程、历史或powercfg /a排除理由；不从多flag自行合成“能睡眠/不能睡眠”的结论。硬件/驱动/固件/策略/配置可以改变当前可用性，采集是历史单次结果，不保证立即进入/唤醒状态，也不与另一电源快照拼成原子状态。

workFeature在首await前绑定，前后tokencheck，使用0.65只读Doctor AwaitDoctorReadAsync，真实Task.Run/native完成才响应取消，不丢后台工作，不弹迟到结果/错误，未取消真实错误仍显示。新native调用没有资源分配/需要释放的pointer。Fluent原生虚拟表、完整时间/raw值/错误/限制，固定CopyReport/Close，工作区/DPI边界；只明确Copy才写剪贴板，无自动复制/持久化/助手上传。无PowerSet、文件写入、睡眠/休眠/重启/关机、shell/powercfg/WMI/elevation或网络请求。

4435778常规核心检查通过，native应用/套件0警告错误仅交叉编译，agent最终见TESTING。27新keys/81翻译、1781旧values保持；原power报告不改，唯一powerarea描述新增，旧动作IDs/设置字段保持。新Doctor建议候选只手动Return；新MyWheel ID <=0.65可能使旧Prefs未知ID拒/default/覆盖，降级保留升级前备份。固定52项22限定实现/21部分/7待做/2平台，普通剩余28不变；F09仍部分，sleep blockers/history/battery health、缓存清理、卸载、可靠撤销和修复继续待做。Windows实际API/固件驱动ModernStandby/false或不一致声明/失败取消/GUI/clipboard/焦点/Narrator/DPI/ARM64执行未完成；托管替身与交叉编译不是实机验收。

## 0.67已交付

2026-10-25。每批实际pull Already up to date c2b72a7/Mac1.12.6(34)，无新Mac。122项Mac文件和132个0.1–0.66旧ZIP保持，无Mac源码/测试/构建/安装写入或执行。

Windows Doctor Background Items新增稳定doctor.services / Inspect Windows Services…，显式单次只读本机Win32服务状态，Doctor仍默认关闭。原Run/RunOnce读取/模型/窗口，以及power/sleep能力检查不改。现16canonical Doctor动作+Home-only doctor.check，13区域均有限只读入口；并非完整Mac诊断。Feature.Doctor/Prefs字段/schema/注册表/依赖不变，浏览分区无读取；无关files不访问，启用Doctor建议候选仍手动Return。

三System32限定advapi32声明。[OpenSCManagerW](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-openscmanagerw) machine/database均NULL，只请求SC_MANAGER_ENUMERATE_SERVICE(4)，API隐含CONNECT，不用ALL_ACCESS或管理员重试。[EnumServicesStatusExW](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-enumservicesstatusexw) info0、SERVICE_WIN32(0x30)、SERVICE_STATE_ALL(3)、groupNULL、resume初始0，仅一次固定256KiB缓冲调用，不自动续扫/探测/轮询。只有成功或ERROR_MORE_DATA234才解析returned记录；234保留partial/neededbytes，其他失败（LastError0也一样）完全忽略native stale outputs，明确不可用，不填完整空清单。权限缺少SERVICE_QUERY_STATUS时系统会默默省略服务，因此成功或0行也不证明清单完整。驱动服务、配置/命令行/账户/启动类型/二进制路径不查询，native只本机SCM/可能本机IPC，不主动远程连接/网络测试。

[ENUM_SERVICE_STATUS_PROCESSW](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/ns-winsvc-enum_service_status_processw)为两个指针加[SERVICE_STATUS_PROCESS](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/ns-winsvc-service_status_process)九DWORD：x64/ARM64指针0/8，Type16/State20/PID44/Flags48，总56字节（含对齐）。先检查完整reportedcount*stride不超过256KiB；物理不可能的count单独InvalidRecord+原count，合法缓冲却超过本地4096行预算单独RecordLimit+原count，均不越界读/截短冒充完整。随后每个string指针须在完整header数组之后、缓冲内、2字节对齐，并在剩余区和最多1024UTF16+NUL内找到终止符；无无界PtrToStringUni。官方名字通常限256字符，本实现1024是防御读取预算，不称微软最大。空key/无效pointer/越界/未终止记录index跳过可见；空display允许，不捏造同名。OrdinalIgnoreCase重复key显式skip，不能静默合并信任/归属。

总复制name+display不超过262144 UTF16单位，超限逐index提示；模型4096行/4100issues、完整防御复制、稳定排序、闭集issuephase，未知state/type/PID/error均保留uint。只展示Name/Display/rawType/rawState/rawPID，不暴露未解释的接受控制、退出码、checkpoint等字段。State1–7分别Stopped/StartPending/StopPending/Running/ContinuePending/PausePending/Paused，其他Unknown+raw。停止不证明禁用、自启动类型或性能问题，PID可复用，不证明有效进程身份或app/helper归属；不打开进程/路径或推断谁可安全结束。

原生HGlobal固定zero初始化，finally释放；嵌套finally确保CloseServiceHandle即使前段清理失败仍被尝试，[CloseServiceHandle](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-closeservicehandle)失败保留原error为partial，不假装关闭成功/反复关闭。所有pointer仅在缓冲有效期间读并复制到有界托管字符串，报告不持有nativepointer。token在native阶段前后检查，workFeature首await前绑定，使用只读Doctor AwaitDoctorReadAsync真实等待native/cleanup，无超时抛弃；取消/feature-off/owner-close/Quit不弹迟到结果或错误，未取消真实异常仍显示。native调用可能阻塞，不能声称固定超时退出。

Fluent虚拟Services/Issues双tab，原state/type/PID、完整安全可见name与全部issues/capturedtime/limits进入明确CopyReport；名字可能包含私人软件，只有明确copy才写剪贴板，未接入助手发送/持久化。Controls/Cf/行段分隔/无效surrogate以原[U+XXXX]可见，正常emoji/文字保留，不让名称改报告行或方向。固定Copy/Close、工作区/DPI适配。无OpenService/StartService/ControlService/DeleteService/ChangeServiceConfig或服务启停/禁用/卸载、文件扫描、命令/shell/WMI/elevation/设置改变/远程机器参数。权限不足提示不能靠elevate兜底。

4435874常规核心通过，native应用与套件0警告错误仅交叉编译，agent最终见TESTING。34新keys/102翻译、1808旧values保持；新MyWheel ID <=0.66可能使旧Prefs未知ID拒/default/覆盖，降级保留升级前备份。F09仍部分，固定52项22限定实现/21部分/7待做/2平台普通28不变。真实WindowsSCM权限/服务实例/字符串及布局/cleanup/失败取消/GUI/Clipboard/Narrator/焦点/DPI/ARM64未运行；托管替身和编译不是实机验收。StartupApproved/启动文件夹/计划任务、服务配置与启停/安全禁用/清理撤销，以及卸载/深诊断继续待做，不把未做写成Windows不兼容。

## 0.68已交付

2026-10-25。实际pull Already up to date c2b72a7/Mac1.12.6(34)，无新Mac。122项Mac文件和134个0.1–0.67旧ZIP保持，无Mac源码/测试/构建/安装写入或执行。

默认关闭Doctor新增稳定doctor.commonfolders / Measure Common Folders…，Storage与Folder Sizes共用，17canonical Doctor动作+Home-only doctor.check/13有限只读区域。先预览Desktop、Downloads、Documents、Pictures、Music、Videos六位置；初始无勾选/Measure禁用，不可用项禁用且原因可见。明确选1–6后复制选择数组，才开始分根读取。Feature/Prefs字段/schema/注册表/依赖不变，无关files不访问，浏览区域不扫描。

五位置使用[Environment.GetFolderPath](https://learn.microsoft.com/en-us/dotnet/api/system.environment.getfolderpath?view=net-10.0)/DoNotVerify、不Create；预期解析错误逐位置保留，其余继续。Downloads没有BCL SpecialFolder枚举，窄[SHGetKnownFolderPath](https://learn.microsoft.com/en-us/windows/win32/api/shlobj_core/nf-shlobj_core-shgetknownfolderpath)使用固定[Downloads GUID](https://learn.microsoft.com/en-us/windows/win32/shell/knownfolderid)374DE290-123F-4565-9164-39C4925E467B、[DONT_VERIFY](https://learn.microsoft.com/en-us/windows/win32/api/shlobj_core/ne-shlobj_core-known_folder_flag)0x4000/current-user tokenNULL，不CREATE/INIT/DEFAULT_PATH，NO_ALIAS对Path无效故不加。不猜UserProfile/Downloads，不fallback到其他目录；重定向可能涉及网络/云，DONT_VERIFY不证明存在/权限，不承诺OS提供者零网络活动，但本工具不发网络测试/远程扫描。

按[Known Folders](https://learn.microsoft.com/en-us/windows/win32/shell/known-folders)要求，路径读取使用已有OnSta真实线程，Downloads先CoInitializeEx(NULL,2)且立即记录initialized=HRESULT>=0，再查token；S_OK/S_FALSE都配对Uninitialize，ChangedMode等失败不查SH/不误Uninit。只有SH HRESULT==0才读pointer，非0保原uintHRESULT/不可用，不读失败pointer；成功nullpointer单独无路径。所有非null pointer成功/失败/取消都finally CoTaskMemFree，嵌套finally确保成功初始化后CoUninitialize仍被尝试。四导入System32/ExactSpelling，GUID16/nativepointer/HRESULT32，不用LastError。依OS保证已分配NUL字符串，只读至多513个UTF16单位；超限显式Truncated且Path强制null，不能把截短前缀作有效路径。此策略限制复制，不证明任意失效nativepointer安全。有效可测路径完整<=240，拒绝路径最多512单位安全显示。

ReportedFolderPath纯抽取原Cache.FromReported词法guard；原Kind检查、字段、错误key、VisiblePath行为保持。只去一个尾反斜杠，原WindowsPathPolicy/strictUTF16/补充Cf拒绝不变，不canonicalize/修复UNC或dot。系统/reparse/cloud/不可访问源仍按原held NativeFiles拒绝。长路径、UNC及合法含ZWJ等Format路径仍受预览保护限制，不说Windows本身不支持，也不放宽guard强行扫描。

所选根串行，每次只持有一个根与祖先，原MetadataDirectory.Open/FolderSizes.Scan：根+4层/20K/~30s soft EACH、最多6根（最多120K元数据条目，不保证180秒硬结束）。workFeature首await前绑定，前后token/只读Doctor helper真实等native与清理；预期UserError/IO/权限/安全/不支持/Win32错误逐根保留、其余继续。取消优先，不把取消作普通失败/迟到弹窗；未取消意外异常仍显示。Win32码取捕获异常，不用过期LastError或原始异常Message。无子文件内容读取、写入、删除/权限更改/清理撤销/助手上传。

去重对经guard去一个尾分隔符后的choice.Path作Ordinal精确、区分大小写比较；重复明确skip，不证明目录身份。大小写不同路径仍各测，避免错过大小写敏感目录。根可嵌套/重叠，hardlink可重复；绝不合计为profile大小/物理占用/可释放量。各根Counted/Partial/失败/skip和限额独立，失败—不是0。Result Recorded仅结果记录时间，不是原子采集。结果UI前所有root lease已释放，留历史快照。

Fluent虚拟根表/详情、明确Copy Summary（仅所选根路径、各自逻辑字节、失败及限制）/View Selected Map/Close，无自动复制或持久保存。子图Owner=结果窗，仅旧快照导航/no rescan，feature-off/ownerclose/Quit关闭owned链，真实WPF未验收。FolderMap尾部optional contextTitle保留旧null/cache标题/报告/控件/导航/DPI行为，新common图单独标题与overlap限制。Map仅Copy Current View，无单独详情复制/助手包装，复用安全可见formatter，未选位置不进结果摘要。

4435981核心通过，native应用/套件0警告错误仅交叉编译，agent最终见TESTING。29新keys/87翻译、1842旧values保持，新MyWheel ID <=0.67可能令旧Prefs拒/default/覆盖，降级保存升级前备份。F06/F09仍部分，固定52项22限定实现/21部分/7待做/2平台普通28不变；重复/相似比较的常用目录批量、完整缓存分类、安全清理、可靠撤销、卸载与深诊断待做。实际Windows COM/KnownFolders/重定向/系统属性/ACL/NativeFiles/ownedWPF/Clipboard/Narrator/DPI/ARM64未运行，托管替身和编译不是实机验收。

## 0.69已交付

2026-10-25。实际pull Already up to date c2b72a7/Mac1.12.6(34)，无新Mac。122项Mac和136个0.1–0.68旧ZIP保持，无Mac源码/测试/构建/安装写入或执行。

Doctor默认关闭；新增稳定doctor.commonduplicates / Compare Common Folders…，Duplicate & Similar Files分区，18canonical+Home-only doctor.check/13有限只读区域。忽略无关files，首await前绑定Doctor。复用0.68六位置读取/路径保护，初始无选择；Compare闭集chooser模式是尾部optional参数，默认Measure标题/按钮/范围/限额不改。Compare先审阅路径，再完整内容读取确认列出所有1–6所选根；取消不扫描。Feature/Prefs/schema/注册表/依赖不变。新MyWheel ID使<=0.68可能拒Prefs/default/后续覆盖，降级须保留升级前备份。

CommonDuplicates纯协调原FolderSizes+一次DuplicateFiles.Scan，不改旧单根DoctorDuplicates、DuplicateFiles、NativeDuplicates、CommonFolderReader、ReportedFolderPath/WindowsPathPolicy/NativeFiles guard。所有所选根共享TOTAL20K metadata条目/~30秒soft，每根深度4；按其余不同路径分配剩余额度/同一全局clock deadline，成功退未用条目、供后根，不重扫之前partial。失败Open不扣条目；扫描前预留quota，扫描在返回count前异常则保守消耗该份额，修复独立agent复现的共享预算P2（原六根SecurityException会23>10）。计数不等于native枚举缓冲区/系统工作量硬上限。阻塞调用可超时，取消等真正结束。未尝试根/失败/局部EntryLimit-TimeLimit/精确重复都留可见行，不把失败当0或无重复。

精确去重只对经旧guard去一个尾分隔符后的choice.Path作Ordinal相等，大小写不同路径可分别扫以保留大小写敏感目录。最多6根串行打开/metadata，各根祖先一直持有到整个内容比较完成；不把只读操作转成可写模式。原同大小→不重叠首尾各64KiB→完整SHA256只跑一次，共享512MiB/file、2GiB requested-read（含samples/full）、60秒soft预算，不逐根乘六。每次最多一个data input，仍由原NativeDuplicate打开子祖先/属性probe→datahandle验证/同根volume/identity-length-mtime前后核对/无FileStream read-ahead；系统/reparse/cloud/offline/不支持identity继续拒绝。

跨根沿用完整[FILE_ID_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_id_info)的64bit volume+128bit file ID，组合在同一电脑上标识文件，不加根目录prefix破坏硬链接/嵌套根去重；不同volume同fileID仍区分。每个输入只在其自己的已持有根volume上读。opaque候选key为单数字kind加分隔符和relative；只经精确只读Sources map与length匹配路由到原Root.OpenDuplicate，不解析标签成路径。长根+叶名不保证完整路径<=240，过长opaque或native完整路径拒绝均保留issue，不崩整批，不静默丢行。叶元数据可被记录并不证明内容可读。

入口验证1–6/不同kind/全部CanAttempt并冻结选择后才I/O；nativeadapter创建异常会Dispose刚打开root。cleanup错误容器在开根前预分配，reverse finally尝试每根即使某个Dispose失败，完成后才抛聚合错误；取消优先，App只读helper真join/不出迟到UI或MessageBox。结果仅纯数据/只读roots与Sources，不持有root/providerdelegate，所有leases结束后才显示。每根记录时间不是原子采集；哈希是历史主数据流，不是当前/同时完整相同或安全删除证明。ADS/metadata/permissions不比较，逻辑重复字节不是物理或可释放空间，根大小绝不合计profile使用量。

原生Fluent三虚拟页：所选根/匹配组与成员/全部记录问题，逐根配额/deadline/Partial/失败/skip与原Win32码保留。路径逐根/逐段安全转义、懒显示，正常来源路径完整，无自动打开/复制/保存/助手上传。明确Copy Report、Copy Selected Group、Copy Selected Path；前两种含根/范围与完整限制。4Mi UTF16累计预算，超限整拒，剪贴板不先写截断内容，可改选小组或单路径。原单目录重复/默认Measure/缓存与地图保持。

4436105核心通过；native应用/套件0warnings/errors仅交叉编译。独立agent修复后最终见TESTING，23新keys/69翻译、1871旧values保持。F04/F06/F09仍部分，固定52项22限定实现/21部分/7待做/2平台，普通28不变；常用目录精确跨根比较已补，相似比较常用批量、保留副本复核清理、可靠回收站撤销/卸载/深诊断仍待做。实际Windows KnownFolders/COM/NativeFS/ACL/不同卷/硬链接/大小写敏感/阻塞取消/GUI/Clipboard/Narrator/DPI/ARM64未运行，托管替身不是实机验收。

## 0.70已交付

2026-10-25。实际pull Already up to date c2b72a7/Mac1.12.6(34)，无新Mac。122Mac文件/138个0.1–0.69旧ZIP保持，无Mac源码/测试/构建/安装写入或执行。

Doctor默认关闭；新增稳定doctor.startupfolders / Inspect Startup Folder Entries…，Background Items分区，19canonical+Home-only doctor.check/13有限只读区域。旧Run/RunOnce注册表、服务和常用目录/查重流程独立保持。只读取[Environment.SpecialFolder.Startup/CommonStartup](https://learn.microsoft.com/en-us/dotnet/api/system.environment.specialfolder?view=net-10.0)对应当前用户/公共登录Startup位置，GetFolderPath DoNotVerify、不Create/猜UserProfile或ProgramData。预期单位置解析错误保留/另一项继续，原ReportedFolderPath词法/240/UTF16-Cf/本地路径保护不改。系统提供者可解析网络/云重定向，不承诺OS零网络，但本工具不发网络测试/远程扫描；不支持位置按旧NativeFiles拒绝并说明，未放宽guard。

首await前绑定Doctor；OnSta解析两位置/只读helper真join，初始无选择/Read禁用，不可用项禁用；明确选1–2冻结数组后才Task.Run读取metadata。完整路径/Scope/Meaning/限额先展示，不读取子文件内容，所以无内容比较确认。不会读取无关files/后台启动扫描，也不读剪贴板。取消/feature-off/ownerclose/Quit等真正原生/清理完成，不弹迟到结果或错误；意外非取消错误仍显示。

一个共享预算TOTAL512计入条目/~10秒soft，Depth0只列直接子项；分配剩余quota/同一全局clock deadline，成功退未用条目，失败扫描保守消耗预留，Open失败不扣条目；沿用0.69异常扣账经验。Native枚举64KiB缓冲/预取可额外获取metadata，这不是OS工作量硬上限。阻塞调用可更久。精确Ordinal路径重复明确skip，大小写不同仍各试。每次最多一个根及祖先，先释放后下一个/结果UI；无OpenChild/子文件内容句柄。

原FolderSizes及MetadataDirectory保留系统/reparse/offline/cloud/同卷/持有祖先检查。目录Depth limit不展开，系统/链接/无效/不可访问/局部限额与枚举失败保留；失败/未尝试根不显示为0或完整空列表。扫描成功Record放在清理后，Dispose异常即使IOException也单独聚合并终止本批，不被Expected根读取错误吞下后继续开第二根；取消优先。agent设计预审发现的风险已在初稿阶段修正，无确认产品P1/P2。

只按最终大小写不敏感.lnk/.url文件名给Shortcut/Internet-shortcut hint，其它只File metadata，跳过文件Not classified、目录Folder not expanded，理由独立显示。不解析shortcut/URL/target，不开文件/运行命令，不推StartupApproved/实际启用、可信、目标存在/启动慢因，不查询任务/Store启动任务/其他用户。根尺寸不合计profile/物理/可释放；每根Recorded是结果记录时间，非原子采集。不改注册表、启停/禁用/删除、权限、文件或助手上下文。

Fluent虚拟条目表/根状态/选中只读详情、明确Copy Report/Copy Selected Item/Close。所有根错误/配额/deadline/partial/原Win32码/根枚举失败可见，完整正常native路径按根与名称安全转义，合法native名字<=255UTF16；非native畸形长值安全省略预览，不作为I/O目标。目录和跳过项逻辑大小—非0，普通文件保历史主流字节。Copy完整范围/限制/所选根和全部已记子项，或单项+所属根限制；4Mi UTF16累计预算超限整拒，无先写截断剪贴板/自动复制。无打开/定位/解析目标/删除/刷新/保存/助手包装；工作区/DPI Fit用原窗口布局。

4436208核心通过，native应用/套件0警告错误仅交叉编译；独立agent最终见TESTING。29新keys/87译、1894旧values保留（旧背景描述key也保留），Feature/Prefs/schema/依赖不变；新MyWheel ID<=0.69可能拒Prefs/default/后续覆盖，降级保存升级前备份。F09仍部分，52项22限定实现/21部分/7待做/2平台普通28不变。StartupApproved/计划任务/完整启用与目标归属、安全禁用/删除及撤销仍待做，不称平台不兼容。真实Windows KnownFolders/重定向/原生FS/ACL/系统属性/阻塞清理/WPF/Clipboard/Narrator/DPI/ARM64未运行；托管替身不是实机验收。

## 需要专项平台验证

- OCR/FaceAnalysis：打包身份、系统语言资源与准确率；便携ZIP不能假定具备所有条件。
- PDF渲染与Media Foundation/WinRT媒体：固定流输入、安全解码、可用系统编解码器、取消/输出管线和x64/ARM64实机。
- 语音识别：本地语言引擎、文件输入、字幕时间精度；不能把SRT编辑宣称为转写完成。
- 原生系统抠图/人像/超分资源、HEIC/WebP/AVIF、多帧/HDR：单独验证，不静默降级后宣称等效。

## 已确认的边界

- 系统：AirDrop/Force Touch 没有本应用可直接复用的 Windows 实现。全局拖拽唤出、非激活面板、截图接管需 Windows 原型。
- 图片：当前受限单帧 SDR PNG/JPEG/BMP/TIFF及完整画布静态GIF输入；另有0.38受限静态GIF独立导出（256索引/二值alpha，不量化），0.39另补受限GIF预设；GIF动画/特殊布局/ICC扩展未开放；HEIC/WebP/AVIF/HDR/多帧不能依赖任意机器都安装的编解码器。
- AI：Apple Vision/Core ML/Speech 不能直接运行；另选本地模型/转换/哈希/许可与质量验证，未授权引入新的第三方应用依赖。
- 重命名：先限定 NTFS 身份规则；0.74已补名称互换/仅改大小写/连锁；exFAT/FAT/ReFS、硬链接待专项实现，真实NTFS两阶段失败恢复/失电场景待验，不能假定各文件系统 file ID 恒定。
- 验收：没有 Windows x64/ARM64 实机执行环境。可继续本地实现和交叉构建，不能声称运行验收完成。

## Mac 医生新功能来源：MangoDisk（2026-10-23，供 PC Doctor 对齐）

- 所有者要求告知 Windows 兼容版：Mac 医生这一批「空间分布图（矩形树图）、重复文件、项目构建产物、应用卸载+残留」是**学习 [harry0703/MangoDisk](https://github.com/harry0703/MangoDisk)** 的功能思路做的。MangoDisk 本身也支持 Windows，可以参考它的功能划分。
- **许可证：MangoDisk 是 GPL-3.0。** 按项目规则只能学功能思路，**不得复制它的代码、清理规则库或界面素材**；Mac 端只看了 README 功能列表，没有读它的源码。Windows 端也须自行重写，并在致谢中写明“思路参考 MangoDisk”。
- Mac 端实现与安全边界（Windows 版可对照）：`Sources/Swivel/DoctorExplore.swift`。
  - 空间分布图：用户选的文件夹，一次遍历，4 层，每层最多 24 块 + “其他”；不跟随链接、不跨卷、不下载云端占位文件、跳过受保护位置。
  - 重复文件：先按大小、再按首尾 64 KB、最后完整 SHA-256；同一文件的硬链接不算重复；每组默认保留最旧的一份；移动前要求至少保留一份未选中的副本，并当场重新计算保留副本与待移副本的哈希，不一致就不动。
  - 项目构建产物：只认旁边有对应项目文件的目录（node_modules+package.json、target+Cargo.toml、.build+Package.swift、Gradle build/.gradle、CMake build 需含 CMakeCache.txt、.venv/venv 需含 pyvenv.cfg、.NET bin/obj+*.csproj、.next、.godot、.dart_tool、Pods）；移动前再确认项目文件仍在。Windows 对应时注意 NTFS 联接点/符号链接不跟随。
  - 应用卸载：只按 bundle ID（Windows 可对应卸载注册表的 DisplayName/InstallLocation 与 %AppData%/%LocalAppData% 下的同名目录）匹配残留；按名称匹配的只显示不预选，常见厂商名（Google、Microsoft、Adobe…）从不按名称匹配；系统自带应用与自身拒绝卸载；全部进废纸篓（Windows 回收站）并可撤销。
- 这些Mac功能已提交为`bcb7bb9`（1.12.2 (30)），0.17开始前已在本地且pull确认最新；另有存储/缓存清理、应用内存分组与正常退出、实时/环形图、相似文件等新增差距，见PROGRESS。Windows 0.13现已补受限只读空间图，0.14补受限只读重复文件查找，0.15补受限只读项目构建文件夹线索；重复文件删除/撤销、项目产物清理及卸载/残留仍未实现，不应统一标为Windows已支持。

0.40降级限制：若将`doctor.textsimilar`加入My Wheel，旧版Windows不认识该动作ID，可能整份设置加载失败回默认、下次保存覆盖；降级前保留升级前设置备份。设置schema未变不代表旧版能读新动作。

与Mac的差异：Windows本批只列独立文件对，不合并关联分组；不会先剔除精确重复文件。支持扩展名/严格解码/扫描预算也不同，因此结果、百分比和排序不承诺跨平台一致；Mac另有代码、JSON/XML/HTML等文本扩展名，Windows本批尚未加入，而非平台不兼容。

## 0.74已交付：重命名依赖、GIF抖色、二维码方向与可用图片互通

批前GitHub pull Already up to date `6cbfdc1` / Mac1.12.7，无Mac源/测试/构建/安装更改。并行实现后独立交叉agent check，记录见TESTING；固定52项22已实现限定范围/21部分/7待做/2差异，普通剩余28不因子功能完成而减少。

- B05：仅大小写、互换及链式目标先把所有改变的源以持有的句柄移到同目录随机临时名，再逐个非覆盖发布；失败先腾回已完成目标再恢复原名，无法恢复的对象保留准确路径与身份用于会话Undo。不删除文件，不覆盖新占名称。普通无依赖改名沿用旧单阶段。所有路径和临时名≤240 UTF16；NTFS单链接非只读限制不变。Shelf、主窗文件/输出、挂图、Wheel/QuickActions的引用以原快照做一次映射。进程崩溃没有持久事务日志，可能留下`.swivel-rename-*`名称；尚不等于完整文件系统支持。
- C02：GIF有损减色可选蛇形Floyd–Steinberg，默认None保持旧字节；原5bit桶/加权盒palette、≤256节点精确RGB最近色KD查找及确定性tie、两行double误差缓冲（最大宽下786528B），校正值先夹到0–255。透明/越界误差丢弃，透明RGB归零、半透明整拒；低色图保原RGB。预览采样同一完整prepared结果，保存无重算；选项变化清旧输出、取消旧任务，关闭等待完成。渐变可减少色带但可能有颗粒，不是感知色彩优化。
- E05：整图严检后仅旋转≤177²模块网格，四个精确方向及显示方向，原像素不变；不插值、不修复RS/BCH、不放宽结构/quiet-zone/文本格式。照相、镜像、任意角度、裁边等仍拒绝，不自动打开或发送结果。
- E04：真正接Mac现有Import Images的PNG互通。只导出现有M级且≤2000UTF8的条目，逐条显示不兼容理由、不修改源纠错级别或缩短正文。全库快照、稳定随机批次前缀+源位置的安全文件名，8px/module/四模块白边；chosen目录及祖先跨整批固定，逐文件独占发布，首错/取消停止并返回全部已发布路径。Mac按文件名新建名称、空分组/夏季树/新日期，名称/分组/场景不迁移；现有或规范等价重复内容可能合并，真实Mac扫码仍需验收。预设反向迁移因Mac无JSON导入UI暂不能闭环，明确保留。

所有Windows native tests仍只交叉编译，真实NTFS/失电恢复/GUI/DPI/Narrator/剪贴板/Mac导入/ARM64验收未执行；不要用核心算法测试替代。

## 0.75 已交付：挂图长按与正常关闭窗口请求

Clothesline原生Button支持600ms长按预览，单击复制等待系统双击间隔；双击/长按/拖出互斥。按住时指针实际位置越界取消hold，已完成单击移出不丢复制；刷新/隐藏/关闭/功能关闭和右键/键盘交互撤销待发手势。定时器只在当前手势需要时启用，不做全局监听；原App动作会话继续持有源和祖先。自动截图/顶边/完整物理仍缺。

Request Window Close通过4096顶层窗口、64合格结果、约10s软界限的显式一次快照，只列当前用户和会话、非提升/非critical、可核验普通程序路径的可见无owner/非tool/非cloaked标题窗口；排除Swivel、Windows目录与系统shell。标题可含私人文档名，界面明确显示，不自动复制/保存/发送。持有QueryLimitedInformation|Synchronize进程句柄及创建时间，确认默认Cancel；发送前再次比对PID/TID/创建时间/会话/SID/权限/title/class/path/live。

专用无窗口MTA线程只发送固定WM_CLOSE，SendMessageTimeoutW使用BLOCK|ABORTIFHUNG|ERRORONEXIT及1500ms软超时。每条一次，失败/超时可能已送达，不重试。取消/关闭/关功能/Quit等待当前请求返回后再释放全部句柄；释放异常也尝试全部lease。未显示即Finish和迟到Dispatcher回调有回归。**不能锁住HWND，检查/发送不是原子的；不保证保存提示、退出整个应用或释放内存。** 不强杀、不提权、不发自定义命令，不称为完整应用退出管理。

官方语义依据：[WM_CLOSE](https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-close)、[SendMessageTimeoutW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendmessagetimeoutw)、[IsWindow 的句柄复用限制](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-iswindow)。真实Windows窗口/UI/消息投递/权限/ARM64仍未执行。

## 0.76 已交付：跨常用目录文字及图片相似比较

两个独立入口共享原比较器：用户先明确选择Windows报告的1–6个常用目录，再确认完整路径与读取内容；初始不选目录。全根合计20K元数据条目和约30s软预算，扫描前预留、成功退未用、异常保守扣账；未尝试根与失败/partial继续可见。根/祖先句柄持有至内容比较结束，同卷/128位文件身份+64位卷号/长度/mtime核对，重叠根和hardlink按原完整身份去重，最多一个内容输入。根清理尝试全部，保留错误诊断。

文本沿用一次200尝试/16MiB/60s及原严格文本/行集合80%配对；图片沿用一次200尝试/128MiB/60s及原9×8明暗网格比较、低细节跳过；最多1000对。不会给每个根重置内容预算，不把相似视为字节相同或可安全删除。Fluent虚拟结果表、问题页与明确复制，不自动扫描/删除/联网/持久化/发送助手。单目录旧入口保持。Windows路径/文件系统/实际解码/GUI/DPI/Narrator/ARM64均待实机验收。

0.74–0.76新增稳定动作ID保存到My Wheel后，旧版可能拒绝整份设置并回默认；降级前保留升级前备份。无新的Prefs schema、第三方应用依赖或模型。打包和独立复核见TESTING及外置构建报告，固定52项普通剩余28，不将子功能换算成整项完成。

## 0.85 Explorer 文件夹样式（B09）

批次开始 `git fetch`，origin/main 仍为 Mac 1.12.7／`6cbfdc1`。追加默认关闭 `Feature.FolderStyle`（枚举末尾）和 `folder.style`（自带多选文件夹选择器，不读取当前文件）。Core `FolderStyles` 生成并逐字节识别 Swivel 的 `desktop.ini`（UTF-16LE BOM、`[.ShellClassInfo] IconResource=<icon>,0`、`[Swivel] Version/Icon/ReadOnlyAdded`），图标名固定为 `.swivel-folder-<8 位小写十六进制>.ico`，ICO 内全为 PNG。Native `NativeFiles.FolderStyle` 持有从盘根到文件夹的所有祖先，另开带 WRITE_ATTRIBUTES 的文件夹句柄；新文件经私有暂存＋`CheckCreatedIn`＋相对持有句柄 `RenameInto`（不替换）发布；旧 `desktop.ini` 读入后用 POSIX 语义经同一句柄删除，图标只按生成名字并经 `CheckCreatedIn` 后删除。顺序：先发布新图标，删旧 ini，发布新 ini（失败则删新图标），再删旧图标、按需设只读，最后 `SHChangeNotify(SHCNE_UPDATEITEM)`。Mac 的 Finder 资源写入、23 种材质、图片与彩色 emoji 不移植。

## 0.86 Windows MCP（G02）

批次开始 `git fetch`，origin/main 仍为 Mac 1.12.7／`6cbfdc1`。追加默认关闭 `Feature.AgentTools`。`App.Main` 只在参数恰为 `--mcp` 时运行 `McpHost.Run`（不创建 WPF App、托盘、热键或单实例锁），无管道时返回 2。Core `McpSession` 实现 JSON-RPC 2.0 逐行协议（2025-11-25/06-18/03-26、2024-11-05，拒绝批量），工具结果带 `structuredContent` 与 `isError`，Agent 可见错误用不翻译的 `McpToolError`。允许清单 `McpSession.Actions` 只含经 `ImageTools.Run`→`NativeFiles` 旁边独占写出的图片动作。计划最多 64 个、单次、1 小时；输入用 `NativeFiles.InspectStamp`（持有祖先＋OpenReparse 探针）的 `SameObjectAndContent` 复核。历史 JSON 存 HKCU `Software\Swivel\WindowsPreview\AgentHistory`（200 条、4M 字符），损坏时拒绝 apply 且不覆盖。Undo 核对输出 stamp，只在 `GetDriveType==DRIVE_FIXED` 且 `SHQueryRecycleBin` 成功时用 `SHFileOperationW(FO_DELETE, FOF_ALLOWUNDO|…|FOF_WANTNUKEWARNING)`，事后确认文件已不在。MSIX 加 `uap3:AppExecutionAlias` `swivel-preview.exe`。Mac 的 OCR/读码/目标大小/改名/移动工具、MCPB 与 2026-07-28 无状态协议未移植。

## 0.87 移除背景与背景虚化（C10 第一部分）

批次开始 `git fetch`，origin/main 仍为 Mac 1.12.7／`6cbfdc1`。按用户 2026-10-25 的模型例外，新增 `Windows/ThirdParty/U2Net`（LICENSE、rembg-LICENSE、UPSTREAM、fetch-model.ps1/.sh）；模型 176 MB 超过 git 单文件限制，构建脚本按固定 URL 下载到被忽略的 `build/models/`，SHA-256 不符即失败，csproj 存在时复制到 `Models/u2net.onnx`。Core `Cutout`：Input（透明按白底、Lanczos3 缩到 320×320、取整截断、最大值归一、ImageNet 归一化、CHW RGB）、Mask（第一输出 min–max、Lanczos3 放大、非有限/平坦/主体<0.1% 拒绝）、Apply（alpha 相乘，alpha 0 清 RGB）、BlurBackground（≤1024 长边副本上 1−蒙版加权三次盒式模糊再放大，NaN 不扩散）。Native `CutoutModel`：一次读入全部字节、核对大小与 SHA-256 后由内存流交给 `LearningModel.LoadFromStream`，DirectXHighPerformance 失败或评估失败时回退 CPU，锁内串行、进程内缓存。`image.removebg`/`image.blurbg` 走 `ImageTools.Export`，也加入 MCP 允许清单。没有 Windows ML 以外的运行库；不同 GPU/驱动结果可能有细微差异。证件照（`Windows.Media.FaceAnalysis` 人脸＋尺寸/底色）留待下一批。

## 0.88 证件照（C10 完成限定范围）

批次开始 `git fetch`，origin/main 仍为 Mac 1.12.7／`6cbfdc1`。Core `IdPhoto`：预设尺寸（护照按 DPI 换算）、自定义像素/毫米、DPI 72–1200、最大 4000 边／16 MP；`SingleFace` 拒绝无脸/多脸/极小框；`HeadTop` 从脸框 10% 处向上走，蒙版覆盖脸宽中间 0.3–0.7 段 ≥30% 的行算头部（头发），最远 1.5 倍脸高，蒙版顶到画面边或无蒙版时用脸框估计（Y−0.4H）；`Crop` 使头顶到下巴占输出高度 70%、头顶在 13%，Zoom 0.5–2、Vertical ±0.3；`Prepare` 用与 Blur Background 共用的 `Cutout.BackgroundPlanes`（半径 2%）估计旧底色；`Render` 三角滤波重采样、行缓存，照片外用底色，像素 `v + (1−α)(底色 − 旧底色)`、透明像素直接用底色，输出不透明。Native `IdPhotoDocument`：一次模型推理得到全尺寸与 ≤900 预览两份蒙版，`FaceDetector`（Gray8）在预览副本上找脸再换算到全尺寸；预览 ≤480，保存经 `FormatConvertedBitmap` Bgr24＋`JpegBitmapEncoder`（95）带 DPI，`NativeFiles.WriteBeside` 独占新建 `名称 id.jpg`。对话框为原生 WPF（ComboBox/TextBox/Slider/ColorDialog），四语翻译。与 Mac 差异：Mac 用 Vision 五官定位（眼睛约 45%），Windows 系统 API 只有脸框，故头部与下巴为估计；未提供 MCP。

## 0.89 AI 图片放大（C11 第一部分）

批次开始 `git fetch`，origin/main 仍为 Mac 1.12.7／`6cbfdc1`。按用户 2026-10-25 的模型例外，新增 `Windows/ThirdParty/RealESRGAN`（LICENSE、NOTICE、UPSTREAM、fetch-model.ps1/.sh、Convert/）。Mac 用 Core ML 包，Windows 不能直接用；也不引入 Python/PyTorch：`Convert/` 是构建时 C# 工具，以只认普通 float 张量的受限 pickle 读取器读取官方 `.pth` 的 `params_ema`（702 个张量、全部用到），手写 protobuf 输出 RRDBNet x4 的 ONNX（IR 6／opset 11：Conv、LeakyRelu、Concat、Mul、Add、Resize nearest asymmetric floor、Identity），输入 `image` 1×3×256×256、输出 `upscaled` 1×3×1024×1024，字节确定、SHA-256 固定。Core `Upscale`：与 Mac 相同的 256 块／64 上下文／128 中心、外沿镜像（PyTorch reflect），2× 为 4× 块 2×2 平均，alpha≠255 时完全透明像素用 `Cutout.BackgroundPlanes` 的 alpha 加权近邻色代替、alpha 用 Lanczos 单独放大，NaN 输出按 0。Native：从 `CutoutModel` 抽出通用 `PinnedModel`（大小/SHA-256 校验、内存流加载、DirectX→CPU 回退、锁内串行），`UpscaleModel` 与 `CutoutModel` 共用；`Upscaler` 写 JPEG（95）或 PNG，`NativeFiles.WriteBeside` 独占新建。逐块进度经 Dispatcher 报告。视频放大、MCP 暂不提供。


## 0.90 语音转文字（D06 第二部分）

批次开始 `git fetch`，origin/main 仍为 Mac 1.12.7／`6cbfdc1`。按用户 2026-10-25 的模型例外（D06 可打包定版本地模型）新增 `Windows/ThirdParty/Whisper`（MIT LICENSE、NOTICE、UPSTREAM、fetch-model.ps1/.sh）。Mac 用 Apple SpeechAnalyzer，Windows 无对应离线系统接口（Windows.Media.SpeechRecognition 需联网听写或只支持短语法），故改用 Whisper base。转换工具从 `RealESRGAN/Convert` 改为共用的 `ThirdParty/ModelConvert`（子命令 `realesrgan`、`whisper`；Real-ESRGAN 输出哈希不变 `043a257b…`）：pickle 读取器现在接受 float16 和带步长的视图（复制为连续数据，边界检查），`whisper` 写两个 IR 6／opset 11 图：编码器 `mel` 1×80×3000 → 各解码层的交叉注意力 K（已乘 64^−¼）与 V；解码器 `tokens` int64 1×n＋12 个交叉张量 → 末位 logits 1×51865，无 KV 缓存（每步重算前缀；Mac M5 上 onnxruntime 300 个 token 一步 24 ms）。权重保持 float16，图内 Cast。Core `Transcribe`：librosa Slaney 梅尔滤波器（与 mel_filters.npz 差 2e-9）、中心反射填充 STFT（窗加权 DFT 点积，SIMD）、整段 log10/max−8 归一化；分词表 50257 行严格解析（末行 `=` 为空）；解码按 whisper 20250625：SuppressBlank、non_speech_tokens（离线由分词表算出的 82 个）＋控制符、ApplyTimestampRules（成对、不减、首个 ≤1 s、时间戳概率和）、贪心＋温度 0.2…1.0 回退（压缩比 >2.4 或平均对数概率 <−1，无人声 >0.6 时不回退/跳过）、前文提示最多 223、每窗最多 224 个 token、上下文 448；分段与 seek 规则同 `transcribe()`。采样回退用固定种子，结果可复现。Native：`PinnedModel` 增加按名字绑定多输入/读多输出的 `Evaluate`（原 `Predict` 不变），每个 30 秒窗口的 12 个交叉张量只建一次供所有解码步共用；`TranscribeDocument` 对整数 PCM WAV 用自有 `WaveConverter` 转 16 kHz 单声道 16 位，其他格式用 `MediaTranscoder` 直接输出 16 kHz 单声道 PCM16（128 MiB 上限）；输出经 `SubRip.Transcript/Write` UTF-8 无 BOM，`NativeFiles.WriteBeside` 独占新建。原生 WPF 对话框（语言 ComboBox、TXT/SRT CheckBox、设备说明），四语翻译。与 Mac 差异：模型不同（Whisper base vs Apple 系统识别），中文常为繁体；Mac 可把字幕烧录进视频并按语音压低配乐，Windows 仍待做；视频/M4A 输入待做；未提供 MCP。

## 0.91 字幕烧录进视频（D06 第三部分）

批次结束前 `git fetch` 发现 origin/main 新增 Mac 1.12.9／`4166d5d`（绳子与菜单面板在 Space 上被系统丢失时自行重建、PanelRecoveryTests）：只涉及 macOS 窗口服务器行为，Windows 的 WPF 绳子/转盘窗口没有对应故障，暂不移植；若真机发现虚拟桌面切换后窗口不显示再照此加自检。Mac 在 TrimEditor 的字幕页用 Core Text 图片经 CI 合成；Windows 无 CI，改用系统 `Windows.Media.Editing`：`MediaComposition` 放原视频 `MediaClip`，一个 `MediaOverlayLayer` 内每条字幕一个图片 `MediaOverlay`（Position 为合成像素，Delay＝开始，图片时长＝结束−开始），`RenderToFileAsync(Precise, CreateMp4(HD1080p) 覆盖原宽高/帧率，码率≥原码率且≥0.1 bit/像素/帧，限 1–100 Mbit/s；无声音时 Audio=null)` 写入 `%TEMP%\Swivel-subtitles-<guid>`，再经 `NativeFiles.WriteBeside` 按 1 MiB 复制并独占发布 `名称 subtitled.mp4`，最后删除临时文件夹。字幕图片在 STA 上用 WPF FormattedText（Segoe UI → YaHei → Yu Gothic → Malgun Gothic 回退，SemiBold）画白字＋ARGB(150,0,0,0) 圆角底条，宽高取偶数。Core `SubtitleBurn`：尺寸检查（64–3840×64–2160、偶数、≤2 h），字号 4.5%/5.8%/7.5% 画面高，宽 90%，底边距 6%，最高 40%；计划先丢掉落在视频内不足 100 ms 的空白/短字幕，再按开始排序、截到下一条开始和视频末尾。视频声音的转写复用 0.90 `TranscribeDocument`（视频走 MediaTranscoder，跳过 128 MB 输入上限检查，无可解码音轨时明确提示改用 SRT）。原输入句柄只共享读，其父目录租约保持，路径打开的 StorageFile 是同一文件。原生 WPF 对话框（来源单选、语言、另存 SRT、SRT 选择、字号），四语翻译。独立 review 修复：另存的 SRT 在之后编码失败/取消时仍列入结果（先画字幕再存 SRT，编码段 try/catch）；短字幕先丢弃再截断；奇数尺寸明确拒绝；SRT 打开失败给出翻译提示；三行判断容差 0.5 行；`CreateMp4(Auto)` 改为固定 HD1080p 再覆盖。仍待：旋转手机视频、临时盘空间预检与崩溃后清理旧临时文件夹、M4A、按语音压低配乐；`MF_MT_VIDEO_ROTATION` 是否经 MediaClip 暴露、上千条字幕叠加的内存和速度需真机确认；未提供 MCP。


## 0.92 按说话压低配乐（D06 第四部分）

批次开始 `git fetch`，origin/main 为 Mac 1.12.9／`4166d5d`（0.91 已检查，无 Windows 影响）。`WaveDucking` 新增可选 `Speech`（`TranscriptSegment` 源时间秒）：有它时 `WaveActivity.Analyze` 不读音频，改由 `FromSpeech` 生成同样的 20 ms 窗口标记（丢弃零长段，按开始排序，<0.5 s 停顿合并——同 Mac `Music.swift`；按 floor/ceil 帧并扣除裁剪起点、裁到选区），之后的 `WaveDuckEnvelope`/`WaveMixer` 不变；校验只查音量 0–100%、区间有限且不倒置、≤100,000 段，跳过阈值和 10 分钟/128 MiB 限制（标记数组约每秒 50 个）。`WaveDialog`：Lower Only While Someone Speaks 复选框与 Find Speech 按钮；查找用独立 Task/CancellationTokenSource（改数值只取消试听，不取消查找；关闭、取消、关掉该选项或退出时取消），`FinishPreviewAsync` 同时等待试听和查找；`UpdateButtons` 统一按钮状态；主音频 >1 小时时禁用并说明。转写用第二个只读句柄（与 WaveDocument 的共享读兼容）。原音量模式说明加“关闭时：”标题。独立 review：修复 P2 Find Speech 按钮可能一直灰掉；P3 修改数值会丢弃长时间查找、旧说明与新模式矛盾、>1 小时提示、零长段一致性。与 Mac 差异：Mac 在视频编辑器里配乐并用 SoundAnalysis 人声分类；Windows 只在整数 PCM WAV 配乐、用 Whisper 转写结果（更慢，但能区分说话和音乐/噪声的能力取决于 Whisper）。

## 0.93 项目构建文件夹移入回收站与撤销（F05 完成限定范围）

批次开始 `git fetch`，origin/main 仍为 Mac 1.12.9／`4166d5d`，无新 Mac 改动。Project Build Folders 窗口改为多选，新增 Move Selected to Recycle Bin…／Undo Move。`NativeFiles.RecycleProjectFolder`：校验路径，`DirectoryLease` 持有父目录及祖先（不共享删除，含拒绝重解析点祖先）；文件夹本身不跟随重解析打开，拒绝重解析／离线／召回／系统／设备属性，并以全共享句柄持有到移动之后——移动后该句柄的 `GetFinalPathNameByHandle` 即回收站中的 `$R…` 路径（撤销用）；`FILE_STANDARD_INFO.DeletePending` 为真说明 Windows 永久删除（用户在过大提示里同意），单独报告且不计入“已移到回收站”。列出的每个项目标记文件（证据，最多 8 个）复核为普通文件。只在 `GetDriveType==3` 且 `SHQueryRecycleBin` 成功的盘上调用 `SHFileOperation`（FO_DELETE，ALLOWUNDO|NOCONFIRMATION|NOERRORUI|SILENT|WANTNUKEWARNING，owner 为对话框 HWND）。共享的 `RecycleBin` 类取代 McpHost 内的私有 P/Invoke，McpHost 行为与消息不变。撤销：原位置为空时用 `Shell.Application` `NameSpace(10)` 中 `Path` 相同的项 `InvokeVerb("undelete")`，在 STA 上以会泵消息的等待最多 5 秒确认文件夹回来，所有 RCW 释放。工作在独立 STA 线程，窗口 busy 时 Close 推迟到批次结束（退出／关掉 Doctor 仍会关窗）。限制：内容不与扫描比较；复核后到移动前文件夹本身仍可被别的进程改名替换（父目录持有，替换物同样只进回收站）；撤销前检查原位置到恢复之间有竞态，Windows 可能弹出自己的冲突对话框；撤销只在窗口打开期间、只针对最近一批。与 Mac 相同只认已列项目文件。

## 0.94 大文件移入回收站与撤销（F06 部分）

批次开始 `git fetch`，origin/main 仍为 Mac 1.12.9／`4166d5d`。把 0.93 的回收站流程抽成共享组件：`RecycleBinSession<T>`（确认列表、独立 STA 批次、最近一批撤销、批次中关窗推迟到结束、报告行）与 `NativeFiles.Recycle.cs`（通用 `Recycle(path, directory, check, owner)`、`RecycleProjectFolder`、新增 `RecycleFile`、`RestoreRecycled`）；错误文字改为不特指文件夹的“它”。Project Build Folders 改用该组件，行为不变。Large Files 窗口改为多选并加入 Move Selected to Recycle Bin…／Undo Move：`RecycleFile` 在持有父目录下不跟随重解析打开文件，拒绝重解析／离线／召回／系统／设备属性，长度须等于扫描记录、`nFileLinks == 1`（Mac 同样跳过硬链接）；移动后由持有句柄确认 `$Recycle.Bin` 路径或 DeletePending。筛选在批次中禁用，移走的文件离开列表与报告。限制：不比较内容（同大小的改动仍移动）；复核到移动之间同名替换的竞态与 0.93 相同；撤销仅限窗口打开期间最近一批。

## 0.134 合并第九轮：A05 截图卡片、B05/B06 exFAT/FAT32、C06 只去位置（C06 由 Codex 编写）

分支 `claude/win-a05-screenshots-b519a1`（195c56f、ad93ff0、1523e6b、50ba46a）、`claude/win-b05-exfat-026bb6`（5100bfa、385197b、950b3eb、3e73876，另有已删除的临时 FAT 身份探测提交）、`codex/win-c06-location`（5f35f52 由 Codex 编写，Claude 按审查修正）。Strings.json 三方 JSON 合并无同键冲突。CI 工作流新增一步：在 Windows 运行器上用 diskpart 创建小型 exFAT/FAT32 虚拟盘供原生检查使用（仅 CI，应用本身不需要提权）。

- A05：默认关闭的设置，对 `FOLDERID_Screenshots`（不创建）运行既有到达会话，不初扫、不改系统设置。卡片不激活，位于指针所在显示器右下角，2.5 s 无人操作离开（悬停/按下/拖动暂停，离开后再 1 s），单击或上拖按引用挂上（顶边绳开启时放下绳子），其他拖动以复制/链接交出。审查（无 P1，P2 已修并复查）：常驻文件夹租约共享删除，使 Screenshots/Pictures 仍可改名移动；达到预算的会话最多每分钟续一次；忙时保留卡片并提示；正被按住的卡片不替换；替换时保留悬停；先定位后显示；全屏应用时不放绳；DPI 重新定位；OneDrive 专门提示；每 5 s 复核文件夹被改名/移动/重定位或稍后创建；永不匹配时停止而不是循环。
- B05/B06：批量改名、移动文件、移动文件夹在同一 exFAT/FAT32 卷内可用；FAT 没有可靠的文件 ID，撤销依赖会话内持有的句柄（释放后撤销被拒），持有文件夹通过相对打开重新打开（ReOpenFile 拒绝目录）；必须用精确存储名（大小写、非 8.3 短名）选择。复制到 FAT：带 Zone.Identifier 的文件须在确认中明确接受丢失标记；额外数据流、非法名称、仅大小写不同、FAT32 ≥4 GB 预先拒绝；不提供撤销。NTFS 行为不变。审查修正：标记竞争、流回退、移动路径、持有竞争，以及诚实列出大小写冲突的跳过项。
- C06：新 `ExifLocation` 严格解析单个源 EXIF（两种字节序，IFD0/Exif/Interop/GPS/IFD1，条目/偏移/重叠/循环有界）并按原字节序重写。审查（无 P1，3 个 P2）修正：EXIF 内的 XMP(700)/IPTC(33723)/Photoshop(34377) 也删除；内嵌缩略图总是去掉（可能自带 GPS 或显示已裁掉的内容）；PNG 中关键字为 XML:com.adobe.xmp 或以 “Raw profile type” 开头的 tEXt/zTXt/iTXt 删除；输出命名为 “location removed”。TIFF 与 Agent 未改。未处理的 P3：其他非标准 APP1 原样保留（已说明）、严格解析可能使部分真实相机文件不可用此模式、每次打开都会多做一次验证解码。

## 0.133 合并第八轮：A05 顶边绳子、F09 网络测试、F05 项目构建（F05 由 Codex 编写）

分支 `claude/win-a05-rope-65af46`（daf9bd1、26480a0、427b24d）、`claude/win-f09-nettest-3f7e97`（90c9045、5f92e12、fcb62fc、11cb8ab）、`codex/win-f05-projects`（a378323 由 Codex 编写；Claude 去掉 Unreal Saved，随后按审查去掉更多不可重建项）。Strings.json 三方 JSON 合并无同键冲突。

- A05：默认关闭的设置；50 ms 光标轮询（无钩子、无权限），在指针所在显示器顶边短暂停留后放下无边框、不激活、置顶的绳子窗口，跳过全屏应用，指针离开约 1 秒收起。挂图复用原窗口的按钮与拼接逻辑（提取为 ClotheslinePrints）。RopeMotion 为解析阻尼弹簧，检查阻尼 ≥0.6、可见摆动 ≤2、1.5 s 内静止；减少动画时直接到位。审查修正（1 个 P2、3 个 P3）：最大化或带标题栏的窗口不当作全屏、最大化无边框窗口仍算全屏、拖出结束在所有宿主清除悬停、WM_DPICHANGED 建议矩形换成绳子自己的。截图接管未做。
- F09：`doctor.nettest` 窗口在按下带说明的按钮前不联网；网关 ping 4×1 s（含 PPP 适配器网关）、系统 DNS、msftconnecttest.com 精确正文且不跟随重定向（只报告重定向主机）、HTTPS 到 www.microsoft.com、可选 25 MB/15 s 下载测速（固定的 .NET 运行时包，丢弃）。使用系统代理并报告；无凭据/Cookie/持久化/修复。审查（无 P1，4 个 P2 已修）：HTTP 被拦而 HTTPS 成功为单独结论、任何 HTTPS 状态都证明 TLS 可用、代理隧道拒绝与 407 归为代理问题、内部超时表现为 I/O 错误时计为超时、无代理时的中性 DNS 说明、WPAD/PAC 说明、取消源释放、极快的完整样本也报告速度。
- F05：新项目类型只用既有元数据快照，要求同一父文件夹中的项目文件（Unity 另需 Assets 目录与 ProjectSettings/ProjectVersion.txt）；Maven/Rust `target`、Unity/.NET `obj` 同时满足时为歧义。审查（无 P1，3 个 P2）后去掉 Unity Temp（崩溃场景备份）、Unreal Binaries（无 Source 时不可重建）、Terraform .terraform（工作区与后端设置），并按 P3 去掉 Elixir deps 与 Nuxt .output；检查确保它们永不出现。未处理的 P3：名为 Library/Pods 等的普通文件夹会在问题列表多出“缺少项目标记”。

## 0.132 合并第七轮：D04 变速声音、F08 Store 卸载、A04 预览（A04 由 Codex 编写）

分支 `claude/win-d04-speedsound-1f2400`（c1208b3、fee7e4e、bdf6fe0、3e85790，另有两个已删除的临时 CI 探测提交）、`claude/win-f08-storeuninstall-e58da2`（802d041、345417d）、`codex/win-a04-preview`（0f68883 由 Codex 编写，Claude 加预览专用解码说明；审查修正提交）。Strings.json 三方 JSON 合并无同键冲突。

- D04：保留部分的声音以同样的剪切解码，用既有 WSOLA 拉伸到重定时视频的精确长度，编码为 AAC，由新的原创 MP4 混流（Mp4AudioMux）作为第二条轨道加入，画面不再重编码。审查修正：块偏移数量受自身盒限定（短 stco 曾导致预留数 GB）、hdlr/tkhd/mdhd/elst 读取留在盒内、0.25× 时允许两源帧误差、AAC 末包填充用编辑列表截到拉伸长度（CI 上 4× 时多 15.8 ms）、拒绝没有编辑列表的音频编辑盒、更严格的起始检查。
- F08：可卸载判定排除框架/资源/捆绑/系统签名/状态非 OK/被依赖/受保护（含 Windows App SDK 运行时、winget）与 Swivel；确认后重新读取列表与运行进程，`RemovePackageAsync` 仅当前用户，结果再读列表；之后对已不再安装的家族提供 Local\Packages 文件夹经回收站会话移走与撤销（仅当 Windows 自身清理后仍存在）。CI 上实际安装并卸载一个测试包。审查修正：运行检查跳过读不到包身份的进程（CI 显示否则总拒绝）、卸载专用读取不计入列表扫描时限、系统调用后任何失败都附新列表、卸载期间禁用撤销、更新隐私说明。
- A04：`FilePreview.Kind` 按扩展名路由到既有读取器；图片走与 image.preview 相同的 OnSta 打开；本地音频经 `WaveNoiseInput`，压缩音频经 `OpenDecodedAsync`（在后台线程开始）；字幕/数据经严格读取器（二进制 plist→YAML），格式有误的 SRT/CSV/TSV 退回文档文字读取；超范围浮点 WAV 给出预览专用说明。审查（无 P1/P2）未修的 P3：图片同时有“预览图片”和“预览文件”两个入口；声明非 UTF 编码的 XML plist 验证与显示解释可能不同（影响小）。

## 0.131 合并 D04 视频、第六轮 D03/F07/G02（D03 由 Codex 编写）

分支 `claude/win-d04-video-de4eb5`（7e91b9a、2d086d8、42c9c45、da4b789、42e29f4，另有已删除的 CI 诊断提交）、`codex/win-d03-float`（bc1a494 由 Codex 编写，Claude 审查与 P3 修正）、`claude/win-f07-quitapp-334968`（5d3145f、d2c1303）、`claude/win-g02-mcp-0be75b`（94330af、f3b9cc6）。Strings.json 三方 JSON 合并无同键冲突。合并时曾有一次其他工作区并发重新打包引起的 packfile 索引警告，随后仓库读取正常。

- D04：CI 探测表明 MediaComposition 拒绝 VideoTransformEffect（0xC00DA7FC），MediaTranscoder 接受；画面修改改用 MediaTranscoder 带剪切时间，纯剪切与只变速保留合成路径。非正方形裁切与旋转放在一个效果里会加黑边，改为两个效果串联。变速由 Mp4Retime 原地改写编码后 MP4 的时间刻度（不含声音），超过 3840×2160@60 的加速拒绝并限制码率。MediaTranscoder 不会把横存的手机视频转正，tkhd 矩阵非单位（含 180° 与镜像；WMV 等非 ISO 文件未知）时拒绝画面修改。审查修正：预览帧脱离解码线程、盒条目数按自身盒限定、cslg 缩放、各速度下声音复选框状态、裁切被拒时位置状态、重定时 IOException。
- D03：新 `WaveNoiseInput.Prepare` 嗅探 RIFF fmt（含 Extensible 浮点 GUID）与 FORM，分别用既有 FloatWave（24 位，不削波）、AiffWave、G711Wave 在内存中转换并以 PcmWave 复核；整数 PCM 仍用原持有流且输出字节不变。`WaveDocument.OpenNoise` 只供降噪使用；Dispose 现在释放准备好的流（含以前未释放的 Windows 解码流）。独立审查无 P1/P2；P3 修正：超过 512 MiB 的 AIFF/浮点文件由各自读取器给出自己的限制说明。仍记录的 P3：内存中转换峰值（G.711 接近上限时约 768 MiB）、少数不支持的压缩 WAV 显示的说明与提示不一致、浮点源信息行显示 24 位 PCM。
- F07：勾选的应用组（含辅助进程）在确认后，对 PID、启动时间与路径仍一致的进程的每个已列窗口发送既有正常 WM_CLOSE；不强制；只等待被请求的进程，未请求的单独列出。可选 2 秒刷新按进程时间差计算组 CPU，悬停、勾选、键盘焦点、退出中暂停。审查（无 P1；1 个 P2、9 个 P3 已修）：重新分组清除选择、截断的窗口扫描会报告、最多 32 个勾选、读取错误可见、说明提权/窗口标题隐私/逐窗口关闭；剩 3 个小 P3。
- G02：`plan_pdf_pages`（1–10 个 PDF，PdfPageChoice 新增可选 Turn，应用时每个 PDF 未变才执行，撤销移到回收站）；`plan_move` 接受文件夹（不与文件混合，按 Move Folder 持有句柄改名，应用时复核文件 ID、存储名、目标身份与空闲名，撤销用 Move Folder 自己的撤销）；`plan_action` 提供 image.jpegmetadata/pngmetadata/tiffmetadata。审查修正：文件夹撤销收据超过 256K 字符历史时拒绝且日志不超限、Agent 的文件夹移动只用计划的名称（被占用则拒绝而不编号）、文件夹检查前先持有父链（链接拒绝而非跟随）。

## 0.130 合并第五轮先完成的分支：E02 保留内容的 PDF 页面、B06 移动文件夹及撤销

分支 `claude/win-e02-pdfpages-578061`（4cb6258、55c2d84、e0f2616）、`claude/win-b06-movefolder-538156`（501ed7d、e8e8a9f、f458411）。Strings.json 三方 JSON 合并无同键冲突。D04 视频画面编辑分支仍在 CI 上试探 Windows 媒体管线，未合并。

- E02：原创有界 PDF 结构读取（传统 xref、xref 流、带 PNG 预测器的对象流、混合 XRefStm、/Prev 更新、继承的页面属性）与页面复制器：每页自己的对象逐字节保留、重新编号、写新的传统 xref。新动作 `pdf.pages`：选页、顺序/重复、旋转叠加 90°、多个输入、输出在第一个旁边。文档级项目、表单字段、指向页面或运行代码的注释去掉。审查（5 个 P2，已修并复查）：输出用 512 MiB 上限复读验证（合并可超过 32 MiB）、400 万解析值共享预算（复制器生成的值也计入）、线性重复键检查、清除页面树节点别名和 ObjStm/XRef/外部文件流、去掉 /AF 附件、链接只保留 http/https/mailto、清除保留注释之外对注释字典或带 JS 字典的引用。
- B06：移动文件夹把持有的文件夹改名进持有的目标（同一 NTFS 卷，FileRenameInformation 不替换，下一个编号名）；在自身内、它所在的文件夹和其他驱动器（建议复制文件夹）拒绝。撤销只在文件 ID 相同且原名称空闲时改回。复制文件夹记录新文件夹身份与每个创建项，结果窗口提供移到回收站，任何内容变化即拒绝。审查修正：结果窗口在回收站会话存在前读取它（提供撤销时必抛错）、晚取消不再遗留句柄、保持存储的名称、占用与被替换的副本各有提示、取消的复制也有结果窗口和撤销、关闭发起窗口会取消复制且结果窗口改由主页拥有。

## 0.129 合并第四轮并行分支：E05 照片二维码、C06 TIFF、D03/D04 M4A

分支 `claude/win-e05-photoqr-326c47`（2e344a7、d4a2b92）、`claude/win-c06-tiff-fd1801`（425da0c、143750f）、`claude/win-d04-m4a-3213f4`（85dc877、ef77560）。Strings.json 三方 JSON 合并无同键冲突。

- E05：严格平面读取不变且先试；失败后原创有界照片读取：灰度、分块局部阈值、1:1:3:1:1 寻像行列、少量排名的寻像三元组（最多 16 组）、按间距估计版本（v7 起以版本信息确认）、右下校正图形做透视否则仿射、网格采样，共享解码器的容错模式（最近 BCH 格式/版本、RS 纠错）。审查（无 P1/P2）修正：寻像交叉检查步长有界（16 MP 条纹图约 5 s→0.13 s）、校正图形搜索随码尺寸缩放、两侧模块尺寸分别估计版本、1-L/1-M/2-L 采样结果保留标准的误读保护、采样结果标为经纠错读取。
- C06：原创有界经典 TIFF 重写：每页保留，条带/图块字节原样复制，删除描述/EXIF/GPS/XMP/IPTC/Photoshop/私有标签，保留 ICC 与渲染标签，可选保留方向；BigTIFF、循环/重叠 IFD、未知结构标签拒绝。审查（无 P1）修正：整页验证解码、仅在确有删除时保存、损坏的 Orientation 作为畸形删除、摘要列出删除大小、说明保留的条带填充。
- D03/D04：原创有界 ISO-BMFF 盒检查（ftyp、moov/trak/mdia/minf/hdlr soun、stsd mp4a/alac、单音轨、无视频/DRM/moof、盒数量与嵌套有界、mdat 在范围内、单声道/立体声 8–192 kHz、2 小时）后交给既有 Windows 解码；系统解码、降噪、语音转文字接受 .m4a。审查修正：mdat 数量上限、以解码器配置判断声道、显式 HE-AAC 拒绝、接受 MPEG-2 AAC-LC、minf 中的 hdlr。

## 0.128 合并第三轮并行分支：F09 启动应用、C02 WebP、E03 DOC

分支 `claude/win-f09-startup-87ed4d`（26bac1c、c835289）、`claude/win-c02-webp-52401e`（3f82a21、bb23090）、`claude/win-e03-doc-3aff11`（c632c8f、959195a）。Strings.json 三方 JSON 合并无同键冲突；Mcp.cs 的输入提示同时保留 DOC 与 WebP 两处改动。

- F09：`doctor.startupapps` 组合既有有界 Run/RunOnce、StartupApproved 和（显式选择的）启动文件夹快照；每项按名称（不区分大小写）匹配其所属键（HKCU Run、HKLM Run、HKLM Run32、HKCU/HKLM StartupFolder），两种注册表视图都查。只解释 12 字节 REG_BINARY（02/06 启用、03/07 禁用、FILETIME 尾部），其余、冲突或不完整为未知。审查修正：文件夹读取器跳过的项（desktop.ini、链接、云端、不可读）列为未匹配；只经 32 位视图看到的机器值、不完整键中的匹配、重复/畸形值为未知并给出原因；原生 HKCU 检查只删除自己创建的键。
- C02：原创有界 WebP 解码（RIFF/VP8X/ALPH，VP8L 四种变换、颜色缓存、元前缀码与 LZ77；VP8 关键帧布尔解码、分段、帧内预测、反 WHT/DCT、简单/普通环路滤波；双线性色度上采样、BT.601）。不使用 WIC 编解码器；动画拒绝。前缀码表预算 1600 万项、未用组不保留、每个像素循环可取消。审查修正（P3）：单符号码也计入预算；相似照片说明不再把 WebP 列为待做。
- E03：按 [MS-CFB]/[MS-DOC] 原创读取：复合文件头、DIFAT/FAT/MiniFAT 链（环与边界检查）、根目录树（16,384 项）、WordDocument FIB 与 0Table/1Table 片段表（ccpText 主文档范围）；CP1252 压缩与 UTF-16 片段；域代码跳过保留结果；段落/单元格/行标记经段落属性页；直接隐藏与修订删除经字符属性页跳过。8 MiB 输入、2M UTF-16 输出、128 层域。审查修正：sprmPHugePapx 跟进 Data 流（有界、按偏移缓存）、PChgTabs cb=255、仅 fObfuscated 不算加密、接受 nFib 0x00C0、ZIP 签名的 .doc 走 DOCX、省略符号字符、孤立代理显示为 U+FFFD。

## 0.127 合并第二轮并行分支：B08 7z、F08 Store/MSIX、C06 保留方向

分支 `claude/win-b08-7z-376500`（1edb235、6afe198）、`claude/win-f08-msix-7c35ce`（d7bf057、0d659e1）、`claude/win-c06-orientation-d4f1eb`（df1bfd2、5754c41、c59c0c5），Strings.json 三方 JSON 合并无同键冲突。

- B08：按公开格式说明自写 7z 读取：签名与头 CRC、普通/编码头、单链 folder、名称/大小/CRC/时间/属性；原创 LZMA/LZMA2 解码（窗口 min(字典, 输出)、上限 256 MB）与 x86 BCJ；加密、其他方法、分卷、anti 项、链接与特殊文件拒绝；生成与 ZIP/TAR 相同的计划，沿用名称规则、上限与持有的独占 NTFS 发布及 Web 标记。审查修正：编码头的 LZMA 起始损坏转为用户可读拒绝。
- F08：安装记录窗口的只读 `PackageManager.FindPackagesForUser("")`（工作线程）：显示名、发布者、版本、架构、框架/资源/捆绑/开发标志、安装日期，包系列名默认隐藏；4096 个包、1024 UTF-16、约 10 秒软限、取消、包管理器不可用为状态；明确复制、4 MiB 报告上限。审查修正：单字段 IO 错误、读取失败后重开记录窗口、安装日期未读标记部分。
- C06：从唯一的源 EXIF 严格读取方向（两种字节序、SHORT×1、1–8；不规范/重复/多个 EXIF 只报告不保留），勾选时（2–8 默认）以只含 IFD0 Orientation 的 26 字节大端 TIFF 作为新的 APP1 或 eXIf 替换原块，其余保留字节不变。审查修正：保留 5–8 方向时按旋转后尺寸校验（以前会拒绝所有竖拍照片），校验失败只撤回该选项。

## 0.126 合并三个并行分支：F07 辅助进程分组、E03 EPUB、D04 保持音调变速

0.125 之后由三个并行会话在各自工作区完成并推送：`claude/win-f07-helpers-be1291`（bb99f1e、4594f9b）、`claude/win-e03-epub-e6d6ae`（ab6085f、d751671）、`claude/win-d04-timestretch-4d2f75`（fbf93a8、95f4996），主会话依次合并（Strings.json 按三方 JSON 合并，无同键冲突）并统一编号 0.126。

- F07：`AppMemory` 可选分组。Toolhelp32 快照在逐进程查询前后各取一次，父 PID 两次一致才用；父进程须在同一快照、路径与启动时间已知、子进程不早于父进程、子程序文件夹等于或位于父程序文件夹内；Windows 文件夹和根目录父进程、环、未知进程单独。分组以未挂靠的应用开头，链一次解析（4096 内线性），报告头计组数。默认关闭，按完整路径分组不变。
- E03：`DocumentEpub` 原创有界读取：严格 mimetype、container.xml 的 rootfile、OPF spine 顺序、XHTML 正文沿用 HTML 读取的空白规则；拒绝越界路径、大小写/反斜杠 ZIP 别名、加密或带权利管理（含大小写变体标记）、DTD 实体与非 UTF-8/16 XML；不读混淆字体；8 MiB 输入、每 XML 8 MiB、XML 合计 32 MiB、2M UTF-16 输出、深度 128，不解压、无网络。审查修正：DRM 标记大小写变体、manifest/spine 父级、隐藏 body、media-type 参数。
- D04：WAV 变速“保持音调”，原创有界 WSOLA（40 ms、75% 重叠、±10 ms 搜索、按权重归一化叠加、固定滑动源窗口），25–400%；关闭时旧的 50–200% 重采样输出逐字节相同；512 MiB/2 h 预算、PCM16 预览前缀与取消不变。审查修正：慢速时结尾不再出现静音空隙、状态文字、短于 40 ms 声音的说明、原生测试时长。

## 0.125 压缩音频降噪（D03 增强）

`WaveDocument` 改为从 `source` 读取：WAV 时就是持有的文件流（行为与字节结果不变），压缩格式时是 `SystemAudioDocument`（MediaTranscoder，128 MiB/2 h）完整解码出的规范 PCM16 WAV 内存流；`SystemAudioDocument.Detach()` 把持有的原文件交给 `WaveDocument`，原文件在整个窗口和保存期间保持持有，输出仍经 `WriteBeside` 放在原文件旁。`audio.noise` 接受 wav/mp3/aac/flac，标题改为 Reduce Audio Noise…，解码后的文件在窗口中注明。M4A/MP4、视频降噪仍未做。

## 0.124 移动文件接受只读文件与硬链接（B06 增强）

`ReadMoveStamp` 只在链接数为 0 时拒绝；撤销收据接受链接数 ≥1。移动路径唯一的修改是句柄相对的 FileRenameInformation（回滚同样），不删除、不改属性；改名引起的 ChangeTime 变化已由移动后重读印记和 `SameRenameMetadata` 处理。链接数仍在 `MoveFileStamp` 中，其他名称在预览后增删链接会使移动或撤销拒绝；撤销按记录的当前路径打开，只移回该名称。同一文件的两个名称：窗口给出专门提示，MCP plan_move 在计划时逐行标出（与 plan_rename 相同）。

## 0.123 批量改名接受只读文件与硬链接（B05 增强）；文件夹 ZIP 指纹修正

`NativeFiles.OpenRename` 不再拒绝只读属性和多链接文件：改名用 DELETE|READ_ATTRIBUTES 句柄经 FileRenameInformation（不替换），只读属性不阻止改名；暂存、回滚、大小写两步改名和撤销都只做改名，从不设删除标记或改属性。`FileStamp`（卷、索引、大小、写入/创建时间）不受改名或其他链接增删影响；撤销按记录的当前路径重新打开，只改那一个链接名。仍拒绝零标识；同一批中同一文件的两个名称仍由 `RenameSession` 拒绝，MCP `plan_rename` 在计划时就在后一行标出。被其他程序以不共享删除方式打开的文件仍无法取得租约。移动文件、回收与撤销输出仍保留各自的单链接/非只读要求。

文件夹 ZIP：`FolderZipPlan.Root` 记录根文件夹的 FILE_ID_INFO 身份并计入指纹。CI 显示刚建好的文件夹在规划与执行之间指纹会变：NTFS 父目录索引中的子文件夹时间是延迟更新的。指纹改为文件夹只计名称（文件仍计长度与修改时间），0.122 首次 CI 失败与 0.123 首次 CI 失败均由此引起。

## 0.122 Agent 工具 plan_zip_folder（G02 增强）

Mac 的 MCP 可把文件夹交给 ZIP 动作。Windows MCP 的计划按单个文件印记核对，所以新增独立工具 `plan_zip_folder { path }`：宿主 `PlanFolderZip` 用 `FolderZipWork` 持有并规划，计划只存文件夹路径和 `FolderZip.Fingerprint`（每个条目的类型、ZIP 名称、长度、修改时间与每个跳过项的路径和原因的 SHA-256）；`apply_plan` 由宿主 `ZipFolder` 在同一个持有会话里重新规划、比较指纹再写出，指纹不同则拒绝；写出的 ZIP 以 `archive.zipfolder` 记入历史（outputs 与其他新建文件相同，`undo` 移到回收站）。返回内容列出最多 200 个跳过项及总数。文件夹名必须与存储的拼写一致（大小写、非 8.3 短名），否则 ZIP 及其顶层文件夹会按另一拼写命名，故拒绝（图形界面同样适用）。已知限制：同长度且修改时间不变的内容改动无法从目录信息察觉（写入时仍逐文件核对打开前后的印记）；指纹不含根文件夹身份，同一路径换成内容列表完全相同的另一个文件夹不会被发现。

## 0.121 复制文件夹到…（B06 增强）

批次开始 `git fetch`：origin/main 仍为 `60a3487`（Mac 1.14.6）。

Mac 的 Copy to… 可复制文件夹；Windows 的“复制到文件夹”只接受文件。新增 `file.copyfolder`（Move Files 开关，自带两个文件夹选择器）。`FolderCopyWork.Open` 持有来源（MetadataDirectory），用 `FolderZip.Plan` 规划（同一套跳过规则与上限），`FolderCopy.CheckDestination` 拒绝目标为来源本身或其子文件夹（不区分大小写），`CheckLengths` 按“目标+名称+编号余量+最长相对路径 ≤240”预检，租用目标文件夹并要求 NTFS 与足够空间。`NativeFiles.CopyFolderTree` 仿照解压：在已持有的目标文件夹中用 CreateDirectory 独占建“名称/名称 N”，`HoldCreated`+`CheckCreatedIn` 持有并核对，子文件夹逐个独占新建（已存在即视为被干扰而停止），每个文件经 `OpenRead`（祖先租约、无链接）打开并核对长度后由既有 `CopyInto`（CopyFile2 FAIL_IF_EXISTS|COPY_SYMLINK、按创建句柄核验流）复制到其新文件夹，名称不符或未能核验即停止；已完成的副本保留。CopyFile2 开始写入时在回调中核对目标文件的真实路径就在所持有的新文件夹内，被挂载点重定向则取消（CopyFile2 删除该副本）并停止；“目标在来源内部”另按两侧文件系统报告的真实路径再比较一次（subst 盘符、8.3 短名）；上限提示用复制自己的文字；来源文件打不开时提示写明相对路径；未能核验的副本计入数量。`FolderPlanDialog` 由 ZIP 与复制共用。未提供撤销（复制文件的结果窗口撤销仍只针对单个文件复制）。

## 0.120 文件夹树压缩为 ZIP（B08 增强）

批次开始 `git fetch`：origin/main 仍为 `60a3487`（Mac 1.14.6），0.119 已推送，CI 40818572638 通过。

Mac 的 ZIP 可直接压缩拖入的文件夹（ditto）。Windows 的选择列表只接受文件，所以新增独立动作 `archive.zipfolder`（Archives 开关，自带文件夹选择器）。`Swivel.Core/FolderZip.cs`：`Plan` 在 `IFolderMetadata` 上按序数名称排序遍历，条目名以文件夹自身名称开头、'/' 分隔、文件夹以 '/' 结尾（空文件夹保留）；属性含 0x400（链接/联接点）、0x1000/0x40000/0x400000（云端/脱机）、0x4（系统）、0x40（设备）或大小/目录标志不一致的项目跳过并记原因，不打开；Windows 不支持的名称、完整路径超过 240 字符、同一文件夹中只有大小写不同的后一项以及无法打开的子文件夹也跳过列出；超 10,000 项、32 层、2 GB 整体拒绝。`Write` 用 .NET ZipArchive（UTF-8 名称，文件 Fastest、文件夹 NoCompression，修改时间按本地 DOS 时间，未知或 1980 年前记 1980-01-01）经 1 MB BufferedStream 写入；每个文件由调用方打开，复制量须恰为计划长度，复制后印记（身份、长度、修改时间）不变，否则整体失败；条目时间取打开时的印记；打开或读取中的 IO/权限/Win32 错误转为写明相对路径的“被占用或无法读取”提示。BufferedStream 只刷新不释放，避免关闭调用方的暂存文件流。Windows `FolderZipWork` 用 `MetadataDirectory.Open` 持有文件夹及其上级，`OpenDuplicate` 经已持有、重新核对的子文件夹打开每个文件（同卷、无链接/占位属性、长度符合），`NativeFiles.WriteInDirectory` 在父文件夹暂存并独占发布；整个驱动器拒绝。`FolderZipDialog` 列出数量、大小、说明和跳过项（虚拟化列表，路径经可见化）。

## 0.119 跟进 Mac 1.14.x：TGA 与 PSD 读写（C02 增强）

批次开始 `git fetch`：origin/main 仍为 `60a3487`（Mac 1.14.6），0.118 已推送，CI 40800721291 通过。

Mac 1.14.x 经 ImageIO 新增 JPEG 2000/PSD/TGA/EXR。WPF 不带这些解码器，也不应调用用户安装的 WIC 编解码器，所以 Windows 用原创实现补 TGA 与 PSD（按 Truevision TGA 2.0 规范与 Adobe 公开的 PSD 文件格式说明自写，无第三方代码）。`Swivel.Core/TgaImage.cs`：类型 1/2/3/9/10/11，8 位索引配 15/16/24/32 位色表（首索引偏移、越界拒绝），15/16/24/32 位真彩色、8 位灰度与 16 位灰度+透明；RLE 包可跨行，按流顺序解码后再按起点/左右方向放置；TGA 2.0 扩展区属性 0/1/2 视为无透明、3 直通、4 预乘还原，无扩展区且全零透明按不透明读；扩展区偏移无效时忽略。写出：不压缩、左上起点，全不透明 24 位/否则 32 位，495 字节扩展区（属性 0 或 3）与 26 字节页脚。`Swivel.Core/PsdImage.cs`：仅版本 1、8 位、RGB/灰度、压缩 0/1；跳过色彩模式数据与图像资源；图层数为负时额外第一通道是合并画面透明度。若文件恰好一个可见、normal、不透明度 255、无蒙版、铺满画布、额外信息只含白名单键（luni、lyid 等，无效果/文字/智能对象）的图层，且其透明度与合并画面一致，就用图层的直通颜色；否则只有带图像资源 1057（版本信息，Photoshop 写入）的文件、且所有部分透明颜色都 ≥255−a−1 时才按混白底去白底（Photoshop、ImageMagick 与 psd-tools 的约定），其余（macOS ImageIO 写的是原色）原样读取；1057 的“有真实合并数据”为 0（未勾选最大兼容）时，若单图层不可用则拒绝。图层记录的所有下标先查边界，损坏的图层记录退回读合并画面。PackBits 写出只在连续 3 个相同字节时用重复包，最坏行长不超过宽度+宽度/128+1。PackBits 行严格要求解出恰好一行宽度。写出：PackBits，全不透明 3 通道且图层段为空；有透明时 4 通道合并画面（混白底）加一个“Layer 1”图层（通道 −1/0/1/2，图层数 −1）。

接入：`ImageTools.Decode` 经 `OwnReader` 让 GIF/TGA/PSD 走自写读取器，元数据只显示尺寸和大小；convert.tga/convert.psd 走 `NativeFiles.WriteBeside`；`Catalog.IsImage`、相似图片、助手文件类型都包含 .tga/.psd，因此所有图片工具、晾衣绳、文件架预览、OCR、二维码读取和 Agent 工具（inspect/plan_action/plan_compress/read_codes/extract_text）都接受它们；转换对话框显示写出说明，输入含 PSD 时另显示读取说明。

## 0.118 跟进 Mac 1.14.5：Windows 医生“清理文件”（F06 增强）

批次开始 `git fetch`：origin/main 仍为 `60a3487`（Mac 1.14.6），0.117 已推送，CI 40695146443 通过。

Mac `DoctorSweep.swift` 把多个检查合成一次“查找可清理的项目”。Windows 只合并已有、各自已审查过的工具：`Swivel.Core/CleanupFiles.cs` 把 AppCacheResult、TempCleanupPlan（带当次截止时间）、LargeFiles 中 ≥5 MB 的安装包提示、ProjectFolderResult（带搜索根）转成条目，按组、大小排序，同路径或位于另一已列文件夹内的只保留外层；只预选能列出运行程序且未在运行、已测大小的应用缓存；组复选框三态。`CleanupReader.Find`（OnSta，下载文件夹经 shell 读取）依次运行 AppCacheReader、CacheLocationReader+TempCleanup、CommonFolderReader 下载 + DoctorLargeFiles、DoctorProjects（文档、桌面、%USERPROFILE%\source），某一部分失败只记该部分问题。`CleanupFilesDialog`：每组 Expander（折叠）、三态组复选框、行复选框（虚拟化 GridView 绑定，空格在 PreviewKeyDown 勾选所选行）、缓存行的状态列与“重新检查正在运行的应用”、`RecycleBinSession<CleanupEntry>` 按来源类型调用原工具的 RecycleAppCache/RecycleTempItem/RecycleFile/RecycleProjectFolder 与相应恢复；撤销后的项目取消勾选，恢复的临时项目离开列表（与旧临时文件窗口相同）。Doctor 区域 uninstall 改名为 Clean Up Files，动作 doctor.cleanup 在前、安装记录在后（与 Mac 1.14.5 同名改动）。未包含：很久没打开的应用（Windows 无可靠的桌面程序上次打开时间）、孤立残留（需卸载前记录）、重复/相似文件和非安装包大文件，说明文字列出。

验证：核心 `CleanupFilesChecks`（预选规则、部分大小、运行列表不可得时不预选、临时/安装包/构建条目与来源、5 MB 边界、合并排序与去重/嵌套、组状态与点击、目录与区域、翻译）。Windows CI 原生 `CleanupNativeChecks`：真实读取器在测试文件夹找到旧临时文件、6 MB 安装包和构建文件夹且无其他；窗口三组折叠、无预选时移动按钮关闭，整组勾选/取消；预选缓存时移动按钮开启、摘要 1 项；三类各自移到回收站并恢复；项目文件被删后构建文件夹拒绝移动；在 CI 机器上真实查找四部分（缓存 4 个、旧临时项目 11 个）。

独立 agent 审查：P1（有预选缓存时窗口构造即空引用，CI 首轮也因同一处失败）已修。P2：空格不能勾选、整组勾选二次方复杂度，已修。P3：撤销后仍勾选、缓存无状态列、项目部分内层异常类型、问题文字、嵌套条目、限制说明、测试措辞，已修；复查无 P1/P2，又修两个 P3（只在刚撤销时取消勾选、状态列通知刷新）。Windows CI 最终 40800721291 通过（核心 4,520,970、原生 5,488）。未验证：真实电脑上大量临时项目时的窗口响应、触屏/讲述人操作复选框。固定 52 项仍 27／23／0／2。

## 0.117 跟进 Mac 1.13–1.14.4：晾衣绳图片拼接（A05 增强）

批次开始 `git fetch`：origin/main 仍为 `60a3487`（Mac 1.14.6），0.116 已推送，CI 40682974347 通过。

按 Mac `ImageStitch.swift` 移植：`Swivel.Core/ImageStitch.cs` 的 `Size`（窄者宽度、高度按比例四舍五入且至少 1，永不放大）、`Stack`（上图行后接下图行）、`StitchHover`（只认目标下半部、0.5 秒停留，换目标或移到上半部重来，不能拼到自己）。Mac 的结果上限为 40 MP/32768；Windows 取图片工具的 16 MP/16384，避免保存的结果无法挂回或再处理（审查发现原 40 MP 上限会写出文件后再报错）。原生 `ImageStitcher.Save`：两张都经 `ImageTools.Open`（持有、转正、SDR/单帧检查），宽者转 Pbgra32 后缩放（透明边不变暗），WPF 若取整不同则补到精确尺寸，PNG 经 `NativeFiles.WriteBeside` 写到上图旁。App `clothesline.stitch`：功能开关、路径校验、Busy/取消；已写出后若取消或晾衣绳被清空则只报告已保存、不改晾衣绳；否则新图替换上图位置并取下两张原图。晾衣绳窗口：拖出的数据加自定义格式，只接受本窗口当前拖动且仍在晾衣绳上的图片；每张图片 DragEnter/Over/Leave/Drop（只在指针离开图片范围时算离开，避免缩略图与文字切换时重计时）、50 ms 计时器在停留到时变为可松开状态、边框加粗与双缩略图预览、拼接在 DoDragDrop 返回后执行；过早松开或离开时显示“未拼接”提示；右键“在下方拼接另一张”子菜单（文件名以 TextBlock 显示，提示框为完整路径）。Mac 的融合动画、减少动画处理与预览到挂图的连续交接无 Windows 对应。

验证：核心 `ImageStitchChecks`（尺寸与边界、拒绝、Stack 顺序、悬停区与计时、重来、自身、翻译与占位符）。Windows CI 原生 `ImageStitchNativeChecks`：300×100 红图在上、150×150 半透明蓝图在下得 150×200，颜色与透明度准确、原图不变、重名编号；301×151 半透明绿图缩到 100 宽，每个像素颜色与透明度误差 ≤2；JPEG 在上得以 JPEG 命名的 PNG；自身、预先取消、损坏图片均不写文件。

独立 agent 审查：无 P1。已修 P2：结果超过 16 MP 时写出后报错且晾衣绳不更新；状态栏在不可拼接后仍显示“松开即拼接”；在图片内部移动会重计时。已修 P3：过早松开无提示、计时器在可松开后不停、可接受其他来源的自定义拖动数据、取消后仍更新晾衣绳、下划线文件名在菜单里被吃掉、测试未覆盖半透明缩放与 {0} 占位符。未改：16 MP 时约数百 MB 峰值内存（有界）。复查无 P1/P2。Windows CI 最终 40695146443 通过（核心 4,520,888、原生 5,478）。未验证：真实 Windows 上鼠标拖放手感、静止停留时 OLE 是否持续刷新光标、触控笔/触屏拖动。固定 52 项仍 27／23／0／2。

## 0.116 跟进 Mac 1.14.4：转换为 ICO（C01 增强）

批次开始 `git fetch`：origin/main 仍为 `60a3487`（Mac 1.14.6），0.115 已推送，CI 40657735049 通过。

按 Mac `Images.makeIcon` 移植：尺寸 16/32/48/64/128/256 取不超过长边者，至少 16；每个尺寸由整张图按长边缩放（较短边四舍五入、至少 1 像素）后居中放在透明方块上。Mac 由 ImageIO 写 ICO，Windows 为原创 `Swivel.Core/IcoFile.cs`：ICONDIR/ICONDIRENTRY（256 记为 0、1 平面、32 位、偏移用 checked）、小于 256 为 BITMAPINFOHEADER 32 位自下而上 BGRA（直通 alpha）加 1 位 AND 掩码（完全透明处置位、行按 4 字节补齐），256 为 WPF PngBitmapEncoder 的 PNG。原生 `ImageTools.IconImages` 先转 Pbgra32 再用 TransformedBitmap 缩放，避免透明边变暗；所有尺寸生成后才经 `NativeFiles.WriteBeside` 独占写出 “名称 converted.ico”。目录 convert.ico（仅图片），转换说明窗口加 ICO 说明，Agent 工具 convert.ico 的 writes 说明各尺寸与放大规则。ICNS 不移植（Mac 专用）。

验证：核心 `IcoFileChecks`（尺寸选择、比例与半数进位、居中与裁切、位图头/行序/AND 掩码、写入读回、损坏拒绝、取消、目录与 Agent）。独立读取：Pillow 读回 16–128 各尺寸随机像素（含半透明）0 差异，macOS sips 识别为 ICO。Windows CI 原生 `IcoNativeChecks`：WPF IconBitmapDecoder 读出全部 6 个方形尺寸、32 与 256 像素横条居中、GDI+ Icon 读 48 像素，512 像素 1 像素棋盘格在 16 像素平均为 128 灰，白色方块缩小后边缘无变暗，10×6 JPEG 得单个 16 像素图标，重名编号。

独立 agent 审查：无 P1/P2（自行用 Pillow 与 Python 逐字段核对目录、位图头和掩码）。已修 P3：透明边检查原为整数倍缩小无法失败（改为不对齐的 100 像素图并要求出现半透明像素）；预先取消检查措辞夸大（改为“开始前取消不写文件”，写入器自身取消由核心检查覆盖）；半数进位缺用例；Agent 说明未写尺寸规则。Windows CI：40681758853（修前）与最终 40682974347 均通过（核心 4,520,839、原生 5,467）。未验证：真实资源管理器/任务栏显示效果、16 MP 图片的耗时。固定 52 项仍 27／23／0／2。

## 0.115 跟进 Mac 1.14.4：字体格式转换（E03 增强）

批次开始 `git fetch`：origin/main 仍为 `60a3487`（Mac 1.14.6），0.114 已推送，Windows CI 40648212711 通过。

按 Mac `FontFormats.swift` 移植为 `Swivel.Core/FontFileFormats.cs`：sfnt 解析（≤1024 表、表重复/缺 head/越界拒绝、总大小 ≤64 MB）、按标签排序 4 字节对齐重排并重算每表校验和与 head.checkSumAdjustment（整文件和为 0xB1B0AFBA）；WOFF 1.0 每表 ZLibStream 压缩（变小才用）、读取时先按声明总长度防解压炸弹再逐表解压并核对长度；WOFF2 写出与 Mac 相同（glyf/loca 用空变换 3、loca 紧跟 glyf、Brotli 11/22）。读取 WOFF2：Mac 用 CoreText，Windows 原创实现——表目录（63 个已知标签、UIntBase128 拒绝前导零与超 32 位）、只允许 glyf/loca 版本 0 与 hmtx 版本 1 变换、Brotli 解压长度必须与声明一致；glyf 变换（W3C §5.1）：七个流、bbox 位图、overlap 位图、255UInt16、三元组坐标表、组合字形大小与指令；head.indexToLocFormat 必须与变换头一致，loca 必须与声明长度一致；hmtx 变换（§5.4）用 glyf 的 xMin 补左侧距。还原的简单字形按参考解码器的方式紧凑写回（0/1/2 字节差值、重复标志），否则比原字形大、短 loca 会溢出。转换结果再读一遍确认表数一致（Mac 用 CoreText 读回）。目录：font.desktop（仅网页字体）、font.woff、font.woff2，不设开关，与字幕/数据共用原生“持有原件读一次、写到旁边”；Agent 工具 plan_action 同有，unpack 预计输出写作 “name.ttf 或 .otf”。

验证：用 fontTools（临时目录的 venv，不进仓库、不随应用发布）对 242 个 macOS 系统 TTF/OTF 各做四项对照（fontTools 的 glyf/loca 变换 WOFF2 及另加 hmtx 变换由 Swivel 还原，Swivel 的 WOFF/WOFF2 由 fontTools 读回；比较每个字形的点、轮廓终点、在线标志、指令、组合部件，hmtx 和其余表），首轮 13 个因短 loca 溢出被误拒，改紧凑写回后 241 个全部一致，1 个（NISC18030，无 head 的位图字体）fontTools 自身读不了。核心检查内嵌 fontTools FontBuilder 合成的小字体（空字形、带二次曲线与指令的两轮廓字形、大坐标三角形、缩放组合字形、空格；CFF 字体）及其带变换 WOFF2。Windows CI 原生检查：系统 Arial → WOFF/WOFF2 → TTF，表逐字节不变，GDI+ 识别为 Arial、WPF 读到与原文件相同的 4,551 个字形（首轮 WPF 在同一线程先加载原 Arial 后再加载还原文件时抛 NullReferenceException；还原文件与原文件表一致、复查确认布局合规，改为各在新 STA 线程、先加载还原文件后两者都正常，判断为 WPF 同名字体缓存问题，未在真实电脑复现）。

独立 agent 审查（只读，含 2000 个变异样本模糊测试并与 fontTools 逐字形比较）：无 P1。已修 P2：WOFF 声明长度接近 4 GB 时 uint 求和回绕绕过防炸弹检查，最终抛溢出异常而非“已损坏”。已修 P3：WOFF/WOFF2 未知 flavor 未拒绝；两点间跳动超出 16 位时写入被截断（现拒绝）。未改：近 64 MB 的字体编码 WOFF2 可能较慢且不可中途取消（与 Mac 相同设置）；选中 .woff 与 .woff2 时两个转换都提供，与 Mac 规则相同。复查无 P1/P2，2000 个变异样本：1475 个转换、525 个以用户消息拒绝、无其他异常，fontTools 能读的 1409 个逐字形一致。

核心 4,520,814；Windows CI：40655171337 原生 5,449 通过、WPF 加载 1 项抛错（见上）；40656443734 通过（核心 4,520,815、原生 5,454）；最终 40657735049 通过（核心 4,520,816、原生 5,454）。未验证：真实网页字体（Google Fonts 等编码器）在 Windows 上、Office/浏览器中使用转换结果、近 64 MB 字体的耗时。固定 52 项仍 27／23／0／2。

## 0.114 跟进 Mac 1.14.4：数据文件互转（E03 增强）

批次开始 `git fetch`：origin/main 仍为 `60a3487`（Mac 1.14.6），0.113 已推送，Windows CI 40641688433 通过。

按 Mac `DataFormats.swift` 移植为 `Swivel.Core/DataFileFormats.cs`（WPF 已有 `DataFormats` 类，改名避免冲突）。值树：null、bool、`DataNumber`（保留原文、双精度值、是否整数）、字符串、UTC 日期、字节数组、列表、字典。读取：JSON 用 System.Text.Json（最深 128 层，键重复拒绝——Mac 的 Foundation 会静默保留一个），先用 Utf8JsonReader 计数，超过 400 万个值不建树；JSONL/NDJSON 逐行；CSV（RFC 4180，按 Unicode 标量读，引号内 CR LF 原样保留）/TSV（无引号）；plist 没有 Windows 系统 API，原创实现 XML 读取（先严格解码为 UTF-8/UTF-16，再交给 XmlReader，DTD 忽略、无解析器，实体无法取文件）与二进制 `bplist00` 读取（尾部与偏移表全部边界检查，1/2/4/8 字节整数及 16 字节无符号，单/双精度，日期，data，ASCII/UTF-16BE 字符串，数组，字典；共享对象只解码一次，引用环拒绝，UID 与集合拒绝）。写出：JSON（两空格缩进、键按序、数字原文、不转义 `/` 与非 ASCII）、JSONL（每项一行紧凑 JSON，空列表输出一个换行，同 Mac）、XML plist（制表缩进，整数仅限 64 位有/无符号，实数须与双精度最短表示数值相同，否则拒绝，同 Mac）、CSV/TSV（带 BOM，CR LF）、YAML（字符串与键一律加引号，转义控制符/行分隔符/BOM/非字符，指数写成 `1.0e+20` 以便 YAML 1.1 读成数字）。写之前遍历检查：展开后的值数（共享对象每处都算）、深度、估算输出字节、JSON/CSV/TSV 中的 NaN/无穷。表格转记录时先按“列名字节 × 行数”估算再建记录。目录：`data.json/jsonl/plist/csv/tsv/yaml` 六个动作，不设开关（同 Mac 的转换菜单），不提供自身格式，.ndjson 视为 JSONL；与字幕共用“持有原件读一次、写到旁边”的原生路径。Agent 工具 plan_action 同有，plan 时拒绝 32 MiB 以上。`UserError` 新增带参数模板（如“第 {0} 行不是有效的 JSON”），`ShowError` 先翻译模板再填值；消息中的键名截到 80 字符并显示控制字符。

与 Mac 的差异（Windows 内存与平台原因）：旧编码拒绝；JSON 最多 400 万个值、表格 500 万单元格、输出估算 128 MB（Mac 无 JSON 值数限制、1000 万单元格、256 MB）；重复键拒绝；JSON 输出 `"a": 1`（Mac 为 `"a" : 1`）。

独立 agent 审查（只读，/private/tmp 探针）：首轮 1 个 P1、3 个 P2，均已修并复查确认。P1：CSV/TSV 转 JSON/JSONL/YAML/PLIST 时记录重复全部列名而输出字节未检查，18.8 MB 的构造 CSV 会膨胀到约 34 GB，内存耗尽，MCP 进程崩溃（现先估算并检查，复查约 640 MB 峰值即拒绝，再按复查建议改为建记录前估算）。P2：JSON 中孤立代理转义抛出非用户错误（现报“不是有效 JSON”）；超出双精度的 JSON 数字被当成无穷（YAML 静默写成 .inf、plist 静默写错）——现只有 plist 的 nan/inf 才算非有限，JSON 大数保留原文、plist 拒绝；普通 32 MB JSON 峰值 2.5 GB、共享 plist 1.4 GB（现预计数、检查时先算子项再入栈，分别降到约 310 MB 和 890 MB 拒绝）。P3 已修：XML plist 声明 ISO-8859-1 可绕过编码规则、二进制 plist 按尾部数目预分配、YAML 指数写法、空 JSONL、只含换行的表头、逐字符分配、错误消息中的超长键；复查 P3：宽表头拒绝前要建全部记录约 6 秒且不可取消（现建之前估算并可取消）、超出 64 位的整数写 plist 时被四舍五入成实数（现拒绝，同 Mac）。

核心 4,520,764；Windows CI 40645059262（首次提交）通过：核心 4,520,752、原生 5,448；审查修复后 40646234854 通过（核心 4,520,764、原生 5,448）；复查修复后 40647198371 通过（核心 4,520,765、原生 5,448）；文档与版本提交后 40648212711 再次通过（核心 4,520,766、原生 5,448）。未验证：真实的 Mac 导出 plist（键控归档拒绝）、Excel 打开输出的 CSV、大文件在真实 Windows 上的内存与耗时。固定 52 项仍 27／23／0／2。

## 0.113 跟进 Mac 1.14.4–1.14.6：字幕格式互转、只读工具接受 16 位图片（D05/C09/E05）

批次开始 `git fetch`：origin/main 为 `60a3487`（Mac 1.14.6）。Windows 分支合并基线仍是 `4166d5d`（Mac 1.12.9）；按 `git diff 4166d5d origin/main -- Sources` 逐项对照 1.14.4–1.14.6 的新功能，状态表见 [PROGRESS](PROGRESS.md#mac-新增功能差距与本次同步)。本批移植其中两项，Mac 源码只读不改。

**字幕格式互转**（Mac `SubtitleFormats.swift`）：核心 `SubtitleFormats` 按 Mac 规则读 SRT/VTT/ASS/SSA/LRC/SBV、写 SRT/VTT/ASS/LRC/SBV/TXT，时间改用毫秒整数（Mac 用秒的 Double，换算后各格式精度相同）：`[h:]mm:ss[.,]f` 1–3 位小数、小时最多 6 位；WebVTT 跳过头部/NOTE/STYLE/REGION、去掉 cue 设置与 `<v>`/`<c>` 等标签并解码常见实体；缺空行时时间行开始新条目；ASS/SSA 按 Format 行确定字段、正文为最后一个字段可含逗号，去掉 `{\…}` 覆盖标签，`\N`/`\n`/`\h` 转换；LRC 一行多时间戳、`[offset:]`（正数提前）、每行持续到下一戳、末行 4 秒、元数据跳过。写出时把条目内空行折叠，VTT 转义 `& < >`，ASS 把 `{}` 改为圆括号、反斜杠改为全角，LRC 以 `[` 开头的歌词前加零宽空格，并在空档超过 50 ms 时补结束戳；时间限制在 0 到 9999:59:59.999。与 Mac 的差异：只读 UTF-8（可带 BOM）和带 BOM 的 UTF-16，GBK/Big5 等旧编码明确拒绝（Mac 用系统检测器识别这些编码；Windows 没有可靠的同等检测，误判比拒绝更糟）；输出 CR LF。上限：8 MB、10 万条（只算保留的条目）、全部文字 32 MB、ASS Format 最多 64 个字段。目录里 `subtitle.srt/vtt/ass/lrc/sbv/txt` 六个动作归入原有 Subtitles 开关（原名 SRT Subtitles，默认关闭），不提供文件本身的格式；App 逐个处理、首错即停，已完成的保留。原生 `SubtitleConvert` 持有原件读一次，写到原文件旁 `名称.新扩展名`（占用时编号），不覆盖。Agent 工具：`plan_action` 新增这 6 个动作（后缀为空，与 Mac 相同），plan 时拒绝自身格式、非字幕文件和超过 8 MiB 的文件，apply 时持有文件并核对 plan 时的身份。

**只读工具接受 16 位图片**（Mac 1.14.6 `Images.load(readOnly:)`）：`ImageTools.OpenForReading` 跳过 SDR 格式检查，16 位/浮点/CMYK 的 PNG/TIFF 经 WPF 转为 8 位 BGRA；文字识别（含 MCP extract_text）与二维码读取（含恢复与 MCP read_codes）改用它。大小与像素上限照旧；转换、编辑等会另存图片的工具仍拒绝。

独立 agent 审查（只读，含 /private/tmp 下的计时探针）：无 P1。已修 P2：一行很多时间标签的 LRC 每读一个标签就复制剩余整行，8 MB 文件约需 5 分钟以上（现按位置读取、每个标签检查条数上限，实测 16 ms 拒绝）；ASS 每个 Dialogue 行都在 Format 字段里重新查找，极宽 Format 行 + 大量对话要几分钟（现在读 Format 时一次算好，超过 64 个字段拒绝）。已修 P3：只含 U+3000/U+00A0 的行不算空行（现按 Unicode 空白判断，与 Mac 一致）；条数上限计入了会被丢弃的条目；解析无法取消（现每 4096 行/标签检查取消）。

复查：字幕修复与 16 位读取均无 P1/P2；其 P3（带色彩配置文件的 16 位/CMYK/浮点图片可能让 Windows 色彩转换抛出原始错误）已修：只读解码时色彩转换失败就不按配置文件转换，并补 CMYK TIFF、带 Windows sRGB 配置文件的 16 位 PNG/TIFF 夹具。

核心 4,520,660；Windows CI 40638689100（首次提交）通过：核心 4,520,652、原生 5,433；修 P2 后 40639764787 通过（核心 4,520,661、原生 5,433，8 MB LRC 标签行 72 ms 拒绝）；色彩回退与新夹具后 40640717318 通过（核心 4,520,661、原生 5,444）；文档与版本提交后 40641688433 再次通过（同样计数）。未验证：真实带样式的字幕文件在各播放器中的显示、旧编码字幕（目前拒绝）、真实 OCR（需 MSIX）读取 16 位图片。固定 52 项仍 27／23／0／2。

## 0.112 剪辑视频 Trim Video…（D04 部分）

批次开始 `git fetch`：origin/main 为 `60a3487`（Mac 1.14.6），不含 `Windows/` 改动；0.111 已推送，Windows CI 40616348475 通过。

新增动作 `video.trim`（Trim Video…，单文件，MP4/MOV/M4V/WMV，可放 Wheel／My Wheel；Mac 的 Trim 编辑器的“剪辑”部分）。核心 `VideoTrim`：起止时间解析（秒、分:秒、时:分:秒，冒号后的分秒须小于 60，最多三位小数，只认西文数字，每段最多 7 位）与显示（m:ss 或一小时以上 h:mm:ss，省略末尾零），长度按整毫秒向下取整；`Plan` 要求 0 ≤ 开始、结束 ≤ 长度、至少 100 ms，保留全部且有声音（或视频本无声音）时拒绝为“没有改变”，无声音视频不声称保留声音。

原生 `VideoTrimmer`：与烧录字幕相同地以只读句柄持有原件（拒绝写入与删除），按 Windows 报告的转正尺寸检查上限（`SubtitleBurn.CheckVideo`）；报告非零旋转同样拒绝。画面预览用一个 `MediaComposition.GetThumbnailAsync`，一次一张（SemaphoreSlim），按 Windows 报告的帧率求出“该时间正在显示的帧”并请求其中点（Windows 返回最近的帧）；但报告的是平均帧率（CI 上 30 fps 测试视频报 30.659），所以预览仍可能差一帧，窗口说明写“大致”；CI 显示在片尾最后一毫秒请求会抛 ArgumentException，所以失败时最多向前退 15 帧。剪辑：新建 MediaClip 设 `TrimTimeFromStart`／`TrimTimeFromEnd`，`RenderToFileAsync(Precise)`，配置与烧录字幕相同（原尺寸与帧率、码率不低于原视频且至少约 0.1 bit/像素/帧，1–100 Mbit/s）；去声音时配置不含音频流。私有临时文件夹里 `FailIfExists` 生成，再经 `NativeFiles.WriteBeside` 发布为 `名称 trimmed.mp4`（占用时下一个编号），临时文件结束即删，文件夹在窗口关闭时删。

窗口 `TrimVideoDialog`：两个时间框、各自的画面（停止输入 0.25 秒后单飞加载，无效或超出的时间清空画面，取帧失败另起一行提示，不覆盖校验说明）、Keep the sound（无音轨时禁用并说明）、状态行说明保留多长从哪到哪、无效时 Trim 不可用；关闭时取消取帧，App 等 `FinishAsync` 完成后才释放视频。编码中进度沿用“正在编码视频：已完成 N%”，可取消，取消不保存。

独立 agent 审查（只读）：无 P1/P2。已修 P3：无声音视频保留全部时仍可“剪辑”出一份无意义的重编码（现拒绝）；预览请求最近帧而非正在显示的帧，可能差一帧（现按帧求中点；因报告的是平均帧率，说明写“大致、可能差一帧”）；无效时间仍显示旧画面、取帧失败覆盖校验说明（现清空画面并单独提示）；`Duration` 的错误文字（现用尺寸／时长上限说明）；测试补无声音整段、颜色交界前后 1 ms、编码中途取消。未改：取帧在取消恰好落在完成后时图像流交给 GC（与其他代码相同）。

Windows CI：第一次（40631037311）原生 5,402 项通过、剪辑检查在片尾最后一毫秒取帧时抛 ArgumentException（已加向前退帧）；第二次（40631898187）在新加的“编码中途取消”检查卡住超过 30 分钟后手动取消——该检查在 Windows 的进度回调里同步取消渲染，应用本身从界面线程取消、不会这样做，检查改为从另一线程取消并加 2 分钟上限；第三次（40634936392）中途取消通过，但颜色交界处取样差一帧（报告帧率 30.659）；取样点挪离交界后第四次（40635735160）通过：核心 4,520,580、原生 5,417；文档与版本提交后 40636734972 再次通过（核心 4,520,581、原生 5,417）。复查 agent 确认 P3 已修且无 P1/P2。未验证：真实电脑上的硬件编码器、长视频与 4K 的耗时、可变帧率手机视频的预览帧、窗口在真实 Windows 上的操作。D04 仍为部分实现，固定 52 项仍 27／23／0／2。

## 0.111 带旋转标记的视频：压缩与烧录字幕（G02/D06 部分）

批次开始 `git fetch`：origin/main 为 `60a3487`（Mac 1.14.6），不含 `Windows/` 改动；0.110 已推送，Windows CI 40600961106 通过。

探测：在临时分支（`codex/windows-probe-rotation`，跑完即删，不进历史）上让 CI 只运行一个探测程序。用 Windows 把左上红、右下绿、其余蓝的 640×360 图片编码成 MP4，再按手机写法改写视频轨 tkhd 矩阵为顺时针 90°/180°/270°。结果（CI 40613950551）：`MediaClip.GetVideoEncodingProperties` 报告转正后的尺寸（90°/270° 为 360×640）且 `MF_MT_VIDEO_ROTATION` 为 0（UInt32）；`GetThumbnailAsync` 显示转正的画面；按转正尺寸渲染得到正向画面与单位矩阵；按存储尺寸渲染得到黑帧；在编码配置里写入旋转值得到带矩阵但画面错误的文件。也就是说 Media Foundation 的 MP4 源已经应用了轨道头矩阵，`VideoCompress` 与 `VideoSubtitles` 原有的“非零旋转即拒绝”对这类文件从不触发，而现有按报告尺寸编码的路径本来就输出正向视频。

因此不改算法：`RotatedVideoNativeChecks` 用同样的夹具对 90/180/270° 检查 `VideoCompress` 的报告尺寸、压缩后尺寸/长度/目标大小、单位矩阵与四角颜色；90° 视频烧录字幕检查报告尺寸、字幕框在画面内、字幕时间内底部三分之一出现白字且之后没有、输出正向；3840×2160 夹具转 90° 后按 2160×3840 烧录并检查正向。`SubtitleBurn.CheckVideo` 改为长边 ≤3840、短边 ≤2160（原先宽 ≤3840、高 ≤2160，会拒绝转正后的 4K 竖拍）。仍保留的拒绝分支（Windows 报告非零旋转、可能未应用）改为如实说明。烧录对话框、拒绝文字与 MCP plan_compress 描述改写，四语。

独立 agent 审查（只读，对照 ISO/IEC 14496-12 tkhd 布局）：无 P1，夹具解析/矩阵/期望颜色正确。已修 P2：4K 竖拍转正后 2160×3840 被烧录上限拒绝，而说明称已支持（现长短边限制，CI 实测软件编码 2 秒竖向 4K 用时 8.3 秒成功）。已修 P3：字幕位置原先只查边界（现查字幕时间内外底部白色占比 0.016／0.000）；“sideways”易误解为横屏、不含 180°（改为“rotation flag”／“带旋转标记”）；保留的拒绝消息原称“暂不支持”（改为说明 Windows 报告的旋转可能未应用）；译文改为“自动转正”等。复查确认，并提示微软软件 H.264 编码器文档写最大 4096×2304，可能拒竖向 4K——CI 实测已通过，但真实电脑上的硬件编码器仍未验证。

核心 4,520,482；Windows CI 40615291253 通过：核心 4,520,483、原生 5,399；措辞修正与文档提交后 CI 40616348475 再次通过（同样核心 4,520,483、原生 5,399）。未验证：真实手机拍摄的 MP4/MOV、硬件编码器的竖向 4K、180°/270° 视频的字幕烧录。G02、D06 状态不变，固定 52 项仍 27／23／0／2。

## 0.110 Claude Desktop 扩展 Swivel.mcpb（G02 部分）

批次开始 `git fetch`：origin/main 仍为 `c053dd0`（Mac 1.14.5），不含 `Windows/` 改动；0.109 已推送，Windows CI 40516148024 通过（核心 4,520,464、原生 5,368）。

新增 `Packaging/mcpb/manifest.json`（manifest_version 0.3，name 与 `McpSetup.ServerName` 同为 `swivel-windows`，版本随应用，`server.type` node，`mcp_config` 为 `node ${__dirname}/index.js`，环境变量只有 `SWIVEL_EXE=${user_config.swivel_exe}`；可选 `user_config.swivel_exe` 类型 file，供便携 ZIP；只支持 win32；工具列表与服务器相同）与 `Packaging/mcpb/index.js`。按 MCPB 库的替换规则，未设置的可选项不会被替换（收到字面 `${user_config.swivel_exe}`），清空则收到空串，两者都按未选处理。启动脚本：选了的路径先 `path.win32.normalize`，须为盘符开头的本地路径且文件名为 Swivel.exe；否则用 `LOCALAPPDATA`（须为本地路径）下的 `Microsoft\WindowsApps\swivel-preview.exe`（AppxManifest 声明的别名）；`spawn(command, ['--mcp'], { stdio: 'inherit', windowsHide: true, shell: false })`，找不到报 ENOENT 提示，退出码原样传回。宿主若只结束 Node 进程，Swivel 在宿主关闭输入管道时读到 EOF 退出，但已在进行的 apply_plan 会做完。`build.ps1`／`build.sh` 把清单、脚本和 `Assets/Square150x150Logo.png`（改名 icon.png）平铺打成 `Swivel.mcpb` 放进便携输出（MSIX 构建会一并复制）。

Windows `ClaudeExtension`：`Swivel.exe` 旁有 `Swivel.mcpb` 时以 ShellExecute 默认动作打开（与资源管理器双击相同，不带参数）；没有文件提示本版本未附带；ERROR_NO_ASSOCIATION（1155）提示没有应用能打开 .mcpb。设置页新增 “Install in Claude Desktop…”，成功状态说明已交给打开 .mcpb 的应用（通常是 Claude Desktop），便携版再提醒在扩展设置里选这个 Swivel.exe。新文字四语。

独立 agent 审查（只读；对照 MCPB 官方 MANIFEST.md 与 schema/替换源码）：无 P1。已修 P2：原先用 AssocQueryString 预先检查 .mcpb 关联，Claude Desktop 若以打包应用或 DelegateExecute 方式登记会被误判为没有（现在直接 ShellExecute，只把 1155 映射为提示）；LOCALAPPDATA 不是本地路径时提示原说“未设置”（已改）。已修 P3：成功状态改为“交给打开 .mcpb 的应用”；选中路径先规范化；build.ps1 清理失败不再掩盖原错误，build.sh 用 trap 清理临时目录；启动脚本测试改为分别数 spawn/require 并拒绝 exec/fork 等。未验证：真实 Claude Desktop 的安装、文件选择、内置 Node 与 .mcpb 关联；从 Node 启动真实命令别名。

核心 4,520,481；推送后 Windows CI 40600961106 在 GitHub 的 Windows 运行器上通过：核心 4,520,482、原生 5,379，含经 Node.js 运行启动脚本、启动真实 Swivel.exe 完成 initialize/tools/list 往返（2.8 秒）及全部拒绝情形。G02 仍为部分实现，固定 52 项仍 27／23／0／2。

## 0.109 MCP 文档文字转换与逐个文件再核对（G02 部分）

批次开始 `git fetch`：origin/main 仍为 `c053dd0`（Mac 1.14.5），不含 `Windows/` 改动；0.108 已推送，Windows CI 40509653918 通过（核心 4,520,456、原生 5,358）。

`McpAction` 新增 `Document`（默认 false）、`Extension`（图片动作用 `ImageOutputInfo.Format`，文档动作取 `document.` 之后的部分）与 `Accepts(path)`（图片动作用 `Catalog.IsImage`，文档动作用 `McpSession.IsDocument`，即 `DocumentText.Extensions`）。`Actions` 末尾追加 `document.txt`、`document.rtf`、`document.docx`、`document.html`、`document.odt`（标题 “Save Text as …”，后缀 ` text`），原有十个图片动作的顺序与 id 不变。`list_actions` 的 inputs/writes 按种类描述；`inspect_files` 的 `actions` 只列 `Accepts` 该文件的动作；`plan_action` 对不接受的文件、以及文档动作下大于 8 MiB（`DocumentText.MaxInputBytes`）的文件整次拒绝并说明原因，输出预测用 `Extension`，文档动作另加 `note`（只保留文字；文档在 apply 时才读取，读不了的那时失败）。历史条目照旧是新建文件条目（action 为新 id），旧版可读。

`IMcpHost.Run(action, path, expected, token)` 新增计划印记参数，`ApplyPlan` 逐个传入。Windows `McpHost.Run`：文档动作用 `DocumentTextFile.Open(path, token, expected)`（`NativeFiles.OpenRead` 租住文件后先比较印记再读取；8 MiB 与各格式读取限制不变），再 `Save`（与“检查文档文字”窗口的五种写出完全相同：`名称 text.扩展名` 经 `WriteBeside` 独占发布）；图片动作改为 `ImageTools.Open(path, token, expected)`（同样先比较印记再解码，`plan_compress` 的图片也走这里）再 `ImageTools.Export`（原 `ImageTools.Run` 即这两步，行为不变）。两个 `Open` 的 `expected` 默认为空，界面调用不变。印记不符报 `McpSession.Changed`（由 0.108 的 `VideoCompress.Changed` 移来，plan_compress 共用）。设置页 Agent 工具说明与兼容表状态四语改写。

独立 agent 审查（只读；跑核心并编译原生，对照 Windows 宿主推演）：无 P1/P2。已修 P3：计划印记原先在文档解析、图片解码之后才比较（现在 `NativeFiles.OpenRead` 后立即比较，先于读取；界面调用不传印记，行为不变）；大于 8 MiB 的文档原先能计划、到 apply 才失败（现在计划时拒绝，inputs 说明与 note 照读取器实际接受的写，并说明读不了的文档在 apply 时失败并停止其余）；“表格布局”措辞；原生拒绝检查只比较消息的一部分（现在逐字相等）；一个测试名笔误。复查确认五项全部修好，只提 TESTING 仍有占位文字（已补）。

核心 4,520,463；Mac 上原生只编译（0 警告 0 错误）。推送后 Windows CI 40516148024 在 GitHub 的 Windows 运行器上通过：核心 4,520,464、原生 5,368，含本批原生检查（文本另存 DOCX/ODT、DOCX 另存 TXT 读回一致；文档、图片与压缩在印记过期时都以同一提示拒绝且不生成文件；撤销三份副本）。Word/LibreOffice 等其他程序生成的文档经 MCP 转换仍未在 Windows 上验证。G02 仍为部分实现，固定 52 项仍 27／23／0／2。

## 0.108 MCP 按大小压缩：plan_compress → apply_plan → undo（G02 部分）

批次开始 `git fetch`：origin/main 仍为 `c053dd0`（Mac 1.14.5），不含 `Windows/` 改动；本分支 0.107 已随另一会话推送的 `cfb952a`（Mac 1.14.6）到远端，该推送未触发 Windows CI。工作区另有其他会话的 Mac 未提交改动，未触碰。

Core 新增 `TargetSize.cs`：`VideoProbe(Width, Height, Seconds, HasSound, Bitrate)`（Bitrate 为视频流码率，0 = 未知）、`VideoTarget(VideoBitrate, AudioBitrate, Width, Height)` 与 `VideoBudget`。`Check` 只接受每边 2–8192、时长有限且 0 < 秒数 ≤ 7200；`MinimumBytes` = ⌈(150k + 有声音时 96k)×秒数÷8÷0.95⌉ + 4,096（容器固定开销）；`Plan(maxBytes, video)` 只在 maxBytes < MinimumBytes 时拒绝（所以它报出的最小值一定被接受），总码率 = (字节数 − 4,096)×8÷秒数×0.95，有声音时总码率 < 1 Mbit/s 用 96 kbit/s、否则 128 kbit/s（Microsoft AAC 编码器只接受 96/128/160/192，所以不用 Mac 的 64），视频码率 = 总码率 − 声音；`For` 先取目标允许的码率（至少 150 kbit/s），按它 < 500k/1M/2.5M/其余 把长边上限定为 640/854/1280/1920（Mac 的阶梯），只缩不放，两边向下取偶数且至少 2；原片码率（未知时 100 Mbit/s）只限制码率，不影响画面大小。`Retry(first, maxBytes, size, video)`：总码率按 maxBytes÷实际大小×0.95 缩放，低于 1 Mbit/s 时声音降到 96k，视频不低于 150 kbit/s；返回与第一次相同的目标即表示已无更小可试。`JpegBudget.Fit(bytes, width, height, encode(w,h,q), token)`：先在原尺寸做 q1，放得下再试 q100，否则在 1–100 间二分取最高放得下的质量（不假设大小单调，返回的总是实测放得下的那份）；q1 放不下时长边乘 0.85（直接按比例四舍五入、短边至少 1，不经 `ImageRules.Fit`，因为它只接受 8192 以内的长边），下限 min(320, 原长边)，到下限仍放不下抛错并给出各尺寸中最小的字节数。界面用的 `JpegBudget.Encode`/`Validate`（4 KB–64 MB）不变。只给 agent 看的错误用 `McpToolError`（英文，不进翻译表）。

`IMcpHost` 新增 `ProbeVideo(path, token)` 与 `Compress(path, maxBytes, expected, token)`（返回新文件路径；文件被租住后与计划印记不符即拒绝）。`McpSession`：工具顺序为 list_actions、inspect_files、extract_text、read_codes、plan_action、plan_compress、plan_rename、plan_move、apply_plan、undo、history，`plan_compress` 标为只读；`inspect_files` 对图片和视频在只读工具之后、plan_rename 之前列出 `plan_compress`。`plan_compress` 只接受 paths、target_bytes、format：target_bytes 须为 1,000–1,000,000,000,000 的整数（文本、小数、越界都拒绝）；format 可省略，`same` 只在路径为 .jpg/.jpeg/.mp4 时接受（其他图片和 MOV/M4V/WMV 拒绝并说明会转成 JPEG/MP4），`jpg` 可大小写不同、也认 `jpeg`，`heic`/`png` 拒绝并说明 Windows 版只输出 JPEG，其他值拒绝。每个路径须为图片（`Catalog.IsImage`）或视频（mp4/mov/m4v/wmv），然后 `Inspect` 记下印记；视频再 `ProbeVideo` 并用 `VideoBudget.Plan` 预先判断，行内给出预计的宽高与视频/声音码率；原文件已不大于目标时加说明（仍会重新编码）。任何一项出错整次拒绝，错误以路径开头。`Plan` 记录新增 `Target?`，动作用内部的 `compress`（不经 plan_action）。`apply_plan` 对带目标的计划逐个调用 `host.Compress` 并传入该文件的计划印记，其余流程（核对历史可读和输入印记、逐个生成、第一个失败即停、保留并记录已完成的、历史条目 `{action:"compress", outputs:[…]}`、`undo` 移入回收站）与 plan_action 完全相同，旧版可读。

Windows：`NativeFiles.Input` 新增 `Stamp`（对租住的句柄读身份、大小与时间；句柄拒绝写入，读到的就一直成立）。`ImageTools.CompressTo` 用共享的 `Open`（同一套 SDR/动画/尺寸限制与方向校正，租住原文件到发布），先核对 `Input.Stamp` 与计划印记，再`OnWhite` + `CopyPixels` 得到不透明位图，每个尺寸缩放一次（`TransformedBitmap`，与其他图片工具相同）再按各质量用 `JpegBitmapEncoder` 编码，不写元数据；结果用 `NativeFiles.WriteBeside` 以 `名称 compressed.jpg` 独占发布。新增 `VideoCompress`：`NativeFiles.OpenRead` 租住原文件（拒绝写入与删除）直到发布，并先核对计划印记；`MediaClip.CreateFromFileAsync` 读宽高、时长、视频码率与是否有内嵌音轨，旋转元数据（MF_MT_VIDEO_ROTATION）非 0 时拒绝，读不了报与“烧录字幕”相同的英文提示；先清掉一天以上、不是链接的 `%TEMP%\Swivel-compress-*`（进程被强行结束时留下的），再在 `%TEMP%\Swivel-compress-<guid>` 里用 `MediaComposition.RenderToFileAsync(Precise)` 与 `MediaEncodingProfile.CreateMp4(HD1080p)` 改写的宽高、码率（无声音时 Audio = null）、帧率（保持原值，超过 60 改 60）编码；编码结果以拒绝写入的句柄打开，大小核对与复制都经这一个句柄；超出目标按 `Retry` 再编一次（`Retry` 返回相同目标时直接报错，不重复编码），仍超出抛错并说出最小结果，未保存任何文件；最后以 1 MiB 块复制并 `WriteBeside` 独占发布为 `名称 compressed.mp4`，句柄与临时文件夹在 finally 中释放、删除。`McpHost.Compress`：图片在 MCP 的 STA 线程上做，视频与 `ProbeVideo` 用 `Task.Run(...).GetAwaiter().GetResult()` 在线程池上等 WinRT（与 OCR 相同），结束后 `GC.Collect` + `WaitForPendingFinalizers`，让 WinRT 的文件与片段对象及时释放，不再占着用户的视频。设置页 Agent 工具说明与兼容表状态四语改写。

独立 agent 审查（只读；用编好的 Core 做探针，并对照 Windows 宿主代码推演）：无 P1。已修 P2：`JpegBudget.Fit` 原先经 `ImageRules.Fit`，它只接受 8192 以内的长边，长边 9,639–16,384 像素的图片（长截图）一需要缩小就失败；现在直接按比例计算。已修 P3：拒绝时报出的最小目标约 3% 的长度下会再次被拒（多了一道浮点判断），现在只按 `maxBytes < MinimumBytes` 拒绝，两者都含 4 KB 容器开销（审查者把 1 毫秒到 2 小时的每个整毫秒都试过，无一不符）；原生测试的最小值改为按 Windows 报告的长度计算；原片码率低时即使空间充足也被缩小（画面大小跟着被原片封顶后的码率走），现在按目标允许的码率定画面、原片码率只限制码率；最低码率与尺寸下的重试会重复同一次编码，现在直接报错；每个文件被租住后再按计划印记核对（压前一个视频时被改的后一个文件原先会按新内容压缩）；编码结果的大小核对与复制经同一个拒绝写入的句柄，被强行结束留下的临时文件夹满一天后清掉；探测与压缩后回收 WinRT 文件与片段对象，免得一直占着用户的视频；`same` 对 MOV/M4V/WMV 拒绝（它们仍会变成 MP4），`jpeg` 当作 `jpg`，说明写明 1600 万像素上限，重试时总码率低于 1 Mbit/s 声音降到 96k。复查确认九项全部修好，另提一个 P3（批量中“文件已变”的提示说什么都没做，但之前的文件已保留），已改。留待 Windows：Media Foundation 是否在回收时放开文件、编码器是否守住所设码率、旋转元数据是否报告。plan_action 仍只在整批开始前核对印记，没有在每个文件租住后再核对（不在本批范围）。

核心 4,520,455；Mac 上原生只编译（0 警告 0 错误）。推送后 Windows CI 40509653918 在 GitHub 的 Windows 运行器上通过：核心 4,520,456、原生 5,358，含本批原生检查（1600×1200 噪点图缩到 982×736、93,874 字节；两个 6 秒视频 4.8 秒压完，11,032 → 10,391、109,369 → 84,990 字节，尺寸、时长、声音与撤销均核对）。真实手机视频、长视频与硬件编码器码率仍未验证。G02 仍为部分实现，固定 52 项仍 27／23／0／2。

## 0.107 MCP 移动文件：plan_move → apply_plan → undo（G02 部分）

批次开始 `git fetch`：origin/main 为 `c053dd0`（Mac 1.14.5：Mac 医生实时应用列表与“清理文件”），不含 `Windows/` 改动，本分支未合并；0.105–0.106 已推送，Windows CI 40494928507 在真实 Windows 上通过（核心 4,520,352、原生 5,328）。工作区另有其他会话的 Mac 未提交改动，未触碰。

`IMcpHost` 新增 `CheckFolder(folder)`（返回目标文件夹的 `MoveIdentity`）、`CheckMove(path, folder)`（已在该文件夹里返回 false，移不了则抛错）与 `Move(plan, expected, folder, token)`（返回与改名相同的 `RenameOutcome`；给了 `folder` 时目标须仍是那个文件夹）。`McpSession`：工具顺序为 list_actions、inspect_files、extract_text、read_codes、plan_action、plan_rename、plan_move、apply_plan、undo、history，`plan_move` 标为只读；`inspect_files` 每个文件的 `tools` 末尾加 `plan_move`。`Plan` 记录新增 `Moves?` 与 `Folder?`（计划时的目标文件夹身份），与 `Renames?` 共用 `Store`；路径写法核对抽成 `SpeltInputs`（plan_rename 与 plan_move 共用，行为不变）。

`plan_move`：只接受 paths 与 destination，其他参数名整次拒绝；destination 须为合法 Unicode 字符串，经 `MoveRules.DirectoryPath`（去掉末尾 `\`，盘符根保留）后由 `CheckFolder` 打开再关闭“移动文件”的目录链租约（本地固定/可移动盘、NTFS、每级可打开）并记下最后一级的身份，失败时错误以 `destination: ` 开头。逐个 `SpeltInputs`：存在、可读印记、最后一段与 `StoredName` 逐字相同。新路径 = 目标文件夹 + 原名。逐行问题依次：父文件夹与目标在字面上相同（不分大小写）→“Already in this folder.”；`WindowsPathPolicy` 不接受新路径；与另一新路径不分大小写相同（Swivel 保留名字，只能有一个过去）；`NameTaken` → 若新路径上的正是这个文件（`IsItself`：`Stamp` 与计划印记同一对象同一内容；目标用 8.3 短名等写法时，被“占用”的就是它自己，只读或被占用也认得出）则报“Already in this folder.”，否则报名字已被文件或文件夹占用（查不了也算问题）；`CheckMove`（Windows：用 `MoveSession.Open` 打开再关闭与移动相同的租约，按目录身份判断已在其中；另一卷/文件系统、只读、硬链接在这里报出）。有问题时 `can_apply=false`、不给 plan_id。

`apply_plan`：沿用改名的路径（`ApplyRename` 泛化为改名与移动），先核对历史可读和每个输入的身份/大小/写入时间，再调用 `host.Move`。Windows `McpHost.Move` 要求所有目标在同一文件夹且保留名字，用 `MoveSession.Open` 租住目录链与每个文件（DELETE + 读属性，租住期间别人无法改名、移动或删除），核对目标文件夹身份（新增 `MoveSession.DestinationIdentity`）与计划时相同、租约下无一“已在其中”、`Stamps` 与计划一致、`StoredNames` 与源路径最后一段逐字相同，再 `Execute`（预检目标空闲、按顺序独占改名、失败反向回滚、从不替换）。宿主抛错 = 未移任何文件，报 “Nothing was moved.”；全部回滚则不记录；回滚失败的文件列出现在位置、记入历史、可撤销。历史条目 `{id,time,action:"move",outputs:[],moves:[{from,path,volume,index,size,write,creation,undone}]}`；`LoadHistory` 校验 moves 的类型、路径、`from` 与印记，并拒绝同时带非空 outputs/renames 或 action 不是 move 的移动条目，损坏时整份拒绝且不覆盖。

`undo`：`Pending` 同时看 outputs、renames 与 moves；移动条目由 `UndoMove` **逐个**处理（与 Mac 的移动撤销和本版新建文件的撤销一致，不同于改名的整批）：已撤销 → “already undone”；原位置正是同一文件、名字写法相同、且记录位置已不是它（`Back`，与改名共用；原位置是它后来加的硬链接时不算）→ 标记已撤销；记录位置不存在 → 不动；身份或内容变了 → 不动；原文件夹 `CheckFolder` 不过（被删或不再是本地普通文件夹）→ 不动；原名被占用 → 不动；否则 `dry_run` 用 `CheckMove` 打开再关闭移回时的租约，被拒绝则报 “would not be moved back: …”，实际执行时以单个配对调用同一个 `host.Move` 移回原文件夹，回到原位则标记，回滚失败换了位置则更新 `path`。取消后其余项报 “not moved back: Cancelled.”。保存历史失败时返回错误，说明文件已如 results 所示、历史未更新，下次撤销会认出已回到原位的文件。`history` 对移动条目另列 `moves`（from、path、undone）。改名撤销的 `dry_run` 也对每个待改回的文件调用 `CheckRename`，会被拒绝的报 “would not be renamed back: …”，其余照整批规则等待。`LoadHistory` 另要求移动行新旧名字相同、同一条目的文件在同一文件夹。Windows `McpHost.Stamp` 遇到文件夹（错误 5 且 `Directory.Exists`）返回 null，原位置变成文件夹时报原名被占用而不是 “Access is denied.”。`McpHost` 把“移动文件”写给窗口用户的提示改成给 agent 的说法（`ForAgent`；目标是文件或链接时说明需要本地普通文件夹，`NativeFiles.LocalOnly` 改为 internal）。Core `MoveRules.SameRenameMetadata` 只忽略 archive 位：改名时 NTFS 可能设置它，原先存档位已清除的文件移动后会因“元数据变了”回滚。设置页与功能状态说明加入移动（四语）。

独立 agent 审查（只读；用替身宿主驱动编好的 Core 做探针，并对照真实 Windows 宿主代码逐条推演原生测试）：无 P1、无 P2；所有改名与回滚都不替换（ReplaceIfExists=FALSE），租约下再核对印记与名字写法。已修 P3：目标文件夹用 8.3 写法、文件又是只读或被占用时，原先报“名字已被占用”，现在按印记认出是它自己，报“Already in this folder.”；撤销的 `dry_run` 现在也用同一套租约检查（移动用 `CheckMove`，改名用 `CheckRename`），实际会被拒绝的文件报 “would not be moved/renamed back: …”，不再一律说会移回；原位置或现位置变成文件夹时，`Stamp` 不再报 “Access is denied.”，而是当作没有文件（原名被占用／不见了）；计划记下目标文件夹的身份（`CheckFolder` 返回 `MoveIdentity`），应用时文件夹被删掉又在原路径新建会拒绝；“移动文件”给窗口用户的提示（“create a new preview”、“Move Files …”、目标是文件时的“Choose a local regular file”）改成给 agent 的说法；原生 8.3 测试只比较文件本身的名字（父文件夹可能有短名而文件没有）；`SameRenameMetadata` 只忽略 archive 位（改名时 NTFS 可能设置它，否则存档位已清除的文件会移不动并回滚），其他属性照样比较，并补存档位已清除文件的原生移动；历史中移动行的新旧名字必须相同、同一条目的文件必须在同一个文件夹（篡改历史仍需用户本人的 HKCU 权限，属加固）。复查确认八项均已关闭、未引入新的 P1/P2，并指出两个 P3，也已修：原文件夹被删除后撤销原先报系统的“找不到路径”，现在先用 `CheckFolder` 检查原文件夹，报 “its old folder is unavailable (…); left alone”（`dry_run` 同样）；补上用大写写法和 8.3 短路径写目标文件夹的原生移动与撤销。接受的限制：撤销的原文件夹不固定身份（按路径移回）；降级到 0.86–0.106 后给它移动条目的 entry_id 会报完成但没有结果。

## 0.106 MCP 批量改名：plan_rename → apply_plan → undo（G02 部分）

批次开始 `git fetch`：origin/main 已从 `4166d5d` 前进到 `38a6fe6`（Mac 1.14.4：拼接、格式、AirDrop、Mac 医生清理；Mac 版 MCP 已有 plan_compress、plan_rename、plan_move），不含 `Windows/` 改动，本分支未合并；0.105 已随另一会话的 `f0817d4` 推到远端。工作区另有其他会话的 Mac 未提交改动，未触碰。

Core `RenameRules.Build` 拆出 `CheckLimits(count)` 与 `Destination(path, index)`，行为不变（批量改名窗口照旧）。`IMcpHost` 新增 `NameTaken(path)`（只有“找不到”算空闲）与 `Rename(plan, expected, token)`。`McpSession`：工具顺序为 list_actions、inspect_files、extract_text、read_codes、plan_action、plan_rename、apply_plan、undo、history；`inspect_files` 每个文件的 `tools` 末尾加 `plan_rename`。`Plan` 记录改为 `Action?`／`Renames?`，计划存取统一由 `Store` 处理（过期清理、64 个上限）。

`plan_rename`：只接受 paths、base_name、find、replace_with、prefix、suffix、number、start、digits，其他参数名整次拒绝（防止 `replace` 之类拼错让 find 变成删除）；文字须为合法 Unicode 字符串，`number` 为布尔，`start` 0–99,999,999、`digits` 1–8 为整数（1.0 也算整数），`start`／`digits` 需 `number: true`；至少给 base_name、find、prefix、suffix 或 number 之一，`CheckLimits` 在检查任何名字前执行。加进名字的四段文字拒绝 `\`、`/` 和 `HiddenCharacter`：控制/格式/行段分隔符、U+0020 以外的空格类、Unicode 默认可忽略的填充符与变体选择符（U+3164、U+115F/1160、U+FFA0、U+034F、U+17B4/17B5、U+180B–180F、U+FE00–FE0F、U+FFF0–FFF8、U+1BCA0–1BCA3、U+1D173–1D17A、U+E0000–E0FFF）和盲文空白 U+2800，报出码位；find 只用来匹配，不检查。逐个 `Inspect` 并读 `IMcpHost.StoredName`（Windows：只读属性句柄 + `GetFinalPathNameByHandle` 的最后一段，即长文件名的实际大小写），与传入路径的最后一段逐字比较，不同就整次拒绝并说出真实名字。逐行问题只查会改名的行：新名为空或从原来不以点开头变成以点开头（扩展名前什么都不剩）；`WindowsPathPolicy` 不接受；`IMcpHost.CheckRename`（Windows：打开再关闭与改名相同的 `NativeFiles.OpenRename` 租约，非 NTFS、只读、硬链接在这里报出）；新名不是任一所选文件当前名字且 `NameTaken`（查不了也算问题）。与另一新名不区分大小写相同则所有行都查。有问题或无任何变化时 `can_apply=false`、不给 plan_id；名字不变的文件不进计划，应用时不租、不核对。

`apply_plan`：先按原有逻辑核对历史可读、每个输入的身份/大小/写入时间，再调用 `host.Rename`。Windows `McpHost.Rename` 用 `RenameSession.Open` 租住每个文件和父文件夹，核对租约时的 `Stamps` 与计划一致、`StoredNames`（租住期间别人改不了名）与每个源路径的最后一段逐字相同，再 `Execute`（预检所有目标空闲，需要时先挪到同目录唯一临时名，失败按相反顺序回滚，从不替换）。宿主抛错=未改任何文件，报 “Nothing was renamed.”；结果中 `Remaining`（不在原名的文件）才记入历史：全部回滚则不记录并报 “Every name was restored”，回滚失败的文件列出现在位置、记入历史、可撤销。历史条目 `{id,time,action:"rename",outputs:[],renames:[{from,path,volume,index,size,write,creation,undone}]}`；`LoadHistory` 校验 renames 的类型、路径、`from` 与印记，并拒绝同时带非空 outputs 或 action 不是 rename 的改名条目（否则撤销永远完不成），损坏时整份拒绝且不覆盖。

`undo`：`Pending` 同时看 outputs 与 renames；改名条目由 `UndoRename` 处理，全部或全不：每个未撤销的文件须仍在、身份内容未变、原名空闲（原名是本条另一文件当前名字或只差大小写时不查）；任何一项不满足则全部不改、逐项说明并加 note；记录的位置已不存在、而原名处正是同一文件（身份与内容相同）时视为“已经回到原名”，标记 undone（`dry_run` 不标）；`dry_run` 只报告。执行时用反向配对调用同一个 `host.Rename`；回到原名的标记 undone，回滚失败而换了位置的更新 `path`，其余保持记录。保存历史失败时返回错误，说明名字已如 results 所示、历史未更新，下次撤销会认出已回到原名的文件。`history` 对改名条目另列 `renames`（from、path、undone）。设置页与功能状态说明改写（四语）。

独立 agent 审查（只读；用替身宿主驱动编好的 Core 做探针，并通读 Windows 宿主代码）：无 P1。已修 P2：新名字和撤销后恢复的名字都由 agent 传入的路径写法算出，大小写不同或给的是 8.3 短名时会把文件改错名（撤销也回不到真名）；现在 plan_rename 用 `StoredName`（最终路径的最后一段）逐字核对，不一致整次拒绝，应用和撤销时在租约下再查一次。已修 P3：加入的文字拒绝 `\`、`/` 和不可见/像空格/改方向的字符；名字不变的文件不检查也不租住；扩展名前为空或只剩空格、未知参数名被拒绝；`start: 1.0` 可用，`start`/`digits` 需 `number`；同时带 outputs 的改名条目使历史不可读，而不是成为永远撤销不完的条目；Swivel 改不了的文件（非 NTFS、只读、硬链接）在对应行报出；撤销后历史存不上会明说，下次撤销认出已回到原名的文件。复查发现这项恢复的新 P2：连锁、互换和只改大小写时，因为先查记录位置再查原名，认不出已回到原名；改为先查原名及其实际写法，并补连锁与只改大小写的回归。最终复查确认已关闭，且这项检查不会在尚未撤销的普通改名、连锁、互换、只改大小写或卡在暂存名的条目上误判；它提出的两个 P3 也已修（原名处是该文件后来加的硬链接时不算“已回到原名”，而算原名被占用；扩展名前为空的提示措辞）。 接受的限制：降级到 0.86–0.105 后给它改名条目的 entry_id，会报完成但没有结果；手动改了大小写后撤销会以“A file's name changed just now”拒绝。

## 0.105 MCP 只读工具：extract_text、read_codes（G02 部分）

批次开始 `git fetch`，origin/main 仍为 Mac 1.12.9／`4166d5d`；工作区另有其他会话的 Mac 未提交改动，未触碰。

`IMcpHost` 新增 `TextLanguages`（已安装的 OCR 语言标签；没有 MSIX 身份或没有语言时为空）、`ReadCode`、`RecognizeText(path, language?)`、`ReadDocumentText`。Core `McpSession`：工具顺序与 Mac 一致（list_actions、inspect_files、extract_text、read_codes、plan_action、apply_plan、undo、history），两个新工具 readOnlyHint=true、destructiveHint=false。

`extract_text`：先整体检查路径（1–100、唯一、合法 Unicode）和 `language` 格式（2–64 个 ASCII 字母数字或连字符，文档也检查格式）；任一既非 `Catalog.IsImage` 也非 `DocumentText.Extensions` 的文件即整体拒绝、不读取。有图片时 `TextLanguages` 每次调用只读一次：指定语言按不区分大小写匹配，换成 Windows 的写法传下去，没有就整次拒绝并列出已安装语言，在解码任何图片之前；列表为空时图片逐个报 `NoOcr`，不调用 OCR，文档照常读。逐文件 `host.Inspect` 后分派，返回 `{path, ok, kind, text}`，失败逐文件报告；文本须为合法 UTF-16，每文件 ≤`MaxFileText`(200,000)、整次回复累计 ≤`MaxReplyText`(400,000) 个 UTF-16 单位，超出的文件报错、不截断，之后放得下的文件仍返回。上限由 1,000,000 降到 400,000：回复里文字既在 text 内容又在 structuredContent，且非 ASCII 字符被转义，中文最坏约每字 13 字节。描述说明主机可能自行缩短长回复。

`read_codes`：只收图片，逐文件 `Inspect` 后 `ReadCode`，返回 `codes`（path、type "QR"、value 原文、version、correction L/M/Q/H）、`not_read`（path、error）和“不代表来源可信”的 note；描述写明按 90° 旋转、整数倍放大可以、平滑缩放不行。`inspect_files` 每项新增 `tools`，图片在 `TextLanguages` 为空时只列 `read_codes`（整次只问一次）。

Windows `McpHost`：`TextLanguages` 先查 `NativeIdentity.HasPackage`，再用 `TextCaptureDocument.InstalledLanguages()`（最多 128 个，超出视为空），COM 异常视为空；`ReadCode` 用 `QrReadDocument.Open`（严格平面读取，不做数据纠错）；`RecognizeText` 用 `TextCaptureDocument.Open`，再次核对语言，识别在线程池 `Task.Run` 上等待；`ReadDocumentText` 用 `DocumentTextFile.Open`。`TextCaptureDocument`：超出 `OcrEngine.MaxImageDimension` 改报新的 `TooLarge`（四语），没有语言仍报 `Unsupported`；`RecognizeAsync` 接受 null（`TryCreateFromUserProfileLanguages`），用户语言设置里没有可用 OCR 时报出已安装语言（仅 agent 路径会传 null），语言比较不区分大小写；文字识别窗口始终传入列表中的原样语言，行为不变。设置页与功能状态说明改写（四语）。

独立代理复查（0.105）：无 P1／P2。P3 已修：无 MSIX 时 inspect_files 仍列 extract_text、用户语言无 OCR 时误导性的提示、错误语言要逐张解码后才发现、读码描述不准确、上限单位与回复体积、"nothing leaves this PC" 措辞、README／PROGRESS 中过期的“OCR/读码待做”、测试缺口（仅文档时的语言、预算后的小文件、读码调用记录、原生 DOCX/RTF/HTML、大小写与未安装语言）。卡住的 OCR 调用没有超时（与现有动作相同，服务器按行顺序处理），记为限制。

## 0.104 在设置中卸载；卸载后的同名残留文件夹（F08 部分）

批次开始 `git fetch`，origin/main 仍为 Mac 1.12.9／`4166d5d`；其他会话的 Mac Doctor 未提交改动未触碰。`WindowsSettingsPage` 追加 `Apps`→`ms-settings:appsfeatures`（封闭枚举，无参数、无提升）。`InstalledAppsDialog` 页脚新增 **Open Installed Apps in Settings** 与 **Find Leftovers of Apps Removed Since This List Was Read…**；后者只设 `LeftoversRequested` 并以 `DialogResult=true` 关闭，App 的 `doctor.installedapps` 流程在 `ShowDoctorOperationAsync` 返回且未取消时继续：`AwaitDoctorReadAsync(Task.Run(AppLeftoverReader.Read(installed, token)))`，再 `ShowOperationDialog(AppLeftoversDialog)`，沿用原有取消／关闭／退出等待。新 `InstalledApps.Uninstall` 说明文字。

Core `AppLeftovers`：`Removed(before, after, recheck, token)` 在 `!Comparable(after)` 时抛 `UserError(NotComplete)`——`Comparable` 要求根无 Missing 以外的问题、每条记录无条目问题且名称与发布者为 Present／Missing／Empty，与 `InstalledAppsSnapshot.Partial` 不同，不看版本字段（版本从不比较）；候选＝之前 Present 名称（OrdinalIgnoreCase）不在之后任何 Present 名称中，按名称分组、发布者去重，Ordinal 排序；只有该名称之前每条记录都有合法子键、且 `recheck(source, key)` 全为 `Gone` 才进入 `RemovedApps.Apps`（带 `Keys`），否则作为带发布者与子键的 `RemovedApp` 进 `Unconfirmed`（不查找，窗口与报告列出名称）。Windows `InstalledAppsReader.Recheck` 先以 QUERY|视图打开该视图的 Uninstall 键，再打开子键：成功＝Present、ERROR_FILE_NOT_FOUND＝Gone、其余（含视图缺失）＝Unknown。`StillRemoved(folder, now, recheck)` 在移动前按查找时的规则拒绝：`now` 不 `Comparable`；任一记录的名称或发布者等于该应用名或以非字母数字字符延伸它；文件夹所在的发布者文件夹与某条记录的名称相同；该应用无子键或任一旧子键不再是 Gone。`Read(removed, after, reported, open, token, limits, elapsed)`：四个位置 Local/Roaming/LocalLow/Programs 依次以 `ReportedFolderPath` 校验后打开（未报告／不安全→Unavailable，缺失→NotFound）；应用名须通过 `ValidateName`、不在共享名单、不等于 `after` 中任何名称或发布者及未确认应用之前的名称或发布者、也不被其中任何一个以非字母数字字符延伸，否则记入 `Skipped`；发布者文件夹须通过 `ValidateName`、不在共享名单，且不等于任何应用名（仍记录、已移除或未确认），可为仍记录的发布者（只列出其中的应用文件夹）。目录项只读、`AppCaches.IsPlain`（排除链接／系统／云占位等），每文件夹 4096、总 20,000 项、最多 256 个文件夹，每个发布者文件夹只打开一次；发布者文件夹不可读标 `Unreadable` 并继续。大小由从 `AppCaches.Read` 抽出的 `AppCaches.MeasureShared` 共享公平预算测量（行为不变：100,000 项／约 30 秒，单个最多 20,000 项四层，打不开不扣预算）。`ProgramPaths(IAppMemorySource)` 用 `AppMemoryReader.Processes`（QUERY_LIMITED 与 `QueryFullProcessImageNameW`），枚举不可用或受限时返回 null；`Inside` 以“文件夹\”前缀 OrdinalIgnoreCase 判断。

Windows `AppLeftoverReader`：Local／Roaming 用 `GetFolderPath(DoNotVerify)`，LocalLow（FOLDERID_LocalAppDataLow）与用户 Programs（FOLDERID_UserProgramFiles）在线程池线程以 `CoInitializeEx(MTA)`＋`SHGetKnownFolderPath(KF_FLAG_DONT_VERIFY)` 读取，限 512 UTF-16，finally 释放并对称反初始化。`NativeFiles.RecycleLeftover` 复用私有 `Recycle`（持有父目录与项本身、普通本地文件夹、回收站可用、移动后核对去向），移动前 `programInside()` 为真（含无法列出）即拒绝；撤销用 `RestoreRecycled`（原位有新内容即拒绝）。`AppLeftoversDialog`：扩展选择、无预选、无全选，列 App／Place／Folder／Size／Status，复用 `RecycleBinSession`，可再查运行程序、复制报告、**Back to Installation Records**（空闲时以 `DialogResult=true` 关闭）。每项移动前在批次工作线程上：同一批只调用一次 `AppLeftoverReader.RecordsCheck`（读一次完整记录），对该项文件夹执行 `StillRemoved`，再以注入的 `programPaths` 做 `AppLeftovers.InUse`（null＝在用）；`RecycleLeftover(folder, Action check)` 失败回调给出 `AppLeftovers.NotRecycled`。App 流程改为循环：同一 `installed` 快照反复打开记录窗口；`NotComplete` 等 UserError 显示后回到记录窗口，残留窗口返回 true 也回到记录窗口，最终状态区分已移动／已查找／只读。`AppCaches.MeasureShared` 改为 internal 并校验预算范围。

独立代理复查（0.104）：无 P1；P2 两项已修——第二次注册表读取非原子（漏读或改名会被误判为已卸载→逐条子键复核）、首次快照被丢弃导致“重试”无法进行（→循环回到同一记录窗口并改写说明）；P3 已修：无法列出程序时的行状态、说明措辞（仅 Windows 报告了路径的程序、8.3 短路径、永久删除询问）、移动时重新核对安装记录、部分删除失败回调、与应用同名的发布者文件夹、Doctor 区域旧文字、`MeasureShared` 公开无校验、失败后的状态行、测试缺口（内部取消、Extends 边界、未确认、发布者同名、InUse(null)、Recheck 原生检查）。复查无 P1/P2，新 P3 三项已修：`Partial` 把读不出的版本号也算作不完整，使这类电脑永远无法查找（→`Comparable` 不看版本，并在 `NotComplete` 中说明记录窗口本身显示不完整时会一直如此）；未确认应用的旧名称／发布者未计入已占用，移动前也只核对名称、不核对发布者与发布者文件夹（→`taken` 包含未确认应用，`StillRemoved` 接收 `LeftoverFolder` 并套用查找时的规则）；残留窗口自己的移动前核对未测试（→`Check` 改为 internal，原生检查同一批只读一次记录、逐项核对、拒绝时文件夹留在原处、有程序在内即拒绝）。

## 0.103 关闭一个应用的全部已列窗口；缓存环形图（F07 部分）

批次开始 `git fetch`，origin/main 仍为 Mac 1.12.9／`4166d5d`；工作区另有其他会话的 Mac Doctor 未提交改动，未触碰。`CloseWindowsSession`：原 `Request` 拆为 `TryBegin`（在同一把锁内占用该条唯一一次尝试和发送闸门）与 `Attempt`（核对快照→再查取消→`Send`），`Request` 行为不变。新 `SameApp(index)`：锁内返回与该条 `ExecutablePath` 序数完全相同、尚未尝试的条目（含自身，列表顺序）；路径不同的 helper、大小写不同的同一路径都单独算。新 `RequestEach(indices, token)`：逐条 `TryBegin`，占用失败（已尝试、越界、另一请求正在发送、会话已释放）记 null 且不消耗；令牌取消即停，其后各条为 null 且保持可请求；本条在最后核对后被取消则消耗但不发送；其他异常（含非本令牌的取消）记 Unknown，因请求可能已送达；出现 Unknown（或非本令牌取消的异常，单条 `Request` 亦然）后，会话记下该 ProcessId，此后本会话的 `RequestEach` 遇该进程条目均记新的 `HeldBack`（枚举末尾追加，只由 `RequestEach` 产生），不检查、不消耗；新 `HeldBack(index)` 供查询，单条 `Request` 仍可逐个发送；同一批中重复索引只报告一次——超时常见原因是窗口的 WM_CLOSE 正在弹保存提示，同线程消息循环仍在泵送，再发 WM_CLOSE 会在该提示内重入。`CloseWindowsReader.RequestEachAsync` 与单条一样在专用 MTA 线程运行。`CloseWindowsDialog` 新按钮 **Review All Listed Windows of This App…**：选中行所属程序路径在列表中仍有 >1 个可请求条目时启用（单窗口仍走原审阅）；本地 `requesting` 标志替代原 `pending.IsCompleted` 判断（原写法在请求自身 finally 中仍未完成，按钮无法恢复）。新 `CloseAppWindowsConfirmDialog`：Cancel 为默认和取消按钮，只读文本框列出转义后的程序路径、条数及每个窗口标题与 PID。完成后状态显示 `CloseWindows.Summary`（“Close requests finished.”，已处理／变化未发送／不确定／暂缓／未发送），新的只读“Close Request Results”文本框由 `ResultLines` 逐窗列出结果、标题（转义）和 PID，每次请求开始时清空并折叠。`Scope` 文字改为包含“一个应用的全部已列窗口”，并说明窗口关闭后应用仍可能在后台或通知区域运行；`AppConfirmation` 说明只含此列表中同一路径的窗口，不含 helper、对话框、未列出窗口和其他用户／会话，并说明某窗口 1.5 秒内未确认时该进程其余窗口不再请求。`AppCacheDialog` 的 `SystemParameters.StaticPropertyChanged` 订阅移到 `SourceInitialized`，`Closed` 解除，从未显示的对话框不会被静态事件留住。

`AppCaches.Shares`：按应用合计已测字节，只保留 >0，降序、同值按名称序数；任一文件夹未测或部分测量即标 Partial。`Sweeps` 只用正值求和分配 360°；`ShortSize` 十进制 B/KB/MB/GB/TB 一位小数。`AppCacheDialog` 列表上方新 `CacheRing`（`FrameworkElement.OnRender`，14 px 环，`ArcSegment`，>180° 用大弧，单段整圆画椭圆，多段之间留不超过 1.5° 的间隙）与图例：前 6 个应用固定配色（Fluent 蓝紫青品橙绿），其余合为一段灰色“另外 N 个 · 大小”；选中行所属应用 100% 不透明、其余 30%，图例相应 100%／55%；高对比度时一律不透明，所选应用用 `HighlightBrush`、其余 `WindowTextBrush`，靠间隙和顺序（图例顺序即自顶部顺时针）区分。中心为所选或全部已列项目的大小，有未测或部分测量即标“至少”；中心文字用继承的 `TextElement.Foreground`（随 Fluent 深浅色），前景变化、`OnDpiChanged` 和 `SystemParameters.HighContrast` 变化（对话框订阅静态事件，关闭时退订）都会重绘。刷新、移动、撤销、选择变化时更新；无测得值时整块隐藏。自定义 `FrameworkElementAutomationPeer`（Image）：读屏名称“Cache sizes by app”，帮助文本列出每个应用及大小（部分值“至少”）。无动画、无新读取。

F07 仍为部分实现（可靠应用／helper 归属仍缺）；固定 52 项仍 27／23／0／2，普通剩余 23。

## 0.102 浏览器与应用缓存移入回收站（F06 部分→已实现限定范围）

批次开始 `git fetch`，origin/main 仍为 Mac 1.12.9／`4166d5d`；工作区另有其他会话的 Mac Doctor 未提交改动，未触碰。新 `AppCaches`（Core）：13 个应用的封闭清单，路径来自各应用公开或通用的默认位置。Chrome／Edge／Brave／Vivaldi：`User Data` 下的 GrShaderCache／ShaderCache／GraphiteDawnCache，以及各配置文件里的 Cache、Code Cache、GPUCache、Dawn*；配置文件必须含普通 `Preferences` 文件，与缓存同名但含 Preferences 的文件夹按配置文件处理。Firefox：`Profiles` 下每个文件夹的 cache2／startupCache。VS Code／Discord／Slack：Electron 缓存，VS Code 另含 CachedData。Steam：`Steam\htmlcache` 下的同类子文件夹（htmlcache 本身是内置浏览器的数据文件夹，含 Cookie，不列）。另有 npm `_cacache`、pip／Yarn `Cache`、NuGet v3-cache／plugins-cache。`Read` 只读目录记录：每个文件夹 4,096 项，每个应用 64 个配置文件（文件夹确认是配置文件后才计数），共 256 个缓存文件夹、20,000 项；名称不分大小写，须是普通目录（`FolderSizes.SkippedAttributes`、名称校验）。找不到为 NotFound；路径未报告、不安全或打不开为 Unavailable；读不了的子文件夹记为 Unreadable，与达到上限的 Partial 分开。测量共用 100,000 项、约 30 秒的软预算，在剩余文件夹间公平分配；每个文件夹最多 20,000 项、四层，打开成功后才扣份额（与 Compare Common Folders 相同），失败的项目保留并写明原因。`AppCacheReader` 按 DoNotVerify 取本地／漫游应用数据路径，用 `NativeFiles.MetadataDirectory`；`RunningNames` 由 `Process.GetProcesses` 得到不分大小写的进程名集合，失败为 null。`NativeFiles.RecycleAppCache`：通用 `Recycle`（父目录 lease、不跟随重解析、固定盘有回收站、移动后核对 `$Recycle.Bin`）；移动前再查应用是否在运行；Windows 没移走时报告可能已删掉一部分。`RestoreAppCache`：应用在运行时不放回，项目留在撤销批次中。`AppCacheDialog`（`RecycleBinSession<AppCacheItem>`）：先列出查找的应用和检查的程序文件名，再分别列出没找到的、找到但没有缓存文件夹的、读不了的应用；表格为应用／配置文件／文件夹／大小／状态，不预选。状态依次显示测量问题、不在固定盘（每次刷新时用 DriveInfo 判断一次，回收站本身在移动前查询）、应用正在运行、可移动。Check Running Apps Again 只在空闲时重新列进程。入口 `doctor.appcaches`（Doctor 默认关闭，不需要所选文件）。Caches 区说明与 Doctor 范围文字改写：结果窗口里明确选择、复核、确认后可移入回收站并撤销。独立审查 P2：Steam 原先列整个 htmlcache，其中有 Cookie、Local Storage，与“保留 Cookie”的说法矛盾；已改为只列子缓存文件夹并复核。P3 已修复：预算扣减时机；说明写出 node／python／dotnet 计数和 Beta／Canary 等版本；Limits 写明单文件夹上限；关闭提示改写；恰好 64 个配置文件时的误报；与缓存同名的配置文件；固定盘状态；读不了与上限分开；撤销时检查应用；测试改进（拒绝原因、句柄释放、回收站不留残余、取消、子目录计数）。与 Mac 差异：Mac Caches 用 ClearDisk（MIT）目录数据，范围更广且按缓存条目；Windows 是自写的固定清单，只认默认位置，不读 ClearDisk。限制：仍是按进程名判断，同名的别的程序也会挡住移动；检查到移动之间应用可能启动（同用户竞态，移动后核对路径只能发现）；Beta／Canary／Insiders、Store 版、便携版、自定义缓存位置找不到；旧 WinINET 位置只能测量；未在真实 Windows 上执行。

## 0.101 ZIP64 解压（B08 部分）

批次开始 `git fetch`，origin/main 仍为 Mac 1.12.9／`4166d5d`。`ZipExtract.Read`：新 `Zip64End` 只在结束记录前 20 字节是定位记录（0x07064b50）时进入 ZIP64：定位记录磁盘 0、总盘数 1，指向的偏移必须正好是“结束记录前 76 字节”，那里是大小字段为 44（无扩展数据）、版本需求 ≤4.5 的 ZIP64 结束记录；磁盘号 0、两种条目数相等；32 位结束记录各字段为标记（0xFFFF／0xFFFFFFFF）或与 64 位值一致；目录须正好在 ZIP64 记录处结束。无定位记录时出现标记按损坏拒绝。两种解释在布局上互斥（ZIP32 要求目录止于结束记录，ZIP64 要求止于 ZIP64 记录），不会同一字节读出两套文件。`CheckExtra` 返回 `Zip64Values`：0x0001 字段长度必须正好等于被标记字段数（大小、压缩大小、本地偏移各 8 字节，起始盘 0xFFFF 时再加 4 字节且须为 0），重复字段、长度不符或有标记无字段都拒绝；空字段（无标记、长度 0）接受。目录条目的 64 位值先与压缩包长度、2 GB 限额比较再转 long。`LocalData` 对本地头同样处理并与目录核对（有数据描述符时不核对大小），返回本地头是否有 ZIP64 字段；`CheckLayout` 对有数据描述符的条目按 20／12 字节最短长度检查重叠。版本需求上限 2.0 → 4.5，并拒绝通用标志位 5（补丁数据）；2.1–4.4 间其余特性是其他压缩方法，本来就拒绝。独立审查 P2：旧版 Python（3.11.4 前 force_zip64，或约 1.9 GiB 以上文件）与 Apache Commons Compress 会在本地头写真实 32 位大小，同时附 16 字节 0x0001 字段重复这两个值；原实现按损坏拒绝，而 Python／unzip／bsdtar 都接受。现仅对本地头放宽：无标记且字段正好 16 字节、两值与本地头 32 位大小相同时接受，并按 64 位描述符长度计；`Zip64Values.WideSizes` 只在字段真带大小时为真（空字段不再加宽，P3）。已用审查者复现的两份旧版 Python 压缩包核对（3 个文件、100,007 字节，与 Python 一致）。限制：不接受带扩展数据的 ZIP64 结束记录（版本 2，目录加密）；目录中仍要求 0x0001 字段严格等于被标记项；ZIP32 压缩包若最后一个目录项恰好以定位记录签名加 16 字节结尾、正好位于结束记录前 20 字节，会被误判为 ZIP64 并拒绝（其他读取器接受；只会拒绝，不会读出不同文件）；从管道读入数据的 Info-ZIP 条目带 FIFO 类型而被拒（早已如此）；未在真实 Windows 上执行。

## 0.100 复制撤销与跨驱动器移动改为复制（B06 部分）

批次开始 `git fetch`，origin/main 仍为 Mac 1.12.9／`4166d5d`；工作区另有其他会话的 Mac Doctor 未提交改动，未触碰。版本号取 0.100.0（AppxManifest 版本各段不允许前导零，`1.00.0.0` 无效；按数值 0.100 > 0.99）。`CopyResult` 新增 init 属性 `Stamp`（`DuplicateStamp`）与 `Verified`：`Verify` 在确认目标对象就是 CopyFile2 回调句柄报告的 FILE_ID_INFO 且流名称/大小一致后，经同一句柄读取戳记；带 `FolderSizes.SkippedAttributes`（系统、设备、重解析、脱机等）的副本无戳记。新 `NativeFiles.RecycleCopy(copy, made, owner)`：通用 `Recycle`，检查单链接与 `StillCompared`；`Recycle` 移动前的 `RecycleChanged` 改报 `CopyChanged`。新 `CopyFilesResultDialog`（`RecycleBinSession<CopyResult>`）：副本、原文件、状态三列，Extended 选择，`Select All Copies`，只提供已验证、有戳记且目标盘 `RecycleBin.Available` 的副本；关闭后 App 报告移走数量并以剩余副本刷新结果。`RunCopyAsync` 可接收已选目录（跨驱动器入口）；`RunMoveAsync` 选目录后用新 `NativeFiles.CountOnVolumeOf`（先按 GetDriveType 2/3/6 排除网络/光驱，再按 FILE_ID_INFO 卷序列号计数，读取失败为 null 并照旧走移动、由移动再拒绝）：0 个在目标卷则转复制流程（`CopyFilesDialog` 的 `crossDrive` 提示），部分在则拒绝；20 批历史上限改在此判断之后，只限制真正的移动。独立审查无 P1/P2，P3（系统属性副本误标未验证、无回收站盘仍显示可移、网络盘可能阻塞、历史上限挡住复制、测试 `!= 1` 过宽、措辞与键名）已修复复核。限制：沿用 `Recycle` 的检查与按路径移动之间的同用户竞态（移动后的最终路径核对能发现但不能阻止）；不重读副本内容，修改时间被还原的改动发现不了；撤销只在窗口打开期间、只覆盖最近一批；目标仍须 NTFS，FAT/exFAT U 盘复制未做；文件夹树未做。

## 0.99 旧临时文件移入回收站（F06 部分）

批次开始 `git fetch`，origin/main 仍为 Mac 1.12.9／`4166d5d`。`FolderDirectoryBuffer.Parse` 读取 FILE_FULL_DIR_INFO 的创建(8)／写入(24)／改动(32)时间：`FolderEntry.LastWrite`、`Touched`（三者最大，任一未知为 0）；`FolderSizeNode` 增 `LastWrite`、`Touched`、`Newest`（其下任一无日期则 -1）。新 `TempCleanup.Plan`：根的直接子项无问题、完整、`Touched` 与 `Newest` 都早于 7 天才列出。新 `NativeFiles.RecycleTempItem`：持有句柄的 LastWrite 与列出时一致、FILE_BASIC_INFO 的 Touched 仍早于截止；文件再核对长度与单链接；文件夹用 `FolderSizes.Scan` 重新完整测量（句柄在移动前关闭）。通用 `Recycle` 新增可选 `failed` 回调：文件夹移动失败或仍存在时重新测量，与移动前不同则报告“只移走部分”。审查两个 P2（复制/解压文件保留旧修改时间；Windows 部分删除被误报为“未动”）均已修复复核。限制：重新测量到移动之间仍有短暂窗口；只按时间判断，不知道哪个程序还在用；撤销后的项目不再列出；仅临时文件夹，WinINET 只测量；浏览器/应用缓存规则未做。

## 0.98 常用目录跨根相似配对清理（F04 部分→已实现限定范围）

批次开始 `git fetch`，origin/main 仍为 Mac 1.12.9／`4166d5d`。`SimilarPairCleanup` 新增按键构造（`absolute` 给文件 API 真实路径、`display` 给显示与复制的转义路径）；原单文件夹构造改为调用它，报告与状态中的文件名改为完整路径（此前为相对路径）。`CommonSimilarityDialog` 以 `result.Sources` 的不透明键配对，真实路径为所读根 `Choice.Path` 加未转义相对路径；戳记来自同一键的 `Stamps`；被拒的根不会产生键。重叠根同一文件只保留首个键（其余为硬链接问题项）。限制同 0.97，另：保留者可能位于可移动盘。

## 0.97 相似配对中移走一个文件（F04 部分）

批次开始 `git fetch`，origin/main 仍为 Mac 1.12.9／`4166d5d`。`TextSimilarityResult`／`PhotoSimilarityResult` 新增 init 属性 `Stamps`（相对路径→比较时 `DuplicateStamp`：FILE_ID_INFO 卷+128 位 ID、长度、最后写入 FILETIME），只含成功比较的文件。新 `NativeFiles.RecycleSimilar(file, compared, keep, keptCompared, owner)`：保留者 `GENERIC_READ` + 仅 `FILE_SHARE_READ` 打开且戳记一致，被移文件戳记一致、单链接，再走通用 `Recycle`。新 `SimilarPairCleanup` 供两个窗口共用：所选配对的两文件列表、`RecycleBinSession<Side>`（Side 只按相对路径判等，保留移动时的伙伴）；已作为保留者的文件在窗口内不能经其他配对移走（独立审查 P2，已修复复核）。Mac 的相似文件同样从不预选、需保留另一成员。限制：不重读内容，修改时间被改回原值的改动无法发现；被移文件移动期间未锁；撤销只覆盖最近一批；常用目录跨根相似配对仍只读。

## 0.96 常用目录跨根重复移入回收站（F04 部分）

批次开始 `git fetch`，origin/main 仍为 Mac 1.12.9／`4166d5d`。`CommonDuplicatesDialog` 复用 0.95 的 `RecycleBinSession<T>` 与 `NativeFiles.RecycleDuplicate`：成员是不透明键，经 `result.Sources` 映射为（根类型、相对路径），真实路径为所读根 `Choice.Path` 加未转义相对路径；转义的显示路径只用于列表、确认、报告和复制。至少保留一个组内成员，保留候选排除已移走与当前批次；保留副本可在其他根或其他卷（卷+文件ID不同即可），被移文件仍须在有回收站的固定盘。范围文字改为“比较本身不删除……”。限制同 0.95，另：保留副本可能位于可移动盘，事后可被拔出；完整报告对已移文件会列两次（行内标注与移动小节），大量移动后更易触及 4 Mi 复制上限而整份拒绝；全部撤销后状态行仍为“未更改文件”。

## 0.95 重复文件保留已复核副本后移入回收站（F04 部分）

批次开始 `git fetch`，origin/main 仍为 Mac 1.12.9／`4166d5d`。Duplicate Files 窗口的组内文件列表改为多选，复用 `RecycleBinSession<T>`（新增 `Batch`：当前批次，供“保留副本”排除）。选择必须留下至少一个组内未移走成员，否则按钮禁用。新 `NativeFiles.RecycleDuplicate(file, length, sha256, keep, owner)`：按组内顺序尝试保留候选，`GENERIC_READ` + 仅 `FILE_SHARE_READ` 打开（拒绝写入者，移动期间不能被改写、改名或删除），须为磁盘普通文件、非目录、无重解析／离线／召回／系统／设备属性、长度与组一致，`RandomAccess` 64KiB 读取重算 SHA-256 一致；找不到即拒绝。随后进入通用 `Recycle`：持有父目录、被移文件 `nFileLinks == 1`、长度一致、文件 ID 与保留副本不同，另开共享 7 的数据句柄并核对同一文件 ID 后重算 SHA-256，一致才交给 Windows 移入回收站。Mac 同样要求一个未选、内容重新哈希相同的保留副本。限制：哈希后到 `SHFileOperation` 之间被移文件仍可被改写（保留副本已锁定，因此至少一份组内容存在）；保留副本被别的程序以写入打开时无法锁定而拒绝；常用目录跨根比较与相似配对窗口仍只读。

