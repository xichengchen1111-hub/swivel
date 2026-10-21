# Swivel — notes for coding agents

Swivel is a native macOS menu-bar app (Swift, AppKit, no third-party dependencies) that bundles ten tools, each switched on or off on its own:

- **Wheel** — start dragging a file in Finder, tap a modifier key, and a menu pops up under the pointer. Drop the file on an option to convert it, open an editor (crop, compress, adjust, background, redact, metadata, annotate, collage, trim), or act on it (AirDrop, copy, move, copy path, Terminal, shelf, hang on the clothesline). Five menu templates: Wheel, List, Bookshelf, Sticky Notes, Clothesline.
- **Clothesline** — a rope under the menu bar. New screenshots first wait as a card in the bottom-right corner (like the system thumbnail); drag the card up or click it to hang it. Moving the pointer to the screen’s top edge immediately lowers the rope. Prints can be clicked (copy), double-clicked (open), held (Quick Look) or dragged away. The rope and prints are animated with a small physics model.
- **Text Capture** — local Vision OCR from a selected image, with editable text and explicit copy.
- **File Shelf** — a session-only list of file references with multiple selection and dragging out together.
- **Image Presets** — saved image size, format, quality and metadata recipes; outputs beside originals.
- **Quick Actions** — searchable keyboard panel for enabled tools and explicitly supplied files.

Optional tools are opt-in and default off. Each can be used independently of Wheel.

