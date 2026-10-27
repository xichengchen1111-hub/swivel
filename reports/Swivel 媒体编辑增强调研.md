# 媒体编辑增强：参考 ArtCraft 全家桶，补高频的图像、视频、音频编辑

调研日期：2026-10-22。只读调研，没有下载或运行任何第三方代码。

## 结论

Swivel 的定位是“拖一个文件、做一件事”。下一步要补的是**普通人最常用的那一小撮编辑**，不是把 Photoshop 或 Premiere 整个搬进来。按使用证据和实现成本，分两批做：

- **第一批**：各项都是小工作量（S），用系统框架就能做，不新增权限，也不需要模型。
  - Edit 编辑器扩成 Lightroom 式的“基础面板”：高光、阴影、白色、黑色、自然饱和度、曲线、自动增强、降噪，再加一组滤镜预设。
  - 背景虚化。
  - 视频变速、裁画面改比例、旋转。
  - 音频淡入淡出、响度标准化、去静音、变速。
- **第二批**：中到大工作量（M–L），价值最高。
  - **去除物体 / 修复画笔**：参考 PatchMatch 论文用 Swift 自己实现。系统没有这个接口，这是和系统“预览”拉开差距的最大一项。
  - **视频字幕 / 音频转文字**：macOS 26 SpeechAnalyzer，模型由系统管理。
  - **图片和视频放大**：macOS 26 VTSuperResolutionScaler。
  - **人声降噪**：系统 AUSoundIsolation，加上谱减法作后备。
  - **视频调色**：复用 Edit 的滤镜，套到视频上。

ArtCraft 的价值在于给出了“哪些功能该有”的完整清单，以及每个算法引用的论文。按 Swivel 的规矩，代码不能照搬，要从论文出发重写。

## ArtCraft 是什么

名字叫 ArtCraft 的东西有两个，都出自 storytold（前 FakeYou 团队）。

