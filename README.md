# Swivel

A native, local file toolkit for macOS. Drag any file, tap a key, and drop it on the wheel that appears under your cursor.

| Key while dragging | Wheel |
| --- | --- |
| ⇧ Shift | **Convert** formats |
| ⌘ Command | File **tools**: editors for Crop, Compress, Edit (exposure, colour…), Add Background, Remove BG (local transparent PNG cutout), Redact (finds faces and text on-device), Metadata and Annotate (arrows, boxes, text, numbered callouts); Collage for several images; Trim for video and audio; Merge for several videos or audio files; plus resize, rotate, mute, split… |
| ⌃ Control | File **actions** (Batch Rename, AirDrop, Float shelf, Copy, Move, Copy Path, Terminal) |
| ⌥ Option | **Apps**: configured applications, Codex and Claude Code drafts |
| fn | **Codes**: QR library and saved codes |

Tap the same key again to page through a wheel that has more than eight options. Drop on the centre to cancel. Results are saved next to the original — no new folders, even for multi-page PDFs or archives. Click the toast to reveal them in Finder.

### How it feels

- The option under the pointer **springs out of the ring**, and a dial on the hub turns to it. Moving on lets it settle back while the next one comes out, so a quick sweep reads as one wave. Selection follows the pointer's direction, so overshooting past the ring keeps the option you aimed at, and a little hysteresis stops it flickering at the edges.
- **Dropping presses the option in**, holds it for a beat and lets it spring back as the rest of the menu clears away; the file's icon flies into it. A Force Click while still dragging pushes the option partway in first.
- Coming back to the centre shows **Cancel**; releasing there (or pressing Esc) clears the menu at once.
- On a Force Touch trackpad each new option gives a light **detent**, a page turn a step, and the drop a firmer **press**. Optional **sound effects** (a soft tick per option, a ratchet for pages, a key “thock” on the drop and a short chime when a result is saved) follow the system's “Play user interface sound effects” setting. Both have switches in General and in the ◎ menu.
- Reduce Motion keeps the colour and feedback but nothing moves; Reduce Transparency puts a solid backdrop behind the wheel.

Everything runs locally, using built-in macOS frameworks (Vision, ImageIO, AVFoundation, PDFKit, AppKit text system) and `afconvert` / `ditto` / `tar`. No third-party dependencies.

## The app window

Swivel is a bundle of eleven tools, each switched on or off on its own — from the app window (open Swivel from Launchpad or Finder, or **Open Swivel…** in the ◎ menu) or straight from the ◎ menu. Optional tools start **off**, so each user can choose what to enable.

The window works like PowerToys: features in a sidebar, each page starting with the feature's switch.

