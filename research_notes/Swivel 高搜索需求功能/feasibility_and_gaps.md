# Swivel high-demand file features: technical feasibility on macOS 15 / 26 with system frameworks only, and gaps in the current code

Notes date: 2026-10-21. "Verified locally" means I ran code or a CLI on this machine (macOS 26.6.2 build 25G83, Apple M5 Pro, arm64, Swift 6.4, compiled against MacOSX26.sdk = SDK 26.5). Nothing was verified on a macOS 15 machine; macOS 15 statements come from SDK availability annotations and Apple documentation. Scratch scripts and outputs are in `file:///private/tmp/claude-501/-Users-wulizhi--codex--chatgpt-projects-g-p-6ad0c15852b08191845e68d96c6dcb35-sites-swivel/fcd810aa-62cd-4697-83ac-5092a29df1e3/scratchpad/feas/` (abbreviated below as SCRATCH; the full link is used in each citation). No repository file except this note was changed; no system setting was changed; the installed Swivel app was not run; nothing was installed or downloaded (no speech, translation or super-resolution model download was triggered).

## 0. Baseline: what Swivel already provides, and the summary verdicts

### Takeaway
Swivel already covers image format conversion, a quality/size compress editor, a backdrop ("Add BG") editor, image→PDF, PDF→PNG/JPG per page, PDF merge/split/compress/rotate/page arranging, video presets (MP4/MOV/HEVC/GIF/720p…), audio via `afconvert`, ZIP/TAR.GZ creation and extraction through `ditto`/`tar`, and single-image OCR. None of the 15 candidate features exist in finished form; several are one small step from existing code (target-size image compression, PDF→long image, 7z/ISO/XAR/CAB extraction which already flows through `tar`). Only MP3 encoding and encrypted 7z/RAR extraction are not achievable with system components.

