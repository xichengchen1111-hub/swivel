# 先补高频工具，再把 Swivel 做成 Mac 医生

Swivel 下一步最值得做的，是先补上四项文件工具：**本地抠图、按目标大小压缩、RAR/7z 解压、防休眠**。这四项搜索量高，用系统框架就能做，也不新增任何隐私权限。之后再按“工具优先”的路线，把 Swivel 做成一个“Mac 医生”：只读诊断、解释原因、用户确认后才动手，而不是去和巨头正面比“替你操作电脑”。需求证据如下。“remove background”这组关键词在美国每月约 **82.3 万次**搜索，remove.bg 九月约 **5,420 万次访问**。“video compressor”每月 **9.05 万次**，印度市场另有大量“压到 100KB”的刚需。知乎“Mac 上最方便的压缩与解压缩软件”约 **391.9 万浏览**，是中文 Mac 问题里最大的单题。Raycast 上的防休眠扩展装机 **15.6 万**，在 3,437 个扩展中排第 15。文件之外，全球和中文社区的头部难题相同：存储与“系统数据”、卡顿与强制退出、“打不开/已损坏”、外置硬盘、网络。这些问题大多不需要特殊权限就能诊断，修复多半是打开正确的系统设置，或者由用户确认一步操作。另一方面，Anthropic、OpenAI、字节、阿里都已发布能看屏幕、能点鼠标的桌面代理，最强模型在 OSWorld-Verified 上约 85%，提示词注入仍未解决。Swivel 的机会在两处：一是通过 App Intents 和 MCP，把自己确定、可预览、可撤销的本地工具交给任何代理调用；二是用一个可换模型的层驱动自己的诊断。在中国大陆，这一层默认用 DeepSeek 或 Qwen，因为 Apple Intelligence、Claude、Gemini 在大陆都不可用。MP3 输出虽然是转换类的头号关键词，但 macOS 没有 MP3 编码器，现成编码器又都是 LGPL，应明确不做。以上排序主要依据美国 Google Ads 数据和第三方估算。Google Trends、百度指数、Reddit 在调研中被限流或拦截，中文需求只能用知乎和 B 站的累计浏览作下限参考。

## 抠图、按大小压缩、RAR 与防休眠是最大的文件空白

