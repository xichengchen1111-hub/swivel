# Codex 任务：Swivel 第二批媒体功能

> 把这整份文件交给 Codex。仓库根目录的 `AGENTS.md` 是这个项目的规矩，必须先读完并遵守。这份文件只补充这次任务要做什么、做到什么程度。

## 背景

Swivel 是一个原生 macOS 菜单栏 App（Swift、AppKit，没有第三方依赖）。在 Finder 里拖着文件轻点修饰键，会弹出一个轮盘，把文件拖到某一项上就能完成转换或打开编辑器。

调研报告 `reports/Swivel 媒体编辑增强调研.md` 把要补的媒体功能分成了两批，第一批已经在分支 `media-editing` 上做完：
- Edit 基础面板和滤镜预设（`PhotoAdjust.swift`、`PhotoEditors.swift` 里的 `AdjustEditor`）
- 背景虚化（`BackgroundBlur`、`BlurBackgroundEditor`）
- 改尺寸…（`Images.resize`、`ResizeForm`）
- 分页的视频编辑器（`TrimEditor.swift`、`VideoEdit.swift`）

第二批报告里的“视频调色”已经包含在视频编辑器的 Color 页里，这次不用再做。

**这次要做的是第二批剩下的 6 项，按下面的顺序，每项单独一个提交。** 每项开工前先读相关的现有代码，照着那里的写法来。

## 工作方式

1. 从 `media-editing` 拉新分支 `codex/media-batch2`，在这个分支上工作。不要推送，不要合并到 `main`。
2. 每做完一项：
   - 跑 `./test.sh`，必须全部通过。
   - 提交一次。提交信息用英文，末尾加上代表你自己的 `Co-Authored-By` 行。
3. 每项都写进 `AGENTS.md`：在 “Photo and video editing upgrade” 那一节后面加一小段，写清楚：
   - 做了什么
   - 怎么验证的
   - **哪些没能验证**。你的沙箱可能跑不了 Vision、语音识别、HEIC 或 H.264，跑不了就如实写下来，不要声称测过。
4. 只要碰到下面任何一种情况，先停下来，在提交说明和 `AGENTS.md` 里写明，**不要自己绕过去**：
   - 需要新的隐私权限（麦克风、屏幕录制、辅助功能、完全磁盘访问）
   - 需要联网
   - 需要打包模型
   - 需要第三方代码

## 每项都要遵守的规矩（细节见 AGENTS.md）

- **写文件**：只能通过 `Paths.create`、`Paths.publish` 或 `Paths.produce`。结果放在原文件旁边，永不覆盖，也不新建文件夹。
- **界面文字**：一律用 `L("English")` 包起来，并在 `Strings.table`（`Localization.swift`）里补上简体中文、繁体中文和日文翻译。
  - 加新键之前先 grep，确认没有重复。字典里有重复键，App 一启动就会崩溃。
  - 注意同一个英文词在不同地方含义不同。例如 “Mono” 已经被音频的“单声道”占用了。
- **菜单项标题**：英文标题就是保存在 Customize 里的 ID，以后不能改名。新项插在相关的现有项旁边，并在测试里固定它的位置（参照 `PhotoEditTests.imageToolsOfferResizeAndBlurNextToTheirNeighbours`）。
- **手感**：按 AGENTS.md 的 *Feel* 规则来。滑块用 `DetentSlider`（在 `Editor.swift`），预览用 `LiveRender`。动起来的东西用 `Spring` 加 `FrameDriver`，不用 Timer 或固定时长的动画。
- **编辑器窗口**：继承 `Editor`。行用 `addRow` 或 `makeRow`；多个分页就照 `AdjustEditor` 的写法：顶部一个分段控件，下面各页叠放在一个等高的盒子里。
- **MCP**：会弹窗或打开编辑器的功能，不要放进 `MCPCatalog.output`。能一步完成、不需要再问用户的功能可以放，但必须写好输出文件名的预测，并补 `MCPTests`。
- **最低系统**：项目最低支持 macOS 15（`Package.swift`），开发机是 macOS 26.6。
  - 只有 macOS 26 才有的接口，一律包在 `if #available(macOS 26, *)` 里。
  - 在 macOS 15 上，这个菜单项要么隐藏，要么给出明确提示，不能崩溃。
  - **API 的名字和参数，以本机 SDK 头文件为准**（`xcrun --show-sdk-path` 下的 `.framework/Headers`，以及 `.swiftinterface`）。不要凭记忆写，这份文件里提到的 API 名字也要逐个核对。
