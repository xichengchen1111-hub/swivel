# Swivel

A zero-click, offline file converter for macOS. Drag any file, tap a key, and drop it on the wheel that appears under your cursor.

| Key while dragging | Wheel |
| --- | --- |
| ⇧ Shift | **Convert** formats |
| ⌘ Command | File **tools**: editors for Crop, Compress, Edit (exposure, colour…), Add Background, Redact (finds faces and text on-device), Metadata and Annotate (arrows, boxes, text, numbered callouts); Collage for several images; Trim for video and audio; Merge for several videos or audio files; plus resize, rotate, mute, split… |
| ⌃ Control | File **actions** (AirDrop, Float shelf, Copy, Move, Copy Path, Terminal) |

Tap the same key again to page through a wheel that has more than eight options. Drop on the centre to cancel. Results are saved next to the original — no new folders, even for multi-page PDFs or archives. Click the toast to reveal them in Finder.

### How it feels

- The option under the pointer **springs out of the ring**, and a dial on the hub turns to it. Moving on lets it settle back while the next one comes out, so a quick sweep reads as one wave. Selection follows the pointer's direction, so overshooting past the ring keeps the option you aimed at, and a little hysteresis stops it flickering at the edges.
- **Dropping presses the option in**, holds it for a beat and lets it spring back as the rest of the menu clears away; the file's icon flies into it. A Force Click while still dragging pushes the option partway in first.
- Coming back to the centre shows **Cancel**; releasing there (or pressing Esc) clears the menu at once.
- On a Force Touch trackpad each new option gives a light **detent**, a page turn a step, and the drop a firmer **press**. Optional **sound effects** (a soft tick per option, a ratchet for pages, a key “thock” on the drop and a short chime when a result is saved) follow the system's “Play user interface sound effects” setting. Both have switches in General and in the ◎ menu.
- Reduce Motion keeps the colour and feedback but nothing moves; Reduce Transparency puts a solid backdrop behind the wheel.

Everything runs locally, using built-in macOS frameworks (Vision, ImageIO, AVFoundation, PDFKit, AppKit text system) and `afconvert` / `ditto` / `tar`. No third-party dependencies.

## The app window

Swivel is a bundle of six tools, each switched on or off on its own — from the app window (open Swivel from Launchpad or Finder, or **Open Swivel…** in the ◎ menu) or straight from the ◎ menu. The four additions in version 1.1 start **off**, so each user can choose what to enable.

The window works like PowerToys: features in a sidebar, each page starting with the feature's switch.

- **Overview**: all six features with their switches.
- **Wheel**: the drag-and-tap menu. Sections for **Shortcuts** (which key, ⇧ ⌘ ⌃ ⌥ fn, opens each menu), **Style** (Wheel, List, Bookshelf, Sticky Notes, Clothesline templates) and **Customize** (which options each menu shows per file type, and their order; drag a row by its grip to reorder). The menu previews are live: move the pointer over one to try how it feels, and click an option to press it.
- **Clothesline**: a rope under the menu bar. A new screenshot first waits as a card in the bottom-right corner, like the system thumbnail: drag it up (or click it) to hang it, drag it into an app or folder like any file, or leave it and it goes to your usual screenshot folder. The rope and prints move on a small physics model (spring drop, pendulum swing, breeze); move the pointer to the screen’s top edge (or press ⌃⌥T) to lower it immediately. Click to copy (the print confirms it with a “Copied” chip), double-click to open, hold or Force Click for Quick Look (it zooms out of the print), drag into an app for a copy or into a folder to take it down, drag to the Trash or click × to discard. Clicking the corner card flies it up to its peg. A rope shown with ⌃⌥T stays until you click elsewhere. The motion is deliberately calm: everything is well damped and settles within about a second. Images can also be hung from the Wheel's ⌃ Actions menu. Unattended screenshot cards slide away after 2.5 seconds; hovering or dragging pauses dismissal, and leaving the card gives you another second. The screenshot is kept in your usual screenshot folder.
- **Text Capture**: choose an image and extract English, Chinese or Japanese text locally with Apple Vision. Review or edit the result, then copy it (⌘Return copies while you edit). Also available in the Wheel's Tools menu for one image and the Clothesline photo's contextual menu. It reads selected image files; it does not capture the screen.
- **File Shelf**: gather files and folders in a floating native list. Command-click or Shift-click to select several, then drag them out together. Space opens Quick Look. New files glide in and glow briefly; one already on the shelf is pointed out instead of added twice. Removing or clearing items only removes references (⌘Z brings them back); originals stay in place. The shelf floats without taking focus from the app you're working in. The shelf is temporary and clears when Swivel quits.
- **Image Presets**: save a name, maximum long edge, JPG/PNG/HEIC/TIFF format, quality and metadata preference. Three built-in presets are included. Apply a preset to one or several images from Settings, the Wheel or Quick Actions. Results are written beside each original with unique names. Small images are not enlarged; JPG composites transparency onto white. Animated images use their first frame. Turning the feature off retains saved presets.
- **Quick Actions**: press **⌃⇧Space** to search enabled tools without finding the menu-bar icon. Choose files or drop them onto the panel, search, use ↑/↓, and press Return; the chosen row blinks as it runs, like a menu. Esc dismisses the search panel. It appears instantly and doesn't take Swivel to the front. Settings offers **⌃⌥K** and **⌘⇧P** as alternatives and reports shortcut registration conflicts. It accepts explicitly supplied files rather than reading the selection in another app.
- **General**: open at login, haptic feedback, sound effects (feature switches click like a light switch), language.

