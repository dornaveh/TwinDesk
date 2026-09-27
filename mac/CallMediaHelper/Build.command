#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
OUT="$PWD/dist/TwinDesk-Calls.app"
mkdir -p "$OUT/Contents/MacOS"
PRODUCER_OBJECT="$(mktemp /tmp/TwinDeskAudioProducer.XXXXXX.o)"
trap 'rm -f "$PRODUCER_OBJECT"' EXIT
xcrun clang -std=c11 -O2 -arch arm64 -mmacosx-version-min=14.2 -c \
  ../AudioDriver/TwinDeskAudioProducer.c -o "$PRODUCER_OBJECT"
xcrun swiftc -swift-version 5 -parse-as-library -O -target arm64-apple-macosx14.2 \
  -import-objc-header ../AudioDriver/TwinDeskAudioShared.h \
  ./*.swift "$PRODUCER_OBJECT" -o "$OUT/Contents/MacOS/TwinDesk-Calls" \
  -framework SwiftUI -framework AppKit -framework Network -framework Security \
  -framework CryptoKit -framework CoreMediaIO -framework CoreMedia -framework CoreVideo \
  -framework ImageIO -framework CoreGraphics -framework CoreAudio -framework AudioToolbox
cp Info.plist "$OUT/Contents/Info.plist"
codesign --force --sign "${TWINDESK_SIGNING_IDENTITY:--}" --timestamp=none "$OUT"
codesign --verify --deep --strict "$OUT"
echo "Built $OUT (not installed)."
