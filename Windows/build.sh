#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
SWIVEL_DOTNET="${SWIVEL_DOTNET:-dotnet}"
SWIVEL_RUNTIME="${1:-win-x64}"
case "$SWIVEL_RUNTIME" in win-x64|win-arm64) ;; *) echo 'Use win-x64 or win-arm64.' >&2; exit 2 ;; esac
SWIVEL_OUTPUT="../build/windows/0.105.0/$SWIVEL_RUNTIME"
SWIVEL_ARCHIVE="../build/windows/Swivel-Windows-0.105.0-$SWIVEL_RUNTIME.zip"
# Published previews are immutable. Even a same-version rebuild needs a new
# version or a deliberate separate backup; this script never removes old output.
if [ -e "$SWIVEL_ARCHIVE" ] || [ -e "$SWIVEL_OUTPUT" ]; then
    echo 'This version already has a package or output folder. Preserve it and choose a new version before rebuilding.' >&2
    exit 2
fi
ThirdParty/U2Net/fetch-model.sh # pinned background model, checked by SHA-256
SWIVEL_DOTNET="$SWIVEL_DOTNET" ThirdParty/RealESRGAN/fetch-model.sh # pinned upscaling model, converted and checked by SHA-256
SWIVEL_DOTNET="$SWIVEL_DOTNET" ThirdParty/Whisper/fetch-model.sh # pinned speech model, converted and checked by SHA-256
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_GENERATE_ASPNET_CERTIFICATE=false
"$SWIVEL_DOTNET" run --project Swivel.Core.Tests -c Release -p:UseSharedCompilation=false
"$SWIVEL_DOTNET" build Swivel.Windows.Tests -c Release -m:1 -p:UseSharedCompilation=false --disable-build-servers
mkdir -p ../build/windows/0.105.0
mkdir "$SWIVEL_OUTPUT"
"$SWIVEL_DOTNET" publish Swivel.Windows -c Release -r "$SWIVEL_RUNTIME" --self-contained true -m:1 -p:UseSharedCompilation=false -p:PublishSingleFile=false -p:PublishReadyToRun=false --disable-build-servers -o "$SWIVEL_OUTPUT"
sed 's|../reports/Windows与macOS兼容调研.md|COMPATIBILITY.md|g' README.md > "$SWIVEL_OUTPUT/README.md"
sed 's|../Windows/TESTING.md|TESTING.md|g; s|../Windows/PORTING.md|PORTING.md|g; s|../Windows/PROGRESS.md|PROGRESS.md|g' '../reports/Windows与macOS兼容调研.md' > "$SWIVEL_OUTPUT/COMPATIBILITY.md"
cp TESTING.md PORTING.md "$SWIVEL_OUTPUT/"
sed -E 's|../reports/Windows与macOS兼容调研.md|COMPATIBILITY.md|g; s/\[([^]]+)\]\(([^)]*\.(cs|swift))\)/\1: `\2`/g' PROGRESS.md > "$SWIVEL_OUTPUT/PROGRESS.md"
(cd "$SWIVEL_OUTPUT" && zip -q -r "../Swivel-Windows-0.105.0-$SWIVEL_RUNTIME.staging.zip" .)
# Hard-link publication is exclusive: a concurrent archive is never overwritten.
ln "../build/windows/0.105.0/Swivel-Windows-0.105.0-$SWIVEL_RUNTIME.staging.zip" "$SWIVEL_ARCHIVE"
echo "Cross-built 0.105.0 $SWIVEL_OUTPUT/Swivel.exe. Native Windows tests have NOT run on this host."
