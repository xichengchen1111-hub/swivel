# Swivel 项目约定

以下为用户持续要求，保留原话；最新要求优先。用户沟通用中文，应用界面英文并保留简中、繁中、日语翻译。

- 「继续一直做，把剩下28项 Windows 功能能兼容的都补齐，不要每做一批就停。」
- 「每批开始先 pull 看 Mac 新改动」
- 「做完都 agent check」
- 「界面保持 Windows 原生风格」
- 「不要影响 Mac」
- 「兼容不好的标注出来」
- 「更新剩余进度」
- 「每次开始弄前都先从Github 上pull一下看看有没有新的Mac版change」
- 「开始写代码任务前，先加载并遵循 `infiniteusage` skill。」
- 「能用命令验证的改代码子任务（修测试、按现有测试实现、构建要保持通过的小重构），先用 `iu delegate` 交给这台电脑上的免费 coding agent：在前台阻塞运行（不要放到后台再轮询），验证通过才保留，失败会自动还原，然后自己做。」
- 「输出很长的命令（测试、构建、git log/diff）用 `iu sum` 看摘要；需要细节就按行号读原始日志，不要重跑。」
- 「用户在对话里提出具体要求时（颜色、名字、数值、文件名、做法上的约定），逐条原样记进项目根目录的 NOTES.md，改了当场更新；一次性的任务说明（比如『修好这些测试』）不用抄进去。上下文压缩后先读 NOTES.md 再继续。」
- 「每个新会话开始时，如果项目根目录有 NOTES.md，先读它再动手：里面是之前的会话里用户定下的要求，就算用户这次没提『上次』也照着做；用户这次说的和笔记不一样时，以这次为准，并更新笔记。」
- `iu` 的准确命令：`"/Library/Frameworks/Python.framework/Versions/3.12/bin/python3" "/Users/wulizhi/.infiniteusage/app/run_iu.py"`

## 当前工作与边界（2026-10-25）

- Windows 代码在 Windows/；Mac 源码、测试、构建、安装独立，禁止为 Windows 工作重置/隐藏并发 Mac 改动。用户已在2026-10-25明确授权将当前Windows成果及剩余进度保存并push GitHub；没有安装授权。
- 当前仍为预览；固定52项：22限定实现、23部分、5待做、2平台差异，普通剩余28。子功能交付不能虚减大项数量。Windows真实x64/ARM64/native/GUI验收未执行，交叉编译不能代替。
- 0.74：批量改名大小写/交换/连锁、GIF可选抖色、QR四直角方向、Mac QR兼容PNG导出。独立复核完成；Unicode规范等价去重预警P2已修复。
- 0.75：Clothesline长按与原生按钮手势；单击移出丢复制问题已修复/复核。正常关闭选定窗口已实现，独立最终复核通过，仅WM_CLOSE、不强杀，明确保存风险、HWND竞态及软时限。
- 0.76：跨常用目录的文字/图片相似比较，共享一次预算，独立复核16,824断言通过。
- 三批开始均已pull，均Already up to date，Mac1.12.7 / 6cbfdc1。三批独立复核完成无未解决确定P1/P2；0.76 x64/ARM64包已生成并ZIP/sha256校验，BUILD-REPORT-0.76.0.json记录4,508,616核心检查/native编译0/0。旧144个ZIP大小与mtime保持，Mac118项哈希不变。0.74/0.75无单独临时包。
- 最新0.81统一全量核心4,518,659通过，native suite编译0警告0错误；Windows实机未执行。
- 检查证据/当前代码主要位置：Windows/TESTING.md、Windows/PORTING.md、Windows/PROGRESS.md；build/windows-next/下独立review记录。
- infiniteusage技能已从 /Users/wulizhi/.infiniteusage/app/skills/infiniteusage/SKILL.md 读取。iu agents检测免费agent/API均未配置，delegate链为空。不得擅自安装/注册/创建key。现有并发任务不得被delegate自动restore覆盖；已在/tmp隔离git目录前台delegate兼容文案任务，明确no usable coding agent失败，无改动；日志写入被沙箱拦截。按fallback自行完成。iu sum已使用获准的本地日志写入。