| 项目 | 是什么 | 许可证 | 和 Swivel 的关系 |
|---|---|---|---|
| [storytold/artcraft](https://github.com/storytold/artcraft)（3.5 万星） | AI 创作工具：文生图、图生视频、3D 摆拍 | MIT 或 Apache-2.0（2026-10-10 从“fair-source”改过来） | 弱。生成类功能全部依赖云端模型。连它的抠图也走云端（fal rembg），而 Swivel 用 Vision 已经在本机做了。去水印在代码里只是一个“等 2 秒报成功”的空壳 |
| **ArtCraft Crafting Apps**（2026-09-30 公开） | 用 Rust 写的仿 Adobe 全家桶 | 都是 MIT 或 Apache-2.0；ArtCraft 名称和图标除外 | **强**，这是你说的“全家桶源代码” |

全家桶里和媒体相关的几款如下。成熟度百分比是各项目 README 的自评。

| 应用 | 对标 | 星数 | 成熟度 | 值得参考的点 |
|---|---|---|---|---|
| [PhotoCraft](https://github.com/storytold/photocraft) | Photoshop | 7.1 万 | 早期 alpha | 67 个工具、16 种调整图层、上百个滤镜；内容识别填充、修复画笔、移除工具都是按论文实现的 |
| [LightCraft](https://github.com/storytold/lightcraft) | Lightroom | 1.4 万 | alpha，日常可用度约 61% | 基础面板（曝光/高光/阴影/白/黑）、HSL、分级色轮、去雾、41 个预设 |
| [FilmCraft](https://github.com/storytold/filmcraft) | Premiere | 1.6 万 | alpha，约 56% | 剪辑与调色；自写音频处理库：降噪、去嗡声、去爆音、自动闪避、EBU R128 响度 |
| [EffectCraft](https://github.com/storytold/effectcraft) | After Effects | 7.6 千 | alpha，约 45% | 306 个特效、跟踪、Roto；对 Swivel 太重 |
| [SoundCraft](https://github.com/storytold/soundcraft) | Pro Tools（不是 Audition） | 3.5 千 | pre-alpha | 去静音、淡入淡出形状；本身没有降噪模块 |

需要注意的几点：

- **可信度**：这套软件主要由 AI 写成。PhotoCraft 有 2,934 个未关闭的 issue，README 自己也说“菜单接通了，不等于功能真的能用”。所以把它当**功能清单和论文索引**最合适，不要把它的实现当标准答案。
- **法律争议**：issue [#2016](https://github.com/storytold/artcraft/issues/2016)、[#2289](https://github.com/storytold/artcraft/issues/2289)、[photocraft#341](https://github.com/storytold/photocraft/issues/341) 认为，界面、菜单、快捷键都和 Adobe 几乎一样，有商业外观和商标风险；还有人质疑 LLM 生成的代码算不算“净室实现”。维护者没有回应。SoundCraft 这个名字还和 Harman 的调音台品牌撞了。
- **对 Swivel 的含义**：只借鉴功能清单，算法从它们引用的论文出发，用 Swift 和系统框架重写。界面保持 Swivel 自己的风格，不模仿 Adobe。

## 什么功能用得最多

没有任何厂商公开按功能统计的使用率；只有 Canva 公布过，它的一键抠图被用了 20 亿次。所以下面用三类代理指标排序：

- 美国 Google 月搜索量（seodata.dev，基于 Google Ads 数据）
- 单一用途网站的访问量（Semrush，2026 年 9 月）
- 大厂“快捷操作”清单（Adobe Express、iLoveIMG、CapCut）

数字只能看数量级。

**Swivel 已经覆盖了最头部的需求**：抠图（82.3 万/月，remove.bg 每月 1.06 亿访问）、HEIC 转 JPG（24.6 万）、图片转 PDF（20.1 万）、压缩、OCR、视频转 GIF（9.05 万）、视频压缩（9.05 万）、提取音频、静音、合并、裁剪时长。

**还缺、而且有需求证据的：**

| 类别 | 功能 | 需求信号 |
|---|---|---|
| 图像 | 去除物体 / 路人 | “remove object from photo”约 10 个同义词，每个约 1.48 万；Adobe Express 有“Remove Objects” |
| 图像 | 放大 / 变清晰 | “image upscaler”4.95 万，“image enhancer”4.05 万；imgupscaler 每月 625 万访问 |
| 图像 | 一键改尺寸 | “image resizer”11 万；Adobe Express 主推。Swivel 只有“Resize 50%”和图片预设 |
| 图像 | 自动增强 / 降噪 | “enhance photo quality”2.71 万；Lightroom 的 AI 降噪是近年的招牌功能 |
| 图像 | 背景虚化 | “blur background”1.21 万 |
| 视频 | 字幕 | Clideo 的头号流量词；CapCut 招牌功能；转写站 TurboScribe 每月 3,640 万访问；69% 的美国人在公共场合静音看视频 |
| 视频 | 变速 | “speed up video”6.6 千；Adobe Express 有这项快捷操作 |
| 视频 | 裁画面 / 改比例 / 旋转 | Adobe Express 有 Crop / Resize Video；CapCut 有 Auto Reframe |
| 音频 | 转文字 | “transcribe audio to text”2.22 万，另有约 10 个同义词 |
| 音频 | 人声降噪 / 增强 | 搜索量小（约 3 千），但 CapCut 的 Voice Enhancer 是主打功能，播客和会议录音常用 |
| 音频 | 人声分离 | “vocal remover”7.4 万；vocalremover.org 每月 1,750 万访问 |

## 推荐清单

工作量：S 为一两天，M 为约一周，L 为一周以上。“依据”一栏写的是要参考的论文或系统接口，而不是要拷的代码。

### 第一批：快速补齐（不新增权限、无模型）

| # | 功能 | 用户看到的效果 | 实现 | 量 |
|---|---|---|---|---|
| 1 | **Edit 升级为“基础面板”** | 在现有 6 个滑块上加高光、阴影、白色、黑色、自然饱和度、色调曲线，再加“自动”按钮和“降噪”滑块 | `CIHighlightShadowAdjust`、`CIToneCurve`、`CIVibrance`、`CIImage.autoAdjustmentFilters`、`CINoiseReduction`；手感沿用现有 DetentSlider | M |
| 2 | **滤镜预设（Looks）** | Edit 顶部一排缩略图：胶片、黑白、暖调、褪色等，配一个“强度”滑块；也可以直接出现在轮盘上 | 每个预设是一组滑块值加可选的 LUT（`CIColorCube`），参数自己定，不抄 Lightroom | S |
| 3 | **背景虚化** | 拖进人像或物体照片，背景变模糊，可调强度 | Vision 前景蒙版（Cutout 里已经有）加 `CIMaskedVariableBlur` | S |
| 4 | **一键改尺寸** | 轮盘上加“Resize…”，常用尺寸有 1080 宽、长边 2048、头像 512 等，也能输入自定义值 | 现有 Images 缩放管线，输出仍写在原文件旁边 | S |
| 5 | **视频变速** | 0.5×、1.5×、2× 档位；音频保持原音调 | `AVMutableComposition.scaleTimeRange` 加音频的 `AVAudioTimePitchAlgorithm` | S |
| 6 | **视频裁画面 / 改比例 / 旋转** | 9:16、1:1、16:9，可拖动取景框；旋转 90° 和翻转 | `AVVideoComposition` 加 `preferredTransform`；取景框界面复用 CropEditor | M |
| 7 | **音频淡入淡出** | 轮盘一键加 0.5 秒或 2 秒淡入淡出；Trim 编辑器里也能调 | `AVAudioMix` 的音量渐变 | S |
| 8 | **响度标准化** | 一键把音量调到 −16 LUFS（播客）或 −14 LUFS（流媒体） | 按 ITU-R BS.1770 测响度，再加增益和峰值限制（`AUPeakLimiter`） | S–M |
| 9 | **去除静音** | 自动删掉录音里的长停顿，停顿阈值可调 | 能量包络检测，再用 `AVMutableComposition` 拼接；也可以用 SoundAnalysis 判断是否有人声 | S–M |
| 10 | **音频变速 / 变调** | 0.75×–2× 慢放、快放，用于听课、听讲座 | `AVAudioUnitTimePitch` 离线渲染 | S |

### 第二批：拉开差距

| # | 功能 | 用户看到的效果 | 实现 | 量 | 系统 |
|---|---|---|---|---|---|
| 11 | **去除物体 / 修复画笔** | 新的“Retouch”编辑器：用画笔涂掉路人、电线、污点，松手自动补背景 | 系统没有公开接口（“照片”App 的“清理”是私有的）。按 Barnes 2009 的 PatchMatch 和 Wexler 2007 的 EM 补全实现，小瑕疵用 Pérez 2003 的泊松融合。可以用 Metal 加速。这些论文正是 PhotoCraft 的 `inpaint.rs`、`poisson.rs` 所引用的 | L | 15 |
| 12 | **视频字幕 / 音频转文字** | 拖进视频，选择生成 SRT 字幕文件或把字幕烧进画面；拖进音频，输出 TXT 或 SRT | `SpeechAnalyzer` / `SpeechTranscriber`，逐词带时间，模型由系统下载，不增加 App 体积；烧录用 `AVVideoCompositionCoreAnimationTool`。上一份报告实测过英文 0.28 秒，中文质量还要测 | M–L | 26 |
| 13 | **图片 / 视频放大** | 2× 或 4× 放大；第一次使用时说明需要下载苹果模型，并征求同意 | `VTSuperResolutionScaler`（图片长边 ≤1920，视频 ≤1080p）；macOS 15 上退回 `CILanczosScaleTransform`，并标明这是普通放大 | M | 26 |
| 14 | **人声降噪 / 增强** | 去掉录音里的空调声、键盘声、风声，人声更清楚 | 优先用系统 `AUSoundIsolation`（macOS 13+，离线处理文件是否可行要先验证）；后备方案是 STFT 谱门限降噪加去嗡声陷波，FilmCraft 的 `restoration.rs` 引用了相应文献 | M | 15 |
| 15 | **视频调色 / 滤镜** | 把第 1、2 项的滑块和预设用在视频上，带实时预览 | `AVVideoComposition(asset:applyingCIFiltersWithHandler:)` 复用同一套 CIFilter 链 | M | 15 |
| 16 | **证件照** | 选定尺寸（一寸、二寸、护照），自动换白、蓝、红底并按人脸位置裁好 | 人像分割、Vision 人脸关键点、固定尺寸表；上一份报告也推荐过 | M | 15 |
| 17 | **给视频配乐 + 自动闪避** | 把一段音乐拖到视频上，人说话时音乐自动变小 | 用语音活动检测得到人声区间，再生成音乐音量曲线（FilmCraft 的 `ducking.rs` 也是这个思路） | M | 15 |

### 可以晚一点再看

- **视频防抖**：可以用 Vision 的 `VNHomographicImageRegistrationRequest` 估计运动再反向补偿，工作量 L，效果难保证。
- **视频降噪**：`VTTemporalNoiseFilter`，需要 macOS 26。
- **视频抠人像**：逐帧人像分割，长视频很慢，质量一般。
- **文档矫正**：拍歪的纸张或白板拉正，用 `DetectDocumentSegmentationRequest` 加 `CIPerspectiveCorrection`，工作量 S–M，更接近 PDF 工具的方向。

## 不建议做

| 功能 | 原因 |
|---|---|
| 生成式填充、扩图、文生图、图生视频 | 必须依赖大模型（云端或数 GB 的本地权重）；ArtCraft 自己也全部走云端。和 Swivel 本地、无依赖的原则冲突 |
| 叫“去水印”的功能 | 搜索量很大（11 万/月），但去掉别人的水印涉及版权问题（DMCA §1202）。第 11 项去除物体在技术上能做到同样的事，但界面上只叫“去除物体 / 修复” |
| 人声分离 / 伴奏提取 | 真正的伴奏提取需要 Demucs 这类模型（MIT 许可，但几百 MB）。要做的话，先请你批准一个类似 Mac Doctor 那样的例外 |
| 黑白照片上色 | 需要打包模型 |
| 图层、时间线、关键帧 | 那是 PhotoCraft / FilmCraft 的路线，不是 Swivel 拖一下就完成的形态，工作量也会失控 |
| 直接拷 Crafting Apps 的代码 | 许可证允许，但 Swivel 的规矩是看思路后自己重写；而且这套代码主要由 AI 生成，质量和净室争议都没定论 |
| MP3 输出 | 系统没有 MP3 编码器，LAME 是 LGPL（上一份报告已经说过） |

## 建议的顺序

1. 第 1、2、3、4 项：一次图片编辑升级，用户感受最明显。
2. 第 5、6、7、8、9、10 项：一次音视频小工具补齐，每项都很小。
3. 第 11 项去除物体：单独作为一个版本做，最费工夫，也最有差异化。
4. 第 12、13 项：需要 macOS 26，在新系统上作为“额外能力”提供，旧系统上隐藏。
5. 第 14 至 17 项按反馈再排。

## 主要来源

- ArtCraft：<https://github.com/storytold/artcraft>（README、NOTICE、PR #2043、`appMenu.tsx`）
- Crafting Apps：<https://github.com/storytold/photocraft>、<https://github.com/storytold/lightcraft>、<https://github.com/storytold/filmcraft>、<https://github.com/storytold/effectcraft>、<https://github.com/storytold/soundcraft>；报道见 <https://designcompass.org/en/2026/10/19/artcraft-open-source-adobe-alternatives/>
- 搜索量：seodata.dev，例如 <https://www.seodata.dev/keyword/remove-object-from-photo>、<https://www.seodata.dev/keyword/image-upscaler>、<https://www.seodata.dev/keyword/vocal-remover>
- 网站访问量：Semrush，例如 <https://www.semrush.com/website/remove.bg/overview/>、<https://www.semrush.com/website/clideo.com/overview/>
- 快捷操作清单：Adobe Express <https://www.adobe.com/express/feature>、iLoveIMG <https://www.iloveimg.com/>、CapCut <https://www.capcut.com/tools>
- Canva 20 亿次抠图：<https://www.smartcompany.com.au/technology/artificial-intelligence/canva-dominated-generative-ai-australia-2023/>
- 系统接口：VTSuperResolutionScaler <https://developer.apple.com/documentation/videotoolbox/vtsuperresolutionscalerconfiguration>、SpeechAnalyzer <https://developer.apple.com/documentation/speech/speechanalyzer>
- 论文：Barnes et al. 2009 *PatchMatch*；Wexler, Shechtman, Irani 2007 *Space-Time Completion of Video*；Pérez et al. 2003 *Poisson Image Editing*；ITU-R BS.1770
