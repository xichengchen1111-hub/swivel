#!/bin/bash
# Downloads the official Whisper base checkpoint and its multilingual tokenizer, converts the
# checkpoint with ../ModelConvert/ (no Python), and refuses anything whose SHA-256 differs from the pinned values.
set -euo pipefail
cd "$(dirname "$0")/../../.."
dotnet_cmd="${SWIVEL_DOTNET:-dotnet}"
encoder=build/models/whisper-base-encoder.onnx
decoder=build/models/whisper-base-decoder.onnx
tokens=build/models/whisper-multilingual.tiktoken
checkpoint=build/models/whisper-base.pt
encoder_sha=4af8caaa455d00dca07d6ebc6333479295ad3a7d738639120d0458f44bf9891f
decoder_sha=7d6f84754df3a51d65a5469608cd75c2addb687db51279acbd9235e3da41daf1
tokens_sha=b34b360dbb493e781e479794586d661700670d65564001f23024971d1f2fa126
pt_sha=ed3a0b6b1c0edf879ad9b11b1af5a0e6ab5db9205f891f668f8b0e6c6326e34e
matches() { [ -f "$1" ] && [ "$(shasum -a 256 "$1" | cut -d' ' -f1)" = "$2" ]; }
fetch() { # url, file, sha
    matches "$2" "$3" && return 0
    local part; part=$(mktemp build/models/whisper.XXXXXX)
    curl -fsSL --proto '=https' --proto-redir '=https' -o "$part" "$1" || { rm -f "$part"; return 1; }
    matches "$part" "$3" || { rm -f "$part"; echo "$(basename "$2") does not match the pinned SHA-256." >&2; return 1; }
    mv -f "$part" "$2"
}
mkdir -p build/models
fetch https://raw.githubusercontent.com/openai/whisper/31243bad24cc746f07d4c8bfdd2d974872cb1803/whisper/assets/multilingual.tiktoken "$tokens" "$tokens_sha"
if matches "$encoder" "$encoder_sha" && matches "$decoder" "$decoder_sha"; then exit 0; fi
fetch "https://openaipublic.azureedge.net/main/whisper/models/$pt_sha/base.pt" "$checkpoint" "$pt_sha"
trap 'rm -f "$encoder.converted" "$encoder.converted.part" "$decoder.converted" "$decoder.converted.part"' EXIT
# From Windows/ so its global.json picks the SDK (and a relative SWIVEL_DOTNET from build.sh resolves).
(cd Windows && "$dotnet_cmd" run --project ThirdParty/ModelConvert -c Release -- whisper "../$checkpoint" "../$encoder.converted" "../$decoder.converted" > /dev/null)
matches "$encoder.converted" "$encoder_sha" && matches "$decoder.converted" "$decoder_sha" || { echo 'The converted Whisper model does not match the pinned SHA-256.' >&2; exit 1; }
mv -f "$encoder.converted" "$encoder"; mv -f "$decoder.converted" "$decoder"
