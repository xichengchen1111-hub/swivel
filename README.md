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

Open Swivel from Launchpad or Finder (or choose **Open Swivel…** from the ◎ menu) for:

- **General**: turn Swivel on or off, open at login.
- **Shortcuts**: pick which key (⇧ ⌘ ⌃ ⌥ fn) opens each menu.
- **Style**: menu templates — Wheel, List, Bookshelf, Sticky Notes, Clothesline.
- **Customize**: choose which options each menu shows per file type, and their order.
- **晾图**: the screenshot clothesline from `../clipline/index.html`, bundled into the app by `build.sh`. Images can be hung on it from the ⌃ Actions menu.

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