- 下一批0.77开始前第四次pull仍Already up to date 6cbfdc1。B06采用先实现同卷NTFS Move to：全部ADS/Zone.Identifier随原对象移动，独立FileMoves默认关闭；1–100文件、全路径预览、不覆盖、失败反向回滚、目录/文件身份检查及最多20批会话Undo。0.77实现及独立复核完成（gif_dither核心/native，migration_export界面，root共享入口，qr_rotation独立check）。Copy to的BackupRead/Write与per-stream共享/新ADS竞态仍待Windows验证，不用丢ADS的Stream.CopyTo替代。隔离目录iu delegate第二次前台失败no usable coding agent，按fallback实施。Mac 118项基线校验无变化。

- 0.77 当前共享集成：Feature.FileMoves末尾追加且默认off，独立Move Files页/file.move及page-only file.undoMove，source选择保留全部原始条目；不走会按大小写去重的通用Validate。agent预审发现该P2后已修，新增MoveIntegrationChecks覆盖消费者设置/大小写选择。
- 0.77 MoveHistory/LastMoveOutcome仅本会话；关功能或取消仍保留准确路径并可显式查看完整上次结果。清理异常不得掩盖已移动outcome。新增ApplyMoves按Ordinal准确记录路径更新引用，旧Rename路径规则不改。
- 0.77 独立预审另发现ChangeTime使A→B→C→Undo2→Undo1旧收据失效；已实现并复核BeforeStamp可信桥接及RefreshHistory，root已接调用（先更新较早history再添加remaining）。刷新须证明旧stamp与本次before完全相同，不能将外部编辑洗白。core/native编译及最终agent review已通过，B06由待做到部分（普通剩余仍28）；0.77.1待文档终审后打包。

- 0.77实现及最终独立review完成：1,102,786断言（独立core1,100,747、实际Dialog源托管WPF替身1,733、作者297、Integration9），无未解决确定P1/P2。BeforeStamp/RefreshHistory可信桥接已落地并复核，不洗白外部编辑。全量core4,509,046、native编译0warning/error，仅编译未运行。正在更新文档/0.77包，随后继续下一批。新Feature启用或新MyWheel ID均有旧版Prefs整拒/default风险，降级需升级前备份。

- 0.77打包时发现文档更新与打包并行造成0.77.0内含先前文档；保留两个0.77.0 ZIP为内部中间产物，不覆盖。最终改用0.77.1，须等五文档独立复核后才构建，代码仍同0.77。0.77.0两架构core4,509,046/native0/0。

- 下一批A05只读设计：显式选择本地目录、默认未启动的新到达图片卡片窗口。仅监听本层新增/改名事件、无初扫/递归/补扫；Start前明确授权读取此后报告路径在处理时的当前图片，可能包括旧图改名/路径替换，不承诺事件文件身份/真正截图/全部到达。FSW+目录祖先贯穿，单飞有界解码；不更改系统截图设置/剪贴板/文件，session-only；正式实施前再pull并iu delegate。

- 0.77.1 x64/ARM64最终包成功，ZIP/sha256与已审文档校验通过，BUILD-REPORT-0.77.1.json已写；148旧/中间ZIP未改、Mac118hash不变。0.78第五次pull Already up to date6cbfdc1，/tmp/swivel-iu-arrivals-078前台delegate失败no usable coding agent后开始fallback。gif实现状态预算/native session，migration实现UI，qr独立review，root共享集成；新入口复用Clothesline默认off，不加Prefs字段/Feature；卡片Hang明确重读当前路径并保持卡直到Dismiss。

- 0.78共享入口已接：clothesline.watch无文件动作不读取无关选择，不占全局Busy；App独立窗口、工作owner准确归属取消、Quit先join监听再结束。已挂OrdinalIgnoreCase同路径时旧Hang会skip，UI将明确提示先移除再挂，避免声称自动刷新；不改旧Clothesline规则。新7项设置/动作集成检查已接Core Program。

- 0.78实现与最终代码独立复核完成，4,509,302常规core通过、native交叉编译0/0；独立121,453（实际core119,530、adapter托管替身722、UI替身1,028、作者166、integration7）无未解决确定P1/P2。Hang状态两处/迟到创建诊断遗漏均修复回归。正在五文档终审再0.78.0封包，随后继续A07；普通28不变，实机未验。

