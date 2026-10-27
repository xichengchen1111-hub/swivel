#!/bin/bash
# Downloads the pinned U²-Net ONNX file into build/models/ and refuses anything else.
set -euo pipefail
cd "$(dirname "$0")/../../.."
target=build/models/u2net.onnx
sha=8d10d2f3bb75ae3b6d527c77944fc5e7dcd94b29809d47a739a7a728a912b491
if [ -f "$target" ] && [ "$(shasum -a 256 "$target" | cut -d' ' -f1)" = "$sha" ]; then exit 0; fi
mkdir -p build/models
part=$(mktemp build/models/u2net.XXXXXX)
trap 'rm -f "$part"' EXIT
curl -fsSL --proto '=https' --proto-redir '=https' -o "$part" https://github.com/danielgatis/rembg/releases/download/v0.0.0/u2net.onnx
[ "$(shasum -a 256 "$part" | cut -d' ' -f1)" = "$sha" ] || { echo 'u2net.onnx does not match the pinned SHA-256.' >&2; exit 1; }
mv -f "$part" "$target"
