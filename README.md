# Swivel

A zero-click, offline file converter for macOS. Drag any file, tap a key, and drop it on the wheel that appears under your cursor.

| Key while dragging | Wheel |
| --- | --- |
| ⇧ Shift | **Convert** formats |
| ⌘ Command | File **tools**: editors for Crop, Compress, Edit (exposure, colour…), Add Background, Redact (finds faces and text on-device), Metadata and Annotate (arrows, boxes, text, numbered callouts); Collage for several images; Trim for video and audio; Merge for several videos or audio files; plus resize, rotate, mute, split… |
| ⌃ Control | File **actions** (AirDrop, Float shelf, Copy, Move, Copy Path, Terminal) |

Tap the same key again to page through a wheel that has more than eight options. Drop on the centre to cancel. Results are saved next to the original. Click the toast to reveal them in Finder.

Everything runs locally, using built-in macOS frameworks (ImageIO, AVFoundation, PDFKit, AppKit text system) and `afconvert` / `ditto` / `tar`. No third-party dependencies.

## The app window

Swivel is a bundle of two tools, each switched on or off on its own — from the app window (open Swivel from Launchpad or Finder, or **Open Swivel…** in the ◎ menu) or straight from the ◎ menu.

The window works like PowerToys: features in a sidebar, each page starting with the feature's switch.

- **Overview**: both features with their switches.
- **Wheel**: the drag-and-tap menu. Sections for **Shortcuts** (which key, ⇧ ⌘ ⌃ ⌥ fn, opens each menu), **Style** (Wheel, List, Bookshelf, Sticky Notes, Clothesline templates) and **Customize** (which options each menu shows per file type, and their order).
- **Clothesline**: a rope under the menu bar. New screenshots hang on it instead of the desktop; rest the pointer on the menu bar (or press ⌃⌥T) to lower it. Click to copy, double-click to open, hold for Quick Look, drag into an app for a copy or into a folder to take it down, drag to the Trash or click × to discard. Images can also be hung from the Wheel's ⌃ Actions menu.
- **General**: open at login.

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