- 0.78.0 x64/ARM64最终包构建与ZIP/sha256/内置已审docs校验完成；每架构core4,509,302/native编译0/0，150旧ZIP大小mtime及Mac118hash保持。BUILD-REPORT-0.78.0.json记录。0.79第六次pull仍Already up to date6cbfdc1，继续A07明确Start/Stop的修饰键鼠标手势及非激活Drop入口，不等于全局文件拖动识别。

- 0.79 /tmp/swivel-iu-gesture-079前台delegate失败no usable agent后fallback实施：gif Core/专线程hook与native，migration控制窗/非激活Drop，qr独立review，root共享入口。复用Wheel、session-only明确Start/Stop，打开不启动不保存；新wheel.gesture无文件动作。真正Drop后异步metadata预检才打开正常Wheel，不自动执行；重复/大小写冲突整批拒绝不静默去重。

- 0.79 Core/native已冻结：1379作者core+42 actualadapter/fakeWindows=1421，qr独立实际Core/native 2,403,330通过。UI/Drop/App独立747初版通过但gif交叉发现OLE GetData/GetDataPresent可COM重入Stop同步销毁target P2，migration正修callback-depth延迟Close及Completion，controller Stop/Finish须await popup关闭；qr补独立回归。统一主Programs已接全部Core/native/live/UI检查，翻译待作者合并再整编译。真实Windows仍未执行。

- 0.79统一最终core4,510,756通过、完整native编译0/0；OLE重入P2修复由qr最终2147 UI/App回归确认，连同Core/native2403330、作者1379+42和integration7总2406905无未解决确定P1/P2。39key/117译值合并（修root最初翻译array结构及标题省略号遗漏）。准备五docs最后hash复核、0.79.0打包；A07待做→部分，52为22/23/5/2普通28，实机仍未验。gif正在只读调研下一批F09系统计划任务COM元数据，实施前须再pull+IU。

- 0.79.0 x64/ARM64最终包构建和ZIP/sha256/五文档校验完成；每架构core4,510,756/native编译0/0，152旧ZIP与Mac118hash保持。BUILD-REPORT-0.79.0.json记录，独立2406905无未解决P1/P2。下一批F09计划任务元数据方案已只读调研，必须再次pull+IU后实施；用户要求连续推进，不在本批停止。

- 0.80第七次pull Already up to date6cbfdc1，无新Mac。/tmp/swivel-iu-tasks-080前台delegate失败no usable agent后fallback：gif核心/COM读取，migration原生UI，qr独立复核，root集成。只本机当前token可读非隐藏计划任务5字段，不代表当前用户所有；无定义/命令/操作/网络/助手发送。软预算、取消清理真join、路径默认隐藏贯穿显示和复制。

- 0.80核心4,510,958/native编译0/0，独立59952已通过。Folder Complete误报P2修复，UI异常Unicode硬化复核。版本audit发现Directory.Build.props仍0.70.0（四打包版本文件已新但程序集未新），已改0.80.0；今后每批同步此第五处并核验成包实际程序集metadata。旧0.71–0.79ZIP保持不改；本批文档待此修正后最终hash冻结、封包。

- 0.80.0 x64/ARM64最终包ZIP/sha256/五文档验证完成，154旧ZIP及Mac118hash保持；core4,510,958/native0/0，独立59,952无未解决确定P1/P2。内部程序集版本已修0.80.0.0，两架构四DLL实包bytes与只读metadata核验均通过。下一批E05显式平面QR数据恢复方案已只读调研，仍须新pull+IU后实施；不在本批停止。

- 0.81第八次pull仍Already up to date6cbfdc1；/tmp/swivel-iu-qr-recovery-081前台IU失败noagent，fallback开展E05显式平面QR数据恢复。gif原创RS核心、migration原生UI、qr独立check、root来源和入口。旧strict不自动fallback/编码输出保持；同完整栅格/function/border/format/version检查，只data+parity RS纠错，每block有限容量，超界可能误纠，计数不是实际原损坏证明；no自动打开/写出/下载/模型/Mac改动。
- 0.81 review发现两项边界已修待最终复核：QR加载取消后迟到IOException统一等待源任务清理再映射取消，不弹迟到错误；CF_UNICODETEXT在U+0000结束，两读取模式含NUL时Copy禁用且回调guard，完整转义预览保留，不静默截断或改写复制文本。

