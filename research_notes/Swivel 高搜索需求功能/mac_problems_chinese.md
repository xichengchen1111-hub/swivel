# Computer problems and must-have utilities of Chinese-speaking Mac users (mainland first, also Taiwan/HK), quantified, as of 2026-10-21

All data in this file was collected on 2026-10-21 unless a different date is given. No logins were used and no CAPTCHAs were attempted. Baidu Index and 5118 were not retried.

**How the numbers were obtained (read this before using them)**
- **Zhihu (知乎).** Zhihu question pages return HTTP 403 to curl, and Zhihu search needs a login, so neither was used. Instead, Sogou web search (搜狗) was queried with `insite=www.zhihu.com`. Sogou's result snippets print the Zhihu question's "N个回答 - N人关注 - N次浏览" (answers, followers, views). These are **Sogou's cached snapshots, and the crawl date is unknown.** The same question can show different values in different snapshots. For example, "Mac上有什么实用的必备软件?" appeared as 327 answers / 555.4万 views in one result, 360 / 662.0万 in another and 362 / 665.7万 in a third. Treat every Zhihu figure as a **lower bound of the current cumulative value**. Question creation dates were not visible. Many of the questions are old: a title starting "Mac OS X …" implies it was asked before about 2016. Views are cumulative and favour old questions, and they include search traffic.
- **Bilibili (B站).** search.bilibili.com sorted by 最多播放 (most played) was readable in the built-in browser without logging in. Plays and upload dates come from the result cards. A date without a year (for example "8月30日") means 2026.
- **CSDN.** Figures come from CSDN's public search JSON (so.csdn.net/api/v3/search). For each query, the highest `view` count on the first results page is reported. That page is ordered by relevance, not by views.
- **V2EX.** Reply counts come from the SOV2EX index (sov2ex.com/api/search). The index can lag behind the live topic: t/1198554 shows 29 replies in the index against 35 on the live API.
- **GitHub.** Star counts come from the GitHub GraphQL API, read through the gh CLI on 2026-10-21.
- **Mac App Store.** Ratings could not be obtained (see Q2 Gaps).
- **Interpretation labels.** "Measured" means counts read directly from a page or API. "Third-party estimate" means figures such as QuestMobile or AI产品榜 numbers reported by media. "Inference" means my own reasoning.

## Q1. Which Mac problems recur most in Chinese communities, and how big is each (ranked, with numbers)?

### Takeaway
By measured attention, the largest recurring clusters for Chinese Mac users are:
1. **Storage and cleaning**: 系统数据 and "其他" storage, cache cleaning and leftovers after uninstalling, plus WeChat's disk usage and file locations.
2. **Windows-habit gaps**: 剪切 (cut-and-paste files), 右键新建 (new file from the right-click menu), mouse-wheel direction, hidden files, and general "新手上手" (getting started).
3. **Unarchiving** (RAR/7z).
4. **Screenshots**: long/scrolling capture plus OCR and translation.
5. **Battery-charging anxiety**.
6. **External drives**: drives not recognised, and NTFS writing.
7. **Menu-bar clutter**.
8. **Recording system audio**.
9. **External-display blur (HiDPI)**.
10. **Printers**.

"已损坏，无法打开" (Gatekeeper/quarantine) is very widely written about (many Zhihu column posts, four CSDN posts with more than 10K views in 2023 alone, Bilibili videos up to 12.4万 plays). Its individual Zhihu question views are smaller, and much of that content comes from pirated-software resource sites.

### Cited Findings

#### A. Ranked summary table
Zhihu figures are "views / answers / followers" from Sogou snapshots. "Sum" adds the Mac-specific related questions that appeared on the first Sogou page; it is a rough indicator, not a total. Each row is detailed in section B.

| # | Problem cluster | Largest Mac-specific Zhihu question (views) | Approx. sum of first-page Mac-specific Zhihu Qs | Top Bilibili play (upload date) | Top CSDN views (post date) | Main tools (GitHub stars, 2026-10-21) |
|---|---|---|---|---|---|---|
| 1 | Storage: 系统数据 / "其他" / cleaning / uninstall leftovers | 182.8万 ("Mac里很占存储空间的“其他”怎么删?") | ≈425万 (storage) + ≈19万 (uninstall) | 34万 (2021-07-16); uninstall video 25.4万 (2024-01-05) | 24,009 (2023-12-25) | tw93/Mole 72,035; Pearcleaner 14,927; Tencent lemon-cleaner 6,342 |
| 2 | WeChat (微信) disk usage, file and chat-history location, migration | 213.5万 (general "微信占用存储空间很大"); Mac-specific 16.4万 | ≈60万 Mac-specific (location/backup/migration) | low: 1.5万 (2022) | 31,095 (2019-01-16) | CleanMyWechat 5,590; WeChatTweak 13,828 (anti-recall, multiple instances) |
| 3 | Windows→Mac newcomer gaps: 新手上手, 剪切, 右键新建 | 275.6万 (tips hub question), 42.9万, 34.7万; 剪切 35.5万; 右键新建/剪切 9.3万 | — | 259万 (2022-02-17); 68.8万 (2025-12-29) | 19,331 (剪切, 2018); 14,028 (右键新建, 2018) | 超级右键 (MAS, closed source); RClick 1,090; OpenInTerminal 7,032 |
| 4 | Unarchiving RAR/7z | 391.9万 ("Mac 上最方便的压缩与解压缩软件是什么?"; another snapshot 294.7万) | — | 8.9万 (2023-04-03) | n/a | (Keka, The Unarchiver; many Chinese MAS unarchivers) |
| 5 | Mouse: wheel direction opposite to trackpad, "mouse experience is bad" | 109.5万 ×2 (see the anomaly note in B) | ≈240万 | 3.9万 (Mos, 2020) | 57,900 (2024-10-20) | Caldis/Mos 21,685; LinearMouse 7,034 |
| 6 | Screenshots: long/scrolling capture, built-in shortcuts | 72.9万 (built-in shortcuts), 41.9万 / 37.0万 (long/scrolling) | ≈206万 | 2.9万 (Longshot) | n/a | (iShot, Snipaste, Xnip, Shottr: closed source) |
| 6b | Screenshot OCR + translation (mostly not Mac-specific) | Mac-specific 4.9万; general OCR 131.0万; screenshot translation 199.3万 | — | — | — | Easydict 15,024; Bob 9,753; pot-desktop 19,367 (archived) |
| 7 | Show hidden files | 205.2万 ("Mac系统如何显示隐藏文件?") | ≈222万 | — | 33,194 (2023-02-25) | (built-in ⌘⇧.) |
| 8 | Battery: should I keep it plugged in, limit charge to 80% | 134.6万 | ≈293万 (mostly reassurance questions) | 102.3万 (2019-08-21) | — | AlDente 9,213; Battery-Toolkit 2,463 (archived) |
| 9 | External drives: not recognised, won't eject, NTFS read-only | 96.9万 ("为什么Macbook Pro 无法识别移动硬盘?"); NTFS 20.1万 | ≈156万 | 16.7万 (2020); 14.1万 (2019) | 22,519 (2018-01-11) | ntfstool 4,066; Nigate 2,484; at least 4 Chinese commercial NTFS apps on MAS |
| 10 | Menu bar: too many icons, notch hides icons | 127.1万 (old question: "Mac OS X 下如何整理顶部菜单栏图标?") | ≈129万 | 11.3万 (2022) | — | Ice 29,836; Hidden Bar 15,232; boring.notch 11,093 |
| 11 | Screen recording with system (internal) audio | 70.3万 | ≈109万 | 9.1万 (Omi, 2022-07-18) | 72,390 (2018-04-14; highest CSDN figure in this sample) | BlackHole 19,942; QuickRecorder 8,734 |
| 12 | External display blurry, HiDPI | 31.7万 | ≈103万 (+42.5万 "2K or 4K?" purchase question) | 11.7万 (2021-10-05) | 13,217 (2021) | BetterDisplay 34,148; MonitorControl 34,521; one-key-hidpi 11,159 |
| 13 | Printers: connect, install drivers | 20.1万 | ≈87万 | — | — | — |
| 14 | Window management, split screen | 71.5万 | ≈79万 | weak | — | Rectangle 30,117; Loop 11,827; alt-tab 16,422 |
| 15 | Clipboard history | 62.8万 | ≈68万 | — | — | Maccy 22,013; Clipy 8,930 (macOS 26 now has a basic built-in history) |
| 16 | "已损坏，无法打开" / "无法验证开发者" / where to download apps safely | 32.8万 ("…将对电脑造成伤害…移到废纸篓") | ≈53万 for 已损坏 + ≈29万 for download sources | 12.4万 (fix tutorial, 2022-04-09); 66.2万 ("在 Mac 上装软件，要学会和苹果斗智斗勇", 2022-12-31) | 17,330 (2023-08-03); 4 posts >10K in 2023 | Sentinel 1,769 |
| 17 | Input method: auto-switching, Chinese/English toggling | 8.6万 | ≈20万 | 11.9万 (2018); 10.3万 (2024-06-27) | 14,119 (2022-07-29) | InputSourcePro 3,650 (created 2025-04) |
| 18 | Android ↔ Mac file transfer | 8.6万 | ≈26万 | 27万 (2021-12-10) | 11,972 (2024) | LocalSend 95,100 (global); OpenMTP 7,582 |
| 19 | Windows-only software: online banking USB keys (网银U盾), VMs, dual boot | 23.8万 (网银U盾 on M1) | ≈32万 for banking + ≈24万 for VMs | 10.7万 (Parallels, 2020) | — | UTM 36,068 |
| 20 | Slowdowns, heat, RAM | 43.2万 ("为何 macOS 的内存占用那么大?") | ≈80万 | — | — | exelban/stats 42,698 |

#### B. Detail per problem, with sources

