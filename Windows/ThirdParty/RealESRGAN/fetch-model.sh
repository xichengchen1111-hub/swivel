#!/bin/bash
# Downloads the official Real-ESRGAN x4plus checkpoint, converts it with Convert/ (no Python),
# and refuses anything whose SHA-256 differs from the pinned values.
set -euo pipefail
cd "$(dirname "$0")/../../.."
dotnet_cmd="${SWIVEL_DOTNET:-dotnet}"
target=build/models/realesrgan-x4plus.onnx
checkpoint=build/models/RealESRGAN_x4plus.pth
onnx_sha=043a257b04a3f1b54d9d7d1ac17b5beca43ffdd457d55278fb944f96e0be5994
pth_sha=4fa0d38905f75ac06eb49a7951b426670021be3018265fd191d2125df9d682f1
if [ -f "$target" ] && [ "$(shasum -a 256 "$target" | cut -d' ' -f1)" = "$onnx_sha" ]; then exit 0; fi
mkdir -p build/models
trap 'rm -f "$target.converted" "$target.converted.part"' EXIT
if ! { [ -f "$checkpoint" ] && [ "$(shasum -a 256 "$checkpoint" | cut -d' ' -f1)" = "$pth_sha" ]; }; then
    part=$(mktemp build/models/realesrgan.XXXXXX)
    trap 'rm -f "$part" "$target.converted" "$target.converted.part"' EXIT
    curl -fsSL --proto '=https' --proto-redir '=https' -o "$part" https://github.com/xinntao/Real-ESRGAN/releases/download/v0.1.0/RealESRGAN_x4plus.pth
    [ "$(shasum -a 256 "$part" | cut -d' ' -f1)" = "$pth_sha" ] || { echo 'RealESRGAN_x4plus.pth does not match the pinned SHA-256.' >&2; exit 1; }
    mv -f "$part" "$checkpoint"
fi
# From Windows/ so its global.json picks the SDK (and a relative SWIVEL_DOTNET from build.sh resolves).
(cd Windows && "$dotnet_cmd" run --project ThirdParty/RealESRGAN/Convert -c Release -- "../$checkpoint" "../$target.converted" > /dev/null)
[ "$(shasum -a 256 "$target.converted" | cut -d' ' -f1)" = "$onnx_sha" ] || { echo 'The converted Real-ESRGAN model does not match the pinned SHA-256.' >&2; exit 1; }
mv -f "$target.converted" "$target"