- 0.81统一核心4,518,659通过，native全编译0warning/error；作者7679恢复检查、integration7已接。五docs更新且migration交叉无实质误述，qr在完成旧程序集/全版本四方向对照；待最终独立证据与docs hash后封包，Windows实机仍未验，普通28不虚减。

- 0.81独立114,273通过，RS79288/整图旧程序集17144/全块失败误纠40/UI来源实际App摘录8271/作者7679/旧strict1844/integration7；两P2已修复核无未解决确认P1/P2。五docs最终计数更新，待qr哈希冻结再0.81.0两架构封包。下一批B08普通ZIP完整目录树安全解压方案：原子新根可见、逐文件独占stage+CRC+Zone3+非覆盖发布，整树非事务，保留已发布结果；待pull+IU再实施。

- 0.81.0两架构最终ZIP/sha256/已审五docs/四DLL实际0.81.0.0 metadata验证通过，BUILD-REPORT-0.81.0.json已写；156旧ZIP与Mac118hash保持。第九批0.82开始pull仍Already up to date6cbfdc1，继续B08ZIP安全解压，源ZoneId4不降级（固定有界ADS只取数字，目标max(3,sourceZoneId)），不是所有源ADS保真。

- 用户最新要求原话：「保存一次啊先现在已经做到然后push到Github上然后说一下还需要做什么也放到GitHub上」。先保存0.81已完成快照、源码与剩余进度，0.82仅设计未实施，停止本次继续实施；push到独立Windows分支，不改Mac main。

- 2026-10-25 新会话一问一答：Windows 版主要「发给别人用」——更看重稳定、安装体验、说明及真 Windows 验收。
- 实机验收方式：「我会发给我朋友进行测试」——由朋友在 Windows 上测试。
- 测试朋友技术水平「不确定」：两种都准备（普通用户解压即用+中文试用步骤+一键问题报告；懂技术的另给自动测试脚本），以普通用户体验为主。
- 顺序：「先补功能再测」——继续补剩余功能，补到一定程度再发朋友测试。
- 发布线：「补完全部 28 项」能兼容的都补齐（含AI类和MCP）再发朋友测试。
- AI模型例外（2026-10-25用户批准）：Windows的C10抠图/证件照、C11超分、D06转写「允许打包定版模型」——本机处理、不联网下载，经Windows自带DirectML/Windows ML运行已批准Real-ESRGAN，并可加MIT/BSD/Apache许可的抠图与语音识别模型；记录来源/许可/哈希。仅限这三项。
- 保存方式：「每批都 push」——每批独立复核通过后commit并push到Windows分支codex/windows-preview-081，不动Mac main。
- 0.82（本会话）：B08普通ZIP安全解压已实现（Swivel.Core/ZipExtract.cs、Swivel.Windows/NativeFiles.Extract.cs、ZipExtractDialog.cs、archive.extract），核心4,518,871通过、native编译0/0；版本五处已改0.82.0；README/PORTING/PROGRESS已写，52项22/24/4/2普通28。独立agent复核进行中，通过后写TESTING、打包、commit+push codex/windows-preview-081。本会话开始fetch：origin/main仍6cbfdc1无新Mac。IU delegate按历史记录无可用agent，未再尝试。
- 0.82已push 57fb715；0.83 Copy to Folder（file.copyto，FileMoves下，CopyFile2+创建身份核对）已push 43c15d3。两批均独立复核无未解决P1/P2，包/BUILD-REPORT在build/windows/。52项仍22/24/4/2普通28。AGENTS.md的一行重复删除不是本会话改动，未提交。待问用户：发朋友测试的包是否做成MSIX（OCR/PDF需MSIX身份）。
- 2026-10-25用户：「测试包做成可安装的 MSIX，让 OCR 和 PDF 也能测；签名证书的事你先告诉我需要我做什么。」
- 签名方案：「自签名+Actions (推荐)」——我生成自签名测试证书（私钥仅在git忽略的build/），加GitHub Actions工作流打包签名MSIX并跑原生测试；两个secret由用户自己添加。