- **视频**：用 `Video.openAsset` 或 `Video.safeAsset` 打开。导出按 `VideoEdit.export` 的写法：先写到隐藏的临时文件，完成后再发布。长时间任务要支持取消，取消后不能留下半成品。
  - 注意：`AVAssetReader` 的 `startReading` 和 `cancelReading` 不能在两个线程上同时跑，参考 `Video.encodeTarget` 里的锁。

---

## 第 1 项：去除物体 / 修复画笔（工作量最大，单独占一个提交）

**用户看到的**
- 图片工具轮盘里新增 “Retouch”（中文“修复”），放在 “Redact” 前面，打开一个编辑器：
  - **画布**：用画笔涂掉路人、电线、污点，可以涂多笔。松手后自动补上背景，结果直接显示在预览里。
  - **控件**：
    - “Brush Size” 画笔大小滑块
    - ⌘Z 撤销，按笔划撤销，用 `Editor.history`
    - “Show Original” 按住对比，`ImagePreview` 已经支持按住看原图
  - **应用**：输出 “photo retouched.jpg”，格式与原图相同。
- 这个功能只叫“去除物体 / 修复”，**界面和文档里都不要出现“去水印”**（原因见调研报告的“不建议做”）。

**实现**
- 系统没有公开的修补接口，所以自己用 Swift 实现。只能参考论文，不能复制任何代码：
  - Barnes et al. 2009, *PatchMatch: A Randomized Correspondence Algorithm for Structural Image Editing*
  - Wexler, Shechtman, Irani 2007, *Space-Time Completion of Video*：用 EM 方式由粗到细补全
  - 小面积污点可以改用 Pérez et al. 2003, *Poisson Image Editing* 的泊松融合，更快
- 可以看 PhotoCraft（github.com/storytold/photocraft，MIT/Apache）的 `crates/algo/src/inpaint.rs` 和 `poisson.rs` 文件头注释，了解它引用了哪些论文、用了哪些参数。**不要照抄它的代码**，这是项目规矩。
- **性能目标**：
  - 预览图长边约 1400 px，松手后 1 秒内给出结果。
  - 保存时按原图分辨率重算：只处理蒙版周围的区域，不要整张图都跑。
  - 可以用 Accelerate 或 Metal 加速，但不能引入任何第三方依赖。
- 画笔交互参考 `RedactCanvas`（`PhotoEditors.swift`）和 `AnnotateCanvas`（`AnnotateEditor.swift`）。
- 补全要放在后台跑，新的一笔会让上一次还没跑完的补全失效（`LiveRender` 的“最新请求优先”）。

**测试**（`Tests/SwivelTests/RetouchTests.swift`）
- **纹理补全**：在规则条纹或棋盘格纹理上挖一块方形，补全后蒙版区域和周围纹理的颜色差异要低于阈值；同时证明直接用平均色填充达不到这个阈值，说明测试真能区分好坏。
- **蒙版外不动**：蒙版外的像素与原图逐像素相同。
- **边界情况**：蒙版贴着图片边缘、蒙版覆盖 90% 的画面、1×1 的蒙版，都不能崩溃。
- **输出**：文件名正确，原文件没有被改动。

---

## 第 2 项：视频字幕 / 音频转文字

**用户看到的**
- **视频**：工具轮盘新增 “Subtitles”（中文“字幕”）。完成后在原视频旁边生成 “clip.srt”，并提示可以在视频编辑器里把字幕烧进画面。
  - 烧录这一步可以放到视频编辑器（`TrimEditor`）里，加一个 “Subtitles” 开关，或者单独做一页。二选一，在 `AGENTS.md` 里写明选了哪个。
- **音频**：工具轮盘新增 “Transcribe”（中文“转文字”），生成 “talk.txt”，加一个可选的 .srt。
- **语言**：默认跟随 App 语言，设置里可以改，至少支持简体中文、英文、日文。
- **语言资源**：第一次用某种语言时，系统可能要下载语言资源。先用一个对话框说明这件事，用户同意后才下载，并显示进度。