### Cited Findings
- The package deploys to macOS 15 (`platforms: [.macOS(.v15)]`) and `build.sh` runs a plain `swift build -c release` (host architecture only) — [Package.swift](file:///Users/wulizhi/.codex/.chatgpt-projects/g-p-6ad0c15852b08191845e68d96c6dcb35/sites/swivel/Package.swift); [build.sh](file:///Users/wulizhi/.codex/.chatgpt-projects/g-p-6ad0c15852b08191845e68d96c6dcb35/sites/swivel/build.sh)
- Image wheel "tools" are Crop, Compress, Edit, Add BG, Redact, Metadata, Annotate, Resize 50%, Rotate, Flip, Grayscale, Make PDF, plus Extract Text / Read QR / presets / Collage when enabled — [Catalog.swift lines 199–232](file:///Users/wulizhi/.codex/.chatgpt-projects/g-p-6ad0c15852b08191845e68d96c6dcb35/sites/swivel/Sources/Swivel/Catalog.swift)
- Image conversion targets are JPG, PNG, HEIC, WEBP, TIFF, AVIF, GIF, BMP, filtered at runtime by `CGImageDestinationCopyTypeIdentifiers()`; WEBP is therefore silently dropped on a machine where ImageIO cannot write WebP (see §11) — [Images.swift lines 14–28](file:///Users/wulizhi/.codex/.chatgpt-projects/g-p-6ad0c15852b08191845e68d96c6dcb35/sites/swivel/Sources/Swivel/Images.swift)
- Video convert: MP4, MOV, HEVC, M4V, GIF, M4A, WAV, PNG Frame; video tools: Compress (`AVAssetExportPresetMediumQuality`), 1080p/720p/480p, Mute, Extract Audio, Frame, GIF, plus Trim/Merge — [Media.swift lines 5–40](file:///Users/wulizhi/.codex/.chatgpt-projects/g-p-6ad0c15852b08191845e68d96c6dcb35/sites/swivel/Sources/Swivel/Media.swift)
- Audio formats are M4A, WAV, AIFF, FLAC, ALAC, CAF, AAC (no MP3); tools are Compress, Mono, Voice Memo — [Media.swift lines 114–135](file:///Users/wulizhi/.codex/.chatgpt-projects/g-p-6ad0c15852b08191845e68d96c6dcb35/sites/swivel/Sources/Swivel/Media.swift)
- PDF convert: PNG, JPG (300 DPI per page), TXT, DOCX, RTF, HTML (embedded text only); tools: Compress, Split Pages, Rotate, Page Count, Merge, Crop, Arrange PDF Pages; `PDFs.open` throws "is password protected" for locked PDFs — [Documents.swift lines 95–117](file:///Users/wulizhi/.codex/.chatgpt-projects/g-p-6ad0c15852b08191845e68d96c6dcb35/sites/swivel/Sources/Swivel/Documents.swift)
- Archives: create ZIP (`ditto -c -k` / `zip -r`) and TAR.GZ; Extract uses `ditto -x -k` for `.zip` and `/usr/bin/tar -xf` for everything else, staging into a hidden folder next to the archive — [Documents.swift lines 211–265](file:///Users/wulizhi/.codex/.chatgpt-projects/g-p-6ad0c15852b08191845e68d96c6dcb35/sites/swivel/Sources/Swivel/Documents.swift)
- OCR: `VNRecognizeTextRequest`, accurate level, languages en-US/zh-Hans/zh-Hant/ja-JP, single image, text shown in a review window — [TextCapture.swift lines 4–21](file:///Users/wulizhi/.codex/.chatgpt-projects/g-p-6ad0c15852b08191845e68d96c6dcb35/sites/swivel/Sources/Swivel/TextCapture.swift)
- Searching the sources found no use of segmentation requests, PDF password options, watermarking, CryptoKit, IOPMAssertion, NSColorSampler, Speech, Translation, MetalFX/Lanczos, ICO/ICNS output, 7z/RAR/ISO, MP3, `scaleTimeRange`, AVAssetWriter, AVVideoComposition or `fileLengthLimit` (only `ProcessInfo.beginActivity` in the drag watcher, and the string "ICNS" in a demo-menu placeholder in `main.swift`) — [grep over Sources/Swivel](file:///Users/wulizhi/.codex/.chatgpt-projects/g-p-6ad0c15852b08191845e68d96c6dcb35/sites/swivel/Sources/Swivel/)
- The repo's own RESEARCH.md already records that "Add Background" is a backdrop/padding/shadow, not subject cut-out — [RESEARCH.md](file:///Users/wulizhi/.codex/.chatgpt-projects/g-p-6ad0c15852b08191845e68d96c6dcb35/sites/swivel/RESEARCH.md)

### Inferences
- Summary matrix (details and sources in §1–§15; "TCC" = a macOS privacy prompt):

| # | Feature | Already in Swivel? | Min macOS | TCC / network | Effort | Verdict |
|---|---|---|---|---|---|---|
| 1a | Subject lift → transparent PNG | No (Add BG is a backdrop) | 14 (Vision) | none / none | S–M | Feasible |
| 1b | ID photo (solid bg + standard crop) | No | 12 (person seg.) / 14 | none / none | M | Feasible (quality caveats on hair) |
| 2 | Image to target file size | No (quality slider + max px only) | 15 | none | S | Feasible |
| 3 | Video to target size | No (fixed presets only) | 15 | none | M | Feasible (needs verify-and-retry) |
| 4 | MP3 encoding | No | — | — | L/XL (own encoder) | Not feasible with system frameworks |
| 5 | 7z/ISO/XAR/CAB extract | Effectively yes via `tar` (untested in-app) | 15 | none | S (verify) | Feasible |
| 5 | RAR4/RAR5 extract | No (.rar not classified as archive) | 15 | none | S | Feasible (unencrypted) |
| 5 | Multi-volume RAR | No | 15 | none | S–M | Feasible via concatenated stdin |
| 5 | Encrypted ZIP extract | No | 15 | none | S–M | Feasible (`tar --passphrase`) |
| 5 | Encrypted 7z / RAR | No | — | — | — | Not feasible |
| 5 | 7z creation | No | 15 | none | S | Feasible (no encryption) |
| 6 | PDF password/permissions; remove password | No (locked PDFs rejected) | 15 (perms option 12+) | none | S | Feasible |
| 7 | Searchable PDF OCR | No | 13 built-in / 26 best | none | S (built-in) – M (custom CJK) | Feasible |
| 8 | Transcribe to TXT/SRT | No | 26 (SpeechTranscriber) | none observed; model download | M | Feasible on 26; partial on 15 |
| 9 | Image text translation | No | 26 (no-UI session) / 15 (SwiftUI) | none; language-pack download | M | Feasible on 26; partial on 15 |
| 10 | Upscaling | No | 13 (MetalFX) / 26 (VT SR) | VT SR model download | S / M | Partial |
| 11 | ICO / ICNS / favicon set | No | 15 | none | S | Feasible (WebP write not available) |
| 12 | Watermark, page numbers, signature stamp | No | 15 | none | M | Feasible |
| 13 | Images→PDF / PDF→long image | Images→PDF yes; long image no | 15 | none | S | Feasible |
| 14 | Keep-awake / color picker / checksum / duplicates | No | 10.15+ | none | S / S / S / M | Feasible |
| 15 | Video speed, rotate/flip, frames, subtitles, GIF optimisation | Single frame + GIF only | 15 | none | S–M | Feasible (soft subtitle track unverified) |

- None of the features above needs Accessibility, Screen Recording or Full Disk Access; the only privacy authorization in scope is Speech Recognition, which applies to the legacy `SFSpeechRecognizer` path and was not triggered by the macOS 26 `SpeechTranscriber` path in my test (§8).

### Gaps
- No macOS 15 machine was available; every macOS 15 claim is from availability annotations, not execution.
- Swivel itself was not run (by instruction), so "works through existing code" (e.g., 7z extraction) is inferred from running the same `tar` command line, not from the app.

## 1. Background removal / subject lift; ID/passport photo

### Takeaway
Not present. `VNGenerateForegroundInstanceMaskRequest` (macOS 14+) produced a clean transparent cut-out of a sample photo in 0.13 s locally, with no prompt or network. An ID-photo tool is buildable from `VNGeneratePersonSegmentationRequest` (macOS 12+) plus face landmarks for head-size cropping; size standards come from third-party/secondary sources and must be configurable.

### Cited Findings
- Existing "Add BG" (`BackgroundEditor`) "Places the photo on a colour or gradient backdrop with padding, rounded corners and a shadow"; it does not segment the subject — [PhotoEditors.swift lines 110–262](file:///Users/wulizhi/.codex/.chatgpt-projects/g-p-6ad0c15852b08191845e68d96c6dcb35/sites/swivel/Sources/Swivel/PhotoEditors.swift)
- `VNGenerateForegroundInstanceMaskRequest` is available from macOS 14.0 and "generates an instance mask of noticable objects to separate from the background" — [Apple: VNGenerateForegroundInstanceMaskRequest](https://developer.apple.com/documentation/vision/vngenerateforegroundinstancemaskrequest)
- SDK headers: `VNGeneratePersonSegmentationRequest` macOS 12.0, `VNGeneratePersonInstanceMaskRequest` macOS 14.0, `VNDetectFaceLandmarksRequest` macOS 10.13; `VNInstanceMaskObservation.generateMaskedImageOfInstances:fromRequestHandler:croppedToInstancesExtent:` returns a masked `CVPixelBuffer` — [MacOSX26.sdk Vision headers](file:///Library/Developer/CommandLineTools/SDKs/MacOSX26.sdk/System/Library/Frameworks/Vision.framework/Headers/)
- Verified locally: on `/Library/User Pictures/Animals/Parrot.heic` (512×512) the request found 1 instance in 0.13 s; `generateMaskedImage(...croppedToInstancesExtent: false)` written as PNG kept an alpha channel and visually separated the parrot cleanly; `VNGeneratePersonSegmentationRequest` (`.accurate`) also ran (its mask came back at its own resolution, 2016 px wide for the 512 px input, so it must be scaled to the source) — [SCRATCH images.swift](file:///private/tmp/claude-501/-Users-wulizhi--codex--chatgpt-projects-g-p-6ad0c15852b08191845e68d96c6dcb35-sites-swivel/fcd810aa-62cd-4697-83ac-5092a29df1e3/scratchpad/feas/scripts/images.swift); output [parrot_cutout.png](file:///private/tmp/claude-501/-Users-wulizhi--codex--chatgpt-projects-g-p-6ad0c15852b08191845e68d96c6dcb35-sites-swivel/fcd810aa-62cd-4697-83ac-5092a29df1e3/scratchpad/feas/img/parrot_cutout.png)
- Chinese ID-photo sizes (300 dpi): 1寸 25×35 mm = 295×413 px, 2寸 35×49 mm = 413×579 px; sources disagree on other variants (some give 413×626 for 2寸, 390×567 for 小二寸/33×48 mm used for passports/visas) — [pconline](https://www.pconline.com.cn/ask/29125.html); [bilibili](https://www.bilibili.com/read/cv48536058); [tixiaolu](https://www.tixiaolu.com/v2/posts/v2-d16cc769.html) (secondary sources)
- US passport digital photo: square, minimum 600×600 and maximum 1200×1200 px, head 1–1⅜ in (25–35 mm) chin-to-crown, white/off-white background; reported by third-party sites citing the State Department (travel.state.gov returned HTTP 403 to my fetch) — [photogov](https://photogov.net/documents/digital-passport-photo/requirements); [imresizer](https://imresizer.com/blog/us-passport-photo-size-requirements)
- One third-party site claims the State Department rejects digitally altered/AI-edited passport photos as of 2026 (unverified) — [WebSearch summary of passport-photo vendors](https://www.smartphone-id.com/en-us/passport-size-photo)

### Inferences
- Subject lift: new `Editor` subclass reusing `ImagePreview`/`LiveRender`, Vision call on a background task, PNG/HEIC-with-alpha output via existing `Images.write`; instance picking (click to include/exclude instances) is optional. Effort S–M. Min macOS 14 (Swivel's 15 floor is fine). No TCC, no network.
- ID photo: person segmentation → composite over solid blue/red/white, face landmarks → scale so head height matches the chosen standard → crop to preset pixels (295×413, 413×579, 600×600…). Quality limits: hair/fine-edge accuracy, glasses/shoulders, lighting mismatch; government rules about editing mean the tool should be labelled as a convenience, not a compliance guarantee. Effort M. Verdict: feasible without third-party code.
- Optional print-sheet layout (several 1寸 on a 6-inch photo) reuses Collage-style composition.

### Gaps
- Not tested on a real portrait (no person photo on the system); edge quality on hair is unmeasured.
- Whether the foreground-instance request runs on Intel Macs was not checked (Swivel builds host-arch only).
- No official Chinese standard document was retrieved; US figures are from secondary sites.

## 2. Compress an image to a target file size (e.g., ≤100 KB)

### Takeaway
Not present, and easy: the existing `CompressEditor` already re-encodes in memory; adding a bisection on `kCGImageDestinationLossyCompressionQuality` plus a downscale step via `kCGImageDestinationImageMaxPixelSize` is S effort. Local test shows quality alone often cannot reach small targets for large images, so downscaling must be part of the search.

### Cited Findings
- `CompressEditor` exposes a Quality slider (10–100 %), a max-size popup (Original, 3840, 2560, 1920, 1280, 800 px) and a format choice, re-encoding via `Images.encode`; there is no target-size mode — [CropEditor.swift lines 738–820](file:///Users/wulizhi/.codex/.chatgpt-projects/g-p-6ad0c15852b08191845e68d96c6dcb35/sites/swivel/Sources/Swivel/CropEditor.swift)
- Image presets store long edge, format, quality, strip-metadata — also no target size — [ImagePresets.swift](file:///Users/wulizhi/.codex/.chatgpt-projects/g-p-6ad0c15852b08191845e68d96c6dcb35/sites/swivel/Sources/Swivel/ImagePresets.swift)
- Verified locally on a 4000×4000 image (parrot Lanczos-upscaled): JPEG bytes at quality 1.0/0.9/0.7/0.5/0.3/0.1/0.0 = 8,813,733 / 1,651,869 / 1,104,317 / 652,640 / 432,225 / 359,829 / 356,749; HEIC at the same qualities = 6,225,295 / 782,665 / 372,507 / 212,721 / 110,664 / 61,470 / 44,415. An 8-step quality bisection for ≤100 KB JPEG at 4000 px found no solution (floor ≈357 KB) and took 0.15 s; with `kCGImageDestinationImageMaxPixelSize` 2000/1600/1200 at q 0.7 the JPEG was 414,769 / 303,339 / 200,082 bytes — [SCRATCH images.swift](file:///private/tmp/claude-501/-Users-wulizhi--codex--chatgpt-projects-g-p-6ad0c15852b08191845e68d96c6dcb35-sites-swivel/fcd810aa-62cd-4697-83ac-5092a29df1e3/scratchpad/feas/scripts/images.swift)

### Inferences
- Algorithm: for the chosen format, bisection on quality (≈6–8 encodes) at the current size; if the q≈0.4 result is still over target, reduce max pixel size by ~15–20 % and repeat; stop at a floor (e.g., 320 px) with a clear "can't reach" message. Encoding is fast (hardware-assisted on Apple silicon). Size is monotonic in quality in this test, so bisection is safe.
- JPEG is the right default for "upload ≤100 KB" use (HEIC is ~3× smaller but many upload forms reject it); offer HEIC/AVIF as alternatives. Strip metadata to save bytes (existing code already can).
- Min macOS 15 (ImageIO). No TCC/network. Effort S. Verdict: feasible.

### Gaps
- Not tested for PNG targets (lossless; only downscale or palette reduction would help, and ImageIO has no palette quantizer).

## 3. Video compression to a target size (e.g., under 10 / 25 MB)

### Takeaway
Not present. `AVAssetExportSession.fileLengthLimit` is not a dependable target-size control (ignored with the HighestQuality preset in my test). `AVAssetWriter` with `AVVideoAverageBitRateKey` alone overshot by 1.3–6×; adding `kVTCompressionPropertyKey_DataRateLimits` brought H.264 within ~0.73–1.06× of target. A target-size feature is feasible (M) with H.264 + data-rate limits + resolution choice + verify-and-retry.

### Cited Findings
- Existing video "Compress" is a fixed `AVAssetExportPresetMediumQuality` export; other tools are fixed 1080p/720p/480p presets — [Media.swift lines 31–40](file:///Users/wulizhi/.codex/.chatgpt-projects/g-p-6ad0c15852b08191845e68d96c6dcb35/sites/swivel/Sources/Swivel/Media.swift)
- Apple documents `fileLengthLimit` only as "The file length that the output of the session must not exceed" (macOS 10.14+) — [Apple: fileLengthLimit](https://developer.apple.com/documentation/avfoundation/avassetexportsession/filelengthlimit)
- `kVTCompressionPropertyKey_DataRateLimits` sets "Zero, one, or two hard limits on data rate", as alternating bytes/seconds pairs; "some codecs do not support limiting to specified data rates" — [Apple: kVTCompressionPropertyKey_DataRateLimits](https://developer.apple.com/documentation/videotoolbox/kvtcompressionpropertykey_dataratelimits)
- Verified locally on a synthetic 10 s 1280×720 source (13.7 MB, with AAC audio): `estimatedOutputFileLengthInBytes` vs actual = Highest 9.62 MB est / 13.37 MB actual; Medium 0.995 / 1.019 MB; Low 0.223 / 0.181 MB; 960×540 6.31 / 6.12 MB; 640×480 3.39 / 3.16 MB; HEVC Highest 8.90 / 8.85 MB. `fileLengthLimit` = 3 MB with HighestQuality gave 12.83 MB at full 10.0 s (ignored); with the 640×480 preset and a 1 MB limit it gave 1.12 MB at full 10.0 s (≈12 % over) — [SCRATCH av.swift / av2.swift](file:///private/tmp/claude-501/-Users-wulizhi--codex--chatgpt-projects-g-p-6ad0c15852b08191845e68d96c6dcb35-sites-swivel/fcd810aa-62cd-4697-83ac-5092a29df1e3/scratchpad/feas/scripts/av2.swift); [output log](file:///private/tmp/claude-501/-Users-wulizhi--codex--chatgpt-projects-g-p-6ad0c15852b08191845e68d96c6dcb35-sites-swivel/fcd810aa-62cd-4697-83ac-5092a29df1e3/scratchpad/feas/av/av2_out.txt)
- Verified locally, `AVAssetReader`→`AVAssetWriter` with only `AVVideoAverageBitRateKey`: synthetic noisy source, 3 MB target → H.264 8.72 MB (2.9×), HEVC 3.80 MB (1.27×); 1.5 MB target → H.264 8.49 MB (5.7×). On the 4K wallpaper video `/System/Library/Desktop Pictures/.wallpapers/Tahoe Day/Tahoe Day.mov` (first 20 s, written at 1920×1080): 4.25 MB target → H.264 6.50 MB (1.53×), HEVC 8.78 MB (2.06×); 1.42 MB target → H.264 3.07 MB (2.17×), HEVC 8.74 MB (6.17×) — [SCRATCH av3.swift](file:///private/tmp/claude-501/-Users-wulizhi--codex--chatgpt-projects-g-p-6ad0c15852b08191845e68d96c6dcb35-sites-swivel/fcd810aa-62cd-4697-83ac-5092a29df1e3/scratchpad/feas/scripts/av3.swift)
- Verified locally, same pipeline plus `kVTCompressionPropertyKey_DataRateLimits` = [target bytes per second, 1.0] inside `AVVideoCompressionPropertiesKey` (accepted by AVAssetWriter without error): H.264 1.42 MB target → 1.50 MB (1.056×); H.264 4.25 MB target → 3.10 MB (0.73×); HEVC 1.42 MB target → 2.13 MB (1.50×). `canPerformMultiplePasses` was false for both codecs in this pipeline — [SCRATCH av3.swift](file:///private/tmp/claude-501/-Users-wulizhi--codex--chatgpt-projects-g-p-6ad0c15852b08191845e68d96c6dcb35-sites-swivel/fcd810aa-62cd-4697-83ac-5092a29df1e3/scratchpad/feas/scripts/av3.swift)
- A naive reader/writer pump that drains video before audio deadlocks (the writer waits to interleave); `requestMediaDataWhenReady` on separate queues is required — [SCRATCH av2.swift](file:///private/tmp/claude-501/-Users-wulizhi--codex--chatgpt-projects-g-p-6ad0c15852b08191845e68d96c6dcb35-sites-swivel/fcd810aa-62cd-4697-83ac-5092a29df1e3/scratchpad/feas/scripts/av2.swift)

### Inferences
- Recommended design: budget = target × 0.92 − audio (e.g., 96 kbps AAC); pick output resolution from bits-per-pixel (drop to 720p/540p when the budget is low); encode H.264 with average bitrate + DataRateLimits; check the file size; if over, retry once at ~85 % bitrate or a lower resolution; show "can't fit" when the duration makes the target unreachable at a watchable quality. HEVC respected limits worse in this test, so H.264 is the safer default for "≤ N MB" (also best compatibility for chat apps).
- Never overwrite; output "name 10MB.mp4" via `Paths.output`. Effort M (new writer pipeline, progress UI, cancellation). Min macOS 15. No TCC/network. Verdict: feasible, with a verify-and-retry loop rather than a one-shot guarantee.

### Gaps
- Tests used a synthetic clip and a graphic wallpaper video, not camera footage or screen recordings; real-world overshoot may differ.
- Multi-pass encoding (`performsMultiPassEncodingIfSupported`) was not available in this pipeline; whether it is available for other configurations is unverified.

## 4. MP3 encoding

### Takeaway
macOS has an MP3 decoder but no MP3 encoder: AudioToolbox lists no `.mp3` encoder, `AVAudioFile` and `afconvert` fail with `'fmt?'`. MP3 output would require writing an encoder from scratch (MP3 patents have expired, but LAME and shine are LGPL and cannot be copied into Swivel). Verdict: not feasible with system frameworks; offer AAC/M4A (existing) and Opus-in-CAF instead.

### Cited Findings
- Verified locally: `kAudioFormatProperty_EncodeFormatIDs` = `aac aace aacf aacg aach aacl aacp alac alaw apac flac ilbc ima4 lpcm opus ulaw` — no `.mp3`; `kAudioFormatProperty_DecodeFormatIDs` contains MP3; `AVAudioFile(forWriting:)` with `kAudioFormatMPEGLayer3` failed with avfaudio error 1718449215 (`'fmt?'`) — [SCRATCH av.swift](file:///private/tmp/claude-501/-Users-wulizhi--codex--chatgpt-projects-g-p-6ad0c15852b08191845e68d96c6dcb35-sites-swivel/fcd810aa-62cd-4697-83ac-5092a29df1e3/scratchpad/feas/scripts/av.swift)
- Verified locally: `afconvert -hf` lists `'MPG3' = MPEG Layer 3 (.mp3…)` as a file type, but `afconvert -f MPG3 -d .mp3 tone.wav out.mp3` (and `.mp2`, `.mp1`, and `.mp3` inside CAF/M4A) fail with `Error: ExtAudioFileSetProperty ('cfmt') failed ('fmt?')`; `-f caff -d opus` succeeded (104,085 bytes for 10 s stereo); `-f Oggf -d opus` failed with `'pck?'` — [SCRATCH av directory](file:///private/tmp/claude-501/-Users-wulizhi--codex--chatgpt-projects-g-p-6ad0c15852b08191845e68d96c6dcb35-sites-swivel/fcd810aa-62cd-4697-83ac-5092a29df1e3/scratchpad/feas/av/)
- LAME is licensed under the LGPL; all relevant MP3 patents have expired (most sources date this to April 2017) — [Wikipedia: LAME](https://en.wikipedia.org/wiki/LAME)
- The shine fixed-point MP3 encoder's COPYING file is the GNU Library General Public License v2 (GitHub reports the license as NOASSERTION) — [toots/shine COPYING](https://github.com/toots/shine/blob/main/COPYING)

### Inferences
- A self-written encoder needs a polyphase analysis filterbank, MDCT with block switching, a psychoacoustic model, rate/distortion quantization loops, Huffman coding tables and a bit reservoir — thousands of lines plus extensive listening tests to be competitive with LAME; that is L/XL effort and outside Swivel's scope. It must be an independent implementation from the ISO/IEC 11172-3 description, not a port of LAME/shine (LGPL) or other GPL code.
- Pragmatic alternative: keep AAC (M4A/ADTS) which Swivel already outputs, optionally add "Opus (CAF)" since the encoder exists; users who specifically need MP3 can be told it isn't supported. Verdict: not feasible without third-party code.

### Gaps
- Not checked on macOS 15 (assumed identical: no MP3 encoder has ever shipped in AudioToolbox, but this is unverified here).
- Whether Ogg/Opus writing works with different afconvert options was not explored beyond one attempt.

## 5. RAR / 7z / ISO / XAR / CAB extraction (and 7z creation, encryption, multi-volume)

### Takeaway
The system `bsdtar 3.5.3 / libarchive 3.7.4` reads RAR4, RAR5 (single and concatenated multi-volume), 7z, ISO 9660, XAR and (per source code) CAB, and writes 7z, ISO 9660, XAR and ZIP (incl. AES-256 ZIP). It cannot decrypt 7z or RAR and cannot create encrypted 7z. Swivel's existing Extract already routes 7z/ISO/XAR/CAB to `tar`; `.rar` is missed because it has no declared UTType on this Mac. Two pitfalls: extracting only the first RAR volume silently produces a NUL-padded file with exit code 0, and bsdtar prompts for passphrases on encrypted ZIPs.

### Cited Findings
- Verified locally: `tar --version` → `bsdtar 3.5.3 - libarchive 3.7.4 zlib/1.2.12 liblzma/5.4.3 bz2lib/1.0.8` (no zstd/lz4 libraries); `tar --zstd`/`--lz4` fail with "Can't launch external program: zstd/lz4" — [SCRATCH arch1](file:///private/tmp/claude-501/-Users-wulizhi--codex--chatgpt-projects-g-p-6ad0c15852b08191845e68d96c6dcb35-sites-swivel/fcd810aa-62cd-4697-83ac-5092a29df1e3/scratchpad/feas/arch1/)
- The local `man tar` says it "can extract from tar, pax, cpio, zip, jar, ar, xar, rpm, 7-zip, and ISO 9660 cdrom images and can create tar, pax, cpio, ar, zip, 7-zip, and shar"; `--passphrase` says "zip is the only supported format that supports encryption"; `man 5 libarchive-formats` (dated 2016) still says RAR support is limited to RARv3 — [local man pages tar(1), libarchive-formats(5)](file:///usr/share/man/man1/tar.1)
- Verified locally with hand-crafted stored (uncompressed) archives: a RAR4 (v2.9 header) and a RAR5 archive were listed and extracted correctly ("hello rar 你好") — [SCRATCH mkrar.py](file:///private/tmp/claude-501/-Users-wulizhi--codex--chatgpt-projects-g-p-6ad0c15852b08191845e68d96c6dcb35-sites-swivel/fcd810aa-62cd-4697-83ac-5092a29df1e3/scratchpad/feas/scripts/mkrar.py)
- Verified locally: `tar --format 7zip --options 7zip:compression=lzma2` created a 7z that extracted byte-identical; `copy/deflate/bzip2/lzma1/ppmd` accepted, `zstd` → "Unknown compression name"; `tar --format 7zip --passphrase x` silently produced an unencrypted 7z readable without a password — [SCRATCH arch1](file:///private/tmp/claude-501/-Users-wulizhi--codex--chatgpt-projects-g-p-6ad0c15852b08191845e68d96c6dcb35-sites-swivel/fcd810aa-62cd-4697-83ac-5092a29df1e3/scratchpad/feas/arch1/)
- Verified locally: ISO 9660 images made by `tar --format iso9660` and by `hdiutil makehybrid -iso -joliet` were listed/extracted by `tar`; XAR archives from `/usr/bin/xar` and `tar --format xar` were readable; a UDZO `.dmg` gave "Unrecognized archive format" — [SCRATCH arch1](file:///private/tmp/claude-501/-Users-wulizhi--codex--chatgpt-projects-g-p-6ad0c15852b08191845e68d96c6dcb35-sites-swivel/fcd810aa-62cd-4697-83ac-5092a29df1e3/scratchpad/feas/arch1/)
- Verified locally (encrypted ZIP): AES-256 ZIP created with `tar --format zip --options zip:encryption=aes256 --passphrase` extracts with `tar --passphrase` but `ditto -x -k` fails "Unknown compression type" and `unzip -P` exits 81; a ZipCrypto archive from `zip -P` fails in `ditto` ("No password was provided…"), extracts with `tar --passphrase`, reports "Incorrect passphrase" for a wrong one, and without `--passphrase` bsdtar printed "Enter passphrase:" ~10,000 times (reading stdin) before "Too many incorrect passphrases" — [SCRATCH arch1](file:///private/tmp/claude-501/-Users-wulizhi--codex--chatgpt-projects-g-p-6ad0c15852b08191845e68d96c6dcb35-sites-swivel/fcd810aa-62cd-4697-83ac-5092a29df1e3/scratchpad/feas/arch1/)
- Verified locally (multi-volume): a hand-crafted two-volume stored RAR5 (`mv.part1.rar`, `mv.part2.rar`): `tar -xf mv.part1.rar` exited 0 and wrote a 1,120-byte file whose last 560 bytes were NUL (silent corruption); `cat mv.part1.rar mv.part2.rar | tar -xf -` produced the exact file; passing `-f` twice just uses the last file — [SCRATCH mkrar5mv.py](file:///private/tmp/claude-501/-Users-wulizhi--codex--chatgpt-projects-g-p-6ad0c15852b08191845e68d96c6dcb35-sites-swivel/fcd810aa-62cd-4697-83ac-5092a29df1e3/scratchpad/feas/scripts/mkrar5mv.py)
- libarchive v3.7.4 source: RAR5 "Encryption is not supported"; RAR (v4) "RAR encryption support unavailable."; 7-Zip "The %s is encrypted, but currently not supported" and "Crypto codec not supported yet"; 7-Zip "ZSTD codec is unsupported" when built without zstd; CAB "Multivolume cabinet file is unsupported" (CAB has LZX/MSZIP decoders); RAR4 reports "Parsing filters is unsupported." in one PPMd code path; RAR5 reader contains logic to continue into the next "partXXX.rar" volume — [libarchive rar5 reader @v3.7.4](https://github.com/libarchive/libarchive/blob/v3.7.4/libarchive/archive_read_support_format_rar5.c); [rar reader](https://github.com/libarchive/libarchive/blob/v3.7.4/libarchive/archive_read_support_format_rar.c); [7zip reader](https://github.com/libarchive/libarchive/blob/v3.7.4/libarchive/archive_read_support_format_7zip.c); [cab reader](https://github.com/libarchive/libarchive/blob/v3.7.4/libarchive/archive_read_support_format_cab.c)
- libarchive 3.7.7 changelog: "rar5: report encrypted entries (#2096)" (reporting, not decryption) — [IPFire libarchive 3.7.7 update](https://patchwork.ipfire.org/project/ipfire/patch/20241119211106.2194373-6-adolf.belka@ipfire.org)
- Upstream libarchive is at v3.8.10 (2026-10-14), so macOS 26.6's 3.7.4 lags upstream — [GitHub releases API: libarchive](https://github.com/libarchive/libarchive/releases)
- The SDK ships `libarchive.tbd` but no `archive.h`, so linking libarchive's C API directly would need hand-written declarations of a non-public interface — [MacOSX26.sdk/usr/lib](file:///Library/Developer/CommandLineTools/SDKs/MacOSX26.sdk/usr/lib/)
- Verified locally (UTType mapping used by `FileKind.of`): zip, 7z (`org.7-zip.7-zip-archive`), iso, xar, cab, dmg, tar, gz, tgz, bz2, xz, pkg, cpio, jar all conform to `public.archive`; `rar`, `lzh`, `zst` resolve to dynamic types that do not — [SCRATCH uti.swift](file:///private/tmp/claude-501/-Users-wulizhi--codex--chatgpt-projects-g-p-6ad0c15852b08191845e68d96c6dcb35-sites-swivel/fcd810aa-62cd-4697-83ac-5092a29df1e3/scratchpad/feas/scripts/uti.swift); `FileKind.of` — [Catalog.swift lines 47–59](file:///Users/wulizhi/.codex/.chatgpt-projects/g-p-6ad0c15852b08191845e68d96c6dcb35/sites/swivel/Sources/Swivel/Catalog.swift)
- `Shell.run` pipes stderr but only reads it in the termination handler and does not set stdin — [Catalog.swift `enum Shell`](file:///Users/wulizhi/.codex/.chatgpt-projects/g-p-6ad0c15852b08191845e68d96c6dcb35/sites/swivel/Sources/Swivel/Catalog.swift)

### Inferences
- 7z/ISO/XAR/CAB extraction: already reachable through the existing Extract item (they classify as `.archive` and go to `tar -xf`); needs an in-app test, not new code. Note `.dmg` (and `.pkg`) also classify as archives and would be offered Extract, which fails for DMG — a small existing rough edge (route DMG to `hdiutil attach -nobrowse -readonly` + copy, or hide Extract).
- RAR: add `"rar"` (and `.r00`/`.partN.rar` detection) to the archive extension list; S effort. Compressed RAR4/RAR5 decoding is documented/implemented in libarchive but was only verified here with stored data.
- Multi-volume RAR: detect the volume set (`name.part1.rar…`, or `.rar` + `.r00/.r01…`), require all parts present, and feed them concatenated through stdin (a `Pipe`/FileHandle writer); never extract from part 1 alone, since that can succeed silently with corrupt output. S–M.
- Encrypted ZIP: route encrypted `.zip` to `tar --passphrase <pw>` after asking in a secure text field; always pass `--passphrase` or close stdin, and drain stderr concurrently so a tool that floods stderr cannot fill the pipe and stall `Shell.run`. Passing a password on the command line exposes it to `ps`; acceptable for a local tool but worth noting. S–M.
- Encrypted 7z/RAR and multi-volume CAB: not feasible with system components (show a clear message). 7z creation: S (no encryption, no zstd).
- No TCC; no network. Min macOS 15 (same tar on 15 is likely but unverified).

### Gaps
- No real RAR (compressed, solid, RAR5 with BLAKE2, encrypted headers) or CAB sample was available; compressed RAR and CAB support rests on libarchive source/docs.
- The exact libarchive version shipped with macOS 15 was not checked.

## 6. PDF password protect / unlock / permissions

### Takeaway
Not present (Swivel currently refuses locked PDFs). PDFKit writes user/owner passwords and permission flags, and unlocks with `unlock(withPassword:)`. Removing a password needs copying pages into a new `PDFDocument` (or redrawing via Core Graphics), because writing the unlocked document keeps the encryption. Effort S.

### Cited Findings
- `PDFs.open` throws "\(name) is password protected" when `doc.isLocked` — [Documents.swift line 116](file:///Users/wulizhi/.codex/.chatgpt-projects/g-p-6ad0c15852b08191845e68d96c6dcb35/sites/swivel/Sources/Swivel/Documents.swift)
- PDFKit header: `PDFDocumentOwnerPasswordOption` and `PDFDocumentUserPasswordOption` (macOS 10.4; owner "Required for encryption"), `PDFDocumentAccessPermissionsOption` (macOS 12.0), `PDFDocumentBurnInAnnotationsOption` and `PDFDocumentSaveTextFromOCROption` (macOS 13.0); `PDFAccessPermissions` flags: low/high quality printing, document changes, assembly, content copying, accessibility, commenting, form-field entry — [MacOSX26.sdk PDFDocument.h](file:///Library/Developer/CommandLineTools/SDKs/MacOSX26.sdk/System/Library/Frameworks/PDFKit.framework/Headers/PDFDocument.h)
- Verified locally: writing with user "open123", owner "owner456" and permissions = low-quality print + accessibility produced a PDF with `isEncrypted`/`isLocked` true; wrong password → false; user password → `permissionsStatus` 1 (user), `allowsCopying` false, `allowsPrinting` true; owner password → status 2, copying allowed — [SCRATCH doc.swift](file:///private/tmp/claude-501/-Users-wulizhi--codex--chatgpt-projects-g-p-6ad0c15852b08191845e68d96c6dcb35-sites-swivel/fcd810aa-62cd-4697-83ac-5092a29df1e3/scratchpad/feas/scripts/doc.swift)
- Verified locally (removing a password): `write(to:)` or `write(to:withOptions:[:])` after an owner unlock still produced an encrypted, locked file; copying each `PDFPage` into a fresh `PDFDocument` produced an unencrypted PDF with text intact — and this also worked after unlocking with only the user password, i.e., PDFKit did not enforce the owner's restrictions; `CGPDFDocument.unlockWithPassword` + `drawPDFPage` redraw also produced an unencrypted PDF — [SCRATCH unlock.swift](file:///private/tmp/claude-501/-Users-wulizhi--codex--chatgpt-projects-g-p-6ad0c15852b08191845e68d96c6dcb35-sites-swivel/fcd810aa-62cd-4697-83ac-5092a29df1e3/scratchpad/feas/scripts/unlock.swift)

### Inferences
- "Protect": sheet with open password (optional), owner password (required by PDFKit for encryption), checkboxes for print/copy/edit; output "name protected.pdf". "Unlock": ask for the password; to remove restrictions, require the owner password (or `permissionsStatus == .owner`) rather than exploiting the page-copy path with only a user password — respecting the author's restrictions is the defensible product choice.
- Page copying drops document-level items (outline/bookmarks, document attributes) unless copied explicitly; set `documentAttributes` and rebuild the outline. Existing PDF tools can then accept unlocked PDFs instead of refusing them.
- Effort S. Min macOS 15 (permissions option 12+). No TCC/network. Verdict: feasible. Encryption algorithm/strength chosen by PDFKit was not inspected.

### Gaps
- The encryption algorithm (RC4 vs AES-128/256) PDFKit uses was not determined.
- Preservation of links, forms and outline through the page-copy path was not tested.

## 7. Searchable PDF (OCR text layer)

### Takeaway
Not present. There is a built-in one-line path: `PDFDocument.write(to:withOptions:[.saveTextFromOCROption: true])` (macOS 13+) OCRs image pages and saves invisible, selectable text — good for English, weak for CJK (spaces inserted between Chinese characters, Japanese kana lost). For CJK quality, a custom layer from Vision (best: macOS 26 `RecognizeDocumentsRequest`) drawn with invisible text mode worked and was searchable.

### Cited Findings
- PDFKit header comment for `PDFDocumentSaveTextFromOCROption` (macOS 13.0): "If enabled, OCR will be performed on all pages on which it has not been done already and saved as invisible, but selectable, text" — [MacOSX26.sdk PDFDocument.h](file:///Library/Developer/CommandLineTools/SDKs/MacOSX26.sdk/System/Library/Frameworks/PDFKit.framework/Headers/PDFDocument.h); availability macOS 13.0 — [Apple: saveTextFromOCROption](https://developer.apple.com/documentation/pdfkit/pdfdocumentwriteoption/savetextfromocroption)
- `RecognizeDocumentsRequest` (macOS 26.0) "scans a document and extracts different groups of text and barcodes", grouped by words, lines or paragraphs, with tables and lists — [Apple: RecognizeDocumentsRequest](https://developer.apple.com/documentation/vision/recognizedocumentsrequest)
- Verified locally on an image-only PDF (4 lines: English invoice, Chinese sentence, Japanese sentence, English report): built-in OCR write took 0.43 s; English text correct and `findString("revenue")` = 1; Chinese came out as "会 议 将 于 明 天 …" so `findString("会议")` = 0; Japanese came out "明 ⽇ の 午 前 九 時 半 会 議 始 ｡" (kana dropped, Kangxi radical "⽇") — [SCRATCH doc.swift](file:///private/tmp/claude-501/-Users-wulizhi--codex--chatgpt-projects-g-p-6ad0c15852b08191845e68d96c6dcb35-sites-swivel/fcd810aa-62cd-4697-83ac-5092a29df1e3/scratchpad/feas/scripts/doc.swift)
- Verified locally: custom layer (VNRecognizeTextRequest + `CGContext.setTextDrawingMode(.invisible)` + CTLine scaled to each box) made Chinese searchable (`findString("会议")` = 1) and English correct; the Japanese line lost kana because the request listed zh-Hans first; `RecognizeDocumentsRequest` on the same image returned 4 paragraphs with an exactly correct transcript of all four lines (incl. Japanese) in 0.28 s — [SCRATCH doc.swift](file:///private/tmp/claude-501/-Users-wulizhi--codex--chatgpt-projects-g-p-6ad0c15852b08191845e68d96c6dcb35-sites-swivel/fcd810aa-62cd-4697-83ac-5092a29df1e3/scratchpad/feas/scripts/doc.swift)
- Swivel's existing PDF → TXT/DOCX reads only embedded text (`selectionForEntireDocument`) and errors "No text found in this PDF" for scans — [Documents.swift lines 120–130](file:///Users/wulizhi/.codex/.chatgpt-projects/g-p-6ad0c15852b08191845e68d96c6dcb35/sites/swivel/Sources/Swivel/Documents.swift)

### Inferences
- Two tiers: (a) S effort — "Make Searchable" = built-in OCR option, fine for Latin-script scans on macOS 13+; (b) M effort — custom renderer: render each page (existing `PDFs.render` at 300 DPI), recognize (macOS 26 `RecognizeDocumentsRequest` lines/words; macOS 15 `VNRecognizeTextRequest` with automatic language detection and correct language ordering), and draw invisible text over the original page (draw the original `PDFPage` vector content rather than the rasterized image to avoid bloat). Use a CJK-capable font (e.g., PingFang via CTFont) for the invisible run so extraction maps glyphs correctly.
- Also add "OCR → TXT" for scanned PDFs (reuse Text Capture window). No TCC/network. Verdict: feasible.

### Gaps
- Not tested on multi-page or rotated/skewed real scans; performance per page at 300 DPI not measured.
- Preview.app's own behaviour was not examined.

## 8. Speech transcription of audio/video to TXT / SRT

### Takeaway
Not present. On macOS 26, `SpeechAnalyzer` + `SpeechTranscriber` transcribed a file on-device in 0.28 s with word-level time ranges, from an unbundled CLI without any Speech Recognition authorization request (status stayed "notDetermined"). English assets were already installed; Chinese and Japanese are supported but need a system asset download via `AssetInventory`. On macOS 15 only `SFSpeechRecognizer` exists: it requires Speech Recognition authorization (TCC + usage string) and on this Mac supports on-device recognition only for en-US, so macOS 15 support would be partial.

### Cited Findings
- SDK: `SpeechAnalyzer`, `SpeechTranscriber`, `DictationTranscriber`, `SpeechDetector`, `AssetInventory` are `@available(macOS 26.0…)` — [MacOSX26.sdk Speech.swiftinterface](file:///Library/Developer/CommandLineTools/SDKs/MacOSX26.sdk/System/Library/Frameworks/Speech.framework/Modules/Speech.swiftmodule/arm64e-apple-macos.swiftinterface)
- Apple's SpeechAnalyzer docs: steps are create modules, "Ensure the relevant assets are installed… See AssetInventory", supply audio (file helpers `analyzeSequence(from: AVAudioFile)`, `start(inputAudioFile:finishAfterFile:)`); the overview does not mention authorization — [Apple: SpeechAnalyzer](https://developer.apple.com/documentation/speech/speechanalyzer)
- WWDC25 session 277: "transcription is entirely on device but the models need to be fetched"; the model "is retained in system storage and does not increase the download or storage size of your application… It operates outside of your application's memory space"; apps can hold language support for a limited number of languages at a time; good for "long-form and distant audio"; results carry `audioTimeRange` when `attributeOptions: [.audioTimeRange]`; DictationTranscriber "supports the same languages… and devices as iOS 10's on-device SFSpeechRecognizer"; the session discusses only microphone permission, not speech authorization — [WWDC25: Bring advanced speech-to-text to your app with SpeechAnalyzer](https://developer.apple.com/videos/play/wwdc2025/277/)
- Verified locally (no downloads): `SpeechTranscriber.isAvailable` = true; supportedLocales (30) = de-AT de-CH de-DE en-AU en-CA en-GB en-IE en-IN en-NZ en-SG en-US en-ZA es-CL es-ES es-MX es-US fr-BE fr-CA fr-CH fr-FR it-CH it-IT ja-JP ko-KR pt-BR pt-PT yue-CN zh-CN zh-HK zh-TW; installedLocales = English variants only; DictationTranscriber supports 54 locales (installed: en-AU, en-US); `AssetInventory.maximumReservedLocales` = 5 — [SCRATCH speech.swift](file:///private/tmp/claude-501/-Users-wulizhi--codex--chatgpt-projects-g-p-6ad0c15852b08191845e68d96c6dcb35-sites-swivel/fcd810aa-62cd-4697-83ac-5092a29df1e3/scratchpad/feas/scripts/speech.swift)
- Verified locally: a `say`-generated English AIFF was transcribed by `SpeechTranscriber(locale: en-US, attributeOptions: [.audioTimeRange])` via `analyzeSequence(from:)` in 0.28 s: "Hello, this is a short test of on-device transcription. The meeting starts at 930 tomorrow morning." with word ranges like [0.00–0.54] "Hello,"; `SFSpeechRecognizer.authorizationStatus()` was 0 (notDetermined) before and after, the binary had no Info.plist and made no authorization request; `AssetInventory.status` reported "supported" (not "installed") for en-US/zh-CN/ja-JP even though en-US worked — [SCRATCH doc.swift (speech part)](file:///private/tmp/claude-501/-Users-wulizhi--codex--chatgpt-projects-g-p-6ad0c15852b08191845e68d96c6dcb35-sites-swivel/fcd810aa-62cd-4697-83ac-5092a29df1e3/scratchpad/feas/scripts/doc.swift)
- The CC0 CLI `yap` transcribes files with SpeechTranscriber (SRT/VTT/TXT/JSON output) on macOS 26; its source calls `AssetInventory.reserve(locale:)` and `assetInstallationRequest(...).downloadAndInstall()` but never requests speech authorization; its README lists Screen Recording only for system-audio capture and Microphone only for dictation — [finnvoor/yap](https://github.com/finnvoor/yap); [Transcribe.swift](https://github.com/finnvoor/yap/blob/main/Sources/yap/Transcribe.swift)
- Third-party guides still recommend `NSSpeechRecognitionUsageDescription` + authorization for SpeechAnalyzer (mostly for live mic capture; not Apple documentation) — [tessl.io speech-recognition skill](https://tessl.io/registry/dpearson2699/swift-ios-skills/3.9.0/files/skills/speech-recognition/SKILL.md)
- `SFSpeechRecognizer` (macOS 10.15+): step 1 is "Request authorization to use speech recognition"; "For some languages, the recognizer might require an Internet connection"; server limits per device/app per day; "Plan for a one-minute limit on audio duration" — [Apple: SFSpeechRecognizer](https://developer.apple.com/documentation/speech/sfspeechrecognizer); `requiresOnDeviceRecognition` "prevent[s]… sending audio over the network", "on-device requests won't be as accurate", honored only if `supportsOnDeviceRecognition` — [Apple: requiresOnDeviceRecognition](https://developer.apple.com/documentation/speech/sfspeechrecognitionrequest/requiresondevicerecognition)
- Verified locally: SFSpeechRecognizer lists 63 locales, but `supportsOnDeviceRecognition` is true only for en-US on this Mac; en-GB, zh-CN, zh-TW, zh-HK, yue-CN, ja-JP, ko-KR, fr-FR are false ("No Assistant asset for language …") — [SCRATCH speech.swift](file:///private/tmp/claude-501/-Users-wulizhi--codex--chatgpt-projects-g-p-6ad0c15852b08191845e68d96c6dcb35-sites-swivel/fcd810aa-62cd-4697-83ac-5092a29df1e3/scratchpad/feas/scripts/speech.swift)
- A third-party article reports SpeechAnalyzer runs with "no network call anywhere in the transcription path" and community tests put it ~2.2–3× faster than Whisper models — [addpipe blog](https://blog.addpipe.com/apple-speechanalyzer-api/)

### Inferences
- Recommended: macOS 26-only "Transcribe" for audio/video (video audio extracted with existing `AVAssetExportPresetAppleM4A` or read directly by `AVAudioFile`), locale picker defaulting to the system language, first-use download sheet driven by `AssetInventory.assetInstallationRequest` with progress (network once per language, stored by the system), output "name.txt" and "name.srt" (group word runs into ≤2-line cues by pauses/length using `audioTimeRange`). Effort M. No TCC observed for file transcription; keep `NSSpeechRecognitionUsageDescription` in Info.plist defensively in case a future OS starts enforcing it.
- macOS 15 fallback via SFSpeechRecognizer would add a Speech Recognition TCC prompt, Siri/dictation asset dependence, network for most languages, and duration limits for server recognition; on this Mac only en-US was on-device. Verdict: feasible on 26; partial (not recommended) on 15.
- Number formatting ("930" for "nine thirty") shows normalisation choices users may notice.

### Gaps
- Chinese/Japanese transcription quality was not tested (assets not installed; downloading was out of scope).
- Apple does not document explicitly whether SpeechTranscriber needs Speech Recognition authorization; my test shows it did not in this context (responsible process = the Claude app, CLI binary), but behaviour inside a bundled, hardened app was not tested.
- Hardware requirements for SpeechTranscriber ("certain hardware requirements" per WWDC) are not itemized; Intel support unknown.

## 9. Image text translation (OCR + Apple Translation)

### Takeaway
Not present. On macOS 26, `TranslationSession(installedSource:target:)` translates without any UI; locally it translated English and Japanese into Simplified Chinese offline using already-installed language packs. On macOS 15 a session is only obtainable through SwiftUI's `.translationTask`, which also handles the user-facing language download prompt. Combined with existing OCR this is M effort; repainting translated text into the image is a larger, optional step.

### Cited Findings
- `TranslationSession` is available from macOS 15.0; it can be obtained via SwiftUI `.translationTask(...)`; "for contexts where there's no UI, you can directly initialize the TranslationSession using `init(installedSource:target:)`… throws an error if the languages aren't already installed"; `prepareTranslation()` "Asks for permission to download translation languages"; "All translations using the TranslationSession class are processed on the user's device"; Apple may collect usage metrics excluding content — [Apple: TranslationSession](https://developer.apple.com/documentation/translation/translationsession)
- SDK: `init(installedSource:target:)`, `canRequestDownloads`, `cancel()` and `TranslationError.notInstalled` are macOS 26.0; `TranslationSession.Strategy` (`highFidelity`, `lowLatency`) and `translate(AttributedString)` are macOS 26.4 — [MacOSX26.sdk Translation.swiftinterface](file:///Library/Developer/CommandLineTools/SDKs/MacOSX26.sdk/System/Library/Frameworks/Translation.framework/Modules/Translation.swiftmodule/arm64e-apple-macos.swiftinterface)
- Verified locally: `LanguageAvailability` lists 38 languages (incl. en, ja, ko, zh, zh-HK, zh-TW); status en→zh-Hans, zh-Hans→en, ja→zh-Hans, ja→en, en→zh-Hant = installed; a CLI with no UI translated "The meeting starts at nine thirty tomorrow morning." → "会议将于明天早上九点半开始。" and "明日の午前九時半に会議が始まります。" → "明天的上午九点半开始会议。" — [SCRATCH doc.swift (translate part)](file:///private/tmp/claude-501/-Users-wulizhi--codex--chatgpt-projects-g-p-6ad0c15852b08191845e68d96c6dcb35-sites-swivel/fcd810aa-62cd-4697-83ac-5092a29df1e3/scratchpad/feas/scripts/doc.swift)
- Swivel already has OCR with editable text in the Text Capture window — [TextCapture.swift](file:///Users/wulizhi/.codex/.chatgpt-projects/g-p-6ad0c15852b08191845e68d96c6dcb35/sites/swivel/Sources/Swivel/TextCapture.swift)

### Inferences
- Simplest product: a "Translate" button/target-language popup in Text Capture: OCR lines → `translations(from:)` batch → show side-by-side and copy. On macOS 26 use the no-UI initializer; if `notInstalled`, host a tiny SwiftUI view (NSHostingView) with `.translationTask` / `prepareTranslation()` so the system download prompt appears. On macOS 15, the SwiftUI hosting path is mandatory (Swivel is AppKit but can host SwiftUI). Effort M.
- In-image overlay (replace text in place, like Live Text translate) requires drawing translated text into OCR boxes with background inpainting — L; not needed for v1.
- No TCC. Network only for language-pack downloads (system UI). Verdict: feasible on 26; partial on 15 (SwiftUI hosting + prompt).

### Gaps
- Behaviour with no network and missing packs was not tested (packs already installed here).
- Whether `Strategy.highFidelity` (26.4) depends on Apple Intelligence hardware/region was not determined.

## 10. Image upscaling without bundled models

### Takeaway
Interpolation-only scaling (Core Image Lanczos/bicubic, `CIEdgePreserveUpsampleFilter`, MetalFX spatial scaler) is available everywhere but adds no real detail. A genuine ML super-resolution exists on macOS 26: VideoToolbox `VTSuperResolutionScaler` with an image mode, but only 4×, input ≤1920 px per side, and an Apple model download is required (not present on this Mac). Verdict: partial.

### Cited Findings
- Verified locally: Core Image filters available for scaling: CIBicubicScaleTransform, CIEdgePreserveUpsampleFilter, CILanczosScaleTransform, CIMaximumScaleTransform, CISampleNearest; Lanczos 512→4000 px took 0.22 s — [SCRATCH images.swift](file:///private/tmp/claude-501/-Users-wulizhi--codex--chatgpt-projects-g-p-6ad0c15852b08191845e68d96c6dcb35-sites-swivel/fcd810aa-62cd-4697-83ac-5092a29df1e3/scratchpad/feas/scripts/images.swift)
- MetalFX `MTLFXSpatialScalerDescriptor` is macOS 13.0+ (Metal 4 variants macOS 26); verified `supportsDevice` = true on the M5 Pro GPU — [MacOSX26.sdk MTLFXSpatialScaler.h](file:///Library/Developer/CommandLineTools/SDKs/MacOSX26.sdk/System/Library/Frameworks/MetalFX.framework/Headers/MTLFXSpatialScaler.h); [SCRATCH misc.swift](file:///private/tmp/claude-501/-Users-wulizhi--codex--chatgpt-projects-g-p-6ad0c15852b08191845e68d96c6dcb35-sites-swivel/fcd810aa-62cd-4697-83ac-5092a29df1e3/scratchpad/feas/scripts/misc.swift)
- `VTSuperResolutionScalerConfiguration` (macOS 26.0) has input types Video and Image; image input max width 1920 and max height 1920 on macOS; "The super-resolution processor may require ML models which the framework needs to download… verify… `configurationModelStatus`… trigger model download using `downloadConfigurationModelWithCompletionHandler:`… drive download with user awareness"; `VTFrameProcessor` itself is macOS 15.4+ — [MacOSX26.sdk VTFrameProcessor_SuperResolutionScaler.h](file:///Library/Developer/CommandLineTools/SDKs/MacOSX26.sdk/System/Library/Frameworks/VideoToolbox.framework/Headers/VTFrameProcessor_SuperResolutionScaler.h)
- Verified locally: `isSupported` = true, `supportedScaleFactors` = [4]; image configs 1920×1920, 1920×1080, 512×512 at 4× → model status 0 (download required); 2000×1000 → nil ("Invalid input width"); low-latency SR: max 960×960, min 96×96, no scale factors for 1280×720 — [SCRATCH misc.swift](file:///private/tmp/claude-501/-Users-wulizhi--codex--chatgpt-projects-g-p-6ad0c15852b08191845e68d96c6dcb35-sites-swivel/fcd810aa-62cd-4697-83ac-5092a29df1e3/scratchpad/feas/scripts/misc.swift)
- WWDC25 session 300: the VT effects are "ML-based video processing algorithms optimized for Apple Silicon"; "Two ML models are available for super resolution, one for images and the other for videos"; demos show "more detailed and less blurry" output (no metrics) — [WWDC25: Enhance your app with machine-learning-based video effects](https://developer.apple.com/videos/play/wwdc2025/300/)

### Inferences
- Tier 1 (S, macOS 15): "Upscale 2×/4×" with Lanczos/edge-preserve — honest labelling ("smooth enlarge") since no detail is synthesized.
- Tier 2 (M, macOS 26 + Apple silicon): VT super-resolution 4× for inputs ≤1920×1920 (larger inputs would need tiling with overlap/blending, unverified for seams), CVPixelBuffer in/out, one-time system model download with consent UI. It is an Apple-provided model, so it does not violate the "no bundled third-party models" rule, but it needs network once.
- No TCC. Verdict: partially feasible (true SR only on macOS 26).

### Gaps
- Super-resolution output quality was not tested (model download required). Model size unknown.

## 11. ICO / ICNS / favicon export (and WebP write check)

### Takeaway
Not present. ImageIO writes multi-size ICO (and ICNS, but it dropped the 1024 and 64 px entries in my test); `iconutil` builds a complete ICNS from an `.iconset`. A favicon pack (PNG sizes + multi-size ICO) is S effort. ImageIO on macOS 26.6.2 cannot write WebP or JPEG-XL (read-only), so Swivel's WEBP conversion target never appears here.

### Cited Findings
- Verified locally: `CGImageDestinationCopyTypeIdentifiers()` (22) = com.adobe.pdf, com.adobe.photoshop-image, com.apple.atx, com.apple.icns, com.compuserve.gif, com.ilm.openexr-image, com.microsoft.bmp, com.microsoft.dds, com.microsoft.ico, com.truevision.tga-image, org.khronos.astc, org.khronos.ktx, org.khronos.ktx2, public.avif, public.heic, public.heics, public.jpeg, public.jpeg-2000, public.pbm, public.png, public.pvr, public.tiff; sources (62) include org.webmproject.webp, public.jpeg-xl and com.microsoft.cur (read-only) — [SCRATCH images.swift](file:///private/tmp/claude-501/-Users-wulizhi--codex--chatgpt-projects-g-p-6ad0c15852b08191845e68d96c6dcb35-sites-swivel/fcd810aa-62cd-4697-83ac-5092a29df1e3/scratchpad/feas/scripts/images.swift)
- Verified locally: ICO with 16/32/48/64/128/256 frames → 239,728 bytes, read back as 6 frames; ICNS via ImageIO from 16…1024 → only 5 frames (512, 256, 128, 32, 16); WebP `CGImageDestinationCreateWithURL` returned nil; AVIF write worked — [SCRATCH img](file:///private/tmp/claude-501/-Users-wulizhi--codex--chatgpt-projects-g-p-6ad0c15852b08191845e68d96c6dcb35-sites-swivel/fcd810aa-62cd-4697-83ac-5092a29df1e3/scratchpad/feas/img/)
- Verified locally: `sips` made a 10-image `.iconset` (16…512@2x) and `iconutil -c icns` produced a 2.3 MB ICNS whose largest representation is 1024 px; `iconutil -c iconset` round-tripped all 10; `sips --formats` lists icns/ico/avif/heic/heics as Writable and webp without "Writable" — [SCRATCH icon](file:///private/tmp/claude-501/-Users-wulizhi--codex--chatgpt-projects-g-p-6ad0c15852b08191845e68d96c6dcb35-sites-swivel/fcd810aa-62cd-4697-83ac-5092a29df1e3/scratchpad/feas/icon/)
- Swivel lists WEBP among output formats but removes any type ImageIO can't write — [Images.swift lines 14–28](file:///Users/wulizhi/.codex/.chatgpt-projects/g-p-6ad0c15852b08191845e68d96c6dcb35/sites/swivel/Sources/Swivel/Images.swift)

### Inferences
- "ICO" convert target: ImageIO multi-frame ICO (16, 32, 48, 256 by default); "ICNS": build an `.iconset` in a temp folder and run `/usr/bin/iconutil` (ImageIO's ICNS path loses sizes); "Favicon set": a folder is against the house rule, so output siblings (`name-16.png`, `name-32.png`, `name-180.png` (apple-touch), `name-192.png`, `name-512.png`, `name.ico`) or a ZIP. Square-crop/pad non-square sources first. Effort S. Min macOS 15. No TCC/network. Verdict: feasible.
- WebP output is not possible with ImageIO on this OS; a WebP encoder would have to be self-written (out of scope). Consider hiding the dead WEBP entry from Customize lists.

### Gaps
- WebP write support on macOS 15 not checked (expected the same).

## 12. Watermark (text/image) on images and PDFs; PDF page numbers; signature stamp

### Takeaway
Not present (Annotate draws arrows/boxes/text but no tiled watermark, and nothing for PDFs). All mechanisms are system-level: Core Graphics/AppKit text and image drawing for images; for PDFs either redraw pages into a new PDF context with overlays (text stays searchable) or add a `PDFAnnotation` stamp and burn it in. Effort M, mostly UI.

### Cited Findings
- Verified locally: redrawing each `PDFPage` into a `CGContext(url:mediaBox:)` and drawing "CONFIDENTIAL" plus "1 / N" page numbers kept the page text searchable (`findString("revenue")` = 1) and added the watermark text to the text layer — [SCRATCH doc.swift (part E)](file:///private/tmp/claude-501/-Users-wulizhi--codex--chatgpt-projects-g-p-6ad0c15852b08191845e68d96c6dcb35-sites-swivel/fcd810aa-62cd-4697-83ac-5092a29df1e3/scratchpad/feas/scripts/doc.swift)
- Verified locally: a `PDFAnnotation` subclass of type `.stamp` that draws an image in `draw(with:in:)`, added to a page and written with `.burnInAnnotationsOption: true` (macOS 13.0), saved successfully — [SCRATCH doc.swift (part F)](file:///private/tmp/claude-501/-Users-wulizhi--codex--chatgpt-projects-g-p-6ad0c15852b08191845e68d96c6dcb35-sites-swivel/fcd810aa-62cd-4697-83ac-5092a29df1e3/scratchpad/feas/scripts/doc.swift); option availability — [MacOSX26.sdk PDFDocument.h](file:///Library/Developer/CommandLineTools/SDKs/MacOSX26.sdk/System/Library/Frameworks/PDFKit.framework/Headers/PDFDocument.h)
- Swivel's AnnotateEditor already draws text with NSAttributedString onto images, and an editor base class with live preview exists — [AnnotateEditor.swift](file:///Users/wulizhi/.codex/.chatgpt-projects/g-p-6ad0c15852b08191845e68d96c6dcb35/sites/swivel/Sources/Swivel/AnnotateEditor.swift); [Editor.swift](file:///Users/wulizhi/.codex/.chatgpt-projects/g-p-6ad0c15852b08191845e68d96c6dcb35/sites/swivel/Sources/Swivel/Editor.swift)

### Inferences
- Image watermark: text or logo, position (9-grid or tiled diagonal), opacity, scale; batch over many files; reuse Editor/LiveRender. PDF watermark/page numbers: redraw approach is simple but drops links/annotations/forms (CG redraw draws page content only); the annotation-stamp approach keeps the document but needs `burnInAnnotationsOption` to flatten. Signature: user picks a transparent PNG (could be produced by §1 subject lift or a "white-to-transparent" threshold), stores it in Application Support (explicit save, like the QR library), places it with a drag on a page preview, then burn-in.
- Effort M (placement UI is the work). Min macOS 15 (burn-in 13+). No TCC/network. Verdict: feasible. A "signature" stamp is not a cryptographic digital signature; PDFKit offers no signing API, so label it "stamp signature image".

### Gaps
- Link/annotation preservation for the redraw path was not tested on a real PDF with links or forms.

## 13. Multiple images → one PDF; PDF → long image

### Takeaway
Images→PDF already exists (convert "PDF" and tools "Make PDF"), with each page sized to the image's point size and no A4/margin options. PDF→long image does not exist, but Swivel has both halves (300 DPI page rendering and a Collage "Column" layout), so it is an S addition.

### Cited Findings
- `Images.makePDF` inserts `PDFPage(image:)` per file and writes next to the first file; exposed as convert "PDF" and tools "Make PDF" — [Images.swift lines 54–65](file:///Users/wulizhi/.codex/.chatgpt-projects/g-p-6ad0c15852b08191845e68d96c6dcb35/sites/swivel/Sources/Swivel/Images.swift); [Catalog.swift lines 176–178, 211](file:///Users/wulizhi/.codex/.chatgpt-projects/g-p-6ad0c15852b08191845e68d96c6dcb35/sites/swivel/Sources/Swivel/Catalog.swift)
- `PDFs.pages` renders every page at 300 DPI to separate PNG/JPG files; `PDFs.render` is reusable — [Documents.swift lines 131–164](file:///Users/wulizhi/.codex/.chatgpt-projects/g-p-6ad0c15852b08191845e68d96c6dcb35/sites/swivel/Sources/Swivel/Documents.swift)
- Collage has a "Column" layout ("equal widths stacked top to bottom") and renders at width 4000 to `Collage.png` — [CollageEditor.swift lines 71–79, 158–159](file:///Users/wulizhi/.codex/.chatgpt-projects/g-p-6ad0c15852b08191845e68d96c6dcb35/sites/swivel/Sources/Swivel/CollageEditor.swift)

### Inferences
- PDF→long image: render pages at a fixed width (e.g., 1080–1600 px for phones), stack with optional gap, output one PNG/JPG; cap total height (very long documents can exceed ImageIO/JPEG limits of 65,535 px for JPEG) and split into "name 1.jpg, name 2.jpg" when needed. Effort S.
- Images→PDF improvements (A4/Letter fit, margins, orientation, JPEG re-compression for size) are S. No TCC/network. Verdict: feasible.

### Gaps
- Maximum pixel height accepted by each ImageIO encoder was not measured.

## 14. Keep-awake timer, color picker, file checksum, duplicate finder

### Takeaway
None exist. All four are small and permission-free: IOPM assertions (verified), `NSColorSampler` (macOS 10.15; third-party sources say it needs no Screen Recording permission because the system draws the sampler), CryptoKit SHA-256/MD5 (verified), and a size-then-hash duplicate scan of a dropped folder.

### Cited Findings
- Verified locally: `IOPMAssertionCreateWithName(kIOPMAssertionTypePreventUserIdleDisplaySleep, …)` returned success with no prompt and was released; `/usr/bin/caffeinate` also ships — [SCRATCH misc.swift](file:///private/tmp/claude-501/-Users-wulizhi--codex--chatgpt-projects-g-p-6ad0c15852b08191845e68d96c6dcb35-sites-swivel/fcd810aa-62cd-4697-83ac-5092a29df1e3/scratchpad/feas/scripts/misc.swift)
- Swivel only uses `ProcessInfo.beginActivity` during drag watching, not for user keep-awake — [DragWatcher.swift line 129](file:///Users/wulizhi/.codex/.chatgpt-projects/g-p-6ad0c15852b08191845e68d96c6dcb35/sites/swivel/Sources/Swivel/DragWatcher.swift)
- `NSColorSampler` (macOS 10.15+) "displays the system's color-sampling interface and returns the selected color to your app" — [Apple: NSColorSampler](https://developer.apple.com/documentation/appkit/nscolorsampler)
- Sources describing NSColorSampler as permission-free: Blender's PR calls it "permission free (no screen capture permission required)"; a react-x11 issue says the magnifier is drawn by the system out of process — [Blender PR #131593](https://projects.blender.org/blender/blender/pulls/131593); [react-x11 issue #517](https://github.com/sidorares/react-x11/issues/517) (via search summaries; Blender page returned 403 to direct fetch)
- Contradicting source: DSFColorPicker's README says "As of macOS 10.15, your app will require Screen Recording permissions to use the color sampler"; however a GitHub code search found no use of `NSColorSampler` in that repository (count 0), so the statement concerns its own screen-capture sampler — [dagronf/DSFColorPicker README](https://github.com/dagronf/DSFColorPicker)
- Verified locally: CryptoKit `SHA256.hash` and `Insecure.MD5.hash` of "hello" gave the standard digests (2cf24dba…9824 and 5d41402a…c592); `/usr/bin/shasum` and `/sbin/md5` also ship — [SCRATCH misc.swift](file:///private/tmp/claude-501/-Users-wulizhi--codex--chatgpt-projects-g-p-6ad0c15852b08191845e68d96c6dcb35-sites-swivel/fcd810aa-62cd-4697-83ac-5092a29df1e3/scratchpad/feas/scripts/misc.swift)

### Inferences
- Keep-awake: ◎-menu item with durations (30 min/1 h/2 h/until off), display vs system sleep assertion, auto-release on timer/quit; S. Color picker: Quick Actions/◎ item → `NSColorSampler().show` → copy HEX/RGB/HSL, recent colors; S; should be manually confirmed once that no Screen Recording prompt appears in the bundled app.
- Checksum: wheel action on any file → stream file in chunks (don't load whole file) → toast/window with SHA-256 (+MD5/SHA-1 for legacy), compare with clipboard; S.
- Duplicate finder on a dropped folder: enumerate, group by size, then hash (first 64 KB, then full) → review window; "Move to Trash" via `FileManager.trashItem` (never permanent delete) with undo; M. Folder access comes from the user's drag, as with existing tools.
- All min macOS ≤10.15 → fine on 15/26. No TCC/network expected. Verdict: feasible.

### Gaps
- NSColorSampler was not exercised interactively (it would show UI on the owner's screen); the no-permission claim relies on third-party sources and Apple's description of a system-provided UI.

## 15. Video: speed, rotate/flip, frame extraction, subtitles (burn-in / soft), screen-recording→GIF optimisation

### Takeaway
Only single-frame export and a basic GIF exist. Speed change (`scaleTimeRange` + `audioTimePitchAlgorithm`), flip/rotate (video composition transform), multi-frame extraction (`AVAssetImageGenerator.images(for:)`) and burned-in CJK subtitles (`AVVideoCompositionCoreAnimationTool` + `CATextLayer`) all worked locally. GIF optimisation can be done in Swift (frame dedupe, fps/size choice) but ImageIO gives no palette control.

### Cited Findings
- Existing: "Frame"/"PNG Frame" grabs one frame at 10 % of the duration; GIF uses 12 fps, max 540 px, first 30 s, loop forever, constant delay — [Media.swift lines 74–104](file:///Users/wulizhi/.codex/.chatgpt-projects/g-p-6ad0c15852b08191845e68d96c6dcb35/sites/swivel/Sources/Swivel/Media.swift)
- Verified locally: `AVMutableComposition.scaleTimeRange` to half duration + `audioTimePitchAlgorithm = .spectral` exported a 10 s clip as 5.04 s — [SCRATCH av.swift](file:///private/tmp/claude-501/-Users-wulizhi--codex--chatgpt-projects-g-p-6ad0c15852b08191845e68d96c6dcb35-sites-swivel/fcd810aa-62cd-4697-83ac-5092a29df1e3/scratchpad/feas/scripts/av.swift)
- Verified locally: an `AVMutableVideoComposition` with a horizontal-flip layer-instruction transform plus `AVVideoCompositionCoreAnimationTool` exported successfully; the first attempt (CATextLayer without explicit font/`displayIfNeeded`) rendered no text; with `NSFont`, `contentsScale = 2` and `displayIfNeeded()` the subtitle "Subtitle 字幕 テスト" rendered on a dark band — [SCRATCH av3.swift](file:///private/tmp/claude-501/-Users-wulizhi--codex--chatgpt-projects-g-p-6ad0c15852b08191845e68d96c6dcb35-sites-swivel/fcd810aa-62cd-4697-83ac-5092a29df1e3/scratchpad/feas/scripts/av3.swift); frame [burned2_frame.png](file:///private/tmp/claude-501/-Users-wulizhi--codex--chatgpt-projects-g-p-6ad0c15852b08191845e68d96c6dcb35-sites-swivel/fcd810aa-62cd-4697-83ac-5092a29df1e3/scratchpad/feas/av/burned2_frame.png)
- `AVVideoCompositionCoreAnimationTool` is available on macOS 10.7+ — [MacOSX26.sdk AVVideoComposition.h](file:///Library/Developer/CommandLineTools/SDKs/MacOSX26.sdk/System/Library/Frameworks/AVFoundation.framework/Headers/AVVideoComposition.h)
- Verified locally: `AVAssetImageGenerator.images(for:)` returned 10 frames for 10 requested times — [SCRATCH av.swift](file:///private/tmp/claude-501/-Users-wulizhi--codex--chatgpt-projects-g-p-6ad0c15852b08191845e68d96c6dcb35-sites-swivel/fcd810aa-62cd-4697-83ac-5092a29df1e3/scratchpad/feas/scripts/av.swift)
- ImageIO's writable list includes GIF and HEICS (animated HEIF), but a 2-frame HEICS with different frame sizes failed to finalize in my test — [SCRATCH images.swift](file:///private/tmp/claude-501/-Users-wulizhi--codex--chatgpt-projects-g-p-6ad0c15852b08191845e68d96c6dcb35-sites-swivel/fcd810aa-62cd-4697-83ac-5092a29df1e3/scratchpad/feas/scripts/images.swift)

### Inferences
- Speed: 0.5×/1.5×/2× items (S); keep pitch with `.spectral`/`.timeDomain`; mute option for >2×.
- Rotate 90°/flip: for rotation, a passthrough export with a changed `preferredTransform` is lossless and fast (as the existing Mute already copies the transform); flips need a re-encode through a video composition (S).
- Frames: "Extract frames" every N seconds or N frames → "name 1.png…" beside the original (S); scene-change detection would be extra.
- Burn subtitles: parse SRT/VTT (simple text format, own parser), one CATextLayer with keyframed opacity/string per cue; pairs naturally with §8 (transcribe → SRT → burn). M. Soft subtitles (tx3g text track via AVAssetWriter / `AVMediaType.text`) — M–L, not verified.
- GIF optimisation for screen recordings: drop duplicate consecutive frames and add their delay to the previous frame (screen recordings are often static), choose fps (8–15) and width (480–960), cap duration; no palette/dither control in ImageIO, so size gains come from dedupe/fps/size. S–M. No TCC (input is an existing recording; Swivel does not record the screen). Verdict: feasible.

### Gaps
- Soft subtitle muxing and GIF dedupe savings were not tested; no real screen recording was available.