**1. Storage / 系统数据 / cleaning**
- Zhihu snapshots (views / answers / followers):
  - "Mac里很占存储空间的“其他”怎么删?" 182.8万 / 19 / 451.
  - "macOS 系统占用储存空间太大怎么办?" 122.0万 / 34 / 501.
  - "为何 macOS 的内存占用那么大?" 43.2万 / 27 / 390.
  - "MacBookPro的系统数据占用100多个G,怎么清理?" 11.2万.
  - "MacBook储存空间 系统数据占了200多GB,怎么可以清理呢?" 7.6万 / 3 / 30.
  - "Mac下如何清理系统或应用产生的垃圾?" 2.5万.
  - "M1芯片的Mac,系统数据占用空间过大怎么处理?" 2.0万.
  - Source: [Sogou→Zhihu "Mac 系统数据 占用 太大"](https://www.sogou.com/sogou?query=Mac+%E7%B3%BB%E7%BB%9F%E6%95%B0%E6%8D%AE+%E5%8D%A0%E7%94%A8+%E5%A4%AA%E5%A4%A7&insite=www.zhihu.com)
- Zhihu: "macOS 是否需要用第三方软件清理系统?" 36.3万 / 40 / 1,174. Single-answer CleanMyMac questions show tens of views, and "如何激活cleanmymac?" shows 2.4万, a piracy signal — [Sogou→Zhihu "Mac 需要 清理软件 CleanMyMac"](https://www.sogou.com/sogou?query=Mac+%E9%9C%80%E8%A6%81+%E6%B8%85%E7%90%86%E8%BD%AF%E4%BB%B6+CleanMyMac&insite=www.zhihu.com)
- Zhihu: "如何清理Mac存储空间?" 22.3万 / 17 / 106; "Mac 越用越卡怎么办?" 4.4万 — [Sogou→Zhihu "Mac 内存 不够 卡顿 发热"](https://www.sogou.com/sogou?query=Mac+%E5%86%85%E5%AD%98+%E4%B8%8D%E5%A4%9F+%E5%8D%A1%E9%A1%BF+%E5%8F%91%E7%83%AD&insite=www.zhihu.com)
- Uninstall leftovers (Zhihu): "mac电脑明明删除了应用软件,但登录项的允许在后台还存在!" 12.4万 / 4 / 15; "关于mac软件的彻底删除?总是有残留" 3.0万; "有什么好的苹果电脑清理软件?" 2.5万 — [Sogou→Zhihu "Mac 卸载软件 残留"](https://www.sogou.com/sogou?query=Mac+%E5%8D%B8%E8%BD%BD%E8%BD%AF%E4%BB%B6+%E6%AE%8B%E7%95%99+%E5%B9%B2%E5%87%80&insite=www.zhihu.com)
- Bilibili:
  - "Mac硬盘又满了？教你3分钟高效清理存储空间！" 34万 plays (2021-07-16).
  - "Mac电脑正确卸载软件的方式，0残留" 25.4万 (2024-01-05).
  - "手把手教你清理 Mac 的“其他空间”" 19.9万 (2021-02-09).
  - "Mac电脑怎么清理磁盘空间？" 14.3万 (2022-06-14).
  - "Mac中的“系统数据”是什么？又该如何清理？" 9.9万 (2024-05-15).
  - "都2021年了，CleanMyMac还值得吗？" 9.1万 (2021-02-24).
  - Sources: [Bilibili search](https://search.bilibili.com/video?keyword=Mac%20%E6%B8%85%E7%90%86%20%E7%B3%BB%E7%BB%9F%E6%95%B0%E6%8D%AE%20%E5%AD%98%E5%82%A8%E7%A9%BA%E9%97%B4&order=click); [Bilibili "Mac 清理软件"](https://search.bilibili.com/video?keyword=Mac%20%E6%B8%85%E7%90%86%E8%BD%AF%E4%BB%B6&order=click)
- CSDN: "Mac 存储空间里面的”系统数据”怎么清理？…释放200G空间" 24,009 views (2023-12-25) — [CSDN](https://blog.csdn.net/ll1042668699/article/details/135198800); "Mac清理垃圾，简直不要太简单了" 13,258 (2024-12-13) — [CSDN](https://blog.csdn.net/2401_88252730/article/details/144443914). The CSDN search reports 40,684 blog results for this query — [CSDN search API](https://so.csdn.net/api/v3/search?q=Mac%20%E7%B3%BB%E7%BB%9F%E6%95%B0%E6%8D%AE%20%E6%B8%85%E7%90%86&t=blog&p=1)
- V2EX: "自己撸了一个小工具、终于吧 Mac“系统数据”占用干下来了" 35 replies (2026-04-11) — [V2EX t/1205096](https://www.v2ex.com/t/1205096). "mac 系统数据 100 多 G 了，有什么清理的办法吗？" 20 replies (2022-11-06) — [V2EX t/893091](https://www.v2ex.com/t/893091). "大家有用腾讯柠檬清理…吗？" 72 replies (2019-12-09) — [V2EX t/627143](https://www.v2ex.com/t/627143)
- GitHub:
  - [tw93/Mole](https://github.com/tw93/Mole): 72,035 stars, created 2025-09-23, GPL-3.0, "Keep your Mac and PC clean…". The fastest-growing cleaner seen; its developer is Chinese (Tw93).
  - [Tencent/lemon-cleaner](https://github.com/Tencent/lemon-cleaner): 6,342 stars, created 2022-07. Description: "腾讯柠檬清理…重复文件和相似照片的识别、软件的定制化垃圾扫描、可视化的全盘空间分析、内存释放…"
  - [alienator88/Pearcleaner](https://github.com/alienator88/Pearcleaner): 14,927 stars.

**2. WeChat storage and files** (a China-specific workload)
- Zhihu: "微信占用存储空间很大,如何解决?" 213.5万 / 99 / 616 (not Mac-specific); "macbook中微信占据200G内存,怎么清理?" 2,758 — [Sogou→Zhihu "Mac 微信 占用空间 清理"](https://www.sogou.com/sogou?query=Mac+%E5%BE%AE%E4%BF%A1+%E5%8D%A0%E7%94%A8%E7%A9%BA%E9%97%B4+%E6%B8%85%E7%90%86&insite=www.zhihu.com)
- Zhihu, file and chat-history location and migration:
  - "微信电脑版聊天记录怎样迁移?" 173.7万 / 56 / 791 (not Mac-specific).
  - "我就想知道电脑版微信聊天记录照片从哪里找?" 19.8万.
  - "Mac微信2.0聊天记录保存在哪?" 16.4万 / 5 / 23.
  - "怎么样将旧Mac上的微信聊天记录迁移到新Mac上?" 9.1万.
  - "mac版微信3.0的聊天记录备份路径在哪里?" 6.3万.
  - "备份在Mac上的微信记录在哪里看到?" 6.3万.
  - "MacBook 电脑版的微信文件存在哪?" 3.6万.
  - Source: [Sogou→Zhihu "Mac 微信 文件 存储位置 聊天记录"](https://www.sogou.com/sogou?query=Mac+%E5%BE%AE%E4%BF%A1+%E6%96%87%E4%BB%B6+%E5%AD%98%E5%82%A8%E4%BD%8D%E7%BD%AE+%E8%81%8A%E5%A4%A9%E8%AE%B0%E5%BD%95&insite=www.zhihu.com)
- CSDN: "Mac如何找到和清理微信和QQ的缓存文件" 31,095 views (2019-01-16) — [CSDN](https://blog.csdn.net/weixin_33207551/article/details/86502158)
- V2EX: "微信为什么会占很大空间？" 94 replies (2019-09-01) — [t/596870](https://www.v2ex.com/t/596870); "Mac 上微信占有空间 32GB，该怎样清理？" 16 replies (2021-07-23) — [t/791330](https://www.v2ex.com/t/791330)
- Bilibili is weak for this topic: "如何清理Mac版微信聊天记录及缓存或备份记录" 1.5万 (2022-06-08); a 少数派 video "Mac 微信缓存自动清理神器" 3,922 (2019-05-31) — [Bilibili](https://search.bilibili.com/video?keyword=Mac%20%E5%BE%AE%E4%BF%A1%20%E6%B8%85%E7%90%86&order=click)
- WeChat 4.0 for Mac storage paths (second-hand). One Bilibili note gives `~/Library/Containers/com.tencent.xinWeChat/Data/Documents/xwechat_files`. It says 4.0 keeps a compatibility copy that can double usage, and recommends WeChat's own 设置→存储空间→清理历史版本冗余数据. Paths may differ by version — [Bilibili opus](https://www.bilibili.com/opus/1087991288539643906) (via search summary)
- GitHub: [blackboxo/CleanMyWechat](https://github.com/blackboxo/CleanMyWechat) 5,590 stars (MIT, pushed 2026-08-28); [sunnyyoung/WeChatTweak](https://github.com/sunnyyoung/WeChatTweak) 13,828 stars ("首款微信 macOS 客户端撤回拦截与多开").

**3. Windows→Mac newcomer gaps (新手, 剪切, 右键新建)**
- Zhihu:
  - "有什么让你相见恨晚的 MacBook 使用技巧?" 275.6万 / 225 / 3.0万 followers — [Sogou→Zhihu](https://www.sogou.com/sogou?query=Mac+%E5%B7%B2%E6%8D%9F%E5%9D%8F+%E6%97%A0%E6%B3%95%E6%89%93%E5%BC%80+%E7%A7%BB%E5%88%B0%E5%BA%9F%E7%BA%B8%E7%AF%93&insite=www.zhihu.com)
  - "对于苹果电脑 MacBook 新手,如何快速掌握基本操作?" 42.9万 / 27 / 642 — [Sogou→Zhihu "Mac 剪切"](https://www.sogou.com/sogou?query=Mac+%E5%89%AA%E5%88%87+%E7%B2%98%E8%B4%B4+%E6%96%87%E4%BB%B6&insite=www.zhihu.com)
  - "一直使用 Windows 的人如何尽快熟练使用macOS ?" 34.7万 / 40 / 2,339 — [Sogou→Zhihu "Windows 转 Mac"](https://www.sogou.com/sogou?query=Windows+%E8%BD%AC+Mac+%E4%B8%8D%E4%B9%A0%E6%83%AF+%E6%96%B0%E6%89%8B&insite=www.zhihu.com)
  - "用惯了 Windows 的人用 macOS 会有什么不适?" 13.3万 / 64 / 352 — [Sogou→Zhihu "鼠标"](https://www.sogou.com/sogou?query=Mac+%E9%BC%A0%E6%A0%87+%E6%BB%9A%E8%BD%AE+%E6%96%B9%E5%90%91+%E7%9B%B8%E5%8F%8D&insite=www.zhihu.com)
- Cut/paste and new file (Zhihu): "为什么 Mac 没有提供剪切文件功能?" 35.5万 / 28 / 228; "为什么 macOS 没有剪切文件和右键新建文件功能?" 9.3万 / 42 / 467; "Mac系统如何创建新文件夹?" 11.6万; "macbookpro怎么新建word文档?" 1.4万 — [Sogou→Zhihu "Mac 剪切"](https://www.sogou.com/sogou?query=Mac+%E5%89%AA%E5%88%87+%E7%B2%98%E8%B4%B4+%E6%96%87%E4%BB%B6&insite=www.zhihu.com); [Sogou→Zhihu "Mac 右键 新建文件"](https://www.sogou.com/sogou?query=Mac+%E5%8F%B3%E9%94%AE+%E6%96%B0%E5%BB%BA%E6%96%87%E4%BB%B6&insite=www.zhihu.com)
- Bilibili:
  - "【看完秒懂】Mac苹果电脑超详细上手入门指南！" 259万 (2022-02-17).
  - "你真的需要一台苹果电脑吗？丨macOS上手体验" 255.8万 (2021-03-05).
  - "Macbook使用技巧，新手小白快速入门！" 95.4万 (2022-12-07).
  - "笔吧采访④：我找到了最适合用Mac的群体" 69万 (2026-08-02).
  - "第一次用Mac？Windows用户的丝滑过渡指南" 68.8万 (2025-12-29).
  - "【萌新必看】MacBook新手使用教程…适合从Windows转OS X的同学们看" 19万 (2020-08-11).
  - Sources: [Bilibili "Mac 使用技巧"](https://search.bilibili.com/video?keyword=Mac%20%E4%BD%BF%E7%94%A8%E6%8A%80%E5%B7%A7&order=click); [Bilibili "Windows转Mac 新手教程"](https://search.bilibili.com/video?keyword=Windows%E8%BD%ACMac%20%E6%96%B0%E6%89%8B%E6%95%99%E7%A8%8B&order=click)
- Bilibili, right-click: "Mac上最强大的右键菜单工具-超级右键教学视频" 7.2万 (2023-05-24) — [Bilibili "Mac 菜单栏 管理"](https://search.bilibili.com/video?keyword=Mac%20%E8%8F%9C%E5%8D%95%E6%A0%8F%20%E7%AE%A1%E7%90%86&order=click); "超级右键使用演示" 3.2万 (2021-02-20); "QuickDoc v1.1" 1.6万 (2026-05-04) — [Bilibili "Mac 右键 新建文件"](https://search.bilibili.com/video?keyword=Mac%20%E5%8F%B3%E9%94%AE%20%E6%96%B0%E5%BB%BA%E6%96%87%E4%BB%B6&order=click)
- CSDN: "Mac finder 文件剪切方法…" 19,331 (2018-04-28) — [CSDN](https://blog.csdn.net/zjuxiaosheng/article/details/80136176); "苹果电脑入门：复制、粘贴、剪切文件" 17,461 (2022-07-19) — [CSDN](https://blog.csdn.net/macruanjian9527/article/details/125874657); "Mac右键新建文件" 14,028 (2018-02-23) — [CSDN](https://blog.csdn.net/u010231453/article/details/79351499)
- V2EX: "[发码] New File Menu…右键菜单新建文件神器" 56 replies (2016) — [t/259916](https://www.v2ex.com/t/259916); "[分享创造] FileMint：给 Finder 加上右键新建文件" (2026-09-15) — [t/1242028](https://www.v2ex.com/t/1242028). New right-click tools are still being launched in 2026.

**4. Unarchiving RAR/7z**
- Zhihu: "Mac 上最方便的压缩与解压缩软件是什么?" 391.9万 / 95 / 1,396 (an earlier snapshot showed 294.7万 / 81 / 1,363). This is the **largest single problem-specific Mac question found** — [Sogou→Zhihu "Mac 视频…压缩"](https://www.sogou.com/sogou?query=Mac+%E8%A7%86%E9%A2%91+%E6%A0%BC%E5%BC%8F%E8%BD%AC%E6%8D%A2+%E5%8E%8B%E7%BC%A9+%E8%BD%AF%E4%BB%B6&insite=www.zhihu.com); [Sogou→Zhihu "Mac 右键"](https://www.sogou.com/sogou?query=Mac+%E5%8F%B3%E9%94%AE+%E6%96%B0%E5%BB%BA%E6%96%87%E4%BB%B6&insite=www.zhihu.com)
- Bilibili: "如何在Mac上快速压缩和解压文件？怎么打开rar,7z,zip压缩文件？" 8.9万 (2023-04-03); "MacBook怎么解压RAR文件？不花钱方法来了" 3.8万 (2023-02-04) — [Bilibili](https://search.bilibili.com/video?keyword=Mac%20%E8%A7%A3%E5%8E%8B%20rar&order=click)
- The China Mac App Store has many Chinese unarchivers: 解压专家 (released 2022-06, updated 2026-06-24), 解优 (2019-01, updated 2025-12), 解压-RAR ZIP 7Z解压助手纯净版 (2020-10, updated 2026-08), 360压缩大师 (2020-02, last updated 2022-07) — [iTunes Search API, country=cn](https://itunes.apple.com/search?term=%E8%A7%A3%E5%8E%8B%E4%B8%93%E5%AE%B6&country=cn&entity=macSoftware&limit=4)

**5. Mouse and scroll wheel**
- Zhihu: "为什么 Mac 的鼠标滚轮方向与 PC 相反?" 109.5万 / 160 / 522; "为什么macos下的鼠标体验这么差?" 109.5万 / 151 / 397. The identical view counts may be a snippet artefact; treat one of them with caution. Also "罗技鼠标在Mac中滚轮与触控板方向相反怎么办?" 15.4万; "在 macOS 中,如何设置鼠标的滚动方向和触摸板的滚动方向不同?" 6.9万 / 8 / 35 — [Sogou→Zhihu](https://www.sogou.com/sogou?query=Mac+%E9%BC%A0%E6%A0%87+%E6%BB%9A%E8%BD%AE+%E6%96%B9%E5%90%91+%E7%9B%B8%E5%8F%8D&insite=www.zhihu.com)
- CSDN: 57,900 views (2024-10-20) for "mac端电脑解决鼠标滚轮反向与触控板设置问题…" — [CSDN](https://blog.csdn.net/m0_74353785/article/details/143092769); 23,407 (2021-03-15) — [CSDN](https://blog.csdn.net/macxingchendahai/article/details/114818506); "Mac解决鼠标滚轮反方向移动逻辑--Mos" 18,175 (2022-11-26)
- Bilibili: "一款提升Mac鼠标滚轮使用体验的软件-Mos" 3.9万 (2020-03-27); "Win到Mac用不惯鼠标滚轮怎么办？" 2.2万 (2025-02-14) — [Bilibili](https://search.bilibili.com/video?keyword=Mac%20%E9%BC%A0%E6%A0%87%20%E6%BB%9A%E8%BD%AE&order=click)
- GitHub: [Caldis/Mos](https://github.com/Caldis/Mos) 21,685 stars (Chinese description: "让你的滚轮爽如触控板", pushed 2026-10-12); [linearmouse](https://github.com/linearmouse/linearmouse) 7,034 stars.

**6. Screenshots, long capture, OCR and translation**
- Zhihu:
  - "macOS 系统自带的截图快捷键有哪些?" 72.9万 / 28 / 651.
  - "在 Mac OS 下如何进行长 web 页面的截图?" 41.9万.
  - "Mac里有什么可以滚动截屏的软件?" 37.0万 / 28 / 143.
  - "MAC如何截取有效网页长图?" 27.0万.
  - "Mac电脑如何进行长截图?" 26.9万.
  - Source: [Sogou→Zhihu](https://www.sogou.com/sogou?query=Mac+%E6%88%AA%E9%95%BF%E5%9B%BE+%E6%BB%9A%E5%8A%A8%E6%88%AA%E5%9B%BE&insite=www.zhihu.com)
- OCR and translation (mostly cross-platform questions): "OCR 中文识别用哪种软件识别率比较高?" 131.0万 / 164 / 4,657; "有什么能截屏翻译的软件?" 199.3万; "Mac电脑有什么软件可以像win系统里的网易有道词典一样截图翻…" 4.9万 (Mac-specific). Two questions show implausible ratios and may be promoted or SEO questions: "大家用过最好用的截图识别文字软件是什么呀?" 300.2万 views with 6 answers, and "电脑截图文字识别…" 233.0万 with 2 answers — [Sogou→Zhihu](https://www.sogou.com/sogou?query=Mac+%E6%88%AA%E5%9B%BE+%E6%96%87%E5%AD%97%E8%AF%86%E5%88%AB+OCR+%E7%BF%BB%E8%AF%91&insite=www.zhihu.com)
- V2EX: "Mac 开源工具 - 截图并通过在线 OCR API 识别文字" 212 replies (2017) — [t/411218](https://www.v2ex.com/t/411218); "求 macOS 上支持 OCR 的截图软件（类似 qq 截图）" 76 replies (2024-03-21) — [t/1025602](https://www.v2ex.com/t/1025602). The second asks for parity with QQ's screenshot tool.
- Bilibili: "我开发了 Mac 截图软件 Longshot, 对标 Snipaste / Xnip…OCR文字识别、滚动长截图、贴图钉图" 2.9万; iShot's official short clips 0.6–1.5万 (2022) — [Bilibili](https://search.bilibili.com/video?keyword=Mac%20%E6%88%AA%E5%9B%BE%E8%BD%AF%E4%BB%B6%20%E6%BB%9A%E5%8A%A8%E6%88%AA%E5%9B%BE&order=click)
- GitHub: [tisfeng/Easydict](https://github.com/tisfeng/Easydict) 15,024 stars (GPL-3.0; "开箱即用，支持离线 OCR 识别…苹果系统翻译"); [ripperhe/Bob](https://github.com/ripperhe/Bob) 9,753 (issue tracker; the app is closed source and on the MAS); [pot-app/pot-desktop](https://github.com/pot-app/pot-desktop) 19,367 (archived; "跨平台的划词翻译和OCR软件").

**7. Show hidden files**
- Zhihu: "Mac系统如何显示隐藏文件?" 205.2万 / 25 / 254; "在 OS X 中如何设置隐藏文件夹?" 15.5万 — [Sogou→Zhihu](https://www.sogou.com/sogou?query=Mac+%E6%98%BE%E7%A4%BA+%E9%9A%90%E8%97%8F%E6%96%87%E4%BB%B6&insite=www.zhihu.com)
- CSDN: 33,194 views (2023-02-25) — [CSDN](https://blog.csdn.net/universsky2015/article/details/129214799); 24,539 (2017-12-12) — [CSDN](https://blog.csdn.net/qq_36478920/article/details/78783153)

**8. Battery charging**
- Zhihu:
  - "macbook可以一直充电吗?" 134.6万 / 86 / 242.
  - "MacBook Pro 一直保持电源连接,对电池有不好的影响吗?" 63.4万 / 33 / 150.
  - "M1 MacBook 可以一直插着电用吗?" 46.3万 / 91 / 153.
  - "请问MacBook Pro插电用好还是用电池…" 43.5万.
  - "MacBook笔记本一直连着电源,需不需要限制电池充电最多到80%…" 3.3万.
  - Generic laptop question: "笔记本电脑一直连着充电器使用…" 183.8万.
  - Source: [Sogou→Zhihu](https://www.sogou.com/sogou?query=MacBook+%E7%94%B5%E6%B1%A0+%E5%85%85%E7%94%B5+%E4%BF%9D%E6%8A%A4+%E4%B8%80%E7%9B%B4%E6%8F%92%E7%94%B5&insite=www.zhihu.com)
- Bilibili: "要怎么充电才能不伤电池？…" 102.3万 (Mac云课堂, 2019-08-21; the most-played problem-specific video found); "MacBook电池保养终极指南" 11万 (2025-05-05); "MacBook要不要一直插电？" 10.7万 (2026-06-16) — [Bilibili](https://search.bilibili.com/video?keyword=MacBook%20%E7%94%B5%E6%B1%A0%20%E5%85%85%E7%94%B5%20%E4%BF%9D%E6%8A%A4&order=click)
- GitHub: [AlDente](https://github.com/AppHouseKitchen/AlDente-Battery_Care_and_Monitoring) 9,213 stars; [Battery-Toolkit](https://github.com/mhaeuser/Battery-Toolkit) 2,463 (archived 2025).

**9. External drives and NTFS**
- Zhihu:
  - "为什么Macbook Pro 无法识别移动硬盘?" 96.9万 / 63 / 309.
  - "mac插上移动硬盘不显示怎么办?" 12.7万.
  - "mac读取不到移动硬盘?" 10.1万.
  - "Mac OS 无法装载移动硬盘,怎么办?" 1.7万.
  - Source: [Sogou→Zhihu](https://www.sogou.com/sogou?query=Mac+%E7%A7%BB%E5%8A%A8%E7%A1%AC%E7%9B%98+%E4%B8%8D%E6%98%BE%E7%A4%BA+%E6%97%A0%E6%B3%95%E6%8E%A8%E5%87%BA&insite=www.zhihu.com)
- Zhihu, NTFS:
  - "新买的移动硬盘是NTFS的,MAC只能读不能写,怎么让它可读可写?" 20.1万 / 20 / 98.
  - "mac系统搜索不到移动硬盘里的文件" 5.7万.
  - "M1版本的MacBook可以正常使用NTFS的移动硬盘吗?" 5.5万.
  - "有没有什么办法可以在mac 上直接打开ntfs移动硬盘?" 2.0万.
  - Source: [Sogou→Zhihu](https://www.sogou.com/sogou?query=Mac+NTFS+%E7%A7%BB%E5%8A%A8%E7%A1%AC%E7%9B%98+%E8%AF%BB%E5%86%99&insite=www.zhihu.com)
- Bilibili:
  - "【黄杨】移动硬盘格式该怎么选？" 16.7万 (2020).
  - "【Mac必装软件】移动硬盘插入 Mac 不兼容？最好的解决方案来了" 14.1万 (2019-01-17).
  - "这是 Mac 使用移动硬盘最好的方案" 12.5万 (2020).
  - "如何在Mac系统下正确使用移动硬盘" 10.5万 (2017).
  - Source: [Bilibili](https://search.bilibili.com/video?keyword=Mac%20NTFS%20%E7%A7%BB%E5%8A%A8%E7%A1%AC%E7%9B%98&order=click)
- CSDN: "Mac怎么读写NTFS格式?" 22,519 (2018-01-11) — [CSDN](https://blog.csdn.net/u010392459/article/details/79034165)
- Commercial supply on the China MAS. Four actively updated Chinese "NTFS磁盘读写" apps:
  - 赤友NTFS助手 (released 2020-12, updated 2026-04).
  - Max NTFS (2021-07, updated 2026-10-12).
  - Omi硬盘助手 (2021-10, updated 2026-10-02).
  - Pro NTFS磁盘读写管理大师 (2023-08, updated 2026-10-12).
  - Source: [iTunes Search API cn "NTFS"](https://itunes.apple.com/search?term=NTFS&country=cn&entity=macSoftware&limit=4)
- 少数派 2019 article: paid options at the time were Paragon ¥139 and Tuxera ¥99; Mounty was the free option (dated 2019-12-01; 13 likes / 8 comments) — [sspai post 57649](https://sspai.com/post/57649); likes and comments via [sspai API](https://sspai.com/api/v1/article/info/get?id=57649&view=second)
- GitHub: [ntfstool/ntfstool](https://github.com/ntfstool/ntfstool) 4,066 stars; [hoochanlon/Free-NTFS-for-Mac (Nigate)](https://github.com/hoochanlon/Free-NTFS-for-Mac) 2,484 stars.

**10. Menu bar and notch**
- Zhihu: "Mac OS X 下如何整理顶部菜单栏图标?" 127.1万 / 20 / 213 (pre-2016 wording); "mac pro m1顶栏显示不全怎么办?" 1.5万 — [Sogou→Zhihu](https://www.sogou.com/sogou?query=Mac+%E8%8F%9C%E5%8D%95%E6%A0%8F+%E5%9B%BE%E6%A0%87+%E5%A4%AA%E5%A4%9A+%E5%88%98%E6%B5%B7&insite=www.zhihu.com)
- V2EX: "目前发现刘海屏唯一不好的地方，菜单栏很多图标不显示了。" 41 replies (2021-10-29) — [t/811469](https://www.v2ex.com/t/811469); "mac 菜单栏图标太多时会被刘海切掉" 17 (2022-10-08) — [t/885183](https://www.v2ex.com/t/885183)
- Bilibili: "5 款免费又好用的 Mac 菜单栏软件" 11.3万 (2022-04-03) — [Bilibili](https://search.bilibili.com/video?keyword=Mac%20%E8%8F%9C%E5%8D%95%E6%A0%8F%20%E7%AE%A1%E7%90%86&order=click)
- GitHub: [jordanbaird/Ice](https://github.com/jordanbaird/Ice) 29,836; [dwarvesf/hidden](https://github.com/dwarvesf/hidden) 15,232; [boring.notch](https://github.com/TheBoredTeam/boring.notch) 11,093. Search results show a 少数派 page selling Bartender 6 as 正版软件 (2026-09-13) — [Sogou insite=sspai.com](https://www.sogou.com/sogou?query=Mac+%E5%BF%85%E8%A3%85+%E8%BD%AF%E4%BB%B6&insite=sspai.com)

**11. Recording system audio**
- Zhihu: "如何用 QuickTime 对 Mac 屏幕录制,而且录上声音?" 70.3万 / 27 / 218; "这个版本的mac怎么只录制屏幕内部的声音(录屏)?" 17.7万; "如何在MacBook上边上网课边录屏(且有声音)?" 6.0万 — [Sogou→Zhihu](https://www.sogou.com/sogou?query=Mac+%E5%BD%95%E5%B1%8F+%E5%86%85%E5%BD%95+%E5%A3%B0%E9%9F%B3&insite=www.zhihu.com)
- CSDN: "【技巧帖】关于Mac如何内录电脑内部声音" 72,390 views (2018-04-14) — [CSDN](https://blog.csdn.net/lin111000713/article/details/79942250)
- Bilibili: "免费无内购！能录系统声音的Mac录屏软件：Omi录屏专家" 9.1万 (2022-07-18); "录屏后没声音？…内录声音最优雅的解决方案" 7.5万 (2020-01-17) — [Bilibili](https://search.bilibili.com/video?keyword=Mac%20%E5%BD%95%E5%B1%8F%20%E5%86%85%E5%BD%95%E5%A3%B0%E9%9F%B3&order=click)
- V2EX: "MacOS 上最好的商业录屏软件是什么？" 45 replies (2020) — [t/647272](https://www.v2ex.com/t/647272); "mac 上 obs 那些第三方录屏没声音" 26 (2022) — [t/860208](https://www.v2ex.com/t/860208)
- GitHub: [BlackHole](https://github.com/ExistentialAudio/BlackHole) 19,942; [lihaoyun6/QuickRecorder](https://github.com/lihaoyun6/QuickRecorder) 8,734 (Chinese description, AGPL-3.0, last push 2025-06).

**12. External display blur / HiDPI**
- Zhihu:
  - "M1版本的MacBook Air如何开启hidpi?" 31.7万 / 17 / 102.
  - "Mac mini M1外接1080P的显示器字体模糊?" 24.9万.
  - "MacBook Pro 外接 27寸 2k显示器 字体发虚怎么办?" 22.7万.
  - "2020 M1款Mac是否没有任何办法开启外接显示器的2K hidpi?" 12.1万 / 26 / 76.
  - "MBP 外接显示器该选择 2k 还是 4k?" 42.5万 / 40 / 456.
  - Source: [Sogou→Zhihu](https://www.sogou.com/sogou?query=Mac+%E5%A4%96%E6%8E%A5%E6%98%BE%E7%A4%BA%E5%99%A8+%E6%A8%A1%E7%B3%8A+HiDPI&insite=www.zhihu.com)
- Bilibili: "Mac mini接1080p显示器模糊？一键开启Hidpi后终于清晰了！" 11.7万 (2021-10-05); "一键开启HIDPI教程" 11.3万 (2023-05-11); M4 Mac mini HiDPI demo 5.5万 (2024-11-26); "终于搞明白了Mac外接显示器HiDPI的逻辑" 5.1万 (2025-07-31) — [Bilibili](https://search.bilibili.com/video?keyword=Mac%20%E5%A4%96%E6%8E%A5%E6%98%BE%E7%A4%BA%E5%99%A8%20HiDPI&order=click)
- GitHub: [BetterDisplay](https://github.com/waydabber/BetterDisplay) 34,148; [MonitorControl](https://github.com/MonitorControl/MonitorControl) 34,521; [xzhih/one-key-hidpi](https://github.com/xzhih/one-key-hidpi) 11,159 (last push 2025-04).

**13. Printers**
- Zhihu:
  - "苹果mac怎么连接打印机?" 20.1万.
  - "macbook pro电脑如何安装hp laserjet 1020+打印机驱动?" 18.9万.
  - "如何从Mac上连接到windows共享下的打印机,并打印文件?" 15.8万 / 13 / 92.
  - "苹果电脑怎么连接爱普生打印机?" 15.5万.
  - "MacBook m1如何安装打印机驱动…" 8.9万.
  - "苹果笔记本怎样连接夏普网络打印机?" 5.8万.
  - Source: [Sogou→Zhihu](https://www.sogou.com/sogou?query=Mac+%E8%BF%9E%E6%8E%A5+%E6%89%93%E5%8D%B0%E6%9C%BA+%E9%A9%B1%E5%8A%A8&insite=www.zhihu.com)

**14. Window management**
- Zhihu: "Mac 上你是如何多任务切换,窗口管理的?" 71.5万 / 23 / 347 — [Sogou→Zhihu](https://www.sogou.com/sogou?query=Mac+%E7%AA%97%E5%8F%A3+%E5%88%86%E5%B1%8F+%E8%BD%AF%E4%BB%B6&insite=www.zhihu.com)
- GitHub: [Rectangle](https://github.com/rxhanson/Rectangle) 30,117; [yabai](https://github.com/asmvik/yabai) 29,748; [AeroSpace](https://github.com/nikitabobko/AeroSpace) 23,572; [alt-tab-macos](https://github.com/lwouis/alt-tab-macos) 16,422; [Loop](https://github.com/MrKai77/Loop) 11,827.

**15. Clipboard history**
- Zhihu: "Mac 上哪款剪贴板增强软件最好用?" 62.8万 / 31 / 207; "有没有“多次复制一次粘贴”的mac软件?" 1.6万 — [Sogou→Zhihu](https://www.sogou.com/sogou?query=Mac+%E5%89%AA%E8%B4%B4%E6%9D%BF+%E5%8E%86%E5%8F%B2+%E5%B7%A5%E5%85%B7&insite=www.zhihu.com)
- macOS 26 Tahoe built-in clipboard history: ⌘Space then ⌘4. It keeps plain text only, removes items after 8 hours, and excludes passwords. Spotlight's app grid replaces the removed Launchpad — [MacMost](https://macmost.com/how-to-use-the-spotlight-clipboard-history-in-macos-tahoe.html); [office-watch](https://office-watch.com/2025/macos-26-clipboard-history/) (via search summary). A Zhihu answer recommends AppPad as a Launchpad replacement — [Zhihu answer](https://www.zhihu.com/tardis/jm/ans/2004284646234531202) (via search summary)

**16. "已损坏，无法打开" / unidentified developer / download sources**
- Zhihu questions:
  - "Mac安装app时,显示将对电脑造成伤害,您应该将它移到废纸…" 32.8万 / 7 / 13.
  - "下载了一个Mac版steam显示文件已损坏怎么办?" 15.7万.
  - "Mac系统里,打开文件提示“xxxx.xlsx”已损坏" 1.3万.
  - "Mac安装软件总是被阻拦…仍要打开…" 3.6万.
  - "MAC版figma中文版下载后安装提示:文件已损坏" 1.1万.
  - Source: [Sogou→Zhihu](https://www.sogou.com/sogou?query=Mac+%E5%B7%B2%E6%8D%9F%E5%9D%8F+%E6%97%A0%E6%B3%95%E6%89%93%E5%BC%80+%E7%A7%BB%E5%88%B0%E5%BA%9F%E7%BA%B8%E7%AF%93&insite=www.zhihu.com)
- Zhihu column posts (zhuanlan, no view counts shown) on the same fix recur every year from 2018 to 2023. Typical titles: "Mac安装破解版软件/汉化版软件提示'已损坏,打不开'" (2018-12-19), "升级macOS 13 Ventura系统后打开软件显示“已损坏…" (2022-10-27), "建议收藏|Mac软件出现报错/损坏/安装不了全网最全的解决办法" (2022-07-03). The usual advice is `sudo spctl --master-disable` — [Sogou insite=zhihu.com](https://www.sogou.com/sogou?query=Mac+%E5%B7%B2%E6%8D%9F%E5%9D%8F+%E6%97%A0%E6%B3%95%E6%89%93%E5%BC%80&insite=zhihu.com)
- CSDN: four separate 2023 posts with more than 10K views: 17,330 (2023-08-03, [CSDN](https://blog.csdn.net/weixin_44624117/article/details/132093947)), 16,609 (2023-09-30, [CSDN](https://blog.csdn.net/bestpasu/article/details/133424626)), 16,443 (2023-02-14), 10,731 (2023-02-09). Search total 4,466. The top snippet's fix is `sudo xattr -d com.apple.quarantine "<app path>"` — [CSDN search API](https://so.csdn.net/api/v3/search?q=Mac%20%E5%B7%B2%E6%8D%9F%E5%9D%8F%E6%97%A0%E6%B3%95%E6%89%93%E5%BC%80&t=blog&p=1)
- Bilibili:
  - "【教程】保姆级搞定Mac各种装不了、打不开、已损坏、移到废纸篓等问题" 12.4万 (2022-04-09).
  - Two videos from "移花接木Mac资源网", a software-resource site: 6.7万 and 6.3万 (2020) — [Bilibili](https://search.bilibili.com/video?keyword=Mac%20%E5%B7%B2%E6%8D%9F%E5%9D%8F%20%E6%97%A0%E6%B3%95%E6%89%93%E5%BC%80&order=click)
  - On the must-have list: "在 Mac 上装软件，要学会和苹果斗智斗勇" 66.2万 (2022-12-31, the top result); "新手必备！…3个Mac软件下载神站！能免费下到几乎任何主流Mac软件" 13.6万 (2024-09-30); "装了破解软件后的…MacOS安全加固" 10.9万 (2021-12-12) — [Bilibili](https://search.bilibili.com/video?keyword=Mac%E5%BF%85%E8%A3%85%E8%BD%AF%E4%BB%B6&order=click)
- Download-source questions on Zhihu: "电脑哪里下载应用软件最安全?" 11.0万; "MacBook只能安装正版软件吗?" 7.3万; "Mac上的安全工具软件?" 4.1万; "有什么好用的mac软件下载网站吗?" 3.5万; "Mac上从哪下载各种软件?" 3.3万 — [Sogou→Zhihu](https://www.sogou.com/sogou?query=Mac+%E8%BD%AF%E4%BB%B6+%E5%8E%BB%E5%93%AA%E9%87%8C%E4%B8%8B%E8%BD%BD+%E5%AE%89%E5%85%A8&insite=www.zhihu.com)
- V2EX: the developer audience barely asks; topics with 0 replies in 2023 and 2025 — [t/1139269](https://www.v2ex.com/t/1139269)
- GitHub: [alienator88/Sentinel](https://github.com/alienator88/Sentinel) 1,769 stars ("Configure Gatekeeper, remove apps from quarantine and self-sign apps").

**17. Input method**
- Zhihu: "Mac OS X 下,怎么能不让中文输入法反复自动切换中英文输入模…" 8.6万 / 7 / 68; "mac经常自动切换输入法造成困扰,请问如何解决?" 6.9万; "macbook输入法总是英文要切换才成中文输入法" 3.3万 — [Sogou→Zhihu](https://www.sogou.com/sogou?query=Mac+%E8%BE%93%E5%85%A5%E6%B3%95+%E8%87%AA%E5%8A%A8%E5%88%87%E6%8D%A2&insite=www.zhihu.com)
- Bilibili:
  - "【Mac入门教程】 输入法（1/3）" 11.9万 (2018).
  - "苹果Mac电脑上反人类的输入法，你中招了吗？" 10.3万 (2024-06-27).
  - "天下苦Mac中文输入法久矣！！！" 6.2万 (2025-11-10).
  - An AI voice input method, "千问语音输入法怎么玩", 47万 (2026-06-24).
  - Source: [Bilibili](https://search.bilibili.com/video?keyword=Mac%20%E8%BE%93%E5%85%A5%E6%B3%95&order=click)
- CSDN: "解决Mac自动切换输入法" 14,119 (2022-07-29) — [CSDN](https://blog.csdn.net/jiuweihu521/article/details/126048919)
- V2EX: "推荐： SwitchKey - 根据当前窗口自动切换输入法" 33 replies (2019) — [t/560956](https://www.v2ex.com/t/560956); "mac26 基本可以放弃输入法自动切换软件了" (2026-01-04) — [t/1182884](https://www.v2ex.com/t/1182884)
- GitHub: [runjuu/InputSourcePro](https://github.com/runjuu/InputSourcePro) 3,650 stars (open-sourced; repo created 2025-04-25).
- ByteDance has brought 豆包输入法 to macOS — [Toutiao](https://www.toutiao.com/article/7639927419469693459/); [Sohu review](https://www.sohu.com/a/1004547539_120333371) (via search summary)

**18. Android ↔ Mac transfer**
- Zhihu: "安卓手机怎么传图片到Mac??" 8.6万; "有什么软件可以满足安卓端和MacOS之间的文件传输和各种交互.?" 5.6万 / 14 / 23; "如何将安卓手机的照片批量快速传到Mac上?" 4.3万; "安卓手机数据线传输大文件到Macbook大于4GB怎么办?" 2,953 — [Sogou→Zhihu](https://www.sogou.com/sogou?query=%E5%AE%89%E5%8D%93%E6%89%8B%E6%9C%BA+%E4%BC%A0%E6%96%87%E4%BB%B6+Mac&insite=www.zhihu.com)
- Bilibili: "全平台隔空投送来了！！ 安卓、苹果、Mac、PC、Linux互传文件" 27万 (2021-12-10); "安卓隔空投送合集" 8万 (2024-04-23) — [Bilibili](https://search.bilibili.com/video?keyword=%E5%AE%89%E5%8D%93%E6%89%8B%E6%9C%BA%E4%BC%A0%E6%96%87%E4%BB%B6%E5%88%B0Mac&order=click); "再也不用微信、QQ 传文件了!这 6 个免费文件传输神器" 13.2万 (2024-07-25) — [Bilibili](https://search.bilibili.com/video?keyword=Mac%20%E5%BE%AE%E4%BF%A1%20%E6%B8%85%E7%90%86&order=click)
- GitHub: [LocalSend](https://github.com/localsend/localsend) 95,100; [OpenMTP](https://github.com/ganeshrvel/openmtp) 7,582.

**19. Windows-only software (banking, VMs)**
- Zhihu: "苹果MacBook m1 可以用网银U盾吗?" 23.8万 / 6 / 16; "在 Linux 或 Mac 下,如何使用网上银行呢?" 7.3万 — [Sogou→Zhihu](https://www.sogou.com/sogou?query=Mac+%E7%BD%91%E9%93%B6+U%E7%9B%BE+%E7%A8%8E%E5%8A%A1+%E5%8F%AA%E6%94%AF%E6%8C%81Windows&insite=www.zhihu.com); "Mac OS 上虚拟机跑 Windows,性能如何?" 11.4万; "苹果macbook air怎么安装虚拟机运行windows?" 4.3万 — [Sogou→Zhihu](https://www.sogou.com/sogou?query=Mac+%E8%BF%90%E8%A1%8C+Windows+%E8%BD%AF%E4%BB%B6+%E8%99%9A%E6%8B%9F%E6%9C%BA+%E5%8F%8C%E7%B3%BB%E7%BB%9F&insite=www.zhihu.com)

**20. File-format hubs Swivel already touches (not Mac-specific)**
- "如何免费的、完整的把 PDF 转换为 Word?" 1363.6万 / 986 / 5.3万 followers, the largest question seen in this pass; "有没有免费的PDF转Word?" 192.2万; "苹果电脑PDF怎么转换word?" 6,440 — [Sogou→Zhihu](https://www.sogou.com/sogou?query=Mac+PDF+%E7%BC%96%E8%BE%91+%E8%BD%ACWord+%E5%85%8D%E8%B4%B9&insite=www.zhihu.com)
- "如何压缩视频大小?" 275.1万 / 125 / 317; "Mac 上有什么好的视频格式转换软件?" 67.5万 / 30 / 397 — [Sogou→Zhihu](https://www.sogou.com/sogou?query=Mac+%E8%A7%86%E9%A2%91+%E6%A0%BC%E5%BC%8F%E8%BD%AC%E6%8D%A2+%E5%8E%8B%E7%BC%A9+%E8%BD%AF%E4%BB%B6&insite=www.zhihu.com)

#### C. "Must-have apps" hub questions and videos (discovery demand)
- Zhihu:
  - "Mac上有什么实用的必备软件?" 665.7万 / 362 / 1.2万 followers. Earlier snapshots show 555.4万 / 327 and 662.0万 / 360, so it is still growing.
  - "macOS 上有哪些值得推荐的常用软件?" 472.5万 / 323 / 2.4万.
  - "MacBook 有哪些好用的软件?" 415.7万 / 161 / 6,742.
  - "作为资深Mac用户,有哪些你相见恨晚的软件值得推荐?" 176.1万 / 145 / 979.
  - "Mac 下有哪些能极大地提高工作效率的软件?" 82.1万 / 110 / 1.2万.
  - "macOS 上有哪些值得推荐的免费软件?" 62.4万.
  - "一般买麦金塔电脑(Mac)后还须买哪些软件?" 58.4万.
  - "想入手一台Mac,大家觉得有哪些免费软件是必备的吗?" 57.6万.
  - "购买Mac后必须装哪些app?Mac是否需要安装清理内存的app?" 10.1万.
  - Sources: [Sogou→Zhihu](https://www.sogou.com/sogou?query=Mac+%E5%BF%85%E8%A3%85%E8%BD%AF%E4%BB%B6+%E6%8E%A8%E8%8D%90&insite=www.zhihu.com); [Sogou→Zhihu](https://www.sogou.com/sogou?query=macOS+%E7%B3%BB%E7%BB%9F%E6%9C%89%E5%93%AA%E4%BA%9B%E8%BD%AF%E4%BB%B6%E5%8F%AF%E4%BB%A5%E8%A2%AB%E4%BD%A0%E8%AF%84%E4%B8%BA%E5%BF%85%E8%A3%85&insite=www.zhihu.com); [Sogou→Zhihu](https://www.sogou.com/sogou?query=Mac+%E5%89%AA%E8%B4%B4%E6%9D%BF+%E5%8E%86%E5%8F%B2+%E5%B7%A5%E5%85%B7&insite=www.zhihu.com); [Sogou→Zhihu](https://www.sogou.com/sogou?query=Mac+%E8%8F%9C%E5%8D%95%E6%A0%8F+%E5%9B%BE%E6%A0%87+%E5%A4%AA%E5%A4%9A+%E5%88%98%E6%B5%B7&insite=www.zhihu.com)
- Bilibili ("Mac必装软件", most played):
  - 66.2万 (2022-12-31), 65.7万 (2020-05-23), 55.7万 ("购买Macbook之后，一定要改变的设置 & 必装软件！（2024最新）", 2023-12-25), 40.8万 (2019), 38万 (2022), 37.7万 (2022).
  - 35.1万 (TESTV "Mac电脑最值得推荐的17个软件！", 2025-09-09).
  - Raycast video 15.8万 (2023-09-21).
  - "Mac 新手必装的 10 类软件" 11.4万 (2024-01-14).
  - The listing ran to 36 result pages.
  - Source: [Bilibili](https://search.bilibili.com/video?keyword=Mac%E5%BF%85%E8%A3%85%E8%BD%AF%E4%BB%B6&order=click)
- V2EX: "一人推荐一款 mac m5 必装软件" 83 replies (2026-04-15) — [t/1206103](https://www.v2ex.com/t/1206103); "macbook pro 换新…开贴征求必装软件" 35 replies (2026-03-16) — [V2EX API](https://www.v2ex.com/api/topics/show.json?id=1198554)
- GitHub: [jaywcjlove/awesome-mac](https://github.com/jaywcjlove/awesome-mac), a Chinese-curated list of macOS software, has 116,628 stars.

#### D. Taiwan / Hong Kong signals (thin)
- PTT MAC board search: about a dozen NTFS help threads ("[求救] ntfs掛不上去", "Macbook M1無法讀取NTFS外接硬碟", etc.) with 1–9 推 each. "[軟體] 新機必裝軟體？" has 28 推 plus replies with 24 and 8. "已損毀" threads have 0–2 推 and are mostly about system apps, not unsigned downloads. PTT search pages show no year — [PTT NTFS](https://www.ptt.cc/bbs/MAC/search?q=NTFS); [PTT 必裝](https://www.ptt.cc/bbs/MAC/search?q=%E5%BF%85%E8%A3%9D); [PTT 已損毀](https://www.ptt.cc/bbs/MAC/search?q=%E5%B7%B2%E6%90%8D%E6%AF%80)
- Traditional-Chinese 2026 roundups emphasise cleaners (BuhoCleaner, updated 2026-01), AppCleaner for uninstall leftovers, Bartender for the menu bar, clipboard tools and IINA — [Dr.Buho zh-tw](https://www.drbuho.com/zh-tw/how-to/best-mac-apps) (a vendor page); [蘋果仁](https://applealmond.com/posts/4890) (via search summary)

#### E. Other observations
- On 2026-10-21, Sogou result pages for "Mac 已损坏 无法打开" carried AI answer prompts "问问WorkBuddy" and "元宝 正在分析问题". This shows Tencent's agent and chatbot already sit on top of Mac troubleshooting searches — [Sogou](https://www.sogou.com/sogou?query=Mac+%E5%B7%B2%E6%8D%9F%E5%9D%8F+%E6%97%A0%E6%B3%95%E6%89%93%E5%BC%80&insite=zhihu.com)
- Apple's China Mac page lists a "MacBook Neo" alongside MacBook Air and MacBook Pro — [Bing snippet of apple.com.cn/mac](https://www.apple.com.cn/mac/)

### Inferences
- **Ranking for an assistant.** Weighing reach (Zhihu views, Bilibili plays), recency (2023–2026 content still being made) and fit for a local agent, the order to solve is:
  1. A safe storage explainer and cleaner (系统数据, caches, WeChat/QQ data, uninstall leftovers, with a preview before anything is deleted).
  2. A "Windows habits" pack (cut/paste files, new file from right-click, mouse-wheel direction independent of the trackpad, show hidden files).
  3. External-drive diagnosis (why a drive isn't showing, why it won't eject because a process holds it, NTFS is read-only, how to reformat to exFAT safely).
  4. Gatekeeper and "已损坏" triage that checks the signature and offers a safe override for a user-chosen app.
  5. A screenshot, OCR and translation workflow.
  6. A battery-charging advisor.
  7. A display/HiDPI advisor.
  8. A printer setup guide.
  Several of these are diagnostic rather than tool problems: the user doesn't know why something happens. That suits an agent that explains and then acts.
- **Partly solved by macOS.** Clipboard history (macOS 26 Spotlight, though limited to 8 h of plain text), window tiling, and charge optimisation are partly addressed by the system. Unarchiving RAR/7z, NTFS writing, a mouse wheel independent of the trackpad, right-click "new file", and system-audio recording without extra setup are still unaddressed by Apple (inference from the continued 2024–2026 posts and tools above).
- **China-specific.** The "已损坏" cluster appears to be largely driven by apps from non-App-Store and cracked sources. Supporting signals: "破解版/汉化版" titles, resource-site uploaders, "如何激活cleanmymac" at 2.4万, and "装了破解软件后…安全加固". A one-click "remove quarantine" feature would help users but also helps malware. If offered, it should verify the code signature and notarisation and explain the risk. The cluster seems much weaker on PTT (Taiwan), so it looks mainland-specific.
- **Anchors for 2026 relevance.** WeChat data management and Windows-only banking/tax software are distinctly Chinese pain points with no equivalents in English-language Mac utility lists. WeChat cleanup has measurable demand (CSDN 31K, Zhihu location and migration questions 3.6万–19.8万) and a large general WeChat-storage question (213.5万).
- **Two kinds of figures.** Many Zhihu figures are old, cumulative and probably inflated by search traffic. Bilibili's most-played problem videos are mostly 2019–2023. Newer (2024–2026) videos in the same topics typically reach 5–25万, which better reflects current demand.

### Gaps
- No Baidu Index, WeChat Index or Douyin (巨量算数) data (blocked or needing login; not retried per the brief). No Baidu Zhidao or 百度经验 view counts were collected: not attempted, to avoid Baidu verification pages.
- Zhihu creation dates and crawl dates are unknown for all Sogou snapshots. Direct Zhihu URLs were not resolved because Sogou link redirects were not followed.
- 少数派 article view counts are not public (the API returns `views_count: 0`, `show_views_count: false`). Only likes and comments are available, and the sspai site search redirected to /whoops.
- Xiaohongshu (小红书) and Douyin, where many young users ask Mac questions, were not checked: login walls.
- Not verified in this pass: whether macOS 15/26 changed the Gatekeeper override flow (for example, removal of the Control-click "Open" bypass); built-in window tiling in macOS 15; the exact WeChat 4.x storage path on current builds.
- Taiwan and Hong Kong lack quantitative view data. Mobile01 and Dcard were not checked.

## Q2. Which problem-solving utilities are most popular among Chinese Mac users (stars, plays, store presence)?

### Takeaway
Among open-source tools, the China-made or China-favoured utilities with the biggest followings are:
- tw93/Mole (cleaner), 72K stars and created only in 2025-09.
- jaywcjlove/awesome-mac (curated list), 117K.
- Caldis/Mos (mouse scroll), 21.7K.
- pot-desktop (19.4K, archived) and Easydict (15.0K) for OCR and translation.
- WeChatTweak, 13.8K.
- one-key-hidpi, 11.2K.
- Bob, 9.8K (issue repo).
- QuickRecorder (system-audio recording), 8.7K.
- Tencent lemon-cleaner, 6.3K.
- CleanMyWechat, 5.6K.

Global tools that Chinese guides keep recommending also have large followings: LocalSend 95K, Stats 43K, BetterDisplay and MonitorControl 34K each, Rectangle 30K, Ice 30K, Maccy 22K. On the commercial side, one Ningbo developer (宁波上官 / Better365) ships iShot, 超级右键, iOCR and iGet. That portfolio is the closest Chinese analogue to Swivel's bundle. Mac App Store rating counts could not be read for any Mac app.

### Cited Findings
- GitHub stars, 2026-10-21 (gh GraphQL), China-made or with a Chinese description:
  - [tw93/Mole](https://github.com/tw93/Mole) 72,035 (created 2025-09-23).
  - [tw93/Pake](https://github.com/tw93/Pake) 62,237.
  - [jaywcjlove/awesome-mac](https://github.com/jaywcjlove/awesome-mac) 116,628.
  - [Caldis/Mos](https://github.com/Caldis/Mos) 21,685.
  - [pot-app/pot-desktop](https://github.com/pot-app/pot-desktop) 19,367 (archived).
  - [tisfeng/Easydict](https://github.com/tisfeng/Easydict) 15,024.
  - [sunnyyoung/WeChatTweak](https://github.com/sunnyyoung/WeChatTweak) 13,828.
  - [xzhih/one-key-hidpi](https://github.com/xzhih/one-key-hidpi) 11,159.
  - [ripperhe/Bob](https://github.com/ripperhe/Bob) 9,753.
  - [lihaoyun6/QuickRecorder](https://github.com/lihaoyun6/QuickRecorder) 8,734.
  - [Ji4n1ng/OpenInTerminal](https://github.com/Ji4n1ng/OpenInTerminal) 7,032.
  - [Tencent/lemon-cleaner](https://github.com/Tencent/lemon-cleaner) 6,342.
  - [jacklandrin/OnlySwitch](https://github.com/jacklandrin/OnlySwitch) 6,001.
  - [blackboxo/CleanMyWechat](https://github.com/blackboxo/CleanMyWechat) 5,590.
  - [ntfstool/ntfstool](https://github.com/ntfstool/ntfstool) 4,066.
  - [gee1k/uPic](https://github.com/gee1k/uPic) 3,716.
  - [runjuu/InputSourcePro](https://github.com/runjuu/InputSourcePro) 3,650.
  - [hoochanlon/Free-NTFS-for-Mac](https://github.com/hoochanlon/Free-NTFS-for-Mac) 2,484.
  - [lihaoyun6/Topit](https://github.com/lihaoyun6/Topit) 1,534.
  - [wflixu/RClick](https://github.com/wflixu/RClick) 1,090.
  - [Molunerfinn/PicGo](https://github.com/Molunerfinn/PicGo) 27,386 (image-host uploader).
- GitHub stars, global tools recurring in Chinese recommendations:
  - [localsend/localsend](https://github.com/localsend/localsend) 95,100.
  - [iina/iina](https://github.com/iina/iina) 46,855.
  - [exelban/stats](https://github.com/exelban/stats) 42,698.
  - [utmapp/UTM](https://github.com/utmapp/UTM) 36,068.
  - [MonitorControl](https://github.com/MonitorControl/MonitorControl) 34,521.
  - [BetterDisplay](https://github.com/waydabber/BetterDisplay) 34,148.
  - [Rectangle](https://github.com/rxhanson/Rectangle) 30,117.
  - [Ice](https://github.com/jordanbaird/Ice) 29,836.
  - [Karabiner-Elements](https://github.com/pqrs-org/Karabiner-Elements) 22,982.
  - [Maccy](https://github.com/p0deje/Maccy) 22,013.
  - [BlackHole](https://github.com/ExistentialAudio/BlackHole) 19,942.
  - [alt-tab-macos](https://github.com/lwouis/alt-tab-macos) 16,422.
  - [Hidden Bar](https://github.com/dwarvesf/hidden) 15,232.
  - [Pearcleaner](https://github.com/alienator88/Pearcleaner) 14,927.
  - [Loop](https://github.com/MrKai77/Loop) 11,827.
  - [boring.notch](https://github.com/TheBoredTeam/boring.notch) 11,093.
  - [AlDente](https://github.com/AppHouseKitchen/AlDente-Battery_Care_and_Monitoring) 9,213.
  - [Clipy](https://github.com/Clipy/Clipy) 8,930.
  - [OpenMTP](https://github.com/ganeshrvel/openmtp) 7,582.
  - [LinearMouse](https://github.com/linearmouse/linearmouse) 7,034.
  - [Sentinel](https://github.com/alienator88/Sentinel) 1,769.
- Commercial and closed-source Chinese Mac utilities on the China Mac App Store (iTunes Search API, country=cn; release → latest update) — [iTunes Search API](https://itunes.apple.com/search?term=iShot&country=cn&entity=macSoftware&limit=4):
  - Ningbo Shangguan Technology (宁波上官): "iShot-优秀的截图贴图录屏录音OCR翻译取色标注工具" (free, 2019-11-06 → 2026-08-31); iShot Pro (¥78, 2022-03); 超级右键-iRightMouse (free, 2020-02-06 → 2026-09-22); 超级右键专业版 (¥68, 2021-01); iOCR (2023-12); iGet text-selection tool (2024-08).
  - Bob - 翻译和 OCR 工具 (2022-07-02 → 2026-10-13).
  - 腾讯柠檬清理(Lite) (2019-02-14 → 2026-07-07).
  - Xnip (2017-12 → 2026-09-27).
  - Longshot - 截图 & OCR文字识别 (2023-06 → 2026-06).
  - 录屏专家Omi (2021-11 → 2026-06).
  - 全能右键 (2021-02 → 2026-06).
  - PasteNow (2021-03 → 2026-10-13).
  - The NTFS and unarchiver apps listed in Q1.
- A MAS listing "Snipaste-截图贴图OCR取色标注工具" (id 6796914713) appeared on 2026-08-07 under seller "意 田", the same seller name as 全能右键. I could not verify that this is the official Snipaste — [iTunes Search API](https://itunes.apple.com/search?term=Snipaste&country=cn&entity=macSoftware&limit=4)
- Bilibili plays for specific tools:
  - Omi录屏专家 9.1万 (2022) — [Bilibili](https://search.bilibili.com/video?keyword=Mac%20%E5%BD%95%E5%B1%8F%20%E5%86%85%E5%BD%95%E5%A3%B0%E9%9F%B3&order=click).
  - 超级右键 tutorial 7.2万 (2023) — [Bilibili](https://search.bilibili.com/video?keyword=Mac%20%E8%8F%9C%E5%8D%95%E6%A0%8F%20%E7%AE%A1%E7%90%86&order=click).
  - Mos 3.9万 (2020) — [Bilibili](https://search.bilibili.com/video?keyword=Mac%20%E9%BC%A0%E6%A0%87%20%E6%BB%9A%E8%BD%AE&order=click).
  - CleanMyMac review 9.1万 (2021) — [Bilibili](https://search.bilibili.com/video?keyword=Mac%20%E6%B8%85%E7%90%86%E8%BD%AF%E4%BB%B6&order=click).
  - Raycast 15.8万 (2023) and Parallels Desktop 10.7万 (2020) — [Bilibili](https://search.bilibili.com/video?keyword=Mac%E5%BF%85%E8%A3%85%E8%BD%AF%E4%BB%B6&order=click).
- Zhihu "必装" answers (2025–2026 snippets) recommend Maccy (clipboard), Rectangle (split screen), AppPad (Launchpad replacement after macOS 26), The Unarchiver and Keka (".rar cannot be opened natively"), EasyFind, XnView MP, Shottr (scrolling screenshots), IINA, UPDF, 滴答清单. One answer complains about WPS ads and file associations — [Zhihu answers](https://www.zhihu.com/tardis/jm/ans/2004284646234531202), [another](https://www.zhihu.com/tardis/jm/ans/2028485322028130998) (via search summary)

### Inferences
- **The closest commercial competitor.** Better365's portfolio covers screenshot plus OCR, right-click, text selection and recording, which overlaps heavily with Swivel's direction. Its products have been updated in 2026, a sign of a paying Chinese audience for "Windows-habit" utilities.
- **The cleaning wave.** Mole's star growth (72K stars in about 13 months) suggests a 2025–2026 surge of interest in transparent, open-source cleaning. A Swivel storage tool that explains and previews before deleting would ride that wave.
- **What appeals.** The popular Chinese-made tools solve one irritation each with zero configuration: Mos for the wheel, one-key-hidpi for blur, Easydict and Bob for OCR and translation. A bundled assistant should expose each fix as a single action with a clear explanation.

### Gaps
- **Mac App Store rating counts are unavailable.** The iTunes lookup and search APIs return `userRatingCount: 0` for every Mac-only app tested (WeChat, Magnet, iShot, 超级右键, Bob, 赤友NTFS) in cn, hk, tw and us. The apps.apple.com server-rendered pages also show `totalNumberOfRatings: 0`, including Magnet in the US. So this is an API limitation, not a China-specific absence — [lookup example](https://itunes.apple.com/lookup?id=441258766&country=us). Non-zero counts appeared only for universal iOS/Mac listings, such as 迅雷 at 117,844, probably pooled from iOS. These are not usable as Mac demand.
- No first-party download counts were found for iShot, Snipaste, 超级右键 or 赤友. Homebrew counts were not used (developer-skewed; covered in sibling notes).

## Q3. Apple Intelligence in mainland China as of October 2026, and can third-party apps use Apple's on-device Foundation Models there?

### Takeaway
Apple Intelligence is **approved but not yet generally live** in mainland China as of 2026-10-21.
- The CAC registered it on 2026-07-15, with Alibaba Qwen as the China model partner across iOS, iPadOS, macOS and visionOS, and Baidu also involved.
- Tim Cook said on 2026-10-15 that a **beta "later this month"** will bring Writing Tools, Clean Up and Live Translation, without the new Siri.
- Bloomberg-based reports point to a December general release with iOS 27.3, said to cover iPhone, iPad and Mac.
- Apple's support page, dated 2026-09-14, still says the features "will not currently work for supported devices purchased in China mainland".

Because the Foundation Models framework needs Apple Intelligence to be enabled, **third-party on-device Foundation Models should be treated as unavailable to mainland users today**. No source confirms developer access after launch.

### Cited Findings
- 2026-03-31: Apple Intelligence briefly appeared on some China-region iPhones (iOS 26.4+, iPhone 15 Pro or newer, China Apple ID) and was pulled within 6 hours. Gurman called it accidental, and Apple told customers it "还没有在中国大陆正式推出" — [21世纪经济报道](https://www.21jingji.com/article/20260331/herald/6164c8935e6f4f3b0aff5d995e8baa96.html)
- 2026-07-15/16: China's Cyberspace Administration approved Apple's AI services. Alibaba said Qwen will be integrated into Apple Intelligence on iOS, iPadOS, macOS and visionOS for text and image understanding and generation. Baidu confirmed it is working with Apple. No launch date was given — [TechCrunch](https://techcrunch.com/2026/07/15/apple-intelligence-approved-for-launch-in-china-with-alibabas-qwen-ai/); see also [MacRumors](https://www.macrumors.com/2026/07/15/apple-intelligence-cleared-to-launch-in-china/)
- 2026-09-14 (page date): Apple support says "Apple Intelligence features will not currently work for supported devices purchased in China mainland." Devices bought elsewhere also won't work for users in mainland China with a mainland Apple Account region. When it becomes available, compatible devices get "a core set of features", and new features won't come to China while Apple addresses regulatory requirements. Supported languages include Chinese (Simplified) and Chinese (Traditional). Macs need M1 or later — [Apple Support 121115](https://support.apple.com/en-us/121115)
- 2026-10-15: per ifanr, Cook in Shanghai confirmed the first features as a beta at the end of October: Writing Tools, Clean Up in Photos (图像消除) and Live Translation. Siri AI is not included. The article cites Bloomberg's earlier report of a December launch via iOS 27.3 covering iPhone, iPad and Mac — [ifanr 1684431](https://www.ifanr.com/1684431)
- 2026-10-15, Seoul Economic Daily (citing Bloomberg): December 2026 launch with iOS 27.3.
  - Included: Genmoji, notification summaries, Writing Tools and object removal.
  - Excluded: the revamped Siri and Apple's own image generation (Image Playground).
  - Alibaba co-developed China-specific safeguards, including a filter that lets the government approve model updates. Baidu is working on search and Siri-related functions.
  - It mentions iOS only.
  - Source: [Seoul Economic Daily](https://en.sedaily.com/international/2026/10/15/apple-brings-ai-platform-to-china-with-alibaba-safeguards)
- Foundation Models framework: an Apple Developer Forums thread says the on-device model asset requires Apple Intelligence to be enabled. A reply cites the support-doc restriction for China-purchased devices as the likely cause of "Model assets are unavailable". Another poster saw the same error in Europe, so the thread is not conclusive — [Apple Developer Forums 802119](https://developer.apple.com/forums/thread/802119)

### Inferences
- **What Swivel should rely on in China.** For mainland users, Swivel's "agent" features should not depend on Apple's Foundation Models through at least the beta and the December rollout. After launch, Apple's "core set of features" wording plus government-approved model updates suggest a lag before developer-facing on-device LLM access is confirmed for China. Vision OCR, NaturalLanguage and other non-generative frameworks remain the safe local baseline. A pluggable provider (Qwen, DeepSeek or Doubao APIs that are legal in China, or a local model) would cover generative needs.
- **Conflicts between sources.** Some include Genmoji and others exclude image generation. Cook says the beta comes at the end of October, while others say the general launch is in December. Mac coverage appears in Bloomberg/ifanr but not in SEDaily. Some Chinese headlines say "iOS 26.3", which is likely a typo for 27.3 given that iOS 27 shipped in September 2026; this is unconfirmed.

### Gaps
- No Apple developer document explicitly addresses Foundation Models framework availability for China-region users after launch, or whether Qwen-backed features are exposed to third-party apps.
- No confirmation of which Mac features will ship in the China beta.

## Q4. Which Chinese AI desktop assistants and agents exist, what do they advertise on Mac, and how many users do they have?

### Takeaway
Chinese AI has moved onto the desktop in 2026:
- **豆包 (Doubao).** Its Mac app gained GUI "操作电脑" (computer operation) in 2026-09.
- **千问 (Qwen).** Its desktop agent organises local folders on a schedule and connects to Notes, Reminders, Calendar, DingTalk, Feishu and Tencent Docs. QwenWork supports macOS 14+.
- **Tencent.** It has WorkBuddy (an office workbench), QClaw (from the 电脑管家 team, OpenClaw-based, with remote control through WeChat) and a WeChat ClawBot plugin.
- **Kimi Work.** A Mac and Windows beta that mounts local folders, runs Python, drives the browser and runs up to 300 sub-agents; paid.
- **OpenClaw ("养龙虾").** It triggered a Mac mini buying rush in China in early 2026, along with official security warnings.

Scale:
- Mobile AI app MAU (QuestMobile, September 2026): 豆包 4.19亿, 千问 1.58亿, DeepSeek 1.23亿.
- PC client AI apps: only 0.18亿 MAU (QuestMobile, May 2026).
- A separate desktop "AI office" ranking (AI产品榜, July 2026) puts that category above 3000万 MAU, led by WorkBuddy (1115万) and 百度搭子 (674万).

The definitions differ between these sources.

### Cited Findings
- **QuestMobile, September 2026 (third-party estimate).** AI-native app MAU: 豆包 4.19亿 (+143.6% YoY), 千问 15,817万 (+5,076.6% YoY), DeepSeek 12,265万 (−15.2% YoY), then 元宝 in 4th. Top 10 in order: 豆包, 千问, DeepSeek, 元宝, 蚂蚁阿福, 小猿AI, 豆包爱学, 即梦AI, Lovekey, 百度搭子 — [199IT](http://www.199it.com/archives/1858382.html) (figures as relayed in search summary); [Weibo/Sina relay](https://www.sina.cn/weibo/detail/5356062362307749.html). June 2026: 豆包 3.82亿 — [观点网](https://www.guandian.cn/article/20260805/581181.html)
- **QuestMobile 2026 H1 (data as of May 2026).** PC web AI apps 1.72亿 MAU (−22.8% YoY); PC client AI apps 0.18亿 MAU (+20.1% YoY) — [IT之家](https://www.ithome.com/0/976/383.htm) (2026-07-14)
- **AI产品榜 desktop (PC) ranking, July 2026.** AI office desktop MAU passed 3000万. WorkBuddy had 1115.23万 and 百度搭子 674.30万, together about 60%. WorkBuddy connects to Tencent Docs, QQ Mail, Tencent Drive and Tencent Meeting. 百度搭子 breaks down goals, searches, handles files and calls tools across apps — [东方财富 / 上海证券报](https://finance.eastmoney.com/a/202608183844353784.html) (2026-08-18)
- **豆包.** On 2026-09-02 the Mac version added "操作电脑": Doubao understands local GUI interfaces and completes actions without MCP, API, plugins or CLI. It also gained multi-agent parallel work. No permissions or user numbers were given — [IT之家](https://www.ithome.com/0/997/506.htm). 豆包输入法 also launched on macOS — [Toutiao](https://www.toutiao.com/article/7639927419469693459/) (via search summary)
- **千问 desktop agent (ifanr, 2026-08-07).**
  - It auto-organises a desktop folder daily or weekly by reading file contents.
  - It runs scheduled news pulls.
  - It connects to Quark Drive, DingTalk, Feishu, WeCom, Notion and Tencent Docs, plus local Reminders, Notes and Calendar.
  - It produces Word, PPT, web and spreadsheet files.
  - Permission levels are "自动审批" and "全部访问".
  - Models: Qwen3.8-Max and DeepSeek-V4-Flash.
  - Free to try; no user numbers.
  - Source: [ifanr 1674265](https://www.ifanr.com/1674265)
- **千问办公 QwenWork desktop.** Supports macOS 14+ on Apple silicon and Intel; outputs are real local files — [Aliyun help](https://help.aliyun.com/zh/qwenwork/desktop-usage) (via search summary)
- **Tencent.** WeChat "ClawBot" plugin (2026-03-22) links WeChat chat to an OpenClaw instance. It is reachable through Lighthouse, WorkBuddy (Claw settings → WeChat channel) and QClaw (v0.1.15) — [IT之家](https://www.ithome.com/0/931/431.htm). QClaw comes from the 腾讯电脑管家 team, is OpenClaw-based and focuses on remote control of the local PC through WeChat. WorkBuddy comes from the 腾讯云 CodeBuddy team and targets office use — [腾讯云开发者社区](https://cloud.tencent.com/developer/article/2664395) (via search summary)
- **Kimi Work (Moonshot), reported 2026-06-12.**
  - A desktop agent for macOS (Apple Silicon) and Windows, in internal testing.
  - It reads mounted folders, runs Python, and covers PDF manipulation, desktop organisation and reports.
  - WebBridge controls the user's Chrome or Edge through CDP.
  - Up to 300 parallel sub-agents.
  - The app is free; agent features need a plan from $19 to $199 a month.
  - No user numbers.
  - Source: [Decrypt](https://decrypt.co/370954/moonshot-ai-kimi-work-300-agents-desktop)
- **智谱 AutoGLM.** The earlier GLM-PC was "暂时向 Mac 系统开放" in 2024. AutoGLM 2.0 (2025) pairs a cloud phone with a cloud PC — [huxiu](https://www.huxiu.com/article/4718355.html); [jiemian](https://m.jiemian.com/article/13206536.html) (via search summary). Open-source [zai-org/Open-AutoGLM](https://github.com/zai-org/Open-AutoGLM) has 26,443 stars (created 2025-12-08), and ByteDance [UI-TARS-desktop](https://github.com/bytedance/UI-TARS-desktop) has 39,359.
- **OpenClaw.**
  - [openclaw/openclaw](https://github.com/openclaw/openclaw) has 392,644 GitHub stars (created 2025-11-24).
  - Yangcheng Evening News, republished by HK01 on 2026-03-11, reported a "养龙虾" Mac mini buying rush in China. Apple's shipping times stretched beyond two weeks and e-commerce stock ran out. Mac mini M4 sales reportedly rose 300%. People queued at Tencent's Shenzhen building on 2026-03-06 to have OpenClaw installed.
  - MIIT's vulnerability-sharing platform warned of an extremely high risk of information leakage under the default configuration. More than 270,000 instances were reportedly exposed to the public internet — [HK01](https://global.hk01.com/大国小事/60329360/openclaw爆火引安全焦虑-养龙虾-掀apple-mac-mini抢购潮).
  - The MIIT warning was dated 2026-02-05, and CNCERT with an industry alliance later issued guidelines — [ycwb](https://news.ycwb.com/ikimvkhtjj/content_54002772.htm); [China Daily](https://govt.chinadaily.com.cn/s/202603/23/WS69c0dbe6498e23165e06e478/guidelines-released-to-curb-openclaw-security-risks.html) (via search summary).
- **Bilibili demand for Mac plus AI agents:**
  - "一个月6度电、营业额4.5W+，我把工作室的MacMini变成了24小时运作的AI服务器" 45.4万 (2026-08-26).
  - "无头Macbook爆改AI工作站？" 39.1万 (2026-08-15).
  - "OpenClaw保姆级教程：你的AI私人管家来了（原Clawdbot）" 31.2万 (2026-02-02).
  - Source: [Bilibili](https://search.bilibili.com/video?keyword=Mac%20AI%20%E5%8A%A9%E6%89%8B%20%E6%99%BA%E8%83%BD%E4%BD%93&order=click)

### Inferences
- **The capability bar.** The big Chinese assistants now advertise GUI control of the Mac (Doubao), scheduled local file organisation (Qwen), WeChat-driven remote control (Tencent QClaw/ClawBot) and folder-mounted code execution (Kimi). A Swivel "computer assistant" competes against free, well-funded agents on general tasks.
- **Where Swivel can differentiate.** The problem list in Q1 points to narrow, safe, native fixes that big agents handle poorly or riskily: storage forensics with a preview before deletion, NTFS and drive diagnosis, Gatekeeper triage, a mouse/HiDPI/menu-bar "Windows habits" pack, and WeChat data management. Swivel needs no Accessibility or Screen Recording permission and stays local.
- **Trust is an opening.** The security warnings around OpenClaw (MIIT, CNCERT, exposed instances) and the "buy a separate Mac mini to isolate it" behaviour show that Chinese users are actively worried about high-permission agents. A clearly scoped, permission-light assistant with reversible actions is a positioning opportunity.
- **Desktop scale.** PC-client AI usage is small relative to mobile (0.18亿 against more than 4亿 for Doubao mobile in QuestMobile's framing). Desktop agent adoption is growing but not yet mass-market. The AI产品榜 figure of more than 3000万 uses a broader or different method and conflicts with QuestMobile's.

### Gaps
- No Mac-specific user numbers for any Chinese desktop assistant. The PC figures do not split Mac from Windows.
- Not researched in this pass: Manus (desktop/My Computer features and its ownership status in 2026), MiniMax Agent desktop, 360 纳米AI desktop, 腾讯元宝 desktop features, and Kimi or 智谱 2026 Mac updates beyond the above.
- No first-party Doubao statement on which macOS permissions "操作电脑" needs (presumably Accessibility and Screen Recording, unverified).
