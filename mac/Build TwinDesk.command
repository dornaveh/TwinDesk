#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
if ! xcode-select -p >/dev/null 2>&1; then
  xcode-select --install
  echo "Install Apple's Command Line Tools, then run this file again."
  exit 1
fi
OUT="$PWD/dist/TwinDesk.app"
mkdir -p "$OUT/Contents/MacOS" "$OUT/Contents/Helpers" "$OUT/Contents/Resources"
make -C third_party/m1ddc
cp third_party/m1ddc/m1ddc "$OUT/Contents/Helpers/display-control"
cp third_party/m1ddc/LICENSE "$OUT/Contents/Resources/Monitor-component-LICENSE.txt"
xcrun swiftc -swift-version 5 -parse-as-library -O -target arm64-apple-macosx14.2 \
  TwinDesk/*.swift -o "$OUT/Contents/MacOS/TwinDesk" \
  -framework SwiftUI -framework AppKit -framework Foundation -framework Network \
  -framework CoreAudio \
  -framework CoreGraphics -framework Security -framework CryptoKit
cp Info.plist "$OUT/Contents/Info.plist"
SIGNING_IDENTITY="${TWINDESK_SIGNING_IDENTITY:--}"
if [[ "$SIGNING_IDENTITY" == "-" ]]; then
  echo "WARNING: Ad-hoc builds change app identity. Re-authorize Accessibility and screen/audio capture after installing each changed build."
  echo "Set TWINDESK_SIGNING_IDENTITY to a stable code-signing identity to preserve identity across builds."
fi
codesign --force --sign "$SIGNING_IDENTITY" --timestamp=none "$OUT/Contents/Helpers/display-control"
codesign --force --sign "$SIGNING_IDENTITY" --timestamp=none "$OUT"
echo "Built: $OUT"
echo "Move TwinDesk.app into Applications before granting Accessibility and audio permissions."
if [[ "${TWINDESK_NO_REVEAL:-0}" != "1" ]]; then open -R "$OUT"; fi