- **Overview**: all eleven features with their switches.
- **Wheel**: the drag-and-tap menu. Sections for **Shortcuts** (which key, ⇧ ⌘ ⌃ ⌥ fn, opens each menu), **Style** (Wheel, List, Bookshelf, Sticky Notes, Clothesline templates) and **Customize** (which options each menu shows per file type, and their order; drag a row by its grip to reorder). Bookshelf, Sticky Notes and Clothesline use quiet, nearly neutral card surfaces, fine borders, light shadows and system text, with matching dark appearances. The menu previews are live: move the pointer over one to try how it feels, and click an option to press it.
- **Clothesline**: a rope under the menu bar. A new screenshot first waits as a card in the bottom-right corner, like the system thumbnail: drag it up (or click it) to hang it, drag it into an app or folder like any file, or leave it and it goes to your usual screenshot folder. The rope and prints move on a small physics model (spring drop, pendulum swing, breeze); move the pointer to the screen’s top edge (or press ⌃⌥T) to lower it immediately. Click to copy (the print confirms it with a “Copied” chip), double-click to open, hold or Force Click for Quick Look (it zooms out of the print), drag into an app for a copy or into a folder to take it down, drag to the Trash or click × to discard. Clicking the corner card flies it up to its peg. A rope shown with ⌃⌥T stays until you click elsewhere. The motion is deliberately calm: everything is well damped and settles within about a second. Images can also be hung from the Wheel's ⌃ Actions menu. Unattended screenshot cards slide away after 2.5 seconds; hovering or dragging pauses dismissal, and leaving the card gives you another second. The screenshot is kept in your usual screenshot folder.
- **Template colors**: Wheel → Colors provides separate Bookshelf and Sticky Notes palettes. Choose Soft Neutrals, Misty Coast, Sage Garden, Warm Linen, Rose Clay or Lavender Haze, or customize four repeating colors with native color wells. The Bookshelf rail has its own color well. Live previews, per-template reset, automatic persistence and readable light/dark text are included.
- **Ready to drop**: dragging an accepted screenshot/image over the rope makes the cord clearly blue, thicker and softly glowing, so you know you can release. Leaving restores the chosen rope color; the cue also works with Reduce Motion and only appears when the source permits a supported drop operation.
- **Clothesline colors**: choose the rope and peg colors independently on the Clothesline page. Six muted presets (Silver, Graphite, Sand, Sage, Mist Blue and Dusty Rose), a native custom-color picker, a preview and Reset rope colors are included. Choices save automatically and update existing hung prints as well as the Clothesline menu template.
- **Text Capture**: choose an image and extract English, Chinese or Japanese text locally with Apple Vision. Review or edit the result, then copy it (⌘Return copies while you edit). Also available in the Wheel's Tools menu for one image and the Clothesline photo's contextual menu. It reads selected image files; it does not capture the screen.
- **File Shelf**: gather files and folders in a floating native list. Command-click or Shift-click to select several, then drag them out together. Space opens Quick Look. New files glide in and glow briefly; one already on the shelf is pointed out instead of added twice. Removing or clearing items only removes references (⌘Z brings them back); originals stay in place. The shelf floats without taking focus from the app you're working in. The shelf is temporary and clears when Swivel quits.
- **Image Presets**: save a name, maximum long edge, JPG/PNG/HEIC/TIFF format, quality and metadata preference. Three built-in presets are included. Apply a preset to one or several images from Settings, the Wheel or Quick Actions. Results are written beside each original with unique names. Small images are not enlarged; JPG composites transparency onto white. Animated images use their first frame. Turning the feature off retains saved presets.
- **Quick Actions**: press **⌃⇧Space** to search enabled tools without finding the menu-bar icon. Choose files or drop them onto the panel, search, use ↑/↓, and press Return; the chosen row blinks as it runs, like a menu. Esc dismisses the search panel. It appears instantly and doesn't take Swivel to the front. Settings offers **⌃⌥K** and **⌘⇧P** as alternatives and reports shortcut registration conflicts. It accepts explicitly supplied files rather than reading the selection in another app.
- **Batch Rename**: combine a base name, find/replace, prefix, suffix and numbering. Review every old/new name and conflict before applying. Extensions are preserved; existing files are never replaced. Undo checks that the renamed files still belong to the operation and remains available while the window is open.
- **PDF Pages**: select ranges such as `1,3-5`, reorder thumbnails, rotate or remove pages and export a selection or the whole arranged document. Results have unique names beside the original; source PDFs remain unchanged.
- **QR & Barcodes**: read selected images locally, including multiple codes and rotated images. Review text before copying or confirming a web link. Explicitly save codes in **QR Library**, with names, collections, search, add/edit/delete and undo. The library stores decoded content locally and can regenerate a QR image. Each code grows into a small 3D voxel scene (Tree, Bouquet or Island, each in spring, summer and autumn) built from the code's own modules; click it and the camera turns to look straight down, where the leaves, flowers or land line up into the scannable code. Copied/exported images always contain a plain QR code with its quiet zone.
- **App Actions**: choose installed applications and optional extension filters. Open files through macOS, or open an app and prepare file references on the clipboard for an explicit paste. Built-in **Codex / Claude Code** actions use official deep links to open a new draft containing local file paths. They do not upload attachments or send messages. These rules affect Swivel only. Apps have individual switches as well as the feature switch.
- **Shortcut wheel**: enable **Wheel → Shortcuts → Shortcut wheel**, then press **⌃⌥J** (alternatives: ⌃⌥Space / ⌃⌥W). Click an item or use arrows and Return; Space changes pages and Esc closes. It uses the selected wheel template. Batch Rename, PDF Pages, Scan QR, QR Library and Apps appear according to their switches. Apps open a submenu; QR Library opens its window directly; file tools ask you to select files. No Finder selection is read implicitly. Existing custom drag-key assignments are preserved when the two new wheels are added.
- **Feel & Text** (also available directly in the ◎ menu): five presets — Original, Calm, Snappy, Playful and Quiet — plus independent speed (0.75–1.5×) and bounce controls for pop-up menus, the Clothesline and tools/settings. Try all five menu layouts in a live preview or replay a local motion sample. Choose Classic, Soft or Bright sounds, set their level relative to the system effects volume, mute selection sounds separately, or use gentler trackpad taps. Selection, page, confirm and completion buttons let you try feedback. Four text sizes (Small through Extra Large) and bold text update native windows and menu labels immediately; exported-image lettering is unchanged. Changes persist automatically, custom tuning is identified, and Restore tuning defaults resets this page. Presets preserve text choices and the existing sound/haptic on/off switches; macOS Reduce Motion and sound settings still take priority.
- **Keep Awake**: stop the Mac from idle sleep for 30 minutes, 1, 2 or 5 hours, or until turned off — from the ◎ menu (the ring fills while it's on), its settings page or Quick Actions. Choose whether the display stays on too. It uses a standard power assertion: no permission, and it ends when the time is up, when you stop it or when Swivel quits. Closing a laptop's lid still sleeps it.
- **Quick Actions assistant** (optional, off until you add a key): in Quick Actions → Assistant, choose Claude (Anthropic) or any OpenAI-compatible service (DeepSeek, Qwen, a local Ollama server) and save your own API key; it is stored in the login keychain. Type a request such as “make these photos smaller than 1 MB” and choose **Ask the assistant**: the model picks one of the enabled actions, which appears first in the list with a short explanation, and runs only when you press Return. Swivel sends the request, the action names (your own preset and app names included; saved QR codes are left out because they are named after images) and the kinds of chosen files — never file names, paths or contents — and accepts only an action that was offered. A key belongs to the address it was saved for, and redirects are never followed, so it can't be sent anywhere else.
- **General**: open at login, haptic feedback, sound effects (feature switches click like a light switch), language.

The editors snap where it helps: crop edges catch the image edges, the centre lines, the thirds and the original ratio, sliders have a detent at their neutral value and reset on double-click, Trim handles stop at the ends, and Annotate arrows catch 45° steps with ⇧. Hold ⌘ while dragging to turn snapping off. Redact waits for its face and text search before saving, and won't write a “redacted” copy with nothing covered. Press and hold the preview in Edit or Compress to compare with the original. Each catch is a light tap on a Force Touch trackpad.

**Remove BG** cuts the subject out with Apple Vision and saves a transparent `name cutout.png` beside the original (several images at once work too). **Compress** for images has a **Target size** (100 KB – 5 MB): the quality, then if needed the dimensions (never below 320 px on the long edge), are chosen automatically, and an unreachable target says so instead of saving a larger file. **Compress** for video first asks for a target — a smaller file, or 8 / 10 / 25 / 50 / 100 MB — and re-encodes to H.264/AAC with one corrective pass; the result message states the final size. **Trim** cuts video exactly (re-encoded), so frames before the start point don't remain hidden in the file.

The new tools use AppKit windows, sheets, sidebars, SF Symbols, system colours and native controls, with light/dark appearance support. Their UI is available in English, Simplified Chinese, Traditional Chinese and Japanese. They require no Accessibility, Screen Recording or Full Disk Access permission.

## Formats

- **Images**: JPG, PNG, HEIC, TIFF, AVIF, GIF, BMP, PDF (WebP is read but only written where macOS supports it)
- **Video** (MOV/MP4/M4V): MP4, MOV, HEVC, M4V, GIF, M4A, WAV, PNG frame
- **Audio**: M4A, WAV, AIFF, FLAC, ALAC, CAF, AAC
- **PDF**: PNG / JPG pages at 300 DPI, TXT, DOCX, RTF, HTML
- **Documents** (DOCX, DOC, RTF, ODT, HTML, TXT, Markdown): DOCX, PDF, RTF, TXT, HTML, ODT, DOC
- **Archives**: ZIP / TAR.GZ any file or folder; extract ZIP, TAR, TGZ/GZ, RAR (including multi-part .partN.rar and .r00 sets), 7Z, XZ, BZ2, ISO, XAR, CAB, LZH and CPIO with the system's own `tar`. Extracted files keep the archive's “downloaded from the internet” flag so Gatekeeper still checks them; extraction stops before it would fill the disk; encrypted archives are reported rather than prompted for. Disk images (DMG) are left to macOS.

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
.build/debug/Swivel --tool feel
.build/debug/Swivel --tool rename /path/to/one.txt /path/to/two.txt
.build/debug/Swivel --tool pdf /path/to/document.pdf
.build/debug/Swivel --tool qr /path/to/code.png
.build/debug/Swivel --tool library
.build/debug/Swivel --tool apps
.build/debug/Swivel --tool launcher
```

`test.sh` runs 177 Swift Testing checks for image output safety, metadata and orientation, preset validation, shelf references, feature gating/search, OCR, hotkeys, interaction feel and settings persistence. Coverage includes rename conflicts, rollback/undo, PDF order/rotation and original preservation, QR decoding and library persistence, draft links, app routing, independent switches and wheel shortcuts. It uses the macro plugin bundled with the selected Swift toolchain when available; no package dependencies are downloaded.

`--tool` is a UI preview mode that enables the optional tools **in that executable's preferences domain**, registers the Quick Actions shortcut and skips the normal app delegate, screenshot takeover and background watchers. Use the unbundled debug executable or a separate validation bundle, not the installed production app. `--recognize` prints OCR results without changing the clipboard or feature preferences.

Set `SWIVEL_PREVIEW_APPEARANCE=light` or `dark` with `--tool` to inspect the two appearances without changing macOS settings.

Quit the running app normally before replacing an installed build, so screenshot settings can be restored. New output files never replace originals.

`SWIVEL_TAB=10` opens Feel & Text in the normal app. `--tool feel` previews that page without screenshot watchers; use a separate validation bundle. `SWIVEL_TUNING_RENDERS=/path ./test.sh` exports light/dark tuning-page renders at every text size.

`SWIVEL_QR_LIBRARY_DIR=/temporary/test-library` isolates the QR library in `--tool` previews. `SWIVEL_FILE_ACTIONS_RENDERS=/path ./test.sh` renders every template and season and decodes each top view with Vision; `SWIVEL_QR_FILMSTRIP=/path` renders the camera turn and times scene building. File results are regenerated from decoded content rather than copies of the original QR artwork. The scene-to-code interaction was inspired by [Tree.ICQR](https://tree.icqr.com/) and independently built with SceneKit; no source or assets were copied.

Draft-link references: [Codex commands](https://learn.chatgpt.com/docs/reference/commands) and [Claude Desktop links](https://support.claude.com/en/articles/14729294-open-claude-desktop-with-a-link).

Version 1.4.1 adds **Add Application…** directly to Wheel → Customize → App Actions, with a link to manage existing rules. Previewing a category now includes all configured extensions in that category (for example TXT alongside DOCX). The shortcut wheel defaults to **Control + Option + J**, checks enabled macOS shortcuts before reporting readiness, and reports a conflict for Control + Option + Space when it is assigned to input-source switching. Space alone is for paging an already open wheel. The QR library has an explicit empty-state New/Import flow; imports report empty and duplicate results, and new/imported codes clear stale search filters.

Version 1.5.0 adds Remove BG, target-size compression for images and video, RAR/7z/ISO extraction, Keep Awake and the optional Quick Actions assistant, and fixes the issues found in a security review: outputs are never written through a symlink planted at the output name; tools can't hang on a full error pipe or a password prompt; extraction keeps the quarantine flag and stops before filling the disk; file names that look like options are archived, not obeyed; web archives and HTML no longer pull in remote or local resources; metadata removal is verified by reading the result back; Redact's blur and mosaic scale with each box; movies that reference other files are refused; Codex/Claude Code drafts show hidden characters in file names; the installed app is signed with the hardened runtime and ignores the developer command-line modes, which remain available to unbundled builds and validation bundles.

Version 1.4.2 fixes screenshot takeover becoming inactive while its switch stayed on. Applications and Desktop copies now reuse a single running instance. Screenshot settings have a process owner, so a stale process cannot undo the current owner's takeover; the original settings survive recovery/restart. Reopening or waking the active app checks and repairs its enabled takeover. Quitting preserves a destination explicitly changed in the system screenshot toolbar. New screenshots first appear as a card: click it or drag it up to hang it; unattended cards still go to the original screenshot folder.
