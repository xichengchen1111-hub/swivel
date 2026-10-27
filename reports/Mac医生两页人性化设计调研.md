# Mac 医生：“变慢或发热”和“清理文件”的人性化设计调研（2026-10-27）

## 学了什么

- **ux-heuristics**（wondelai/skills，MIT）：Krug 的“别让我思考”、文字砍掉一半；Nielsen 十条启发式；能撤销比弹窗确认更好；暗模式清单（偷偷替用户勾选、假紧迫感、吓唬人）。
- **apple-design**（emilkowalski/skills，MIT）：Apple 八原则里的掌控感、对用户负责、简单而非极简、细节可靠（“列表抖动、位置乱跳会被当成粗糙”）；反馈分四种：状态、完成、警告、错误。
- 苹果 HIG：常见且能撤销的操作，即使是删除，也不要弹窗；进度要具体（“第 N 个，共 M 个”）。
- NN/g：确认弹窗用多了大家会不看就点；会动的界面对手慢、读得慢的人不友好；渐进展开不要超过两层。

## 同类产品怎么做（详细来源见文末）

### 实时内存和处理器

| 产品 | 做法 |
| --- | --- |
| 活动监视器 | 默认 5 秒刷新；“退出”只在安全时关闭，“强制退出”另外选；内存压力用绿/黄/红说明 |
| CleanMyMac 菜单 | 每秒刷新，但**鼠标放在列表上时停止刷新**，方便点“退出”；占内存超过 20% 的应用标黄 |
| iStat Menus | 把一个应用的所有子进程合成一行 |
| Windows 任务管理器 | 颜色深浅表示占用高低；刷新速度可选，**可以暂停** |
| Edge / Chrome | 先用温和的办法（让标签页休眠），正在放声音、共享屏幕、填表的不动 |
| App Tamer | 把占用换算成用户在意的后果（“能多用 20–30 分钟电池”） |

### 清理

| 产品 | 做法 |
| --- | --- |
| macOS / iOS 储存空间 | 先给“建议”，再给完整清单；应用可以按“上次使用”排序；默认什么都不帮你开 |
| Files by Google | 卡片式分类；“垃圾文件”一个按钮，大文件/重复文件要用户自己选；重复文件标出“原件”并保留 |
| Windows 清理建议 | 分类；“下载”文件夹默认不碰，要用户自己选 |
| CleanMyMac | “一键”默认全选，但每个分类可以展开复查；卸载器把 6 个月没打开的应用归为“未使用” |
| DaisyDisk | 用户自己把东西拖进收集器；删除前有 5 秒可以取消 |

### 反复出现的好做法

1. 先说一句人话的状态，再给数字。
2. 按“够不够高”提示（颜色、高/中/低），而不是只摆原始数字。
3. 按应用算，不按进程算。
4. 列表在用户要点的时候保持不动（鼠标在上面就暂停）。
5. 先给温和的操作（正常退出），不默认强制。
6. 先给建议，再给完整清单。
7. **只预选“本来就安全”的类别；个人文件要用户自己选。**
8. 按钮上写清动作和大小：“移到废纸篓（3.2 GB）”。
9. 优先能恢复的删除（废纸篓 + 撤销）。
10. 长时间扫描要显示具体进度和已找到的部分结果。

### 要避免的

- “释放内存”式的假紧迫感；只给强制退出；列表在鼠标下面重新排序；
- 每个操作都弹“确定吗？”；把个人文件预先勾上；含糊的“加载中…”；
- 用“垃圾”这类吓人的词；按名字粗略匹配残留。

## 设计方案

### 变慢或发热

1. **最上面一句话状态**，用 macOS 自己的内存压力定颜色：绿“内存够用”、黄“内存偏紧，下面几个应用占得最多”、红“内存不足，建议退出一些应用”。
2. 图表保留（圆环 + 两分钟曲线）。
3. **实时应用列表**（每 2 秒）：每个应用一行（含它的辅助进程），显示内存和处理器；占用高的标黄/标红并写“占用很高”。
   - 鼠标在列表上或已经选中应用时**暂停刷新**，列表上方写“已暂停更新”；离开后继续。
   - 排序：处理器最高 / 内存最多。
   - 当前正在用的应用（最前面的那个）标“正在使用”。
4. **操作**：勾选 → “退出所选（约释放 X）”，正常退出（应用会先请你保存），不提供强制退出。
5. macOS 自己的进程放在“更多详情”里，只读。

### 清理文件

1. **最上面**：“可以释放约 X”+ 一个主按钮“移到废纸篓（N 项，X）”。
2. **按类别分组**，每组一行：复选框 + 名称 + 大小 + 一句后果说明，点开看每一项：
   - 3 个月没打开的应用（大小、上次打开日期；连同以它命名的设置一起卸载）
   - 已删应用留下的文件
   - 缓存（含浏览器缓存）——“应用需要时会自己重建”
   - 安装包和磁盘映像
   - 重复文件——“每个文件保留最早的一份”
   - 项目构建产物——“下次构建会重新生成，可能需要联网”
3. 用**复选框**代替现在的“点选行”（现在点一下别的行会把勾选全部取消，容易误操作）。
4. **扫描进度**：每组自己显示“正在查找…”，找完一组显示一组。
5. 清理后显示：“已移到废纸篓：N 项，X。清倒废纸篓后才释放空间。[撤销] [打开废纸篓]”。
6. 用词：不说“垃圾”，说“可能不再需要”。

## 来源

活动监视器 https://support.apple.com/guide/activity-monitor/view-memory-usage-actmntr1004/mac ·
https://support.apple.com/kb/PH19650 · https://support.apple.com/guide/activity-monitor/actmntr2224 ·
https://support.apple.com/guide/activity-monitor/quit-a-process-actmntr1002/mac ·
CleanMyMac https://macpaw.com/support/cleanmymac/knowledgebase/ram-intensive-apps ·
https://macpaw.com/support/cleanmymac/knowledgebase/smart-care ·
https://macpaw.com/support/cleanmymac-x/knowledgebase/uninstaller ·
iStat Menus https://bjango.com/help/istatmenus7/memory/ · App Tamer https://www.stclairsoft.com/AppTamer/ ·
Edge https://support.microsoft.com/en-US/edge/learn-about-performance-features-in-microsoft-edge ·
Chrome https://support.google.com/chrome/answer/12929150 ·
Windows https://www.elevenforum.com/t/change-task-manager-real-time-update-speed-in-windows-11.9595 ·
https://support.microsoft.com/en-us/windows/manage-drive-space-with-storage-sense-654f6ada-7bfc-45e5-966b-e24aded96ad5 ·
macOS 储存 https://support.apple.com/guide/mac-help/free-up-storage-space-on-your-mac-sysp4ee93ca4/mac ·
iOS https://support.apple.com/en-us/108429 ·
Files by Google https://support.google.com/files/answer/9713869 · https://support.google.com/files/answer/10434652 ·
Android https://developer.android.com/topic/performance/app-hibernation ·
DaisyDisk https://daisydiskapp.com/guide/4/en/DeletingFiles/ ·
HIG https://developer.apple.com/design/human-interface-guidelines/alerts ·
https://developer.apple.com/design/human-interface-guidelines/progress-indicators ·
NN/g https://www.nngroup.com/articles/confirmation-dialog/ · https://www.nngroup.com/articles/progress-indicators/ ·
https://www.nngroup.com/articles/auto-forwarding/ · https://www.nngroup.com/articles/progressive-disclosure/ ·
Skills https://github.com/wondelai/skills（ux-heuristics）· https://github.com/emilkowalski/skills（apple-design）
