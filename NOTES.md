# Owner requirements (keep verbatim; latest word wins)

## Natural-language file search (2026-10-27)

- Mac 和 Windows 自带搜索又慢又不准，要做「自然语言超强搜索」。
- 第一阶段先做 Mac 原型：Quick Actions 里加自然语言搜索。
- 解析用 Foundation Models（macOS 26），查询走 Spotlight（NSMetadataQuery）。
- macOS 15 用规则兜底（无 Foundation Models 时）。
- 沿用项目规矩：无第三方依赖、不加新权限、不默认把内容发到云端。
- 已做（2026-10-27，未提交）：`Sources/Swivel/FileSearch.swift`、`FileSearchStrings.swift`、Quick Actions 接入、设置页勾选「Search files too, in plain words」（`Prefs.fileSearch`，默认开）、`Swivel --find`、`FileSearchTests.swift`。回车打开，⌘回车在访达显示，结果可拖出。
- 构建验证在 Mac mini（`ssh melbourne-mac-mini`，目录 `~/swivel-nlsearch`）；Windows 检出的 CRLF 会弄坏模型/MCPB 文件，需用 `git -c core.autocrlf=false archive` 同步。
- 未验证：Foundation Models 实际运行（测试机 Apple 智能关闭）、macOS 15、桌面/文稿/下载的访问授权弹窗、真实键盘和拖拽。
- 用户（2026-10-27）：「先在我们mac上试一下，然后做个win版本可以吗」——已装到 Mac mini 的 /Applications/Swivel.app 并运行；Windows 版在 `windows/Swivel/Tools/FileSearch.cs`（Windows Search SystemIndex，ADO COM），Quick Actions 接入，Ctrl+Enter 在文件资源管理器显示，微信/QQ/钉钉文件夹别名。
- Windows 版已做（2026-10-27，未提交）：`FileSearchTests.cs` 9 项，全套 329 项通过；`--find` 实测 0.2–0.4 秒。未手动试面板（Ctrl+Enter、拖出）。
- 用户（2026-10-27）问「能搜最近一些大学的文件吗」「抽象的能搜到吗？很多时候搜索都很抽象」，然后：「都帮我做了吧，按照实际情况来做就行，因为实际上搜的时候很多情况下都是很抽象的提示词比如上周保存的领导给的工作内容这些类型的，需要也能处理抽象问法」。
  - 第一步（Mac + Windows）：内置概念词表 + 中英互译扩展（找工作→简历/resume/CV/推荐信…，大学→lecture/assignment/unimelb/作业/课件…，复习→exam/cheat sheet…），词也匹配文件夹名，去掉 anaconda 等杂项；“领导给的/老师发的/别人发的”= 收到的文件（下载 + 微信/QQ/钉钉文件夹）；“保存的”= 加入日期。
  - 第二步：选定文件夹的本地语义索引（按意思找）。Mac 用系统自带嵌入；Windows 需要带小的开源模型——用户回复「都帮我做了吧」视为同意，但下载模型文件前仍要说明文件名/来源/大小并确认。
  - 已做（Windows）：`FileSearch.cs` 概念词表 `ConceptGroups`（与 Mac 同表）、`Received`、文件夹名匹配、短英文词整词匹配、过滤条件兜底；`SemanticIndex.cs` 用 Windows 自带 ONNX 运行时跑 multilingual-e5-small（`windows/ThirdParty/MultilingualE5`，模型不进 git，`fetch.ps1` 按 SHA-256 下载），文本来自 Windows 搜索的 AutoSummary（不打开文件），索引在 %LOCALAPPDATA%\Swivel\SemanticIndex；设置 `Prefs.SemanticSearch`（`quickActions.semanticSearch`，默认开）；`Swivel --index ["请求"]`。
  - 已做（Mac 第一步，后台助手）：同一张概念表、received（kMDItemWhereFroms/DownloadedDate + 下载文件夹第二个查询）、短词整词、文件夹加分；12 项 FileSearchTests 通过。Mac 第二步已做：`Sources/Swivel/SemanticIndex.swift`，系统自带 NLEmbedding 词向量（en + zh-Hans，只取内容词平均，减去各语言均值，按每个词组单独打分），正文来自 PDFKit 前 3 页 / docx·rtf·odt / 纯文本（每个文件最多 4000 字，不跟随符号链接，不下载 iCloud 占位文件，pptx/xlsx 跳过），索引在 Application Support/Swivel/SemanticIndex（0700/0600，int8 向量，不存文字）；`Prefs.semanticSearch` 默认开；`Swivel --index`。实测 7,878 个文件首次 245 秒、增量 0.7 秒。弱点：中文短文档分数普遍偏高；纯中文词只能靠概念表连到英文文档。未验证：真实设置窗口和面板、桌面/文稿/下载授权弹窗、pptx/xlsx、日文、macOS 15。Mac mini 上留有测试目录 `~/swivel-nlsearch/semantic-fixtures`、`semantic-index-dev`、`semantic-index-release`、`~/swivel-probe`。
  - 验收例子：「上周保存的领导给的工作内容」「找工作相关的东西」「操作系统复习资料」「最近的大学文件」「最近的作业」。