**实现**
- 只用 macOS 26 的 `SpeechAnalyzer` 和 `SpeechTranscriber`（Speech 框架），语言资源由 `AssetInventory` 管理。先读头文件，确认结果里怎么拿到逐词时间。
- macOS 15 上隐藏这两个菜单项。不要退回 `SFSpeechRecognizer`：它会弹语音识别授权，而且这台机器上只有英文能在本地识别。
- **必须在本机完成，不能联网。** 如果 API 有“仅本地”之类的选项，一定要打开。确认它不会触发麦克风或语音识别的授权弹窗；如果会，停下来报告。
- **SRT 格式**：
  - 按句子或停顿切分，每条最长约 7 秒、每行最多约 42 个字符。中文和日文按字符数计算。
  - 时间格式是 `HH:MM:SS,mmm`。
  - 序号连续，时间不重叠。
- **烧录字幕**：用 `AVVideoCompositionCoreAnimationTool`，或者在现有 CI 视频合成（`VideoEdit.frame`）里叠加文字图层。字体用系统字体，加半透明底板，避开画面底部的安全区。

**测试**
- **SRT 生成**（纯函数）：给定一串“词 + 时间”，检查切分、时间格式、中文按字符切行、空输入。
- **集成测试**：用 `say -o` 生成一段已知内容的英文音频，转写后包含预期的关键词。这一项要用 `#available` 和 `.enabled(if:)` 限制在 macOS 26 上，并且语言资源已安装时才跑。在你的沙箱里跑不了的话，在 `AGENTS.md` 里注明。

---

## 第 3 项：图片和视频放大

**用户看到的**
- 图片工具轮盘新增 “Upscale”（中文“放大”），打开一个小对话框：2× 或 4×，显示放大后的尺寸，输出 “photo 2x.png”。格式与原图相同，原图是 JPEG 就保持 JPEG。
- 视频：在视频编辑器的 Picture 页加一个 “Upscale” 选项（2×），或者在轮盘上单独加一项。二选一，并写明理由。
- 第一次使用时，如果系统需要下载 Apple 的放大模型，先用对话框说明并征得同意，下载时显示进度，然后才开始处理。

**实现**
- macOS 26：用 VideoToolbox 的超分辨率接口（`VTSuperResolutionScalerConfiguration` 一类，名字以头文件为准）。
  - 注意输入尺寸上限：图片长边约 1920 px，视频约 1080p。
  - 超过上限的图，先分块处理再拼接，接缝处要做重叠混合；或者明确告诉用户“这张图太大，最多放大到……”。
- macOS 15：退回 `CILanczosScaleTransform`。界面上写清楚这只是普通放大，不是 AI 放大。
- 不打包任何模型。

**测试**
- **尺寸**：输出正好是 2 倍或 4 倍，超过上限的图走分块或提示。
- **文件**：输出名正确、原图不变。
- **画质**：在 macOS 26 上（`.enabled(if:)`），用一张先缩小的测试图，放大后与原图的 PSNR 要高于 Lanczos 的结果。跑不了的话在 `AGENTS.md` 里说明。

---

## 第 4 项：人声降噪 / 增强

**用户看到的**
- 音频的工具轮盘，以及视频编辑器的 Sound 页，新增 “Reduce Noise”（中文“降噪”），带一个强度滑块。音频输出 “talk clean.m4a”，视频就在导出时一起处理。
- 只针对人声：空调声、风扇声、键盘声。不做人声和伴奏分离，那需要模型，不做。

**实现**
- **优先**用系统的 `AUSoundIsolation`（AudioToolbox，macOS 13 起就有，`kAudioUnitSubType_AUSoundIsolation`），参数在 `AudioUnitParameters.h` 里。先确认它能不能离线处理文件：用 `AVAudioEngine` 的手动渲染模式（`enableManualRenderingMode`）。
- 如果离线用不了，**备选**自己用 vDSP 写 STFT 谱门限降噪：先估计噪声谱（取最安静的一段，或者整段的低分位数），再做带平滑的谱减法，避免“音乐噪声”。
  - 可以看 FilmCraft（github.com/storytold/filmcraft）`crates/audio-dsp/src/effects/restoration.rs` 的文件头注释，了解它引用了哪些文献。**同样只参考思路，不照抄代码。**
- 视频的处理：视频轨照常导出，音轨先经过同一个处理，再合进最终文件。