A native Windows port (C# / WPF) lives in `windows/`; it has its own notes in `windows/AGENTS.md`. When a Mac
feature or string changes, port it there too (regenerate the shared strings with `python windows/tools/port_strings.py`).

The owner is a Chinese-speaking user; talk to them in Chinese. The app UI is English with translations (see Localization).

## Build, install, run

```bash
./build.sh                                   # swift build -c release → build/Swivel.app (ad-hoc signed)
./test.sh                                   # Swift Testing checks (tools, springs, hit-testing, sounds)
```

Quit the installed app normally before replacing it, so screenshot takeover can restore the user's settings. Keep a backup of the previous application when installing a development build.

Requires macOS 15+ and the Command Line Tools (Swift 6 toolchain, Swift 5 language mode). Liquid Glass uses macOS 26 APIs behind `#available`.

## Testing without a real drag

- `Swivel --run <convert|tools|actions|apps|codes> "<item title>" <file>...` — runs one menu item headlessly and prints `OK`/`FAIL`. A wrong title lists the available ones.
- `Swivel --demo <convert|tools|actions|apps|codes> <file> [script] [style]` — shows a menu at the pointer (for screenshots and recordings). The script is one hover index (`2`) or steps run every 0.35 s: `0,1,2,n,1,p1` (`n` next page, `pN` drop on item N without running it). The style applies to this run only.
- `Swivel --frames <wheel|list|shelf|notes|clothesline> <out dir> [light|dark]` — renders the menu's animations offscreen, 60 frames a second, to PNGs: open, a slow arc, a fast flick, back to the hub (cancel), a page turn and a drop. Prints mean and slowest draw time; `SWIVEL_FRAMES_SCALE=2` renders at Retina size, `SWIVEL_FRAMES_LOG=1` lists slow frames.
- `Swivel --feedback <check|play>` — `check` reports whether the trackpad actuator and the sounds are ready; `play` runs every haptic and sound once. To confirm haptic requests without feeling them: `/usr/bin/log stream --level debug --predicate 'eventMessage CONTAINS "AppKitActuateWithID"'` (the drop press goes through the actuator directly and doesn't appear there).
- `Swivel --edit <crop|compress|edit|bg|redact|metadata|annotate|collage|trim> <file>...` — opens one editor directly.
- `Swivel --recognize <image>` — prints OCR text without touching clipboard or screenshot settings.
- `Swivel --tool <text|shelf|quick|presets|rename|pdf|qr|library|apps|launcher> [file]...` — isolated UI preview; skips the app delegate and background watchers, but registers the Quick Actions shortcut. Enables the optional tools in the executable's defaults domain, so use an unbundled binary or a separate validation bundle. `SWIVEL_PREVIEW_APPEARANCE=light|dark` changes only the preview's appearance.
- `SWIVEL_TAB=<0-15>` opens a settings page (general, shortcuts, style, customize, clothesline, overview, textCapture, shelf, imagePresets, quickActions, feel, colors, batchRename, pdfPages, qrCodes, appRouting); `SWIVEL_SHOW_ROPE=1` lowers the rope at launch.
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
| Personal tuning | `InteractionSettings.swift` (bounded, persisted per-area motion, sound styles, typography), `FeelPane.swift` (Feel & Text settings, safe previews); `AppTypography` scales native controls idempotently, skips field editors and system file/color/font panels, never exported annotation fonts |
| Menu appearance / rope colours | `MenuAppearance.swift` (neutral light/dark surfaces, opaque sRGB persistence, rope/peg presets and independent four-colour template palettes), `RopeColorsPane.swift` (rope/peg selectors), `TemplateColorsPane.swift` (Wheel → Colors with per-template presets, wells and preview); `Prefs.clotheslineColors` updates existing print pegs and the resting rope immediately as well as the menu template |
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

### Version 1.4.3 (build 15): 3D QR scenes and screenshot drag paths (2026-10-21)
- Installed to /Applications and ~/Desktop after a normal quit (screenshot settings verified restored); previous apps and both defaults domains backed up in ignored build/previous-1.4.2-before-1.4.3/. Not committed or pushed yet.
- `QRGarden.swift` replaces the flat tree flip: a SceneKit voxel scene (Tree, Bouquet, Island × three seasons) built from the code's modules. Seen straight from above, `cover`/`ground` top faces are exactly the code (dark modules dark, light modules pale); `structure` (trunks, stems, paper, boats, filler leaves over light modules) fades out during the camera turn. Colours are baked per face with a constant lighting model, so the top view keeps exact colours. Camera elevation/azimuth and fade run on one `Spring` via `FrameDriver`.
- Checks: `everyTemplateTopSurfaceMatchesTheCode` (contrast per module), `SWIVEL_FILE_ACTIONS_RENDERS=<dir>` renders every template/season and decodes the top view with Vision, `SWIVEL_QR_FILMSTRIP=<dir>` renders the transition and times builds (release: ~18 ms for a 2,000-byte code).
- `SavedQRCode.template` decodes with a default, so older libraries load.
- Screenshot card drags: the file is moved to the usual screenshot folder before the drag starts, so apps that read a dropped file later (chat attachments) keep a valid path. Hanging it afterwards still moves it, unless another app accepted the drop.

### Version 1.4.2 screenshot takeover ownership (2026-10-21)

- User reported new screenshots no longer reaching the rope. Live state: only Desktop 1.4.1 running, Clothesline and takeover flags true, but system location/thumbnail overrides and original-settings backup absent. Re-enabling takeover through native UI restored Incoming + thumbnail off immediately. Two same-ID copies could share/clear the original backup and let an exiting process undo another's takeover.
- AppInstance acquires a process-held advisory file lock before installing the delegate/watchers. Launch dates are unreliable for executables invoked directly, so the process list is only an activation aid. A second normal launch forwards reopen/activation to that instance and exits. CLI/isolated --tool modes remain separate. On reopen/activation/wake or preferences changes, a started Clothesline reconciles its enabled screenshot takeover; start/stop are idempotent, watcher/hotkey/observer cleanup is complete.
- ScreenshotSession owns screenshot settings with a per-process token. A stale owner cannot restore or delete a current session's backup; restart can adopt a legacy/crashed session while retaining original values. Healthy checks do not rewrite settings. Orphaned Incoming without a backup recovers to system defaults rather than leaving screenshots hidden on quit. Restore does not overwrite an external screenshot destination change. Unit checks inject an isolated in-memory system store and temporary defaults domain, never change the actual screenshot settings.
- 137 tests in nine suites pass: seven new cases cover unset/custom restoration, two-process ownership, legacy backups, lost-setting repair/orphaned Incoming, external destination changes and deterministic activation selection and exclusive file-lock release/reacquisition. Incoming cards expose a native accessibility press to hang the screenshot. Existing calm-motion and file hand-off checks remain.
- Back up 1.4.1 apps/preferences in ignored build/previous-1.4.1-before-1.4.2, normally quit before replacement and verify screenshot restoration; install signed 1.4.2 (build 14) to Applications/Desktop. Keep the owner's feature flags and screenshot originals. Final installed duplicate-process regression: start Applications, restore system screenshot defaults to simulate the fault, invoke the Desktop executable. The second process exited successfully; exactly one app remained, its owner token and original backup were unchanged, and Incoming/thumbnail-off recovered automatically. A generated blank input in Incoming was handed back to Desktop unchanged after its unattended-card timeout. Normal quit restored original screenshot settings. Native physical card dragging and a system-triggered screenshot were not verified by the UI tool; do not claim them. No remote push requested.

### Version 1.4.1 app entry points, shortcut conflicts and empty QR library (2026-10-21)

- User reported missing Add App on Customize, non-working Space, and an unusable empty QR library. Added Add Application and Manage Applications directly on Customize's Apps mode, and Open QR Library on Codes mode. App customization now matches any configured extension in the selected category instead of a single representative JPG/DOCX; regression covers PNG-only and TXT-only actions.
- Found the owner's enabled macOS symbolic shortcut 61 uses Control+Option+Space for input-source switching. Carbon registration alone incorrectly reported ready. CopySymbolicHotKeys now blocks enabled system conflicts without changing OS preferences. Default is Control+Option+J; an explicit older choice stays saved and shows the conflict. Native settings explain the full key combination and Space paging. Recheck on activation, retry failed app registrations, and support local AppKit key events while Swivel has focus (no new permissions).
- The root tool wheel's QR Library now opens the window directly. Empty library has central New/Import controls and instructions; a blank tree is neither drawn nor exposed as an active button. Save/import clears search so newly saved items are visible. Empty imports retain an explicit no-code message; duplicate imports do not add undo steps or rewrite the library.
- 130 automated checks pass, including enabled system conflict matching, complete key modifiers/repeats, empty library actions and saved tree visibility, category-specific app extensions. Native isolated UI: Add App from Customize saved TextEdit/TXT; app wheel selected TextEdit and a fixture opened in its real window; QR New/Save/tree-to-code/export PNG passed, export decoded with Vision, saved QR and app settings survived preview restart. Test data stays under build/file-actions-qa/fixes-library, separate from the owner's library. Space correctly showed system conflict; J opened the wheel when the Swivel window had focus. Computer-use key injection in another app does not confirm a physical Carbon hotkey; do not overclaim that check.
- Back up 1.4.0 apps/preferences before installing 1.4.1 (build 11), quit production normally and preserve screenshot restoration. No remote push requested.

### Version 1.4.0 file tools, QR library and shortcut wheels (2026-10-21)

- Native AppKit Batch Rename (combined rules, preview, identity checks, exclusive moves, rollback and undo), PDF Pages (ranges, reorder, rotation, export), QR/barcode reading with Vision, and App Actions each have independent switches. No third-party dependencies or new privileged permissions. Batch rename intentionally changes names only after explicit confirmation; other exports never overwrite originals.
- QR library persists only explicitly saved decoded content under Application Support/Swivel/QRCodes (JSON mode 600). Add/edit, collections/search, remove, change-specific undo, import, QR copy/export. Original native isometric tree illustration has three seasons and flips to a valid plain code; inspired by Tree.ICQR, no assets/source copied. It does not preserve imported QR artwork.
- App rules use stable IDs, per-app enable/extension filters, a native installed-app popup and optional application picker. Open files uses NSWorkspace; copy-files delivery updates the clipboard only after successful launch. Official Codex/Claude deep links open a draft with escaped local file references, never send or upload attachments. Claude's file parameter is unsupported. Codex may resolve to /Applications/ChatGPT.app through its codex URL scheme.
- Five drag menus: convert/tools/actions/apps/codes. Default keys Shift/Command/Control/Option/fn; migration preserves explicit older assignments. Independently enabled Ctrl+Option+Space (J/W alternatives) clickable tool wheel supports arrows, Return, Space paging, Esc, apps/codes submenus, and a Show button in Shortcuts. It honors the selected template and reduced motion.
- Automated: 125 checks in eight suites, including file collision/rollback/stale identity, PDF exact content/order/rotations/source preservation, real Vision decoding of rotated/Unicode/multiple QR images, library persistence/corruption/undo, injected app delivery/failures/clipboard safety, deep links, key migration and feature gates. Six custom tree/QR faces rendered; all three flat faces decoded successfully. Native UI verified rename+undo, PDF selected-page export with rotation (read back via PDFKit), library add/delete/undo and tree/code switch, app selection+txt filter, clickable wheel → saved code → selected library tree. Global hotkey registration is checked; synthetic computer-use key injection is not a physical hotkey test.
- Release 1.4.0 (build 10), ad-hoc signed. Previous Applications/Desktop apps and preferences are backed up in ignored build/previous-1.3.3-before-1.4.0/. Normally quit production first, verified screenshot settings restored, then updated both copies. The owner's four requested features and shortcut wheel were enabled through native UI; legacy preferences remain. Test QR data stays in ignored build/file-actions-qa/library, separate from the real library. No remote push requested.
- New file implementations: BatchRename.swift, PDFPages.swift, QRCodes.swift, QRLibrary.swift, QRLibraryWindow.swift, AppRouting.swift, FileActionsPanes.swift, FileToolWindow.swift, ShortcutWheel.swift; tests in FileActionsTests.swift. --tool previews skip screenshot takeover/watchers; set SWIVEL_QR_LIBRARY_DIR for isolated library data. Actual Finder drags and physical keyboard shortcuts still need hands-on validation; do not describe injected opener tests as real assistant attachment uploads.

## Not verified yet

- Real Finder drags for the Bookshelf / Sticky Notes / Clothesline menu templates.
- Editors' Apply buttons were never clicked end to end (windows were only opened and screenshotted).
- Clothesline: dragging the corner card up to the rope, persistence of hung prints, and whether the calmer swing feels right.
- The original feature switches and language picker were checked by screenshot, not clicked.

### Versions 1.3.2–1.3.3 template palettes and drop readiness (2026-10-21)

- Wheel → Colors (`SWIVEL_TAB=11`, isolated `--tool colors`) edits independent Bookshelf / Sticky Notes palettes: Soft Neutrals, Misty Coast, Sage Garden, Warm Linen, Rose Clay, Lavender Haze, four custom repeating colours, and a separate shelf rail colour. Per-template reset and automatic persistence (`templates.colors.v1`) preserve the other template, active style, rope colours and feel settings. Dark variants and automatic text contrast cover custom near-black and bright colours. Changing the rail alone preserves the book colours.
- A supported incoming media drag makes the real rope system blue, 5 pt wide with a light blue halo; leaving/cancelling restores the chosen idle colour. The same cue remains under Reduce Motion. Source-operation filtering prevents a false ready cue for unsupported drags. Terminal colour frames are rendered exactly so the cache threshold cannot leave a residual blue tint.
- Verified 97 Swift Testing checks, including palette separation/persistence/reset, native custom-panel opening, 4-language large-text layout, minimum 4.5:1 label contrast, supported-drop filtering, and blue/restore behaviour with normal and reduced motion. Rendered all six palettes on both templates in light/dark plus rope readiness and motion filmstrips. Native preview UI selected Sage Bookshelf / Lavender Notes and kept both after restart.
- Final signed release is 1.3.3. 1.3.2 was installed briefly during the task, then the requested blue drop cue was added. Backups of 1.3.1 and 1.3.2 apps/preferences are in ignored `build/previous-1.3.1-before-1.3.2/` and `build/previous-1.3.2-before-1.3.3/`. Applications and Desktop now have matching signed 1.3.3 binaries, the existing preferences/screenshot behavior are preserved, and only Applications is running on Wheel → Colors. The owner subsequently requested synchronization to origin/main. Physical cross-app screenshot-card dragging still needs a person; do not present the simulated readiness checks as a real Finder drag.

### Version 1.3.1 quiet templates and rope colours (2026-10-21)

- Bookshelf, Sticky Notes and Clothesline menus now use almost-neutral light/dark card surfaces, fine selection borders, gentler shadows and system text. Wood, gold bands, saturated paper and handwritten lettering were replaced with satin grey, small paper tabs and quiet typography. Existing motion and hit-target dimensions are retained (notes gain rounded corners).
- Clothesline settings offer independent rope and peg colours: Silver, Graphite, Sand, Sage, Mist Blue, Dusty Rose, or a native custom colour well. Choices persist in `clothesline.colors.v1`, update existing prints and a resting rope, and apply to the menu template too. Reset affects only these colours. `--tool style` and `--tool clothesline` are isolated settings previews.
- Verified 92 Swift Testing checks; all three templates rendered at Retina scale in light/dark (opening, hover, page, confirmation); native UI preset selection and independent rope/peg changes; preview restart retained Sage rope and Sand pegs. A new test verifies saved colour data, custom opacity sanitization, immediate layer updates on an existing print, reset, and large-text settings layout.
- Signed release 1.3.1 installed to Applications and Desktop after normal quit and screenshot-restoration check. Previous apps/preferences are in ignored `build/previous-1.3.0-before-1.3.1/`. Both installed binaries match the release, previous feature/feel/language/style/preset settings and screenshot behavior are preserved. Only Applications is running, on the Clothesline colour settings page. No push requested.

### Version 1.3.0 personal tuning (2026-10-21)

- Pulled origin/main (already current at `28a7fc7`) and reviewed the 1.2.0 feel/review fixes before adding Feel & Text. Five presets, independent menu/rope/tools speed and bounce, three sound styles with relative level and optional selection sounds, gentle haptics, four text sizes and bold text. Preferences persist in `interaction.v1`; presets preserve text choices and the legacy channel switches.
- Verified: 90 Swift Testing checks, release build and signatures; tuning-page renders in English / Simplified Chinese / Traditional Chinese / Japanese at all four text sizes in light and dark; real native UI selection of Quiet, Extra Large, a custom per-area speed and local replay; restart preserved the preview settings. Tests use the isolated executable/defaults domain; preview bundle `local.swivel.tuning-preview` skips screenshot watchers.
- Normally quit old installed, desktop and stale demo instances, verified screenshot settings restored, backed up apps/preferences under ignored `build/previous-1.2.0-before-1.3.0/`, and installed signed 1.3.0 to Applications and Desktop. Both binaries match the release build. Only the Applications copy is running, on Feel & Text. Existing feature/language/feedback/image-preset preferences and screenshot behavior match the pre-update snapshot. The Desktop File Provider can re-add empty FinderInfo metadata; removing it lets the strict bundle check pass. The desktop executable itself verifies independently and matches the release build.
- Physical trackpad intensity, subjective sound comfort and the older hands-on checks below remain for a person. No remote push was requested.

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
