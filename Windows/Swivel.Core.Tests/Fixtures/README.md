These fixtures were encoded on 2026-10-23 with Swift Foundation JSONEncoder
(prettyPrinted + sortedKeys), using the stored-field Codable schemas from
Sources/Swivel/ImagePresets.swift and Sources/Swivel/QRLibrary.swift at 2fc19d1.
They contain invented data only. They check Swift's optional-field omission,
reference-date numeric Date encoding, escaped URL slashes, Unicode and CRLF
against the independent Windows readers. No Mac application was run or changed.
The standalone generation source is preserved in the local validation artifacts
at build/windows-next/swift-library-fixtures/main.swift.

7z/ (2026-10-29): `make7z.py` regenerates these with `python3 -I make7z.py <dir>` on macOS.
`libarchive-*.7z` were written by the system bsdtar (libarchive 3.7.4, liblzma 5.4.3) with
encoded headers; `handmade-*.7z` are assembled by the script's own small writer from Python
`lzma` raw streams (x86 BCJ + LZMA2 in both coder orders, LZMA with five lc/lp/pb shapes).
bsdtar read every archive back except `handmade-bcj-filter-first.7z`, whose coder order
libarchive does not accept. All contents are generated test data.

webp/ (2026-10-29): `makewebp.py` regenerates these with `python3 -I makewebp.py <dir>` (Pillow 12 with
libwebp 1.6.0). Each `<name>.rgba` holds Pillow's decoded RGBA rows for `<name>.webp`: lossless files
must match exactly, lossy files within one level per colour channel (alpha exactly). `animated.webp`
has two frames and must be refused. All pictures are generated test data.
