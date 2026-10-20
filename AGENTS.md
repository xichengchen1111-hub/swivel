# Swivel — notes for coding agents

Swivel is a native macOS menu-bar app (Swift, AppKit, no third-party dependencies) that bundles two tools, each switched on or off on its own:

- **Wheel** — start dragging a file in Finder, tap a modifier key, and a menu pops up under the pointer. Drop the file on an option to convert it, open an editor (crop, compress, adjust, background, redact, metadata, annotate, collage, trim), or act on it (AirDrop, copy, move, copy path, Terminal, shelf, hang on the clothesline). Five menu templates: Wheel, List, Bookshelf, Sticky Notes, Clothesline.
- **Clothesline** — a rope under the menu bar. New screenshots first wait as a card in the bottom-right corner (like the system thumbnail); drag the card up or click it to hang it. Resting the pointer on the menu bar lowers the rope. Prints can be clicked (copy), double-clicked (open), held (Quick Look) or dragged away. The rope and prints are animated with a small physics model.

The owner is a Chinese-speaking user; talk to them in Chinese. The app UI is English with translations (see Localization).

## Build, install, run

```bash
./build.sh                                   # swift build -c release → build/Swivel.app (ad-hoc signed)
pkill -x Swivel; rm -rf /Applications/Swivel.app && cp -R build/Swivel.app /Applications/ && open /Applications/Swivel.app
```

Requires macOS 15+ and the Command Line Tools (Swift 6 toolchain, Swift 5 language mode). Liquid Glass uses macOS 26 APIs behind `#available`.

## Testing without a real drag

- `Swivel --run <convert|tools|actions> "<item title>" <file>...` — runs one menu item headlessly and prints `OK`/`FAIL`. A wrong title lists the available ones.
- `Swivel --demo <convert|tools|actions> <file> [hover index] [style]` — shows a menu at the pointer for 6 s (for screenshots).
- `Swivel --edit <crop|compress|edit|bg|redact|metadata|annotate|collage|trim> <file>...` — opens one editor directly.
- `SWIVEL_TAB=<0-5>` opens a settings page (general, shortcuts, style, customize, clothesline, overview); `SWIVEL_SHOW_ROPE=1` lowers the rope at launch.
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
| Misc UI | `Toast.swift`, `Shelf.swift`, `Localization.swift` |

## Rules of the house

- **No third-party dependencies.** Use system frameworks and the CLI tools that ship with macOS (`afconvert`, `ditto`, `tar`, `zip`).
- **Outputs go next to the original file.** Never create a new folder for results (multi-page outputs are `name 1.png`, `name 2.png`…). Never overwrite: use `Paths.output` / `Paths.unique`.
- **Text:** wrap every user-facing string in `L("English")` and add the 简体中文 / 繁體中文 / 日本語 translations to `Strings.table`. Menu item titles stay English internally — they are the IDs saved in Customize — and are translated only when drawn.
- **Settings** live in `Prefs` (UserDefaults, posts `Prefs.changed`). Feature on/off goes through `Feature.isOn`.
- **Screenshot takeover** changes `com.apple.screencapture` (`location`, `show-thumbnail`). Always keep `ScreenshotTakeover.restore()` working: it runs when the feature is turned off and on quit.
- **Motion:** the clothesline uses a hand-rolled spring/pendulum step (`RopeView.step`). The owner asked for *gentle* motion — keep swings small and well damped.
- **Licensing:** Tendedero (github.com/alejandrobujan/tendedero) inspired the clothesline; its code is MIT but its name and icon are not licensed — don't use them. Don't copy code from GPL projects into this MIT-style codebase. When borrowing an idea, reimplement it.
- Don't edit sibling projects (`../atelier-focus`, `../clipline`); other sessions own them.
- Commit messages end with a `Co-Authored-By` line for the agent that wrote the change. Push to `origin main` (private repo `xichengchen1111-hub/swivel`) when the owner asks.

## Not verified yet

- Real Finder drags for the Bookshelf / Sticky Notes / Clothesline menu templates.
- Editors' Apply buttons were never clicked end to end (windows were only opened and screenshotted).
- Clothesline: dragging the corner card up to the rope, persistence of hung prints, and whether the calmer swing feels right.
- Feature switches and the language picker were checked by screenshot, not clicked.

## Current task: research new features (for Codex)

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
