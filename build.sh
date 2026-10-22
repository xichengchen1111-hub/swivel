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
# Licences of vendored third-party data (Mac Doctor), inside the signature.
mkdir -p "$APP/Contents/Resources/ThirdParty"
cp ThirdParty/ClearDisk/LICENSE "$APP/Contents/Resources/ThirdParty/ClearDisk-LICENSE.txt"
# Hardened runtime: no DYLD_* injection or unsigned libraries in the shipped app. Don't add entitlements that
# re-enable either (allow-dyld-environment-variables, disable-library-validation).
codesign --force --options runtime --sign - "$APP" >/dev/null
echo "Built $APP"
