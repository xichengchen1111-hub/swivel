# Online single-purpose file tools: traffic and per-tool popularity as a usage proxy (as of 2026-10-21)

Method and labelling, applied throughout:
- **[3P-SW]** means a third-party estimate from Similarweb's public website page, collected 2026-10-21. Every page was labelled "September 2026 · All traffic". The figure sits under a chart headed "Total Visits Last 3 Months", but it is the **latest single month (Sep 2026)**: google.com shows 85.2B on the same page type ([Similarweb](https://www.similarweb.com/website/google.com/)), and ilovepdf.com shows 280.2M against Semrush's single-month September 325.13M ([Similarweb](https://www.similarweb.com/website/ilovepdf.com/), [Semrush](https://www.semrush.com/website/ilovepdf.com/overview/)). Free pages show only desktop keyword and channel data and the top 5 keywords, with no device split. The "MoM" (month-over-month) sign is taken from the page's own sentence ("increased/decreased by X% compared to last month").
- **[3P-SR]** means a third-party estimate from a Semrush public overview page, last updated 11–14 Oct 2026. **Caution:** for every site checked, Semrush's top country in September 2026 was Bangladesh at about 99% desktop (18–43% of all visits), and totals jumped 20–40% from August to September. This looks like a panel or bot artefact, so Similarweb is used as the primary figure and Semrush July/August values are the saner cross-check.
- **[1P]** means a first-party statement by the company itself.
- **[Inf]** means my own inference.
- A site's visits measure the **whole site**. They equal one feature's demand only when the site is single-purpose; each row says which.
- Do not add rows together to get a market size: sites overlap and users visit several of them.

## Q1. Estimated monthly visits of single-purpose tool sites, grouped by function

### Takeaway
PDF tools are in a class of their own. ilovepdf.com had about 280M visits in Sep 2026 (Similarweb), several times any other file tool. **Background removal is the largest function that Swivel lacks:** remove.bg alone had 54.2M visits, more than CloudConvert, Convertio and FreeConvert each. After those two, the strongest functions are generic conversion (whose most-searched pair is MP4→MP3), image compression and resizing (iLoveIMG 34.1M) and transcription (TurboScribe 27.1M, inflated by downloader SEO pages). Upscaling, HEIC, video compression, RAR, favicon, passport photos and watermarking are each in the 0.4–3M per site range. Image translation as a standalone site is negligible (about 28K).

### Cited Findings

**Background removal (Swivel lacks this; it has padding and shadow only)**

| Site (scope) | Visits Sep 2026 [3P-SW] | MoM | Global rank | Top keywords / notes | Source |
|---|---|---|---|---|---|
| remove.bg (single-purpose) | **54.2M** | −8.63% | #960 (from 902) | India 19.63%, US 9.28%, Indonesia 9.26%, Brazil 7.13%, Philippines 6.03% | [Similarweb](https://www.similarweb.com/website/remove.bg/) |
| remove.bg, Semrush cross-check | Sep 105.63M; Aug 80.36M; Jul 73.84M [3P-SR] | — | — | keyword "remove bg" 450,000 searches a month and 27.14% of the site's traffic; "background remover" 301,000 (Bangladesh database) | [Semrush](https://www.semrush.com/website/remove.bg/overview/) |
| photoroom.com (web only; the product is mostly a mobile app) | 18.7M | −6.97% | #3,348 | keywords: photoroom, remove background, remove bg, quitar fondo, background remover; organic search 42.79% | [Similarweb](https://www.similarweb.com/website/photoroom.com/) |
| picwish.com (background removal + enhance) | 2.8M | −8.11% | #22,053 | keywords: picwish, "melhorar qualidade de imagem", remove bg, "mejorar calidad de imagen", photo enhancer; Brazil 21.02% | [Similarweb](https://www.similarweb.com/website/picwish.com/) |
| erase.bg (single-purpose) | 866.1K | +5.99% | #62,040 | Indonesia 25.76% | [Similarweb](https://www.similarweb.com/website/erase.bg/) |
| koukoutu.com 抠抠图 (CN cutout) | 431.3K | −10.86% | #136,740 | keywords: 抠图, 在线抠图, 免费抠图软件 | [Similarweb](https://www.similarweb.com/website/koukoutu.com/) |
| picwish.cn 佐糖 (CN) | 218.6K | −25.67% | #219,194 | keywords: 佐糖, 抠图, 在线抠图, **证件照**, 无痕改字; China 89.09%; direct 69.5% | [Similarweb](https://www.similarweb.com/website/picwish.cn/) |

- [1P] remove.bg's enterprise page claims "150M+ images & videos processed every month" and 240+ enterprise customers. The page is undated — [remove.bg Enterprise](https://www.remove.bg/enterprise).
- [1P via press, Feb 2024] Photoroom said it was processing about 5 billion images a year when it raised $43M at a $500M valuation — [TechCrunch](https://techcrunch.com/2024/02/27/confirmed-photoroom-the-ai-image-editor-raised-43m-at-a-500m-valuation). A later figure of about 7B a year appears only in directory profiles and is unverified.

**Image compression, resizing and HEIC (Swivel has these, with quality-based compression; this is the baseline)**

| Site | Visits Sep 2026 [3P-SW] | MoM | Rank | Top keywords / notes | Source |
|---|---|---|---|---|---|
| iloveimg.com (multi-tool image suite) | **34.1M** | −4.79% | #1,318 | keywords: compress image, **heic to jpg**, webp to jpg, iloveimg, image compressor; organic 72.23%; 3.85 pages per visit, 3:26 per visit | [Similarweb](https://www.similarweb.com/website/iloveimg.com/) |
| iloveimg.com, Semrush | Sep 50.14M; Aug 43.78M; Jul 42.49M [3P-SR] | — | — | photo resizer 90,500 searches a month; image resizer 74,000; resize image 49,500; heic to jpg and photo compressor 6.88% traffic share each | [Semrush](https://www.semrush.com/website/iloveimg.com/overview/) |
| 11zon.com (India; PDF and image tools) | about 8M (read from the comparison chart) | −3.5% | #8,566 | India 74.93%; keywords: 11zon, compress pdf, jpg to pdf | [Similarweb](https://www.similarweb.com/website/11zon.com/) |
| imageresizer.com | about 6M (unlabelled widget value) | 7.93% | #7,443 | image resizer, resize image, compress image, crop image, photo resizer | [Similarweb](https://www.similarweb.com/website/imageresizer.com/) |
| tinypng.com (single-purpose compressor) | 2.3M | −1.39% | #33,813 | China 13.92%, US 13.89%; keywords: tinypng, image compressor, compress image, **compress webp**; direct 74.65% | [Similarweb](https://www.similarweb.com/website/tinypng.com/) |
| heictojpg.com (single-purpose) | 2.1M | −0.07% | #34,008 | **US 45.9%**, India 12.4%, UK 6.49%, Canada 5.29%, Australia 3.72%; organic 82.57% | [Similarweb](https://www.similarweb.com/website/heictojpg.com/) |
| compressjpeg.com | 1.6M | −4.2% | #42,374 | compress jpeg / jpg compress | [Similarweb](https://www.similarweb.com/website/compressjpeg.com/) |
| imagecompressor.com | 1.4M | −3.31% | #48,673 | India 34.59%, US 16.82% | [Similarweb](https://www.similarweb.com/website/imagecompressor.com/) |
| bulkresizephotos.com | 517K | −12.44% | #55,686 | bulk image resizer, **reduce image size**; direct 65.63% | [Similarweb](https://www.similarweb.com/website/bulkresizephotos.com/) |

- The target-file-size signal: Zamzar's fifth-ranked keyword is "**resize image to 20kb**" — [Similarweb zamzar.com](https://www.similarweb.com/website/zamzar.com/).
- TinyPNG's own homepage gives no usage counts. It offers a web tool, an API, a CDN and WordPress/Figma plugins, with no desktop app and no target-size mode — [tinypng.com](https://tinypng.com/).

**Upscaling and enhancement (Swivel lacks this)**

| Site | Visits Sep 2026 [3P-SW] | MoM | Rank | Notes | Source |
|---|---|---|---|---|---|
| upscale.media | 2.7M | −18.06% | #22,672 | Mexico is the top country; "mejorar calidad de imagen" | [Similarweb](https://www.similarweb.com/website/upscale.media/) |
| imgupscaler.com | 2.3M | −2.27% | #28,326 | image upscaler, upscale image, ai upscaler | [Similarweb](https://www.similarweb.com/website/imgupscaler.com/) |
| bigjpg.com | 1.5M | −3.75% | #46,251 | China 12.12%, Mexico 8.07%; direct 57.28% | [Similarweb](https://www.similarweb.com/website/bigjpg.com/) |

**General converters (whole-site figures; the top keyword shows which conversion pair dominates)**

| Site | Visits Sep 2026 [3P-SW] | MoM | Rank | Top 5 keywords | Source |
|---|---|---|---|---|---|
| freeconvert.com | **29.7M** | −5.6% | #1,586 | **mp4 to mp3, video compressor, video to mp3**, heic to jpg, mp3 converter | [Similarweb](https://www.similarweb.com/website/freeconvert.com/) |
| cloudconvert.com | 23.2M | −0.55% | rank not shown | **mp4 to mp3**, webp to png, png to webp, mp4 to gif, cloudconvert; US 23%, India 15.87%; organic 82.51% | [Similarweb](https://www.similarweb.com/website/cloudconvert.com/) |
| convertio.co | 19.8M | +1.8% | rank not shown | convertio, png to svg, epub to pdf, **mp4 to mp3, mp4 a mp3**; Russia 14.84%, Brazil 11.49%, France 6.36% | [Similarweb](https://www.similarweb.com/website/convertio.co/) |
| online-convert.com | 7.5M | −1.76% | #7,734 | online converter, mp4 a mp3, image convert to jpg, mp4 to mp3, convertidor mp3 | [Similarweb](https://www.similarweb.com/website/online-convert.com/) |
| zamzar.com | 2M | −3.75% | #30,310 | mp4 to mp3, zamzar, epub to pdf, mp4 to mp3 converter, resize image to 20kb | [Similarweb](https://www.similarweb.com/website/zamzar.com/) |
| xunjiepdf.com 迅捷 (CN) | 241.3K | −23.62% | #212,363 | 迅捷pdf, 在线格式转换器mp3免费, 免费在线格式转换, 大小写转换, azw3在线转化; China 88.58% | [Similarweb](https://www.similarweb.com/website/xunjiepdf.com/) |

- Semrush cross-checks [3P-SR]:
  - cloudconvert.com: Sep 52.78M, Aug 40.33M, Jul 37.32M — [Semrush](https://www.semrush.com/website/cloudconvert.com/overview/).
  - freeconvert.com: Sep 64.33M, Aug 52.62M, Jul 48.96M. Its top keywords (Bangladesh database) are video to audio (8.89%), video to audio converter (7.27%), image converter, mp3 converter and video compressor — [Semrush](https://www.semrush.com/website/freeconvert.com/overview/).

**GIF**
- ezgif.com: 6.6M visits (−6.76%), rank #6,805, US 27.24%. Top keywords: ezgif, gif maker, video to gif, mp4 to gif, gif to mp4 — [Similarweb](https://www.similarweb.com/website/ezgif.com/). Swivel already covers this (baseline).

**PDF suites (whole-site figures)**

| Site | Visits Sep 2026 [3P-SW] | MoM | Rank | Top keywords / notes | Source |
|---|---|---|---|---|---|
| ilovepdf.com | **280.2M** | +7.57% | **#113** (from 127) | ilovepdf, i love pdf, **jpg to pdf, pdf to jpg, pdf to word**; India 17.62%, Brazil 10.59%, Indonesia 6.03%, Colombia 4.91%, Mexico 4.55%; organic 67.41%; 106.4K ranking keywords | [Similarweb](https://www.similarweb.com/website/ilovepdf.com/) |
| ilovepdf.com, Semrush | Sep 325.13M; Aug 255.46M; Jul 250.21M; average session 07:08 [3P-SR] | — | — | Dec 2025: 216.83M (Semrush snapshot of Jan 13, 2026) | [Semrush](https://www.semrush.com/website/ilovepdf.com/overview/) |
| smallpdf.com | 44.8M | +3.57% | #916 | **pdf to word**, smallpdf, jpg to pdf, **compress pdf, pdf compressor**; paid is 9.44% of its search traffic | [Similarweb](https://www.similarweb.com/website/smallpdf.com/) |
| smallpdf.com, Semrush | Sep 56.71M; Aug 42.78M; Jul 38.45M [3P-SR] | — | — | "pdf to word" accounts for 28.28% of keyword traffic (246,000 searches a month) | [Semrush](https://www.semrush.com/website/smallpdf.com/overview/) |
| pdf24.org | 19.2M | 6.71% | #2,446 | pdf24, **compress pdf, pdf zusammenfügen (merge)**, pdf24 tools; Germany 13.86%; 4.18 pages per visit, 4:35 per visit | [Similarweb](https://www.similarweb.com/website/pdf24.org/) |
| sejda.com | 16.4M | −1.47% | #3,165 | **pdf editor**, sejda, sejda pdf editor, pdf editor online; India 37.54% | [Similarweb](https://www.similarweb.com/website/sejda.com/) |
| pdf2go.com | 4.2M | −0.31% | #13,348 | epub to pdf, pdf2go, compress pdf, word to pdf, pdf to text | [Similarweb](https://www.similarweb.com/website/pdf2go.com/) |

- Adobe reports no web-only Acrobat figure.
  - [1P] Acrobat monthly active users (MAU) rose 23% year over year in Q1 FY2025 — [MarketBeat transcript](https://www.marketbeat.com/earnings/reports/2025-3-12-adobe-inc-stock).
  - [1P] Combined Acrobat + Express MAU passed 750M in Q4 FY2025, up 20% year over year — [Motley Fool Q4 FY2025 transcript](https://www.fool.com/earnings/call-transcripts/2025/12/10/adobe-adbe-q4-2025-earnings-call-transcript/).

**OCR, transcription and subtitles**

| Site | Visits Sep 2026 [3P-SW] | MoM | Rank | Notes | Source |
|---|---|---|---|---|---|
| turboscribe.ai | **27.1M** | +0.86% | #1,927 (from 2,095) | Top keywords are **youtube to mp4** / baixar video youtube / descargar videos de youtube, plus turboscribe, so much of the traffic is downloader SEO and not transcription. Paid is 12.84% of search; Brazil and US lead | [Similarweb](https://www.similarweb.com/website/turboscribe.ai/) |
| veed.io (video editor; subtitles) | 7.9M | −7.09% | #4,574 | veed, video downloader, ai video generator, **video compressor** | [Similarweb](https://www.similarweb.com/website/veed.io/) |
| otter.ai (meeting notes) | 6.7M | +6.07% | #7,352 | brand and login keywords; direct 65.93%; US, UK, Canada | [Similarweb](https://www.similarweb.com/website/otter.ai/) |
| kapwing.com (video editor) | 4.4M | −4.99% | #10,166 | brand and novelty keywords | [Similarweb](https://www.similarweb.com/website/kapwing.com/) |
| notta.ai | 2.5M | −1.07% | #19,833 | Japan 69.09%; direct 55.58% | [Similarweb](https://www.similarweb.com/website/notta.ai/) |
| onlineocr.net (OCR, including PDF) | 851.8K | −6.15% | #78,178 | **"переводчик по фото" (photo translator)**, ocr, ocr online, online ocr, "перевод по фото" | [Similarweb](https://www.similarweb.com/website/onlineocr.net/) |

- TurboScribe's homepage returned HTTP 403, so its first-party user claims could not be read.

**Video and audio tools (Swivel has conversion, trim and audio extraction to M4A/WAV, but no MP3 output and no target-size compression)**

| Site | Visits Sep 2026 [3P-SW] | MoM | Rank | Top keywords | Source |
|---|---|---|---|---|---|
| clideo.com | 11.1M | −3.81% | #3,856 | **audio cutter, mp3 cutter**, clideo, **video compressor, compress video** | [Similarweb](https://www.similarweb.com/website/clideo.com/) |
| online-video-cutter.com (123apps) | 6.4M | −4.23% | #9,281 | video cutter, video trimmer, обрезать видео онлайн, crop video, video cropper | [Similarweb](https://www.similarweb.com/website/online-video-cutter.com/) |
| online-audio-converter.com (123apps) | 3.4M | −4.45% | #22,248 | **mp3 converter**, audio converter, convertir a mp3, convertidor mp3, converter para mp3 | [Similarweb](https://www.similarweb.com/website/online-audio-converter.com/) |
| 123apps.com (hub domain only) | 1.7M | −8.13% | #43,010 | brand keywords | [Similarweb](https://www.similarweb.com/website/123apps.com/) |
| 8mb.video (single-purpose target-size compressor) | 396.9K | −10.86% | #137,087 | 8mb video, **discord video compressor**, 8mb compressor; direct 53.27% | [Similarweb](https://www.similarweb.com/website/8mb.video/) |
| y2mate.nu (YouTube→MP3 ripper; a demand signal only, NOT a recommendation) | 1.4M | −52.05% | #56,095 | yt to mp3, youtube to mp3 | [Similarweb](https://www.similarweb.com/website/y2mate.nu/) |

**Archives, favicon, passport photos, watermarking, image translation**

| Site | Visits Sep 2026 [3P-SW] | MoM | Rank | Notes | Source |
|---|---|---|---|---|---|
| ezyzip.com (zip/unzip) | 936.4K | 5.02% | #47,074 | ezyzip, **folder to zip**, descomprimir zip | [Similarweb](https://www.similarweb.com/website/ezyzip.com/) |
| extract.me (RAR/7z extraction) | 416.5K | −6.67% | #128,107 | **rar extractor**, descomprimir rar, rar extractor online, разархивировать онлайн | [Similarweb](https://www.similarweb.com/website/extract.me/) |
| unzip-online.com | 64.5K | 0.7% | #482,041 | unzip online, decompress file | [Similarweb](https://www.similarweb.com/website/unzip-online.com/) |
| watermarkly.com | 930.6K | −8.55% | #67,888 | Russia 50.08%; keywords include crop photo, webp to jpg, add text to photo, so it is not purely watermarking | [Similarweb](https://www.similarweb.com/website/watermarkly.com/) |
| passport-photo.online | 677.9K | −5.89% | #81,513 | passport size photo maker, passport photo online; US 27.27% | [Similarweb](https://www.similarweb.com/website/passport-photo.online/) |
| favicon.io | 551.5K | −7.41% | #104,087 | favicon generator, **png to favicon**, favicon.ico | [Similarweb](https://www.similarweb.com/website/favicon.io/) |
| gaitubao.com 改图宝 (CN) | 141.3K | −12.59% | #297,445 | 改图宝, 印章产生器, **照片尺寸修改, 在线改证件照**, 条码产生器 | [Similarweb](https://www.similarweb.com/website/gaitubao.com/) |
| imagetranslate.com (image translation) | 27.6K | −27.07% | #1,009,457 | its top keyword is "google translate image" | [Similarweb](https://www.similarweb.com/website/imagetranslate.com/) |

**Open-source demand signals (GitHub API, fetched 2026-10-21)**
- [upscayl](https://github.com/upscayl/upscayl) (upscaler): 50,829 stars, AGPL-3.0.
- [whisper.cpp](https://github.com/ggml-org/whisper.cpp) (speech-to-text): 54,530 stars, MIT.
- [rembg](https://github.com/danielgatis/rembg) (background removal): 25,142 stars, MIT.
- [HandBrake](https://github.com/HandBrake/HandBrake) (video transcoding and compression): 24,734 stars.
- [ImageOptim](https://github.com/ImageOptim/ImageOptim) (Mac image compressor): 10,007 stars, GPL-2.0.
- [Keka](https://github.com/aonez/Keka) (Mac archiver for 7z/RAR): 7,533 stars.
- [Clop](https://github.com/FuzzyIdeas/Clop) (Mac clipboard image/video optimiser): 1,852 stars, GPL-3.0.

### Inferences
- [Inf] Ranking of functions Swivel lacks, by the largest single-purpose site (Sep 2026, Similarweb):
  1. Background removal: remove.bg 54.2M, Photoroom web 18.7M, PicWish 2.8M, erase.bg 0.87M.
  2. Transcription: TurboScribe 27.1M, discounted for downloader SEO; Otter 6.7M; Notta 2.5M.
  3. Upscaling: about 1.5–2.7M per site.
  4. HEIC→JPG: 2.1M. Swivel already has this.
  5. Archive extraction: 0.4–0.9M.
  6. Watermark, passport photo and favicon: 0.55–0.93M each.
  7. Target-size video compression (8mb.video): 0.4M as a standalone site. Its demand mostly lives inside general converters (see Q2).
  8. Image translation: about 0.03M standalone.
- [Inf] remove.bg's 54.2M is larger than CloudConvert, Convertio and FreeConvert combined per site. Background removal is therefore the clearest high-traffic single function Swivel does not offer. Whether macOS's own on-device subject lifting would make a Swivel version easy (or redundant) needs a separate feasibility check; I did not verify that here.
- [Inf] Similarweb's panel is generally thin for mainland China. The figures for 改图宝, 迅捷, 佐糖.cn and 抠抠图 (0.14–0.43M) probably understate domestic use, and Baidu-search demand is not captured.
- [Inf] Almost all sites declined month over month in Sep 2026 (typically −2% to −10%). The exceptions are PDF suites (ilovepdf +7.57%, smallpdf +3.57%) and otter.ai. A single month is noisy, so treat these as levels, not trends.

### Gaps
- I did not get Similarweb figures for removal.ai, pixelcut, clipdrop, idphoto4you, persofoto, submagic or the Adobe Express free tools. Express sits on adobe.com subpaths, and Similarweb's public pages do not separate subpaths.
- 稿定抠图 cannot be separated from gaoding.com. "一键抠图" is a generic phrase, not one dominant domain; koukoutu.com and picwish.cn were used instead.
- I found no single-purpose PDF protect/unlock, PDF-OCR (other than onlineocr.net) or page-number site big enough to measure.
- Similarweb's public pages expose only the latest month and the top 5 keywords. Multi-month history and device split need a paid plan.
- TurboScribe (403) and Smallpdf's about and statistics pages (JavaScript-rendered or truncated) could not be read directly.

## Q2. Which tools inside multi-tool sites are used most (first-party or published rankings)

### Takeaway
None of the big multi-tool sites (iLovePDF, Smallpdf, PDF24, CloudConvert, FreeConvert, Convertio, Adobe) publishes a per-tool usage ranking I could find.

The best proxies are:
- Smallpdf's homepage block headed "Most Popular PDF Tools". It may be curated rather than measured.
- Each site's top organic keywords, which measure search demand, not usage.

Both proxies point the same way:
- PDF: PDF↔Word, JPG↔PDF, compress, merge, then edit/sign.
- Conversion: **MP4→MP3** is the #1 non-brand keyword at FreeConvert, CloudConvert, Zamzar and online-convert, and #3–4 at Convertio. Video compression, WebP↔PNG/JPG and HEIC→JPG follow.

### Cited Findings
- [1P, display] Smallpdf's homepage section "**Most Popular PDF Tools**" lists, in order: PDF to Word, Merge PDF, JPG to PDF, Sign PDF, Edit PDF, Compress PDF. The page does not say whether the order is measured. It also says "Over a billion users have used our service" — [smallpdf.com](https://smallpdf.com/).
- [1P] Smallpdf's about page (seen only through a search snippet; the page body could not be retrieved) claims 40M+ monthly users and 20+ tools. It says Compress PDF was the original tool, launched in 2013 — [Smallpdf About](https://smallpdf.com/de/about).
- [1P, undated and probably stale] iLovePDF says it "has processed more than 500 million files" and has "millions of users". Its mobile app launched in 2017 and its desktop app in 2018. There is no per-tool data — [iLovePDF Press](https://www.ilovepdf.com/press).
- [1P] PDF24's tool grid is not labelled as popularity-ranked, and the page gives no usage numbers — [PDF24 Tools](https://tools.pdf24.org/en/).
- [1P] Convertio's homepage counter, read 2026-10-21: "We've converted 3,191,036,609 files with a total size of 58,299 TB". Its format categories are not ranked by usage — [convertio.co](https://convertio.co/).
- [1P] CloudConvert's homepage rendered empty through the fetcher, so no counter or popular list could be read — [cloudconvert.com](https://cloudconvert.com/).
- [1P, display] FreeConvert's Google Workspace add-on lists "popular conversions" for audio as M4A to MP3, WAV to MP3, M4A to WAV, MP3 to OGG and AAC to MP3. The add-on has 50K+ installs and is not usage-labelled — [Google Workspace Marketplace](https://workspace.google.com/marketplace/app/freeconvert_file_converter/869351005185).
- [1P] Adobe gives only combined Acrobat and Express MAU (750M+ in Q4 FY2025, +20% year over year) and no tool-level data — [Motley Fool transcript](https://www.fool.com/earnings/call-transcripts/2025/12/10/adobe-adbe-q4-2025-earnings-call-transcript/).
- [3P] Torii's Shadow-IT Index for October 2025 ranked iLovePDF #4 among apps used without IT approval, with no per-tool split — [Torii](https://www.toriihq.com/research/apps/ilovepdf).
- [3P-SW] Top non-brand organic keywords (desktop, worldwide, Sep 2026), each one a search-demand signal for a single tool:
  - ilovepdf: jpg to pdf, pdf to jpg, pdf to word — [Similarweb](https://www.similarweb.com/website/ilovepdf.com/).
  - smallpdf: pdf to word, jpg to pdf, compress pdf, pdf compressor — [Similarweb](https://www.similarweb.com/website/smallpdf.com/).
  - pdf24: compress pdf, pdf zusammenfügen — [Similarweb](https://www.similarweb.com/website/pdf24.org/).
  - sejda: pdf editor — [Similarweb](https://www.similarweb.com/website/sejda.com/).
  - pdf2go: epub to pdf, compress pdf, word to pdf, pdf to text — [Similarweb](https://www.similarweb.com/website/pdf2go.com/).
  - iloveimg: compress image, heic to jpg, webp to jpg, image compressor — [Similarweb](https://www.similarweb.com/website/iloveimg.com/).
  - freeconvert: mp4 to mp3, video compressor, video to mp3, heic to jpg, mp3 converter — [Similarweb](https://www.similarweb.com/website/freeconvert.com/).
  - cloudconvert: mp4 to mp3, webp to png, png to webp, mp4 to gif — [Similarweb](https://www.similarweb.com/website/cloudconvert.com/).
  - convertio: png to svg, epub to pdf, mp4 to mp3 — [Similarweb](https://www.similarweb.com/website/convertio.co/).
  - zamzar: mp4 to mp3, epub to pdf, resize image to 20kb — [Similarweb](https://www.similarweb.com/website/zamzar.com/).
  - clideo: audio cutter, mp3 cutter, video compressor, compress video — [Similarweb](https://www.similarweb.com/website/clideo.com/).
- [3P-SR] Keyword traffic shares, Bangladesh database, Sep 2026:
  - smallpdf: pdf to word 28.28%, jpg to pdf 7.82%, pdf to jpg 7.82%, word to pdf 4.29% — [Semrush](https://www.semrush.com/website/smallpdf.com/overview/).
  - ilovepdf: jpg to pdf 9.23%, pdf to jpg 9.23%, pdf to word converter 5.06%, word to pdf 5.06% — [Semrush](https://www.semrush.com/website/ilovepdf.com/overview/).
  - cloudconvert: jpg to png 7.63%, webp to jpg 7.63%, pdf to jpg 6.62% — [Semrush](https://www.semrush.com/website/cloudconvert.com/overview/).

### Inferences
- [Inf] Usage order implied for PDF suites: PDF↔Word and JPG↔PDF, then compress, then merge, then edit/sign. Swivel has all of these except edit/sign. Protect/unlock, OCR, watermarks and page numbers appear in none of the visible top keywords for any PDF site, so their search demand is likely an order of magnitude below the leaders. This is consistent with them being "long tail" tools.
- [Inf] MP4→MP3 or video→MP3 is the single most recurrent non-brand conversion keyword across general converters (#1 at four of five). It is also the core of online-audio-converter.com (3.4M) and of downloader sites. It is the clearest evidence for MP3 output, which Swivel lacks.
- [Inf] "Video compressor" appears in the top 5 at FreeConvert, Clideo and VEED. Together with 8mb.video's "discord video compressor", this indicates target-size video compression is a top-tier task. It lives inside multi-tool sites more than as a standalone site.
- [Inf] Swivel has WebP read-only, but "webp to jpg/png" is a top keyword at iLoveIMG, CloudConvert and even Watermarkly, and "png to webp" and "compress webp" also rank. WebP output (writing) is a cheap, high-signal gap to check.
- [Inf] "png to svg" (Convertio's #2) and "epub to pdf" (Convertio, Zamzar, pdf2go) are recurring pairs Swivel does not list. Vectorisation is hard; EPUB→PDF may be feasible.

### Gaps
- No first-party per-tool usage counts were found for iLovePDF, iLoveIMG, Smallpdf, PDF24, CloudConvert, FreeConvert, Convertio, TinyPNG or Adobe Acrobat online.
- Smallpdf's "PDF Statistics" page (smallpdf.com/pdf-statistics) rendered only its title, "PDF Statistics & Usage in 2025", through the fetcher. Its content, including whether it contains Smallpdf-internal task data, is unverified.
- Smallpdf's "1 billion users" blog post returns HTTP 410.
- Per-page traffic for each tool URL (an Ahrefs/Semrush "Top pages" report) would rank tools directly, but it is paywalled or behind a CAPTCHA and was not attempted.

## Q3. What this implies for demand for features Swivel lacks

### Takeaway
By traffic signal, the missing features rank as follows:
1. Background removal, by far the strongest.
2. MP3 output.
3. Target-size video compression.
4. Target-size image compression, which is strongest in India, where uploads must fit forms like "20kb".
5. Transcription and subtitles.
6. Upscaling.
7. A long tail of mid-to-low single-site demand: ID photos, watermarking, RAR/7z, favicon/ICO, PDF protect/unlock, PDF OCR, sign and page numbers. Image translation as a standalone online tool is negligible.

### Cited Findings
- Background removal:
  - remove.bg had 54.2M visits in Sep 2026, rank #960 — [Similarweb](https://www.similarweb.com/website/remove.bg/).
  - Photoroom web had 18.7M — [Similarweb](https://www.similarweb.com/website/photoroom.com/).
  - remove.bg claims 150M+ images and videos processed each month — [remove.bg](https://www.remove.bg/enterprise).
  - "remove bg" has 450,000 monthly searches (Semrush, Bangladesh database) — [Semrush](https://www.semrush.com/website/remove.bg/overview/).
  - China: 抠图 and 在线抠图 are the top keywords for koukoutu.com (431.3K) and picwish.cn — [Similarweb koukoutu](https://www.similarweb.com/website/koukoutu.com/), [Similarweb picwish.cn](https://www.similarweb.com/website/picwish.cn/).
- MP3 output:
  - mp4 to mp3 is the #1 non-brand keyword at FreeConvert (29.7M), CloudConvert (23.2M), Zamzar and online-convert — [Similarweb freeconvert](https://www.similarweb.com/website/freeconvert.com/), [cloudconvert](https://www.similarweb.com/website/cloudconvert.com/), [zamzar](https://www.similarweb.com/website/zamzar.com/), [online-convert](https://www.similarweb.com/website/online-convert.com/).
  - online-audio-converter.com had 3.4M visits, with "mp3 converter" as its top keyword — [Similarweb](https://www.similarweb.com/website/online-audio-converter.com/).
  - Clideo's top keywords are "audio cutter" and "mp3 cutter" — [Similarweb](https://www.similarweb.com/website/clideo.com/).
- Target-size video compression:
  - "video compressor" is FreeConvert's #2 keyword — [Similarweb](https://www.similarweb.com/website/freeconvert.com/).
  - Clideo ranks "video compressor" and "compress video" — [Similarweb](https://www.similarweb.com/website/clideo.com/).
  - VEED ranks "video compressor" — [Similarweb](https://www.similarweb.com/website/veed.io/).
  - 8mb.video had 396.9K visits, with "discord video compressor" as a keyword — [Similarweb](https://www.similarweb.com/website/8mb.video/).
- Target-size image compression:
  - Zamzar ranks "resize image to 20kb" — [Similarweb](https://www.similarweb.com/website/zamzar.com/).
  - 11zon.com had about 8M visits, 74.93% from India, with "compress pdf" and "jpg to pdf" as keywords — [Similarweb](https://www.similarweb.com/website/11zon.com/).
  - "compress image" is iLoveIMG's top keyword (34.1M site) — [Similarweb](https://www.similarweb.com/website/iloveimg.com/).
  - bulkresizephotos ranks "reduce image size" — [Similarweb](https://www.similarweb.com/website/bulkresizephotos.com/).
- Transcription:
  - TurboScribe had 27.1M visits, but its top keywords are YouTube-download terms — [Similarweb](https://www.similarweb.com/website/turboscribe.ai/).
  - otter.ai had 6.7M, mostly direct and login traffic — [Similarweb](https://www.similarweb.com/website/otter.ai/).
  - notta.ai had 2.5M, 69% from Japan — [Similarweb](https://www.similarweb.com/website/notta.ai/).
  - whisper.cpp has 54,530 GitHub stars — [GitHub](https://github.com/ggml-org/whisper.cpp).
- Upscaling:
  - upscale.media had 2.7M, imgupscaler 2.3M and bigjpg 1.5M — [Similarweb](https://www.similarweb.com/website/upscale.media/), [imgupscaler](https://www.similarweb.com/website/imgupscaler.com/), [bigjpg](https://www.similarweb.com/website/bigjpg.com/).
  - upscayl has 50,829 GitHub stars — [GitHub](https://github.com/upscayl/upscayl).
- RAR/7z:
  - extract.me had 416.5K visits ("rar extractor"); ezyzip 936.4K; unzip-online 64.5K — [Similarweb extract.me](https://www.similarweb.com/website/extract.me/), [ezyzip](https://www.similarweb.com/website/ezyzip.com/), [unzip-online](https://www.similarweb.com/website/unzip-online.com/).
  - Keka has 7,533 GitHub stars — [GitHub](https://github.com/aonez/Keka).
- ID and passport photos:
  - passport-photo.online had 677.9K visits — [Similarweb](https://www.similarweb.com/website/passport-photo.online/).
  - 改图宝's keywords include 照片尺寸修改 and 在线改证件照 — [Similarweb](https://www.similarweb.com/website/gaitubao.com/).
  - 佐糖.cn ranks 证件照 among its top 5 — [Similarweb](https://www.similarweb.com/website/picwish.cn/).
- Watermarking: watermarkly had 930.6K visits, but its keyword mix is cropping and conversion — [Similarweb](https://www.similarweb.com/website/watermarkly.com/).
- Favicon/ICO: favicon.io had 551.5K visits, with "png to favicon" as a keyword — [Similarweb](https://www.similarweb.com/website/favicon.io/).
- Image translation:
  - imagetranslate.com had 27.6K visits — [Similarweb](https://www.similarweb.com/website/imagetranslate.com/).
  - onlineocr.net's top keywords are the Russian for "photo translator" and "translation by photo", so OCR-site traffic partly comes from translation intent — [Similarweb](https://www.similarweb.com/website/onlineocr.net/).
- PDF sign and edit:
  - Smallpdf lists Sign PDF and Edit PDF in its "Most Popular PDF Tools" block — [smallpdf.com](https://smallpdf.com/).
  - Sejda's whole 16.4M is driven by "pdf editor" keywords — [Similarweb](https://www.similarweb.com/website/sejda.com/).
  - Reviewers mention iLovePDF signing contracts — [Capterra](https://capterra.com/p/173963/iLovePDF/reviews/).

### Inferences
- [Inf] Proposed demand × practicality order for the writer:
  1. **Background removal.** Very high demand, and as a drag-and-drop action it fits Swivel's "drop on an option" model perfectly.
  2. **MP3 output.** High demand. On-device encoding feasibility on macOS without third-party code needs verification; I did not check it here.
  3. **Target-size video compression** (presets such as 8/10/25 MB). High demand.
  4. **Target-size image compression** ("under 100 KB / 20 KB"). High demand. This extends the existing quality-based compressor.
  5. **WebP output.** A cheap gap with strong keyword evidence.
  6. **Transcription to subtitles (SRT) and text.** Mid-to-high demand, though TurboScribe's figure is inflated.
  7. **Upscaling.** Mid demand.
  8. **ID-photo sizes.** Niche but persistent in CN and IN, and it reuses background removal.
  9. **Long tail:** PDF sign/edit, PDF protect/unlock, PDF OCR, watermarks and page numbers, RAR/7z extraction, ICO/favicon. These are cheap to build but each has low measurable standalone demand.
  10. **Image translation.** Lowest as a standalone site; demand appears to be absorbed by Google Translate/Lens. No data was obtained on those.
- [Inf] Licensing and permissions are out of scope here, but some candidates may conflict with Swivel's no-third-party-dependency rule: RAR extraction (unrar licence), upscaling (model weights) and transcription (models). The writer should cross-check with the feasibility notes.

### Gaps
- No direct search-volume data for "unlock pdf", "protect pdf", "add page numbers", "pdf ocr", "png to ico", "7z" or "translate image" was collected in this task. They do not appear in any visible top-5 keyword list. Keyword-volume research, if covered elsewhere, should fill this.
- No usage data for Google Translate/Lens image translation.

## Q4. Signals specific to Mac users

### Takeaway
No source publishes a Mac share of visits. The indirect signals are as follows.

**PDF tools are overwhelmingly desktop in the US.** Semrush puts US desktop share at about 85–90% for ilovepdf and smallpdf, against about 57–73% for converters and remove.bg.

**Several services ship a native Mac app:**
- iLovePDF (Mac App Store, offline processing).
- remove.bg (Windows/Mac/Linux desktop, API key required).

**Others do not:**
- Smallpdf offers a "Windows App" only on its homepage.
- PDF24's desktop software is Windows-only.
- TinyPNG has no desktop app.

**HEIC→JPG demand is concentrated in iPhone-heavy markets** (US 45.9%, plus the UK, Canada and Australia).

**Mac-native open-source tools are popular:** Keka, ImageOptim and Clop.

### Cited Findings
- [3P-SR] US desktop share, Sep 2026:
  - ilovepdf.com 89.52% — [Semrush](https://www.semrush.com/website/ilovepdf.com/overview/).
  - smallpdf.com 84.9% — [Semrush](https://www.semrush.com/website/smallpdf.com/overview/).
  - cloudconvert.com 72.67% (UK 79.56%) — [Semrush](https://www.semrush.com/website/cloudconvert.com/overview/).
  - iloveimg.com 72.88% — [Semrush](https://www.semrush.com/website/iloveimg.com/overview/).
  - remove.bg 58.84% — [Semrush](https://www.semrush.com/website/remove.bg/overview/).
  - freeconvert.com 57.52% — [Semrush](https://www.semrush.com/website/freeconvert.com/overview/).
  - Desktop share is lower in India and Indonesia: about 40–73% depending on site.
- [1P] iLovePDF has a Mac App Store app, free with in-app purchases, that processes files "offline, locally on your computer": merge, split, compress, repair, Office↔PDF conversion and Protect PDF — [App Store](https://apps.apple.com/us/app/ilovepdf-pdf-editor-scan/id1207332399), [iLovePDF blog](https://www.ilovepdf.com/blog/pdf-editing-tools-for-mac-or-windows-pc).
- [1P] remove.bg ships desktop apps for Windows, Mac and Linux with drag-and-drop batch processing. They need an API key — [remove.bg desktop](https://remove.bg/windows-mac-linux).
- [1P] Smallpdf's homepage advertises a "Windows App" and mobile apps, with no Mac desktop app mentioned — [smallpdf.com](https://smallpdf.com/).
- [1P] PDF24 describes its desktop software as "for Windows" — [PDF24 Tools](https://tools.pdf24.org/en/).
- [1P] TinyPNG offers web, API, CDN and WordPress/Figma plugins, with no desktop app — [tinypng.com](https://tinypng.com/).
- [3P-SW] heictojpg.com traffic by country: US 45.9%, UK 6.49%, Canada 5.29%, Australia 3.72% — [Similarweb](https://www.similarweb.com/website/heictojpg.com/).
- [3P-SW] ezgif.com (US 27.24%) and otter.ai (US, UK, Canada) skew toward US and Anglophone markets. PDF, background-removal and converter sites skew toward India, Brazil and Indonesia — [Similarweb ezgif](https://www.similarweb.com/website/ezgif.com/), [otter](https://www.similarweb.com/website/otter.ai/), [ilovepdf](https://www.similarweb.com/website/ilovepdf.com/).
- GitHub popularity of Mac-native file tools: [Keka](https://github.com/aonez/Keka) 7,533 stars; [ImageOptim](https://github.com/ImageOptim/ImageOptim) 10,007; [Clop](https://github.com/FuzzyIdeas/Clop) 1,852.

### Inferences
- [Inf] PDF tasks are done at a desk; most Mac users are desktop users; and PDF services are pushing native apps (iLovePDF's offline Mac app). Together these suggest PDF and Office conversion features are valued on Mac even though macOS Preview covers some of them.
- [Inf] Swivel already wins on a privacy argument: it runs locally, with no upload and no API key. remove.bg's desktop app still needs an API key and cloud credits, and TinyPNG has no Mac app. Locally run background removal and compression are a differentiator, not just parity.
- [Inf] The traffic leaders' largest audiences are in India, Brazil and Indonesia, where Mac share is low. Raw visit counts therefore overstate Mac-user demand for any tool, most for PDF and background removal and least for HEIC and GIF tools, which skew toward the US and Anglophone markets.

### Gaps
- No public macOS or OS-level traffic split for any of these sites; Similarweb's OS breakdown is paid.
- No Mac App Store download or ranking data was collected for Photoroom, remove.bg, iLovePDF, Clideo or The Unarchiver.
- Whether Photoroom offers a native macOS app was not confirmed; the search returned nothing.
- CloudConvert desktop-app availability was not confirmed.