**测试**
- 生成 “440 Hz 正弦波 + 白噪声”，处理后：
  - 噪声段（只有噪声的那几秒）的能量下降超过 10 dB
  - 正弦波频点的能量下降不超过 3 dB
- 时长不变（误差在 20 ms 以内），声道数不变，原文件不变。

---

## 第 5 项：证件照

**用户看到的**
- 图片工具轮盘新增 “ID Photo”（中文“证件照”），打开编辑器：
  - **尺寸预设**：一寸 295×413、二寸 413×579、护照 33×48 mm（按 300 dpi 算）、美国签证 600×600。最后一项“自定义”，输入毫米或像素。
  - **底色**：白、蓝（#438EDB）、红（#D9001B），也可以自定义颜色。
  - **人脸位置**：自动居中，可以手动微调缩放和上下位置。
- 输出 “photo id.jpg”，按所选 dpi 写入元数据。
- 界面上加一行小字：“仅供便捷使用，正式用途请以办理机构要求为准”，并补上翻译。

**实现**
- 人像蒙版用 Vision 的 `VNGeneratePersonSegmentationRequest`（质量选 `.accurate`），或者现有的 `BackgroundBlur.subjectMask`。头发边缘要做羽化。换底色时参考 `BackgroundBlur` 的处理，避免边缘出现原来背景的颜色。
- 人脸关键点用 `VNDetectFaceLandmarksRequest`，用眼睛和下巴的位置来决定缩放和裁剪。
  - 规则可以简化为：头顶到下巴约占画面高度的 70%，双眼连线在画面上方约 45% 的位置。
  - 把这些比例做成可以测试的纯函数。
- **出错提示**：找不到人脸时明确说明，检测到多张人脸时让用户选择或给出提示，**不要静默裁掉**。

**测试**
- **裁剪计算**（纯函数）：给定人脸框和关键点，检查输出的裁剪框比例、位置，以及出界时怎么处理。
- **换底色**：用合成图（人形蒙版加纯色背景）检查边缘没有残留原来背景的颜色，参照 `PhotoEditTests` 里的光晕测试，并做反向验证，证明测试真能抓出问题。
- **输出**：尺寸正好等于预设，dpi 元数据正确。

---

## 第 6 项：配乐时人声出现自动压低音乐（自动闪避）

**用户看到的**
- 视频编辑器的 Sound 页新增 “Music”：
  - 选择一个音频文件作为配乐。
  - 音乐音量滑块。
  - “Lower music when someone speaks” 开关（中文“有人说话时压低音乐”），以及压低的程度，默认 −12 dB。
- 音乐比视频短就循环，比视频长就截断，结尾自动淡出。

**实现**
- **人声检测**：
  - 用 SoundAnalysis 的内置分类器（`SNClassifySoundRequest`，`.version1`，"speech" 类别），对原视频的音轨离线分析。
  - 备选：基于能量和过零率的简单语音活动检测。
- **生成音量曲线**：把检测到的人声区间合并（间隔小于 0.5 秒就连起来），再给音乐轨生成音量曲线，用 `AVMutableAudioMixInputParameters.setVolumeRamp` 实现。压下去约 0.3 秒，抬回来约 0.8 秒，避免忽大忽小。
- **集成**：在 `VideoEdit.export` 里加第二条音轨，`VideoSettings` 加对应字段，并保证 `isTrimOnly` 的判断仍然正确。预览也要能听到效果。

**测试**
- **区间合并和音量曲线**（纯函数）：检查边界、重叠区间，以及空的人声区间。
- **集成测试**：用 `say -o` 生成人声，配上一段正弦波音乐，导出后，人声区间内音乐那条音轨的电平要明显更低。可以分别导出两条音轨来测。
- 循环和截断后的长度要准确。

---

## 全部做完时的标准

- `./test.sh` 全部通过，连跑两次都通过。
- `./build.sh` 能成功构建，签名是 `adhoc,runtime`，没有新增 entitlements。
- 每个新菜单项都在正确位置，四种语言都有翻译，并有测试固定它的位置。
- `AGENTS.md` 的代码地图和这一节都已更新，每一项都写了“验证了什么、没能验证什么”。
- 分支 `codex/media-batch2` 上有 6 个提交，没有推送，也没有安装到 /Applications。安装和在真实窗口里测试，留给 Claude Code 或主人来做。