The editors snap where it helps: crop edges catch the image edges, the centre lines, the thirds and the original ratio, sliders have a detent at their neutral value and reset on double-click, Trim handles stop at the ends, and Annotate arrows catch 45° steps with ⇧. Hold ⌘ while dragging to turn snapping off. Redact waits for its face and text search before saving, and won't write a “redacted” copy with nothing covered. Press and hold the preview in Edit or Compress to compare with the original. Each catch is a light tap on a Force Touch trackpad.

The new tools use AppKit windows, sheets, sidebars, SF Symbols, system colours and native controls, with light/dark appearance support. Their UI is available in English, Simplified Chinese, Traditional Chinese and Japanese. They require no Accessibility, Screen Recording or Full Disk Access permission.

## Formats

- **Images**: JPG, PNG, HEIC, TIFF, AVIF, GIF, BMP, PDF (WebP is read but only written where macOS supports it)
- **Video** (MOV/MP4/M4V): MP4, MOV, HEVC, M4V, GIF, M4A, WAV, PNG frame
- **Audio**: M4A, WAV, AIFF, FLAC, ALAC, CAF, AAC
- **PDF**: PNG / JPG pages at 300 DPI, TXT, DOCX, RTF, HTML
- **Documents** (DOCX, DOC, RTF, ODT, HTML, TXT, Markdown): DOCX, PDF, RTF, TXT, HTML, ODT, DOC
- **Archives**: ZIP / TAR.GZ any file or folder; extract ZIP / TAR / TGZ

## Build

```bash
./build.sh
open build/Swivel.app
```

Requires macOS 15+ and the Xcode command-line tools. It lives in the menu bar (◎ icon), where you can enable *Open at Login*.

For testing without dragging: `Swivel --run <convert|tools|actions> "<item>" <file>...`, or open an editor directly with `Swivel --edit <crop|compress|edit|bg|redact|metadata|annotate|collage|trim> <file>`.

## Validate the new tools

```bash
./test.sh
.build/debug/Swivel --recognize /path/to/image.png
.build/debug/Swivel --tool text /path/to/image.png
.build/debug/Swivel --tool shelf /path/to/one.png /path/to/two.txt
.build/debug/Swivel --tool quick /path/to/image.png
.build/debug/Swivel --tool presets
```

`test.sh` runs eight Swift Testing checks for image output safety, metadata and orientation, preset validation, shelf references, feature gating/search, OCR and independent hotkey registration. It uses the macro plugin bundled with the selected Swift toolchain when available; no package dependencies are downloaded.

`--tool` is a UI preview mode that enables the four additions **in that executable's preferences domain**, registers the Quick Actions shortcut and skips the normal app delegate, screenshot takeover and background watchers. Use the unbundled debug executable or a separate validation bundle, not the installed production app. `--recognize` prints OCR results without changing the clipboard or feature preferences.

Set `SWIVEL_PREVIEW_APPEARANCE=light` or `dark` with `--tool` to inspect the two appearances without changing macOS settings.

Quit the running app normally before replacing an installed build, so screenshot settings can be restored. New output files never replace originals.
