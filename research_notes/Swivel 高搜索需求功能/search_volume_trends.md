# Search demand (keyword volume + Google Trends) for drag-and-drop file tasks

Research date: 2026-10-21. Scope: monthly search volume and relative interest for ~90 file-processing keywords, to show which *new* Swivel features have the most search demand. Already-built Swivel features are included as a baseline and marked **built**.

**Evidence labels used throughout**
- **(a) first-party measured**: Google Trends relative index (0–100, normalised within each comparison), pulled from Google Trends' own JSON endpoint (the data behind trends.google.com/explore).
- **(a−) first-party, relayed**: Google Ads (Keyword Planner) average monthly searches for the **US**, as published by seodata.dev ("Monthly average for US, from the Google Ads API: the same numbers you'd see in Google Ads"). These are Google's own figures, but rounded into buckets and grouped across close variants; seodata.dev is the third party relaying them.
- **(b) third-party estimate**: Ahrefs (via its public ahrefstop.com site pages, which show only the site's top country) and Exploding Topics.
- **(c) inference**: anything labelled *Inference*.

**How to read the numbers (important)**
- Google Ads groups close variants under one number. "remove background", "background remover" and "remove bg" all show **823,000**, and "pdf to word" / "convert pdf to word" show 246,000 / 110,000. These figures **must not be added together**; each cluster is roughly one demand pool ([seodata: remove background](https://www.seodata.dev/keyword/remove-background), [seodata: background remover](https://www.seodata.dev/keyword/background-remover)).
- Google Trends without quotes matches words "in any order" and excludes plurals and spelling variants ([Google Trends Help: Search tips](https://support.google.com/trends/answer/4359582)). So directional pairs merge: "pdf to word" and "word to pdf" returned identical series (5-year means 78.4 vs 78.2, same peak week). "jpg to pdf" likewise includes "pdf to jpg".
- Ahrefs public pages show one country only, usually **India** for global tool sites (iLovePDF, remove.bg, iLoveIMG, 11zon, pi7) and **US** for others. They are not global totals.

---

## Q1. Absolute monthly search volumes (US and global or India)

### Takeaway
The best consistent dataset obtainable was **US Google Ads average monthly searches** (relayed by seodata.dev, updated May–Oct 2026) for about 90 keywords. Among tasks Swivel does **not** yet do, the largest US demand pools are background removal (823K cluster), images→PDF (201K for "jpg to pdf"), MP3 output (165K for "mp4 to mp3"), video compression with a size target (90.5K for "video compressor"), image upscaling/enhancing (~50K), image translation (40.5K) and transcription (33K for "speech to text", 22K for "transcribe audio to text"). India-market Ahrefs figures are 5–20× larger for PDF and background-removal terms and show a big "compress to N KB" market that barely exists in the US.

### Cited Findings

**Master table: US average monthly searches (Google Ads via seodata.dev; label a−).** "Updated" is the seodata refresh date for that keyword. A dated cell of 2026-10-21 means a fresh API lookup made during this research. Cluster/variant volumes come from the same page's "Also searched / similar" list. Trends columns come from the Q2 data (label a): WW = worldwide, ratio of last-52-week mean to "compress pdf"; growth = last-52-week mean ÷ 2022 mean.

**A. Background removal, AI image, ID photos**

| Keyword | Swivel | US monthly (updated) | Close variants (same source) | Other estimates | Trends WW (×compress pdf) / growth |
|---|---|---|---|---|---|
| remove background | NEW | [823,000](https://www.seodata.dev/keyword/remove-background) (2026-07-12) | remove bg 823,000; background remove 110,000; erase background free 90,500 | Ahrefs India (Oct 2026): remove bg 5.8M, background remover 2.5M, bg remove 2M, remove background 1.5M, bg remover 579K ([Ahrefs/remove.bg](https://ahrefstop.com/websites/remove.bg)) | 1.63× / 1.47× |
| background remover | NEW | [823,000](https://www.seodata.dev/keyword/background-remover) (2026-05-24); same cluster as above | — | — | — |
| remove background from image | NEW | [60,500](https://www.seodata.dev/keyword/remove-background-from-image) (2026-07-16) | — | — | — |
| passport photo | NEW | [246,000](https://www.seodata.dev/keyword/passport-photo) (2026-07-26) | passport photos / photo for passport 246,000 (this cluster includes in-person service intent) | — | — |
| passport photo maker | NEW | [5,400](https://www.seodata.dev/keyword/passport-photo-maker) (2026-07-08) | passport photo generator 5,400; free passport photo 4,400 | — | — |
| id photo | NEW | [9,900](https://www.seodata.dev/keyword/id-photo) (2026-06-08) | passport size photo 14,800 | — | — |
| change photo background color | NEW | [1,600](https://api.seodata.dev/v1/keyword?q=change+photo+background+color&country=us) (2026-10-21) | change background of photo 12,100; how to photo background change 6,600 | — | — |
| image upscaler | NEW | [49,500](https://www.seodata.dev/keyword/image-upscaler) (2026-07-11) | image upscaling 49,500; upscale pictures 40,500; ai upscale 27,100 | Ahrefs US (Oct 2026): image upscaler 39K, upscale image 16K, ai upscale 16K ([Ahrefs/upscale.media](https://ahrefstop.com/websites/upscale.media)); Exploding Topics: 673K, growth −2%, "regular" (period and region not stated) ([ET](https://explodingtopics.com/topic/image-upscaler-KdL22fcu)) | not fetched |
| upscale image | NEW | [27,100](https://www.seodata.dev/keyword/upscale-image) (2026-07-12) | — | — | — |
| ai image upscaler | NEW | [22,200](https://www.seodata.dev/keyword/ai-image-upscaler) (2026-07-11) | — | — | — |
| enhance image | NEW | [6,600](https://www.seodata.dev/keyword/enhance-image) (2026-10-19) | image enhancer 40,500; enhance image quality 27,100; enhance photo quality 27,100 | — | — |
| remove object from photo | NEW | [14,800](https://www.seodata.dev/keyword/remove-object-from-photo) (2026-07-29) | remove object from image 14,800 | — | — |
| remove watermark | NEW | [33,100](https://www.seodata.dev/keyword/remove-watermark) (2026-08-29) | watermark remover 110,000; remove a watermark 110,000; remove watermark from photo 22,200 | — | — |
| add watermark to photo | NEW | [1,600](https://www.seodata.dev/keyword/add-watermark-to-photo) (2026-05-25) | photo watermark / watermark a picture 5,400 | — | — |

**B. Image compress, resize, crop (mostly built)**

| Keyword | Swivel | US monthly | Close variants | Other estimates | Trends WW / growth |
|---|---|---|---|---|---|
| resize image | built | [60,500](https://www.seodata.dev/keyword/resize-image) (2026-07-12) | image resizer / image resize 110,000 | Ahrefs India: resize image 719K ([Ahrefs/iloveimg](https://ahrefstop.com/websites/iloveimg.com)) | 0.32× / 1.23× |
| collage maker | built | [60,500](https://www.seodata.dev/keyword/collage-maker) (2026-05-27) | collage generator 60,500 | — | — |
| crop image | built | [27,100](https://www.seodata.dev/keyword/crop-image) (2026-07-12) | crop a picture 27,100 | — | — |
| compress image | built | [22,200](https://www.seodata.dev/keyword/compress-image) (2026-07-16) | compression picture 74,000; compress picture 60,500; photo compressor 60,500 | Ahrefs India: compress image 313K, jpg compress 193K, compress jpg 71K ([Ahrefs/iloveimg](https://ahrefstop.com/websites/iloveimg.com)) | 0.17× / 1.46× |
| compress jpeg | built | [12,100](https://www.seodata.dev/keyword/compress-jpeg) (2026-07-22) | jpeg image compressor 12,100 | — | — |
| compress image to 100kb | NEW (target size) | [170](https://www.seodata.dev/keyword/compress-image-to-100kb) (2026-07-16) | compress jpeg 100 kb 170 | Ahrefs India (Oct 2026): "compress pdf to 100kb" 108K, "compress pdf to 200kb" 92K, "pdf resizer" 90K, "photo resizer in kb" 36K, "kb converter" 24K ([Ahrefs/pi7.org](https://ahrefstop.com/websites/pi7.org)) | — |
| compress image to 200kb | NEW (target size) | [170](https://www.seodata.dev/keyword/compress-image-to-200kb) (2026-07-19) | compress jpeg to 200kb 140 | see above | — |
| compress image to 50kb | NEW (target size) | [140](https://www.seodata.dev/keyword/compress-image-to-50kb) (2026-06-14) | — | — | — |
| compress image to 20kb | NEW (target size) | [110](https://www.seodata.dev/keyword/compress-image-to-20kb) (2026-06-14) | compress image 20kb 140 | — | — |

**C. Image format conversion and icons**

| Keyword | Swivel | US monthly | Close variants | Other estimates | Trends WW / growth |
|---|---|---|---|---|---|
| heic to jpg | built | [246,000](https://www.seodata.dev/keyword/heic-to-jpg) (2026-07-09) | convert heic to jpg 60,500; heic to jpg converter [33,100](https://www.seodata.dev/keyword/heic-to-jpg-converter) | — | 0.25× / 1.35× |
| webp to png | built (WebP read) | [201,000](https://www.seodata.dev/keyword/webp-to-png) (2026-07-13) | webp png 201,000 | Ahrefs US: 172K ([Ahrefs/cloudconvert](https://ahrefstop.com/websites/cloudconvert.com)) | not fetched |
| webp to jpg | built (WebP read) | [135,000](https://www.seodata.dev/keyword/webp-to-jpg) (2026-07-12) | convert webp to jpg 27,100 | Ahrefs US: 108K ([Ahrefs/ezgif](https://ahrefstop.com/websites/ezgif.com)); Ahrefs India: 233K ([Ahrefs/iloveimg](https://ahrefstop.com/websites/iloveimg.com)) | not fetched |
| jpg to webp | NEW (WebP output) | [6,600](https://www.seodata.dev/keyword/jpg-to-webp) (2026-05-16) | convert to webp 1,900; CPC $17.11 (highest in set) | — | — |
| png to jpg | built | [60,500](https://www.seodata.dev/keyword/png-to-jpg) (2026-07-11) | png file to jpg 49,500 | — | — |
| jpg to png | built | [40,500](https://www.seodata.dev/keyword/jpg-to-png) (2026-05-11) | — | — | — |
| heic to png | built | [40,500](https://www.seodata.dev/keyword/heic-to-png) (2026-05-19) | — | — | — |
| png to svg | NEW (vectorise) | [40,500](https://www.seodata.dev/keyword/png-to-svg) (2026-07-11) | converter svg 40,500 | — | — |
| svg to png | NEW (SVG input not listed) | [33,100](https://www.seodata.dev/keyword/svg-to-png) (2026-07-19) | — | — | — |
| compress gif | NEW | [40,500](https://www.seodata.dev/keyword/compress-gif) (2026-06-14) | gif compressor 40,500 | Ahrefs US: gif compressor 24K ([Ahrefs/freeconvert](https://ahrefstop.com/websites/freeconvert.com)) | — |
| png to ico | NEW | [22,200](https://www.seodata.dev/keyword/png-to-ico) (2026-08-20) | ico converter 6,600 | — | — |
| favicon generator | NEW | [12,100](https://www.seodata.dev/keyword/favicon-generator) (2026-07-07) | — | — | — |
| png to icns | NEW | [880](https://api.seodata.dev/v1/keyword?q=png+to+icns&country=us) (2026-10-21) | png to icns online 880; convert png to icns 170 | — | — |
| image converter | built (generic) | [22,200](https://www.seodata.dev/keyword/image-converter) (2026-07-11) | — | — | — |

**D. OCR and translation**

| Keyword | Swivel | US monthly | Close variants | Trends |
|---|---|---|---|---|
| image to text | built | [90,500](https://www.seodata.dev/keyword/image-to-text) (2026-05-11) | text from picture 90,500; image to text converter 22,200 | not fetched |
| extract text from image | built | [12,100](https://www.seodata.dev/keyword/extract-text-from-image) (2026-05-14) | — | — |
| translate image | NEW | [40,500](https://www.seodata.dev/keyword/translate-image) (2026-08-13) | image translator [40,500](https://www.seodata.dev/keyword/image-translator); translate by picture 33,100 | not fetched |
| translate pdf | NEW | [14,800](https://api.seodata.dev/v1/keyword?q=translate+pdf&country=us) (2026-07-16) | pdf translator 8,100 | — |
| translate document | NEW | [12,100](https://api.seodata.dev/v1/keyword?q=translate+document&country=us) (2026-08-01) | document translator 12,100 | — |
| pdf ocr / ocr pdf | NEW (searchable PDF) | [5,400](https://www.seodata.dev/keyword/pdf-ocr) / [4,400](https://www.seodata.dev/keyword/ocr-pdf) | ocr a pdf 5,400 | "ocr pdf" 0.22× / 2.94× |

**E. PDF and office documents**

| Keyword | Swivel | US monthly | Close variants | Other estimates | Trends WW / growth |
|---|---|---|---|---|---|
| pdf to word | built | [246,000](https://www.seodata.dev/keyword/pdf-to-word) (2026-07-11) | convert pdf to word 110,000 | Ahrefs US (Aug 2026): 230K ([Ahrefs/smallpdf](https://ahrefstop.com/websites/smallpdf.com)); Ahrefs India: 4.0M ([Ahrefs/ilovepdf](https://ahrefstop.com/websites/ilovepdf.com)) | 5.03× (merged with "word to pdf") / 1.08× |
| jpg to pdf | NEW (images→PDF) | [201,000](https://www.seodata.dev/keyword/jpg-to-pdf) (2026-05-25) | convert jpg to pdf 49,500 | Ahrefs US: 168K ([Ahrefs/smallpdf](https://ahrefstop.com/websites/smallpdf.com)); Ahrefs India: 4.8M ([Ahrefs/ilovepdf](https://ahrefstop.com/websites/ilovepdf.com)) | 3.45× (merged with "pdf to jpg") / 1.29× |
| pdf to jpg | built | [165,000](https://www.seodata.dev/keyword/pdf-to-jpg) (2026-07-11) | — | Ahrefs US: 190K ([Ahrefs/smallpdf](https://ahrefstop.com/websites/smallpdf.com)) | (see above) |
| merge pdf | built | [135,000](https://www.seodata.dev/keyword/merge-pdf) (2026-05-25) | combine pdf 135,000 | Ahrefs India: 1.7M ([Ahrefs/11zon](https://ahrefstop.com/websites/11zon.com)) | 1.46× / 1.70× |
| compress pdf | built (anchor) | [110,000](https://www.seodata.dev/keyword/compress-pdf) (2026-05-25) | pdf compress 90,500; pdf size reducer 33,100 | Ahrefs India: 1.6M; pdf compressor 1.3M ([Ahrefs/11zon](https://ahrefstop.com/websites/11zon.com)) | 1.00× / 1.71× |
| word to pdf | built | [110,000](https://www.seodata.dev/keyword/word-to-pdf) (2026-07-11) | doc to pdf 18,100 | — | (merged with "pdf to word") |
| pdf converter | built (generic) | [110,000](https://www.seodata.dev/keyword/pdf-converter) (2026-07-14) | convert to pdf 40,500 | — | — |
| image to pdf | NEW | [60,500](https://www.seodata.dev/keyword/image-to-pdf) (2026-05-10) | images to pdf 60,500 | — | 1.17× / 2.72× |
| epub to pdf | NEW | [60,500](https://www.seodata.dev/keyword/epub-to-pdf) (2026-08-14) | — | — | — |
| split pdf | built | [40,500](https://www.seodata.dev/keyword/split-pdf) (2026-07-11) | pdf splitter 33,100 | — | 0.62× / 2.63× |
| pdf to excel | NEW | [33,100](https://www.seodata.dev/keyword/pdf-to-excel) (2026-07-11) | convert pdf to excel 27,100 | — | 1.12× / 1.43× |
| sign pdf | NEW | [22,200](https://www.seodata.dev/keyword/sign-pdf) (2026-07-24) | sign a pdf 22,200 | — | 0.58× / **4.64×** |
| ppt to pdf | NEW (PPT not listed) | [22,200](https://www.seodata.dev/keyword/ppt-to-pdf) (2026-07-11) | powerpoint to pdf 22,200 | — | 0.41× / 1.55× |
| pdf to powerpoint | NEW | [22,200](https://www.seodata.dev/keyword/pdf-to-powerpoint) (2026-07-11) | pdf to ppt 22,200 | — | — |
| unlock pdf | NEW | [14,800](https://www.seodata.dev/keyword/unlock-pdf) (2026-07-11) | pdf unlocker 6,600 | — | 0.18× / 2.55× |
| excel to pdf | NEW | [9,900](https://www.seodata.dev/keyword/excel-to-pdf) (2026-07-11) | xls convert to pdf 14,800 | — | — |
| remove password from pdf | NEW | [8,100](https://www.seodata.dev/keyword/remove-password-from-pdf) (2026-10-09) | how to remove password from pdf 12,100 | — | — |
| password protect pdf | NEW | [8,100](https://www.seodata.dev/keyword/password-protect-pdf) (2026-07-24) | how to password protect a pdf 8,100 | — | 0.02× / 1.33× (values 1–2, imprecise) |
| redact pdf | NEW (Swivel redacts images only) | [5,400](https://www.seodata.dev/keyword/redact-pdf) (2026-07-09) | how to redact a pdf 5,400; CPC $6.97 | — | — |
| extract images from pdf | NEW | [3,600](https://www.seodata.dev/keyword/extract-images-from-pdf) (2026-06-05) | — | — | — |
| add page numbers to pdf | NEW | [2,900](https://www.seodata.dev/keyword/add-page-numbers-to-pdf) (2026-05-24) | — | — | — |
| watermark pdf | NEW | [1,600](https://www.seodata.dev/keyword/watermark-pdf) (2026-07-24) | put watermark on pdf 2,900; add watermark to pdf 2,400 | — | — |

**F. Audio and video**

| Keyword | Swivel | US monthly | Close variants | Other estimates | Trends |
|---|---|---|---|---|---|
| gif maker | partly built (video→GIF) | [201,000](https://www.seodata.dev/keyword/gif-maker) (2026-05-14) | animated gif maker 201,000 | Ahrefs US: 186K ([Ahrefs/veed](https://ahrefstop.com/websites/veed.io)) | not fetched |
| mp4 to mp3 | NEW (no MP3 output) | [165,000](https://www.seodata.dev/keyword/mp4-to-mp3) (2026-05-12) | mp4 convert to mp3 60,500; mp3 converter 246,000 (generic, from [video to mp3](https://www.seodata.dev/keyword/video-to-mp3) page) | Ahrefs US: 145K ([Ahrefs/cloudconvert](https://ahrefstop.com/websites/cloudconvert.com)) | not fetched |
| video compressor | partial (HEVC re-encode only; no size target) | [90,500](https://www.seodata.dev/keyword/video-compressor) (2026-07-11) | — | Ahrefs US: 58K ([Ahrefs/freeconvert](https://ahrefstop.com/websites/freeconvert.com)) | not fetched |
| video to gif | built | [90,500](https://www.seodata.dev/keyword/video-to-gif) (2026-07-14) | — | Ahrefs US: 79K ([Ahrefs/ezgif](https://ahrefstop.com/websites/ezgif.com)) | — |
| mp4 to gif | built | [90,500](https://www.seodata.dev/keyword/mp4-to-gif) (2026-05-11) | — | Ahrefs US: 116K ([Ahrefs/ezgif](https://ahrefstop.com/websites/ezgif.com)) | — |
| vocal remover | NEW | [74,000](https://www.seodata.dev/keyword/vocal-remover) (2026-07-11) | remove vocals 74,000 | ET: "ai vocal remover" 33.1K, +38% (snapshot via search; period not stated) ([ET](https://explodingtopics.com/topic/ai-vocal-remover?period=24)) | — |
| video to mp3 | NEW | [60,500](https://www.seodata.dev/keyword/video-to-mp3) (2026-07-08) | video to mp3 converter 40,500 | — | — |
| mov to mp4 | built | [60,500](https://www.seodata.dev/keyword/mov-to-mp4) (2026-07-11) | convert mov to mp4 22,200 | — | — |
| m4a to mp3 | NEW | [49,500](https://www.seodata.dev/keyword/m4a-to-mp3) (2026-05-14) | convert m4a to mp3 14,800 | — | — |
| compress video | partial | [33,100](https://www.seodata.dev/keyword/compress-video) (2026-07-13) | compress video online 12,100 | — | — |
| wav to mp3 | NEW | [33,100](https://www.seodata.dev/keyword/wav-to-mp3) (2026-07-31) | — | — | — |
| mp3 to wav | built | [33,100](https://www.seodata.dev/keyword/mp3-to-wav) (2026-08-20) | — | Ahrefs US: 43K ([Ahrefs/cloudconvert](https://ahrefstop.com/websites/cloudconvert.com)) | — |
| convert to mp3 | NEW | [27,100](https://www.seodata.dev/keyword/convert-to-mp3) (2026-06-08) | — | — | — |
| video converter | built (generic) | [27,100](https://www.seodata.dev/keyword/video-converter) (2026-07-14) | video convert 33,100 | Ahrefs US: 45K ([Ahrefs/veed](https://ahrefstop.com/websites/veed.io)) | — |
| trim video | built | [8,100](https://www.seodata.dev/keyword/trim-video) (2026-07-14) | video trimmer 22,200; trim a video 22,200 | — | — |
| merge videos | built | [4,400](https://www.seodata.dev/keyword/merge-videos) (2026-10-08) | video merger 3,600 | — | — |
| remove background noise from audio | NEW | [2,900](https://www.seodata.dev/keyword/remove-background-noise-from-audio) (2026-07-15) | background noise removal 2,400 | — | — |
| rotate video | NEW | [1,900](https://www.seodata.dev/keyword/rotate-video) (2026-08-22) | video rotate 2,900 | — | — |
| compress video for discord | NEW (size target) | [1,300](https://www.seodata.dev/keyword/compress-video-for-discord) (2026-05-18) | discord video compressor 6,600; discord compressor 2,900 | — | — |
| compress video for email | NEW (size target) | [880](https://www.seodata.dev/keyword/compress-video-for-email) (2026-08-14) | compress a video file 9,900 | — | — |

**G. Transcription, subtitles, speech**

| Keyword | Swivel | US monthly | Close variants | Other estimates |
|---|---|---|---|---|
| text to speech | NEW (not in brief; discovered) | [301,000](https://api.seodata.dev/v1/keyword?q=text+to+speech&country=us) (2026-07-10) | text to speech free 40,500 | — |
| speech to text | NEW | [33,100](https://api.seodata.dev/v1/keyword?q=speech+to+text&country=us) (2026-07-10) | voice to text 22,200; voice typing 12,100 ("speech text" 301,000 is probably grouped with text-to-speech; *inference*) | — |
| transcribe audio to text | NEW | [22,200](https://www.seodata.dev/keyword/transcribe-audio-to-text) (2026-07-09) | audio to text transcription 18,100; audio transcription 14,800 | Ahrefs US: 9.7K; turboscribe.ai had 5.2M US organic visits in Oct 2026 ([Ahrefs/turboscribe](https://ahrefstop.com/websites/turboscribe.ai)) |
| transcribe audio | NEW | [14,800](https://www.seodata.dev/keyword/transcribe-audio) (2026-08-01) | — | — |
| audio to text | NEW | [9,900](https://www.seodata.dev/keyword/audio-to-text) (2026-05-16) | — | — |
| video to text | NEW | [6,600](https://www.seodata.dev/keyword/video-to-text) (2026-07-24) | transcribe video / video transcription 18,100 | Ahrefs US: video transcription 2.9K, ai transcription 2.3K ([Ahrefs/turboscribe](https://ahrefstop.com/websites/turboscribe.ai)); ET "ai transcription" 14.8K, +175%, "peaked" ([ET](https://explodingtopics.com/topic/ai-transcription)) |
| subtitle generator | NEW | [1,900](https://www.seodata.dev/keyword/subtitle-generator) (2026-07-11) | auto caption 2,400; video subtitle generator 880 | — |
| srt generator | NEW | [110](https://www.seodata.dev/keyword/srt-generator) (2026-08-04) | srt file creator 480 | — |

**H. Archives**

| Keyword | Swivel | US monthly | Close variants |
|---|---|---|---|
| unzip | built | [8,100](https://api.seodata.dev/v1/keyword?q=unzip&country=us) (2026-10-21) | decompress zip file 12,100; unzip files 9,900; open zip file 5,400 |
| rar extractor | NEW | [6,600](https://www.seodata.dev/keyword/rar-extractor) (2026-10-19) | extract rar 6,600 |
| the unarchiver (context: free Mac app) | — | [2,900](https://api.seodata.dev/v1/keyword?q=the+unarchiver&country=us) (2026-10-21) | the unarchiver mac 5,400; unarchiver 3,600 |
| 7z mac | NEW | [390](https://api.seodata.dev/v1/keyword?q=7z+mac&country=us) (2026-10-21) | — |
| (Mac RAR variants: see Q3) | | | |

**I. Privacy and utilities**

| Keyword | Swivel | US monthly | Close variants |
|---|---|---|---|
| qr code scanner | built (QR reading) | [201,000](https://www.seodata.dev/keyword/qr-code-scanner) (2026-07-19) | qr code reader 201,000 (mostly phone-camera intent; *inference*) |
| color picker from image | NEW | [60,500](https://www.seodata.dev/keyword/color-picker-from-image) (2026-06-06) | color picker from an image 49,500; color identifier 14,800 |
| blur face | built | [5,400](https://www.seodata.dev/keyword/blur-face) (2026-07-27) | blur faces 5,400 |
| remove metadata from photo | built | [2,400](https://www.seodata.dev/keyword/remove-metadata-from-photo) (2026-07-14) | delete metadata 4,400 |
| remove exif data | built | [1,300](https://www.seodata.dev/keyword/remove-exif-data) (2026-07-18) | remove image metadata 2,400 |
| duplicate file finder mac | NEW | [1,300](https://api.seodata.dev/v1/keyword?q=duplicate+file+finder+mac&country=us) (2026-10-21) | macos duplicate file finder 1,300; duplicate finder mac 720 |
| keep mac awake | NEW | [260](https://api.seodata.dev/v1/keyword?q=keep+mac+awake&country=us) (2026-10-21) | how to stop mac from sleeping 2,400; prevent mac from sleeping 1,000 |
| sha256 checksum mac | NEW | [20](https://api.seodata.dev/v1/keyword?q=sha256+checksum+mac&country=us) (2026-10-21) | sha256 mac 70 |

**J. Mac context (not file tasks)**: "how to screenshot on mac" [450,000](https://www.seodata.dev/keyword/how-to-screenshot-on-mac) (2026-10-18); "screenshot mac" [90,500](https://www.seodata.dev/keyword/screenshot-mac); "screen recording mac" [22,200](https://api.seodata.dev/v1/keyword?q=screen+recording+mac&country=us), with "how to screen record on mac" 110,000 in the same response.

**Cross-source check (US; Ahrefs Aug–Oct 2026 vs Google Ads)**: pdf to word 230K vs 246K; jpg to pdf 168K vs 201K; pdf to jpg 190K vs 165K; webp to png 172K vs 201K; webp to jpg 108K vs 135K; mp4 to mp3 145K vs 165K; gif maker 186K vs 201K; video to gif 79K vs 90.5K; image upscaler 39K vs 49.5K; video compressor 58K vs 90.5K; transcribe audio to text 9.7K vs 22.2K (sources as cited in the rows above). Most pairs agree within 0.8–1.7×. Transcription differs 2.3×, consistent with Google Ads pooling close variants.

### Inferences
- *(c)* **Largest new US demand pools**, deduplicated by cluster and not added together: background removal (~823K) ≫ images→PDF (jpg to pdf 201K, image to pdf 60.5K) ≈ MP3 output (mp4 to mp3 165K; video to mp3 60.5K; m4a to mp3 49.5K; wav to mp3 33.1K; convert to mp3 27.1K; these are separate phrasings, partly the same users) > video compression with a size target (video compressor 90.5K, partly covered) > vocal remover (74K) > epub to pdf (60.5K) ≈ color picker from image (60.5K) > image upscale/enhance (~40–50K) ≈ translate image (40.5K) ≈ png to svg (40.5K) ≈ compress gif (40.5K) > speech-to-text/transcription (22–33K per phrasing) ≈ pdf to excel (33.1K) > sign pdf / ppt to pdf / pdf to ppt / png to ico (22.2K each) > unlock pdf / translate pdf / remove object (14.8K) > favicon (12.1K) > PDF password, OCR, redact (5–8K) > long tail (page numbers, watermark PDF, subtitle/SRT, Discord/email presets, ICNS, duplicate finder, keep-awake, checksum: under 3K).
- *(c)* "text to speech" (301K) is a large adjacent query the brief did not list. It is mostly a web/AI-voice intent, so its fit with a drop-a-file workflow is uncertain.
- *(c)* Target-size compression ("to 100kb / 20kb") is negligible in the US (110–170/month) but large in India (Ahrefs: about 100K per phrasing for PDF), consistent with Indian government and exam upload limits. It is a market-specific feature, not a US one.
- *(c)* "remove watermark" (33K; watermark remover 110K) has high demand but a legitimacy risk (removing others' watermarks). "passport photo" (246K) is dominated by in-person services; the tool-intent phrasing "passport photo maker" is only 5.4K.
- *(c)* CPC is a weak commercial-value signal: jpg to webp $17.11, favicon generator $7.67, redact pdf $6.97, transcribe audio $6.18, speech to text $5.13 vs heic to jpg $0.28 and remove background $0.96 (CPCs from the cited pages).

### Gaps
- **No global absolute volumes**: Ahrefs global numbers sit behind sign-up, and its public pages show one country only. The seodata.dev API (which supports other countries) hit its anonymous limit after ~25 lookups and asked for email registration, which was not done.
- Google Ads figures are 12-month averages in rounded buckets. No monthly series or seasonality was available from this source.
- Exploding Topics figures do not state region or period, and the same page showed 368K/+163% in an older search snapshot vs 673K/−2% live. Treat them as weak.
- No volume found for "remove exif" in Trends, or for "image to pdf multiple images" phrasing.

---

## Q2. Google Trends relative interest and growth (worldwide)

### Takeaway
Only 4 of 32 planned comparison groups could be fetched before Google Trends rate-limited (HTTP 429) the local IP, WebFetch and the browser pane. They cover PDF and image leaders, all anchored on "compress pdf". Worldwide in the last 52 weeks, "remove background" ran at **1.63×** "compress pdf", "merge pdf" 1.46×, "image to pdf" 1.17×, "pdf to excel" 1.12×, "split pdf" 0.62×, "sign pdf" 0.58×, "resize image" 0.32×, "heic to jpg" 0.25×, "ocr pdf" 0.22×, "unlock pdf" 0.18×, "compress image" 0.17×. Relative to the anchor, the fastest risers since 2022 are **sign pdf, ocr pdf, image to pdf, split pdf and unlock pdf**. Remove background, heic to jpg, resize image and pdf to word grew more slowly than the anchor, so their share is flat to declining.

### Cited Findings
Data: Google Trends JSON endpoint (same data as the Explore page), worldwide, "today 5-y", weekly, fetched 2026-10-21 09:43–09:44 UTC. Complete weeks run 2021-10-17 → 2026-10-11 (partial final week excluded). "Last 52w" = mean of the last 52 complete weeks. "Growth" = last-52w mean ÷ calendar-2022 mean. "Rel. growth" = growth ÷ anchor growth (1.71–1.72 in every group, a consistency check across groups).

| Group (Explore link) | Term | Last-52w mean | ×compress pdf | Growth vs 2022 | Rel. growth | Peak week |
|---|---|---|---|---|---|---|
| [P01](https://trends.google.com/trends/explore?date=today%205-y&q=compress%20pdf,pdf%20to%20word,merge%20pdf,split%20pdf,word%20to%20pdf) | compress pdf | 15.98 | 1.00 | 1.71 | 1.00 | 2026-06-07 |
| | pdf to word (≡ word to pdf) | 80.33 | 5.03 | 1.08 | 0.63 | 2024-09-01 |
| | merge pdf | 23.35 | 1.46 | 1.70 | 0.99 | 2026-10-04 |
| | split pdf | 9.90 | 0.62 | 2.63 | 1.54 | 2026-02-15 |
| [P02](https://trends.google.com/trends/explore?date=today%205-y&q=compress%20pdf,jpg%20to%20pdf,image%20to%20pdf,pdf%20to%20excel,sign%20pdf) | jpg to pdf (≡ pdf to jpg) | 82.81 | 3.45 | 1.29 | 0.75 | 2026-07-26 |
| | image to pdf | 28.08 | 1.17 | 2.72 | 1.59 | 2026-05-31 |
| | pdf to excel (≡ excel to pdf) | 26.79 | 1.12 | 1.43 | 0.84 | 2026-05-31 |
| | sign pdf | 13.92 | 0.58 | **4.64** | **2.71** | 2026-05-31 |
| [P03](https://trends.google.com/trends/explore?date=today%205-y&q=compress%20pdf,unlock%20pdf,password%20protect%20pdf,ppt%20to%20pdf,ocr%20pdf) | unlock pdf | 14.56 | 0.18 | 2.55 | 1.48 | 2026-05-31 |
| | password protect pdf | 1.33 | 0.02 | 1.33 (values 1–2; imprecise) | — | — |
| | ppt to pdf (≡ pdf to ppt) | 32.69 | 0.41 | 1.55 | 0.90 | 2026-05-31 |
| | ocr pdf | 17.52 | 0.22 | 2.94 | 1.71 | 2026-05-31 |
| [P04](https://trends.google.com/trends/explore?date=today%205-y&q=compress%20pdf,remove%20background,compress%20image,resize%20image,heic%20to%20jpg) | remove background | 50.19 | 1.63 | 1.47 | 0.85 | 2026-02-15 |
| | compress image | 5.10 | 0.17 | 1.46 | 0.85 | 2023-08-06 |
| | resize image | 9.94 | 0.32 | 1.23 | 0.72 | 2024-07-21 |
| | heic to jpg | 7.83 | 0.25 | 1.35 | 0.78 | 2026-02-15 |

Calendar-year means from the same pulls (Trends index):
- P04: remove background 34.2 (2022) → 41.8 (2023) → 41.1 (2024) → 42.3 (2025) → 51.7 (2026 YTD); heic to jpg 5.8 → 6.7 → 6.7 → 6.8 → 8.2; resize image 8.1 → 9.4 → 10.4 → 10.6 → 9.8 ([P04](https://trends.google.com/trends/explore?date=today%205-y&q=compress%20pdf,remove%20background,compress%20image,resize%20image,heic%20to%20jpg)).
- P02: sign pdf 3.0 → 3.0 → 3.3 → 6.1 → 15.3 (2026 YTD), a step-up during 2025–26; image to pdf 10.3 → 12.8 → 15.9 → 21.9 → 28.2 ([P02](https://trends.google.com/trends/explore?date=today%205-y&q=compress%20pdf,jpg%20to%20pdf,image%20to%20pdf,pdf%20to%20excel,sign%20pdf)).
- P03: ocr pdf 6.0 → 6.9 → 9.5 → 13.6 → 17.7; unlock pdf 5.7 → 6.6 → 7.6 → 10.3 → 15.3; ppt to pdf 21.1 → 21.6 → 22.7 → 21.9 → 35.2 ([P03](https://trends.google.com/trends/explore?date=today%205-y&q=compress%20pdf,unlock%20pdf,password%20protect%20pdf,ppt%20to%20pdf,ocr%20pdf)).
- The partial 2021 weeks (Oct–Dec 2021) run at roughly half the 2022 level for every term (e.g. compress pdf 23.3 vs 46.7 in P03). This is consistent with Google's data-collection change applied from 1 Jan 2022, which the Explore page annotates, so 2022 was used as the base year. The 2022 annotation is a known Trends notice, but its text was not re-fetched in this session.
- Many PDF terms share a mild late-May / early-June 2026 high (compress pdf 97–100 on 2026-05-31 and 06-07, vs 72–92 in surrounding weeks in P03). It is a plateau, not a one-week spike ([P03](https://trends.google.com/trends/explore?date=today%205-y&q=compress%20pdf,unlock%20pdf,password%20protect%20pdf,ppt%20to%20pdf,ocr%20pdf)).
- Matching caveat: unquoted terms match "in any order" ([Google Trends Help](https://support.google.com/trends/answer/4359582)). Observed: "pdf to word" and "word to pdf" have identical series, so directional pairs (to/from) cannot be separated without quotes.

### Inferences
- *(c)* Within PDF tools, demand is shifting toward **sign, OCR/searchable, unlock and image→PDF** (each grew 1.5–2.7× faster than "compress pdf" since 2022). Conversion to Word is losing share (0.63× relative), though it is still the largest PDF query.
- *(c)* "remove background" is large and still rising in absolute terms (2026 YTD is the highest year) but grew slower than "compress pdf". It looks like a mature, very large demand pool rather than an emerging one.
- *(c)* "heic to jpg" has been flat in share since 2023 (6.7–6.8 index), with a modest 2026 uptick; "resize image" peaked in 2024.
- *(c)* Worldwide Trends ratios differ from US Google Ads ratios (e.g. remove background is 1.63× compress pdf in WW Trends vs 7.5× in US Google Ads). Likely reasons: different matching rules (Trends broad match includes "compress pdf to 100kb" etc.) and India's weight in worldwide data. Keep the two scales separate.

### Gaps
- **Not fetched (HTTP 429)**: groups for WebP formats, AI upscaling, MP3 conversion, video compression and GIF, transcription and subtitles, image translation and watermarks, archives (RAR/7z), target-size compression, Mac variants (worldwide and US), small utilities, Chinese terms, and "related/rising queries" for remove background, compress image, mp4 to mp3, transcribe, compress video, heic to jpg and pdf. Growth for **transcribe, upscale, webp and MP3 is therefore not measured by Trends here**. The only growth signals are weak Exploding Topics figures (image upscaler −2% "regular"; ai transcription +175% "peaked"; period unstated) ([ET upscaler](https://explodingtopics.com/topic/image-upscaler-KdL22fcu), [ET ai transcription](https://explodingtopics.com/topic/ai-transcription)).
- The built-in browser's navigation to trends.google.com was denied or failed, and WebFetch to the Trends API returned 429. A person can rerun the planned groups in the Trends UI. The group definitions are in the session scratchpad (`scripts/groups.json`, `scripts/groups_cn.json`).
- No 12-month-only pull was made; last-52-week figures come from the 5-year weekly series (same normalisation).

---

## Q3. Mac-specific phrasing ("… mac" / "how to … on mac")

### Takeaway
Mac-qualified queries are usually **0.1–3% of the generic query's US volume**, and Mac users more often phrase them as "how to … on mac". The clear exception is **RAR/archive opening**: Mac-specific RAR queries (open rar mac cluster 3,600; how to open rar files on mac 2,900; rar extractor mac 2,400; "the unarchiver mac" 5,400) are similar in size to the generic "rar extractor" (6,600). That fits macOS having no built-in RAR support. "convert to mp3 mac" (4,400 = 16% of "convert to mp3") is the second strongest Mac-specific signal.

### Cited Findings (US Google Ads average monthly searches; label a−)
| Mac phrasing | US monthly | Generic comparison | Mac share (c) |
|---|---|---|---|
| open rar file mac / open rar mac / rar file opener mac (cluster) | 3,600 ([seodata API](https://api.seodata.dev/v1/keyword?q=how+to+open+rar+files+on+mac&country=us)) | rar extractor 6,600 ([seodata](https://www.seodata.dev/keyword/rar-extractor)) | ~55% |
| how to open rar files on mac | 2,900 (same source) | — | — |
| rar extractor mac | 2,400 ([seodata API](https://api.seodata.dev/v1/keyword?q=rar+extractor+mac&country=us)) | — | — |
| the unarchiver mac | 5,400 ([seodata API](https://api.seodata.dev/v1/keyword?q=the+unarchiver&country=us)) | — | — |
| 7z mac | 390 ([seodata API](https://api.seodata.dev/v1/keyword?q=7z+mac&country=us)) | — | — |
| convert to mp3 mac (= mac convert to mp3) | 4,400 ([seodata API](https://api.seodata.dev/v1/keyword?q=convert+to+mp3+mac&country=us)); mp3 converter mac 590 | convert to mp3 27,100 ([seodata](https://www.seodata.dev/keyword/convert-to-mp3)) | ~16% |
| how to convert mp4 to mp3 on mac | 260; mp4 to mp3 for mac 110 ([seodata API](https://api.seodata.dev/v1/keyword?q=how+to+convert+mp4+to+mp3+on+mac&country=us)) | mp4 to mp3 165,000 | ~0.2% |
| how to combine pdf files on mac | 2,900; mac combine pdf 2,400; merge pdf mac 1,600 ([seodata API](https://api.seodata.dev/v1/keyword?q=how+to+combine+pdf+files+on+mac&country=us)) | merge pdf 135,000 | ~2% |
| how to compress a pdf on mac | 1,900; how to reduce pdf file size mac 2,400 ([seodata API](https://api.seodata.dev/v1/keyword?q=how+to+compress+a+pdf+on+mac&country=us)) | compress pdf 110,000 | ~2% |
| how to convert heic to jpg on mac | 2,900; convert heic to jpg mac 1,600; heic to jpg mac 1,000 ([seodata](https://www.seodata.dev/keyword/heic-to-jpg-mac), 2026-10-20) | heic to jpg 246,000 | ~1% |
| how to compress a video on mac | 720; compress video mac 590 ([seodata API](https://api.seodata.dev/v1/keyword?q=how+to+compress+a+video+on+mac&country=us), [compress video mac](https://api.seodata.dev/v1/keyword?q=compress+video+mac&country=us)) | video compressor 90,500 | ~1% |
| duplicate file finder mac | 1,300; macos duplicate file finder 1,300 ([seodata API](https://api.seodata.dev/v1/keyword?q=duplicate+file+finder+mac&country=us)) | — | — |
| remove background from image mac | 320; how to remove background from image on mac 260; remove background mac 170 ([seodata API](https://api.seodata.dev/v1/keyword?q=remove+background+mac&country=us)) | remove background 823,000 | ~0.04% |
| make gif mac / how to make a gif on mac | 320 / 260 ([seodata API](https://api.seodata.dev/v1/keyword?q=how+to+make+a+gif+on+mac&country=us)) | gif maker 201,000 | ~0.2% |
| transcribe audio mac | 140; transcribe audio to text on mac 110; how to transcribe audio on mac 40 ([seodata API](https://api.seodata.dev/v1/keyword?q=transcribe+audio+mac&country=us)) | transcribe audio 14,800 | ~1% |
| keep mac awake | 260; how to stop mac from sleeping 2,400; prevent mac from sleeping 1,000 ([seodata API](https://api.seodata.dev/v1/keyword?q=keep+mac+awake&country=us)) | — | — |
| sha256 checksum mac | 20; sha256 mac 70 ([seodata API](https://api.seodata.dev/v1/keyword?q=sha256+checksum+mac&country=us)) | — | — |
| (context) how to screenshot on mac / how to screen record on mac | 450,000 ([seodata](https://www.seodata.dev/keyword/how-to-screenshot-on-mac)) / 110,000 ([seodata API](https://api.seodata.dev/v1/keyword?q=screen+recording+mac&country=us)) | — | — |

### Inferences
- *(c)* Mac-qualified volume is tiny next to the generic web-tool queries, so web search demand mostly measures need for the **task**, and Mac users are a slice of it. Rank features by generic demand, then weight up the tasks macOS handles poorly natively (RAR/7z, MP3 output), where Mac-specific searching is highest.
- *(c)* People search "how to … on mac" for built-in-capable tasks (combine PDFs, compress PDFs, HEIC→JPG: 2–3K each), suggesting a discoverability gap. Swivel's existing PDF/HEIC tools address queries Mac users already ask.
- *(c)* Background removal on Mac is barely searched with a Mac qualifier (≤320). Mac users likely use web tools or the built-in Preview/Photos lift-subject feature (not verified in this research).

### Gaps
- Trends comparisons for Mac variants (worldwide and US) were blocked by HTTP 429. No Mac-variant growth data.
- Volumes are US-only. No Mac-variant data for other countries.

---

## Q4. Chinese-language demand (Baidu)

### Takeaway
**No quantitative Chinese-language demand data could be obtained.** Baidu Index requires an account and returned an "abnormal access" block. 5118 served a CAPTCHA, 站长之家 and 爱站 returned no keyword data, and no published article with Baidu Index figures for these terms was found. Chinese-term Google Trends groups were prepared but blocked by rate limiting (and would mostly miss mainland China, where Google is unavailable). The only evidence is qualitative: many 2026-dated Chinese tutorials target 证件照换底色 (changing ID-photo background colour).

### Cited Findings
- Baidu Index API (index.baidu.com/api/SearchApi/index, word 抠图) returned `{"status":10018, ...}` with the message "百度指数监测到您疑似存在异常访问行为" (abnormal access detected) and no data, on 2026-10-21. Baidu Index normally requires login, which was not attempted ([Baidu Index](https://index.baidu.com/)).
- 5118 keyword page for 抠图 redirected to a "安全验证" CAPTCHA page; not bypassed ([5118](https://www.5118.com/seo/words/%E6%8A%A0%E5%9B%BE)).
- 站长之家 (data.chinaz.com/keyword/allindex/抠图) redirected to a generic long-tail tool page with no figures ([chinaz](https://data.chinaz.com/keyword/allindex/%E6%8A%A0%E5%9B%BE)). 爱站 ci.aizhan.com/抠图/ returned HTTP 500 with only page chrome; it notes it lists only keywords that have an index ([aizhan](https://ci.aizhan.com/%E6%8A%A0%E5%9B%BE/)).
- General description: Baidu Index reflects users' search behaviour on Baidu, and not every keyword is indexed ([woshipm](https://www.woshipm.com/data-analysis/449898.html)), per the search-result summary of that article.
- Qualitative only (content supply, not demand): multiple 2026-dated tutorials and tool round-ups for 证件照换底色 on Sohu, cnblogs, Zhihu and Juejin ([Sohu](https://m.sohu.com/a/1032388923_122856904), [Sohu](https://www.sohu.com/a/1034518596_120535592), [cnblogs](https://www.cnblogs.com/bgxzs/p/20830225), [Juejin](https://juejin.cn/post/7697136186276642826)). They mostly recommend WeChat mini-programs and phone apps (e.g. "证件照生成喵", per the search-result summary).

### Inferences
- *(c)* The India-like "compress to N KB" pattern likely has a Chinese counterpart (图片压缩到200k / 20k for exam and government upload limits), and ID-photo background colour change is a common Chinese need. Neither could be quantified here; treat both as hypotheses.
- *(c)* Chinese demand for these tasks appears to be served largely by WeChat mini-programs and mobile apps rather than desktop tools, which may limit how much of it a Mac menu-bar app can capture.

### Gaps
- Baidu Index values for 抠图 / 去背景, 图片压缩 (到200k/20k), 证件照 / 证件照换底色, 图片转pdf, pdf转word, 视频转mp3 / 提取音频, 视频压缩, 语音转文字 / 视频转文字 / 字幕生成, 图片翻译, rar解压 mac, 加水印 / 去水印, pdf加密 / pdf解密: **not accessible** (login, CAPTCHA, block).
- Google Trends Chinese groups (worldwide simplified-Chinese terms; Taiwan traditional-Chinese terms 去背 / 證件照 / 圖片壓縮 / pdf轉word / 語音轉文字) were defined but not fetched (HTTP 429).
- WeChat Index (微信指数), 巨量算数 (Douyin) and 360 Trends were not tried; all typically require an app or login.
