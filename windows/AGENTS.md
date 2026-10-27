# Swivel for Windows — notes for coding agents

`windows/` is the Windows port of Swivel: the same ten tools as the macOS app (Wheel, Clothesline, Text Capture,
File Shelf, Image Presets, Quick Actions, Batch Rename, PDF Pages, QR & Barcodes, App Actions), rewritten in C# / WPF for Windows 10 (2004+) and 11. The macOS sources in
`../Sources/Swivel` are the specification: keep the behaviour, wording, numbers (spring responses, delays, sizes) and
file-naming rules the same unless Windows makes that impossible, and say so in a comment where it differs.

The owner is a Chinese-speaking user; talk to them in Chinese. The app UI is English with translations.

## Build, test, run

```powershell
./build.ps1          # dotnet publish → windows/build/Swivel/Swivel.exe (framework-dependent, win-x64)
./test.ps1           # MSTest checks in Swivel.Tests
```

Requires the .NET 10 SDK. `Swivel.csproj` targets `net10.0-windows10.0.19041.0`, which projects the built-in WinRT
APIs (Windows.Media.Ocr, Windows.Graphics.Imaging, Windows.Media.Transcoding / Editing, Windows.Data.Pdf,
Windows.Media.FaceAnalysis, DataTransferManager).

Several agents may build at once: always pass your own `--artifacts-path` (a folder in your scratch directory), e.g.
`dotnet build windows/Swivel/Swivel.csproj --artifacts-path <scratch>/art` and
`dotnet test windows/Swivel.Tests --artifacts-path <scratch>/art --filter ClassName~Images`.

Command-line test modes (same as the Mac app): `--run <convert|tools|actions|apps|codes> "<item>" <file>…`,
`--edit <crop|compress|edit|bg|redact|metadata|annotate|collage|trim> <file>…`, `--recognize <image>`,
`--tool <text|shelf|quick|presets|feel|clothesline|style|colors|rename|pdf|qr|library|apps|launcher> [file]…`, `--demo`, `--frames`, `--feedback <check|play>`. `SWIVEL_SETTINGS=<file>`
points the app at a separate settings file; `SWIVEL_TAB=<0-15>` opens a settings page; `SWIVEL_QR_LIBRARY_DIR=<dir>` isolates the QR library. Do not start the app without
arguments while testing: that starts the tray icon, the drag watcher and global hotkeys on the owner's desktop.

## Code map (`windows/Swivel`)

| Area | Files |
| --- | --- |
| Entry, command-line modes, tray menu, login item | `Core/Program.cs`, `Core/AppHost.cs` |
| Settings (JSON in `%APPDATA%\Swivel\settings.json`, same keys as the Mac app's UserDefaults), feature switches | `Core/Prefs.cs`, `Core/Features.cs`, `Core/ImagePreset.cs` |
| What each menu offers, actions, output naming | `Core/Catalog.cs` (`FileKind`, `Catalog`, `FileActions`, `Paths`, `Shell`) |
| Localization | `Core/Localization.cs` (`L(...)`, printf-style `%@ %d`), `Core/Strings.Mac.cs` (generated from the Mac table by `tools/port_strings.py` — never edit), `Core/Strings.<Module>.cs` (Windows-only rows, one file per module) |
| Springs, frame driver, reduced motion | `Core/Feel.cs` |
| Win32 helpers, share sheet | `Core/Native.cs` |
| Converters | `Converters/` — images, video/audio, archives, documents, PDF |
| Drag detection, menu window and its five templates, feedback sounds, toast | `Wheel/` |
| Editors | `Editors/` |
| Settings window (PowerToys-style sidebar) | `Settings/` |
| Shelf, Quick Actions, Text Capture, Image Presets, shared tool UI | `Tools/` |
| Clothesline | `Clothesline/` |
| Batch Rename, PDF Pages (1.4) | `FileTools/` |
| QR & Barcodes: reader/encoder, scan window, QR Library and its 3D scenes (1.4) | `Qr/` |
| App Actions and the keyboard tool wheel (1.4) | `Apps/` |
| Tests | `../Swivel.Tests/` |

## Rules of the house

- **No third-party dependencies in the app.** Use .NET, WPF, the WinRT APIs above, Win32 (P/Invoke with
  `LibraryImport`) and tools that ship with Windows (`%SystemRoot%\System32\tar.exe`). WinForms is referenced only
  for `NotifyIcon` and silent printing. The test project uses Microsoft's MSTest.
- **Code-only WPF**: no `.xaml` files (they slow parallel builds and the port reads closer to the AppKit code).
- **Outputs go next to the original file.** Never create a new folder for results (multi-page outputs are
  `name 1.png`, `name 2.png`…). Never overwrite: use `Paths.Output` / `Paths.Unique`. Write to a temp file in the
  same folder and move it into place, so a failure never leaves a half-written result.
- **Text:** wrap every user-facing string in `L("English")`. Reuse the Mac app's English wording where it still fits
  (it is already translated in `Strings.Mac.cs`); add any new string with 简体中文 / 繁體中文 / 日本語 to your
  module's `Strings.<Module>.cs`. `CoreTests.EveryUserFacingStringIsTranslated` fails on a missing row. Use Windows
  words: File Explorer (not Finder), taskbar / notification area (not menu bar), Ctrl (not ⌘), Recycle Bin (not
  Trash), "on your PC" (not "on your Mac"), Preview (not Quick Look). Menu item titles stay English internally —
  they are the IDs saved in Customize; draw them with `WheelItem.DisplayTitle` / `Catalog.DisplayTitle`.
- **Settings** live in `Prefs` (raises `Prefs.Changed`). Feature on/off goes through `Feature.IsOn()` / `SetOn()`.
- **Look:** the app uses WPF's Fluent theme (`ThemeMode.System`), so standard controls look like Windows 11 and
  follow light/dark. Icons are Segoe Fluent Icons glyphs (font family `"Segoe Fluent Icons, Segoe MDL2 Assets"`)
  in place of SF Symbols. Use theme resources / `SystemColors` (accent: `SystemParameters.WindowGlassColor` or
  `SystemColors.AccentColor*` keys) rather than fixed colours, except where the Mac app draws its own palette
  (menu templates, clothesline).
- **Feel (手感):** anything that moves is a `Spring` driven by a `FrameDriver` (Core/Feel.cs), never a timer or a
  fixed storyboard; retarget springs instead of restarting them. Colour and opacity are critically damped; only
  geometry may overshoot a little (damping ≥ 0.6). Feedback starts on the next frame, presses respond on
  mouse-down, and real work never waits for an animation. Sounds go through `Feedback` only. Every motion path
  checks `Motion.Reduced`. Windows has no public trackpad haptics API, so haptics are not ported.
- **Panels that must not steal focus** (menu, toast, shelf, quick actions, clothesline) use
  `Native.MakeNonActivating`. Screen coordinates from Win32 are physical pixels; convert with the window's DPI.
- **Drops from Explorer**: always report `DragDropEffects.Copy` (or `Link`), never `Move` — Explorer deletes the
  source after a move.
- Don't copy code from GPL projects; reimplement ideas. Tendedero's name and icon are not licensed.

## Not verified on real hardware yet

See the bottom of `README.md` in this folder for what has been checked automatically and what still needs a person.
