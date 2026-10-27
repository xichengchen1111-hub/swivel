# Swivel for Windows

The Windows port of Swivel: the same zero-click, offline file converter and the same ten tools as the macOS app,
rebuilt natively in C# / WPF for Windows 10 (2004 or later) and Windows 11. It lives in the notification area (tray),
uses only what ships with Windows and .NET, and needs no administrator rights.

| Key while dragging | Menu |
| --- | --- |
| Shift | **Convert** formats |
| Ctrl | File **tools**: Crop, Compress, Edit, Add Background, Redact, Metadata, Annotate, Collage, Trim, Merge, resize, rotate, mute, split… |
| Alt | File **actions**: Share (Windows share sheet), Add to Shelf, Hang, Copy, Move, Copy Path, Terminal |
| Space | **App Actions**: open the files in your chosen apps, or prepare a Codex / Claude Code draft (while App Actions is on) |
| Tab | **QR Library**: the library and your saved codes (while QR & Barcodes is on) |

Start dragging any file in File Explorer, keep holding the mouse button, tap a key, and drop the file on the menu
that opens under the pointer. Tap the same key again for the next page; drop on the centre (or press Esc) to cancel.
Results are saved next to the original and never overwrite anything; click the toast to show them in File Explorer.
The keys can be changed in Settings › Wheel › Shortcuts (Windows has no Command or fn key that survives a drag, and
the Windows key opens Start, so the menus use Shift, Ctrl and Alt, plus Space and Tab, which Windows ignores during a
drag). Keys chosen before 1.4 are kept; the two new menus take whichever keys are still free.

**Tool wheel** (off by default; Settings › Wheel › Shortcuts): **Ctrl + Alt + J** (or Ctrl + Alt + Space / W) opens a
clickable wheel at the pointer, in the chosen template, without dragging anything: Batch Rename, PDF Pages, Scan QR,
QR Library, App Actions (a second wheel of your apps) and Open Swivel…, each only while its tool is on. Click an item,
or use the arrow keys and Enter; Space turns the page, Esc or a click elsewhere closes it.

All five menu templates (Wheel, List, Bookshelf, Sticky Notes, Clothesline) and the Clothesline, Text Capture, File
Shelf, Image Presets and Quick Actions tools work as described in the [main README](../README.md), with these
Windows equivalents:

