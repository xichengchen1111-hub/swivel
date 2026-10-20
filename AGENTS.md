# Swivel — notes for coding agents

Swivel is a native macOS menu-bar app (Swift, AppKit, no third-party dependencies) that bundles six tools, each switched on or off on its own:

- **Wheel** — start dragging a file in Finder, tap a modifier key, and a menu pops up under the pointer. Drop the file on an option to convert it, open an editor (crop, compress, adjust, background, redact, metadata, annotate, collage, trim), or act on it (AirDrop, copy, move, copy path, Terminal, shelf, hang on the clothesline). Five menu templates: Wheel, List, Bookshelf, Sticky Notes, Clothesline.
- **Clothesline** — a rope under the menu bar. New screenshots first wait as a card in the bottom-right corner (like the system thumbnail); drag the card up or click it to hang it. Moving the pointer to the screen’s top edge immediately lowers the rope. Prints can be clicked (copy), double-clicked (open), held (Quick Look) or dragged away. The rope and prints are animated with a small physics model.
- **Text Capture** — local Vision OCR from a selected image, with editable text and explicit copy.
- **File Shelf** — a session-only list of file references with multiple selection and dragging out together.
- **Image Presets** — saved image size, format, quality and metadata recipes; outputs beside originals.
- **Quick Actions** — searchable keyboard panel for enabled tools and explicitly supplied files.

The four additions are opt-in and default off. Each can be used independently of Wheel.

The owner is a Chinese-speaking user; talk to them in Chinese. The app UI is English with translations (see Localization).

## Build, install, run

```bash
./build.sh                                   # swift build -c release → build/Swivel.app (ad-hoc signed)
./test.sh                                   # Swift Testing checks (tools, springs, hit-testing, sounds)
```

Quit the installed app normally before replacing it, so screenshot takeover can restore the user's settings. Keep a backup of the previous application when installing a development build.

Requires macOS 15+ and the Command Line Tools (Swift 6 toolchain, Swift 5 language mode). Liquid Glass uses macOS 26 APIs behind `#available`.

## Testing without a real drag

