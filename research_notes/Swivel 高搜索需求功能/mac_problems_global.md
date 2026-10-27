# Everyday Mac problems beyond file conversion: global (English-language) demand and how an assistant app could solve each

Research date: 2026-10-21. Scope: system, OS and everyday-use problems that Mac users look up. File-processing demand was covered by the sibling notes and is excluded here (images, PDF, video/audio, archives, background removal, MP3, transcription, keep-awake, checksums).

**Platform context (as of the research date).** macOS 27 "Golden Gate" shipped on 2026-09-14. It is the first release that runs only on Apple silicon ([ithinkdiff](https://www.ithinkdiff.com/macos-27-golden-gate-release-date-compatibility/); [Macworld](https://www.macworld.com/article/3139330/macos-27-golden-gate-has-arrived-heres-what-s-new-on-your-mac.html)). Apple's Mac support hub already links "What's new in macOS 27" ([support.apple.com/mac](https://support.apple.com/mac)). The brief asked for a focus on macOS 15 Sequoia and macOS 26 Tahoe. Most Apple Community threads below are Sequoia or Tahoe threads. Where macOS 27 changes a problem, the row says so. The read-only command checks in this research ran on macOS 26.6.2.

**Evidence labels**
- **(M) Measured, first-party counts.** All fetched 2026-10-21.
  - Ask Different (apple.stackexchange.com) question view counts. These are lifetime totals from the public API (`/2.3/search/advanced`, sort=votes, title match). The creation year is shown because old questions keep accumulating views.
  - Apple Support Community "Me too (N)" counts. Threads show no view counts. "Me too" is the number of users who marked "same problem". Counts on closed threads are frozen.
  - Apple Support YouTube channel view counts (cumulative). The age shown is YouTube's relative upload age.
  - Homebrew install events: 365 days = 2025-10-21 → 2026-10-21; 30 days = 2026-09-21 → 2026-10-21. "Pace" = (30-day count × 365/30) ÷ 365-day count; above 1 means the tool is gaining.
  - Raycast Store cumulative downloads (3,437 listings).
- **(G) Google Ads US average monthly searches, relayed by seodata.dev.** The refresh date shown is per keyword (May–Oct 2026). Google groups close variants into one bucket, so **never add keyword volumes together**: one cluster is one demand pool.
- **(T) Third-party estimates.** seo.ai (global, as of Sep 2024), Glimpse (past 30 days, page updated 2026-02-02), and the MacKeeper study (2021).
- **(I) Inference.** Every demand tier and every solvability class is my inference.

**Source access notes (what failed).** seodata.dev's anonymous API returned "rate_limited" (20 lookups/day without registration; I did not register). Only keyword pages that someone had already looked up could be read. Of 338 guessed or linked slugs, 74 Mac keyword pages were found and 264 returned 404 ("We haven't looked this keyword up yet"). So many basic how-to terms (right-click, copy/paste, hidden files, Wi-Fi password, split screen) have **no measured US volume** here. Google Trends returned HTTP 429 again. Apple Community pages show a human-verification page to curl; WebFetch read them normally. The Stack Exchange keyless quota ended at 69 of 300 remaining.

---

## Q1. Which everyday Mac problems have the most demand, by category and with evidence?

### Takeaway
On measured counts, the largest everyday-Mac demand pools beyond file conversion are:
- **Screenshots and screen recording.** 450K and 110K US searches a month. The unmet part is recording **system audio**: the BlackHole audio driver had 111K Homebrew installs in a year.
- **Frozen apps.** "force quit" is 60.5K a month, and the Ask Different question about the Mac's Ctrl+Alt+Delete has 1.6M views.
- **Uninstalling apps.** 33.1K a month, plus AppCleaner and Pearcleaner at 92K installs a year.
- **Storage.** Many 5.4K-a-month clusters, plus System Data and purgeable space; Apple Community's largest "Me too" count for storage is 3,723.
- **Finding a saved Wi-Fi password.** The single most-viewed problem question on Ask Different, at 1.56M.
- **Window tiling and split screen.** Apple's Split View video has 587K views; Rectangle has 116K installs.
- **Resets.** Apple's factory-reset video has 1.38M views and its login-password reset video 640K.
- **Time Machine.** Up to 756K views on Ask Different.
- **Keychain password prompts.** 748K views.
- **kernel_task, CPU and fan diagnostics.** 619K and 579K views; the Stats monitor has 145K installs.

The second tier is drives and transfers (NTFS 9.9K, Android File Transfer 9.9K, AirDrop 8.1K, iPhone photos 8.1K, external drive not detected 5.4K a month), plus mouse, keyboard and display fixes that people install tools for: BetterDisplay 92K, Ice + Thaw + Hidden Bar ≈117K, LinearMouse + Mos + Scroll Reverser ≈103K, Maccy 111K. Recent Apple Community spikes match Sequoia and Tahoe regressions: Background Items notifications, printing, external drives and displays, battery drain, AirDrop.

### Cited Findings

**Overall "most-asked" context (T)**
- "how to screenshot on mac" is rank 9 among the most-googled questions worldwide, at 603,000 searches. In the same table: "how to screen record on mac" 121,000, "how to reset airpods" 117,000, "how to clear cache" 157,000 (not Mac-specific). The data is global, as of Sep 2024 — [seo.ai](https://seo.ai/blog/what-is-the-most-googled-question)
- Glimpse's "100 most asked questions in 2026" ranks "how to screenshot on mac" #15 at 134,209 (past 30 days) and "what is my ip" #6 at 439,376. No other Mac or computer-use question is on the list. The page was updated 2026-02-02 and states no geography — [Glimpse](https://meetglimpse.com/top-searched/most-searched-questions/)
- A 2021 MacKeeper study of Google and YouTube searches found that MacBook users' two top questions were how to take a screenshot and "how to split screen". No counts were published — [9to5Mac](https://9to5mac.com/2021/10/11/new-study-reveals-the-top-problems-people-face-with-apple-products-in-each-state/)

**Apple Support YouTube: Mac videos by views (M; channel search, 2026-10-21)**
- Erase and reset your Mac to factory settings: 1,375,752 views (2 years old) — [YouTube](https://www.youtube.com/watch?v=5ZK3KD3hmiI)
- iPhone Mirroring on Mac: 1,063,538 (1 year) — [YouTube](https://www.youtube.com/watch?v=f3REBdDPd58)
- Stage Manager: 912,392 (3 years) — [YouTube](https://www.youtube.com/watch?v=B7t_BCmY-lg)
- Reset your Mac login password: 640,166 (1 year) — [YouTube](https://www.youtube.com/watch?v=iXQk2PkKZUo)
- Split View: 586,664 (1 year) — [YouTube](https://www.youtube.com/watch?v=FBHDlzj2ViE)
- Restore files from a Time Machine backup: 417,287 (4 years) — [YouTube](https://www.youtube.com/watch?v=CSwy_thSXow)
- Back up with Time Machine: 378,893 (1 year) — [YouTube](https://www.youtube.com/watch?v=A74Q5_YHvHU)
- Free up storage space: 334,250 (1 year) — [YouTube](https://www.youtube.com/watch?v=phFdXb9-xEQ)
- Take a screenshot and record the screen: 321,473 (2 years) — [YouTube](https://www.youtube.com/watch?v=hOawEk3sKoU)
- Mac keyboard shortcuts: 274,652 (1 year) — [YouTube](https://www.youtube.com/watch?v=jAyd89j0B58)
- Delete or uninstall apps: 271,549 (6 months) — [YouTube](https://www.youtube.com/watch?v=mcKay1dYFmM)
- Migration Assistant to a new Mac: 148,167 — [YouTube](https://www.youtube.com/watch?v=L63cirAEthY)
- Transfer from a Windows PC with Migration Assistant: 140,513 (10 months) — [YouTube](https://www.youtube.com/watch?v=eOFaYzLz6Fo)
- Window tiling: 129,200 (1 year) — [YouTube](https://www.youtube.com/watch?v=U3KAmUYkkVc)
- Update macOS: 118,872 (8 months) — [YouTube](https://www.youtube.com/watch?v=_y0ZEuUMz38)
- Reinstall macOS from Recovery: 71,121 (4 months) — [YouTube](https://www.youtube.com/watch?v=jDWHv649--4)
- Caveat: channel search returned a fixed set of 158 videos, 38 of them Mac videos. A topic with no Apple video (NTFS, Wi-Fi password, mouse scrolling) has no figure here, which is not the same as zero demand.

**Catalog tables.** Columns: problem | US monthly searches (G) | Ask Different top Mac question views (M, creation year) | Apple Community "Me too" (M) | installs and other signals (M).

#### S. Storage

| # | Problem | US searches (G) | Ask Different (M) | Apple Community (M) | Installs / other (M) |
|---|---|---|---|---|---|
| S1 | Mac storage full / free up disk space / clean up | "how to free up disk space on mac" [5,400](https://www.seodata.dev/keyword/how-to-free-up-disk-space-on-mac) (2026-06-09); same-cluster variants "how to clear disk space on mac" 5,400, "how to free up space on mac" [2,900](https://www.seodata.dev/keyword/how-to-free-up-space-on-mac); "how to check storage on mac" [2,400](https://www.seodata.dev/keyword/how-to-check-storage-on-mac); "storage on mac" [1,900](https://www.seodata.dev/keyword/storage-on-mac); "disk cleanup mac" [1,600](https://www.seodata.dev/keyword/disk-cleanup-mac); "mac storage full" only [110](https://www.seodata.dev/keyword/mac-storage-full) (2026-10-19); "cleanmymac" [27,100](https://www.seodata.dev/keyword/cleanmymac) | "good ways to free up disk space" [6,757](https://apple.stackexchange.com/questions/202241) (2015) | — | Apple video 334,250 (above). Mole CLI 320,336 (formula #205) plus the older tap 85,355 ([formula 365d](https://formulae.brew.sh/api/analytics/install/365d.json); earlier note). GrandPerspective 14,037, DaisyDisk 8,334, OmniDiskSweeper 6,898, OnyX 13,719, CleanMyMac cask 4,590 ([cask 365d](https://formulae.brew.sh/api/analytics/cask-install/365d.json)). ncdu 43,431, dust 32,384 ([formula 365d](https://formulae.brew.sh/api/analytics/install/365d.json)). Raycast Mole 22,507 ([store](https://www.raycast.com/jlrochin/mole)), Disk Usage 1,276 ([store](https://www.raycast.com/easymikey/disk-usage)) |
| S2 | "System Data" huge | "system data mac" [880](https://www.seodata.dev/keyword/system-data-mac) (2026-10-19); in that cluster: "how to clear system data on mac" 3,600, "what is system data on mac" 3,600, "system data mac storage" 2,400, "mac system data large" 1,600 | "'System' storage on macOS Sierra is 470GB!" [229,155](https://apple.stackexchange.com/questions/261252) (2016); "macOS System Data storage is huge" [3,618](https://apple.stackexchange.com/questions/450402) (2022) | "System data taking up 115GB" **3,723** ([thread](https://discussions.apple.com/thread/254588225), Jan 2023, macOS 13.2); "MacOS Sequoia filling up with system data" 236 ([thread](https://discussions.apple.com/thread/255806791), Oct 2024; top reply found a 416 GB Carbon Copy Cloner snapshot); Tahoe 26.2 "165 GB /System" 1 ([thread](https://discussions.apple.com/thread/256245076)) | Community answers name caches, Time Machine snapshots and iCloud Drive local copies (~/Library/Mobile Documents) as causes, and quote Apple: System Data "can't be managed" ([search summary of threads](https://discussions.apple.com/thread/254249296)) |
| S3 | Purgeable space | no cached page | "What does Purgeable mean…" [140,486](https://apple.stackexchange.com/questions/252217) (2016); "How do I clear the purgeable area" [81,562](https://apple.stackexchange.com/questions/254676) (2016) | (inside System Data threads) | — |
| S4 | Find large files and folders | (part of S1) | "quickly find large files and folders on my mac" [109,496](https://apple.stackexchange.com/questions/118167) (2014) | — | same tools as S1 |
| S5 | Clear cache | "how to clear cache" 157,000 global, not Mac-specific (T, [seo.ai](https://seo.ai/blog/what-is-the-most-googled-question)) | Mac questions are app-specific, e.g. "Mac App Store download failure – clear cache?" [45,698](https://apple.stackexchange.com/questions/171827) | — | — |
| S6 | iCloud storage full (adjacent, mostly iPhone) | "icloud storage full" [2,900](https://www.seodata.dev/keyword/icloud-storage-full); "how to free up icloud storage" 6,600 (same cluster) | "turn off iCloud storage space warning" [312,315](https://apple.stackexchange.com/questions/233656) (2016); "disable 'iCloud Storage is Full' on OS X" [46,819](https://apple.stackexchange.com/questions/218494) | — | — |

#### P. Performance, battery, heat, frozen apps

| # | Problem | US searches (G) | Ask Different (M) | Apple Community (M) | Installs / other (M) |
|---|---|---|---|---|---|
| P1 | App frozen / force quit / "Task Manager" / Ctrl+Alt+Del | "how to force quit on mac" [60,500](https://www.seodata.dev/keyword/how-to-force-quit-on-mac) (2026-06-02); "force quit mac" [49,500](https://www.seodata.dev/keyword/force-quit-mac) (same pool); "how to force close an app on mac" 12,100 | "Ctrl + Alt + Delete: Mac Equivalent?" **[1,595,626](https://apple.stackexchange.com/questions/86010)** (2013); "Where can I find the task manager in Mac OS X Lion?" [204,291](https://apple.stackexchange.com/questions/41125) (2012); "something stronger than Force Quit?" [82,423](https://apple.stackexchange.com/questions/64697); "diagnose repeated hang / spinning beach ball" [63,533](https://apple.stackexchange.com/questions/99150) | — | Apple: Option-Command-Esc force quits an app ([Apple keyboard shortcuts, 2026-09-14](https://support.apple.com/en-us/102650)) |
| P2 | Slow Mac / what is using CPU and memory (Activity Monitor) | "activity monitor mac" [4,400](https://www.seodata.dev/keyword/activity-monitor-mac) cluster; "active monitor mac" 5,400; "mac resource monitor" 390 ([page](https://www.seodata.dev/keyword/mac-resources-monitor)); "mac running slow"/"why is my mac slow" not cached | "keyboard shortcut for launching Activity Monitor" [120,648](https://apple.stackexchange.com/questions/1520) (2010); "Troubleshooting a slow Mac" [8,043](https://apple.stackexchange.com/questions/186815) | Tahoe battery/overheat threads (P6) | Stats 144,548 (cask #26, pace 1.01) ([cask 365d](https://formulae.brew.sh/api/analytics/cask-install/365d.json)); htop 252,654 and btop 131,994 ([formula 365d](https://formulae.brew.sh/api/analytics/install/365d.json)); iStat Menus 8,819; "istat menus" [2,400](https://www.seodata.dev/keyword/istat-menus); Raycast System Monitor 123,233 (#20) ([store](https://www.raycast.com/hossammourad/raycast-system-monitor)) |
| P3 | kernel_task high CPU | — | "How to find cause of high kernel_task cpu usage?" **[618,653](https://apple.stackexchange.com/questions/363337)** (2019); "Kernel_task 300% CPU, high fan, slow" [74,836](https://apple.stackexchange.com/questions/133001); [63,127](https://apple.stackexchange.com/questions/10738); "What exactly does kernel_task do?" [61,031](https://apple.stackexchange.com/questions/37366) | — | — |
| P4 | WindowServer high CPU / memory | — | "WindowServer high CPU on Yosemite" [189,050](https://apple.stackexchange.com/questions/153397) (2014); "WindowServer takes a lot of memory" [57,525](https://apple.stackexchange.com/questions/331964); "WindowServer high CPU using external monitor – Big Sur" [15,205](https://apple.stackexchange.com/questions/407177) | "macOS 26 Tahoe – WindowServer taking chunks of CPU and Memory" 69 ([thread](https://discussions.apple.com/thread/256158509), Oct 2025) | — |
| P5 | Memory pressure / "out of application memory" / swap | — | "What is memory pressure, and how do I relieve it?" [59,974](https://apple.stackexchange.com/questions/202744); "run out of application memory but pressure <50%" [16,943](https://apple.stackexchange.com/questions/270150); "free up swap manually" [58,690](https://apple.stackexchange.com/questions/325098); "why already using swap" [57,427](https://apple.stackexchange.com/questions/190892) | — | — |
| P6 | Battery draining / which app drains it | not cached | "battery drain during sleep (MBP 2017)" [16,727](https://apple.stackexchange.com/questions/310403); "Energy Impact numbers" [7,510](https://apple.stackexchange.com/questions/107167) | "Battery Draining Fast since Tahoe upgrade" **290** ([thread](https://discussions.apple.com/thread/256139706), Sep 2025; top reply: Electron apps); "Tahoe causes overheating, battery drain" 36 ([thread](https://discussions.apple.com/thread/256140931)) | — |
| P7 | Battery health / charge limit | not cached | "How to check battery health on MacBook Pro" [15,439](https://apple.stackexchange.com/questions/126252); "'Battery Health…' button missing in Big Sur" [14,615](https://apple.stackexchange.com/questions/431607) | — | AlDente 18,123 (pace 0.94); coconutBattery 8,248; battery (CLI) 8,988 (pace 0.48) ([cask 365d](https://formulae.brew.sh/api/analytics/cask-install/365d.json)); Raycast Battery Health 22,852 ([store](https://www.raycast.com/o1y/battery-health)), Battery Optimizer 3,317 ([store](https://www.raycast.com/Qetesh/battery-optimizer)). macOS Tahoe 26.4 added a built-in 80–100% charge limit (System Settings > Battery > Charging "i") ([T3](https://t3.com/tech/laptops/this-new-macbook-feature-could-extend-your-battery-massively); [Tom's Guide](https://www.tomsguide.com/computing/macos/macos-tahoe-26-4-public-beta-is-here-and-these-are-the-best-new-features-for-your-mac)) |
| P8 | Fan noise / CPU temperature | — | "CPU temperature and fan speed from the command line" **[579,137](https://apple.stackexchange.com/questions/54329)** (2012); "current fan speed via terminal" [168,203](https://apple.stackexchange.com/questions/117391); "Mac won't boot and fan runs high" [136,730](https://apple.stackexchange.com/questions/142556) | — | Macs Fan Control 12,733; Hot 5,167; TG Pro 1,659 ([cask 365d](https://formulae.brew.sh/api/analytics/cask-install/365d.json)) |

#### G. Installing, opening, uninstalling and updating apps; startup items; default apps

| # | Problem | US searches (G) | Ask Different (M) | Apple Community (M) | Installs / other (M) |
|---|---|---|---|---|---|
| G1 | "App is damaged and can't be opened" | not cached | "macOS Catalina: 'App is damaged and can't be opened…'" [69,993](https://apple.stackexchange.com/questions/372084) (2019); "Minecraft is damaged…" [20,158](https://apple.stackexchange.com/questions/72609); "'FileName' is damaged" (documents) [8,956](https://apple.stackexchange.com/questions/387000) | "XYZ Is Damaged and Can't Be Opened" **431** ([thread](https://discussions.apple.com/thread/253714860), 2022). On Sequoia 15.4.1–15.5, plain data files carrying the quarantine flag (e.g. .srt) are reported as "damaged" ([thread](https://discussions.apple.com/thread/256070860)) | — |
| G2 | "Cannot be opened because the developer cannot be verified" / "unidentified developer" / Gatekeeper | not cached | "Open an app from an unidentified developer without the GUI" [28,289](https://apple.stackexchange.com/questions/202169); "open a single app by an unidentified developer" [25,132](https://apple.stackexchange.com/questions/58509); "can't open a document from an unidentified developer" [23,136](https://apple.stackexchange.com/questions/251205); "disable Gatekeeper via terminal" [20,344](https://apple.stackexchange.com/questions/75390); "reset all gatekeeper rules" [16,982](https://apple.stackexchange.com/questions/289071) | no high-count Sequoia thread found (subagent) | Sequoia removed the Control-click override for unsigned/unnotarized software; users must go to System Settings > Privacy & Security ([BleepingComputer](https://www.bleepingcomputer.com/news/apple/macos-sequoia-brings-better-gatekeeper-stalkerware-protections); [AppleInsider](https://appleinsider.com/inside/macos-sequoia/tips/whats-changed-in-runtime-protection-for-macos-sequoia)) |
| G3 | Uninstall apps (and leftovers) | "how to uninstall apps on mac" **[33,100](https://www.seodata.dev/keyword/how-to-uninstall-apps-on-mac)** (2026-06-15; same pool as "how to delete applications on mac" and "how to uninstall a program on mac"); "how to delete apps on mac" [22,200](https://www.seodata.dev/keyword/how-to-delete-apps-on-mac); "how to uninstall programs on mac" [9,900](https://www.seodata.dev/keyword/how-to-uninstall-programs-on-mac) | Top "uninstall" questions are developer tools ("uninstall zsh" [194,966](https://apple.stackexchange.com/questions/100468), "uninstall all Homebrew programs" [176,163](https://apple.stackexchange.com/questions/198623)); "uninstall apps without admin password" [14,846](https://apple.stackexchange.com/questions/197713) | — | Apple video 271,549 (6 months, above). AppCleaner 49,483 and Pearcleaner 42,850 ([cask 365d](https://formulae.brew.sh/api/analytics/cask-install/365d.json)); Raycast App Cleaner 37,297 ([store](https://www.raycast.com/dziad/appcleaner)). Switching guides call out that dragging an app to the Trash leaves support files ([Nektony, 2026-06-29](https://nektony.com/tips/switching-from-windows-to-mac)) |
| G4 | macOS update stuck / failed / how to update | "how to update mac" [6,600](https://www.seodata.dev/keyword/how-to-update-mac) (2026-06-20; same pool as "how to update macos"); "mac update" 3,600 | "Mac App Store update stuck (Installing – Calculating…)" [93,166](https://apple.stackexchange.com/questions/82551); "Sierra installation stuck in a reboot loop" [12,333](https://apple.stackexchange.com/questions/259196) | "Updating macOS Tahoe 26.3 is stuck at 5 minutes" **145** ([thread](https://discussions.apple.com/thread/256248022)); "Tahoe 26.4 stuck at 'Preparing'" 125 ([thread](https://discussions.apple.com/thread/256271054)) | Apple video "Update macOS" 118,872. App updaters: Latest 7,112, MacUpdater 1,435 ([cask 365d](https://formulae.brew.sh/api/analytics/cask-install/365d.json)); mas 132,161, topgrade 50,665 ([formula 365d](https://formulae.brew.sh/api/analytics/install/365d.json)) |
| G5 | Login items / "Background Items Added" / startup apps | "startup apps mac" 2,900; "how to stop apps from opening on startup mac" [1,000](https://www.seodata.dev/keyword/how-to-stop-apps-from-opening-on-startup-mac) (2026-06-24); "macbook startup apps" 1,600 | "Where do the 'Allow in the Background' login items… come from?" [52,812](https://apple.stackexchange.com/questions/449441) (2022); "startup items that run despite being unchecked" [30,778](https://apple.stackexchange.com/questions/42570); "remove zombie login items" [15,979](https://apple.stackexchange.com/questions/75589) | "Perpetual 'Background Items Added'" **5,057** — the highest count found ([thread](https://discussions.apple.com/thread/254341579), Nov 2022, macOS 13.1; top reply: Safe Mode, delete LaunchAgents/Daemons, `sfltool resetbtm`); Sequoia 15.7.4 repeat pop-ups 3 ([thread](https://discussions.apple.com/thread/256269375)) | — |
| G6 | Default app for a file type / default browser / per-link routing | not cached | "Force URL link to open with a specific browser" [72,152](https://apple.stackexchange.com/questions/121980); "add a new application to the Open With menu" [49,464](https://apple.stackexchange.com/questions/15030); "set default application for link types" [22,806](https://apple.stackexchange.com/questions/238047); "remove the default application" [18,814](https://apple.stackexchange.com/questions/47512); "default doesn't stay after reboot" [12,577](https://apple.stackexchange.com/questions/39648) | — | Finicky 5,896 (pace **1.97**), SwiftDefaultApps 1,841 ([cask 365d](https://formulae.brew.sh/api/analytics/cask-install/365d.json)); duti 21,006 ([formula 365d](https://formulae.brew.sh/api/analytics/install/365d.json)) |
| G7 | Running Windows apps or Windows on a Mac | not cached | — | — | UTM 67,321 (pace 1.56), Whisky 33,261, CrossOver 12,869 (pace 1.81), Parallels cask 5,607 ([cask 365d](https://formulae.brew.sh/api/analytics/cask-install/365d.json)) |

#### D. External drives and backups

| # | Problem | US searches (G) | Ask Different (M) | Apple Community (M) | Installs / other (M) |
|---|---|---|---|---|---|
| D1 | External drive / USB not showing up or not mounting | "external hard disk not detected mac" 5,400 and "external hard drive not showing up mac" [3,600](https://www.seodata.dev/keyword/external-hard-drive-not-showing-up-mac) (2026-06-21); "mac external hard drive not showing up" 2,900; "hard drive not showing up mac" 2,400 | "External Hard Drive Won't Mount" [191,472](https://apple.stackexchange.com/questions/268998) (2017); "Can't mount exFAT external drive" [177,464](https://apple.stackexchange.com/questions/293390); "External drive does not mount after plug off without eject" [154,188](https://apple.stackexchange.com/questions/235309); "make my iMac recognize my USB external hard drive" [101,026](https://apple.stackexchange.com/questions/22209); "USB flash drive not mounting" [52,117](https://apple.stackexchange.com/questions/193239) | "Sequoia 15.0 bugs – external hard drives" **314** ([thread](https://discussions.apple.com/thread/255776197), Sep 2024) | — |
| D2 | NTFS drives read-only / write NTFS | "ntfs for mac" **[9,900](https://www.seodata.dev/keyword/ntfs-for-mac)** (2026-07-14); "ntfs mac" 4,400; "what is ntfs for mac" 3,600; "free ntfs for mac" 1,900 | "How to format a disk to NTFS using Mac OS X" **[433,464](https://apple.stackexchange.com/questions/101817)** (2013); "Write to NTFS drives on Yosemite" [137,390](https://apple.stackexchange.com/questions/152661); [90,180](https://apple.stackexchange.com/questions/106589); "completely remove FUSE/NTFS-3G" [83,658](https://apple.stackexchange.com/questions/177885); "write to NTFS drives in OS X" [66,863](https://apple.stackexchange.com/questions/20889) | "WD hard drive is now read only?" 41 ([thread](https://discussions.apple.com/thread/254926080)); demand on Apple Community is low | Mounty for NTFS 29,515; macFUSE 131,227 (general purpose); FUSE-T 25,286 + 9,455; Paragon NTFS 3,116; Tuxera 685 ([cask 365d](https://formulae.brew.sh/api/analytics/cask-install/365d.json)); ntfs-3g-mac (gromgit tap) 20,360 ([formula 365d](https://formulae.brew.sh/api/analytics/install/365d.json)). New FSKit-based option: xntfs, $0.99 on the Mac App Store, released 2026-06-24, requires macOS 15.4, "without a kernel extension and without macFUSE", using ntfs-3g ([iTunes lookup](https://itunes.apple.com/lookup?id=6782636021&country=us)) |
| D3 | Format a drive for Mac and Windows (exFAT/FAT32) | "how to format usb on mac" [3,600](https://www.seodata.dev/keyword/how-to-format-usb-on-mac) (2026-06-07; same pool as "how to format a flash drive on mac"); "format flash drive on mac" 2,400 | "Format USB for Samsung TV" [41,287](https://apple.stackexchange.com/questions/239926); "Why is exFAT so slow on Mac?" [44,805](https://apple.stackexchange.com/questions/56872); "exFAT drive mounted read only" [15,064](https://apple.stackexchange.com/questions/354363) | — | balenaEtcher 34,358 (for bootable images) ([cask 365d](https://formulae.brew.sh/api/analytics/cask-install/365d.json)) |
| D4 | "Disk not ejected properly" / can't eject (in use) | not cached | "disable 'the disk was not ejected properly'" [78,542](https://apple.stackexchange.com/questions/6078) (2011); "dismiss many 'Disk Not Ejected Properly' notifications" [46,881](https://apple.stackexchange.com/questions/325547); after waking from sleep [9,071](https://apple.stackexchange.com/questions/359621); "eject my drive when my Mac refuses because of Spotlight" [165,790](https://apple.stackexchange.com/questions/142459) | "External Drives STILL ejecting during sleep in Ventura" **1,047** ([thread](https://discussions.apple.com/thread/254322044)); Sequoia "drive not ejected properly / in use" 22 ([thread](https://discussions.apple.com/thread/255888886)) | Raycast Single Disk Eject 9,985 ([store](https://www.raycast.com/johndturn/single-disk-eject)); Jettison 286 ([cask 365d](https://formulae.brew.sh/api/analytics/cask-install/365d.json)) |
| D5 | Time Machine: space, failures, restore | "how to backup mac" [2,900](https://www.seodata.dev/keyword/how-to-backup-mac) (2026-06-29) | "manually delete old backups to free space for Time Machine" **[755,867](https://apple.stackexchange.com/questions/39287)** (2012); "How big should my Time Machine drive be?" [402,748](https://apple.stackexchange.com/questions/100780); "back up only specific folders" [125,983](https://apple.stackexchange.com/questions/3159); "stuck on 'Preparing Backup'" [25,873](https://apple.stackexchange.com/questions/90478); "Time Machine Backup Failed" [22,269](https://apple.stackexchange.com/questions/200272) | "Time Machine Backup Fails After Upgrading to Sequoia" 65 ([thread](https://discussions.apple.com/thread/255763419)); demand is spread over many small threads | Apple videos 378,893 (back up) and 417,287 (restore); Carbon Copy Cloner 2,578, TimeMachineEditor 1,520 ([cask 365d](https://formulae.brew.sh/api/analytics/cask-install/365d.json)); Raycast Time Machine 2,517 ([store](https://www.raycast.com/SuoweiHu/time-machine)) |

#### T. Moving files between devices

| # | Problem | US searches (G) | Ask Different (M) | Apple Community (M) | Installs / other (M) |
|---|---|---|---|---|---|
| T1 | Android ↔ Mac file transfer | "android file transfer" **[9,900](https://www.seodata.dev/keyword/android-file-transfer)** (2026-06-09); "android file transfer mac" [6,600](https://www.seodata.dev/keyword/android-file-transfer-mac) | "send a file by Bluetooth from Android to an iMac" [125,980](https://apple.stackexchange.com/questions/39004); "Android File Transfer fails to copy to SD card" [23,930](https://apple.stackexchange.com/questions/237079); "Android File Transfer doesn't work on Apple Silicon" [4,442](https://apple.stackexchange.com/questions/468773) (2024) | — | OpenMTP 26,414 with 30-day pace **2.51** (the fastest-rising tool in this survey); Google's Android File Transfer 11,925 (pace 0.62); MacDroid 2,206; NearDrop 8,525; LocalSend 67,036 ([cask 365d](https://formulae.brew.sh/api/analytics/cask-install/365d.json), [30d](https://formulae.brew.sh/api/analytics/cask-install/30d.json)). Google's Quick Share works with AirDrop ("Everyone for 10 minutes") on Pixel 10 and, from Feb 2026, Pixel 9; reports in Sep 2026 add Samsung S24–S26 ([Android Authority](https://www.androidauthority.com/google-pixel-9-quick-share-airdrop-support-rollout-3641846); [TuttoAndroid](https://www.tuttoandroid.net/news/2026/09/05/quick-share-airdrop-segnalazioni-pixel-7-8-tablet-1171090/)) |
| T2 | iPhone photos → Mac | "how to transfer photos from iphone to mac" **[8,100](https://www.seodata.dev/keyword/how-to-transfer-photos-from-iphone-to-mac)** (2026-05-09; same pool as import/copy/export variants) | "import photos from iPhone to Mac without a cable" [160,017](https://apple.stackexchange.com/questions/122066); "move photos from iPhone to my local disk" [153,703](https://apple.stackexchange.com/questions/32788) | — | iPhone Mirroring: "iphone mirroring mac" 6,600 ([page](https://www.seodata.dev/keyword/how-to-mirror-iphone-to-mac)); Apple video 1,063,538 |
| T3 | AirDrop not working / how to AirDrop on Mac | "airdrop not working" **[8,100](https://www.seodata.dev/keyword/airdrop-not-working)** (2026-06-18; same pool as "airdrop stopped working"); "airdrop mac" [6,600](https://www.seodata.dev/keyword/airdrop-mac) (same pool as "how to airdrop on mac"); "airdrop" 74,000, mostly iPhone ([page](https://www.seodata.dev/keyword/airdrop)) | "Devices not showing up for AirDrop" [152,366](https://apple.stackexchange.com/questions/193614); "AirDrop: Bluetooth or Wi-Fi?" [143,148](https://apple.stackexchange.com/questions/146644); "Force AirDrop images to be JPEG not HEIC" [95,381](https://apple.stackexchange.com/questions/374556) | "Cannot add AirDrop to Finder sidebar in Sequoia" **473** ([thread](https://discussions.apple.com/thread/255770778), Sep 2024) | Raycast Quick AirDrop 322 ([store](https://www.raycast.com/ridemountainpig/quick-airdrop)) |
| T4 | Mac ↔ Windows PC (large files) | "transfer files mac to pc" 390, "file transfer from mac to windows" 140, "mac to pc file transfer" [50](https://www.seodata.dev/keyword/mac-to-pc-file-transfer) (2026-06-29) — low | "Is there an AirDrop client for Microsoft Windows?" **[283,707](https://apple.stackexchange.com/questions/18262)** (2011); "Send very large files through Ethernet between two Macs" [19,638](https://apple.stackexchange.com/questions/259569); "encrypted ZIP from Mac to PC" [9,364](https://apple.stackexchange.com/questions/296332) | — | LocalSend 67,036 (pace 1.23); Syncthing app 13,822 ([cask 365d](https://formulae.brew.sh/api/analytics/cask-install/365d.json)); croc 19,434, magic-wormhole 15,313 ([formula 365d](https://formulae.brew.sh/api/analytics/install/365d.json)) |

#### N. Wi-Fi and network

| # | Problem | US searches (G) | Ask Different (M) | Apple Community (M) | Installs / other (M) |
|---|---|---|---|---|---|
| N1 | Find a saved Wi-Fi password | not cached | **"How to Retrieve the Wi-Fi Password of a Connected Network on a Mac" [1,563,630](https://apple.stackexchange.com/questions/56130)** (2012) — the most-viewed problem question in this survey; "find my WiFi password when already connected" [377,493](https://apple.stackexchange.com/questions/99065); "Wi-Fi password through Terminal" [141,917](https://apple.stackexchange.com/questions/176119) | — | Raycast WiFi Password Reveal 6,528 ([store](https://www.raycast.com/louishuyng/wifi-password-reveal)), Wifi Share QR-Code 1,646 ([store](https://www.raycast.com/abul_kalam_robin/wifi-share)). In Sequoia, the Passwords app has a Wi-Fi section that reveals saved network passwords after authentication ([Eclectic Light](https://eclecticlight.co/2024/09/26/inside-sequioas-new-passwords-app/); [TechRadar](https://www.techradar.com/computing/software/forget-lastpass-apple-just-made-a-new-passwords-app-that-could-outshine-every-other-login-manager)) |
| N2 | Wi-Fi won't connect / keeps dropping / slow | "mac won't connect to wifi" 2,400; "mac not connecting to wifi" 1,900; "mac wifi not working" [1,000](https://www.seodata.dev/keyword/mac-wifi-not-working) (2026-08-15) | "Mac Wifi Slow, Fine on Other Devices" **[285,117](https://apple.stackexchange.com/questions/323130)** (2018); "Wifi Keeps Dropping MacBook Pro" [122,583](https://apple.stackexchange.com/questions/195359); "WiFi keeps turning off" [36,394](https://apple.stackexchange.com/questions/90877) | "macOS Sequoia network issues" **227** ([thread](https://discussions.apple.com/thread/255765230); fixed in 15.1) | Raycast Wi-Fi 18,443 ([store](https://www.raycast.com/koinzhang/wi-fi)), Refresh WiFi 4,430 ([store](https://www.raycast.com/natterstefan/refresh-wifi)), Network Diagnostics 4,186 ([store](https://www.raycast.com/hmarr/network-diagnostics)); WiFi Explorer 1,208, NetSpot 1,106 ([cask 365d](https://formulae.brew.sh/api/analytics/cask-install/365d.json)) |
| N3 | Internet speed / network quality test | "internet speed test" **[4,090,000](https://www.seodata.dev/keyword/internet-speed-test)** (2026-07-20; not Mac-specific); "speed test" 11.1M (same page) | "Interpreting networkQuality JSON output" [392](https://apple.stackexchange.com/questions/452584) | — | Raycast Speedtest **188,851** (#12 of 3,437) ([store](https://www.raycast.com/tonka3000/speedtest)), Network Speed 8,575 ([store](https://www.raycast.com/koinzhang/network-speed)); speedtest-cli 18,264 ([formula 365d](https://formulae.brew.sh/api/analytics/install/365d.json)); `networkQuality` ships at /usr/bin (checked on macOS 26.6.2) |
| N4 | DNS not resolving / flush or change DNS | not cached | "DNS not resolving on Mac OS X" **[361,808](https://apple.stackexchange.com/questions/26616)** (2011); "display the current DNS servers from the command line" [183,385](https://apple.stackexchange.com/questions/98021); "Flush DNS without sudo" [1,071](https://apple.stackexchange.com/questions/192108) | — | Raycast Flush DNS 15,377 ([store](https://www.raycast.com/rasmusbe/flush-dns)), Dig 4,703 ([store](https://www.raycast.com/danielbahl/dig)) |
| N5 | "What is my IP" | "what is my ip" 439,376 in 30 days, not Mac-specific (T, [Glimpse](https://meetglimpse.com/top-searched/most-searched-questions/)) | — | — | Raycast IP Geolocation 27,697 ([store](https://www.raycast.com/koinzhang/ip-geolocation)) |
| N6 | VPN setup | "how to connect to vpn on mac" [210](https://www.seodata.dev/keyword/how-to-connect-to-vpn-on-mac) | "PPTP VPN client for Sierra" [157,888](https://apple.stackexchange.com/questions/252228); "'Send all traffic over VPN' setting" [109,454](https://apple.stackexchange.com/questions/144572) | — | Raycast Connect to VPN 12,893 ([store](https://www.raycast.com/rasmusbe/connect-to-vpn)) |

#### B. Bluetooth, AirPods, audio, camera, microphone

| # | Problem | US searches (G) | Ask Different (M) | Apple Community (M) | Installs / other (M) |
|---|---|---|---|---|---|
| B1 | Bluetooth not working / devices disconnect | not cached | "Cannot turn on Bluetooth – exhausted all options" [13,416](https://apple.stackexchange.com/questions/192682); "Prevent auto-pairing for certain devices" [55,541](https://apple.stackexchange.com/questions/159548) | "Bluetooth connection problems since Sequoia" **178** ([thread](https://discussions.apple.com/thread/255814082)); "Tahoe 26.1 AirPods randomly disconnecting" 55 ([thread](https://discussions.apple.com/thread/256189921)) | Raycast Toothpick (Bluetooth connections) **60,154** (#51) ([store](https://www.raycast.com/VladCuciureanu/toothpick)); blueutil 18,213 ([formula 365d](https://formulae.brew.sh/api/analytics/install/365d.json)); Bluesnooze 3,993, AirBuddy 1,064 ([cask 365d](https://formulae.brew.sh/api/analytics/cask-install/365d.json)) |
| B2 | AirPods on Mac: choppy audio, poor mic, switching | "how to reset airpods" 117,000 global (T, [seo.ai](https://seo.ai/blog/what-is-the-most-googled-question)) | "AirPods choppy/unreliable when connected to Mac" [135,649](https://apple.stackexchange.com/questions/277923); "AirPods: Extremely poor mic quality on Mac" [114,932](https://apple.stackexchange.com/questions/282705); "connect two pairs of AirPods to one computer" [191,433](https://apple.stackexchange.com/questions/276661) | (B1) | Raycast Set Audio Device **77,940** (#36) ([store](https://www.raycast.com/benvp/audio-device)), AirPods Noise Control 9,309 ([store](https://www.raycast.com/chrahe/airpods-noise-control)) |
| B3 | No sound / wrong output | — | "External monitor on HDMI but no sound" [43,199](https://apple.stackexchange.com/questions/259140); "No sound after sleep" [22,867](https://apple.stackexchange.com/questions/119177) | — | Background Music 18,337, eqMac 5,293, SoundSource 4,468 ([cask 365d](https://formulae.brew.sh/api/analytics/cask-install/365d.json)) |
| B4 | Camera / microphone not working or levels changing | "mac camera not working" [1,300](https://www.seodata.dev/keyword/mac-camera-not-working) (2026-06-14) | "microphone input volume auto-adjusts – disable?" [169,243](https://apple.stackexchange.com/questions/97810); "small microphone icon floating on screen" [113,920](https://apple.stackexchange.com/questions/363545); "Why is my camera not working?" [31,988](https://apple.stackexchange.com/questions/198829) | — | Raycast Toggle Audio Input (mute mic) 7,919 ([store](https://www.raycast.com/Quentin23Soleil/mute-microphone)) |

#### M. Displays and appearance

| # | Problem | US searches (G) | Ask Different (M) | Apple Community (M) | Installs / other (M) |
|---|---|---|---|---|---|
| M1 | External display not detected; using lid closed; choosing the main display | "macbook external monitor" [1,900](https://www.seodata.dev/keyword/macbook-external-monitor) (2026-06-29); "how to connect a mac to a monitor" 3,600 (same page) | "use only the external display with MacBook Pro lid open" **[267,899](https://apple.stackexchange.com/questions/20591)**; "turn off MacBook display when I have an external display" [266,357](https://apple.stackexchange.com/questions/21729); "set an external display as the primary one" [168,029](https://apple.stackexchange.com/questions/6191) | "External Monitor Not Detected After macOS 26 Upgrade" **284** ([thread](https://discussions.apple.com/thread/256135940); top reply: stale WindowServer display plists); HDMI after Tahoe 83 ([thread](https://discussions.apple.com/thread/256145576)) | DisplayLink 8,168; displayplacer 19,234 (formula); Raycast Display Placer 9,261 ([store](https://www.raycast.com/eluce2/displayplacer)), Mirror Displays 1,702 |
| M2 | Blurry scaling / HiDPI / refresh rate on third-party monitors | "betterdisplay" [5,400](https://www.seodata.dev/keyword/betterdisplay) (2026-10-15) | "Force external display refresh rate" **[244,691](https://apple.stackexchange.com/questions/123181)**; "properly use scaling on an external display" [138,920](https://apple.stackexchange.com/questions/342682); "resolution lost after wake" [69,778](https://apple.stackexchange.com/questions/85472); "enable HiDPI on external display" [33,850](https://apple.stackexchange.com/questions/107846) | (no dedicated thread fetched) | BetterDisplay **92,161** (cask #52, pace 1.29) ([cask 365d](https://formulae.brew.sh/api/analytics/cask-install/365d.json)); Raycast Display Modes 7,331 ([store](https://www.raycast.com/agustin.prats/display-modes)) |
| M3 | External monitor brightness/volume from the keyboard | not cached | "adjust brightness of a third-party external monitor via keyboard" [26,606](https://apple.stackexchange.com/questions/314090) | — | MonitorControl 32,897 (pace 1.24), Lunar 9,952 ([cask 365d](https://formulae.brew.sh/api/analytics/cask-install/365d.json)); Raycast Brightness Control 6,549 ([store](https://www.raycast.com/cali/brightness-control)) |
| M4 | Dark mode schedule / per-app / Night Shift | not cached | "Fixing Night Shift on unsupported external monitors" [78,381](https://apple.stackexchange.com/questions/278789); "Dark Mode only for specific apps" [46,411](https://apple.stackexchange.com/questions/338044) | — | Tools are tiny: Dark Mode Buddy 145, Nightfall 389 ([cask 365d](https://formulae.brew.sh/api/analytics/cask-install/365d.json)); Raycast Night Light 6,884 ([store](https://www.raycast.com/devmoath/night-light)) |

#### I. Mouse, keyboard, clipboard, text input

| # | Problem | US searches (G) | Ask Different (M) | Apple Community (M) | Installs / other (M) |
|---|---|---|---|---|---|
| I1 | Right-click (incl. "New text file" from the context menu) | not cached | "Right-click, create a new text file. How?" **[146,252](https://apple.stackexchange.com/questions/94755)**; "right click on a Magic Mouse" [126,297](https://apple.stackexchange.com/questions/283887); "right click using only the keyboard" [105,518](https://apple.stackexchange.com/questions/119699) | — | Raycast Easy New File 14,456 ([store](https://www.raycast.com/koinzhang/easy-new-file)); "Right-click" is section 1 of Apple's Windows-switcher article (Q3) |
| I2 | Cut and paste (move) files in Finder | "cut and paste mac" not cached | "Why is it not possible to use 'cut' in the Finder?" **[201,414](https://apple.stackexchange.com/questions/12391)** (2011); "move multiple files at once" [12,991](https://apple.stackexchange.com/questions/10784); still asked about Catalina [7,733](https://apple.stackexchange.com/questions/396549) | — | Apple: "Option-Command-V: Move the files in the Clipboard from their original location to the current location" ([Apple 102650](https://support.apple.com/en-us/102650)) |
| I3 | Keyboard shortcuts / discovering an app's shortcuts / maximize | not cached | "Maximize window shortcut" [52,563](https://apple.stackexchange.com/questions/372719) | — | Apple video 274,652; KeyClu 8,157 ([cask 365d](https://formulae.brew.sh/api/analytics/cask-install/365d.json)) |
| I4 | Mouse scroll direction separate from trackpad | not cached | "Natural scroll: on with trackpad, off with mouse" [42,868](https://apple.stackexchange.com/questions/181186); "reverse the scroll direction of my mouse" [22,757](https://apple.stackexchange.com/questions/60732); Mac mini (2023) [8,848](https://apple.stackexchange.com/questions/452971) | "Option to separate trackpad and mouse (natural) scroll direction?" **131**, plus five more duplicate threads ([thread](https://discussions.apple.com/thread/254641913)) | LinearMouse **45,291** (pace 1.31), Mos 36,519, Scroll Reverser 20,748, Mac Mouse Fix 13,347, UnnaturalScrollWheels 8,821 ([cask 365d](https://formulae.brew.sh/api/analytics/cask-install/365d.json)); Raycast Change Scroll Direction 3,353 ([store](https://www.raycast.com/gregoralbrecht/change-scroll-direction)). The Natural scrolling option is shared by mouse and trackpad ([Korben](https://korben.info/en/natural-scroll-trackpad-reversed-mouse-macos.html); [Apple guide](https://support.apple.com/guide/mac-help/mh29222/mac)) |
| I5 | Mouse acceleration | not cached | "disable mouse acceleration in Yosemite" **[99,846](https://apple.stackexchange.com/questions/151539)**; High Sierra [45,547](https://apple.stackexchange.com/questions/333989); Monterey [25,104](https://apple.stackexchange.com/questions/439131) | — | Since Sonoma: System Settings > Mouse > Advanced > "Pointer acceleration" ([Tom's Guide](https://tomsguide.com/how-to/how-to-disable-mouse-acceleration-macos); [NoMouseAccel README](https://github.com/apsun/NoMouseAccel)) |
| I6 | Third-party mouse side buttons / extra buttons | — | "perfect app for customizing a 5-button mouse?" [5,547](https://apple.stackexchange.com/questions/451950) | — | Logi Options+ 39,941; SaneSideButtons 2,443; SensibleSideButtons 760 ([cask 365d](https://formulae.brew.sh/api/analytics/cask-install/365d.json)) |
| I7 | Home/End, forward delete, PC keyboard remapping | — | "Remap 'Home' and 'End' to beginning and end of line" **[230,975](https://apple.stackexchange.com/questions/16135)**; Home/End in Terminal [94,799](https://apple.stackexchange.com/questions/12997) | — | Karabiner-Elements 58,814 (pace 1.07) ([cask 365d](https://formulae.brew.sh/api/analytics/cask-install/365d.json)). Apple: "Fn-Delete: Forward delete… Or use Control-D" ([Apple 102650](https://support.apple.com/en-us/102650)) |
| I8 | Input source (keyboard language) switching, incl. per app | not cached | "Change shortcut to change input source" **[188,303](https://apple.stackexchange.com/questions/238064)**; "keyboard shortcut for switching input source" [142,579](https://apple.stackexchange.com/questions/135370); "automatically change keyboard language when switching windows" [15,734](https://apple.stackexchange.com/questions/252648) | — | Input Source Pro 5,183 (pace 1.25) ([cask 365d](https://formulae.brew.sh/api/analytics/cask-install/365d.json)); Raycast Keyboard Layout Switcher 5,341 ([store](https://www.raycast.com/lucaschultz/input-switcher)) |
| I9 | MacBook keyboard not working (mostly hardware) | "macbook keyboard not working" [5,400](https://www.seodata.dev/keyword/macbook-keyboard-not-working) (2026-06-28) | — | — | Raycast Clean Keyboard (locks keys for cleaning) 81,268 (#35) ([store](https://www.raycast.com/ike-gg/clean-keyboard)); KeyboardCleanTool 6,436 |
| I10 | Clipboard history | "clipboard history mac" not cached | "anything like the clipboard manager Ditto for Mac?" [61,900](https://apple.stackexchange.com/questions/97507) | — | Maccy **111,014** (cask #44), Clipy 11,363, CopyQ 4,014, Flycut 3,795 ([cask 365d](https://formulae.brew.sh/api/analytics/cask-install/365d.json)). Raycast's built-in clipboard history is not a store extension, so it is not counted. Tahoe added clipboard history in Spotlight, off until opted in; 26.1 added retention controls ([Tom's Guide](https://www.tomsguide.com/computing/software/how-to-enable-clipboard-history-in-macos-tahoe); [Macworld](https://www.macworld.com/article/2962021/this-macos-tahoe-26-1-setting-will-eliminate-embarrassing-clipboard-mishaps.html)) |
| I11 | Text expansion | not cached | "Text expansion for Mac OS X Lion?" [2,476](https://apple.stackexchange.com/questions/30777) — low | — | espanso 10,721, TextExpander 492 ([cask 365d](https://formulae.brew.sh/api/analytics/cask-install/365d.json)); Raycast Text Replacements (manages macOS's built-in ones) 7,356 ([store](https://www.raycast.com/raycast/text-replacements)) |

#### W. Windows, screen capture, menu bar, Finder visibility

| # | Problem | US searches (G) | Ask Different (M) | Apple Community (M) | Installs / other (M) |
|---|---|---|---|---|---|
| W1 | Split screen / window snapping / tiling | "split screen mac" not cached; MacKeeper 2021 named "how to split screen" a top MacBook question ([9to5Mac](https://9to5mac.com/2021/10/11/new-study-reveals-the-top-problems-people-face-with-apple-products-in-each-state/)) | "tile windows without going into full screen" [25,758](https://apple.stackexchange.com/questions/401872); "snap windows to fill 1/4 of the screen like in Windows" [1,466](https://apple.stackexchange.com/questions/387508) | — | Apple videos: Split View 586,664, Stage Manager 912,392, window tiling 129,200. Rectangle **115,768** (cask #39), AeroSpace 64,826, Hammerspoon 54,186, Amethyst 9,197, Loop 7,281 ([cask 365d](https://formulae.brew.sh/api/analytics/cask-install/365d.json)). Built-in tiling since Sequoia: drag to an edge (or hold Option), green-button menu, Window menu ([MacRumors](https://macrumors.com/2024/06/12/macos-sequoia-window-tiling/); [OWC](https://eshop.macsales.com/blog/96674-how-to-use-macos-sequoias-new-tiled-window-management-feature/)) |
| W2 | Switch between windows (Alt-Tab, windows of the same app) | "alt tab mac" not cached | "Switch between windows (not applications) in both directions" [26,038](https://apple.stackexchange.com/questions/287488); "Command-Tab between windows of the same app" [13,348](https://apple.stackexchange.com/questions/71184) | — | AltTab **63,689** (cask #74, pace 0.67) ([cask 365d](https://formulae.brew.sh/api/analytics/cask-install/365d.json)). Apple: "Command–Grave accent (`): Switch between the windows of the app you're using" ([Apple 102650](https://support.apple.com/en-us/102650)) |
| W3 | Take a screenshot (Swivel's Clothesline already handles new screenshots) | "how to screenshot on mac" **[450,000](https://www.seodata.dev/keyword/how-to-screenshot-on-mac)** (2026-10-18); "screenshot on mac" 301,000; "how to take a screenshot on mac" 135,000 | "full page screenshot in Mac OS Safari" [260,754](https://apple.stackexchange.com/questions/80434); "quickly hide/blur/redact text in screenshot" [184,905](https://apple.stackexchange.com/questions/72971) | — | Apple video 321,473. Shottr 26,410, CleanShot 7,898 ([cask 365d](https://formulae.brew.sh/api/analytics/cask-install/365d.json)); Raycast CleanShot X 122,403 ([store](https://www.raycast.com/Aayush9029/cleanshotx)) |
| W4 | Screen recording, especially **with system (internal) audio** | "how to screen record on mac" **[110,000](https://www.seodata.dev/keyword/how-to-screen-record-on-mac)** (2026-10-07); "screen record on mac" 33,100; "screen recording mac" 22,200 | "record both screen and sound with QuickTime" **[438,759](https://apple.stackexchange.com/questions/212829)** (2015); "record screen with the audio coming from your Mac" [93,334](https://apple.stackexchange.com/questions/371683); "easiest screen recording with a small file" [67,553](https://apple.stackexchange.com/questions/187399) | — | BlackHole 2ch **111,367** (cask #43, pace **1.55**), BlackHole 16ch 15,833, Background Music 18,337, OBS 49,915 (pace 1.47) ([cask 365d](https://formulae.brew.sh/api/analytics/cask-install/365d.json)); Raycast QuickTime Recording 16,446 ([store](https://www.raycast.com/pradeepb28/quicktime)). The built-in Shift-Command-5 toolbar and QuickTime record a microphone only, not system audio, including on Tahoe ([Tella](https://www.tella.com/blog/quicktime-screen-recording-with-audio)) |
| W5 | Scrolling (long) screenshot | "scrolling screenshot mac" [1,300](https://www.seodata.dev/keyword/scrolling-screenshot-mac) (2026-10-18) | "Screenshot the entire web-page" [59,976](https://apple.stackexchange.com/questions/334689); "'scrolling' screenshot" [3,786](https://apple.stackexchange.com/questions/480061) (2025) | — | Shottr 26,410 (pace 1.22) ([cask 365d](https://formulae.brew.sh/api/analytics/cask-install/365d.json)) |
| W6 | Menu bar crowded / icons hidden behind the notch | "menu bar mac" [2,400](https://www.seodata.dev/keyword/menu-bar-mac) (mostly "where is the menu bar"); "hide menu bar mac" [480](https://www.seodata.dev/keyword/hide-menu-bar-mac); "bartender mac" [1,900](https://www.seodata.dev/keyword/bartender-mac) | "prevent MacBook Pro notch from hiding menu bar icons" **[85,023](https://apple.stackexchange.com/questions/444113)** (2022); "Menu bar icons get hidden by long menus" [19,953](https://apple.stackexchange.com/questions/46679); "fit more things on my Mac menu bar" [14,613](https://apple.stackexchange.com/questions/454979) | "How to see menubar items under the notch?" 166 ([thread](https://discussions.apple.com/thread/253334703)) | Ice 58,613, Thaw 29,324 (pace 1.47), Hidden Bar 29,108 (pace 1.35), Bartender 8,926 ([cask 365d](https://formulae.brew.sh/api/analytics/cask-install/365d.json)); Raycast Toggle Menu Bar 12,100 ([store](https://www.raycast.com/iamyeizi/toggle-menu-bar)). Tahoe added System Settings > Menu Bar > "Allow in the Menu Bar" on/off toggles for third-party items ([OWC](https://eshop.macsales.com/blog/97824-macos-tahoe-makes-it-extremely-easy-to-clean-up-a-messy-menu-bar/)); macOS 27 adds "light menu-bar management" for items that overflow the notch ([Six Colors](https://sixcolors.com/post/2026/09/macos-27-golden-gate-review-bridging-the-tahoe-gap/)) |
| W7 | Show hidden files | not cached | "Hotkey to show hidden files in File Open dialog" **[201,792](https://apple.stackexchange.com/questions/186376)**; "view hidden files in the Finder" [147,884](https://apple.stackexchange.com/questions/58709); [86,790](https://apple.stackexchange.com/questions/5870); "display hidden files hot key on Mavericks gone?" [57,172](https://apple.stackexchange.com/questions/114628) | — | Raycast Toggle Desktop Visibility 4,819 ([store](https://www.raycast.com/Haojen/toggle-desktop-visibility)) |
| W8 | Copy a file's full path (Swivel already has "copy path") | not cached | "How do I get the full path for a file in Finder?" **[133,003](https://apple.stackexchange.com/questions/96737)** | — | Raycast Open Path 4,321 |

#### O. Printing, passwords, accounts, permissions, other

| # | Problem | US searches (G) | Ask Different (M) | Apple Community (M) | Installs / other (M) |
|---|---|---|---|---|---|
| O1 | Printer offline / won't print | not cached | "print in black and white as default" [67,610](https://apple.stackexchange.com/questions/16030); "actual IP address of a printer" [67,345](https://apple.stackexchange.com/questions/198037); "Hold for Authentication" [30,438](https://apple.stackexchange.com/questions/226773); "add shared Windows printer" [33,934](https://apple.stackexchange.com/questions/287980) | "Updated to Tahoe and now printers do not print" **364** ([thread](https://discussions.apple.com/thread/256145647); replies: enable print entries under Privacy & Security > Local Network) | — |
| O2 | Scanning documents | not cached | "better third-party scanner software than Preview?" [55,558](https://apple.stackexchange.com/questions/175007); "'Import from Scanner' greyed out in Preview" [35,504](https://apple.stackexchange.com/questions/155376) | — | — |
| O3 | Keychain keeps asking for a password / saved passwords | not cached | "OSX is repeatedly asking for login keychain password" **[748,455](https://apple.stackexchange.com/questions/124591)** (2014); "Stop '…wants to use keychain' prompts" [69,167](https://apple.stackexchange.com/questions/265821); "reset Keychain password without knowing the old one" [25,361](https://apple.stackexchange.com/questions/236828) | "wants to use the login keychain" **642** ([thread](https://discussions.apple.com/thread/253536979), 2022) | 1Password 60,005, Bitwarden 56,411 ([cask 365d](https://formulae.brew.sh/api/analytics/cask-install/365d.json)) |
| O4 | Forgot Mac login password | "how to reset mac password" [6,600](https://www.seodata.dev/keyword/how-to-reset-mac-password) (2026-06-22; same pool as "forgot macbook password") | "Why does OS X show a forgot password prompt…" [138,789](https://apple.stackexchange.com/questions/153040) | — | Apple video 640,166 |
| O5 | Factory reset / erase / reinstall macOS | not cached | — | — | Apple video **1,375,752** (the most-viewed Mac video on the channel); reinstall from Recovery 71,121 |
| O6 | Permission prompts confusion (Privacy & Security, Screen Recording, Full Disk Access, "Operation not permitted") | — | "ls: Operation not permitted – Mojave security?" [85,498](https://apple.stackexchange.com/questions/339862); "add Screen Recording permission to Chrome" [80,224](https://apple.stackexchange.com/questions/391785); "configure Screen Recording permission" [45,274](https://apple.stackexchange.com/questions/384228); "command to grant Full Disk Access" [43,580](https://apple.stackexchange.com/questions/351632); "reset Screen Recording permission" [33,111](https://apple.stackexchange.com/questions/384230) | "Bypass system private window picker, meaning?" 18 ([thread](https://discussions.apple.com/thread/255932446)) | Sequoia re-prompts screen-recording apps monthly ("Allow For One Month"), after a weekly design drew criticism in beta ([9to5Mac](https://9to5mac.com/2024/08/14/macos-sequoia-screen-recording-prompt-monthly/)) |
| O7 | Spotlight not finding apps (Tahoe) | — | weak (largest [2,688](https://apple.stackexchange.com/questions/219153)) | "Spotlight search broken since update to Tahoe" **176** ([thread](https://discussions.apple.com/thread/256149405); cause: Logi Options+ agents) | macOS 27 merges Spotlight with Siri on a new index ([Six Colors](https://sixcolors.com/post/2026/09/macos-27-golden-gate-review-bridging-the-tahoe-gap/)) |
| O8 | Moving to a new Mac / from a Windows PC | — | — | — | Apple videos 148,167 and 140,513 |
| O9 | Homebrew / developer setup (adjacent) | "homebrew" [49,500](https://www.seodata.dev/keyword/homebrew) (2026-07-01); "brew install" 22,200 | — | — | Raycast Brew 299,397 (#6) ([store](https://www.raycast.com/nhojb/brew)) |

### Inferences
- *(I)* **Demand tiers.** Signals were never added together. A problem is **Tier 1** when at least one independent signal is very large: a US cluster of 20K+ a month, an Apple video of 500K+ views, a Mac question on Ask Different with 500K+ views, or a dedicated GUI tool category with 100K+ installs a year. **Tier 2** needs at least two medium signals (3K+ a month, 100K+ Ask Different views, 300+ Me-too, or 25K+ installs). **Tier 3** is the rest. Ask Different views are lifetime totals and favour 2011–2015 questions, so a single old Ask Different number was never enough for Tier 1 when no recent signal backed it up.
- *(I)* **Ranked catalog** (most to least demand; ★ = signal from Sequoia/Tahoe-era threads or a rising tool):
  - **Tier 1**
    1. W4 screen recording with system audio ★ (BlackHole pace 1.55)
    2. W3 screenshots (already partly covered by Swivel)
    3. P1 frozen app / force quit / Task Manager
    4. G3 uninstall apps
    5. S1–S4 storage, System Data, purgeable, large files
    6. N1 Wi-Fi password
    7. W1 split screen / tiling
    8. O5 factory reset
    9. O4 forgotten login password
    10. D5 Time Machine
    11. O3 keychain prompts
    12. P3/P2 kernel_task and "what's using my Mac"
    13. P8 fans and temperatures
    14. N3 speed test
  - **Tier 2**
    15. D2 NTFS
    16. T3 AirDrop ★
    17. T1 Android transfer ★ (OpenMTP pace 2.51)
    18. T2 iPhone photos
    19. D1 drive not detected ★
    20. G5 background/login items ★ (5,057 Me-too)
    21. I10 clipboard history
    22. M2 scaling/HiDPI ★
    23. M1 display detection and lid-closed ★
    24. W6 menu bar/notch ★
    25. I2 cut/paste files
    26. W7 hidden files
    27. I1 right-click
    28. I7 Home/End
    29. I8 input source
    30. I4 scroll direction
    31. I5 mouse acceleration
    32. N2 Wi-Fi drops ★
    33. N4 DNS
    34. D4 eject problems ★
    35. G1/G2 Gatekeeper "damaged" / "can't be verified" ★
    36. P4 WindowServer
    37. D3 exFAT formatting
    38. G4 stuck updates ★
    39. P6 battery drain ★
    40. B2 AirPods on Mac
    41. B1 Bluetooth ★
    42. M3 monitor brightness
    43. O1 printing ★
    44. W8 copy path
    45. W2 window switching
    46. P5 memory pressure
  - **Tier 3**
    47. P7 battery health and charge limit
    48. G6 default apps (Finicky rising)
    49. O6 permission prompts
    50. B4 camera/mic
    51. B3 no sound
    52. O2 scanning
    53. I3 shortcut discovery
    54. I6 mouse buttons
    55. W5 scrolling screenshot
    56. T4 Mac↔PC transfer
    57. G7 Windows apps
    58. M4 dark mode schedule
    59. O7 Spotlight
    60. I11 text expansion
    61. N6 VPN
    62. I9 keyboard hardware
    63. S5/S6 caches and iCloud
    64. N5 IP lookup
    65. O8/O9 migration and Homebrew
- *(I)* **Outdated or shifting problems.** Count these as declining demand for third-party fixes, but rising demand for "where is the setting" guidance:
  - Mouse acceleration (built-in toggle since Sonoma).
  - Charge limit ≥80% (built-in since 26.4).
  - Clipboard history (built into Tahoe's Spotlight).
  - Window tiling (built-in since Sequoia).
  - Menu bar hiding (built-in toggles in Tahoe; notch overflow handling in macOS 27).
  - Wi-Fi password lookup (Passwords app in Sequoia).
  - Android↔Mac sharing for supported phones (Quick Share↔AirDrop in 2026).
  - Many top Ask Different questions are about OS X 10.9–10.15 (Yosemite WindowServer, Catalina "damaged" apps), so their view totals overstate current demand.
- *(I)* **Problems that got worse on Sequoia, Tahoe and 27, and so need explaining:**
  - Gatekeeper: the Control-click bypass was removed, and data files are flagged "damaged" on 15.4–15.5.
  - Background Items notifications.
  - Tahoe printing, now gated by Local Network permission.
  - External displays not detected on Tahoe.
  - Monthly screen-recording re-prompts.
  - A Tahoe regression in Spotlight.
- *(I)* Tool installs are a developer-skewed proxy (Homebrew users). Large install counts for BlackHole, Rectangle, Maccy, BetterDisplay and LinearMouse show what power users fix themselves. They don't measure how often ordinary users hit the problem; search volume does that better.

### Gaps
- **No measured US search volume** for many classic how-tos: right-click, copy/paste and cut/paste files, show hidden files, Wi-Fi password, split screen and window snapping, Ctrl-Alt-Del/Task Manager, Home/End, clipboard history, "app is damaged", "cannot be verified", NTFS write, Time Machine errors, battery health, Bluetooth, printer offline, dark mode, login items. seodata had no cached page and its anonymous API was rate-limited; Google Trends returned 429. A person with a Google Ads or Ahrefs account could fill these in.
- Apple Community view counts are not displayed, so "Me too" counts are the only measure there. The Apple Support site publishes no "most viewed articles" ranking that I could find.
- Reddit (r/mac, r/MacOS) was not reachable in the earlier pass and was not retried, so Reddit post and comment counts are missing.
- Apple Support YouTube search returned only 38 Mac videos. Topics without an Apple video can't be compared on that scale.
- No global (non-US) volumes were collected for these terms. The seo.ai and Glimpse lists cover only the very top questions.

---

## Q2. How could an assistant app solve each problem? (solvability classes A–E)

### Takeaway
Most high-demand problems can be **diagnosed** with no special permission (class A): disk and APFS snapshot usage, processes and memory, `networkQuality`, drive lists and ejects, battery and display reports, quarantine and signature checks, default apps, per-app input source. Most **fixes** are either a guided step in System Settings, which an app can open directly with `x-apple.systempreferences:` URLs (class E), or one admin-authorised command (class B): flushing DNS, Wi-Fi diagnostics, power metrics, resetting background items, deleting snapshots, restarting Bluetooth.

The problems that power users install tools for mostly need permissions Swivel avoids:
- Accessibility or Input Monitoring: separate scroll direction, mouse buttons, text expansion, window snapping, menu-bar hiding.
- Screen Recording or audio capture: system-audio recording, scrolling screenshots.
- Full Disk Access or App Management: complete uninstall with leftovers, full System Data analysis, Time Machine history.
- Private APIs: HiDPI scaling and DDC brightness.

NTFS write moved from "kernel extension only" to "feasible as an FSKit app extension" (macOS 15.4+), but it still needs an NTFS engine, and the open one is GPL ntfs-3g.

### Cited Findings
**Platform facts behind the classes**
- System Settings panes open by URL, for example:
  - Storage `x-apple.systempreferences:com.apple.settings.Storage`
  - Login Items `…com.apple.LoginItems-Settings.extension`
  - Privacy & Security `…com.apple.settings.PrivacySecurity.extension`
  - Full Disk Access `…com.apple.preference.security?Privacy_AllFiles`
  - Screen Recording `…?Privacy_ScreenCapture`
  - Battery, Displays, Bluetooth, Wi-Fi, Network, Keyboard, Mouse, Trackpad, Time Machine, Printers & Scanners, Software Update, Desktop & Dock and Sharing each have an ID too.

  This list is as of Sonoma 14.2 ([bvanpeski/SystemPreferences](https://github.com/bvanpeski/SystemPreferences/blob/main/macos_preferencepanes-Ventura.md)). IDs can change between releases and should be tested per macOS version ([MacScripter](https://www.macscripter.net/t/-/74147); [Keyboard Maestro forum](https://forum.keyboardmaestro.com/t/system-settings-lock-screen/37658)).
- Gatekeeper: after a blocked launch, go to System Settings > Privacy & Security and click "Open Anyway". macOS then saves an exception. Alerts include "Apple cannot check 'X' for malicious software" ([Apple: Safely open apps on your Mac, 2026-05-27](https://support.apple.com/en-us/102445)). Sequoia removed the Control-click override ([BleepingComputer](https://www.bleepingcomputer.com/news/apple/macos-sequoia-brings-better-gatekeeper-stalkerware-protections)).
- **App Management** (since Ventura) blocks one app from modifying or deleting another without permission. Apps signed by the same team are exempt ([Michael Tsai](https://mjtsai.com/blog/2022/11/07/ventura-app-management/); [Apple Developer Forums](https://developer.apple.com/forums/thread/715220)).
- Sequoia prompts when an app reads another app's data ("would like to access data from other apps") ([Apple Developer Forums](https://developer.apple.com/forums/thread/768173); [Michael Tsai](https://mjtsai.com/blog/?p=44819)).
- From a bundled app, `tmutil latestbackup` failed with "requires Full Disk Access privileges" on macOS 15.5 ([Apple Developer Forums](https://developer.apple.com/forums/thread/784254)).
- Since macOS 14.4, CoreWLAN returns Wi-Fi SSIDs only to apps with Location Services permission, and the `airport` tool was removed ([Apple Developer Forums](https://developer.apple.com/forums/thread/748518); [thread 759044](https://developer.apple.com/forums/thread/759044)).
- Sequoia added Local Network privacy prompts for apps that reach devices on the LAN. There is no programmatic way to pre-approve ([Michael Tsai](https://mjtsai.com/blog/2024/10/02/local-network-privacy-on-sequoia); [Apple Developer Forums](https://developer.apple.com/forums/thread/763484)).
- Read-only checks on macOS 26.6.2, run from Terminal on 2026-10-21 (local test):
  - `wdutil info` and `powermetrics` refuse to run without root ("usage: sudo wdutil info"; "powermetrics must be invoked as the superuser").
  - These ran without root: `tmutil listlocalsnapshots /`, `tmutil destinationinfo`, `networksetup -getdnsservers Wi-Fi`, `sfltool dumpbtm` (showed only one UID's records), and `ipconfig getsummary en0` (showed the SSID line).
  - `networkQuality`, `softwareupdate`, `diskutil`, `system_profiler`, `memory_pressure` and `tccutil` are all present.
- Screen capture: Sequoia's re-prompt applies to apps using ScreenCaptureKit too ([9to5Mac](https://9to5mac.com/2024/08/14/macos-sequoia-screen-recording-prompt-monthly/)). macOS 14.4 Core Audio taps let an app capture system or other apps' audio with user permission (`NSAudioCaptureUsageDescription`, TCC service kTCCServiceAudioCapture). There is no public API to check that permission ([AudioCap](https://github.com/insidegui/AudioCap)).
- Clipboard: Apple announced alerts when an app programmatically reads the general pasteboard without paste-related user input. New `detect` methods check content types without triggering the alert, and an `accessBehavior` property exists. Testing is via the `EnablePasteboardPrivacyDeveloperPreview` default. A September 2025 report says it is not on by default in macOS 26 ([Michael Tsai, 2025-05-12](https://mjtsai.com/blog/2025/05/12/); [Apple Developer Forums](https://developer.apple.com/forums/thread/794560)).
- File systems:
  - FSKit (macOS 15.4+) lets developers ship a file system as a user-space app extension that is "compatible with Mac App Store distribution". It currently supports `FSUnaryFileSystem`, the pattern macOS's own HFS, msdos, ExFAT and ntfs follow ([Apple FSKit docs](https://developer.apple.com/documentation/fskit)).
  - ExtendFS, a read-only ext4 FSKit driver, requires macOS 15.6 because of FSKit issues in 15.4–15.5 ([ExtendFS](https://github.com/kthchew/ExtendFS)).
  - xntfs ships NTFS read/write via FSKit plus ntfs-3g on the App Store, with "copyable Terminal commands when the sandbox prevents the app from safely doing something directly" ([iTunes lookup](https://itunes.apple.com/lookup?id=6782636021&country=us)).
  - iBoysoft, a vendor, says its FSKit NTFS driver needs no SIP change or Recovery Mode ([iBoysoft, 2026-09-20](https://iboysoft.com/ntfs-for-mac/ntfs-driver-tech-evolution.html)).
- Licences of the open engines:
  - ntfs-3g: "GPL-2.0-or-later AND LGPL-2.0-or-later" ([Homebrew formula](https://formulae.brew.sh/api/formula/ntfs-3g.json)).
  - libmtp (Android MTP): LGPL-2.1-or-later ([Homebrew formula](https://formulae.brew.sh/api/formula/libmtp.json)).
  - LocalSend: Apache-2.0, ~95K GitHub stars ([GitHub API](https://api.github.com/repos/localsend/localsend)).
- Built-in fixes that now exist:
  - Pointer acceleration toggle, Sonoma+ ([Tom's Guide](https://tomsguide.com/how-to/how-to-disable-mouse-acceleration-macos)).
  - Window tiling, Sequoia+ ([MacRumors](https://macrumors.com/2024/06/12/macos-sequoia-window-tiling/)).
  - Wi-Fi passwords in the Passwords app, Sequoia+ ([Eclectic Light](https://eclecticlight.co/2024/09/26/inside-sequioas-new-passwords-app/)).
  - Clipboard history in Spotlight, Tahoe+ ([Tom's Guide](https://www.tomsguide.com/computing/software/how-to-enable-clipboard-history-in-macos-tahoe)).
  - "Allow in the Menu Bar" toggles, Tahoe ([OWC](https://eshop.macsales.com/blog/97824-macos-tahoe-makes-it-extremely-easy-to-clean-up-a-messy-menu-bar/)).
  - Charge limit 80–100%, 26.4+ ([T3](https://t3.com/tech/laptops/this-new-macbook-feature-could-extend-your-battery-massively)).
  - Notch overflow handling, macOS 27 ([Six Colors](https://sixcolors.com/post/2026/09/macos-27-golden-gate-review-bridging-the-tahoe-gap/)).
  - Option-Command-V to move files, Command-` to switch windows, Fn-Delete or Control-D to forward delete ([Apple 102650](https://support.apple.com/en-us/102650)).
- Still missing from macOS:
  - No separate scroll direction for mouse and trackpad ([Korben](https://korben.info/en/natural-scroll-trackpad-reversed-mouse-macos.html); third-party apps listed at [TechPP](https://techpp.com/2024/06/29/change-scrolling-directions-for-mac-mouse-and-trackpad-separately/)).
  - No system-audio capture in Screenshot or QuickTime ([Tella](https://www.tella.com/blog/quicktime-screen-recording-with-audio)).
  - No NTFS write: macOS reads NTFS only, and the community answer recommends exFAT for drives shared with Windows ([Apple Community](https://discussions.apple.com/thread/254926080)).
- Fixes the community recommends that act on user-level files:
  - Tahoe external-display detection: move `~/Library/Preferences/ByHost/com.apple.windowserver.displays*` plists and restart ([thread](https://discussions.apple.com/thread/256145576)).
  - Background Items: `sfltool resetbtm` ([thread](https://discussions.apple.com/thread/254341579)).
  - Spotlight: disable conflicting launch agents ([thread](https://discussions.apple.com/thread/256149405)).
  - Tahoe printing: enable Local Network entries ([thread](https://discussions.apple.com/thread/256145647)).

### Inferences
*(I)* Classification. **A** = a sandbox-free app with no special permission (guidance, opening panes, read-only diagnostics, actions within the user's own rights). **B** = needs an admin password once (privileged helper, AuthorizationServices or `osascript … with administrator privileges`). **C** = needs Accessibility, Screen Recording, Full Disk Access, Input Monitoring or Automation; Location Services, Local Network and Bluetooth prompts are noted too. **D** = needs a driver or file-system extension, a private API, or isn't feasible natively. **E** = best answered by explaining or linking the right setting. The first class listed is the realistic primary route.

| # | Problem | Class | Reasoning (inference unless cited above) |
|---|---|---|---|
| S1 | Free up space / large files | **A** (+C for full coverage) | `du` or a FileManager scan of user-chosen folders, APFS container usage, Trash. Protected app data (Mail, Messages, Safari, other apps' containers) needs Full Disk Access or triggers Sequoia's "data from other apps" prompt. |
| S2 | System Data huge | **A** diagnose, **E/B** fix | List local snapshots (worked without root), iCloud Drive local copies, caches, swap. Explain that System Data is partly unmanageable. Deleting snapshots or cleaning system areas needs admin (B). Time Machine history needs FDA (C). |
| S3 | Purgeable | **E/A** | Explain the concept. Compare the "available" and "available for important usage" volume capacities (public URL resource keys). Thinning snapshots is B. |
| S4 | Find large files | **A** | Same scanner as S1, limited to folders the user chooses. |
| S5 | Clear caches | **E** (A for ~/Library/Caches) | Deleting caches gains little and is risky. Other apps' containers prompt (C). |
| S6 | iCloud storage full | **E** | Link to the Apple Account / iCloud storage settings. |
| P1 | Frozen app / force quit | **A** | `NSRunningApplication.forceTerminate` for the user's own apps; root processes need B. Teach Option-Command-Esc. |
| P2 | Slow Mac / CPU / memory | **A** | `top`/`ps`, `memory_pressure`, `vm_stat` and libproc for all processes' CPU. Name the heavy apps and offer to quit them (A). |
| P3 | kernel_task | **A/E** | Explain thermal throttling. Show CPU and thermal state via `ProcessInfo.thermalState` (public). Detailed sensors need SMC/HID reads (private-ish). |
| P4 | WindowServer | **A/E** | Detect display count, scaling, transparency and Electron apps. Link Reduce Transparency (Accessibility settings) — E. |
| P5 | Memory pressure / swap | **A** | Read pressure and swap; name the top memory users. |
| P6 | Battery drain / which app | **A** partial, **B** full | `pmset -g assertions` and per-process CPU time are A. Per-app energy needs `powermetrics` (root, verified) — B. |
| P7 | Battery health / charge limit | **A** read, **E**, **B** for <80% | `system_profiler SPPowerDataType` / `ioreg` for cycle count and health (A). Point to the built-in 80–100% limit on 26.4+ (E). Lower limits mean writing SMC through a privileged helper, as AlDente does (B). |
| P8 | Fans / temperature | **A\*** read, **B** control | Temperature and fan reads use IOKit/SMC keys (undocumented, an acceptable read but a private-API question). Fan control needs a root helper. |
| G1 | "App is damaged" | **E + A** (B/C to strip quarantine) | Check the quarantine xattr, `codesign`/`spctl` results and where the file was downloaded from (A). Open Privacy & Security for "Open Anyway" (A/E). Stripping quarantine from a bundle may hit App Management (C) or root ownership (B), and it is a deliberate security bypass, so it needs a strong warning. |
| G2 | "Can't be verified" / unidentified developer | **E + A** | As G1. Sequoia removed Control-click, so a guided "Open Anyway" walkthrough is the main value. |
| G3 | Uninstall + leftovers | **C** (A partial, E) | Deleting another developer's app bundle hits App Management. Finding leftovers in other apps' containers prompts on Sequoia, and a full scan needs FDA. A-level fallback: list matching ~/Library items by bundle ID and reveal them in Finder for the user to trash. |
| G4 | Update stuck / failed | **A** diagnose, **E**, **B** | `softwareupdate -l`, free-space check, unified log; guide the Apple steps. Downloading a full installer or installing needs admin (B). |
| G5 | Background / login items | **A** read, **E**, **B** reset | Read the plists in ~/Library/LaunchAgents and /Library/LaunchAgents and LaunchDaemons and map them to their vendor (A). Open the Login Items pane (E). Remove daemons or run `sfltool resetbtm` with admin (B). |
| G6 | Default apps | **A** | `NSWorkspace` default-app APIs (macOS 12+); for browsers the system shows its own confirmation. Per-link routing like Finicky needs the app registered as the browser (A, invasive). |
| G7 | Windows apps | **E/D** | Point to UTM, CrossOver or Parallels; virtualization is out of scope. |
| D1 | Drive not detected / not mounting | **A** | `diskutil list`, DiskArbitration, USB/Thunderbolt `system_profiler`; mount attempts; read-only First Aid verify. Separate "no hardware seen" from "seen but unmounted" from "unreadable file system". |
| D2 | NTFS write | **D** (E/A workaround) | Feasible since 15.4 as an FSKit extension, but Swivel would need an NTFS engine. ntfs-3g is GPL, and the house rules bar copying GPL code. The user must also enable the extension. Workaround: explain that NTFS is read-only, copy files off, reformat as exFAT (A). |
| D3 | Format for Mac + Windows | **A** | `diskutil eraseDisk ExFAT … GPT` on an external disk. Destructive, so require explicit confirmation. Admin may be needed for some devices (B fallback). |
| D4 | Eject problems | **A** (+B) | Unmount and eject via DiskArbitration (A). List blocking processes with `lsof` (only the user's own without root; Spotlight's `mds` needs B). Use a power assertion against sleep-time ejects (A; see the keep-awake note). |
| D5 | Time Machine | **A** status, **C/B** | `tmutil status` and `destinationinfo` work without root (verified). Backup lists need FDA when run from an app (cited). Deleting backups is B. Setup and restore are E. |
| T1 | Android ↔ Mac | **A** (+Local Network prompt) / **E** | LAN transfer with a published protocol (e.g. LocalSend's; the project is Apache-2.0); Sequoia shows the Local Network prompt. USB MTP in user space is possible without a driver but heavy, and libmtp is LGPL-2.1. Point supported phones to Quick Share↔AirDrop (E). |
| T2 | iPhone photos | **E/A** | Image Capture and Photos are built in. ImageCaptureCore import is plausible (A). |
| T3 | AirDrop not working | **A/E** | Check Wi-Fi and Bluetooth power and the discoverability setting; open the AirDrop window. Teach "Everyone for 10 minutes" (E). |
| T4 | Mac ↔ PC | **E/A** | Built-in SMB File Sharing (E) or LAN transfer (A + Local Network); exFAT drive (D3). |
| N1 | Wi-Fi password | **E + B** | The Passwords app (Sequoia) is the supported path (E). Reading the system keychain item via `security` needs admin authentication (B). Show a QR code for sharing after reveal (A). |
| N2 | Wi-Fi drops / slow | **A** partial, **B**, **C(Location)** | `networkQuality`, `scutil --dns`, gateway ping (A). The SSID needs Location permission for apps. `wdutil` needs root (verified). DHCP renew and resets are B. "Forget network" is E. |
| N3 | Speed test | **A** | Wrap the built-in `networkQuality`. |
| N4 | DNS | **A** read, **B** flush/change | `scutil --dns` read (A). Flush via `dscacheutil` + mDNSResponder HUP and `networksetup -setdnsservers` need admin (B). |
| N5 | IP lookup | **A** | Local interfaces plus an optional public-IP request. |
| N6 | VPN | **E** | Link the VPN settings. |
| B1 | Bluetooth | **A** read, **B** reset, **E** | `system_profiler SPBluetoothDataType` (A). Restarting `bluetoothd` needs admin (B). CoreBluetooth access shows the Bluetooth prompt. |
| B2 | AirPods on Mac | **E/A** | Explain that using the AirPods mic drops to a low-quality headset profile. Switch the input to the built-in mic via CoreAudio default-device APIs (A). |
| B3 | No sound / output | **A** | List CoreAudio devices and set the default output (A). Restarting `coreaudiod` is B. |
| B4 | Camera / mic | **E/A** | Point to the Privacy & Security panes. Name which app is using the mic or camera if possible (system indicator; no public API). Killing `VDCAssistant` is B. |
| M1 | Display detection / lid closed / main display | **A/E** | `system_profiler SPDisplaysDataType` (A). Guide the Displays settings (E). Moving the user's ByHost WindowServer plists aside (Tahoe fix) is user-level (A) but needs restart and confirmation. |
| M2 | HiDPI / scaling / refresh | **D** (private API) / **E** | Custom HiDPI modes rely on virtual-display tricks (BetterDisplay); public APIs only expose existing modes. Swivel's rule allows one private API. |
| M3 | Monitor brightness via DDC | **D** (private IOAVService on Apple silicon) + **C** for media keys | MonitorControl-style DDC needs undocumented IOKit; intercepting brightness keys needs Accessibility. |
| M4 | Dark mode schedule / per app | **C** (Automation) / **D** / **E** | Toggling appearance needs Automation of System Events or a private call. Explain the "Auto" appearance (E). |
| I1 | Right-click | **E** | Teach two-finger click, Control-click and the Mouse/Trackpad settings. A "New file here" action could be a Swivel Quick Action (A). |
| I2 | Cut/paste files | **E** (+A) | Teach Command-C then Option-Command-V. Swivel's Move action and File Shelf already cover the drag version. |
| I3 | Shortcut discovery / maximize | **E** (+C) | A cheat sheet of system shortcuts (E). Reading another app's menu shortcuts needs Accessibility (KeyClu-style), so C. |
| I4 | Separate scroll direction | **C** | Rewriting scroll events needs an event tap (Accessibility/Input Monitoring). Not possible otherwise. |
| I5 | Mouse acceleration | **E** | The built-in toggle since Sonoma. |
| I6 | Mouse side buttons | **C** | Event remapping. |
| I7 | Home/End / forward delete | **A/E** | A per-user `~/Library/KeyBindings/DefaultKeyBinding.dict` remaps Home/End in Cocoa text views without permissions. `hidutil` key remaps run in the user session. Teach Fn-Delete. Karabiner-level remaps need a driver (D). |
| I8 | Input source per app | **A** | Watch frontmost-app changes (NSWorkspace) and select the input source (Text Input Sources API), which needs no permission. Link the built-in "automatically switch to a document's input source" option (E). |
| I9 | Keyboard hardware | **E** | Point to service, or an external keyboard. |
| I10 | Clipboard history | **A** capture (future prompt risk), **C** for auto-paste, **E** | Polling `NSPasteboard` works today, but Apple's pasteboard-privacy alerts could later prompt on reads. Pasting into another app with a synthetic Cmd-V needs Accessibility. Point to Tahoe's built-in Spotlight clipboard (E). |
| I11 | Text expansion | **E** / **C** | The built-in Text Replacements (E); system-wide expansion needs keystroke monitoring (C). |
| W1 | Tiling / snapping | **E** (C for custom) | Teach the built-in tiling (Option-drag, green-button menu). Custom snapping needs Accessibility. |
| W2 | Window switching | **E** / **C** | Command-` (E). An AltTab-style switcher needs Accessibility, plus Screen Recording for thumbnails. |
| W3 | Screenshots | **E/A** | The built-in tools plus Swivel's Clothesline. |
| W4 | Screen recording with system audio | **C** | ScreenCaptureKit needs Screen Recording (monthly re-prompts in Sequoia). Core Audio taps need the audio-capture TCC grant. No class A route exists. |
| W5 | Scrolling screenshot | **C** (E workaround) | Needs Screen Recording plus scroll injection (Accessibility). Workaround: the browser's full-page capture or PDF export (E). |
| W6 | Menu bar / notch | **E** (C to manage others' items) | Teach Tahoe's "Allow in the Menu Bar" and macOS 27's overflow handling. Ice-style hiding needs Accessibility (and screen capture for its UI). |
| W7 | Hidden files | **E/A** | Teach Shift-Command-Period (a widely used Finder toggle; not on Apple's shortcuts page). `defaults write com.apple.finder AppleShowAllFiles` + relaunching Finder is user-level (A). |
| W8 | Copy path | **A** | Already in Swivel's action menu. |
| O1 | Printing | **A** read, **E**, **B** | `lpstat` queue status (A). Guide the Local Network grant (Tahoe) and re-adding the printer (E). Resetting the printing system or `lpadmin` needs admin (B). |
| O2 | Scanning | **E/A** | Image Capture and Preview; ImageCaptureCore scanning (A). |
| O3 | Keychain prompts | **E** (+B) | Explain a login keychain that is out of sync with the password, and guide Keychain Access. Reset needs the user password. |
| O4 | Forgot login password | **E** | The user is locked out, so in-session help is impossible; guide Recovery and the Apple Account reset. |
| O5 | Factory reset | **E** | "Erase All Content and Settings" is destructive; guide only. |
| O6 | Permission confusion | **A/E** | Explain each pane and open the exact one by URL (A). Reset a single app's grant with `tccutil reset <service> <bundle-id>` (user-level; A, behaviour to verify per release). Reading every app's grants needs FDA (C). |
| O7 | Spotlight broken | **A** read, **B** reindex, **E** | `mdutil -s /` (A); `mdutil -E` with admin (B); Spotlight privacy list (E). |
| O8 | Migration | **E** | Migration Assistant. |
| O9 | Homebrew setup | **B** | Its installer needs admin; out of scope for most users. |

- *(I)* **Best first targets for an assistant, high demand + class A/E:**
  1. **Storage doctor** (S1–S4): a snapshot / iCloud Drive / cache explainer plus a large-file finder on user-chosen folders, with FDA optional.
  2. **"What's slowing my Mac" and force quit** (P1–P5): processes, memory pressure, kernel_task and WindowServer explanations, one-click quit.
  3. **"Can't open this app" helper** (G1–G2): read the quarantine and notarization state, explain it, open Privacy & Security at the right spot.
  4. **Network check** (N2–N4): `networkQuality`, DNS servers, gateway reachability; admin-gated flush; Wi-Fi password via the Passwords app.
  5. **Drive doctor** (D1, D3, D4): detect, mount, eject, name blockers, exFAT formatting for Mac + Windows, an NTFS explanation.
  6. **Background items explainer** (G5): read LaunchAgents and LaunchDaemons and map them to apps; deep-link Login Items.
  7. **Default-app chooser** (G6) and **per-app input source** (I8): both class A.
  8. **Battery and display report** (P7, M1): health, cycle count, a link to the charge limit; display info; the Tahoe plist reset.
  9. **Windows-switcher coach** (I1, I2, I5, I7, W1, W2, W7, P1).
  10. **Permission centre** (O6): open the exact pane, reset one grant.
- *(I)* **Defer or avoid** under Swivel's no-permission rule:
  - Separate scroll direction (C).
  - Clipboard auto-paste and text expansion (C).
  - Menu-bar item hiding (C).
  - System-audio recording (C; the biggest-demand item that is blocked).
  - HiDPI and DDC brightness (private API).
  - Complete uninstall with leftovers (C).
  - NTFS write (D, plus licensing).
  - Battery limits below 80% (B helper).

  If Swivel ever accepts one permission, Screen Recording/audio capture (W4, W5) or Accessibility (I4, I6, W1, W6) unlocks the most demand.

### Gaps
- Whether `tccutil reset`, `diskutil eraseDisk` on external media, `tmutil deletelocalsnapshots` and `networksetup -setdnsservers` need admin on macOS 26 and 27 was **not tested**, to avoid changing system state. Those B/A labels are inferences.
- I didn't verify how Sequoia's App Management behaves when an app moves another app to the Trash through `NSWorkspace.recycle`.
- No Apple documentation was found for the exact pasteboard-privacy shipping status in macOS 26 or 27.
- I didn't check whether FSKit extensions still need the user to enable them in System Settings, or how that works on macOS 27.
- I didn't check whether any permissively licensed NTFS write implementation exists that Swivel could use.

---

## Q3. Which problems recur most in "switching from Windows to Mac" guides and in Apple's most-viewed support topics?

### Takeaway
Windows-switcher material keeps coming back to the same items:
- Right-click.
- Ctrl+Alt+Del / Task Manager → Force Quit / Activity Monitor.
- Cut and paste of files.
- Control Panel → System Settings, File Explorer → Finder.
- Closing, maximising and snapping windows.
- Home/End and other keyboard differences.
- Uninstalling (Trash leaves leftovers).
- Exchanging files with Windows (exFAT/NTFS, Migration Assistant).
- Screenshots.

Ask Different confirms each of these with large view counts: Ctrl+Alt+Del 1.6M, Home/End 231K, Task Manager 204K, cut in Finder 201K, right-click "new text file" 146K. Apple's own most-viewed Mac videos on the Apple Support channel are about resets and recovery (factory reset 1.38M, login password 640K), multitasking (Stage Manager 912K, Split View 587K) and maintenance (Time Machine 379K/417K, free up storage 334K, uninstall 272K, screenshots and recording 321K).

### Cited Findings
- Apple's "Mac tips for Windows switchers", published 2026-04-09, is organised as:
  1. Right-click
  2. Scroll, swipe, click
  3. Close and resize windows
  4. Change volume
  5. Change Mac settings (instead of Control Panel)
  6. Browse for files (Finder)
  7. Find files
  8. Open apps
  9. Throw files away
  10. Rename files
  11. Preview files
  12. Back up files
  13. Keyboard shortcuts (cut, copy, paste, undo, print, close window, switch apps, quit, **forward delete**, find, **force quit**, screenshot)

  It has no "Task Manager" heading; Force Quit is the stand-in ([Apple 102323](https://support.apple.com/en-au/102323); older URL [HT204216](https://support.apple.com/en-gb/HT204216)).
- Nektony's switching guide (2026-06-29) lists:
  - Migration Assistant skips .exe apps; an exFAT drive or the cloud for transfer.
  - Start menu, taskbar, File Explorer and Control Panel equivalents.
  - Spotlight, Force Quit, gestures, Time Machine, screenshots.
  - The Command key replacing Ctrl.
  - Substitutes for WinRAR and Notepad.
  - "Missing built-in tools" (archive, video, window management).
  - Uninstalling (Trash leaves support files).
  - Running Windows via Parallels/VMware.

  Source: [Nektony](https://nektony.com/tips/switching-from-windows-to-mac). The vendor sells Mac utilities.
- Another switcher guide says file cut-and-paste uses Cmd+C then Cmd+Option+V, since Finder has no plain cut; the Option key is "sometimes called the Alt key" ([Evetech, 2026](https://evezone.evetech.co.za/deep-dives/switching-from-windows-to-mac-complete-beginners-guide-2026); [MacMost](https://macmost.com/10-ways-macos-is-different-than-windows.html)). Apple confirms Option-Command-V moves files ([Apple 102650](https://support.apple.com/en-us/102650)).
- Ask Different views (M) for switcher-type questions:
  - "Ctrl + Alt + Delete: Mac Equivalent?" [1,595,626](https://apple.stackexchange.com/questions/86010)
  - "Remap Home and End" [230,975](https://apple.stackexchange.com/questions/16135)
  - "Where can I find the task manager…" [204,291](https://apple.stackexchange.com/questions/41125)
  - "Why is it not possible to use 'cut' in the Finder?" [201,414](https://apple.stackexchange.com/questions/12391)
  - "Right-click, create a new text file" [146,252](https://apple.stackexchange.com/questions/94755)
  - "AirDrop client for Microsoft Windows?" [283,707](https://apple.stackexchange.com/questions/18262)
  - "anything like Ditto (Windows clipboard manager) for Mac?" [61,900](https://apple.stackexchange.com/questions/97507)
  - "snap windows to fill 1/4 of the screen like in Windows" [1,466](https://apple.stackexchange.com/questions/387508)
  - "Maximize window shortcut" [52,563](https://apple.stackexchange.com/questions/372719)
  - "How to format a disk to NTFS" [433,464](https://apple.stackexchange.com/questions/101817)
- Install signals (M) for Windows-style behaviour:
  - AltTab 63,689 (Windows-style Alt-Tab).
  - Rectangle 115,768 (Windows-style snapping).
  - Karabiner-Elements 58,814 (PC keyboard remapping).
  - LinearMouse / Mos / Scroll Reverser 45,291 / 36,519 / 20,748 (a PC mouse with traditional scrolling).
  - Mounty for NTFS 29,515.

  All from [cask 365d](https://formulae.brew.sh/api/analytics/cask-install/365d.json).
- Apple's Mac support hub features: Upgrade your Mac, "If an error occurs while upgrading", password reset (iforgot), macOS Recovery (reinstall), Time Machine, Mac setup guides, Migration Assistant, and Apple Communities ([support.apple.com/mac](https://support.apple.com/mac)).
- Apple's own "Switched from Windows to Mac?" guide pages point to wallpaper, icon and widget styles, text size, and using iPhone with Mac ([Apple MacBook Air guide](https://support.apple.com/en-ca/guide/macbook-air/apdd88fdad7a/mac)).
- Apple Support YouTube, most-viewed Mac videos (M, 2026-10-21):
  - Factory reset 1,375,752
  - iPhone Mirroring 1,063,538
  - Stage Manager 912,392
  - Reset login password 640,166
  - Add widgets 630,050
  - Split View 586,664
  - Restore from Time Machine 417,287
  - Back up with Time Machine 378,893
  - Free up storage 334,250
  - Screenshot and record the screen 321,473
  - Keyboard shortcuts 274,652
  - Uninstall apps 271,549
  - Migration from a Windows PC 140,513

  Video links are in Q1.
- Secondary "Tahoe problems" roundups repeat: installation failures and stuck updates; Wi-Fi and Bluetooth drops; apps that won't open; battery drain and overheating; external displays and drives; visual slowdowns (Reduce Motion / transparency) ([MacPaw](https://macpaw.com/how-to/macos-tahoe-issues); [iThinkDifferent](https://www.ithinkdiff.com/macos-tahoe-issues/); [Stellar](https://www.stellarinfo.com/article/macos-tahoe-issues-and-fixes.php); [CleanMyMac blog](https://cleanmymac.com/blog/macos-tahoe-issues)). These are vendor or SEO blogs; use them as context only.

### Inferences
- *(I)* The Windows-switcher cluster is the cleanest **class E/A opportunity**. A "Mac for Windows users" coach in Swivel could map each Windows habit to its Mac equivalent and, where possible, perform it. Each item already shows large Ask Different demand:
  - Ctrl+Alt+Del → force-quit panel.
  - Cut → Option-Command-V.
  - Show hidden files.
  - Home/End → a key-binding file.
  - Snap → built-in tiling.
  - Alt-Tab → Command-`.
  - Exchange drives with a PC → exFAT format, with an NTFS explanation.
  - Uninstall → explain leftovers.
  - Control Panel → open the exact System Settings pane.
- *(I)* Apple's most-viewed topics lean toward **destructive or recovery tasks** (erase, reset password, reinstall, restore). An assistant should explain and deep-link these (E) rather than automate them, because the user is often locked out or about to lose data.
- *(I)* The overlap of "Apple's most-viewed" and "highest search demand" is: storage, uninstalling, Time Machine, screenshots and recording, split screen and tiling, keyboard shortcuts, updates. Those are the strongest candidates for Swivel's first assistant features. Each has an A/E route, except system-audio recording (C).

### Gaps
- Apple does not publish view counts for support articles. The prominence of articles is inferred only from the Mac support hub layout and the YouTube channel.
- Only three switcher guides were read in full (Apple, Nektony, plus Evetech/MacMost summaries). Tom's Guide's "9 things Windows users should do first" could not be read (only the title was returned).
- No quantified survey of switchers' pain points (e.g. a percentage of switchers who struggle with X) was found.