| macOS | Windows |
| --- | --- |
| ◎ menu-bar icon | Tray icon (click: Settings; right-click: the same menu) |
| Clothesline rope under the menu bar, ⌃⌥T | Rope along the top of the screen; move to the top edge or press **Ctrl + Alt + T** |
| Screenshot takeover | Watches your Screenshots folder (Print Screen, Win + Print Screen, Snipping Tool with auto-save); nothing in Windows settings is changed |
| Quick Look | A small Preview window (press and hold a print, or Space on the Shelf) |
| Quick Actions ⌃⇧Space / ⌃⌥K / ⌘⇧P | **Ctrl + Shift + Space** / Ctrl + Alt + K / Ctrl + Shift + P |
| AirDrop | Share (Nearby Sharing, Mail, Phone Link…) |
| Vision OCR / face detection | Windows.Media.Ocr / Windows.Media.FaceAnalysis (on-device) |
| Force Touch haptics | Not available on Windows (no public API); sounds are kept |
| Feel & Text (1.3) | Same page (tray › Feel & Text…): presets, per-area speed and bounce, sound style and level, text size and bold; "gentle haptics" is omitted |
| Wheel › Colors, rope colours (1.3) | Same Bookshelf / Sticky Notes palettes and rope / peg colours, stored in the same `templates.colors.v1` / `clothesline.colors.v1` keys |
| Apps ⌥ / QR Library fn menus (1.4) | **Space** / **Tab** while dragging |
| Tool wheel ⌃⌥J / ⌃⌥Space / ⌃⌥W (1.4) | **Ctrl + Alt + J** / Ctrl + Alt + Space / Ctrl + Alt + W. Windows has no list of reserved shortcuts like macOS; a shortcut another app holds fails to register and Settings says so (it is tried again on the next settings change) |
| App Actions: apps in /Applications, Open with NSWorkspace (1.4) | Start menu apps (shortcuts resolved to their .exe) and packaged Store apps, or Browse… for any .exe. *Open files* starts the .exe with the files as arguments, or activates a packaged app for the files (IApplicationActivationManager); *Copy files and open app* starts the app, then puts the files on the clipboard as Explorer's Copy does. Rules are stored in `appTargets.v1` with the Mac's fields (`bundleID` holds a packaged app's AppUserModelID, `appPath` an .exe) |
| Codex / Claude Code drafts (1.4) | The same `codex://threads/new?prompt=…&path=…` and `claude://code/new?q=…&folder=…` links, opened with whatever app Windows has registered for the scheme; if none is, the action says so. File paths are JSON-escaped in the prompt; nothing is uploaded or sent |

### Formats

- **Images**: reads everything Windows has a codec for (JPG, PNG, HEIC, TIFF, GIF, BMP, WebP, AVIF, ICO, camera raw…);
  writes JPG, PNG, TIFF, GIF, BMP, plus HEIC / AVIF / WebP when the Store extensions that encode them are installed.
  PDF from one or more images.
- **Video**: MP4, HEVC (with HEVC Video Extensions), M4V, GIF, M4A, WAV, PNG frame; compress, 1080p/720p/480p, mute,
  extract audio, trim, merge. MOV can be read but not written (Windows has no QuickTime writer).
- **Audio**: M4A, WAV, AIFF, FLAC, ALAC, CAF, AAC, MP3; compress, mono, voice memo, trim, merge.
- **PDF**: PNG / JPG pages at 300 DPI, TXT, DOCX, RTF, HTML; merge, split, rotate, compress, crop, page count.
- **Documents** (DOCX, DOC, RTF, ODT, HTML, TXT, Markdown): DOCX, PDF, RTF, TXT, HTML, ODT; word count. Legacy .doc
  is read (text) but not written — that needs Word itself.
- **Archives**: ZIP / TAR.GZ of any files or folders; extracts ZIP, TAR, TGZ, GZ, and 7Z / RAR / BZ2 / XZ / CAB via
  Windows' built-in `tar.exe`.

UI languages: English, 简体中文, 繁體中文, 日本語 (General › Language, or follows Windows).

## Build

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
cd windows
./build.ps1                 # → build\Swivel\Swivel.exe (needs the .NET 10 Desktop Runtime)
./build.ps1 -SelfContained  # one Swivel.exe that runs without installing .NET
./test.ps1                  # 319 MSTest checks
```

Run `build\Swivel\Swivel.exe`. The first launch opens Settings and asks whether to hang new screenshots. Turn on
**Open at Login** in General or the tray menu (it uses the per-user Run key). Settings are stored in
`%APPDATA%\Swivel\settings.json`.

For testing without dragging (same as the Mac app):

```powershell
Swivel.exe --run <convert|tools|actions|apps|codes> "<item>" <file>...
Swivel.exe --edit <crop|compress|edit|bg|redact|metadata|annotate|collage|trim> <file>...
Swivel.exe --recognize <image>
Swivel.exe --tool <text|shelf|quick|presets|feel|clothesline|style|colors|rename|pdf|qr|library|apps|launcher> [file]...
Swivel.exe --demo <convert|tools|actions> <file> [script] [style]
Swivel.exe --frames <wheel|list|shelf|notes|clothesline> <out dir> [light|dark]
Swivel.exe --rope-frames <out dir>
Swivel.exe --feedback <check|play>
```

`SWIVEL_SETTINGS=<file>` uses a separate settings file, `SWIVEL_TAB=<0-15>` opens a settings page,
`SWIVEL_SCREENSHOTS=<folder>` watches another folder instead of Screenshots, `SWIVEL_SHOW_ROPE=1` lowers the rope at
launch. Code layout and house rules for contributors are in [AGENTS.md](AGENTS.md).

## Verification status (2026-10-21)

Checked automatically on Windows 11 (200 % display scaling):

- `./test.ps1`: 319 tests pass — converters (every image format round trip, EXIF orientation, alpha, GIF encoder,
  PDF structure, all audio containers, generated MP4 through every video option, archives incl. zip-slip),
  documents and the PDF engine (read/write combinations, CJK text → PDF → text, xref streams, encrypted files,
  merge/split/rotate/compress/crop), wheel hit-testing for all five templates, the drag tap state machine and sound
  synthesis, editor snapping/detents/adjustment maths, settings pages, shelf/quick actions/hotkey conflicts/OCR/presets,
  batch rename safety (conflicts, rollback, undo, case-only names) and PDF page export, the QR encoder/reader and 1D
  barcodes (standard test vectors, rotation, perspective, noise, charsets), the QR library and its 3D scenes, app
  actions and deep links, the tool wheel and the two new drag keys,
  clothesline physics (damping ≥ 0.6, ≤ 2 swings, settles within 1.5 s) and screenshot watcher, and that every UI
  string has all three translations.
- Release build: `--run` conversions (PNG→JPG with alpha flattened, PNG→HEIC→PNG, Rotate, images→PDF, PDF page
  count and 300 DPI pages, Markdown→DOCX→PDF→TXT incl. Chinese, WAV→FLAC, Voice Memo, ZIP and Extract), `--recognize`
  (English + Chinese), `--feedback check`, and a full launch (tray, Settings window, watchers) with no errors logged.
- Offscreen renders of all menu templates, editors, settings pages, tools and the clothesline were inspected.

Still needs a person at the PC:

- A real drag from File Explorer onto the menu (the drop target, drag image and running an action from the drop).
- How the sounds, springs and the rope feel; top-edge rope, Ctrl + Alt + T, clipboard hanging and card dragging.
- Shelf and Quick Actions focus after the global shortcut; drags in and out of the Shelf and the rope.
- Editors' Apply with a real mouse; video playback in Trim; face/text detection on real photos.
- Opening the DOCX / ODT / RTF / PDF outputs in Word, LibreOffice or Acrobat.
- Windows 10, mixed-DPI multi-monitor setups, Japanese OCR (not installed on the test PC), AVIF/WebP encoding.
- The tool wheel from the physical Ctrl + Alt + J (focus, arrows and Enter, click-outside closing), and App Actions
  really starting apps: an .exe with files, a packaged app via ActivateForFile, the clipboard paste, and Codex /
  Claude Code opening their drafts (the tests inject the launcher and never start anything). On keyboard layouts
  where Ctrl + Alt acts as AltGr, the chosen shortcut replaces the character it would type while the tool wheel is on.