衡量文件工具需求，最一致的数据是 Google Ads 的美国月均搜索量（由 seodata.dev 转述，按近义词合并成组，**组与组之间不能相加**），再用 Similarweb 的单月访问量和 Homebrew、Raycast 的安装数交叉核对。按这套口径，Swivel 1.4.3 还没有的功能里，**背景移除遥遥领先**。“remove background / background remover / remove bg”同属一个需求池，美国每月 82.3 万次 ([seodata](https://www.seodata.dev/keyword/remove-background))。全球 Google Trends 上它是“compress pdf”的 **1.63 倍**，2026 年迄今是五年来最高的一年 ([Google Trends P04](https://trends.google.com/trends/explore?date=today%205-y&q=compress%20pdf,remove%20background,compress%20image,resize%20image,heic%20to%20jpg))。remove.bg 单站 2026 年 9 月有 5,420 万次访问，最大来源国是印度 ([Similarweb](https://www.similarweb.com/website/remove.bg/))，Photoroom 网页版另有 1,870 万 ([Similarweb](https://www.similarweb.com/website/photoroom.com/))。需要冷静看待的是：macOS 的 Finder 已自带“移除背景”快速操作 ([Apple](https://support.apple.com/guide/mac-help/perform-quick-actions-in-the-finder-on-mac-mchl97ff9142/mac))，带“mac”字样的同类搜索每月只有 170–320 次 ([seodata](https://api.seodata.dev/v1/keyword?q=remove+background+mac&country=us))。所以 Swivel 版本必须比系统做得多：拖到轮盘一次完成、可批量、可选输出格式，结果直接交给现有的 Add BG 加衬底。技术上，`VNGenerateForegroundInstanceMaskRequest`（macOS 14+）在本机 0.13 秒就把示例照片抠成干净的透明 PNG，不弹授权也不联网 ([Apple](https://developer.apple.com/documentation/vision/vngenerateforegroundinstancemaskrequest))，工作量 **S–M**。证件照换底色可以作为第二步（M）：人像分割、换纯色底，再按面部关键点裁成 295×413 等标准尺寸。中文需求在佐糖、改图宝的关键词里都能看到 ([Similarweb 佐糖](https://www.similarweb.com/website/picwish.cn/), [Similarweb 改图宝](https://www.similarweb.com/website/gaitubao.com/))，不过多数被微信小程序满足了。这一功能应标明是“便利工具”，不是合规保证。

**按目标文件大小压缩**是第二大空白，而且 macOS 没有对应的内置功能。“video compressor”美国每月 9.05 万次 ([seodata](https://www.seodata.dev/keyword/video-compressor))，是 FreeConvert 的第二大非品牌关键词 ([Similarweb](https://www.similarweb.com/website/freeconvert.com/))。知乎“如何压缩视频大小?”约 275.1 万浏览 ([搜狗→知乎](https://www.sogou.com/sogou?query=Mac+%E8%A7%86%E9%A2%91+%E6%A0%BC%E5%BC%8F%E8%BD%AC%E6%8D%A2+%E5%8E%8B%E7%BC%A9+%E8%BD%AF%E4%BB%B6&insite=www.zhihu.com))，专做“压到 8MB”的 8mb.video 每月近 40 万访问 ([Similarweb](https://www.similarweb.com/website/8mb.video/))。图片与 PDF 的“压到 N KB”有很强的地区性：美国“compress image to 100kb”每月只有 170 次 ([seodata](https://www.seodata.dev/keyword/compress-image-to-100kb))，Ahrefs 的印度数据里“compress pdf to 100kb”却有约 **10.8 万**次 ([Ahrefs/pi7](https://ahrefstop.com/websites/pi7.org))，对应政府与考试网站的上传限制。商业产品也在往这个方向走：Clop 主打“Fit under size” ([Clop](https://lowtechguys.com/clop/))，Permute 提供目标大小 ([Permute](https://software.charliemonroe.net/permute/))，Clop 近 30 天的 Homebrew 安装速度是全年均值的 **4.65 倍** ([Homebrew](https://formulae.brew.sh/api/analytics/cask-install/30d.json))。图片部分是 **S**：现有 `CompressEditor` 已能在内存中重新编码，只需对质量做二分搜索，再配合降低分辨率。本机测试发现，4000 px 的 JPEG 即使质量降到 0 仍有约 357 KB，可见光靠质量够不着小目标。视频部分是 **M**：`fileLengthLimit` 在最高质量预设下会被直接忽略 ([Apple](https://developer.apple.com/documentation/avfoundation/avassetexportsession/filelengthlimit))；改用 `AVAssetWriter` 加 `kVTCompressionPropertyKey_DataRateLimits` 后，H.264 的结果落在目标的 0.73–1.06 倍，HEVC 却超出 1.5 倍 ([Apple](https://developer.apple.com/documentation/videotoolbox/kvtcompressionpropertykey_dataratelimits))。所以默认用 H.264，并加上“超了就降码率重试一次”的循环。

**RAR/7z 解压**是 Mac 特有的痛点，因为 macOS 没有内置 RAR 支持。带“mac”的 RAR 搜索约占通用“rar extractor”的 55% ([seodata](https://api.seodata.dev/v1/keyword?q=how+to+open+rar+files+on+mac&country=us))，远高于其他任务 0.1–3% 的 Mac 占比。Ask Different 上“解压 .7z”有 25.7 万浏览 ([Ask Different](https://apple.stackexchange.com/q/307377))，“压缩时去掉 .DS_Store/__MACOSX”有 15.9 万 ([Ask Different](https://apple.stackexchange.com/q/239578))。Homebrew 上 7-Zip 的两个公式一年合计约 21.1 万次安装，Keka 4.38 万 ([Homebrew](https://formulae.brew.sh/api/analytics/install-on-request/365d.json))；中国区 Mac App Store 也有好几款中文解压应用在持续更新 ([iTunes 中国区](https://itunes.apple.com/search?term=%E8%A7%A3%E5%8E%8B%E4%B8%93%E5%AE%B6&country=cn&entity=macSoftware&limit=4))。好消息是，系统自带的 bsdtar 3.5.3 / libarchive 3.7.4 在本机就能读 RAR4、RAR5、7z、ISO 和 XAR。Swivel 的“解压”本来就把非 ZIP 文件交给 `tar`，`.rar` 之所以没被识别，只是因为它在本机没有声明 UTType ([Catalog.swift](file:///Users/wulizhi/.codex/.chatgpt-projects/g-p-6ad0c15852b08191845e68d96c6dcb35/sites/swivel/Sources/Swivel/Catalog.swift))，所以工作量是 **S**。有三个坑必须处理。第一，分卷 RAR 只解第一卷时，退出码为 0，文件却悄悄被 NUL 填充，必须把各卷拼接后从 stdin 输入。第二，加密的 7z 和 RAR，libarchive 解不开 ([libarchive 源码](https://github.com/libarchive/libarchive/blob/v3.7.4/libarchive/archive_read_support_format_rar5.c))，要给出明确提示。第三，加密 ZIP 可以用 `tar --passphrase` 解开，但如果没传口令，bsdtar 会反复读取 stdin。顺带要修一个现有问题：`.dmg` 也被归为压缩包，会被提供“解压”，而这必然失败。

**防休眠**是排名最靠前的“小工具”信号。Raycast 的 Coffee 扩展装机 15.6 万，在 3,437 个扩展中排第 15 ([Raycast](https://www.raycast.com/mooxl/coffee))。Ask Different 上“锁屏时保持 Mac 唤醒”有 22.4 万浏览，“拷贝文件时不休眠”另有 2.2 万 ([Ask Different](https://apple.stackexchange.com/q/76107), [Ask Different](https://apple.stackexchange.com/q/414134))。`IOPMAssertion` 在本机创建时没有任何弹窗，工作量 **S**。Amphetamine 的“下载/某 App 运行期间保持唤醒”很适合 Swivel 的拖放模型，比如把文件夹拖上去，拷贝完成前不休眠 ([App Store](https://apps.apple.com/us/app/amphetamine/id937984704?mt=12))。“合盖不休眠”在 Apple 芯片上需要安装管理员脚本，应该排除。

PDF 方向的增长点在签名、OCR 和解锁，而不在已经做过的合并、拆分、压缩。以 2022 年为基准，全球 Trends 上“sign pdf”增长 **4.64 倍**，“ocr pdf”增长 2.94 倍，“unlock pdf”增长 2.55 倍，都明显快于基准词“compress pdf” ([Trends P02](https://trends.google.com/trends/explore?date=today%205-y&q=compress%20pdf,jpg%20to%20pdf,image%20to%20pdf,pdf%20to%20excel,sign%20pdf), [Trends P03](https://trends.google.com/trends/explore?date=today%205-y&q=compress%20pdf,unlock%20pdf,password%20protect%20pdf,ppt%20to%20pdf,ocr%20pdf))。按绝对量算，美国“sign pdf”每月 2.22 万次、“unlock pdf”1.48 万次 ([seodata](https://www.seodata.dev/keyword/sign-pdf), [seodata](https://www.seodata.dev/keyword/unlock-pdf))。Swivel 目前直接拒绝加密 PDF ([Documents.swift](file:///Users/wulizhi/.codex/.chatgpt-projects/g-p-6ad0c15852b08191845e68d96c6dcb35/sites/swivel/Sources/Swivel/Documents.swift))。用 PDFKit 设置和移除密码是 **S**：移除时要把页面复制到新文档，并且应要求所有者密码，尊重作者设下的限制。“可搜索 PDF”也有现成的一行做法 `saveTextFromOCROption`（macOS 13+）([Apple](https://developer.apple.com/documentation/pdfkit/pdfdocumentwriteoption/savetextfromocroption))。但本机测试中，它在中文字之间插入空格，日文假名丢失；改用 macOS 26 的 `RecognizeDocumentsRequest` 自绘文字层，中英日三种文字都完全正确 ([Apple](https://developer.apple.com/documentation/vision/recognizedocumentsrequest))，工作量升到 **M**。“签名”只能做成印章图片，不是加密数字签名，界面上要写清楚。

**音视频转文字（TXT/SRT）**和**图片翻译**需求中等偏高，但要等到 macOS 26 才能做好。美国“speech to text”每月 3.31 万次，“transcribe audio to text”2.22 万次 ([seodata](https://api.seodata.dev/v1/keyword?q=speech+to+text&country=us), [seodata](https://www.seodata.dev/keyword/transcribe-audio-to-text))。Homebrew 上 Whisper 相关公式一年约 21.9 万次安装，约十个转写类 GUI 应用近 30 天的安装速度是全年均值的 1.5 倍，whisper.cpp 有 **5.45 万**星 ([GitHub](https://github.com/ggml-org/whisper.cpp))。日本是突出的地区市场：notta.ai 69% 的流量来自日本 ([Similarweb](https://www.similarweb.com/website/notta.ai/))。macOS 26 的 `SpeechAnalyzer`/`SpeechTranscriber` 在本机 0.28 秒转完一段英文，带逐词时间，没有触发语音识别授权，支持 30 个语言区域，包括简中、粤语和日语；不过中文需要系统下载语言资源，中文质量也没测 ([Apple](https://developer.apple.com/documentation/speech/speechanalyzer), [WWDC25](https://developer.apple.com/videos/play/wwdc2025/277/))。macOS 15 上只有 `SFSpeechRecognizer`，本机只有英语能端侧识别。图片翻译方面，“translate image”美国每月 4.05 万次 ([seodata](https://www.seodata.dev/keyword/translate-image))，可独立网站几乎没有流量（imagetranslate.com 月访问 2.8 万），说明需求被 Google Lens 吸走了 ([Similarweb](https://www.similarweb.com/website/imagetranslate.com/))。中文侧的“截屏翻译”问题有 199.3 万浏览 ([搜狗→知乎](https://www.sogou.com/sogou?query=Mac+%E6%88%AA%E5%9B%BE+%E6%96%87%E5%AD%97%E8%AF%86%E5%88%AB+OCR+%E7%BF%BB%E8%AF%91&insite=www.zhihu.com))，Easydict 有 1.5 万星 ([GitHub](https://github.com/tisfeng/Easydict))。做法最省力的是在现有 Text Capture 窗口里加一个“翻译”按钮，macOS 26 上可以用无界面的 `TranslationSession(installedSource:target:)` ([Apple](https://developer.apple.com/documentation/translation/translationsession))，工作量 M。

还有几项低成本补件：PNG→ICO（每月 2.22 万次）、图片取色（6.05 万）、GIF 压缩（4.05 万）、文件校验和（Ask Different 15.3 万浏览，但偏技术用户），工作量都是 S ([seodata](https://www.seodata.dev/keyword/png-to-ico), [seodata](https://www.seodata.dev/keyword/color-picker-from-image), [seodata](https://www.seodata.dev/keyword/compress-gif), [Ask Different](https://apple.stackexchange.com/q/230917))。AI 放大（“image upscaler”每月 4.95 万，upscayl 有 5.08 万星）只能部分实现：真正的超分辨率只在 macOS 26 的 `VTSuperResolutionScaler` 上有，限 4 倍、输入不超过 1920 px，还要下载苹果模型。另有一项要清理：本机 ImageIO 不能写 WebP，而 Swivel 把 WEBP 列为输出格式，运行时又悄悄把它丢掉 ([Images.swift](file:///Users/wulizhi/.codex/.chatgpt-projects/g-p-6ad0c15852b08191845e68d96c6dcb35/sites/swivel/Sources/Swivel/Images.swift))，应该从 Customize 里隐藏。

| 功能 | 最强需求信号 | 系统级可行性（本机验证） | 工作量 | 最低系统 |
|---|---|---|---|---|
| 本地抠图（含证件照） | 美国 82.3 万/月；remove.bg 5,420 万访问 | Vision 前景实例蒙版，0.13 秒 | S–M（证件照 M） | macOS 14 |
| 目标大小压缩 | video compressor 9.05 万/月；知乎 275.1 万；印度“100kb”约 10.8 万 | 图片：质量二分 + 降分辨率；视频：H.264 + DataRateLimits 落在 0.73–1.06 倍 | 图片 S / 视频 M | macOS 15 |
| RAR/7z/ISO 解压 | 知乎 391.9 万；.7z 25.7 万浏览；Mac 占比约 55% | 系统 bsdtar 可读，需补类型识别与分卷拼接 | S | macOS 15 |
| 防休眠 | Raycast 15.6 万装机（第 15） | IOPMAssertion，无弹窗 | S | 10.15+ |
| PDF 加解密与可搜索 OCR | sign 4.64 倍、ocr 2.94 倍、unlock 2.55 倍增长 | PDFKit；中日韩文需 macOS 26 文档识别 | S–M | 15 / 26 |
| 转写 TXT/SRT | speech to text 3.31 万/月；Whisper 21.9 万安装 | SpeechTranscriber，0.28 秒，无授权弹窗 | M | macOS 26 |
| 图片翻译 | translate image 4.05 万/月；知乎 199.3 万 | TranslationSession 端侧离线 | M | 26（15 需 SwiftUI） |

## 文件之外，存储、卡顿与“打不开”占据电脑难题的头部

把视角从文件扩展到“电脑上的难题”，全球英文数据呈现清晰的梯队。截图（“how to screenshot on mac”每月 45 万次）已经由晾衣绳覆盖 ([seodata](https://www.seodata.dev/keyword/how-to-screenshot-on-mac))。紧随其后的是**带系统声音的录屏**：“how to screen record on mac”每月 11 万次，Ask Different 上的同类问题有 43.9 万浏览，虚拟声卡 BlackHole 一年装机 **11.1 万**且还在加速 ([seodata](https://www.seodata.dev/keyword/how-to-screen-record-on-mac), [Ask Different](https://apple.stackexchange.com/q/212829))。原因是 QuickTime 和截屏工具栏只能录麦克风 ([Tella](https://www.tella.com/blog/quicktime-screen-recording-with-audio))。再往下是卡死与强制退出：“how to force quit on mac”每月 6.05 万次，“Mac 上的 Ctrl+Alt+Del 是什么”累计 **159.6 万**浏览 ([seodata](https://www.seodata.dev/keyword/how-to-force-quit-on-mac), [Ask Different](https://apple.stackexchange.com/questions/86010))，kernel_task 占满 CPU 的问题有 61.9 万浏览 ([Ask Different](https://apple.stackexchange.com/questions/363337))。然后是卸载应用（每月 3.31 万次，AppCleaner 与 Pearcleaner 合计约 9.2 万装机）([seodata](https://www.seodata.dev/keyword/how-to-uninstall-apps-on-mac))，以及存储。存储这一项，Apple 社区“系统数据占 115GB”帖有 **3,723** 人点“我也是” ([Apple 社区](https://discussions.apple.com/thread/254588225))，Ask Different 上“System 占 470GB”有 22.9 万浏览、“可清除空间”有 14.0 万 ([Ask Different](https://apple.stackexchange.com/questions/261252), [Ask Different](https://apple.stackexchange.com/questions/252217))。开源清理工具 Mole 上线约 13 个月就拿到 **7.2 万**星 ([GitHub](https://github.com/tw93/Mole))。近两个系统版本的退化问题也在持续制造需求：“后台项目已添加”反复弹出，在 Apple 社区拿到全场最高的 **5,057** 个“我也是” ([Apple 社区](https://discussions.apple.com/thread/254341579))；升级 Tahoe 后打印机失效，原因是“本地网络”权限 ([Apple 社区](https://discussions.apple.com/thread/256145647))；Sequoia 取消了按住 Control 点开未公证应用的绕过方式 ([BleepingComputer](https://www.bleepingcomputer.com/news/apple/macos-sequoia-brings-better-gatekeeper-stalkerware-protections))。

需求大，不等于 Swivel 能解。笔记把每个问题按解决途径分成五类：**A** 无需特殊权限、**B** 需一次管理员授权、**C** 需辅助功能/屏幕录制/完全磁盘访问等隐私权限、**D** 需驱动或私有 API、**E** 最适合讲解并直达设置。结论对 Swivel 很有利：存储、卡顿、“打不开”、外置硬盘、网络、后台项目这些头部问题的**诊断都属于 A 类**。本机不用 root 就能运行 `tmutil listlocalsnapshots /`、`networksetup -getdnsservers`、`sfltool dumpbtm`。修复多半属于 E 类，可以用 `x-apple.systempreferences:` 链接直接打开对应面板（面板 ID 会随版本变化，需要逐版测试）([bvanpeski](https://github.com/bvanpeski/SystemPreferences/blob/main/macos_preferencepanes-Ventura.md))；少数属于 B 类，如刷新 DNS、删快照、重置后台项目。有几条边界需要事先知道。`wdutil info` 和 `powermetrics` 必须 root；macOS 14.4 起读取 Wi-Fi 名称需要定位权限 ([Apple 论坛](https://developer.apple.com/forums/thread/748518))；从打包的 App 里调用 `tmutil latestbackup` 会要求完全磁盘访问 ([Apple 论坛](https://developer.apple.com/forums/thread/784254))；Ventura 起的“App 管理”权限会阻止一个 App 删除另一个 App ([Michael Tsai](https://mjtsai.com/blog/2022/11/07/ventura-app-management/))。所以“彻底卸载含残留”只能做到半自动：列出 ~/Library 里与该应用 bundle ID 匹配的项目，由用户自己移到废纸篓。另一个趋势同样重要：分屏平铺（Sequoia 起）、剪贴板历史（Tahoe）、Wi-Fi 密码（“密码”应用）、80% 充电上限（26.4）、菜单栏图标开关与刘海溢出处理（Tahoe 和 macOS 27）都已陆续内置 ([MacRumors](https://macrumors.com/2024/06/12/macos-sequoia-window-tiling/), [Tom's Guide](https://www.tomsguide.com/computing/software/how-to-enable-clipboard-history-in-macos-tahoe), [Six Colors](https://sixcolors.com/post/2026/09/macos-27-golden-gate-review-bridging-the-tahoe-gap/))。这些问题因此从“需要装第三方工具”变成了“设置在哪里”，而回答“设置在哪里”正是助理擅长的事。

中文用户是一个结构相近、但有本地特色的细分市场。按知乎（经搜狗快照，属下限值）和 B 站的数据，最大的问题簇依次是：存储与“系统数据/其他”（知乎单题 182.8 万浏览）([搜狗→知乎](https://www.sogou.com/sogou?query=Mac+%E7%B3%BB%E7%BB%9F%E6%95%B0%E6%8D%AE+%E5%8D%A0%E7%94%A8+%E5%A4%AA%E5%A4%A7&insite=www.zhihu.com))、微信占用空间与聊天记录位置（通用问题 213.5 万，CSDN 单篇 3.1 万）([搜狗→知乎](https://www.sogou.com/sogou?query=Mac+%E5%BE%AE%E4%BF%A1+%E5%8D%A0%E7%94%A8%E7%A9%BA%E9%97%B4+%E6%B8%85%E7%90%86&insite=www.zhihu.com), [CSDN](https://blog.csdn.net/weixin_33207551/article/details/86502158))、Windows 习惯差异（使用技巧汇总题 275.6 万、“剪切”35.5 万；B 站入门指南 259 万播放）([搜狗→知乎](https://www.sogou.com/sogou?query=Mac+%E5%89%AA%E5%88%87+%E7%B2%98%E8%B4%B4+%E6%96%87%E4%BB%B6&insite=www.zhihu.com), [B站](https://search.bilibili.com/video?keyword=Mac%20%E4%BD%BF%E7%94%A8%E6%8A%80%E5%B7%A7&order=click))、RAR/7z 解压（391.9 万）、鼠标滚轮方向（109.5 万）、显示隐藏文件（205.2 万）、电池能否一直插电（134.6 万；B 站 102.3 万）、移动硬盘无法识别（96.9 万），以及 NTFS 只读（20.1 万）；中国区 App Store 上至少有四款持续更新的付费 NTFS 应用 ([iTunes 中国区](https://itunes.apple.com/search?term=NTFS&country=cn&entity=macSoftware&limit=4))。“已损坏，无法打开”在中文网络被反复写成教程：B 站保姆级教程 12.4 万播放，CSDN 2023 年有四篇破万阅读 ([B站](https://search.bilibili.com/video?keyword=Mac%20%E5%B7%B2%E6%8D%9F%E5%9D%8F%20%E6%97%A0%E6%B3%95%E6%89%93%E5%BC%80&order=click), [CSDN](https://so.csdn.net/api/v3/search?q=Mac%20%E5%B7%B2%E6%8D%9F%E5%9D%8F%E6%97%A0%E6%B3%95%E6%89%93%E5%BC%80&t=blog&p=1))。但这类内容大量来自破解软件资源站，常见“解法”是 `sudo spctl --master-disable`，即直接关闭整个 Gatekeeper；台湾 PTT 上同类帖子很少 ([PTT](https://www.ptt.cc/bbs/MAC/search?q=%E5%B7%B2%E6%90%8D%E6%AF%80))。因此这是大陆特有的问题，Swivel 只应提供“逐个应用、先校验签名与公证、再引导到‘仍要打开’”的安全路径。另一个信号值得注意：搜狗的“Mac 已损坏”结果页上已经挂着“问问 WorkBuddy”和元宝 AI 答案 ([搜狗](https://www.sogou.com/sogou?query=Mac+%E5%B7%B2%E6%8D%9F%E5%9D%8F+%E6%97%A0%E6%B3%95%E6%89%93%E5%BC%80&insite=zhihu.com))，腾讯的代理已经站在 Mac 求助流量的入口。

| 难题 | 全球证据 | 中文证据 | 解法类别 | Swivel 的合理做法 |
|---|---|---|---|---|
| 存储/系统数据/大文件 | 3,723 个“我也是”；22.9 万、14.0 万浏览；Mole 7.2 万星 | 182.8 万；微信 213.5 万 | 诊断 A，清理 A/B | 只读扫描用户选定的文件夹 + 快照/iCloud/缓存讲解；只移到废纸篓并可撤销 |
| 卡顿/卡死/发热 | 6.05 万/月；159.6 万、61.9 万浏览 | 43.2 万 | A | 进程、内存压力、温控状态的解读；一键退出用户自己的应用 |
| 应用“打不开/已损坏” | 社区帖 431；Sequoia 取消 Control 点开 | B 站 12.4 万；CSDN 四篇破万 | A/E | 读取隔离标记与签名公证状态，打开“隐私与安全性”，不提供全局关闭 |
| 后台项目/登录项 | 5,057 个“我也是” | 12.4 万（删了 App 仍在后台） | 读 A，重置 B | 把 LaunchAgents 对应到厂商；`sfltool resetbtm` 只显示命令，由用户执行 |
| 外置硬盘/NTFS | 19.1 万、43.3 万浏览；9,900/月 | 96.9 万、20.1 万 | A；NTFS 写入 D | 区分“没检测到 / 未挂载 / 文件系统不可读”，找出占用进程，经确认后格式化为 exFAT |
| 网络/测速/DNS | Raycast Speedtest 18.9 万装机 | — | A；刷新 DNS 需 B | 封装 `networkQuality` 和 DNS 读取 |
| 带系统声音的录屏 | 11 万/月；BlackHole 11.1 万 | 70.3 万 | C | 不做，只讲解或转交其他工具 |
| Windows 习惯 | 剪切 20.1 万、Home/End 23.1 万浏览 | 275.6 万、35.5 万 | E/A | 教 ⌥⌘V；在 Quick Actions 里提供“在此新建文件”；用 KeyBinding 文件映射 Home/End |

## 通用“替你点鼠标”已是巨头赛道，Swivel 应占工具层和 Mac 医生

2026 年，每家主要厂商都发布了桌面代理，架构也收敛成同一种形态：优先走结构化连接器和 MCP，其次用浏览器，像素级 GUI 操作放在最后，并且越来越多地放进隔离环境里运行。Anthropic 对 computer use 的说明是让 Claude “reach for the most precise tool first”，即先用连接器，没有可用工具时才动屏幕；它同时承认 computer use 仍处早期、比直连慢，复杂任务有时要试第二次 ([Anthropic](https://claude.com/blog/dispatch-and-computer-use))。Cowork 跑在本机隔离虚拟机里 ([Claude 更新日志](https://support.claude.com/en/articles/12138966-release-notes))。OpenAI 的 computer use 要求屏幕录制和辅助功能两项权限，每个 App 单独批准，不能操作终端，也不能代批系统隐私弹窗 ([ChatGPT 文档](https://learn.chatgpt.com/docs/computer-use.md))；Codex 上线时还排除了欧洲经济区、英国和瑞士 ([OpenAI](https://developers.openai.com/codex/app/computer-use))。微软给代理单独设账户和工作区 ([Microsoft](https://support.microsoft.com/en-us/windows/ai/ai-features/experimental-agentic-features))，但在 10 月 21 日宣布从文件资源管理器右键菜单撤下 8 个 Copilot 操作 ([Windows Latest](https://www.windowslatest.com/2026/10/21/microsoft-is-now-scaling-back-copilot-in-windows-11-amid-microslop-backlash-pulling-8-ai-actions-from-file-explorer-and-calling-it-simplifying/))。这对 Swivel 的轮盘是直接的提醒：AI 入口不能硬塞进每个菜单，“每项默认关、单独开”的现有做法是对的。

可靠性和安全性仍是这条赛道的硬伤。即使 OSWorld-Verified 前沿成绩约 85–86% ([BenchLM](https://www.benchlm.ai/benchmarks/osworld-verified))，在基准条件下多步 GUI 任务仍有大约七分之一失败。2025 年 Claude for Chrome 的红队测试中，提示词注入成功率加了防护后仍有 **11.2%** ([Anthropic](https://www.anthropic.com/news/claude-for-chrome))；Simon Willison 指出，可信指令和不可信内容被拼进同一串 token，至今没人证明能把它们可靠地区分开 ([Simon Willison](https://simonwillison.net/2025/Aug/25/agentic-browser-security/))。2026 年最大的事故来自本地代理 OpenClaw：它的技能市场 2,857 个技能中有 **341 个恶意**，控制界面接受 URL 参数里的网关地址并把令牌发出去（CVE-2026-25253），默认还监听在 `0.0.0.0` ([Barrack AI](https://blog.barrack.ai/openclaw-security-vulnerabilities-2026/), [Unit 42](https://unit42.paloaltonetworks.com/openclaw-ai-supply-chain-risk/))。在中国，它一度引发 Mac mini 抢购潮，工信部也发出风险预警 ([HK01](https://global.hk01.com/大国小事/60329360/openclaw爆火引安全焦虑-养龙虾-掀apple-mac-mini抢购潮))。这些教训可以直接写成 Swivel 的规则：不开放第三方技能市场；本地服务只用 stdio，或只绑定 127.0.0.1；令牌放请求头，绝不放 URL；内容中出现的“安装说明”一律视为数据。

中国市场的竞争同样激烈，但桌面端规模仍然很小。豆包 Mac 版 9 月 2 日加入“操作电脑”，不需要 MCP 或 API，直接理解图形界面完成操作 ([IT之家](https://www.ithome.com/0/997/506.htm))。千问桌面代理能定时整理文件夹，连接钉钉、飞书、腾讯文档和本机日历；但评测中一个数据透视表请求没能完成，原因是可用的工具和技能不够 ([爱范儿](https://www.ifanr.com/1674265))。这恰好说明工具目录的价值。QuestMobile 9 月数据显示豆包移动端月活 **4.19 亿** ([199IT](http://www.199it.com/archives/1858382.html))，而 PC 客户端 AI 应用合计只有约 **1,800 万**月活 ([IT之家](https://www.ithome.com/0/976/383.htm))；另一份桌面榜单称 WorkBuddy 月活 1,115 万 ([东方财富](https://finance.eastmoney.com/a/202608183844353784.html))，两者口径不同。在 Mac 专属赛道上，MacPaw 的 Eney 于 10 月 15 日发布，发消息、改日程、移动文件前都会先确认，按点数计费 ([TechRepublic](https://www.techrepublic.com/article/news-macpaw-eney-ai-assistant/))。中文侧最接近 Swivel 的商业对手是宁波上官（Better365），产品线包括 iShot、超级右键、iOCR，2026 年仍在更新 ([iTunes 中国区](https://itunes.apple.com/search?term=iShot&country=cn&entity=macSoftware&limit=4))。Clop 则已经为 AI 代理提供 MCP 服务器 ([Clop](https://lowtechguys.com/clop/))。综合来看，一个人维护的菜单栏应用正面拼“通用操作电脑”没有胜算。Swivel 的位置有四点：确定、可预览、可撤销的本地工具；几乎不需要权限；能在中国大陆正常使用的模型路由；以及大代理都不主打的 Mac 专属“修电脑”场景。

## 架构：工具目录在前，模型可换，权限分五级逐级开放

### 工具目录与“计划—预览—执行—撤销”

核心是一份 Swivel 自己维护、带类型的工具目录。现有的转换、压缩、PDF、OCR、二维码、批量重命名，加上新增的诊断工具，每项都有参数模式、风险等级、试运行预览和撤销日志。模型只负责把一句话变成计划并解释结果，真正执行的是确定性代码。输出照旧写在原文件旁，绝不覆盖，继续沿用 `Paths.unique`。风险等级分五档：只读操作自动执行；可逆的本地写入自动执行并记撤销；移动、重命名、移到废纸篓先预览再确认；凡是发送、发布、改设置的操作每次都要确认，不提供“始终允许”；永久删除和输入凭据一律拒绝，交给用户自己做。这套“计划 + 预览”的形态也正好契合 MCP 2026-07-28 新规范的无状态核心：会改动文件的工具拆成 `plan_*` 和 `apply_plan`，或者用多轮请求的 `input_required` 向用户要确认 ([MCP](https://blog.modelcontextprotocol.io/posts/2026-07-28/))。

### 模型层：苹果模型只是快速通道，中国大陆默认用国产 API

Apple 的端侧模型约 3B 参数，每个会话 4,096 token 的上下文；中文约 1 个字 1 个 token，所以连同指令和工具定义在内大约只放得下 4,000 字 ([Apple TN3193](https://developer.apple.com/tutorials/data/documentation/technotes/tn3193-managing-the-on-device-foundation-model-s-context-window.md))。macOS 27 的上下文是 4K 还是 8K，两个 WWDC 场次说法冲突 ([WWDC26 241](https://developer.apple.com/videos/play/wwdc2026/241/), [WWDC26 319](https://developer.apple.com/videos/play/wwdc2026/319/))，应在运行时读取 `contextSize`。更关键的是中国大陆。Apple 支持页至今写明，在大陆购买的设备上 Apple Intelligence 目前无法使用 ([Apple](https://support.apple.com/en-us/121115))。网信办 7 月已完成备案，由阿里 Qwen 合作 ([TechCrunch](https://techcrunch.com/2026/07/15/apple-intelligence-approved-for-launch-in-china-with-alibabas-qwen-ai/))；库克称 10 月底会有测试版，彭博报道 12 月随 iOS 27.3 正式推出，但 Mac 是否同步、第三方能否调用 Foundation Models 都未确认 ([爱范儿](https://www.ifanr.com/1684431), [TechNode](https://technode.com/2026/10/15/apple-reportedly-plans-december-china-launch-for-apple-intelligence-using-alibaba-models/))，而该框架要求先开启 Apple Intelligence ([Apple 论坛](https://developer.apple.com/forums/thread/802119))。macOS 27 的 PCC 云端模型只开放给加入 App Store 小企业计划、累计下载不足 200 万的开发者 ([WWDC26 319](https://developer.apple.com/videos/play/wwdc2026/319/))；Swivel 目前是 ad-hoc 签名、不在 App Store，用不了。因此 Swivel 绝不能依赖苹果模型，只能把它当作“有就用的免费快速通道”。建议的抽象是一个 Swivel 自有的 `ModelProvider` 协议，下挂三个实现：一个 URLSession 客户端，对接 OpenAI 兼容的 Chat Completions 接口（覆盖 DeepSeek、百炼 Qwen、Kimi、GLM、OpenAI、Ollama、LM Studio）；一个对接 Anthropic Messages 接口（DeepSeek 也兼容这一格式）；以及一个放在 `#available(macOS 26, *)` 后面的 Apple 适配器。macOS 27 允许任何模型实现 `LanguageModel` 协议 ([WWDC26 指南](https://developer.apple.com/wwdc26/guides/macos/))。Anthropic 的 `ClaudeForFoundationModels` 采用 Apache 2.0 许可，但引入它会打破“无第三方依赖”的规则，而且官方明确说打包进 App 的密钥可以被提取 ([Claude 文档](https://platform.claude.com/docs/en/cli-sdks-libraries/libraries/apple-foundation-models))，所以应自己写一个薄的协议实现，密钥由用户自带、存进钥匙串。

| 模型层 | 可用条件 | 适合做什么 | 一次诊断（2 万输入 + 2 千输出）约 |
|---|---|---|---|
| Apple 端侧模型（macOS 26+） | 需开启 Apple Intelligence，中国大陆目前不可用 | 把一句话解析成 `@Generable` 参数，比如“压到 1MB 以内的 JPG”→ {动作, 目标大小, 格式}；生成短摘要 | 0 |
| Claude Haiku 5.5 / Sonnet 5.5 | 中国大陆、香港、澳门不可用 ([The Decoder](https://the-decoder.com/anthropic-bans-companies-majority-controlled-by-china-russia-iran-and-north-korea-from-claude/)) | 诊断摘要 / 难推理与 GUI 操作 | $0.003 / $0.06 ([Anthropic 定价](https://platform.claude.com/docs/en/about-claude/pricing)) |
| DeepSeek `deepseek-flash` | 大陆可用，支持 Tool Calls，提供 OpenAI 与 Anthropic 兼容端点 | 大陆默认 | ¥0.03（闲时）/ ¥0.06（高峰）([DeepSeek](https://api-docs.deepseek.com/zh-cn/quick_start/pricing)) |
| Qwen3.8-Max（百炼） | 大陆可用，OpenAI 兼容端点 | 大陆的高阶推理 | 约 ¥0.31 ([百炼](https://help.aliyun.com/zh/model-studio/model-pricing)) |
| Ollama / LM Studio 本地模型 | 用户自行安装，`localhost:11434/v1` | 离线、隐私优先 | 0；工具调用的可靠性因模型而异 |

Gemini 的可用地区列表里看不到中国 ([Google](https://ai.google.dev/available_regions))。OpenAI 在大陆的可用性只有低质量二手来源，本报告不下结论。

### 集成面：App Intents 与 MCP 让 Swivel 进入每个代理

不用模型、也不要权限、杠杆最大的一步，是把现有工具同时开放成两种形式。第一种是 **App Intents**：macOS 26 上进入 Spotlight 动作和快捷指令，用户可以用“Use Model”把 Apple Intelligence 或 ChatGPT 和 Swivel 的动作串起来 ([MacMost](https://macmost.com/how-to-use-spotlight-actions-in-macos-tahoe.html))；macOS 27 上再通过实体/意图 schema 进入 Siri 和 Spotlight 语义索引 ([WWDC26 指南](https://developer.apple.com/wwdc26/guides/macos/))。第二种是本地 **MCP 服务器**：加一个 `Swivel --mcp` 的 stdio 模式，手写 JSON-RPC，因为目前没有一线的 Swift MCP SDK，这样反而保住了无依赖规则；再打包成 `server.type: "binary"` 的 MCPB 文件 ([MCPB](https://github.com/modelcontextprotocol/mcpb/blob/main/MANIFEST.md))，供 Claude Desktop、Raycast ([Raycast](https://www.raycast.com/changelog/macos-v1/1-98-0))、Codex 插件等宿主调用。macOS 27 SDK 里没有系统级 MCP 接口，这一点已在本机 grep 确认，所以没有“等苹果做”的捷径。这样做还有一个好处：宿主代理只能传入它自己有权访问的路径，Swivel 无需新增任何系统授权。Swivel 已有的 Codex/Claude Code 草稿深链接可以顺势升级为“分派器”：由 Swivel 准备好文件、OCR 文本或诊断报告，打开用户选定的代理，把重活交给代理在它自己的权限下完成。

### 权限阶梯与安全规则

| 级别 | 能做什么 | macOS 权限 | 默认 |
|---|---|---|---|
| 0 | 处理用户拖入、选择或由 MCP 宿主传入的文件；运行固定的只读诊断命令；调用模型；App Intents；MCP stdio | 无（联网调用需用户自己配置密钥） | 随各功能开关 |
| 1 | 理解用户亲自指定的内容：晾衣绳截图卡片，或用 `SCContentSharingPicker` 选中的窗口 → OCR + 解释报错 | 截图卡片无需权限；按开发者报告，Picker 可免屏幕录制授权，但 26/27 上未验证 ([Nonstrict](https://nonstrict.eu/blog/2023/a-look-at-screencapturekit-on-macos-sonoma/)) | 关 |
| 2 | 对特定 App 执行脚本化动作，如在 Finder 中显示、打标签，或创建邮件草稿 | Automation，每个目标 App 单独询问 | 关 |
| 3 | 完整的 GUI 操作，按 App 设白名单；硬性禁止终端、系统隐私设置、密码管理器、银行支付类 App 和 Swivel 自身；屏幕上显示“代理正在操作”及停止键 | 辅助功能 + 屏幕录制，Sequoia 起每月重新确认 ([9to5Mac](https://9to5mac.com/2024/08/14/macos-sequoia-screen-recording-prompt-monthly/))；与 App Store 沙盒不兼容 ([Apple 论坛](https://developer.apple.com/forums/thread/756130)) | 关，标为“实验” |
| 4 | 特权修复，如刷新 DNS、重置后台项目、删除快照 | 管理员 | 永不静默执行：展示命令和理由，由用户在终端执行或自己批准授权 |

安全底线是：OCR 文本、屏幕内容、网页、文件内容和诊断输出一律视为数据，不能借此扩大权限，也不能绕过确认。同一个会话里，读取不可信内容、持有私密数据、能向外发送，这三者不能同时具备而中间没有人工把关。完全磁盘访问和输入监控应完全回避。

### Mac 医生的诊断目录

诊断不开放自由 shell，而是一组有名称、参数受检查的命令：磁盘（`du` 只扫用户选定的文件夹，加 `diskutil info /` 和本地快照列表）、电池与睡眠（`pmset -g batt`、`pmset -g assertions`）、网络（`networkQuality`、`scutil --dns`、`ping`/`dig`）、Spotlight 状态（`mdutil -s /`）、当前用户的后台项目、安全状态（`spctl --status`、`csrutil status`、`fdesetup status`），以及用户自己的崩溃报告。输出先在本地截断，并脱敏序列号、用户名和 Wi-Fi 名称，再交给云端模型。模型负责解释和提出建议；修复是单独的、需要确认的动作，比如打开对应的系统设置面板、把缓存移到废纸篓（可撤销）、确认后才重建 Spotlight 索引。需要 root 的 `powermetrics`、`wdutil` 等默认不放进目录。

## 高需求但不建议做

下表中的需求都真实存在，有的数字比短名单里的还大。不推荐的原因有四类：违反无第三方依赖或许可证规则；需要 Swivel 一直回避的隐私权限；系统已经内置；或者存在法律与安全风险。

| 功能 | 需求证据 | 不建议的原因 | 替代做法 |
|---|---|---|---|
| MP3 输出 | “mp4 to mp3”每月 16.5 万 ([seodata](https://www.seodata.dev/keyword/mp4-to-mp3))；四家转换站的头号非品牌词 | 系统里没有 MP3 编码器：编码器列表里没有 mp3，`afconvert` 报 `'fmt?'`；LAME 和 shine 是 LGPL ([Wikipedia](https://en.wikipedia.org/wiki/LAME))；自写编码器工作量 XL | 明确提示不支持，提供 AAC/M4A；可加 Opus（CAF 封装） |
| 带系统声音录屏 | 每月 11 万；43.9 万浏览；BlackHole 11.1 万；知乎 70.3 万 | 需要屏幕录制加音频捕获授权（[AudioCap](https://github.com/insidegui/AudioCap)），每月重新确认 | 讲解做法，转交其他工具 |
| 视频下载 | yt-dlp 一年 62.3 万次安装；Raycast 13.8 万 | 依赖第三方引擎，有法律与服务条款风险 | 无 |
| 正面竞争通用 GUI 代理 | 豆包、千问、Claude、Codex 都已上线 | 对方有前沿模型和隔离环境；成功率约 85%；注入问题未解 | 通过 MCP 或深链接转交给它们 |
| 窗口管理、独立滚动方向、菜单栏隐藏 | Rectangle 11.6 万；LinearMouse 4.5 万、Mos 2.2 万星、知乎 109.5 万；Ice 5.9 万 | 需要辅助功能或输入监控；平铺和菜单栏已部分内置 | 讲解内置功能；将来最多放到第 3 级 |
| NTFS 写入 | 每月 9,900；43.3 万浏览；知乎 20.1 万 | FSKit 理论上可行，但引擎 ntfs-3g 是 GPL；还有写坏数据的风险 | 解释只读的原因，引导拷出数据后格式化为 exFAT |
| 剪贴板历史 | Maccy 11.1 万 | 市场饱和；Tahoe 已内置；读取剪贴板将来可能触发隐私提示 | 无 |
| 外接显示器 HiDPI / DDC 亮度 | BetterDisplay 9.2 万；one-key-hidpi 1.1 万星 | 依赖私有 API，而 Swivel 只允许触感反馈这一个私有接口 | 提供显示器报告和设置指引 |
| 一键关闭 Gatekeeper | 中文教程大量推荐 `spctl --master-disable` | 等于为恶意软件开门 | 逐个应用校验签名后，引导到“仍要打开” |
| 技能/插件市场与局域网服务 | OpenClaw 有 39.3 万星 | 出现过 341 个恶意技能和远程代码执行漏洞 | 只用内置的签名工具，只用 stdio |
| 去水印 | “watermark remover”每月 11 万 | 去掉别人水印有正当性问题 | 无 |
| WebP 写出 | “jpg to webp”每月 6,600 | ImageIO 不能写 WebP，需要自写编码器 | 隐藏无效的 WEBP 选项，引导改用 AVIF/HEIC |

## 数据局限：哪些是估算，哪些被拦截

| 来源 | 性质 | 主要局限 |
|---|---|---|
| Google Ads 月均搜索（经 seodata.dev 转述） | Google 第一方数据，由第三方转述 | **仅限美国**；按区间取整；近义词合并成组，不能相加；匿名额度约 20–25 次查询，338 个猜测的关键词页中 264 个无缓存，所以右键、剪切、隐藏文件、Wi-Fi 密码等经典问题没有美国搜索量 |
| Google Trends | 第一方测量 | 32 组对比只取到 4 组就被限流（HTTP 429），转写、放大、WebP、MP3、视频压缩、压缩包、中文词和 Mac 变体都**没有增长数据** |
| Similarweb | 第三方估算 | 只有最近一个月（2026 年 9 月），免费页只显示前 5 个关键词和桌面端数据；中国大陆样本偏薄；整站访问量不等于单个功能的用量 |
| Semrush | 第三方估算 | 9 月头号国家是孟加拉、约 99% 为桌面流量，疑似样本或机器人异常，只作交叉核对 |
| Ahrefs 公开页 | 第三方估算 | 每站只显示一个国家（常为印度），不是全球总量 |
| Exploding Topics、seo.ai、Glimpse | 第三方估算 | 地区或时段没有说明；同一页面前后数字矛盾 |
| QuestMobile、AI 产品榜 | 第三方估算 | 统计口径不同（PC 客户端约 1,800 万月活 vs 桌面办公类 3,000 万以上） |
| Homebrew、Raycast | 第一方测量 | 记录的是安装事件，不是独立用户；偏开发者和高级用户；Raycast 是累计值 |
| Ask Different、Apple 社区、Apple YouTube | 第一方测量 | 浏览量是累计值，偏向 2011–2016 年的老问题；“我也是”的计数在关帖后冻结 |
| 知乎（经搜狗快照）、B 站、CSDN、V2EX | 第一方页面上显示的计数 | 知乎抓取日期未知，只能当下限；个别问题浏览量与回答数的比例异常，疑似 SEO；多数热门视频是 2019–2023 年的 |

被拦截或无法获取的来源包括：百度指数（返回“异常访问”10018）、5118（验证码）、站长之家与爱站（无数据）、Reddit（403，且搜索工具禁止该域名）、知乎直连（403，搜索需登录）、小红书与抖音（需登录）、微信指数与巨量算数（未尝试）、Mac App Store 评分数（接口对纯 Mac 应用一律返回 0）、TurboScribe 与 openai.com（403）、美国国务院护照照片页（403）、Smallpdf 统计页（JS 渲染，无法读取）。本机验证只在 macOS 26.6.2 / M5 Pro 上做过：没有 macOS 15 机器，macOS 27 的 API 无法运行，Swivel 本体也没有运行。中日文转写质量、超分辨率效果、真人肖像抠图、真实的压缩 RAR 样本都没测。所有排序和 S/M/L 工作量都是产品与工程判断，不是实测用量。

## 结论与排名短名单

这次调研最反直觉的发现是：**搜索需求衡量的是“任务”，几乎不衡量“平台”**。带“mac”字样的查询通常只占通用查询的 0.1–3%，RAR 和 MP3 是例外，因为 macOS 恰好做不到。Mac 用户还会去搜那些系统本来就能做的事，比如“how to combine pdf files on mac”每月 2,900 次 ([seodata](https://api.seodata.dev/v1/keyword?q=how+to+combine+pdf+files+on+mac&country=us))。这说明 Swivel 的瓶颈有一半在“被找到”：进入 Spotlight 和快捷指令的 App Intents，可能比再多一个转换格式更能放大已有的功能。第二个发现是，macOS 正在逐年吸收第三方小工具（抠图、平铺、剪贴板、充电上限、菜单栏），价值因此从“提供工具”转向“告诉你设置在哪、为什么出问题”，而这正是“Mac 医生”式助理的本职。第三个发现是，对中国大陆用户，真正的杠杆不是苹果模型，而是**模型路由**；对所有地区的用户，信任才是差异点：OpenClaw 之后，“权限少、可撤销、不联网也能用”本身就是卖点。每一阶段上线后，都可以用一个仅存本机、可关闭的成功操作计数来验证排序，不上传任何数据。

| 排名 | 功能 | 类型 | 需求证据（摘要） | 工作量 | 需要的权限 | 阶段 |
|---|---|---|---|---|---|---|
| 1 | 本地抠图（透明 PNG，可接 Add BG；之后加证件照换底色） | 文件工具 | 美国 82.3 万/月；remove.bg 5,420 万访问；Trends 为 compress pdf 的 1.63 倍；中文“抠图”“证件照” | S–M（证件照 M） | 无 | 0 |
| 2 | 按目标大小压缩（图片 ≤N KB/MB；视频 8/10/25 MB） | 文件工具 | video compressor 9.05 万/月；知乎 275.1 万；印度“100kb”约 10.8 万；Clop 安装速度 4.65 倍 | 图片 S / 视频 M | 无 | 0 |
| 3 | RAR/7z/ISO 解压、分卷拼接、加密 ZIP、干净 ZIP（去 .DS_Store） | 文件工具 | 知乎 391.9 万；.7z 25.7 万浏览；7-Zip 约 21.1 万安装；RAR 的 Mac 占比约 55% | S（分卷 S–M） | 无 | 0 |
| 4 | 防休眠（定时、直到拷贝完成；◎ 菜单和拖放） | 小工具 | Raycast Coffee 15.6 万（第 15）；22.4 万浏览 | S | 无 | 0 |
| 5 | 工具对外开放：App Intents + `Swivel --mcp`（MCPB），分 plan/apply 两步，带审计与撤销 | 基础设施 | 行业已收敛到“连接器优先”；Claude、Raycast、Codex 都能当宿主；Clop 已有 MCP | M | 无 | 1 |
| 6 | 存储医生（系统数据、快照、iCloud 本地副本、大文件、微信数据讲解；只移到废纸篓） | 助理 | 3,723 个“我也是”；22.9 万、14.0 万、10.9 万浏览；Mole 7.2 万星；知乎 182.8 万；微信 213.5 万 | M–L | 无（完全磁盘访问可选，默认不开） | 2 |
| 7 | 一句话指令 + 多模型路由（Quick Actions 里先预览计划，再执行） | 基础设施 / 助理 | 大陆没有苹果模型；DeepSeek 每次约 ¥0.03；Haiku 每次约 $0.003 | M | 无（用户自带密钥） | 2 |
| 8 | 卡顿/卡死诊断与强制退出 | 助理 | 6.05 万/月；159.6 万、61.9 万浏览；Stats 14.5 万装机 | M | 无 | 2 |
| 9 | PDF 加解密与可搜索 OCR（含中日韩文） | 文件工具 | sign 4.64 倍、ocr 2.94 倍、unlock 2.55 倍增长；unlock 1.48 万/月 | S–M | 无 | 0–2 |
| 10 | 音视频转文字 TXT/SRT（之后可把字幕烧进视频） | 文件工具 | speech to text 3.31 万/月；Whisper 21.9 万安装；转写 GUI 安装速度 1.5 倍 | M（macOS 26） | 无（首次需下载系统语言资源） | 2 |
| 11 | “打不开/已损坏”、后台项目与权限向导（精确打开设置面板） | 助理 | 5,057 个“我也是”；Sequoia 取消 Control 点开；B 站 12.4 万；CSDN 四篇破万 | S–M | 无（重置操作需管理员，由用户执行） | 2 |
| 12 | 外置硬盘医生（没检测到/推不出/NTFS 只读讲解/格式化为 exFAT） | 助理 | 19.1 万、43.3 万浏览；知乎 96.9 万 | M | 无（格式化需明确确认） | 2 |

小件可以和阶段 0 一起交付：PNG→ICO/ICNS（S）、校验和（S）、图片取色（S，需在打包后的 App 里确认不弹屏幕录制授权）、GIF 去重帧压缩（S–M）、视频变速/旋转/抽帧（S）、图片翻译（M，macOS 26），以及两个清理项：隐藏无效的 WEBP 输出、修正 DMG 被误判为压缩包。

| 阶段 | 内容 | 模型 | 权限 | 进入下一阶段的条件 |
|---|---|---|---|---|
| 0 高需求文件补齐 | 第 1–4、9 项和上面的小件 | 不用模型 | 不新增 | 测试通过，并用真实 Finder 拖放验证（AGENTS.md 里仍是未验证项） |
| 1 让任何代理都能调用 Swivel | 第 5 项：App Intents、MCP stdio、MCPB、plan/apply 两步、撤销日志 | 不需要（由宿主提供模型） | 不新增；不监听任何网络端口 | 在 Claude Desktop 或 Raycast 中跑通“转换 + 预览 + 撤销”的完整流程 |
| 2 Swivel 自己的 Mac 医生 | 第 6–8、10–12 项，加网络检查 | 端侧 Apple 模型（非大陆可用时）解析意图；Haiku 5.5 或 DeepSeek flash 生成摘要；可选 Ollama | 不新增；管理员操作只展示命令；完全磁盘访问可选 | 诊断建议的采纳率高、零误删 |
| 3 看懂用户指定的窗口 | 截图卡片 / `SCContentSharingPicker` + OCR，解释报错对话框；按 App 授权的 Automation | 带视觉能力的云端模型 | Automation（按 App 询问）；Picker 的授权行为需先验证 | 前三个阶段被证明有价值 |
| 4 可选的 GUI 操作（默认关） | 按 App 白名单、硬性禁止名单、操作提示层与停止键；或直接转交 Claude/Codex/豆包 | 前沿模型（Sonnet/Opus 5.5、Qwen3.8-Max） | 辅助功能 + 屏幕录制（每月重新确认，从此无法上架 App Store） | 只有在用户明确需要、且转交方案不够用时才做 |
