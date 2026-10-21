# Mac demand signals for small utilities and file tools (install counts, store installs, question views)

All counts below were pulled on 2026-10-21 (UTC timestamps given per source). Raw downloads are in the session scratchpad (`brew_raw/`, `raycast_raw/`, `se_raw/`, `se_raw2/`, `mas_raw/`), not in the repo. Three kinds of claim are kept apart: **measured** (a number copied from a public endpoint), **arithmetic** (my calculation on measured numbers, e.g. views per year, category sums, 30-day vs 365-day trend ratios), and **inference** (my reading, in the Inferences sections only). All three main sources skew technical. Homebrew users are mostly developers. Raycast users are keyboard-first power users. Ask Different visitors arrive mostly from search engines, and its high-view questions are old.

## Q1. Homebrew cask and formula analytics: which utility categories developers install most

### Takeaway
On Homebrew, the most-installed small Mac utilities are system and menu-bar tools (Stats 144,548 installs in 365 days; Raycast 116,455; Rectangle 115,768; Maccy 111,014), app and disk cleanup (Mole CLI ≈405k; AppCleaner 49,483; Pearcleaner 42,850), menu-bar hiders, and archive tools. Among file-processing tools, ffmpeg (1.76M), ImageMagick (802k), poppler (661k), yt-dlp (623k), tesseract (257k), Whisper (≈219k across formulas) and 7-Zip (≈211k across two formulas) dominate. The fastest-growing GUI categories are local transcription and dictation (30-day pace 1.5× the 365-day average) and image optimisation (1.9×, led by Clop at 4.65×).