- `Swivel --run <convert|tools|actions> "<item title>" <file>...` — runs one menu item headlessly and prints `OK`/`FAIL`. A wrong title lists the available ones.
- `Swivel --demo <convert|tools|actions> <file> [script] [style]` — shows a menu at the pointer (for screenshots and recordings). The script is one hover index (`2`) or steps run every 0.35 s: `0,1,2,n,1,p1` (`n` next page, `pN` drop on item N without running it). The style applies to this run only.
- `Swivel --frames <wheel|list|shelf|notes|clothesline> <out dir> [light|dark]` — renders the menu's animations offscreen, 60 frames a second, to PNGs: open, a slow arc, a fast flick, back to the hub (cancel), a page turn and a drop. Prints mean and slowest draw time; `SWIVEL_FRAMES_SCALE=2` renders at Retina size, `SWIVEL_FRAMES_LOG=1` lists slow frames.
- `Swivel --feedback <check|play>` — `check` reports whether the trackpad actuator and the sounds are ready; `play` runs every haptic and sound once. To confirm haptic requests without feeling them: `/usr/bin/log stream --level debug --predicate 'eventMessage CONTAINS "AppKitActuateWithID"'` (the drop press goes through the actuator directly and doesn't appear there).
- `Swivel --edit <crop|compress|edit|bg|redact|metadata|annotate|collage|trim> <file>...` — opens one editor directly.
- `Swivel --recognize <image>` — prints OCR text without touching clipboard or screenshot settings.
- `Swivel --tool <text|shelf|quick|presets> [file]...` — isolated UI preview; skips the app delegate and background watchers, but registers the Quick Actions shortcut. Enables the four new tools in the executable's defaults domain, so use an unbundled binary or a separate validation bundle. `SWIVEL_PREVIEW_APPEARANCE=light|dark` changes only the preview's appearance.
- `SWIVEL_TAB=<0-9>` opens a settings page (general, shortcuts, style, customize, clothesline, overview, textCapture, shelf, imagePresets, quickActions); `SWIVEL_SHOW_ROPE=1` lowers the rope at launch.
- Note: `.build/release/Swivel` run directly has no bundle ID, so it reads a different defaults domain than the installed app (`local.swivel.app`).
- A process launched from the terminal can't take focus while the user is in another app; capture its window with `screencapture -l <windowID>`.

## Code map (`Sources/Swivel`)

| Area | Files |
| --- | --- |
| App entry, ◎ menu, launch/reopen | `main.swift`, `AppDelegate.swift` |
| Feature switches, settings storage | `Features.swift`, `Prefs.swift` |
| Settings window (PowerToys-style sidebar) | `BundleWindow.swift` (split view, pages, headers), `AppWindow.swift` (window, panes) |
| Drag detection | `DragWatcher.swift` — polls the drag pasteboard and modifier flags (no Accessibility permission needed) |
| Menu + drop target, five templates | `Wheel.swift` (controller, panel), `WheelView.swift` (layout/drawing per `MenuStyle`) |
| What each menu offers | `Catalog.swift` — per `FileKind` and `WheelMode`; `Prefs.arrange` applies the user's order/hidden set |
| Converters | `Images.swift`, `Media.swift` (video/audio), `Documents.swift` (docs, PDF, archives) |
| Editors | `Editor.swift` (base window), `CropEditor.swift`, `PhotoEditors.swift`, `AnnotateEditor.swift`, `CollageEditor.swift`, `TrimEditor.swift` |
| Clothesline | `Clothesline.swift` (controller, rope physics view, incoming card), `ClotheslineSupport.swift` (Quick Look, screenshot takeover, ⌃⌥T hotkey) |
| Text Capture | `TextCapture.swift` — file-only Vision OCR, review/copy window |
| File Shelf | `Shelf.swift` — temporary references, multi-select and drag |
| Image Presets | `ImagePresets.swift` — persisted model, ImageIO output, form sheet |
| Quick Actions | `QuickActions.swift` — search panel, file input, configurable Carbon shortcut |
| New settings and shared UI | `AdditionalPanes.swift`, `ToolUI.swift` |
| Feel: springs, frame driver, haptics, sounds | `Feel.swift` — `Spring` (response/damping, solved analytically, retargetable), `FrameDriver` (display link), `Motion.reduced`, `Feedback` (the moments that get feedback), `Haptics`, `TrackpadActuator` (private MultitouchSupport, loaded with dlopen), `Sounds` (synthesised once into Caches, played as system sounds) |
| Misc UI | `Toast.swift`, `Localization.swift` |
| Automated checks | `Tests/SwivelTests/` — `NativeToolsTests`, `FeelTests` (springs, wheel hit-testing, sounds, strings table), `ClotheslineFeelTests`, `ToolsFeelTests`, `EditorsFeelTests` (snapping, detents), `SettingsFeelTests`; run with `test.sh` |

## Rules of the house

- **No third-party dependencies.** Use system frameworks and the CLI tools that ship with macOS (`afconvert`, `ditto`, `tar`, `zip`).
- **Outputs go next to the original file.** Never create a new folder for results (multi-page outputs are `name 1.png`, `name 2.png`…). Never overwrite: use `Paths.output` / `Paths.unique`.
- **Text:** wrap every user-facing string in `L("English")` and add the 简体中文 / 繁體中文 / 日本語 translations to `Strings.table`. Menu item titles stay English internally — they are the IDs saved in Customize — and are translated only when drawn. Saved preset action IDs are `preset.<stable id>`; resolve their display titles through `Catalog.displayTitle` / `WheelItem.displayTitle`, so renaming does not lose menu customization.
- **Settings** live in `Prefs` (UserDefaults, posts `Prefs.changed`). Feature on/off goes through `Feature.isOn`.
- **Screenshot takeover** changes `com.apple.screencapture` (`location`, `show-thumbnail`). Always keep `ScreenshotTakeover.restore()` working: it runs when the feature is turned off and on quit.
- **Motion:** the clothesline's oscillators live in `RopeTuning` (pure, tested) and `RopeView.advance` runs on a `FrameDriver` with a fixed 1/240 s step. The owner asked for *gentle* motion — keep swings small and well damped; `ClotheslineFeelTests` enforces damping ratio ≥ 0.6, ≤ 2 visible swings and settling within 1.5 s. `SWIVEL_ROPE_FRAMES=<dir> ./test.sh` renders rope and card filmstrips for review.
- **Feel (手感):** anything that moves is a `Spring` driven by a `FrameDriver`, never a fixed tween or a `Timer`; retarget springs instead of restarting them. Colour and opacity are critically damped; only geometry may overshoot a little (damping ≥ 0.6), and the wheel's numbers live in `WheelView`'s `Tune`. Feedback starts on the next frame, presses respond on mouse-down, and the real work never waits for an animation. Fire haptics and sounds only through `Feedback` (one owner per moment, same frame as the visual): detents for snapping or a new item, a press for a commit, nothing on hover-off. Every motion path checks `Motion.reduced`. Research and sources: emilkowalski/skills `apple-design` (MIT), Apple's HIG on haptics, Kando/Fly-Pie (MIT) for pie-menu numbers.
- **Shelf and Quick Actions** are non-activating `ToolPanel`s: they take typing without bringing Swivel to the front (and route ⌘C/V/X/A/Z/W themselves while another app owns the menu bar). Editors open at `.floating` and drop to `.normal` once key, so they still appear when activation is refused during a Finder drop.
- **Private haptics:** `TrackpadActuator` is the only private API. It is resolved with dlopen/dlsym (never linked), skipped when System Settings turns trackpad haptics off, and can be disabled with `defaults write local.swivel.app privateHaptics -bool NO`.
- **Licensing:** Tendedero (github.com/alejandrobujan/tendedero) inspired the clothesline; its code is MIT but its name and icon are not licensed — don't use them. Don't copy code from GPL projects into this MIT-style codebase. When borrowing an idea, reimplement it.
- Don't edit sibling projects (`../atelier-focus`, `../clipline`); other sessions own them.
- Commit messages end with a `Co-Authored-By` line for the agent that wrote the change. Push to `origin main` (private repo `xichengchen1111-hub/swivel`) when the owner asks.

## Not verified yet

- Real Finder drags for the Bookshelf / Sticky Notes / Clothesline menu templates.
- Editors' Apply buttons were never clicked end to end (windows were only opened and screenshotted).
- Clothesline: dragging the corner card up to the rope, persistence of hung prints, and whether the calmer swing feels right.
- The original feature switches and language picker were checked by screenshot, not clicked.

### Version 1.2.0 feel overhaul (2026-10-21)

- Every feature's interaction feel was reworked from a research pass (feel/motion skills, pie-menu and macOS haptics research, per-feature audits) and then a 10-lens code review whose findings were each checked by three verifiers; all confirmed findings were fixed. Summary of the rules is under *Feel* above.
- Verified automatically: 82 Swift Testing checks (`./test.sh`); `--frames` renders of all five menu templates (mean draw 1.4–3.8 ms at Retina scale); `--feedback check` (actuator usable, 8 sounds registered); haptic requests seen reaching WindowServer (`AppKitActuateWithID`) from a background process; a real on-screen `--demo` recording of the wheel.
- Installed 1.2.0 to `/Applications/Swivel.app` and the desktop copy after quitting both 1.1.1 processes normally (screenshot settings restored, then re-taken by the new build). Previous apps and preferences are in ignored `build/previous-1.1.1-before-1.2/`.
- Still needs a person: how the haptics and sounds feel during a real Finder drag; the Force Click press during a drag; whether Finder's icon visibly flies into the chosen sector; the rope and card on real hardware (hover wake-up, card click while pinned, click-to-hang flight); editor snapping and detents with a real trackpad; Shelf / Quick Actions keyboard focus after the global shortcut.

### Version 1.1.1 interaction tuning (2026-10-20)

- Touching the top two points of a screen starts lowering the rope without a hover delay (50 ms polling). Moving away rearms the gesture, so hiding it while at the edge does not immediately reopen it.
- Incoming cards wait 2.5 seconds while idle. Hovering, pressing or dragging pauses dismissal; leaving or ending a cancelled/copy drag grants one more second. Entry/exit use short eased animations; old animation completions cannot hide a newer card.
- Existing eight tests and the release build pass. Installed 1.1.1 after a normal quit and verified screenshot-setting restoration. The previous app/preferences are in ignored `build/previous-1.1/`.
- Tested the real incoming-file watcher with a generated image: it moved to the user's original screenshot directory after 2.95 seconds, including watch delay, with identical bytes. The generated test image was then removed.
- Updated both `/Applications/Swivel.app` and the owner's desktop copy to 1.1.1. Quit the stale desktop 1.0 process normally, verified screenshot restoration, then launched only the Applications copy. Both binaries match the release build; signatures pass and preferences are unchanged. The old desktop app/preferences are backed up in ignored `build/desktop-update-backup/`.
- Physical top-edge movement and hover/drag timing still need subjective manual checking; automated tests do not measure the feel of the gesture.

### Version 1.1 validation (2026-10-20)

- Eight automated tests pass via `./test.sh`; the release app builds and is ad-hoc signed.
- In a separate validation bundle, created a preset, checked invalid-size feedback, saved it and applied it through the file picker. Verified a 600 × 350 result beside the original.
- Opened Text Capture with an English/Chinese fixture, verified the recognized text and clicked Copy Text. Toggled Text Capture off/on and verified the file-picker button became disabled/enabled.
- After unlocking, checked Shelf multi-selection, Space/Quick Look and Show in Finder; checked Quick Actions search, Up/Down, Return, and disabled-state behavior. Visually checked Overview and Quick Actions in light appearance, plus the new tools in dark appearance.
- Remaining manual checks: Shelf multi-file drag into/out of Finder and the physical global shortcut. Shortcut registration reports ready and its automated conflict tests pass, but the UI automation's simulated global shortcut did not open the panel. Do not report physical-key or cross-app drag behavior as tested.
- Before updating the installed app, quit version 1.0 normally and verified restoration of both screenshot location and thumbnail preference. The previous app and preferences are backed up under the ignored `build/previous-1.0/`.
- Installed 1.1 in `/Applications/Swivel.app`, checked that its binary matches the release build, and reopened Overview. Existing feature/language preferences and active screenshot behavior match the pre-update values; all four additions remain off.
- Existing editor Apply and clothesline drag checks above remain outstanding.

## Research task and selected implementation

The research deliverable is `RESEARCH.md`. The owner subsequently selected its first four proposals for implementation, with individual switches and native macOS styling. That instruction supersedes the original research-only stopping point below. See `README.md` for the implemented behavior and testing entry points.

Original research brief:

The owner wants ideas for what to add next, grounded in what similar open-source projects on GitHub do well.

1. **Survey** GitHub for open-source macOS utilities close to Swivel's two features, for example:
   - drag-and-drop / radial or pop-up menus and file actions (e.g. radial window managers such as Loop, drop shelves, Finder extensions);
   - screenshot tools and screenshot organisers (Tendedero and others), clipboard managers;
   - "bundle of small tools" apps (e.g. Microsoft PowerToys, which is Windows but sets the bar for the settings UX);
   - menu-bar managers (the owner's menu bar is crowded and the notch hid Swivel's icon once).
   Check each repo's licence, activity and star count; prefer well-maintained, permissively licensed projects.
2. **Write `RESEARCH.md`** in this repo with:
   - a table of the repos looked at: link, licence, what it does, what Swivel could learn from it;
   - a ranked list of 8–12 feature proposals. For each: the user-facing behaviour, why it fits Swivel, which repo(s) it came from, rough effort (S/M/L), and any macOS permission it would need (Accessibility, Screen Recording, Full Disk Access — Swivel currently needs none, which is worth keeping);
   - a short "not recommended" list with reasons.
3. **Don't implement** anything yet. Present the top proposals to the owner in Chinese and let them pick.
