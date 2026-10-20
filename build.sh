#!/bin/bash
# Builds Swivel.app into ./build
set -euo pipefail
cd "$(dirname "$0")"
swift build -c release
APP=build/Swivel.app
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp .build/release/Swivel "$APP/Contents/MacOS/Swivel"
cp Resources/Info.plist "$APP/Contents/Info.plist"
cp Resources/Swivel.icns "$APP/Contents/Resources/Swivel.icns"
codesign --force --sign - "$APP" >/dev/null
echo "Built $APP"
