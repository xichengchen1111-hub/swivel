#!/bin/bash
# Packs mcpb/ into build/Swivel.mcpb (a zip with manifest.json at its root) for Claude Desktop:
# Settings → Extensions → Advanced settings → Install Extension…, or double-click the file.
set -euo pipefail
cd "$(dirname "$0")/.."
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
cp mcpb/manifest.json "$STAGE/"
mkdir "$STAGE/server"
cp mcpb/server/swivel-mcp "$STAGE/server/"
chmod 755 "$STAGE/server/swivel-mcp"
sips -s format png -Z 256 Resources/Swivel.icns --out "$STAGE/icon.png" >/dev/null
mkdir -p build
rm -f build/Swivel.mcpb
(cd "$STAGE" && /usr/bin/zip -q -r -X "$OLDPWD/build/Swivel.mcpb" manifest.json icon.png server)
echo "Packed build/Swivel.mcpb"