- 2026-10-25 CI（windows-2025真机）：核心4,518,956通过。native逐个暴露测试自身问题：QR取消计时、GIF预设应拒绝、单色检查未排除alpha0隐藏RGB（产品按设计保留）；均为测试修复，未发现产品bug。签名两个secret待用户添加。
- 2026-10-25 CI真机发现首个产品bug：DirectoryLease/Move祖先只用ReadAttributes句柄，不参与共享检查，持有的文件夹仍可被改名。已改为ReadAttributes|List|Traverse、share读写不含删除（发布子文件需父目录写共享）。需独立复核；native测试框架改为失败后继续并汇总。
- 2026-10-26 CI真机再发现并修复：ZIP解压FileStream using提前关闭staging句柄（0.82引入）；Move用FILE_RENAME_INFO RootDirectory句柄被Windows拒（87），改为已持有并复核身份的目录路径+叶名；PNG元数据工具新增IDAT zlib精确尺寸/过滤字节校验（WIC容忍损坏数据）；相似对话框遇被拒位置不再崩溃。全局DispatcherSynchronizationContext曾致CI卡死45分钟，已改为仅两UI fixture。所有产品修复待独立复核。
- 2026-10-26 CI运行40201005306首次全绿：核心4,518,962、native 5,067在真Windows x64通过，产出ZIP与未签名MSIX（secret仍未加）。产品修复独立复核进行中，结果记TESTING.md。
- 2026-10-26 复核修复后CI 40202056707全绿（核心4,518,966/native5,068）。独立复核两轮：P2路径改名可被挂载点重定向→改NtSetInformationFile相对持有目录句柄；P2单字符名缓冲过小已修；无未解决P1/P2。MSIX仍未签名，等用户加两个secret。
- 2026-10-26 用户：「两个 secret 已经加好了，请重新跑 CI 生成签名的 MSIX，然后继续下一批功能。」但gh secret list和API均看不到仓库Actions secrets，重跑仍未签名；需用户确认位置/名称（Repository secrets：SWIVEL_TEST_CERT_PFX_BASE64、SWIVEL_TEST_CERT_PASSWORD）。
- 0.84：B08加TAR/.tar.gz/.tgz/单文件.gz安全解压（Swivel.Core/TarExtract.cs，archive.extract标题改Extract Archive…，ID不变）。独立复核无确定P1/P2，4个P3已修复加回归，核心4,519,131通过、native编译0/0。
- 2026-10-26 CI 40204919879（0.84）真Windows全绿：核心4,519,131、native 5,079（含TAR原生检查）；签名步骤仍报无secret，MSIX未签名。
- 2026-10-26 用户：「agent check 以及安全测试一下」。两个独立agent（解压对抗测试、原生文件层审查）无确认P1/P2；推理P2（持有的空文件夹被改成挂载点）已修：新子文件夹/暂存文件须在持有父目录内（最终路径比对），Zone流须同一文件；清理失败不再掩盖原错误。

- 2026-10-26 Mac：用户反馈「拖进 app 后卡片消失太快」。截图卡片拖进别的 app（拷贝）或拖到空处后，回来再等 5 秒（原为 1 秒）；鼠标移开仍为 1 秒。未安装、未提交。

