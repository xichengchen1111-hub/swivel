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