### Cited Findings
**Provenance.** Fetched 2026-10-21T09:40:48Z from the cask endpoints ([365d](https://formulae.brew.sh/api/analytics/cask-install/365d.json), [90d](https://formulae.brew.sh/api/analytics/cask-install/90d.json), [30d](https://formulae.brew.sh/api/analytics/cask-install/30d.json)) and the formula "install-on-request" endpoints ([365d](https://formulae.brew.sh/api/analytics/install-on-request/365d.json), [90d](https://formulae.brew.sh/api/analytics/install-on-request/90d.json), [30d](https://formulae.brew.sh/api/analytics/install-on-request/30d.json)). Windows:
- 365d: 2025-10-21..2026-10-21; casks total 25,355,829 installs over 27,034 casks; formula on-request total 101,156,158 over 89,187 formulas.
- 90d: 2026-07-23..2026-10-21.
- 30d: 2026-09-21..2026-10-21; casks total 2,263,043.
- Scale: the #1 cask (365d) is claude-code with 1,170,837.

**What the counts mean.** Homebrew analytics record install *events* with no user identifier, so the counts are not unique users. Each event is flagged as "installed on request" or "installed as a dependency". Users can opt out with `brew analytics off`, and opted-out users are absent from the data — [Homebrew docs: Analytics](https://docs.brew.sh/Analytics).

**Casks, 365d count (rank), with 30d count — [cask 365d](https://formulae.brew.sh/api/analytics/cask-install/365d.json) / [cask 30d](https://formulae.brew.sh/api/analytics/cask-install/30d.json)**
- **System monitor / display / input:**
  - stats 144,548 (#26), 30d 11,974 (#33)
  - betterdisplay 92,161 (#52)
  - karabiner-elements 58,814
  - linearmouse 45,291
  - logi-options+ 39,941
  - mos 36,519
  - monitorcontrol 32,897
  - scroll-reverser 20,748
  - aldente 18,123
  - mac-mouse-fix 13,347
  - bettertouchtool 12,623
  - istat-menus 8,819
  - coconutbattery 8,248
- **Launcher / clipboard:**
  - raycast 116,455 (#38)
  - maccy 111,014 (#44), 30d 8,659
  - alfred 24,210
  - clipy 11,363
  - copyq 4,014
  - flycut 3,795
  - copyclip 3,580
  - paste 1,247
- **Window management:**
  - rectangle 115,768 (#39)
  - nikitabobko/tap/aerospace 64,826 (#73)
  - alt-tab 63,689 (#74)
  - hammerspoon 54,186
  - amethyst 9,197
  - loop 7,281
  - rectangle-pro 4,103
- **Menu bar managers:**
  - jordanbaird-ice 58,613 (#84)
  - thaw 29,324 (#147); 30d 3,552
  - hiddenbar 29,108 (#150)
  - bartender 8,926
  - Thaw's own cask description is "Menu bar manager" — [Homebrew cask API](https://formulae.brew.sh/api/cask/thaw.json)
- **Keep awake:**
  - caffeine 31,559 (#136)
  - keepingyouawake 14,447 (#258)
  - jiggler 2,212
  - Amphetamine is not in Homebrew. A substring search for "amphetamine" found only third-party taps with 1–5 installs.
- **Archives (GUI):**
  - rar 47,166 (#103), but only 2 in the 30d window. The cask is now disabled with reason `fails_gatekeeper_check` — [rar cask JSON](https://formulae.brew.sh/api/cask/rar.json)
  - keka 43,828 (#111); 30d 4,446 (#89)
  - the-unarchiver 23,029 (#173)
  - betterzip 3,118
  - maczip 1,569
- **Screenshot / recording:**
  - obs 49,915
  - shottr 26,410 (#157)
  - flameshot 19,026. Its 30d count is only 31, which suggests a cask move or rename, so treat its trend as unreliable.
  - cleanshot 7,898 (#372)
  - snipaste 6,728
  - macshot 5,930 (30d 1,118)
  - kap 5,905
  - loom 4,677
  - cap 2,727
  - screen-studio 1,692
  - xnapper 352
- **Image editing / optimisation / upscaling:**
  - inkscape 51,123; gimp 43,431; figma 40,711; affinity 13,623
  - upscayl 11,923 (#286); 30d 2,004
  - darktable 9,546
  - imageoptim 7,202 (#392)
  - pika 3,905
  - clop 3,521 (#677); 30d 1,345 (#243)
  - codeforreal1/tap/compresso (video compressor) 1,027
  - xnconvert 663
  - retrobatch 135
  - squash 105
- **Video:**
  - vlc 122,107; iina 70,813
  - handbrake-app 21,425 (#180)
  - downie 6,136; motrix 5,891; jdownloader 5,849
  - losslesscut 5,287
  - makemkv 3,760
  - mediainfo 2,339
  - permute 1,703
  - shutter-encoder 1,389
  - subler 1,282
- **Audio:**
  - blackhole-2ch 111,367
  - audacity 29,535
  - background-music 18,337
  - kid3 4,430
  - xld 4,176
  - musicbrainz-picard 3,802
  - mp3tag 2,142
  - youtube-to-mp3 921
  - mediahuman-audio-converter 740
- **Transcription / dictation (GUI):**
  - handy 15,227 (#246); 30d 3,071 (#134)
  - fluidvoice 11,771
  - wispr-flow 10,627
  - macwhisper 9,671
  - superwhisper 8,682
  - voiceink 8,620
  - opensuperwhisper 8,480
  - typewhisper 3,452
  - buzz 2,463
  - spokenly 1,864
  - Cask descriptions, e.g. Handy "Speech to text application" and FluidVoice "Offline voice-to-text dictation app with AI enhancement" — [handy](https://formulae.brew.sh/api/cask/handy.json), [fluidvoice](https://formulae.brew.sh/api/cask/fluidvoice.json)
- **PDF / documents:**
  - adobe-acrobat-reader 47,647 (#102); calibre 40,360; zotero 17,339
  - skim 14,082
  - pdf-expert 4,106
  - stirling-pdf 2,626
  - pdfsam-basic 1,252
  - pdf-squeezer 878
  - updf 833
- **Disk / app cleanup:**
  - appcleaner 49,483 (#98)
  - pearcleaner 42,850 (#115)
  - gemini (duplicate finder) 24,881 (#166)
  - grandperspective 14,037
  - daisydisk 8,334
  - omnidisksweeper 6,898
  - cleanmymac 4,590
  - disk-drill 3,196
  - dupeguru 2,179
  - disk-inventory-x 1,987
- **File shelves:**
  - Dropover is not in Homebrew.
  - iordv/tap/droppy 1,405
  - yoink 666
  - dropzone 626
  - droppy 512
  - dropshelf 194
- **File transfer and Finder helpers:**
  - macfuse 131,227; localsend 67,036 (#70)
  - qlmarkdown 33,667 (#128)
  - cyberduck 31,525
  - dockdoor 31,901
  - easydict 9,876
  - syntax-highlight 7,933
  - marta 7,314
  - deepl 5,666
  - openinterminal 4,944, plus openinterminal-lite 2,195
  - forklift 4,543
  - qlstephen 3,617
  - suspicious-package 3,352
  - popclip 3,105
  - qlvideo 2,804
  - trex (OCR) 2,038
  - easyfind 1,304
  - textsniper 1,080
  - find-any-file 1,070
  - a-better-finder-rename 1,036
  - image2icon 573
  - namechanger 509

**Formulas (CLI) installed on request, 365d count (rank) — [formula 365d](https://formulae.brew.sh/api/analytics/install-on-request/365d.json) / [30d](https://formulae.brew.sh/api/analytics/install-on-request/30d.json)**
- **Media:**
  - ffmpeg 1,757,038 (#5)
  - yt-dlp 622,708 (#24)
  - exiftool 139,553
  - openai-whisper 114,525 (#159)
  - libheif 89,676
  - whisper-cpp 86,032 (#214). It has since been renamed whisper.cpp, which shows 18,393 (#92) in the 30d window.
  - mpv 85,847
  - ffmpeg-full 55,885
  - sox 28,204
  - mkvtoolnix 23,111
  - handbrake (CLI) 22,554
  - gifsicle 9,693
  - lame 6,488
  - gifski 2,784
  - whisperkit-cli 2,844
- **Image:**
  - imagemagick 801,629 (#14)
  - librsvg 209,904
  - jpeg-xl 96,292
  - webp 83,058
  - vips 60,167
  - libavif 46,673
  - qrencode (QR *generation*) 41,782
  - graphicsmagick 40,717
  - zbar (barcode/QR *reading*) 22,635
  - potrace 21,581
  - pngquant 17,717
  - resvg 17,558
  - oxipng 8,070
  - jpegoptim 4,991
  - svgo 2,711
  - exiv2 2,480
- **OCR / PDF / documents:**
  - poppler 660,852 (#21)
  - pandoc 400,369 (#45)
  - tesseract 256,667 (#78)
  - ghostscript 186,937 (#109)
  - tesseract-lang 65,605
  - ocrmypdf 63,719 (#285)
  - qpdf 62,894 (#291); 30d 10,900 (#152)
  - weasyprint 20,888
  - mupdf 12,093
  - pdfcpu 4,902
  - pdftk-java 4,723
  - img2pdf 4,715
- **Archives:**
  - xz 577,283
  - gnu-tar 302,712
  - zstd 118,176
  - p7zip 112,105 (#164)
  - sevenzip 98,536 (#184); 30d 17,454 (#97)
  - unar 51,691 (#340)
  - unzip 42,581
- **Disk / files:**
  - mole 320,224 (#61), plus the older tw93/tap/mole 85,355 (#217). In the 30d window, mole is #21 with 57,319. Its formula description is "Deep clean and optimize your Mac", homepage mole.fit — [mole formula JSON](https://formulae.brew.sh/api/formula/mole.json)
  - ncdu 43,417
  - dust 32,382
  - trash-cli 19,990
  - duti 19,682
  - md5sha1sum 10,396
  - rename 8,489
  - fdupes 7,568
  - czkawka 6,274
  - jdupes 3,063
  - b3sum 1,692
- **Not found in core analytics:** rembg, Real-ESRGAN (no core formula), Dropover, Amphetamine, Pixelmator Pro and Magnet (Mac App Store distribution) — [cask 365d](https://formulae.brew.sh/api/analytics/cask-install/365d.json)

**Category sums and trend (arithmetic on the cask JSON above).** The trend ratio is (30d × 365/30) ÷ 365d. A ratio above 1 means the last month ran faster than the yearly average.

| Category (my grouping) | 365d | 30d | Trend ratio |
| --- | ---: | ---: | ---: |
| Window management | 267,910 | 20,403 | 0.93 |
| Disk/app cleanup (casks only; excludes the Mole CLI) | 158,435 | 11,392 | 0.87 |
| Clipboard (excl. Raycast/Alfred) | 138,769 | 10,097 | 0.89 |
| Menu-bar managers | 126,920 | 11,646 | 1.12 |
| GUI archivers | 119,088 | 6,468 | 0.66 (distorted by the disabled rar cask) |
| Transcription/dictation | 80,857 | 9,972 | **1.50** |
| Screenshot/recording | 78,363 | 5,917 | 0.92 |
| PDF | 71,424 | 3,603 | 0.61 |
| Finder helpers (Quick Look/Terminal) | 56,349 | 4,664 | 1.01 |
| Video convert/download | 52,459 | 4,459 | 1.03 |
| Keep-awake | 48,218 | 4,471 | 1.13 |
| Image optimise/upscale | 28,515 | 4,402 | **1.88** |
| File shelves | 3,403 | 510 | 1.82 (tiny base) |

**Per-item trend ratios (arithmetic)**
- Rising casks:
  - clop 4.65
  - handy 2.45
  - macshot 2.29
  - upscayl 2.04
  - wispr-flow 1.68
  - fluidvoice 1.62
  - losslesscut 1.56
  - thaw 1.47
  - voiceink 1.46
  - hiddenbar 1.35
  - keepingyouawake 1.25
  - keka 1.23
  - localsend 1.23
  - shottr 1.22
- Falling casks:
  - gemini 0.47
  - permute 0.67
  - cleanshot 0.82
  - jordanbaird-ice 0.86
  - raycast 0.87
- Rising formulas:
  - gifsicle 2.96
  - trash-cli 2.59
  - mole 2.18 (part of this is the tap-to-core move)
  - sevenzip 2.16 (replacing p7zip, 0.88)
  - qpdf 2.11
  - dust 1.61
  - gifski 1.58
  - zbar 1.52
  - vips 1.52
  - lame 1.46
  - libheif 1.23
- Falling formulas:
  - tesseract 0.63
  - webp 0.67
  - ffmpeg 1.06 (flat)
- Source for all ratios — [cask 30d](https://formulae.brew.sh/api/analytics/cask-install/30d.json), [formula 30d](https://formulae.brew.sh/api/analytics/install-on-request/30d.json)

### Inferences
- Among file tasks Swivel does not yet cover, these are explicitly installed in volume:
  - multi-format extraction (sevenzip + p7zip ≈211k, unar 51,691, keka 43,828, the-unarchiver 23,029)
  - speech-to-text (Whisper formulas ≈219k, about ten GUI casks totalling ≈81k and rising)
  - QR *generation* (qrencode 41,782 vs zbar 22,635 for reading)
  - Mac cleanup (Mole ≈405k; AppCleaner and Pearcleaner ≈92k combined)
  - GIF tooling (gifsicle and gifski both rising)
- PDF password and repair CLIs (qpdf) are growing fast. That fits "unlock/encrypt PDF" as a PDF gap, since Swivel already merges, splits and compresses.
- The Clop and Upscayl growth suggests demand is shifting from "compress by quality" to "convert to modern formats, fit under a size cap, upscale" (see Q4 for Clop's features).
- Window managers and clipboard managers are the largest GUI categories, but they need Accessibility permission (window management) or are saturated by Raycast and Maccy. They fit poorly with Swivel's no-permission, drag-first design.
- Ffmpeg, ImageMagick and poppler are partly installed by developers for pipelines. Their size overstates end-user GUI demand.

### Gaps
- Homebrew does not publish unique-user counts or non-developer reach, and Mac App Store-only apps (Amphetamine, Dropover, Magnet, Pixelmator Pro, PopClip MAS build) are invisible here.
- Some trend ratios are distorted by renames or moves: whisper-cpp to whisper.cpp, tw93/tap/mole to mole, the disabled rar cask, and probably flameshot.
- Background removal (rembg) and AI upscaling CLIs have no core formula, so their CLI demand could not be measured.

## Q2. Raycast Store install counts for file, image, PDF, media and small-utility extensions

### Takeaway
Among 3,437 Raycast extensions, the small utilities with the most installs are keep-awake (Coffee 155,602, #15 overall), Speedtest (188,851), System Monitor (123,233), CleanShot X control (122,403), Video Downloader (137,939), Downloads Manager (86,618) and Clean Keyboard (81,268). The most-installed file-processing extensions are Image Modification (71,961), ScreenOCR (62,699), TinyPNG (46,768), QR Code Generator (38,554), App Cleaner (37,297), Media Converter/FFmpeg (29,107) and Dropover (28,722). PDF, archive, checksum and rename extensions are an order of magnitude smaller (about 1k–9k).

### Cited Findings
**Provenance.** Fetched 2026-10-21T09:45:35Z from the public listing endpoint `https://backend.raycast.com/api/v1/store_listings?page=N&per_page=100` (35 pages). Results: 3,437 listings with a combined 14,791,714 downloads. The field is `download_count`, and every listing carries a `store_url` — [Raycast store listings API](https://backend.raycast.com/api/v1/store_listings?page=1&per_page=100). Raycast's built-in features (Clipboard History, Window Management, Snippets) are not store extensions and are not counted.

**Overall leaders, for scale**
- Google Chrome 577,456 (#1) — [store](https://www.raycast.com/Codely/google-chrome)
- Spotify Player 496,869 — [store](https://www.raycast.com/mattisssa/spotify-player)
- Brew 299,397 — [store](https://www.raycast.com/nhojb/brew)

**Small utilities**
- Speedtest 188,851 (#12) — [store](https://www.raycast.com/tonka3000/speedtest)
- **Coffee ("Prevent the sleep function on your computer") 155,602 (#15)** — [store](https://www.raycast.com/mooxl/coffee)
- Video Downloader 137,939 (#17) — [store](https://www.raycast.com/vimtor/video-downloader)
- System Monitor 123,233 (#20) — [store](https://www.raycast.com/hossammourad/raycast-system-monitor)
- CleanShot X 122,403 (#21) — [store](https://www.raycast.com/Aayush9029/cleanshotx)
- Downloads Manager ("Search and organize your downloads") 86,618 (#33) — [store](https://www.raycast.com/thomas/downloads-manager)
- Clean Keyboard 81,268 — [store](https://www.raycast.com/ike-gg/clean-keyboard)
- Set Audio Device 77,940 — [store](https://www.raycast.com/benvp/audio-device)
- Port Manager 63,640 — [store](https://www.raycast.com/lucaschultz/port-manager)
- Toothpick (Bluetooth) 60,154 — [store](https://www.raycast.com/VladCuciureanu/toothpick)
- Quit Applications 52,285 — [store](https://www.raycast.com/mackopes/quit-applications)
- Amphetamine control 33,805 — [store](https://www.raycast.com/gstvds/amphetamine)
- Battery Health 22,852 — [store](https://www.raycast.com/o1y/battery-health)
- Single Disk Eject 9,985 — [store](https://www.raycast.com/johndturn/single-disk-eject)
- Sleep Timer 2,955 — [store](https://www.raycast.com/dtmzr/sleep-timer)
- Doorstopper (lid-closed awake) 1,793 — [store](https://www.raycast.com/roele/doorstopper)

**Image / screenshot / OCR**
- Image Modification ("Apply filters and transformations to various image formats, and convert between them", sips-based) 71,961 (#40) — [store](https://www.raycast.com/HelloImSteven/sips)
- ScreenOCR ("Extract text from screen captures and clipboard images locally via OCR") 62,699 (#47) — [store](https://www.raycast.com/huzef44/screenocr)
- TinyPNG ("Compress the selected images in Finder") 46,768 — [store](https://www.raycast.com/kawamataryo/tinypng)
- Screenshot 31,999 — [store](https://www.raycast.com/Aayush9029/screenshot)
- Remove Background (remove.bg) 17,095 — [store](https://www.raycast.com/maantje/remove-background)
- Shottr 14,372 — [store](https://www.raycast.com/fernando_barrios/shottr)
- Easy OCR (Tesseract) 12,518 — [store](https://www.raycast.com/Rafo94/easy-ocr)
- Remove Background – Powered by Mac (Vision API) 11,432 — [store](https://www.raycast.com/xmorse/remove-background-powered-by-mac)
- Capture Fullpage of Website 4,890 — [store](https://www.raycast.com/Coun1er/capture-fullpage-of-website)
- Zipic 4,086 — [store](https://www.raycast.com/okooo5km/zipic)
- SVGO 3,838 — [store](https://www.raycast.com/kaichi/raycast-svgo)
- ImageOptim 2,819 — [store](https://www.raycast.com/marckohlbrugge/imageoptim)
- Exif Viewer 1,469 — [store](https://www.raycast.com/j3lte/exif)
- Rename Images with AI 1,285 — [store](https://www.raycast.com/husnainfareed/rename-images-with-ai)

**QR / colour**
- QR Code Generator 38,554 vs QR Code Scanner 18,674. The scanner needs Screen Recording permission per its description — [generator](https://www.raycast.com/Melvynx/qrcode-generator), [scanner](https://www.raycast.com/StevenRCE0/qr-code-scanner)
- Color Casket 9,051 — [store](https://www.raycast.com/slavarazum/color-casket)
- ColorSlurp 7,169 — [store](https://www.raycast.com/Jordan-Ellis/colorslurp)

**Media**
- Media Converter ("converts video, image, and audio files using FFmpeg") 29,107 — [store](https://www.raycast.com/leandro.maia/media-converter)
- YAFW (ffmpeg video compression) 5,765 — [store](https://www.raycast.com/pablopunk/yafw)
- FFmpeg – View, Analyze and Manipulate 3,916 — [store](https://www.raycast.com/RenderCoder/ffmpeg)
- Compresto 3,660 — [store](https://www.raycast.com/hieudinh/compressx)
- GIF Maker 89 — [store](https://www.raycast.com/tristan_gabl/gif-maker)

**Transcription / translation**
- Deepcast (DeepL) 63,487 — [store](https://www.raycast.com/mooxl/deepcast)
- Easy Dictionary 40,621 — [store](https://www.raycast.com/isfeng/easydict)
- iTranslate (with OCR) 11,680 — [store](https://www.raycast.com/JinShi/itranslate)
- Superwhisper 10,376 — [store](https://www.raycast.com/nchudleigh/superwhisper)
- Whisper Dictation 5,733 — [store](https://www.raycast.com/finjo/whisper-dictation)
- Fetch YouTube Transcript 4,388 — [store](https://www.raycast.com/apoorv_khandelwal/fetch-youtube-transcript)

**Files / Finder**
- Folder Search 52,414 — [store](https://www.raycast.com/GastroGeek/folder-search)
- App Cleaner (wraps AppCleaner/Pearcleaner) 37,297 — [store](https://www.raycast.com/dziad/appcleaner)
- File Manager 33,312 — [store](https://www.raycast.com/erics118/file-manager)
- Dropover ("Add selected files or clipboard content to Dropover") 28,722 — [store](https://www.raycast.com/jag-k/dropover)
- Terminal Finder (open Finder window in Terminal) 25,445 — [store](https://www.raycast.com/yedongze/terminalfinder)
- Copy Path 23,534 — [store](https://www.raycast.com/koinzhang/copy-path)
- Mole (Mac cleanup) 22,507 — [store](https://www.raycast.com/jlrochin/mole)
- Easy New File ("Quickly create file in the open Finder window") 14,456 — [store](https://www.raycast.com/koinzhang/easy-new-file)
- Renaming 7,125 — [store](https://www.raycast.com/theo_daguier/renaming)
- LocalSend 2,424 — [store](https://www.raycast.com/kud/localsend)
- Yoink 1,291 — [store](https://www.raycast.com/koinzhang/yoink)
- Checksum 1,080 — [store](https://www.raycast.com/Ek217/checksum)
- Deduplicator 563 — [store](https://www.raycast.com/HerbertLu/deduplicator)
- Quick AirDrop 322 — [store](https://www.raycast.com/ridemountainpig/quick-airdrop)

**PDF / archives**
- PDF Tools ("all-in-one PDF toolkit") 9,116 — [store](https://www.raycast.com/xilopaint/pdf-tools)
- Compress PDF 6,515 — [store](https://www.raycast.com/felixhaeberle/compress-pdf)
- Archiver 3,175 — [store](https://www.raycast.com/JinShi/archiver)
- PDF Compression (PDFKit) 2,161 — [store](https://www.raycast.com/birkhoff/pdf-compression)
- Office2PDF 1,331 — [store](https://www.raycast.com/ARui/office2pdf)
- BetterZip 1,220 — [store](https://www.raycast.com/koinzhang/betterzip)
- Keka 1,019 — [store](https://www.raycast.com/aone/keka)

**Clipboard / encoding**
- Base64 42,960 — [store](https://www.raycast.com/DanielSinclair/base64)
- Paste as Plain Text 27,353 — [store](https://www.raycast.com/koinzhang/paste-as-plain-text)

### Inferences
- Keep-awake is the clearest small-utility signal that Swivel lacks. It ranks #15 of 3,437 on Raycast, and Homebrew Caffeine plus KeepingYouAwake add ≈46k installs a year. The common needs are "while a file copies or downloads" and "while the lid is closed" (see Q3).
- Downloads Manager (86,618), Folder Search, Copy Path, Terminal Finder and Easy New File show steady demand for small Finder gaps. Swivel already covers copy path and Terminal. "New file here" and "recent downloads" are not drag gestures but could fit Quick Actions.
- QR generation outdraws scanning about 2:1 on both Raycast and Homebrew. Swivel's QR tool is scan-first, so text/URL/clipboard → QR deserves equal prominence.
- Image tasks dominate file processing (Image Modification, TinyPNG, OCR). Background *removal* draws ≈28.5k installs across two extensions, even though macOS now ships a Finder Remove Background action (Q4).
- PDF and archive extensions are small on Raycast. These users likely rely on Preview, Keka or The Unarchiver, so low Raycast counts are not evidence of low general demand (compare the Ask Different views in Q3).

### Gaps
- `download_count` has no time window in the API, so I assume it is lifetime cumulative; the data cannot separate recent growth from age.
- Raycast's built-in commands (Clipboard History, Window Management, Snippets) have no public usage counts.

## Q3. Ask Different (apple.stackexchange.com) question view counts by task

### Takeaway
By cumulative views, the biggest Mac how-to demands are:
- combining images into a PDF (1.50M views)
- recording the screen with sound (439k)
- adding images to or splitting PDFs (335k / 306k)
- full-page screenshots (261k)
- unpacking .7z (257k)
- keeping the Mac awake (224k, plus several 50–80k variants)
- showing hidden files (202k)
- Finder "cut" (201k)
- redacting screenshots (185k)
- zips without .DS_Store/__MACOSX (159k)
- MKV metadata (157k)
- GIF screen capture (154k)
- SHA-256 checksums (153k)
- split zips (147k)

Per year, the strongest still-current signals are images→PDF (≈97k/yr), add images to PDF (≈49k/yr), screen + sound recording (≈40k/yr), .7z (≈29k/yr), the notch hiding menu-bar icons (≈20k/yr), split PDF and full-page screenshot (≈19–20k/yr), and keep awake (≈16k/yr).

### Cited Findings
**Provenance.** Fetched 2026-10-21T09:42:47Z via `api.stackexchange.com/2.3/search/advanced?site=apple&sort=votes` (free-text `q=` and `title=` queries, no key). View counts are cumulative since creation. "/yr" figures are my arithmetic to 2026-10-21. The search ranks by votes, so a high-view question with few votes can be missed.

**Ranked by views (question date in parentheses)**
- 1,502,718 — "How do I combine two or more images to get a single pdf file?" (2011; ≈96.6k/yr) — [q/11163](https://apple.stackexchange.com/q/11163)
- 438,757 — record screen and sound with QuickTime (2015; ≈39.9k/yr) — [q/212829](https://apple.stackexchange.com/q/212829); plus 93,333 "record screen with the audio coming from your Mac" (2019) — [q/371683](https://apple.stackexchange.com/q/371683)
- 334,860 — "Add images to existing PDF with Preview" (2020; ≈49.3k/yr) — [q/378888](https://apple.stackexchange.com/q/378888)
- 306,304 — "Split a single page PDF into multiple pages" (2011; ≈19.7k/yr) — [q/12305](https://apple.stackexchange.com/q/12305)
- 260,754 — full page screenshot in Safari (2013; ≈19.0k/yr) — [q/80434](https://apple.stackexchange.com/q/80434); the "scrolling screenshot" question has 3,786 (2025) — [q/480061](https://apple.stackexchange.com/q/480061)
- 256,847 — "How can I unpack .7z files via MacOS terminal?" (2017; ≈28.9k/yr) — [q/307377](https://apple.stackexchange.com/q/307377)
- Related archive questions:
  - 43,414 "Preview an archive contents without extracting it" (2019) — [q/364706](https://apple.stackexchange.com/q/364706)
  - 36,918 "Tool for creating .rar files" — [q/2674](https://apple.stackexchange.com/q/2674)
  - 26,886 "Mac software to handle archives (zip, rar, 7z, tar) that allows browsing" — [q/16872](https://apple.stackexchange.com/q/16872)
- 224,368 — "How can I keep my Mac awake AND locked?" (2012; ≈16.2k/yr) — [q/76107](https://apple.stackexchange.com/q/76107). Related:
  - 81,473 prevent sleep with lid closed — [q/361384](https://apple.stackexchange.com/q/361384)
  - 79,964 prevent sleep during a Time Machine backup — [q/88677](https://apple.stackexchange.com/q/88677)
  - 73,016 prevent sleep while media plays — [q/74123](https://apple.stackexchange.com/q/74123)
  - 53,826 caffeinate with lid closed on battery — [q/219885](https://apple.stackexchange.com/q/219885)
  - 48,526 prevent sleep from command line — [q/62558](https://apple.stackexchange.com/q/62558)
  - 34,910 find what prevents sleep — [q/364622](https://apple.stackexchange.com/q/364622)
  - 22,213 prevent sleep during a file copy (2021) — [q/414134](https://apple.stackexchange.com/q/414134)
- 201,790 — hotkey to show hidden files in Open dialog — [q/186376](https://apple.stackexchange.com/q/186376); plus 86,790 show hidden files in Finder — [q/5870](https://apple.stackexchange.com/q/5870) and 63,074 hidden files in Finder search — [q/302954](https://apple.stackexchange.com/q/302954)
- 201,414 — why no "cut" for files in Finder (2011) — [q/12391](https://apple.stackexchange.com/q/12391)
- 185,207 — export Preview signature to another Mac — [q/74950](https://apple.stackexchange.com/q/74950); 33,066 signature blocked by owner password — [q/441130](https://apple.stackexchange.com/q/441130)
- 184,903 — "Tool to quickly hide/blur/redact text in screenshot?" — [q/72971](https://apple.stackexchange.com/q/72971)
- 175,058 — multipage PDF from JPGs in Preview — [q/28795](https://apple.stackexchange.com/q/28795)
- 170,631 — disk space not freed after emptying Trash — [q/44203](https://apple.stackexchange.com/q/44203). Related:
  - 109,495 "quickly find large files and folders" — [q/118167](https://apple.stackexchange.com/q/118167)
  - 55,747 actual free space — [q/324440](https://apple.stackexchange.com/q/324440)
  - 50,796 "combined file size of selected files in Finder" — [q/43983](https://apple.stackexchange.com/q/43983)
  - 32,012 size of multiple folders — [q/33305](https://apple.stackexchange.com/q/33305)
- 158,942 — "compress a Zip archive without .DS_Store and __MACOSX" (2016; ≈15.3k/yr) — [q/239578](https://apple.stackexchange.com/q/239578)
- 146,684 — "compress a folder into multiple zip files" — [q/12371](https://apple.stackexchange.com/q/12371)
- Password-protected zips for Mac+PC: 9,364 — [q/296332](https://apple.stackexchange.com/q/296332); 2,119 — [q/40570](https://apple.stackexchange.com/q/40570)
- 156,984 — edit MKV metadata without re-encoding (2015; ≈14.5k/yr) — [q/219699](https://apple.stackexchange.com/q/219699); 26,744 remove "Where from" info from a downloaded file — [q/368338](https://apple.stackexchange.com/q/368338)
- 153,632 — "Mac screen capture tool that will export to animated gif?" — [q/46036](https://apple.stackexchange.com/q/46036). Related:
  - 46,904 .mov → .gif/.apng — [q/24526](https://apple.stackexchange.com/q/24526)
  - 32,163 make GIF smaller — [q/351578](https://apple.stackexchange.com/q/351578)
  - 27,781 GIF → MP4 — [q/103473](https://apple.stackexchange.com/q/103473)
- 153,259 — "Need a cli to check the sha256 hash of a file" (2016; ≈14.5k/yr) — [q/230917](https://apple.stackexchange.com/q/230917). Related:
  - 34,623 md5sum --check equivalent (2023) — [q/453204](https://apple.stackexchange.com/q/453204)
  - 21,867 MD5 on multiple files — [q/30507](https://apple.stackexchange.com/q/30507)
  - 20,381 verify checksum of a download — [q/388965](https://apple.stackexchange.com/q/388965)
- 139,790 — combine multiple PDFs via command line — [q/230437](https://apple.stackexchange.com/q/230437); 90,126 combine two PDFs in Preview — [q/812](https://apple.stackexchange.com/q/812)
- 138,295 — convert audio files without iTunes — [q/26099](https://apple.stackexchange.com/q/26099). Related:
  - 26,204 FLAC→MP3 — [q/9064](https://apple.stackexchange.com/q/9064)
  - 11,498 WAV→MP3 with LAME — [q/22803](https://apple.stackexchange.com/q/22803)
  - 64,269 "Can the finder edit mp3 sound file tags" — [q/21099](https://apple.stackexchange.com/q/21099)
- 134,584 — "Convert .mov to .mp4" — [q/232910](https://apple.stackexchange.com/q/232910); 67,553 "screen recording and get a small file" — [q/187399](https://apple.stackexchange.com/q/187399)
- 113,605 — "decrease .pdf size without losing quality" — [q/297417](https://apple.stackexchange.com/q/297417); 34,283 compress PDF with adjustable ratio — [q/33702](https://apple.stackexchange.com/q/33702)
- 103,526 — HEIF/HEIC → JPEG — [q/297134](https://apple.stackexchange.com/q/297134); 95,381 force AirDrop to send JPEG — [q/374556](https://apple.stackexchange.com/q/374556); 20,852 batch HEIC → PNG/JPG — [q/416220](https://apple.stackexchange.com/q/416220)
- 103,218 — rename multiple files at once — [q/64497](https://apple.stackexchange.com/q/64497)
- 102,013 — copy current directory path — [q/47216](https://apple.stackexchange.com/q/47216); 68,653 path as string — [q/252171](https://apple.stackexchange.com/q/252171); 13,800 shortcut to open Terminal at the Finder location — [q/359729](https://apple.stackexchange.com/q/359729)
- 85,022 — "prevent MacBook Pro notch from hiding menu bar icons" (2022; ≈20.0k/yr) — [q/444113](https://apple.stackexchange.com/q/444113)
- 70,025 — extract audio from a video file — [q/13221](https://apple.stackexchange.com/q/13221)
- 61,365 — native tools for SVG conversion — [q/25012](https://apple.stackexchange.com/q/25012)
- 54,465 — make an existing PDF searchable (OCR) — [q/76471](https://apple.stackexchange.com/q/76471); 38,474 "Built-in OCR in macOS?" (2022) — [q/437910](https://apple.stackexchange.com/q/437910); 9,181 screenshot to OCR — [q/244557](https://apple.stackexchange.com/q/244557)
- 43,510 — custom right-click context menu items — [q/104021](https://apple.stackexchange.com/q/104021)
- 39,933 — quickest app to remove an image background to transparent (2011) — [q/14146](https://apple.stackexchange.com/q/14146)
- 30,597 — WebP support on macOS — [q/285698](https://apple.stackexchange.com/q/285698)
- 29,943 — screenshot to clipboard by default — [q/365500](https://apple.stackexchange.com/q/365500)
- 26,836 — password-less version of a protected PDF — [q/42222](https://apple.stackexchange.com/q/42222); 8,827 remove a PDF password I know (2023) — [q/463745](https://apple.stackexchange.com/q/463745)
- 20,656 — change EXIF of multiple images in one click — [q/39418](https://apple.stackexchange.com/q/39418)
- 19,012 — exact colour value at a point on a PNG — [q/26719](https://apple.stackexchange.com/q/26719); 22,641 colour-picker swatch removal — [q/248237](https://apple.stackexchange.com/q/248237)
- Low-view topics:
  - batch resize 100,000 JPEGs 7,648 — [q/41520](https://apple.stackexchange.com/q/41520)
  - transcribe a Voice Memo 6,727 — [q/126959](https://apple.stackexchange.com/q/126959)
  - duplicates via APFS clones 4,937 — [q/316435](https://apple.stackexchange.com/q/316435)
  - delete all duplicate files 2,946 — [q/448507](https://apple.stackexchange.com/q/448507)
  - .icns creation, top question only 5,884 — [q/112313](https://apple.stackexchange.com/q/112313)

### Inferences
Mapped to Swivel's current tools:
- **Already covered:**
  - images→PDF (if Swivel's documents↔PDF accepts several images into one PDF)
  - PDF merge/split/compress
  - HEIC→JPG
  - redact
  - batch rename
  - copy path
  - Terminal
  - move (the "cut" workaround)
  - OCR from images
- **Clear gaps with high views:**
  - .7z/RAR extraction and archive browsing (Swivel does only ZIP/TAR)
  - clean zips without .DS_Store/__MACOSX, split zips and password zips
  - keep awake (timed, during a copy, lid closed)
  - video→GIF
  - audio→MP3
  - checksum (SHA-256/MD5, including verify against a pasted hash)
  - searchable-PDF OCR (OCR into the PDF, not just text out)
  - PDF password remove/add
  - folder/selection size and large-file finding
  - metadata editing beyond stripping (MKV titles, EXIF dates, the "Where from"/quarantine attribute)
- **Feasibility notes (inference, not verified):**
  - macOS's bundled libarchive/`bsdtar` may extract 7z and some RAR without new dependencies.
  - Checksums can use CryptoKit.
  - Keep-awake can use IOPMAssertion without special permissions.
  - Full-page or scrolling screenshots of other apps would need Screen Recording permission, which conflicts with Swivel's no-permission principle.
- **Not a file gesture:** the notch/menu-bar question (≈20k views/yr) and the hidden-files toggle (>350k combined). They could be settings toggles, but the hidden-files toggle already has a system shortcut (⌘⇧.).

### Gaps
- The Stack Exchange API cannot sort search results by views, so the vote-sorted searches may miss some high-view questions.
- Views are cumulative and dominated by 2010–2016 questions. Newer tasks (local AI transcription, background removal, upscaling) are under-represented because the questions were asked before these tools existed or are now answered elsewhere.
- No Google search-volume data was gathered here; another researcher's notes (`online_tool_traffic.md`) may cover web search and online-tool traffic.

## Q4. Feature sets of successful Mac utilities (commercial or open source) and the built-in baseline

### Takeaway
The leading paid utilities sell **one-step outcomes on drop or capture**:
- **CleanShot X:** annotate, backgrounds, a quick-access overlay, scrolling capture, GIF/MP4 recording, cloud links, OCR/QR.
- **Clop:** automatic optimisation on copy or drop, "fit under size", modern formats (JPEG XL/WebP/AVIF/HEVC), PDF compression, pipelines and watched folders.
- **Dropover:** shake-to-open shelf, instant actions (resize, extract text, ZIP), cloud share links.
- **Permute:** 100+ formats, target file size, merge/split, ringtone, OCR, transcription to SRT.
- **Keka:** creates 14 archive formats and extracts 32 (incl. RAR), AES-256 7z, split volumes.
- **Amphetamine:** sessions by time, app or download, rich triggers, closed-lid mode.

macOS itself now ships Finder Quick Actions for Rotate, Markup, Trim, Create PDF, Convert Image and Remove Background, which raises the baseline.

### Cited Findings
**CleanShot X — [cleanshot.com/features](https://cleanshot.com/features)**
- Headline features in page order:
  - annotate (blur/pixelate/arrows/counter)
  - background tool with presets
  - Quick Access Overlay for copy/save/drag-out after capture
  - area/window/fullscreen/**scrolling** capture with self-timer
  - screen recording to MP4 or **GIF** with mic and computer audio, plus click/keystroke overlays
  - video editor with smart zooms
  - Cloud links (with transcription, passwords, expiry)
  - floating pinned screenshots
  - **on-device OCR and QR reading in 30+ languages**
  - All-In-One mode
  - one month of capture history
- No price on that page; 30-day money-back guarantee.

**Shottr — [shottr.cc](https://shottr.cc/)**
- Features in page order:
  - lightweight (17 ms capture claim)
  - backgrounds
  - **scrolling screenshots**
  - pixelate/remove objects
  - **OCR & QR**
  - combine screenshots
  - resize
  - pin
  - markup
  - overlay/before-after
  - screen ruler
  - dedicated screenshot folder
  - magnifier
  - **colour picker (TAB)**
- Pricing is "pay what you want", including $0.

**Clop — [lowtechguys.com/clop](https://lowtechguys.com/clop/)**
- Images:
  - optimise automatically on copy to clipboard
  - drop zone (⌘-drop for stronger compression)
  - downscale and crop
- Video:
  - optimise screen recordings when recording stops
  - crop, **convert to GIF**, change speed, mute, encode
  - uses the Apple Silicon Media Engine
- PDF: optimisation (claims up to 80%).
- Audio:
  - WAV/AIFF/FLAC → smaller formats
  - loudness normalisation (−14/−16 LUFS)
- Formats and size:
  - converts to **JPEG XL, WebP, AVIF, AV1, WEBM, HEVC**
  - **"Fit under size"** target (e.g. 8 MB or 25 MB)
- Automation:
  - pipelines
  - per-folder automatic conversion
  - watched folders
  - shelf integrations (Yoink, Dropover)
  - CLI and an MIT-licensed SDK
  - an MCP server for AI agents (Pro)
- Privacy: on-device processing.
- Price: Pro €15 lifetime; free tier limited to 5 files per session for some features.

**Dropover — [dropoverapp.com](https://dropoverapp.com/)**
- Shelf:
  - shake-to-open shelf for files, links, text and images
  - **Instant Actions** that run when files are dropped
- Built-in actions:
  - **resize images, extract text, create ZIP archives**
  - share via AirDrop/Mail/Messages
- Cloud: free anonymous link uploads with password and expiry; iCloud/S3/Drive/OneDrive/Imgur.
- Integrations:
  - custom scripts
  - watched folders
  - notch/menu-bar drops
  - Raycast/Alfred extensions
- Ratings: **4.9 / 5 from about 7.7K Mac App Store ratings**, per its own site.

**Permute — [software.charliemonroe.net/permute](https://software.charliemonroe.net/permute/)**
- Formats: "100+ formats" (video, audio, image, PDF, text), incl. HEVC/ProRes/AV1/MP3/FLAC/HEIC/AVIF/WebP.
- Editing:
  - trim, crop, resize, rotate
  - **set a target file size**
  - merge, split by time or CUE
  - extract tracks without re-encoding
- Smart tools:
  - text-to-audio
  - **OCR**
  - **iPhone ringtone creation**
  - slideshows
  - frame export
- On-device AI **transcription to text/SRT**.
- Other: PDF pages → images, metadata editing.
- Price: $14.99 one-time; also on Setapp.

**Keka — [keka.io](https://www.keka.io/en/)**
- Creates 14 formats: 7Z, ZIP, TAR, GZIP, BZIP2, XZ, LZIP, DMG, ISO, BROTLI, ZSTD, LRZIP, AAR, WIM.
- **Extracts 32**, incl. **RAR, 7Z, ZIPX, EXE, CAB, MSI, ISO, DMG**.
- **AES-256 7z passwords**, ZIP legacy encryption.
- **Split volumes**.
- Drag onto the Dock icon to compress.

**Amphetamine — [Mac App Store](https://apps.apple.com/us/app/amphetamine/id937984704?mt=12)**
- Free, no ads or pro tier.
- Sessions:
  - indefinite, for a duration, or until a time
  - **while a file downloads**
  - **while an app runs**
- Options: allow/prevent display sleep, screen saver and closed-display sleep, plus mouse movement and screen lock.
- Triggers: external display, USB/Bluetooth device, app, power/battery, Wi-Fi, VPN, mounted drive, CPU, idle.
- **Closed-lid mode**, which on Apple Silicon requires an installed script and admin authentication.
- The App Store page fetched did not display a rating count.

**MacWhisper** (third-party summaries; the developer page could not be fetched)
- Transcribes audio and video files on-device.
- Exports **SRT/VTT**, CSV, DOCX, PDF, HTML.
- Pro adds batch folder transcription, YouTube URLs and speaker diarization.
- Price:
  - €59 lifetime direct (≈$69 per a competitor page)
  - App Store subscriptions $6.99/mo, $29.99/yr or $99.99 lifetime
- Sources: [getvoibe.com pricing summary](https://www.getvoibe.com/resources/macwhisper-pricing/), [openwhispr comparison](https://openwhispr.com/compare/macwhisper). Competitor-authored, so treat with caution.

**macOS built-in Finder Quick Actions baseline — [Apple Support](https://support.apple.com/guide/mac-help/perform-quick-actions-in-the-finder-on-mac-mchl97ff9142/mac)**
- Rotate Left/Right (images and movies)
- Markup
- **Trim** (movies and audio)
- **Create PDF**
- **Convert Image** (format and size, e.g. HEIC → JPEG/PNG)
- **Remove Background** (images)
- Customize
- The page lists no "encode media" action.

**Mac App Store rating counts via the iTunes Search API** (fetched 2026-10-21T09:49:12Z)
- For most Mac-only apps the API returned `userRatingCount` 0, including Amphetamine, The Unarchiver, Dropover, Yoink, Permute, PDF Squeezer and Magnet.
- Non-zero counts appeared only for some Universal apps:
  - "Resize it – compress any image" 1,103
  - "Video Compressor – resize all" 986
  - "Remove Background – Snip BG" 1,253
  - "RAR Extractor – Unarchiver" 544
  - "Paste – Limitless Clipboard" 1,335
  - Whisper-branded transcription apps 103–659
- Source: [iTunes Search API, Amphetamine query example](https://itunes.apple.com/search?term=Amphetamine&entity=macSoftware&country=us&limit=8)

### Inferences
- Swivel already matches much of this list: image convert/compress/crop/annotate/collage/redact/metadata, OCR, video trim/merge/mute, PDF tools, ZIP/TAR, shelf, screenshot holding area, AirDrop, Terminal and copy path.
- Recurring features in the leaders that Swivel lacks:
  1. **GIF output** from video or screen recordings (CleanShot, Clop)
  2. **target-size compression** ("fit under 8/25 MB": Clop, Permute)
  3. **modern-format output** (AVIF/JPEG XL/WebP/HEVC: Clop, Permute)
  4. **multi-format extraction with RAR/7z, password and split archives** (Keka)
  5. **transcription to text/SRT** (Permute, CleanShot Cloud, MacWhisper)
  6. **keep-awake sessions tied to a file copy, download or app** (Amphetamine)
  7. **audio normalisation and MP3/ringtone output** (Clop, Permute)
  8. **QR generation** alongside reading (CleanShot/Shottr read QR; generation demand from Q1/Q2)
  9. a **colour picker** (Shottr, CleanShot)
  10. **cloud/share links** (CleanShot, Dropover). These need a server, which conflicts with Swivel's local, no-dependency rules — not recommended.
- Because Finder now ships Remove Background, Convert Image, Create PDF and Trim, a Swivel version of these must beat the built-ins: batch processing, chosen output format, side-by-side output and one drop instead of a context menu.

### Gaps
- No reliable Mac App Store rating counts or chart ranks were obtainable for Mac-only utilities (the API returned 0), and the App Store page did not render a rating.
- Setapp per-app install numbers are not public.
- PopClip, Yoink, PDF Squeezer, Aiko, Photoroom and Pixelmator Pro feature pages were not fetched due to the time limit, so feature lists for those are missing.

## Q5. Reddit, surveys and app roundups: which file-utility categories recur most

### Takeaway
Reddit could not be reached (HTTP 403 for the public JSON; the search tool blocks reddit.com), and Setapp's published surveys give no per-category utility usage. Survey evidence is limited to general behaviour: most daily-use apps are pre-installed, the average user has ≈51 apps, and 42% use AI apps daily. Across the measured sources, the recurring small-utility categories are keep-awake, system/menu-bar monitoring and hiding, clipboard, window management, screenshot + OCR, image compress/convert, archives (7z/RAR), app/disk cleanup, transcription and quick file-sharing.

### Cited Findings
- **Reddit:** `reddit.com/r/macapps/search.json` and `/top.json` returned HTTP 403 on 2026-10-21. The web-search tool rejects reddit.com as a domain. A secondary account says an r/macapps "must-have apps" post drew 121k+ views and ~200 replies, but the page could not be fetched (403) — [Medium post (search snippet only)](https://medium.com/p/6add3883c09a)
- **Setapp Mac Apps Report** — [MacTech 2023](https://www.mactech.com/2023/10/16/setapps-mac-apps-report-2023-reveals-42-of-mac-users-embrace-ai-apps/amp/), [MacTech 2022](https://www.mactech.com/2022/12/15/mac-apps-report-out-of-13-apps-used-daily-are-pre-installed-on-mac/amp/), [Setapp 2021](https://setapp.com/news/mac-apps-report-2021):
  - 2023: 1,241 mostly-US Mac users; average 51 installed apps; up to 15 used daily; 42% use AI-based apps daily.
  - 2022: 611 US users; 10 of the 13 apps used daily are pre-installed; 36% prefer one-time purchases.
  - 2021: 462 users.
  - Per-category figures were not in the summaries found.
- **Setapp 2024 Mac Developer Survey:** developers' own app categories are 49% productivity, 33% developer tools, 19% creativity, 15% "Mac hacks" and 14% maintenance. This describes supply (what developers build), not user demand — [Setapp survey PDF (search snippet)](https://cdn.setapp.com/blog/images/mac-developer-survey-2024.pdf)
- **Setapp "most downloaded Mac apps" blog:** promotional, with no install counts. It names Raycast, Magnet, CleanMyMac, iStat Menus and AppCleaner as leading picks — [setapp.com](https://setapp.com/app-reviews/most-downloaded-mac-apps)
- **Setapp "App Download Stats and Trends 2024" infographic:** covers mobile apps only (Instagram, TikTok …), so it is irrelevant to Mac utilities — [Setapp PDF](https://cdn.setapp.com/blog/images/top-apps-2024-infographic.pdf)
- **Labnol "Essential Mac Apps and Utilities – 2025 Edition":** lists AltTab, Magnet/Rectangle and Camo among essentials — [labnol.org](https://labnol.org/software/essential-mac-utilities/9479/) (search snippet only)

### Inferences
Cross-source demand summary for the report writer. Each category below is supported by at least two independent measured sources from Q1–Q3; Swivel coverage is per the brief.
1. **Keep Mac awake.** Not built in Swivel.
   - Raycast Coffee 155,602 (#15 of 3,437) and Amphetamine 33,805
   - Homebrew Caffeine + KeepingYouAwake ≈46k in 365d, trend 1.13
   - Ask Different 224k + 81k + 80k + 73k + 54k + 49k + 35k + 22k views
   - Amphetamine's lead features: while a file downloads or an app runs, closed-lid mode
2. **Archive extraction beyond ZIP/TAR (7z, RAR, browse before extracting, password, split).** Partially built (ZIP/TAR only).
   - Ask Different 257k (.7z), 159k (clean zip), 147k (split zip), 43k (preview contents)
   - Homebrew sevenzip/p7zip ≈211k, unar 51,691, Keka 43,828 (rising, 1.23), The Unarchiver 23,029
   - Keka's feature list
3. **Speech-to-text from audio/video files (TXT/SRT).** Not built.
   - Homebrew Whisper formulas ≈219k; about ten GUI casks ≈81k, trend 1.50; Handy 2.45×
   - Raycast Superwhisper 10,376
   - Permute and CleanShot both added transcription
   - Ask Different views are low because the questions predate Whisper.
4. **Video→GIF and GIF optimisation.** Not built (per the brief, video conversion lacks GIF mention).
   - Ask Different 154k + 47k + 32k + 28k
   - Homebrew gifsicle 2.96× trend, gifski 1.58×
   - CleanShot and Clop lead with GIF
5. **Checksum / hash verify.** Not built.
   - Ask Different 153k + 35k + 22k + 20k
   - Raycast Checksum only 1,080 (developers use the CLI)
   - Moderate, mostly technical audience
6. **Target-size compression and modern formats (AVIF/JPEG XL/WebP/HEVC).** Partial (Swivel compresses by quality).
   - Clop trend 4.65×; Homebrew jpeg-xl 96k, webp 83k, libavif 47k, libheif 90k
   - Raycast TinyPNG 46,768 and Image Modification 71,961
   - Clop's "fit under size" and Permute's target size
7. **MP3/audio conversion and extraction.** Swivel lacks MP3 output.
   - Ask Different 138k (convert audio), 70k (extract audio), 26k (FLAC→MP3), 64k (MP3 tags)
   - Homebrew lame 6,488 (1.46×)
   - macOS `afconvert` cannot encode MP3 (inference; verify)
8. **PDF security and searchable OCR.** Swivel has image OCR and PDF merge/split/compress, but not PDF password, signature or searchable OCR.
   - Ask Different 185k (signature), 54k (searchable OCR), 27k + 9k (remove password)
   - Homebrew qpdf rising 2.11×, ocrmypdf 63,719, tesseract 256,667
9. **App uninstall leftovers and disk usage (folder size, large files).** Not built.
   - Homebrew Mole ≈405k, AppCleaner 49k, Pearcleaner 43k, GrandPerspective 14k
   - Raycast App Cleaner 37k and Mole 22.5k
   - Ask Different 171k + 109k + 56k + 51k + 32k
   - Scanning other apps' containers may hit Full Disk Access limits (inference).
10. **QR generation.** Swivel's library may already allow "New".
    - Raycast generator 38,554 vs scanner 18,674
    - Homebrew qrencode 41,782 vs zbar 22,635
11. **Background removal.** Swivel only pads backgrounds.
    - Raycast 17,095 + 11,432; Ask Different 40k; Universal MAS apps around 1.2k ratings
    - Finder now has a built-in Remove Background, which lowers the marginal value (inference).
12. **Small "system" utilities that bundles like Swivel commonly include.** No file drag involved, so these are lower fit.
    - speed test (Raycast 188,851)
    - system monitor (Stats 144,548 casks; Raycast 123,233)
    - menu-bar hiding (Ice/Thaw/Hidden Bar ≈117k; Ask Different notch 85k)
    - clean-keyboard lock (Raycast 81,268; needs input-event capture, likely an Accessibility/Input Monitoring permission — inference)
    - audio device switch (77,940)
    - Bluetooth (60,154)

**Not recommended despite volume (inference):**
- **Video downloading** (yt-dlp 622,708; Raycast Video Downloader 137,939): needs a third-party engine plus legal/ToS risk.
- **Window management** (≈268k casks): needs Accessibility.
- **Clipboard history** (Maccy 111k plus Raycast built-in): saturated and needs no file input.
- **Full-page or scrolling capture of other apps:** needs Screen Recording.
- **Cloud share links:** needs a server.

### Gaps
- No Reddit thread counts or comment-frequency analysis could be collected: Reddit was blocked by HTTP 403 and the search tool disallows the domain. A writer should not cite Reddit frequencies from these notes.
- No Setapp per-app or per-category install data was found. Setapp's public surveys report general behaviour only.
- Mac App Store chart positions and rating counts for Mac-only utilities were not obtainable through the public API.