- 用户（2026-10-27）问「像是有一天我和XXX说过的XXX这种非常抽象的能操作吗？先别做只跟我说」——只回答了，未做（聊天截图 OCR、「和X说过」、Mac 邮件；微信/QQ 加密数据库不碰）。
- 用户问「那我说比如前两周存的一个密钥能直接找出来吗」，接着问「语义是不是有点问题？比如时间和内容要分开？」——已改（Windows；Mac 交给后台助手同步）：
  - 语义只比较内容词（`Plan.Topic`）：时间、存/改/开的日期、谁发的、位置、类型、填充词只当筛选，不进向量。
  - 「前N天/周/个月/年」= 过去 N（「前几天」仍是最近，「前天」不变）。
  - 新概念「密钥」组（密钥/秘钥/私钥/密码/凭证/api key/token/password/secret…），只匹配文件名和文件夹名，不看正文，不走语义。
  - 排除企业微信内嵌浏览器缓存（qtCef、*CefCache）。
  - 实测：「前两周存的一个密钥」→ 0 条（最近两周确实没存）；「今年存的密钥」→ keys 文件夹、agentmail_api_key、ClawdKeys。全套 336 项通过。
  - 仍弱：「上周保存的领导给的工作内容」第一条还是简历（e5 认为简历≈工作）。
- 用户（2026-10-27）：「我们别写死就行，比如都按前N周就不行，换个说法就烂了」——时间（以及其他条件）的理解不能靠逐条列举说法；要组合式解析（方向 + 数量 + 单位 + 具体日期），换说法也要能懂；没读懂的时间词不能变成内容词；时间边界放宽、按远近排序，读得稍偏也不至于全错。
  - 已做（Windows，2026-10-27）：`windows/Swivel/Tools/FileSearchTime.cs` 组合式时间阅读；`Period` 加 Calendar/Date/Weekday/Season、Part、Loose、Onward、Window()；排序时落在时间范围内的 +15；测试 `TimesAreReadFromTheirPartsHoweverTheyAreSaid` 约 70 种说法全过，全套 337 项通过。Mac 已移植（`Sources/Swivel/FileSearchTime.swift`，同一批约 70 种说法测试），Mac mini 上 `FileSearchTests|SemanticIndexTests` 20 项通过。
  - 南半球（AU 等）季节对调：夏天 = 12–2 月。
- 以后阶段（未做）：可选 Everything IPC（Windows）、用户选定文件夹的语义内容索引（NLContextualEmbedding / PDFKit / Vision OCR）、FSEvents/USN 增量索引。
- 用户（2026-10-27）：「提交这批搜索改动到 windows-port 分支（不推送）」——已提交到 windows-port（未推送）：先单独提交了 10-21 的 Windows 1.4 移植（文件工具/二维码/App 操作/工具轮，320 项测试通过），再提交搜索改动。