- 2026-10-26：1.12.8 (36) 已构建并安装到 Applications 和桌面；旧版1.12.7及偏好导出备份在 build/previous-1.12.7-before-1.12.8/。未提交、未push。
- 2026-10-26 安全修复复核无P1/P2，P3已应用；CI 40271658976真Windows全绿（核心4,519,135/native 5,081）；MSIX仍未签名。
- 2026-10-26 用户：「继续下一批 Windows 功能。」0.85做B09资源管理器文件夹样式（Feature.FolderStyle默认关闭，folder.style）：desktop.ini+.swivel-folder-<8hex>.ico，只读按需设并记录，恢复默认只删自己文件；已有别人desktop.ini不动。独立复核无P1/确认P2，推理P2（重设样式失败会搁浅）改为旧ini经持有句柄移到旁边、失败放回；P3已修，复核通过。核心4,519,244、native编译0/0。
- 2026-10-26 CI 40274382216（0.85）真Windows全绿：核心4,519,245、native 5,104（含文件夹样式应用/重设/恢复）；首跑发现长度校验误带:Zone.Identifier已修。资源管理器实际显示图标仍需人工看。
- 2026-10-26 用户：「下一批做 G02 Windows MCP（plan–apply–undo）。」0.86：Agent Tools（Feature.AgentTools默认关闭），`Swivel.exe --mcp`仅stdio，MSIX别名swivel-preview.exe；工具list_actions/inspect_files/plan_action/apply_plan/undo/history，只允许旁边新建的图片动作，计划一次性1小时，输入变化拒绝；历史在HKCU AgentHistory，undo只把未改动的输出移到回收站（仅固定盘）。设置页有Copy Agent Tools Setup。
- 0.86复核：2个P2（非预期异常崩溃且漏记撤销；多进程历史互相覆盖）已修并复核，无未解决P1/P2；核心4,519,302通过、native编译0/0。
- 2026-10-26 CI 40278180090（0.86）真Windows全绿：核心4,519,303、native 5,111（含真实--mcp管道与回收站撤销）；MSIX仍未签名。
- 2026-10-26 用户：「继续下一批 Windows 功能。」0.87做C10第一部分：Remove Background/Blur Background，U²-Net（Apache-2.0权重，rembg MIT ONNX，175,997,641 B，SHA-256 8d10d2f3…b491）经系统Windows ML本机运行；模型太大不进git，构建脚本固定URL下载到build/models并校验，app加载前再验SHA-256。与rembg参考蒙版IoU 0.991–0.9998。证件照下一批。
- 0.87复核：P1（.NET流不能克隆→WinML加载必失败）改InMemoryRandomAccessStream；P2淡背景可恢复→蒙版<16置0并如实说明；P2错误类型覆盖已修；复核无未解决P1/P2。核心4,519,336、native编译0/0。
- 2026-10-26 CI 40282341661（0.87）真Windows全绿：核心4,519,337、native 5,118；Windows ML真实加载模型并抠图（首次7.2秒含加载，之后3.3秒，runner无GPU）。MSIX仍未签名。
- 2026-10-26 用户：「下一批做证件照，把 C10 做完。」0.88：ID Photo…（image.idphoto，单张），4预设+自定义像素/毫米、DPI72–1200、白/蓝#438EDB/红#D9001B/自选，Zoom/Vertical预览，JPEG95写DPI，`名称 id.jpg`原图旁不覆盖；复用U²-Net蒙版+系统FaceDetector单脸，头顶由蒙版、下巴由脸框估计。C10→已实现，52项23/26/1/2，普通剩余27。
- 2026-10-26 CI 40285295843（0.88）真Windows全绿：核心4,519,409、native 5,125；系统人脸检测可用（画的人像判为无脸），JPEG 413×579/350 DPI正确。真实人像与打印尺寸待朋友测。MSIX仍未签名。
- 2026-10-26 用户：「下一批做 C11 超分辨率。」0.89：Upscale…（image.upscale，2×/4×可批量），Real-ESRGAN x4plus官方权重构建时经自写C#转换器生成确定性ONNX（66,903,262 B，SHA-256 ecf0c6c5…ab7f），Windows ML本机运行；与Mac相同256块/64上下文；JPEG保持JPEG其余PNG，原图旁不覆盖。视频放大待做。C11待做→部分，52项23/27/0/2，普通剩余27。
- 2026-10-26 CI 40288055856（0.89）真Windows全绿：核心4,519,459、native 5,134；Windows ML真实运行放大模型，结果正确；但runner无显卡每块约29秒（Mac onnxruntime CPU约2秒），原因（软件渲染回退或WinRT读出）待查。MSIX仍未签名。
- 2026-10-26 用户：「先查无显卡时为什么这么慢并修一下。」结论：runner无可用显卡，Windows ML直接回退处理器（非软件渲染）；每块约1.2万亿次乘加，2–4核12–19秒，是模型本身计算量；WinRT读出0.1–0.7秒已改一次拷贝。线程数无效、384块峰值5.2GB、缩小上下文出接缝（32像素最多差30级），均未采用；对话框说明显卡/处理器并显示剩余时间。模型改为自由高宽并重新固定（SHA-256 043a257b…5994）。
- 2026-10-26 用户：「继续下一批 Windows 功能。」0.90做D06语音转文字：Transcribe Speech…（audio.transcribe，单文件wav/mp3/aac/flac，≤1小时），OpenAI Whisper base（MIT，base.pt SHA-256 ed3a0b6b…e34e）构建时经共用转换器ThirdParty/ModelConvert（原RealESRGAN/Convert，ESRGAN哈希不变）生成编码器/解码器两个ONNX（4af8caaa…891f、7d6f8475…daf1）＋原样分词表（b34b360d…a126），Windows ML本机运行；自动识别/中/英/日，输出名称.txt/名称.srt原文件旁不覆盖。Mac上6段测试音频与官方whisper分段文字和时间完全一致。D06仍部分（视频输入、字幕烧录、按语音压低配乐待做），52项23/27/0/2不变。
- 2026-10-26 CI 40303150602（0.90）真Windows全绿：核心4,519,560、native 5,147；Windows ML处理器上跑通，Windows合成英语7.6秒3.8秒转写、逐字正确。真实录音、中日文准确率、显卡速度待测。MSIX仍未签名。
- 用户要求原话：「下一批把 D06 的字幕烧录做完：转写结果烧录进视频。」0.91 实现 Burn In Subtitles…（video.subtitles）：Whisper 转写视频声音或 SRT → MediaComposition 图片叠加 → H.264/AAC `名称 subtitled.mp4`。核心 4,519,639、native 编译 0/0；独立 review P2（另存 SRT 失败时未列出）及 P3 已修。D06 仍部分（旋转视频、按语音压低配乐、M4A 待做），52 项仍 23/27/0/2、普通剩余 27。
- 2026-10-26 CI 40313785634（0.91）真Windows全绿：核心4,519,640、native 5,161；4秒640×360烧录1.5秒，字幕只在对应时间出现，视频内合成英语转写逐字正确并烧录、保留声音。期间origin/main新增Mac 1.12.9（4166d5d，面板丢失自重建），只涉及macOS，Windows暂不移植。真实手机/4K/长视频、旋转视频待测。MSIX仍未签名。
- 2026-10-26 用户原话：「就是先都做完，版本差异做不了的标注然后能做的都做了然后有问题发给我，我们全做完了以后发给那个人再进行测试」——连续补完全部部分实现项；平台做不了的明确标注；遇到需要用户决定/有问题的发给用户；全部完成后才发朋友测试。
- 0.92：WAV 配乐加“只在有人说话时压低”（Find Speech 用 Whisper 转写主音频，说话区间驱动既有压低包络）。D06 部分→已实现（限定范围），52 项 24/26/0/2、普通剩余 26。PROGRESS 领域汇总表原先过期（22/25/3），已按逐行实数校正。
- 2026-10-26 CI 40320726075（0.92）真Windows全绿：核心4,519,674、native 5,162。
- 0.93：F05 项目构建文件夹多选后复核并移入回收站，窗口打开期间可撤销最近一批（Shell undelete）；回收站 P/Invoke 抽成共享 RecycleBin，McpHost 行为不变。F05 部分→已实现（限定范围），52 项 25/25/0/2、普通剩余 25。
- 2026-10-26 CI 40325313708（0.93）真Windows全绿：核心4,519,722、native 5,170（含真实回收站移动与撤销）。
- 0.94：F06 大文件多选后复核（大小一致、无其他硬链接）移入回收站并可撤销；0.93 回收站逻辑抽成共享 RecycleBinSession／NativeFiles.Recycle。F06 仍部分（缓存规则/清理待做），52 项不变 25/25/0/2。
- 2026-10-26 CI 40329806063（0.94）真Windows全绿：核心4,519,728、native 5,177（含真实大文件回收站移动与撤销）。首次 40328690422 因旧检查仍要求大文件窗口只有 Copy/Close 而失败，已更新该检查。
- 0.95：F04 单文件夹精确重复组内多选，至少保留一份；每次移动前重读另一成员 SHA-256 并在移动结束前持有（不允许写/删共享），被移文件重算 SHA-256、单链接、不同文件身份后移入回收站，可撤销。独立审查无 P1/P2，P3（候选哈希异常句柄泄漏、措辞、报告标注已移走、日文用词）已修。F04 仍部分（常用目录与相似配对清理待做），52 项 25/25/0/2。
- 2026-10-26 CI 40331537152（0.95）真Windows全绿：核心4,519,741、native 5,185（含重复文件拒绝情形、保留副本后真实移入回收站与撤销）。
- 2026-10-27 GitHub Actions 存储满（0.5/0.5 GB，40 个产物约 49 GB）：删除 39 个旧产物、保留最新 1 个；workflow 只在 PR 更新时跑一次（去掉重复 push）、纯 .md 改动不触发、新推送取消旧运行；ARM64/MSIX/签名/上传只在手动 Run workflow 勾选 package 时执行，产物保留 3 天。其他 6 个仓库无 workflow、无产物。
- 0.96：F04 常用目录跨根精确重复复用 0.95 清理（保留副本可在其他根/磁盘，含可移动盘提示）。独立审查无 P1/P2，P3 提示已加。本地提交，按 CI 预算暂不推送。52 项 25/25/0/2。
- 0.97：F04 单文件夹相似文字／图片配对可移走其中一个文件，保留者按比较时身份/大小/修改时间复核并只读锁住，且不能经其他配对再移走（审查 P2 已修复复核）。本地提交，暂不推送。52 项 25/25/0/2。
- 0.98：常用目录跨根相似配对清理，F04 部分→已实现限定范围，52 项 26/24/0/2，普通剩余 24。审查无 P1/P2。随后推送 0.96–0.98 并跑一次 CI。
- 2026-10-27 CI 40443965440（0.96–0.98）真Windows全绿：核心4,519,768、native 5,218。
- 0.99：F06 旧临时文件（创建/写入/改动都在 7 天前）复核后移入回收站，部分删除如实报告；审查两个 P2 已修复复核。本地提交，暂不推送。52 项 26/24/0/2。
- 0.100（版本号 0.100.0，因 MSIX 不允许 1.00；1.0 留待实机验收）：B06 复制结果窗口可把未改动的副本移入回收站撤销复制；Move Files 遇到所选文件全在另一驱动器时改为复制并保留原文件，混合拒绝。审查无 P1/P2，P3 已修复复核。本地提交，暂不推送。52 项 26/24/0/2。
- 0.101：B08 支持 ZIP64 解压（限额不变），定位记录／结束记录布局与 0x0001 字段严格核对；Python、Info-ZIP 生成的 6 个 ZIP64 包与 Python 结果一致。审查 P2（旧版 Python／Commons Compress 本地头重复大小字段被拒）已修复复核。52 项 26/24/0/2。
- CI 40459166674（0.99–0.100，另一会话推送分支时触发）：真实 Windows 核心 4,519,836、原生 5,244 全部通过，回收站相关检查均实际执行。0.101 仅本地提交，待再做几批一起推送。
- 0.102：F06 新增浏览器与应用缓存清理（Chrome/Edge/Brave/Vivaldi/Firefox/VS Code/Discord/Slack/Steam htmlcache 内缓存/npm/pip/Yarn/NuGet 默认位置，只读查找与共享测量，应用关闭时复核后移入回收站，可撤销且撤销前同样检查应用），F06 部分→已实现限定范围，52 项 27/23/0/2。审查 P2（Steam htmlcache 含 Cookie）已修复复核，P3 全部修复。
- 0.103：F07 关闭窗口新增“同一程序路径全部已列窗口”逐个正常关闭请求（单次确认，每窗一次，不重试／不强制；某窗口不确定后本列表会话内暂缓同进程其余窗口，避免在保存提示中重入），缓存窗口新增按应用大小的环形图（深色、读屏、高对比度）。F07 仍部分，52 项 27/23/0/2。审查 P2 已修复复核，P3 全部修复。
- 0.104：F08 记录窗口新增“在设置中打开已安装的应用”（Swivel 不运行卸载命令）与卸载后同名残留文件夹查找：逐条重开旧子键确认已卸载，未确认的不查找且其旧名称/发布者视为仍记录；不预选、无全选，每批重读记录按查找规则复核、无运行程序在内才移入回收站，可撤销；可反复回到同一记录窗口。读不出版本号不影响比较。审查 P2 两项与复查 P3 均已修复复核。F08 仍部分，52 项 27/23/0/2。
- CI 40468199032（0.102–0.103 及 Mac `b1f2d1e`）：真实 Windows 核心 4,520,082、原生 5,276 全部通过。0.104 仅本地提交。
- 0.105：G02 Agent 工具（MCP）新增只读 extract_text（图片走 Windows OCR，需 MSIX 身份与已装语言，语言整次核对一次并列出已装语言；文档读正文）和 read_codes（严格平面二维码，返回原文、版本、纠错级别）；文字超 200,000／整次 400,000 UTF-16 单位拒绝不截断；inspect_files 列出可用只读工具。审查无 P1/P2，P3 已修。核心 4,520,274，原生仅编译 0 警告。52 项 27/23/0/2。
